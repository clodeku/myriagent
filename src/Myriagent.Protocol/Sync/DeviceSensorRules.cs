using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Myriagent.Sync;

/// <summary>
/// 電腦狀態的 GPU 與 CPU 溫度（stat1 的 <c>gpus</c>、<c>cpuTemp</c>）的純函式規則：桌面的 <c>SystemStatus</c> 讀原始值（效能計數器、DXGI、
/// HWiNFO 的共享記憶體、LibreHardwareMonitor 的 WMI），這裡解析與彙總（Protocol 不能有 P/Invoke）。
/// 一律只讀、不需要管理員：真正的 CPU 溫度要核心驅動程式才讀得到，所以只讀已經在跑的監控工具，筆電另有 ACPI 熱區
/// </summary>
internal static class DeviceSensorRules
{
    /// <summary>HWiNFO 的共享記憶體（要在 HWiNFO 開啟「Shared Memory Support」；免費版每 12 小時要重新開一次）</summary>
    public const string SourceHwInfo = "hwinfo";

    /// <summary>LibreHardwareMonitor 的 WMI（<c>root\LibreHardwareMonitor</c>）</summary>
    public const string SourceLibreHardwareMonitor = "lhm";

    /// <summary>OpenHardwareMonitor 的 WMI（<c>root\OpenHardwareMonitor</c>）</summary>
    public const string SourceOpenHardwareMonitor = "ohm";

    /// <summary>ACPI 熱區（效能計數器 Thermal Zone Information；主機板回報的，未必是 CPU 核心的溫度，桌機常沒有或是固定值）</summary>
    public const string SourceAcpi = "acpi";

    /// <summary>讀到的溫度要在這個範圍內（攝氏）才採用：0 以下是沒接感測器，125 以上不是真的 CPU 溫度</summary>
    public const double MinReadingCelsius = 1;

    public const double MaxReadingCelsius = 125;

    // ───── GPU ─────

    /// <summary>
    /// 效能計數器「GPU Engine」的實例名稱 → 哪張卡（LUID）的哪個引擎：<c>pid_1234_luid_0x00000000_0x0000C5B2_phys_0_eng_3_engtype_3D</c>。
    /// 認不得回 null
    /// </summary>
    public static (long Luid, int Phys, int Engine)? ParseEngineInstance(string? instance)
    {
        if (Tokens(instance) is not { } tokens || Luid(tokens) is not { } luid || IntAfter(tokens, "phys") is not { } phys || IntAfter(tokens, "eng") is not { } engine)
            return null;
        return (luid, phys, engine);
    }

    /// <summary>效能計數器「GPU Adapter Memory」的實例名稱 → 哪張卡（LUID）：<c>luid_0x00000000_0x0000C5B2_phys_0</c>。認不得回 null</summary>
    public static (long Luid, int Phys)? ParseAdapterInstance(string? instance)
    {
        if (Tokens(instance) is not { } tokens || Luid(tokens) is not { } luid || IntAfter(tokens, "phys") is not { } phys)
            return null;
        return (luid, phys);
    }

    /// <summary>DXGI 的 LUID（高位有號、低位無號）合成一個鍵，和計數器實例名稱解析出來的相同</summary>
    public static long LuidKey(int high, uint low) => (long)(((ulong)(uint)high << 32) | low);

    /// <summary>
    /// 各張卡的使用率（工作管理員的算法）：同一個引擎（LUID、phys、eng）各程序的使用率相加，一張卡取最忙的引擎；夾在 0～100、一位小數。
    /// 實例名稱認不得、值不是有限數的略過
    /// </summary>
    public static Dictionary<long, double> GpuUtilization(IEnumerable<(string Instance, double Value)> samples)
    {
        var engines = new Dictionary<(long, int, int), double>();
        foreach (var (instance, value) in samples)
        {
            if (!double.IsFinite(value) || ParseEngineInstance(instance) is not { } key)
                continue;
            engines[key] = engines.GetValueOrDefault(key) + Math.Max(0, value);
        }
        var result = new Dictionary<long, double>();
        foreach (var ((luid, _, _), sum) in engines)
            result[luid] = Math.Max(result.GetValueOrDefault(luid), Percent(sum));
        return result;
    }

    /// <summary>各張卡的專用記憶體已用（位元組；同一張卡各 phys 相加）。實例名稱認不得、值不是有限的非負數略過</summary>
    public static Dictionary<long, long> GpuDedicatedUsage(IEnumerable<(string Instance, double Value)> samples)
    {
        var result = new Dictionary<long, long>();
        foreach (var (instance, value) in samples)
        {
            if (!double.IsFinite(value) || value < 0 || ParseAdapterInstance(instance) is not { } key)
                continue;
            var bytes = value >= DeviceStatsRules.MaxBytes ? DeviceStatsRules.MaxBytes : (long)value;
            result[key.Luid] = Math.Min(DeviceStatsRules.MaxBytes, result.GetValueOrDefault(key.Luid) + bytes);
        }
        return result;
    }

