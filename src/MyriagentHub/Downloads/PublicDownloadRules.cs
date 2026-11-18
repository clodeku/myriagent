using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyriagentHub.Downloads;

/// <summary>
/// 放在這台給任何人下載的安裝檔（單機版，tools/setup/New-Setup.ps1 建的那一個 exe）的說明：
/// <c>downloads\myriagent\latest.json</c>，內容就是建置腳本寫出的 setup.json（file、version、size、sha256，其他欄位不看）
/// </summary>
internal sealed record PublicDownloadInfo(string File, string Version, long Size, string Sha256);

/// <summary>安裝檔下載頁（<c>/myriagent</c>）的純規則：說明檔的解析與檢查、頁面的 HTML</summary>
internal static partial class PublicDownloadRules
{
    public const string InfoFileName = "latest.json";

    /// <summary>安裝檔的大小上限（說明檔寫更大的當成不合格）：自帶 .NET 的安裝檔約 50～60 MB，留很大的餘裕</summary>
    public const long MaxSize = 512L * 1024 * 1024;

    /// <summary>說明檔本身的大小上限（位元組）</summary>
    public const int MaxInfoBytes = 16 * 1024;

    /// <summary>
    /// 解析說明檔。不是 JSON 物件、少了欄位、檔名不是單純的 <c>名稱.exe</c>（只收英數、點、底線、連字號：不能帶路徑）、
    /// 版本不是「主.次.修」、大小不合理、雜湊不是 64 個十六進位字元時回 null（當成沒有可下載的檔）
    /// </summary>
    public static PublicDownloadInfo? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxInfoBytes)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var file = Text(root, "file");
            var version = Text(root, "version");
            var sha = Text(root, "sha256");
            if (file is null || version is null || sha is null || !FileName().IsMatch(file) || !VersionText().IsMatch(version) || !Sha256Hex().IsMatch(sha))
                return null;
            if (!root.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number || !sizeValue.TryGetInt64(out var size) || size is <= 0 or > MaxSize)
                return null;
            return new PublicDownloadInfo(file, version, size, sha.ToLowerInvariant());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>給人看的大小：「52.4 MB」</summary>
    public static string SizeText(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>下載安裝檔的網址（同站的相對路徑）</summary>
    public static string DownloadPath(PublicDownloadInfo info) => PublicDownloadEndpoints.Route + "/download/" + info.File;

    /// <summary>下載頁：只有 HTML 與一段樣式（網站的 CSP 不准任何腳本）；說明檔來的字一律先跳脫</summary>
    public static string Page(PublicDownloadInfo info)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        var html = new StringBuilder(4096);
        html.Append("<!doctype html>\n<html lang=\"zh-Hant\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<meta name=\"description\" content=\"Myriagent：在一台電腦上同時使用多個 AI 程式代理帳戶的 Windows 桌面程式。免費下載。\">\n")
            .Append("<title>Myriagent 下載</title>\n<style>\n").Append(Style).Append("</style>\n</head>\n<body>\n<main>\n")
            .Append("<h1>Myriagent</h1>\n")
            .Append("<p class=\"lead\">在一台電腦上同時登入、管理多個 Claude Code、Codex、Antigravity 與 Grok Build 帳戶：")
            .Append("圖形化監看每個帳戶的用量，每個帳戶可以同時開好幾個對話，額度用完自動換下一個帳戶接著做。</p>\n")
            .Append("<p><a class=\"btn\" href=\"").Append(E(DownloadPath(info))).Append("\" download>下載 Windows 安裝檔</a></p>\n")
            .Append("<p class=\"meta\">版本 ").Append(E(info.Version)).Append("・").Append(E(SizeText(info.Size))).Append("・Windows 10／11（64 位元）・免費</p>\n")
            .Append("<h2>安裝</h2>\n<ol>\n")
            .Append("<li>執行下載的安裝檔，按「安裝」。只安裝給目前的使用者，不需要系統管理員權限，也不必另外安裝 .NET。</li>\n")
            .Append("<li>安裝檔沒有程式碼簽章：Windows 顯示「Windows 已保護您的電腦」時，按「其他資訊」再按「仍要執行」。</li>\n")
            .Append("<li>裝好後會自動開啟，之後從開始選單開啟。要用哪一家的代理，先在這台電腦安裝它的命令列工具並登入")
            .Append("（程式裡的「CLI」頁可以安裝與更新）。</li>\n</ol>\n")
            .Append("<h2>說明</h2>\n<ul>\n")
            .Append("<li>這是單機版：帳戶、對話紀錄與登入憑證都留在你的電腦上，不連任何同步伺服器。</li>\n")
            .Append("<li>需要 Microsoft Edge WebView2 Runtime：Windows 11 內建，Windows 10 通常已經隨 Edge 安裝。</li>\n")
            .Append("<li>不會自動更新。要更新時回到這一頁下載新版、再安裝一次，帳戶與對話都會保留。</li>\n")
            .Append("<li>移除：Windows 設定 → 應用程式 → 已安裝的應用程式 → Myriagent → 解除安裝。資料留在 <code>%LOCALAPPDATA%\\Myriagent</code>，要一併清掉請自己刪除那個資料夾。</li>\n")
            .Append("</ul>\n")
            .Append("<p class=\"hash\">檔名 <code>").Append(E(info.File)).Append("</code><br>SHA-256 <code>").Append(E(info.Sha256)).Append("</code></p>\n")
            .Append("</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    private const string Style = """
        :root { color-scheme: light dark; --bg: #f6f8fc; --fg: #16202e; --dim: #5b6779; --card: #fff; --line: #dfe5ee; --accent: #2563eb; --accent-fg: #fff; }
        @media (prefers-color-scheme: dark) { :root { --bg: #0b1220; --fg: #e6ecf5; --dim: #9aa7ba; --card: #111a2c; --line: #223049; --accent: #3b82f6; } }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--fg); font: 16px/1.7 "Segoe UI", "Microsoft JhengHei", system-ui, sans-serif; }
        main { max-width: 720px; margin: 0 auto; padding: 56px 24px 72px; }
        h1 { margin: 0 0 12px; font-size: 40px; letter-spacing: .01em; }
        h2 { margin: 36px 0 8px; font-size: 20px; }
        .lead { margin: 0 0 28px; font-size: 18px; color: var(--dim); }
        .btn { display: inline-block; padding: 14px 28px; border-radius: 10px; background: var(--accent); color: var(--accent-fg); font-size: 18px; font-weight: 600; text-decoration: none; }
        .btn:hover, .btn:focus-visible { filter: brightness(1.1); }
        .meta { margin: 12px 0 0; color: var(--dim); font-size: 14px; }
        ol, ul { margin: 0; padding-left: 1.4em; }
        li { margin: 6px 0; }
        code { font: 13px/1.5 Consolas, "Cascadia Mono", monospace; overflow-wrap: anywhere; }
        .hash { margin: 36px 0 0; padding: 14px 16px; border: 1px solid var(--line); border-radius: 10px; background: var(--card); color: var(--dim); font-size: 14px; }

        """;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\.exe$", RegexOptions.CultureInvariant)]
    private static partial Regex FileName();

    [GeneratedRegex(@"^[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionText();

    [GeneratedRegex(@"^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Hex();
}
