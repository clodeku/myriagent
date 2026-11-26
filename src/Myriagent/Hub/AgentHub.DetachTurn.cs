using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Git;
using Myriagent.Update;

namespace Myriagent.Hub;

/// <summary>
/// 執行中的一輪跟著放開的程序過 app 更新（設計 docs/plans/2026-11-26-agent-host.md 第 10 節，H2）：中樞這一輪的狀態——哪個事件的鍵對到哪個項目、
/// 哪些項目還在執行中——在放開之後擷取（<see cref="CaptureTurn"/>，寫進 resume.json 的 Attached[].Turn），新版在接回程序之前還原
/// （<see cref="RestoreTurn"/>）。項目本身不搬：照 items.json 的內容，載入時被收尾的執行中狀態改回來而已。
/// 接不回去時這一輪就地收尾（<see cref="EndLostTurn"/>），照 running.json 當成被中斷的一輪接續
/// </summary>
internal sealed partial class AgentHub
{
    /// <summary>程序放開、還沒接回時狀態列顯示的文字（這段時間對話看起來在執行、其實畫面不會動）</summary>
    internal const string DetachedStatus = "Clode Myriagent 更新中，稍後接著顯示…";

    /// <summary>開始前的 Git 快照超過這個大小（檔案內容的字元數合計）就不帶過去：resume.json 不能因為一個對話變成幾十 MB</summary>
    internal const int MaxDetachedGitSnapshotChars = 4_000_000;

