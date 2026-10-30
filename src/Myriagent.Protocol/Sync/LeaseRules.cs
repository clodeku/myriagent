using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Sync;

/// <summary>伺服器交接狀態機的事件（<see cref="LeaseRules.Transition"/>；計畫 7.1）</summary>
internal enum HandoffEvent
{
    /// <summary>A 回 <c>HandoffProgress{accepted}</c></summary>
    SourceAccepted,

    /// <summary>15 秒內 A 沒有接受（<see cref="LeaseRules.AcceptTimeoutSeconds"/>）</summary>
    AcceptTimeout,

    /// <summary>A 回 <c>HandoffProgress{draining}</c></summary>
    SourceDraining,

    /// <summary>排空超過期限（afterTurn 30 分、now 3 分；<see cref="LeaseRules.DeadlineFor"/>）</summary>
    DeadlineReached,

    /// <summary>任何發起端的 <c>HandoffCancel</c></summary>
    Cancel,

    /// <summary>A 回 <c>HandoffProgress{aborted}</c>（帶 A 的代碼）</summary>
    SourceAborted,

    /// <summary>A 送了 <c>HandoffReady</c></summary>
    Ready,

    /// <summary>ready 的驗證通過（事件收齊、日誌相同、掃描無誤）</summary>
    VerifyPassed,

    /// <summary>ready 的驗證失敗（events_missing、log_mismatch、log_damaged）</summary>
    VerifyFailed,

    /// <summary>伺服器啟動時發現這筆還在進行中（D17）</summary>
    ServerRestart,
}

/// <summary>
/// <see cref="LeaseRules.CheckStart"/> 的輸入：發起交接當下、伺服器在寫入交易裡讀到的快照（計畫 7.1「HandoffStart 的檢查」）。
/// 「在線」一律以 <see cref="LeaseRules.IsOrphaned"/> 判斷（伺服器的 HandoffPresence 算好再填）
/// </summary>
/// <param name="Provider"><c>conversations.provider</c>（線上名稱）</param>
/// <param name="SyncPolicy"><c>conversations.sync_policy</c></param>
/// <param name="HasActiveHandoff">這個對話已有進行中的交接（<see cref="HandoffStates.IsActive"/>）</param>
/// <param name="TargetOnline">目標電腦有已證明的連線，而且 UI 沒有卡住（不是失聯）</param>
/// <param name="TargetHasHand1">目標電腦目前的連線協商出 hand1</param>
/// <param name="TargetHasAccount">目標電腦有這個對話的邏輯帳戶 Ready 的連結（<c>account_links</c>）</param>
/// <param name="TargetAllowsControl">目標電腦 Hello 帶的政策是 Control、遠端操作沒有暫停</param>
/// <param name="InitiatedByTarget">發起者就是目標電腦（自己要接手，不查自己的政策）</param>
/// <param name="Force">強制接手</param>
/// <param name="OwnerOrphaned">擁有者失聯（<see cref="LeaseRules.IsOrphaned"/>）</param>
/// <param name="OwnerHasHand1">擁有者目前的連線協商出 hand1</param>
/// <param name="OwnerAllowsControl">擁有者 Hello 帶的政策是 Control、遠端操作沒有暫停</param>
/// <param name="InitiatedByOwner">發起者就是擁有者電腦</param>
internal sealed record HandoffStartSnapshot(
    bool ConversationExists,
    bool Deleted,
    string? Provider,
    string? SyncPolicy,
    bool HasActiveHandoff,
    string? OwnerDeviceId,
    string ToDeviceId,
    bool TargetOnline,
    bool TargetHasHand1,
    bool TargetHasAccount,
    bool TargetAllowsControl,
    bool InitiatedByTarget,
    bool Force,
    bool OwnerOrphaned,
    bool OwnerHasHand1,
    bool OwnerAllowsControl,
    bool InitiatedByOwner);

/// <summary>
/// 租約與交接的純函式規則（第 5 階段計畫 5.4、7.1、D7；伺服器、桌面、FakeDevice 的劇本共用）。擁有者與 epoch 只由伺服器的交接服務改（D3），
/// 這裡只回答「可不可以」與「下一個狀態」；時間一律是 Unix 毫秒、由呼叫端給（伺服器用 <c>TimeProvider</c>）
/// </summary>
internal static class LeaseRules
{
    /// <summary>沒有已證明的連線滿這麼久就算失聯（同 <see cref="SyncProtocol.OfflineAfterSeconds"/>）</summary>
    public const int OfflineAfterSeconds = SyncProtocol.OfflineAfterSeconds;

    /// <summary>連線在但 UI 卡住（心跳的 <c>uiAliveAt</c>）超過這麼久也算失聯</summary>
    public const int UiStaleAfterSeconds = 120;

    /// <summary>伺服器重啟寬限（D7）：重啟前還在線的裝置，啟動後這段時間內不算失聯（每次部署都會重啟，桌面退避上限 16 秒、部署的 shutdown 通知預設 30 秒）</summary>
    public const int RestartGraceSeconds = 180;

