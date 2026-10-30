using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Git;

/// <summary><c>git status --porcelain=v2 --branch --show-stash</c> 整理出來的數字</summary>
/// <param name="Oid">HEAD 的提交；還沒有任何提交（<c>(initial)</c>）是 null</param>
/// <param name="Branch">目前的分支；分離的 HEAD（<c>(detached)</c>）是 null</param>
/// <param name="Upstream">上游分支的簡稱（例 <c>origin/main</c>）；沒設是 null</param>
/// <param name="Ahead">比上游多的提交（未推送）；沒有上游、或上游的提交不在這台（已刪除）是 null</param>
/// <param name="Behind">比上游少的提交（未拉取），同 <paramref name="Ahead"/></param>
/// <param name="Changed">有變更的已追蹤檔案（一般與更名的項目）</param>
/// <param name="Staged">已暫存的（索引欄不是 <c>.</c>）</param>
/// <param name="Unstaged">還沒暫存的（工作樹欄不是 <c>.</c>）；同一個檔案可能同時算進兩邊</param>
/// <param name="Untracked">未追蹤的檔案（被忽略的不算）</param>
/// <param name="Conflicts">合併衝突、還沒解決的檔案</param>
/// <param name="Stashes">stash 的筆數</param>
internal sealed record GitPorcelainStatus(
    string? Oid, string? Branch, string? Upstream, int? Ahead, int? Behind,
    int Changed, int Staged, int Unstaged, int Untracked, int Conflicts, int Stashes);

/// <summary><c>git for-each-ref</c> 的一筆（格式見 <see cref="GitStatusRules.RefFormat"/>）</summary>
/// <param name="Name">完整名稱（<c>refs/remotes/origin/main</c>）</param>
/// <param name="Object">指向的提交</param>
/// <param name="SymRef">符號參照指向的完整名稱（<c>refs/remotes/origin/HEAD</c> 才有）</param>
/// <param name="UpstreamRemote">本機分支的上游所在的遠端（<c>origin</c>；上游是本機分支時是 <c>.</c>）</param>
/// <param name="UpstreamRef">上游在遠端的完整名稱（<c>refs/heads/main</c>）</param>
internal sealed record GitRefInfo(string Name, string Object, string? SymRef, string? UpstreamRemote, string? UpstreamRef);

/// <summary>要問遠端的分支（<c>git ls-remote</c>）與這台記著的那個分支的提交</summary>
/// <param name="Remote">遠端名稱（<c>origin</c>）</param>
/// <param name="Ref">遠端上的完整名稱（<c>refs/heads/main</c>）</param>
/// <param name="LocalSha">這台記著的提交（遠端追蹤分支，上次 fetch 時的）</param>
internal sealed record GitRemoteTarget(string Remote, string Ref, string LocalSha);

/// <summary>問遠端的結果</summary>
/// <param name="Target">問的是哪個分支</param>
/// <param name="Sha">遠端現在的提交；遠端沒有這個分支是 null</param>
/// <param name="CheckedAt">問到的時間（UTC）</param>
internal sealed record GitRemoteSnapshot(GitRemoteTarget Target, string? Sha, DateTime CheckedAt);

/// <summary>比較基準：沒有追蹤它的時候，和遠端的預設分支比（分離的 HEAD、沒有上游的分支、追蹤別的分支）</summary>
/// <param name="Name">簡稱（<c>origin/main</c>）</param>
/// <param name="Ref">完整名稱（<c>refs/remotes/origin/main</c>）</param>
internal sealed record GitBaseRef(string Name, string Ref);

/// <summary>一個工作資料夾的 git 狀態（只讀本機；遠端的結果另外合併，見 <see cref="GitStatusRules.Render"/>）</summary>
internal sealed record GitRepoStatus
{
    /// <summary>repo 的最上層資料夾（Windows 路徑）</summary>
    public required string Root { get; init; }

    public required GitPorcelainStatus Porcelain { get; init; }

