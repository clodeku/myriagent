using System.Text;
using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Hub;

/// <summary>掃描單一帳戶外部 CLI 歷史對話所需的快照資訊（不持有 UI 執行緒狀態）</summary>
internal sealed record ExternalAccountScanTarget(
    string AccountId,
    string AccountLabel,
    AgentProvider Provider,
    string ConfigDirectory);

/// <summary>
/// 唯讀共享掃描並載入本機外部 CLI（Claude Code、Codex、Antigravity、Grok）的歷史對話：
/// 一律以 <c>FileShare.ReadWrite | FileShare.Delete</c> 開檔、限制讀取位元組上限、絕不鎖檔或引用 SQLite
/// </summary>
internal static class ExternalConversationImporter
{
    private const int MaxHeadScanBytes = 1024 * 1024;
    private const int MaxTailScanBytes = 512 * 1024;
    private const int MaxFullImportBytes = 8 * 1024 * 1024;
    private const int MaxFullImportHeadBytes = 512 * 1024;
    private const int MaxFullImportTailBytes = MaxFullImportBytes - MaxFullImportHeadBytes;
    private const int MaxSidecarJsonBytes = 64 * 1024;
    private const int MaxCandidateFilesPerAccount = 120;

    private static readonly HashSet<string> GrokIgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "prompt_history.jsonl",
        "summary.json",
        "prompt_context.json",
        "metadata.json",
        "tool_calls.json",
        "subagents.json",
        "state.json",
    };

    /// <summary>掃描所有帳戶設定資料夾內尚未匯入 Myriagent 的外部 CLI 對話，依最後更新時間由新到舊排序</summary>
    public static List<ExternalConversationCandidate> Scan(
        IReadOnlyList<ExternalAccountScanTarget> accounts,
        IReadOnlyCollection<(AgentProvider Provider, string SessionId)> existingSessions,
        int maxResults = 100)
    {
        var existing = new HashSet<string>(
            existingSessions
                .Where(x => !string.IsNullOrWhiteSpace(x.SessionId))
                .Select(x => SessionKey(x.Provider, x.SessionId)),
            StringComparer.OrdinalIgnoreCase);

        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bySession = new Dictionary<string, ExternalConversationCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.ConfigDirectory))
                continue;
            var dirKey = $"{account.Provider}|{account.ConfigDirectory.TrimEnd('\\', '/')}";
            if (!seenDirs.Add(dirKey))
                continue;

            IEnumerable<ExternalConversationCandidate> found = account.Provider switch
            {
                AgentProvider.Claude => ScanClaudeAccount(account),
                AgentProvider.Codex => ScanCodexAccount(account),
                AgentProvider.Antigravity => ScanAntigravityAccount(account),
                AgentProvider.Grok => ScanGrokAccount(account),
                _ => Array.Empty<ExternalConversationCandidate>(),
            };

            foreach (var candidate in found)
            {
                var key = SessionKey(candidate.Provider, candidate.SessionId);
                if (existing.Contains(key))
                    continue;
                if (!bySession.TryGetValue(key, out var prev) || candidate.UpdatedAt > prev.UpdatedAt)
                    bySession[key] = candidate;
            }
        }

        return bySession.Values
            .OrderByDescending(c => c.UpdatedAt)
            .Take(Math.Max(1, maxResults))
            .ToList();
    }

    /// <summary>讀取單一候選對話的完整紀錄（供匯入成為正式 Myriagent 對話）</summary>
    public static ExternalConversationImportData? Load(ExternalConversationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(candidate.FilePath) || !File.Exists(candidate.FilePath))
            return null;

        DateTime fallbackTime;
        try
        {
            fallbackTime = File.GetLastWriteTime(candidate.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fallbackTime = candidate.UpdatedAt;
        }

        var lines = ReadLinesShared(candidate.FilePath, MaxFullImportHeadBytes, MaxFullImportTailBytes);
        if (lines.Count == 0)
            return null;

        switch (candidate.Provider)
        {
            case AgentProvider.Claude:
                return ExternalConversationRules.ParseClaudeJsonl(candidate.SessionId, lines, fallbackTime);
            case AgentProvider.Codex:
            {
                string? indexTitle = null;
                if (TryFindCodexConfigDirectory(candidate.FilePath) is { } codexRoot)
                {
                    var titles = ReadCodexSessionIndexTitles(codexRoot);
                    titles.TryGetValue(candidate.SessionId, out indexTitle);
                }
                return ExternalConversationRules.ParseCodexRolloutJsonl(candidate.SessionId, lines, fallbackTime, indexTitle);
            }
            case AgentProvider.Antigravity:
                return ExternalConversationRules.ParseAntigravityTranscriptJsonl(candidate.SessionId, lines, fallbackTime);
            case AgentProvider.Grok:
            {
                var sidecar = ReadGrokSidecar(candidate.FilePath, candidate.SessionId);
                return ExternalConversationRules.ParseGrokFile(
                    sidecar.FallbackSessionId,
                    lines,
                    fallbackTime,
                    sidecar.WorkingDirectory,
                    sidecar.Title,
                    sidecar.UpdatedAt);
            }
            default:
                return null;
        }
    }

    // ───────────────────────────── 各家資料夾掃描 ─────────────────────────────

    private static IEnumerable<ExternalConversationCandidate> ScanClaudeAccount(ExternalAccountScanTarget account)
    {
        var projectsDir = Path.Combine(account.ConfigDirectory, "projects");
        if (!Directory.Exists(projectsDir))
            return Array.Empty<ExternalConversationCandidate>();

        var files = new List<(string Path, DateTime LastWriteUtc)>();
        try
        {
            foreach (var projDir in Directory.EnumerateDirectories(projectsDir))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(projDir, "*.jsonl", SearchOption.TopDirectoryOnly))
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("agent-", StringComparison.OrdinalIgnoreCase))
                            continue;
                        files.Add((file, File.GetLastWriteTimeUtc(file)));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<ExternalConversationCandidate>();
        }

        var results = new List<ExternalConversationCandidate>();
        foreach (var (file, _) in files.OrderByDescending(f => f.LastWriteUtc).Take(MaxCandidateFilesPerAccount))
        {
            var fallbackId = Path.GetFileNameWithoutExtension(file);
            var fallbackTime = SafeLastWriteLocal(file);
            var lines = ReadLinesShared(file, MaxHeadScanBytes, MaxTailScanBytes);
            if (lines.Count == 0)
                continue;
            if (ExternalConversationRules.ParseClaudeJsonl(fallbackId, lines, fallbackTime) is { } parsed)
                results.Add(ToCandidate(account, file, parsed, fallbackTime));
        }
        return results;
    }

    private static IEnumerable<ExternalConversationCandidate> ScanCodexAccount(ExternalAccountScanTarget account)
    {
        var sessionsDir = Path.Combine(account.ConfigDirectory, "sessions");
        if (!Directory.Exists(sessionsDir))
            return Array.Empty<ExternalConversationCandidate>();

        var files = new List<(string Path, DateTime LastWriteUtc)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(sessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    files.Add((file, File.GetLastWriteTimeUtc(file)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<ExternalConversationCandidate>();
        }

        var indexTitles = ReadCodexSessionIndexTitles(account.ConfigDirectory);
        var results = new List<ExternalConversationCandidate>();
        foreach (var (file, _) in files.OrderByDescending(f => f.LastWriteUtc).Take(MaxCandidateFilesPerAccount))
        {
            var fallbackId = ExternalConversationRules.ExtractCodexThreadIdFromFileName(file);
            var fallbackTime = SafeLastWriteLocal(file);
            var lines = ReadLinesShared(file, MaxHeadScanBytes, MaxTailScanBytes);
            if (lines.Count == 0)
                continue;
            string? fallbackTitle = fallbackId != null && indexTitles.TryGetValue(fallbackId, out var t) ? t : null;
            if (ExternalConversationRules.ParseCodexRolloutJsonl(fallbackId, lines, fallbackTime, fallbackTitle) is { } parsed)
            {
                if (fallbackTitle == null && indexTitles.TryGetValue(parsed.SessionId, out var matchedTitle))
                    parsed = parsed with { Title = matchedTitle };
                results.Add(ToCandidate(account, file, parsed, fallbackTime));
            }
        }
        return results;
    }

    private static IEnumerable<ExternalConversationCandidate> ScanAntigravityAccount(ExternalAccountScanTarget account)
    {
        var brainRoot = Path.Combine(account.ConfigDirectory, "brain");
        if (!Directory.Exists(brainRoot))
            return Array.Empty<ExternalConversationCandidate>();

        var entries = new List<(string ConvId, string Path, DateTime LastWriteUtc)>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(brainRoot))
            {
                var convId = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(convId) || convId.StartsWith('.'))
                    continue;
                var logsDir = Path.Combine(dir, ".system_generated", "logs");
                var fullPath = Path.Combine(logsDir, "transcript_full.jsonl");
                var compactPath = Path.Combine(logsDir, "transcript.jsonl");
                string? chosen = File.Exists(fullPath) ? fullPath : File.Exists(compactPath) ? compactPath : null;
                if (chosen == null)
                    continue;
                try
                {
                    entries.Add((convId, chosen, File.GetLastWriteTimeUtc(chosen)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<ExternalConversationCandidate>();
        }

        var recent = entries.OrderByDescending(e => e.LastWriteUtc).Take(MaxCandidateFilesPerAccount).ToList();
        var loadedLines = new List<(string ConvId, string Path, List<string> Lines)>(recent.Count);
        var subagentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in recent)
        {
            var lines = ReadLinesShared(entry.Path, MaxHeadScanBytes, MaxTailScanBytes);
            if (lines.Count == 0)
                continue;
            foreach (var subId in ExternalConversationRules.CollectAntigravitySubagentIds(lines))
                subagentIds.Add(subId);
            loadedLines.Add((entry.ConvId, entry.Path, lines));
        }

        var results = new List<ExternalConversationCandidate>();
        foreach (var (convId, path, lines) in loadedLines)
        {
            if (subagentIds.Contains(convId))
                continue;
            var fallbackTime = SafeLastWriteLocal(path);
            if (ExternalConversationRules.ParseAntigravityTranscriptJsonl(convId, lines, fallbackTime) is { } parsed)
                results.Add(ToCandidate(account, path, parsed, fallbackTime));
        }
        return results;
    }

    private static IEnumerable<ExternalConversationCandidate> ScanGrokAccount(ExternalAccountScanTarget account)
    {
        var files = new List<(string Path, DateTime LastWriteUtc)>();
        foreach (var sub in new[] { "sessions", "conversations" })
        {
            var dir = Path.Combine(account.ConfigDirectory, sub);
            if (!Directory.Exists(dir))
                continue;
            try
            {
                foreach (var pattern in new[] { "*.jsonl", "*.json" })
                {
                    foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
                    {
                        var fileName = Path.GetFileName(file);
                        if (string.IsNullOrWhiteSpace(fileName) || GrokIgnoredFileNames.Contains(fileName))
                            continue;
                        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            var parent = Path.GetDirectoryName(file);
                            if (parent != null && File.Exists(Path.Combine(parent, "chat_history.jsonl")))
                                continue;
                        }
                        try
                        {
                            files.Add((file, File.GetLastWriteTimeUtc(file)));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        var results = new List<ExternalConversationCandidate>();
        foreach (var (file, _) in files.OrderByDescending(f => f.LastWriteUtc).Take(MaxCandidateFilesPerAccount))
        {
            var sidecar = ReadGrokSidecar(file, fallbackSessionId: null);
            var fallbackTime = SafeLastWriteLocal(file);
            var lines = ReadLinesShared(file, MaxHeadScanBytes, MaxTailScanBytes);
            if (lines.Count == 0)
                continue;
            if (ExternalConversationRules.ParseGrokFile(
                    sidecar.FallbackSessionId,
                    lines,
                    fallbackTime,
                    sidecar.WorkingDirectory,
                    sidecar.Title,
                    sidecar.UpdatedAt) is { } parsed)
            {
                results.Add(ToCandidate(account, file, parsed, fallbackTime));
            }
        }
        return results;
    }

    private static Dictionary<string, string> ReadCodexSessionIndexTitles(string configDir)
    {
        var indexPath = Path.Combine(configDir, "session_index.jsonl");
        if (!File.Exists(indexPath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = ReadLinesShared(indexPath, MaxHeadScanBytes, MaxTailScanBytes);
        return ExternalConversationRules.ParseCodexSessionIndexTitles(lines);
    }

    private static string? TryFindCodexConfigDirectory(string rolloutPath)
    {
        try
        {
            var dir = Directory.GetParent(rolloutPath);
            while (dir != null)
            {
                if (string.Equals(dir.Name, "sessions", StringComparison.OrdinalIgnoreCase))
                    return dir.Parent?.FullName;
                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return null;
    }

    private static (string FallbackSessionId, string? WorkingDirectory, string? Title, DateTime? UpdatedAt) ReadGrokSidecar(
        string filePath,
        string? fallbackSessionId)
    {
        var fileName = Path.GetFileName(filePath);
        if (string.Equals(fileName, "chat_history.jsonl", StringComparison.OrdinalIgnoreCase))
        {
            var sessionDir = Path.GetDirectoryName(filePath);
            var sid = !string.IsNullOrWhiteSpace(fallbackSessionId)
                ? fallbackSessionId
                : (sessionDir != null ? Path.GetFileName(sessionDir) : "chat_history");
            string? promptCtx = sessionDir != null ? ReadSmallTextShared(Path.Combine(sessionDir, "prompt_context.json")) : null;
            string? summary = sessionDir != null ? ReadSmallTextShared(Path.Combine(sessionDir, "summary.json")) : null;
            string? encodedProject = sessionDir != null ? Path.GetFileName(Path.GetDirectoryName(sessionDir)) : null;
            var meta = ExternalConversationRules.ParseGrokSidecarMetadata(promptCtx, summary, encodedProject);
            return (sid, meta.WorkingDirectory, meta.Title, meta.UpdatedAt);
        }

        var defaultSid = !string.IsNullOrWhiteSpace(fallbackSessionId)
            ? fallbackSessionId
            : Path.GetFileNameWithoutExtension(filePath);
        return (defaultSid, null, null, null);
    }

    private static string? ReadSmallTextShared(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= 0 || stream.Length > MaxSidecarJsonBytes)
                return null;
            var buf = ReadExact(stream, (int)stream.Length);
            return DecodeUtf8Safe(buf, trimIncompleteStart: false, trimIncompleteEnd: false).TrimStart('\uFEFF');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ───────────────────────────── 共用唯讀共享檔案讀取 ─────────────────────────────

    private static ExternalConversationCandidate ToCandidate(
        ExternalAccountScanTarget account,
        string filePath,
        ExternalConversationImportData parsed,
        DateTime fileWriteTime)
    {
        var msgCount = parsed.Items.Count(i => i.Kind is AgentItemKind.User or AgentItemKind.Assistant);
        var updated = parsed.UpdatedAt > fileWriteTime ? parsed.UpdatedAt : fileWriteTime;
        var sourceKey = ExternalConversationRules.ComputeSourceKey(account.Provider, account.AccountId, filePath);
        return new ExternalConversationCandidate(
            sourceKey,
            account.Provider,
            account.AccountId,
            account.AccountLabel,
            parsed.SessionId,
            parsed.Title,
            parsed.WorkingDirectory,
            updated,
            Math.Max(1, msgCount),
            filePath);
    }

    internal static string SessionKey(AgentProvider provider, string sessionId)
        => $"{provider}|{sessionId.Trim()}";

    private static DateTime SafeLastWriteLocal(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.Now;
        }
    }

    /// <summary>
    /// 以唯讀共享開啟檔案：檔案不大於 <c>headBytes + tailBytes</c> 時一次讀完；超大檔案只讀開頭完整行與結尾完整行，不鎖檔也不耗盡記憶體
    /// </summary>
    internal static List<string> ReadLinesShared(string path, int headBytes, int tailBytes)
    {
        var result = new List<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            if (length <= 0)
                return result;

            long maxCombined = (long)headBytes + tailBytes;
            if (length <= maxCombined)
            {
                var buf = ReadExact(stream, (int)length);
                AppendSplitLines(result, DecodeUtf8Safe(buf, trimIncompleteStart: false, trimIncompleteEnd: false).TrimStart('\uFEFF'), skipFirstPartial: false, skipLastPartial: false);
                return result;
            }

            if (headBytes > 0)
            {
                int actualHead = (int)Math.Min(headBytes, length);
                var headBuf = ReadExact(stream, actualHead);
                AppendSplitLines(result, DecodeUtf8Safe(headBuf, trimIncompleteStart: false, trimIncompleteEnd: true).TrimStart('\uFEFF'), skipFirstPartial: false, skipLastPartial: true);
            }

            if (tailBytes > 0 && length > headBytes)
            {
                int actualTail = (int)Math.Min(tailBytes, length - headBytes);
                stream.Seek(length - actualTail, SeekOrigin.Begin);
                var tailBuf = ReadExact(stream, actualTail);
                AppendSplitLines(result, DecodeUtf8Safe(tailBuf, trimIncompleteStart: true, trimIncompleteEnd: false), skipFirstPartial: true, skipLastPartial: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"讀取外部 CLI 對話檔失敗（{path}）：{ex.Message}");
        }
        return result;
    }

    internal static string DecodeUtf8Safe(ReadOnlySpan<byte> bytes, bool trimIncompleteStart, bool trimIncompleteEnd)
    {
        int start = 0;
        if (trimIncompleteStart)
        {
            while (start < bytes.Length && start < 4 && (bytes[start] & 0xC0) == 0x80)
                start++;
        }

        int end = bytes.Length;
        if (trimIncompleteEnd && end > start)
        {
            int checkStart = Math.Max(start, end - 4);
            for (int i = end - 1; i >= checkStart; i--)
            {
                byte b = bytes[i];
                if ((b & 0x80) == 0)
                    break;
                if ((b & 0xC0) == 0x80)
                    continue;
                int expectedLen = (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : 4;
                if (end - i < expectedLen)
                    end = i;
                break;
            }
        }

        return start >= end ? string.Empty : Encoding.UTF8.GetString(bytes[start..end]);
    }

    private static byte[] ReadExact(Stream stream, int count)
    {
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = stream.Read(buffer, offset, count - offset);
            if (n <= 0)
                break;
            offset += n;
        }
        return offset == count ? buffer : buffer[..offset];
    }

    private static void AppendSplitLines(List<string> target, string text, bool skipFirstPartial, bool skipLastPartial)
    {
        var parts = text.Split('\n');
        int start = skipFirstPartial && parts.Length > 1 ? 1 : 0;
        int end = skipLastPartial && parts.Length > 1 ? parts.Length - 1 : parts.Length;
        for (int i = start; i < end; i++)
        {
            var trimmed = parts[i].TrimEnd('\r');
            if (trimmed.Length > 0)
                target.Add(trimmed);
        }
    }
}
