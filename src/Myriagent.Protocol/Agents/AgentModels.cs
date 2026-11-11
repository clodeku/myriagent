using Myriagent.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Myriagent.Agents;

/// <summary>
/// 代理的種類：本機 Claude Code CLI、Codex CLI、Antigravity CLI（agy）或 Grok Build CLI（grok）（都走登入帳戶的訂閱額度）。
/// 以字串存檔（accounts.json、對話 meta.json、設定的對話預設值）；依種類分派的程式一律用 switch、遇到未知值丟例外，不用二分的條件式
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
internal enum AgentProvider
{
    Claude,
    Codex,
    Antigravity,
    Grok,
}

/// <summary>代理種類的清單（依畫面上的排列順序）：要逐一處理每一種時用它，不要自己列</summary>
internal static class AgentProviders
{
    public static IReadOnlyList<AgentProvider> All { get; } = new[] { AgentProvider.Claude, AgentProvider.Codex, AgentProvider.Antigravity, AgentProvider.Grok };

    /// <summary>給使用者看的 CLI 產品名稱（後面常接「CLI」：「Claude Code CLI」「Antigravity CLI」）</summary>
    public static string ProductName(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => "Claude Code",
        AgentProvider.Codex => "Codex",
        AgentProvider.Antigravity => "Antigravity",
        AgentProvider.Grok => "Grok Build",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };
}

/// <summary>
/// 代理執行指令、改檔案前要不要先問使用者（每個對話可隨時切換，下一輪生效）。
/// Claude 五種都支援；Codex 沒有「自動接受編輯」與「規劃模式」，分別視同「自動」與「每步詢問」；
/// Antigravity 不能在對話中詢問（stream-json 沒有核准協定），「每步詢問」視同「自動接受編輯」（見 AgentPermissionModes）；
/// Grok 五種都支援（「自動接受編輯」由會話代為核准工作資料夾內的編輯，見 GrokAcpRules）。
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
internal enum AgentPermissionMode
{
    /// <summary>每步詢問：執行指令、修改檔案都先跳核准卡</summary>
    Ask,

    /// <summary>
    /// 自動接受編輯：工作資料夾內改檔案、mkdir／rm／mv 等檔案指令不問，其他指令仍會問（Claude acceptEdits）；
    /// 工作資料夾外的路徑與 .claude、.git 等敏感檔 CLI 仍會詢問
    /// </summary>
    AcceptEdits,

    /// <summary>規劃模式：只讀、先提出計畫，確認後才動手（Claude plan）</summary>
    Plan,

    /// <summary>自動：由代理自己的安全判斷放行，高風險操作才問</summary>
    Auto,

    /// <summary>完全自動：一律放行，不再詢問</summary>
    FullAccess,
}

/// <summary>對話內容的一列（畫面上一個區塊）</summary>
[JsonConverter(typeof(StringEnumConverter))]
internal enum AgentItemKind
{
    User,
    Assistant,
    Thinking,
    Tool,
    Question,
    Approval,

    /// <summary>Claude 規劃模式結束時請使用者確認計畫（ExitPlanMode）</summary>
    PlanApproval,

    /// <summary>待辦清單（Claude TodoWrite 或 TaskCreate／TaskUpdate、Codex turn/plan）</summary>
    Plan,
    Notice,
    Error,
}

/// <summary>
/// Notice 的種類（網頁依此加圖示）；一般提示不設（null）。
/// 存進 items.json：之後新增的值，U1 以後的舊版讀成 null（AgentChatItem.NoticeKind 掛 UnknownEnumNameAsNullConverter），比 U1 舊的建置讀不懂
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
internal enum AgentNoticeKind
{
    /// <summary>CLI 換版：已用新版自動接續的說明、沒有自動接續的原因、背景工作要不要現在重開的提示</summary>
    CliUpdate,

    /// <summary>app 自我更新：新版接續了哪些對話、沒有接續的原因（設計 9.5）</summary>
    AppUpdate,

    /// <summary>
    /// /clear 清除了對話：代理之後看不到這則之前的內容。畫面把這則之前的項目藏起來（可再展開），前文節錄也只收這則之後的
    /// （AgentHubRules.AfterLastClear）；項目本身不刪（同步照常，回滾後的舊版當成一般提示）
    /// </summary>
    Clear,

    /// <summary>額度用完時自動換帳戶繼續：哪個帳戶用完、改用哪個帳戶接續（AccountSwitchRules；緊接著的一輪是隱藏送出的接續訊息）</summary>
    AccountSwitch,

    /// <summary>第 5 階段：這個對話從別台電腦交接或強制接手過來（「已從 OFFICE-PC 交接到 HOME-PC」；WP5.7 寫）</summary>
    Handoff = 4,

    // 數值 5 刻意不用：ItemRenderer 黃金語料的 undefined-enums 以 (AgentNoticeKind)5 當成「沒定義的值」（存檔與線上一律是名稱，數值不影響相容）

    /// <summary>第 5 階段：離線期間被別台強制接手、這台的新內容另存成的分支（「離線期間在 OFFICE-PC 的 N 輪另存成這個分支」；WP5.8 寫）</summary>
    Fork = 6,

    /// <summary>
    /// Codex 的背景指令跑完了：代理接著處理（緊接著的一輪是隱藏送出的接續訊息），或說明為什麼這次沒有自動接續（BackgroundWorkRules）。
    /// 舊版讀到這個名稱當成一般提示（UnknownEnumNameAsNullConverter）
    /// </summary>
    BackgroundTask = 7,

