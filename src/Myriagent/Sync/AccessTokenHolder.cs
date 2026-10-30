namespace Myriagent.Sync;

/// <summary>
/// 目前這條連線的 <c>cha_</c>（WP4.7 計畫 D24）：引擎執行緒寫（Prove、換新、連線丟掉），HTTP 用戶端在任何執行緒讀快照。
/// <c>IssuedTimestamp</c> 是收到時的單調時鐘（<see cref="TimeProvider.GetTimestamp"/>），到期由 <see cref="AccessTokenRules.NeedsRefresh"/> 判斷
/// </summary>
internal sealed class AccessTokenHolder
{
    private Entry? current;

    private sealed record Entry(string Token, long IssuedTimestamp);

    /// <summary>目前的權杖（沒有是 null）；整組一次換掉，讀到的權杖與時間一定是同一組</summary>
    public (string Token, long IssuedTimestamp)? Current => Volatile.Read(ref current) is { } entry ? (entry.Token, entry.IssuedTimestamp) : null;

    public void Set(string token, long issuedTimestamp)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        Volatile.Write(ref current, new Entry(token, issuedTimestamp));
    }

    public void Clear() => Volatile.Write(ref current, null);
}
