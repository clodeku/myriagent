using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Myriagent.Agents;
using Myriagent.Media;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 複本（其他電腦的對話）在這台顯示圖片與影片的純函式（WP4.7 計畫步 12，D38）：
/// <list type="bullet">
/// <item>線上的代理產出媒體（<see cref="WireItem.Media"/>）只有參照與檔名，<see cref="WireItemReader"/> 不轉；<see cref="AddMediaRefs"/> 把它們放進
/// <see cref="AgentChatItem.MediaRefs"/>（<see cref="AgentMediaRef.MediaPath"/> 是檔名，不是路徑）。附件的參照 WireItemReader 已經放了。</item>
/// <item>共用渲染器只認路徑：有媒體來源時，渲染之前以 <see cref="WithMedia"/> 做一份複本，把有參照的附件與代理媒體換成「複本路徑」
/// <c>myriagent-replica:{鍵}/{檔名}</c>，<see cref="ReplicaMediaUrls"/> 再把它換成 <c>/m/</c>、<c>/t/</c> 網址。複本路徑不是本機磁碟路徑：
/// openPath、revealPath、<c>/media</c>、<c>/thumb</c> 一律不認（<see cref="AgentAttachmentRules.IsLocalDrivePath"/> 是 false）。</item>
/// <item>E9 的請求路徑與 Range（<see cref="RequestPath"/>、<see cref="RequestRange"/>）：原檔一次最多 8 MB（D38）。</item>
/// </list>
/// 純函式、不碰檔案系統
/// </summary>
internal static class ReplicaMediaRules
{
    /// <summary>複本路徑的開頭（不是磁碟代號、也不是 UNC：任何把它當成本機檔案的程式都會認不得）</summary>
    public const string PathPrefix = "myriagent-replica:";

    /// <summary>原檔一次在記憶體緩衝的上限（同伺服器的單段上限，D38）</summary>
    public const long MaxMediaBytes = MediaRelayRules.MaxSegmentBytes;

    /// <summary>縮圖的上限（同伺服器的 <c>Hub:Media:MaxThumbBytes</c> 預設）</summary>
    public const long MaxThumbBytes = 512 * 1024;

    /// <summary>E9 的路徑前綴（<c>GET /api/v1/device/media/{key}</c>）</summary>
    public const string DeviceMediaPrefix = "/api/v1/device/media/";

