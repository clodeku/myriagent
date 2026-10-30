using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Usage;

namespace Myriagent.Hub;

/// <summary>
/// 換帳戶的候選（中樞在 UI 執行緒從帳戶、登入狀態、模型清單與用量快照整理出來）。
/// Models＝這個帳戶 CLI 回報過的模型（AgentModelOption.Value）；沒回報過為 null。Windows＝用量快照的時間窗；沒查過為 null
/// </summary>
internal sealed record AccountSwitchCandidate(
    string AccountId,
    AgentProvider Provider,
    AccountStatus Status,
    IReadOnlyList<string>? Models,
    IReadOnlyList<UsageWindow>? Windows,
    int Order);

/// <summary>選中的帳戶與它下一次重置的時間（不明為 null）</summary>
internal sealed record AccountSwitchChoice(string AccountId, DateTimeOffset? ResetsAt);

/// <summary>
/// 額度用完時自動換帳戶繼續（設定頁「額度用完時自動換帳戶繼續」，預設開）：對話執行到一半碰到訂閱額度上限（AgentQuotaExhaustedEvent），
/// 改用同一種代理、同一個模型、還有額度的其他帳戶，挑下一次重置最早的（先用掉快要重置、不用就浪費的額度），
/// 以前文節錄＋接續訊息隱藏送出、從中斷處繼續（同手動換帳戶：新帳戶開新 session，CLI 的逐字稿不跨帳戶搬）。
/// </summary>
internal static class AccountSwitchRules
{
    /// <summary>
    /// 執行中能不能手動換帳戶（使用者 2026-10-23：Claude 換另一個 Claude 帳戶、Codex 換另一個 Codex 帳戶；2026-10-24 起
    /// 四種代理之間都能互換）：執行中的 CLI 程序綁著原帳戶，所以是先停下這一輪、這一輪結束時換過去，
    /// 再以前文節錄＋接續訊息從中斷處接著做（同額度用完時的自動換帳戶）；或選「目前步驟完成後換」（<see cref="WhenTurnEnd"/>，
    /// 使用者 2026-10-27 改為等目前步驟完成）：到了安全點就停下交接，不等整個長任務結束。目前與目標兩邊都要能換；會話被停下時要照常回報 Interrupted 的
    /// TurnCompleted（中樞在那時換過去；Grok 送 session/cancel、Antigravity 結束程序樹，都會回報）。新種類要明確決定
    /// </summary>
    public static bool SupportsSwitchWhileRunning(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => true,
        AgentProvider.Codex => true,
        AgentProvider.Antigravity => true,
        AgentProvider.Grok => true,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 執行中從 <paramref name="current"/> 換到 <paramref name="target"/> 這種代理的帳戶，不能換時的原因（可以換回 null）：
    /// 這種代理執行中不能換；換到執行中不能換的代理也不行。目前四種都可以（使用者 2026-10-24）：換到另一種代理時新的一方開新對話、
    /// 以前文節錄接續（同沒在執行時換到另一家）；留著這道檢查給之後新增、還不能換的種類
    /// </summary>
    public static string? WhileRunningBlock(AgentProvider current, AgentProvider target)
    {
        if (!SupportsSwitchWhileRunning(current))
            return "代理執行中不能換帳戶，請先停止";
        if (!SupportsSwitchWhileRunning(target))
        {
            var allowed = string.Join(" 或 ", AgentProviders.All.Where(SupportsSwitchWhileRunning).Select(CliRestartRules.ProductName));
            return $"執行中只能換到 {allowed} 帳戶；要改用 {CliRestartRules.ProductName(target)} 請先停止這一輪";
        }
        return null;
    }

    /// <summary>setAccount 的 when（web-protocol）：立即換（先停下這一輪；不帶 when 也是）</summary>
    public const string WhenNow = "now";

    /// <summary>setAccount 的 when：等目前步驟完成後換；沿用原協定值 turnEnd，2026-10-27 起到安全點即停下交接</summary>
    public const string WhenTurnEnd = "turnEnd";

    /// <summary>setAccount 的 when 是「目前步驟完成後換」；沒帶與其他值都是立即</summary>
    public static bool IsAtTurnEnd(string? when) => string.Equals(when, WhenTurnEnd, StringComparison.Ordinal);

    /// <summary>
    /// setAccount 的 when 明確是 <see cref="WhenNow"/>：閒置、背景工作還在跑時，使用者確認了現在就換（背景工作跟著舊的程序停掉）。
    /// 沒帶 when 的（瀏覽器版、其他電腦看不到這台的背景工作）在這時等背景工作結束才換
    /// </summary>
    public static bool IsExplicitNow(string? when) => string.Equals(when, WhenNow, StringComparison.Ordinal);

    /// <summary>
    /// 執行中手動選的帳戶在這一輪結束時要不要從中斷處接著做（否則只換帳戶、照一般的結束收尾，正常結束時排隊的訊息在新帳戶送）：
    /// 立即換的（先停下這一輪）——這一輪停下或失敗就接著做，停止還沒生效就自己做完了只換帳戶；
    /// 還在等目前步驟完成的（尚未轉成立即換）——只有這一輪因為原帳戶額度用完而失敗時接著做（同額度用完的自動換帳戶，換到使用者選的），
    /// 正常結束、使用者按了停止與其他失敗都照一般的收尾
    /// </summary>
    /// <param name="quotaExhausted">這一輪收到額度用完、使用者沒按停止，而且開著「額度用完時自動換帳戶繼續」</param>
    public static bool ContinuesAfterUserSwitch(bool atTurnEnd, AgentTurnOutcome outcome, bool quotaExhausted)
        => atTurnEnd
            ? outcome == AgentTurnOutcome.Failed && quotaExhausted
            : outcome != AgentTurnOutcome.Completed;

    /// <summary>用完的帳戶不知道何時重置時，多久之內不再換回去（重置時間已知就到重置為止）</summary>
    public static readonly TimeSpan UnknownResetHold = TimeSpan.FromMinutes(30);

    /// <summary>接續訊息最多列幾個中斷前的步驟（最新的）</summary>
    public const int MaxRecentSteps = 12;

    /// <summary>每個步驟的摘要最多幾個字</summary>
    public const int MaxStepChars = 160;

    /// <summary>
    /// 這一種代理能自動換帳戶：四種代理皆支援（Grok 沒有帶內額度錯誤，每一輪非中斷結束後查 _x.ai/billing，鎖定才發 AgentQuotaExhaustedEvent；
    /// Antigravity 只有單一帳戶時，開啟跨代理備援即可自動換到其他代理種類接手）
    /// </summary>
    public static bool Supports(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => true,
        AgentProvider.Codex => true,
        AgentProvider.Antigravity => true,
        AgentProvider.Grok => true,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>用完的帳戶到什麼時候之前不當候選：重置時間已知（而且還沒到）就到重置為止，否則 <see cref="UnknownResetHold"/></summary>
    public static DateTimeOffset ExhaustedUntil(DateTimeOffset? resetsAt, DateTimeOffset now)
        => resetsAt is { } reset && reset > now ? reset : now + UnknownResetHold;

    /// <summary>
    /// 原帳戶的重置時間過了之後再多等多久才換回去（2026-10-26 使用者要求「原本的帳戶解除 session limit 就換回去」）：
    /// 這台與伺服器的時鐘差幾秒，剛到就換回去會馬上又用完、再換走一次
    /// </summary>
    public static readonly TimeSpan ReturnGrace = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 開著 Remote Control 的對話，最後一次動靜之後多久才在閒置時換回原帳戶：可能正從 claude.ai 或 Claude App 在用，換帳戶會關掉程序、
    /// 遠端工作階段跟著結束（同 app 自我更新的 AppUpdateRestartRules.IsRemoteInUse）。從 app 送出時不看這個（人就在這裡）
    /// </summary>
    public static readonly TimeSpan ReturnRemoteIdle = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 自動換到別的帳戶之後，原帳戶（<see cref="QuotaHomeAccount"/>）的額度恢復了、可以換回去：已登入訂閱（Ready）、記憶體裡沒標著用完；
    /// 重置時間已知的過了 <see cref="ReturnGrace"/>，不知道的等用完之後 <see cref="UnknownResetHold"/>，而且要用完之後查過用量、有時間窗
    /// （沒有正面的證據就不換，免得每 30 分鐘換回去一次又馬上用完）；用量顯示這個模型還有額度（重置時間已過的時間窗先歸零，同 <see cref="HasQuota"/>）
    /// </summary>
    public static bool HomeRecovered(QuotaHomeAccount home, AccountStatus status, UsageSnapshot? usage, DateTimeOffset? exhaustedUntil, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(home);
        if (status != AccountStatus.Ready || (exhaustedUntil is { } until && until > now))
            return false;
        if (home.ResetsAt is { } reset)
        {
            if (now < reset + ReturnGrace)
                return false;
        }
        else if (now < home.ExhaustedAt + UnknownResetHold || usage == null || usage.FetchedAt <= home.ExhaustedAt || usage.Windows.Count == 0)
        {
            return false;
        }
        return HasQuota(usage?.Windows, home.Model, now);
    }

    /// <summary>
    /// 送出這則之前可以先換回原帳戶：斜線指令不換（換帳戶後的第一則要附前文節錄，附了就不是指令了，例如 /clear、/compact），等下一則一般的訊息或閒置時再換
    /// </summary>
    public static bool ReturnsBeforeSend(string? text) => !(text?.TrimStart().StartsWith('/') ?? false);

    /// <summary>
    /// 換回原帳戶時要不要還原換走之前的模型與推理強度：換到另一種代理再換回來的一律還原（換過去時已照新對話的預設換掉）；
    /// 同一種代理的只在目前的還是自動換過去之後的（使用者沒自己改過）才還原
    /// </summary>
    public static bool RestoresHomeModel(QuotaHomeAccount home, bool crossProvider, string? currentModel, string? currentEffort)
    {
        ArgumentNullException.ThrowIfNull(home);
        return crossProvider
               || (string.Equals(currentModel, home.SwitchedModel, StringComparison.Ordinal) && string.Equals(currentEffort, home.SwitchedEffort, StringComparison.Ordinal));
    }

    /// <summary>
    /// 換回原帳戶時對話裡的提示（noticeKind accountSwitch）：
    /// 「「A」的額度已恢復，已從「B」換回「A」繼續這個對話；接下來的訊息會附上先前對話的節錄，讓代理了解前情。」
    /// </summary>
    public static string BuildReturnNotice(string homeLabel, string? fromLabel)
    {
        var home = homeLabel.Trim();
        var from = string.IsNullOrWhiteSpace(fromLabel) ? "備援帳戶" : $"「{fromLabel.Trim()}」";
        return $"「{home}」的額度已恢復，已從{from}換回「{home}」繼續這個對話；接下來的訊息會附上先前對話的節錄，讓代理了解前情。";
    }

    /// <summary>模型是「帳戶預設」（沒指定、空白或 default）</summary>
    public static bool IsDefaultModel(string? model)
        => string.IsNullOrWhiteSpace(model) || string.Equals(model.Trim(), "default", StringComparison.OrdinalIgnoreCase);

    /// <summary>帳戶能用這個模型：帳戶預設、帳戶還沒回報模型清單（不知道就不排除），或清單裡有它</summary>
    public static bool SupportsModel(IReadOnlyList<string>? models, string? model)
        => IsDefaultModel(model) || models == null || models.Count == 0 || models.Any(m => string.Equals(m, model!.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 時間窗管不管這個模型：模型專屬的每週上限（key「weekly:Opus」）只管名稱含這個模型的對話（opus、claude-opus-4-…）；
    /// Antigravity 的模型分桶（agy-gemini*／agy-3p* 或標籤含 Gemini／Claude/GPT）只管對應家族的模型；
    /// 帳戶預設模型不知道是哪一個，一律算。其他時間窗（5 小時、每週、Codex 的額度桶、Grok 點數）都算
    /// </summary>
    public static bool AppliesToModel(UsageWindow window, string? model)
    {
        var prefix = UsageRules.WeeklyKey + ":";
        if (window.Key.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (IsDefaultModel(model))
                return true;
            var scope = window.Key[prefix.Length..].Trim();
            return scope.Length == 0 || model!.Contains(scope, StringComparison.OrdinalIgnoreCase);
        }

        if (window.Key.StartsWith(UsageRules.AntigravityKeyPrefix, StringComparison.Ordinal))
        {
            if (IsDefaultModel(model))
                return true;
            var m = model!.Trim();
            bool isGeminiModel = m.Contains("gemini", StringComparison.OrdinalIgnoreCase);
            bool isThirdPartyModel = m.Contains("claude", StringComparison.OrdinalIgnoreCase)
                                     || m.Contains("gpt", StringComparison.OrdinalIgnoreCase)
                                     || m.Contains("o3", StringComparison.OrdinalIgnoreCase)
                                     || m.Contains("o4", StringComparison.OrdinalIgnoreCase);
            bool isGeminiBucket = window.Key.Contains("gemini", StringComparison.OrdinalIgnoreCase)
                                  || (!string.IsNullOrEmpty(window.Label)
                                      && window.Label.Contains("Gemini", StringComparison.OrdinalIgnoreCase)
                                      && !window.Label.Contains("Claude", StringComparison.OrdinalIgnoreCase));
            bool isThirdPartyBucket = window.Key.Contains("3p", StringComparison.OrdinalIgnoreCase)
                                      || window.Key.Contains("claude", StringComparison.OrdinalIgnoreCase)
                                      || (!string.IsNullOrEmpty(window.Label)
                                          && window.Label.Contains("Claude/GPT", StringComparison.OrdinalIgnoreCase));
            if (isGeminiModel && isThirdPartyBucket)
                return false;
            if (isThirdPartyModel && isGeminiBucket)
                return false;
        }

        return true;
    }

    /// <summary>適用的時間窗（重置時間已過的先歸零、解除鎖定，同 UsageRules.ApplyResets）</summary>
    private static List<UsageWindow> Applicable(IReadOnlyList<UsageWindow>? windows, string? model, DateTimeOffset now)
        => windows == null ? new List<UsageWindow>() : UsageRules.ApplyResets(windows, now).Where(w => AppliesToModel(w, model)).ToList();

    /// <summary>還有額度：適用的時間窗都沒有鎖定、使用率未滿 100%。沒有用量資料（沒查過、查不到）不算沒額度</summary>
    public static bool HasQuota(IReadOnlyList<UsageWindow>? windows, string? model, DateTimeOffset now)
        => Applicable(windows, model, now).All(w => !w.Locked && w.Percent < 100);

    /// <summary>下一次重置：適用的時間窗裡最早的（還沒到的）重置時間；都不知道回 null（例如 5 小時窗還沒開始、從沒查過用量）</summary>
    public static DateTimeOffset? NextReset(IReadOnlyList<UsageWindow>? windows, string? model, DateTimeOffset now)
    {
        DateTimeOffset? earliest = null;
        foreach (var w in Applicable(windows, model, now))
        {
            if (w.ResetsAt is { } reset && reset > now && (earliest == null || reset < earliest))
                earliest = reset;
        }
        return earliest;
    }

    /// <summary>用完的帳戶何時能再用：鎖定或用滿的適用時間窗裡最晚的重置時間；看不出來回 null</summary>
    public static DateTimeOffset? ExhaustedResetsAt(IReadOnlyList<UsageWindow>? windows, string? model, DateTimeOffset now)
    {
        DateTimeOffset? latest = null;
        foreach (var w in Applicable(windows, model, now))
        {
            if ((w.Locked || w.Percent >= 100) && w.ResetsAt is { } reset && reset > now && (latest == null || reset > latest))
                latest = reset;
        }
        return latest;
    }

    /// <summary>
    /// 挑接手的帳戶：優先挑同一種代理、不是目前的帳戶、已登入訂閱（Ready；API Key 計費、未登入、檢查中都不算）、最近沒有用完（exhaustedUntil）、
    /// 能用這個模型、還有額度。同種沒有合格的且 <paramref name="allowCrossProvider"/> 為 true 時，再挑其他代理種類裡已登入訂閱且還有額度的帳戶
    /// （跨家不看原對話的模型名；Antigravity 預設以 gemini 分桶看額度）。依下一次重置由早到晚，重置時間不明的排最後；
    /// 同時重置的取使用率最高的時間窗較低者，再照帳戶順序。沒有合格的回 null
    /// </summary>
    public static AccountSwitchChoice? Choose(AgentProvider provider, string currentAccountId, string? model, IEnumerable<AccountSwitchCandidate> candidates,
        IReadOnlyDictionary<string, DateTimeOffset> exhaustedUntil, DateTimeOffset now, bool allowCrossProvider = false)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(exhaustedUntil);
        var list = candidates as IReadOnlyList<AccountSwitchCandidate> ?? candidates.ToList();
        var same = list
            .Where(c => c.Provider == provider
                        && !string.Equals(c.AccountId, currentAccountId, StringComparison.Ordinal)
                        && c.Status == AccountStatus.Ready
                        && !(exhaustedUntil.TryGetValue(c.AccountId, out var until) && until > now)
                        && SupportsModel(c.Models, model)
                        && HasQuota(c.Windows, model, now))
            .Select(c => (Candidate: c, Reset: NextReset(c.Windows, model, now), Peak: Applicable(c.Windows, model, now).Select(w => w.Percent).DefaultIfEmpty(0).Max()))
            .OrderBy(x => x.Reset.HasValue ? 0 : 1)
            .ThenBy(x => x.Reset ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.Peak)
            .ThenBy(x => x.Candidate.Order)
            .ThenBy(x => x.Candidate.AccountId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (same.Candidate != null)
            return new AccountSwitchChoice(same.Candidate.AccountId, same.Reset);
        if (!allowCrossProvider)
            return null;

        var cross = list
            .Where(c => c.Provider != provider
                        && !string.Equals(c.AccountId, currentAccountId, StringComparison.Ordinal)
                        && c.Status == AccountStatus.Ready
                        && !(exhaustedUntil.TryGetValue(c.AccountId, out var until) && until > now))
            .Select(c =>
            {
                var crossModel = c.Provider == AgentProvider.Antigravity ? "gemini" : null;
                return (Candidate: c, Model: crossModel);
            })
            .Where(x => HasQuota(x.Candidate.Windows, x.Model, now))
            .Select(x => (
                x.Candidate,
                Reset: NextReset(x.Candidate.Windows, x.Model, now),
                Peak: Applicable(x.Candidate.Windows, x.Model, now).Select(w => w.Percent).DefaultIfEmpty(0).Max()))
            .OrderBy(x => x.Reset.HasValue ? 0 : 1)
            .ThenBy(x => x.Reset ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.Peak)
            .ThenBy(x => x.Candidate.Order)
            .ThenBy(x => x.Candidate.AccountId, StringComparer.Ordinal)
            .FirstOrDefault();
        return cross.Candidate == null ? null : new AccountSwitchChoice(cross.Candidate.AccountId, cross.Reset);
    }

    /// <summary>
    /// 對話裡的提示（隱藏送出的那則 Notice，noticeKind accountSwitch）：
    /// 「「A」的額度已用完（10/24 06:10 重置），已自動改用「B」（2 小時 5 分後重置）繼續這個對話；新帳戶會收到先前對話的節錄，從中斷處接著做。」
    /// <paramref name="homeLabel"/> 不為 null（原帳戶的額度恢復後會換回去）時接著「「A」的額度恢復後會自動換回去。」
    /// </summary>
    public static string BuildNotice(string? fromLabel, DateTimeOffset? fromResetsAt, string toLabel, DateTimeOffset? toResetsAt, DateTimeOffset now, TimeZoneInfo? zone = null,
        string? homeLabel = null)
    {
        var from = string.IsNullOrWhiteSpace(fromLabel) ? "原帳戶" : $"「{fromLabel.Trim()}」";
        var fromReset = fromResetsAt is { } r && r > now ? $"（{FormatLocal(r, zone)} 重置）" : string.Empty;
        var toReset = toResetsAt is { } t && t > now ? $"（{UsageRules.DescribeReset(t, now)}）" : string.Empty;
        var back = string.IsNullOrWhiteSpace(homeLabel) ? string.Empty : $"「{homeLabel.Trim()}」的額度恢復後會自動換回去。";
        return $"{from}的額度已用完{fromReset}，已自動改用「{toLabel.Trim()}」{toReset}繼續這個對話；新帳戶會收到先前對話的節錄，從中斷處接著做。{back}";
    }

    /// <summary>沒有可以接手的帳戶時的提示（只在有其他同種帳戶，或開啟跨代理備援且有其他帳戶時加，只用一個帳戶的人不必看到）</summary>
    public static string BuildNoCandidateNotice(AgentProvider provider, bool crossProvider = false)
        => crossProvider
            ? "其他已設定的代理帳戶也沒有可用的額度（或沒有登入），沒有自動換帳戶；可以等額度重置後按送出繼續。"
            : $"其他 {CliRestartRules.ProductName(provider)} 帳戶也沒有可用的額度（或沒有登入、不支援這個模型），沒有自動換帳戶；可以等額度重置後按送出繼續。";

    /// <summary>
    /// 給接手帳戶的接續訊息（接在前文節錄後面隱藏送出，不顯示成使用者訊息）：誰為什麼換了帳戶（不是使用者中斷）、新帳戶看不到先前的工具結果、
    /// 中斷前這一輪做過的步驟（已完成的不要重做，先確認實際狀態）、背景工作已停止、使用者沒有送新訊息。
    /// <paramref name="fromProvider"/> 與 <paramref name="provider"/> 不同時寫成跨代理備援
    /// </summary>
    public static string BuildContinuation(AgentProvider provider, IReadOnlyList<string> recentSteps, bool backgroundWorkStopped, AgentProvider? fromProvider = null)
        => Continuation(fromProvider is { } from && from != provider
            ? $"（系統通知：原本的 {CliRestartRules.ProductName(from)} 帳戶訂閱額度用完，這一輪在中途停下；Clode Myriagent 已自動改用 {CliRestartRules.ProductName(provider)} 帳戶接續這個對話，這不是使用者中斷。"
            : $"（系統通知：原本的帳戶訂閱額度用完，這一輪在中途停下；Clode Myriagent 已自動改用另一個 {CliRestartRules.ProductName(provider)} 帳戶接續這個對話，這不是使用者中斷。",
            recentSteps, backgroundWorkStopped);

    /// <summary>執行中手動換帳戶時的提示（對話裡的 noticeKind accountSwitch）</summary>
    public static string BuildUserSwitchNotice(string? fromLabel, string toLabel)
    {
        var from = string.IsNullOrWhiteSpace(fromLabel) ? "原帳戶" : $"「{fromLabel.Trim()}」";
        return $"已從{from}改用「{toLabel.Trim()}」繼續：這一輪在中途停下，新帳戶會收到先前對話的節錄，從中斷處接著做。";
    }

    /// <summary>選了「這一輪結束時才換」、這一輪卻因為原帳戶額度用完而失敗時的提示（接著由選的帳戶從中斷處做）</summary>
    public static string BuildChosenAfterQuotaNotice(string? fromLabel, string toLabel)
    {
        var from = string.IsNullOrWhiteSpace(fromLabel) ? "原帳戶" : $"「{fromLabel.Trim()}」";
        return $"{from}的額度已用完，這一輪在中途停下；改用你選的「{toLabel.Trim()}」繼續，新帳戶會收到先前對話的節錄，從中斷處接著做。";
    }

    /// <summary>
    /// 執行中手動換帳戶時給新帳戶的接續訊息（同 <see cref="BuildContinuation"/>，第一句換成使用者在執行中換了帳戶：
    /// 停下這一輪是為了換帳戶，不是要放棄這一輪的工作）。<paramref name="fromProvider"/> 是另一種代理時寫成換了代理（四家代理互換）
    /// </summary>
    public static string BuildUserSwitchContinuation(AgentProvider provider, IReadOnlyList<string> recentSteps, bool backgroundWorkStopped, AgentProvider? fromProvider = null)
        => Continuation(fromProvider is { } from && from != provider
            ? $"（系統通知：使用者在這一輪執行中把這個對話從 {CliRestartRules.ProductName(from)} 改用 {CliRestartRules.ProductName(provider)}，原本的這一輪在中途停下；" +
              $"Clode Myriagent 已改用 {CliRestartRules.ProductName(provider)} 接續這個對話，停下是為了換代理，不是要放棄原本的工作。"
            : $"（系統通知：使用者在這一輪執行中把帳戶換成另一個 {CliRestartRules.ProductName(provider)} 帳戶，舊帳戶的這一輪在中途停下；" +
              "Clode Myriagent 已改用新帳戶接續這個對話，停下是為了換帳戶，不是要放棄原本的工作。", recentSteps, backgroundWorkStopped);

    /// <summary>
    /// 選了「這一輪結束時才換」、這一輪卻因為原帳戶額度用完而失敗時給選的帳戶的接續訊息（同 <see cref="BuildContinuation"/>，
    /// 寫成改用使用者事先選的帳戶；另一種代理時寫成改用那種代理）
    /// </summary>
    public static string BuildChosenAfterQuotaContinuation(AgentProvider fromProvider, AgentProvider provider, IReadOnlyList<string> recentSteps, bool backgroundWorkStopped)
        => Continuation(fromProvider == provider
            ? $"（系統通知：原本的帳戶訂閱額度用完，這一輪在中途停下；Clode Myriagent 已改用使用者事先選的另一個 {CliRestartRules.ProductName(provider)} 帳戶接續這個對話，這不是使用者中斷。"
            : $"（系統通知：原本的 {CliRestartRules.ProductName(fromProvider)} 帳戶訂閱額度用完，這一輪在中途停下；Clode Myriagent 已依使用者事先的選擇改用 {CliRestartRules.ProductName(provider)} 接續這個對話，這不是使用者中斷。",
            recentSteps, backgroundWorkStopped);

    /// <summary>
    /// 使用者選了要換的帳戶、還有背景工作在跑，等它們結束才換（一輪結束時才換的，或閒置時沒有確認現在就換的）時對話裡的提示
    /// </summary>
    public static string BuildDeferredNotice(int backgroundCount, string toLabel)
    {
        var count = backgroundCount > 1 ? $" {backgroundCount} 個" : string.Empty;
        return $"還有{count}背景工作在跑：換帳戶會關掉程序、背景工作跟著停，所以等它們都結束、代理處理完結果之後才改用「{toLabel.Trim()}」。" +
               "要現在就換，在帳戶選單再選一次並選「現在換」；選回目前的帳戶就不換。";
    }

    /// <summary>等背景工作結束才換的帳戶已經不能換（移除、登出、沒安裝）時的提示</summary>
    public static string BuildDeferredDroppedNotice(string? toLabel)
        => $"背景工作都結束了，但{(string.IsNullOrWhiteSpace(toLabel) ? "選的帳戶" : $"「{toLabel.Trim()}」")}已經不能用（移除、登出或沒安裝），沒有換帳戶。";

    /// <summary>閒置、背景工作還在跑時使用者確認了現在就換：對話裡的提示（noticeKind accountSwitch）</summary>
    public static string BuildIdleSwitchStoppedNotice(string? fromLabel, string toLabel, int backgroundCount)
    {
        var from = string.IsNullOrWhiteSpace(fromLabel) ? "原帳戶" : $"「{fromLabel.Trim()}」";
        var count = backgroundCount > 1 ? $" {backgroundCount} 個" : string.Empty;
        return $"已從{from}改用「{toLabel.Trim()}」繼續：還在跑的{count}背景工作跟著舊的程序停了，新帳戶會收到先前對話的節錄，確認背景工作做到哪裡再接著做。";
    }

    /// <summary>
    /// 閒置、背景工作還在跑時使用者確認了現在就換：給新帳戶的接續訊息（同 <see cref="BuildContinuation"/>，第一句寫使用者在等背景工作時換了帳戶，
    /// 另列出被停掉的背景工作）。<paramref name="fromProvider"/> 是另一種代理時寫成換了代理
    /// </summary>
    public static string BuildIdleSwitchContinuation(AgentProvider fromProvider, AgentProvider provider, IReadOnlyList<string> recentSteps, IReadOnlyList<string> stoppedBackground)
        => Continuation(fromProvider != provider
            ? $"（系統通知：上一輪結束後還有背景工作在跑，使用者選了現在就把這個對話從 {CliRestartRules.ProductName(fromProvider)} 改用 {CliRestartRules.ProductName(provider)}；" +
              $"換代理關掉了舊的程序，背景工作跟著停止。Clode Myriagent 已改用 {CliRestartRules.ProductName(provider)} 接續這個對話。"
            : $"（系統通知：上一輪結束後還有背景工作在跑，使用者選了現在就把帳戶換成另一個 {CliRestartRules.ProductName(provider)} 帳戶；" +
              "換帳戶關掉了舊的程序，背景工作跟著停止。Clode Myriagent 已改用新帳戶接續這個對話。", recentSteps, backgroundWorkStopped: true, stoppedBackground);

    /// <summary>被停掉的背景工作的一行說明（工具名稱：摘要的第一行，最多 <see cref="MaxStepChars"/> 字、<see cref="MaxRecentSteps"/> 個）</summary>
    public static List<string> BackgroundSteps(IReadOnlyList<AgentBackgroundTool>? tools)
    {
        var steps = new List<string>();
        foreach (var tool in tools ?? Array.Empty<AgentBackgroundTool>())
        {
            if (steps.Count >= MaxRecentSteps)
                break;
            var name = string.IsNullOrWhiteSpace(tool.ToolName) ? "背景工作" : tool.ToolName.Trim();
            var title = ClaudeStreamRules.FirstLine(tool.Title);
            var step = title.Length == 0 ? name : $"{name}：{title}";
            steps.Add(step.Length > MaxStepChars ? step[..MaxStepChars] + "…" : step);
        }
        return steps;
    }

    private static string Continuation(string first, IReadOnlyList<string> recentSteps, bool backgroundWorkStopped, IReadOnlyList<string>? stoppedBackground = null)
    {
        var lines = new List<string> { first + "上面是先前對話的節錄，你看不到先前的工具結果與讀過的檔案內容。" };
        var steps = (recentSteps ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (steps.Count > 0)
            lines.Add("使用者最後一則訊息之後已經做過的步驟（由舊到新）：" + string.Join("、", steps.Select(s => $"〈{s}〉")) + "。");
        lines.Add("已完成的步驟不要重做；先確認目前的實際狀態（例如相關檔案、git 狀態），再從中斷處繼續原本的工作。");
        var stopped = (stoppedBackground ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (stopped.Count > 0)
            lines.Add("被停掉的背景工作：" + string.Join("、", stopped.Select(s => $"〈{s}〉")) + "。");
        if (backgroundWorkStopped)
            lines.Add("原本還在跑的背景工作已隨舊的程序停止，結果未知；可安全重跑才重跑，不可重複的操作（例如部署、發布、付款）不要直接重跑。");
        lines.Add("使用者沒有另外傳送新訊息；請繼續原本的工作並回報。）");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 中斷前做過的步驟：最後一則使用者訊息之後的工具（名稱：摘要的第一行，由舊到新，最多 <see cref="MaxRecentSteps"/> 個最新的）；
    /// 被停止或失敗的工具標「（未完成）」
    /// </summary>
    public static List<string> RecentSteps(IReadOnlyList<AgentChatItem> items)
    {
        var steps = new List<string>();
        if (items == null)
            return steps;
        for (int i = items.Count - 1; i >= 0 && steps.Count < MaxRecentSteps; i--)
        {
            var item = items[i];
            if (item.Kind == AgentItemKind.User || AgentHubRules.IsClearNotice(item))
                break;
            if (item.Kind != AgentItemKind.Tool)
                continue;
            var name = string.IsNullOrWhiteSpace(item.ToolName) ? "工具" : item.ToolName.Trim();
            var title = ClaudeStreamRules.FirstLine(item.Title);
            var step = title.Length == 0 ? name : $"{name}：{title}";
            if (step.Length > MaxStepChars)
                step = step[..MaxStepChars] + "…";
            if (item.Status is AgentItemStatus.Cancelled or AgentItemStatus.Failed or AgentItemStatus.Declined)
                step += "（未完成）";
            steps.Add(step);
        }
        steps.Reverse();
        return steps;
    }

    private static string FormatLocal(DateTimeOffset time, TimeZoneInfo? zone)
        => TimeZoneInfo.ConvertTime(time, zone ?? TimeZoneInfo.Local).ToString("M/d HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
