using System.Globalization;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Rendering;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Rendering;

/// <summary>
/// 伺服器渲染項目的純函式規則（WP4.2 計畫步 7、D15、D18、D20、D33）：線上的 <see cref="WireItem"/> 轉成共用渲染器吃的 <see cref="AgentChatItem"/>，
/// 渲染之後的後處理（拿掉路徑、seq 填 Ord、自己組附件與媒體、加卡片摘要與風險、淨化 HTML）、太大時只留尾段、佔位。
/// <c>Myriagent.Protocol/Rendering</c> 一律不動（需求裁定 C3）：伺服器多出來的都在這裡。純函式、執行緒安全
/// </summary>
internal static class HubItemRules
{
    /// <summary>D33：blob 讀不到（WP4.7 之前）、只留尾段之後仍然太大</summary>
    public const string TooLargeMessage = "內容太大，暫時無法在瀏覽器顯示";

    /// <summary>存的 payload 解不開、blob 的內容有權杖、渲染時發生例外</summary>
    public const string UnavailableMessage = "這一筆內容暫時無法在瀏覽器顯示";

    /// <summary>附件的縮圖邊長（同桌面 ItemRenderer.RenderAttachment）</summary>
    public const int AttachmentThumbSize = 320;

    /// <summary>
    /// 只留尾段時每個字元預留的位元組（<see cref="ShrinkChars"/>）：UTF-8 最多 3 位元組、HTML 實體最多 6 倍（<c>&amp;quot;</c>）、
    /// 控制字元的 JSON 跳脫 6 位元組，text、output、detail 三欄各留這麼多，整筆通常放得下；放不下的送佔位
    /// </summary>
    public const int ShrinkBytesPerChar = 16;

    /// <summary>瀏覽器模式另帶 <c>requestDigest</c>、<c>risk</c> 的卡片（docs/web-protocol.md 第三節 Item）</summary>
    private static readonly HashSet<string> CardKinds = new(StringComparer.Ordinal)
    {
        nameof(AgentItemKind.Approval), nameof(AgentItemKind.Question), nameof(AgentItemKind.PlanApproval),
    };

    /// <summary>佔位照原本的種類顯示的（文字類）；其他（卡片、工具、待辦）改成 Notice：少了欄位的卡片在網頁上按了也沒用</summary>
    private static readonly HashSet<string> PlaceholderKinds = new(StringComparer.Ordinal)
    {
        nameof(AgentItemKind.User), nameof(AgentItemKind.Assistant), nameof(AgentItemKind.Thinking), nameof(AgentItemKind.Notice), nameof(AgentItemKind.Error),
    };

