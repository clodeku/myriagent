using System.Collections.Concurrent;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Fakes;

// WP4.7（媒體）的假伺服器行為：RefreshAccessToken 換新的 cha_（第 n 次是 cha_test-{n}）
internal sealed partial class FakeSyncServer
{
    private int accessTokenRefreshes;

    /// <summary>RefreshAccessToken 被呼叫的次數（全部連線）</summary>
    public int AccessTokenRefreshes => Volatile.Read(ref accessTokenRefreshes);

    /// <summary>RefreshAccessToken 回的權杖（依序）</summary>
    public ConcurrentQueue<string> IssuedAccessTokens { get; } = new();

    /// <summary>Prove 與 RefreshAccessToken 的 expiresAt 相對測試時鐘的位移（測試「伺服器時鐘比本機早」時用負值）</summary>
    public long AccessTokenExpiryOffsetMs { get; set; }

    /// <summary>RefreshAccessToken 要丟的例外（null＝照常發）</summary>
    public Func<int, Exception?>? AccessTokenFailure { get; set; }

    private AccessTokenResponse RefreshAccessToken()
    {
        lock (gate)
        {
            var n = ++accessTokenRefreshes;
            if (AccessTokenFailure?.Invoke(n) is { } failure)
                throw failure;
            var token = "cha_test-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            IssuedAccessTokens.Enqueue(token);
            var expires = time.GetUtcNow().ToUnixTimeMilliseconds() + 900_000 + AccessTokenExpiryOffsetMs;
            return new AccessTokenResponse(token, expires);
        }
    }

    internal sealed partial class FakeSession
    {
        public Task<AccessTokenResponse> RefreshAccessTokenAsync(CancellationToken ct) => Call(nameof(DeviceHubMethods.RefreshAccessToken), ct, () =>
        {
            RequireProven();
            return server.RefreshAccessToken();
        });
    }
}
