using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Accounts;

/// <summary>
/// 不開對話、只問控制請求的短命 Claude 程序（<c>claude -p --input-format stream-json</c>，不送任何訊息、不用額度）：帳戶層的 CLI 代查用量
/// （get_usage）與 app 內登入（claude_authenticate、claude_oauth_wait_for_completion、claude_oauth_callback）。
/// 精簡模式（不跑 hooks、不載入外掛與 MCP，起來快）、不留工作階段的紀錄（--no-session-persistence）。
/// 實測見 docs/protocol-notes.md「Claude：CLI 代查用量、控制請求登入、標題產生、健康度清單與一輪中的事件」
/// </summary>
internal sealed class ClaudeControlClient : IAsyncDisposable
{
    private readonly AgentProcess process;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ClaudeControlResponse?>> waiters = new(StringComparer.Ordinal);
    private long nextId;

    private ClaudeControlClient(AgentProcess process) => this.process = process;

    /// <summary>程序已結束</summary>
    public Task ExitTask => process.ExitTask;

    public static IReadOnlyList<string> Arguments { get; } =
    [
        "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
        "--no-session-persistence", "--safe-mode", "--setting-sources", "project,local",
    ];

    /// <summary>
    /// 啟動並 initialize。killWithApp＝false 時不加入 app 的工作物件（CLI 代查用量時可能正在換發權杖：app 結束時不能在途中被砍，
    /// 否則 refresh token 可能已經用掉、新的還沒寫回）；stdin 關了 CLI 自己會結束
    /// </summary>
    public static async Task<ClaudeControlClient> StartAsync(AgentLauncher launcher, Action<IDictionary<string, string?>> configureEnvironment,
        bool killWithApp, CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.CacheDirectory);
        var process = await AgentProcess.StartWithRetryAsync(launcher, Arguments, AppPaths.CacheDirectory, configureEnvironment, ct, joinJob: killWithApp);
        var client = new ClaudeControlClient(process);
        process.LineReceived += (_, line) => client.OnLine(line);
        process.Exited += (_, _) => client.FailAll();
        process.BeginReading();
        ClaudeControlResponse? init;
        try
        {
            init = await client.RequestAsync(new JObject { ["subtype"] = "initialize" }, TimeSpan.FromSeconds(30), ct);
        }
        catch
        {
            // 取消（帳戶移除、app 結束）：不留下程序
            await client.DisposeAsync();
            throw;
        }
        if (init is not { Success: true })
        {
            var tail = process.GetStderrTail(300);
            await client.DisposeAsync();
            throw new InvalidOperationException("Claude 初始化失敗：" + (init?.Error ?? (string.IsNullOrWhiteSpace(tail) ? "沒有回應" : tail)));
        }
        return client;
    }

    private void OnLine(string line)
    {
        if (!line.TrimStart().StartsWith('{'))
            return;
        JObject message;
        try
        {
            message = JObject.Parse(line);
        }
        catch (Exception)
        {
            return;
        }
        switch (message.Value<string>("type"))
        {
            case "control_response":
                if (ClaudeStreamRules.ParseControlResponseMessage(message) is { } response && waiters.TryRemove(response.RequestId, out var waiter))
                    waiter.TrySetResult(response);
                break;
            case "control_request":
                // 沒有對話，不該有權限詢問之類的請求：一律拒絕
                if (message["request_id"] is JValue { Type: JTokenType.String } id)
                    _ = WriteQuietlyAsync(ClaudeStreamRules.BuildErrorResponse(id.ToString(), "不支援"));
                break;
        }
    }

    /// <summary>送一個控制請求、等它的回應；逾時、程序結束回 null（取消照常丟出）</summary>
    public async Task<ClaudeControlResponse?> RequestAsync(JObject request, TimeSpan timeout, CancellationToken ct = default)
    {
        var requestId = "c" + Interlocked.Increment(ref nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<ClaudeControlResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiters[requestId] = completion;
        // 程序已經結束（Exited 在 stdout 讀完之後才觸發，那時登記的等待已經失敗過了）：不必等到逾時
        if (process.HasExited)
        {
            waiters.TryRemove(requestId, out _);
            return null;
        }
        try
        {
            await process.WriteLineAsync(ClaudeStreamRules.BuildControlRequest(requestId, request));
        }
        catch (IOException)
        {
            waiters.TryRemove(requestId, out _);
            return null;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await completion.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            waiters.TryRemove(requestId, out _);
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>stderr 的最後幾行（錯誤說明用）</summary>
    public string StderrTail(int maxChars = 300) => process.GetStderrTail(maxChars);

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

    private void FailAll()
    {
        foreach (var (id, waiter) in waiters.ToArray())
        {
            if (waiters.TryRemove(id, out _))
                waiter.TrySetResult(null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await process.CloseAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            FailAll();
            process.Dispose();
        }
    }
}
