using System.Runtime.InteropServices;
using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Sync;
using Microsoft.Win32.SafeHandles;

namespace Myriagent.Hub;

/// <summary>遠端資料夾檢查的結果（WP4.4 計畫 D18）：處理者把 Invalid 回 invalid、Policy 回 policy、Missing 與 Timeout 回 conflict</summary>
internal enum RemoteFolderVerdict
{
    Ok,
    Invalid,
    Policy,
    Missing,
    Timeout,
}

/// <param name="Path">Ok 時是展開、正規化之後的資料夾（大寫磁碟代號、長檔名、沒有結尾的分隔字元；根目錄是「C:\」）；其他是 null</param>
/// <param name="Detail">給人看的說明（臺灣繁體中文；處理者當成拒絕的訊息）；Ok 是 null</param>
internal sealed record RemoteFolderCheck(RemoteFolderVerdict Verdict, string? Path, string? Detail);

/// <summary>資料夾探針碰檔案系統的四個動作（測試注入計數或卡住的替身）；都在背景執行緒呼叫</summary>
internal interface IRemoteFolderFileSystem
{
    /// <summary>完整路徑所在磁碟代號的種類</summary>
    RemoteDriveKind DriveKind(string fullPath);

    /// <summary>GetLongPathNameW：展開 8.3 短檔名；不存在或讀不到回 null</summary>
    string? LongPath(string fullPath);

    bool DirectoryExists(string path);

    /// <summary>
    /// 真實路徑（參數是正規化的「X:\…」，所在磁碟已確認是固定或卸除式）：GetFinalPathNameByHandle 的 VOLUME_NAME_DOS 結果（<c>\\?\X:\…</c>），
    /// 路徑上每一層的 junction、符號連結、掛載點（有磁碟代號的磁碟區換成它的代號）都解析掉；路徑上有不能跟進的連結（目標是網路、沒有代號的磁碟區、網路磁碟機…）時回
    /// <see cref="RemoteFolderFinalPathRules.NotFollowed"/>，打不開回 null
    /// </summary>
    string? FinalPath(string normalizedPath);
}

