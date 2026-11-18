using System.Text;
using MyriagentHub.Hosting;

namespace MyriagentHub.Downloads;

/// <summary>
/// 目前放著的安裝檔（<see cref="HubPaths.PublicDownloadsDir"/>：說明檔 latest.json 與它寫的那一個 exe）與同時下載的上限。
/// 檔案由管理員手動放進資料夾（先放 exe、再換 latest.json），服務只讀；沒有說明檔、說明檔不合格、檔案不在或大小不符時當成沒有。
/// </summary>
internal sealed class PublicDownloadStore(HubPaths paths)
{
    /// <summary>同時下載的上限：不讓匿名下載把主機的上傳頻寬佔滿（超過的回 503，請稍後再試）</summary>
    public const int MaxConcurrent = 4;

    private int active;

    public (PublicDownloadInfo Info, string Path)? Current()
    {
        try
        {
            var infoFile = new FileInfo(Path.Combine(paths.PublicDownloadsDir, PublicDownloadRules.InfoFileName));
            if (!infoFile.Exists || infoFile.Length > PublicDownloadRules.MaxInfoBytes)
                return null;
            if (PublicDownloadRules.Parse(File.ReadAllText(infoFile.FullName, Encoding.UTF8)) is not { } info)
                return null;
            var file = new FileInfo(Path.Combine(paths.PublicDownloadsDir, info.File));
            return file.Exists && file.Length == info.Size ? (info, file.FullName) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool TryEnter()
    {
        if (Interlocked.Increment(ref active) <= MaxConcurrent)
            return true;
        Interlocked.Decrement(ref active);
        return false;
    }

    public void Leave() => Interlocked.Decrement(ref active);
}

internal static class PublicDownloadEndpoints
{
    public const string Route = "/myriagent";

    public static IServiceCollection AddHubDownloads(this IServiceCollection services) => services.AddSingleton<PublicDownloadStore>();

    /// <summary>
    /// 安裝檔的下載頁：不用登入（匿名；和其他未認證的請求一樣受每個 IP 的總量限流）。<c>GET /myriagent</c> 下載頁、
    /// <c>GET /myriagent/latest.json</c> 目前的版本（file、version、size、sha256）、<c>GET /myriagent/download/{file}</c> 安裝檔本身
    /// （只認說明檔寫的那一個檔名，支援續傳）。沒有放安裝檔時三個都回 404——沒放檔的伺服器等於沒有這一頁。
    /// </summary>
    public static void MapHubDownloads(this WebApplication app)
    {
        app.MapGet(Route, (PublicDownloadStore store) => store.Current() is { } current
                ? Results.Content(PublicDownloadRules.Page(current.Info), "text/html; charset=utf-8")
                : Results.NotFound())
            .AllowAnonymous();
        app.MapGet(Route + "/latest.json", (PublicDownloadStore store) => store.Current() is { } current
                ? Results.Json(new { file = current.Info.File, version = current.Info.Version, size = current.Info.Size, sha256 = current.Info.Sha256 })
                : Results.NotFound())
            .AllowAnonymous();
        app.MapGet(Route + "/download/{file}", async (string file, HttpContext http, PublicDownloadStore store) =>
            {
                if (store.Current() is not { } current || !string.Equals(file, current.Info.File, StringComparison.Ordinal))
                    return Results.NotFound();
                if (!store.TryEnter())
                {
                    http.Response.Headers.RetryAfter = "30";
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                try
                {
                    // 檔名帶版本、內容不會變：可以快取；ETag 用說明檔的雜湊（續傳靠它確認還是同一個檔）
                    http.Response.Headers.CacheControl = "public, max-age=3600";
                    await Results.File(current.Path, "application/octet-stream", current.Info.File,
                        entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue("\"" + current.Info.Sha256 + "\""), enableRangeProcessing: true).ExecuteAsync(http);
                    return Results.Empty;
                }
                finally
                {
                    store.Leave();
                }
            })
            .AllowAnonymous();
    }
}
