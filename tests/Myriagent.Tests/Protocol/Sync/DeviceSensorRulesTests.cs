using System.Buffers.Binary;
using System.Text;
using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 電腦狀態的 GPU 與 CPU 溫度（sync-protocol 14.8 的 gpus、cpuTemp）：效能計數器實例名稱的解析與彙總（工作管理員的算法）、
/// HWiNFO 共享記憶體的解析、LibreHardwareMonitor 感測器的挑選、ACPI 熱區的換算
/// </summary>
public sealed class DeviceSensorRulesTests
{
    private const string Nvidia = "luid_0x00000000_0x00014375";
    private const string Radeon = "luid_0x00000000_0x0000D2A1";
    private static readonly long NvidiaLuid = DeviceSensorRules.LuidKey(0, 0x14375);
    private static readonly long RadeonLuid = DeviceSensorRules.LuidKey(0, 0xD2A1);

    [Fact]
    public void 計數器實例名稱_引擎與卡的LUID()
    {
        DeviceSensorRules.ParseEngineInstance($"pid_35828_{Nvidia}_phys_0_eng_1_engtype_VideoDecode").Should().Be((NvidiaLuid, 0, 1));
        DeviceSensorRules.ParseEngineInstance($"pid_4_{Nvidia}_phys_1_eng_10_engtype_Compute_0").Should().Be((NvidiaLuid, 1, 10), "引擎類型裡也可能有底線");
        DeviceSensorRules.ParseEngineInstance("pid_4_luid_0xFFFFFFFF_0x00000001_phys_0_eng_0_engtype_3D")!.Value.Luid
            .Should().Be(DeviceSensorRules.LuidKey(-1, 1), "高位是有號的 LONG，和 DXGI 的 LUID 合成同一個鍵");
        DeviceSensorRules.ParseEngineInstance("pid_4_luid_0x0_phys_0_eng_0").Should().BeNull("LUID 少一段");
        DeviceSensorRules.ParseEngineInstance($"pid_4_{Nvidia}_phys_0").Should().BeNull("沒有 eng");
        DeviceSensorRules.ParseEngineInstance($"pid_4_{Nvidia}_phys_x_eng_0").Should().BeNull();
        DeviceSensorRules.ParseEngineInstance("pid_4_luid_00000000_0x1_phys_0_eng_0").Should().BeNull("沒有 0x");
        DeviceSensorRules.ParseEngineInstance("").Should().BeNull();
        DeviceSensorRules.ParseEngineInstance(null).Should().BeNull();

        DeviceSensorRules.ParseAdapterInstance($"{Nvidia}_phys_0").Should().Be((NvidiaLuid, 0));
        DeviceSensorRules.ParseAdapterInstance("_Total").Should().BeNull();
    }

    [Fact]
    public void GPU使用率_同一個引擎各程序相加_一張卡取最忙的引擎_夾在100以內()
    {
        var util = DeviceSensorRules.GpuUtilization(
        [
            ($"pid_1_{Nvidia}_phys_0_eng_0_engtype_3D", 12.34),
            ($"pid_2_{Nvidia}_phys_0_eng_0_engtype_3D", 20),
            ($"pid_3_{Nvidia}_phys_0_eng_1_engtype_VideoDecode", 30),
            ($"pid_3_{Nvidia}_phys_0_eng_10_engtype_Copy", 3.5),
            ($"pid_1_{Radeon}_phys_0_eng_0_engtype_3D", 70),
            ($"pid_2_{Radeon}_phys_0_eng_0_engtype_3D", 60),
            ($"pid_2_{Radeon}_phys_0_eng_2_engtype_Copy", -5),
            ("_Total", 99),
            ($"pid_9_{Nvidia}_phys_0_eng_2_engtype_3D", double.NaN),
        ]);
        util.Should().HaveCount(2);
        util[NvidiaLuid].Should().Be(32.3, "3D 引擎的兩個程序相加（32.34）比視訊解碼的 30 忙，一位小數");
        util[RadeonLuid].Should().Be(100, "相加超過 100 時夾住");

        DeviceSensorRules.GpuUtilization([]).Should().BeEmpty();
    }

    [Fact]
    public void GPU專用記憶體_同一張卡各phys相加()
    {
        var used = DeviceSensorRules.GpuDedicatedUsage(
        [
            ($"{Nvidia}_phys_0", 6.5 * (1L << 30)),
            ($"{Nvidia}_phys_1", 1L << 30),
            ($"{Radeon}_phys_0", 300_000_000),
            ($"{Radeon}_phys_1", -1),
            ("bogus", 5),
        ]);
        used[NvidiaLuid].Should().Be((long)(7.5 * (1L << 30)));
        used[RadeonLuid].Should().Be(300_000_000);
    }