/// <summary>真的檔案系統（Windows）</summary>
internal sealed class WindowsRemoteFolderFileSystem : IRemoteFolderFileSystem
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint ReparseTagMountPoint = 0xA0000003;
    private const int ErrorMoreData = 234;
    private const string LongPrefix = @"\\?\";

    public static WindowsRemoteFolderFileSystem Instance { get; } = new();

    private WindowsRemoteFolderFileSystem()
    {
    }

    public RemoteDriveKind DriveKind(string fullPath) => RemoteHandlerRules.DriveKindOf(new DriveInfo(fullPath[..1]).DriveType);

    /// <summary>
    /// 不跟進任何連結地解析：從磁碟根目錄逐層以 FILE_FLAG_OPEN_REPARSE_POINT 讀屬性與標籤（<see cref="RemoteFileGuard.TryGetAttributeTag"/>），
    /// 名稱代理（junction、符號連結、掛載點）就讀它的目標（FileSystemInfo.LinkTarget，也不跟進），經 <see cref="RemoteFolderFinalPathRules.LinkTargetPath"/>
    /// 換成完整路徑；磁碟區掛載點的目標（LinkTarget 回 <c>Volume{GUID}\…</c>）以 GetVolumePathNamesForVolumeNameW 換成那個磁碟區的磁碟代號
    /// （<see cref="RemoteFolderFinalPathRules.VolumeTargetPath"/>），沒有代號就不跟進。換到新的磁碟代號時先查那個磁碟的種類，從目標的根目錄重新走（之後的每一層照接）。全程開的都是已確認沒有連結的上層底下的一層，
    /// 所以連結指到網路時不會碰到網路（不送 SMB 雜湊）。走完的路徑沒有連結，最後同樣以 FILE_FLAG_OPEN_REPARSE_POINT 開它取正規的名稱
    /// </summary>
    public string? FinalPath(string normalizedPath)
    {
        var drives = new HashSet<char> { char.ToUpperInvariant(normalizedPath[0]) };
        var current = normalizedPath[..3];
        var pending = normalizedPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
        var hops = 0;
        var index = 0;
        while (index < pending.Count)
        {
            var next = current.EndsWith('\\') ? current + pending[index] : current + @"\" + pending[index];
            if (RemoteFileGuard.TryGetAttributeTag(next, out var attributes, out var tag) != 0)
                return null;
            if ((attributes & FileAttributeReparsePoint) == 0 || !RemoteFileGuardRules.IsNameSurrogate(tag))
            {
                current = next;
                index++;
                continue;
            }

            if (++hops > RemoteFolderFinalPathRules.MaxLinkHops)
                return null;
            var raw = ReadLinkTarget(next);
            // 磁碟區掛載點（LinkTarget 是 Volume{GUID}\…，合併前審查 PROD-2）：換成那個磁碟區的磁碟代號照「指到別的磁碟的 junction」走；
            // 沒有代號的磁碟區（或不是掛載點標籤的連結卻寫成這種目標）不跟進
            var target = RemoteFolderFinalPathRules.TryParseVolumeTarget(raw, out var volume, out var rest)
                ? tag == ReparseTagMountPoint ? RemoteFolderFinalPathRules.VolumeTargetPath(next, VolumeDriveRoot(volume), rest) : null
                : RemoteFolderFinalPathRules.LinkTargetPath(next, raw);
            if (target is null)
            {
                Log.Warn($"遠端選資料夾：{next} 是指到 {raw ?? "（讀不到）"} 的連結，不跟進");
                return RemoteFolderFinalPathRules.NotFollowed;
            }

            if (drives.Add(char.ToUpperInvariant(target[0])) && SafeDriveKind(target) is not (RemoteDriveKind.Fixed or RemoteDriveKind.Removable))
            {
                Log.Warn($"遠端選資料夾：{next} 指到的 {target} 不在固定或卸除式磁碟上，不跟進");
                return RemoteFolderFinalPathRules.NotFollowed;
            }

            pending = [.. target[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries), .. pending.Skip(index + 1)];
            current = target[..3];
            index = 0;
        }

        using var handle = CreateFileW(LongPrefix + current, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        return handle.IsInvalid ? null : RemoteFileGuard.GetFinalPath(handle);
    }

    /// <summary>
    /// 磁碟區（<c>\\?\Volume{GUID}\</c>）的磁碟代號根目錄 <c>X:\</c>（GetVolumePathNamesForVolumeNameW 列出的掛載路徑裡第一個是磁碟代號根目錄的）；
    /// 沒有代號、或查不到回 null。只問掛載管理員，不開那個磁碟區上的任何檔案
    /// </summary>
    private static string? VolumeDriveRoot(string volumeName)
    {
        var buffer = new char[512];
        while (true)
        {
            if (GetVolumePathNamesForVolumeNameW(volumeName, buffer, (uint)buffer.Length, out var needed))
                break;
            if (Marshal.GetLastWin32Error() != ErrorMoreData || needed <= buffer.Length || needed > 65536)
                return null;
            buffer = new char[needed];
        }

        foreach (var name in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (name.Length == 3 && char.IsAsciiLetter(name[0]) && name[1] == ':' && name[2] == '\\')
                return name;
        }

        return null;
    }

    /// <summary>連結的目標原文（不跟進）；讀不到回 null</summary>
    private static string? ReadLinkTarget(string path)
    {
        try
        {
            return new DirectoryInfo(LongPrefix + path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private RemoteDriveKind SafeDriveKind(string fullPath)
    {
        try
        {
            return DriveKind(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return RemoteDriveKind.Unknown;
        }
    }

    public string? LongPath(string fullPath)
    {
        var buffer = new char[260];
        while (true)
        {
            var length = GetLongPathNameW(fullPath, buffer, (uint)buffer.Length);
            if (length == 0)
                return null;
            if (length < buffer.Length)
                return new string(buffer, 0, (int)length);
            // 緩衝區不夠：length 是需要的大小（含結尾的 NUL）
            if (length > RemoteFolderRules.MaxPathLength + 1)
                return null;
            buffer = new char[length];
        }
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, [Out] char[] lpszLongPath, uint cchBuffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNamesForVolumeNameW(string volumeName, [Out] char[] volumePathNames, uint bufferLength, out uint returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}

/// <summary>
/// 遠端選的資料夾（setFolder、conversationCreate 的 folder；WP4.4 計畫 D18、設計 8.7）在背景執行緒、整體逾時內檢查：
/// <list type="number">
/// <item>純字串：不是「磁碟代號:\」開頭（UNC、<c>\\?\</c>、<c>\\.\</c>、相對路徑）→ Invalid，不碰檔案系統。</item>
/// <item>磁碟的種類不是固定或卸除式（網路磁碟機代號、光碟…）→ Policy。</item>
/// <item>GetLongPathNameW 展開 8.3 短檔名；資料夾不存在（或讀不到）→ Missing。</item>
/// <item><see cref="RemoteFolderRules.TryNormalize"/> 不收（保留裝置名稱、替代資料流、結尾是點…）→ Invalid。</item>
/// <item><see cref="RemoteFolderRules.IsForbidden(string?, RemoteDriveKind, RemoteFolderContext)"/>（程式資料、帳戶設定、瀏覽器資料…）→ Policy。</item>
/// <item>真實路徑（設計 8.7 第 3 步，<see cref="RemoteFolderFinalPathRules"/>）：路徑上的 junction、符號連結、掛載點解析掉（不跟進任何連結）之後，
/// 真實路徑再過一次正規化、磁碟種類與禁止清單（禁止的資料夾也比對它們自己的真實路徑）；拒絕的代碼同直接選那個地方（Policy 或 Invalid），
/// 打不開 → Missing。通過時套用的是真實路徑。</item>
/// </list>
/// 禁止清單的資料夾在同一個背景工作裡展開（GetFullPath＋GetLongPathNameW；不存在的部分從最長的存在上層展開），UNC 的不展開
/// （規則對正規化不了的項目 fail-closed）。逾時 → Timeout（背景工作留著自己結束）
/// </summary>
internal static class RemoteFolderProbe
{
    /// <summary>整體逾時（D18）</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <param name="context">
    /// 禁止清單用的資料夾，在呼叫端（中樞的 UI 執行緒）組好的原始路徑：中樞的狀態只能在 UI 執行緒讀，展開在背景做
    /// </param>
    /// <param name="fileSystem">null＝真的檔案系統</param>
    public static Task<RemoteFolderCheck> CheckAsync(string? path, RemoteFolderContext context, TimeSpan timeout, IRemoteFolderFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!RemoteHandlerRules.LooksLikeLocalDrivePath(path))
            return Task.FromResult(NotLocalPath());
        return CheckInBackgroundAsync(path!, context, timeout, fileSystem ?? WindowsRemoteFolderFileSystem.Instance);
    }

    private static async Task<RemoteFolderCheck> CheckInBackgroundAsync(string path, RemoteFolderContext context, TimeSpan timeout, IRemoteFolderFileSystem fs)
    {
        try
        {
            return await Task.Run(() => Check(path, context, fs)).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn($"遠端選的資料夾 {path} 在 {timeout.TotalSeconds:0.#} 秒內沒有檢查完（可能是離線或很慢的磁碟）");
            return new RemoteFolderCheck(RemoteFolderVerdict.Timeout, null, "資料夾無法存取（逾時）");
        }
    }

    private static RemoteFolderCheck Check(string path, RemoteFolderContext context, IRemoteFolderFileSystem fs)
    {
        // GetFullPath 對完整路徑只做字串處理（.、..、分隔字元、結尾的點與空白），不碰檔案系統；結果還要是本機磁碟的路徑
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return NotLocalPath();
        }

        if (!RemoteHandlerRules.LooksLikeLocalDrivePath(full))
            return NotLocalPath();

        var kind = SafeDriveKind(full, fs);
        if (kind is not (RemoteDriveKind.Fixed or RemoteDriveKind.Removable))
            return new RemoteFolderCheck(RemoteFolderVerdict.Policy, null, "這個資料夾不在這台電腦的固定磁碟或卸除式磁碟上（例如網路磁碟機），不能從遠端選");

        var longPath = TryLongPath(full, fs);
        if (longPath == null || !SafeDirectoryExists(longPath, fs))
            return new RemoteFolderCheck(RemoteFolderVerdict.Missing, null, $"找不到資料夾（或沒有權限讀取）：{full}");

        if (!RemoteFolderRules.TryNormalize(longPath, out var normalized))
            return new RemoteFolderCheck(RemoteFolderVerdict.Invalid, null, "資料夾的名稱不能從遠端使用（裝置名稱、資料流或結尾是點的名稱）");

        var expanded = Expand(context, fs);
        if (string.IsNullOrEmpty(expanded.UserProfile) || string.IsNullOrEmpty(expanded.LocalAppData))
        {
            Log.Warn("遠端選資料夾：不知道使用者資料夾或 %LOCALAPPDATA%，禁止清單不完整，一律不收");
            return Forbidden();
        }

        if (RemoteFolderRules.IsForbidden(normalized, kind, expanded, out var unusable))
        {
            if (unusable != null)
                Log.Warn($"遠端選資料夾：禁止清單裡的 {unusable} 正規化不了，一律不收");
            return Forbidden();
        }

        return CheckFinalPath(normalized, kind, expanded, fs);
    }

    /// <summary>
    /// 字串那一關通過之後：真實路徑再判一次（代理不用管理員權限就能在允許的工作資料夾裡建一個指到資料根目錄的 junction）。
    /// 拒絕的代碼同直接選那個地方；通過時套用真實路徑（之後 junction 改指向也影響不到這個對話，檔案抽屜也不會因為路徑上的 junction 一律被拒）
    /// </summary>
    private static RemoteFolderCheck CheckFinalPath(string normalized, RemoteDriveKind kind, RemoteFolderContext expanded, IRemoteFolderFileSystem fs)
    {
        if (SafeFinalPath(normalized, fs) is not { } raw)
            return new RemoteFolderCheck(RemoteFolderVerdict.Missing, null, $"找不到資料夾（或沒有權限讀取）：{normalized}");

        var final = RemoteFolderFinalPathRules.Interpret(normalized, raw);
        var finalKind = RemoteFolderFinalPathRules.ChangesDrive(normalized, final) ? SafeDriveKind(final.Path!, fs) : kind;
        var refusal = RemoteFolderFinalPathRules.Refusal(final, finalKind);
        if (refusal == null)
        {
            var roots = new RootResolver(fs);
            if (!RemoteFolderFinalPathRules.IsForbidden(final.Path!, finalKind, expanded, roots.Resolve, out var unusable))
                return new RemoteFolderCheck(RemoteFolderVerdict.Ok, final.Path, null);
            if (unusable != null)
                Log.Warn($"遠端選資料夾：禁止清單裡的 {unusable} 正規化不了，一律不收");
            refusal = RemoteFolderVerdict.Policy;
        }

        if (final.Kind == RemoteFinalPathKind.Same)
        {
            // 路徑上沒有連結，是禁止的資料夾本身是 junction、這裡是它的目標
            Log.Warn($"遠端選的資料夾 {normalized} 是禁止的資料夾（經過 junction）的真實位置，不收");
            return Forbidden();
        }

        Log.Warn($"遠端選的資料夾 {normalized} 經過 junction、符號連結或掛載點，實際在 {final.Path ?? "（不能跟進的地方）"}：{refusal}");
        return refusal == RemoteFolderVerdict.Invalid
            ? new RemoteFolderCheck(RemoteFolderVerdict.Invalid, null, "這個資料夾經過連結指到的名稱不能從遠端使用（裝置名稱、資料流或結尾是點的名稱）")
            : new RemoteFolderCheck(RemoteFolderVerdict.Policy, null,
                "這個資料夾經過連結（junction 或符號連結）指到不能從遠端選的地方（程式資料、帳戶設定、金鑰、瀏覽器資料、網路位置或網路磁碟）");
    }

    private static RemoteFolderCheck NotLocalPath()
        => new(RemoteFolderVerdict.Invalid, null, "資料夾要是這台電腦本機磁碟的完整路徑（「磁碟代號:\\」開頭）");

    private static RemoteFolderCheck Forbidden()
        => new(RemoteFolderVerdict.Policy, null, "這個資料夾不能從遠端選（程式資料、帳戶設定、金鑰或瀏覽器資料）");

    private static string? SafeFinalPath(string normalized, IRemoteFolderFileSystem fs)
    {
        try
        {
            return fs.FinalPath(normalized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static RemoteDriveKind SafeDriveKind(string fullPath, IRemoteFolderFileSystem fs)
    {
        try
        {
            return fs.DriveKind(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return RemoteDriveKind.Unknown;
        }
    }

    /// <summary>
    /// 禁止清單的資料夾的真實路徑（同一次檢查裡快取）：從自己往上找第一個取得到真實路徑的資料夾，不存在的部分照接回去
    /// （例如還沒建立的 <c>~\.codex</c>）；取不到、或路徑上有不能跟進的連結時回原樣（參數都是正規化的「X:\…」）
    /// </summary>
    private sealed class RootResolver(IRemoteFolderFileSystem fs)
    {
        private readonly Dictionary<string, string> cache = new(StringComparer.OrdinalIgnoreCase);

        public string Resolve(string root)
        {
            if (cache.TryGetValue(root, out var known))
                return known;
            var result = root;
            var missing = new Stack<string>();
            var current = root;
            while (true)
            {
                if (SafeFinalPath(current, fs) is { } raw)
                {
                    if (RemoteFileGuardRules.FromFinalPath(raw) is { } local && RemoteFolderRules.TryNormalize(local, out var real))
                        result = missing.Count == 0 ? real : Path.Combine([real, .. missing]);
                    break;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent))
                    break;
                missing.Push(Path.GetFileName(current));
                current = parent;
            }

            cache[root] = result;
            return result;
        }
    }

    private static string? TryLongPath(string full, IRemoteFolderFileSystem fs)
    {
        try
        {
            return fs.LongPath(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool SafeDirectoryExists(string path, IRemoteFolderFileSystem fs)
    {
        try
        {
            return fs.DirectoryExists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>禁止清單的每個資料夾展開成長檔名的完整路徑（UNC 與展開不了的原樣留著：規則對正規化不了的項目 fail-closed）</summary>
    private static RemoteFolderContext Expand(RemoteFolderContext raw, IRemoteFolderFileSystem fs)
    {
        string? E(string? folder) => string.IsNullOrEmpty(folder) ? folder : ExpandPath(folder, fs);
        return new RemoteFolderContext(
            E(raw.UserProfile) ?? string.Empty,
            E(raw.LocalAppData) ?? string.Empty,
            E(raw.RoamingAppData),
            E(raw.AppDataRoot) ?? string.Empty,
            (raw.AccountConfigDirectories ?? []).Select(d => E(d) ?? string.Empty).ToList(),
            raw.AllowedDataSubtrees?.Select(d => E(d) ?? string.Empty).ToList(),
            E(raw.ConversationDirectory));
    }

    /// <summary>
    /// 一個資料夾的長檔名完整路徑：從自己往上找第一個存在的資料夾展開，不存在的部分照接回去（例如還沒建立的 <c>~\.codex</c>）。
    /// UNC 不碰檔案系統
    /// </summary>
    private static string ExpandPath(string folder, IRemoteFolderFileSystem fs)
    {
        if (!RemoteHandlerRules.LooksLikeLocalDrivePath(folder))
            return folder;
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return folder;
        }

        var missing = new Stack<string>();
        var current = full;
        while (true)
        {
            if (TryLongPath(current, fs) is { } longPath)
                return missing.Count == 0 ? longPath : Path.Combine([longPath, .. missing]);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
                return full;
            missing.Push(Path.GetFileName(current));
            current = parent;
        }
    }
}
