using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Myriagent.Git;

/// <summary>自動拉取要做的事：先 fetch（遠端有還沒下載的提交時）、再快轉到 <paramref name="TargetRef"/></summary>
/// <param name="TargetRef">快轉的目標（遠端追蹤分支的完整名稱，<c>refs/remotes/origin/main</c>）</param>
/// <param name="TargetName">簡稱（<c>origin/main</c>；顯示用）</param>
/// <param name="Fetch">要先 fetch 的遠端分支；本機記著的已經比較新（只差快轉）時是 null</param>
/// <param name="ExpectedHead">決定時的 HEAD：執行前 HEAD 變了就不拉</param>
/// <param name="Behind">決定時已知的未拉取提交數（fetch 之前）</param>
internal sealed record GitPullPlan(string TargetRef, string TargetName, GitRemoteTarget? Fetch, string ExpectedHead, int Behind);

/// <summary>要不要自動拉取：Plan 有值就拉；SkipReason 有值是有提交可拉、但這次不拉的原因；兩個都是 null＝沒有要拉的</summary>
internal sealed record GitPullDecision(GitPullPlan? Plan, string? SkipReason)
{
    public static GitPullDecision Nothing { get; } = new(null, null);
}

internal enum GitAutoPullState
{
    Pulling,
    Pulled,
    Skipped,
    Failed,
}

/// <summary>自動拉取的結果（讀取器回報；<see cref="Pulled"/> 是快轉過來的提交數）</summary>
internal sealed record GitPullResult(GitAutoPullState State, int Pulled, string? Detail)
{
    public static GitPullResult Done(int pulled) => new(GitAutoPullState.Pulled, pulled, null);

    public static GitPullResult Skip(string reason) => new(GitAutoPullState.Skipped, 0, reason);

    public static GitPullResult Fail(string message) => new(GitAutoPullState.Failed, 0, message);
}

/// <summary>面板上「自動拉取」那一列（GitStatusView 的 autoPull）</summary>
internal sealed record GitAutoPullView(GitAutoPullState State, string Text, DateTime At);

/// <summary>
/// 工作資料夾有未拉取的提交時自動拉取（web-protocol GitStatusView 的 autoPull）。只做快轉：<c>git fetch</c> 那一個遠端分支到它的追蹤分支，
/// 再 <c>git merge --ff-only</c>；不產生合併提交、不 rebase。有上游的分支和上游比，分離的 HEAD 和遠端的預設分支比（同面板的未拉取）；
/// 沒有上游的分支不拉（不知道要從哪裡拉）。有進行中的操作、衝突、這裡有不在目標上的提交（不能快轉）、已追蹤的檔案有未提交的變更、
/// 這個 repo 有對話正在執行時不拉，面板寫出原因。純函式
/// </summary>
internal static class GitAutoPullRules
{
    /// <summary>自動拉取失敗之後多久內不再自動試（使用者按「重新檢查」照樣試）：離線、要登入時不要每 30 秒跑一次 fetch</summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(15);

    /// <summary>兩次自動拉取之間至少隔多久（拉完重讀仍顯示未拉取時，不會一直重拉）</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(1);

    private const string RemotesPrefix = "refs/remotes/";
    private const string HeadsPrefix = "refs/heads/";

    private static readonly Dictionary<string, string> Operations = new(StringComparer.Ordinal)
    {
        ["merge"] = "合併",
        ["rebase"] = "rebase",
        ["cherryPick"] = "揀選（cherry-pick）",
        ["revert"] = "還原（revert）",
        ["bisect"] = "bisect",
    };

    /// <summary>
    /// 這次要不要自動拉取。<paramref name="remote"/> 是最近一次問遠端的結果（遠端有還沒下載的提交時先 fetch）；
    /// <paramref name="agentRunning"/>＝這個 repo 裡有對話正在執行（代理可能正在改檔、提交）
    /// </summary>
    public static GitPullDecision Decide(GitRepoStatus status, GitRemoteSnapshot? remote, bool agentRunning)
    {
        ArgumentNullException.ThrowIfNull(status);
        var p = status.Porcelain;
        if (p.Oid == null)
            return GitPullDecision.Nothing;

        string targetRef, targetName;
        int ahead, behind;
        if (p.Branch != null)
        {
            // 有上游、上游還在而且是遠端分支：和上游比。沒有上游的分支（還沒推送過、追蹤本機分支）不拉
            if (p.Upstream is not { } upstream || p.Ahead is not { } a || p.Behind is not { } b
                || TrackingRefOf(status.RemoteTarget) != RemotesPrefix + upstream)
                return GitPullDecision.Nothing;
            (targetRef, targetName, ahead, behind) = (RemotesPrefix + upstream, upstream, a, b);
        }
        else
        {
            // 分離的 HEAD：和遠端的預設分支比
            if (status.Base is not { } baseRef || status.BaseAhead is not { } a || status.BaseBehind is not { } b
                || TrackingRefOf(status.RemoteTarget) != baseRef.Ref)
                return GitPullDecision.Nothing;
            (targetRef, targetName, ahead, behind) = (baseRef.Ref, baseRef.Name, a, b);
        }

        bool remoteNew = GitStatusRules.RemoteHasNew(status.RemoteTarget, remote) == true;
        if (behind == 0 && !remoteNew)
            return GitPullDecision.Nothing;

        if (status.Operation != null)
            return new(null, $"有進行中的{(Operations.TryGetValue(status.Operation, out var op) ? op : status.Operation)}還沒完成");
        if (p.Conflicts > 0)
            return new(null, $"有 {p.Conflicts} 個檔案的衝突還沒解決");
        if (ahead > 0)
            return new(null, $"這裡有 {ahead} 個提交不在 {targetName}，不能快轉");
        if (p.Changed > 0)
            return new(null, $"有 {p.Changed} 個已追蹤的檔案還沒提交");
        if (agentRunning)
            return new(null, "這個資料夾有對話正在執行，結束後再拉");

        return new(new GitPullPlan(targetRef, targetName, remoteNew ? status.RemoteTarget : null, p.Oid, behind), null);
    }

