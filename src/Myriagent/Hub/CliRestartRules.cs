using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Rendering;

namespace Myriagent.Hub;

/// <summary>對話在 CLI 換版當下（或重新檢查時）的狀態：設計 7.4.1 政策表的列</summary>
internal enum CliRestartSituation
{
    /// <summary>沒有程序（下次送出自然用新版）</summary>
    NoProcess,

    /// <summary>閒置、程序活著、沒有存活的背景工作</summary>
    Idle,

    /// <summary>閒置、有存活的背景工作（Claude run_in_background 還沒收到 task_notification）</summary>
    IdleWithBackground,

    /// <summary>執行中、在安全點（沒有工具在執行、沒有等你回應、沒有存活的背景工作）</summary>
    AtSafePoint,

    /// <summary>執行中、有工具在執行</summary>
    ToolRunning,

    /// <summary>執行中、沒有工具在執行但背景工作還在跑</summary>
    BackgroundRunning,

    /// <summary>執行中、等使用者回應（核准、問卷、計畫確認）</summary>
    WaitingForUser,
}

/// <summary>中樞對這個對話要做的事（政策表的格子）</summary>
internal enum CliRestartAction
{
    /// <summary>不做事</summary>
    None,

    /// <summary>立即 CloseAsync：下次送出自然以新版續接，不送接續訊息（上一輪已完成時接續只會白耗一輪）</summary>
    Close,

    /// <summary>以 RestartAsync 重開並自動接續（Settings＝新設定）</summary>
    Restart,

    /// <summary>更新屏障：以 RestartAsync(Settings: null) 停下，等 UpdateFinished 後再以新版重開接續</summary>
    Stop,

    /// <summary>等背景工作結束（超過時限提示使用者）</summary>
    WaitBackground,

    /// <summary>等工具結束後的安全點</summary>
    WaitTool,

    /// <summary>等使用者回應之後的安全點（卡片在程序重開後會失效，所以不打斷）</summary>
    WaitUser,

    /// <summary>等這一輪結束再關程序（AfterTurn 政策，或會話還不支援中途重開時的退路）</summary>
    WaitTurn,

    /// <summary>不主動處理：下次送出時才用新版（NextSend 政策）</summary>
    NextSend,
}

/// <summary>對話的換版狀態（協定 HeaderRestart.state，見 docs/web-protocol.md）</summary>
internal enum CliRestartState
{
    WaitingTool,
    WaitingUser,
    WaitingBackground,
    WaitingTurn,
    WaitingUpdate,
    Restarting,
    Continued,
    NotContinued,
    Failed,
    NextSend,

    /// <summary>已停下，等 Clode Myriagent 本身更新後由新版自動接續（app 自我更新的排空）</summary>
    AppUpdate,
}

/// <summary>為什麼被打斷的那一輪不自動接續（設計 7.7）</summary>
internal enum CliNoContinueReason
{
    /// <summary>使用者自己按了停止</summary>
    UserStopped,

    /// <summary>上一輪已完成、處於閒置：只關程序</summary>
    Idle,

    /// <summary>中斷距今超過 1 小時（同 CLI 的 CLAUDE_CODE_RESUME_INTERRUPTED_TURN_MAX_AGE_MS 預設）</summary>
    TooOld,

    /// <summary>上一輪因額度用完或 API 錯誤而結束</summary>
    PreviousTurnFailed,

    /// <summary>上一次接續（續接 session）失敗</summary>
    ContinuationFailed,

    /// <summary>被打斷的是不可重複的高風險操作（git push、npm publish…，見 CliRestartRules.FindRisk）</summary>
    RiskyTool,

    /// <summary>中樞要求接續，但會話沒有接續（CLI 認為這一輪已不在執行中）</summary>
    Unknown,
}

/// <summary>符合的高風險規則</summary>
/// <param name="RuleId">規則 id（預設規則的 id，例如 "git-push"；使用者加的樣式是 "custom"）</param>
/// <param name="Description">規則的說明</param>
/// <param name="Operation">被打斷的操作（一行摘要，給提示文字用）</param>
internal sealed record CliRiskMatch(string RuleId, string Description, string Operation);

/// <summary>
/// CLI 熱更新的純函式規則：政策表（設計 7.4.1）、不自動接續的情況與高風險操作（7.7）、標頭與 Notice 的文字（7.8）。
/// 中樞（AgentHub.Restart.cs）只負責收集狀態與執行規則決定的動作。
/// </summary>
internal static class CliRestartRules
{
    /// <summary>
    /// 換版（CLI 換版與 app 自我更新）最多等背景工作多久（2026-10-24 使用者要求：背景工作不論跑多久、做完沒有，都不能一直擋住重啟）：
    /// 從排定換版起算，超過就照立即套用停下（背景工作跟著停止），重開後馬上請代理確認背景工作做到哪裡、需要的話重新執行
    /// （app 更新由新版照 running.json 接續；CLI 換版由中樞隱藏送出，AgentHub.Restart.cs 的 BackgroundCheck）
    /// </summary>
    public static readonly TimeSpan BackgroundWaitLimit = TimeSpan.FromMinutes(30);

    /// <summary>等背景工作已經等滿上限（從排定換版起算）：照立即套用停下</summary>
    public static bool BackgroundWaitExpired(DateTime since, DateTime now) => now - since >= BackgroundWaitLimit;

