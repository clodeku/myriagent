using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Myriagent.Core;
using Myriagent.Launch;
using Myriagent.Remote;
using Myriagent.Sync;

namespace Myriagent.RemoteExec;

/// <summary>這台的人對高風險遠端指令的回應</summary>
internal enum RemoteExecConfirmResult
{
    Allowed,
    Denied,

    /// <summary>時間內沒有回應（當成拒絕）</summary>
    TimedOut,
}

/// <summary>要這台的人同意的內容</summary>
/// <param name="IssuerLabel">發起者（「電腦「DEMO-PC-3」」）</param>
internal sealed record RemoteExecConfirmation(string CommandId, string IssuerLabel, RemoteExecRequest Request, RemoteExecRisk Risk);

/// <summary>在這台問人（WinForms 的確認視窗；測試換成假的）</summary>
internal interface IRemoteExecConfirmer
{
    /// <summary>
    /// 跳出確認（UI 執行緒呼叫，要很快返回）：<paramref name="timeout"/> 內沒有回應回 <see cref="RemoteExecConfirmResult.TimedOut"/>；
    /// <paramref name="cancellationToken"/> 取消時關掉視窗、回 <see cref="RemoteExecConfirmResult.Denied"/>
    /// </summary>
    Task<RemoteExecConfirmResult> ConfirmAsync(RemoteExecConfirmation confirmation, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>被操作的電腦執行遠端指令（中樞的處理者交來；docs/sync-protocol.md 14.11）</summary>
internal interface IRemoteExecRunner
{
    /// <summary>執行前的檢查（UI 執行緒）：null＝可以；否則是拒絕的代碼（<see cref="RemoteResultCodes"/>）與給發起者看的說明</summary>
    (string Code, string Message)? Check(RemoteExecRequest request);

    /// <summary>開始（UI 執行緒，很快返回）：高風險的先問這台的人；輸出、狀態與結束碼以 <see cref="ExecChunk"/> 送出</summary>
    void Start(string commandId, RemoteExecRequest request, string issuerLabel);

    /// <summary>停止（任何執行緒）：發起的一方要求（<paramref name="orphaned"/> false）或伺服器不再轉送（true）；不認得的 id 不理</summary>
    void Abort(string commandId, bool orphaned);

    /// <summary>還沒結束的指令數（任何執行緒；app 自我更新不在這時自動重啟）</summary>
    int RunningCount { get; }
}

/// <summary>
/// 遠端執行指令的執行者（docs/sync-protocol.md 14.11；2026-10-25 擁有者要求「高風險例如 UAC 的操作都還是要到該電腦操作同意」）：
/// <list type="bullet">
/// <item>一律以 app 的身分（一般權限）執行；app 以系統管理員執行時整個不收（<see cref="Check"/>）——指令會有完整的系統管理員權限、略過 UAC。
/// 要系統管理員的操作由 Windows 在這台的桌面跳 UAC，只能在現場同意。</item>
/// <item><see cref="RemoteExecRiskRules"/> 判成高風險的先送 <c>status confirm</c>、跳確認視窗，3 分鐘內同意才執行，否則 <c>exit denied</c>。</item>
/// <item>每個指令一個工作物件（KILL_ON_JOB_CLOSE）：停止、逾時、app 結束都結束整個程序樹；不給輸入（讀輸入的程式讀到檔案結尾）。</item>
/// <item>輸出以專用執行緒讀（<see cref="PipeReadThread"/>）、UTF-8 解，每 100 毫秒合併成一段送出，總量超過 <see cref="RemoteExecRules.MaxOutputChars"/> 之後丟掉（送一段 truncated）。</item>
/// </list>
/// 任何執行緒都能呼叫（一把鎖）
/// </summary>
internal sealed class RemoteExecRunner : IRemoteExecRunner, IDisposable
{
    /// <summary>同時執行的上限</summary>
    public const int MaxConcurrent = 8;

    private readonly Action<ExecChunk> send;
    private readonly IRemoteExecConfirmer confirmer;
    private readonly string scriptDirectory;
    private readonly Func<bool> isElevated;
    private readonly object gate = new();
    private readonly Dictionary<string, RunningExec> running = new(StringComparer.Ordinal);
    private bool disposed;

    /// <param name="send">把一段送出去（任何執行緒；正式是同步引擎的 SubmitExecOutput）</param>
    /// <param name="scriptDirectory">PowerShell 的暫存腳本放這裡（執行完就刪）</param>
    /// <param name="isElevated">app 是不是以系統管理員執行（測試換掉；null＝真的查）</param>
    public RemoteExecRunner(Action<ExecChunk> send, IRemoteExecConfirmer confirmer, string scriptDirectory, Func<bool>? isElevated = null)
    {
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.confirmer = confirmer ?? throw new ArgumentNullException(nameof(confirmer));
        this.scriptDirectory = scriptDirectory ?? throw new ArgumentNullException(nameof(scriptDirectory));
        this.isElevated = isElevated ?? IsProcessElevated;
    }

    /// <summary>高風險的同意等多久（測試縮短）</summary>
    internal TimeSpan ConfirmTimeout { get; set; } = RemoteExecRules.ConfirmTimeout;

    public int RunningCount
    {
        get
        {
            lock (gate)
                return running.Count;
        }
    }

    public (string Code, string Message)? Check(RemoteExecRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (isElevated())
            return (RemoteResultCodes.Policy, "這台的 Clode Myriagent 以系統管理員身分執行：遠端指令會有完整的系統管理員權限、不經 UAC，所以不接受。請在那台以一般身分重新開啟 app");
        if (RemoteExecRunRules.CheckFolder(request.Cwd, Directory.Exists) is { } folder)
            return (RemoteResultCodes.Invalid, folder);
        if (request.Shell == RemoteExecRules.Pwsh && FindPwsh() is null)
            return (RemoteResultCodes.Invalid, "這台沒有安裝 PowerShell 7（pwsh），請改用 powershell 或 cmd");
        lock (gate)
        {
            if (disposed)
                return (RemoteResultCodes.Busy, "這台的 Clode Myriagent 正在結束，請稍後再試");
            if (running.Count >= MaxConcurrent)
                return (RemoteResultCodes.Busy, $"這台同時執行的遠端指令已經有 {MaxConcurrent} 個，請等前面的結束再試");
        }

        return null;
    }

    public void Start(string commandId, RemoteExecRequest request, string issuerLabel)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        ArgumentNullException.ThrowIfNull(request);
        var exec = new RunningExec(commandId, request, send);
        lock (gate)
        {
            if (disposed || !running.TryAdd(commandId, exec))
                return;
        }

        var risk = RemoteExecRiskRules.Classify(request.Shell, request.Command);
        Log.Info($"遠端執行指令 {commandId}（{issuerLabel}{(request.Label is { } label ? "，對話「" + label + "」" : string.Empty)}）：{request.Shell}"
                 + $"，資料夾 {request.Cwd ?? "（家目錄）"}，上限 {request.TimeoutSeconds} 秒{(risk is null ? string.Empty : "，高風險：" + risk.Category)}"
                 + $"：{RemoteExecRunRules.Preview(request.Command)}");
        if (risk is null)
        {
            // 寫腳本、開程序不在呼叫端（UI 執行緒）做
            _ = Task.Run(() => RunAsync(exec));
            return;
        }

        exec.Status(RemoteExecRules.StatusConfirm,
            $"高風險（{risk.Category}：{risk.Match}），等待這台電腦的人在 {(int)ConfirmTimeout.TotalMinutes} 分鐘內同意");
        Task<RemoteExecConfirmResult> asking;
        try
        {
            asking = confirmer.ConfirmAsync(new RemoteExecConfirmation(commandId, issuerLabel, request, risk), ConfirmTimeout, exec.Stopping.Token);
        }
        catch (Exception ex)
        {
            Log.Error("遠端執行指令的確認視窗開不起來", ex);
            asking = Task.FromResult(RemoteExecConfirmResult.Denied);
        }

        _ = ConfirmThenRunAsync(exec, asking);
    }

    public void Abort(string commandId, bool orphaned)
    {
        RunningExec? exec;
        lock (gate)
            running.TryGetValue(commandId, out exec);
        if (exec is null)
            return;
        Log.Info($"遠端執行指令 {commandId}：{(orphaned ? "伺服器不再轉送（發起的電腦離線太久），停止" : "發起的一方要求停止")}");
        exec.Stop(orphaned ? RemoteExecRules.ExitOrphaned : RemoteExecRules.ExitCancelled);
    }

    /// <summary>app 結束：停止所有指令（結束碼盡量送出；工作物件關掉時系統也會結束它們）</summary>
    public void Dispose()
    {
        List<RunningExec> all;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            all = running.Values.ToList();
        }

        foreach (var exec in all)
        {
            exec.Stop(RemoteExecRules.ExitAppExit);
            exec.Finish(null, RemoteExecRules.ExitAppExit, ExitText(RemoteExecRules.ExitAppExit, exec.Request));
        }
    }

