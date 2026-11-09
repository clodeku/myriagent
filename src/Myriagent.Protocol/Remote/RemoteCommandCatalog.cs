using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Remote;

/// <summary>遠端指令由誰執行（設計 14.11 D53）</summary>
internal enum RemoteRoute
{
    /// <summary>轉給對話的擁有者電腦（信封帶 conv／epoch）</summary>
    Owner,

    /// <summary>轉給指定的電腦（帳戶、CLI 與新對話；信封不帶 conv，電腦改驗 dev）</summary>
    Device,

    /// <summary>伺服器自己處理（瀏覽器模式），不轉給任何電腦；電腦的閘門一律拒絕</summary>
    ServerOnly,
}

/// <summary>遠端執行時中樞要守的限制（D15：遠端不寫這台的持久設定）；處理者（WP4.4）照這裡做，測試照這裡斷言</summary>
[Flags]
internal enum RemoteLimits
{
    None = 0,

    /// <summary>setOption 只改這個對話（不把模型記成帳戶下次的預設）</summary>
    ConversationOnly = 1,

    /// <summary>setFolder 不寫進最近資料夾（遠端選的資料夾不算「已知」）</summary>
    NoRememberFolder = 2,

    /// <summary>accountRemove 一律保留資料夾（deleteData 當成 false，<see cref="RemoteCommandCatalog.ApplyLimits"/>）</summary>
    KeepAccountData = 4,

    /// <summary>新對話的工作資料夾不取最近資料夾，由指令明給（沒給＝對話自己的 workspace）</summary>
    ExplicitFolder = 8,
}

/// <summary>
/// 型錄的一項（D53）。只有路由是 Owner／Device 的指令有形狀與層級；伺服器自己處理的只列名稱（<see cref="RemoteCommandCatalog.ServerOnlyCommands"/>）
/// </summary>
/// <param name="BaseTier">最低的層級（依事實只升不降，RemoteTierRules）</param>
/// <param name="DurableAllowed">可以 durable（電腦不在線時伺服器保存未簽的內容、上線後重簽，D16；目前只有所有電腦登出）</param>
/// <param name="Fields">cmd 物件除了 <c>cmd</c> 以外可以有的鍵（RemoteCommandShapeRules）</param>
/// <param name="Outputs">CommandOutput 的 <c>message.type</c> 白名單（D18；中樞回給「這個檢視」的網頁訊息）</param>
internal sealed record RemoteCommandSpec(
    string Name,
    RemoteRoute Route,
    int BaseTier,
    bool DurableAllowed,
    IReadOnlyList<RemoteField> Fields,
    IReadOnlyList<string> Outputs,
    RemoteLimits Limits = RemoteLimits.None)
{
    /// <summary>要帶卡片摘要的指令（approve、approvePlan、answer；閘門 5h，RemoteCardRules）</summary>
    public bool IsCard => Fields.Any(f => f.Kind == RemoteFieldKind.Digest);
}

/// <summary>
/// 遠端指令的型錄（設計 14.11 D53；伺服器與電腦共用）：web-protocol 第二節的每個指令都在這裡（電腦執行或伺服器處理），
/// 或明列為只在電腦本機（<see cref="LocalOnlyCommands"/>）；另有只由伺服器送給電腦的 <c>conversationCreate</c>
/// （瀏覽器的伺服器草稿第一次 send 時，設計 4.9.2）。第 5 階段的 <c>takeover</c>、<c>handoffCancel</c> 不在型錄（閘門 5a 拒絕）。
/// WP4.1 只立型錄：伺服器照樣回 not_available、電腦的預設授權者一律回 unsupported
/// </summary>
internal static class RemoteCommandCatalog
{
    /// <summary>只由伺服器送給電腦、不是網頁指令的</summary>
    public const string ConversationCreate = "conversationCreate";

    /// <summary>Claude 遠端登入貼回授權碼（2026-10-26）</summary>
    public const string AccountLoginCode = "accountLoginCode";

    /// <summary>
    /// 遠端執行指令（2026-10-25；docs/sync-protocol.md 14.11、docs/plans/2026-10-25-remote-exec.md）：只由桌面發起（對話裡的代理經 <c>myriagent-remote</c>，
    /// 裝置 hub 的 <c>PeerCmd</c>），不是網頁指令——瀏覽器版不收（<c>BrowserCommandRules</c> 不分類，回 invalid）。輸出另走 <c>exec1</c> 的串流
    /// </summary>
    public const string RemoteExec = "remoteExec";

