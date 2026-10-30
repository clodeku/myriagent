using Myriagent.Security;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

// 裝置 hub 的訊息（設計 4.2、4.3、6.6；docs/sync-protocol.md 第 3、4 節）。參數一律是一個 JSON 物件、camelCase（SyncJson）。
// 前半是第 3 階段的範圍；第 4 階段（第 14 節）在 Hello／HelloResponse 的最後加 features、檔尾加新訊息。第 5 階段**不改** Hello／Prove
// （計畫 docs/plans/2026-10-23-phase5-handoff.md D2：原本預留的 leases、leaseVerdicts、pendingCommands 不做，擁有者換手由伺服器寫的持久事件
// conv.lease 告知），新訊息在 HandoffMessages.cs（第 16 節）。新欄位一律加在記錄最後、有預設值：舊版不認得的欄位一律忽略，新版讀不到的欄位是 null。
// Hello、Prove、RefreshAccessToken 的請求與回應只能加選用欄位（docs/sync-protocol.md 1.4 的不變式；app 自我更新 U2 的 latestApp、updateOnly
// 與 ServerNotice 的 release 就是這樣加的），HandshakeShapeInvariantTests 凍結這些記錄的主建構子。

/// <summary>伺服器資料庫的身分：建庫時的亂數 instanceId＋每次還原 +1 的 restoreGeneration（設計 10.2）</summary>
internal sealed record ServerInstance(string InstanceId, long RestoreGeneration);

/// <summary>裝置 outbox 的位置（Hello 帶上，伺服器決定從哪裡重送、還原後要裝置重送多少）</summary>
/// <param name="NextDeviceSeq">下一筆要配的 deviceSeq</param>
/// <param name="OldestUnackedDeviceSeq">還沒收到確認的最小 deviceSeq；沒有為 null</param>
/// <param name="OldestRetainedDeviceSeq">outbox 裡還留著的最小 deviceSeq（含 24 小時內已確認的）；沒有為 null</param>
internal sealed record OutboxPosition(long NextDeviceSeq, long? OldestUnackedDeviceSeq, long? OldestRetainedDeviceSeq);

/// <summary>
/// 連上後第一個呼叫（Invoke）。<c>observedSpkiSha256</c> 是裝置在 TLS 交握看到的伺服器葉憑證 SPKI 雜湊（<see cref="HandshakeRules.SpkiSha256"/>）；
/// <c>seenServer</c> 是上次連上的伺服器身分（第一次為 null）；<c>cursors</c> 以類別為鍵（<c>catalog</c>、<c>usage</c>；第 4 階段起 <c>meta</c>）。
/// <c>features</c>（第 4 階段，14.1）：裝置支援的功能（<see cref="DeviceFeatureRules"/>）；null＝沒宣告（第 3 階段的行為，JSON 與第 3 階段相同）
/// </summary>
internal sealed record HelloRequest(
    int ProtocolVersion,
    string DeviceId,
    string BootId,
    string AppVersion,
    string? MachineName,
    string? WindowsUser,
    string NonceD,
    string ObservedSpkiSha256,
    ServerInstance? SeenServer,
    OutboxPosition Outbox,
    IReadOnlyDictionary<string, long> Cursors,
    DevicePolicy Policy,
    IReadOnlyList<string>? Features = null);

/// <summary>裝置名冊目前的版本（裝置的比較舊就呼叫 GetTrust）</summary>
internal sealed record TrustRevs(long Registry);

/// <summary>
/// 伺服器身分金鑰的輪換聲明（<c>admin rotate-hub-identity</c>）：舊金鑰簽 <see cref="HandshakeRules.RotationSignedData"/>。
/// 裝置釘的是 <see cref="OldSpki"/> 而且簽章驗得過，就自動改釘 <see cref="NewSpki"/>；舊金鑰遺失（伺服器重建）時沒有這份聲明，
/// 裝置要在本機確認新指紋才恢復同步。
/// </summary>
internal sealed record HubIdentityRotation(string OldSpki, string NewSpki, string Sig);

