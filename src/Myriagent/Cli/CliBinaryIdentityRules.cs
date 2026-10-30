using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Myriagent.Agents;
using Microsoft.Win32.SafeHandles;

namespace Myriagent.Cli;

/// <summary>
/// 一個檔案在檔案系統上的身分欄位（CliBinaryIdentity 去掉 CLI 資訊的部分）：磁碟區序號＋128 位元檔案識別碼＋大小＋最後寫入時間。
/// 換檔（改名換上新檔）後 FileId 一定會變；不用建立時間（NTFS tunneling 讓新檔沿用舊檔的建立時間）
/// </summary>
internal readonly record struct CliFileStamp(ulong VolumeSerial, UInt128 FileId, long Size, DateTime LastWriteUtc);

/// <summary>
/// 讀 CLI 原生執行檔的身分與版本（不啟動程序）。
/// <para>
/// 任何讀 CLI 執行檔（與它旁邊的套件檔）的程式碼都以 <c>FileShare.ReadWrite | FileShare.Delete</c> 開檔、用完立即關：
/// 只用 FileShare.Read 開著時，Claude 換檔的改名會失敗（實測 ERROR_SHARING_VIOLATION／Node EBUSY）。
/// 取身分只要 FILE_READ_ATTRIBUTES；FileVersionInfo 在 2026-10-10 實測不擋改名（與 FileShare.Read 的對照組一起測，
/// 見 CliBinaryIdentityRulesTests 的「讀身分與版本時另一個程序改名不會失敗」）。
/// </para>
/// </summary>
internal static class CliBinaryIdentityRules
{
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x0010;
    private const int FileIdInfoClass = 18;   // FILE_INFO_BY_HANDLE_CLASS.FileIdInfo
    private const int FileIdInfoSize = 24;    // FILE_ID_INFO：ULONGLONG VolumeSerialNumber＋FILE_ID_128（16 個位元組）

    /// <summary>
    /// 讀這個安裝的原生執行檔（CliInstallation.NativeExecutable）的身分；version 是呼叫端確認過的版本（三方一致時的值），
    /// 會轉成 x.y.z[-pre]。執行檔不存在、讀不到、還沒解析出原生執行檔、版本無法解析時回 null（身分不明不構成重開的理由）
    /// </summary>
    public static CliBinaryIdentity? TryRead(CliInstallation installation, string version)
        => TryRead(installation.Provider, installation.Kind, installation.NativeExecutable, version);

    /// <summary>讀指定路徑的原生執行檔身分；失敗回 null（同上）</summary>
    public static CliBinaryIdentity? TryRead(AgentProvider provider, CliInstallKind kind, string? nativeExecutable, string? version)
    {
        if (string.IsNullOrWhiteSpace(nativeExecutable) || CliVersionRules.ParseVersion(version) == null)
            return null;
        return TryReadStamp(nativeExecutable) is { } stamp ? Create(provider, kind, nativeExecutable, version, stamp) : null;
    }

    /// <summary>純函式：由檔案欄位組出身分；版本轉成 x.y.z[-pre]（「2.1.295.0」→「2.1.295」），無法解析回 null</summary>
    public static CliBinaryIdentity? Create(AgentProvider provider, CliInstallKind kind, string nativeExecutable, string? version, CliFileStamp stamp)
    {
        var normalized = CliVersionRules.ParseVersion(version);
        return normalized == null
            ? null
            : new CliBinaryIdentity(provider, kind, nativeExecutable, normalized, stamp.VolumeSerial, stamp.FileId, stamp.Size, stamp.LastWriteUtc);
    }