    /// <summary>排程頁的快速操作（S3）</summary>
    public const string ScheduleAction = "scheduleAction";

    /// <summary>排程頁的交給代理（S3）</summary>
    public const string ScheduleAgent = "scheduleAgent";

    /// <summary>
    /// 瀏覽器版新對話（伺服器草稿）的「瀏覽資料夾…」（2026-10-27）：網頁照樣送草稿 id 的 <c>listFiles</c>（dirsOnly），伺服器改成這個指令轉給草稿選的電腦
    /// （沒選是送出時會挑的那台）。那台還沒有這個對話，只列它的最近資料夾（同 dirsOnly 的範圍，不含工作資料夾）；輸出的 <c>files</c> 不帶 conversationId，
    /// 伺服器換成草稿 id。只由伺服器送給電腦，不是網頁指令
    /// </summary>
    public const string ListFolders = "listFolders";

    /// <summary>
    /// 電腦狀態面板的檔案傳輸（2026-11-09）：列那台的一個資料夾（path 是 null＝磁碟與常用資料夾）。
    /// 輸出是 <c>files</c>（conversationId 是 null，網頁以 requestId 認領）；目標要協商出 <c>xfer1</c>
    /// </summary>
    public const string DeviceBrowse = "deviceBrowse";

    /// <summary>電腦狀態面板的檔案傳輸：那台一個檔的下載參照（輸出 <c>fileRef</c>，伺服器登記 <c>xfer</c> 參照、2 小時）</summary>
    public const string DeviceFetch = "deviceFetch";

    /// <summary>電腦狀態面板的檔案傳輸：那台把已上傳到伺服器的檔下載進 folder（同名自動改名）；進度與結果走 DeviceStats.xfers</summary>
    public const string DevicePut = "devicePut";

    /// <summary>
    /// 整個資料夾的下載（2026-11-09）：列那台一個資料夾底下所有的檔（遞迴；輸出 <c>fileTree</c>，相對路徑與大小，最多 5000 個檔）。目標要協商出 <c>xfer2</c>
    /// </summary>
    public const string DeviceTree = "deviceTree";

    /// <summary>
    /// 整個資料夾的下載：一次要多個檔的下載參照（<c>root</c> 加上 <c>pathsJson</c> 裡的相對路徑，最多 200 個；輸出一則 <c>fileRefs</c>）。目標要協商出 <c>xfer2</c>
    /// </summary>
    public const string DeviceFetchMany = "deviceFetchMany";

    /// <summary>devicePut 的子資料夾（整個資料夾的上傳：存進 folder 底下的這個相對路徑，沒有就建）；帶它時目標要協商出 <c>xfer2</c></summary>
    public const string DevicePutSubfolder = "subfolder";

    /// <summary>電腦狀態面板的檔案傳輸的指令（伺服器只送給協商出 xfer1 的連線；整個資料夾的兩個另要 xfer2）</summary>
    public static IReadOnlySet<string> DeviceTransferCommands { get; } = Set(DeviceBrowse, DeviceFetch, DevicePut, DeviceTree, DeviceFetchMany);

    /// <summary>整個資料夾的傳輸要 <c>xfer2</c> 的指令（devicePut 帶 subfolder 時也要）</summary>
    public static IReadOnlySet<string> DeviceFolderCommands { get; } = Set(DeviceTree, DeviceFetchMany);

    /// <summary>型錄裡不是網頁指令的：只由伺服器送給電腦的 <see cref="ConversationCreate"/>、<see cref="ListFolders"/>，只由桌面發起的 <see cref="RemoteExec"/></summary>
    public static IReadOnlySet<string> NonWebCommands { get; } = Set(ConversationCreate, RemoteExec, ListFolders);

    /// <summary>停止（層級 0）：電腦的伺服器時間推算不可用時，信封只收它（docs/sync-protocol.md 14.4 第 8 步）</summary>
    public const string Stop = "stop";

