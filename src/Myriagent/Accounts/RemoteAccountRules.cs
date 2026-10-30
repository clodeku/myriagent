using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Accounts;

/// <summary>
/// 遠端登入前置條件的事實（UI 執行緒收集，WP4.8 D6）。新建帳戶時 Exists=false、ManagedFolder=true、Status=NotLoggedIn、
/// IsSystemDefault=false（新建的一律是 app 的隔離資料夾）
/// </summary>
/// <param name="ManagedFolder">設定資料夾是 app 管理的隔離資料夾（AccountManager.GetDeletableAccountRoot 不是 null）</param>
/// <param name="LoginExpired">Claude 的登入已到期（AccountRuntimeState.LoginExpiresAt 已過；Codex 一律 false）</param>
/// <param name="AccountLoginBusy">這個帳戶正在登入</param>
/// <param name="AnyLoginActive">這台正在登入任何帳戶（AccountManager.HasActiveLogin；全機同時只有一個登入）</param>
/// <param name="CliPaused">這個種類的 CLI 正在更新（登入用的程序會擋住換檔）</param>
/// <param name="CliInstalled">找得到 CLI（AccountManager.ResolveLauncher 不是 null）</param>
internal sealed record RemoteLoginFacts(AgentProvider Provider, bool Exists, bool IsSystemDefault, bool ManagedFolder, AccountStatus Status,
    bool LoginExpired, bool AccountLoginBusy, bool AnyLoginActive, bool CliPaused, bool CliInstalled);

/// <summary>Code 是 null＝放行；否則是 RemoteResultCodes 的值，Message 是給人看的臺灣繁體中文（≤1000 字，直接成為瀏覽器的 toast）</summary>
internal sealed record RemoteDecision(string? Code, string? Message)
{
    public bool Allowed => Code is null;

    public static RemoteDecision Allow { get; } = new(null, null);
}

/// <summary>目錄裡這個邏輯帳戶的預期身分（都可能是 null＝不明，不比）</summary>
/// <param name="IdentityKey">目錄的身分鍵（Codex：<c>codex:{email}</c>；Claude：<c>claude:{org}:{email}</c> 或 <c>claude:{email}</c>）</param>
/// <param name="Email">目錄的預期信箱（CatalogPayloads 的 ExpectedEmail）</param>
/// <param name="OrgId">目錄的預期 org；只記錄，不拿來自動登出（D12）</param>
internal sealed record RemoteLoginExpectation(string? IdentityKey, string? Email, string? OrgId)
{
    public static RemoteLoginExpectation None { get; } = new(null, null, null);
}

/// <summary>遠端登入完成後的身分比對（D12）：Unknown＝任一邊不明，不登出</summary>
internal enum RemoteIdentityVerdict
{
    Unknown,
    Match,
    Mismatch,
}

/// <summary>遠端登入開始的里程碑（D7）：Codex 拿到合格的驗證碼、Claude 的登入主控台已啟動；Failed＝登入在里程碑之前就結束或逾時</summary>
internal enum RemoteLoginMilestone
{
    CodeShown,
    ConsoleOpened,
    /// <summary>Claude 的 app 內登入給了登入網址（2026-10-26）：在手機或其他電腦打開，登入後把授權碼貼回來（accountLoginCode）</summary>
    UrlShown,
    Failed,
}

/// <summary>遠端登入開始的結果（AccountManager.StartRemoteLoginAsync）</summary>
/// <param name="Code">Failed 時的 RemoteResultCodes 值</param>
/// <param name="Message">Failed 時給人看的說明</param>
/// <param name="DeviceCode">CodeShown 時的驗證碼（輸出由它組成，不再從狀態讀；LoginId 絕不輸出）</param>
internal sealed record RemoteLoginStart(RemoteLoginMilestone Milestone, string? Code = null, string? Message = null, CodexDeviceCode? DeviceCode = null,
    Uri? LoginUrl = null);

/// <summary>遠端登出的 CLI 結果（D16）：Done＝結束碼 0；Failed＝其他結束碼；TimedOut＝超過期限還沒結束</summary>
internal enum RemoteLogoutResult
{
    Done,
    Failed,
    TimedOut,
}

