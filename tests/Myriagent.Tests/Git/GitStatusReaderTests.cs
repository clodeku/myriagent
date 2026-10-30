using System.Diagnostics;
using Myriagent.Git;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Git;

/// <summary>
/// 以這台的 git 讀暫存資料夾裡的 repo（本機的裸 repo 當遠端，不連網路）：未提交、未推送、未拉取、遠端有新提交、分離的 HEAD、子資料夾、
/// 合併衝突、還沒有提交、不是 repo。準備 repo 的 git 指令不讀使用者的 git 設定（GIT_CONFIG_GLOBAL 指到空檔）
/// </summary>
public sealed class GitStatusReaderTests : IDisposable
{
    private readonly TempDirectory tmp = new("git");
    private readonly GitStatusReader reader = new();
    private readonly string emptyConfig;

    public GitStatusReaderTests()
    {
        emptyConfig = tmp.WriteFile("empty.gitconfig", string.Empty);
    }

    public void Dispose()
    {
        try
        {
            // .git 裡的物件檔是唯讀的，先清掉屬性才刪得掉
            foreach (var f in Directory.EnumerateFiles(tmp.Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
        }
        catch (IOException)
        {
        }
        tmp.Dispose();
    }

    private string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.test", "-c", "init.defaultBranch=main" }.Concat(args))
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = emptyConfig;
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit(30_000).Should().BeTrue($"git {string.Join(' ', args)} 要在 30 秒內結束");
        return p.ExitCode == 0 ? stdout.Result : throw new InvalidOperationException($"git {string.Join(' ', args)} 失敗（{p.ExitCode}）：{stderr.Result}");
    }

    private void Commit(string dir, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(dir, file), content);
        Git(dir, "add", file);
        Git(dir, "commit", "--no-verify", "-m", message);
    }

    private string Head(string dir) => Git(dir, "rev-parse", "HEAD").Trim();

    /// <summary>裸 repo（遠端）＋已推送一個提交，再複製出 work（遠端/HEAD 指向 main）</summary>
    private (string Origin, string Work) CreateRemoteAndClone()
    {
        var origin = tmp.Combine("origin.git");
        Git(tmp.Root, "init", "--bare", "origin.git");
        var seed = tmp.Combine("seed");
        Git(tmp.Root, "clone", origin, "seed");
        Commit(seed, "a.txt", "a\n", "first");
        Git(seed, "push", "--no-verify", "origin", "main");
        Git(tmp.Root, "clone", origin, "work");
        return (origin, tmp.Combine("work"));
    }

    [Fact]
    public async Task 乾淨的clone_追蹤預設分支_問遠端沒有新提交()
    {
        var (_, work) = CreateRemoteAndClone();
        var s = (await reader.ReadAsync(work, CancellationToken.None))!;
        s.Should().NotBeNull();
        s.Root.Should().BeEquivalentTo(Path.GetFullPath(work));
        s.Porcelain.Should().Be(new GitPorcelainStatus(Head(work), "main", "origin/main", 0, 0, 0, 0, 0, 0, 0, 0));
        s.Operation.Should().BeNull();
        s.Base.Should().BeNull("上游就是預設分支");
        s.RemoteTarget.Should().Be(new GitRemoteTarget("origin", "refs/heads/main", Head(work)));

        var remote = await reader.CheckRemoteAsync(work, s.RemoteTarget!, CancellationToken.None);
        remote.Sha.Should().Be(Head(work));
        GitStatusRules.RemoteHasNew(s.RemoteTarget, remote).Should().BeFalse();
    }

    [Fact]
    public async Task 子資料夾與worktree_共用資料夾相同_問遠端的鍵相同()
    {
        var (_, work) = CreateRemoteAndClone();
        var sub = Path.Combine(work, "src", "app");
        Directory.CreateDirectory(sub);
        var tree = tmp.Combine("work-tree");
        Git(work, "worktree", "add", "--detach", tree);

        var top = (await reader.ReadAsync(work, CancellationToken.None))!;
        var inSub = (await reader.ReadAsync(sub, CancellationToken.None))!;
        var inTree = (await reader.ReadAsync(tree, CancellationToken.None))!;

        var common = Path.GetFullPath(Path.Combine(work, ".git"));
        top.CommonDir.Should().BeEquivalentTo(common);
        inSub.CommonDir.Should().BeEquivalentTo(common, "子資料夾回的共用資料夾是相對路徑，以執行的資料夾換成完整路徑");
        inTree.CommonDir.Should().BeEquivalentTo(common, "worktree 的共用資料夾是主 repo 的 .git");
        inTree.Root.Should().BeEquivalentTo(Path.GetFullPath(tree));
        GitStatusRules.RemoteKey(inSub).Should().BeEquivalentTo(GitStatusRules.RemoteKey(top));
        GitStatusRules.RemoteKey(inTree).Should().BeEquivalentTo(GitStatusRules.RemoteKey(top), "分離的 HEAD 問的也是 origin 的 main");
    }

