using Myriagent.UI;
using Microsoft.Win32;

namespace Myriagent.Tests.UI;

/// <summary>
/// 沒有人看得到畫面時不畫網頁（2026-11-12）：鎖定、沒接在螢幕上、螢幕關閉的判斷，與漏掉事件時的保險。
/// 判斷錯的代價不對稱（以為沒人看而其實看得到是一片空白）：拿不準的一律當成看得到
/// </summary>
public class ScreenPresenceRulesTests
{
    private static readonly ScreenPresenceState Seen = new(Locked: false, Detached: false, DisplayOff: false);

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]   // 變暗還看得到
    [InlineData(-1, false)]  // 不認得的值當成開著
    [InlineData(7, false)]
    public void 螢幕狀態_只有0算關閉(int value, bool off)
        => ScreenPresenceRules.DisplayOff(value).Should().Be(off);

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, false, true)]
    [InlineData(-1, false, false)]  // WTS_SESSIONSTATE_UNKNOWN：不算鎖定，也不算明確沒鎖
    [InlineData(5, false, false)]
    public void 工作階段旗標_0鎖定_1明確沒鎖_其他都不算(int flags, bool locked, bool unlocked)
    {
        ScreenPresenceRules.LockedFromSessionFlags(flags).Should().Be(locked);
        ScreenPresenceRules.UnlockedFromSessionFlags(flags).Should().Be(unlocked);
    }

    [Theory]
    [InlineData(0, false)]  // WTSActive
    [InlineData(1, false)]  // WTSConnected
    [InlineData(4, true)]   // WTSDisconnected
    [InlineData(5, false)]
    public void 連線狀態_只有Disconnected算沒接上(int state, bool detached)
        => ScreenPresenceRules.DetachedFromConnectState(state).Should().Be(detached);

    [Fact]
    public void 鎖定與解鎖事件()
    {
        var locked = ScreenPresenceRules.Apply(Seen, SessionSwitchReason.SessionLock);
        locked.Locked.Should().BeTrue();
        ScreenPresenceRules.Unseen(locked, beingViewed: false).Should().BeTrue();
        var unlocked = ScreenPresenceRules.Apply(locked, SessionSwitchReason.SessionUnlock);
        unlocked.Should().Be(Seen);
        ScreenPresenceRules.Unseen(unlocked, beingViewed: false).Should().BeFalse();
    }

    [Theory]
    [InlineData(SessionSwitchReason.ConsoleDisconnect, SessionSwitchReason.ConsoleConnect)]
    [InlineData(SessionSwitchReason.RemoteDisconnect, SessionSwitchReason.RemoteConnect)]
    [InlineData(SessionSwitchReason.ConsoleDisconnect, SessionSwitchReason.RemoteConnect)] // 從本機換成遠端桌面連進來
    public void 中斷連線與重新連線事件(SessionSwitchReason off, SessionSwitchReason on)
    {
        var detached = ScreenPresenceRules.Apply(Seen, off);
        detached.Detached.Should().BeTrue();
        ScreenPresenceRules.Unseen(detached, beingViewed: false).Should().BeTrue();
        ScreenPresenceRules.Apply(detached, on).Should().Be(Seen);
    }

    [Theory]
    [InlineData(SessionSwitchReason.SessionLogon)]
    [InlineData(SessionSwitchReason.SessionLogoff)]
    [InlineData(SessionSwitchReason.SessionRemoteControl)]
    public void 不相干的事件不變(SessionSwitchReason reason)
    {
        ScreenPresenceRules.Apply(Seen, reason).Should().Be(Seen);
        var locked = Seen with { Locked = true, DisplayOff = true };
        ScreenPresenceRules.Apply(locked, reason).Should().Be(locked);
    }

    [Fact]
    public void 螢幕關閉_沒有人看得到_被遠端桌面看著時照常畫()
    {
        var off = Seen with { DisplayOff = true };
        ScreenPresenceRules.Unseen(off, beingViewed: false).Should().BeTrue();
        ScreenPresenceRules.Unseen(off, beingViewed: true).Should().BeFalse("對方看到的是擷取的畫面，和實體螢幕開不開無關");
    }

    [Fact]
    public void 鎖定或沒接上時_被遠端桌面看著也不必畫()
    {
        ScreenPresenceRules.Unseen(Seen with { Locked = true }, beingViewed: true).Should().BeTrue("對方看到的也是鎖定畫面");
        ScreenPresenceRules.Unseen(Seen with { Detached = true }, beingViewed: true).Should().BeTrue();
    }

    [Fact]
    public void 都正常時看得到()
        => ScreenPresenceRules.Unseen(Seen, beingViewed: false).Should().BeFalse();

    [Fact]
    public void 保險_系統說沒鎖沒中斷就改回來()
    {
        var stuck = new ScreenPresenceState(Locked: true, Detached: true, DisplayOff: false);
        ScreenPresenceRules.Recheck(stuck, locked: false, detached: false, inputSinceDisplayOff: false).Should().Be(Seen);
        ScreenPresenceRules.Recheck(stuck, locked: true, detached: false, inputSinceDisplayOff: false)
            .Should().Be(Seen with { Locked = true });
    }

    [Fact]
    public void 保險_螢幕關閉之後有人動過鍵盤滑鼠就不當成關著()
    {
        var off = Seen with { DisplayOff = true };
        ScreenPresenceRules.Recheck(off, locked: false, detached: false, inputSinceDisplayOff: true).Should().Be(Seen);
        ScreenPresenceRules.Recheck(off, locked: false, detached: false, inputSinceDisplayOff: false).Should().Be(off, "沒有人動過：螢幕還關著");
    }

    [Fact]
    public void 保險_鎖定或沒接上時有輸入也不當成螢幕開了()
    {
        // 鎖定畫面上打密碼也是輸入：這時螢幕開不開都看不到 app
        var lockedOff = new ScreenPresenceState(Locked: true, Detached: false, DisplayOff: true);
        ScreenPresenceRules.Recheck(lockedOff, locked: true, detached: false, inputSinceDisplayOff: true).Should().Be(lockedOff);
    }

    [Theory]
    [InlineData(10_000, 9_000, false)]   // 最後一次輸入在螢幕關閉之前
    [InlineData(10_000, 10_500, false)]  // 關螢幕的那一下按鍵（2 秒內）不算
    [InlineData(10_000, 12_000, true)]
    [InlineData(10_000, 60_000, true)]
    [InlineData(10_000, 0, false)]       // 問不到輸入時間
    public void 螢幕關閉之後的輸入_要比關閉晚兩秒以上(long offAt, long lastInput, bool expected)
        => ScreenPresenceRules.InputSinceDisplayOff(offAt, lastInput).Should().Be(expected);

    [Fact]
    public void 這台真的問得到工作階段的狀態與最後輸入時間()
    {
        var session = ScreenPresence.NativeSession.Query();
        session.Should().NotBeNull("WTSQuerySessionInformation 的 WTSSessionInfoEx 在 Windows 10／11 都有");
        session!.Value.ConnectState.Should().BeInRange(0, 9);
        session.Value.Flags.Should().BeOneOf(0, 1, -1);
        ScreenPresence.NativeSession.LastInputTick().Should().BeLessThanOrEqualTo(Environment.TickCount64);
        // 鎖定畫面的程序在不在都可以（跑測試時電腦可能鎖著），只要不丟例外
        _ = ScreenPresence.NativeSession.LogonUiRunning();
    }
}
