using Myriagent.Accounts;
using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 中樞→同步協調者的掛鉤（UI 執行緒；只用中樞、帳戶與 Newtonsoft 的型別：AgentHub 在未配對的路徑上也持有它，S6）。
/// 伺服器的值經協調者以 AccountManager／AgentHub 的套用方法寫回，不經 HandleCommand，所以這些掛鉤不會回音
/// </summary>
internal interface ISyncBridge
{
    /// <summary>sync*、deviceRename；處理了回 true（只收桌面本機 WebView 的 view，鏡像送來的一律不理）</summary>
    bool HandleCommand(IAgentView view, string type, JObject msg);

    /// <summary>處理完 accountUpdate 之後；讀 AccountManager 套用後的值</summary>
    void OnAccountUpdated(string accountId);

    /// <summary>處理完 accountReorder 之後；讀 AccountManager 的新順序</summary>
    void OnAccountsReordered();

    /// <summary>漫遊偏好（theme、fontSize、enterToSend、notifications）在這台被使用者改了（值是改後的；fontSize 是 JSON 整數）</summary>
    void OnRoamingSettingChanged(string key, JToken value);

    /// <summary>init.remote：未配對、也沒在配對時 null（還在啟動時也是 null，看 <see cref="IsStarting"/>）</summary>
    JObject? InitRemote();

    /// <summary>init.syncStarting：延後啟動還沒讀完配對資料（這段期間 init.remote 是 null，但不一定是未配對）</summary>
    bool IsStarting { get; }

    /// <summary>init 與 cli 訊息的 devicesCli：未配對時 null</summary>
    JObject? DevicesCli();

    /// <summary>accounts 的每一筆加上〔同步〕欄位；未配對時什麼都不做</summary>
    void DecorateAccounts(JArray accounts);

    /// <summary>
    /// 「在這台登入」（accountAdd {logicalAccountId}，WP3.4）：套 AccountIdentityRules.CheckCatalogClaim（格式、已配對、目錄有、種類、
    /// 這台還沒連結、還沒有別的本機帳戶宣告），通過時回目錄的名稱、顏色與預期信箱；不合格回 null，failure 是 DescribeClaimFailure 的文字
    /// </summary>
    CatalogAccountTemplate? ClaimCatalogAccount(string? logicalAccountId, out string failure);

    /// <summary>本機帳戶對應的邏輯帳戶（目錄的連結優先，其次是宣告）在目錄裡的 ExpectedEmail；未配對、沒有對應、目錄沒有信箱回 null</summary>
    string? ExpectedEmail(string localAccountId);

    /// <summary>
    /// 遠端登入（WP4.8 D21：accountLoginDevice 給 <paramref name="logicalAccountId"/>、accountLogin 給 <paramref name="localAccountId"/>，恰好給一個）
    /// 要登入的本機帳戶與目錄的預期身分：邏輯帳戶先找這台的連結、其次是宣告（清單裡第一個），都沒有就給建立的範本；本機帳戶找它對應的邏輯帳戶，
    /// 目錄沒有這一筆時預期身分是 None。只看目錄的快照與帳戶清單。預設實作（測試替身）回 <see cref="RemoteLoginLookup.NotPaired"/>
    /// </summary>
    RemoteLoginLookup FindRemoteLoginTarget(string? localAccountId, string? logicalAccountId) => RemoteLoginLookup.NotPaired;

    /// <summary>
    /// 其他電腦的對話（副本）用的帳戶：擁有者電腦 <paramref name="ownerDeviceId"/> 上的本機帳戶 <paramref name="ownerAccountId"/> 連結到的邏輯帳戶
    /// （目錄的 account.upsert 的 links；標頭的帳戶晶片與換帳戶選單用）。未配對、目錄還沒到、沒有連結（或測試替身）回 null
    /// </summary>
    string? ReplicaLogicalAccount(string? ownerDeviceId, string? ownerAccountId) => null;

    /// <summary>電腦狀態面板（stat1）的這台：已配對時是這台的 deviceId，未配對（或測試替身）是 null</summary>
    string? StatsDeviceId => null;

    /// <summary>這台有沒有視窗開著電腦狀態面板（值變了才呼叫）：同步引擎據此決定要不要收其他電腦的狀態、多久送一次</summary>
    void OnMonitorWatchChanged(bool watching)
    {
    }

    /// <summary>側欄的 token 計數變了（50 毫秒合併一次）：同步引擎據此排一次上傳（tok1，sync-protocol 14.9）</summary>
    void OnTokensChanged()
    {
    }

    /// <summary>這台排程頁的摘要變了（中樞讀完排程、內容不同時）：同步引擎據此排一次上傳（sched1，sync-protocol 14.12）</summary>
    void OnSchedulesChanged()
    {
    }

    /// <summary>這台某個對話的輸入框草稿變了（線上 id；空字串＝清掉）：同步引擎據此排著送給伺服器（draft1，sync-protocol 14.17）</summary>
    void OnComposerDraft(string conversationId, string text)
    {
    }

    /// <summary>
    /// 替擁有者送協作人的管理指令（多人協作，collab1，sync-protocol 14.14）：經目前的連線交給伺服器。未配對、沒有連線、伺服器太舊（或測試替身）回 unavailable
    /// </summary>
    Task<CollabCmdResult> CollabCmdAsync(CollabCmdRequest request)
        => Task.FromResult(new CollabCmdResult(false, CollaboratorCodes.Unavailable));

    /// <summary>
    /// tokens（web-protocol 的 Tokens）加上其他電腦的計數（tok2，sync-protocol 14.10；<c>now</c> 決定這台的本機日期）：
    /// 未配對、還沒收到過其他電腦的計數（或測試替身）時什麼都不做
    /// </summary>
    void DecorateTokens(JObject tokens, DateTime now)
    {
    }
}
