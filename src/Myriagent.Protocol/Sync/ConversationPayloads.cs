using Myriagent.Agents;

namespace Myriagent.Sync;

// 第 4 階段對話事件的 payload（設計 14.11 D30～D34；docs/sync-protocol.md 14.2）。線上一律 camelCase、null 欄位省略（SyncJson），
// 時間是 Unix 毫秒（只供顯示，不參與排序：排序只看伺服器 seq 與對話內的 ord）。項目不是桌面的存檔格式（ProtocolJson 的 AgentChatItem），
// 而是同步專用的投影（WireItem，由 ConversationWire 產生）：不送程序實例、請求鍵、本機路徑與網頁的 seq（D32）。
// 所有字串在投影之前先經權杖 DLP 遮蔽（CredentialLeakRules.MaskItem，D43，WP4.1 步 5）。

/// <summary>
/// <c>conv.created</c> 與 <c>conv.meta</c>（meta 類）：對話完整的中繼資料，兩種事件都帶整份（不是只帶改了的）——outbox 合併
/// 同一個對話待送的 conv.meta 時新內容原地取代，才不會掉欄位；null 一律表示「預設」，不是「沒改」。
/// </summary>
/// <param name="Provider">線上的供應商名稱（<c>Claude</c>／<c>Codex</c>／<c>Grok</c>／<c>Antigravity</c>；後兩種只在協商出 prov1 的連線上有，不進同步的對話沒有對話事件）</param>
/// <param name="LocalAccountId">執行這個對話的本機帳戶 id（伺服器以 link.upsert 的對應找邏輯帳戶）；帳戶不明（meta.json 重建）時 null</param>
/// <param name="Title">標題（≤ <see cref="ConversationEventRules.MaxTitleLength"/> 字）</param>
/// <param name="PermissionMode">權限模式的名稱（<see cref="AgentPermissionMode"/>）；存檔裡是沒定義的數值時 null（不明）</param>
/// <param name="Model">null＝帳戶預設</param>
/// <param name="Effort">null＝帳戶預設</param>
/// <param name="CustomFolder">false＝對話自己的 workspace（不送路徑）</param>
/// <param name="WorkingDirectory">自訂工作資料夾的完整路徑（只供顯示）；預設 workspace、或路徑超過 <see cref="ConversationEventRules.MaxPathLength"/> 字時 null</param>
/// <param name="SyncPolicy">解讀後的同步政策（<see cref="ConversationSyncPolicies"/>；localOnly 不發布，所以不會出現）</param>
/// <param name="Running">執行中</param>
/// <param name="Waiting">有等待使用者回應的卡片（未讀不送）</param>
/// <param name="Models">
/// 這個對話的帳戶在擁有者電腦上可選的模型（CLI 回報的清單，沒回報過是內建的預設選單；2026-10-23 起）：其他電腦與瀏覽器版改這個對話的模型時照它列選單。
/// 不一定有「預設」（讀的一方自己補在最前面）；舊版擁有者不送（省略＝不明，讀的一方照舊）
/// </param>
/// <param name="Transcript">
/// 第 5 階段（選用、在最後；docs/sync-protocol.md 第 16 節）：逐字稿的狀態（<see cref="TranscriptStates.MetaValues"/>：missing、held、unportable）；
/// 省略＝正常。伺服器寫進 <c>conversations.transcript</c>，換電腦時這幾種一律前文節錄。WP5.3 才傳值
/// </param>
/// <param name="Ephemeral">
/// 臨時對話（2026-11-11，桌面的 <c>AgentConversation.Ephemeral</c>；瀏覽器版開的）：伺服器與其他電腦的清單都不列，關掉就刪掉。
/// 選用、在最後；只有 true 時帶（省略＝一般對話），前一版忽略不認得的欄位
/// </param>
/// <param name="WorkingDirectoryRef">
/// 第 5 階段（選用、在最後）：自訂工作資料夾的跨電腦參照（<see cref="PathRef"/>，<c>{kind, repoKey?, sub?, display?}</c>）；只有 <see cref="CustomFolder"/> 是 true 時帶。
/// WP5.4 才傳值；前一版伺服器忽略不認得的欄位
/// </param>
internal sealed record ConversationMetaPayload(
    string Provider,
    string? LocalAccountId,
    string Title,
    string? PermissionMode,
    string? Model,
    string? Effort,
    bool CustomFolder,
    string? WorkingDirectory,
    string SyncPolicy,
    long? CreatedAt,
    long? UpdatedAt,
    bool Running,
    bool Waiting,
    IReadOnlyList<ConversationModelOption>? Models = null,
    string? Transcript = null,
    PathRef? WorkingDirectoryRef = null,
    bool? Ephemeral = null);

