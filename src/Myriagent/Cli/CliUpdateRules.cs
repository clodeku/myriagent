using System.Text.RegularExpressions;
using Myriagent.Agents;

namespace Myriagent.Cli;

/// <summary>claude update 的結果種類（2.1.296 的輸出，見 docs/protocol-notes.md「版本與更新」）</summary>
internal enum ClaudeUpdateKind
{
    /// <summary>「Successfully updated from X to version Y」（或 downgraded）：指令說換版了（仍要以 FileVersionInfo 驗證）</summary>
    Updated,

    /// <summary>「Claude Code is up to date (X)」「Claude is up to date!」</summary>
    UpToDate,

    /// <summary>
    /// 「Another Claude process (PID x) is currently running. Please try again in a moment.」：版本鎖被別的程序占住，
    /// 結束碼一樣是 0 但其實沒有更新——30 分鐘後再試，不算成功、也不算「沒有效果」
    /// </summary>
    LockBusy,

    /// <summary>結束碼 0、沒換版的其他說明：套件管理員管理（winget、Homebrew…）、stable 頻道停留、更新目標早於簽章、更新被管理員停用</summary>
    Declined,

    /// <summary>結束碼不是 0（Failed to check for updates、Error: Failed to install native update…）</summary>
    Failed,
}

/// <summary>claude update 輸出的解析結果；Message＝給使用者看的一行說明（沒有為 null）</summary>
internal sealed record ClaudeUpdateResult(ClaudeUpdateKind Kind, string? FromVersion, string? ToVersion, int? LockHolderProcessId, string? Message);

/// <summary>
/// 一次更新指令的判定（以執行前後的實際版本為準）：Switched＝執行檔真的換了版；Error＝失敗的原因（顯示給使用者）；
/// Note＝沒換版時 CLI 自己的說明；RetryLater＝30 分鐘後再試（版本鎖被占住）
/// </summary>
internal sealed record CliUpdateJudgement(bool Switched, string? Error, string? Note, bool RetryLater);

/// <summary>npm install -g 的結果：Succeeded＝結束碼 0；FilesInUse＝失敗原因是檔案被占用（EBUSY／EPERM）；LeftResidue＝成功但退役的舊版刪不掉（npm 只記警告）</summary>
internal sealed record NpmRunVerdict(bool Succeeded, string? Error, bool FilesInUse, bool LeftResidue);

/// <summary>屏障的回覆之後怎麼走（CliUpdateRules.DecidePreparation）</summary>
internal enum CliBarrierDecision
{
    /// <summary>對話都已停下或關掉：往下換檔</summary>
    Proceed,

    /// <summary>自動更新遇到有對話在跑：什麼都不換，安靜地延後再試</summary>
    Defer,

    /// <summary>不換檔並告訴使用者原因（使用者取消、還有對話沒停下）</summary>
    Abort,
}

/// <summary>怎麼更新這種安裝（設計 7.3 的表）</summary>
internal enum CliUpdateRoute
{
    /// <summary>CLI 自己的 update（Claude 原生安裝與其他安裝方式）：換檔用改名，執行中的舊程序照常回應，不需要屏障</summary>
    SelfUpdate,

    /// <summary>app 自管 Codex：下載驗證後舊資料夾改名再放新版，不需要屏障</summary>
    ManagedSwap,

    /// <summary>npm 版（Codex、Claude）：npm install -g 會刪舊執行檔，要走 7.3.1 的全體屏障</summary>
    NpmBarrier,

    /// <summary>WinGet 的 portable 套件：CLI 自己的 update 不換檔，改跑 winget upgrade；它先刪舊執行檔，執行中的會擋住，所以也走屏障</summary>
    WingetBarrier,
}

/// <summary>正在使用某個安裝資料夾的程序（屏障等程序結束、殘留刪不掉時列給使用者）</summary>
internal sealed record CliProcessUse(int ProcessId, string ImagePath, bool InAppJob);

/// <summary>CLI 安裝／更新流程的純函式規則：失敗原因、更新指令的說明、程序是否在安裝資料夾裡、失敗原因何時不再顯示</summary>
internal static partial class CliUpdateRules
{
    /// <summary>「Another Claude process」之後多久再試</summary>
    public static readonly TimeSpan LockBusyRetry = TimeSpan.FromMinutes(30);

