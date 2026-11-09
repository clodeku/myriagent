using System.Security.Principal;
using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Sync;

namespace Myriagent.Hub;

/// <summary>
/// 電腦狀態面板的檔案傳輸（deviceBrowse、deviceFetch、devicePut；sync-protocol 14.15）在被操作那台的規則：
/// 範圍的根是整台的本機磁碟，禁止清單是遠端選資料夾的那一份再加上傳輸專用的資料夾（金鑰、雲端憑證、DPAPI、桌面發佈的簽章金鑰），
/// 寫進資料夾時同名自動改名、絕不覆寫。純函式，碰檔案系統的只有 <see cref="FixedDriveRoots"/>
/// </summary>
internal static class DeviceTransferRules
{
    /// <summary>一次 devicePut 最多幾個檔（網頁照這個分批）</summary>
    public const int MaxPutFiles = 20;

    /// <summary>單檔上限：同伺服器 E4 的 512 MB（網頁上傳的上限）</summary>
    public const long MaxPutFileBytes = PeerUploadRules.MaxUploadBytes;

    /// <summary>接收中的暫存檔副檔名（下載完才改名成真正的檔名；同一個資料夾，改名不必再複製）</summary>
    public const string PartSuffix = ".myriagent-part";

    /// <summary>以系統管理員執行時的拒絕說明</summary>
    public const string ElevatedMessage = "這台的 Clode Myriagent 以系統管理員權限執行，不接受其他電腦存取檔案（請以一般權限執行 app）";

    /// <summary>範圍外、禁止清單的路徑</summary>
    public const string RefusedPathMessage = "這個路徑不能給其他電腦存取（網路磁碟、捷徑連結，或含金鑰、憑證、app 資料的資料夾）";

    /// <summary>沒有和伺服器連線（或伺服器不支援媒體）</summary>
    public const string NoTransferMessage = "這台電腦目前沒有和伺服器連線（或伺服器不支援檔案轉送），不能傳檔案";

    /// <summary>
    /// 傳輸另外禁止的資料夾（加在 <see cref="RemoteScopeBuilder.DeviceFoldersAsync"/> 的那一份之外；遠端選資料夾、抽屜不受影響）：
    /// 其他工具的金鑰與雲端憑證、Windows 的 DPAPI 主金鑰與憑證私鑰、認證管理員的保存庫、桌面發佈的簽章金鑰
    /// </summary>
    public static IReadOnlyList<string> ExtraForbiddenFolders(MediaFolderRoots folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var list = new List<string>();
        void Add(string? root, string relative)
        {
            if (!string.IsNullOrWhiteSpace(root))
                list.Add(Path.Combine(root, relative));
        }

        foreach (var name in new[] { ".gnupg", ".aws", ".azure", ".kube", ".docker", ".grok", ".gemini", ".config\\gh", ".git-credentials" })
            Add(folders.UserProfile, name);
        Add(folders.LocalAppData, "MyriagentRelease");
        Add(folders.LocalAppData, @"Microsoft\Vault");
        Add(folders.LocalAppData, @"Microsoft\Protect");
        Add(folders.RoamingAppData, @"Microsoft\Protect");
        Add(folders.RoamingAppData, @"Microsoft\Crypto");
        Add(folders.RoamingAppData, @"Microsoft\Vault");
        Add(folders.RoamingAppData, @"Microsoft\SystemCertificates");
        return list;
    }

    /// <summary>禁止清單用的資料夾：原本的加上 <see cref="ExtraForbiddenFolders"/>（放進帳戶設定資料夾那一欄：規則把它們一律當成禁止的根）</summary>
    public static MediaFolderRoots WithExtraForbidden(MediaFolderRoots folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        return folders with { AccountConfigDirectories = [.. folders.AccountConfigDirectories, .. ExtraForbiddenFolders(folders)] };
    }

