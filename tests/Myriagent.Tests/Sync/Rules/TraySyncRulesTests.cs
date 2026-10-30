using Myriagent.Core;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Pairing;

namespace Myriagent.Tests.Sync.Rules;

public sealed class TraySyncRulesTests
{
    private static SyncStatusSnapshot Status(SyncEngineState state, int pending = 0) =>
        SyncCoordinatorRules.Initial(PairingTestData.Device(), null) with { State = state, Pending = pending };

    [Fact]
    public void 未配對沒有項目()
    {
        TraySyncRules.Items(null, paired: false, paused: false, remotePaused: false).Should().BeEmpty();
        var pairing = SyncCoordinatorRules.Pairing(new Uri(PairingTestData.Server), null, SyncCoordinatorRules.PairingStarting, null);
        TraySyncRules.Items(pairing, paired: false, paused: true, remotePaused: true).Should().BeEmpty("配對中還不是已配對");
        TraySyncRules.TrayText(pairing, paired: false).Should().BeNull();
    }

    [Fact]
    public void 已連線_狀態項目停用_暫停與遠端可按()
    {
        var items = TraySyncRules.Items(Status(SyncEngineState.Live), paired: true, paused: false, remotePaused: false);

        items.Should().Equal(
            new TraySyncItem("同步：已連線", false, TraySyncAction.None),
            new TraySyncItem("暫停同步", true, TraySyncAction.PauseSync),
            new TraySyncItem("暫停遠端操作", true, TraySyncAction.PauseRemote));
        TraySyncRules.TrayText(Status(SyncEngineState.Live), paired: true).Should().Be("同步 已連線");
    }

    [Fact]
    public void 暫停時顯示繼續同步()
    {
        var items = TraySyncRules.Items(Status(SyncEngineState.Paused, pending: 2), paired: true, paused: true, remotePaused: false);

        items[0].Text.Should().Be("同步：已暫停（2 筆待送）");
        items[1].Should().Be(new TraySyncItem("繼續同步", true, TraySyncAction.ResumeSync));
        TraySyncRules.Items(Status(SyncEngineState.Live), paired: true, paused: true, remotePaused: false)[1].Action
            .Should().Be(TraySyncAction.ResumeSync, "照設定顯示，不等引擎回報 Paused");
    }

    [Fact]
    public void 離線顯示待送筆數()
    {
        TraySyncRules.Items(Status(SyncEngineState.Backoff, pending: 3), paired: true, paused: false, remotePaused: false)[0].Text
            .Should().Be("同步：離線（3 筆待送）");
        TraySyncRules.Items(Status(SyncEngineState.Backoff), paired: true, paused: false, remotePaused: false)[0].Text
            .Should().Be("同步：離線", "沒有待送的不加註");
        TraySyncRules.TrayText(Status(SyncEngineState.Backoff, pending: 3), paired: true).Should().Be("同步 離線（3 筆待送）");
    }

    [Fact]
    public void 遠端暫停時Hello政策為Off()
    {
        var settings = new SyncSettings { RemoteControlLevel = SyncSettings.LevelControl, AllowRemoteFullAccess = true, RemotePaused = true };

        TraySyncRules.Items(Status(SyncEngineState.Live), paired: true, paused: false, remotePaused: true)[2]
            .Should().Be(new TraySyncItem("恢復遠端操作", true, TraySyncAction.ResumeRemote));
        SyncCoordinatorRules.HelloPolicy(settings).Should().Be(new DevicePolicy(SyncSettings.LevelOff, false));
        settings.RemotePaused = false;
        SyncCoordinatorRules.HelloPolicy(settings).Should().Be(new DevicePolicy(SyncSettings.LevelControl, true), "恢復後回報設定值");
    }

    [Theory]
    [InlineData("Idle", "連線中")]
    [InlineData("Connecting", "連線中")]
    [InlineData("Handshaking", "連線中")]
    [InlineData("Draining", "連線中")]
    [InlineData("Live", "已連線")]
    [InlineData("Backoff", "離線")]
    [InlineData("Paused", "已暫停")]
    [InlineData("Stopped", "已停止")]
    [InlineData("NeedsConfirmation", "伺服器身分改變，請到設定確認")]
    public void StatusText_每個狀態都有文字(string state, string expected)
    {
        TraySyncRules.StatusText(Status(Enum.Parse<SyncEngineState>(state))).Should().Be(expected);
    }