    /// <summary>
    /// 標頭要不要帶開始等待的時間（HeaderRestart.since，網頁顯示「已等 3:25」；2026-10-25 使用者要求）：還在等的狀態（等工具、等你回應、
    /// 等背景工作、等這一輪結束）與已停下等更新的（waitingUpdate、appUpdate）帶，從排定換版起算（同 <see cref="BackgroundWaitLimit"/>）；
    /// 重新啟動中與處理完的結果不帶
    /// </summary>
    public static bool ShowsWaitTime(CliRestartState state) => state is CliRestartState.WaitingTool or CliRestartState.WaitingUser
        or CliRestartState.WaitingBackground or CliRestartState.WaitingTurn or CliRestartState.WaitingUpdate or CliRestartState.AppUpdate;

    /// <summary>更新屏障排空的寬限（超過就在 CLI 卡問使用者要等還是現在中斷）</summary>
    public static readonly TimeSpan DrainGrace = TimeSpan.FromMinutes(10);

    /// <summary>中斷超過這麼久就不自動接續</summary>
    public static readonly TimeSpan MaxInterruptionAge = TimeSpan.FromHours(1);

    /// <summary>產生卡片的 CLI 程序重開（或結束）後，還在等待的卡片改為 Cancelled 時的 output（協定固定文字）</summary>
    public const string StaleCardOutput = "CLI 已重新啟動，這張卡已失效；代理會視需要重新提出";

    /// <summary>
    /// 還在等核准就被重開停下的工具卡（核准卡同時失效）改為 Cancelled 時的 output（協定固定文字）：那個操作沒有核准、沒有執行，
    /// 不是「被中斷、結果未知」（InterruptedToolOutput）
    /// </summary>
    public const string NotApprovedToolOutput = "CLI 已重新啟動，這個操作還沒有核准，沒有執行；代理會視需要重新提出";

    /// <summary>使用者按「繼續」時對話裡加的 Notice（接續訊息本身不顯示成使用者訊息）</summary>
    public const string ManualContinueNotice = "已請代理從中斷處繼續（CLI 更新後的接續訊息，不是你送的）";

    /// <summary>重開中的對話（RestartAsync 進行中）在標頭的狀態文字（不講「新版」：取消、沒換版的更新與放棄的 app 更新也以同一版重開）</summary>
    public const string RestartingStatus = "正在重新啟動…";

    /// <summary>自動接續的一輪開始時的狀態文字（同上，版本由標頭的換版列說明）</summary>
    public const string ContinuedStatus = "已自動接續…";

