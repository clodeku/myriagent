using Myriagent.Agents;
using Myriagent.Rendering;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Rendering;

/// <summary>
/// 共用渲染器（Protocol 的 ItemRenderer）：網址一律經 IMediaUrlBuilder（伺服器換成自己的網址、畫面上不會出現桌面網域）、
/// 呼叫的順序、失敗的處理與記錄（同一筆只記一次）。輸出逐位元組與抽出前相同由 ItemRendererGoldenTests 負責
/// </summary>
public class ItemRendererTests
{
    private const string Cwd = @"C:\repo\proj";

    /// <summary>伺服器那種網址（/m、/t、/f）：記下每次呼叫，順序就是渲染器呼叫的順序</summary>
    private sealed class RecordingUrls : IMediaUrlBuilder
    {
        public List<string> Calls { get; } = new();

        public string MediaUrl(string path)
        {
            Calls.Add($"media {path}");
            return "/m/" + Uri.EscapeDataString(path);
        }

        public string ThumbUrl(string path, int size, string? version = null)
        {
            Calls.Add($"thumb {path} {size}");
            return $"/t/{Uri.EscapeDataString(path)}?s={size}";
        }

        public string MarkdownImageUrl(string path)
        {
            Calls.Add($"image {path}");
            return "/m/" + Uri.EscapeDataString(path);
        }

        public string MarkdownLinkUrl(string path)
        {
            Calls.Add($"link {path}");
            return "/f/" + Uri.EscapeDataString(path);
        }

        public void AllowMarkdownImage(string path) => Calls.Add($"allow {path}");
    }

    [Fact]
    public void 網址一律經IMediaUrlBuilder_伺服器換成自己的網址_畫面上沒有桌面網域()
    {
        var urls = new RecordingUrls();
        var item = new AgentChatItem
        {
            Id = "a1",
            Kind = AgentItemKind.Assistant,
            Text = @"![圖](C:\repo\proj\out\chart.png) [報表](out/r.xlsx) [官網](https://example.com) ![外站](https://evil.example/x.png)",
            MediaPaths = { @"C:\repo\proj\out\demo.mp4" },
        };

        var rendered = new ItemRenderer(urls).Render(item, Cwd);

        var html = rendered.Value<string>("html");
        html.Should().Contain("src=\"/m/C%3A%5Crepo%5Cproj%5Cout%5Cchart.png\"")
            .And.Contain("href=\"/f/C%3A%5Crepo%5Cproj%5Cout%5Cr.xlsx\"")
            .And.Contain("href=\"https://example.com\"")
            .And.Contain("href=\"https://evil.example/x.png\"", "外站圖片照樣只改成連結");
        rendered["media"]![0]!.Value<string>("url").Should().Be("/m/C%3A%5Crepo%5Cproj%5Cout%5Cdemo.mp4");
        rendered["media"]![0]!.Value<string>("kind").Should().Be("video");
        rendered.ToString().Should().NotContain(AgentMarkdownRenderer.Host);
        urls.Calls.Should().Equal(
            @"image C:\repo\proj\out\chart.png",
            @"link C:\repo\proj\out\r.xlsx",
            @"allow C:\repo\proj\out\chart.png",
            @"media C:\repo\proj\out\demo.mp4");
    }

    [Fact]
    public void 計畫確認_文字與計畫內容的圖片轉換完才依序通知_接著才是附件與媒體()
    {
        var urls = new RecordingUrls();
        var item = new AgentChatItem
        {
            Id = "p1",
            Kind = AgentItemKind.PlanApproval,
            Text = @"![不顯示](C:\repo\proj\text.png)",
            Detail = "![流程](docs/flow.png) ![重複](docs/flow.png)",
            MediaPaths = { @"C:\repo\proj\out\after.png" },
        };

        var rendered = new ItemRenderer(urls).Render(item, Cwd);

        rendered.Value<string>("html").Should().BeEmpty("PlanApproval 的 Text 不轉換");
        rendered.Value<string>("detail").Should().BeNull("計畫內容只放在 detailHtml");
        rendered.Value<string>("detailHtml").Should().Contain("src=\"/m/C%3A%5Crepo%5Cproj%5Cdocs%5Cflow.png\"");
        urls.Calls.Should().Equal(
            @"image C:\repo\proj\docs\flow.png",
            @"image C:\repo\proj\docs\flow.png",
            @"allow C:\repo\proj\docs\flow.png",
            @"allow C:\repo\proj\docs\flow.png",
            @"media C:\repo\proj\out\after.png");
    }

