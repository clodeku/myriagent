using Myriagent.Sync;
using MyriagentHub.Sync;

namespace MyriagentHub.Media;

/// <summary>媒體請求從哪個端點進來（WP4.7 計畫 3.1 的 E1、E2、E3、E9）</summary>
internal enum MediaEndpointKind
{
    /// <summary>E1 <c>GET /m/{key}</c>：原檔（圖片、影片），擁有者 cookie</summary>
    Media,

    /// <summary>E2 <c>GET /t/{key}</c>：縮圖，擁有者 cookie</summary>
    Thumb,

    /// <summary>E3 <c>GET /f/{key}</c>：<c>fileFetch</c> 的預覽與下載，擁有者 cookie</summary>
    File,

    /// <summary>E9 <c>GET /api/v1/device/media/{key}?purpose=media</c>：桌面複本的代理，存取權杖</summary>
    ReplicaMedia,

    /// <summary>E9 <c>GET /api/v1/device/media/{key}?purpose=thumb</c>：桌面複本的縮圖代理，存取權杖</summary>
    ReplicaThumb,

    /// <summary>
    /// E11 <c>GET /api/v1/device/media/{key}?purpose=file</c>：桌面遠端操作的 <c>fileFetch</c>（sync-protocol 14.7；同 <c>/f/</c>，只收 <c>file</c> 的參照），存取權杖
    /// </summary>
    ReplicaFile,
}

/// <summary>
/// <c>media_refs.purpose</c> 的值（WP4.7 計畫 D3；sync.db 不升版，以文字區分來源）：項目的附件與代理產出媒體是 <c>item:{itemId}</c>，
/// 遠端檔案抽屜的媒體是 <c>list</c>（30 天），<c>fileFetch</c> 的檔案是 <c>file</c>（15 分鐘）
/// </summary>
internal static class MediaRefPurposes
{
    public const string ItemPrefix = "item:";

    public const string List = "list";

    public const string File = "file";

    /// <summary>電腦狀態面板的檔案傳輸（deviceFetch；2026-11-09）：沒有對話（conversation_id 是空字串），2 小時</summary>
    public const string Transfer = "xfer";

    /// <summary><c>item:</c> 後面接著項目 id（不是空的）</summary>
    public static bool IsItem(string? purpose) =>
        purpose is { Length: > 5 } && purpose.StartsWith(ItemPrefix, StringComparison.Ordinal);
}

/// <summary>媒體請求查不到的原因（只寫進伺服器的記錄，回應一律同一種 404，D7）</summary>
internal static class MediaLookupMisses
{
    public const string BadKey = "鍵不合格";

    public const string NoRow = "沒有這個參照（電腦還沒送、已刪除或索引還沒追上）";

    public const string Purpose = "用途和端點不符";

    public const string Expired = "已到期";

    public const string NoConversation = "對話不在目錄裡（已刪除或還沒發布）";

    public const string OtherOwner = "對話的擁有者不是鍵裡的電腦";

    public const string NotFull = "對話的同步政策不是 full";
}

/// <summary>
/// 媒體請求能不能看到這個參照（WP4.7 計畫 D7）：用途與端點相符、沒到期、對話還在而且擁有者是鍵裡的裝置、政策是 full。
/// 任何一項不符一律回同一種 404（不分原因）；降級（metadata、paused）與刪除在目錄提交之後的下一個請求就看不到（答 4）。純函式
/// </summary>
internal static class MediaLookupRules
{
    /// <summary>抽屜（<c>list</c>）的參照從建立起 30 天內有效（設計 4.10:1066）</summary>
    public static readonly TimeSpan ListLifetime = TimeSpan.FromDays(30);

    /// <summary><c>fileFetch</c>（<c>file</c>）的參照從建立起 15 分鐘內有效</summary>
    public static readonly TimeSpan FileLifetime = TimeSpan.FromMinutes(15);

    /// <summary>檔案傳輸（<c>xfer</c>）的參照從建立起 2 小時內有效（桌面的 E11 每一段都會查，大檔要撐得過整個下載）</summary>
    public static readonly TimeSpan XferLifetime = TimeSpan.FromHours(2);

