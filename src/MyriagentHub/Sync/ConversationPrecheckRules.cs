using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>
/// 一筆對話事件在寫入者之外先做完的檢查（WP4.2 計畫 D3、D8、D44）。
/// </summary>
/// <param name="Rejected">被擋的原因（<c>invalid</c>、<c>too_large</c>）；null＝通過，接著在寫入者裡做授權（不會是 <c>dlp</c>：保留碼，答 5）</param>
/// <param name="Json">要套用的原文：沒命中 DLP 時是裝置送的原文，命中時是遮蔽版；被擋時 null</param>
/// <param name="Parsed"><see cref="Json"/> 解析後的物件；被擋時 null</param>
/// <param name="DlpClean">
/// 解碼成功，而且原文的走遍檢查是假（D8、提案 C6）：被擋的對話事件只有這時 <c>rejected_events.payload</c> 才存原文，其他一律存 null
/// （原文命中——包括遮蔽後仍命中、命中又被授權擋下——解碼失敗、<c>too_large</c>、還沒跑到 DLP 的拒絕）
/// </param>
/// <param name="Remasked">原文命中、遮蔽後通過而接受（只有這時為真；遮蔽後仍不合格的是 <see cref="Rejected"/>＝invalid、這裡是 false）</param>
/// <param name="StorePayload">遮蔽版以 <see cref="PayloadRules.Encode"/> 編碼的 payload（大小規則見 PayloadRules.Encode）；沒命中時 null＝存裝置送的原始位元組</param>
/// <param name="StoreEnc"><see cref="StorePayload"/> 的 enc；沒命中時 null</param>
internal sealed record ConversationPrecheck(string? Rejected, string? Json, JObject? Parsed, bool DlpClean,
    bool Remasked = false, string? StorePayload = null, string? StoreEnc = null);

/// <summary>
/// 對話事件的預先檢查（WP4.2 計畫 D3）：純函式、執行緒安全，在 <c>SyncWriter.WriteAsync</c> 之前、呼叫端的執行緒上做完，
/// 寫入者裡只剩查表的授權、寫入與物化（一筆 128 KB、接近命中的事件不會拖住全站的寫入）。
/// </summary>
internal static class ConversationPrecheckRules
{
    private static readonly AsyncLocal<Func<string, JObject, string>?> RemaskOverride = new();

    /// <summary>測試注入點：取代 <see cref="ConversationRemaskRules.Remask"/>，讓「遮蔽後仍命中」的分支測得到（AsyncLocal：只影響設定它的測試流程）</summary>
    internal static Func<string, JObject, string>? RemaskForTest
    {
        get => RemaskOverride.Value;
        set => RemaskOverride.Value = value;
    }

    /// <summary>
    /// 只給對話種類的事件。順序：
    /// <list type="number">
    /// <item>伺服器寫的種類（<see cref="ConversationEventKinds.IsServerAuthored"/>：第 5 階段的 <c>conv.lease</c>）：裝置不能送，一律 invalid、payload 不存。</item>
    /// <item>事件外框：<see cref="ConversationEventRules.CheckEvent"/>（對話種類、id 是線上格式、epoch ≥1、串流是 <c>c:{id}</c>）。</item>
    /// <item><see cref="CatalogRules.CheckPayload"/>：解碼與解壓後的大小（超過 <see cref="SyncLimits.MaxInlinePayload"/> 是 too_large）。</item>
    /// <item>SHA-256 與裝置送的 <c>payloadSha256</c> 相符。以上被擋的都還沒跑到 DLP，<see cref="ConversationPrecheck.DlpClean"/> 是 false。</item>
    /// <item><see cref="ConversationEventRules.Validate(string?, string?)"/>：不合格時照樣走遍檢查解碼後的內容（<see cref="ConversationPrecheck.DlpClean"/> 照實填；
    /// 有標準 JSON 沒有的節點〔<see cref="ConversationDlpRules.HasNonStandardToken"/>〕當成不乾淨）；內嵌字串太大的 too_large 不檢查（存 null）。</item>
    /// <item>合格但有標準 JSON 沒有的節點（Newtonsoft 接受的 <c>new 名稱(…)</c> 建構式、<c>undefined</c>）：invalid、payload 不存
    /// （走遍檢查不看建構式的名稱，不能當成乾淨的原文存下或經 Pull 發出去）。</item>
    /// <item>原文的走遍檢查（<see cref="ConversationDlpRules.HasLeak"/>）：沒命中就通過、存原始位元組。</item>
    /// <item>命中：<see cref="ConversationRemaskRules.Remask"/>，遮蔽版再跑 Validate 與走遍檢查，都過才接受（存遮蔽版），否則 invalid。
    /// 再遮蔽丟出毒事件的例外（<see cref="ConversationFaultRules.IsPoison"/>）也是 invalid，其他例外照舊往外丟。</item>
    /// </list>
    /// </summary>
    public static ConversationPrecheck Check(SyncEvent e, SyncLimits limits)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(limits);
        if (ConversationEventKinds.IsServerAuthored(e.Kind))
            return Reject(SyncErrorCodes.Invalid, dlpClean: false);
        if (ConversationEventRules.CheckEvent(e.Kind, e.Stream, e.ConversationId, e.Epoch) is { } frame)
            return Reject(frame, dlpClean: false);
        var decoded = CatalogRules.CheckPayload(e.Enc, e.Payload, limits.MaxInlinePayload, out var json);
        if (decoded != PayloadCheck.Ok)
            return Reject(decoded == PayloadCheck.TooLarge ? SyncErrorCodes.TooLarge : SyncErrorCodes.Invalid, dlpClean: false);
        if (!string.Equals(PayloadRules.Sha256Hex(json), e.PayloadSha256, StringComparison.Ordinal))
            return Reject(SyncErrorCodes.Invalid, dlpClean: false);

