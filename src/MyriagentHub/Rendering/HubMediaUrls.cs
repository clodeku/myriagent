using System.Globalization;
using Myriagent.Media;
using Myriagent.Rendering;
using Myriagent.Sync;

namespace MyriagentHub.Rendering;

/// <summary>
/// 伺服器渲染項目時的網址（WP4.2 計畫步 7、D15、D18）。伺服器沒有任何本機路徑：<see cref="IMediaUrlBuilder"/> 的路徑類方法一律回 <c>"#"</c>
/// （Markdown 引用的本機圖片因此被 <see cref="ItemSanitizer"/> 換成 alt 文字），只有本機檔案連結是帶路徑的頁內錨點（<see cref="MarkdownLinkUrl"/>），
/// <see cref="AllowMarkdownImage"/> 不做事。
/// 附件與代理產出的媒體只以裝置發的參照取網址（<see cref="ForRef"/>，WP4.7 起照 <c>/m/</c>、<c>/t/</c>、<c>/f/</c> 給）；沒有參照的媒體，
/// 渲染就改成只有檔名的附件（D15）。無狀態、執行緒安全；方法是 virtual，測試以子類別給網址
/// </summary>
internal class HubMediaUrls : IMediaUrlBuilder
{
    /// <summary><see cref="ForRef"/> 的用途：直接顯示或播放的原檔（<c>/m/</c>）</summary>
    public const string PurposeMedia = "media";

    /// <summary><see cref="ForRef"/> 的用途：縮圖（<c>/t/</c>）</summary>
    public const string PurposeThumb = "thumb";

    /// <summary><see cref="ForRef"/> 的用途：預覽或下載（<c>/f/</c>）</summary>
    public const string PurposeFile = "file";

    /// <summary>伺服器不認得本機路徑</summary>
    public const string NoUrl = "#";

    /// <summary>縮圖網址的 <c>s</c>（E2 只收 160、320）</summary>
    private const int SmallThumbSize = 160;

    private const int LargeThumbSize = 320;

    public virtual string MediaUrl(string path) => NoUrl;

    public virtual string ThumbUrl(string path, int size, string? version = null) => NoUrl;

    public virtual string MarkdownImageUrl(string path) => NoUrl;

    /// <summary>
    /// 本機檔案連結：<c>#local-file=</c> 加上編碼後的完整路徑（頁內錨點，點了不會離開頁面）；網頁攔下點擊，改向對話所在的電腦送
    /// <c>fileFetch</c>，那台照工作資料夾範圍、拒絕清單與大小上限決定給不給（web-protocol 第三節）
    /// </summary>
    public virtual string MarkdownLinkUrl(string path) => LocalFilePrefix + Uri.EscapeDataString(path ?? string.Empty);

    /// <summary>瀏覽器版本機檔案連結的錨點前綴（網頁 filelinkrules.js 的 LOCAL_MARKER）</summary>
    public const string LocalFilePrefix = "#local-file=";

    public void AllowMarkdownImage(string path)
    {
    }

    /// <summary>
    /// 擁有者裝置 <paramref name="deviceId"/> 發的媒體參照 <paramref name="mediaRef"/> 的伺服器相對網址（<c>/m/{…}</c>、<c>/t/{…}</c>、<c>/f/{…}</c>，
    /// 形狀要符合 <see cref="ItemSanitizer"/> 的本站網址）；沒有參照或還不能提供回 null。
    /// WP4.7（計畫 D2）：鍵是 <c>{deviceId}-{ref}</c>（<see cref="MediaKeyRules"/>），裝置 id 與參照都要合格才給；縮圖的 <c>s</c> 只有 160、320
    /// （≤160 給 160，其他給 320）。網址能不能看由媒體端點每次請求查 <c>media_refs</c> 決定（D7），這裡不查資料庫
    /// </summary>
    /// <param name="purpose"><see cref="PurposeMedia"/>、<see cref="PurposeThumb"/>、<see cref="PurposeFile"/>；其他回 null</param>
    /// <param name="thumbSize">縮圖的邊長（只有 <see cref="PurposeThumb"/> 用；沒給是 320）</param>
    public virtual string? ForRef(string deviceId, string? mediaRef, string purpose, int? thumbSize = null)
    {
        if (!SyncIds.IsDeviceId(deviceId) || !ConversationEventRules.IsMediaRef(mediaRef))
            return null;
        var key = MediaKeyRules.Format(deviceId, mediaRef!);
        return purpose switch
        {
            PurposeMedia => "/m/" + key,
            PurposeThumb => "/t/" + key + "?s=" + (thumbSize <= SmallThumbSize ? SmallThumbSize : LargeThumbSize).ToString(CultureInfo.InvariantCulture),
            PurposeFile => "/f/" + key,
            _ => null,
        };
    }
}
