using System.Buffers;
using System.Security.Cryptography;
using Myriagent.Diagnostics;
using Myriagent.Media;
using Myriagent.Sync;
using MyriagentHub.Auth;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace MyriagentHub.Media;

/// <summary>
/// 媒體轉送的端點（WP4.7 計畫 3.1）：
/// <list type="bullet">
/// <item>E1 <c>GET /m/{key}</c>、E3 <c>GET /f/{key}</c>、E2 <c>GET /t/{key}?s=160|320[&amp;v=]</c>：擁有者 cookie（GET 不改狀態，不必 CSRF 標頭）。</item>
/// <item>E7 <c>PUT /api/v1/device/relay/{relayId}</c>：裝置回覆轉送（只收存取權杖 <c>cha_</c>，不用 cookie，所以豁免 CSRF 標頭）。</item>
/// <item>E9 <c>GET /api/v1/device/media/{key}?purpose=media|thumb[&amp;s=]</c>：桌面複本的代理（存取權杖），邏輯同 E1／E2、一律 no-store。</item>
/// </list>
/// 媒體專用的 sandbox CSP 由 <see cref="SecurityHeaderRules.ContentSecurityPolicyFor"/> 依路徑決定，<c>nosniff</c> 照安全標頭中介程式
/// </summary>
internal static class MediaEndpoints
{
    public const string RelayRoute = "/relay/{relayId}";

    public const string ReplicaRoute = "/media/{key}";

    public static void Map(WebApplication app, RouteGroupBuilder device)
    {
        app.MapGet("/m/{key}", (string key, HttpContext http, MediaResponder responder) => responder.ServeOriginalAsync(http, MediaEndpointKind.Media, key))
            .RequireAuthorization(HubPolicies.Browser);
        app.MapGet("/t/{key}", (string key, HttpContext http, MediaResponder responder) => responder.ServeThumbAsync(http, MediaEndpointKind.Thumb, key))
            .RequireAuthorization(HubPolicies.Browser);
        app.MapGet("/f/{key}", (string key, HttpContext http, MediaResponder responder) => responder.ServeOriginalAsync(http, MediaEndpointKind.File, key))
            .RequireAuthorization(HubPolicies.Browser);
        device.MapPut(RelayRoute, RelayAsync).CsrfExempt();
        device.MapGet(ReplicaRoute, ReplicaAsync);
    }

    private static Task<IResult> RelayAsync(string relayId, HttpContext http, MediaRelay relay)
    {
        if (http.User.FindFirst(HubClaims.DeviceId)?.Value is not { } deviceId)
            return Task.FromResult(MediaResults.Error(http, StatusCodes.Status404NotFound, MediaApiErrors.NotFound));
        return relay.AcceptAsync(deviceId, relayId, http);
    }

    private static Task ReplicaAsync(string key, HttpContext http, MediaResponder responder)
    {
        var purpose = http.Request.Query["purpose"];
        return (purpose.Count == 1 ? purpose[0] : null) switch
        {
            MediaPurposes.Media => responder.ServeOriginalAsync(http, MediaEndpointKind.ReplicaMedia, key),
            MediaPurposes.Thumb => responder.ServeThumbAsync(http, MediaEndpointKind.ReplicaThumb, key),
            // 桌面遠端操作（sync-protocol 14.7）：fileFetch 的 fileRef（同 /f/）
            MediaPurposes.File => responder.ServeOriginalAsync(http, MediaEndpointKind.ReplicaFile, key),
            _ => MediaResults.Error(http, StatusCodes.Status400BadRequest, MediaApiErrors.Invalid).ExecuteAsync(http),
        };
    }
}

/// <summary>伺服器手上的縮圖（<c>thumb_blob</c> 或快取）</summary>
/// <param name="ETag">只有 blob 有（<c>"{sha256}"</c>，伺服器自己處理 304，D10）</param>
internal sealed record ThumbImage(byte[] Data, string ContentType, string? ETag);

