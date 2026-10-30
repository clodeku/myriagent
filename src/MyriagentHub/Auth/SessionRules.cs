using System.Buffers.Text;
using System.Security.Cryptography;
using MyriagentHub.Hosting;

namespace MyriagentHub.Auth;

/// <summary>工作階段的時間欄位（毫秒）</summary>
internal readonly record struct SessionTimes(long CreatedAt, long LastSeenAt, long IdleExpiresAt, long AbsoluteExpiresAt, long LastUvAt, long? RevokedAt);

/// <summary>伺服器端瀏覽器工作階段的純函式規則（8.2.2、SEC-04）</summary>
internal static class SessionRules
{
    /// <summary>最後使用時間每分鐘最多寫一次資料庫（閒置期限也隨之延長）</summary>
    public const long TouchIntervalMs = 60_000;

    public static long IdleMs(SessionSettings s) => s.IdleHours * 3_600_000L;

    public static long AbsoluteMs(SessionSettings s) => s.AbsoluteDays * 86_400_000L;

    public static SessionTimes New(long now, bool userVerified, SessionSettings s) =>
        new(now, now, now + IdleMs(s), now + AbsoluteMs(s), userVerified ? now : 0, null);

    /// <summary>協作人的閒置逾時（多人協作；預設 30 天）</summary>
    public static long CollaboratorIdleMs(SessionSettings s) => s.CollaboratorIdleDays * 86_400_000L;

    /// <summary>協作人的絕對期限（預設 180 天）</summary>
    public static long CollaboratorAbsoluteMs(SessionSettings s) => s.CollaboratorAbsoluteDays * 86_400_000L;

    /// <summary>協作人登入的工作階段（登入就是輸入過密碼）</summary>
    public static SessionTimes NewCollaborator(long now, SessionSettings s) =>
        new(now, now, now + CollaboratorIdleMs(s), now + CollaboratorAbsoluteMs(s), now, null);

    /// <summary>這個工作階段的閒置逾時：協作人的（<paramref name="collaborator"/>）或擁有者的</summary>
    public static long IdleMsFor(bool collaborator, SessionSettings s) => collaborator ? CollaboratorIdleMs(s) : IdleMs(s);

    public static bool IsValid(SessionTimes t, long now) =>
        t.RevokedAt is null && now < t.IdleExpiresAt && now < t.AbsoluteExpiresAt;

    public static bool ShouldTouch(SessionTimes t, long now) => now - t.LastSeenAt >= TouchIntervalMs;

    /// <summary>閒置期限滑動延長，但不超過絕對期限</summary>
    public static SessionTimes Touch(SessionTimes t, long now, SessionSettings s) => Touch(t, now, IdleMs(s));

    /// <summary>同上，閒置逾時另外給（協作人的工作階段是 <see cref="CollaboratorIdleMs"/>）</summary>
    public static SessionTimes Touch(SessionTimes t, long now, long idleMs) =>
        t with { LastSeenAt = now, IdleExpiresAt = Math.Min(now + idleMs, t.AbsoluteExpiresAt) };

    /// <summary>近期使用者驗證：最近 N 分鐘內輸入過密碼（登入或再驗證；時鐘倒退時不算數）</summary>
    public static bool HasRecentAuth(long lastUvAt, long now, int minutes) =>
        lastUvAt > 0 && lastUvAt <= now && now - lastUvAt <= minutes * 60_000L;

    public static long RecentAuthUntil(long lastUvAt, int minutes) => lastUvAt <= 0 ? 0 : lastUvAt + minutes * 60_000L;

    /// <summary>cookie 裡的 key：256 位元亂數的 base64url</summary>
    public static string NewKey() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] HashKey(string key) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));

    /// <summary>對外的工作階段 id（列表、撤銷、稽核用）：key 雜湊的 base64url，拿到也推不回 key</summary>
    public static string SessionId(byte[] keyHash) => Base64Url.EncodeToString(keyHash);

    public static byte[]? ParseSessionId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64)
            return null;
        try
        {
            var bytes = Base64Url.DecodeFromChars(id);
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>稽核與記錄用的短 id（前 12 碼）</summary>
    public static string ShortId(string sessionId) => sessionId.Length > 12 ? sessionId[..12] : sessionId;
}
