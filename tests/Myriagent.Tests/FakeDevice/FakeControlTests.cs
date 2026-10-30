using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.FakeDevice;

/// <summary>
/// 假裝置的控制通道與它的 op（WP4.9 計畫步 11；用連結編譯的 Runtime，未配對的一台、不連伺服器）：一行一個指令、回覆帶 id、不合格的照常讀下一行、
/// quit 之後不再讀；背景串流在 status 看得到、quit 時停掉；遠端回合的劇本；回收區還原；XSS 語料放進各個欄位
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class FakeControlTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static readonly FakeAccountSpec Account = new("a", AgentProvider.Claude, "Claude a", null, "a@example.test", "org-a", "max");

    private static Task<DeviceRuntime> StartAsync(string root) =>
        DeviceRuntime.StartAsync(new DeviceRuntimeOptions { DataRoot = root, Persist = false, Accounts = [Account] });

    private static List<JObject> Lines(StringWriter output) =>
        output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(JObject.Parse).ToList();

    private static Task<JObject> RunAsync(FakeControl control, string json) =>
        control.ExecuteAsync(FakeControlRules.Parse(json).Command ?? throw new InvalidOperationException(FakeControlRules.Parse(json).Error));

    [Fact]
    public async Task 一行一個指令_回覆帶id_不合格的照常讀下一行_quit之後不再讀()
    {
        using var temp = new TempDirectory("fd-control");
        await using var device = await StartAsync(temp.Combine("device"));
        await using var control = new FakeControl(device);
        var input = new StringReader(string.Join("\n",
            """{"id":1,"op":"status"}""",
            """{"id":2,"op":"bogus"}""",
            "",
            "not json",
            """{"id":"s","op":"stream","conversation":"串流","account":"a","segments":4,"delayMs":0,"stamp":true,"wait":true}""",
            """{"id":4,"op":"ords","conversation":"串流"}""",
            """{"id":5,"op":"setTitle","conversation":"串流","title":"  新的標題  "}""",
            """{"id":6,"op":"remoteAudit","limit":5}""",
            """{"id":7,"op":"setTitle","conversation":"沒有這個","title":"x"}""",
            """{"id":8,"op":"quit"}""",
            """{"id":9,"op":"status"}"""));
        var output = new StringWriter();

        var summary = await control.RunChannelAsync(input, output);

        var lines = Lines(output);
        lines[0].Value<string>("event").Should().Be("ready");
        lines[0].Value<bool>("paired").Should().BeFalse();
        var replies = lines.Skip(1).ToList();
        replies.Select(r => r["id"]!.ToString()).Should().Equal("1", "2", "", "s", "4", "5", "6", "7", "8");
        replies.Select(r => r.Value<bool>("ok")).Should().Equal(true, false, false, true, true, true, false, false, true);
        replies.Should().OnlyContain(r => r["ms"] != null);
        replies[0]["result"]!.Value<int>("conversations").Should().Be(0);
        replies[0]["result"]!["remoteTurns"]!.Value<int>("queued").Should().Be(0);
        replies[1].Value<string>("error").Should().Contain("不認得的 op");
        replies[2].Value<string>("error").Should().Contain("不是 JSON");

        var stream = (JObject)replies[3]["result"]!;
        stream.Value<string>("outcome").Should().Be("Completed");
        stream.Value<bool>("created").Should().BeTrue();
        stream.Value<int>("items").Should().Be(2);
        var id = stream.Value<string>("conversationId")!;
        var items = await device.OnUi(() => device.Hub.GetItems(id).ToList());
        StampRules.Parse(items[1].Text).Should().HaveCount(4, "每段開頭一個時間標記");
        items[1].Text.Should().Contain("第0001段。").And.Contain("第0004段。");

        var ords = (JObject)replies[4]["result"]!;
        ords.Value<int>("count").Should().Be(2);
        ((JArray)ords["items"]!).Select(i => i.Value<string>("id")).Should().Equal(items.Select(i => i.Id));
        ords.Value<int>("withoutOrd").Should().Be(2, "沒有配對時中樞不配 Ord");
        ords.Value<int>("withoutPublishedOrd").Should().Be(2, "沒有配對就沒有 sync.db 的 published_items（WP4.9 步 15）");
        ((JArray)ords["items"]!).Should().OnlyContain(i => i["publishedOrd"]!.Type == JTokenType.Null);
        ((JArray)ords["published"]!).Should().BeEmpty();

        replies[5]["result"]!.Value<string>("title").Should().Be("新的標題");
        (await device.OnUi(() => device.Hub.GetConversation(id)!.Title)).Should().Be("新的標題");
        replies[6].Value<string>("error").Should().Contain("沒有配對");
        replies[7].Value<string>("error").Should().Contain("沒有對話 沒有這個");

        summary.Value<int>("commands").Should().Be(9, "空白行不算、quit 之後不再讀");
        summary.Value<int>("errors").Should().Be(4);
        summary.Value<string>("ended").Should().Be("quit");
        device.Host.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task 背景串流_收下就回覆_status看得到_輸入結束時停掉()
    {
        using var temp = new TempDirectory("fd-control-bg");
        await using var device = await StartAsync(temp.Combine("device"));
        await using var control = new FakeControl(device);

        var started = await RunAsync(control, """{"op":"stream","conversation":"長","account":"a","segments":1000,"delayMs":50}""");
        started.Value<bool>("background").Should().BeTrue();
        var id = started.Value<string>("conversationId")!;
        (await device.OnUi(() => device.Hub.IsRunning(id))).Should().BeTrue();
        var again = () => RunAsync(control, """{"op":"stream","conversation":"長","segments":2,"delayMs":0}""");
        await again.Should().ThrowAsync<DeviceRuntimeException>().WithMessage("*上一個串流還沒結束*");

        var status = await RunAsync(control, """{"op":"status"}""");
        var entry = (JObject)((JArray)status["streams"]!).Single();
        entry.Value<string>("conversationId").Should().Be(id);
        entry.Value<bool>("done").Should().BeFalse();

        // 輸入結束（EOF）＝結束：還在跑的背景串流以網頁的 stop 停掉
        var summary = await control.RunChannelAsync(new StringReader(string.Empty), new StringWriter());
        summary.Value<string>("ended").Should().Be("eof");
        var stopped = (JObject)((JArray)summary["streams"]!).Single();
        stopped.Value<bool>("done").Should().BeTrue();
        stopped.Value<string>("outcome").Should().Be(nameof(AgentTurnOutcome.Interrupted));
        (await device.OnUi(() => device.Hub.IsRunning(id))).Should().BeFalse();
    }

    [Fact]
    public async Task 遠端回合的劇本_下一則不是chat送的訊息照它回_之後回到固定回覆()
    {
        using var temp = new TempDirectory("fd-control-remote");
        await using var device = await StartAsync(temp.Combine("device"));
        await using var control = new FakeControl(device);
        var id = (await device.ChatAsync(new ChatSpec("遠端", "a", "開始", [], null, Wait))).ConversationId;

        var queued = await RunAsync(control, """{"op":"nextRemoteTurn","turn":[{"text":"照遠端劇本的回覆","segments":3}]}""");
        queued.Value<int>("queued").Should().Be(1);
        queued.Value<int>("steps").Should().Be(1);

        // 網頁送的（不經 chat）：照排好的劇本回
        await device.CommandAsync("send", new JObject { ["conversationId"] = id, ["text"] = "從網頁送的" });
        (await device.WaitUntilAsync(() => !device.Hub.IsRunning(id) && device.Hub.GetItems(id).Count == 4, Wait)).Should().BeTrue();
        (await device.OnUi(() => device.Hub.GetItems(id)[3].Text)).Should().Be("照遠端劇本的回覆");
        device.RemoteTurns.Should().Be((0, 1));

        // 佇列空了：固定回覆
        await device.CommandAsync("send", new JObject { ["conversationId"] = id, ["text"] = "再送一則" });
        (await device.WaitUntilAsync(() => !device.Hub.IsRunning(id) && device.Hub.GetItems(id).Count == 6, Wait)).Should().BeTrue();
        (await device.OnUi(() => device.Hub.GetItems(id)[5].Text)).Should().NotBe("照遠端劇本的回覆");
        device.Host.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task restoreFromTrash_搬回資料根裡的對話_寫了伺服器刪除的旁檔_第二次不動()
    {
        using var temp = new TempDirectory("fd-control-trash");
        var root = temp.Combine("device");
        var seeded = ConversationSeeder.Write(root, new SeedSpec(Count: 3, OlderThanDays: 0, ItemsPerConversation: 4, Seed: 20261021,
            BaseTimeUtc: new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc)));
        var trashed = seeded.Ids[1];
        var conversations = Path.Combine(root, "conversations");
        new ConversationTrash(ConversationTrash.RootFor(conversations), conversations, TimeProvider.System)
            .TryMove(trashed, new TrashInfo(0, null, null, "遠端刪掉的")).Should().BeTrue();
        await using var device = await StartAsync(root);
        await using var control = new FakeControl(device);
        (await device.OnUi(() => device.Hub.GetConversation(trashed))).Should().BeNull("在回收區的不在清單");

        var result = await RunAsync(control, $$"""{"op":"restoreFromTrash","conversation":"{{trashed}}"}""");

        result.Value<bool>("restored").Should().BeTrue();
        result.Value<bool>("wasInTrash").Should().BeTrue();
        result.Value<bool>("serverDeleted").Should().BeTrue("還原的對話只在這台電腦");
        result.Value<bool>("inList").Should().BeTrue();
        FakeControlRules.IsUnder(result.Value<string>("trashRoot")!, root).Should().BeTrue();
        Directory.Exists(Path.Combine(conversations, trashed)).Should().BeTrue();

        var again = await RunAsync(control, $$"""{"op":"restoreFromTrash","conversation":"{{trashed}}"}""");
        again.Value<bool>("restored").Should().BeFalse("清單裡已有它");
        again.Value<bool>("wasInTrash").Should().BeFalse();
    }

    [Fact]
    public async Task xssFill_語料放進標題_帳戶名稱_卡片與檔名_放不進的記未覆蓋()
    {
        using var temp = new TempDirectory("fd-control-xss");
        var root = temp.Combine("device");
        string[] payloads = ["<img src=x onerror=alert(1)>", "<script>alert(2)</script>", "<svg onload=alert(3)>" + new string('x', 100)];
        var payloadFile = temp.WriteFile("payloads.json", new JObject { ["xss"] = new JArray(payloads.Select((p, i) => new JObject { ["name"] = $"p{i}", ["html"] = p })) }.ToString());
        var folder = temp.Combine("xss-files");
        await using var device = await StartAsync(root);
        await using var control = new FakeControl(device);

        var result = await RunAsync(control, new JObject
        {
            ["op"] = "xssFill",
            ["payloadFile"] = payloadFile,
            ["fields"] = new JArray("title", "accountLabel", "deviceName", "text", "tool", "question", "approval", "fileNames", "toast"),
            ["account"] = "a",
            ["folder"] = folder,
        }.ToString());

        result.Value<int>("count").Should().Be(3);
        var covered = (JObject)result["covered"]!;
        covered.Properties().Select(p => p.Name).Should().BeEquivalentTo("title", "accountLabel", "text", "tool", "question", "approval", "fileNames");
        covered.Value<int>("title").Should().Be(3);
        ((JArray)result["uncovered"]!).Select(u => u.Value<string>("field")).Should().BeEquivalentTo("toast", "deviceName");
        result.Value<int>("titleTruncated").Should().Be(1, "中樞的標題最多 80 字");

        var titles = await device.OnUi(() => ((JArray)result["titleConversations"]!).Select(t => device.Hub.GetConversation(t.ToString())!.Title).ToList());
        titles.Should().Equal(payloads[0], payloads[1], payloads[2][..80]);
        result.Value<string>("accountLabel").Should().Be(payloads[0]);

        var cards = result.Value<string>("cardsConversation")!;
        var items = await device.OnUi(() => device.Hub.GetItems(cards).ToList());
        items.Should().HaveCount(1 + 3 * 4);
        items.Where(i => i.Kind == AgentItemKind.Assistant).Select(i => i.Text).Should().Equal(payloads);
        items.Where(i => i.Kind == AgentItemKind.Question).Should().OnlyContain(i => i.Status == AgentItemStatus.Done && i.Decision == "answered");
        items.Where(i => i.Kind == AgentItemKind.Approval).Should().OnlyContain(i => i.Decision == "deny");

        ((JArray)result["files"]!).Select(f => f.ToString()).Should().Equal(payloads.Select((p, i) => FakeControlRules.SafeFileName(p, i)));
        Directory.GetFiles(folder).Should().HaveCount(3);
        result.Value<string>("folderConversation").Should().Be(cards);
        (await device.OnUi(() => device.Hub.GetConversation(cards)!.WorkingDirectory)).Should().Be(Path.GetFullPath(folder));

        // 第二批從 from 接著：同一個別名的標題對話沿用
        var next = await RunAsync(control, new JObject { ["op"] = "xssFill", ["payloadFile"] = payloadFile, ["fields"] = new JArray("title"), ["account"] = "a", ["from"] = 2 }.ToString());
        next.Value<int>("count").Should().Be(1);
        ((JArray)next["titleConversations"]!).Single().ToString().Should().Be(((JArray)result["titleConversations"]!)[2].ToString());

        // 路徑：不在 Golden 或 TEMP 底下、資料夾在資料根裡都被拒
        var outside = () => RunAsync(control, new JObject
        {
            ["op"] = "xssFill", ["payloadFile"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "x.json"), ["fields"] = new JArray("text"),
        }.ToString());
        await outside.Should().ThrowAsync<DeviceRuntimeException>().WithMessage("*Golden 資料夾或 TEMP*");
        var inData = () => RunAsync(control, new JObject
        {
            ["op"] = "xssFill", ["payloadFile"] = payloadFile, ["fields"] = new JArray("fileNames"), ["account"] = "a", ["folder"] = Path.Combine(root, "x"),
        }.ToString());
        await inData.Should().ThrowAsync<DeviceRuntimeException>().WithMessage("*不能在資料根*");
        device.Host.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task localCommand_經中樞的本機檢視_回這段時間的錯誤toast與對話狀態_沒配對時replicas失敗()
    {
        using var temp = new TempDirectory("fd-control-local");
        await using var device = await StartAsync(temp.Combine("device"));
        await using var control = new FakeControl(device);
        var id = (await device.ChatAsync(new ChatSpec("本機", "a", "開始", [], null, Wait))).ConversationId;

        var renamed = await RunAsync(control, $$$"""{"op":"localCommand","cmd":"rename","args":{"conversationId":"{{{id}}}","title":"本機改的名稱"}}""");
        renamed.Value<string>("cmd").Should().Be("rename");
        ((JArray)renamed["toasts"]!).Should().BeEmpty();
        var conversation = (JObject)renamed["conversation"]!;
        conversation.Value<bool>("exists").Should().BeTrue();
        conversation.Value<bool>("replica").Should().BeFalse();
        conversation.Value<string>("title").Should().Be("本機改的名稱");
        conversation.Value<string>("syncPolicy").Should().Be(ConversationSyncPolicies.Full);
        conversation.Value<int>("items").Should().Be(2);
        conversation.Value<bool>("serverDeleted").Should().BeFalse();

        var metadata = await RunAsync(control, $$$"""{"op":"localCommand","cmd":"syncSetConversationPolicy","args":{"conversationId":"{{{id}}}","policy":"metadata"}}""");
        ((JArray)metadata["toasts"]!).Should().BeEmpty();
        metadata["conversation"]!.Value<string>("syncPolicy").Should().Be(ConversationSyncPolicies.Metadata);

        // 已從伺服器刪除（旁檔）的對話改回 full：中樞拒絕、跳一則錯誤 toast，有效政策仍是 localOnly
        (await device.OnUi(() => device.Hub.MarkServerDeleted(id, 1))).Should().BeTrue();
        var back = await RunAsync(control, $$$"""{"op":"localCommand","cmd":"syncSetConversationPolicy","args":{"conversationId":"{{{id}}}","policy":"full"}}""");
        ((JArray)back["toasts"]!).Select(t => t.ToString()).Should().Equal(ConversationPolicyCommandRules.ServerDeletedReason);
        back["conversation"]!.Value<string>("syncPolicy").Should().Be(ConversationSyncPolicies.LocalOnly);
        back["conversation"]!.Value<bool>("serverDeleted").Should().BeTrue();

        var missing = await RunAsync(control, """{"op":"localCommand","cmd":"open","args":{"conversationId":"0123456789abcdef0123456789abcdef"}}""");
        missing["conversation"]!.Value<bool>("exists").Should().BeFalse();
        missing.ContainsKey("paused").Should().BeFalse("只有 syncPause 附暫停的設定");

        var replicas = () => RunAsync(control, """{"op":"replicas"}""");
        await replicas.Should().ThrowAsync<DeviceRuntimeException>().WithMessage("*沒有配對*");
        // unpair（WP4.9 步 17）：沒有配對時失敗，什麼都不刪
        var unpair = () => RunAsync(control, """{"op":"unpair"}""");
        await unpair.Should().ThrowAsync<DeviceRuntimeException>().WithMessage("*沒有配對*");
        device.Host.Errors.Should().BeEmpty();
    }

    [Fact]
    public void repo的e2e_fixtures_在可以讀的根目錄底下_格式讀得出200筆以上()
    {
        var roots = FakeControl.PayloadRoots();
        roots.Should().HaveCount(3, "找得到 repo（往上找 Myriagent.slnx）");
        var golden = Path.Combine(roots[1], "e2e-fixtures.json");
        FakeControlRules.CheckPayloadFile(golden, roots).Should().BeNull();
        FakeControl.LoadPayloads(golden).Should().HaveCountGreaterThanOrEqualTo(200);
    }
}