    /// <summary>
    /// 同時最多幾個程序在啟動中（Claude 每個約 1.1 秒、執行檔約 250 MB；Codex 約 0.17 秒；agy 執行檔約 190 MB、每個程序另起 language server）
    /// </summary>
    public static int MaxConcurrentStarts(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => 2,
        AgentProvider.Codex => 4,
        AgentProvider.Antigravity => 2,
        AgentProvider.Grok => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    public static string ProductName(AgentProvider provider) => AgentProviders.ProductName(provider);

    // ───────────────────────────── 政策表 ─────────────────────────────

    /// <summary>
    /// 依會話的快照判斷對話在政策表的哪一列。running＝這一輪在進行中（中樞的 c.Running）；
    /// atSafePoint＝IAgentSession.IsAtSafePoint；其他三個旗標只用來分辨「不在安全點」的原因（等你回應優先，再來是工具、背景工作）
    /// </summary>
    public static CliRestartSituation Classify(bool hasLiveProcess, bool running, bool atSafePoint, bool toolRunning, bool waitingForUser, bool backgroundWork)
    {
        if (!hasLiveProcess)
            return CliRestartSituation.NoProcess;
        if (!running)
            return backgroundWork ? CliRestartSituation.IdleWithBackground : CliRestartSituation.Idle;
        if (atSafePoint)
            return CliRestartSituation.AtSafePoint;
        if (waitingForUser)
            return CliRestartSituation.WaitingForUser;
        if (toolRunning)
            return CliRestartSituation.ToolRunning;
        // 不在安全點又沒說原因：有背景工作就是它，否則保守地當成工具還在執行
        return backgroundWork ? CliRestartSituation.BackgroundRunning : CliRestartSituation.ToolRunning;
    }

    /// <summary>
    /// 設計 7.4.1 的政策表：這個對話現在要怎麼做。
    /// </summary>
    /// <param name="drain">必須排空（app 要換掉 npm 版的檔案：更新屏障）。不看重開政策：npm 不能在程序執行中換檔，執行中的到安全點停下等更新</param>
    /// <param name="applyNow">使用者按了「立即套用」（或屏障的「現在中斷」）：不等安全點</param>
    /// <param name="canRestart">會話支援以 RestartAsync 重開（回過 NotSupported 就是 false：退回「等這一輪結束後關程序」）</param>
    /// <param name="remoteControl">
    /// 程序開著 Claude Remote Control（IAgentSession.HasRemoteControl）：閒置的不關（關了 claude.ai 與 Claude App 上的工作階段就斷線），
    /// 改成下次送出時才用新版；排空與立即套用照常關（app 更新也是排空：app 本身要結束，程序留不住——中樞把它記進 resume.json，
    /// 新版重開程序接回）。app 更新的排空遇到它在等你回應不當成安全點（手機上可能正要回應）
    /// </param>
    /// <param name="appUpdate">
    /// 排空是為了 Clode Myriagent 本身換版（設計 9.2）：等你回應也當成安全點直接停下；開著 Remote Control 的照 CLI 更新等回應，
    /// 排空滿 DrainGrace 才由中樞以 applyNow 停下（AgentHub.ApplyNowForAppOverdue）
    /// </param>
    public static CliRestartAction Decide(CliRestartPolicy policy, CliRestartSituation situation, bool drain, bool applyNow, bool canRestart = true,
        bool remoteControl = false, bool appUpdate = false)
    {
        if (situation == CliRestartSituation.NoProcess)
            return CliRestartAction.None;

        // 下次送出時才用新版：不主動關任何程序（排空與立即套用除外）
        if (policy == CliRestartPolicy.NextSend && !drain && !applyNow)
            return CliRestartAction.NextSend;

        // 開著 Remote Control 的閒置程序要隨時能從手機接手：不關，下次送出才用新版（執行中的照政策重開，新程序會接回同一個遠端工作階段）
        if (remoteControl && situation == CliRestartSituation.Idle && !drain && !applyNow)
            return CliRestartAction.NextSend;

        if (!canRestart)
        {
            return situation switch
            {
                CliRestartSituation.Idle => CliRestartAction.Close,
                CliRestartSituation.IdleWithBackground => applyNow ? CliRestartAction.Close : CliRestartAction.WaitBackground,
                _ => CliRestartAction.WaitTurn,
            };
        }

        if (drain)
        {
            return situation switch
            {
                CliRestartSituation.Idle => CliRestartAction.Close,
                CliRestartSituation.AtSafePoint => CliRestartAction.Stop,
                _ when applyNow => CliRestartAction.Stop,
                CliRestartSituation.IdleWithBackground or CliRestartSituation.BackgroundRunning => CliRestartAction.WaitBackground,
                // app 更新只在使用者閒置時開始（AppUpdateRestartRules）：等回應的卡片照「立即套用」作廢、接續後由代理重新提出。
                // 開著 Remote Control 的例外：桌面沒人時使用者可能正在手機上要回應，照 CLI 更新等回應
                CliRestartSituation.WaitingForUser => appUpdate && !remoteControl ? CliRestartAction.Stop : CliRestartAction.WaitUser,
                _ => CliRestartAction.WaitTool,
            };
        }

        if (applyNow)
            return situation == CliRestartSituation.Idle ? CliRestartAction.Close : CliRestartAction.Restart;

        return policy switch
        {
            CliRestartPolicy.AfterTurn => situation switch
            {
                CliRestartSituation.Idle => CliRestartAction.Close,
                CliRestartSituation.IdleWithBackground => CliRestartAction.WaitBackground,
                _ => CliRestartAction.WaitTurn,
            },
            _ => situation switch
            {
                CliRestartSituation.Idle => CliRestartAction.Close,
                CliRestartSituation.IdleWithBackground or CliRestartSituation.BackgroundRunning => CliRestartAction.WaitBackground,
                CliRestartSituation.AtSafePoint => CliRestartAction.Restart,
                CliRestartSituation.WaitingForUser => CliRestartAction.WaitUser,
                _ => CliRestartAction.WaitTool,
            },
        };
    }

    /// <summary>等待類動作對應的標頭狀態；其他動作沒有對應的等待狀態（null）</summary>
    public static CliRestartState? WaitingStateOf(CliRestartAction action) => action switch
    {
        CliRestartAction.WaitBackground => CliRestartState.WaitingBackground,
        CliRestartAction.WaitTool => CliRestartState.WaitingTool,
        CliRestartAction.WaitUser => CliRestartState.WaitingUser,
        CliRestartAction.WaitTurn => CliRestartState.WaitingTurn,
        _ => null,
    };

    /// <summary>協定的 state 字串（HeaderRestart.state、CliView.restartWaiting[].state）</summary>
    public static string ToProtocol(CliRestartState state) => state switch
    {
        CliRestartState.WaitingTool => "waitingTool",
        CliRestartState.WaitingUser => "waitingUser",
        CliRestartState.WaitingBackground => "waitingBackground",
        CliRestartState.WaitingTurn => "waitingTurn",
        CliRestartState.WaitingUpdate => "waitingUpdate",
        CliRestartState.Restarting => "restarting",
        CliRestartState.Continued => "continued",
        CliRestartState.NotContinued => "notContinued",
        CliRestartState.Failed => "failed",
        CliRestartState.AppUpdate => "appUpdate",
        _ => "nextSend",
    };

    /// <summary>協定的 CliView.update.phase（小駝峰）；None＝沒有在更新（null）</summary>
    public static string? ToProtocol(CliUpdatePhase phase) => phase switch
    {
        CliUpdatePhase.Draining => "draining",
        CliUpdatePhase.WaitingForProcesses => "waitingProcesses",
        CliUpdatePhase.Installing => "installing",
        CliUpdatePhase.Verifying => "verifying",
        _ => null,
    };

    /// <summary>協定的 CliView.lastChange.origin</summary>
    public static string ToProtocol(CliChangeOrigin origin) => origin == CliChangeOrigin.App ? "app" : "external";

    /// <summary>協定的 Item.noticeKind（小駝峰）：對照在 Protocol 的 ProtocolValues（共用渲染器與伺服器也用），這裡只轉呼叫</summary>
    public static string ToProtocol(AgentNoticeKind kind) => ProtocolValues.ToProtocol(kind);

    /// <summary>協定的 Settings.cliRestartPolicy／CliView.restartPolicy（照協定是列舉名稱：SafePoint、AfterTurn、NextSend）</summary>
    public static string ToProtocol(CliRestartPolicy policy) => policy switch
    {
        CliRestartPolicy.AfterTurn => "AfterTurn",
        CliRestartPolicy.NextSend => "NextSend",
        _ => "SafePoint",
    };

    /// <summary>setting cliRestartPolicy 的值：只接受協定的三個值（大小寫要對）；其他回 null（不改設定）</summary>
    public static CliRestartPolicy? ParsePolicy(string? value) => value switch
    {
        "SafePoint" => CliRestartPolicy.SafePoint,
        "AfterTurn" => CliRestartPolicy.AfterTurn,
        "NextSend" => CliRestartPolicy.NextSend,
        _ => null,
    };

    /// <summary>
    /// 標頭要不要顯示「立即套用」：等待類狀態才有（等背景工作的也有：不想等到上限就自己按）；已停下等更新、重開中、已完成的都沒有
    /// </summary>
    public static bool CanApplyNow(CliRestartState state) => state switch
    {
        CliRestartState.WaitingTool or CliRestartState.WaitingUser or CliRestartState.WaitingTurn or CliRestartState.WaitingBackground => true,
        _ => false,
    };

    /// <summary>還在 CLI 卡「換版中」彙總（restartWaiting）的狀態：等待類、重開中與等 app 更新</summary>
    public static bool IsPending(CliRestartState state)
        => state is CliRestartState.WaitingTool or CliRestartState.WaitingUser or CliRestartState.WaitingBackground
            or CliRestartState.WaitingTurn or CliRestartState.WaitingUpdate or CliRestartState.Restarting or CliRestartState.AppUpdate;

    // ───────────────────────────── 不自動接續 ─────────────────────────────

    /// <summary>
    /// 被打斷的那一輪為什麼不自動接續（設計 7.7）；可以自動接續回 null。依序判斷：使用者按過停止、上一輪已完成（閒置）、
    /// 中斷超過 1 小時、上一輪因額度或 API 錯誤結束、上一次接續失敗、被打斷的是高風險操作
    /// </summary>
    /// <param name="interruptedAt">這一輪被打斷（程序被停下）的時間；還沒停下為 null（＝現在）</param>
    public static CliNoContinueReason? WhyNotContinue(bool userStopped, bool turnRunning, DateTime? interruptedAt, DateTime now,
        bool previousTurnFailed, bool continuationFailed, bool riskyTool)
    {
        if (userStopped)
            return CliNoContinueReason.UserStopped;
        if (!turnRunning)
            return CliNoContinueReason.Idle;
        if (interruptedAt is { } at && now - at > MaxInterruptionAge)
            return CliNoContinueReason.TooOld;
        if (previousTurnFailed)
            return CliNoContinueReason.PreviousTurnFailed;
        if (continuationFailed)
            return CliNoContinueReason.ContinuationFailed;
        if (riskyTool)
            return CliNoContinueReason.RiskyTool;
        return null;
    }

    /// <summary>沒有自動接續的對話，標頭要不要給「繼續」：使用者自己按停止的、本來就閒置的不給（送出新訊息即可）</summary>
    public static bool CanContinueManually(CliNoContinueReason reason)
        => reason is not (CliNoContinueReason.UserStopped or CliNoContinueReason.Idle);

    /// <summary>不自動接續的原因（Notice 與標頭的文字）</summary>
    /// <param name="operation">RiskyTool 時被打斷的操作（一行摘要）</param>
    public static string DescribeNoContinue(CliNoContinueReason reason, string? operation = null) => reason switch
    {
        CliNoContinueReason.UserStopped => "你按了停止",
        CliNoContinueReason.Idle => "上一輪已經完成",
        CliNoContinueReason.TooOld => "中斷已超過 1 小時，請確認狀況後按「繼續」或送出新訊息",
        CliNoContinueReason.PreviousTurnFailed => "上一輪因額度用完或 API 錯誤而結束，請確認後按「繼續」或送出新訊息",
        CliNoContinueReason.ContinuationFailed => "上一次接續沒有成功，請確認後按「繼續」或送出新訊息",
        CliNoContinueReason.RiskyTool => $"上一個操作〈{(string.IsNullOrWhiteSpace(operation) ? "工具" : operation)}〉結果未知，請確認後按「繼續」或送出新訊息",
        _ => "CLI 沒有接續這一輪，請確認後按「繼續」或送出新訊息",
    };

    // ───────────────────────────── 高風險（不可重複）的操作 ─────────────────────────────

    /// <summary>使用者加的樣式符合時的規則 id</summary>
    public const string CustomRuleId = "custom";

    /// <summary>無法判斷副作用的 MCP 工具（名稱不是查詢類動詞開頭）的規則 id</summary>
    public const string McpRuleId = "mcp-tool";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>
    /// 預設的指令規則（id、說明、樣式；不分大小寫，比對工具名稱、一行摘要與輸入）。
    /// 寧可多擋：符合只代表「不自動接續，改請使用者確認後按繼續」，漏擋則可能重做不可重複的操作
    /// </summary>
    private static readonly (string Id, string Description, Regex Pattern)[] CommandRules =
    {
        ("git-push", "git push", Pattern(@"\bgit\b[^\r\n;&|]*?\bpush\b")),
        ("npm-publish", "npm publish", Pattern(@"\b(npm|pnpm|yarn|bun)\s+publish\b")),
        ("dotnet-nuget-push", "dotnet nuget push", Pattern(@"\b(dotnet\s+nuget|nuget(\.exe)?)\s+push\b")),
        ("gh-release", "gh release", Pattern(@"\bgh\s+release\b")),
        ("gh-pr-merge", "gh pr merge", Pattern(@"\bgh\s+pr\s+merge\b")),
        ("deploy-script", "deploy／publish 腳本", Pattern(@"deploy|\bpublish[\w.-]*\.(ps1|sh|cmd|bat|py|js|mjs|ts)\b|\b(npm|pnpm|yarn|bun)\s+(run\s+)?(publish|release)\b")),
        ("ef-database-update", "dotnet ef database update", Pattern(@"\bdotnet\s+ef\s+database\s+update\b|\bUpdate-Database\b")),
        ("sqlcmd", "sqlcmd", Pattern(@"\b(sqlcmd|Invoke-Sqlcmd)\b")),
        ("sql-drop", "DROP／TRUNCATE", Pattern(@"\bDROP\s+(TABLE|DATABASE|SCHEMA|VIEW|INDEX|PROC|PROCEDURE|FUNCTION|TRIGGER|USER|LOGIN|ROLE|SEQUENCE|TYPE|COLUMN|CONSTRAINT|IF\s+EXISTS)\b|\bTRUNCATE\s+TABLE\b")),
        ("remove-item-recurse", "Remove-Item -Recurse", Pattern(@"\b(Remove-Item|ri|rm|del|erase|rmdir|rd)\b[^\r\n;|]*\s-r(ecurse)?\b|\b(rd|rmdir)\s+/s\b")),
        ("rm-rf", "rm -rf", Pattern(@"\brm\s+(-[a-z]*r[a-z]*f[a-z]*|-[a-z]*f[a-z]*r[a-z]*|-r\s+-f|-f\s+-r|--recursive\s+--force|--force\s+--recursive)\b")),
        ("terraform-apply", "terraform apply", Pattern(@"\bterraform\s+(apply|destroy)\b")),
        ("kubectl", "kubectl", Pattern(@"\bkubectl\b|\bhelm\s+(install|upgrade|uninstall|delete|rollback)\b")),
        ("http-mutating", "curl -X POST／PUT／DELETE", Pattern(@"\bcurl(\.exe)?\b[^\r\n]*?(\s-X\s*|\s--request[\s=])['""]?(POST|PUT|DELETE|PATCH)\b|\b(Invoke-RestMethod|Invoke-WebRequest|irm|iwr)\b[^\r\n]*?-Method\s+['""]?(Post|Put|Delete|Patch)\b")),
        ("payment", "付款", Pattern(@"\b(stripe|paypal|braintree|adyen|newebpay|ecpay|tappay)\b|付款|扣款|退款|刷卡|請款|/v\d+/(charges|payment_intents|payments|refunds|payouts|transfers)\b")),
    };

    /// <summary>MCP 工具名稱是查詢類動詞開頭（get_issue、listFiles、search…）：副作用可判斷為唯讀</summary>
    private static readonly Regex ReadOnlyToolName = new(
        @"^(?i:get|list|read|search|fetch|query|find|describe|show|view|lookup|inspect|count|check|status|browse|retrieve|resolve|explain)(?=$|[_\-.\s:]|\p{Lu})",
        RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>不比對指令規則的工具種類（讀檔、搜尋、讀網頁、改檔案、待辦…重做不會有不可逆的副作用）</summary>
    private static readonly HashSet<string> SafeToolKinds = new(StringComparer.OrdinalIgnoreCase) { "read", "search", "web", "edit", "write", "todo", "plan", "image" };

    /// <summary>預設規則的 id（設定的 CliRestartRiskDisabled 用這些 id 停用）</summary>
    public static IReadOnlyList<string> DefaultRiskRuleIds { get; } = CommandRules.Select(r => r.Id).Append(McpRuleId).ToList();

    /// <summary>
    /// 被打斷的工具是不是不可重複的高風險操作（設計 7.7）：預設規則（可用 disabledRules 的 id 停用）＋使用者加的樣式
    /// （不分大小寫的 .NET 正規式，比對工具名稱、一行摘要與輸入；寫錯或比對逾時的樣式略過）。不符合回 null
    /// </summary>
    /// <param name="toolName">顯示名稱（執行指令、MCP：server…）</param>
    /// <param name="toolKind">工具種類（bash、mcp、edit…）</param>
    /// <param name="title">一行摘要（指令第一行、MCP 工具名稱）</param>
    /// <param name="detail">展開後的輸入（完整指令、MCP 參數）</param>
    public static CliRiskMatch? FindRisk(string? toolName, string? toolKind, string? title, string? detail,
        IEnumerable<string>? extraPatterns = null, IEnumerable<string>? disabledRules = null)
    {
        var disabled = new HashSet<string>(disabledRules ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var operation = DescribeOperation(toolName, title);
        var text = string.Join("\n", new[] { toolName, title, detail }.Where(s => !string.IsNullOrEmpty(s)));
        var kind = toolKind ?? string.Empty;

        if (!SafeToolKinds.Contains(kind))
        {
            foreach (var (id, description, pattern) in CommandRules)
            {
                if (!disabled.Contains(id) && SafeIsMatch(pattern, text))
                    return new CliRiskMatch(id, description, operation);
            }
        }

        if (string.Equals(kind, "mcp", StringComparison.OrdinalIgnoreCase) && !disabled.Contains(McpRuleId)
            && !SafeIsMatch(ReadOnlyToolName, (title ?? string.Empty).Trim()))
            return new CliRiskMatch(McpRuleId, "無法判斷副作用的 MCP 工具", operation);

        foreach (var custom in extraPatterns ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(custom))
                continue;
            Regex regex;
            try
            {
                regex = Pattern(custom);
            }
            catch (ArgumentException ex)
            {
                Log.Warn($"高風險操作的自訂樣式寫錯了，已略過：{custom}（{ex.Message}）");
                continue;
            }
            if (SafeIsMatch(regex, text))
                return new CliRiskMatch(CustomRuleId, custom, operation);
        }

        return null;
    }

    /// <summary>被打斷的工具是不可重複的高風險操作（FindRisk 有符合的規則）</summary>
    public static bool IsNonIdempotent(string? toolName, string? toolKind, string? title, string? detail,
        IEnumerable<string>? extraPatterns = null, IEnumerable<string>? disabledRules = null)
        => FindRisk(toolName, toolKind, title, detail, extraPatterns, disabledRules) != null;

    private static bool SafeIsMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            Log.Warn($"高風險操作的樣式比對逾時，已略過：{regex}");
            return false;
        }
    }

