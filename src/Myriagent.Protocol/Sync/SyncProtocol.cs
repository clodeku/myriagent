namespace Myriagent.Sync;

/// <summary>
/// 裝置↔伺服器同步協定 v1 的常數（docs/sync-protocol.md）。裝置 hub 是 SignalR、只允許 WebSockets、Newtonsoft JSON 協定（<see cref="SyncJson"/>）；
/// 瀏覽器 hub 與它完全分開（裝置權杖在瀏覽器 hub 無效、cookie 在裝置 hub 無效）。
/// </summary>
internal static class SyncProtocol
{
    /// <summary>
    /// 這一版的 protocolVersion：桌面的 Hello 與配對送它，發佈工具寫進 manifest。伺服器支援 [<see cref="MinSupportedVersion"/>, Version]：
    /// 配對與 manifest 以 <see cref="IsSupported"/> 檢查（範圍外配對回 <see cref="SyncErrorCodes.Protocol"/>、manifest 拒收）；Hello 由 MyriagentHub 的
    /// <c>DeviceHubRules.Gate</c> 依同一組常數判斷，比最低版本舊而且宣告了 <see cref="DeviceFeatureRules.Update1"/> 的是只更新的連線，不回 protocol（docs/sync-protocol.md 1.4）
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// 伺服器支援的最舊 protocolVersion：支援 [MinSupportedVersion, Version]。升 <see cref="Version"/> 時至少留前一版
    /// （app 自我更新規格 7.6：伺服器一律先部署、再發佈用新協定的桌面；HandshakeShapeInvariantTests 把關）
    /// </summary>
    public const int MinSupportedVersion = 1;

    /// <summary>
    /// 協定版本在 [<see cref="MinSupportedVersion"/>, <see cref="Version"/>] 之內：配對（/pair/start，MyriagentHub 的 <c>PairingServerRules.CheckStart</c>）與
    /// manifest 的 protocolVersion（<see cref="Myriagent.Update.ReleasePackageRules.VerifyManifest"/>：伺服器收件與發佈工具）用它。
    /// Hello 不呼叫它：MyriagentHub 的 <c>DeviceHubRules.Gate</c> 依同一組常數另外判斷（比最低版本舊而且宣告了 update1 的是只更新的連線），改這裡不會改到 Hello
    /// </summary>
    public static bool IsSupported(int protocolVersion) => protocolVersion >= MinSupportedVersion && protocolVersion <= Version;

    public const string DeviceHubPath = "/hubs/v1/device";

    public const string BrowserHubPath = "/hubs/v1/browser";

    /// <summary>裝置權杖：只能用在裝置 hub 的連線（Hello）；配對時給一次，DPAPI 保存</summary>
    public const string DeviceTokenPrefix = "chd_";

    /// <summary>Prove 成功後發的 15 分鐘存取權杖：HTTP 的裝置端點只收它</summary>
    public const string AccessTokenPrefix = "cha_";

    public const int AccessTokenMinutes = 15;

    /// <summary>存取權杖到期前多久換新（RefreshAccessToken）</summary>
    public const int AccessTokenRefreshBeforeMinutes = 2;

    /// <summary>裝置 hub 單則訊息上限（MaximumReceiveMessageSize；用戶端送出前自檢，永遠不送超過）</summary>
    public const int DeviceHubMaxMessageBytes = 1024 * 1024;

    /// <summary>
    /// 單一引數的呼叫（CommandResult、CommandOutput、LiveRelay）替 SignalR 呼叫框預留的位元組：上限量的是整個框
    /// （<c>{"type":1,"invocationId":"N","target":"…","arguments":[…]}</c> 加結尾的 0x1E），不只是引數。方法名稱固定、invocationId 是短數字，
    /// 框的額外位元組有上限，4 KB 足夠（PublishBatch 另有 64 KB 的 OutboxRules.BatchEnvelopeReserveBytes）
    /// </summary>
    public const int InvocationFrameReserveBytes = 4 * 1024;

    /// <summary>引數序列化後 <paramref name="argumentBytes"/> 位元組（UTF-8）放得進一個呼叫框嗎：≤ 上限減預留（<see cref="InvocationFrameReserveBytes"/>）</summary>
    public static bool FitsInvocation(int argumentBytes) => argumentBytes <= DeviceHubMaxMessageBytes - InvocationFrameReserveBytes;

