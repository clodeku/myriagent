namespace Myriagent.Sync;

/// <summary>
/// 存取權杖 <c>cha_</c>（媒體 HTTP 端點用，Prove 與 RefreshAccessToken 發，15 分鐘）的換新時機與格式（WP4.7 計畫 D24）。
/// 到期一律以收到權杖之後經過的時間（單調時鐘）判斷；伺服器回的 <c>expiresAt</c> 只記錄、不和本機時鐘比（不比較各電腦的時鐘）。純函式
/// </summary>
internal static class AccessTokenRules
{
    /// <summary>權杖的長度上限（只放進 Authorization 標頭；伺服器發的是 cha_ 加 26 字）</summary>
    public const int MaxTokenLength = 256;

    /// <summary>收到權杖之後經過多久就換：<see cref="SyncProtocol.AccessTokenMinutes"/> − <see cref="SyncProtocol.AccessTokenRefreshBeforeMinutes"/>（13 分鐘）</summary>
    public static TimeSpan RefreshAfter { get; } =
        TimeSpan.FromMinutes(SyncProtocol.AccessTokenMinutes - SyncProtocol.AccessTokenRefreshBeforeMinutes);

    /// <summary>收到之後經過 <paramref name="elapsedSinceIssued"/>（單調時鐘）：到了 <see cref="RefreshAfter"/> 就要換</summary>
    public static bool NeedsRefresh(TimeSpan elapsedSinceIssued) => elapsedSinceIssued >= RefreshAfter;

    /// <summary>可以放進 <c>Authorization: Bearer</c> 的權杖：<c>cha_</c> 開頭、≤256 字、只有可印的 ASCII（沒有空白與控制字元）</summary>
    public static bool IsUsable(string? token)
    {
        if (token is null || token.Length <= SyncProtocol.AccessTokenPrefix.Length || token.Length > MaxTokenLength
            || !token.StartsWith(SyncProtocol.AccessTokenPrefix, StringComparison.Ordinal))
            return false;
        foreach (var c in token)
        {
            if (c is < (char)0x21 or > (char)0x7E)
                return false;
        }

        return true;
    }
}
