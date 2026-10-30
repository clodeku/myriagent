using Myriagent.Rendering;

namespace Myriagent.Hub;

/// <summary>
/// 不登記任何本機檔案的網址產生器（WP4.3 計畫步 15，D27）：其他電腦的對話（唯讀複本）的項目以它渲染。複本裡的路徑是擁有者電腦上的，
/// 登記進這台 /media、/thumb 的白名單，等於讓別台送來的內容決定這台要提供哪些本機檔案。附件與代理產出的媒體沒有網址（網頁不顯示縮圖與預覽），
/// 回覆 Markdown 裡的本機圖片與檔案連結一律是「#」（同渲染器對不認得的網址的處理）
/// </summary>
internal sealed class NullMediaUrlBuilder : IMediaUrlBuilder
{
    public static NullMediaUrlBuilder Instance { get; } = new();

    private NullMediaUrlBuilder()
    {
    }

    /// <remarks>介面的回傳型別是 string：渲染器把它放進可以是 null 的欄位（附件與媒體的 url），網頁沒有網址就不顯示</remarks>
    public string MediaUrl(string path) => null!;

    /// <remarks>同 <see cref="MediaUrl"/>：附件的 thumbUrl 是 null</remarks>
    public string ThumbUrl(string path, int size, string? version = null) => null!;

    public string MarkdownImageUrl(string path) => "#";

    public string MarkdownLinkUrl(string path) => "#";

    public void AllowMarkdownImage(string path)
    {
    }
}