    [Fact]
    public async Task 未提交_未推送_遠端有新提交_fetch之後變成未拉取()
    {
        var (origin, work) = CreateRemoteAndClone();
        // 別人推了一個提交
        Git(tmp.Root, "clone", origin, "other");
        var other = tmp.Combine("other");
        Commit(other, "o.txt", "o\n", "other");
        Git(other, "push", "--no-verify", "origin", "main");
        // 這裡提交一個（未推送），再改檔、暫存、新增
        Commit(work, "w.txt", "w\n", "local");
        File.WriteAllText(Path.Combine(work, "a.txt"), "changed\n");
        File.WriteAllText(Path.Combine(work, "staged.txt"), "s\n");
        Git(work, "add", "staged.txt");
        File.WriteAllText(Path.Combine(work, "new.txt"), "n\n");

        var s = (await reader.ReadAsync(work, CancellationToken.None))!;
        s.Porcelain.Ahead.Should().Be(1);
        s.Porcelain.Behind.Should().Be(0, "還沒 fetch");
        s.Porcelain.Changed.Should().Be(2);
        s.Porcelain.Staged.Should().Be(1);
        s.Porcelain.Unstaged.Should().Be(1);
        s.Porcelain.Untracked.Should().Be(1);

        var remote = await reader.CheckRemoteAsync(work, s.RemoteTarget!, CancellationToken.None);
        remote.Sha.Should().Be(Head(other));
        GitStatusRules.RemoteHasNew(s.RemoteTarget, remote).Should().BeTrue("遠端有這台還沒下載的提交");
        Git(work, "status", "--porcelain").Should().Contain("new.txt", "讀狀態不改工作樹");

        Git(work, "fetch", "origin");
        var after = (await reader.ReadAsync(work, CancellationToken.None))!;
        after.Porcelain.Behind.Should().Be(1);
        GitStatusRules.RemoteHasNew(after.RemoteTarget, remote).Should().BeFalse("fetch 過，不必再問就知道跟上了");

        // 子資料夾也認得同一個 repo
        Directory.CreateDirectory(Path.Combine(work, "sub", "deep"));
        var sub = (await reader.ReadAsync(Path.Combine(work, "sub", "deep"), CancellationToken.None))!;
        sub.Root.Should().BeEquivalentTo(Path.GetFullPath(work));
        sub.Porcelain.Untracked.Should().Be(1);
    }

    /// <summary>別人推了一個提交（other 的 HEAD）</summary>
    private string PushFromOther(string origin, string file = "o.txt")
    {
        var other = tmp.Combine("other");
        if (!Directory.Exists(other))
            Git(tmp.Root, "clone", origin, "other");
        Commit(other, file, "o\n", "other " + file);
        Git(other, "push", "--no-verify", "origin", "main");
        return Head(other);
    }

    /// <summary>照中樞的流程：讀狀態、問遠端、決定、拉取</summary>
    private async Task<(GitPullDecision Decision, GitPullResult? Result)> AutoPullAsync(string work)
    {
        var s = (await reader.ReadAsync(work, CancellationToken.None))!;
        var remote = await reader.CheckRemoteAsync(work, s.RemoteTarget!, CancellationToken.None);
        var decision = GitAutoPullRules.Decide(s, remote, agentRunning: false);
        return (decision, decision.Plan is { } plan ? await reader.FastForwardAsync(work, plan, CancellationToken.None) : null);
    }

    [Fact]
    public async Task 自動拉取_遠端有新提交_fetch並快轉_未追蹤的檔案留著()
    {
        var (origin, work) = CreateRemoteAndClone();
        var pushed = PushFromOther(origin);
        File.WriteAllText(Path.Combine(work, "mine.txt"), "untracked\n");

        var (decision, result) = await AutoPullAsync(work);

        decision.Plan!.Fetch.Should().NotBeNull("還沒 fetch：遠端有這台還沒下載的提交");
        result.Should().Be(GitPullResult.Done(1));
        Head(work).Should().Be(pushed);
        File.ReadAllText(Path.Combine(work, "mine.txt")).Should().Be("untracked\n");
        var after = (await reader.ReadAsync(work, CancellationToken.None))!;
        after.Porcelain.Behind.Should().Be(0);
        after.Porcelain.Untracked.Should().Be(1);
        Git(work, "log", "--format=%P", "-1").Trim().Split(' ').Should().ContainSingle("快轉，不產生合併提交");
    }

    [Fact]
    public async Task 自動拉取_分離的HEAD_快轉到預設分支()
    {
        var (origin, work) = CreateRemoteAndClone();
        Git(work, "checkout", "--detach");
        var pushed = PushFromOther(origin);

        var (decision, result) = await AutoPullAsync(work);

        decision.Plan!.TargetRef.Should().Be("refs/remotes/origin/main");
        result.Should().Be(GitPullResult.Done(1));
        Head(work).Should().Be(pushed);
        Git(work, "rev-parse", "--abbrev-ref", "HEAD").Trim().Should().Be("HEAD", "還是分離的 HEAD");
    }

