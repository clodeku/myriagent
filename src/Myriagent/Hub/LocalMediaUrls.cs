using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Rendering;

namespace Myriagent.Hub;

/// <summary>
/// 桌面的媒體網址與 /media、/thumb 白名單（共用渲染器 IMediaUrlBuilder 的桌面實作，設計 2.5）。
/// 網址照舊是 https://myriagent.local/media?path=…、/thumb?path=…&amp;size=…、/open?path=…；產生媒體與縮圖網址的同時登記白名單，
/// AgentWebView 的 /media、/thumb 只提供登記過的本機檔（UNC 一律不認：檢查存在就會對外送 SMB 雜湊）。
/// 回覆 Markdown 裡的本機圖片只在那筆項目轉換成功後、而且是可直接顯示的圖片與影片才登記；本機檔案連結另外登記「另存新檔」的白名單。
/// 中樞的項目渲染、輸入框的附件與檔案瀏覽器共用同一份白名單。只在 UI 執行緒使用（WebResourceRequested 也在 UI 執行緒觸發），不加鎖
/// </summary>
internal sealed class LocalMediaUrls : IMediaUrlBuilder
{
    /// <summary>白名單各最多保留幾筆：列一次幾千張圖的資料夾就登記幾千筆，常駐系統匣好幾週也不會無限累積（最久沒用到的先移除，畫面重繪時會再登記）</summary>
    internal const int MaxAllowedPaths = 20_000;

    private readonly RecentSet allowedMedia = new(MaxAllowedPaths, StringComparer.OrdinalIgnoreCase);
    private readonly RecentSet allowedThumbs = new(MaxAllowedPaths, StringComparer.OrdinalIgnoreCase);
    private readonly RecentSet allowedLinks = new(MaxAllowedPaths, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 媒體端點（/media）只提供畫面上真的出現過的本機檔案：附件、影片畫面、代理產出的媒體、回覆中引用的本機圖片、檔案瀏覽器列出的媒體
    /// </summary>
    public bool IsMediaAllowed(string path) => NormalizePath(path) is { } p && allowedMedia.Contains(p);

    /// <summary>縮圖端點（/thumb）只提供登記過的檔案（附件與檔案瀏覽器列出的圖片／影片）</summary>
    public bool IsThumbAllowed(string path) => NormalizePath(path) is { } p && (allowedThumbs.Contains(p) || allowedMedia.Contains(p));

    /// <summary>「另存新檔」（saveFile）只複製畫面上出現過的檔案：回覆中的本機檔案連結與媒體端點登記過的檔案</summary>
    public bool IsSaveAllowed(string path) => NormalizePath(path) is { } p && (allowedLinks.Contains(p) || allowedMedia.Contains(p));

    /// <summary>登記與查詢一律用正規化後的完整路徑（正斜線、.. 等寫法視為同一個檔案；Windows 外殼也只認反斜線）</summary>
    internal static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !AgentAttachmentRules.IsLocalDrivePath(path))
            return null;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public string MediaUrl(string path)
    {
        var normalized = NormalizePath(path) ?? path;
        allowedMedia.Add(normalized);
        return AgentMarkdownRenderer.MediaUrl(normalized);
    }

    /// <param name="version">檔案版本（會被覆寫的檔案才需要，見 AgentMarkdownRenderer.ThumbUrl）</param>
    public string ThumbUrl(string path, int size, string? version = null)
    {
        var normalized = NormalizePath(path) ?? path;
        allowedThumbs.Add(normalized);
        return AgentMarkdownRenderer.ThumbUrl(normalized, size, version);
    }

    /// <summary>Markdown 裡的本機圖片：網址用 Markdown 解析出的路徑（不正規化），登記另由 AllowMarkdownImage 在轉換成功後做</summary>
    public string MarkdownImageUrl(string path) => MarkdownImageUrlOf(path);

    /// <summary>Markdown 裡的本機檔案連結：網址用解析出的路徑（不正規化），同時登記「另存新檔」的白名單（不放行 /media、/thumb）</summary>
    public string MarkdownLinkUrl(string path)
    {
        AllowMarkdownLink(path);
        return MarkdownLinkUrlOf(path);
    }

    /// <summary>回覆中的本機檔案連結登記「另存新檔」的白名單（背景轉好的 Markdown 回到 UI 執行緒時補登記，見 <see cref="Deferred"/>）</summary>
    public void AllowMarkdownLink(string path)
    {
        if (NormalizePath(path) is { } normalized)
            allowedLinks.Add(normalized);
    }

    /// <summary>Markdown 圖片與檔案連結的網址：純函式，UI 執行緒與背景執行緒（<see cref="Deferred"/>）共用同一個算法</summary>
    private static string MarkdownImageUrlOf(string path) => AgentMarkdownRenderer.MediaUrl(path);

    private static string MarkdownLinkUrlOf(string path) => AgentMarkdownRenderer.OpenUrl(path);

    /// <summary>
    /// 背景執行緒轉 Markdown 用的網址（BackgroundMarkdown）：網址和 <see cref="LocalMediaUrls"/> 一模一樣，但不碰白名單（白名單只在 UI 執行緒），
    /// 只記下出現過的本機檔案連結；回到 UI 執行緒後由呼叫端以 <see cref="AllowMarkdownLink"/> 補登記。AgentMarkdownRenderer.ToHtml 只用到
    /// Markdown 圖片與連結的網址，其他方法不該被叫到。一次轉換一個實例
    /// </summary>
    internal sealed class Deferred : IMediaUrlBuilder
    {
        /// <summary>這次轉換出現過的本機檔案連結（依出現順序）</summary>
        public List<string> Links { get; } = new();

        public string MarkdownImageUrl(string path) => MarkdownImageUrlOf(path);

        public string MarkdownLinkUrl(string path)
        {
            Links.Add(path);
            return MarkdownLinkUrlOf(path);
        }

        public string MediaUrl(string path) => throw new InvalidOperationException("背景轉換只產生 Markdown 圖片與連結的網址");

        public string ThumbUrl(string path, int size, string? version = null) => throw new InvalidOperationException("背景轉換只產生 Markdown 圖片與連結的網址");

        public void AllowMarkdownImage(string path) => throw new InvalidOperationException("背景轉換不登記白名單");
    }

    /// <summary>回覆中引用的本機圖片：只登記畫面能顯示的圖片與影片（其他副檔名登記了也只會讓 /thumb 替任意檔案產生外殼縮圖）</summary>
    public void AllowMarkdownImage(string path)
    {
        if (NormalizePath(path) is { } normalized && AgentAttachmentRules.IsDisplayableMedia(normalized))
            allowedMedia.Add(normalized);
    }

    /// <summary>檔案已不在畫面上（例如移除附件）：撤銷媒體與縮圖的登記</summary>
    public void Revoke(string path)
    {
        var normalized = NormalizePath(path) ?? path;
        allowedMedia.Remove(normalized);
        allowedThumbs.Remove(normalized);
    }
}