    /// <summary>
    /// 線上的項目 → 共用渲染器的輸入（D18）：附件與媒體清空（由 <see cref="Finish"/> 依參照自己組）、<c>Seq</c>＝<c>Ord</c>、<c>RequestKey</c> 是 null、
    /// 答案陣列轉回以題目 id 為鍵的字典（重複的 id 取第一個）。時間以 UTC 轉（<see cref="ProtocolValues.ToUnixMs"/> 轉回來逐毫秒相同，不受夏令時間重複的那一小時影響）；
    /// <c>createdAt</c> 是 null 時用收到事件的時間，轉換不了的時間當成 <see cref="DateTime.MinValue"/>（網頁的 time 是 null）。
    /// 種類或狀態不是列舉的名稱（共用驗證已擋，這裡再擋一次）回 null；<c>noticeKind</c>、<c>risk</c> 不認得的值當成沒有
    /// </summary>
    /// <param name="receivedAt">伺服器收到這筆的時間（Unix 毫秒）</param>
    public static AgentChatItem? ToChatItem(WireItem item, long receivedAt)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!TryParseName<AgentItemKind>(item.Kind, out var kind) || !TryParseName<AgentItemStatus>(item.Status, out var status))
            return null;

        var answers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var answer in item.Answers ?? [])
            answers.TryAdd(answer.Id, answer.Values.ToList());

        return new AgentChatItem
        {
            Id = item.Id,
            Kind = kind,
            Status = status,
            Text = item.Text ?? string.Empty,
            ToolName = item.ToolName,
            ToolKind = item.ToolKind,
            Title = item.Title,
            Detail = item.Detail,
            Output = item.Output,
            Diff = item.Diff?.ToList(),
            FilePath = item.FilePath,
            Attachments = [],
            Questions = (item.Questions ?? []).Select(q => new AgentQuestion
            {
                Id = q.Id,
                Header = q.Header,
                Text = q.Text,
                MultiSelect = q.MultiSelect,
                AllowOther = q.AllowOther,
                IsSecret = q.IsSecret,
                Options = q.Options.Select(o => new AgentQuestionOption { Label = o.Label, Description = o.Description, Preview = o.Preview }).ToList(),
            }).ToList(),
            Answers = answers,
            Decision = item.Decision,
            CanAllowForSession = item.CanAllowForSession,
            AllowSessionLabel = item.AllowSessionLabel,
            AnswerAsMessage = item.AnswerAsMessage,
            MediaPaths = [],
            PlanSteps = (item.Plan ?? []).Select(s => new AgentPlanStep { Text = s.Text, Status = s.Status }).ToList(),
            CreatedAt = ToTime(item.CreatedAt ?? receivedAt),
            UpdatedAt = ToTime(item.UpdatedAt ?? item.CreatedAt ?? receivedAt),
            NoticeKind = TryParseName<AgentNoticeKind>(item.NoticeKind, out var notice) ? notice : null,
            Ord = item.Ord,
            RequestDigest = item.RequestDigest,
            Risk = TryParseName<AgentRemoteRisk>(item.Risk, out var risk) ? risk : null,
            Author = CollaboratorRules.CleanAuthor(item.Author),
            ResolvedBy = CollaboratorRules.CleanAuthor(item.ResolvedBy),
            RequestKey = null,
            Seq = item.Ord,
        };
    }

    /// <summary>
    /// 共用渲染器的輸出 → 送給瀏覽器的 Item（D18，就地修改並回傳 <paramref name="rendered"/>）：
    /// ① 任何深度的 <c>path</c> 鍵拿掉（答案物件的鍵是題目 id，不是路徑，不動）② <c>seq</c>＝<c>ord</c>
    /// ③ 依 <see cref="WireAttachment"/>／<see cref="WireMedia"/> 的參照組 <c>attachments</c>／<c>media</c>（D15：沒有網址的媒體改成只有檔名的附件、
    /// 沒有網址的影片附件改成 Document）④ 卡片加 <c>requestDigest</c>（原樣）與小寫的 <c>risk</c>（不認得的省略）⑤ <c>html</c>／<c>detailHtml</c> 經 <see cref="ItemSanitizer"/>
    /// </summary>
    /// <param name="ownerDeviceId">擁有者裝置：參照是它發的（<see cref="HubMediaUrls.ForRef"/>）</param>
    public static JObject Finish(JObject rendered, WireItem item, HubMediaUrls urls, string ownerDeviceId)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(urls);

        foreach (var property in rendered.Descendants().OfType<JProperty>().Where(p => IsPath(rendered, p)).ToList())
            property.Remove();

        rendered["seq"] = item.Ord;

        rendered.Remove("attachments");
        rendered.Remove("media");
        var attachments = new JArray();
        var media = new JArray();
        foreach (var attachment in item.Attachments ?? [])
            attachments.Add(Attachment(attachment, urls, ownerDeviceId));
        var index = 0;
        foreach (var entry in item.Media ?? [])
        {
            index++;
            if (urls.ForRef(ownerDeviceId, entry.Ref, HubMediaUrls.PurposeMedia) is { } url)
            {
                media.Add(new JObject { ["url"] = url, ["kind"] = entry.Kind, ["name"] = entry.Name });
                continue;
            }

            attachments.Add(new JObject
            {
                ["id"] = "media-" + index.ToString(CultureInfo.InvariantCulture),
                ["name"] = entry.Name,
                ["kind"] = nameof(AgentAttachmentKind.Document),
                ["size"] = null,
                ["thumbUrl"] = null,
                ["url"] = null,
                ["videoInfo"] = null,
            });
        }

        if (attachments.Count > 0)
            rendered["attachments"] = attachments;
        if (media.Count > 0)
            rendered["media"] = media;

        if (CardKinds.Contains(item.Kind))
        {
            if (item.RequestDigest is { } digest)
                rendered["requestDigest"] = digest;
            if (TryParseName<AgentRemoteRisk>(item.Risk, out var risk))
                rendered["risk"] = risk.ToString().ToLowerInvariant();
        }

        foreach (var key in new[] { "html", "detailHtml" })
        {
            if (rendered[key] is JValue { Type: JTokenType.String, Value: string html })
                rendered[key] = ItemSanitizer.Sanitize(html);
        }

        return rendered;
    }

    /// <summary>
    /// 太大的項目（D20）：<c>text</c>、<c>output</c>、<c>detail</c> 各只留最後 <paramref name="maxChars"/> 字，前面加「…（前 N 字省略）」
    /// （原本就截斷過的累計字數，同中樞的工具輸出），切點不切開代理字元對；切點可能落在權杖中間，留下的第一行經
    /// <see cref="CredentialLeakRules.MaskTruncated"/> 換掉。其他欄位不動；沒有超過的欄位原樣
    /// </summary>
    public static WireItem Shrink(WireItem item, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        return item with { Text = Tail(item.Text, maxChars), Output = Tail(item.Output, maxChars), Detail = Tail(item.Detail, maxChars) };
    }

    /// <summary>渲染後的上限是 <paramref name="maxBytes"/> 位元組時，<see cref="Shrink"/> 每欄留的字數</summary>
    public static int ShrinkChars(int maxBytes) => Math.Max(1, maxBytes / ShrinkBytesPerChar);

    /// <summary>
    /// 不先完整渲染、直接只留尾段：<c>text</c>、<c>output</c>、<c>detail</c> 加起來的字數已經超過 <paramref name="maxBytes"/>
    /// （WP4.7 的 blob 換回之後一欄可以到 64 MB，整段丟進 Markdig 會拖住閘道的迴圈；渲染出來幾乎一定超過上限）
    /// </summary>
    public static bool ShouldShrinkFirst(WireItem item, int maxBytes)
        => (long)(item.Text?.Length ?? 0) + (item.Output?.Length ?? 0) + (item.Detail?.Length ?? 0) > maxBytes;

    /// <summary>
    /// 顯示不了的項目的佔位（D33）：形狀同共用渲染器失敗時的錯誤說明（id、kind、status、html、time、seq）。文字類的種類照原本的、其他改成 Notice，
    /// 狀態不認得的當成 Done；<paramref name="createdAt"/> 轉換不了（或 null）時 time 是 null
    /// </summary>
    public static JObject Placeholder(string id, string? kind, string? status, long ord, long? createdAt, string message) => new()
    {
        ["id"] = id,
        ["kind"] = kind is not null && PlaceholderKinds.Contains(kind) ? kind : nameof(AgentItemKind.Notice),
        ["status"] = TryParseName<AgentItemStatus>(status, out var parsed) ? parsed.ToString() : nameof(AgentItemStatus.Done),
        ["html"] = AgentMarkdownRenderer.PlainToHtml(message),
        ["time"] = createdAt is { } ms ? ProtocolValues.ToUnixMs(ToTime(ms)) : null,
        ["seq"] = ord,
    };

    /// <summary>
    /// 附件大小的文字（同 <see cref="AgentAttachmentRules.FormatSize"/> 的格式，但固定以不變文化特性格式化：伺服器的輸出不隨主機的地區設定改變；
    /// zh-TW 下與桌面逐字相同，HubItemRulesTests 比對）
    /// </summary>
    public static string FormatSize(long bytes)
    {
        var c = CultureInfo.InvariantCulture;
        if (bytes < 1024)
            return string.Create(c, $"{bytes} B");
        if (bytes < 1024 * 1024)
            return string.Create(c, $"{bytes / 1024.0:0.#} KB");
        if (bytes < 1024L * 1024 * 1024)
            return string.Create(c, $"{bytes / 1024.0 / 1024:0.#} MB");
        return string.Create(c, $"{bytes / 1024.0 / 1024 / 1024:0.##} GB");
    }

    /// <summary>Unix 毫秒 → UTC 時間；超出 DateTimeOffset 範圍的回 <see cref="DateTime.MinValue"/>（ToUnixMs 回 null）</summary>
    public static DateTime ToTime(long ms)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>只認列舉的名稱（區分大小寫；不認數字字串與沒定義的值）</summary>
    private static bool TryParseName<T>(string? value, out T result)
        where T : struct, Enum
    {
        result = default;
        return value is not null && Enum.GetNames<T>().Contains(value, StringComparer.Ordinal) && Enum.TryParse(value, ignoreCase: false, out result);
    }

    /// <summary>名稱是 path 的屬性（不分大小寫），頂層 <c>answers</c> 物件的鍵（題目 id）除外</summary>
    private static bool IsPath(JObject root, JProperty property)
        => string.Equals(property.Name, "path", StringComparison.OrdinalIgnoreCase)
           && !(property.Parent is JObject owner && owner.Parent is JProperty { Name: "answers" } answers && answers.Parent == root);

    /// <summary>附件（同桌面的 Attachment 形狀，沒有 path；網址只看參照）</summary>
    private static JObject Attachment(WireAttachment a, HubMediaUrls urls, string ownerDeviceId)
    {
        var kind = a.Kind;
        var url = AgentAttachmentRules.IsDisplayableMedia(a.Name) ? urls.ForRef(ownerDeviceId, a.Ref, HubMediaUrls.PurposeMedia) : null;
        var thumb = kind is nameof(AgentAttachmentKind.Image) or nameof(AgentAttachmentKind.Video)
            ? urls.ForRef(ownerDeviceId, a.Ref, HubMediaUrls.PurposeThumb, AttachmentThumbSize)
            : null;
        string? videoInfo = null;
        if (kind == nameof(AgentAttachmentKind.Video))
        {
            if (url is null)
            {
                // D15：放不了的影片在網頁上只剩按了沒反應的播放鈕，改成只有檔名與大小的文件
                kind = nameof(AgentAttachmentKind.Document);
                thumb = null;
            }
            else
            {
                videoInfo = (a.Duration is { } duration ? AgentAttachmentRules.FormatSeconds(duration) + "・" : string.Empty)
                            + string.Create(CultureInfo.InvariantCulture, $"{a.Frames ?? 0} 張畫面");
            }
        }

        return new JObject
        {
            ["id"] = a.Id,
            ["name"] = a.Name,
            ["kind"] = kind,
            ["size"] = FormatSize(a.Size),
            ["thumbUrl"] = thumb,
            ["url"] = url,
            ["videoInfo"] = videoInfo,
        };
    }

    private static string? Tail(string? text, int maxChars)
    {
        if (text is null)
            return null;
        var prefixLength = AgentHubRules.OmittedPrefixLength(text);
        var body = text[prefixLength..];
        if (body.Length <= maxChars)
            return text;

        long omitted = 0;
        if (prefixLength > 0)
            _ = long.TryParse(new string(text[..prefixLength].Where(char.IsAsciiDigit).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out omitted);
        var cut = body.Length - maxChars;
        if (char.IsLowSurrogate(body[cut]))
            cut++;
        omitted += cut;
        return CredentialLeakRules.MaskTruncated(string.Create(CultureInfo.InvariantCulture, $"…（前 {omitted:N0} 字省略）\n") + body[cut..]);
    }
}
