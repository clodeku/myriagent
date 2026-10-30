using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.Cli;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>中樞配合 CLI 安裝／更新的部分：更新屏障（排空與接續，詳細情境見 AgentHubRestartTests）、npm 換檔期間送出排隊、CLI 卡的檢查中與查最新版失敗</summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubCliTests
{
    private static CliInstallation NpmCodex()
        => new(AgentProvider.Codex, new AgentLauncher(@"C:\npm\node.exe", new[] { @"C:\npm\node_modules\@openai\codex\bin\codex.js" }), CliInstallKind.Npm, @"C:\npm\codex.cmd");

    [Fact]
    public async Task 更新屏障_閒置的關掉_執行中的到安全點停下才回報可以更新_其他代理的不動()
    {
        using var h = new HubHarness();
        var (_, idle) = h.StartConversation(h.Main, h.Codex, "問完了");
        h.CompleteTurn(idle);
        var (runningId, running) = h.StartConversation(h.Main, h.Codex, "還在跑");
        running.OnRestart = request => new AgentRestartResult(request.Settings == null ? AgentRestartOutcome.Stopped : AgentRestartOutcome.Restarted);
        var (_, claude) = h.StartConversation(h.Main, h.Claude, "Claude 閒置");
        h.CompleteTurn(claude);

        var participant = h.Cli.UpdateParticipant;
        participant.Should().BeSameAs(h.Hub, "中樞建立時要接上更新屏障，CLI 手動更新才不會被 app 自己的程序擋下");
        // CliManager 在背景執行緒呼叫（測試執行緒不是 UI 執行緒：回來前已排進 UI 執行緒）
        var prepare = participant!.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None);
        await h.Ui.DrainAsync();

        idle.Closes.Should().Be(1, "閒置的對話程序立刻關（下次送出自動續接）");
        prepare.IsCompleted.Should().BeFalse("執行中的還沒到安全點：不回報可以更新");
        running.Restarts.Count.Should().Be(0);
        claude.Closes.Should().Be(0, "只排空要更新的那種代理");

        // 到了安全點：停下（Settings=null）、標成等待 CLI 更新，全部停下才回 Ready
        running.SafePoint = true;
        h.Raise(running, new AgentSafePointChangedEvent(false, false, false));
        var preparation = await prepare.WaitAsync(TimeSpan.FromSeconds(5));

        running.Restarts.All.Should().ContainSingle().Which.Settings.Should().BeNull();
        preparation.Should().Be(new CliUpdatePreparation(Ready: true, ClosedIdle: 1, Held: 1, Busy: 0));
        h.IsRunning(runningId).Should().BeTrue("等待 CLI 更新不是「已停止」");

        // 屏障結束的通知不能丟例外（CliManager 一定會在 PrepareForUpdateAsync 之後呼叫）；身分不明＝停下的那一輪以提示收尾
        participant!.Invoking(p => p.UpdateFinished(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Unchanged, null)))
            .Should().NotThrow();
        await h.Ui.DrainAsync();
        h.IsRunning(runningId).Should().BeFalse();
        running.Restarts.Count.Should().Be(1, "沒有可用的身分就不重開");
    }

    [Fact]
    public async Task 手動更新npm版_真的CliManager_屏障停下的對話以新版重開接續_不是沒有可用的版本()
    {
        // 整合員的第 1 條：UpdateFinished 的 Identity 一直是 null 時，在安全點被屏障停下的對話會以「更新後沒有可用的版本」收尾
        using var temp = new TempDirectory("hub-cli-update");
        var codex = new CodexNpmLayout(temp, "0.160.1");
        var commands = FakeCliCommands.For(codex);
        commands.Npm = (_, _) =>
        {
            codex.Retire();
            codex.WritePackage("0.162.0", "0.162.0", extraFiles: 3);
            codex.RemoveTrash();
            codex.RewriteShims();
            return new CliRunResult(0, "changed 2 packages", string.Empty, false);
        };
        using var registry = new HttpClient(new FakeRegistry { Latest = "0.162.0" });
        using var h = new HubHarness(createCli: _ => CliManagerTestKit.Create(registry, p => p == AgentProvider.Codex ? codex.Installation : null, commands,
            new FakeProcesses(), AgentProvider.Codex, background: false));
        await Task.Run(() => h.Cli.CheckAsync(AgentProvider.Codex));   // 啟動時第一次讀到的身分（Old: null）
        await h.Ui.DrainAsync();
        var old = h.Cli.GetIdentity(AgentProvider.Codex)!;
        old.Version.Should().Be("0.160.1");

        var (id, s) = h.StartConversation(h.Main, h.Codex, "做事");
        h.ProcessStarted(s, "c1", old);
        s.Sent.All[0].Settings.Cli.Should().Be(old);
        s.SafePoint = true;   // 執行中、在安全點：屏障停下它（Stopped），更新後以新版接續
        s.SucceedRestarts();

        await Task.Run(() => h.Cli.UpdateAsync(AgentProvider.Codex)).WaitAsync(TimeSpan.FromSeconds(30));
        await h.WaitUntilAsync(() => s.Restarts.Count == 2, "更新結束後以新版重開停下的那一輪");

        var fresh = h.Cli.GetIdentity(AgentProvider.Codex)!;
        fresh.Version.Should().Be("0.162.0");
        s.Restarts.All[0].Settings.Should().BeNull("屏障先停下（Stopped），等更新完成");
        var resume = s.Restarts.All[1];
        resume.Settings!.Cli.Should().Be(fresh, "UpdateFinished 帶穩定後的新身分");
        resume.AutoContinue.Should().BeTrue();
        resume.ToVersion.Should().Be("0.162.0");
        h.IsRunning(id).Should().BeTrue("以新版接續的一輪");
        var notices = h.Items(id).Where(i => i.NoticeKind == AgentNoticeKind.CliUpdate).Select(i => i.Text).ToList();
        notices.Should().Contain(t => t.Contains("0.162.0") && t.Contains("自動接續"));
        h.Items(id).Should().NotContain(i => i.Text != null && i.Text.Contains("沒有可用的版本"));
        h.Queue(id).Should().BeEmpty();
        h.Cli.Get(AgentProvider.Codex).UpdatePhase.Should().Be(CliUpdatePhase.None);
    }

    [Fact]
    public async Task 宣告換版_狀態先更新_再觸發VersionChanged()
    {
        using var h = new HubHarness();
        var old = CliIdentities.Claude("2.1.295", fileId: 1);
        var fresh = CliIdentities.Claude("2.1.297", fileId: 2);
        h.Cli.PublishVersionChange(new CliVersionChange(AgentProvider.Claude, null, old, CliChangeOrigin.External, Stable: true, InstallBroken: false, null));

        var seen = new TaskCompletionSource<(CliVersionChange Change, CliBinaryIdentity? IdentityAtEvent)>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Cli.VersionChanged += change => seen.TrySetResult((change, h.Cli.GetIdentity(change.Provider)));
        var change = new CliVersionChange(AgentProvider.Claude, old, fresh, CliChangeOrigin.External, Stable: true, InstallBroken: false, "你在終端機更新了 Claude Code");
        await Task.Run(() => h.Cli.PublishVersionChange(change));   // 偵測流程在背景執行緒宣告

        var (received, identityAtEvent) = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        received.Should().BeSameAs(change);
        identityAtEvent.Should().Be(fresh, "VersionChanged 觸發前狀態已更新：訂閱端讀得到新身分");
        h.Cli.Get(AgentProvider.Claude).LastChange.Should().BeSameAs(change);
        h.Cli.Get(AgentProvider.Claude).Version.Should().Be("2.1.297");
    }

    [Fact]
    public async Task Npm更新換檔期間_送出排進佇列並提示_換完檔自動送出()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "第一則");
        h.CompleteTurn(s);
        var sentBefore = s.Sent.Count;

        h.Cli.Update(AgentProvider.Codex, st => st with { Installation = NpmCodex(), Busy = true, Progress = "更新中…" });
        var mark = h.Main.Mark();
        h.Send(h.Main, id, "更新時送出");

        h.Main.Messages("sendRejected", mark, id).Should().BeEmpty("不退回：排進佇列，更新完成後自動送出");
        h.Queue(id).Should().Equal("更新時送出");
        h.Main.Messages("toast", mark).Should().Contain(m => m.Value<string>("text")!.Contains("Codex CLI 正在更新"));
        s.Sent.Count.Should().Be(sentBefore, "npm 換檔途中啟動 CLI 一定失敗");

        h.Cli.Update(AgentProvider.Codex, st => st with { Busy = false, Progress = null });
        await h.WaitUntilAsync(() => s.Sent.Count == sentBefore + 1, "換完檔自動送出排隊的訊息");
        s.Sent.All[^1].Input.Text.Should().Be("更新時送出");
        h.Queue(id).Should().BeEmpty();
    }

    [Fact]
    public async Task CLI卡_送出檢查中與查最新版失敗的原因()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "ready", new { mode = "main" });
        var mark = h.Main.Mark();

        h.Cli.Update(AgentProvider.Codex, st => st with
        {
            Installation = NpmCodex(),
            Version = null,
            VersionCheckedFor = null,
            LatestError = "無法查詢最新版本：連線逾時",
            CheckedAt = DateTime.Now,
        });

        var cli = await h.Main.WaitForAsync("cli", m => m["cli"]?["Codex"]?.Value<string>("latestError") != null, mark);
        var codex = (JObject)cli["cli"]!["Codex"]!;
        codex.Value<bool>("checking").Should().BeTrue("找到執行檔但還沒查過版本");
        codex.Value<bool>("installed").Should().BeFalse();
        codex.Value<string>("latestError").Should().Be("無法查詢最新版本：連線逾時");
        codex["checkedAt"]!.Type.Should().Be(JTokenType.Integer, "查最新版失敗也有檢查完成的時間：網頁的「立即檢查」靠它判斷完成");
        codex["lastCheckedAt"]!.Type.Should().Be(JTokenType.Null, "沒查到過最新版就沒有「上次檢查」");
    }
}