    /// <summary>心跳間隔；伺服器 60 秒沒收到心跳就當成離線（用量指派改派、租約失聯的依據）</summary>
    public const int HeartbeatSeconds = 15;

    public const int OfflineAfterSeconds = 60;
}

/// <summary>裝置 hub 的方法名稱（伺服器與桌面的 SignalR 都用字串呼叫，兩邊共用這份常數）</summary>
internal static class DeviceHubMethods
{
    // 裝置 → 伺服器（Invoke／Send）
    public const string Hello = "Hello";
    public const string Prove = "Prove";
    public const string PublishBatch = "PublishBatch";
    public const string Pull = "Pull";
    public const string Heartbeat = "Heartbeat";
    public const string UsagePublish = "UsagePublish";
    public const string GetTrust = "GetTrust";
    public const string RefreshAccessToken = "RefreshAccessToken";

    // 伺服器 → 裝置（第 3 階段只有這些：沒有任何會讓裝置執行代理動作的訊息）
    public const string BatchAck = "BatchAck";
    public const string Notify = "Notify";
    public const string UsageAssign = "UsageAssign";
    public const string TrustChanged = "TrustChanged";
    public const string ServerNotice = "ServerNotice";

    // 第 4 階段（docs/sync-protocol.md 14.3、14.6）：雙方在 Hello 宣告對應的功能才送（DeviceFeatureRules）；WP4.1 只立名稱，兩邊都不送
    // 裝置 → 伺服器
    public const string LiveRelay = "LiveRelay";

    /// <summary>串流中的項目只送和上一則不同的部分（<c>live2</c>，sync-protocol 14.3；docs/plans/2026-10-23-live-delta-and-replica-live.md 第 3 節）</summary>
    public const string LiveDelta = "LiveDelta";
    public const string CommandResult = "CommandResult";
    public const string CommandOutput = "CommandOutput";

    // 伺服器 → 裝置（CommandDelivery 是第一個會讓裝置執行指令的訊息：裝置驗過信封、本機政策與中樞閘門之後才執行，14.4、14.6）
    public const string WatchChanged = "WatchChanged";

    /// <summary>伺服器沒有（或對不上）這一筆的增量基底：請下一則送整則（<c>live2</c>）</summary>
    public const string LiveResync = "LiveResync";
    public const string SnapshotRequest = "SnapshotRequest";
    public const string MediaRequest = "MediaRequest";
    public const string CommandDelivery = "CommandDelivery";

    // 桌面遠端操作（docs/sync-protocol.md 14.7，peer1）：桌面對其他電腦下指令、收結果
    // 裝置 → 伺服器
    public const string PeerCmd = "PeerCmd";

    // 伺服器 → 裝置
    public const string PeerReply = "PeerReply";

    // 各電腦的即時狀態（docs/sync-protocol.md 14.8，stat1）：只在記憶體、可丟的即時資料
    // 裝置 → 伺服器
    public const string StatsPublish = "StatsPublish";

    // 伺服器 → 裝置
    public const string StatsUpdate = "StatsUpdate";

    // 各帳戶的 token 計數（docs/sync-protocol.md 14.9，tok1）：裝置 → 伺服器，只供顯示
    public const string TokensPublish = "TokensPublish";

    // 其他電腦的 token 計數（docs/sync-protocol.md 14.10，tok2）：伺服器 → 裝置，只供桌面帳戶卡顯示各電腦的合計
    public const string TokensUpdate = "TokensUpdate";

    // 各電腦的排程（docs/sync-protocol.md 14.12，sched1）：裝置 → 伺服器（Invoke，這台的摘要整份取代）、伺服器 → 裝置（Send，其他電腦的）
    public const string SchedulesPublish = "SchedulesPublish";

    public const string SchedulesUpdate = "SchedulesUpdate";

    // 第 5 階段：租約、交接與強制接手（docs/sync-protocol.md 第 16 節，hand1）；逐字稿的 HTTP 端點不是 hub 方法（log1）。
    // WP5.1 只立名稱與形狀：伺服器的實作在 WP5.5（Realtime/DeviceHub.Handoff.cs），桌面的在 WP5.6／WP5.7
    // 裝置 → 伺服器
    public const string HandoffPreview = "HandoffPreview";
    public const string HandoffStart = "HandoffStart";
    public const string HandoffCancel = "HandoffCancel";
    public const string HandoffProgress = "HandoffProgress";
    public const string HandoffReady = "HandoffReady";

