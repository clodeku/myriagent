namespace Myriagent.UI;

/// <summary>嵌入介面網頁送出前的小調整</summary>
internal static class WebAssetRules
{
    private const string DarkTheme = "data-theme=\"dark\"";
    private const string LightTheme = "data-theme=\"light\"";

    /// <summary>
    /// 把 index.html 根元素 &lt;html data-theme="dark"&gt; 換成目前的主題：第一個影格就是正確的深淺色，不必等 init，
    /// 淺色主題開主視窗、彈出視窗或 WebView2 重新載入時才不會先畫出一整套深色介面。只改 &lt;html&gt; 開始標籤，找不到就原樣回傳。
    /// </summary>
    public static string ApplyInitialTheme(string html, bool dark)
    {
        if (dark || string.IsNullOrEmpty(html))
            return html;
        int tagStart = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
        int tagEnd = tagStart < 0 ? -1 : html.IndexOf('>', tagStart);
        if (tagEnd < 0)
            return html;
        int at = html.IndexOf(DarkTheme, tagStart, tagEnd - tagStart, StringComparison.Ordinal);
        return at < 0 ? html : html[..at] + LightTheme + html[(at + DarkTheme.Length)..];
    }
}
