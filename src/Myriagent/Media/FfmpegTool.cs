using System.Diagnostics;
using System.Globalization;
using System.Text;
using Myriagent.Cli;
using Newtonsoft.Json.Linq;

namespace Myriagent.Media;

/// <summary>
/// 以本機的 ffmpeg／ffprobe 讀影片長度、擷取畫面（給模型看影片附件用）。沒有安裝就回 null／false，呼叫端改成只給路徑。
/// 找執行檔：PATH（含登錄檔的使用者／系統 PATH）；找不到不快取，app 開著時才裝的下次附加影片就找得到。
/// </summary>
internal static class FfmpegTool
{
    private static readonly object LocateGate = new();

    private static (string? Ffmpeg, string? Ffprobe) located;

    public static bool IsAvailable => Tools.Ffmpeg != null;

    /// <summary>ffmpeg.exe 的完整路徑；沒有安裝是 null（語音訊息的辨識程序以它解碼錄音，Voice/WhisperVoiceTranscriber）</summary>
    public static string? FfmpegPath => Tools.Ffmpeg;

    /// <summary>找到的執行檔：每次用到時確認還在，不在（還沒安裝、已解除安裝或搬家）就重新查找</summary>
    private static (string? Ffmpeg, string? Ffprobe) Tools
    {
        get
        {
            lock (LocateGate)
            {
                if (NeedsRelocate(located, File.Exists))
                    located = Locate();
                return located;
            }
        }
    }

    /// <summary>快取的路徑要不要重找：找不到的不快取，快取的檔案不見了也重找</summary>
    internal static bool NeedsRelocate((string? Ffmpeg, string? Ffprobe) tools, Func<string, bool> fileExists)
        => tools.Ffmpeg == null || tools.Ffprobe == null || !fileExists(tools.Ffmpeg) || !fileExists(tools.Ffprobe);

    internal static (string? Ffmpeg, string? Ffprobe) Locate()
        => Locate(new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        }, File.Exists);

    /// <summary>
    /// 純函式版本（測試用）：依 PATH 的目錄順序找 ffmpeg.exe／ffprobe.exe。目錄清單與 CLI 共用 <see cref="CliLocator.MergeSearchDirectories"/>
    /// （展開環境變數、去重、略過 UNC 目錄：檢查檔案是否存在會對外發 SMB 連線、離線時還會卡到逾時）
    /// </summary>
    internal static (string? Ffmpeg, string? Ffprobe) Locate(IEnumerable<string?> pathValues, Func<string, bool> fileExists)
    {
        var dirs = CliLocator.MergeSearchDirectories(pathValues, Array.Empty<string>());
        // 用 Path.Join：清單裡的目錄去掉了結尾的反斜線，磁碟根目錄「C:」以 Path.Combine 接檔名會變成磁碟相對路徑
        string? Find(string name) => dirs.Select(d => Path.Join(d, name)).FirstOrDefault(fileExists);
        return (Find("ffmpeg.exe"), Find("ffprobe.exe"));
    }

    /// <summary>影片長度（秒）；讀不到回 null</summary>
    public static async Task<double?> ProbeDurationAsync(string path, CancellationToken ct)
    {
        var ffprobe = Tools.Ffprobe;
        if (ffprobe == null)
            return null;
        var (exit, stdout) = await RunAsync(ffprobe, new[]
        {
            "-v", "error", "-show_entries", "format=duration", "-of", "json", path,
        }, TimeSpan.FromSeconds(20), ct);
        if (exit != 0)
            return null;
        try
        {
            var duration = (JObject.Parse(stdout)["format"] as JObject)?["duration"]?.ToString();
            return double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>擷取指定秒數的一張畫面存成 JPEG，長邊縮到 maxEdge 以內（不放大）</summary>
    public static async Task<bool> ExtractFrameAsync(string path, double seconds, string target, int maxEdge, TimeSpan timeout, CancellationToken ct)
    {
        var ffmpeg = Tools.Ffmpeg;
        if (ffmpeg == null)
            return false;
        var (exit, _) = await RunAsync(ffmpeg, BuildExtractFrameArguments(path, seconds, target, maxEdge), timeout, ct);
        return exit == 0 && File.Exists(target) && new FileInfo(target).Length > 0;
    }

    /// <summary>
    /// 擷取單張畫面的參數（純函式）。縮放濾鏡套在 ffmpeg 依旋轉中繼資料轉正之後，以 min(上限, 原尺寸)＋force_original_aspect_ratio=decrease
    /// 只限制長邊、不放大（手機直拍是編碼 1920×1080＋旋轉 90°，依編碼寬度決定縮放會把轉正後 1080 的寬放大）；
    /// -update 1 讓 image2 把輸出路徑當成單一檔名，路徑裡的「%d」不會被當成序號樣式代換
    /// </summary>
    internal static List<string> BuildExtractFrameArguments(string path, double seconds, string target, int maxEdge)
    {
        var edge = maxEdge.ToString(CultureInfo.InvariantCulture);
        return new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", path, "-frames:v", "1", "-q:v", "3",
            "-vf", $"scale='min({edge},iw)':'min({edge},ih)':force_original_aspect_ratio=decrease",
            "-update", "1",
            target,
        };
    }

    private static async Task<(int ExitCode, string Stdout)> RunAsync(string fileName, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi);
        if (process == null)
            return (-1, string.Empty);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            ct.ThrowIfCancellationRequested();
            return (-1, string.Empty);
        }
        await stderr;
        return (process.ExitCode, await stdout);
    }
}
