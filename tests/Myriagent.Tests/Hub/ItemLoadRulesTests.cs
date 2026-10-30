using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>載入 items.json 時的收尾（中樞的 EnsureItems 與 WP4.3 步 12 唯讀讀未載入的對話共用）</summary>
public sealed class ItemLoadRulesTests
{
    private static AgentChatItem Item(AgentItemKind kind, AgentItemStatus status, bool answerAsMessage = false)
        => new() { Kind = kind, Status = status, AnswerAsMessage = answerAsMessage, Text = "x" };

    // 列舉是 internal：以名稱傳入（public 的測試方法不能有 internal 型別的參數）
    [Theory]
    [InlineData("Tool", "Running", false, "Cancelled", true)]
    [InlineData("Assistant", "Running", false, "Done", true)]
    [InlineData("Thinking", "Running", false, "Done", true)]
    [InlineData("Approval", "Waiting", false, "Cancelled", true)]
    [InlineData("Question", "Waiting", false, "Cancelled", true)]
    [InlineData("Question", "Waiting", true, "Waiting", false)]
    [InlineData("Tool", "Done", false, "Done", false)]
    [InlineData("Tool", "Failed", false, "Failed", false)]
    [InlineData("User", "Done", false, "Done", false)]
    public void 執行中與等待中的項目照規則收尾_回傳有沒有改(string kind, string status, bool answerAsMessage, string expected, bool changed)
    {
        var item = Item(Enum.Parse<AgentItemKind>(kind), Enum.Parse<AgentItemStatus>(status), answerAsMessage);

        ItemLoadRules.FinalizeOnLoad(item).Should().Be(changed);
        item.Status.Should().Be(Enum.Parse<AgentItemStatus>(expected));
    }

    [Fact]
    public void 旁支提問的答案還沒回來app就結束_改寫成沒有回答_有答案的不動()
    {
        var pending = new AgentChatItem
        {
            Kind = AgentItemKind.Notice, NoticeKind = AgentNoticeKind.SideQuestion, Status = AgentItemStatus.Running,
            Text = SideQuestionRules.Text("問題", null, null),
        };
        ItemLoadRules.FinalizeOnLoad(pending).Should().BeTrue();
        pending.Status.Should().Be(AgentItemStatus.Failed);
        pending.Text.Should().Be("> 問題\n\n" + SideQuestionRules.UnansweredLine);
        ItemLoadRules.FinalizeOnLoad(pending).Should().BeFalse("冪等");

        // 一輪結束時中樞把 Running 的項目都改成 Done：答案還沒回來的照樣改寫
        var doneButPending = new AgentChatItem
        {
            Kind = AgentItemKind.Notice, NoticeKind = AgentNoticeKind.SideQuestion, Status = AgentItemStatus.Done,
            Text = SideQuestionRules.Text("問題", null, null),
        };
        ItemLoadRules.FinalizeOnLoad(doneButPending).Should().BeTrue();
        doneButPending.Status.Should().Be(AgentItemStatus.Failed);

        var answered = new AgentChatItem
        {
            Kind = AgentItemKind.Notice, NoticeKind = AgentNoticeKind.SideQuestion, Status = AgentItemStatus.Done,
            Text = SideQuestionRules.Text("問題", "答案", null),
        };
        ItemLoadRules.FinalizeOnLoad(answered).Should().BeFalse();
        answered.Text.Should().Be("> 問題\n\n答案");
    }

    [Fact]
    public void 收尾是冪等的()
    {
        var item = Item(AgentItemKind.Tool, AgentItemStatus.Running);
        ItemLoadRules.FinalizeOnLoad(item).Should().BeTrue();

        ItemLoadRules.FinalizeOnLoad(item).Should().BeFalse("第二次沒有東西要改");
        item.Status.Should().Be(AgentItemStatus.Cancelled);
    }

    [Fact]
    public void null丟例外()
    {
        var act = () => ItemLoadRules.FinalizeOnLoad(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