    /// <summary>進行中的操作：merge、rebase、cherryPick、revert、bisect；沒有是 null</summary>
    public string? Operation { get; init; }

    /// <summary>和遠端預設分支比較（沒有就是 null）</summary>
    public GitBaseRef? Base { get; init; }

    /// <summary>比 <see cref="Base"/> 多的提交（還沒進預設分支的）</summary>
    public int? BaseAhead { get; init; }

    /// <summary>比 <see cref="Base"/> 少的提交</summary>
    public int? BaseBehind { get; init; }

    /// <summary>可以問遠端的分支；沒有遠端分支可比時是 null</summary>
    public GitRemoteTarget? RemoteTarget { get; init; }

    /// <summary>
    /// git 的共用資料夾（<c>--git-common-dir</c>：同一個 repo 的各個 worktree 都一樣，遠端追蹤分支存在這裡）；不明是 null（以 <see cref="Root"/> 代替）
    /// </summary>
    public string? CommonDir { get; init; }

    /// <summary>讀完的時間（UTC）</summary>
    public DateTime CheckedAt { get; init; }
}

/// <summary>
/// 對話工作資料夾的 git 狀態（web-protocol 的 <c>gitStatus</c>）：解析 git 的輸出、決定要和哪個遠端分支比、產生給網頁的 GitStatusView、
/// 決定什麼時候重讀。讀狀態只讀：git 指令一律以 <c>GIT_OPTIONAL_LOCKS=0</c> 執行（不寫索引、不取鎖），遠端只用 <c>ls-remote</c> 問；
/// 唯一會改 repo 的是有未拉取的提交時的自動快轉拉取（<see cref="GitAutoPullRules"/>）
/// </summary>
internal static class GitStatusRules
{
    /// <summary>開分頁、切分頁、視窗回到前景：比這個舊才重讀</summary>
    public static readonly TimeSpan OpenMaxAge = TimeSpan.FromSeconds(5);

    /// <summary>畫面上看得到的 repo 定時重讀（維護計時器每 30 秒一次，留一點餘裕）</summary>
    public static readonly TimeSpan PeriodicMaxAge = TimeSpan.FromSeconds(25);

    /// <summary>不是 repo 的資料夾定時重查（代理可能 git init）</summary>
    public static readonly TimeSpan NotRepoMaxAge = TimeSpan.FromMinutes(2);

    /// <summary>問遠端（ls-remote，要連網路）的間隔</summary>
    public static readonly TimeSpan RemoteInterval = TimeSpan.FromMinutes(5);

    /// <summary>讀失敗（逾時、git 出錯）之後多久內只有使用者按「重新檢查」才再讀（很大的 repo 不要每 30 秒卡一次）</summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(2);

    /// <summary>for-each-ref 的格式：名稱、提交、符號參照、上游的遠端、上游在遠端的名稱，以 tab 分隔</summary>
    public const string RefFormat = "%(refname)%09%(objectname)%09%(symref)%09%(upstream:remotename)%09%(upstream:remoteref)";

    private const string RemotesPrefix = "refs/remotes/";
    private const string HeadsPrefix = "refs/heads/";

    /// <summary>只讀本機磁碟（<c>C:\…</c>）的資料夾：UNC 一律不碰（連過去就會送出 SMB 雜湊），網路磁碟由讀取器另外擋</summary>
    public static bool IsEligibleFolder(string? folder) => AgentAttachmentRules.IsLocalDrivePath(folder);

