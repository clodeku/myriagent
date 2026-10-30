using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Myriagent.Sync;

/// <summary>
/// 日誌的種類（第 5 階段計畫 5.6）：<see cref="ClaudeMain"/>、<see cref="ClaudeSub"/>、<see cref="CodexRollout"/> 是只附加的 jsonl（每段要到最後一個 LF，
/// <see cref="LogIdRules.RequiresLf"/>）；<see cref="ClaudeFile"/>、<see cref="Attachment"/>、<see cref="Workspace"/> 是整檔（內容變了就以新世代整檔重傳）
/// </summary>
internal static class LogKinds
{
    /// <summary>Claude 的主逐字稿 <c>&lt;sid&gt;.jsonl</c></summary>
    public const string ClaudeMain = "claude-main";

    /// <summary>Claude 子代理的逐字稿 <c>&lt;sid&gt;\subagents\agent-&lt;id&gt;.jsonl</c></summary>
    public const string ClaudeSub = "claude-sub";

    /// <summary>Claude 的其他伴隨檔：<c>&lt;sid&gt;\tool-results\*</c>（寫一次）、<c>&lt;sid&gt;\subagents\*.meta.json</c>（會被改寫，E-1）</summary>
    public const string ClaudeFile = "claude-file";

    /// <summary>Codex 的 rollout</summary>
    public const string CodexRollout = "codex-rollout";

    /// <summary>對話的附件（<c>attachments\</c>，含 <c>.model\</c>）</summary>
    public const string Attachment = "attachment";

    /// <summary>對話預設工作區的檔（只在線上交接時快照，D10）</summary>
    public const string Workspace = "workspace";

    public static IReadOnlyList<string> All { get; } = [ClaudeMain, ClaudeSub, ClaudeFile, CodexRollout, Attachment, Workspace];

    public static bool IsKnown(string? kind) => kind is ClaudeMain or ClaudeSub or ClaudeFile or CodexRollout or Attachment or Workspace;

    /// <summary>CLI 紀錄（逐字稿與它的伴隨檔）：<c>transcript</c> 是 missing／held／unportable／damaged 時接手端不下載、不放回</summary>
    public static bool IsCliRecord(string? kind) => kind is ClaudeMain or ClaudeSub or ClaudeFile or CodexRollout;
}

/// <summary>一個 logId 的組成（<see cref="LogIdRules.TryParse"/>）</summary>
/// <param name="Kind">由 logId 決定的種類（<see cref="LogKinds"/>；上傳時的 <c>X-Myriagent-Kind</c> 必須等於它）</param>
/// <param name="Owner">Claude 的 session id、Codex 的 thread id，或附件與工作區的對話 id</param>
/// <param name="RelativePath">伴隨檔、附件、工作區檔的相對路徑（<see cref="LogIdRules.NormalizeRelativePath"/> 過的、正斜線）；主檔與 rollout 是 null</param>
internal sealed record LogIdParts(string Kind, string Owner, string? RelativePath);

/// <summary>
/// 日誌的識別（第 5 階段計畫 5.6；伺服器與桌面共用）：
/// <list type="bullet">
/// <item><c>cl:{sid}</c>（Claude 主檔）、<c>cl:{sid}/subagents/{名稱}</c>、<c>cl:{sid}/tool-results/{名稱}</c>（伴隨檔，一律以 <c>&lt;sid&gt;\</c> 之下的相對路徑命名，
/// 不管它在本機哪一個 <c>projects\*</c> 資料夾，E-1）、<c>cx:{threadId}</c>（Codex rollout）、<c>fa:{conversationId}/{相對路徑}</c>（附件）、
/// <c>fw:{conversationId}/{相對路徑}</c>（預設工作區）。</item>
/// <item>網址用 <see cref="LogKey"/>＝logId 的 UTF-8 的 SHA-256 小寫十六進位；伺服器以它當檔名與主鍵，路徑本身不碰檔案系統。</item>
/// <item>相對路徑一律經 <see cref="NormalizeRelativePath"/>：接手端把它接在目標資料夾之下，不能逃出去（zip slip），也不能是 Windows 不認的名稱。</item>
/// </list>
/// </summary>
internal static class LogIdRules
{
    public const string ClaudePrefix = "cl:";

