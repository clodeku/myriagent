using System.Runtime.InteropServices;
using Myriagent.Agents;
using Myriagent.Remote;
using Microsoft.Win32.SafeHandles;

namespace Myriagent.Sync;

/// <summary>開檔的結果：<see cref="File"/> 不是 null 就是通過（呼叫端負責 Dispose）；否則 <see cref="Reason"/> 是拒絕原因（<see cref="RemoteFileGuardReasons"/>）</summary>
internal readonly record struct RemoteFileOpen(GuardedFile? File, string? Reason);

/// <summary>資料夾檢查的結果：通過時 <see cref="Directory"/> 是真實路徑（長檔名、「磁碟代號:\…」）；否則 <see cref="Reason"/> 是拒絕原因</summary>
internal readonly record struct RemoteDirectoryCheck(string? Directory, string? Reason);

/// <summary>
/// 守門員開好的檔：唯讀、<c>FileShare.ReadWrite | FileShare.Delete</c>（不擋使用者與代理繼續寫或刪），非同步 I/O、沒有緩衝。
/// <see cref="FinalPath"/> 是開檔後以 GetFinalPathNameByHandle 取得、驗過的真實路徑；長度與修改時間是開檔當下的
/// </summary>
internal sealed class GuardedFile(FileStream stream, string finalPath, long length, DateTime lastWriteTimeUtc) : IDisposable
{
    public FileStream Stream { get; } = stream;

    public string FinalPath { get; } = finalPath;

    public long Length { get; } = length;

    public DateTime LastWriteTimeUtc { get; } = lastWriteTimeUtc;

    /// <summary>從頭讀最多 <paramref name="maxBytes"/> 個位元組（檔案比較短時是整份；檔案在開檔後變短丟 <see cref="EndOfStreamException"/>）</summary>
    public async Task<byte[]> ReadPrefixAsync(int maxBytes, CancellationToken ct)
    {
        var prefix = new byte[(int)Math.Min(maxBytes, Length)];
        Stream.Position = 0;
        await Stream.ReadExactlyAsync(prefix, ct).ConfigureAwait(false);
        return prefix;
    }

    public void Dispose() => Stream.Dispose();
}

/// <summary>
/// 遠端檔案存取的唯一入口（設計 8.7，WP4.7 步 7）：媒體轉送、縮圖、列檔、檔案預覽與下載都先經過這裡，拿到 null 就不碰那個路徑。依序：
/// <list type="number">
/// <item>字串層（<see cref="RemoteFileGuardRules.CheckSyntax"/>）：UNC、裝置命名空間、替代資料流、保留名稱…；再以「固定磁碟」先跑一次
/// 範圍、禁止清單與檔名清單（都是純字串），不通過的連磁碟種類都不查。8.3 短名例外：查過磁碟種類之後以 GetLongPathName 展開再重跑。</item>
/// <item>磁碟種類：只收固定與卸除式（網路磁碟機代號也是「X:\」開頭）。</item>
/// <item>重新解析點：從根目錄逐層以 FILE_FLAG_OPEN_REPARSE_POINT 讀屬性與標籤，符號連結、junction、掛載點一律拒絕，OneDrive 佔位檔放行。</item>
/// <item>範圍與禁止清單：<see cref="RemoteFileGuardRules.CheckString"/>（以實際的磁碟種類）。</item>
/// <item>開檔（唯讀、共用讀寫刪）之後以 GetFinalPathNameByHandle 取真實路徑重跑第 1、4、5 步（防開檔前一刻被換成 junction），
/// 另外拒絕多個硬連結、超過上限的檔；不通過就關掉串流。</item>
/// </list>
/// 每個碰檔案系統的階段都在背景執行緒、各自有逾時（<c>timeout</c>，預設 2 秒；網路磁碟機與 SMB 會卡住）；逾時之後才完成的開檔會被關掉。
/// 不碰中樞、SignalR 與 SQLite，可以從任何執行緒呼叫
/// </summary>
internal sealed class RemoteFileGuard
{
    /// <summary>每個檔案系統階段的逾時（設計 8.7 第 2 步的 2 秒）</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private static readonly long MaxFileTime = DateTime.MaxValue.ToFileTimeUtc();

    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    private const int FileAttributeTagInfoClass = 9;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const string LongPrefix = @"\\?\";

    private readonly Func<string, RemoteDriveKind> driveKind;
    private readonly TimeSpan timeout;
    private readonly TimeProvider time;
    private int fileSystemStages;

