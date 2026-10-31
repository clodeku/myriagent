using System.Globalization;
using System.Text;
using Myriagent.Security;
using Myriagent.Usage;

namespace Myriagent.Sync;

/// <summary>這台對某個邏輯帳戶的用量查詢指派</summary>
internal enum UsageAssignmentState
{
    /// <summary>連線後還沒收到指派（ProveResponse 只列 on 的）</summary>
    Unknown,

    On,

    Off,
}

/// <summary>伺服器的用量指派（term 遞增，舊 term 的不採用）</summary>
internal sealed record UsageAssignment(long Term, bool On);

/// <summary>本機帳戶在目錄裡的連結狀態（account.upsert 的 links 裡自己那一格）</summary>
internal sealed record UsageLinkState(string LogicalAccountId, bool Ready, bool IdentityOk);

/// <summary>
/// 引擎發布、UI 執行緒以 Volatile 讀的快照（IUsagePollPolicy 用）。<see cref="Links"/> 以本機帳戶 id 為鍵，
/// <see cref="Assignments"/> 以邏輯帳戶 id 為鍵；<see cref="DisconnectedSince"/> 是不在 Live 起算的時間
/// </summary>
internal sealed record UsagePollSnapshot(
    bool Paired,
    SyncEngineState State,
    DateTimeOffset? DisconnectedSince,
    IReadOnlyDictionary<string, UsageLinkState> Links,
    IReadOnlyDictionary<string, UsageAssignment> Assignments);

/// <summary>這次用量要不要上傳、怎麼傳</summary>
internal enum UsageUpload
{
    None,

    /// <summary>負責查詢的那台：帶 term（source 必須是 api）</summary>
    Termed,

    /// <summary>對話中的 rate limit 事件：不帶 term（source 必須是 session），每帳戶每 60 秒最多一次</summary>
    Session,
}

/// <summary>
/// 共享用量的裝置端規則（sync-protocol 6.4、設計 6.6；S12：同一個邏輯帳戶同時只有一台查官方用量）。
/// 排序與「誰比較新」一律看伺服器的 seq 與 term，不比較各電腦的時鐘（FetchedAt 只供顯示）
/// </summary>
internal static class UsageSyncRules
{
    /// <summary>與伺服器斷線超過這麼久，自動回到本機各自查詢（sync-protocol 6.4）</summary>
    public static readonly TimeSpan OfflineTakeover = TimeSpan.FromMinutes(5);

    /// <summary>不帶 term 的上傳間隔（伺服器端是 50 秒，留 10 秒的計時誤差）</summary>
    public static readonly TimeSpan SessionUploadInterval = TimeSpan.FromSeconds(60);

    public const string SourceApi = "api";

    public const string SourceSession = "session";

    public const string SourceShared = "shared";

    public const int MaxWindows = 8;

    public const int MaxWindowKeyLength = UsageWindowKeyRules.MaxLength;

    public const double MaxPercent = 1000;

    /// <summary><see cref="DateTimeOffset.FromUnixTimeMilliseconds"/> 收得下的最大值（9999-12-31T23:59:59.999Z）；超過就丟例外</summary>
    public const long MaxUnixMs = 253402300799999;

    /// <summary>
    /// 這台現在要不要自己查官方用量（IUsagePollPolicy.ShouldPoll；手動「更新用量」一樣適用）：
    /// 沒有快照、未配對、暫停、停止、待確認、只更新（伺服器不分配用量）→照本機原本的頻率查；這個帳戶沒有連結、連結不是 ready 或身分不符→查；
    /// Live、Draining、Handshaking→只有被指派（On）才查；斷線（Idle、Connecting、Backoff）未滿 5 分鐘不查、滿了查
    /// </summary>
    public static bool ShouldPoll(UsagePollSnapshot? s, string localAccountId, DateTimeOffset now)
    {
        if (s is null || !s.Paired)
            return true;
        switch (s.State)
        {
            case SyncEngineState.Paused or SyncEngineState.Stopped or SyncEngineState.NeedsConfirmation or SyncEngineState.UpdateOnly:
                return true;
        }

        if (!s.Links.TryGetValue(localAccountId, out var link) || !link.Ready || !link.IdentityOk)
            return true;
        return s.State switch
        {
            SyncEngineState.Live or SyncEngineState.Draining or SyncEngineState.Handshaking => StateOf(s, link.LogicalAccountId) == UsageAssignmentState.On,
            // 不知道從何時斷線就當成已經很久：離線優先，寧可多查也不讓用量停住
            SyncEngineState.Idle or SyncEngineState.Connecting or SyncEngineState.Backoff =>
                s.DisconnectedSince is not { } since || now - since >= OfflineTakeover,
            _ => throw new ArgumentOutOfRangeException(nameof(s), s.State, "不認得的同步狀態"),
        };
    }

