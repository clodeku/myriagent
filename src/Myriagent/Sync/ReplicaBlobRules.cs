using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>要取的一個 blob（同一頁裡同一個內容只取一次）</summary>
internal sealed record ReplicaBlobWant(string Sha256, long Length);

/// <summary>一筆要預取 blob 的 <c>conv.item</c>（依頁裡的順序）</summary>
internal sealed record ReplicaBlobEvent(string Stream, string ConversationId, long Seq, IReadOnlyList<string> Shas);

/// <summary>
/// 一個串流這一頁停在哪裡：<paramref name="Seq"/> 起只經串流拉到的事件這一頁不套用、串流的游標不越過它（下次從它重拉）。
/// <paramref name="Transient"/>＝取 blob 暫時失敗（連不上、伺服器錯誤、沒有權杖；過一陣子再拉）；false＝這一頁的記憶體上限放不下（馬上再拉下一頁）
/// </summary>
internal sealed record ReplicaBlobHold(string Stream, string ConversationId, long Seq, bool Transient);

/// <summary>一頁的預取計畫（<see cref="ReplicaBlobRules.Plan"/>）</summary>
internal sealed record ReplicaBlobPlan(IReadOnlyList<ReplicaBlobWant> Wants, IReadOnlyList<ReplicaBlobEvent> Events, IReadOnlyDictionary<string, ReplicaBlobHold> BudgetHolds)
{
    public static ReplicaBlobPlan Empty { get; } = new([], [], new Dictionary<string, ReplicaBlobHold>(StringComparer.Ordinal));
}

internal enum ReplicaBlobOutcome
{
    /// <summary>取到了，大小與 SHA-256 都對</summary>
    Fetched,

    /// <summary>永久取不到（伺服器沒有、大小或內容不符）：那筆項目照 WP4.3 當成轉不回來，游標照樣前進</summary>
    Missing,

    /// <summary>暫時取不到（連不上、伺服器錯誤、沒有權杖、逾時）：那個串流這一頁停住，之後重拉</summary>
    Failed,
}

/// <summary>取一個 blob 的結果（<see cref="Content"/> 只在 <see cref="ReplicaBlobOutcome.Fetched"/> 有值）</summary>
internal sealed record ReplicaBlobFetch(ReplicaBlobOutcome Outcome, byte[]? Content)
{
    public static ReplicaBlobFetch Missing { get; } = new(ReplicaBlobOutcome.Missing, null);

    public static ReplicaBlobFetch Failed { get; } = new(ReplicaBlobOutcome.Failed, null);

    public static ReplicaBlobFetch Fetched(byte[] content) => new(ReplicaBlobOutcome.Fetched, content);
}

/// <summary>一頁的預取結果：解碼用的內容（以 SHA-256 取）與停住的串流（串流 → 停在哪裡）</summary>
internal sealed record ReplicaBlobPage(Func<string, byte[]?> Resolve, IReadOnlyDictionary<string, ReplicaBlobHold> Holds)
{
    public static ReplicaBlobPage Empty { get; } = new(_ => null, new Dictionary<string, ReplicaBlobHold>(StringComparer.Ordinal));
}

/// <summary>
/// 複本的 blob 預取（WP4.7 計畫步 12，D39）的純函式：其他電腦的大項目欄位是 blob 參照（<c>{blob, length}</c>，步 10），
/// 複本要在解碼一頁之前以 E6 取回內容。
/// <list type="bullet">
/// <item><see cref="Plan"/>：依頁裡的順序收集要取的 blob；同一頁合計 ≤ <see cref="MaxPageBytes"/>（32 MB）、單一個 ≤ <see cref="MaxBlobBytes"/>（4 MB）。
/// 放不下的那筆起，那個串流這一頁停住（下一頁再取）；單獨一筆就超過上限、或有超過單一上限的 blob 的，那筆不取（永久當成轉不回來，不會卡住）。</item>
/// <item><see cref="Holds"/>：有一個暫時取不到的，那筆起那個串流停住（游標不前進、之後重拉；4.3 計畫:1202 的「略過並前進」只適用永久取不到的）。
/// 沒有 media1 的連線一個都取不到，要取的那筆起一律停住（<see cref="HoldsWithoutMedia"/>）。</item>
/// <item><see cref="WithoutHeld"/>、<see cref="CapHeld"/>：停住的串流裡、停住那筆起只經串流拉到的事件這一頁不套用，串流的游標只前進到它之前；
/// meta 類的事件（也經 meta 的游標拉到的）照常套用（重拉時依 seq 不會重做）。</item>
/// <item><see cref="Inline"/>：取到的內容換回 payload 再存進 replica_items（之後打開複本時不必再取）。</item>
/// </list>
/// 純函式、不碰網路與檔案系統
/// </summary>
internal static class ReplicaBlobRules
{
    /// <summary>每頁同時取幾個</summary>
    public const int MaxConcurrent = 4;

    /// <summary>每頁取回的內容合計上限（記憶體）</summary>
    public const long MaxPageBytes = 32L * 1024 * 1024;