    /// <summary>
    /// 一張卡（<see cref="DeviceStatsRules.Check"/> 一定合格）：名稱清理後不能用時是「GPU n」（n 從 1 起）；使用率夾在 0～100、一位小數；
    /// 總量是正數而且有已用時才帶記憶體（已用夾到總量）
    /// </summary>
    public static GpuStats Gpu(string? rawName, int index, double util, long? memUsed, long memTotal)
    {
        var name = DeviceStatsSampleRules.DisplayName(rawName, DeviceStatsRules.MaxGpuNameLength) ?? "GPU " + (index + 1).ToString(CultureInfo.InvariantCulture);
        var hasMemory = memUsed is not null && memTotal is > 0 and <= DeviceStatsRules.MaxBytes;
        return new GpuStats(name, double.IsFinite(util) ? Percent(util) : 0,
            hasMemory ? Math.Clamp(memUsed!.Value, 0, memTotal) : null,
            hasMemory ? memTotal : null);
    }

    private static double Percent(double value) => Math.Round(Math.Clamp(value, 0, 100), 1, MidpointRounding.AwayFromZero);

    private static string[]? Tokens(string? instance) => string.IsNullOrEmpty(instance) ? null : instance.Split('_');

    private static long? Luid(string[] tokens)
    {
        var i = Array.IndexOf(tokens, "luid");
        if (i < 0 || i + 2 >= tokens.Length || Hex(tokens[i + 1]) is not { } high || Hex(tokens[i + 2]) is not { } low)
            return null;
        return LuidKey((int)high, low);
    }