    /// <summary>遠端分支在這台的追蹤分支（照預設的 refspec：<c>refs/heads/main</c> → <c>refs/remotes/origin/main</c>）；不是分支回 null</summary>
    public static string? TrackingRefOf(GitRemoteTarget? target)
        => target != null && target.Ref.StartsWith(HeadsPrefix, StringComparison.Ordinal) && target.Ref.Length > HeadsPrefix.Length
            ? $"{RemotesPrefix}{target.Remote}/{target.Ref[HeadsPrefix.Length..]}"
            : null;

    /// <summary>fetch 用的 refspec：只更新要快轉的那一個追蹤分支（遠端被強制推送時照樣跟上，同預設的 refspec）</summary>
    public static string Refspec(GitPullPlan plan)
        => plan.Fetch is { } fetch ? $"+{fetch.Ref}:{plan.TargetRef}" : throw new InvalidOperationException("這個計畫不需要 fetch");

    /// <summary>使用者按「重新檢查」之後多久內，第一次拉取不管冷卻（重讀本機、問遠端都可能要幾秒到十幾秒）</summary>
    public static readonly TimeSpan UserRequestWindow = TimeSpan.FromMinutes(2);

    /// <summary>使用者剛要求過（<see cref="UserRequestWindow"/> 內）、還沒用掉</summary>
    public static bool UserRequested(DateTime? requestedAt, DateTime now) => requestedAt is { } at && now - at < UserRequestWindow && now >= at;

    /// <summary>這次能不能真的跑 git：使用者要求一律可以；否則離上次自動拉取至少 <see cref="MinInterval"/>，上次失敗的要等 <see cref="FailureBackoff"/></summary>
    public static bool CanAttempt(DateTime? lastAttemptAt, bool lastFailed, bool userRequested, DateTime now)
    {
        if (userRequested || lastAttemptAt is not { } last)
            return true;
        return now - last >= (lastFailed ? FailureBackoff : MinInterval);
    }

    /// <summary>工作資料夾在這個 repo 裡（同一個資料夾或子資料夾；不分大小寫）</summary>
    public static bool IsInside(string folder, string root)
    {
        var f = folder.TrimEnd('\\', '/');
        var r = root.TrimEnd('\\', '/');
        return string.Equals(f, r, StringComparison.OrdinalIgnoreCase)
               || (f.Length > r.Length + 1 && f.StartsWith(r, StringComparison.OrdinalIgnoreCase) && f[r.Length] is '\\' or '/');
    }

    /// <summary>拉取中的說明</summary>
    public static GitAutoPullView Pulling(GitPullPlan plan, DateTime now) => new(GitAutoPullState.Pulling, $"正在從 {plan.TargetName} 拉取…", now);

    /// <summary>有提交可拉、這次不拉的說明</summary>
    public static GitAutoPullView Skipped(string reason, DateTime now) => new(GitAutoPullState.Skipped, $"這次不拉：{reason}", now);

    /// <summary>拉取的結果要顯示什麼；快轉了 0 個（fetch 之後發現已經是最新）不顯示（null）</summary>
    public static GitAutoPullView? ViewOf(GitPullResult result, GitPullPlan plan, DateTime now) => result.State switch
    {
        GitAutoPullState.Pulled => result.Pulled > 0 ? new(GitAutoPullState.Pulled, $"已從 {plan.TargetName} 快轉拉取 {result.Pulled} 個提交", now) : null,
        GitAutoPullState.Skipped => Skipped(result.Detail ?? "狀態在檢查之後變了", now),
        GitAutoPullState.Failed => new(GitAutoPullState.Failed, $"失敗：{result.Detail ?? "原因不明"}", now),
        GitAutoPullState.Pulling => Pulling(plan, now),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.State, null),
    };

    /// <summary>兩個說明是不是同一件事（不看時間：沒變就不重送）</summary>
    public static bool SameView(GitAutoPullView? a, GitAutoPullView? b)
        => a == null ? b == null : b != null && a.State == b.State && a.Text == b.Text;

    private static readonly Regex UrlCredentials = new(@"://[^/\s@]+@", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// git 的錯誤輸出整理成一行：優先取 <c>fatal:</c>／<c>error:</c> 開頭的那一行，否則第一個非空行；網址裡的帳號密碼遮掉、最多 200 字
    /// </summary>
    public static string ErrorLine(string? stderr, string fallback)
    {
        var lines = (stderr ?? string.Empty).Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();
        var line = lines.FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                   ?? lines.FirstOrDefault();
        if (line == null)
            return fallback;
        line = UrlCredentials.Replace(line, "://***@");
        return line.Length <= 200 ? line : line[..200] + "…";
    }

    /// <summary>GitStatusView 的 autoPull（沒有是 null）</summary>
    public static JToken Render(GitAutoPullView? view)
        => view == null
            ? JValue.CreateNull()
            : new JObject
            {
                ["state"] = view.State switch
                {
                    GitAutoPullState.Pulling => "pulling",
                    GitAutoPullState.Pulled => "pulled",
                    GitAutoPullState.Skipped => "skipped",
                    GitAutoPullState.Failed => "failed",
                    _ => throw new ArgumentOutOfRangeException(nameof(view), view.State, null),
                },
                ["text"] = view.Text,
                ["at"] = new DateTimeOffset(DateTime.SpecifyKind(view.At, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            };
}
