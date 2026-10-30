using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// Ord 的配發規劃（WP4.3 計畫 D8～D10）：先保留、後配發，當機、舊版剝除、sync.db 以外的資料遺失時都不重用；
/// published_items 記得的 Ord 在不破壞遞增、沒被別的項目用掉時沿用
/// </summary>
public sealed class OrdAssignRulesTests
{
    private static readonly OrdReservation NoSidecar = new(null, false);

    private static List<(string Id, long? Ord)> Items(params long?[] ords)
        => ords.Select((ord, i) => ("item" + i, ord)).ToList();

    private static OrdHint Hint(long max, params (string Id, long Ord)[] ords)
        => new(ords.ToDictionary(o => o.Id, o => o.Ord, StringComparer.Ordinal), max);

    [Fact]
    public void 常數逐字釘住()
    {
        OrdAssignRules.ReserveBlock.Should().Be(64);
        OrdAssignRules.CorruptSidecarJump.Should().Be(1024);
    }

    [Fact]
    public void 全新_1到N_下一個N加1()
    {
        var plan = OrdAssignRules.PlanLoad(Items(null, null, null), null, NoSidecar, null);

        plan.Ords.Should().Equal(1, 2, 3);
        plan.Next.Should().Be(4);
        plan.Reserved.Should().Be(4 + 64, "先保留、後配：配完之後再保留一段");
        plan.Changed.Should().BeTrue();
        plan.Stripped.Should().BeFalse("沒有旁檔、沒有 hint：是第一次，不是舊版剝掉的");

        var empty = OrdAssignRules.PlanLoad(Items(), null, NoSidecar, null);
        empty.Ords.Should().BeEmpty();
        empty.Next.Should().Be(1);
        empty.Reserved.Should().Be(65, "第一次保留 64 個：上限 65（不含）");
        empty.Changed.Should().BeFalse();
    }

    [Fact]
    public void 已有Ord的照舊_小於1的當成沒配()
    {
        var plan = OrdAssignRules.PlanLoad(Items(1, 2, 0, -3, null), 6, NoSidecar, null);

        plan.Ords.Should().Equal(1, 2, 6, 7, 8);
        plan.Next.Should().Be(9);

        var unchanged = OrdAssignRules.PlanLoad(Items(1, 2, 3), 65, NoSidecar, null);
        unchanged.Ords.Should().Equal(1, 2, 3);
        unchanged.Changed.Should().BeFalse("沒有補配，不用存檔");
        unchanged.Next.Should().Be(65, "下一個從保留上限接著配");
        unchanged.Reserved.Should().Be(129);
    }

    [Fact]
    public void 保留_下一個等於上限時要保留_新上限加64()
    {
        OrdAssignRules.NeedsReservation(1, 65).Should().BeFalse();
        OrdAssignRules.NeedsReservation(64, 65).Should().BeFalse();
        OrdAssignRules.NeedsReservation(65, 65).Should().BeTrue("上限不含：65 還沒保留");
        OrdAssignRules.NeedsReservation(66, 65).Should().BeTrue();
        OrdAssignRules.NewReservation(65).Should().Be(129);
        OrdAssignRules.NewReservation(1).Should().Be(65);
    }

    [Fact]
    public void 當機後_從保留上限接著配()
    {
        // 保留到 129、配到 70 之後當機（71～128 可能已經發布、items.json 沒存到）
        var ords = Enumerable.Range(1, 70).Select(i => (long?)i).Append(null).ToArray();
        var plan = OrdAssignRules.PlanLoad(Items(ords), 129, NoSidecar, null);

        plan.Ords[^1].Should().Be(129, "下一個是 max(meta 的 NextOrd 129、現有最大 70＋1)");
        plan.Next.Should().Be(130);
        plan.Reserved.Should().Be(130 + 64);

        OrdAssignRules.PlanLoad(Items(ords[..^1]), 129, NoSidecar, null).Next.Should().Be(129);
        OrdAssignRules.PlanLoad(Items(ords[..^1]), 50, NoSidecar, null).Next.Should().Be(71, "items.json 比 meta.json 新：現有最大＋1");
    }

