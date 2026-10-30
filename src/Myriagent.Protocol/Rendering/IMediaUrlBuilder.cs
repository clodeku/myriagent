namespace Myriagent.Rendering;

/// <summary>
/// 渲染器（ItemRenderer 與它呼叫的 AgentMarkdownRenderer）放進畫面的本機檔案網址（設計 docs/plans/2026-10-10-multi-device-sync.md 2.5、4.9.2）：
/// 渲染器只決定「哪個檔案、拿來做什麼」，網址長什麼樣、要不要登記白名單由實作決定。
/// 桌面＝LocalMediaUrls（https://myriagent.local/media?path=…，同時登記 /media、/thumb 白名單）；
/// 伺服器＝HubMediaUrls（/m/{ref}、/t/{ref}、/f/{ref}，同時寫入 media_refs）；別台對話的唯讀複本＝ReplicaMediaUrls。
/// 渲染器依項目內容的順序呼叫（桌面白名單有上限、最久沒用到的先移除，順序決定誰先被擠掉）
/// </summary>
internal interface IMediaUrlBuilder
{
    /// <summary>直接顯示或播放的本機媒體：可直接顯示的附件原檔、代理產出的圖片與影片</summary>
    string MediaUrl(string path);

    /// <summary>縮圖：附件（圖片本身、影片的第一張畫面）、檔案瀏覽器列出的圖片與影片</summary>
    /// <param name="version">檔案版本（會被覆寫的檔案才需要：檔案一變網址就變，不會沿用快取裡的舊縮圖）</param>
    string ThumbUrl(string path, int size, string? version = null);

    /// <summary>回覆 Markdown 引用的本機圖片，寫進 &lt;img src&gt;。只產生網址；那筆項目轉換成功後再以 AllowMarkdownImage 通知</summary>
    string MarkdownImageUrl(string path);

    /// <summary>回覆 Markdown 的本機檔案連結，寫進 &lt;a href&gt;（點了由宿主決定怎麼開）</summary>
    string MarkdownLinkUrl(string path);

    /// <summary>
    /// 一筆項目的 Markdown 都轉換成功後，依出現順序（含重複）通知其中的本機圖片；轉換失敗改用純文字時不通知（畫面上沒有這些圖）。
    /// 實作只能放行可直接顯示的圖片與影片（AgentAttachmentRules.IsDisplayableMedia）：其他副檔名放行了，也只會讓縮圖端點替任意檔案產生外殼縮圖
    /// </summary>
    void AllowMarkdownImage(string path);
}