    /// <summary>
    /// 端點與用途相符：<c>/f/</c> 只收 <c>file</c>；<c>/m/</c>、<c>/t/</c> 與桌面複本的代理只收項目（<c>item:*</c>）與抽屜（<c>list</c>）。
    /// 不認得的用途一律不收
    /// </summary>
    public static bool Allows(MediaEndpointKind endpoint, string? purpose) => endpoint switch
    {
        MediaEndpointKind.File or MediaEndpointKind.ReplicaFile => purpose is MediaRefPurposes.File or MediaRefPurposes.Transfer,
        MediaEndpointKind.Media or MediaEndpointKind.Thumb or MediaEndpointKind.ReplicaMedia or MediaEndpointKind.ReplicaThumb =>
            MediaRefPurposes.IsItem(purpose) || purpose == MediaRefPurposes.List,
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "不認得的媒體端點"),
    };

    /// <summary>
    /// 到期（<paramref name="nowMs"/> − <paramref name="createdAtMs"/> ≥ 有效期，伺服器時鐘）：<c>list</c> 30 天、<c>file</c> 15 分鐘；
    /// 項目的參照不會到期（跟著項目，由刪除與降級收回）。不認得的用途一律算到期
    /// </summary>
    public static bool Expired(string? purpose, long createdAtMs, long nowMs)
    {
        if (MediaRefPurposes.IsItem(purpose))
            return false;
        var lifetime = purpose switch
        {
            MediaRefPurposes.List => ListLifetime,
            MediaRefPurposes.File => FileLifetime,
            MediaRefPurposes.Transfer => XferLifetime,
            _ => TimeSpan.Zero,
        };
        return nowMs - createdAtMs >= (long)lifetime.TotalMilliseconds;
    }

    /// <summary>對話還在（沒刪除）、擁有者就是鍵裡的裝置（第 4 階段對話固定在建立它的電腦上）、政策是 full</summary>
    public static bool Visible(ConversationOwnership? owner, string keyDeviceId) =>
        owner is not null
        && string.Equals(owner.OwnerDeviceId, keyDeviceId, StringComparison.Ordinal)
        && owner.SyncPolicy == ConversationSyncPolicies.Full;

    /// <summary>
    /// 查到的列為什麼不能看（null＝可以看）：只給伺服器記錄（<see cref="MediaLookupMisses"/>），回應一律同一種 404。
    /// 順序同 <see cref="MediaLookup.FindAsync"/>：用途 → 到期 → 可見性
    /// </summary>
    public static string? Miss(MediaEndpointKind endpoint, string? purpose, long createdAtMs, long nowMs, ConversationOwnership? owner, string keyDeviceId)
    {
        if (!Allows(endpoint, purpose))
            return MediaLookupMisses.Purpose;
        if (Expired(purpose, createdAtMs, nowMs))
            return MediaLookupMisses.Expired;
        // 檔案傳輸沒有對話：鍵裡的裝置就是來源（電腦的閘門以這條連線的政策再判一次）
        if (purpose == MediaRefPurposes.Transfer)
            return null;
        if (owner is null)
            return MediaLookupMisses.NoConversation;
        if (!string.Equals(owner.OwnerDeviceId, keyDeviceId, StringComparison.Ordinal))
            return MediaLookupMisses.OtherOwner;
        return owner.SyncPolicy == ConversationSyncPolicies.Full ? null : MediaLookupMisses.NotFull;
    }

    /// <summary>向來源電腦要位元組時 <c>MediaRequest.purpose</c> 的值</summary>
    public static string RequestPurpose(MediaEndpointKind endpoint) => endpoint switch
    {
        MediaEndpointKind.Media or MediaEndpointKind.ReplicaMedia => MediaPurposes.Media,
        MediaEndpointKind.Thumb or MediaEndpointKind.ReplicaThumb => MediaPurposes.Thumb,
        MediaEndpointKind.File or MediaEndpointKind.ReplicaFile => MediaPurposes.File,
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "不認得的媒體端點"),
    };

    /// <summary>向來源電腦要位元組時的 <c>MediaRequest.purpose</c>（依參照的用途：檔案傳輸的參照是 <c>xfer</c>，其他照端點）</summary>
    public static string RequestPurpose(MediaEndpointKind endpoint, string? refPurpose)
        => refPurpose == MediaRefPurposes.Transfer && endpoint is MediaEndpointKind.File or MediaEndpointKind.ReplicaFile ? MediaPurposes.Transfer : RequestPurpose(endpoint);
}