/// <summary>
/// Hello 的回應：<c>hubSig</c> 是伺服器身分金鑰的簽章（<see cref="HandshakeRules.HubSignedData"/>），<c>hubIdentitySpki</c> 是簽它的公鑰。
/// 裝置只信自己釘的公鑰：相同就直接驗；不同時要有驗得過的 <c>rotation</c>（一律帶最近一次的）才改釘，
/// 否則中斷並請使用者在本機比對新指紋（伺服器重建、舊金鑰遺失的情況）。
/// <c>features</c>（第 4 階段，14.1）：伺服器支援的功能；雙方實際啟用的是交集（<see cref="DeviceFeatureRules.Negotiate"/>），null＝沒宣告。
/// <c>latestApp</c>（app 自我更新 U2，docs/sync-protocol.md 第 3、15 節）：伺服器目前最新的可用桌面版，沒有就是 null（線上省略）；U3 之前的桌面忽略
/// </summary>
internal sealed record HelloResponse(
    long ServerTime,
    string NonceS,
    ServerInstance Server,
    string HubSig,
    string HubIdentitySpki,
    TrustRevs TrustRevs,
    SyncLimits Limits,
    string MinAppVersion,
    HubIdentityRotation? Rotation = null,
    IReadOnlyList<string>? Features = null,
    AppReleaseInfo? LatestApp = null);

/// <summary>裝置金鑰的持有證明（<see cref="HandshakeRules.DeviceSignedData"/>，P1363 base64url）</summary>
internal sealed record ProveRequest(string DeviceSig);

/// <summary>伺服器指派或收回某邏輯帳戶的用量查詢（設計 6.6）：term 遞增，舊 term 的結果不採用</summary>
internal sealed record UsageAssign(string LogicalAccountId, long Term, bool On);

/// <summary>
/// Prove 成功：發 15 分鐘的存取權杖、告訴裝置從哪個 deviceSeq 開始重送（之前的都已提交）。
/// <c>resync</c>＝伺服器還原後的對帳模式（設計 10.2）：裝置重設游標、重送 outbox 裡保留的全部事件。
/// <c>tokenRotation</c>：權杖超過 30 天時帶新的 chd_（已在釘選驗證過的通道內），裝置換存新的。
/// <c>updateOnly</c>（app 自我更新 U2，docs/sync-protocol.md 1.4）：只更新的連線才是 true（hub 只放行 RefreshAccessToken 與 Heartbeat，
/// 存取權杖照常發）；其他時候是 null、線上省略，伺服器不送 false。
/// </summary>
internal sealed record ProveResponse(
    string AccessToken,
    long AccessTokenExpiresAt,
    long NextDeviceSeqExpected,
    bool Resync,
    IReadOnlyList<UsageAssign> UsageAssignments,
    string? TokenRotation,
    bool? UpdateOnly = null);

/// <summary>
/// 持久事件（outbox 的一列）。<c>payload</c> 是字串（<see cref="PayloadRules"/>），<c>payloadSha256</c> 是 UTF-8 原文的 SHA-256。
/// 對話事件（第 4 階段）另帶 conversationId 與 epoch。
/// </summary>
internal sealed record SyncEvent(
    long DeviceSeq,
    string Stream,
    string Kind,
    string Enc,
    string PayloadSha256,
    string Payload,
    string? ConversationId = null,
    long? Epoch = null);

/// <summary>PublishBatch（Invoke，只入列、立即返回）：deviceSeq 必須連續；結果以 BatchAck 另外送回</summary>
internal sealed record Batch(string BatchId, IReadOnlyList<SyncEvent> Events);

/// <summary>
/// 一筆的結果：接受時帶伺服器 seq；被擋時帶原因（仍然佔掉 deviceSeq、寫進 rejected_events，不會重送）。
/// 第 3 階段的原因：<c>invalid</c>、<c>too_large</c>、<c>conflict</c>（樂觀並行的版本不符）；第 4 階段起另有 <c>fenced</c>、<c>not_owner</c>、<c>deleted</c>、<c>dlp</c>。
/// </summary>
internal sealed record BatchEventResult(long DeviceSeq, long? Seq = null, string? Rejected = null, long? Epoch = null, string? Owner = null);

/// <summary>整批被拒（沒有任何一筆生效）：<c>gap</c>（從 expected 重送）、<c>seq_regression</c>（停止同步）、<c>invalid</c>／<c>too_large</c></summary>
internal sealed record BatchError(string Code, long? Expected = null);

/// <summary>伺服器 → 裝置：交易提交（fsync）之後才送；acceptedThrough 以前的 deviceSeq 都有結果</summary>
internal sealed record BatchAck(string BatchId, long AcceptedThrough, IReadOnlyList<BatchEventResult> Results, BatchError? Error = null);

/// <summary>拉取的位置：依類別（<c>cls</c>）或依串流（<c>stream</c>，第 4 階段的單一對話）擇一</summary>
internal sealed record PullCursor(long After, string? Cls = null, string? Stream = null);

internal sealed record PullRequest(IReadOnlyList<PullCursor> Cursors, int MaxEvents, int MaxBytes);

