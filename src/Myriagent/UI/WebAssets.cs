using System.Reflection;

namespace Myriagent.UI;

/// <summary>嵌入的介面網頁（src/Myriagent/Web/ 底下所有檔案，邏輯名稱 Web/檔名）</summary>
internal static class WebAssets
{
    private static readonly Assembly Assembly = typeof(WebAssets).Assembly;

    private static readonly Lazy<HashSet<string>> Names = new(() =>
        Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Web/", StringComparison.Ordinal))
            .Select(n => n["Web/".Length..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    public static bool Exists(string name) => Names.Value.Contains(name);

    public static Stream? Open(string name)
    {
        var actual = Names.Value.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        return actual == null ? null : Assembly.GetManifestResourceStream("Web/" + actual);
    }

    /// <summary>依副檔名決定 Content-Type；不在白名單的類型不提供</summary>
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
}
