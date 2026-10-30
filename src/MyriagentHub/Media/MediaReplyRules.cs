using System.Collections.Frozen;
using System.Globalization;
using Myriagent.Media;
using Myriagent.Sync;

namespace MyriagentHub.Media;

/// <summary>裝置以 <c>PUT /api/v1/device/relay/{relayId}</c> 回覆轉送時，請求標頭的原始值（<see cref="MediaRelayHeaders"/>；都是外部輸入）</summary>
/// <param name="ContentLength">PUT 的 <c>Content-Length</c>（沒宣告是 null）</param>
internal sealed record MediaReplyHeaders(string? Status, string? TotalLength, string? ContentRange, long? ContentLength,
    string? ContentType, string? ETag, string? FileName, string? ContentKind, string? Error);

/// <summary>驗過的裝置回覆（<see cref="MediaReplyRules.Parse"/>）</summary>
/// <param name="Status">裝置判斷的狀態碼：200、206、403、404、413、416、503</param>
/// <param name="TotalLength">檔案的總長（200、206、416 一定有）</param>
/// <param name="Range">206 的範圍（其他是 null）</param>
/// <param name="BodyLength">本文的位元組數（200、206 以外是 0）</param>
/// <param name="ContentType">裝置宣告的型別（還沒經白名單；只保證是可印的 ASCII、≤255 字）</param>
/// <param name="ETag">實體標籤（格式不合格的已丟掉）</param>
/// <param name="FileName">解碼並清理過的檔名（<see cref="MediaHeaderRules.DecodeFileName"/>）</param>
/// <param name="ContentKind"><see cref="MediaContentKinds.Text"/> 或 <see cref="MediaContentKinds.Binary"/>；不認得的是 null</param>
/// <param name="Error">拒絕的原因（<see cref="MediaErrorCodes"/>；不認得的是 null）</param>
internal sealed record MediaReply(int Status, long? TotalLength, ByteRange? Range, long BodyLength, string? ContentType, string? ETag,
    string? FileName, string? ContentKind, string? Error)
{
    public bool HasBody => Status is StatusCodes.Status200OK or StatusCodes.Status206PartialContent;
}

/// <summary>
/// 裝置回覆轉送的標頭檢查（WP4.7 計畫 D12；設計 4.10）。伺服器送的 <c>MediaRequest</c> 決定能收哪些回覆：
/// <list type="bullet">
/// <item>沒帶範圍（縮圖）：200（整份，本文 ≤ 上限、<c>Content-Length</c> 等於 <c>X-Total-Length</c>）或拒絕。</item>
/// <item>帶範圍（原檔與檔案一律帶，每段 ≤8 MB）：206（<c>Content-Range</c> 的起點等於要的、結尾正好是 min(要的結尾, 總長 − 1)，
/// <c>Content-Length</c> 等於範圍長度）、416（起點不在檔案裡）或拒絕。</item>
/// <item>拒絕（403、404、413、503）不帶本文。</item>
/// </list>
/// 不合格一律 null（伺服器回瀏覽器 502、不轉發任何位元組）。純函式
/// </summary>
internal static class MediaReplyRules
{
    public const int MaxContentTypeLength = 255;

    public const int MaxETagLength = 128;

    private static readonly FrozenSet<int> Statuses = new[]
    {
        StatusCodes.Status200OK, StatusCodes.Status206PartialContent, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound,
        StatusCodes.Status413PayloadTooLarge, StatusCodes.Status416RangeNotSatisfiable, StatusCodes.Status503ServiceUnavailable,
    }.ToFrozenSet();

    private static readonly FrozenSet<string> ErrorCodes = new[]
    {
        MediaErrorCodes.NotFound, MediaErrorCodes.Policy, MediaErrorCodes.Forbidden, MediaErrorCodes.TooLarge, MediaErrorCodes.Dlp,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <param name="requested">伺服器送出的範圍（null＝整份，縮圖）</param>
    /// <param name="maxBodyBytes">這個轉送的本文上限（原檔與檔案 8 MB、縮圖 <see cref="MediaOptions.MaxThumbBytes"/>）</param>
    public static MediaReply? Parse(MediaReplyHeaders headers, MediaRange? requested, long maxBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (!TryNumber(headers.Status, out var statusValue) || statusValue > int.MaxValue || !Statuses.Contains((int)statusValue))
            return null;
        var status = (int)statusValue;
        long? total = null;
        if (headers.TotalLength is not null)
        {
            if (!TryNumber(headers.TotalLength, out var parsed))
                return null;
            total = parsed;
        }

        var contentType = Printable(headers.ContentType, MaxContentTypeLength);
        var etag = IsETag(headers.ETag) ? headers.ETag!.Trim() : null;
        var fileName = MediaHeaderRules.DecodeFileName(headers.FileName);
        var kind = headers.ContentKind is MediaContentKinds.Text or MediaContentKinds.Binary ? headers.ContentKind : null;
        var bodyless = headers.ContentLength is null or 0 && string.IsNullOrEmpty(headers.ContentRange);

        switch (status)
        {
            case StatusCodes.Status200OK:
                if (requested is not null || total is not { } whole || whole > maxBodyBytes || headers.ContentLength != whole
                    || !string.IsNullOrEmpty(headers.ContentRange))
                    return null;
                return new MediaReply(status, whole, null, whole, contentType, etag, fileName, kind, null);

            case StatusCodes.Status206PartialContent:
                if (requested is null || total is not { } length
                    || !HttpRangeRules.TryParseContentRange(headers.ContentRange, out var range, out var rangeTotal) || rangeTotal != length)
                    return null;
                var expectedEnd = Math.Min(requested.End ?? long.MaxValue, length - 1);
                if (range.Start != requested.Start || range.End != expectedEnd || range.Length > maxBodyBytes || headers.ContentLength != range.Length)
                    return null;
                return new MediaReply(status, length, range, range.Length, contentType, etag, fileName, kind, null);

            case StatusCodes.Status416RangeNotSatisfiable:
                if (requested is null || total is not { } size || requested.Start < size || !bodyless)
                    return null;
                return new MediaReply(status, size, null, 0, contentType, etag, fileName, kind, null);

            default:
                if (!bodyless)
                    return null;
                var error = headers.Error is { } code && ErrorCodes.Contains(code) ? code : null;
                return new MediaReply(status, total, null, 0, contentType, null, fileName, kind, error);
        }
    }

    /// <summary>
    /// 實體標籤：<c>"…"</c> 或 <c>W/"…"</c>，引號裡只有可印的 ASCII（不含引號與空白），≤128 字（RFC 9110 的 entity-tag）
    /// </summary>
    public static bool IsETag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var tag = value.Trim();
        if (tag.Length > MaxETagLength)
            return false;
        var opaque = tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;
        if (opaque.Length < 2 || opaque[0] != '"' || opaque[^1] != '"')
            return false;
        foreach (var c in opaque.AsSpan(1, opaque.Length - 2))
        {
            if (c is < (char)0x21 or > (char)0x7E or '"')
                return false;
        }

        return true;
    }

    /// <summary>可印的 ASCII（含空白）、去掉前後空白、≤<paramref name="max"/> 字；其他 null</summary>
    private static string? Printable(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed.Length > max)
            return null;
        foreach (var c in trimmed)
        {
            if (c is < ' ' or > (char)0x7E)
                return null;
        }

        return trimmed;
    }

    private static bool TryNumber(string? text, out long value)
    {
        value = 0;
        return text is not null && long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
