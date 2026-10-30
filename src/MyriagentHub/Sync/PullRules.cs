namespace MyriagentHub.Sync;

/// <summary>
/// 拉取（<see cref="EventLog.Pull"/>）的合併與上限（純函式）：每個游標各讀一段、依 seq 合併後取前綴（筆數與位元組上限、至少一筆）。
/// 各游標拿到的一定是它待拉事件的前綴，裝置照每筆的 cls／stream 前進游標不會跳過事件。
/// </summary>
internal static class PullRules
{
    /// <summary>
    /// 一個游標讀到第 <paramref name="rows"/> 列、這個游標累計 <paramref name="bytes"/> 位元組（含每筆的固定開銷）時可以停讀了嗎（合併前審查 P4SRV-MAT-01）：
    /// 兩列以上、累計超過 <paramref name="maxBytes"/>。之後的列一定不會被選上——選上它代表這個游標讀到的這些都已選上，
    /// 而 <see cref="SelectPrefix"/> 選兩筆以上時合計不超過上限。剛讀到、讓它越界的這一列要留在合併裡：合併的前綴停在它（或更前面），
    /// 不會越過它選到別的游標 seq 更大的事件，<c>more</c> 也照實（第一筆就越界時多讀的第二筆同理）。
    /// 所以停讀之後回應的事件與 <c>more</c> 和讀滿 maxEvents＋1 列時完全相同，每個游標暫存的只有 maxBytes 加兩筆
    /// </summary>
    public static bool CursorDone(int rows, long bytes, long maxBytes) => rows >= 2 && bytes > maxBytes;

    /// <summary>
    /// 依 seq 合併之後取前綴：<paramref name="sizes"/> 是各筆的位元組（含固定開銷，依 seq 由小到大），最多 <paramref name="maxEvents"/> 筆、
    /// 合計不超過 <paramref name="maxBytes"/>（至少一筆：單筆超過上限也回）。回傳選幾筆（<paramref name="sizes"/> 只列舉到停下的那一筆）
    /// </summary>
    public static int SelectPrefix(IEnumerable<long> sizes, int maxEvents, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        var count = 0;
        long total = 0;
        foreach (var size in sizes)
        {
            if (count >= maxEvents || (count > 0 && total + size > maxBytes))
                break;
            total += size;
            count++;
        }
        return count;
    }
}
