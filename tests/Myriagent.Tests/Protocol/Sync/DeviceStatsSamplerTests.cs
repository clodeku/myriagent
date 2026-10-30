using System.Net.NetworkInformation;
using Myriagent.Sync;
using Myriagent.SystemStatus;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 電腦狀態的取樣（sync-protocol 14.8；桌面與伺服器共用 <see cref="DeviceStatsSampler"/>）：哪些網卡算流量、磁碟與 CPU 名稱的整理、
/// CPU 以兩次累計值的差算、記憶體夾在範圍內，這台真的取樣出來的值通過伺服器的檢查
/// </summary>
public sealed class DeviceStatsSamplerTests
{
    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, OperationalStatus.Up, "Intel(R) Ethernet Connection I219-V", true)]
    [InlineData(NetworkInterfaceType.Wireless80211, OperationalStatus.Up, "Intel(R) Wi-Fi 6 AX201 160MHz", true)]
    [InlineData(NetworkInterfaceType.Wwanpp, OperationalStatus.Up, "行動寬頻", true)]
    [InlineData(NetworkInterfaceType.Ethernet, OperationalStatus.Down, "Intel(R) Ethernet", false)]
    [InlineData(NetworkInterfaceType.Loopback, OperationalStatus.Up, "Software Loopback Interface 1", false)]
    [InlineData(NetworkInterfaceType.Tunnel, OperationalStatus.Up, "Teredo Tunneling Pseudo-Interface", false)]
    [InlineData(NetworkInterfaceType.Ethernet, OperationalStatus.Up, "Hyper-V Virtual Ethernet Adapter", false)]
    [InlineData(NetworkInterfaceType.Ethernet, OperationalStatus.Up, "VMware Virtual Ethernet Adapter for VMnet8", false)]
    [InlineData(NetworkInterfaceType.Ethernet, OperationalStatus.Up, "TAP-Windows Adapter V9", false)]
    [InlineData(NetworkInterfaceType.Unknown, OperationalStatus.Up, "WireGuard Tunnel", false)]
    [InlineData(NetworkInterfaceType.Ethernet, OperationalStatus.Up, "Bluetooth Device (Personal Area Network)", false)]
    [InlineData(NetworkInterfaceType.Ethernet, OperationalStatus.Up, null, true)]
    public void 算進流量的網卡_實體乙太與無線_虛擬的不算(NetworkInterfaceType type, OperationalStatus status, string? description, bool counts) =>
        DeviceStatsSampleRules.CountsTraffic(type, status, description).Should().Be(counts);

    [Fact]
    public void 磁碟名稱與CPU名稱的整理()
    {
        DeviceStatsSampleRules.DiskName(@"C:\").Should().Be("C:");
        DeviceStatsSampleRules.DiskName("D:/").Should().Be("D:");
        DeviceStatsSampleRules.DiskName(@"\").Should().BeNull();
        DeviceStatsSampleRules.DiskName("C:\u200b").Should().BeNull("有格式字元的不送（伺服器會擋）");
        DeviceStatsSampleRules.DiskName(null).Should().BeNull();

        DeviceStatsSampleRules.CpuName("  Intel(R) Core(TM)  i7-12700K   ").Should().Be("Intel(R) Core(TM) i7-12700K", "登錄的值常有連續與尾端空白");
        DeviceStatsSampleRules.CpuName("   ").Should().BeNull();
        DeviceStatsSampleRules.CpuName(null).Should().BeNull();
        DeviceStatsSampleRules.CpuName(new string('x', 120)).Should().HaveLength(DeviceStatsRules.MaxCpuNameLength);
    }

    [Fact]
    public void 取樣器_CPU是兩次累計值的差_記憶體夾在範圍內_讀不到給保守值()
    {
        var cpu = new Queue<CpuTimes?>([new CpuTimes(1_000, 2_000, 1_000), new CpuTimes(1_250, 2_500, 1_500), null]);
        (long, long)? memory = (20, 10);
        var sampler = new DeviceStatsSampler(() => cpu.Dequeue(), () => memory, "  Test  CPU ");

        var first = sampler.Sample();
        first.Cpu.Should().Be(75, "kernel 500（含 idle 250）＋user 500，忙碌 750／1000");
        first.MemUsed.Should().Be(10, "已用夾在總量以內");
        first.MemTotal.Should().Be(10);
        first.CpuName.Should().Be("Test CPU");
        first.Cores.Should().Be(Environment.ProcessorCount);
        DeviceStatsRules.Check(first).Should().BeNull();

        memory = null;
        var second = sampler.Sample();
        second.Cpu.Should().Be(0, "這次讀不到 CPU");
        (second.MemUsed, second.MemTotal).Should().Be((0, 1), "讀不到記憶體");
        DeviceStatsRules.Check(second).Should().BeNull("讀不到的也要能送");
    }

    [Fact]
    public void 取樣器_顯示卡與溫度_不合格的略過_讀取器丟例外只記一次警告()
    {
        var warnings = new List<string>();
        IReadOnlyList<GpuStats>? gpus = [new GpuStats("RTX", 23.5, 1, 2), new GpuStats("壞的", 101), new GpuStats("iGPU", 0)];
        (double, string)? temp = (67.84, DeviceSensorRules.SourceHwInfo);
        var throwGpu = false;
        var throwTemp = false;
        var sampler = new DeviceStatsSampler(() => null, () => (1, 2), null, warnings.Add,
            () => throwGpu ? throw new InvalidOperationException("gpu 壞了") : gpus,
            () => throwTemp ? throw new InvalidOperationException("溫度壞了") : temp);

        var first = sampler.Sample();
        first.Gpus.Should().Equal(new GpuStats("RTX", 23.5, 1, 2), new GpuStats("iGPU", 0));
        first.CpuTemp.Should().Be(67.8, "一位小數");
        first.CpuTempSource.Should().Be("hwinfo");
        DeviceStatsRules.Check(first).Should().BeNull();

        gpus = Enumerable.Range(0, 12).Select(i => new GpuStats("GPU " + i, i)).ToList();
        temp = (200, "hwinfo");
        var second = sampler.Sample();
        second.Gpus.Should().HaveCount(DeviceStatsRules.MaxGpus, "最多 8 張");
        (second.CpuTemp, second.CpuTempSource).Should().Be((null, null), "不合理的溫度兩個都不送");
        DeviceStatsRules.Check(second).Should().BeNull();

        gpus = [];
        temp = (50, "bad\nsource");
        var third = sampler.Sample();
        third.Gpus.Should().BeNull("一張都沒有");
        third.CpuTemp.Should().BeNull("來源不乾淨");

        throwGpu = throwTemp = true;
        sampler.Sample().Should().Match<DeviceStats>(s => s.Gpus == null && s.CpuTemp == null);
        sampler.Sample();
        warnings.Should().HaveCount(2, "各記一次，不每秒洗記錄");
        warnings.Should().Contain(w => w.Contains("gpu 壞了")).And.Contain(w => w.Contains("溫度壞了"));

        new DeviceStatsSampler(() => null, () => (1, 2), null).Sample().Should().Match<DeviceStats>(s => s.Gpus == null && s.CpuTemp == null, "伺服器主機不給讀取器");
    }

    [Fact]
    public void 同一張網卡只算一次_篩選器和本體共用實體位址_沒有位址的各自算()
    {
        var nics = new[]
        {
            ("Realtek PCIe 5GbE Family Controller", "C87F54000001"),
            ("Realtek PCIe 5GbE Family Controller-WFP Native MAC Layer LightWeight Filter-0000", "C87F54000001"),
            ("Realtek PCIe 5GbE Family Controller-QoS Packet Scheduler-0000", "c87f54000001"),
            ("Intel(R) Wi-Fi 6 AX201 160MHz", "A0B1C2000002"),
            ("行動寬頻", (string?)null),
            ("撥接", ""),
        };
        DeviceStatsSampleRules.OncePerAddress(nics, n => n.Item2).Select(n => n.Item1).Should().Equal(
            "Realtek PCIe 5GbE Family Controller", "Intel(R) Wi-Fi 6 AX201 160MHz", "行動寬頻", "撥接");
    }

    [Fact]
    public void 取樣器_網路用呼叫端給的讀取函式算速率_丟例外只記一次警告()
    {
        var warnings = new List<string>();
        var totals = new Queue<(long, long)?>([(1_000, 2_000), (1_000 + 500, 2_000 + 250), null]);
        var throwNet = false;
        var sampler = new DeviceStatsSampler(() => null, () => (1, 2), null, warnings.Add,
            readNetwork: () => throwNet ? throw new InvalidOperationException("網卡壞了") : totals.Dequeue());

        var first = sampler.Sample();
        first.NetRx.Should().BeGreaterThan(0, "兩次累計值的差");
        first.NetTx.Should().BeGreaterThan(0);
        first.NetRx.Should().BeGreaterThan(first.NetTx, "收 500、送 250");

        sampler.Sample().Should().Match<DeviceStats>(s => s.NetRx == 0 && s.NetTx == 0, "這次讀不到");

        throwNet = true;
        sampler.Sample();
        sampler.Sample();
        warnings.Should().ContainSingle(w => w.Contains("網卡壞了"), "只記一次，不每秒洗記錄");
    }

    [Fact]
    public async Task 這台真的取樣_通過伺服器的檢查()
    {
        var sampler = NativeStats.CreateSampler();
        await Task.Delay(200);
        var stats = sampler.Sample();
        DeviceStatsRules.Check(stats).Should().BeNull();
        stats.MemTotal.Should().BeGreaterThan(1L << 28, "實體記憶體讀得到");
        stats.Disks.Should().NotBeEmpty("至少有系統磁碟");
        stats.Cpu.Should().BeInRange(0, 100);
        stats.Gpus.Should().BeNull("顯示卡第一次取樣只收基準");

        await Task.Delay(300);
        DeviceStatsRules.Check(sampler.Sample()).Should().BeNull("第二次起帶顯示卡（這台有 GPU 計數器時）也要合格");
    }
}