    // ───────────────────────────── 文字 ─────────────────────────────

    /// <summary>被打斷的操作的一行摘要（指令第一行；沒有摘要就用工具名稱）</summary>
    public static string DescribeOperation(string? toolName, string? title)
    {
        var line = ClaudeStreamRules.FirstLine(title);
        if (line.Length > 0)
            return line;
        return string.IsNullOrWhiteSpace(toolName) ? "工具" : toolName.Trim();
    }

    /// <summary>
    /// 重開時被中斷的工具卡的說明（狀態是「已停止」，不是「失敗」）：與會話送的一致——Claude 立即重開時會話送
    /// AgentToolCompletedEvent(Cancelled, ClaudeStreamRules.InterruptedToolOutput)，Codex 被結束的指令改成 CodexAppServerRules.InterruptedToolOutput
    /// </summary>
    public static string InterruptedToolOutput(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => ClaudeStreamRules.InterruptedToolOutput,
        AgentProvider.Codex => CodexAppServerRules.InterruptedToolOutput,
        AgentProvider.Antigravity => AntigravityStreamRules.InterruptedToolOutput,
        AgentProvider.Grok => GrokAcpRules.InterruptedToolOutput,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>工具卡的輸出後面加上被重開中斷的說明（已經有就不重複；沒有輸出就只有說明）</summary>
    public static string AppendInterruptedNote(string? output, AgentProvider provider)
    {
        var note = InterruptedToolOutput(provider);
        if (string.IsNullOrWhiteSpace(output))
            return note;
        return output.Contains(note, StringComparison.Ordinal) ? output : output.TrimEnd() + "\n\n" + note;
    }

    /// <summary>已執行多久：42 秒、3 分 5 秒、1 小時 2 分</summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        var seconds = (long)elapsed.TotalSeconds;
        if (seconds < 60)
            return $"{seconds} 秒";
        if (seconds < 3600)
            return seconds % 60 == 0 ? $"{seconds / 60} 分" : $"{seconds / 60} 分 {seconds % 60} 秒";
        var minutes = seconds / 60 % 60;
        return minutes == 0 ? $"{seconds / 3600} 小時" : $"{seconds / 3600} 小時 {minutes} 分";
    }

