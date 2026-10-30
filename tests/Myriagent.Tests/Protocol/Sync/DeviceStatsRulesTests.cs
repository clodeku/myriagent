using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 各電腦的即時狀態（docs/sync-protocol.md 14.8，stat1）：格式與上限（伺服器收件、裝置收 StatsUpdate 共用）、送出的間隔、
/// CPU 使用率與網路速率的計算、線上 JSON 與網頁的 DeviceStatsView
/// </summary>
public sealed class DeviceStatsRulesTests
{
    private static readonly DeviceStats Ok = new(23.5, 8L << 30, 32L << 30, [new DiskStats("C:", 400L << 30, 1L << 40)], 125_000, 9_000,
        RttMs: 42, Cores: 16, CpuName: "AMD Ryzen 9 7950X 16-Core Processor", UptimeSec: 86_400);

    [Fact]
    public void 合格的狀態_省略的選用欄位也合格()
    {
        DeviceStatsRules.Check(Ok).Should().BeNull();
        DeviceStatsRules.Check(Ok with { RttMs = null, Cores = null, CpuName = null, UptimeSec = null, Disks = [] }).Should().BeNull();
        DeviceStatsRules.Check(Ok with { Cpu = 0 }).Should().BeNull();
        DeviceStatsRules.Check(Ok with { Cpu = 100, MemUsed = Ok.MemTotal, NetRx = 0, NetTx = 0, RttMs = 0 }).Should().BeNull("邊界值");
        DeviceStatsRules.Check(Hardware).Should().BeNull();
        DeviceStatsRules.Check(Hardware with { Gpus = [], CpuTemp = null, CpuTempSource = null }).Should().BeNull();
        DeviceStatsRules.Check(Hardware with { Gpus = [new GpuStats("GPU", 0), new GpuStats("GPU", 100, 0, 1)], CpuTemp = -50 }).Should().BeNull("邊界值、同名也可以");
        DeviceStatsRules.Check(Hardware with { CpuTemp = 150, CpuTempSource = "some-new-tool" }).Should().BeNull("不認得的來源照收（舊伺服器不會擋新版的來源）");
    }

    /// <summary>有顯示卡與 CPU 溫度的狀態（桌面的取樣器才有）</summary>
    private static readonly DeviceStats Hardware = Ok with
    {
        Gpus = [new GpuStats("NVIDIA GeForce RTX 4070", 23.5, 6L << 30, 16L << 30), new GpuStats("AMD Radeon(TM) Graphics", 0)],
        CpuTemp = 67.8,
        CpuTempSource = DeviceSensorRules.SourceHwInfo,
    };

    [Fact]
    public void 顯示卡與CPU溫度_不合格的()
    {
        DeviceStatsRules.Check(Hardware with { Gpus = Enumerable.Range(0, 9).Select(i => new GpuStats("GPU " + i, 1)).ToList() }).Should().NotBeNull("最多 8 張");
        DeviceStatsRules.Check(Hardware with { Gpus = [null!] }).Should().NotBeNull();
        DeviceStatsRules.Check(Hardware with { Gpus = [new GpuStats("", 1)] }).Should().NotBeNull();
        DeviceStatsRules.Check(Hardware with { Gpus = [new GpuStats("RTX\u202e", 1)] }).Should().NotBeNull("名稱不能有格式字元");
        DeviceStatsRules.Check(Hardware with { Gpus = [new GpuStats(new string('x', 81), 1)] }).Should().NotBeNull("81 字");
        DeviceStatsRules.Check(Hardware with { Gpus = [new GpuStats("GPU", -0.1)] }).Should().NotBeNull();
        DeviceStatsRules.Check(Hardware with { Gpus = [new GpuStats("GPU", 100.1)] }).Should().NotBeNull();
        DeviceStatsRules.Check(Hardware with { Gpus = [new GpuStats("GPU", double.NaN)] }).Should().NotBeNull();
        DeviceStatsRules.Check(Hardware with { Gpus = [new GpuStats("GPU", 1, 5, null)] }).Should().NotBeNull("記憶體只有已用");
        DeviceStatsRules.Check(Hardware with { Gpus = [new GpuStats("GPU", 1, null, 5)] }).Should().NotBeNull("記憶體只有總量");
        DeviceStatsRules.Check(Hardware with { Gpus = [new GpuStats("GPU", 1, 6, 5)] }).Should().NotBeNull("已用比總量多");
        DeviceStatsRules.Check(Hardware with { Gpus = [new GpuStats("GPU", 1, 0, 0)] }).Should().NotBeNull("總量要大於 0");
        DeviceStatsRules.Check(Hardware with { Gpus = [new GpuStats("GPU", 1, -1, 5)] }).Should().NotBeNull();
        DeviceStatsRules.Check(Hardware with { CpuTemp = double.NaN }).Should().NotBeNull();
        DeviceStatsRules.Check(Hardware with { CpuTemp = -50.1 }).Should().NotBeNull();
        DeviceStatsRules.Check(Hardware with { CpuTemp = 150.1 }).Should().NotBeNull();
        DeviceStatsRules.Check(Hardware with { CpuTempSource = "" }).Should().NotBeNull();
        DeviceStatsRules.Check(Hardware with { CpuTempSource = new string('a', 17) }).Should().NotBeNull("17 字");
        DeviceStatsRules.Check(Hardware with { CpuTempSource = "hw\ninfo" }).Should().NotBeNull();
    }

