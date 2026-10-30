using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.RemoteExec;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.RemoteExec;

/// <summary>
/// 發起的一方的本機管道（真的具名管道、假的同步）：權杖、list、run 送出 remoteExec 並把結果與段落一行一行寫回、缺漏與重複、
/// myriagent-remote 送 cancel 或中途斷線時要求那台停止、被拒的結果
/// </summary>
public sealed class RemoteExecServiceTests : IDisposable
{
    private const string Conversation = "c0fb12cac6b5403eb81eafe1f32cb2f8";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly FakeTransport transport = new();

    private readonly RemoteExecService service;

    public RemoteExecServiceTests()
    {
        service = new RemoteExecService(transport, new NeverConfirmer(), Path.Combine(Path.GetTempPath(), "myriagent-rexec-svc"),
            "myriagent-agent-remote-test-" + Guid.NewGuid().ToString("N")[..12], _ => Task.FromResult<string?>("整理磁碟"), isElevated: () => false);
        service.StartPipe();
    }

    public void Dispose() => service.Dispose();

    [Fact]
    public async Task 權杖對不上就拒絕()
    {
        await using var client = await ConnectAsync();
        await client.SendAsync(new JObject { ["v"] = 1, ["op"] = "list", ["conversation"] = Conversation, ["token"] = new string('0', 64) });
        var line = await client.ReadAsync();
        line.Value<string>("t").Should().Be("error");
        line.Value<string>("code").Should().Be(RemoteExecPipeRules.ErrorToken);
    }

    [Fact]
    public async Task 列出電腦_未配對時說明()
    {
        transport.Directory = new RemoteExecDirectory(false, false, null, []);
        await using (var unpaired = await ConnectAsync())
        {
            await unpaired.SendAsync(Request("list"));
            (await unpaired.ReadAsync()).Value<string>("code").Should().Be(RemoteExecPipeRules.ErrorNotPaired);
        }

        transport.Directory = Paired();
        await using var client = await ConnectAsync();
        await client.SendAsync(Request("list"));
        var devices = await client.ReadAsync();
        devices.Value<string>("t").Should().Be("devices");
        devices.Value<string>("self").Should().Be("d_01J9QZ3K8M5R7T2V4W6X8Y0AAA");
        ((JArray)devices["devices"]!).Select(d => d.Value<string>("name")).Should().Equal("A", "B", "C");
        (await client.ReadOrEndAsync()).Should().BeNull("list 回完就關");
    }

    [Fact]
    public async Task 執行_送出remoteExec_結果與段落依序寫回_缺的送gap_重複的丟掉()
    {
        transport.Directory = Paired();
        await using var client = await ConnectAsync();
        var request = Request("run");
        request["device"] = "b";
        request["command"] = "Get-Date";
        request["cwd"] = @"C:\work";
        request["timeoutSec"] = 30;
        await client.SendAsync(request);

        var accepted = await client.ReadAsync();
        accepted.Value<string>("t").Should().Be("accepted");
        accepted.Value<string>("device").Should().Be("B");
        var (commandId, cmd) = await transport.NextSubmitAsync();
        cmd.Value<string>("cmd").Should().Be(RemoteCommandCatalog.RemoteExec);
        cmd.Value<string>("deviceId").Should().Be("d_01J9QZ3K8M5R7T2V4W6X8Y0BBB");
        cmd.Value<string>("label").Should().Be("整理磁碟");
        RemoteCommandShapeRules.Check(cmd).Should().BeNull();

        service.TryDeliverPeerReply(new PeerReply(commandId, new JObject { ["type"] = "commandResult", ["state"] = "done" })).Should().BeTrue();
        service.TryDeliverPeerReply(new PeerReply("01J9QZ3K8M5R7T2V4W6X8Y0ZZZ", new JObject { ["type"] = "commandResult" })).Should().BeFalse("不是這裡送的交給中樞");
        service.DeliverExecReply(new ExecChunk(commandId, 0, RemoteExecRules.KindStatus, "開始", null, RemoteExecRules.StatusStarted));
        service.DeliverExecReply(new ExecChunk(commandId, 1, RemoteExecRules.KindOut, "第一段"));
        service.DeliverExecReply(new ExecChunk(commandId, 1, RemoteExecRules.KindOut, "重複的"));
        service.DeliverExecReply(new ExecChunk(commandId, 3, RemoteExecRules.KindErr, "跳過了一段"));
        service.DeliverExecReply(new ExecChunk(commandId, 4, RemoteExecRules.KindExit, null, 5));

        var lines = new List<JObject>();
        while (await client.ReadOrEndAsync() is { } line)
            lines.Add(line);
        lines.Select(l => l.Value<string>("t")).Should().Equal("status", "out", "gap", "err", "exit");
        lines[1].Value<string>("text").Should().Be("第一段");
        lines[4].Value<int>("exitCode").Should().Be(5);
        transport.Cancels.Should().BeEmpty();
    }

    [Fact]
    public async Task 被拒的結果寫成錯誤()
    {
        transport.Directory = Paired();
        await using var client = await ConnectAsync();
        var request = Request("run");
        request["device"] = "B";
        request["command"] = "dir";
        await client.SendAsync(request);
        await client.ReadAsync();
        var (commandId, _) = await transport.NextSubmitAsync();
        service.TryDeliverPeerReply(new PeerReply(commandId, new JObject
        {
            ["type"] = "commandResult", ["state"] = "rejected", ["code"] = "policy", ["message"] = RemoteExecHubRules.NotAllowedMessage,
        }));
        var error = await client.ReadAsync();
        error.Value<string>("t").Should().Be("error");
        error.Value<string>("code").Should().Be(RemoteExecPipeRules.ErrorRejected);
        error.Value<string>("message").Should().Be(RemoteExecHubRules.NotAllowedMessage);
    }

