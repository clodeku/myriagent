using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Myriagent.Agents;

namespace Myriagent.Cli;

/// <summary>執行結果</summary>
internal sealed record CliRunResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    public string Combined => (Stdout + "\n" + Stderr).Trim();

    /// <summary>
    /// 執行的程序 PID（沒有啟動成功為 null）。熱更新用它判斷換版是不是 app 自己觸發的：claude update 的暫存檔名與版本鎖帶的就是這個 PID
    /// （CliInstallStabilityRules.ResolveOrigin）
    /// </summary>
    public int? ProcessId { get; init; }
}

/// <summary>不開視窗執行 CLI 子指令（版本、登入狀態、登出、更新），收集輸出；逾時結束程序樹</summary>
internal static class CliRunner
{
    /// <param name="timeout">Timeout.InfiniteTimeSpan＝不設時限（等程序自己結束）</param>
    /// <param name="killWithApp">false＝不加入 app 的工作物件，app 結束時不會跟著被結束（npm 安裝／更新半途被砍會讓 CLI 從電腦上消失）</param>
    /// <param name="started">程序一啟動就回報它的 PID（在讀輸出之前；更新指令執行期間就要能判斷換版的來源）</param>
    /// <param name="input">寫進 stdin 的文字（無 BOM 的 UTF-8、換行 \n），寫完就關；null＝一開始就關 stdin</param>
    public static async Task<CliRunResult> RunAsync(
        AgentLauncher launcher,
        IEnumerable<string> args,
        Action<IDictionary<string, string?>>? configureEnvironment,
        TimeSpan timeout,
        CancellationToken ct = default,
        string? workingDirectory = null,
        bool killWithApp = true,
        Action<int>? started = null,
        string? input = null)
    {
        var utf8 = new UTF8Encoding(false);
        var psi = new ProcessStartInfo
        {
            FileName = launcher.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            StandardInputEncoding = utf8,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            psi.WorkingDirectory = workingDirectory;
        foreach (var a in launcher.PrefixArguments.Concat(args))
            psi.ArgumentList.Add(a);
        configureEnvironment?.Invoke(psi.Environment);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            return new CliRunResult(-1, string.Empty, ex.Message, false);
        }
        if (process == null)
            return new CliRunResult(-1, string.Empty, "無法啟動程序", false);

        using (process)
        {
            int processId = process.Id;
            if (killWithApp)
                ChildProcessJob.TryAssign(process);
            try
            {
                started?.Invoke(processId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CliRunner] 回報 PID 失敗：{ex.Message}");
            }
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            // 不等寫完：程序不讀 stdin 時寫入會卡在管線緩衝區，逾時結束程序樹後管線斷掉，寫入跟著失敗
            _ = WriteInputAsync(process, input);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                }
                ct.ThrowIfCancellationRequested();
                return new CliRunResult(-1, string.Empty, "執行逾時", true) { ProcessId = processId };
            }

            // 讀完輸出也要有時限：孫程序繼承了 stdout handle 時，主程序結束後管線仍不會關
            try
            {
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            catch (TimeoutException)
            {
                return new CliRunResult(process.ExitCode, stdout.IsCompletedSuccessfully ? stdout.Result : string.Empty,
                    stderr.IsCompletedSuccessfully ? stderr.Result : "讀取輸出逾時", false) { ProcessId = processId };
            }
            return new CliRunResult(process.ExitCode, await stdout, await stderr, false) { ProcessId = processId };
        }
    }

    /// <summary>
    /// 寫入 stdin 後關閉（先開始讀輸出才寫，大段輸入不會和輸出互相卡住）。程序沒讀完就結束、被結束或已處置時忽略錯誤，照結束碼與輸出判斷
    /// </summary>
    private static async Task WriteInputAsync(Process process, string? input)
    {
        try
        {
            if (!string.IsNullOrEmpty(input))
            {
                await process.StandardInput.WriteAsync(input.Replace("\r\n", "\n").AsMemory());
                await process.StandardInput.FlushAsync();
            }
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Debug.WriteLine($"[CliRunner] 寫入 stdin 失敗：{ex.Message}");
        }
    }

    /// <summary>開新的主控台視窗執行（登入、官方安裝程式這類要使用者看到或操作的流程）；回傳程序供呼叫端等它結束</summary>
    public static Process StartConsole(AgentLauncher launcher, IEnumerable<string> args, Action<IDictionary<string, string?>>? configureEnvironment, string? title = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = launcher.FileName,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        foreach (var a in launcher.PrefixArguments.Concat(args))
            psi.ArgumentList.Add(a);
        configureEnvironment?.Invoke(psi.Environment);
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"無法開啟{title ?? "主控台"}視窗");
        process.EnableRaisingEvents = true;
        return process;
    }
}