    public static UsageAssignmentState StateOf(UsagePollSnapshot s, string logicalAccountId) =>
        s.Assignments.TryGetValue(logicalAccountId, out var assignment)
            ? assignment.On ? UsageAssignmentState.On : UsageAssignmentState.Off
            : UsageAssignmentState.Unknown;

    /// <summary>
    /// ProveResponse 的 usageAssignments（只列 on 的，UsageCoordinator）：清單裡的照 term 合併；表裡是 On 而清單沒列的改成 Off（term 不變）
    /// </summary>
    public static IReadOnlyDictionary<string, UsageAssignment> FromProve(IReadOnlyDictionary<string, UsageAssignment> current, IReadOnlyList<UsageAssign>? list)
    {
        var listed = (list ?? []).Where(a => a is not null).Select(a => a.LogicalAccountId).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, UsageAssignment>(StringComparer.Ordinal);
        foreach (var (id, assignment) in current)
            result[id] = assignment.On && !listed.Contains(id) ? assignment with { On = false } : assignment;
        IReadOnlyDictionary<string, UsageAssignment> merged = result;
        foreach (var assign in list ?? [])
        {
            if (assign is not null)
                merged = Merge(merged, assign);
        }

        return merged;
    }

    /// <summary>UsageAssign 的到達順序不保證：term 較大才覆蓋，同 term 照收（同一個 term 只會有一台 on），較小的丟掉</summary>
    public static IReadOnlyDictionary<string, UsageAssignment> Merge(IReadOnlyDictionary<string, UsageAssignment> current, UsageAssign incoming)
    {
        if (!SyncIds.IsLogicalAccountId(incoming.LogicalAccountId))
            return current;
        if (current.TryGetValue(incoming.LogicalAccountId, out var existing) && incoming.Term < existing.Term)
            return current;
        var result = new Dictionary<string, UsageAssignment>(current, StringComparer.Ordinal)
        {
            [incoming.LogicalAccountId] = new UsageAssignment(incoming.Term, incoming.On),
        };
        return result;
    }

    /// <summary>
    /// 本機的用量快照 → 線上格式（照伺服器 UsageRules.Validate 的上限清理）：時間窗最多 8 個、鍵經 <see cref="UsageWindowKeyRules.Encode"/>
    /// 轉成伺服器收的字元（轉不了的丟掉、重複的丟掉）、名稱截到 60 字後清理（清理後是空的用鍵）、百分比夾在 0～1000（非有限值丟掉該窗）、
    /// resetsAt ≤0 改成 null、文字欄位截斷並去掉控制字元；FetchedAt ≤0（Antigravity 的 MinValue）回 null
    /// </summary>
    public static SharedUsage? ToShared(UsageSnapshot snapshot)
    {
        var fetchedAt = snapshot.FetchedAt.ToUnixTimeMilliseconds();
        if (fetchedAt <= 0)
            return null;
        var windows = new List<SharedUsageWindow>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in snapshot.Windows)
        {
            if (windows.Count == MaxWindows)
                break;
            if (w is null || UsageWindowKeyRules.Encode(w.Key) is not { } key || !keys.Add(key) || !double.IsFinite(w.Percent))
                continue;
            var resetsAt = w.ResetsAt?.ToUnixTimeMilliseconds();
            windows.Add(new SharedUsageWindow(key, CleanWindowLabel(w.Label) ?? key, Math.Clamp(w.Percent, 0, MaxPercent), resetsAt is > 0 ? resetsAt : null, w.Locked));
        }

