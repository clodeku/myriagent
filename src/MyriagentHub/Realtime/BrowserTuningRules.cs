namespace MyriagentHub.Realtime;

/// <summary>
/// 只給開發與測試用的瀏覽器網頁調整（純函式）：正式站（Production、Console 以外的任何名稱都一樣）一律不送，網頁照內建的值
/// </summary>
internal static class BrowserTuningRules
{
    /// <summary>網頁內建的「重新連線中」寬限期（remoterules.js 的 RECONNECT_GRACE_MS）；設定只能縮短、不能加長</summary>
    public const int DefaultReconnectGraceMs = 60_000;

    public const int MinReconnectGraceMs = 1_000;

    /// <summary>
    /// init 的 <c>remote.reconnectGraceMs</c>（web-protocol 的 Remote）：環境是 Development 或 Testing、而且設定在 1000～60000 之間才回設定值，
    /// 其他回 null（不送，網頁照 60 秒）
    /// </summary>
    public static int? ReconnectGraceMs(string? environmentName, int? configured)
    {
        if (configured is not { } ms || ms < MinReconnectGraceMs || ms > DefaultReconnectGraceMs)
            return null;
        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
               || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase)
            ? ms
            : null;
    }
}