    [Fact]
    public void 一張卡_名稱清理_使用率夾住_記憶體要總量與已用都有()
    {
        DeviceSensorRules.Gpu("  NVIDIA  GeForce RTX 4070 ", 0, 23.45, 6L << 30, 16L << 30)
            .Should().Be(new GpuStats("NVIDIA GeForce RTX 4070", 23.5, 6L << 30, 16L << 30));
        DeviceSensorRules.Gpu(null, 1, 120, null, 16L << 30).Should().Be(new GpuStats("GPU 2", 100), "沒有名稱、沒有已用");
        DeviceSensorRules.Gpu("AMD Radeon(TM) Graphics", 0, double.NaN, 5, 0).Should().Be(new GpuStats("AMD Radeon(TM) Graphics", 0), "沒有總量不帶記憶體");
        DeviceSensorRules.Gpu("x", 0, 1, 20L << 30, 16L << 30).MemUsed.Should().Be(16L << 30, "已用夾到總量");
        DeviceSensorRules.Gpu("Bad\u202eName", 0, 1, null, 0).Name.Should().Be("BadName", "格式字元清掉（同 CPU 名稱）");
        DeviceSensorRules.Gpu(" \t ", 2, 1, null, 0).Name.Should().Be("GPU 3", "清完是空的");
        DeviceStatsRules.CheckGpu(DeviceSensorRules.Gpu(new string('x', 200), 0, 50, 1, 2)).Should().BeNull("規則產生的一定合格");
    }

    [Fact]
    public void ACPI熱區_十分之一K或K_取最高的_不合理的略過()
    {
        DeviceSensorRules.ThermalZoneCelsius([3181.5, 3231.5], tenthsOfKelvin: true).Should().Be(50);
        DeviceSensorRules.ThermalZoneCelsius([300.15, 0], tenthsOfKelvin: false).Should().Be(27);
        DeviceSensorRules.ThermalZoneCelsius([0, 2731.5], tenthsOfKelvin: true).Should().BeNull("0 K、0 °C 都不是讀值");
        DeviceSensorRules.ThermalZoneCelsius([], tenthsOfKelvin: true).Should().BeNull();
    }

    [Fact]
    public void LibreHardwareMonitor_只看CPU底下的_依優先順序_沒有時取核心最高的()
    {
        DeviceSensorRules.HardwareMonitorCpuTemperature(
        [
            ("Core #1", "/intelcpu/0", 70),
            ("CPU Package", "/intelcpu/0", 65.25),
            ("Core Max", "/intelcpu/0", 72),
            ("GPU Core", "/gpu-nvidia/0", 80),
        ]).Should().Be(65.3, "Intel 取封裝溫度");
        DeviceSensorRules.HardwareMonitorCpuTemperature(
        [
            ("CCD1 (Tdie)", "/amdcpu/0", 61),
            ("Core (Tctl/Tdie)", "/amdcpu/0", 63),
        ]).Should().Be(63, "AMD 取 Tctl/Tdie");
        DeviceSensorRules.HardwareMonitorCpuTemperature(
        [
            ("Core #1 Distance to TjMax", "/intelcpu/0", 90),
            ("Core #1", "/intelcpu/0", 40),
            ("Core #2", "/intelcpu/0", 44),
        ]).Should().Be(44, "沒有優先的名稱時取核心最高的，Distance to TjMax 不是溫度");
        DeviceSensorRules.HardwareMonitorCpuTemperature([("CPU", "/lpc/nct6798d", 45), ("CPU Package", "/intelcpu/0", 0)])
            .Should().BeNull("主機板的不算、0 °C 不是讀值");
        DeviceSensorRules.HardwareMonitorCpuTemperature([]).Should().BeNull();
    }