    /// <summary>這種安裝的更新方式（7.3）：npm 版要屏障，自管 Codex 換資料夾、其他用 CLI 自己的 update 都不需要</summary>
    public static CliUpdateRoute RouteFor(CliInstallKind kind) => kind switch
    {
        CliInstallKind.Npm => CliUpdateRoute.NpmBarrier,
        CliInstallKind.Managed => CliUpdateRoute.ManagedSwap,
        _ => CliUpdateRoute.SelfUpdate,
    };

    /// <summary>這個安裝的更新方式：WinGet 的 portable 套件走 winget upgrade（屏障），其他照安裝方式</summary>
    public static CliUpdateRoute RouteFor(CliInstallation installation)
        => installation.Winget != null ? CliUpdateRoute.WingetBarrier : RouteFor(installation.Kind);

    /// <summary>
    /// 解析 claude update 的輸出（stdout 與 stderr 一起看；色碼先去掉）。順序：版本鎖被占住（結束碼 0，但沒有更新）→
    /// 結束碼不是 0＝失敗 → 「Successfully updated／downgraded from X to version Y」→「is up to date」→ 其他＝沒有換版的說明
    /// </summary>
    public static ClaudeUpdateResult ParseClaudeUpdate(int exitCode, string? stdout, string? stderr)
    {
        var text = AnsiPattern().Replace((stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty), string.Empty);
        if (LockBusyPattern().Match(text) is { Success: true } busy)
        {
            int? pid = busy.Groups["pid"].Success && int.TryParse(busy.Groups["pid"].Value, out var p) ? p : null;
            return new ClaudeUpdateResult(ClaudeUpdateKind.LockBusy, null, null, pid, FirstMatchingLine(text, LockBusyPattern()));
        }
        if (exitCode != 0)
            return new ClaudeUpdateResult(ClaudeUpdateKind.Failed, null, null, null, DescribeFailure(stdout, stderr));
        if (UpdatedPattern().Match(text) is { Success: true } updated)
            return new ClaudeUpdateResult(ClaudeUpdateKind.Updated, CliVersionRules.ParseVersion(updated.Groups["from"].Value),
                CliVersionRules.ParseVersion(updated.Groups["to"].Value), null, FirstMatchingLine(text, UpdatedPattern()));
        if (UpToDatePattern().Match(text) is { Success: true } upToDate)
        {
            var version = CliVersionRules.ParseVersion(upToDate.Groups["version"].Value);
            return new ClaudeUpdateResult(ClaudeUpdateKind.UpToDate, version, version, null, FirstMatchingLine(text, UpToDatePattern()));
        }
        return new ClaudeUpdateResult(ClaudeUpdateKind.Declined, null, null, null, SummarizeOutput(stdout, stderr));
    }

    /// <summary>
    /// 一次 CLI 自己的 update（claude update）的判定：一律以執行前後的版本資訊（Claude 讀 FileVersionInfo）為準——
    /// 回報成功卻沒換檔（#70039、#74733）算失敗；版本鎖被占住不算成功、30 分鐘後再試；失敗但版本其實換了（外部同時更新）就算換版
    /// </summary>
    public static CliUpdateJudgement JudgeSelfUpdate(ClaudeUpdateResult result, string? versionBefore, string? versionAfter)
    {
        bool switched = versionAfter != null && (versionBefore == null || !CliVersionRules.SameRelease(versionBefore, versionAfter, coreOnly: true));
        switch (result.Kind)
        {
            case ClaudeUpdateKind.LockBusy:
            {
                var holder = result.LockHolderProcessId is int pid ? $"（PID {pid}）" : string.Empty;
                return switched
                    ? new CliUpdateJudgement(true, null, null, false)
                    : new CliUpdateJudgement(false, null, $"另一個 Claude 程序{holder}正在更新 Claude Code，{LockBusyRetry.TotalMinutes:0} 分鐘後再試", RetryLater: true);
            }
            case ClaudeUpdateKind.Failed:
                return switched ? new CliUpdateJudgement(true, null, null, false) : new CliUpdateJudgement(false, $"更新失敗：{result.Message ?? "原因不明"}", null, false);
            case ClaudeUpdateKind.Updated when !switched:
                return new CliUpdateJudgement(false, $"claude update 回報已更新到 {result.ToVersion ?? "新版"}，但執行檔仍是 {versionAfter ?? versionBefore ?? "原本的版本"}", null, false);
            case ClaudeUpdateKind.Updated:
                return new CliUpdateJudgement(true, null, null, false);
            default:
                return new CliUpdateJudgement(switched, null, switched ? null : result.Message, false);
        }
    }

