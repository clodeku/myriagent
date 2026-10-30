using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Rendering;
using Myriagent.Sync;

namespace Myriagent.Hub;

/// <summary>
/// 複本（其他電腦的對話）的媒體網址（WP4.7 計畫步 12，D38；共用渲染器 <see cref="IMediaUrlBuilder"/> 的複本實作）：
/// 只認複本路徑（<see cref="ReplicaMediaRules.PathFor"/>），原檔是 <c>https://myriagent.local/m/{鍵}</c>、縮圖是 <c>…/t/{鍵}?s=160|320</c>，
/// 產生網址的同時登記鍵（AgentWebView 的 <c>/m/</c>、<c>/t/</c> 只轉送登記過的鍵，同 <see cref="LocalMediaUrls"/> 的白名單）。
/// 其他路徑（擁有者電腦上的本機路徑、Markdown 裡的圖片與連結）一律沒有網址（同 <see cref="NullMediaUrlBuilder"/>）。
/// 只在 UI 執行緒使用（WebResourceRequested 也在 UI 執行緒觸發），不加鎖
/// </summary>
internal sealed class ReplicaMediaUrls : IMediaUrlBuilder
{
    /// <summary>登記各最多保留幾個鍵（最久沒用到的先移除，畫面重繪時會再登記）</summary>
    internal const int MaxAllowedKeys = 20_000;

    public const string MediaPrefix = AgentMarkdownRenderer.Origin + "/m/";

    public const string ThumbPrefix = AgentMarkdownRenderer.Origin + "/t/";

    private RecentSet allowedMedia = new(MaxAllowedKeys, StringComparer.Ordinal);
    private RecentSet allowedThumbs = new(MaxAllowedKeys, StringComparer.Ordinal);
    private RecentSet allowedFiles = new(MaxAllowedKeys, StringComparer.Ordinal);

    /// <summary>原檔：複本路徑而且是可直接顯示的圖片或影片才有網址</summary>
    public string MediaUrl(string path)
    {
        if (!ReplicaMediaRules.TryKey(path, out var key) || !AgentAttachmentRules.IsDisplayableMedia(path))
            return null!;
        allowedMedia.Add(key);
        return MediaPrefix + key;
    }

    /// <summary>縮圖：複本路徑才有網址（附件的圖片與影片，縮圖由擁有者電腦產生）</summary>
    public string ThumbUrl(string path, int size, string? version = null)
    {
        if (!ReplicaMediaRules.TryKey(path, out var key))
            return null!;
        allowedThumbs.Add(key);
        return ThumbPrefix + key + "?s=" + ReplicaMediaRules.ThumbSize(size).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public string MarkdownImageUrl(string path) => "#";

    public string MarkdownLinkUrl(string path) => "#";

    public void AllowMarkdownImage(string path)
    {
    }

    /// <summary>這個鍵登記過這個用途（<c>media</c>：原檔的鍵；<c>thumb</c>：縮圖或原檔的鍵；<c>file</c>：桌面遠端操作的 fileRef）</summary>
    public bool IsAllowed(string key, string purpose) => purpose switch
    {
        MediaPurposes.Media => allowedMedia.Contains(key),
        MediaPurposes.Thumb => allowedThumbs.Contains(key) || allowedMedia.Contains(key),
        MediaPurposes.File => allowedFiles.Contains(key),
        _ => false,
    };

    /// <summary>
    /// 桌面遠端操作（sync-protocol 14.7）：伺服器轉來的 <c>files</c>、<c>fileRef</c> 帶的本站相對網址（<c>/m/{鍵}</c>、<c>/t/{鍵}?s=…</c>、<c>/f/{鍵}</c>）
    /// 登記成可以轉送的鍵（同產生網址時的登記）；不是這三種前綴或鍵不合格的略過
    /// </summary>
    public void AllowRelativeUrl(string? url)
    {
        if (ReplicaMediaRules.KeyOfRelativeUrl(url) is not { } parsed)
            return;
        switch (parsed.Purpose)
        {
            case MediaPurposes.Media:
                allowedMedia.Add(parsed.Key);
                break;
            case MediaPurposes.Thumb:
                allowedThumbs.Add(parsed.Key);
                break;
            case MediaPurposes.File:
                allowedFiles.Add(parsed.Key);
                break;
        }
    }

    /// <summary>複本都移除了（關掉顯示、換了接收者）：登記全部丟掉</summary>
    public void Clear()
    {
        allowedMedia = new RecentSet(MaxAllowedKeys, StringComparer.Ordinal);
        allowedThumbs = new RecentSet(MaxAllowedKeys, StringComparer.Ordinal);
        allowedFiles = new RecentSet(MaxAllowedKeys, StringComparer.Ordinal);
    }
}
