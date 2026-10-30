using Myriagent.Usage;

namespace Myriagent.Sync;

/// <summary>
/// UI 執行緒→引擎的橋（只在 UI 執行緒呼叫，不加鎖）：帳戶清單與 CLI 狀態和上次送的相同就不送（鏡像每 500 毫秒可能收到一樣的卡片），
/// 用量與修改類照收照送。收件匣（<see cref="SyncInbox"/>）再把快照類合併成最新的一份，所以這裡只擋完全相同的內容。
/// 未配對時不建立（S6：這個型別只碰引擎的介面）
/// </summary>
internal sealed class HubBridge
{
    private readonly ISyncEngine engine;
    private IReadOnlyList<LocalAccountSnapshot>? lastAccounts;
    private bool lastAccountsReadOnly;
    private CliStatePayload? lastCli;
    private AppStatePayload? lastApp;

    public HubBridge(ISyncEngine engine) => this.engine = engine;

    /// <summary>本機帳戶清單的摘要（鏡像從 accounts 訊息組成）；和上次的每一筆都相同、唯讀狀態也相同就不送</summary>
    public void OfferAccounts(IReadOnlyList<LocalAccountSnapshot> snapshot, bool listReadOnly)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (lastAccounts is not null && lastAccountsReadOnly == listReadOnly && lastAccounts.SequenceEqual(snapshot))
            return;
        lastAccounts = snapshot.ToList();
        lastAccountsReadOnly = listReadOnly;
        engine.Post(new SyncSignal.Accounts(lastAccounts, listReadOnly));
    }

    /// <summary>CLI 狀態（只含 Claude、Codex）：和上次相同就不送</summary>
    public void OfferCli(CliStatePayload state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (CliStateRules.Same(lastCli, state))
            return;
        lastCli = state;
        engine.Post(new SyncSignal.Cli(state));
    }

    /// <summary>app 的更新狀態（app 自我更新 U3）：和上次相同就不送（下載進度每次都會觸發，內容不含進度）</summary>
    public void OfferApp(AppStatePayload state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state == lastApp)
            return;
        lastApp = state;
        engine.Post(new SyncSignal.App(state));
    }

    /// <summary>某個帳戶本機的用量（鏡像只在 fetchedAt 改變時呼叫；要不要上傳由引擎的 UsageSyncRules.Decide 決定）</summary>
    public void OfferUsage(string localAccountId, UsageSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrEmpty(localAccountId);
        ArgumentNullException.ThrowIfNull(snapshot);
        engine.Post(new SyncSignal.UsageObserved(localAccountId, snapshot));
    }

    /// <summary>使用者在這台的修改（改名稱或顏色、重排、漫遊偏好、裝置改名）：不丟、照順序。快照類要走 Offer 的方法</summary>
    public void Edit(SyncSignal edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        switch (edit)
        {
            case SyncSignal.AccountEdited or SyncSignal.AccountsReordered or SyncSignal.PrefChanged or SyncSignal.DeviceRenamed:
                engine.Post(edit);
                break;
            default:
                throw new ArgumentException($"不是修改類的同步訊號：{edit.GetType().Name}", nameof(edit));
        }
    }
}
