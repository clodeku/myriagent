using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Myriagent.Agents;

/// <summary>
/// 附件與媒體的純函式規則：檔案分類、給 Claude 的圖片格式、何時另存縮圖、影片擷取畫面的時間點、附在訊息後的說明文字、存檔檔名、開檔白名單。
/// Claude 的圖片區塊只收 PNG／JPEG／GIF／WebP 且單張有大小上限，其他格式或太大的圖另存 JPEG 縮圖給模型；
/// 影片兩種代理都看不了，改附均勻擷取的畫面並寫明原檔路徑。
/// </summary>
internal static class AgentAttachmentRules
{
    public const int MaxAttachmentsPerMessage = 20;

    /// <summary>單一附件上限（複製進對話資料夾）</summary>
    public const long MaxAttachmentBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>給模型看的圖超過這個大小就另存縮圖（圖片留在脈絡裡每輪重送，Claude 單次請求上限 32 MB）</summary>
    public const long ModelImageMaxBytes = 1_000_000;

    /// <summary>給模型看的圖長邊上限（1568 px 是 Claude 不再自行縮圖的最大尺寸）</summary>
    public const int ModelImageMaxEdge = 1568;

    public const int VideoFrameCount = 6;

    /// <summary>影片畫面的長邊上限（依旋轉中繼資料轉正之後；直式與橫式影片的畫面一樣大）</summary>
    public const int VideoFrameMaxEdge = 1280;

    /// <summary>存檔檔名截斷時，比這個長的「副檔名」不當副檔名（Path.GetExtension 把最後一個「.」之後全當副檔名）</summary>
    private const int MaxExtensionLength = 20;

    private static ReadOnlySpan<byte> PngSignature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>PNG 最後一個區塊：IEND 與它固定的 CRC</summary>
    private static ReadOnlySpan<byte> PngEnd => new byte[] { 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };

    private static ReadOnlySpan<byte> JpegSignature => new byte[] { 0xFF, 0xD8, 0xFF };

    /// <summary>JPEG 的影像結束標記（EOI）</summary>
    private static ReadOnlySpan<byte> JpegEnd => new byte[] { 0xFF, 0xD9 };

