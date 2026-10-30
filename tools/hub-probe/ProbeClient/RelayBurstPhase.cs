using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace MyriagentHub.ProbeClient;

/// <summary>轉送階段的一台：裝置名稱、瀏覽器拿到的 <c>/m/</c> 網址（沒拿到是 null，不發請求）與它的媒體檔</summary>
internal sealed record RelayTarget(string Device, string? Url, RelayMediaFile File);

/// <summary>
/// load 的媒體轉送上限階段（WP4.9 計畫步 12；WP4.7 R6：每台來源同時 ≤20 個轉送、每段 ≤8 MB，P47:129、:1439）：
/// S9 的負載期間之後接 <see cref="PhaseSeconds"/> 秒，串流照跑。每台並行：
/// <list type="number">
/// <item>對它的 <c>/m/</c> 網址同時發 20 個 GET（沒帶 Range；檔案正好 8 MiB，伺服器向桌面要一段），等 20 個都拿到標頭——名額都佔住了、本文還沒讀。
/// 每條連線的接收緩衝區固定 64 KB（設了 SO_RCVBUF 就不自動調整）：本文沒讀完，伺服器的轉送就結束不了、放不掉名額。</item>
/// <item>再發第 21 個：伺服器要立刻回 503 <c>busy</c> 加 <c>Retry-After</c>（MediaServeRules.IsBusy）。</item>
/// <item>20 個本文照 <see cref="ReadSeconds"/> 秒讀完的節奏慢速讀（每次 16 KB，<see cref="LoadRules.RelayPaceDelay"/>），邊讀邊算 SHA-256 比對檔案
/// （每台的檔案不同，也證明網址對到哪一台）。</item>
/// <item>所有台都讀完之後再一個 Range 請求（前 64 KB）：名額收回了才是 206，內容是檔案的開頭。伺服器另有所有來源合計的上限
/// （<c>Hub:Media:MaxConcurrentRelays</c>，預設 64）：台數 × 20 超過它時，前 20 個裡超出的也回 503 busy（判定見 <see cref="LoadRules.RelayVerdict"/>）。</item>
/// </list>
/// 擁有者 cookie 是這個階段自己登入的（另一個工作階段）；HTTP/1.1（每個請求一條連線）；只連 load 守門過的本機伺服器
/// </summary>
internal static class RelayBurstPhase
{
    /// <summary>階段的長度（秒，計畫：S9 的 10 分鐘之後接 2 分鐘）</summary>
    public const int PhaseSeconds = 120;

    /// <summary>20 個本文從開始讀到讀完的目標秒數（留時間給等標頭與名額收回的請求）</summary>
    public const int ReadSeconds = 90;

    private const int ReceiveBufferBytes = 64 * 1024;

    private const int ReadChunkBytes = 16 * 1024;

    private const int MaxErrorBodyBytes = 4 * 1024;

    /// <summary>等標頭的上限（伺服器的首位元組逾時是 15 秒，之後回 504）</summary>
    private static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(30);