    /// <summary>快取的鍵：完整路徑、去掉結尾的分隔符號（磁碟根目錄保留）；不合格的路徑回 null</summary>
    public static string? FolderKey(string? folder)
    {
        if (!IsEligibleFolder(folder))
            return null;
        try
        {
            var full = Path.GetFullPath(folder!);
            var trimmed = full.TrimEnd('\\', '/');
            return trimmed.Length <= 2 ? trimmed + "\\" : trimmed;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// 同一個 repository 的鍵：git 的共用資料夾（同一個 repo 的子資料夾、各個 worktree 都一樣），不明時用最上層資料夾。
    /// 自動拉取以它序列化（同一個 repo 同時只拉一次：fetch 會更新共用的遠端追蹤分支）
    /// </summary>
    public static string RepoKey(GitRepoStatus status)
    {
        var trimmed = (status.CommonDir ?? status.Root).TrimEnd('\\', '/');
        return trimmed.Length <= 2 ? trimmed + "\\" : trimmed;
    }

    /// <summary>
    /// 問遠端（ls-remote）的快取鍵：同一個 repository 的同一個遠端分支只問一次——結果與這台記著的提交都在共用資料夾，
    /// 子資料夾、各個 worktree、每個開著的對話都共用；沒有可問的分支是 null
    /// </summary>
    public static string? RemoteKey(GitRepoStatus status)
        => status.RemoteTarget is { } target ? $"{RepoKey(status)}|{target.Remote}|{target.Ref}" : null;

    /// <summary>在 PATH 找 git.exe（只認完整路徑的項目：不從目前資料夾找），找不到再試常見的安裝位置</summary>
    public static string? FindGit(string? pathVariable, IEnumerable<string> fallbacks, Func<string, bool> fileExists)
    {
        foreach (var entry in (pathVariable ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = entry.Trim('"');
            if (dir.Length == 0 || !Path.IsPathFullyQualified(dir) || dir.StartsWith(@"\\", StringComparison.Ordinal))
                continue;
            var candidate = Path.Combine(dir, "git.exe");
            if (fileExists(candidate))
                return candidate;
        }
        return fallbacks.FirstOrDefault(fileExists);
    }

    /// <summary>
    /// <c>git rev-parse --show-toplevel --absolute-git-dir --git-common-dir</c>：三行（最上層、.git 資料夾、共用資料夾）；只有兩行時共用資料夾就是 .git 資料夾。
    /// 共用資料夾可能是相對於執行時資料夾（<paramref name="cwd"/>）的路徑；行數不對（例如在 .git 裡面）回 null
    /// </summary>
    public static (string Root, string GitDir, string CommonDir)? ParseRevParse(string? output, string? cwd = null)
    {
        var lines = (output ?? string.Empty).Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count is not (2 or 3))
            return null;
        try
        {
            var gitDir = Path.GetFullPath(lines[1]);
            var common = lines.Count == 2 ? gitDir
                : Path.IsPathFullyQualified(lines[2]) || cwd == null ? Path.GetFullPath(lines[2])
                : Path.GetFullPath(Path.Combine(cwd, lines[2]));
            return (Path.GetFullPath(lines[0]), gitDir, common);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>解析 <c>git status --porcelain=v2 --branch --show-stash</c>（不加 -z：路徑裡的特殊字元會被加上引號，一筆一定是一行）</summary>
    public static GitPorcelainStatus ParseStatus(string? output)
    {
        string? oid = null, branch = null, upstream = null;
        int? ahead = null, behind = null;
        int changed = 0, staged = 0, unstaged = 0, untracked = 0, conflicts = 0, stashes = 0;
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var header = line[2..];
                if (TryValue(header, "branch.oid ", out var v))
                    oid = v == "(initial)" ? null : v;
                else if (TryValue(header, "branch.head ", out v))
                    branch = v == "(detached)" ? null : v;
                else if (TryValue(header, "branch.upstream ", out v))
                    upstream = v;
                else if (TryValue(header, "branch.ab ", out v))
                    (ahead, behind) = ParseAheadBehind(v);
                else if (TryValue(header, "stash ", out v) && int.TryParse(v, out var n) && n >= 0)
                    stashes = n;
                continue;
            }
            if (line.Length < 2 || line[1] != ' ')
                continue;
            switch (line[0])
            {
                case '1':
                case '2':
                    changed++;
                    if (line.Length >= 4)
                    {
                        if (line[2] != '.')
                            staged++;
                        if (line[3] != '.')
                            unstaged++;
                    }
                    break;
                case 'u':
                    conflicts++;
                    break;
                case '?':
                    untracked++;
                    break;
            }
        }
        return new GitPorcelainStatus(oid, branch, upstream, ahead, behind, changed, staged, unstaged, untracked, conflicts, stashes);
    }

    private static bool TryValue(string header, string key, out string value)
    {
        if (header.StartsWith(key, StringComparison.Ordinal))
        {
            value = header[key.Length..].Trim();
            return true;
        }
        value = string.Empty;
        return false;
    }

    /// <summary><c>+3 -1</c> → (3, 1)；格式不對回 (null, null)</summary>
    private static (int?, int?) ParseAheadBehind(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0].StartsWith('+') && parts[1].StartsWith('-')
            && int.TryParse(parts[0][1..], out var a) && int.TryParse(parts[1][1..], out var b) && a >= 0 && b >= 0)
            return (a, b);
        return (null, null);
    }

