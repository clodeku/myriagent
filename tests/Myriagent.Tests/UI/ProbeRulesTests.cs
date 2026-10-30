using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;
using Myriagent.UI;

namespace Myriagent.Tests.UI;

public sealed class ProbeRulesTests
{
    [Theory]
    [InlineData(@"C:\probe", "1", true)]
    [InlineData(@"C:\probe", "0", false)]
    [InlineData(@"C:\probe", null, false)]
    [InlineData(@"C:\probe", "true", false)]
    [InlineData(null, "1", false)]
    [InlineData("  ", "1", false)]
    public void 不找CLI_只在探針模式而且明確設成1時(string? probe, string? noCli, bool expected) =>
        ProbeRules.NoCli(probe, noCli).Should().Be(expected);

    [Fact]
    public async Task 不找CLI的選項_找不到任何安裝_執行指令一律失敗_不查最新版()
    {
        var options = ProbeRules.NoCliOptions();

        foreach (var provider in AgentProviders.All)
            options.Locate(provider).Should().BeNull();
        options.LocateNpm().Should().BeNull();
        options.FetchLatestOnStart.Should().BeFalse();
        var run = () => options.Run(new AgentLauncher("claude.exe", []), ["--version"], null, TimeSpan.FromSeconds(1), false, null, CancellationToken.None);
        await run.Should().ThrowAsync<InvalidOperationException>();

        using var http = new HttpClient(new StubHttpHandler());
        using var cli = new CliManager(http, _ => false, () => false, startBackgroundChecks: false, options);
        foreach (var provider in AgentProviders.All)
            cli.GetLauncher(provider).Should().BeNull($"{provider} 一律未安裝");
    }

    [Fact]
    public void 已配對閒置CPU的取樣窗_涵蓋至少兩次心跳()
    {
        // 一進 Live 就開始取樣：第一次心跳在 Live＋15 秒，只取一窗（10 秒）的話窗裡沒有任何週期性的同步工作
        (ProbeRules.PairedIdleCpuWindows * 10).Should().BeGreaterThanOrEqualTo(2 * Myriagent.Sync.SyncProtocol.HeartbeatSeconds);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void 閒置等待不看是否配對_只有已配對而且連上才先等安定(bool paired, bool notOnline, bool settle)
    {
        var wait = ProbeRules.IdleWait(paired, notOnline);

        wait.Settle.Should().Be(settle);
        wait.Delay.Should().Be(TimeSpan.FromSeconds(25), "未配對、連不上的也等同樣久，取樣時間點才可比（WP4.9 D1）");
        ProbeRules.IdleSettleDelay.Should().Be(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public void 安定的連續時間與時限()
    {
        ProbeRules.SettleQuiet.Should().Be(TimeSpan.FromSeconds(1), "同 fake-device 的回補等待");
        ProbeRules.SettleTimeout.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void DescribeGc_位元組換MB四捨五入一位()
    {
        const long Mb = 1048576;
        var gc = ProbeRules.DescribeGc(heapBytes: 25 * Mb + Mb / 16, committedBytes: 35 * Mb + 4 * Mb / 10, fragmentedBytes: Mb / 4, privateBytes: 100 * Mb + Mb / 2);

        gc.HeapMb.Should().Be(25.1, "25.0625 MB");
        gc.CommittedMb.Should().Be(35.4);
        gc.FragmentedMb.Should().Be(0.3, "0.25 MB 四捨五入（不是銀行家捨入的 0.2）");
        gc.NativeEstimateMb.Should().Be(65.1, "私有記憶體減 GC 已提交（100.5 − 35.4）");
    }

    [Fact]
    public void DescribeGc_原生估計不會是負數()
    {
        const long Mb = 1048576;

        ProbeRules.DescribeGc(10 * Mb, 50 * Mb, 0, 40 * Mb).NativeEstimateMb.Should().Be(0, "已提交比私有記憶體大（共用頁面、量的時間點不同）時記 0");
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("0", 0)]
    [InlineData("1800", 1800)]
    [InlineData("86400", 86400)]
    [InlineData("86401", 0)]
    [InlineData("-5", 0)]
    [InlineData(" 30", 0)]
    [InlineData("1.5", 0)]
    [InlineData("abc", 0)]
    public void 第二次閒置取樣之前多等的秒數_只收0到一天的整數(string? value, int expectedSeconds)
    {
        ProbeRules.IdleExtraVariable.Should().Be("MYRIAGENT_AGENT_PROBE_IDLE_EXTRA_SECONDS", "measure_sync 的 IDLE_EXTRA_VARIABLE");
        ProbeRules.IdleExtraDelay(value).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void DescribeIdleLate_配置速率與長滿gen0預算的時間()
    {
        const long Mb = 1048576;
        // 30 分鐘配置 24 MB＝每小時 48 MB；gen0 預算 48 MB：60 分鐘配置滿
        var late = ProbeRules.DescribeIdleLate(TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30), 24 * Mb, 48 * Mb);

        late.ExtraSeconds.Should().Be(1740);
        late.ElapsedSeconds.Should().Be(1800);
        late.AllocatedMb.Should().Be(24);
        late.AllocMbPerHour.Should().Be(48);
        late.Gen0MaxBudgetMb.Should().Be(48);
        late.Gen0BudgetFillMinutes.Should().Be(60);
    }

    [Fact]
    public void DescribeIdleLate_沒有配置或預算不明_推算不出來的是null()
    {
        ProbeRules.DescribeIdleLate(TimeSpan.Zero, TimeSpan.FromSeconds(30), 0, 50_331_648).Should()
            .Be(new ProbeIdleLate(0, 30, 0, 0, 48, null), "沒有配置：速率 0、長不滿");
        ProbeRules.DescribeIdleLate(TimeSpan.Zero, TimeSpan.FromSeconds(30), 1048576, null).Gen0BudgetFillMinutes.Should().BeNull();
        ProbeRules.DescribeIdleLate(TimeSpan.Zero, TimeSpan.Zero, 1048576, 50_331_648).AllocMbPerHour.Should().BeNull("沒有經過時間");
    }

    [Fact]
    public void Gen0MaxBudget_讀GC設定的值_這個程序也讀得到()
    {
        ProbeRules.Gen0MaxBudgetBytes(new Dictionary<string, object> { ["GCGen0MaxBudget"] = 6291456UL }).Should().Be(6291456);
        ProbeRules.Gen0MaxBudgetBytes(new Dictionary<string, object> { ["GCGen0MaxBudget"] = 50331648L }).Should().Be(50331648);
        ProbeRules.Gen0MaxBudgetBytes(new Dictionary<string, object> { ["GCGen0MaxBudget"] = 0UL }).Should().BeNull();
        ProbeRules.Gen0MaxBudgetBytes(new Dictionary<string, object> { ["GCGen0MaxBudget"] = ulong.MaxValue }).Should().BeNull();
        ProbeRules.Gen0MaxBudgetBytes(new Dictionary<string, object>()).Should().BeNull();
        ProbeRules.Gen0MaxBudgetBytes(GC.GetConfigurationVariables()).Should().BePositive("測試主機的 GC 設定裡也有 gen0 預算（探針靠它推算回彈的上限）");
    }
}
