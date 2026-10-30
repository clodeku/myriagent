using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 儀表板的「每週額度每 5% 的 token」（AgentHub.UsageSegments.cs）：用量快照（對話中的 rate_limit_event）開始一段、會話的增量依模型記進去、
/// 跨過 5% 的邊界就結一筆並主動送 usageSegments 給要過的檢視；usageSegments 指令回目前的彙總、進行中的段落與最近的紀錄
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubUsageSegmentsTests
{
    private static AgentUsage U(long input, long output, long cached) => new(input, output, cached, null);

    private static JObject RateLimit(double weekly) => new()
    {
        ["status"] = "allowed",
        ["unifiedWindows"] = new JObject
        {
            ["five_hour"] = new JObject { ["utilization"] = 0.3, ["resetsAt"] = 1793322000 },
            ["seven_day"] = new JObject { ["utilization"] = weekly, ["resetsAt"] = 1793696400 },
        },
    };

    [Fact]
    public async Task 跨過5百分比結一筆_依模型記token_主動送給要過的檢視()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "ready");
        await h.Main.WaitForAsync("init");
        var (_, session) = h.StartConversation(h.Main, h.Claude);

        var mark = h.Main.Mark();
        h.Command(h.Main, "usageSegments");
        var empty = await h.Main.WaitForAsync("usageSegments", since: mark);
        empty["usageSegments"]!.Value<int>("total").Should().Be(0);
        empty["usageSegments"]!.Value<string>("path").Should().EndWith(Path.Combine("usage-segments", "records.jsonl"));

        h.Ui.Invoke(() => h.Accounts.ReportSessionRateLimit("acc-claude", AgentProvider.Claude, RateLimit(0.123)));
        h.Raise(session, new AgentTokenUsageEvent(U(46_873, 6, 16_240), "claude-opus-5-5", 30_631));
        h.Raise(session, new AgentTokenUsageEvent(U(0, 267, 0), "claude-opus-5-5"));
        h.Raise(session, new AgentTokenUsageEvent(U(16_544, 138, 0), "claude-sonnet-5-5", 16_542));
        h.Raise(session, new AgentTokenUsageEvent(U(500, 10, 0)));   // 沒帶模型：用對話選的模型
        await Task.Delay(100);

        mark = h.Main.Mark();
        h.Ui.Invoke(() => h.Accounts.ReportSessionRateLimit("acc-claude", AgentProvider.Claude, RateLimit(0.151)));
        var pushed = await h.Main.WaitForAsync("usageSegments", since: mark);
        var view = (JObject)pushed["usageSegments"]!;
        view.Value<int>("total").Should().Be(1);
        var record = (JObject)((JArray)view["records"]!).Single();
        record.Value<string>("windowKey").Should().Be("weekly");
        record.Value<double>("fromBucket").Should().Be(10);
        record.Value<double>("toBucket").Should().Be(15);
        record.Value<double>("startPercent").Should().Be(12.3);
        record.Value<double>("endPercent").Should().Be(15.1);
        record.Value<bool>("complete").Should().BeFalse("開始追蹤時已在 10% 那一段的中間");
        var models = ((JArray)record["models"]!).Cast<JObject>().ToDictionary(m => m.Value<string>("model")!);
        models["claude-opus-5-5"].Value<long>("input").Should().Be(46_873);
        models["claude-opus-5-5"].Value<long>("output").Should().Be(273);
        models["claude-opus-5-5"].Value<long>("cacheWrite").Should().Be(30_631);
        models["claude-sonnet-5-5"].Value<long>("output").Should().Be(138);
        models.Should().HaveCount(3, "沒帶模型的那筆記在對話選的模型（沒選是未知的模型）");
        ((JArray)view["summary"]!).Should().BeEmpty("不完整的一段不算進彙總");
        var open = ((JArray)view["open"]!).Cast<JObject>().Single();
        open.Value<double>("bucket").Should().Be(15);
        open.Value<bool>("complete").Should().BeTrue();

        File.ReadAllLines(Path.Combine(AppPaths.UsageSegmentsDirectory, "records.jsonl")).Should().ContainSingle();
    }
}
