using System.Security.Cryptography;
using System.Text;

namespace MyriagentHub.Tls;

/// <summary>汰換下來、24 小時後才刪的憑證</summary>
internal sealed record RetiredCertificate(string Thumbprint, long RetiredAt);

/// <summary>TLS 與 ACME 的持久狀態（kv acme_state）</summary>
internal sealed record TlsState
{
    /// <summary>正式（ACME）憑證的指紋；null＝還沒有正式憑證</summary>
    public string? CurrentThumbprint { get; init; }

    public string? CurrentChainPem { get; init; }

    /// <summary>ARI 的憑證識別碼（RFC 9773），續期時也當 replaces</summary>
    public string? CurrentAriId { get; init; }

    public long? NotBefore { get; init; }

    public long? NotAfter { get; init; }

    public string? Issuer { get; init; }

    public string? Domain { get; init; }

    public string? Directory { get; init; }

    /// <summary>store 模式下的自簽憑證（重啟沿用，不每次重新匯入）</summary>
    public string? SelfSignedThumbprint { get; init; }

    public long? SelfSignedNotAfter { get; init; }

    /// <summary>staging 成功的時間：外部確實連得到 443，之後才打正式目錄</summary>
    public long? StagingVerifiedAt { get; init; }

    public long? LastAttemptAt { get; init; }

    public bool? LastOk { get; init; }

    public string? LastMessage { get; init; }

    public int ConsecutiveFailures { get; init; }

    /// <summary>失敗退避到這個時間才再試</summary>
    public long? NextAttemptAt { get; init; }

    public long? AriWindowStart { get; init; }

    public long? AriWindowEnd { get; init; }

    public long? AriCheckedAt { get; init; }

    public long? AriRetryAfterMs { get; init; }

    public IReadOnlyList<RetiredCertificate> Retired { get; init; } = [];
}

internal enum AcmeAction
{
    Wait,
    IssueStaging,
    IssueProduction,
    Renew,
}

internal sealed record AcmePlan(AcmeAction Action, long NextCheckAt, string Reason);

internal enum ExpirySeverity
{
    None,
    Warning,
    Critical,
    Expired,
}

/// <summary>
/// 憑證取得與續期的純函式規則（9.6）：先 staging 再正式；ARI 視窗內挑一個固定時點續期（ARI 不可用時效期三分之二）；
/// 不寫死 55／60 天（2027 起 64 天、2028 起 45 天）；失敗以 1、3、6、12 小時退避（同網域每帳戶每小時最多 5 次驗證失敗）。
/// </summary>
internal static class AcmeRules
{
    public const long HourMs = 3_600_000;
    public const long DayMs = 86_400_000;

    /// <summary>舊憑證換下來 24 小時後才刪（可能還有交握在用）</summary>
    public const long RetireAfterMs = DayMs;

    public static long BackoffMs(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 1 => 1 * HourMs,
        2 => 3 * HourMs,
        3 => 6 * HourMs,
        _ => 12 * HourMs,
    };

    /// <summary>
    /// 續期時點：有 ARI 視窗就在視窗內依憑證指紋取一個固定比例的時點（同一張憑證每次算出來一樣、不同憑證分散開），
    /// 否則效期三分之二。
    /// </summary>
    public static long RenewAt(TlsState state)
    {
        if (state.AriWindowStart is { } start && state.AriWindowEnd is { } end && end > start)
            return start + (long)((end - start) * Fraction(state.CurrentThumbprint ?? string.Empty));
        if (state.NotBefore is { } nb && state.NotAfter is { } na && na > nb)
            return nb + (na - nb) * 2 / 3;
        return 0;
    }

    public static double Fraction(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return BitConverter.ToUInt32(hash, 0) / (double)uint.MaxValue;
    }

    /// <summary>
    /// 這一輪要做什麼。<paramref name="forceRenew"/>（<c>admin cert-renew --force</c>）：已有正式憑證時不看續期時點直接續期；
    /// 還沒有正式憑證時照常取得。手動觸發要清掉退避，由呼叫端先把 NextAttemptAt 清成 null。
    /// </summary>
    public static AcmePlan Plan(TlsState state, long now, bool skipStaging, long checkIntervalMs, bool forceRenew = false)
    {
        if (state.NextAttemptAt is { } next && now < next)
            return new AcmePlan(AcmeAction.Wait, next, "backoff");
        if (state.CurrentThumbprint is null)
        {
            if (!skipStaging && state.StagingVerifiedAt is null)
                return new AcmePlan(AcmeAction.IssueStaging, now, "first-staging");
            return new AcmePlan(AcmeAction.IssueProduction, now, "first-production");
        }
        if (forceRenew)
            return new AcmePlan(AcmeAction.Renew, now, "forced");
        var renewAt = RenewAt(state);
        if (now >= renewAt)
            return new AcmePlan(AcmeAction.Renew, now, "renewal-due");
        return new AcmePlan(AcmeAction.Wait, Math.Min(renewAt, now + checkIntervalMs), "valid");
    }

    /// <summary>
    /// ARI 該不該再查：從沒查過、或距上次超過 max(伺服器要求的間隔, 檢查週期)。
    /// <paramref name="manual"/>（<c>admin cert-renew</c>）一律重查，忽略 AriCheckedAt 與 Retry-After：
    /// CA 以 ARI 要求提前續期（例如撤銷事件）時，管理員不必等下一次排定的查詢。
    /// </summary>
    public static bool ShouldCheckAri(TlsState state, long now, long checkIntervalMs, bool manual = false)
    {
        if (state.CurrentThumbprint is null || state.CurrentAriId is null)
            return false;
        if (manual || state.AriCheckedAt is not { } checkedAt)
            return true;
        var interval = Math.Max(checkIntervalMs, Math.Clamp(state.AriRetryAfterMs ?? 0, HourMs, DayMs));
        return now - checkedAt >= interval;
    }

    public static TlsState Failed(TlsState state, long now, string message)
    {
        var failures = state.ConsecutiveFailures + 1;
        return state with
        {
            LastAttemptAt = now,
            LastOk = false,
            LastMessage = message,
            ConsecutiveFailures = failures,
            NextAttemptAt = now + BackoffMs(failures),
        };
    }

    /// <summary>剩不到 30 天警告、不到 14 天嚴重（推給所有裝置與網頁橫幅）</summary>
    public static ExpirySeverity Expiry(long notAfter, long now)
    {
        var left = notAfter - now;
        if (left <= 0)
            return ExpirySeverity.Expired;
        if (left < 14 * DayMs)
            return ExpirySeverity.Critical;
        return left < 30 * DayMs ? ExpirySeverity.Warning : ExpirySeverity.None;
    }

    /// <summary>自簽憑證剩不到 7 天就換一張新的</summary>
    public static bool SelfSignedNeedsRenewal(long? notAfter, long now) => notAfter is null || notAfter.Value - now < 7 * DayMs;

    /// <summary>換下來超過 24 小時、可以刪的舊憑證</summary>
    public static IReadOnlyList<RetiredCertificate> DueForRemoval(IEnumerable<RetiredCertificate> retired, long now) =>
        retired.Where(r => now - r.RetiredAt >= RetireAfterMs).ToList();
}
