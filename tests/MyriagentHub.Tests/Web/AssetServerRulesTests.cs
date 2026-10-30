using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using MyriagentHub.Hosting;
using MyriagentHub.Web;
using Microsoft.Extensions.Primitives;

namespace MyriagentHub.Tests.Web;

/// <summary>/app/ 的純函式規則（設計 14.6；web-protocol 第六節第 1、2 點）：網址對應、類型白名單、ETag、腳本插入、快取政策</summary>
public sealed class AssetServerRulesTests
{
    private const string Anchor = "<script type=\"module\" src=\"app.js\"></script>";

    [Theory]
    [InlineData("/app/", "index.html")]
    [InlineData("/app/app.js", "app.js")]
    [InlineData("/app/app.css", "app.css")]
    [InlineData("/app/index.html", "index.html")]
    [InlineData("/app/a/b.js", null)]
    [InlineData("/app/.x", null)]
    [InlineData("/app/..", null)]
    [InlineData("/app/a:b.js", null)]
    [InlineData("/apps/x", null)]
    [InlineData("/app", null)]
    [InlineData("/APP/app.js", null)]
    [InlineData("/", null)]
    public void ResolveName(string path, string? expected) =>
        AssetServerRules.ResolveName(new PathString(path)).Should().Be(expected);

    [Fact]
    public void ResolveName_反斜線與空路徑()
    {
        AssetServerRules.ResolveName(new PathString("/app/a" + '\\' + "b.js")).Should().BeNull();
        AssetServerRules.ResolveName(PathString.Empty).Should().BeNull();
    }

    [Fact]
    public void ContentType白名單與桌面相同()
    {
        foreach (var name in new[] { "a.html", "a.css", "a.js", "a.mjs", "a.json", "a.svg", "a.png", "a.ico", "a.woff2", "a.woff", "A.JS" })
            AssetServerRules.ContentType(name).Should().NotBeNull(name);
        foreach (var name in new[] { "a.exe", "a.map", "a.txt", "a", "a.htm" })
            AssetServerRules.ContentType(name).Should().BeNull(name);
        AssetServerRules.ContentType("index.html").Should().Be("text/html; charset=utf-8");
        AssetServerRules.ContentType("app.js").Should().Be("text/javascript; charset=utf-8");
        AssetServerRules.ContentType("app.css").Should().Be("text/css; charset=utf-8");
    }

    [Fact]
    public void ETag是內容SHA256的base64url()
    {
        var content = Encoding.UTF8.GetBytes("console.log('x');");
        var etag = AssetServerRules.ETag(content);
        etag.Should().Be("\"" + Base64Url.EncodeToString(SHA256.HashData(content)) + "\"");
        etag.Should().StartWith("\"").And.EndWith("\"").And.NotContain("+").And.NotContain("/").And.NotContain("=");
        AssetServerRules.ETag(Encoding.UTF8.GetBytes("console.log('y');")).Should().NotBe(etag);
    }

    [Fact]
    public void IfNoneMatch弱比較與星號與清單()
    {
        const string etag = "\"abc\"";
        AssetServerRules.IfNoneMatchHits(new StringValues("\"abc\""), etag).Should().BeTrue();
        AssetServerRules.IfNoneMatchHits(new StringValues("W/\"abc\""), etag).Should().BeTrue("弱比較");
        AssetServerRules.IfNoneMatchHits(new StringValues("*"), etag).Should().BeTrue();
        AssetServerRules.IfNoneMatchHits(new StringValues("\"x\", \"abc\""), etag).Should().BeTrue("逗號清單");
        AssetServerRules.IfNoneMatchHits(new StringValues(["\"x\"", "W/\"abc\""]), etag).Should().BeTrue("多個標頭值");
        AssetServerRules.IfNoneMatchHits(new StringValues("\"abd\""), etag).Should().BeFalse();
        AssetServerRules.IfNoneMatchHits(new StringValues("abc"), etag).Should().BeFalse("少了引號不是同一個");
        AssetServerRules.IfNoneMatchHits(StringValues.Empty, etag).Should().BeFalse();
    }

    [Fact]
    public void InjectScripts插在app_js的module之前()
    {
        var html = "<head>\n  <link rel=\"stylesheet\" href=\"app.css\">\n  " + Anchor + "\n</head>";
        var injected = AssetServerRules.InjectScripts(html, ["/lib/signalr-10.0.13.min.js", "/transport-hub.js"]);
        injected.Should().Be("<head>\n  <link rel=\"stylesheet\" href=\"app.css\">\n  "
            + "<script src=\"/lib/signalr-10.0.13.min.js\"></script>\n  <script src=\"/transport-hub.js\"></script>\n  "
            + Anchor + "\n</head>");
        AssetServerRules.InjectScripts(html, []).Should().Be(html, "沒有要插的就原樣");
    }

