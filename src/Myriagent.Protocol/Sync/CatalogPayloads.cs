using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

// 第 3 階段事件的 payload（設計 4.4、6.1～6.6；docs/sync-protocol.md 第 5 節）。時間一律 Unix 毫秒，只供顯示、不參與排序。
// 任何一個欄位都不放權杖、登入碼或憑證檔內容（伺服器資料庫沒有能放權杖的欄位，設計 6.1）。

/// <summary>對話數（同 web-protocol 的 AccountView.sessions）</summary>
/// <param name="Background">
/// 代理這一輪已回覆完畢、背景工作還在跑（等背景工作回報）的對話數（2026-10-24 起；舊版沒有這個欄位，讀成 0，舊版讀到也忽略）
/// </param>
/// <param name="Programs">
/// 這一輪結束了、但代理開的程式脫離 CLI 還在跑（背景程式）的對話數（2026-10-24 起）：0 的時候是 null、線上省略（沒有背景程式的連結和以前逐位元組相同，
/// 摘要不變、不必重送）；舊版忽略這個欄位
/// </param>
/// <param name="Subagents">
/// 執行中的子代理數（個數，不是對話數；2026-10-28 起）：同 <paramref name="Programs"/>，0 的時候是 null、線上省略；舊版忽略這個欄位
/// </param>
internal sealed record LinkSessions(int Open, int Running, int Waiting, int Background = 0, int? Programs = null, int? Subagents = null);

/// <summary>
/// <c>link.upsert</c>（裝置寫）：這台一個本機帳戶的狀態摘要。伺服器依 identityKey 自動連結到邏輯帳戶（對不上就以這台的名稱與顏色新建一個），
/// 結果以 <c>account.upsert</c> 發布；連結後身分變了（這台改登別的帳號）伺服器標 identityOk=false。
/// </summary>
/// <param name="ConfigKind"><c>systemDefault</c>（~/.claude、~/.codex）或 <c>isolated</c>（app 的隔離資料夾）</param>
/// <param name="IdentityKey">Claude <c>claude:{orgId}:{email 小寫}</c>、Codex <c>codex:{email 小寫}</c>；不知道身分時 null（不自動連結）</param>
/// <param name="LogicalAccountId">這台目前認定的對應（伺服器發布的 account.upsert 告訴它的）；還沒連結時 null</param>
/// <param name="Status">同 web-protocol AccountView.status</param>
internal sealed record LinkUpsertPayload(
    string LocalAccountId,
    string Provider,
    string ConfigKind,
    string? IdentityKey,
    string? LogicalAccountId,
    string Status,
    string? StatusText,
    string? Email,
    string? OrgId,
    string? Plan,
    long? AccessExpiresAt,
    long? LoginExpiresAt,
    bool LoginBusy,
    LinkSessions Sessions,
    string Label,
    string Color,
    int SortOrder,
    long CheckedAt);

/// <summary><c>link.removed</c>（裝置寫）：這台移除了本機帳戶（「從這台移除」）</summary>
internal sealed record LinkRemovedPayload(string LocalAccountId);

/// <summary>
/// <c>account.update</c>（裝置寫）：修改邏輯帳戶的名稱、顏色、順序（只填要改的）。樂觀並行：<see cref="BaseVersion"/> 不是目前版本就被拒
/// （BatchAck 的 rejected＝<c>conflict</c>），這台之後拉到的 account.upsert 會蓋回伺服器的值。
/// </summary>
internal sealed record AccountUpdatePayload(string LogicalAccountId, long BaseVersion, string? Label, string? Color, int? SortOrder);

/// <summary><c>prefs.update</c>（裝置寫）：漫遊的外觀偏好，只接受 <see cref="PrefsRules"/> 的白名單；樂觀並行同上</summary>
internal sealed record PrefsUpdatePayload(string Key, JToken Value, long BaseVersion);

/// <summary>
/// <c>device.update</c>（裝置寫）：改某台裝置的顯示名稱（網頁協定的 <c>deviceRename</c>；擁有者在瀏覽器改時伺服器直接套用）。
/// 名稱規則同 <see cref="PairingRules.CleanDeviceName"/>；結果以 <c>device.upsert</c> 發布。只改顯示名稱，
/// 裝置名冊裡配對時的名稱（與裝置指紋一起顯示）不變。
/// </summary>
internal sealed record DeviceUpdatePayload(string DeviceId, string Name);

