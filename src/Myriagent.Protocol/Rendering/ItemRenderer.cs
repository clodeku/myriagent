using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Rendering;

/// <summary>
/// 對話項目 → 網頁的 Item JSON（docs/web-protocol.md 的 Item、Attachment）。桌面中樞與伺服器共用（設計 2.5、4.9）：
/// 伺服器不信任裝置送來的 HTML，一律從原始 AgentChatItem 以這裡重新渲染，兩邊的輸出必須逐位元組相同（ItemRendererGoldenTests）。
/// 本機檔案的網址一律經 IMediaUrlBuilder（桌面同時登記白名單）；代理產生的文字只會進 *html 欄位（Markdig 停用原始 HTML）或純文字欄位。
/// 不是執行緒安全的：桌面只在 UI 執行緒使用（白名單也只在 UI 執行緒讀寫）
/// </summary>
internal sealed class ItemRenderer
{
    private readonly IMediaUrlBuilder urls;
    private readonly Action<string, Exception>? logError;
    private readonly Action<string>? logWarning;

    /// <summary>渲染失敗過的項目（同一筆只記一次：串流時每 50 毫秒重新渲染，不能每次都寫一筆記錄）</summary>
    private readonly HashSet<string> failures = new(StringComparer.Ordinal);

    /// <param name="urls">本機檔案的網址（桌面：LocalMediaUrls）</param>
    /// <param name="logError">整筆渲染失敗、改送錯誤說明時的記錄</param>
    /// <param name="logWarning">Markdown 轉換失敗、改用純文字顯示時的記錄</param>
    public ItemRenderer(IMediaUrlBuilder urls, Action<string, Exception>? logError = null, Action<string>? logWarning = null)
    {
        this.urls = urls ?? throw new ArgumentNullException(nameof(urls));
        this.logError = logError;
        this.logWarning = logWarning;
    }

    /// <summary>
    /// 一筆項目 → 畫面資料。開啟對話、往上載入、合併送出都是整批渲染，任何一筆失敗都不能讓整則訊息送不出去
    /// （分頁會一直停在載入中、之後每次開都失敗）：失敗時改送一筆錯誤說明
    /// </summary>
    /// <param name="workingDirectory">對話的工作資料夾（回覆裡的相對路徑連結以此解析）；null＝相對路徑不當本機檔案</param>
    /// <param name="prerendered">
    /// 項目文字已經在別處轉好的 HTML（桌面：很長的執行中回覆在背景執行緒轉，BackgroundMarkdown）；null＝照常在這裡轉。
    /// 只用在文字走 Markdown 的種類（使用者訊息、工具與卡片照舊）
    /// </param>
    public JObject Render(AgentChatItem item, string? workingDirectory, PrerenderedMarkdown? prerendered = null)
    {
        try
        {
            return RenderCore(item, workingDirectory, prerendered);
        }
        catch (Exception ex)
        {
            if (failures.Add(item.Id))
                logError?.Invoke($"渲染項目 {item.Id} 失敗", ex);
            return new JObject
            {
                ["id"] = item.Id,
                ["kind"] = AgentItemKind.Error.ToString(),
                ["status"] = AgentItemStatus.Done.ToString(),
                ["html"] = AgentMarkdownRenderer.PlainToHtml($"這一筆內容無法顯示：{ex.Message}"),
                ["time"] = ProtocolValues.ToUnixMs(item.CreatedAt),
                ["seq"] = item.Seq,
            };
        }
    }

