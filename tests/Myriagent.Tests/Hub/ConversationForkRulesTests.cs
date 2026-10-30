using System.Text;
using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>延伸成新對話的純規則：標題、提示、前文節錄、複製項目（新 id、清掉只屬於原對話的欄位、收尾、附件路徑）、逐字稿副本的截尾</summary>
public class ConversationForkRulesTests
{
    private const string From = @"C:\data\conversations\aaa\attachments";
    private const string To = @"C:\data\conversations\bbb\attachments";

    [Theory]
    [InlineData("修正登入流程", "修正登入流程（延伸）")]
    [InlineData("修正登入流程（延伸）", "修正登入流程（延伸 2）")]
    [InlineData("修正登入流程（延伸 2）", "修正登入流程（延伸 3）")]
    [InlineData("  ", "新對話（延伸）")]
    [InlineData(null, "新對話（延伸）")]
    public void Title_加上延伸_再延伸改成下一個編號(string? source, string expected)
        => ConversationForkRules.Title(source).Should().Be(expected);

    [Fact]
    public void Notice_說明前情與原對話是否執行中()
    {
        ConversationForkRules.Notice("修正登入流程", nativeContext: true, sourceRunning: false)
            .Should().StartWith("從「修正登入流程」延伸出來的新對話").And.Contain("完整紀錄").And.NotContain("還在執行");
        ConversationForkRules.Notice("修正登入流程", nativeContext: false, sourceRunning: true)
            .Should().Contain("前文節錄").And.Contain("延伸時原對話還在執行");
        ConversationForkRules.Notice(new string('長', 60), true, false).Should().Contain(new string('長', 40) + "…」");
    }

    [Fact]
    public void BuildRecap_延伸的開頭說明()
    {
        var items = new List<AgentChatItem>
        {
            new() { Kind = AgentItemKind.User, Text = "先查 bug" },
            new() { Kind = AgentItemKind.Assistant, Text = "找到了：登入逾時" },
        };
        var recap = ConversationForkRules.BuildRecap(items)!;
        recap.Should().StartWith(ConversationForkRules.RecapIntro).And.Contain("【使用者】先查 bug").And.Contain("【代理】找到了：登入逾時");
        AgentHubRules.BuildHistoryRecap(items)!.Should().StartWith(AgentHubRules.HistoryRecapIntro, "換帳戶的照舊");
    }

    [Fact]
    public void CopyItems_新的實例與id_清掉只屬於原對話的欄位_保留CliUuid()
    {
        var source = new AgentChatItem
        {
            Kind = AgentItemKind.User,
            Text = "第一則",
            CliUuid = "u-1",
            Ord = 7,
            RequestDigest = "rd1",
            ProcessInstance = "p1",
            RequestKey = "k",
            Seq = 3,
            MediaRefs = [new AgentMediaRef { Ref = "R1", AttachmentId = "a" }],
            KeptMedia = new Dictionary<string, string> { [@"C:\shots\a.png"] = @"attachments\.kept\x\a.png" },
        };
        var copy = ConversationForkRules.CopyItems([source], From, To).Single();

        copy.Should().NotBeSameAs(source);
        copy.Id.Should().NotBe(source.Id);
        copy.Text.Should().Be("第一則");
        copy.CliUuid.Should().Be("u-1", "Claude 的分支保留每則的 uuid：延伸的對話照樣能回溯");
        copy.Ord.Should().BeNull();
        copy.RequestDigest.Should().BeNull();
        copy.ProcessInstance.Should().BeNull();
        copy.RequestKey.Should().BeNull();
        copy.Seq.Should().Be(0);
        copy.MediaRefs.Should().BeNull();
        copy.KeptMedia.Should().Equal(source.KeptMedia, "副本是相對於對話資料夾的路徑，附件資料夾整份複製過去");
        copy.KeptMedia.Should().NotBeSameAs(source.KeptMedia);
        source.Ord.Should().Be(7, "原本的項目不動");
    }

