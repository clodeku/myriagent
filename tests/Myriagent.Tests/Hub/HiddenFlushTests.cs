using System.Diagnostics;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>背景分頁的串流更新節流（HiddenFlushRules）：沒顯示在任何視窗的對話每 500 ms 最多送一次項目更新，最後的內容一定送到，切回來立刻送</summary>
[Collection(AppPathsCollection.Name)]
public sealed class HiddenFlushTests
{
    [Fact]
    public void Defer_只有每個view都回報過visible而且都沒顯示_距上次不到間隔才延後()
    {
        HiddenFlushRules.Defer(shownAnywhere: false, allViewsReportVisibility: true, lastFlushMs: 1000, nowMs: 1200).Should().BeTrue();
        HiddenFlushRules.Defer(false, true, 1000, 1000 + HiddenFlushRules.IntervalMs).Should().BeFalse("滿間隔了");
        HiddenFlushRules.Defer(false, true, null, 1200).Should().BeFalse("背景之後的第一次照送");
        HiddenFlushRules.Defer(true, true, 1000, 1200).Should().BeFalse("有視窗顯示著");
        HiddenFlushRules.Defer(false, false, 1000, 1200).Should().BeFalse("有 view 沒回報過 visible：不知道看不看得到");
    }

    [Fact]
    public async Task 背景分頁_項目更新每500ms最多一次_最後的內容一定送到_切回來立刻送()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        // 切到別的分頁（或儀表板）：網頁回報畫面上沒有這個對話
        h.Command(h.Main, "visible", new { conversationIds = Array.Empty<string>() });
        var mark = h.Main.Mark();

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            h.Raise(s, new AgentTextDeltaEvent("b1", "字"));
            await Task.Delay(40);
        }
        var streamed = clock.ElapsedMilliseconds;
        await Task.Delay(HiddenFlushRules.IntervalMs + 300);

        var sent = h.Main.Messages("item", mark, id).Where(m => m["item"]!.Value<string>("kind") == "Assistant").ToList();
        sent.Should().NotBeEmpty();
        sent.Count.Should().BeLessThanOrEqualTo((int)(streamed / HiddenFlushRules.IntervalMs) + 2, $"串流 {streamed} ms，背景分頁不再每 50 ms 送一次");
        sent[^1]["item"]!.Value<string>("html").Should().Contain(new string('字', 20), "最後的內容一定送到");

        // 切回來：節流中的更新立刻送（不等滿 500 ms）
        h.Raise(s, new AgentTextDeltaEvent("b1", "尾"));
        var back = h.Main.Mark();
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        var shown = await h.Main.WaitForAsync("item", m => (m["item"]!.Value<string>("html") ?? "").Contains("字尾"), since: back, timeoutMs: 300, conversationId: id);
        shown.Should().NotBeNull();
    }

    [Fact]
    public async Task 背景分頁_標頭每500ms最多一次_最後的標頭一定送到_切回來立刻送()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "visible", new { conversationIds = Array.Empty<string>() });
        var mark = h.Main.Mark();

        // 上下文用量每 40 ms 變一次（實機的狀態文字、上下文用量一秒好幾次）：每次都讓標頭變動
        var clock = Stopwatch.StartNew();
        for (var i = 1; i <= 20; i++)
        {
            h.Raise(s, new AgentContextUsageEvent(1000 * i, 200_000));
            await Task.Delay(40);
        }
        var streamed = clock.ElapsedMilliseconds;
        await Task.Delay(HiddenFlushRules.IntervalMs + 300);

        var sent = h.Main.Messages("header", mark, id);
        sent.Should().NotBeEmpty();
        sent.Count.Should().BeLessThanOrEqualTo((int)(streamed / HiddenFlushRules.IntervalMs) + 2, $"變動 {streamed} ms，背景分頁的標頭不再每 50 ms 送一次");
        sent[^1]["header"]!["context"]!.Value<long>("used").Should().Be(20_000, "最後的標頭一定送到");

        // 切回來：節流中的標頭立刻送（不等滿 500 ms）
        h.Raise(s, new AgentContextUsageEvent(99_000, 200_000));
        var back = h.Main.Mark();
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        var shown = await h.Main.WaitForAsync("header", m => m["header"]?["context"]?.Value<long?>("used") == 99_000, since: back, timeoutMs: 300, conversationId: id);
        shown.Should().NotBeNull();
    }

    [Fact]
    public async Task 顯示中的對話照舊每次合併都送()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        var mark = h.Main.Mark();
        for (var i = 0; i < 8; i++)
        {
            h.Raise(s, new AgentTextDeltaEvent("b1", "字"));
            await Task.Delay(120);
        }
        await Task.Delay(200);
        h.Main.Messages("item", mark, id).Count(m => m["item"]!.Value<string>("kind") == "Assistant").Should().BeGreaterThanOrEqualTo(6, "每 120 ms 一個片段、每 50 ms 合併一次");
    }
}
