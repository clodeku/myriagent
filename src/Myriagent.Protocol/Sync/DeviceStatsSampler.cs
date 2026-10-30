using System.Diagnostics;
using System.Net.NetworkInformation;
using Myriagent.Security;

namespace Myriagent.Sync;

/// <summary>
/// 讀一台電腦的狀態（stat1 的 <see cref="DeviceStats"/>；桌面的 <c>SystemMonitor</c> 與伺服器的 <c>HubStatsSampler</c> 共用）。
/// CPU 以兩次 GetSystemTimes 的差算使用率、網路以兩次累計位元組的差算速率，所以每次的值是「上一次取樣到這一次」的平均（第一次是建構到第一次取樣）。
/// 磁碟只讀固定磁碟（網路磁碟、光碟、隨身碟都不讀：不會卡在斷線的網路磁碟上），網路只算 <see cref="DeviceStatsSampleRules.CountsTraffic"/> 的網卡。
/// Protocol 不能有 P/Invoke：CPU 的累計值與實體記憶體由呼叫端以 <paramref name="readCpu"/>、<paramref name="readMemory"/> 提供（讀不到回 null）；
/// 顯示卡與 CPU 溫度也由呼叫端提供（<paramref name="readGpus"/>、<paramref name="readCpuTemp"/>，桌面才有，伺服器主機不給）。
/// 網路的累計值可由呼叫端以 <paramref name="readNetwork"/> 提供（桌面：GetIfTable2，一次約 0.1 ms）；沒給的用 .NET 的 NetworkInterface（<see cref="ReadNetworkBytes"/>，
/// 一次約 20 ms、篩選器依實體位址去重）。不是執行緒安全：呼叫端自己序列化。某一項讀不到時那一項給保守值（0、空或 null），不丟例外
/// </summary>
internal sealed class DeviceStatsSampler(
    Func<CpuTimes?> readCpu,
    Func<(long Used, long Total)?> readMemory,
    string? cpuName,
    Action<string>? warn = null,
    Func<IReadOnlyList<GpuStats>?>? readGpus = null,
    Func<(double Celsius, string Source)?>? readCpuTemp = null,
    Func<(long Rx, long Tx)?>? readNetwork = null)
{
    private bool gpuFailed;
    private bool tempFailed;
    private bool netFailed;

    private readonly int cores = Environment.ProcessorCount;
    private CpuTimes? previousCpu = readCpu();
    private (long Rx, long Tx)? previousNet = FirstNetwork(readNetwork, warn);
    private long previousAt = Stopwatch.GetTimestamp();

    /// <summary>建構時的網路累計值（讀取函式丟例外算讀不到；警告留給取樣時記）</summary>
    private static (long Rx, long Tx)? FirstNetwork(Func<(long Rx, long Tx)?>? read, Action<string>? warn)
    {
        if (read is null)
            return ReadNetworkBytes(warn);
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>網路的累計值：呼叫端給了讀取函式就用它（丟例外算讀不到、只記一次警告），沒給的用 <see cref="ReadNetworkBytes"/></summary>
    private (long Rx, long Tx)? ReadNetwork()
    {
        if (readNetwork is null)
            return ReadNetworkBytes(warn);
        try
        {
            return readNetwork();
        }
        catch (Exception ex)
        {
            if (!netFailed)
            {
                netFailed = true;
                warn?.Invoke($"讀不到網路流量：{ex.Message}");
            }
            return null;
        }
    }

    /// <summary>取一次樣（<c>rttMs</c> 是 null：延遲由同步引擎量）</summary>
    public DeviceStats Sample()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = Stopwatch.GetElapsedTime(previousAt, now).TotalMilliseconds;
        previousAt = now;

        var cpuTimes = readCpu();
        double cpu = 0;
        if (previousCpu is { } before && cpuTimes is { } after)
            cpu = DeviceStatsRules.CpuPercent(before, after) ?? 0;
        previousCpu = cpuTimes ?? previousCpu;

        var net = ReadNetwork();
        long rx = 0, tx = 0;
        if (previousNet is { } netBefore && net is { } netAfter)
        {
            rx = DeviceStatsRules.Rate(netBefore.Rx, netAfter.Rx, elapsedMs);
            tx = DeviceStatsRules.Rate(netBefore.Tx, netAfter.Tx, elapsedMs);
        }
        previousNet = net ?? previousNet;

        var (memUsed, memTotal) = Memory();
        var temp = CpuTemperature();
        return new DeviceStats(cpu, memUsed, memTotal, ReadDisks(warn), rx, tx,
            Cores: cores is >= 1 and <= DeviceStatsRules.MaxCores ? cores : null,
            CpuName: DeviceStatsSampleRules.CpuName(cpuName),
            UptimeSec: Math.Min(DeviceStatsRules.MaxUptimeSec, Environment.TickCount64 / 1000),
            Gpus: Gpus(),
            CpuTemp: temp?.Celsius,
            CpuTempSource: temp?.Source);
    }

    /// <summary>
    /// 顯示卡：最多 <see cref="DeviceStatsRules.MaxGpus"/> 張，不合格的略過（<see cref="DeviceStatsRules.Check"/> 一定合格）；沒有讀取器、讀不到、
    /// 一張都沒有回 null。讀取器丟例外時只記一次警告（每次取樣都會再試）
    /// </summary>
    private IReadOnlyList<GpuStats>? Gpus()
    {
        if (readGpus is null)
            return null;
        IReadOnlyList<GpuStats>? gpus;
        try
        {
            gpus = readGpus();
        }
        catch (Exception ex)
        {
            if (!gpuFailed)
                warn?.Invoke($"讀不到顯示卡的狀態：{ex.Message}");
            gpuFailed = true;
            return null;
        }
        var valid = (gpus ?? []).Where(g => DeviceStatsRules.CheckGpu(g) is null).Take(DeviceStatsRules.MaxGpus).ToList();
        return valid.Count == 0 ? null : valid;
    }

    /// <summary>CPU 溫度與來源：溫度要通過 <see cref="DeviceSensorRules.Celsius"/>、來源要是乾淨的短字串，否則兩個都是 null（例外同 <see cref="Gpus"/>）</summary>
    private (double Celsius, string Source)? CpuTemperature()
    {
        if (readCpuTemp is null)
            return null;
        (double Celsius, string Source)? temp;
        try
        {
            temp = readCpuTemp();
        }
        catch (Exception ex)
        {
            if (!tempFailed)
                warn?.Invoke($"讀不到 CPU 溫度：{ex.Message}");
            tempFailed = true;
            return null;
        }
        if (temp is not { } t || DeviceSensorRules.Celsius(t.Celsius) is not { } celsius
            || !LabelRules.IsClean(t.Source, DeviceStatsRules.MaxTempSourceLength))
            return null;
        return (celsius, t.Source);
    }

    /// <summary>實體記憶體的已用、總量（夾在上限內；讀不到時是 0／1，<see cref="DeviceStatsRules.Check"/> 照樣合格）</summary>
    private (long Used, long Total) Memory()
    {
        if (readMemory() is not { Total: > 0 } memory)
            return (0, 1);
        var total = Math.Min(memory.Total, DeviceStatsRules.MaxBytes);
        return (Math.Clamp(memory.Used, 0, total), total);
    }

    /// <summary>固定磁碟（最多 <see cref="DeviceStatsRules.MaxDisks"/> 個）；讀不到的略過</summary>
    public static List<DiskStats> ReadDisks(Action<string>? warn = null)
    {
        var disks = new List<DiskStats>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (disks.Count == DeviceStatsRules.MaxDisks)
                    break;
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady || DeviceStatsSampleRules.DiskName(drive.Name) is not { } name)
                        continue;
                    var total = drive.TotalSize;
                    if (total <= 0 || total > DeviceStatsRules.MaxBytes)
                        continue;
                    disks.Add(new DiskStats(name, Math.Clamp(total - drive.TotalFreeSpace, 0, total), total));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 這個磁碟讀不到（BitLocker 鎖住、正在卸除）：略過
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"讀不到磁碟清單：{ex.Message}");
        }
        return disks;
    }

    /// <summary>算進流量的網卡累計收送位元組的合計（篩選器依實體位址只算本體一次，<see cref="DeviceStatsSampleRules.OncePerAddress"/>）；整個讀不到回 null</summary>
    public static (long Rx, long Tx)? ReadNetworkBytes(Action<string>? warn = null)
    {
        try
        {
            long rx = 0, tx = 0;
            var counted = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => DeviceStatsSampleRules.CountsTraffic(nic.NetworkInterfaceType, nic.OperationalStatus, nic.Description));
            foreach (var nic in DeviceStatsSampleRules.OncePerAddress(counted, AddressOf))
            {
                try
                {
                    var stats = nic.GetIPStatistics();
                    rx += Math.Max(0, stats.BytesReceived);
                    tx += Math.Max(0, stats.BytesSent);
                }
                catch (NetworkInformationException)
                {
                    // 網卡剛好被停用或拔掉：略過這一張
                }
            }
            return (rx, tx);
        }
        catch (NetworkInformationException ex)
        {
            warn?.Invoke($"讀不到網路介面：{ex.Message}");
            return null;
        }
    }

    /// <summary>網卡的實體位址（十六進位字串）；讀不到或沒有的回 null（那張各自算）</summary>
    private static string? AddressOf(NetworkInterface nic)
    {
        try
        {
            var bytes = nic.GetPhysicalAddress().GetAddressBytes();
            return bytes.Length == 0 || bytes.All(b => b == 0) ? null : Convert.ToHexString(bytes);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