    /// <summary>「重啟前還在線」：持久的最後心跳不早於啟動時間減這麼久</summary>
    public const int RestartOnlineWindowSeconds = 120;

    /// <summary>A 收到 <c>HandoffRequest</c> 之後要在這段時間內接受</summary>
    public const int AcceptTimeoutSeconds = 15;

    /// <summary>afterTurn 的排空期限（D18：固定 30 分鐘）</summary>
    public const int AfterTurnDeadlineMinutes = 30;

    /// <summary>now 的排空期限</summary>
    public const int NowDeadlineMinutes = 3;

    /// <summary>
    /// 擁有者失聯了嗎（可以強制接手；D7）：
    /// <list type="number">
    /// <item>被撤銷的裝置立即算失聯。</item>
    /// <item>有已證明的連線：UI 的最後活動（<paramref name="uiAliveAt"/>）超過 <see cref="UiStaleAfterSeconds"/> 秒才算（還沒收到心跳的 null 不算）。</item>
    /// <item>沒有連線：重啟前還在線（<paramref name="lastSeenAt"/> ≥ 啟動時間 − <see cref="RestartOnlineWindowSeconds"/> 秒）而且還在重啟寬限
    /// （<paramref name="now"/> &lt; 啟動時間 ＋ <see cref="RestartGraceSeconds"/> 秒）時不算；否則從沒見過（null）或最後一次有連線滿
    /// <see cref="OfflineAfterSeconds"/> 秒就算。</item>
    /// </list>
    /// </summary>
    /// <param name="connected">現在有已證明的連線（ConnectionRegistry）</param>
    /// <param name="lastSeenAt">最後一次有已證明連線的時間；啟動之前的以持久的 <c>device_streams.last_heartbeat_at</c> 代替</param>
    /// <param name="uiAliveAt">最後一次心跳回報的 UI 活動時間</param>
    /// <param name="serverStartedAt">這次伺服器程序啟動的時間</param>
    /// <param name="revoked">裝置已被撤銷</param>
    public static bool IsOrphaned(long now, bool connected, long? lastSeenAt, long? uiAliveAt, long serverStartedAt, bool revoked)
    {
        if (revoked)
            return true;
        if (connected)
            return uiAliveAt is { } ui && now - ui > UiStaleAfterSeconds * 1000L;
        if (lastSeenAt is not { } seen)
            return true;
        var onlineBeforeRestart = seen >= serverStartedAt - RestartOnlineWindowSeconds * 1000L;
        if (onlineBeforeRestart && now < serverStartedAt + RestartGraceSeconds * 1000L)
            return false;
        return now - seen >= OfflineAfterSeconds * 1000L;
    }

    /// <summary>
    /// 伺服器以 <c>conversations.provider</c>（線上名稱，大小寫完全相同才算）判斷這種代理的對話能不能換電腦（D20 的第二道；
    /// 規則本身是 <see cref="AgentHubRules.SupportsHandoff"/>）。不認得的名稱一律不能
    /// </summary>
    public static bool SupportsHandoff(string? providerWireName)
    {
        foreach (var provider in AgentProviders.All)
        {
            if (string.Equals(provider.ToString(), providerWireName, StringComparison.Ordinal))
                return AgentHubRules.SupportsHandoff(provider);
        }

        return false;
    }

