namespace Myriagent.Sync;

/// <summary>
/// outbox 瘦身的純函式（WP4.3 計畫 D31、步 9）：回補的數萬筆對話列不讓記憶體與每次確認的成本跟著長；第 3 階段的列行為不變。
/// <list type="bullet">
/// <item>已完成（Acked、Rejected）的對話列在記憶體只留骨架（<see cref="Skeleton"/>）：payload 與它的雜湊換成空字串，已確認的連合併鍵也拿掉
/// （被拒的留著：之後要從合併鍵找回項目，D36）；種類、編碼、拒絕碼換成共用的常數，串流與對話 id 由呼叫端共用同一個字串。
/// 要重送（gap、伺服器還原後的對帳）時才從 sync.db 讀回（<see cref="ReloadNeeded"/>、<see cref="Restore"/>）。</item>
/// <item>BatchAck 只處理在途的尾段（<see cref="AckStart"/>），結果與整份處理逐筆相同；尾段原地接回（<see cref="Splice"/>）。</item>
/// </list>
/// outbox 一律依 deviceSeq 遞增排序（引擎的記憶體副本與 sync.db 一致）。<c>knownDone</c>＝呼叫端已知「這個位置之前都已完成」的位置
/// （引擎記著上一次的結果，找第一個未完成的列不必每次從頭掃）；不知道就給 0
/// </summary>
internal static class OutboxSliceRules
{
    /// <summary>第一個未完成（不是 Acked、Rejected）的列；全部完成（或空的）回 <c>Count</c>。從 <paramref name="knownDone"/> 起找（它之前的列呼叫端保證都已完成）</summary>
    public static int FirstActiveIndex(IReadOnlyList<OutboxEntry> ordered, int knownDone = 0)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        for (var i = Math.Clamp(knownDone, 0, ordered.Count); i < ordered.Count; i++)
        {
            if (!ordered[i].IsDone)
                return i;
        }

