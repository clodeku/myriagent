using System.Diagnostics;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit.Sdk;

namespace Myriagent.Tests.Hub;

/// <summary>
/// WP4.8 步 7（D1～D10、D22）：中樞的遠端登入處理者——網頁發起的 accountLogin、accountLoginDevice 經閘門到 AccountRemoteHandler：
/// 前置條件在起任何程序之前查、什麼都不改；拿到驗證碼（Codex）或主控台開啟（Claude）就以唯一的一則 accountLogin 回 done（形狀同 Hub/Samples 的樣本）；
/// 被拒時沒有任何輸出、這個指令新建的帳戶連資料夾回滾。一律經 RemoteAccountRig：假 app-server／假 claude、假 CLI 管理員、守門，不跑真的 CLI
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteLoginTests
{
    private const int Slow = RemoteAccountRig.Slow;

    private const string Logical = "la_3123456789ABCDEFGHJKMNPQRS";

    /// <summary>目錄裡的 Codex（這台沒有本機帳戶：要建立）</summary>
    private static RemoteLoginLookup NewCodexTarget(string email = FakeCodexAppServer.Email) => RemoteLoginLookup.Found(new RemoteLoginTarget(
        Logical, AgentProvider.Codex, "網頁的 Codex", null,
        new CatalogAccountTemplate(Logical, AgentProvider.Codex, "網頁的 Codex", "#19C3A6", email),
        new RemoteLoginExpectation("codex:" + email, email, null)));

    private static void ShouldBeRejectedWithoutOutput(RemoteOutcome outcome, Recorder<JObject> output, string code)
    {
        RemoteEntrySamples.ShouldBeRejected(outcome, code, RemoteGateSteps.Handler);
        output.All.Should().BeEmpty("被拒時只回拒絕，不送 accountLogin（D9）");
    }

    private static IReadOnlyList<string> AccountIds(RemoteAccountRig rig) => rig.H.OnUi(() => rig.H.Accounts.Accounts.Select(a => a.Id).ToList());

    private static int AppServerStarts(RemoteAccountRig rig) => rig.Fake.Calls.Count(c => c.StartsWith("args=", StringComparison.Ordinal));

    [Fact]
    public async Task accountLogin_Codex隔離_done_輸出恰好一則帶deviceCode_本機視窗也收到()
    {
        using var rig = new RemoteAccountRig();
        var h = rig.H;
        var output = new Recorder<JObject>();

        var outcome = await rig.RunAsync(RemoteAccountRig.Login(h.Codex.Id, "deviceCode"), output: output);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        outcome.Tier.Should().Be(2);
        output.All.Should().ContainSingle("只輸出一則（D9）");
        var message = output.All[0];
        message.Value<string>("type").Should().Be("accountLogin");
        message.Value<string>("accountId").Should().Be(h.Codex.Id, "電腦送本機 id，伺服器轉給瀏覽器時才換");
        message.Value<bool>("done").Should().BeFalse();
        message["deviceCode"]!.Value<string>("userCode").Should().Be(FakeCodexAppServer.UserCode);
        message["deviceCode"]!["expiresAt"]!.Type.Should().Be(JTokenType.Integer);
        message.ToString(Formatting.None).Should().NotContainAny(["loginId", FakeCodexAppServer.LoginId]);
        RemoteAccountOutputContractTests.AssertSameShape(message, RemoteAccountOutputContractTests.Sample(RemoteAccountOutputContractTests.CodeShownSample));

        // 驗證碼也顯示在這台的帳戶卡（D22）；只登隔離資料夾、不開瀏覽器
        await h.WaitUntilAsync(() => h.Main.Messages("accountLogin").Any(m => m["deviceCode"] is JObject), "本機視窗收到帶驗證碼的 accountLogin", Slow);
        rig.Fake.CodexHomes.Should().NotBeEmpty().And.OnlyContain(home => string.Equals(Path.GetFullPath(home), Path.GetFullPath(h.Codex.ConfigDirectory!), StringComparison.OrdinalIgnoreCase));
        rig.Opened.Should().Be(0);

        await rig.CancelAndWaitAsync(h.Codex.Id);
    }

    [Fact]
    public async Task accountLogin_Codex沒帶method_invalid_Claude帶deviceCode_invalid_都沒有啟動()
    {
        using var rig = new RemoteAccountRig();
        var output = new Recorder<JObject>();

        ShouldBeRejectedWithoutOutput(await rig.RunAsync(RemoteAccountRig.Login(rig.H.Codex.Id), output: output), output, RemoteResultCodes.Invalid);
        ShouldBeRejectedWithoutOutput(await rig.RunAsync(RemoteAccountRig.Login(rig.H.Claude2.Id, "deviceCode"), output: output), output, RemoteResultCodes.Invalid);

        await rig.H.Ui.DrainAsync();
        rig.Fake.Started.Should().BeFalse("Codex 從網頁只能用裝置碼（D3）");
        rig.Claude.Started.Should().BeFalse("Claude 沒有裝置碼登入");
    }

    [Theory]
    [InlineData("系統預設")]
    [InlineData("不是受管資料夾")]
    public async Task accountLogin_系統預設或不是受管資料夾的Codex_policy_沒有啟動_訊息說明隔離帳戶(string kind)
    {
        using var rig = new RemoteAccountRig();
        var h = rig.H;
        h.Ui.Invoke(() => h.Accounts.Find(h.Codex.Id)!.ConfigDirectory = kind == "系統預設" ? null : h.Temp.Combine("elsewhere", "codex"));
        var output = new Recorder<JObject>();

        var outcome = await rig.RunAsync(RemoteAccountRig.Login(h.Codex.Id, "deviceCode"), output: output);

        ShouldBeRejectedWithoutOutput(outcome, output, RemoteResultCodes.Policy);
        outcome.Message.Should().Contain("隔離帳戶");
        await h.Ui.DrainAsync();
        rig.Fake.Started.Should().BeFalse("擁有者指示 3：從網頁絕不登入電腦預設的 ~/.codex（D4）");
    }

    [Theory]
    [InlineData("別的帳戶在登入")]
    [InlineData("同一個帳戶在登入")]
    [InlineData("CLI正在更新")]
    [InlineData("沒安裝")]
    [InlineData("已登入")]
    [InlineData("ApiKey")]
    public async Task accountLogin_前置條件不過_conflict_沒有啟動新的登入(string kind)
    {
        using var rig = new RemoteAccountRig(status: kind == "已登入" ? AccountStatus.Ready : AccountStatus.NotLoggedIn);
        var h = rig.H;
        var target = h.Codex.Id;
        string expected;
        switch (kind)
        {
            case "別的帳戶在登入":
            case "同一個帳戶在登入":
                // 這台本機開一個裝置碼登入（Codex），等驗證碼出現
                h.Command(h.Main, "accountLogin", new { accountId = h.Codex.Id, method = "deviceCode" });
                await h.WaitUntilAsync(() => h.Main.Messages("accountLogin").Any(m => m["deviceCode"] is JObject), "本機的裝置碼登入已開始", Slow);
                target = kind == "別的帳戶在登入" ? h.Claude2.Id : h.Codex.Id;
                expected = kind == "別的帳戶在登入" ? "另一個帳戶" : "正在那台登入";
                break;
            case "CLI正在更新":
                h.Ui.Invoke(() => h.Cli.Update(AgentProvider.Codex, s => s with { UpdatePhase = CliUpdatePhase.Installing }));
                expected = "正在更新";
                break;
            case "沒安裝":
                rig.Launchers = provider => provider == AgentProvider.Codex ? null : rig.DefaultLauncher(provider);
                expected = "還沒有安裝";
                break;
            case "已登入":
                expected = "已經登入";
                break;
            case "ApiKey":
                h.Ui.Invoke(() => h.Accounts.GetState(h.Codex.Id).Status = AccountStatus.ApiKey);
                expected = "API Key";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        var starts = AppServerStarts(rig);
        var output = new Recorder<JObject>();
        var method = target == h.Codex.Id ? "deviceCode" : null;

        var outcome = await rig.RunAsync(RemoteAccountRig.Login(target, method), output: output);

        ShouldBeRejectedWithoutOutput(outcome, output, RemoteResultCodes.Conflict);
        outcome.Message.Should().Contain(expected);
        await h.Ui.DrainAsync();
        AppServerStarts(rig).Should().Be(starts, "前置條件不過就不起任何程序（D6）");
        rig.Claude.Started.Should().BeFalse();
        h.OnUi(() => h.Accounts.HasRemoteLoginRun(target)).Should().BeFalse();
        if (kind is "別的帳戶在登入" or "同一個帳戶在登入")
            await rig.CancelAndWaitAsync(h.Codex.Id);
    }

    [Fact]
    public async Task accountLogin_slow_start_在30秒內rejected_conflict_不是unknown()
    {
        using var rig = new RemoteAccountRig(new FakeCodexAppServer("ok", startDelayMs: 40_000));
        var h = rig.H;
        h.Ui.Invoke(() => h.Accounts.RemoteStartDeadline = TimeSpan.FromSeconds(2));
        var output = new Recorder<JObject>();
        var watch = Stopwatch.StartNew();

        var outcome = await rig.RunAsync(RemoteAccountRig.Login(h.Codex.Id, "deviceCode"), output: output);

        watch.Elapsed.Should().BeLessThan(AgentHub.RemoteOutputWindow, "期限到了就拒絕，不等到閘門的 30 秒變成 unknown（D8）");
        ShouldBeRejectedWithoutOutput(outcome, output, RemoteResultCodes.Conflict);
        outcome.Message.Should().Be(RemoteAccountRules.StartTimeoutMessage(AgentProvider.Codex));
        await rig.WaitLoginEndedAsync(h.Codex.Id);
        rig.State(h.Codex.Id).DeviceCode.Should().BeNull();
    }

    [Fact]
    public async Task accountLogin_unsupported_conflict_請更新Codex_evil_host_rejected_沒有任何輸出()
    {
        using (var unsupported = new RemoteAccountRig(new FakeCodexAppServer("unsupported")))
        {
            var output = new Recorder<JObject>();
            var outcome = await unsupported.RunAsync(RemoteAccountRig.Login(unsupported.H.Codex.Id, "deviceCode"), output: output);
            ShouldBeRejectedWithoutOutput(outcome, output, RemoteResultCodes.Conflict);
            outcome.Message.Should().Contain("請更新 Codex");
            await unsupported.WaitLoginEndedAsync(unsupported.H.Codex.Id);
        }

        using var evil = new RemoteAccountRig(new FakeCodexAppServer("evil_host"));
        var evilOutput = new Recorder<JObject>();
        var rejected = await evil.RunAsync(RemoteAccountRig.Login(evil.H.Codex.Id, "deviceCode"), output: evilOutput);
        ShouldBeRejectedWithoutOutput(rejected, evilOutput, RemoteResultCodes.Conflict);
        evil.Fake.Calls.Should().Contain("account/login/cancel loginId=" + FakeCodexAppServer.LoginId);
        await evil.WaitLoginEndedAsync(evil.H.Codex.Id);
        evil.H.Main.Messages("accountLogin").Should().NotContain(m => m["deviceCode"] is JObject, "不合格的網址不顯示");
    }

    [Fact]
    public async Task complete_fail_輸出仍帶驗證碼()
    {
        // 驗證碼一出現就收到失敗的完成通知，狀態裡的驗證碼馬上被清掉：輸出由里程碑帶的那一組組成，不從狀態讀（D9、審查 #10）
        using var rig = new RemoteAccountRig(new FakeCodexAppServer("complete_fail"));
        var output = new Recorder<JObject>();

        var outcome = await rig.RunAsync(RemoteAccountRig.Login(rig.H.Codex.Id, "deviceCode"), output: output);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        output.All.Should().ContainSingle();
        output.All[0]["deviceCode"]!.Value<string>("userCode").Should().Be(FakeCodexAppServer.UserCode);
        await rig.WaitLoginEndedAsync(rig.H.Codex.Id);
    }

    [Fact]
    public async Task accountLoginDevice_沒有本機帳戶_建立隔離帳戶並登入_LogicalAccountId_CODEX_HOME在accounts下()
    {
        using var rig = new RemoteAccountRig();
        var h = rig.H;
        rig.Bridge.ByLogical[Logical] = NewCodexTarget();
        var before = AccountIds(rig);
        var output = new Recorder<JObject>();

        var outcome = await rig.RunAsync(RemoteAccountRig.LoginDevice(Logical, "deviceCode"), output: output);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var created = h.OnUi(() => h.Accounts.Accounts.Single(a => !before.Contains(a.Id)));
        created.Provider.Should().Be(AgentProvider.Codex);
        created.LogicalAccountId.Should().Be(Logical, "宣告目錄的邏輯帳戶（同「在這台登入」）");
        created.Label.Should().Be("網頁的 Codex");
        created.Color.Should().Be("#19C3A6");
        var folder = Path.GetFullPath(created.ConfigDirectory!);
        folder.Should().Be(Path.GetFullPath(Path.Combine(AppPaths.AccountsDirectory, created.Id, "codex")), "app 管理的隔離資料夾");
        rig.Fake.CodexHomes.Should().NotBeEmpty().And.OnlyContain(home => string.Equals(Path.GetFullPath(home), folder, StringComparison.OrdinalIgnoreCase));
        output.All.Should().ContainSingle();
        output.All[0].Value<string>("accountId").Should().Be(created.Id, "伺服器把它換成邏輯 id 再給瀏覽器");
        RemoteAccountOutputContractTests.AssertSameShape(output.All[0], RemoteAccountOutputContractTests.Sample(RemoteAccountOutputContractTests.CodeShownSample));
        h.OnUi(() => h.Accounts.LastLoginRequest).Should().Be((LoginMethod.DeviceCode, false), "新建時不自動登入，只有遠端這一次（D11）");

        await rig.CancelAndWaitAsync(created.Id);
    }

    [Theory]
    [InlineData("拿不到驗證碼")]
    [InlineData("防禦分支")]
    public async Task accountLoginDevice_建立之後被拒_帳戶與資料夾都回滾(string kind)
    {
        using var rig = new RemoteAccountRig(new FakeCodexAppServer(kind == "拿不到驗證碼" ? "unsupported" : "ok"));
        var h = rig.H;
        rig.Bridge.ByLogical[Logical] = NewCodexTarget();
        if (kind == "防禦分支")
            h.Ui.Invoke(() => h.Hub.RemoteVerificationUrlCheck = _ => false);
        var before = AccountIds(rig);
        var output = new Recorder<JObject>();

        var outcome = await rig.RunAsync(RemoteAccountRig.LoginDevice(Logical, "deviceCode"), output: output);

        ShouldBeRejectedWithoutOutput(outcome, output, kind == "拿不到驗證碼" ? RemoteResultCodes.Conflict : RemoteResultCodes.Policy);
        AccountIds(rig).Should().Equal(before, "rejected＝什麼都沒改：這個指令新建的帳戶移除（D10）");
        var homes = rig.Fake.CodexHomes;
        homes.Should().NotBeEmpty("前置條件過了才建立、起 app-server");
        var folder = Path.GetDirectoryName(Path.GetFullPath(homes[0]))!;
        Path.GetDirectoryName(folder).Should().Be(Path.GetFullPath(AppPaths.AccountsDirectory).TrimEnd('\\'));
        await h.WaitUntilAsync(() => !Directory.Exists(folder), "新建帳戶的資料夾刪掉了", Slow);
        await h.WaitUntilAsync(() => rig.Fake.AllExited(), "登入的 app-server 已結束", Slow);
    }

    [Fact]
    public async Task accountLogin_防禦分支_已有的帳戶取消登入_帳戶留著()
    {
        // 帳戶管理員放過、中樞的最後一道擋下（D19）：已經開始的登入要取消（不留一個沒人看得到驗證碼的登入），已有的帳戶不移除
        using var rig = new RemoteAccountRig();
        var h = rig.H;
        h.Ui.Invoke(() => h.Hub.RemoteVerificationUrlCheck = _ => false);
        var before = AccountIds(rig);
        var output = new Recorder<JObject>();

        var outcome = await rig.RunAsync(RemoteAccountRig.Login(h.Codex.Id, "deviceCode"), output: output);

        ShouldBeRejectedWithoutOutput(outcome, output, RemoteResultCodes.Policy);
        outcome.Message.Should().Contain("不是允許的網址");
        await rig.WaitLoginEndedAsync(h.Codex.Id);
        rig.Fake.Calls.Should().Contain("account/login/cancel loginId=" + FakeCodexAppServer.LoginId, "已開始的那一次要取消");
        AccountIds(rig).Should().Equal(before);
        rig.State(h.Codex.Id).DeviceCode.Should().BeNull();
    }

    [Fact]
    public async Task accountLoginDevice_未配對_invalid_找不到目標照同步橋的代碼()
    {
        using (var unpaired = new RemoteAccountRig(attachBridge: false))
        {
            var output = new Recorder<JObject>();
            var outcome = await unpaired.RunAsync(RemoteAccountRig.LoginDevice(Logical, "deviceCode"), output: output);
            ShouldBeRejectedWithoutOutput(outcome, output, RemoteResultCodes.Invalid);
            outcome.Message.Should().Be(AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.NotPaired));
            unpaired.Fake.Started.Should().BeFalse();
        }

        using var rig = new RemoteAccountRig();
        var before = AccountIds(rig);
        var outputs = new Recorder<JObject>();
        rig.Bridge.ByLogical[Logical] = RemoteLoginLookup.Rejected(RemoteResultCodes.Policy, "這種帳戶只在各台電腦本機使用");
        var policy = await rig.RunAsync(RemoteAccountRig.LoginDevice(Logical, "deviceCode"), output: outputs);
        ShouldBeRejectedWithoutOutput(policy, outputs, RemoteResultCodes.Policy);
        policy.Message.Should().Be("這種帳戶只在各台電腦本機使用");
        rig.Bridge.ByLogical.Remove(Logical);
        ShouldBeRejectedWithoutOutput(await rig.RunAsync(RemoteAccountRig.LoginDevice(Logical, "deviceCode"), output: outputs), outputs, RemoteResultCodes.Invalid);
        AccountIds(rig).Should().Equal(before);
        rig.Fake.Started.Should().BeFalse();
    }

    [Fact]
    public async Task accountLoginDevice_Claude_輸出寫明要有人在那台_帶目錄的預期信箱()
    {
        using var rig = new RemoteAccountRig();
        var h = rig.H;
        rig.Bridge.ByLogical[Logical] = RemoteLoginLookup.Found(new RemoteLoginTarget(Logical, AgentProvider.Claude, "公司的 Claude", h.Claude2.Id, null,
            new RemoteLoginExpectation("claude:expected@example.test", "expected@example.test", null)));
        rig.Bridge.Emails[h.Claude2.Id] = "expected@example.test";
        var output = new Recorder<JObject>();

        var outcome = await rig.RunAsync(RemoteAccountRig.LoginDevice(Logical), output: output);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        output.All.Should().ContainSingle();
        output.All[0].Value<string>("message").Should().Be(RemoteAccountRules.ConsoleOpenedMessage);
        output.All[0].Value<string>("accountId").Should().Be(h.Claude2.Id);
        RemoteAccountOutputContractTests.AssertSameShape(output.All[0], RemoteAccountOutputContractTests.Sample(RemoteAccountOutputContractTests.ConsoleOpenedSample));
        await rig.Claude.WaitForAsync(calls => calls.Any(c => c.Contains("auth login", StringComparison.Ordinal)), Slow);
        rig.Claude.Commands.Should().Contain(c => c.Contains("--email expected@example.test", StringComparison.Ordinal));

        await rig.CancelAndWaitAsync(h.Claude2.Id);
    }

    [Fact]
    public async Task 處理者_未授權_invalid_不是登入指令的不接()
    {
        using var rig = new RemoteAccountRig();
        var h = rig.H;
        var handler = h.OnUi(() => h.Hub.CreateAccountRemoteHandlerForTest());
        var view = new FakeView("remote-test");
        var context = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, null, null, 2, false, null, 2_000, false, RemoteEntrySamples.IssuerLabel);

        h.OnUi(() => handler.TryHandle(context, view, "accountLogin", RemoteAccountRig.Login(h.Codex.Id, "deviceCode"))).Should().BeTrue();
        var outcome = await context.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Invalid, RemoteGateSteps.Handler);

        var other = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, null, null, 1, false, null, 2_000, false, RemoteEntrySamples.IssuerLabel);
        h.OnUi(() => handler.TryHandle(other, view, "accountLoginCancel", new JObject { ["cmd"] = "accountLoginCancel", ["accountId"] = h.Codex.Id }))
            .Should().BeFalse("accountLoginCancel 留在退路（D18）：帳戶處理者只接登入、登出與 CLI 的安裝更新");
        await h.Ui.DrainAsync();
        rig.Fake.Started.Should().BeFalse();
    }

    [Fact]
    public async Task 身分不符_系統匣通知只帶帳戶名稱_不含email與驗證碼()
    {
        using var rig = new RemoteAccountRig();
        var h = rig.H;
        rig.Bridge.ByLocal[h.Codex.Id] = RemoteLoginLookup.Found(new RemoteLoginTarget(Logical, AgentProvider.Codex, "Codex 帳戶", h.Codex.Id, null,
            new RemoteLoginExpectation("codex:other@example.test", "other@example.test", null)));

        var outcome = await rig.RunAsync(RemoteAccountRig.Login(h.Codex.Id, "deviceCode"));
        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        rig.Fake.Complete();

        await h.WaitUntilAsync(() => h.Host.Notifications.All.Any(n => n.Title == AgentHub.RemoteIdentityMismatchTitle), "身分不符的系統匣通知", Slow);
        var notice = h.Host.Notifications.All.Single(n => n.Title == AgentHub.RemoteIdentityMismatchTitle);
        notice.Message.Should().Be(RemoteAccountRules.MismatchNotifyText("Codex 帳戶", loggedOut: true));
        notice.Message.Should().NotContainAny(["@", FakeCodexAppServer.UserCode, FakeCodexAppServer.LoginId, rig.Claude.Sentinel]);
        rig.Fake.Calls.Should().Contain("logout", "在同一個資料夾登出（D13）");
        await rig.WaitLoginEndedAsync(h.Codex.Id);
    }

    [Fact]
    public async Task rig守門_解析到暫存以外的執行檔就失敗_而且不交出去()
    {
        var rig = new RemoteAccountRig();
        try
        {
            rig.Launchers = _ => new AgentLauncher(Path.Combine(Environment.SystemDirectory, "where.exe"), ["codex"]);
            var outcome = await rig.RunAsync(RemoteAccountRig.Login(rig.H.Codex.Id, "deviceCode"));
            RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
            outcome.Message.Should().Contain("還沒有安裝", "守門回 null：這台當成沒裝，什麼都沒跑");

            rig.Installations = provider => new CliInstallation(provider, new AgentLauncher(@"C:\Program Files\fake\claude.exe", []), CliInstallKind.Native,
                @"C:\Program Files\fake\claude.exe");
            rig.H.OnUi(() => rig.H.Cli.GetLauncher(AgentProvider.Claude)).Should().BeNull("找到暫存以外的安裝也不交出去");
            rig.Violations.Should().HaveCountGreaterThanOrEqualTo(2);
        }
        finally
        {
            rig.Invoking(r => r.Dispose()).Should().Throw<XunitException>().WithMessage("*守門*");
        }
    }
}
