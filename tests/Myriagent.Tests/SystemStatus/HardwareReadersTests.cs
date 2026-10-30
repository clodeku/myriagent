using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Myriagent.Sync;
using Myriagent.SystemStatus;
using Myriagent.Tests.Protocol.Sync;
using Xunit.Abstractions;

namespace Myriagent.Tests.SystemStatus;

/// <summary>
/// 這台的顯示卡與 CPU 溫度讀取器（電腦狀態的 gpus、cpuTemp）：HWiNFO 的共享記憶體經真的檔案對應讀得到、來源的優先順序與重試間隔、
/// 這台真的 GPU 計數器與溫度來源不丟例外而且通過伺服器的檢查（測試主機沒有 GPU 計數器、沒有溫度來源時是 null）
/// </summary>
public sealed class HardwareReadersTests(ITestOutputHelper output)
{
    [Fact]
    public void HWiNFO_經檔案對應讀得到_沒有這個對應回null()
    {
        var name = @"Local\myriagent-test-hwinfo-" + Guid.NewGuid().ToString("N");
        var data = HwInfoBuffer.Build(["CPU [#0]: AMD Ryzen 7 7700X: Enhanced"], [(1, 0, "CPU (Tctl/Tdie)", "°C", 71.26)]);
        using (var map = MemoryMappedFile.CreateNew(name, data.Length))
        {
            using (var view = map.CreateViewAccessor())
                view.WriteArray(0, data, 0, data.Length);
            CpuTemperatureReader.ReadHwInfo(name).Should().Be(71.3);
        }
        CpuTemperatureReader.ReadHwInfo(name).Should().BeNull("HWiNFO 關掉（對應不在了）");
        CpuTemperatureReader.ReadHwInfo(@"Local\myriagent-test-hwinfo-missing").Should().BeNull();

        var dead = (byte[])data.Clone();
        "DEAD"u8.CopyTo(dead);
        using var deadMap = MemoryMappedFile.CreateNew(name, dead.Length);
        using (var view = deadMap.CreateViewAccessor())
            view.WriteArray(0, dead, 0, dead.Length);
        CpuTemperatureReader.ReadHwInfo(name).Should().BeNull("已停用的共享記憶體");
    }

    [Fact]
    public void 溫度來源_依序取第一個讀得到的_讀不到的隔一段時間才再試_恢復就換回去()
    {
        double? hwinfo = null;
        double? acpi = 45;
        var calls = new List<string>();
        var reader = new CpuTemperatureReader(
            new CpuTemperatureReader.Source("hwinfo", () => { calls.Add("hwinfo"); return hwinfo; }, TimeSpan.FromMilliseconds(300)),
            new CpuTemperatureReader.Source("broken", () => { calls.Add("broken"); throw new InvalidOperationException("壞了"); }, TimeSpan.FromHours(1)),
            new CpuTemperatureReader.Source("acpi", () => { calls.Add("acpi"); return acpi; }, TimeSpan.FromHours(1)));

        reader.Read().Should().Be((45.0, "acpi"));
        calls.Should().Equal("hwinfo", "broken", "acpi");

        calls.Clear();
        hwinfo = 70;
        reader.Read().Should().Be((45.0, "acpi"), "HWiNFO 還在重試間隔內");
        calls.Should().Equal("acpi");

        Thread.Sleep(350);
        calls.Clear();
        reader.Read().Should().Be((70.0, "hwinfo"), "過了重試間隔，優先的來源恢復就換回去");
        calls.Should().Equal("hwinfo");

        acpi = null;
        hwinfo = null;
        calls.Clear();
        reader.Read().Should().BeNull();
        calls.Should().Equal(new[] { "hwinfo", "acpi" }, "壞掉的來源一小時內不再試");
    }

    [Fact]
    public void 這台真的顯示卡_不丟例外_第一次只收基準_之後合格()
    {
        using var reader = new GpuReader();
        var watch = Stopwatch.StartNew();
        reader.Read().Should().BeNull("第一次只收基準");
        output.WriteLine($"第一次（開查詢、列舉顯示卡）：{watch.Elapsed.TotalMilliseconds:F1} ms");
        Thread.Sleep(500);
        IReadOnlyList<GpuStats>? gpus = null;
        var times = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            watch.Restart();
            gpus = reader.Read();
            times.Add(watch.Elapsed.TotalMilliseconds);
            Thread.Sleep(200);
        }
        output.WriteLine($"之後每次：{string.Join("、", times.Select(t => t.ToString("F1")))} ms");
        foreach (var gpu in gpus ?? [])
        {
            output.WriteLine($"{gpu.Name}：{gpu.Util}%，{gpu.MemUsed}/{gpu.MemTotal}");
            DeviceStatsRules.CheckGpu(gpu).Should().BeNull();
        }
    }

    [Fact]
    public void 這台真的CPU溫度_不丟例外_讀得到時是合理的值()
    {
        using var reader = new CpuTemperatureReader();
        var watch = Stopwatch.StartNew();
        var first = reader.Read();
        output.WriteLine($"第一次（每個來源都試）：{watch.Elapsed.TotalMilliseconds:F1} ms，{first?.ToString() ?? "讀不到"}");
        watch.Restart();
        var second = reader.Read();
        output.WriteLine($"第二次：{watch.Elapsed.TotalMilliseconds:F1} ms，{second?.ToString() ?? "讀不到"}");
        if (second is { } t)
        {
            t.Celsius.Should().BeInRange(DeviceSensorRules.MinReadingCelsius, DeviceSensorRules.MaxReadingCelsius);
            t.Source.Should().BeOneOf(DeviceSensorRules.SourceHwInfo, DeviceSensorRules.SourceLibreHardwareMonitor,
                DeviceSensorRules.SourceOpenHardwareMonitor, DeviceSensorRules.SourceAcpi);
        }
    }
}
