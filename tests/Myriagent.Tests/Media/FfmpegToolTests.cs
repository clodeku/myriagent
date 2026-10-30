using System.Diagnostics;
using System.Drawing;
using Myriagent.Agents;
using Myriagent.Media;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Media;

/// <summary>需要 PATH 上有 ffmpeg 與 ffprobe 的測試：找不到時顯示為「略過」（不要寫成沒執行也顯示通過）</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (FfmpegTool.Locate() is not { Ffmpeg: not null, Ffprobe: not null })
            Skip = "需要 PATH 上有 ffmpeg 與 ffprobe（影片附件擷取畫面）";
    }
}

/// <summary>影片附件擷取畫面：找執行檔、擷取參數（純函式），以及實際呼叫本機 ffmpeg 的結果</summary>
public sealed class FfmpegToolTests : IDisposable
{
    private readonly TempDirectory temp = new("ffmpeg");

    public void Dispose() => temp.Dispose();

    [Fact]
    public void Locate_略過UNC目錄_不對外發SMB連線_磁碟根目錄也找得到()
    {
        var probed = new List<string>();

        var result = FfmpegTool.Locate(new[] { @"\\fileserver\tools;C:\a", @"//nas/bin;C:\", @"C:\tools" }, p =>
        {
            probed.Add(p);
            return p.StartsWith(@"C:\tools\", StringComparison.OrdinalIgnoreCase);
        });

        result.Should().Be((@"C:\tools\ffmpeg.exe", @"C:\tools\ffprobe.exe"));
        probed.Should().NotContain(p => p.StartsWith(@"\\") || p.StartsWith("//"));
        probed.Should().Contain(@"C:\ffmpeg.exe", "PATH 裡的「C:\\」要接成 C:\\ffmpeg.exe，不是磁碟相對路徑 C:ffmpeg.exe");
    }

    [Fact]
    public void NeedsRelocate_找不到的不快取_快取的檔案不見了也重找()
    {
        (string? Ffmpeg, string? Ffprobe) found = (@"C:\ff\ffmpeg.exe", @"C:\ff\ffprobe.exe");

        FfmpegTool.NeedsRelocate((null, null), _ => true).Should().BeTrue("還沒安裝：app 開著時才裝的，下次用到要找得到");
        FfmpegTool.NeedsRelocate((@"C:\ff\ffmpeg.exe", null), _ => true).Should().BeTrue();
        FfmpegTool.NeedsRelocate(found, _ => true).Should().BeFalse();
        FfmpegTool.NeedsRelocate(found, p => !p.EndsWith("ffprobe.exe")).Should().BeTrue("解除安裝或搬家");
    }

    [Fact]
    public void BuildExtractFrameArguments_限制長邊不放大_輸出路徑不當成序號樣式()
    {
        var args = FfmpegTool.BuildExtractFrameArguments(@"C:\a\v.mp4", 12.5, @"C:\work 50%d\f_1.jpg", 1280);

        args[^1].Should().Be(@"C:\work 50%d\f_1.jpg");
        args.Should().ContainInConsecutiveOrder("-ss", "12.5");
        args.Should().ContainInConsecutiveOrder("-i", @"C:\a\v.mp4");
        args.Should().ContainInConsecutiveOrder("-vf", "scale='min(1280,iw)':'min(1280,ih)':force_original_aspect_ratio=decrease");
        args.Should().ContainInConsecutiveOrder("-update", "1");
    }

    [FfmpegFact]
    public async Task 手機直拍影片_畫面依旋轉轉正後長邊不超過上限()
    {
        var encoded = MakeVideo(temp.Combine("src", "encoded.mp4"), 1920, 1080);
        var rotated = temp.Combine("src", "phone.mp4");
        RunFfmpeg("-display_rotation", "90", "-i", encoded, "-c", "copy", rotated);

        var frames = await ExtractFramesAsync(rotated);

        frames.Should().NotBeEmpty();
        frames.Should().OnlyContain(f => f.Width == 720 && f.Height == 1280, "編碼 1920×1080＋旋轉 90° 轉正後是 1080×1920，長邊縮到 1280、不放大寬度");
    }

    [FfmpegFact]
    public async Task 橫式影片縮到長邊上限_小影片不放大()
    {
        var landscape = await ExtractFramesAsync(MakeVideo(temp.Combine("src", "land.mp4"), 1920, 1080));
        var small = await ExtractFramesAsync(MakeVideo(temp.Combine("src", "small.mp4"), 640, 480));
        var portrait = await ExtractFramesAsync(MakeVideo(temp.Combine("src", "portrait.mp4"), 1080, 1920));

        landscape.Should().NotBeEmpty().And.OnlyContain(f => f.Width == 1280 && f.Height == 720);
        small.Should().NotBeEmpty().And.OnlyContain(f => f.Width == 640 && f.Height == 480);
        portrait.Should().NotBeEmpty().And.OnlyContain(f => f.Width == 720 && f.Height == 1280, "不帶旋轉的直式影片也限制長邊");
    }

    [FfmpegFact]
    public async Task 資料夾路徑含百分比樣式_畫面照樣寫到指定路徑()
    {
        var root = temp.Combine("work3 50%d");
        var video = MakeVideo(Path.Combine(root, "src", "clip.mp4"), 320, 240);
        var modelDirectory = Path.Combine(root, "model");

        var attachment = await AgentAttachmentProcessor.ImportFileAsync(video, Path.Combine(root, "attachments"), modelDirectory, CancellationToken.None);

        attachment.ModelImagePaths.Should().HaveCount(AgentAttachmentRules.VideoFrameCount);
        attachment.ModelImagePaths.Should().OnlyContain(p => Path.GetDirectoryName(p) == modelDirectory && File.Exists(p));
    }

    // ───── 測試資料 ─────

    private async Task<List<Size>> ExtractFramesAsync(string video)
    {
        var name = Path.GetFileNameWithoutExtension(video);
        var attachment = await AgentAttachmentProcessor.ImportFileAsync(video, temp.Combine(name, "attachments"), temp.Combine(name, "model"), CancellationToken.None);
        return attachment.ModelImagePaths.Select(p =>
        {
            using var image = Image.FromFile(p);
            return image.Size;
        }).ToList();
    }

    /// <summary>2 秒的測試畫面影片（ffmpeg 內建的 mpeg4 編碼器，所有版本都有）</summary>
    private static string MakeVideo(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RunFfmpeg("-f", "lavfi", "-i", $"testsrc=size={width}x{height}:rate=10:duration=2", "-c:v", "mpeg4", "-q:v", "5", path);
        return path;
    }

    private static void RunFfmpeg(params string[] args)
    {
        var psi = new ProcessStartInfo(FfmpegTool.Locate().Ffmpeg!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, stderr);
    }
}
