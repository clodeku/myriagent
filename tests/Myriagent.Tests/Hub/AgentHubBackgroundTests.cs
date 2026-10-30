using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 代理這一輪結束了、背景工作還在跑（AgentBackgroundWorkEvent，AgentHub.Background.cs）：對話仍算在進行——
/// 標頭與清單帶 background（網頁以「等背景工作回報」的樣貌顯示）、系統匣與結束確認算它、完成的通知不說「已完成」；
/// 背景工作沒了、換了會話就不算
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubBackgroundTests
{
    private static readonly AgentBackgroundTool Tests = new("執行指令", "bash", "dotnet test Myriagent.slnx\n-c Release", null);
    private static readonly AgentBackgroundTool Agent = new("子代理", "agent", "檢查文件裡的連結", null);

    /// <summary>會話回報背景工作（同真的會話：有背景工作時 HasLiveBackgroundWork 也是 true）</summary>
    private static void Background(HubHarness h, FakeSession s, int count, params AgentBackgroundTool[] tools)
    {
        s.HasLiveBackgroundWork = count > 0;
        h.Raise(s, new AgentBackgroundWorkEvent(count, tools));
    }

    [Fact]
    public async Task 這一輪結束後背景工作還在跑_標頭與清單帶background_系統匣算它_背景工作沒了就拿掉()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        Background(h, s, 2, Tests, Agent);
        var mark = h.Main.Mark();
        h.CompleteTurn(s);

        var header = (await h.Main.WaitForAsync("header", m => !m["header"]!.Value<bool>("running"), mark, conversationId: id))["header"]!;
        var background = header["background"]!;
        background.Value<int>("count").Should().Be(2);
        background["tasks"]!.Select(t => (t.Value<string>("tool"), t.Value<string>("title"))).Should().Equal(
            ("執行指令", "dotnet test Myriagent.slnx"), ("子代理", "檢查文件裡的連結"));
        var list = await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(c => c.Value<string>("id") == id && c.Value<bool>("background")), mark);
        list["items"]!.Single(c => c.Value<string>("id") == id).Value<bool>("running").Should().BeFalse("代理這一輪已結束：網頁照 background 改用等背景工作回報的樣貌");
        h.OnUi(() => h.Hub.BackgroundWaitingCount).Should().Be(1);
        h.OnUi(() => h.Hub.RunningCount).Should().Be(0, "可以照常送出（不排隊）：代理沒有在執行");
        h.Host.Tray.All.Last().Tooltip.Should().Contain("等背景工作 1");

        mark = h.Main.Mark();
        Background(h, s, 0);
        (await h.Main.WaitForAsync("header", m => m["header"]!["background"] is not Newtonsoft.Json.Linq.JObject, mark, conversationId: id))
            .Should().NotBeNull("背景工作完成：標頭的 background 改回 null，畫面不再停在等背景工作回報");
        await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(c => c.Value<string>("id") == id && !c.Value<bool>("background")), mark);
        h.OnUi(() => h.Hub.BackgroundWaitingCount).Should().Be(0);
        h.Host.Tray.All.Last().Tooltip.Should().NotContain("等背景工作");
    }

    [Fact]
    public async Task 背景工作的開始時間_標頭帶startedAt_之後的回報沿用_新開的另算()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        var before = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        Background(h, s, 1, Tests);
        var after = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var mark = h.Main.Mark();
        h.CompleteTurn(s);

        var bg = (await h.Main.WaitForAsync("header", m => !m["header"]!.Value<bool>("running") && m["header"]!["background"] is Newtonsoft.Json.Linq.JObject,
            mark, conversationId: id))["header"]!["background"]!;
        var started = bg.Value<long>("startedAt");
        started.Should().BeInRange(before, after, "Claude 的背景工作開始時就回報：從回報的時候算");
        bg["tasks"]![0]!.Value<long>("startedAt").Should().Be(started);

        await Task.Delay(30);
        mark = h.Main.Mark();
        Background(h, s, 2, Tests, Agent);
        bg = (await h.Main.WaitForAsync("header", m => m["header"]!["background"] is Newtonsoft.Json.Linq.JObject b && b.Value<int>("count") == 2,
            mark, conversationId: id))["header"]!["background"]!;
        bg.Value<long>("startedAt").Should().Be(started, "還在跑的沿用原本的開始時間；外層是最早開始的那一個");
        bg["tasks"]![0]!.Value<long>("startedAt").Should().Be(started);
        bg["tasks"]![1]!.Value<long>("startedAt").Should().BeGreaterThan(started, "新開的子代理從它回報的時候算");
    }

    [Fact]
    public void 執行中的背景工作_標頭照樣帶著_等背景工作的計數只算這一輪已結束的()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        Background(h, s, 1, Tests);

        h.IsRunning(id).Should().BeTrue();
        h.OnUi(() => h.Hub.BackgroundWaitingCount).Should().Be(0, "執行中照執行中算");
        h.CompleteTurn(s);
        h.OnUi(() => h.Hub.BackgroundWaitingCount).Should().Be(1);
    }

    [Fact]
    public void 一輪結束時背景工作還在跑_通知不說已完成_沒有背景工作才說已完成()
    {
        using var h = new HubHarness();
        h.Host.IsForeground = false;
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        Background(h, s, 1, Tests);
        h.CompleteTurn(s);

        h.Host.Notifications.All.Should().ContainSingle().Which.Title.Should().StartWith("代理已回覆，背景工作還在執行",
            "代理回一句「等測試跑完」就結束這一輪：還沒真的完成，但回覆（例如開發伺服器的網址）要看得到");

        // 背景工作完成、CLI 自己開的一輪做完：這才是完成
        Background(h, s, 0);
        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));
        h.CompleteTurn(s);
        h.Host.Notifications.All.Last().Title.Should().StartWith("代理已完成");
        h.OnUi(() => h.Hub.BackgroundWaitingCount).Should().Be(0);
    }

    [Fact]
    public void 換了會話或會話已沒有背景工作_就不算等背景工作()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        Background(h, s, 1, Tests);
        h.CompleteTurn(s);
        h.OnUi(() => h.Hub.BackgroundWaitingCount).Should().Be(1);

        // 程序被關掉、背景工作跟著沒了（會話照理會再發一次 0；沒發也不能一直顯示）
        s.HasLiveBackgroundWork = false;
        h.OnUi(() => h.Hub.BackgroundWaitingCount).Should().Be(0);
        s.HasLiveBackgroundWork = true;
        h.OnUi(() => h.Hub.BackgroundWaitingCount).Should().Be(1);

        // 換了帳戶（背景工作還在跑時要確認現在換，2026-10-27；沒確認的等背景工作結束才換）：會話換新的，舊會話的背景工作不算
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id, when = AccountSwitchRules.WhenNow });
        h.OnUi(() => h.Hub.BackgroundWaitingCount).Should().Be(0);
    }

    // ───── Codex 的背景指令：一輪結束時還在跑，跑完時 Codex 不會自己開一輪，由中樞隱藏送出接續訊息 ─────

    private static readonly AgentBackgroundTool NpmTest = new("執行指令", "bash", "npm test", "npm test");

    /// <summary>Codex 這一輪開始了 npm test、這一輪結束時它還在跑（同 CodexAgentSession：先回報背景工作再結束這一輪）</summary>
    private static (string Id, FakeSession S) CodexTurnWithBackground(HubHarness h)
    {
        var (id, s) = h.StartConversation(h.Main, h.Codex, "跑測試");
        h.Raise(s, new AgentToolStartedEvent("bg1", "執行指令", "bash", "npm test", "npm test"));
        s.HasLiveBackgroundWork = true;
        h.Raise(s, new AgentBackgroundWorkEvent(1, new[] { NpmTest }, new[] { "bg1" }));
        return (id, s);
    }

    /// <summary>背景指令跑完（同 CodexAgentSession：工具結束、背景工作變 0、再回報跑完）</summary>
    private static void Finish(HubHarness h, FakeSession s, int exitCode = 1)
    {
        h.Raise(s, new AgentToolCompletedEvent("bg1", exitCode == 0 ? AgentItemStatus.Done : AgentItemStatus.Failed, "1 failed", Array.Empty<string>()));
        s.HasLiveBackgroundWork = false;
        h.Raise(s, new AgentBackgroundWorkEvent(0, Array.Empty<AgentBackgroundTool>()));
        h.Raise(s, new AgentBackgroundCommandFinishedEvent("bg1", "npm test", exitCode == 0 ? AgentItemStatus.Done : AgentItemStatus.Failed, exitCode, TimeSpan.FromSeconds(26.9)));
    }

    private static AgentChatItem Card(HubHarness h, string id) => h.Items(id).Single(i => i.Kind == AgentItemKind.Tool && i.Title == "npm test");

    [Fact]
    public async Task Codex背景指令_開始時間是工具卡建立的時間()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "跑測試");
        h.Raise(s, new AgentToolStartedEvent("bg1", "執行指令", "bash", "npm test", "npm test"));
        await Task.Delay(50);
        var mark = h.Main.Mark();
        s.HasLiveBackgroundWork = true;
        h.Raise(s, new AgentBackgroundWorkEvent(1, new[] { NpmTest }, new[] { "bg1" }));

        var bg = (await h.Main.WaitForAsync("header", m => m["header"]!["background"] is Newtonsoft.Json.Linq.JObject, mark, conversationId: id))["header"]!["background"]!;
        bg.Value<long>("startedAt").Should().Be(new DateTimeOffset(Card(h, id).CreatedAt).ToUnixTimeMilliseconds(),
            "Codex 的指令讓出給模型（或這一輪結束）時才算背景工作：從指令開始的時候算，不是從回報的時候");
    }

    [Fact]
    public void Codex背景指令_這一輪結束時卡片留在執行中_跑完才結束_閒置時自動隱藏送出接續訊息()
    {
        using var h = new HubHarness();
        var (id, s) = CodexTurnWithBackground(h);
        h.CompleteTurn(s);

        Card(h, id).Status.Should().Be(AgentItemStatus.Running, "指令還在跑：以前這一輪結束就標成完成，畫面看不出它還在跑");
        h.OnUi(() => h.Hub.BackgroundWaitingCount).Should().Be(1);

        Finish(h, s);

        Card(h, id).Status.Should().Be(AgentItemStatus.Failed, "一輪結束後才送來的 item/completed 也找得到卡片");
        Card(h, id).Output.Should().Be("1 failed");
        s.Sent.Count.Should().Be(2, "Codex 不會自己開一輪：中樞隱藏送出接續訊息");
        var text = s.Sent.All[1].Input.Text;
        text.Should().Contain("系統通知").And.Contain("`npm test`（結束碼 1，執行了 27 秒）").And.Contain("接著完成原本的工作");
        var notice = h.Items(id).Last();
        notice.Kind.Should().Be(AgentItemKind.Notice);
        notice.NoticeKind.Should().Be(AgentNoticeKind.BackgroundTask);
        notice.Text.Should().Be("背景指令已結束：`npm test`（結束碼 1，執行了 27 秒），代理接著處理。");
        h.Items(id).Should().NotContain(i => i.Kind == AgentItemKind.User && i.Text.Contains("系統通知"), "接續訊息不顯示成使用者的訊息");
        h.IsRunning(id).Should().BeTrue();
    }

    [Fact]
    public void Codex背景指令_執行中跑完_等這一輪正常結束再接續_不通知已完成()
    {
        using var h = new HubHarness();
        h.Host.IsForeground = false;
        var (id, s) = CodexTurnWithBackground(h);
        h.CompleteTurn(s);
        h.Send(h.Main, id, "順便看一下 README");
        s.Sent.Count.Should().Be(2);

        Finish(h, s);
        s.Sent.Count.Should().Be(2, "代理正在處理使用者的訊息：不插話，等這一輪結束");

        var notified = h.Host.Notifications.Count;
        h.CompleteTurn(s);
        s.Sent.Count.Should().Be(3, "這一輪結束後接著處理跑完的背景指令");
        s.Sent.All[2].Input.Text.Should().Contain("`npm test`");
        h.Host.Notifications.Count.Should().Be(notified, "接著還有一輪：這時不通知已完成");
    }

    [Fact]
    public void Codex背景指令_執行中跑完但這一輪被停止_不接續()
    {
        using var h = new HubHarness();
        var (id, s) = CodexTurnWithBackground(h);
        h.CompleteTurn(s);
        h.Send(h.Main, id, "另一件事");
        Finish(h, s);

        h.Command(h.Main, "stop", new { conversationId = id });
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        s.Sent.Count.Should().Be(2, "使用者按了停止：不讓代理自己接著做");
        h.Items(id).Should().NotContain(i => i.NoticeKind == AgentNoticeKind.BackgroundTask);
    }

    [Fact]
    public void Codex背景指令_持續目標還沒完成時_這一輪結束不另外送_codex會自己開下一輪()
    {
        using var h = new HubHarness();
        var (id, s) = CodexTurnWithBackground(h);
        h.CompleteTurn(s);
        h.Send(h.Main, id, "繼續移植");
        Finish(h, s);

        h.Raise(s, new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, null) { ContinuesOnItsOwn = true });

        s.Sent.Count.Should().Be(2, "兩邊同時開一輪會撞在一起：codex 馬上自己開下一輪，跑完的結果已在它的上下文裡");
        h.Items(id).Should().NotContain(i => i.NoticeKind == AgentNoticeKind.BackgroundTask);
        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: false, ForThreadGoal: true));
        h.CompleteTurn(s);
        s.Sent.Count.Should().Be(2, "跑完的那個已經交給持續目標的下一輪：之後也不補送");
    }

    [Fact]
    public void Codex背景指令_排隊的訊息暫停中_不自動接續_只提示()
    {
        using var h = new HubHarness();
        var (id, s) = CodexTurnWithBackground(h);
        h.Send(h.Main, id, "排著的");
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);
        h.Queue(id).Should().Equal("排著的");

        // 停止的這一輪不留背景指令（會話照理不回報）；這裡以會話照樣回報跑完來確認中樞不會越過暫停的佇列
        h.Raise(s, new AgentBackgroundCommandFinishedEvent("bg1", "npm test", AgentItemStatus.Done, 0, null));

        s.Sent.Count.Should().Be(1);
        var notice = h.Items(id).Last();
        notice.NoticeKind.Should().Be(AgentNoticeKind.BackgroundTask);
        notice.Text.Should().Be("背景指令已結束：`npm test`（結束碼 0）。排隊的訊息暫停中，這次沒有讓代理自動接著處理，按送出繼續。");
    }

    [Fact]
    public void Codex背景指令_連續自動接續有上限_使用者送出後重新計算()
    {
        using var h = new HubHarness();
        var (id, s) = CodexTurnWithBackground(h);
        h.CompleteTurn(s);
        for (int i = 0; i < BackgroundWorkRules.MaxAutoContinues; i++)
        {
            h.Raise(s, new AgentBackgroundCommandFinishedEvent("bg" + i, "npm test", AgentItemStatus.Done, 0, null));
            h.IsRunning(id).Should().BeTrue();
            h.CompleteTurn(s);
        }
        s.Sent.Count.Should().Be(1 + BackgroundWorkRules.MaxAutoContinues);

        h.Raise(s, new AgentBackgroundCommandFinishedEvent("bgx", "npm test", AgentItemStatus.Done, 0, null));
        s.Sent.Count.Should().Be(1 + BackgroundWorkRules.MaxAutoContinues, "代理每次接續又開新的背景指令時不能無限循環");
        h.Items(id).Last().Text.Should().Contain("代理已連續自動接著處理 10 次");

        h.Send(h.Main, id, "繼續");
        h.CompleteTurn(s);
        h.Raise(s, new AgentBackgroundCommandFinishedEvent("bgy", "npm test", AgentItemStatus.Done, 0, null));
        s.Sent.Count.Should().Be(3 + BackgroundWorkRules.MaxAutoContinues, "使用者送出之後重新計算");
    }

    [Fact]
    public async Task 帳戶卡的對話數_等背景工作回報另外算_執行中的不算_背景工作沒了就歸零()
    {
        using var h = new HubHarness();
        var (_, s) = h.StartConversation(h.Main, h.Claude, "開開發伺服器");
        int Count(Newtonsoft.Json.Linq.JObject m, string key) => m["accounts"]!.Single(a => a.Value<string>("id") == h.Claude.Id)["sessions"]!.Value<int>(key);

        Background(h, s, 1, Tests);
        var mark = h.Main.Mark();
        h.Command(h.Main, "ready");   // 重送 init：代理還在執行時算在執行中，不重複算成等背景工作回報
        var init = await h.Main.WaitForAsync("init", since: mark);
        Count(init, "running").Should().Be(1);
        Count(init, "background").Should().Be(0);

        mark = h.Main.Mark();
        h.CompleteTurn(s);
        var accounts = await h.Main.WaitForAsync("accounts", m => Count(m, "background") == 1, mark);
        Count(accounts, "running").Should().Be(0);

        mark = h.Main.Mark();
        Background(h, s, 0);
        await h.Main.WaitForAsync("accounts", m => Count(m, "background") == 0, mark);
    }
}
