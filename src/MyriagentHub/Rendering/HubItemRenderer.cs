using System.Collections.Concurrent;
using System.Text;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Rendering;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Rendering;

/// <summary>伺服器渲染一筆項目的來源：物化的 <c>items</c> 列與它的對話</summary>
/// <param name="ConversationId">對話（記錄去重用）</param>
/// <param name="OwnerDeviceId">擁有者裝置：附件與媒體的參照是它發的</param>
/// <param name="WorkingDirectory">對話的工作資料夾（meta 的，已遮蔽；回覆裡的相對路徑以它解析，伺服器一律換成 <c>#</c>）</param>
/// <param name="ItemId">項目 id（payload 解不開時佔位用）</param>
/// <param name="Ord">項目的 Ord（網頁的 seq）</param>
/// <param name="Body">存的 payload（<c>items.body</c>）</param>
/// <param name="Enc">payload 的編碼（<c>items.enc</c>：json、gzip）</param>
/// <param name="ReceivedAt">伺服器收到這筆的時間（Unix 毫秒）：項目沒有 createdAt 時當成建立時間</param>
/// <param name="MaxPayloadBytes">解碼 <paramref name="Body"/> 的上限：持久項目是 <see cref="ConversationEventRules.MaxPayloadBytes"/>（128 KB）；
/// 即時疊加層的格是 <see cref="LiveRules.MaxDeltaItemBytes"/>（live2 收件時以 CheckRelay 驗過 ≤256 KB），否則 128～256 KB 的串流中項目會變成「暫時無法顯示」</param>
internal sealed record HubItemSource(string ConversationId, string OwnerDeviceId, string? WorkingDirectory, string ItemId, long Ord, string Body, string Enc, long ReceivedAt,
    int MaxPayloadBytes = ConversationEventRules.MaxPayloadBytes);

/// <summary>copyItem 讀原文的結果（<see cref="HubItemRenderer.TryReadText"/>）</summary>
internal enum ItemTextOutcome
{
    Ok,

    /// <summary>不是要讀的種類（copyItem 只收 Assistant；側欄的對話搜尋只收使用者訊息、代理回覆與計畫確認）</summary>
    NotAssistant,

    /// <summary>payload 解不開、blob 讀不到或命中 DLP</summary>
    Unreadable,
}

/// <summary>
/// 伺服器以共用的 <see cref="ItemRenderer"/> 產生瀏覽器的 Item JSON（WP4.2 計畫步 7、D18、D20、D24、D33）：
/// 解碼 → <see cref="ConversationWire.TryReadItem"/>（blob 經 <see cref="IBlobStore"/> 換回）→ 換回的 blob 內容跑第二道權杖 DLP
/// （<see cref="ConversationDlpRules.HasLeakText"/>：blob 不在上行的檢查範圍內）→ <see cref="HubItemRules.ToChatItem"/> → 每次 new 一個 ItemRenderer
/// → <see cref="HubItemRules.Finish"/>（含淨化）→ 序列化（<see cref="ConversationWire.Serialize"/>）。UTF-8 超過上限時以 <see cref="HubItemRules.Shrink"/>
/// 只留尾段再渲染一次（三欄加起來已超過上限的直接只留尾段、不先完整渲染），仍然超過、讀不到 blob、payload 解不開或渲染發生例外時回佔位，不丟例外。
/// 執行緒安全：ItemRenderer 不是，所以每次呼叫都 new 一個；失敗的記錄以 (對話, 項目) 去重、最多記 <see cref="MaxLoggedFailures"/> 個鍵，
/// 記錄只有例外的型別、對話與項目 id，不寫內容與例外訊息
/// </summary>
internal sealed class HubItemRenderer(HubMediaUrls urls, IBlobStore blobs, ILogger<HubItemRenderer> log)
{
    /// <summary>記過失敗的 (對話, 項目) 上限：超過之後不再記（防止病態的資料灌爆記錄與記憶體）</summary>
    public const int MaxLoggedFailures = 1000;

    private readonly ConcurrentDictionary<(string Conversation, string Item), byte> logged = new();

