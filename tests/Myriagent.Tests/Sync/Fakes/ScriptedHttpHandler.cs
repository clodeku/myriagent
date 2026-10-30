using System.Net;
using System.Text;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>一個已送出的請求（內容先讀成字串：請求物件之後會被釋放）</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body, DateTimeOffset At);

/// <summary>
/// 配對測試的假 HTTP 傳輸（同 TestSupport/StubHttpHandler 的原則：沒有預備的回應就丟例外，測試不該連網），另外支援
/// 回應標頭（Retry-After）、丟出網路例外，以及「先擋住、測試放行才回」的回應（驗證下一次輪詢在收到回應之後才等 interval）。
/// </summary>
internal sealed class ScriptedHttpHandler(TimeProvider? time = null) : HttpMessageHandler
{
    private readonly object gate = new();
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> script = new();
    private readonly List<RecordedRequest> requests = new();

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (gate)
                return requests.ToList();
        }
    }

    public int Remaining
    {
        get
        {
            lock (gate)
                return script.Count;
        }
    }

    public void Respond(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null) =>
        Add((_, _) => Task.FromResult(Response(status, body, configure)));

    /// <summary>回應的內容在收到請求時才組（例如要用到配對中才產生的公鑰）</summary>
    public void RespondWith(HttpStatusCode status, Func<string> body) => Add((_, _) => Task.FromResult(Response(status, body(), null)));

    public void Throw(Exception error) => Add((_, _) => Task.FromException<HttpResponseMessage>(error));

    /// <summary>擋住的回應：回傳的 TaskCompletionSource 被設定（呼叫 SetResult）之後才回應</summary>
    public TaskCompletionSource Gate(HttpStatusCode status, string body)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Add(async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Response(status, body, null);
        });
        return release;
    }

    /// <summary>等到至少送出 <paramref name="count"/> 個請求</summary>
    public async Task WaitForRequestsAsync(int count, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Requests.Count < count)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"{timeoutMs} ms 內沒有送出 {count} 個請求（目前 {Requests.Count} 個）");
            await Task.Delay(5);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> next;
        lock (gate)
        {
            requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body, (time ?? TimeProvider.System).GetUtcNow()));
            if (script.Count == 0)
                throw new InvalidOperationException($"沒有預備給 {request.Method} {request.RequestUri} 的回應（測試不該連網）");
            next = script.Dequeue();
        }

        return await next(request, cancellationToken);
    }

    private void Add(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step)
    {
        lock (gate)
            script.Enqueue(step);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, new UTF8Encoding(false), "application/json") };
        configure?.Invoke(response);
        return response;
    }
}
