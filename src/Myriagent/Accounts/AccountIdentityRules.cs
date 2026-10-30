using Myriagent.Agents;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Accounts;

/// <summary>
/// 帳戶目錄（WP3.4）要用的身分規則：Claude 的登入到期、檔案裡的 orgId、orgId 的來源優先序、登入帶的 --email、「在這台登入」的檢查。
/// .claude.json 的路徑規則與 IsValidOrgId 在 AccountRules、identityKey 在 CatalogSyncRules（D3），這裡只放新規則
/// </summary>
internal static class AccountIdentityRules
{
    /// <summary>時間的上限（9999-12-31，毫秒；DateTimeOffset 的上限，同 UsageSyncRules.MaxUnixMs）：網頁換成日期時不會出錯</summary>
    public const long MaxUnixMs = 253_402_300_799_999;

    /// <summary>--email 的長度上限（RFC 5321 的路徑上限）</summary>
    public const int MaxLoginEmailLength = 254;

    /// <summary>
    /// .credentials.json 最上層的 organizationUuid 能不能當成 orgId（D4）：E-13 證明它和 auth status 的 orgId 相等之前一律 false。
    /// 身分鍵一旦發布就不能換成不同的值（伺服器沒有升級規則），所以不確定時寧可不用
    /// </summary>
    public static bool CredentialsOrgConfirmed => false;

    /// <summary>CLI 設定檔（.claude.json）oauthAccount.organizationUuid 能不能當成 orgId（D4）：同上</summary>
    public static bool GlobalConfigOrgConfirmed => false;

    /// <summary>
    /// 憑證檔 claudeAiOauth.refreshTokenExpiresAt（整數毫秒；D5，只是參考值）：大於 0 而且不超過 <see cref="MaxUnixMs"/> 才回傳，
    /// 缺欄位、不是整數、超出範圍、讀不懂一律 null。只讀這個中繼欄位，不讀任何權杖字串
    /// </summary>
    public static long? ReadClaudeLoginExpiresAt(string? credentialsJson)
    {
        if (ParseObject(credentialsJson)?["claudeAiOauth"] is not JObject oauth)
            return null;
        return oauth["refreshTokenExpiresAt"] is JValue { Type: JTokenType.Integer, Value: long ms } && ms is > 0 and <= MaxUnixMs
            ? ms
            : null;
    }

    /// <summary>
    /// 網頁帳戶卡的登入到期（AccountView.loginExpiresAt，網頁 loginrules.js 在剩 3 天內提醒重新登入）：只有已登入時才給——
    /// 登入失效時 CLI 清掉權杖、憑證檔的 refreshTokenExpiresAt 還在，照送的話會對失效的登入顯示「N 天後到期」（同 CatalogSyncRules 的 B42）。
    /// 目前只有 Claude 讀得到到期（其他種類的 AccountRuntimeState.LoginExpiresAt 一律 null）
    /// </summary>
    public static long? ShownLoginExpiresAt(AccountStatus status, long? loginExpiresAt)
        => status == AccountStatus.Ready && loginExpiresAt is > 0 and <= MaxUnixMs ? loginExpiresAt : null;

    /// <summary>憑證檔最上層的 organizationUuid（claudeAiOauth 裡同名的欄位不算），以 AccountRules.IsValidOrgId 過濾；沒有或不合格回 null</summary>
    public static string? ReadClaudeCredentialOrgId(string? credentialsJson)
        => ParseObject(credentialsJson)?["organizationUuid"] is JValue { Type: JTokenType.String, Value: string orgId } && AccountRules.IsValidOrgId(orgId)
            ? orgId
            : null;

    /// <summary>
    /// Claude 帳戶的 orgId 取哪個來源（各來源都先以 AccountRules.IsValidOrgId 過濾，不合格就往下一個找）：
    /// <list type="number">
    /// <item>這次 auth status 的 orgId</item>
    /// <item>身分快取的 orgId：快取的 email 和這次判定的 email 都知道、而且相同（不分大小寫）才用——外部換了帳號不能沿用舊的 org</item>
    /// <item>憑證檔最上層的 organizationUuid（<paramref name="useCredentials"/>，D4 的閘門）</item>
    /// <item>CLI 設定的 oauthAccount.organizationUuid（<paramref name="useGlobalConfig"/>，D4 的閘門）</item>
    /// </list>
    /// 都沒有回 null
    /// </summary>
    public static string? ResolveClaudeOrgId(string? fromAuthStatus, string? cachedOrgId, string? cachedEmail, string? currentEmail,
        string? fromCredentials, string? fromGlobalConfig, bool useCredentials, bool useGlobalConfig)
    {
        if (AccountRules.IsValidOrgId(fromAuthStatus))
            return fromAuthStatus;
        if (AccountRules.IsValidOrgId(cachedOrgId) && SameEmail(cachedEmail, currentEmail))
            return cachedOrgId;
        if (useCredentials && AccountRules.IsValidOrgId(fromCredentials))
            return fromCredentials;
        if (useGlobalConfig && AccountRules.IsValidOrgId(fromGlobalConfig))
            return fromGlobalConfig;
        return null;
    }

