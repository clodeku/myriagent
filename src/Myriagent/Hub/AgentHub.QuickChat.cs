using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 臨時對話（2026-11-11 使用者要求：右上角的對話鈕開一個浮動面板，問日常、不屬於任何專案的問題，可以選帳戶、模型與推理強度、不用選工作資料夾，
// 問完不留紀錄；回覆可以按「朗讀」念出來）。是一般的對話（同樣的會話、權限、核准），只是：
// - meta 的 Ephemeral＝true：不列在對話清單與搜尋（ListedConversations）、桌面開的不上伺服器（localOnly）、工作資料夾是對話自己的工作區；
// - 網頁關掉面板時送 quickChatEnd：停下程序、刪掉對話資料夾（不進回收區），連 CLI 自己留的逐字稿（Claude 的 projects\…、Codex 的 rollout）一起刪；
// - app 下次啟動時，上次沒刪掉的（當掉、直接結束）一樣刪掉（LoadEphemeralCleanup）。
// 朗讀（speakItem）只在桌面：以語音通知同一個 SAPI 念這則回覆的純文字（QuickChatRules.SpeechText）；瀏覽器版由網頁自己念
internal sealed partial class AgentHub
{
    /// <summary>quickChat：開一個新的臨時對話（草稿，第一次送出才建立），嵌進網頁的浮動面板（paneId 是面板的記號）</summary>
    private void QuickChat(IAgentView view, string? accountId, string? paneId)
    {
        var account = PickQuickChatAccount(accountId);
        if (account == null)
        {
            Toast("沒有可用的帳戶，請先登入一個帳戶", isError: true, view);
            return;
        }
        // 臨時對話不改一般新對話預設的帳戶（CreateDraft 會記成上次用的帳戶）
        var lastAccount = settings.LastAccountId;
        var c = CreateDraft(view, account);
        settings.LastAccountId = lastAccount;
        c.Meta.Ephemeral = true;
        c.Meta.Title = QuickChatRules.Title;
        c.Meta.WorkingDirectory = null;
        c.Meta.SyncPolicy = ConversationSyncPolicies.LocalOnly;
        PostConversation(view, c, paneId);
        MarkAccountsDirty();
    }

    /// <summary>指定的（這台的 id 或帳戶目錄的邏輯 id）可用就用它，否則同一般新對話的預設：上次用的、第一個 Claude、第一個</summary>
    private AgentAccount? PickQuickChatAccount(string? requested)
    {
        var ready = accounts.Accounts.Where(ScheduleAccountUsable).ToList();
        return ready.FirstOrDefault(a => a.Id == requested || (requested != null && a.LogicalAccountId == requested))
               ?? ready.FirstOrDefault(a => a.Id == settings.LastAccountId)
               ?? ready.FirstOrDefault(a => a.Provider == AgentProvider.Claude)
               ?? ready.FirstOrDefault();
    }

    /// <summary>quickChatEnd：關掉面板。只收臨時對話：停下、刪掉對話與 CLI 的逐字稿；草稿（還沒送出過）直接丟掉</summary>
    private void EndQuickChat(ConversationState? c)
    {
        if (c == null || !QuickChatRules.IsEphemeral(c.Meta) || c.IsReplica)
            return;
        if (c.IsDraft)
        {
            DiscardDraft(c);
            return;
        }
        var cleanup = CliTraceOf(c.Meta);
        Delete(c);
        if (cleanup != null)
            DeleteCliTraceLater(cleanup);
    }

    /// <summary>啟動時：上次沒刪掉的臨時對話（app 當掉、直接結束）不載入，資料夾與 CLI 的逐字稿刪掉</summary>
    private bool LoadEphemeralCleanup(AgentConversation meta)
    {
        if (!QuickChatRules.IsEphemeral(meta))
            return false;
        var trace = CliTraceOf(meta);
        DeleteFolderInBackground(meta.Id, quiet: true);
        if (trace != null)
            DeleteCliTraceLater(trace);
        return true;
    }

    /// <summary>朗讀一則回覆（臨時對話的「朗讀」鈕）：念出這則的純文字；沒有語音就提示</summary>
    private void SpeakItem(IAgentView view, ConversationState? c, string? itemId)
    {
        if (c == null || itemId == null)
            return;
        var item = EnsureItems(c).FirstOrDefault(i => i.Id == itemId);
        var text = QuickChatRules.SpeechText(item?.Text);
        if (text.Length == 0)
        {
            Toast("這則沒有可以朗讀的文字", isError: false, view);
            return;
        }
        host.StopSpeaking();
        host.Speak(text, VoiceTuningSetting, null);
    }

    /// <summary>CLI 自己留的紀錄在哪裡（關掉面板、或啟動清理時刪）：Claude 是專案資料夾、Codex 是 rollout 檔的搜尋條件；其他代理沒有可以安全找到的位置</summary>
    private QuickChatTrace? CliTraceOf(AgentConversation meta)
    {
        var account = accounts.Find(meta.AccountId);
        if (account == null)
            return null;
        string configDirectory;
        try
        {
            configDirectory = AccountRules.ResolveConfigDirectory(account);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        var workingDirectory = store.GetDefaultWorkingDirectory(meta.Id);
        switch (meta.Provider)
        {
            case AgentProvider.Claude:
                return QuickChatRules.ClaudeProjectFolder(configDirectory, workingDirectory) is { } folder ? new QuickChatTrace(folder, null, null) : null;
            case AgentProvider.Codex:
                return CodexAppServerRules.IsSearchableThreadId(meta.SessionId) ? new QuickChatTrace(null, Path.Combine(configDirectory, "sessions"), meta.SessionId) : null;
            case AgentProvider.Antigravity:
            case AgentProvider.Grok:
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(meta), meta.Provider, "未知的代理種類");
        }
    }

    /// <summary>背景刪 CLI 的紀錄：程序結束才放開檔案，等一下再刪、刪不掉重試幾次；只記錄、不提示</summary>
    private static void DeleteCliTraceLater(QuickChatTrace trace) => _ = Task.Run(async () =>
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 3 : 10));
            try
            {
                if (trace.ClaudeProjectFolder is { } folder)
                {
                    if (Directory.Exists(folder))
                        Directory.Delete(folder, recursive: true);
                }
                else if (trace.CodexSessions is { } sessions && trace.CodexThreadId is { } thread && Directory.Exists(sessions))
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
                    foreach (var file in Directory.EnumerateFiles(sessions, CodexAppServerRules.RolloutSearchPattern(thread), options).ToList())
                        File.Delete(file);
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                    Log.Warn($"刪不掉臨時對話的 CLI 紀錄：{ex.Message}");
            }
        }
    });

    private sealed record QuickChatTrace(string? ClaudeProjectFolder, string? CodexSessions, string? CodexThreadId);
}
