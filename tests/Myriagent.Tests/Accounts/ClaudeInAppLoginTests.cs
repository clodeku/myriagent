using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Accounts;

/// <summary>
/// Claude 的 app 內登入（AccountManager.LoginClaudeAsync，2026-10-26）：不送訊息的程序 claude_authenticate，
/// 這台登入開瀏覽器到本機回呼的網址、等 claude_oauth_wait_for_completion；遠端登入的第一個里程碑是 UrlShown（手動的網址），
/// 貼回的授權碼經 SubmitClaudeLoginCodeAsync 交給 CLI。假 claude 支援控制請求（FakeClaudeLogin 的 inApp），不連網、不開瀏覽器
/// </summary>
public sealed class ClaudeInAppLoginTests
{
    private const int Slow = RemoteLoginRig.Slow;

    private static RemoteLoginRig NewRig() => new(claude: new FakeClaudeLogin(inApp: true));

    [Fact]
    public async Task 這台登入_開瀏覽器到本機回呼的網址_瀏覽器完成後登入成功_不開主控台()
    {
        using var rig = NewRig();
        var claude = rig.H.Claude2;

        _ = rig.H.OnUi(() => rig.H.Accounts.LoginAsync(claude.Id));
        await rig.H.WaitUntilAsync(() => rig.Opened == 1, "開了瀏覽器", Slow);
        await rig.Claude.WaitForAsync(calls => calls.Contains("control:claude_oauth_wait_for_completion"), Slow);
        rig.State(claude.Id).LoginBusy.Should().BeTrue();

        rig.Claude.Go();
        await rig.WaitLoginEndedAsync(claude.Id);

        rig.State(claude.Id).Status.Should().Be(AccountStatus.Ready);
        rig.Claude.Calls.Should().Contain("login-done");
        rig.Claude.Commands.Should().NotContain(c => c.StartsWith("auth login", StringComparison.Ordinal), "app 內登入成功就不開主控台");
        rig.Claude.Commands.Should().Contain(c => c.StartsWith("-p --input-format stream-json", StringComparison.Ordinal) && c.Contains("--no-session-persistence"));
    }

    [Fact]
    public async Task 遠端登入_第一個里程碑是登入網址_貼回正確的授權碼就登入()
    {
        using var rig = NewRig();
        var claude = rig.H.Claude2;

        var start = await rig.StartAsync(claude.Id, new LoginRequest());

        start.Milestone.Should().Be(RemoteLoginMilestone.UrlShown);
        start.LoginUrl!.Host.Should().Be("claude.com");
        var state = rig.Claude.LastState;
        state.Should().NotBeNull();
        start.LoginUrl.Query.Should().Contain("state=" + state);
        rig.Opened.Should().Be(0, "遠端登入不在這台開瀏覽器");
        await rig.Claude.WaitForAsync(calls => calls.Contains("control:claude_oauth_wait_for_completion"), Slow);

        var error = await rig.H.OnUi(() => rig.H.Accounts.SubmitClaudeLoginCodeAsync(claude.Id, $"  {FakeClaudeLogin.GoodCode}#{state} "));

        error.Should().BeNull();
        await rig.WaitLoginEndedAsync(claude.Id);
        rig.State(claude.Id).Status.Should().Be(AccountStatus.Ready);
        rig.Claude.Calls.Should().Contain("control:claude_oauth_callback");
    }

    [Fact]
    public async Task 遠端登入_授權碼錯了_回原因_這次登入結束()
    {
        using var rig = NewRig();
        var claude = rig.H.Claude2;
        var start = await rig.StartAsync(claude.Id, new LoginRequest());
        start.Milestone.Should().Be(RemoteLoginMilestone.UrlShown);
        await rig.Claude.WaitForAsync(calls => calls.Contains("control:claude_oauth_wait_for_completion"), Slow);

        var error = await rig.H.OnUi(() => rig.H.Accounts.SubmitClaudeLoginCodeAsync(claude.Id, "wrong-code"));

        error.Should().Be("授權碼不對或已經過期，請重新登入");
        await rig.WaitLoginEndedAsync(claude.Id);
        rig.State(claude.Id).Status.Should().Be(AccountStatus.NotLoggedIn);
        rig.Claude.Calls.Should().NotContain("login-done");
        (await rig.H.OnUi(() => rig.H.Accounts.SubmitClaudeLoginCodeAsync(claude.Id, FakeClaudeLogin.GoodCode)))
            .Should().Be("這個帳戶沒有進行中的 Claude 登入，請重新登入", "這次登入已經結束");
    }

    [Fact]
    public async Task 授權碼的state和這次不同_不交給CLI()
    {
        using var rig = NewRig();
        var claude = rig.H.Claude2;
        await rig.StartAsync(claude.Id, new LoginRequest());
        await rig.Claude.WaitForAsync(calls => calls.Contains("control:claude_oauth_wait_for_completion"), Slow);

        var error = await rig.H.OnUi(() => rig.H.Accounts.SubmitClaudeLoginCodeAsync(claude.Id, FakeClaudeLogin.GoodCode + "#another-state"));

        error.Should().Be("授權碼的格式不對，請把登入頁上顯示的整串貼過來");
        rig.Claude.Calls.Should().NotContain("control:claude_oauth_callback");
        rig.State(claude.Id).LoginBusy.Should().BeTrue("還在等正確的授權碼");
        await rig.CancelAndWaitAsync(claude.Id);
    }

    [Fact]
    public async Task 沒有進行中的登入_貼授權碼回原因()
    {
        using var rig = NewRig();
        (await rig.H.OnUi(() => rig.H.Accounts.SubmitClaudeLoginCodeAsync(rig.H.Claude2.Id, FakeClaudeLogin.GoodCode)))
            .Should().Be("這個帳戶沒有進行中的 Claude 登入，請重新登入");
    }

    [Fact]
    public async Task CLI不支援app內登入_退回主控台()
    {
        using var rig = new RemoteLoginRig();   // 預設的假 claude：-p 直接結束（舊版 CLI、程序起不來）
        var claude = rig.H.Claude2;

        var start = await rig.StartAsync(claude.Id, new LoginRequest());

        start.Milestone.Should().Be(RemoteLoginMilestone.ConsoleOpened);
        await rig.Claude.WaitForAsync(calls => calls.Any(c => c.Contains("#auth login", StringComparison.Ordinal)), Slow);
        await rig.CancelAndWaitAsync(claude.Id);
    }
}