/// <summary>
/// <see cref="ConversationMetaPayload.Models"/> 的一項（同桌面 <see cref="AgentModelOption"/>）：<paramref name="Value"/> 是 setOption 的值，
/// <paramref name="Efforts"/> 是這個模型支援的推理強度（沒有時省略）
/// </summary>
internal sealed record ConversationModelOption(string Value, string Label, string? Description, IReadOnlyList<string>? Efforts);

/// <summary><c>conv.turn</c> 的 phase</summary>
internal static class ConversationTurnPhases
{
    public const string Started = "started";

    public const string Completed = "completed";
}

/// <summary><c>conv.turn</c> 結束時的 outcome（<see cref="AgentTurnOutcome"/> 的小寫）</summary>
internal static class ConversationTurnOutcomes
{
    public const string Completed = "completed";

    public const string Interrupted = "interrupted";

    public const string Failed = "failed";

    public static bool IsKnown(string? outcome) => outcome is Completed or Interrupted or Failed;

    public static string From(AgentTurnOutcome outcome) => outcome switch
    {
        AgentTurnOutcome.Completed => Completed,
        AgentTurnOutcome.Interrupted => Interrupted,
        AgentTurnOutcome.Failed => Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };
}

/// <summary>一輪的 token 數（只供顯示；<see cref="AgentUsage"/> 的線上格式，欄位名稱與桌面模型分開維護）</summary>
internal sealed record ConversationTurnUsage(long InputTokens, long OutputTokens, long CachedInputTokens, double? CostUsd = null);

/// <summary><c>conv.turn</c>（meta 類）：started 不帶其他欄位；completed 帶 outcome，有用量時帶 usage</summary>
internal sealed record ConversationTurnPayload(string Phase, string? Outcome = null, ConversationTurnUsage? Usage = null);

/// <summary><c>conv.attention</c> 的 kind</summary>
internal static class ConversationAttentionKinds
{
    public const string Question = "question";

    public const string Approval = "approval";

    public const string Plan = "plan";

    public const string Completed = "completed";

    public const string Failed = "failed";

    public static bool IsKnown(string? kind) => kind is Question or Approval or Plan or Completed or Failed;
}

/// <summary>
/// <c>conv.attention</c>（meta 類）：要使用者注意的事；卡片類帶那張卡的項目 id。
/// <paramref name="Speech"/>＝語音通知念的句子（VoiceNotifyRules，≤ <see cref="ConversationEventRules.MaxSpeechLength"/> 字；sync-protocol 14.2）：
/// 對話是完整同步才帶簡要說明，否則只有「標題＋發生什麼事」；空字串＝這件事不念（背景工作還在跑時的完成）；不帶（舊版桌面）時伺服器自己組只有標題的句子
/// </summary>
internal sealed record ConversationAttentionPayload(string Kind, string? ItemId = null, string? Speech = null);

/// <summary><c>conv.deleted</c>（meta 類）：線上是 <c>{}</c>（遠端刪除是移到回收區，D10，也是這一筆）</summary>
internal sealed record ConversationDeletedPayload;

/// <summary><c>conv.itemRemoved</c>（item 類）</summary>
internal sealed record ConversationItemRemovedPayload(string Id);