    /// <summary>GIF 的結尾（trailer）</summary>
    private static ReadOnlySpan<byte> GifEnd => new byte[] { 0x3B };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm", ".wmv", ".flv", ".3gp", ".mpg", ".mpeg",
    };

    /// <summary>畫面上可直接播放的影片（WebView2 的 &lt;video&gt; 支援）</summary>
    private static readonly HashSet<string> PlayableVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".webm", ".mov",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma",
        // 語音訊息的錄音（Chromium 的 MediaRecorder 錄的 WebM／Opus；VoiceMessageRules）
        ".weba",
    };

    /// <summary>可直接顯示的圖片。刻意不含 SVG：SVG 可帶腳本，經媒體端點以本頁來源載入有注入風險</summary>
    private static readonly HashSet<string> DisplayableImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".avif",
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".py", ".java", ".kt",
        ".go", ".rs", ".c", ".h", ".cpp", ".hpp", ".rb", ".php", ".swift", ".sql", ".sh", ".ps1", ".psm1", ".bat", ".cmd",
        ".html", ".htm", ".css", ".scss", ".less", ".vue", ".svelte", ".xaml", ".razor", ".cshtml", ".lua", ".dart", ".r",
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".log", ".yaml", ".yml", ".toml", ".ini",
        ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf",
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz",
    };

    /// <summary>
    /// 點連結時可以直接用預設程式開啟的副檔名（白名單：圖片、影片、音訊、文件、純文字類、壓縮檔）；
    /// 其他（執行檔、腳本、捷徑、HTML 等）一律只在檔案總管中選取，不直接開。
    /// 可帶巨集的 Office 格式（.xlsm、舊版 .doc／.xls／.ppt、.rtf）也不直接開：代理在本機寫出的檔案沒有網路來源標記，
    /// Office 不會用受保護的檢視開啟
    /// </summary>
    private static readonly HashSet<string> SafeToOpenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".ico", ".avif", ".heic",
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm", ".wmv", ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg",
        ".pdf", ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".log", ".yaml", ".yml", ".toml",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp",
        ".zip", ".7z", ".rar",
    };

    public static AgentAttachmentKind Classify(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        if (ImageExtensions.Contains(ext))
            return AgentAttachmentKind.Image;
        if (VideoExtensions.Contains(ext))
            return AgentAttachmentKind.Video;
        return AgentAttachmentKind.Document;
    }

    /// <summary>檔案瀏覽器的種類：image／video／audio／code／doc／archive／file</summary>
    public static string ClassifyForBrowser(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        if (ImageExtensions.Contains(ext) || DisplayableImageExtensions.Contains(ext) || ext.Equals(".svg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".heic", StringComparison.OrdinalIgnoreCase))
            return "image";
        if (VideoExtensions.Contains(ext))
            return "video";
        if (AudioExtensions.Contains(ext))
            return "audio";
        if (CodeExtensions.Contains(ext))
            return "code";
        if (DocumentExtensions.Contains(ext))
            return "doc";
        if (ArchiveExtensions.Contains(ext))
            return "archive";
        return "file";
    }

    public static string? GetClaudeImageMediaType(string fileName) => Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => null,
    };

    /// <summary>
    /// 依檔案內容判斷 Claude 收的圖片格式（PNG、JPEG、GIF、WebP），不認得或檔案不完整回 null。
    /// 副檔名可能與內容不符（網站的 WebP 存成 .jpg、截圖工具把 JPEG 存成 .png），宣告錯的 media_type 會讓 API 拒絕整輪；
    /// 截斷的檔案 GDI+ 照樣解得開，所以還要看結尾有沒有該格式的結束標記（PNG 的 IEND、JPEG 的 EOI、GIF 的 0x3B、WebP 的 RIFF 長度）
    /// </summary>
    /// <param name="head">檔案開頭（12 位元組才認得出 WebP）</param>
    /// <param name="tail">檔案結尾（至少 8 位元組）</param>
    /// <param name="length">檔案長度</param>
    public static string? DetectImageMediaType(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, long length)
    {
        if (head.StartsWith(PngSignature))
            return tail.EndsWith(PngEnd) ? "image/png" : null;
        if (head.StartsWith(JpegSignature))
            return tail.EndsWith(JpegEnd) ? "image/jpeg" : null;
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
            return tail.EndsWith(GifEnd) ? "image/gif" : null;
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            return length >= 8L + BinaryPrimitives.ReadUInt32LittleEndian(head[4..8]) ? "image/webp" : null;
        return null;
    }

    /// <summary>
    /// 原圖不能直接給模型、要另存縮圖：內容格式（<see cref="DetectImageMediaType"/>，不認得或不完整為 null）與路徑的副檔名不同
    /// （送出時依副檔名宣告 media_type）、檔案太大或太寬
    /// </summary>
    public static bool NeedsModelCopy(string path, string? contentType, long sizeBytes, int width, int height)
        => contentType == null
           || contentType != GetClaudeImageMediaType(path)
           || sizeBytes > ModelImageMaxBytes
           || Math.Max(width, height) > ModelImageMaxEdge;

    /// <summary>GDI+ 讀不了的圖能不能照原樣給模型：只有完整的 WebP（Claude 收、GDI+ 不支援）而且大小允許；PNG／JPEG／GIF 讀不了代表檔案損毀，只給路徑</summary>
    public static bool CanSendUndecoded(string? contentType, long sizeBytes)
        => contentType == "image/webp" && sizeBytes <= ModelImageMaxBytes;

    public static (int Width, int Height) FitWithin(int width, int height, int maxEdge)
    {
        if (width <= 0 || height <= 0)
            return (Math.Max(1, width), Math.Max(1, height));
        int longest = Math.Max(width, height);
        if (longest <= maxEdge)
            return (width, height);
        double scale = (double)maxEdge / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>影片擷取畫面的秒數：均勻分成 count 段、取每段中點；長度不明或不到 1 秒只取第 0 秒</summary>
    public static List<double> GetFrameSeconds(double? durationSeconds, int count = VideoFrameCount)
    {
        if (durationSeconds is not > 1 || count <= 1)
            return new List<double> { 0 };

        var result = new List<double>();
        for (int i = 0; i < count; i++)
            result.Add(Math.Round(durationSeconds.Value * (i + 0.5) / count, 2));
        return result;
    }

    public static string FormatSeconds(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : ts.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 附在使用者訊息後面的附件說明：每個附件的種類、檔名、本機路徑、影片的長度與擷取畫面時間點。
    /// imagesInline=false（Antigravity：stream-json 只收文字）時圖片與影片畫面不附在訊息裡，改列出路徑請代理用工具讀取
    /// </summary>
    public static string BuildAttachmentNote(IReadOnlyList<AgentAttachment> attachments, bool imagesInline = true)
    {
        if (!imagesInline)
            return BuildPathOnlyAttachmentNote(attachments);
        if (attachments == null || attachments.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("使用者附加了以下檔案（已存在這台電腦，可用工具直接讀取）：");
        int index = 1;
        foreach (var a in attachments)
        {
            switch (a.Kind)
            {
                case AgentAttachmentKind.Image:
                    sb.AppendLine(a.ModelImagePaths.Count > 0
                        ? $"{index}. 圖片「{a.FileName}」：{a.StoredPath}（圖片已附在這則訊息中）"
                        : $"{index}. 圖片「{a.FileName}」：{a.StoredPath}（無法直接附上，請用工具讀取）");
                    break;
                case AgentAttachmentKind.Video:
                {
                    var length = a.DurationSeconds.HasValue ? $"長 {FormatSeconds(a.DurationSeconds.Value)}，" : string.Empty;
                    var frames = a.ModelImagePaths.Count > 0
                        ? $"已附 {a.ModelImagePaths.Count} 張畫面（{string.Join("、", a.FrameSeconds.Select(FormatSeconds))}）"
                        : "無法擷取畫面，請自行處理原檔";
                    sb.AppendLine($"{index}. 影片「{a.FileName}」：{a.StoredPath}（{length}{frames}）");
                    break;
                }
                default:
                    sb.AppendLine($"{index}. 檔案「{a.FileName}」：{a.StoredPath}");
                    break;
            }
            index++;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>只能送文字時的附件說明：圖片給原檔路徑（太大的另給縮小的複本）、影片給原檔與擷取畫面的路徑，都請代理用工具讀取</summary>
    private static string BuildPathOnlyAttachmentNote(IReadOnlyList<AgentAttachment> attachments)
    {
        if (attachments == null || attachments.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("使用者附加了以下檔案（已存在這台電腦，請用工具讀取；圖片請用能看圖的工具開啟）：");
        int index = 1;
        foreach (var a in attachments)
        {
            switch (a.Kind)
            {
                case AgentAttachmentKind.Image:
                {
                    var copy = a.ModelImagePaths.FirstOrDefault(p => !string.Equals(p, a.StoredPath, StringComparison.OrdinalIgnoreCase));
                    sb.AppendLine(copy != null
                        ? $"{index}. 圖片「{a.FileName}」：{a.StoredPath}（縮小的複本：{copy}）"
                        : $"{index}. 圖片「{a.FileName}」：{a.StoredPath}");
                    break;
                }
                case AgentAttachmentKind.Video:
                {
                    var length = a.DurationSeconds.HasValue ? $"長 {FormatSeconds(a.DurationSeconds.Value)}" : null;
                    sb.AppendLine(length != null
                        ? $"{index}. 影片「{a.FileName}」：{a.StoredPath}（{length}）"
                        : $"{index}. 影片「{a.FileName}」：{a.StoredPath}");
                    for (int i = 0; i < a.ModelImagePaths.Count; i++)
                    {
                        var at = i < a.FrameSeconds.Count ? $"{FormatSeconds(a.FrameSeconds[i])} 的畫面" : $"第 {i + 1} 張畫面";
                        sb.AppendLine($"   - {at}：{a.ModelImagePaths[i]}");
                    }
                    break;
                }
                default:
                    sb.AppendLine($"{index}. 檔案「{a.FileName}」：{a.StoredPath}");
                    break;
            }
            index++;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>存進對話資料夾的檔名：去掉不合法字元、保留副檔名、同名加序號</summary>
    public static string MakeStoredFileName(string originalName, Func<string, bool> exists)
    {
        var name = Path.GetFileName(originalName ?? string.Empty);
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length > 120)
        {
            // 沒有副檔名、但中段有「v1.0」「2026.10.10」的長檔名，「副檔名」可能比上限還長：不當副檔名，整串截斷。
            // 截斷處的點號與空白要去掉（Windows 會吃掉檔名結尾的點號與空白，路徑就對不上）
            var ext = Path.GetExtension(name);
            if (ext.Length > MaxExtensionLength)
                ext = string.Empty;
            name = name[..(120 - ext.Length)].TrimEnd(' ', '.') + ext;
        }
        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name)))
            name = "附件" + name;

        if (!exists(name))
            return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (int i = 2; i < 10000; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (!exists(candidate))
                return candidate;
        }

        return $"{stem} ({Guid.NewGuid():N}){extension}";
    }

    public static bool IsDisplayableMedia(string path)
    {
        var ext = Path.GetExtension(path ?? string.Empty);
        return DisplayableImageExtensions.Contains(ext) || PlayableVideoExtensions.Contains(ext);
    }

    public static bool IsDisplayableImage(string path)
        => DisplayableImageExtensions.Contains(Path.GetExtension(path ?? string.Empty));

    public static bool IsPlayableVideo(string path)
        => PlayableVideoExtensions.Contains(Path.GetExtension(path ?? string.Empty));

    public static bool IsSafeToOpen(string path)
        => SafeToOpenExtensions.Contains(Path.GetExtension(path ?? string.Empty));

    /// <summary>
    /// 本機磁碟路徑（「C:\」這種磁碟代號開頭的完整路徑）。UNC（\\server\share）一律不算：
    /// 光是檢查檔案存不存在就會對該主機發起 SMB 連線、送出 Windows 登入雜湊，代理回覆裡的 UNC 路徑可能來自惡意網頁
    /// </summary>
    public static bool IsLocalDrivePath(string? path)
        => !string.IsNullOrEmpty(path)
           && path.Length >= 3
           && char.IsAsciiLetter(path[0])
           && path[1] == ':'
           && (path[2] == '\\' || path[2] == '/');

    public static string? GetMediaContentType(string path) => Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".avif" => "image/avif",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        _ => null,
    };

    /// <summary>PDF 的型別（預覽：桌面的 /media、伺服器的 /f/ 以它 inline 回應，瀏覽器內建的檢視器顯示）</summary>
    public const string PdfContentType = "application/pdf";

    /// <summary>副檔名是 .pdf（只看字串、不碰檔案系統）</summary>
    public static bool IsPdf(string? path) => Path.GetExtension(path ?? string.Empty).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / 1024.0 / 1024:0.#} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:0.##} GB";
    }
}
