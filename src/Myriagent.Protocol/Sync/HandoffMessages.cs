namespace Myriagent.Sync;

// 第 5 階段：租約、交接與強制接手的裝置 hub 訊息（計畫 docs/plans/2026-10-23-phase5-handoff.md 第 5.5 節；docs/sync-protocol.md 第 16 節）。
// 參數一律是一個 JSON 物件、camelCase（SyncJson），null 欄位省略。只在雙方協商出 hand1 的連線上送（DeviceFeatureRules.RequiredFor），
// LogRepair 要 log1。WP5.1 只立形狀：伺服器的處理在 WP5.5、桌面的在 WP5.6／WP5.7；任何一方都不送，直到 hand1 啟用（WP5.10）。
// 不簽章（擁有者決定 3、D16）：連線本來就綁 TLS SPKI，被操作的電腦照樣只看本機政策。
// 新欄位一律加在記錄最後、有預設值：舊版不認得的欄位忽略，新版讀不到的欄位是 null。

/// <summary>交接的種類（<c>handoffs.kind</c>、<c>HandoffUpdate.kind</c>；D6：沒有 release／acquire）</summary>
internal static class HandoffKinds
{
    /// <summary>線上交接：擁有者配合排空、上傳、回報</summary>
    public const string Transfer = "transfer";

    /// <summary>強制接手：擁有者失聯（<see cref="LeaseRules.IsOrphaned"/>），伺服器以已上傳的內容直接提交並記圍欄</summary>
    public const string Takeover = "takeover";

    public static IReadOnlyList<string> All { get; } = [Transfer, Takeover];

    public static bool IsKnown(string? kind) => kind is Transfer or Takeover;
}

/// <summary>線上交接的排空模式（D18：afterTurn 的期限固定 30 分鐘，now 3 分鐘）</summary>
internal static class HandoffModes
{
    /// <summary>等這一輪結束、到安全點才關（預設）</summary>
    public const string AfterTurn = "afterTurn";

    /// <summary>立即停止（中斷執行中的工具，記下 <see cref="HandoffInterruption"/>）</summary>
    public const string Now = "now";

    public static IReadOnlyList<string> All { get; } = [AfterTurn, Now];

    public static bool IsKnown(string? mode) => mode is AfterTurn or Now;
}

/// <summary>伺服器 <c>handoffs.state</c>（計畫 7.1 的狀態機，<see cref="LeaseRules.Transition"/>）</summary>
internal static class HandoffStates
{
    /// <summary>已建立、已送 <c>HandoffRequest</c> 給擁有者，等它接受（15 秒）</summary>
    public const string Requested = "requested";

    public const string Accepted = "accepted";

    /// <summary>擁有者正在等這一輪結束、關程序、上傳</summary>
    public const string Draining = "draining";

    /// <summary>擁有者送了 <c>HandoffReady</c>（持久；伺服器重啟後重新驗證再提交，D17）</summary>
    public const string Ready = "ready";

    /// <summary>已提交：擁有者與 epoch 已換、<c>conv.lease</c> 已寫（之後只更新 <c>import_state</c>）</summary>
    public const string Committed = "committed";

    public const string Cancelled = "cancelled";

    public const string Aborted = "aborted";

    public const string Expired = "expired";

    public static IReadOnlyList<string> All { get; } = [Requested, Accepted, Draining, Ready, Committed, Cancelled, Aborted, Expired];

    /// <summary>進行中（伺服器 <c>ux_handoffs_active</c> 的條件：同一個對話最多一筆）</summary>
    public static IReadOnlyList<string> Active { get; } = [Requested, Accepted, Draining, Ready];

    public static bool IsKnown(string? state) => state is not null && All.Contains(state, StringComparer.Ordinal);

    public static bool IsActive(string? state) => state is Requested or Accepted or Draining or Ready;

    /// <summary>結束了（committed 之後只有接手端的匯入狀態會變）</summary>
    public static bool IsTerminal(string? state) => state is Committed or Cancelled or Aborted or Expired;
}

/// <summary><c>HandoffProgress.role</c> 與桌面 <c>handoffs_local.role</c>（後者另有 <see cref="Fork"/>）</summary>
internal static class HandoffRoles
{
    /// <summary>交出端（目前的擁有者 A）</summary>
    public const string Source = "source";

    /// <summary>接手端（B）</summary>
    public const string Target = "target";

    /// <summary>桌面本機：離線期間被強制接手後另存的分支（只在 <c>handoffs_local</c>，不上線）</summary>
    public const string Fork = "fork";
}

/// <summary><c>HandoffProgress.state</c>：交出端與接手端各自的進度（<see cref="IsValid"/>）</summary>
internal static class HandoffProgressStates
{
    // 交出端
    public const string Accepted = "accepted";
    public const string Draining = "draining";
    public const string Aborted = "aborted";