    /// <summary>單一個 blob 的上限（同伺服器的項目 blob 上限 <c>Hub:Media:MaxItemBlobBytes</c>、桌面的 <see cref="ItemBlobRules.MaxItemBlobBytes"/>）</summary>
    public const long MaxBlobBytes = ItemBlobRules.MaxItemBlobBytes;

    /// <summary>暫時取不到時多久之後再拉那些串流</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>payload 裡的 blob 參照（<see cref="ConversationEventRules.BlobFields"/> 上的 <see cref="ConversationEventRules.TryReadBlobRef"/>，照欄位順序）</summary>
    public static IReadOnlyList<ConversationBlobRef> Collect(JObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var refs = new List<ConversationBlobRef>();
        foreach (var name in ConversationEventRules.BlobFields)
        {
            if (ConversationEventRules.TryReadBlobRef(payload[name], out var blobRef))
                refs.Add(blobRef);
        }

        return refs;
    }

    /// <summary>
    /// 一頁的預取計畫：<paramref name="eligible"/> 為真的 <c>conv.item</c>（引擎判斷：這一頁要的串流、不是這台寫的、不是本機的對話）依序收集 blob。
    /// payload 解不開的不管（套用時照樣算不合格）
    /// </summary>
    public static ReplicaBlobPlan Plan(IReadOnlyList<PulledEvent> events, Func<PulledEvent, bool> eligible, long maxPageBytes = MaxPageBytes, long maxBlobBytes = MaxBlobBytes)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(eligible);
        var wanted = new Dictionary<string, long>(StringComparer.Ordinal);
        var wants = new List<ReplicaBlobWant>();
        var planned = new List<ReplicaBlobEvent>();
        var holds = new Dictionary<string, ReplicaBlobHold>(StringComparer.Ordinal);
        long total = 0;
        foreach (var e in events)
        {
            if (e is null || e.Kind != ConversationEventKinds.Item || e.Stream is not { } stream || e.ConversationId is not { } conversationId
                || holds.ContainsKey(stream) || !eligible(e))
                continue;
            if (!PayloadRules.TryDecode(e.Enc, e.Payload, ConversationEventRules.MaxPayloadBytes, out var json)
                || !ConversationEventRules.TryParseStrict(json, out var token) || token is not JObject payload)
                continue;
            var refs = Collect(payload);
            if (refs.Count == 0 || refs.Any(r => r.Length > maxBlobBytes))
                continue;
            var fresh = refs.Where(r => !wanted.ContainsKey(r.Blob)).DistinctBy(r => r.Blob, StringComparer.Ordinal).ToList();
            var bytes = fresh.Sum(r => r.Length);
            if (bytes > maxPageBytes)
                continue;
            if (total + bytes > maxPageBytes)
            {
                holds[stream] = new ReplicaBlobHold(stream, conversationId, e.Seq, Transient: false);
                continue;
            }

            foreach (var r in fresh)
            {
                wanted[r.Blob] = r.Length;
                wants.Add(new ReplicaBlobWant(r.Blob, r.Length));
            }

            total += bytes;
            planned.Add(new ReplicaBlobEvent(stream, conversationId, e.Seq, refs.Select(r => r.Blob).Distinct(StringComparer.Ordinal).ToList()));
        }

