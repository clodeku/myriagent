using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Files;
using Myriagent.Media;
using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 電腦狀態面板的檔案傳輸（sync-protocol 14.15）在被操作那台的處理者：
// deviceBrowse 列整台本機磁碟的一個資料夾（path 是 null＝磁碟與常用資料夾）、deviceFetch 發 @xfer 的下載參照（2 小時）、
// devicePut 把網頁或其他電腦上傳到伺服器的檔下載進指定的資料夾（同名自動改名、不覆寫）。路由 Device、層級 2：閘門 5e 只收「可以操作」，
// app 以系統管理員執行時一律拒絕。檔案系統與網路都在背景，結果回 UI 執行緒才送（同 AgentHub.RemoteFiles.cs）
internal sealed partial class AgentHub
{
    private static readonly Lazy<bool> ProcessElevated = new(DeviceTransferRules.ProcessIsElevated);

    private readonly DeviceXferBoard deviceXfers = new();

    /// <summary>收檔提示的合併（UI 執行緒）</summary>
    private PutToast putToast = new();

    private sealed class PutToast
    {
        public int Saved { get; set; }

        public int Failed { get; set; }

        public string Issuer { get; set; } = string.Empty;

        /// <summary>存放的資料夾（都同一個時才寫出來；不同的是空字串）</summary>
        public string? Directory { get; set; }

        public int Version { get; set; }
    }

    /// <summary>測試接縫：app 是不是以系統管理員執行</summary>
    internal Func<bool>? DeviceFilesElevatedForTest { get; set; }

    /// <summary>測試接縫：這台的磁碟根目錄（範圍的根）</summary>
    internal Func<IReadOnlyList<string>>? DeviceDriveRootsForTest { get; set; }

    /// <summary>測試接縫：虛擬根的常用資料夾</summary>
    internal Func<IReadOnlyList<(string Name, string Path)>>? DevicePlacesForTest { get; set; }

    /// <summary>測試接縫：網路芳鄰（解析主機、網路磁碟機、電腦與分享的清單）</summary>
    internal INetworkShares? DeviceNetworkForTest { get; set; }

    /// <summary>測試：接收中與剛收完的檔</summary>
    internal DeviceXferBoard DeviceXfers => deviceXfers;

    /// <summary>
    /// 這台正在和其他電腦傳檔案（電腦狀態面板的檔案傳輸；UI 執行緒）：從別台下載（peerXferSave）、上傳給別台（peerUpload）、
    /// 接收別台送來的（devicePut）、替別台送檔案的分段轉送（FileTransferActivity）。app 自我更新以它擋住自動重啟
    /// （AppRestartBlocker.FileTransfer；2026-11-10：來源那台在下載途中自動換版，正在轉送的那一段被切斷）
    /// </summary>
    internal bool FileTransferActive => peerXfers.Count > 0 || peerUploads.Count > 0 || deviceXfers.AnyActive() || fileTransferActivity();

    /// <summary>
    /// 替別台送檔案的分段轉送與同步層的下載、上傳（整個程序共用的 Sync.FileTransferActivity）。中樞預設不看（測試裡別的測試做過的傳輸
    /// 不能算進來），只有 AppController 以 <see cref="AttachFileTransferActivity"/> 接上
    /// </summary>
    private Func<bool> fileTransferActivity = () => false;

    internal void AttachFileTransferActivity(Func<bool> active) => fileTransferActivity = active ?? throw new ArgumentNullException(nameof(active));

    /// <summary>給電腦狀態的接收進度（任何執行緒；沒有是 null）</summary>
    internal IReadOnlyList<XferStats>? DeviceXferSnapshot() => deviceXfers.Snapshot();

    private bool DeviceFilesElevated => DeviceFilesElevatedForTest?.Invoke() ?? ProcessElevated.Value;

    private void RegisterDeviceFileRemoteHandlers(List<IRemoteCommandHandler> into) => into.Add(new DeviceFilesRemoteHandler(this));

    /// <summary>UI 執行緒取的快照（之後在背景用）</summary>
    private sealed record DeviceFilesSnapshot(MediaFolderRoots Folders, IReadOnlyList<string> Drives, IReadOnlyList<(string Name, string Path)> Places,
        RemoteFileGuard Guard, IRemoteFileTransfer? Transfer, NetworkPathGuard Network);

