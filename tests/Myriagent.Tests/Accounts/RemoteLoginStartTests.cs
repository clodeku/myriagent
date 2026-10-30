using System.Diagnostics;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Remote;
using Myriagent.Tests.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Accounts;

/// <summary>
/// WP4.8 步 3：遠端登入的里程碑與 25 秒期限（AccountManager.StartRemoteLoginAsync）、新增帳戶不自動登入、前置條件的事實。
/// 一律以假 app-server（FakeCodexAppServer）與假 claude（FakeClaudeLogin）為 ResolveLauncher，開瀏覽器換成計數器：
/// 不跑真的 claude／codex、不開瀏覽器、不連網；設定資料夾都在暫存資料夾（HubHarness 的 AppPaths）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class RemoteLoginStartTests
{
    /// <summary>帳戶相關的等待一律 60 秒（D30）</summary>
    private const int Slow = RemoteLoginRig.Slow;

    private const string LogicalId = "la_0123456789ABCDEFGHJKMNPQRS";

    private static LoginRequest CodexRemote() => new(LoginMethod.DeviceCode, null, new RemoteLoginExpectation("codex:" + FakeCodexAppServer.Email, FakeCodexAppServer.Email, null));

    [Fact]
    public async Task Codex隔離_拿到驗證碼回CodeShown_帶DeviceCode_CODEX_HOME是隔離資料夾_沒開瀏覽器()
    {
        using var rig = new RemoteLoginRig();
        var start = await rig.StartAsync(rig.Codex, CodexRemote());

        start.Milestone.Should().Be(RemoteLoginMilestone.CodeShown);
        start.Code.Should().BeNull();
        start.DeviceCode.Should().NotBeNull();
        start.DeviceCode!.UserCode.Should().Be(FakeCodexAppServer.UserCode);
        start.DeviceCode.VerificationUrl.AbsoluteUri.Should().Be(FakeCodexAppServer.VerificationUrl);
        rig.State(rig.Codex).LoginBusy.Should().BeTrue("拿到驗證碼就回報，登入本身還在等核准");
        rig.H.OnUi(() => rig.H.Accounts.HasRemoteLoginRun(rig.Codex)).Should().BeTrue();

        var homes = rig.Fake.CodexHomes;
        homes.Should().NotBeEmpty();
        var isolated = Path.GetFullPath(rig.H.Codex.ConfigDirectory!);
        var userDefault = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        foreach (var home in homes)
        {
            home.Should().NotBeNullOrWhiteSpace("app-server 一定帶這個帳戶的 CODEX_HOME");
            Path.GetFullPath(home).Should().BeEquivalentTo(isolated);
            Path.GetFullPath(home).Should().NotBeEquivalentTo(userDefault);
        }
        rig.Opened.Should().Be(0, "裝置碼登入不開瀏覽器");

        await rig.CancelAndWaitAsync(rig.Codex);
    }

    [Fact]
    public async Task slow_start_期限2秒_回Failed_conflict_送了cancel或程序已結束_LoginBusy最後變false_沒有驗證碼()
    {
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("ok", startDelayMs: 5000));
        rig.H.Ui.Invoke(() => rig.H.Accounts.RemoteStartDeadline = TimeSpan.FromSeconds(2));
        var watch = Stopwatch.StartNew();
        var start = await rig.StartAsync(rig.Codex, CodexRemote());

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "期限一到就回報");
        start.Milestone.Should().Be(RemoteLoginMilestone.Failed);
        start.Code.Should().Be(RemoteResultCodes.Conflict);
        start.Message.Should().Be(RemoteAccountRules.StartTimeoutMessage(AgentProvider.Codex));
        start.DeviceCode.Should().BeNull();

        // 期限到了就取消這次登入：假 app-server 5 秒後才回驗證碼，取消之後不會顯示
        await rig.WaitLoginEndedAsync(rig.Codex);
        await rig.H.WaitUntilAsync(() => rig.Fake.Calls.Any(c => c.StartsWith("account/login/cancel", StringComparison.Ordinal)) || rig.Fake.AllExited(),
            "送了 account/login/cancel 或登入的 app-server 已結束", Slow);
        rig.State(rig.Codex).DeviceCode.Should().BeNull();
        rig.H.Main.Messages("accountLogin").Should().NotContain(m => m["deviceCode"] is JObject, "取消之後沒有顯示驗證碼");
        rig.H.OnUi(() => rig.H.Accounts.HasRemoteLoginRun(rig.Codex)).Should().BeFalse();
    }

    [Fact]
    public async Task unsupported_Failed_conflict_訊息是請更新Codex()
    {
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("unsupported"));
        var start = await rig.StartAsync(rig.Codex, CodexRemote());

        start.Milestone.Should().Be(RemoteLoginMilestone.Failed);
        start.Code.Should().Be(RemoteResultCodes.Conflict);
        start.Message.Should().Contain("請更新 Codex");
        start.DeviceCode.Should().BeNull();
        await rig.WaitLoginEndedAsync(rig.Codex);
    }

    [Fact]
    public async Task evil_host_Failed_送了cancel()
    {
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("evil_host"));
        var start = await rig.StartAsync(rig.Codex, CodexRemote());

        start.Milestone.Should().Be(RemoteLoginMilestone.Failed);
        start.Code.Should().Be(RemoteResultCodes.Conflict);
        start.Message.Should().Be("Codex 回傳的登入資訊不合格，已取消");
        start.DeviceCode.Should().BeNull();
        rig.Fake.Calls.Should().Contain("account/login/cancel loginId=" + FakeCodexAppServer.LoginId);
        await rig.WaitLoginEndedAsync(rig.Codex);
    }

    [Fact]
    public async Task missing_fields_Failed()
    {
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("missing_fields"));
        var start = await rig.StartAsync(rig.Codex, CodexRemote());

        start.Milestone.Should().Be(RemoteLoginMilestone.Failed);
        start.Code.Should().Be(RemoteResultCodes.Conflict);
        start.DeviceCode.Should().BeNull();
        await rig.WaitLoginEndedAsync(rig.Codex);
    }

    [Fact]
    public async Task complete_fail_仍回CodeShown且DeviceCode是那一組()
    {
        // complete_fail：驗證碼一出現就收到失敗的完成通知，狀態裡的驗證碼馬上被清掉；里程碑帶的是那一組（輸出不從狀態讀）
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("complete_fail"));
        var start = await rig.StartAsync(rig.Codex, CodexRemote());

        start.Milestone.Should().Be(RemoteLoginMilestone.CodeShown);
        start.DeviceCode.Should().NotBeNull();
        start.DeviceCode!.UserCode.Should().Be(FakeCodexAppServer.UserCode);
        start.DeviceCode.LoginId.Should().Be(FakeCodexAppServer.LoginId);
        await rig.WaitLoginEndedAsync(rig.Codex);
        rig.State(rig.Codex).DeviceCode.Should().BeNull("登入結束就清掉");
    }

    [Theory]
    [InlineData("CLI更新中")]
    [InlineData("沒有launcher")]
    [InlineData("已在登入中")]
    [InlineData("帳戶不存在")]
    public async Task 提早返回都在3秒內回Failed(string kind)
    {
        using var rig = new RemoteLoginRig();
        // 期限遠大於 3 秒：只靠期限才結束的話這個測試會失敗
        rig.H.Ui.Invoke(() => rig.H.Accounts.RemoteStartDeadline = TimeSpan.FromSeconds(20));
        var id = rig.Codex;
        string? expected = null;
        switch (kind)
        {
            case "CLI更新中":
                rig.H.Ui.Invoke(() => rig.H.Cli.Update(AgentProvider.Codex, s => s with { UpdatePhase = CliUpdatePhase.Installing }));
                expected = "正在更新";
                break;
            case "沒有launcher":
                rig.H.Ui.Invoke(() => rig.H.Accounts.ResolveLauncher = _ => null);
                expected = "安裝";
                break;
            case "已在登入中":
                rig.H.Ui.Invoke(() => { _ = rig.H.Accounts.LoginAsync(rig.Codex, new LoginRequest(LoginMethod.DeviceCode)); });
                await rig.H.WaitUntilAsync(() => rig.H.Accounts.GetState(rig.Codex).DeviceCode != null, "本機的裝置碼登入顯示驗證碼", Slow);
                expected = RemoteAccountRules.LoginNotStartedMessage;
                break;
            case "帳戶不存在":
                id = "acc-missing";
                expected = RemoteAccountRules.LoginNotStartedMessage;
                break;
        }

        var start = await rig.StartAsync(id, CodexRemote(), timeoutMs: 3000);

        start.Milestone.Should().Be(RemoteLoginMilestone.Failed);
        start.Code.Should().Be(RemoteResultCodes.Conflict);
        start.Message.Should().Contain(expected!);
        start.Message.Should().NotContain("@").And.NotContain("http");
        start.DeviceCode.Should().BeNull();
        rig.H.OnUi(() => rig.H.Accounts.HasRemoteLoginRun(rig.Codex)).Should().BeFalse("遠端這次沒有留下掛著里程碑的登入");
        if (kind == "已在登入中")
        {
            rig.State(rig.Codex).DeviceCode.Should().NotBeNull("本機那一次照常進行");
            await rig.CancelAndWaitAsync(rig.Codex);
        }
        else
        {
            rig.State(rig.Codex).LoginBusy.Should().BeFalse();
            rig.H.OnUi(() => rig.H.Accounts.HasActiveLogin).Should().BeFalse();
            rig.Fake.Started.Should().BeFalse("提早返回不起 app-server");
        }
    }

    [Fact]
    public async Task 另一個本機登入的里程碑不會送到遠端這次()
    {
        using var rig = new RemoteLoginRig();
        rig.H.Ui.Invoke(() => { _ = rig.H.Accounts.LoginAsync(rig.Codex, new LoginRequest(LoginMethod.DeviceCode)); });
        await rig.H.WaitUntilAsync(() => rig.H.Accounts.GetState(rig.Codex).DeviceCode != null, "本機的裝置碼登入顯示驗證碼", Slow);

        var start = await rig.StartAsync(rig.Codex, CodexRemote(), timeoutMs: 10_000);

        start.Milestone.Should().Be(RemoteLoginMilestone.Failed, "本機那一次的驗證碼不是這次遠端登入的");
        start.DeviceCode.Should().BeNull();
        rig.H.OnUi(() => rig.H.Accounts.HasRemoteLoginRun(rig.Codex)).Should().BeFalse();
        await rig.CancelAndWaitAsync(rig.Codex);
    }

    [Fact]
    public async Task Claude_主控台啟動回ConsoleOpened_帶預期信箱()
    {
        using var rig = new RemoteLoginRig();
        var claude = rig.H.Claude2;
        var start = await rig.StartAsync(claude.Id, new LoginRequest(LoginMethod.Default, "expected@example.test",
            new RemoteLoginExpectation("claude:expected@example.test", "expected@example.test", null)));

        start.Milestone.Should().Be(RemoteLoginMilestone.ConsoleOpened);
        start.Code.Should().BeNull();
        start.DeviceCode.Should().BeNull();
        await rig.Claude.WaitForAsync(calls => calls.Any(c => c.Contains("#auth login", StringComparison.Ordinal)), Slow);
        var login = rig.Claude.Calls.Single(c => c.Contains("#auth login", StringComparison.Ordinal));
        login.Should().EndWith("#auth login --claudeai --email expected@example.test");
        Path.GetFullPath(login[..login.IndexOf('#', StringComparison.Ordinal)]).Should().BeEquivalentTo(Path.GetFullPath(claude.ConfigDirectory!), "主控台帶這個帳戶的 CLAUDE_CONFIG_DIR");
        rig.State(claude.Id).LoginBusy.Should().BeTrue("主控台還開著，等那台的人完成");
        rig.Opened.Should().Be(0);

        await rig.CancelAndWaitAsync(claude.Id);
        rig.Claude.Calls.Should().NotContain("login-done", "取消了就沒有寫憑證");
    }

    [Fact]
    public async Task Add_startLogin為false_不登入()
    {
        using var rig = new RemoteLoginRig();
        var added = rig.H.OnUi(() => rig.H.Accounts.Add(AgentProvider.Codex, "遠端建立", null, new AccountClaim(LogicalId, null), startLogin: false));
        await rig.H.Ui.DrainAsync();
        await Task.Delay(500);

        rig.H.OnUi(() => rig.H.Accounts.LastLoginRequest).Should().BeNull("沒有開始登入");
        rig.State(added.Id).LoginBusy.Should().BeFalse();
        rig.State(added.Id).Status.Should().Be(AccountStatus.NotLoggedIn);
        added.LogicalAccountId.Should().Be(LogicalId);
        added.IsSystemDefault.Should().BeFalse();
        AccountManager.GetDeletableAccountRoot(added).Should().NotBeNull("建在 app 管理的 accounts 底下");
        Directory.Exists(added.ConfigDirectory).Should().BeTrue();
        rig.Fake.Started.Should().BeFalse("沒有起 app-server");
        rig.H.OnUi(() => rig.H.Accounts.GetRemoteLoginFacts(added, DateTimeOffset.UtcNow)).ManagedFolder.Should().BeTrue();

        // 對照：預設值照舊開始登入（Claude 開主控台）
        var local = rig.H.OnUi(() => rig.H.Accounts.Add(AgentProvider.Claude, "本機新增", null));
        await rig.H.WaitUntilAsync(() => rig.H.Accounts.LastLoginRequest is not null, "預設的新增照舊開始登入", Slow);
        rig.H.OnUi(() => rig.H.Accounts.LastLoginRequest).Should().Be((LoginMethod.Default, false));
        await rig.Claude.WaitForAsync(calls => calls.Any(c => c.Contains("#auth login", StringComparison.Ordinal)), Slow);
        await rig.CancelAndWaitAsync(local.Id);
    }

    [Fact]
    public void RemoteLoginFacts_受管資料夾判斷()
    {
        using var rig = new RemoteLoginRig();
        var now = DateTimeOffset.UtcNow;
        var outside = new AgentAccount
        {
            Id = "acc-outside", Provider = AgentProvider.Codex, Label = "資料夾在外面", Color = "#123456",
            ConfigDirectory = Path.Combine(Path.GetTempPath(), "x", "codex"),
        };
        rig.H.Ui.Invoke(() => rig.H.Accounts.Seed(new[] { outside }, AccountStatus.NotLoggedIn));
        RemoteLoginFacts Facts(AgentAccount a) => rig.H.OnUi(() => rig.H.Accounts.GetRemoteLoginFacts(a, now));

        var codex = Facts(rig.H.Codex);
        codex.Should().Be(new RemoteLoginFacts(AgentProvider.Codex, Exists: true, IsSystemDefault: false, ManagedFolder: true, AccountStatus.NotLoggedIn,
            LoginExpired: false, AccountLoginBusy: false, AnyLoginActive: false, CliPaused: false, CliInstalled: true));
        RemoteAccountRules.CheckLogin(codex, LoginMethod.DeviceCode).Allowed.Should().BeTrue();

        Facts(outside).ManagedFolder.Should().BeFalse("不在 app 的 accounts 資料夾底下");
        Facts(outside).IsSystemDefault.Should().BeFalse();
        RemoteAccountRules.CheckLogin(Facts(outside), LoginMethod.DeviceCode).Code.Should().Be(RemoteResultCodes.Policy);

        var systemDefault = Facts(rig.H.Claude);
        systemDefault.IsSystemDefault.Should().BeTrue();
        systemDefault.ManagedFolder.Should().BeFalse();

        // 新建帳戶的事實
        var fresh = rig.H.OnUi(() => rig.H.Accounts.GetNewAccountFacts(AgentProvider.Codex));
        fresh.Should().Be(new RemoteLoginFacts(AgentProvider.Codex, Exists: false, IsSystemDefault: false, ManagedFolder: true, AccountStatus.NotLoggedIn,
            LoginExpired: false, AccountLoginBusy: false, AnyLoginActive: false, CliPaused: false, CliInstalled: true));

        // Claude 的登入到期看 LoginExpiresAt；CLI 更新中、沒安裝照實回報
        rig.H.Ui.Invoke(() =>
        {
            var state = rig.H.Accounts.GetState(rig.H.Claude2.Id);
            state.Status = AccountStatus.Ready;
            state.LoginExpiresAt = now.ToUnixTimeMilliseconds() - 1000;
        });
        Facts(rig.H.Claude2).LoginExpired.Should().BeTrue();
        rig.H.Ui.Invoke(() => rig.H.Accounts.GetState(rig.H.Claude2.Id).LoginExpiresAt = now.AddHours(1).ToUnixTimeMilliseconds());
        Facts(rig.H.Claude2).LoginExpired.Should().BeFalse();
        rig.H.Ui.Invoke(() => rig.H.Cli.Update(AgentProvider.Codex, s => s with { UpdatePhase = CliUpdatePhase.Installing }));
        Facts(rig.H.Codex).CliPaused.Should().BeTrue();
        rig.H.OnUi(() => rig.H.Accounts.GetNewAccountFacts(AgentProvider.Codex)).CliPaused.Should().BeTrue();
        rig.H.Ui.Invoke(() => rig.H.Accounts.ResolveLauncher = _ => null);
        Facts(rig.H.Codex).CliInstalled.Should().BeFalse();
    }
}