    /// <summary>
    /// 讀檔案的身分欄位：只要 FILE_READ_ATTRIBUTES、共用 ReadWrite|Delete 開檔（不擋換檔），讀完立即關。
    /// 不存在、是資料夾、打不開回 null
    /// </summary>
    public static CliFileStamp? TryReadStamp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        using var handle = CreateFile(path, FILE_READ_ATTRIBUTES, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;
        if (!GetFileInformationByHandle(handle, out var info) || (info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            return null;
        var buffer = new byte[FileIdInfoSize];
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, buffer, FileIdInfoSize))
            return null;
        var volume = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(0, 8));
        var fileId = ParseFileId(buffer.AsSpan(8, 16));
        var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        var written = DateTime.FromFileTimeUtc(((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow);
        return new CliFileStamp(volume, fileId, size, written);
    }

    /// <summary>
    /// 純函式：FILE_ID_INFO.FileId（FILE_ID_128 的 16 個位元組）依 little-endian 解讀成 128 位元整數。
    /// 與 fsutil file queryfileid 印的十六進位相同（例：0x000000000000000000220000003bf109）
    /// </summary>
    public static UInt128 ParseFileId(ReadOnlySpan<byte> identifier)
    {
        if (identifier.Length != 16)
            throw new ArgumentException("FILE_ID_128 必須是 16 個位元組", nameof(identifier));
        return new UInt128(BinaryPrimitives.ReadUInt64LittleEndian(identifier[8..]), BinaryPrimitives.ReadUInt64LittleEndian(identifier[..8]));
    }

    /// <summary>
    /// claude.exe 的檔案版本資訊（FileVersionInfo，約 0.23 ms、不啟動程序）→ x.y.z；不存在、沒有版本資源回 null。
    /// codex.exe 沒有版本資源（每一份都是空的），Codex 要讀 package.json 或跑 --version
    /// </summary>
    public static string? ReadClaudeFileVersion(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            return ParseFileVersion(info.FileVersion, info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// 純函式：檔案版本資訊 → x.y.z。先解析 FileVersion 字串（「2.1.296.0」→「2.1.296」），
    /// 字串是空的就用 VS_FIXEDFILEINFO 的數字（全是 0＝沒有版本資源，回 null）
    /// </summary>
    public static string? ParseFileVersion(string? fileVersion, int major, int minor, int build)
        => CliVersionRules.ParseVersion(fileVersion)
           ?? (major == 0 && minor == 0 && build == 0 ? null : $"{major}.{minor}.{build}");

    /// <summary>
    /// npm 版 Codex 的兩個版本來源：套件根目錄（…\node_modules\@openai\codex）的 package.json，與實際執行的 vendor 目錄裡的
    /// codex-package.json（沒有這個檔的舊配置改讀平台套件的 package.json，去掉「-win32-x64」字尾）。讀不到的是 null。
    /// 兩者不一致（npm rollback 留下的混版）＝安裝損壞，見 CliInstallStabilityRules.Evaluate
    /// </summary>
    public static (string? Package, string? Vendor) ReadCodexPackageVersions(string packageRoot)
    {
        var package = ReadPackageVersion(Path.Combine(packageRoot, "package.json"));
        var vendorRoot = CliLocator.FindCodexVendorRoot(packageRoot, File.Exists);
        string? vendor = null;
        if (vendorRoot != null)
        {
            vendor = ReadPackageVersion(Path.Combine(vendorRoot, "codex-package.json"));
            // vendor\<triple> 的上兩層是平台套件（@openai\codex-win32-x64）；套件自己的 vendor（舊配置）沒有平台套件
            var platformPackage = Path.GetDirectoryName(Path.GetDirectoryName(vendorRoot));
            if (vendor == null && platformPackage != null && !SamePath(platformPackage, packageRoot))
                vendor = CliVersionRules.ParsePackageJsonVersion(ReadSharedText(Path.Combine(platformPackage, "package.json")), platformPackage: true);
        }
        return (package, vendor);
    }

    /// <summary>讀 package.json（或 codex-package.json）的 version；不存在、讀不到、格式不符回 null</summary>
    public static string? ReadPackageVersion(string? packageJsonPath)
        => string.IsNullOrWhiteSpace(packageJsonPath) ? null : CliVersionRules.ParsePackageJsonVersion(ReadSharedText(packageJsonPath));

    /// <summary>
    /// 以不擋改名與刪除的方式讀小文字檔（UTF-8，有 BOM 也可以）：CLI 安裝裡的 package.json、Claude 的鎖定檔。
    /// 超過 maxBytes 或讀不到回 null
    /// </summary>
    internal static string? ReadSharedText(string path, int maxBytes = 1 << 20)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            if (stream.Length > maxBytes)
                return null;
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool SamePath(string a, string b)
        => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, [Out] byte[] information, uint bufferSize);
}
