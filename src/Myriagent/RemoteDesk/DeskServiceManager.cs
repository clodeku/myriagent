using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Update;

namespace Myriagent.RemoteDesk;

/// <summary>app 帶的那一份服務檔（和 app 同一個資料夾）</summary>
internal sealed record DeskServiceBundle(string Version, string Build, string Payload);

/// <summary>鎖定畫面支援目前的樣子（背景執行緒查出來的快照）</summary>
/// <param name="Installed">裝在這台的（安裝紀錄；沒裝是 null）</param>
/// <param name="Bundled">app 帶的（服務檔不完整是 null：不能安裝）</param>
/// <param name="Hello">服務的回覆（連不上是 null：沒裝、沒在執行）</param>
/// <param name="Probe">服務看到的輸入桌面（沒問、問不到是 null）</param>
internal sealed record DeskServiceStatus(DeskServiceInstall? Installed, DeskServiceBundle? Bundled, DeskServiceHello? Hello, DeskServiceProbe? Probe);

/// <summary>
/// 遠端桌面的鎖定畫面支援（服務 <c>MyriagentDesk</c>）在 app 這一端的管理：查狀態、以 UAC 啟動安裝器。
/// 真的碰 Program Files、管道與 UAC；中樞的測試以假的換掉。兩個方法都會等，只在背景執行緒呼叫
/// </summary>
internal interface IDeskServiceManager
{
    DeskServiceStatus Query(bool probe);

    /// <summary>
    /// 以系統管理員執行安裝器（<paramref name="verb"/> 是 install 或 uninstall）：Windows 跳出 UAC，使用者同意才執行。
    /// 回安裝器的結束碼（<see cref="DeskServiceExit"/>）；使用者在 UAC 按了否回 <see cref="DeskServiceManager.Declined"/>
    /// </summary>
    int RunElevated(string verb);
}

internal sealed class DeskServiceManager(string appDirectory) : IDeskServiceManager
{
    /// <summary>使用者沒有同意 UAC（或啟動不了安裝器）</summary>
    public const int Declined = -1;

    /// <summary>安裝器沒有在時間內結束</summary>
    public const int TimedOut = -2;

    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(3);

    private readonly object gate = new();
    private string? bundleSignature;
    private DeskServiceBundle? bundle;

    public DeskServiceStatus Query(bool probe)
    {
        var installed = ReadInstalled();
        var bundled = ReadBundled();
        DeskServiceHello? hello = null;
        DeskServiceProbe? seen = null;
        // 沒裝就不必連（管道不存在時連線要等到逾時）
        if (installed is not null)
        {
            try
            {
                using var client = DeskServiceClient.Connect(DeskServiceRules.PipeNameOf(DeskServiceRules.ServiceName), TimeSpan.FromSeconds(2));
                hello = client.Hello();
                if (probe && hello is { } h && h.Features.Contains(DeskServiceRules.FeatureProbe))
                    seen = client.Probe();
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log.Info("鎖定畫面支援的服務連不上：" + ex.Message);
            }
        }
        return new DeskServiceStatus(installed, bundled, hello, seen);
    }