/// <summary>遠端登出前置條件的事實（UI 執行緒收集）</summary>
/// <param name="LoginBusy">這個帳戶正在登入</param>
/// <param name="CliInstalled">找得到 CLI（ResolveLauncher 不是 null）</param>
internal sealed record RemoteLogoutFacts(AgentProvider Provider, AccountStatus Status, bool CliPaused, bool LoginBusy, bool CliInstalled);

/// <summary>遠端 CLI 安裝、更新前的事實（CliManager.Get(provider) 的 Busy、Installed、InstallBroken）</summary>
/// <param name="InstallBroken">已安裝但損壞（CliView.InstallBroken 有說明）</param>
internal sealed record RemoteCliFacts(bool Busy, bool Installed, bool InstallBroken);

/// <summary>
/// 遠端帳戶與 CLI 操作（WP4.8：網頁對指定的電腦登入、登出、安裝與更新 CLI）的純規則：前置條件、身分比對、期限與給人看的文字。
/// 代碼一律是 <see cref="RemoteResultCodes"/> 的值；訊息會直接成為瀏覽器的 toast，所以不放信箱、網址、驗證碼。
/// 代理種類一律 switch、未知值丟例外；不進同步的種類（<see cref="ProviderSyncKind.LocalOnly"/>）一律 policy
/// </summary>
internal static class RemoteAccountRules
{
    /// <summary>遠端登入等里程碑的期限（D8）：閘門 30 秒沒有完成信號就是 unknown，留 5 秒給拒絕與回滾</summary>
    public static readonly TimeSpan StartDeadline = TimeSpan.FromSeconds(25);

    /// <summary>遠端登出等 CLI 結束的期限（D16）</summary>
    public static readonly TimeSpan LogoutDeadline = TimeSpan.FromSeconds(20);

    /// <summary>身分比對期間（identityHold，D14）延後的查詢多久之後再試</summary>
    public static readonly TimeSpan IdentityHoldRetry = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 遠端登入的前置條件（D6；取第一個符合的，都在起任何程序之前檢查、什麼都不改）：
    /// 1 不能從遠端操作帳戶的種類 policy；2 Claude、Antigravity 帶裝置碼 invalid；3 Codex、Grok 沒帶裝置碼 invalid；4 Codex、Grok 是系統預設或不是受管資料夾 policy；
    /// 5 已有可用的登入（Ready 而且沒到期、ApiKey、Checking）conflict；6 這個帳戶正在登入 conflict；7 這台正在登入別的帳戶 conflict；
    /// 8 CLI 正在更新 conflict；9 沒有安裝 CLI conflict
    /// </summary>
    public static RemoteDecision CheckLogin(RemoteLoginFacts facts, LoginMethod method)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var rule = LoginRuleOf(facts.Provider);
        var deviceCode = method switch
        {
            LoginMethod.Default => false,
            LoginMethod.DeviceCode => true,
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
        };
        var product = AgentProviders.ProductName(facts.Provider);

