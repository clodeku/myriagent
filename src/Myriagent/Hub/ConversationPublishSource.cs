using Myriagent.Agents;
using Myriagent.Sync;

namespace Myriagent.Hub;

/// <summary>
/// 中樞提供給對話發布者（<c>ConversationPublisher</c>）的快照來源（WP4.3 計畫步 7，D1、D8、D12）。除了 <see cref="BeginInvoke"/>，
/// 每個方法都只在 UI 執行緒呼叫、要很快回來：只複製容器與可變的元素（<see cref="ChatItemCopyRules"/>），字串共用，不序列化、不遮蔽、不讀寫檔
/// （<see cref="SnapshotItems"/> 準備 Ord 時可能寫保留的旁檔，D9）。不參考 SignalR、SQLite 或引擎的型別（未配對時中樞也編進這個介面，S6）
/// </summary>
internal interface IConversationPublishSource
{
    /// <summary>對話資料夾的根目錄（引擎在執行緒集區唯讀讀未載入的對話，D10）</summary>
    string ConversationsRoot { get; }

    /// <summary>排到 UI 執行緒（轉給中樞的 IAgentHost.BeginInvoke；任何執行緒都可以呼叫）</summary>
    void BeginInvoke(Action action);

    /// <summary>所有本機擁有的對話 id（不含草稿與其他電腦的副本；同一次 UI 呼叫取完，D12）</summary>
    IReadOnlyList<string> ConversationIds();

    /// <summary>
    /// 這些對話的中繼資料快照（一次最多 <see cref="PublishTimingRules.MetaSlice"/> 個，超過丟 <see cref="ArgumentOutOfRangeException"/>）。
    /// 找不到的 id 略過；草稿與副本照樣回（旗標在快照裡）
    /// </summary>
    IReadOnlyList<PublishMetaSnapshot> SnapshotMeta(IReadOnlyList<string> ids);

    /// <summary>
    /// 一頁項目（依 Ord，<c>Ord</c> 大於 <paramref name="afterOrd"/> 的最前面 <paramref name="take"/> 筆）：先以 <paramref name="hint"/> 準備 Ord（D8）。
    /// 項目還沒載入（不讀檔；引擎改在執行緒集區讀，D10）、找不到、草稿與副本回 null；Ord 準備不了回 <see cref="PublishItemsPage.OrdsReady"/> 為 false 的空頁。
    /// 第一頁（<paramref name="afterOrd"/> ≤0）附完整的項目 id 集合（D12）
    /// </summary>
    PublishItemsPage? SnapshotItems(string conversationId, long afterOrd, int take, OrdHint? hint);

    /// <summary>佇列的快照（顯示文字：秘密回答已遮蔽，<see cref="SecretAnswerScrubRules.QueuedDisplay"/>）；找不到、草稿與副本回 null</summary>
    PublishQueueSnapshot? SnapshotQueue(string conversationId);

    /// <summary>引擎判斷「其他地方看得到」的對話（整份取代；D19，IsSeenRemotely 用）</summary>
    void SetWatched(IReadOnlySet<string> conversationIds);

    /// <summary>伺服器以 dlp 擋下 item 類事件（保留碼的防禦，D14）：政策比 paused 寬的改成 paused，經 SaveConversation 存</summary>
    void PauseForDlp(string conversationId);

    /// <summary>寫伺服器刪除的旁檔（D25）：成功（或已經有）才回 true，發布者收到 true 才送 conv.deleted</summary>
    bool MarkServerDeleted(string conversationId, long atMs);

    /// <summary>彙總提示（D14、D32：同一種原因的所有對話併成一則）</summary>
    void ShowPublishNotice(PublishNoticeKind kind, int count);

    /// <summary>其他電腦的對話（複本）的變化（WP4.3 計畫步 15；AgentHub.Replica.cs）：中樞不顯示複本時什麼都不做；例外照常丟出</summary>
    void ApplyReplicaDelta(ReplicaDelta delta);
}

/// <summary>彙總提示的種類（<see cref="IConversationPublishSource.ShowPublishNotice"/>）</summary>
internal enum PublishNoticeKind
{
    /// <summary>伺服器回 not_owner（重新配對前的舊身分上傳的對話，D32）</summary>
    NotOwner,

    /// <summary>其他原因的封鎖（fenced、deleted、程式錯誤、補送 created 的次數用完）</summary>
    Blocked,

    /// <summary>伺服器回 dlp，政策改成 paused（保留碼的防禦，D14）</summary>
    DlpPaused,
}

/// <summary>對話中繼資料的快照（UI 執行緒建立，之後只讀）</summary>
/// <param name="Meta">中繼資料的複本（<see cref="ChatItemCopyRules.CopyMeta"/>）</param>
/// <param name="EffectivePolicy">實際的同步政策：已含代理種類與伺服器刪除的旁檔（有旁檔一律 localOnly，D25）</param>
/// <param name="Running">正在執行一輪</param>
/// <param name="Waiting">等使用者回應（AwaitsUser，F2）</param>
/// <param name="ItemsLoaded">項目已載入記憶體（沒載入的由引擎在執行緒集區讀檔，D10）</param>
/// <param name="ServerDeleted">有伺服器刪除的旁檔</param>
/// <param name="Models">這個對話的帳戶在這台可選的模型（meta 的 <c>models</c>，其他電腦與瀏覽器版照它列選單）；null＝不送</param>
internal sealed record PublishMetaSnapshot(AgentConversation Meta, string EffectivePolicy, bool Running, bool Waiting, bool ItemsLoaded, bool IsDraft,
    bool IsReplica, bool ServerDeleted, IReadOnlyList<AgentModelOption>? Models = null);

/// <summary>一頁項目（依 Ord 分頁，途中移除項目也不跳號，D12）</summary>
/// <param name="Items">這一頁的項目複本，依 Ord 由小到大</param>
/// <param name="AllIds">第一頁才有：目前所有項目的 id（清單順序）；之後的頁是 null</param>
/// <param name="LastOrd">這一頁最後一筆的 Ord（空頁是要求的 afterOrd）：下一頁從它之後接著要</param>
/// <param name="More">還有 Ord 更大的項目</param>
/// <param name="OrdsReady">Ord 已準備好（false 時這一頁是空的，之後再要）</param>
/// <param name="RemovalsTrusted">
/// 第一頁的 <paramref name="AllIds"/> 可以拿來推斷刪除（「發布過、集合裡沒有」送 itemRemoved）；項目檔損毀過（有 <c>items.json.corrupt-*</c>）時是 false，
/// 清單可能少了東西，不推斷刪除（合併前審查 F2）
/// </param>
internal sealed record PublishItemsPage(string ConversationId, IReadOnlyList<AgentChatItem> Items, IReadOnlyList<string>? AllIds, long LastOrd, bool More,
    bool OrdsReady, bool RemovalsTrusted = true);

/// <summary>佇列的快照：每一則只有 id 與給人看的文字（秘密回答已遮蔽，D20）</summary>
internal sealed record PublishQueueSnapshot(string ConversationId, IReadOnlyList<(string Id, string DisplayText)> Entries);