    /// <summary>範圍的根：這台的固定磁碟與卸除式磁碟（準備好的才列；網路磁碟、光碟、RAM 磁碟不列），依磁碟代號排序</summary>
    public static IReadOnlyList<string> FixedDriveRoots()
    {
        var roots = new List<string>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"列不出這台的磁碟：{ex.Message}");
            return roots;
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady && IsDriveRoot(drive.Name))
                    roots.Add(drive.Name.ToUpperInvariant());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 剛拔掉的隨身碟：略過
            }
        }

        roots.Sort(StringComparer.OrdinalIgnoreCase);
        return roots;
    }

    /// <summary>「X:\」（磁碟代號是英文字母）</summary>
    public static bool IsDriveRoot(string? path)
        => path is { Length: 3 } && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    /// <summary>磁碟在虛擬根的名稱：「本機磁碟 (C:)」或「資料 (D:)」（有標籤時用標籤）</summary>
    public static string DriveLabel(string root, string? label)
    {
        var letter = root[..2].ToUpperInvariant();
        var clean = string.IsNullOrWhiteSpace(label) ? null : Security.LabelRules.Clean(label.Trim(), 32);
        return (string.IsNullOrEmpty(clean) ? "本機磁碟" : clean) + " (" + letter + ")";
    }

    /// <summary>虛擬根的常用資料夾（名稱、路徑）：桌面、文件、下載、使用者資料夾；路徑是空的不列</summary>
    public static IReadOnlyList<(string Name, string Path)> Places(string? userProfile, string? desktop, string? documents)
    {
        var list = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(desktop))
            list.Add(("桌面", desktop));
        if (!string.IsNullOrWhiteSpace(documents))
            list.Add(("文件", documents));
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            list.Add(("下載", Path.Combine(userProfile, "Downloads")));
            list.Add(("使用者資料夾", userProfile));
        }

        return list;
    }

    /// <summary>
    /// 存檔的名稱（<paramref name="folder"/> 裡還沒有的）：先以 <see cref="PeerFileRules.SafeFileName"/> 清理，同名（檔或資料夾，不分大小寫）時加「 (2)」、「 (3)」…；
    /// <paramref name="exists"/> 判斷完整路徑在不在。試到 999 都有就回 null
    /// </summary>
    public static string? UniquePath(string folder, string? name, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(exists);
        var safe = PeerFileRules.SafeFileName(name);
        if (safe.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase))
            safe += "_";
        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        for (var n = 1; n < 1000; n++)
        {
            var path = Path.Combine(folder, n == 1 ? safe : $"{stem} ({n}){ext}");
            if (!exists(path))
                return path;
        }

        return null;
    }

    /// <summary>接收中的暫存檔（和目的地同一個資料夾，名稱帶上傳 id；隱藏屬性在建好之後設）</summary>
    public static string PartPath(string folder, string uploadId) => Path.Combine(folder, "~myriagent-" + uploadId + PartSuffix);

    /// <summary>app 有系統管理員的權限（權杖裡的 Administrators 群組是啟用的；以系統管理員執行，或 UAC 關掉的管理員帳戶）</summary>
    public static bool ProcessIsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // 判斷不了：當成有（寧可不給）
            return true;
        }
    }
}

/// <summary>傳輸的守門範圍（被操作那台；中樞的三個指令與媒體轉送的 <c>@xfer</c> 共用）</summary>
internal static class DeviceTransferScope
{
    /// <summary>範圍的根是 <paramref name="driveRoots"/>（整台的本機磁碟），禁止清單是遠端選資料夾的那一份加上 <see cref="DeviceTransferRules.ExtraForbiddenFolders"/></summary>
    public static async Task<RemoteFileScope> BuildAsync(MediaFolderRoots folders, IReadOnlyList<string> driveRoots, RemoteFileGuard guard, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(driveRoots);
        ArgumentNullException.ThrowIfNull(guard);
        var context = await RemoteScopeBuilder.DeviceFoldersAsync(DeviceTransferRules.WithExtraForbidden(folders), guard.NormalizeRootAsync, ct).ConfigureAwait(false);
        var roots = await RemoteScopeBuilder.RootsAsync(driveRoots, guard.NormalizeRootAsync, ct).ConfigureAwait(false);
        return new RemoteFileScope(string.Empty, roots, new HashSet<string>(StringComparer.OrdinalIgnoreCase), context);
    }
}

