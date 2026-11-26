using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Core;
using Myriagent.Host;
using Newtonsoft.Json;

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
/// 放開一個經主機開的程序時記下的事（寫進 resume.json，新的 app 據此接回；設計 docs/plans/2026-11-26-agent-host.md 3.3）：
/// 主機給的代號、PID、兩條輸出各自處理到的位移（整行的邊界）、app 這一側的實例代號（核准卡的有效範圍認它）與管線位元組
/// </summary>
internal sealed record AgentProcessDetachState(int Handle, int ProcessId, long OutOffset, long ErrOffset, string InstanceId, long PipeBytes);

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

    /// <summary>
    /// 接回上一個 app 放開的程序（從記下的位移起補送輸出）。接不回去——主機不在了、程序不在了、暫存不夠——丟
    /// <see cref="AgentHostUnavailableException"/>，呼叫端照「程序已經沒了」處理
    /// </summary>
    HostedProcess Attach(AgentProcessDetachState state);

    /// <summary>結束一個沒有人要接回的放開的程序（主機手上確實有它、而且沒有 app 接著才結束；沒有就不做事）</summary>
    void Discard(AgentProcessDetachState state);

    /// <summary>結束主機手上所有沒有 app 接著的程序（新的 app 接回該接的之後收掉剩下的）</summary>
    void DiscardUnclaimed();
}

/// <summary>
/// 一條輸出的逐行讀取：主機轉來的位元組自己切行（以 \n 為界、去掉行尾的 \r，同 StreamReader 對 CLI 輸出的結果），
/// 並記下「處理到哪個位移」——放開程序時新的 app 要從整行的邊界接著讀。只給一個讀的人、一個寫的人用
/// </summary>
internal sealed class HostedLineReader : TextReader
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly BlockingCollection<byte[]> chunks = new(new ConcurrentQueue<byte[]>());
    private readonly MemoryStream pending = new();
    private byte[]? current;
    private int position;
    private long consumed;
    private volatile bool detached;

    public HostedLineReader(long startOffset = 0) => consumed = startOffset;

    /// <summary>已經交出去的最後一行的結尾位移（含它的換行）：之後的位元組還沒被處理</summary>
    public long ConsumedOffset => Interlocked.Read(ref consumed);

    /// <summary>每交出一行之後呼叫（讀的那條執行緒上）：用來定期向主機確認</summary>
    public Action? Consumed { get; set; }

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

    /// <summary>程序結束（或連線斷了）：讀完剩下的，最後沒有換行的半行也交出（同管線關閉時的 StreamReader）</summary>
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

    /// <summary>放開程序：讀完已經收到的整行就結束，最後的半行不交出（它還在主機的暫存裡，新的 app 從整行的邊界重讀）</summary>
    public void CompleteDetached()
    {
        detached = true;
        Complete();
    }

    public override string? ReadLine()
    {
        while (true)
        {
            if (current == null || position >= current.Length)
            {
                try
                {
                    if (!chunks.TryTake(out current, Timeout.Infinite))
                        return FlushTail();
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
                position = 0;
            }
            int newline = Array.IndexOf(current!, (byte)'\n', position);
            if (newline < 0)
            {
                pending.Write(current!, position, current!.Length - position);
                position = current.Length;
                continue;
            }
            pending.Write(current!, position, newline - position);
            position = newline + 1;
            int length = (int)pending.Length;
            var bytes = pending.GetBuffer();
            Interlocked.Add(ref consumed, length + 1);
            if (length > 0 && bytes[length - 1] == (byte)'\r')
                length--;
            var line = Utf8.GetString(bytes, 0, length);
            pending.SetLength(0);
            Consumed?.Invoke();
            return line;
        }
    }

    private string? FlushTail()
    {
        if (detached || pending.Length == 0)
            return null;
        int length = (int)pending.Length;
        Interlocked.Add(ref consumed, length);
        var line = Utf8.GetString(pending.GetBuffer(), 0, length);
        pending.SetLength(0);
        return line;
    }
}