    private DeviceFilesSnapshot SnapshotDeviceFiles()
    {
        var folders = CurrentMediaFolderRoots();
        return new DeviceFilesSnapshot(
            folders,
            DeviceDriveRootsForTest?.Invoke() ?? DeviceTransferRules.FixedDriveRoots(),
            DevicePlacesForTest?.Invoke() ?? DeviceTransferRules.Places(folders.UserProfile,
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            RemoteGuard,
            CurrentRemoteFileTransfer(),
            new NetworkPathGuard(DeviceNetworkForTest ?? NetworkShares.Instance, NetworkPathGuard.DefaultTimeout));
    }

    /// <summary>三個指令的處理者（其他指令回 false）</summary>
    private sealed class DeviceFilesRemoteHandler(AgentHub hub) : IRemoteCommandHandler
    {
        public bool TryHandle(RemoteViewContext context, IAgentView view, string cmd, JObject command)
        {
            if (!RemoteCommandCatalog.DeviceTransferCommands.Contains(cmd))
                return false;
            if (!context.Authorized)
            {
                context.Reject(RemoteResultCodes.Invalid, "這個指令沒有經過這台電腦的遠端操作檢查", RemoteGateSteps.Handler);
                return true;
            }

            if (hub.DeviceFilesElevated)
            {
                context.Reject(RemoteResultCodes.Policy, DeviceTransferRules.ElevatedMessage, RemoteGateSteps.Handler);
                return true;
            }

            var snapshot = hub.SnapshotDeviceFiles();
            var requestId = command.Value<string>("requestId");
            switch (cmd)
            {
                case RemoteCommandCatalog.DeviceBrowse:
                    _ = hub.DeviceBrowseAsync(context, snapshot, requestId, command["path"] is JValue { Type: JTokenType.String, Value: string path } ? path : null);
                    break;
                case RemoteCommandCatalog.DeviceFetch:
                    if (snapshot.Transfer is not { CanTransfer: true })
                    {
                        context.Reject(RemoteResultCodes.Unsupported, DeviceTransferRules.NoTransferMessage, RemoteGateSteps.Handler);
                        break;
                    }

                    _ = hub.DeviceFetchAsync(context, snapshot, requestId, command.Value<string>("path") ?? string.Empty);
                    break;
                case RemoteCommandCatalog.DeviceTree:
                    _ = hub.DeviceTreeAsync(context, snapshot, requestId, command.Value<string>("path") ?? string.Empty);
                    break;
                case RemoteCommandCatalog.DeviceFetchMany:
                    if (snapshot.Transfer is not { CanTransfer: true })
                    {
                        context.Reject(RemoteResultCodes.Unsupported, DeviceTransferRules.NoTransferMessage, RemoteGateSteps.Handler);
                        break;
                    }

                    _ = hub.DeviceFetchManyAsync(context, snapshot, requestId, command.Value<string>("root") ?? string.Empty, command.Value<string>("pathsJson") ?? "[]");
                    break;
                default:
                    hub.StartDevicePut(context, snapshot, command);
                    break;
            }

            return true;
        }
    }

    // ───────────────────────────── deviceBrowse ─────────────────────────────

    private async Task DeviceBrowseAsync(RemoteViewContext context, DeviceFilesSnapshot s, string? requestId, string? path)
    {
        RemoteFilesResult result;
        try
        {
            result = await Task.Run(() => DeviceBrowseCoreAsync(s, requestId, path, CancellationToken.None));
        }
        catch (Exception ex)
        {
            FailRemoteFiles(context, RemoteCommandCatalog.DeviceBrowse, ex);
            return;
        }

        FinishRemoteFiles(context, result);
    }

    /// <summary>背景：path 是 null 列磁碟與常用資料夾；否則守門員驗資料夾、列資料夾與檔案（同 listFiles，但範圍是整台的本機磁碟、不發媒體參照）</summary>
    private static async Task<RemoteFilesResult> DeviceBrowseCoreAsync(DeviceFilesSnapshot s, string? requestId, string? path, CancellationToken ct)
    {
        var scope = await DeviceTransferScope.BuildAsync(s.Folders, s.Drives, s.Guard, ct).ConfigureAwait(false);
        var source = new RemoteListSource(null, null, [], s.Guard);
        if (path is null)
            return RemoteFilesResult.Send(await DeviceRootMessageAsync(source, s, scope, requestId, ct).ConfigureAwait(false));
        // 網路芳鄰（2026-11-09）：「網路」、\\電腦、\\電腦\分享\…、網路磁碟機上的路徑
        if (s.Network.IsNetworkPath(path))
            return RemoteFilesResult.Send(await NetworkBrowseAsync(source, s.Network, requestId, path, ct).ConfigureAwait(false));
        var result = await ListDirectoryCoreAsync(source, scope, requestId, path, dirsOnly: false,
            _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()), ct).ConfigureAwait(false);
        return ReferenceEquals(result, RemoteFilesResult.RefusedPath) ? DeviceRefusedPath : result;
    }

    private static RemoteFilesResult DeviceRefusedPath { get; } = new(RemoteResultCodes.Policy, DeviceTransferRules.RefusedPathMessage, null);