/// <summary>
/// 媒體請求的處理（WP4.7 計畫 D7、D10、D11、D29）：查參照（<see cref="MediaLookup"/>，查不到一律 404）→ 依 Range 向來源電腦要位元組
/// （<see cref="MediaRelay"/>，每段 ≤8 MB）→ 檢查過的標頭與本文轉給瀏覽器。
/// <list type="bullet">
/// <item>單一區間：開放式或超過 8 MB 的縮成 8 MB，回 206 與實際的 <c>Content-Range</c>；後綴範圍先以 <c>(0,0)</c> 問總長。</item>
/// <item>沒帶 Range：以多段 ≤8 MB 接成一個 200（總長 ≤64 MB，<c>/f/</c> 的檔案下載 ≤512 MB，超過 413）；每段的 ETag 與總長要和第一段相同，否則中止連線（不讓截斷的內容被當成完整的）。</item>
/// <item>裝置的本文提早結束或長度不符：中止給瀏覽器的連線（不是正常結束）。</item>
/// <item><c>/m/</c> 的圖片在來源離線、電腦拒絕（403）時退回縮圖（<c>no-store</c>、<c>X-Myriagent-Fallback: thumb</c>）；影片照回錯誤。</item>
/// <item>縮圖：<c>thumb_blob</c>（ETag＝SHA-256、自己回 304）→ 快取 → 轉送（完整、魔數正確才寫進快取）。</item>
/// </list>
/// </summary>
internal sealed class MediaResponder(MediaLookup lookup, MediaRelay relay, ThumbCache cache, BlobStore blobs, IOptions<HubOptions> options, TimeProvider time,
    ILogger<MediaResponder> log, CollaboratorDirectory? collaborators = null)
{
    private const int CopyBufferBytes = 64 * 1024;

    /// <summary>記錄裡的鍵最多幾個字（不合格的鍵是網址裡任意的字串）</summary>
    private const int MaxLoggedKeyLength = 80;

    /// <summary>
    /// 查不到與轉送失敗的記錄：同一個鍵＋端點＋原因 10 分鐘內只記一次（網頁每次重畫都再要一次同一張破圖；2026-10-23 之前這兩條路完全不記，
    /// 網頁版的破圖在伺服器上查不到原因）
    /// </summary>
    private readonly LogThrottle throttle = new(TimeSpan.FromMinutes(10), time);

    /// <summary>E1、E3、E9 的 media</summary>
    public async Task ServeOriginalAsync(HttpContext http, MediaEndpointKind endpoint, string key)
    {
        try
        {
            if (await FindAsync(http, endpoint, key) is not { } target)
            {
                await NotFoundAsync(http);
                return;
            }

            var purpose = MediaLookupRules.RequestPurpose(endpoint);
            var (kind, range, suffix) = HttpRangeRules.Parse(Single(http.Request.Headers.Range), length: null);
            switch (kind)
            {
                case RangeKind.Satisfiable:
                    await ServeRangeAsync(http, endpoint, target, purpose, range.Start, range.IsOpenEnded ? null : range.End);
                    break;
                case RangeKind.Suffix:
                    await ServeSuffixAsync(http, endpoint, target, purpose, suffix);
                    break;
                default:
                    if (http.Request.Headers.IfNoneMatch.Count > 0 && await TryRevalidateAsync(http, endpoint, target, purpose))
                        break;
                    await ServeWholeAsync(http, endpoint, target, purpose);
                    break;
            }
        }
        catch (Exception ex) when (MediaResults.ClientGone(http, ex))
        {
            // 瀏覽器離開：轉送已在 Dispose 時收掉（裝置的 PUT 收到 410）
        }
    }

    /// <summary>E2、E9 的 thumb</summary>
    public async Task ServeThumbAsync(HttpContext http, MediaEndpointKind endpoint, string key)
    {
        var query = http.Request.Query;
        var size = query["s"];
        var versions = query["v"];
        var version = versions.Count == 1 ? versions[0] : null;
        if (size.Count != 1 || !MediaServeRules.IsThumbSize(size[0]) || versions.Count > 1 || !MediaServeRules.IsThumbVersion(version))
        {
            await MediaResults.Error(http, StatusCodes.Status400BadRequest, MediaApiErrors.Invalid).ExecuteAsync(http);
            return;
        }

        try
        {
            if (await FindAsync(http, endpoint, key) is not { } target)
            {
                await NotFoundAsync(http);
                return;
            }

            if (target.ThumbBlob is { } sha && ReadThumbBlob(sha) is { } stored)
            {
                http.Response.Headers.ETag = stored.ETag;
                http.Response.Headers.CacheControl = MediaServeRules.CacheControl(endpoint);
                if (MediaServeRules.MatchesETag(http.Request.Headers.IfNoneMatch.ToString(), stored.ETag!))
                {
                    http.Response.StatusCode = StatusCodes.Status304NotModified;
                    return;
                }

                await WriteImageAsync(http, stored, MediaServeRules.CacheControl(endpoint));
                return;
            }

            if (cache.TryRead(target.Key, version) is { } cached)
            {
                await WriteThumbAsync(http, endpoint, cached, BlobInspectRules.ThumbnailType(cached)!);
                return;
            }

            var max = options.Value.Media.MaxThumbBytes;
            using var outcome = await relay.StartAsync(target.DeviceId, target.Ref, MediaPurposes.Thumb, range: null, max, http.RequestAborted);
            if (outcome.Failure != RelayFailure.None || outcome.Reply is not { Status: StatusCodes.Status200OK } reply)
            {
                await FailAsync(http, endpoint, target, outcome);
                return;
            }

            var data = await ReadAllAsync(outcome.Body!, reply.BodyLength, http.RequestAborted);
            if (data is null || BlobInspectRules.ThumbnailType(data) is not { } type)
            {
                log.LogWarning("來源電腦的縮圖不完整或不是 PNG／JPEG：{Key}", target.Key);
                await MediaResults.Error(http, StatusCodes.Status502BadGateway, MediaApiErrors.BadGateway).ExecuteAsync(http);
                return;
            }

            await cache.WriteAsync(target.Key, version, data, CancellationToken.None);
            await WriteThumbAsync(http, endpoint, data, type);
        }
        catch (Exception ex) when (MediaResults.ClientGone(http, ex))
        {
        }
    }

    /// <summary>
    /// 快取與轉送來的縮圖也帶內容的 ETag（SHA-256，同 thumb_blob 的格式）並處理 If-None-Match：max-age 過了之後重新驗證拿到 304，
    /// 不必整張重傳（2026-10-23 以前只有 thumb_blob 有 ETag，重開有很多張圖的對話每張都整張重抓）
    /// </summary>
    private static async Task WriteThumbAsync(HttpContext http, MediaEndpointKind endpoint, byte[] data, string type)
    {
        var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(data)) + "\"";
        var cacheControl = MediaServeRules.CacheControl(endpoint);
        if (MediaServeRules.MatchesETag(http.Request.Headers.IfNoneMatch.ToString(), etag))
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = cacheControl;
            return;
        }
        await WriteImageAsync(http, new ThumbImage(data, type, etag), cacheControl);
    }

    /// <summary>
    /// 沒帶 Range 但帶了 If-None-Match（瀏覽器的快取過了 max-age 重新驗證）：先以 (0,0) 向來源電腦要 1 個位元組拿 ETag，相同就回 304，
    /// 不必把整個檔從來源電腦再傳一次（來源電腦的上傳通常是最慢的一段：3 MB 的截圖在 10 Mbps 要約 2.4 秒）。
    /// 回 true＝已經回應（304，或來源電腦轉送失敗的錯誤）；false＝ETag 不同或拿不到，照常整份轉送
    /// </summary>
    private async Task<bool> TryRevalidateAsync(HttpContext http, MediaEndpointKind endpoint, MediaTarget target, string purpose)
    {
        using var probe = await relay.StartAsync(target.DeviceId, target.Ref, purpose, new MediaRange(0, 0), MediaRelayRules.MaxSegmentBytes, http.RequestAborted);
        if (probe.Failure != RelayFailure.None)
        {
            // 來源電腦離線、拒絕：和整份轉送失敗時的處理相同（圖片退回縮圖），不再試第二次
            await FailAsync(http, endpoint, target, probe);
            return true;
        }
        if (probe.Reply is not { Status: StatusCodes.Status206PartialContent } reply || reply.ETag is null
            || !MediaServeRules.MatchesETag(http.Request.Headers.IfNoneMatch.ToString(), reply.ETag))
            return false;
        // 探 ETag 的那 1 個位元組讀完（裝置的 PUT 正常結束，不是 410）
        if (reply.HasBody && await ReadAllAsync(probe.Body!, reply.BodyLength, http.RequestAborted) is null)
            return false;
        http.Response.StatusCode = StatusCodes.Status304NotModified;
        http.Response.Headers.ETag = reply.ETag;
        http.Response.Headers.CacheControl = MediaServeRules.CacheControl(endpoint);
        return true;
    }

    /// <summary>單一區間：從 <paramref name="start"/> 起最多 8 MB（<paramref name="end"/> 是 null＝開放式）</summary>
    private async Task ServeRangeAsync(HttpContext http, MediaEndpointKind endpoint, MediaTarget target, string purpose, long start, long? end)
    {
        var segment = MediaRelayRules.ClampSegment(start, end);
        using var outcome = await relay.StartAsync(target.DeviceId, target.Ref, purpose, segment, MediaRelayRules.MaxSegmentBytes, http.RequestAborted);
        if (outcome.Failure != RelayFailure.None || outcome.Reply is not { Status: StatusCodes.Status206PartialContent or StatusCodes.Status416RangeNotSatisfiable } reply)
        {
            await FailAsync(http, endpoint, target, outcome);
            return;
        }

        if (reply.Status == StatusCodes.Status416RangeNotSatisfiable)
        {
            Unsatisfiable(http, reply.TotalLength!.Value);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status206PartialContent;
        SetRepresentation(http, endpoint, reply);
        http.Response.Headers.ContentRange = HttpRangeRules.ContentRange(reply.Range!.Value, reply.TotalLength!.Value);
        http.Response.ContentLength = reply.BodyLength;
        await RelayBodyAsync(http, outcome.Body!, reply.BodyLength, target);
    }

    /// <summary>後綴範圍 <c>bytes=-N</c>：凍結的 <c>MediaRange</c> 沒有後綴，先以 <c>(0,0)</c> 問總長，再要最後 N 個位元組（D11）</summary>
    private async Task ServeSuffixAsync(HttpContext http, MediaEndpointKind endpoint, MediaTarget target, string purpose, long suffix)
    {
        long total;
        using (var probe = await relay.StartAsync(target.DeviceId, target.Ref, purpose, new MediaRange(0, 0), MediaRelayRules.MaxSegmentBytes, http.RequestAborted))
        {
            if (probe.Failure != RelayFailure.None || probe.Reply is not { Status: StatusCodes.Status206PartialContent or StatusCodes.Status416RangeNotSatisfiable } reply)
            {
                await FailAsync(http, endpoint, target, probe);
                return;
            }

            total = reply.TotalLength!.Value;
            // 探總長的那 1 個位元組讀完（裝置的 PUT 正常結束，不是 410）
            if (reply.HasBody && await ReadAllAsync(probe.Body!, reply.BodyLength, http.RequestAborted) is null)
            {
                await MediaResults.Error(http, StatusCodes.Status502BadGateway, MediaApiErrors.BadGateway).ExecuteAsync(http);
                return;
            }
        }

        if (total == 0)
        {
            Unsatisfiable(http, 0);
            return;
        }

        await ServeRangeAsync(http, endpoint, target, purpose, Math.Max(0, total - suffix), total - 1);
    }

    /// <summary>沒帶 Range：多段接成一個 200（總長 ≤64 MB；<c>/f/</c> 的檔案下載 ≤512 MB）</summary>
    private async Task ServeWholeAsync(HttpContext http, MediaEndpointKind endpoint, MediaTarget target, string purpose)
    {
        var outcome = await relay.StartAsync(target.DeviceId, target.Ref, purpose, MediaRelayRules.ClampSegment(0, null), MediaRelayRules.MaxSegmentBytes, http.RequestAborted);
        try
        {
            if (outcome.Failure != RelayFailure.None || outcome.Reply is not { Status: StatusCodes.Status206PartialContent or StatusCodes.Status416RangeNotSatisfiable } first)
            {
                await FailAsync(http, endpoint, target, outcome);
                return;
            }

            // 416 對 (0, …) 只在檔案是空的時候：整份就是 0 個位元組
            var total = first.TotalLength!.Value;
            if (total > (endpoint == MediaEndpointKind.File ? MediaRelayRules.MaxUnrangedFileBytes : MediaRelayRules.MaxUnrangedBytes))
            {
                await MediaResults.Error(http, StatusCodes.Status413PayloadTooLarge, MediaApiErrors.TooLarge).ExecuteAsync(http);
                return;
            }

            http.Response.StatusCode = StatusCodes.Status200OK;
            SetRepresentation(http, endpoint, first);
            http.Response.ContentLength = total;
            if (!first.HasBody)
                return;
            if (!await RelayBodyAsync(http, outcome.Body!, first.BodyLength, target))
                return;
            outcome.Dispose();

            if (first.BodyLength == total)
                return;
            foreach (var segment in MediaRelayRules.Segments(first.BodyLength, total - 1))
            {
                using var next = await relay.StartAsync(target.DeviceId, target.Ref, purpose, segment, MediaRelayRules.MaxSegmentBytes, http.RequestAborted);
                if (next.Failure != RelayFailure.None || next.Reply is not { Status: StatusCodes.Status206PartialContent } reply
                    || reply.TotalLength != total || !string.Equals(reply.ETag, first.ETag, StringComparison.Ordinal))
                {
                    log.LogWarning("分段轉送的第 {Start} 位元組起和第一段不一致（{Failure}、ETag {ETag}），中止給瀏覽器的回應：{Key}",
                        segment.Start, next.Failure, next.Reply?.ETag, target.Key);
                    http.Abort();
                    return;
                }

                if (!await RelayBodyAsync(http, next.Body!, reply.BodyLength, target))
                    return;
            }
        }
        finally
        {
            outcome.Dispose();
        }
    }

    /// <summary>
    /// 轉送失敗或電腦拒絕：<c>/m/</c>（與複本）的圖片在離線、403、原檔不在（404）時退回縮圖；原檔不在又沒有縮圖時瀏覽器的圖片回佔位圖
    /// （<see cref="MediaServeRules.MissingPlaceholder"/>），其他照 <see cref="MediaServeRules.Failure"/> 回錯誤
    /// </summary>
    private async Task FailAsync(HttpContext http, MediaEndpointKind endpoint, MediaTarget target, RelayOutcome outcome)
    {
        if (endpoint is MediaEndpointKind.Media or MediaEndpointKind.ReplicaMedia
            && MediaServeRules.FallbackToThumb(outcome.Failure, outcome.Reply, Single(http.Request.Headers["Sec-Fetch-Dest"]))
            && LoadThumb(target) is { } thumb)
        {
            LogFailure(endpoint, target, outcome, "退回縮圖");
            http.Response.Headers[MediaRelayHeaders.Fallback] = "thumb";
            await WriteImageAsync(http, thumb with { ETag = null }, MediaServeRules.NoStore);
            return;
        }

        if (MediaServeRules.MissingPlaceholder(endpoint, outcome.Failure, outcome.Reply, Single(http.Request.Headers["Sec-Fetch-Dest"])))
        {
            LogFailure(endpoint, target, outcome, "原檔已不在，回佔位圖");
            http.Response.Headers[MediaRelayHeaders.Fallback] = MediaServeRules.MissingFallback;
            await WriteImageAsync(http, new ThumbImage(MediaServeRules.MissingImageSvg(), "image/svg+xml", null), MediaServeRules.MissingCacheControl);
            return;
        }

        var (status, code) = MediaServeRules.Failure(outcome.Failure, outcome.Reply);
        LogFailure(endpoint, target, outcome, $"回 {status} {code}");
        if (MediaServeRules.IsBusy(outcome.Failure, outcome.Reply))
            http.Response.Headers.RetryAfter = MediaServeRules.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await MediaResults.Error(http, status, code).ExecuteAsync(http);
    }

    /// <summary>查參照；查不到時記下原因（回應照樣不分原因，D7）</summary>
    private async Task<MediaTarget?> FindAsync(HttpContext http, MediaEndpointKind endpoint, string key)
    {
        var (target, row, miss) = await lookup.LookupAsync(endpoint, key, http.RequestAborted);
        // 協作人（多人協作）：只看得到分享給他的對話的媒體，其他的和查不到一樣回 404
        if (target is not null && collaborators is not null && !collaborators.CanSee(http.User.CollaboratorId(), target.ConversationId))
            (target, row, miss) = (null, target, "not_shared");
        if (target is not null)
            return target;
        if (throttle.ShouldLog($"miss|{endpoint}|{LoggedKey(key)}|{miss}", out var suppressed))
        {
            log.LogInformation("媒體請求查不到、回 404：{Miss}（{Endpoint}、鍵 {Key}、對話 {ConversationId}、參照用途 {Purpose}）{Suppressed}",
                miss, endpoint, LoggedKey(key), row?.ConversationId ?? "?", row?.Purpose ?? "?", LogThrottle.SuppressedNote(suppressed));
        }

        return null;
    }

    /// <summary>轉送沒拿到（離線、忙碌、逾時、電腦拒絕…）：層級照 <see cref="MediaServeRules.FailureLogLevel"/></summary>
    private void LogFailure(MediaEndpointKind endpoint, MediaTarget target, RelayOutcome outcome, string result)
    {
        var reply = outcome.Reply;
        if (!throttle.ShouldLog($"fail|{endpoint}|{target.Key}|{outcome.Failure}|{reply?.Status}|{reply?.Error}|{result}", out var suppressed))
            return;
        log.Log(MediaServeRules.FailureLogLevel(outcome.Failure, reply),
            "媒體轉送沒拿到、{Result}：{Failure}，來源電腦回 {DeviceStatus} {DeviceError}（{Endpoint}、鍵 {Key}、對話 {ConversationId}）{Suppressed}",
            result, outcome.Failure, reply?.Status.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-", reply?.Error ?? "-", endpoint, target.Key,
            target.ConversationId, LogThrottle.SuppressedNote(suppressed));
    }

    private static string LoggedKey(string key) =>
        MediaKeyRules.TryParse(key, out _, out _) ? key : (key.Length > MaxLoggedKeyLength ? key[..MaxLoggedKeyLength] + "…" : key);

    /// <summary>退回用的縮圖：先 <c>thumb_blob</c>，再沒有版本的快取</summary>
    private ThumbImage? LoadThumb(MediaTarget target)
    {
        if (target.ThumbBlob is { } sha && ReadThumbBlob(sha) is { } stored)
            return stored;
        return cache.TryRead(target.Key, version: null) is { } cached ? new ThumbImage(cached, BlobInspectRules.ThumbnailType(cached)!, null) : null;
    }

    /// <summary>縮圖 blob（≤上限、魔數正確才算；讀不到或不合格回 null）</summary>
    private ThumbImage? ReadThumbBlob(string sha256)
    {
        try
        {
            using var stream = blobs.OpenRead(sha256);
            if (stream is null || stream.Length > options.Value.Media.MaxThumbBytes)
                return null;
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            return BlobInspectRules.ThumbnailType(data) is { } type ? new ThumbImage(data, type, "\"" + sha256 + "\"") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "縮圖 blob 讀不到：{Sha}", sha256);
            return null;
        }
    }

    /// <summary>
    /// 把裝置的本文轉給瀏覽器：正好 <paramref name="count"/> 個位元組而且裝置正常結束才回 true；裝置提早結束、長度不符時中止給瀏覽器的連線
    /// （不是正常結束：截斷的內容不會被當成完整的快取起來）
    /// </summary>
    private async Task<bool> RelayBodyAsync(HttpContext http, Stream body, long count, MediaTarget target)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            long copied = 0;
            while (true)
            {
                int read;
                try
                {
                    read = await body.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), http.RequestAborted);
                }
                catch (Exception ex) when (!MediaResults.ClientGone(http, ex))
                {
                    log.LogInformation(ex, "來源電腦的轉送中斷，中止給瀏覽器的回應：{Key}", target.Key);
                    http.Abort();
                    return false;
                }

                if (read == 0)
                    break;
                copied += read;
                if (copied > count)
                    break;
                await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
            }

            if (copied == count)
                return true;
            log.LogWarning("來源電腦的轉送長度不符（{Copied}／{Count}），中止給瀏覽器的回應：{Key}", copied, count, target.Key);
            http.Abort();
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>讀完正好 <paramref name="length"/> 個位元組而且到結尾；裝置提早結束、多送或讀取失敗回 null</summary>
    private static async Task<byte[]?> ReadAllAsync(Stream body, long length, CancellationToken cancellationToken)
    {
        var data = new byte[length];
        try
        {
            await body.ReadExactlyAsync(data, cancellationToken);
            var extra = new byte[1];
            return await body.ReadAsync(extra, cancellationToken) == 0 ? data : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    private static void SetRepresentation(HttpContext http, MediaEndpointKind endpoint, MediaReply reply)
    {
        var (type, disposition) = MediaServeRules.Representation(endpoint, reply);
        var headers = http.Response.Headers;
        http.Response.ContentType = type;
        if (disposition is not null)
            headers.ContentDisposition = disposition;
        if (reply.ETag is not null)
            headers.ETag = reply.ETag;
        headers.AcceptRanges = "bytes";
        headers.CacheControl = MediaServeRules.CacheControl(endpoint);
    }

    private static void Unsatisfiable(HttpContext http, long total)
    {
        http.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
        http.Response.Headers.ContentRange = HttpRangeRules.Unsatisfied(total);
        http.Response.Headers.CacheControl = MediaServeRules.NoStore;
    }

    private static async Task WriteImageAsync(HttpContext http, ThumbImage image, string cacheControl)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = image.ContentType;
        http.Response.Headers.CacheControl = cacheControl;
        if (image.ETag is not null)
            http.Response.Headers.ETag = image.ETag;
        http.Response.ContentLength = image.Data.Length;
        await http.Response.Body.WriteAsync(image.Data, http.RequestAborted);
    }

    private static Task NotFoundAsync(HttpContext http) => MediaResults.Error(http, StatusCodes.Status404NotFound, MediaApiErrors.NotFound).ExecuteAsync(http);

    /// <summary>只有一個值的標頭（多個值當成沒帶）</summary>
    private static string? Single(StringValues values) => values.Count == 1 ? values[0] : null;
}
