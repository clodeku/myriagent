using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 引擎→宿主（協調者）：引擎改中樞的唯一入口是 <see cref="RunOnUiAsync"/>（內部用 IAgentHost.BeginInvoke；正式環境一律 Post）。
/// 狀態與用量快照以 Volatile 發布，UI 執行緒自己讀。
/// </summary>
internal interface ISyncHostPort
{
    /// <summary>
    /// 在 UI 執行緒執行並等它完成。TaskCompletionSource 用 RunContinuationsAsynchronously（引擎的接續不在 UI 執行緒上跑）；
    /// UI 端先檢查已釋放；取消時立即結束（app 結束時 BeginInvoke 排的工作不會執行）
    /// </summary>
    Task RunOnUiAsync(Action action, CancellationToken ct);

    /// <summary>引擎已經節流過（<see cref="SyncStatusRules.Decide"/>），收到就送</summary>
    void PublishStatus(SyncStatusSnapshot status);

    /// <summary>用量查詢閘門的快照（UI 執行緒以 Volatile 讀，IUsagePollPolicy）</summary>
    void PublishPollSnapshot(UsagePollSnapshot snapshot);

    /// <summary>排一個寫下 UI 存活時間的小工作到 UI 執行緒，不等它（心跳的 uiAliveAt）</summary>
    void PingUi();

    /// <summary>UI 執行緒最後一次跑到 PingUi 的時間（Unix 毫秒；Volatile 讀）</summary>
    long UiAliveAt { get; }
}

/// <summary>拉到的共享用量要套用到哪個本機帳戶（歷史點的時間是伺服器收到的時間；ReplaceCurrent＝也換掉目前值）</summary>
internal sealed record SharedUsageApply(string LocalAccountId, UsageSnapshot Snapshot, DateTimeOffset PointTime, bool ReplaceCurrent);

/// <summary>
/// 目錄有、這台沒有連結的邏輯帳戶最新的共享用量（remoteOnly 卡片的 usage；WP3.4 D15：協調者只放記憶體）。
/// Usage 已照 UsageSyncRules.ForRemoteOnly 驗證並清理
/// </summary>
internal sealed record RemoteUsageApply(string LogicalAccountId, SharedUsage Usage);

/// <summary>
/// 一頁拉取（或啟動時的快取）要在 UI 執行緒套用的目錄結果：檢視（devices、AccountView 的同步欄位）、
/// 本機帳戶的名稱與顏色、順序（只有目錄的帳戶有變時才有）、漫遊偏好
/// </summary>
internal sealed record CatalogApply(
    SyncCatalogView View,
    IReadOnlyList<(string LocalAccountId, string Label, string Color)> Appearance,
    IReadOnlyList<string>? LocalOrder,
    IReadOnlyList<(string Key, JToken Value)> Prefs);

/// <summary>在 UI 執行緒套用伺服器的結果（協調者實作；值經 AccountManager／AgentHub 的套用方法，不經 HandleCommand，不會回送）</summary>
internal interface ISyncUiApplier
{
    void ApplyCatalog(CatalogApply apply);

    void ApplySharedUsage(IReadOnlyList<SharedUsageApply> items);

    /// <summary>
    /// 這台沒有連結的邏輯帳戶的共享用量（每個邏輯帳戶一筆，seq 最大的）。reset＝伺服器換了實例：先清掉之前收的再套用
    /// （換實例後第一次套用一定帶 reset，items 可能是空的）
    /// </summary>
    void ApplyRemoteUsage(IReadOnlyList<RemoteUsageApply> items, bool reset);
}