    private static uint? Hex(string token) =>
        token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && uint.TryParse(token.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static int? IntAfter(string[] tokens, string name)
    {
        var i = Array.IndexOf(tokens, name);
        return i >= 0 && i + 1 < tokens.Length && int.TryParse(tokens[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    // ───── CPU 溫度 ─────

    /// <summary>讀到的溫度（攝氏）：在 <see cref="MinReadingCelsius"/>～<see cref="MaxReadingCelsius"/> 之間才採用，一位小數；否則 null</summary>
    public static double? Celsius(double value) =>
        double.IsFinite(value) && value >= MinReadingCelsius && value <= MaxReadingCelsius
            ? Math.Round(value, 1, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>
    /// ACPI 熱區（效能計數器 Thermal Zone Information）：<paramref name="tenthsOfKelvin"/> 是 High Precision Temperature（十分之一 K），
    /// 否則是 Temperature（K）。取各熱區中最高的；都不合理時 null
    /// </summary>
    public static double? ThermalZoneCelsius(IEnumerable<double> values, bool tenthsOfKelvin)
    {
        double? best = null;
        foreach (var raw in values)
        {
            if (Celsius((tenthsOfKelvin ? raw / 10 : raw) - 273.15) is { } c && (best is null || c > best))
                best = c;
        }
        return best;
    }

    /// <summary>
    /// LibreHardwareMonitor／OpenHardwareMonitor 的溫度感測器（WMI 的 Sensor，SensorType 是 Temperature）→ CPU 溫度：只看 CPU 底下的
    /// （Parent 是 <c>/amdcpu/…</c>、<c>/intelcpu/…</c>、<c>/cpu/…</c>），依 <see cref="HardwareMonitorPreference"/> 的順序挑第一個有的；
    /// 都沒有時取各核心中最高的（「Distance to TjMax」不是溫度，不算）
    /// </summary>
    public static double? HardwareMonitorCpuTemperature(IEnumerable<(string? Name, string? Parent, double Value)> sensors)
    {
        var cpu = sensors.Where(s => s.Name is not null && IsCpuParent(s.Parent) && Celsius(s.Value) is not null).ToList();
        foreach (var name in HardwareMonitorPreference)
        {
            if (cpu.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } hit)
                return Celsius(hit.Value);
        }
        var rest = cpu.Where(s => !s.Name!.Contains("Distance", StringComparison.OrdinalIgnoreCase)).Select(s => s.Value).ToList();
        return rest.Count == 0 ? null : Celsius(rest.Max());
    }

    /// <summary>LibreHardwareMonitor 的 CPU 溫度感測器名稱，越前面越優先（Intel 的封裝溫度、AMD 的 Tctl/Tdie）</summary>
    public static readonly string[] HardwareMonitorPreference =
        ["CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core (Tctl)", "Package", "Core Max", "Core Average"];

    private static bool IsCpuParent(string? parent) =>
        parent is not null && (parent.StartsWith("/amdcpu", StringComparison.OrdinalIgnoreCase)
                               || parent.StartsWith("/intelcpu", StringComparison.OrdinalIgnoreCase)
                               || parent.StartsWith("/cpu", StringComparison.OrdinalIgnoreCase));

    // ───── HWiNFO 的共享記憶體 ─────

    /// <summary>HWiNFO 建立的共享記憶體名稱（開啟「Shared Memory Support」時才有）</summary>
    public const string HwInfoMapName = @"Global\HWiNFO_SENS_SM2";

    /// <summary>
    /// 標頭（44 位元組，小端序）：<c>signature</c>（"HWiS"＝作用中，"DEAD"＝已停用）、version、revision、poll_time（int64）、
    /// 感測器區的 offset／元素大小／個數、讀值區的 offset／元素大小／個數
    /// </summary>
    public const int HwInfoHeaderSize = 44;

    /// <summary>感測器元素至少這麼大：id（4）、instance（4）、原始名稱（128）、使用者名稱（128）；新版尾端另有 UTF-8 欄位，以標頭的元素大小跳格</summary>
    private const int HwInfoSensorMinSize = 264;

    /// <summary>讀值元素至少這麼大：type（4）、sensorIndex（4）、id（4）、原始標籤（128）、使用者標籤（128）、單位（16）、value／min／max／avg（double×4）</summary>
    private const int HwInfoReadingMinSize = 316;

    private const int HwInfoMaxSensors = 4096;

    private const int HwInfoMaxReadings = 65536;

    /// <summary>讀值的 type：1 是溫度</summary>
    private const uint HwInfoTemperature = 1;

    /// <summary>整份最多讀這麼多（防呆；真的資料約幾百 KB）</summary>
    public const int HwInfoMaxLength = 64 << 20;

    /// <summary>HWiNFO 的 CPU 溫度標籤（讀值的原始標籤），越前面越優先：Intel 的封裝溫度、AMD 的 Tctl/Tdie 與晶粒平均；最後的「CPU」是主機板量的插槽溫度</summary>
    public static readonly string[] HwInfoPreference =
        ["CPU Package", "CPU (Tctl/Tdie)", "CPU Die (average)", "CPU (Tdie)", "CPU (Tctl)", "Core Max", "CPU"];

    /// <summary>
    /// 從標頭算出要讀多長（感測器區與讀值區的結尾）：簽章不是 "HWiS"、元素太小、個數或結尾超出上限回 null
    /// </summary>
    public static int? HwInfoRequiredLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < HwInfoHeaderSize || !header[..4].SequenceEqual("HWiS"u8))
            return null;
        var sensorOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        var sensorSize = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        var sensorCount = BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
        var readingOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
        var readingSize = BinaryPrimitives.ReadUInt32LittleEndian(header[36..]);
        var readingCount = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);
        if (sensorSize < HwInfoSensorMinSize || readingSize < HwInfoReadingMinSize || sensorCount > HwInfoMaxSensors || readingCount > HwInfoMaxReadings
            || sensorOffset < HwInfoHeaderSize || readingOffset < HwInfoHeaderSize)
            return null;
        var end = Math.Max(sensorOffset + (ulong)sensorSize * sensorCount, readingOffset + (ulong)readingSize * readingCount);
        return end <= HwInfoMaxLength ? (int)end : null;
    }

    /// <summary>
    /// HWiNFO 共享記憶體（<see cref="HwInfoRequiredLength"/> 那麼長的開頭）裡的 CPU 溫度：溫度讀值中原始標籤依 <see cref="HwInfoPreference"/> 的順序
    /// 挑第一個有的（同一個標籤有好幾個時，名稱以「CPU」開頭的感測器優先）；單位是華氏時換算。格式不對、沒有合理的讀值回 null
    /// </summary>
    public static double? HwInfoCpuTemperature(ReadOnlySpan<byte> data)
    {
        if (HwInfoRequiredLength(data) is not { } length || data.Length < length)
            return null;
        var sensorOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        var sensorSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);
        var sensorCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
        var readingOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[32..]);
        var readingSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[36..]);
        var readingCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[40..]);

        (int Rank, bool CpuSensor, double Celsius)? best = null;
        for (var i = 0; i < readingCount; i++)
        {
            var reading = data.Slice(readingOffset + i * readingSize, readingSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(reading) != HwInfoTemperature)
                continue;
            var label = AnsiString(reading.Slice(12, 128));
            var rank = Array.FindIndex(HwInfoPreference, p => string.Equals(p, label, StringComparison.OrdinalIgnoreCase));
            if (rank < 0)
                continue;
            var value = BinaryPrimitives.ReadDoubleLittleEndian(reading[284..]);
            if (AnsiString(reading.Slice(268, 16)).EndsWith('F'))
                value = (value - 32) * 5 / 9;
            if (Celsius(value) is not { } celsius)
                continue;
            var sensorIndex = BinaryPrimitives.ReadUInt32LittleEndian(reading[4..]);
            var cpuSensor = sensorIndex < sensorCount
                            && AnsiString(data.Slice(sensorOffset + (int)sensorIndex * sensorSize + 8, 128)).StartsWith("CPU", StringComparison.OrdinalIgnoreCase);
            if (best is null || rank < best.Value.Rank || (rank == best.Value.Rank && cpuSensor && !best.Value.CpuSensor))
                best = (rank, cpuSensor, celsius);
        }
        return best?.Celsius;
    }

    /// <summary>固定長度、以 NUL 結尾的 ANSI 字串（HWiNFO 的原始名稱是 ASCII；° 之類的以 Latin-1 解）</summary>
    private static string AnsiString(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]).Trim();
    }
}