    /// <summary>
    /// 瀏覽器模式下伺服器自己處理、不轉給電腦的（伺服器處理、接受不處理、伺服器草稿的 newConversation、伺服器回 clipboard 的 copyItem、
    /// 電腦狀態面板的 monitor、網頁錯誤回報的 clientError、伺服器搜清單的 searchConversations）
    /// </summary>
    public static IReadOnlySet<string> ServerOnlyCommands { get; } = Set(
        "ready", "layout", "open", "close", "visible", "loadOlder", "setting", "accountUpdate", "accountReorder", "deviceRename",
        "newConversation", "copyItem", "monitor", "clientError", "searchConversations",
        // 多人協作（2026-10-30）：擁有者管理協作人與對話的分享，伺服器處理（桌面經裝置 hub 的 CollabCmd 交給伺服器）
        "collabCreate", "collabUpdate", "collabDelete", "collabShare");

    /// <summary>
    /// 只在電腦本機的 WebView 收的（瀏覽器模式回 desktop_only）：遠端一律不收。另外 <c>sync</c> 後面接大寫字母的名稱一律算（<see cref="IsLocalOnly"/>）
    /// </summary>
    public static IReadOnlySet<string> LocalOnlyCommands { get; } = Set(
        "syncPair", "syncCancelPair", "syncUnpair", "syncPause", "syncRetry", "syncConfirm", "syncSetPolicy", "syncSetConversationPolicy",
        "setConversationDefault", "cliApplyNow", "cliContinue", "cliCancelUpdate",
        "chooseFolder", "attach", "addFiles", "pasteImage", "addPathAttachment",
        "openFolder", "openPath", "revealPath", "saveFile", "copyMedia", "openLink", "copyText", "popout", "accountOpenFolder",
        // Codex 的免費重置（2026-10-31）：會真的用掉一次，只在這台的儀表板按
        "accountResetCredit",
        // 多視窗（2026-10-25）：這台的視窗與分頁怎麼排，只有這台的視窗能做
        "newWindow", "tabDrag", "tabDragEnd", "tabMoveIn", "moveToNewWindow", "closeWindow",
        // 自動重新命名（標題的右鍵選單）：用這台的帳戶跑 CLI 產生標題
        "autoTitle",
        // 自訂主代理（Antigravity 工作資料夾的 .agents/agents/*.md、Claude 帳戶的代理）與 Claude 的輸出風格：清單是這台的 CLI 回報的
        "setAgent", "setOutputStyle",
        // Claude 的持續目標：清除這台程序裡的 /goal（標頭的目標只有這台的對話有）
        "goalClear", "codexControl",
        // 背景工作的詳細與強制停止：問這台活著的 CLI 程序（標頭的背景工作只有這台的對話有）
        "taskDetail", "taskStop",
        // 「最近的指令」的歷史指令：讀這台的對話紀錄（瀏覽器版從已載入的項目找）
        "promptHistory",
        // 取消插進這一輪的訊息：只有這台本機送出的那則可以（以這台活著的 Claude 程序取消）
        "cancelSteer",
        // 回溯：讀這台的 Claude 逐字稿、以這台的程序還原檔案
        "rewindPreview", "rewind",
        // 取消等額度重置後自動接著做：等待只記在這台（遠端的標頭沒有）
        "quotaWaitCancel",
        // 匯入本機外部 CLI 歷史對話：唯讀掃描與匯入這台電腦上的 CLI 對話紀錄
        "listExternalConversations", "importExternalConversation",
        // 延伸成新對話：複製這台的對話資料夾與 Claude 逐字稿
        "forkConversation",
        // 工作資料夾的 git 狀態「重新檢查」：讀這台的 repo
        "gitRefresh",
        // 執行列：在這台建置並執行工作資料夾裡的視窗程式（遠端不能在這台啟動程式）
        "launchAction", "launchSelect", "launchLog", "launchRescan",
        // 停止代理另開的背景程式：結束這台的程序（遠端的標頭沒有背景程式）
        "programStop",
        // 儀表板的 MCP：在這台開程序檢查、在這台的瀏覽器授權（以這台的帳戶為單位）
        "mcpCheck", "mcpAction",
        // 儀表板的健康度：讀這台各 CLI 的設定資料夾、以這台的帳戶開對話
        "healthCheck", "healthFix",
        // 排程頁（只有桌面；瀏覽器版與其他電腦還沒有，設計 2026-10-26-schedule-manager 的 S3）
        "scheduleWatch", "scheduleRefresh", "timedSave", "timedAction",
        // 儀表板的每週額度每 5% 的 token：紀錄在這台的資料夾
        "usageSegments",
        "draftSnapshot", "appUpdateCheck", "appUpdateRestartNow", "appUpdateForce", "appUpdateCancel", "appInstallHere",
        // 桌面遠端操作（docs/sync-protocol.md 14.7）：宿主代傳的附件與檔案下載、桌面草稿改開在其他電腦
        "peerUpload", "peerUploadCancel", "peerFileSave", "draftSetDevice",
        // 電腦狀態面板的檔案傳輸（2026-11-09）：把其他電腦的檔下載進這台的資料夾、取消、挑這台的資料夾
        "peerXferSave", "peerXferCancel", "peerXferReveal", "xferPickFolder");