    /// <summary>虛擬根：磁碟（名稱是標籤加代號，例如「本機磁碟 (C:)」）與守門員驗得過的常用資料夾；parent、path 都是 null</summary>
    private static async Task<JObject> DeviceRootMessageAsync(RemoteListSource source, DeviceFilesSnapshot s, RemoteFileScope scope, string? requestId, CancellationToken ct)
    {
        var entries = new JArray();
        foreach (var root in scope.ListRoots.Where(DeviceTransferRules.IsDriveRoot))
        {
            string? label = null;
            try
            {
                label = new DriveInfo(root).VolumeLabel;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 讀不到標籤：照代號
            }

            entries.Add(RenderRemoteEntry(new FileEntry(DeviceTransferRules.DriveLabel(root, label), root, "dir", null, DateTime.MinValue, false), null));
        }

        foreach (var (name, path) in s.Places)
        {
            var check = await s.Guard.CheckDirectoryAsync(path, scope, ct).ConfigureAwait(false);
            if (check.Directory is { } directory)
                entries.Add(RenderRemoteEntry(new FileEntry(name, directory, "dir", null, DateTime.MinValue, false), null));
        }

        // 網路芳鄰：對應的網路磁碟機（「Z: (\\NAS\資料)」）與「網路」這一層
        var mapped = await s.Network.RunRefAsync(() => s.Network.Shares.MappedDrives(), ct).ConfigureAwait(false) ?? [];
        foreach (var (root, target) in mapped)
            entries.Add(RenderRemoteEntry(new FileEntry(root[..2] + " (" + target + ")", root, "dir", null, DateTime.MinValue, false), null));
        entries.Add(RenderRemoteEntry(new FileEntry("網路", NetworkPathRules.NetworkRoot, "dir", null, DateTime.MinValue, false), null));
        return FilesMessage(source, requestId, null, null, entries, false, null);
    }

