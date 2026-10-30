using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;

namespace Myriagent.Git;

/// <summary>讀 git 狀態（中樞在背景執行緒呼叫；測試換成假的）</summary>
internal interface IGitStatusReader
{
    /// <summary>讀本機的狀態（不連網路）。不是 git repo、沒有 git、不是本機磁碟回 null；git 出錯或逾時丟 <see cref="GitReadException"/></summary>
    Task<GitRepoStatus?> ReadAsync(string folder, CancellationToken ct);

    /// <summary>問遠端這個分支現在的提交（<c>git ls-remote</c>，要連網路）。遠端沒有這個分支時 Sha 是 null；連不上、要登入、逾時丟 <see cref="GitReadException"/></summary>
    Task<GitRemoteSnapshot> CheckRemoteAsync(string folder, GitRemoteTarget target, CancellationToken ct);

    /// <summary>
    /// 自動拉取（<see cref="GitAutoPullRules"/>）：需要時先 fetch 那一個遠端分支，重讀狀態再判斷一次（HEAD 變了、有了未提交的變更就不拉），
    /// 再 <c>git merge --ff-only</c>。git 失敗回 Failed（不丟例外）；找不到 git、逾時丟 <see cref="GitReadException"/>
    /// </summary>
    Task<GitPullResult> FastForwardAsync(string folder, GitPullPlan plan, CancellationToken ct);
}

/// <summary>git 指令失敗（逾時、非預期的結束碼）</summary>
internal sealed class GitReadException(string message) : Exception(message);

/// <summary>
/// 以本機的 git.exe 讀狀態。讀的部分只讀：<c>GIT_OPTIONAL_LOCKS=0</c>（status 不重寫索引、不取 index.lock，不會和代理或其他程式的 git 撞在一起）、
/// <c>core.fsmonitor=false</c>（repo 設定的監看程式不跟著執行）；問遠端用 <c>ls-remote</c>（不更新任何參照），而且不跳任何登入提示。
/// 自動拉取（<see cref="FastForwardAsync"/>）是唯一會改 repo 的：fetch 一個追蹤分支、快轉，同樣不跳登入提示
/// </summary>
internal sealed class GitStatusReader : IGitStatusReader
{
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PullTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LocateInterval = TimeSpan.FromMinutes(10);

    private readonly object gate = new();
    private string? gitPath;
    private DateTime nextLocate = DateTime.MinValue;

    public async Task<GitRepoStatus?> ReadAsync(string folder, CancellationToken ct)
    {
        if (!GitStatusRules.IsEligibleFolder(folder) || IsNetworkDrive(folder) || !Directory.Exists(folder) || Locate() is not { } git)
            return null;

        var top = await RunAsync(git, folder, ["rev-parse", "--show-toplevel", "--absolute-git-dir", "--git-common-dir"], LocalTimeout, ct);
        // 不是 repo（128）、在 .git 裡面、被 safe.directory 擋下：都當成不是 repo
        if (top.ExitCode != 0 || GitStatusRules.ParseRevParse(top.Stdout, folder) is not { } paths)
            return null;

        var status = await RunAsync(git, folder, ["status", "--porcelain=v2", "--branch", "--show-stash"], LocalTimeout, ct);
        if (status.ExitCode != 0)
            throw new GitReadException($"git status 失敗（{status.ExitCode}）：{Tail(status.Stderr)}");
        var porcelain = GitStatusRules.ParseStatus(status.Stdout);

        var refsResult = await RunAsync(git, folder, ["for-each-ref", "--format=" + GitStatusRules.RefFormat, .. GitStatusRules.RefPatterns(porcelain)], LocalTimeout, ct);
        var refs = refsResult.ExitCode == 0 ? GitStatusRules.ParseRefs(refsResult.Stdout) : [];
        var (baseRef, target) = GitStatusRules.PlanCompare(porcelain, refs);

        (int Left, int Right)? baseCounts = null;
        if (baseRef != null && porcelain.Oid != null)
        {
            var counts = await RunAsync(git, folder, ["rev-list", "--left-right", "--count", "HEAD..." + baseRef.Ref], LocalTimeout, ct);
            baseCounts = counts.ExitCode == 0 ? GitStatusRules.ParseLeftRight(counts.Stdout) : null;
        }

        return new GitRepoStatus
        {
            Root = paths.Root,
            Porcelain = porcelain,
            Operation = GitStatusRules.OperationOf(paths.GitDir, File.Exists, Directory.Exists),
            Base = baseRef,
            BaseAhead = baseCounts?.Left,
            BaseBehind = baseCounts?.Right,
            RemoteTarget = target,
            CommonDir = paths.CommonDir,
            CheckedAt = DateTime.UtcNow,
        };
    }

