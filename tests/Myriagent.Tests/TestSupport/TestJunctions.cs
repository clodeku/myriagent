using Myriagent.Update;

namespace Myriagent.Tests.TestSupport;

/// <summary>
/// 測試建立的 junction（<c>mklink /J</c> 同一種，不需要系統管理員）與資料夾符號連結：記下每一個，Dispose 時倒序只刪連結本身
/// （不帶 recursive 的 <see cref="Directory.Delete(string)"/>，絕不走進目標）。要比暫存資料夾先 Dispose：
/// 宣告在暫存資料夾或 HubHarness 之後，<c>using</c> 會先收它
/// </summary>
internal sealed class TestJunctions : IDisposable
{
    private readonly List<string> links = [];

    /// <summary>在 <paramref name="link"/> 建立指向 <paramref name="target"/> 的 junction（上層資料夾不存在就先建），回 <paramref name="link"/></summary>
    public string Create(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Junction.Create(link, target);
        links.Add(link);
        return link;
    }

    /// <summary>資料夾符號連結（目標照原文存，可以是相對路徑或 UNC；建立時不檢查目標），回 <paramref name="link"/></summary>
    public string CreateSymbolicLink(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, target);
        links.Add(link);
        return link;
    }

    /// <summary>
    /// 磁碟區掛載點（同 mountvol 的重解析點：標籤 IO_REPARSE_TAG_MOUNT_POINT、SubstituteName <c>\??\Volume{GUID}\</c>；以 FSCTL_SET_REPARSE_POINT 直接設，
    /// 不需要系統管理員、不登記到掛載管理員）。<paramref name="volumeName"/> 是 <c>\\?\Volume{GUID}\</c>（<see cref="VolumeNameOf"/>）。回 <paramref name="link"/>；
    /// 收的時候同 junction 只刪連結本身（不帶 recursive 的 Directory.Delete）
    /// </summary>
    public string CreateVolumeMountPoint(string link, string volumeName)
    {
        if (!volumeName.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || !volumeName.EndsWith('\\'))
            throw new ArgumentException("要是 \\\\?\\Volume{GUID}\\", nameof(volumeName));
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateDirectory(link);
        links.Add(link);
        var substitute = System.Text.Encoding.Unicode.GetBytes(@"\??\" + volumeName[4..]);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.Unicode, leaveOpen: true))
        {
            // REPARSE_DATA_BUFFER（MountPointReparseBuffer）：tag、資料長度、保留、四個 offset／length，再接以 NUL 結尾的 SubstituteName 與空的 PrintName
            writer.Write(0xA0000003u);
            writer.Write((ushort)(8 + substitute.Length + 2 + 2));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)substitute.Length);
            writer.Write((ushort)(substitute.Length + 2));
            writer.Write((ushort)0);
            writer.Write(substitute);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
        }

        var data = stream.ToArray();
        using var handle = CreateFileW(link, 0x80000000 | 0x40000000, 0x7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException($"開啟 {link} 失敗", new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
        if (!DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException($"設定掛載點失敗：{link} → {volumeName}", new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
        return link;
    }

    /// <summary>磁碟根目錄（<c>C:\</c>）的磁碟區名稱 <c>\\?\Volume{GUID}\</c></summary>
    public static string VolumeNameOf(string driveRoot)
    {
        var buffer = new char[64];
        if (!GetVolumeNameForVolumeMountPointW(driveRoot, buffer, (uint)buffer.Length))
            throw new IOException($"取不到 {driveRoot} 的磁碟區名稱", new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
        return new string(buffer).TrimEnd('\0');
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, byte[] inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, [System.Runtime.InteropServices.Out] char[] volumeName, uint length);

    public void Dispose()
    {
        for (var i = links.Count - 1; i >= 0; i--)
        {
            try
            {
                // GetAttributes 讀的是連結本身（不跟進），目標不在了也刪得掉
                if ((File.GetAttributes(links[i]) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(links[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 已經不在（或刪不掉）：留給暫存資料夾的清理；遞迴刪除不會穿過 junction
            }
        }
    }
}
