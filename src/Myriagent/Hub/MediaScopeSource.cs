namespace Myriagent.Hub;

/// <summary>
/// 媒體轉送要問中樞的事（WP4.7 計畫 D26）：<paramref name="ConversationId"/> 是 <c>media_refs_local</c> 的對話 id（線上格式），
/// <paramref name="ItemId"/> 是項目 id 或 <c>@list</c>／<c>@file</c>；<paramref name="AttachmentId"/>、<paramref name="MediaPath"/> 照列上的值（項目參照恰有一個）。
/// <paramref name="DeviceId"/> 是這台的裝置 id（對話的擁有者要是它或還沒記）
/// </summary>
internal sealed record MediaScopeQuery(string ConversationId, string ItemId, string? AttachmentId, string? MediaPath, string DeviceId);

/// <summary>算禁止清單用的本機資料夾（原樣；引擎在背景以 RemoteFileGuard.NormalizeRootAsync 展開，正規化不了的原樣保留，規則會 fail-closed）</summary>
/// <param name="CodexHome">繼承的 CODEX_HOME（系統預設的 Codex 帳戶用的；沒設是 null）：和帳戶設定資料夾一樣進禁止清單，也是 Codex 產生圖片的根目錄之一（WP4.7 Q2-A）</param>
internal sealed record MediaFolderRoots(string UserProfile, string LocalAppData, string? RoamingAppData, string AppDataRoot, IReadOnlyList<string> AccountConfigDirectories,
    string? CodexHome = null);

/// <summary>
/// 中樞在 UI 執行緒取的快照（之後在背景用，不再碰中樞）。<see cref="Visible"/> 是 false 時其他欄位都不用（回 404）。
/// <see cref="ItemsLoaded"/> 是 true 時 <see cref="Resolved"/> 是項目參照對到的檔（null＝項目或附件已經不在）；沒載入時由引擎在背景唯讀讀 items.json
/// </summary>
/// <param name="ConversationId">本機的對話 id（資料夾名）</param>
/// <param name="WorkingDirectory">目前的工作資料夾（沒設定時是預設的 workspace）</param>
/// <param name="MediaSequence">項目已載入、而且是代理媒體的參照時，那個路徑在項目 MediaPaths 裡的序號（從 1 起；Codex 產生圖片的複本檔名用，WP4.7 Q2-A）</param>
/// <param name="KeptPath">項目已載入、而且是代理媒體的參照時，這台替它留的副本（<see cref="MediaKeepRules"/>；只算路徑、不看在不在，引擎在背景看）</param>
internal sealed record MediaScopeSnapshot(
    string ConversationId,
    bool Visible,
    string ConversationDirectory,
    string AttachmentDirectory,
    string DefaultWorkingDirectory,
    string WorkingDirectory,
    bool ItemsLoaded,
    (string Path, string FileName)? Resolved,
    MediaFolderRoots Folders,
    int? MediaSequence = null,
    string? KeptPath = null)
{
    /// <summary>對話在但看不到（草稿、複本、別台擁有、政策不是 full）：其他欄位不用</summary>
    public static MediaScopeSnapshot Hidden(string conversationId) =>
        new(conversationId, false, string.Empty, string.Empty, string.Empty, string.Empty, false, null, new MediaFolderRoots(string.Empty, string.Empty, null, string.Empty, []));
}

/// <summary>中樞提供給媒體轉送的對話快照（中樞實作；只在 UI 執行緒呼叫）</summary>
internal interface IMediaScopeSource
{
    /// <summary>對話不在回 null；在但看不到回 <see cref="MediaScopeSnapshot.Hidden"/></summary>
    MediaScopeSnapshot? GetMediaScope(MediaScopeQuery query);
}