    [Fact]
    public void 旁檔比meta新_取較大者()
    {
        // 先寫旁檔、再寫 meta.json：兩者之間當機時旁檔比較新
        OrdAssignRules.PlanLoad(Items(1, 2), 65, new OrdReservation(129, false), null).Next.Should().Be(129);
        OrdAssignRules.PlanLoad(Items(1, 2), 193, new OrdReservation(129, false), null).Next.Should().Be(193);
        OrdAssignRules.PlanLoad(Items(1, 2), 65, new OrdReservation(0, false), null).Next.Should().Be(65, "不合格的旁檔值不算");
    }

    [Fact]
    public void 舊版剝除_旁檔128_全部null_配128起()
    {
        var plan = OrdAssignRules.PlanLoad(Items(null, null, null, null, null), null, new OrdReservation(128, false), null);

        plan.Ords.Should().Equal(128, 129, 130, 131, 132);
        plan.Next.Should().Be(133);
        plan.Stripped.Should().BeTrue("全部沒有 Ord、meta 沒有 NextOrd，但旁檔有值：中間跑過舊版");

        // meta 還有 NextOrd（舊版會剝掉它）：只是項目都還沒配過，不是舊版
        OrdAssignRules.PlanLoad(Items(null, null), 65, new OrdReservation(65, false), null).Stripped.Should().BeFalse();
        OrdAssignRules.PlanLoad(Items(1, null), null, new OrdReservation(65, false), null).Stripped.Should().BeFalse("有項目還留著 Ord");
    }

    [Fact]
    public void 舊版剝除_hint有記錄_沿用原本的Ord()
    {
        var items = new List<(string Id, long? Ord)> { ("a", null), ("b", null), ("c", null), ("d", null) };
        var plan = OrdAssignRules.PlanLoad(items, null, new OrdReservation(65, false), Hint(3, ("a", 1), ("b", 2), ("c", 3)));

        plan.Ords.Should().Equal(1, 2, 3, 65);
        plan.Next.Should().Be(66);
        plan.Changed.Should().BeTrue();
        plan.Stripped.Should().BeTrue();

        var hintOnly = OrdAssignRules.PlanLoad(items, null, NoSidecar, Hint(9, ("a", 4), ("c", 9)));
        hintOnly.Ords.Should().Equal(4, 10, 11, 12);
        hintOnly.Ords[2].Should().NotBe(9, "c 的 hint 9 小於前一個項目新配的 10，沿用會破壞遞增");
        hintOnly.Stripped.Should().BeTrue("hint 有值");
    }

    [Fact]
    public void hint的Ord會破壞遞增_不沿用()
    {
        // 前一個項目的 Ord 是 10：hint 的 4 會讓順序倒過來
        var after = new List<(string Id, long? Ord)> { ("x", 10), ("a", null) };
        OrdAssignRules.PlanLoad(after, null, NoSidecar, Hint(10, ("a", 4), ("x", 10))).Ords.Should().Equal(10, 11);

        // 後面已有的 Ord 是 3：hint 的 5 會大於後面的項目
        var before = new List<(string Id, long? Ord)> { ("a", null), ("y", 3) };
        OrdAssignRules.PlanLoad(before, null, NoSidecar, Hint(5, ("a", 5))).Ords.Should().Equal(6, 3);

        // hint 的值已經是別的項目的 Ord
        var taken = new List<(string Id, long? Ord)> { ("a", null), ("z", 2) };
        OrdAssignRules.PlanLoad(taken, null, NoSidecar, Hint(2, ("a", 2))).Ords.Should().Equal(3, 2);

        // 兩個項目的 hint 相同（記錄不一致）：只有第一個沿用
        var same = new List<(string Id, long? Ord)> { ("a", null), ("b", null) };
        OrdAssignRules.PlanLoad(same, null, NoSidecar, Hint(7, ("a", 7), ("b", 7))).Ords.Should().Equal(7, 8);

        // 合格的照用
        var ok = new List<(string Id, long? Ord)> { ("x", 1), ("a", null), ("y", 9) };
        OrdAssignRules.PlanLoad(ok, null, NoSidecar, Hint(9, ("a", 5))).Ords.Should().Equal(1, 5, 9);
    }

