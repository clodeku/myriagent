using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>每個主對話一個具目前 Windows 使用者限制的管道；MCP 子程序只轉送 JSON，不載入帳戶憑證。</summary>
internal sealed class DelegationMcpServer : IDisposable
{
    private readonly CancellationTokenSource shutdown = new();
    private readonly Func<string, JObject, CancellationToken, Task<JObject>> call;
    private readonly ConcurrentDictionary<int, Task> connections = new();
    private int sequence;

    public DelegationMcpServer(Func<string, JObject, CancellationToken, Task<JObject>> call)
    {
        this.call = call;
        PipeName = "myriagent-delegation-" + Guid.NewGuid().ToString("N");
        _ = AcceptAsync();
    }

    public string PipeName { get; }

    private async Task AcceptAsync()
    {
        while (!shutdown.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
                var id = Interlocked.Increment(ref sequence);
                var connected = pipe;
                pipe = null;
                var task = ServeAsync(connected);
                connections[id] = task;
                _ = RemoveConnectionAsync(id, task);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (IOException) { break; }
            finally { pipe?.Dispose(); }
        }
    }

    private async Task RemoveConnectionAsync(int id, Task task)
    {
        await task.ConfigureAwait(false);
        connections.TryRemove(id, out _);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
        using (var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" })
        {
            try
            {
                var line = await reader.ReadLineAsync(cancelled.Token).ConfigureAwait(false);
                if (line == null || line.Length > DelegationMcpRules.MaxWireLength)
                    return;
                var request = JObject.Parse(line);
                // MCP 程序被停止或工具呼叫被取消時，連線關閉也要停止那個子工作。
                var disconnected = reader.ReadLineAsync(cancelled.Token).AsTask();
                var response = DispatchAsync(request, cancelled.Token);
                if (await Task.WhenAny(response, disconnected).ConfigureAwait(false) != response)
                    cancelled.Cancel();
                var result = await response.ConfigureAwait(false);
                if (result != null && !cancelled.IsCancellationRequested)
                    await writer.WriteLineAsync(result.ToString(Formatting.None).AsMemory(), cancelled.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or ObjectDisposedException) { }
            finally { cancelled.Cancel(); }
        }
    }

    private async Task<JObject?> DispatchAsync(JObject request, CancellationToken ct)
    {
        var id = request["id"];
        if (id == null)
            return null;
        // params 可能沒有或是 JSON null：一律先轉 JObject 再取欄位
        var parameters = request["params"] as JObject;
        try
        {
            var result = DelegationMcpRules.StringField(request, "method") switch
            {
                "initialize" => new JObject
                {
                    ["protocolVersion"] = DelegationMcpRules.NegotiateProtocol(DelegationMcpRules.StringField(parameters, "protocolVersion")),
                    ["capabilities"] = new JObject { ["tools"] = new JObject() },
                    ["serverInfo"] = new JObject { ["name"] = DelegationMcpRules.ServerName, ["version"] = "1.0.0" },
                },
                "ping" => new JObject(),
                "tools/list" => DelegationMcpRules.Tools(),
                "tools/call" => await call(DelegationMcpRules.StringField(parameters, "name") ?? "", parameters?["arguments"] as JObject ?? new JObject(), ct).ConfigureAwait(false),
                _ => null,
            };
            return result == null ? DelegationMcpRules.Error(id, -32601, "不支援的 MCP 方法") : DelegationMcpRules.Result(id, result);
        }
        catch (OperationCanceledException)
        {
            return DelegationMcpRules.Error(id, -32800, "委派已取消");
        }
        catch (Exception)
        {
            return DelegationMcpRules.Error(id, -32603, "帳戶委派工具執行失敗");
        }
    }

    public void Dispose() => shutdown.Cancel();
}

/// <summary>由 CLI 啟動的 stdio MCP 橋接。入口在任何桌面初始化之前，stdout 只有 MCP JSON。</summary>
internal static class DelegationMcpBridge
{
    public static async Task<int> RunAsync(string pipeName, TextReader input, TextWriter output)
    {
        if (!DelegationMcpRules.IsPipeName(pipeName))
            return 2;
        using var stopped = new CancellationTokenSource();
        using var writing = new SemaphoreSlim(1);
        var requests = new ConcurrentDictionary<string, CancellationTokenSource>();
        var tasks = new List<Task>();
        try
        {
            while (await input.ReadLineAsync(stopped.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length > DelegationMcpRules.MaxWireLength)
                    continue;
                JObject request;
                try { request = JObject.Parse(line); }
                catch (JsonException)
                {
                    await WriteAsync(DelegationMcpRules.Error(null, -32700, "無法解析 JSON")).ConfigureAwait(false);
                    continue;
                }
                if (DelegationMcpRules.StringField(request, "method") == "notifications/cancelled")
                {
                    var key = (request["params"] as JObject)?["requestId"]?.ToString(Formatting.None);
                    if (key != null && requests.TryGetValue(key, out var cancellation))
                    {
                        try { cancellation.Cancel(); }
                        catch (ObjectDisposedException) { }
                    }
                    continue;
                }
                if (request["id"] is not { } id)
                    continue;
                var keyId = id.ToString(Formatting.None);
                var source = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                if (!requests.TryAdd(keyId, source))
                {
                    source.Dispose();
                    continue;
                }
                tasks.RemoveAll(t => t.IsCompleted);
                tasks.Add(ForwardAsync(request, keyId, source));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        finally
        {
            stopped.Cancel();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        return 0;

        async Task ForwardAsync(JObject request, string key, CancellationTokenSource source)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(10000, source.Token).ConfigureAwait(false);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
                await writer.WriteLineAsync(request.ToString(Formatting.None).AsMemory(), source.Token).ConfigureAwait(false);
                if (await reader.ReadLineAsync(source.Token).ConfigureAwait(false) is { } response)
                    await WriteAsync(JObject.Parse(response)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException)
            {
                if (!stopped.IsCancellationRequested)
                    await WriteAsync(DelegationMcpRules.Error(request["id"], -32603, "帳戶委派連線已中斷或取消")).ConfigureAwait(false);
            }
            finally
            {
                requests.TryRemove(key, out _);
                source.Dispose();
            }
        }

        async Task WriteAsync(JObject response)
        {
            await writing.WaitAsync().ConfigureAwait(false);
            try
            {
                await output.WriteLineAsync(response.ToString(Formatting.None)).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException) { stopped.Cancel(); }
            finally { writing.Release(); }
        }
    }
}