        return ordered.Count;
    }

    /// <summary>
    /// <see cref="OutboxRules.ApplyAck"/> 只要處理這個位置起的尾段，結果就與整份處理逐筆相同（這個位置之前的列不變）：
    /// 一般是第一個未完成的列（逐筆的結果只改未完成的列，重連只把在途的改回重送）。gap 帶 expected 時 Rewind 會把 expected 起已完成的列改回重送、
    /// 並以第一列判斷伺服器是不是缺了這台已刪掉的事件：從 deviceSeq 小於 expected 的最後一列起（沒有這種列就從頭）
    /// </summary>
    public static int AckStart(IReadOnlyList<OutboxEntry> ordered, BatchAck ack, int knownDone = 0)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(ack);
        var first = FirstActiveIndex(ordered, knownDone);
        if (ack.Error is not { Code: SyncErrorCodes.Gap, Expected: { } expected })
            return first;
        var lower = LowerBound(ordered, expected);
        return lower == 0 ? 0 : Math.Min(first, lower - 1);
    }

    /// <summary>可以換成骨架：已完成的對話列（帶 conversationId；第 3 階段的列一律不換）</summary>
    public static bool CanSkeleton(OutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.ConversationId is not null && entry.IsDone;
    }

    /// <summary>是骨架（對話列、payload 與雜湊都是空的；寫進 outbox 的列不會是空的）。狀態可能已被改回重送（gap、對帳），送出前要讀回</summary>
    public static bool IsSkeleton(OutboxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.ConversationId is not null && entry.Payload.Length == 0 && entry.PayloadSha256.Length == 0;
    }

    /// <summary>
    /// 已完成的對話列的骨架：payload 與雜湊換成空字串；已確認的拿掉合併鍵（被拒的留著）；種類、編碼、拒絕碼換成共用的常數；
    /// <paramref name="share"/> 給了就以它共用串流與對話 id 的字串（同一個對話的列共用一份）。不能換的列丟 <see cref="ArgumentException"/>
    /// </summary>
    public static OutboxEntry Skeleton(OutboxEntry entry, Func<string, string>? share = null)
    {
        if (!CanSkeleton(entry))
            throw new ArgumentException("只有已完成的對話列可以換成骨架", nameof(entry));
        return entry with
        {
            Stream = share is null ? entry.Stream : share(entry.Stream),
            Kind = SharedKind(entry.Kind),
            Enc = SharedEnc(entry.Enc),
            Payload = string.Empty,
            PayloadSha256 = string.Empty,
            CoalesceKey = entry.State == OutboxState.Acked ? null : entry.CoalesceKey,
            RejectReason = SharedReason(entry.RejectReason),
            ConversationId = share is null ? entry.ConversationId : share(entry.ConversationId!),
        };
    }

    /// <summary>以 sync.db 讀回的列補回骨架的內容（編碼、payload、雜湊、合併鍵）；狀態與其他欄位照記憶體的。deviceSeq 不同丟 <see cref="ArgumentException"/></summary>
    public static OutboxEntry Restore(OutboxEntry skeleton, OutboxEntry stored)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(stored);
        if (skeleton.DeviceSeq != stored.DeviceSeq)
            throw new ArgumentException($"讀回的列（{stored.DeviceSeq}）不是這個骨架（{skeleton.DeviceSeq}）", nameof(stored));
        return skeleton with { Enc = stored.Enc, Payload = stored.Payload, PayloadSha256 = stored.PayloadSha256, CoalesceKey = stored.CoalesceKey };
    }

    /// <summary>
    /// 把尾段（<see cref="OutboxRules.ApplyAck"/> 對 <paramref name="from"/> 起的尾段算出的結果，同樣長度與順序）原地接回 <paramref name="ordered"/>：
    /// 長度與順序不變。尾段要接到最後、每一列的 deviceSeq 相同，否則丟 <see cref="ArgumentException"/>、什麼都不改
    /// （計畫寫成回傳新的清單；原地接回才不必每次確認都複製整份）
    /// </summary>
    public static void Splice(List<OutboxEntry> ordered, int from, IReadOnlyList<OutboxEntry> tail)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(tail);
        if (from < 0 || from + tail.Count != ordered.Count)
            throw new ArgumentException($"尾段要接到最後：從 {from} 起 {tail.Count} 列，outbox 有 {ordered.Count} 列", nameof(tail));
        for (var i = 0; i < tail.Count; i++)
        {
            if (tail[i].DeviceSeq != ordered[from + i].DeviceSeq)
                throw new ArgumentException($"尾段的第 {i + 1} 列（{tail[i].DeviceSeq}）與原本（{ordered[from + i].DeviceSeq}）不同", nameof(tail));
        }

        for (var i = 0; i < tail.Count; i++)
            ordered[from + i] = tail[i];
    }

    /// <summary>
    /// 送出前要從 sync.db 讀回內容的骨架：從第一個待送（Pending、Retry）的列起、連續待送的最多 <paramref name="maxEvents"/> 列裡的骨架
    /// （<see cref="OutboxRules.NextBatch"/> 最多選這些；位元組上限只會讓它選得更少）。依 deviceSeq
    /// </summary>
    public static IReadOnlyList<long> ReloadNeeded(IReadOnlyList<OutboxEntry> ordered, int maxEvents, int knownDone = 0)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        var start = -1;
        for (var i = FirstActiveIndex(ordered, knownDone); i < ordered.Count; i++)
        {
            if (ordered[i].State is OutboxState.Pending or OutboxState.Retry)
            {
                start = i;
                break;
            }
        }

        if (start < 0)
            return [];
        var needed = new List<long>();
        for (var i = start; i < ordered.Count && i - start < maxEvents; i++)
        {
            var entry = ordered[i];
            if (entry.State is not (OutboxState.Pending or OutboxState.Retry))
                break;
            if (IsSkeleton(entry))
                needed.Add(entry.DeviceSeq);
        }

        return needed;
    }

    /// <summary>第一個 deviceSeq ≥ <paramref name="deviceSeq"/> 的位置（都比它小回 <c>Count</c>）</summary>
    private static int LowerBound(IReadOnlyList<OutboxEntry> ordered, long deviceSeq)
    {
        int lo = 0, hi = ordered.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (ordered[mid].DeviceSeq < deviceSeq)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private static string SharedKind(string kind)
    {
        foreach (var known in ConversationEventKinds.All)
        {
            if (string.Equals(known, kind, StringComparison.Ordinal))
                return known;
        }

        return kind;
    }

    private static string SharedEnc(string enc) => enc switch
    {
        PayloadRules.EncJson => PayloadRules.EncJson,
        PayloadRules.EncGzip => PayloadRules.EncGzip,
        _ => enc,
    };

    private static string? SharedReason(string? reason)
    {
        if (reason is null)
            return null;
        foreach (var known in ConversationRejectCodes.All)
        {
            if (string.Equals(known, reason, StringComparison.Ordinal))
                return known;
        }

        return reason switch
        {
            SyncErrorCodes.Invalid => SyncErrorCodes.Invalid,
            SyncErrorCodes.TooLarge => SyncErrorCodes.TooLarge,
            _ => reason,
        };
    }
}
