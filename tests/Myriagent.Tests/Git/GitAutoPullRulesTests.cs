using Myriagent.Git;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Git;

/// <summary>
/// 自動拉取（GitAutoPullRules）：有上游的分支和上游比、分離的 HEAD 和預設分支比；只快轉，遠端有新提交時先 fetch 那一個追蹤分支；
/// 進行中的操作、衝突、不能快轉、未提交、對話執行中不拉並寫出原因；失敗之後的冷卻、面板的說明
/// </summary>
public sealed class GitAutoPullRulesTests
{
    private const string Head = "1111111111111111111111111111111111111111";
    private const string Tracked = "2222222222222222222222222222222222222222";
    private const string Remote = "3333333333333333333333333333333333333333";
    private static readonly DateTime Now = new(2026, 10, 23, 3, 40, 0, DateTimeKind.Utc);

    private static GitRepoStatus OnBranch(int ahead = 0, int behind = 0, int changed = 0, int untracked = 0, int conflicts = 0, string? operation = null) => new()
    {
        Root = @"C:\repo\proj",
        Porcelain = new GitPorcelainStatus(Head, "main", "origin/main", ahead, behind, changed, 0, changed, untracked, conflicts, 0),
        Operation = operation,
        RemoteTarget = new GitRemoteTarget("origin", "refs/heads/main", Tracked),
        CheckedAt = Now,
    };

    private static GitRepoStatus Detached(int baseAhead = 0, int baseBehind = 1, int untracked = 1) => new()
    {
        Root = @"C:\repo\myriagent",
        Porcelain = new GitPorcelainStatus(Head, null, null, null, null, 0, 0, 0, untracked, 0, 0),
        Base = new GitBaseRef("origin/main", "refs/remotes/origin/main"),
        BaseAhead = baseAhead,
        BaseBehind = baseBehind,
        RemoteTarget = new GitRemoteTarget("origin", "refs/heads/main", Tracked),
        CheckedAt = Now,
    };

    private static GitRemoteSnapshot RemoteAt(GitRepoStatus s, string sha) => new(s.RemoteTarget!, sha, Now);

    [Fact]
    public void 分支落後上游而且乾淨_快轉到上游_不必fetch()
    {
        var d = GitAutoPullRules.Decide(OnBranch(behind: 2), null, agentRunning: false);
        d.SkipReason.Should().BeNull();
        d.Plan.Should().Be(new GitPullPlan("refs/remotes/origin/main", "origin/main", null, Head, 2));
    }

    [Fact]
    public void 遠端有還沒下載的提交_先fetch那一個追蹤分支()
    {
        var s = OnBranch();
        var d = GitAutoPullRules.Decide(s, RemoteAt(s, Remote), agentRunning: false);
        d.Plan!.Fetch.Should().Be(s.RemoteTarget);
        GitAutoPullRules.Refspec(d.Plan).Should().Be("+refs/heads/main:refs/remotes/origin/main");

        GitAutoPullRules.Decide(s, RemoteAt(s, Tracked), agentRunning: false).Should().Be(GitPullDecision.Nothing, "遠端和這台記著的一樣：沒有要拉的");
    }

    [Fact]
    public void 分離的HEAD_落後預設分支_快轉到預設分支_未追蹤的檔案不擋()
    {
        var s = Detached();
        var d = GitAutoPullRules.Decide(s, RemoteAt(s, Remote), agentRunning: false);
        d.Plan.Should().Be(new GitPullPlan("refs/remotes/origin/main", "origin/main", s.RemoteTarget, Head, 1));
    }

