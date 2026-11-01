using Myriagent.Agents;
using Myriagent.Preview;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// PDF 預覽（2026-11-01）：/media 只以 application/pdf 提供畫面上出現過的 PDF——回覆中的本機檔案連結、文件附件、檔案瀏覽器列出的；
/// 沒出現過的、不是 PDF 的一律不給（/media 照舊只給圖片與影片）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class PdfPreviewTests
{
    [Fact]
    public void 回覆中的PDF連結_可以預覽_其他文件與沒出現過的PDF不行()
    {
        using var h = new HubHarness();
        var pdf = h.Temp.WriteFile(@"output\季報 第3季.pdf", "%PDF-1.7");
        var docx = h.Temp.WriteFile(@"output\說明.docx", "docx");
        var other = h.Temp.WriteFile(@"private\未出現.pdf", "%PDF-1.7");

        h.OnUi(() => h.Hub.RenderItem(new AgentChatItem
        {
            Id = "reply",
            Kind = AgentItemKind.Assistant,
            Text = $"[季報](<{pdf.Replace('\\', '/')}>)、[說明](<{docx.Replace('\\', '/')}>)",
        }, h.Temp.Root));

        h.OnUi(() => h.Hub.IsPdfAllowed(pdf)).Should().BeTrue();
        h.OnUi(() => h.Hub.IsPdfAllowed(pdf.ToUpperInvariant().Replace('\\', '/'))).Should().BeTrue("大小寫與分隔符不同也是同一個檔");
        h.OnUi(() => h.Hub.IsPdfAllowed(docx)).Should().BeFalse("只有 PDF 經 /media 預覽");
        h.OnUi(() => h.Hub.IsPdfAllowed(other)).Should().BeFalse("沒出現在畫面上");
        h.OnUi(() => h.Hub.IsMediaAllowed(pdf)).Should().BeFalse("圖片與影片的白名單不變");
    }

    [Fact]
    public void 使用者訊息的PDF附件_可以預覽()
    {
        using var h = new HubHarness();
        var stored = h.Temp.WriteFile(@"attachments\合約.pdf", "%PDF-1.7");

        h.OnUi(() => h.Hub.RenderItem(new AgentChatItem
        {
            Id = "user",
            Kind = AgentItemKind.User,
            Text = "幫我看合約",
            Attachments = [new AgentAttachment { Id = "a1", FileName = "合約.pdf", Kind = AgentAttachmentKind.Document, StoredPath = stored, SizeBytes = 8 }],
        }, h.Temp.Root));

        h.OnUi(() => h.Hub.IsPdfAllowed(stored)).Should().BeTrue();
    }

    [Fact]
    public async Task 檔案瀏覽器列出的PDF_可以預覽()
    {
        using var h = new HubHarness();
        var folder = h.Temp.Combine("proj");
        Directory.CreateDirectory(folder);
        var pdf = Path.Combine(folder, "簡報.pdf");
        File.WriteAllText(pdf, "%PDF-1.7");
        File.WriteAllText(Path.Combine(folder, "筆記.txt"), "x");
        var (id, _) = h.StartConversation(h.Main, h.Claude, "看檔案");
        var mark = h.Main.Mark();

        h.Command(h.Main, "listFiles", new { conversationId = id, requestId = "r1", path = folder });

        var files = await h.Main.WaitForAsync("files", m => m.Value<string>("requestId") == "r1", mark);
        files["entries"]!.Select(e => e!.Value<string>("name")).Should().Contain("簡報.pdf");
        h.OnUi(() => h.Hub.IsPdfAllowed(pdf)).Should().BeTrue();
        h.OnUi(() => h.Hub.IsPdfAllowed(Path.Combine(folder, "筆記.txt"))).Should().BeFalse();
    }

    [Fact]
    public void PDF的判斷只看副檔名()
    {
        AgentAttachmentRules.IsPdf(@"C:\a\b.PDF").Should().BeTrue();
        AgentAttachmentRules.IsPdf(@"C:\a\b.pdf.exe").Should().BeFalse();
        AgentAttachmentRules.IsPdf(null).Should().BeFalse();
        AgentAttachmentRules.PdfContentType.Should().Be("application/pdf");
    }

    // ───── Word、Excel、PowerPoint（docPreview）─────

    private sealed class FakeConverter(string? error = null) : IDocumentPdfConverter
    {
        public List<(string Source, DocPreviewKind Kind)> Calls { get; } = new();

        public Task<string?> ConvertAsync(string source, DocPreviewKind kind, string outputPdf, CancellationToken cancellationToken)
        {
            lock (Calls)
                Calls.Add((source, kind));
            if (error == null)
                File.WriteAllText(outputPdf, "%PDF-1.7 fake");
            return Task.FromResult(error);
        }
    }

    private static string RenderLink(HubHarness h, string relative)
    {
        var path = h.Temp.WriteFile(relative, "office");
        h.OnUi(() => h.Hub.RenderItem(new AgentChatItem
        {
            Id = "reply-" + Path.GetFileName(relative),
            Kind = AgentItemKind.Assistant,
            Text = $"[檔案](<{path.Replace(Path.DirectorySeparatorChar, '/')}>)",
        }, h.Temp.Root));
        return path;
    }

    [Fact]
    public async Task Office文件_轉成PDF_回可以預覽的網址_第二次用快取()
    {
        using var h = new HubHarness();
        var converter = new FakeConverter();
        var cache = h.Temp.Combine("cache", "doc-preview");
        h.Hub.AttachDocPreview(converter, cache);
        var docx = RenderLink(h, @"output\企劃書.docx");
        var other = h.AddView("other");
        var mark = h.Main.Mark();
        var otherMark = other.Mark();

        h.Command(h.Main, "docPreview", new { requestId = "d1", path = docx });

        var reply = await h.Main.WaitForAsync("docPreview", m => m.Value<string>("requestId") == "d1", mark);
        reply.Value<string>("error").Should().BeNull();
        var url = reply.Value<string>("url")!;
        url.Should().StartWith("https://myriagent.local/media?path=");
        var pdf = Uri.UnescapeDataString(url[(url.IndexOf("path=", StringComparison.Ordinal) + 5)..]);
        Path.GetDirectoryName(pdf).Should().Be(cache);
        File.ReadAllText(pdf).Should().StartWith("%PDF");
        h.OnUi(() => h.Hub.IsPdfAllowed(pdf)).Should().BeTrue("轉好的 PDF 經 /media 預覽");
        converter.Calls.Should().Equal((docx, DocPreviewKind.Word));
        other.Messages("docPreview", otherMark).Should().BeEmpty("只回發起的視窗");

        h.Command(h.Main, "docPreview", new { requestId = "d2", path = docx });
        var again = await h.Main.WaitForAsync("docPreview", m => m.Value<string>("requestId") == "d2", mark);
        again.Value<string>("url").Should().Be(url);
        converter.Calls.Should().HaveCount(1, "檔案沒變就用快取");

        File.WriteAllText(docx, "改過的內容，長度不同");
        h.Command(h.Main, "docPreview", new { requestId = "d3", path = docx });
        var changed = await h.Main.WaitForAsync("docPreview", m => m.Value<string>("requestId") == "d3", mark);
        changed.Value<string>("url").Should().NotBe(url, "檔案改過了就重新轉");
        converter.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Office文件_沒出現在畫面上_不轉()
    {
        using var h = new HubHarness();
        var converter = new FakeConverter();
        h.Hub.AttachDocPreview(converter, h.Temp.Combine("cache"));
        var hidden = h.Temp.WriteFile(@"private\秘密.xlsx", "x");

        h.Command(h.Main, "docPreview", new { requestId = "d1", path = hidden });

        (await h.Main.WaitForAsync("docPreview")).Value<string>("error").Should().Be(DocPreviewRules.NotShownText);
        converter.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 沒有轉檔器或轉檔失敗_回說明()
    {
        using var h = new HubHarness();
        var pptx = RenderLink(h, @"output\簡報.pptx");

        h.Command(h.Main, "docPreview", new { requestId = "d1", path = pptx });
        (await h.Main.WaitForAsync("docPreview", m => m.Value<string>("requestId") == "d1")).Value<string>("error").Should().Be(DocPreviewRules.NoConverterText);

        h.Hub.AttachDocPreview(new FakeConverter(DocPreviewRules.FailedText), h.Temp.Combine("cache"));
        h.Command(h.Main, "docPreview", new { requestId = "d2", path = pptx });
        var failed = await h.Main.WaitForAsync("docPreview", m => m.Value<string>("requestId") == "d2");
        failed.Value<string>("error").Should().Be(DocPreviewRules.FailedText);
        failed.Value<string>("url").Should().BeNull();

        h.Command(h.Main, "docPreview", new { requestId = "d3", path = RenderLink(h, @"output\說明.txt") });
        (await h.Main.WaitForAsync("docPreview", m => m.Value<string>("requestId") == "d3")).Value<string>("error").Should().Be(DocPreviewRules.NotSupportedText);
    }

    [Fact]
    public void 文件種類_快取檔名()
    {
        DocPreviewRules.KindOf("a.DOCX").Should().Be(DocPreviewKind.Word);
        DocPreviewRules.KindOf("a.xlsm").Should().Be(DocPreviewKind.Excel);
        DocPreviewRules.KindOf("a.ppsx").Should().Be(DocPreviewKind.PowerPoint);
        DocPreviewRules.KindOf("a.pdf").Should().BeNull();
        DocPreviewRules.KindOf("a.docx.exe").Should().BeNull();
        var t = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var path = Path.Combine("C:" + Path.DirectorySeparatorChar, "a", "b.docx");
        DocPreviewRules.CacheFileName(path, 10, t).Should().Be(DocPreviewRules.CacheFileName(path.ToUpperInvariant(), 10, t));
        DocPreviewRules.CacheFileName(path, 10, t).Should().NotBe(DocPreviewRules.CacheFileName(path, 11, t));
        DocPreviewRules.CacheFileName(path, 10, t).Should().NotBe(DocPreviewRules.CacheFileName(path, 10, t.AddSeconds(1)));
        DocPreviewRules.CacheFileName(path, 10, t).Should().MatchRegex("^[0-9a-f]{32}[.]pdf$");
        DocPreviewRules.Expired(t, t.AddDays(8)).Should().BeTrue();
        DocPreviewRules.Expired(t, t.AddDays(6)).Should().BeFalse();
        DocPreviewRules.ErrorOf(0).Should().BeNull();
        DocPreviewRules.ErrorOf(3).Should().Be(DocPreviewRules.NoConverterText);
        DocPreviewRules.ErrorOf(2).Should().Be(DocPreviewRules.FailedText);
    }
}
