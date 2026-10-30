using Myriagent.Agents;

namespace Myriagent.Hub;

// 其他電腦的對話（唯讀複本，WP4.3 計畫步 14、15，D26、D27）在引擎與中樞之間交換的樣子。引擎（Sync/SyncEngine.Replicas.cs）從 sync.db 的
// replica_meta／replica_items 與拉到的事件組出來，經 IConversationUiPort.ApplyReplicaDelta 在 UI 執行緒交給中樞（AgentHub.Replica.cs）。
// 內容都是新建的物件，交出去之後引擎不再碰。不參考 SignalR、SQLite 或引擎的實作型別（中樞與發布者在未配對的路徑上也看得到，S6）

/// <summary>一個複本的中繼資料</summary>
/// <param name="ConversationId">線上的對話 id（小寫 32 個十六進位）</param>
/// <param name="OwnerDeviceId">擁有（執行）這個對話的電腦</param>
/// <param name="Meta">擁有者發布的中繼資料轉成的對話：沒有本機帳戶、工作資料夾與 CLI 的 session</param>
/// <param name="Running">擁有者那邊執行中</param>
/// <param name="Waiting">有等待使用者回應的卡片</param>
/// <param name="ItemsAvailable">政策是 full（有同步項目）；metadata、paused 只有標題與狀態</param>
/// <param name="CustomFolder">擁有者那邊是自訂工作資料夾（false＝那個對話自己的 workspace）</param>
/// <param name="Folder">擁有者電腦上的自訂工作資料夾（只供顯示，不當成這台的路徑；不是自訂、或擁有者沒送路徑時 null）</param>
/// <param name="Models">擁有者那邊這個對話的帳戶可選的模型（標頭的模型選單；從這台改那台的模型時照它列）；擁有者沒送（舊版）時 null</param>
/// <param name="OwnerAccountId">
/// 擁有者電腦上執行這個對話的本機帳戶 id（中繼資料的 <c>localAccountId</c>；只在那台有意義，中樞經同步橋換成邏輯帳戶給標頭的帳戶晶片）；帳戶不明時 null
/// </param>
internal sealed record ReplicaMeta(string ConversationId, string OwnerDeviceId, AgentConversation Meta, bool Running, bool Waiting, bool ItemsAvailable,
    bool CustomFolder = false, string? Folder = null, IReadOnlyList<AgentModelOption>? Models = null, string? OwnerAccountId = null);

/// <summary>一個複本的項目變化（依伺服器 seq 套用之後的結果）</summary>
/// <param name="Reset">先清掉這個複本原本的項目（政策不是 full、重新打開時的整份）；<paramref name="Upserts"/> 是清掉之後的</param>
/// <param name="Upserts">新增或取代的項目（WireItemReader 轉好的，依 Ord；每個 id 只有最後一版）</param>
/// <param name="Removed">移除的項目 id</param>
internal sealed record ReplicaItems(string ConversationId, bool Reset, IReadOnlyList<AgentChatItem> Upserts, IReadOnlyList<string> Removed);

/// <summary>交給中樞的一次複本變化</summary>
/// <param name="Metas">新增或改變的複本</param>
/// <param name="Deleted">被擁有者刪除的複本（從清單移除）</param>
/// <param name="Items">項目的變化</param>
/// <param name="Full"><paramref name="Metas"/> 是整份清單：不在裡面的複本都移除（整份清單的要求、伺服器換實例）</param>
internal sealed record ReplicaDelta(IReadOnlyList<ReplicaMeta> Metas, IReadOnlyList<string> Deleted, IReadOnlyList<ReplicaItems> Items, bool Full = false)
{
    public bool IsEmpty => !Full && Metas.Count == 0 && Deleted.Count == 0 && Items.Count == 0;
}

/// <summary>
/// 中樞向引擎要複本的出口（對話變化的接收者另外實作：發布者把要求放進盒子，結果以 <see cref="ReplicaDelta"/> 回到中樞）。
/// 只在 UI 執行緒呼叫、要很快回來
/// </summary>
internal interface IReplicaRequester
{
    /// <param name="conversationId">打開了這個複本：引擎送它存著的項目（整份，<see cref="ReplicaItems.Reset"/>）並開始拉它的串流；null＝要整份複本清單（<see cref="ReplicaDelta.Full"/>）</param>
    void RequestReplica(string? conversationId);

    /// <param name="conversationId">沒有任何視窗開著這個複本了：引擎不再拉它的串流（存著的項目與游標留著）；null＝全部（關掉顯示、移除所有複本）</param>
    void CloseReplica(string? conversationId);
}
