using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Usage;

/// <summary>每週 5% 分段紀錄的存檔：每個每週額度窗各記一段、token 加到這個帳戶的每一段、跨過邊界就附加到 records.jsonl、open.json 重開後接著記</summary>
public sealed class UsageSegmentStoreTests : IDisposable
{
    private readonly TempDirectory temp = new("usage-segments");

    public void Dispose() => temp.Dispose();

    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset Resets = new(2026, 10, 30, 1, 0, 0, TimeSpan.Zero);

    private static readonly AgentAccount Account = new() { Id = "a1", Provider = AgentProvider.Claude, Label = "Claude 甲", Email = "a@example.com" };

    private static UsageSnapshot Snap(double weekly, double opus, double session = 50)
        => new(T0, "api", new List<UsageWindow>
        {
            new("session", "5 小時", session, Resets),
            new("weekly", "每週", weekly, Resets),
            new("weekly:Opus", "每週・Opus", opus, Resets),
        }, null, null, null, false);

    private static AgentUsage U(long input, long output, long cached) => new(input, output, cached, null);

    [Fact]
    public void 每個每週額度窗各記一段_跨過邊界附加一行_重開後接著記()
    {
        var dir = temp.Combine("usage-segments");
        var store = new UsageSegmentStore(dir);
        store.AddTokens("a1", "claude-opus-5-5", U(1, 1, 0), 0);
        store.OpenSegments().Should().BeEmpty("還沒看過使用率：沒有段落，token 不記");

        store.Observe(Account, Snap(12, 3), T0).Should().BeEmpty();
        store.OpenSegments().Select(s => s.WindowKey).Should().BeEquivalentTo(new[] { "weekly", "weekly:Opus" }, "5 小時的不記");
        store.AddTokens("a1", "claude-opus-5-5", U(10_000, 100, 8_000), 1_500);
        store.AddTokens("a2", "claude-opus-5-5", U(99, 9, 0), 0);
        store.Flush();

        var reopened = new UsageSegmentStore(dir);
        reopened.OpenSegments().Single(s => s.WindowKey == "weekly").Models["claude-opus-5-5"].Input.Should().Be(10_000, "open.json 接著記");
        var closed = reopened.Observe(Account, Snap(15, 4), T0.AddHours(1), () => "id-1");
        closed.Should().ContainSingle().Which.Should().Match<UsageSegmentRecord>(r => r.Id == "id-1" && r.WindowKey == "weekly" && r.FromBucket == 10
            && r.ToBucket == 15 && r.AccountLabel == "Claude 甲" && r.Provider == "Claude" && r.Email == "a@example.com");

        var lines = File.ReadAllLines(Path.Combine(dir, "records.jsonl"));
        lines.Should().ContainSingle();
        var json = JObject.Parse(lines[0]);
        json.Value<string>("accountId").Should().Be("a1", "camelCase");
        json["models"]!["claude-opus-5-5"]!.Value<long>("cacheWrite").Should().Be(1_500, "模型名稱照原樣");
        json.Value<double>("startPercent").Should().Be(12);

        reopened.Records().Should().ContainSingle();
        new UsageSegmentStore(dir).Records().Should().ContainSingle().Which.Models["claude-opus-5-5"].Output.Should().Be(100, "從 records.jsonl 讀回");
    }

    [Fact]
    public void 錯誤的快照不看_移除帳戶後不再記_壞掉的行略過()
    {
        var dir = temp.Combine("usage-segments");
        var store = new UsageSegmentStore(dir);
        store.Observe(Account, Snap(12, 3) with { Error = "查不到" }, T0).Should().BeEmpty();
        store.OpenSegments().Should().BeEmpty();
        store.Observe(Account, Snap(12, 3), T0);
        store.RemoveAccount("a1");
        store.OpenSegments().Should().BeEmpty();

        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "records.jsonl"), "{\"id\":\"ok\",\"accountId\":\"a1\"}\n{壞掉\n");
        new UsageSegmentStore(dir).Records().Should().ContainSingle().Which.Id.Should().Be("ok");
    }
}