    private static Dictionary<string, string> ItemIds(Dictionary<string, AgentChatItem> map)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, item) in map)
        {
            if (!string.IsNullOrEmpty(key))
                ids[key] = item.Id;
        }
        return ids;
    }

    /// <summary>執行中的一輪在中樞這邊的狀態（放開成功之後、動任何狀態之前呼叫：會話在放開之前發的事件都已經處理完）</summary>
    private static ResumeTurn CaptureTurn(ConversationState c)
    {
        var turn = new ResumeTurn
        {
            StartedAt = UnixMilliseconds(c.TurnStartedAt ?? DateTime.Now),
            Status = c.StatusText,
            ClearTurn = c.ClearTurn,
            Denied = c.DeniedThisTurn,
            StartedByCli = c.ProgramsTurnByCli,
            AutoResumes = c.RunAutoResumes,
            Tokens = c.TurnTokens,
            Blocks = ItemIds(c.BlockItems),
            Tools = ItemIds(c.ToolItems),
            Requests = ItemIds(c.RequestItems),
            Unapproved = ItemIds(c.UnapprovedTools),
            BackgroundTools = ItemIds(c.BackgroundToolItems),
            Movable = c.MovableTools.ToList(),
            PlanItem = c.PlanItem?.Id,
            SubagentCards = c.SubagentCards is { Count: > 0 } cards ? new Dictionary<string, string>(cards, StringComparer.Ordinal) : null,
        };
        foreach (var item in c.Items)
        {
            if (item.Status is AgentItemStatus.Running or AgentItemStatus.Waiting)
                turn.Statuses[item.Id] = item.Status.ToString();
        }
        foreach (var finished in c.PendingBackgroundFinished)
        {
            turn.BackgroundFinished.Add(new ResumeBackgroundFinished
            {
                ToolId = finished.ToolId,
                Title = finished.Title,
                Status = finished.Status.ToString(),
                ExitCode = finished.ExitCode,
                DurationMs = finished.Duration is { } duration ? (long)duration.TotalMilliseconds : null,
                StoppedByUser = finished.StoppedByUser,
            });
        }
        if (c.TurnDiff is { Completed: false } diff)
        {
            bool small = diff.Before is { } before && before.DirtyFiles.Values.Sum(f => (long)(f.Text?.Length ?? 0)) <= MaxDetachedGitSnapshotChars;
            turn.Diff = new ResumeTurnDiff
            {
                TurnId = diff.TurnId,
                Card = diff.Card?.Id,
                Before = small ? diff.Before : null,
                BaselineNote = diff.Before != null && !small ? "app 更新後沒有保留開始前的 Git 快照，僅顯示 CLI 回報的差異。" : diff.BaselineNote,
            };
        }
        return turn;
    }

    /// <summary>
    /// 新版接回程序之前：把上一版放開時的一輪還原到中樞（項目的執行中狀態、事件對到項目的對照表、執行中的旗標）。
    /// 還沒有結果之前 running.json 不動（接不回去時照它接續）。對話變化的接收者照一輪開始通知（其他裝置看到它在執行）
    /// </summary>
    private void RestoreTurn(ConversationState c, ResumeTurn turn)
    {
        EnsureItems(c);
        var byId = new Dictionary<string, AgentChatItem>(StringComparer.Ordinal);
        foreach (var item in c.Items)
            byId[item.Id] = item;   // 同一個 id 有兩個時（不應該）以後面的為準

        foreach (var (id, name) in turn.Statuses ?? new Dictionary<string, string>())
        {
            if (id == null || !byId.TryGetValue(id, out var item) || !Enum.TryParse<AgentItemStatus>(name, out var status)
                || status is not (AgentItemStatus.Running or AgentItemStatus.Waiting) || item.Status == status)
                continue;
            item.Status = status;
            MarkItemDirty(c, item);
        }

        void Fill(Dictionary<string, AgentChatItem> map, Dictionary<string, string>? ids)
        {
            foreach (var (key, id) in ids ?? new Dictionary<string, string>())
            {
                if (!string.IsNullOrEmpty(key) && id != null && byId.TryGetValue(id, out var item))
                    map[key] = item;
            }
        }
        Fill(c.BlockItems, turn.Blocks);
        Fill(c.ToolItems, turn.Tools);
        Fill(c.RequestItems, turn.Requests);
        Fill(c.UnapprovedTools, turn.Unapproved);
        Fill(c.BackgroundToolItems, turn.BackgroundTools);
        c.MovableTools.UnionWith((turn.Movable ?? new List<string>()).Where(id => !string.IsNullOrEmpty(id)));
        c.PlanItem = turn.PlanItem != null && byId.TryGetValue(turn.PlanItem, out var plan) ? plan : null;
        if (turn.SubagentCards is { Count: > 0 } subagents)
            c.SubagentCards = new Dictionary<string, string>(subagents.Where(kv => kv.Key != null && kv.Value != null), StringComparer.Ordinal);
        foreach (var finished in turn.BackgroundFinished ?? new List<ResumeBackgroundFinished>())
        {
            if (finished == null || string.IsNullOrEmpty(finished.ToolId))
                continue;
            c.PendingBackgroundFinished.Add(new AgentBackgroundCommandFinishedEvent(finished.ToolId, finished.Title,
                Enum.TryParse<AgentItemStatus>(finished.Status, out var status) ? status : AgentItemStatus.Done, finished.ExitCode,
                finished.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null, finished.StoppedByUser));
        }
        if (turn.Diff is { } diff)
        {
            c.TurnDiff = new TurnDiffState
            {
                TurnId = diff.TurnId,
                Before = diff.Before is { Root: not null, DirtyFiles: not null } before ? before : null,
                BaselineNote = diff.BaselineNote,
                Card = diff.Card != null && byId.TryGetValue(diff.Card, out var card) ? card : null,
            };
        }

        var now = DateTime.Now;
        c.Running = true;
        c.StopRequested = false;
        c.DeniedThisTurn = turn.Denied;
        c.ClearTurn = turn.ClearTurn;
        c.QuotaExhausted = null;
        c.StatusText = string.IsNullOrEmpty(turn.Status) || turn.Status == DetachedStatus ? "思考中…" : turn.Status;
        c.TurnStartedAt = turn.StartedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(turn.StartedAt).LocalDateTime : now;
        c.TurnTokens = turn.Tokens;
        c.LastActivity = now;
        c.RunAutoResumes = Math.Max(0, turn.AutoResumes);
        c.JournalTurnStarted = c.TurnStartedAt;
        // 這一輪開出的程式要等它結束才算背景程式（同 HookTurnStarted 的 OnProgramsTurnStarted）
        c.ProgramsIdleSince = null;
        c.ProgramsTurnByCli = turn.StartedByCli;
        c.RunMarkerFrozen = true;
        NotifySinkTurnStarted(c);
        UpdateRunningCounts();
        MarkHeaderDirty(c);
        MarkListDirty();
    }

    /// <summary>
    /// 放開的程序接不回去、而那一輪還算執行中：就地收尾——執行中的項目照載入紀錄時的規則收尾（工具算被中斷）、對照表清掉、對話回到閒置。
    /// hooked＝這一輪是在這個 app 開始的（放棄更新時就地接回失敗）：照一般的一輪結束走掛鉤（馬上會接著做）；
    /// 新版還原的那一輪沒有經過 HookTurnStarted，只通知對話變化的接收者
    /// </summary>
    private void EndLostTurn(ConversationState c, bool hooked)
    {
        EnsureItems(c);
        foreach (var item in c.Items.Where(i => i.Status is AgentItemStatus.Running or AgentItemStatus.Waiting).ToList())
        {
            if (item.Kind is AgentItemKind.Assistant or AgentItemKind.Thinking && item.Status == AgentItemStatus.Running
                && string.IsNullOrWhiteSpace(item.Text) && item.MediaPaths.Count == 0)
            {
                RemoveItem(c, item);
                continue;
            }
            if (ItemLoadRules.FinalizeOnLoad(item))
                MarkItemDirty(c, item);
        }
        c.BlockItems.Clear();
        c.ToolItems.Clear();
        c.MovableTools.Clear();
        c.UnapprovedTools.Clear();
        c.BackgroundToolItems.Clear();
        foreach (var key in c.RequestItems.Where(kv => !kv.Value.AnswerAsMessage).Select(kv => kv.Key).ToList())
            c.RequestItems.Remove(key);
        c.PlanItem = null;
        c.TurnDiff = null;
        c.PendingBackgroundFinished.Clear();
        c.PendingFeedback.Clear();
        ForgetSteer(c);
        if (TurnTokenRules.Final(null, c.TurnTokens) is { } counted)
            c.LastUsage = counted;
        CloseTurnTokens(c);
        bool wasRunning = c.Running;
        c.Running = false;
        c.StatusText = string.Empty;
        c.JournalTurnStarted = hooked ? c.JournalTurnStarted : null;
        if (wasRunning)
        {
            if (hooked)
                HookTurnCompleted(c, AgentTurnOutcome.Interrupted, null, continues: true);
            else
                NotifySinkTurnCompleted(c, AgentTurnOutcome.Interrupted);
        }
        c.Meta.UpdatedAt = DateTime.Now;
        SaveConversation(c, saveItems: true);
        UpdateRunningCounts();
        MarkHeaderDirty(c);
        MarkListDirty();
    }

    private void NotifySinkTurnStarted(ConversationState c)
    {
        if (conversationSink is not { } sink)
            return;
        try
        {
            sink.OnTurnStarted(HookInfo(c), startedByCli: true);
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(sink.OnTurnStarted), ex);
        }
    }

    private void NotifySinkTurnCompleted(ConversationState c, AgentTurnOutcome outcome)
    {
        if (conversationSink is not { } sink)
            return;
        try
        {
            sink.OnTurnCompleted(HookInfo(c), outcome, null);
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(sink.OnTurnCompleted), ex);
        }
    }
}
