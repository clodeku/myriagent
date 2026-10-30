using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Files;
using Myriagent.Media;
using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 遠端的檔案指令（WP4.7 計畫步 11；D22、D33～D36）：listFiles（含 dirsOnly）、fileSearch、fileFetch、addUploaded 的處理者，
// 瀏覽器新對話的資料夾挑選 listFolders（2026-10-27：沒有對話，只列這台的最近資料夾），
// 以及 WP4.4 的 conversationCreate 帶 uploads 時呼叫的登記點 PrepareRemoteUploads（第一則延到上傳處理完才送，DeferUntilRemoteUploads）。
// - 遠端 send 只帶網頁上傳成為的待送附件（RemoteUploadedPending；本機使用者自己附的不帶，WP4.4 計畫 D19）。
// - 處理者都先看 context.Authorized（false 就 invalid）；UI 執行緒只取快照，檔案系統一律在背景（守門員 RemoteFileGuard，每個階段有逾時），
//   結果回 UI 執行緒以 context.Complete(輸出) 送出。範圍外（D33：工作資料夾子樹與 attachments\ 以外）與禁止清單一律以 policy 拒絕。
// - 輸出不帶任何本機網址（url、thumbUrl 由伺服器以參照換成 /m/、/t/、/f/，D37）；媒體與 fileFetch 的參照經 IRemoteFileTransfer
//   在引擎執行緒寫進 media_refs_local 之後才送輸出。對話 id 一律是線上格式。
// - 上傳的檔：名額先算好、ImportingCount 先加上（下載期間送出被拒），30 秒內 Complete，背景以 E6 下載到 cache\uploads\{uploadId}\、
//   驗大小與 SHA-256，回 UI 執行緒先減回自己的計數再逐一 ImportAsync；失敗加一個 Notice 項目並跳 toast。
// 中樞不碰同步引擎（S6）：傳輸只經 IRemoteFileTransfer（發布者交出），未配對時沒有傳輸，上傳與 fileFetch 以 unsupported 拒絕
internal sealed partial class AgentHub
{
    private const string ListFilesCommand = "listFiles";
    private const string FileSearchCommand = "fileSearch";
    private const string FileFetchCommand = "fileFetch";
    private const string AddUploadedCommand = "addUploaded";
    private const string ListFoldersCommand = RemoteCommandCatalog.ListFolders;

    private static readonly IReadOnlySet<string> NoExactFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>正在下載的上傳 id（UI 執行緒；同一個 id 同時只下載一次：暫存資料夾以 id 命名）</summary>
    private readonly HashSet<string> remoteUploadsInFlight = new(StringComparer.Ordinal);

    private RemoteFileGuard? remoteFileGuard;
    private RemoteFileIndex? remoteFileIndex;

    /// <summary>測試接縫：遠端檔案指令用的傳輸（null＝發布者交出的同步引擎實作）</summary>
    internal IRemoteFileTransfer? RemoteFileTransferForTest { get; set; }

    /// <summary>測試接縫：守門員（null＝正式的 <see cref="RemoteFileGuard.CreateDefault"/>）</summary>
    internal RemoteFileGuard? RemoteFileGuardForTest { get; set; }

    /// <summary>fileFetch 的 <c>expiresAt</c> 用的時鐘（測試換成假時鐘）</summary>
    internal TimeProvider RemoteFilesTime { get; set; } = TimeProvider.System;

    /// <summary>測試接縫：上傳暫存資料夾所在磁碟的可用空間（參數是暫存資料夾；回 null＝量不到）</summary>
    internal Func<string, long?>? UploadFreeSpaceForTest { get; set; }

    /// <summary>上傳的暫存資料夾（<c>cache\uploads</c>）</summary>
    internal static string UploadCacheDirectory => Path.Combine(AppPaths.CacheDirectory, "uploads");

    /// <summary>WP4.1 的登記點：檔案指令的處理者（建構式呼叫）</summary>
    partial void RegisterFileRemoteHandlers(List<IRemoteCommandHandler> into) => into.Add(new FileRemoteHandler(this));

    /// <summary>
    /// WP4.4 的登記點：conversationCreate 帶 uploads（UI 執行緒；<paramref name="conversation"/> 還是只在記憶體的草稿，處理者接著存成正式對話）。
    /// 開始背景下載並成為附件，<paramref name="prepared"/> 設成 true；不收（沒有傳輸、形狀、名額、正在下載）時以那個代碼拒絕
    /// （先到的結果為準：WP4.4 的處理者看到 false 會拿掉這個對話、再回的 unsupported 不會蓋掉），什麼都不建
    /// </summary>
    partial void PrepareRemoteUploads(ConversationState conversation, JArray uploads, RemoteViewContext context, ref bool prepared)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(context);
        if (StartRemoteUploads(conversation, ParseRemoteUploads(uploads)) is { } refused)
        {
            Log.Info($"新對話 {conversation.Meta.Id} 的 {uploads.Count} 個上傳不收（{refused.Code}）：{refused.Message}");
            context.Reject(refused.Code, refused.Message, RemoteGateSteps.Handler);
            return;
        }

