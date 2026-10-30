using Newtonsoft.Json;

namespace Myriagent.Sync;

/// <summary>拉到的一筆共享用量（依 seq 全部保留，歷史點的時間用伺服器收到的時間 CreatedAt）</summary>
internal sealed record SharedUsagePoint(long Seq, long CreatedAt, UsageSnapshotPayload Payload);

/// <summary>
/// 一頁拉取化簡後的結果：目錄類每個鍵只留 seq 最大的一筆，用量點全部保留。
/// <see cref="Ignored"/>＝略過的筆數（裝置寫的種類、不認得的種類、解不開的 payload；游標照樣前進）；
/// <see cref="IgnoredKinds"/>＝其中「不認得或解不開」的種類（異常，引擎記警告並顯示在 status.detail；裝置寫的種類是正常的，不列）；
/// <see cref="Conversations"/>＝第 4 階段的對話事件（<c>conv.*</c>，sync-protocol 14.2），依頁內順序、原樣（未解碼、未驗證），
/// 交給對話的處理（WP4.3 的 PlanConversationApply），不算略過也不警告
/// </summary>
internal sealed record PullDelta(
    IReadOnlyDictionary<string, (long Seq, AccountUpsertPayload? Upsert, AccountDeletedPayload? Deleted)> Accounts,
    IReadOnlyDictionary<string, DeviceUpsertPayload> Devices,
    IReadOnlyDictionary<string, DeviceRevokedPayload> Revoked,
    IReadOnlyDictionary<string, PrefsSetPayload> Prefs,
    IReadOnlyList<SharedUsagePoint> UsagePoints,
    int Ignored,
    IReadOnlyList<string> IgnoredKinds,
    IReadOnlyList<PulledEvent> Conversations);

/// <summary>
/// 拉取的化簡（sync-protocol 5.3、6）：只套用伺服器寫的種類（SyncEventKinds.IsServerAuthored）；排序一律看伺服器 seq、不看 CreatedAt
/// （不比較各電腦的時鐘）。同一個邏輯帳戶的 upsert 與 deleted 也取 seq 最大的：伺服器會讓刪掉的帳戶復活。
/// 對話事件（ConversationEventKinds，擁有者裝置寫的）另放 <see cref="PullDelta.Conversations"/>（WP4.1 步 10；之前當成不認得的種類警告）
/// </summary>
internal static class PullReduceRules
{
    /// <summary>伺服器寫的事件沒有大小上限（帳戶連結很多時 account.upsert 可能超過 128 KB）：解碼上限至少 4 MB</summary>
    public const int MinServerPayloadBytes = 4 * 1024 * 1024;

    public static int ServerPayloadLimit(SyncLimits limits) => Math.Max(limits.MaxPullBytes, MinServerPayloadBytes);

    public static PullDelta Reduce(IReadOnlyList<PulledEvent> page, int maxJsonBytes)
    {
        var accounts = new Dictionary<string, (long Seq, AccountUpsertPayload? Upsert, AccountDeletedPayload? Deleted)>(StringComparer.Ordinal);
        var devices = new Dictionary<string, (long Seq, DeviceUpsertPayload Payload)>(StringComparer.Ordinal);
        var revoked = new Dictionary<string, (long Seq, DeviceRevokedPayload Payload)>(StringComparer.Ordinal);
        var prefs = new Dictionary<string, (long Seq, PrefsSetPayload Payload)>(StringComparer.Ordinal);
        var usage = new List<SharedUsagePoint>();
        var conversations = new List<PulledEvent>();
        var ignored = 0;
        var ignoredKinds = new List<string>();

        void Anomaly(string? kind)
        {
            ignored++;
            var name = kind ?? "(null)";
            if (!ignoredKinds.Contains(name, StringComparer.Ordinal))
                ignoredKinds.Add(name);
        }

        foreach (var e in page)
        {
            if (e is null)
                continue;
            if (ConversationEventKinds.IsConversationKind(e.Kind))
            {
                conversations.Add(e);
                continue;
            }

            if (!SyncEventKinds.IsServerAuthored(e.Kind))
            {
                if (SyncEventKinds.IsDeviceAuthored(e.Kind))
                    ignored++;
                else
                    Anomaly(e.Kind);
                continue;
            }

            if (!PayloadRules.TryDecode(e.Enc, e.Payload, maxJsonBytes, out var json))
            {
                Anomaly(e.Kind);
                continue;
            }

            switch (e.Kind)
            {
                case SyncEventKinds.AccountUpsert when Decode<AccountUpsertPayload>(json) is { LogicalAccountId: { } id } upsert:
                    if (!accounts.TryGetValue(id, out var a) || e.Seq > a.Seq)
                        accounts[id] = (e.Seq, upsert, null);
                    break;
                case SyncEventKinds.AccountDeleted when Decode<AccountDeletedPayload>(json) is { LogicalAccountId: { } id } deleted:
                    if (!accounts.TryGetValue(id, out var d) || e.Seq > d.Seq)
                        accounts[id] = (e.Seq, null, deleted);
                    break;
                case SyncEventKinds.DeviceUpsert when Decode<DeviceUpsertPayload>(json) is { DeviceId: { } id } device:
                    if (!devices.TryGetValue(id, out var dv) || e.Seq > dv.Seq)
                        devices[id] = (e.Seq, device);
                    break;
                case SyncEventKinds.DeviceRevoked when Decode<DeviceRevokedPayload>(json) is { DeviceId: { } id } revoke:
                    if (!revoked.TryGetValue(id, out var rv) || e.Seq > rv.Seq)
                        revoked[id] = (e.Seq, revoke);
                    break;
                case SyncEventKinds.PrefsSet when Decode<PrefsSetPayload>(json) is { Key: { } key } pref:
                    if (!prefs.TryGetValue(key, out var pv) || e.Seq > pv.Seq)
                        prefs[key] = (e.Seq, pref);
                    break;
                case SyncEventKinds.UsageSnapshot when Decode<UsageSnapshotPayload>(json) is { LogicalAccountId: not null, Usage: not null } snapshot:
                    usage.Add(new SharedUsagePoint(e.Seq, e.CreatedAt, snapshot));
                    break;
                default:
                    Anomaly(e.Kind);
                    break;
            }
        }

        return new PullDelta(
            accounts,
            devices.ToDictionary(p => p.Key, p => p.Value.Payload, StringComparer.Ordinal),
            revoked.ToDictionary(p => p.Key, p => p.Value.Payload, StringComparer.Ordinal),
            prefs.ToDictionary(p => p.Key, p => p.Value.Payload, StringComparer.Ordinal),
            usage.OrderBy(p => p.Seq).ToList(),
            ignored,
            ignoredKinds,
            conversations);
    }