/// <summary>一種 CLI 在一台電腦上的狀態（web-protocol CliView 的子集；儀表板的「各電腦 CLI 版本」）</summary>
internal sealed record DeviceCliView(
    bool Installed,
    string? Version,
    string? InstallKind,
    string? Latest,
    bool UpdateAvailable,
    int RunningOnOldVersion,
    int RestartPending,
    string? InstallBroken,
    long? CheckedAt);

/// <summary><c>cli.state</c>（裝置寫）：這台各種 CLI 的狀態，鍵是 <c>Claude</c>／<c>Codex</c></summary>
internal sealed record CliStatePayload(IReadOnlyDictionary<string, DeviceCliView> Providers);

/// <summary>app 的更新階段（<c>app.state</c> 的 <c>phase</c>，app 自我更新規格 7.8）：線上的值大小寫固定，不認得的值伺服器回 invalid</summary>
internal static class AppStatePhases
{
    /// <summary>沒有要做的事（已是最新，或還沒檢查）</summary>
    public const string Idle = "idle";

    /// <summary>下載中</summary>
    public const string Downloading = "downloading";

    /// <summary>已下載並驗證，等著套用</summary>
    public const string Staged = "staged";

    /// <summary>等使用者閒置才套用</summary>
    public const string Waiting = "waiting";

    /// <summary>套用前排空對話中</summary>
    public const string Draining = "draining";

    /// <summary>換版中</summary>
    public const string Applying = "applying";

    /// <summary>新版啟動失敗，已退回前一版</summary>
    public const string RolledBack = "rolledBack";

    /// <summary>不能更新：缺 runtime、空間不足或未啟用</summary>
    public const string Blocked = "blocked";

    /// <summary>是不是上面八個值之一（序數比對，大小寫要相同）</summary>
    public static bool IsKnown(string? phase) => phase is Idle or Downloading or Staged or Waiting or Draining or Applying or RolledBack or Blocked;
}

/// <summary>
/// <c>app.state</c>（裝置寫，合併鍵 <c>app</c>）：這台 app 的更新狀態。伺服器檢查格式（MyriagentHub 的 <c>CatalogRules.ValidateApp</c>）後存進 sync.db 的
/// <c>device_app</c>，併進之後每一筆 <c>device.upsert</c> 的 <see cref="DeviceUpsertPayload.App"/>，給「版本」頁看各電腦的更新進度
/// </summary>
/// <param name="Version">目前執行的版本：三段數字的標準寫法（<c>ReleaseVersion</c>，例如 <c>0.2.300</c>）</param>
/// <param name="Release">目前執行的 release 名稱（<c>0.2.300-ab12cd3</c>）；開發版沒有 release 名稱，送 null</param>
/// <param name="Phase"><see cref="AppStatePhases"/> 的值</param>
/// <param name="Staged">已下載並驗證、等著套用的 release 名稱；沒有時 null</param>
/// <param name="LastError">最近一次失敗的原因（一行、最多 500 字）；沒有時 null</param>
/// <param name="CheckedAt">最後一次檢查更新的時間（Unix 毫秒，只供顯示）</param>
internal sealed record AppStatePayload(string Version, string? Release, string Phase, string? Staged, string? LastError, long CheckedAt);

/// <summary>「邏輯帳戶 × 裝置」矩陣的一格（伺服器組成；設計 3.2 account_links、6.8）</summary>
internal sealed record AccountLinkView(
    string DeviceId,
    string LocalAccountId,
    string ConfigKind,
    string Status,
    string? StatusText,
    string? Email,
    string? OrgId,
    string? Plan,
    long? AccessExpiresAt,
    long? LoginExpiresAt,
    bool LoginBusy,
    bool IdentityOk,
    LinkSessions? Sessions,
    long? CheckedAt,
    bool UsagePoller);

/// <summary><c>account.upsert</c>（伺服器寫）：邏輯帳戶整筆＋各電腦矩陣；裝置只套用名稱、顏色、順序與自己的對應</summary>
internal sealed record AccountUpsertPayload(
    string LogicalAccountId,
    string Provider,
    string IdentityKey,
    string Label,
    string Color,
    int SortOrder,
    string? ExpectedEmail,
    string? ExpectedOrgId,
    string? PlanHint,
    long Version,
    IReadOnlyList<AccountLinkView> Links);

/// <summary><c>account.deleted</c>（伺服器寫）：從目錄刪除邏輯帳戶；各台解除連結、本機帳戶保留（變成「未同步」）</summary>
internal sealed record AccountDeletedPayload(string LogicalAccountId, long Version);

