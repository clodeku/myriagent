using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.RemoteExec;

/// <summary>遠端執行指令經同步送出與收回（<see cref="SyncCoordinator"/> 實作；docs/sync-protocol.md 14.11）</summary>
internal interface IRemoteExecTransport
{
    /// <summary>同步的現況（在 UI 執行緒取快照）</summary>
    Task<RemoteExecDirectory> GetDirectoryAsync();

    /// <summary>送一個這台下的 <c>remoteExec</c>（任何執行緒）：結果以 <see cref="IRemoteExecEndpoint.TryDeliverPeerReply"/> 回來</summary>
    void SubmitPeerCommand(string commandId, JObject cmd);

    /// <summary>停止這台下的指令（任何執行緒）</summary>
    void CancelExec(string commandId);

    /// <summary>被操作的一方：送一段輸出（任何執行緒）</summary>
    void SendExecOutput(ExecChunk chunk);
}

/// <summary>協調者交來的遠端執行指令訊息（任何執行緒）</summary>
internal interface IRemoteExecEndpoint
{
    /// <summary>這台下的指令的結果（<c>PeerReply</c>）：是這裡送出的就接下、回 true；其他（桌面遠端操作的）回 false 交給中樞</summary>
    bool TryDeliverPeerReply(PeerReply reply);

    /// <summary>這台下的指令的一段輸出（<c>ExecReply</c>）</summary>
    void DeliverExecReply(ExecChunk chunk);

    /// <summary>被操作的一方：停止這台正在執行的指令（<c>ExecAbort</c>，或伺服器不再轉送）</summary>
    void AbortExec(string commandId, bool orphaned);
}

/// <summary>
/// 遠端執行指令的桌面端（docs/sync-protocol.md 14.11；2026-10-25 擁有者要求：對話裡的代理經 <c>myriagent-remote</c> 在其他配對的電腦執行主控台指令）：
/// <list type="bullet">
/// <item>被操作的一方：<see cref="Runner"/>（中樞的處理者交給它；輸出經 <see cref="IRemoteExecTransport.SendExecOutput"/> 送出）。</item>
/// <item>發起的一方：本機具名管道（<see cref="RemoteExecPipeRules"/>）收 <c>myriagent-remote</c> 的要求，經同步送 <c>remoteExec</c>，把結果與輸出一行一行寫回去；
/// myriagent-remote 中途結束（被 Ctrl+C、代理的工具逾時）或送 cancel 時要求那台停止。</item>
/// </list>
/// 任何執行緒都能呼叫（一把鎖）
/// </summary>
internal sealed class RemoteExecService : IRemoteExecEndpoint, IDisposable
{
    private readonly IRemoteExecTransport transport;
    private readonly Func<string, Task<string?>> conversationTitle;
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    private readonly object gate = new();
    private readonly Dictionary<string, ClientSession> sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private readonly TimeProvider time;
    private Task? acceptLoop;

    /// <param name="transport">同步（正式是 SyncCoordinator）</param>
    /// <param name="confirmer">高風險指令在這台問人</param>
    /// <param name="scriptDirectory">被操作時 PowerShell 的暫存腳本</param>
    /// <param name="pipeName">本機管道的名稱（<see cref="RemoteExecPipeRules.PipeName"/>）</param>
    /// <param name="conversationTitle">對話的標題（UI 執行緒取；給那台的人看是哪個對話下的）</param>
    public RemoteExecService(IRemoteExecTransport transport, IRemoteExecConfirmer confirmer, string scriptDirectory, string pipeName,
        Func<string, Task<string?>> conversationTitle, TimeProvider? time = null, Func<bool>? isElevated = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.conversationTitle = conversationTitle ?? throw new ArgumentNullException(nameof(conversationTitle));
        this.time = time ?? TimeProvider.System;
        PipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
        Runner = new RemoteExecRunner(transport.SendExecOutput, confirmer, scriptDirectory, isElevated);
    }

    /// <summary>被操作的一方的執行者（中樞以 AttachRemoteExec 接上）</summary>
    public RemoteExecRunner Runner { get; }

    /// <summary>本機管道的名稱（給對話的 CLI 的環境變數）</summary>
    public string PipeName { get; }

    /// <summary>myriagent-schedule 的定時訊息（AppController 接到中樞，在 UI 執行緒處理）：對話 id、要求 → 結果；沒接時回錯誤</summary>
    public Func<string, JObject, Task<JObject>>? TimedHandler { get; set; }

    /// <summary>這個對話的權杖（給對話的 CLI 的環境變數）</summary>
    public string TokenFor(string conversationId) => RemoteExecPipeRules.Token(secret, conversationId);

    /// <summary>結果最多等多久：指令的上限＋等待同意＋路上的時間</summary>
    internal static TimeSpan ResultLimit(int? timeoutSeconds)
        => TimeSpan.FromSeconds(RemoteExecRules.TimeoutSeconds(timeoutSeconds)) + RemoteExecRules.RelayGrace;