/// <summary>佇列裡的一則：只有 id 與給人看的文字（秘密回答已遮蔽，D20；≤ <see cref="ConversationEventRules.MaxQueueTextLength"/> 字）</summary>
internal sealed record ConversationQueueEntry(string Id, string DisplayText);

/// <summary><c>conv.queue</c>（item 類）：整份佇列的快照；超過 <see cref="ConversationEventRules.MaxQueueEntries"/> 則或放不下時只帶前面的，<see cref="More"/> 是沒帶的則數</summary>
internal sealed record ConversationQueuePayload(IReadOnlyList<ConversationQueueEntry> Entries, int More = 0);

/// <summary>
/// <c>conv.lease</c>（meta 類；第 5 階段計畫 5.2、docs/sync-protocol.md 第 16 節）：擁有者換手。只由伺服器寫（作者 <see cref="SyncAuthors.Server"/>、
/// 在交接服務的提交交易裡，D2、D3），每次 epoch＋1；裝置送它一律 invalid。<see cref="Logs"/> 是接手端要下載的完整集合（線上交接取 A 回報的值、
/// 強制接手取伺服器驗證過的值）。形狀由 <see cref="ConversationEventRules.Validate(string?, string?)"/> 檢查
/// </summary>
/// <param name="Owner">新的擁有者 deviceId</param>
/// <param name="Epoch">新的 epoch（＝<see cref="PrevEpoch"/>＋1）</param>
/// <param name="Reason"><see cref="LeaseReasons"/></param>
/// <param name="HandoffId">交接的 id（ULID）；restore 沒有</param>
/// <param name="Mode"><see cref="HandoffModes"/>；takeover、restore 沒有</param>
/// <param name="Initiator">發起者；restore 沒有</param>
/// <param name="FromName">提交當下原擁有者的名稱（<c>devices.name</c>；換機說明與 Notice 寫兩台的名稱，14.2）</param>
/// <param name="ToName">提交當下新擁有者的名稱</param>
/// <param name="LogicalAccountId">這個對話的邏輯帳戶（接手端以它找本機帳戶，D21）</param>
/// <param name="Transcript">逐字稿的狀態（<see cref="TranscriptStates.LeaseValues"/>；正常是 null）：不是 null 時接手端以前文節錄續接</param>
/// <param name="Workspace">A 回報的工作資料夾 git 狀態；沒有是 null</param>
/// <param name="Interruption">now 才有：中斷了什麼</param>
/// <param name="Queue">隨交接帶走的佇列（計畫第 9 節；強制接手沒有，接手端以伺服器最後的 <c>conv.queue</c> 還原）</param>
/// <param name="Skipped">沒有搬的東西</param>
/// <param name="Fence">takeover 才有：圍欄</param>
/// <param name="CliVersion">原擁有者最後執行這個對話的 CLI 版本</param>
internal sealed record ConversationLeasePayload(
    string Owner,
    long Epoch,
    string PrevOwner,
    long PrevEpoch,
    string Reason,
    string? HandoffId = null,
    string? Mode = null,
    bool AutoContinue = false,
    HandoffInitiator? Initiator = null,
    string? FromName = null,
    string? ToName = null,
    string? LogicalAccountId = null,
    IReadOnlyList<LogHead>? Logs = null,
    string? Transcript = null,
    HandoffWorkspace? Workspace = null,
    HandoffInterruption? Interruption = null,
    IReadOnlyList<HandoffQueueEntry>? Queue = null,
    HandoffSkipped? Skipped = null,
    HandoffFence? Fence = null,
    string? CliVersion = null);

/// <summary>
/// blob 參照（D31、D34）：太大的欄位改放 blob（<c>/api/v1/device/blobs</c>，WP4.7），payload 的那個欄位換成 <c>{blob, length}</c>。
/// 內容是 UTF-8：字串欄位是字串本身，陣列欄位是那個陣列的 JSON（<see cref="ConversationEventRules.TryFit"/>）
/// </summary>
/// <param name="Blob">內容的 SHA-256（64 個小寫十六進位）</param>
/// <param name="Length">內容的位元組數</param>
internal sealed record ConversationBlobRef(string Blob, long Length);

