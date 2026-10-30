using System.Net;
using System.Net.Http.Headers;
using Myriagent.Core;
using Myriagent.Update;

namespace Myriagent.Sync;

/// <summary>
/// 版本來源暫時不能用（伺服器回 503、沒有連上伺服器）：<see cref="IOException"/> 的子類，更新管理員照「下次檢查時續傳」處理（保留部分下載），
/// <see cref="RetryAfter"/> 是伺服器的 <c>Retry-After</c>（沒有是 null：照 1、2、4…60 分鐘退避）
/// </summary>
internal sealed class ReleaseSourceUnavailableException(string message, TimeSpan? retryAfter = null, bool offline = false) : IOException(message), IRetryAfterHint
{
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public bool Offline { get; } = offline;
}

/// <summary>
/// app 自我更新 U3 的版本來源：MyriagentHub 的裝置下載端點（docs/sync-protocol.md 第 15 節；存取權杖 <c>cha_</c>，經同步引擎的 <see cref="DeviceHttpClient"/>）。
/// <list type="bullet">
/// <item><c>GET latest</c>：204＝沒有可用的發佈（回 null）；200 的 <c>manifest</c> 是原文位元組的標準 base64，簽章以
/// <see cref="ReleaseManifestRules.SerializeSignature"/> 組回 .manifest.sig 的內容（由更新管理員以內建的 <c>ReleaseKeys.Production</c> 驗）。</item>
/// <item><c>GET package</c>：已有部分內容時帶 <c>Range: bytes=&lt;長度&gt;-</c> 與 <c>If-Range: "&lt;zipSha256&gt;"</c>；206 接著寫、200 從頭寫（伺服器的套件換了或不認 Range）、
/// 416（部分內容比套件還大）丟 <see cref="InvalidDataException"/>（內容錯誤：管理員刪掉重新下載）。</item>
/// <item>503（<c>draining</c>、<c>unavailable</c>）丟 <see cref="ReleaseSourceUnavailableException"/> 帶 <c>Retry-After</c>；410、404 與其他狀態、沒有連上伺服器
/// （用戶端沒有權杖）也是暫時的錯誤：保留部分下載，之後的檢查會拿到新的 latest。</item>
/// </list>
/// 用戶端每次呼叫才向協調者要（引擎在同步啟動、配對之後才有；解除配對後是 null）。任何執行緒都可以呼叫
/// </summary>
internal sealed class HubReleaseSource(Func<DeviceHttpClient?> client) : IAppReleaseSource
{
    /// <summary>latest 的回應本文上限（manifest 的 base64 加簽章與其他欄位）</summary>
    internal const int MaxLatestBytes = ReleaseManifestRules.MaxManifestBytes / 3 * 4 + 16 * 1024;

    private const int CopyBufferBytes = 64 * 1024;

    /// <summary>單一次請求等回應標頭最多多久（本文另外照讀到的位元組判斷；DeviceHttpClient 本身沒有逾時）</summary>
    private static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(60);

    public string Name => "伺服器";

    public async Task<ReleaseOffer?> GetLatestAsync(CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, AppReleaseRoutes.Latest), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;
        if (response.StatusCode != HttpStatusCode.OK)
            throw Unavailable(response, "查詢最新版");
        var body = await ReadLimitedAsync(response, MaxLatestBytes, ct).ConfigureAwait(false);
        return HubReleaseRules.ParseLatest(body);
    }

    public async Task DownloadZipAsync(ReleaseOffer offer, string zipName, string destination, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var existing = File.Exists(destination) ? new FileInfo(destination).Length : 0;
        // 上次已經下載完整（之後的驗證或解壓失敗）：不再續傳——從結尾續傳伺服器回 416，被當成內容錯誤刪掉重下載（2026-10-27 每小時重抓一次）
        switch (HubReleaseRules.PlanExisting(existing, offer.ZipBytes))
        {
            case ReleaseExistingPlan.Complete:
                progress?.Report(existing);
                return;
            case ReleaseExistingPlan.Discard:
                File.Delete(destination);
                existing = 0;
                break;
        }
        var etag = HubReleaseRules.ZipETag(offer.ManifestBytes);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, AppReleaseRoutes.Package(offer.Release));
            if (existing > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existing, null);
                if (etag is not null)
                    request.Headers.TryAddWithoutValidation("If-Range", etag);
            }
            return request;
        }, ct).ConfigureAwait(false);

        var plan = HubReleaseRules.PlanWrite((int)response.StatusCode, existing, ContentRangeStart(response));
        switch (plan)
        {
            case ReleaseWritePlan.Append:
            case ReleaseWritePlan.Restart:
                break;
            case ReleaseWritePlan.ContentError:
                throw new InvalidDataException($"部分下載的檔案和伺服器上的套件對不上（HTTP {(int)response.StatusCode}）");
            default:
                throw Unavailable(response, "下載新版");
        }

        await using var output = new FileStream(destination, plan == ReleaseWritePlan.Append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None,
            CopyBufferBytes, useAsync: true);
        long offset = output.Length;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[CopyBufferBytes];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            offset += read;
            progress?.Report(offset);
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request, CancellationToken ct)
    {
        if (client() is not { } http)
            throw new ReleaseSourceUnavailableException("沒有連上伺服器", offline: true);
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(HeadersTimeout);
        HttpResponseMessage? response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ReleaseSourceUnavailableException("伺服器沒有回應");
        }
        return response ?? throw new ReleaseSourceUnavailableException("沒有連上伺服器（還沒有存取權杖）", offline: true);
    }

    private static ReleaseSourceUnavailableException Unavailable(HttpResponseMessage response, string what)
    {
        var retryAfter = response.Headers.RetryAfter is { } header
            ? header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null)
            : null;
        Log.Info($"{what}：伺服器回 HTTP {(int)response.StatusCode}" + (retryAfter is { } after ? $"，{after.TotalSeconds:0} 秒後再試" : string.Empty));
        return new ReleaseSourceUnavailableException(HubReleaseRules.StatusText((int)response.StatusCode, what), retryAfter);
    }

    private static long? ContentRangeStart(HttpResponseMessage response) => response.Content.Headers.ContentRange?.From;

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > 0 and var declared && declared > max)
            throw new InvalidDataException($"最新版的回應太大：{declared} 位元組");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[CopyBufferBytes];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > max)
                throw new InvalidDataException($"最新版的回應超過 {max} 位元組");
            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }
}