    [Fact]
    public async Task 送出之前就知道不行的不送()
    {
        transport.Directory = Paired();
        foreach (var (device, code) in new[] { ("A", RemoteExecPipeRules.ErrorInvalid), ("C", RemoteExecPipeRules.ErrorOffline), ("zzz", RemoteExecPipeRules.ErrorUnknownDevice) })
        {
            await using var client = await ConnectAsync();
            var request = Request("run");
            request["device"] = device;
            request["command"] = "dir";
            await client.SendAsync(request);
            (await client.ReadAsync()).Value<string>("code").Should().Be(code, device);
        }

        transport.Directory = Paired() with { Live = false };
        await using (var offline = await ConnectAsync())
        {
            var request = Request("run");
            request["device"] = "B";
            request["command"] = "dir";
            await offline.SendAsync(request);
            (await offline.ReadAsync()).Value<string>("message").Should().Contain("沒有連上");
        }

        transport.Submits.Should().BeEmpty();
    }

    [Fact]
    public async Task 送cancel或中途斷線時要求那台停止()
    {
        transport.Directory = Paired();
        var client = await ConnectAsync();
        var request = Request("run");
        request["device"] = "B";
        request["command"] = "ping -n 100 127.0.0.1";
        await client.SendAsync(request);
        await client.ReadAsync();
        var (first, _) = await transport.NextSubmitAsync();
        await client.SendAsync(new JObject { ["op"] = "cancel" });
        await WaitForAsync(() => transport.Cancels.Contains(first));
        service.DeliverExecReply(new ExecChunk(first, 0, RemoteExecRules.KindExit, "已停止", 1, RemoteExecRules.ExitCancelled));
        (await client.ReadAsync()).Value<string>("code").Should().Be(RemoteExecRules.ExitCancelled);
        await client.DisposeAsync();

        var second = await ConnectAsync();
        await second.SendAsync(request);
        await second.ReadAsync();
        var (commandId, _) = await transport.NextSubmitAsync();
        await second.DisposeAsync();
        await WaitForAsync(() => transport.Cancels.Contains(commandId));
    }

    private static RemoteExecDirectory Paired() => new(true, true, "d_01J9QZ3K8M5R7T2V4W6X8Y0AAA",
    [
        new RemoteExecDevice("d_01J9QZ3K8M5R7T2V4W6X8Y0AAA", "A", null, Self: true, Online: true, false, true, "Off", false),
        new RemoteExecDevice("d_01J9QZ3K8M5R7T2V4W6X8Y0BBB", "B", null, Self: false, Online: true, false, true, "Control", true),
        new RemoteExecDevice("d_01J9QZ3K8M5R7T2V4W6X8Y0CCC", "C", null, Self: false, Online: false, false, true, "Control", true),
    ]);

    private JObject Request(string op) => new()
    {
        ["v"] = 1,
        ["op"] = op,
        ["conversation"] = Conversation,
        ["token"] = service.TokenFor(Conversation),
    };

    private async Task<PipeClient> ConnectAsync()
    {
        var pipe = new NamedPipeClientStream(".", service.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync((int)Wait.TotalMilliseconds);
        return new PipeClient(pipe);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Wait)
                throw new TimeoutException("等不到條件成立");
            await Task.Delay(20);
        }
    }

    private sealed class PipeClient(NamedPipeClientStream pipe) : IAsyncDisposable
    {
        private readonly StreamReader reader = new(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);

        public async Task SendAsync(JObject message)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();
        }

        /// <summary>下一行（連線結束了就失敗）</summary>
        public async Task<JObject> ReadAsync() => await ReadOrEndAsync() ?? throw new InvalidOperationException("連線已經結束");

        /// <summary>下一行；連線結束了回 null</summary>
        public async Task<JObject?> ReadOrEndAsync()
        {
            using var timeout = new CancellationTokenSource(Wait);
            var line = await reader.ReadLineAsync(timeout.Token);
            return line is null ? null : JObject.Parse(line);
        }

        public async ValueTask DisposeAsync()
        {
            reader.Dispose();
            await pipe.DisposeAsync();
        }
    }

    private sealed class FakeTransport : IRemoteExecTransport
    {
        private readonly ConcurrentQueue<(string, JObject)> submits = new();

        public RemoteExecDirectory Directory { get; set; } = new(true, true, null, []);

        public IReadOnlyList<(string CommandId, JObject Cmd)> Submits => submits.ToList();

        public ConcurrentBag<string> Cancels { get; } = [];

        public Task<RemoteExecDirectory> GetDirectoryAsync() => Task.FromResult(Directory);

        public void SubmitPeerCommand(string commandId, JObject cmd) => submits.Enqueue((commandId, cmd));

        public void CancelExec(string commandId) => Cancels.Add(commandId);

        public void SendExecOutput(ExecChunk chunk)
        {
        }

        public async Task<(string CommandId, JObject Cmd)> NextSubmitAsync()
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                if (submits.TryDequeue(out var next))
                    return next;
                if (watch.Elapsed > Wait)
                    throw new TimeoutException("沒有送出 remoteExec");
                await Task.Delay(20);
            }
        }
    }

    private sealed class NeverConfirmer : IRemoteExecConfirmer
    {
        public Task<RemoteExecConfirmResult> ConfirmAsync(RemoteExecConfirmation confirmation, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(RemoteExecConfirmResult.Denied);
    }
}