    /// <param name="driveKind">查磁碟種類（參數是根目錄「C:\」；正式是 <see cref="QueryDriveKind"/>，測試換成替身）</param>
    /// <param name="timeout">每個檔案系統階段的逾時</param>
    /// <param name="time">逾時用的時鐘</param>
    public RemoteFileGuard(Func<string, RemoteDriveKind> driveKind, TimeSpan timeout, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(driveKind);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        this.driveKind = driveKind;
        this.timeout = timeout;
        this.time = time;
    }

    /// <summary>正式用的守門員：真的 GetDriveType、2 秒逾時、系統時鐘</summary>
    public static RemoteFileGuard CreateDefault() => new(QueryDriveKind, DefaultTimeout, TimeProvider.System);

    /// <summary>開始過的檔案系統階段數（含查磁碟種類；測試確認字串層擋下的路徑完全沒碰檔案系統）</summary>
    internal int FileSystemStages => Volatile.Read(ref fileSystemStages);

    /// <summary>測試接縫：通過重新解析點檢查之後、開檔之前呼叫（參數是要開的路徑），用來在這個空窗把資料夾換成 junction</summary>
    internal Action<string>? BeforeOpenForTest { get; set; }

    /// <summary>
    /// 依用途檢查並唯讀開檔（六步全部）。通過時回開好的檔（長度 ≤ <paramref name="maxBytes"/>），否則回拒絕原因；
    /// <paramref name="ct"/> 取消時丟 <see cref="OperationCanceledException"/>（之後才開好的檔會被關掉）
    /// </summary>
    public async Task<RemoteFileOpen> OpenAsync(string path, RemoteFilePurpose purpose, RemoteFileScope scope, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        var resolved = await ResolveAsync(path, purpose, scope, directory: false, ct).ConfigureAwait(false);
        if (resolved.Reason != null)
            return new RemoteFileOpen(null, resolved.Reason);

        BeforeOpenForTest?.Invoke(resolved.Path!);
        var (done, opened) = await RunAsync(() => OpenAndVerify(resolved.Path!, purpose, scope, resolved.Kind, maxBytes),
            static late => late.File?.Dispose(), ct).ConfigureAwait(false);
        return done ? opened : new RemoteFileOpen(null, RemoteFileGuardReasons.Timeout);
    }

    /// <summary>
    /// Codex 產生圖片的原檔（WP4.7 Q2-A，只給 <see cref="GeneratedMediaCopier"/> 讀來複製，不轉送原檔）：原檔在禁止清單的設定資料夾裡，
    /// 所以不跑範圍與禁止清單，改成更窄的檢查——字串層（8.3 短名也拒絕）、在某個產生圖片的根目錄底下（<paramref name="generatedRoots"/>，已正規化）、
    /// 副檔名是 PNG／JPEG／GIF／WebP、固定或卸除式磁碟、路徑上沒有重新解析點；開檔之後真實路徑仍在根目錄底下、只有一個硬連結、不超過 <paramref name="maxBytes"/>。
    /// 內容格式由複製的一方再驗
    /// </summary>
    public async Task<RemoteFileOpen> OpenGeneratedSourceAsync(string path, IReadOnlyList<string> generatedRoots, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(generatedRoots);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        var reason = RemoteFileGuardRules.CheckSyntax(path, out var normalized);
        if (reason != null)
            return new RemoteFileOpen(null, reason);
        if (!GeneratedMediaRules.IsGenerated(normalized, generatedRoots))
            return new RemoteFileOpen(null, RemoteFileGuardReasons.Scope);
        if (AgentAttachmentRules.GetClaudeImageMediaType(normalized!) is null)
            return new RemoteFileOpen(null, RemoteFileGuardReasons.Type);

        var (kindDone, kind) = await RunAsync(() => SafeDriveKind(normalized![..3]), null, ct).ConfigureAwait(false);
        if (!kindDone)
            return new RemoteFileOpen(null, RemoteFileGuardReasons.Timeout);
        if (kind is not (RemoteDriveKind.Fixed or RemoteDriveKind.Removable))
            return new RemoteFileOpen(null, RemoteFileGuardReasons.Drive);
        var (walkDone, walkReason) = await RunAsync(() => WalkReparsePoints(normalized!, directory: false), null, ct).ConfigureAwait(false);
        if (!walkDone)
            return new RemoteFileOpen(null, RemoteFileGuardReasons.Timeout);
        if (walkReason != null)
            return new RemoteFileOpen(null, walkReason);

        var (done, opened) = await RunAsync(() => OpenGeneratedAndVerify(normalized!, generatedRoots, maxBytes), static late => late.File?.Dispose(), ct)
            .ConfigureAwait(false);
        return done ? opened : new RemoteFileOpen(null, RemoteFileGuardReasons.Timeout);
    }

