using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 多視窗（AgentHub.Windows.cs）：分頁在完整視窗之間搬移（tabMoveIn、拖到別的視窗或視窗外）、拖曳時帶著輸入框的字、
/// 新視窗以 init.movedIn 接住對話、各視窗的版面分開存、另外開的視窗搬空後自己關掉。宿主是假的（不開真的視窗、不看游標）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubWindowsTests
{
    private sealed class FakeWindowView(string id, string key, bool primary) : FakeView(id), IWindowView
    {
        public string WindowKey => key;

        public bool IsPrimaryWindow => primary;
    }

    private sealed class FakeWindowHost : IWindowHost
    {
        public List<(string Key, string? Source, bool AtCursor)> Opened { get; } = new();

        public List<string> Activated { get; } = new();

        public List<string> Closed { get; } = new();

        public List<string> Located { get; } = new();

        public TabDropTarget Drop { get; set; } = new(TabDropKind.None);

        public bool OpenResult { get; set; } = true;

        public bool OpenWindow(string key, string? sourceViewId, bool atCursor)
        {
            Opened.Add((key, sourceViewId, atCursor));
            return OpenResult;
        }

        public TabDropTarget LocateTabDrop(string sourceViewId)
        {
            Located.Add(sourceViewId);
            return Drop;
        }

        public void ActivateWindow(string viewId) => Activated.Add(viewId);

        public void CloseWindow(string viewId) => Closed.Add(viewId);

        /// <summary>中樞送來的各視窗外框（依序；只在 UI 執行緒寫）</summary>
        public List<IReadOnlyDictionary<string, WindowBorder>> Borders { get; } = new();

        public void UpdateWindowBorders(IReadOnlyDictionary<string, WindowBorder> borders)
            => Borders.Add(new Dictionary<string, WindowBorder>(borders));
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            H = new HubHarness();
            H.Ui.Invoke(() => H.Hub.AttachWindows(Windows));
            A = Attach("wa", WindowRules.PrimaryKey, primary: true);
            B = Attach("wb", "key-b", primary: false);
        }

        public HubHarness H { get; }

        public FakeWindowHost Windows { get; } = new();

        /// <summary>主視窗</summary>
        public FakeWindowView A { get; }

        /// <summary>另外開的視窗（settings.Windows 有它的設定）</summary>
        public FakeWindowView B { get; }

        public FakeWindowView Attach(string id, string key, bool primary)
        {
            if (!primary && H.Settings.FindWindow(key) == null)
                H.Settings.Windows.Add(new AppWindowState { Key = key });
            var view = new FakeWindowView(id, key, primary);
            H.Ui.Invoke(() => H.Hub.AttachView(view));
            return view;
        }

        public void Dispose() => H.Dispose();
    }

    [Fact]
    public void 接住別的視窗拖來的分頁_目標開著它並帶著輸入框的字_原本的視窗拿掉_草稿不會被丟掉()
    {
        using var r = new Rig();
        var h = r.H;
        var id = h.NewConversation(r.A, h.Claude);
        h.Command(r.A, "tabDrag", new { conversationId = id, text = "打到一半的字" });
        var markA = r.A.Mark();
        var markB = r.B.Mark();

        h.Command(r.B, "tabMoveIn", new { conversationId = id, paneId = "p2" });

        var conv = r.B.Messages("conversation", markB).Should().ContainSingle().Subject;
        conv.Value<string>("paneId").Should().Be("p2");
        conv.Value<string>("draft").Should().Be("打到一半的字", "拖曳時記下的字帶到新的分頁");
        conv["header"]!.Value<bool>("isDraft").Should().BeTrue();
        r.B.Messages("focusConversation", markB).Should().ContainSingle();
        r.A.Messages("conversationMoved", markA).Should().ContainSingle().Which.Value<string>("conversationId").Should().Be(id);
        r.A.Messages("conversationClosed", markA).Should().BeEmpty("搬走不是關掉");
        r.Windows.Activated.Should().Equal(r.B.Id);

        // 原本的視窗關掉、重新載入都不影響：草稿已經算是開在 B
        h.Ui.Invoke(() => h.Hub.DetachView(r.A.Id));
        h.Conversation(id).Should().NotBeNull("草稿在搬移後由目標視窗開著，不會因為原本的視窗關掉而被丟掉");
    }

    [Fact]
    public void 還沒切過去的背景分頁_沒送過open_拖曳的來源照樣拿掉()
    {
        using var r = new Rig();
        var h = r.H;
        var (id, _) = h.StartConversation(r.A, h.Claude);
        h.Command(r.A, "close", new { conversationId = id });   // 背景分頁：網頁沒送過 open，宿主不知道 A 開著它
        h.Command(r.A, "tabDrag", new { conversationId = id, text = "" });
        var markA = r.A.Mark();

        h.Command(r.B, "tabMoveIn", new { conversationId = id, paneId = (string?)null });

        r.A.Messages("conversationMoved", markA).Should().ContainSingle("拖曳的來源記在 tabDrag，不只看 ViewIds");
        r.B.Last("conversation", id)!.Value<string>("draft").Should().BeNull("輸入框沒有字就不帶");
    }

    [Fact]
    public void 開始拖分頁_通知其他完整視窗_拖完再通知一次_彈出視窗與來源不通知()
    {
        using var r = new Rig();
        var h = r.H;
        var (id, _) = h.StartConversation(r.A, h.Claude);
        var popout = h.AddPopout("pop", id);
        var markA = r.A.Mark();
        var markB = r.B.Mark();

        h.Command(r.A, "tabDrag", new { conversationId = id, text = "" });
        h.Command(r.A, "tabDragEnd", new { conversationId = id, dropped = true });

        r.B.Messages("tabDrag", markB).Select(m => m.Value<string>("conversationId")).Should().Equal(id, null);
        r.A.Messages("tabDrag", markA).Should().BeEmpty();
        popout.Messages("tabDrag").Should().BeEmpty();
        r.Windows.Located.Should().BeEmpty("有人接住（dropped）就不必看游標");
    }

    [Fact]
    public void 沒有人接住_游標在另一個視窗上_搬過去()
    {
        using var r = new Rig();
        var h = r.H;
        var (id, _) = h.StartConversation(r.A, h.Claude);
        r.Windows.Drop = new TabDropTarget(TabDropKind.Window, r.B.Id);
        h.Command(r.A, "tabDrag", new { conversationId = id, text = "還沒送" });
        var markA = r.A.Mark();
        var markB = r.B.Mark();

        h.Command(r.A, "tabDragEnd", new { conversationId = id, dropped = false });

        r.Windows.Located.Should().Equal(r.A.Id);
        var conv = r.B.Messages("conversation", markB).Should().ContainSingle().Subject;
        conv.Value<string>("paneId").Should().BeNull("放在目標的聚焦窗格");
        conv.Value<string>("draft").Should().Be("還沒送");
        r.A.Messages("conversationMoved", markA).Should().ContainSingle();
    }

    [Fact]
    public void 沒有人接住_游標在視窗外_在游標的位置開新視窗_新視窗就緒後搬進去()
    {
        using var r = new Rig();
        var h = r.H;
        var id = h.NewConversation(r.A, h.Claude);
        r.Windows.Drop = new TabDropTarget(TabDropKind.NewWindow);
        h.Command(r.A, "tabDrag", new { conversationId = id, text = "帶過去" });
        var markA = r.A.Mark();

        h.Command(r.A, "tabDragEnd", new { conversationId = id, dropped = false });

        var opened = r.Windows.Opened.Should().ContainSingle().Subject;
        opened.AtCursor.Should().BeTrue();
        opened.Source.Should().Be(r.A.Id);
        r.A.Messages("conversationMoved", markA).Should().BeEmpty("新視窗還沒就緒前分頁留在原本的視窗");

        // 宿主開好視窗，網頁就緒
        var c = r.Attach("wc", opened.Key, primary: false);
        h.Command(c, "ready");

        var all = c.All();
        var init = all.Single(m => m.Value<string>("type") == "init");
        init["movedIn"]!.Values<string>().Should().Equal(id);
        init.Value<string>("windowRole").Should().Be("extra");
        var initAt = all.ToList().IndexOf(init);
        var convAt = all.ToList().FindIndex(m => m.Value<string>("type") == "conversation" && m.Value<string>("conversationId") == id);
        convAt.Should().BeGreaterThan(initAt, "對話在 init 之後送（同 restoredDrafts）");
        all[convAt].Value<string>("draft").Should().Be("帶過去");
        r.A.Messages("conversationMoved", markA).Should().ContainSingle();
        h.Conversation(id).Should().NotBeNull();

        // 重新載入不會再搬一次
        var mark = c.Mark();
        h.Command(c, "ready");
        c.Messages("init", mark).Single()["movedIn"]!.Should().BeEmpty();
    }

    [Fact]
    public void 按Esc或還在原本的視窗_不處理()
    {
        using var r = new Rig();
        var h = r.H;
        var (id, _) = h.StartConversation(r.A, h.Claude);
        r.Windows.Drop = new TabDropTarget(TabDropKind.None);
        var markA = r.A.Mark();
        var markB = r.B.Mark();

        h.Command(r.A, "tabDrag", new { conversationId = id, text = "" });
        h.Command(r.A, "tabDragEnd", new { conversationId = id, dropped = false });

        r.Windows.Opened.Should().BeEmpty();
        r.A.Messages("conversationMoved", markA).Should().BeEmpty();
        r.B.Messages("conversation", markB).Should().BeEmpty();
    }

    [Fact]
    public void 移到新視窗_開新視窗_就緒後搬進去_開不了就不留著等()
    {
        using var r = new Rig();
        var h = r.H;
        var (id, _) = h.StartConversation(r.A, h.Claude);

        h.Command(r.A, "moveToNewWindow", new { conversationId = id, text = "x" });
        var opened = r.Windows.Opened.Should().ContainSingle().Subject;
        opened.AtCursor.Should().BeFalse("從選單開的視窗從來源錯開，不跟著游標");
        var c = r.Attach("wc", opened.Key, primary: false);
        var markA = r.A.Mark();
        h.Command(c, "ready");
        c.Last("conversation", id).Should().NotBeNull();
        r.A.Messages("conversationMoved", markA).Should().ContainSingle();

        r.Windows.OpenResult = false;
        h.Command(c, "moveToNewWindow", new { conversationId = id, text = "" });
        var failedKey = r.Windows.Opened[^1].Key;
        var d = r.Attach("wd", failedKey, primary: false);
        h.Command(d, "ready");
        d.Last("init")!["movedIn"]!.Should().BeEmpty("宿主開不了視窗時不留著等");
    }

    [Fact]
    public void 各視窗的版面分開存_主視窗存Layout_另外開的視窗存Windows()
    {
        using var r = new Rig();
        var h = r.H;
        var primaryLayout = new { panes = new[] { new { id = "p1", tabs = new[] { "c1" }, active = "c1" } }, focusedPane = "p1" };
        var extraLayout = new { panes = new[] { new { id = "p9", tabs = new[] { "c2" }, active = "c2" } }, focusedPane = "p9" };

        h.Command(r.A, "layout", new { layout = primaryLayout });
        h.Command(r.B, "layout", new { layout = extraLayout });

        h.Settings.Layout!["focusedPane"]!.ToString().Should().Be("p1");
        h.Settings.FindWindow("key-b")!.Layout!["focusedPane"]!.ToString().Should().Be("p9");

        h.Command(r.B, "ready");
        r.B.Last("init")!["layout"]!["focusedPane"]!.ToString().Should().Be("p9");
        h.Command(r.A, "ready");
        var init = r.A.Last("init")!;
        init["layout"]!["focusedPane"]!.ToString().Should().Be("p1");
        init.Value<string>("windowRole").Should().Be("primary");

        // 設定已被移除的視窗（使用者關掉了）送來的版面不存
        h.Settings.Windows.Clear();
        h.Command(r.B, "layout", new { layout = primaryLayout });
        h.Settings.Windows.Should().BeEmpty();
    }

    [Fact]
    public void 開新視窗與關掉自己_只有另外開的視窗能關()
    {
        using var r = new Rig();
        var h = r.H;

        h.Command(r.A, "newWindow");
        var opened = r.Windows.Opened.Should().ContainSingle().Subject;
        opened.Source.Should().Be(r.A.Id);
        opened.AtCursor.Should().BeFalse();

        h.Command(r.A, "closeWindow");
        r.Windows.Closed.Should().BeEmpty("主視窗不會因為搬空而關掉");
        h.Command(r.B, "closeWindow");
        r.Windows.Closed.Should().Equal(r.B.Id);
    }

    [Fact]
    public void 彈出視窗與一般檢視不能搬分頁_init的windowRole是null或primary()
    {
        using var r = new Rig();
        var h = r.H;
        var (id, _) = h.StartConversation(r.A, h.Claude);
        var popout = h.AddPopout("pop", id);
        var markA = r.A.Mark();

        h.Command(popout, "tabMoveIn", new { conversationId = id, paneId = "p1" });
        h.Command(popout, "newWindow");

        r.A.Messages("conversationMoved", markA).Should().BeEmpty();
        r.Windows.Opened.Should().BeEmpty();

        h.Command(popout, "ready", new { mode = "popout", conversationId = id });
        popout.Last("init")!.Value<string>("windowRole").Should().BeNull();
        h.Command(h.Main, "ready");
        h.Main.Last("init")!.Value<string>("windowRole").Should().Be("primary", "不是 IWindowView 的一般檢視當成主視窗（多視窗之前只有它）");
    }

    [Fact]
    public void 沒接上宿主時_拖到視窗外不處理_視窗之間照樣搬()
    {
        using var h = new HubHarness();
        h.Settings.Windows.Add(new AppWindowState { Key = "k" });
        var a = new FakeWindowView("wa", WindowRules.PrimaryKey, true);
        var b = new FakeWindowView("wb", "k", false);
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachView(a);
            h.Hub.AttachView(b);
        });
        var (id, _) = h.StartConversation(a, h.Claude);
        var markA = a.Mark();

        h.Command(a, "tabDrag", new { conversationId = id, text = "" });
        h.Command(a, "tabDragEnd", new { conversationId = id, dropped = false });
        a.Messages("conversationMoved", markA).Should().BeEmpty();

        h.Command(b, "tabMoveIn", new { conversationId = id, paneId = "p1" });
        a.Messages("conversationMoved", markA).Should().ContainSingle();
        b.Last("conversation", id).Should().NotBeNull();
    }
    // ───── 視窗外框（WindowBorderRules，第七批視覺效果）─────

    private static async Task<WindowBorder> BorderOfAsync(Rig r, string viewId, Func<WindowBorder, bool> want, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var last = r.H.OnUi(() => r.Windows.Borders.Count == 0 ? (WindowBorder?)null : r.Windows.Borders[^1].TryGetValue(viewId, out var b) ? b : null);
            if (last is { } got && want(got))
                return got;
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException($"等不到外框：{because}（最後是 {last?.ToString() ?? "沒有"}）");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task 視窗外框_執行中是帳戶色_等你回應是琥珀色優先_結束後回到主題_各視窗看自己開著的對話()
    {
        using var r = new Rig();
        var h = r.H;
        var (id, s) = h.StartConversation(r.A, h.Claude, "跑一輪");

        var running = await BorderOfAsync(r, r.A.Id, b => b.State == WindowBorderState.Running, "主視窗的對話執行中");
        running.AccountColor.Should().Be("#D97757", "執行中用那個對話的帳戶色");
        h.OnUi(() => r.Windows.Borders[^1][r.B.Id]).Should().Be(WindowBorder.None, "另外開的視窗沒開著這個對話");

        h.Raise(s, new AgentQuestionEvent("q-1", new[] { new AgentQuestion { Id = "q", Text = "要嗎？" } }, AnswerAsMessage: false));
        await BorderOfAsync(r, r.A.Id, b => b.State == WindowBorderState.Waiting, "有提問等你回應時立刻變琥珀色，不等 30 秒的維護");

        // 略過提問：這一輪還在跑，回到帳戶色；結束之後回到主題
        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);
        h.Command(r.A, "skipQuestion", new { conversationId = id, itemId = question.Id });
        await BorderOfAsync(r, r.A.Id, b => b.State == WindowBorderState.Running, "提問略過了、這一輪還在跑");
        h.CompleteTurn(s);
        await BorderOfAsync(r, r.A.Id, b => b.State == WindowBorderState.None, "沒有等你回應、也沒在執行");
        var sent = h.OnUi(() => r.Windows.Borders.Count);
        h.OnUi(() => { h.Hub.RunMaintenance(); return 0; });
        h.OnUi(() => r.Windows.Borders.Count).Should().Be(sent, "外框沒變就不再送");

        // 搬到另外開的視窗：外框跟著對話走
        var (id2, s2) = h.StartConversation(r.A, h.Codex, "再跑一輪");
        await BorderOfAsync(r, r.A.Id, b => b.State == WindowBorderState.Running && b.AccountColor == "#19C3A6", "Codex 的對話執行中");
        h.Command(r.B, "tabMoveIn", new { conversationId = id2, paneId = "p1" });
        await BorderOfAsync(r, r.B.Id, b => b.State == WindowBorderState.Running && b.AccountColor == "#19C3A6", "搬過去的視窗變成帳戶色");
        await BorderOfAsync(r, r.A.Id, b => b.State == WindowBorderState.None, "原本的視窗回到主題");
        h.CompleteTurn(s2);
        await BorderOfAsync(r, r.B.Id, b => b.State == WindowBorderState.None, "結束後回到主題");
    }
}
