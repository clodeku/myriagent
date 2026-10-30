using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>桌面的下載：只另存畫面登記過的本機檔案，原檔保留，結果只通知發起的視窗。</summary>
[Collection(AppPathsCollection.Name)]
public sealed class SaveFileTests
{
    private static void RenderLink(HubHarness h, string path)
        => h.OnUi(() => h.Hub.RenderItem(new AgentChatItem
        {
            Id = "download-item",
            Kind = AgentItemKind.Assistant,
            Text = $"[下載完整 PowerPoint](<{path.Replace('\\', '/') }>)",
        }, h.Temp.Root));

    [Fact]
    public async Task PowerPoint連結_另存完整位元組_使用原檔名_只通知發起視窗()
    {
        using var h = new HubHarness();
        var other = h.AddView("other");
        var source = h.Temp.WriteFile(@"output\營運報告 37頁.pptx", "原始簡報");
        byte[] content = [0x50, 0x4b, 0x03, 0x04, 0x00, 0xff, 0x80, 0x0a];
        File.WriteAllBytes(source, content);
        var destination = h.Temp.Combine("下載完成.pptx");
        h.Host.SaveFileToPick = destination;
        RenderLink(h, source);
        var mainMark = h.Main.Mark();
        var otherMark = other.Mark();

        h.Command(other, "saveFile", new { path = source });

        var toast = await other.WaitForAsync("toast", since: otherMark);
        toast.Value<string>("level").Should().NotBe("error");
        File.ReadAllBytes(destination).Should().Equal(content);
        File.ReadAllBytes(source).Should().Equal(content, "另存不會搬走或修改原始檔案");
        h.Host.SaveFileRequests.All.Should().Equal((other.Id, "營運報告 37頁.pptx"));
        h.Main.Messages("toast", mainMark).Should().BeEmpty();
        h.Host.Opened.All.Should().BeEmpty("下載不會啟動 PowerPoint");
    }

    [Fact]
    public async Task 圖片預覽已登記的媒體_也可以另存()
    {
        using var h = new HubHarness();
        var source = h.Temp.WriteFile(@"output\完整預覽.png", "預覽圖片內容");
        var destination = h.Temp.Combine("下載預覽.png");
        h.Host.SaveFileToPick = destination;
        h.OnUi(() => h.Hub.RenderItem(new AgentChatItem
        {
            Id = "preview-item",
            Kind = AgentItemKind.Assistant,
            Text = "整體視覺預覽",
            MediaPaths = [source],
        }, h.Temp.Root));

        h.Command(h.Main, "saveFile", new { path = source });

        (await h.Main.WaitForAsync("toast")).Value<string>("level").Should().NotBe("error");
        File.ReadAllBytes(destination).Should().Equal(File.ReadAllBytes(source));
    }

    [Fact]
    public async Task 取消另存_不複製也不提示失敗()
    {
        using var h = new HubHarness();
        var source = h.Temp.WriteFile(@"output\報告.pptx", "原始簡報");
        RenderLink(h, source);
        h.Host.SaveFileToPick = null;
        var mark = h.Main.Mark();

        h.Command(h.Main, "saveFile", new { path = source });

        await h.WaitUntilAsync(() => h.Host.SaveFileRequests.Count == 1, "已顯示另存對話框並取消");
        await h.Ui.DrainAsync();
        File.ReadAllText(source).Should().Be("原始簡報");
        h.Main.Messages("toast", mark).Should().BeEmpty();
    }

    [Fact]
    public async Task 已登記但檔案被移除_通知發起視窗_不顯示另存對話框()
    {
        using var h = new HubHarness();
        var other = h.AddView("other");
        var source = h.Temp.WriteFile(@"output\已移除.pptx", "原始簡報");
        RenderLink(h, source);
        File.Delete(source);
        h.Host.SaveFileToPick = h.Temp.Combine("不該產生.pptx");
        var mainMark = h.Main.Mark();
        var otherMark = other.Mark();

        h.Command(other, "saveFile", new { path = source });

        var toast = await other.WaitForAsync("toast", since: otherMark);
        toast.Value<string>("level").Should().Be("error");
        toast.Value<string>("text").Should().Contain("找不到");
        h.Host.SaveFileRequests.All.Should().BeEmpty();
        File.Exists(h.Host.SaveFileToPick).Should().BeFalse();
        h.Main.Messages("toast", mainMark).Should().BeEmpty();
    }

    [Fact]
    public async Task 未登記的本機檔案_即使存在也拒絕下載()
    {
        using var h = new HubHarness();
        var source = h.Temp.WriteFile(@"private\未登記.pptx", "不在回覆中的檔案");
        h.Host.SaveFileToPick = h.Temp.Combine("不該產生.pptx");

        h.Command(h.Main, "saveFile", new { path = source });

        (await h.Main.WaitForAsync("toast")).Value<string>("level").Should().Be("error");
        h.Host.SaveFileRequests.All.Should().BeEmpty();
        File.Exists(h.Host.SaveFileToPick).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"\\evil\share\簡報.pptx")]
    [InlineData("//evil/share/簡報.pptx")]
    public async Task UNC連結_渲染後也拒絕下載(string source)
    {
        using var h = new HubHarness();
        RenderLink(h, source);
        h.Host.SaveFileToPick = h.Temp.Combine("不該產生.pptx");

        h.Command(h.Main, "saveFile", new { path = source });

        (await h.Main.WaitForAsync("toast")).Value<string>("level").Should().Be("error");
        h.Host.SaveFileRequests.All.Should().BeEmpty();
        File.Exists(h.Host.SaveFileToPick).Should().BeFalse();
    }

    [Fact]
    public async Task 另存選到同一檔案_不截斷原檔_大小寫與分隔符差異也視為相同()
    {
        using var h = new HubHarness();
        var source = h.Temp.WriteFile(@"output\原始簡報.pptx", "完整簡報內容");
        RenderLink(h, source);
        h.Host.SaveFileToPick = source.ToUpperInvariant().Replace('\\', '/');

        h.Command(h.Main, "saveFile", new { path = source });

        await h.Main.WaitForAsync("toast");
        File.ReadAllText(source).Should().Be("完整簡報內容");
        h.Host.SaveFileRequests.Count.Should().Be(1);
    }

    [Fact]
    public async Task 目的檔案無法寫入_回報失敗_原檔與目的檔保留()
    {
        using var h = new HubHarness();
        var source = h.Temp.WriteFile(@"output\報告.pptx", "原始簡報");
        var destination = h.Temp.WriteFile("既有簡報.pptx", "既有內容");
        RenderLink(h, source);
        h.Host.SaveFileToPick = destination;
        using var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None);

        h.Command(h.Main, "saveFile", new { path = source });

        (await h.Main.WaitForAsync("toast")).Value<string>("level").Should().Be("error");
        File.ReadAllText(source).Should().Be("原始簡報");
        using var reader = new StreamReader(locked);
        reader.ReadToEnd().Should().Be("既有內容");
    }
}
