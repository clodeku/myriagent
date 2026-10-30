using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;

namespace Myriagent.Schedules;

/// <summary>要掃的一個帳戶設定資料夾（同匯入外部對話的 ExternalAccountScanTarget）</summary>
internal sealed record TranscriptRoot(AgentProvider Provider, string? AccountId, string ConfigDirectory);

/// <summary>一個紀錄檔掃到哪（大小與修改時間沒變就不再讀；變大了從 Offset 接著讀）</summary>
internal sealed class TranscriptFileState
{
    public long Length { get; set; }

    public DateTime WriteTime { get; set; }

    /// <summary>讀到的位置（最後一個完整的行之後）</summary>
    public long Offset { get; set; }
}

/// <summary>&lt;資料根目錄&gt;\schedules\scan-index.json：每個檔掃到哪、找到的碰到排程的指令</summary>
internal sealed class TranscriptScanIndex
{
    public Dictionary<string, TranscriptFileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<TranscriptHit> Hits { get; set; } = new();
}

/// <summary>
/// 排程的回溯（S2）：增量掃各帳戶的 CLI 對話紀錄（Claude 的 projects、Codex 的 sessions、Antigravity 的 brain、Grok 的 sessions／conversations），
/// 找出建立、修改、刪除排程的指令（TranscriptOriginRules）。只讀、FileShare.ReadWrite|Delete；先以位元組找關鍵字，有找到的區塊才逐行解析。
/// 第一次要讀完全部（這台 2026-10-26 約 Claude 8 GB、Codex 1.1 GB），在背景低優先的執行緒；之後只讀新增的部分。檔案不見了就從索引拿掉
/// </summary>
internal static class TranscriptOriginScanner
{
    private const int BlockSize = 4 * 1024 * 1024;

    /// <summary>一次最多讀這麼多（太多就下次接著讀；位置記在索引）</summary>
    public const long MaxBytesPerRun = 4L * 1024 * 1024 * 1024;

    private static readonly byte[][] Needles =
    {
        "chtasks"u8.ToArray(), "CHTASKS"u8.ToArray(), "cheduledTask"u8.ToArray(), "cheduledtask"u8.ToArray(), "CHEDULEDTASK"u8.ToArray(),
        "chedule.Service"u8.ToArray(), "chedule.service"u8.ToArray(),
    };

