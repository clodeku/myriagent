using System.Collections.Concurrent;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Json;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Accounts;

/// <summary>
/// WP4.8 步 4：遠端登入完成後比對身分（只比信箱）、不符就在同一個資料夾登出並寫明原因（D12～D15），以及比對期間暫停查詢（identityHold，D14）。
/// 一律以假 app-server（FakeCodexAppServer，登出狀態依 CODEX_HOME 分開）與假 claude（FakeClaudeLogin）為 ResolveLauncher：
/// 不跑真的 claude／codex、不開瀏覽器、不連網；設定資料夾都在暫存資料夾
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class RemoteIdentityTests
{
    private const int Slow = RemoteLoginRig.Slow;
    private const string Expected = "expected@example.test";
    private const string Wrong = "wrong@example.test";

    private static LoginRequest CodexRemote(string email = Expected)
        => new(LoginMethod.DeviceCode, null, new RemoteLoginExpectation("codex:" + email, email, null));

    private static LoginRequest ClaudeRemote(RemoteLoginExpectation expected) => new(LoginMethod.Default, expected.Email, expected);

    /// <summary>
    /// 在背景每 20 毫秒記一次帳戶的信箱與同步身分（UI 執行緒上讀），Dispose 時停下；<see cref="Seen"/> 是看過的所有值
    /// </summary>
    private sealed class IdentitySampler : IDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;

        public IdentitySampler(RemoteLoginRig rig, AgentAccount account)
        {
            loop = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    Seen.Add(rig.H.OnUi(() => (account.Email ?? "") + "|" + JsonConvert.SerializeObject(rig.H.Accounts.GetSyncIdentity(account.Id))));
                    await Task.Delay(20);
                }
            });
        }

        public ConcurrentBag<string> Seen { get; } = new();

        public void Dispose()
        {
            stop.Cancel();
            loop.Wait(TimeSpan.FromSeconds(20));
        }
    }

    private static string NormalizedPath(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    [Fact]
    public async Task Codex_身分相符_不登出_Ready()
    {
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("ok", email: Expected));
        var start = await rig.StartAsync(rig.Codex, CodexRemote());
        start.Milestone.Should().Be(RemoteLoginMilestone.CodeShown);
        rig.IsHeld(rig.Codex).Should().BeTrue("身分比對完之前暫停查詢");
        rig.Fake.Complete();

        var done = await rig.WaitDoneAsync(rig.Codex);
        done.Success.Should().BeTrue();
        done.Message.Should().Be("「Codex 帳戶」已登入");
        rig.Fake.Calls.Should().NotContain("logout");
        rig.Mismatches.Should().BeEmpty();
        rig.IsHeld(rig.Codex).Should().BeFalse();
        rig.State(rig.Codex).Status.Should().Be(AccountStatus.Ready);
        rig.State(rig.Codex).RemoteLoginNote.Should().BeNull();
        rig.H.OnUi(() => rig.H.Codex.Email).Should().Be(Expected);
    }

    [Fact]
    public async Task 登入後的重新查詢做完才清LoginBusy_不忙的時候一定已經是登入後的狀態()
    {
        // 審查 F1：account/read 慢 2 秒，登入後的重新查詢（起 app-server、account/read）這段期間狀態還是登入前的未登入。
        // 每 20 毫秒取樣一次（同「任何時候的一次帳戶清單送出」）：先清 LoginBusy 的話，會取到「不忙＋未登入」，網頁會把成功的登入判成沒有完成
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("ok", email: Expected, readDelayMs: 2000));
        var samples = new ConcurrentQueue<(bool Busy, AccountStatus Status)>();
        (bool, AccountStatus) Sample() => rig.H.OnUi(() => (rig.H.Accounts.GetState(rig.Codex).LoginBusy, rig.H.Accounts.GetState(rig.Codex).Status));
        using var stop = new CancellationTokenSource();
        var sampling = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                samples.Enqueue(Sample());
                await Task.Delay(20);
            }
        });

        var start = await rig.StartAsync(rig.Codex, CodexRemote());
        start.Milestone.Should().Be(RemoteLoginMilestone.CodeShown);
        rig.Fake.Complete();
        var done = await rig.WaitDoneAsync(rig.Codex);
        stop.Cancel();
        await sampling.WaitAsync(TimeSpan.FromSeconds(20));
        samples.Enqueue(Sample());

        done.Success.Should().BeTrue();
        var list = samples.ToList();
        var first = list.FindIndex(s => s.Busy);
        first.Should().BeGreaterThanOrEqualTo(0, "取樣看得到登入中");
        var after = list.Skip(first).ToList();
        after.Where(s => !s.Busy).Should().NotBeEmpty("正向對照：取樣看得到登入結束")
            .And.OnlyContain(s => s.Status == AccountStatus.Ready, "LoginBusy 變回 false 時重新查詢已經做完（不會發布 loginBusy=false＋未登入）");
        rig.State(rig.Codex).LoginBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Codex_身分不符_app_server關掉之後才登出_同一個CODEX_HOME_未登入_Email從沒變成錯的()
    {
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("ok", email: Wrong));
        using var sampler = new IdentitySampler(rig, rig.H.Codex);
        var start = await rig.StartAsync(rig.Codex, CodexRemote());
        start.Milestone.Should().Be(RemoteLoginMilestone.CodeShown);
        rig.Fake.Complete();
        // 比對期間另外要求查詢（像憑證檔變動、定期輪詢）：暫停中一律延後，不會先套用錯的身分
        for (var i = 0; i < 5; i++)
        {
            rig.H.Ui.Invoke(() => { _ = rig.H.Accounts.RefreshAsync(rig.Codex, includeStatus: true); });
            await Task.Delay(200);
        }

        var done = await rig.WaitDoneAsync(rig.Codex);
        sampler.Dispose();

        var calls = rig.Fake.Calls;
        var logoutAt = calls.ToList().IndexOf("args=logout");
        logoutAt.Should().BeGreaterThan(0, "身分不符就登出");
        var exitAt = calls.ToList().IndexOf("exit");
        exitAt.Should().BeGreaterThan(0).And.BeLessThan(logoutAt, "登入那條 app-server 先關掉才登出（app-server 開著時登出可能被它寫回）");
        calls[logoutAt + 1].Should().StartWith("codexHome=");
        NormalizedPath(calls[logoutAt + 1]["codexHome=".Length..]).Should().BeEquivalentTo(NormalizedPath(rig.H.Codex.ConfigDirectory!), "在同一個資料夾登出");
        File.Exists(Path.Combine(rig.H.Codex.ConfigDirectory!, "fake-logged-out")).Should().BeTrue("假 app-server 在這個 CODEX_HOME 留了登出標記");

        rig.Mismatches.Should().Equal((rig.Codex, true));
        rig.IsHeld(rig.Codex).Should().BeFalse();
        var state = rig.State(rig.Codex);
        state.Status.Should().Be(AccountStatus.NotLoggedIn);
        state.RemoteLoginNote.Should().Be(RemoteAccountRules.MismatchNote(true));
        state.StatusText.Should().Be(RemoteAccountRules.MismatchNote(true), "說明經 StatusText（link.upsert）到矩陣");
        done.Success.Should().BeFalse();
        done.Message.Should().Be(RemoteAccountRules.MismatchNote(true));

        sampler.Seen.Should().NotBeEmpty();
        sampler.Seen.Should().NotContain(s => s.Contains(Wrong, StringComparison.OrdinalIgnoreCase), "錯的信箱從沒寫進帳戶與同步身分");
        rig.H.OnUi(() => rig.H.Codex.Email).Should().NotBe(Wrong);
    }

    [Fact]
    public async Task Codex_身分不符_登出結束碼1_事件loggedOut為false_note寫沒有成功()
    {
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("ok", email: Wrong, logoutExitCode: 1));
        var start = await rig.StartAsync(rig.Codex, CodexRemote());
        start.Milestone.Should().Be(RemoteLoginMilestone.CodeShown);
        rig.Fake.Complete();

        var done = await rig.WaitDoneAsync(rig.Codex);
        rig.Fake.Calls.Should().Contain("logout");
        rig.Mismatches.Should().Equal((rig.Codex, false));
        rig.State(rig.Codex).RemoteLoginNote.Should().Be(RemoteAccountRules.MismatchNote(false));
        rig.State(rig.Codex).StatusText.Should().Be(RemoteAccountRules.MismatchNote(false));
        done.Success.Should().BeFalse();
        done.Message.Should().Be(RemoteAccountRules.MismatchNote(false));
    }

    [Fact]
    public async Task 暫停期間_憑證檔變動的重新查詢延後_沒有起app_server()
    {
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("ok", email: Expected));
        var start = await rig.StartAsync(rig.Codex, CodexRemote());
        start.Milestone.Should().Be(RemoteLoginMilestone.CodeShown);
        rig.IsHeld(rig.Codex).Should().BeTrue();
        int AppServers() => rig.Fake.Calls.Count(c => c.StartsWith("args=app-server", StringComparison.Ordinal));
        var before = AppServers();

        // 同憑證檔監看的重新查詢（CredentialsChanged）：暫停中不起任何程序，稍後再查
        var requested = DateTimeOffset.UtcNow;
        await rig.H.OnUi(() => rig.H.Accounts.RefreshAsync(rig.Codex, includeStatus: true, skipIfRunning: false, ClaudeStatusReason.CredentialsChanged));
        await Task.Delay(1500);
        AppServers().Should().Be(before, "暫停期間沒有起 app-server");
        rig.H.OnUi(() => rig.H.Accounts.GetNextPoll(rig.Codex)).Should().NotBeNull()
            .And.BeOnOrBefore(requested + RemoteAccountRules.IdentityHoldRetry + TimeSpan.FromSeconds(2), "排了稍後再查");

        // 登入結束後照常查（登入的重新查詢起 app-server）
        await rig.CancelAndWaitAsync(rig.Codex);
        await rig.WaitDoneAsync(rig.Codex);
        rig.IsHeld(rig.Codex).Should().BeFalse();
        AppServers().Should().BeGreaterThan(before);
    }

    [Fact]
    public async Task 進行中的查詢在暫停後回來_不套用身分()
    {
        // 查詢用的假 app-server：已登入（錯的信箱），account/read 4 秒後才回；登入用另一個（rig 的）
        using var slow = new FakeCodexAppServer("ok", email: Wrong, readDelayMs: 4000);
        slow.Complete();
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("ok", email: Expected));
        rig.H.Ui.Invoke(() => rig.H.Accounts.ResolveLauncher = p => p == AgentProvider.Codex ? slow.Launcher : rig.Resolve(p));
        var refresh = rig.H.OnUi(() => rig.H.Accounts.RefreshAsync(rig.Codex, includeStatus: true));
        await slow.WaitForAsync(calls => calls.Contains("account/read"), Slow);

        // 查詢已在等 account/read 的回應：這時才開始遠端登入
        rig.H.Ui.Invoke(() => rig.H.Accounts.ResolveLauncher = rig.Resolve);
        var start = await rig.StartAsync(rig.Codex, CodexRemote());
        start.Milestone.Should().Be(RemoteLoginMilestone.CodeShown);
        rig.IsHeld(rig.Codex).Should().BeTrue();
        await refresh.WaitAsync(TimeSpan.FromMilliseconds(Slow));

        rig.H.OnUi(() => rig.H.Codex.Email).Should().BeNull("暫停期間回來的查詢不套用身分");
        rig.State(rig.Codex).Status.Should().Be(AccountStatus.NotLoggedIn, "也不套用狀態");
        rig.H.OnUi(() => rig.H.Accounts.GetUsage(rig.Codex)).Should().BeNull("也不套用用量");
        await rig.CancelAndWaitAsync(rig.Codex);
    }

    [Theory]
    [InlineData("CLI更新中")]
    [InlineData("沒有launcher")]
    public async Task 提早返回不會留下暫停(string kind)
    {
        using var rig = new RemoteLoginRig();
        if (kind == "CLI更新中")
            rig.H.Ui.Invoke(() => rig.H.Cli.Update(AgentProvider.Codex, s => s with { UpdatePhase = CliUpdatePhase.Installing }));
        else
            rig.H.Ui.Invoke(() => rig.H.Accounts.ResolveLauncher = _ => null);

        var start = await rig.StartAsync(rig.Codex, CodexRemote(), timeoutMs: 10_000);

        start.Milestone.Should().Be(RemoteLoginMilestone.Failed);
        rig.IsHeld(rig.Codex).Should().BeFalse("try 之外的提早返回沒有登記暫停");
    }

    [Fact]
    public async Task 暫停在例外之後也放開()
    {
        // init_fail：initialize 回錯誤，CodexAppServerClient.StartAsync 丟例外（登入的 try 之內）
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("init_fail"));
        var start = await rig.StartAsync(rig.Codex, CodexRemote());
        start.Milestone.Should().Be(RemoteLoginMilestone.Failed);
        start.Message.Should().StartWith("登入失敗");
        // 例外的 done 進度之後還有一則（重新查詢完的「登入沒有完成」）：等到它，登入那邊的查詢才都結束了
        await rig.H.WaitUntilAsync(() => rig.Progress.Any(p => p.Id == rig.Codex && p.Done && p.Message == "登入沒有完成"), "登入回報結束", Slow);
        rig.IsHeld(rig.Codex).Should().BeFalse("例外之後 finally 也放開暫停");

        int AppServers() => rig.Fake.Calls.Count(c => c.StartsWith("args=app-server", StringComparison.Ordinal));
        var before = AppServers();
        await rig.H.OnUi(() => rig.H.Accounts.RefreshAsync(rig.Codex, includeStatus: true)).WaitAsync(TimeSpan.FromMilliseconds(Slow));
        AppServers().Should().Be(before + 1, "之後的查詢照常起 app-server");
    }

    [Fact]
    public async Task Claude_信箱不符_先跑auth_logout_之前沒有auth_status()
    {
        using var rig = new RemoteLoginRig(claude: new FakeClaudeLogin(email: Wrong));
        var claude = rig.H.Claude2;
        using var sampler = new IdentitySampler(rig, claude);
        var start = await rig.StartAsync(claude.Id, ClaudeRemote(new RemoteLoginExpectation("claude:org-1:" + Expected, Expected, "org-1")));
        start.Milestone.Should().Be(RemoteLoginMilestone.ConsoleOpened);
        rig.Claude.Go();

        var done = await rig.WaitDoneAsync(claude.Id);
        sampler.Dispose();

        // 先試的 app 內登入（-p 的控制程序）假 claude 不支援、直接結束，之後才退回主控台
        var commands = rig.Claude.Commands.Where(c => !c.StartsWith("-p ", StringComparison.Ordinal)).ToList();
        commands[0].Should().StartWith("auth login");
        var logoutAt = commands.IndexOf("auth logout");
        logoutAt.Should().BeGreaterThan(0, "身分不符就登出");
        commands.Take(logoutAt).Should().NotContain(c => c.StartsWith("auth status", StringComparison.Ordinal), "比對只讀 CLI 設定檔，不跑 auth status（設計 6.5）");
        File.Exists(Path.Combine(claude.ConfigDirectory!, ".credentials.json")).Should().BeFalse("在同一個資料夾登出");
        rig.Mismatches.Should().Equal((claude.Id, true));
        rig.State(claude.Id).Status.Should().Be(AccountStatus.NotLoggedIn);
        rig.State(claude.Id).StatusText.Should().Be(RemoteAccountRules.MismatchNote(true));
        done.Success.Should().BeFalse();
        done.Message.Should().Be(RemoteAccountRules.MismatchNote(true));
        sampler.Seen.Should().NotContain(s => s.Contains(Wrong, StringComparison.OrdinalIgnoreCase), "錯的信箱從沒寫進帳戶");

        // 假憑證的哨兵字串沒有離開設定資料夾
        var log = RemoteLoginRig.LogText();
        log.Should().NotContain(rig.Claude.Sentinel).And.NotContain(Wrong);
        foreach (var message in rig.H.Main.All())
            message.ToString(Formatting.None).Should().NotContain(rig.Claude.Sentinel);
    }

    [Fact]
    public async Task Claude_自動登出之後在app外重新登入_說明清掉_回到已登入的預設說明()
    {
        // 審查 F5：「已在這台自動登出」之後，那台的人在終端機以 claude auth login 重新登入同一個資料夾：重新查詢是 Ready，那句說明不再成立
        using var rig = new RemoteLoginRig(claude: new FakeClaudeLogin(email: Wrong));
        var claude = rig.H.Claude2;
        var start = await rig.StartAsync(claude.Id, ClaudeRemote(new RemoteLoginExpectation("claude:org-1:" + Expected, Expected, "org-1")));
        start.Milestone.Should().Be(RemoteLoginMilestone.ConsoleOpened);
        rig.Claude.Go();
        await rig.WaitDoneAsync(claude.Id);
        rig.State(claude.Id).StatusText.Should().Be(RemoteAccountRules.MismatchNote(true), "前提：身分不符、已自動登出");

        // 在 app 外重新登入同一個資料夾；之後同憑證檔監看的重新查詢
        await rig.Claude.LoginOutsideAppAsync(claude.ConfigDirectory!);
        await rig.H.OnUi(() => rig.H.Accounts.RefreshAsync(claude.Id, includeStatus: true, skipIfRunning: false, ClaudeStatusReason.CredentialsChanged))
            .WaitAsync(TimeSpan.FromMilliseconds(Slow));

        var state = rig.State(claude.Id);
        state.Status.Should().Be(AccountStatus.Ready, "狀態說明：{0}；呼叫：{1}", state.StatusText, string.Join(" | ", rig.Claude.Calls));
        state.RemoteLoginNote.Should().BeNull();
        state.RemoteLoginNoteLoggedOut.Should().BeFalse();
        state.StatusText.Should().Be(AccountRules.DescribeStatus(AgentProvider.Claude, AccountStatus.Ready));
    }

    [Fact]
    public async Task Codex_自動登出沒有成功的說明_之後查到已登入也保留()
    {
        // 審查 F5 的對照：「自動登出沒有成功，請到這台登出」在登出成功之前一直成立，重新查詢是 Ready 也保留
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("ok", email: Wrong, logoutExitCode: 1));
        var start = await rig.StartAsync(rig.Codex, CodexRemote());
        start.Milestone.Should().Be(RemoteLoginMilestone.CodeShown);
        rig.Fake.Complete();
        await rig.WaitDoneAsync(rig.Codex);
        rig.State(rig.Codex).RemoteLoginNote.Should().Be(RemoteAccountRules.MismatchNote(false), "前提：身分不符、自動登出沒有成功");

        // 假 app-server 的 logout 結束碼是 1，但已留下登出標記：拿掉它，重新查詢回已登入（登出其實沒有發生）
        File.Delete(Path.Combine(rig.H.Codex.ConfigDirectory!, "fake-logged-out"));
        await rig.H.OnUi(() => rig.H.Accounts.RefreshAsync(rig.Codex, includeStatus: true)).WaitAsync(TimeSpan.FromMilliseconds(Slow));

        var state = rig.State(rig.Codex);
        state.Status.Should().Be(AccountStatus.Ready);
        state.RemoteLoginNote.Should().Be(RemoteAccountRules.MismatchNote(false));
        state.StatusText.Should().Be(RemoteAccountRules.MismatchNote(false));
    }

    [Fact]
    public async Task Claude_只有org不同_不登出()
    {
        using var rig = new RemoteLoginRig(claude: new FakeClaudeLogin(email: Expected, orgUuid: "org-other"));
        var claude = rig.H.Claude2;
        var start = await rig.StartAsync(claude.Id, ClaudeRemote(new RemoteLoginExpectation("claude:org-expected:" + Expected, Expected, "org-expected")));
        start.Milestone.Should().Be(RemoteLoginMilestone.ConsoleOpened);
        rig.Claude.Go();

        var done = await rig.WaitDoneAsync(claude.Id);
        rig.Claude.Commands.Should().NotContain("auth logout", "org 未確認、表示法也可能不同：只比信箱");
        rig.Mismatches.Should().BeEmpty();
        rig.State(claude.Id).RemoteLoginNote.Should().BeNull();
        rig.State(claude.Id).Status.Should().Be(AccountStatus.Ready, "狀態說明：{0}；呼叫：{1}", rig.State(claude.Id).StatusText, string.Join(" | ", rig.Claude.Calls));
        done.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Claude_目錄沒有信箱_Unknown_不登出()
    {
        using var rig = new RemoteLoginRig(claude: new FakeClaudeLogin(email: Wrong));
        var claude = rig.H.Claude2;
        var start = await rig.StartAsync(claude.Id, ClaudeRemote(new RemoteLoginExpectation("claude:org-only:", null, "org-only")));
        start.Milestone.Should().Be(RemoteLoginMilestone.ConsoleOpened);
        rig.Claude.Go();

        await rig.WaitDoneAsync(claude.Id);
        rig.Claude.Commands.Should().NotContain("auth logout", "目錄的預期信箱不明：不比、不登出");
        rig.Mismatches.Should().BeEmpty();
        rig.State(claude.Id).RemoteLoginNote.Should().BeNull();
        rig.IsHeld(claude.Id).Should().BeFalse();
    }

    [Fact]
    public async Task 記錄檔與事件不含驗證碼與loginId()
    {
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("ok", email: Wrong));
        var start = await rig.StartAsync(rig.Codex, CodexRemote());
        start.Milestone.Should().Be(RemoteLoginMilestone.CodeShown);
        rig.Fake.Complete();
        await rig.WaitDoneAsync(rig.Codex);
        await Task.Delay(700);   // 帳戶卡合併送出

        static bool HasSecret(string text) => text.Contains(FakeCodexAppServer.UserCode, StringComparison.OrdinalIgnoreCase)
                                              || text.Contains(FakeCodexAppServer.LoginId, StringComparison.OrdinalIgnoreCase);

        var log = RemoteLoginRig.LogText();
        log.Should().Contain("遠端登入「Codex 帳戶」的帳號和帳戶目錄不同", "正向對照：不符有寫進記錄檔");
        HasSecret(log).Should().BeFalse();
        log.Should().NotContain(Wrong, "記錄檔不記信箱");

        rig.Mismatches.Should().ContainSingle();
        rig.State(rig.Codex).RemoteLoginNote.Should().NotBeNull().And.NotContain("@");
        rig.H.Main.Messages("accounts").Should().NotBeEmpty();
        foreach (var accounts in rig.H.Main.Messages("accounts"))
        {
            var text = accounts.ToString(Formatting.None);
            HasSecret(text).Should().BeFalse("AccountView 不帶驗證碼");
            text.Should().NotContain(Wrong, "錯的信箱沒有寫進帳戶");
        }
        rig.H.Main.Last("accounts")!["accounts"]!.Children<JObject>().Single(a => a.Value<string>("id") == rig.Codex)
            .ToString(Formatting.None).Should().Contain(RemoteAccountRules.MismatchNote(true), "正向對照：說明在帳戶卡上");
        foreach (var toast in rig.H.Main.Messages("toast"))
            HasSecret(toast.ToString(Formatting.None)).Should().BeFalse();
        foreach (var done in rig.H.Main.Messages("accountLogin").Where(m => m.Value<bool>("done")))
            HasSecret(done.ToString(Formatting.None)).Should().BeFalse("done 的訊息不帶驗證碼");
        foreach (var progress in rig.Progress.Where(p => p.Done))
            progress.Message.Should().NotContain(Wrong);
    }
}