    /// <summary>附件（使用者訊息與輸入框的附件列）：圖片與影片有縮圖，可直接顯示或播放的另有媒體網址</summary>
    public JObject RenderAttachment(AgentAttachment a)
    {
        string? thumb = a.Kind switch
        {
            AgentAttachmentKind.Image => urls.ThumbUrl(a.StoredPath, 320),
            AgentAttachmentKind.Video => a.ModelImagePaths.FirstOrDefault() is { } frame ? urls.ThumbUrl(frame, 320) : urls.ThumbUrl(a.StoredPath, 320),
            _ => null,
        };
        var json = new JObject
        {
            ["id"] = a.Id,
            ["name"] = a.FileName,
            ["kind"] = a.Kind.ToString(),
            ["size"] = AgentAttachmentRules.FormatSize(a.SizeBytes),
            ["path"] = a.StoredPath,
            ["thumbUrl"] = thumb,
            ["url"] = AgentAttachmentRules.IsDisplayableMedia(a.StoredPath) ? urls.MediaUrl(a.StoredPath) : null,
            ["videoInfo"] = a.Kind == AgentAttachmentKind.Video
                ? (a.DurationSeconds.HasValue ? AgentAttachmentRules.FormatSeconds(a.DurationSeconds.Value) + "・" : string.Empty) + $"{a.ModelImagePaths.Count} 張畫面"
                : null,
        };
        // 語音訊息的錄音波形（AgentAttachment.Waveform）：有才帶，沒有的附件輸出和以前一字不差
        if (VoiceMessageRules.NormalizeWaveform(a.Waveform) is { } wave)
            json["wave"] = wave;
        return json;
    }

    /// <summary>
    /// 代理的 Markdown → HTML。Markdig 遇到巢狀過深的內容（例如一行 128 個「&gt;」）會丟 ArgumentException：
    /// 改用純文字顯示，內容照樣看得到；轉換失敗時找到的本機圖片不算（不通知 AllowMarkdownImage）
    /// </summary>
    private string MarkdownToHtml(AgentChatItem item, string? markdown, List<string> localImages, string? workingDirectory)
    {
        var found = new List<string>();
        try
        {
            var html = AgentMarkdownRenderer.ToHtml(markdown, found, workingDirectory, urls);
            localImages.AddRange(found);
            return html;
        }
        catch (Exception ex)
        {
            if (failures.Add(item.Id))
                logWarning?.Invoke($"項目 {item.Id} 的 Markdown 轉換失敗，改用純文字顯示：{ex.Message}");
            return AgentMarkdownRenderer.PlainToHtml(markdown);
        }
    }

    /// <summary>項目文字走 Markdown 的種類（其他的 html 是純文字或空的）</summary>
    public static bool TextIsMarkdown(AgentItemKind kind) => kind switch
    {
        AgentItemKind.User or AgentItemKind.Tool or AgentItemKind.Approval or AgentItemKind.Question or AgentItemKind.PlanApproval => false,
        _ => true,
    };

