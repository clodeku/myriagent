using Myriagent.Media;

namespace MyriagentHub.Media;

/// <summary>
/// 媒體回應給瀏覽器（與桌面複本）的標頭與狀態（WP4.7 計畫 D9～D12、D29；設計 4.10:1057、:1062）。
/// 型別只收白名單（<see cref="MediaHeaderRules.SafeMediaContentType"/>），其他一律 <c>application/octet-stream</c> 加 attachment；
/// <c>/f/</c> 的文字 ≤2 MB 以 <c>text/plain; charset=utf-8</c> inline 預覽。純函式
/// </summary>
internal static class MediaServeRules
{
    /// <summary><c>/m/</c>、<c>/t/</c> 的成功回應（設計:1062）：降級之後最多 5 分鐘還在那個瀏覽器的快取裡（R11）</summary>
    public const string PrivateCache = "private, max-age=300";

    /// <summary><c>/f/</c>、桌面複本的代理、退回的縮圖與所有錯誤</summary>
    public const string NoStore = "no-store";

    public const string OctetStream = "application/octet-stream";

    public const string TextPlain = "text/plain; charset=utf-8";

    /// <summary><c>/f/</c> 以文字 inline 預覽的上限；更大的改成下載（D36）</summary>
    public const long MaxTextPreviewBytes = 2L * 1024 * 1024;

    /// <summary>忙碌（每台來源同時 ≤20 個轉送）時的 <c>Retry-After</c> 秒數</summary>
    public const int RetryAfterSeconds = 2;

    /// <summary>縮圖一律給 320 px（<c>s=160</c> 也是 320，瀏覽器縮放）</summary>
    public const int ThumbPixels = 320;

    /// <summary>縮圖網址的 <c>v</c>（抽屜的檔案修改時間）最多幾位數</summary>
    public const int MaxThumbVersionLength = 19;

