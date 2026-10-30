using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// 複本的 blob 預取的純函式（WP4.7 計畫步 12，D39）：收集與每頁上限、暫時取不到時串流停住、停住的事件不套用、游標不越過停住那筆、取到的內容換回 payload
/// </summary>
public sealed class ReplicaBlobRulesTests
{
    private static readonly string Other = SyncIds.NewDeviceId();

    private const string IdA = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8";

    private const string IdB = "1a2b3c4d5e6f40718293a4b5c6d7e8f9";

    private static string StreamA => SyncStreams.Conversation(IdA);

    private static string StreamB => SyncStreams.Conversation(IdB);

    private static string Sha(int n) => Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(n)));

    private static PulledEvent Event(long seq, string id, string kind, string json, string? author = null)
    {
        var (cls, stream) = ConversationEventKinds.Placement(kind, id) ?? throw new ArgumentException("不是對話事件", nameof(kind));
        var (enc, text) = PayloadRules.Encode(json);
        return new PulledEvent(seq, stream, cls, author ?? Other, kind, enc, text, 0, id, 1);
    }

    /// <summary>conv.item 的 payload：每個欄位是一個 blob 參照（只看參照，不驗項目的形狀）</summary>
    private static PulledEvent RefItem(long seq, string id, params (string Field, string Sha, long Length)[] refs)
    {
        var o = new JObject { ["id"] = "i" + seq, ["ord"] = seq };
        foreach (var (field, sha, length) in refs)
            o[field] = new JObject { ["blob"] = sha, ["length"] = length };
        return Event(seq, id, ConversationEventKinds.Item, o.ToString(Formatting.None));
    }

    private static Dictionary<string, ReplicaBlobFetch> Results(params (string Sha, ReplicaBlobFetch Fetch)[] results)
        => results.ToDictionary(r => r.Sha, r => r.Fetch, StringComparer.Ordinal);

    // ───────────────────────────── Plan ─────────────────────────────

    [Fact]
    public void Plan_依序收集_同一個內容只取一次_沒有blob的不列()
    {
        var events = new[]
        {
            RefItem(1, IdA, ("text", Sha(1), 100), ("output", Sha(2), 200)),
            Event(2, IdA, ConversationEventKinds.Meta, "{}"),
            RefItem(3, IdA, ("text", Sha(1), 100)),
            Event(4, IdB, ConversationEventKinds.Item, """{"id":"i4","ord":4,"text":"短的"}"""),
            RefItem(5, IdB, ("diff", Sha(3), 300)),
        };

        var plan = ReplicaBlobRules.Plan(events, _ => true);

        plan.Wants.Should().Equal(new ReplicaBlobWant(Sha(1), 100), new ReplicaBlobWant(Sha(2), 200), new ReplicaBlobWant(Sha(3), 300));
        plan.Events.Select(e => (e.Stream, e.Seq, string.Join(",", e.Shas))).Should().Equal(
            (StreamA, 1L, Sha(1) + "," + Sha(2)),
            (StreamA, 3L, Sha(1)),
            (StreamB, 5L, Sha(3)));
        plan.BudgetHolds.Should().BeEmpty();
    }

    [Fact]
    public void Plan_合計上限_放不下的那筆起那個串流停住_其他串流照收()
    {
        var events = new[]
        {
            RefItem(1, IdA, ("text", Sha(1), 4)),
            RefItem(2, IdA, ("text", Sha(2), 4)),
            RefItem(3, IdA, ("text", Sha(3), 4)),
            RefItem(4, IdA, ("text", Sha(4), 1)),
            RefItem(5, IdB, ("text", Sha(5), 2)),
        };

        var plan = ReplicaBlobRules.Plan(events, _ => true, maxPageBytes: 10, maxBlobBytes: 4);

        plan.Wants.Select(w => w.Sha256).Should().Equal(Sha(1), Sha(2), Sha(5));
        plan.Wants.Sum(w => w.Length).Should().BeLessThanOrEqualTo(10);
        plan.BudgetHolds.Should().ContainSingle().Which.Value.Should().Be(new ReplicaBlobHold(StreamA, IdA, 3, Transient: false));
    }

    [Fact]
    public void Plan_單一筆就放不下或有超過單一上限的blob_那筆不取也不停住()
    {
        var events = new[]
        {
            RefItem(1, IdA, ("text", Sha(1), 5)),
            RefItem(2, IdA, ("text", Sha(2), 4), ("output", Sha(3), 4)),
            RefItem(3, IdA, ("text", Sha(4), 3)),
        };

        var plan = ReplicaBlobRules.Plan(events, _ => true, maxPageBytes: 6, maxBlobBytes: 4);

        plan.Wants.Select(w => w.Sha256).Should().Equal(Sha(4));
        plan.Events.Select(e => e.Seq).Should().Equal(3);
        plan.BudgetHolds.Should().BeEmpty("永久放不下的當成轉不回來，不卡住串流");
    }

    [Fact]
    public void Plan_不合格的與不符合條件的不管()
    {
        var garbage = new PulledEvent(2, StreamA, SyncClasses.Item, Other, ConversationEventKinds.Item, "gzip", "不是 base64", 0, IdA, 1);
        var notJson = Event(3, IdA, ConversationEventKinds.Item, "{ 不是 JSON");
        var duplicateKey = Event(4, IdA, ConversationEventKinds.Item, $$"""{"text":{"blob":"{{Sha(9)}}","length":1},"text":"x"}""");
        var mine = RefItem(5, IdB, ("text", Sha(5), 1));
        var events = new[] { RefItem(1, IdA, ("text", Sha(1), 1)), garbage, notJson, duplicateKey, mine };

        var plan = ReplicaBlobRules.Plan(events, e => e.Seq != 5);

        plan.Wants.Select(w => w.Sha256).Should().Equal(Sha(1));
    }

    [Fact]
    public void HoldsWithoutMedia_要取的與放不下的那筆起都停住_一律算暫時的()
    {
        // 合併前審查 DI-3、INT-6：沒有 media1 的連線一個都取不到，不能照永久取不到前進
        var events = new[]
        {
            Event(1, IdA, ConversationEventKinds.Item, """{"id":"i1","ord":1,"text":"短的"}"""),
            RefItem(2, IdA, ("text", Sha(1), 4)),
            RefItem(3, IdA, ("text", Sha(2), 1)),
            RefItem(4, IdB, ("text", Sha(3), 3)),
            RefItem(5, IdB, ("text", Sha(4), 3)),
        };
        var plan = ReplicaBlobRules.Plan(events, _ => true, maxPageBytes: 9, maxBlobBytes: 4);
        plan.Events.Select(e => e.Seq).Should().Equal(2, 3, 4);
        plan.BudgetHolds.Should().ContainSingle().Which.Value.Should().Be(new ReplicaBlobHold(StreamB, IdB, 5, Transient: false), "對照：放不下的");

        var holds = ReplicaBlobRules.HoldsWithoutMedia(plan);

        holds.Should().HaveCount(2);
        holds[StreamA].Should().Be(new ReplicaBlobHold(StreamA, IdA, 2, Transient: true), "第一筆帶 blob 參照的起");
        holds[StreamB].Should().Be(new ReplicaBlobHold(StreamB, IdB, 4, Transient: true), "同一個串流取最早的那筆");
        ReplicaBlobRules.HoldsWithoutMedia(ReplicaBlobRules.Plan([events[0]], _ => true)).Should().BeEmpty("沒有 blob 的頁不停");
        ReplicaBlobRules.HoldsWithoutMedia(ReplicaBlobRules.Plan([RefItem(6, IdA, ("text", Sha(5), 99))], _ => true, maxBlobBytes: 4))
            .Should().BeEmpty("超過單一上限的永久轉不回來，有沒有 media1 都一樣");

        var budgetOnly = ReplicaBlobRules.HoldsWithoutMedia(new ReplicaBlobPlan([], [], new Dictionary<string, ReplicaBlobHold> { [StreamB] = new(StreamB, IdB, 7, Transient: false) }));
        budgetOnly.Should().ContainSingle().Which.Value.Transient.Should().BeTrue("放不下的也是暫時的（不馬上重拉，不然沒有 media1 時會一直重拉）");
    }

    // ───────────────────────────── Holds、Resolver、Verify ─────────────────────────────

    [Fact]
    public void Holds_暫時取不到或沒有結果的那筆起停住_同串流取最早_永久取不到的不停()
    {
        var events = new[]
        {
            RefItem(1, IdA, ("text", Sha(1), 1)),
            RefItem(2, IdA, ("text", Sha(2), 1)),
            RefItem(3, IdA, ("text", Sha(3), 1)),
            RefItem(4, IdB, ("text", Sha(4), 1)),
            RefItem(5, IdB, ("text", Sha(5), 1)),
        };
        var plan = ReplicaBlobRules.Plan(events, _ => true);
        var results = Results(
            (Sha(1), ReplicaBlobFetch.Fetched([1])),
            (Sha(2), ReplicaBlobFetch.Failed),
            (Sha(3), ReplicaBlobFetch.Failed),
            (Sha(4), ReplicaBlobFetch.Missing));

        var holds = ReplicaBlobRules.Holds(plan, results);

        holds.Should().BeEquivalentTo(new Dictionary<string, ReplicaBlobHold>
        {
            [StreamA] = new(StreamA, IdA, 2, Transient: true),
            [StreamB] = new(StreamB, IdB, 5, Transient: true),
        });
    }

    [Fact]
    public void Holds_放不下的照留_比它早的暫時失敗優先()
    {
        var plan = ReplicaBlobRules.Plan([RefItem(1, IdA, ("text", Sha(1), 4)), RefItem(2, IdA, ("text", Sha(2), 4))], _ => true, 4, 4);
        plan.BudgetHolds.Should().ContainKey(StreamA).WhoseValue.Seq.Should().Be(2);

        ReplicaBlobRules.Holds(plan, Results((Sha(1), ReplicaBlobFetch.Fetched([1])))).Should().BeEquivalentTo(new Dictionary<string, ReplicaBlobHold>
        {
            [StreamA] = new(StreamA, IdA, 2, Transient: false),
        });
        ReplicaBlobRules.Holds(plan, Results((Sha(1), ReplicaBlobFetch.Failed))).Should().BeEquivalentTo(new Dictionary<string, ReplicaBlobHold>
        {
            [StreamA] = new(StreamA, IdA, 1, Transient: true),
        });
    }

    [Fact]
    public void Resolver只給取到的()
    {
        var resolve = ReplicaBlobRules.Resolver(Results(
            (Sha(1), ReplicaBlobFetch.Fetched([7, 8])),
            (Sha(2), ReplicaBlobFetch.Missing),
            (Sha(3), ReplicaBlobFetch.Failed)));

        resolve(Sha(1)).Should().Equal(7, 8);
        resolve(Sha(2)).Should().BeNull();
        resolve(Sha(3)).Should().BeNull();
        resolve(Sha(4)).Should().BeNull();
    }

    [Fact]
    public void Verify_大小與SHA256都要對()
    {
        var content = Encoding.UTF8.GetBytes("內容");
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));

        ReplicaBlobRules.Verify(new ReplicaBlobWant(sha, content.Length), content).Should().BeTrue();
        ReplicaBlobRules.Verify(new ReplicaBlobWant(sha, content.Length + 1), content).Should().BeFalse();
        ReplicaBlobRules.Verify(new ReplicaBlobWant(Sha(1), content.Length), content).Should().BeFalse();
        ReplicaBlobRules.Verify(new ReplicaBlobWant(sha.ToUpperInvariant(), content.Length), content).Should().BeFalse("參照一律小寫");
    }

    // ───────────────────────────── WithoutHeld、CapHeld ─────────────────────────────

    [Fact]
    public void WithoutHeld_停住的串流從那筆起拿掉_meta類照套用()
    {
        var a1 = RefItem(1, IdA, ("text", Sha(1), 1));
        var a2 = RefItem(2, IdA, ("text", Sha(2), 1));
        var a3 = Event(3, IdA, ConversationEventKinds.Meta, "{}");
        var a4 = Event(4, IdA, ConversationEventKinds.ItemRemoved, """{"id":"i1"}""");
        var b5 = RefItem(5, IdB, ("text", Sha(5), 1));
        var events = new[] { a1, a2, a3, a4, b5 };
        var holds = new Dictionary<string, ReplicaBlobHold>(StringComparer.Ordinal) { [StreamA] = new(StreamA, IdA, 2, true) };

        ReplicaBlobRules.WithoutHeld(events, holds, new HashSet<string> { SyncClasses.Meta }).Should().Equal(a1, a3, b5);
        ReplicaBlobRules.WithoutHeld(events, holds, new HashSet<string>()).Should().Equal(new[] { a1, b5 }, "這一頁沒有要 meta 類：meta 只經串流拉到");
        ReplicaBlobRules.WithoutHeld(events, new Dictionary<string, ReplicaBlobHold>(), new HashSet<string>()).Should().BeSameAs(events);
    }

    [Fact]
    public void CapHeld_停住的串流只前進到那筆之前()
    {
        var page = new[]
        {
            RefItem(11, IdA, ("text", Sha(1), 1)),
            RefItem(12, IdA, ("text", Sha(2), 1)),
            RefItem(13, IdA, ("text", Sha(3), 1)),
            RefItem(14, IdB, ("text", Sha(4), 1)),
        };
        var next = new Dictionary<string, long> { [SyncClasses.Meta] = 14, [StreamA] = 13, [StreamB] = 14 };
        var previous = new Dictionary<string, long> { [SyncClasses.Meta] = 3, [StreamA] = 5 };

        ReplicaBlobRules.CapHeld(next, previous, page, Hold(12)).Should().BeEquivalentTo(new Dictionary<string, long> { [SyncClasses.Meta] = 14, [StreamA] = 11, [StreamB] = 14 });
        ReplicaBlobRules.CapHeld(next, previous, page, Hold(11)).Should().BeEquivalentTo(new Dictionary<string, long> { [SyncClasses.Meta] = 14, [StreamA] = 5, [StreamB] = 14 },
            "停在這一頁的第一筆：維持原本的游標");
        ReplicaBlobRules.CapHeld(next, new Dictionary<string, long>(), page, Hold(11)).Should().BeEquivalentTo(new Dictionary<string, long> { [SyncClasses.Meta] = 14, [StreamB] = 14 },
            "原本沒有游標：拿掉（下次從頭拉）");
        ReplicaBlobRules.CapHeld(next, previous, page, new Dictionary<string, ReplicaBlobHold>()).Should().BeSameAs(next);

        static Dictionary<string, ReplicaBlobHold> Hold(long seq) => new(StringComparer.Ordinal) { [StreamA] = new(StreamA, IdA, seq, true) };
    }

    // ───────────────────────────── Inline ─────────────────────────────

    /// <summary>擁有者那邊的大項目：text 與 diff 都超過內嵌上限，換成 blob 參照（步 10 的發布路徑）</summary>
    private static (JObject Payload, string Json, Dictionary<string, byte[]> Blobs, AgentChatItem Source) BigItem()
    {
        var source = new AgentChatItem
        {
            Id = "i1", Ord = 1, Kind = AgentItemKind.Tool, Status = AgentItemStatus.Done, ToolName = "Edit", Title = "改檔",
            Text = new string('長', 20_000),
            Diff = [new AgentDiffLine("add", new string('x', 40_000)), new AgentDiffLine("ctx", "短的")],
            CreatedAt = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc),
        };
        ConversationWire.TryItem(source, 1, out var wire).Should().BeTrue();
        ConversationEventRules.TryFit(wire!, out var blobs).Should().BeTrue();
        blobs.Should().HaveCount(2, "text 與 diff");
        var json = ConversationWire.Serialize(wire!);
        return ((JObject)JToken.Parse(json), json, blobs.ToDictionary(b => b.Sha256, b => b.Content, StringComparer.Ordinal), source);
    }

    [Fact]
    public void Inline_取到就換回內容_字串與陣列_其他欄位照原樣()
    {
        var (payload, json, blobs, source) = BigItem();
        ReplicaRules.ToItem(json).Should().BeNull("陰性對照：沒換回的轉不回來（WP4.3 的行為）");

        var stored = ReplicaBlobRules.Inline(payload, json, blobs.GetValueOrDefault);

        var o = (JObject)JToken.Parse(stored);
        ReplicaBlobRules.Collect(o).Should().BeEmpty();
        o.Value<string>("text").Should().Be(source.Text);
        o["diff"].Should().BeOfType<JArray>();
        o.Properties().Select(p => p.Name).Should().Equal(payload.Properties().Select(p => p.Name), "欄位順序照原樣");
        var item = ReplicaRules.ToItem(stored)!;
        item.Text.Should().Be(source.Text);
        item.Diff.Should().Equal(source.Diff);
        item.ToolName.Should().Be("Edit");
        payload.ToString(Formatting.None).Should().Be(json, "不改傳進來的 payload");
    }

    [Fact]
    public void Inline_有一個取不到或內容不符_回原本的()
    {
        var (payload, json, blobs, _) = BigItem();
        var first = blobs.Keys.First();

        ReplicaBlobRules.Inline(payload, json, sha => sha == first ? null : blobs[sha]).Should().BeSameAs(json);
        ReplicaBlobRules.Inline(payload, json, sha => sha == first ? Encoding.UTF8.GetBytes("被換掉") : blobs[sha]).Should().BeSameAs(json);
        ReplicaBlobRules.Inline(payload, json, null).Should().BeSameAs(json);
    }

    [Fact]
    public void Inline_陣列欄位的內容不是陣列_回原本的()
    {
        var content = Encoding.UTF8.GetBytes("\"不是陣列\"");
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var json = new JObject
        {
            ["id"] = "i1", ["ord"] = 1, ["ver"] = 1, ["kind"] = "Tool", ["status"] = "Done", ["createdAt"] = 0, ["updatedAt"] = 0,
            ["diff"] = new JObject { ["blob"] = sha, ["length"] = content.Length },
        }.ToString(Formatting.None);

        ReplicaBlobRules.Inline((JObject)JToken.Parse(json), json, s => s == sha ? content : null).Should().BeSameAs(json);
    }

    [Fact]
    public void Inline_沒有blob參照_回原本的()
    {
        var source = new AgentChatItem { Id = "i1", Ord = 1, Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Done, Text = "短的" };
        ConversationWire.TryItem(source, 1, out var wire).Should().BeTrue();
        var json = ConversationWire.Serialize(wire!);

        ReplicaBlobRules.Inline(wire!, json, _ => throw new InvalidOperationException("沒有參照就不取")).Should().BeSameAs(json);
    }
}
