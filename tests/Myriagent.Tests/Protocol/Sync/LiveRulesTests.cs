using Myriagent.Agents;
using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>即時更新的合併、作廢、大小與頻率（WP4.1 步 4；docs/sync-protocol.md 14.3）：只是提示，可合併可丟，持久事件到了以它為準</summary>
public sealed class LiveRulesTests
{
    private const string C1 = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8";

    private const string C2 = "1f3c9a52b7e94d0c8a61f2d4e5b6c7d8";

    private sealed record Update(string Name, LiveVersion Version);

    private static Update U(string name, string conv, long ord, long ver, long epoch = 1) => new(name, new LiveVersion(conv, epoch, ord, ver));

    [Fact]
    public void 合併_同一筆只留最新_不同項目的順序不變()
    {
        var merged = LiveRules.Merge(
            [U("a1", C1, 1, 1), U("b1", C1, 2, 1), U("a3", C1, 1, 3), U("c1", C2, 1, 1), U("a2", C1, 1, 2), U("b2", C1, 2, 2)],
            u => u.Version);

        merged.Select(u => u.Name).Should().Equal(["a3", "b2", "c1"], "a 留 ver 3（晚到的 ver 2 比較舊）、b 留 ver 2，位置是第一次出現的位置；不同對話的同一個 ord 是不同項目");
    }

    [Fact]
    public void 合併_版本相同時留後到的_世代大的勝()
    {
        LiveRules.Merge([U("first", C1, 1, 5), U("second", C1, 1, 5)], u => u.Version).Single().Name.Should().Be("second");
        LiveRules.Merge([U("new-owner", C1, 1, 1, epoch: 2), U("old-owner", C1, 1, 99, epoch: 1)], u => u.Version).Single().Name
            .Should().Be("new-owner", "換了擁有者後 ver 從頭算，世代大的一律比較新");
        LiveRules.Merge(Array.Empty<Update>(), u => u.Version).Should().BeEmpty();
    }

    [Fact]
    public void 比較新舊()
    {
        var v = new LiveVersion(C1, 1, 7, 10);

        LiveRules.IsNewer(v with { Ver = 11 }, v).Should().BeTrue();
        LiveRules.IsNewer(v, v).Should().BeFalse("完全相同不算新");
        LiveRules.IsNewer(v with { Ver = 9 }, v).Should().BeFalse();
        LiveRules.IsNewer(v with { Epoch = 2, Ver = 1 }, v).Should().BeTrue();
        LiveRules.IsSameItem(v, v with { Ver = 1 }).Should().BeTrue();
        LiveRules.IsSameItem(v, v with { Ord = 8 }).Should().BeFalse();
        LiveRules.IsSameItem(v, v with { ConversationId = C2 }).Should().BeFalse();
    }

    [Fact]
    public void 持久事件到了_不比即時更新舊就作廢()
    {
        var live = new LiveVersion(C1, 1, 7, 10);

        LiveRules.IsSuperseded(live, durableEpoch: 1, durableVer: 10).Should().BeTrue("同一個版本的持久事件到了");
        LiveRules.IsSuperseded(live, 1, 12).Should().BeTrue();
        LiveRules.IsSuperseded(live, 1, 9).Should().BeFalse("持久事件是比較舊的檢查點，即時更新照樣顯示");
        LiveRules.IsSuperseded(live, 2, 1).Should().BeTrue("世代比較新");
        LiveRules.IsSuperseded(live with { Epoch = 3 }, 2, 50).Should().BeFalse();
    }

    [Fact]
    public void 一則超過64KB就不送()
    {
        LiveRules.MaxLiveBytes.Should().Be(64 * 1024);
        LiveRules.Fits(new string('x', 64 * 1024)).Should().BeTrue();
        LiveRules.Fits(new string('x', 64 * 1024 + 1)).Should().BeFalse();
        LiveRules.Fits(new string('字', 22_000)).Should().BeFalse("以 UTF-8 位元組算");
    }

    [Fact]
    public void 頻率_每個對話每秒最多maxHz則()
    {
        var hz = SyncLimits.Default.MaxLiveHz;
        hz.Should().Be(4);

        LiveRules.Allows(null, 1_000, hz).Should().BeTrue("還沒送過");
        LiveRules.Allows(1_000, 1_249, hz).Should().BeFalse("4 Hz＝至少間隔 250 毫秒");
        LiveRules.Allows(1_000, 1_250, hz).Should().BeTrue();
        LiveRules.Allows(1_000, 1_334, 3).Should().BeTrue("1000/3 無條件進位是 334 毫秒");
        LiveRules.Allows(1_000, 1_333, 3).Should().BeFalse();
        LiveRules.Allows(null, 1_000, 0).Should().BeFalse("0 Hz＝不送即時更新");
        LiveRules.Allows(null, 1_000, -1).Should().BeFalse();
    }

    /// <summary>一筆執行中的項目的即時更新（線上格式，沒有 blob）</summary>
    private static LiveRelay RelayOf(AgentChatItem item, long ver = 2)
    {
        ConversationWire.TryItem(item, ver, out var wire).Should().BeTrue();
        return new LiveRelay(C1, 1, item.Ord!.Value, ver, ConversationWire.Serialize(wire!));
    }

    [Fact]
    public void 即時更新的單一字串上限是64KB_長的中文回覆與工具輸出照樣能送()
    {
        // 11,000 個中文字的回覆（33 KB，超過持久事件的 32 KB 內嵌上限；即時更新沒有 blob 可用）
        var reply = RelayOf(new AgentChatItem { Id = "r1", Ord = 5, Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Running, Text = new string('字', 11_000) });
        System.Text.Encoding.UTF8.GetByteCount(reply.Item).Should().BeGreaterThan(ConversationEventRules.MaxInlineStringBytes);
        LiveRules.CheckRelay(reply, out var detail).Should().BeNull(detail);
        ConversationEventRules.Validate(ConversationEventKinds.Item, reply.Item).Should().Be(SyncErrorCodes.TooLarge, "持久事件的內嵌上限不變（要換成 blob）");

        // 執行中工具保留的上限 12,000 字（AgentHubRules.MaxToolOutputChars），全是中文是 36 KB
        var tool = RelayOf(new AgentChatItem
        {
            Id = "t1", Ord = 6, Kind = AgentItemKind.Tool, Status = AgentItemStatus.Running, ToolName = "執行指令",
            Output = new string('輸', Myriagent.Hub.AgentHubRules.MaxToolOutputChars),
        });
        LiveRules.CheckRelay(tool, out detail).Should().BeNull(detail);

        // 整則超過 64 KB 照樣不送
        var huge = RelayOf(new AgentChatItem { Id = "r2", Ord = 7, Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Running, Text = new string('字', 22_000) });
        LiveRules.CheckRelay(huge, out _).Should().Be(SyncErrorCodes.TooLarge);
    }
}
