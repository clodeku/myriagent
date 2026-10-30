namespace Myriagent.Usage;

/// <summary>一個額度時間窗（例如 5 小時、每週、每週・某模型）的使用率</summary>
internal sealed record UsageWindow(string Key, string Label, double Percent, DateTimeOffset? ResetsAt, bool Locked = false);

/// <summary>帳戶的用量快照</summary>
internal sealed record UsageSnapshot(
    DateTimeOffset FetchedAt,
    string Source,
    IReadOnlyList<UsageWindow> Windows,
    string? Credits,
    string? Note,
    string? Error,
    bool Stale)
{
    public static UsageSnapshot Empty(string? error = null)
        => new(DateTimeOffset.MinValue, "cache", Array.Empty<UsageWindow>(), null, null, error, true);
}
