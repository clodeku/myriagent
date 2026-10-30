using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 桌面的同步檢視（WP4.6 計畫步 9；D22、D25、D27）：其他電腦對話的副本在清單與標頭帶擁有者電腦與那邊的狀態、不被當成「帳戶已移除」；
// 掛著接收者的本機對話在標頭帶這個對話的同步政策選單；協調者的本機指令（紀錄、回收區）只回給發問的檢視。
// 擁有者：WP4.6（本檔）。只在 UI 執行緒；只用 LocalSyncViewRules 的純函式，不參考 SignalR、SQLite 或同步引擎的型別（S6）
internal sealed partial class AgentHub
{
    /// <summary>
    /// 清單的一筆（RenderConversationList 呼叫）：副本加 <c>deviceId</c>、<c>running</c>／<c>waiting</c>、工作資料夾換成擁有者那邊的（D22）；
    /// 本機對話逐鍵不變
    /// </summary>
    private void DecorateSummary(ConversationState c, JObject summary)
    {
        if (c.IsReplica)
            LocalSyncViewRules.ApplyReplicaSummary(summary, c.ReplicaOwnerDeviceId, c.ReplicaRunning, c.ReplicaWaiting, c.ReplicaCustomFolder, c.ReplicaFolder);
    }

    /// <summary>
    /// 標頭（RenderHeader 呼叫）：副本加 <c>deviceId</c>、<c>replica</c>（擁有者那台的帳戶以目錄換成邏輯帳戶），<c>accountMissing</c> 改成 false、
    /// <c>recentFolders</c>／<c>commands</c> 清空、工作資料夾換成擁有者那邊的（D22）；
    /// 掛著接收者、不是草稿的本機對話加 <c>conversationSync</c>（D25：政策是實際生效的那一個，選項由能不能同步與是否已從伺服器刪除算出）。
    /// 沒有接收者的本機對話逐鍵不變
    /// </summary>
    private void DecorateHeader(ConversationState c, JObject header)
    {
        if (c.IsReplica)
        {
            LocalSyncViewRules.ApplyReplicaHeader(header, c.ReplicaOwnerDeviceId, c.ReplicaRunning, c.ReplicaWaiting, c.ReplicaItemsAvailable,
                c.ReplicaCustomFolder, c.ReplicaFolder, sync?.ReplicaLogicalAccount(c.ReplicaOwnerDeviceId, c.ReplicaOwnerAccountId));
            return;
        }

        if (conversationSink is null || c.IsDraft)
            return;
        var syncable = SyncProviderRules.TryWireName(c.Meta.Provider, out _);
        header["conversationSync"] = LocalSyncViewRules.ConversationSyncView(EffectiveSyncPolicy(c), syncable, IsServerDeleted(c));
    }

    /// <summary>
    /// 接收者換了或拿掉（協調者的 AttachPublisher 呼叫，在 AttachConversationSink 之前）：開著的本機對話重送標頭（<c>conversationSync</c> 出現或消失）。
    /// 只排進合併送出（50 毫秒後在 UI 執行緒），送的時候接收者已經換好；不經對話變化的掛鉤（不是對話本身的改變，不發布）
    /// </summary>
    internal void RefreshSyncHeaders()
    {
        if (disposed)
            return;
        foreach (var c in conversations.Values)
        {
            if (!c.IsReplica && !c.IsDraft)
                RefreshSyncHeader(c);
        }
    }

    /// <summary>
    /// 這個對話的同步政策或伺服器刪除改了（SetConversationSyncPolicy、MarkServerDeleted、PauseForDlp 之後）：開著它的視窗重送標頭。
    /// 只更新畫面、不經對話變化的掛鉤（存檔本身已經通知過接收者；伺服器刪除不能再讓發布者多送一次中繼資料）
    /// </summary>
    private void RefreshSyncHeader(ConversationState c)
    {
        if (c.ViewIds.Count == 0)
            return;
        dirtyHeaders.Add(c.Meta.Id);
        ScheduleFlush();
    }

    /// <summary>
    /// 協調者背景讀完的結果（紀錄的一頁、回收區清單、匯出的結果；D27）：回到 UI 執行緒之後只送給發問的檢視，那個視窗已經關了就丟掉
    /// </summary>
    internal void PostToViewIfOpen(string viewId, JObject message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!disposed && views.TryGetValue(viewId, out var view))
            Post(view, message);
    }

    /// <summary>協調者本機指令的提示（還原回收區的成功與失敗）：只給送指令的那個視窗</summary>
    internal void LocalViewToast(IAgentView view, string text, bool isError) => Toast(text, isError, view);
}