        return new ReplicaBlobPlan(wants, planned, holds);
    }

    /// <summary>
    /// 停住的串流：計畫裡放不下的（不是暫時的），加上有任何一個 blob 暫時取不到（或沒有結果）的那筆起（暫時的）；
    /// 同一個串流取最早的那筆
    /// </summary>
    public static IReadOnlyDictionary<string, ReplicaBlobHold> Holds(ReplicaBlobPlan plan, IReadOnlyDictionary<string, ReplicaBlobFetch> results)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(results);
        var holds = new Dictionary<string, ReplicaBlobHold>(plan.BudgetHolds, StringComparer.Ordinal);
        foreach (var e in plan.Events)
        {
            if (!e.Shas.Any(sha => !results.TryGetValue(sha, out var r) || r.Outcome == ReplicaBlobOutcome.Failed))
                continue;
            if (holds.TryGetValue(e.Stream, out var existing) && existing.Seq <= e.Seq)
                continue;
            holds[e.Stream] = new ReplicaBlobHold(e.Stream, e.ConversationId, e.Seq, Transient: true);
        }

        return holds;
    }

    /// <summary>
    /// 沒有 media1 的連線（伺服器回滾到 WP4.7 之前）：一個 blob 都取不到。計畫裡要取的（含這一頁放不下的）那筆起，那個串流停住、全部算暫時的
    /// （合併前審查 DI-3、INT-6：照永久取不到前進的話，存進 replica_items 的是參照版本，游標越過它，之後協商出 media1 也不會回頭重取）；
    /// 同一個串流取最早的那筆
    /// </summary>
    public static IReadOnlyDictionary<string, ReplicaBlobHold> HoldsWithoutMedia(ReplicaBlobPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var holds = new Dictionary<string, ReplicaBlobHold>(StringComparer.Ordinal);
        foreach (var hold in plan.BudgetHolds.Values)
            holds[hold.Stream] = hold with { Transient = true };
        foreach (var e in plan.Events)
        {
            if (holds.TryGetValue(e.Stream, out var existing) && existing.Seq <= e.Seq)
                continue;
            holds[e.Stream] = new ReplicaBlobHold(e.Stream, e.ConversationId, e.Seq, Transient: true);
        }

        return holds;
    }

    /// <summary>解碼用的內容（只有取到的；其他回 null）</summary>
    public static Func<string, byte[]?> Resolver(IReadOnlyDictionary<string, ReplicaBlobFetch> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var fetched = results.Where(p => p.Value.Outcome == ReplicaBlobOutcome.Fetched && p.Value.Content is not null)
            .ToDictionary(p => p.Key, p => p.Value.Content!, StringComparer.Ordinal);
        return sha => fetched.GetValueOrDefault(sha);
    }

    /// <summary>取到的內容大小與 SHA-256 都對</summary>
    public static bool Verify(ReplicaBlobWant want, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(want);
        ArgumentNullException.ThrowIfNull(content);
        return content.LongLength == want.Length && string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), want.Sha256, StringComparison.Ordinal);
    }

    /// <summary>
    /// 這一頁要套用的事件：停住的串流裡、停住那筆起只經串流拉到的事件拿掉（<paramref name="classes"/> 是這一頁有要的對話類別，
    /// 例如 meta：類別也拉到的事件照常套用）
    /// </summary>
    public static IReadOnlyList<PulledEvent> WithoutHeld(IReadOnlyList<PulledEvent> events, IReadOnlyDictionary<string, ReplicaBlobHold> holds, IReadOnlySet<string> classes)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(holds);
        ArgumentNullException.ThrowIfNull(classes);
        if (holds.Count == 0)
            return events;
        return events.Where(e => e is null || !IsHeld(e, holds, classes)).ToList();
    }

    /// <summary>
    /// 對話游標：停住的串流只前進到停住那筆之前（這一頁裡那個串流 seq 比它小的事件的最大值；沒有的話維持 <paramref name="previous"/> 的值，
    /// 原本沒有就拿掉）；其他照 <paramref name="next"/>
    /// </summary>
    public static IReadOnlyDictionary<string, long> CapHeld(IReadOnlyDictionary<string, long> next, IReadOnlyDictionary<string, long> previous,
        IReadOnlyList<PulledEvent> page, IReadOnlyDictionary<string, ReplicaBlobHold> holds)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(holds);
        if (holds.Count == 0)
            return next;
        var capped = new Dictionary<string, long>(next, StringComparer.Ordinal);
        foreach (var (stream, hold) in holds)
        {
            long? best = previous.TryGetValue(stream, out var before) ? before : null;
            foreach (var e in page)
            {
                if (e is not null && e.Stream == stream && e.Seq < hold.Seq && (best is null || e.Seq > best))
                    best = e.Seq;
            }

            if (best is { } value)
                capped[stream] = value;
            else
                capped.Remove(stream);
        }

        return capped;
    }

    /// <summary>
    /// 存進 replica_items 的 payload：有 blob 參照而且全部取得到、換回之後通過 <see cref="ConversationWire.TryReadItem"/>（雜湊、長度、嚴格 UTF-8、形狀）時，
    /// 回換回內容的 payload（字串欄位是文字、陣列欄位是陣列；其他欄位與順序照原樣）；否則回原本的 <paramref name="json"/>（取不到的照 WP4.3 轉不回來）
    /// </summary>
    public static string Inline(JObject payload, string json, Func<string, byte[]?>? blobs)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(json);
        if (blobs is null || Collect(payload).Count == 0 || !ConversationWire.TryReadItem(payload, blobs, out _))
            return json;
        var resolved = (JObject)payload.DeepClone();
        foreach (var name in ConversationEventRules.BlobFields)
        {
            if (!ConversationEventRules.TryReadBlobRef(resolved[name], out var blobRef))
                continue;
            if (blobs(blobRef.Blob) is not { } content || TryText(content) is not { } text)
                return json;
            if (!ConversationEventRules.ArrayFields.Contains(name))
            {
                resolved[name] = text;
                continue;
            }

            if (!ConversationEventRules.TryParseStrict(text, out var token) || token is not JArray array)
                return json;
            resolved[name] = array;
        }

        return ConversationWire.Serialize(resolved);
    }

    /// <summary>嚴格 UTF-8 解碼；不合格回 null（<see cref="ConversationWire.TryReadItem"/> 已經驗過，這裡是第二道）</summary>
    private static string? TryText(byte[] content)
    {
        try
        {
            return StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool IsHeld(PulledEvent e, IReadOnlyDictionary<string, ReplicaBlobHold> holds, IReadOnlySet<string> classes)
        => e.Stream is { } stream && holds.TryGetValue(stream, out var hold) && e.Seq >= hold.Seq && !(e.Cls is { } cls && classes.Contains(cls));
}
