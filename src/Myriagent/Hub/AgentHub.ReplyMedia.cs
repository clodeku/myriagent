using Myriagent.Accounts;
using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>
/// 回覆文字裡的相對媒體路徑（2026-11-06 使用者回報：Grok 回「videos/1.mp4」，對話裡看不到影片）：Grok 產生的圖片與影片存在它自己的
/// 工作階段資料夾（<see cref="GrokAcpRules.SessionDirectory"/>），回覆寫的是相對那裡的路徑。回覆完成時（AgentTextCompletedEvent）
/// 依序以工作階段資料夾、工作資料夾解析，存在的可顯示媒體接在回覆下面（<see cref="AgentHubRules.FindMediaToShow"/>），之後照常留一份（MediaKeep）
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>找到的 grok 工作階段資料夾（只記找到的；鍵是 session id＋工作資料夾，換了就重找）</summary>
        public (string Key, string Directory)? GrokSessionDirectory;
    }

    /// <summary>
    /// 回覆裡相對路徑的基準資料夾（依序）；這種代理不找相對路徑、或文字裡沒有相對的媒體路徑時回 null（不碰磁碟）
    /// </summary>
    private IReadOnlyList<string?>? RelativeMediaBases(ConversationState c, string? text)
    {
        if (c.IsReplica || !AgentHubRules.ShowsRelativeReplyMedia(c.Meta.Provider) || AgentHubRules.ExtractRelativeMediaPaths(text).Count == 0)
            return null;
        var workingDirectory = GetWorkingDirectory(c);
        return [FindGrokSessionDirectory(c, workingDirectory), workingDirectory];
    }

    /// <summary>
    /// 這個對話在 grok 的工作階段資料夾：先照編碼規則組出來，不在（編碼不同、工作資料夾換過）就在 sessions\* 底下找同一個 session id 的資料夾
    /// </summary>
    private string? FindGrokSessionDirectory(ConversationState c, string workingDirectory)
    {
        var sessionId = c.Meta.SessionId;
        if (!c.Meta.SessionStarted || !GrokAcpRules.IsPlainSessionId(sessionId))
            return null;
        var key = sessionId + "|" + workingDirectory;
        if (c.GrokSessionDirectory is { } known && known.Key == key && Directory.Exists(known.Directory))
            return known.Directory;
        if (accounts.Find(c.Meta.AccountId) is not { } account)
            return null;
        string home;
        try
        {
            home = AccountRules.ResolveConfigDirectory(account);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
        var found = GrokAcpRules.SessionDirectory(home, workingDirectory, sessionId) is { } direct && Directory.Exists(direct)
            ? direct
            : ScanGrokSessions(home, sessionId!);
        if (found != null)
            c.GrokSessionDirectory = (key, found);
        return found;
    }

    private static string? ScanGrokSessions(string home, string sessionId)
    {
        try
        {
            var root = Path.Combine(home, "sessions");
            if (!AgentAttachmentRules.IsLocalDrivePath(root) || !Directory.Exists(root))
                return null;
            foreach (var project in Directory.EnumerateDirectories(root))
            {
                var candidate = Path.Combine(project, sessionId);
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }
}
