using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 額度全用完時等重置後自動接著做（QuotaWaitRules；web-protocol 的 Header.quotaWait、quotaWaitCancel、設定 autoResumeAfterQuota）。
/// 等待記在 meta 的 QuotaWait（app 重開、換版之後照樣等）；開始任何一輪（使用者送出、接續）就不等了
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>從使用者上一次送出起，等額度重置後自動接著做了幾次（只在記憶體）</summary>
        public int QuotaResumeAttempts;
    }

    /// <summary>這一輪因為額度用完結束、沒有帳戶可以接手：要等多久（CompleteTurn 呼叫；設定關著、副本、草稿回 null）</summary>
    private DateTimeOffset? PlanQuotaWait(ConversationState c, DateTimeOffset? resetsAt)
    {
        if (!settings.AutoResumeAfterQuotaReset || c.IsDraft || c.IsReplica)
            return null;
        return QuotaWaitRules.Plan(resetsAt, AccountSwitchClock(), c.QuotaResumeAttempts);
    }

    /// <summary>記下等待並加一則提示（CompleteTurn 收尾時，項目順序在錯誤與提示之後）</summary>
    private void ScheduleQuotaWait(ConversationState c, DateTimeOffset until)
    {
        c.Meta.QuotaWait = new QuotaWait(c.Meta.AccountId, until);
        Log.Info($"對話 {c.Meta.Id} 額度用完、沒有帳戶可以接手：等到 {until:O} 自動接著做（第 {c.QuotaResumeAttempts + 1} 次）");
        AddItem(c, new AgentChatItem
        {
            Kind = AgentItemKind.Notice,
            Text = QuotaWaitRules.ScheduledNotice(accounts.Find(c.Meta.AccountId)?.Label, until, AccountSwitchClock()),
            NoticeKind = AgentNoticeKind.AccountSwitch,
        });
        MarkHeaderDirty(c);
    }

    /// <summary>開始一輪（HookTurnStarted）或換了帳戶：不再等</summary>
    private void ForgetQuotaWait(ConversationState c)
    {
        if (c.Meta.QuotaWait == null)
            return;
        c.Meta.QuotaWait = null;
        MarkHeaderDirty(c);
    }

    /// <summary>取消等待（quotaWaitCancel）</summary>
    private void CancelQuotaWait(ConversationState? c)
    {
        if (c?.Meta.QuotaWait == null)
            return;
        ForgetQuotaWait(c);
        AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = QuotaWaitRules.CancelledNotice });
        SaveConversation(c, saveItems: true);
    }

    /// <summary>
    /// 維護（每 30 秒）：等著額度重置的閒置對話。設定關了、帳戶換了就不等；等待期間有別的帳戶可以接手（設定「額度用完時自動換帳戶」開著）就換過去；
    /// 時間到了就以同一個帳戶隱藏送出接續訊息（等你回應、換版、背景工作、Remote Control 有人在用時下次再看）
    /// </summary>
    private void TickQuotaWait()
    {
        foreach (var c in conversations.Values.Where(c => c.Meta.QuotaWait != null).ToList())
        {
            var wait = c.Meta.QuotaWait!;
            if (!settings.AutoResumeAfterQuotaReset || c.IsReplica || accounts.Find(wait.AccountId) == null || c.Meta.AccountId != wait.AccountId)
            {
                ForgetQuotaWait(c);
                SaveConversation(c, saveItems: false);
                continue;
            }
            if (c.Running || IsRestartInFlight(c) || c.PendingAccountSwitch != null)
                continue;
            var now = AccountSwitchClock();
            // 等待期間別的帳戶恢復了：照自動換帳戶換過去（換過去就是一輪新的開始，HookTurnStarted 清掉等待）
            if (now < wait.Until)
            {
                if (settings.AutoSwitchAccountOnLimit && PlanIdleAccountSwitch(c, wait) is { } plan)
                {
                    ForgetQuotaWait(c);
                    ContinueOnAnotherAccount(c, plan);
                }
                continue;
            }
            var items = EnsureItems(c, notifyUnreadable: false);
            if (!c.ItemsLoaded || items.Any(i => i.Status == AgentItemStatus.Waiting) || (c.Session?.HasLiveBackgroundWork ?? false))
                continue;
            ForgetQuotaWait(c);
            c.QuotaResumeAttempts++;
            Log.Info($"對話 {c.Meta.Id} 的帳戶額度已重置：自動接著做（第 {c.QuotaResumeAttempts} 次）");
            var label = accounts.Find(c.Meta.AccountId)?.Label;
            _ = SendCoreAsync(c, QuotaWaitRules.BuildContinuation(AccountSwitchRules.RecentSteps(items)), new List<AgentAttachment>(),
                hiddenNotice: QuotaWaitRules.ResumedNotice(label), hiddenNoticeKind: AgentNoticeKind.AccountSwitch, rememberLocal: false, returnHome: false);
        }
    }

    /// <summary>等待期間：有沒有別的帳戶可以接手（同 PlanAccountSwitch 的挑法；挑不到回 null）</summary>
    private AccountSwitchPlan? PlanIdleAccountSwitch(ConversationState c, QuotaWait wait)
    {
        if (!AccountSwitchRules.Supports(c.Meta.Provider))
            return null;
        var now = AccountSwitchClock();
        var candidates = accounts.Accounts.Select(a => new AccountSwitchCandidate(
            a.Id,
            a.Provider,
            accounts.GetState(a.Id).Status,
            settings.Models.TryGetValue(a.Id, out var models) ? models.Select(m => m.Value).ToList() : null,
            accounts.GetUsage(a.Id)?.Windows,
            a.Order));
        var choice = AccountSwitchRules.Choose(c.Meta.Provider, c.Meta.AccountId, c.Meta.Model, candidates, exhaustedAccounts, now, settings.AutoSwitchCrossProviderOnLimit);
        return choice != null && accounts.Find(choice.AccountId) is { } account
            ? new AccountSwitchPlan(account, wait.Until - QuotaWaitRules.Grace, choice.ResetsAt, null)
            : null;
    }

    /// <summary>標頭的 quotaWait：{until, account}；沒有等待是 null</summary>
    private JObject? RenderQuotaWait(ConversationState c)
        => c.Meta.QuotaWait is { } wait && !c.IsReplica
            ? new JObject { ["until"] = wait.Until.ToUnixTimeMilliseconds(), ["account"] = accounts.Find(wait.AccountId)?.Label }
            : null;
}