    public const string CodexPrefix = "cx:";

    public const string AttachmentPrefix = "fa:";

    public const string WorkspacePrefix = "fw:";

    /// <summary>session id、thread id 的長度上限</summary>
    public const int MaxSessionIdLength = 64;

    /// <summary>相對路徑的長度上限（字元；同對話事件的 <c>MaxPathLength</c>）</summary>
    public const int MaxRelativePathLength = 1024;

    /// <summary>logId 的長度上限（前綴、id、斜線與相對路徑）</summary>
    public const int MaxLogIdLength = 3 + MaxSessionIdLength + 1 + MaxRelativePathLength;

    private const string SessionIdChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    private const string LowerHex = "0123456789abcdef";

    /// <summary>Windows 不能用在檔名裡的字元（另有控制字元與 <c>:</c>、斜線，另外檢查）</summary>
    private const string ForbiddenNameChars = "<>\"|?*";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Claude 的主逐字稿</summary>
    public static string ClaudeMain(string sessionId) => ClaudePrefix + RequireSessionId(sessionId);

    /// <summary>Claude 的伴隨檔：<paramref name="relativePath"/> 是 <c>&lt;sid&gt;\</c> 之下的相對路徑（例如 <c>subagents\agent-1.jsonl</c>）</summary>
    public static string ClaudeCompanion(string sessionId, string relativePath)
        => ClaudePrefix + RequireSessionId(sessionId) + "/" + RequirePath(relativePath);

    public static string CodexRollout(string threadId) => CodexPrefix + RequireSessionId(threadId);

    /// <summary>附件：<paramref name="relativePath"/> 是對話 <c>attachments\</c> 之下的相對路徑</summary>
    public static string Attachment(string conversationId, string relativePath)
        => AttachmentPrefix + RequireConversationId(conversationId) + "/" + RequirePath(relativePath);

    /// <summary>預設工作區的檔：<paramref name="relativePath"/> 是對話 <c>workspace\</c> 之下的相對路徑</summary>
    public static string Workspace(string conversationId, string relativePath)
        => WorkspacePrefix + RequireConversationId(conversationId) + "/" + RequirePath(relativePath);

