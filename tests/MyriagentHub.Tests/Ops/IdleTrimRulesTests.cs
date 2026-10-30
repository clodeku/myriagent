using Myriagent.Sync;
using MyriagentHub.Ops;

namespace MyriagentHub.Tests.Ops;

/// <summary>閒置修剪的純函式規則（S9 的閒置記憶體，設計 14.20）：什麼時候回收、什麼算活動</summary>
public sealed class IdleTrimRulesTests
{
    private static readonly TimeSpan Quiet = IdleTrimRules.QuietPeriod;

    private static readonly TimeSpan Hour = IdleTrimRules.RecompactInterval;

    private const long Growth = IdleTrimRules.RecompactGrowthBytes;

    [Fact]
    public void 靜置不夠久或有請求在處理_一律不做()
    {
        foreach (var compiled in new[] { true, false })
        {
            IdleTrimRules.Decide(Quiet - TimeSpan.FromMilliseconds(1), 0, false, null, 0, compiled).Should().Be(IdleTrimAction.None, "還不到靜置期");
            IdleTrimRules.Decide(TimeSpan.FromHours(5), 1, false, null, 0, compiled).Should().Be(IdleTrimAction.None, "轉送或上傳還在處理");
            IdleTrimRules.Decide(TimeSpan.FromHours(5), 3, true, Hour * 2, Growth * 4, compiled).Should().Be(IdleTrimAction.None, "處理中的時候也不再回收");
            IdleTrimRules.Decide(TimeSpan.FromSeconds(1), 0, false, TimeSpan.FromMinutes(5), Growth * 10, compiled).Should().Be(IdleTrimAction.None, "剛有活動");
        }
    }

    [Fact]
    public void 啟動之後第一次靜置滿_沒有樣式也回收一次()
    {
        IdleTrimRules.Decide(Quiet, 0, false, null, 0, dlpCompiled: false).Should().Be(IdleTrimAction.Trim, "啟動讀資料庫留下的已提交空間");
        IdleTrimRules.Decide(Quiet, 0, false, null, 0, dlpCompiled: true).Should().Be(IdleTrimAction.Trim);
    }

    [Fact]
    public void 新的一段閒置_已提交長了16MB就回收_只有短請求的不回收()
    {
        var since = TimeSpan.FromMinutes(3);
        IdleTrimRules.Decide(Quiet, 0, false, since, Growth, dlpCompiled: false).Should().Be(IdleTrimAction.Trim, "媒體轉送之後已提交的空間長了");
        IdleTrimRules.Decide(Quiet, 0, false, since, Growth, dlpCompiled: true).Should().Be(IdleTrimAction.Trim, "長了 16 MB 的不等 10 分鐘");
        IdleTrimRules.Decide(Quiet, 0, false, since, Growth - 1, dlpCompiled: false).Should().Be(IdleTrimAction.None, "只有版本檢查之類的短請求");
        IdleTrimRules.Decide(Quiet, 0, false, Hour * 5, Growth - 1, dlpCompiled: false).Should().Be(IdleTrimAction.None, "隔得再久，沒有東西可修剪也不做");
        IdleTrimRules.Decide(Quiet, 0, false, since, -Growth, dlpCompiled: false).Should().Be(IdleTrimAction.None);
    }