    private static readonly string[] ToastOnly = ["toast"];

    private static readonly RemoteCommandSpec[] SpecList = BuildSpecs().ToArray();

    private static readonly IReadOnlyDictionary<string, RemoteCommandSpec> Specs = SpecList.ToDictionary(s => s.Name, StringComparer.Ordinal);

    /// <summary>由電腦執行的指令（路由 Owner／Device），依型錄的順序</summary>
    public static IReadOnlyList<RemoteCommandSpec> All => SpecList;

    /// <summary>由電腦執行的指令的定義；伺服器處理的、本機專屬的、沒列名的（含大小寫不同）回 null</summary>
    public static RemoteCommandSpec? Find(string? name) => name != null && Specs.TryGetValue(name, out var spec) ? spec : null;

    /// <summary>指令的路由；只在本機的與沒列名的回 null</summary>
    public static RemoteRoute? RouteOf(string? name)
        => Find(name)?.Route ?? (name != null && ServerOnlyCommands.Contains(name) ? RemoteRoute.ServerOnly : null);

    /// <summary>只在電腦本機（<see cref="LocalOnlyCommands"/>，或 <c>sync</c> 後面接大寫字母）</summary>
    public static bool IsLocalOnly(string? name)
        => name != null && (LocalOnlyCommands.Contains(name)
                            || name.Length > 4 && name.StartsWith("sync", StringComparison.Ordinal) && char.IsAsciiLetterUpper(name[4]));

    /// <summary>這個指令的 CommandOutput 可以帶這種網頁訊息（D18）；不在型錄的一律不行</summary>
    public static bool AllowsOutput(string? name, string? messageType)
        => messageType != null && Find(name) is { } spec && spec.Outputs.Contains(messageType, StringComparer.Ordinal);

    /// <summary>
    /// 遠端執行前套上 <see cref="RemoteLimits"/> 裡能在指令上表達的部分（回傳複本，不改原物件）：accountRemove 的 deleteData 一律 false
    /// </summary>
    public static JObject ApplyLimits(JObject command)
    {
        var copy = (JObject)command.DeepClone();
        if (RemoteCommandShapeRules.IsString(copy["cmd"], out var name) && Find(name) is { } spec && spec.Limits.HasFlag(RemoteLimits.KeepAccountData)
            && copy.Property("deleteData", StringComparison.Ordinal) != null)
            copy["deleteData"] = false;
        return copy;
    }

