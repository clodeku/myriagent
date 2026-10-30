using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Accounts;

/// <summary>
/// 短命的 codex app-server 連線：查帳戶與用量、在 app 內登入／登出時用，用完就關（權杖由 Codex 自己維護）。
/// 對話用的 app-server 由 CodexAgentSession 另外管理。
/// </summary>
internal sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly AgentProcess process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<(JObject? Result, string? Error)>> responses = new();
    private readonly CodexEventParser parser = new();
    private long nextId;

    /// <summary>伺服器通知（method、params），在背景執行緒觸發</summary>
    public event Action<string, JObject>? Notification;

    /// <summary>app-server 程序已結束（等通知的流程用它提早結束，不必等到逾時）</summary>
    public Task ExitTask => process.ExitTask;

    private CodexAppServerClient(AgentProcess process)
    {
        this.process = process;
    }

    public static async Task<CodexAppServerClient> StartAsync(AgentLauncher launcher, Action<IDictionary<string, string?>> configureEnvironment, CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.CacheDirectory);
        var process = AgentProcess.Start(launcher, new[] { "app-server", "-c", "suppress_unstable_features_warning=true" }, AppPaths.CacheDirectory, configureEnvironment);
        var client = new CodexAppServerClient(process);
        process.LineReceived += (_, line) => client.OnLine(line);
        process.Exited += (_, _) => client.FailAll("Codex 程序已結束");
        process.BeginReading();

        var (_, error) = await client.RequestAsync("initialize", CodexAppServerRules.BuildInitializeParams(AppVersionInfo.VersionText), TimeSpan.FromSeconds(30), ct);
        if (error != null)
        {
            await client.DisposeAsync();
            throw new InvalidOperationException($"Codex 初始化失敗：{error}");
        }
        await process.WriteLineAsync(CodexAppServerRules.BuildNotification("initialized"));
        return client;
    }

    private void OnLine(string line)
    {
        if (!line.TrimStart().StartsWith('{'))
            return;
        var result = parser.Parse(line);
        if (result.Response is { } response)
        {
            if (response.Id.Type == JTokenType.Integer && responses.TryRemove(response.Id.Value<long>(), out var pending))
                pending.TrySetResult((response.Result, response.Error));
            return;
        }
        if (result.ServerRequest != null)
        {
            // 查詢用連線不處理任何伺服器請求
            _ = WriteQuietlyAsync(CodexAppServerRules.BuildError(result.ServerRequest.Id, -32601, "不支援"));
            return;
        }
        if (result.Notification is { } notification)
        {
            try
            {
                Notification?.Invoke(notification.Method, notification.Params);
            }
            catch (Exception ex)
            {
                Log.Warn($"Codex 通知處理失敗：{ex.Message}");
            }
        }
    }

    public async Task<(JObject? Result, string? Error)> RequestAsync(string method, JObject? parameters, TimeSpan timeout, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<(JObject?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        responses[id] = completion;
        try
        {
            await process.WriteLineAsync(CodexAppServerRules.BuildRequest(id, method, parameters));
        }
        catch (IOException)
        {
            responses.TryRemove(id, out _);
            return (null, $"Codex 程序已結束：{process.GetStderrTail(300)}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await completion.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            responses.TryRemove(id, out _);
            ct.ThrowIfCancellationRequested();
            return (null, $"{method} 逾時");
        }
    }

    private async Task WriteQuietlyAsync(string line)
    {
        try
        {
            await process.WriteLineAsync(line);
        }
        catch (IOException)
        {
        }
    }

    private void FailAll(string error)
    {
        foreach (var (id, pending) in responses.ToArray())
        {
            if (responses.TryRemove(id, out _))
                pending.TrySetResult((null, error));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await process.CloseAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            FailAll("連線已關閉");
            process.Dispose();
        }
    }
}