    /// <summary>讀本文時一次讀取的上限（沒有資料這麼久＝卡住）</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    public static async Task<IReadOnlyList<RelayDeviceResult>> RunAsync(string baseUrl, string cookie, IReadOnlyList<RelayTarget> targets, Action<object> emit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        using var http = CreateClient();
        var baseUri = new Uri(baseUrl);
        // 名額收回的 Range 請求等所有台的本文都讀完才送（WP4.9 步 19）：伺服器有全域上限（Hub:Media:MaxConcurrentRelays）時，
        // 前 20 個大多被擋下的那幾台很快就「讀完」，先送的話別台還佔著全域名額，會被誤判成名額沒有收回
        var reading = new AllReadBarrier(targets.Count(t => t.Url is not null));
        return await Task.WhenAll(targets.Select(t => DeviceAsync(http, baseUri, cookie, t, reading, emit, ct)));
    }

    /// <summary>所有台的第 3 步（慢速讀完）都結束了：每台恰好呼叫一次 <see cref="Done"/>（例外時也是）</summary>
    private sealed class AllReadBarrier
    {
        private readonly TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int pending;

        public AllReadBarrier(int count)
        {
            pending = count;
            if (count <= 0)
                all.TrySetResult();
        }

        public Task All => all.Task;

        public void Done()
        {
            if (Interlocked.Decrement(ref pending) == 0)
                all.TrySetResult();
        }
    }

    /// <summary>HTTP/1.1、不用 cookie 容器（cookie 照字面帶）、不轉址不解壓；連線的接收緩衝區固定 <see cref="ReceiveBufferBytes"/></summary>
    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            // 本機 MyriagentHub 的開發憑證（load 已經過正式站守門，只連迴路位址）
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            ConnectCallback = ConnectAsync,
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, ReceiveBufferSize = ReceiveBufferBytes };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<RelayDeviceResult> DeviceAsync(HttpClient http, Uri baseUri, string cookie, RelayTarget target, AllReadBarrier reading, Action<object> emit,
        CancellationToken ct)
    {
        if (target.Url is null)
        {
            emit(new { @event = "relayDevice", device = target.Device, url = (string?)null, error = "沒有拿到 /m/ 網址" });
            return new RelayDeviceResult(target.Device, null, [], null);
        }

        var uri = new Uri(baseUri, target.Url);
        var results = new List<RelayRequestResult>();
        var held = Enumerable.Range(0, LoadRules.RelayConcurrentPerDevice).Select(i => new RelayCall(i, uri, cookie, range: null)).ToList();
        try
        {
            // 1. 20 個同時：都拿到標頭才往下（名額佔住、本文一個位元組都還沒讀）
            await Task.WhenAll(held.Select(c => c.SendAsync(http, HeadersTimeout, ct)));

            // 2. 第 21 個：要立刻被拒（多收時照樣讀完，判定看得到它是完整的 200）
            using (var extra = new RelayCall(LoadRules.RelayConcurrentPerDevice, uri, cookie, range: null))
            {
                await extra.SendAsync(http, HeadersTimeout, ct);
                results.Add(await extra.FinishAsync(target.File.Length, target.File.Sha256, span: null, ct));
            }

            // 3. 20 個慢速讀完
            results.AddRange(await Task.WhenAll(held.Select(c => c.FinishAsync(target.File.Length, target.File.Sha256, TimeSpan.FromSeconds(ReadSeconds), ct))));
        }
        finally
        {
            foreach (var call in held)
                call.Dispose();
            reading.Done();
        }

        // 4. 名額收回：等所有台都讀完（全域上限的名額才都放掉），再送前 64 KB 的 Range 請求
        await reading.All.WaitAsync(ct);
        RelayRequestResult after;
        using (var release = new RelayCall(LoadRules.RelayRequestsPerDevice, uri, cookie, range: (0, LoadRules.RelayAfterReleaseBytes - 1)))
        {
            await release.SendAsync(http, HeadersTimeout, ct);
            after = await release.FinishAsync(Math.Min(target.File.Length, LoadRules.RelayAfterReleaseBytes), target.File.PrefixSha256, span: null, ct);
        }

        var ordered = results.OrderBy(r => r.Index).ToList();
        var done = ordered.Where(r => r.Index < LoadRules.RelayConcurrentPerDevice && r.DoneMs is not null).Select(r => r.DoneMs!.Value).ToList();
        emit(new
        {
            @event = "relayDevice",
            device = target.Device,
            url = target.Url,
            statuses = ordered.Select(r => r.Status).ToList(),
            complete = ordered.Count(r => r.Complete),
            extra = ordered.FirstOrDefault(r => r.Index == LoadRules.RelayConcurrentPerDevice) is { } x ? new { x.Status, x.Error, x.RetryAfter, x.HeadersMs } : null,
            afterRelease = new { after.Status, after.Complete, after.Bytes },
            failures = ordered.Append(after).Where(r => r.Failure is not null).Select(r => r.Failure).Distinct().Take(3).ToList(),
            readSeconds = done.Count > 0 ? Math.Round(done.Max() / 1000, 1) : (double?)null,
        });
        return new RelayDeviceResult(target.Device, target.Url, ordered, after);
    }

    /// <summary>一個 GET：先只拿標頭（<see cref="SendAsync"/>），之後才讀本文（<see cref="FinishAsync"/>）</summary>
    private sealed class RelayCall(int index, Uri uri, string cookie, (long From, long To)? range) : IDisposable
    {
        private long started;
        private HttpResponseMessage? response;
        private string? failure;
        private double? headersMs;

        public async Task SendAsync(HttpClient http, TimeSpan timeout, CancellationToken ct)
        {
            started = Stopwatch.GetTimestamp();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            if (range is { } r)
                request.Headers.Range = new RangeHeaderValue(r.From, r.To);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                headersMs = ElapsedMs();
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                failure = ex is OperationCanceledException ? $"{timeout.TotalSeconds:0} 秒內沒有拿到標頭" : ex.GetBaseException().Message;
            }
        }

        /// <summary>讀本文：200／206 算長度與 SHA-256（<paramref name="span"/> 有值時照它慢速讀），其他狀態讀錯誤代碼</summary>
        public async Task<RelayRequestResult> FinishAsync(long expectedBytes, string? expectedSha256, TimeSpan? span, CancellationToken ct)
        {
            if (response is null)
                return new RelayRequestResult(index, null, null, false, 0, false, headersMs, null, failure ?? "沒有送出");
            var status = (int)response.StatusCode;
            var retryAfter = response.Headers.RetryAfter is not null;
            if (status is not (200 or 206))
                return new RelayRequestResult(index, status, await ReadErrorAsync(ct), retryAfter, 0, false, headersMs, ElapsedMs(), null);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[ReadChunkBytes];
            long bytes = 0;
            string? problem = null;
            var readStart = Stopwatch.GetTimestamp();
            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                while (true)
                {
                    int read;
                    using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        stall.CancelAfter(StallTimeout);
                        read = await body.ReadAsync(buffer, stall.Token);
                    }

                    if (read == 0)
                        break;
                    hash.AppendData(buffer, 0, read);
                    bytes += read;
                    if (span is { } s && LoadRules.RelayPaceDelay(bytes, expectedBytes, Stopwatch.GetElapsedTime(readStart), s) is var wait && wait > TimeSpan.Zero)
                        await Task.Delay(wait, ct);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                problem = ex is OperationCanceledException ? $"{StallTimeout.TotalSeconds:0} 秒沒有讀到資料" : ex.GetBaseException().Message;
            }

            var length = response.Content.Headers.ContentLength;
            var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (problem is null && length != bytes)
                problem = $"Content-Length {length?.ToString(CultureInfo.InvariantCulture) ?? "沒有"}，讀到 {bytes} 位元組";
            if (problem is null && bytes != expectedBytes)
                problem = $"讀到 {bytes} 位元組（要 {expectedBytes}）";
            if (problem is null && expectedSha256 is not null && !string.Equals(sha, expectedSha256, StringComparison.Ordinal))
                problem = "SHA-256 和檔案不同";
            return new RelayRequestResult(index, status, null, retryAfter, bytes, problem is null, headersMs, ElapsedMs(), problem);
        }

        public void Dispose() => response?.Dispose();

        /// <summary>錯誤回應的 <c>{"error":"…"}</c>（最多讀 4 KB；讀不到或不是這個形狀回 null）</summary>
        private async Task<string?> ReadErrorAsync(CancellationToken ct)
        {
            try
            {
                await using var body = await response!.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[MaxErrorBodyBytes];
                var total = 0;
                int read;
                while (total < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(total), ct)) > 0)
                    total += read;
                using var json = JsonDocument.Parse(buffer.AsMemory(0, total));
                return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                return null;
            }
        }

        private double ElapsedMs() => Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
    }
}
