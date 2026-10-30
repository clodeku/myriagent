using Myriagent.Media;
using MyriagentHub.Auth;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Media;

/// <summary>
/// 裝置的 blob 端點（WP4.7 計畫 E5、E6；D13、D15）：只收存取權杖 <c>cha_</c>（<see cref="HubPolicies.DeviceAccess"/>），
/// 不用 cookie，所以 PUT 不要求 CSRF 標頭（<c>.CsrfExempt()</c>）。blob 以 SHA-256 定址：任何一台已證明的裝置都可以取任何 blob（自用、伺服器可信任）。
/// <list type="bullet">
/// <item>E5 <c>PUT /api/v1/device/blobs/{sha256}</c>：項目欄位的 blob，Content-Length 必填、≤<see cref="MediaOptions.MaxItemBlobBytes"/>；
/// 存入前過第二道 DLP（<see cref="ItemBlobInspector"/>）。新存 201、已存在 200（不讀本文），回 <c>{sha256, length, existed}</c>。</item>
/// <item>E5 <c>HEAD</c>：列與檔都在 200（Content-Length 是大小）、其他 404（有列沒檔也是：裝置會重傳）。</item>
/// <item>E6 <c>GET</c>：已有的 blob，單一區間 Range（206／416，<see cref="HttpRangeRules"/>），<c>application/octet-stream</c>、<c>no-store</c>。</item>
/// </list>
/// </summary>
internal static class BlobEndpoints
{
    /// <summary>路由參數只收 64 個十六進位（路由的正規式不分大小寫，處理常式再以 <see cref="BlobIdRules.IsBlobId"/> 只收小寫）</summary>
    public const string Route = "/blobs/{sha256:regex(^[0-9a-f]{{64}}$)}";

    private const string OctetStream = "application/octet-stream";

    private const int CopyBufferBytes = 64 * 1024;

    public static void Map(RouteGroupBuilder device)
    {
        device.MapPut(Route, PutAsync).CsrfExempt();
        device.MapMethods(Route, [HttpMethods.Head], Head);
        device.MapGet(Route, Get);
    }

