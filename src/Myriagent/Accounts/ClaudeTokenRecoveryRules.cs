using Myriagent.Cli;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Accounts;

/// <summary>
/// 可以借來查用量的 Claude 登入（另一個帳戶，或沒登記成帳戶的本機預設設定資料夾）：只看 CLI 設定的 oauthAccount 與憑證檔的中繼欄位，不看權杖
/// </summary>
/// <param name="CredentialFile">憑證檔（.credentials.json）</param>
/// <param name="Label">記錄用的名稱（帳戶名稱，或「本機預設設定資料夾」）</param>
/// <param name="Email">CLI 設定 oauthAccount.emailAddress</param>
/// <param name="OrgId">CLI 設定 oauthAccount.organizationUuid</param>
/// <param name="ExpiresAt">憑證檔 claudeAiOauth.expiresAt；沒有為 null</param>
/// <param name="HasToken">憑證檔有 accessToken（不是空字串）</param>
internal sealed record ClaudeUsageCredentialSource(string CredentialFile, string Label, string? Email, string? OrgId, DateTimeOffset? ExpiresAt, bool HasToken);

/// <summary>能不能讓 CLI 換發閒置帳戶的權杖（ClaudeTokenRecoveryRules.CanRefreshWithCli）；None＝可以</summary>
internal enum ClaudeTokenRefreshBlock
{
    None,

    /// <summary>設定關掉了</summary>
    Disabled,

    /// <summary>沒有安裝 Claude CLI</summary>
    NotInstalled,

    /// <summary>不是已登入的訂閱帳戶（未登入、API Key、檢查失敗）</summary>
    NotReady,

    /// <summary>權杖在 Windows 認證管理員（看不到期限，任何短命的 claude 都不跑）</summary>
    Conservative,

    /// <summary>這個帳戶正在登入</summary>
    LoginActive,

    /// <summary>Claude CLI 正在更新</summary>
    CliPaused,

    /// <summary>這個帳戶有一輪對話正在跑：CLI 自己會換發，對話中也會即時回報用量</summary>
    TurnRunning,

    /// <summary>上一次換發還在跑</summary>
    InFlight,

    /// <summary>權杖沒有過期（不需要換發）</summary>
    NotExpired,

    /// <summary>憑證檔沒有 refreshToken：沒有東西可以換發</summary>
    NoRefreshToken,

    /// <summary>距上一次嘗試不到最小間隔</summary>
    TooSoon,
}

/// <summary>讓 CLI 換發權杖的結果（ClaudeTokenRecoveryRules.EvaluateRefresh）</summary>
internal enum ClaudeTokenRefreshResult
{
    /// <summary>憑證檔換成新的權杖（回覆本身失敗也算：例如額度用完的 429，權杖在呼叫之前就換好了）</summary>
    Refreshed,

    /// <summary>CLI 換發被拒後清空了登入（accessToken 變成空字串）：要重新登入</summary>
    LoginLost,

    /// <summary>權杖沒變（逾時、啟動失敗、網路錯誤）：之後再試</summary>
    Failed,
}

/// <summary>
/// Claude 帳戶權杖過期後的用量恢復（純函式規則）。app 絕不自己換發權杖（會輪換 refresh token，CLI 手上的舊 token 失效就等於被登出），所以：
/// <list type="number">
/// <item>先借用同身分（CLI 設定的 email 與 organizationUuid 都相同）、權杖還有效的其他登入，唯讀查官方用量端點；</item>
/// <item>借不到時，由 Claude CLI 自己換發：替這個帳戶跑一次最小的 claude -p（haiku、不帶工具、精簡模式；實測約 0.0001 美元等值額度、5 秒），
/// CLI 換發後寫回憑證檔，再照常查用量。和平常對話時 CLI 換發權杖是同一條路徑：多個 CLI 程序同時換發由 CLI 自己協調
/// （2.1.306 執行檔：換發前鎖定設定資料夾，拿到鎖後重讀憑證檔，別的程序剛換發過就直接用）。</item>
/// </list>
/// 實測紀錄見 docs/protocol-notes.md「權杖過期後的用量」與 tools/probes/auth/claude_token_refresh_probe.py
/// </summary>
internal static class ClaudeTokenRecoveryRules
{
    /// <summary>借來的權杖至少還要有效這麼久（避免查到一半過期）</summary>
    public static readonly TimeSpan BorrowMinimumLife = TimeSpan.FromMinutes(5);

