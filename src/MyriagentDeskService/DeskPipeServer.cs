using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Myriagent.Remote;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace MyriagentDesk;

/// <summary>查出連上管道的是誰（真的問 Windows；測試以假的換掉）</summary>
internal interface IDeskClientIdentifier
{
    /// <summary>在讀過用戶端的第一行之後呼叫（Windows 要先讀過資料才查得到管道另一端的身分）</summary>
    DeskServiceClientInfo Identify(NamedPipeServerStream pipe);
}

/// <summary>看一個工作階段目前的輸入桌面（真的在那個工作階段開幫手；測試以假的換掉）</summary>
internal interface IDeskSessionProbe
{
    DeskServiceProbe Probe(int sessionId);
}

/// <summary>
/// 服務的管道（<see cref="DeskServiceRules"/> 的協定）。每條連線先查出用戶端是誰（<see cref="DeskServiceRules.Admit"/>），
/// 不收的每個要求都回 denied；收的才處理。以 LocalSystem 執行時管道開給互動登入的使用者、明確拒絕從網路連進來的
/// （具名管道經 SMB 連得到）；前景模式只開給執行它的使用者
/// </summary>
internal sealed class DeskPipeServer : IDisposable
{
    /// <summary>同時最多幾條連線（一台電腦通常只有一個 app 在連）</summary>
    public const int MaxInstances = 8;

    /// <summary>一條連線多久沒有要求就關掉（不讓不講話的連線佔著位置）</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly string pipeName;
    private readonly bool system;
    private readonly ReleaseIdentity identity;
    private readonly IDeskClientIdentifier identifier;
    private readonly IDeskSessionProbe probe;
    private readonly CancellationTokenSource stop = new();
    private Task? loop;

    public DeskPipeServer(string pipeName, bool system, ReleaseIdentity identity, IDeskClientIdentifier identifier, IDeskSessionProbe probe)
    {
        this.pipeName = pipeName;
        this.system = system;
        this.identity = identity;
        this.identifier = identifier;
        this.probe = probe;
    }

    /// <summary>第一個管道已經建好、可以連了（測試等它）</summary>
    public Task Listening => listening.Task;

    private readonly TaskCompletionSource listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Start() => loop = Task.Run(AcceptLoopAsync);

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
                    if (client is null)
                    {
                        client = identifier.Identify(pipe);
                        denied = DeskServiceRules.Admit(client, system);
                        if (denied is not null)
                            ServiceLog.Warn($"不收的用戶端（工作階段 {client.SessionId}、使用者 {client.UserSid ?? "?"}）：{denied}");
                    }

                    var reply = denied is not null
                        ? DeskServiceRules.ErrorReply(DeskServiceRules.CodeDenied, denied)
                        : Handle(line, client);
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes(reply + "\n"), stop.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(stop.Token).ConfigureAwait(false);
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
        }
    }

    /// <summary>處理一行要求（已經確認收這個用戶端）</summary>
    internal string Handle(string line, DeskServiceClientInfo client)
    {
        if (!DeskServiceRules.TryParseRequest(line, out var op, out JObject? _))
            return DeskServiceRules.ErrorReply(DeskServiceRules.CodeInvalid, "要求不是 JSON 物件或沒有 op");
        switch (op)
        {
            case DeskServiceRules.OpHello:
                return DeskServiceRules.HelloReply(new DeskServiceHello(DeskServiceRules.ProtocolVersion, identity.Version.ToString(), identity.Build,
                    system, client.SessionId, [DeskServiceRules.FeatureProbe]));
            case DeskServiceRules.OpProbe:
                try
                {
                    // 只看用戶端自己的工作階段：要求裡不收工作階段編號
                    return DeskServiceRules.ProbeReply(probe.Probe(client.SessionId));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or TimeoutException)
                {
                    ServiceLog.Warn($"看不到工作階段 {client.SessionId} 的輸入桌面：{ex.Message}");
                    return DeskServiceRules.ErrorReply(DeskServiceRules.CodeFailed, ex.Message);
                }
            default:
                return DeskServiceRules.ErrorReply(DeskServiceRules.CodeUnsupported, $"這一版的服務不認得 {op}");
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
