namespace Myriagent.Sync;

/// <summary>
/// 對話事件在 outbox 的合併鍵（WP4.3 計畫 D5；docs/sync-protocol.md 14.2）：只有 Pending 而且同鍵的列會被原地取代（連 kind 一起換），
/// 所以鍵決定了「哪些事件可以只留最新」。鍵一律是 <c>{用途}:{對話 id}…</c>，屬於那個對話（<see cref="OutboxAppendRules.KeyBelongsTo"/>）。
/// <list type="bullet">
/// <item><c>meta:{id}:{gen}</c>：中繼資料。世代在每個一輪開始或結束的標記之後換新，<c>running:false</c> 的 meta 才不會換到 <c>turn.started</c> 前面。</item>
/// <item><c>item:{id}:{itemId}</c>：<c>conv.item</c> 與 <c>conv.itemRemoved</c> 共用，同一筆只留最新的狀態。</item>
/// <item><c>queue:{id}</c>：佇列快照。</item>
/// <item><c>conv.created</c>、<c>conv.turn</c>、<c>conv.attention</c>、<c>conv.deleted</c> 沒有鍵（不能被之後的事件換掉）。</item>
/// </list>
/// </summary>
internal static class PublishKeyRules
{
    private const string MetaPrefix = "meta:";

    private const string ItemPrefix = "item:";

    private const string QueuePrefix = "queue:";

    /// <summary><c>meta:{id}:{gen}</c></summary>
    /// <param name="wireId">線上格式的對話 id（<see cref="ConversationIds.IsWireId"/>）</param>
    /// <param name="generation"><see cref="Generation"/> 產生的世代</param>
    public static string Meta(string wireId, string generation)
    {
        RequireWireId(wireId);
        ArgumentException.ThrowIfNullOrEmpty(generation);
        return MetaPrefix + wireId + ":" + generation;
    }

    /// <summary><c>item:{id}:{itemId}</c>（項目 id 照原樣、區分大小寫）</summary>
    public static string Item(string wireId, string itemId)
    {
        RequireWireId(wireId);
        if (!ConversationIds.IsItemId(itemId))
            throw new ArgumentException("項目 id 不合格", nameof(itemId));
        return ItemPrefix + wireId + ":" + itemId;
    }

    /// <summary><c>queue:{id}</c></summary>
    public static string Queue(string wireId)
    {
        RequireWireId(wireId);
        return QueuePrefix + wireId;
    }

    /// <summary>meta 鍵的世代：<c>{bootId}.{n}</c>，n 是這次啟動裡一輪開始或結束標記的序號（換一次啟動就換 bootId，不會跨啟動誤合併）</summary>
    public static string Generation(string bootId, long markerSeq)
    {
        ArgumentException.ThrowIfNullOrEmpty(bootId);
        if (bootId.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("bootId 不可以含冒號", nameof(bootId));
        ArgumentOutOfRangeException.ThrowIfNegative(markerSeq);
        return bootId + "." + markerSeq.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>從項目鍵解回對話與項目；不是 <see cref="Item"/> 產生的格式（含別種鍵、大寫的對話 id、不合格的項目 id）回 false</summary>
    public static bool TryParseItem(string? key, out string wireId, out string itemId)
    {
        wireId = string.Empty;
        itemId = string.Empty;
        if (key is null || !key.StartsWith(ItemPrefix, StringComparison.Ordinal))
            return false;
        var rest = key.AsSpan(ItemPrefix.Length);
        if (rest.Length <= ConversationIds.Length + 1 || rest[ConversationIds.Length] != ':')
            return false;
        var id = rest[..ConversationIds.Length].ToString();
        var item = rest[(ConversationIds.Length + 1)..].ToString();
        if (!ConversationIds.IsWireId(id) || !ConversationIds.IsItemId(item))
            return false;
        wireId = id;
        itemId = item;
        return true;
    }

    private static void RequireWireId(string wireId)
    {
        if (!ConversationIds.IsWireId(wireId))
            throw new ArgumentException("對話 id 不是線上格式（32 個小寫十六進位）", nameof(wireId));
    }
}
