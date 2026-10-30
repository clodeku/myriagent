using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 伺服器→瀏覽器的即時更新增量（patch1；docs/plans/2026-10-23-live-delta-and-replica-live.md 3.2、3.4）：<see cref="ItemPatchRules"/> 的算與套用、
/// 送出佇列記的即時版本（<see cref="ClientOutbox.CanPatch"/>）、ClientState 的 features，以及經閘道的端到端（宣告 patch1 的連線收增量、
/// 套用後與舊分頁收到的整筆相同；舊分頁只收 ≤64 KB 的格；重新 open 之後接在 open 補推的整筆後面）
/// </summary>
public sealed class ItemPatchTests
{
    private static readonly string C1 = ConversationHarness.ConversationId(1);

    private static readonly string[] Patch1 = [MessageGuard.Patch1Feature];

    private static JObject Parse(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return (JObject)JToken.ReadFrom(reader);
    }

    private static string Item(string html, string? detail = null, string status = "Running", string id = "i1")
    {
        var item = new JObject { ["id"] = id, ["kind"] = "Assistant", ["status"] = status, ["time"] = "2026-10-23T01:00:00.000Z", ["html"] = html };
        if (detail is not null)
            item["detail"] = detail;
        return item.ToString(Formatting.None);
    }

    // ───── 規則 ─────

    [Fact]
    public void 串流中只長尾巴_增量只帶新的部分_套用後與整筆相同()
    {
        var basis = Item("<p>" + new string('前', 2000) + "</p>");
        var current = Item("<p>" + new string('前', 2000) + "，接著一段</p>", status: "Running");
        var patch = ItemPatchRules.Compute(basis, current);

        patch.Should().NotBeNull();
        patch!.Ops.Should().ContainSingle().Which.Should().Be(new ItemPatchOp("html", 2003, "，接著一段</p>", 2003 + "，接著一段</p>".Length));
        Parse(patch.SetJson).Properties().Select(p => p.Name).Should().Equal("id", "kind", "status", "time");
        Parse(patch.SetJson)["time"]!.Value<string>().Should().Be("2026-10-23T01:00:00.000Z", "日期字串原樣帶回（不轉成 DateTime）");
        JToken.DeepEquals(ItemPatchRules.Apply(basis, patch), Parse(current)).Should().BeTrue();
    }

    [Fact]
    public void 長欄位有無變了_或增量不比整筆省一半_送整筆()
    {
        var html = "<p>" + new string('前', 2000) + "</p>";
        ItemPatchRules.Compute(Item(html), Item(html, detail: "新的細節")).Should().BeNull("detail 從沒有變成有");
        ItemPatchRules.Compute(Item(html, detail: "x"), Item(html)).Should().BeNull("detail 從有變成沒有");
        ItemPatchRules.Compute(Item(html), Item("<p>" + new string('後', 2000) + "</p>")).Should().BeNull("整段換掉");
        ItemPatchRules.Compute(Item(html), Item(html, id: "i2")).Should().BeNull("不同的項目");
        ItemPatchRules.Compute(Item(html), "不是 JSON").Should().BeNull();

        var same = ItemPatchRules.Compute(Item(html), Item(html, status: "Waiting"));
        same.Should().NotBeNull("長欄位沒變、只改狀態");
        same!.Ops.Should().BeEmpty();
        Parse(same.SetJson)["status"]!.Value<string>().Should().Be("Waiting");
    }