    [Fact]
    public void HWiNFO共享記憶體_依標籤優先順序_CPU感測器優先_華氏換算_格式不對回null()
    {
        var amd = HwInfoBuffer.Build(
            [("Example Motherboard (Nuvoton NCT6799D)"), ("CPU [#0]: AMD Ryzen 7 7700X: Enhanced"), ("GPU [#0]: NVIDIA GeForce RTX 4070")],
            [
                (1, 0, "CPU", "°C", 48),
                (1, 1, "CPU CCD1 (Tdie)", "°C", 66),
                (1, 1, "CPU (Tctl/Tdie)", "°C", 67.84),
                (1, 2, "GPU Temperature", "°C", 55),
                (7, 1, "CPU (Tctl/Tdie)", "%", 99),
            ]);
        DeviceSensorRules.HwInfoRequiredLength(amd).Should().Be(amd.Length);
        DeviceSensorRules.HwInfoCpuTemperature(amd).Should().Be(67.8, "AMD 取 Tctl/Tdie（不是主機板量的 CPU，也不是使用率）");

        var intel = HwInfoBuffer.Build(
            [("CPU [#0]: Intel Core i9-13900K"), ("CPU [#0]: Intel Core i9-13900K: DTS")],
            [(1, 1, "Core Max", "°C", 80), (1, 1, "CPU Package", "°F", 158)]);
        DeviceSensorRules.HwInfoCpuTemperature(intel).Should().Be(70, "封裝溫度優先，華氏換算");

        var motherboardOnly = HwInfoBuffer.Build([("GIGABYTE B650 (ITE IT8689E)"), ("CPU [#0]: X")], [(1, 0, "CPU", "°C", 41), (1, 1, "CPU", "°C", 43)]);
        DeviceSensorRules.HwInfoCpuTemperature(motherboardOnly).Should().Be(43, "同一個標籤時名稱以 CPU 開頭的感測器優先");

        DeviceSensorRules.HwInfoCpuTemperature(HwInfoBuffer.Build([("CPU [#0]: X")], [(1, 0, "Motherboard", "°C", 30)])).Should().BeNull("沒有 CPU 的標籤");
        DeviceSensorRules.HwInfoCpuTemperature(HwInfoBuffer.Build([("CPU [#0]: X")], [(1, 0, "CPU Package", "°C", 0)])).Should().BeNull("0 °C 不是讀值");

        var dead = (byte[])amd.Clone();
        "DEAD"u8.CopyTo(dead);
        DeviceSensorRules.HwInfoCpuTemperature(dead).Should().BeNull("已停用的共享記憶體");
        DeviceSensorRules.HwInfoRequiredLength(dead).Should().BeNull();
        DeviceSensorRules.HwInfoCpuTemperature(amd.AsSpan(0, amd.Length - 1)).Should().BeNull("比標頭說的短");
        DeviceSensorRules.HwInfoCpuTemperature(amd.AsSpan(0, 20)).Should().BeNull("連標頭都不完整");

        var hugeCount = (byte[])amd.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(hugeCount.AsSpan(40), 1_000_000);
        DeviceSensorRules.HwInfoRequiredLength(hugeCount).Should().BeNull("讀值個數超出上限");
        var smallElement = (byte[])amd.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(smallElement.AsSpan(36), 100);
        DeviceSensorRules.HwInfoRequiredLength(smallElement).Should().BeNull("讀值元素比格式小");

        var badSensorIndex = HwInfoBuffer.Build([("Board")], [(1, 7, "CPU Package", "°C", 51)]);
        DeviceSensorRules.HwInfoCpuTemperature(badSensorIndex).Should().Be(51, "感測器索引超出範圍時照樣採用讀值，只是不算 CPU 感測器");
    }
}

/// <summary>
/// 照 HWiNFO SDK 的格式組一份共享記憶體（標頭 44 位元組、感測器區、讀值區；元素比最小的格式大，模擬新版尾端多出來的欄位）
/// </summary>
internal static class HwInfoBuffer
{
    public const int SensorSize = 264 + 128;
    public const int ReadingSize = 316 + 144;

    public static byte[] Build(IReadOnlyList<string> sensors, IReadOnlyList<(uint Type, uint Sensor, string Label, string Unit, double Value)> readings)
    {
        var sensorOffset = DeviceSensorRules.HwInfoHeaderSize;
        var readingOffset = sensorOffset + sensors.Count * SensorSize;
        var data = new byte[readingOffset + readings.Count * ReadingSize];
        "HWiS"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(12), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), (uint)sensorOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), SensorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), (uint)sensors.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), (uint)readingOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), ReadingSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), (uint)readings.Count);
        for (var i = 0; i < sensors.Count; i++)
        {
            var at = sensorOffset + i * SensorSize;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), 0xF0000000u + (uint)i);
            Ansi(sensors[i]).CopyTo(data.AsSpan(at + 8, 127));
            Ansi(sensors[i]).CopyTo(data.AsSpan(at + 136, 127));
        }
        for (var i = 0; i < readings.Count; i++)
        {
            var at = readingOffset + i * ReadingSize;
            var r = readings[i];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), r.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 4), r.Sensor);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 8), (uint)i);
            Ansi(r.Label).CopyTo(data.AsSpan(at + 12, 127));
            Ansi("使用者改的標籤不看").CopyTo(data.AsSpan(at + 140, 127));
            Ansi(r.Unit).CopyTo(data.AsSpan(at + 268, 15));
            BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(at + 284), r.Value);
            BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(at + 292), r.Value - 10);
            BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(at + 300), r.Value + 10);
            BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(at + 308), r.Value);
        }
        return data;
    }

    private static byte[] Ansi(string text) => Encoding.Latin1.GetBytes(text);
}
