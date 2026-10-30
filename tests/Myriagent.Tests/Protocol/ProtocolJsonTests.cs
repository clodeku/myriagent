using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Json;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json;

namespace Myriagent.Tests.Protocol;

/// <summary>
/// 桌面與伺服器共用的 Newtonsoft 設定（Protocol 的 ProtocolJson）：與存檔（JsonFile）是同一份；
/// 原始項目送出再收回，除了不存檔的欄位都還原（伺服器據此重新渲染）。
/// 不認得的 NoticeKind 名稱讀成 null（設計 8.8：回滾後的舊版讀新版寫的 items.json），其他列舉維持嚴格
/// </summary>
public class ProtocolJsonTests
{
    [Fact]
    public void 設定與存檔相同_JsonFile用的就是這一份()
    {
        ProtocolJson.Settings.Formatting.Should().Be(Formatting.Indented);
        ProtocolJson.Settings.NullValueHandling.Should().Be(NullValueHandling.Ignore);
        ProtocolJson.Settings.DateTimeZoneHandling.Should().Be(DateTimeZoneHandling.Local);
        JsonFile.Settings.Should().BeSameAs(ProtocolJson.Settings);
    }

    [Fact]
    public void 原始項目送出再收回_除了不存檔的欄位都還原()
    {
        var created = new DateTime(2026, 7, 15, 1, 0, 0, DateTimeKind.Utc);
        var item = new AgentChatItem
        {
            Id = "i1",
            Kind = AgentItemKind.Approval,
            Status = AgentItemStatus.Waiting,
            Text = "說明",
            ToolName = "修改檔案",
            ToolKind = "edit",
            Title = @"C:\repo\a.cs",
            Detail = "第 1 行",
            FilePath = @"C:\repo\a.cs",
            Diff = DiffRules.FromEdit("a", "b"),
            Attachments = { new AgentAttachment { Id = "x1", FileName = "a.png", StoredPath = @"C:\att\a.png", Kind = AgentAttachmentKind.Image, SizeBytes = 10, ModelImagePaths = { @"C:\att\a.png" } } },
            Questions = { new AgentQuestion { Id = "q1", Header = "標題", Text = "題目", AllowOther = false, Options = { new AgentQuestionOption { Label = "甲", Description = "說明" } } } },
            Answers = { ["q1"] = new List<string> { "甲" } },
            Decision = "allow",
            CanAllowForSession = false,
            AllowSessionLabel = "本對話都允許",
            AnswerAsMessage = true,
            MediaPaths = { @"C:\out\a.mp4" },
            PlanSteps = { new AgentPlanStep { Text = "步驟", Status = "inProgress" } },
            CreatedAt = created,
            UpdatedAt = created,
            ProcessInstance = "proc-1",
            NoticeKind = AgentNoticeKind.CliUpdate,
            RequestKey = "req-1",
            Seq = 42,
        };

        var json = JsonConvert.SerializeObject(new[] { item }, ProtocolJson.Settings);
        var received = JsonConvert.DeserializeObject<List<AgentChatItem>>(json, ProtocolJson.Settings)!.Single();

        received.Should().BeEquivalentTo(item, options => options
            .Excluding(i => i.Seq)
            .Excluding(i => i.RequestKey)
            .Using<DateTime>(c => c.Subject.ToUniversalTime().Should().Be(c.Expectation.ToUniversalTime()))
            .WhenTypeIs<DateTime>());
        received.Seq.Should().Be(0, "建立順序不存檔也不送出，由接收端依順序配");
        received.RequestKey.Should().BeNull("等待中的請求鍵只在程序存活期間有效");
        json.Should().NotContain("RequestKey").And.NotContain("\"Seq\"");
    }

    [Fact]
    public void 不認得的NoticeKind讀成null_其他欄位照樣還原()
    {
        var items = JsonConvert.DeserializeObject<List<AgentChatItem>>("""[{"Kind":"Notice","Text":"x","NoticeKind":"SomeFutureKind"}]""", ProtocolJson.Settings)!;

        items.Should().ContainSingle();
        items[0].Kind.Should().Be(AgentItemKind.Notice);
        items[0].Text.Should().Be("x");
        items[0].NoticeKind.Should().BeNull("之後的版本新增的提示種類（設計 8.8：回滾會讓舊版讀新版寫的檔）當成一般提示");
    }

