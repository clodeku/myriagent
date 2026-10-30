using System.Diagnostics;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Tests.Cli;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// WP4.8 步 8（D1、D16、D17、D22）：中樞的遠端登出與 CLI 的安裝、更新回報真的結果（不再是退路的假 done）——
/// accountLogout 在帳戶自己的設定資料夾跑 CLI 的登出，結束碼 0 才是 done、失敗或逾時是 unknown，CLI 正在更新或帳戶正在登入是 busy（可以重送）；
/// cliInstall／cliUpdate 只收 Claude、Codex，放行時在背景開始（done＝已開始）；遠端動作的通知認得 accountLoginDevice 的目錄帳戶名稱。
/// 一律經 RemoteAccountRig：假 app-server／假 claude、假 CLI 管理員與安裝視窗、守門，不跑真的 CLI、不開安裝視窗或 winget
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteLogoutCliTests
{
    private const int Slow = RemoteAccountRig.Slow;

    private const string Logical = "la_3123456789ABCDEFGHJKMNPQRS";

    private const string Label = RemoteEntrySamples.IssuerLabel;

    /// <summary>假 app-server 的 logout 子指令在這個 CODEX_HOME 留下的標記</summary>
    private static string LogoutMarker(HubHarness h) => Path.Combine(h.Codex.ConfigDirectory!, "fake-logged-out");

    private static bool CodexLogoutRan(RemoteAccountRig rig) => rig.Fake.Calls.Contains("args=logout");

    /// <summary>不進同步的帳戶（Antigravity 的本機預設、Grok 在暫存資料夾）：遠端帳戶與 CLI 操作一律 policy</summary>
    private static void SeedLocalOnly(HubHarness h) => h.Ui.Invoke(() => h.Accounts.Seed(
    [
        new AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity", Color = "#4285F4" },
        new AgentAccount { Id = "acc-grok", Provider = AgentProvider.Grok, Label = "Grok", Color = "#9CA3AF", ConfigDirectory = h.Temp.Combine("accounts", "acc-grok", "grok") },
    ], AccountStatus.Ready));

    private static void ShouldBeUnknown(RemoteOutcome outcome)
    {
        outcome.State.Should().Be(RemoteResultStates.Unknown, outcome.Message);
        outcome.Code.Should().BeNull();
        outcome.Step.Should().Be(RemoteGateSteps.Handler);
        outcome.Message.Should().Be(RemoteAccountRules.LogoutFailedMessage, "可能已經登出：瀏覽器不得自動重送，請看那一格的狀態");
    }

    private static Task WaitNotLoggedInAsync(RemoteAccountRig rig, string accountId)
        => rig.H.WaitUntilAsync(() => rig.H.Accounts.GetState(accountId).Status == AccountStatus.NotLoggedIn, "登出之後重新查詢：未登入", Slow);

    // ───────────────────────────── accountLogout ─────────────────────────────

    [Theory]
    [InlineData("結束碼0")]
    [InlineData("結束碼1")]
    [InlineData("逾時")]
    [InlineData("CLI正在更新")]
    [InlineData("正在登入")]
    [InlineData("沒安裝")]
    [InlineData("本來就未登入")]
    [InlineData("不進同步的種類")]
    public async Task accountLogout_表(string kind)
    {
        var codex = kind switch
        {
            "結束碼1" => new FakeCodexAppServer("ok", logoutExitCode: 1),
            "逾時" => new FakeCodexAppServer("ok", logoutDelayMs: 5000),
            _ => new FakeCodexAppServer("ok"),
        };
        using var rig = new RemoteAccountRig(codex, status: kind is "本來就未登入" or "正在登入" ? AccountStatus.NotLoggedIn : AccountStatus.Ready);
        var h = rig.H;
        switch (kind)
        {
            case "逾時":
                h.Ui.Invoke(() => h.Accounts.RemoteLogoutDeadline = TimeSpan.FromSeconds(1));
                break;
            case "CLI正在更新":
                h.Ui.Invoke(() => h.Cli.Update(AgentProvider.Codex, s => s with { UpdatePhase = CliUpdatePhase.Installing }));
                break;
            case "正在登入":
                // 這台本機開一個裝置碼登入（Codex），等驗證碼出現
                h.Command(h.Main, "accountLogin", new { accountId = h.Codex.Id, method = "deviceCode" });
                await h.WaitUntilAsync(() => h.Main.Messages("accountLogin").Any(m => m["deviceCode"] is JObject), "本機的裝置碼登入已開始", Slow);
                break;
            case "沒安裝":
                rig.Launchers = provider => provider == AgentProvider.Codex ? null : rig.DefaultLauncher(provider);
                break;
            case "不進同步的種類":
                SeedLocalOnly(h);
                break;
        }

        var watch = Stopwatch.StartNew();
        var outcome = await rig.RunAsync(RemoteAccountRig.Logout(kind == "不進同步的種類" ? "local-antigravity" : h.Codex.Id));

        switch (kind)
        {
            case "結束碼0":
            {
                outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
                outcome.Tier.Should().Be(2);
                var calls = rig.Fake.Calls.ToList();
                var logoutAt = calls.IndexOf("args=logout");
                logoutAt.Should().BeGreaterThanOrEqualTo(0, "等 CLI 結束才回 done");
                Path.GetFullPath(calls[logoutAt + 1]["codexHome=".Length..]).Should().BeEquivalentTo(Path.GetFullPath(h.Codex.ConfigDirectory!), "在這個帳戶的資料夾登出");
                File.Exists(LogoutMarker(h)).Should().BeTrue();
                await WaitNotLoggedInAsync(rig, h.Codex.Id);
                break;
            }
            case "結束碼1":
                ShouldBeUnknown(outcome);
                CodexLogoutRan(rig).Should().BeTrue();
                await WaitNotLoggedInAsync(rig, h.Codex.Id);
                break;
            case "逾時":
                ShouldBeUnknown(outcome);
                watch.Elapsed.Should().BeLessThan(AgentHub.RemoteOutputWindow, "登出的期限（測試 1 秒）一到就回報，不等到閘門的 30 秒");
                CodexLogoutRan(rig).Should().BeTrue();
                await WaitNotLoggedInAsync(rig, h.Codex.Id);
                break;
            case "CLI正在更新":
                RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Busy, RemoteGateSteps.Handler);
                outcome.Message.Should().Contain("正在更新");
                await h.Ui.DrainAsync();
                rig.Fake.Started.Should().BeFalse("busy＝什麼都沒執行（「所有電腦登出」由伺服器重送）");
                break;
            case "正在登入":
                RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Busy, RemoteGateSteps.Handler);
                outcome.Message.Should().Contain("正在那台登入");
                await h.Ui.DrainAsync();
                CodexLogoutRan(rig).Should().BeFalse("busy＝什麼都沒執行");
                await rig.CancelAndWaitAsync(h.Codex.Id);
                break;
            case "沒安裝":
                RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
                outcome.Message.Should().Contain("沒有安裝");
                await h.Ui.DrainAsync();
                rig.Fake.Started.Should().BeFalse("退路會回假的 done；處理者說明沒安裝");
                break;
            case "本來就未登入":
                outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
                await h.Ui.DrainAsync();
                rig.Fake.Started.Should().BeFalse("本來就未登入：直接 done，不跑 CLI");
                break;
            case "不進同步的種類":
            {
                // Antigravity 沒有登出子指令（設計 2026-10-22-grok-antigravity-sync 的 D4）；Grok 的遠端登出照 Codex（RemoteAccountRulesTests）
                RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Policy, RemoteGateSteps.Handler);
                outcome.Message.Should().Be(RemoteAccountRules.AntigravityLogoutMessage);
                await h.Ui.DrainAsync();
                rig.Fake.Started.Should().BeFalse();
                rig.Claude.Started.Should().BeFalse();
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    [Fact]
    public async Task accountLogout_Claude_在那個帳戶的資料夾登出_done()
    {
        using var rig = new RemoteAccountRig(status: AccountStatus.Ready);
        var h = rig.H;
        var folder = h.Claude2.ConfigDirectory!;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".credentials.json"), "{}");
        File.WriteAllText(Path.Combine(folder, ".claude.json"), "{}");

        var outcome = await rig.RunAsync(RemoteAccountRig.Logout(h.Claude2.Id));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var logout = rig.Claude.Calls.Single(c => c.EndsWith("#auth logout", StringComparison.Ordinal));
        Path.GetFullPath(logout[..logout.IndexOf('#', StringComparison.Ordinal)]).Should().BeEquivalentTo(Path.GetFullPath(folder), "CLAUDE_CONFIG_DIR 是這個帳戶的資料夾");
        File.Exists(Path.Combine(folder, ".credentials.json")).Should().BeFalse();
        File.Exists(Path.Combine(folder, ".claude.json")).Should().BeFalse();
        rig.Claude.Calls.Should().NotContain("refused-not-temp");
        await h.Ui.DrainAsync();
    }

    [Fact]
    public async Task accountLogout_busy之後同一個cid再送_會執行()
    {
        // durable 的「所有電腦登出」收到 busy 會由伺服器以同一個 cid 重送（WP4.4 D9：busy 刪掉 seen）：busy 不能在中樞留下任何擋住重送的狀態
        using var rig = new RemoteAccountRig(status: AccountStatus.Ready);
        var h = rig.H;
        var cid = RemoteEntrySamples.NewCid();
        h.Ui.Invoke(() => h.Cli.Update(AgentProvider.Codex, s => s with { UpdatePhase = CliUpdatePhase.Installing }));

        var first = await rig.RunAsync(RemoteAccountRig.Logout(h.Codex.Id), cid: cid);

        RemoteEntrySamples.ShouldBeRejected(first, RemoteResultCodes.Busy, RemoteGateSteps.Handler);
        rig.Fake.Started.Should().BeFalse();

        h.Ui.Invoke(() => h.Cli.Update(AgentProvider.Codex, s => s with { UpdatePhase = CliUpdatePhase.None }));
        var second = await rig.RunAsync(RemoteAccountRig.Logout(h.Codex.Id), cid: cid);

        second.State.Should().Be(RemoteResultStates.Done, second.Message);
        CodexLogoutRan(rig).Should().BeTrue();
        File.Exists(LogoutMarker(h)).Should().BeTrue();
        await WaitNotLoggedInAsync(rig, h.Codex.Id);
    }

    [Fact]
    public async Task accountLogout_邏輯id或不存在_invalid_在5c_什麼都沒做()
    {
        // 從 AgentHubRemoteEntryTests 移來（WP4.4 的 5c 斷言照舊）：網頁的邏輯 id 由伺服器換成這台的本機 id，換不出來或不存在就在閘門擋下
        using var rig = new RemoteAccountRig(status: AccountStatus.Ready);
        var h = rig.H;
        var accounts = h.OnUi(() => h.Accounts.Accounts.Select(a => a.Id).ToList());

        foreach (var accountId in new[] { RemoteSamples.LogicalAccountId, "acc-missing", "ACC-CODEX" })
        {
            var outcome = await rig.RunAsync(RemoteAccountRig.Logout(accountId));
            RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Invalid, RemoteGateSteps.Conversation);
            outcome.Tier.Should().Be(2, "在 5d 之前停下：層級是信封宣告的");
        }

        await h.Ui.DrainAsync();
        h.OnUi(() => h.Accounts.Accounts.Select(a => a.Id).ToList()).Should().Equal(accounts);
        rig.Fake.Started.Should().BeFalse();
        rig.Claude.Started.Should().BeFalse();
    }

    // ───────────────────────────── cliInstall／cliUpdate ─────────────────────────────

    [Theory]
    [InlineData("cliInstall", "Claude", "沒安裝", RemoteResultStates.Done)]
    [InlineData("cliInstall", "Codex", "沒安裝", RemoteResultStates.Done)]
    [InlineData("cliInstall", "Claude", "損壞", RemoteResultStates.Done)]
    [InlineData("cliInstall", "Claude", "已安裝", RemoteResultCodes.Conflict)]
    [InlineData("cliUpdate", "Claude", "已安裝", RemoteResultStates.Done)]
    [InlineData("cliUpdate", "Codex", "沒安裝", RemoteResultCodes.Conflict)]
    [InlineData("cliUpdate", "Claude", "正在動作", RemoteResultCodes.Conflict)]
    [InlineData("cliUpdate", "Antigravity", "沒安裝", RemoteResultCodes.Conflict)]
    public async Task cliInstall_cliUpdate_表(string cmd, string providerName, string setup, string expected)
    {
        var provider = Enum.Parse<AgentProvider>(providerName);
        using var temp = new TempDirectory("remote-cli");
        using var rig = new RemoteAccountRig();
        var h = rig.H;
        if (setup is "已安裝" or "損壞" or "正在動作")
        {
            // 暫存資料夾裡的 Claude 原生安裝（守門只交出暫存資料夾裡的安裝）；--version 是假的，回報 claude.exe 的版本資訊
            var claude = new ClaudeLayout(temp);
            rig.Installations = p => p == AgentProvider.Claude ? claude.Installation : null;
            rig.Commands.Version = _ => $"{ClaudeLayout.OldVersion} (Claude Code)";
            await h.Cli.CheckAsync(AgentProvider.Claude);
            h.Cli.Get(AgentProvider.Claude).Installed.Should().BeTrue("前提：已安裝");
            if (setup == "損壞")
                h.Ui.Invoke(() => h.Cli.Update(AgentProvider.Claude, s => s with { InstallBrokenReason = "測試：安裝不完整" }));
            if (setup == "正在動作")
                h.Ui.Invoke(() => h.Cli.Update(AgentProvider.Claude, s => s with { Busy = true, Progress = "檢查中…" }));
        }

        var callsBefore = rig.Commands.Calls.Count;
        var outcome = await rig.RunAsync(RemoteAccountRig.CliCommand(cmd, provider));

        if (expected == RemoteResultStates.Done)
        {
            outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
            outcome.Tier.Should().Be(2);
            // done＝已經開始（D17）：等背景的動作跑完
            switch ((cmd, provider))
            {
                case ("cliInstall", AgentProvider.Claude):
                    await h.WaitUntilAsync(() => rig.InstallersStarted == 1, "Claude 的假安裝視窗啟動了一次", Slow);
                    break;
                case ("cliInstall", AgentProvider.Codex):
                    await h.WaitUntilAsync(() => rig.Commands.Count("install") >= 1, "npm 安裝（假的）", Slow);
                    rig.InstallersStarted.Should().Be(0);
                    break;
                case ("cliUpdate", AgentProvider.Claude):
                    await h.WaitUntilAsync(() => rig.Commands.Calls.All.Contains("update"), "claude update（假的）", Slow);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(cmd), (cmd, provider), null);
            }

            await h.WaitUntilAsync(() => !h.Cli.Get(provider).Busy, "背景的安裝或更新結束", Slow);
            if (cmd == "cliUpdate")
            {
                rig.InstallersStarted.Should().Be(0, "更新不落進安裝");
                rig.Commands.Count("install").Should().Be(0);
            }
        }
        else
        {
            RemoteEntrySamples.ShouldBeRejected(outcome, expected, RemoteGateSteps.Handler);
            await h.Ui.DrainAsync();
            rig.InstallersStarted.Should().Be(0, "被拒＝什麼都沒開始");
            rig.Commands.Calls.All.Skip(callsBefore).Should().BeEmpty("沒有跑任何 CLI 子指令（安裝、更新、npm）");
            if (setup == "正在動作")
                h.Ui.Invoke(() => h.Cli.Update(AgentProvider.Claude, s => s with { Busy = false, Progress = null }));
        }
    }

    // ───────────────────────────── 通知與處理者 ─────────────────────────────

    [Fact]
    public async Task 通知_accountLoginDevice帶目錄的帳戶名稱()
    {
        using var rig = new RemoteAccountRig();
        var h = rig.H;
        rig.Bridge.ByLogical[Logical] = RemoteLoginLookup.Found(new RemoteLoginTarget(Logical, AgentProvider.Codex, "目錄裡的 Codex", h.Codex.Id, null,
            RemoteLoginExpectation.None));
        var notified = h.Host.Notifications.Count;

        var outcome = await rig.RunAsync(RemoteAccountRig.LoginDevice(Logical, "deviceCode"));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        h.Host.Notifications.All.Skip(notified).Should().Equal(
            (RemoteNotifyRules.ActionTitle, $"{Label} 開始在這台登入目錄的帳戶『目錄裡的 Codex』", (string?)null));
        h.Host.Notifications.All.Should().NotContain(n => n.Message.Contains(FakeCodexAppServer.UserCode, StringComparison.Ordinal), "通知不放驗證碼");
        await rig.CancelAndWaitAsync(h.Codex.Id);
    }

    [Fact]
    public async Task 通知_accountLogout照舊()
    {
        using var rig = new RemoteAccountRig(status: AccountStatus.Ready);
        var h = rig.H;
        var notified = h.Host.Notifications.Count;

        var outcome = await rig.RunAsync(RemoteAccountRig.Logout(h.Codex.Id));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        h.Host.Notifications.All.Skip(notified).Should().Equal((RemoteNotifyRules.ActionTitle, $"{Label} 登出了帳戶『Codex 帳戶』", (string?)null));
        await WaitNotLoggedInAsync(rig, h.Codex.Id);
    }

    [Fact]
    public async Task 處理者_未授權_登出與CLI指令都invalid_什麼都沒做()
    {
        using var rig = new RemoteAccountRig(status: AccountStatus.Ready);
        var h = rig.H;
        var handler = h.OnUi(() => h.Hub.CreateAccountRemoteHandlerForTest());
        var view = new FakeView("remote-test");

        foreach (var (cmd, command) in new[]
                 {
                     ("accountLogout", RemoteAccountRig.Logout(h.Codex.Id)),
                     ("cliInstall", RemoteAccountRig.CliCommand("cliInstall", AgentProvider.Codex)),
                     ("cliUpdate", RemoteAccountRig.CliCommand("cliUpdate", AgentProvider.Claude)),
                 })
        {
            var context = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, null, null, 2, false, null, 2_000, false, RemoteEntrySamples.IssuerLabel);
            h.OnUi(() => handler.TryHandle(context, view, cmd, command)).Should().BeTrue(cmd);
            var outcome = await context.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Invalid, RemoteGateSteps.Handler);
        }

        await h.Ui.DrainAsync();
        rig.Fake.Started.Should().BeFalse();
        rig.InstallersStarted.Should().Be(0);
        rig.Commands.Calls.All.Should().NotContain(c => c.Contains("install", StringComparison.Ordinal) || c == "update");
    }
}