    /// <summary>代理另開、脫離 CLI 的程式自己結束了（「代理另開的程式「…」已結束（結束碼 1，執行了 12 分鐘）」；BackgroundProgramRules.ExitNotice）</summary>
    BackgroundProgram = 8,

    /// <summary>
    /// API 過載，排定了自動重試（「60 秒後自動重試」；OverloadRetryRules.ScheduledNotice）：網頁從這則的時間起算倒數，倒數完開始重試時閃一下，
    /// 提早開始別的一輪（重試取消）時倒數條淡掉。舊版讀到這個名稱當成一般提示（UnknownEnumNameAsNullConverter）
    /// </summary>
    OverloadRetry = 9,

    /// <summary>
    /// 旁支提問（/btw，Claude 的 side_question）：問題與答案，不在對話的上下文裡（代理看不到）。舊版讀到這個名稱當成一般提示（UnknownEnumNameAsNullConverter）
    /// </summary>
    SideQuestion = 10,
}

/// <summary>
/// 核准卡從遠端回應時的風險（設計 8.10、14.11 D9）：由擁有者電腦分類（WP4.4 的 ClassifyRemoteRisk），只影響遠端的層級，本機核准不受影響。
/// 存進 items.json：屬性掛 UnknownEnumNameAsNullConverter，之後新增的值舊版讀成 null；null（沒分類）時電腦端當成非一般（allow 是層級 2）
/// </summary>
[JsonConverter(typeof(StringEnumConverter))]
internal enum AgentRemoteRisk
{
    Normal,
    High,
}

/// <summary>使用者訊息不是在這個 app 打的（網頁依此標示來源）；在 app 送出的不設（null）</summary>
[JsonConverter(typeof(StringEnumConverter))]
internal enum AgentItemSource
{
    /// <summary>從 claude.ai 或 Claude App 送來（Claude 的 Remote Control）</summary>
    Remote,
}

[JsonConverter(typeof(StringEnumConverter))]
internal enum AgentItemStatus
{
    Running,
    Waiting,
    Done,
    Failed,
    Declined,
    Cancelled,
}

[JsonConverter(typeof(StringEnumConverter))]
internal enum AgentAttachmentKind
{
    Image,
    Video,
    Document,
}

/// <summary>使用者附加的檔案：原檔複製到對話資料夾；圖片／影片另備給模型看的圖（縮圖或影片畫面）</summary>
internal sealed class AgentAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string FileName { get; set; } = string.Empty;

    /// <summary>複製後在對話資料夾內的完整路徑（代理以此路徑讀檔）</summary>
    public string StoredPath { get; set; } = string.Empty;

    public AgentAttachmentKind Kind { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>直接附給模型看的圖片路徑：圖片本身或其縮圖、影片擷取的畫面；文件為空</summary>
    public List<string> ModelImagePaths { get; set; } = new();

    /// <summary>影片：每張畫面對應的秒數（與 ModelImagePaths 同順序）</summary>
    public List<double> FrameSeconds { get; set; } = new();

    public double? DurationSeconds { get; set; }

    /// <summary>
    /// 語音訊息的錄音波形（VoiceMessageRules.NormalizeWaveform：一格一個 0～9 的數字，錄音時麥克風實際的音量）；只有桌面這台錄的才有，
    /// 不經同步（ConversationWire 不帶，瀏覽器版與其他電腦看不到）。新欄位：前一版讀 items.json 時忽略
    /// </summary>
    public string? Waveform { get; set; }

    /// <summary>存檔快照（背景執行緒序列化用，ItemsFileWriter）：清單另外複製，之後 UI 執行緒改原本的不影響快照</summary>
    internal AgentAttachment CloneForSave()
    {
        var copy = (AgentAttachment)MemberwiseClone();
        copy.ModelImagePaths = ModelImagePaths is null ? null! : new List<string>(ModelImagePaths);
        copy.FrameSeconds = FrameSeconds is null ? null! : new List<double>(FrameSeconds);
        return copy;
    }
}

internal sealed class AgentQuestionOption
{
    public string Label { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Preview { get; set; }

    /// <summary>存檔快照（同 AgentAttachment.CloneForSave）</summary>
    internal AgentQuestionOption CloneForSave() => (AgentQuestionOption)MemberwiseClone();
}

/// <summary>代理提出的一道題目</summary>
internal sealed class AgentQuestion
{
    /// <summary>回答時的鍵：Claude 是題目文字本身、Codex 是題目 id</summary>
    public string Id { get; set; } = string.Empty;

    public string? Header { get; set; }

    public string Text { get; set; } = string.Empty;

    public List<AgentQuestionOption> Options { get; set; } = new();

    public bool MultiSelect { get; set; }

    public bool AllowOther { get; set; } = true;

    public bool IsSecret { get; set; }

    /// <summary>存檔快照（同 AgentAttachment.CloneForSave）</summary>
    internal AgentQuestion CloneForSave()
    {
        var copy = (AgentQuestion)MemberwiseClone();
        copy.Options = Options?.Select(o => o?.CloneForSave()!).ToList()!;
        return copy;
    }
}

/// <summary>待辦清單的一個步驟</summary>
internal sealed class AgentPlanStep
{
    public string Text { get; set; } = string.Empty;

    /// <summary>pending／inProgress／completed</summary>
    public string Status { get; set; } = "pending";

