using System.Text;
using Myriagent.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// Claude 子代理的 token（2026-10-25 實測，tools/probes/claude_subagent_usage_probe.py；docs/protocol-notes.md「Claude 子代理的 token」）：
/// 子代理沒有串流事件，--forward-subagent-text 轉送的 assistant 訊息帶 usage，但 output_tokens 是開頭的值（16，實際 138）；
/// 逐字稿 <c>&lt;session&gt;\subagents\agent-*.jsonl</c> 那幾行才是這次呼叫的最終用量（帶 stop_reason）。
/// 所以子代理的用量從逐字稿讀：只讀新增的部分（記每個檔讀到的位置），以 message.id 去重、逐欄取最大值，回傳比上次多的部分與它的模型。
/// 建立時資料夾裡已有的內容（之前的程序留下的）當成已讀。執行緒安全（stdout 讀取執行緒與程序結束的回呼都會叫）
/// </summary>
internal sealed class ClaudeSubagentUsageTail
{
    /// <summary>一次最多讀這麼多新增的位元組（超過的下次再讀）</summary>
    private const int MaxReadBytes = 32 << 20;

    private static readonly byte[] UsageMarker = Encoding.UTF8.GetBytes("\"usage\"");

    private readonly object gate = new();
    private readonly Dictionary<string, long> offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long[]> seen = new(StringComparer.Ordinal);

    public ClaudeSubagentUsageTail(string directory)
    {
        Directory = directory;
        foreach (var (file, length) in ListFiles())
            offsets[file] = length;
    }

    /// <summary>子代理逐字稿的資料夾（主對話逐字稿旁邊的 &lt;session&gt;\subagents）</summary>
    public string Directory { get; }

    /// <summary>主對話逐字稿 → 子代理逐字稿的資料夾</summary>
    public static string DirectoryFor(string transcriptPath)
        => Path.Combine(Path.GetDirectoryName(transcriptPath) ?? string.Empty, Path.GetFileNameWithoutExtension(transcriptPath), "subagents");

    /// <summary>讀新增的部分：每一筆是一次呼叫比上次多的用量（四個欄位：input、cache_creation、cache_read、output）與它的模型</summary>
    public List<(string? Model, long[] Fields)> Poll()
    {
        var result = new List<(string? Model, long[] Fields)>();
        lock (gate)
        {
            foreach (var (file, length) in ListFiles())
            {
                offsets.TryGetValue(file, out var offset);
                if (length < offset)
                    offset = 0;   // 檔案被改寫（不該發生）：從頭讀，同一則訊息以 id 去重不會重複記
                if (length == offset)
                    continue;
                offsets[file] = offset + ReadLines(file, offset, length, result);
            }
        }
        return result;
    }

    private List<(string File, long Length)> ListFiles()
    {
        var files = new List<(string, long)>();
        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return files;
            foreach (var info in new DirectoryInfo(Directory).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
                files.Add((info.FullName, info.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"列出 Claude 子代理逐字稿失敗：{ex.Message}");
        }
        return files;
    }

    /// <summary>讀 [offset, length) 裡完整的行（最後沒有換行的半行留到下次），回傳讀掉的位元組數</summary>
    private long ReadLines(string file, long offset, long length, List<(string? Model, long[] Fields)> result)
    {
        byte[] buffer;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(offset, SeekOrigin.Begin);
            var want = (int)Math.Min(length - offset, MaxReadBytes);
            buffer = new byte[want];
            var read = 0;
            while (read < want)
            {
                var n = stream.Read(buffer, read, want - read);
                if (n <= 0)
                    break;
                read += n;
            }
            if (read < want)
                Array.Resize(ref buffer, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"讀 Claude 子代理逐字稿失敗：{ex.Message}");
            return 0;
        }

        var span = buffer.AsSpan();
        var end = span.LastIndexOf((byte)'\n');
        if (end < 0)
        {
            // 一整段都沒有換行：一行比上限還長（巨大的工具結果）就整段跳過，否則等它寫完
            return buffer.Length >= MaxReadBytes ? buffer.Length : 0;
        }
        var consumed = span[..(end + 1)];
        while (consumed.Length > 0)
        {
            var nl = consumed.IndexOf((byte)'\n');
            var line = nl < 0 ? consumed : consumed[..nl];
            consumed = nl < 0 ? Span<byte>.Empty : consumed[(nl + 1)..];
            if (line.IndexOf(UsageMarker) >= 0 && ParseLine(file, line) is { } usage)
                result.Add(usage);
        }
        return end + 1;
    }

    /// <summary>一行 assistant 訊息的用量 → 比這則訊息上次記的多的部分；不是、或沒有增加回 null</summary>
    private (string? Model, long[] Fields)? ParseLine(string file, ReadOnlySpan<byte> line)
    {
        JObject o;
        try
        {
            o = JObject.Parse(Encoding.UTF8.GetString(line));
        }
        catch (JsonException)
        {
            return null;
        }
        if (o.Value<string>("type") != "assistant" || o["message"] is not JObject message || message["usage"] is not JObject usage)
            return null;
        var id = message["id"] is JValue { Type: JTokenType.String } idValue ? (string?)idValue.Value : null;
        var key = file + "|" + (id ?? o["uuid"]?.ToString() ?? Guid.NewGuid().ToString("N"));
        var after = ClaudeStreamRules.MergeMessageUsage(null, usage);
        seen.TryGetValue(key, out var before);
        var merged = before == null ? after : before.Zip(after, Math.Max).ToArray();
        seen[key] = merged;
        var delta = ClaudeStreamRules.FieldIncrement(before, merged);
        if (delta.All(v => v <= 0))
            return null;
        var model = message["model"] is JValue { Type: JTokenType.String } m ? (string?)m.Value : null;
        // CLI 合成的訊息（API 錯誤等）model 是 "<synthetic>"、用量 0：上面已略過；有用量就照記
        return (model, delta);
    }
}
