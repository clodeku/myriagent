using Myriagent.Core;
using Myriagent.Git;
using Newtonsoft.Json.Linq;
using Trigger = Myriagent.Git.GitStatusRules.Trigger;

namespace Myriagent.Hub;

// 對話工作資料夾的 git 狀態（web-protocol 的 gitStatus／gitRefresh）：本機的狀態以資料夾為單位快取（同一個資料夾的多個對話共用一份），
// 讀完送給開著那些對話的視窗。什麼時候讀（GitStatusRules.ShouldReadLocal）：開分頁與切到前景（5 秒內讀過就不再讀）、畫面上看得到的每 30 秒
// （維護計時器）、換了資料夾與這一輪結束（代理可能改了檔案、提交了）、使用者按「重新檢查」。
// 問遠端（ls-remote，要連 GitHub 等遠端）以「repository＋遠端分支」為單位（GitStatusRules.RemoteKey：git 的共用資料夾，同一個 repo 的子資料夾、
// 各個 worktree 都一樣）：不論有幾個對話、幾個資料夾，同一個遠端分支最多每 5 分鐘問一次，結果大家共用。
// 讀完本機或問完遠端時，有未拉取的提交就自動快轉拉取（GitAutoPullRules：只快轉；有未提交的變更、未推送的提交、這個 repo 有對話在執行時不拉，
// 面板寫出原因）；同一個 repository 同時只拉一次，拉完重讀這個 repo 的每個資料夾。
// 沒掛讀取器（AttachGitStatus；測試與 HubHarness 預設）時什麼都不做、也不送 gitStatus。副本（其他電腦的對話）與要開在其他電腦的草稿不讀。
// 狀態只在 UI 執行緒改，git 指令在背景執行緒、同時最多兩個
internal sealed partial class AgentHub
{
    private const int GitMaxConcurrentReads = 2;

    private IGitStatusReader? gitReader;
    private readonly Dictionary<string, GitFolderState> gitFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>問遠端的結果（鍵是 GitStatusRules.RemoteKey）：同一個 repository 的同一個遠端分支共用</summary>
    private readonly Dictionary<string, GitRemoteState> gitRemotes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>正在自動拉取的 repository（GitStatusRules.RepoKey）：同一個 repo 同時只拉一次</summary>
    private readonly HashSet<string> gitPullingRepos = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim gitSlots = new(GitMaxConcurrentReads);
    private readonly CancellationTokenSource gitCancel = new();

    /// <summary>現在時間（UTC；測試可換）</summary>
    internal Func<DateTime> GitClock { get; set; } = () => DateTime.UtcNow;

    private sealed class GitFolderState
    {
        /// <summary>最近一次讀到的狀態；不是 repo 是 null</summary>
        public GitRepoStatus? Status;

        /// <summary>讀完過至少一次（之前不知道是不是 repo）</summary>
        public bool Known;

        public DateTime? CheckedAt;
        public DateTime? RetryAfter;
        public bool Reading;

        /// <summary>讀的途中又被要求讀（換了資料夾、這一輪結束、使用者要求）：讀完再讀一次；值是最強的那個原因</summary>
        public Trigger? Again;

        /// <summary>面板上「自動拉取」那一列（沒有是 null）</summary>
        public GitAutoPullView? AutoPull;

        public bool Pulling;

        /// <summary>上次真的跑 git 拉取的時間與結果（失敗的等 GitAutoPullRules.FailureBackoff 才再自動試）</summary>
        public DateTime? PullAttemptAt;

        public bool PullFailed;

        /// <summary>
        /// 使用者按「重新檢查」的時間：之後的第一次拉取不管冷卻（重讀本機與問遠端都會判斷一次，只用掉一次，否則一次要求拉兩次）；
        /// 過了 GitAutoPullRules.UserRequestWindow 就失效
        /// </summary>
        public DateTime? PullRequestedAt;
    }

    /// <summary>一個 repository 的一個遠端分支問過的結果</summary>
    private sealed class GitRemoteState
    {
        public GitRemoteSnapshot? Snapshot;

