using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>
/// 假的 E5（WP4.7 計畫步 10）：<c>HEAD</c>／<c>PUT /api/v1/device/blobs/{sha256}</c>，掛在 <see cref="FakeRelayHandler.AsyncResponder"/>（其他路徑回 null，交給原本的 Responder）。
/// PUT 照伺服器驗雜湊與長度後存（新存 201、已有 200，回 <c>{sha256, length, existed}</c>）；<see cref="PutResult"/>、<see cref="HeadResult"/> 可以改回別的狀態碼（不存）。
/// <see cref="HoldPuts"/> 為真時 PUT 讀完本文之後擋住，等 <see cref="Release"/> 或取消。
/// 步 12 起也是假的 E6（<c>GET</c>）：有存就 200 與內容、沒有 404；<see cref="GetResult"/> 可以改回別的回應，<see cref="MaxConcurrentGets"/> 記同時進行的最大數
/// </summary>
internal sealed class FakeBlobEndpoint
{
    private const string Prefix = "/api/v1/device/blobs/";

    private readonly ConcurrentDictionary<string, byte[]> stored = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> putShas = new();
    private readonly ConcurrentQueue<string> getShas = new();
    private int heads;
    private int held;
    private int inFlightGets;
    private int maxConcurrentGets;

    public FakeBlobEndpoint(FakeRelayHandler relay) => relay.AsyncResponder = HandleAsync;

    /// <summary>存好的 blob（sha → 內容）</summary>
    public IReadOnlyDictionary<string, byte[]> Stored => stored;

    public int Heads => Volatile.Read(ref heads);

    /// <summary>收到的 PUT（依序的 sha；含被改回別的狀態碼的）</summary>
    public IReadOnlyList<string> Puts => putShas.ToList();

    /// <summary>PUT 怎麼回（sha、本文 → 狀態碼；null＝照常驗證與存）</summary>
    public Func<string, byte[], HttpStatusCode?>? PutResult { get; set; }

    /// <summary>HEAD 怎麼回（sha → 狀態碼；null＝有存就 200、沒有 404）</summary>
    public Func<string, HttpStatusCode?>? HeadResult { get; set; }

    /// <summary>擋住 PUT（讀完本文之後），等 <see cref="Release"/></summary>
    public bool HoldPuts { get; set; }

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int HeldPuts => Volatile.Read(ref held);

    /// <summary>收到的 GET（E6，依序的 sha）</summary>
    public IReadOnlyList<string> Gets => getShas.ToList();

    /// <summary>GET 怎麼回（sha → 回應；null＝有存就 200、沒有 404）</summary>
    public Func<string, HttpResponseMessage?>? GetResult { get; set; }

    /// <summary>每個 GET 回應前等多久（量同時進行的數量用）</summary>
    public TimeSpan GetDelay { get; set; }

    /// <summary>同時進行的 GET 的最大數</summary>
    public int MaxConcurrentGets => Volatile.Read(ref maxConcurrentGets);

    /// <summary>直接放一個 blob（模擬伺服器上已經有）</summary>
    public void Put(byte[] content) => stored[Convert.ToHexStringLower(SHA256.HashData(content))] = content;

    public byte[]? Read(string sha) => stored.TryGetValue(sha, out var content) ? content : null;

    public async Task WaitForHeldAsync(int count, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (HeldPuts < count)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"{timeoutMs} ms 內沒有擋住 {count} 個 blob 的 PUT（目前 {HeldPuts} 個）");
            await Task.Delay(5);
        }
    }

    private async Task<HttpResponseMessage?> HandleAsync(RelayPut request, CancellationToken ct)
    {
        if (!request.Path.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var sha = request.Path[Prefix.Length..];
        if (request.Method == HttpMethod.Head)
        {
            Interlocked.Increment(ref heads);
            if (HeadResult?.Invoke(sha) is { } headStatus)
                return new HttpResponseMessage(headStatus);
            if (!stored.TryGetValue(sha, out var existing))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) { Headers = { ContentLength = existing.LongLength } } };
        }

        if (request.Method == HttpMethod.Get)
            return await GetAsync(sha, ct);
        if (request.Method != HttpMethod.Put)
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        putShas.Enqueue(sha);
        if (HoldPuts)
        {
            Interlocked.Increment(ref held);
            try
            {
                await Release.Task.WaitAsync(ct);
            }
            finally
            {
                Interlocked.Decrement(ref held);
            }
        }

        if (PutResult?.Invoke(sha, request.Body) is { } status)
            return Json(status, status == HttpStatusCode.UnprocessableEntity ? """{"error":"dlp"}""" : """{"error":"unavailable"}""");
        if (request.Header("Content-Length") != request.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || Convert.ToHexStringLower(SHA256.HashData(request.Body)) != sha)
            return Json(HttpStatusCode.BadRequest, """{"error":"mismatch"}""");
        var existed = !stored.TryAdd(sha, request.Body);
        return Json(existed ? HttpStatusCode.OK : HttpStatusCode.Created,
            $$"""{"sha256":"{{sha}}","length":{{request.Body.Length}},"existed":{{(existed ? "true" : "false")}}}""");
    }

    private async Task<HttpResponseMessage> GetAsync(string sha, CancellationToken ct)
    {
        getShas.Enqueue(sha);
        var now = Interlocked.Increment(ref inFlightGets);
        int seen;
        while (now > (seen = Volatile.Read(ref maxConcurrentGets)) && Interlocked.CompareExchange(ref maxConcurrentGets, now, seen) != seen)
        {
        }

        try
        {
            if (GetDelay > TimeSpan.Zero)
                await Task.Delay(GetDelay, ct);
            if (GetResult?.Invoke(sha) is { } answer)
                return answer;
            return stored.TryGetValue(sha, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        finally
        {
            Interlocked.Decrement(ref inFlightGets);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
