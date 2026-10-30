using System.Reflection;
using System.Text;
using Myriagent.UI;
using MyriagentHub.Auth;
using MyriagentHub.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace MyriagentHub.Web;

/// <summary>
/// 一個要供應的檔案（內容、類型、ETag、快取政策都在啟動時算好）。壓縮版（<see cref="Compressed"/>）由 <see cref="AssetCatalog.Precompressed"/>
/// 在背景補上：還沒補上、或沒有那一版（不是文字類型、太小、壓完沒有變小）時給原檔
/// </summary>
internal sealed record AssetFile(string Name, byte[] Content, string ContentType, string ETag, string CacheControl)
{
    /// <summary>值得壓縮（文字類型而且不太小）：回應一律帶 Vary: Accept-Encoding，壓縮版補上前後才一致</summary>
    public bool Compressible { get; } = Content.Length >= AssetServerRules.MinCompressBytes && AssetServerRules.IsCompressible(ContentType);

    /// <summary>背景壓好的版本（null＝還沒壓好或不壓）</summary>
    public AssetVariants? Compressed { get => Volatile.Read(ref compressed); set => Volatile.Write(ref compressed, value); }

    private AssetVariants? compressed;

    public static AssetFile Create(string name, byte[] content, string contentType, string cacheControl) =>
        new(name, content, contentType, AssetServerRules.ETag(content), cacheControl);
}

/// <summary>壓縮版（各自 null＝沒有那一版）</summary>
internal sealed record AssetVariants(byte[]? Brotli, byte[]? Gzip);

/// <summary>
/// 啟動時載入的瀏覽器介面：<see cref="Files"/> 是 index.html 以外的檔案；index.html 有深色（插入腳本後原樣）與淺色兩個版本。
/// <see cref="RootFiles"/> 是插進 index.html 的 wwwroot 腳本（以請求路徑為鍵，例如 /transport-hub.js）：也從記憶體供應壓縮版。
/// <see cref="Problem"/> 不是 null 時（錨點不對、wwwroot 的腳本組合不合法）/app/* 一律 503，其他端點照常
/// </summary>
internal sealed record AssetCatalog(IReadOnlyDictionary<string, AssetFile> Files, AssetFile? IndexDark, AssetFile? IndexLight, string? Problem,
    IReadOnlyDictionary<string, AssetFile>? RootFiles = null)
{
    /// <summary>背景的預先壓縮（<see cref="StartPrecompress"/> 之前是已完成的工作）；測試等它做完再比對壓縮版</summary>
    public Task Precompressed { get; private set; } = Task.CompletedTask;

    /// <summary>同一個程序裡壓過的內容（以 ETag＝內容的 SHA-256 為鍵）：測試一次啟動上百個主機，同一份網頁只壓一次</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, AssetVariants> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// 在執行緒集區壓縮全部的檔案（Brotli 品質 11 約 1 MB 要零點幾秒到一秒多：不能擋服務啟動與 /readyz）。大的先壓；壓好一個就換上一個
    /// </summary>
    public AssetCatalog StartPrecompress(ILogger log)
    {
        var all = Files.Values.Concat(RootFiles?.Values ?? []).Append(IndexDark).Append(IndexLight)
            .OfType<AssetFile>().Where(f => f.Compressible).OrderByDescending(f => f.Content.Length).ToList();
        Precompressed = Task.Run(() =>
        {
            var started = Environment.TickCount64;
            try
            {
                foreach (var file in all)
                {
                    file.Compressed = Cache.GetOrAdd(file.ETag, _ =>
                    {
                        var (brotli, gzip) = AssetServerRules.Precompress(file.Content, file.ContentType);
                        return new AssetVariants(brotli, gzip);
                    });
                }
                log.LogInformation("瀏覽器介面（/app/）預先壓縮完成：{Count} 個檔案，{Raw} KB → Brotli {Brotli} KB，{Ms} ms",
                    all.Count, all.Sum(f => f.Content.Length) / 1024, all.Sum(f => (f.Compressed?.Brotli ?? f.Content).Length) / 1024, Environment.TickCount64 - started);
            }
            catch (Exception ex)
            {
                // 壓不成就一直給原檔（功能不受影響）
                log.LogError(ex, "瀏覽器介面（/app/）預先壓縮失敗，改給原檔");
            }
        });
        return this;
    }
}

/// <summary>
/// 在 /app/ 供應嵌入的桌面網頁（設計 14.6；web-protocol 第六節第 1、2 點）。是中介程式不是端點：放在安全標頭與 5xx 計數之後、
/// 靜態檔之前（wwwroot 蓋不掉 /app/*；安全標頭與 CSP 照樣由最外層套上）。匿名可下載（設計 8.3），只有 index.html 依擁有者的漫遊偏好給淺色版。
/// 文字類型依 Accept-Encoding 給啟動時預先壓好的 Brotli／gzip 版（Vary: Accept-Encoding；壓縮版的 ETag 加 .br／.gz）。
/// </summary>
internal sealed class AssetServer
{
    private const string ProblemText = "瀏覽器介面的設定有誤，請看伺服器記錄";

