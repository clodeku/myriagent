namespace MyriagentHub.Data;

// hub.db 的資料表（3.2）。時間一律存 INTEGER Unix 毫秒（EF SQLite 對 DateTimeOffset 排序要在用戶端算）。
// 欄位名稱在 HubDbContext 統一轉成 snake_case。

/// <summary>已配對的電腦（配對核准時建立；權杖在裝置第一次換到時才產生，見 PairingService）</summary>
internal sealed class DeviceRecord
{
    /// <summary>d_＋26 碼 Crockford Base32（128 位元亂數）</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? MachineName { get; set; }

    public string? WindowsUser { get; set; }

    /// <summary>SHA-256(chd_…)；原文只在配對當下給桌面一次</summary>
    public byte[] TokenHash { get; set; } = [];

    /// <summary>輪換時舊權杖保留 24 小時</summary>
    public byte[]? PrevTokenHash { get; set; }

    public long? PrevTokenExpires { get; set; }

    /// <summary>目前權杖的發出時間：超過 30 天時 Prove 帶新的（舊的保留 24 小時）</summary>
    public long? TokenIssuedAt { get; set; }

    /// <summary>裝置金鑰 SPKI（ECDSA P-256）</summary>
    public byte[] DevicePubkey { get; set; } = [];

    /// <summary>tpm | software</summary>
    public string KeyStorage { get; set; } = string.Empty;

    /// <summary>（停用）擁有者以 passkey 簽署的名冊聲明：第 3 階段改成帳號密碼、伺服器授權之後不再使用；欄位留到下一版的遷移才移除（部署回滾時舊版還讀得到，見 HubDbContext）；一律空字串</summary>
    public string RegistryStatement { get; set; } = string.Empty;

    /// <summary>（停用）對應的 WebAuthn 斷言：一律空字串</summary>
    public string RegistryAssertion { get; set; } = string.Empty;

    public string? AppVersion { get; set; }

    public int? ProtocolVersion { get; set; }

    public long CreatedAt { get; set; }

    public long? LastSeenAt { get; set; }

    public string? LastIp { get; set; }

    public long? RevokedAt { get; set; }

    /// <summary>retired（退役，之前送出的內容仍可信）| compromised（疑似遭入侵）</summary>
    public string? RevokedReason { get; set; }

    /// <summary>
    /// 擁有者把已撤銷的電腦從清單移除的時間（設計 14.22）：列留著（公鑰不能再配對、稽核對得上），不列在名冊、電腦頁、矩陣與版本頁，
    /// 不算進名冊上限；它擁有的對話在伺服器上刪除。只有已撤銷的才能移除
    /// </summary>
    public long? RemovedAt { get; set; }
}

/// <summary>
/// 配對請求（仿 RFC 8628，設計 8.2.4）：deviceCode 只存 SHA-256、只能換一次權杖；10 分鐘到期。
/// 核准時配 deviceId，裝置下一次輪詢才產生權杖（伺服器從不保存權杖原文）。
/// </summary>
internal sealed class PairingRequestRecord
{
    /// <summary>ULID</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>SHA-256(deviceCode)</summary>
    public byte[] DeviceCodeHash { get; set; } = [];

    /// <summary>XXXX-XXXX（PairingRules.NormalizeUserCode 的格式）</summary>
    public string UserCode { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string? MachineName { get; set; }

    public string? WindowsUser { get; set; }

    /// <summary>裝置金鑰 SPKI（ECDSA P-256）</summary>
    public byte[] DevicePubkey { get; set; } = [];

    /// <summary>tpm | software</summary>
    public string KeyStorage { get; set; } = string.Empty;

    public string? RemoteIp { get; set; }

    public string? UserAgent { get; set; }

    public string? AppVersion { get; set; }

    public int? ProtocolVersion { get; set; }

    public long CreatedAt { get; set; }

    public long ExpiresAt { get; set; }

    /// <summary>目前的輪詢間隔（秒；slow_down 時加 5）</summary>
    public int IntervalSeconds { get; set; }

    public long? LastPollAt { get; set; }

    /// <summary>核准時配的 deviceId</summary>
    public string? DeviceId { get; set; }

    public long? ApprovedAt { get; set; }

    /// <summary>核准的瀏覽器工作階段（短 id）</summary>
    public string? ApprovedSession { get; set; }

    public long? DeniedAt { get; set; }

    /// <summary>裝置已換到權杖（deviceCode 只能換一次）</summary>
    public long? ConsumedAt { get; set; }
}

/// <summary>（停用）第 3 階段原本的擁有者金鑰紀錄（passkey 金鑰集的變更）：第 3 階段改成帳號密碼、伺服器授權之後不再使用；欄位留到下一版的遷移才移除（部署回滾時舊版還讀得到，見 HubDbContext）</summary>
internal sealed class OwnerKeyLogRecord
{
    /// <summary>1＝genesis，之後連續</summary>
    public long Rev { get; set; }

    /// <summary>genesis | add | remove | reset</summary>
    public string Action { get; set; } = string.Empty;

    public byte[] CredentialId { get; set; } = [];

    /// <summary>COSE_Key（genesis、add、reset）</summary>
    public byte[]? CosePublicKey { get; set; }

    public int? Alg { get; set; }

    public string? Name { get; set; }

    /// <summary>被簽署的 JSON 原文</summary>
    public string Statement { get; set; } = string.Empty;

    /// <summary>簽署者（add、remove）；genesis／reset 沒有簽署者，裝置要本機確認</summary>
    public byte[]? SignerCredentialId { get; set; }

    /// <summary>WebAuthn 斷言（JSON）</summary>
    public string? Assertion { get; set; }