    [Fact]
    public void 切點不在代理對中間()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        var other = char.ConvertFromUtf32(0x1F601);
        var prefix = new string('前', 3000);
        var patch = ItemPatchRules.Compute(Item(prefix + emoji), Item(prefix + other));
        patch.Should().NotBeNull();
        patch!.Ops[0].At.Should().Be(prefix.Length, "兩個表情符號的高位代理相同，切點退回代理對之前");
        char.IsLowSurrogate(patch.Ops[0].S[0]).Should().BeFalse();
        JToken.DeepEquals(ItemPatchRules.Apply(Item(prefix + emoji), patch), Parse(Item(prefix + other))).Should().BeTrue();
    }

    // ───── 送出佇列 ─────

    private static readonly string Key = ClientOutboxRules.ItemKey(C1, "i1");

    private static ClientOutboxMessage Live(long rv) => ConversationGatewayRules.LiveItemMessage(C1, "i1", Item("<p>x</p>"), rv);

    private static ClientOutboxMessage PatchMsg(long rv, long baseRv) =>
        ConversationGatewayRules.ItemPatchMessage(C1, "i1", rv, baseRv, new ItemPatch(Parse(Item("<p>x</p>")).ToString(Formatting.None), []));

    [Fact]
    public void 佇列記每筆最後入列的即時版本_排著同一筆時不送增量()
    {
        var outbox = new ClientOutbox(500, 2 * 1024 * 1024);
        outbox.CanPatch(Key, 1).Should().BeFalse("網頁還沒有任何即時版本");
        outbox.Enqueue(Live(1)).Should().Be(ClientOutboxResult.Queued);
        outbox.CanPatch(Key, 1).Should().BeFalse("那一則還排著：改送整筆、以同一個鍵合併");
        outbox.TryDequeue(out _).Should().BeTrue();
        outbox.CanPatch(Key, 1).Should().BeTrue();
        outbox.CanPatch(Key, 2).Should().BeFalse("基底版本不同");

        outbox.Enqueue(PatchMsg(2, 1)).Should().Be(ClientOutboxResult.Queued);
        outbox.Enqueue(PatchMsg(3, 2)).Should().Be(ClientOutboxResult.Queued, "增量不合併");
        outbox.Count.Should().Be(2);
        outbox.CanPatch(Key, 3).Should().BeFalse("增量還排著");
        while (outbox.TryDequeue(out _))
        {
        }
        outbox.CanPatch(Key, 3).Should().BeTrue("最後入列的是 rv 3");

        outbox.Enqueue(ConversationGatewayRules.ItemMessage(C1, "i1", Item("<p>持久</p>")));
        outbox.TryDequeue(out _);
        outbox.CanPatch(Key, 3).Should().BeFalse("持久的 item 清掉即時版本");
    }

    [Fact]
    public void 對話屏障與init與溢出清掉即時版本_屏障也清掉排著的增量()
    {
        var outbox = new ClientOutbox(500, 2 * 1024 * 1024);
        outbox.Enqueue(Live(1));
        outbox.TryDequeue(out _);
        outbox.Enqueue(PatchMsg(2, 1));
        outbox.Enqueue(ConversationGatewayRules.ConversationMessage(C1, "{}", [], false, "{}"));
        outbox.Count.Should().Be(1, "conversation 屏障清掉排著的 itemPatch");
        outbox.TryDequeue(out _);
        outbox.CanPatch(Key, 2).Should().BeFalse();

        outbox.Enqueue(Live(5));
        outbox.TryDequeue(out _);
        outbox.Enqueue(new ClientOutboxMessage(ClientOutboxRules.InitType, "{}"));
        outbox.TryDequeue(out _);
        outbox.CanPatch(Key, 5).Should().BeFalse("init 清掉");

        var small = new ClientOutbox(2, 2 * 1024 * 1024);
        small.Enqueue(Live(7));
        small.TryDequeue(out _);
        small.Enqueue(new ClientOutboxMessage("toast", "{}"));
        small.Enqueue(new ClientOutboxMessage("toast", "{}"));
        small.Enqueue(new ClientOutboxMessage("toast", "{}")).Should().Be(ClientOutboxResult.Overflow);
        small.CanPatch(Key, 7).Should().BeFalse("溢出清空");
    }

    // ───── ClientState ─────

    [Fact]
    public void ClientState的features_選用_只收字串陣列()
    {
        static JObject State(JToken? features)
        {
            var state = JObject.Parse("""{"visible":true,"focused":true,"openConversationIds":[],"tzOffsetMinutes":0}""");
            if (features is not null)
                state["features"] = features;
            return state;
        }

        MessageGuard.ParseClientState(State(null))!.Patch1.Should().BeFalse("舊網頁沒有 features");
        MessageGuard.ParseClientState(State(new JArray("patch1", "future9")))!.Patch1.Should().BeTrue("不認得的名稱照收");
        MessageGuard.ParseClientState(State(new JArray("PATCH1")))!.Patch1.Should().BeFalse("區分大小寫");
        MessageGuard.ParseClientState(State("patch1")).Should().BeNull("不是陣列");
        MessageGuard.ParseClientState(State(new JArray(1))).Should().BeNull();
        MessageGuard.ParseClientState(State(new JArray(""))).Should().BeNull();
        MessageGuard.ParseClientState(State(new JArray(new string('f', MessageGuard.MaxClientFeatureLength + 1)))).Should().BeNull();
        MessageGuard.ParseClientState(State(new JArray(Enumerable.Range(0, MessageGuard.MaxClientFeatures + 1).Select(i => (object)("f" + i))))).Should().BeNull();
    }

    // ───── 經閘道 ─────

    private static string Text(int paragraphs) => string.Join("\n\n", Enumerable.Range(1, paragraphs).Select(i => "第" + i + "段：" + new string('字', 120)));

    private static JObject Running(string text, long ver) => ConversationSamples.Item(text, "Running", "item1", 1, ver);

    private static async Task<JObject> NextOfAsync(SendRecorder recorder, string type)
    {
        while (true)
        {
            var message = Parse(await recorder.NextRawAsync());
            if (message["type"]!.Value<string>() == type)
                return message;
        }
    }

    private static JObject ApplyPatch(JObject baseItem, JObject message)
    {
        var ops = ((JArray)message["ops"]!).Select(o => new ItemPatchOp(o["f"]!.Value<string>()!, o["at"]!.Value<int>(), o["s"]!.Value<string>()!, o["n"]!.Value<int>())).ToList();
        var applied = ItemPatchRules.Apply(baseItem.ToString(Formatting.None), new ItemPatch(message["set"]!.ToString(Formatting.None), ops));
        applied.Should().NotBeNull();
        return applied!;
    }

    [Fact]
    public async Task live2的大項目_128KB以上照樣渲染_不是暫時無法顯示()
    {
        // 回歸（甲5 的流量量測抓到的）：渲染即時的格曾以持久 payload 的 128 KB 解碼，128～256 KB 的串流中項目在瀏覽器變成「這一筆內容暫時無法在瀏覽器顯示」
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive2(device);
        var (a, _) = await h.ReadyAsync("b1");
        h.SetState(a, true, [C1], features: Patch1);
        await h.OpenAsync(a, C1);

        var big = Running(string.Concat(Enumerable.Repeat("串流中的長回覆，", 7000)) + "結尾記號", 1);
        System.Text.Encoding.UTF8.GetByteCount(big.ToString(Formatting.None)).Should().BeInRange(ConversationEventRules.MaxPayloadBytes + 1, LiveRules.MaxDeltaItemBytes);
        h.Relay(live, C1, 1, 1, big).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        var html = (await NextOfAsync(a, "item"))["item"]!["html"]!.Value<string>()!;
        html.Should().Contain("結尾記號").And.NotContain(MyriagentHub.Rendering.HubItemRules.UnavailableMessage);
    }

    [Fact]
    public async Task 宣告patch1的分頁收增量_套用後和舊分頁收到的整筆相同_重新open之後接在補推的整筆後面()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive2(device);
        var (a, _) = await h.ReadyAsync("b1");
        var (old, _) = await h.ReadyAsync("b2", "s2");
        h.SetState(a, true, [C1], features: Patch1);
        await h.OpenAsync(a, C1);
        await h.OpenAsync(old, C1);

        h.Relay(live, C1, 1, 1, Running(Text(20), 1)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        var first = await NextOfAsync(a, "item");
        var rv1 = first["rv"]!.Value<long>();
        (await NextOfAsync(old, "item"))["rv"]!.Value<long>().Should().Be(rv1, "舊分頁也帶 rv（忽略）");
        var shown = (JObject)first["item"]!;
        var rv = rv1;

        for (var step = 2; step <= 4; step++)
        {
            h.Relay(live, C1, 1, step, Running(Text(20) + string.Concat(Enumerable.Range(2, step - 1).Select(i => "\n\n新的第" + i + "段")), step))
                .Should().Be(LiveIntakeOutcome.Offered);
            await h.RoundAsync();
            var patch = await NextOfAsync(a, "itemPatch");
            var full = (JObject)(await NextOfAsync(old, "item"))["item"]!;
            patch["baseRv"]!.Value<long>().Should().Be(rv, "接在網頁收到的上一則後面");
            patch["id"]!.Value<string>().Should().Be(full["id"]!.Value<string>());
            var next = ApplyPatch(shown, patch);
            JToken.DeepEquals(next, full).Should().BeTrue($"第 {step} 版：套用增量＝整筆");
            patch.ToString(Formatting.None).Length.Should().BeLessThan(full.ToString(Formatting.None).Length / 2);
            shown = next;
            rv = patch["rv"]!.Value<long>();
        }

        // 重新 open：conversation 屏障清掉 rv，open 之後補推的整筆帶目前的 rv，下一則增量接在它後面
        (await h.CmdAsync(a, "open", new { conversationId = C1 })).Should().BeNull();
        await h.RoundAsync();
        (await NextOfAsync(a, "conversation"))["conversationId"]!.Value<string>().Should().Be(C1);
        var reopened = await NextOfAsync(a, "item");
        h.Relay(live, C1, 1, 5, Running(Text(20) + "\n\n最後一段", 5)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        (await NextOfAsync(a, "itemPatch"))["baseRv"]!.Value<long>().Should().Be(reopened["rv"]!.Value<long>());
    }

    [Fact]
    public async Task 舊分頁只收64KB以內的格_patch1的分頁收到256KB()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive2(device);
        var (a, _) = await h.ReadyAsync("b1");
        var (old, _) = await h.ReadyAsync("b2", "s2");
        h.SetState(a, true, [C1], features: Patch1);
        await h.OpenAsync(a, C1);
        await h.OpenAsync(old, C1);

        h.Relay(live, C1, 1, 1, Running(new string('長', 30_000), 1)).Should().Be(LiveIntakeOutcome.Offered, "UTF-8 約 90 KB：live2 照收");
        await h.RoundAsync();
        (await NextOfAsync(a, "item"))["item"]!["html"]!.Value<string>().Should().Contain("長長長");
        (await old.TakeAsync(0)).Where(m => m["type"]!.Value<string>() is "item" or "itemPatch").Should().BeEmpty("沒宣告 patch1 的分頁照今天：不收超過 64 KB 的格");
    }
}
