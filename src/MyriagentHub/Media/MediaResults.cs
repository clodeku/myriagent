using Microsoft.AspNetCore.Connections;

namespace MyriagentHub.Media;

/// <summary>媒體端點的回應：JSON 一律 <c>Cache-Control: no-store</c>（WP4.7 計畫步 3）</summary>
internal static class MediaResults
{
    public static IResult Json(HttpContext http, int status, object body)
    {
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(body, statusCode: status);
    }

    public static IResult Error(HttpContext http, int status, string code) => Json(http, status, new { error = code });

    public static IResult Error(HttpContext http, (int Status, string Code) failure) => Error(http, failure.Status, failure.Code);

    /// <summary>用戶端已經斷線（取消、連線被重設）：回應送不到，不必寫，也不算伺服器的錯誤</summary>
    public static bool ClientGone(HttpContext http, Exception ex) =>
        http.RequestAborted.IsCancellationRequested || ex is OperationCanceledException or ConnectionResetException;
}
