using System.Buffers;
using System.Security.Cryptography;
using Myriagent.Media;
using MyriagentHub.Auth;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Media;

/// <summary>
/// 瀏覽器上傳（WP4.7 計畫 E4；D13、D21；設計 4.10:1068）：<c>PUT /api/v1/uploads/{uploadId}</c>，擁有者 cookie，
/// 非 GET 的 /api 照常要求本站 Origin 與 <c>X-Myriagent-Csrf: 1</c>（不豁免）。一次 PUT、邊收邊算 SHA-256、≤<see cref="MediaOptions.MaxUploadBytes"/>；
/// 上傳的檔只成為附件、永不執行，所以不做文字 DLP、不解壓。
/// <list type="bullet">
/// <item>新的 201、同一個 id 同一份內容 200（重傳冪等，讀完本文比對雜湊、不再存一份）、不同內容 409，都回 <c>{uploadId, sha256, size}</c> 或 <c>{error}</c>。</item>
/// <item>宣告的大小超過上限 413、待處理合計超過上限 507 <c>pending_full</c>（都在讀本文之前）；blob 配額或磁碟下限 507 <c>insufficient_storage</c>。</item>
/// <item>上傳 id 照 <see cref="UploadIdRules"/>（和遠端指令的 <c>uploads</c> 同一份），不合格 400 <c>invalid</c>。</item>
/// </list>
/// </summary>
internal static class UploadEndpoints
{
    private const int HashBufferBytes = 256 * 1024;

    /// <summary>
    /// 瀏覽器的 E4，與桌面遠端操作的裝置上傳（sync-protocol 14.7：<c>PUT /api/v1/device/uploads/{uploadId}</c>，存取權杖、免 CSRF；同一個
    /// <see cref="UploadRegistry"/> 與上限，之後的 <c>addUploaded</c> 經 <c>PeerCmd</c> 送）
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, RouteGroupBuilder device)
    {
        app.MapPut("/api/v1/uploads/{uploadId}", PutAsync).RequireAuthorization(HubPolicies.Browser);
        device.MapPut(DeviceRoute, PutAsync).CsrfExempt();
    }

    /// <summary>桌面遠端操作的裝置上傳（在 <c>/api/v1/device</c> 底下）</summary>
    public const string DeviceRoute = "/uploads/{uploadId}";

    private static async Task<IResult> PutAsync(string uploadId, HttpContext http, BlobStore store, UploadRegistry registry, IOptions<HubOptions> options,
        ILoggerFactory logs)
    {
        if (!UploadIdRules.IsValid(uploadId))
            return MediaResults.Error(http, StatusCodes.Status400BadRequest, MediaApiErrors.Invalid);
        var max = options.Value.Media.MaxUploadBytes;
        var declared = http.Request.ContentLength;
        if (!RequestBodyLimits.TryAllow(http, max))
            return MediaResults.Error(http, StatusCodes.Status413PayloadTooLarge, MediaApiErrors.TooLarge);

        var decision = registry.Begin(uploadId, declared);
        if (MediaApiRules.BeginFailure(decision.Kind) is { } refused)
            return MediaResults.Error(http, refused);

        var log = logs.CreateLogger(typeof(UploadEndpoints).FullName!);
        var owned = decision.Kind == UploadPutKind.New;
        var completed = false;
        try
        {
            if (decision.Kind == UploadPutKind.Existing)
                return await RetransmitAsync(http, uploadId, decision.Sha256!, decision.Size);

            var result = await store.PutStreamingAsync(null, declared, max, BlobKinds.Upload, contentType: null, http.Request.Body, inspect: null, http.RequestAborted);
            if (MediaApiRules.PutFailure(result.Outcome, result.Error) is { } failure)
                return MediaResults.Error(http, failure);
            await registry.CompleteAsync(uploadId, result.Sha256!, result.Length, CancellationToken.None);
            completed = true;
            return MediaResults.Json(http, StatusCodes.Status201Created, new { uploadId, sha256 = result.Sha256, size = result.Length });
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
            log.LogError(ex, "存瀏覽器上傳的檔失敗：{Upload}", uploadId);
            return MediaResults.Error(http, StatusCodes.Status503ServiceUnavailable, MediaApiErrors.Unavailable);
        }
        finally
        {
            if (owned && !completed)
                registry.Abort(uploadId);
        }
    }

    /// <summary>已上傳過的 id：讀完本文算雜湊（不落地），和登記的相同回 200、不同回 409</summary>
    private static async Task<IResult> RetransmitAsync(HttpContext http, string uploadId, string sha256, long size)
    {
        var (sha, total) = await HashAsync(http.Request.Body, size, http.RequestAborted);
        if (total != size || !string.Equals(sha, sha256, StringComparison.Ordinal))
            return MediaResults.Error(http, StatusCodes.Status409Conflict, MediaApiErrors.Conflict);
        return MediaResults.Json(http, StatusCodes.Status200OK, new { uploadId, sha256, size });
    }

    /// <summary>算本文的 SHA-256，最多讀 <paramref name="limit"/>＋1 個位元組（超過就停，回的 total 比 limit 大）</summary>
    private static async Task<(string? Sha, long Total)> HashAsync(Stream body, long limit, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(HashBufferBytes);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            while (true)
            {
                var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit + 1 - total)), cancellationToken);
                if (read == 0)
                    break;
                total += read;
                if (total > limit)
                    return (null, total);
                hash.AppendData(buffer, 0, read);
            }

            return (Convert.ToHexStringLower(hash.GetHashAndReset()), total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