        return new SharedUsage(
            fetchedAt,
            snapshot.Source,
            windows,
            CatalogSyncRules.CleanText(snapshot.Credits, CatalogSyncRules.MaxShortTextLength),
            CatalogSyncRules.CleanText(snapshot.Note, CatalogSyncRules.MaxTextLength),
            CatalogSyncRules.CleanText(snapshot.Error, CatalogSyncRules.MaxTextLength),
            snapshot.Stale,
            snapshot.ResetCredits is { } count ? Math.Clamp(count, 0, UsageRules.MaxResetCredits) : null,
            snapshot.ResetCreditExpiresAt?.ToUnixTimeMilliseconds() is > 0 and var expires ? expires : null);
    }

    /// <summary>
    /// 要不要上傳（只看本機快照的 Source，避免回音）：
    /// api、這台被指派（On）、Live、連結 ready 且身分相符、不是 Stale、FetchedAt 和上次帶 term 上傳的不同→Termed；
    /// session、連結 ready 且身分相符、Live、不是 Stale、距上次至少 60 秒→Session；其他（shared、cache、none）一律 None
    /// </summary>
    public static UsageUpload Decide(UsageSnapshot s, UsageAssignmentState assignment, bool live, UsageLinkState? link,
        DateTimeOffset? lastTermedFetchedAt, DateTimeOffset? lastSessionUploadAt, DateTimeOffset now)
    {
        if (!live || link is not { Ready: true, IdentityOk: true } || s.Stale || s.FetchedAt.ToUnixTimeMilliseconds() <= 0)
            return UsageUpload.None;
        return s.Source switch
        {
            SourceApi when assignment == UsageAssignmentState.On && s.FetchedAt != lastTermedFetchedAt => UsageUpload.Termed,
            SourceSession when lastSessionUploadAt is not { } last || now - last >= SessionUploadInterval => UsageUpload.Session,
            _ => UsageUpload.None,
        };
    }

    /// <summary>
    /// 拉到的 usage.snapshot → 本機快照（Source="shared"）；自己發的回 null。FetchedAt 只供顯示，不參與任何比較。
    /// 時間超過 <see cref="MaxUnixMs"/> 的不轉換（伺服器只檢查 &gt;0，有問題的裝置送來的值不能讓拉取丟例外、游標卡住）：
    /// FetchedAt 超過就整筆回 null，某一窗的 resetsAt 超過就當成沒有
    /// </summary>
    public static UsageSnapshot? FromShared(UsageSnapshotPayload p, string selfDeviceId)
    {
        if (p.SourceDeviceId == selfDeviceId || p.Usage is not { } usage)
            return null;
        if (usage.FetchedAt > MaxUnixMs)
            return null;
        var windows = (usage.Windows ?? [])
            .Where(w => w is not null && w.Key is not null && double.IsFinite(w.Percent))
            .Select(w => new UsageWindow(
                UsageWindowKeyRules.Decode(w.Key),
                w.Label ?? w.Key,
                Math.Clamp(w.Percent, 0, MaxPercent),
                w.ResetsAt is > 0 and <= MaxUnixMs ? DateTimeOffset.FromUnixTimeMilliseconds(w.ResetsAt.Value) : null,
                w.Locked))
            .ToList();
        return new UsageSnapshot(
            usage.FetchedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(usage.FetchedAt) : DateTimeOffset.MinValue,
            SourceShared,
            windows,
            usage.Credits,
            usage.Note,
            usage.Error,
            usage.Stale,
            usage.ResetCredits is { } count ? Math.Clamp(count, 0, UsageRules.MaxResetCredits) : null,
            usage.ResetCreditExpiresAt is > 0 and <= MaxUnixMs ? DateTimeOffset.FromUnixTimeMilliseconds(usage.ResetCreditExpiresAt.Value) : null);
    }

    /// <summary>
    /// remoteOnly 卡片（這台沒有連結的邏輯帳戶，WP3.4）的共享用量：照 <see cref="FromShared"/> 驗證（自己發的、FetchedAt 超出範圍的回 null，
    /// 格式不對丟 ArgumentException），再以 <see cref="ToShared"/> 清理成線上格式（時間窗最多 8 個、百分比夾在範圍內、非有限值丟掉、文字截斷）。
    /// FetchedAt ≤0 回 null；Source 一律 shared
    /// </summary>
    public static SharedUsage? ForRemoteOnly(UsageSnapshotPayload p, string selfDeviceId)
        => FromShared(p, selfDeviceId) is { } snapshot ? ToShared(snapshot) : null;

    /// <summary>追趕積壓的期間，本機的目前值是自己查到的（api／session）時共享的只記歷史、不蓋目前值；Live 時一律取代</summary>
    public static bool ReplaceCurrent(bool catchingUp, string? localSource) =>
        !catchingUp || localSource is not (SourceApi or SourceSession);

    private static string? CleanWindowLabel(string? label)
    {
        if (LabelRules.Clean(label, int.MaxValue) is not { } cleaned)
            return null;
        if (cleaned.Length > CatalogSyncRules.MaxLabelLength)
        {
            var builder = new StringBuilder(CatalogSyncRules.MaxLabelLength);
            var elements = StringInfo.GetTextElementEnumerator(cleaned);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                if (builder.Length + element.Length > CatalogSyncRules.MaxLabelLength)
                    break;
                builder.Append(element);
            }

            cleaned = builder.ToString();
        }

        return LabelRules.Clean(cleaned, CatalogSyncRules.MaxLabelLength);
    }
}
