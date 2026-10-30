namespace MyriagentHub.Realtime;

/// <summary>瀏覽器 hub 的方法名稱（設計 4.9.1；以名稱常數送，不用 Hub&lt;T&gt;，14.1；契約見 docs/web-protocol.md 第六節）</summary>
internal static class BrowserHubMethods
{
    public const string Cmd = "Cmd";

    public const string ClientState = "ClientState";

    /// <summary>伺服器→瀏覽器：參數就是 web-protocol 第一節的宿主訊息</summary>
    public const string Msg = "Msg";
}

/// <summary>瀏覽器 hub 的錯誤代碼（web-protocol 第六節第 6 點）</summary>
internal static class BrowserErrorCodes
{
    public const string Invalid = "invalid";

    public const string DesktopOnly = "desktop_only";

    public const string NotAvailable = "not_available";

    public const string Conflict = "conflict";

    public const string RateLimited = "rate_limited";

    public const string Unavailable = "unavailable";

    public const string TooManyConnections = "too_many_connections";
}

/// <summary>Cmd 的回傳（以 SyncJson 序列化：camelCase、null 省略）；<see cref="CommandId"/> 是請求帶來的原樣</summary>
internal sealed record CmdResult(bool Accepted, string? CommandId = null, string? Error = null);
