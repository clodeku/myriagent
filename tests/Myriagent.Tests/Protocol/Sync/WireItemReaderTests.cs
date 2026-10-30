using System.Reflection;
using Myriagent.Agents;
using Myriagent.Rendering;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 線上的項目 → 桌面的項目（WP4.3 計畫步 14，D28）：TryItem 的反向。線上欄位一個不少地轉回、本機欄位一律是空的、
/// 不認得的列舉值當成沒有值（種類與狀態不認得時整筆不轉）
/// </summary>
public sealed class WireItemReaderTests
{
    private const string AttachmentRef = "01J9Q0000000000000000000AB";

    private const string MediaRef = "01J9Q0000000000000000000CD";

    private static readonly DateTime Created = new(2026, 7, 15, 9, 30, 0, 123, DateTimeKind.Local);

    private static readonly JsonSerializer Serializer = JsonSerializer.Create(SyncJson.Settings);

    /// <summary>線上一定保留的欄位（代理產出的媒體 Media 沒有本機路徑可以指，轉不回來：明確列在外面）</summary>
    private static readonly string[] Preserved =
    [
        "Id", "Ord", "Ver", "Kind", "Status", "CreatedAt", "UpdatedAt", "Text", "ToolName", "ToolKind", "Title", "Detail", "Output", "Diff", "FilePath",
        "Attachments", "Questions", "Answers", "Decision", "CanAllowForSession", "AllowSessionLabel", "AnswerAsMessage", "Plan", "NoticeKind",
        "RequestDigest", "Risk", "Author", "ResolvedBy",
    ];

    private static readonly string[] NotPreserved = ["Media"];

    /// <summary>每個線上欄位都不是預設值的項目（另外帶著本機欄位）</summary>
    private static AgentChatItem Full() => new()
    {
        Id = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8",
        Ord = 7,
        Kind = AgentItemKind.Question,
        Status = AgentItemStatus.Waiting,
        Text = "內文",
        ToolName = "詢問",
        ToolKind = "question",
        Title = "標題",
        Detail = "說明",
        Output = "輸出",
        Diff = [new AgentDiffLine("add", "+a"), new AgentDiffLine("del", "-b")],
        FilePath = @"C:\repo\a.cs",
        Attachments =
        {
            new AgentAttachment { Id = "x1", FileName = "截圖.png", StoredPath = @"C:\a\截圖.png", Kind = AgentAttachmentKind.Image, SizeBytes = 2048, DurationSeconds = 1.5, ModelImagePaths = { @"C:\a\截圖.thumb.png" } },
            new AgentAttachment { Id = "x2", FileName = "說明.pdf", StoredPath = @"C:\a\說明.pdf", Kind = AgentAttachmentKind.Document, SizeBytes = 9 },
        },
        Questions =
        {
            new AgentQuestion
            {
                Id = "q", Header = "頭", Text = "問？", MultiSelect = true, AllowOther = false, IsSecret = false,
                Options = { new AgentQuestionOption { Label = "A", Description = "選 A", Preview = "預覽" }, new AgentQuestionOption { Label = "B" } },
            },
        },
        Answers = { ["q"] = ["A", "B"] },
        Decision = "answered",
        CanAllowForSession = false,
        AllowSessionLabel = "本對話都允許",
        AnswerAsMessage = true,
        MediaPaths = { @"C:\repo\out\chart.png" },
        PlanSteps = { new AgentPlanStep { Text = "第一步", Status = "inProgress" }, new AgentPlanStep { Text = "第二步" } },
        CreatedAt = Created,
        UpdatedAt = Created.AddMinutes(3),
        NoticeKind = AgentNoticeKind.CliUpdate,
        RequestDigest = "rd1:" + new string('a', 64),
        Risk = AgentRemoteRisk.High,
        Author = "小明",
        ResolvedBy = "小華",
        MediaRefs =
        [
            new AgentMediaRef { Ref = AttachmentRef, AttachmentId = "x1" },
            new AgentMediaRef { Ref = MediaRef, MediaPath = @"C:\repo\out\chart.png" },
        ],
        ProcessInstance = "p-1",
        RequestKey = "req-1",
        Seq = 42,
    };

    private static WireItem Read(AgentChatItem item, long ver)
    {
        // 照線上的路徑：投影、序列化、驗證，收的一方解析文字再讀回
        ConversationWire.TryItem(item, ver, out var wire).Should().BeTrue();
        var json = ConversationWire.Serialize(wire!);
        ConversationEventRules.Validate(ConversationEventKinds.Item, json).Should().BeNull();
        ConversationEventRules.TryParseStrict(json, out var token).Should().BeTrue();
        ConversationWire.TryReadItem((JObject)token!, _ => null, out var read).Should().BeTrue();
        return read!;
    }

    private static JToken Token(object? value) => value is null ? JValue.CreateNull() : JToken.FromObject(value, Serializer);

    [Fact]
    public void TryItem到TryReadItem到ToChatItem_線上欄位都保留()
    {
        typeof(WireItem).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)
            .Should().BeEquivalentTo(Preserved.Concat(NotPreserved), "WireItem 新增的欄位要先決定轉不轉得回來（WireItemReader），再更新這張表");

        var first = Read(Full(), ver: 3);
        var defaults = new WireItem("x", 1, 1, "User", "Done", null, null);
        foreach (var property in typeof(WireItem).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            JToken.DeepEquals(Token(property.GetValue(first)), Token(property.GetValue(defaults)))
                .Should().BeFalse($"{property.Name}：測試的項目要讓每個線上欄位都不是預設值，才驗得到它有轉回來");
        }