    public async Task<GitRemoteSnapshot> CheckRemoteAsync(string folder, GitRemoteTarget target, CancellationToken ct)
    {
        if (Locate() is not { } git)
            throw new GitReadException("找不到 git");
        var result = await RunAsync(git, folder, ["ls-remote", "--refs", target.Remote, target.Ref], RemoteTimeout, ct);
        if (result.ExitCode != 0)
            throw new GitReadException(result.TimedOut ? "git ls-remote 逾時" : $"git ls-remote 失敗（{result.ExitCode}）：{Tail(result.Stderr)}");
        return new GitRemoteSnapshot(target, GitStatusRules.ParseLsRemote(result.Stdout, target.Ref), DateTime.UtcNow);
    }

    public async Task<GitPullResult> FastForwardAsync(string folder, GitPullPlan plan, CancellationToken ct)
    {
        if (Locate() is not { } git)
            throw new GitReadException("找不到 git");
        if (plan.Fetch is { } fetch)
        {
            var fetched = await RunAsync(git, folder, ["fetch", "--no-tags", "--no-recurse-submodules", "--quiet", fetch.Remote, GitAutoPullRules.Refspec(plan)], PullTimeout, ct);
            if (fetched.ExitCode != 0)
                return GitPullResult.Fail(GitAutoPullRules.ErrorLine(fetched.Stderr, $"git fetch 失敗（{fetched.ExitCode}）"));
        }

        // 執行前再看一次（決定到現在，代理、使用者或其他程式可能提交了、改了檔、換了分支）；fetch 過的話未拉取的數目也是新的
        var now = await ReadAsync(folder, ct);
        if (now == null)
            return GitPullResult.Fail("讀不到這個 repo");
        if (!string.Equals(now.Porcelain.Oid, plan.ExpectedHead, StringComparison.OrdinalIgnoreCase))
            return GitPullResult.Skip("HEAD 在檢查之後變了");
        var again = GitAutoPullRules.Decide(now, remote: null, agentRunning: false);
        if (again.Plan is not { } fresh)
            return again.SkipReason is { } reason ? GitPullResult.Skip(reason) : GitPullResult.Done(0);
        if (fresh.TargetRef != plan.TargetRef)
            return GitPullResult.Skip("要比較的分支在檢查之後變了");

        var merged = await RunAsync(git, folder, ["merge", "--ff-only", "--no-stat", "--quiet", fresh.TargetRef], PullTimeout, ct);
        return merged.ExitCode == 0
            ? GitPullResult.Done(fresh.Behind)
            : GitPullResult.Fail(GitAutoPullRules.ErrorLine(merged.Stderr, $"git merge --ff-only 失敗（{merged.ExitCode}）"));
    }

    private static async Task<CliRunResult> RunAsync(string git, string folder, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var result = await CliRunner.RunAsync(new AgentLauncher(git, ["-c", "core.fsmonitor=false"]), args, ConfigureEnvironment, timeout, ct, workingDirectory: folder);
        if (result.TimedOut)
            throw new GitReadException($"git {args[0]} 逾時");
        return result;
    }

    /// <summary>不取鎖、不跳提示；拿掉會把 git 指到別的 repo 的變數</summary>
    private static void ConfigureEnvironment(IDictionary<string, string?> env)
    {
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_COMMON_DIR", "GIT_NAMESPACE" })
            env.Remove(name);
        env["GIT_OPTIONAL_LOCKS"] = "0";
        env["GIT_TERMINAL_PROMPT"] = "0";
        env["GCM_INTERACTIVE"] = "never";
    }

    private string? Locate()
    {
        lock (gate)
        {
            if (gitPath != null && File.Exists(gitPath))
                return gitPath;
            if (DateTime.UtcNow < nextLocate)
                return null;
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            gitPath = GitStatusRules.FindGit(Environment.GetEnvironmentVariable("PATH"),
                [Path.Combine(programFiles, "Git", "cmd", "git.exe"), Path.Combine(localPrograms, "Git", "cmd", "git.exe")], File.Exists);
            if (gitPath == null)
            {
                nextLocate = DateTime.UtcNow + LocateInterval;
                Log.Info("找不到 git.exe：對話的工作資料夾不顯示 git 狀態");
            }
            return gitPath;
        }
    }

    private static bool IsNetworkDrive(string folder)
    {
        try
        {
            return Path.GetPathRoot(folder) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string Tail(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length <= 300 ? t : t[^300..];
    }
}
