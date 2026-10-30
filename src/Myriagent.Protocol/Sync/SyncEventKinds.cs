namespace Myriagent.Sync;

/// <summary>事件的類別（拉取用的虛擬串流，events.cls）</summary>
internal static class SyncClasses
{
    /// <summary>所有對話的輕量事件（第 4 階段）</summary>
    public const string Meta = "meta";

    /// <summary>某個對話的項目事件（第 4 階段，依 stream 拉）</summary>
    public const string Item = "item";

    /// <summary>帳戶目錄、裝置、外觀偏好</summary>
    public const string Catalog = "catalog";

    /// <summary>共享用量</summary>
    public const string Usage = "usage";

    public static bool IsKnown(string? cls) => cls is Meta or Item or Catalog or Usage;
}

/// <summary>事件的作者（events.author）：裝置寫的是它的 deviceId（<c>d_</c> 開頭），伺服器寫的是 <see cref="Server"/></summary>
internal static class SyncAuthors
{
    /// <summary>伺服器寫的事件（帳戶目錄的結果、裝置、共享用量，第 5 階段的 <c>conv.lease</c>）的作者</summary>
    public const string Server = "server";
}

/// <summary>事件的串流（events.stream）</summary>
internal static class SyncStreams
{
    public const string Catalog = "catalog";

    public const string Usage = "usage";

    public static string Conversation(string conversationId) => "c:" + conversationId;
}

/// <summary>
/// 第 3 階段的事件種類（設計 4.4，docs/sync-protocol.md）。分兩種：
/// <list type="bullet">
/// <item><b>裝置寫的</b>（經 outbox 發佈，是給伺服器的輸入）：本機帳戶的狀態摘要、目錄與偏好的修改要求（樂觀並行，帶 baseVersion）、裝置改名、CLI 狀態、app 的更新狀態。</item>
/// <item><b>伺服器寫的</b>（伺服器套用輸入後發布的結果）：邏輯帳戶整筆（含各電腦矩陣）、裝置、偏好、共享用量。</item>
/// </list>
/// 拉取端（裝置、瀏覽器閘道）只套用伺服器寫的事件；裝置寫的事件也會被拉到（游標照樣前進），一律略過。不認得的種類同樣略過（新版伺服器多出的種類）。
/// </summary>
internal static class SyncEventKinds
{
    // 裝置寫的（輸入）
    public const string LinkUpsert = "link.upsert";
    public const string LinkRemoved = "link.removed";
    public const string AccountUpdate = "account.update";
    public const string PrefsUpdate = "prefs.update";
    public const string DeviceUpdate = "device.update";
    public const string CliState = "cli.state";

    /// <summary>這台 app 的更新狀態（app 自我更新規格 7.8，合併鍵 <c>app</c>）：伺服器從 U2 起收，桌面從 U3 起送</summary>
    public const string AppState = "app.state";

    // 伺服器寫的（結果）
    public const string AccountUpsert = "account.upsert";
    public const string AccountDeleted = "account.deleted";
    public const string DeviceUpsert = "device.upsert";
    public const string DeviceRevoked = "device.revoked";
    public const string PrefsSet = "prefs.set";
    public const string UsageSnapshot = "usage.snapshot";

    /// <summary>裝置可以經 outbox 發佈的種類（第 3 階段；app.state 是 app 自我更新 U2 加的；其他種類伺服器回 invalid）</summary>
    public static bool IsDeviceAuthored(string? kind) => kind is LinkUpsert or LinkRemoved or AccountUpdate or PrefsUpdate or DeviceUpdate or CliState or AppState;

    public static bool IsServerAuthored(string? kind) => kind is AccountUpsert or AccountDeleted or DeviceUpsert or DeviceRevoked or PrefsSet or UsageSnapshot;

    /// <summary>種類所屬的類別與串流（伺服器寫入 events 時依此填 cls、stream；裝置送的 stream 必須相同）</summary>
    public static (string Cls, string Stream)? Placement(string? kind) => kind switch
    {
        LinkUpsert or LinkRemoved or AccountUpdate or PrefsUpdate or DeviceUpdate or CliState or AppState
            or AccountUpsert or AccountDeleted or DeviceUpsert or DeviceRevoked or PrefsSet => (SyncClasses.Catalog, SyncStreams.Catalog),
        UsageSnapshot => (SyncClasses.Usage, SyncStreams.Usage),
        _ => null,
    };
}
