using System.Net;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>測試的 cookie 容器不照 cookie 上的到期時間丟 cookie（假時鐘的起點固定，真實日期一過「起點＋7 天」就全部 401）</summary>
public sealed class CookieHandlerTests
{
    [Fact]
    public void 有值的cookie去掉expires與max_age_其他屬性保留()
    {
        var header = "__Host-myriagenthub=abc; expires=Sun, 18 Oct 2026 09:00:00 GMT; Max-Age=604800; path=/; secure; samesite=strict; httponly";

        var cleaned = CookieHandler.WithoutExpiry(header);

        cleaned.Should().Be("__Host-myriagenthub=abc; path=/; secure; samesite=strict; httponly");
    }

    [Fact]
    public void 刪除用的空值cookie照原樣_容器照舊刪掉()
    {
        var delete = "__Host-myriagenthub=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/; secure; samesite=strict; httponly";
        CookieHandler.WithoutExpiry(delete).Should().Be(delete);

        var uri = new Uri("https://localhost/");
        var container = new CookieContainer();
        container.SetCookies(uri, CookieHandler.WithoutExpiry("__Host-myriagenthub=abc; expires=Sun, 18 Oct 2026 09:00:00 GMT; path=/; secure"));
        container.GetCookieHeader(uri).Should().Be("__Host-myriagenthub=abc", "真實日期早已過了 cookie 上寫的到期時間，去掉之後照樣送");
        container.SetCookies(uri, CookieHandler.WithoutExpiry(delete));
        container.GetCookieHeader(uri).Should().BeEmpty();
    }
}
