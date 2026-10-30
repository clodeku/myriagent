using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 額度用完時自動換帳戶繼續（設定 autoSwitchAccount，預設開；規則在 AccountSwitchRules）：會話回報這一輪因為訂閱額度用完而失敗
/// （AgentQuotaExhaustedEvent），這一輪結束時改用同一種代理、同一個模型、還有額度、下一次重置最早的帳戶，
/// 隱藏送出接續訊息（前文節錄照換帳戶的規則附在前面），對話加一則 noticeKind accountSwitch 的提示。
/// 用完的帳戶記在記憶體到重置為止（不知道何時重置就 30 分鐘），這段時間不再被選，同一個對話連續用完也不會在帳戶之間來回
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>這一輪收到的額度用完（ResetsAt 已補上用量快照推得的重置時間）；這一輪結束時取用並清掉，新的一輪開始時也清掉</summary>
        public AgentQuotaExhaustedEvent? QuotaExhausted;

        /// <summary>
        /// 執行中手動選了另一個帳戶（SwitchAccountWhileRunning）：這一輪結束時（CompleteTurn）換過去——立即換的這一輪已在停下，
        /// AtTurnEnd 的等目前步驟完成，到安全點再停下交接；同一輪再選一次只換目標
        /// </summary>
        public PendingAccountSwitch? PendingAccountSwitch;
    }

    /// <summary>
    /// 執行中手動換帳戶的目標；RememberAccount＝false 是遠端（不改「上次使用的帳戶」）；
    /// AtTurnEnd＝等目前步驟完成；到安全點後改成 false，沿用立即換的停止與接續流程（協定仍用 turnEnd）
    /// </summary>
    private sealed record PendingAccountSwitch(string AccountId, bool RememberAccount, bool AtTurnEnd = false);

    /// <summary>這一輪結束時要不要換帳戶：Account 不為 null＝換到它；NoCandidateNotice 不為 null＝換不了、要加的提示</summary>
    /// <param name="WaitUntil">換不了：等這個帳戶的額度重置到這個時間後自動接著做（AgentHub.QuotaWait.cs）；不等是 null</param>
    private sealed record AccountSwitchPlan(AgentAccount? Account, DateTimeOffset? FromResetsAt, DateTimeOffset? ToResetsAt, string? NoCandidateNotice,
        DateTimeOffset? WaitUntil = null);

    /// <summary>最近用完額度的帳戶與到什麼時候之前不當候選（只在記憶體；AccountSwitchRules.ExhaustedUntil）</summary>
    private readonly Dictionary<string, DateTimeOffset> exhaustedAccounts = new(StringComparer.Ordinal);

    /// <summary>現在時間（測試換成固定的；重置時間、用完的帳戶何時可再選都照它）</summary>
    internal Func<DateTimeOffset> AccountSwitchClock { get; set; } = () => DateTimeOffset.Now;

    /// <summary>會話回報額度用完：記下這個帳戶用完到何時、立即排一次用量重查；在執行中的一輪就記給這一輪結束時用</summary>
    private void OnQuotaExhausted(ConversationState c, AgentQuotaExhaustedEvent ev)
    {
        var now = AccountSwitchClock();
        var resets = ev.ResetsAt is { } r && r > now
            ? r
            : AccountSwitchRules.ExhaustedResetsAt(accounts.GetUsage(c.Meta.AccountId)?.Windows, c.Meta.Model, now);
        exhaustedAccounts[c.Meta.AccountId] = AccountSwitchRules.ExhaustedUntil(resets, now);
        Log.Info($"對話 {c.Meta.Id} 的帳戶 {c.Meta.AccountId} 額度用完（{(resets.HasValue ? resets.Value.ToString("O") : "重置時間不明")}）");
        accounts.ReportQuotaExhausted(c.Meta.AccountId);
        if (c.Running)
            c.QuotaExhausted = ev with { ResetsAt = resets };
    }

    /// <summary>
    /// 這一輪結束時（CompleteTurn）：取用並清掉這一輪的額度用完，決定要不要換帳戶。設定關著、使用者按了停止、這一輪是被停止的、
    /// 這種代理不支援時不換；挑不到接手的帳戶時，有其他同種帳戶才給提示（只用一個帳戶的人原本的錯誤就夠了）。
    /// 只做決定、不改狀態（提示由 CompleteTurn 依項目順序加、換帳戶由 ContinueOnAnotherAccount 做）
    /// </summary>
    private AccountSwitchPlan? PlanAccountSwitch(ConversationState c, AgentTurnOutcome outcome, bool stopRequested)
    {
        var quota = c.QuotaExhausted;
        c.QuotaExhausted = null;
        if (quota == null || outcome == AgentTurnOutcome.Interrupted || stopRequested || c.IsDraft || c.IsReplica)
            return null;
        // 換不了帳戶時等重置後自動接著做（設定 autoResumeAfterQuota；12 小時內重置的）
        var wait = PlanQuotaWait(c, quota.ResetsAt);
        if (!settings.AutoSwitchAccountOnLimit || !AccountSwitchRules.Supports(c.Meta.Provider))
            return wait != null ? new AccountSwitchPlan(null, quota.ResetsAt, null, null, wait) : null;

        var now = AccountSwitchClock();
        bool allowCross = settings.AutoSwitchCrossProviderOnLimit;
        var candidates = accounts.Accounts.Select(a => new AccountSwitchCandidate(
            a.Id,
            a.Provider,
            accounts.GetState(a.Id).Status,
            settings.Models.TryGetValue(a.Id, out var models) ? models.Select(m => m.Value).ToList() : null,
            accounts.GetUsage(a.Id)?.Windows,
            a.Order));
        var choice = AccountSwitchRules.Choose(c.Meta.Provider, c.Meta.AccountId, c.Meta.Model, candidates, exhaustedAccounts, now, allowCross);
        if (choice != null && accounts.Find(choice.AccountId) is { } account)
            return new AccountSwitchPlan(account, quota.ResetsAt, choice.ResetsAt, null);

        bool hasOthers = allowCross
            ? accounts.Accounts.Any(a => a.Id != c.Meta.AccountId)
            : accounts.Accounts.Any(a => a.Provider == c.Meta.Provider && a.Id != c.Meta.AccountId);
        Log.Info($"對話 {c.Meta.Id} 額度用完，沒有可以接手的帳戶（{(hasOthers ? "其他帳戶都不合格" : "沒有其他可選帳戶")}）");
        // 會等重置的不另外說「可以等額度重置後按送出繼續」（等待的提示已經寫了沒有帳戶可以接手）
        if (wait != null)
            return new AccountSwitchPlan(null, quota.ResetsAt, null, null, wait);
        return hasOthers ? new AccountSwitchPlan(null, quota.ResetsAt, null, AccountSwitchRules.BuildNoCandidateNotice(c.Meta.Provider, allowCross)) : null;
    }

    /// <summary>
    /// 執行中手動換帳戶（同一種代理或換到另一種代理，呼叫端已以 AccountSwitchRules.WhileRunningBlock 確認）：記下目標並停下這一輪；這一輪結束時
    /// （CompleteTurn → TakeUserAccountSwitch）換過去、從中斷處接著做。已經在停下（按了停止、前一次選的還沒換）時只換目標。
    /// <paramref name="atTurnEnd"/>：先記下目標，等目前步驟完成再停下交接；這一輪先結束則照結果換——已經為了換帳戶在停下時
    /// 停止收不回來，照立即換（只換目標）
    /// </summary>
    private void SwitchAccountWhileRunning(IAgentView view, ConversationState c, AgentAccount account, bool rememberAccount, bool atTurnEnd)
    {
        bool stopping = c.StopRequested;
        if (atTurnEnd && c.PendingAccountSwitch is { AtTurnEnd: false })
            atTurnEnd = false;
        c.PendingAccountSwitch = new PendingAccountSwitch(account.Id, rememberAccount, atTurnEnd);
        // 之前選了、在等背景工作結束才換的：以這次選的為準（這一輪結束時還有背景工作就再等一次）
        DropDeferredAccountSwitch(c, "replaced");
        MarkHeaderDirty(c);
        if (atTurnEnd)
        {
            Log.Info($"對話 {c.Meta.Id} 執行中選了帳戶 {account.Id}：等目前步驟完成後換");
            Toast($"目前步驟完成後改用「{account.Label}」繼續", view: view);
            EvaluatePendingAccountSwitch(c);
            return;
        }
        Log.Info($"對話 {c.Meta.Id} 執行中改用帳戶 {account.Id}：先停下這一輪{(stopping ? "（已經在停下）" : string.Empty)}");
        if (!stopping)
            _ = StopAsync(c);
        SetStatus(c, $"停下這一輪，改用「{account.Label}」繼續…");
    }

    /// <summary>
    /// 等目前步驟完成的換帳戶：會話已送出、沒有工具、背景工作或待回應請求時停下交接，不等整個長任務自行結束。
    /// 安全點事件可能已過時，要重新讀會話的狀態；工具卡也要處理完才停，以便接手者拿到完整的工具結果。
    /// 使用者已按停止、正在重開或目標已失效時不自動停下。先改成立即換，避免停止觸發事件後重複中斷。
    /// </summary>
    private void EvaluatePendingAccountSwitch(ConversationState c)
    {
        if (disposed || !c.Running || c.StopRequested || c.IsDraft || c.IsReplica || IsRestartInFlight(c)
            || c.PendingAccountSwitch is not { AtTurnEnd: true } pending
            || c.Session is not { IsRunning: true, IsAtSafePoint: true } || !c.Meta.SessionStarted
            || c.WaitingCount > 0 || ForegroundRunningTools(c).Any())
            return;
        if (accounts.Find(pending.AccountId) is not { } account || account.Id == c.Meta.AccountId
            || AccountSwitchRules.WhileRunningBlock(c.Meta.Provider, account.Provider) != null
            || accounts.GetState(account.Id).Status is AccountStatus.NotLoggedIn or AccountStatus.NotInstalled)
            return;

        c.PendingAccountSwitch = pending with { AtTurnEnd = false };
        MarkHeaderDirty(c);
        Log.Info($"對話 {c.Meta.Id} 目前步驟已完成：停下這一輪，改用帳戶 {account.Id} 接續");
        _ = StopAsync(c);
        SetStatus(c, $"目前步驟已完成，改用「{account.Label}」繼續…");
    }

    /// <summary>
    /// 這一輪結束時取用並清掉執行中手動選的帳戶：帳戶還在、執行中還能換過去（AccountSwitchRules.WhileRunningBlock）、不是目前的帳戶才換
    /// （否則照一般的停止收尾）
    /// </summary>
    private PendingAccountSwitch? TakeUserAccountSwitch(ConversationState c)
    {
        var pending = c.PendingAccountSwitch;
        c.PendingAccountSwitch = null;
        if (pending == null || c.IsDraft || c.IsReplica)
            return null;
        if (accounts.Find(pending.AccountId) is not { } account || AccountSwitchRules.WhileRunningBlock(c.Meta.Provider, account.Provider) != null
            || account.Id == c.Meta.AccountId
            || accounts.GetState(account.Id).Status is AccountStatus.NotLoggedIn or AccountStatus.NotInstalled)
        {
            Log.Info($"對話 {c.Meta.Id} 執行中選的帳戶 {pending.AccountId} 已不能換（不存在、未登入／未安裝、換成不能換的代理種類或就是目前的帳戶），照一般的停止收尾");
            return null;
        }
        return pending;
    }

    /// <summary>
    /// 標頭的 pendingAccount（web-protocol 的 Header）：執行中選了另一個帳戶、還沒換過去；或選了帳戶、在等背景工作結束才換
    /// （<c>background: true</c>，執行中與閒置都有）。沒有是 null
    /// </summary>
    private JToken RenderPendingAccount(ConversationState c)
    {
        var pending = c.Running ? c.PendingAccountSwitch : null;
        var accountId = pending?.AccountId ?? c.Meta.DeferredAccount?.AccountId;
        if (accountId == null || accounts.Find(accountId) is not { } account || account.Id == c.Meta.AccountId)
            return JValue.CreateNull();
        var view = new JObject
        {
            ["accountId"] = account.Id,
            ["label"] = account.Label,
            ["when"] = pending is { AtTurnEnd: false } ? AccountSwitchRules.WhenNow : AccountSwitchRules.WhenTurnEnd,
        };
        if (pending == null)
            view["background"] = true;
        return view;
    }

    // ───────────────────────────── 等背景工作結束才換帳戶（2026-10-27） ─────────────────────────────
    // 換帳戶會關掉程序（ApplyAccountChange），還在跑的背景工作跟著停、代理也不知道（10-27 實例：「這一輪結束後再換」在一輪結束時
    // 直接換過去，兩個背景擷取被停掉，新帳戶的代理花了 8 分鐘查原因再重跑）。所以使用者選了帳戶、還有背景工作在跑時先記著
    // （meta 的 DeferredAccount），等背景工作都結束、代理處理完結果、對話閒置時才換（維護、送出之前）；使用者確認現在就換的
    // （setAccount 的 when 是 now）照換，並隱藏送出接續訊息告訴新帳戶哪些背景工作被停掉了

    /// <summary>
    /// 記下要換的帳戶，等背景工作結束才換：對話加一則提示、標頭的 pendingAccount 標出（background）。
    /// 已經在等的再選一次只換目標
    /// </summary>
    private void DeferAccountSwitch(ConversationState c, AgentAccount account, bool rememberAccount)
    {
        var work = BackgroundOf(c);
        int count = work?.Count ?? 0;
        c.Meta.DeferredAccount = new DeferredAccountSwitch(account.Id, rememberAccount);
        Log.Info($"對話 {c.Meta.Id} 選了帳戶 {account.Id}，還有 {count} 個背景工作在跑：等背景工作結束才換");
        Journal("acct.defer", c, new JObject
        {
            ["phase"] = "waiting",
            ["to"] = account.Id,
            ["bgCount"] = work?.Count,
            ["bg"] = work is { Tools.Count: > 0 } ? BackgroundJournalRules.Tools(work.Tools) : null,
        });
        AddItem(c, new AgentChatItem
        {
            Kind = AgentItemKind.Notice,
            Text = AccountSwitchRules.BuildDeferredNotice(count, account.Label),
            NoticeKind = AgentNoticeKind.AccountSwitch,
        });
        SaveConversation(c, saveItems: true);
        MarkHeaderDirty(c);
    }

    /// <summary>不再等（選回目前的帳戶、換成別的帳戶、目標不能用了）：清掉並記進日誌；沒在等時不做事。呼叫端負責提示與存檔</summary>
    private void DropDeferredAccountSwitch(ConversationState c, string why)
    {
        if (c.Meta.DeferredAccount is not { } deferred)
            return;
        c.Meta.DeferredAccount = null;
        Journal("acct.defer", c, new JObject { ["phase"] = why, ["to"] = deferred.AccountId });
        MarkHeaderDirty(c);
    }

    /// <summary>
    /// 等背景工作結束才換的帳戶，現在能不能換、能換就換（ApplyChosenAccount：關掉程序、下一則訊息附前文節錄）：不在執行中、沒有存活的背景工作、
    /// 沒有在換版、沒有排著的過載重試、背景工作的接續或 CLI 換版後的背景確認（那些接續要先由原帳戶做完）、沒有等你回應的卡片。
    /// 選的帳戶已經不能用（移除、登出、沒安裝）就不再等、加提示
    /// </summary>
    /// <returns>換過去了</returns>
    private bool TryApplyDeferredAccountSwitch(ConversationState c)
    {
        if (c.Meta.DeferredAccount is not { } deferred || c.Running || c.IsDraft || c.IsReplica)
            return false;
        var account = accounts.Find(deferred.AccountId);
        if (account != null && account.Id == c.Meta.AccountId)
        {
            DropDeferredAccountSwitch(c, "same");
            SaveConversation(c, saveItems: false);
            return false;
        }
        if ((c.Session?.HasLiveBackgroundWork ?? false) || IsRestartInFlight(c) || c.OverloadRetryTicket != 0 || c.PendingBackgroundCheck != null
            || c.PendingBackgroundFinished.Count > 0)
            return false;
        var items = EnsureItems(c, notifyUnreadable: false);
        if (!c.ItemsLoaded || items.Any(i => i.Status == AgentItemStatus.Waiting))
            return false;
        if (account == null || accounts.GetState(account.Id).Status is AccountStatus.NotLoggedIn or AccountStatus.NotInstalled)
        {
            Log.Info($"對話 {c.Meta.Id} 等背景工作結束要換的帳戶 {deferred.AccountId} 已不能用，不換");
            DropDeferredAccountSwitch(c, "unusable");
            AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = AccountSwitchRules.BuildDeferredDroppedNotice(account?.Label) });
            SaveConversation(c, saveItems: true);
            return false;
        }
        Log.Info($"對話 {c.Meta.Id} 的背景工作都結束了，由帳戶 {c.Meta.AccountId} 改用 {account.Id}");
        Journal("acct.defer", c, new JObject { ["phase"] = "applied", ["to"] = account.Id });
        c.Meta.DeferredAccount = null;
        ApplyChosenAccount(c, account, deferred.RememberAccount);
        return true;
    }

    /// <summary>維護（每 30 秒）：等背景工作結束才換帳戶的對話，現在能換就換</summary>
    private void TickDeferredAccountSwitch()
    {
        foreach (var c in conversations.Values.Where(c => c.Meta.DeferredAccount != null).ToList())
            TryApplyDeferredAccountSwitch(c);
    }

    /// <summary>
    /// 閒置、背景工作還在跑時使用者確認了現在就換（setAccount 的 when 是 now）：換過去（背景工作跟著舊的程序停掉），
    /// 隱藏送出接續訊息告訴新帳戶哪些背景工作被停掉了、請它確認做到哪裡再接著做（同額度用完換帳戶時的 backgroundStopped）
    /// </summary>
    private void SwitchAccountStoppingBackground(ConversationState c, AgentAccount account, bool rememberAccount)
    {
        var previous = accounts.Find(c.Meta.AccountId);
        var fromProvider = c.Meta.Provider;
        var steps = AccountSwitchRules.RecentSteps(EnsureItems(c, notifyUnreadable: false));
        var work = BackgroundOf(c);
        var stopped = AccountSwitchRules.BackgroundSteps(work?.Tools);
        int count = work?.Count ?? 0;
        Log.Info($"對話 {c.Meta.Id} 由帳戶 {c.Meta.AccountId} 改用 {account.Id}：使用者確認現在就換，停掉 {count} 個背景工作、請新帳戶確認後接著做");

        // 使用者自己選了帳戶：不再自動換回額度用完之前的原帳戶
        c.Meta.QuotaHome = null;
        ApplyAccountChange(c, account);
        if (rememberAccount)
            settings.LastAccountId = account.Id;
        PublishAccountChange(c);

        var notice = AccountSwitchRules.BuildIdleSwitchStoppedNotice(previous?.Label, account.Label, count);
        var text = AccountSwitchRules.BuildIdleSwitchContinuation(fromProvider, account.Provider, steps, stopped);
        _ = SendCoreAsync(c, text, new List<AgentAttachment>(), hiddenNotice: notice, hiddenNoticeKind: AgentNoticeKind.AccountSwitch,
            rememberLocal: rememberAccount);
    }

    /// <summary>
    /// 執行中手動換帳戶、這一輪停下（或失敗）之後：換過去並隱藏送出接續訊息（同 ContinueOnAnotherAccount，提示與接續訊息寫使用者換了帳戶）。
    /// 選了「這一輪結束時才換」的只有原帳戶額度用完而失敗時才走到這裡（AccountSwitchRules.ContinuesAfterUserSwitch）：提示與接續訊息寫額度用完
    /// </summary>
    private void ContinueOnChosenAccount(ConversationState c, PendingAccountSwitch pending)
    {
        var account = accounts.Find(pending.AccountId)!;
        var previous = accounts.Find(c.Meta.AccountId);
        // 跨代理互換時接續訊息寫成換了代理（ApplyAccountChange 之後 Meta.Provider 就是新的）
        var fromProvider = c.Meta.Provider;
        var steps = AccountSwitchRules.RecentSteps(c.Items);
        bool backgroundStopped = c.Session?.HasLiveBackgroundWork ?? false;
        Log.Info($"對話 {c.Meta.Id} 執行中由帳戶 {c.Meta.AccountId} 改用 {account.Id}，從中斷處接著做");

        // 使用者自己選了帳戶：不再自動換回額度用完之前的原帳戶
        c.Meta.QuotaHome = null;
        ApplyAccountChange(c, account);
        if (pending.RememberAccount)
            settings.LastAccountId = account.Id;
        PublishAccountChange(c);

        var notice = pending.AtTurnEnd
            ? AccountSwitchRules.BuildChosenAfterQuotaNotice(previous?.Label, account.Label)
            : AccountSwitchRules.BuildUserSwitchNotice(previous?.Label, account.Label);
        var text = pending.AtTurnEnd
            ? AccountSwitchRules.BuildChosenAfterQuotaContinuation(fromProvider, account.Provider, steps, backgroundStopped)
            : AccountSwitchRules.BuildUserSwitchContinuation(account.Provider, steps, backgroundStopped, fromProvider);
        // 遠端換的（RememberAccount＝false）：接續的送出也不寫「上次使用的帳戶」（D23）
        _ = SendCoreAsync(c, text, new List<AgentAttachment>(), hiddenNotice: notice, hiddenNoticeKind: AgentNoticeKind.AccountSwitch,
            rememberLocal: pending.RememberAccount);
    }

    /// <summary>
    /// 換到接手的帳戶並隱藏送出接續訊息（這一輪已收尾、Running 已是 false）：中斷前的步驟與背景工作要在關掉舊程序之前記下；
    /// 換帳戶同手動（ApplyAccountChange：關舊程序、新帳戶開新 session，送出時附前文節錄），不寫「上次使用的帳戶」以外的設定。
    /// 記下原帳戶（meta 的 QuotaHome；連續換了好幾個帳戶時一直是第一個），它的額度恢復後換回去（TryReturnHome）；接手的就是原帳戶時當成換回
    /// </summary>
    private void ContinueOnAnotherAccount(ConversationState c, AccountSwitchPlan plan)
    {
        var account = plan.Account!;
        var previous = accounts.Find(c.Meta.AccountId);
        var fromProvider = c.Meta.Provider;
        var steps = AccountSwitchRules.RecentSteps(c.Items);
        bool backgroundStopped = c.Session?.HasLiveBackgroundWork ?? false;
        var now = AccountSwitchClock();
        Log.Info($"對話 {c.Meta.Id} 額度用完，自動由帳戶 {c.Meta.AccountId} 改用 {account.Id} 繼續");

        var home = c.Meta.QuotaHome ?? new QuotaHomeAccount(c.Meta.AccountId, plan.FromResetsAt, now, c.Meta.Model, c.Meta.Effort, c.Meta.PermissionMode,
            c.Meta.Agent, c.Meta.OutputStyle);
        var modelBefore = c.Meta.Model;
        var effortBefore = c.Meta.Effort;
        ApplyAccountChange(c, account);
        bool backHome = account.Id == home.AccountId;
        if (backHome)
        {
            RestoreHomeSettings(c, home, crossProvider: fromProvider != account.Provider, modelBefore, effortBefore);
            c.Meta.QuotaHome = null;
        }
        else
        {
            c.Meta.QuotaHome = home with { SwitchedModel = c.Meta.Model, SwitchedEffort = c.Meta.Effort };
        }
        PublishAccountChange(c);

        var homeLabel = backHome ? null : accounts.Find(home.AccountId)?.Label;
        var notice = AccountSwitchRules.BuildNotice(previous?.Label, plan.FromResetsAt, account.Label, plan.ToResetsAt, now, homeLabel: homeLabel);
        var text = AccountSwitchRules.BuildContinuation(account.Provider, steps, backgroundStopped, fromProvider);
        _ = SendCoreAsync(c, text, new List<AgentAttachment>(), hiddenNotice: notice, hiddenNoticeKind: AgentNoticeKind.AccountSwitch, rememberLocal: false);
    }

    /// <summary>
    /// 額度用完自動換到別的帳戶之後，原帳戶的額度恢復了就換回去（2026-10-26 使用者要求）。只在安全的時候換：不在執行中、
    /// 沒有存活的背景工作（換帳戶會關掉程序，背景工作跟著停）、沒有等你回應的卡片、沒有在換版、沒有排著過載重試或 CLI 換版後的背景確認、
    /// 沒有回溯點、沒有進行中的 /goal 目標、使用者沒有選了別的帳戶在等背景工作結束才換；閒置時（維護）另外避開 10 分鐘內有動靜的 Remote Control（AccountSwitchRules.ReturnRemoteIdle）。
    /// 換回時同手動換帳戶（關掉程序、下一則訊息附前文節錄），跨代理換回的另外還原換走之前的模型、推理強度、權限模式等，加一則提示。
    /// 設定「額度用完時自動換帳戶繼續」關著時不換（記著的原帳戶留著）；原帳戶已移除時不再記著
    /// </summary>
    /// <param name="fromSend">送出之前（使用者的訊息、從佇列接著送的；SendCoreAsync 接著存檔）；false＝閒置時的維護</param>
    /// <param name="localNow">現在時間（本機時鐘；和 LastActivity 比）</param>
    /// <returns>換回去了</returns>
    private bool TryReturnHome(ConversationState c, bool fromSend, DateTime localNow)
    {
        if (c.Meta.QuotaHome is not { } home || !settings.AutoSwitchAccountOnLimit || c.Running || c.IsDraft || c.IsReplica)
            return false;
        var account = accounts.Find(home.AccountId);
        if (account == null || account.Id == c.Meta.AccountId)
        {
            c.Meta.QuotaHome = null;
            if (!fromSend)
                SaveConversation(c, saveItems: false);
            return false;
        }
        // Claude 進行中的 /goal 目標只活在程序裡（AgentHub.Goal.cs）：換帳戶會關掉程序、目標跟著沒了，等它達成或清掉
        var session = c.Session;
        if ((session?.HasLiveBackgroundWork ?? false) || IsRestartInFlight(c) || c.OverloadRetryTicket != 0 || c.PendingBackgroundCheck != null
            || c.Meta.ResumeAt != null || c.PendingAccountSwitch != null || c.Meta.DeferredAccount != null || c.Goal != null)
            return false;
        if (!fromSend && session is { HasRemoteControl: true } && localNow - c.LastActivity < AccountSwitchRules.ReturnRemoteIdle)
            return false;
        var now = AccountSwitchClock();
        DateTimeOffset? exhaustedUntil = exhaustedAccounts.TryGetValue(account.Id, out var until) ? until : null;
        if (!AccountSwitchRules.HomeRecovered(home, accounts.GetState(account.Id).Status, accounts.GetUsage(account.Id), exhaustedUntil, now))
            return false;
        // 紀錄檔暫時讀不到：下次再換（不跳提示：維護每 30 秒一次，送出時 SendCoreAsync 自己會提示）；
        // 等你回應的卡片（核准、問卷、計畫確認，含以訊息回答的）：答案要給問的那個程序
        var items = EnsureItems(c, notifyUnreadable: false);
        if (!c.ItemsLoaded || items.Any(i => i.Status == AgentItemStatus.Waiting))
            return false;

        var previous = accounts.Find(c.Meta.AccountId);
        var fromProvider = c.Meta.Provider;
        var modelBefore = c.Meta.Model;
        var effortBefore = c.Meta.Effort;
        Log.Info($"對話 {c.Meta.Id} 的原帳戶 {account.Id} 額度已恢復，由帳戶 {c.Meta.AccountId} 換回（{(fromSend ? "送出之前" : "閒置時")}）");
        c.Meta.QuotaHome = null;
        ApplyAccountChange(c, account);
        RestoreHomeSettings(c, home, crossProvider: fromProvider != account.Provider, modelBefore, effortBefore);
        AddItem(c, new AgentChatItem
        {
            Kind = AgentItemKind.Notice,
            Text = AccountSwitchRules.BuildReturnNotice(account.Label, previous?.Label),
            NoticeKind = AgentNoticeKind.AccountSwitch,
        });
        if (!fromSend)
            SaveConversation(c, saveItems: true);
        MarkListDirty();
        PublishAccountChange(c);
        return true;
    }

    /// <summary>維護（每 30 秒）：記著原帳戶的閒置對話，原帳戶的額度恢復了就換回去</summary>
    private void TickReturnHome(DateTime localNow)
    {
        foreach (var c in conversations.Values.Where(c => c.Meta.QuotaHome != null).ToList())
            TryReturnHome(c, fromSend: false, localNow);
    }

    /// <summary>
    /// 換回原帳戶之後（ApplyAccountChange 已照換帳戶的規則設好）還原換走之前的設定：模型與推理強度照 AccountSwitchRules.RestoresHomeModel，
    /// 原帳戶已知的模型清單沒有那個模型就不還原；跨代理換回的另外還原權限模式、自訂代理與輸出風格
    /// </summary>
    private void RestoreHomeSettings(ConversationState c, QuotaHomeAccount home, bool crossProvider, string? modelBefore, string? effortBefore)
    {
        if (AccountSwitchRules.RestoresHomeModel(home, crossProvider, modelBefore, effortBefore))
        {
            var models = ModelOptions(c.Meta.AccountId, c.Meta.Provider).Select(m => m.Value).ToList();
            if (AccountSwitchRules.SupportsModel(models, home.Model))
            {
                c.Meta.Model = home.Model;
                c.Meta.Effort = home.Effort;
            }
        }
        if (crossProvider)
        {
            c.Meta.PermissionMode = AgentPermissionModes.Normalize(c.Meta.Provider, home.PermissionMode);
            c.Meta.Agent = home.Agent;
            c.Meta.OutputStyle = home.OutputStyle;
        }
    }
}
