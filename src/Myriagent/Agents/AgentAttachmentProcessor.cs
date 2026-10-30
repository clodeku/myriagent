using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Myriagent.Media;

namespace Myriagent.Agents;

/// <summary>
/// 把使用者選的檔案（或貼上的圖片）複製進對話的附件資料夾，並準備給模型看的圖：
/// 圖片太大、格式不收、內容與副檔名不符或不完整就另存 JPEG 縮圖（含 EXIF 轉正）、影片以 ffmpeg 均勻擷取畫面；文件只複製。
/// </summary>
internal static class AgentAttachmentProcessor
{
    private const long JpegQuality = 85L;

    /// <summary>擷取單張影片畫面的逾時（壞檔可能讓 ffmpeg 卡住）</summary>
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>建立存檔時遇到同名檔（被同時匯入的附件搶先）最多重新挑幾次名字</summary>
    private const int MaxCreateAttempts = 100;

    public static async Task<AgentAttachment> ImportFileAsync(string sourcePath, string attachmentDirectory, string modelImageDirectory, CancellationToken ct)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists)
            throw new FileNotFoundException($"找不到檔案：{sourcePath}");
        if (info.Length > AgentAttachmentRules.MaxAttachmentBytes)
            throw new InvalidOperationException($"「{info.Name}」超過 {AgentAttachmentRules.FormatSize(AgentAttachmentRules.MaxAttachmentBytes)}，請改把檔案放在工作資料夾，在訊息中告訴代理路徑");

        Directory.CreateDirectory(attachmentDirectory);
        var attachment = new AgentAttachment
        {
            FileName = info.Name,
            Kind = AgentAttachmentRules.Classify(info.Name),
            SizeBytes = info.Length,
        };
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true))
            await using (var target = CreateStoredFile(attachmentDirectory, info.Name))
            {
                attachment.StoredPath = target.Name;
                await source.CopyToAsync(target, ct);
            }
            await PrepareAsync(attachment, modelImageDirectory, ct);
            return attachment;
        }
        catch
        {
            DeletePartial(attachment, modelImageDirectory);
            throw;
        }
    }

    /// <summary>網頁送來的檔案內容（語音訊息的錄音）：種類照檔名（AgentAttachmentRules.Classify）</summary>
    public static async Task<AgentAttachment> ImportBytesAsync(byte[] bytes, string fileName, string attachmentDirectory, string modelImageDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(attachmentDirectory);
        var attachment = new AgentAttachment
        {
            Kind = AgentAttachmentRules.Classify(fileName),
            SizeBytes = bytes.LongLength,
        };
        try
        {
            await using (var target = CreateStoredFile(attachmentDirectory, fileName))
            {
                attachment.StoredPath = target.Name;
                attachment.FileName = Path.GetFileName(target.Name);
                await target.WriteAsync(bytes, ct);
            }
            await PrepareAsync(attachment, modelImageDirectory, ct);
            return attachment;
        }
        catch
        {
            DeletePartial(attachment, modelImageDirectory);
            throw;
        }
    }

    /// <summary>剪貼簿貼上的圖片</summary>
    public static async Task<AgentAttachment> ImportImageBytesAsync(byte[] bytes, string fileName, string attachmentDirectory, string modelImageDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(attachmentDirectory);
        var attachment = new AgentAttachment
        {
            Kind = AgentAttachmentKind.Image,
            SizeBytes = bytes.LongLength,
        };
        try
        {
            await using (var target = CreateStoredFile(attachmentDirectory, fileName))
            {
                attachment.StoredPath = target.Name;
                attachment.FileName = Path.GetFileName(target.Name);
                await target.WriteAsync(bytes, ct);
            }
            await PrepareAsync(attachment, modelImageDirectory, ct);
            return attachment;
        }
        catch
        {
            DeletePartial(attachment, modelImageDirectory);
            throw;
        }
    }

    /// <summary>
    /// 在附件資料夾建立新檔（CreateNew，絕不覆寫別的附件）。同一批附件是平行匯入的：同名檔可能在挑名字時還不存在、
    /// 建立前卻被另一個匯入搶先，這時重新挑名字（MakeStoredFileName 會看到它而改用「名稱 (2)」）
    /// </summary>
    private static FileStream CreateStoredFile(string attachmentDirectory, string fileName)
    {
        for (int attempt = 1; ; attempt++)
        {
            var path = Path.Combine(attachmentDirectory, AgentAttachmentRules.MakeStoredFileName(fileName, n => File.Exists(Path.Combine(attachmentDirectory, n))));
            try
            {
                return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            }
            catch (IOException) when (attempt < MaxCreateAttempts && File.Exists(path))
            {
            }
        }
    }

    /// <summary>
    /// 匯入到一半失敗（磁碟滿、來源被拔除、取消）：刪掉這次建立的複本與給模型看的圖，不留下不屬於任何附件、畫面上也看不到的殘檔。
    /// StoredPath 要等這次匯入建好檔案才設定，建檔前就失敗時不會刪到同名的別的附件；給模型的圖都以附件 Id 命名
    /// （影片擷取到一半、還沒加進 ModelImagePaths 的畫面也一併刪掉）
    /// </summary>
    private static void DeletePartial(AgentAttachment attachment, string modelImageDirectory)
    {
        if (attachment.StoredPath.Length == 0)
            return;
        var files = attachment.ModelImagePaths.Append(attachment.StoredPath).ToList();
        try
        {
            if (Directory.Exists(modelImageDirectory))
                files.AddRange(Directory.EnumerateFiles(modelImageDirectory, attachment.Id + "*"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        foreach (var path in files.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[AgentAttachmentProcessor] 刪除匯入失敗的殘檔失敗 {path}：{ex.Message}");
            }
        }
    }

    private static async Task PrepareAsync(AgentAttachment attachment, string modelImageDirectory, CancellationToken ct)
    {
        switch (attachment.Kind)
        {
            case AgentAttachmentKind.Image:
                await Task.Run(() => PrepareImage(attachment, modelImageDirectory), ct);
                break;
            case AgentAttachmentKind.Video:
                await PrepareVideoAsync(attachment, modelImageDirectory, ct);
                break;
        }
    }

    private static void PrepareImage(AgentAttachment attachment, string modelImageDirectory)
    {
        string? contentType = null;
        try
        {
            using var stream = File.OpenRead(attachment.StoredPath);
            contentType = DetectContentType(stream);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            int orientation = GetExifOrientation(image);
            if (orientation <= 1 && !AgentAttachmentRules.NeedsModelCopy(attachment.StoredPath, contentType, attachment.SizeBytes, image.Width, image.Height))
            {
                // 上面只讀了標頭：原圖直接給模型前先完整解碼一次，資料損毀的圖送出去 API 會拒絕整輪
                new Bitmap(image).Dispose();
                attachment.ModelImagePaths = new List<string> { attachment.StoredPath };
                return;
            }

            Directory.CreateDirectory(modelImageDirectory);
            var target = Path.Combine(modelImageDirectory, $"{attachment.Id}.jpg");
            using var oriented = (Image)image.Clone();
            ApplyExifOrientation(oriented, orientation);
            var (width, height) = AgentAttachmentRules.FitWithin(oriented.Width, oriented.Height, AgentAttachmentRules.ModelImageMaxEdge);
            using var bitmap = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);   // 透明背景存 JPEG 會變黑，先鋪白
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(oriented, 0, 0, width, height);
            }
            SaveJpeg(bitmap, target);
            attachment.ModelImagePaths = new List<string> { target };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // GDI+ 讀不了（WebP、損毀檔）或縮圖失敗（超大尺寸配不出點陣圖時是 OverflowException）：完整的 WebP 照原樣給，其他只給路徑
            System.Diagnostics.Debug.WriteLine($"[AgentAttachmentProcessor] 圖片處理失敗 {attachment.StoredPath}：{ex.Message}");
            attachment.ModelImagePaths = AgentAttachmentRules.CanSendUndecoded(contentType, attachment.SizeBytes)
                ? UseUndecodedImage(attachment, modelImageDirectory)
                : new List<string>();
        }
    }

    /// <summary>讀檔頭與檔尾判斷內容的格式（見 <see cref="AgentAttachmentRules.DetectImageMediaType"/>），讀完移回開頭</summary>
    private static string? DetectContentType(FileStream stream)
    {
        Span<byte> head = stackalloc byte[12];
        Span<byte> tail = stackalloc byte[12];
        int headLength = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Seek(-Math.Min(tail.Length, stream.Length), SeekOrigin.End);
        int tailLength = stream.ReadAtLeast(tail, tail.Length, throwOnEndOfStream: false);
        stream.Position = 0;
        return AgentAttachmentRules.DetectImageMediaType(head[..headLength], tail[..tailLength], stream.Length);
    }

    /// <summary>GDI+ 讀不了的 WebP 照原樣給模型；副檔名不是 .webp（例如網站的 WebP 存成 .jpg）就另存一份 .webp，送出時才會宣告對的 media_type</summary>
    private static List<string> UseUndecodedImage(AgentAttachment attachment, string modelImageDirectory)
    {
        if (AgentAttachmentRules.GetClaudeImageMediaType(attachment.StoredPath) == "image/webp")
            return new List<string> { attachment.StoredPath };
        try
        {
            Directory.CreateDirectory(modelImageDirectory);
            var copy = Path.Combine(modelImageDirectory, $"{attachment.Id}.webp");
            File.Copy(attachment.StoredPath, copy, overwrite: true);
            return new List<string> { copy };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private static async Task PrepareVideoAsync(AgentAttachment attachment, string modelImageDirectory, CancellationToken ct)
    {
        try
        {
            var duration = await FfmpegTool.ProbeDurationAsync(attachment.StoredPath, ct);
            if (duration is > 0)
                attachment.DurationSeconds = duration;

            Directory.CreateDirectory(modelImageDirectory);
            var seconds = AgentAttachmentRules.GetFrameSeconds(attachment.DurationSeconds);
            for (int i = 0; i < seconds.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(modelImageDirectory, $"{attachment.Id}_{i + 1}.jpg");
                if (await FfmpegTool.ExtractFrameAsync(attachment.StoredPath, seconds[i], target, AgentAttachmentRules.VideoFrameMaxEdge, FrameTimeout, ct))
                {
                    attachment.ModelImagePaths.Add(target);
                    attachment.FrameSeconds.Add(seconds[i]);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[AgentAttachmentProcessor] 影片擷取畫面失敗 {attachment.StoredPath}：{ex.Message}");
        }
    }

    private static int GetExifOrientation(Image image)
    {
        const int OrientationId = 0x0112;
        try
        {
            if (image.PropertyIdList.Contains(OrientationId))
            {
                var item = image.GetPropertyItem(OrientationId);
                if (item?.Value is { Length: >= 2 } value)
                    return BitConverter.ToUInt16(value, 0);
            }
        }
        catch (ArgumentException)
        {
        }
        return 1;
    }

    private static void ApplyExifOrientation(Image image, int orientation)
    {
        var flip = orientation switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (flip != RotateFlipType.RotateNoneFlipNone)
            image.RotateFlip(flip);
    }

    private static void SaveJpeg(Image image, string path)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
        image.Save(path, codec, parameters);
    }
}
