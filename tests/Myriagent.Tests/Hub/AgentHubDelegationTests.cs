using System.IO.Pipes;
using System.Text;
using Myriagent.Agents;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

public sealed class AgentHubDelegationTests
{
    private const string Mention = "@[Codex 帳戶](account:acc-codex)";

    [Fact]
    public async Task Claude委派Codex使用目標帳戶隔離與相同工作資料夾並回傳結果()
    {
        using var h = new HubHarness();
        var (parent, main) = h.StartConversation(h.Main, h.Claude, Mention + " 請檢查測試");
        var pipe = main.Sent.All.Single().Settings.DelegationMcpPipeName!;
        var initialized = await RpcAsync(pipe, "initialize");
        initialized["capabilities"]!["tools"].Should().NotBeNull();
        var list = ToolValue(await ToolAsync(pipe, "list_accounts"));
        list["accounts"]!.OfType<JObject>().Select(a => a.Value<string>("id")).Should().Equal(h.Codex.Id);
        var result = ToolAsync(pipe, "delegate_task", new JObject { ["accountId"] = h.Codex.Id, ["prompt"] = "檢查工作資料夾並回報" });
        await h.WaitUntilAsync(() => h.Sessions.Count == 2, "Codex 子對話已啟動");
        var child = h.Sessions[1];
        var sent = child.Sent.All.Single();
        child.Provider.Should().Be(AgentProvider.Codex);
        sent.Settings.WorkingDirectory.Should().Be(main.Sent.All.Single().Settings.WorkingDirectory);
        sent.Settings.PermissionMode.Should().Be(AgentPermissionMode.Ask);
        sent.Settings.DelegationMcpPipeName.Should().BeNull("子對話不能遞迴委派");
        var env = new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "不可以繼承" };
        sent.Settings.ConfigureEnvironment(env);
        env["CODEX_HOME"].Should().Be(h.Codex.ConfigDirectory);
        env.Should().NotContainKey("OPENAI_API_KEY");
        result.IsCompleted.Should().BeFalse("完成前不能回傳成功");
        h.Raise(child, new AgentTextCompletedEvent("answer", "測試通過，已完成檢查"));
        h.CompleteTurn(child);
        var answer = ToolValue(await result.WaitAsync(TimeSpan.FromSeconds(10)));
        answer.Value<string>("status").Should().Be("Completed");
        answer.Value<string>("result").Should().Contain("測試通過");
        var childId = answer.Value<string>("conversationId")!;
        h.Conversation(childId)!.ParentConversationId.Should().Be(parent);
        // 對話清單：子對話帶主對話的 id（側欄掛在主對話底下、委派工具卡以它找到子對話），主對話不帶
        var listed = await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(c => c.Value<string>("id") == childId));
        listed["items"]!.Single(c => c.Value<string>("id") == childId).Value<string>("parentId").Should().Be(parent);
        ((JObject)listed["items"]!.Single(c => c.Value<string>("id") == parent)).ContainsKey("parentId").Should().BeFalse();
        h.Settings.LastAccountId.Should().Be(h.Claude.Id, "子工作不改使用者上次用的帳戶");
        h.IsRunning(parent).Should().BeTrue("主 AI 拿到結果後繼續自己的這一輪");
    }

    [Fact]
    public async Task 未被提及的帳戶不能委派且沒有子對話()
    {
        using var h = new HubHarness();
        var (_, main) = h.StartConversation(h.Main, h.Claude, Mention + " 幫忙檢查");
        var reply = await ToolAsync(main.Sent.All.Single().Settings.DelegationMcpPipeName!, "delegate_task",
            new JObject { ["accountId"] = h.Claude2.Id, ["prompt"] = "修改檔案" });
        reply.Value<bool>("isError").Should().BeTrue();
        ToolValue(reply).Value<string>("error").Should().Contain("先在主對話");
        h.Sessions.Should().HaveCount(1);
    }

    [Fact]
    public void 沒有提及過帳戶的對話不掛MCP_第一次提及起才帶並附上對象()
    {
        using var h = new HubHarness();
        var (id, main) = h.StartConversation(h.Main, h.Claude, "一般問題");
        main.Sent.All.Single().Settings.DelegationMcpPipeName.Should().BeNull("沒用到委派的對話不多開橋接程序");
        main.Sent.All.Single().Input.Text.Should().NotContain("myriagent_account_delegation");
        h.CompleteTurn(main);

        h.Send(h.Main, id, Mention + " 請檢查測試");
        var second = main.Sent.All[^1];
        second.Settings.DelegationMcpPipeName.Should().NotBeNull();
        second.Input.Text.Should().Contain("accountId：acc-codex");
        h.Conversation(id)!.DelegatedAccountIds.Should().Equal("acc-codex");
        h.CompleteTurn(main);

        h.Send(h.Main, id, "繼續");
        var third = main.Sent.All[^1];
        third.Settings.DelegationMcpPipeName.Should().Be(second.Settings.DelegationMcpPipeName, "同一個中樞期間管道名稱不變，程序不必再重開");
        third.Input.Text.Should().Contain("只有使用者要求").And.NotContain("accountId：");
    }

    [Fact]
    public void 執行中送出提及帳戶的訊息不插進這一輪_排隊到這一輪結束才掛上MCP送出()
    {
        using var h = new HubHarness();
        var (id, main) = h.StartConversation(h.Main, h.Claude, "整理報表");
        main.CanSteer = true;

        h.Send(h.Main, id, Mention + " 請檢查測試");

        main.SteerAttempts.Should().Be(0, "要在回合之間重開程序掛上 MCP，不能插進執行中的一輪");
        h.Queue(id).Should().Equal(Mention + " 請檢查測試");

        h.CompleteTurn(main);

        main.Sent.Count.Should().Be(2);
        main.Sent.All[^1].Settings.DelegationMcpPipeName.Should().NotBeNull();
        main.Sent.All[^1].Input.Text.Should().Contain("accountId：acc-codex");
        h.Queue(id).Should().BeEmpty();
    }

    [Fact]
    public async Task 子對話的工作說明含提及格式也照常執行()
    {
        using var h = new HubHarness();
        var (_, main) = h.StartConversation(h.Main, h.Claude, Mention + " 幫忙檢查");
        var pending = ToolAsync(main.Sent.All.Single().Settings.DelegationMcpPipeName!, "delegate_task",
            new JObject { ["accountId"] = h.Codex.Id, ["prompt"] = "使用者寫的是 " + Mention + "，請檢查" });
        await h.WaitUntilAsync(() => h.Sessions.Count == 2 && h.Sessions[1].Sent.All.Count == 1, "子工作已送出");
        h.Sessions[1].Sent.All.Single().Input.Text.Should().NotContain("myriagent_account_delegation");
        h.CompleteTurn(h.Sessions[1]);
        ToolValue(await pending.WaitAsync(TimeSpan.FromSeconds(10))).Value<string>("status").Should().Be("Completed");
    }

    [Fact]
    public async Task 停止主對話會停止子工作並結束等待()
    {
        using var h = new HubHarness();
        var (id, main) = h.StartConversation(h.Main, h.Claude, Mention + " 幫忙檢查");
        var pending = ToolAsync(main.Sent.All.Single().Settings.DelegationMcpPipeName!, "delegate_task",
            new JObject { ["accountId"] = h.Codex.Id, ["prompt"] = "持續工作" });
        await h.WaitUntilAsync(() => h.Sessions.Count == 2, "子工作已開始");
        h.Command(h.Main, "stop", new { conversationId = id });
        var reply = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        reply.Value<bool>("isError").Should().BeTrue();
        h.Sessions[1].Interrupts.Should().Be(1);
    }

    [Fact]
    public async Task 連線取消會停止子工作且失敗不會當成功()
    {
        using var h = new HubHarness();
        var (_, main) = h.StartConversation(h.Main, h.Claude, Mention + " 幫忙檢查");
        using var cancelled = new CancellationTokenSource();
        var pending = ToolAsync(main.Sent.All.Single().Settings.DelegationMcpPipeName!, "delegate_task",
            new JObject { ["accountId"] = h.Codex.Id, ["prompt"] = "持續工作" }, cancelled.Token);
        await h.WaitUntilAsync(() => h.Sessions.Count == 2, "子工作已開始");
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await h.WaitUntilAsync(() => h.Sessions[1].Interrupts == 1, "關閉工具連線會停止子工作");
    }

    [Fact]
    public async Task 規劃模式不會藉委派執行且精簡模式不注入MCP()
    {
        using var h = new HubHarness();
        var (id, main) = h.StartConversation(h.Main, h.Claude, Mention + " 幫忙檢查");
        h.Ui.Invoke(() => h.Hub.GetConversation(id)!.PermissionMode = AgentPermissionMode.Plan);
        var reply = await ToolAsync(main.Sent.All.Single().Settings.DelegationMcpPipeName!, "delegate_task",
            new JObject { ["accountId"] = h.Codex.Id, ["prompt"] = "修改檔案" });
        reply.Value<bool>("isError").Should().BeTrue();
        h.Sessions.Should().HaveCount(1);
        h.Ui.Invoke(() => h.Claude2.SafeMode = true);
        var (_, safe) = h.StartConversation(h.Main, h.Claude2);
        safe.Sent.All.Single().Settings.DelegationMcpPipeName.Should().BeNull();
    }

    internal static JObject ToolValue(JObject reply) => JObject.Parse(reply["content"]![0]!.Value<string>("text")!);

    internal static Task<JObject> ToolAsync(string pipe, string name, JObject? args = null, CancellationToken ct = default)
        => RpcAsync(pipe, "tools/call", new JObject { ["name"] = name, ["arguments"] = args ?? new JObject() }, ct);

    internal static async Task<JObject> RpcAsync(string pipeName, string method, JObject? parameters = null, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000, timeout.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters }.ToString(Formatting.None).AsMemory(), timeout.Token);
        var response = JObject.Parse((await reader.ReadLineAsync(timeout.Token))!);
        response["error"].Should().BeNull();
        return (JObject)response["result"]!;
    }
}
