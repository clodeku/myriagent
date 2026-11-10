using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;

namespace Myriagent.Tests.Hub;

/// <summary>臨時對話的純規則（QuickChatRules）與同步 meta 的 ephemeral（伺服器、其他電腦不列）</summary>
public sealed class QuickChatRulesTests
{
    [Fact]
    public void 朗讀的文字_程式碼區塊換成說明_拿掉Markdown與網址()
    {
        var text = QuickChatRules.SpeechText("# 標題\n\n先這樣：\n\n```python\nprint('hi')\nx = 1\n```\n\n再看 [說明](https://example.com) 與 `code`。\n\n~~~\nraw\n~~~");
        text.Should().Contain("標題").And.Contain("先這樣").And.Contain("再看").And.Contain("連結");
        text.Should().NotContain("print").And.NotContain("raw").And.NotContain("```").And.NotContain("#").And.NotContain("https");
        text.Split(QuickChatRules.CodeBlockSpoken).Length.Should().Be(3, "兩個程式碼區塊各換成一句");
        QuickChatRules.SpeechText("   ").Should().BeEmpty();
        QuickChatRules.SpeechText(null).Should().BeEmpty();
    }

    [Fact]
    public void 沒有收尾的程式碼區塊_到結尾都不念()
    {
        var text = QuickChatRules.SpeechText("說明\n```\nsecret()\nmore()");
        text.Should().Contain("說明").And.Contain(QuickChatRules.CodeBlockSpoken).And.NotContain("secret");
    }

    [Fact]
    public void Claude的專案資料夾_照工作資料夾換算_太長不刪()
    {
        var work = @"C:\Users\me\AppData\Local\Myriagent\conversations\0123456789abcdef0123456789abcdef\workspace";
        QuickChatRules.ClaudeProjectFolder(@"C:\cfg", work).Should().Be(Path.Combine(@"C:\cfg", "projects", ClaudeStreamRules.ProjectFolderName(work)));
        QuickChatRules.ClaudeProjectFolder(null, work).Should().BeNull();
        QuickChatRules.ClaudeProjectFolder(@"C:\cfg", @"C:\" + new string('a', 300)).Should().BeNull("CLI 截斷之後加雜湊，找不準就不刪");
    }

    [Fact]
    public void 同步的meta_只有臨時對話帶ephemeral()
    {
        var meta = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "臨時對話", Ephemeral = true };
        var payload = ConversationWire.Meta(meta, "claude", running: false, waiting: false, models: null, transcript: null, workingDirectoryRef: null);
        payload.Ephemeral.Should().BeTrue();
        SyncJson.Serialize(payload).Should().Contain("\"ephemeral\":true");
        meta.Ephemeral = null;
        var normal = ConversationWire.Meta(meta, "claude", running: false, waiting: false, models: null, transcript: null, workingDirectoryRef: null);
        SyncJson.Serialize(normal).Should().NotContain("ephemeral", "一般對話不帶（前一版的格式不變）");
    }
}
