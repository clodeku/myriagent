using Myriagent.Agents;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Myriagent.Accounts;

/// <summary>帳戶的登入狀態</summary>
[JsonConverter(typeof(StringEnumConverter))]
internal enum AccountStatus
{
    Checking,
    Ready,
    NotLoggedIn,

    /// <summary>以 API Key 或第三方雲端登入：可以用，但走 API 計費而不是訂閱</summary>
    ApiKey,
    NotInstalled,
    Error,
}

/// <summary>
/// 一個訂閱帳戶。ConfigDirectory＝null 表示使用這台電腦的預設設定資料夾（~/.claude、~/.codex，或繼承的 CLAUDE_CONFIG_DIR／CODEX_HOME），
/// 否則是 app 建立的隔離資料夾（Claude 以 CLAUDE_CONFIG_DIR、Codex 以 CODEX_HOME 指向它），登入憑證與 CLI 對話紀錄都在裡面。
/// </summary>
internal sealed class AgentAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public AgentProvider Provider { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>代表色（#RRGGBB）</summary>
    public string Color { get; set; } = "#D97757";

    public string? ConfigDirectory { get; set; }

    /// <summary>精簡模式：不載入外掛、hooks、MCP、CLAUDE.md、使用者權限設定</summary>
    public bool SafeMode { get; set; }

    /// <summary>
    /// 隔離帳戶共用這台系統預設設定資料夾（~/.claude、~/.codex）的全域指示、自動記憶、MCP 與 skills／agents／commands，只有登入各自分開
    /// （SharedCustomizations）。預設開；舊版 accounts.json 沒有這個欄位時讀成預設值 true，舊版 app 讀到會忽略（只做加法）。系統預設帳戶本身就是來源，不看這個值
    /// </summary>
    public bool ShareCustomizations { get; set; } = true;

    public int Order { get; set; }

    /// <summary>上次查到的 email 與方案（狀態還在檢查時先顯示）</summary>
    public string? Email { get; set; }

    public string? Plan { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Claude：最近一次 claude auth status 成功時的結果與當時憑證檔的樣子（設計 6.5）。啟動時憑證檔的大小、修改時間與 expiresAt 都沒變
    /// 就直接以它判定已登入、不跑 auth status（auth status 遇到快到期的權杖會開始換發卻不等結果，可能把 refresh token 用掉）。null＝不知道
    /// </summary>
    public AccountIdentityCache? LastIdentity { get; set; }

    /// <summary>
    /// 「在這台登入」宣告的邏輯帳戶（多電腦同步的帳戶目錄，la_…；WP3.4）：還沒登入也照樣送 link.upsert，伺服器把這台連到那個邏輯帳戶。
    /// 一般新增的帳戶是 null；舊版 accounts.json 沒有這個欄位時讀成 null，舊版 app 讀到會忽略（只做加法）
    /// </summary>
    public string? LogicalAccountId { get; set; }

    [JsonIgnore]
    public bool IsSystemDefault => string.IsNullOrWhiteSpace(ConfigDirectory);
}

/// <summary>claude auth status 成功時的身分與憑證檔的樣子（AgentAccount.LastIdentity；存進 accounts.json，不含權杖）</summary>
internal sealed class AccountIdentityCache
{
    /// <summary>當時的狀態（Ready 或 ApiKey）</summary>
    public AccountStatus Status { get; set; }

    public string? Email { get; set; }

    public string? Plan { get; set; }

    /// <summary>憑證檔的大小（位元組）</summary>
    public long CredentialSize { get; set; }

    /// <summary>憑證檔的最後寫入時間（UTC 刻度：存成整數，讀回來比較不受時區與序列化精度影響）</summary>
    public long CredentialWriteTicks { get; set; }

    /// <summary>憑證檔 claudeAiOauth.expiresAt（毫秒）；沒有為 null</summary>
    public long? ExpiresAt { get; set; }

    public DateTime CheckedAt { get; set; }

    /// <summary>
    /// claude auth status 的 orgId（多電腦同步的身分鍵 claude:{org}:{email} 要用；設計 6.2、WP3.3 Q1）。
    /// 舊版 accounts.json 沒有這個欄位時讀成 null，舊版 app 讀到會忽略（只做加法）
    /// </summary>
    public string? OrgId { get; set; }
}

/// <summary>帳戶的執行期狀態（不存檔）</summary>
internal sealed class AccountRuntimeState
{
    public AccountStatus Status { get; set; } = AccountStatus.Checking;

    public string StatusText { get; set; } = "檢查中…";

    public bool LoginBusy { get; set; }

    public DateTime? CheckedAt { get; set; }

    /// <summary>Claude：這次執行查到的 orgId（auth status 或身分快取）；沒有為 null</summary>
    public string? OrgId { get; set; }

    /// <summary>
    /// Claude：保守模式下 <see cref="OrgId"/> 不明時，CLI 設定的 oauthAccount.organizationUuid（還沒確認，D4；AccountIdentityRules.ClaudeOrgIdHint）。
    /// 只拿來判斷「在這台登入」的帳戶和目錄鍵的 org 不符（CatalogSyncRules.ClaudeIdentityKey 的 D11 分支），絕不寫進任何身分鍵或同步欄位。
    /// 其他判定（快取、權杖待更新、登入失效、auth status 有結果）清成 null；暫時讀不到檔案、auth status 失敗時和 OrgId 一樣保留上次的值
    /// </summary>
    public string? OrgIdHint { get; set; }

    /// <summary>Claude：上次讀到的憑證檔 claudeAiOauth.expiresAt（毫秒；只讀這個中繼欄位，不讀權杖）</summary>
    public long? AccessExpiresAt { get; set; }

    /// <summary>Claude：上次讀到的憑證檔 claudeAiOauth.refreshTokenExpiresAt（毫秒；登入的到期，只是參考值，D5）；Codex 一律 null</summary>
    public long? LoginExpiresAt { get; set; }

    /// <summary>
    /// Codex：進行中的裝置碼登入的驗證碼（WP3.4；只在記憶體、不存檔，登入結束就清掉）。只放進 accountLogin 訊息：這台的本機視窗，
    /// 以及網頁發起的遠端登入（WP4.8）經 CommandOutput 只給發起的瀏覽器連線；不進記錄檔、AccountView 與任何同步 payload（R20）
    /// </summary>
    public CodexDeviceCode? DeviceCode { get; set; }

    /// <summary>
    /// 遠端登入身分不符的說明（WP4.8 D15；只在記憶體、不存檔，不含信箱）：SetStatus 沒指定文字時取代預設說明，於是經 link.upsert 到網頁矩陣的那一格。
    /// 登入開始或登出成功時清掉；「已在這台自動登出」的那一句（<see cref="RemoteLoginNoteLoggedOut"/>）之後查到已登入（例如在 app 外重新登入）時也清掉
    /// </summary>
    public string? RemoteLoginNote { get; set; }

    /// <summary><see cref="RemoteLoginNote"/> 是「已在這台自動登出」（自動登出成功）的那一句；和它一起設、一起清</summary>
    public bool RemoteLoginNoteLoggedOut { get; set; }
}
