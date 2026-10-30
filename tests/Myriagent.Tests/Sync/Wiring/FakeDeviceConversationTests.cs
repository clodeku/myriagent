using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Myriagent.Tests.Sync.Wiring;

/// <summary>
/// tools/fake-device 的劇本化會話與「N 個舊對話」（WP4.3 計畫步 13；用連結編譯的 Runtime）：劇本的一輪照順序發出串流、工具、卡片與媒體、
/// 等中樞回應卡片；chat 經網頁的指令開對話、附檔、送出並照劇本回應卡片；seedConversations 只由種子與基準時間決定
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class FakeDeviceConversationTests(ITestOutputHelper output)
{
    private static readonly DateTime BaseTime = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>直接驅動會話時的設定（劇本化的會話不看）</summary>
    private static readonly AgentSessionSettings Settings = new(null, false, null, "fake", _ => { }, null, null, null, AgentPermissionMode.Ask, @"C:\", @"C:\", "", false);

    /// <summary>文字含代理字對（🙂）：串流的片段不能切開它</summary>
    private const string Reply = "你好，世界🙂再見";

    /// <summary>劇本（JSON，同 README 的格式）：三段文字、兩段輸出的工具、問卷、核准、媒體。<paramref name="answer"/>、<paramref name="decision"/> 是 null 時卡片留著等</summary>
    private static ScriptedTurn FullTurn(string mediaPath, string? answer, string? decision)
    {
        var question = new JObject
        {
            ["question"] = new JArray(new JObject { ["id"] = "q1", ["text"] = "要用哪個資料夾？", ["options"] = new JArray("src", "tests") }),
        };
        if (answer is not null)
            question["answer"] = new JObject { ["q1"] = new JArray(answer) };
        var approval = new JObject { ["approval"] = "寫入檔案", ["kind"] = "write", ["title"] = "README.md" };
        if (decision is not null)
            approval["decision"] = decision;
        return ScriptedTurn.Parse(new JObject
        {
            ["steps"] = new JArray(
                new JObject { ["text"] = Reply, ["segments"] = 3 },
                new JObject { ["tool"] = "執行指令", ["kind"] = "bash", ["title"] = "dotnet build", ["output"] = "建置成功，0 個錯誤", ["segments"] = 2 },
                question,
                approval,
                new JObject { ["media"] = mediaPath }),
        });
    }

    [Fact]
    public async Task 劇本的一輪_產生文字串流工具與卡片()
    {
        using var temp = new TempDirectory("fake-device-chat");
        var media = temp.Combine("chart.png");
        File.WriteAllBytes(media, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // 1. 直接驅動會話：事件的順序、片段不切開代理字對、卡片等回應才接著做
        var events = new ConcurrentQueue<AgentEvent>();
        var gate = new object();
        PendingTurn? next = null;
        var session = new ScriptedSession(AgentProvider.Claude, action =>
        {
            lock (gate)
                action();
        }, () => Interlocked.Exchange(ref next, null));
        session.EventReceived += events.Enqueue;

        var turn = new PendingTurn(FullTurn(media, answer: null, decision: null));
        next = turn;
        await session.SendAsync(new AgentTurnInput("開始", []), Settings);
        turn.Started.Task.IsCompleted.Should().BeTrue("送出時就取走這一輪");
        session.IsRunning.Should().BeTrue();
        var question = await WaitForAsync(events, e => e is AgentQuestionEvent) as AgentQuestionEvent;
        await Task.Delay(100);
        events.OfType<AgentApprovalEvent>().Should().BeEmpty("問卷沒回答之前不接著做");
        await session.AnswerQuestionAsync(question!.RequestKey, new Dictionary<string, IReadOnlyList<string>> { ["q1"] = ["src"] });
        var approval = await WaitForAsync(events, e => e is AgentApprovalEvent) as AgentApprovalEvent;
        await session.RespondApprovalAsync(approval!.RequestKey, AgentApprovalChoice.Allow, null);
        (await turn.Completed.Task.WaitAsync(Wait)).Should().Be(AgentTurnOutcome.Completed);
        session.IsRunning.Should().BeFalse();

        events.Select(e => e.GetType().Name).Should().Equal(
            nameof(AgentSessionStartedEvent),
            nameof(AgentTextDeltaEvent), nameof(AgentTextDeltaEvent), nameof(AgentTextDeltaEvent), nameof(AgentTextCompletedEvent),
            nameof(AgentToolStartedEvent), nameof(AgentToolOutputEvent), nameof(AgentToolOutputEvent), nameof(AgentToolCompletedEvent),
            nameof(AgentQuestionEvent), nameof(AgentApprovalEvent), nameof(AgentMediaEvent), nameof(AgentTurnCompletedEvent));
        var deltas = events.OfType<AgentTextDeltaEvent>().Select(d => d.Delta).ToList();
        string.Concat(deltas).Should().Be(Reply);
        deltas.Should().HaveCount(3).And.OnlyContain(d => d.Length > 0 && !char.IsHighSurrogate(d[d.Length - 1]) && !char.IsLowSurrogate(d[0]), "不切開代理字對");
        string.Concat(events.OfType<AgentToolOutputEvent>().Select(o => o.Delta)).Should().Be("建置成功，0 個錯誤");
        events.OfType<AgentToolCompletedEvent>().Single().Status.Should().Be(AgentItemStatus.Done);

        // 停止：停在卡片上的一輪以「被中斷」結束；沒有劇本的一輪照舊（一段固定回覆）
        events.Clear();
        var stopped = new PendingTurn(FullTurn(media, null, null));
        next = stopped;
        await session.SendAsync(new AgentTurnInput("再一次", []), Settings);
        await WaitForAsync(events, e => e is AgentQuestionEvent);
        await session.InterruptAsync();
        (await stopped.Completed.Task.WaitAsync(Wait)).Should().Be(AgentTurnOutcome.Interrupted);
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted);

        events.Clear();
        var plain = new PendingTurn(null);
        next = plain;
        await session.SendAsync(new AgentTurnInput("固定回覆", []), Settings);
        (await plain.Completed.Task.WaitAsync(Wait)).Should().Be(AgentTurnOutcome.Completed);
        events.Select(e => e.GetType().Name).Should().Equal(nameof(AgentTextCompletedEvent), nameof(AgentTurnCompletedEvent));
        events.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be(session.Reply);
        session.Dispose();

        // 2. 經中樞（未配對的假裝置）：chat 開新對話、附檔、照劇本回應卡片；項目的種類與結果
        var attachment = temp.Combine("notes.txt");
        File.WriteAllText(attachment, "附件的內容");
        var root = temp.Combine("device");
        await using var device = await DeviceRuntime.StartAsync(new DeviceRuntimeOptions
        {
            DataRoot = root,
            Persist = false,
            Accounts = [new FakeAccountSpec("chat", AgentProvider.Claude, "Claude chat", null, "chat@example.test", "org-chat", "max")],
        });
        var result = await device.ChatAsync(new ChatSpec("工作", "chat", "幫我整理專案", [attachment], FullTurn(media, "tests", "allow"), Wait));
        result.Created.Should().BeTrue();
        result.Waiting.Should().BeFalse();
        result.Outcome.Should().Be(AgentTurnOutcome.Completed);
        var items = await device.OnUi(() => device.Hub.GetItems(result.ConversationId).ToList());
        items.Select(i => i.Kind).Should().Equal(AgentItemKind.User, AgentItemKind.Assistant, AgentItemKind.Tool, AgentItemKind.Question, AgentItemKind.Approval,
            AgentItemKind.Assistant);
        items[0].Text.Should().Be("幫我整理專案");
        items[0].Attachments.Should().ContainSingle(a => a.FileName == "notes.txt");
        items[1].Text.Should().Be(Reply);
        items[2].Should().Match<AgentChatItem>(i => i.Status == AgentItemStatus.Done && i.Output == "建置成功，0 個錯誤");
        items[3].Should().Match<AgentChatItem>(i => i.Status == AgentItemStatus.Done && i.Decision == "answered" && i.Answers["q1"].Single() == "tests");
        items[4].Should().Match<AgentChatItem>(i => i.Status == AgentItemStatus.Done && i.Decision == "allow");
        items[5].MediaPaths.Should().Equal(media);

        // 劇本沒有替使用者回應的卡片：停在那裡（這一輪沒結束），同一個對話不能再送；停止後以「被中斷」結束
        var waiting = await device.ChatAsync(new ChatSpec("工作", null, "再問一次", [], FullTurn(media, answer: null, decision: "deny"), Wait));
        waiting.Should().Match<ChatResult>(r => r.Waiting && r.Outcome == null && !r.Created && r.ConversationId == result.ConversationId);
        var busy = () => device.ChatAsync(new ChatSpec(result.ConversationId, null, "插隊", [], null, Wait));
        await busy.Should().ThrowAsync<InvalidOperationException>().WithMessage("*還在執行上一輪*");
        await device.CommandAsync("stop", new JObject { ["conversationId"] = result.ConversationId });
        (await device.WaitUntilAsync(() => !device.Hub.IsRunning(result.ConversationId), Wait)).Should().BeTrue("停止後這一輪結束");
        var plainChat = await device.ChatAsync(new ChatSpec(result.ConversationId, null, "沒有劇本", [], null, Wait));
        plainChat.Outcome.Should().Be(AgentTurnOutcome.Completed);
        (await device.OnUi(() => device.Hub.GetItems(result.ConversationId).Last().Text)).Should().Be(session.Reply);

        // 帳戶不在：開不了新對話
        var rejected = () => device.ChatAsync(new ChatSpec("別的", "沒有這個帳戶", "x", [], null, Wait));
        await rejected.Should().ThrowAsync<InvalidOperationException>().WithMessage("*沒有帳戶*");
        device.Host.Errors.Should().BeEmpty();
    }

    /// <summary>
    /// Grok、Antigravity 的對話（設計 2026-10-22-grok-antigravity-sync P5）：劇本的思考、待辦、計畫確認卡與這一輪的用量經中樞成為對應的項目與標頭；
    /// 執行中再送一則時，中樞只對支援插入的代理（Claude）呼叫 SteerAsync，Grok、Antigravity 直接排隊（AgentHubRules.SupportsSteering），
    /// 假會話一律不插，所以三種都排進佇列、這一輪結束後接著送
    /// </summary>
    [Fact]
    public async Task Grok與Antigravity的對話_思考待辦計畫卡與用量_執行中送出只排隊不插入()
    {
        using var temp = new TempDirectory("fake-device-providers");
        await using var device = await DeviceRuntime.StartAsync(new DeviceRuntimeOptions
        {
            DataRoot = temp.Combine("device"),
            Persist = false,
            Accounts =
            [
                new FakeAccountSpec("p-claude", AgentProvider.Claude, "Claude", null, "p-claude@example.test", "org-p", "max"),
                new FakeAccountSpec("p-grok", AgentProvider.Grok, "Grok", null, "p-grok@example.test", null, null),
                new FakeAccountSpec("p-agy", AgentProvider.Antigravity, "Antigravity", null, "p-agy@example.test", null, null),
            ],
        });
        var agy = await device.FindAccountAsync("p-agy");
        agy.Should().Match<Myriagent.Accounts.AgentAccount>(a => a.Provider == AgentProvider.Antigravity && a.IsSystemDefault && a.Email == "p-agy@example.test",
            "Antigravity 的假帳戶和 app 的一樣是本機預設帳戶的形狀（沒有隔離的設定資料夾）");
        DeviceRuntime.IsFakeAntigravity(agy!).Should().BeTrue();

        // Grok 的一輪：思考 → 待辦 → 工具 → 計畫確認（chat 照劇本回 manual）→ 核准（allow）→ 回覆；結束事件帶用量
        var grokTurn = ScriptedTurn.Parse(JToken.Parse("""
            {"steps":[
              {"thinking":"先看專案的結構","segments":2},
              {"todo":[{"text":"讀設定","status":"completed"},{"text":"改程式","status":"inProgress"},{"text":"跑測試"}]},
              {"tool":"執行指令","kind":"bash","title":"dotnet build","detail":"建置\n\ndotnet build","output":"建置成功"},
              {"planApproval":"1. 改 A\n2. 跑測試","decision":"manual"},
              {"approval":"修改檔案","kind":"edit","title":"src/A.cs","decision":"allow"},
              {"text":"做完了","segments":2}
            ],"outcome":"completed","usage":{"input":1234,"output":56,"cached":7}}
            """));
        var grok = await device.ChatAsync(new ChatSpec("grok", "p-grok", "幫我改 A", [], grokTurn, Wait));
        grok.Outcome.Should().Be(AgentTurnOutcome.Completed);
        var items = await device.OnUi(() => device.Hub.GetItems(grok.ConversationId).ToList());
        items.Select(i => i.Kind).Should().Equal(AgentItemKind.User, AgentItemKind.Thinking, AgentItemKind.Plan, AgentItemKind.Tool, AgentItemKind.PlanApproval,
            AgentItemKind.Approval, AgentItemKind.Assistant);
        items[1].Text.Should().Be("先看專案的結構");
        items[2].PlanSteps.Select(st => (st.Text, st.Status)).Should().Equal(("讀設定", "completed"), ("改程式", "inProgress"), ("跑測試", "pending"));
        items[4].Should().Match<AgentChatItem>(i => i.ToolName == "計畫" && i.Title == "照這個計畫執行？" && i.Detail == "1. 改 A\n2. 跑測試" && i.Decision == "manual"
                                                     && i.Status == AgentItemStatus.Done);
        (await device.OnUi(() => device.Hub.GetConversation(grok.ConversationId)!.Provider)).Should().Be(AgentProvider.Grok);
        JObject? header = null;
        (await device.WaitUntilAsync(() =>
        {
            header = device.View.Recent.LastOrDefault(m => m.Value<string>("conversationId") == grok.ConversationId && m["header"] is JObject h && h.Value<bool>("running") == false)
                ?["header"] as JObject;
            return header?.Value<string>("lastTurn") is not null;
        }, Wait)).Should().BeTrue($"這一輪結束後的標頭帶用量（最後一份：{header?.ToString(Newtonsoft.Json.Formatting.None)}）");
        header!.Value<string>("lastTurn").Should().Be("輸入 1,234・輸出 56 tokens", "這一輪的用量（劇本的 usage）照常顯示在標頭");
        header.Value<bool>("steer").Should().BeFalse("Grok 不支援執行中插入");

        // 三種代理各開一輪停在核准卡（執行中），再送一則：只有 Claude 嘗試插入
        var waitingTurn = () => ScriptedTurn.Parse(JToken.Parse("""[{"approval":"執行指令","kind":"bash","title":"npm test"},{"text":"好了"}]"""));
        var claude = await device.ChatAsync(new ChatSpec("claude", "p-claude", "開始", [], waitingTurn(), Wait));
        var grokWait = await device.ChatAsync(new ChatSpec("grok", null, "再一輪", [], waitingTurn(), Wait));
        var agyChat = await device.ChatAsync(new ChatSpec("agy", "p-agy", "開始", [], waitingTurn(), Wait));
        new[] { claude, grokWait, agyChat }.Should().OnlyContain(r => r.Waiting);
        (await device.OnUi(() => device.Hub.GetConversation(agyChat.ConversationId)!.Provider)).Should().Be(AgentProvider.Antigravity);

        long Steers() => device.SessionCalls.ToJson().Value<long>("steers");
        var before = Steers();
        foreach (var (id, expected) in new[] { (claude.ConversationId, 1L), (grokWait.ConversationId, 0L), (agyChat.ConversationId, 0L) })
        {
            var mark = Steers();
            await device.CommandAsync("send", new JObject { ["conversationId"] = id, ["text"] = "執行中再送一則" });
            (await device.WaitUntilAsync(() => device.Hub.GetQueue(id).Count == 1, Wait)).Should().BeTrue($"{id} 的這則排進佇列（假會話不插入）");
            (Steers() - mark).Should().Be(expected, $"{id}：中樞只對支援插入的代理嘗試插入");
        }

        (Steers() - before).Should().Be(1);
        foreach (var id in new[] { claude.ConversationId, grokWait.ConversationId, agyChat.ConversationId })
        {
            var card = await device.OnUi(() => device.Hub.GetItems(id).Single(i => i.Kind == AgentItemKind.Approval && i.Status == AgentItemStatus.Waiting));
            await device.CommandAsync("approve", new JObject { ["conversationId"] = id, ["itemId"] = card.Id, ["choice"] = "allow" });
            (await device.WaitUntilAsync(() => !device.Hub.IsRunning(id) && device.Hub.GetQueue(id).Count == 0, Wait))
                .Should().BeTrue($"{id}：這一輪結束後排隊的那則接著送、也跑完");
        }

        device.Host.Errors.Should().BeEmpty();
    }

    [Fact]
    public void 假裝置的Antigravity帳戶_只認別名算的id_真實資料根的不載入()
    {
        var fake = new Myriagent.Accounts.AgentAccount { Id = DeviceRuntime.AccountId("agy"), Provider = AgentProvider.Antigravity, ConfigDirectory = null };
        DeviceRuntime.IsFakeAntigravity(fake).Should().BeTrue();
        DeviceRuntime.IsFakeAntigravity(new Myriagent.Accounts.AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity })
            .Should().BeFalse("app 建的 Antigravity 帳戶是固定的 local-antigravity：從真實資料根複製來的不載入");
        DeviceRuntime.IsFakeAntigravity(new Myriagent.Accounts.AgentAccount { Id = fake.Id, Provider = AgentProvider.Antigravity, ConfigDirectory = @"C:\x" })
            .Should().BeFalse("Antigravity 沒有隔離的設定資料夾");
        foreach (var provider in new[] { AgentProvider.Claude, AgentProvider.Codex, AgentProvider.Grok })
            DeviceRuntime.IsFakeAntigravity(new Myriagent.Accounts.AgentAccount { Id = fake.Id, Provider = provider }).Should().BeFalse($"{provider} 的系統預設帳戶照舊不載入");
    }

    [Fact]
    public void seedConversations_固定種子與基準時間_產生同一份()
    {
        var spec = new SeedSpec(Count: 40, OlderThanDays: 120, ItemsPerConversation: 25, Seed: 20261020, BaseTimeUtc: BaseTime);
        var first = ConversationSeeder.Describe(ConversationSeeder.Generate(spec));
        var again = ConversationSeeder.Describe(ConversationSeeder.Generate(spec));
        again.Sha256.Should().Be(first.Sha256, "同一個種子與基準時間產生同一份");
        again.Ids.Should().Equal(first.Ids);
        first.Should().Match<SeedResult>(r => r.Conversations == 40 && r.Items == 40 * 25);
        first.Ids.Should().OnlyHaveUniqueItems().And.OnlyContain(id => id.Length == 32 && id.All(Uri.IsHexDigit));

        var otherSeed = ConversationSeeder.Describe(ConversationSeeder.Generate(spec with { Seed = 20261021 }));
        otherSeed.Sha256.Should().NotBe(first.Sha256);
        otherSeed.Ids.Should().NotIntersectWith(first.Ids, "不同的種子 id 不同");
        ConversationSeeder.Describe(ConversationSeeder.Generate(spec with { BaseTimeUtc = BaseTime.AddMinutes(1) })).Sha256.Should().NotBe(first.Sha256, "時間只由基準時間決定");

        // 寫進資料根再讀回來：筆數、時間（UTC 比）、舊格式（沒有 Ord、NextOrd）；讀回來的指紋也相同（與時區無關）
        using var temp = new TempDirectory("fake-device-seed");
        var written = ConversationSeeder.Write(temp.Root, spec);
        written.Sha256.Should().Be(first.Sha256);
        var store = new ConversationStore(Path.Combine(temp.Root, "conversations"));
        var loaded = store.LoadConversations().ToDictionary(c => c.Id, StringComparer.Ordinal);
        loaded.Keys.Should().BeEquivalentTo(first.Ids);
        var reloaded = new List<(AgentConversation, List<AgentChatItem>)>();
        for (var i = 0; i < first.Ids.Count; i++)
        {
            var meta = loaded[first.Ids[i]];
            meta.UpdatedAt.ToUniversalTime().Should().Be(BaseTime.AddDays(-120).AddHours(-i), $"第 {i + 1} 個對話的時間");
            meta.NextOrd.Should().BeNull("WP4.3 之前的格式");
            meta.SyncPolicy.Should().BeNull();
            meta.AccountId.Should().Be(DeviceRuntime.AccountId("seed"));
            var items = store.LoadItems(meta.Id);
            items.Should().HaveCount(25).And.OnlyContain(item => item.Ord == null);
            reloaded.Add((meta, items));
        }

        reloaded.SelectMany(c => c.Item2).Count(i => i.Kind == AgentItemKind.Tool).Should().Be(40 * 5, "每五則一則工具");
        ConversationSeeder.Fingerprint(reloaded).Should().Be(first.Sha256, "讀回來（本機時區）的指紋也一樣");
        var twice = () => ConversationSeeder.Write(temp.Root, spec);
        twice.Should().Throw<InvalidOperationException>("不覆寫已經有的對話");
        var local = () => ConversationSeeder.Generate(spec with { BaseTimeUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Local) });
        local.Should().Throw<ArgumentException>().WithMessage("*UTC*");
        output.WriteLine($"seedConversations（count 40、olderThanDays 120、itemsPerConversation 25、seed 20261020、baseTimeUtc 2026-10-20T00:00:00Z）：" +
                         $"{first.Conversations} 個對話、{first.Items} 則，SHA-256 {first.Sha256}");
    }

    private static async Task<AgentEvent> WaitForAsync(ConcurrentQueue<AgentEvent> events, Func<AgentEvent, bool> match)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            if (events.FirstOrDefault(match) is { } found)
                return found;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("等不到事件：" + string.Join("、", events.Select(e => e.GetType().Name)));
            await Task.Delay(20);
        }
    }
}