    /// <summary>存檔快照（同 AgentAttachment.CloneForSave）</summary>
    internal AgentPlanStep CloneForSave() => (AgentPlanStep)MemberwiseClone();
}

/// <summary>差異的一行：t＝add／del／ctx／hunk</summary>
internal sealed record AgentDiffLine(string T, string S);

/// <summary>
/// 這台電腦發給附件或代理產出媒體的參照（設計 14.11 D14；docs/sync-protocol.md 14.3）：同步與網頁只看 <see cref="Ref"/>，
/// 本機路徑不離開電腦（伺服器只存 device_id＋ref）。<see cref="AttachmentId"/> 與 <see cref="MediaPath"/> 恰有一個，
/// 指向同一筆項目的附件（<see cref="AgentAttachment.Id"/>）或 <see cref="AgentChatItem.MediaPaths"/> 的一項；這兩欄不上線。
/// 正本在同步資料庫的 <c>media_refs_local</c>（WP4.7 計畫 D4 的契約修正）：發布時投影器才把參照填進項目的複本，items.json 不存（中樞的項目沒有它）
/// </summary>
internal sealed class AgentMediaRef
{
    /// <summary>26 個 Crockford Base32 字的亂數（由這台產生，WP4.3）</summary>
    public string Ref { get; set; } = string.Empty;

    public string? AttachmentId { get; set; }

    public string? MediaPath { get; set; }

    /// <summary>存檔快照（同 AgentAttachment.CloneForSave）</summary>
    internal AgentMediaRef CloneForSave() => (AgentMediaRef)MemberwiseClone();
}

/// <summary>對話內容的一列；存進 items.json，也是畫面渲染的來源</summary>
internal sealed class AgentChatItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public AgentItemKind Kind { get; set; }

    public AgentItemStatus Status { get; set; } = AgentItemStatus.Done;

    /// <summary>使用者訊息原文、代理回覆／思考的 Markdown、提示與錯誤文字</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>工具的顯示名稱（執行指令、讀取檔案…）</summary>
    public string? ToolName { get; set; }

    /// <summary>工具種類（決定圖示）：bash、read、write、edit、search、web、agent、mcp、todo、image、plan、other</summary>
    public string? ToolKind { get; set; }

    public string? Title { get; set; }

    public string? Detail { get; set; }

    public string? Output { get; set; }

    /// <summary>編輯類工具的行差異</summary>
    public List<AgentDiffLine>? Diff { get; set; }

    /// <summary>讀寫的檔案完整路徑</summary>
    public string? FilePath { get; set; }

    public List<AgentAttachment> Attachments { get; set; } = new();

    public List<AgentQuestion> Questions { get; set; } = new();

    public Dictionary<string, List<string>> Answers { get; set; } = new();

    /// <summary>核准卡：allow／allowSession／deny；問卷：answered／skip；計畫確認：acceptEdits／manual／keepPlanning</summary>
    public string? Decision { get; set; }

    public bool CanAllowForSession { get; set; } = true;

    /// <summary>「本對話都允許」按鈕的文字（說明放行範圍）；null＝預設文字</summary>
    public string? AllowSessionLabel { get; set; }

    /// <summary>問卷的答案改以下一則訊息送出（Codex 非同步提問）</summary>
    public bool AnswerAsMessage { get; set; }

    /// <summary>代理產出、要在畫面上直接顯示的圖片／影片（本機完整路徑）</summary>
    public List<string> MediaPaths { get; set; } = new();

