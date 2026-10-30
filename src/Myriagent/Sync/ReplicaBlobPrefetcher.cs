using System.Net;
using Myriagent.Core;
using Myriagent.Media;

namespace Myriagent.Sync;

/// <summary>
/// 複本的 blob 預取（WP4.7 計畫步 12，D39）：引擎在解碼一頁之前，把 <see cref="ReplicaBlobRules.Plan"/> 列的 blob 以 E6
/// （<c>GET /api/v1/device/blobs/{sha256}</c>）取回記憶體。同時 ≤ <see cref="ReplicaBlobRules.MaxConcurrent"/> 個，合計由計畫限制在 32 MB 之內。
/// 任何執行緒都可以呼叫
/// </summary>
internal interface IReplicaBlobPrefetcher
{
    /// <summary>取回計畫裡的 blob：每個 SHA-256 一個結果（取到、永久取不到、暫時取不到）。<paramref name="ct"/> 取消時丟 <see cref="OperationCanceledException"/></summary>
    Task<IReadOnlyDictionary<string, ReplicaBlobFetch>> FetchAsync(IReadOnlyList<ReplicaBlobWant> wants, CancellationToken ct);
}

/// <summary>
/// <see cref="IReplicaBlobPrefetcher"/> 的實作：每個 blob 一個請求（自己的 <see cref="DeviceHttpClient"/>，權杖與換新同媒體轉送），
/// 每個最多等 <see cref="FetchTimeout"/>。200 而且大小與 SHA-256 都對才算取到；404、大小或內容不符是永久取不到（伺服器的 blob 不會變）；
/// 沒有權杖、其他狀態碼、連不上、讀到一半斷線、逾時是暫時取不到。同一個 blob 這次執行暫時取不到 <see cref="MaxTransientAttempts"/> 次、
/// 而且從第一次失敗起過了 <see cref="MinTransientSpan"/> 之後改算永久取不到（伺服器上壞掉的檔每次都讀到一半斷線，不能讓那個串流永遠停住；
/// 只看次數的話，對方正在寫、通知一直來的串流幾秒內就會用完次數）。引擎停止時 Dispose（進行中的請求取消）
/// </summary>
internal sealed class ReplicaBlobPrefetcher : IReplicaBlobPrefetcher, IDisposable
{
    /// <summary>每個 blob 最多等多久（4 MB 在慢的線路上也夠）</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    /// <summary>同一個 blob 至少暫時取不到幾次才改算永久取不到（每次間隔至少 <see cref="ReplicaBlobRules.RetryAfter"/>，除非有新事件）</summary>
    public const int MaxTransientAttempts = 5;

    /// <summary>同一個 blob 從第一次暫時取不到起至少多久才改算永久取不到</summary>
    public static readonly TimeSpan MinTransientSpan = TimeSpan.FromMinutes(2);

    /// <summary>記暫時失敗次數的 blob 數上限（超過就整份重記）</summary>
    private const int MaxTrackedFailures = 10_000;

    private const int CopyBufferBytes = 64 * 1024;

    private readonly Func<ReplicaBlobWant, CancellationToken, Task<ReplicaBlobFetch>> fetch;
    private readonly IDisposable? owned;
    private readonly TimeProvider time;
    private readonly CancellationTokenSource stopping = new();
    private readonly Dictionary<string, (int Attempts, long FirstTimestamp)> failures = new(StringComparer.Ordinal);
    private int disposed;