/// <summary>主機開的一個程序在 app 這一側的樣子：兩條輸出（主機轉來的）、寫 stdin、結束的通知、放開</summary>
internal sealed class HostedProcess
{
    /// <summary>處理過的輸出每累積這麼多就向主機確認一次（主機丟掉確認過的暫存）</summary>
    private const long AckEvery = 64 * 1024;

    private readonly AgentHostConnection connection;
    private readonly TaskCompletionSource<int?> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<(long Out, long Err)> detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<int, TaskCompletionSource<bool>> pendingWrites = new();
    private int nextWrite;
    private bool ended;
    private long acked;

    internal HostedProcess(AgentHostConnection connection, int handle, int processId, long outOffset = 0, long errOffset = 0)
    {
        this.connection = connection;
        Handle = handle;
        ProcessId = processId;
        Output = new HostedLineReader(outOffset) { Consumed = MaybeAck };
        Error = new HostedLineReader(errOffset) { Consumed = MaybeAck };
        acked = outOffset + errOffset;
    }

    public int Handle { get; }

    public int ProcessId { get; }

    public HostedLineReader Output { get; }

    public HostedLineReader Error { get; }

    /// <summary>主機會在 app 交棒後保留程序（協定第 2 版起）：才放得開、接得回</summary>
    public bool CanDetach => connection.Keeps;

    /// <summary>從下限起最多留這麼多沒確認的 stdout（主機每個程序的暫存上限是 64 MB：超過就整個接不回去，所以先放掉下限）</summary>
    internal const long MaxHeldOutput = 32L * 1024 * 1024;

    /// <summary>stdout 的確認下限；-1＝沒有（收到就確認）</summary>
    private long ackFloor = -1;

    /// <summary>
    /// 主機留著這個位移之後的 stdout（代理主機 H2：會話的檢查點——接回時解析器從這裡重播）：之後的確認不超過它。
    /// 位移要是還沒確認過的（目前讀到的位置一定是）。留的量超過 <see cref="MaxHeldOutput"/> 就自己放掉（<see cref="OutputHeldFrom"/> 變回 -1）
    /// </summary>
    public void HoldOutputFrom(long offset)
    {
        Interlocked.Exchange(ref ackFloor, Math.Max(0, offset));
        MaybeAck();
    }

    /// <summary>不再留：收到的照常確認</summary>
    public void ReleaseOutputHold()
    {
        Interlocked.Exchange(ref ackFloor, -1);
        MaybeAck();
    }

    /// <summary>目前留著的起點；-1＝沒有留（沒設過、放掉了、或量太大被放掉）</summary>
    public long OutputHeldFrom => Interlocked.Read(ref ackFloor);

    /// <summary>程序結束（主機兩條輸出都轉完之後才通知）；主機的連線斷了也算結束，結束碼不明（null）</summary>
    public Task<int?> ExitTask => exited.Task;

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

    public Task CloseInputAsync() => connection.SendAsync(HostProtocol.CloseInput, HostProtocol.EncodeHandle(Handle));

    /// <summary>
    /// 放開這個程序（不結束它）：請主機先不再送它的輸出，回傳主機已經送到哪裡之後，兩條輸出讀完已收到的整行就結束。
    /// 回 false＝放不開（主機是舊版、程序已經結束、主機沒回應）：呼叫端照原本的方式處理這個程序
    /// </summary>
    public async Task<bool> DetachAsync(TimeSpan timeout)
    {
        if (!CanDetach || exited.Task.IsCompleted)
            return false;
        try
        {
            await connection.SendAsync(HostProtocol.Detach, HostProtocol.EncodeHandle(Handle)).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return false;
        }
        var finished = await Task.WhenAny(detached.Task, exited.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != detached.Task || detached.Task.Result.Out < 0)
            return false;
        // 主機回覆之前送的輸出都已經排進兩條讀取器了（同一條連線、照順序處理）：讀完整行就結束，之後這個物件不再收任何東西
        connection.Forget(this);
        lock (pendingWrites)
            ended = true;
        Output.CompleteDetached();
        Error.CompleteDetached();
        return true;
    }

