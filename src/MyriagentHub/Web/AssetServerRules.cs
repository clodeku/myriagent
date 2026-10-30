using System.Buffers.Text;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Primitives;

namespace MyriagentHub.Web;

/// <summary>
/// 瀏覽器介面（/app/）的純函式規則（設計 14.6；web-protocol 第六節第 1、2 點）：網址對應到嵌入的桌面網頁、內容類型白名單、
/// 強 ETag、要插入哪些傳輸腳本（只插 wwwroot 實際存在的）與插在哪裡（index.html 唯一的 app.js module 錨點之前）。
/// </summary>
internal static partial class AssetServerRules
{
    public const string Root = "/app";

    public const string Prefix = "/app/";

    /// <summary>嵌入資源的邏輯名稱前綴（MyriagentHub.csproj：桌面 Web 資料夾的檔案以 app/檔名 嵌入）</summary>
    public const string ResourcePrefix = "app/";

    public const string IndexName = "index.html";

    /// <summary>index.html 載入網頁主程式的那一行（src/Myriagent/Web/index.html）：傳輸腳本一律插在它前面</summary>
    public const string Anchor = "<script type=\"module\" src=\"app.js\"></script>";

    public const string TransportScript = "/transport-hub.js";

    public const string TransportFileName = "transport-hub.js";

    public const string LibraryFolder = "lib";

    /// <summary>"/app/"→"index.html"、"/app/app.js"→"app.js"；名稱不可為空、不可含 / \ :、不可以 . 開頭；其他路徑（含 "/app"）回 null</summary>
    public static string? ResolveName(PathString path)
    {
        var value = path.Value;
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var name = value[Prefix.Length..];
        if (name.Length == 0)
            return IndexName;
        if (name[0] == '.' || name.AsSpan().IndexOfAny('/', '\\', ':') >= 0)
            return null;
        return name;
    }