    /// <param name="fetch">取一個 blob（測試換成假的；正式的是 <see cref="Create"/> 的 E6）</param>
    /// <param name="owned">Dispose 時一起釋放的（HTTP 用戶端）</param>
    /// <param name="time">算 <see cref="MinTransientSpan"/> 的時鐘（null＝系統時鐘）</param>
    public ReplicaBlobPrefetcher(Func<ReplicaBlobWant, CancellationToken, Task<ReplicaBlobFetch>> fetch, IDisposable? owned = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        this.fetch = fetch;
        this.owned = owned;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>正式的：以 E6 取（<paramref name="http"/> 由預取器擁有，Dispose 時關掉）</summary>
    public static ReplicaBlobPrefetcher Create(DeviceHttpClient http, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(time);
        return new ReplicaBlobPrefetcher((want, ct) => FetchViaDeviceBlobAsync(http, time, want, ct), http, time);
    }

    public async Task<IReadOnlyDictionary<string, ReplicaBlobFetch>> FetchAsync(IReadOnlyList<ReplicaBlobWant> wants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wants);
        var results = new Dictionary<string, ReplicaBlobFetch>(StringComparer.Ordinal);
        if (wants.Count == 0)
            return results;
        if (Volatile.Read(ref disposed) != 0)
        {
            foreach (var want in wants)
                results[want.Sha256] = ReplicaBlobFetch.Failed;
            return results;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
        var gate = new object();
        var options = new ParallelOptions { MaxDegreeOfParallelism = ReplicaBlobRules.MaxConcurrent, CancellationToken = linked.Token };
        try
        {
            await Parallel.ForEachAsync(wants.DistinctBy(w => w.Sha256, StringComparer.Ordinal), options, async (want, token) =>
            {
                ReplicaBlobFetch result;
                try
                {
                    result = await fetch(want, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
                {
                    Log.Info($"取複本的 blob 失敗（之後重拉）：{ex.Message}");
                    result = ReplicaBlobFetch.Failed;
                }

                lock (gate)
                    results[want.Sha256] = Count(want.Sha256, result);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 預取器停了（引擎停止）：沒取完的算暫時取不到
            foreach (var want in wants)
                results.TryAdd(want.Sha256, ReplicaBlobFetch.Failed);
        }

        return results;
    }

    /// <summary>
    /// 暫時失敗的次數與第一次失敗的時間（呼叫端持有 gate）：取到或永久取不到時清掉；次數與時間都到了改算永久取不到
    /// </summary>
    private ReplicaBlobFetch Count(string sha, ReplicaBlobFetch result)
    {
        if (result.Outcome != ReplicaBlobOutcome.Failed)
        {
            failures.Remove(sha);
            return result;
        }

        if (failures.Count >= MaxTrackedFailures && !failures.ContainsKey(sha))
            failures.Clear();
        var now = time.GetTimestamp();
        var (attempts, first) = failures.TryGetValue(sha, out var seen) ? (seen.Attempts + 1, seen.FirstTimestamp) : (1, now);
        if (attempts < MaxTransientAttempts || time.GetElapsedTime(first, now) < MinTransientSpan)
        {
            failures[sha] = (attempts, first);
            return result;
        }

        failures.Remove(sha);
        Log.Warn($"複本的 blob {sha} 連續 {attempts} 次取不到：當成取不到（那筆項目不顯示，串流照樣前進）");
        return ReplicaBlobFetch.Missing;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        try
        {
            stopping.Cancel();
        }
        catch (AggregateException ex)
        {
            Log.Warn($"取消複本的 blob 預取時出錯：{ex.Message}");
        }

        owned?.Dispose();
    }

    /// <summary>E6：200 而且大小與 SHA-256 都對才算取到；最多讀到宣告的長度多 1 個位元組（多送的當成內容不符）</summary>
    private static async Task<ReplicaBlobFetch> FetchViaDeviceBlobAsync(DeviceHttpClient http, TimeProvider time, ReplicaBlobWant want, CancellationToken ct)
    {
        if (!BlobIdRules.IsBlobId(want.Sha256) || want.Length is < 1 or > ReplicaBlobRules.MaxBlobBytes)
            return ReplicaBlobFetch.Missing;
        using var timeout = new CancellationTokenSource(FetchTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var token = linked.Token;
        try
        {
            using var response = await http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ItemBlobRules.BlobPath(want.Sha256)), HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (response is null)
                return ReplicaBlobFetch.Failed;
            if (response.StatusCode == HttpStatusCode.NotFound)
                return ReplicaBlobFetch.Missing;
            if (response.StatusCode != HttpStatusCode.OK)
                return ReplicaBlobFetch.Failed;
            if (response.Content.Headers.ContentLength is { } declared && declared != want.Length)
                return ReplicaBlobFetch.Missing;

            var content = new byte[want.Length];
            await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var read = 0;
            while (read < content.Length)
            {
                var n = await body.ReadAsync(content.AsMemory(read, Math.Min(CopyBufferBytes, content.Length - read)), token).ConfigureAwait(false);
                if (n == 0)
                    return ReplicaBlobFetch.Failed;   // 提早結束：傳到一半斷線（重試有上限，見 FetchAsync）
                read += n;
            }

            if (await body.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                return ReplicaBlobFetch.Missing;
            return ReplicaBlobRules.Verify(want, content) ? ReplicaBlobFetch.Fetched(content) : ReplicaBlobFetch.Missing;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Log.Info($"取複本的 blob 逾時（{FetchTimeout.TotalSeconds:0} 秒，之後重拉）");
            return ReplicaBlobFetch.Failed;
        }
    }
}
