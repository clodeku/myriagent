using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Myriagent.Remote;

namespace MyriagentDesk;

/// <summary>查出連上管道的是誰（真的問 Windows；測試以假的換掉）</summary>
internal interface IDeskClientIdentifier
{
    /// <summary>在讀過用戶端的第一行之後呼叫（Windows 要先讀過資料才查得到管道另一端的身分）</summary>
    DeskServiceClientInfo Identify(NamedPipeServerStream pipe);
}

/// <summary>
/// 一個要求的回覆：一行 JSON，後面可以緊接一段二進位資料（擷取的畫面）。<paramref name="Done"/> 在資料寫完（或寫不出去）之後呼叫一次——
/// 處理者把共用的緩衝借給這次回覆，寫完才能再用
/// </summary>
internal readonly record struct DeskPipeReply(string Line, byte[]? Payload = null, int PayloadLength = 0, Action? Done = null);

/// <summary>管道上的要求由誰處理（服務本體與使用者工作階段裡的幫手各一個）</summary>
internal interface IDeskPipeHandler
{
    /// <summary>收不收這個用戶端；收回 null，不收回原因（每條連線問一次）</summary>
    string? Admit(DeskServiceClientInfo client);

    /// <summary>處理一行要求（已經確認收這個用戶端）</summary>
    DeskPipeReply Handle(string line, DeskServiceClientInfo client);
}

/// <summary>
/// 服務與幫手共用的管道伺服器（<see cref="DeskServiceRules"/> 的協定）。每條連線先查出用戶端是誰、問處理者收不收，
/// 不收的回 denied 就斷線；收的才處理。以 LocalSystem 執行時管道開給互動登入的使用者、明確拒絕從網路連進來的
/// （具名管道經 SMB 連得到）；前景模式只開給執行它的使用者
/// </summary>
internal sealed class DeskPipeServer : IDisposable
{
    /// <summary>同時最多幾條連線（一台電腦通常只有一個 app 在連）</summary>
    public const int MaxInstances = 8;

    /// <summary>一條連線多久沒有要求就關掉（不讓不講話的連線佔著位置）</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 一個回覆最多寫多久：用戶端要了畫面卻不讀，管道的緩衝滿了寫入就停住——處理者借出去的緩衝要等這個回覆寫完才收得回來，
    /// 不設上限的話一條不讀的連線會讓其他連線都要不到畫面。超過就關掉那條連線
    /// </summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

    private readonly string pipeName;
    private readonly bool system;
    private readonly IDeskClientIdentifier identifier;
    private readonly IDeskPipeHandler handler;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? loop;
    private int connections;
    private long lastActivityTick = Environment.TickCount64;

    public DeskPipeServer(string pipeName, bool system, IDeskClientIdentifier identifier, IDeskPipeHandler handler)
    {
        this.pipeName = pipeName;
        this.system = system;
        this.identifier = identifier;
        this.handler = handler;
    }

    /// <summary>第一個管道已經建好、可以連了</summary>
    public Task Listening => listening.Task;

    /// <summary>現在連著幾條</summary>
    public int ActiveConnections => Volatile.Read(ref connections);

    /// <summary>最近一次有連線連上、斷開或送要求的時間（<see cref="Environment.TickCount64"/>）：幫手拿它決定閒置多久了</summary>
    public long LastActivityTick => Volatile.Read(ref lastActivityTick);

    public void Start() => loop = Task.Run(AcceptLoopAsync);

    private void Touch() => Volatile.Write(ref lastActivityTick, Environment.TickCount64);

    private async Task AcceptLoopAsync()
    {
        var first = true;
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = Create(first);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 第一個建不起來：名稱被別的程序佔走了（FirstPipeInstance 不和別人共用名稱）；之後的建不起來：連線滿了
                if (first)
                    ServiceLog.Warn($"管道 {pipeName} 建不起來（名稱被佔用？）：{ex.Message}");
                if (!await DelayAsync(first ? 5000 : 200).ConfigureAwait(false))
                    break;
                continue;
            }