    /// <summary>
    /// 保守模式的 org 提示（SEC-1）：orgId 不明（<paramref name="resolvedOrgId"/> 是 null）、CLI 設定的 organizationUuid 合格
    /// （AccountRules.IsValidOrgId），而且 CLI 設定的 email 和帳戶的 email 都知道、相同（不分大小寫）時，回 CLI 設定的 organizationUuid；
    /// 否則 null。它還沒經 E-13 確認（D4），只能拿來判斷和目錄鍵的 org 不符，不能當成 orgId
    /// </summary>
    public static string? ClaudeOrgIdHint(string? resolvedOrgId, string? configOrgId, string? configEmail, string? accountEmail)
        => resolvedOrgId is null && AccountRules.IsValidOrgId(configOrgId) && SameEmail(configEmail, accountEmail) ? configOrgId : null;

    /// <summary>
    /// Claude 登入帶的 --email（D12）：只收單純的 ASCII 信箱（本地部分 A-Z a-z 0-9 . _ + -，網域 A-Z a-z 0-9 . -，正好一個 @）、
    /// 不超過 254 字、不以 '-' 開頭（免得被當成旗標）。npm 版的 claude 是 .cmd，cmd 會重新解析參數，所以不能放行任何 cmd 的特殊字元；
    /// 不合格回 null（不帶 --email）
    /// </summary>
    public static string? LoginEmailArgument(string? email)
    {
        if (string.IsNullOrEmpty(email) || email.Length > MaxLoginEmailLength || email[0] == '-')
            return null;
        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1 || email.IndexOf('@', at + 1) >= 0)
            return null;
        for (var i = 0; i < email.Length; i++)
        {
            if (i == at)
                continue;
            var c = email[i];
            var allowed = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'
                          || (i < at && c is '_' or '+');
            if (!allowed)
                return null;
        }
        return email;
    }

    /// <summary>
    /// 「在這台登入」（accountAdd {logicalAccountId}）能不能宣告這個邏輯帳戶，依序檢查：
    /// 格式（SyncIds.IsLogicalAccountId）→ 已配對 → 目錄裡有（<paramref name="catalogProvider"/> 不是 null）→ 種類（Claude、Codex、Grok 可以；
    /// Antigravity 不行；未知的種類丟例外）→ 這台還沒連結 → 這台還沒有別的本機帳戶宣告
    /// </summary>
    public static CatalogClaimCheck CheckCatalogClaim(string? logicalAccountId, bool paired, AgentProvider? catalogProvider, bool linkedHere, bool claimedHere)
    {
        if (!SyncIds.IsLogicalAccountId(logicalAccountId))
            return CatalogClaimCheck.InvalidId;
        if (!paired)
            return CatalogClaimCheck.NotPaired;
        if (catalogProvider is not { } provider)
            return CatalogClaimCheck.NotInCatalog;
        switch (provider)
        {
            case AgentProvider.Claude:
            case AgentProvider.Codex:
            case AgentProvider.Grok:
                break;
            // agy 一個 Windows 使用者只有一個登入（認證管理員）：從目錄「在這台登入」會換掉這台原本的登入，只顯示不提供
            // （設計 2026-10-22-grok-antigravity-sync 5.2）
            case AgentProvider.Antigravity:
                return CatalogClaimCheck.UnsupportedProvider;
            default:
                throw new ArgumentOutOfRangeException(nameof(catalogProvider), provider, "沒有同步分類的代理種類");
        }
        if (linkedHere)
            return CatalogClaimCheck.AlreadyLinkedHere;
        if (claimedHere)
            return CatalogClaimCheck.AlreadyClaimedHere;
        return CatalogClaimCheck.Ok;
    }

    /// <summary>「在這台登入」被拒絕時給使用者看的原因（toast；Ok 不是失敗，丟例外）</summary>
    public static string DescribeClaimFailure(CatalogClaimCheck check) => check switch
    {
        CatalogClaimCheck.InvalidId => "帳戶目錄的識別碼格式不對，無法在這台登入",
        CatalogClaimCheck.NotPaired => "這台還沒配對，不能在這台登入目錄裡的帳戶",
        CatalogClaimCheck.NotInCatalog => "帳戶目錄裡找不到這個帳戶（可能已被刪除），請稍後再試",
        CatalogClaimCheck.UnsupportedProvider => "Antigravity 一台電腦只有一個登入，不能從帳戶目錄在這台登入（請在那台的 agy 登入）",
        CatalogClaimCheck.AlreadyLinkedHere => "這台已經登入這個帳戶了",
        CatalogClaimCheck.AlreadyClaimedHere => "這台已經有一個本機帳戶要登入這個帳戶，請在那個帳戶上登入",
        CatalogClaimCheck.Ok => throw new ArgumentException("Ok 不是失敗", nameof(check)),
        _ => throw new ArgumentOutOfRangeException(nameof(check), check, null),
    };

    /// <summary>兩個 email 都知道而且相同（去掉前後空白、不分大小寫）</summary>
    private static bool SameEmail(string? a, string? b)
    {
        var x = a?.Trim();
        var y = b?.Trim();
        return !string.IsNullOrEmpty(x) && !string.IsNullOrEmpty(y) && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>JSON 物件；空白、讀不懂、寫到一半、不是物件、物件後面還有東西都回 null</summary>
    private static JObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JObject.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
