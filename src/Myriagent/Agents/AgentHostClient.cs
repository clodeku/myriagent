using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using Myriagent.Core;
using Myriagent.Host;

namespace Myriagent.Agents;

/// <summary>代理主機用不了（執行檔不在、開不起來、連不上、連線斷了）：呼叫端退回直接開程序</summary>
internal sealed class AgentHostUnavailableException : Exception
{
    public AgentHostUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// 經代理主機開 CLI 程序（設計 docs/plans/2026-11-26-agent-host.md）。中樞預設沒有（null＝直接開程序），只有 AppController 接上真的；
/// 測試給暫存資料夾的 <see cref="AgentHostClient"/>
/// </summary>
internal interface IAgentProcessHost
{
    /// <summary>
    /// 請主機開一個程序。主機用不了丟 <see cref="AgentHostUnavailableException"/>；主機開程序失敗丟和直接開一樣的例外
    /// （找不到執行檔等是帶 Win32 錯誤碼的 <see cref="Win32Exception"/>，AgentProcess 據此重試）
    /// </summary>
    HostedProcess Spawn(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyList<KeyValuePair<string, string>> environment);
}

/// <summary>主機開的一個程序在 app 這一側的樣子：兩條輸出的位元組串流（主機轉來的）、寫 stdin、結束的通知</summary>
internal sealed class HostedProcess
{
    private readonly AgentHostConnection connection;
    private readonly TaskCompletionSource<int?> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal HostedProcess(AgentHostConnection connection, int handle, int processId)
    {
        this.connection = connection;
        Handle = handle;
        ProcessId = processId;
    }

    public int Handle { get; }

    public int ProcessId { get; }

    public BlockingByteStream Output { get; } = new();

    public BlockingByteStream Error { get; } = new();

    /// <summary>程序結束（主機兩條輸出都轉完之後才通知）；主機的連線斷了也算結束，結束碼不明（null）</summary>
    public Task<int?> ExitTask => exited.Task;

    private readonly Dictionary<int, TaskCompletionSource<bool>> pendingWrites = new();
    private int nextWrite;
    private bool ended;

    /// <summary>
    /// 寫進 stdin 並等主機說寫進去了；那個程序已經結束、或它自己關了 stdin 丟 IOException（同直接寫已關閉的管線）。
    /// 等結果也讓寫入有順序與節制：主機還沒寫完上一段，下一段不會先到
    /// </summary>
    public async Task WriteAsync(byte[] data)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int sequence;
        lock (pendingWrites)
        {
            if (ended)
                throw new IOException("代理程序已結束");
            sequence = ++nextWrite;
            pendingWrites[sequence] = done;
        }
        try
        {
            await connection.SendAsync(HostProtocol.Write, HostProtocol.EncodeWrite(Handle, sequence, data)).ConfigureAwait(false);
        }
        catch
        {
            lock (pendingWrites)
                pendingWrites.Remove(sequence);
            throw;
        }
        if (!await done.Task.ConfigureAwait(false))
            throw new IOException("代理程序已結束或已關閉輸入");
    }

    internal void OnWriteResult(int sequence, bool ok)
    {
        TaskCompletionSource<bool>? waiting;
        lock (pendingWrites)
            pendingWrites.Remove(sequence, out waiting);
        waiting?.TrySetResult(ok);
    }

    public Task CloseInputAsync() => connection.SendAsync(HostProtocol.CloseInput, HostProtocol.EncodeHandle(Handle));

    internal void OnOutput(HostOutput output) => (output.Stream == HostProtocol.StdErr ? Error : Output).Add(output.Data);

    internal void OnExited(int? exitCode)
    {
        List<TaskCompletionSource<bool>> waiting;
        lock (pendingWrites)
        {
            ended = true;
            waiting = pendingWrites.Values.ToList();
            pendingWrites.Clear();
        }
        foreach (var write in waiting)
            write.TrySetResult(false);
        Output.Complete();
        Error.Complete();
        exited.TrySetResult(exitCode);
    }
}