        var chat = WireItemReader.ToChatItem(first);
        chat.Should().NotBeNull();
        var second = Read(chat!, ver: 3);

        foreach (var name in Preserved)
        {
            var property = typeof(WireItem).GetProperty(name)!;
            JToken.DeepEquals(Token(property.GetValue(second)), Token(property.GetValue(first)))
                .Should().BeTrue($"{name}：線上的欄位轉回桌面再投影一次要一樣（{Token(property.GetValue(first))} → {Token(property.GetValue(second))}）");
        }

        second.Media.Should().BeNull("代理產出的媒體沒有本機路徑可以指（WP4.7 經參照取）");
        chat!.CreatedAt.Should().Be(Created);
        chat.Ord.Should().Be(7);
        chat.Answers["q"].Should().Equal("A", "B");
    }

    [Fact]
    public void 本機欄位是null()
    {
        var chat = WireItemReader.ToChatItem(Read(Full(), ver: 1))!;

        chat.ProcessInstance.Should().BeNull();
        chat.RequestKey.Should().BeNull();
        chat.Seq.Should().Be(0, "收下的一方自己編號");
        chat.MediaPaths.Should().BeEmpty();
        chat.Attachments.Should().HaveCount(2).And.OnlyContain(a => a.StoredPath == string.Empty && a.ModelImagePaths.Count == 0 && a.FrameSeconds.Count == 0);
        chat.MediaRefs.Should().ContainSingle("只有附件的參照轉得回來（媒體沒有本機路徑）")
            .Which.Should().Match<AgentMediaRef>(r => r.Ref == AttachmentRef && r.AttachmentId == "x1" && r.MediaPath == null);
        chat.FilePath.Should().Be(@"C:\repo\a.cs", "只是顯示用的文字（擁有者電腦上的路徑）");

        // 影片的畫面張數是擁有者電腦上的畫面檔數：轉不回來
        var video = new AgentChatItem
        {
            Id = "v1", Ord = 1, Kind = AgentItemKind.User, Text = "影片",
            Attachments = { new AgentAttachment { Id = "x3", FileName = "錄影.mp4", Kind = AgentAttachmentKind.Video, SizeBytes = 5, ModelImagePaths = { @"C:\a\1.jpg", @"C:\a\2.jpg" }, DurationSeconds = 35 } },
        };
        var read = Read(video, 1);
        read.Attachments![0].Frames.Should().Be(2);
        var attachment = WireItemReader.ToChatItem(read)!.Attachments.Single();
        attachment.ModelImagePaths.Should().BeEmpty();
        attachment.DurationSeconds.Should().Be(35);

        // 沒有時間的項目：轉成 MinValue，再投影回 null（來回不變）
        var timeless = WireItemReader.ToChatItem(new WireItem("t1", 1, 1, "User", "Done", null, null))!;
        timeless.CreatedAt.Should().Be(DateTime.MinValue);
        ProtocolValues.ToUnixMs(timeless.CreatedAt).Should().BeNull();
        WireItemReader.FromUnixMs(long.MaxValue).Should().Be(DateTime.MinValue, "超出範圍不丟例外");
    }

    [Fact]
    public void 未知列舉值是null()
    {
        WireItem Item(string kind = "Assistant", string status = "Done", string? notice = null, string? risk = null, string attachmentKind = "Image")
            => new("u1", 1, 1, kind, status, null, null, Text: "x", NoticeKind: notice, Risk: risk,
                Attachments: [new WireAttachment("a1", "f.bin", attachmentKind, 1, null, null, null)]);

        WireItemReader.ToChatItem(Item(kind: "Hologram")).Should().BeNull("不認得的種類不能顯示成別的種類");
        WireItemReader.ToChatItem(Item(status: "Paused")).Should().BeNull();
        WireItemReader.ToChatItem(Item(kind: "1")).Should().BeNull("數字字串不是名稱");
        WireItemReader.ToChatItem(Item(kind: "assistant")).Should().BeNull("大小寫不同");
        WireItemReader.ToChatItem(Item(kind: "User, Tool")).Should().BeNull("逗號分隔");
        WireItemReader.ToChatItem(Item(kind: " User")).Should().BeNull("前後空白");
        WireItemReader.ToChatItem(Item(kind: "")).Should().BeNull();

        var future = WireItemReader.ToChatItem(Item(notice: "FutureKind", risk: "Extreme", attachmentKind: "Hologram"))!;
        future.Should().NotBeNull();
        future.Kind.Should().Be(AgentItemKind.Assistant);
        future.NoticeKind.Should().BeNull("新版的提示種類當成沒有值");
        future.Risk.Should().BeNull();
        future.Attachments.Single().Kind.Should().Be(AgentAttachmentKind.Document, "同 TryItem");

        var known = WireItemReader.ToChatItem(Item(notice: "AppUpdate", risk: "Normal"))!;
        known.NoticeKind.Should().Be(AgentNoticeKind.AppUpdate);
        known.Risk.Should().Be(AgentRemoteRisk.Normal);

        // 同一道題有兩份答案：留第一份
        var answers = WireItemReader.ToChatItem(new WireItem("q1", 1, 1, "Question", "Done", null, null,
            Answers: [new WireAnswer("q", ["第一份"]), new WireAnswer("q", ["第二份"])]))!;
        answers.Answers.Should().ContainSingle().Which.Value.Should().Equal("第一份");
    }
}
