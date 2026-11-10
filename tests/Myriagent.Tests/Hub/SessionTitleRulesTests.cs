using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

public class SessionTitleRulesTests
{
    [Fact]
    public void Normalize_控制與格式字元換成空白_去頭尾_空的回Null()
    {
        SessionTitleRules.Normalize("  探針\t改名\nC  ").Should().Be("探針 改名 C", "同 CLI 實測的整理");
        SessionTitleRules.Normalize("報表\u200B整理\u202E").Should().Be("報表 整理", "零寬、雙向控制字元");
        SessionTitleRules.Normalize("😀 表情").Should().Be("😀 表情", "代理字對不是格式字元");
        foreach (var empty in new[] { null, "", " ", "\t\n", "\u200B" })
            SessionTitleRules.Normalize(empty).Should().BeNull(empty ?? "null");
    }

    [Fact]
    public void FromAgent_在別處改的名稱_截到上限後改成它()
    {
        var sent = new List<string>();
        SessionTitleRules.FromAgent(sent, "整理報表", "手機上改的名稱").Should().Be("手機上改的名稱");
        SessionTitleRules.FromAgent(sent, "整理報表", new string('字', 120)).Should().Be(new string('字', SessionTitleRules.MaxTitleLength));
        SessionTitleRules.FromAgent(sent, "整理報表", "整理報表 (2)").Should().Be("整理報表 (2)", "CLI 為了不和其他工作階段同名加的後綴照收，兩邊才一致");
    }

    [Fact]
    public void FromAgent_和目前標題相同_或空的_不改()
    {
        var sent = new List<string>();
        SessionTitleRules.FromAgent(sent, "整理報表", "整理報表").Should().BeNull("啟動時與同名重送");
        SessionTitleRules.FromAgent(sent, "整理 報表", "整理\t報表").Should().BeNull("整理後相同");
        SessionTitleRules.FromAgent(sent, new string('字', SessionTitleRules.MaxTitleLength), new string('字', 150)).Should().BeNull("CLI 的上限比較長：截到對話標題的上限後相同");
        SessionTitleRules.FromAgent(sent, "整理報表", "  ").Should().BeNull();
        SessionTitleRules.FromAgent(sent, "整理報表", null).Should().BeNull();
    }

    [Fact]
    public void FromAgent_自己改名的回送不改_連續改名時較早的回送也不會把標題改回去()
    {
        var sent = new List<string>();
        SessionTitleRules.RememberSent(sent, "第一個");
        SessionTitleRules.RememberSent(sent, " 第二個 ");
        SessionTitleRules.RememberSent(sent, "第三個");

        // 標題已經是第三個；CLI 依序回送三個名稱
        SessionTitleRules.FromAgent(sent, "第三個", "第一個").Should().BeNull();
        sent.Should().Equal("第二個", "第三個");
        SessionTitleRules.FromAgent(sent, "第三個", "第二個").Should().BeNull();
        SessionTitleRules.FromAgent(sent, "第三個", "第三個").Should().BeNull();
        sent.Should().BeEmpty();

        // 回送漏掉中間的（只收到最後一個）：更早的一起拿掉
        SessionTitleRules.RememberSent(sent, "甲");
        SessionTitleRules.RememberSent(sent, "乙");
        SessionTitleRules.FromAgent(sent, "乙", "乙").Should().BeNull();
        sent.Should().BeEmpty();
        SessionTitleRules.FromAgent(sent, "乙", "甲").Should().Be("甲", "已經回送過的之後再出現，就是在別處改的");
    }

    [Fact]
    public void IsStaleReport_改名還沒回應時_其他名稱是session原本存的()
    {
        SessionTitleRules.IsStaleReport(Array.Empty<string>(), "舊名稱").Should().BeFalse("沒有送出中的改名：照常交給中樞");
        var pending = new[] { "新名稱" };
        SessionTitleRules.IsStaleReport(pending, "舊名稱").Should().BeTrue("啟動時先回報的既有名稱");
        SessionTitleRules.IsStaleReport(pending, "新名稱").Should().BeFalse("這次改名的回送");
        SessionTitleRules.IsStaleReport(pending, " 新名稱\t").Should().BeFalse("照 CLI 整理名稱的方式比較");
        SessionTitleRules.IsStaleReport(pending, "  ").Should().BeFalse("空的名稱中樞本來就略過");
    }

    [Fact]
    public void RememberSent_最多記MaxPending個_空白不記()
    {
        var sent = new List<string>();
        for (int i = 0; i < SessionTitleRules.MaxPending + 3; i++)
            SessionTitleRules.RememberSent(sent, "名稱" + i);
        sent.Should().HaveCount(SessionTitleRules.MaxPending);
        sent[0].Should().Be("名稱3");
        SessionTitleRules.RememberSent(sent, " ");
        sent.Should().HaveCount(SessionTitleRules.MaxPending);
    }
}
