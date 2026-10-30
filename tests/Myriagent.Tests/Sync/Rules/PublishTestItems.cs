using System.Reflection;
using Myriagent.Agents;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// 發布規則測試共用的項目樣本：每個屬性都不是預設值（清單與字典都有元素），反射測試靠它逐一改屬性；
/// 新增的屬性型別要在 <see cref="Changed"/> 補上，否則反射測試直接失敗（新屬性先讓測試失敗）
/// </summary>
internal static class PublishTestItems
{
    public static AgentChatItem Full() => new()
    {
        Id = "0f1e2d3c4b5a69788796a5b4c3d2e1f0",
        Kind = AgentItemKind.Question,
        Status = AgentItemStatus.Waiting,
        Text = new string('文', 3) + "內容",
        ToolName = "執行指令",
        ToolKind = "bash",
        Title = "標題",
        Detail = "細節",
        Output = "輸出",
        Diff = [new AgentDiffLine("add", "+ 一行"), new AgentDiffLine("ctx", "  另一行")],
        FilePath = @"C:\work\a.txt",
        Attachments =
        [
            new AgentAttachment
            {
                Id = "att1", FileName = "a.png", StoredPath = @"C:\data\a.png", Kind = AgentAttachmentKind.Image, SizeBytes = 12,
                ModelImagePaths = [@"C:\data\a.thumb.png"], FrameSeconds = [0.5], DurationSeconds = 1.5, Waveform = "0369",
            },
        ],
        Questions =
        [
            new AgentQuestion
            {
                Id = "q1", Header = "選擇", Text = "要哪一個？", MultiSelect = true, AllowOther = false, IsSecret = true,
                Options = [new AgentQuestionOption { Label = "甲", Description = "說明", Preview = "預覽" }],
            },
        ],
        Answers = new Dictionary<string, List<string>> { ["q1"] = ["甲", "乙"] },
        Decision = "answered",
        CanAllowForSession = false,
        AllowSessionLabel = "本對話都允許",
        AnswerAsMessage = true,
        MediaPaths = [@"C:\out\m.png"],
        PlanSteps = [new AgentPlanStep { Text = "步驟", Status = "inProgress" }],
        CreatedAt = new DateTime(2026, 10, 19, 9, 0, 0, DateTimeKind.Local),
        UpdatedAt = new DateTime(2026, 10, 19, 9, 5, 0, DateTimeKind.Local),
        ProcessInstance = "proc-1",
        NoticeKind = AgentNoticeKind.AppUpdate,
        Source = AgentItemSource.Remote,
        Author = "小明",
        ResolvedBy = "小華",
        CliUuid = "8531da8c-76f3-449f-b729-fa9121ac0c94",
        Ord = 7,
        RequestDigest = "rd1:abc",
        Risk = AgentRemoteRisk.High,
        MediaRefs = [new AgentMediaRef { Ref = "0123456789ABCDEFGHJKMNPQRS", MediaPath = @"C:\out\m.png" }],
        KeptMedia = new Dictionary<string, string> { [@"C:\out\m.png"] = @"attachments\.kept\0123456789abcdef\m.png" },
        RequestKey = "request-1",
        Seq = 42,
    };

    /// <summary>AgentChatItem 可寫的公開屬性（宣告順序）</summary>
    public static IReadOnlyList<PropertyInfo> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .OrderBy(p => p.MetadataToken)
            .ToList();

    /// <summary>和 <paramref name="value"/> 不同的一個值（清單與字典是多一個元素的新實例）</summary>
    public static object? Changed(object? value, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string))
            return (string?)value + "x";
        if (underlying == typeof(bool))
            return !(bool)value!;
        if (underlying == typeof(long))
            return ((long?)value ?? 0) + 1;
        if (underlying == typeof(int))
            return ((int?)value ?? 0) + 1;
        if (underlying == typeof(double))
            return ((double?)value ?? 0) + 1;
        if (underlying == typeof(DateTime))
            return ((DateTime)value!).AddSeconds(1);
        if (underlying.IsEnum)
        {
            // 只有一個值的可為 null 列舉（AgentItemSource）：改成 null 也是不同的值
            var values = Enum.GetValues(underlying).Cast<object>().ToList();
            var other = values.FirstOrDefault(v => !v.Equals(value));
            return other ?? (Nullable.GetUnderlyingType(type) != null && value != null ? null : throw new NotSupportedException($"{type} 沒有其他值"));
        }

        if (underlying == typeof(Dictionary<string, List<string>>))
        {
            var copy = new Dictionary<string, List<string>>((Dictionary<string, List<string>>)value!) { ["added"] = ["v"] };
            return copy;
        }

        if (underlying == typeof(Dictionary<string, string>))
            return new Dictionary<string, string>((Dictionary<string, string>?)value ?? []) { ["added"] = "v" };

        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(List<>))
        {
            var element = underlying.GetGenericArguments()[0];
            var list = (System.Collections.IList)Activator.CreateInstance(underlying)!;
            foreach (var item in (System.Collections.IEnumerable)value!)
                list.Add(item);
            list.Add(Sample(element));
            return list;
        }

        throw new NotSupportedException($"新的屬性型別 {type}：在 PublishTestItems.Changed 補上");
    }

    private static object Sample(Type element)
    {
        if (element == typeof(string))
            return "added";
        if (element == typeof(double))
            return 9.5;
        if (element == typeof(AgentDiffLine))
            return new AgentDiffLine("del", "- 刪掉");
        if (element == typeof(AgentAttachment))
            return new AgentAttachment { Id = "att2", FileName = "b.pdf", Kind = AgentAttachmentKind.Document };
        if (element == typeof(AgentQuestion))
            return new AgentQuestion { Id = "q2", Text = "第二題" };
        if (element == typeof(AgentQuestionOption))
            return new AgentQuestionOption { Label = "丙" };
        if (element == typeof(AgentPlanStep))
            return new AgentPlanStep { Text = "下一步" };
        if (element == typeof(AgentMediaRef))
            return new AgentMediaRef { Ref = "ZYXWVTSRQPNMKJHGFEDCBA9876", AttachmentId = "att1" };
        throw new NotSupportedException($"新的清單元素型別 {element}：在 PublishTestItems.Sample 補上");
    }
}
