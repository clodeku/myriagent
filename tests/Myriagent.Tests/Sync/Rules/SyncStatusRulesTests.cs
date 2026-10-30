using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

public sealed class SyncStatusRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);

    private static SyncStatusSnapshot Snapshot(SyncEngineState state = SyncEngineState.Live, int pending = 0, string? detail = null) =>
        new(state, SyncStopReason.None, detail, DevTrustRules.DefaultServer, pending, 1791580000000, null, "d_0123456789ABCDEFGHJKMNPQRS",
            "ABCD-EFGH", "ABCD-EFGH-JKMN-PQRS", null, null, false);

    [Theory]
    [InlineData("Idle", "connecting")]
    [InlineData("Connecting", "connecting")]
    [InlineData("Handshaking", "connecting")]
    [InlineData("Draining", "connecting")]
    [InlineData("Live", "online")]
    [InlineData("Backoff", "offline")]
    [InlineData("Paused", "paused")]
    [InlineData("Stopped", "stopped")]
    [InlineData("NeedsConfirmation", "stopped")]
    public void WebState對應表(string name, string expected)
    {
        var state = Enum.Parse<SyncEngineState>(name);
        SyncStatusRules.WebState(state, pairing: false).Should().Be(expected);
        SyncStatusRules.WebState(state, pairing: true).Should().Be("pairing", "配對中一律 pairing");
    }

    [Fact]
    public void WebState_每個狀態都有對應()
    {
        foreach (var state in Enum.GetValues<SyncEngineState>())
            SyncStatusRules.WebState(state, false).Should().BeOneOf("connecting", "online", "offline", "paused", "stopped");
        FluentActions.Invoking(() => SyncStatusRules.WebState((SyncEngineState)999, false)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 只有pending變_500ms內排尾端_尾端時間正確()
    {
        var sent = Snapshot(pending: 3);

        SyncStatusRules.Decide(sent, Snapshot(pending: 4), T0, T0.AddMilliseconds(120))
            .Should().Be(new StatusPublish.At(T0.AddMilliseconds(500)), "距上次送出 120 ms，排在上次＋500 ms");
        SyncStatusRules.Decide(sent, Snapshot(pending: 9), T0, T0.AddMilliseconds(499))
            .Should().Be(new StatusPublish.At(T0.AddMilliseconds(500)));
        SyncStatusRules.Decide(sent, Snapshot(pending: 4), T0, T0.AddMilliseconds(500)).Should().BeOfType<StatusPublish.Now>("滿 500 ms 就立刻送");
        SyncStatusRules.Decide(sent, Snapshot(pending: 3), T0, T0.AddMilliseconds(10)).Should().BeOfType<StatusPublish.Skip>("完全沒變");
    }

    [Fact]
    public void state變_立刻送()
    {
        var sent = Snapshot(SyncEngineState.Live, pending: 3);

        SyncStatusRules.Decide(null, sent, T0, T0).Should().BeOfType<StatusPublish.Now>("第一次");
        SyncStatusRules.Decide(sent, Snapshot(SyncEngineState.Backoff, pending: 3), T0, T0.AddMilliseconds(1)).Should().BeOfType<StatusPublish.Now>();
        SyncStatusRules.Decide(sent, Snapshot(SyncEngineState.Backoff, pending: 9), T0, T0.AddMilliseconds(1)).Should().BeOfType<StatusPublish.Now>("state 和 pending 一起變");
        SyncStatusRules.Decide(sent, Snapshot(pending: 3, detail: "伺服器連不上"), T0, T0.AddMilliseconds(1)).Should().BeOfType<StatusPublish.Now>("detail 變");
        SyncStatusRules.Decide(sent, sent with { ClockSkewMs = 185000 }, T0, T0.AddMilliseconds(1)).Should().BeOfType<StatusPublish.Now>("時差變");
        SyncStatusRules.Decide(sent, sent with { ConfirmFingerprint = "ABCD-EFGH-JKMN-PQRT" }, T0, T0.AddMilliseconds(1)).Should().BeOfType<StatusPublish.Now>("要本機確認");
        SyncStatusRules.Decide(sent, sent with { Pairing = new PairingStatus("MTLZ-XFCH", "https://x/#pair", "https://x/#pair=MTLZ-XFCH", 1, null) }, T0, T0.AddMilliseconds(1))
            .Should().BeOfType<StatusPublish.Now>("配對進度");
    }

    [Fact]
    public void Detail_停止退避與確認()
    {
        SyncStatusRules.Detail(SyncEngineState.Stopped, SyncStopReason.Unauthorized, null, null, false).Should().Contain("重新配對");
        SyncStatusRules.Detail(SyncEngineState.Stopped, SyncStopReason.Protocol, null, null, false).Should().Contain("更新 app");
        SyncStatusRules.Detail(SyncEngineState.Stopped, SyncStopReason.Forbidden, new SyncFailure(SyncFailureKind.Forbidden, "403", "x"), null, false).Should().Contain("403");
        SyncStatusRules.Detail(SyncEngineState.NeedsConfirmation, SyncStopReason.None, null, null, false).Should().Contain("admin status");
        SyncStatusRules.Detail(SyncEngineState.Backoff, SyncStopReason.None, new SyncFailure(SyncFailureKind.Transient, null, "伺服器連不上"), TimeSpan.FromSeconds(29.2), false)
            .Should().Be("伺服器連不上，30 秒後重試");
        SyncStatusRules.Detail(SyncEngineState.Backoff, SyncStopReason.None, new SyncFailure(SyncFailureKind.Protocol, "protocol", "伺服器較舊"), TimeSpan.FromMinutes(59.5), false)
            .Should().Be("伺服器較舊，60 分鐘後重試", "兩分鐘以上改用分鐘（protocol 一小時重試一次）");
        SyncStatusRules.Detail(SyncEngineState.Backoff, SyncStopReason.None, null, TimeSpan.FromSeconds(119), false).Should().Be("伺服器連不上，119 秒後重試");
        SyncStatusRules.Detail(SyncEngineState.UpdateOnly, SyncStopReason.None, null, null, false).Should().Be(SyncStatusRules.UpdateOnlyText);
        SyncStatusRules.WebState(SyncEngineState.UpdateOnly, pairing: false).Should().Be("offline", "只更新的連線不同步資料");
        TraySyncRules.StatusText(Snapshot(SyncEngineState.UpdateOnly, pending: 2)).Should().Be("只提供更新（app 版本太舊）（2 筆待送）");
        foreach (var state in Enum.GetValues<SyncEngineState>())
            SyncStatusRules.WebState(state, pairing: false).Should().NotBeNullOrEmpty(state.ToString());
        SyncStatusRules.Detail(SyncEngineState.Live, SyncStopReason.None, null, null, false).Should().BeNull();
        foreach (var reason in Enum.GetValues<SyncStopReason>())
            SyncStatusRules.Detail(SyncEngineState.Stopped, reason, null, null, false).Should().NotBeNullOrWhiteSpace(reason.ToString());
    }

    [Fact]
    public void Detail_開發模式加註()
    {
        SyncStatusRules.Detail(SyncEngineState.Live, SyncStopReason.None, null, null, devMode: true).Should().Be(SyncStatusRules.DevModeNote);
        SyncStatusRules.Detail(SyncEngineState.Paused, SyncStopReason.None, null, null, devMode: true)
            .Should().Be($"同步已暫停（{SyncStatusRules.DevModeNote}）");
        SyncStatusRules.Detail(SyncEngineState.Paused, SyncStopReason.None, null, null, devMode: false).Should().Be("同步已暫停");
    }
}