    [Fact]
    public void 附件_縮圖先於媒體網址_影片用第一張畫面當縮圖_文件兩者都沒有()
    {
        var urls = new RecordingUrls();
        var renderer = new ItemRenderer(urls);

        var image = renderer.RenderAttachment(new AgentAttachment { Id = "x1", FileName = "a.png", StoredPath = @"C:\att\a.png", Kind = AgentAttachmentKind.Image, SizeBytes = 2048 });
        var video = renderer.RenderAttachment(new AgentAttachment
        {
            Id = "x2", FileName = "b.mp4", StoredPath = @"C:\att\b.mp4", Kind = AgentAttachmentKind.Video, SizeBytes = 1536,
            ModelImagePaths = { @"C:\att\.model\b_1.jpg", @"C:\att\.model\b_2.jpg" }, DurationSeconds = 65,
        });
        var document = renderer.RenderAttachment(new AgentAttachment { Id = "x3", FileName = "c.pdf", StoredPath = @"C:\att\c.pdf", Kind = AgentAttachmentKind.Document, SizeBytes = 10 });

        image.Value<string>("thumbUrl").Should().Be("/t/C%3A%5Catt%5Ca.png?s=320");
        image.Value<string>("url").Should().Be("/m/C%3A%5Catt%5Ca.png");
        image.Value<string>("size").Should().Be("2 KB");
        video.Value<string>("thumbUrl").Should().Be("/t/C%3A%5Catt%5C.model%5Cb_1.jpg?s=320");
        video.Value<string>("videoInfo").Should().Be("1:05・2 張畫面");
        document.Value<string>("thumbUrl").Should().BeNull();
        document.Value<string>("url").Should().BeNull();
        urls.Calls.Should().Equal(
            @"thumb C:\att\a.png 320",
            @"media C:\att\a.png",
            @"thumb C:\att\.model\b_1.jpg 320",
            @"media C:\att\b.mp4");
    }

    [Fact]
    public void 整筆渲染失敗_改送錯誤說明_同一筆只記一次()
    {
        var errors = new List<(string Message, Exception Error)>();
        var renderer = new ItemRenderer(new RecordingUrls(), (message, ex) => errors.Add((message, ex)));
        var item = new AgentChatItem
        {
            Id = "bad",
            Kind = AgentItemKind.User,
            Text = "附件清單裡有 null",
            Attachments = { null! },
            CreatedAt = new DateTime(2026, 7, 15, 1, 0, 0, DateTimeKind.Utc),
            Seq = 7,
        };

        var first = renderer.Render(item, Cwd);
        var second = renderer.Render(item, Cwd);

        JToken.DeepEquals(first, second).Should().BeTrue();
        first.Value<string>("id").Should().Be("bad");
        first.Value<string>("kind").Should().Be("Error");
        first.Value<string>("status").Should().Be("Done");
        first.Value<string>("html").Should().StartWith("這一筆內容無法顯示：");
        first.Value<long>("time").Should().Be(1_784_077_200_000);
        first.Value<long>("seq").Should().Be(7);
        errors.Should().ContainSingle("串流時每 50 毫秒重新渲染，同一筆不能每次都記");
        errors[0].Message.Should().Be("渲染項目 bad 失敗");
        errors[0].Error.Should().BeOfType<NullReferenceException>();
    }

    [Fact]
    public void Markdown轉換失敗_改用純文字_同一筆只記一次警告_其中的本機圖片不通知()
    {
        var urls = new RecordingUrls();
        var warnings = new List<string>();
        var renderer = new ItemRenderer(urls, logWarning: warnings.Add);
        var item = new AgentChatItem { Id = "deep", Kind = AgentItemKind.Assistant, Text = new string('>', 130) + @" ![x](C:\repo\proj\x.png)" };

        var html = renderer.Render(item, Cwd).Value<string>("html");
        renderer.Render(item, Cwd);

        html.Should().StartWith("&gt;&gt;&gt;").And.Contain(@"![x](C:\repo\proj\x.png)", "內容改用純文字照樣看得到");
        warnings.Should().ContainSingle().Which.Should().StartWith("項目 deep 的 Markdown 轉換失敗，改用純文字顯示：");
        urls.Calls.Should().BeEmpty("轉換失敗的內容裡的圖片沒有出現在畫面上，不通知登記");
    }

    [Fact]
    public void 記過Markdown警告的項目_之後整筆失敗不再記錯誤()
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var renderer = new ItemRenderer(new RecordingUrls(), (message, _) => errors.Add(message), warnings.Add);
        var item = new AgentChatItem { Id = "both", Kind = AgentItemKind.Assistant, Text = new string('>', 130), Attachments = { null! } };

        renderer.Render(item, Cwd).Value<string>("kind").Should().Be("Error");

        warnings.Should().ContainSingle();
        errors.Should().BeEmpty("與抽出前的中樞相同：Markdown 與整筆失敗共用同一份「已記過」的紀錄");
    }

    [Fact]
    public void 沒有網址實作_不能建立()
        => FluentActions.Invoking(() => new ItemRenderer(null!)).Should().Throw<ArgumentNullException>();
}