    private JObject RenderCore(AgentChatItem item, string? workingDirectory, PrerenderedMarkdown? prerendered)
    {
        var localImages = new List<string>();
        string html;
        if (item.Kind == AgentItemKind.User)
            html = AgentMarkdownRenderer.PlainToHtml(item.Text);
        else if (!TextIsMarkdown(item.Kind))
            html = string.Empty;
        else if (prerendered is not null)
        {
            html = prerendered.Html;
            localImages.AddRange(prerendered.Images);
        }
        else
            html = MarkdownToHtml(item, item.Text, localImages, workingDirectory);

        var result = new JObject
        {
            ["id"] = item.Id,
            ["kind"] = item.Kind.ToString(),
            ["status"] = item.Status.ToString(),
            ["html"] = html,
            ["time"] = ProtocolValues.ToUnixMs(item.CreatedAt),
            ["seq"] = item.Seq,
        };
        // 〔熱更新〕CLI 換版的說明（網頁加 CLI 圖示）；一般提示省略
        if (item.NoticeKind is { } noticeKind)
            result["noticeKind"] = ProtocolValues.ToProtocol(noticeKind);
        // 從 claude.ai 或 Claude App 送來的使用者訊息（網頁標示來源）；在 app 送出的省略
        if (item.Source is { } source)
            result["source"] = ProtocolValues.ToProtocol(source);
        // 多人協作（2026-10-30）：協作人送的使用者訊息、協作人回應的卡片（網頁標出是誰）；擁有者的省略
        if (item.Author is { Length: > 0 } author)
            result["author"] = author;
        if (item.ResolvedBy is { Length: > 0 } resolvedBy)
            result["resolvedBy"] = resolvedBy;

        if (item.Kind is AgentItemKind.Tool or AgentItemKind.Approval or AgentItemKind.PlanApproval)
        {
            result["toolKind"] = item.ToolKind ?? "other";
            result["toolName"] = item.ToolName;
            result["title"] = item.Title;
            result["detail"] = item.Kind == AgentItemKind.PlanApproval ? null : item.Detail;
            result["output"] = item.Output;
            result["decision"] = item.Decision;
            result["canAllowForSession"] = item.CanAllowForSession;
            if (item.AllowSessionLabel != null)
                result["allowSessionLabel"] = item.AllowSessionLabel;
            result["filePath"] = item.FilePath;
            if (item.Diff is { Count: > 0 })
            {
                result["diff"] = new JArray(item.Diff.Select(d => new JObject { ["t"] = d.T, ["s"] = d.S }));
                var (add, del) = DiffRules.Stats(item.Diff);
                result["diffStats"] = new JObject { ["add"] = add, ["del"] = del };
            }
        }
        if (item.Kind == AgentItemKind.PlanApproval)
            result["detailHtml"] = MarkdownToHtml(item, item.Detail ?? string.Empty, localImages, workingDirectory);
        else if (item.Kind == AgentItemKind.Tool && item.ToolKind is "write" or "edit" && !string.IsNullOrEmpty(item.Text))
            result["detailHtml"] = MarkdownToHtml(item, item.Text, localImages, workingDirectory);
        else if (item.Kind == AgentItemKind.Question && !string.IsNullOrEmpty(item.Detail))
            result["detailHtml"] = MarkdownToHtml(item, item.Detail, localImages, workingDirectory);

        // 回覆中引用的本機圖片：畫面上確實出現了（轉換成功），由網址的實作決定登記哪些（桌面只登記可顯示的圖片與影片）
        foreach (var path in localImages)
            urls.AllowMarkdownImage(path);

        if (item.Kind == AgentItemKind.Question)
        {
            if (!string.IsNullOrEmpty(item.FilePath))
                result["filePath"] = item.FilePath;
            // 不是代理自己問的（MCP 伺服器的 elicitation：「MCP：伺服器名稱」）：網頁寫在卡片標頭；代理問的省略
            if (!string.IsNullOrEmpty(item.ToolName))
                result["toolName"] = item.ToolName;
            result["questions"] = new JArray(item.Questions.Select(q => new JObject
            {
                ["id"] = q.Id,
                ["header"] = q.Header,
                ["text"] = q.Text,
                ["multi"] = q.MultiSelect,
                ["allowOther"] = q.AllowOther,
                ["secret"] = q.IsSecret,
                ["options"] = new JArray(q.Options.Select(o => new JObject
                {
                    ["label"] = o.Label,
                    ["description"] = o.Description,
                    ["preview"] = o.Preview,
                })),
            }));
            result["answers"] = JObject.FromObject(item.Answers);
            result["decision"] = item.Decision;
            result["answerAsMessage"] = item.AnswerAsMessage;
        }

        if (item.Kind == AgentItemKind.Plan)
            result["plan"] = new JArray(item.PlanSteps.Select(s => new JObject { ["text"] = s.Text, ["status"] = s.Status }));

        if (item.Attachments.Count > 0)
            result["attachments"] = new JArray(item.Attachments.Select(RenderAttachment));

        if (item.MediaPaths.Count > 0)
        {
            result["media"] = new JArray(item.MediaPaths.Select(path => new JObject
            {
                ["url"] = urls.MediaUrl(path),
                ["kind"] = AgentAttachmentRules.IsPlayableVideo(path) ? "video" : "image",
                ["path"] = path,
                ["name"] = Path.GetFileName(path),
            }));
        }

        return result;
    }
}
