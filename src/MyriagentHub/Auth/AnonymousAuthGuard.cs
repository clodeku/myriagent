using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Auth;

/// <summary>某個分區（IP 或 IPv6 /64）在匿名認證端點的狀態</summary>
internal sealed record BackoffState(int ConsecutiveFailures, long BlockedUntil, long[] RecentAttempts)
{
    public static readonly BackoffState Empty = new(0, 0, []);
}

/// <summary>
/// 密碼驗證的退避規則（8.5、SEC-07）：每小時嘗試上限；連續失敗達門檻後該分區退避一段時間。
/// 未登入的登入以 IP 分區；已登入後的再驗證與改密碼以工作階段分區，不受 IP 退避影響（家中 NAT 後的所有電腦共用一個 IP）。
/// </summary>
internal static class AuthBackoffRules
{
    public const long HourMs = 3_600_000;

    public static bool IsBlocked(BackoffState state, long now) => now < state.BlockedUntil;

    /// <summary>嘗試一次：先清掉一小時前的紀錄，超過上限就拒絕（不記錄這次）</summary>
    public static (bool Allowed, BackoffState State) TryAttempt(BackoffState state, long now, int perHour)
    {
        var recent = state.RecentAttempts.Where(t => now - t < HourMs).ToArray();
        if (recent.Length >= perHour)
            return (false, state with { RecentAttempts = recent });
        return (true, state with { RecentAttempts = [.. recent, now] });
    }

    public static BackoffState RecordFailure(BackoffState state, long now, int threshold, long backoffMs)
    {
        var failures = state.ConsecutiveFailures + 1;
        return failures >= threshold
            ? state with { ConsecutiveFailures = 0, BlockedUntil = now + backoffMs }
            : state with { ConsecutiveFailures = failures };
    }

    public static BackoffState RecordSuccess(BackoffState state) => state with { ConsecutiveFailures = 0 };

    /// <summary>退避結束還要幾秒（Retry-After 用）</summary>
    public static int RetryAfterSeconds(BackoffState state, long now) =>
        (int)Math.Max(1, Math.Ceiling((state.BlockedUntil - now) / 1000.0));
}

/// <summary>密碼驗證的退避與全域失敗計數（記憶體內；重啟歸零無妨，攻擊者無法藉此重啟服務）</summary>
internal sealed class AnonymousAuthGuard(TimeProvider time, IOptions<HubOptions> options)
{
    /// <summary>分區數上限：IP 噴灑時不讓記憶體無限長大（超過就丟掉最舊的一半）</summary>
    public const int MaxPartitions = 10_000;

    private readonly object gate = new();
    private readonly Dictionary<string, BackoffState> states = new(StringComparer.Ordinal);
    private readonly Queue<long> globalFailures = new();

    private RateLimitSettings Settings => options.Value.RateLimits;

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>回傳 null＝放行；否則是要等的秒數（429 Retry-After）</summary>
    public int? TryEnter(string partition)
    {
        if (!Settings.Enabled)
            return null;
        lock (gate)
        {
            var now = Now;
            var state = Get(partition);
            if (AuthBackoffRules.IsBlocked(state, now))
                return AuthBackoffRules.RetryAfterSeconds(state, now);
            var (allowed, next) = AuthBackoffRules.TryAttempt(state, now, Settings.LoginPerHour);
            Set(partition, next);
            return allowed ? null : 60;
        }
    }

    public void RecordFailure(string partition)
    {
        lock (gate)
        {
            var now = Now;
            Set(partition, AuthBackoffRules.RecordFailure(Get(partition), now, Settings.FailuresBeforeBackoff, Settings.BackoffMinutes * 60_000L));
            globalFailures.Enqueue(now);
            Trim(now);
        }
    }

    public void RecordSuccess(string partition)
    {
        lock (gate)
            Set(partition, AuthBackoffRules.RecordSuccess(Get(partition)));
    }

    /// <summary>最近一小時的全域登入失敗次數（≥20 次告警，不做全域鎖定以免攻擊者藉此把擁有者鎖在門外）</summary>
    public int GlobalFailuresLastHour
    {
        get
        {
            lock (gate)
            {
                Trim(Now);
                return globalFailures.Count;
            }
        }
    }

    private BackoffState Get(string partition) => states.TryGetValue(partition, out var s) ? s : BackoffState.Empty;

    private void Set(string partition, BackoffState state)
    {
        if (states.Count >= MaxPartitions && !states.ContainsKey(partition))
        {
            foreach (var key in states.OrderBy(kv => kv.Value.RecentAttempts.LastOrDefault()).Take(MaxPartitions / 2).Select(kv => kv.Key).ToList())
                states.Remove(key);
        }
        states[partition] = state;
    }

    private void Trim(long now)
    {
        while (globalFailures.Count > 0 && now - globalFailures.Peek() >= AuthBackoffRules.HourMs)
            globalFailures.Dequeue();
    }
}