    public List<AgentPlanStep> PlanSteps { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 產生這張卡（Approval／Question／PlanApproval）的 CLI 程序實例（AgentProcessStartedEvent 的 ProcessInstance）：
    /// 那個程序結束或換了新程序，這張卡就失效——絕不能把舊卡的回覆送給新程序。null＝不明（舊資料、會話沒有回報實例），不做比對
    /// </summary>
    public string? ProcessInstance { get; set; }

    /// <summary>
    /// Notice 的種類；null＝一般提示。讀到不認得的名稱（之後的版本新增的種類）當成 null，不讓整份 items.json 被當成損毀（設計 8.8）
    /// </summary>
    [JsonConverter(typeof(UnknownEnumNameAsNullConverter))]
    public AgentNoticeKind? NoticeKind { get; set; }

    /// <summary>使用者訊息的來源；null＝在這個 app 送出。不認得的名稱（之後的版本新增的來源）讀成 null（設計 8.8）</summary>
    [JsonConverter(typeof(UnknownEnumNameAsNullConverter))]
    public AgentItemSource? Source { get; set; }

    /// <summary>
    /// 使用者訊息是哪位協作人送的（協作人的顯示名稱，docs/plans/2026-10-30-collaborators.md；遠端指令信封的 <c>issuer.kind</c> 是
    /// <c>collaborator</c> 時取它的 label）。null＝擁有者送的（這台、擁有者的瀏覽器、其他電腦）。隨 <c>conv.item</c> 同步，網頁標出是誰送的
    /// </summary>
    public string? Author { get; set; }

    /// <summary>卡片（Approval／Question／PlanApproval）是哪位協作人回應的（顯示名稱，同 <see cref="Author"/>）；null＝擁有者或還沒回應</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>
    /// 使用者訊息寫給 CLI 時的 uuid（Claude，2026-10-25）：回溯（rewind_files 的 user_message_id、逐字稿裡那則之前的 assistant）與取消插入以它指名那則。
    /// null＝沒有（其他代理、隱藏送出、舊項目）。只在這台用：不上線、ItemRenderer 不輸出
    /// </summary>
    public string? CliUuid { get; set; }

    // 第 4 階段的同步與遠端欄位（WP4.1 計畫 5.2）：全部可為 null、null 時不寫進 items.json（舊檔照讀、存回逐位元組不變），
    // ItemRenderer 不輸出它們（網頁的 Item 與黃金檔不變）

    /// <summary>
    /// 這個對話內由擁有者電腦配發、嚴格遞增的順序（OrdRules；同步只認它，網頁的 <see cref="Seq"/> 不變）。null＝還沒配（舊項目、WP4.3 之前）
    /// </summary>
    public long? Ord { get; set; }

    /// <summary>卡片（Approval／Question／PlanApproval）的摘要 rd1（RequestDigestRules，docs/sync-protocol.md 14.5）：遠端回應時比對；null＝沒有</summary>
    public string? RequestDigest { get; set; }

    /// <summary>核准卡的遠端風險；null＝沒分類（電腦端當成非一般）。不認得的名稱讀成 null（不讓整份 items.json 被當成損毀）</summary>
    [JsonConverter(typeof(UnknownEnumNameAsNullConverter))]
    public AgentRemoteRisk? Risk { get; set; }

    /// <summary>
    /// 這台發給附件與代理產出媒體的參照（<see cref="AgentMediaRef"/>）；null＝沒有。擁有的項目只在發布的投影裡有（正本在 <c>media_refs_local</c>，WP4.7 D4），
    /// 複本的項目由線上的 <c>ref</c> 讀回
    /// </summary>
    public List<AgentMediaRef>? MediaRefs { get; set; }

    /// <summary>
    /// 桌面替這則回覆留的媒體副本（2026-10-25）：鍵是原檔正規化後的完整路徑（<see cref="MediaPaths"/> 或回覆 Markdown 引用的本機圖片），
    /// 值是副本相對於對話資料夾的路徑（<c>attachments\.kept\{雜湊}\{原檔名}</c>）。畫面優先顯示副本，原檔被刪或被覆寫也照樣看得到當時那一張。
    /// null＝沒有（不寫進 items.json）；只在這台電腦有意義，不上線、ItemRenderer 不輸出
    /// </summary>
    public Dictionary<string, string>? KeptMedia { get; set; }

    /// <summary>等待使用者回應的請求鍵；只在程序存活期間有效，不存檔</summary>
    [JsonIgnore]
    public string? RequestKey { get; set; }

    /// <summary>
    /// 在對話裡的建立順序（只增不減，不受系統時間調整影響）：網頁靠它分辨新項目與還沒載入的較早項目。
    /// 載入時依存檔順序重新編號，只在程序存活期間有效，不存檔
    /// </summary>
    [JsonIgnore]
    public long Seq { get; set; }

    /// <summary>
    /// 存檔快照：在 UI 執行緒取（便宜：欄位整份複製，字串本來就不可變），交給背景執行緒序列化寫檔（ItemsFileWriter）。
    /// 可變的欄位（清單、字典、巢狀物件）另外複製，之後 UI 執行緒繼續改原本的項目不影響快照；AgentDiffLine 是不可變的 record，元素共用。
    /// 新增可變欄位時要加在這裡（ItemSnapshotTests 以反射擋下沒處理的）
    /// </summary>
    internal AgentChatItem CloneForSave()
    {
        var copy = (AgentChatItem)MemberwiseClone();
        copy.Diff = Diff is null ? null : new List<AgentDiffLine>(Diff);
        copy.Attachments = Attachments?.Select(a => a?.CloneForSave()!).ToList()!;
        copy.Questions = Questions?.Select(q => q?.CloneForSave()!).ToList()!;
        copy.Answers = Answers is null ? null! : CopyAnswers(Answers);
        copy.MediaPaths = MediaPaths is null ? null! : new List<string>(MediaPaths);
        copy.PlanSteps = PlanSteps?.Select(s => s?.CloneForSave()!).ToList()!;
        copy.MediaRefs = MediaRefs?.Select(m => m?.CloneForSave()!).ToList();
        copy.KeptMedia = KeptMedia is null ? null : new Dictionary<string, string>(KeptMedia, KeptMedia.Comparer);
        return copy;
    }

    /// <summary>
    /// 之前取的存檔快照（<see cref="CloneForSave"/>）能不能沿用（ItemsSaveCache：沒變的項目不再複製、存檔時也不再序列化）：
    /// 寫進 items.json 的每個欄位都和這個項目相同才算。字串、數值、時間（含 Kind）逐一比；差異、媒體路徑、媒體副本逐筆照列舉順序比
    /// （序列化的順序）；附件、問卷、答案、計畫步驟、媒體參照是巢狀的可變物件，不逐層比——有內容就當成不同（重新複製；這幾種項目很少），
    /// 兩邊都沒有內容（都是 null、或都是空的）才算相同（null 不寫、空的寫成 []，兩者不同）。
    /// 新增存檔的欄位時要加在這裡（ItemsSaveCacheTests 以反射逐欄改動、擋下沒比到的）
    /// </summary>
    internal bool SameForSave(AgentChatItem snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Id == snapshot.Id && Kind == snapshot.Kind && Status == snapshot.Status
            && Text == snapshot.Text && ToolName == snapshot.ToolName && ToolKind == snapshot.ToolKind
            && Title == snapshot.Title && Detail == snapshot.Detail && Output == snapshot.Output
            && SameList(Diff, snapshot.Diff) && FilePath == snapshot.FilePath
            && BothEmpty(Attachments, snapshot.Attachments) && BothEmpty(Questions, snapshot.Questions) && BothEmpty(Answers, snapshot.Answers)
            && Decision == snapshot.Decision && CanAllowForSession == snapshot.CanAllowForSession && AllowSessionLabel == snapshot.AllowSessionLabel
            && AnswerAsMessage == snapshot.AnswerAsMessage && SameList(MediaPaths, snapshot.MediaPaths) && BothEmpty(PlanSteps, snapshot.PlanSteps)
            && SameTime(CreatedAt, snapshot.CreatedAt) && SameTime(UpdatedAt, snapshot.UpdatedAt)
            && ProcessInstance == snapshot.ProcessInstance && NoticeKind == snapshot.NoticeKind && Source == snapshot.Source
            && Author == snapshot.Author && ResolvedBy == snapshot.ResolvedBy && CliUuid == snapshot.CliUuid
            && Ord == snapshot.Ord && RequestDigest == snapshot.RequestDigest && Risk == snapshot.Risk
            && BothEmpty(MediaRefs, snapshot.MediaRefs) && SameMap(KeptMedia, snapshot.KeptMedia);
    }

    /// <summary>時間：DateTime 的相等只比 Ticks，存檔還看 Kind（轉本機時區）</summary>
    private static bool SameTime(DateTime a, DateTime b) => a.Ticks == b.Ticks && a.Kind == b.Kind;

    /// <summary>都是 null，或都不是 null 而且逐筆相同（AgentDiffLine 是 record、字串，都是值相等）</summary>
    private static bool SameList<T>(List<T>? a, List<T>? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a.Count != b.Count)
            return false;
        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < a.Count; i++)
        {
            if (!comparer.Equals(a[i], b[i]))
                return false;
        }
        return true;
    }

    /// <summary>都是 null，或都不是 null 而且照列舉順序（序列化的順序）逐筆相同</summary>
    private static bool SameMap(Dictionary<string, string>? a, Dictionary<string, string>? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a.Count != b.Count)
            return false;
        using var x = a.GetEnumerator();
        using var y = b.GetEnumerator();
        while (x.MoveNext() && y.MoveNext())
        {
            if (x.Current.Key != y.Current.Key || x.Current.Value != y.Current.Value)
                return false;
        }
        return true;
    }

    /// <summary>都是 null，或都是空的（有內容的巢狀物件不逐層比，一律當成不同）</summary>
    private static bool BothEmpty(System.Collections.ICollection? a, System.Collections.ICollection? b)
        => a is null || b is null ? a is null && b is null : a.Count == 0 && b.Count == 0;

    /// <summary>答案的字典：比較子與列舉順序照舊（序列化的鍵順序不變）、每個值另外複製</summary>
    private static Dictionary<string, List<string>> CopyAnswers(Dictionary<string, List<string>> answers)
    {
        var copy = new Dictionary<string, List<string>>(answers.Count, answers.Comparer);
        foreach (var (key, value) in answers)
            copy[key] = value is null ? null! : new List<string>(value);
        return copy;
    }
}

