using Myriagent.Agents;
using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 執行中把前景的指令與子代理移到背景（AgentHub.MoveToBackground.cs，web-protocol 的 backgroundTasks 與 Header.canBackground；2026-11-11）：
/// Claude 本機對話執行中、前景有指令或子代理在跑時標頭才帶 canBackground，按了交給會話（全部）；沒有能移的、會話回原因時提示送指令的視窗。
/// 也包含同一天的另一項：Claude 按停止不砍背景工作，還有背景工作時停止的提示說明它們繼續執行
/// </summary>
public class AgentHubMoveToBackgroundTests
{
    private static Task<JObject> HeaderAsync(HubHarness h, string id, Func<JObject, bool> pred, int mark)
        => h.Main.WaitForAsync("header", m => m["header"] is JObject hd && pred(hd), mark, conversationId: id);

    [Fact]
    public async Task 前景有指令在跑才帶canBackground_按了交給會話_結束了就不帶()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "dotnet test", "dotnet test"));
        await HeaderAsync(h, id, hd => hd.Value<bool?>("canBackground") == true, mark);

        h.Command(h.Main, "backgroundTasks", new { conversationId = id });
        await h.WaitUntilAsync(() => s.BackgroundMoves.All.Count == 1, "交給會話", 5000);
        s.BackgroundMoves.All.Should().Equal("*");

        mark = h.Main.Mark();
        h.Raise(s, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "Command was manually backgrounded by user", Array.Empty<string>()));
        await HeaderAsync(h, id, hd => hd["canBackground"] == null, mark);
    }

    [Fact]
    public async Task 子代理也能移_讀檔這類工具不能()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();
        h.Raise(s, new AgentToolStartedEvent("t1", "讀取檔案", "read", "a.txt", "a.txt"));
        await HeaderAsync(h, id, hd => hd.Value<string>("status")?.Contains("讀取檔案") == true, mark);
        h.Main.Messages("header", mark, id).Should().NotContain(m => m["header"]!["canBackground"] != null);

        mark = h.Main.Mark();
        h.Raise(s, new AgentToolStartedEvent("t2", "子代理", "agent", "整理", "{}"));
        await HeaderAsync(h, id, hd => hd.Value<bool?>("canBackground") == true, mark);
    }

    [Fact]
    public async Task 沒有能移的或會話回原因_提示送指令的視窗()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();
        h.Command(h.Main, "backgroundTasks", new { conversationId = id });
        (await h.Main.WaitForAsync("toast", null, mark)).Value<string>("text").Should().Contain("沒有可以移到背景");
        s.BackgroundMoves.All.Should().BeEmpty();

        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm run build", "npm run build"));
        s.MoveToBackgroundError = "目前沒有可以移到背景的指令或子代理";
        mark = h.Main.Mark();
        h.Command(h.Main, "backgroundTasks", new { conversationId = id });
        (await h.Main.WaitForAsync("toast", null, mark)).Value<string>("text").Should().Be("沒有移到背景：目前沒有可以移到背景的指令或子代理");
    }

    [Fact]
    public async Task Codex對話不帶canBackground()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex);
        var mark = h.Main.Mark();
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", "npm test"));
        await HeaderAsync(h, id, hd => hd.Value<string>("status")?.Contains("執行指令") == true, mark);
        h.Main.Messages("header", mark, id).Should().NotContain(m => m["header"]!["canBackground"] != null);
    }

    [Fact]
    public void Claude按停止_背景工作還在跑_提示說明它們繼續執行()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        s.HasLiveBackgroundWork = true;
        h.Raise(s, new AgentBackgroundWorkEvent(2, [new AgentBackgroundTool("子代理", "agent", "整理", null, StopId: "a1")]));

        h.Command(h.Main, "stop", new { conversationId = id });
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        h.Items(id).Last().Text.Should().Be(BackgroundWorkRules.StoppedTurnNotice(false, 2));
        h.Items(id).Last().Text.Should().Contain("2 個背景工作繼續執行");
    }

    [Fact]
    public void 停止時沒有背景工作_照舊只說已停止()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "stop", new { conversationId = id });
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);
        h.Items(id).Last().Text.Should().Be("已停止這一輪");
    }
}

/// <summary>
/// 快取過期後整份重送的額度成本（2026-11-11）：Claude 的會話每一輪之後回報 coldResumeQuota，中樞記在 meta（app 重開之後照樣提醒）、
/// 閒置的標頭帶 coldResume 給網頁算百分比；Claude 沒回報（沒有訂閱方案、閒置壓縮已寫好摘要）就清掉、不帶
/// </summary>
public class AgentHubColdResumeTests
{
    [Fact]
    public async Task 回報的成本記在meta_閒置的標頭帶coldResume_沒有就清掉()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        var mark = h.Main.Mark();

        h.Raise(s, new AgentColdResumeQuotaEvent(new ColdResumeQuota(0.9165, 5)));

        var header = await h.Main.WaitForAsync("header", m => m["header"]?["coldResume"] is JObject, mark, conversationId: id);
        header["header"]!["coldResume"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"perMillion":0.9165,"minPercent":5.0}""");
        header["header"]!["idleSince"].Should().NotBeNull();
        h.Store.LoadConversations().Single(c => c.Id == id).ColdResume.Should().Be(new ColdResumeQuota(0.9165, 5), "存進 meta：app 重開之後回到閒置的對話照樣提醒");

        mark = h.Main.Mark();
        h.Raise(s, new AgentColdResumeQuotaEvent(null));
        await h.Main.WaitForAsync("header", m => m["header"] is JObject hd && hd["coldResume"] == null && hd["idleSince"] != null, mark, conversationId: id);
        h.Store.LoadConversations().Single(c => c.Id == id).ColdResume.Should().BeNull();
    }

    [Fact]
    public async Task 執行中不帶coldResume()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentColdResumeQuotaEvent(new ColdResumeQuota(40, 5)));
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "ls", "ls"));
        var header = await h.Main.WaitForAsync("header", m => m["header"]?.Value<string>("status")?.Contains("執行指令") == true, conversationId: id);
        header["header"]!["coldResume"].Should().BeNull();
        header["header"]!["idleSince"].Should().BeNull();
    }
}
