using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 圖片與影片右鍵選單的「複製」（copyMedia，2026-11-01）：只把畫面登記過的本機檔案以檔案清單放進剪貼簿，
/// 原檔不動、結果只通知發起的視窗；未登記、UNC、不存在、資料夾一律拒絕
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class CopyMediaTests
{
    private static string RenderVideo(HubHarness h, string relative)
    {
        var source = h.Temp.WriteFile(relative, "影片內容");
        h.OnUi(() => h.Hub.RenderItem(new AgentChatItem
        {
            Id = "video-item",
            Kind = AgentItemKind.Assistant,
            Text = "做好的影片",
            MediaPaths = [source],
        }, h.Temp.Root));
        return source;
    }

    [Fact]
    public async Task 回覆中的影片_放進剪貼簿_只通知發起視窗()
    {
        using var h = new HubHarness();
        var other = h.AddView("other");
        var source = RenderVideo(h, @"output\成品 1080p.mp4");
        var mainMark = h.Main.Mark();
        var otherMark = other.Mark();

        h.Command(other, "copyMedia", new { path = source });

        var toast = await other.WaitForAsync("toast", since: otherMark);
        toast.Value<string>("level").Should().NotBe("error");
        toast.Value<string>("text").Should().Contain("成品 1080p.mp4");
        h.Host.CopiedFiles.All.Should().Equal(source);
        File.ReadAllText(source).Should().Be("影片內容", "複製不會搬走或修改原始檔案");
        h.Main.Messages("toast", mainMark).Should().BeEmpty();
        h.Host.Copied.All.Should().BeEmpty("不是複製文字");
    }

    [Fact]
    public async Task 剪貼簿被佔著_回報失敗()
    {
        using var h = new HubHarness();
        var source = RenderVideo(h, @"output\成品.mp4");
        h.Host.CopyFileSucceeds = false;

        h.Command(h.Main, "copyMedia", new { path = source });

        var toast = await h.Main.WaitForAsync("toast");
        toast.Value<string>("level").Should().Be("error");
        toast.Value<string>("text").Should().Contain("剪貼簿");
    }

    [Fact]
    public async Task 未登記的本機檔案_即使存在也拒絕()
    {
        using var h = new HubHarness();
        var source = h.Temp.WriteFile(@"private\未登記.mp4", "不在回覆中的檔案");

        h.Command(h.Main, "copyMedia", new { path = source });

        (await h.Main.WaitForAsync("toast")).Value<string>("level").Should().Be("error");
        h.Host.CopiedFiles.All.Should().BeEmpty();
    }

    [Fact]
    public async Task 已登記但檔案被移除_回報找不到()
    {
        using var h = new HubHarness();
        var source = RenderVideo(h, @"output\已移除.mp4");
        File.Delete(source);

        h.Command(h.Main, "copyMedia", new { path = source });

        var toast = await h.Main.WaitForAsync("toast");
        toast.Value<string>("level").Should().Be("error");
        toast.Value<string>("text").Should().Contain("找不到");
        h.Host.CopiedFiles.All.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"\\evil\share\影片.mp4")]
    [InlineData("//evil/share/影片.mp4")]
    public async Task UNC路徑_一律拒絕(string source)
    {
        using var h = new HubHarness();

        h.Command(h.Main, "copyMedia", new { path = source });

        (await h.Main.WaitForAsync("toast")).Value<string>("level").Should().Be("error");
        h.Host.CopiedFiles.All.Should().BeEmpty();
    }
}
