using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 依 Ord 分頁（WP4.3 計畫步 7，D12）：下一頁從上一頁最後一筆的 Ord 之後接著要，途中移除項目也不跳過；Ord 是 null 的不算；
/// 遞增被破壞時（存檔裡的舊資料）照 Ord 排序
/// </summary>
public sealed class PublishPageRulesTests
{
    private static PublishPageSelection Select(IReadOnlyList<long?> ords, long afterOrd, int take)
        => PublishPageRules.Select(ords, o => o, afterOrd, take);

    [Fact]
    public void 遞增的清單_從afterOrd之後取take筆()
    {
        long?[] ords = [1, 2, 3, 4, 5, 6, 7];

        var first = Select(ords, 0, 3);
        first.Indices.Should().Equal(0, 1, 2);
        first.LastOrd.Should().Be(3);
        first.More.Should().BeTrue();

        var last = Select(ords, 6, 3);
        last.Indices.Should().Equal(6);
        last.LastOrd.Should().Be(7);
        last.More.Should().BeFalse();
    }

    [Fact]
    public void 剛好取完_More是false_空頁的LastOrd是afterOrd()
    {
        long?[] ords = [1, 2, 3];

        Select(ords, 0, 3).Should().Match<PublishPageSelection>(p => p.Indices.Count == 3 && p.LastOrd == 3 && !p.More);
        var empty = Select(ords, 3, 10);
        empty.Indices.Should().BeEmpty();
        empty.LastOrd.Should().Be(3);
        empty.More.Should().BeFalse();
        Select([], 0, 10).Should().Match<PublishPageSelection>(p => p.Indices.Count == 0 && p.LastOrd == 0 && !p.More);
    }

    [Fact]
    public void 途中移除項目_下一頁不跳號()
    {
        var ords = new List<long?> { 1, 2, 3, 4, 5, 6, 7, 8 };
        var first = Select(ords, 0, 4);
        first.LastOrd.Should().Be(4);

        ords.RemoveAt(1);   // 第一頁裡的一筆被移除：位置往前移
        var second = Select(ords, first.LastOrd, 4);

        // 依 Ord 接著要，不是跳過前 4 個位置（那樣會漏掉 Ord 5）
        second.Indices.Select(i => ords[i]).Should().Equal(5L, 6L, 7L, 8L);
        second.More.Should().BeFalse();
    }

    [Fact]
    public void Ord是null的不算()
    {
        long?[] ords = [null, 1, null, 2, 3, null];

        var page = Select(ords, 0, 2);

        page.Indices.Should().Equal(1, 3);
        page.LastOrd.Should().Be(2);
        page.More.Should().BeTrue();
        Select(ords, 2, 5).Indices.Should().Equal(4);
    }

    [Fact]
    public void 遞增被破壞_照Ord排序_同Ord依清單順序()
    {
        long?[] ords = [5, 1, 9, 3, 7, 3];

        var first = Select(ords, 0, 3);
        first.Indices.Should().Equal(1, 3, 5);
        first.LastOrd.Should().Be(3);
        first.More.Should().BeTrue();

        var second = Select(ords, 3, 3);
        second.Indices.Should().Equal(0, 4, 2);
        second.LastOrd.Should().Be(9);
        second.More.Should().BeFalse();
    }

    [Fact]
    public void 遞增被破壞_More只看比最後一筆大的()
    {
        long?[] ords = [2, 1, 2];

        var page = Select(ords, 0, 2);

        page.Indices.Should().Equal(1, 0);
        page.LastOrd.Should().Be(2);
        page.More.Should().BeFalse("剩下的只有和最後一筆同 Ord 的（存檔損毀），下一頁也拿不到");

        // 照順序但有重複的 Ord 也算遞增被破壞（不走只看一遍的路）
        var duplicated = Select([1, 2, 2], 0, 2);
        duplicated.Indices.Should().Equal(0, 1);
        duplicated.More.Should().BeFalse();
    }

    [Fact]
    public void take不是正數丟例外()
    {
        var act = () => Select([1], 0, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
