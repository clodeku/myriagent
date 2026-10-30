namespace Myriagent.Hub;

/// <summary>
/// 依 Ord 分頁（WP4.3 計畫 D12）：下一頁從上一頁最後一筆的 Ord 之後接著要，不用位置（skip／take），途中移除項目也不會跳過還沒送的。
/// 中樞在 UI 執行緒呼叫：一般情況（Ord 照清單順序遞增）只走一遍、只配置這一頁的索引；遞增被破壞時（存檔裡的舊資料）才收集後排序。純函式
/// </summary>
internal static class PublishPageRules
{
    /// <summary>
    /// Ord 大於 <paramref name="afterOrd"/> 的最前面 <paramref name="take"/> 筆（依 Ord 由小到大；同 Ord 依清單順序）在清單裡的索引。
    /// Ord 是 null 的不算。<see cref="PublishPageSelection.More"/>＝還有 Ord 比這一頁最後一筆大的項目
    /// </summary>
    public static PublishPageSelection Select<T>(IReadOnlyList<T> items, Func<T, long?> ordOf, long afterOrd, int take)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(ordOf);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        return IsAscending(items, ordOf) ? SelectAscending(items, ordOf, afterOrd, take) : SelectSorted(items, ordOf, afterOrd, take);
    }

    /// <summary>不是 null 的 Ord 照清單順序嚴格遞增</summary>
    private static bool IsAscending<T>(IReadOnlyList<T> items, Func<T, long?> ordOf)
    {
        long? previous = null;
        for (var i = 0; i < items.Count; i++)
        {
            if (ordOf(items[i]) is not { } ord)
                continue;
            if (previous is { } p && ord <= p)
                return false;
            previous = ord;
        }

        return true;
    }

    private static PublishPageSelection SelectAscending<T>(IReadOnlyList<T> items, Func<T, long?> ordOf, long afterOrd, int take)
    {
        var indices = new List<int>(Math.Min(take, items.Count));
        var last = afterOrd;
        for (var i = 0; i < items.Count; i++)
        {
            if (ordOf(items[i]) is not { } ord || ord <= afterOrd)
                continue;
            if (indices.Count == take)
                return new PublishPageSelection(indices, last, More: true);
            indices.Add(i);
            last = ord;
        }

        return new PublishPageSelection(indices, last, More: false);
    }

    private static PublishPageSelection SelectSorted<T>(IReadOnlyList<T> items, Func<T, long?> ordOf, long afterOrd, int take)
    {
        var candidates = new List<(long Ord, int Index)>();
        for (var i = 0; i < items.Count; i++)
        {
            if (ordOf(items[i]) is { } ord && ord > afterOrd)
                candidates.Add((ord, i));
        }

        candidates.Sort((a, b) => a.Ord != b.Ord ? a.Ord.CompareTo(b.Ord) : a.Index.CompareTo(b.Index));
        var count = Math.Min(take, candidates.Count);
        var indices = new List<int>(count);
        for (var k = 0; k < count; k++)
            indices.Add(candidates[k].Index);
        var last = count > 0 ? candidates[count - 1].Ord : afterOrd;
        var more = false;
        for (var k = count; k < candidates.Count && !more; k++)
            more = candidates[k].Ord > last;
        return new PublishPageSelection(indices, last, more);
    }
}

/// <param name="Indices">這一頁的項目在清單裡的索引（依 Ord 由小到大）</param>
/// <param name="LastOrd">這一頁最後一筆的 Ord；空頁是要求的 afterOrd</param>
/// <param name="More">還有 Ord 比 <paramref name="LastOrd"/> 大的項目</param>
internal sealed record PublishPageSelection(IReadOnlyList<int> Indices, long LastOrd, bool More);
