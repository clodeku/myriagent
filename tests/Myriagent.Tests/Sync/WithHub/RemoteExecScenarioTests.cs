using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.RemoteExec;
using Myriagent.Sync;
using Myriagent.Tests.Sync.LongRun;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using LongRunGate = Myriagent.Tests.Sync.LongRun.LongRun;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>
/// 遠端執行指令的端到端（docs/sync-protocol.md 14.11）：兩台整條桌面（tools/fake-device 的 DeviceRuntime，接上 RemoteExecService）配對到同一棵樹的 MyriagentHub 子程序，
/// 由真的 myriagent-remote.exe（測試輸出裡的那支）經 A 的本機管道下指令 → A 送 PeerCmd → 伺服器簽信封 → B 驗章、閘門、真的執行 cmd 與 PowerShell → 輸出經 ExecReply 回到 myriagent-remote。
/// 另驗高風險被 B 拒絕、myriagent-remote 中途被砍時 B 停止、B 關掉開關之後被拒、B 的遠端操作紀錄
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class RemoteExecScenarioTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private const string Conversation = "5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d";

    private static readonly FakeAccountSpec AccountA = LongRunDevice.Account("exec-a", AgentProvider.Claude, "exec-a@example.test", "org-exec-a");

    private static readonly FakeAccountSpec AccountB = LongRunDevice.Account("exec-b", AgentProvider.Claude, "exec-b@example.test", "org-exec-b");

    [EnvFact(LongRunGate.Gate, "遠端執行指令的端到端：myriagent-remote → 桌面 A → 同一棵樹的 MyriagentHub → 桌面 B 真的執行 → 輸出回 myriagent-remote")]
    public async Task 代理經myriagent_remote在另一台執行指令()
    {
        var keysBefore = UserKeyStore.FilesCreatedByTestHub();
        var hub = await MyriagentHubProcess.StartAsync(publicOriginIsBaseAddress: true);
        try
        {
            await ScenarioAsync(hub);
        }
        finally
        {
            await hub.DisposeAsync();
        }

        hub.ForcedKill.Should().BeFalse("MyriagentHub 要能正常停止（強制結束會留下孤兒金鑰檔）");
        UserKeyStore.FilesCreatedByTestHub().Except(keysBefore).Should().BeEmpty("正常停止的 MyriagentHub 不留下金鑰檔");
    }

    private async Task ScenarioAsync(MyriagentHubProcess hub)
    {
        using var owner = await OwnerClient.LoginAsync(hub);
        var confirmerB = new ScriptedConfirmer();
        var (dirA, pairedA) = await LongRunDevice.PairAsync(hub, owner, "exec-a");
        await using var a = await LongRunDevice.StartPairedAsync(dirA, pairedA, "exec-a", [AccountA], HubEngine.FastBackoff(0.2, 0.5, 1),
            remoteExecConfirmer: new ScriptedConfirmer());
        var (dirB, pairedB) = await LongRunDevice.PairAsync(hub, owner, "exec-b");
        await using var b = await LongRunDevice.StartPairedAsync(dirB, pairedB, "exec-b", [AccountB], HubEngine.FastBackoff(0.2, 0.5, 1),
            remoteExecConfirmer: confirmerB);
        await a.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Exec1), Wait, "A 連上、協商出 exec1");
        await b.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Exec1), Wait, "B 連上、協商出 exec1");

        // 0. B 還沒開放：myriagent-remote 在送出之前就說明
        await WaitForCliAsync(a, r => r.ExitCode == 125 && r.Stderr.Contains("允許其他電腦的代理在這台執行指令"), "B 沒開放時 run 回 125",
            "run", "exec-b", "--shell", "cmd", "--", "echo nope");

        // B 允許遠端操作與遠端執行指令（預設都關）
        var generation = await b.Inspect(v => v.ConnectionGeneration);
        await b.Runtime.CommandAsync("syncSetPolicy", new JObject { ["remoteControlLevel"] = "Control", ["allowRemoteExec"] = true });
        await b.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.ConnectionGeneration > generation && v.Features.Contains(DeviceFeatureRules.Exec1), Wait,
            "B 以新的政策重新連上");
        var listed = await WaitForCliAsync(a, r => r.ExitCode == 0 && ListedAllowed(r.Stdout, "exec-b"), "A 的 list 看到 B 可以執行", "list", "--json");
        output.WriteLine("myriagent-remote list：" + listed.Stdout);

        // 1. cmd：輸出與結束碼
        var cmd = await RunCliAsync(a, "run", "exec-b", "--shell", "cmd", "--", "echo hello-from-b& echo warn-from-b 1>&2& exit /b 3");
        output.WriteLine($"cmd：exit={cmd.ExitCode} stdout={cmd.Stdout} stderr={cmd.Stderr}");
        cmd.ExitCode.Should().Be(3, cmd.Stderr);
        cmd.Stdout.Should().Contain("hello-from-b");
        cmd.Stderr.Should().Contain("warn-from-b").And.Contain("已送到「exec-b」");

        // 2. PowerShell（預設殼層）：中文輸出
        var ps = await RunCliAsync(a, "run", "exec-b", "--", "Write-Output ('中文' + '輸出 ' + (1 + 2))");
        ps.ExitCode.Should().Be(0, ps.Stderr);
        ps.Stdout.Should().Contain("中文輸出 3");

        // 3. 高風險：B 的人拒絕 → 126，沒有執行
        confirmerB.Answer = RemoteExecConfirmResult.Denied;
        var marker = Path.Combine(dirB.Root, "ran.txt");
        var denied = await RunCliAsync(a, "run", "exec-b", "--shell", "cmd", "--", $"echo shutdown > \"{marker}\"");
        denied.ExitCode.Should().Be(126, denied.Stderr);
        denied.Stderr.Should().Contain("等待這台電腦的人").And.Contain("拒絕");
        File.Exists(marker).Should().BeFalse("拒絕了就不執行");
        confirmerB.Asked.Should().ContainSingle().Which.IssuerLabel.Should().Be("電腦「exec-a」");

        // 4. myriagent-remote 中途被砍（代理的工具逾時）：B 停止那個指令
        using (var running = StartCli(a, "run", "exec-b", "--shell", "cmd", "--", "ping -n 120 127.0.0.1"))
        {
            await WaitAsync(() => b.Runtime.RemoteExec!.Runner.RunningCount == 1, "B 開始執行 ping");
            running.Kill(entireProcessTree: true);
            await WaitAsync(() => b.Runtime.RemoteExec!.Runner.RunningCount == 0, "myriagent-remote 被砍之後 B 停止 ping");
        }

        // B 的遠端操作紀錄：發起者是「電腦「exec-a」」
        var audit = await ((ISyncEngineCommands)await b.EngineAsync()).LoadRemoteAuditAsync(null, 20, CancellationToken.None);
        foreach (var entry in audit)
            output.WriteLine($"B 的 remote_audit：{entry.Cmd} {entry.State} {entry.Code} step={entry.Step} issuer={entry.IssuerLabel}");
        audit.Count(e => e.Cmd == RemoteCommandCatalog.RemoteExec && e.State == RemoteResultStates.Done && e.IssuerLabel == "電腦「exec-a」")
            .Should().BeGreaterThanOrEqualTo(4);

        // 5. B 關掉開關：之後被拒（A 看到新的政策之前由 B 的閘門拒絕，之後在送出前就擋）
        generation = await b.Inspect(v => v.ConnectionGeneration);
        await b.Runtime.CommandAsync("syncSetPolicy", new JObject { ["allowRemoteExec"] = false });
        await b.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.ConnectionGeneration > generation, Wait, "B 以新的政策重新連上");
        // B 重新連線的那一下，A 可能先看到 B 離線（也是 125）：等到說明是「沒開放」
        var refused = await WaitForCliAsync(a, r => r.ExitCode == 125 && r.Stderr.Contains("允許其他電腦的代理在這台執行指令"), "B 關掉之後 run 回 125",
            "run", "exec-b", "--shell", "cmd", "--", "echo nope");
        refused.Stdout.Should().NotContain("nope");
    }

    private static bool ListedAllowed(string json, string name)
    {
        try
        {
            return JArray.Parse(json).OfType<JObject>().Any(d => d.Value<string>("name") == name && d.Value<bool>("online") && d.Value<bool>("exec") && d.Value<bool>("allowExec"));
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return false;
        }
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    private static Process StartCli(LongRunDevice device, params string[] args)
    {
        var service = device.Runtime.RemoteExec ?? throw new InvalidOperationException("沒有接上遠端執行指令");
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "myriagent-remote.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.Environment[RemoteExecPipeRules.PipeVariable] = service.PipeName;
        psi.Environment[RemoteExecPipeRules.TokenVariable] = service.TokenFor(Conversation);
        psi.Environment["MYRIAGENT_AGENT_CONVERSATION"] = Conversation;
        return Process.Start(psi) ?? throw new InvalidOperationException("myriagent-remote 沒有啟動");
    }

    private static async Task<CliResult> RunCliAsync(LongRunDevice device, params string[] args)
    {
        using var process = StartCli(device, args);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(Wait);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("myriagent-remote " + string.Join(' ', args) + " 沒有結束");
        }

        return new CliResult(process.ExitCode, await stdout, await stderr);
    }

    private static async Task<CliResult> WaitForCliAsync(LongRunDevice device, Func<CliResult, bool> condition, string because, params string[] args)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var result = await RunCliAsync(device, args);
            if (condition(result))
                return result;
            if (watch.Elapsed > Wait)
                throw new TimeoutException($"等不到「{because}」：最後一次 exit={result.ExitCode} stdout={result.Stdout} stderr={result.Stderr}");
            await Task.Delay(500);
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string because)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Wait)
                throw new TimeoutException($"等不到「{because}」");
            await Task.Delay(100);
        }
    }

    private sealed class ScriptedConfirmer : IRemoteExecConfirmer
    {
        private readonly ConcurrentQueue<RemoteExecConfirmation> asked = new();

        public RemoteExecConfirmResult Answer { get; set; } = RemoteExecConfirmResult.Denied;

        public IReadOnlyList<RemoteExecConfirmation> Asked => asked.ToList();

        public Task<RemoteExecConfirmResult> ConfirmAsync(RemoteExecConfirmation confirmation, TimeSpan timeout, CancellationToken cancellationToken)
        {
            asked.Enqueue(confirmation);
            return Task.FromResult(Answer);
        }
    }
}