        /// <summary>上次開始問的時間（失敗也算：離線、要登入時也要等下一個間隔）</summary>
        public DateTime? CheckedAt;

        public bool Reading;
    }

    /// <summary>掛上 git 狀態的讀取器（AppController；只掛一次）。沒掛時不讀、不送</summary>
    internal void AttachGitStatus(IGitStatusReader reader) => gitReader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>這個對話要讀的資料夾（快取的鍵）；不讀的對話回 null</summary>
    private string? GitFolderOf(ConversationState c)
    {
        if (gitReader == null || c.IsReplica || (c.IsDraft && c.PeerDeviceId != null))
            return null;
        return GitStatusRules.FolderKey(GetWorkingDirectory(c));
    }

    /// <summary>這個資料夾所在 repository 的遠端分支問過的結果（還沒讀到 repo、沒有可問的分支是 null）</summary>
    private GitRemoteState? GitRemoteOf(GitFolderState e)
        => e.Status is { } status && GitStatusRules.RemoteKey(status) is { } key && gitRemotes.TryGetValue(key, out var r) ? r : null;

    /// <summary>送這個對話目前知道的 git 狀態（還沒讀完或不是 repo 時 git 是 null）；targets 是 null＝開著它的所有視窗</summary>
    private void PostGit(ConversationState c, IAgentView? only = null)
    {
        if (gitReader == null || c.IsReplica)
            return;
        var targets = (only != null ? new[] { only } : ViewsOf(c)).Where(v => v is not RemoteResponseView).ToList();
        if (targets.Count == 0)
            return;
        var key = GitFolderOf(c);
        var entry = key != null && gitFolders.TryGetValue(key, out var e) && e.Known ? e : null;
        TryPost(targets, () => new JObject
        {
            ["type"] = "gitStatus",
            ["conversationId"] = c.Meta.Id,
            ["git"] = entry?.Status is { } status ? GitStatusRules.Render(status, GitRemoteOf(entry)?.Snapshot, entry.AutoPull) : null,
        });
    }

    /// <summary>送出整份對話之後（PostConversation）：先送快取的，太舊就重讀</summary>
    private void OnGitConversationPosted(IAgentView view, ConversationState c)
    {
        if (gitReader == null || view is RemoteResponseView || c.IsReplica)
            return;
        PostGit(c, view);
        RequestGit(c, Trigger.Open);
    }

    /// <summary>換了工作資料夾：先送新資料夾快取的（沒有就是 null，藏起舊的），再重讀</summary>
    private void OnGitFolderChanged(ConversationState c)
    {
        if (gitReader == null)
            return;
        PostGit(c);
        RequestGit(c, Trigger.Changed);
    }

    /// <summary>這一輪結束：開著的對話重讀（代理可能改了檔案、提交了、推送了）</summary>
    private void OnGitTurnCompleted(ConversationState c)
    {
        if (gitReader != null && c.ViewIds.Count > 0)
            RequestGit(c, Trigger.Changed);
    }

    /// <summary>維護計時器：畫面上看得到的對話（同一個資料夾的只讀一次：讀過的在間隔內不再讀）</summary>
    private void TickGit()
    {
        if (gitReader == null)
            return;
        foreach (var c in conversations.Values.Where(c => c.VisibleViewIds.Count > 0).ToList())
            RequestGit(c, Trigger.Periodic);
    }

    private void RequestGit(ConversationState? c, Trigger trigger)
    {
        if (c == null || disposed || GitFolderOf(c) is not { } key)
            return;
        RequestGitFolder(key, trigger);
    }

    private void RequestGitFolder(string key, Trigger trigger)
    {
        if (disposed)
            return;
        if (!gitFolders.TryGetValue(key, out var e))
            gitFolders[key] = e = new GitFolderState();
        var now = GitClock();
        if (trigger == Trigger.User)
            e.PullRequestedAt = now;
        if (e.Reading)
        {
            // 讀的途中：要求的原因比定時強（資料可能已經變了）才在讀完後再讀一次
            if (trigger is Trigger.Changed or Trigger.User)
                e.Again = e.Again == Trigger.User ? Trigger.User : trigger;
            return;
        }
        if (GitStatusRules.ShouldReadLocal(trigger, e.CheckedAt, e.Status != null, e.RetryAfter, now))
            _ = ReadGitAsync(key, e, trigger);
        else
            MaybeCheckGitRemote(key, e, trigger, now);
    }