/// <summary>
/// 只給一個讀的人、一個寫的人用的位元組串流：寫的一方（主機連線的讀取執行緒）加一段就回，讀的一方（AgentProcess 的讀行執行緒）沒有資料時等著，
/// <see cref="Complete"/> 之後讀完剩下的就回 0（同管線的另一端關閉）
/// </summary>
internal sealed class BlockingByteStream : Stream
{
    private readonly BlockingCollection<byte[]> chunks = new(new ConcurrentQueue<byte[]>());
    private byte[]? current;
    private int position;

    public void Add(byte[] data)
    {
        if (data.Length == 0)
            return;
        try
        {
            chunks.Add(data);
        }
        catch (InvalidOperationException)
        {
            // 已經結束（Complete 之後晚到的一段）：丟掉
        }
    }

    public void Complete()
    {
        try
        {
            chunks.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0)
            return 0;
        if (current == null || position >= current.Length)
        {
            try
            {
                if (!chunks.TryTake(out current, Timeout.Infinite))
                    return 0;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
            position = 0;
        }
        int n = Math.Min(count, current.Length - position);
        Buffer.BlockCopy(current, position, buffer, offset, n);
        position += n;
        return n;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>和一個主機程序的連線：一條專用執行緒讀主機送來的框、分給各個程序；送出以信號量排隊</summary>
internal sealed class AgentHostConnection : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<int, HostedProcess> processes = new();
    private readonly Dictionary<int, TaskCompletionSource<HostSpawnResult>> pendingSpawns = new();
    private readonly TaskCompletionSource<bool> hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int nextRequest;
    private volatile bool closed;

    public AgentHostConnection(NamedPipeClientStream pipe, int hostProcessId, string jobName)
    {
        this.pipe = pipe;
        HostProcessId = hostProcessId;
        JobName = jobName;
    }

    /// <summary>主機程序的 id（啟動它的時候記下）</summary>
    public int HostProcessId { get; }

    public string JobName { get; }

    /// <summary>主機回報的協定版本（Hello 之後）</summary>
    public int ProtocolVersion { get; private set; }

    public bool IsAlive => !closed;

    /// <summary>連線斷了（主機結束、當掉）：這條連線上的程序都已當成結束</summary>
    public event Action? Closed;

    public void Start() => PipeReadThread.Start($"agent host {HostProcessId}", ReadLoop);

    /// <summary>打招呼並等主機回覆；版本太舊、逾時或連線斷了丟 <see cref="AgentHostUnavailableException"/></summary>
    public void Handshake(TimeSpan timeout)
    {
        SendAsync(HostProtocol.Hello, HostProtocol.EncodeHello(HostProtocol.Version)).GetAwaiter().GetResult();
        if (!hello.Task.Wait(timeout) || !hello.Task.Result)
            throw new AgentHostUnavailableException("代理主機沒有回應");
        if (ProtocolVersion < HostProtocol.MinVersion)
            throw new AgentHostUnavailableException($"代理主機的協定太舊（{ProtocolVersion}）");
    }

    public HostedProcess Spawn(HostSpawnRequest request, TimeSpan timeout)
    {
        var done = new TaskCompletionSource<HostSpawnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (closed)
                throw new AgentHostUnavailableException("代理主機的連線已經斷了");
            request.RequestId = ++nextRequest;
            pendingSpawns[request.RequestId] = done;
        }
        try
        {
            SendAsync(HostProtocol.Spawn, HostProtocol.EncodeSpawn(request)).GetAwaiter().GetResult();
        }
        catch (IOException ex)
        {
            throw new AgentHostUnavailableException("送不到代理主機", ex);
        }
        if (!done.Task.Wait(timeout))
        {
            lock (gate)
                pendingSpawns.Remove(request.RequestId);
            throw new AgentHostUnavailableException("代理主機沒有回覆開程序的要求");
        }
        var result = done.Task.Result;
        if (!result.Ok)
        {
            // 同直接開程序時的例外：Win32 錯誤（找不到檔、檔案被占用…）讓 AgentProcess 的重試認得
            if (result.NativeError != 0)
                throw new Win32Exception(result.NativeError, result.Error ?? "無法啟動程序");
            throw new InvalidOperationException(result.Error ?? $"無法啟動 {request.FileName}");
        }
        lock (gate)
            return processes[result.Handle];
    }

    /// <summary>送一個框；連線已斷丟 IOException（同寫進已結束程序的 stdin）</summary>
    public async Task SendAsync(byte type, byte[] payload)
    {
        if (closed)
            throw new IOException("代理主機的連線已經斷了");
        var frame = HostProtocol.Frame(type, payload);
        await sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(frame).ConfigureAwait(false);
            await pipe.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            throw new IOException("代理主機的連線已經斷了", ex);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (HostProtocol.ReadFrame(pipe, out var type, out var payload))
            {
                switch (type)
                {
                    case HostProtocol.HelloOk:
                        HostProtocol.DecodeHelloOk(payload, out var version, out _);
                        ProtocolVersion = version;
                        hello.TrySetResult(true);
                        break;
                    case HostProtocol.SpawnResult:
                    {
                        var result = HostProtocol.DecodeSpawnResult(payload);
                        TaskCompletionSource<HostSpawnResult>? waiting;
                        lock (gate)
                        {
                            // 先登記程序再回覆：主機緊接著送來的 Output 才找得到它
                            if (result.Ok)
                                processes[result.Handle] = new HostedProcess(this, result.Handle, result.ProcessId);
                            pendingSpawns.Remove(result.RequestId, out waiting);
                        }
                        waiting?.TrySetResult(result);
                        break;
                    }
                    case HostProtocol.Output:
                    {
                        var output = HostProtocol.DecodeOutput(payload);
                        HostedProcess? target;
                        lock (gate)
                            processes.TryGetValue(output.Handle, out target);
                        target?.OnOutput(output);
                        break;
                    }
                    case HostProtocol.WriteResult:
                    {
                        HostProtocol.DecodeWriteResult(payload, out var handle, out var sequence, out var ok);
                        HostedProcess? target;
                        lock (gate)
                            processes.TryGetValue(handle, out target);
                        target?.OnWriteResult(sequence, ok);
                        break;
                    }
                    case HostProtocol.Exited:
                    {
                        HostProtocol.DecodeExited(payload, out var handle, out var exitCode);
                        HostedProcess? target;
                        lock (gate)
                            processes.Remove(handle, out target);
                        target?.OnExited(exitCode);
                        break;
                    }
                    // 不認得的型別略過（新版主機多送的通知）
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or EndOfStreamException)
        {
            Log.Warn($"代理主機的連線中斷：{ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    /// <summary>連線結束：等著的要求失敗，還在的程序都當成結束（主機不在了，它的工作物件已經把 CLI 結束）</summary>
    private void Close()
    {
        List<HostedProcess> orphaned;
        List<TaskCompletionSource<HostSpawnResult>> waiting;
        lock (gate)
        {
            if (closed)
                return;
            closed = true;
            orphaned = processes.Values.ToList();
            processes.Clear();
            waiting = pendingSpawns.Values.ToList();
            pendingSpawns.Clear();
        }
        hello.TrySetResult(false);
        foreach (var spawn in waiting)
            spawn.TrySetResult(new HostSpawnResult { Ok = false, Error = "代理主機的連線中斷" });
        foreach (var process in orphaned)
            process.OnExited(null);
        try
        {
            pipe.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        try
        {
            Closed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"處理代理主機斷線失敗：{ex.Message}");
        }
    }

    /// <summary>app 結束：請主機結束（它會結束全部 CLI），再關連線</summary>
    public void Dispose()
    {
        if (!closed)
        {
            try
            {
                SendAsync(HostProtocol.Shutdown, Array.Empty<byte>()).Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is IOException or AggregateException or ObjectDisposedException)
            {
            }
        }
        Close();
    }
}

/// <summary>
/// 代理主機的用戶端：第一次要開程序時才啟動主機、連上；連線斷了（主機當掉）下次再啟動一個新的。
/// H0（行為不變）：app 結束時 <see cref="Dispose"/> 請主機結束，主機看到連線斷了也會自己結束全部程序
/// </summary>
internal sealed class AgentHostClient : IAgentProcessHost, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SpawnTimeout = TimeSpan.FromSeconds(30);

    private readonly string hostExecutable;
    private readonly string dataRoot;
    private readonly string? logPath;
    private readonly object gate = new();
    private AgentHostConnection? connection;
    private bool disposed;

    /// <param name="hostExecutable">主機的執行檔（app 資料夾裡的 Myriagent.Host.exe）</param>
    /// <param name="dataRoot">資料根目錄（管道與工作物件的名稱由它算出：每個資料根目錄各自一個主機）</param>
    /// <param name="logPath">主機的記錄檔（只記錯誤）；null＝不記</param>
    public AgentHostClient(string hostExecutable, string dataRoot, string? logPath = null)
    {
        this.hostExecutable = hostExecutable;
        this.dataRoot = dataRoot;
        this.logPath = logPath;
    }

    /// <summary>app 資料夾裡主機執行檔的檔名</summary>
    public const string ExecutableName = "Myriagent.Host.exe";

    /// <summary>目前連著的主機程序 id（沒有連線回 null；測試與記錄用）</summary>
    public int? HostProcessId
    {
        get
        {
            lock (gate)
                return connection is { IsAlive: true } live ? live.HostProcessId : null;
        }
    }

    public HostedProcess Spawn(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyList<KeyValuePair<string, string>> environment)
    {
        var live = EnsureConnected();
        var request = new HostSpawnRequest { FileName = fileName, WorkingDirectory = workingDirectory };
        request.Arguments.AddRange(arguments);
        request.Environment.AddRange(environment);
        return live.Spawn(request, SpawnTimeout);
    }

    private AgentHostConnection EnsureConnected()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (connection is { IsAlive: true } live)
                return live;
            connection = Launch();
            return connection;
        }
    }

    /// <summary>啟動一個主機並連上。每次啟動用新的管道與工作物件名稱：上一個主機還在收尾時不會撞名</summary>
    private AgentHostConnection Launch()
    {
        if (!File.Exists(hostExecutable))
            throw new AgentHostUnavailableException($"找不到代理主機的執行檔：{hostExecutable}");
        var suffix = "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var pipeName = HostProtocol.PipeName(dataRoot) + suffix;
        var jobName = HostProtocol.JobName(dataRoot) + suffix;

        Process host;
        try
        {
            var psi = new ProcessStartInfo { FileName = hostExecutable, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--pipe");
            psi.ArgumentList.Add(pipeName);
            psi.ArgumentList.Add("--job");
            psi.ArgumentList.Add(jobName);
            if (!string.IsNullOrEmpty(logPath))
            {
                psi.ArgumentList.Add("--log");
                psi.ArgumentList.Add(logPath);
            }
            // 主機不加入 app 的工作物件（之後的階段它要活得比 app 久）；H0 靠連線斷了讓它自己結束
            host = Process.Start(psi) ?? throw new AgentHostUnavailableException("代理主機啟動不了");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new AgentHostUnavailableException($"代理主機啟動不了：{ex.Message}", ex);
        }

        int hostPid = host.Id;
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
            var created = new AgentHostConnection(pipe, hostPid, jobName);
            created.Start();
            created.Handshake(ConnectTimeout);
            var jobHandle = ChildProcessJob.AttachHostJob(jobName);
            created.Closed += () => ChildProcessJob.DetachHostJob(jobHandle);
            Log.Info($"已連上代理主機（程序 {hostPid}，協定 {created.ProtocolVersion}）");
            host.Dispose();
            return created;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or AgentHostUnavailableException)
        {
            pipe.Dispose();
            try
            {
                if (!host.HasExited)
                    host.Kill();
            }
            catch (Exception killError) when (killError is InvalidOperationException or Win32Exception)
            {
            }
            host.Dispose();
            throw ex as AgentHostUnavailableException ?? new AgentHostUnavailableException($"連不上代理主機：{ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        AgentHostConnection? live;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            live = connection;
            connection = null;
        }
        live?.Dispose();
    }
}