    /// <summary>開始收 myriagent-remote 的連線（只呼叫一次）</summary>
    public void StartPipe()
    {
        lock (gate)
            acceptLoop ??= Task.Run(() => AcceptLoopAsync(stopping.Token));
    }

    public bool TryDeliverPeerReply(PeerReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ClientSession? session;
        lock (gate)
            sessions.TryGetValue(reply.CommandId, out session);
        if (session is null)
            return false;
        session.OnResult(reply.Message);
        return true;
    }

    public void DeliverExecReply(ExecChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ClientSession? session;
        lock (gate)
            sessions.TryGetValue(chunk.CommandId, out session);
        session?.OnChunk(chunk);
    }

    public void AbortExec(string commandId, bool orphaned) => Runner.Abort(commandId, orphaned);

    public void Dispose()
    {
        stopping.Cancel();
        Runner.Dispose();
        List<ClientSession> all;
        lock (gate)
            all = sessions.Values.ToList();
        foreach (var session in all)
            session.Fail(RemoteExecPipeRules.ErrorUnknown, "這台的 Clode Myriagent 結束了，不知道遠端指令有沒有執行完");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"遠端執行指令的本機管道開不起來（{ex.Message}），5 秒後再試");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = HandleClientAsync(server, ct);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        await using var _ = server;
        var utf8 = new UTF8Encoding(false);
        using var reader = new StreamReader(server, utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var writing = WriteLoopAsync(server, output.Reader, utf8);
        try
        {
            string? first;
            using (var firstLine = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                firstLine.CancelAfter(TimeSpan.FromSeconds(10));
                first = await reader.ReadLineAsync(firstLine.Token).ConfigureAwait(false);
            }

            var (request, problem) = RemoteExecPipeRules.ParseRequest(first);
            if (request is null)
            {
                output.Writer.TryWrite(RemoteExecPipeRules.Error(RemoteExecPipeRules.ErrorInvalid, problem!));
                return;
            }

            if (!RemoteExecPipeRules.VerifyToken(secret, request.Conversation, request.Token))
            {
                output.Writer.TryWrite(RemoteExecPipeRules.Error(RemoteExecPipeRules.ErrorToken,
                    "權杖對不上：myriagent-remote 只能在這台 Clode Myriagent 的對話裡執行（app 更新重開之後，請讓對話重新開始一輪再試）"));
                return;
            }

            // 定時訊息（myriagent-schedule）：不需要配對
            if (request.Op == RemoteExecPipeRules.OpTimed)
            {
                var result = TimedHandler is { } handler
                    ? await handler(request.Conversation, request.Timed!).ConfigureAwait(false)
                    : new JObject { ["ok"] = false, ["message"] = "這台的 Clode Myriagent 沒有開定時訊息" };
                result["t"] = "timed";
                output.Writer.TryWrite(result.ToString(Newtonsoft.Json.Formatting.None));
                return;
            }

            var directory = await transport.GetDirectoryAsync().ConfigureAwait(false);
            if (!directory.Paired || directory.SelfDeviceId is null)
            {
                output.Writer.TryWrite(RemoteExecPipeRules.Error(RemoteExecPipeRules.ErrorNotPaired, "這台電腦還沒有和 MyriagentHub 配對，沒有其他電腦可以操作"));
                return;
            }

            if (request.Op == RemoteExecPipeRules.OpList)
            {
                output.Writer.TryWrite(RemoteExecPipeRules.Devices(directory));
                return;
            }

            await RunAsync(request, directory, reader, output.Writer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("處理 myriagent-remote 的要求失敗", ex);
            output.Writer.TryWrite(RemoteExecPipeRules.Error(RemoteExecPipeRules.ErrorUnknown, "這台的 Clode Myriagent 處理要求時出錯：" + ex.Message));
        }
        finally
        {
            output.Writer.TryComplete();
            try
            {
                await writing.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    private async Task RunAsync(RemoteExecPipeRequest request, RemoteExecDirectory directory, StreamReader reader, ChannelWriter<string> output, CancellationToken ct)
    {
        var (device, problem) = RemoteExecPipeRules.Resolve(directory.Devices, request.Device!);
        if (device is null)
        {
            output.TryWrite(RemoteExecPipeRules.Error(RemoteExecPipeRules.ErrorUnknownDevice, problem!));
            return;
        }

        if (RemoteExecPipeRules.Precheck(device) is { } precheck)
        {
            output.TryWrite(RemoteExecPipeRules.Error(device.Self ? RemoteExecPipeRules.ErrorInvalid : RemoteExecPipeRules.ErrorOffline, precheck));
            return;
        }

        if (!directory.Live)
        {
            output.TryWrite(RemoteExecPipeRules.Error(RemoteExecPipeRules.ErrorOffline, "這台目前沒有連上 MyriagentHub（或伺服器還不支援遠端執行指令），請稍後再試"));
            return;
        }

        string? label = null;
        try
        {
            label = await conversationTitle(request.Conversation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"取不到對話標題：{ex.Message}");
        }

        var commandId = SyncIds.NewUlid(time.GetUtcNow().ToUnixTimeMilliseconds());
        var session = new ClientSession(output);
        lock (gate)
            sessions[commandId] = session;
        try
        {
            Log.Info($"對話 {request.Conversation} 經 myriagent-remote 在「{device.Name}」執行指令（{commandId}）：{RemoteExecRunRules.Preview(request.Command!)}");
            output.TryWrite(RemoteExecPipeRules.Accepted(device));
            transport.SubmitPeerCommand(commandId, RemoteExecPipeRules.Command(device, request, label));

            // 等結束碼；同時讀 myriagent-remote 送來的 cancel，讀到檔案結尾（myriagent-remote 結束了）就要求那台停止
            using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var gone = WatchClientAsync(reader, commandId, watching.Token);
            var limit = Task.Delay(ResultLimit(request.TimeoutSeconds), watching.Token);
            var first = await Task.WhenAny(session.Done, gone, limit).ConfigureAwait(false);
            if (first == gone && !session.Done.IsCompleted)
            {
                Log.Info($"myriagent-remote 中途結束：要求停止遠端執行指令 {commandId}");
                transport.CancelExec(commandId);
            }
            else if (first == limit && !session.Done.IsCompleted)
            {
                session.Fail(RemoteExecPipeRules.ErrorUnknown, "等不到那台的結果（連線中斷或那台的 app 結束），不知道指令有沒有執行完");
            }

            watching.Cancel();
        }
        finally
        {
            lock (gate)
                sessions.Remove(commandId);
        }
    }

    /// <summary>讀 myriagent-remote 之後送的行：cancel 就要求停止（繼續等結束碼）；讀到結尾或讀不下去時結束</summary>
    private async Task WatchClientAsync(StreamReader reader, string commandId, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                    return;
                if (line.Contains("\"" + RemoteExecPipeRules.OpCancel + "\"", StringComparison.Ordinal))
                {
                    Log.Info($"myriagent-remote 要求停止遠端執行指令 {commandId}");
                    transport.CancelExec(commandId);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
            // 結束碼到了：不再讀
        }
    }

    private static async Task WriteLoopAsync(Stream stream, ChannelReader<string> lines, Encoding encoding)
    {
        await foreach (var line in lines.ReadAllAsync().ConfigureAwait(false))
        {
            var bytes = encoding.GetBytes(line + "\n");
            await stream.WriteAsync(bytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>一個 run 的連線：結果與段落轉成一行一行（依 seq；缺的送 gap、重複的丟掉）</summary>
    private sealed class ClientSession(ChannelWriter<string> output)
    {
        private readonly object gate = new();
        private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long nextSeq;

        public Task Done => done.Task;

        public void OnResult(JObject message)
        {
            if (message["type"] is not JValue { Value: "commandResult" })
                return;
            var state = message["state"] is JValue { Type: JTokenType.String, Value: string s } ? s : null;
            var text = message["message"] is JValue { Type: JTokenType.String, Value: string m } && m.Length > 0 ? m : null;
            var code = message["code"] is JValue { Type: JTokenType.String, Value: string c } ? c : null;
            switch (state)
            {
                case RemoteResultStates.Done:
                    // 那台接下了：輸出與結束碼走串流
                    break;
                case RemoteResultStates.Rejected:
                    Fail(RemoteExecPipeRules.ErrorRejected, text ?? RejectText(code));
                    break;
                default:
                    Fail(RemoteExecPipeRules.ErrorUnknown, text ?? "不知道那台有沒有執行這個指令（結果不明）");
                    break;
            }
        }

        public void OnChunk(ExecChunk chunk)
        {
            lock (gate)
            {
                if (done.Task.IsCompleted || chunk.Seq < nextSeq)
                    return;
                if (chunk.Seq > nextSeq)
                    output.TryWrite(RemoteExecPipeRules.Gap());
                nextSeq = chunk.Seq + 1;
                output.TryWrite(RemoteExecPipeRules.Chunk(chunk));
                if (chunk.Kind == RemoteExecRules.KindExit)
                    done.TrySetResult();
            }
        }

        public void Fail(string code, string message)
        {
            lock (gate)
            {
                if (done.Task.IsCompleted)
                    return;
                output.TryWrite(RemoteExecPipeRules.Error(code, message));
                done.TrySetResult();
            }
        }

        private static string RejectText(string? code) => code switch
        {
            RemoteResultCodes.Policy => "那台的設定不允許這個指令",
            RemoteResultCodes.Offline => "那台目前不在線上",
            RemoteResultCodes.Unsupported => "那台的 Clode Myriagent 不支援遠端執行指令",
            RemoteResultCodes.Busy => "那台現在很忙，請稍後再試",
            "not_sent" => "這台目前沒有連上伺服器，指令沒有送出",
            "rate_limited" => "送得太快，請稍後再試",
            _ => $"那台拒絕了這個指令（{code ?? "沒有原因"}）",
        };
    }
}
