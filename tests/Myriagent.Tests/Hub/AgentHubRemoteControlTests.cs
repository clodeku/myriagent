using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// Claude Remote Control 的中樞流程：送出時帶的選項（對話標題、上次的遠端工作階段）、記下遠端工作階段、
/// 設定頁切換立即套用到活著的程序、換帳戶不接回、開著的程序閒置不回收
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteControlTests
{
    [Fact]
    public async Task 預設開啟_送出時帶對話標題_記下遠端工作階段後下次送出接回且存進對話資料()
    {
        using var h = new HubHarness();
        h.OnUi(() => h.Settings.ClaudeRemoteControl).Should().BeTrue("預設開");
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理上週的報表");
        s.Sent.All.Single().Settings.RemoteControl.Should().Be(new AgentRemoteControlOptions("整理上週的報表", null), "第一次沒有可接回的工作階段");

        h.Raise(s, new AgentRemoteControlEvent("cse_abc", "https://claude.ai/code/session_cse_abc"));

        h.Conversation(id)!.RemoteControlSessionId.Should().Be("cse_abc");
        new ConversationStore(h.Store.RootDirectory).LoadConversations().Single(m => m.Id == id).RemoteControlSessionId
            .Should().Be("cse_abc", "存進 meta.json：app 重開後也接回同一個遠端工作階段");

        h.CompleteTurn(s);
        h.Send(h.Main, id, "第二則");
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "第二則送出");
        s.Sent.All[1].Settings.RemoteControl.Should().Be(new AgentRemoteControlOptions("整理上週的報表", "cse_abc"));
    }

    [Fact]
    public async Task 設定關閉後送出不帶選項_設定值會送給網頁()
    {
        using var h = new HubHarness();
        var mark = h.Main.Mark();
        h.Command(h.Main, "setting", new { key = "claudeRemoteControl", value = false });

        h.OnUi(() => h.Settings.ClaudeRemoteControl).Should().BeFalse();
        var settings = await h.Main.WaitForAsync("settings", since: mark);
        settings["settings"]!.Value<bool>("claudeRemoteControl").Should().BeFalse();

        var (_, s) = h.StartConversation(h.Main, h.Claude, "不要遠端");
        s.Sent.All.Single().Settings.RemoteControl.Should().BeNull();
    }

    [Fact]
    public void 設定切換_通知所有會話_值沒變不重送()
    {
        using var h = new HubHarness();
        var (liveId, live) = h.StartConversation(h.Main, h.Claude, "活著的");
        h.Raise(live, new AgentRemoteControlEvent("cse_live", null));
        h.CompleteTurn(live);
        var (_, codex) = h.StartConversation(h.Main, h.Codex, "Codex 的");
        h.CompleteTurn(codex);
        var (_, gone) = h.StartConversation(h.Main, h.Claude, "程序已結束");
        h.CompleteTurn(gone);
        gone.LiveProcess = false;

        h.Command(h.Main, "setting", new { key = "claudeRemoteControl", value = false });

        live.RemoteControlChanges.All.Should().Equal(new AgentRemoteControlOptions?[] { null });
        codex.RemoteControlChanges.All.Should().Equal(new AgentRemoteControlOptions?[] { null }, "不支援的會話自己忽略");
        gone.RemoteControlChanges.All.Should().Equal(new AgentRemoteControlOptions?[] { null }, "沒有程序（或正在啟動、重開）：會話記下，接下來啟動的程序照新設定");

        h.Command(h.Main, "setting", new { key = "claudeRemoteControl", value = true });
        h.Command(h.Main, "setting", new { key = "claudeRemoteControl", value = true });

        live.RemoteControlChanges.All.Should().HaveCount(2, "值沒變不重送");
        live.RemoteControlChanges.All[1].Should().Be(new AgentRemoteControlOptions("活著的", "cse_live"), "再開啟時接回同一個遠端工作階段");
        h.Conversation(liveId)!.RemoteControlSessionId.Should().Be("cse_live");
    }

    [Fact]
    public async Task 換帳戶_清掉遠端工作階段_新帳戶開新的()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "換帳戶的對話");
        h.Raise(s, new AgentRemoteControlEvent("cse_old", null));
        h.CompleteTurn(s);

        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id });

        h.Conversation(id)!.RemoteControlSessionId.Should().BeNull("遠端工作階段屬於原帳戶");
        h.Send(h.Main, id, "用新帳戶繼續");
        await h.WaitUntilAsync(() => h.Sessions.Count == 2 && h.Sessions[1].Sent.Count == 1, "新帳戶的會話送出");
        h.Sessions[1].Sent.All.Single().Settings.RemoteControl.Should().Be(new AgentRemoteControlOptions("換帳戶的對話", null));
    }

    [Fact]
    public void 閒置回收_開著RemoteControl的程序閒置2小時才關_斷線或關掉後照常回收()
    {
        using var h = new HubHarness();
        var (_, remote) = h.StartConversation(h.Main, h.Claude, "從手機接手");
        h.CompleteTurn(remote);
        remote.HasRemoteControl = true;
        var (_, plain) = h.StartConversation(h.Main, h.Claude, "一般的");
        h.CompleteTurn(plain);
        var (_, dropped) = h.StartConversation(h.Main, h.Claude, "遠端斷線的");
        h.CompleteTurn(dropped);
        dropped.HasRemoteControl = true;
        var soon = DateTime.Now + AgentHubRules.IdleProcessTimeout + TimeSpan.FromMinutes(5);

        h.Ui.Invoke(() => h.Hub.RunMaintenance(soon));

        plain.Closes.Should().Be(1, "閒置太久照常回收");
        remote.Closes.Should().Be(0, "2 小時內隨時能從 claude.ai 或 Claude App 接手");
        dropped.Closes.Should().Be(0);

        dropped.HasRemoteControl = false;
        h.Ui.Invoke(() => h.Hub.RunMaintenance(soon));
        dropped.Closes.Should().Be(1, "不再開著（關掉設定或斷線）就照一般程序回收，下次送出重開時再開");

        h.Ui.Invoke(() => h.Hub.RunMaintenance(DateTime.Now + AgentHubRules.RemoteControlIdleTimeout + TimeSpan.FromMinutes(1)));
        remote.Closes.Should().Be(1, "閒置滿 2 小時也關（以前一律不關，一天下來掛著三十多個、約 16 GB）");
    }

    [Fact]
    public void 開不起來的提示_每個帳戶只在第一個對話顯示()
    {
        using var h = new HubHarness();
        var (first, s1) = h.StartConversation(h.Main, h.Claude, "第一個");
        var (second, s2) = h.StartConversation(h.Main, h.Claude, "第二個");
        var (other, s3) = h.StartConversation(h.Main, h.Claude2, "另一個帳戶");

        foreach (var s in new[] { s1, s2, s3 })
            h.Raise(s, new AgentRemoteControlFailedEvent("Remote Control 沒有開啟（not enabled）"));

        Notices(h, first).Should().ContainSingle();
        Notices(h, second).Should().BeEmpty("同一個帳戶已提示過：帳戶不支援時不要每個對話都多一則");
        Notices(h, other).Should().ContainSingle("另一個帳戶照樣提示");
    }

    [Fact]
    public async Task 遠端訊息_閒置時送來_加成標示來源的使用者訊息並存檔_對話標成執行中_app送出的排隊到這一輪結束()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        h.CompleteTurn(s);
        var mark = h.Main.Mark();

        h.Raise(s, new AgentRemoteUserMessageEvent("做好了嗎？", 1, 0, StartsTurn: true));

        var item = h.Items(id).Last();
        item.Kind.Should().Be(AgentItemKind.User);
        item.Source.Should().Be(AgentItemSource.Remote);
        item.Text.Should().Be("做好了嗎？\n\n（附 1 張圖片，請在 claude.ai 或 Claude App 查看）");
        (await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("source") == "remote", mark, conversationId: id))["item"]!
            .Value<string>("kind").Should().Be("User", "網頁照 source 標示從 claude.ai 或 Claude App 送出");
        new ConversationStore(h.Store.RootDirectory).LoadItems(id).Last().Source.Should().Be(AgentItemSource.Remote, "存檔：重開 app 也看得到來源");
        h.OnUi(() => h.Hub.RunningCount).Should().Be(1, "遠端開始的一輪也算執行中");
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("running"), mark, conversationId: id);

        h.Send(h.Main, id, "桌面這邊也問一下");
        s.Sent.Count.Should().Be(1, "遠端那一輪還在跑：排進佇列，不直接送");
        h.Queue(id).Should().Equal("桌面這邊也問一下");

        h.CompleteTurn(s);
        s.Sent.Count.Should().Be(2, "遠端那一輪結束後接著送");
        s.Sent.All[1].Input.Text.Should().Be("桌面這邊也問一下");
    }

    [Fact]
    public void 遠端訊息_開始的一輪可以從app停止()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        h.CompleteTurn(s);
        h.Raise(s, new AgentRemoteUserMessageEvent("跑一下全部測試", 0, 0, StartsTurn: true));

        h.Command(h.Main, "stop", new { conversationId = id });

        s.Interrupts.Should().Be(1);
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);
        h.OnUi(() => h.Hub.RunningCount).Should().Be(0);
        h.Items(id).Last().Text.Should().Be("已停止這一輪");
    }

    [Fact]
    public void 遠端訊息_在手機上按停止_顯示已停止_不是錯誤_排隊的訊息暫停()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        h.CompleteTurn(s);
        h.Raise(s, new AgentRemoteUserMessageEvent("跑一下全部測試", 0, 0, StartsTurn: true));
        h.Send(h.Main, id, "桌面這邊排著的");

        // 會話把 CLI 插進停止標記後的 error_during_execution 回報成已停止（app 沒按停止）
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        s.Interrupts.Should().Be(0);
        h.Items(id).Should().NotContain(i => i.Kind == AgentItemKind.Error);
        h.Items(id).Last().Text.Should().Be("已停止這一輪（排隊的訊息暫停，按送出繼續）");
        s.Sent.Count.Should().Be(1, "停止後排隊的訊息不自動送出");
        h.Queue(id).Should().Equal("桌面這邊排著的");
    }

    [Fact]
    public void 遠端訊息_併進app這一輪_只加訊息_這一輪照常結束()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");

        h.Raise(s, new AgentRemoteUserMessageEvent("順便看一下圖表", 0, 0, StartsTurn: false));

        h.Items(id).Where(i => i.Kind == AgentItemKind.User).Select(i => (i.Text, i.Source))
            .Should().Equal(("整理報表", (AgentItemSource?)null), ("順便看一下圖表", AgentItemSource.Remote));
        h.OnUi(() => h.Hub.RunningCount).Should().Be(1);
        h.CompleteTurn(s);
        h.OnUi(() => h.Hub.RunningCount).Should().Be(0);
    }

    private static List<AgentChatItem> Notices(HubHarness h, string id)
        => h.Items(id).Where(i => i.Kind == AgentItemKind.Notice && i.Text.Contains("Remote Control")).ToList();
}