    /// <summary>
    /// for-each-ref 要列的：目前的分支（取上游的遠端與名稱）、上游（取它記著的提交）、各遠端的 HEAD、main、master（找預設分支）。
    /// 不列全部的遠端分支：遠端分支很多的 repo 會很慢
    /// </summary>
    public static IReadOnlyList<string> RefPatterns(GitPorcelainStatus status)
    {
        var patterns = new List<string>();
        if (status.Branch is { } branch)
            patterns.Add(HeadsPrefix + branch);
        if (status.Upstream is { } upstream)
            patterns.Add(RemotesPrefix + upstream);
        patterns.Add(RemotesPrefix + "*/HEAD");
        patterns.Add(RemotesPrefix + "*/main");
        patterns.Add(RemotesPrefix + "*/master");
        return patterns;
    }

    /// <summary>解析 for-each-ref（<see cref="RefFormat"/>）的輸出</summary>
    public static IReadOnlyList<GitRefInfo> ParseRefs(string? output)
    {
        var refs = new List<GitRefInfo>();
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split('\t');
            // 後面的空欄位可能被去掉（只有名稱與提交）：缺的當成沒有
            if (parts.Length is < 2 or > 5 || parts[0].Length == 0 || parts[1].Length == 0)
                continue;
            refs.Add(new GitRefInfo(parts[0], parts[1], PartAt(parts, 2), PartAt(parts, 3), PartAt(parts, 4)));
        }
        return refs;
    }

    private static string? PartAt(string[] parts, int index) => index < parts.Length && parts[index].Length > 0 ? parts[index] : null;

    /// <summary>
    /// 決定比較基準與要問遠端的分支：
    /// <list type="bullet">
    /// <item>遠端：上游所在的遠端，沒有上游就用 origin，再沒有就用唯一的那一個。</item>
    /// <item>基準：那個遠端的預設分支（<c>遠端/HEAD</c> 指向的，沒有就 main、master）；就是上游時不另外比（上游的領先落後已經是同一件事）。</item>
    /// <item>問遠端：有上游（而且是遠端分支）就問上游，否則問基準。</item>
    /// </list>
    /// </summary>
    public static (GitBaseRef? Base, GitRemoteTarget? Remote) PlanCompare(GitPorcelainStatus status, IReadOnlyList<GitRefInfo> refs)
    {
        var byName = new Dictionary<string, GitRefInfo>(StringComparer.Ordinal);
        foreach (var r in refs)
            byName.TryAdd(r.Name, r);
        // 遠端/HEAD 指向的分支不一定在列出的範圍（例如指向 develop）：for-each-ref 給的 HEAD 提交就是它指向的那一個
        foreach (var r in refs)
        {
            if (r.SymRef is { } pointed && pointed.StartsWith(RemotesPrefix, StringComparison.Ordinal))
                byName.TryAdd(pointed, new GitRefInfo(pointed, r.Object, null, null, null));
        }

        var branchRef = status.Branch is { } branch && byName.TryGetValue(HeadsPrefix + branch, out var b) ? b : null;
        var upstreamRemote = branchRef?.UpstreamRemote is { } ur && ur != "." ? ur : null;

        var remotes = refs.Where(r => r.Name.StartsWith(RemotesPrefix, StringComparison.Ordinal))
            .Select(r => RemoteOf(r.Name)).Where(n => n != null).Select(n => n!).Distinct(StringComparer.Ordinal).ToList();
        var remote = upstreamRemote
                     ?? (remotes.Contains("origin", StringComparer.Ordinal) ? "origin" : remotes.Count == 1 ? remotes[0] : null);

        GitBaseRef? baseRef = null;
        if (remote != null)
        {
            var head = byName.TryGetValue($"{RemotesPrefix}{remote}/HEAD", out var h) ? h.SymRef : null;
            var candidates = new[] { head, $"{RemotesPrefix}{remote}/main", $"{RemotesPrefix}{remote}/master" };
            var found = candidates.FirstOrDefault(c => c != null && c.StartsWith(RemotesPrefix + remote + "/", StringComparison.Ordinal) && byName.ContainsKey(c));
            if (found != null)
                baseRef = new GitBaseRef(found[RemotesPrefix.Length..], found);
        }

        GitRemoteTarget? target = null;
        if (status.Upstream is { } upstream && upstreamRemote != null && branchRef?.UpstreamRef is { } upstreamRef
            && byName.TryGetValue(RemotesPrefix + upstream, out var tracking))
            target = new GitRemoteTarget(upstreamRemote, upstreamRef, tracking.Object);
        else if (status.Upstream == null && baseRef != null && remote != null && byName.TryGetValue(baseRef.Ref, out var baseInfo))
            target = new GitRemoteTarget(remote, HeadsPrefix + baseRef.Name[(remote.Length + 1)..], baseInfo.Object);

        if (baseRef != null && string.Equals(baseRef.Name, status.Upstream, StringComparison.Ordinal))
            baseRef = null;
        return (baseRef, target);
    }

    /// <summary><c>refs/remotes/origin/main</c> → <c>origin</c></summary>
    private static string? RemoteOf(string name)
    {
        var rest = name[RemotesPrefix.Length..];
        var slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : null;
    }

    /// <summary><c>git rev-list --left-right --count A...B</c> 的輸出（<c>3\t1</c>）→ (3, 1)</summary>
    public static (int Left, int Right)? ParseLeftRight(string? output)
    {
        var parts = (output ?? string.Empty).Trim().Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var l) && int.TryParse(parts[1], out var r) && l >= 0 && r >= 0 ? (l, r) : null;
    }

    /// <summary><c>git ls-remote</c> 的輸出裡這個分支的提交；沒有這個分支回 null</summary>
    public static string? ParseLsRemote(string? output, string refName)
    {
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split('\t');
            if (parts.Length == 2 && parts[1] == refName && parts[0].Length >= 7)
                return parts[0];
        }
        return null;
    }

    /// <summary>進行中的操作（看 .git 資料夾裡的標記檔；worktree 的是它自己的那一個）</summary>
    public static string? OperationOf(string gitDir, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        if (directoryExists(Path.Combine(gitDir, "rebase-merge")) || directoryExists(Path.Combine(gitDir, "rebase-apply")))
            return "rebase";
        if (fileExists(Path.Combine(gitDir, "MERGE_HEAD")))
            return "merge";
        if (fileExists(Path.Combine(gitDir, "CHERRY_PICK_HEAD")))
            return "cherryPick";
        if (fileExists(Path.Combine(gitDir, "REVERT_HEAD")))
            return "revert";
        if (fileExists(Path.Combine(gitDir, "BISECT_LOG")))
            return "bisect";
        return null;
    }

    /// <summary>為什麼要讀（決定要不要真的讀、要不要順便問遠端）</summary>
    public enum Trigger
    {
        /// <summary>開分頁、切分頁、視窗回到前景</summary>
        Open,

        /// <summary>維護計時器（畫面上看得到的對話）</summary>
        Periodic,

        /// <summary>換了工作資料夾、這一輪結束（代理可能改了檔案、提交了）</summary>
        Changed,

        /// <summary>使用者按「重新檢查」</summary>
        User,
    }

    /// <summary>這次要不要讀本機的狀態</summary>
    /// <param name="checkedAt">上次讀完的時間（沒讀過是 null）</param>
    /// <param name="isRepo">上次讀到的是 repo</param>
    /// <param name="retryAfter">上次讀失敗時，這個時間之前只有使用者要求才讀</param>
    public static bool ShouldReadLocal(Trigger trigger, DateTime? checkedAt, bool isRepo, DateTime? retryAfter, DateTime now)
    {
        if (trigger == Trigger.User)
            return true;
        if (retryAfter is { } retry && now < retry)
            return false;
        if (trigger == Trigger.Changed || checkedAt is not { } last)
            return true;
        var age = now - last;
        return trigger switch
        {
            Trigger.Open => age >= OpenMaxAge,
            Trigger.Periodic => age >= (isRepo ? PeriodicMaxAge : NotRepoMaxAge),
            _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, null),
        };
    }

    /// <summary>讀完本機的狀態之後要不要問遠端：使用者要求就問，其他照 <see cref="RemoteInterval"/></summary>
    public static bool ShouldCheckRemote(Trigger trigger, DateTime? remoteCheckedAt, DateTime now)
        => trigger == Trigger.User || remoteCheckedAt is not { } last || now - last >= RemoteInterval;

    /// <summary>
    /// 遠端有沒有這台還沒下載的提交：問過同一個分支、遠端現在的提交和這台記著的不同就是 true；沒問過、問的不是同一個分支、遠端沒有這個分支是 null。
    /// 用這台目前記著的提交比，fetch 過之後不必再問一次就會變回 false
    /// </summary>
    public static bool? RemoteHasNew(GitRemoteTarget? target, GitRemoteSnapshot? snapshot)
    {
        if (target == null || snapshot == null || snapshot.Sha == null)
            return null;
        if (snapshot.Target.Remote != target.Remote || snapshot.Target.Ref != target.Ref)
            return null;
        return !string.Equals(snapshot.Sha, target.LocalSha, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>GitStatusView（web-protocol 第三節）；remote 是最近一次問遠端的結果（沒有是 null），autoPull 是自動拉取那一列（沒有是 null）</summary>
    public static JObject Render(GitRepoStatus status, GitRemoteSnapshot? remote, GitAutoPullView? autoPull = null)
    {
        var p = status.Porcelain;
        var remoteMatches = remote != null && status.RemoteTarget != null
                            && remote.Target.Remote == status.RemoteTarget.Remote && remote.Target.Ref == status.RemoteTarget.Ref;
        return new JObject
        {
            ["root"] = status.Root,
            ["branch"] = p.Branch,
            ["head"] = p.Oid is { Length: >= 7 } oid ? oid[..7] : null,
            ["upstream"] = p.Upstream,
            ["upstreamGone"] = p.Upstream != null && p.Ahead == null,
            ["ahead"] = p.Ahead,
            ["behind"] = p.Behind,
            ["base"] = status.Base?.Name,
            ["baseAhead"] = status.BaseAhead,
            ["baseBehind"] = status.BaseBehind,
            ["changed"] = p.Changed,
            ["staged"] = p.Staged,
            ["unstaged"] = p.Unstaged,
            ["untracked"] = p.Untracked,
            ["conflicts"] = p.Conflicts,
            ["stashes"] = p.Stashes,
            ["operation"] = status.Operation,
            ["remote"] = status.RemoteTarget?.Remote,
            ["remoteNew"] = RemoteHasNew(status.RemoteTarget, remote),
            ["remoteCheckedAt"] = remoteMatches ? ToUnixMs(remote!.CheckedAt) : null,
            ["checkedAt"] = ToUnixMs(status.CheckedAt),
            ["autoPull"] = GitAutoPullRules.Render(autoPull),
        };
    }

    private static long ToUnixMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
