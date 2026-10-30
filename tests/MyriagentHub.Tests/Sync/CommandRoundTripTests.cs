using System.Diagnostics;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using static MyriagentHub.Tests.Sync.CommandRouterTests;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// <c>send</c> 立即拒絕時 <c>Cmd</c> 的往返時間（WP4.5 計畫步 6 的完成條件：p95 ≤ 50 ms）。放在量測的集合（<see cref="PerfCollection"/>）：
/// 平行的集合都跑完之後才跑、不和其他測試同時跑，量到的不受其他測試的負載影響（步 9 起完整測試多了幾個等候較長的類別，和它同時跑時 p95 偶爾超過 50 ms）。
/// 不看 <c>MYRIAGENT_HUB_PERF</c>，每次都跑
/// </summary>
[Collection(PerfCollection.Name)]
public sealed class CommandRoundTripTests(ITestOutputHelper output)
{
    [Fact]
    public async Task send立即拒絕的往返時間_p95不超過50ms()
    {
        await using var s = await Setup.StartAsync(connect: false);
        var samples = new List<double>();
        for (var i = 0; i < 100; i++)
        {
            var watch = Stopwatch.StartNew();
            (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "hi" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        var p50 = samples[49];
        var p95 = samples[94];
        output.WriteLine($"send 立即拒絕（offline）的 Cmd 往返 100 次：p50 {p50:F1} ms、p95 {p95:F1} ms、最大 {samples[^1]:F1} ms");
        p95.Should().BeLessThanOrEqualTo(50);
        s.Counters.Snapshot().RejectedAtServer.Should().Be(100);
    }
}
