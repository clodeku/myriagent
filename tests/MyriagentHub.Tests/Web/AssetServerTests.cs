using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using MyriagentHub.Web;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Web;

/// <summary>
/// /app/ 供應嵌入的桌面網頁（設計 14.6；web-protocol 第六節第 1、2 點）：轉址、同一份 CSP 與安全標頭、匿名一律深色、
/// 已登入且偏好淺色時給淺色、ETag 與 304、只插入 wwwroot 實際存在的腳本、組合不合法時只有 /app/ 回 503
/// </summary>
public sealed partial class AssetServerTests
{
    [Fact]
    public async Task app不帶斜線302到app斜線並保留查詢字串()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        using (var response = await client.GetAsync("/app?mode=popout&conversation=c1"))
        {
            response.StatusCode.Should().Be(HttpStatusCode.Found);
            response.Headers.Location!.OriginalString.Should().Be("/app/?mode=popout&conversation=c1");
        }
        using (var response = await client.GetAsync("/app"))
            response.Headers.Location!.OriginalString.Should().Be("/app/");
    }

    [Fact]
    public async Task app斜線回嵌入的index()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        using var response = await client.GetAsync("/app/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("text/html; charset=utf-8");
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain(AssetServerRules.Anchor).And.Contain("src=\"app.js\"").And.Contain("<title>Clode Myriagent</title>");
        html.Should().NotContain("myriagenthub-page", "不是登入頁");

        using var popout = await client.GetAsync("/app/?mode=popout&conversation=c1");
        (await popout.Content.ReadAsStringAsync()).Should().Be(html, "查詢字串不影響內容");
    }

    [Theory]
    [InlineData("/app/", "no-store")]
    [InlineData("/app/app.js", "no-cache")]
    [InlineData("/app/app.css", "no-cache")]
    public async Task app路徑的回應帶同一份CSP與安全標頭(string path, string cache)
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        csp.Should().Be(SecurityHeaderRules.ContentSecurityPolicy("https://localhost"));
        csp.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith("script-src ", StringComparison.Ordinal)).Should().Be("script-src 'self'");
        response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        response.Headers.GetValues("X-Frame-Options").Single().Should().Be("DENY");
        response.Headers.GetValues("Cross-Origin-Opener-Policy").Single().Should().Be("same-origin");
        response.Headers.GetValues("Referrer-Policy").Single().Should().Be("no-referrer");
        response.Headers.CacheControl!.ToString().Should().Be(cache);
        response.Headers.ETag.Should().NotBeNull();
    }

    [Fact]
    public async Task 匿名一律深色()
    {
        await using var host = await HubTestHost.StartAsync();
        await SetThemeAsync(host, "light");
        using var client = host.CreateClient(cookies: false);
        var html = await client.Http.GetStringAsync("/app/");
        RootTheme(html).Should().Be("dark", "匿名不讀偏好");

        // 帶了不合格的 cookie 也一樣
        using var request = new HttpRequestMessage(HttpMethod.Get, "/app/");
        request.Headers.Add("Cookie", "__Host-myriagenthub=garbage");
        using var response = await client.Http.SendAsync(request);
        RootTheme(await response.Content.ReadAsStringAsync()).Should().Be("dark");
    }

    [Fact]
    public async Task 已登入且偏好淺色時給淺色()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        RootTheme(await client.Http.GetStringAsync("/app/")).Should().Be("dark", "還沒有偏好時是預設的深色");

        await SetThemeAsync(host, "light");
        var light = await client.Http.GetStringAsync("/app/");
        RootTheme(light).Should().Be("light");
        light.Should().Contain(AssetServerRules.Anchor, "淺色版除了主題其他相同");

        await SetThemeAsync(host, "dark");
        RootTheme(await client.Http.GetStringAsync("/app/")).Should().Be("dark");
    }

    [Fact]
    public async Task 偏好system時不替換()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        await SetThemeAsync(host, "system");
        RootTheme(await client.Http.GetStringAsync("/app/")).Should().Be("dark", "system 交給網頁的 theme.js");
    }

    [Fact]
    public async Task ETag相同回304且HEAD沒有本文()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        string etag;
        long length;
        using (var first = await client.GetAsync("/app/app.js"))
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            etag = first.Headers.ETag!.Tag;
            length = (await first.Content.ReadAsByteArrayAsync()).Length;
            first.Content.Headers.ContentLength.Should().Be(length);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/app/app.js"))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var response = await client.Http.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.NotModified);
            response.Headers.ETag!.Tag.Should().Be(etag);
            (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/app/app.js"))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", "\"other\"");
            using var response = await client.Http.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK, "ETag 不同照常回內容");
        }

        using (var request = new HttpRequestMessage(HttpMethod.Head, "/app/app.js"))
        {
            using var response = await client.Http.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentLength.Should().Be(length);
            (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty("HEAD 不回本文");
        }
    }

    [Theory]
    [InlineData("/app/app.js", "no-cache")]
    [InlineData("/app/conversation.js", "no-cache")]
    [InlineData("/app/app.css", "no-cache")]
    [InlineData("/app/", "no-store")]
    public async Task 依AcceptEncoding給預先壓縮的版本_解回原檔_ETag分開但互相命中(string path, string cache)
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        await WaitCompressedAsync(client, path);
        async Task<(HttpResponseMessage Response, byte[] Body)> Get(string? acceptEncoding, string? ifNoneMatch = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (acceptEncoding is not null)
                request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
            if (ifNoneMatch is not null)
                request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            var response = await client.Http.SendAsync(request);
            return (response, await response.Content.ReadAsByteArrayAsync());
        }

        var (plain, raw) = await Get(null);
        plain.Content.Headers.ContentEncoding.Should().BeEmpty("沒有 Accept-Encoding 給原檔");
        plain.Headers.Vary.Should().Contain("Accept-Encoding");
        var (br, brBody) = await Get("gzip, deflate, br, zstd");
        var (gz, gzBody) = await Get("gzip, deflate");
        br.Content.Headers.ContentEncoding.Should().Equal("br");
        gz.Content.Headers.ContentEncoding.Should().Equal("gzip");
        foreach (var r in new[] { plain, br, gz })
        {
            r.StatusCode.Should().Be(HttpStatusCode.OK);
            r.Headers.CacheControl!.ToString().Should().Be(cache);
            r.Headers.Vary.Should().Contain("Accept-Encoding");
        }
        br.Content.Headers.ContentLength.Should().Be(brBody.Length);
        brBody.Length.Should().BeLessThan(gzBody.Length).And.BeLessThan(raw.Length / 2);
        Decompress(new System.IO.Compression.BrotliStream(new MemoryStream(brBody), System.IO.Compression.CompressionMode.Decompress)).Should().Equal(raw);
        Decompress(new System.IO.Compression.GZipStream(new MemoryStream(gzBody), System.IO.Compression.CompressionMode.Decompress)).Should().Equal(raw);
        new[] { plain.Headers.ETag!.Tag, br.Headers.ETag!.Tag, gz.Headers.ETag!.Tag }.Should().OnlyHaveUniqueItems("不同的內容編碼是不同的表示");

        // 拿 br 的 ETag 來問原檔（或反過來）也是 304：回應帶這次會給的那一版的 ETag
        var (notModified, empty) = await Get(null, br.Headers.ETag.Tag);
        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
        notModified.Headers.ETag!.Tag.Should().Be(plain.Headers.ETag.Tag);
        empty.Should().BeEmpty();
        var (brNotModified, _) = await Get("br", plain.Headers.ETag.Tag);
        brNotModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
        brNotModified.Headers.ETag!.Tag.Should().Be(br.Headers.ETag.Tag);
        foreach (var r in new[] { plain, br, gz, notModified, brNotModified })
            r.Dispose();
    }

    [Fact]
    public async Task 插入的wwwroot腳本也從記憶體給壓縮版_快取政策照舊()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!File.Exists(Path.Combine(root, AssetServerRules.TransportFileName)))
            return;
        var library = "/lib/" + Directory.GetFiles(Path.Combine(root, "lib"), "signalr-*.min.js").Select(Path.GetFileName).Single();
        foreach (var (path, cache) in new[] { (AssetServerRules.TransportScript, "no-cache"), (library, "public, max-age=31536000, immutable") })
        {
            await WaitCompressedAsync(client, path);
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "br");
            using var response = await client.Http.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            response.Content.Headers.ContentEncoding.Should().Equal(["br"], path);
            response.Content.Headers.ContentType!.ToString().Should().Be("text/javascript; charset=utf-8");
            response.Headers.CacheControl!.ToString().Should().Be(cache, path);
            var body = Decompress(new System.IO.Compression.BrotliStream(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), System.IO.Compression.CompressionMode.Decompress));
            body.Should().Equal(File.ReadAllBytes(Path.Combine(root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar))), "內容和 wwwroot 的檔逐位元組相同（程式庫的雜湊釘選照樣成立）");
        }
    }

    [Fact]
    public async Task index的modulepreload涵蓋app_js靜態import到的模組_而且都在app裡()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        var html = await client.Http.GetStringAsync("/app/");
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", AssetServerRules.TransportFileName)))
            return;
        var preloads = ModulePreload().Matches(html).Select(m => m.Groups[1].Value).ToList();
        preloads.Should().NotBeEmpty().And.OnlyHaveUniqueItems().And.NotContain("app.js");
        preloads.Should().Contain(["store.js", "transport.js", "conversation.js"], "app.js 直接或間接 import 的模組");
        html.IndexOf("modulepreload", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf(AssetServerRules.Anchor, StringComparison.Ordinal));
        foreach (var module in preloads)
        {
            using var response = await client.GetAsync("/app/" + module);
            response.StatusCode.Should().Be(HttpStatusCode.OK, module);
        }
    }

    [Fact]
    public async Task 背景壓縮完之後每個文字檔都有Brotli與gzip版_解回原檔()
    {
        var catalog = AssetServer.Load(typeof(HubApplication).Assembly, new NullFileProvider(), NullLogger.Instance);
        await catalog.Precompressed.WaitAsync(TimeSpan.FromSeconds(60));
        var files = catalog.Files.Values.Append(catalog.IndexDark!).Append(catalog.IndexLight!).ToList();
        files.Where(f => f.Name.EndsWith(".js", StringComparison.Ordinal) || f.Name.EndsWith(".css", StringComparison.Ordinal)).Should().OnlyContain(f => f.Compressible);
        foreach (var file in files.Where(f => f.Compressible))
        {
            file.Compressed.Should().NotBeNull(file.Name);
            file.Compressed!.Brotli.Should().NotBeNull(file.Name);
            Decompress(new System.IO.Compression.BrotliStream(new MemoryStream(file.Compressed.Brotli!), System.IO.Compression.CompressionMode.Decompress))
                .Should().Equal(file.Content, file.Name);
        }
        AssetFile.Create("x.png", new byte[4096], "image/png", "no-cache").Compressible.Should().BeFalse("PNG 本身已壓縮");
        AssetFile.Create("x.js", new byte[100], "text/javascript; charset=utf-8", "no-cache").Compressible.Should().BeFalse("太小");
    }

    /// <summary>主機啟動後在背景壓縮：等到這個檔有 Brotli 版（同一個程序壓過的會立刻有）</summary>
    private static async Task WaitCompressedAsync(HubClient client, string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, path);
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "br");
            using var response = await client.Http.SendAsync(request);
            if (response.Content.Headers.ContentEncoding.Contains("br"))
                return;
            DateTime.UtcNow.Should().BeBefore(deadline, $"{path} 一直沒有壓縮版");
            await Task.Delay(50);
        }
    }

    [GeneratedRegex("<link rel=\"modulepreload\" href=\"([^\"]+)\">")]
    private static partial Regex ModulePreload();

    private static byte[] Decompress(Stream stream)
    {
        using (stream)
        using (var output = new MemoryStream())
        {
            stream.CopyTo(output);
            return output.ToArray();
        }
    }

    [Fact]
    public async Task js與css回no_cache與強ETag()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        foreach (var (path, type) in new[] { ("/app/app.js", "text/javascript; charset=utf-8"), ("/app/app.css", "text/css; charset=utf-8") })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            response.Content.Headers.ContentType!.ToString().Should().Be(type);
            response.Headers.CacheControl!.ToString().Should().Be("no-cache");
            response.Headers.ETag!.IsWeak.Should().BeFalse("強 ETag");
            var body = await response.Content.ReadAsByteArrayAsync();
            response.Headers.ETag.Tag.Should().Be(AssetServerRules.ETag(body), "ETag 是內容的 SHA-256");
        }
    }

    [Theory]
    [InlineData("/app/nope.js")]
    [InlineData("/app/.hidden")]
    [InlineData("/app/app.exe")]
    public async Task 不認得的名稱回404(string path)
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.CacheControl!.ToString().Should().Be("no-store");
    }

    [Fact]
    public async Task 子路徑回404()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        foreach (var path in new[] { "/app/a/b.js", "/app/lib/app.js", "/app//app.js" })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        }
    }

    [Fact]
    public async Task POST回405()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        foreach (var path in new[] { "/app/", "/app/app.js", "/app" })
        {
            using var response = await client.PostAsync(path, new { });
            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, path);
            response.Content.Headers.Allow.Should().BeEquivalentTo(new[] { "GET", "HEAD" });
        }
    }

    [Fact]
    public void 嵌入的資源名稱都是平的且與桌面Web資料夾一致()
    {
        var web = RepoFiles.Combine("src", "Myriagent", "Web");
        Directory.GetDirectories(web).Should().BeEmpty("嵌入的名稱是平的：子資料夾的檔案會互相覆蓋");
        var files = Directory.GetFiles(web).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToList();
        files.Should().Contain("index.html").And.Contain("app.js");
        var resources = typeof(HubApplication).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(AssetServerRules.ResourcePrefix, StringComparison.Ordinal))
            .Select(n => n[AssetServerRules.ResourcePrefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToList();
        resources.Should().Equal(files, "嵌入的資源要與桌面 Web 資料夾一致");
        files.Should().OnlyContain(f => AssetServerRules.ContentType(f) != null, "每個檔案都要有內容類型（不然 /app/ 不會提供）");

        var catalog = AssetServer.Load(typeof(HubApplication).Assembly, new NullFileProvider(), NullLogger.Instance);
        catalog.Problem.Should().BeNull();
        catalog.Files.Keys.Should().BeEquivalentTo(files.Where(f => f != "index.html"));
        foreach (var name in catalog.Files.Keys)
        {
            var bytes = File.ReadAllBytes(Path.Combine(web, name));
            catalog.Files[name].Content.Should().Equal(bytes, $"{name} 原樣供應");
        }
    }

    [Fact]
    public async Task 登入頁與網頁版的圖示都是匿名可取的app_favicon()
    {
        File.ReadAllText(RepoFiles.Combine("src", "MyriagentHub", "wwwroot", "auth.html"))
            .Should().Contain("<link rel=\"icon\" href=\"/app/favicon.ico\">", "登入頁沿用網頁版的圖示");
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        using var response = await client.GetAsync("/app/favicon.ico");
        response.StatusCode.Should().Be(HttpStatusCode.OK, "登入頁還沒登入就要用到");
        response.Content.Headers.ContentType!.ToString().Should().Be("image/x-icon");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(File.ReadAllBytes(RepoFiles.Combine("src", "Myriagent", "Web", "favicon.ico")));
    }

    [Fact]
    public void 嵌入的index_html正好有一個錨點且沒有inline_script()
    {
        var html = ReadIndex();
        AssetServerRules.CountAnchors(html).Should().Be(1);
        var scripts = ScriptTag().Matches(html);
        scripts.Should().NotBeEmpty();
        foreach (Match script in scripts)
        {
            script.Groups["attrs"].Value.Should().Contain("src=", "不可以有 inline script");
            script.Groups["body"].Value.Should().BeEmpty("script 標籤不能有內容");
        }
        EventAttribute().IsMatch(html).Should().BeFalse("不可以有 on* 事件屬性");
        ExternalResource().IsMatch(html).Should().BeFalse("不從外站載入腳本或樣式");
        html.Should().NotContain("javascript:");
    }

    [Fact]
    public void repo的wwwroot注入組合合法()
    {
        // WP3.5 放錯檔案（多個程式庫、有 transport-hub.js 卻沒有程式庫）時雲端 CI 在這裡就紅
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.Exists(root).Should().BeTrue();
        var catalog = AssetServer.Load(typeof(HubApplication).Assembly, new PhysicalFileProvider(root), NullLogger.Instance);
        catalog.Problem.Should().BeNull();
        catalog.IndexDark.Should().NotBeNull();
        catalog.IndexLight.Should().NotBeNull();
    }

    [Fact]
    public async Task WP3_5合併前不插入任何腳本()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        var html = await client.Http.GetStringAsync("/app/");
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (File.Exists(Path.Combine(root, AssetServerRules.TransportFileName)))
        {
            // WP3.5 合併之後：wwwroot 有 transport-hub.js，兩個腳本依序插在 app.js 之前
            html.IndexOf("/lib/signalr-", StringComparison.Ordinal).Should().BeGreaterThan(0);
            html.IndexOf(AssetServerRules.TransportScript, StringComparison.Ordinal).Should().BeLessThan(html.IndexOf(AssetServerRules.Anchor, StringComparison.Ordinal));
            return;
        }
        html.Should().NotContain(AssetServerRules.TransportScript).And.NotContain("/lib/");
        html.Should().Be(ReadIndex(), "沒有要插的腳本時與嵌入的原檔相同");
    }

    [Fact]
    public void wwwroot有兩個腳本時依序插入()
    {
        var root = TempDir.Create("myriagenthub-wwwroot");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "lib"));
            File.WriteAllText(Path.Combine(root, "lib", "signalr-10.0.13.min.js"), "// lib");
            File.WriteAllText(Path.Combine(root, "lib", "README.txt"), "x");
            File.WriteAllText(Path.Combine(root, "transport-hub.js"), "// transport");
            using var provider = new PhysicalFileProvider(root);
            var catalog = AssetServer.Load(typeof(HubApplication).Assembly, provider, NullLogger.Instance);
            catalog.Problem.Should().BeNull();

            foreach (var index in new[] { catalog.IndexDark!, catalog.IndexLight! })
            {
                var html = Encoding.UTF8.GetString(index.Content);
                var library = html.IndexOf("<script src=\"/lib/signalr-10.0.13.min.js\"></script>", StringComparison.Ordinal);
                var transport = html.IndexOf("<script src=\"/transport-hub.js\"></script>", StringComparison.Ordinal);
                var app = html.IndexOf(AssetServerRules.Anchor, StringComparison.Ordinal);
                library.Should().BeGreaterThan(0);
                transport.Should().BeGreaterThan(library, "程式庫先載入");
                app.Should().BeGreaterThan(transport, "傳輸層在 app.js 之前掛好");
                index.ETag.Should().Be(AssetServerRules.ETag(index.Content));
            }
            RootTheme(Encoding.UTF8.GetString(catalog.IndexDark!.Content)).Should().Be("dark");
            RootTheme(Encoding.UTF8.GetString(catalog.IndexLight!.Content)).Should().Be("light");
        }
        finally
        {
            TempDir.TryDelete(root);
        }
    }

    [Fact]
    public async Task wwwroot組合不合法時app回503且其他端點照常()
    {
        var root = TempDir.Create("myriagenthub-wwwroot");
        try
        {
            File.WriteAllText(Path.Combine(root, "transport-hub.js"), "// transport");
            using var provider = new PhysicalFileProvider(root);
            var catalog = AssetServer.Load(typeof(HubApplication).Assembly, provider, NullLogger.Instance);
            catalog.Problem.Should().NotBeNull("有 transport-hub.js 卻沒有程式庫");
            catalog.IndexDark.Should().BeNull();

            var nextCalls = new List<string>();
            var server = new AssetServer(context =>
            {
                nextCalls.Add(context.Request.Path.Value!);
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            }, catalog, new PrefsThemeSource(null!, NullLogger<PrefsThemeSource>.Instance), NullLogger.Instance);

            foreach (var path in new[] { "/app/", "/app/app.js" })
            {
                var context = Request(path);
                await server.InvokeAsync(context);
                context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable, path);
                context.Response.Headers.CacheControl.ToString().Should().Be("no-store");
                context.Response.ContentType.Should().Be("text/plain; charset=utf-8");
                Body(context).Should().Contain("伺服器記錄");
            }

            foreach (var path in new[] { "/", "/healthz", "/auth.js", "/apps/x", "/lib/signalr-10.0.13.min.js" })
            {
                var context = Request(path);
                await server.InvokeAsync(context);
                context.Response.StatusCode.Should().Be(StatusCodes.Status200OK, $"{path} 交給後面的中介程式");
            }
            nextCalls.Should().Equal("/", "/healthz", "/auth.js", "/apps/x", "/lib/signalr-10.0.13.min.js");

            // 轉址與方法檢查不受影響
            var redirect = Request("/app");
            await server.InvokeAsync(redirect);
            redirect.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        }
        finally
        {
            TempDir.TryDelete(root);
        }
    }

    [Fact]
    public void wwwroot沒有app資料夾也沒有app檔案()
    {
        // /app/* 一律由 AssetServer 供應（它在靜態檔之前）；wwwroot 放同名的東西只會讓人誤以為改得到
        foreach (var root in new[] { RepoFiles.Combine("src", "MyriagentHub", "wwwroot"), Path.Combine(AppContext.BaseDirectory, "wwwroot") })
        {
            Directory.Exists(Path.Combine(root, "app")).Should().BeFalse(root);
            File.Exists(Path.Combine(root, "app")).Should().BeFalse(root);
            File.Exists(Path.Combine(root, "index.html")).Should().BeFalse($"{root} 的 index.html 會與 /app/ 的網頁混淆");
        }
    }

    private static async Task SetThemeAsync(HubTestHost host, string theme)
    {
        var writer = host.Services.GetRequiredService<SyncWriter>();
        var catalog = host.Services.GetRequiredService<Catalog>();
        var outcome = await writer.WriteAsync(ctx => catalog.SetPreference(ctx, "owner", "theme", new JValue(theme), null));
        outcome.Rejected.Should().BeNull();
    }

    private static string ReadIndex()
    {
        using var stream = typeof(HubApplication).Assembly.GetManifestResourceStream(AssetServerRules.ResourcePrefix + AssetServerRules.IndexName)!;
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    private static string? RootTheme(string html) => RootThemeAttribute().Match(html) is { Success: true } m ? m.Groups[1].Value : null;

    private static DefaultHttpContext Request(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string Body(HttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    [GeneratedRegex("<html[^>]*\\sdata-theme=\"([a-z]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex RootThemeAttribute();

    [GeneratedRegex(@"<script(?<attrs>[^>]*)>(?<body>[\s\S]*?)</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();

    [GeneratedRegex(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttribute();

    [GeneratedRegex(@"(src|href)\s*=\s*[""']?(https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalResource();
}
