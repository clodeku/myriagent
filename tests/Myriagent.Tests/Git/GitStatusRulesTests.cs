using Myriagent.Git;
using Trigger = Myriagent.Git.GitStatusRules.Trigger;

namespace Myriagent.Tests.Git;

/// <summary>對話工作資料夾的 git 狀態：解析 git 的輸出、決定比較基準與要問遠端的分支、什麼時候重讀、給網頁的 GitStatusView</summary>
public sealed class GitStatusRulesTests
{
    private const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha2 = "2222222222222222222222222222222222222222";
    private const string Sha3 = "3333333333333333333333333333333333333333";

    [Fact]
    public void 解析porcelain_v2_分支_上游_領先落後_stash_各種檔案()
    {
        var output = string.Join("\n",
            "# branch.oid " + Sha1,
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +2 -3",
            "# stash 4",
            "1 M. N... 100644 100644 100644 aaa bbb staged.txt",
            "1 .M N... 100644 100644 100644 aaa aaa unstaged.txt",
            "1 MM N... 100644 100644 100644 aaa bbb both.txt",
            "2 R. N... 100644 100644 100644 aaa aaa R100 new.txt\told.txt",
            "u UU N... 100644 100644 100644 100644 a b c conflict.txt",
            "? untracked.txt",
            "? \"引號 路徑.txt\"",
            "! ignored.txt",
            "");
        var s = GitStatusRules.ParseStatus(output);
        s.Should().Be(new GitPorcelainStatus(Sha1, "main", "origin/main", 2, 3, Changed: 4, Staged: 3, Unstaged: 2, Untracked: 2, Conflicts: 1, Stashes: 4));
    }