    /// <summary>一筆項目 → 送給瀏覽器的 Item JSON（不縮排）；UTF-8 不超過 <paramref name="maxBytes"/>（佔位本身除外，它很小）</summary>
    public string Render(HubItemSource source, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            return RenderCore(source, maxBytes);
        }
        catch (Exception ex)
        {
            LogFailure(source, ex.GetType().Name);
            return Placeholder(source, null, HubItemRules.UnavailableMessage);
        }
    }

    private string RenderCore(HubItemSource source, int maxBytes)
    {
        if (!PayloadRules.TryDecode(source.Enc, source.Body, source.MaxPayloadBytes, out var json)
            || !ConversationEventRules.TryParseStrict(json, out var token) || token is not JObject payload)
        {
            LogFailure(source, "payload");
            return Placeholder(source, null, HubItemRules.UnavailableMessage);
        }

        var contents = new List<byte[]>();
        if (!ConversationWire.TryReadItem(payload, sha => ReadBlob(sha, contents), out var item))
            return Placeholder(source, payload, HubItemRules.TooLargeMessage);
        foreach (var content in contents)
        {
            if (ConversationDlpRules.HasLeakText(Encoding.UTF8.GetString(content)))
            {
                LogFailure(source, "blob-dlp");
                return Placeholder(source, payload, HubItemRules.UnavailableMessage);
            }
        }

        if (HubItemRules.ToChatItem(item, source.ReceivedAt) is null)
        {
            // 種類或狀態不是列舉的名稱：共用驗證已擋，這裡不會發生
            LogFailure(source, "kind");
            return Placeholder(source, payload, HubItemRules.UnavailableMessage);
        }

        if (!HubItemRules.ShouldShrinkFirst(item, maxBytes) && RenderOnce(source, item) is var full && Fits(full, maxBytes))
            return full;
        if (RenderOnce(source, HubItemRules.Shrink(item, HubItemRules.ShrinkChars(maxBytes))) is var shrunk && Fits(shrunk, maxBytes))
            return shrunk;
        return Placeholder(source, payload, HubItemRules.TooLargeMessage);
    }

    /// <summary>渲染一次（種類與狀態已確認是列舉的名稱）</summary>
    private string RenderOnce(HubItemSource source, WireItem item)
    {
        var chat = HubItemRules.ToChatItem(item, source.ReceivedAt)!;
        var renderer = new ItemRenderer(urls, (_, ex) => LogFailure(source, ex.GetType().Name), _ => LogFailure(source, "markdown"));
        var rendered = renderer.Render(chat, source.WorkingDirectory);
        return ConversationWire.Serialize(HubItemRules.Finish(rendered, item, urls, source.OwnerDeviceId));
    }

    /// <summary>
    /// copyItem（WP4.2 計畫 D30）：一筆項目的 Markdown 原文（<see cref="WireItem.Text"/>；blob 經 <paramref name="blobs"/> 換回，換回的內容跑第二道權杖 DLP，同渲染）。
    /// 不渲染。不是 Assistant 回 <see cref="ItemTextOutcome.NotAssistant"/>；payload 解不開、blob 讀不到或命中 DLP 回 <see cref="ItemTextOutcome.Unreadable"/>；
    /// 沒有文字是空字串
    /// </summary>
    internal static ItemTextOutcome TryReadText(IBlobStore blobs, string enc, string body, out string text)
        => TryReadText(blobs, enc, body, kind => kind == nameof(AgentItemKind.Assistant), out text);

    /// <summary>
    /// 側欄的對話搜尋（web-protocol 的 <c>searchConversations</c>）：同 copyItem 讀原文（blob 換回、第二道 DLP），只收內容搜得到的種類
    /// （<see cref="ConversationSearchRules.IsSearchable"/>：使用者訊息、代理回覆、計畫確認），其他種類回 <see cref="ItemTextOutcome.NotAssistant"/>
    /// </summary>
    internal static ItemTextOutcome TryReadSearchText(IBlobStore blobs, string enc, string body, out string text)
        => TryReadText(blobs, enc, body, kind => Enum.TryParse<AgentItemKind>(kind, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            && kind == parsed.ToString() && ConversationSearchRules.IsSearchable(parsed), out text);

    private static ItemTextOutcome TryReadText(IBlobStore blobs, string enc, string body, Func<string, bool> kindAllowed, out string text)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        text = string.Empty;
        if (!PayloadRules.TryDecode(enc, body, ConversationEventRules.MaxPayloadBytes, out var json)
            || !ConversationEventRules.TryParseStrict(json, out var token) || token is not JObject payload)
            return ItemTextOutcome.Unreadable;
        if (payload["kind"] is not JValue { Type: JTokenType.String, Value: string kind } || !kindAllowed(kind))
            return ItemTextOutcome.NotAssistant;
        var contents = new List<byte[]>();
        if (!ConversationWire.TryReadItem(payload, sha => ReadBlob(blobs, sha, contents), out var item))
            return ItemTextOutcome.Unreadable;
        if (contents.Any(content => ConversationDlpRules.HasLeakText(Encoding.UTF8.GetString(content))))
            return ItemTextOutcome.Unreadable;
        text = item.Text ?? string.Empty;
        return ItemTextOutcome.Ok;
    }

    private byte[]? ReadBlob(string sha256, List<byte[]> contents) => ReadBlob(blobs, sha256, contents);

    /// <summary>blob 的內容（最多 <see cref="ConversationEventRules.MaxBlobLength"/> 位元組）；沒有或更長回 null。讀到的內容另外收進 <paramref name="contents"/> 給 DLP 檢查</summary>
    private static byte[]? ReadBlob(IBlobStore blobs, string sha256, List<byte[]> contents)
    {
        using var stream = blobs.OpenRead(sha256);
        if (stream is null)
            return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > ConversationEventRules.MaxBlobLength)
                return null;
            buffer.Write(chunk, 0, read);
        }

        var content = buffer.ToArray();
        contents.Add(content);
        return content;
    }

    private static bool Fits(string json, int maxBytes) => Encoding.UTF8.GetByteCount(json) <= maxBytes;

    /// <summary>佔位：payload 解得開時照它的種類、狀態與建立時間，否則 Notice、Done、收到的時間</summary>
    private static string Placeholder(HubItemSource source, JObject? payload, string message)
    {
        var createdAt = payload?["createdAt"] is JValue { Type: JTokenType.Integer, Value: long ms } ? ms : source.ReceivedAt;
        var kind = payload?["kind"] is JValue { Type: JTokenType.String, Value: string k } ? k : null;
        var status = payload?["status"] is JValue { Type: JTokenType.String, Value: string s } ? s : null;
        return ConversationWire.Serialize(HubItemRules.Placeholder(source.ItemId, kind, status, source.Ord, createdAt, message));
    }

    private void LogFailure(HubItemSource source, string what)
    {
        var key = (source.ConversationId, source.ItemId);
        if (logged.Count >= MaxLoggedFailures || !logged.TryAdd(key, 0))
            return;
        log.LogWarning("伺服器渲染對話 {ConversationId} 的項目 {ItemId} 失敗（{Failure}），改送佔位或純文字", source.ConversationId, source.ItemId, what);
    }
}