    /// <summary>同一個帳戶兩次讓 CLI 換發之間至少隔多久（成功後權杖約 8 小時有效，這個間隔只擋換發沒成功時的重試）</summary>
    public static readonly TimeSpan RefreshMinInterval = TimeSpan.FromHours(1);

    /// <summary>CLI 代查用量的程序最多跑多久（實測 get_usage 約 4～12 秒；逾時才結束它）</summary>
    public static readonly TimeSpan RefreshTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 從可以借用的登入裡挑一個：CLI 設定的 email（不分大小寫）與 organizationUuid 都和這個帳戶相同、有權杖、距到期至少 5 分鐘，
    /// 取到期最晚的。這個帳戶自己的 email 或 org 不明時一律不借（只靠 email 可能是同一個人的另一個組織）
    /// </summary>
    public static ClaudeUsageCredentialSource? PickBorrowSource(string? email, string? orgId, IEnumerable<ClaudeUsageCredentialSource> sources, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(email) || !AccountRules.IsValidOrgId(orgId))
            return null;
        var wantedEmail = email.Trim();
        return sources
            .Where(s => s.HasToken
                        && s.ExpiresAt is { } expiresAt && expiresAt - BorrowMinimumLife > now
                        && string.Equals(s.Email?.Trim(), wantedEmail, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(s.OrgId, orgId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.ExpiresAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// 能不能讓 CLI 換發這個帳戶的權杖（呼叫端在用量查詢回「權杖已過期」、又借不到其他登入時才問）。
    /// 依序：設定關掉、沒裝 CLI、不是已登入的帳戶、權杖在認證管理員、正在登入、CLI 正在更新、有對話正在跑、上一次還在跑、
    /// 權杖其實沒過期、沒有 refreshToken、距上一次嘗試不到 1 小時
    /// </summary>
    public static ClaudeTokenRefreshBlock CanRefreshWithCli(bool enabled, bool installed, AccountStatus status, bool conservative, bool loginActive, bool cliPaused,
        int runningTurns, bool inFlight, ClaudeTokenState tokenState, bool hasRefreshToken, DateTimeOffset? lastAttempt, DateTimeOffset now)
    {
        if (!enabled)
            return ClaudeTokenRefreshBlock.Disabled;
        if (!installed)
            return ClaudeTokenRefreshBlock.NotInstalled;
        if (status != AccountStatus.Ready)
            return ClaudeTokenRefreshBlock.NotReady;
        if (conservative)
            return ClaudeTokenRefreshBlock.Conservative;
        if (loginActive)
            return ClaudeTokenRefreshBlock.LoginActive;
        if (cliPaused)
            return ClaudeTokenRefreshBlock.CliPaused;
        if (runningTurns > 0)
            return ClaudeTokenRefreshBlock.TurnRunning;
        if (inFlight)
            return ClaudeTokenRefreshBlock.InFlight;
        if (tokenState != ClaudeTokenState.Expiring)
            return ClaudeTokenRefreshBlock.NotExpired;
        if (!hasRefreshToken)
            return ClaudeTokenRefreshBlock.NoRefreshToken;
        if (lastAttempt is { } last && now - last < RefreshMinInterval)
            return ClaudeTokenRefreshBlock.TooSoon;
        return ClaudeTokenRefreshBlock.None;
    }

    /// <summary>憑證檔 claudeAiOauth.refreshToken 有沒有值（只看有沒有，不讀內容）；讀不懂回 false</summary>
    public static bool HasRefreshToken(string? credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
            return false;
        try
        {
            return JObject.Parse(credentialsJson)["claudeAiOauth"] is JObject oauth
                   && oauth["refreshToken"] is JValue { Type: JTokenType.String } token && !string.IsNullOrWhiteSpace((string?)token);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 讓 CLI 代查用量的控制請求（get_usage，SDK 標成實驗中的介面；2026-10-26 起取代以前「跑一次最小的 claude -p」）：
    /// 在一個不送訊息的程序裡問（ClaudeControlClient），CLI 查用量前自己換發過期的權杖、寫回憑證檔。不呼叫模型：不用額度、不會開始新的 5 小時時間窗
    /// </summary>
    public static JObject BuildUsageRequest() => new() { ["subtype"] = "get_usage" };

    /// <summary>
    /// get_usage 的回應 → 用量端點同樣形狀的 rate_limits（five_hour、seven_day…，UsageRules.ParseClaudeUsage 照樣解析）；
    /// rate_limits_available 是 false 或沒有 rate_limits 回 null（實測 2.1.310 的形狀見 protocol-notes）
    /// </summary>
    public static JObject? ParseCliUsage(JObject? response)
    {
        if (response == null || response["rate_limits_available"] is JValue { Type: JTokenType.Boolean, Value: false })
            return null;
        return response["rate_limits"] as JObject;
    }

    /// <summary>
    /// 換發的結果：看跑完之後的憑證檔（不看結束碼——回覆失敗時權杖也可能已經換好）。權杖新鮮＝換發成功；accessToken 變空字串＝CLI 換發被拒、
    /// 清空了登入；其他＝沒成功，detail 是給人看的原因（逾時、結束碼與輸出的第一行）
    /// </summary>
    public static (ClaudeTokenRefreshResult Result, string? Detail) EvaluateRefresh(ClaudeTokenState after, CliRunResult run)
    {
        switch (after)
        {
            case ClaudeTokenState.Fresh:
                return (ClaudeTokenRefreshResult.Refreshed, null);
            case ClaudeTokenState.Empty:
                return (ClaudeTokenRefreshResult.LoginLost, "CLI 換發被拒，登入已失效");
        }
        if (run.TimedOut)
            return (ClaudeTokenRefreshResult.Failed, "執行逾時");
        // 結束碼 0 而且不是錯誤結果（回覆成功但權杖沒變，例如 CLI 把憑證移到 Windows 認證管理員）：stdout 是一整行 JSON，不拿來當原因
        var line = FirstLine(run.Stderr) ?? ResultText(run.Stdout) ?? (run.ExitCode == 0 ? null : FirstLine(run.Stdout));
        return (ClaudeTokenRefreshResult.Failed, line == null ? $"結束碼 {run.ExitCode}，權杖沒有更新" : $"結束碼 {run.ExitCode}：{line}");
    }

    /// <summary>
    /// 用量查詢回「權杖已過期」時給人看的說明（儀表板與側欄提示）：依能不能讓 CLI 換發、上一次的結果，說明接下來會怎樣。
    /// nextAttempt＝下一次可以再試的時間（TooSoon 才用得到）
    /// </summary>
    public static string DescribeExpired(ClaudeTokenRefreshBlock block, string? lastError, DateTimeOffset? nextAttempt, DateTimeOffset now) => block switch
    {
        ClaudeTokenRefreshBlock.None or ClaudeTokenRefreshBlock.InFlight => "登入權杖已過期，正在讓 Claude CLI 換發權杖…",
        ClaudeTokenRefreshBlock.TooSoon when lastError != null =>
            $"登入權杖已過期，Claude CLI 換發沒有成功（{lastError}）；{DescribeWait(nextAttempt, now)}再試",
        ClaudeTokenRefreshBlock.TooSoon => $"登入權杖已過期；{DescribeWait(nextAttempt, now)}再讓 Claude CLI 換發",
        ClaudeTokenRefreshBlock.CliPaused => "登入權杖已過期，Claude CLI 更新完會再換發權杖",
        ClaudeTokenRefreshBlock.TurnRunning => "登入權杖已過期，對話中 CLI 會自動換發",
        ClaudeTokenRefreshBlock.NoRefreshToken => "登入權杖已過期，憑證沒有可換發的權杖，請重新登入這個帳戶",
        _ => "登入權杖已過期，下次使用這個帳戶時會自動更新",
    };

    private static string DescribeWait(DateTimeOffset? nextAttempt, DateTimeOffset now)
    {
        if (nextAttempt is not { } next || next <= now)
            return "稍後";
        var minutes = (int)Math.Ceiling((next - now).TotalMinutes);
        return minutes >= 60 ? $"約 {Math.Round(minutes / 60.0)} 小時後" : $"{minutes} 分鐘後";
    }

    private static string? FirstLine(string? text)
    {
        var line = text?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line == null ? null : line.Length <= 160 ? line : line[..160] + "…";
    }

    /// <summary>--output-format json 的結果裡 is_error 為 true 時的 result 文字（例如額度用完的說明）</summary>
    private static string? ResultText(string? stdout)
    {
        var line = stdout?.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (line == null)
            return null;
        try
        {
            var json = JObject.Parse(line);
            return json["is_error"] is JValue { Type: JTokenType.Boolean } flag && (bool)flag && json["result"] is JValue { Type: JTokenType.String } text
                ? FirstLine((string?)text)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