    [Fact]
    public async Task 自動拉取_決定之後HEAD變了或有了未提交的變更_不拉()
    {
        var (origin, work) = CreateRemoteAndClone();
        PushFromOther(origin);
        var s = (await reader.ReadAsync(work, CancellationToken.None))!;
        var remote = await reader.CheckRemoteAsync(work, s.RemoteTarget!, CancellationToken.None);
        var plan = GitAutoPullRules.Decide(s, remote, false).Plan!;

        // 決定之後代理提交了一個
        Commit(work, "w.txt", "w\n", "local");
        var local = Head(work);
        (await reader.FastForwardAsync(work, plan, CancellationToken.None)).Should().Be(GitPullResult.Skip("HEAD 在檢查之後變了"));
        Head(work).Should().Be(local);

        // 撤掉那個提交，改成改了已追蹤的檔案（fetch 已經做過，這次只差快轉）
        Git(work, "reset", "--hard", plan.ExpectedHead);
        File.WriteAllText(Path.Combine(work, "a.txt"), "edited\n");
        (await reader.FastForwardAsync(work, plan, CancellationToken.None)).Should().Be(GitPullResult.Skip("有 1 個已追蹤的檔案還沒提交"));
        Head(work).Should().Be(plan.ExpectedHead);
        File.ReadAllText(Path.Combine(work, "a.txt")).Should().Be("edited\n");
    }

    [Fact]
    public async Task 自動拉取_未追蹤的檔案會被蓋掉_merge拒絕_回失敗什麼都沒改()
    {
        var (origin, work) = CreateRemoteAndClone();
        PushFromOther(origin, "clash.txt");
        File.WriteAllText(Path.Combine(work, "clash.txt"), "mine\n");
        var before = Head(work);

        var (_, result) = await AutoPullAsync(work);

        result!.State.Should().Be(GitAutoPullState.Failed);
        result.Detail.Should().StartWith("error:");
        Head(work).Should().Be(before);
        File.ReadAllText(Path.Combine(work, "clash.txt")).Should().Be("mine\n");
    }

    [Fact]
    public async Task 分離的HEAD_和預設分支比()
    {
        var (_, work) = CreateRemoteAndClone();
        Git(work, "checkout", "--detach");
        Commit(work, "d.txt", "d\n", "detached");
        var s = (await reader.ReadAsync(work, CancellationToken.None))!;
        s.Porcelain.Branch.Should().BeNull();
        s.Porcelain.Upstream.Should().BeNull();
        s.Base.Should().Be(new GitBaseRef("origin/main", "refs/remotes/origin/main"));
        s.BaseAhead.Should().Be(1);
        s.BaseBehind.Should().Be(0);
        s.RemoteTarget!.Ref.Should().Be("refs/heads/main");
    }

    [Fact]
    public async Task 功能分支_還沒推送過_和預設分支比()
    {
        var (_, work) = CreateRemoteAndClone();
        Git(work, "switch", "-c", "feat");
        Commit(work, "f.txt", "f\n", "feat 1");
        Commit(work, "g.txt", "g\n", "feat 2");
        var s = (await reader.ReadAsync(work, CancellationToken.None))!;
        s.Porcelain.Branch.Should().Be("feat");
        s.Porcelain.Upstream.Should().BeNull();
        s.Base!.Name.Should().Be("origin/main");
        s.BaseAhead.Should().Be(2);
    }

    [Fact]
    public async Task 合併衝突_進行中的合併()
    {
        var repo = tmp.CreateDirectory("conflict");
        Git(repo, "init");
        Commit(repo, "a.txt", "base\n", "base");
        Git(repo, "switch", "-c", "x");
        Commit(repo, "a.txt", "x\n", "x");
        Git(repo, "switch", "main");
        Commit(repo, "a.txt", "main\n", "main");
        var act = () => Git(repo, "merge", "--no-edit", "x");
        act.Should().Throw<InvalidOperationException>("有衝突");

        var s = (await reader.ReadAsync(repo, CancellationToken.None))!;
        s.Operation.Should().Be("merge");
        s.Porcelain.Conflicts.Should().Be(1);
        s.Base.Should().BeNull("沒有遠端");
        s.RemoteTarget.Should().BeNull();
    }

    [Fact]
    public async Task 還沒有提交()
    {
        var repo = tmp.CreateDirectory("fresh");
        Git(repo, "init");
        File.WriteAllText(Path.Combine(repo, "x.txt"), "x\n");
        var s = (await reader.ReadAsync(repo, CancellationToken.None))!;
        s.Porcelain.Oid.Should().BeNull();
        s.Porcelain.Branch.Should().Be("main");
        s.Porcelain.Untracked.Should().Be(1);
        GitStatusRules.Render(s, null).Value<string>("head").Should().BeNull();
    }

    [Fact]
    public async Task 不是repo_不存在_UNC_都是null()
    {
        (await reader.ReadAsync(tmp.CreateDirectory("plain"), CancellationToken.None)).Should().BeNull();
        (await reader.ReadAsync(tmp.Combine("missing"), CancellationToken.None)).Should().BeNull();
        (await reader.ReadAsync(@"\\localhost\c$\repo", CancellationToken.None)).Should().BeNull();
    }
}
