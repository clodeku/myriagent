using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Management;
using System.Runtime.InteropServices;
using Myriagent.Core;
using Myriagent.Sync;

namespace Myriagent.SystemStatus;

/// <summary>
/// 這台的 CPU 溫度（stat1 的 <c>cpuTemp</c>、<c>cpuTempSource</c>）。真正的 CPU 溫度要核心驅動程式才讀得到（要管理員），app 不裝驅動程式，
/// 只讀已經在跑的監控工具與 Windows 現成的值，依序：HWiNFO 的共享記憶體、LibreHardwareMonitor／OpenHardwareMonitor 的 WMI、ACPI 熱區
/// （效能計數器；筆電常有，桌機常沒有或是固定值）。讀不到的來源隔一段時間才再試（工具晚一點才開也會接上，優先的來源恢復就換回去）。
/// 全部只讀，解析規則在 <see cref="DeviceSensorRules"/>。不是執行緒安全：呼叫端（<see cref="SystemMonitor"/> 的取樣鎖）序列化
/// </summary>
internal sealed class CpuTemperatureReader : IDisposable
{
    private readonly Source[] sources;
    private readonly AcpiThermalZone? acpi;

    /// <summary>一個來源：<paramref name="Read"/> 讀不到回 null，之後 <paramref name="Retry"/> 這麼久才再試</summary>
    internal sealed record Source(string Id, Func<double?> Read, TimeSpan Retry)
    {
        public long RetryAt { get; set; }

        public bool Warned { get; set; }
    }

    public CpuTemperatureReader()
    {
        acpi = new AcpiThermalZone();
        sources =
        [
            new(DeviceSensorRules.SourceHwInfo, () => ReadHwInfo(DeviceSensorRules.HwInfoMapName), TimeSpan.FromSeconds(10)),
            new(DeviceSensorRules.SourceLibreHardwareMonitor, () => HardwareMonitorWmi.Read(@"root\LibreHardwareMonitor"), TimeSpan.FromSeconds(30)),
            new(DeviceSensorRules.SourceOpenHardwareMonitor, () => HardwareMonitorWmi.Read(@"root\OpenHardwareMonitor"), TimeSpan.FromSeconds(30)),
            new(DeviceSensorRules.SourceAcpi, acpi.Read, TimeSpan.FromMinutes(1)),
        ];
    }

    internal CpuTemperatureReader(params Source[] sources) => this.sources = sources;

    /// <summary>第一個讀得到的來源的溫度（攝氏）與來源代號；都讀不到回 null</summary>
    public (double Celsius, string Source)? Read()
    {
        var now = Stopwatch.GetTimestamp();
        foreach (var source in sources)
        {
            if (now < source.RetryAt)
                continue;
            double? value = null;
            try
            {
                value = source.Read();
            }
            catch (Exception ex)
            {
                // 解析之外的意外（不該發生）：每個來源只記一次，照樣隔一段時間再試
                if (!source.Warned)
                    Log.Warn($"讀 CPU 溫度（{source.Id}）失敗：{ex.Message}");
                source.Warned = true;
            }
            if (value is { } celsius)
                return (celsius, source.Id);
            source.RetryAt = now + (long)(source.Retry.TotalSeconds * Stopwatch.Frequency);
        }
        return null;
    }

    public void Dispose() => acpi?.Dispose();

    /// <summary>HWiNFO 的共享記憶體（每次開、讀、關：HWiNFO 關掉之後不會讀到凍結的舊值）；沒開共享記憶體、格式不對回 null</summary>
    internal static double? ReadHwInfo(string mapName)
    {
        byte[]? data = null;
        try
        {
            using var map = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (view.Capacity < DeviceSensorRules.HwInfoHeaderSize)
                return null;
            var header = new byte[DeviceSensorRules.HwInfoHeaderSize];
            view.ReadArray(0, header, 0, header.Length);
            if (DeviceSensorRules.HwInfoRequiredLength(header) is not { } length || length > view.Capacity)
                return null;
            data = ArrayPool<byte>.Shared.Rent(length);
            view.ReadArray(0, data, 0, length);
            return DeviceSensorRules.HwInfoCpuTemperature(data.AsSpan(0, length));
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (data is not null)
                ArrayPool<byte>.Shared.Return(data);
        }
    }

    /// <summary>
    /// ACPI 熱區（效能計數器 Thermal Zone Information）：先用十分之一 K 的 High Precision Temperature，舊的 Windows 沒有時用 Temperature（K）。
    /// 第一次讀才開查詢；這台沒有這個計數器時一直回 null
    /// </summary>
    private sealed class AcpiThermalZone : IDisposable
    {
        private PdhQuery? query;
        private IntPtr? counter;
        private bool tenths;
        private bool opened;

        public double? Read()
        {
            if (!opened)
            {
                opened = true;
                query = PdhQuery.Open();
                counter = query?.AddCounter(@"\Thermal Zone Information(*)\High Precision Temperature");
                tenths = counter is not null;
                counter ??= query?.AddCounter(@"\Thermal Zone Information(*)\Temperature");
            }
            if (query is null || counter is not { } c || !query.Collect())
                return null;
            return DeviceSensorRules.ThermalZoneCelsius(query.Read(c).Select(v => v.Value), tenths);
        }

        public void Dispose()
        {
            query?.Dispose();
            query = null;
        }
    }
}

/// <summary>
/// LibreHardwareMonitor／OpenHardwareMonitor 的 WMI（工具開著、而且以管理員執行讀得到感測器時才有這個命名空間）。
/// 獨立一個類別：沒用到時不載入 System.Management
/// </summary>
internal static class HardwareMonitorWmi
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>CPU 溫度（<see cref="DeviceSensorRules.HardwareMonitorCpuTemperature"/>）；命名空間不存在（工具沒開）、查詢失敗回 null</summary>
    public static double? Read(string scopePath)
    {
        try
        {
            var scope = new ManagementScope(scopePath, new ConnectionOptions { Timeout = Timeout });
            scope.Connect();
            var query = new ObjectQuery("SELECT Name, Parent, Value FROM Sensor WHERE SensorType = 'Temperature'");
            using var searcher = new ManagementObjectSearcher(scope, query, new System.Management.EnumerationOptions { Timeout = Timeout, Rewindable = false, ReturnImmediately = true });
            using var results = searcher.Get();
            var sensors = new List<(string?, string?, double)>();
            foreach (var item in results)
            {
                using (item)
                {
                    var value = item["Value"] is IConvertible v ? v.ToDouble(CultureInfo.InvariantCulture) : double.NaN;
                    sensors.Add((item["Name"] as string, item["Parent"] as string, value));
                }
            }
            return DeviceSensorRules.HardwareMonitorCpuTemperature(sensors);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException or FormatException
                                       or OverflowException or System.TimeoutException)
        {
            return null;
        }
    }
}