    /// <summary>
    /// 代理或指令來的文字放進 Notice（Markdown）時跳脫：ASCII 標點一律加反斜線（CommonMark 允許跳脫任何 ASCII 標點），
    /// 指令裡的 *、_、反引號不會被當成格式，Windows 路徑的反斜線也照原樣顯示
    /// </summary>
    public static string EscapeMarkdown(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var builder = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            if (ch < 128 && char.IsPunctuation(ch) || ch is '`' or '^' or '|' or '~' or '<' or '>' or '=' or '+' or '$')
                builder.Append('\\');
            builder.Append(ch);
        }
        return builder.ToString();
    }

    /// <summary>「CLI 已更新到 2.1.297」；版本不明時「CLI 已更新」</summary>
    private static string UpdatedTo(string? version) => string.IsNullOrWhiteSpace(version) ? "CLI 已更新" : $"CLI 已更新到 {version.Trim()}";

    /// <summary>動詞接產品與版本：「已用 Claude Code 2.1.297」；版本不明時「已用新版 Claude Code」</summary>
    private static string WithProduct(string verb, AgentProvider provider, string? version)
        => string.IsNullOrWhiteSpace(version) ? $"{verb}新版 {ProductName(provider)}" : $"{verb} {ProductName(provider)} {version.Trim()}";

    /// <summary>
    /// 重開前後的 CLI 版本相同：CLI 更新取消、失敗或沒有換版後以同一版接續，放棄 app 更新後就地接續，同一版換了安裝位置。
    /// 標頭不送「舊版 → 新版」、說明不講「已用新版」；任一邊不明時當成不同（照換版的說法）
    /// </summary>
    public static bool SameVersion(string? fromVersion, string? toVersion)
        => !string.IsNullOrWhiteSpace(fromVersion) && !string.IsNullOrWhiteSpace(toVersion)
           && string.Equals(fromVersion.Trim(), toVersion.Trim(), StringComparison.Ordinal);

    /// <summary>兩邊的版本都知道而且不同（放棄 app 更新的排空期間 CLI 剛好換了版，說明才帶 CLI 的版本）</summary>
    private static bool KnownSwitch(string? fromVersion, string? toVersion)
        => !string.IsNullOrWhiteSpace(fromVersion) && !string.IsNullOrWhiteSpace(toVersion) && !SameVersion(fromVersion, toVersion);

    private const string AppUpdateAbandonedLead = "已放棄這次 Clode Myriagent 更新";

    /// <summary>「Claude Code 沒有換版（仍是 2.1.310）」</summary>
    private static string NoSwitch(AgentProvider provider, string toVersion) => $"{ProductName(provider)} 沒有換版（仍是 {toVersion.Trim()}）";

    /// <summary>
    /// 自動接續的開頭：「已用 Claude Code 2.1.297 自動接續」；版本沒變「Claude Code 沒有換版（仍是 2.1.310），已自動接續」；
    /// 放棄 app 更新後就地接續「已放棄這次 Clode Myriagent 更新，已自動接續」（排空期間 CLI 換了版時「…，改用 Claude Code 2.1.311 自動接續」）
    /// </summary>
    private static string ContinuedHead(AgentProvider provider, string? fromVersion, string? toVersion, bool appUpdateAbandoned)
    {
        if (appUpdateAbandoned)
            return KnownSwitch(fromVersion, toVersion) ? $"{AppUpdateAbandonedLead}，{WithProduct("改用", provider, toVersion)} 自動接續" : $"{AppUpdateAbandonedLead}，已自動接續";
        return SameVersion(fromVersion, toVersion) ? $"{NoSwitch(provider, toVersion!)}，已自動接續" : $"{WithProduct("已用", provider, toVersion)} 自動接續";
    }

    /// <summary>沒有自動接續時標頭的開頭：「已換成 Claude Code 2.1.297」；版本沒變、放棄 app 更新的說法同 <see cref="ContinuedHead"/></summary>
    private static string NotContinuedHead(AgentProvider provider, string? fromVersion, string? toVersion, bool appUpdateAbandoned)
    {
        if (appUpdateAbandoned)
            return KnownSwitch(fromVersion, toVersion) ? $"{AppUpdateAbandonedLead}，{WithProduct("已換成", provider, toVersion)}" : AppUpdateAbandonedLead;
        return SameVersion(fromVersion, toVersion) ? NoSwitch(provider, toVersion!) : WithProduct("已換成", provider, toVersion);
    }

    /// <summary>
    /// 標頭的一行說明（HeaderRestart.detail，純文字）。
    /// </summary>
    /// <param name="subject">WaitingTool：在等的工具（一行摘要）；NotContinued：原因；Failed：錯誤</param>
    /// <param name="fromVersion">Continued、NotContinued：程序原本跑的版本（和 toVersion 相同就不說「已用新版」）</param>
    /// <param name="appUpdateAbandoned">Continued、NotContinued：放棄 app 更新後就地重開（不是 CLI 換版）</param>
    public static string DescribeState(CliRestartState state, AgentProvider provider, string? toVersion, string? subject = null,
        string? fromVersion = null, bool appUpdateAbandoned = false)
    {
        var updated = UpdatedTo(toVersion);
        var item = string.IsNullOrWhiteSpace(subject) ? null : ClaudeStreamRules.FirstLine(subject);
        return state switch
        {
            CliRestartState.WaitingTool => item == null ? $"{updated}，等目前的工具完成後切換" : $"{updated}，等目前的指令（{item}）完成後切換",
            CliRestartState.WaitingUser => $"{updated}，等你回應後切換",
            CliRestartState.WaitingBackground => $"{updated}，等背景工作結束後切換（最多等 {FormatMinutes(BackgroundWaitLimit)}）",
            CliRestartState.WaitingTurn => $"{updated}，這一輪結束後切換",
            CliRestartState.WaitingUpdate => $"等待 {ProductName(provider)} 更新完成…",
            CliRestartState.Restarting => string.IsNullOrWhiteSpace(toVersion) ? RestartingStatus : $"{WithProduct("正在以", provider, toVersion)} 重新啟動…",
            CliRestartState.Continued => ContinuedHead(provider, fromVersion, toVersion, appUpdateAbandoned),
            CliRestartState.NotContinued => $"{NotContinuedHead(provider, fromVersion, toVersion, appUpdateAbandoned)}，這一輪沒有自動接續" + (item == null ? string.Empty : $"：{item}"),
            CliRestartState.Failed => item == null ? "以新版 CLI 重新啟動失敗" : $"以新版 CLI 重新啟動失敗：{item}",
            CliRestartState.AppUpdate => "等待 Clode Myriagent 更新，更新後自動接續…",
            _ => $"{updated}，下次送出時改用新版",
        };
    }

    /// <summary>
    /// 更新屏障排空中還在等的對話（還沒停下）：「等目前的指令（npm test）完成後停下，讓 Codex 更新」。
    /// product 是要更新的產品（CLI 的 ProductName，或 app 更新的「Clode Myriagent」）；subject 是在等的工具（只取第一行）
    /// </summary>
    public static string DescribeDrainWaiting(CliRestartState state, string product, string? subject) => state switch
    {
        CliRestartState.WaitingUser => $"等你回應後停下，讓 {product} 更新",
        CliRestartState.WaitingBackground => $"等背景工作結束後停下，讓 {product} 更新（最多等 {FormatMinutes(BackgroundWaitLimit)}）",
        _ => string.IsNullOrWhiteSpace(subject)
            ? $"等目前的工具完成後停下，讓 {product} 更新"
            : $"等目前的指令（{ClaudeStreamRules.FirstLine(subject)}）完成後停下，讓 {product} 更新",
    };

    /// <summary>app 更新排空中還在等的對話：「等目前的指令（npm test）完成後停下，讓 Clode Myriagent 更新」</summary>
    public static string DescribeAppDrainWaiting(CliRestartState state, string? subject) => DescribeDrainWaiting(state, "Clode Myriagent", subject);

    /// <summary>
    /// 自動接續時對話裡的 Notice：「已用 Claude Code 2.1.297 自動接續（中斷時正在執行：npm test，已執行 42 秒）」；
    /// 版本沒變、放棄 app 更新後就地接續的開頭見 <see cref="ContinuedHead"/>
    /// </summary>
    public static string ContinuedNotice(AgentProvider provider, string? toVersion, IReadOnlyList<AgentInterruptedTool> interrupted,
        string? fromVersion = null, bool appUpdateAbandoned = false)
    {
        var head = ContinuedHead(provider, fromVersion, toVersion, appUpdateAbandoned);
        if (interrupted.Count == 0)
            return head + "（重啟時沒有工具在執行）";
        var tools = string.Join("、", interrupted.Select(t => $"{EscapeMarkdown(DescribeOperation(t.ToolName, t.Title))}，已執行 {FormatElapsed(t.Elapsed)}"));
        return $"{head}（中斷時正在執行：{tools}）";
    }

    /// <summary>佇列裡還有訊息時，收尾的提示後面加的說明（與一輪失敗、停止時相同：佇列暫停，按送出才接著送）</summary>
    private const string QueuePausedSuffix = "（排隊的訊息暫停，按送出繼續）";

    /// <summary>
    /// 沒有自動接續時的 Notice（Markdown；操作摘要會跳脫）：「CLI 已更新到 2.1.297；…」，版本沒變是「CLI 沒有換版（仍是 2.1.310）；…」，
    /// 放棄 app 更新後就地重開是「已放棄這次 Clode Myriagent 更新；…」
    /// </summary>
    public static string NotContinuedNotice(string? toVersion, CliNoContinueReason reason, string? operation, bool queuePaused,
        string? fromVersion = null, bool appUpdateAbandoned = false)
    {
        var lead = appUpdateAbandoned
            ? KnownSwitch(fromVersion, toVersion) ? $"{AppUpdateAbandonedLead}，{UpdatedTo(toVersion)}" : AppUpdateAbandonedLead
            : SameVersion(fromVersion, toVersion) ? $"CLI 沒有換版（仍是 {toVersion!.Trim()}）" : UpdatedTo(toVersion);
        return $"{lead}；這一輪沒有自動接續，原因：{DescribeNoContinue(reason, operation == null ? null : EscapeMarkdown(operation))}"
               + (queuePaused ? QueuePausedSuffix : string.Empty);
    }

    /// <summary>「30 分鐘」（整分鐘）</summary>
    private static string FormatMinutes(TimeSpan span) => $"{(int)Math.Round(span.TotalMinutes)} 分鐘";

    /// <summary>
    /// CLI 換版停下了只在等背景工作的對話（等滿上限或使用者按了立即套用）、以新版重開後隱藏送出確認訊息時的 Notice
    /// （緊接著的一輪是隱藏送出的 ContinuationRules.Build，WaitingBackground）
    /// </summary>
    public static string BackgroundCheckNotice(string? toVersion)
        => $"{UpdatedTo(toVersion)}；切換時停止了還在跑的背景工作，已請代理確認背景工作做到哪裡、需要的話重新執行";

    /// <summary>同上，但被停掉的背景工作是高風險操作（部署、發布、git push）：不自動請代理確認，給「繼續」</summary>
    public static string BackgroundNotCheckedNotice(string? toVersion, string operation)
        => $"{UpdatedTo(toVersion)}；切換時停止了還在跑的背景工作，沒有自動請代理確認，原因：{DescribeNoContinue(CliNoContinueReason.RiskyTool, EscapeMarkdown(operation))}";

    /// <summary>以新版重開失敗時的 Notice</summary>
    public static string FailedNotice(string? error, bool queuePaused)
        => $"以新版 CLI 重新啟動失敗：{EscapeMarkdown(string.IsNullOrWhiteSpace(error) ? "原因不明" : ClaudeStreamRules.FirstLine(error))}；按送出繼續"
           + (queuePaused ? QueuePausedSuffix : string.Empty);

    /// <summary>更新屏障停下的對話：更新結束後沒有可用的版本（不明、未安裝、損壞），不重開</summary>
    public static string UpdateUnavailableNotice(AgentProvider provider, string? error, bool queuePaused)
    {
        var why = string.IsNullOrWhiteSpace(error) ? string.Empty : $"（{EscapeMarkdown(ClaudeStreamRules.FirstLine(error))}）";
        return $"{ProductName(provider)} 更新後沒有可用的版本{why}，這一輪沒有接續；修好 CLI 後按送出繼續"
               + (queuePaused ? QueuePausedSuffix : string.Empty);
    }

    /// <summary>一波換版處理完的系統匣彙總：標題</summary>
    public static string WaveSummaryTitle(AgentProvider provider, string? toVersion)
        => string.IsNullOrWhiteSpace(toVersion) ? $"{ProductName(provider)} 已更新" : $"{ProductName(provider)} 已更新到 {toVersion}";

    /// <summary>一波換版處理完的系統匣彙總：內容（「3 個對話已改用新版：2 個已自動接續、1 個閒置的已關閉」）</summary>
    public static string WaveSummaryMessage(int continued, int restarted, int notContinued, int failed)
    {
        var parts = new List<string>();
        if (continued > 0)
            parts.Add($"{continued} 個已自動接續");
        if (restarted > 0)
            parts.Add($"{restarted} 個閒置的已關閉（下次送出用新版）");
        if (notContinued > 0)
            parts.Add($"{notContinued} 個沒有自動接續（請查看對話）");
        if (failed > 0)
            parts.Add($"{failed} 個重開失敗");
        var total = continued + restarted + notContinued + failed;
        return parts.Count == 0 ? "沒有需要換版的對話" : $"{total} 個對話已處理：{string.Join("、", parts)}";
    }
}