    /// <summary>
    /// v1 的 <c>cursors</c>：第 3 階段的類別（catalog、usage）只前進到這一頁裡該類別的最大 seq（不能直接設成 high：別的類別還沒拉到的會被跳過）。
    /// 對話的類別與串流不在這裡（<see cref="AdvanceConversationCursors"/>，存 v2 的 <c>conversation_cursors</c>；WP4.3 計畫步 14、契約步 11）
    /// </summary>
    public static IReadOnlyDictionary<string, long> AdvanceCursors(IReadOnlyDictionary<string, long> cursors, IReadOnlyList<PulledEvent> page)
    {
        var next = new Dictionary<string, long>(cursors, StringComparer.Ordinal);
        foreach (var e in page)
        {
            if (e?.Cls is not { } cls || cls is not (SyncClasses.Catalog or SyncClasses.Usage))
                continue;
            if (!next.TryGetValue(cls, out var current) || e.Seq > current)
                next[cls] = e.Seq;
        }

        return next;
    }

    /// <summary>
    /// 沒有對話盒子的引擎（WP4.1 的契約測試、不發布對話的測試引擎）：照第 3 階段原本的做法，頁裡出現的每個類別都前進到它的最大 seq
    /// （正式的引擎一律有盒子，走 <see cref="AdvanceCursors"/>＋<see cref="AdvanceConversationCursors"/>）
    /// </summary>
    public static IReadOnlyDictionary<string, long> AdvanceEveryClass(IReadOnlyDictionary<string, long> cursors, IReadOnlyList<PulledEvent> page)
    {
        var next = new Dictionary<string, long>(cursors, StringComparer.Ordinal);
        foreach (var e in page)
        {
            if (e?.Cls is not { } cls)
                continue;
            if (!next.TryGetValue(cls, out var current) || e.Seq > current)
                next[cls] = e.Seq;
        }

        return next;
    }

    /// <summary>
    /// v2 的 <c>conversation_cursors</c>（鍵是類別 <c>meta</c> 或串流 <c>c:{id}</c>）：這一頁「有要」的對話游標（<paramref name="requested"/> 裡類別是 meta 的、
    /// 有串流的）各自只前進到頁裡符合它的事件的最大 seq——類別看 <see cref="PulledEvent.Cls"/>、串流看 <see cref="PulledEvent.Stream"/>，同伺服器挑事件的規則
    /// （EventLog.Pull：每個游標拿到的是它待拉事件的前綴）。沒要的不動（頁裡不會有它的全部事件）；第 3 階段的類別不在這裡；游標不倒退。
    /// 不認得的種類、不合格的事件也算（略過但游標照樣前進）
    /// </summary>
    public static IReadOnlyDictionary<string, long> AdvanceConversationCursors(IReadOnlyDictionary<string, long> cursors, IReadOnlyList<PullCursor> requested,
        IReadOnlyList<PulledEvent> page)
    {
        var next = new Dictionary<string, long>(cursors, StringComparer.Ordinal);
        var classes = new HashSet<string>(StringComparer.Ordinal);
        var streams = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cursor in requested)
        {
            if (cursor?.Stream is { } stream)
                streams.Add(stream);
            else if (cursor?.Cls is SyncClasses.Meta or SyncClasses.Item)
                classes.Add(cursor.Cls);
        }

        void Advance(string key, long seq)
        {
            if (!next.TryGetValue(key, out var current) || seq > current)
                next[key] = seq;
        }

        foreach (var e in page)
        {
            if (e is null)
                continue;
            if (e.Cls is { } cls && classes.Contains(cls))
                Advance(cls, e.Seq);
            if (e.Stream is { } stream && streams.Contains(stream))
                Advance(stream, e.Seq);
        }

        return next;
    }

    private static T? Decode<T>(string json) where T : class
    {
        try
        {
            return SyncJson.Deserialize<T>(json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}
