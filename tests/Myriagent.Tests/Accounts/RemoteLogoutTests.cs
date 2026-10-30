using System.Diagnostics;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Accounts;

/// <summary>
/// WP4.8 步 4：遠端登出回報真結果（AccountManager.LogoutForRemoteAsync，D16）——在帳戶自己的設定資料夾跑 CLI 的登出、最多 RemoteLogoutDeadline；
/// 本機登出（LogoutAsync）的行為不變。一律用假 app-server（FakeCodexAppServer 的 logout 子指令依 CODEX_HOME 留標記）與假 claude
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class RemoteLogoutTests
{
    private const int Slow = RemoteLoginRig.Slow;

    [Theory]
    [InlineData(0, 0, 20_000, nameof(RemoteLogoutResult.Done))]
    [InlineData(1, 0, 20_000, nameof(RemoteLogoutResult.Failed))]
    [InlineData(0, 5000, 1000, nameof(RemoteLogoutResult.TimedOut))]
    public async Task LogoutForRemoteAsync_結束碼0_Done_1_Failed_延遲超過期限_TimedOut(int exitCode, int delayMs, int deadlineMs, string expectedName)
    {
        var expected = Enum.Parse<RemoteLogoutResult>(expectedName);
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("ok", logoutExitCode: exitCode, logoutDelayMs: delayMs));
        rig.Fake.Complete();   // 假 app-server 回已登入（直到這個 CODEX_HOME 有登出標記）
        rig.H.Ui.Invoke(() =>
        {
            rig.H.Accounts.RemoteLogoutDeadline = TimeSpan.FromMilliseconds(deadlineMs);
            var state = rig.H.Accounts.GetState(rig.Codex);
            state.Status = AccountStatus.Ready;
            state.RemoteLoginNote = RemoteAccountRules.MismatchNote(false);
        });

        var watch = Stopwatch.StartNew();
        var result = await rig.H.OnUi(() => rig.H.Accounts.LogoutForRemoteAsync(rig.Codex)).WaitAsync(TimeSpan.FromMilliseconds(Slow));

        result.Should().Be(expected);
        if (expected == RemoteLogoutResult.TimedOut)
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "期限一到就回報（程序樹已結束）");
        var calls = rig.Fake.Calls.ToList();
        var logoutAt = calls.IndexOf("args=logout");
        logoutAt.Should().BeGreaterThanOrEqualTo(0);
        Path.GetFullPath(calls[logoutAt + 1]["codexHome=".Length..]).Should().BeEquivalentTo(Path.GetFullPath(rig.H.Codex.ConfigDirectory!), "在這個帳戶的資料夾登出");
        File.Exists(Path.Combine(rig.H.Codex.ConfigDirectory!, "fake-logged-out")).Should().BeTrue();
        rig.State(rig.Codex).RemoteLoginNote.Should().Be(expected == RemoteLogoutResult.Done ? null : RemoteAccountRules.MismatchNote(false),
            "登出成功才清掉說明");

        // 之後在背景重新查詢（不等）：這個 CODEX_HOME 有登出標記，假 app-server 回未登入
        await rig.H.WaitUntilAsync(() => rig.H.Accounts.GetState(rig.Codex).Status == AccountStatus.NotLoggedIn, "重新查詢後是未登入", Slow);
    }

    [Fact]
    public async Task LogoutForRemoteAsync_Claude_在同一個資料夾登出_Done()
    {
        using var rig = new RemoteLoginRig();
        var claude = rig.H.Claude2;
        Directory.CreateDirectory(claude.ConfigDirectory!);
        File.WriteAllText(Path.Combine(claude.ConfigDirectory!, ".credentials.json"), "{}");
        File.WriteAllText(Path.Combine(claude.ConfigDirectory!, ".claude.json"), "{}");

        var result = await rig.H.OnUi(() => rig.H.Accounts.LogoutForRemoteAsync(claude.Id)).WaitAsync(TimeSpan.FromMilliseconds(Slow));

        result.Should().Be(RemoteLogoutResult.Done);
        var logout = rig.Claude.Calls.Single(c => c.EndsWith("#auth logout", StringComparison.Ordinal));
        Path.GetFullPath(logout[..logout.IndexOf('#', StringComparison.Ordinal)]).Should().BeEquivalentTo(Path.GetFullPath(claude.ConfigDirectory!));
        File.Exists(Path.Combine(claude.ConfigDirectory!, ".credentials.json")).Should().BeFalse();
        File.Exists(Path.Combine(claude.ConfigDirectory!, ".claude.json")).Should().BeFalse();
    }

    [Fact]
    public async Task LogoutForRemoteAsync_帳戶不存在或找不到CLI_Failed_沒有執行()
    {
        using var rig = new RemoteLoginRig();
        (await rig.H.OnUi(() => rig.H.Accounts.LogoutForRemoteAsync("acc-missing"))).Should().Be(RemoteLogoutResult.Failed);
        rig.H.Ui.Invoke(() => rig.H.Accounts.ResolveLauncher = _ => null);
        (await rig.H.OnUi(() => rig.H.Accounts.LogoutForRemoteAsync(rig.Codex))).Should().Be(RemoteLogoutResult.Failed);
        rig.Fake.Started.Should().BeFalse();
    }

    [Fact]
    public async Task LogoutAsync_本機的行為不變_成功後清掉RemoteLoginNote()
    {
        using var rig = new RemoteLoginRig(new FakeCodexAppServer("ok"));
        rig.Fake.Complete();
        rig.H.Ui.Invoke(() =>
        {
            var state = rig.H.Accounts.GetState(rig.Codex);
            state.Status = AccountStatus.Ready;
            state.RemoteLoginNote = RemoteAccountRules.MismatchNote(false);
        });

        await rig.H.OnUi(() => rig.H.Accounts.LogoutAsync(rig.Codex)).WaitAsync(TimeSpan.FromMilliseconds(Slow));

        rig.Fake.Calls.Should().Contain("logout");
        rig.State(rig.Codex).RemoteLoginNote.Should().BeNull();
        rig.State(rig.Codex).Status.Should().Be(AccountStatus.NotLoggedIn, "LogoutAsync 等重新查詢完才返回");
        rig.State(rig.Codex).StatusText.Should().Be(AccountRules.DescribeStatus(AgentProvider.Codex, AccountStatus.NotLoggedIn), "說明清掉後回到預設文字");
    }

    [Fact]
    public async Task 登入開始時清掉RemoteLoginNote()
    {
        using var rig = new RemoteLoginRig();
        rig.H.Ui.Invoke(() => rig.H.Accounts.GetState(rig.Codex).RemoteLoginNote = RemoteAccountRules.MismatchNote(true));
        var start = await rig.StartAsync(rig.Codex, new LoginRequest(LoginMethod.DeviceCode, null, new RemoteLoginExpectation("codex:" + FakeCodexAppServer.Email, FakeCodexAppServer.Email, null)));
        start.Milestone.Should().Be(RemoteLoginMilestone.CodeShown);
        rig.State(rig.Codex).RemoteLoginNote.Should().BeNull("新的一次登入，上次的說明不再適用");
        await rig.CancelAndWaitAsync(rig.Codex);
    }
}