    /// <summary>
    /// 資料夾（listFiles、dirsOnly）的第 1～5 步：範圍是 <see cref="RemoteFileScope.ListRoots"/>，路徑上（含它本身）不能有重新解析點，
    /// 最後開資料夾取真實路徑再驗一次。通過時回真實路徑；列目錄時另外要略過重新解析點（呼叫端的事）
    /// </summary>
    public async Task<RemoteDirectoryCheck> CheckDirectoryAsync(string path, RemoteFileScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var resolved = await ResolveAsync(path, RemoteFilePurpose.List, scope, directory: true, ct).ConfigureAwait(false);
        if (resolved.Reason != null)
            return new RemoteDirectoryCheck(null, resolved.Reason);

        var (done, verified) = await RunAsync(() => VerifyDirectory(resolved.Path!, scope, resolved.Kind), null, ct).ConfigureAwait(false);
        return done ? verified : new RemoteDirectoryCheck(null, RemoteFileGuardReasons.Timeout);
    }

    /// <summary>
    /// 範圍與禁止清單的根目錄（工作資料夾、資料根目錄、帳戶設定資料夾…）在放進 <see cref="RemoteFileScope"/>／<see cref="RemoteFolderContext"/>
    /// 之前先經過這裡：字串層合格、磁碟是固定或卸除式，有 8.3 短名時以 GetLongPathName 展開。回正規化的長檔名完整路徑，不合格或逾時回 null
    /// （<see cref="RemoteFolderRules.TryGetForbiddenRoots"/> 遇到 null 以外正規化不了的項目會 fail-closed，所以禁止清單的根目錄要原樣保留不合格的值）
    /// </summary>
    public async Task<string?> NormalizeRootAsync(string? path, CancellationToken ct)
    {
        var reason = RemoteFileGuardRules.CheckSyntax(path, out var normalized);
        if (reason != null && reason != RemoteFileGuardReasons.ShortName)
            return null;
        var (kindDone, kind) = await RunAsync(() => SafeDriveKind(normalized![..3]), null, ct).ConfigureAwait(false);
        if (!kindDone || kind is not (RemoteDriveKind.Fixed or RemoteDriveKind.Removable))
            return null;
        if (reason == null)
            return normalized;
        var (longDone, longPath) = await RunAsync(() => TryGetLongPath(normalized!), null, ct).ConfigureAwait(false);
        if (!longDone || longPath == null || RemoteFileGuardRules.CheckSyntax(longPath, out var expanded) != null)
            return null;
        return SameDrive(expanded!, normalized!) ? expanded : null;
    }

    /// <summary>GetDriveType 的結果 → <see cref="RemoteDriveKind"/>（參數是根目錄「C:\」；網路磁碟機離線時可能卡住，只能在背景執行緒呼叫）</summary>
    public static RemoteDriveKind QueryDriveKind(string root) => GetDriveTypeW(root) switch
    {
        1 => RemoteDriveKind.NoRootDirectory,
        2 => RemoteDriveKind.Removable,
        3 => RemoteDriveKind.Fixed,
        4 => RemoteDriveKind.Network,
        5 => RemoteDriveKind.CdRom,
        6 => RemoteDriveKind.Ram,
        _ => RemoteDriveKind.Unknown,
    };