    /// <summary>
    /// <c>HandoffStart</c> 的檢查（計畫 7.1，依序、第一個不符就回 <see cref="HandoffCodes"/>；都通過回 null）：對話存在（not_found）、沒刪除（deleted）、
    /// 代理種類能換（unsupported_provider）、同步政策是 full（not_full）、沒有進行中的交接（busy；強制接手不擋，進行中的那筆由交接服務中止）、
    /// 目標不是擁有者（same_device）、目標在線（target_offline）而且有 hand1（target_outdated）、目標有這個邏輯帳戶 Ready 的連結（no_account）、
    /// 不是目標自己發起時目標允許遠端操作（policy）；最後是擁有者：一般交接要擁有者在線（owner_offline）、有 hand1（target_outdated）、
    /// 不是擁有者自己發起時擁有者允許遠端操作（policy）；強制接手要擁有者失聯（owner_online），不問擁有者的政策。
    /// 「改成立即」（同一筆進行中的 afterTurn 改 now）不經這裡，由交接服務在檢查之前處理
    /// </summary>
    public static string? CheckStart(HandoffStartSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!s.ConversationExists)
            return HandoffCodes.NotFound;
        if (s.Deleted)
            return HandoffCodes.Deleted;
        if (!SupportsHandoff(s.Provider))
            return HandoffCodes.UnsupportedProvider;
        if (ConversationSyncPolicies.Interpret(s.SyncPolicy) != ConversationSyncPolicies.Full)
            return HandoffCodes.NotFull;
        if (s.HasActiveHandoff && !s.Force)
            return HandoffCodes.Busy;
        if (string.Equals(s.ToDeviceId, s.OwnerDeviceId, StringComparison.Ordinal))
            return HandoffCodes.SameDevice;
        if (!s.TargetOnline)
            return HandoffCodes.TargetOffline;
        if (!s.TargetHasHand1)
            return HandoffCodes.TargetOutdated;
        if (!s.TargetHasAccount)
            return HandoffCodes.NoAccount;
        if (!s.InitiatedByTarget && !s.TargetAllowsControl)
            return HandoffCodes.Policy;
        if (s.Force)
            return s.OwnerOrphaned ? null : HandoffCodes.OwnerOnline;
        if (s.OwnerOrphaned)
            return HandoffCodes.OwnerOffline;
        if (!s.OwnerHasHand1)
            return HandoffCodes.TargetOutdated;
        if (!s.InitiatedByOwner && !s.OwnerAllowsControl)
            return HandoffCodes.Policy;
        return null;
    }

    /// <summary>
    /// 伺服器狀態機（計畫 7.1）：目前狀態遇到這個事件的下一個狀態；這個狀態不理這個事件（重送、遲到、已結束）回 null（呼叫端什麼都不做）。
    /// <list type="bullet">
    /// <item>requested：A 接受→accepted；15 秒沒接受或期限到→expired。</item>
    /// <item>accepted：A 排空→draining；期限到→expired。draining：A 的 HandoffReady→ready；期限到→expired。</item>
    /// <item>ready：驗證通過→committed、失敗→aborted；伺服器重啟仍是 ready（重新驗證再提交，D17）。</item>
    /// <item>進行中（requested、accepted、draining、ready）：取消→cancelled、A 中止→aborted；requested～draining 遇到伺服器重啟→aborted（server_restart）。</item>
    /// <item>committed、cancelled、aborted、expired：不再變（committed 之後只更新接手端的 import_state）。強制接手不經這張表，直接建 committed 的列。</item>
    /// </list>
    /// </summary>
    public static string? Transition(string? state, HandoffEvent e) => (state, e) switch
    {
        (HandoffStates.Requested, HandoffEvent.SourceAccepted) => HandoffStates.Accepted,
        (HandoffStates.Requested, HandoffEvent.AcceptTimeout or HandoffEvent.DeadlineReached) => HandoffStates.Expired,
        (HandoffStates.Accepted, HandoffEvent.SourceDraining) => HandoffStates.Draining,
        (HandoffStates.Accepted or HandoffStates.Draining, HandoffEvent.DeadlineReached) => HandoffStates.Expired,
        (HandoffStates.Draining, HandoffEvent.Ready) => HandoffStates.Ready,
        (HandoffStates.Ready, HandoffEvent.VerifyPassed) => HandoffStates.Committed,
        (HandoffStates.Ready, HandoffEvent.VerifyFailed) => HandoffStates.Aborted,
        (HandoffStates.Ready, HandoffEvent.ServerRestart) => HandoffStates.Ready,
        (HandoffStates.Requested or HandoffStates.Accepted or HandoffStates.Draining, HandoffEvent.ServerRestart) => HandoffStates.Aborted,
        (HandoffStates.Requested or HandoffStates.Accepted or HandoffStates.Draining or HandoffStates.Ready, HandoffEvent.Cancel) => HandoffStates.Cancelled,
        (HandoffStates.Requested or HandoffStates.Accepted or HandoffStates.Draining or HandoffStates.Ready, HandoffEvent.SourceAborted) => HandoffStates.Aborted,
        _ => null,
    };

    /// <summary>排空的期限（<see cref="HandoffModes"/>；不認得的模式丟例外）</summary>
    public static long DeadlineFor(string mode, long requestedAt) => mode switch
    {
        HandoffModes.AfterTurn => requestedAt + AfterTurnDeadlineMinutes * 60_000L,
        HandoffModes.Now => requestedAt + NowDeadlineMinutes * 60_000L,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "不認得的交接模式"),
    };

    /// <summary>A 要在這個時間之前接受（<see cref="AcceptTimeoutSeconds"/>）</summary>
    public static long AcceptDeadline(long requestedAt) => requestedAt + AcceptTimeoutSeconds * 1000L;

    /// <summary>
    /// 伺服器還原到交接之前的備份後的認領（計畫 5.4 第 2 點）：列是 recovering、事件是 <c>conv.created</c>、作者不是列上的擁有者，
    /// 而且事件的 epoch 比列上的大（交接後的新擁有者；還原把擁有者倒回去了）。成立時伺服器在同一個寫入裡把擁有者與 epoch 改成事件的值、
    /// 再寫一筆 <c>conv.lease{reason: restore}</c>。epoch 相同而作者不同照舊 not_owner，epoch 較小在這之前就是 fenced（D4）
    /// </summary>
    public static bool IsRecoveringClaim(bool recovering, string? kind, string? ownerDeviceId, long rowEpoch, string? author, long eventEpoch)
        => recovering
           && kind == ConversationEventKinds.Created
           && author is not null
           && !string.Equals(author, ownerDeviceId, StringComparison.Ordinal)
           && eventEpoch > rowEpoch;
}