    private void MaybeAck()
    {
        if (!connection.Keeps)
            return;
        long outOffset = Output.ConsumedOffset, errOffset = Error.ConsumedOffset;
        long floor = Interlocked.Read(ref ackFloor);
        if (floor >= 0)
        {
            if (outOffset - floor > MaxHeldOutput)
                Interlocked.CompareExchange(ref ackFloor, -1, floor);   // 這一輪的輸出太多：不留了（接回時沒有檢查點可以重播）
            else
                outOffset = Math.Min(outOffset, floor);
        }
        long total = outOffset + errOffset;
        if (total - Interlocked.Read(ref acked) < AckEvery)
            return;
        Interlocked.Exchange(ref acked, total);
        // 不等：確認晚一點到只是主機多留一下暫存
        _ = connection.SendQuietlyAsync(HostProtocol.Ack, HostProtocol.EncodeOffsets(Handle, outOffset, errOffset));
    }

    internal void OnOutput(HostOutput output) => (output.Stream == HostProtocol.StdErr ? Error : Output).Add(output.Data);

    internal void OnWriteResult(int sequence, bool ok)
    {
        TaskCompletionSource<bool>? waiting;
        lock (pendingWrites)
            pendingWrites.Remove(sequence, out waiting);
        waiting?.TrySetResult(ok);
    }

    internal void OnDetached(long outEnd, long errEnd) => detached.TrySetResult((outEnd, errEnd));

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

/// <summary>和一個主機程序的連線：一條專用執行緒讀主機送來的框、分給各個程序；送出以信號量排隊</summary>
internal sealed class AgentHostConnection : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<int, HostedProcess> processes = new();
    private readonly Dictionary<int, TaskCompletionSource<HostSpawnResult>> pendingSpawns = new();
    private readonly Dictionary<int, TaskCompletionSource<List<HostProcessInfo>>> pendingLists = new();
    private readonly Dictionary<int, TaskCompletionSource<string?>> pendingAttaches = new();
    private readonly TaskCompletionSource<bool> hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int nextRequest;
    private volatile bool closed;

    public AgentHostConnection(NamedPipeClientStream pipe, int hostProcessId, string jobName)
    {
        this.pipe = pipe;
        HostProcessId = hostProcessId;
        JobName = jobName;
    }

    /// <summary>主機程序的 id（啟動它的時候記下，或 host.json 記的）</summary>
    public int HostProcessId { get; }

    public string JobName { get; }

    /// <summary>主機回報的協定版本（Hello 之後）</summary>
    public int ProtocolVersion { get; private set; }

    /// <summary>主機會在 app 交棒後保留程序、認得確認與接回（協定第 2 版起）</summary>
    public bool Keeps => ProtocolVersion >= HostProtocol.KeepVersion;

    public bool IsAlive => !closed;

    /// <summary>連線斷了（主機結束、當掉）：這條連線上的程序都已當成結束</summary>
    public event Action? Closed;

    public void Start() => PipeReadThread.Start($"agent host {HostProcessId}", ReadLoop);