    private static IEnumerable<RemoteCommandSpec> BuildSpecs()
    {
        RemoteField Conv() => new("conversationId", RemoteFieldKind.ConversationId);
        RemoteField Item(string name = "itemId") => new(name, RemoteFieldKind.ItemId);
        RemoteField Text(string name, int max = RemoteCommandShapeRules.MaxTextLength, bool required = true, bool nullable = false)
            => new(name, RemoteFieldKind.Text, required, nullable, max);
        RemoteField Choice(string name, bool required, bool nullable, params string[] values) => new(name, RemoteFieldKind.Choice, required, nullable, Values: values);
        RemoteField Digest() => new("requestDigest", RemoteFieldKind.Digest);
        RemoteField Account(bool required = true, bool nullable = false) => new("accountId", RemoteFieldKind.AccountId, required, nullable);
        RemoteField Device(bool required = false) => new("deviceId", RemoteFieldKind.DeviceId, required);
        RemoteField RequestId() => new("requestId", RemoteFieldKind.RequestId);
        RemoteField Provider() => Choice("provider", true, false, AgentProviders.All.Select(p => p.ToString()).ToArray());
        RemoteField LoginMethod() => Choice("method", false, true, "deviceCode");
        RemoteField Uploads(bool required) => new("uploads", RemoteFieldKind.Uploads, required);
        RemoteCommandSpec Owner(string name, int tier, RemoteField[] fields, string[]? outputs = null, RemoteLimits limits = RemoteLimits.None)
            => new(name, RemoteRoute.Owner, tier, false, [Conv(), .. fields], outputs ?? ToastOnly, limits);
        RemoteCommandSpec ForDevice(string name, int tier, RemoteField[] fields, string[]? outputs = null, RemoteLimits limits = RemoteLimits.None, bool durable = false)
            => new(name, RemoteRoute.Device, tier, durable, fields, outputs ?? ToastOnly, limits);

        // 對話（轉給擁有者）
        yield return Owner("send", 1, [Text("text", RemoteCommandShapeRules.MaxAgentTextLength)], ["sendRejected", "toast"]);
        yield return Owner("cancelQueued", 1, [Item("id")]);
        yield return Owner(Stop, 0, []);
        // notes：每題的補充說明（2026-11-05，選用；形狀同 answers，QuestionNoteRules）
        yield return Owner("answer", 1, [Item(), new("answers", RemoteFieldKind.Answers), new("notes", RemoteFieldKind.Answers, false), Digest()], ["sendRejected", "toast"]);
        yield return Owner("skipQuestion", 1, [Item()]);
        yield return Owner("approve", 1, [Item(), Choice("choice", true, false, "allow", "allowSession", "deny"),
            Text("message", RemoteCommandShapeRules.MaxAgentTextLength, required: false, nullable: true), Digest()]);
        yield return Owner("approvePlan", 1, [Item(), Choice("choice", true, false, "acceptEdits", "manual", "keepPlanning"),
            Text("message", RemoteCommandShapeRules.MaxAgentTextLength, required: false, nullable: true), Digest()]);
        yield return Owner("rename", 1, [Text("title")]);
        yield return Owner("delete", 2, []);
        yield return Owner("setOption", 1, [Choice("key", true, false, "model", "effort", "permission"), new("value", RemoteFieldKind.OptionValue)],
            limits: RemoteLimits.ConversationOnly);
        // when：執行中換帳戶的時機（2026-10-24）。turnEnd＝這一輪結束時才換；沒帶＝立即（先停下這一輪），舊版的電腦只收沒帶的
        yield return Owner("setAccount", 2, [Account(), Choice("when", false, true, "now", "turnEnd")]);
        yield return Owner("setFolder", 1, [Text("path")], limits: RemoteLimits.NoRememberFolder);
        yield return Owner("resetFolder", 1, []);
        yield return Owner("removeAttachment", 1, [Item("id")]);
        yield return Owner("addUploaded", 1, [Uploads(required: true)]);
        // 語音訊息（2026-10-23）：網頁上傳的錄音（uploads 只有一筆），那台電腦下載、轉成逐字稿後當成一則訊息送進代理（同 send 的輸出）
        yield return Owner("voiceMessage", 1, [Uploads(required: true)], ["sendRejected", "toast"]);
        yield return Owner("listFiles", 1, [RequestId(), Text("path", required: false, nullable: true), new("dirsOnly", RemoteFieldKind.Bool, Required: false)], ["files", "toast"]);
        yield return Owner("fileSearch", 1, [RequestId(), Text("query")], ["fileSearch", "toast"]);
        yield return Owner("fileFetch", 2, [RequestId(), Text("path")], ["fileRef", "toast"]);

        // 帳戶與 CLI（轉給指定的電腦；deviceId 是瀏覽器指定的電腦，有帶時要等於信封的 dev）
        yield return ForDevice("accountAdd", 2, [Provider(), Text("label", 64, required: false), Text("color", 32, required: false),
            new("logicalAccountId", RemoteFieldKind.LogicalAccountId, Required: false, Nullable: true), Device()], ["accountLogin", "toast"]);
        yield return ForDevice("accountLogin", 2, [Account(), LoginMethod(), Device()], ["accountLogin", "toast"]);
        yield return ForDevice("accountLoginCancel", 1, [Account(), Device()]);
        yield return ForDevice("accountLogout", 2, [Account(), Device()], durable: true);
        yield return ForDevice("accountRemove", 2, [Account(), new("deleteData", RemoteFieldKind.Bool, Required: false), Device()], limits: RemoteLimits.KeepAccountData);
        yield return ForDevice("accountRefresh", 1, [Account(required: false, nullable: true), Device()]);
        yield return ForDevice("cliCheck", 1, [Device()]);
        yield return ForDevice("cliInstall", 2, [Provider(), Device()]);
        yield return ForDevice("cliUpdate", 2, [Provider(), Device()]);
        yield return ForDevice("accountLoginDevice", 2, [new("logicalAccountId", RemoteFieldKind.LogicalAccountId), Device(required: true), LoginMethod()], ["accountLogin", "toast"]);
        // Claude 的遠端登入貼回來的授權碼（2026-10-26）：accountLogin 的 loginUrl 登入之後，頁面顯示的授權碼交給那台進行中的 app 內登入
        yield return ForDevice(AccountLoginCode, 2, [new("logicalAccountId", RemoteFieldKind.LogicalAccountId), Device(required: true), Text("code", 2048)]);

        // 瀏覽器建立的新對話（伺服器草稿第一次 send，交給使用者選的電腦；folder 沒給＝對話自己的 workspace）
        yield return ForDevice(ConversationCreate, 1,
        [
            Account(),
            Choice("permissionMode", true, false, Enum.GetNames<AgentPermissionMode>()),
            Text("model", 128, required: false, nullable: true),
            Text("effort", 128, required: false, nullable: true),
            Text("folder", required: false, nullable: true),
            Text("title", required: false),
            Text("text", RemoteCommandShapeRules.MaxAgentTextLength, required: false),
            Uploads(required: false),
        ], ["sendRejected", "toast"], RemoteLimits.ExplicitFolder | RemoteLimits.NoRememberFolder);
        // 瀏覽器新對話的資料夾挑選（伺服器把草稿的 listFiles dirsOnly 改成它；同 dirsOnly 是層級 2）
        yield return ForDevice(ListFolders, 2, [Device(required: true), RequestId(), Text("path", required: false, nullable: true)], ["files", "toast"]);

        // 電腦狀態面板的檔案傳輸（2026-11-09）：瀏覽整台的本機磁碟、下載一個檔、把上傳的檔存進那台的資料夾（同遠端選資料夾與 fileFetch，層級 2）
        yield return ForDevice(DeviceBrowse, 2, [Device(required: true), RequestId(), Text("path", required: false, nullable: true)], ["files", "toast"]);
        yield return ForDevice(DeviceFetch, 2, [Device(required: true), RequestId(), Text("path")], ["fileRef", "toast"]);
        yield return ForDevice(DevicePut, 2, [Device(required: true), RequestId(), Text("folder"), Uploads(required: true),
            Text(DevicePutSubfolder, 1024, required: false, nullable: true)]);
        // 整個資料夾（2026-11-09）：列遞迴的檔案清單、一次要多個檔的下載參照（pathsJson 是相對路徑的 JSON 陣列字串）
        yield return ForDevice(DeviceTree, 2, [Device(required: true), RequestId(), Text("path")], ["fileTree", "toast"]);
        yield return ForDevice(DeviceFetchMany, 2, [Device(required: true), RequestId(), Text("root"), Text("pathsJson", 60_000)], ["fileRefs", "toast"]);

        // 排程頁（S3，docs/sync-protocol.md 14.12）：其他電腦或手機對那台的 Windows 排程立即執行、停止、啟用、停用（只有這四個：
        // 不再列出、納入管理是那台自己的選擇，遠端不寫那台的持久設定），交給代理＝在那台的管理對話送出說明（沒有就開一個）
        yield return ForDevice(ScheduleAction, 2, [Device(required: true), Text("path", 512), Choice("action", true, false, "run", "stop", "enable", "disable")]);
        yield return ForDevice(ScheduleAgent, 1,
        [
            Device(required: true),
            Text("path", 512),
            Choice("task", true, false, "diagnose", "explain", "retime", "retire", "custom"),
            Text("note", 4000, required: false, nullable: true),
            Account(required: false, nullable: true),
        ]);

        // 遠端執行指令（只由桌面發起；deviceId 必帶）：那台要開「允許遠端執行指令」，高風險的另要在那台按同意（RemoteExecRiskRules）；
        // 結果的 done 只表示那台接下了，輸出與結束碼走 exec1 的串流（ExecOutput → ExecReply）
        yield return ForDevice(RemoteExec, 2,
        [
            Device(required: true),
            Choice("shell", true, false, [.. RemoteExecRules.Shells]),
            Text("command", RemoteExecRules.MaxCommandLength),
            Text("cwd", required: false, nullable: true),
            new("timeoutSec", RemoteFieldKind.Seconds, Required: false),
            Text("label", RemoteExecRules.MaxLabelLength, required: false),
        ]);
    }

    private static IReadOnlySet<string> Set(params string[] names) => names.ToHashSet(StringComparer.Ordinal);
}