/// <summary>對話的基本資料（meta.json）</summary>
internal sealed class AgentConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "新對話";

    /// <summary>執行這個對話的帳戶</summary>
    public string AccountId { get; set; } = string.Empty;

    public AgentProvider Provider { get; set; }

    /// <summary>CLI 端的對話識別：Claude 的 session id（預先產生的 UUID）、Codex 的 thread id</summary>
    public string? SessionId { get; set; }

    /// <summary>CLI 端是否已真的建立過這個 session（建立過才能續接）</summary>
    public bool SessionStarted { get; set; }

    /// <summary>建立 session 的帳戶（換帳戶後原 session 續接不了，要以前文節錄開新的）</summary>
    public string? SessionAccountId { get; set; }

    public string? Model { get; set; }

    public string? Effort { get; set; }

    /// <summary>Codex 速度模式的原生 id；null＝照 CLI 設定，default＝標準。</summary>
    public string? CodexServiceTier { get; set; }

    /// <summary>Codex 原生模式：plan／default；null＝尚未覆寫 CLI 設定。</summary>
    public string? CodexMode { get; set; }

    /// <summary>這個對話明確指定的 Codex 技能；送出時以 CLI 的最新清單重驗。</summary>
    public List<AgentSkillSelection>? CodexSkills { get; set; }

    /// <summary>自訂代理名稱（Antigravity、Claude；首輪建立對話前以 --agent 指定，Claude 之後每次啟動照帶）；null＝預設代理</summary>
    public string? Agent { get; set; }

    /// <summary>Claude 的輸出風格（啟動時以 --settings 的 outputStyle、執行中以 apply_flag_settings 套用）；null＝照 CLI 的設定</summary>
    public string? OutputStyle { get; set; }

    /// <summary>
    /// Claude 的回溯（AgentHub.Rewind.cs）：下次啟動程序時以 --resume --resume-session-at 這個 assistant 訊息的 uuid --fork-session
    /// 從那裡分支出新的 session（之後的內容代理看不到）；新的 session 開始後清掉。null＝照常續接
    /// </summary>
    public string? ResumeAt { get; set; }

    public AgentPermissionMode PermissionMode { get; set; } = AgentPermissionMode.Ask;

    /// <summary>代理的工作資料夾；null＝對話資料夾下的 workspace</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>最後執行這個對話的 CLI 版本（程序自己回報的，AgentRunningVersionEvent）；跨電腦接手時拿來比較新舊。null＝還沒有回報過</summary>
    public string? LastCliVersion { get; set; }

    /// <summary>
    /// Claude Remote Control 的遠端工作階段 id（AgentRemoteControlEvent）：程序重開時接回，claude.ai 與 Claude App 上一直是同一個工作階段。
    /// 換帳戶時清掉；null＝還沒開過
    /// </summary>
    public string? RemoteControlSessionId { get; set; }

    /// <summary>使用者以 @ 選取、授權這個主對話委派的本機帳戶 id；null＝沒有選取。</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? DelegatedAccountIds { get; set; }

    /// <summary>
    /// 臨時對話（右上角的對話鈕開的浮動面板，2026-11-11 使用者要求：問日常的問題、不屬於任何專案、問完不留紀錄）：不列在對話清單、
    /// 不上伺服器（桌面開的是 localOnly）、關掉面板就連同 CLI 的逐字稿一起刪掉，app 下次啟動時把沒刪掉的也刪掉。null＝一般對話（不寫進 meta.json，前一版忽略）
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Ephemeral { get; set; }

    /// <summary>委派工作的來源對話；子對話不能再往下委派。</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? ParentConversationId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    // 第 4 階段的同步欄位（WP4.1 計畫 5.2）：全部可為 null、null 時不寫進 meta.json（舊檔照讀、存回逐位元組不變）

    /// <summary>
    /// 這個對話的同步政策（設計 14.11 D37；docs/sync-protocol.md 14.2）：小寫的 full／metadata／localOnly／paused，null＝full（沒設過）。
    /// 存原字串：不認得的值（之後的版本新增的）不能被當成 full 而把對話發布出去，由規則（WP4.1 步 3）解讀
    /// </summary>
    public string? SyncPolicy { get; set; }

    /// <summary>擁有這個對話（執行它）的電腦 deviceId；null＝這台（未配對、或還沒發布過）</summary>
    public string? OwnerDeviceId { get; set; }

    /// <summary>擁有權的世代（第 5 階段的租約）；null＝1（第 4 階段沒有租約）</summary>
    public long? LeaseEpoch { get; set; }

    /// <summary>
    /// 下一個要配給項目的 <see cref="AgentChatItem.Ord"/>（OrdRules，WP4.1 步 4）：刪掉的項目的 Ord 也不重用，所以要記在 meta.json；
    /// 實際的下一個一律是它與現有項目最大 Ord＋1 的較大者（items.json 可能比 meta.json 新）。null＝還沒配過
    /// </summary>
    public long? NextOrd { get; set; }

    // 第 5 階段的欄位（計畫 docs/plans/2026-10-23-phase5-handoff.md 第 4.3 節第 6 點、WP5.1 步 5）：同第 4 階段，全部可為 null、null 時不寫進 meta.json
    // （舊檔照讀、存回逐位元組不變），前一版忽略不認得的欄位

    /// <summary>
    /// 自訂工作資料夾跨電腦的對應依據（WP5.4 的 RepoIdentity，隨 <c>conv.meta</c> 的 <c>workingDirectoryRef</c> 發布）：只有 <see cref="WorkingDirectory"/> 有值時才有；
    /// null＝還沒算過（或對話自己的資料夾）
    /// </summary>
    public PathRef? WorkingDirectoryRef { get; set; }

    /// <summary>這個對話是分支：原對話的 id（WP5.8：離線期間被強制接手、這台的新內容另存成的對話）；null＝不是分支</summary>
    public string? ForkedFrom { get; set; }

    /// <summary>分支建立當下原對話的標題（側欄與標頭的「來自〈原標題〉」）</summary>
    public string? ForkedFromTitle { get; set; }

    /// <summary>分支點：強制接手時伺服器的 seq（<c>conv.lease.fence.atSeq</c>）</summary>
    public long? ForkPointSeq { get; set; }

    /// <summary>
    /// Claude 分支第一次送出要延續的來源逐字稿的完整路徑（<c>--resume &lt;路徑&gt; --fork-session --session-id &lt;這個對話的 sid&gt;</c>，E-4；WP5.8）：
    /// 看到 init 之後清掉。只在這台有意義（本機路徑，不上線）
    /// </summary>
    public string? ForkSourceTranscript { get; set; }

    /// <summary>換機說明（Hub/HandoffNoteRules，WP5.7）：接手後第一次送出時附在使用者訊息之前（畫面只顯示原文），送出開始後清掉</summary>
    public string? PendingHandoffNote { get; set; }

    /// <summary>
    /// 額度用完、自動換到別的帳戶之前的原帳戶（Hub/AgentHub.AccountSwitch.cs，2026-10-26）：原帳戶的額度恢復後自動換回來。
    /// 使用者自己換了帳戶、換回原帳戶之後清掉；可為 null、null 時不寫進 meta.json，前一版忽略。只在這台有意義（不上線）
    /// </summary>
    public QuotaHomeAccount? QuotaHome { get; set; }

    /// <summary>
    /// 額度用完、沒有帳戶可以接手時，等這個帳戶的額度重置後自動接著做（Hub/AgentHub.QuotaWait.cs，2026-10-26）；開始任何一輪、取消就清掉。
    /// 可為 null、null 時不寫進 meta.json，前一版忽略。只在這台有意義（不上線）
    /// </summary>
    public QuotaWait? QuotaWait { get; set; }

    /// <summary>
    /// 使用者選了要換的帳戶，但還有背景工作在跑（換帳戶會關掉程序、背景工作跟著停）：等背景工作都結束、對話閒置時才換
    /// （Hub/AgentHub.AccountSwitch.cs，2026-10-27）。換了帳戶、選回目前的帳戶就清掉；記在 meta 是因為 app 換版、重開時背景工作已被停掉，
    /// 重開後照樣換過去。可為 null、null 時不寫進 meta.json，前一版忽略。只在這台有意義（不上線）
    /// </summary>
    public DeferredAccountSwitch? DeferredAccount { get; set; }

    /// <summary>
    /// Claude 回報的快取過期後整份重送的額度成本（get_context_usage 的 coldResumeQuota）：送出前的快取提醒以它算這一則約用掉
    /// 5 小時額度的幾 %（網頁 claudeoptrules.cacheResumeWarning）。每一輪之後的回應更新；CLI 不知道價格（沒有訂閱方案）時是 null、不提醒。
    /// 記在 meta 是因為使用者常在 app 重開之後才回到閒置的對話。可為 null、null 時不寫進 meta.json，前一版忽略。只在這台有意義（不上線）
    /// </summary>
    public ColdResumeQuota? ColdResume { get; set; }
}

