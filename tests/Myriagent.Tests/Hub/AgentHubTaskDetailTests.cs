using Myriagent.Agents;
using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 背景工作的「詳細」（taskDetail）：標頭的 background.tasks 帶 key，問的結果（在做什麼、代理的說明、指令、輸出）只回給問的視窗；
/// Claude 的輸出問會話、Codex 的用工具卡收到的、其他寫看不到的原因；不是目前回報的背景工作回「已經結束了」
/// </summary>
public class AgentHubTaskDetailTests
{
    [Fact]
    public async Task Claude_在做什麼與代理的說明_指令_輸出問會話_只回給問的視窗()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        var other = h.AddView("w2");
        session.HasLiveBackgroundWork = true;
        var ping = new AgentBackgroundTool("執行指令", "bash", "ping -n 30 127.0.0.1", "Wait for the server\n\nping -n 30 127.0.0.1", TaskId: "b1", StopId: "b1");
        var agent = new AgentBackgroundTool("子代理", "agent", "整理報告", "把 docs 裡的報告整理成一頁", StopId: "a1");
        h.Raise(session, new AgentBackgroundWorkEvent(2, [ping, agent]));
        h.CompleteTurn(session);
        var header = await h.Main.WaitForAsync("header", m => m["header"]?["background"] is JObject, conversationId: id);
        var tasks = header["header"]!["background"]!["tasks"]!.Cast<JObject>().ToList();
        tasks.Select(t => t.Value<string>("key")).Should().Equal(BackgroundTaskDetailRules.Key(ping), BackgroundTaskDetailRules.Key(agent));

        session.TaskOutput = new AgentTaskOutput("Reply from 127.0.0.1", 20, false, null);
        h.Command(h.Main, "taskDetail", new { conversationId = id, key = tasks[0].Value<string>("key"), requestId = "td1" });
        var reply = await h.Main.WaitForAsync("taskDetail", m => m.Value<string>("requestId") == "td1");
        reply.Value<string>("tool").Should().Be("執行指令");
        reply.Value<string>("explain").Should().Contain("等待");
        reply.Value<string>("agentNote").Should().Be("Wait for the server", "代理自己寫的說明原文照登");
        reply.Value<string>("content").Should().Be("ping -n 30 127.0.0.1");
        reply.Value<string>("contentLabel").Should().Be("指令");
        reply.Value<string>("output").Should().Be("Reply from 127.0.0.1");
        reply.Value<long>("totalBytes").Should().Be(20);
        reply.Value<string>("error").Should().BeNull();
        other.Messages("taskDetail").Should().BeEmpty();
        session.TaskOutputRequests.All.Should().Equal("b1");

        h.Command(h.Main, "taskDetail", new { conversationId = id, key = tasks[1].Value<string>("key"), requestId = "td2" });
        var sub = await h.Main.WaitForAsync("taskDetail", m => m.Value<string>("requestId") == "td2");
        sub.Value<string>("explain").Should().Contain("整理報告");
        sub.Value<string>("content").Should().Be("把 docs 裡的報告整理成一頁");
        sub.Value<string>("contentLabel").Should().Be("交代給子代理的工作");
        sub["output"].Should().BeNull("子代理沒有 taskId：不問會話");
        sub.Value<string>("outputNote").Should().Contain("子代理");
        session.TaskOutputRequests.All.Should().Equal("b1");

        h.Command(h.Main, "taskDetail", new { conversationId = id, key = "tzzz", requestId = "td3" });
        var gone = await h.Main.WaitForAsync("taskDetail", m => m.Value<string>("requestId") == "td3");
        gone.Value<string>("error").Should().Be("這個背景工作已經結束了");
        gone["explain"].Should().BeNull();
    }

    [Fact]
    public async Task Codex_輸出用工具卡收到的_一輪結束後的輸出也接在卡片上()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "開開發伺服器");
        h.Raise(s, new AgentToolStartedEvent("dev1", "執行指令", "bash", "npm run dev", "npm run dev -- --port 5173"));
        h.Raise(s, new AgentToolOutputEvent("dev1", "VITE ready\n"));
        s.HasLiveBackgroundWork = true;
        var dev = new AgentBackgroundTool("執行指令", "bash", "npm run dev", "npm run dev -- --port 5173", StopId: "dev1");
        h.Raise(s, new AgentBackgroundWorkEvent(1, [dev], ["dev1"]));
        h.CompleteTurn(s);
        h.Raise(s, new AgentToolOutputEvent("dev1", "GET / 200\n"));

        h.Command(h.Main, "taskDetail", new { conversationId = id, key = BackgroundTaskDetailRules.Key(dev), requestId = "td1" });
        var reply = await h.Main.WaitForAsync("taskDetail", m => m.Value<string>("requestId") == "td1");
        reply.Value<string>("explain").Should().Contain("開發伺服器");
        reply.Value<string>("agentNote").Should().BeNull("Codex 的指令沒有代理寫的說明");
        reply.Value<string>("content").Should().Be("npm run dev -- --port 5173");
        reply.Value<string>("output").Should().Be("VITE ready\nGET / 200\n", "這一輪結束後才到的輸出也在卡片上");
        h.Items(id).Single(i => i.Kind == AgentItemKind.Tool).Output.Should().Be("VITE ready\nGET / 200\n");
    }

    [Fact]
    public async Task Grok_看不到輸出時寫原因()
    {
        using var h = new HubHarness();
        var grok = new Myriagent.Accounts.AgentAccount { Id = "acc-grok", Provider = AgentProvider.Grok, Label = "Grok 帳戶", Color = "#A3B83A", ConfigDirectory = h.Temp.Combine("accounts", "acc-grok", "grok") };
        h.Ui.Invoke(() => h.Accounts.Seed(new[] { h.Claude, h.Codex, grok }, Myriagent.Accounts.AccountStatus.Ready));
        var (id, s) = h.StartConversation(h.Main, grok);
        s.HasLiveBackgroundWork = true;
        var sleep = new AgentBackgroundTool("背景工作", "bash", "sleep 30", null);
        h.Raise(s, new AgentBackgroundWorkEvent(1, [sleep]));
        h.CompleteTurn(s);

        h.Command(h.Main, "taskDetail", new { conversationId = id, key = BackgroundTaskDetailRules.Key(sleep), requestId = "td1" });
        var reply = await h.Main.WaitForAsync("taskDetail", m => m.Value<string>("requestId") == "td1");
        reply.Value<string>("content").Should().Be("sleep 30", "沒有說明時用一行摘要");
        reply["output"].Should().BeNull();
        reply.Value<string>("outputNote").Should().Be("Grok 看不到背景工作執行中的輸出。");
    }
}