    [Fact]
    public void 沒有要拉的_或沒有可以比的分支_什麼都不說()
    {
        GitAutoPullRules.Decide(OnBranch(), null, false).Should().Be(GitPullDecision.Nothing);
        GitAutoPullRules.Decide(OnBranch(changed: 3, ahead: 2), null, false).Should().Be(GitPullDecision.Nothing, "有變更、有未推送，但沒有要拉的");
        var noUpstream = OnBranch(behind: 1) with { Porcelain = new GitPorcelainStatus(Head, "feat", null, null, null, 0, 0, 0, 0, 0, 0) };
        GitAutoPullRules.Decide(noUpstream, null, false).Should().Be(GitPullDecision.Nothing, "沒有上游的分支不知道要從哪裡拉");
        var gone = OnBranch() with { Porcelain = new GitPorcelainStatus(Head, "main", "origin/main", null, null, 0, 0, 0, 0, 0, 0) };
        GitAutoPullRules.Decide(gone, null, false).Should().Be(GitPullDecision.Nothing, "上游已刪除");
        var noCommit = OnBranch(behind: 1) with { Porcelain = new GitPorcelainStatus(null, "main", "origin/main", 0, 1, 0, 0, 0, 0, 0, 0) };
        GitAutoPullRules.Decide(noCommit, null, false).Should().Be(GitPullDecision.Nothing);
        var otherTarget = OnBranch(behind: 1) with { RemoteTarget = new GitRemoteTarget("origin", "refs/heads/dev", Tracked) };
        GitAutoPullRules.Decide(otherTarget, null, false).Should().Be(GitPullDecision.Nothing, "問遠端的分支不是上游：不拉");
        GitAutoPullRules.Decide(Detached() with { Base = null, BaseAhead = null, BaseBehind = null }, null, false).Should().Be(GitPullDecision.Nothing);
    }

    [Theory]
    [InlineData("op", "有進行中的rebase還沒完成")]
    [InlineData("conflict", "有 1 個檔案的衝突還沒解決")]
    [InlineData("ahead", "這裡有 2 個提交不在 origin/main，不能快轉")]
    [InlineData("changed", "有 3 個已追蹤的檔案還沒提交")]
    [InlineData("agent", "這個資料夾有對話正在執行，結束後再拉")]
    public void 有提交可拉但不安全_不拉_寫出原因(string why, string reason)
    {
        var s = why switch
        {
            "op" => OnBranch(behind: 1, operation: "rebase"),
            "conflict" => OnBranch(behind: 1, conflicts: 1),
            "ahead" => OnBranch(behind: 1, ahead: 2),
            "changed" => OnBranch(behind: 1, changed: 3),
            _ => OnBranch(behind: 1),
        };
        var d = GitAutoPullRules.Decide(s, null, agentRunning: why == "agent");
        d.Plan.Should().BeNull();
        d.SkipReason.Should().Be(reason);
        GitAutoPullRules.Skipped(reason, Now).Text.Should().Be("這次不拉：" + reason);
    }

    [Fact]
    public void 分離的HEAD有自己的提交_不能快轉()
        => GitAutoPullRules.Decide(Detached(baseAhead: 1), null, false).SkipReason.Should().Be("這裡有 1 個提交不在 origin/main，不能快轉");

    [Fact]
    public void 失敗之後冷卻15分鐘_成功之後隔1分鐘_使用者要求一律可以()
    {
        GitAutoPullRules.CanAttempt(null, false, false, Now).Should().BeTrue();
        GitAutoPullRules.CanAttempt(Now.AddSeconds(-59), false, false, Now).Should().BeFalse();
        GitAutoPullRules.CanAttempt(Now.AddMinutes(-1), false, false, Now).Should().BeTrue();
        GitAutoPullRules.CanAttempt(Now.AddMinutes(-14), true, false, Now).Should().BeFalse();
        GitAutoPullRules.CanAttempt(Now.AddMinutes(-15), true, false, Now).Should().BeTrue();
        GitAutoPullRules.CanAttempt(Now, true, userRequested: true, Now).Should().BeTrue();
    }