/// <summary>
/// 快取過期後重送整份上下文的額度成本（<see cref="AgentConversation.ColdResume"/>；Claude Code 的 get_context_usage.coldResumeQuota，
/// 終端機「Resume this conversation?」對話框用的同一份）
/// </summary>
/// <param name="PercentPerMillionTokens">重送一百萬 tokens 用掉 5 小時額度的幾 %（大於 0）</param>
/// <param name="MinPercent">值得提醒使用者的最小百分比（至少 1）</param>
internal sealed record ColdResumeQuota(double PercentPerMillionTokens, double MinPercent);

/// <summary>等額度重置後自動接著做（<see cref="AgentConversation.QuotaWait"/>）</summary>
/// <param name="AccountId">等的帳戶（對話換了帳戶就不等）</param>
/// <param name="Until">到這個時間接著做（重置時間加一點寬限）</param>
internal sealed record QuotaWait(string AccountId, DateTimeOffset Until);

/// <summary>等背景工作結束才換的帳戶（<see cref="AgentConversation.DeferredAccount"/>）</summary>
/// <param name="AccountId">要換到的帳戶</param>
/// <param name="RememberAccount">false＝遠端選的（換過去時不改「上次使用的帳戶」）</param>
internal sealed record DeferredAccountSwitch(string AccountId, bool RememberAccount);

