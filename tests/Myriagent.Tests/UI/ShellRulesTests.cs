using Myriagent.UI;
using Microsoft.Web.WebView2.Core;

namespace Myriagent.Tests.UI;

/// <summary>桌面外殼的規則：單一實例名稱、WebView2 程序失敗、關閉與 Windows 登出／關機、視窗位置與 DPI、標題</summary>
public class ShellRulesTests
{
    private static readonly string DefaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Myriagent");

    // ───── 單一實例 ─────

    [Theory]
    [InlineData(@"C:\tmp\myriagent-data")]
    [InlineData(@"C:\tmp\myriagent-data\")]
    [InlineData(@"C:/tmp/myriagent-data")]
    [InlineData(@"C:/tmp/myriagent-data/")]
    [InlineData(@"c:\TMP\Myriagent-Data")]
    [InlineData(@"C:\tmp\x\..\myriagent-data")]
    public void InstanceSuffix_同一個資料夾的不同寫法得到同一組名稱(string root)
        => ShellRules.InstanceSuffix(root, DefaultRoot).Should().Be(".03A4E8B96DA0", "與修正前不帶結尾斜線的寫法相同（已經在執行的實例照樣認得）；修正前帶結尾斜線會得到 .5EED4FD22CBF");

    [Fact]
    public void InstanceSuffix_預設資料夾沒有後綴_不同資料夾各自一組()
    {
        ShellRules.InstanceSuffix(DefaultRoot, DefaultRoot).Should().BeEmpty();
        ShellRules.InstanceSuffix(DefaultRoot + Path.DirectorySeparatorChar, DefaultRoot).Should().BeEmpty();
        ShellRules.InstanceSuffix(DefaultRoot.ToUpperInvariant(), DefaultRoot).Should().BeEmpty();
        ShellRules.InstanceSuffix(@"C:\tmp\a", DefaultRoot).Should().NotBe(ShellRules.InstanceSuffix(@"C:\tmp\b", DefaultRoot));
        ShellRules.InstanceSuffix(@"C:\tmp\a", DefaultRoot).Should().MatchRegex(@"^\.[0-9A-F]{12}$");
    }

    [Fact]
    public void NormalizeDataRoot_相對路徑展開成完整路徑_去掉結尾斜線_磁碟根目錄保留()
    {
        ShellRules.NormalizeDataRoot("data").Should().Be(Path.Combine(Environment.CurrentDirectory, "data"));
        ShellRules.NormalizeDataRoot(@"C:\tmp\data\\").Should().Be(@"C:\tmp\data");
        ShellRules.NormalizeDataRoot(@"C:/tmp/data").Should().Be(@"C:\tmp\data");
        ShellRules.NormalizeDataRoot(@"C:\").Should().Be(@"C:\");
    }

    // ───── WebView2 程序失敗 ─────

    [Theory]
    [InlineData(CoreWebView2ProcessFailedKind.BrowserProcessExited, nameof(WebViewRecovery.RecreateWebView))]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessExited, nameof(WebViewRecovery.Reload))]
    [InlineData(CoreWebView2ProcessFailedKind.RenderProcessUnresponsive, nameof(WebViewRecovery.AskReload))]
    [InlineData(CoreWebView2ProcessFailedKind.GpuProcessExited, nameof(WebViewRecovery.None))]
    [InlineData(CoreWebView2ProcessFailedKind.UtilityProcessExited, nameof(WebViewRecovery.None))]
    [InlineData(CoreWebView2ProcessFailedKind.SandboxHelperProcessExited, nameof(WebViewRecovery.None))]
    [InlineData(CoreWebView2ProcessFailedKind.FrameRenderProcessExited, nameof(WebViewRecovery.None))]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiPluginProcessExited, nameof(WebViewRecovery.None))]
    [InlineData(CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited, nameof(WebViewRecovery.None))]
    [InlineData(CoreWebView2ProcessFailedKind.UnknownProcessExited, nameof(WebViewRecovery.None))]
    public void RecoveryFor_只有瀏覽器與主框架渲染程序結束要復原_其他程序自動重建不重新載入(CoreWebView2ProcessFailedKind kind, string expected)
        => ShellRules.RecoveryFor(kind).Should().Be(Enum.Parse<WebViewRecovery>(expected));

    [Fact]
    public void UnresponsiveTracker_持續沒有回應一段時間才問_問過之後一段時間內不再問()
    {
        var t0 = new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);
        var tracker = new UnresponsiveTracker();

        tracker.Report(t0).Should().BeFalse("第一次回報可能只是暫時忙碌");
        tracker.Report(t0.AddSeconds(4)).Should().BeFalse();
        tracker.Report(t0.AddSeconds(8)).Should().BeFalse();
        tracker.Report(t0.AddSeconds(12)).Should().BeTrue("連續沒有回應超過 10 秒");
        tracker.Report(t0.AddSeconds(16)).Should().BeFalse("對話框開著時 WebView2 仍會每隔幾秒回報，不能再跳一個");

        tracker.Dismissed(t0.AddSeconds(30));
        tracker.Report(t0.AddSeconds(34)).Should().BeFalse("使用者選了繼續等待");
        tracker.Report(t0.AddSeconds(50)).Should().BeFalse();
        tracker.Report(t0.AddSeconds(70)).Should().BeFalse();
        tracker.Report(t0.AddSeconds(89)).Should().BeFalse();
        tracker.Report(t0.AddSeconds(91)).Should().BeTrue("繼續等待 60 秒後還是沒有回應，再問一次");
    }

    [Fact]
    public void UnresponsiveTracker_中間恢復過就重新計算_重新載入後也重新計算()
    {
        var t0 = new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);
        var tracker = new UnresponsiveTracker();

        tracker.Report(t0).Should().BeFalse();
        tracker.Report(t0.AddSeconds(5)).Should().BeFalse();
        tracker.Report(t0.AddSeconds(40)).Should().BeFalse("隔了 35 秒沒有回報＝中間已經恢復，這是新的一段");
        tracker.Report(t0.AddSeconds(45)).Should().BeFalse();
        tracker.Report(t0.AddSeconds(50)).Should().BeTrue();

        tracker.Reset();
        tracker.Report(t0.AddSeconds(52)).Should().BeFalse("重新載入後重新計算");
        tracker.Report(t0.AddSeconds(63)).Should().BeTrue();
    }

    // ───── 關閉與 Windows 登出／關機 ─────

    [Theory]
    [InlineData(CloseReason.UserClosing, true, nameof(MainClosingAction.HideToTray))]
    [InlineData(CloseReason.UserClosing, false, nameof(MainClosingAction.Exit))]
    [InlineData(CloseReason.WindowsShutDown, true, nameof(MainClosingAction.SaveForSessionEnd))]
    [InlineData(CloseReason.WindowsShutDown, false, nameof(MainClosingAction.SaveForSessionEnd))]
    [InlineData(CloseReason.TaskManagerClosing, true, nameof(MainClosingAction.ExitImmediately))]
    [InlineData(CloseReason.ApplicationExitCall, false, nameof(MainClosingAction.ExitImmediately))]
    [InlineData(CloseReason.None, true, nameof(MainClosingAction.ExitImmediately))]
    public void ClosingAction_Windows詢問登出關機時只存檔_不停代理也不結束(CloseReason reason, bool closeToTray, string expected)
        => ShellRules.ClosingAction(reason, exiting: false, closeToTray).Should().Be(Enum.Parse<MainClosingAction>(expected));

    [Theory]
    [InlineData(CloseReason.UserClosing)]
    [InlineData(CloseReason.WindowsShutDown)]
    [InlineData(CloseReason.TaskManagerClosing)]
    public void ClosingAction_已經在結束流程裡就照常關閉(CloseReason reason)
        => ShellRules.ClosingAction(reason, exiting: true, closeToTray: true).Should().Be(MainClosingAction.Close);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClosingAction_還有另外開的視窗看得到_使用者關主視窗只藏起來(bool closeToTray)
    {
        ShellRules.ClosingAction(CloseReason.UserClosing, exiting: false, closeToTray, otherWindowsVisible: true).Should().Be(MainClosingAction.Hide,
            "像 Chrome 關掉一個視窗：其他視窗照常用，不結束 app、也不提示系統匣");
        ShellRules.ClosingAction(CloseReason.WindowsShutDown, exiting: false, closeToTray, otherWindowsVisible: true).Should().Be(MainClosingAction.SaveForSessionEnd);
        ShellRules.ClosingAction(CloseReason.UserClosing, exiting: true, closeToTray, otherWindowsVisible: true).Should().Be(MainClosingAction.Close);
    }

    [Theory]
    [InlineData(CloseReason.UserClosing, true, true, nameof(ExtraClosingAction.Discard))]
    [InlineData(CloseReason.UserClosing, true, false, nameof(ExtraClosingAction.Discard))]
    [InlineData(CloseReason.UserClosing, false, true, nameof(ExtraClosingAction.Discard))]
    [InlineData(CloseReason.UserClosing, false, false, nameof(ExtraClosingAction.Exit))]
    [InlineData(CloseReason.None, false, false, nameof(ExtraClosingAction.Discard))]
    [InlineData(CloseReason.WindowsShutDown, true, true, nameof(ExtraClosingAction.Keep))]
    [InlineData(CloseReason.TaskManagerClosing, false, false, nameof(ExtraClosingAction.Keep))]
    [InlineData(CloseReason.ApplicationExitCall, true, true, nameof(ExtraClosingAction.Keep))]
    public void 另外開的視窗關閉_使用者關掉的移除_結束與關機留著_不縮到系統匣的最後一個視窗當成結束(CloseReason reason, bool closeToTray, bool otherVisible, string expected)
        => ShellRules.ExtraWindowClosingAction(reason, exiting: false, closeToTray, otherVisible).Should().Be(Enum.Parse<ExtraClosingAction>(expected));

    [Theory]
    [InlineData(CloseReason.UserClosing)]
    [InlineData(CloseReason.None)]
    public void 另外開的視窗_結束app的途中關閉一律留著設定(CloseReason reason)
        => ShellRules.ExtraWindowClosingAction(reason, exiting: true, closeToTray: false, otherWindowsVisible: false).Should().Be(ExtraClosingAction.Keep);

    [Fact]
    public void 再開一次app_主視窗看得到就開新視窗_藏著就叫出主視窗()
    {
        ShellRules.LaunchAgainOpensWindow(primaryVisible: true).Should().BeTrue();
        ShellRules.LaunchAgainOpensWindow(primaryVisible: false).Should().BeFalse();
    }

    [Fact]
    public void ExitWhenClosed_只有確定登出關機才在關閉後結束()
    {
        ShellRules.ExitWhenClosed(CloseReason.WindowsShutDown, exiting: false).Should().BeTrue("WM_ENDSESSION（wParam＝TRUE）");
        ShellRules.ExitWhenClosed(CloseReason.WindowsShutDown, exiting: true).Should().BeFalse("已經在結束");
        ShellRules.ExitWhenClosed(CloseReason.UserClosing, exiting: false).Should().BeFalse();
        ShellRules.ExitWhenClosed(CloseReason.TaskManagerClosing, exiting: false).Should().BeFalse("關閉前已經立即結束");
    }

    // ───── 視窗大小與 DPI ─────

    [Theory]
    [InlineData(820, 900, 96, 820, 900)]
    [InlineData(820, 900, 144, 1230, 1350)]
    [InlineData(820, 900, 120, 1025, 1125)]
    [InlineData(520, 480, 192, 1040, 960)]
    [InlineData(960, 620, 144, 1440, 930)]
    [InlineData(960, 620, 0, 960, 620)]
    public void ScaleForDpi_邏輯像素換算成實體像素(int width, int height, int dpi, int expectedWidth, int expectedHeight)
        => ShellRules.ScaleForDpi(new Size(width, height), dpi).Should().Be(new Size(expectedWidth, expectedHeight));

    [Fact]
    public void FitWithin_超過工作區比例就縮小_沒超過照舊()
    {
        ShellRules.FitWithin(new Size(1230, 1350), new Rectangle(0, 0, 3840, 2112), 0.9, 0.9).Should().Be(new Size(1230, 1350));
        ShellRules.FitWithin(new Size(1025, 1125), new Rectangle(0, 0, 1920, 1032), 0.9, 0.9).Should().Be(new Size(1025, 928), "125% 縮放的 1080p 筆電放不下 1125 高");
        ShellRules.FitWithin(new Size(2250, 1410), new Rectangle(0, 0, 1920, 1040), 0.85, 0.88).Should().Be(new Size(1632, 915));
    }

    [Fact]
    public void FitMinimumSize_換算後的最小大小不超過最小的螢幕工作區()
    {
        var at175 = ShellRules.ScaleForDpi(new Size(960, 620), 168);
        at175.Should().Be(new Size(1680, 1085));
        ShellRules.FitMinimumSize(at175, new[] { new Rectangle(0, 0, 1920, 1032) }).Should().Be(new Size(1680, 1032), "1080p 開 175% 縮放：比工作區還高就以工作區為準");
        ShellRules.FitMinimumSize(new Size(1440, 930), new[] { new Rectangle(0, 0, 3840, 2088), new Rectangle(3840, 0, 1920, 1032) })
            .Should().Be(new Size(1440, 930), "兩個螢幕都放得下");
        ShellRules.FitMinimumSize(new Size(1440, 930), new[] { new Rectangle(0, 0, 3840, 2088), new Rectangle(3840, 0, 1280, 688) })
            .Should().Be(new Size(1280, 688), "以最小的螢幕為準，移過去也放得下");
        ShellRules.FitMinimumSize(new Size(1440, 930), Array.Empty<Rectangle>()).Should().Be(new Size(1440, 930));
    }

    // ───── 還原視窗位置 ─────

    private static readonly Rectangle Primary = new(0, 0, 1920, 1040);
    private static readonly Rectangle External = new(1920, 0, 2560, 1400);

    [Fact]
    public void 新視窗_從來源錯開一樣大_掉出工作區就繞回左上角()
    {
        var source = new Rectangle(100, 80, 1200, 800);
        ShellRules.NewWindowBounds(source, null, 96, new[] { Primary }).Should().Be(new Rectangle(132, 112, 1200, 800));
        ShellRules.NewWindowBounds(source, null, 144, new[] { Primary }).Should().Be(new Rectangle(148, 128, 1200, 800), "錯開的距離依 DPI 換算");

        var bottomRight = new Rectangle(700, 230, 1200, 800);
        ShellRules.NewWindowBounds(bottomRight, null, 96, new[] { Primary }).Should().Be(new Rectangle(32, 32, 1200, 800), "像串聯排列一樣繞回來");
    }

    [Fact]
    public void 拖出分頁的新視窗_游標落在分頁列附近_放不下就縮到游標所在螢幕()
    {
        var source = new Rectangle(0, 0, 1500, 900);
        ShellRules.NewWindowBounds(source, new Point(600, 400), 96, new[] { Primary }).Should().Be(new Rectangle(420, 140, 1500, 900),
            "游標在新視窗左上角往內 140×18 的地方；超出工作區下緣就往上推");
        ShellRules.NewWindowBounds(source, new Point(300, 100), 96, new[] { Primary }).Should().Be(new Rectangle(160, 82, 1500, 900));

        var small = new Rectangle(0, 1040, 1024, 700);
        var bounds = ShellRules.NewWindowBounds(new Rectangle(0, 0, 1800, 1000), new Point(200, 1200), 96, new[] { Primary, small });
        small.Contains(bounds).Should().BeTrue("拖到比較小的螢幕：縮到那個螢幕放得下");
        bounds.Width.Should().Be((int)(1024 * 0.9));
    }

    [Fact]
    public void FitToScreens_標題列抓得到就照存檔放()
    {
        var inside = new Rectangle(100, 100, 1200, 800);
        ShellRules.FitToScreens(inside, new[] { Primary }).Should().Be(inside);

        var spanning = new Rectangle(1800, 100, 1500, 900);
        ShellRules.FitToScreens(spanning, new[] { Primary, External }).Should().Be(spanning, "跨兩個螢幕，標題列在兩邊加起來都看得到");

        var slightlyAbove = new Rectangle(100, -10, 1200, 800);
        ShellRules.FitToScreens(slightlyAbove, new[] { Primary }).Should().Be(slightlyAbove, "只超出上緣一點，標題列大部分還在");
    }

    [Fact]
    public void FitToScreens_拔掉外接螢幕後只剩一角_移回主螢幕內()
    {
        // 視窗原本在右邊的外接螢幕，左緣跨進主螢幕 20 像素
        var saved = new Rectangle(1900, 100, 1500, 900);
        ShellRules.FitToScreens(saved, new[] { Primary, External }).Should().Be(saved, "外接螢幕還在時照舊");
        ShellRules.FitToScreens(saved, new[] { Primary }).Should().Be(new Rectangle(420, 100, 1500, 900), "修正前只要重疊 1 像素就照存檔放，只剩 20 像素可見");

        ShellRules.FitToScreens(new Rectangle(1820, 100, 1500, 900), new[] { Primary }).Should().Be(new Rectangle(420, 100, 1500, 900), "只剩 100 像素寬的標題列也不夠抓");
    }

    [Fact]
    public void FitToScreens_標題列在畫面外_沒有重疊就移到最近的螢幕_太大就縮成工作區大小()
    {
        ShellRules.FitToScreens(new Rectangle(100, -500, 1200, 800), new[] { Primary }).Should().Be(new Rectangle(100, 0, 1200, 800), "標題列在螢幕上方看不到");
        ShellRules.FitToScreens(new Rectangle(5000, 100, 1200, 800), new[] { Primary, External }).Should().Be(new Rectangle(3280, 100, 1200, 800), "最近的是右邊的外接螢幕");
        ShellRules.FitToScreens(new Rectangle(2000, 0, 3000, 2000), new[] { Primary }).Should().Be(Primary, "比工作區還大");
        ShellRules.FitToScreens(new Rectangle(0, 0, 1200, 800), Array.Empty<Rectangle>()).Should().BeNull();
    }

    // ───── 標題 ─────

    [Theory]
    [InlineData("Clode Myriagent － 修正登入流程", "Clode Myriagent － 修正登入流程")]
    [InlineData("  Clode Myriagent － 儀表板 ", "Clode Myriagent － 儀表板")]
    [InlineData("", "Clode Myriagent")]
    [InlineData("   ", "Clode Myriagent")]
    [InlineData(null, "Clode Myriagent")]
    [InlineData("https://myriagent.local/index.html?mode=popout", "Clode Myriagent")]
    [InlineData("myriagent.local/index.html", "Clode Myriagent")]
    public void WindowTitle_跟著網頁標題_空白或網址時用app名稱(string? documentTitle, string expected)
        => ShellRules.WindowTitle(documentTitle).Should().Be(expected);
}