    private readonly RequestDelegate next;
    private readonly AssetCatalog catalog;
    private readonly PrefsThemeSource themes;
    private readonly ILogger log;

    public AssetServer(RequestDelegate next, IWebHostEnvironment env, PrefsThemeSource themes, ILogger<AssetServer> log)
        : this(next, Load(typeof(AssetServer).Assembly, env.WebRootFileProvider, log), themes, log)
    {
    }

    /// <summary>測試直接給載入結果（不經主機的 wwwroot）</summary>
    internal AssetServer(RequestDelegate next, AssetCatalog catalog, PrefsThemeSource themes, ILogger log)
    {
        this.next = next;
        this.catalog = catalog;
        this.themes = themes;
        this.log = log;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (path is not null && catalog.RootFiles?.GetValueOrDefault(path) is { } rootFile
            && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            await ServeAsync(context, rootFile);
            return;
        }
        if (path is null || !(path == AssetServerRules.Root || path.StartsWith(AssetServerRules.Prefix, StringComparison.Ordinal)))
        {
            await next(context);
            return;
        }

        var response = context.Response;
        var method = context.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            response.Headers.Allow = "GET, HEAD";
            response.Headers.CacheControl = "no-store";
            return;
        }
        if (path == AssetServerRules.Root)
        {
            // 網頁以相對網址載入 app.css、app.js：一定要有結尾斜線（302：之後還能搬）
            response.StatusCode = StatusCodes.Status302Found;
            response.Headers.Location = AssetServerRules.Prefix + context.Request.QueryString.Value;
            response.Headers.CacheControl = "no-store";
            return;
        }
        if (catalog.Problem is not null)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            response.ContentType = "text/plain; charset=utf-8";
            response.Headers.CacheControl = "no-store";
            if (HttpMethods.IsGet(method))
                await response.WriteAsync(ProblemText, context.RequestAborted);
            return;
        }

        var name = AssetServerRules.ResolveName(context.Request.Path);
        AssetFile? file = name switch
        {
            null => null,
            AssetServerRules.IndexName => await IsLightAsync(context) ? catalog.IndexLight : catalog.IndexDark,
            _ => catalog.Files.GetValueOrDefault(name),
        };
        if (file is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            response.Headers.CacheControl = "no-store";
            return;
        }
        await ServeAsync(context, file);
    }

    /// <summary>回一個檔案（GET／HEAD）：選壓縮版、ETag 命中回 304</summary>
    private static async Task ServeAsync(HttpContext context, AssetFile file)
    {
        var response = context.Response;
        var variants = file.Compressed;
        var encoding = AssetServerRules.PickEncoding(context.Request.Headers.AcceptEncoding, variants?.Brotli is not null, variants?.Gzip is not null);
        // 值得壓縮的檔案，回應依 Accept-Encoding 而不同：快取（瀏覽器、代理）要分開存（壓縮版補上之前也帶，前後一致）
        if (file.Compressible)
            response.Headers.Vary = HeaderNames.AcceptEncoding;
        response.Headers.ETag = AssetServerRules.VariantETag(file.ETag, encoding);
        response.Headers.CacheControl = file.CacheControl;
        if (AssetServerRules.IfNoneMatchHits(context.Request.Headers.IfNoneMatch, file.ETag))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        var body = encoding switch
        {
            AssetServerRules.EncodingBrotli => variants!.Brotli!,
            AssetServerRules.EncodingGzip => variants!.Gzip!,
            _ => file.Content,
        };
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = file.ContentType;
        if (encoding is not null)
            response.Headers.ContentEncoding = encoding;
        response.ContentLength = body.Length;
        if (HttpMethods.IsGet(context.Request.Method))
            await response.Body.WriteAsync(body, context.RequestAborted);
    }

    /// <summary>
    /// 啟動時載入一次：嵌入資源（app/ 開頭；類型不在白名單的略過）、wwwroot 的 lib/ 與 transport-hub.js（決定插入哪些腳本，內容也載入）、
    /// index.html 的兩個版本（插入 app.js 靜態 import 到的模組的 modulepreload）；文字類型在背景壓縮（AssetCatalog.StartPrecompress）。問題只記一次錯誤，不讓整個服務啟動失敗（部署閘門會因此回滾）
    /// </summary>
    internal static AssetCatalog Load(Assembly assembly, IFileProvider webRoot, ILogger log)
    {
        var started = Environment.TickCount64;
        var files = new Dictionary<string, AssetFile>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        byte[]? index = null;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(AssetServerRules.ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var name = resource[AssetServerRules.ResourcePrefix.Length..];
            var type = AssetServerRules.ContentType(name);
            if (type is null || name.Length == 0)
            {
                log.LogWarning("嵌入的網頁檔 {Name} 類型不在白名單，不提供", name);
                continue;
            }
            var content = ReadResource(assembly, resource);
            if (name == AssetServerRules.IndexName)
            {
                index = content;
                continue;
            }
            if (name.EndsWith(".js", StringComparison.Ordinal))
                sources[name] = Decode(content);
            files[name] = AssetFile.Create(name, content, type, AssetServerRules.CacheControl(name));
        }

        var libraries = webRoot.GetDirectoryContents(AssetServerRules.LibraryFolder)
            .Where(f => !f.IsDirectory)
            .Select(f => f.Name)
            .ToList();
        var transport = webRoot.GetFileInfo(AssetServerRules.TransportFileName);
        var (scripts, problem) = AssetServerRules.SelectInjected(libraries, transport.Exists && !transport.IsDirectory);

        var rootFiles = new Dictionary<string, AssetFile>(StringComparer.Ordinal);
        foreach (var script in scripts)
        {
            var info = webRoot.GetFileInfo(script.TrimStart('/'));
            if (!info.Exists || info.IsDirectory)
                continue;
            using var stream = info.CreateReadStream();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            rootFiles[script] = AssetFile.Create(script, buffer.ToArray(), AssetServerRules.ContentType(script)!,
                SecurityHeaderRules.CacheControlFor(script, isStaticFile: true));
        }

        AssetFile? dark = null;
        AssetFile? light = null;
        var preloads = AssetServerRules.ModulePreloads(sources);
        if (index is null)
            problem ??= "嵌入的網頁沒有 index.html";
        else
        {
            var html = Decode(index);
            var anchors = AssetServerRules.CountAnchors(html);
            if (anchors != 1)
                problem ??= $"index.html 的 app.js 錨點應該正好一個，實際 {anchors} 個";
            if (problem is null)
            {
                var injected = AssetServerRules.InjectScripts(html, scripts, preloads);
                dark = Index(injected);
                light = Index(WebAssetRules.ApplyInitialTheme(injected, dark: false));
            }
        }

        if (problem is not null)
        {
            log.LogError("瀏覽器介面（/app/）停用：{Problem}", problem);
            return new AssetCatalog(files, dark, light, problem, rootFiles);
        }
        log.LogInformation("瀏覽器介面（/app/）：{Count} 個檔案，插入的腳本 {Scripts}、預載 {Preloads} 個模組；載入 {Ms} ms",
            files.Count + 1, scripts.Count == 0 ? "（無）" : string.Join(", ", scripts), scripts.Count == 0 ? 0 : preloads.Count, Environment.TickCount64 - started);
        return new AssetCatalog(files, dark, light, problem, rootFiles).StartPrecompress(log);
    }

    /// <summary>只有明確驗過擁有者的 cookie、而且漫遊偏好是 light 才給淺色版；匿名一律深色；system 交給網頁的 theme.js</summary>
    private async Task<bool> IsLightAsync(HttpContext context)
    {
        try
        {
            var result = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
            if (!result.Succeeded)
                return false;
            // 協作人（多人協作）看他自己的介面設定
            if (result.Principal.CollaboratorId() is { } collaboratorId)
                return context.RequestServices.GetService<CollaboratorDirectory>()?.SettingsOf(collaboratorId)["theme"]?.ToString() == "light";
            return themes.GetTheme() == "light";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "瀏覽器介面判斷初始主題失敗，改給深色");
            return false;
        }
    }

    private static AssetFile Index(string html) =>
        AssetFile.Create(AssetServerRules.IndexName, new UTF8Encoding(false).GetBytes(html), AssetServerRules.ContentType(AssetServerRules.IndexName)!,
            AssetServerRules.CacheControl(AssetServerRules.IndexName));

    private static string Decode(byte[] content)
    {
        var text = new UTF8Encoding(false).GetString(content);
        return text.Length > 0 && text[0] == (char)0xFEFF ? text[1..] : text;
    }

    private static byte[] ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("讀不到嵌入資源 " + name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

internal static class AssetServerExtensions
{
    /// <summary>在 /app/ 供應嵌入的桌面網頁（HubApplication 的中介程式順序：安全標頭、5xx 計數之後，靜態檔之前）</summary>
    public static IApplicationBuilder UseHubBrowserAssets(this IApplicationBuilder app) => app.UseMiddleware<AssetServer>();
}
