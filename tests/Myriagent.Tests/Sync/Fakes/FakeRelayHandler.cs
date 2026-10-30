using System.Collections.Concurrent;
using System.Net;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>裝置送出的一個 E7 回覆（本文先讀成位元組：請求物件之後可能被釋放）</summary>
internal sealed record RelayPut(string RelayId, HttpMethod Method, string Path, string? Authorization, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public int? Status => int.TryParse(Header("X-Status"), out var status) ? status : null;

    /// <summary>網址的查詢字串（含 ?；沒有是空字串）</summary>
    public string Query { get; init; } = string.Empty;
}

/// <summary>
/// 媒體轉送測試的假伺服器 E7（<c>PUT /api/v1/device/relay/{relayId}</c>）：記錄每個請求（標頭含本文的標頭、本文的位元組），
/// 依序回 <see cref="Statuses"/>（用完一律 204）。<see cref="Hold"/> 為真的請求在讀完本文之後擋住，等 <see cref="Release"/> 或取消（取消的記進 <see cref="Cancelled"/>）。
/// 其他裝置端點（步 9 的縮圖 E8、E10）也記進 <see cref="Requests"/>，不擋、不用 <see cref="Statuses"/>，以 <see cref="Responder"/> 回（沒有時 404）
/// </summary>
internal sealed class FakeRelayHandler : HttpMessageHandler
{
    private const string RelayPrefix = "/api/v1/device/relay/";

    private readonly ConcurrentQueue<RelayPut> puts = new();
    private readonly ConcurrentQueue<RelayPut> requests = new();
    private int held;

    /// <summary>E7 的回覆（轉送）</summary>
    public IReadOnlyList<RelayPut> Puts => puts.ToList();

    /// <summary>所有請求（E7 以外的也在內，依收到的順序）</summary>
    public IReadOnlyList<RelayPut> Requests => requests.ToList();

    /// <summary>E7 以外的請求怎麼回（null 或回 null＝404）；在測試主機的執行緒集區呼叫</summary>
    public Func<RelayPut, HttpResponseMessage?>? Responder { get; set; }

    /// <summary>E7 以外的請求先問它（可以擋住、等取消；回 null 再問 <see cref="Responder"/>），步 10 的項目 blob（<see cref="FakeBlobEndpoint"/>）用</summary>
    public Func<RelayPut, CancellationToken, Task<HttpResponseMessage?>>? AsyncResponder { get; set; }

    /// <summary>依序回的狀態（用完一律 204 No Content）</summary>
    public ConcurrentQueue<HttpStatusCode> Statuses { get; } = new();

    /// <summary>哪些請求要擋住（null＝都不擋）</summary>
    public Func<RelayPut, bool>? Hold { get; set; }

    /// <summary>放行擋住的請求</summary>
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>擋住時被取消的轉送 id</summary>
    public ConcurrentQueue<string> Cancelled { get; } = new();

    /// <summary>目前擋著的請求數</summary>
    public int Held => Volatile.Read(ref held);

    public async Task WaitForPutsAsync(int count, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (puts.Count < count)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"{timeoutMs} ms 內沒有收到 {count} 個轉送回覆（目前 {puts.Count} 個）");
            await Task.Delay(5);
        }
    }

    public async Task WaitForHeldAsync(int count, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Held < count)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"{timeoutMs} ms 內沒有擋住 {count} 個轉送回覆（目前 {Held} 個）");
            await Task.Delay(5);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 先算出 Content-Length（正式的 SocketsHttpHandler 送出時也會算）：標頭的列舉才會有它
        _ = request.Content?.Headers.ContentLength;
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var path = request.RequestUri!.AbsolutePath;
        var put = new RelayPut(path[(path.LastIndexOf('/') + 1)..], request.Method, path, request.Headers.Authorization?.ToString(), headers, body)
        {
            Query = request.RequestUri.Query,
        };
        requests.Enqueue(put);
        if (!path.StartsWith(RelayPrefix, StringComparison.Ordinal))
        {
            var answer = (AsyncResponder is { } async ? await async(put, cancellationToken) : null)
                         ?? Responder?.Invoke(put) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
            answer.RequestMessage = request;
            return answer;
        }

        puts.Enqueue(put);
        if (Hold?.Invoke(put) == true)
        {
            Interlocked.Increment(ref held);
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.Enqueue(put.RelayId);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref held);
            }
        }

        var status = Statuses.TryDequeue(out var next) ? next : HttpStatusCode.NoContent;
        return new HttpResponseMessage(status) { RequestMessage = request };
    }
}
