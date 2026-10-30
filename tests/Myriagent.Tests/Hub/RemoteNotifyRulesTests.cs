using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;

namespace Myriagent.Tests.Hub;

/// <summary>遠端動作的本機通知與啟動提醒（WP4.4 計畫步 10、D30；契約 D8、D26）</summary>
public sealed class RemoteNotifyRulesTests
{
    private static RemoteOutcome Done(int tier) => new(RemoteResultStates.Done, null, null, tier, false, null);

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(0, false)]
    public void Decide_done而且層級1以上才通知(int tier, bool notifies)
    {
        RemoteNotifyRules.Decide("send", Done(tier),notifyEnabled: true, shownInForeground: false, lastTrayAtMs: null, nowMs: 5_000)
            .Should().Be(notifies ? RemoteNotifyKind.Tray : RemoteNotifyKind.None);
        RemoteNotifyRules.Decide("send", Done(tier),notifyEnabled: true, shownInForeground: true, lastTrayAtMs: null, nowMs: 5_000)
            .Should().Be(notifies ? RemoteNotifyKind.ViewToast : RemoteNotifyKind.None);
    }

    [Fact]
    public void Decide_rejected與unknown不通知()
    {
        var rejected = new RemoteOutcome(RemoteResultStates.Rejected, RemoteResultCodes.Policy, "不允許", 2, false, RemoteGateSteps.Policy);
        var unknown = new RemoteOutcome(RemoteResultStates.Unknown, null, "不明", 2, false, RemoteGateSteps.Handler);

        RemoteNotifyRules.Decide("send", rejected, true, false, null, 5_000).Should().Be(RemoteNotifyKind.None);
        RemoteNotifyRules.Decide("send", unknown, true, false, null, 5_000).Should().Be(RemoteNotifyKind.None);
        RemoteNotifyRules.Decide("send", rejected, true, true, null, 5_000).Should().Be(RemoteNotifyKind.None, "前景也不提示");
    }

    [Fact]
    public void Decide_關閉時不通知()
    {
        RemoteNotifyRules.Decide("send", Done(2), notifyEnabled: false, shownInForeground: false, lastTrayAtMs: null, nowMs: 5_000).Should().Be(RemoteNotifyKind.None);
        RemoteNotifyRules.Decide("send", Done(2), notifyEnabled: false, shownInForeground: true, lastTrayAtMs: null, nowMs: 5_000).Should().Be(RemoteNotifyKind.None);
    }

    [Fact]
    public void Decide_對話開在前景_只在視窗提示_不看系統匣的間隔()
    {
        RemoteNotifyRules.Decide("send", Done(1), true, shownInForeground: true, lastTrayAtMs: null, nowMs: 5_000).Should().Be(RemoteNotifyKind.ViewToast);
        RemoteNotifyRules.Decide("send", Done(1), true, shownInForeground: true, lastTrayAtMs: 4_999, nowMs: 5_000).Should().Be(RemoteNotifyKind.ViewToast);
    }

    [Theory]
    [InlineData("listFiles")]
    [InlineData("fileSearch")]
    public void Decide_網頁自動送的listFiles與fileSearch前景與不在前景都不通知(string cmd)
    {
        // 合併前審查 INT-5：@ 提示每停 120 ms 送一次 fileSearch、抽屜每換一層送一次 listFiles，原本對話開在前景時每次都跳提示
        RemoteNotifyRules.Decide(cmd, Done(1), true, shownInForeground: true, lastTrayAtMs: null, nowMs: 5_000).Should().Be(RemoteNotifyKind.None);
        RemoteNotifyRules.Decide(cmd, Done(2), true, shownInForeground: false, lastTrayAtMs: null, nowMs: 5_000).Should().Be(RemoteNotifyKind.None);
    }

    [Theory]
    [InlineData("fileFetch")]
    [InlineData("addUploaded")]
    public void Decide_fileFetch與addUploaded是使用者的操作_照常通知(string cmd)
    {
        RemoteNotifyRules.Decide(cmd, Done(2), true, shownInForeground: true, lastTrayAtMs: null, nowMs: 5_000).Should().Be(RemoteNotifyKind.ViewToast);
        RemoteNotifyRules.Decide(cmd, Done(2), true, shownInForeground: false, lastTrayAtMs: null, nowMs: 5_000).Should().Be(RemoteNotifyKind.Tray);
    }

    [Fact]
    public void Decide_系統匣每分鐘最多一則_60秒內None_滿60秒Tray()
    {
        const long last = 1_000_000;

        RemoteNotifyRules.Decide("send", Done(1), true, false, last, last + 1).Should().Be(RemoteNotifyKind.None);
        RemoteNotifyRules.Decide("send", Done(1), true, false, last, last + RemoteNotifyRules.TrayIntervalMs - 1).Should().Be(RemoteNotifyKind.None);
        RemoteNotifyRules.Decide("send", Done(1), true, false, last, last + RemoteNotifyRules.TrayIntervalMs).Should().Be(RemoteNotifyKind.Tray);
        RemoteNotifyRules.Decide("send", Done(1), true, false, last, last + 3_600_000).Should().Be(RemoteNotifyKind.Tray);
        RemoteNotifyRules.TrayIntervalMs.Should().Be(60_000);
    }

    [Fact]
    public void Describe_對話的指令寫在哪個對話()
    {
        RemoteNotifyRules.Describe("send", "Safari・iOS", "修正登入流程").Should().Be("Safari・iOS 在『修正登入流程』送出了訊息");
        RemoteNotifyRules.Describe("delete", "Chrome・Windows", "舊對話").Should().Be("Chrome・Windows 在『舊對話』刪除了對話（已移到回收區）");
    }

    [Fact]
    public void Describe_帳戶與開新對話_對象放在動作後面()
    {
        RemoteNotifyRules.Describe("accountLogout", "Safari・iOS", "Claude 主帳戶").Should().Be("Safari・iOS 登出了帳戶『Claude 主帳戶』");
        RemoteNotifyRules.Describe(RemoteCommandCatalog.ConversationCreate, "Safari・iOS", "整理報表").Should().Be("Safari・iOS 開了新對話『整理報表』");
    }

    [Fact]
    public void Describe_沒有對象或標籤()
    {
        RemoteNotifyRules.Describe("cliCheck", "Safari・iOS", null).Should().Be("Safari・iOS 檢查了 CLI 的版本");
        RemoteNotifyRules.Describe("rename", "Safari・iOS", "   ").Should().Be("Safari・iOS 改了對話名稱");
        RemoteNotifyRules.Describe("rename", "", "標題").Should().Be($"{RemoteNotifyRules.UnknownIssuer} 在『標題』改了對話名稱");
        RemoteNotifyRules.Describe("rename", " \n ", null).Should().Be($"{RemoteNotifyRules.UnknownIssuer} 改了對話名稱");
    }

    [Fact]
    public void Describe_標題單行_拿掉格式字元_截斷不切開代理字對()
    {
        var rtl = ((char)0x202E).ToString();
        RemoteNotifyRules.Describe("send", "Safari・iOS", "第一行\r\n\t第二行  " + rtl + "尾").Should().Be("Safari・iOS 在『第一行 第二行 尾』送出了訊息");

        var longTitle = new string('長', 100);
        var clipped = RemoteNotifyRules.Describe("rename", "Safari・iOS", longTitle);
        clipped.Should().Be($"Safari・iOS 在『{new string('長', RemoteNotifyRules.MaxSubjectLength - 1)}…』改了對話名稱");

        var emoji = char.ConvertFromUtf32(0x1F600);
        var title = new string('a', RemoteNotifyRules.MaxSubjectLength - 2) + emoji + "bbbb";
        var cut = RemoteNotifyRules.Describe("rename", "Safari・iOS", title);
        cut.Should().Be($"Safari・iOS 在『{new string('a', RemoteNotifyRules.MaxSubjectLength - 2)}…』改了對話名稱", "表情符號的代理字對不會只留一半");
    }

    [Fact]
    public void 型錄每個電腦執行的指令都有自己的說法()
    {
        var actions = new List<string>();
        foreach (var spec in RemoteCommandCatalog.All)
        {
            var (action, inConversation) = RemoteNotifyRules.ActionOf(spec.Name);
            action.Should().NotStartWith("執行了遠端操作", spec.Name);
            inConversation.Should().Be(spec.Route == RemoteRoute.Owner, $"{spec.Name}：對話的指令寫「在『標題』」");
            actions.Add(action);
        }

        actions.Should().OnlyHaveUniqueItems();
        RemoteNotifyRules.ActionOf("someNewCommand").Action.Should().Contain("someNewCommand", "不認得的寫指令名稱");
    }

    [Fact]
    public void 啟動提醒_Off不提醒_View與Control提醒_不認得的層級不提醒()
    {
        RemoteNotifyRules.StartupReminder(SyncSettings.LevelOff, true).Should().BeNull();
        RemoteNotifyRules.StartupReminder(null, false).Should().BeNull();
        RemoteNotifyRules.StartupReminder("control", false).Should().BeNull("大小寫不同不是合格的層級");

        RemoteNotifyRules.StartupReminder(SyncSettings.LevelView, false).Should().Contain("只能查看").And.Contain("多電腦同步")
            .And.Contain("不能做任何操作").And.NotContain("也可以停止代理", "View 不收任何遠端指令，連 stop 也在閘門 5e 拒絕（合併前審查 GATE-2）");
        var control = RemoteNotifyRules.StartupReminder(SyncSettings.LevelControl, false);
        control.Should().Contain("可以操作").And.NotContain("完全自動");
        RemoteNotifyRules.StartupReminder(SyncSettings.LevelControl, true).Should().Contain("可以操作，允許完全自動");
    }
}