    [Fact]
    public void 回補中_選單多一列停用的回補進度()
    {
        var items = TraySyncRules.Items(Status(SyncEngineState.Live), paired: true, paused: false, remotePaused: false,
            backfill: new SyncBackfillProgress(12, 52428800));

        items.Should().Equal(
            new TraySyncItem("同步：已連線", false, TraySyncAction.None),
            new TraySyncItem("回補到伺服器：還有 12 個對話（約 50 MB）", false, TraySyncAction.None),
            new TraySyncItem("暫停同步", true, TraySyncAction.PauseSync),
            new TraySyncItem("暫停遠端操作", true, TraySyncAction.PauseRemote));
        TraySyncRules.Items(null, paired: false, paused: false, remotePaused: false, backfill: new SyncBackfillProgress(12, 52428800))
            .Should().BeEmpty("未配對照舊沒有項目");
    }

    [Fact]
    public void 沒有回補_選單逐項和之前相同()
    {
        foreach (var (paused, remotePaused) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var baseline = TraySyncRules.Items(Status(SyncEngineState.Backoff, pending: 3), paired: true, paused, remotePaused);
            TraySyncRules.Items(Status(SyncEngineState.Backoff, pending: 3), paired: true, paused, remotePaused, backfill: null)
                .Should().Equal(baseline);
            TraySyncRules.Items(Status(SyncEngineState.Backoff, pending: 3), paired: true, paused, remotePaused, backfill: null, backfillPaused: true)
                .Should().Equal(baseline, "沒有在回補時暫停也不加列");
            baseline.Should().HaveCount(3);
        }
        TraySyncRules.TrayText(Status(SyncEngineState.Live), paired: true, backfill: null).Should().Be("同步 已連線");
    }

    [Fact]
    public void 回補暫停_文字是已暫停()
    {
        var items = TraySyncRules.Items(Status(SyncEngineState.Live), paired: true, paused: false, remotePaused: false,
            backfill: new SyncBackfillProgress(12, 52428800), backfillPaused: true);

        items[1].Should().Be(new TraySyncItem("回補已暫停（保護這台的磁碟）", false, TraySyncAction.None));
        items.Should().HaveCount(4);
        TraySyncRules.BackfillText(new SyncBackfillProgress(1, 1), paused: true).Should().Be("回補已暫停（保護這台的磁碟）");
        TraySyncRules.BackfillText(null, paused: true).Should().BeNull();
        TraySyncRules.BackfillText(null, paused: false).Should().BeNull();
    }

    [Fact]
    public void TrayText_回補中加上回補N個對話()
    {
        var backfill = new SyncBackfillProgress(12, 52428800);

        TraySyncRules.TrayText(Status(SyncEngineState.Live), paired: true, backfill).Should().Be("同步 已連線・回補 12 個對話");
        TraySyncRules.TrayText(Status(SyncEngineState.Backoff, pending: 3), paired: true, backfill).Should().Be("同步 離線（3 筆待送）・回補 12 個對話");
        TraySyncRules.TrayText(Status(SyncEngineState.Live), paired: false, backfill).Should().BeNull();
    }

    [Theory]
    [InlineData(0L, "不到 1 MB")]
    [InlineData(1L, "不到 1 MB")]
    [InlineData(1048575L, "不到 1 MB")]
    [InlineData(1048576L, "約 1 MB")]
    [InlineData(1048577L, "約 2 MB")]
    [InlineData(52428800L, "約 50 MB")]
    [InlineData(52428801L, "約 51 MB")]
    [InlineData(12345L * 1048576L, "約 12,345 MB")]
    public void 位元組_無條件進位到MB_不到1MB寫不到1MB(long bytes, string size)
    {
        TraySyncRules.BackfillText(new SyncBackfillProgress(3, bytes), paused: false).Should().Be($"回補到伺服器：還有 3 個對話（{size}）");
    }

    [Fact]
    public void StatusText_沒有狀態是連線中_不認得的狀態丟例外()
    {
        TraySyncRules.StatusText(null).Should().Be("連線中");
        Enum.GetValues<SyncEngineState>().Should().HaveCount(10, "新的狀態要在系統匣有文字（U3 加了 UpdateOnly）");
        var act = () => TraySyncRules.StatusText(Status((SyncEngineState)99));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
