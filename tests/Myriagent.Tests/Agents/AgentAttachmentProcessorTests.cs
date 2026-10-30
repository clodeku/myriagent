using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 附件匯入：複製進對話資料夾、準備給模型看的圖。送出時依路徑副檔名宣告 media_type，
/// 所以給模型的圖一定要「副檔名與內容同格式、檔案完整」，否則 API 會拒絕整輪；匯入到一半失敗時不留下殘檔
/// </summary>
public sealed class AgentAttachmentProcessorTests : IDisposable
{
    /// <summary>8×8 紅色無損 WebP（ffmpeg -c:v libwebp -lossless 1 產生）；GDI+ 讀不了 WebP</summary>
    private static readonly byte[] TinyWebp = Convert.FromBase64String("UklGRhwAAABXRUJQVlA4TA8AAAAvB8ABAAcQ9Y/+ByKi/wEA");

    private readonly TempDirectory temp = new("attachments");

    public void Dispose() => temp.Dispose();

    private string AttachmentDirectory => temp.Combine("conv", "attachments");

    private string ModelDirectory => temp.Combine("conv", "model");

    private Task<AgentAttachment> ImportAsync(string fileName, byte[] bytes)
    {
        var source = temp.Combine("src", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, bytes);
        return AgentAttachmentProcessor.ImportFileAsync(source, AttachmentDirectory, ModelDirectory, CancellationToken.None);
    }

    [Theory]
    [InlineData("jpeg-named.png", "jpeg")]
    [InlineData("png-named.jpg", "png")]
    public async Task 內容與副檔名不符_給模型的圖改成副檔名與內容一致(string fileName, string actualFormat)
    {
        var bytes = actualFormat == "jpeg" ? Encode(ImageFormat.Jpeg, 800, 600) : Encode(ImageFormat.Png, 800, 600);

        var attachment = await ImportAsync(fileName, bytes);

        var modelImage = attachment.ModelImagePaths.Should().ContainSingle().Subject;
        AgentAttachmentRules.GetClaudeImageMediaType(modelImage).Should().Be(Sniff(File.ReadAllBytes(modelImage)), "送出時依副檔名宣告 media_type");
        File.ReadAllBytes(attachment.StoredPath).Should().Equal(bytes, "原檔照原樣保留");
    }

    [Fact]
    public async Task WebP存成Jpg_另存一份副檔名正確的WebP給模型()
    {
        var attachment = await ImportAsync("webp-named.jpg", TinyWebp);

        var modelImage = attachment.ModelImagePaths.Should().ContainSingle().Subject;
        AgentAttachmentRules.GetClaudeImageMediaType(modelImage).Should().Be("image/webp");
        File.ReadAllBytes(modelImage).Should().Equal(TinyWebp);
    }

    [Fact]
    public async Task 真正的WebP_GDI讀不了也直接給原圖()
    {
        var attachment = await ImportAsync("photo.webp", TinyWebp);

        attachment.ModelImagePaths.Should().Equal(attachment.StoredPath);
    }

    [Fact]
    public async Task 只有標頭正常的損毀檔_不給模型只給路徑()
    {
        var png = Encode(ImageFormat.Png, 800, 600);
        var garbage = png.Take(33).Concat(Enumerable.Range(0, 5000).Select(i => (byte)(i * 7 + 3))).ToArray();

        var attachment = await ImportAsync("garbage.png", garbage);

        attachment.ModelImagePaths.Should().BeEmpty();
        AgentAttachmentRules.BuildAttachmentNote(new[] { attachment }).Should().Contain("無法直接附上");
    }

    [Theory]
    [InlineData("truncated.png", "png")]
    [InlineData("truncated.jpg", "jpeg")]
    public async Task 截斷的檔案_不直接給原圖_改給重新編碼的完整JPEG(string fileName, string format)
    {
        var full = format == "png" ? Encode(ImageFormat.Png, 800, 600) : Encode(ImageFormat.Jpeg, 800, 600);

        var attachment = await ImportAsync(fileName, full.Take(full.Length / 2).ToArray());

        var modelImage = attachment.ModelImagePaths.Should().ContainSingle().Subject;
        modelImage.Should().NotBe(attachment.StoredPath, "GDI+ 解得開截斷的檔案，但原樣送出 API 可能拒絕");
        var bytes = File.ReadAllBytes(modelImage);
        Sniff(bytes).Should().Be("image/jpeg");
        bytes[^2..].Should().Equal(new byte[] { 0xFF, 0xD9 });
    }

    [Theory]
    [InlineData("ok.png", "png")]
    [InlineData("ok.jpg", "jpeg")]
    [InlineData("ok.gif", "gif")]
    public async Task 格式正確的小圖_直接給原圖(string fileName, string format)
    {
        var bytes = format switch
        {
            "png" => Encode(ImageFormat.Png, 800, 600),
            "gif" => Encode(ImageFormat.Gif, 300, 200),
            _ => Encode(ImageFormat.Jpeg, 800, 600),
        };

        var attachment = await ImportAsync(fileName, bytes);

        attachment.ModelImagePaths.Should().Equal(attachment.StoredPath);
    }

    [Fact]
    public async Task 太寬的圖_另存縮圖JPEG()
    {
        var attachment = await ImportAsync("wide.png", Encode(ImageFormat.Png, 3000, 1000));

        var modelImage = attachment.ModelImagePaths.Should().ContainSingle().Subject;
        modelImage.Should().EndWith(".jpg");
        using var image = Image.FromFile(modelImage);
        image.Width.Should().Be(AgentAttachmentRules.ModelImageMaxEdge);
    }