    /// <summary>
    /// 背景：網路芳鄰的一層。「網路」列區域網路的電腦（網路探索），<c>\\電腦</c> 列它的資料夾分享，其他（分享以下、網路磁碟機上的路徑）列資料夾與檔案。
    /// 主機不在區域網路、是這台自己、逾時、不存在都以 <c>error</c> 說明（清單是空的）
    /// </summary>
    private static async Task<JObject> NetworkBrowseAsync(RemoteListSource source, NetworkPathGuard network, string? requestId, string path, CancellationToken ct)
    {
        var mapped = network.MappedTargetOf(path);
        var display = mapped is null ? NetworkPathRules.Normalize(path) ?? path : (path.Length > 3 ? path.TrimEnd(Path.DirectorySeparatorChar) : path);
        string? parent = mapped is null ? NetworkPathRules.Parent(display)
            : display.Length <= 3 ? null : Path.GetDirectoryName(display);
        JObject Error(string reason) => FilesMessage(source, requestId, display, parent, [], false, NetworkPathRules.Explain(reason));

        if (NetworkPathRules.IsNetworkRoot(display))
        {
            var computers = await network.RunRefAsync(() => network.Shares.Computers(), ct).ConfigureAwait(false);
            if (computers is null)
                return Error(NetworkPathReasons.Timeout);
            var list = new JArray(computers.Where(NetworkPathRules.IsHostName)
                .Select(c => RenderRemoteEntry(new FileEntry(c, NetworkPathRules.NetworkRoot + c, "dir", null, DateTime.MinValue, false), null)));
            return FilesMessage(source, requestId, display, null, list, false,
                list.Count == 0 ? NetworkPathRules.NoComputersMessage(NetworkDiscovery.LastSummary) : null);
        }

        if (mapped is null && NetworkPathRules.TryParse(display, out var host, out var share, out _) && share is null)
        {
            if (await network.CheckHostAsync(host, ct).ConfigureAwait(false) is { } refused)
                return Error(refused);
            var shares = await network.RunRefAsync(() => network.Shares.Shares(host), ct).ConfigureAwait(false);
            if (shares is null)
                return Error(NetworkPathReasons.Timeout);
            var list = new JArray(shares.Where(sh => !NetworkPathRules.IsBlockedShare(sh))
                .Select(sh => RenderRemoteEntry(new FileEntry(sh, display + @"\" + sh, "dir", null, DateTime.MinValue, false), null)));
            return FilesMessage(source, requestId, display, parent, list, false, list.Count == 0 ? "這台電腦沒有可以瀏覽的分享資料夾（或要先在那台登入過）" : null);
        }

        var (directory, reason) = await network.CheckDirectoryAsync(display, ct).ConfigureAwait(false);
        if (directory is null)
            return Error(reason ?? NetworkPathReasons.NotFound);
        var listed = await network.RunRefAsync<Tuple<List<FileEntry>, bool>?>(() =>
        {
            try
            {
                var (entries, truncated) = RemoteFileIndex.ListDirectory(directory, _ => false, dirsOnly: false);
                return Tuple.Create(entries, truncated);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Info($"列不出網路資料夾：{ex.Message}");
                return null;
            }
        }, ct).ConfigureAwait(false);
        if (listed is null)
            return Error(NetworkPathReasons.NotFound);
        var (fitted, cut) = RemoteListRules.Fit(listed.Item1.Select(e => RenderRemoteEntry(e, null)), RemoteListRules.MaxOutputBytes);
        return FilesMessage(source, requestId, directory, parent, fitted, listed.Item2 || cut, null);
    }

    // ───────────────────────────── deviceFetch ─────────────────────────────

    private async Task DeviceFetchAsync(RemoteViewContext context, DeviceFilesSnapshot s, string? requestId, string path)
    {
        var now = RemoteFilesTime.GetUtcNow().ToUnixTimeMilliseconds();
        RemoteFilesResult result;
        try
        {
            result = await Task.Run(() => DeviceFetchCoreAsync(s, requestId, path, now, CancellationToken.None));
        }
        catch (Exception ex)
        {
            FailRemoteFiles(context, RemoteCommandCatalog.DeviceFetch, ex);
            return;
        }

        FinishRemoteFiles(context, result);
    }

    /// <summary>背景：守門員以整台本機磁碟的範圍開檔（沒有大小上限、不做文字 DLP：擁有者自己挑的檔，憑證類檔名照樣擋）→ 發 <c>@xfer</c> 參照（2 小時）</summary>
    private static async Task<RemoteFilesResult> DeviceFetchCoreAsync(DeviceFilesSnapshot s, string? requestId, string path, long nowMs, CancellationToken ct)
    {
        if (s.Network.IsNetworkPath(path))
        {
            var (networkFile, networkReason) = await s.Network.OpenAsync(path, ct).ConfigureAwait(false);
            if (networkFile is null)
            {
                Log.Info($"deviceFetch 的網路路徑不給（{networkReason}）");
                return networkReason is NetworkPathReasons.NotFound or NetworkPathReasons.Name or NetworkPathReasons.Timeout
                    ? RemoteFilesResult.Send(DeviceFileRefError(requestId))
                    : new RemoteFilesResult(RemoteResultCodes.Policy, NetworkPathRules.Explain(networkReason!), null);
            }

            return await RegisterFetchAsync(s, requestId, networkFile, nowMs, ct).ConfigureAwait(false);
        }

        var scope = await DeviceTransferScope.BuildAsync(s.Folders, s.Drives, s.Guard, ct).ConfigureAwait(false);
        var opened = await s.Guard.OpenAsync(path, RemoteFilePurpose.File, scope, long.MaxValue, ct).ConfigureAwait(false);
        if (opened.File is not { } file)
        {
            if (RemoteListRules.RefusesPath(opened.Reason!))
            {
                Log.Info($"deviceFetch 的路徑被拒（{opened.Reason}）");
                return DeviceRefusedPath;
            }

            Log.Info($"deviceFetch 的檔案不給（{opened.Reason}）");
            return RemoteFilesResult.Send(DeviceFileRefError(requestId));
        }

        return await RegisterFetchAsync(s, requestId, file, nowMs, ct).ConfigureAwait(false);
    }

    /// <summary>開好的檔（本機或網路）→ 發 <c>@xfer</c> 參照、組 fileRef（檔案在這裡關掉，轉送時再開一次）</summary>
    private static async Task<RemoteFilesResult> RegisterFetchAsync(DeviceFilesSnapshot s, string? requestId, GuardedFile file, long nowMs, CancellationToken ct)
    {
        string finalPath;
        long length;
        using (file)
        {
            finalPath = file.FinalPath;
            length = file.Length;
        }

        IReadOnlyList<string> refs;
        try
        {
            refs = await s.Transfer!.RegisterRefsAsync(string.Empty, MediaServerRules.TransferItemId, [finalPath], ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn($"deviceFetch 的參照寫不進去：{ex.Message}");
            return new RemoteFilesResult(RemoteResultCodes.Unsupported, DeviceTransferRules.NoTransferMessage, null);
        }

        if (refs.Count != 1)
            throw new InvalidOperationException($"傳輸回的參照數（{refs.Count}）不是 1");
        return RemoteFilesResult.Send(new JObject
        {
            ["type"] = "fileRef",
            ["requestId"] = TextOrNull(requestId),
            ["conversationId"] = JValue.CreateNull(),
            ["ref"] = refs[0],
            ["name"] = Path.GetFileName(finalPath),
            ["size"] = length,
            ["expiresAt"] = nowMs + (long)LooseMediaRefRules.XferLifetime.TotalMilliseconds,
            ["error"] = JValue.CreateNull(),
        });
    }

    private static JObject DeviceFileRefError(string? requestId) => new()
    {
        ["type"] = "fileRef",
        ["requestId"] = TextOrNull(requestId),
        ["conversationId"] = JValue.CreateNull(),
        ["error"] = RemoteListRules.FetchRefused,
    };

    // ───────────────────────────── deviceTree／deviceFetchMany（整個資料夾） ─────────────────────────────

    private async Task DeviceTreeAsync(RemoteViewContext context, DeviceFilesSnapshot s, string? requestId, string path)
    {
        RemoteFilesResult result;
        try
        {
            result = await Task.Run(() => DeviceTreeCoreAsync(s, requestId, path, CancellationToken.None));
        }
        catch (Exception ex)
        {
            FailRemoteFiles(context, RemoteCommandCatalog.DeviceTree, ex);
            return;
        }

        FinishRemoteFiles(context, result);
    }

    /// <summary>
    /// 背景：驗資料夾（本機照守門員、網路照網路的守門），再逐層列底下所有的檔（相對路徑與大小）與資料夾：不進重新解析點與禁止清單的資料夾、
    /// 不列憑證類的檔名；最多 <see cref="DeviceFolderRules.MaxTreeFiles"/> 個檔、<see cref="DeviceFolderRules.TreeBudget"/>，超過標 truncated
    /// </summary>
    private static async Task<RemoteFilesResult> DeviceTreeCoreAsync(DeviceFilesSnapshot s, string? requestId, string path, CancellationToken ct)
    {
        JObject Tree(string? root, JArray files, JArray dirs, bool truncated, string? error) => new()
        {
            ["type"] = "fileTree",
            ["requestId"] = TextOrNull(requestId),
            ["conversationId"] = JValue.CreateNull(),
            ["root"] = TextOrNull(root),
            ["name"] = TextOrNull(root is null ? null : DeviceFolderRules.FolderName(root)),
            ["files"] = files,
            ["dirs"] = dirs,
            ["truncated"] = truncated,
            ["error"] = TextOrNull(error),
        };

        string root;
        Func<string, bool> forbiddenDirectory;
        var network = s.Network.IsNetworkPath(path);
        if (network)
        {
            var (directory, reason) = await s.Network.CheckDirectoryAsync(path, ct).ConfigureAwait(false);
            if (directory is null)
                return RemoteFilesResult.Send(Tree(null, [], [], false, NetworkPathRules.Explain(reason ?? NetworkPathReasons.NotFound)));
            root = directory;
            forbiddenDirectory = _ => false;
        }
        else
        {
            var scope = await DeviceTransferScope.BuildAsync(s.Folders, s.Drives, s.Guard, ct).ConfigureAwait(false);
            var check = await s.Guard.CheckDirectoryAsync(path, scope, ct).ConfigureAwait(false);
            if (check.Directory is not { } directory)
            {
                if (RemoteListRules.RefusesPath(check.Reason ?? RemoteFileGuardReasons.NotFound))
                    return DeviceRefusedPath;
                return RemoteFilesResult.Send(Tree(null, [], [], false, "這個資料夾不存在或讀不到"));
            }

            root = directory;
            var kind = RemoteFileGuard.QueryDriveKind(Path.GetPathRoot(directory) ?? directory);
            forbiddenDirectory = d => RemoteFolderRules.IsForbidden(d, kind, scope.Folders);
        }

        var files = new JArray();
        var dirs = new JArray();
        var truncated = false;
        var deadline = Environment.TickCount64 + (long)DeviceFolderRules.TreeBudget.TotalMilliseconds;
        var walked = await Task.Run(() =>
        {
            var queue = new Queue<string>();
            queue.Enqueue(root);
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 };
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                IEnumerable<FileSystemInfo> items;
                try
                {
                    items = new DirectoryInfo(current).EnumerateFileSystemInfos("*", options).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    continue;
                }

                foreach (var item in items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (Environment.TickCount64 > deadline || files.Count >= DeviceFolderRules.MaxTreeFiles || dirs.Count >= DeviceFolderRules.MaxTreeDirs)
                    {
                        truncated = true;
                        return true;
                    }

                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0 && (network || !RemoteFileGuard.IsAllowedReparsePoint(item.FullName)))
                        continue;
                    if (DeviceFolderRules.RelativeTo(root, item.FullName) is not { } relative)
                        continue;
                    if ((item.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (forbiddenDirectory(item.FullName))
                            continue;
                        dirs.Add(relative);
                        queue.Enqueue(item.FullName);
                    }
                    else if (!RemoteFileGuardRules.IsForbiddenFileName(item.FullName))
                    {
                        files.Add(new JObject { ["path"] = relative, ["size"] = (item as FileInfo)?.Length ?? 0 });
                    }
                }
            }

            return true;
        }, ct).WaitAsync(DeviceFolderRules.TreeBudget + TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
        if (!walked)
            truncated = true;
        var message = Tree(root, files, dirs, truncated, null);
        // 太大放不進一則輸出：從後面拿掉檔案（標 truncated）
        while (System.Text.Encoding.UTF8.GetByteCount(message.ToString(Newtonsoft.Json.Formatting.None)) > RemoteListRules.MaxOutputBytes && files.Count > 0)
        {
            files.RemoveAt(files.Count - 1);
            message["truncated"] = true;
        }

        return RemoteFilesResult.Send(message);
    }

    private async Task DeviceFetchManyAsync(RemoteViewContext context, DeviceFilesSnapshot s, string? requestId, string root, string pathsJson)
    {
        var now = RemoteFilesTime.GetUtcNow().ToUnixTimeMilliseconds();
        RemoteFilesResult result;
        try
        {
            result = await Task.Run(() => DeviceFetchManyCoreAsync(s, requestId, root, pathsJson, now, CancellationToken.None));
        }
        catch (Exception ex)
        {
            FailRemoteFiles(context, RemoteCommandCatalog.DeviceFetchMany, ex);
            return;
        }

        FinishRemoteFiles(context, result);
    }

    /// <summary>
    /// 背景：<paramref name="root"/> 底下的多個相對路徑（JSON 陣列字串，最多 <see cref="DeviceFolderRules.MaxFetchMany"/> 個），逐一照單檔下載的守門開檔，
    /// 開得了的一次發 <c>@xfer</c> 參照；開不了的那一項帶 error。輸出一則 <c>fileRefs</c>
    /// </summary>
    private static async Task<RemoteFilesResult> DeviceFetchManyCoreAsync(DeviceFilesSnapshot s, string? requestId, string root, string pathsJson, long nowMs, CancellationToken ct)
    {
        List<string> relatives;
        try
        {
            relatives = JArray.Parse(pathsJson).Select(t => t is JValue { Type: JTokenType.String, Value: string v } ? v : string.Empty).ToList();
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return new RemoteFilesResult(RemoteResultCodes.Invalid, "檔案清單的格式不正確", null);
        }

        if (relatives.Count is 0 or > DeviceFolderRules.MaxFetchMany)
            return new RemoteFilesResult(RemoteResultCodes.Invalid, $"一次要 1～{DeviceFolderRules.MaxFetchMany} 個檔", null);
        var network = s.Network.IsNetworkPath(root);
        var scope = network ? null : await DeviceTransferScope.BuildAsync(s.Folders, s.Drives, s.Guard, ct).ConfigureAwait(false);
        var items = new List<(string Relative, string? Final, long Size, string? Error)>();
        foreach (var raw in relatives)
        {
            if (DeviceFolderRules.CheckRelative(raw) is not { } relative)
            {
                items.Add((raw.Length == 0 ? "?" : raw, null, 0, RemoteListRules.FetchRefused));
                continue;
            }

            var full = Path.Combine(root, relative);
            GuardedFile? file;
            if (network)
                (file, _) = await s.Network.OpenAsync(full, ct).ConfigureAwait(false);
            else
                file = (await s.Guard.OpenAsync(full, RemoteFilePurpose.File, scope!, long.MaxValue, ct).ConfigureAwait(false)).File;
            if (file is null)
            {
                items.Add((relative, null, 0, RemoteListRules.FetchRefused));
                continue;
            }

            using (file)
                items.Add((relative, file.FinalPath, file.Length, null));
        }

        var finals = items.Where(i => i.Final is not null).Select(i => i.Final!).ToList();
        IReadOnlyList<string> refs = [];
        if (finals.Count > 0)
        {
            try
            {
                refs = await s.Transfer!.RegisterRefsAsync(string.Empty, MediaServerRules.TransferItemId, finals, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                Log.Warn($"deviceFetchMany 的參照寫不進去：{ex.Message}");
                return new RemoteFilesResult(RemoteResultCodes.Unsupported, DeviceTransferRules.NoTransferMessage, null);
            }
        }

        var array = new JArray();
        var next = 0;
        foreach (var (relative, final, size, error) in items)
        {
            if (final is null)
            {
                array.Add(new JObject { ["path"] = relative, ["error"] = error });
                continue;
            }

            array.Add(new JObject { ["path"] = relative, ["name"] = Path.GetFileName(final), ["size"] = size, ["ref"] = refs[next++] });
        }

        return RemoteFilesResult.Send(new JObject
        {
            ["type"] = "fileRefs",
            ["requestId"] = TextOrNull(requestId),
            ["conversationId"] = JValue.CreateNull(),
            ["root"] = root,
            ["expiresAt"] = nowMs + (long)LooseMediaRefRules.XferLifetime.TotalMilliseconds,
            ["refs"] = array,
        });
    }

    // ───────────────────────────── devicePut ─────────────────────────────

    /// <summary>UI 執行緒：檢查上傳的形狀與名額，背景驗資料夾與磁碟空間，通過才接下（done）、之後在背景依序下載</summary>
    private void StartDevicePut(RemoteViewContext context, DeviceFilesSnapshot s, JObject command)
    {
        var uploads = ParseRemoteUploads(command["uploads"] as JArray);
        var folder = command.Value<string>("folder") ?? string.Empty;
        var subfolder = DeviceFolderRules.CleanSubfolder(command[RemoteCommandCatalog.DevicePutSubfolder] is JValue { Type: JTokenType.String, Value: string sub } ? sub : null);
        if (subfolder is null)
        {
            context.Reject(RemoteResultCodes.Invalid, "子資料夾的路徑不合格", RemoteGateSteps.Handler);
            return;
        }

        if (disposed)
        {
            context.Reject(RemoteResultCodes.Busy, "電腦的程式正在關閉", RemoteGateSteps.Handler);
            return;
        }

        if (uploads.Count is 0 or > DeviceTransferRules.MaxPutFiles
            || uploads.Any(u => u is null || !UploadIdRules.IsValid(u.UploadId) || string.IsNullOrEmpty(u.Name) || !BlobIdRules.IsBlobId(u.Sha256)
                                || u.Size is < 0 or > DeviceTransferRules.MaxPutFileBytes)
            || uploads.Select(u => u.UploadId).Distinct(StringComparer.Ordinal).Count() != uploads.Count)
        {
            context.Reject(RemoteResultCodes.Invalid, $"上傳的檔案資料不合格（一次 1～{DeviceTransferRules.MaxPutFiles} 個、每個不超過 512 MB）", RemoteGateSteps.Handler);
            return;
        }

        if (uploads.Any(u => deviceXfers.IsActive(u.UploadId)))
        {
            context.Reject(RemoteResultCodes.Busy, "這個上傳的檔案正在接收，請稍候", RemoteGateSteps.Handler);
            return;
        }

        if (s.Transfer is not { CanTransfer: true } transfer)
        {
            context.Reject(RemoteResultCodes.Unsupported, DeviceTransferRules.NoTransferMessage, RemoteGateSteps.Handler);
            return;
        }

        _ = DevicePutAsync(context, s, folder, subfolder, uploads, transfer);
    }

    private async Task DevicePutAsync(RemoteViewContext context, DeviceFilesSnapshot s, string folder, string subfolder, IReadOnlyList<RemoteUpload> uploads,
        IRemoteFileTransfer transfer)
    {
        (string? Directory, string? Code, string? Message) check;
        try
        {
            check = await Task.Run(() => CheckPutFolderAsync(s, folder, subfolder, uploads.Sum(u => u.Size), CancellationToken.None));
        }
        catch (Exception ex)
        {
            FailRemoteFiles(context, RemoteCommandCatalog.DevicePut, ex);
            return;
        }

        if (check.Directory is not { } directory)
        {
            context.Reject(check.Code ?? RemoteResultCodes.Invalid, check.Message ?? "不能存進這個資料夾", RemoteGateSteps.Handler);
            return;
        }

        if (disposed || uploads.Any(u => deviceXfers.IsActive(u.UploadId)))
        {
            context.Reject(RemoteResultCodes.Busy, "這個上傳的檔案正在接收，請稍候", RemoteGateSteps.Handler);
            return;
        }

        foreach (var upload in uploads)
            deviceXfers.Queue(upload.UploadId, upload.Name, upload.Size, DeviceTransferRules.PartPath(directory, upload.UploadId));
        // 接下了（512 MB 的下載等不到 30 秒的期限）：進度與結果經電腦狀態的 xfers 給發起的一方
        context.Complete();
        var issuer = context.IssuerLabel;
        _ = Task.Run(() => ReceiveDevicePutAsync(directory, uploads, transfer, issuer));
    }

    /// <summary>
    /// 背景：資料夾要在範圍裡、不在禁止清單、存在；所在磁碟的可用空間要 ≥ 合計＋1 GB。有子資料夾（整個資料夾的上傳）時在它底下建好、再驗一次
    /// （建之前先以禁止清單擋：子資料夾名稱可能是 .ssh 這類禁止的資料夾）
    /// </summary>
    private static async Task<(string? Directory, string? Code, string? Message)> CheckPutFolderAsync(DeviceFilesSnapshot s, string folder, string subfolder, long total,
        CancellationToken ct)
    {
        var (baseDirectory, code, message) = await CheckPutBaseAsync(s, folder, total, ct).ConfigureAwait(false);
        if (baseDirectory is null || subfolder.Length == 0)
            return (baseDirectory, code, message);
        var target = Path.Combine(baseDirectory, subfolder);
        if (s.Network.IsNetworkPath(baseDirectory))
        {
            var created = await s.Network.RunAsync(() => TryCreateDirectory(target), ct).ConfigureAwait(false);
            if (created != true)
                return (null, RemoteResultCodes.Conflict, "建不了子資料夾（網路資料夾沒有寫入權限或逾時）");
            var (networkTarget, reason) = await s.Network.CheckDirectoryAsync(target, ct).ConfigureAwait(false);
            return networkTarget is null ? (null, RemoteResultCodes.Policy, NetworkPathRules.Explain(reason ?? NetworkPathReasons.NotFound)) : (networkTarget, null, null);
        }

        var scope = await DeviceTransferScope.BuildAsync(s.Folders, s.Drives, s.Guard, ct).ConfigureAwait(false);
        if (RemoteFolderRules.IsForbidden(target, RemoteFileGuard.QueryDriveKind(Path.GetPathRoot(target) ?? target), scope.Folders))
            return (null, RemoteResultCodes.Policy, DeviceTransferRules.RefusedPathMessage);
        if (!TryCreateDirectory(target))
            return (null, RemoteResultCodes.Conflict, "建不了子資料夾（沒有寫入權限）");
        var check = await s.Guard.CheckDirectoryAsync(target, scope, ct).ConfigureAwait(false);
        return check.Directory is { } directory ? (directory, null, null) : (null, RemoteResultCodes.Policy, DeviceTransferRules.RefusedPathMessage);
    }

    private static bool TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Info($"devicePut 建不了子資料夾：{ex.Message}");
            return false;
        }
    }

    private static async Task<(string? Directory, string? Code, string? Message)> CheckPutBaseAsync(DeviceFilesSnapshot s, string folder, long total, CancellationToken ct)
    {
        if (s.Network.IsNetworkPath(folder))
        {
            var (networkDirectory, networkReason) = await s.Network.CheckDirectoryAsync(folder, ct).ConfigureAwait(false);
            if (networkDirectory is null)
            {
                Log.Info($"devicePut 的網路資料夾被拒（{networkReason}）");
                return (null, networkReason is NetworkPathReasons.NotFound or NetworkPathReasons.Timeout ? RemoteResultCodes.Conflict : RemoteResultCodes.Policy,
                    NetworkPathRules.Explain(networkReason!));
            }

            var free = await s.Network.RunAsync(() => s.Network.Shares.FreeSpace(networkDirectory) ?? -1L, ct).ConfigureAwait(false);
            if (!UploadNameRules.HasDiskRoom(free is null or < 0 ? null : free, total))
                return (null, RemoteResultCodes.Conflict, $"那個網路資料夾的空間不夠或量不到（要 {AgentAttachmentRules.FormatSize(total)}，另外至少要留 1 GB）");
            return (networkDirectory, null, null);
        }

        var scope = await DeviceTransferScope.BuildAsync(s.Folders, s.Drives, s.Guard, ct).ConfigureAwait(false);
        var check = await s.Guard.CheckDirectoryAsync(folder, scope, ct).ConfigureAwait(false);
        if (check.Directory is not { } directory)
        {
            var reason = check.Reason ?? RemoteFileGuardReasons.NotFound;
            Log.Info($"devicePut 的資料夾被拒（{reason}）");
            return RemoteListRules.RefusesPath(reason)
                ? (null, RemoteResultCodes.Policy, DeviceTransferRules.RefusedPathMessage)
                : (null, RemoteResultCodes.Conflict, "這個資料夾不存在或讀不到");
        }

        if (!UploadNameRules.HasDiskRoom(MeasureFreeSpace(directory), total))
            return (null, RemoteResultCodes.Conflict, $"那台的磁碟空間不夠（要 {AgentAttachmentRules.FormatSize(total)}，另外至少要留 1 GB）");
        return (directory, null, null);
    }

    /// <summary>背景：依序下載到同一個資料夾的暫存檔（驗大小與 SHA-256），改名成不重複的檔名、標成從網際網路下載；全部做完在這台提示一次</summary>
    private async Task ReceiveDevicePutAsync(string directory, IReadOnlyList<RemoteUpload> uploads, IRemoteFileTransfer transfer, string issuer)
    {
        var saved = new List<string>();
        foreach (var upload in uploads)
        {
            var part = DeviceTransferRules.PartPath(directory, upload.UploadId);
            deviceXfers.Start(upload.UploadId);
            try
            {
                if (File.Exists(part))
                    File.Delete(part);
                await transfer.DownloadUploadAsync(upload, part, CancellationToken.None).ConfigureAwait(false);
                var final = MoveToUniqueName(part, directory, upload.Name);
                MarkOfTheWeb.Mark(final);
                deviceXfers.Saved(upload.UploadId, Path.GetFileName(final));
                saved.Add(final);
                Log.Info($"devicePut：收到 {AgentAttachmentRules.FormatSize(upload.Size)} 的檔");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException
                                           or InvalidOperationException or OperationCanceledException)
            {
                Log.Warn($"devicePut：接收失敗：{ex.Message}");
                deviceXfers.Failed(upload.UploadId);
                PeerUploadRules.TryDelete(part);
            }
        }

        var failed = uploads.Count - saved.Count;
        host.BeginInvoke(() =>
        {
            if (disposed)
                return;
            // 整個資料夾的上傳一個檔送一次 devicePut：4 秒內的併成一則提示
            putToast.Saved += saved.Count;
            putToast.Failed += failed;
            putToast.Issuer = issuer;
            putToast.Directory = putToast.Directory is null || string.Equals(putToast.Directory, directory, StringComparison.OrdinalIgnoreCase) ? directory : string.Empty;
            var version = ++putToast.Version;
            _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => host.BeginInvoke(() =>
            {
                if (disposed || version != putToast.Version)
                    return;
                var (ok, bad, who, where) = (putToast.Saved, putToast.Failed, putToast.Issuer, putToast.Directory);
                putToast = new PutToast();
                var text = ok == 0
                    ? $"{who} 傳來的 {bad} 個檔案沒有收成功"
                    : $"收到 {who} 傳來的 {ok} 個檔案" + (string.IsNullOrEmpty(where) ? string.Empty : $"，存在「{where}」") + (bad > 0 ? $"（另有 {bad} 個失敗）" : string.Empty);
                PostToAll(PeerFileRules.Toast(text, ok == 0 ? "error" : "info"));
            }), TaskScheduler.Default);
        });
    }

    /// <summary>暫存檔改名成資料夾裡還沒有的檔名（同一個資料夾，只改名）；名稱剛好被別人搶先用掉時換下一個</summary>
    private static string MoveToUniqueName(string part, string directory, string name)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var target = DeviceTransferRules.UniquePath(directory, name, p => File.Exists(p) || Directory.Exists(p))
                         ?? throw new IOException("資料夾裡同名的檔太多");
            try
            {
                File.Move(part, target, overwrite: false);
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                // 改名之前同名的檔剛好出現：換下一個名字
            }
        }

        throw new IOException("改不了檔名（同名的檔一直出現）");
    }
}