        prepared = true;
    }

    /// <summary>
    /// WP4.4 的 conversationCreate 建好對話之後（UI 執行緒）：這個對話還有網頁上傳的檔在下載或匯入時，第一則的送出（<paramref name="send"/>）
    /// 延到全部處理完再做（送出時還在匯入會被拒；附件和第一則一起送）。沒有在處理的上傳回 false，呼叫端照常馬上送。
    /// 上傳失敗時照樣送（對話裡有說明的 Notice）；對話在那之前被刪就不送
    /// </summary>
    private bool DeferUntilRemoteUploads(ConversationState conversation, Action send)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(send);
        if (conversation.RemoteUploadBatches == 0)
            return false;
        conversation.AfterRemoteUploads.Add(send);
        return true;
    }

    /// <summary>
    /// 遠端 send 帶走的附件（WP4.4 計畫 D19 的補充，WP4.7 計畫步 13）：只有網頁上傳的（addUploaded、conversationCreate 的 uploads）
    /// 還在輸入區的那些，依輸入區的順序；本機使用者自己附的不帶。順便拿掉已經不在輸入區的 id（送出、移除）
    /// </summary>
    private static List<AgentAttachment> RemoteUploadedPending(ConversationState c)
    {
        c.RemoteUploadedAttachmentIds.RemoveWhere(id => !c.PendingAttachments.Any(a => a.Id == id));
        return c.PendingAttachments.Where(a => c.RemoteUploadedAttachmentIds.Contains(a.Id)).ToList();
    }

    /// <summary>網頁上傳的檔的處理狀態（UI 執行緒）</summary>
    private sealed partial class ConversationState
    {
        /// <summary>從網頁上傳、已成為待送附件的 id（遠端 send 只帶這些，<see cref="RemoteUploadedPending"/>）</summary>
        public readonly HashSet<string> RemoteUploadedAttachmentIds = new(StringComparer.Ordinal);

        /// <summary>網頁上傳（uploadId）匯入後成為的附件 id（語音訊息以它找到自己的錄音，AgentHub.Voice.cs）；只在記憶體</summary>
        public readonly Dictionary<string, string> RemoteUploadAttachments = new(StringComparer.Ordinal);

        /// <summary>還在下載或匯入的上傳批數</summary>
        public int RemoteUploadBatches;

        /// <summary>上傳全部處理完之後要做的（conversationCreate 的第一則，<see cref="DeferUntilRemoteUploads"/>）</summary>
        public readonly List<Action> AfterRemoteUploads = new();
    }

    /// <summary>測試接縫：檔案指令的處理者本身（不經閘門，驗「沒有授權就拒絕」）</summary>
    internal IRemoteCommandHandler CreateFileRemoteHandlerForTest() => new FileRemoteHandler(this);

    /// <summary>測試接縫：對話的匯入計數與待送附件的檔名（UI 執行緒）</summary>
    internal (int Importing, IReadOnlyList<string> Attachments) ComposerStateForTest(string conversationId)
        => conversations.TryGetValue(conversationId, out var c) ? (c.ImportingCount, c.PendingAttachments.Select(a => a.FileName).ToList()) : (0, []);

    private RemoteFileGuard RemoteGuard => RemoteFileGuardForTest ?? (remoteFileGuard ??= RemoteFileGuard.CreateDefault());

    /// <summary>目前的傳輸（測試接縫優先；否則發布者交出的、引擎協商出 media1 才有）</summary>
    private IRemoteFileTransfer? CurrentRemoteFileTransfer() => RemoteFileTransferForTest ?? (conversationSink as IRemoteFileTransferSource)?.RemoteFileTransfer;

    /// <summary>檔案指令的處理者：認得的五個指令一律接下（回 true），其他不接</summary>
    private sealed class FileRemoteHandler(AgentHub hub) : IRemoteCommandHandler
    {
        public bool TryHandle(RemoteViewContext context, IAgentView view, string cmd, JObject command)
        {
            if (cmd is not (ListFilesCommand or FileSearchCommand or FileFetchCommand or AddUploadedCommand or ListFoldersCommand))
                return false;
            if (!context.Authorized)
            {
                context.Reject(RemoteResultCodes.Invalid, "這個指令沒有經過這台電腦的遠端操作檢查", RemoteGateSteps.Handler);
                return true;
            }

            // 新對話的資料夾挑選：還沒有對話（路由 Device，閘門 5c 已確認是給這台的）
            if (cmd == ListFoldersCommand)
            {
                _ = hub.RemoteListFoldersAsync(context, hub.SnapshotRemoteFolders(), command.Value<string>("requestId"), OptionalString(command["path"]));
                return true;
            }

            if (hub.SnapshotRemoteFiles(command) is not { } snapshot)
            {
                context.Reject(RemoteResultCodes.NotOwner, "這台電腦不是這個對話的擁有者，或對話已不存在", RemoteGateSteps.Handler);
                return true;
            }

            switch (cmd)
            {
                case ListFilesCommand:
                    _ = hub.RemoteListFilesAsync(context, snapshot, command.Value<string>("requestId"), OptionalString(command["path"]),
                        command["dirsOnly"] is JValue { Type: JTokenType.Boolean, Value: true });
                    break;
                case FileSearchCommand:
                    _ = hub.RemoteFileSearchAsync(context, snapshot, command.Value<string>("requestId"), command.Value<string>("query") ?? string.Empty);
                    break;
                case FileFetchCommand:
                    hub.StartRemoteFileFetch(context, snapshot, command.Value<string>("requestId"), command.Value<string>("path") ?? string.Empty);
                    break;
                default:
                    hub.HandleAddUploaded(context, snapshot, command);
                    break;
            }

            return true;
        }

        private static string? OptionalString(JToken? token) => token is JValue { Type: JTokenType.String, Value: string text } ? text : null;
    }

    // ───────────────────────────── 快照與範圍 ─────────────────────────────

    /// <summary>UI 執行緒取的快照（之後在背景用，不再碰中樞）。<paramref name="Folders"/> 是禁止清單用的資料夾（原樣）</summary>
    private sealed record RemoteFileSnapshot(
        ConversationState Conversation,
        string LocalId,
        string WireId,
        string ConversationDirectory,
        string AttachmentDirectory,
        string DefaultWorkingDirectory,
        string WorkingDirectory,
        bool CreateDefaultWorkspace,
        MediaFolderRoots Folders,
        IReadOnlyList<string> RecentFolders,
        RemoteFileGuard Guard,
        IRemoteFileTransfer? Transfer);

    /// <summary>指令的對話（閘門 5c 已確認存在、本機擁有、政策 full；conversationId 已換成本機 id）</summary>
    private RemoteFileSnapshot? SnapshotRemoteFiles(JObject command)
    {
        if (command.Value<string>("conversationId") is not { } id || !conversations.TryGetValue(id, out var c) || ConversationIds.ToWire(c.Meta.Id) is not { } wire)
            return null;
        return new RemoteFileSnapshot(
            c,
            c.Meta.Id,
            wire,
            store.GetConversationDirectory(c.Meta.Id),
            store.GetAttachmentDirectory(c.Meta.Id),
            store.GetDefaultWorkingDirectory(c.Meta.Id),
            GetWorkingDirectory(c),
            string.IsNullOrWhiteSpace(c.Meta.WorkingDirectory),
            CurrentMediaFolderRoots(),
            settings.RecentFolders.ToList(),
            RemoteGuard,
            CurrentRemoteFileTransfer());
    }

    /// <summary>listFolders 的快照（UI 執行緒）：沒有對話，只有最近資料夾與禁止清單用的資料夾</summary>
    private sealed record RemoteFolderSnapshot(MediaFolderRoots Folders, IReadOnlyList<string> RecentFolders, RemoteFileGuard Guard);

    private RemoteFolderSnapshot SnapshotRemoteFolders() => new(CurrentMediaFolderRoots(), settings.RecentFolders.ToList(), RemoteGuard);

    /// <summary>列資料夾的來源：線上的對話 id 與工作資料夾（<c>files</c> 的 conversationId、root；listFolders 沒有對話，都是 null）</summary>
    private sealed record RemoteListSource(string? WireId, string? Root, IReadOnlyList<string> RecentFolders, RemoteFileGuard Guard);

    private static RemoteListSource ListSourceOf(RemoteFileSnapshot s) => new(s.WireId, s.WorkingDirectory, s.RecentFolders, s.Guard);

    /// <summary>守門員的範圍（背景）：禁止清單的資料夾與這個對話的允許子樹照媒體轉送的組法（<see cref="RemoteScopeBuilder"/>），範圍的根是 <paramref name="roots"/></summary>
    private static async Task<RemoteFileScope> BuildRemoteFileScopeAsync(RemoteFileSnapshot s, IEnumerable<string?> roots, CancellationToken ct)
    {
        var guard = s.Guard;
        var folders = await RemoteScopeBuilder.FoldersAsync(s.Folders, s.ConversationDirectory, s.DefaultWorkingDirectory, s.AttachmentDirectory,
            guard.NormalizeRootAsync, ct).ConfigureAwait(false);
        var listRoots = await RemoteScopeBuilder.RootsAsync(roots, guard.NormalizeRootAsync, ct).ConfigureAwait(false);
        return new RemoteFileScope(s.LocalId, listRoots, NoExactFiles, folders);
    }

    /// <summary>對話自己的預設 workspace 不存在就建（同本機的檔案瀏覽；使用者選的資料夾不建）</summary>
    private static void EnsureDefaultWorkspace(RemoteFileSnapshot s)
    {
        if (!s.CreateDefaultWorkspace)
            return;
        try
        {
            Directory.CreateDirectory(s.DefaultWorkingDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"建不了對話的預設工作資料夾：{ex.Message}");
        }
    }

    /// <summary>背景工作出錯（程式錯誤或沒預期的 I/O）：記錄、結果不明（UI 執行緒）</summary>
    private void FailRemoteFiles(RemoteViewContext context, string cmd, Exception ex)
    {
        Log.Error($"遠端指令 {context.CommandId}（{cmd}）處理途中出錯", ex);
        context.Fail("電腦處理這個指令時出錯", RemoteGateSteps.Handler);
    }

    /// <summary>
    /// 背景的結果：<see cref="RejectCode"/> 不是 null 時以它拒絕（什麼都沒做；範圍外是 policy，傳輸剛好停了是 unsupported），否則送 <see cref="Message"/>
    /// </summary>
    private sealed record RemoteFilesResult(string? RejectCode, string? RejectMessage, JObject? Message)
    {
        public static RemoteFilesResult Send(JObject message) => new(null, null, message);

        /// <summary>範圍外、禁止清單、UNC、網路磁碟、重新解析點…（D33：一律 policy）</summary>
        public static RemoteFilesResult RefusedPath { get; } = new(RemoteResultCodes.Policy, "這個路徑不在對話的工作資料夾裡，或不能給遠端讀取", null);

        /// <summary>同步引擎在途中停了（參照寫不進去）</summary>
        public static RemoteFilesResult NoTransfer { get; } = new(RemoteResultCodes.Unsupported, "這台電腦目前沒有和伺服器連線（或伺服器不支援媒體），不能從網頁讀檔", null);
    }

    /// <summary>背景工作做完（UI 執行緒）：送輸出並完成，或拒絕</summary>
    private void FinishRemoteFiles(RemoteViewContext context, RemoteFilesResult result)
    {
        if (result.RejectCode is { } code)
            context.Reject(code, result.RejectMessage ?? "電腦不接受這個指令", RemoteGateSteps.Handler);
        else
            context.Complete(result.Message);
    }

    /// <summary>JSON 字串或 JSON null（<c>(string?)null</c> 隱含轉成的 JValue 型別是 String）</summary>
    private static JToken TextOrNull(string? value) => value is null ? JValue.CreateNull() : new JValue(value);

    // ───────────────────────────── listFiles（D33、D34） ─────────────────────────────

    private async Task RemoteListFilesAsync(RemoteViewContext context, RemoteFileSnapshot s, string? requestId, string? path, bool dirsOnly)
    {
        RemoteFilesResult result;
        try
        {
            result = await Task.Run(() => ListRemoteFilesCoreAsync(s, requestId, path, dirsOnly, CancellationToken.None));
        }
        catch (Exception ex)
        {
            FailRemoteFiles(context, ListFilesCommand, ex);
            return;
        }

        FinishRemoteFiles(context, result);
    }

    /// <summary>
    /// 背景：範圍（一般是工作資料夾與 attachments\；dirsOnly 是最近資料夾與工作資料夾）→ 守門員驗資料夾 → 列目錄（不列重新解析點）→
    /// 範圍內、不在拒絕清單的圖片與影片經傳輸取 <c>@list</c> 參照 → 邊加邊量（700 KB）
    /// </summary>
    private static async Task<RemoteFilesResult> ListRemoteFilesCoreAsync(RemoteFileSnapshot s, string? requestId, string? path, bool dirsOnly, CancellationToken ct)
    {
        EnsureDefaultWorkspace(s);
        IEnumerable<string?> roots = dirsOnly ? [.. s.RecentFolders, s.WorkingDirectory] : [s.WorkingDirectory, s.AttachmentDirectory];
        var scope = await BuildRemoteFileScopeAsync(s, roots, ct).ConfigureAwait(false);
        return await ListDirectoryCoreAsync(ListSourceOf(s), scope, requestId, path ?? (dirsOnly ? null : s.WorkingDirectory), dirsOnly,
            entries => ListRefsAsync(s, entries, ct), ct).ConfigureAwait(false);
    }

    // ───────────────────────────── listFolders（新對話的資料夾挑選） ─────────────────────────────

    private async Task RemoteListFoldersAsync(RemoteViewContext context, RemoteFolderSnapshot s, string? requestId, string? path)
    {
        RemoteFilesResult result;
        try
        {
            result = await Task.Run(() => ListRemoteFoldersCoreAsync(s, requestId, path, CancellationToken.None));
        }
        catch (Exception ex)
        {
            FailRemoteFiles(context, ListFoldersCommand, ex);
            return;
        }

        FinishRemoteFiles(context, result);
    }

    /// <summary>背景：範圍只有最近資料夾（還沒有對話：沒有工作資料夾，app 的資料夾整個在禁止清單），其餘同 listFiles 的 dirsOnly</summary>
    private static async Task<RemoteFilesResult> ListRemoteFoldersCoreAsync(RemoteFolderSnapshot s, string? requestId, string? path, CancellationToken ct)
    {
        var folders = await RemoteScopeBuilder.DeviceFoldersAsync(s.Folders, s.Guard.NormalizeRootAsync, ct).ConfigureAwait(false);
        var roots = await RemoteScopeBuilder.RootsAsync(s.RecentFolders, s.Guard.NormalizeRootAsync, ct).ConfigureAwait(false);
        var scope = new RemoteFileScope(string.Empty, roots, NoExactFiles, folders);
        return await ListDirectoryCoreAsync(new RemoteListSource(null, null, s.RecentFolders, s.Guard), scope, requestId, path, dirsOnly: true,
            _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 列一個資料夾（listFiles 與 listFolders 共用）：<paramref name="path"/> 是 null（只有 dirsOnly）列最近資料夾的虛擬根，
    /// 否則守門員驗資料夾 → 列目錄（不列重新解析點）→ 媒體經 <paramref name="refs"/> 取參照 → 邊加邊量（700 KB）
    /// </summary>
    private static async Task<RemoteFilesResult> ListDirectoryCoreAsync(RemoteListSource s, RemoteFileScope scope, string? requestId, string? path, bool dirsOnly,
        Func<IReadOnlyList<FileEntry>, Task<IReadOnlyDictionary<string, string>>> refs, CancellationToken ct)
    {
        if (path is null)
            return RemoteFilesResult.Send(await RecentFoldersMessageAsync(s, scope, requestId, ct).ConfigureAwait(false));

        var target = path;
        var check = await s.Guard.CheckDirectoryAsync(target, scope, ct).ConfigureAwait(false);
        if (check.Reason is { } reason)
        {
            if (RemoteListRules.RefusesPath(reason))
            {
                Log.Info($"遠端 listFiles 的資料夾被拒（{reason}）");
                return RemoteFilesResult.RefusedPath;
            }

            return RemoteFilesResult.Send(FilesMessage(s, requestId, target, RemoteListRules.Parent(target, scope.ListRoots), [], false, RemoteListRules.ListError(reason)));
        }

        var directory = check.Directory!;
        List<FileEntry> entries;
        bool truncated;
        try
        {
            (entries, truncated) = RemoteFileIndex.ListDirectory(directory, RemoteFileGuard.IsAllowedReparsePoint, dirsOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Info($"遠端 listFiles 讀不到資料夾：{ex.Message}");
            return RemoteFilesResult.Send(FilesMessage(s, requestId, directory, RemoteListRules.Parent(directory, scope.ListRoots), [], false,
                RemoteListRules.ListError(ex is DirectoryNotFoundException ? RemoteFileGuardReasons.NotFound : RemoteFileGuardReasons.Unreadable)));
        }

        var mediaRefs = dirsOnly ? new Dictionary<string, string>() : await refs(entries).ConfigureAwait(false);
        var rendered = entries.Select(e => RenderRemoteEntry(e, mediaRefs.GetValueOrDefault(e.Path)));
        var (fitted, cut) = RemoteListRules.Fit(rendered, RemoteListRules.MaxOutputBytes);
        return RemoteFilesResult.Send(FilesMessage(s, requestId, directory, RemoteListRules.Parent(directory, scope.ListRoots), fitted, truncated || cut, null));
    }

    /// <summary>dirsOnly、path 是 null：最近資料夾的虛擬根（守門員驗得過的才列，名稱是完整路徑；parent 是 null）</summary>
    private static async Task<JObject> RecentFoldersMessageAsync(RemoteListSource s, RemoteFileScope scope, string? requestId, CancellationToken ct)
    {
        var entries = new List<JObject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in s.RecentFolders)
        {
            var check = await s.Guard.CheckDirectoryAsync(folder, scope, ct).ConfigureAwait(false);
            if (check.Directory is not { } directory || !seen.Add(directory))
                continue;
            DateTime modified;
            try
            {
                modified = Directory.GetLastWriteTime(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                modified = DateTime.MinValue;
            }

            entries.Add(RenderRemoteEntry(new FileEntry(directory, directory, "dir", null, modified, false), null));
        }

        var (fitted, cut) = RemoteListRules.Fit(entries, RemoteListRules.MaxOutputBytes);
        return FilesMessage(s, requestId, null, null, fitted, cut, null);
    }

    /// <summary>可以給遠端看的圖片與影片（不在拒絕清單）→ 參照；沒有傳輸、引擎寫不進去時沒有參照（照樣列名稱）</summary>
    private static async Task<IReadOnlyDictionary<string, string>> ListRefsAsync(RemoteFileSnapshot s, IReadOnlyList<FileEntry> entries, CancellationToken ct)
    {
        var media = entries
            .Where(e => e.Kind is "image" or "video" && AgentAttachmentRules.IsDisplayableMedia(e.Name) && !RemoteFileGuardRules.IsForbiddenFileName(e.Path))
            .Select(e => e.Path)
            .ToList();
        if (media.Count == 0 || s.Transfer is not { CanTransfer: true } transfer)
            return new Dictionary<string, string>();
        try
        {
            var refs = await transfer.RegisterRefsAsync(s.WireId, MediaServerRules.ListItemId, media, ct).ConfigureAwait(false);
            if (refs.Count != media.Count)
                throw new InvalidOperationException($"傳輸回的參照數（{refs.Count}）和路徑數（{media.Count}）不一樣");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < media.Count; i++)
                map[media[i]] = refs[i];
            return map;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            Log.Warn($"抽屜的媒體參照寫不進去（這次不給縮圖）：{ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>一個 entry：同本機的 FileEntry，但沒有 url、thumbUrl（伺服器以 ref 換成本站網址）；媒體另帶 ref 與 ver（修改時間，縮圖網址的 v）</summary>
    private static JObject RenderRemoteEntry(FileEntry e, string? mediaRef)
    {
        var entry = new JObject
        {
            ["name"] = e.Name,
            ["path"] = e.Path,
            ["kind"] = e.Kind,
            ["size"] = e.Size,
            ["modified"] = e.Modified == DateTime.MinValue ? null : ToUnixMs(e.Modified),
            ["hidden"] = e.Hidden,
        };
        if (mediaRef != null)
        {
            entry["ref"] = mediaRef;
            if (RemoteListRules.Version(e.Modified) is { } version)
                entry["ver"] = version;
        }

        return entry;
    }

    private static JObject FilesMessage(RemoteListSource s, string? requestId, string? path, string? parent, JArray entries, bool truncated, string? error)
        => new()
        {
            ["type"] = "files",
            ["requestId"] = TextOrNull(requestId),
            ["conversationId"] = TextOrNull(s.WireId),
            ["path"] = TextOrNull(path),
            ["parent"] = TextOrNull(parent),
            ["root"] = TextOrNull(s.Root),
            ["entries"] = entries,
            ["truncated"] = truncated,
            ["error"] = TextOrNull(error),
        };

    // ───────────────────────────── fileSearch（D35） ─────────────────────────────

    private async Task RemoteFileSearchAsync(RemoteViewContext context, RemoteFileSnapshot s, string? requestId, string query)
    {
        var index = remoteFileIndex ??= new RemoteFileIndex();
        RemoteFilesResult result;
        try
        {
            result = await Task.Run(() => FileSearchCoreAsync(s, index, requestId, query, CancellationToken.None));
        }
        catch (Exception ex)
        {
            FailRemoteFiles(context, FileSearchCommand, ex);
            return;
        }

        FinishRemoteFiles(context, result);
    }

    /// <summary>背景：工作資料夾經守門員驗過（真實路徑）→ 遠端專用的索引（不走進 junction、禁止的資料夾，不收拒絕清單的檔名）</summary>
    private static async Task<RemoteFilesResult> FileSearchCoreAsync(RemoteFileSnapshot s, RemoteFileIndex index, string? requestId, string query, CancellationToken ct)
    {
        EnsureDefaultWorkspace(s);
        var scope = await BuildRemoteFileScopeAsync(s, [s.WorkingDirectory, s.AttachmentDirectory], ct).ConfigureAwait(false);
        var check = await s.Guard.CheckDirectoryAsync(s.WorkingDirectory, scope, ct).ConfigureAwait(false);
        List<FileSearchResult> results;
        if (check.Reason is { } reason)
        {
            if (RemoteListRules.RefusesPath(reason))
            {
                Log.Info($"遠端 fileSearch 的工作資料夾被拒（{reason}）");
                return RemoteFilesResult.RefusedPath;
            }

            results = [];
        }
        else
        {
            // 子資料夾和工作資料夾在同一個磁碟（掛載點是重新解析點，索引不會走進去），以固定磁碟判斷禁止清單
            results = await index.SearchAsync(check.Directory!, query,
                directory => RemoteFileGuardRules.CheckDirectoryString(directory, scope, RemoteDriveKind.Fixed) != null, ct).ConfigureAwait(false);
        }

        return RemoteFilesResult.Send(new JObject
        {
            ["type"] = "fileSearch",
            ["requestId"] = TextOrNull(requestId),
            ["conversationId"] = s.WireId,
            ["query"] = query,
            ["results"] = new JArray(results.Select(r => new JObject
            {
                ["relativePath"] = r.RelativePath,
                ["fullPath"] = r.FullPath,
                ["kind"] = r.Kind,
            })),
        });
    }

    // ───────────────────────────── fileFetch（D36） ─────────────────────────────

    private void StartRemoteFileFetch(RemoteViewContext context, RemoteFileSnapshot s, string? requestId, string path)
    {
        if (s.Transfer is not { CanTransfer: true })
        {
            context.Reject(RemoteResultCodes.Unsupported, "這台電腦目前沒有和伺服器連線（或伺服器不支援媒體），不能從網頁讀檔", RemoteGateSteps.Handler);
            return;
        }

        _ = RemoteFileFetchAsync(context, s, requestId, path);
    }

    private async Task RemoteFileFetchAsync(RemoteViewContext context, RemoteFileSnapshot s, string? requestId, string path)
    {
        var now = RemoteFilesTime.GetUtcNow().ToUnixTimeMilliseconds();
        RemoteFilesResult result;
        try
        {
            result = await Task.Run(() => FileFetchCoreAsync(s, requestId, path, now, CancellationToken.None));
        }
        catch (Exception ex)
        {
            FailRemoteFiles(context, FileFetchCommand, ex);
            return;
        }

        FinishRemoteFiles(context, result);
    }

    /// <summary>
    /// 背景：守門員以 fileFetch 的範圍開檔（≤512 MB）→ 開頭就證明是二進位的（<see cref="MediaServerRules.IsProvenBinary"/>）不掃；其他要 ≤20 MB、
    /// 整份讀進來，文字類（副檔名、嚴格 UTF-8 或 UTF-16／UTF-32 的 BOM）整份跑權杖 DLP
    /// （<see cref="MediaServerRules.HasLeak"/>：UTF-8 之外也照 BOM 或 UTF-16 解碼掃），命中就回帶 error 的 fileRef →
    /// 經傳輸發 <c>@file</c> 參照，<c>expiresAt</c> 是現在加 15 分鐘。轉送時 MediaServer 會再查一次（檔案可能改過）
    /// </summary>
    private static async Task<RemoteFilesResult> FileFetchCoreAsync(RemoteFileSnapshot s, string? requestId, string path, long nowMs, CancellationToken ct)
    {
        var scope = await BuildRemoteFileScopeAsync(s, [s.WorkingDirectory, s.AttachmentDirectory], ct).ConfigureAwait(false);
        var opened = await s.Guard.OpenAsync(path, RemoteFilePurpose.File, scope, MediaServerRules.MaxBinaryFileBytes, ct).ConfigureAwait(false);
        if (opened.File is not { } file)
        {
            if (RemoteListRules.RefusesPath(opened.Reason!))
            {
                Log.Info($"遠端 fileFetch 的路徑被拒（{opened.Reason}）");
                return RemoteFilesResult.RefusedPath;
            }

            Log.Info($"遠端 fileFetch 的檔案不給（{opened.Reason}）");
            return RemoteFilesResult.Send(FileRefError(s, requestId));
        }

        string finalPath;
        long length;
        using (file)
        {
            finalPath = file.FinalPath;
            length = file.Length;
            var prefix = await file.ReadPrefixAsync(MediaServerRules.BinaryProbeBytes, ct).ConfigureAwait(false);
            if (!MediaServerRules.IsProvenBinary(finalPath, prefix))
            {
                if (length > MediaServerRules.MaxFileBytes)
                {
                    Log.Info("遠端 fileFetch 的檔案可能是文字而且超過 20 MB：不給（too_large）");
                    return RemoteFilesResult.Send(FileRefError(s, requestId));
                }

                var content = new byte[checked((int)length)];
                file.Stream.Position = 0;
                await file.Stream.ReadExactlyAsync(content, ct).ConfigureAwait(false);
                if (MediaServerRules.IsText(finalPath, content) && MediaServerRules.HasLeak(content))
                {
                    Log.Warn("遠端 fileFetch 的檔案含疑似憑證：不給（dlp）");
                    return RemoteFilesResult.Send(FileRefError(s, requestId));
                }
            }
        }

        IReadOnlyList<string> refs;
        try
        {
            refs = await s.Transfer!.RegisterRefsAsync(s.WireId, MediaServerRules.FileItemId, [finalPath], ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn($"fileFetch 的參照寫不進去：{ex.Message}");
            return RemoteFilesResult.NoTransfer;
        }

        if (refs.Count != 1)
            throw new InvalidOperationException($"傳輸回的參照數（{refs.Count}）不是 1");
        return RemoteFilesResult.Send(new JObject
        {
            ["type"] = "fileRef",
            ["requestId"] = TextOrNull(requestId),
            ["conversationId"] = s.WireId,
            ["ref"] = refs[0],
            ["name"] = Path.GetFileName(finalPath),
            ["size"] = length,
            ["expiresAt"] = nowMs + (long)LooseMediaRefRules.FileLifetime.TotalMilliseconds,
            ["error"] = JValue.CreateNull(),
        });
    }

    /// <summary>不給的檔（太大、拒絕清單、含憑證、讀不到…）：fileRef 只帶固定的錯誤文字（WPR：error 不為 null 時其他欄位省略）</summary>
    private static JObject FileRefError(RemoteFileSnapshot s, string? requestId) => new()
    {
        ["type"] = "fileRef",
        ["requestId"] = TextOrNull(requestId),
        ["conversationId"] = s.WireId,
        ["error"] = RemoteListRules.FetchRefused,
    };

    // ───────────────────────────── addUploaded 與 BeginRemoteUploads（D22） ─────────────────────────────

    /// <summary>指令的 <c>uploads</c>（閘門已照 RemoteCommandShapeRules 驗過形狀；不是物件的略過，其他欄位由 <see cref="StartRemoteUploads"/> 再驗一次）</summary>
    private static List<RemoteUpload> ParseRemoteUploads(JArray? uploads) => (uploads ?? [])
        .OfType<JObject>()
        .Select(o => new RemoteUpload(o.Value<string>("uploadId")!, o.Value<string>("name")!,
            o["size"] is JValue { Type: JTokenType.Integer, Value: long size } ? size : -1, o.Value<string>("sha256")!))
        .ToList();

    private void HandleAddUploaded(RemoteViewContext context, RemoteFileSnapshot s, JObject command)
    {
        var uploads = ParseRemoteUploads(command["uploads"] as JArray);
        if (StartRemoteUploads(s.Conversation, uploads) is { } refused)
        {
            context.Reject(refused.Code, refused.Message, RemoteGateSteps.Handler);
            return;
        }

        // 中樞已接受（下載與匯入在背景，512 MB 等不到 30 秒的期限；下載期間送出被拒）
        context.Complete();
    }

    /// <summary>
    /// UI 執行緒：檢查傳輸與名額、先把計數加上（ImportingCount 加筆數，下載期間送出被拒），開始背景下載；不收回拒絕的代碼與訊息
    /// </summary>
    private (string Code, string Message)? StartRemoteUploads(ConversationState c, IReadOnlyList<RemoteUpload> uploads)
    {
        if (disposed)
            return (RemoteResultCodes.Busy, "電腦的程式正在關閉");
        if (uploads.Count == 0)
            return (RemoteResultCodes.Invalid, "沒有要加的上傳");
        // 閘門已照 RemoteCommandShapeRules 驗過；接縫的呼叫端（WP4.4）也可能直接傳進來，這裡再擋一次（id 會當成暫存資料夾名）
        if (uploads.Any(u => u is null || !UploadIdRules.IsValid(u.UploadId) || string.IsNullOrEmpty(u.Name) || !BlobIdRules.IsBlobId(u.Sha256)
                             || u.Size is < 0 or > AgentAttachmentRules.MaxAttachmentBytes))
            return (RemoteResultCodes.Invalid, "上傳的檔案資料不合格");
        if (CurrentRemoteFileTransfer() is not { CanTransfer: true } transfer)
            return (RemoteResultCodes.Unsupported, "這台電腦目前沒有和伺服器連線（或伺服器不支援媒體），收不到網頁上傳的檔案");
        var ids = uploads.Select(u => u.UploadId).ToList();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            return (RemoteResultCodes.Invalid, "同一個上傳出現兩次");
        if (ids.Any(remoteUploadsInFlight.Contains))
            return (RemoteResultCodes.Busy, "這個上傳的檔案正在下載，請稍候");
        var room = AgentAttachmentRules.MaxAttachmentsPerMessage - c.PendingAttachments.Count - c.ImportingCount;
        if (uploads.Count > room)
            return (RemoteResultCodes.Invalid, $"每則訊息最多附 {AgentAttachmentRules.MaxAttachmentsPerMessage} 個檔案");

        c.ImportingCount += uploads.Count;
        c.RemoteUploadBatches++;
        foreach (var id in ids)
            remoteUploadsInFlight.Add(id);
        PostComposer(c, uploads.Count == 1 ? $"正在下載網頁上傳的「{uploads[0].Name}」…" : $"正在下載網頁上傳的 {uploads.Count} 個檔案…");
        _ = ReceiveRemoteUploadsAsync(c, uploads, transfer);
        return null;
    }

    /// <summary>背景下載的結果：全部成功時 <see cref="Failure"/> 是 null；有任何一個失敗就整批不加（暫存一律刪掉）</summary>
    private sealed record UploadDownloads(List<(RemoteUpload Upload, string File)> Files, List<string> Directories, string? Failure);

    private async Task ReceiveRemoteUploadsAsync(ConversationState c, IReadOnlyList<RemoteUpload> uploads, IRemoteFileTransfer transfer)
    {
        try
        {
            await ReceiveRemoteUploadBatchAsync(c, uploads, transfer);
        }
        finally
        {
            FinishRemoteUploadBatch(c);
        }
    }

    /// <summary>
    /// 一批上傳處理完（UI 執行緒；成功、失敗、對話不在了都會到這裡）：沒有別批在處理時，做延後的事（conversationCreate 的第一則）；
    /// 對話已不在（或程式正在關閉）就丟掉不做
    /// </summary>
    private void FinishRemoteUploadBatch(ConversationState c)
    {
        c.RemoteUploadBatches--;
        if (c.RemoteUploadBatches > 0 || c.AfterRemoteUploads.Count == 0)
            return;
        var pending = c.AfterRemoteUploads.ToList();
        c.AfterRemoteUploads.Clear();
        if (disposed || !conversations.TryGetValue(c.Meta.Id, out var current) || !ReferenceEquals(current, c))
            return;
        foreach (var action in pending)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error($"對話 {c.Meta.Id} 的上傳處理完之後的動作出錯", ex);
            }
        }
    }

    private async Task ReceiveRemoteUploadBatchAsync(ConversationState c, IReadOnlyList<RemoteUpload> uploads, IRemoteFileTransfer transfer)
    {
        var cache = UploadCacheDirectory;
        var freeSpace = UploadFreeSpaceForTest ?? MeasureFreeSpace;
        UploadDownloads result;
        try
        {
            result = await Task.Run(() => DownloadRemoteUploadsAsync(uploads, transfer, cache, freeSpace, CancellationToken.None));
        }
        catch (Exception ex)
        {
            Log.Error($"對話 {c.Meta.Id} 的上傳下載途中出錯", ex);
            result = new UploadDownloads([], uploads.Select(u => Path.Combine(cache, u.UploadId)).ToList(), "下載途中出錯");
        }

        // 先減回自己的計數（之後 ImportAsync 自己會計數，名額不會被吃掉）
        c.ImportingCount -= uploads.Count;
        foreach (var upload in uploads)
            remoteUploadsInFlight.Remove(upload.UploadId);
        var live = !disposed && conversations.TryGetValue(c.Meta.Id, out var current) && ReferenceEquals(current, c);
        if (!live)
        {
            Log.Info($"上傳下載完時對話 {c.Meta.Id} 已不在（或程式正在關閉）：丟掉暫存");
            DeleteUploadTempInBackground(result.Directories);
            return;
        }

        if (result.Failure is { } failure)
        {
            DeleteUploadTempInBackground(result.Directories);
            Log.Warn($"對話 {c.Meta.Id} 的上傳沒有加進附件：{failure}");
            AddItem(c, new AgentChatItem
            {
                Kind = AgentItemKind.Notice,
                Text = $"從網頁上傳的檔案沒有加進附件：{failure}",
            });
            SaveConversation(c, saveItems: true);
            PostComposer(c);
            Toast($"從網頁上傳的檔案沒有加進附件：{failure}", isError: true);
            return;
        }

        PostComposer(c);
        var attachments = store.GetAttachmentDirectory(c.Meta.Id);
        var modelImages = store.GetModelImageDirectory(c.Meta.Id);
        var imports = result.Files
            .Select(f => ImportRemoteUploadAsync(c, f.File, attachments, modelImages, f.Upload.UploadId))
            .ToList();
        try
        {
            await Task.WhenAll(imports);
        }
        finally
        {
            DeleteUploadTempInBackground(result.Directories);
        }
    }

    /// <summary>
    /// 匯入一個下載好的上傳（同 <c>AgentHub.Files.cs</c> 的 ImportAsync：計數、進度文字、對話不在時刪資料夾；失敗跳 toast），
    /// 另把成為待送附件的 id 記進 <see cref="ConversationState.RemoteUploadedAttachmentIds"/>（遠端 send 只帶這些）
    /// </summary>
    private async Task ImportRemoteUploadAsync(ConversationState c, string file, string attachmentDirectory, string modelImageDirectory, string uploadId)
    {
        var displayName = Path.GetFileName(file);
        c.ImportingCount++;
        PostComposer(c, $"正在處理「{displayName}」…");
        try
        {
            var attachment = await Task.Run(() => AgentAttachmentProcessor.ImportFileAsync(file, attachmentDirectory, modelImageDirectory, CancellationToken.None));
            if (conversations.TryGetValue(c.Meta.Id, out var live) && ReferenceEquals(live, c))
            {
                c.PendingAttachments.Add(attachment);
                c.RemoteUploadedAttachmentIds.Add(attachment.Id);
                c.RemoteUploadAttachments[uploadId] = attachment.Id;
            }
            else
            {
                DeleteFolderInBackground(c.Meta.Id, quiet: true);   // 匯入期間對話被刪：不留下孤兒資料夾
            }
        }
        catch (Exception ex)
        {
            Toast($"附加「{displayName}」失敗：{ex.Message}", isError: true);
        }
        finally
        {
            c.ImportingCount--;
            if (!disposed)
                PostComposer(c);
        }
    }

    /// <summary>
    /// 背景：先確認磁碟空間（可用 ≥ 合計＋1 GB），再逐一下載到 <c>cache\uploads\{uploadId}\{安全檔名}</c>（檔名清理之後必須還在那個資料夾裡），
    /// 傳輸驗大小與 SHA-256；任何一個失敗就停（已下載的也不要）
    /// </summary>
    private static async Task<UploadDownloads> DownloadRemoteUploadsAsync(IReadOnlyList<RemoteUpload> uploads, IRemoteFileTransfer transfer, string cache,
        Func<string, long?> freeSpace, CancellationToken ct)
    {
        var files = new List<(RemoteUpload, string)>();
        var directories = new List<string>();
        var total = uploads.Sum(u => u.Size);
        Directory.CreateDirectory(cache);
        if (!UploadNameRules.HasDiskRoom(freeSpace(cache), total))
            return new UploadDownloads(files, directories, $"這台電腦的磁碟空間不夠（要 {AgentAttachmentRules.FormatSize(total)}，另外至少要留 1 GB）");

        foreach (var upload in uploads)
        {
            var directory = Path.Combine(cache, upload.UploadId);
            var name = UploadNameRules.SafeName(upload.Name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var file = Path.Combine(directory, name);
            if (!UploadNameRules.IsUnder(directory, cache) || !UploadNameRules.IsUnder(file, directory))
                return new UploadDownloads(files, directories, $"「{name}」的檔名不合格");
            directories.Add(directory);
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);   // 上次沒清掉的暫存（當機、關閉）
                Directory.CreateDirectory(directory);
                await transfer.DownloadUploadAsync(upload, file, ct).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                return new UploadDownloads(files, directories, $"「{name}」下載後的內容和上傳的不一樣（{ex.Message}）");
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            {
                return new UploadDownloads(files, directories, $"「{name}」下載失敗：{ex.Message}");
            }

            files.Add((upload, file));
        }

        return new UploadDownloads(files, directories, null);
    }

    /// <summary>暫存資料夾所在磁碟的可用空間；量不到回 null（當成不夠）</summary>
    private static long? MeasureFreeSpace(string directory)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(directory)) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 啟動時清掉上次留下的上傳暫存（合併前審查 R6）：下載或匯入途中程式被關閉、當機或重開機時，<c>cache\uploads\{uploadId}\</c> 不會被刪；
    /// 網頁每次上傳的 id 都是新的，下載前「同一個 id 先刪」永遠輪不到。AppController 建好中樞、同步接上之前在背景呼叫一次（這時沒有任何上傳在下載）
    /// </summary>
    internal static void CleanStaleUploadTemp() => CleanStaleUploadTemp(UploadCacheDirectory);

    /// <summary><paramref name="cache"/> 底下名稱是上傳 id（<see cref="UploadIdRules.IsValid"/>）的資料夾整個刪掉；刪不掉的只記錄。回刪掉的個數</summary>
    internal static int CleanStaleUploadTemp(string cache)
    {
        List<string> directories;
        try
        {
            if (!Directory.Exists(cache))
                return 0;
            directories = Directory.EnumerateDirectories(cache).Where(d => UploadIdRules.IsValid(Path.GetFileName(d))).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"列不出上傳的暫存資料夾：{ex.Message}");
            return 0;
        }

        var removed = 0;
        foreach (var directory in directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"刪不掉上次留下的上傳暫存：{ex.Message}");
            }
        }

        if (removed > 0)
            Log.Info($"清掉上次留下的上傳暫存 {removed} 個");
        return removed;
    }

    /// <summary>刪上傳的暫存資料夾（背景；刪不掉只記錄，下次啟動時 <see cref="CleanStaleUploadTemp()"/> 再刪）</summary>
    private static void DeleteUploadTempInBackground(IReadOnlyList<string> directories)
    {
        if (directories.Count == 0)
            return;
        _ = Task.Run(() =>
        {
            foreach (var directory in directories)
            {
                try
                {
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"刪不掉上傳的暫存資料夾：{ex.Message}");
                }
            }
        });
    }
}
