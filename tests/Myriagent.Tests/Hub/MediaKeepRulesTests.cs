using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 回覆媒體「留一份」的規則（2026-10-25）：要留哪些檔、副本放哪裡、項目記的相對路徑只認固定的形狀（items.json 被改也不會指到對話資料夾外）
/// </summary>
public sealed class MediaKeepRulesTests
{
    private const string Conversation = @"C:\data\conversations\c1";

    [Fact]
    public void 要留的檔_本機磁碟的圖片與影片_不含UNC_其他副檔名_對話自己的附件_已經留過的_不重複()
    {
        var result = MediaKeepRules.Candidates(
            [
                @"C:\repo\out\a.png",
                "C:/repo/out/b.MP4",
                @"c:\REPO\out\A.png",
                @"\\evil\share\c.png",
                @"C:\repo\notes.txt",
                @"C:\data\conversations\c1\attachments\photo.png",
                @"C:\repo\out\kept.jpg",
                null,
                "",
                "out/relative.png",
            ],
            Conversation,
            [@"C:\repo\out\KEPT.jpg"]);

        result.Should().Equal(@"C:\repo\out\a.png", @"C:\repo\out\b.MP4");
    }

    [Fact]
    public void 延伸對話補留_只留conversations底下的_工作資料夾與自己的不留()
    {
        var result = MediaKeepRules.Candidates(
            [
                @"C:\data\conversations\src\caption-demo\a.png",
                @"C:\data\conversations\src\workspace\shot.png",
                @"C:\data\conversations\c1\attachments\b.png",
                @"C:\repo\out\c.png",
                @"C:\data\conversations\grand\d.webp",
            ],
            Conversation, null, onlyUnder: @"C:\data\conversations", exceptUnder: @"C:\data\conversations\src\workspace");

        result.Should().Equal([@"C:\data\conversations\src\caption-demo\a.png", @"C:\data\conversations\grand\d.webp"],
            "原對話資料夾與更早的對話（原對話也是延伸來的）留；工作資料夾（原對話的工作區）遠端本來就放行、conversations 以外照一般的規則");
    }

    [Fact]
    public void 延伸對話補留_範圍資料夾寫法不合格_一個都不留()
    {
        MediaKeepRules.Candidates([@"C:\repo\a.png"], Conversation, null, onlyUnder: @"\\server\share\conversations").Should().BeEmpty();
        MediaKeepRules.Candidates([@"C:\repo\a.png"], Conversation, null, exceptUnder: @"\\server\share").Should().Equal([@"C:\repo\a.png"], "排除的寫法不合格＝不排除");
    }

    [Fact]
    public void 延伸對話要不要補留_其他對話資料夾的代理媒體或有圖片語法的回覆()
    {
        const string root = @"C:\data\conversations";
        const string card = @"C:\data\conversations\src\caption-demo\a.png";
        var tool = new AgentChatItem { Kind = AgentItemKind.Tool, MediaPaths = [card] };
        MediaKeepRules.NeedsForkKeep(tool, root, Conversation, @"C:\repo").Should().BeTrue();

        tool.KeptMedia = new Dictionary<string, string> { [card] = @"attachments\.kept\0123456789abcdef\a.png" };
        MediaKeepRules.NeedsForkKeep(tool, root, Conversation, @"C:\repo").Should().BeFalse("已經留過");

        MediaKeepRules.NeedsForkKeep(new AgentChatItem { Kind = AgentItemKind.Tool, MediaPaths = [@"C:\repo\out\a.png"] }, root, Conversation, @"C:\repo")
            .Should().BeFalse("conversations 以外的照一般的留存規則（回覆完成時留）");
        MediaKeepRules.NeedsForkKeep(new AgentChatItem { Kind = AgentItemKind.Tool, MediaPaths = [@"C:\data\conversations\src\workspace\s.png"] },
            root, Conversation, @"C:\data\conversations\src\workspace").Should().BeFalse("工作資料夾裡的不補");

        MediaKeepRules.NeedsForkKeep(new AgentChatItem { Kind = AgentItemKind.Assistant, Text = "看 ![字卡](caption-demo/a.png)" }, root, Conversation, null)
            .Should().BeTrue("Markdown 引用的路徑要背景解析才知道");
        MediaKeepRules.NeedsForkKeep(new AgentChatItem { Kind = AgentItemKind.Assistant, Text = "沒有圖" }, root, Conversation, null).Should().BeFalse();
        MediaKeepRules.NeedsForkKeep(new AgentChatItem { Kind = AgentItemKind.User, Text = "![x](C:/data/conversations/src/a.png)" }, root, Conversation, null)
            .Should().BeFalse("只有回覆的 Markdown 會畫成圖");
    }