/// <summary>要另外上傳的 blob 內容（<see cref="ConversationEventRules.TryFit"/> 換出來的）</summary>
internal sealed record ConversationBlob(string Sha256, byte[] Content);

/// <summary>附件的線上投影：沒有 StoredPath、ModelImagePaths（D32）；檔案本身經裝置發的 <see cref="Ref"/> 取（14.3，WP4.7）</summary>
/// <param name="Kind">Image／Video／Document（沒定義的數值當成 Document）</param>
/// <param name="Size">位元組數</param>
/// <param name="Frames">影片給模型看的畫面張數（網頁顯示「N 張畫面」）；不是影片時 null</param>
/// <param name="Duration">影片長度（秒）</param>
/// <param name="Ref">這台發的媒體參照；還沒發時 null</param>
internal sealed record WireAttachment(string Id, string Name, string Kind, long Size, int? Frames, double? Duration, string? Ref);

/// <summary>代理產出的媒體（圖片、影片）的線上投影：沒有本機路徑，只有這台發的參照、種類與檔名</summary>
/// <param name="Ref">這台發的媒體參照；還沒發時 null</param>
/// <param name="Kind"><c>image</c>／<c>video</c>（同網頁 Item 的 media[].kind）</param>
internal sealed record WireMedia(string? Ref, string Kind, string Name);

internal sealed record WireQuestionOption(string Label, string? Description, string? Preview);

/// <summary>問卷的一道題（Claude 的 Id 就是題目文字）</summary>
internal sealed record WireQuestion(string Id, string? Header, string Text, bool MultiSelect, bool AllowOther, bool IsSecret, IReadOnlyList<WireQuestionOption> Options);

/// <summary>一道題的答案（線上是陣列、不是以題目 id 為鍵的物件：每個字串都在值的位置，太大時能整欄換成 blob）；秘密題目的答案一律是「（已隱藏）」</summary>
internal sealed record WireAnswer(string Id, IReadOnlyList<string> Values);

internal sealed record WirePlanStep(string Text, string Status);

/// <summary>
/// <c>conv.item</c>（item 類）的 payload：一筆項目的同步投影（D31、D32）。<see cref="Ord"/> 是擁有者在這個對話內配發的順序（OrdRules，
/// 同步只認它）、<see cref="Ver"/> 是擁有者對這筆的更新次數（即時更新與持久事件比新舊用，14.3）。
/// 列舉欄位是名稱（Kind／Status 同網頁 Item）；空字串與空清單省略（讀回時是空的）。
/// 不在這裡的欄位：ProcessInstance、RequestKey、附件的 StoredPath 與 ModelImagePaths、MediaPaths、MediaRefs 的本機欄位、Seq（D32）。
/// <see cref="Author"/>、<see cref="ResolvedBy"/>（2026-10-30，多人協作）是協作人的顯示名稱（送訊息的人、回應卡片的人），擁有者的省略
/// </summary>
internal sealed record WireItem(
    string Id,
    long Ord,
    long Ver,
    string Kind,
    string Status,
    long? CreatedAt,
    long? UpdatedAt,
    string? Text = null,
    string? ToolName = null,
    string? ToolKind = null,
    string? Title = null,
    string? Detail = null,
    string? Output = null,
    IReadOnlyList<AgentDiffLine>? Diff = null,
    string? FilePath = null,
    IReadOnlyList<WireAttachment>? Attachments = null,
    IReadOnlyList<WireQuestion>? Questions = null,
    IReadOnlyList<WireAnswer>? Answers = null,
    string? Decision = null,
    bool CanAllowForSession = true,
    string? AllowSessionLabel = null,
    bool AnswerAsMessage = false,
    IReadOnlyList<WireMedia>? Media = null,
    IReadOnlyList<WirePlanStep>? Plan = null,
    string? NoticeKind = null,
    string? RequestDigest = null,
    string? Risk = null,
    string? Author = null,
    string? ResolvedBy = null);
