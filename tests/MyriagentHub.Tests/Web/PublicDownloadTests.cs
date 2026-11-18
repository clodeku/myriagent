using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyriagentHub.Downloads;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Web;

/// <summary>安裝檔的下載頁（/myriagent，匿名）：說明檔的規則、頁面、下載與續傳、沒放檔時沒有這一頁、同時下載的上限</summary>
public sealed class PublicDownloadTests
{
    private const string FileName = "Myriagent-Setup-0.2.74.exe";

    private static string Info(string file = FileName, string version = "0.2.74", object? size = null, string? sha256 = null) => JsonSerializer.Serialize(new
    {
        file,
        version,
        release = version + "-748ee09",
        edition = "standalone",
        size = size ?? 12345L,
        sha256 = sha256 ?? new string('a', 64),
    });

    /// <summary>在主機的資料根放一個安裝檔與說明檔（先放 exe、再放說明檔）</summary>
    private static byte[] Place(HubTestHost host, int length = 300_000, string? infoOverride = null)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        Directory.CreateDirectory(host.Paths.PublicDownloadsDir);
        File.WriteAllBytes(Path.Combine(host.Paths.PublicDownloadsDir, FileName), bytes);
        File.WriteAllText(Path.Combine(host.Paths.PublicDownloadsDir, PublicDownloadRules.InfoFileName),
            infoOverride ?? Info(size: (long)length, sha256: Convert.ToHexStringLower(SHA256.HashData(bytes))), new UTF8Encoding(false));
        return bytes;
    }

    // ───────────────────────────── 規則 ─────────────────────────────

    [Fact]
    public void 說明檔_建置腳本寫的那一份讀得懂_多的欄位不看()
    {
        var info = PublicDownloadRules.Parse(Info(sha256: new string('A', 64)));
        info.Should().Be(new PublicDownloadInfo(FileName, "0.2.74", 12345, new string('a', 64)));
    }

    [Theory]
    [InlineData("..\\\\hub.db")]
    [InlineData("../secrets/x.exe")]
    [InlineData("sub/Setup.exe")]
    [InlineData("C:\\\\x.exe")]
    [InlineData("Setup.msi")]
    [InlineData(".exe")]
    [InlineData("安裝.exe")]
    [InlineData("a b.exe")]
    public void 說明檔_檔名只收單純的exe名稱_帶路徑或別的副檔名一律不認(string file)
        => PublicDownloadRules.Parse(Info(file: file)).Should().BeNull();

    [Fact]
    public void 說明檔_其他不合格的寫法()
    {
        PublicDownloadRules.Parse(null).Should().BeNull();
        PublicDownloadRules.Parse("").Should().BeNull();
        PublicDownloadRules.Parse("[]").Should().BeNull();
        PublicDownloadRules.Parse("{不是 JSON").Should().BeNull();
        PublicDownloadRules.Parse(Info(version: "0.2")).Should().BeNull();
        PublicDownloadRules.Parse(Info(version: "0.2.74-beta")).Should().BeNull();
        PublicDownloadRules.Parse(Info(sha256: "abc")).Should().BeNull();
        PublicDownloadRules.Parse(Info(size: 0L)).Should().BeNull();
        PublicDownloadRules.Parse(Info(size: PublicDownloadRules.MaxSize + 1)).Should().BeNull();
        PublicDownloadRules.Parse(Info(size: "12345")).Should().BeNull();
        PublicDownloadRules.Parse(Info() + new string(' ', PublicDownloadRules.MaxInfoBytes)).Should().BeNull("說明檔太大");
    }

    [Fact]
    public void 頁面_寫版本大小與雜湊_連到那一個檔_沒有腳本()
    {
        var html = PublicDownloadRules.Page(new PublicDownloadInfo(FileName, "0.2.74", 54_948_352, new string('b', 64)));

        html.Should().Contain("<title>Myriagent 下載</title>");
        html.Should().Contain("href=\"/myriagent/download/" + FileName + "\"");
        html.Should().Contain("版本 0.2.74・52.4 MB");
        html.Should().Contain(new string('b', 64));
        html.Should().NotContainEquivalentOf("<script", "網站的 CSP 不准腳本");
        html.Should().NotContainEquivalentOf("clode", "這一頁給單機版用：不帶任何 Clode 的字樣");
    }

    // ───────────────────────────── 端點 ─────────────────────────────

    [Theory]
    [InlineData("/myriagent")]
    [InlineData("/myriagent/")]
    [InlineData("/myriagent/latest.json")]
    [InlineData("/myriagent/download/" + FileName)]
    public async Task 沒有放安裝檔_沒有這一頁(string path)
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);

        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 放了安裝檔_不用登入就看得到頁面與版本資訊()
    {
        await using var host = await HubTestHost.StartAsync();
        var bytes = Place(host);
        using var client = host.CreateClient(cookies: false);

        using var page = await client.GetAsync("/myriagent");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Content.Headers.ContentType!.ToString().Should().Be("text/html; charset=utf-8");
        page.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("script-src 'self'");
        page.Headers.CacheControl!.NoStore.Should().BeTrue();
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain("/myriagent/download/" + FileName).And.Contain("版本 0.2.74・0.3 MB");

        using var slash = await client.GetAsync("/myriagent/");
        slash.StatusCode.Should().Be(HttpStatusCode.OK);

        using var latest = await client.GetAsync("/myriagent/latest.json");
        latest.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await latest.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("file").GetString().Should().Be(FileName);
        doc.RootElement.GetProperty("version").GetString().Should().Be("0.2.74");
        doc.RootElement.GetProperty("size").GetInt64().Should().Be(bytes.Length);
        doc.RootElement.GetProperty("sha256").GetString().Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["file", "version", "size", "sha256"], "說明檔的其他欄位（提交雜湊等）不對外");
    }

    [Fact]
    public async Task 下載_整個檔逐位元組相同_當成附件_可以續傳()
    {
        await using var host = await HubTestHost.StartAsync();
        var bytes = Place(host);
        using var client = host.CreateClient(cookies: false);

        using var full = await client.GetAsync("/myriagent/download/" + FileName);
        full.StatusCode.Should().Be(HttpStatusCode.OK);
        (await full.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
        full.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        full.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        full.Content.Headers.ContentDisposition.FileName.Should().Be(FileName);
        full.Headers.AcceptRanges.Should().Contain("bytes");
        full.Headers.ETag!.Tag.Should().Be("\"" + Convert.ToHexStringLower(SHA256.HashData(bytes)) + "\"");
        full.Headers.CacheControl!.Public.Should().BeTrue();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/myriagent/download/" + FileName);
        request.Headers.Range = new RangeHeaderValue(100_000, null);
        using var rest = await client.Http.SendAsync(request);
        rest.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await rest.Content.ReadAsByteArrayAsync()).Should().Equal(bytes[100_000..]);
    }

    [Theory]
    [InlineData("other.exe")]
    [InlineData("latest.json")]
    [InlineData("myriagent-setup-0.2.74.exe")]
    [InlineData("..%2F..%2Fdb%2Fhub.db")]
    [InlineData("%2E%2E%5Clatest.json")]
    public async Task 下載_只認說明檔寫的那一個檔名_其他一律404(string file)
    {
        await using var host = await HubTestHost.StartAsync();
        Place(host);
        // 資料夾裡的其他檔也不給
        File.WriteAllText(Path.Combine(host.Paths.PublicDownloadsDir, "other.exe"), "x");
        using var client = host.CreateClient(cookies: false);

        using var response = await client.GetAsync("/myriagent/download/" + file);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 說明檔不合格或檔案大小不符_當成沒有放()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);

        Place(host, infoOverride: "{ 壞掉的說明檔");
        using (var response = await client.GetAsync("/myriagent"))
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 說明檔先換了、exe 還沒放好（或放到一半）：大小對不上就先不給
        Place(host, infoOverride: Info(size: 999_999L));
        using (var response = await client.GetAsync("/myriagent"))
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var response = await client.GetAsync("/myriagent/download/" + FileName))
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        Place(host);
        using (var response = await client.GetAsync("/myriagent"))
            response.StatusCode.Should().Be(HttpStatusCode.OK, "放好之後不必重啟服務就有");
    }

    [Fact]
    public async Task 同時下載超過上限_回503請稍後再試_放開之後恢復()
    {
        await using var host = await HubTestHost.StartAsync();
        Place(host);
        using var client = host.CreateClient(cookies: false);
        var store = host.Services.GetRequiredService<PublicDownloadStore>();
        for (int i = 0; i < PublicDownloadStore.MaxConcurrent; i++)
            store.TryEnter().Should().BeTrue();

        using (var busy = await client.GetAsync("/myriagent/download/" + FileName))
        {
            busy.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            busy.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
        }
        using (var page = await client.GetAsync("/myriagent"))
            page.StatusCode.Should().Be(HttpStatusCode.OK, "頁面不受下載上限影響");

        store.Leave();
        using (var ok = await client.GetAsync("/myriagent/download/" + FileName))
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var again = await client.GetAsync("/myriagent/download/" + FileName))
            again.StatusCode.Should().Be(HttpStatusCode.OK, "下載完會把名額還回去");
    }
}