    private async Task ConfirmThenRunAsync(RunningExec exec, Task<RemoteExecConfirmResult> asking)
    {
        RemoteExecConfirmResult result;
        try
        {
            result = await asking.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("遠端執行指令的確認失敗", ex);
            result = RemoteExecConfirmResult.Denied;
        }

        if (exec.StopCode is { } stopped)
        {
            Complete(exec, null, stopped, ExitText(stopped, exec.Request));
            return;
        }

        switch (result)
        {
            case RemoteExecConfirmResult.Allowed:
                Log.Info($"遠端執行指令 {exec.CommandId}：這台的人同意了");
                await RunAsync(exec).ConfigureAwait(false);
                break;
            case RemoteExecConfirmResult.Denied:
                Log.Info($"遠端執行指令 {exec.CommandId}：這台的人拒絕了");
                Complete(exec, null, RemoteExecRules.ExitDenied, "這台電腦的人拒絕了這個指令，沒有執行");
                break;
            case RemoteExecConfirmResult.TimedOut:
                Log.Info($"遠端執行指令 {exec.CommandId}：沒有人在時間內同意");
                Complete(exec, null, RemoteExecRules.ExitDenied, $"{(int)ConfirmTimeout.TotalMinutes} 分鐘內沒有人在這台電腦同意，沒有執行");
                break;
            default:
                throw new InvalidOperationException("不認得的確認結果");
        }
    }

