using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Sync;

namespace MyriagentHub.Auth;

/// <summary>
/// 裝置的存取權杖（cha_，sync-protocol 3）：Prove 成功後發、15 分鐘、綁 deviceId，只給 <c>/api/v1/device/*</c>。
/// 只存在記憶體（只存 SHA-256）：服務重啟後全部失效——裝置的 hub 連線也一起斷了，重連 Prove 時拿新的。
/// </summary>
internal sealed class AccessTokenStore(TimeProvider time)
{
    public const string PathPrefix = "/api/v1/device/";

    private readonly ConcurrentDictionary<string, (string DeviceId, long ExpiresAt, long Serial)> tokens = new(StringComparer.Ordinal);

    /// <summary>發放序號：同一毫秒發的權杖到期時間相同，新舊以它分</summary>
    private long serial;

    public static bool IsWellFormed(string? token) =>
        token is not null
        && token.Length == SyncProtocol.AccessTokenPrefix.Length + DeviceTokenRules.RandomChars
        && token.StartsWith(SyncProtocol.AccessTokenPrefix, StringComparison.Ordinal)
        && token.AsSpan(SyncProtocol.AccessTokenPrefix.Length).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0;

    /// <summary>每台同時有效的存取權杖上限：換新時保留上一個（換新前發出的請求還用它），更早的作廢</summary>
    public const int MaxPerDevice = 2;

    public AccessTokenResponse Issue(string deviceId)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var mine = new List<(string Key, long Serial)>();
        foreach (var (key, entry) in tokens)
        {
            if (entry.ExpiresAt <= now)
                tokens.TryRemove(key, out _);
            else if (entry.DeviceId == deviceId)
                mine.Add((key, entry.Serial));
        }
        // RefreshAccessToken 沒有次數限制：每台只留最新的一個（加上這次發的），表的大小跟裝置數成正比
        foreach (var (key, _) in mine.OrderByDescending(m => m.Serial).Skip(MaxPerDevice - 1))
            tokens.TryRemove(key, out _);
        var token = SyncProtocol.AccessTokenPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var expiresAt = now + SyncProtocol.AccessTokenMinutes * 60_000L;
        tokens[Key(token)] = (deviceId, expiresAt, Interlocked.Increment(ref serial));
        return new AccessTokenResponse(token, expiresAt);
    }

    /// <summary>有效的權杖回傳它綁的裝置；過期、不認得回 null</summary>
    public string? Validate(string token)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        return tokens.TryGetValue(Key(token), out var entry) && now < entry.ExpiresAt ? entry.DeviceId : null;
    }

    /// <summary>撤銷裝置時一併作廢它的存取權杖</summary>
    public void RevokeDevice(string deviceId)
    {
        foreach (var (key, entry) in tokens)
        {
            if (entry.DeviceId == deviceId)
                tokens.TryRemove(key, out _);
        }
    }

    private static string Key(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