    public long CreatedAt { get; set; }
}

/// <summary>伺服器端瀏覽器工作階段（ITicketStore）：cookie 只放隨機 key，這裡只存 key 的 SHA-256</summary>
internal sealed class BrowserSessionRecord
{
    public byte[] KeyHash { get; set; } = [];

    /// <summary>Data Protection 加密的 AuthenticationTicket</summary>
    public byte[] Ticket { get; set; } = [];

    /// <summary>（停用）第 2 階段登入時使用的 passkey：第 3 階段改成帳號密碼、伺服器授權之後不再使用；欄位留到下一版的遷移才移除（部署回滾時舊版還讀得到，見 HubDbContext）；一律 null</summary>
    public byte[]? PasskeyCredentialId { get; set; }

    public long CreatedAt { get; set; }

    public long LastSeenAt { get; set; }

    /// <summary>閒置期限（滑動，預設 12 小時）</summary>
    public long IdleExpiresAt { get; set; }

    /// <summary>絕對期限（預設 7 天）</summary>
    public long AbsoluteExpiresAt { get; set; }

    /// <summary>最近一次輸入密碼（登入或再驗證；伺服器端管理動作要 5 分鐘內）</summary>
    public long LastUvAt { get; set; }

    public string? IpFirst { get; set; }

    public string? IpLast { get; set; }

    public string? UserAgent { get; set; }

    public long? RevokedAt { get; set; }

    /// <summary>
    /// 協作人的工作階段：協作人的 Identity 使用者 id（2026-10-30，多人協作）。null＝擁有者（含這個欄位之前建立的、回滾到舊版之後建立的）。
    /// 擁有者改密碼、reset-owner、工作階段清單只管 null 的；刪除、停用、重設協作人時以它撤銷那位的工作階段
    /// </summary>
    public string? UserId { get; set; }
}

/// <summary>稽核紀錄：只增不改；每列 hash = SHA-256(prev_hash ‖ 正規化列內容)，串成雜湊鏈（8.14）</summary>
internal sealed class AuditRecord
{
    public long Seq { get; set; }

    public long At { get; set; }

    /// <summary>owner | device | system | anonymous | admin | deploy</summary>
    public string ActorType { get; set; } = string.Empty;

    public string? ActorId { get; set; }

    public string? Ip { get; set; }

    public string Action { get; set; } = string.Empty;

    public string? Target { get; set; }

    /// <summary>ok | denied | error</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>JSON；絕不含訊息原文、權杖、cookie、密碼、驗證碼</summary>
    public string? Detail { get; set; }

    public byte[] PrevHash { get; set; } = [];

    public byte[] Hash { get; set; } = [];
}

/// <summary>鍵值設定：acme_state、backup_state、restore_generation、audit_anchor…</summary>
internal sealed class KvRecord
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// 收進來的桌面版（規格 §7.2；U2）：release 是主鍵，撤回或清除過的同名版本永遠不能再收（U2 D14）。
/// 檔案在 app-releases\&lt;release&gt;\（zip、manifest 原文、簽章檔；每日備份不含）。只加表：服務回滾到舊版仍相容
/// </summary>
internal sealed class AppReleaseRecord
{
    /// <summary>「0.2.1523-ab12cd3」（ReleaseIdentity.TryParseRelease 的標準寫法）</summary>
    public string Release { get; set; } = string.Empty;

    /// <summary>三段版本號「0.2.1523」</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>提交前 7 碼小寫</summary>
    public string Build { get; set; } = string.Empty;

    /// <summary>完整的提交雜湊（40 碼小寫）</summary>
    public string Commit { get; set; } = string.Empty;

    /// <summary>manifest 原文位元組的 SHA-256（小寫十六進位）：載入最新版快取時比對檔案</summary>
    public string ManifestSha256 { get; set; } = string.Empty;

    public string ZipSha256 { get; set; } = string.Empty;

    public long ZipBytes { get; set; }

    public int ProtocolVersion { get; set; }

    /// <summary>「Microsoft.WindowsDesktop.App 10.0.0」（AppReleaseRules.MinRuntimeText）</summary>
    public string MinRuntime { get; set; } = string.Empty;

    /// <summary>簽章金鑰的 kid（16 個小寫十六進位字）</summary>
    public string Kid { get; set; } = string.Empty;

    /// <summary>available | withdrawn | pruned（AppReleaseStates）</summary>
    public string State { get; set; } = string.Empty;

    public long IngestedAt { get; set; }

    public long? WithdrawnAt { get; set; }
}

/// <summary>
/// 協作人（2026-10-30，docs/plans/2026-10-30-collaborators.md）：帳號與密碼在 Identity（AspNetUsers，<see cref="UserId"/>），這裡記顯示名稱、狀態與個人的介面設定。
/// 只加表：服務回滾到舊版時舊版不認得協作人（登入只找 admin、票證只認 admin），協作人登不進去
/// </summary>
internal sealed class CollaboratorRecord
{
    public string UserId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }

    /// <summary>停用的時間；null＝啟用。停用時撤銷他的工作階段、登入一律失敗，分享照留（啟用後照舊）</summary>
    public long? DisabledAt { get; set; }

    public long? LastLoginAt { get; set; }

    /// <summary>個人的介面設定（JSON 物件，只有 <c>CollaboratorRules.PrefKeys</c> 的鍵）；null＝都是預設</summary>
    public string? PrefsJson { get; set; }
}

/// <summary>對話分享給協作人：線上的對話 id（32 個小寫十六進位）＋協作人的 Identity 使用者 id。對話刪除後列照留（看不到），刪除協作人時一起刪</summary>
internal sealed class CollaboratorShareRecord
{
    public string ConversationId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public long GrantedAt { get; set; }
}