    // 接手端（伺服器寫進 handoffs.import_state）
    public const string Importing = "importing";
    public const string NeedsFolder = "needsFolder";
    public const string Imported = "imported";
    public const string ImportFailed = "importFailed";

    public static IReadOnlyList<string> SourceStates { get; } = [Accepted, Draining, Aborted];

    public static IReadOnlyList<string> TargetStates { get; } = [Importing, NeedsFolder, Imported, ImportFailed];

    /// <summary>這個角色可以回報這個狀態（交出端 accepted／draining／aborted、接手端 importing／needsFolder／imported／importFailed）</summary>
    public static bool IsValid(string? role, string? state) => role switch
    {
        HandoffRoles.Source => state is Accepted or Draining or Aborted,
        HandoffRoles.Target => state is Importing or NeedsFolder or Imported or ImportFailed,
        _ => false,
    };
}

/// <summary><c>conv.lease.reason</c>（計畫 5.2）</summary>
internal static class LeaseReasons
{
    public const string Transfer = "transfer";

    public const string Takeover = "takeover";

    /// <summary>伺服器還原到交接之前的備份後，新擁有者的 <c>conv.created</c> 認領（計畫 5.4 第 2 點，<see cref="LeaseRules.IsRecoveringClaim"/>）</summary>
    public const string Restore = "restore";

    public static IReadOnlyList<string> All { get; } = [Transfer, Takeover, Restore];

    public static bool IsKnown(string? reason) => reason is Transfer or Takeover or Restore;
}

/// <summary>交接的發起者（<see cref="HandoffInitiator.Kind"/>；D8：擁有者登入的瀏覽器或任何協商出 hand1 的桌面）</summary>
internal static class HandoffInitiatorKinds
{
    public const string Browser = "browser";

    public const string Device = "device";

    public static bool IsKnown(string? kind) => kind is Browser or Device;
}

/// <summary>
/// 逐字稿的狀態。<c>conv.created</c>／<c>conv.meta</c> 的 <c>transcript</c>（擁有者回報，計畫 5.3）只有 <see cref="MetaValues"/>，
/// <c>conv.lease.transcript</c> 另有伺服器判定的 <see cref="Damaged"/>，<c>HandoffPreview</c> 另有 <see cref="Ok"/>、<see cref="None"/>。
/// 除了 ok，換電腦時一律以前文節錄續接
/// </summary>
internal static class TranscriptStates
{
    /// <summary>找不到主檔（被 CLI 清掉、手動刪除；14.2 規則 2）</summary>
    public const string Missing = "missing";

    /// <summary>含這台的 Claude／Codex 登入憑證（D11：只有這台各帳戶設定資料夾裡的權杖值會擋，一般像權杖的字串照樣上傳）</summary>
    public const string Held = "held";

    /// <summary>Codex 主 rollout 的 <c>session_meta</c> 帶 <c>history_base</c>：別處 fork 出來的差量檔，不能單獨搬（E-5）</summary>
    public const string Unportable = "unportable";

    /// <summary>伺服器存的日誌損毀（掃描發現；只在伺服器判定，擁有者不回報）</summary>
    public const string Damaged = "damaged";

    /// <summary><c>HandoffPreview</c>：日誌完整</summary>
    public const string Ok = "ok";

    /// <summary><c>HandoffPreview</c>：伺服器沒有這個對話的日誌</summary>
    public const string None = "none";

    /// <summary>擁有者在 <c>conv.meta</c> 回報的值</summary>
    public static IReadOnlyList<string> MetaValues { get; } = [Missing, Held, Unportable];

    /// <summary><c>conv.lease</c>、<c>HandoffReady</c> 的值</summary>
    public static IReadOnlyList<string> LeaseValues { get; } = [Missing, Held, Unportable, Damaged];

    public static IReadOnlyList<string> PreviewValues { get; } = [Ok, Missing, Held, Unportable, Damaged, None];
}

/// <summary><see cref="HandoffTarget.Why"/>：這台不能接手的原因</summary>
internal static class HandoffTargetReasons
{
    public const string Offline = "offline";

    /// <summary>沒有協商出 hand1（app 太舊）</summary>
    public const string Outdated = "outdated";

    public const string NoAccount = "no_account";

    public const string Policy = "policy";

    /// <summary>它就是目前的擁有者</summary>
    public const string Owner = "owner";

    public static IReadOnlyList<string> All { get; } = [Offline, Outdated, NoAccount, Policy, Owner];
}

/// <summary><see cref="LogRepair.Reason"/></summary>
internal static class LogRepairReasons
{
    public const string Damaged = "damaged";
}