/// <summary>拉到的事件；payload 與 enc 是伺服器存下的原樣</summary>
internal sealed record PulledEvent(
    long Seq,
    string Stream,
    string Cls,
    string Author,
    string Kind,
    string Enc,
    string Payload,
    long CreatedAt,
    string? ConversationId = null,
    long? Epoch = null);

/// <summary><c>more</c>＝還有沒拉完的；<c>high</c>＝伺服器目前最大的 seq</summary>
internal sealed record PullResponse(IReadOnlyList<PulledEvent> Events, bool More, long High);

/// <summary>伺服器 → 裝置：「有新的持久事件」（≤每 200 ms 合併一次），裝置自己來拉</summary>
internal sealed record Notify(long High, IReadOnlyList<string> Cls);

/// <summary>裝置 → 伺服器（Send，每 15 秒）：UI 執行緒最後一次存活的時間；執行中與等待回應的對話（第 4 階段起填）</summary>
internal sealed record Heartbeat(long UiAliveAt, IReadOnlyList<string> Running, IReadOnlyList<string> Waiting);

/// <summary>
/// 裝置 → 伺服器（Invoke）：分享用量。負責查詢的裝置帶 term；對話中的 rate limit 事件（任何裝置，每帳戶 ≤每 60 秒一次）不帶 term。
/// 伺服器寫 usage_latest／usage_points 後以 usage.snapshot 發布；舊 term 的結果不採用。
/// </summary>
internal sealed record UsagePublish(string LogicalAccountId, long? Term, SharedUsage Usage);

/// <summary>伺服器 → 裝置：裝置名冊變了（配對、撤銷），裝置呼叫 GetTrust 拉新的</summary>
internal sealed record TrustChanged(long Registry);

/// <summary>
/// 伺服器 → 裝置（kind 見 <see cref="ServerNoticeKinds"/>）：<c>shutdown</c>（等 retryAfterSec 再重連，不顯示錯誤）、<c>certExpiring</c>、
/// <c>minAppVersion</c>、<c>alert</c>、<c>appRelease</c>（app 自我更新 U2：<c>release</c> 是新收的版本，裝置自己到 latest 檢查、自己驗簽章）
/// </summary>
internal sealed record ServerNotice(string Kind, int? RetryAfterSec = null, string? Text = null, string? Release = null);

/// <summary>GetTrust：裝置目前採用的名冊版本（第一次為 0）</summary>
internal sealed record GetTrustRequest(long RegistryAfter);

/// <summary>
/// 信任資料（GetTrust 的回應、配對回應的一部分）：裝置名冊（registryAfter 與目前版本不同時給整份，否則 null）。
/// 名冊由伺服器授權（設計 14.4）：裝置只檢查格式（<see cref="DeviceRegistryRules.IsValid(IReadOnlyList{DeviceRegistryEntry})"/>），
/// 信任來自釘選伺服器身分金鑰的通道。
/// </summary>
internal sealed record TrustBundle(
    long RegistryRev,
    IReadOnlyList<DeviceRegistryEntry>? Registry);

internal sealed record AccessTokenResponse(string AccessToken, long ExpiresAt);

// ── 第 4 階段（docs/sync-protocol.md 第 14 節；WP4.1 只立型別）：雙方在 Hello 宣告對應的功能（DeviceFeatureRules）才送，
//    沒宣告的一方照第 3 階段——伺服器不送這些訊息給它、它也不送

/// <summary>
/// 伺服器 → 裝置（Send，<c>cmd1</c>）：一個遠端指令（14.4）。<c>commandId</c> 等於信封的 <c>cid</c>（<see cref="CommandEnvelope.Verify"/> 的第 6 步）
/// </summary>
internal sealed record CommandDelivery(string CommandId, CommandEnvelope Envelope);

/// <summary>
/// 裝置 → 伺服器（Invoke，<c>cmd1</c>）：遠端指令的最終結果（14.6）。<c>state</c>、<c>code</c> 的值在 <c>Myriagent.Remote.RemoteResultStates</c>／
/// <c>RemoteResultCodes</c>；<c>tier</c>／<c>fullAccess</c> 是電腦算出的層級（<c>step_up_required</c> 時網頁據此提示）。伺服器以
/// <c>RemoteResultRules.Check</c> 檢查，只收目標裝置已證明連線送來的（D18）
/// </summary>
internal sealed record CommandResult(string CommandId, string State, string? Code = null, string? Message = null, int? Tier = null, bool? FullAccess = null);

