using System.Diagnostics;
using System.Text;

namespace MyriagentTools.AcmeE2e;

/// <summary>
/// 測試期間啟動的子程序：標準輸出與錯誤原樣寫進記錄檔（可同時讀）；captureLines 時另外留一份文字行
/// （Pebble、challtestsrv 的記錄是 ASCII，用來找「ARI: order … is a replacement of …」這類證據）。
/// </summary>
internal sealed class ManagedProcess : IDisposable
{
    private readonly Process process;
    private readonly FileStream log;
    private readonly List<string> lines = [];
    private readonly bool captureLines;
    private readonly Task[] pumps;

    private ManagedProcess(string label, Process process, FileStream log, bool captureLines)
    {
        Label = label;
        this.process = process;
        this.log = log;
        this.captureLines = captureLines;
        pumps =
        [
            PumpAsync(process.StandardOutput.BaseStream),
            PumpAsync(process.StandardError.BaseStream),
        ];
    }

    public string Label { get; }

    public int Id => process.Id;

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode => HasExited ? (int?)process.ExitCode : null;

    public static ManagedProcess Start(
        string label,
        string exe,
        IEnumerable<string> arguments,
        string workingDirectory,
        string logPath,
        IReadOnlyDictionary<string, string>? environment,
        ChildProcessJob job,
        bool captureLines)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                info.Environment[key] = value;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var log = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var process = Process.Start(info) ?? throw new InvalidOperationException("無法啟動 " + exe);
        job.TryAdd(process);
        return new ManagedProcess(label, process, log, captureLines);
    }

    /// <summary>目前為止輸出的文字行（只有 captureLines 時）</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (lines)
                return lines.ToList();
        }
    }

    public int CountLines(Func<string, bool> predicate)
    {
        lock (lines)
            return lines.Count(predicate);
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(5));
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return true;
        }
    }

    /// <summary>強制結束（只用在 Pebble、challtestsrv 與逾時的退路）</summary>
    public void Kill()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        process.Dispose();
        lock (log)
            log.Dispose();
    }

    private async Task PumpAsync(Stream source)
    {
        var buffer = new byte[8192];
        var pending = new StringBuilder();
        try
        {
            int n;
            while ((n = await source.ReadAsync(buffer)) > 0)
            {
                lock (log)
                {
                    if (log.CanWrite)
                    {
                        log.Write(buffer, 0, n);
                        log.Flush();
                    }
                }
                if (!captureLines)
                    continue;
                pending.Append(Encoding.UTF8.GetString(buffer, 0, n));
                int newline;
                while ((newline = pending.ToString().IndexOf('\n')) >= 0)
                {
                    var line = pending.ToString(0, newline).TrimEnd('\r');
                    pending.Remove(0, newline + 1);
                    lock (lines)
                        lines.Add(line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        if (captureLines && pending.Length > 0)
        {
            lock (lines)
                lines.Add(pending.ToString());
        }
    }
}