    [Fact]
    public void 解析porcelain_v2_分離的HEAD_還沒有提交_沒有上游_CRLF()
    {
        GitStatusRules.ParseStatus("# branch.oid (initial)\r\n# branch.head main\r\n")
            .Should().Be(new GitPorcelainStatus(null, "main", null, null, null, 0, 0, 0, 0, 0, 0));
        GitStatusRules.ParseStatus("# branch.oid " + Sha1 + "\n# branch.head (detached)\n")
            .Should().Be(new GitPorcelainStatus(Sha1, null, null, null, null, 0, 0, 0, 0, 0, 0));
        // 上游設了但不在了：沒有 branch.ab
        var gone = GitStatusRules.ParseStatus("# branch.oid " + Sha1 + "\n# branch.head feat\n# branch.upstream origin/feat\n");
        gone.Upstream.Should().Be("origin/feat");
        gone.Ahead.Should().BeNull();
        GitStatusRules.ParseStatus(null).Changed.Should().Be(0);
        GitStatusRules.ParseStatus("# branch.ab +x -1\n# stash -2\n").Should().Be(new GitPorcelainStatus(null, null, null, null, null, 0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void rev_parse_兩行或三行才算_路徑換成Windows格式_共用資料夾的相對路徑以執行的資料夾為準()
    {
        GitStatusRules.ParseRevParse("C:/repo/proj\nC:/repo/proj/.git\n").Should().Be((@"C:\repo\proj", @"C:\repo\proj\.git", @"C:\repo\proj\.git"));
        GitStatusRules.ParseRevParse("C:/repo/proj\nC:/repo/proj/.git\n.git\n", @"C:\repo\proj")
            .Should().Be((@"C:\repo\proj", @"C:\repo\proj\.git", @"C:\repo\proj\.git"));
        GitStatusRules.ParseRevParse("C:/repo/proj\nC:/repo/proj/.git\n../../.git\n", @"C:\repo\proj\src\app")!.Value.CommonDir.Should().Be(@"C:\repo\proj\.git");
        GitStatusRules.ParseRevParse("C:/repo/wt\nC:/repo/proj/.git/worktrees/wt\nC:/repo/proj/.git\n", @"C:\repo\wt")
            .Should().Be((@"C:\repo\wt", @"C:\repo\proj\.git\worktrees\wt", @"C:\repo\proj\.git"), "worktree 的共用資料夾是主 repo 的 .git");
        GitStatusRules.ParseRevParse("C:/repo/proj/.git\n").Should().BeNull("在 .git 裡面沒有最上層");
        GitStatusRules.ParseRevParse("").Should().BeNull();
        GitStatusRules.ParseRevParse(null).Should().BeNull();
    }

    [Fact]
    public void for_each_ref_解析_缺欄位當成沒有()
    {
        var refs = GitStatusRules.ParseRefs(
            $"refs/heads/main\t{Sha1}\t\torigin\trefs/heads/main\n" +
            $"refs/remotes/origin/HEAD\t{Sha2}\trefs/remotes/origin/main\t\t\r\n" +
            $"refs/remotes/origin/main\t{Sha2}\n" +
            "壞掉的一行\n");
        refs.Should().Equal(
            new GitRefInfo("refs/heads/main", Sha1, null, "origin", "refs/heads/main"),
            new GitRefInfo("refs/remotes/origin/HEAD", Sha2, "refs/remotes/origin/main", null, null),
            new GitRefInfo("refs/remotes/origin/main", Sha2, null, null, null));
    }

    [Fact]
    public void 要列的ref_目前分支_上游_各遠端的預設分支()
    {
        GitStatusRules.RefPatterns(new GitPorcelainStatus(Sha1, "feat/x", "origin/feat/x", 0, 0, 0, 0, 0, 0, 0, 0))
            .Should().Equal("refs/heads/feat/x", "refs/remotes/origin/feat/x", "refs/remotes/*/HEAD", "refs/remotes/*/main", "refs/remotes/*/master");
        GitStatusRules.RefPatterns(new GitPorcelainStatus(Sha1, null, null, null, null, 0, 0, 0, 0, 0, 0))
            .Should().Equal("refs/remotes/*/HEAD", "refs/remotes/*/main", "refs/remotes/*/master");
    }

    private static GitPorcelainStatus Status(string? branch, string? upstream, int? ahead = 0)
        => new(Sha1, branch, upstream, upstream != null ? ahead : null, upstream != null && ahead != null ? 0 : null, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void 追蹤預設分支_不另外比_問上游()
    {
        var refs = new[]
        {
            new GitRefInfo("refs/heads/main", Sha1, null, "origin", "refs/heads/main"),
            new GitRefInfo("refs/remotes/origin/HEAD", Sha2, "refs/remotes/origin/main", null, null),
            new GitRefInfo("refs/remotes/origin/main", Sha2, null, null, null),
        };
        var (baseRef, target) = GitStatusRules.PlanCompare(Status("main", "origin/main"), refs);
        baseRef.Should().BeNull("上游就是預設分支");
        target.Should().Be(new GitRemoteTarget("origin", "refs/heads/main", Sha2));
    }

    [Fact]
    public void 功能分支_和預設分支比_問上游()
    {
        var refs = new[]
        {
            new GitRefInfo("refs/heads/feat", Sha1, null, "origin", "refs/heads/feat"),
            new GitRefInfo("refs/remotes/origin/feat", Sha3, null, null, null),
            new GitRefInfo("refs/remotes/origin/HEAD", Sha2, "refs/remotes/origin/main", null, null),
            new GitRefInfo("refs/remotes/origin/main", Sha2, null, null, null),
        };
        var (baseRef, target) = GitStatusRules.PlanCompare(Status("feat", "origin/feat"), refs);
        baseRef.Should().Be(new GitBaseRef("origin/main", "refs/remotes/origin/main"));
        target.Should().Be(new GitRemoteTarget("origin", "refs/heads/feat", Sha3));
    }

    [Fact]
    public void 分離的HEAD_沒有遠端HEAD時用main_問預設分支()
    {
        var refs = new[]
        {
            new GitRefInfo("refs/remotes/upstream/main", Sha3, null, null, null),
            new GitRefInfo("refs/remotes/origin/master", Sha2, null, null, null),
            new GitRefInfo("refs/remotes/origin/main", Sha2, null, null, null),
        };
        var (baseRef, target) = GitStatusRules.PlanCompare(Status(null, null), refs);
        baseRef.Should().Be(new GitBaseRef("origin/main", "refs/remotes/origin/main"), "有 origin 就用 origin、main 優先於 master");
        target.Should().Be(new GitRemoteTarget("origin", "refs/heads/main", Sha2));
    }

    [Fact]
    public void 遠端HEAD指向沒列出的分支_用HEAD的提交()
    {
        var refs = new[] { new GitRefInfo("refs/remotes/origin/HEAD", Sha2, "refs/remotes/origin/develop", null, null) };
        var (baseRef, target) = GitStatusRules.PlanCompare(Status("local", null), refs);
        baseRef.Should().Be(new GitBaseRef("origin/develop", "refs/remotes/origin/develop"));
        target.Should().Be(new GitRemoteTarget("origin", "refs/heads/develop", Sha2));
    }

    [Fact]
    public void 只有一個不叫origin的遠端_用它_沒有遠端時都是null()
    {
        var refs = new[] { new GitRefInfo("refs/remotes/gh/master", Sha2, null, null, null) };
        GitStatusRules.PlanCompare(Status(null, null), refs).Base.Should().Be(new GitBaseRef("gh/master", "refs/remotes/gh/master"));
        GitStatusRules.PlanCompare(Status("main", null), []).Should().Be(((GitBaseRef?)null, (GitRemoteTarget?)null));
    }

    [Fact]
    public void 上游已刪除_比預設分支_不問遠端()
    {
        var refs = new[]
        {
            new GitRefInfo("refs/heads/feat", Sha1, null, "origin", "refs/heads/feat"),
            new GitRefInfo("refs/remotes/origin/main", Sha2, null, null, null),
        };
        var (baseRef, target) = GitStatusRules.PlanCompare(Status("feat", "origin/feat", ahead: null), refs);
        baseRef!.Name.Should().Be("origin/main");
        target.Should().BeNull("上游已刪除時不問（問預設分支會把它當成上游）");
    }

    [Fact]
    public void 上游是本機分支_不問遠端()
    {
        var refs = new[]
        {
            new GitRefInfo("refs/heads/feat", Sha1, null, ".", "refs/heads/main"),
            new GitRefInfo("refs/remotes/origin/main", Sha2, null, null, null),
        };
        var (baseRef, target) = GitStatusRules.PlanCompare(Status("feat", "main"), refs);
        baseRef!.Name.Should().Be("origin/main");
        target.Should().BeNull();
    }

    [Fact]
    public void rev_list與ls_remote的輸出()
    {
        GitStatusRules.ParseLeftRight("3\t1\n").Should().Be((3, 1));
        GitStatusRules.ParseLeftRight("bad").Should().BeNull();
        GitStatusRules.ParseLsRemote($"{Sha2}\trefs/heads/mainline\n{Sha3}\trefs/heads/main\r\n", "refs/heads/main").Should().Be(Sha3);
        GitStatusRules.ParseLsRemote("", "refs/heads/main").Should().BeNull();
    }

    [Fact]
    public void 進行中的操作_看git資料夾的標記()
    {
        var gitDir = @"C:\r\.git";
        string? Op(params string[] present)
        {
            var set = present.Select(p => Path.Combine(gitDir, p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return GitStatusRules.OperationOf(gitDir, set.Contains, set.Contains);
        }
        Op().Should().BeNull();
        Op("MERGE_HEAD").Should().Be("merge");
        Op("rebase-merge").Should().Be("rebase");
        Op("rebase-apply", "MERGE_HEAD").Should().Be("rebase");
        Op("CHERRY_PICK_HEAD").Should().Be("cherryPick");
        Op("REVERT_HEAD").Should().Be("revert");
        Op("BISECT_LOG").Should().Be("bisect");
    }

    [Fact]
    public void 資料夾_只認本機磁碟_鍵是完整路徑()
    {
        GitStatusRules.FolderKey(@"C:\repo\proj\").Should().Be(@"C:\repo\proj");
        GitStatusRules.FolderKey("C:/repo/proj").Should().Be(@"C:\repo\proj");
        GitStatusRules.FolderKey(@"D:\").Should().Be(@"D:\");
        GitStatusRules.FolderKey(@"\\server\share\proj").Should().BeNull("UNC 一律不碰");
        GitStatusRules.FolderKey(@"relative\path").Should().BeNull();
        GitStatusRules.FolderKey(null).Should().BeNull();
    }

    [Fact]
    public void 找git_只認PATH裡的完整路徑_再找常見位置()
    {
        var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Tools\Git\cmd\git.exe", @"C:\Fallback\git.exe", @"bin\git.exe", @"\\srv\git\git.exe" };
        GitStatusRules.FindGit(@"bin;\\srv\git;""C:\Tools\Git\cmd"";C:\Other", [@"C:\Fallback\git.exe"], exists.Contains).Should().Be(@"C:\Tools\Git\cmd\git.exe");
        GitStatusRules.FindGit(@"bin;C:\Other", [@"C:\Nope\git.exe", @"C:\Fallback\git.exe"], exists.Contains).Should().Be(@"C:\Fallback\git.exe", "相對路徑不認");
        GitStatusRules.FindGit(null, [], exists.Contains).Should().BeNull();
    }

    [Fact]
    public void 什麼時候讀本機()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        GitStatusRules.ShouldReadLocal(Trigger.Open, null, false, null, now).Should().BeTrue("沒讀過");
        GitStatusRules.ShouldReadLocal(Trigger.Open, now.AddSeconds(-4), true, null, now).Should().BeFalse();
        GitStatusRules.ShouldReadLocal(Trigger.Open, now.AddSeconds(-5), true, null, now).Should().BeTrue();
        GitStatusRules.ShouldReadLocal(Trigger.Periodic, now.AddSeconds(-20), true, null, now).Should().BeFalse();
        GitStatusRules.ShouldReadLocal(Trigger.Periodic, now.AddSeconds(-25), true, null, now).Should().BeTrue();
        GitStatusRules.ShouldReadLocal(Trigger.Periodic, now.AddSeconds(-60), false, null, now).Should().BeFalse("不是 repo 的 2 分鐘才重查");
        GitStatusRules.ShouldReadLocal(Trigger.Periodic, now.AddMinutes(-2), false, null, now).Should().BeTrue();
        GitStatusRules.ShouldReadLocal(Trigger.Changed, now, true, null, now).Should().BeTrue();
        // 讀失敗之後：只有使用者要求才讀
        var retry = now.AddMinutes(1);
        GitStatusRules.ShouldReadLocal(Trigger.Changed, now.AddMinutes(-5), true, retry, now).Should().BeFalse();
        GitStatusRules.ShouldReadLocal(Trigger.Periodic, now.AddMinutes(-5), true, retry, now).Should().BeFalse();
        GitStatusRules.ShouldReadLocal(Trigger.User, now, true, retry, now).Should().BeTrue();
        GitStatusRules.ShouldReadLocal(Trigger.Periodic, now.AddMinutes(-5), true, now, now).Should().BeTrue("到了就照常");
    }

    [Fact]
    public void 什麼時候問遠端()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        GitStatusRules.ShouldCheckRemote(Trigger.Open, null, now).Should().BeTrue();
        GitStatusRules.ShouldCheckRemote(Trigger.Changed, now.AddMinutes(-4), now).Should().BeFalse();
        GitStatusRules.ShouldCheckRemote(Trigger.Periodic, now.AddMinutes(-5), now).Should().BeTrue();
        GitStatusRules.ShouldCheckRemote(Trigger.User, now, now).Should().BeTrue();
    }

    [Fact]
    public void 遠端有新提交_用這台目前記著的比()
    {
        var target = new GitRemoteTarget("origin", "refs/heads/main", Sha1);
        var at = DateTime.UtcNow;
        GitStatusRules.RemoteHasNew(target, null).Should().BeNull();
        GitStatusRules.RemoteHasNew(target, new GitRemoteSnapshot(target, Sha1, at)).Should().BeFalse();
        GitStatusRules.RemoteHasNew(target, new GitRemoteSnapshot(target, Sha2, at)).Should().BeTrue();
        // fetch 之後這台記著的跟上了：不必再問就變回 false
        GitStatusRules.RemoteHasNew(target with { LocalSha = Sha2 }, new GitRemoteSnapshot(target, Sha2, at)).Should().BeFalse();
        GitStatusRules.RemoteHasNew(target with { Ref = "refs/heads/feat" }, new GitRemoteSnapshot(target, Sha2, at)).Should().BeNull("換了分支，舊的結果不算");
        GitStatusRules.RemoteHasNew(target, new GitRemoteSnapshot(target, null, at)).Should().BeNull("遠端沒有這個分支");
    }

    [Fact]
    public void GitStatusView()
    {
        var at = new DateTime(2026, 10, 23, 4, 5, 6, DateTimeKind.Utc);
        var target = new GitRemoteTarget("origin", "refs/heads/feat", Sha3);
        var status = new GitRepoStatus
        {
            Root = @"C:\repo\proj",
            Porcelain = new GitPorcelainStatus(Sha1, "feat", "origin/feat", 1, 2, 3, 1, 2, 4, 0, 1),
            Operation = "merge",
            Base = new GitBaseRef("origin/main", "refs/remotes/origin/main"),
            BaseAhead = 5,
            BaseBehind = 6,
            RemoteTarget = target,
            CheckedAt = at,
        };
        var view = GitStatusRules.Render(status, new GitRemoteSnapshot(target, Sha2, at.AddMinutes(-1)));
        view.Value<string>("root").Should().Be(@"C:\repo\proj");
        view.Value<string>("branch").Should().Be("feat");
        view.Value<string>("head").Should().Be("1111111");
        view.Value<string>("upstream").Should().Be("origin/feat");
        view.Value<bool>("upstreamGone").Should().BeFalse();
        view.Value<int>("ahead").Should().Be(1);
        view.Value<int>("behind").Should().Be(2);
        view.Value<string>("base").Should().Be("origin/main");
        view.Value<int>("baseAhead").Should().Be(5);
        view.Value<int>("baseBehind").Should().Be(6);
        view.Value<int>("changed").Should().Be(3);
        view.Value<int>("staged").Should().Be(1);
        view.Value<int>("unstaged").Should().Be(2);
        view.Value<int>("untracked").Should().Be(4);
        view.Value<int>("conflicts").Should().Be(0);
        view.Value<int>("stashes").Should().Be(1);
        view.Value<string>("operation").Should().Be("merge");
        view.Value<string>("remote").Should().Be("origin");
        view.Value<bool?>("remoteNew").Should().BeTrue();
        view.Value<long>("remoteCheckedAt").Should().Be(new DateTimeOffset(at.AddMinutes(-1)).ToUnixTimeMilliseconds());
        view.Value<long>("checkedAt").Should().Be(new DateTimeOffset(at).ToUnixTimeMilliseconds());

        var bare = GitStatusRules.Render(status with { Porcelain = new GitPorcelainStatus(null, "main", "origin/main", null, null, 0, 0, 0, 0, 0, 0), RemoteTarget = null, Base = null, BaseAhead = null, BaseBehind = null, Operation = null }, null);
        bare.Value<string>("head").Should().BeNull();
        bare.Value<bool>("upstreamGone").Should().BeTrue("設了上游卻沒有領先落後");
        bare.Value<bool?>("remoteNew").Should().BeNull();
        bare.Value<long?>("remoteCheckedAt").Should().BeNull();
        bare.Value<string>("remote").Should().BeNull();
        bare.ToString(Newtonsoft.Json.Formatting.None).Should().Contain("\"head\":null");
    }
}