/// <summary>交接的發起者：瀏覽器帶 <see cref="Label"/>（給人看的「瀏覽器」），桌面帶 <see cref="DeviceId"/></summary>
internal sealed record HandoffInitiator(string Kind, string? DeviceId = null, string? Label = null);

/// <summary>
/// 一個日誌的目前狀態（計畫 5.2 的 <c>logs</c>、5.5 的 <c>HandoffReady.logs</c>）：<see cref="LogId"/> 照 <see cref="LogIdRules"/> 組成、
/// <see cref="LogKey"/>＝<see cref="LogIdRules.LogKey"/>、<see cref="Kind"/> 是 <see cref="LogKinds"/>；<see cref="Chain"/> 是到 <see cref="Length"/> 為止的
/// 鏈雜湊（<see cref="LogChainRules"/>，base64）、<see cref="FullSha256"/> 是同一段位元組的 SHA-256（64 個小寫十六進位）
/// </summary>
/// <param name="Main">目前的主檔（Claude 換 session 時換）</param>
/// <param name="RelPath">附件與工作區檔的相對路徑（正斜線）；CLI 紀錄是 null</param>
/// <param name="RefId">附件 id（<c>fa:</c>）</param>
internal sealed record LogHead(
    string LogId,
    string LogKey,
    string Kind,
    bool Main,
    long Gen,
    long Length,
    string Chain,
    string FullSha256,
    string? RelPath = null,
    string? RefId = null);

/// <summary>擁有者回報的工作資料夾 git 狀態（自訂資料夾才有；A4，<c>IGitStatusReader</c> 的摘要）</summary>
/// <param name="DirtyFiles">前 20 個未提交的檔名（相對路徑）</param>
internal sealed record HandoffWorkspace(
    string? RepoKey = null,
    string? Sub = null,
    string? Branch = null,
    string? Head = null,
    string? Upstream = null,
    int Ahead = 0,
    int Behind = 0,
    int Dirty = 0,
    int Untracked = 0,
    IReadOnlyList<string>? DirtyFiles = null);

/// <summary><c>now</c> 模式中斷了什麼（<c>FinalizeInterruptedItems</c> 記下的）</summary>
/// <param name="Tools">被中斷的工具名稱</param>
/// <param name="PendingRequests">等回應而被作廢的卡數</param>
/// <param name="BackgroundStopped">背景工作被停掉了</param>
internal sealed record HandoffInterruption(IReadOnlyList<string> Tools, int PendingRequests, bool BackgroundStopped);

/// <summary>
/// 隨交接帶走的佇列訊息（計畫第 9 節）：<see cref="Secret"/> 的那則 <see cref="Text"/> 只帶遮蔽過的 DisplayText（B 上請使用者重新輸入）；
/// <see cref="Attachments"/> 是附件 id（附件本身以 <c>fa:</c> 日誌搬）
/// </summary>
internal sealed record HandoffQueueEntry(string Id, string Text, IReadOnlyList<string>? Attachments = null, bool Secret = false);

/// <summary>沒有搬的東西（換機說明列出）：超過上限的預設工作區檔、超過 50 MB 的附件</summary>
internal sealed record HandoffSkipped(IReadOnlyList<string>? WorkspaceFiles = null, IReadOnlyList<string>? Attachments = null);

/// <summary>強制接手的圍欄：提交當下伺服器的 seq 與原擁有者最後被處理的 deviceSeq（A 回來時據此判斷有沒有新內容，WP5.8）</summary>
internal sealed record HandoffFence(long AtSeq, long LastDeviceSeq);

/// <summary>裝置 → 伺服器（Invoke，hand1）：換電腦的預覽（瀏覽器經閘道呼叫同一個服務，D15）</summary>
internal sealed record HandoffPreviewRequest(string ConversationId);

/// <summary>一台可能的接手電腦（<see cref="Why"/> 是 <see cref="HandoffTargetReasons"/>，可以接手時 null）</summary>
internal sealed record HandoffTarget(string DeviceId, string? Name, bool Eligible, string? Why = null);

/// <summary>
/// <see cref="HandoffPreviewRequest"/> 的結果（計畫 5.5）。<see cref="Transcript"/> 是 <see cref="TranscriptStates.PreviewValues"/>、
/// <see cref="TranscriptAt"/> 是主檔最後一次附加的時間；<see cref="Blocked"/> 是整個對話不能換的原因（<see cref="HandoffCodes"/> 的
/// unsupported_provider、not_full、deleted、busy）。<see cref="Error"/>（WP5.1 加的，計畫的形狀沒有）：預覽本身失敗（not_found、not_available），
/// 這時其他欄位不算數
/// </summary>
internal sealed record HandoffPreviewResult(
    string? Provider,
    string? OwnerDeviceId,
    bool OwnerOnline,
    long? OwnerLastSeenAt,
    bool CanForce,
    string? Transcript,
    long? TranscriptAt,
    string? CliVersion,
    IReadOnlyList<HandoffTarget>? Targets,
    string? Blocked,
    string? Error = null);

