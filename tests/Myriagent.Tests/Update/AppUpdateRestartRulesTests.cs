using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Hub;
using Myriagent.Update;

namespace Myriagent.Tests.Update;

public sealed class AppUpdateRestartRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0);

    private static AppRestartSituation Idle(bool staged = true) => new(
        Staged: staged, AnyWindowForeground: false, SinceLastInput: TimeSpan.FromMinutes(10), CliUpdating: false, LoginInProgress: false,
        InstallInProgress: false, Now: Now, SnoozedUntil: null, RetryAfter: null, UserRequested: false);

    [Fact]
    public void 已備妥而且使用者沒在用_開始套用()
        => AppUpdateRestartRules.CanBegin(Idle(), AppUpdateRestartRules.IdleThreshold).Should().BeTrue();

    [Fact]
    public void 沒有備妥的版本_不開始()
        => AppUpdateRestartRules.CanBegin(Idle(staged: false), AppUpdateRestartRules.IdleThreshold).Should().BeFalse();

    [Theory]
    [InlineData(false, 0, true)]     // 視窗都不在前景（藏在系統匣、最小化、其他程式在前景）
    [InlineData(true, 119, false)]   // 在前景而且 2 分鐘內有輸入
    [InlineData(true, 120, true)]    // 在前景但超過 2 分鐘沒有輸入
    public void 使用者閒置的判斷(bool foreground, int seconds, bool idle)
        => AppUpdateRestartRules.IsUserIdle(foreground, TimeSpan.FromSeconds(seconds), AppUpdateRestartRules.IdleThreshold).Should().Be(idle);

    [Fact]
    public void 使用者在用_不開始_按立即重啟就開始()
    {
        var busy = Idle() with { AnyWindowForeground = true, SinceLastInput = TimeSpan.FromSeconds(5) };
        AppUpdateRestartRules.CanBegin(busy, AppUpdateRestartRules.IdleThreshold).Should().BeFalse();
        AppUpdateRestartRules.CanBegin(busy with { UserRequested = true }, AppUpdateRestartRules.IdleThreshold).Should().BeTrue();
    }

    [Fact]
    public void RemoteControl的對話在用_列成阻擋原因_不開始_按立即重啟就開始()
    {
        var remote = Idle() with { RemoteInUse = true };
        AppUpdateRestartRules.Blocker(remote).Should().Be(AppRestartBlocker.RemoteInUse, "橫幅與設定頁說明為什麼還沒重啟，不再承諾閒置時自動重啟");
        AppUpdateRestartRules.CanBegin(remote, AppUpdateRestartRules.IdleThreshold).Should().BeFalse("桌面沒人時使用者可能正在手機上接手");
        AppUpdateRestartRules.Blocker(remote with { UserRequested = true }).Should().Be(AppRestartBlocker.None, "按立即重啟等於條件 1 成立");
        AppUpdateRestartRules.CanBegin(remote with { UserRequested = true }, AppUpdateRestartRules.IdleThreshold).Should().BeTrue();
        AppUpdateRestartRules.Blocker(remote with { SnoozedUntil = Now.AddHours(1) }).Should().Be(AppRestartBlocker.Snoozed, "冷卻期間本來就不自動套用：說明冷卻");
        AppUpdateRestartRules.Blocker(remote with { CliUpdating = true, UserRequested = true }).Should().Be(AppRestartBlocker.CliUpdating, "系統工作照樣優先");
    }

    [Fact]
    public void 執行列正在建置_列成阻擋原因_不開始_按立即重啟就開始()
    {
        var launched = Idle() with { LaunchBuilding = true };
        AppUpdateRestartRules.Blocker(launched).Should().Be(AppRestartBlocker.LaunchBuilding, "結束 app 會中斷建置");
        AppUpdateRestartRules.CanBegin(launched, AppUpdateRestartRules.IdleThreshold).Should().BeFalse("建置完就要執行");
        AppUpdateRestartRules.CanBegin(launched with { UserRequested = true }, AppUpdateRestartRules.IdleThreshold).Should().BeTrue();
        AppUpdateRestartRules.Blocker(launched with { RemoteInUse = true }).Should().Be(AppRestartBlocker.RemoteInUse);
        AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.LaunchBuilding).Should().Contain("執行列");

        // Debug 模式執行中的程式：重啟會一起結束它，不自動開始；按「立即重啟」照樣開始
        var debugging = Idle() with { LaunchDebugging = true };
        AppUpdateRestartRules.Blocker(debugging).Should().Be(AppRestartBlocker.LaunchDebugging);
        AppUpdateRestartRules.CanBegin(debugging, AppUpdateRestartRules.IdleThreshold).Should().BeFalse();
        AppUpdateRestartRules.Blocker(debugging with { UserRequested = true }).Should().Be(AppRestartBlocker.None);
        AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.LaunchDebugging).Should().Contain("Debug");
    }

    /// <summary>2026-11-10：下載另一台的 9.8 GB 資料夾時，來源那台在途中自動換版重開，一個檔失敗——傳檔案期間不自動開始</summary>
    [Fact]
    public void 正在和其他電腦傳檔案_列成阻擋原因_不自動開始_按立即重啟就開始()
    {
        var transferring = Idle() with { FileTransfer = true };
        AppUpdateRestartRules.Blocker(transferring).Should().Be(AppRestartBlocker.FileTransfer, "重啟會切斷傳輸");
        AppUpdateRestartRules.CanBegin(transferring, AppUpdateRestartRules.IdleThreshold).Should().BeFalse();
        AppUpdateRestartRules.CanBegin(transferring with { UserRequested = true }, AppUpdateRestartRules.IdleThreshold).Should().BeTrue();
        AppUpdateRestartRules.Blocker(transferring with { RemoteExecRunning = true }).Should().Be(AppRestartBlocker.RemoteExecRunning);
        AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.FileTransfer).Should().Contain("傳檔案");
    }

    /// <summary>
    /// 2026-10-28 使用者要求桌面版換版一直等到背景工作都完成（以前排空最多等 30 分鐘就停掉，先停下的對話這段時間一直停著）：
    /// 有對話的背景工作還在跑就不開始，按「立即重啟」也等（重啟會停掉它們）；系統工作與冷卻照舊優先說明
    /// </summary>
    [Fact]
    public void 有對話的背景工作還在跑_不開始_按立即重啟也等它們都結束()
    {
        var background = Idle() with { BackgroundWork = true };
        AppUpdateRestartRules.Blocker(background).Should().Be(AppRestartBlocker.BackgroundWork);
        AppUpdateRestartRules.CanBegin(background, AppUpdateRestartRules.IdleThreshold).Should().BeFalse("重啟會停掉背景工作");
        AppUpdateRestartRules.Blocker(background with { UserRequested = true }).Should().Be(AppRestartBlocker.BackgroundWork, "立即重啟也不略過：記著，結束就開始");
        AppUpdateRestartRules.CanBegin(background with { UserRequested = true }, AppUpdateRestartRules.IdleThreshold).Should().BeFalse();
        AppUpdateRestartRules.CanBegin(background with { UserRequested = true, BackgroundWork = false, AnyWindowForeground = true, SinceLastInput = TimeSpan.Zero },
            AppUpdateRestartRules.IdleThreshold).Should().BeTrue("按過立即重啟的，背景工作都結束就馬上開始（不必等閒置）");
        AppUpdateRestartRules.Blocker(background with { SnoozedUntil = Now.AddHours(1) }).Should().Be(AppRestartBlocker.Snoozed, "冷卻期間本來就不自動套用：說明冷卻");
        AppUpdateRestartRules.Blocker(background with { CliUpdating = true, UserRequested = true }).Should().Be(AppRestartBlocker.CliUpdating, "系統工作照樣優先");
        AppUpdateRestartRules.Blocker(background with { RetryAfter = Now.AddMinutes(5) }).Should().Be(AppRestartBlocker.BackgroundWork,
            "排空中有對話開始背景工作而放棄的：說明在等背景工作，不是「稍後再試」");
        AppUpdateRestartRules.Blocker(background with { RemoteInUse = true }).Should().Be(AppRestartBlocker.BackgroundWork);
        AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.BackgroundWork).Should().Contain("背景工作").And.NotContain("分鐘", "沒有時間上限");

        // 「強制更新」不等背景工作（排空中滿 10 分鐘還沒停下的照立即套用停下）；系統工作照樣先等
        var forced = background with { UserRequested = true, Forced = true, AnyWindowForeground = true, SinceLastInput = TimeSpan.Zero };
        AppUpdateRestartRules.Blocker(forced).Should().Be(AppRestartBlocker.None);
        AppUpdateRestartRules.CanBegin(forced, AppUpdateRestartRules.IdleThreshold).Should().BeTrue("不等背景工作、也不等閒置");
        AppUpdateRestartRules.Blocker(forced with { CliUpdating = true }).Should().Be(AppRestartBlocker.CliUpdating);
        AppUpdateRestartRules.Blocker(background with { Forced = true }).Should().Be(AppRestartBlocker.BackgroundWork, "說明照常（顯示用的不帶使用者的要求）");
    }

    [Theory]
    [InlineData(false, true, true, 0, false)]    // 沒開 Remote Control：不管其他
    [InlineData(true, false, false, 9, true)]    // 10 分鐘內有動靜
    [InlineData(true, false, false, 10, false)]  // 滿 10 分鐘沒有動靜
    [InlineData(true, true, false, 59, true)]    // 在等回應（手機上可能正要核准）：1 小時內都算
    [InlineData(true, true, false, 60, false)]   // 卡片 1 小時沒人回：不再算，交給排空（等回應滿 10 分鐘照立即套用停下、新版重新提出）
    [InlineData(true, false, true, 59, true)]    // 有工具在執行（遠端開始的一輪跑很久的指令，這段期間沒有其他動靜）
    [InlineData(true, false, true, 60, false)]   // 1 小時沒有動靜的工具（可能是不會結束的前景指令）：交給排空（等到排空上限 35 分鐘）
    public void RemoteControl在用的判斷(bool remoteControl, bool waiting, bool toolRunning, int minutesAgo, bool inUse)
        => AppUpdateRestartRules.IsRemoteInUse(remoteControl, waiting, toolRunning, Now.AddMinutes(-minutesAgo), Now,
                AppUpdateRestartRules.RemoteIdleThreshold, AppUpdateRestartRules.RemoteWaitThreshold)
            .Should().Be(inUse);

    [Fact]
    public void 系統工作進行中_連立即重啟也不開始()
    {
        foreach (var s in new[]
                 {
                     Idle() with { CliUpdating = true, UserRequested = true },
                     Idle() with { LoginInProgress = true, UserRequested = true },
                     Idle() with { InstallInProgress = true, UserRequested = true },
                 })
        {
            AppUpdateRestartRules.Blocker(s).Should().NotBe(AppRestartBlocker.None);
            AppUpdateRestartRules.CanBegin(s, AppUpdateRestartRules.IdleThreshold).Should().BeFalse();
        }
    }

    [Fact]
    public void 取消後6小時內與放棄後1小時內_不自動開始_按立即重啟可以()
    {
        var snoozed = Idle() with { SnoozedUntil = Now.AddHours(5) };
        AppUpdateRestartRules.Blocker(snoozed).Should().Be(AppRestartBlocker.Snoozed);
        AppUpdateRestartRules.CanBegin(snoozed with { UserRequested = true }, AppUpdateRestartRules.IdleThreshold).Should().BeTrue();
        var retry = Idle() with { RetryAfter = Now.AddMinutes(30) };
        AppUpdateRestartRules.Blocker(retry).Should().Be(AppRestartBlocker.RetryLater);
        AppUpdateRestartRules.Blocker(Idle() with { RetryAfter = Now }).Should().Be(AppRestartBlocker.None, "時間到了");
    }

    [Fact]
    public void 排空上限35分鐘_比背景工作的上限多5分鐘()
    {
        AppUpdateRestartRules.DrainExpired(Now, Now.AddMinutes(34)).Should().BeFalse();
        AppUpdateRestartRules.DrainExpired(Now, Now.AddMinutes(35)).Should().BeTrue();
        (AppUpdateRestartRules.DrainLimit - CliRestartRules.BackgroundWaitLimit).Should().Be(TimeSpan.FromMinutes(5),
            "等背景工作的對話滿 30 分鐘才停下：要留時間讓它停下、排空做完，不然一律在停下之前就被放棄");
    }

    [Fact]
    public void 排空中要求結束_改成結束時套用()
    {
        // 系統匣「結束」：照常結束（不卡在排空）、有已備妥的版本就在結束時換版（不重新啟動、不接續）；Windows 關機或登出時時間不夠，不換版
        AppUpdateRestartRules.OnExit(staged: true, windowsShuttingDown: false).Should().Be(AppExitAction.ApplyOnExit);
        AppUpdateRestartRules.OnExit(staged: true, windowsShuttingDown: true).Should().Be(AppExitAction.Plain);
        AppUpdateRestartRules.OnExit(staged: false, windowsShuttingDown: false).Should().Be(AppExitAction.Plain);
    }

    [Fact]
    public void CLI更新中的判斷()
    {
        var idle = new CliState(AgentProvider.Codex, null, "0.160.1", null, Busy: false, null, null, null);
        AppUpdateRestartRules.IsCliUpdating(idle).Should().BeFalse();
        AppUpdateRestartRules.IsCliUpdating(idle with { Busy = true }).Should().BeTrue();
        AppUpdateRestartRules.IsCliUpdating(idle with { UpdatePhase = CliUpdatePhase.Draining }).Should().BeTrue();
        AppUpdateRestartRules.IsCliUpdating(idle with { InstallChanging = true }).Should().BeTrue();
    }

    [Fact]
    public void 每個阻擋原因都有說明()
    {
        foreach (var b in Enum.GetValues<AppRestartBlocker>().Where(b => b != AppRestartBlocker.None))
            AppUpdateRestartRules.DescribeBlocker(b).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void 時限照設計()
    {
        // 設計 8.4、9.1～9.3、第 12 節的數值（第 13 節把放棄後 1 小時、幫手失敗 6 小時再試列在這個類別的測試）；
        // 改常數要一起改設計與 DescribeBlocker 的說明文字
        AppUpdateRestartRules.CheckInterval.Should().Be(TimeSpan.FromHours(6));
        AppUpdateRestartRules.IdleThreshold.Should().Be(TimeSpan.FromMinutes(2));
        AppUpdateRestartRules.RemoteIdleThreshold.Should().Be(TimeSpan.FromMinutes(10), "設計 9.1 條件 1：Remote Control 的對話 10 分鐘沒有動靜");
        AppUpdateRestartRules.RemoteWaitThreshold.Should().Be(TimeSpan.FromHours(1), "設計 9.1 條件 1：在等回應或有工具在執行的 1 小時沒有動靜就不再算在用");
        AppUpdateRestartRules.DrainLimit.Should().Be(TimeSpan.FromMinutes(35));
        AppUpdateRestartRules.RetryAfterAbort.Should().Be(TimeSpan.FromHours(1), "排空逾時放棄後 1 小時再試");
        AppUpdateRestartRules.RetryAfterBackground.Should().Be(TimeSpan.FromMinutes(5), "排空中有對話開始背景工作、放棄之後 5 分鐘內不再自動開始");
        AppUpdateRestartRules.ForceStopAfter.Should().Be(TimeSpan.FromMinutes(10), "強制更新按下後 10 分鐘，還沒停下的照立即套用停下");
        AppUpdateRestartRules.RetryAfterHelperFailure.Should().Be(TimeSpan.FromHours(6), "幫手啟動不了或沒寫 .armed 時 6 小時後再試");
        AppUpdateRestartRules.SnoozeAfterCancel.Should().Be(TimeSpan.FromHours(6));
        AppUpdateRestartRules.ArmedTimeout.Should().Be(TimeSpan.FromSeconds(10));
        AppUpdateRestartRules.DraftSnapshotTimeout.Should().Be(TimeSpan.FromSeconds(2));
    }
}
