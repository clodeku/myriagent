using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.RemoteExec;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 遠端執行指令的被操作端（AgentHub.RemoteExec.cs；docs/sync-protocol.md 14.11）：閘門 5a～5i 照一般的遠端指令（要 Control），
/// 處理者另外要接上執行者、這台開了「允許遠端執行指令」、執行者的檢查通過，才交給執行者並回 done
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteExecTests
{
    private const string CommandId = "01J9QZ3K8M5R7T2V4W6X8Y0A1B";

    private sealed class FakeRunner : IRemoteExecRunner
    {
        public (string Code, string Message)? Problem { get; set; }

        public Recorder<(string CommandId, RemoteExecRequest Request, string Issuer)> Started { get; } = new();

        public int RunningCount { get; set; }

        public (string Code, string Message)? Check(RemoteExecRequest request) => Problem;

        public void Start(string commandId, RemoteExecRequest request, string issuerLabel) => Started.Add((commandId, request, issuerLabel));

        public void Abort(string commandId, bool orphaned)
        {
        }
    }

    private static RemoteViewContext Context() =>
        new(CommandId, RemoteSamples.DeviceId, null, null, 2, false, null, 2_000, false, RemoteNotifyRules.IssuerDisplay("device", "LAPTOP"));

    private static JObject Exec(string command = "Get-Date") => new()
    {
        ["cmd"] = RemoteCommandCatalog.RemoteExec,
        ["deviceId"] = RemoteSamples.DeviceId,
        ["shell"] = RemoteExecRules.PowerShell,
        ["command"] = command,
        ["timeoutSec"] = 45,
        ["label"] = "整理",
    };

    private static Task<RemoteOutcome> ExecuteAsync(HubHarness h, JObject command)
        => h.Ui.Invoke(() => h.Hub.ExecuteRemoteAsync(Context(), command, _ => { })).WaitAsync(TimeSpan.FromSeconds(10));

    private static void Allow(HubHarness h, bool exec = true, string level = SyncSettings.LevelControl, bool paused = false)
        => h.Ui.Invoke(() =>
        {
            h.Settings.Sync.RemoteControlLevel = level;
            h.Settings.Sync.AllowRemoteExec = exec;
            h.Settings.Sync.RemotePaused = paused;
        });

    private static void Rejected(RemoteOutcome outcome, string code, string step)
    {
        outcome.State.Should().Be(RemoteResultStates.Rejected, outcome.Message);
        outcome.Code.Should().Be(code, outcome.Message);
        outcome.Step.Should().Be(step, outcome.Message);
        outcome.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task 沒有接上執行者時回unsupported()
    {
        using var h = new HubHarness();
        Allow(h);
        Rejected(await ExecuteAsync(h, Exec()), RemoteResultCodes.Unsupported, RemoteGateSteps.Handler);
    }

    [Fact]
    public async Task 沒開允許遠端執行指令_或遠端操作不是Control_或暫停中_都不收()
    {
        using var h = new HubHarness();
        var runner = new FakeRunner();
        h.Ui.Invoke(() => h.Hub.AttachRemoteExec(runner));

        Allow(h, exec: false);
        var off = await ExecuteAsync(h, Exec());
        Rejected(off, RemoteResultCodes.Policy, RemoteGateSteps.Handler);
        off.Message.Should().Be(RemoteExecHubRules.NotAllowedMessage);

        Allow(h, level: SyncSettings.LevelView);
        Rejected(await ExecuteAsync(h, Exec()), RemoteResultCodes.Policy, RemoteGateSteps.Policy);

        Allow(h, paused: true);
        Rejected(await ExecuteAsync(h, Exec()), RemoteResultCodes.Policy, RemoteGateSteps.Policy);
        runner.Started.All.Should().BeEmpty();
    }

    [Fact]
    public async Task 執行者的檢查不過就拒絕_通過就交給它並回done()
    {
        using var h = new HubHarness();
        var runner = new FakeRunner { Problem = (RemoteResultCodes.Policy, "這台的 app 以系統管理員身分執行") };
        h.Ui.Invoke(() => h.Hub.AttachRemoteExec(runner));
        Allow(h);

        var refused = await ExecuteAsync(h, Exec());
        Rejected(refused, RemoteResultCodes.Policy, RemoteGateSteps.Handler);
        refused.Message.Should().Contain("系統管理員");
        runner.Started.All.Should().BeEmpty();

        runner.Problem = null;
        var outcome = await ExecuteAsync(h, Exec("dir"));
        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        outcome.Tier.Should().Be(2);
        var started = runner.Started.All.Should().ContainSingle().Subject;
        started.CommandId.Should().Be(CommandId);
        started.Request.Should().Be(new RemoteExecRequest(RemoteExecRules.PowerShell, "dir", null, 45, "整理"));
        started.Issuer.Should().Be("電腦「LAPTOP」");

        runner.RunningCount = 1;
        h.Ui.Invoke(() => h.Hub.RemoteExecRunning).Should().BeTrue("app 自我更新不在這時自動重啟");
    }

    [Fact]
    public void 對話CLI的PATH與給代理的說明()
    {
        RemoteExecHubRules.PrependPath(@"C:\app", null).Should().Be(@"C:\app");
        RemoteExecHubRules.PrependPath(@"C:\app", @"C:\Windows;C:\bin").Should().Be(@"C:\app;C:\Windows;C:\bin");
        RemoteExecHubRules.PrependPath(@"C:\app", @"C:\app\;C:\bin").Should().Be(@"C:\app\;C:\bin", "已經在第一個就不重複");
        RemoteExecHubRules.InstructionsHint.Should().StartWith("\n\n").And.Contain("myriagent-remote list").And.Contain("不要嘗試繞過");
        RemoteNotifyRules.Describe(RemoteCommandCatalog.RemoteExec, "電腦「LAPTOP」", "Get-Date").Should().Be("電腦「LAPTOP」 執行了主控台指令『Get-Date』");
    }
}
