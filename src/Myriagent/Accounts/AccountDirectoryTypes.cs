using Myriagent.Agents;
using Myriagent.Remote;

namespace Myriagent.Accounts;

/// <summary>
/// 「在這台登入」要建立的本機帳戶（WP3.4，web-protocol 的 accountAdd {logicalAccountId}）：名稱、顏色、預期信箱都取自帳戶目錄，
/// 不是網頁傳來的值
/// </summary>
/// <param name="LogicalAccountId">目錄裡的邏輯帳戶（la_…）</param>
/// <param name="Provider">目錄的種類（只會是 Claude 或 Codex）</param>
/// <param name="Label">目錄的名稱（建立時再以 AccountRules.CleanCatalogLabel 清理）</param>
/// <param name="Color">目錄的顏色（建立時再以 AccountRules.NormalizeCatalogColor 處理）</param>
/// <param name="ExpectedEmail">目錄身分鍵裡的信箱（Claude 登入時當 --email；沒有為 null）</param>
internal sealed record CatalogAccountTemplate(string LogicalAccountId, AgentProvider Provider, string Label, string Color, string? ExpectedEmail);

/// <summary>本機帳戶宣告的邏輯帳戶（存進 AgentAccount.LogicalAccountId）與目錄的預期信箱</summary>
internal sealed record AccountClaim(string LogicalAccountId, string? ExpectedEmail);

/// <summary>
/// 多電腦同步要的身分資訊（AccountManager.GetSyncIdentity；UI 執行緒上取得）
/// </summary>
/// <param name="OrgId">Claude 的 orgId（auth status、身分快取，或 E-13 確認後的檔案來源）；沒有為 null</param>
/// <param name="AccessExpiresAt">Claude 憑證檔 claudeAiOauth.expiresAt（毫秒）</param>
/// <param name="LoginExpiresAt">Claude 憑證檔 claudeAiOauth.refreshTokenExpiresAt（毫秒，只是參考值，D5）；Codex 一律 null</param>
/// <param name="ClaimedLogicalAccountId">「在這台登入」宣告的邏輯帳戶（AgentAccount.LogicalAccountId）</param>
/// <param name="OrgIdHint">OrgId 不明時，保守模式從 CLI 設定讀到、還沒確認的 organizationUuid（AccountRuntimeState.OrgIdHint；只用來判斷和目錄鍵的 org 不符，SEC-1）</param>
internal sealed record AccountSyncIdentity(string? OrgId, long? AccessExpiresAt, long? LoginExpiresAt, string? ClaimedLogicalAccountId, string? OrgIdHint = null);

/// <summary>「在這台登入」的檢查結果（AccountIdentityRules.CheckCatalogClaim）</summary>
internal enum CatalogClaimCheck
{
    Ok,

    /// <summary>不是合格的邏輯帳戶 id（la_ 加 26 字 Crockford Base32）</summary>
    InvalidId,

    /// <summary>這台還沒配對（沒有帳戶目錄）</summary>
    NotPaired,

    /// <summary>帳戶目錄裡沒有這個邏輯帳戶（已被刪除，或網頁的資料過期）</summary>
    NotInCatalog,

    /// <summary>目錄的種類不能從目錄在這台登入（Antigravity：一台只有一個 agy 登入，會換掉這台原本的登入）</summary>
    UnsupportedProvider,

    /// <summary>這台已經有本機帳戶連結到這個邏輯帳戶</summary>
    AlreadyLinkedHere,

    /// <summary>這台已經有本機帳戶宣告了這個邏輯帳戶（伺服器以 (邏輯帳戶, 裝置) 為主鍵，後到的會覆蓋前一個）</summary>
    AlreadyClaimedHere,
}

/// <summary>登入方式</summary>
internal enum LoginMethod
{
    /// <summary>各種類原本的登入（Claude 主控台、Codex 開瀏覽器）</summary>
    Default,

    /// <summary>Codex 的裝置碼登入（驗證碼顯示在 app 裡，可以在手機或別台電腦核准）</summary>
    DeviceCode,
}

/// <summary>登入的選項</summary>
/// <param name="Method">登入方式</param>
/// <param name="ClaudeEmail">Claude 登入時帶的 --email（目錄的預期信箱；以 AccountIdentityRules.LoginEmailArgument 過濾，不合格就不帶）</param>
/// <param name="Remote">遠端登入（WP4.8，網頁對這台發起）時目錄的預期身分：登入完成後拿它比對、不符就在同一個資料夾登出（D12～D14）；本機登入是 null</param>
internal sealed record LoginRequest(LoginMethod Method = LoginMethod.Default, string? ClaudeEmail = null, RemoteLoginExpectation? Remote = null);

/// <summary>遠端登入（WP4.8 D21：網頁對這台發起的 accountLogin／accountLoginDevice）要登入的本機帳戶與目錄的預期身分</summary>
/// <param name="LogicalAccountId">目錄的邏輯帳戶（la_…）；本機帳戶沒有對應任何邏輯帳戶時是 null</param>
/// <param name="Provider">要登入的種類（給邏輯帳戶時是目錄的種類，給本機帳戶時是本機帳戶的種類）</param>
/// <param name="Label">目錄的名稱（目錄沒有這一筆時是本機帳戶的名稱）</param>
/// <param name="LocalAccountId">這台對應的本機帳戶；null＝要建立（<paramref name="Create"/> 不是 null）</param>
/// <param name="Create">要建立的本機帳戶（名稱、顏色、預期信箱取自目錄，同「在這台登入」）；已有本機帳戶時是 null</param>
/// <param name="Expected">目錄的預期身分（登入完成後比對，D12）；目錄沒有這一筆時是 <see cref="RemoteLoginExpectation.None"/></param>
internal sealed record RemoteLoginTarget(string? LogicalAccountId, AgentProvider Provider, string Label, string? LocalAccountId,
    CatalogAccountTemplate? Create, RemoteLoginExpectation Expected);

/// <summary>
/// 找遠端登入目標的結果（ISyncBridge.FindRemoteLoginTarget）：找到時 Target 不是 null、Code 與 Message 是 null；
/// 找不到時 Target 是 null，Code 是 RemoteResultCodes 的值、Message 是給人看的說明（直接成為瀏覽器的 toast）
/// </summary>
internal sealed record RemoteLoginLookup(RemoteLoginTarget? Target, string? Code, string? Message)
{
    /// <summary>這台沒有配對（沒有帳戶目錄）：測試替身與沒有同步時的預設</summary>
    public static RemoteLoginLookup NotPaired { get; } =
        new(null, RemoteResultCodes.Invalid, AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.NotPaired));

    public static RemoteLoginLookup Found(RemoteLoginTarget target) => new(target ?? throw new ArgumentNullException(nameof(target)), null, null);

    /// <param name="code">RemoteResultCodes 的值</param>
    public static RemoteLoginLookup Rejected(string code, string message) => new(null, code, message);
}