    private async Task RunAsync(RunningExec exec)
    {
        var request = exec.Request;
        RemoteExecLaunch? launch;
        try
        {
            launch = RemoteExecRunRules.Launch(request, exec.CommandId, PowerShellPath, FindPwsh(), CmdPath, scriptDirectory);
        }
        catch (Exception ex)
        {
            Complete(exec, null, RemoteExecRules.ExitFailed, "沒有執行：" + ex.Message);
            return;
        }

        if (launch is null)
        {
            Complete(exec, null, RemoteExecRules.ExitFailed, $"這台找不到 {request.Shell}，沒有執行");
            return;
        }

        SafeKernelHandle? job = null;
        Process? process = null;
        try
        {
            if (launch.Script is { } script)
            {
                Directory.CreateDirectory(scriptDirectory);
                // Windows PowerShell 5.1 把沒有 BOM 的檔當 ANSI 讀
                File.WriteAllText(launch.ScriptPath!, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }

            var psi = new ProcessStartInfo(launch.FileName)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = request.Cwd ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (launch.RawArguments is { } raw)
                psi.Arguments = raw;
            foreach (var argument in launch.Arguments)
                psi.ArgumentList.Add(argument);
            foreach (var name in psi.Environment.Keys.Where(RemoteExecRunRules.IsStrippedVariable).ToList())
                psi.Environment.Remove(name);

            if (exec.StopCode is { } early)
            {
                Complete(exec, null, early, ExitText(early, request));
                return;
            }

            job = LaunchNative.CreateJob(killOnJobClose: true);
            process = Process.Start(psi) ?? throw new InvalidOperationException("程序沒有啟動");
            var inJob = false;
            try
            {
                inJob = job != null && LaunchNative.AssignProcessToJobObject(job, process.SafeHandle);
            }
            catch (Exception ex)
            {
                Log.Warn($"遠端執行指令的程序加入工作物件失敗：{ex.Message}");
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }

            var started = process;
            var startedJob = inJob ? job : null;
            exec.Attach(() =>
            {
                try
                {
                    if (startedJob is not null && LaunchNative.TerminateJobObject(startedJob, 1))
                        return;
                    started.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or ObjectDisposedException)
                {
                    // 已經結束
                }
            });
            exec.Status(RemoteExecRules.StatusStarted, $"開始執行（PID {process.Id}）");

            var fallback = LaunchLineSplitter.ConsoleEncoding();
            var stdout = PipeReadThread.Start($"remote-exec {process.Id} stdout",
                () => Pump(process.StandardOutput.BaseStream, new RemoteExecOutputDecoder(fallback), text => exec.Append(RemoteExecRules.KindOut, text)));
            var stderr = PipeReadThread.Start($"remote-exec {process.Id} stderr",
                () => Pump(process.StandardError.BaseStream, new RemoteExecOutputDecoder(fallback), text => exec.Append(RemoteExecRules.KindErr, text)));
            using var timeout = new System.Threading.Timer(_ => exec.Stop(RemoteExecRules.ExitTimeout), null, TimeSpan.FromSeconds(request.TimeoutSeconds), Timeout.InfiniteTimeSpan);

            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(LaunchRules.OutputDrainTimeout)).ConfigureAwait(false);
            var code = process.ExitCode;
            var stop = exec.StopCode;
            Log.Info($"遠端執行指令 {exec.CommandId} 結束：結束碼 {code}{(stop is null ? string.Empty : "（" + stop + "）")}");
            Complete(exec, code, stop, stop is null ? null : ExitText(stop, request));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn($"遠端執行指令 {exec.CommandId} 開不起來：{ex.Message}");
            Complete(exec, null, RemoteExecRules.ExitFailed, $"沒有執行：開不起來 {request.Shell}（{ex.Message}）");
        }
        finally
        {
            job?.Dispose();
            process?.Dispose();
            if (launch.ScriptPath is { } path)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"刪不掉遠端執行指令的暫存腳本 {path}：{ex.Message}");
                }
            }
        }
    }

    /// <summary>送出結束碼並從清單移除</summary>
    private void Complete(RunningExec exec, int? exitCode, string? code, string? text)
    {
        exec.Finish(exitCode, code, text);
        lock (gate)
            running.Remove(exec.CommandId);
    }

    private static void Pump(Stream stream, RemoteExecOutputDecoder decoder, Action<string> append)
    {
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var text = decoder.Decode(buffer.AsSpan(0, read));
                if (text.Length > 0)
                    append(text);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        var rest = decoder.Decode([], flush: true);
        if (rest.Length > 0)
            append(rest);
    }

    /// <summary>沒有正常結束時給人看的說明</summary>
    internal static string ExitText(string code, RemoteExecRequest request) => code switch
    {
        RemoteExecRules.ExitTimeout => $"超過 {request.TimeoutSeconds} 秒的上限，已停止",
        RemoteExecRules.ExitCancelled => "發起的一方要求停止，已停止",
        RemoteExecRules.ExitOrphaned => "發起的電腦離線太久（或伺服器重新啟動），已停止",
        RemoteExecRules.ExitAppExit => "這台的 Clode Myriagent 結束了，已停止",
        RemoteExecRules.ExitDenied => "這台電腦的人拒絕了這個指令，沒有執行",
        _ => "沒有執行",
    };

    private static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>PowerShell 7：PATH 上的 pwsh.exe，再看預設的安裝位置</summary>
    internal static string? FindPwsh()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "pwsh.exe");
                if (!candidate.StartsWith(@"\\", StringComparison.Ordinal) && File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }

        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(installed) ? installed : null;
    }

    /// <summary>app 有系統管理員的權限（以系統管理員執行，或 UAC 關掉的管理員帳戶）：權杖裡的 Administrators 群組是啟用的</summary>
    private static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>一個執行中的指令：段落的序號、合併輸出的緩衝、停止的原因</summary>
    private sealed class RunningExec(string commandId, RemoteExecRequest request, Action<ExecChunk> send)
    {
        private readonly object gate = new();
        private readonly List<(string Kind, StringBuilder Text)> pending = [];
        private long seq;
        private long forwarded;
        private bool truncated;
        private bool truncatedSent;
        private bool finished;
        private System.Threading.Timer? flushTimer;
        private Action? kill;

        public string CommandId { get; } = commandId;

        public RemoteExecRequest Request { get; } = request;

        /// <summary>停止時取消（確認視窗跟著關掉）</summary>
        public CancellationTokenSource Stopping { get; } = new();

        /// <summary>為什麼停止（null＝沒有要求停止）</summary>
        public string? StopCode { get; private set; }

        /// <summary>程序開起來了：停止時怎麼結束它</summary>
        public void Attach(Action killProcess)
        {
            bool stopNow;
            lock (gate)
            {
                kill = killProcess;
                stopNow = StopCode is not null;
            }

            if (stopNow)
                killProcess();
        }

        public void Stop(string code)
        {
            Action? killNow;
            lock (gate)
            {
                if (finished || StopCode is not null)
                    return;
                StopCode = code;
                killNow = kill;
            }

            try
            {
                Stopping.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            killNow?.Invoke();
        }

        public void Status(string code, string? text)
        {
            lock (gate)
            {
                if (!finished)
                    Emit(RemoteExecRules.KindStatus, text, null, code);
            }
        }

        /// <summary>讀到一段輸出（讀取執行緒）：合併到 <see cref="RemoteExecRules.OutputBatchDelay"/> 後送出</summary>
        public void Append(string kind, string text)
        {
            lock (gate)
            {
                if (finished || truncated)
                    return;
                var room = RemoteExecRules.MaxOutputChars - forwarded;
                if (text.Length > room)
                {
                    text = text[..(int)Math.Max(0, room)];
                    truncated = true;
                }

                forwarded += text.Length;
                if (text.Length > 0)
                {
                    if (pending.Count > 0 && pending[^1].Kind == kind)
                        pending[^1].Text.Append(text);
                    else
                        pending.Add((kind, new StringBuilder(text)));
                }

                flushTimer ??= new System.Threading.Timer(_ => Flush(), null, RemoteExecRules.OutputBatchDelay, Timeout.InfiniteTimeSpan);
            }
        }

        public void Finish(int? exitCode, string? code, string? text)
        {
            lock (gate)
            {
                if (finished)
                    return;
                FlushLocked();
                finished = true;
                Emit(RemoteExecRules.KindExit, text, exitCode, code);
            }

            Stopping.Dispose();
        }

        private void Flush()
        {
            lock (gate)
            {
                if (!finished)
                    FlushLocked();
            }
        }

        private void FlushLocked()
        {
            flushTimer?.Dispose();
            flushTimer = null;
            foreach (var (kind, text) in pending)
            {
                foreach (var part in RemoteExecRules.Split(text.ToString()))
                    Emit(kind, part, null, null);
            }

            pending.Clear();
            if (truncated && !truncatedSent)
            {
                // 只送一次；之後 Append 一律丟掉（truncated 一直是 true）
                truncatedSent = true;
                Emit(RemoteExecRules.KindStatus, $"輸出超過 {RemoteExecRules.MaxOutputChars / (1024 * 1024)} MB，之後的不再傳回（指令照樣執行到結束）", null, RemoteExecRules.StatusTruncated);
            }
        }

        private void Emit(string kind, string? text, int? exitCode, string? code)
        {
            try
            {
                send(new ExecChunk(CommandId, seq++, kind, text, exitCode, code));
            }
            catch (Exception ex)
            {
                Log.Error("送出遠端執行指令的輸出失敗", ex);
            }
        }
    }
}
