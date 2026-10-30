using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 同步引擎的對外介面（只用 BCL、Protocol 與桌面型別：協調者在未配對的路徑上也持有這個型別，S6）。
/// 所有方法都執行緒安全：只把工作排進引擎自己的執行緒（MyriagentSync），立即返回。
/// </summary>
internal interface ISyncEngine : IAsyncDisposable
{
    /// <summary>開執行緒；sync.db 在引擎執行緒上開啟（不在 UI 執行緒做 I/O）。只有第一次有效</summary>
    void Start();

    /// <summary>UI 執行緒→引擎的訊號（快照類只留最新、修改類照順序，<see cref="SyncInbox"/>）</summary>
    void Post(SyncSignal signal);

    /// <summary>
    /// 串流中的項目（第 4 階段 <c>live1</c>，sync-protocol 14.3；WP4.3 的對話接收者在 UI 執行緒合併後呼叫）：可以丟，不經收件匣、不寫 outbox。
    /// WP4.1 沒有實作（引擎的 OnLiveRelay 登記點），呼叫了也不會送出
    /// </summary>
    void PostLive(LiveRelay relay);

    void SetPaused(bool paused);

    /// <summary>退避中立刻重試（網路恢復、從睡眠喚醒、syncRetry）；停止、待確認時不理；不早於 ServerNotice{shutdown} 指定的時間</summary>
    void RetryNow();

    /// <summary>等於候選的 16 字指紋才改釘並重連；不符就忽略</summary>
    void ConfirmHubIdentity(string fingerprint);

    /// <summary>存下並重連（新的 Hello 才帶得到）</summary>
    void UpdatePolicy(DevicePolicy policy);

    /// <summary>正常停止：關連線、關 sync.db；逾時就取消</summary>
    Task StopAsync(TimeSpan timeout);

    /// <summary>只取消、不等（app 立即結束時）</summary>
    void Cancel();

    /// <summary>這個引擎送出成功的心跳數（實機探針量閒置 CPU 時確認取樣窗裡真的有心跳；任何執行緒都可以讀）</summary>
    long HeartbeatsSent { get; }
}

/// <summary>UI 執行緒交給引擎的訊號</summary>
internal abstract record SyncSignal
{
    // ── 快照類：只留最新（SyncInbox 合併） ──

    /// <summary>本機帳戶清單的摘要（<paramref name="ListReadOnly"/>＝accounts.json 讀不到，不送 link.removed）</summary>
    public sealed record Accounts(IReadOnlyList<LocalAccountSnapshot> Snapshot, bool ListReadOnly) : SyncSignal;

    public sealed record Cli(CliStatePayload State) : SyncSignal;

    /// <summary>app 的更新狀態（app 自我更新 U3 的 app.state）</summary>
    public sealed record App(AppStatePayload State) : SyncSignal;

    /// <summary>某個帳戶本機的用量（每個帳戶留最新）</summary>
    public sealed record UsageObserved(string LocalAccountId, UsageSnapshot Snapshot) : SyncSignal;

    // ── 修改類：不丟、照順序 ──

    /// <summary>使用者改了帳戶名稱或顏色（AccountManager 套用後的值）</summary>
    public sealed record AccountEdited(string LocalAccountId, string Label, string Color) : SyncSignal;

    public sealed record AccountsReordered(IReadOnlyList<string> LocalOrder) : SyncSignal;

    public sealed record PrefChanged(string Key, JToken Value) : SyncSignal;

    public sealed record DeviceRenamed(string DeviceId, string Name) : SyncSignal;

    /// <summary>直接寫進 outbox（測試與 fake-device 的積壓）</summary>
    public sealed record Append(OutboxAppend Item) : SyncSignal;

    /// <summary>清掉 published 摘要：下一次帳戶與 CLI 快照全部重送</summary>
    public sealed record FullResync : SyncSignal;
}