    /// <summary>logId → logKey（UTF-8 的 SHA-256，64 個小寫十六進位）</summary>
    public static string LogKey(string logId)
    {
        ArgumentNullException.ThrowIfNull(logId);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(logId)));
    }

    /// <summary>64 個小寫十六進位</summary>
    public static bool IsLogKey(string? value) => value is { Length: 64 } && value.AsSpan().IndexOfAnyExcept(LowerHex) < 0;

    /// <summary>這個種類的每一段要到最後一個 LF（只附加的 jsonl）</summary>
    public static bool RequiresLf(string? kind) => kind is LogKinds.ClaudeMain or LogKinds.ClaudeSub or LogKinds.CodexRollout;

    public static bool IsValid(string? logId) => TryParse(logId, out _);

    /// <summary>
    /// 解析 logId：前綴、id 與相對路徑都合格，而且是唯一的寫法（相對路徑已正規化：正斜線、沒有多餘的分隔）才成立。
    /// 種類由 logId 決定：<c>cl:{sid}</c> 是 claude-main；<c>cl:{sid}/subagents/…/*.jsonl</c> 是 claude-sub；其他 <c>cl:{sid}/…</c> 是 claude-file
    /// </summary>
    public static bool TryParse(string? logId, [NotNullWhen(true)] out LogIdParts? parts)
    {
        parts = null;
        if (logId is null || logId.Length > MaxLogIdLength || logId.Length < 4)
            return false;
        var prefix = logId[..3];
        var rest = logId[3..];
        switch (prefix)
        {
            case ClaudePrefix:
            {
                var slash = rest.IndexOf('/');
                var sid = slash < 0 ? rest : rest[..slash];
                if (!IsSessionId(sid))
                    return false;
                if (slash < 0)
                {
                    parts = new LogIdParts(LogKinds.ClaudeMain, sid, null);
                    return true;
                }

                if (!IsCanonicalPath(rest[(slash + 1)..], out var rel))
                    return false;
                var sub = rel.StartsWith("subagents/", StringComparison.Ordinal) && rel.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
                parts = new LogIdParts(sub ? LogKinds.ClaudeSub : LogKinds.ClaudeFile, sid, rel);
                return true;
            }

            case CodexPrefix:
                if (!IsSessionId(rest))
                    return false;
                parts = new LogIdParts(LogKinds.CodexRollout, rest, null);
                return true;
            case AttachmentPrefix:
            case WorkspacePrefix:
            {
                var slash = rest.IndexOf('/');
                if (slash != ConversationIds.Length || !ConversationIds.IsWireId(rest[..slash]) || !IsCanonicalPath(rest[(slash + 1)..], out var rel))
                    return false;
                parts = new LogIdParts(prefix == AttachmentPrefix ? LogKinds.Attachment : LogKinds.Workspace, rest[..slash], rel);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>logId 的種類（不合格回 null）：上傳的 <c>X-Myriagent-Kind</c> 與 <c>conv.lease.logs[].kind</c> 必須等於它</summary>
    public static string? KindOf(string? logId) => TryParse(logId, out var parts) ? parts.Kind : null;

    /// <summary>Claude 的 session id、Codex 的 thread id：1～64 個英數字、連字號、底線（不能有句點與斜線：接手端拿它組檔名與資料夾名）</summary>
    public static bool IsSessionId(string? value)
        => value is { Length: > 0 and <= MaxSessionIdLength } && value.AsSpan().IndexOfAnyExcept(SessionIdChars) < 0;

    /// <summary>
    /// 相對路徑的正規化：反斜線換成正斜線；不能是空的、根路徑（開頭的斜線）、含 <c>:</c>（磁碟代號、替代資料流）、控制字元、Windows 不認的字元
    /// （<c>&lt;&gt;"|?*</c>）、落單的代理字元；每一段不能是空的、<c>.</c>、<c>..</c>，不能以句點或空白結尾，主檔名不能是保留的裝置名稱
    /// （CON、PRN、AUX、NUL、COM0～9、LPT0～9，不分大小寫、含副檔名）。非 ASCII 的名稱照收。不合格回 null
    /// </summary>
    public static string? NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxRelativePathLength)
            return null;
        var normalized = path.Replace('\\', '/');
        if (normalized[0] == '/')
            return null;
        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            if (ch < 0x20 || ch == 0x7F || ch == ':' || ForbiddenNameChars.Contains(ch))
                return null;
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= normalized.Length || !char.IsLowSurrogate(normalized[i + 1]))
                    return null;
                i++;
            }
            else if (char.IsLowSurrogate(ch))
            {
                return null;
            }
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment[^1] is '.' or ' ')
                return null;
            var dot = segment.IndexOf('.');
            if (ReservedNames.Contains(dot < 0 ? segment : segment[..dot]))
                return null;
        }

        return normalized;
    }

    private static bool IsCanonicalPath(string text, [NotNullWhen(true)] out string? rel)
    {
        rel = NormalizeRelativePath(text);
        if (rel is null || !string.Equals(rel, text, StringComparison.Ordinal))
        {
            rel = null;
            return false;
        }

        return true;
    }

    private static string RequireSessionId(string id)
        => IsSessionId(id) ? id : throw new ArgumentException("session id／thread id 不合格", nameof(id));

    private static string RequireConversationId(string id)
        => ConversationIds.IsWireId(id) ? id : throw new ArgumentException("對話 id 不是線上格式", nameof(id));

    private static string RequirePath(string path)
        => NormalizeRelativePath(path) ?? throw new ArgumentException("相對路徑不合格", nameof(path));
}