/// <summary>
/// 裝置 → 伺服器（Invoke，<c>cmd1</c>）：指令執行後 30 秒內中樞回給「這個檢視」的網頁訊息（14.6；例如 <c>files</c>、<c>accountLogin</c>）。
/// 伺服器只轉型錄白名單裡的 <c>message.type</c>（<c>RemoteResultRules.CheckOutput</c>）給發起的瀏覽器連線、不保存
/// </summary>
internal sealed record CommandOutput(string CommandId, JObject Message);

/// <summary>
/// 裝置 → 伺服器（Invoke，<c>peer1</c>；桌面遠端操作，docs/sync-protocol.md 14.7）：桌面對其他電腦下的指令。<c>commandId</c> 是桌面產生的 ULID
/// （網頁的 commandId，結果以它回），<c>cmd</c> 是網頁指令物件（同瀏覽器 <c>Cmd</c> 的 <c>cmd</c>）。伺服器照瀏覽器的路由檢查、簽信封、轉送，
/// 回 <see cref="PeerCmdResult"/>；之後的結果與輸出以 <see cref="PeerReply"/> 送回這台
/// </summary>
internal sealed record PeerCmd(string CommandId, JObject Cmd);

/// <summary>
/// <see cref="PeerCmd"/> 的回應（同瀏覽器的 <c>CmdResult</c>）：<c>accepted</c> 只表示已送出或已立即拒絕（拒絕另以 <see cref="PeerReply"/> 的
/// <c>commandResult</c> 送）；false 時 <c>error</c> 是伺服器自己判得出的錯誤（<c>invalid</c>、<c>conflict</c>、<c>unavailable</c>、<c>not_available</c>、<c>rate_limited</c>）
/// </summary>
internal sealed record PeerCmdResult(bool Accepted, string? CommandId = null, string? Error = null);

/// <summary>
/// 伺服器 → 裝置（Send，<c>peer1</c>）：桌面發起的指令的結果與輸出（14.7）。<c>commandId</c> 是 <see cref="PeerCmd.CommandId"/>，
/// <c>message</c> 是網頁訊息（<c>commandResult</c>、<c>sendRejected</c>、<c>toast</c>、型錄的輸出白名單與 <c>peerCreated</c>），桌面只轉給發起的檢視
/// </summary>
internal sealed record PeerReply(string CommandId, JObject Message);

/// <summary>
/// 遠端執行指令的一段串流（docs/sync-protocol.md 14.11，<c>exec1</c>）：被操作的電腦以 <c>ExecOutput</c> 送（<c>commandId</c> 是信封的 cid），
/// 伺服器以 <c>ExecReply</c> 轉給發起的電腦（<c>commandId</c> 換成 <see cref="PeerCmd.CommandId"/>）。<c>seq</c> 從 0 起每段加 1（收的一方以它看出缺漏）；
/// <c>kind</c> 是 <c>status</c>（<c>code</c>：<c>confirm</c> 等待那台的人同意、<c>started</c> 已開始、<c>truncated</c> 之後的輸出不再轉送）、
/// <c>out</c>／<c>err</c>（<c>text</c> 是標準輸出／錯誤）、<c>exit</c>（最後一段：<c>exitCode</c> 是程式的結束碼、沒跑起來是 null，
/// <c>code</c> 是沒有正常結束的原因，<c>text</c> 是給人看的說明）。規則在 <c>RemoteExecRules</c>
/// </summary>
internal sealed record ExecChunk(string CommandId, long Seq, string Kind, string? Text = null, int? ExitCode = null, string? Code = null);

/// <summary><c>ExecOutput</c> 的回應：<c>keep</c> 是 false 時伺服器已經不轉送這個指令（發起的電腦離線太久、伺服器重啟、已過期），被操作的電腦要停止它</summary>
internal sealed record ExecOutputResult(bool Keep);

/// <summary>裝置 → 伺服器（Invoke，<c>exec1</c>）：發起的電腦要停止它下的遠端執行指令（<c>commandId</c> 是 <see cref="PeerCmd.CommandId"/>）</summary>
internal sealed record ExecCancel(string CommandId);

/// <summary><c>ExecCancel</c> 的回應：<c>found</c>＝伺服器找得到這個指令、已轉給被操作的電腦</summary>
internal sealed record ExecCancelResult(bool Found);

/// <summary>伺服器 → 裝置（Send，<c>exec1</c>）：停止這個遠端執行指令（<c>commandId</c> 是信封的 cid）</summary>
internal sealed record ExecAbort(string CommandId);

