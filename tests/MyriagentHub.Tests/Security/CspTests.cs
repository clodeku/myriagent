using System.Net;
using System.Text.RegularExpressions;
using MyriagentHub.Hosting;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Security;

/// <summary>
/// SEC-06（伺服器端）：嚴格 CSP 與安全標頭、網頁沒有 inline script／事件屬性／外站資源。
/// 瀏覽器實測（主控台 0 違規、inline 正向對照被擋）由 tools/hub-e2e 的 Playwright 負責。
/// </summary>
public sealed partial class CspTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/auth.html")]
    [InlineData("/auth.js")]
    [InlineData("/healthz")]
    [InlineData("/api/v1/setup/status")]
    [InlineData("/api/v1/auth/me")]
    public async Task 每個回應都帶嚴格CSP與安全標頭(string path)
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient();
        using var response = await client.GetAsync(path);
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        csp.Should().Be(SecurityHeaderRules.ContentSecurityPolicy("https://localhost"));
        var scriptSrc = csp.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith("script-src ", StringComparison.Ordinal));
        scriptSrc.Should().Be("script-src 'self'", "不准 inline、eval、外站腳本");
        csp.Should().Contain("default-src 'none'").And.Contain("frame-ancestors 'none'").And.Contain("object-src 'none'").And.Contain("base-uri 'none'");
        csp.Should().NotContain("unsafe-eval");

        response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        response.Headers.GetValues("Referrer-Policy").Single().Should().Be("no-referrer");
        response.Headers.GetValues("X-Frame-Options").Single().Should().Be("DENY");
        response.Headers.GetValues("Cross-Origin-Opener-Policy").Single().Should().Be("same-origin");
        response.Headers.GetValues("Cross-Origin-Resource-Policy").Single().Should().Be("same-origin");
        var permissions = response.Headers.GetValues("Permissions-Policy").Single();
        permissions.Should().Contain("camera=()").And.Contain("publickey-credentials-get=(self)");
        permissions.Split(',').Select(d => d.Trim()).Should().Contain("microphone=(self)", "瀏覽器版的語音訊息要在本站錄音；microphone=() 讓 getUserMedia 一律被拒")
            .And.NotContain("microphone=()");
        response.Headers.GetValues("Strict-Transport-Security").Single().Should().Be("max-age=31536000");
        response.Headers.Contains("Server").Should().BeFalse();
        var cache = response.Headers.CacheControl?.ToString();
        if (path.EndsWith(".js", StringComparison.Ordinal))
            cache.Should().Be("no-cache", "靜態檔每次以 ETag 重新驗證，避免部署後新 HTML 配舊 JS");
        else
            cache.Should().Be("no-store", "HTML 與 API 一律 no-store");
    }

    [Fact]
    public async Task 根路徑是登入頁_帶固定標記()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient();
        using var response = await client.GetAsync("/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await response.Content.ReadAsStringAsync()).Should().Contain("<meta name=\"myriagenthub-page\" content=\"auth\">");
    }

    [Fact]
    public void 網頁沒有inline_script_事件屬性_外站資源_危險API()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        files.Should().NotBeEmpty();
        foreach (var file in files.Where(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
        {
            var html = File.ReadAllText(file);
            foreach (Match script in ScriptTag().Matches(html))
                script.Groups["attrs"].Value.Should().Contain("src=", $"{Path.GetFileName(file)} 不可以有 inline script");
            InlineScriptBody().IsMatch(html).Should().BeFalse($"{Path.GetFileName(file)} 的 script 標籤不能有內容");
            EventAttribute().IsMatch(html).Should().BeFalse($"{Path.GetFileName(file)} 不可以有 on* 事件屬性");
            StyleAttribute().IsMatch(html).Should().BeFalse($"{Path.GetFileName(file)} 不用 style 屬性");
            ExternalResource().IsMatch(html).Should().BeFalse($"{Path.GetFileName(file)} 不從外站載入任何資源（不用 CDN）");
            html.Should().NotContain("javascript:");
        }
        // wwwroot/lib 是逐位元組放進來的第三方程式庫：不能改它的內容，改以固定 SHA-256 清單與下面三個測試把關
        foreach (var file in files.Where(f => f.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && !IsUnderLib(root, f)))
        {
            var js = File.ReadAllText(file);
            js.Should().NotContain("eval(").And.NotContain("new Function").And.NotContain("innerHTML").And.NotContain("outerHTML")
                .And.NotContain("insertAdjacentHTML").And.NotContain("document.write");
            ExternalUrl().IsMatch(js).Should().BeFalse($"{Path.GetFileName(file)} 不連外站");
        }
        foreach (var file in files.Where(f => f.EndsWith(".css", StringComparison.OrdinalIgnoreCase)))
            ExternalUrl().IsMatch(File.ReadAllText(file)).Should().BeFalse($"{Path.GetFileName(file)} 不載入外站字型或圖片");
    }

    /// <summary>
    /// wwwroot/lib 允許的 JS 與它的 SHA-256（WP3.5：SignalR JS 用戶端逐位元組放進來，tools/vendor-signalr/fetch-signalr.mjs 取檔與檢查）。
    /// 換版時與 fetch-signalr.mjs 的 PIN、lib/ 的檔案同一個 commit 改（tools/vendor-signalr/README.md 的換版程序）
    /// </summary>
    private static readonly Dictionary<string, string> LibraryHashes = new(StringComparer.Ordinal)
    {
        ["signalr-10.0.11.min.js"] = "97e9b97e642a72e5a470917147a2bf79f86cad829a5c6786adb10614d248bb95",
    };

    /// <summary>SignalR 程式庫裡唯一的 new Function 前面緊接的程式（取全域物件的備援：globalThis 都沒有時才會跑到）</summary>
    private const string GlobalThisFallback = "\"object\"==typeof globalThis)return globalThis;try{return this||";

    private static string LibDir() => Path.Combine(AppContext.BaseDirectory, "wwwroot", "lib");

    private static bool IsUnderLib(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        return relative.StartsWith("lib" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("lib" + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> LibraryScripts() =>
        Directory.GetFiles(LibDir(), "*.js", SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    [Fact]
    public void wwwroot_lib的JS都在固定SHA256清單()
    {
        var dir = LibDir();
        Directory.Exists(dir).Should().BeTrue("WP3.5 起 wwwroot/lib 放 SignalR 程式庫");
        Directory.GetDirectories(dir).Should().BeEmpty("lib/ 不放子資料夾");
        foreach (var file in Directory.GetFiles(dir))
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(name).ToLowerInvariant();
            extension.Should().NotBe(".map", $"lib/ 不放 source map（{name}）");
            new[] { ".mjs", ".cjs" }.Should().NotContain(extension, $"lib/ 的 JS 只能是清單裡的 .js（{name}）");
            if (extension != ".js")
                continue;
            LibraryHashes.Should().ContainKey(name, $"lib/{name} 不在固定 SHA-256 清單裡");
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)))
                .Should().Be(LibraryHashes[name], $"lib/{name} 的內容和固定的 SHA-256 不同");
        }
        foreach (var name in LibraryHashes.Keys)
            File.Exists(Path.Combine(dir, name)).Should().BeTrue($"清單裡的 lib/{name} 要存在");
    }

    [Fact]
    public void SignalR程式庫唯一的new_Function在globalThis備援分支()
    {
        var libraries = LibraryScripts().ToList();
        libraries.Should().NotBeEmpty();
        foreach (var file in libraries)
        {
            var js = File.ReadAllText(file);
            var at = js.IndexOf("new Function", StringComparison.Ordinal);
            at.Should().BeGreaterThan(0, $"{Path.GetFileName(file)} 應該正好有一處 new Function（確認讀到的是程式庫）");
            js.IndexOf("new Function", at + 1, StringComparison.Ordinal).Should().Be(-1, $"{Path.GetFileName(file)} 只能有一處 new Function");
            js[..at].Should().EndWith(GlobalThisFallback, "唯一的 new Function 要在 globalThis 的備援分支（CSP 沒有 unsafe-eval，那裡就算跑到也只會被擋）");
        }
    }

    [Fact]
    public void SignalR程式庫沒有eval與HTML注入()
    {
        var libraries = LibraryScripts().ToList();
        libraries.Should().NotBeEmpty();
        foreach (var file in libraries)
        {
            var js = File.ReadAllText(file);
            foreach (var banned in new[] { "eval(", "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write" })
                js.Should().NotContain(banned, $"{Path.GetFileName(file)} 不可以有 {banned}");
        }
    }

    [GeneratedRegex(@"<script(?<attrs>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();

    [GeneratedRegex(@"<script[^>]*>\s*[^<\s]", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScriptBody();

    [GeneratedRegex(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttribute();

    [GeneratedRegex(@"\sstyle\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex(@"(src|href)\s*=\s*[""']?(https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalResource();

    [GeneratedRegex(@"https?://(?!localhost)", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalUrl();
}