    /// <summary>
    /// npm install -g 的結果：結束碼 0＝成功（舊程序還在用退役的舊版時 npm 只記「cleanup Failed to remove」警告、留下殘留）；
    /// 其他＝失敗，原因取說明行（DescribeFailure），EBUSY／EPERM／「resource busy or locked」＝檔案被占用
    /// </summary>
    public static NpmRunVerdict EvaluateNpm(int exitCode, string? stdout, string? stderr)
    {
        var text = (stdout ?? string.Empty) + "\n" + (stderr ?? string.Empty);
        bool residue = NpmResiduePattern().IsMatch(text);
        if (exitCode == 0)
            return new NpmRunVerdict(true, null, false, residue);
        return new NpmRunVerdict(false, DescribeFailure(stdout, stderr), NpmInUsePattern().IsMatch(text), residue);
    }

    /// <summary>
    /// 屏障的回覆（ICliUpdateParticipant.PrepareForUpdateAsync）之後怎麼走：Ready＝往下換檔；
    /// Ready=false 一律不換檔（還有程序在用舊檔，npm 一定 EBUSY）——自動更新且沒有原因（有對話在跑）＝安靜延後，
    /// 其他＝中止並說明（Reason，例如「已取消更新」；沒有原因就依 Busy 組預設說明）
    /// </summary>
    public static (CliBarrierDecision Decision, string? Reason) DecidePreparation(CliUpdatePreparation preparation, bool automatic, AgentProvider provider)
    {
        if (preparation.Ready)
            return (CliBarrierDecision.Proceed, null);
        if (automatic && preparation.Reason == null)
            return (CliBarrierDecision.Defer, null);
        if (preparation.Reason != null)
            return (CliBarrierDecision.Abort, preparation.Reason);
        var name = CliInstallStabilityRules.DisplayName(provider);
        var busy = preparation.Busy > 0 ? $"還有 {preparation.Busy} 個 {name} 對話在執行或等你回應" : $"{name} 對話還沒停下";
        return (CliBarrierDecision.Abort, $"{busy}，這次沒有更新；請等它們結束（或在 CLI 卡按「現在中斷」）後再更新");
    }

    /// <summary>
    /// 哪些程序的執行檔在這些資料夾底下（屏障等 app 自己的程序結束；殘留刪不掉時列出不在 app 工作物件裡的程序）。
    /// processes：每個程序的 PID、QueryFullProcessImageName 的路徑（打不開為 null）、是否在 app 的工作物件裡
    /// </summary>
    public static IReadOnlyList<CliProcessUse> FindProcessesUnder(IEnumerable<(int ProcessId, string? ImagePath, bool InAppJob)> processes, IReadOnlyList<string> roots)
        => processes.Where(p => p.ImagePath != null && IsUnderAny(p.ImagePath, roots))
            .Select(p => new CliProcessUse(p.ProcessId, p.ImagePath!, p.InAppJob))
            .OrderBy(p => p.ProcessId)
            .ToList();

    /// <summary>殘留刪不掉時給使用者看的說明：列出不在 app 工作物件裡、執行檔在 npm 目錄的程序（最多 5 個），請使用者先結束它們</summary>
    public static string DescribeForeignProcesses(AgentProvider provider, IReadOnlyList<CliProcessUse> foreign, IReadOnlyList<string> residue)
    {
        var name = CliInstallStabilityRules.DisplayName(provider);
        var left = residue.Count == 0 ? "上次更新留下的檔案" : string.Join("、", residue.Select(Path.GetFileName));
        if (foreign.Count == 0)
            return $"無法刪除上次 npm 更新留下的 {left}（可能還有程序在使用舊版 {name}），這次沒有更新；請關閉其他使用 {name} 的程式後再更新";
        var list = string.Join("、", foreign.Take(5).Select(p => $"PID {p.ProcessId} {p.ImagePath}")) + (foreign.Count > 5 ? $" 等 {foreign.Count} 個" : string.Empty);
        return $"還有 app 以外的程序在使用 npm 安裝的 {name}（{list}，例如終端機或 VS Code 裡的 {name}），無法刪除上次更新留下的 {left}，這次沒有更新；請先結束它們再更新";
    }