/// <summary>
/// 額度用完自動換帳戶之前的原帳戶（<see cref="AgentConversation.QuotaHome"/>）。連續換了好幾個帳戶時一直是第一個（使用者原本用的）
/// </summary>
/// <param name="AccountId">原帳戶</param>
/// <param name="ResetsAt">原帳戶的額度重置的時間（用完時推得的）；null＝不知道</param>
/// <param name="ExhaustedAt">原帳戶用完的時間</param>
/// <param name="Model">換走之前的模型（換回時還原：跨代理換回一律還原，同種代理只在使用者沒自己改過時，見 <paramref name="SwitchedModel"/>）</param>
/// <param name="Effort">換走之前的推理強度</param>
/// <param name="PermissionMode">換走之前的權限模式（跨代理換回時還原）</param>
/// <param name="Agent">換走之前的自訂代理（跨代理換回時還原）</param>
/// <param name="OutputStyle">換走之前的輸出風格（跨代理換回時還原）</param>
/// <param name="SwitchedModel">最近一次自動換帳戶之後的模型：換回時目前的模型還是它，就是使用者沒自己改過</param>
/// <param name="SwitchedEffort">最近一次自動換帳戶之後的推理強度</param>
internal sealed record QuotaHomeAccount(
    string AccountId,
    DateTimeOffset? ResetsAt,
    DateTimeOffset ExhaustedAt,
    string? Model,
    string? Effort,
    AgentPermissionMode PermissionMode,
    string? Agent,
    string? OutputStyle,
    string? SwitchedModel = null,
    string? SwitchedEffort = null);

/// <summary>工作資料夾的跨電腦參照的種類（<see cref="PathRef.Kind"/>）</summary>
internal static class PathRefKinds
{
    /// <summary>git 工作樹：以正規化的 remote（或 root commit）＝<see cref="PathRef.RepoKey"/> 對應到別台的資料夾</summary>
    public const string Git = "git";

    /// <summary>不是 git：只能以同一個絕對路徑對應</summary>
    public const string Path = "path";

    public static bool IsKnown(string? kind) => kind is Git or Path;
}

/// <summary>
/// 自訂工作資料夾的跨電腦參照（第 5 階段計畫 5.3、第 8 節；WP5.4 計算）：meta.json 的 <c>WorkingDirectoryRef</c> 與 <c>conv.meta</c> 的 <c>workingDirectoryRef</c>
/// 是同一個記錄（存檔 PascalCase、線上 camelCase）
/// </summary>
/// <param name="Kind"><see cref="PathRefKinds"/></param>
/// <param name="RepoKey"><c>git:&lt;主機&gt;/&lt;路徑&gt;</c> 或 <c>root:&lt;root commit&gt;</c>（Protocol 的 RepoKeyRules，WP5.4）；<c>path</c> 種類沒有</param>
/// <param name="Sub">工作資料夾相對 git 頂層的路徑（正斜線；頂層本身是 null）</param>
/// <param name="Display">給人看的名稱（repo 名）</param>
internal sealed record PathRef(string Kind, string? RepoKey = null, string? Sub = null, string? Display = null);

/// <summary>一輪的 token 用量（只供顯示）</summary>
internal sealed record AgentUsage(long InputTokens, long OutputTokens, long CachedInputTokens, double? CostUsd);

internal enum AgentTurnOutcome
{
    Completed,
    Interrupted,
    Failed,
}

/// <summary>Codex 明確指定的技能名稱與 CLI 回報的檔案路徑。</summary>
internal sealed record AgentSkillSelection(string Name, string Path);

/// <summary>送給代理的一則訊息</summary>
/// <param name="MessageUuid">
/// 寫給 CLI 的訊息 uuid（Claude；中樞給了就用它、記在使用者項目的 CliUuid：回溯與取消插入以它指名那則）。null＝會話自己產生
/// </param>
internal sealed record AgentTurnInput(string Text, IReadOnlyList<AgentAttachment> Attachments, string? MessageUuid = null);

/// <summary>核准卡的選擇</summary>
internal enum AgentApprovalChoice
{
    Allow,
    AllowForSession,
    Deny,
}