    // 伺服器 → 裝置
    public const string HandoffRequest = "HandoffRequest";
    public const string HandoffUpdate = "HandoffUpdate";

    /// <summary>伺服器 → 擁有者（Send，log1）：存放的日誌損毀，整檔以新世代重傳</summary>
    public const string LogRepair = "LogRepair";

    // 遠端執行指令（docs/sync-protocol.md 14.11，exec1）：remoteExec 照一般的遠端指令簽發與執行，輸出與結束碼走這四個
    // 裝置 → 伺服器
    /// <summary>被操作的電腦送指令的輸出、狀態與結束碼（Invoke；回 <see cref="ExecOutputResult"/>）</summary>
    public const string ExecOutput = "ExecOutput";

    /// <summary>發起的電腦要停止它下的指令（Invoke；回 <see cref="ExecCancelResult"/>）</summary>
    public const string ExecCancel = "ExecCancel";

    // 伺服器 → 裝置
    /// <summary>轉給發起的電腦（Send）：commandId 換成發起時的</summary>
    public const string ExecReply = "ExecReply";

    /// <summary>轉給被操作的電腦（Send）：停止這個指令</summary>
    public const string ExecAbort = "ExecAbort";

    // 多人協作（docs/sync-protocol.md 14.14，collab1）：擁有者在桌面管理協作人與對話的分享
    /// <summary>裝置 → 伺服器（Invoke；回 <see cref="CollabCmdResult"/>）：桌面替擁有者送的管理指令</summary>
    public const string CollabCmd = "CollabCmd";

    /// <summary>伺服器 → 裝置（Send，<see cref="CollaboratorsState"/>）：協作人清單與分享，整份取代</summary>
    public const string CollabUpdate = "CollabUpdate";

    // 遠端桌面（docs/sync-protocol.md 14.16，desk1）：deskOpen 照一般的遠端指令簽發，之後的封包走這兩個
    /// <summary>裝置 → 伺服器（Invoke；回 <see cref="DeskUpResult"/>）：被看的電腦送影格與狀態、看的電腦送輸入與確認</summary>
    public const string DeskUp = "DeskUp";

    /// <summary>伺服器 → 裝置（Send，<see cref="DeskPacket"/>）：轉給工作階段的另一邊</summary>
    public const string DeskDown = "DeskDown";

    /// <summary>裝置 → 伺服器（Invoke，<see cref="ComposerDraftPublish"/>，draft1）：這台輸入框打到一半的字（空字串＝清掉）</summary>
    public const string ComposerDraftPublish = "ComposerDraftPublish";

    /// <summary>伺服器 → 裝置（Send，<see cref="ComposerDraftUpdate"/>，draft1）：其他電腦與瀏覽器版的草稿（第一份是整份，之後逐則）</summary>
    public const string ComposerDraftUpdate = "ComposerDraftUpdate";

    /// <summary>
    /// 裝置 → 伺服器的方法（伺服器裝置 hub 的公開方法就是這些；WP4.1 步 12 的 <c>DeviceHubFilter</c> 以它當允許清單，清單外的一律 HubException）
    /// </summary>
    public static IReadOnlyList<string> DeviceToServer { get; } =
        [Hello, Prove, PublishBatch, Pull, Heartbeat, UsagePublish, GetTrust, RefreshAccessToken, LiveRelay, LiveDelta, CommandResult, CommandOutput, PeerCmd, StatsPublish, TokensPublish,
            HandoffPreview, HandoffStart, HandoffCancel, HandoffProgress, HandoffReady, ExecOutput, ExecCancel, SchedulesPublish, CollabCmd, DeskUp, ComposerDraftPublish];

    /// <summary>伺服器 → 裝置的方法（桌面的 SignalR 用戶端以 <c>On</c> 登記；第 4 階段的由用到它的工作包才登記）</summary>
    public static IReadOnlyList<string> ServerToDevice { get; } =
        [BatchAck, Notify, UsageAssign, TrustChanged, ServerNotice, WatchChanged, LiveResync, SnapshotRequest, MediaRequest, CommandDelivery, PeerReply, StatsUpdate, TokensUpdate,
            HandoffRequest, HandoffUpdate, LogRepair, ExecReply, ExecAbort, SchedulesUpdate, CollabUpdate, DeskDown, ComposerDraftUpdate];
}

