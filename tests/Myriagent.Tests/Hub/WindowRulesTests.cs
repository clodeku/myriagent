using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>多視窗的純函式規則（WindowRules）</summary>
public sealed class WindowRulesTests
{
    private sealed class View(string id, bool popout = false) : IAgentView
    {
        public string Id => id;

        public bool IsPopout => popout;

        public void PostToWeb(Newtonsoft.Json.Linq.JObject message)
        {
        }
    }

    private sealed class Window(string id, bool primary) : IWindowView
    {
        public string Id => id;

        public bool IsPopout => false;

        public string WindowKey => primary ? WindowRules.PrimaryKey : "k-" + id;

        public bool IsPrimaryWindow => primary;

        public void PostToWeb(Newtonsoft.Json.Linq.JObject message)
        {
        }
    }

    [Fact]
    public void 視窗的種類_一般檢視當成主視窗_彈出視窗都不是()
    {
        WindowRules.IsWindow(new Window("a", true)).Should().BeTrue();
        WindowRules.IsWindow(new Window("b", false)).Should().BeTrue();
        WindowRules.IsWindow(new View("c")).Should().BeFalse("不是 IWindowView 的檢視（遠端、同步、測試）不能搬分頁");

        WindowRules.IsPrimary(new Window("a", true)).Should().BeTrue();
        WindowRules.IsPrimary(new Window("b", false)).Should().BeFalse();
        WindowRules.IsPrimary(new View("c")).Should().BeTrue("多視窗之前只有一個主視窗");
        WindowRules.IsPrimary(new View("d", popout: true)).Should().BeFalse();

        WindowRules.RoleOf(new Window("a", true)).Should().Be("primary");
        WindowRules.RoleOf(new Window("b", false)).Should().Be("extra");
        WindowRules.RoleOf(new View("d", popout: true)).Should().BeNull();
    }

    [Fact]
    public void 要拿掉分頁的視窗_其他完整視窗加上拖曳的來源_不含目標與彈出視窗_不重複()
    {
        var windows = new HashSet<string> { "a", "b", "c" };
        WindowRules.SourcesToClear(new[] { "a", "pop", "b" }, windows.Contains, "b", null).Should().Equal("a");
        WindowRules.SourcesToClear(new[] { "a" }, windows.Contains, "b", "c").Should().Equal("a", "c");
        WindowRules.SourcesToClear(new[] { "a" }, windows.Contains, "b", "a").Should().Equal("a");
        WindowRules.SourcesToClear(Array.Empty<string>(), windows.Contains, "b", "b").Should().BeEmpty("來源就是目標");
        WindowRules.SourcesToClear(Array.Empty<string>(), windows.Contains, "b", "pop").Should().BeEmpty("彈出視窗不算");
    }

    [Fact]
    public void 拖曳資訊_同一個對話而且沒過期才用()
    {
        var now = new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc);
        var drag = new TabDragInfo("c1", "a", "字", now);
        WindowRules.DragApplies(drag, "c1", now.AddSeconds(3)).Should().BeTrue();
        WindowRules.DragApplies(drag, "c2", now).Should().BeFalse();
        WindowRules.DragApplies(drag, "c1", now + WindowRules.TabDragTtl + TimeSpan.FromSeconds(1)).Should().BeFalse();
        WindowRules.DragApplies(drag, "c1", now.AddSeconds(-5)).Should().BeFalse("時鐘往回跳不算");
        WindowRules.DragApplies(null, "c1", now).Should().BeFalse();
    }

    [Fact]
    public void 帶過去的字_空白不帶_太長截掉()
    {
        WindowRules.CarriedText(null).Should().BeNull();
        WindowRules.CarriedText("  \n").Should().BeNull();
        WindowRules.CarriedText(" 字 ").Should().Be(" 字 ", "有字就原樣帶過去（含前後空白）");
        WindowRules.CarriedText(new string('a', WindowRules.MaxCarriedText + 10))!.Length.Should().Be(WindowRules.MaxCarriedText);
    }

    [Fact]
    public void 新視窗的鍵每次不同_不是主視窗的鍵()
    {
        var a = WindowRules.NewKey();
        var b = WindowRules.NewKey();
        a.Should().NotBe(b);
        a.Should().NotBe(WindowRules.PrimaryKey);
    }
}