/// <summary>
/// 裝置 → 伺服器（Invoke，hand1）：發起交接或強制接手（<see cref="Force"/>）。<see cref="CommandId"/> 是發起端的 ULID、重送不變：
/// 同一個 commandId 回原結果（伺服器以 <c>handoffs.command_id</c> 唯一鍵去重）。<see cref="AutoContinue"/> 只有 <see cref="HandoffModes.Now"/> 可以
/// </summary>
internal sealed record HandoffStartRequest(string CommandId, string ConversationId, string ToDeviceId, string Mode, bool Force, bool AutoContinue);

/// <summary><see cref="HandoffStartRequest"/> 的結果：成功時 <see cref="HandoffId"/>，失敗時 <see cref="Error"/>（<see cref="HandoffCodes"/>）</summary>
internal sealed record HandoffStartResult(string? HandoffId, string? Error = null);

/// <summary>裝置 → 伺服器（Invoke，hand1）：取消（提交前；任何發起端都可以）</summary>
internal sealed record HandoffCancelRequest(string HandoffId);

internal sealed record HandoffCancelResult(string? Error = null);

/// <summary>
/// 伺服器 → 擁有者 A（Send，hand1）：請它交出（A1～A6）。同一個 <see cref="HandoffId"/> 重送時 A 照目前進度回；A 重連（ConnectionProven）時伺服器重送進行中的
/// </summary>
internal sealed record HandoffRequest(
    string HandoffId,
    string ConversationId,
    long Epoch,
    string Mode,
    string ToDeviceId,
    string? ToName,
    long DeadlineAt,
    HandoffInitiator Initiator);

/// <summary>
/// 裝置 → 伺服器（Invoke，hand1）：交出端（accepted、draining、aborted）或接手端（importing、needsFolder、imported、importFailed）的進度
/// （<see cref="HandoffProgressStates.IsValid"/>）。<see cref="Workspace"/> 是交出端 draining 時附的 git 狀態；回 <see cref="HandoffAck"/>
/// </summary>
internal sealed record HandoffProgress(string HandoffId, string Role, string State, string? Error = null, HandoffWorkspace? Workspace = null, string? Detail = null);

/// <summary>
/// 裝置 → 伺服器（Invoke，hand1）：交出端排空、上傳完（A6）。伺服器驗過（計畫 7.1 的 ready 驗證）就在同一個交易提交；回 <see cref="HandoffAck"/>
/// </summary>
/// <param name="FinalDeviceSeq">A 這個對話最後一筆事件的 deviceSeq（伺服器要處理到它才提交）</param>
/// <param name="Logs">各日誌的目前狀態（主檔、伴隨檔、附件、這次快照的工作區檔）</param>
/// <param name="Transcript">A 判定的逐字稿狀態（<see cref="TranscriptStates.MetaValues"/>；正常是 null）</param>
/// <param name="CliVersion">A 最後執行這個對話的 CLI 版本（<c>LastCliVersion</c>）</param>
internal sealed record HandoffReady(
    string HandoffId,
    long FinalDeviceSeq,
    IReadOnlyList<LogHead> Logs,
    string? Transcript = null,
    HandoffWorkspace? Workspace = null,
    HandoffInterruption? Interruption = null,
    IReadOnlyList<HandoffQueueEntry>? Queue = null,
    HandoffSkipped? Skipped = null,
    string? CliVersion = null);

/// <summary><see cref="HandoffProgress"/>、<see cref="HandoffReady"/> 的回應（計畫 5.5 的 <c>{ok, error}</c>）</summary>
internal sealed record HandoffAck(bool Ok, string? Error = null);

/// <summary>
/// 伺服器 → 發起者、A、B（Send，hand1）：交接的狀態（<see cref="State"/> 是 <see cref="HandoffStates"/>，committed 之後 <see cref="ImportState"/> 是接手端的進度）。
/// <see cref="Detail"/> 是給人看的說明（例如 A 的 git 摘要），不可信字串
/// </summary>
internal sealed record HandoffUpdate(
    string HandoffId,
    string ConversationId,
    string Kind,
    string State,
    string FromDeviceId,
    string ToDeviceId,
    string? Mode = null,
    long? DeadlineAt = null,
    string? Error = null,
    string? Detail = null,
    string? ImportState = null);

/// <summary>伺服器 → 擁有者（Send，log1）：這個日誌在伺服器上損毀（<see cref="Reason"/> 是 <see cref="LogRepairReasons"/>），整檔以新世代重傳</summary>
internal sealed record LogRepair(string LogKey, string LogId, string Reason);