/// <summary>計畫確認卡的選擇</summary>
internal enum AgentPlanChoice
{
    /// <summary>照計畫執行，之後改檔案不再詢問</summary>
    AcceptEdits,

    /// <summary>照計畫執行，每次改檔案仍詢問</summary>
    Manual,

    /// <summary>不同意，繼續規劃</summary>
    KeepPlanning,
}

/// <summary>模型選單的一個選項</summary>
/// <param name="DefaultEffort">
/// 不指定推理強度時這個模型實際用的強度（2026-11-12 使用者要求：選「預設」時也顯示是哪個強度）：Codex 取 model/list 的 defaultReasoningEffort，
/// Claude 由能力探查以 set_model＋get_settings 的 applied.effort 量；不知道是 null（settings.json 裡沒有這個欄位，前一版忽略）
/// </param>
internal sealed record AgentModelOption(string Value, string Label, string? Description, IReadOnlyList<string> Efforts, string? DefaultEffort = null);

/// <summary>「/」指令選單的一個項目（Claude initialize 回報的 commands）</summary>
internal sealed record AgentSlashCommand(string Name, string? Description, string? ArgumentHint);

/// <summary>有名稱與說明的選項（Claude 的自訂代理清單：initialize 回應的 agents）</summary>
internal sealed record AgentNamedOption(string Value, string? Description);

/// <summary>權限模式在各代理的顯示名稱與說明</summary>
internal static class AgentPermissionModes
{
    public sealed record Option(AgentPermissionMode Mode, string Label, string Description);

    private static readonly Option[] ClaudeOptions =
    {
        new(AgentPermissionMode.Ask, "每步詢問", "執行指令、修改檔案前先問你"),
        new(AgentPermissionMode.AcceptEdits, "自動接受編輯", "修改檔案與 mkdir、rm、mv 等檔案指令不再詢問，其他指令仍會問"),
        new(AgentPermissionMode.Plan, "規劃模式", "只讀取與分析，先提出計畫給你確認"),
        new(AgentPermissionMode.Auto, "自動", "由代理判斷，高風險操作才問"),
        new(AgentPermissionMode.FullAccess, "完全自動", "一律放行，不再詢問"),
    };

    private static readonly Option[] CodexOptions =
    {
        new(AgentPermissionMode.Ask, "每步詢問", "沙箱內執行，每個指令先問你"),
        new(AgentPermissionMode.Auto, "自動", "沙箱內自動執行，需要更多權限時才問"),
        new(AgentPermissionMode.FullAccess, "完全自動", "完全存取、一律放行"),
    };

    /// <summary>Antigravity：stream-json 沒有核准協定，拿不到核准的操作會被 agy 略過（軟拒絕），所以沒有「每步詢問」；「自動（沙箱）」以 --dangerously-skip-permissions --sandbox 在 Windows 終端機沙箱內自動執行</summary>
    private static readonly Option[] AntigravityOptions =
    {
        new(AgentPermissionMode.AcceptEdits, "自動接受編輯", "可以讀取與修改檔案；執行指令等需要核准的操作會被自動拒絕（agy 無法在對話中詢問）"),
        new(AgentPermissionMode.Plan, "規劃模式", "只讀取與分析，寫出計畫後顯示確認卡等你決定"),
        new(AgentPermissionMode.Auto, "自動（沙箱）", "在 Windows 終端機沙箱內自動執行指令與修改檔案，保護工作區外的敏感目錄"),
        new(AgentPermissionMode.FullAccess, "完全自動", "一律放行，包括在沙箱外執行指令"),
    };

    /// <summary>
    /// Grok：ACP 有核准請求與即時切換（_x.ai/yolo_mode_changed、session/set_mode）。grok 沒有「自動接受編輯」模式，由會話代為核准
    /// 工作資料夾內的編輯；「自動」要帳戶有開放（遠端設定），沒開放時 grok 會留在每步詢問
    /// </summary>
    private static readonly Option[] GrokOptions =
    {
        new(AgentPermissionMode.Ask, "每步詢問", "執行指令、修改檔案前先問你"),
        new(AgentPermissionMode.AcceptEdits, "自動接受編輯", "工作資料夾內修改檔案不再詢問，執行指令仍會問"),
        new(AgentPermissionMode.Plan, "規劃模式", "只讀取與分析，先提出計畫給你確認"),
        new(AgentPermissionMode.Auto, "自動", "由 Grok 判斷，高風險操作才問（帳戶沒開放時照每步詢問）"),
        new(AgentPermissionMode.FullAccess, "完全自動", "一律放行，不再詢問"),
    };

    public static IReadOnlyList<Option> For(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => ClaudeOptions,
        AgentProvider.Codex => CodexOptions,
        AgentProvider.Antigravity => AntigravityOptions,
        AgentProvider.Grok => GrokOptions,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 這個代理不支援的模式換成最接近的：Codex 的「自動接受編輯」→自動、「規劃模式」→每步詢問；
    /// Antigravity 的「每步詢問」→自動接受編輯（stream-json 不能詢問；「自動（沙箱）」對應 --dangerously-skip-permissions --sandbox）
    /// </summary>
    public static AgentPermissionMode Normalize(AgentProvider provider, AgentPermissionMode mode) => provider switch
    {
        AgentProvider.Claude => mode,
        AgentProvider.Codex => mode switch
        {
            AgentPermissionMode.AcceptEdits => AgentPermissionMode.Auto,
            AgentPermissionMode.Plan => AgentPermissionMode.Ask,
            _ => mode,
        },
        AgentProvider.Antigravity => mode == AgentPermissionMode.Ask ? AgentPermissionMode.AcceptEdits : mode,
        AgentProvider.Grok => mode,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };
}