/// <summary>
/// 這台正在接收（devicePut）與剛收完的檔（給 <see cref="DeviceStats.Xfers"/>；任何執行緒，自己加鎖）：最多 <see cref="DeviceStatsRules.MaxXfers"/> 筆，
/// 結束的留 <see cref="KeepFinished"/>；接收中的進度看暫存檔的大小（讀狀態時才量）
/// </summary>
internal sealed class DeviceXferBoard
{
    public static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(10);

    private readonly object gate = new();
    private readonly List<Entry> entries = [];
    private readonly Func<long> now;
    private readonly Func<string, long?> measure;

    private sealed class Entry(string id, string name, long size, string part)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public long Size { get; } = size;

        public string Part { get; } = part;

        public string State { get; set; } = "queued";

        public string? Saved { get; set; }

        public long? FinishedAt { get; set; }
    }

    /// <param name="now">單調時鐘（毫秒）；null＝Environment.TickCount64</param>
    /// <param name="measure">暫存檔的大小（不在回 null）；null＝FileInfo</param>
    public DeviceXferBoard(Func<long>? now = null, Func<string, long?>? measure = null)
    {
        this.now = now ?? (() => Environment.TickCount64);
        this.measure = measure ?? MeasureFile;
    }

    /// <summary>還在排隊或接收中（同一個上傳 id 不收第二次）</summary>
    public bool IsActive(string id)
    {
        lock (gate)
            return entries.Any(e => e.Id == id && e.FinishedAt is null);
    }

    /// <summary>排進來（還沒開始下載，進度是 0）</summary>
    public void Queue(string id, string name, long size, string part)
    {
        lock (gate)
        {
            entries.RemoveAll(e => e.Id == id);
            entries.Add(new Entry(id, name, size, part));
            Trim();
        }
    }

    public void Start(string id) => Set(id, e => e.State = "receiving");

    public void Saved(string id, string savedName) => Set(id, e =>
    {
        e.State = "saved";
        e.Saved = savedName;
        e.FinishedAt = now();
    });

    public void Failed(string id) => Set(id, e =>
    {
        e.State = "failed";
        e.FinishedAt = now();
    });

    /// <summary>給狀態的那一份（沒有任何一筆是 null：線上省略）；排隊中算接收中、進度 0</summary>
    public IReadOnlyList<XferStats>? Snapshot()
    {
        lock (gate)
        {
            var t = now();
            entries.RemoveAll(e => e.FinishedAt is { } at && t - at > (long)KeepFinished.TotalMilliseconds);
            if (entries.Count == 0)
                return null;
            return entries.Select(e =>
            {
                var done = e.State switch
                {
                    "saved" => e.Size,
                    "receiving" => Math.Clamp(measure(e.Part) ?? 0, 0, e.Size),
                    _ => 0,
                };
                var state = e.State == "queued" ? "receiving" : e.State;
                return new XferStats(e.Id, Clean(e.Name), e.Size, done, state, state == "saved" ? Clean(e.Saved ?? e.Name) : null);
            }).ToList();
        }
    }

    private void Set(string id, Action<Entry> change)
    {
        lock (gate)
        {
            if (entries.FirstOrDefault(e => e.Id == id) is { } entry)
                change(entry);
        }
    }

    /// <summary>超過上限時先丟最早結束的，都還沒結束就丟最早排的</summary>
    private void Trim()
    {
        while (entries.Count > DeviceStatsRules.MaxXfers)
        {
            var victim = entries.Where(e => e.FinishedAt is not null).MinBy(e => e.FinishedAt) ?? entries[0];
            entries.Remove(victim);
        }
    }

    /// <summary>狀態的檔名要過 <see cref="DeviceStatsRules.CheckXfer"/>：去掉控制與格式字元、截到上限，空的換成「檔案」</summary>
    private static string Clean(string name)
    {
        var cut = name.Length <= DeviceStatsRules.MaxXferNameLength ? name : name[..DeviceStatsRules.MaxXferNameLength];
        var clean = Security.LabelRules.Clean(cut, DeviceStatsRules.MaxXferNameLength);
        return string.IsNullOrEmpty(clean) ? "檔案" : clean;
    }

    private static long? MeasureFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