    public static TranscriptScanIndex Scan(IReadOnlyList<TranscriptRoot> roots, TranscriptScanIndex? previous, CancellationToken ct)
    {
        var index = previous ?? new TranscriptScanIndex();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long budget = MaxBytesPerRun;
        foreach (var root in roots.DistinctBy(r => (r.Provider, r.ConfigDirectory.TrimEnd('\\', '/').ToLowerInvariant())))
        {
            foreach (var (file, session) in Files(root))
            {
                ct.ThrowIfCancellationRequested();
                seen.Add(file);
                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (!info.Exists)
                        continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                index.Files.TryGetValue(file, out var state);
                if (state != null && state.Length == info.Length && state.WriteTime == info.LastWriteTimeUtc)
                    continue;
                if (budget <= 0)
                    continue;
                if (state == null || info.Length < state.Offset)
                {
                    // 新檔或被截短（重寫）：從頭讀，舊的紀錄拿掉
                    index.Hits.RemoveAll(h => h.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase));
                    state = new TranscriptFileState();
                }
                var (offset, read) = ReadFrom(file, state.Offset, info.LastWriteTime, root, session, index.Hits, budget, ct);
                budget -= read;
                state.Offset = offset;
                if (offset >= info.Length)
                {
                    state.Length = info.Length;
                    state.WriteTime = info.LastWriteTimeUtc;
                }
                index.Files[file] = state;
            }
        }
        // 不見了的檔（被刪掉、帳戶移除）：從索引拿掉，找到的紀錄留著（排程可能還在）
        foreach (var gone in index.Files.Keys.Where(f => !seen.Contains(f)).ToList())
            index.Files.Remove(gone);
        return index;
    }

    /// <summary>從 <paramref name="offset"/> 讀到檔尾（最後一個不完整的行留到下次）；回新的位置與讀了多少位元組</summary>
    private static (long Offset, long Read) ReadFrom(string file, long offset, DateTime fallbackTime, TranscriptRoot root, string session,
        List<TranscriptHit> hits, long budget, CancellationToken ct)
    {
        long read = 0;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[BlockSize];
            int carry = 0;
            bool wholeFileJson = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n = stream.Read(buffer, carry, buffer.Length - carry);
                if (n <= 0)
                {
                    // 整份是一個 JSON 的檔（Grok 的 .json）沒有換行：剩下的當一行
                    if (wholeFileJson && carry > 0)
                    {
                        Process(buffer.AsSpan(0, carry), fallbackTime, root, session, file, hits);
                        offset += carry;
                    }
                    break;
                }
                read += n;
                int total = carry + n;
                int lastNewline = buffer.AsSpan(0, total).LastIndexOf((byte)'\n');
                if (lastNewline < 0)
                {
                    if (total == buffer.Length)
                    {
                        // 一行超過區塊大小：整段當一行處理再往下
                        Process(buffer.AsSpan(0, total), fallbackTime, root, session, file, hits);
                        offset += total;
                        carry = 0;
                    }
                    else
                    {
                        carry = total;
                    }
                    continue;
                }
                var complete = buffer.AsSpan(0, lastNewline + 1);
                if (ContainsNeedle(complete))
                {
                    foreach (var line in Lines(complete))
                        Process(line, fallbackTime, root, session, file, hits);
                }
                offset += lastNewline + 1;
                carry = total - (lastNewline + 1);
                buffer.AsSpan(lastNewline + 1, carry).CopyTo(buffer);
                if (read >= budget)
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"排程回溯：讀不到 {file}：{ex.Message}");
        }
        return (offset, read);
    }

    private static bool ContainsNeedle(ReadOnlySpan<byte> span)
    {
        foreach (var needle in Needles)
        {
            if (span.IndexOf(needle) >= 0)
                return true;
        }
        return false;
    }

    private static IEnumerable<byte[]> Lines(ReadOnlySpan<byte> block)
    {
        var list = new List<byte[]>();
        int start = 0;
        while (start < block.Length)
        {
            int nl = block[start..].IndexOf((byte)'\n');
            int end = nl < 0 ? block.Length : start + nl;
            var line = block[start..end];
            if (ContainsNeedle(line))
                list.Add(line.ToArray());
            start = end + 1;
        }
        return list;
    }

    private static void Process(ReadOnlySpan<byte> bytes, DateTime fallbackTime, TranscriptRoot root, string session, string file, List<TranscriptHit> hits)
    {
        if (!ContainsNeedle(bytes))
            return;
        var line = Encoding.UTF8.GetString(bytes).TrimEnd('\r');
        foreach (var (verb, name) in TranscriptOriginRules.HitsInLine(line))
        {
            hits.Add(new TranscriptHit
            {
                Provider = root.Provider.ToString(),
                AccountId = root.AccountId,
                SessionId = session,
                FilePath = file,
                At = TranscriptOriginRules.TimeOf(line) ?? fallbackTime,
                Verb = TranscriptOriginRules.VerbName(verb),
                Name = name,
                Cwd = TranscriptOriginRules.CwdOf(line),
            });
        }
    }

    /// <summary>這個帳戶資料夾的紀錄檔與工作階段 id</summary>
    internal static IEnumerable<(string File, string Session)> Files(TranscriptRoot root)
    {
        var dir = root.ConfigDirectory;
        IEnumerable<(string, string)> Safe(Func<IEnumerable<(string, string)>> f)
        {
            try
            {
                return f().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<(string, string)>();
            }
        }
        switch (root.Provider)
        {
            case AgentProvider.Claude:
            {
                var projects = Path.Combine(dir, "projects");
                return Directory.Exists(projects)
                    ? Safe(() => Directory.EnumerateFiles(projects, "*.jsonl", SearchOption.AllDirectories).Select(f => (f, Path.GetFileNameWithoutExtension(f))))
                    : Array.Empty<(string, string)>();
            }
            case AgentProvider.Codex:
            {
                var sessions = Path.Combine(dir, "sessions");
                return Directory.Exists(sessions)
                    ? Safe(() => Directory.EnumerateFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories)
                        .Select(f => (f, ExternalConversationRules.ExtractCodexThreadIdFromFileName(Path.GetFileName(f)) ?? Path.GetFileNameWithoutExtension(f))))
                    : Array.Empty<(string, string)>();
            }
            case AgentProvider.Antigravity:
            {
                var brain = Path.Combine(dir, "brain");
                return Directory.Exists(brain)
                    ? Safe(() => Directory.EnumerateDirectories(brain).Where(d => !Path.GetFileName(d).StartsWith('.'))
                        .SelectMany(d =>
                        {
                            var logs = Path.Combine(d, ".system_generated", "logs");
                            return Directory.Exists(logs)
                                ? Directory.EnumerateFiles(logs, "transcript*.jsonl").Select(f => (f, Path.GetFileName(d)))
                                : Enumerable.Empty<(string, string)>();
                        }))
                    : Array.Empty<(string, string)>();
            }
            case AgentProvider.Grok:
            {
                var list = new List<(string, string)>();
                foreach (var sub in new[] { "sessions", "conversations" })
                {
                    var d = Path.Combine(dir, sub);
                    if (!Directory.Exists(d))
                        continue;
                    list.AddRange(Safe(() => Directory.EnumerateFiles(d, "*.jsonl", SearchOption.AllDirectories)
                        .Concat(Directory.EnumerateFiles(d, "*.json", SearchOption.AllDirectories).Where(f => new FileInfo(f).Length < 16 * 1024 * 1024))
                        .Select(f => (f, Path.GetFileName(Path.GetDirectoryName(f)) is { Length: > 0 } parent && parent != sub ? parent : Path.GetFileNameWithoutExtension(f)))));
                }
                return list;
            }
            default:
                return Array.Empty<(string, string)>();
        }
    }
}
