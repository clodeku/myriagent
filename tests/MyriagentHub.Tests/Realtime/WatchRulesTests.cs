using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;

namespace MyriagentHub.Tests.Realtime;

/// <summary>觀看清單的純函式規則（WP4.2 計畫步 12；D25、P4）</summary>
public sealed class WatchRulesTests
{
    private const string A = "dev-a";
    private const string B = "dev-b";

    private static string Id(int n) => n.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);

    private static ConversationEntry Entry(int n, string? owner = A, string policy = ConversationSyncPolicies.Full, bool deleted = false) =>
        new(Id(n), owner, 1, policy, deleted, false, 0, 0, 0);

    private static ViewerState Viewer(bool visible, string[] open, params string[] visibleIds) =>
        new(new BrowserClientState(visible, true, open, 0), visibleIds);

    private static Func<string, ConversationEntry?> Directory(params ConversationEntry[] entries)
    {
        var map = entries.ToDictionary(e => e.ConversationId, StringComparer.Ordinal);
        return id => map.TryGetValue(id, out var e) ? e : null;
    }

    [Fact]
    public void 只列擁有且full且看得到的()
    {
        var find = Directory(Entry(1), Entry(2, owner: B), Entry(3, policy: ConversationSyncPolicies.Metadata), Entry(4, policy: ConversationSyncPolicies.Paused),
            Entry(5), Entry(6, owner: null), Entry(7));
        var viewers = new[]
        {
            Viewer(true, [Id(5)], Id(1), Id(2), Id(3), Id(4), Id(6), Id(8)),
            Viewer(true, [], Id(5)),
        };

        WatchRules.WatchedBy(A, viewers, find, 500).Should().Equal(new[] { Id(1), Id(5) }, "B 擁有的、metadata、paused、沒有擁有者、目錄沒有的、沒人看的（7）都不列；重複的只列一次");
        WatchRules.WatchedBy(B, viewers, find, 500).Should().Equal(new[] { Id(2) });
        WatchRules.WatchedBy("dev-c", viewers, find, 500).Should().BeEmpty();
        WatchRules.ByOwner(viewers, find, 500).Keys.Should().BeEquivalentTo(new[] { A, B }, "清單是空的裝置不列");
    }

    [Fact]
    public void 已刪除的不列()
    {
        var find = Directory(Entry(1, deleted: true), Entry(2));
        WatchRules.WatchedBy(A, [Viewer(true, [Id(1)], Id(1), Id(2))], find, 500).Should().Equal(new[] { Id(2) }, "提案 C2：刪除的對話不列");
    }

    [Fact]
    public void ClientState不可見_不算()
    {
        var find = Directory(Entry(1), Entry(2));
        WatchRules.WatchedBy(A, [Viewer(false, [Id(1)], Id(2))], find, 500).Should().BeEmpty("分頁在背景（visible=false）");
        WatchRules.WatchedBy(A, [new ViewerState(null, [Id(2)])], find, 500).Should().BeEmpty("還沒送過 ClientState");
        WatchRules.WatchedBy(A, [Viewer(false, [Id(1)]), Viewer(true, [], Id(2))], find, 500).Should().Equal(new[] { Id(2) }, "另一條看得到");
    }

    [Fact]
    public void open了但不在visible_不算()
    {
        // 伺服器端 open（BrowserConnection 的開著的對話）不是 ViewerState 的輸入：只看 visible 指令的清單與 openConversationIds
        var find = Directory(Entry(1), Entry(2));
        WatchRules.WatchedBy(A, [Viewer(true, [])], find, 500).Should().BeEmpty();
        WatchRules.WatchedBy(A, [Viewer(true, [Id(2)])], find, 500).Should().Equal(new[] { Id(2) }, "openConversationIds 算");
        WatchRules.WatchedBy(A, [Viewer(true, [], Id(1))], find, 500).Should().Equal(new[] { Id(1) }, "visible 指令的清單算");
    }

    [Fact]
    public void openConversationIds的非wire_id被濾掉()
    {
        var find = Directory(Entry(1));
        var upper = Id(1).ToUpperInvariant().Replace('0', 'A');
        var viewers = new[] { Viewer(true, ["local-1", "d0123", Id(1).ToUpperInvariant() + "x", "", upper], "draft") };

        WatchRules.SeenBy(viewers[0]).Should().BeEmpty();
        WatchRules.WatchedBy(A, viewers, id => throw new InvalidOperationException("不是對話 id 形狀的不查目錄：" + id), 500).Should().BeEmpty();
        WatchRules.WatchedBy(A, [Viewer(true, ["local-1", Id(1)])], find, 500).Should().Equal(new[] { Id(1) });
    }

    [Fact]
    public void 超過上限截斷_依id排序()
    {
        var entries = Enumerable.Range(1, 12).Select(n => Entry(n)).ToArray();
        var find = Directory(entries);
        var shuffled = entries.Select(e => e.ConversationId).Reverse().ToArray();

        var list = WatchRules.WatchedBy(A, [Viewer(true, shuffled[..6], shuffled[6..])], find, 5);

        list.Should().Equal(entries.Take(5).Select(e => e.ConversationId), "依 id 排序後取前 5 個（P4：整份取代、有上限）");
        WatchRules.WatchedBy(A, [Viewer(true, shuffled)], find, 0).Should().BeEmpty("上限 0：一個都不列");
    }

    [Fact]
    public void 看得到的判斷與閘道的CanSee一致()
    {
        // 觀看清單（送給擁有者）與閘道推送的「看得到」（步 10）是同一個定義（D25）：隨機的狀態逐一比對
        var random = new Random(4212);
        var ids = Enumerable.Range(1, 8).Select(Id).Concat(new[] { "local-1", "d1", "" }).ToArray();
        for (var round = 0; round < 500; round++)
        {
            var open = ids.Where(_ => random.Next(3) == 0).ToArray();
            var visible = ids.Where(_ => random.Next(3) == 0).ToArray();
            var state = random.Next(5) == 0 ? null : new BrowserClientState(random.Next(2) == 0, true, open, 0);
            var seen = WatchRules.SeenBy(new ViewerState(state, visible)).ToHashSet(StringComparer.Ordinal);
            foreach (var id in ids)
                seen.Contains(id).Should().Be(ConversationGatewayRules.CanSee(state, visible, id), $"第 {round} 輪的 {id}");
        }
    }
}
