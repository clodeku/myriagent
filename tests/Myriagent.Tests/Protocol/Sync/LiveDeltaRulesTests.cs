using System.Text;
using Myriagent.Agents;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 即時更新的增量（live2；docs/plans/2026-10-23-live-delta-and-replica-live.md 第 3、6 節）：重組的結果與直接送整則逐位元組相同（固定種子的亂數編輯序列：
/// 中文、emoji 與代理對、CRLF、欄位出現與消失、前段被換掉、整欄重送），對不上的一律拒絕，依大小降頻、上限
/// </summary>
public sealed class LiveDeltaRulesTests
{
    private const string Conv = "0123456789abcdef0123456789abcdef";

    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);

    private static AgentChatItem Item(string? text, string? detail = null, string? output = null, AgentItemKind kind = AgentItemKind.Assistant) => new()
    {
        Id = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6",
        Ord = 42,
        Kind = kind,
        Status = AgentItemStatus.Running,
        Text = text,
        Detail = detail,
        Output = output,
        ToolName = kind == AgentItemKind.Tool ? "Bash" : null,
        CreatedAt = new DateTime(2026, 10, 23, 1, 2, 3, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 10, 23, 1, 2, 4, DateTimeKind.Utc),
    };

    private static JObject Wire(AgentChatItem item, long ver)
    {
        ConversationWire.TryItem(item, ver, out var wire).Should().BeTrue();
        return wire!;
    }

    [Fact]
    public void 亂數的編輯序列_每一步重組都和整則逐位元組相同_下一則的基底就是這一則()
    {
        var random = new Random(20261023);
        var pieces = new[] { "串流中的回覆，", "plain ascii ", Emoji, "\r\n", "第二行\n", "`code` ", "**粗體**", "|表格|", "\t", "a" };
        string? text = null, detail = null, output = null;
        var kind = AgentItemKind.Tool;
        var basis = LiveLongFields.Empty;
        long baseVer = 0;
        var deltaBytes = 0L;
        var fullBytes = 0L;
        for (var ver = 1L; ver <= 400; ver++)
        {
            var roll = random.Next(100);
            string Grow(string? s) => (s ?? string.Empty) + string.Concat(Enumerable.Range(0, random.Next(1, 6)).Select(_ => pieces[random.Next(pieces.Length)]));
            if (roll < 70)
                text = Grow(text);
            else if (roll < 78 && text is { Length: > 4 })
                text = text[..random.Next(text.Length / 2, text.Length)] + "［疑似權杖已遮蔽］";   // 遮蔽改到前面
            else if (roll < 84)
                output = Grow(output);
            else if (roll < 88)
                output = null;   // 欄位消失
            else if (roll < 93)
                detail = roll % 2 == 0 ? Grow(detail) : null;
            else if (roll < 96 && output is { Length: > 10 })
                output = output[3..] + "x";   // 滑動視窗：前綴每次都變
            else
                text = Grow(null);   // 整欄換掉

            var current = Wire(Item(text, detail, output, kind), ver);
            var full = ConversationWire.Serialize(current);
            var delta = LiveDeltaRules.Encode(Conv, 3, 42, ver, baseVer, basis, current);
            var rebuilt = LiveDeltaRules.Apply(basis, baseVer, delta, out var next, out var why);
            rebuilt.Should().Be(full, $"第 {ver} 步（{why}）");
            next.Get(LiveDeltaRules.TextField).Should().Be(text ?? string.Empty);
            next.Get(LiveDeltaRules.OutputField).Should().Be(output ?? string.Empty);
            next.Get(LiveDeltaRules.DetailField).Should().Be(detail ?? string.Empty);
            foreach (var op in delta.Ops)
            {
                var value = next.Get(op.F);
                value[..op.At].Should().Be(basis.Get(op.F)[..op.At]);
                if (op.At > 0 && op.At < value.Length)
                    char.IsLowSurrogate(value[op.At]).Should().BeFalse("切點不在代理對中間");
            }
            deltaBytes += Encoding.UTF8.GetByteCount(delta.Head) + delta.Ops.Sum(o => Encoding.UTF8.GetByteCount(o.S));
            fullBytes += Encoding.UTF8.GetByteCount(full);
            basis = next;
            baseVer = ver;
        }

        deltaBytes.Should().BeLessThan(fullBytes, "就算常常整欄換掉，增量也不比整則多");
    }

    [Fact]
    public void 持續成長的回覆_增量的流量是線性的_整則是平方()
    {
        var basis = LiveLongFields.Empty;
        var text = string.Empty;
        long deltaBytes = 0, fullBytes = 0;
        for (var ver = 1L; ver <= 200; ver++)
        {
            text += "這是一段串流中的回覆文字，每次多 100 個字左右。" + new string('長', 70);
            var current = Wire(Item(text), ver);
            var delta = LiveDeltaRules.Encode(Conv, 1, 42, ver, ver - 1, basis, current);
            LiveDeltaRules.Apply(basis, ver - 1, delta, out basis, out _).Should().NotBeNull();
            deltaBytes += Encoding.UTF8.GetByteCount(delta.Head) + delta.Ops.Sum(o => Encoding.UTF8.GetByteCount(o.S));
            fullBytes += Encoding.UTF8.GetByteCount(ConversationWire.Serialize(current));
        }

        var last = Encoding.UTF8.GetByteCount(text);
        deltaBytes.Should().BeLessThan(last * 2 + 200 * 512, "增量合計 ≤ 最後大小 × 2 ＋ 則數 × 512 B（設計第 6 節的門檻）");
        fullBytes.Should().BeGreaterThan(deltaBytes * 50, $"整則的合計是平方成長（{fullBytes / 1024} KB 對 {deltaBytes / 1024} KB）");
    }

    [Fact]
    public void 沒有基底_從空的開始也能重組_沒變的欄位不列op()
    {
        var current = Wire(Item("你好" + Emoji, output: "out"), 1);
        var first = LiveDeltaRules.Encode(Conv, 1, 42, 1, 0, LiveLongFields.Empty, current);
        first.Ops.Select(o => (o.F, o.At)).Should().Equal((LiveDeltaRules.TextField, 0), (LiveDeltaRules.OutputField, 0));
        LiveDeltaRules.Apply(LiveLongFields.Empty, 0, first, out var basis, out _).Should().Be(ConversationWire.Serialize(current));

        var next = Wire(Item("你好" + Emoji + "！", output: "out"), 2);
        var delta = LiveDeltaRules.Encode(Conv, 1, 42, 2, 1, basis, next);
        delta.Ops.Should().ContainSingle().Which.Should().Be(new LiveDeltaOp(LiveDeltaRules.TextField, 4, "！", 5), "output 沒變不列；新內容只有最後一個字");
        LiveDeltaRules.Apply(basis, 1, delta, out _, out _).Should().Be(ConversationWire.Serialize(next));
        delta.Head.Should().NotContain("\"text\"").And.Contain("\"ver\":2");
    }

    [Fact]
    public void CommonPrefix_不把代理對拆成兩半()
    {
        var high = (char)0xD83D;
        var a = "x" + high + (char)0xDE00;
        var b = "x" + high + (char)0xDE01;
        LiveDeltaRules.CommonPrefix(a, b).Should().Be(1, "第 2 個碼元是同一個高代理，但後半不同：退到字的開頭");
        LiveDeltaRules.CommonPrefix("abc", "abd").Should().Be(2);
        LiveDeltaRules.CommonPrefix("abc", "abc").Should().Be(3);
        LiveDeltaRules.CommonPrefix("", "abc").Should().Be(0);
        LiveDeltaRules.CommonPrefix("x" + Emoji, "x" + Emoji + "y").Should().Be(3, "整個 emoji 都相同");
    }

    [Fact]
    public void Apply_對不上的一律拒絕()
    {
        var basis = new LiveLongFields("基底" + Emoji, null, null);
        var head = ConversationWire.Serialize(HeadOf(Wire(Item("x"), 5)));
        string? Try(LiveDelta d, long basisVer = 4) => LiveDeltaRules.Apply(basis, basisVer, d, out _, out _);
        LiveDelta Delta(long baseVer = 4, long ver = 5, string? headJson = null, params LiveDeltaOp[] ops) => new(Conv, 1, 42, ver, baseVer, headJson ?? head, ops);
        var ok = new LiveDeltaOp(LiveDeltaRules.TextField, 4, "！", 5);

        Try(Delta(ops: ok)).Should().NotBeNull();
        Try(Delta(baseVer: 3, ops: ok)).Should().BeNull("baseVer 不是基底的版本");
        Try(Delta(ver: 4, headJson: ConversationWire.Serialize(HeadOf(Wire(Item("x"), 4))), ops: ok)).Should().BeNull("ver 沒有比 baseVer 新");
        Try(Delta(ops: new LiveDeltaOp(LiveDeltaRules.TextField, 5, "！", 6))).Should().BeNull("at 超出基底");
        Try(Delta(ops: new LiveDeltaOp(LiveDeltaRules.TextField, 3, "！", 4))).Should().BeNull("at 切在代理對中間");
        Try(Delta(ops: new LiveDeltaOp(LiveDeltaRules.TextField, 4, "！", 9))).Should().BeNull("n 對不上");
        Try(Delta(ops: new LiveDeltaOp("title", 0, "x", 1))).Should().BeNull("不是長欄位");
        Try(Delta(ops: [ok, ok])).Should().BeNull("同一欄兩個 op");
        Try(Delta(headJson: ConversationWire.Serialize(Wire(Item("x"), 5)), ops: ok)).Should().BeNull("head 含長欄位");
        Try(Delta(headJson: head.Replace("\"ord\":42", "\"ord\":43", StringComparison.Ordinal), ops: ok)).Should().BeNull("head 的 ord 與外層不同");
        Try(Delta(headJson: head.Replace("\"id\":", "\"evil\":1,\"id\":", StringComparison.Ordinal), ops: ok)).Should().BeNull("head 有不認得的欄位");
        Try(Delta(headJson: "[1]", ops: ok)).Should().BeNull("head 不是 JSON 物件");
        Try(Delta(headJson: "{\"ord\":42,", ops: ok)).Should().BeNull("head 不是合法的 JSON");
    }

    [Fact]
    public void ShouldSendFull_增量超過整則一半就送整則()
    {
        var current = Wire(Item(new string('字', 1000)), 2);
        var full = ConversationWire.Serialize(current).Length;
        var small = LiveDeltaRules.Encode(Conv, 1, 42, 2, 1, new LiveLongFields(new string('字', 990), null, null), current);
        LiveDeltaRules.ShouldSendFull(small, full).Should().BeFalse();
        var rewritten = LiveDeltaRules.Encode(Conv, 1, 42, 2, 1, new LiveLongFields("完全不同的開頭", null, null), current);
        LiveDeltaRules.ShouldSendFull(rewritten, full).Should().BeTrue("前綴幾乎都變了");
    }

    [Fact]
    public void DeltaIntervalMs_依大小降頻_MaxItemBytes依協商()
    {
        LiveRules.DeltaIntervalMs(10 * 1024, 8).Should().Be(125);
        LiveRules.DeltaIntervalMs(64 * 1024, 8).Should().Be(125);
        LiveRules.DeltaIntervalMs(64 * 1024 + 1, 8).Should().Be(250);
        LiveRules.DeltaIntervalMs(128 * 1024 + 1, 8).Should().Be(500);
        LiveRules.DeltaIntervalMs(1024, 2).Should().Be(500, "伺服器給的頻率比降頻更慢時照伺服器的");
        LiveRules.DeltaIntervalMs(1024, 0).Should().Be(0, "伺服器沒提供");
        LiveRules.MaxItemBytes(live2: false).Should().Be(64 * 1024);
        LiveRules.MaxItemBytes(live2: true).Should().Be(256 * 1024);
        SyncLimits.Default.MaxLiveHz.Should().Be(4, "舊桌面照它送整則，調高會讓舊桌面的流量加倍");
        SyncLimits.Default.MaxLiveDeltaHz.Should().Be(8);
    }

    [Fact]
    public void CheckRelay_上限依參數_沒有live2的照舊64KB()
    {
        var big = ConversationWire.Serialize(Wire(Item(new string('a', 100 * 1024)), 1));
        var relay = new LiveRelay(Conv, 1, 42, 1, big);
        LiveRules.CheckRelay(relay, out _).Should().Be(SyncErrorCodes.TooLarge, "沒有 live2：64 KB");
        LiveRules.CheckRelay(relay, out var detail, LiveRules.MaxDeltaItemBytes).Should().BeNull(detail);
        var huge = new LiveRelay(Conv, 1, 42, 1, ConversationWire.Serialize(Wire(Item(new string('a', 300 * 1024)), 1)));
        LiveRules.CheckRelay(huge, out _, LiveRules.MaxDeltaItemBytes).Should().Be(SyncErrorCodes.TooLarge, "超過 256 KB 照舊不送");
    }

    [Fact]
    public void 功能與方法_LiveDelta與LiveResync要live2()
    {
        DeviceFeatureRules.Known.Should().Contain(DeviceFeatureRules.Live2);
        DeviceFeatureRules.RequiredFor(DeviceHubMethods.LiveDelta).Should().Be(DeviceFeatureRules.Live2);
        DeviceFeatureRules.RequiredFor(DeviceHubMethods.LiveResync).Should().Be(DeviceFeatureRules.Live2);
        LiveResyncReasons.IsKnown(LiveResyncReasons.Remask).Should().BeTrue();
        LiveResyncReasons.IsKnown("x").Should().BeFalse();
    }

    private static JObject HeadOf(JObject item)
    {
        var head = (JObject)item.DeepClone();
        foreach (var f in LiveDeltaRules.Fields)
            head.Remove(f);
        return head;
    }
}