    public int RunElevated(string verb)
    {
        if (verb is not ("install" or "uninstall"))
            throw new ArgumentOutOfRangeException(nameof(verb));
        var exe = Path.Combine(appDirectory, DeskServiceRules.ExeName);
        try
        {
            // runas＝要求系統管理員（UAC）；已經是系統管理員時直接執行。主控台視窗藏起來
            using var process = Process.Start(new ProcessStartInfo(exe, verb)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = appDirectory,
            });
            if (process is null)
                return Declined;
            if (!process.WaitForExit((int)InstallerTimeout.TotalMilliseconds))
                return TimedOut;
            return process.ExitCode;
        }
        catch (Win32Exception ex)
        {
            // 1223＝使用者在 UAC 按了否；其他（找不到檔、被原則擋下）也一樣沒有執行
            Log.Info($"鎖定畫面支援的安裝器沒有執行（{verb}）：{ex.NativeErrorCode} {ex.Message}");
            return Declined;
        }
    }

    /// <summary>這台有沒有裝服務（只看安裝紀錄在不在；很便宜，擷取迴圈也用它決定要不要試著連）</summary>
    public static bool IsInstalled()
    {
        try
        {
            return File.Exists(InstallFilePath());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string InstallFilePath()
        => Path.Combine(DeskServiceRules.InstallRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), DeskServiceRules.ServiceName), DeskServiceRules.InstallFile);

    private static DeskServiceInstall? ReadInstalled()
    {
        try
        {
            var path = InstallFilePath();
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 64 * 1024)
                return null;
            return DeskServiceRules.ParseInstall(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>app 帶的服務檔的指紋（檔案沒變就不重算：以每個檔的大小與修改時間當鍵）</summary>
    private DeskServiceBundle? ReadBundled()
    {
        try
        {
            var signature = new StringBuilder();
            foreach (var name in DeskServiceRules.PayloadFiles)
            {
                var info = new FileInfo(Path.Combine(appDirectory, name));
                if (!info.Exists || info.Length > DeskServiceRules.MaxPayloadFileBytes)
                    return null;
                signature.Append(name).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }

            lock (gate)
            {
                if (bundle is not null && bundleSignature == signature.ToString())
                    return bundle;
            }

            var hashes = DeskServiceRules.PayloadFiles.ToDictionary(n => n,
                n => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(appDirectory, n)))).ToLowerInvariant(), StringComparer.Ordinal);
            var fresh = new DeskServiceBundle(AppVersionInfo.VersionText, AppVersionInfo.Current.Build, DeskServiceRules.PayloadHash(hashes));
            lock (gate)
            {
                bundle = fresh;
                bundleSignature = signature.ToString();
            }
            return fresh;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// 連到服務的管道（<see cref="DeskServiceRules"/> 的協定：一行要求、一行回覆）。以「識別」層級連線：服務只需要知道我們是誰，
/// 不讓它以我們的身分做事。每個呼叫都有逾時；服務回錯誤丟 <see cref="InvalidOperationException"/>
/// </summary>
internal sealed class DeskServiceClient : IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    private readonly NamedPipeClientStream pipe;
    private readonly MemoryStream pending = new();
    private readonly byte[] chunk = new byte[4096];

    private DeskServiceClient(NamedPipeClientStream pipe) => this.pipe = pipe;

    public static DeskServiceClient Connect(string pipeName, TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect((int)timeout.TotalMilliseconds);
            return new DeskServiceClient(pipe);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public DeskServiceHello Hello()
    {
        var reply = Call(DeskServiceRules.HelloRequest());
        return DeskServiceRules.ParseHello(reply) ?? throw Failure(reply);
    }

    public DeskServiceProbe Probe()
    {
        var reply = Call(DeskServiceRules.ProbeRequest());
        return DeskServiceRules.ParseProbe(reply) ?? throw Failure(reply);
    }

    /// <summary>請服務在這個工作階段開（或沿用）擷取用的幫手；回幫手的管道名稱</summary>
    public string Open()
    {
        var reply = Call(DeskServiceRules.OpenRequest());
        return DeskServiceRules.ParseOpen(reply) ?? throw Failure(reply);
    }

    /// <summary>
    /// 向幫手要一張畫面：ok 時畫面（BGRA，<c>OutW×OutH×4</c> 個位元組）寫進 <paramref name="bgra"/>；unchanged、failed 不動它。
    /// 回覆對不上要求的大小丟 <see cref="IOException"/>（之後的資料對不齊了，這條連線不能再用）
    /// </summary>
    public DeskServiceCaptureHeader Capture(DeskServiceCaptureRequest request, byte[] bgra)
    {
        var bytes = DeskServiceRules.CaptureBytes(request.OutW, request.OutH);
        if (bgra.Length < bytes)
            throw new ArgumentException("緩衝太小", nameof(bgra));
        using var timeout = new CancellationTokenSource(CallTimeout);
        try
        {
            var reply = Call(DeskServiceRules.CaptureRequest(request), timeout.Token);
            if (DeskServiceRules.ErrorOf(reply) is { } error)
                throw new InvalidOperationException($"{error.Code}：{error.Message}");
            var header = DeskServiceRules.ParseCaptureReply(reply, bytes) ?? throw new IOException("幫手的回覆和要求的大小對不上");
            if (header.Bytes > 0)
                ReadExact(bgra, header.Bytes, timeout.Token);
            return header;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("幫手沒有在時間內回覆");
        }
    }

    private static InvalidOperationException Failure(string reply)
    {
        var error = DeskServiceRules.ErrorOf(reply);
        return new InvalidOperationException(error is null ? "服務的回覆看不懂" : $"{error.Code}：{error.Message}");
    }

    /// <summary>送一行、等一行</summary>
    internal string Call(string request)
    {
        using var timeout = new CancellationTokenSource(CallTimeout);
        try
        {
            return Call(request, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("服務沒有在時間內回覆");
        }
    }

    private string Call(string request, CancellationToken ct)
    {
        pipe.WriteAsync(Encoding.UTF8.GetBytes(request + "\n"), ct).AsTask().GetAwaiter().GetResult();
        pipe.FlushAsync(ct).GetAwaiter().GetResult();
        while (true)
        {
            var buffer = pending.GetBuffer();
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, (int)pending.Length);
            if (newline >= 0)
            {
                var line = Encoding.UTF8.GetString(buffer, 0, newline).TrimEnd('\r');
                var rest = (int)pending.Length - newline - 1;
                Buffer.BlockCopy(buffer, newline + 1, buffer, 0, rest);
                pending.SetLength(rest);
                return line;
            }

            if (pending.Length > DeskServiceRules.MaxLineChars * 3)
                throw new IOException("服務的回覆太長");
            var read = pipe.ReadAsync(chunk, ct).AsTask().GetAwaiter().GetResult();
            if (read <= 0)
                throw new IOException("服務關掉了連線");
            pending.Seek(0, SeekOrigin.End);
            pending.Write(chunk, 0, read);
        }
    }

    /// <summary>讀正好 <paramref name="count"/> 個位元組（那一行後面緊接的資料）：先拿讀那一行時多讀進來的，不夠的再從管道讀</summary>
    private void ReadExact(byte[] target, int count, CancellationToken ct)
    {
        var have = (int)Math.Min(pending.Length, count);
        if (have > 0)
        {
            var buffer = pending.GetBuffer();
            Buffer.BlockCopy(buffer, 0, target, 0, have);
            var rest = (int)pending.Length - have;
            Buffer.BlockCopy(buffer, have, buffer, 0, rest);
            pending.SetLength(rest);
        }

        while (have < count)
        {
            var read = pipe.ReadAsync(target.AsMemory(have, count - have), ct).AsTask().GetAwaiter().GetResult();
            if (read <= 0)
                throw new IOException("服務關掉了連線");
            have += read;
        }
    }

    public void Dispose() => pipe.Dispose();
}