            first = false;
            listening.TrySetResult();
            try
            {
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            catch (IOException)
            {
                pipe.Dispose();
                continue;
            }

            _ = Task.Run(() => ServeAsync(pipe));
        }
    }

    private async Task<bool> DelayAsync(int ms)
    {
        try
        {
            await Task.Delay(ms, stop.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private NamedPipeServerStream Create(bool first)
    {
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        if (!system)
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, options | PipeOptions.CurrentUserOnly);

        var security = new PipeSecurity();
        // 拒絕放最前面：從網路（SMB 的 \\這台\pipe\…）連進來的一律不收
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // 互動登入的使用者只能連進來讀寫（ReadWrite 不含 CreateNewInstance：不能冒充服務再開一個同名的管道）
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        Interlocked.Increment(ref connections);
        Touch();
        using (pipe)
        {
            try
            {
                var reader = new PipeLineReader(pipe);
                DeskServiceClientInfo? client = null;
                string? denied = null;
                while (!stop.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    idle.CancelAfter(IdleTimeout);
                    var line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                    if (line is null)
                        break;
                    Touch();
                    if (client is null)
                    {
                        client = identifier.Identify(pipe);
                        denied = handler.Admit(client);
                        if (denied is not null)
                            ServiceLog.Warn($"不收的用戶端（工作階段 {client.SessionId}、使用者 {client.UserSid ?? "?"}）：{denied}");
                    }

                    var reply = denied is not null
                        ? new DeskPipeReply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeDenied, denied))
                        : handler.Handle(line, client);
                    try
                    {
                        using var writing = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        writing.CancelAfter(WriteTimeout);
                        await pipe.WriteAsync(Encoding.UTF8.GetBytes(reply.Line + "\n"), writing.Token).ConfigureAwait(false);
                        if (reply.Payload is { } payload && reply.PayloadLength > 0)
                            await pipe.WriteAsync(payload.AsMemory(0, reply.PayloadLength), writing.Token).ConfigureAwait(false);
                        await pipe.FlushAsync(writing.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        reply.Done?.Invoke();
                    }

                    if (denied is not null)
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
            {
                // 用戶端斷線、太久沒講話、一行太長、服務要停了：關掉這條連線就好
            }
            catch (Exception ex)
            {
                ServiceLog.Error("處理管道連線時發生錯誤", ex);
            }
            finally
            {
                Interlocked.Decrement(ref connections);
                Touch();
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        try
        {
            loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // 迴圈自己結束時的例外不重要
        }
        stop.Dispose();
    }
}

/// <summary>從管道一次讀一行（UTF-8、<c>\n</c> 結尾）；一行超過上限丟 <see cref="InvalidDataException"/>，另一端關了回 null</summary>
internal sealed class PipeLineReader(Stream stream)
{
    /// <summary>位元組的上限：一個字最多 3 個位元組（協定的內容都在基本多文種平面內），再留一點</summary>
    private const int MaxLineBytes = DeskServiceRules.MaxLineChars * 3;

    private readonly byte[] chunk = new byte[4096];
    private readonly MemoryStream pending = new();

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            if (TryTakeLine(out var line))
                return line;
            if (pending.Length > MaxLineBytes)
                throw new InvalidDataException("一行太長");
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read <= 0)
                return null;
            pending.Seek(0, SeekOrigin.End);
            pending.Write(chunk, 0, read);
        }
    }

    private bool TryTakeLine(out string? line)
    {
        line = null;
        var buffer = pending.GetBuffer();
        var length = (int)pending.Length;
        var newline = Array.IndexOf(buffer, (byte)'\n', 0, length);
        if (newline < 0)
            return false;
        line = new UTF8Encoding(false, false).GetString(buffer, 0, newline).TrimEnd('\r');
        var rest = length - newline - 1;
        Buffer.BlockCopy(buffer, newline + 1, buffer, 0, rest);
        pending.SetLength(rest);
        return true;
    }
}