/// <summary>
/// 裝置 → 伺服器（Send，<c>live1</c>）：串流中的項目（14.3）。<c>item</c> 是 <c>conv.item</c> 的 payload 的 JSON 字串
/// （同持久事件的 payload 以字串傳：伺服器驗證只有一種解讀、原樣轉發），其中的 <c>ord</c>／<c>ver</c> 要等於外層的（<see cref="LiveRules.CheckRelay"/>）
/// </summary>
internal sealed record LiveRelay(string ConversationId, long Epoch, long Ord, long Ver, string Item);

/// <summary>
/// 增量的一個長欄位（<see cref="LiveDeltaRules.Fields"/>：<c>text</c>、<c>detail</c>、<c>output</c>）：新值＝基底那一欄的前 <see cref="At"/> 個 UTF-16 碼元＋<see cref="S"/>，
/// 長度是 <see cref="N"/>（收方套用後核對）。新值是空字串時重組的整則省略那一欄
/// </summary>
internal sealed record LiveDeltaOp(string F, int At, string S, int N);

/// <summary>
/// 裝置 → 伺服器（Send，<c>live2</c>）：串流中的項目只送和上一則（<see cref="BaseVer"/>，這條連線上這一筆最後送出的版本）不同的部分。
/// <see cref="Head"/> 是 <c>conv.item</c> 投影去掉長欄位之後的 JSON 字串（同 <see cref="LiveRelay.Item"/> 以字串傳），沒列在 <see cref="Ops"/> 的長欄位＝與基底相同。
/// 重組（<see cref="LiveDeltaRules.Apply"/>）的結果與直接送整則（<see cref="LiveRelay"/>）的 <c>item</c> 逐位元組相同
/// </summary>
internal sealed record LiveDelta(string ConversationId, long Epoch, long Ord, long Ver, long BaseVer, string Head, IReadOnlyList<LiveDeltaOp> Ops);

/// <summary>伺服器 → 裝置（Send，<c>live2</c>）：這一筆沒有（或對不上）增量的基底，請下一則送整則（<see cref="LiveResyncReasons"/>）</summary>
internal sealed record LiveResync(string ConversationId, long Ord, string Reason);

/// <summary><see cref="LiveResync.Reason"/></summary>
internal static class LiveResyncReasons
{
    /// <summary>伺服器沒有這一筆的基底，或 baseVer、長度對不上</summary>
    public const string Base = "base";

    /// <summary>伺服器的第二道 DLP 命中、再遮蔽過：這一筆到離開 Running 之前都只送整則（伺服器不留它判定外洩的原文當基底）</summary>
    public const string Remask = "remask";

    /// <summary>伺服器的基底滿了，丟掉了這一筆</summary>
    public const string Evicted = "evicted";

    public static bool IsKnown(string? reason) => reason is Base or Remask or Evicted;
}

/// <summary>伺服器 → 裝置（Send，<c>live1</c>）：有人在看的對話（整份取代）；擁有者只對這些對話送 <see cref="LiveRelay"/></summary>
internal sealed record WatchChanged(IReadOnlyList<string> ConversationIds);

/// <summary>
/// 伺服器 → 裝置（Send，<c>conv1</c>）：要這個對話的整份內容（<see cref="SnapshotReasons"/>：伺服器還原後的補齊、回補還沒輪到就有人打開，插隊到下一個）
/// </summary>
internal sealed record SnapshotRequest(string ConversationId, string Reason);

internal static class SnapshotReasons
{
    /// <summary>伺服器還原後的補齊</summary>
    public const string Restore = "restore";

    /// <summary>回補還沒輪到就有人打開</summary>
    public const string Open = "open";

    public static IReadOnlyList<string> All { get; } = [Restore, Open];
}

/// <summary>
/// 伺服器 → 裝置（Send，<c>media1</c>）：向發這個媒體參照的裝置要位元組（14.3、設計 4.10）；裝置以 <c>PUT /api/v1/device/relay/{relayId}</c>（<c>cha_</c>）回。
/// <c>range</c> 是瀏覽器要的位元組範圍（null＝整份）
/// </summary>
internal sealed record MediaRequest(string RelayId, string Ref, string Purpose, MediaRange? Range = null);

/// <summary>位元組範圍：<c>start</c> 起（含），到 <c>end</c> 為止（含；null＝到結尾）——同 HTTP 的 <c>Range: bytes=start-end</c></summary>
internal sealed record MediaRange(long Start, long? End = null);

internal static class MediaPurposes
{
    /// <summary>原檔（圖片、影片）</summary>
    public const string Media = "media";

    /// <summary>縮圖</summary>
    public const string Thumb = "thumb";

    /// <summary>檔案下載（fileFetch，層級 2）</summary>
    public const string File = "file";

    public static IReadOnlyList<string> All { get; } = [Media, Thumb, File];
}