    /// <summary>第 1～4 步（開檔或列目錄之前）：回正規化的長檔名路徑與磁碟種類，或拒絕原因</summary>
    private async Task<(string? Path, RemoteDriveKind Kind, string? Reason)> ResolveAsync(string path, RemoteFilePurpose purpose,
        RemoteFileScope scope, bool directory, CancellationToken ct)
    {
        var reason = RemoteFileGuardRules.CheckSyntax(path, out var normalized);
        if (reason != null && reason != RemoteFileGuardReasons.ShortName)
            return (null, RemoteDriveKind.Unknown, reason);
        if (reason == null)
        {
            // 純字串先判一次（當成固定磁碟）：範圍外、禁止清單、拒絕的檔名不碰檔案系統，連磁碟種類都不查
            var early = Check(normalized!, purpose, scope, RemoteDriveKind.Fixed, directory);
            if (early != null)
                return (null, RemoteDriveKind.Unknown, early);
        }

        var root = normalized![..3];
        var (kindDone, kind) = await RunAsync(() => SafeDriveKind(root), null, ct).ConfigureAwait(false);
        if (!kindDone)
            return (null, RemoteDriveKind.Unknown, RemoteFileGuardReasons.Timeout);
        if (kind is not (RemoteDriveKind.Fixed or RemoteDriveKind.Removable))
            return (null, kind, RemoteFileGuardReasons.Drive);

        if (reason == RemoteFileGuardReasons.ShortName)
        {
            // 8.3 短名：不讀檔案系統就不知道對到哪裡，確認是本機磁碟之後展開再重跑字串層（契約:604）
            var (longDone, longPath) = await RunAsync(() => TryGetLongPath(normalized), null, ct).ConfigureAwait(false);
            if (!longDone)
                return (null, kind, RemoteFileGuardReasons.Timeout);
            if (longPath == null)
                return (null, kind, RemoteFileGuardReasons.NotFound);
            reason = RemoteFileGuardRules.CheckSyntax(longPath, out var expanded);
            if (reason != null)
                return (null, kind, reason);
            if (!SameDrive(expanded!, normalized))
                return (null, kind, RemoteFileGuardReasons.FinalPath);
            normalized = expanded!;
        }

        reason = Check(normalized, purpose, scope, kind, directory);
        if (reason != null)
            return (null, kind, reason);

        var (walkDone, walkReason) = await RunAsync(() => WalkReparsePoints(normalized, directory), null, ct).ConfigureAwait(false);
        if (!walkDone)
            return (null, kind, RemoteFileGuardReasons.Timeout);
        return walkReason != null ? (null, kind, walkReason) : (normalized, kind, null);
    }

    private static string? Check(string normalized, RemoteFilePurpose purpose, RemoteFileScope scope, RemoteDriveKind kind, bool directory)
        => directory
            ? RemoteFileGuardRules.CheckDirectoryString(normalized, scope, kind)
            : RemoteFileGuardRules.CheckString(normalized, purpose, scope, kind);

