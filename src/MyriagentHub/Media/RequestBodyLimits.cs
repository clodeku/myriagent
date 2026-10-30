using Microsoft.AspNetCore.Http.Features;

namespace MyriagentHub.Media;

/// <summary>
/// 個別端點放寬 Kestrel 的本文上限（WP4.7 計畫 D13）：全域上限 1 MB（<c>HubApplication.ConfigureHubKestrel</c>）照舊，
/// 上傳（512 MB）、項目 blob（4 MB）等端點在讀本文之前把這個請求的上限放寬到端點自己的上限。
/// 計數分兩層：宣告的 <c>Content-Length</c> 超過就在讀本文之前回 413（<see cref="TryAllow"/>），沒有宣告長度（chunked）時
/// 由 <see cref="BlobStore.PutStreamingAsync"/> 讀到上限＋1 個位元組就停（Kestrel 在同一個上限也會擋，兩邊都回 413）
/// </summary>
internal static class RequestBodyLimits
{
    /// <summary>宣告的長度超過上限（沒宣告不算）</summary>
    public static bool Exceeds(long? declaredLength, long maxBytes) => declaredLength > maxBytes;

    /// <summary>
    /// 宣告的長度超過 <paramref name="maxBytes"/> 回 false（呼叫端回 413，不讀本文）；否則把這個請求的 Kestrel 本文上限放寬到 <paramref name="maxBytes"/>
    /// 並回 true。沒有這個功能（TestServer）或已經開始讀本文（唯讀）時不改
    /// </summary>
    public static bool TryAllow(HttpContext context, long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        if (Exceeds(context.Request.ContentLength, maxBytes))
            return false;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = maxBytes;
        return true;
    }
}
