using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>項目在對話內的順序 Ord（WP4.1 步 4；設計 14.11 D35）：嚴格遞增、重開程式後照樣遞增、刪掉的也不重用</summary>
public sealed class OrdRulesTests : IDisposable
{
    private readonly TempDirectory temp = new("ord");

    public void Dispose() => temp.Dispose();

    [Fact]
    public void 下一個_取NextOrd與現有最大加一的較大者()
    {
        OrdRules.Next(null, []).Should().Be(1, "還沒配過");
        OrdRules.Next(null, [1, 2, 3]).Should().Be(4);
        OrdRules.Next(10, [1, 2, 3]).Should().Be(10, "刪掉的項目的 Ord 不重用");
        OrdRules.Next(2, [1, 2, 7]).Should().Be(8, "items.json 比 meta.json 新（當機時兩個檔不是一起寫的）");
        OrdRules.Next(null, [null, 0, -3, 5]).Should().Be(6, "小於 1 的當成沒配");
        OrdRules.Next(0, []).Should().Be(1);
        OrdRules.Next(-5, [null]).Should().Be(1);
    }

    [Fact]
    public void 補配_第一次是1到N_已配的不動_之後的配在最大值之後()
    {
        OrdRules.Backfill([null, null, null], null, out var next).Should().Equal(1, 2, 3);
        next.Should().Be(4);

        OrdRules.Backfill([1, 2, null], 3, out next).Should().Equal(1, 2, 3);
        next.Should().Be(4);

        OrdRules.Backfill([1, 2, null], 9, out next).Should().Equal(new long[] { 1, 2, 9 }, "刪掉過 3～8");
        next.Should().Be(10);

        OrdRules.Backfill([], 5, out next).Should().BeEmpty();
        next.Should().Be(5);
    }

    [Fact]
    public void 重開程式後照樣遞增_刪掉最後一筆也不重用()
    {
        var store = new ConversationStore(temp.Root);
        var meta = new AgentConversation { Title = "排序" };
        var items = new List<AgentChatItem>
        {
            new() { Kind = AgentItemKind.User, Text = "一" },
            new() { Kind = AgentItemKind.Assistant, Text = "二" },
        };

        // 第一次同步載入：補配 1..N，NextOrd 存進 meta.json
        var ords = OrdRules.Backfill(items.Select(i => i.Ord).ToList(), meta.NextOrd, out var next);
        for (int i = 0; i < items.Count; i++)
            items[i].Ord = ords[i];
        meta.NextOrd = next;
        // 新增一筆、再刪掉它（最後一筆）
        var removed = new AgentChatItem { Kind = AgentItemKind.Thinking, Text = "三", Ord = OrdRules.Next(meta.NextOrd, items.Select(i => i.Ord)) };
        meta.NextOrd = removed.Ord + 1;
        store.SaveConversation(meta);
        store.SaveItems(meta.Id, items);

        // 重開程式
        var reloaded = new ConversationStore(temp.Root);
        var loadedMeta = reloaded.LoadConversations().Single();
        var loadedItems = reloaded.LoadItems(meta.Id);

        loadedItems.Select(i => i.Ord).Should().Equal(1L, 2L);
        loadedMeta.NextOrd.Should().Be(4);
        OrdRules.Next(loadedMeta.NextOrd, loadedItems.Select(i => i.Ord)).Should().BeGreaterThan(removed.Ord!.Value, "刪掉的 3 不能再配給新項目");
    }
}
