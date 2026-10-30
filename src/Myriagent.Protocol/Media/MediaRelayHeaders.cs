namespace Myriagent.Media;

/// <summary>
/// 裝置以 <c>PUT /api/v1/device/relay/{relayId}</c> 回覆轉送時的標頭（WP4.7 計畫 D12；伺服器與桌面共用同一份名稱）。
/// 另外照用標準的 <c>Content-Range</c>、<c>Content-Type</c>、<c>ETag</c>；拒絕的回覆不帶本文
/// </summary>
internal static class MediaRelayHeaders
{
    /// <summary>裝置判斷的狀態碼：200、206、403、404、413、416、503</summary>
    public const string Status = "X-Status";

    /// <summary>檔案的總長（位元組，十進位）</summary>
    public const string TotalLength = "X-Total-Length";

    /// <summary>檔名：<c>UTF-8''</c> 加百分比編碼（<see cref="MediaHeaderRules.EncodeFileName"/>），伺服器解碼、清理後交給 <c>Content-Disposition</c></summary>
    public const string FileName = "X-File-Name";

    /// <summary><see cref="MediaContentKinds.Text"/> 或 <see cref="MediaContentKinds.Binary"/>（<c>/f/</c> 決定 inline 預覽或下載）</summary>
    public const string ContentKind = "X-Content-Kind";

    /// <summary>拒絕的原因（<see cref="MediaErrorCodes"/>）</summary>
    public const string Error = "X-Myriagent-Error";

    /// <summary>伺服器給瀏覽器：<c>/m/</c> 的圖片在來源離線或拒絕時改回縮圖，值是 <c>thumb</c></summary>
    public const string Fallback = "X-Myriagent-Fallback";

    /// <summary>裝置上傳縮圖時帶的對話 id</summary>
    public const string Conversation = "X-Myriagent-Conversation";
}

/// <summary><see cref="MediaRelayHeaders.ContentKind"/> 的值</summary>
internal static class MediaContentKinds
{
    public const string Text = "text";

    public const string Binary = "binary";
}

/// <summary><see cref="MediaRelayHeaders.Error"/> 的值</summary>
internal static class MediaErrorCodes
{
    /// <summary>不認得的參照、對話不在或不是完整同步</summary>
    public const string NotFound = "not_found";

    /// <summary>電腦的遠端政策不給（Off 或暫停時的原檔與檔案下載）</summary>
    public const string Policy = "policy";

    /// <summary>路徑檢查不通過（範圍外、禁止清單、重新解析點）</summary>
    public const string Forbidden = "forbidden";

    /// <summary>超過上限</summary>
    public const string TooLarge = "too_large";

    /// <summary>內容含憑證（設計 8.8）</summary>
    public const string Dlp = "dlp";
}
