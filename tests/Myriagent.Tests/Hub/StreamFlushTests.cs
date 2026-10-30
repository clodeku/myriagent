using System.Diagnostics;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>很長的執行中項目依長度降頻（StreamFlushRules）：短的照舊每次合併都送，長的 100／200 ms 才送一次，結束時馬上送最後的內容</summary>
[Collection(AppPathsCollection.Name)]
public sealed class StreamFlushTests
{
    [Fact]
    public void IntervalMs_依長度分三段_Defer只在執行中而且送過才延後()
    {
        StreamFlushRules.IntervalMs(0).Should().Be(0);
        StreamFlushRules.IntervalMs(StreamFlushRules.ShortChars).Should().Be(0);
        StreamFlushRules.IntervalMs(StreamFlushRules.ShortChars + 1).Should().Be(100);
        StreamFlushRules.IntervalMs(StreamFlushRules.MediumChars).Should().Be(100);
        StreamFlushRules.IntervalMs(StreamFlushRules.MediumChars + 1).Should().Be(200);

        StreamFlushRules.Defer(running: true, textChars: 40_000, lastFlushMs: 1000, nowMs: 1150).Should().BeTrue();
        StreamFlushRules.Defer(true, 40_000, 1000, 1200).Should().BeFalse("滿 200 ms");
        StreamFlushRules.Defer(true, 40_000, null, 1150).Should().BeFalse("第一次照送");
        StreamFlushRules.Defer(false, 40_000, 1000, 1010).Should().BeFalse("結束了：最後的內容馬上送");
        StreamFlushRules.Defer(true, 100, 1000, 1010).Should().BeFalse("短的每次合併都送");
    }

    [Fact]
    public async Task 長回覆串流時每200ms最多送一次_結束時馬上送最後的內容()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        h.Raise(s, new AgentTextDeltaEvent("b1", new string('長', StreamFlushRules.MediumChars + 10)));
        await Task.Delay(150);
        var mark = h.Main.Mark();

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 25; i++)
        {
            h.Raise(s, new AgentTextDeltaEvent("b1", "字"));
            await Task.Delay(40);
        }
        var streamed = clock.ElapsedMilliseconds;
        await Task.Delay(300);
        var sent = h.Main.Messages("item", mark, id).Where(m => m["item"]!.Value<string>("kind") == "Assistant").ToList();
        sent.Count.Should().BeLessThanOrEqualTo((int)(streamed / 200) + 2, $"串流 {streamed} ms，超過 {StreamFlushRules.MediumChars} 字的每 200 ms 才送一次（以前每 50 ms）");
        sent[^1]["item"]!.Value<string>("html").Should().Contain(new string('字', 25), "留著沒送的下一輪照送");

        var done = h.Main.Mark();
        h.CompleteTurn(s);
        var final = await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("kind") == "Assistant" && m["item"]!.Value<string>("status") != "Running",
            since: done, timeoutMs: 300, conversationId: id);
        final.Should().NotBeNull("結束時馬上送");
    }
}