    [Fact]
    public void 找不到錨點丟例外()
    {
        var act = () => AssetServerRules.InjectScripts("<script src=\"app.js\"></script>", ["/transport-hub.js"]);
        act.Should().Throw<InvalidOperationException>();
        var none = () => AssetServerRules.InjectScripts("<html></html>", []);
        none.Should().Throw<InvalidOperationException>("沒有要插的也要檢查錨點");
        AssetServerRules.CountAnchors("<html></html>").Should().Be(0);
    }

    [Fact]
    public void 錨點兩個丟例外()
    {
        var html = Anchor + "\n" + Anchor;
        AssetServerRules.CountAnchors(html).Should().Be(2);
        var act = () => AssetServerRules.InjectScripts(html, ["/transport-hub.js"]);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SelectInjected()
    {
        Check([], false, [], problem: false);
        Check(["signalr-10.0.13.min.js"], true, ["/lib/signalr-10.0.13.min.js", "/transport-hub.js"], problem: false);
        Check(["readme.txt", "signalr-10.0.13.min.js", "signalr.min.js"], true, ["/lib/signalr-10.0.13.min.js", "/transport-hub.js"], problem: false);
        Check([], true, [], problem: true);
        Check(["signalr-10.0.13.min.js"], false, [], problem: false);
        Check(["signalr-10.0.13.min.js", "signalr-10.0.14.min.js"], true, [], problem: true);
        Check(["signalr-10.0.13.min.js", "signalr-10.0.14.min.js"], false, [], problem: true);
        Check(["signalr.min.js", "signalr-10.0.min.js", "signalr-10.0.13.js", "SIGNALR-10.0.13.min.js"], true, [], problem: true);
    }

    [Theory]
    [InlineData("signalr-10.0.13.min.js", true)]
    [InlineData("signalr-1.2.3.min.js", true)]
    [InlineData("signalr.min.js", false)]
    [InlineData("signalr-10.0.min.js", false)]
    [InlineData("signalr-10.0.13.js", false)]
    [InlineData("signalr-10.0.13.min.js.map", false)]
    [InlineData("x-signalr-10.0.13.min.js", false)]
    public void IsSignalRLibraryName(string name, bool expected) => AssetServerRules.IsSignalRLibraryName(name).Should().Be(expected);

    [Theory]
    [InlineData("/lib/signalr-10.0.13.min.js", true, "public, max-age=31536000, immutable")]
    [InlineData("/LIB/signalr-10.0.13.min.js", true, "public, max-age=31536000, immutable")]
    [InlineData("/lib/other-1.2.3.js", true, "public, max-age=31536000, immutable")]
    [InlineData("/lib/x.js", true, "no-cache")]
    [InlineData("/signalr-10.0.13.min.js", true, "no-cache")]
    [InlineData("/app/app.js", true, "no-cache")]
    [InlineData("/transport-hub.js", true, "no-cache")]
    [InlineData("/auth.js", true, "no-cache")]
    [InlineData("/auth.html", true, "no-store")]
    [InlineData("/app/index.html", true, "no-store")]
    [InlineData("/lib/signalr-10.0.13.min.js", false, "no-store")]
    [InlineData("/api/v1/auth/me", false, "no-store")]
    public void CacheControlFor以請求路徑判斷(string path, bool isStaticFile, string expected) =>
        SecurityHeaderRules.CacheControlFor(path, isStaticFile).Should().Be(expected);

    [Fact]
    public void CacheControl_index不快取其他每次驗證()
    {
        AssetServerRules.CacheControl("index.html").Should().Be("no-store");
        AssetServerRules.CacheControl("app.js").Should().Be("no-cache");
        AssetServerRules.CacheControl("app.css").Should().Be("no-cache");
    }

    [Theory]
    [InlineData("gzip, deflate, br, zstd", true, true, "br")]
    [InlineData("gzip, deflate", true, true, "gzip")]
    [InlineData("br;q=0, gzip", true, true, "gzip")]
    [InlineData("br;q=0.5, gzip;q=0.8", true, true, "gzip")]
    [InlineData("br;q=0.8, gzip;q=0.8", true, true, "br")]
    [InlineData("BR", true, true, "br")]
    [InlineData("*", true, true, "br")]
    [InlineData("*;q=0", true, true, null)]
    [InlineData("gzip;q=0, *", true, true, "br")]
    [InlineData("identity", true, true, null)]
    [InlineData("br", false, true, null)]
    [InlineData("br, gzip", false, true, "gzip")]
    [InlineData("br, gzip", false, false, null)]
    [InlineData("br;q=abc, gzip", true, true, "gzip")]
    [InlineData("", true, true, null)]
    public void PickEncoding依Accept_Encoding與有沒有那一版(string header, bool hasBrotli, bool hasGzip, string? expected) =>
        AssetServerRules.PickEncoding(new StringValues(header), hasBrotli, hasGzip).Should().Be(expected);

    [Fact]
    public void PickEncoding沒有標頭給原檔() =>
        AssetServerRules.PickEncoding(StringValues.Empty, true, true).Should().BeNull();

    [Fact]
    public void 壓縮版的ETag與原檔視為同一份內容()
    {
        var etag = AssetServerRules.ETag("內容"u8);
        var br = AssetServerRules.VariantETag(etag, AssetServerRules.EncodingBrotli);
        var gz = AssetServerRules.VariantETag(etag, AssetServerRules.EncodingGzip);
        AssetServerRules.VariantETag(etag, null).Should().Be(etag);
        br.Should().Be(etag[..^1] + ".br\"").And.NotBe(etag);
        gz.Should().Be(etag[..^1] + ".gz\"");
        foreach (var sent in new[] { etag, br, gz, "W/" + br })
            AssetServerRules.IfNoneMatchHits(new StringValues(sent), etag).Should().BeTrue(sent);
        AssetServerRules.IfNoneMatchHits(new StringValues(AssetServerRules.VariantETag(AssetServerRules.ETag("別的"u8), AssetServerRules.EncodingBrotli)), etag)
            .Should().BeFalse("別的內容的壓縮版");
        AssetServerRules.IfNoneMatchHits(new StringValues(etag[..^1] + ".zz\""), etag).Should().BeFalse("不認得的後綴不是同一份");
    }

    [Fact]
    public void Precompress_文字類型壓成Brotli與gzip_解回原文_太小與非文字不壓()
    {
        var js = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 200).Select(i => $"export function f{i}() {{ return '第 {i} 個'; }}\n")));
        var (brotli, gzip) = AssetServerRules.Precompress(js, "text/javascript; charset=utf-8");
        brotli.Should().NotBeNull();
        gzip.Should().NotBeNull();
        brotli!.Length.Should().BeLessThan(gzip!.Length).And.BeLessThan(js.Length / 3);
        using (var input = new System.IO.Compression.BrotliStream(new MemoryStream(brotli), System.IO.Compression.CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            input.CopyTo(output);
            output.ToArray().Should().Equal(js);
        }
        using (var input = new System.IO.Compression.GZipStream(new MemoryStream(gzip), System.IO.Compression.CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            input.CopyTo(output);
            output.ToArray().Should().Equal(js);
        }

        AssetServerRules.Precompress(js, "image/png").Should().Be(((byte[]?)null, (byte[]?)null), "PNG 本身已壓縮");
        AssetServerRules.Precompress(Encoding.UTF8.GetBytes("a {}"), "text/css; charset=utf-8").Should().Be(((byte[]?)null, (byte[]?)null), "太小");
        AssetServerRules.IsCompressible("image/svg+xml").Should().BeTrue();
        AssetServerRules.IsCompressible("application/json; charset=utf-8").Should().BeTrue();
        AssetServerRules.IsCompressible("font/woff2").Should().BeFalse();
    }

    [Fact]
    public void ModulePreloads_沿著靜態import廣度優先_不含入口_只收存在的同資料夾檔案()
    {
        var sources = new Dictionary<string, string>
        {
            ["app.js"] = "// import { x } from './commented.js';\nimport { a } from './a.js';\nimport {\n  b,\n  c,\n} from \"./b.js\";\nimport './side.js';\nexport { d } from './d.js';\nconst s = 'import x from \"./fake.js\"';\n",
            ["a.js"] = "import { b } from './b.js';\nimport { e } from './e.js';\nimport x from '../outside.js';\nimport y from './missing.js';\n",
            ["b.js"] = "import { a } from './a.js';\nexport const b = 1;\n",
            ["e.js"] = "export function e() { return h('div', { text: './a.js' }); }\n",
            ["side.js"] = "",
            ["d.js"] = "export const d = 1;\n",
            ["commented.js"] = "",
            ["fake.js"] = "",
            ["lonely.js"] = "",
        };
        AssetServerRules.ModulePreloads(sources).Should().Equal("a.js", "b.js", "side.js", "d.js", "e.js");
        AssetServerRules.ModulePreloads(new Dictionary<string, string>()).Should().BeEmpty("沒有入口");
    }

    [Fact]
    public void InjectScripts先插modulepreload再插腳本_沒有腳本時連preload也不插()
    {
        var html = "<head>\n  " + Anchor + "\n</head>";
        AssetServerRules.InjectScripts(html, ["/lib/signalr-10.0.13.min.js", "/transport-hub.js"], ["store.js", "ui.js"]).Should().Be("<head>\n  "
            + "<link rel=\"modulepreload\" href=\"store.js\">\n  <link rel=\"modulepreload\" href=\"ui.js\">\n  "
            + "<script src=\"/lib/signalr-10.0.13.min.js\"></script>\n  <script src=\"/transport-hub.js\"></script>\n  "
            + Anchor + "\n</head>");
        AssetServerRules.InjectScripts(html, [], ["store.js"]).Should().Be(html, "傳輸層不在時 /app/ 用不了，不插");
    }

    private static void Check(string[] libs, bool transport, string[] scripts, bool problem)
    {
        var (actual, why) = AssetServerRules.SelectInjected(libs, transport);
        actual.Should().Equal(scripts, $"lib=[{string.Join(",", libs)}] transport={transport}");
        (why is not null).Should().Be(problem, $"lib=[{string.Join(",", libs)}] transport={transport} 的問題說明：{why}");
    }
}