    [Fact]
    public void 旁檔損毀_多跳1024()
    {
        var plan = OrdAssignRules.PlanLoad(Items(1, null), 65, new OrdReservation(null, true), null);

        plan.Ords.Should().Equal(1, 65 + 1024);
        plan.Next.Should().Be(65 + 1024 + 1);
        OrdAssignRules.PlanLoad(Items((long?)null), null, new OrdReservation(null, true), null).Stripped.Should().BeTrue("損毀的旁檔也表示保留過");
    }

    [Fact]
    public void 旁檔與hint都沒有_published最大當下限()
    {
        // 沒有旁檔、hint 也沒有這些項目的記錄（例如都是新的），但這個對話發布過到 200（含已移除的項目）：不重用
        var plan = OrdAssignRules.PlanLoad(Items(null, null), null, NoSidecar, Hint(200));

        plan.Ords.Should().Equal(201, 202);
        plan.Next.Should().Be(203);
    }

    [Fact]
    public void 同樣的輸入一定同樣的輸出()
    {
        var items = new List<(string Id, long? Ord)> { ("a", null), ("b", 5), ("c", null), ("d", null), ("e", 40) };
        var hint = Hint(41, ("a", 2), ("c", 7), ("d", 50));

        var first = OrdAssignRules.PlanLoad(items, 33, new OrdReservation(30, false), hint);
        var second = OrdAssignRules.PlanLoad(items.ToList(), 33, new OrdReservation(30, false), Hint(41, ("d", 50), ("c", 7), ("a", 2)));

        second.Ords.Should().Equal(first.Ords);
        (second.Next, second.Reserved, second.Changed, second.Stripped).Should().Be((first.Next, first.Reserved, first.Changed, first.Stripped));
        // d 的 hint 50 大於後面已有的 40（沿用會破壞遞增）：從 published 的最大值（記錄裡的 50）之後配
        first.Ords.Should().Equal(2, 5, 7, 51, 40);
        first.Ords.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void 配出去的值不和任何現有或hint的值重疊()
    {
        var random = new Random(20261019);
        for (var round = 0; round < 200; round++)
        {
            var count = random.Next(0, 30);
            var items = Enumerable.Range(0, count)
                .Select(i => ("i" + i, random.Next(3) == 0 ? (long?)random.Next(1, 100) : null))
                .ToList();
            var hint = Hint(random.Next(0, 120), items.Where(_ => random.Next(2) == 0).Select(item => (item.Item1, (long)random.Next(1, 120))).ToArray());
            var meta = random.Next(2) == 0 ? (long?)random.Next(1, 150) : null;
            var plan = OrdAssignRules.PlanLoad(items, meta, new OrdReservation(random.Next(2) == 0 ? random.Next(1, 150) : null, random.Next(5) == 0), hint);

            plan.Ords.Should().HaveCount(count);
            for (var i = 0; i < count; i++)
            {
                if (items[i].Item2 is { } existing)
                    plan.Ords[i].Should().Be(existing, "已有的不動");
            }

            var existingOrds = items.Where(i => i.Item2 != null).Select(i => i.Item2!.Value).ToList();
            var assigned = Enumerable.Range(0, count).Where(i => items[i].Item2 == null).Select(i => plan.Ords[i]).ToList();
            assigned.Should().OnlyHaveUniqueItems();
            assigned.Should().NotIntersectWith(existingOrds, "不重用已有的值");
            foreach (var (ord, i) in assigned.Select((o, i) => (o, i)))
            {
                var id = items.Where(it => it.Item2 == null).ElementAt(i).Item1;
                var reused = hint.PublishedOrds.TryGetValue(id, out var h) && h == ord;
                if (!reused)
                    ord.Should().BeGreaterThan(Math.Max(hint.PublishedMax, existingOrds.DefaultIfEmpty(0).Max()), "新配的一律在所有用過的值之後");
            }

            plan.Reserved.Should().BeGreaterThan(plan.Ords.DefaultIfEmpty(0).Max(), "保留上限涵蓋所有配出去的值");
            plan.Next.Should().BeGreaterThan(plan.Ords.DefaultIfEmpty(0).Max());
        }
    }

    [Fact]
    public void 輸入是null_丟例外()
    {
        var act = () => OrdAssignRules.PlanLoad(null!, null, NoSidecar, null);
        act.Should().Throw<ArgumentNullException>();
    }
}