    [Fact]
    public void CopyItems_延伸時還沒結束的收尾()
    {
        var items = new List<AgentChatItem>
        {
            new() { Kind = AgentItemKind.Tool, Status = AgentItemStatus.Running, Output = "編譯中" },
            new() { Kind = AgentItemKind.Tool, Status = AgentItemStatus.Running },
            new() { Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Running, Text = "我先" },
            new() { Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Running, Text = "  " },
            new() { Kind = AgentItemKind.Thinking, Status = AgentItemStatus.Running, Text = string.Empty },
            new() { Kind = AgentItemKind.Thinking, Status = AgentItemStatus.Running, Text = "想一下" },
            new() { Kind = AgentItemKind.Approval, Status = AgentItemStatus.Waiting },
            new() { Kind = AgentItemKind.Question, Status = AgentItemStatus.Waiting, AnswerAsMessage = true },
            new() { Kind = AgentItemKind.Plan, Status = AgentItemStatus.Running },
            new() { Kind = AgentItemKind.Tool, Status = AgentItemStatus.Done, Output = "ok" },
        };
        var copies = ConversationForkRules.CopyItems(items, From, To);

        copies.Should().HaveCount(8, "空的回覆與思考拿掉");
        copies[0].Status.Should().Be(AgentItemStatus.Cancelled);
        copies[0].Output.Should().Be("編譯中\n" + ConversationForkRules.InterruptedToolNote);
        copies[1].Output.Should().Be(ConversationForkRules.InterruptedToolNote);
        copies[2].Status.Should().Be(AgentItemStatus.Cancelled);
        copies[3].Kind.Should().Be(AgentItemKind.Thinking);
        copies[3].Status.Should().Be(AgentItemStatus.Done);
        copies[4].Status.Should().Be(AgentItemStatus.Cancelled);
        copies[4].Output.Should().Be(ConversationForkRules.StaleCardOutput);
        copies[5].Status.Should().Be(AgentItemStatus.Cancelled, "新對話的代理沒有提出這題");
        copies[6].Status.Should().Be(AgentItemStatus.Running, "待辦清單記的是進度");
        copies[7].Output.Should().Be("ok");
        items[0].Status.Should().Be(AgentItemStatus.Running, "原本的項目不動");
    }

    [Fact]
    public void CopyItems_附件與媒體的路徑改到新對話的附件資料夾()
    {
        var item = new AgentChatItem
        {
            Kind = AgentItemKind.User,
            Attachments =
            [
                new AgentAttachment
                {
                    FileName = "a.png",
                    StoredPath = From + @"\a.png",
                    ModelImagePaths = [From + @"\.model\a.webp", @"D:\other\b.png"],
                },
            ],
            MediaPaths = [From.ToUpperInvariant() + @"\shot.png", @"C:\data\conversations\aaa\workspace\out.png", @"C:\data\conversations\aaa\attachmentsX\c.png"],
        };
        var copy = ConversationForkRules.CopyItems([item], From, To).Single();

        copy.Attachments.Single().StoredPath.Should().Be(To + @"\a.png");
        copy.Attachments.Single().ModelImagePaths.Should().Equal(To + @"\.model\a.webp", @"D:\other\b.png");
        copy.MediaPaths.Should().Equal(To + @"\shot.png", @"C:\data\conversations\aaa\workspace\out.png", @"C:\data\conversations\aaa\attachmentsX\c.png");
        item.Attachments.Single().StoredPath.Should().Be(From + @"\a.png", "原本的附件不動");
    }

    [Fact]
    public void Block_副本草稿讀不到正在延伸()
    {
        ConversationForkRules.Block(false, false, true, false).Should().BeNull();
        ConversationForkRules.Block(true, false, true, false).Should().Contain("這台電腦");
        ConversationForkRules.Block(false, true, true, false).Should().Contain("沒有內容");
        ConversationForkRules.Block(false, false, false, false).Should().Contain("讀不到");
        ConversationForkRules.Block(false, false, true, true).Should().Contain("正在延伸");
    }

    [Fact]
    public void CompleteLength_截到最後一個換行()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"a\":1}\n{\"b\":2}\n{\"c\":");
        ConversationForkRules.CompleteLength(bytes, 100).Should().Be(100 + 16);
        ConversationForkRules.CompleteLength(Encoding.UTF8.GetBytes("{\"c\":"), 0).Should().Be(-1);
        ConversationForkRules.CompleteLength(Encoding.UTF8.GetBytes("x\n"), 0).Should().Be(2);
    }
}
