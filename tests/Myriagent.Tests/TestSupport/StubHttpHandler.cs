using System.Net;
using System.Text;

namespace Myriagent.Tests.TestSupport;

/// <summary>假 HTTP 傳輸：記錄每次請求（方法、網址、標頭），依序回放預備的回應；沒有預備回應時丟例外（測試不該連網）</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    public void Enqueue(HttpStatusCode status, string body) => responses.Enqueue((status, body));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (responses.Count == 0)
            throw new InvalidOperationException($"沒有預備給 {request.Method} {request.RequestUri} 的回應（測試不該連網）");
        var (status, body) = responses.Dequeue();
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
