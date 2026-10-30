namespace Myriagent.Sync;

/// <summary>對話為什麼要重整（整份比對、補送沒送過或改過的）</summary>
internal enum ReconcileReason
{
    /// <summary>插隊：伺服器的 <c>SnapshotRequest{open}</c>（有人在網頁打開這個對話，D23）</summary>
    Requested,

    /// <summary>還原的後備：<c>SnapshotRequest{restore}</c>（D23）</summary>
    Restore,

    /// <summary>被拒或沒有 conv1 時記的 <c>needs_redo</c>（D13、D14）</summary>
    Redo,

    /// <summary>啟動或上線時發現 items.json 與發布時不同（D11）</summary>
    Changed,

    /// <summary>回補：舊對話第一次上傳（答 2，D24）</summary>
    Backfill,
}

/// <summary>回補因磁碟保護上限暫停的原因（D24：不是節流，只防 outbox 把記憶體與磁碟撐爆）</summary>
internal enum BackfillHold
{
    None,

    /// <summary>保留中的對話列數超過 <see cref="ReconcileQueueRules.RetainedMaxRows"/></summary>
    RetainedRows,

    /// <summary>保留中的對話列 payload 合計超過 <see cref="ReconcileQueueRules.RetainedMaxBytes"/></summary>
    RetainedBytes,

    /// <summary>sync.db 所在磁碟剩下的空間少於 <see cref="ReconcileQueueRules.MinFreeDiskBytes"/></summary>
    LowDisk,
}

/// <summary>重整佇列裡的一個對話</summary>
/// <param name="UpdatedAtMs">對話最後更新的時間（meta 的 UpdatedAt；只用來排「由新到舊」，不跨電腦比較）</param>
/// <param name="EstimatedBytes">估計要送的位元組（給進度用）</param>
internal sealed record ReconcileCandidate(string WireId, ReconcileReason Reason, long UpdatedAtMs, long EstimatedBytes);

/// <summary>
/// 重整與回補的排程（WP4.3 計畫 D24；擁有者 2026-10-19 答 3 選 B：不限速，只讓新的事件優先，本機有對話執行中也不暫停）。
/// 一輪最多 <see cref="RoundMaxRows"/> 列、<see cref="RoundMaxBytes"/> 位元組只是一輪的組成上限，不是節奏；
/// 保留上限（<see cref="Hold"/>）只防 outbox 把記憶體與磁碟撐爆，等 24 小時的修剪。伺服器端照舊以批次上限與背壓保護 Hub 主機。純函式
/// </summary>
internal static class ReconcileQueueRules
{
    /// <summary>一輪寫進 outbox 的列數上限（一輪的組成，不是節流）</summary>
    public const int RoundMaxRows = 500;

    /// <summary>一輪寫進 outbox 的 payload 位元組上限</summary>
    public const long RoundMaxBytes = 2L * 1024 * 1024;

    /// <summary>
    /// 保留中的對話列數上限。步 9 實測（2026-10-19，<c>OutboxSlimLongRunTests</c>）：已完成的對話列的骨架每 10 萬筆 15.26～15.34 MB
    /// （每筆約 160 位元組），不超過 16 MB，所以維持 1,000,000（到上限時骨架約 153 MB）
    /// </summary>
    public const int RetainedMaxRows = 1_000_000;

    /// <summary>保留中的對話列 payload 合計上限（4 GiB）</summary>
    public const long RetainedMaxBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>sync.db 所在磁碟至少要剩的空間（2 GiB）</summary>
    public const long MinFreeDiskBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// 處理順序：插隊（Requested）優先，其次還原（Restore）、重整（Redo、Changed），最後回補（Backfill）；同一類依最後更新由新到舊，
    /// 平手比對話 id（序數）。同一個對話出現多次只留排最前面的那一筆
    /// </summary>
    public static IReadOnlyList<ReconcileCandidate> Order(IEnumerable<ReconcileCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return candidates
            .Where(c => c != null)
            .OrderBy(c => Rank(c.Reason))
            .ThenByDescending(c => c.UpdatedAtMs)
            .ThenBy(c => c.WireId, StringComparer.Ordinal)
            .Where(c => seen.Add(c.WireId))
            .ToList();
    }

    /// <summary>
    /// 磁碟保護：保留中的列數超過上限、payload 合計超過上限、或磁碟剩下的少於下限時暫停（依這個順序回報第一個）。
    /// 剩餘空間查不到時呼叫端傳 <see cref="long.MaxValue"/>
    /// </summary>
    public static BackfillHold Hold(int retainedRows, long retainedBytes, long freeDiskBytes)
    {
        if (retainedRows > RetainedMaxRows)
            return BackfillHold.RetainedRows;
        if (retainedBytes > RetainedMaxBytes)
            return BackfillHold.RetainedBytes;
        if (freeDiskBytes < MinFreeDiskBytes)
            return BackfillHold.LowDisk;
        return BackfillHold.None;
    }

    /// <summary>可以排下一輪：outbox 沒有一般事件待送（新的事件優先），而且沒有因磁碟保護暫停。不看本機有沒有對話在執行、沒有時間或位元組的預算</summary>
    public static bool MayStartRound(int generalPending, int retainedRows, long retainedBytes, long freeDiskBytes)
        => generalPending == 0 && Hold(retainedRows, retainedBytes, freeDiskBytes) == BackfillHold.None;

    private static int Rank(ReconcileReason reason) => reason switch
    {
        ReconcileReason.Requested => 0,
        ReconcileReason.Restore => 1,
        ReconcileReason.Redo => 2,
        ReconcileReason.Changed => 2,
        ReconcileReason.Backfill => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "不認得的重整原因"),
    };
}
