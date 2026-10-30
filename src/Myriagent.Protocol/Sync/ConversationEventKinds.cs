namespace Myriagent.Sync;

/// <summary>
/// 第 4 階段的對話事件種類（設計 14.11 D30；docs/sync-protocol.md 14.2）：只由對話的擁有者裝置寫（伺服器不寫對話事件，D19；
/// 第 5 階段唯一的例外是伺服器寫的 <see cref="Lease"/>，<see cref="IsServerAuthored"/>），
/// 每筆都帶 conversationId 與 epoch、串流是 <c>c:{conversationId}</c>。類別分兩種：<c>meta</c>（所有裝置都拉、組全域的對話清單）與
/// <c>item</c>（開著或擁有的對話才依串流拉）。第 3 階段的 <see cref="SyncEventKinds.Placement"/> 不認得它們（照舊回 null），
/// 對話事件的類別與串流一律經 <see cref="Placement"/>（要對話 id 才組得出串流）。
/// </summary>
internal static class ConversationEventKinds
{
    /// <summary>對話的第一筆（回補時也從它開始）：完整的中繼資料</summary>
    public const string Created = "conv.created";

    /// <summary>中繼資料改了（改名、換模式與模型、換資料夾、換帳戶、執行中與等待回應）：每次都帶完整的中繼資料</summary>
    public const string Meta = "conv.meta";

    /// <summary>一輪開始與結束（結束時帶結果與那一輪的 token 數）</summary>
    public const string Turn = "conv.turn";

    /// <summary>要使用者注意的事（瀏覽器的 notify）</summary>
    public const string Attention = "conv.attention";

    /// <summary>擁有者刪除了對話</summary>
    public const string Deleted = "conv.deleted";

    /// <summary>一筆項目的線上投影（新增或整筆取代，以 id 判斷）</summary>
    public const string Item = "conv.item";

    public const string ItemRemoved = "conv.itemRemoved";

    /// <summary>佇列快照（每則只有 id 與給人看的文字）</summary>
    public const string Queue = "conv.queue";

    /// <summary>
    /// 第 5 階段：擁有者換手（交接、強制接手、還原後的認領；docs/sync-protocol.md 第 16 節，<see cref="ConversationLeasePayload"/>）。
    /// 唯一由伺服器寫的對話事件（作者是 <see cref="SyncAuthors.Server"/>，D2）：裝置送它一律 invalid（伺服器的 ConversationPrecheckRules、
    /// ConversationApplyRules，桌面的 OutboxAppendRules）；電腦拉到作者不是伺服器的略過。前一版的拉取端不認得它、略過（游標照樣前進）
    /// </summary>
    public const string Lease = "conv.lease";

    public static IReadOnlyList<string> All { get; } = [Created, Meta, Turn, Attention, Deleted, Item, ItemRemoved, Queue, Lease];

    /// <summary>裝置（擁有者）寫的種類：<see cref="All"/> 去掉 <see cref="IsServerAuthored"/> 的</summary>
    public static IReadOnlyList<string> DeviceAuthored { get; } = [Created, Meta, Turn, Attention, Deleted, Item, ItemRemoved, Queue];

    /// <summary>種類所屬的類別（<see cref="SyncClasses.Meta"/> 或 <see cref="SyncClasses.Item"/>）；不是對話種類回 null</summary>
    public static string? ClassOf(string? kind) => kind switch
    {
        Created or Meta or Turn or Attention or Deleted or Lease => SyncClasses.Meta,
        Item or ItemRemoved or Queue => SyncClasses.Item,
        _ => null,
    };

    public static bool IsConversationKind(string? kind) => ClassOf(kind) != null;

    /// <summary>只由伺服器寫的對話事件（第 5 階段只有 <see cref="Lease"/>）：裝置不能送，拉到的要作者是 <see cref="SyncAuthors.Server"/> 才算數</summary>
    public static bool IsServerAuthored(string? kind) => kind == Lease;

    /// <summary>對話事件的類別與串流；不是對話種類、或對話 id 不是線上格式（<see cref="ConversationIds.IsWireId"/>）回 null</summary>
    public static (string Cls, string Stream)? Placement(string? kind, string? conversationId)
        => ClassOf(kind) is { } cls && ConversationIds.IsWireId(conversationId) ? (cls, SyncStreams.Conversation(conversationId!)) : null;
}

/// <summary>
/// 對話事件的識別碼與世代（D38）。線上的對話 id 一律是 32 個小寫十六進位（<c>^[0-9a-f]{32}$</c>）：本機資料夾名大小寫都收
/// （<c>ConversationStoreRules.IsConversationFolderName</c>），送出前以 <see cref="ToWire"/> 轉小寫，收到大寫一律拒絕。
/// epoch 是擁有權的世代：第 4 階段沒有租約，一律是對話的 <c>LeaseEpoch</c>，沒有時是 1。
/// </summary>
internal static class ConversationIds
{
    public const int Length = 32;