    /// <summary>
    /// app 要換 npm 版的檔案時，這個安裝正被別的程式改（另一個 npm 正在安裝或更新全域套件、持有中的版本鎖）：給使用者看的說明。
    /// detail 是 CliInstallStabilityRules 判定「安裝正在變動」的說明（例如「npm 正在安裝或更新全域套件（PID 4242）」）
    /// </summary>
    public static string DescribeInstallBusy(AgentProvider provider, string? detail)
    {
        var name = CliInstallStabilityRules.DisplayName(provider);
        var why = string.IsNullOrWhiteSpace(detail) ? string.Empty : $"（{detail.Trim()}）";
        return $"{name} 的安裝正在被其他程式安裝或更新{why}，這次沒有更新；請等它完成後再更新";
    }

    /// <summary>npm 退役的殘留資料夾名稱（.codex-&lt;雜湊&gt;、.claude-code-&lt;雜湊&gt;；在 node_modules\@openai、node_modules\@anthropic-ai 底下）</summary>
    public static bool IsNpmResidue(AgentProvider provider, string? folderName)
        => !string.IsNullOrEmpty(folderName)
           && folderName.StartsWith(provider == AgentProvider.Claude ? ".claude-code-" : ".codex-", StringComparison.OrdinalIgnoreCase);

    /// <summary>npm 版 CLI 的 scope 資料夾（npm 前綴\node_modules\@openai 或 @anthropic-ai）：套件、退役殘留、提升到前綴的平台套件都在這底下</summary>
    public static string NpmScopeDirectory(AgentProvider provider, string npmPrefix)
        => Path.Combine(npmPrefix, "node_modules", provider == AgentProvider.Claude ? "@anthropic-ai" : "@openai");

    /// <summary>app 自己跑 npm 的參數：cmd /d /c call "npm.cmd" install -g 套件@latest（call 讓 npm.cmd 的路徑含空白時引號照常有效；跑使用者實際會用的 npm）</summary>
    public static IReadOnlyList<string> NpmInstallArguments(string npmCmd, string package)
        => new[] { "/d", "/c", "call", npmCmd, "install", "-g", $"{package}@latest" };

    /// <summary>第一個符合的行（去頭尾空白）；沒有回 null</summary>
    private static string? FirstMatchingLine(string text, Regex pattern)
        => Lines(text).FirstOrDefault(l => pattern.IsMatch(l));
    /// <summary>
    /// 安裝／更新失敗時給使用者看的原因。claude update 一開始一定會在 stdout 印「Current version: …」，原因寫在 stderr，
    /// 所以不能取合併輸出的第一行：先找 stderr、再找 stdout 裡含 error／failed 的行（npm 的 code／errno／path 這類細節行與 warn 行跳過），
    /// 找不到就退回只有細節的 error 行、stderr 最後一行、stdout 最後一行
    /// </summary>
    public static string DescribeFailure(string? stdout, string? stderr)
    {
        var candidates = Lines(stderr).Concat(Lines(stdout)).Where(l => !NpmWarnPattern().IsMatch(l)).ToList();
        return candidates.FirstOrDefault(l => ErrorWordPattern().IsMatch(l) && !NpmDetailPattern().IsMatch(l))
            ?? candidates.FirstOrDefault(l => ErrorWordPattern().IsMatch(l))
            ?? Lines(stderr).LastOrDefault()
            ?? Lines(stdout).LastOrDefault()
            ?? "原因不明";
    }