/// <summary>已經有部分下載時怎麼辦（HubReleaseRules.PlanExisting）</summary>
internal enum ReleaseExistingPlan
{
    /// <summary>沒有部分下載，或不知道套件多大：照常（有的話續傳）</summary>
    Download,

    /// <summary>已經和套件一樣大：不再下載，直接驗證（雜湊不符時照內容錯誤重新下載）</summary>
    Complete,

    /// <summary>比套件還大：刪掉從頭下載</summary>
    Discard,
}

/// <summary>寫套件的方式</summary>
internal enum ReleaseWritePlan
{
    /// <summary>206：接在部分內容後面</summary>
    Append,

    /// <summary>200：從頭寫（沒有部分內容、或伺服器的套件換了而回整份）</summary>
    Restart,

    /// <summary>416，或 206 的起點和部分內容的長度不同：內容錯誤（刪掉重新下載）</summary>
    ContentError,

    /// <summary>其他狀態：暫時不能下載</summary>
    Unavailable,
}

/// <summary>app 自我更新 U3 的版本來源規則（docs/sync-protocol.md 第 15 節）</summary>
internal static class HubReleaseRules
{
    /// <summary>
    /// latest 的 200 本文 → 版本提議：release 合法、manifest 是標準 base64 而且不超過上限、簽章組回 .manifest.sig 的內容。
    /// 不合格丟 <see cref="InvalidDataException"/>（管理員當成這次檢查失敗）
    /// </summary>
    public static ReleaseOffer ParseLatest(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        LatestAppRelease? latest;
        try
        {
            latest = SyncJson.Deserialize<LatestAppRelease>(System.Text.Encoding.UTF8.GetString(body));
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            throw new InvalidDataException("最新版的回應不是合格的 JSON：" + ex.Message);
        }

        if (latest is null || !ReleaseIdentity.TryParseRelease(latest.Release, out _) || string.IsNullOrEmpty(latest.Manifest) || latest.Sig is null)
            throw new InvalidDataException("最新版的回應缺少必要的欄位");
        byte[] manifest;
        try
        {
            manifest = Convert.FromBase64String(latest.Manifest);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("最新版的 manifest 不是標準 base64");
        }

        if (manifest.Length > ReleaseManifestRules.MaxManifestBytes)
            throw new InvalidDataException("最新版的 manifest 太大");
        return new ReleaseOffer(latest.Release, manifest, ReleaseManifestRules.SerializeSignature(latest.Sig)) { ZipBytes = latest.ZipBytes > 0 ? latest.ZipBytes : null };
    }

    /// <summary>If-Range 用的強 ETag：manifest 的 <c>zipSha256</c>（伺服器的 package 回 <c>ETag: "&lt;zipSha256&gt;"</c>）；manifest 讀不出來回 null（不帶 If-Range）</summary>
    public static string? ZipETag(byte[] manifestBytes)
        => ReleaseManifestRules.TryParse(manifestBytes, out var manifest, out _) ? "\"" + manifest.ZipSha256 + "\"" : null;

    /// <summary>已下載的部分（<paramref name="existing"/> 位元組）對 latest 給的套件大小（<paramref name="zipBytes"/>，舊伺服器沒給是 null）</summary>
    public static ReleaseExistingPlan PlanExisting(long existing, long? zipBytes) => existing <= 0 || zipBytes is not > 0
        ? ReleaseExistingPlan.Download
        : existing == zipBytes ? ReleaseExistingPlan.Complete
        : existing > zipBytes ? ReleaseExistingPlan.Discard
        : ReleaseExistingPlan.Download;

    /// <summary>依回應的狀態與部分內容決定怎麼寫（<paramref name="contentRangeStart"/> 是 206 的 Content-Range 起點）</summary>
    public static ReleaseWritePlan PlanWrite(int status, long existing, long? contentRangeStart) => status switch
    {
        200 => ReleaseWritePlan.Restart,
        206 when existing > 0 && contentRangeStart == existing => ReleaseWritePlan.Append,
        206 => ReleaseWritePlan.ContentError,
        416 => ReleaseWritePlan.ContentError,
        _ => ReleaseWritePlan.Unavailable,
    };

    /// <summary>給人看的原因（更新橫幅與設定頁的「下載失敗：…」）</summary>
    public static string StatusText(int status, string what) => status switch
    {
        503 => $"{what}：伺服器暫時不能提供（可能正在更新），稍後自動再試",
        410 => $"{what}：這一版已被撤回，下次檢查會改用伺服器目前提供的版本",
        404 => $"{what}：伺服器上找不到這一版",
        401 or 403 => $"{what}：伺服器拒絕了這台電腦的存取權杖，稍後再試",
        _ => $"{what}：伺服器回 HTTP {status}",
    };
}
