using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>
/// 給同步引擎的淺快照（WP4.3 計畫 D1；設計 2.3）：中樞在 UI 執行緒複製項目與中繼資料，引擎在自己的執行緒投影、遮蔽、序列化。
/// 容器（清單、字典）與可變的元素（題目與選項、待辦步驟、差異行、附件、媒體參照、答案的內層清單）一律是新的實例，
/// UI 之後改原本的物件不會影響快照；字串不可變、照原參照共用（不複製文字，UI 執行緒的成本與內容長度無關）。
/// 清單裡的 null 照樣是 null（存檔讀回的病態內容由投影處理）
/// </summary>
internal static class ChatItemCopyRules
{
    public static AgentChatItem Copy(AgentChatItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new AgentChatItem
        {
            Id = item.Id,
            Kind = item.Kind,
            Status = item.Status,
            Text = item.Text,
            ToolName = item.ToolName,
            ToolKind = item.ToolKind,
            Title = item.Title,
            Detail = item.Detail,
            Output = item.Output,
            Diff = item.Diff?.Select(line => line is null ? null! : line with { }).ToList(),
            FilePath = item.FilePath,
            Attachments = CopyList(item.Attachments, CopyAttachment)!,
            Questions = CopyList(item.Questions, CopyQuestion)!,
            Answers = CopyAnswers(item.Answers)!,
            Decision = item.Decision,
            CanAllowForSession = item.CanAllowForSession,
            AllowSessionLabel = item.AllowSessionLabel,
            AnswerAsMessage = item.AnswerAsMessage,
            MediaPaths = item.MediaPaths is null ? null! : [.. item.MediaPaths],
            PlanSteps = CopyList(item.PlanSteps, step => new AgentPlanStep { Text = step.Text, Status = step.Status })!,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            ProcessInstance = item.ProcessInstance,
            NoticeKind = item.NoticeKind,
            Source = item.Source,
            Author = item.Author,
            ResolvedBy = item.ResolvedBy,
            CliUuid = item.CliUuid,
            Ord = item.Ord,
            RequestDigest = item.RequestDigest,
            Risk = item.Risk,
            MediaRefs = CopyList(item.MediaRefs, media => new AgentMediaRef { Ref = media.Ref, AttachmentId = media.AttachmentId, MediaPath = media.MediaPath }),
            KeptMedia = item.KeptMedia is null ? null : new Dictionary<string, string>(item.KeptMedia, item.KeptMedia.Comparer),
            RequestKey = item.RequestKey,
            Seq = item.Seq,
        };
    }

    /// <summary>中繼資料的不可變值共用；技能集合與帳戶委派的選取清單另外複製，避免之後的介面操作改到快照。</summary>
    public static AgentConversation CopyMeta(AgentConversation meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return new AgentConversation
        {
            Id = meta.Id,
            Title = meta.Title,
            AccountId = meta.AccountId,
            Provider = meta.Provider,
            SessionId = meta.SessionId,
            SessionStarted = meta.SessionStarted,
            SessionAccountId = meta.SessionAccountId,
            Model = meta.Model,
            Effort = meta.Effort,
            Agent = meta.Agent,
            OutputStyle = meta.OutputStyle,
            CodexServiceTier = meta.CodexServiceTier,
            CodexMode = meta.CodexMode,
            CodexSkills = meta.CodexSkills?.ToList(),
            ResumeAt = meta.ResumeAt,
            QuotaWait = meta.QuotaWait,
            PermissionMode = meta.PermissionMode,
            WorkingDirectory = meta.WorkingDirectory,
            LastCliVersion = meta.LastCliVersion,
            RemoteControlSessionId = meta.RemoteControlSessionId,
            DelegatedAccountIds = meta.DelegatedAccountIds?.ToList(),
            ParentConversationId = meta.ParentConversationId,
            Ephemeral = meta.Ephemeral,
            CreatedAt = meta.CreatedAt,
            UpdatedAt = meta.UpdatedAt,
            SyncPolicy = meta.SyncPolicy,
            OwnerDeviceId = meta.OwnerDeviceId,
            LeaseEpoch = meta.LeaseEpoch,
            NextOrd = meta.NextOrd,
            // 第 5 階段（WP5.1）：PathRef 是不可變的記錄，共用同一個
            WorkingDirectoryRef = meta.WorkingDirectoryRef,
            ForkedFrom = meta.ForkedFrom,
            ForkedFromTitle = meta.ForkedFromTitle,
            ForkPointSeq = meta.ForkPointSeq,
            ForkSourceTranscript = meta.ForkSourceTranscript,
            PendingHandoffNote = meta.PendingHandoffNote,
            // 不可變的記錄，共用同一個
            QuotaHome = meta.QuotaHome,
            DeferredAccount = meta.DeferredAccount,
            ColdResume = meta.ColdResume,
        };
    }

    private static AgentAttachment CopyAttachment(AgentAttachment attachment) => new()
    {
        Id = attachment.Id,
        FileName = attachment.FileName,
        StoredPath = attachment.StoredPath,
        Kind = attachment.Kind,
        SizeBytes = attachment.SizeBytes,
        ModelImagePaths = attachment.ModelImagePaths is null ? null! : [.. attachment.ModelImagePaths],
        FrameSeconds = attachment.FrameSeconds is null ? null! : [.. attachment.FrameSeconds],
        DurationSeconds = attachment.DurationSeconds,
        Waveform = attachment.Waveform,
    };

    private static AgentQuestion CopyQuestion(AgentQuestion question) => new()
    {
        Id = question.Id,
        Header = question.Header,
        Text = question.Text,
        Options = CopyList(question.Options, option => new AgentQuestionOption { Label = option.Label, Description = option.Description, Preview = option.Preview })!,
        MultiSelect = question.MultiSelect,
        AllowOther = question.AllowOther,
        IsSecret = question.IsSecret,
    };

    private static Dictionary<string, List<string>>? CopyAnswers(Dictionary<string, List<string>>? answers)
    {
        if (answers is null)
            return null;
        var copy = new Dictionary<string, List<string>>(answers.Count, answers.Comparer);
        foreach (var (key, values) in answers)
            copy[key] = values is null ? null! : [.. values];
        return copy;
    }

    /// <summary>清單與裡面的每個元素都複製；清單是 null 回 null、元素是 null 照樣是 null</summary>
    private static List<T>? CopyList<T>(List<T>? source, Func<T, T> copy) where T : class
    {
        if (source is null)
            return null;
        var result = new List<T>(source.Count);
        foreach (var element in source)
            result.Add(element is null ? null! : copy(element));
        return result;
    }
}