    private static async Task<IResult> PutAsync(string sha256, HttpContext http, BlobStore store, ItemBlobInspector inspector, IOptions<HubOptions> options,
        ILoggerFactory logs)
    {
        var log = logs.CreateLogger(typeof(BlobEndpoints).FullName!);
        if (!BlobIdRules.IsBlobId(sha256))
            return MediaResults.Error(http, StatusCodes.Status404NotFound, MediaApiErrors.NotFound);
        if (http.Request.ContentLength is not { } length)
            return MediaResults.Error(http, StatusCodes.Status411LengthRequired, MediaApiErrors.LengthRequired);
        var max = options.Value.Media.MaxItemBlobBytes;
        if (!RequestBodyLimits.TryAllow(http, max))
            return MediaResults.Error(http, StatusCodes.Status413PayloadTooLarge, MediaApiErrors.TooLarge);

        try
        {
            var result = await store.PutStreamingAsync(sha256, length, max, BlobKinds.ItemBody, contentType: null, http.Request.Body, inspector.InspectAsync,
                http.RequestAborted);
            if (MediaApiRules.PutFailure(result.Outcome, result.Error) is { } failure)
            {
                if (result.Outcome == BlobPutOutcome.Rejected)
                    log.LogWarning("拒收裝置的項目 blob（{Code}）：裝置 {Device}、{Length} 位元組", failure.Code, http.User.FindFirst(HubClaims.DeviceId)?.Value, result.Length);
                return MediaResults.Error(http, failure);
            }

            var existed = result.Outcome == BlobPutOutcome.Exists;
            return MediaResults.Json(http, existed ? StatusCodes.Status200OK : StatusCodes.Status201Created,
                new { sha256 = result.Sha256, length = result.Length, existed });
        }
        catch (BadHttpRequestException ex)
        {
            return MediaResults.Error(http, ex.StatusCode, MediaApiRules.BadRequestCode(ex.StatusCode));
        }
        catch (Exception ex) when (MediaResults.ClientGone(http, ex))
        {
            return Results.Empty;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "存裝置的項目 blob 失敗：{Sha}", sha256);
            return MediaResults.Error(http, StatusCodes.Status503ServiceUnavailable, MediaApiErrors.Unavailable);
        }
    }

    private static IResult Head(string sha256, HttpContext http, BlobStore store)
    {
        http.Response.Headers.CacheControl = "no-store";
        // 列與檔都在才算有（同 PUT 的「已存在」）：有列沒檔時回 404，裝置會 PUT、把檔補回來（合併前審查 DI-2）
        if (store.SizeOnDisk(sha256) is not { } size)
            return Results.StatusCode(StatusCodes.Status404NotFound);
        http.Response.ContentLength = size;
        http.Response.ContentType = OctetStream;
        return Results.Empty;
    }

    private static IResult Get(string sha256, HttpContext http, BlobStore store, ILoggerFactory logs)
    {
        if (store.SizeOf(sha256) is null)
            return MediaResults.Error(http, StatusCodes.Status404NotFound, MediaApiErrors.NotFound);
        var content = store.OpenRead(sha256);
        if (content is null)
        {
            logs.CreateLogger(typeof(BlobEndpoints).FullName!).LogWarning("blob 有列沒檔：{Sha}", sha256);
            return MediaResults.Error(http, StatusCodes.Status404NotFound, MediaApiErrors.NotFound);
        }

        var length = content.Length;
        var (kind, range, _) = HttpRangeRules.Parse(http.Request.Headers.Range.ToString(), length);
        http.Response.Headers.AcceptRanges = "bytes";
        http.Response.Headers.CacheControl = "no-store";
        switch (kind)
        {
            case RangeKind.Unsatisfiable:
                content.Dispose();
                http.Response.Headers.ContentRange = HttpRangeRules.Unsatisfied(length);
                return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
            case RangeKind.Satisfiable or RangeKind.Suffix:
                http.Response.Headers.ContentRange = HttpRangeRules.ContentRange(range, length);
                return new RangeResult(content, StatusCodes.Status206PartialContent, range.Start, range.Length);
            default:
                return new RangeResult(content, StatusCodes.Status200OK, 0, length);
        }
    }

    /// <summary>從 <paramref name="start"/> 起寫 <paramref name="count"/> 個位元組（寫完或失敗都關掉 <paramref name="content"/>）</summary>
    private sealed class RangeResult(Stream content, int status, long start, long count) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            await using (content)
            {
                var response = http.Response;
                response.StatusCode = status;
                response.ContentType = OctetStream;
                response.ContentLength = count;
                if (count == 0)
                    return;
                content.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[(int)Math.Min(CopyBufferBytes, count)];
                var left = count;
                try
                {
                    while (left > 0)
                    {
                        var read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), http.RequestAborted);
                        if (read == 0)
                            throw new IOException("blob 檔比記錄的短");
                        await response.Body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
                        left -= read;
                    }
                }
                catch (Exception ex) when (MediaResults.ClientGone(http, ex))
                {
                    // 用戶端中斷下載：不算伺服器的錯誤
                }
            }
        }
    }
}

/// <summary>
/// 項目 blob 的第二道 DLP（D15）：一次只檢查一個（<see cref="SemaphoreSlim"/>），讀暫存檔（≤4 MB）跑 <see cref="BlobInspectRules.CheckItemBody"/>。
/// 收本文不在鎖裡（慢的裝置不擋別台），解碼與掃描的記憶體同時只有一份
/// </summary>
internal sealed class ItemBlobInspector
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<string?> InspectAsync(string tmpPath, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var bytes = await File.ReadAllBytesAsync(tmpPath, cancellationToken);
            return BlobInspectRules.CheckItemBody(bytes);
        }
        finally
        {
            gate.Release();
        }
    }
}