    [Fact]
    public void 對話資料夾裡attachments以外的媒體照留_代理之後可能清掉暫存檔()
    {
        // 2026-11-02 實機：代理把預覽圖寫在 conversations\{id}\d11\，之後刪掉，回覆就破圖。只有 attachments\（附件與副本）是 app 自己管的
        MediaKeepRules.Candidates(
            [@"C:\data\conversations\c1\d11\_sheet.png", @"C:\data\conversations\c1\workspace\shot.png",
             @"C:\data\conversations\c1\attachments\.kept\0123456789abcdef\a.png", @"C:\data\conversations\c1\attachmentsX\b.png"],
            Conversation, null)
            .Should().Equal(@"C:\data\conversations\c1\d11\_sheet.png", @"C:\data\conversations\c1\workspace\shot.png", @"C:\data\conversations\c1\attachmentsX\b.png");
    }

    [Fact]
    public void 對話資料夾以分隔字元為界_名字開頭相同的別的資料夾照留()
    {
        MediaKeepRules.Candidates([@"C:\data\conversations\c10\x.png"], Conversation, null).Should().Equal(@"C:\data\conversations\c10\x.png");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(MediaKeepRules.MaxBytes, true)]
    [InlineData(MediaKeepRules.MaxBytes + 1, false)]
    public void 大小_空檔與超過上限的不留(long length, bool expected)
    {
        MediaKeepRules.ShouldCopy(length).Should().Be(expected);
    }

    [Fact]
    public void 副本的相對路徑_在attachments的kept底下_檔名照舊_同一個路徑在不同回覆各留各的()
    {
        var a = MediaKeepRules.RelativePath("item-1", @"C:\repo\out\shot.png");
        var parts = a.Split('\\');

        parts.Should().HaveCount(4);
        parts[0].Should().Be("attachments");
        parts[1].Should().Be(".kept");
        parts[2].Should().MatchRegex("^[0-9a-f]{16}$");
        parts[3].Should().Be("shot.png");
        MediaKeepRules.RelativePath("item-1", @"c:\REPO\OUT\SHOT.png").Split('\\')[2].Should().Be(parts[2], "路徑不分大小寫");
        MediaKeepRules.RelativePath("item-2", @"C:\repo\out\shot.png").Should().NotBe(a, "探針每次重拍同名的截圖：每則回覆留自己看到的那一張");
    }

    [Fact]
    public void 完整路徑_只認固定的形狀_一定在對話資料夾裡()
    {
        var relative = MediaKeepRules.RelativePath("item-1", @"C:\repo\out\shot.png");
        MediaKeepRules.FullPath(Conversation, relative).Should().Be(Path.Combine(Conversation, relative));
        MediaKeepRules.FullPath(Conversation, relative.Replace('\\', '/')).Should().Be(Path.Combine(Conversation, relative));

        MediaKeepRules.FullPath(Conversation, null).Should().BeNull();
        MediaKeepRules.FullPath(Conversation, @"attachments\.kept\0123456789abcdef\..").Should().BeNull();
        MediaKeepRules.FullPath(Conversation, @"..\..\0123456789abcdef\x.png").Should().BeNull();
        MediaKeepRules.FullPath(Conversation, @"attachments\.kept\0123456789abcdef\..\..\..\x.png").Should().BeNull();
        MediaKeepRules.FullPath(Conversation, @"attachments\other\0123456789abcdef\x.png").Should().BeNull();
        MediaKeepRules.FullPath(Conversation, @"attachments\.kept\0123\x.png").Should().BeNull("雜湊要 16 碼");
        MediaKeepRules.FullPath(Conversation, @"attachments\.kept\0123456789abcdeg\x.png").Should().BeNull("雜湊要是十六進位");
        MediaKeepRules.FullPath(Conversation, @"attachments\.kept\0123456789abcdef\a\x.png").Should().BeNull();
        MediaKeepRules.FullPath(Conversation, @"C:\Windows\x.png").Should().BeNull();
        MediaKeepRules.FullPath(Conversation, @"attachments\.kept\0123456789abcdef\a:b.png").Should().BeNull("替代資料流");
    }

    [Fact]
    public void 原路徑對到副本_不分大小寫與分隔字元_沒有留的回null()
    {
        var relative = MediaKeepRules.RelativePath("item-1", @"C:\repo\out\shot.png");
        // 從 items.json 讀回的字典分大小寫
        var kept = new Dictionary<string, string> { [@"C:\repo\out\shot.png"] = relative };

        MediaKeepRules.KeptPath(kept, Conversation, @"C:\repo\out\shot.png").Should().Be(Path.Combine(Conversation, relative));
        MediaKeepRules.KeptPath(kept, Conversation, "c:/REPO/out/SHOT.png").Should().Be(Path.Combine(Conversation, relative));
        MediaKeepRules.KeptPath(kept, Conversation, @"C:\repo\out\other.png").Should().BeNull();
        MediaKeepRules.KeptPath(null, Conversation, @"C:\repo\out\shot.png").Should().BeNull();
        MediaKeepRules.KeptPath(new Dictionary<string, string> { [@"C:\repo\out\shot.png"] = @"..\..\x.png" }, Conversation, @"C:\repo\out\shot.png")
            .Should().BeNull("記錄不合格就照原檔");
    }
}
