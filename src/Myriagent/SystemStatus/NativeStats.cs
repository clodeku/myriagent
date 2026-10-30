using System.ComponentModel;
using System.Runtime.InteropServices;
using Myriagent.Core;
using Myriagent.Sync;
using Microsoft.Win32;

namespace Myriagent.SystemStatus;

/// <summary>
/// 這台電腦的 Win32 讀值（<see cref="DeviceStatsSampler"/> 在 Protocol，不能有 P/Invoke）：CPU 的累計時間（GetSystemTimes）、
/// 實體記憶體（GlobalMemoryStatusEx）、CPU 名稱（登錄的 ProcessorNameString）；顯示卡在 <see cref="GpuReader"/>、CPU 溫度在 <see cref="CpuTemperatureReader"/>
/// </summary>
internal static class NativeStats
{
    /// <summary>
    /// 這台的取樣器（建構時讀一次累計值，第一次取樣是建構到那時的平均）。顯示卡與溫度的讀取器第一次取樣才開始讀（app 啟動時不多做事），
    /// 跟著 app 的生命週期，不另外釋放
    /// </summary>
    public static DeviceStatsSampler CreateSampler()
    {
        var gpus = new GpuReader();
        var temperature = new CpuTemperatureReader();
        return new(ReadCpuTimes, ReadMemory, ReadCpuName(), Log.Warn, gpus.Read, temperature.Read, ReadNetwork);
    }

    /// <summary>
    /// 網路的累計值改用對話框網路燈的 GetIfTable2（NetworkTotals：只算硬體網卡、篩選器不重複，一次約 0.1 ms）：Protocol 預設的 NetworkInterface
    /// 每次約 20 ms，電腦狀態面板開著時每秒都讀一次
    /// </summary>
    private static (long Rx, long Tx)? ReadNetwork()
        => NetworkTotals.Read() is { } totals ? ((long)Math.Min(totals.Rx, long.MaxValue), (long)Math.Min(totals.Tx, long.MaxValue)) : null;

    public static CpuTimes? ReadCpuTimes() => GetSystemTimes(out var idle, out var kernel, out var user) ? new CpuTimes(idle, kernel, user) : null;

    public static (long Used, long Total)? ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
            return null;
        var total = (long)Math.Min(status.TotalPhys, (ulong)DeviceStatsRules.MaxBytes);
        return (total - (long)Math.Min(status.AvailPhys, (ulong)total), total);
    }

    public static string? ReadCpuName()
    {
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