    /// <summary>只因為樣式（已提交沒長到 16 MB）的修剪要距上一次回收滿 10 分鐘：平常使用時回合之間的短閒置不是每次都積極回收</summary>
    [Fact]
    public void 新的一段閒置_只有樣式的要距上一次回收滿10分鐘()
    {
        var interval = IdleTrimRules.MinDlpOnlyTrimInterval;
        interval.Should().Be(TimeSpan.FromMinutes(10));
        IdleTrimRules.Decide(Quiet, 0, false, interval - TimeSpan.FromSeconds(1), 0, dlpCompiled: true).Should().Be(IdleTrimAction.None, "9 分 59 秒：還不到");
        IdleTrimRules.Decide(Quiet, 0, false, interval, 0, dlpCompiled: true).Should().Be(IdleTrimAction.Trim, "滿 10 分鐘");
        IdleTrimRules.Decide(Quiet, 0, false, interval - TimeSpan.FromSeconds(1), Growth - 1, dlpCompiled: true).Should().Be(IdleTrimAction.None, "差 1 位元組到門檻：還是只有樣式");
        IdleTrimRules.Decide(Quiet, 0, false, TimeSpan.FromMinutes(2), Growth - 1, dlpCompiled: true).Should().Be(IdleTrimAction.None, "兩分鐘前才回收過");
        IdleTrimRules.Decide(Quiet, 0, false, TimeSpan.FromMinutes(2), Growth, dlpCompiled: true).Should().Be(IdleTrimAction.Trim, "到門檻：立即");
        IdleTrimRules.Decide(Quiet, 0, false, null, 0, dlpCompiled: true).Should().Be(IdleTrimAction.Trim, "啟動之後第一次不受影響");
        IdleTrimRules.Decide(Quiet, 1, false, interval * 3, 0, dlpCompiled: true).Should().Be(IdleTrimAction.None, "有請求在處理照樣不做");
    }

    [Fact]
    public void 同一段閒置_滿一小時而且已提交又長了16MB才再回收()
    {
        IdleTrimRules.Decide(Hour * 3, 0, true, Hour, Growth, dlpCompiled: false).Should().Be(IdleTrimAction.Recompact);
        IdleTrimRules.Decide(Hour * 3, 0, true, Hour - TimeSpan.FromSeconds(1), Growth * 8, dlpCompiled: false).Should().Be(IdleTrimAction.None, "一小時最多一次");
        IdleTrimRules.Decide(Hour * 3, 0, true, Hour * 2, Growth - 1, dlpCompiled: false).Should().Be(IdleTrimAction.None, "沒長到門檻");
        IdleTrimRules.Decide(Hour * 3, 0, true, TimeSpan.FromMinutes(1), 0, dlpCompiled: true).Should().Be(IdleTrimAction.None, "同一段閒置不因樣式再做（背景工作建的留到下一段）");
    }

    [Theory]
    [InlineData(DeviceHubMethods.Heartbeat, false)]
    [InlineData(DeviceHubMethods.UsagePublish, false)]
    [InlineData(DeviceHubMethods.RefreshAccessToken, false)]
    [InlineData(DeviceHubMethods.Hello, true)]
    [InlineData(DeviceHubMethods.Prove, true)]
    [InlineData(DeviceHubMethods.PublishBatch, true)]
    [InlineData(DeviceHubMethods.Pull, true)]
    [InlineData(DeviceHubMethods.LiveRelay, true)]
    [InlineData(DeviceHubMethods.GetTrust, true)]
    [InlineData(DeviceHubMethods.CommandResult, true)]
    [InlineData(DeviceHubMethods.CommandOutput, true)]
    [InlineData("Cmd", true)]
    [InlineData("ClientState", true)]
    [InlineData("heartbeat", true)]
    [InlineData(null, true)]
    public void 心跳用量與換權杖不算活動_其他呼叫都算(string? method, bool counts) =>
        IdleTrimRules.CountsAsActivity(method).Should().Be(counts);

    [Theory]
    [InlineData("/healthz", true)]
    [InlineData("/readyz", true)]
    [InlineData("/READYZ", true)]
    [InlineData("/hubs", true)]
    [InlineData("/hubs/v1/device", true)]
    [InlineData("/hubs/v1/browser/negotiate", true)]
    [InlineData("/healthz/x", false)]
    [InlineData("/hubsx", false)]
    [InlineData("/api/v1/ops/status", false)]
    [InlineData("/api/v1/device/relay/01ARZ3NDEKTSV4RRFFQ69G5FAV", false)]
    [InlineData("/api/v1/uploads/u1", false)]
    [InlineData("/m/key", false)]
    [InlineData("/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 健康檢查與hub的連線不算請求活動(string? path, bool background) =>
        IdleTrimRules.IsBackgroundRequest(path).Should().Be(background);

    [Fact]
    public void 已提交的估計_取上一次GC與目前配置的較大者()
    {
        IdleTrimRules.CommittedEstimate(100, 40).Should().Be(100);
        IdleTrimRules.CommittedEstimate(40, 100).Should().Be(100);
    }
}