    /// <summary>打招呼並等主機回覆；版本太舊、逾時或連線斷了丟 <see cref="AgentHostUnavailableException"/></summary>
    public void Handshake(TimeSpan timeout)
    {
        try
        {
            SendAsync(HostProtocol.Hello, HostProtocol.EncodeHello(HostProtocol.Version)).GetAwaiter().GetResult();
        }
        catch (IOException ex)
        {
            throw new AgentHostUnavailableException("送不到代理主機", ex);
        }
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
            if (result.Handle == 0 && result.Error == ClosedError)
                throw new AgentHostUnavailableException(ClosedError);
            throw new InvalidOperationException(result.Error ?? $"無法啟動 {request.FileName}");
        }
        lock (gate)
            return processes[result.Handle];
    }

    private const string ClosedError = "代理主機的連線中斷";

    /// <summary>主機手上的程序（上一個 app 放開的、還在跑或已經結束還沒回報的）</summary>
    public List<HostProcessInfo> List(TimeSpan timeout)
    {
        var done = new TaskCompletionSource<List<HostProcessInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int id;
        lock (gate)
        {
            if (closed || !Keeps)
                return new List<HostProcessInfo>();
            id = ++nextRequest;
            pendingLists[id] = done;
        }
        try
        {
            SendAsync(HostProtocol.List, HostProtocol.EncodeHandle(id)).GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            return new List<HostProcessInfo>();
        }
        return done.Task.Wait(timeout) ? done.Task.Result : new List<HostProcessInfo>();
    }

    /// <summary>接回一個程序：先登記（主機回覆後緊接著補送的輸出才找得到它），主機說不行就拿掉並丟 <see cref="AgentHostUnavailableException"/></summary>
    public HostedProcess Attach(AgentProcessDetachState state, TimeSpan timeout)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attached = new HostedProcess(this, state.Handle, state.ProcessId, state.OutOffset, state.ErrOffset);
        int id;
        lock (gate)
        {
            if (closed || !Keeps)
                throw new AgentHostUnavailableException("代理主機不支援接回");
            if (processes.ContainsKey(state.Handle))
                throw new AgentHostUnavailableException("這個程序已經接著");
            id = ++nextRequest;
            pendingAttaches[id] = done;
            processes[state.Handle] = attached;
        }
        string? error;
        try
        {
            SendAsync(HostProtocol.Attach, HostProtocol.EncodeAttach(id, state.Handle, state.OutOffset, state.ErrOffset)).GetAwaiter().GetResult();
            error = done.Task.Wait(timeout) ? done.Task.Result : "代理主機沒有回覆";
        }
        catch (IOException)
        {
            error = "送不到代理主機";
        }
        if (error == null)
            return attached;
        lock (gate)
        {
            pendingAttaches.Remove(id);
            if (processes.TryGetValue(state.Handle, out var current) && ReferenceEquals(current, attached))
                processes.Remove(state.Handle);
        }
        throw new AgentHostUnavailableException($"接不回程序 {state.ProcessId}：{error}");
    }

    /// <summary>告訴主機：這個 app 要結束了，斷線後把程序保留這麼久等新的 app</summary>
    public void Handoff(TimeSpan keep)
        => SendAsync(HostProtocol.Handoff, HostProtocol.EncodeHandoff((int)Math.Ceiling(keep.TotalSeconds))).GetAwaiter().GetResult();

    internal void Forget(HostedProcess process)
    {
        lock (gate)
        {
            if (processes.TryGetValue(process.Handle, out var current) && ReferenceEquals(current, process))
                processes.Remove(process.Handle);
        }
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

    /// <summary>送出、失敗不管（確認這類晚到或沒到都無妨的框）</summary>
    public async Task SendQuietlyAsync(byte type, byte[] payload)
    {
        try
        {
            await SendAsync(type, payload).ConfigureAwait(false);
        }
        catch (IOException)
        {
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
                        Find(output.Handle)?.OnOutput(output);
                        break;
                    }
                    case HostProtocol.WriteResult:
                    {
                        HostProtocol.DecodeWriteResult(payload, out var handle, out var sequence, out var ok);
                        Find(handle)?.OnWriteResult(sequence, ok);
                        break;
                    }
                    case HostProtocol.Detached:
                    {
                        HostProtocol.DecodeOffsets(payload, out var handle, out var outEnd, out var errEnd);
                        Find(handle)?.OnDetached(outEnd, errEnd);
                        break;
                    }
                    case HostProtocol.ListResult:
                    {
                        var list = HostProtocol.DecodeListResult(payload, out var requestId);
                        TaskCompletionSource<List<HostProcessInfo>>? waiting;
                        lock (gate)
                            pendingLists.Remove(requestId, out waiting);
                        waiting?.TrySetResult(list);
                        break;
                    }
                    case HostProtocol.AttachResult:
                    {
                        HostProtocol.DecodeAttachResult(payload, out var requestId, out var ok, out var error);
                        TaskCompletionSource<string?>? waiting;
                        lock (gate)
                            pendingAttaches.Remove(requestId, out waiting);
                        waiting?.TrySetResult(ok ? null : error ?? "接不回去");
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
            if (!closed)
                Log.Warn($"代理主機的連線中斷：{ex.Message}");
        }
        finally
        {
            Close();
        }
    }

    private HostedProcess? Find(int handle)
    {
        lock (gate)
            return processes.TryGetValue(handle, out var found) ? found : null;
    }

    /// <summary>連線結束：等著的要求失敗，還接著的程序都當成結束（主機不在了，或這個 app 不再管它們）</summary>
    private void Close()
    {
        List<HostedProcess> orphaned;
        List<TaskCompletionSource<HostSpawnResult>> spawns;
        List<TaskCompletionSource<List<HostProcessInfo>>> lists;
        List<TaskCompletionSource<string?>> attaches;
        lock (gate)
        {
            if (closed)
                return;
            closed = true;
            orphaned = processes.Values.ToList();
            processes.Clear();
            spawns = pendingSpawns.Values.ToList();
            pendingSpawns.Clear();
            lists = pendingLists.Values.ToList();
            pendingLists.Clear();
            attaches = pendingAttaches.Values.ToList();
            pendingAttaches.Clear();
        }
        hello.TrySetResult(false);
        foreach (var spawn in spawns)
            spawn.TrySetResult(new HostSpawnResult { Ok = false, Error = ClosedError });
        foreach (var list in lists)
            list.TrySetResult(new List<HostProcessInfo>());
        foreach (var attach in attaches)
            attach.TrySetResult(ClosedError);
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

    /// <summary>關掉連線，不叫主機結束（交棒：主機照 Handoff 說的保留程序）</summary>
    public void Disconnect() => Close();

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
/// app 一般結束時 <see cref="Dispose"/> 請主機結束；換版交棒時 <see cref="Handoff"/> 讓主機留著程序，新的 app 以 <see cref="TryAdopt"/> 找到它、
/// <see cref="Attach"/> 接回放開的程序。主機是誰記在 <c>&lt;資料根&gt;\host\host.json</c>
/// </summary>
internal sealed class AgentHostClient : IAgentProcessHost, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AdoptTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SpawnTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(10);

    private readonly string hostExecutable;
    private readonly string dataRoot;
    private readonly string? logPath;
    private readonly object gate = new();
    private AgentHostConnection? connection;
    private bool adoptTried;
    private bool disposed;

    /// <param name="hostExecutable">主機的執行檔（app 資料夾裡的 Myriagent.Host.exe）</param>
    /// <param name="dataRoot">資料根目錄（管道與工作物件的名稱由它算出：每個資料根目錄各自一個主機）</param>
    /// <param name="logPath">主機的記錄檔（只記錯誤與交棒）；null＝不記</param>
    public AgentHostClient(string hostExecutable, string dataRoot, string? logPath = null)
    {
        this.hostExecutable = hostExecutable;
        this.dataRoot = dataRoot;
        this.logPath = logPath;
    }

    /// <summary>app 資料夾裡主機執行檔的檔名</summary>
    public const string ExecutableName = "Myriagent.Host.exe";

    private string HostDirectory => Path.Combine(dataRoot, "host");

    private string InfoPath => Path.Combine(HostDirectory, "host.json");

    /// <summary>host.json：目前這個資料根目錄的主機是誰（新的 app 據此找到交棒留下的主機）</summary>
    private sealed class HostInfo
    {
        public string Pipe { get; set; } = string.Empty;

        public string Job { get; set; } = string.Empty;

        public int ProcessId { get; set; }
    }

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

    public HostedProcess Attach(AgentProcessDetachState state)
    {
        var live = AdoptedOrNull() ?? throw new AgentHostUnavailableException("上一個 app 留下的代理主機不在了");
        return live.Attach(state, AttachTimeout);
    }

    /// <summary>
    /// 連上上一個 app 交棒留下的主機（host.json 記的、還活著的）；沒有或連不上回 false。只試一次。
    /// 新的 app 啟動後、接回程序之前呼叫；沒呼叫也無妨（第一次用到主機時會試）
    /// </summary>
    public bool TryAdopt() => AdoptedOrNull() != null;

    /// <summary>主機手上還沒有 app 接著的程序（上一個 app 放開的）；沒有主機回空的</summary>
    public IReadOnlyList<HostProcessInfo> ListDetached()
    {
        var live = AdoptedOrNull();
        return live == null ? Array.Empty<HostProcessInfo>() : live.List(AttachTimeout).Where(p => !p.Attached).ToList();
    }

    public void Discard(AgentProcessDetachState state)
    {
        var live = AdoptedOrNull();
        if (live == null)
            return;
        foreach (var info in live.List(AttachTimeout).Where(p => !p.Attached && p.Handle == state.Handle && p.ProcessId == state.ProcessId))
            KillTree(info);
    }

    public void DiscardUnclaimed()
    {
        var live = AdoptedOrNull();
        if (live == null)
            return;
        foreach (var info in live.List(AttachTimeout).Where(p => !p.Attached))
            KillTree(info);
    }

    private static void KillTree(HostProcessInfo info)
    {
        if (info.Exited)
            return;
        try
        {
            using var process = Process.GetProcessById(info.ProcessId);
            process.Kill(entireProcessTree: true);
            Log.Info($"已結束沒有人接回的程序 {info.ProcessId}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    private AgentHostConnection? AdoptedOrNull()
    {
        lock (gate)
        {
            if (disposed)
                return null;
            if (connection is { IsAlive: true } live)
                return live;
            if (adoptTried)
                return null;
            adoptTried = true;
            connection = Adopt();
            return connection;
        }
    }

    private AgentHostConnection EnsureConnected()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (connection is { IsAlive: true } live)
                return live;
            // 上一個 app 交棒留下的主機還在就沿用（它手上可能有還沒接回的程序；同一個資料根目錄不開第二個）
            if (!adoptTried)
            {
                adoptTried = true;
                connection = Adopt();
                if (connection != null)
                    return connection;
            }
            connection = Launch();
            return connection;
        }
    }

    private AgentHostConnection? Adopt()
    {
        HostInfo? info = null;
        try
        {
            if (File.Exists(InfoPath))
                info = JsonConvert.DeserializeObject<HostInfo>(File.ReadAllText(InfoPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        if (info == null || string.IsNullOrEmpty(info.Pipe) || string.IsNullOrEmpty(info.Job))
            return null;
        var pipe = new NamedPipeClientStream(".", info.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect((int)AdoptTimeout.TotalMilliseconds);
            var adopted = Connected(pipe, info.ProcessId, info.Job);
            Log.Info($"已連上上一次留下的代理主機（程序 {info.ProcessId}，協定 {adopted.ProtocolVersion}）");
            return adopted;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or AgentHostUnavailableException)
        {
            pipe.Dispose();
            DeleteInfo();
            return null;
        }
    }

    private AgentHostConnection Connected(NamedPipeClientStream pipe, int hostPid, string jobName)
    {
        var created = new AgentHostConnection(pipe, hostPid, jobName);
        created.Start();
        created.Handshake(ConnectTimeout);
        var jobHandle = ChildProcessJob.AttachHostJob(jobName);
        created.Closed += () => ChildProcessJob.DetachHostJob(jobHandle);
        return created;
    }

    /// <summary>啟動一個主機並連上。每次啟動用新的管道與工作物件名稱：上一個主機還在收尾時不會撞名</summary>
    private AgentHostConnection Launch()
    {
        if (!File.Exists(hostExecutable))
            throw new AgentHostUnavailableException($"找不到代理主機的執行檔：{hostExecutable}");
        var executable = StableExecutable();
        var suffix = "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var pipeName = HostProtocol.PipeName(dataRoot) + suffix;
        var jobName = HostProtocol.JobName(dataRoot) + suffix;

        Process host;
        try
        {
            var psi = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--pipe");
            psi.ArgumentList.Add(pipeName);
            psi.ArgumentList.Add("--job");
            psi.ArgumentList.Add(jobName);
            if (!string.IsNullOrEmpty(logPath))
            {
                psi.ArgumentList.Add("--log");
                psi.ArgumentList.Add(logPath);
            }
            // 主機不加入 app 的工作物件：它要活得比 app 久（換版交棒時留著程序）
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
            var created = Connected(pipe, hostPid, jobName);
            Log.Info($"已連上代理主機（程序 {hostPid}，協定 {created.ProtocolVersion}）");
            host.Dispose();
            WriteInfo(new HostInfo { Pipe = pipeName, Job = jobName, ProcessId = hostPid });
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

    /// <summary>
    /// 主機從資料根目錄底下的一份副本執行（<c>host\bin\&lt;內容雜湊&gt;\</c>）：它會活得比啟動它的那一版 app 久，
    /// 直接從 app 的版本資料夾執行會把那個資料夾鎖住、舊版清不掉。複製不了就直接用原檔（照樣能用）。順手清掉沒在用的舊副本
    /// </summary>
    private string StableExecutable()
    {
        try
        {
            var bytes = File.ReadAllBytes(hostExecutable);
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
            var bin = Path.Combine(HostDirectory, "bin");
            var folder = Path.Combine(bin, hash);
            var target = Path.Combine(folder, ExecutableName);
            if (!File.Exists(target) || new FileInfo(target).Length != bytes.Length)
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(target, bytes);
            }
            foreach (var other in Directory.EnumerateDirectories(bin))
            {
                if (string.Equals(Path.GetFileName(other), hash, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    Directory.Delete(other, recursive: true);   // 還在執行的那一份刪不掉，留著
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"代理主機的執行檔複製不了，直接用 app 資料夾裡的：{ex.Message}");
            return hostExecutable;
        }
    }

    private void WriteInfo(HostInfo info)
    {
        try
        {
            Directory.CreateDirectory(HostDirectory);
            File.WriteAllText(InfoPath, JsonConvert.SerializeObject(info));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"寫不了 host.json（換版時接不回程序）：{ex.Message}");
        }
    }

    private void DeleteInfo()
    {
        try
        {
            File.Delete(InfoPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 換版交棒：告訴主機斷線後把程序保留 <paramref name="keep"/> 這麼久，然後只關連線、不叫它結束。之後這個用戶端不能再用。
    /// 沒有連著主機、或主機是舊版（不會保留）回 false——呼叫端照一般的結束處理
    /// </summary>
    public bool Handoff(TimeSpan keep)
    {
        AgentHostConnection? live;
        lock (gate)
        {
            if (disposed || connection is not { IsAlive: true, Keeps: true })
                return false;
            live = connection;
            connection = null;
            disposed = true;
        }
        try
        {
            live.Handoff(keep);
        }
        catch (IOException)
        {
            live.Dispose();
            return false;
        }
        live.Disconnect();
        return true;
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
        if (live == null)
            return;
        live.Dispose();
        DeleteInfo();
    }
}
