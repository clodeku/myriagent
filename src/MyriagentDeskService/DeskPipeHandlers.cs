using Myriagent.Remote;
using Myriagent.Update;

namespace MyriagentDesk;

/// <summary>看一個工作階段目前的輸入桌面（真的在那個工作階段開幫手；測試以假的換掉）</summary>
internal interface IDeskSessionProbe
{
    DeskServiceProbe Probe(int sessionId);
}

/// <summary>各個工作階段的擷取幫手（真的開程序；測試以假的換掉）</summary>
internal interface ISessionAgents
{
    /// <summary>那個工作階段的幫手的管道名稱：還活著就沿用，沒有就開一個、等它的管道建好</summary>
    string Open(int sessionId);
}

/// <summary>擷取目前的輸入桌面（真的切桌面、讀螢幕；測試以假的換掉）</summary>
internal interface IDesktopCapturer
{
    /// <summary>
    /// 把要求的範圍縮成 <c>OutW×OutH</c>、以 BGRA 由上而下寫進 <paramref name="buffer"/>（長度至少 <see cref="DeskServiceRules.CaptureBytes"/>）。
    /// 回 ok（寫了）或 failed（沒寫），連同擷取的是哪個桌面
    /// </summary>
    DeskServiceCaptureHeader Capture(DeskServiceCaptureRequest request, byte[] buffer);
}

/// <summary>
/// 服務本體（工作階段 0）的要求：<c>hello</c>、<c>probe</c>（開一次性的幫手看輸入桌面）、<c>open</c>（開擷取用的幫手、回它的管道）。
/// 只做用戶端自己那個工作階段的事：要求裡不收工作階段編號
/// </summary>
internal sealed class ServicePipeHandler(ReleaseIdentity identity, bool system, IDeskSessionProbe probe, ISessionAgents agents) : IDeskPipeHandler
{
    public string? Admit(DeskServiceClientInfo client) => DeskServiceRules.Admit(client, system);

    public DeskPipeReply Handle(string line, DeskServiceClientInfo client)
    {
        if (!DeskServiceRules.TryParseRequest(line, out var op, out _))
            return new DeskPipeReply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeInvalid, "要求不是 JSON 物件或沒有 op"));
        switch (op)
        {
            case DeskServiceRules.OpHello:
                return new DeskPipeReply(DeskServiceRules.HelloReply(new DeskServiceHello(DeskServiceRules.ProtocolVersion, identity.Version.ToString(), identity.Build,
                    system, client.SessionId, [DeskServiceRules.FeatureProbe, DeskServiceRules.FeatureCapture])));
            case DeskServiceRules.OpProbe:
                return Guarded(() => DeskServiceRules.ProbeReply(probe.Probe(client.SessionId)), $"看不到工作階段 {client.SessionId} 的輸入桌面");
            case DeskServiceRules.OpOpen:
                return Guarded(() => DeskServiceRules.OpenReply(agents.Open(client.SessionId)), $"開不了工作階段 {client.SessionId} 的擷取幫手");
            default:
                return new DeskPipeReply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeUnsupported, $"這一版的服務不認得 {op}"));
        }
    }

    private static DeskPipeReply Guarded(Func<string> reply, string what)
    {
        try
        {
            return new DeskPipeReply(reply());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or TimeoutException)
        {
            ServiceLog.Warn($"{what}：{ex.Message}");
            return new DeskPipeReply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeFailed, ex.Message));
        }
    }
}

/// <summary>
/// 使用者工作階段裡的幫手的要求：<c>hello</c>、<c>capture</c>。只收同一個工作階段登入的那個使用者（<see cref="DeskServiceRules.AdmitHelper"/>）。
/// 擷取一次只做一個（緩衝是共用的，回覆寫完才放開）；和上一張一模一樣、要求也一樣時回 unchanged、不送資料——
/// 鎖定畫面大部分時間不動，不必每次搬幾 MB
/// </summary>
internal sealed class AgentPipeHandler(ReleaseIdentity identity, bool system, int session, IDesktopCapturer capturer) : IDeskPipeHandler
{
    private static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim busy = new(1, 1);
    private byte[] last = [];
    private byte[] scratch = [];
    private DeskServiceCaptureRequest? lastRequest;
    private string? lastDesktop;

    public string? Admit(DeskServiceClientInfo client) => DeskServiceRules.AdmitHelper(client, session, system);

    public DeskPipeReply Handle(string line, DeskServiceClientInfo client)
    {
        if (!DeskServiceRules.TryParseRequest(line, out var op, out var request))
            return new DeskPipeReply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeInvalid, "要求不是 JSON 物件或沒有 op"));
        switch (op)
        {
            case DeskServiceRules.OpHello:
                return new DeskPipeReply(DeskServiceRules.HelloReply(new DeskServiceHello(DeskServiceRules.ProtocolVersion, identity.Version.ToString(), identity.Build,
                    system, session, [DeskServiceRules.FeatureCapture])));
            case DeskServiceRules.OpCapture:
                return DeskServiceRules.ParseCaptureRequest(request) is { } capture
                    ? Capture(capture)
                    : new DeskPipeReply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeInvalid, "擷取的範圍或大小不合格"));
            default:
                return new DeskPipeReply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeUnsupported, $"這一版的幫手不認得 {op}"));
        }
    }

    private DeskPipeReply Capture(DeskServiceCaptureRequest request)
    {
        // 上一個擷取的回覆還沒寫完（另一條連線）：等一下，等不到就回擷取不到，不讓這條連線的執行緒一直卡著
        if (!busy.Wait(BusyTimeout))
            return new DeskPipeReply(DeskServiceRules.CaptureReply(new DeskServiceCaptureHeader(DeskServiceRules.CaptureFailed, 0, null, false, 0)));
        var release = true;
        try
        {
            var bytes = DeskServiceRules.CaptureBytes(request.OutW, request.OutH);
            if (scratch.Length < bytes)
                scratch = new byte[bytes];
            DeskServiceCaptureHeader header;
            try
            {
                header = capturer.Capture(request, scratch);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                ServiceLog.Warn("擷取失敗：" + ex.Message);
                header = new DeskServiceCaptureHeader(DeskServiceRules.CaptureFailed, 0, null, false, 0);
            }

            if (header.Result != DeskServiceRules.CaptureOk)
            {
                // 擷取不到：下一次成功的一定整張回
                lastRequest = null;
                return new DeskPipeReply(DeskServiceRules.CaptureReply(header with { Result = DeskServiceRules.CaptureFailed, Bytes = 0 }));
            }

            // 和上一張比：同一個範圍與大小、同一個桌面、內容一樣，而且沒有要求整張
            var same = !request.Force && lastRequest is { } before && before with { Force = false } == request with { Force = false }
                       && lastDesktop == header.Desktop && last.Length >= bytes && scratch.AsSpan(0, bytes).SequenceEqual(last.AsSpan(0, bytes));
            if (same)
                return new DeskPipeReply(DeskServiceRules.CaptureReply(header with { Result = DeskServiceRules.CaptureUnchanged, Bytes = 0 }));

            (last, scratch) = (scratch, last);
            lastRequest = request;
            lastDesktop = header.Desktop;
            // 緩衝借給這次回覆：寫完（Done）才放開，下一個擷取才能覆寫它
            release = false;
            return new DeskPipeReply(DeskServiceRules.CaptureReply(header with { Bytes = bytes }), last, bytes, () => busy.Release());
        }
        finally
        {
            if (release)
                busy.Release();
        }
    }
}