    [Fact]
    public void 不合格的狀態()
    {
        DeviceStatsRules.Check(null).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { Cpu = -0.1 }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { Cpu = 100.1 }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { Cpu = double.NaN }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { Cpu = double.PositiveInfinity }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { MemTotal = 0, MemUsed = 0 }).Should().NotBeNull("總量要大於 0");
        DeviceStatsRules.Check(Ok with { MemUsed = Ok.MemTotal + 1 }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { MemUsed = -1 }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { Disks = null! }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { Disks = Enumerable.Range(0, 9).Select(i => new DiskStats(((char)('C' + i)) + ":", 1, 2)).ToList() }).Should().NotBeNull("最多 8 個");
        DeviceStatsRules.Check(Ok with { Disks = [new DiskStats("C:\u202e", 1, 2)] }).Should().NotBeNull("磁碟名稱不能有格式字元");
        DeviceStatsRules.Check(Ok with { Disks = [new DiskStats("", 1, 2)] }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { Disks = [new DiskStats(new string('a', 17), 1, 2)] }).Should().NotBeNull("17 字");
        DeviceStatsRules.Check(Ok with { Disks = [new DiskStats("C:", 3, 2)] }).Should().NotBeNull("已用比總量多");
        DeviceStatsRules.Check(Ok with { Disks = [null!] }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { NetRx = -1 }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { NetTx = DeviceStatsRules.MaxRate + 1 }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { RttMs = -1 }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { RttMs = DeviceStatsRules.MaxRttMs + 1 }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { Cores = 0 }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { CpuName = " Intel" }).Should().NotBeNull("頭尾空白要先清掉");
        DeviceStatsRules.Check(Ok with { CpuName = "Intel\n" }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { CpuName = new string('x', 81) }).Should().NotBeNull();
        DeviceStatsRules.Check(Ok with { UptimeSec = -1 }).Should().NotBeNull();
    }

    [Fact]
    public void StatsUpdate的檢查_台數_deviceId_重複_負數_內容()
    {
        var a = SyncIds.NewDeviceId();
        var b = SyncIds.NewDeviceId();
        DeviceStatsRules.CheckUpdate(new StatsUpdate([])).Should().BeNull();
        DeviceStatsRules.CheckUpdate(new StatsUpdate([new(a, 1, 0, Ok), new(b, 2, 1500, Ok)])).Should().BeNull();

        DeviceStatsRules.CheckUpdate(null).Should().NotBeNull();
        DeviceStatsRules.CheckUpdate(new StatsUpdate(null!)).Should().NotBeNull();
        DeviceStatsRules.CheckUpdate(new StatsUpdate([new(a, 1, 0, Ok), new(a, 2, 0, Ok)])).Should().NotBeNull("重複");
        DeviceStatsRules.CheckUpdate(new StatsUpdate([new("d_x", 1, 0, Ok)])).Should().NotBeNull("deviceId 格式");
        DeviceStatsRules.CheckUpdate(new StatsUpdate([new(a, -1, 0, Ok)])).Should().NotBeNull();
        DeviceStatsRules.CheckUpdate(new StatsUpdate([new(a, 1, -1, Ok)])).Should().NotBeNull();
        DeviceStatsRules.CheckUpdate(new StatsUpdate([new(a, 1, 0, Ok with { Cpu = 101 })])).Should().NotBeNull();
        DeviceStatsRules.CheckUpdate(new StatsUpdate([null!])).Should().NotBeNull();
        var many = Enumerable.Range(0, DeviceStatsRules.MaxDevices + 1).Select(i => new DeviceStatsEntry(SyncIds.NewDeviceId(), i, 0, Ok)).ToList();
        DeviceStatsRules.CheckUpdate(new StatsUpdate(many)).Should().NotBeNull("最多 256 台");
        DeviceStatsRules.CheckUpdate(new StatsUpdate(many.Take(DeviceStatsRules.MaxDevices).ToList())).Should().BeNull();
    }

    [Fact]
    public void 送出的間隔_有人在看2秒_沒人10秒()
    {
        DeviceStatsRules.PublishInterval(true).Should().Be(TimeSpan.FromSeconds(2));
        DeviceStatsRules.PublishInterval(false).Should().Be(TimeSpan.FromSeconds(10));
        DeviceStatsRules.MinPublishGapMs.Should().BeLessThan((long)DeviceStatsRules.WatchedInterval.TotalMilliseconds, "伺服器的最小間隔要比 2 秒短，容許計時誤差");
        DeviceStatsRules.StaleAfterMs.Should().BeGreaterThan(2 * (long)DeviceStatsRules.IdleInterval.TotalMilliseconds, "沒人看時 10 秒一次，至少留兩次的餘裕");
    }

    [Fact]
    public void CPU使用率_忙碌是kernel加user減idle_一位小數()
    {
        var before = new CpuTimes(Idle: 1_000, Kernel: 2_000, User: 500);
        // 經過 kernel 600（含 idle 450）＋user 400 ＝ 1000，忙碌 550 → 55%
        DeviceStatsRules.CpuPercent(before, new CpuTimes(1_450, 2_600, 900)).Should().Be(55.0);
        DeviceStatsRules.CpuPercent(before, new CpuTimes(1_000 + 2, 2_000 + 3, 500)).Should().Be(33.3, "1／3 四捨五入到一位");
        DeviceStatsRules.CpuPercent(before, new CpuTimes(1_100, 2_100, 500)).Should().Be(0, "全部是 idle");
        DeviceStatsRules.CpuPercent(before, new CpuTimes(1_000, 2_100, 600)).Should().Be(100);
        DeviceStatsRules.CpuPercent(before, before).Should().BeNull("沒有經過時間");
        DeviceStatsRules.CpuPercent(before, new CpuTimes(999, 2_100, 600)).Should().BeNull("計數倒退");
        DeviceStatsRules.CpuPercent(before, new CpuTimes(1_200, 2_100, 500)).Should().Be(0, "idle 比 kernel＋user 多（計數不同步）時夾在 0");
    }

    [Fact]
    public void 網路速率_每秒位元組_倒退或沒有經過時間是0()
    {
        DeviceStatsRules.Rate(1_000, 3_000, 2_000).Should().Be(1_000);
        DeviceStatsRules.Rate(0, 1, 3).Should().Be(333);
        DeviceStatsRules.Rate(0, 1_000, 500).Should().Be(2_000);
        DeviceStatsRules.Rate(5_000, 1_000, 1_000).Should().Be(0, "網卡重設");
        DeviceStatsRules.Rate(0, 1_000, 0).Should().Be(0);
        DeviceStatsRules.Rate(0, long.MaxValue, 1).Should().Be(DeviceStatsRules.MaxRate);
    }

    [Fact]
    public void 往返時間_無條件進位_夾在上限()
    {
        DeviceStatsRules.RttMs(TimeSpan.FromMilliseconds(41.2)).Should().Be(42);
        DeviceStatsRules.RttMs(TimeSpan.Zero).Should().Be(0);
        DeviceStatsRules.RttMs(TimeSpan.FromMilliseconds(-3)).Should().Be(0);
        DeviceStatsRules.RttMs(TimeSpan.FromHours(1)).Should().Be(DeviceStatsRules.MaxRttMs);
    }

    [Fact]
    public void 線上JSON_null省略_欄位名稱camelCase()
    {
        var json = SyncJson.Serialize(new StatsPublish(Ok with { RttMs = null, CpuName = null, UptimeSec = null, Cores = null }, Watch: true));
        json.Should().Be("""{"stats":{"cpu":23.5,"memUsed":8589934592,"memTotal":34359738368,"disks":[{"name":"C:","used":429496729600,"total":1099511627776}],"netRx":125000,"netTx":9000},"watch":true}""");
        SyncJson.Deserialize<StatsPublish>(json)!.Stats.Disks.Should().ContainSingle();
        SyncJson.Serialize(new StatsPublishResult(true)).Should().Be("""{"watched":true}""");
        var id = SyncIds.NewDeviceId();
        var update = SyncJson.Serialize(new StatsUpdate([new DeviceStatsEntry(id, 7, 1200, Ok)]));
        update.Should().StartWith("{\"devices\":[{\"deviceId\":\"" + id + "\",\"seq\":7,\"ageMs\":1200,\"stats\":{\"cpu\":23.5,");
        SyncJson.Deserialize<StatsUpdate>(update)!.Devices.Single().Stats.Should().BeEquivalentTo(Ok);

        // 顯示卡與溫度：沒有記憶體的那張省略 memUsed／memTotal；舊版的 JSON（沒有這些欄位）讀成 null
        var hardware = SyncJson.Serialize(new StatsPublish(Hardware with { RttMs = null, CpuName = null, UptimeSec = null, Cores = null }, Watch: false));
        hardware.Should().Contain(""","gpus":[{"name":"NVIDIA GeForce RTX 4070","util":23.5,"memUsed":6442450944,"memTotal":17179869184},{"name":"AMD Radeon(TM) Graphics","util":0.0}],"cpuTemp":67.8,"cpuTempSource":"hwinfo"}""");
        SyncJson.Deserialize<StatsPublish>(hardware)!.Stats.Should().BeEquivalentTo(Hardware with { RttMs = null, CpuName = null, UptimeSec = null, Cores = null });
        SyncJson.Deserialize<StatsPublish>(json)!.Stats.Should().Match<DeviceStats>(s => s.Gpus == null && s.CpuTemp == null && s.CpuTempSource == null);
    }

    [Fact]
    public void 網頁的DeviceStatsView_null照樣帶出()
    {
        var view = DeviceStatsRules.WebView(null, self: true, "OFFICE-PC", 3, 250, Ok with { RttMs = null });
        view.Properties().Select(p => p.Name).Should().Equal("id", "self", "name", "seq", "ageMs", "cpu", "cores", "cpuName", "memUsed", "memTotal", "disks",
            "netRx", "netTx", "rttMs", "uptimeSec", "gpus", "cpuTemp", "cpuTempSource");
        view["id"]!.Type.Should().Be(JTokenType.Null, "未配對的這台");
        view["rttMs"]!.Type.Should().Be(JTokenType.Null);
        ((JArray)view["gpus"]!).Should().BeEmpty("沒有顯示卡時是空陣列");
        view["cpuTemp"]!.Type.Should().Be(JTokenType.Null);
        view["cpuTempSource"]!.Type.Should().Be(JTokenType.Null);

        var hardware = DeviceStatsRules.WebView("d_x", self: false, null, 1, 0, Hardware);
        var gpus = (JArray)hardware["gpus"]!;
        gpus.Should().HaveCount(2);
        ((JObject)gpus[0]).Properties().Select(p => p.Name).Should().Equal("name", "util", "memUsed", "memTotal");
        gpus[0].Value<string>("name").Should().Be("NVIDIA GeForce RTX 4070");
        gpus[0].Value<long>("memTotal").Should().Be(16L << 30);
        gpus[1]["memUsed"]!.Type.Should().Be(JTokenType.Null);
        hardware.Value<double>("cpuTemp").Should().Be(67.8);
        hardware.Value<string>("cpuTempSource").Should().Be("hwinfo");
        DeviceStatsRules.HubWebView(new HubStatsEntry("Hub 主機", 1, 0, Ok, 2, 1), 0).ContainsKey("gpus").Should().BeTrue("主機卡片同一組欄位（伺服器不取樣顯示卡，是空的）");
        view.Value<bool>("self").Should().BeTrue();
        view.Value<double>("cpu").Should().Be(23.5);
        ((JArray)view["disks"]!).Single().Value<string>("name").Should().Be("C:");
        DeviceStatsRules.WebView("d_x", self: false, null, 1, -5, Ok).Value<long>("ageMs").Should().Be(0, "時間不會是負的");
    }
}
