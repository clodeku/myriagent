using Myriagent.Remote;
using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Remote;

/// <summary>
/// 閘門的驗證時間（5f）、預設密碼（5g）、本機政策（5e）與速率（WP4.1 步 6；設計 8.10、14.11 D4、D7、D25、Q2、Q5）
/// </summary>
public sealed class RemoteGateRulesTests
{
    // ───────────────────────────── 驗證時間（Q2） ─────────────────────────────

    [Fact]
    public void 兩個時間窗都是null_任何uvAt都算新鮮_Q2()
    {
        RemoteAuthRules.Tier1WindowMs.Should().BeNull("擁有者選「都不用輸入」");
        RemoteAuthRules.Tier2WindowMs.Should().BeNull();
        for (var tier = 0; tier <= 2; tier++)
        {
            RemoteAuthRules.WindowFor(tier).Should().BeNull();
            foreach (var uvAt in new long?[] { null, 0, 1_000, 5_000_000, long.MaxValue })
                RemoteAuthRules.IsFresh(tier, uvAt, created: 5_000_000).Should().BeTrue($"tier {tier}, uvAt {uvAt}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void 參數給的時間窗_邊界前後1毫秒(int tier)
    {
        const long window = 10 * 60_000;
        const long created = 1_791_580_300_000;
        long? tier1 = tier == 1 ? window : null;
        long? tier2 = tier == 2 ? window : null;
        RemoteAuthRules.IsFresh(tier, created - window, created, tier1, tier2).Should().BeTrue("剛好在邊界算新鮮");
        RemoteAuthRules.IsFresh(tier, created - window + 1, created, tier1, tier2).Should().BeTrue();
        RemoteAuthRules.IsFresh(tier, created - window - 1, created, tier1, tier2).Should().BeFalse("超過 1 毫秒");
        RemoteAuthRules.IsFresh(tier, created, created, tier1, tier2).Should().BeTrue();
        RemoteAuthRules.IsFresh(tier, created + 1, created, tier1, tier2).Should().BeFalse("uvAt 晚於指令建立（伺服器的資料不一致）不算");
        RemoteAuthRules.IsFresh(tier, null, created, tier1, tier2).Should().BeFalse("從沒輸入過");
        RemoteAuthRules.IsFresh(tier == 1 ? 2 : 1, created - window - 1, created, tier1, tier2).Should().BeTrue("另一個層級的時間窗是 null");
        RemoteAuthRules.IsFresh(0, null, created, window, window).Should().BeTrue("層級 0 只要工作階段有效");
    }

    [Fact]
    public void 預設密碼不降任何層級_Q5()
    {
        RemoteAuthRules.DefaultPasswordMaxTier.Should().Be(2);
        for (var tier = 0; tier <= 2; tier++)
        {
            RemoteAuthRules.DefaultPasswordAllows(tier, defaultPassword: true).Should().BeTrue();
            RemoteAuthRules.DefaultPasswordAllows(tier, defaultPassword: false).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void 層級超出0到2丟例外(int tier)
    {
        ((Action)(() => RemoteAuthRules.IsFresh(tier, 0, 0))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => RemoteAuthRules.WindowFor(tier))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => RemoteAuthRules.DefaultPasswordAllows(tier, false))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => RemotePolicyRules.Check(new DevicePolicy(RemotePolicyRules.Control, true), tier, false))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => RemoteRateRules.LimitFor(tier))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => new RemoteRateWindow().TryAcquire(tier, 0))).Should().Throw<ArgumentOutOfRangeException>();
    }

    // ───────────────────────────── 本機政策（D7） ─────────────────────────────

    [Fact]
    public void 政策_只有Control收指令_完全自動另要允許()
    {
        RemotePolicyRules.Levels.Should().Equal("Off", "View", "Control");
        for (var tier = 0; tier <= 2; tier++)
        {
            foreach (var fullAccess in new[] { false, true })
            {
                RemotePolicyRules.Check(null, tier, fullAccess).Should().Be(RemoteResultCodes.Policy, "沒有政策＝Off");
                RemotePolicyRules.Check(new DevicePolicy(RemotePolicyRules.Off, true), tier, fullAccess).Should().Be(RemoteResultCodes.Policy, "Off 連 stop 也不收");
                RemotePolicyRules.Check(new DevicePolicy(RemotePolicyRules.View, true), tier, fullAccess).Should().Be(RemoteResultCodes.Policy, "View 不收任何指令");
                RemotePolicyRules.Check(new DevicePolicy("control", true), tier, fullAccess).Should().Be(RemoteResultCodes.Policy, "不認得的名稱（大小寫不同）當成 Off");
                RemotePolicyRules.Check(new DevicePolicy("", true), tier, fullAccess).Should().Be(RemoteResultCodes.Policy);
                RemotePolicyRules.Check(new DevicePolicy(RemotePolicyRules.Control, true), tier, fullAccess).Should().BeNull();
                RemotePolicyRules.Check(new DevicePolicy(RemotePolicyRules.Control, false), tier, fullAccess)
                    .Should().Be(fullAccess ? RemoteResultCodes.Policy : null, "要完全自動的另要 allowRemoteFullAccess");
            }
        }
    }

    // ───────────────────────────── 速率（D25） ─────────────────────────────

    [Fact]
    public void 速率_每分鐘60_30_5次_各層級分開()
    {
        RemoteRateRules.WindowMs.Should().Be(60_000);
        RemoteRateRules.LimitFor(0).Should().Be(60);
        RemoteRateRules.LimitFor(1).Should().Be(30);
        RemoteRateRules.LimitFor(2).Should().Be(5);

        for (var tier = 0; tier <= 2; tier++)
        {
            var window = new RemoteRateWindow();
            var limit = RemoteRateRules.LimitFor(tier);
            for (var i = 0; i < limit; i++)
                window.TryAcquire(tier, 1_000 + i).Should().BeTrue($"tier {tier} 第 {i + 1} 次");
            window.TryAcquire(tier, 1_000 + limit).Should().BeFalse("到上限");
            window.CountIn(tier, 1_000 + limit).Should().Be(limit, "被拒的不計入");
            for (var other = 0; other <= 2; other++)
            {
                if (other != tier)
                    window.TryAcquire(other, 1_000 + limit).Should().BeTrue("其他層級分開算");
            }

            // 第一次在 1000：60999 時還在窗內，61000 時滑出
            window.TryAcquire(tier, 1_000 + RemoteRateRules.WindowMs - 1).Should().BeFalse("前 1 毫秒還在窗內");
            window.TryAcquire(tier, 1_000 + RemoteRateRules.WindowMs).Should().BeTrue("最早的一次剛好滑出");
            window.TryAcquire(tier, 1_000 + RemoteRateRules.WindowMs).Should().BeFalse("只空出一個");
            window.CountIn(tier, 1_000 + RemoteRateRules.WindowMs).Should().Be(limit);
            window.CountIn(tier, 1_000_000).Should().Be(0);
            window.TryAcquire(tier, 1_000_000).Should().BeTrue("整窗過去後重新算");
        }
    }

    [Fact]
    public void 速率_持續送的時候每分鐘最多上限次()
    {
        var window = new RemoteRateWindow();
        var accepted = Enumerable.Range(0, 600).Count(i => window.TryAcquire(2, i * 1_000L));
        accepted.Should().Be(50, "10 分鐘、每秒一次，層級 2 每分鐘 5 次");
    }
}