    /// <summary>
    /// 更新指令成功結束、版本卻沒變時，CLI 自己的說明（「Claude Code is up to date (2.1.286)」「Claude is managed by winget. …」）：
    /// 去掉「Current version」「Checking for updates」這類進度行後合併成一行；沒有內容回 null
    /// </summary>
    public static string? SummarizeOutput(string? stdout, string? stderr, int maxLength = 300)
    {
        var lines = Lines(stdout).Concat(Lines(stderr)).Where(l => !ProgressPattern().IsMatch(l)).ToList();
        if (lines.Count == 0)
            return null;
        var text = string.Join(" ", lines);
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    /// <summary>更新指令沒有報錯，但版本沒變、仍有新版（stable 頻道、由 winget 管理、另一個 claude 正在更新…）</summary>
    public static bool IsNoEffectUpdate(string? versionBefore, string? versionAfter, bool updateAvailable)
        => versionBefore != null && string.Equals(versionBefore, versionAfter, StringComparison.Ordinal) && updateAvailable;

    /// <summary>
    /// 查完版本後還要不要顯示上次安裝／更新失敗的原因：版本變了（使用者自己更新、原生版背景自動更新、這次其實已經裝好）
    /// 或已經是最新版就不再顯示；還沒裝好、仍有新版時保留
    /// </summary>
    public static bool KeepActionError(string? actionError, string? versionBefore, string? versionNow, bool installed, bool updateAvailable)
        => actionError != null
           && string.Equals(versionBefore, versionNow, StringComparison.Ordinal)
           && !(installed && !updateAvailable);

    /// <summary>
    /// 向 npm registry 查完最新版後的狀態（本機版本由版本偵測負責，這裡不動 Installation、Version、VersionCheckedFor）：
    /// LastCheckedAt 只記真的查到最新版的時間，CheckedAt 不論成功與否都更新；已是最新版就不再顯示上次的失敗原因
    /// </summary>
    public static CliState AfterLatestCheck(CliState s, string? latest, string? latestError, DateTime now)
    {
        var next = s with
        {
            Latest = latest ?? s.Latest,
            LatestError = latestError,
            LastCheckedAt = latest != null ? now : s.LastCheckedAt,
            CheckedAt = now,
        };
        return KeepActionError(s.ActionError, s.Version, next.Version, next.Installed, next.UpdateAvailable)
            ? next
            : next with { ActionError = null };
    }

    /// <summary>程序的執行檔是否在任一個資料夾底下（不分大小寫；C:\npm 不會誤認 C:\npm2 底下的檔案）</summary>
    public static bool IsUnderAny(string? path, IEnumerable<string> roots)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        foreach (var root in roots)
        {
            var r = root.TrimEnd('\\', '/');
            if (r.Length > 0 && path.Length > r.Length && path.StartsWith(r, StringComparison.OrdinalIgnoreCase) && path[r.Length] is '\\' or '/')
                return true;
        }
        return false;
    }

    private static List<string> Lines(string? text)
        => AnsiPattern().Replace(text ?? string.Empty, string.Empty).Replace("\r", string.Empty).Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    [GeneratedRegex(@"\b(error|err|failed|fatal)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorWordPattern();

    /// <summary>npm 錯誤的細節行（code、errno、syscall、path、dest、log 位置）：真正的說明在別行</summary>
    [GeneratedRegex(@"^npm (error|ERR!) (code|errno|syscall|path|dest|A complete log)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NpmDetailPattern();

    [GeneratedRegex(@"^npm warn", RegexOptions.IgnoreCase)]
    private static partial Regex NpmWarnPattern();

    [GeneratedRegex(@"^(Current version|Checking for updates)", RegexOptions.IgnoreCase)]
    private static partial Regex ProgressPattern();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiPattern();

    /// <summary>「Another Claude process (PID 4242) is currently running.」（PID 可能沒有）</summary>
    [GeneratedRegex(@"Another Claude process(?: \(PID (?<pid>\d+)\))? is currently running", RegexOptions.IgnoreCase)]
    private static partial Regex LockBusyPattern();

    /// <summary>「Successfully updated from 2.1.296 to version 2.1.297」與「Successfully downgraded from … to version …」</summary>
    [GeneratedRegex(@"Successfully (?:updated|downgraded) from (?<from>\S+) to version (?<to>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex UpdatedPattern();

    /// <summary>「Claude Code is up to date (2.1.296)」與舊版的「Claude is up to date!」</summary>
    [GeneratedRegex(@"Claude(?: Code)? is up to date(?: \((?<version>[^)]+)\))?", RegexOptions.IgnoreCase)]
    private static partial Regex UpToDatePattern();

    /// <summary>npm 換檔時檔案被占用（Windows 上執行中的 exe：EBUSY copyfile、EPERM unlink／rename）</summary>
    [GeneratedRegex(@"\b(EBUSY|EPERM)\b|resource busy or locked|operation not permitted", RegexOptions.IgnoreCase)]
    private static partial Regex NpmInUsePattern();

    /// <summary>npm 成功但刪不掉退役的舊版（npm 11／12：「npm warn cleanup Failed to remove some directories」）</summary>
    [GeneratedRegex(@"^npm warn cleanup Failed to remove", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex NpmResiduePattern();
}