    /// <summary>這個資料夾所在 repository 的遠端分支該問了（使用者要求，或這個 repo 的這個分支超過間隔沒問）就問；別的資料夾、別的對話問過的照樣算</summary>
    private void MaybeCheckGitRemote(string folderKey, GitFolderState e, Trigger trigger, DateTime now)
    {
        if (e.Status is not { RemoteTarget: { } target } status || GitStatusRules.RemoteKey(status) is not { } remoteKey)
            return;
        if (!gitRemotes.TryGetValue(remoteKey, out var remote))
            gitRemotes[remoteKey] = remote = new GitRemoteState();
        if (!remote.Reading && GitStatusRules.ShouldCheckRemote(trigger, remote.CheckedAt, now))
            _ = CheckGitRemoteAsync(folderKey, remoteKey, remote, target);
    }

    private async Task ReadGitAsync(string key, GitFolderState e, Trigger trigger)
    {
        var reader = gitReader!;
        var ct = gitCancel.Token;
        e.Reading = true;
        try
        {
            while (!disposed)
            {
                GitRepoStatus? status = null;
                var failed = false;
                try
                {
                    status = await Task.Run(async () =>
                    {
                        await gitSlots.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            return await reader.ReadAsync(key, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            gitSlots.Release();
                        }
                    }, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failed = true;
                    Log.Warn($"讀 git 狀態失敗（{key}）：{ex.Message}");
                }
                if (disposed)
                    return;

                var now = GitClock();
                if (failed)
                {
                    // 留著上次的狀態；一陣子之內只有使用者要求才再讀
                    e.RetryAfter = now + GitStatusRules.FailureBackoff;
                    e.CheckedAt = now;
                    e.Known = true;
                }
                else
                {
                    e.Status = status;
                    e.Known = true;
                    e.CheckedAt = now;
                    e.RetryAfter = null;
                    UpdateAutoPull(key, e);
                }
                PostGitFolder(key);
                if (!failed)
                    MaybeCheckGitRemote(key, e, trigger, now);

                if (e.Again is not { } again)
                    break;
                e.Again = null;
                trigger = again;
            }
        }
        finally
        {
            e.Reading = false;
        }
    }

    /// <summary>問遠端（在發起的資料夾執行 ls-remote；同一個 repository 的哪個資料夾都一樣），結果給這個 repo 這個分支的每個資料夾</summary>
    private async Task CheckGitRemoteAsync(string folderKey, string remoteKey, GitRemoteState remote, GitRemoteTarget target)
    {
        var reader = gitReader!;
        var ct = gitCancel.Token;
        remote.Reading = true;
        // 先記時間：失敗（離線、要登入）也要等下一個間隔才再問
        remote.CheckedAt = GitClock();
        try
        {
            var snapshot = await Task.Run(() => reader.CheckRemoteAsync(folderKey, target, ct), ct);
            if (disposed)
                return;
            remote.Snapshot = snapshot;
            foreach (var (key, e) in gitFolders.ToList())
            {
                if (e.Status is { } status && string.Equals(GitStatusRules.RemoteKey(status), remoteKey, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateAutoPull(key, e);
                    PostGitFolder(key);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Info($"問遠端的 git 分支失敗（{folderKey}）：{ex.Message}");
        }
        finally
        {
            remote.Reading = false;
        }
    }

    /// <summary>
    /// 讀完本機或問完遠端之後：照 GitAutoPullRules 決定要不要自動拉取。不拉的更新面板的說明（呼叫端接著送）；
    /// 要拉的在背景執行（同時讀取的名額；同一個 repository 正在拉就先不拉，拉完會重讀），拉完更新說明、重讀這個 repo 的每個資料夾
    /// </summary>
    private void UpdateAutoPull(string key, GitFolderState e)
    {
        if (disposed || e.Pulling || e.Status is not { } status)
            return;
        var now = GitClock();
        var decision = GitAutoPullRules.Decide(status, GitRemoteOf(e)?.Snapshot, GitAgentRunningIn(status.Root));
        if (decision.Plan is not { } plan)
        {
            // 沒有要拉的：留著剛拉完的說明（讓使用者看到自動拉過了），其他的清掉；有提交可拉但不拉的寫出原因
            var view = decision.SkipReason is { } reason ? GitAutoPullRules.Skipped(reason, now)
                : e.AutoPull?.State == GitAutoPullState.Pulled ? e.AutoPull : null;
            if (!GitAutoPullRules.SameView(view, e.AutoPull))
                e.AutoPull = view;
            return;
        }
        var repo = GitStatusRules.RepoKey(status);
        if (gitPullingRepos.Contains(repo))
            return;
        if (!GitAutoPullRules.CanAttempt(e.PullAttemptAt, e.PullFailed, GitAutoPullRules.UserRequested(e.PullRequestedAt, now), now))
            return;
        e.PullRequestedAt = null;
        _ = AutoPullAsync(key, e, plan, repo);
    }

    /// <summary>這個 repo 裡有沒有對話正在執行（代理可能正在改檔、提交：這時不拉）</summary>
    private bool GitAgentRunningIn(string root)
        => conversations.Values.Any(c => c.Running && !c.IsReplica && GitFolderOf(c) is { } folder && GitAutoPullRules.IsInside(folder, root));

    private async Task AutoPullAsync(string key, GitFolderState e, GitPullPlan plan, string repo)
    {
        var reader = gitReader!;
        var ct = gitCancel.Token;
        e.Pulling = true;
        gitPullingRepos.Add(repo);
        e.PullAttemptAt = GitClock();
        e.AutoPull = GitAutoPullRules.Pulling(plan, GitClock());
        PostGitFolder(key);
        GitPullResult result;
        try
        {
            result = await Task.Run(async () =>
            {
                await gitSlots.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await reader.FastForwardAsync(key, plan, ct).ConfigureAwait(false);
                }
                finally
                {
                    gitSlots.Release();
                }
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            result = GitPullResult.Fail(ex.Message);
        }
        finally
        {
            e.Pulling = false;
            gitPullingRepos.Remove(repo);
        }
        if (disposed)
            return;

        e.PullFailed = result.State == GitAutoPullState.Failed;
        e.AutoPull = GitAutoPullRules.ViewOf(result, plan, GitClock());
        if (result.State == GitAutoPullState.Pulled)
            Log.Info($"自動拉取 {key}：從 {plan.TargetName} 快轉 {result.Pulled} 個提交");
        else
            Log.Info($"自動拉取 {key} 沒有拉：{result.Detail}");
        PostGitFolder(key);
        // 拉完（或發現狀態變了）重讀這個 repository 的每個資料夾：未拉取歸零、HEAD 換成新的；fetch 更新了共用的追蹤分支，其他 worktree 的未拉取也變了
        foreach (var (other, state) in gitFolders.ToList())
        {
            if (string.Equals(other, key, StringComparison.OrdinalIgnoreCase)
                || (state.Status is { } s && string.Equals(GitStatusRules.RepoKey(s), repo, StringComparison.OrdinalIgnoreCase)))
                RequestGitFolder(other, Trigger.Changed);
        }
    }

    /// <summary>送給開著工作資料夾是這個的對話的視窗</summary>
    private void PostGitFolder(string key)
    {
        foreach (var c in conversations.Values.Where(c => c.ViewIds.Count > 0).ToList())
        {
            if (string.Equals(GitFolderOf(c), key, StringComparison.OrdinalIgnoreCase))
                PostGit(c);
        }
    }

    /// <summary>中樞關閉：停掉進行中的 git 指令</summary>
    private void StopGit()
    {
        try
        {
            gitCancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
