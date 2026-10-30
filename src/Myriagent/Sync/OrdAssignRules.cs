namespace Myriagent.Sync;

/// <summary>對話資料夾裡 Ord 保留的旁檔 <c>sync-ord.json</c>（<c>{"reserved":N}</c>，WP4.3 計畫 D9）讀到的內容</summary>
/// <param name="Reserved">保留到的上限（不含；小於它的 Ord 都可能已經配出去）；沒有旁檔或值不合格是 null</param>
/// <param name="Corrupt">旁檔存在但讀不懂（損毀）：不知道保留到哪裡，下一個 Ord 多跳 <see cref="OrdAssignRules.CorruptSidecarJump"/></param>
internal readonly record struct OrdReservation(long? Reserved, bool Corrupt);

/// <summary>
/// sync.db 的 <c>published_items</c> 記得的 Ord（D9、D10）：舊版剝掉 Ord 之後、或未載入的對話先在執行緒集區算過，載入時沿用同一個值
/// </summary>
/// <param name="PublishedOrds">項目 id → 發布時的 Ord</param>
/// <param name="PublishedMax">這個對話發布過的最大 Ord（含已移除的項目；Ord 不重用）；沒有是 0</param>
internal sealed record OrdHint(IReadOnlyDictionary<string, long> PublishedOrds, long PublishedMax);

/// <summary>載入（或第一次準備發布）時的 Ord 規劃</summary>
/// <param name="Ords">每個位置的 Ord（與輸入的清單同順序）</param>
/// <param name="Next">配完之後下一個要配的 Ord</param>
/// <param name="Reserved">要先寫進旁檔與 meta.json 的保留上限（不含；先保留、後配發）</param>
/// <param name="Changed">有項目的 Ord 和輸入不同（補配了），項目要存檔</param>
/// <param name="Stripped">看起來中間跑過舊版：全部的項目都沒有 Ord、meta 也沒有 NextOrd，但旁檔或 published_items 有值（D36）</param>
internal sealed record OrdLoadPlan(IReadOnlyList<long> Ords, long Next, long Reserved, bool Changed, bool Stripped);

/// <summary>
/// 項目 Ord 的配發規劃（WP4.3 計畫 D8～D10；docs/sync-protocol.md 14.2）：對話內嚴格遞增、配過就不再用——當機（發布了、還沒存檔）、
/// 舊版存檔剝掉 Ord 與 NextOrd、sync.db 以外的資料遺失時都不重用。做法是「先保留、後配發」：meta.json 的 <c>NextOrd</c> 與旁檔
/// <c>sync-ord.json</c> 存的是保留到的上限（每次多保留 <see cref="ReserveBlock"/> 個），重開時從上限之後接著配，當機前已配出去的不會再配一次。
/// 純函式：中樞（UI 執行緒）與引擎讀未載入的對話（執行緒集區、唯讀）呼叫同一份，同樣的輸入一定得到同樣的結果
/// </summary>
internal static class OrdAssignRules
{
    /// <summary>每次保留的個數</summary>
    public const int ReserveBlock = 64;

    /// <summary>旁檔損毀時多跳的個數（不知道保留到哪裡：寧可跳號，也不重用）</summary>
    public const int CorruptSidecarJump = 1024;

    /// <summary>
    /// 載入時的規劃：已有 Ord（≥1）的項目照舊；沒有的（null 或小於 1）依清單順序——<paramref name="hint"/> 記得它、那個值大於前一個項目的 Ord、
    /// 小於後面所有已有的 Ord、而且沒被別的項目用掉就沿用，否則從下一個起配。
    /// 下一個＝max(meta 的 <c>NextOrd</c>、旁檔的保留上限、現有最大＋1、published_items 最大＋1，缺的當 1)，旁檔損毀時再加 <see cref="CorruptSidecarJump"/>。
    /// 保留上限一律是配完之後的下一個＋<see cref="ReserveBlock"/>（重開之後的下一個至少是舊的上限，所以每次載入都要重新保留）
    /// </summary>
    /// <param name="items">項目 id 與存檔裡的 Ord（清單順序）</param>
    /// <param name="metaNextOrd">meta.json 的 <c>NextOrd</c>（保留上限）</param>
    /// <param name="sidecar">旁檔 <c>sync-ord.json</c></param>
    /// <param name="hint">sync.db 的 published_items；沒有（未配對、沒發布過）是 null</param>
    public static OrdLoadPlan PlanLoad(IReadOnlyList<(string Id, long? Ord)> items, long? metaNextOrd, OrdReservation sidecar, OrdHint? hint)
    {
        ArgumentNullException.ThrowIfNull(items);
        var existing = new HashSet<long>();
        long max = 0;
        foreach (var (_, ord) in items)
        {
            if (ord is >= 1)
            {
                existing.Add(ord.Value);
                max = Math.Max(max, ord.Value);
            }
        }

        // published_items 的最大值：PublishedMax 與每一筆記錄的較大者（記錄不一致時也不會配到 hint 沿用的值）
        var publishedMax = hint == null ? 0 : Math.Max(hint.PublishedMax, hint.PublishedOrds.Values.DefaultIfEmpty(0).Max());
        var next = Math.Max(Math.Max(Valid(metaNextOrd), Valid(sidecar.Reserved)), Math.Max(max + 1, publishedMax + 1));
        if (sidecar.Corrupt)
            next += CorruptSidecarJump;

        // 每個位置之後（不含）已有 Ord 的最小值：沿用 hint 時不能大於等於它（會破壞遞增）
        var laterMin = new long[items.Count];
        var min = long.MaxValue;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            laterMin[i] = min;
            if (items[i].Ord is { } ord && ord >= 1)
                min = Math.Min(min, ord);
        }

        var used = new HashSet<long>(existing);
        var ords = new long[items.Count];
        var changed = false;
        long previous = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var (id, ord) = items[i];
            long assigned;
            if (ord is >= 1)
            {
                assigned = ord.Value;
            }
            else if (hint != null && id != null && hint.PublishedOrds.TryGetValue(id, out var hinted)
                     && hinted >= 1 && hinted > previous && hinted < laterMin[i] && !used.Contains(hinted))
            {
                assigned = hinted;
                used.Add(hinted);
                changed = true;
            }
            else
            {
                assigned = next++;
                used.Add(assigned);
                changed = true;
            }

            ords[i] = assigned;
            previous = assigned;
        }

        var stripped = items.All(item => item.Ord is not >= 1) && metaNextOrd is not >= 1
                       && (sidecar.Reserved is >= 1 || sidecar.Corrupt || publishedMax >= 1);
        return new OrdLoadPlan(ords, next, NewReservation(next), changed, stripped);
    }

    /// <summary>下一個要配的已經到了保留上限：要先保留下一段（寫旁檔與 meta.json）才能配</summary>
    public static bool NeedsReservation(long next, long reserved) => next >= reserved;

    /// <summary>從 <paramref name="next"/> 起保留 <see cref="ReserveBlock"/> 個之後的新上限</summary>
    public static long NewReservation(long next) => next + ReserveBlock;

    private static long Valid(long? value) => value is >= 1 ? value.Value : 1;
}
