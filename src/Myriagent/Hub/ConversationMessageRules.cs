using Myriagent.Sync;

namespace Myriagent.Hub;

/// <summary>一個找得到的對話（myriagent-remote conversations 的一列；送訊息時的候選）</summary>
/// <param name="Id">線上 id（32 個小寫十六進位；每台電腦、瀏覽器版都一樣）</param>
/// <param name="DeviceId">對話所在的電腦（這台未配對時是 null）</param>
/// <param name="Self">在這台</param>
internal sealed record ConversationTarget(string Id, string Title, string? DeviceId, string DeviceName, bool Self, bool Running, bool Waiting, DateTime UpdatedAt);

/// <summary>
/// 對話之間互送訊息（myriagent-remote conversations／send；2026-11-15 使用者要求：A 電腦的對話 A1 的代理通知 B 電腦的對話 B1 的代理，
/// 「要確保跨電腦都可以找得到對話框 ID 與指定要送的電腦」）：找對話、組送出的文字。純函式
/// </summary>
internal static class ConversationMessageRules
{
    /// <summary>訊息內容的上限（加上來源說明之後仍在遠端 send 的上限 16,000 字之內）</summary>
    public const int MaxBodyLength = 15_000;

    /// <summary>標題在來源說明裡最多幾個字</summary>
    public const int MaxTitleInHeader = 80;

    /// <summary>未配對時代表這台的寫法（配對後這些也都指這台）</summary>
    public static bool IsSelfAlias(string? query)
        => query is { } q && q.Trim().ToLowerInvariant() is "這台" or "这台" or "self" or "local" or "this" or ".";

    /// <summary>
    /// 在 <paramref name="device"/> 這台的對話裡找 <paramref name="query"/>：對話 ID（32 個十六進位，大小寫都收）完全相同，或標題不分大小寫完全相同（只能有一個）。
    /// ID 對到的是別台的對話時說它在哪台。找不到或分不出來回 null 與說明
    /// </summary>
    public static (ConversationTarget? Target, string? Problem) Resolve(IReadOnlyList<ConversationTarget> all, string deviceName, Func<ConversationTarget, bool> onDevice, string query)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(onDevice);
        var q = (query ?? string.Empty).Trim();
        if (q.Length == 0)
            return (null, "少了要送的對話（對話 ID 或標題）");
        if (ConversationIds.ToWire(q) is { } wire)
        {
            var byId = all.FirstOrDefault(t => t.Id == wire);
            if (byId is null)
                return (null, $"找不到對話 {wire}：它可能已經刪除、是臨時對話、或那台沒有把它同步到伺服器（myriagent-remote conversations 列出找得到的對話）");
            if (!onDevice(byId))
                return (null, $"對話 {wire}（「{byId.Title}」）在「{byId.DeviceName}」，不在「{deviceName}」：請改送到「{byId.DeviceName}」");
            return (byId, null);
        }

        var mine = all.Where(onDevice).ToList();
        var titled = mine.Where(t => string.Equals(t.Title.Trim(), q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (titled.Count == 1)
            return (titled[0], null);
        if (titled.Count > 1)
            return (null, $"「{deviceName}」有 {titled.Count} 個對話叫「{q}」，請改用對話 ID：" + string.Join("、", titled.Select(t => t.Id)));
        var elsewhere = all.Where(t => !onDevice(t) && string.Equals(t.Title.Trim(), q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (elsewhere.Count > 0)
            return (null, $"「{deviceName}」沒有叫「{q}」的對話；" + string.Join("、", elsewhere.Select(t => $"「{t.DeviceName}」有一個（{t.Id}）")));
        return (null, $"「{deviceName}」沒有叫「{q}」的對話（myriagent-remote conversations \"{deviceName}\" 列出那台的對話與 ID）");
    }

    /// <summary>
    /// 送進對方對話的整段文字：前面是來源（哪台、哪個對話、對話 ID）與怎麼回覆，對方的代理才知道這不是使用者在那裡打的字、要回給誰。
    /// <paramref name="replyDevice"/> 是回覆時要指定的電腦（配對了是 deviceId，最穩；未配對是「這台」）
    /// </summary>
    public static string Compose(string sourceDeviceName, string? replyDevice, string sourceTitle, string sourceId, string body)
    {
        var title = (sourceTitle ?? string.Empty).Trim();
        if (title.Length == 0)
            title = "（沒有標題）";
        if (title.Length > MaxTitleInHeader)
            title = title[..MaxTitleInHeader] + "…";
        var device = string.IsNullOrWhiteSpace(replyDevice) ? "這台" : replyDevice;
        return "【其他對話送來的訊息】\n" +
               $"來自：電腦「{sourceDeviceName}」的對話「{title}」（對話 ID {sourceId}）\n" +
               $"這是那個對話的代理用 myriagent-remote send 轉達的，不是使用者在這裡打的字。要回覆它：myriagent-remote send {device} {sourceId} -- <內容>\n" +
               "\n" +
               body.Trim();
    }
}