    /// <summary>第 4 階段沒有租約時的 epoch</summary>
    public const long DefaultEpoch = 1;

    /// <summary>項目、佇列訊息 id 的長度上限（桌面產生的是 Guid "N" 的 32 字）</summary>
    public const int MaxItemIdLength = 64;

    private const string LowerHex = "0123456789abcdef";

    private const string AnyHex = "0123456789abcdefABCDEF";

    private const string ItemIdChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    public static bool IsWireId(string? id) => id is { Length: Length } && id.AsSpan().IndexOfAnyExcept(LowerHex) < 0;

    /// <summary>本機的對話 id（資料夾名，大小寫都收）→ 線上格式（小寫）；不是 32 個十六進位回 null</summary>
    public static string? ToWire(string? localId)
        => localId is { Length: Length } && localId.AsSpan().IndexOfAnyExcept(AnyHex) < 0 ? localId.ToLowerInvariant() : null;

    public static bool IsEpoch(long? epoch) => epoch >= 1;

    /// <summary>
    /// 項目與佇列訊息的 id：1～64 個英數字、連字號、底線（桌面產生的都是 Guid "N"；手改或舊版留下的怪 id 不上線，
    /// 伺服器拿它當物化表的鍵，不能有路徑或控制字元）
    /// </summary>
    public static bool IsItemId(string? id)
        => id is { Length: > 0 and <= MaxItemIdLength } && id.AsSpan().IndexOfAnyExcept(ItemIdChars) < 0;
}

/// <summary>
/// 對話的同步政策（D37；docs/sync-protocol.md 14.2）：小寫的四個值。存在 <c>AgentConversation.SyncPolicy</c> 的是原字串
/// （之後的版本新增、這版不認得的值不能被讀成 null 而當成 full），一律經 <see cref="Interpret"/> 解讀：
/// 沒設過（null）是 <see cref="Full"/>、不認得的值是最嚴格的 <see cref="LocalOnly"/>。
/// </summary>
internal static class ConversationSyncPolicies
{
    /// <summary>全部發布（預設）</summary>
    public const string Full = "full";

    /// <summary>只發 meta 類（對話清單看得到，內容不上線）</summary>
    public const string Metadata = "metadata";

    /// <summary>什麼都不發（連 conv.created 都沒有）</summary>
    public const string LocalOnly = "localOnly";

    /// <summary>權杖 DLP 命中（設計 8.8）：同 <see cref="Metadata"/>，等擁有者在本機選擇</summary>
    public const string Paused = "paused";

    public static IReadOnlyList<string> All { get; } = [Full, Metadata, LocalOnly, Paused];

    public static bool IsKnown(string? policy) => policy is Full or Metadata or LocalOnly or Paused;

    /// <summary>存檔的值 → 實際的政策：null 是 full、認得的照原樣、不認得的（含大小寫不同）是 localOnly</summary>
    public static string Interpret(string? stored) => stored switch
    {
        null => Full,
        Full or Metadata or LocalOnly or Paused => stored,
        _ => LocalOnly,
    };

    /// <summary>
    /// 嚴格度（先經 <see cref="Interpret"/>）：localOnly 3 ＞ paused 2 ＞ metadata 1 ＞ full 0。兩個來源（meta.json 與旁檔 sync-policy.json）
    /// 不一致時取嚴格度高的
    /// </summary>
    public static int Strictness(string? stored) => Interpret(stored) switch
    {
        Full => 0,
        Metadata => 1,
        Paused => 2,
        _ => 3,
    };

    /// <summary>
    /// 加上代理種類的限制：不進同步的種類（桌面 <c>SyncProviderRules</c>：Antigravity、Grok 在沒協商出 prov1 的連線上，D39）一律 localOnly，
    /// 不看存檔的值
    /// </summary>
    public static string Effective(string? stored, bool providerSyncable) => providerSyncable ? Interpret(stored) : LocalOnly;

    /// <summary>這個政策下發不發布這個種類的事件（政策要先經 <see cref="Interpret"/>／<see cref="Effective"/>；不認得的政策或種類一律不發）</summary>
    public static bool Publishes(string? policy, string? kind) => (policy, ConversationEventKinds.ClassOf(kind)) switch
    {
        (Full, SyncClasses.Meta or SyncClasses.Item) => true,
        (Metadata or Paused, SyncClasses.Meta) => true,
        _ => false,
    };
}
