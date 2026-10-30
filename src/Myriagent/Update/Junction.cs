using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Myriagent.Update;

/// <summary>
/// junction（掛載點重解析點，不需要管理員權限）：建立、原地改指向、讀目標。改指向是對既有的 junction 再設一次同一種重解析點
/// （FSCTL_SET_REPARSE_POINT 會取代同標籤的舊資料），沒有「current 不存在」的空窗；已開啟的檔案在開啟時就解析好了，不受影響
/// </summary>
internal static class Junction
{
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint ReparseTagMountPoint = 0xA0000003;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const string NonInterpretedPrefix = @"\??\";
    private const int MaxReparseBuffer = 16 * 1024;

    /// <summary>在 link 建立指向 target 的 junction（link 不能已存在；target 必須是存在的資料夾）</summary>
    public static void Create(string link, string target)
    {
        target = Path.GetFullPath(target);
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException($"junction 的目標不存在：{target}");
        if (Directory.Exists(link) || File.Exists(link))
            throw new IOException($"已經存在：{link}");
        Directory.CreateDirectory(link);
        try
        {
            SetTarget(link, target);
        }
        catch
        {
            Directory.Delete(link);
            throw;
        }
    }

    /// <summary>把既有的 junction 改指向 target（不先刪除）</summary>
    public static void Retarget(string link, string target)
    {
        target = Path.GetFullPath(target);
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException($"junction 的目標不存在：{target}");
        if (!IsJunction(link))
            throw new IOException($"不是 junction：{link}");
        SetTarget(link, target);
    }

    public static bool IsJunction(string path) => GetTarget(path) != null;

    /// <summary>junction 的目標（完整路徑）；不存在或不是 junction 回 null</summary>
    public static string? GetTarget(string link)
    {
        if (!Directory.Exists(link) || !new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint))
            return null;
        using var handle = Open(link, GenericRead);
        var buffer = new byte[MaxReparseBuffer];
        if (!DeviceIoControl(handle, FsctlGetReparsePoint, IntPtr.Zero, 0, buffer, buffer.Length, out _, IntPtr.Zero))
            return null;
        if (BitConverter.ToUInt32(buffer, 0) != ReparseTagMountPoint)
            return null;
        int substituteOffset = BitConverter.ToUInt16(buffer, 8);
        int substituteLength = BitConverter.ToUInt16(buffer, 10);
        var name = Encoding.Unicode.GetString(buffer, 16 + substituteOffset, substituteLength);
        return name.StartsWith(NonInterpretedPrefix, StringComparison.Ordinal) ? name[NonInterpretedPrefix.Length..] : name;
    }

    private static void SetTarget(string link, string target)
    {
        var substitute = Encoding.Unicode.GetBytes(NonInterpretedPrefix + target);
        var print = Encoding.Unicode.GetBytes(target);
        // REPARSE_DATA_BUFFER（MountPointReparseBuffer）：tag、資料長度、保留、四個 offset／length，再接兩個以 NUL 結尾的名稱
        int pathBytes = substitute.Length + 2 + print.Length + 2;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(ReparseTagMountPoint);
            writer.Write((ushort)(8 + pathBytes));
            writer.Write((ushort)0);
            writer.Write((ushort)0);                          // SubstituteNameOffset
            writer.Write((ushort)substitute.Length);          // SubstituteNameLength
            writer.Write((ushort)(substitute.Length + 2));    // PrintNameOffset
            writer.Write((ushort)print.Length);               // PrintNameLength
            writer.Write(substitute);
            writer.Write((ushort)0);
            writer.Write(print);
            writer.Write((ushort)0);
        }
        var data = stream.ToArray();
        using var handle = Open(link, GenericRead | GenericWrite);
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, pinned.AddrOfPinnedObject(), data.Length, null, 0, out _, IntPtr.Zero))
                throw new IOException($"設定 junction 失敗：{link} → {target}", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally
        {
            pinned.Free();
        }
    }

    private static SafeFileHandle Open(string path, uint access)
    {
        var handle = CreateFileW(path, access, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException($"開啟 {path} 失敗", new Win32Exception(Marshal.GetLastWin32Error()));
        return handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize, byte[]? outBuffer, int outSize, out int returned, IntPtr overlapped);
}