    /// <summary>
    /// 桌面遠端操作（sync-protocol 14.7）：伺服器給的本站相對網址 <c>/m/{鍵}</c>（media）、<c>/t/{鍵}?…</c>（thumb）、<c>/f/{鍵}</c>（file）的鍵與用途；
    /// 其他（絕對網址、別的路徑、鍵不合格、太長）回 null
    /// </summary>
    public static (string Key, string Purpose)? KeyOfRelativeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 2048 || !url.StartsWith('/') || url.StartsWith("//", StringComparison.Ordinal))
            return null;
        var path = url.Split('?', 2)[0];
        var purpose = path.StartsWith("/m/", StringComparison.Ordinal) ? MediaPurposes.Media
            : path.StartsWith("/t/", StringComparison.Ordinal) ? MediaPurposes.Thumb
            : path.StartsWith("/f/", StringComparison.Ordinal) ? MediaPurposes.File
            : null;
        if (purpose is null)
            return null;
        var key = path[3..];
        return MediaKeyRules.TryParse(key, out _, out _) ? (key, purpose) : null;
    }

    /// <summary>桌面遠端操作的上傳（sync-protocol 14.7）：<c>PUT /api/v1/device/uploads/{uploadId}</c></summary>
    public const string DeviceUploadPrefix = "/api/v1/device/uploads/";

    /// <summary>縮圖網址的邊長只有兩種（同伺服器的 E2、E9）</summary>
    public const int SmallThumb = 160;

    public const int LargeThumb = 320;

    /// <summary>沒有可用檔名時</summary>
    private const string FallbackName = "file";

    /// <summary>複本路徑：<c>myriagent-replica:{鍵}/{清理過的檔名}</c>；鍵不合格丟 <see cref="ArgumentException"/>（呼叫端的錯）</summary>
    public static string PathFor(string key, string? name)
    {
        if (!MediaKeyRules.TryParse(key, out _, out _))
            throw new ArgumentException("不是媒體鍵", nameof(key));
        return PathPrefix + key + "/" + (MediaHeaderRules.CleanFileName(name) ?? FallbackName);
    }

    /// <summary>複本路徑裡的鍵；不是複本路徑、鍵不合格、鍵後面不是 <c>/</c> 都回 false</summary>
    public static bool TryKey(string? path, [NotNullWhen(true)] out string? key)
    {
        key = null;
        if (path is null || !path.StartsWith(PathPrefix, StringComparison.Ordinal))
            return false;
        var rest = path.AsSpan(PathPrefix.Length);
        if (rest.Length <= MediaKeyRules.Length || rest[MediaKeyRules.Length] != '/')
            return false;
        var candidate = rest[..MediaKeyRules.Length].ToString();
        if (!MediaKeyRules.TryParse(candidate, out _, out _))
            return false;
        key = candidate;
        return true;
    }

    /// <summary>
    /// 線上的代理產出媒體 → <see cref="AgentChatItem.MediaRefs"/>（接在附件的參照後面，照線上的順序）：參照合格、檔名是可直接顯示的圖片或影片的才加；
    /// <see cref="AgentMediaRef.MediaPath"/> 是線上的檔名。沒有任何一筆時不動 <paramref name="item"/>
    /// </summary>
    public static void AddMediaRefs(AgentChatItem item, WireItem wire)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(wire);
        foreach (var media in wire.Media ?? [])
        {
            if (media?.Ref is not { } mediaRef || !ConversationEventRules.IsMediaRef(mediaRef) || string.IsNullOrEmpty(media.Name)
                || !AgentAttachmentRules.IsDisplayableMedia(media.Name))
                continue;
            (item.MediaRefs ??= []).Add(new AgentMediaRef { Ref = mediaRef, MediaPath = media.Name });
        }
    }

    /// <summary>
    /// 渲染用的複本（不改 <paramref name="item"/>）：有參照的附件的 <see cref="AgentAttachment.StoredPath"/> 與代理媒體（<see cref="AgentChatItem.MediaPaths"/>）
    /// 換成複本路徑，鍵是 <c>{擁有者}-{參照}</c>。擁有者不是裝置 id、沒有任何合格的參照回 null（照原樣渲染，沒有網址）
    /// </summary>
    public static AgentChatItem? WithMedia(AgentChatItem item, string? ownerDeviceId)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!SyncIds.IsDeviceId(ownerDeviceId) || item.MediaRefs is not { Count: > 0 } refs)
            return null;
        var attachmentRefs = new Dictionary<string, string>(StringComparer.Ordinal);
        var media = new List<(string Ref, string Name)>();
        foreach (var r in refs)
        {
            if (r is null || !ConversationEventRules.IsMediaRef(r.Ref))
                continue;
            if (r.AttachmentId is { } attachmentId)
                attachmentRefs.TryAdd(attachmentId, r.Ref);
            else if (r.MediaPath is { Length: > 0 } name)
                media.Add((r.Ref, name));
        }

        var attachments = item.Attachments ?? [];
        if (media.Count == 0 && !attachments.Any(a => a is not null && attachmentRefs.ContainsKey(a.Id)))
            return null;
        var copy = ChatItemCopyRules.Copy(item);
        foreach (var a in copy.Attachments ?? [])
        {
            if (a is not null && attachmentRefs.TryGetValue(a.Id, out var attachmentRef))
                a.StoredPath = PathFor(MediaKeyRules.Format(ownerDeviceId!, attachmentRef), a.FileName);
        }

        copy.MediaPaths = media.Select(m => PathFor(MediaKeyRules.Format(ownerDeviceId!, m.Ref), m.Name)).ToList();
        return copy;
    }

    /// <summary>
    /// 渲染好的複本項目（<see cref="WithMedia"/> 的複本經 <see cref="ReplicaMediaUrls"/> 渲染）：附件與代理媒體的 <c>path</c> 是複本路徑的拿掉
    /// （網頁的「開啟」「在檔案總管顯示」只對這台的檔案有用；附件照 WP4.3 是空字串、代理媒體是 null）。直接改 <paramref name="rendered"/> 並回傳它
    /// </summary>
    public static JObject HidePaths(JObject rendered)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        if (rendered["attachments"] is JArray attachments)
        {
            foreach (var a in attachments.OfType<JObject>())
            {
                if (TryKey(a.Value<string>("path"), out _))
                    a["path"] = string.Empty;
            }
        }

        if (rendered["media"] is JArray media)
        {
            foreach (var m in media.OfType<JObject>())
            {
                if (TryKey(m.Value<string>("path"), out _))
                    m["path"] = null;
            }
        }

        return rendered;
    }

    /// <summary>
    /// E9 的請求路徑（相對網址）：原檔 <c>?purpose=media</c>（沒有 size）、縮圖 <c>?purpose=thumb&amp;s=160|320</c>；鍵、用途或大小不合格回 null
    /// </summary>
    public static string? RequestPath(string? key, string? purpose, int? size)
    {
        if (!MediaKeyRules.TryParse(key, out _, out _))
            return null;
        return purpose switch
        {
            MediaPurposes.Media when size is null => DeviceMediaPrefix + key + "?purpose=" + MediaPurposes.Media,
            MediaPurposes.Thumb when size is SmallThumb or LargeThumb => DeviceMediaPrefix + key + "?purpose=" + MediaPurposes.Thumb + "&s="
                                                                         + size.Value.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    /// <summary>
    /// 原檔送給 E9 的 Range（一次最多 <see cref="MaxMediaBytes"/>）：網頁的單一區間照起點、結尾縮到 8 MB 之內；後綴範圍照原樣（伺服器縮）；
    /// 沒帶或不合格的要前 8 MB，<c>Whole</c> 為 true（回來的正好是整份時改回 200）
    /// </summary>
    public static (string Header, bool Whole) RequestRange(string? browserRange)
    {
        var (kind, range, suffix) = HttpRangeRules.Parse(browserRange, length: null);
        switch (kind)
        {
            case RangeKind.Satisfiable:
            {
                var last = range.Start > long.MaxValue - MaxMediaBytes ? long.MaxValue : range.Start + MaxMediaBytes - 1;
                var end = range.IsOpenEnded ? last : Math.Min(range.End, last);
                return (string.Create(CultureInfo.InvariantCulture, $"bytes={range.Start}-{end}"), false);
            }

            case RangeKind.Suffix:
                return (string.Create(CultureInfo.InvariantCulture, $"bytes=-{suffix}"), false);
            default:
                return (string.Create(CultureInfo.InvariantCulture, $"bytes=0-{MaxMediaBytes - 1}"), true);
        }
    }

    /// <summary>縮圖網址的邊長（160 以下給 160，其他 320）</summary>
    public static int ThumbSize(int size) => size <= SmallThumb ? SmallThumb : LargeThumb;
}