    /// <summary>依副檔名決定 Content-Type（白名單同桌面 UI/WebAssets.ContentType）；其他類型不提供</summary>
    public static string? ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".woff" => "font/woff",
        _ => null,
    };

    /// <summary>強 ETag："\"" + base64url(SHA-256(內容)) + "\""</summary>
    public static string ETag(ReadOnlySpan<byte> content) => "\"" + Base64Url.EncodeToString(SHA256.HashData(content)) + "\"";

    /// <summary>
    /// If-None-Match 是否命中（弱比較：W/ 前綴不計；支援 * 與逗號分隔的清單）。壓縮版的 ETag（<see cref="VariantETag"/>）與原檔的視為同一份內容：
    /// 瀏覽器換了 Accept-Encoding（或經過不壓縮的代理）也照樣 304，回應帶的是這次會給的那一版的 ETag
    /// </summary>
    public static bool IfNoneMatchHits(StringValues header, string etag)
    {
        var target = StripVariant(StripWeak(etag));
        foreach (var value in header)
        {
            if (value is null)
                continue;
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part == "*" || StripVariant(StripWeak(part)) == target)
                    return true;
            }
        }
        return false;
    }

    public const string EncodingBrotli = "br";

    public const string EncodingGzip = "gzip";

    /// <summary>壓縮版的強 ETag：引號內加上 .br／.gz（「.」不在 base64url 的字母表裡，不會和雜湊本身混淆）</summary>
    public static string VariantETag(string etag, string? encoding) => encoding switch
    {
        null => etag,
        EncodingBrotli => etag[..^1] + ".br\"",
        EncodingGzip => etag[..^1] + ".gz\"",
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "不認得的內容編碼"),
    };

    /// <summary>值得預先壓縮的類型：文字（HTML、CSS、JS、JSON）與 SVG；PNG、字型本身已壓縮</summary>
    public static bool IsCompressible(string contentType) =>
        contentType.StartsWith("text/", StringComparison.Ordinal) || contentType.StartsWith("application/json", StringComparison.Ordinal)
        || contentType.StartsWith("image/svg+xml", StringComparison.Ordinal);

    /// <summary>小於這個大小的不壓（壓縮的標頭與解壓的成本比省下的多）</summary>
    public const int MinCompressBytes = 256;

    /// <summary>
    /// 啟動時預先壓縮一次（2026-10-23 以前 /app/ 一律原樣送出：43 個檔約 1 MB，gzip 後約 340 KB）：Brotli 最高品質（11、視窗 22）與 gzip 最小體積；
    /// 壓完沒有比原檔小的那一種回 null。不經 ASP.NET Core 的回應壓縮中介程式（它的 Brotli「Optimal」只有品質 4，而且每個請求都要重壓）
    /// </summary>
    public static (byte[]? Brotli, byte[]? Gzip) Precompress(byte[] content, string contentType)
    {
        if (content.Length < MinCompressBytes || !IsCompressible(contentType))
            return (null, null);
        byte[]? brotli = null;
        var buffer = new byte[BrotliEncoder.GetMaxCompressedLength(content.Length)];
        if (BrotliEncoder.TryCompress(content, buffer, out var written, quality: 11, window: 22) && written < content.Length)
            brotli = buffer[..written];
        byte[]? gzip;
        using (var output = new MemoryStream())
        {
            using (var stream = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
                stream.Write(content);
            gzip = output.Length < content.Length ? output.ToArray() : null;
        }
        return (brotli, gzip);
    }

    /// <summary>
    /// 依 Accept-Encoding 選這次要給的版本：有 Brotli 版而且接受 br（或 *）就給 br，否則有 gzip 版而且接受 gzip 就給 gzip，否則原檔（null）。
    /// q=0 表示不接受；q 值相同時偏好 br（同樣的內容比 gzip 小約兩成）
    /// </summary>
    public static string? PickEncoding(StringValues acceptEncoding, bool hasBrotli, bool hasGzip)
    {
        double? br = null, gzip = null, any = null;
        foreach (var value in acceptEncoding)
        {
            if (value is null)
                continue;
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var semi = part.IndexOf(';');
                var token = (semi < 0 ? part : part[..semi]).Trim();
                var q = 1.0;
                if (semi >= 0)
                {
                    var param = part[(semi + 1)..].Trim();
                    if (param.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                        && !double.TryParse(param[2..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out q))
                        q = 0;
                }
                if (token.Equals(EncodingBrotli, StringComparison.OrdinalIgnoreCase))
                    br = q;
                else if (token.Equals(EncodingGzip, StringComparison.OrdinalIgnoreCase))
                    gzip = q;
                else if (token == "*")
                    any = q;
            }
        }
        var brQ = hasBrotli ? br ?? any ?? 0 : 0;
        var gzipQ = hasGzip ? gzip ?? any ?? 0 : 0;
        if (brQ > 0 && brQ >= gzipQ)
            return EncodingBrotli;
        if (gzipQ > 0)
            return EncodingGzip;
        return null;
    }

    /// <summary>SignalR 的 JS 程式庫：<c>signalr-主.次.修.min.js</c>（版本號不寫死，由 WP3.5 決定）</summary>
    public static bool IsSignalRLibraryName(string fileName) => SignalRLibrary().IsMatch(fileName);

    /// <summary>
    /// 要插入的腳本：wwwroot/lib 恰好一個程式庫而且有 transport-hub.js → [/lib/…, /transport-hub.js]（程式庫在前）；
    /// 都沒有、或只有程式庫 → []；有 transport-hub.js 但沒有程式庫、或程式庫不只一個 → ([], 問題說明)
    /// </summary>
    public static (IReadOnlyList<string> Scripts, string? Problem) SelectInjected(IReadOnlyList<string> libFileNames, bool hasTransport)
    {
        var libraries = libFileNames.Where(IsSignalRLibraryName).Order(StringComparer.Ordinal).ToList();
        if (libraries.Count > 1)
            return ([], "wwwroot/lib 有不只一個 SignalR 程式庫：" + string.Join("、", libraries));
        if (hasTransport && libraries.Count == 0)
            return ([], "wwwroot 有 transport-hub.js 但 lib 沒有 signalr-x.y.z.min.js");
        if (hasTransport)
            return (["/" + LibraryFolder + "/" + libraries[0], TransportScript], null);
        return ([], null);
    }

    public static int CountAnchors(string html)
    {
        var count = 0;
        for (var at = html.IndexOf(Anchor, StringComparison.Ordinal); at >= 0; at = html.IndexOf(Anchor, at + Anchor.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>
    /// app.js 以靜態 import 連到的模組（不含 app.js 本身），依廣度優先第一次遇到的順序：伺服器把它們以 modulepreload 插進 index.html，
    /// 瀏覽器一開始就平行抓全部的模組，不必等 app.js 解析完才發現第二層、第二層解析完才發現第三層（4 層＝4 輪往返）。
    /// 只認行首的 import／export（多行的 import 也行）、同資料夾的相對路徑（./名稱.js）、而且是 <paramref name="sources"/> 裡有的檔
    /// </summary>
    public static IReadOnlyList<string> ModulePreloads(IReadOnlyDictionary<string, string> sources, string entry = "app.js")
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { entry };
        var order = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(entry);
        while (queue.Count > 0)
        {
            if (!sources.TryGetValue(queue.Dequeue(), out var source))
                continue;
            foreach (Match match in StaticImport().Matches(source))
            {
                var target = match.Groups["name"].Value;
                if (!sources.ContainsKey(target) || !seen.Add(target))
                    continue;
                order.Add(target);
                queue.Enqueue(target);
            }
        }
        return order;
    }

    /// <summary>
    /// 依序插在錨點前：先是 <paramref name="preloads"/> 的 <c>&lt;link rel="modulepreload" href="…"&gt;</c>，再是 <paramref name="scripts"/> 的
    /// <c>&lt;script src="…"&gt;&lt;/script&gt;</c>（傳統腳本，在 app.js 這個 module 之前執行）；縮排照錨點那一行。
    /// 沒有要插的腳本時原樣（傳輸層不在時 /app/ 本來就用不了，也不插 preload）；錨點不是正好一個就丟 <see cref="InvalidOperationException"/>
    /// </summary>
    public static string InjectScripts(string html, IReadOnlyList<string> scripts, IReadOnlyList<string>? preloads = null)
    {
        var anchors = CountAnchors(html);
        if (anchors != 1)
            throw new InvalidOperationException($"index.html 的 app.js 錨點應該正好一個，實際 {anchors} 個");
        if (scripts.Count == 0)
            return html;
        var at = html.IndexOf(Anchor, StringComparison.Ordinal);
        var lineStart = html.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        var indent = html[lineStart..at];
        if (indent.AsSpan().IndexOfAnyExcept(' ', '\t') >= 0)
            indent = string.Empty;
        var builder = new StringBuilder(html.Length + (scripts.Count + (preloads?.Count ?? 0)) * 64);
        builder.Append(html, 0, at);
        foreach (var module in preloads ?? [])
            builder.Append("<link rel=\"modulepreload\" href=\"").Append(module).Append("\">\n").Append(indent);
        foreach (var script in scripts)
            builder.Append("<script src=\"").Append(script).Append("\"></script>\n").Append(indent);
        builder.Append(html, at, html.Length - at);
        return builder.ToString();
    }

    /// <summary>index.html → no-store（部署後一定拿到新的）；其他 → no-cache（每次以 ETag 重新驗證）</summary>
    public static string CacheControl(string name) =>
        name.Equals(IndexName, StringComparison.Ordinal) ? "no-store" : "no-cache";

    private static string StripWeak(string tag) => tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;

    private static string StripVariant(string tag) =>
        tag.EndsWith(".br\"", StringComparison.Ordinal) || tag.EndsWith(".gz\"", StringComparison.Ordinal) ? tag[..^4] + "\"" : tag;

    [GeneratedRegex(@"^[ \t]*(?:import|export)\b[^'""`;]*?['""]\./(?<name>[A-Za-z0-9_.-]+\.m?js)['""]", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StaticImport();

    [GeneratedRegex(@"^signalr-[0-9]+\.[0-9]+\.[0-9]+\.min\.js$", RegexOptions.CultureInvariant)]
    private static partial Regex SignalRLibrary();
}