        var kind = e.Kind;
        if (ConversationEventRules.Validate(kind, json) is { } problem)
        {
            if (problem == SyncErrorCodes.TooLarge)
                return Reject(SyncErrorCodes.TooLarge, dlpClean: false);
            // 有標準 JSON 沒有的節點（建構式等）一律當成不乾淨：走遍檢查看不到建構式的名稱，原文不能存進 rejected_events
            var leak = ConversationEventRules.TryParseStrict(json, out var token)
                ? ConversationDlpRules.HasNonStandardToken(token) || ConversationDlpRules.HasLeak(token)
                : ConversationDlpRules.HasLeakText(json);
            return Reject(problem, dlpClean: !leak);
        }

        if (!ConversationEventRules.TryParseStrict(json, out var parsedToken) || parsedToken is not JObject parsed)
            return Reject(SyncErrorCodes.Invalid, dlpClean: !ConversationDlpRules.HasLeakText(json));
        // Validate 收的建構式（不在字串欄位上的 `new 名稱(…)`）：SyncJson 不會寫出，走遍檢查也看不到名稱——拒收、payload 不存
        if (ConversationDlpRules.HasNonStandardToken(parsed))
            return Reject(SyncErrorCodes.Invalid, dlpClean: false);
        if (!ConversationDlpRules.HasLeak(parsed))
            return new ConversationPrecheck(null, json, parsed, DlpClean: true);

        // 第二道 DLP 命中：遮蔽後接受（答 5）；遮蔽後仍不合格（含超過大小上限）或仍命中就 invalid，payload 不存
        string remasked;
        try
        {
            remasked = (RemaskForTest ?? ConversationRemaskRules.Remask)(kind, parsed);
        }
        catch (Exception ex) when (ConversationFaultRules.IsPoison(ex))
        {
            // 這一筆的內容造成的（D37）：只拒收這一筆，不讓 AppendBatchAsync 往外丟而中斷連線；payload 不存
            return Reject(SyncErrorCodes.Invalid, dlpClean: false);
        }

        if (ConversationEventRules.Validate(kind, remasked) is not null
            || !ConversationEventRules.TryParseStrict(remasked, out var remaskedToken) || remaskedToken is not JObject reparsed
            || ConversationDlpRules.HasLeak(reparsed))
            return Reject(SyncErrorCodes.Invalid, dlpClean: false);
        var (enc, payload) = PayloadRules.Encode(remasked);
        return new ConversationPrecheck(null, remasked, reparsed, DlpClean: false, Remasked: true, StorePayload: payload, StoreEnc: enc);
    }

    private static ConversationPrecheck Reject(string reason, bool dlpClean) => new(reason, null, null, dlpClean);
}
