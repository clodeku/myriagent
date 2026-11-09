using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Myriagent.Core;

namespace Myriagent.Hub;

/// <summary>網路芳鄰要問作業系統的事（電腦狀態面板的檔案傳輸；任何執行緒、可能很慢，呼叫端在背景並設逾時）。測試換成假的</summary>
internal interface INetworkShares
{
    /// <summary>主機名稱的位址（IP 字串直接解析；解析不出是空的）</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct);

    /// <summary>這台所有網卡的位址（含迴路）</summary>
    IReadOnlyCollection<IPAddress> LocalAddresses();

    /// <summary>這台的電腦名稱</summary>
    string MachineName { get; }

    /// <summary>對應的網路磁碟機（「Z:\」→「\\主機\分享」）</summary>
    IReadOnlyList<(string Root, string Target)> MappedDrives();

    /// <summary>網路上的電腦名稱（網路探索；關著時是空的）</summary>
    IReadOnlyList<string> Computers();

    /// <summary>一台電腦的資料夾分享（不含系統管理用的 C$ 這類）</summary>
    IReadOnlyList<string> Shares(string host);

    /// <summary>資料夾所在磁碟的可用空間（網路路徑也可以；讀不到是 null）</summary>
    long? FreeSpace(string directory);
}

/// <summary><see cref="INetworkShares"/> 的實作：DNS／LLMNR／NetBIOS 解析、WNet、NetShareEnum、GetDiskFreeSpaceEx</summary>
internal sealed class NetworkShares : INetworkShares
{
    public static NetworkShares Instance { get; } = new();

    public string MachineName => Environment.MachineName;

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var literal))
            return [literal];
        try
        {
            return await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            return [];
        }
    }

    public IReadOnlyCollection<IPAddress> LocalAddresses()
    {
        var list = new List<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                list.AddRange(nic.GetIPProperties().UnicastAddresses.Select(u => u.Address));
        }
        catch (NetworkInformationException ex)
        {
            Log.Warn($"列不出這台的網卡位址：{ex.Message}");
        }

        return list;
    }

    public IReadOnlyList<(string Root, string Target)> MappedDrives()
    {
        var result = new List<(string, string)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Network)
                    continue;
                var local = drive.Name.TrimEnd('\\');
                var buffer = new StringBuilder(1024);
                var length = buffer.Capacity;
                if (WNetGetConnectionW(local, buffer, ref length) == 0 && buffer.Length > 0)
                    result.Add((drive.Name.ToUpperInvariant(), buffer.ToString()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 剛中斷的網路磁碟機：略過
            }
        }

        return result;
    }

    public IReadOnlyList<string> Computers()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        // 現在的 Windows 舊式列舉多半回 6118（沒有瀏覽清單），主要靠 NetBIOS 與 WS-Discovery 自己找（NetworkDiscovery）
        try
        {
            foreach (var name in NetworkDiscovery.FindAsync(CancellationToken.None).GetAwaiter().GetResult())
                names.Add(name);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            Log.Info($"找網路上的電腦失敗：{ex.Message}");
        }

        // 已經連過的分享（net use、網路磁碟機）的主機
        foreach (var scope in new[] { ResourceConnected, ResourceRemembered })
            foreach (var remote in EnumRemoteNames(scope))
            {
                if (remote.StartsWith(@"\\", StringComparison.Ordinal) && remote[2..].Split('\\')[0] is { Length: > 0 } host && NetworkPathRules.IsHostName(host))
                    names.Add(host);
            }

        // 舊式的網路芳鄰列舉（RESOURCE_CONTEXT；有瀏覽清單的網域或工作群組才有）
        foreach (var remote in EnumRemoteNames(ResourceContext, serversOnly: true))
            names.Add(remote[2..]);
        names.RemoveWhere(n => string.Equals(n, Environment.MachineName, StringComparison.OrdinalIgnoreCase));
        return names.ToList();
    }

    /// <summary>WNet 列舉一層的遠端名稱（<c>\\主機</c> 或 <c>\\主機\分享</c>）；serversOnly：只要電腦</summary>
    private static List<string> EnumRemoteNames(int scope, bool serversOnly = false)
    {
        var names = new List<string>();
        if (WNetOpenEnumW(scope, ResourceTypeDisk, 0, IntPtr.Zero, out var handle) != 0)
            return names;
        var buffer = Marshal.AllocHGlobal(BufferBytes);
        try
        {
            while (true)
            {
                var count = -1;
                var size = BufferBytes;
                var status = WNetEnumResourceW(handle, ref count, buffer, ref size);
                if (status != 0)
                    break;
                var itemSize = Marshal.SizeOf<NETRESOURCE>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<NETRESOURCE>(buffer + i * itemSize);
                    if ((!serversOnly || item.dwDisplayType == ResourceDisplayTypeServer) && item.lpRemoteName is { Length: > 2 } remote && remote.StartsWith(@"\\", StringComparison.Ordinal))
                        names.Add(remote);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            WNetCloseEnum(handle);
        }

        return names;
    }

    public IReadOnlyList<string> Shares(string host)
    {
        var result = new List<string>();
        var resume = 0;
        var status = NetShareEnum(@"\\" + host, 1, out var buffer, -1, out var read, out _, ref resume);
        try
        {
            if (status is not (0 or 234) || buffer == IntPtr.Zero)
                return result;
            var itemSize = Marshal.SizeOf<SHARE_INFO_1>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<SHARE_INFO_1>(buffer + i * itemSize);
                // 只要資料夾（STYPE_DISKTREE），不要系統管理用的特殊分享（STYPE_SPECIAL：C$、ADMIN$）
                if ((info.shi1_type & 0xFF) == 0 && (info.shi1_type & 0x80000000) == 0 && !string.IsNullOrEmpty(info.shi1_netname))
                    result.Add(info.shi1_netname);
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                NetApiBufferFree(buffer);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public long? FreeSpace(string directory)
        => GetDiskFreeSpaceExW(directory.EndsWith('\\') ? directory : directory + @"\", out var available, out _, out _) ? (long)available : null;

    private const int ResourceConnected = 1;
    private const int ResourceRemembered = 3;
    private const int ResourceContext = 5;
    private const int ResourceTypeDisk = 1;
    private const int ResourceDisplayTypeServer = 2;
    private const int BufferBytes = 64 * 1024;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1
    {
        public string shi1_netname;
        public uint shi1_type;
        public string? shi1_remark;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnectionW(string localName, StringBuilder remoteName, ref int length);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetOpenEnumW(int scope, int type, int usage, IntPtr netResource, out IntPtr handle);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetEnumResourceW(IntPtr handle, ref int count, IntPtr buffer, ref int bufferSize);

    [DllImport("mpr.dll")]
    private static extern int WNetCloseEnum(IntPtr handle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string serverName, int level, out IntPtr buffer, int preferredMaxLength, out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
}
