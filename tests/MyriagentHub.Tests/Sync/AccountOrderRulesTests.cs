using MyriagentHub.Sync;

namespace MyriagentHub.Tests.Sync;

/// <summary>帳戶目錄的順序（瀏覽器的 accountReorder；web-protocol 第六節第 10 點）</summary>
public sealed class AccountOrderRulesTests
{
    [Fact]
    public void 排序依sort_order再created_at再id()
    {
        var accounts = new[]
        {
            new AccountOrder("la_C", 1, 100),
            new AccountOrder("la_B", 0, 200),
            new AccountOrder("la_A", 1, 100),
            new AccountOrder("la_a", 1, 100),
            new AccountOrder("la_D", 0, 50),
            new AccountOrder("la_E", -5, 999),
        };
        AccountOrderRules.Sort(accounts).Select(a => a.LogicalAccountId).Should().Equal("la_E", "la_D", "la_B", "la_A", "la_C", "la_a");
    }

    [Fact]
    public void 請求裡列到的先排並照請求的順序()
    {
        var current = Accounts("A", "B", "C", "D");
        var plan = AccountOrderRules.Plan(current, ["la_D", "la_B", "la_A", "la_C"]);
        Apply(current, plan).Should().Equal("la_D", "la_B", "la_A", "la_C");
    }

    [Fact]
    public void 沒列到的接在後面並維持原相對順序()
    {
        var current = Accounts("A", "B", "C", "D", "E");
        var plan = AccountOrderRules.Plan(current, ["la_D", "la_B"]);
        Apply(current, plan).Should().Equal("la_D", "la_B", "la_A", "la_C", "la_E");
    }

    [Fact]
    public void 已不存在的id略過()
    {
        var current = Accounts("A", "B", "C");
        var plan = AccountOrderRules.Plan(current, ["la_X", "la_C", "la_Y", "la_A"]);
        plan.Select(p => p.Id).Should().NotContain(new[] { "la_X", "la_Y" });
        Apply(current, plan).Should().Equal("la_C", "la_A", "la_B");
    }

    [Fact]
    public void 重複的id只算第一次()
    {
        var current = Accounts("A", "B", "C");
        Apply(current, AccountOrderRules.Plan(current, ["la_B", "la_A", "la_B"])).Should().Equal("la_B", "la_A", "la_C");
    }

    [Fact]
    public void 只回順序有變的帳戶()
    {
        var current = Accounts("A", "B", "C", "D");
        AccountOrderRules.Plan(current, ["la_A", "la_B", "la_C", "la_D"]).Should().BeEmpty("順序沒變");

        var plan = AccountOrderRules.Plan(current, ["la_B", "la_A"]);
        plan.Should().BeEquivalentTo(new[] { ("la_B", 0), ("la_A", 1) }, "C、D 的位置沒變");

        // 原本的 sort_order 不是 0..n-1（例如 10、20、30）：照樣重新編號，只回值有變的
        var sparse = new[] { new AccountOrder("la_A", 0, 1), new AccountOrder("la_B", 20, 1), new AccountOrder("la_C", 2, 1) };
        AccountOrderRules.Plan(sparse, ["la_A", "la_C", "la_B"]).Should().BeEquivalentTo(new[] { ("la_C", 1), ("la_B", 2) });
    }

    [Fact]
    public void 新順序在IsSortOrder範圍內()
    {
        var current = Enumerable.Range(0, 600)
            .Select(i => new AccountOrder("la_" + i.ToString("D4"), CatalogRules.MaxSortOrder - i, i))
            .ToList();
        var plan = AccountOrderRules.Plan(current, current.Select(a => a.LogicalAccountId).Reverse().Take(500).ToList());
        plan.Should().NotBeEmpty();
        plan.Should().OnlyContain(p => CatalogRules.IsSortOrder(p.SortOrder) && p.SortOrder >= 0 && p.SortOrder < current.Count);
        Apply(current, plan).Should().OnlyHaveUniqueItems();
    }

    /// <summary>以 created_at 遞增、sort_order 0..n-1 建立帳戶（id 是 la_ 加上字母）</summary>
    private static List<AccountOrder> Accounts(params string[] names) =>
        names.Select((n, i) => new AccountOrder("la_" + n, i, 1000 + i)).ToList();

    /// <summary>套用計畫後的顯示順序</summary>
    private static List<string> Apply(IReadOnlyList<AccountOrder> current, IReadOnlyList<(string Id, int SortOrder)> plan)
    {
        var changes = plan.ToDictionary(p => p.Id, p => p.SortOrder, StringComparer.Ordinal);
        var applied = current.Select(a => changes.TryGetValue(a.LogicalAccountId, out var order) ? a with { SortOrder = order } : a);
        return AccountOrderRules.Sort(applied).Select(a => a.LogicalAccountId).ToList();
    }
}