    [Fact]
    public async Task 超大尺寸圖片_GDI溢位時只給路徑_附件本身仍成功()
    {
        var attachment = await ImportAsync("huge-30000.png", MonochromePng(30000, 30000));

        attachment.ModelImagePaths.Should().BeEmpty();
        File.Exists(attachment.StoredPath).Should().BeTrue();
    }

    [Fact]
    public async Task 匯入中途取消_不留下複製到一半的檔案()
    {
        var source = temp.WriteFile(@"src\notes.txt", new string('x', 100_000));

        var act = () => AgentAttachmentProcessor.ImportFileAsync(source, AttachmentDirectory, ModelDirectory, new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(AttachmentDirectory).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(AttachmentDirectory).Should().BeEmpty();
    }

    [Fact]
    public async Task 同一批的同名檔平行匯入_各自存成不同檔名()
    {
        for (int round = 0; round < 20; round++)
        {
            var directory = temp.Combine($"att{round}");
            var first = temp.WriteFile($@"src{round}\a\notes.txt", "first");
            var second = temp.WriteFile($@"src{round}\b\notes.txt", "second");

            var results = await Task.WhenAll(
                Task.Run(() => AgentAttachmentProcessor.ImportFileAsync(first, directory, ModelDirectory, CancellationToken.None)),
                Task.Run(() => AgentAttachmentProcessor.ImportFileAsync(second, directory, ModelDirectory, CancellationToken.None)));

            results.Select(r => r.StoredPath).Should().OnlyHaveUniqueItems();
            results.Select(r => File.ReadAllText(r.StoredPath)).Should().BeEquivalentTo("first", "second");
        }
    }

    [Fact]
    public async Task 同名的貼上圖片平行匯入_不互相覆寫()
    {
        var red = Encode(ImageFormat.Png, 40, 30);
        var blue = Encode(ImageFormat.Png, 50, 30);
        for (int round = 0; round < 20; round++)
        {
            var directory = temp.Combine($"paste{round}");

            var results = await Task.WhenAll(
                Task.Run(() => AgentAttachmentProcessor.ImportImageBytesAsync(red, "貼上的圖片.png", directory, ModelDirectory, CancellationToken.None)),
                Task.Run(() => AgentAttachmentProcessor.ImportImageBytesAsync(blue, "貼上的圖片.png", directory, ModelDirectory, CancellationToken.None)));

            results.Select(r => r.StoredPath).Should().OnlyHaveUniqueItems();
            results.Select(r => r.FileName).Should().BeEquivalentTo("貼上的圖片.png", "貼上的圖片 (2).png");
            results.Select(r => Convert.ToBase64String(File.ReadAllBytes(r.StoredPath))).Should().BeEquivalentTo(Convert.ToBase64String(red), Convert.ToBase64String(blue));
        }
    }

    [Fact]
    public async Task 匯入成功_複製到附件資料夾()
    {
        var source = temp.WriteFile(@"src\報告.txt", "內容");
        var attachments = temp.Combine("conv", "attachments");

        var attachment = await AgentAttachmentProcessor.ImportFileAsync(source, attachments, Path.Combine(attachments, ".model"), CancellationToken.None);

        attachment.StoredPath.Should().Be(Path.Combine(attachments, "報告.txt"));
        File.ReadAllText(attachment.StoredPath).Should().Be("內容");
        attachment.Kind.Should().Be(AgentAttachmentKind.Document);
    }

    [Fact]
    public async Task 複製途中失敗_刪掉複製到一半的檔案()
    {
        var source = temp.WriteFile(@"src\影片.mp4", new string('x', 1 << 20));
        var attachments = temp.Combine("conv", "attachments");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await FluentActions.Awaiting(() => AgentAttachmentProcessor.ImportFileAsync(source, attachments, Path.Combine(attachments, ".model"), canceled.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        Directory.EnumerateFiles(attachments, "*", SearchOption.AllDirectories).Should().BeEmpty("失敗時不留下不屬於任何附件、畫面上也看不到的殘檔");
    }

    [Fact]
    public async Task 貼上的圖片寫入途中失敗_刪掉寫到一半的檔案()
    {
        var attachments = temp.Combine("conv", "attachments");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await FluentActions.Awaiting(() => AgentAttachmentProcessor.ImportImageBytesAsync(new byte[4096], "貼上.png", attachments, Path.Combine(attachments, ".model"), canceled.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        Directory.EnumerateFiles(attachments, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    // ───── 測試資料 ─────

    private static byte[] Encode(ImageFormat format, int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.SteelBlue);
            graphics.FillEllipse(Brushes.Orange, 10, 10, width / 2, height / 2);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, format);
        return stream.ToArray();
    }

    /// <summary>依檔頭判斷格式（測試自己的版本，不依賴受測程式）</summary>
    private static string? Sniff(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 6 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
            return "image/gif";
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            return "image/webp";
        return null;
    }

    /// <summary>全黑的單色 PNG：尺寸可以很大、檔案仍然很小（30000×30000 約 110 KB）</summary>
    private static byte[] MonochromePng(int width, int height)
    {
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 1;   // 每像素 1 位元
        header[9] = 0;   // 灰階
        using var pixels = new MemoryStream();
        using (var zlib = new ZLibStream(pixels, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + (width + 7) / 8];
            for (int y = 0; y < height; y++)
                zlib.Write(row);
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", pixels.ToArray());
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var buffer = new byte[4];
        WriteBigEndian(buffer, 0, (uint)data.Length);
        stream.Write(buffer);
        var typeAndData = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typeAndData);
        WriteBigEndian(buffer, 0, Crc32(typeAndData));
        stream.Write(buffer);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