    /// <summary>Cache-Control：<c>/m/</c>、<c>/t/</c> 照設計 private 5 分鐘；<c>/f/</c> 與桌面複本的代理 no-store</summary>
    public static string CacheControl(MediaEndpointKind endpoint) => endpoint switch
    {
        MediaEndpointKind.Media or MediaEndpointKind.Thumb => PrivateCache,
        MediaEndpointKind.File or MediaEndpointKind.ReplicaMedia or MediaEndpointKind.ReplicaThumb or MediaEndpointKind.ReplicaFile => NoStore,
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "不認得的媒體端點"),
    };

    /// <summary>
    /// 原檔與檔案回應的 <c>Content-Type</c> 與 <c>Content-Disposition</c>（null＝不帶）。
    /// 原檔（<c>/m/</c>、複本）：白名單內照標準寫法 inline（有檔名才帶 <c>inline; filename…</c>），其他 octet-stream 加 attachment。
    /// 檔案（<c>/f/</c>）：裝置說是文字而且總長 ≤2 MB 時 <c>text/plain; charset=utf-8</c> inline，其他一律 attachment
    /// </summary>
    public static (string ContentType, string? Disposition) Representation(MediaEndpointKind endpoint, MediaReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var safe = MediaHeaderRules.SafeMediaContentType(reply.ContentType);
        switch (endpoint)
        {
            case MediaEndpointKind.Media or MediaEndpointKind.ReplicaMedia:
                if (safe is not null)
                    return (safe, reply.FileName is null ? null : MediaHeaderRules.ContentDisposition(reply.FileName, attachment: false));
                return (OctetStream, MediaHeaderRules.ContentDisposition(reply.FileName, attachment: true));
            case MediaEndpointKind.File or MediaEndpointKind.ReplicaFile:
                if (reply.ContentKind == MediaContentKinds.Text && reply.TotalLength is { } total && total <= MaxTextPreviewBytes)
                    return (TextPlain, MediaHeaderRules.ContentDisposition(reply.FileName, attachment: false));
                return (safe ?? OctetStream, MediaHeaderRules.ContentDisposition(reply.FileName, attachment: true));
            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "縮圖不經這裡");
        }
    }

    /// <summary>
    /// 轉送失敗或裝置拒絕時回瀏覽器的狀態與代碼：來源離線或沒有 media1 503 <c>offline</c>、忙碌 503 <c>busy</c>（加 Retry-After）、
    /// 首位元組逾時 504 <c>timeout</c>、裝置的回覆不合格 502 <c>bad_gateway</c>；裝置的 403 帶它的原因（<c>policy</c>、<c>forbidden</c>、<c>dlp</c>）、
    /// 404 <c>not_found</c>、413 <c>too_large</c>、503 <c>busy</c>
    /// </summary>
    public static (int Status, string Code) Failure(RelayFailure failure, MediaReply? reply) => failure switch
    {
        RelayFailure.Offline => (StatusCodes.Status503ServiceUnavailable, MediaApiErrors.Offline),
        RelayFailure.Busy => (StatusCodes.Status503ServiceUnavailable, MediaApiErrors.Busy),
        RelayFailure.Timeout => (StatusCodes.Status504GatewayTimeout, MediaApiErrors.Timeout),
        RelayFailure.BadReply => (StatusCodes.Status502BadGateway, MediaApiErrors.BadGateway),
        RelayFailure.None => reply?.Status switch
        {
            StatusCodes.Status403Forbidden => (StatusCodes.Status403Forbidden, reply.Error ?? MediaErrorCodes.Forbidden),
            StatusCodes.Status404NotFound => (StatusCodes.Status404NotFound, MediaApiErrors.NotFound),
            StatusCodes.Status413PayloadTooLarge => (StatusCodes.Status413PayloadTooLarge, MediaApiErrors.TooLarge),
            StatusCodes.Status503ServiceUnavailable => (StatusCodes.Status503ServiceUnavailable, MediaApiErrors.Busy),
            _ => (StatusCodes.Status502BadGateway, MediaApiErrors.BadGateway),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "不認得的失敗"),
    };

    /// <summary>
    /// 轉送沒拿到時的記錄層級：來源電腦離線、忙碌、檔案不在（404）、電腦的遠端政策不給（403 <c>policy</c>）是預期中的，記 Information；
    /// 其他（電腦的守門員擋下的 403、逾時、回覆不合格、413、DLP、不認得的狀態）多半是規則或程式的問題，記 Warning
    /// </summary>
    public static LogLevel FailureLogLevel(RelayFailure failure, MediaReply? reply) => failure switch
    {
        RelayFailure.Offline or RelayFailure.Busy => LogLevel.Information,
        RelayFailure.None when reply?.Status is StatusCodes.Status404NotFound or StatusCodes.Status503ServiceUnavailable => LogLevel.Information,
        RelayFailure.None when reply is { Status: StatusCodes.Status403Forbidden, Error: MediaErrorCodes.Policy } => LogLevel.Information,
        _ => LogLevel.Warning,
    };

    /// <summary>忙碌（伺服器或裝置的同時上限）：回應要帶 <c>Retry-After</c></summary>
    public static bool IsBusy(RelayFailure failure, MediaReply? reply) =>
        failure == RelayFailure.Busy || (failure == RelayFailure.None && reply?.Status == StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// <c>/m/</c> 的圖片在來源離線（或沒有 media1）、電腦拒絕（403，例如遠端政策 Off）、原檔已經不在（404，2026-10-27）時改回縮圖
    /// （D29；呼叫端另外確認縮圖存在）。
    /// 是不是圖片：裝置宣告的型別是白名單的影片就不是；沒有型別（離線）時看瀏覽器的 <c>Sec-Fetch-Dest</c>，<c>video</c>、<c>audio</c> 不是，其他算圖片。
    /// 忙碌、逾時與不合格的回覆照回錯誤（暫時的，瀏覽器重試就好）
    /// </summary>
    public static bool FallbackToThumb(RelayFailure failure, MediaReply? reply, string? fetchDest)
    {
        var eligible = failure == RelayFailure.Offline
                       || (failure == RelayFailure.None && reply?.Status is StatusCodes.Status403Forbidden or StatusCodes.Status404NotFound);
        if (!eligible)
            return false;
        var declared = MediaHeaderRules.SafeMediaContentType(reply?.ContentType);
        if (declared is not null)
            return declared.StartsWith("image/", StringComparison.Ordinal);
        return fetchDest?.Trim().ToLowerInvariant() is not ("video" or "audio");
    }

    /// <summary>
    /// 原檔已經不在（來源電腦回 404：回覆引用的截圖被刪掉、又沒有留存的副本）、伺服器也沒有縮圖可退時，瀏覽器的 <c>&lt;img&gt;</c>
    /// （<c>Sec-Fetch-Dest: image</c>）改回一張「圖片已不存在」的佔位圖（2026-10-27：兩天 583 筆瀏覽器回報「媒體載入失敗」、畫面是破圖）。
    /// 只給瀏覽器的 <c>/m/</c>、<c>/t/</c>：桌面複本的代理只收 PNG、JPEG，照舊回錯誤
    /// </summary>
    public static bool MissingPlaceholder(MediaEndpointKind endpoint, RelayFailure failure, MediaReply? reply, string? fetchDest)
        => endpoint is MediaEndpointKind.Media or MediaEndpointKind.Thumb
           && failure == RelayFailure.None && reply?.Status == StatusCodes.Status404NotFound
           && string.Equals(fetchDest?.Trim(), "image", StringComparison.OrdinalIgnoreCase);

    /// <summary>佔位圖的快取：1 小時內同一張不再問來源電腦（原檔放回來最晚 1 小時後看得到）</summary>
    public const string MissingCacheControl = "private, max-age=3600";

    /// <summary>佔位圖的 <c>X-Myriagent-Fallback</c>（退回縮圖是 <c>thumb</c>）</summary>
    public const string MissingFallback = "missing";

    /// <summary>「圖片已不存在」的佔位圖（SVG；半透明的灰，深淺色主題都看得清楚；媒體回應的 CSP 是 sandbox，沒有腳本）</summary>
    public static byte[] MissingImageSvg() => System.Text.Encoding.UTF8.GetBytes(MissingImageSvgText);

    internal const string MissingImageSvgText = """
<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180" viewBox="0 0 320 180"><rect x="0.5" y="0.5" width="319" height="179" rx="10" fill="#8a8f98" fill-opacity="0.08" stroke="#8a8f98" stroke-opacity="0.35"/><g fill="none" stroke="#8a8f98" stroke-width="2" stroke-linejoin="round"><rect x="141" y="50" width="38" height="30" rx="3"/><path d="M145 76l9-10 7 7 5-5 9 8"/><path d="M136 45l48 40"/></g><text x="160" y="114" text-anchor="middle" font-family="system-ui,'Microsoft JhengHei',sans-serif" font-size="15" fill="#8a8f98">圖片已不存在</text><text x="160" y="136" text-anchor="middle" font-family="system-ui,'Microsoft JhengHei',sans-serif" font-size="12" fill="#8a8f98" fill-opacity="0.8">原檔已被刪除或移走</text></svg>
""";

    /// <summary>E2 的 <c>s</c>：只收 160、320（只能一個值）</summary>
    public static bool IsThumbSize(string? size) => size is "160" or "320";

    /// <summary>E2 的 <c>v</c>（抽屜的修改時間）：沒帶可以；帶了只能是 1～19 位數字</summary>
    public static bool IsThumbVersion(string? version) =>
        version is null || (version.Length is > 0 and <= MaxThumbVersionLength && version.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0);

    /// <summary>
    /// <c>If-None-Match</c> 有沒有包含這個實體標籤（逗號分隔的清單、<c>*</c>、弱比較：<c>W/"x"</c> 等於 <c>"x"</c>）
    /// </summary>
    public static bool MatchesETag(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch))
            return false;
        var target = Opaque(etag);
        foreach (var candidate in ifNoneMatch.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (candidate == "*" || string.Equals(Opaque(candidate), target, StringComparison.Ordinal))
                return true;
        }

        return false;

        static string Opaque(string tag) => tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;
    }
}