    [Fact]
    public void 使用者的要求2分鐘內有效()
    {
        GitAutoPullRules.UserRequested(null, Now).Should().BeFalse();
        GitAutoPullRules.UserRequested(Now, Now).Should().BeTrue();
        GitAutoPullRules.UserRequested(Now.AddSeconds(-119), Now).Should().BeTrue();
        GitAutoPullRules.UserRequested(Now.AddMinutes(-2), Now).Should().BeFalse();
        GitAutoPullRules.UserRequested(Now.AddMinutes(1), Now).Should().BeFalse("時鐘往回調：不算");
    }

    [Theory]
    [InlineData(@"C:\repo\proj", @"C:\repo\proj", true)]
    [InlineData(@"c:\REPO\proj\src\app", @"C:\repo\proj", true)]
    [InlineData(@"C:\repo\proj\", @"C:\repo\proj", true)]
    [InlineData(@"C:\repo\proj2", @"C:\repo\proj", false)]
    [InlineData(@"C:\repo", @"C:\repo\proj", false)]
    public void 資料夾在不在repo裡(string folder, string root, bool inside)
        => GitAutoPullRules.IsInside(folder, root).Should().Be(inside);

    [Fact]
    public void 結果的說明_快轉0個不顯示()
    {
        var plan = new GitPullPlan("refs/remotes/origin/main", "origin/main", null, Head, 1);
        GitAutoPullRules.ViewOf(GitPullResult.Done(3), plan, Now)!.Text.Should().Be("已從 origin/main 快轉拉取 3 個提交");
        GitAutoPullRules.ViewOf(GitPullResult.Done(0), plan, Now).Should().BeNull();
        GitAutoPullRules.ViewOf(GitPullResult.Skip("HEAD 在檢查之後變了"), plan, Now)!.Text.Should().Be("這次不拉：HEAD 在檢查之後變了");
        GitAutoPullRules.ViewOf(GitPullResult.Fail("fatal: x"), plan, Now)!.Should().Be(new GitAutoPullView(GitAutoPullState.Failed, "失敗：fatal: x", Now));
        GitAutoPullRules.Pulling(plan, Now).Text.Should().Be("正在從 origin/main 拉取…");
        GitAutoPullRules.SameView(GitAutoPullRules.Skipped("a", Now), GitAutoPullRules.Skipped("a", Now.AddMinutes(5))).Should().BeTrue("不看時間");
        GitAutoPullRules.SameView(GitAutoPullRules.Skipped("a", Now), GitAutoPullRules.Skipped("b", Now)).Should().BeFalse();
        GitAutoPullRules.SameView(null, null).Should().BeTrue();
    }

    [Fact]
    public void git的錯誤取一行_遮掉網址裡的帳密()
    {
        GitAutoPullRules.ErrorLine("remote: x\nfatal: unable to access 'https://user:ghp_secret@github.com/a/b.git/': Could not resolve host\n", "預設")
            .Should().Be("fatal: unable to access 'https://***@github.com/a/b.git/': Could not resolve host");
        GitAutoPullRules.ErrorLine("error: Your local changes would be overwritten by merge:\n\ta.txt\nAborting\n", "預設")
            .Should().Be("error: Your local changes would be overwritten by merge:");
        GitAutoPullRules.ErrorLine("  \n", "預設").Should().Be("預設");
        GitAutoPullRules.ErrorLine(new string('x', 300), "預設").Should().HaveLength(201);
    }

    [Fact]
    public void GitStatusView帶autoPull_沒有是null()
    {
        GitStatusRules.Render(OnBranch(), null)["autoPull"]!.Type.Should().Be(JTokenType.Null);
        var view = GitStatusRules.Render(OnBranch(), null, new GitAutoPullView(GitAutoPullState.Pulled, "已從 origin/main 快轉拉取 1 個提交", Now));
        var ap = (JObject)view["autoPull"]!;
        ap.Value<string>("state").Should().Be("pulled");
        ap.Value<string>("text").Should().Be("已從 origin/main 快轉拉取 1 個提交");
        ap.Value<long>("at").Should().Be(new DateTimeOffset(Now).ToUnixTimeMilliseconds());
    }
}
