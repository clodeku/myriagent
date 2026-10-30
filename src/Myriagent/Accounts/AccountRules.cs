using System.Diagnostics.CodeAnalysis;
using Myriagent.Agents;
using Myriagent.Usage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Accounts;

/// <summary>憑證檔裡 OAuth 權杖的狀況（AccountRules.InspectClaudeToken）</summary>
internal enum ClaudeTokenState
{
    /// <summary>沒有 OAuth 權杖（沒有憑證檔、沒有 claudeAiOauth）：沒有東西可換發</summary>
    None,

    /// <summary>距到期還有 10 分鐘以上</summary>
    Fresh,

    /// <summary>已過期或 10 分鐘內到期：短命的 claude 會開始換發卻不等結果</summary>
    Expiring,

    /// <summary>有權杖卻沒有 expiresAt：不知道何時到期，當成不安全</summary>
    MissingExpiry,

    /// <summary>accessToken 是空字串：CLI 換發被拒後清空的登入（登入已失效）</summary>
    Empty,

    /// <summary>內容讀不懂（CLI 可能正在寫入）</summary>
    Unreadable,
}

/// <summary>Claude 憑證檔的樣子（只讀大小、最後寫入時間與內容；判斷時只看 accessToken 有沒有值與 expiresAt）</summary>
internal sealed record ClaudeCredentialInfo(bool Exists, long Size, DateTime LastWriteUtc, string? Json)
{
    public static ClaudeCredentialInfo Missing { get; } = new(false, 0, default, null);
}

/// <summary>
/// CLI 全域設定檔（.claude.json，AccountRules.ParseClaudeGlobalConfig）裡和登入儲存位置有關的部分。
/// CLI 2.1.296 的 dWn()：cachedGrowthBookFeatures.tengu_windows_credman 為 true（或 CLAUDE_CODE_FORCE_WINDOWS_CREDMAN=1，app 啟動的 CLI 會被剝掉）
/// 就把權杖改存 Windows 認證管理員，第一次寫入成功時刪掉 .credentials.json；設定檔不存在或讀不懂時 CLI 當成沒有啟用
/// </summary>
/// <param name="Readable">讀得到也讀得懂（檔案不存在也算：CLI 自己也當成沒有啟用認證管理員）</param>
/// <param name="CredentialManager">cachedGrowthBookFeatures.tengu_windows_credman 是 true：權杖存在 Windows 認證管理員，app 看不到期限</param>
/// <param name="HasOAuthAccount">有 oauthAccount（OAuth 登入時寫入，CLI 登出會清掉）</param>
/// <param name="OAuthEmail">oauthAccount.emailAddress</param>
/// <param name="OAuthOrgId">oauthAccount.organizationUuid（以 IsValidOrgId 過濾；E-13 確認之前不當成 orgId，AccountIdentityRules.GlobalConfigOrgConfirmed）</param>
internal sealed record ClaudeGlobalConfigInfo(bool Readable, bool CredentialManager, bool HasOAuthAccount, string? OAuthEmail, string? OAuthOrgId = null)
{
    /// <summary>設定檔不存在：沒有啟用認證管理員、沒有 OAuth 登入</summary>
    public static ClaudeGlobalConfigInfo Missing { get; } = new(true, false, false, null);

    /// <summary>設定檔存在卻讀不到或讀不懂（CLI 可能正在寫入）：無法判斷權杖在哪裡</summary>
    public static ClaudeGlobalConfigInfo Unreadable { get; } = new(false, false, false, null);
}

/// <summary>為什麼要確認 Claude 的登入狀態（決定能不能用啟動時的身分快取）</summary>
internal enum ClaudeStatusReason
{
    /// <summary>app 啟動時</summary>
    Startup,

    /// <summary>憑證檔變了（監看）：CLI 換發了權杖、在外部登入或登出</summary>
    CredentialsChanged,

    /// <summary>使用者要求（帳戶卡的更新、登入或登出之後、CLI 動作之後）</summary>
    Manual,

    /// <summary>定期重查（未登入、未安裝、上次檢查失敗的帳戶）</summary>
    Periodic,

    /// <summary>這個帳戶的對話剛跑完一輪（保守模式的帳戶在這時重新看 CLI 設定檔，發現登出就改判未登入）</summary>
    AfterTurn,
}

/// <summary>確認 Claude 登入狀態的方式（AccountRules.DecideClaudeStatusCheck）</summary>
internal enum ClaudeStatusDecision
{
    /// <summary>跑 claude auth status</summary>
    RunAuthStatus,

    /// <summary>用啟動身分快取（憑證檔沒變）：直接判定已登入，不跑 CLI</summary>
    UseCache,

    /// <summary>權杖快到期（或有權杖卻沒有 expiresAt）：不跑 CLI，等 CLI 在對話中換發後再查</summary>
    TokenPending,

    /// <summary>權杖是空字串：登入已失效，請重新登入（不跑 CLI）</summary>
    LoginExpired,

    /// <summary>
    /// 憑證檔不存在、權杖存在 Windows 認證管理員（CLI 設定啟用了認證管理員、還有 OAuth 登入）：保守模式——看不到權杖期限，
    /// 任何理由都不跑短命的 claude（auth status、能力探查），沿用上次確認的身分（沒有就以 CLI 設定的 oauthAccount 判定已登入）
    /// </summary>
    Conservative,

    /// <summary>憑證檔存在卻讀不到或讀不懂（CLI 可能正在寫入）：不能判斷權杖，不跑 CLI，稍後再查</summary>
    CredentialsUnreadable,

    /// <summary>憑證檔不存在、CLI 設定檔又讀不到或讀不懂：不知道權杖是不是在認證管理員，不跑 CLI，稍後再查（曾經登入就沿用快取）</summary>
    ConfigUnreadable,
}

