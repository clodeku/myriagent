using System.Collections.Concurrent;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// 一個 grok agent 程序的 ACP 連線：送請求並配對回應、送通知、回覆 agent 的請求。帳戶查詢（GrokAcpClient）與對話會話（GrokAgentSession）共用。
/// 讀取執行緒依序觸發 Response、Request、Notification 事件（同步，不加鎖）；RequestAsync 的等待在事件之後完成
/// </summary>
internal sealed class GrokAcpConnection
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<(JToken? Result, GrokAcpError? Error)>> pending = new();
    private long nextId;

    public GrokAcpConnection(AgentProcess process)
    {
        Process = process;
    }

    public AgentProcess Process { get; }

    /// <summary>每一則回應（含 RequestAsync 在等的）：在完成等待之前、於讀取執行緒觸發</summary>
    public event Action<GrokAcpMessage>? Response;

    /// <summary>agent 對客戶端的請求（核准、提問、計畫確認）</summary>
    public event Action<GrokAcpMessage>? Request;

    public event Action<GrokAcpMessage>? Notification;

    /// <summary>讀到一行 stdout（AgentProcess.LineReceived 轉進來）</summary>
    public void OnLine(string line)
    {
        var message = GrokAcpRules.Parse(line);
        if (message == null)
            return;
        try
        {
            switch (message.Kind)
            {
                case GrokAcpMessageKind.Response:
                    Response?.Invoke(message);
                    if (message.Id is { Type: JTokenType.Integer } id && pending.TryRemove(id.Value<long>(), out var waiter))
                        waiter.TrySetResult((message.Result, message.Error));
                    break;
                case GrokAcpMessageKind.Request:
                    if (Request != null)
                        Request.Invoke(message);
                    else
                        _ = RespondErrorAsync(message.Id!, -32601, "Clode Myriagent 不處理這個請求");
                    break;
                default:
                    Notification?.Invoke(message);
                    break;
            }
        }
        catch (Exception ex)
        {
            // 帶上更新的種類（session/update 有十幾種），才看得出是哪一種的格式沒料到
            var kind = (message.Params["update"] as JObject)?["sessionUpdate"] is JValue { Type: JTokenType.String } v ? $"：{(string?)v}" : string.Empty;
            Log.Warn($"Grok 訊息處理失敗（{message.Method ?? "回應"}{kind}）：{ex.Message}");
        }
    }

    /// <summary>保留一個請求 id（先記下要等的回應再送出，回應不會比記錄早到）</summary>
    public long ReserveId() => Interlocked.Increment(ref nextId);

    /// <summary>以保留的 id 送出請求、不等回應（session/prompt 由 Response 事件收尾）；寫入失敗丟 IOException</summary>
    public Task SendRequestAsync(long id, string method, JObject? parameters)
        => Process.WriteLineAsync(GrokAcpRules.BuildRequest(id, method, parameters));

    /// <summary>送出請求並等回應；逾時（timeout 為 null＝不限）回錯誤，程序結束時回「已結束」的錯誤，ct 取消時丟 OperationCanceledException</summary>
    public async Task<(JToken? Result, GrokAcpError? Error)> RequestAsync(string method, JObject? parameters, TimeSpan? timeout, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<(JToken?, GrokAcpError?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await Process.WriteLineAsync(GrokAcpRules.BuildRequest(id, method, parameters)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            pending.TryRemove(id, out _);
            return (null, Failure($"Grok 程序已結束：{Process.GetStderrTail(300)}"));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } limit)
            cts.CancelAfter(limit);
        try
        {
            return await completion.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            pending.TryRemove(id, out _);
            ct.ThrowIfCancellationRequested();
            return (null, Failure($"{method} 逾時"));
        }
    }

    public Task NotifyAsync(string method, JObject? parameters) => Process.WriteLineAsync(GrokAcpRules.BuildNotification(method, parameters));

    public Task RespondAsync(JToken id, JToken result) => Process.WriteLineAsync(GrokAcpRules.BuildResult(id, result));

    public Task RespondErrorAsync(JToken id, int code, string message) => Process.WriteLineAsync(GrokAcpRules.BuildError(id, code, message));

    /// <summary>還在等的請求一律以 error 結束（程序結束、連線關閉）</summary>
    public void FailAll(string error)
    {
        foreach (var (id, waiter) in pending.ToArray())
        {
            if (pending.TryRemove(id, out _))
                waiter.TrySetResult((null, Failure(error)));
        }
    }

    private static GrokAcpError Failure(string text) => new(0, text, null, text);
}
