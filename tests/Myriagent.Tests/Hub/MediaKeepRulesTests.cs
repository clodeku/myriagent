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