    [Fact]
    public void 認得的NoticeKind名稱與數字照原值還原()
    {
        static AgentNoticeKind? Read(string noticeKind)
            => JsonConvert.DeserializeObject<List<AgentChatItem>>("[{\"Kind\":\"Notice\",\"NoticeKind\":" + noticeKind + "}]", ProtocolJson.Settings)!.Single().NoticeKind;

        Read("\"AppUpdate\"").Should().Be(AgentNoticeKind.AppUpdate);
        Read("\"CliUpdate\"").Should().Be(AgentNoticeKind.CliUpdate);
        Read("\"Handoff\"").Should().Be(AgentNoticeKind.Handoff, "第 5 階段的新值（U1 起的舊版讀成 null）");
        Read("\"Fork\"").Should().Be(AgentNoticeKind.Fork);
        Enum.IsDefined((AgentNoticeKind)5).Should().BeFalse("5 留給黃金語料的「沒定義的值」");
        Read("5").Should().Be((AgentNoticeKind)5, "以數字寫入的未定義值照原值還原（黃金語料 undefined-enums 的往返）");
        Read("null").Should().BeNull();
    }

    [Fact]
    public void items檔含不認得的NoticeKind_照樣讀得回來_不會被當成損毀備份()
    {
        using var temp = new TempDirectory("protocoljson");
        var path = temp.WriteFile("items.json",
            """[{"Id":"i1","Kind":"Notice","Text":"新版的提示","NoticeKind":"SomeFutureKind"},{"Id":"i2","Kind":"User","Text":"hi"}]""");

        var (status, value, backup) = JsonFile.TryRead<List<AgentChatItem>>(path);

        status.Should().Be(JsonReadStatus.Ok);
        backup.Should().BeNull();
        var items = value!;
        items.Select(i => i.Id).Should().Equal("i1", "i2");
        items[0].NoticeKind.Should().BeNull();
        File.Exists(path).Should().BeTrue("原檔沒有被改名成 .corrupt-*");
        Directory.GetFiles(temp.Root, "*.corrupt-*").Should().BeEmpty();
    }

    private sealed class NonNullableKindHolder
    {
        [JsonConverter(typeof(UnknownEnumNameAsNullConverter))]
        public AgentNoticeKind Kind { get; set; }
    }

    [Fact]
    public void 容忍未知名稱只限可為null的屬性_寫入照舊存名稱()
    {
        var act = () => JsonConvert.DeserializeObject<NonNullableKindHolder>("""{"Kind":"SomeFutureKind"}""", ProtocolJson.Settings);

        act.Should().Throw<JsonSerializationException>("不可為 null 的屬性沒有「沒有值」可用，照 StringEnumConverter 丟例外");
        JsonConvert.DeserializeObject<NonNullableKindHolder>("""{"Kind":"AppUpdate"}""", ProtocolJson.Settings)!.Kind.Should().Be(AgentNoticeKind.AppUpdate);
        JsonConvert.SerializeObject(new AgentChatItem { Kind = AgentItemKind.Notice, NoticeKind = AgentNoticeKind.AppUpdate }, ProtocolJson.Settings)
            .Should().Contain("\"NoticeKind\": \"AppUpdate\"", "寫入沿用 StringEnumConverter：存名稱");
    }

    [Fact]
    public void 其他列舉仍然嚴格_不認得的項目種類照樣丟例外()
    {
        var act = () => JsonConvert.DeserializeObject<List<AgentChatItem>>("""[{"Kind":"SomeFutureKind"}]""", ProtocolJson.Settings);

        act.Should().Throw<JsonSerializationException>("只有 NoticeKind 容忍未知值：項目種類、狀態讀錯會讓流程判斷錯，新增這類值要另外設計相容（設計 8.8）");
    }
}