        // 1. 不能從遠端操作帳戶的種類（目前沒有：四種都可以）
        if (!rule.Remote)
            return new(RemoteResultCodes.Policy, LocalOnlyAccountMessage(product));
        // 2.、3. 登入方式：Codex、Grok 用裝置碼，而且從網頁只能用裝置碼（不在那台開瀏覽器）
        if (deviceCode && !rule.DeviceCode)
            return new(RemoteResultCodes.Invalid, "只有 Codex、Grok 帳戶能用裝置碼登入");
        if (!deviceCode && rule.DeviceCode)
            return new(RemoteResultCodes.Invalid, $"從網頁登入 {product} 帳戶只能用裝置碼");
        // 4. 只登 app 管理的隔離資料夾（擁有者指示 3：絕不碰電腦預設的 ~/.codex、~/.grok，未登入也一樣；Grok 是設計 2026-10-22 的 D5）
        if (rule.ManagedFolderOnly && facts.IsSystemDefault)
            return new(RemoteResultCodes.Policy, SystemDefaultFolderMessage(product));
        if (rule.ManagedFolderOnly && !facts.ManagedFolder)
            return new(RemoteResultCodes.Policy, UnmanagedFolderMessage(product));
        // 5. 已有可用的登入就不重登（Claude 的登入已到期時放行）
        if (facts.Status == AccountStatus.Ready && !facts.LoginExpired)
            return new(RemoteResultCodes.Conflict, "那台的這個帳戶已經登入；要換帳號請先登出那台");
        if (facts.Status == AccountStatus.ApiKey)
            return new(RemoteResultCodes.Conflict, "那台的這個帳戶以 API Key 登入，從網頁不能改成訂閱登入；請到那台處理");
        if (facts.Status == AccountStatus.Checking)
            return new(RemoteResultCodes.Conflict, "那台還在檢查這個帳戶的登入狀態，請稍後再試");
        // 6. 這個帳戶正在登入
        if (facts.AccountLoginBusy)
            return new(RemoteResultCodes.Conflict, AccountLoginBusyMessage);
        // 7. 全機同時只有一個登入
        if (facts.AnyLoginActive)
            return new(RemoteResultCodes.Conflict, "那台正在登入另一個帳戶，請等它完成後再試");
        // 8. CLI 正在更新
        if (facts.CliPaused)
            return new(RemoteResultCodes.Conflict, $"那台的 {product} CLI 正在更新，請等更新完成後再登入");
        // 9. 沒有安裝 CLI
        if (!facts.CliInstalled)
            return new(RemoteResultCodes.Conflict, $"那台還沒有安裝 {product} CLI，請先安裝");
        return RemoteDecision.Allow;
    }

    /// <summary>
    /// Codex 的身分比對（D12）：<see cref="CatalogSyncRules.CodexIdentityKey"/>(信箱) 和目錄的身分鍵相同（區分大小寫，同伺服器 Catalog 的判斷）＝Match，
    /// 不同＝Mismatch；目錄的鍵、信箱任一邊不明＝Unknown
    /// </summary>
    public static RemoteIdentityVerdict CheckCodexIdentity(string? catalogIdentityKey, string? email) => CheckEmailIdentity("codex:", catalogIdentityKey, email);

    /// <summary>
    /// 以信箱當身分鍵的種類（Codex <c>codex:</c>、Grok <c>grok:</c>、Antigravity <c>antigravity:</c>）的身分比對：
    /// <see cref="CatalogSyncRules.EmailIdentityKey"/>(前綴, 信箱) 和目錄的身分鍵相同＝Match，不同＝Mismatch；任一邊不明＝Unknown
    /// </summary>
    public static RemoteIdentityVerdict CheckEmailIdentity(string prefix, string? catalogIdentityKey, string? email)
    {
        if (string.IsNullOrWhiteSpace(catalogIdentityKey))
            return RemoteIdentityVerdict.Unknown;
        if (CatalogSyncRules.EmailIdentityKey(prefix, email) is not { } key)
            return RemoteIdentityVerdict.Unknown;
        return string.Equals(key, catalogIdentityKey, StringComparison.Ordinal) ? RemoteIdentityVerdict.Match : RemoteIdentityVerdict.Mismatch;
    }

    /// <summary>
    /// Claude 的預期信箱：目錄鍵是 <c>claude:</c> 開頭而且最後一個 <c>:</c> 之後有信箱段就用它（同 CatalogSyncRules.ClaudeIdentityKey 的比對）；
    /// 否則用目錄的 Email；都沒有回 null
    /// </summary>
    public static string? ClaudeExpectedEmail(RemoteLoginExpectation expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        const string prefix = "claude:";
        var key = expected.IdentityKey;
        if (key is not null && key.StartsWith(prefix, StringComparison.Ordinal))
        {
            var segment = key[(key.LastIndexOf(':') + 1)..].Trim();
            if (segment.Length > 0)
                return segment;
        }
        var email = expected.Email?.Trim();
        return string.IsNullOrEmpty(email) ? null : email;
    }

    /// <summary>
    /// Claude 的身分比對（D12）：只比信箱（不分大小寫）；org 一律不拿來判斷（CLI 設定的 organizationUuid 沒有確認，
    /// 表示法也可能和目錄不同，比了會把正確的登入自動登出）。預期信箱或登入的信箱不明＝Unknown
    /// </summary>
    public static RemoteIdentityVerdict CheckClaudeIdentity(RemoteLoginExpectation expected, string? email)
    {
        var want = ClaudeExpectedEmail(expected);
        var got = email?.Trim();
        if (want is null || string.IsNullOrEmpty(got))
            return RemoteIdentityVerdict.Unknown;
        return string.Equals(want, got, StringComparison.OrdinalIgnoreCase) ? RemoteIdentityVerdict.Match : RemoteIdentityVerdict.Mismatch;
    }

    /// <summary>
    /// 遠端登出的前置條件（D16）：不進同步的種類 policy；CLI 正在更新、這個帳戶正在登入 busy（什麼都沒執行，「所有電腦登出」由伺服器重送）；
    /// 沒有安裝 CLI conflict。放行之後狀態是未登入就直接 done（<see cref="IsAlreadyLoggedOut"/>）
    /// </summary>
    public static RemoteDecision CheckLogout(RemoteLogoutFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var product = AgentProviders.ProductName(facts.Provider);
        var rule = LoginRuleOf(facts.Provider);
        if (!rule.Remote)
            return new(RemoteResultCodes.Policy, LocalOnlyAccountMessage(product));
        // Antigravity 沒有登出子指令（只有互動介面的 /logout，設計 2026-10-22 的 D4）
        if (!rule.Logout)
            return new(RemoteResultCodes.Policy, AntigravityLogoutMessage);
        if (facts.CliPaused)
            return new(RemoteResultCodes.Busy, $"那台的 {product} CLI 正在更新，稍後再試");
        if (facts.LoginBusy)
            return new(RemoteResultCodes.Busy, "這個帳戶正在那台登入，稍後再試");
        if (!facts.CliInstalled)
            return new(RemoteResultCodes.Conflict, $"那台沒有安裝 {product} CLI，無法登出");
        return RemoteDecision.Allow;
    }

    /// <summary>本來就未登入：遠端登出直接回 done，不跑 CLI</summary>
    public static bool IsAlreadyLoggedOut(RemoteLogoutFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.Status == AccountStatus.NotLoggedIn;
    }

    /// <summary>
    /// 遠端 cliInstall／cliUpdate 的前置條件（D17）：能從遠端操作的種類（四種都可以，會在那台開安裝視窗）；正在檢查、安裝或更新 conflict；
    /// cliUpdate 沒安裝 conflict（免得落進安裝）；cliInstall 已安裝而且沒損壞 conflict（免得重裝並排空對話）。
    /// cmd 不是這兩個丟例外（呼叫端寫錯）
    /// </summary>
    public static RemoteDecision CheckCliAction(string cmd, AgentProvider provider, RemoteCliFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var install = cmd switch
        {
            "cliInstall" => true,
            "cliUpdate" => false,
            _ => throw new ArgumentOutOfRangeException(nameof(cmd), cmd, "不是遠端 CLI 指令"),
        };
        var product = AgentProviders.ProductName(provider);
        if (!LoginRuleOf(provider).Remote)
            return new(RemoteResultCodes.Policy, $"{product} CLI 只能在那台電腦安裝與更新");
        if (facts.Busy)
            return new(RemoteResultCodes.Conflict, $"那台的 {product} CLI 正在檢查、安裝或更新，請等它完成");
        if (!install && !facts.Installed)
            return new(RemoteResultCodes.Conflict, $"那台還沒有安裝 {product} CLI，請改用安裝");
        if (install && facts.Installed && !facts.InstallBroken)
            return new(RemoteResultCodes.Conflict, $"那台已經安裝 {product} CLI；要換新版請用更新");
        return RemoteDecision.Allow;
    }

    /// <summary>
    /// D15：帳戶狀態的說明文字。有遠端登入身分不符的說明（AccountRuntimeState.RemoteLoginNote）就用它取代預設說明，
    /// 於是經 link.upsert 到網頁矩陣的那一格；沒有（或空白）用 <paramref name="described"/>
    /// </summary>
    public static string StatusTextWithNote(string? note, string described) =>
        string.IsNullOrWhiteSpace(note) ? described : note;

    /// <summary>Claude 遠端登入的里程碑：主控台已在那台開啟（done:false 的 accountLogin 訊息）</summary>
    /// <summary>Claude 的 app 內登入給了登入網址（遠端登入的第一則 accountLogin，帶 loginUrl）</summary>
    public const string UrlShownMessage = "請打開登入網址登入 Claude（可以用這支手機），登入後把頁面上的授權碼貼回這裡";

    public static string ConsoleOpenedMessage => "已在那台電腦開啟 Claude 登入視窗，要有人在那台電腦前完成登入";

    /// <summary>
    /// 遠端登入開始時唯一的一則輸出（D9：處理者以 <c>context.Complete</c> 送出，之後的進度不轉；型錄允許 accountLogin）：
    /// <c>{type:"accountLogin", accountId, message, done:false, success:null, deviceCode?}</c>。
    /// CodeShown 的 deviceCode 是 <c>{userCode, verificationUrl, expiresAt（毫秒，JSON 整數）}</c>，由 <see cref="RemoteLoginStart.DeviceCode"/> 組成
    /// （不從狀態讀：complete_fail 時狀態裡的驗證碼可能已被清掉；LoginId 絕不輸出），message 是 <see cref="CodexDeviceCodeRules.ProgressText"/>；
    /// ConsoleOpened 的 message 是 <see cref="ConsoleOpenedMessage"/>、沒有 deviceCode。Failed 沒有輸出（被拒時只 Reject，D9），丟例外。
    /// 鍵與值的型別以 <c>tests/Myriagent.Tests/Hub/Samples/wp48-accountLogin-*.json</c> 固定，伺服器的 <c>CommandOutputRules.Project</c>
    /// 以同一份樣本測（步 10，D32）；改這裡的形狀要兩邊一起改
    /// </summary>
    /// <param name="accountId">這台的本機帳戶 id（伺服器轉給瀏覽器時換成瀏覽器看到的 id）</param>
    /// <param name="now">算 message 裡「幾分鐘內有效」的時間</param>
    public static JObject LoginOutput(string accountId, RemoteLoginStart start, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(start);
        var output = new JObject
        {
            ["type"] = "accountLogin",
            ["accountId"] = accountId,
        };
        switch (start.Milestone)
        {
            case RemoteLoginMilestone.CodeShown:
                var code = start.DeviceCode ?? throw new ArgumentException("CodeShown 一定帶驗證碼", nameof(start));
                output["message"] = CodexDeviceCodeRules.ProgressText(code, now);
                output["done"] = false;
                output["success"] = null;
                output["deviceCode"] = new JObject
                {
                    ["userCode"] = code.UserCode,
                    ["verificationUrl"] = code.VerificationUrl.AbsoluteUri,
                    ["expiresAt"] = code.ExpiresAt,
                };
                return output;
            case RemoteLoginMilestone.ConsoleOpened:
                output["message"] = ConsoleOpenedMessage;
                output["done"] = false;
                output["success"] = null;
                return output;
            case RemoteLoginMilestone.UrlShown:
                var url = start.LoginUrl ?? throw new ArgumentException("UrlShown 一定帶登入網址", nameof(start));
                output["message"] = UrlShownMessage;
                output["done"] = false;
                output["success"] = null;
                output["loginUrl"] = url.AbsoluteUri;
                return output;
            case RemoteLoginMilestone.Failed:
                throw new ArgumentException("被拒的遠端登入沒有輸出（只 Reject）", nameof(start));
            default:
                throw new ArgumentOutOfRangeException(nameof(start), start.Milestone, null);
        }
    }

    /// <summary>期限（<see cref="StartDeadline"/>）內沒有到里程碑、已取消那次登入（conflict 的訊息）</summary>
    public static string StartTimeoutMessage(AgentProvider provider)
    {
        var seconds = (int)StartDeadline.TotalSeconds;
        return provider switch
        {
            AgentProvider.Codex => $"那台的 Codex 在 {seconds} 秒內沒有開始登入，已取消；請再試一次",
            AgentProvider.Claude => $"那台的 Claude Code 在 {seconds} 秒內沒有開啟登入視窗，已取消；請再試一次",
            AgentProvider.Antigravity or AgentProvider.Grok => $"那台的 {AgentProviders.ProductName(provider)} 在 {seconds} 秒內沒有開始登入，已取消；請再試一次",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
    }

    /// <summary>
    /// 身分不符的說明（D15；AccountRuntimeState.RemoteLoginNote，經 link.upsert 發布）：<paramref name="loggedOut"/>＝在同一個資料夾的登出成功。
    /// 不含信箱（會進伺服器與網頁）
    /// </summary>
    public static string MismatchNote(bool loggedOut) => loggedOut
        ? "上次遠端登入的帳號和目錄不同，已在這台自動登出"
        : "上次遠端登入的帳號和目錄不同，自動登出沒有成功，請到這台登出";

    /// <summary>身分不符的系統匣通知（D15）：帶帳戶名稱、不含信箱與驗證碼</summary>
    public static string MismatchNotifyText(string label, bool loggedOut) => loggedOut
        ? $"從網頁登入的「{label}」帳號和目錄不同，已在這台自動登出"
        : $"從網頁登入的「{label}」帳號和目錄不同，自動登出沒有成功，請在這台登出";

    /// <summary>遠端登入在里程碑之前就結束、又沒有任何進度可以說明時（帳戶不見了、已經在登入中）的 conflict 訊息</summary>
    public static string LoginNotStartedMessage => "那台沒有開始登入，請看那一格的狀態後再試一次";

    /// <summary>遠端登入在里程碑之前被取消（那台的人按了取消、帳戶被移除）的 conflict 訊息</summary>
    public static string LoginCanceledMessage => "那台的登入已取消";

    /// <summary>遠端登出沒有回報成功（結束碼不是 0 或逾時，unknown 的訊息）</summary>
    public static string LogoutFailedMessage => "登出沒有在時限內完成或 CLI 回報失敗，可能已經登出；請看那一格的狀態";

    /// <summary>遠端登入的目標：那台對應的本機帳戶和帳戶目錄的種類不同（conflict，D21；手改過帳戶清單才會發生）</summary>
    public static string TargetKindMismatchMessage => "那台對應的本機帳戶和帳戶目錄的種類不同，請到那台處理";

    private static string SystemDefaultFolderMessage(string product) =>
        $"那台的這個 {product} 帳戶用的是電腦預設的設定資料夾，從網頁只能登入 app 的隔離帳戶；請到那台登入，或在那台新增一個 {product} 帳戶";

    private static string UnmanagedFolderMessage(string product) =>
        $"那台的這個 {product} 帳戶不在 app 管理的資料夾裡，從網頁只能登入 app 的隔離帳戶；請到那台登入，或在那台新增一個 {product} 帳戶";

    /// <summary>Antigravity 的遠端登出（D4）</summary>
    public const string AntigravityLogoutMessage = "Antigravity 沒有登出指令，請到那台的 agy 互動介面輸入 /logout";

    private const string AccountLoginBusyMessage = "這個帳戶正在那台登入；要重新開始請先取消那次登入";

    private static string LocalOnlyAccountMessage(string product) => $"{product} 的帳戶目前不能從網頁登入或登出，請到那台電腦操作";

    /// <param name="Remote">可以從網頁操作（登入、安裝、更新）</param>
    /// <param name="DeviceCode">從網頁登入一律用裝置碼（不是就一律不用）</param>
    /// <param name="ManagedFolderOnly">只登 app 管理的隔離資料夾</param>
    /// <param name="Logout">可以從網頁登出（有登出子指令）</param>
    private sealed record LoginRule(bool Remote, bool DeviceCode, bool ManagedFolderOnly, bool Logout);

    private static LoginRule LoginRuleOf(AgentProvider provider) => provider switch
    {
        // Claude 在那台開主控台（要有人在那台電腦前），系統預設的 ~/.claude 也可以
        AgentProvider.Claude => new(Remote: true, DeviceCode: false, ManagedFolderOnly: false, Logout: true),
        AgentProvider.Codex => new(Remote: true, DeviceCode: true, ManagedFolderOnly: true, Logout: true),
        // Grok：比照 Codex（設計 2026-10-22-grok-antigravity-sync 的 D5：網址帶裝置碼，只登 app 管理的隔離資料夾）
        AgentProvider.Grok => new(Remote: true, DeviceCode: true, ManagedFolderOnly: true, Logout: true),
        // Antigravity：在那台開 agy 視窗（同 Claude，要有人在那台完成 Google 登入）；沒有登出子指令（D4）
        AgentProvider.Antigravity => new(Remote: true, DeviceCode: false, ManagedFolderOnly: false, Logout: false),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "沒有遠端帳戶規則的代理種類"),
    };
}