/// <summary>裝置自報的遠端操作政策（只供網頁顯示；強制在裝置端，設計 8.10）</summary>
/// <param name="AllowRemoteExec">
/// 允許其他電腦對話裡的代理在這台執行指令（docs/sync-protocol.md 14.11；2026-10-25）：只在開啟時是 true，其他是 null（線上省略，舊版與黃金檔不變）
/// </param>
internal sealed record DevicePolicy(string RemoteControlLevel, bool AllowRemoteFullAccess, bool? AllowRemoteExec = null);

/// <summary>
/// <c>device.upsert</c>（伺服器寫）：裝置的名稱、上線狀態、版本、指紋、政策、CLI 狀態與 app 的更新狀態。
/// <c>features</c>（第 4 階段，sync-protocol 14.1）：那台目前在線的連線協商出的功能（<see cref="DeviceFeatureRules.Negotiate"/>）；
/// 沒有連線或是空的時候為 null（不寫進 JSON，舊版拉取端與黃金檔不變）
/// </summary>
/// <param name="App">
/// 這台最後一次回報的 <c>app.state</c>（伺服器整理過，sync-protocol 6.6）；沒回報過時 null，線上省略。app 自我更新 U2 加的選用欄位，只能加在最後
/// </param>
internal sealed record DeviceUpsertPayload(
    string DeviceId,
    string Name,
    string? MachineName,
    string? AppVersion,
    string KeyStorage,
    string Fingerprint,
    long CreatedAt,
    long? LastSeenAt,
    bool Online,
    DevicePolicy? Policy,
    IReadOnlyDictionary<string, DeviceCliView>? Cli,
    IReadOnlyList<string>? Features = null,
    AppStatePayload? App = null);

/// <summary><c>device.revoked</c>（伺服器寫）：原因 <c>retired</c>（退役，簽過的內容仍可信）或 <c>compromised</c>（疑似遭入侵）</summary>
internal sealed record DeviceRevokedPayload(string DeviceId, long RevokedAt, string Reason);

/// <summary><c>prefs.set</c>（伺服器寫）</summary>
internal sealed record PrefsSetPayload(string Key, JToken Value, long Version);

/// <summary>用量的一個時間窗（桌面 UsageWindow 的線上格式）</summary>
internal sealed record SharedUsageWindow(string Key, string Label, double Percent, long? ResetsAt, bool Locked);

/// <summary>
/// 共享的用量快照（桌面 UsageSnapshot 的線上格式）；<see cref="Source"/> 是 <c>api</c>（官方用量端點）或 <c>session</c>（對話中的 rate limit 事件）。
/// <see cref="ResetCredits"/>、<see cref="ResetCreditExpiresAt"/>（毫秒）是 Codex 的免費重置次數與最早到期時間（2026-10-30 加的，舊版不送、送了舊版也忽略）
/// </summary>
internal sealed record SharedUsage(
    long FetchedAt,
    string Source,
    IReadOnlyList<SharedUsageWindow> Windows,
    string? Credits,
    string? Note,
    string? Error,
    bool Stale,
    int? ResetCredits = null,
    long? ResetCreditExpiresAt = null);

/// <summary><c>usage.snapshot</c>（伺服器寫）：某邏輯帳戶最新的用量，來自負責查詢的裝置或任何裝置的對話中 rate limit 事件</summary>
internal sealed record UsageSnapshotPayload(string LogicalAccountId, SharedUsage Usage, string SourceDeviceId, long? Term);

/// <summary>
/// 漫遊的外觀偏好白名單（設計 8.9、SEC-24）：只有這四個鍵；其他設定（Instructions、權限預設、Sync 區段、CLI 更新政策）只能在每台電腦本機改。
/// 伺服器收 prefs.update 時檢查一次，裝置套用 prefs.set 時再檢查一次。
/// </summary>
internal static class PrefsRules
{
    public const int MinFontSize = 12;

    public const int MaxFontSize = 18;

    public static readonly IReadOnlyList<string> Keys = ["theme", "fontSize", "enterToSend", "notifications"];

    public static bool IsValid(string? key, JToken? value) => key switch
    {
        "theme" => value is { Type: JTokenType.String } && (string?)value is "dark" or "light" or "system",
        // 超過 long 的整數 Newtonsoft 讀成 BigInteger（Type 仍是 Integer），(long) 轉型會丟 OverflowException
        "fontSize" => value is JValue { Type: JTokenType.Integer, Value: long size } && size is >= MinFontSize and <= MaxFontSize,
        "enterToSend" or "notifications" => value is { Type: JTokenType.Boolean },
        _ => false,
    };
}
