using System.Globalization;
using System.Text;
using Myriagent.Hub;
using Myriagent.Media;
using Myriagent.Sync;

namespace Myriagent.UI;

/// <summary>AgentWebView 的複本媒體路由比對結果</summary>
internal enum ReplicaRouteKind
{
    /// <summary>不是 <c>/m/</c>、<c>/t/</c>（照原本的路由）</summary>
    None,

    /// <summary>是複本的路由但鍵或大小不合格（直接 404，來源不呼叫）</summary>
    Invalid,

    /// <summary>合格：<see cref="ReplicaRoute.Key"/>、<see cref="ReplicaRoute.Purpose"/>、<see cref="ReplicaRoute.Size"/> 有值</summary>
    Valid,
}

/// <summary>複本媒體路由（<see cref="ReplicaMediaRouteRules.Match"/> 的結果）</summary>
internal readonly record struct ReplicaRoute(ReplicaRouteKind Kind, string? Key = null, string? Purpose = null, int? Size = null)
{
    public static ReplicaRoute None { get; } = new(ReplicaRouteKind.None);

    public static ReplicaRoute Invalid { get; } = new(ReplicaRouteKind.Invalid);
}

/// <summary>
/// AgentWebView 的複本媒體路由（WP4.7 計畫步 12，D38）的純函式：
/// <list type="bullet">
/// <item><c>/m/{鍵}</c>（原檔，可帶 Range）與 <c>/t/{鍵}?s=160|320</c>（縮圖），網址由 <see cref="ReplicaMediaUrls"/> 產生；比對大小寫有別、
/// 路徑後面不能再有字，鍵要完全合格（<see cref="MediaKeyRules.TryParse"/>）。</item>
/// <item>來源回的內容變成 WebView2 的回應（<see cref="Headers"/>）：只收 200、206、416，一律 <c>no-store</c> 與 <c>nosniff</c>；
/// Content-Type 與 Content-Range 有換行就不收（標頭注入）。</item>
/// </list>
/// 本機的 <c>/media</c>、<c>/thumb</c>、<c>/open</c> 不經這裡
/// </summary>
internal static class ReplicaMediaRouteRules
{
    public const string MediaRoute = "/m/";

    public const string ThumbRoute = "/t/";

    /// <summary>比對網址（只看路徑與 <c>s</c>；主機由呼叫端先確認是本頁來源）</summary>
    public static ReplicaRoute Match(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var path = uri.AbsolutePath;
        var isMedia = path.StartsWith(MediaRoute, StringComparison.Ordinal);
        if (!isMedia && !path.StartsWith(ThumbRoute, StringComparison.Ordinal))
            return ReplicaRoute.None;
        var key = path[MediaRoute.Length..];
        if (!MediaKeyRules.TryParse(key, out _, out _))
            return ReplicaRoute.Invalid;
        if (isMedia)
            return new ReplicaRoute(ReplicaRouteKind.Valid, key, MediaPurposes.Media);
        return AgentWebView.GetQueryValue(uri, "s") switch
        {
            "160" => new ReplicaRoute(ReplicaRouteKind.Valid, key, MediaPurposes.Thumb, ReplicaMediaRules.SmallThumb),
            "320" => new ReplicaRoute(ReplicaRouteKind.Valid, key, MediaPurposes.Thumb, ReplicaMediaRules.LargeThumb),
            _ => ReplicaRoute.Invalid,
        };
    }

    /// <summary>狀態碼的說明（只有三種；其他回 null）</summary>
    public static string? ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        206 => "Partial Content",
        416 => "Range Not Satisfiable",
        _ => null,
    };

    /// <summary>
    /// 回應標頭（CRLF 分隔，WebView2 的格式）：Content-Type、Content-Length、206／416 的 Content-Range、原檔的 Accept-Ranges、no-store、nosniff。
    /// 狀態碼不是 200／206／416、206／416 沒有 Content-Range、標頭值有換行回 null（404）
    /// </summary>
    public static string? Headers(ReplicaMediaReply reply, bool isMedia)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (ReasonPhrase(reply.Status) is null || HasLineBreak(reply.ContentType) || HasLineBreak(reply.ContentRange))
            return null;
        var partial = reply.Status is 206 or 416;
        if (partial && string.IsNullOrEmpty(reply.ContentRange))
            return null;
        var headers = new StringBuilder();
        headers.Append(CultureInfo.InvariantCulture, $"Content-Type: {reply.ContentType}\r\nContent-Length: {reply.Body.Length}");
        if (partial)
            headers.Append(CultureInfo.InvariantCulture, $"\r\nContent-Range: {reply.ContentRange}");
        if (isMedia)
            headers.Append("\r\nAccept-Ranges: bytes");
        headers.Append("\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
        return headers.ToString();
    }

    private static bool HasLineBreak(string? value) => value is not null && value.AsSpan().IndexOfAny('\r', '\n') >= 0;
}