/// <summary>錯誤代碼（BatchAck.error、hub 例外訊息、HTTP 錯誤的 error 欄位）</summary>
internal static class SyncErrorCodes
{
    /// <summary>批次第一筆的 deviceSeq 比伺服器期待的大：從 <c>expected</c> 重送</summary>
    public const string Gap = "gap";

    /// <summary>同一個 (deviceId, deviceSeq) 已有內容不同的事件：裝置的 sync.db 倒退了（被還原或複製到別台），停止同步、提示重新配對</summary>
    public const string SeqRegression = "seq_regression";

    /// <summary>
    /// 提示更新 app（docs/sync-protocol.md 1.4）。Hello：protocolVersion 比伺服器支援的最高版本新（或小於 1）；或協定比支援的最低版本舊、
    /// app 版本低於 minAppVersion（兩者其一），而且 Hello 沒宣告 <see cref="DeviceFeatureRules.Update1"/>（宣告了就是只更新的連線）。
    /// 配對（/pair/start）：協定不在支援的範圍或 app 版本低於 minAppVersion 就回它，不看 update1
    /// </summary>
    public const string Protocol = "protocol";

    /// <summary>沒有先 Hello／Prove、或 Prove 失敗</summary>
    public const string NotProven = "not_proven";

    /// <summary>批次或事件格式錯誤（不佔 deviceSeq）</summary>
    public const string Invalid = "invalid";

    public const string TooLarge = "too_large";

    /// <summary>
    /// 伺服器排空中（部署換版）：新的 Hello 一律回它，app 套件下載回 503 的本文也是它。裝置照 <c>ServerNotice{shutdown}</c> 的
    /// retryAfterSec 或 <c>Retry-After</c> 稍後再試，不顯示錯誤（docs/sync-protocol.md 第 11、15 節）
    /// </summary>
    public const string Draining = "draining";

    /// <summary>只更新的連線呼叫 RefreshAccessToken、Heartbeat 以外的方法：這台的 app 太舊，只能下載更新（docs/sync-protocol.md 1.4）</summary>
    public const string UpdateRequired = "update_required";
}

/// <summary>
/// <see cref="ServerNotice"/> 的 kind（app 自我更新 U2 起伺服器用這份；桌面 SyncEngine.Session.cs 的私有常數在 U3 換成它）。
/// 裝置遇到不認得的種類只記錄、略過。第 4 階段的 <c>ownerLogin</c>（D27）依擁有者 2026-10-19 指示不做（伺服器不送、桌面不處理），沒有常數
/// </summary>
internal static class ServerNoticeKinds
{
    /// <summary>伺服器即將停機（排空開始：部署或 admin drain；直接停止服務時不送）：等 retryAfterSec 再重連，不顯示錯誤</summary>
    public const string Shutdown = "shutdown";

    public const string CertExpiring = "certExpiring";

    public const string MinAppVersion = "minAppVersion";

    public const string Alert = "alert";

    /// <summary>收進了新版（<see cref="ServerNotice.Release"/>）：裝置自己到 latest 檢查、自己驗簽章（docs/sync-protocol.md 第 4、15 節）</summary>
    public const string AppRelease = "appRelease";
}

/// <summary>伺服器在 HelloResponse 告訴裝置的上限（設計 4.6）；裝置照它切批次</summary>
internal sealed record SyncLimits(
    int MaxBatchBytes,
    int MaxBatchEvents,
    int MaxInlinePayload,
    int MaxLiveHz,
    int MaxInFlightBatches,
    int MaxPullEvents,
    int MaxPullBytes,
    int? MaxLiveDeltaHz = null)
{
    // MaxLiveDeltaHz：協商出 live2 時每個對話即時更新的上限（依大小降頻，LiveRules.DeltaIntervalMs）；
    // 舊伺服器的 HelloResponse 沒有這個欄位（null，線上省略）＝不提供，桌面照 MaxLiveHz 送整則。MaxLiveHz 維持 4：舊桌面照它送整則，調高會讓舊桌面的流量加倍
    public static readonly SyncLimits Default = new(
        MaxBatchBytes: 256 * 1024,
        MaxBatchEvents: 500,
        MaxInlinePayload: 128 * 1024,
        MaxLiveHz: 4,
        MaxInFlightBatches: 4,
        MaxPullEvents: 500,
        MaxPullBytes: 1024 * 1024,
        MaxLiveDeltaHz: 8);
}