    /// <summary>
    /// 在背景執行緒做一個檔案系統階段，等到完成或逾時（<see cref="timeout"/>）。逾時或取消時，之後才完成的結果交給 <paramref name="discard"/>
    /// （例如關掉開好的檔）；回 Completed=false 表示逾時
    /// </summary>
    private async Task<(bool Completed, T Value)> RunAsync<T>(Func<T> work, Action<T>? discard, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref fileSystemStages);
        var task = Task.Run(work, CancellationToken.None);
        try
        {
            return (true, await task.WaitAsync(timeout, time, ct).ConfigureAwait(false));
        }
        catch (TimeoutException)
        {
            DiscardLater(task, discard);
            return (false, default!);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DiscardLater(task, discard);
            throw;
        }
    }

    private static void DiscardLater<T>(Task<T> task, Action<T>? discard)
        => task.ContinueWith(static (t, state) =>
        {
            if (t.Status == TaskStatus.RanToCompletion)
                ((Action<T>?)state)?.Invoke(t.Result);
            else
                _ = t.Exception;   // 觀察掉，不讓它變成未觀察的例外
        }, discard, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>磁碟種類；替身丟例外時當成不明（拒絕）</summary>
    private RemoteDriveKind SafeDriveKind(string root)
    {
        try
        {
            return driveKind(root);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return RemoteDriveKind.Unknown;
        }
    }

    /// <summary>
    /// 第 3 步：從根目錄底下第一層到路徑本身，逐層以 FILE_FLAG_OPEN_REPARSE_POINT 開屬性（不跟進連結、不讓雲端檔案下載）讀標籤；
    /// 不放行的重新解析點回 <see cref="RemoteFileGuardReasons.Reparse"/>。中間層要是資料夾、最後一層依 <paramref name="directory"/> 是資料夾或檔案
    /// </summary>
    internal static string? WalkReparsePoints(string normalized, bool directory)
    {
        var segments = normalized[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return directory ? null : RemoteFileGuardReasons.NotFile;
        var current = normalized[..2];
        for (var i = 0; i < segments.Length; i++)
        {
            current += @"\" + segments[i];
            var error = TryGetAttributeTag(current, out var attributes, out var tag);
            if (error != 0)
                return error is ErrorFileNotFound or ErrorPathNotFound ? RemoteFileGuardReasons.NotFound : RemoteFileGuardReasons.Unreadable;
            if ((attributes & FileAttributeReparsePoint) != 0 && !RemoteFileGuardRules.IsAllowedReparseTag(tag))
                return RemoteFileGuardReasons.Reparse;
            var isDirectory = (attributes & FileAttributeDirectory) != 0;
            if (i < segments.Length - 1)
            {
                if (!isDirectory)
                    return RemoteFileGuardReasons.NotFound;
            }
            else if (isDirectory != directory)
            {
                return directory ? RemoteFileGuardReasons.NotDirectory : RemoteFileGuardReasons.NotFile;
            }
        }

        return null;
    }

    /// <summary>
    /// 列目錄與建檔案索引時（WP4.7 步 11，D33、D35）：一個帶重新解析屬性的項目能不能列出來、能不能進去——以 FILE_FLAG_OPEN_REPARSE_POINT 讀這一層的標籤
    /// （不跟進連結、不讓雲端檔案下載），只有雲端檔案的佔位檔（<see cref="RemoteFileGuardRules.IsAllowedReparseTag"/>）回 true；
    /// 沒有重新解析屬性的也回 true，讀不到回 false。會碰檔案系統，只能在背景執行緒呼叫
    /// </summary>
    public static bool IsAllowedReparsePoint(string path)
    {
        if (RemoteFileGuardRules.CheckSyntax(path, out var normalized) != null)
            return false;
        if (TryGetAttributeTag(normalized!, out var attributes, out var tag) != 0)
            return false;
        return (attributes & FileAttributeReparsePoint) == 0 || RemoteFileGuardRules.IsAllowedReparseTag(tag);
    }

    /// <summary>第 5、6 步：唯讀開檔、取真實路徑重跑字串層、硬連結與大小；不通過就關掉</summary>
    private static RemoteFileOpen OpenAndVerify(string normalized, RemoteFilePurpose purpose, RemoteFileScope scope, RemoteDriveKind kind, long maxBytes)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(normalized, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0,
            });
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new RemoteFileOpen(null, RemoteFileGuardReasons.NotFound);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or ArgumentException)
        {
            return new RemoteFileOpen(null, RemoteFileGuardReasons.Unreadable);
        }

        var keep = false;
        try
        {
            var final = RemoteFileGuardRules.FromFinalPath(GetFinalPath(stream.SafeFileHandle));
            if (final == null || !SameDrive(final, normalized))
                return new RemoteFileOpen(null, RemoteFileGuardReasons.FinalPath);
            var reason = RemoteFileGuardRules.CheckString(final, purpose, scope, kind);
            if (reason != null)
                return new RemoteFileOpen(null, reason);
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
                return new RemoteFileOpen(null, RemoteFileGuardReasons.Unreadable);
            if ((info.FileAttributes & FileAttributeDirectory) != 0)
                return new RemoteFileOpen(null, RemoteFileGuardReasons.NotFile);
            if (info.NumberOfLinks > 1)
                return new RemoteFileOpen(null, RemoteFileGuardReasons.HardLink);
            var length = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            if (length > maxBytes)
                return new RemoteFileOpen(null, RemoteFileGuardReasons.TooLarge);
            var lastWrite = FromFileTime(((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow);
            keep = true;
            return new RemoteFileOpen(new GuardedFile(stream, final, length, lastWrite), null);
        }
        finally
        {
            if (!keep)
                stream.Dispose();
        }
    }

    /// <summary>產生圖片原檔的開檔（<see cref="OpenGeneratedSourceAsync"/>）：真實路徑仍在根目錄底下、只有一個硬連結、不是資料夾、不超過上限；不通過就關掉</summary>
    private static RemoteFileOpen OpenGeneratedAndVerify(string normalized, IReadOnlyList<string> generatedRoots, long maxBytes)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(normalized, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0,
            });
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new RemoteFileOpen(null, RemoteFileGuardReasons.NotFound);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or ArgumentException)
        {
            return new RemoteFileOpen(null, RemoteFileGuardReasons.Unreadable);
        }

        var keep = false;
        try
        {
            var final = RemoteFileGuardRules.FromFinalPath(GetFinalPath(stream.SafeFileHandle));
            if (final == null || !SameDrive(final, normalized) || RemoteFileGuardRules.CheckSyntax(final, out var finalNormalized) != null
                || !GeneratedMediaRules.IsGenerated(finalNormalized, generatedRoots))
                return new RemoteFileOpen(null, RemoteFileGuardReasons.FinalPath);
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
                return new RemoteFileOpen(null, RemoteFileGuardReasons.Unreadable);
            if ((info.FileAttributes & FileAttributeDirectory) != 0)
                return new RemoteFileOpen(null, RemoteFileGuardReasons.NotFile);
            if (info.NumberOfLinks > 1)
                return new RemoteFileOpen(null, RemoteFileGuardReasons.HardLink);
            var length = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            if (length > maxBytes)
                return new RemoteFileOpen(null, RemoteFileGuardReasons.TooLarge);
            var lastWrite = FromFileTime(((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow);
            keep = true;
            return new RemoteFileOpen(new GuardedFile(stream, finalNormalized!, length, lastWrite), null);
        }
        finally
        {
            if (!keep)
                stream.Dispose();
        }
    }

    /// <summary>資料夾的最後一步：開資料夾（跟進連結）取真實路徑，再跑一次資料夾的字串層</summary>
    private static RemoteDirectoryCheck VerifyDirectory(string normalized, RemoteFileScope scope, RemoteDriveKind kind)
    {
        using var handle = CreateFileW(LongPrefix + normalized, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return new RemoteDirectoryCheck(null, error is ErrorFileNotFound or ErrorPathNotFound ? RemoteFileGuardReasons.NotFound : RemoteFileGuardReasons.Unreadable);
        }

        var final = RemoteFileGuardRules.FromFinalPath(GetFinalPath(handle));
        if (final == null || !SameDrive(final, normalized))
            return new RemoteDirectoryCheck(null, RemoteFileGuardReasons.FinalPath);
        var reason = RemoteFileGuardRules.CheckDirectoryString(final, scope, kind);
        if (reason != null)
            return new RemoteDirectoryCheck(null, reason);
        RemoteFileGuardRules.CheckSyntax(final, out var normalizedFinal);
        return new RemoteDirectoryCheck(normalizedFinal, null);
    }

    /// <summary>GetFinalPathNameByHandle（FILE_NAME_NORMALIZED、VOLUME_NAME_DOS）；失敗回 null</summary>
    internal static string? GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[512];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
                return null;
            if (length < buffer.Length)
                return new string(buffer, 0, (int)length);
            buffer = new char[length + 1];
        }

        return null;
    }

    /// <summary>GetLongPathName（以 \\?\ 前綴，不受 MAX_PATH 限制）；不存在或失敗回 null。只能對已確認是本機固定或卸除式磁碟的路徑呼叫</summary>
    internal static string? TryGetLongPath(string normalized)
    {
        var input = LongPrefix + normalized;
        var buffer = new char[512];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var length = GetLongPathNameW(input, buffer, (uint)buffer.Length);
            if (length == 0)
                return null;
            if (length < buffer.Length)
            {
                var result = new string(buffer, 0, (int)length);
                return result.StartsWith(LongPrefix, StringComparison.Ordinal) ? result[LongPrefix.Length..] : result;
            }

            buffer = new char[length + 1];
        }

        return null;
    }

    /// <summary>以 FILE_FLAG_OPEN_REPARSE_POINT 讀屬性與重新解析標籤（不跟進這一層的連結）；成功回 0，否則回 Win32 錯誤碼</summary>
    internal static int TryGetAttributeTag(string normalized, out uint attributes, out uint tag)
    {
        attributes = 0;
        tag = 0;
        using var handle = CreateFileW(LongPrefix + normalized, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
            return Math.Max(1, Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var info, (uint)Marshal.SizeOf<FILE_ATTRIBUTE_TAG_INFO>()))
            return Math.Max(1, Marshal.GetLastWin32Error());
        attributes = info.FileAttributes;
        tag = info.ReparseTag;
        return 0;
    }

    private static bool SameDrive(string a, string b) => char.ToUpperInvariant(a[0]) == char.ToUpperInvariant(b[0]);

    /// <summary>FILETIME → UTC；超出 DateTime 範圍的值（壞掉的時間戳）夾到邊界，不丟例外</summary>
    private static DateTime FromFileTime(long fileTime)
        => fileTime <= 0 ? DateTime.FromFileTimeUtc(0)
            : fileTime >= MaxFileTime ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
            : DateTime.FromFileTimeUtc(fileTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveTypeW(string rootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FILE_ATTRIBUTE_TAG_INFO information, uint bufferSize);
}
