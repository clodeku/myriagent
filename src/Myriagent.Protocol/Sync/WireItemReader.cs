using Myriagent.Agents;

namespace Myriagent.Sync;

/// <summary>
/// 線上的項目（<see cref="WireItem"/>，<see cref="ConversationWire.TryReadItem"/> 讀回的）→ 桌面的 <see cref="AgentChatItem"/>（WP4.3 計畫 D28：
/// 其他電腦的對話以唯讀複本顯示；伺服器的渲染也能用）。是 <see cref="ConversationWire.TryItem"/> 的反向：線上的欄位照原樣轉回，
/// 只在本機有意義的欄位一律是空的——<see cref="AgentChatItem.ProcessInstance"/>、<see cref="AgentChatItem.RequestKey"/> 是 null、
/// <see cref="AgentChatItem.Seq"/> 是 0（收下的一方自己編號）、附件沒有 <see cref="AgentAttachment.StoredPath"/>、<see cref="AgentAttachment.ModelImagePaths"/>
/// 與 <see cref="AgentAttachment.FrameSeconds"/>、<see cref="AgentChatItem.MediaPaths"/> 是空的。
/// <list type="bullet">
/// <item>附件的參照放回 <see cref="AgentChatItem.MediaRefs"/>（只有 <see cref="AgentMediaRef.AttachmentId"/>）；影片的畫面張數（<see cref="WireAttachment.Frames"/>）
/// 是本機的畫面檔數，轉不回來。</item>
/// <item>代理產出的媒體（<see cref="WireItem.Media"/>）沒有本機路徑可以指，不轉：原始 payload 留在複本資料，WP4.7 經參照取。</item>
/// <item><see cref="AgentChatItem.FilePath"/> 只是顯示用的文字（擁有者電腦上的路徑，這台不一定有）。</item>
/// <item>不認得的列舉值（新版的種類、狀態、提示種類、風險）當成沒有值：<see cref="WireItem.Kind"/>、<see cref="WireItem.Status"/> 不認得時整筆回 null
/// （不能顯示成別的種類）；<see cref="WireItem.NoticeKind"/>、<see cref="WireItem.Risk"/> 是 null；附件的種類當成 Document（同 TryItem）。
/// 名稱一律逐字比對（數字字串、大小寫不同、逗號分隔的都不算）。</item>
/// <item>同一道題有兩份答案時留第一份。</item>
/// </list>
/// 純函式、不碰檔案系統
/// </summary>
internal static class WireItemReader
{
    /// <summary>線上的項目 → 桌面的項目；種類或狀態不認得回 null</summary>
    public static AgentChatItem? ToChatItem(WireItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!TryName<AgentItemKind>(item.Kind, out var kind) || !TryName<AgentItemStatus>(item.Status, out var status))
            return null;

        var attachments = new List<AgentAttachment>();
        var refs = new List<AgentMediaRef>();
        foreach (var a in item.Attachments ?? [])
        {
            if (a is null)
                continue;
            attachments.Add(new AgentAttachment
            {
                Id = a.Id ?? string.Empty,
                FileName = a.Name ?? string.Empty,
                StoredPath = string.Empty,
                Kind = TryName<AgentAttachmentKind>(a.Kind, out var attachmentKind) ? attachmentKind : AgentAttachmentKind.Document,
                SizeBytes = Math.Max(0, a.Size),
                DurationSeconds = a.Duration,
            });
            if (a.Ref is { } mediaRef)
                refs.Add(new AgentMediaRef { Ref = mediaRef, AttachmentId = a.Id });
        }

        var answers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var answer in item.Answers ?? [])
        {
            if (answer?.Id is { } id && !answers.ContainsKey(id))
                answers[id] = (answer.Values ?? []).Where(v => v != null).ToList();
        }

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
            Diff = item.Diff?.Where(d => d != null).Select(d => new AgentDiffLine(d.T, d.S)).ToList(),
            FilePath = item.FilePath,
            Attachments = attachments,
            Questions = (item.Questions ?? []).Where(q => q != null).Select(Question).ToList(),
            Answers = answers,
            Decision = item.Decision,
            CanAllowForSession = item.CanAllowForSession,
            AllowSessionLabel = item.AllowSessionLabel,
            AnswerAsMessage = item.AnswerAsMessage,
            MediaPaths = [],
            PlanSteps = (item.Plan ?? []).Where(s => s != null).Select(s => new AgentPlanStep { Text = s.Text ?? string.Empty, Status = s.Status ?? "pending" }).ToList(),
            CreatedAt = FromUnixMs(item.CreatedAt),
            UpdatedAt = FromUnixMs(item.UpdatedAt),
            ProcessInstance = null,
            NoticeKind = TryName<AgentNoticeKind>(item.NoticeKind, out var notice) ? notice : null,
            Ord = item.Ord,
            RequestDigest = item.RequestDigest,
            Risk = TryName<AgentRemoteRisk>(item.Risk, out var risk) ? risk : null,
            Author = CollaboratorRules.CleanAuthor(item.Author),
            ResolvedBy = CollaboratorRules.CleanAuthor(item.ResolvedBy),
            MediaRefs = refs.Count > 0 ? refs : null,
            RequestKey = null,
            Seq = 0,
        };
    }

    /// <summary>
    /// 線上的時間（Unix 毫秒）→ 本機時間；null 或超出範圍是 <see cref="DateTime.MinValue"/>（<see cref="Rendering.ProtocolValues.ToUnixMs"/> 再轉回 null，來回不變）
    /// </summary>
    public static DateTime FromUnixMs(long? ms)
    {
        if (ms is not { } value)
            return DateTime.MinValue;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>列舉的名稱逐字比對（數字字串、大小寫不同、逗號分隔、前後空白都不算）</summary>
    public static bool TryName<T>(string? value, out T result)
        where T : struct, Enum
    {
        result = default;
        if (string.IsNullOrEmpty(value) || !Enum.TryParse(value, ignoreCase: false, out T parsed) || !Enum.IsDefined(parsed)
            || !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
            return false;
        result = parsed;
        return true;
    }

    private static AgentQuestion Question(WireQuestion q) => new()
    {
        Id = q.Id ?? string.Empty,
        Header = q.Header,
        Text = q.Text ?? string.Empty,
        MultiSelect = q.MultiSelect,
        AllowOther = q.AllowOther,
        IsSecret = q.IsSecret,
        Options = (q.Options ?? []).Where(o => o != null).Select(o => new AgentQuestionOption { Label = o.Label ?? string.Empty, Description = o.Description, Preview = o.Preview }).ToList(),
    };
}
