using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>同步安定的判斷（WP4.9 計畫步 1、D2：探針的 S6 閒置取樣與 fake-device 的回補等待共用）</summary>
public sealed class SyncSettleRulesTests
{
    private static readonly SyncSettleSnapshot Settled = new(Live: true, InFlight: 0, Refreshing: false, ReconcileActive: false, ReconcileQueue: 0, OutboxSettled: true, BackfillDone: true);

    [Fact]
    public void 全部條件成立才算安定() => SyncSettleRules.IsSettled(Settled).Should().BeTrue();

    /// <summary>每一列只翻轉一個欄位：拿掉任何一個條件，那一列就紅</summary>
    [Theory]
    [InlineData("Live")]
    [InlineData("InFlight")]
    [InlineData("Refreshing")]
    [InlineData("ReconcileActive")]
    [InlineData("ReconcileQueue")]
    [InlineData("OutboxSettled")]
    [InlineData("BackfillDone")]
    public void 任一條件不成立就不安定(string field)
    {
        var snapshot = field switch
        {
            "Live" => Settled with { Live = false },
            "InFlight" => Settled with { InFlight = 1 },
            "Refreshing" => Settled with { Refreshing = true },
            "ReconcileActive" => Settled with { ReconcileActive = true },
            "ReconcileQueue" => Settled with { ReconcileQueue = 1 },
            "OutboxSettled" => Settled with { OutboxSettled = false },
            "BackfillDone" => Settled with { BackfillDone = false },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "不認得的欄位"),
        };

        SyncSettleRules.IsSettled(snapshot).Should().BeFalse($"只有 {field} 不成立");
    }

    [Fact]
    public void QuietEnough_剛好等於quiet為真_少1tick為假()
    {
        var quiet = TimeSpan.FromSeconds(1);

        SyncSettleRules.QuietEnough(quiet, quiet).Should().BeTrue("剛好等於也算");
        SyncSettleRules.QuietEnough(quiet - TimeSpan.FromTicks(1), quiet).Should().BeFalse();
        SyncSettleRules.QuietEnough(quiet + TimeSpan.FromTicks(1), quiet).Should().BeTrue();
    }
}