/// <summary>
/// 帳戶的純函式規則：子程序環境變數、設定資料夾與憑證檔位置、登入狀態判讀、方案名稱、預設顏色與名稱。
/// </summary>
internal static class AccountRules
{
    /// <summary>帳戶代表色調色盤（畫面上的選色也用這組）</summary>
    public static readonly IReadOnlyList<string> Palette = new[]
    {
        "#D97757", "#E5A04B", "#E06C75", "#C678DD", "#7C8CF8", "#4FA3F7", "#19C3A6", "#3FB950", "#A3B83A", "#9AA4B2",
    };

    /// <summary>永遠不傳給子程序的變數：API Key／Token（會改走 API 計費而不是訂閱）</summary>
    private static readonly HashSet<string> SecretVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "OPENAI_API_KEY", "CODEX_API_KEY",
    };

    /// <summary>繼承的 CLAUDE* 變數中要保留的（使用者自己的設定）；其他是從 Claude Code session 內啟動時帶進來的，要剝除</summary>
    private static readonly HashSet<string> ClaudeVariablesToKeep = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLAUDE_CONFIG_DIR",
        "CLAUDE_CODE_GIT_BASH_PATH",
    };

    /// <summary>
    /// agy 程序另外剝除的變數：Gemini API Key 會改走 API 計費、GOOGLE_GEMINI_BASE_URL 會把模型呼叫轉到別的端點。
    /// AGY_ADC_AUTH／GOOGLE_APPLICATION_CREDENTIALS 是 agy 正式的登入方式（Application Default Credentials），保留。
    /// 只在 agy 程序剝：Claude、Codex 的工具（使用者自己的腳本）可能正需要這些變數
    /// </summary>
    private static readonly HashSet<string> AntigravitySecretVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_GEMINI_BASE_URL",
    };

    /// <summary>
    /// agy 給自己子程序的變數字首（對話 id、language server 位址、CSRF token…）：app 從 agy 的工作階段裡啟動時會繼承到，
    /// 傳下去會讓新的 agy 連到別人的 language server、工具以為自己在那個對話裡
    /// </summary>
    private const string AntigravityInheritedPrefix = "ANTIGRAVITY_";

    /// <summary>
    /// grok 程序另外剝除的變數：XAI_API_KEY 會改走 API 計費；GROK_AUTH_PATH、GROK_CONFIG／GROK_CONFIG_PATH 會讓 grok 讀別處的登入與設定；
    /// GROK_LEADER_SOCKET 會讓 grok 連到別人的 leader（app 從 grok 的工作階段裡啟動時會繼承到）。
    /// GROK_HOME 不在此列：系統預設帳戶保留使用者自己設的，隔離帳戶由 ApplyEnvironment 改成它的資料夾
    /// </summary>
    private static readonly HashSet<string> GrokSecretVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "XAI_API_KEY", "GROK_AUTH_PATH", "GROK_CONFIG", "GROK_CONFIG_PATH", "GROK_LEADER_SOCKET",
    };

    /// <summary>
    /// 設定子程序環境：剝除 API Key、剝除父 Claude session 帶來的 CLAUDE*／CLAUDECODE（父 session 的 effort、訊息通道、session id）
    /// 與父 agy 工作階段的 ANTIGRAVITY_*，再依帳戶指定設定資料夾。系統預設帳戶保留繼承的 CLAUDE_CONFIG_DIR／CODEX_HOME（使用者自己設的）。
    /// Antigravity 只有系統預設帳戶（登入存在 Windows 認證管理員，一台電腦一個），另剝除 Gemini API Key 類變數，並關掉 agy 的背景自動更新；
    /// Grok 另剝除 xAI API Key 與指向別處設定的變數，一律關掉 grok 的背景自動更新（換版由 app 協調），隔離帳戶以 GROK_HOME 指定資料夾
    /// </summary>
    public static void ApplyEnvironment(AgentAccount account, IDictionary<string, string?> env)
    {
        ApplyToolEnvironment(env);
        if (account.Provider == AgentProvider.Antigravity)
        {
            foreach (var key in env.Keys.Where(AntigravitySecretVariables.Contains).ToList())
                env.Remove(key);
            env[AntigravityStreamRules.DisableAutoUpdateVariable] = AntigravityStreamRules.DisableAutoUpdateValue;
        }
        if (account.Provider == AgentProvider.Grok)
        {
            foreach (var key in env.Keys.Where(GrokSecretVariables.Contains).ToList())
                env.Remove(key);
            env[GrokAcpRules.DisableAutoUpdaterVariable] = "1";
        }
        if (account.IsSystemDefault)
            return;

        switch (account.Provider)
        {
            case AgentProvider.Claude:
                env["CLAUDE_CONFIG_DIR"] = account.ConfigDirectory;
                break;
            case AgentProvider.Codex:
                env["CODEX_HOME"] = account.ConfigDirectory;
                break;
            case AgentProvider.Antigravity:
                // 沒有隔離帳戶（AccountManager 不會建立）；萬一清單被手動改出來，照系統預設帳戶執行
                break;
            case AgentProvider.Grok:
                env[GrokAcpRules.HomeVariable] = account.ConfigDirectory;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(account), account.Provider, null);
        }
    }

    /// <summary>不分帳戶的 CLI 呼叫（查版本、更新）：只剝除 API Key、父 Claude session 與父 agy 工作階段的變數，設定資料夾維持繼承值</summary>
    public static void ApplyToolEnvironment(IDictionary<string, string?> env)
    {
        foreach (var key in env.Keys.ToList())
        {
            // 背景程式的標記（Background/BackgroundProgramRules）：app 自己是從某個對話開出來的（開發時）也不往下傳，只有對話的 CLI 由中樞重新放
            if (SecretVariables.Contains(key)
                || (key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) && !ClaudeVariablesToKeep.Contains(key))
                || key.StartsWith(AntigravityInheritedPrefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, Background.BackgroundProgramRules.MarkerVariable, StringComparison.OrdinalIgnoreCase))
                env.Remove(key);
        }
    }

    /// <summary>
    /// app 自己跑的短命 CLI 子指令（--version、claude update、auth status、能力探查、登出）：帳戶環境（account 為 null＝不分帳戶，
    /// 同 ApplyToolEnvironment）之外另帶 DISABLE_AUTOUPDATER=1——app 啟動的 claude 一律不在背景自動更新（7.2.4 保險；
    /// 它只關掉背景更新，claude update 照常可用，只有 DISABLE_UPDATES 才會擋 claude update）
    /// </summary>
    public static void ApplyCommandEnvironment(AgentAccount? account, IDictionary<string, string?> env)
    {
        if (account == null)
            ApplyToolEnvironment(env);
        else
            ApplyEnvironment(account, env);
        env[ClaudeStreamRules.DisableAutoUpdaterVariable] = "1";
    }

    /// <summary>帳戶實際使用的設定資料夾（Antigravity 是 %USERPROFILE%\.gemini\antigravity-cli；Grok 的系統預設帳戶是繼承的 GROK_HOME 或 %USERPROFILE%\.grok）</summary>
    public static string ResolveConfigDirectory(AgentAccount account)
    {
        switch (account.Provider)
        {
            case AgentProvider.Claude:
            {
                if (!account.IsSystemDefault)
                    return account.ConfigDirectory!;
                var inherited = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                return !string.IsNullOrWhiteSpace(inherited)
                    ? inherited
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            }
            case AgentProvider.Codex:
                return account.IsSystemDefault ? CodexConfigRules.ResolveHome(null) : account.ConfigDirectory!;
            case AgentProvider.Antigravity:
                return AntigravityStreamRules.ResolveHome(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            case AgentProvider.Grok:
            {
                if (!account.IsSystemDefault)
                    return account.ConfigDirectory!;
                var inherited = Environment.GetEnvironmentVariable(GrokAcpRules.HomeVariable);
                return !string.IsNullOrWhiteSpace(inherited)
                    ? inherited
                    : GrokAcpRules.ResolveHome(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(account), account.Provider, null);
        }
    }

    /// <summary>Claude 帳戶的 .credentials.json（只用在 Claude 的流程：讀權杖查用量、能力探查）</summary>
    public static string GetClaudeCredentialFile(AgentAccount account)
        => account.Provider == AgentProvider.Claude
            ? Path.Combine(ResolveConfigDirectory(account), ".credentials.json")
            : throw new ArgumentException("不是 Claude 帳戶", nameof(account));

    /// <summary>
    /// 登入憑證檔：Claude 的 .credentials.json、Codex 與 Grok 的 auth.json（監看它的變化以重新檢查狀態與用量，不讀內容）；
    /// Antigravity 的登入存在 Windows 認證管理員，沒有檔案可監看，回 null
    /// </summary>
    public static string? GetCredentialFile(AgentAccount account) => account.Provider switch
    {
        AgentProvider.Claude => Path.Combine(ResolveConfigDirectory(account), ".credentials.json"),
        AgentProvider.Codex => Path.Combine(ResolveConfigDirectory(account), "auth.json"),
        AgentProvider.Antigravity => null,
        AgentProvider.Grok => Path.Combine(ResolveConfigDirectory(account), "auth.json"),
        _ => throw new ArgumentOutOfRangeException(nameof(account), account.Provider, null),
    };

    /// <summary>
    /// Claude CLI 的全域設定檔（照 CLI 2.1.296 的 Tt()：設定資料夾裡舊的 .config.json 存在就用它，否則是
    /// CLAUDE_CONFIG_DIR（沒設時是使用者資料夾）底下的 .claude.json）。隔離帳戶的 CLAUDE_CONFIG_DIR 就是它的設定資料夾；
    /// 系統預設帳戶沿用繼承的 CLAUDE_CONFIG_DIR，沒設時是 %USERPROFILE%\.claude.json
    /// </summary>
    public static (string LegacyPath, string ConfigPath) GetClaudeGlobalConfigPaths(AgentAccount account)
    {
        var configDirectory = ResolveConfigDirectory(account);
        string home;
        if (!account.IsSystemDefault)
            home = account.ConfigDirectory!;
        else
        {
            var inherited = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            home = !string.IsNullOrWhiteSpace(inherited) ? inherited : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        return (Path.Combine(configDirectory, ".config.json"), Path.Combine(home, ".claude.json"));
    }

    /// <summary>
    /// 讀 CLI 全域設定檔的內容（ClaudeGlobalConfigInfo）：只看最上層的 cachedGrowthBookFeatures.tengu_windows_credman 與 oauthAccount
    /// 的 emailAddress、organizationUuid（設定檔可能有好幾 MB 的專案紀錄：逐一略過其他欄位，不整份載入）。空白或 null＝讀不到內容；格式不對＝讀不懂
    /// </summary>
    public static ClaudeGlobalConfigInfo ParseClaudeGlobalConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ClaudeGlobalConfigInfo.Unreadable;
        try
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                return ClaudeGlobalConfigInfo.Unreadable;
            bool credman = false, hasOAuth = false;
            string? email = null, orgId = null;
            while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
            {
                var name = (string?)reader.Value;
                if (!reader.Read())
                    return ClaudeGlobalConfigInfo.Unreadable;
                switch (name)
                {
                    case "cachedGrowthBookFeatures" when reader.TokenType == JsonToken.StartObject:
                        credman = JToken.ReadFrom(reader) is JObject features
                                  && features["tengu_windows_credman"] is JValue { Type: JTokenType.Boolean } flag && (bool)flag;
                        break;
                    case "oauthAccount" when reader.TokenType == JsonToken.StartObject:
                        if (JToken.ReadFrom(reader) is JObject account)
                        {
                            hasOAuth = true;
                            email = account["emailAddress"]?.Type == JTokenType.String ? account.Value<string>("emailAddress") : null;
                            orgId = account["organizationUuid"] is JValue { Type: JTokenType.String, Value: string org } && IsValidOrgId(org) ? org : null;
                        }
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            // 迴圈要停在最上層物件的結尾（停在內層的結尾＝檔案寫到一半）
            if (reader.TokenType != JsonToken.EndObject || reader.Depth != 0)
                return ClaudeGlobalConfigInfo.Unreadable;
            return new ClaudeGlobalConfigInfo(true, credman, hasOAuth, string.IsNullOrWhiteSpace(email) ? null : email.Trim(), orgId);
        }
        catch (JsonException)
        {
            return ClaudeGlobalConfigInfo.Unreadable;
        }
    }

    /// <summary>
    /// claude auth status --json 的輸出 → 狀態、email、方案。authMethod＝claude.ai 才是訂閱；其他（console、API Key、Bedrock）可用但走 API 計費。
    /// 只有讀到含 loggedIn 的 JSON 物件才下判斷；讀不到（逾時、啟動失敗、輸出被含大括號的警告打斷）回 Error：
    /// 這是這次檢查失敗，不能當成未登入（未登入會清掉用量、擋住送出，之後也不會自己重查）。
    /// </summary>
    public static (AccountStatus Status, string? Email, string? Plan) EvaluateClaudeAuthStatus(string? output)
    {
        var status = FindJsonObject(output, "loggedIn");
        if (status == null)
            return (AccountStatus.Error, null, null);
        if (!(status.Value<bool?>("loggedIn") ?? false))
            return (AccountStatus.NotLoggedIn, null, null);

        var email = status.Value<string>("email");
        var plan = DescribeClaudePlan(status.Value<string>("subscriptionType"), null);
        bool subscription = string.Equals(status.Value<string>("authMethod"), "claude.ai", StringComparison.OrdinalIgnoreCase);
        return (subscription ? AccountStatus.Ready : AccountStatus.ApiKey, email, plan ?? status.Value<string>("authMethod"));
    }

    /// <summary>orgId 的長度上限（伺服器 CatalogRules 的短文字上限）</summary>
    public const int MaxOrgIdLength = 128;

    /// <summary>
    /// claude auth status --json 的 orgId（多電腦同步的身分鍵要用，設計 6.2）：和 EvaluateClaudeAuthStatus 一樣只認含 loggedIn 的 JSON 物件；
    /// 沒有、不是字串、含空白或控制字元、超過 128 字回 null
    /// </summary>
    public static string? ReadClaudeOrgId(string? authStatusOutput)
        => FindJsonObject(authStatusOutput, "loggedIn")?["orgId"] is JValue { Type: JTokenType.String, Value: string orgId } && IsValidOrgId(orgId)
            ? orgId
            : null;

    /// <summary>合格的 orgId：1～128 字，不含空白、控制字元與代理字元（auth status、憑證檔與 CLI 設定的 organizationUuid 共用）</summary>
    public static bool IsValidOrgId([NotNullWhen(true)] string? orgId)
    {
        if (orgId is null || orgId.Length is 0 or > MaxOrgIdLength)
            return false;
        foreach (var c in orgId)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || char.IsSurrogate(c))
                return false;
        }
        return true;
    }

    /// <summary>
    /// 從輸出裡找第一個含指定欄位的完整 JSON 物件（前後夾雜的警告、stderr 都略過，即使它們也有大括號）
    /// </summary>
    private static JObject? FindJsonObject(string? text, string requiredProperty)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        for (int start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
        {
            try
            {
                using var reader = new JsonTextReader(new StringReader(text[start..])) { DateParseHandling = DateParseHandling.None };
                if (JToken.ReadFrom(reader) is JObject candidate && candidate.ContainsKey(requiredProperty))
                    return candidate;
            }
            catch (JsonException)
            {
                // 這個位置不是完整的 JSON：試下一個大括號
            }
        }
        return null;
    }

    /// <summary>claude auth status 沒有讀到結果時給人看的原因：逾時、stderr 第一行（啟動失敗的訊息、CLI 的錯誤），都沒有就說讀不到結果</summary>
    public static string DescribeClaudeAuthStatusFailure(string? stderr, bool timedOut)
    {
        if (timedOut)
            return "claude auth status 執行逾時";
        var line = (stderr ?? string.Empty).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (string.IsNullOrEmpty(line))
            return "讀不到 claude auth status 的結果";
        return line.Length <= 160 ? line : line[..160] + "…";
    }

    /// <summary>權杖剩下不到這麼久就不跑短命的 claude 子指令（實測 CLI 在剩不到約 5 分鐘時就會開始換發權杖）</summary>
    public static readonly TimeSpan ClaudeAuthStatusExpiryMargin = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 憑證檔裡 OAuth 權杖的狀況（只讀 accessToken 有沒有值與 expiresAt，不看權杖內容）：
    /// None＝沒有 OAuth 權杖（沒有憑證檔、沒有 claudeAiOauth 或沒有 accessToken 欄位：沒有東西可換發）；Fresh＝距到期還有 10 分鐘以上；
    /// Expiring＝已過期或 10 分鐘內到期；MissingExpiry＝有權杖卻沒有 expiresAt（不知道何時到期）；
    /// Empty＝accessToken 是空字串（CLI 換發被拒後清空的登入）；Unreadable＝內容讀不懂（CLI 可能正在寫入）
    /// </summary>
    public static ClaudeTokenState InspectClaudeToken(string? credentialsJson, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
            return ClaudeTokenState.None;
        try
        {
            if (JObject.Parse(credentialsJson)["claudeAiOauth"] is not JObject oauth)
                return ClaudeTokenState.None;
            var token = oauth["accessToken"];
            if (token == null || token.Type == JTokenType.Null)
                return ClaudeTokenState.None;
            if (token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)token))
                return ClaudeTokenState.Empty;
            var expires = oauth["expiresAt"];
            if (expires is not JValue { Type: JTokenType.Integer or JTokenType.Float } value)
                return ClaudeTokenState.MissingExpiry;
            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds((long)value.Value<double>());
            return expiresAt - ClaudeAuthStatusExpiryMargin > now ? ClaudeTokenState.Fresh : ClaudeTokenState.Expiring;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidCastException or OverflowException or ArgumentOutOfRangeException)
        {
            return ClaudeTokenState.Unreadable;
        }
    }

    /// <summary>
    /// 短命的 claude 子指令（claude auth status、能力探查 CapabilityProbe.ProbeClaudeAsync 的 claude -p、之後的 doctor 類指令）現在跑安不安全
    /// （設計 6.5 的統一守門；--version 不在此限）。實測 2.1.295／2.1.296 的 auth status 遇到已過期或 5 分鐘內到期的權杖會連到
    /// platform.claude.com 開始換發，卻約 0.2 秒就結束、不等結果也不寫回：換發若已送出，refresh token 就被用掉，之後 CLI 換發失敗、整個帳戶被登出。
    /// 只有權杖距到期還有 10 分鐘以上、或根本沒有 OAuth 權杖（沒有東西可換發）時才跑；空權杖、沒有 expiresAt、快到期、讀不懂一律不跑。
    /// app 絕不自己換發權杖：等 CLI 在對話中自己換發（憑證檔改變後監看會再查）
    /// </summary>
    public static bool CanRunShortLivedClaude(string? credentialsJson, DateTimeOffset now)
        => InspectClaudeToken(credentialsJson, now) is ClaudeTokenState.Fresh or ClaudeTokenState.None;

    /// <summary>
    /// 同上，另外看 CLI 全域設定（credentialsJson 為 null＝憑證檔不存在時才用得到）：憑證檔不存在而 CLI 啟用了 Windows 認證管理員
    /// （或設定檔讀不到、無法判斷），權杖可能就在認證管理員裡、app 看不到期限——一律不跑
    /// </summary>
    public static bool CanRunShortLivedClaude(string? credentialsJson, DateTimeOffset now, ClaudeGlobalConfigInfo globalConfig)
    {
        ArgumentNullException.ThrowIfNull(globalConfig);
        if (credentialsJson == null && (!globalConfig.Readable || globalConfig.CredentialManager))
            return false;
        return CanRunShortLivedClaude(credentialsJson, now);
    }

    /// <summary>憑證檔 claudeAiOauth.expiresAt（毫秒）；沒有或讀不懂回 null（只讀這個中繼欄位）</summary>
    public static long? ReadClaudeExpiresAt(string? credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
            return null;
        try
        {
            return JObject.Parse(credentialsJson)["claudeAiOauth"] is JObject oauth && oauth["expiresAt"] is JValue { Type: JTokenType.Integer or JTokenType.Float } value
                ? (long)value.Value<double>()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// 要不要跑 claude auth status 來確認登入狀態（設計 6.5）。順序：
    /// <list type="number">
    /// <item>
    /// 憑證檔不存在，看 CLI 全域設定（globalConfig；null＝沒讀，當成讀不到）：沒有啟用 Windows 認證管理員、或啟用了但 oauthAccount 已清掉（CLI 登出會清）
    /// ＝權杖不在任何地方，照常跑（沒有東西可換發；判斷未登入、API Key 或第三方登入）；啟用了而且還有 OAuth 登入＝保守模式，任何理由都不跑
    /// （權杖在認證管理員，看不到期限：對話一輪剛結束時權杖也可能只剩 5 分鐘出頭）；設定檔讀不到＝無法判斷，不跑、稍後再查
    /// </item>
    /// <item>權杖是空字串＝登入已失效（CLI 換發被拒後清空），不跑 CLI</item>
    /// <item>憑證檔讀不到或讀不懂＝稍後再查，不跑；快到期、沒有 expiresAt＝等權杖更新，不跑</item>
    /// <item>沒有 OAuth 權杖（API Key 等）＝照常跑</item>
    /// <item>權杖新鮮：啟動與定期重查時快取有效（憑證檔大小、修改時間、expiresAt 都沒變）＝用快取；憑證檔變了、使用者手動要求、身分未知＝跑</item>
    /// </list>
    /// </summary>
    public static ClaudeStatusDecision DecideClaudeStatusCheck(ClaudeCredentialInfo credentials, AccountIdentityCache? cache, ClaudeStatusReason reason, DateTimeOffset now,
        ClaudeGlobalConfigInfo? globalConfig = null)
    {
        if (!credentials.Exists)
        {
            var config = globalConfig ?? ClaudeGlobalConfigInfo.Unreadable;
            if (!config.Readable)
                return ClaudeStatusDecision.ConfigUnreadable;
            return config.CredentialManager && config.HasOAuthAccount ? ClaudeStatusDecision.Conservative : ClaudeStatusDecision.RunAuthStatus;
        }
        // 憑證檔存在卻讀不到（被占用：CLI 可能正在換發權杖）或讀不懂：不能判斷權杖，不跑
        if (credentials.Json == null)
            return ClaudeStatusDecision.CredentialsUnreadable;
        switch (InspectClaudeToken(credentials.Json, now))
        {
            case ClaudeTokenState.Empty:
                return ClaudeStatusDecision.LoginExpired;
            case ClaudeTokenState.Unreadable:
                return ClaudeStatusDecision.CredentialsUnreadable;
            case ClaudeTokenState.Expiring or ClaudeTokenState.MissingExpiry:
                return ClaudeStatusDecision.TokenPending;
            case ClaudeTokenState.None:
                return ClaudeStatusDecision.RunAuthStatus;
        }
        if ((reason is ClaudeStatusReason.Startup or ClaudeStatusReason.Periodic or ClaudeStatusReason.AfterTurn) && IsIdentityCacheValid(cache, credentials))
            return ClaudeStatusDecision.UseCache;
        return ClaudeStatusDecision.RunAuthStatus;
    }

    /// <summary>快取有效：曾經以 auth status 確認登入（Ready 或 ApiKey），而且憑證檔的大小、最後寫入時間、expiresAt 都和那時一樣</summary>
    public static bool IsIdentityCacheValid(AccountIdentityCache? cache, ClaudeCredentialInfo credentials)
        => cache is { Status: AccountStatus.Ready or AccountStatus.ApiKey }
           && credentials.Exists
           && cache.CredentialSize == credentials.Size
           && cache.CredentialWriteTicks == credentials.LastWriteUtc.Ticks
           && cache.ExpiresAt == ReadClaudeExpiresAt(credentials.Json);

    /// <summary>
    /// 兩個要補做的登入狀態檢查合併時取哪個理由：要求跑 auth status 的（使用者手動、憑證檔變了）優先於可以用快取的（對話結束後、啟動、定期）
    /// </summary>
    public static ClaudeStatusReason StrongerStatusReason(ClaudeStatusReason a, ClaudeStatusReason b)
    {
        static int Rank(ClaudeStatusReason reason) => reason switch
        {
            ClaudeStatusReason.Manual => 4,
            ClaudeStatusReason.CredentialsChanged => 3,
            ClaudeStatusReason.AfterTurn => 2,
            ClaudeStatusReason.Startup => 1,
            _ => 0,
        };
        return Rank(b) > Rank(a) ? b : a;
    }

    /// <summary>
    /// auth status 成功（Ready 或 ApiKey）後要存的快取；其他狀態（未登入、檢查失敗）回 null＝清掉快取。
    /// 憑證檔不存在時照樣記下「曾經登入」：大小與時間記 0，啟動時不會被當成有效的快取（保守模式沿用它的狀態、email 與方案）。
    /// orgId 一併記下（多電腦同步用；啟動時用快取就不必再跑 auth status 取得）
    /// </summary>
    public static AccountIdentityCache? BuildIdentityCache(AccountStatus status, string? email, string? plan, ClaudeCredentialInfo credentials, DateTime now,
        string? orgId = null)
        => status is AccountStatus.Ready or AccountStatus.ApiKey
            ? new AccountIdentityCache
            {
                Status = status,
                Email = email,
                Plan = plan,
                CredentialSize = credentials.Exists ? credentials.Size : 0,
                CredentialWriteTicks = credentials.Exists ? credentials.LastWriteUtc.Ticks : 0,
                ExpiresAt = ReadClaudeExpiresAt(credentials.Json),
                CheckedAt = now,
                OrgId = orgId,
            }
            : null;

    /// <summary>Claude 方案名稱：subscriptionType（max／pro／team…）＋ rateLimitTier（default_claude_max_20x → 20x）</summary>
    public static string? DescribeClaudePlan(string? subscriptionType, string? rateLimitTier)
    {
        if (string.IsNullOrWhiteSpace(subscriptionType))
            return null;
        // initialize 回報「Claude Max」、auth status 回報「max」：先去掉前綴再對應
        var key = subscriptionType.Trim();
        if (key.StartsWith("Claude ", StringComparison.OrdinalIgnoreCase))
            key = key[7..].Trim();
        if (key.Length == 0)
            return null;
        var name = key.ToLowerInvariant() switch
        {
            "max" => "Max",
            "pro" => "Pro",
            "team" => "Team",
            "enterprise" => "Enterprise",
            "free" => "Free",
            _ => char.ToUpperInvariant(key[0]) + key[1..],
        };
        var tier = rateLimitTier ?? string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(tier, @"(\d+)x$");
        return match.Success ? $"{name} {match.Groups[1].Value}x" : name;
    }

    /// <summary>Claude .credentials.json 的方案資訊（不讀權杖）</summary>
    public static string? ReadClaudePlanFromCredentials(string? credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
            return null;
        try
        {
            var oauth = JObject.Parse(credentialsJson)["claudeAiOauth"] as JObject;
            return DescribeClaudePlan(oauth?.Value<string>("subscriptionType"), oauth?.Value<string>("rateLimitTier"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Codex account/read 的結果 → 狀態、email、方案</summary>
    public static (AccountStatus Status, string? Email, string? Plan) EvaluateCodexAccount(JObject? result)
    {
        if (result?["account"] is not JObject account)
            return (AccountStatus.NotLoggedIn, null, null);
        return account.Value<string>("type") switch
        {
            "chatgpt" => (AccountStatus.Ready, account.Value<string>("email"), DescribeCodexPlan(account.Value<string>("planType"))),
            "apiKey" => (AccountStatus.ApiKey, null, "API Key"),
            var other => (AccountStatus.ApiKey, null, other),
        };
    }

    public static string? DescribeCodexPlan(string? planType) => planType switch
    {
        null or "" or "unknown" => null,
        "plus" => "Plus",
        "pro" => "Pro",
        "prolite" => "Pro Lite",
        "promax" => "Pro Max",
        "team" => "Team",
        "business" => "Business",
        "enterprise" => "Enterprise",
        "edu" => "Edu",
        "free" => "Free",
        "go" => "Go",
        var other => other,
    };

    /// <summary>
    /// 本機預設帳戶的固定 Id：帳戶清單遺失或損毀後重建時沿用同一個 Id，既有對話、用量歷史與模型設定才接得回來
    /// （隔離帳戶的 Id 是 32 碼十六進位，不會和這些撞名）
    /// </summary>
    public static string SystemDefaultId(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => "local-claude",
        AgentProvider.Codex => "local-codex",
        AgentProvider.Antigravity => "local-antigravity",
        AgentProvider.Grok => "local-grok",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>能不能新增隔離帳戶（各自的設定資料夾與登入）：Antigravity 的登入存在認證管理員的固定名稱，一台電腦只能有一個</summary>
    public static bool SupportsIsolatedAccounts(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude or AgentProvider.Codex or AgentProvider.Grok => true,
        AgentProvider.Antigravity => false,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 能不能開精簡模式：Claude、Codex 不載入外掛、hooks、MCP、CLAUDE.md／AGENTS.md；Antigravity 以 --disable-slash-commands 停用斜線指令與 Skills 展開；
    /// Grok 沒有「不載入使用者外掛與 MCP」的單一旗標，開了也不會生效，所以不提供
    /// </summary>
    public static bool SupportsSafeMode(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude or AgentProvider.Codex or AgentProvider.Antigravity => true,
        AgentProvider.Grok => false,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 隔離帳戶能不能共用系統預設帳戶的設定（全域指示、記憶、MCP、skills、rules；SharedCustomizations）：
    /// Claude、Codex、Grok 都支援隔離帳戶與對應預設資料夾共用；Antigravity 沒有隔離帳戶
    /// </summary>
    public static bool SupportsSharedCustomizations(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude or AgentProvider.Codex or AgentProvider.Grok => true,
        AgentProvider.Antigravity => false,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>有沒有額度可查（Claude 用量端點、Codex rateLimits、Antigravity 的 -p /usage、Grok 的 _x.ai/billing）</summary>
    public static bool HasUsageQuota(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude or AgentProvider.Codex or AgentProvider.Antigravity or AgentProvider.Grok => true,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>使用者看的產品名稱（帳戶名稱、提示文字）</summary>
    public static string ProviderName(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => "Claude",
        AgentProvider.Codex => "Codex",
        AgentProvider.Antigravity => "Antigravity",
        AgentProvider.Grok => "Grok",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>本機預設帳戶的名稱與顏色（第一次啟動、帳戶清單重建、升級後補上 Antigravity 或 Grok 時）</summary>
    public static (string Label, string Color) SystemDefaultAppearance(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => ("Claude（本機）", "#D97757"),
        AgentProvider.Codex => ("Codex（本機）", "#19C3A6"),
        AgentProvider.Antigravity => ("Antigravity（本機）", "#7C8CF8"),
        AgentProvider.Grok => ("Grok（本機）", "#A3B83A"),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 從用量時間窗的 key 判斷是哪種代理（帳戶清單遺失時找回舊版本機預設帳戶的 Id 用）：
    /// Claude 是 session／weekly／weekly:模型名，Codex 是 {limitId}:{分鐘數}，Antigravity 是 agy-*，Grok 是 grok-credits；像不只一種或都不像回 null
    /// </summary>
    public static AgentProvider? GuessProviderFromUsageKeys(IEnumerable<string> keys)
    {
        var found = new HashSet<AgentProvider>();
        foreach (var key in keys)
        {
            if (key is UsageRules.SessionKey or UsageRules.WeeklyKey || key.StartsWith(UsageRules.WeeklyKey + ":", StringComparison.Ordinal))
                found.Add(AgentProvider.Claude);
            else if (key == UsageRules.GrokCreditsKey)
                found.Add(AgentProvider.Grok);
            else if (key.StartsWith(UsageRules.AntigravityKeyPrefix, StringComparison.Ordinal))
                found.Add(AgentProvider.Antigravity);
            else if (key.Contains(':'))
                found.Add(AgentProvider.Codex);
        }
        return found.Count == 1 ? found.First() : null;
    }

    public static string DescribeStatus(AgentProvider provider, AccountStatus status) => status switch
    {
        AccountStatus.Checking => "檢查中…",
        AccountStatus.Ready => "已登入",
        AccountStatus.NotLoggedIn => provider switch
        {
            AgentProvider.Claude => "尚未登入 Claude 帳戶",
            AgentProvider.Codex => "尚未登入 ChatGPT 帳戶",
            AgentProvider.Antigravity => "尚未登入 Antigravity（Google 帳戶）",
            AgentProvider.Grok => "尚未登入 Grok（grok.com 帳戶）",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        },
        AccountStatus.ApiKey => "以 API Key 登入，會走 API 計費而不是訂閱額度",
        AccountStatus.NotInstalled => provider switch
        {
            AgentProvider.Claude => "這台電腦尚未安裝 Claude Code CLI",
            AgentProvider.Codex => "這台電腦尚未安裝 Codex CLI",
            AgentProvider.Antigravity => "這台電腦尚未安裝 Antigravity CLI",
            AgentProvider.Grok => "這台電腦尚未安裝 Grok Build CLI",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        },
        _ => "檢查失敗",
    };

    /// <summary>新帳戶的預設名稱：「Claude 帳戶 2」；同類已有幾個就往後編</summary>
    public static string DefaultLabel(AgentProvider provider, IEnumerable<AgentAccount> existing)
    {
        var name = ProviderName(provider);
        var labels = existing.Select(a => a.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < 1000; i++)
        {
            var label = i == 1 ? $"{name} 帳戶" : $"{name} 帳戶 {i}";
            if (!labels.Contains(label))
                return label;
        }
        return $"{name} 帳戶 {Guid.NewGuid():N}"[..20];
    }

    /// <summary>新帳戶的預設顏色：Claude 暖色、Codex 冷色、Antigravity 藍紫色、Grok 黃綠色，避開已用過的</summary>
    public static string DefaultColor(AgentProvider provider, IEnumerable<AgentAccount> existing)
    {
        var used = existing.Select(a => a.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preferred = provider switch
        {
            AgentProvider.Claude => new[] { "#D97757", "#E5A04B", "#E06C75", "#C678DD" },
            AgentProvider.Codex => new[] { "#19C3A6", "#4FA3F7", "#3FB950", "#7C8CF8" },
            AgentProvider.Antigravity => new[] { "#7C8CF8", "#C678DD", "#4FA3F7", "#3FB950" },
            AgentProvider.Grok => new[] { "#A3B83A", "#3FB950", "#E5A04B", "#9AA4B2" },
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
        return preferred.Concat(Palette).FirstOrDefault(c => !used.Contains(c)) ?? preferred[0];
    }

    /// <summary>只接受 #RRGGBB；其他回 null</summary>
    public static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return null;
        var c = color.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9A-Fa-f]{6}$") ? c.ToUpperInvariant() : null;
    }

    /// <summary>帳戶目錄（多電腦同步）的名稱上限：伺服器 CatalogRules.MaxLabelLength；本機輸入仍截到 40 字（Update）</summary>
    public const int MaxCatalogLabelLength = 60;

    /// <summary>帳戶目錄的顏色（伺服器接受 #RGB 與 #RRGGBB）：#RGB 展開成 #RRGGBB、一律大寫；其他格式回 null</summary>
    public static string? NormalizeCatalogColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
            return null;
        var c = color.Trim();
        if (c.Length is not (4 or 7) || c[0] != '#' || c.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0)
            return null;
        if (c.Length == 4)
            c = new string(new[] { '#', c[1], c[1], c[2], c[2], c[3], c[3] });
        return c.ToUpperInvariant();
    }

    /// <summary>
    /// 帳戶目錄的名稱：去掉控制、格式字元與單獨的代理字元（LabelRules），超過 60 字以文字元素為界截斷
    /// （不切斷代理字元對與組合字），再清理一次；清理後是空的回 null
    /// </summary>
    public static string? CleanCatalogLabel(string? label)
    {
        if (Myriagent.Security.LabelRules.Clean(label, int.MaxValue) is not { } cleaned)
            return null;
        if (cleaned.Length > MaxCatalogLabelLength)
        {
            var builder = new System.Text.StringBuilder(MaxCatalogLabelLength);
            var elements = System.Globalization.StringInfo.GetTextElementEnumerator(cleaned);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                if (builder.Length + element.Length > MaxCatalogLabelLength)
                    break;
                builder.Append(element);
            }
            cleaned = builder.ToString();
        }
        return Myriagent.Security.LabelRules.Clean(cleaned, MaxCatalogLabelLength);
    }

    /// <summary>
    /// Codex 查狀態時要送哪些 app-server 請求：account/read 一律送（登入狀態與 email 靠它）；
    /// account/rateLimits/read 只在用量閘門放行時送（多電腦同步時只有被指派的那台查，設計 6.6）；模型清單只在還不知道時探
    /// </summary>
    public static (bool AccountRead, bool RateLimits, bool Models) CodexProbePlan(bool pollUsage, bool needModels)
        => (true, pollUsage, needModels);

    /// <summary>
    /// Antigravity 查狀態時要跑哪些 agy 指令：要探模型清單、或這次不查額度（多電腦同步由別台負責查）時跑 agy models；
    /// 用量閘門放行時跑 agy -p /usage --output-format json（模型清單已知時只跑這一次，同時確認登入狀態、讀信箱與取額度）
    /// </summary>
    public static (bool Models, bool Usage) AntigravityProbePlan(bool pollUsage, bool needModels)
        => (Models: needModels || !pollUsage, Usage: pollUsage);

    /// <summary>
    /// 定期輪詢（Tick）時要不要一併重查登入狀態：未登入、未安裝（在外部登入或安裝 CLI 後跟得上），
    /// 以及上次檢查失敗（暫時性逾時或後端錯誤後自動恢復）
    /// </summary>
    public static bool ShouldRecheckStatusOnTick(AccountStatus status)
        => status is AccountStatus.NotLoggedIn or AccountStatus.NotInstalled or AccountStatus.Error;

    /// <summary>檢查失敗後第一次自動重試的間隔（1 分鐘），之後每次加倍，不超過帳戶原本的輪詢間隔</summary>
    public static readonly TimeSpan InitialErrorRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// 查詢結束後到下一次輪詢的間隔：正常狀態照帳戶原本的輪詢間隔（<paramref name="normalInterval"/>）；
    /// 檢查失敗（<see cref="AccountStatus.Error"/>）時依連續失敗次數退避（1 → 2 → 4 分鐘…，上限為 <paramref name="normalInterval"/>），
    /// 讓啟動瞬間的系統忙碌或短暫網路錯誤在 1 分鐘內自動恢復，長期故障也不會密集起子程序
    /// </summary>
    public static TimeSpan NextPollDelay(AccountStatus status, int consecutiveErrors, TimeSpan normalInterval)
    {
        if (status != AccountStatus.Error)
            return normalInterval;
        var shift = Math.Clamp(consecutiveErrors - 1, 0, 10);
        var backoff = TimeSpan.FromTicks(InitialErrorRetryDelay.Ticks * (1L << shift));
        return backoff < normalInterval ? backoff : normalInterval;
    }
}
