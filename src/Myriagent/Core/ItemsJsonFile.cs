using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Myriagent.Agents;
using Newtonsoft.Json;

namespace Myriagent.Core;

/// <summary>
/// 一個對話上次寫進 items.json 的樣子（<see cref="ItemsJsonFile"/>）：寫完的檔案大小與修改時間，以及每個快照項目（以物件本身為鍵）在檔案裡的位置、長度與雜湊。
/// 背景存檔的執行緒與 UI 執行緒的同步存檔都會用：一律在 <see cref="Gate"/> 裡讀寫
/// </summary>
internal sealed class ItemsJsonLayout
{
    internal readonly object Gate = new();

    internal string? Path;
    internal long Length;
    internal DateTime LastWriteUtc;

    /// <summary>快照項目 → 在檔案裡的位置；null＝沒有可以沿用的（還沒寫過、上次寫失敗）</summary>
    internal Dictionary<AgentChatItem, Segment>? Segments;

    /// <summary>測試用：上一次寫入沿用了幾項、序列化了幾項、是不是因為核對不過而整份重寫</summary>
    internal (int Reused, int Serialized, bool Fallback) LastWrite;

    internal readonly record struct Segment(long Offset, int Length, ulong Hash);
}

/// <summary>
/// 增量寫 items.json：輸出與 <see cref="JsonFile.Write{T}"/> 整份序列化逐位元組相同（JSON 陣列、縮排），但上次寫過、物件沒換的快照項目
/// （ItemsSaveCache 沿用的快照；快照建立後就不再改）不再序列化，直接從上一份檔案照位置複製位元組，只序列化有變動的。
/// 2026-10-25 實測：執行中的對話有 1 萬多個項目、items.json 53 MB 時，整份序列化一次 105～260 ms（每 30 秒的定期存檔在背景、一輪結束的存檔在 UI 執行緒）。
/// 沿用之前先核對上一份檔案的大小與修改時間，複製時逐段核對雜湊：任何一項對不上（被別的程式改過、換掉）就捨棄這次的暫存檔、整份重新序列化。
/// 寫法同 JsonFile.Write：先寫暫存檔、落盤後再取代
/// </summary>
internal static class ItemsJsonFile
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>
    /// 縮排陣列的框：Newtonsoft 寫成「[」＋每項之前的換行與縮排（項目之間另加「,」）＋項目＋最後的換行＋「]」，空陣列是「[]」。
    /// 由 Newtonsoft 自己寫一次兩個 null 的陣列推出來（不寫死換行字元）；格式和預期的不同時 <see cref="Framing"/> 是 null，一律整份序列化
    /// </summary>
    private static readonly (byte[] Before, byte[] End)? Framing = DetectFraming();

    /// <summary>
    /// 寫 <paramref name="items"/>（快照：寫的期間不會被改）。<paramref name="layout"/> 記著上次寫的樣子，寫完更新成這一次的；
    /// 失敗（丟例外）時清掉，下一次整份序列化
    /// </summary>
    public static void Write(string path, IReadOnlyList<AgentChatItem?> items, ItemsJsonLayout layout, bool createDirectory = true)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(layout);
        if (Framing is not { } framing)
        {
            JsonFile.Write(path, items, createDirectory);
            return;
        }
        lock (layout.Gate)
        {
            try
            {
                WriteLocked(path, items, layout, framing, createDirectory);
            }
            catch
            {
                layout.Segments = null;
                throw;
            }
        }
    }

    private static void WriteLocked(string path, IReadOnlyList<AgentChatItem?> items, ItemsJsonLayout layout, (byte[] Before, byte[] End) framing, bool createDirectory)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (createDirectory && !string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        var segments = new Dictionary<AgentChatItem, ItemsJsonLayout.Segment>(items.Count, ReferenceEqualityComparer.Instance);
        var previous = string.Equals(layout.Path, path, StringComparison.OrdinalIgnoreCase) ? layout.Segments : null;
        var old = previous is null ? null : OpenPrevious(path, layout);
        try
        {
            if (!TryWrite(temp, items, framing, old, previous, segments, out var reused))
            {
                // 上一份檔案的內容和記下的不同：這次整份序列化
                Log.Warn($"{path} 和上次寫的內容對不上，整份重新寫");
                old!.Dispose();
                old = null;
                segments.Clear();
                TryWrite(temp, items, framing, null, null, segments, out _);
                layout.LastWrite = (0, items.Count, true);
            }
            else
            {
                layout.LastWrite = (reused, items.Count - reused, false);
            }
        }
        finally
        {
            old?.Dispose();
        }
        JsonFile.ReplaceWithRetry(temp, path);
        var info = new FileInfo(path);
        layout.Path = path;
        layout.Length = info.Length;
        layout.LastWriteUtc = info.LastWriteTimeUtc;
        layout.Segments = segments;
    }

    /// <summary>開上一份檔案來複製：大小或修改時間和上次寫完的不同（被別的程式寫過）、開不了，就回 null（整份序列化）</summary>
    private static FileStream? OpenPrevious(string path, ItemsJsonLayout layout)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            if (stream.Length == layout.Length && File.GetLastWriteTimeUtc(stream.SafeFileHandle) == layout.LastWriteUtc)
                return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 不在了、被占用：整份序列化
        }
        stream?.Dispose();
        return null;
    }

    /// <summary>寫暫存檔；沿用的項目在上一份檔案裡的雜湊對不上時回 false（暫存檔作廢，呼叫端整份重寫）</summary>
    private static bool TryWrite(
        string temp,
        IReadOnlyList<AgentChatItem?> items,
        (byte[] Before, byte[] End) framing,
        FileStream? old,
        Dictionary<AgentChatItem, ItemsJsonLayout.Segment>? previous,
        Dictionary<AgentChatItem, ItemsJsonLayout.Segment> segments,
        out int reused)
    {
        reused = 0;
        byte[]? buffer = null;
        using var chunks = new ChunkSerializer();
        try
        {
            using var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.None);
            stream.WriteByte((byte)'[');
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                    stream.WriteByte((byte)',');
                stream.Write(framing.Before);
                var item = items[i];
                var offset = stream.Position;
                if (item is not null && old is not null && previous!.TryGetValue(item, out var seg))
                {
                    if (buffer is null || buffer.Length < seg.Length)
                    {
                        if (buffer is not null)
                            ArrayPool<byte>.Shared.Return(buffer);
                        buffer = ArrayPool<byte>.Shared.Rent(Math.Max(seg.Length, 1 << 16));
                    }
                    var bytes = buffer.AsSpan(0, seg.Length);
                    if (old.Position != seg.Offset)
                        old.Position = seg.Offset;
                    old.ReadExactly(bytes);
                    if (Hash(bytes) != seg.Hash)
                        return false;
                    stream.Write(bytes);
                    segments.TryAdd(item, seg with { Offset = offset });
                    reused++;
                }
                else
                {
                    var bytes = chunks.Serialize(item);
                    stream.Write(bytes);
                    if (item is not null)
                        segments.TryAdd(item, new ItemsJsonLayout.Segment(offset, bytes.Length, Hash(bytes)));
                }
            }
            if (items.Count > 0)
                stream.Write(framing.End);
            stream.WriteByte((byte)']');
            stream.Flush(flushToDisk: true);   // 先落盤再取代：斷電時不會出現「改名成功但內容是空的」
            return true;
        }
        catch (EndOfStreamException)
        {
            // 上一份檔案比記下的短（不該發生：大小核對過）：同雜湊對不上
            return false;
        }
        finally
        {
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 一項一項序列化：同一支 JsonTextWriter 一直停在「陣列裡」，每項寫完取出它的位元組（去掉前面的「,」與換行縮排）——巢狀內容的縮排和整份序列化時相同
    /// </summary>
    private sealed class ChunkSerializer : IDisposable
    {
        private readonly JsonSerializer serializer = JsonSerializer.CreateDefault(JsonFile.Settings);
        private readonly MemoryStream buffer = new();
        private readonly StreamWriter text;
        private readonly JsonTextWriter json;
        private bool first = true;

        public ChunkSerializer()
        {
            text = new StreamWriter(buffer, Utf8, 1 << 16, leaveOpen: true);
            json = new JsonTextWriter(text) { Formatting = serializer.Formatting, CloseOutput = false, AutoCompleteOnClose = false };
            json.WriteStartArray();
            json.Flush();
            buffer.SetLength(0);
        }

        /// <summary>這一項在陣列裡的位元組（下一次呼叫之前有效）</summary>
        public ReadOnlySpan<byte> Serialize(AgentChatItem? item)
        {
            buffer.SetLength(0);
            serializer.Serialize(json, item);
            json.Flush();
            var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
            if (!first)
                bytes = bytes[1..];
            first = false;
            return bytes[Framing!.Value.Before.Length..];
        }

        public void Dispose()
        {
            ((IDisposable)json).Dispose();
            text.Dispose();
            buffer.Dispose();
        }
    }

    /// <summary>以 Newtonsoft 寫兩個 null 的陣列，推出「每項之前」與「最後」的位元組；和預期的形狀不同回 null</summary>
    private static (byte[] Before, byte[] End)? DetectFraming()
    {
        try
        {
            using var stream = new MemoryStream();
            JsonFile.Serialize(stream, new object?[] { null, null });
            var s = Utf8.GetString(stream.ToArray());
            // 預期：[ B null , B null E ]
            var first = s.IndexOf("null", StringComparison.Ordinal);
            var second = s.IndexOf("null", first + 4, StringComparison.Ordinal);
            if (first < 1 || second < 0 || s[0] != '[' || s[^1] != ']')
                return null;
            var before = s[1..first];
            if (s[(first + 4)..second] != "," + before)
                return null;
            var end = s[(second + 4)..^1];
            using var empty = new MemoryStream();
            JsonFile.Serialize(empty, Array.Empty<object>());
            if (Utf8.GetString(empty.ToArray()) != "[]" || before.Length == 0 || before.Trim().Length != 0 || end.Trim().Length != 0)
                return null;
            return (Utf8.GetBytes(before), Utf8.GetBytes(end));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>核對用的 64 位元雜湊（不是密碼學用途；只防上一份檔案在兩次寫入之間被改過）：一次吃 8 個位元組，53 MB 約 10 ms</summary>
    internal static ulong Hash(ReadOnlySpan<byte> data)
    {
        const ulong P1 = 0x9E3779B185EBCA87UL;
        const ulong P2 = 0xC2B2AE3D27D4EB4FUL;
        var h = ((ulong)data.Length + 1) * P1;
        var words = MemoryMarshal.Cast<byte, ulong>(data);
        foreach (var w in words)
            h = BitOperations.RotateLeft(h ^ (w * P2), 31) * P1;
        for (var i = words.Length * 8; i < data.Length; i++)
            h = BitOperations.RotateLeft(h ^ (data[i] * P2), 31) * P1;
        h ^= h >> 33;
        h *= P2;
        h ^= h >> 29;
        return h;
    }
}
