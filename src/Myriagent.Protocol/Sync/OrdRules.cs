namespace Myriagent.Sync;

/// <summary>
/// 項目在對話內的順序 <c>Ord</c>（設計 14.11 D35；docs/sync-protocol.md 14.2）：由擁有者電腦配發、嚴格遞增、配過就不再用
/// （刪掉的項目的 Ord 也不重用：即時更新以 (對話, ord) 合併，重用會讓晚到的舊更新蓋到新項目上）。同步只認 Ord，網頁的 Seq 不變。
/// 「下一個」記在 meta.json 的 <c>AgentConversation.NextOrd</c>；meta.json 與 items.json 不是同一次寫入（當機時 items.json 可能比較新），
/// 所以一律取它與現有項目最大 Ord＋1 的較大者，重開程式後照樣遞增。
/// </summary>
internal static class OrdRules
{
    /// <summary>下一個要配的 Ord（≥1）：<paramref name="storedNext"/>（meta.json 記的）與現有項目最大 Ord＋1 的較大者；小於 1 的 Ord 當成沒配</summary>
    public static long Next(long? storedNext, IEnumerable<long?> existing)
    {
        long max = 0;
        foreach (var ord in existing)
        {
            if (ord is >= 1 && ord.Value > max)
                max = ord.Value;
        }

        return Math.Max(storedNext is >= 1 ? storedNext.Value : 1, max + 1);
    }

    /// <summary>
    /// 補配：依檔案順序，還沒配（null 或小於 1）的項目依序配下一個，已配的不動。回傳每個位置的 Ord，<paramref name="nextOrd"/> 是之後要存回的 NextOrd。
    /// 第一次（WP4.3 第一次同步載入時，全部都還沒配）就是 1..N；之後新增的項目都配在最大值之後，所以只要補配發生在新增之前，Ord 的順序就是檔案的順序
    /// </summary>
    public static long[] Backfill(IReadOnlyList<long?> ords, long? storedNext, out long nextOrd)
    {
        var next = Next(storedNext, ords);
        var result = new long[ords.Count];
        for (int i = 0; i < ords.Count; i++)
            result[i] = ords[i] is >= 1 ? ords[i]!.Value : next++;
        nextOrd = next;
        return result;
    }
}
