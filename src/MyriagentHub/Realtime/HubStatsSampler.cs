using System.ComponentModel;
using System.Runtime.InteropServices;
using Myriagent.Sync;
using Microsoft.Win32;

namespace MyriagentHub.Realtime;

/// <summary>
/// MyriagentHub 主機自己的負載（stat1 的 <c>hub</c>；docs/sync-protocol.md 14.8）：同桌面的取樣（<see cref="DeviceStatsSampler"/>，Protocol 共用），
/// CPU 累計時間與實體記憶體以這裡的 Win32 呼叫讀（只讀，不改任何設定）。只在有人在看時由轉送迴圈每 2 秒取一次；以鎖序列化
/// </summary>
internal sealed class HubStatsSampler(ILogger<HubStatsSampler> log)
{
    private readonly object gate = new();
    private DeviceStatsSampler? sampler;

    /// <summary>這台主機的電腦名稱（給擁有者看）：清理後不合格時是「MyriagentHub」</summary>
    public string Name { get; } = LabelRulesName(Environment.MachineName);

    public DeviceStats Sample()
    {
        lock (gate)
        {
            sampler ??= new DeviceStatsSampler(ReadCpuTimes, ReadMemory, ReadCpuName(), message => log.LogWarning("{Message}", message));
            return sampler.Sample();
        }
    }

    private static string LabelRulesName(string? machineName) =>
        Myriagent.Security.LabelRules.Clean(machineName, DeviceStatsRules.MaxHubNameLength) ?? "MyriagentHub";

    private static CpuTimes? ReadCpuTimes() => GetSystemTimes(out var idle, out var kernel, out var user) ? new CpuTimes(idle, kernel, user) : null;

    private static (long Used, long Total)? ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
            return null;
        var total = (long)Math.Min(status.TotalPhys, (ulong)DeviceStatsRules.MaxBytes);
        return (total - (long)Math.Min(status.AvailPhys, (ulong)total), total);
    }

    private static string? ReadCpuName()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or Win32Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>FILETIME 以 64 位元整數接（低位在前，與 FILETIME 的記憶體配置相同）</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);
}
