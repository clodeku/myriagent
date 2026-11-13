using System.Security.Cryptography;
using System.Text;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Update;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
/// 服務本體（工作階段 0）的要求：<c>hello</c>、<c>probe</c>（開一次性的幫手看輸入桌面）、<c>open</c>（開擷取用的幫手、回它的管道）、
/// <c>updateBegin</c>／<c>updatePut</c>／<c>updateApply</c>（服務自己更新，<see cref="SelfUpdater"/>）。
/// 只做用戶端自己那個工作階段的事：要求裡不收工作階段編號
/// </summary>
/// <param name="updater">自己更新（沒有給＝這個服務不自己更新：前景模式、沒有內建發佈金鑰的組建）</param>
internal sealed class ServicePipeHandler(ReleaseIdentity identity, bool system, IDeskSessionProbe probe, ISessionAgents agents, ISelfUpdater? updater = null) : IDeskPipeHandler
{
    public string? Admit(DeskServiceClientInfo client) => DeskServiceRules.Admit(client, system);

    public DeskPipeReply Handle(string line, DeskServiceClientInfo client)
    {
        if (!DeskServiceRules.TryParseRequest(line, out var op, out var request))
            return new DeskPipeReply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeInvalid, "要求不是 JSON 物件或沒有 op"));
        switch (op)
        {
            case DeskServiceRules.OpHello:
                return new DeskPipeReply(DeskServiceRules.HelloReply(new DeskServiceHello(DeskServiceRules.ProtocolVersion, identity.Version.ToString(), identity.Build,
                    system, client.SessionId, updater is null
                        ? [DeskServiceRules.FeatureProbe, DeskServiceRules.FeatureCapture]
                        : [DeskServiceRules.FeatureProbe, DeskServiceRules.FeatureCapture, DeskServiceRules.FeatureSelfUpdate])));
            case DeskServiceRules.OpUpdateBegin when updater is not null:
                return DeskServiceRules.ParseUpdateBegin(request) is { } sizes
                    ? Updating(() => DeskServiceRules.UpdateBeginReply(updater.Begin(sizes)))
                    : new DeskPipeReply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeInvalid, "更新的檔案清單不合格"));
            case DeskServiceRules.OpUpdatePut when updater is not null:
                return DeskServiceRules.ParseUpdatePut(request) is { } put
                    ? Updating(() => DeskServiceRules.UpdatePutReply(updater.Put(put.Id, put.Name, put.Offset, put.Data)))
                    : new DeskPipeReply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeInvalid, "這一段的格式不對"));
            case DeskServiceRules.OpUpdateApply when updater is not null:
                return DeskServiceRules.ParseUpdateApply(request) is { } applyId
                    ? Updating(() => DeskServiceRules.UpdateApplyReply(updater.Apply(applyId).Release))
                    : new DeskPipeReply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeInvalid, "沒有這一次更新的 id"));
            case DeskServiceRules.OpProbe:
                return Guarded(() => DeskServiceRules.ProbeReply(probe.Probe(client.SessionId)), $"看不到工作階段 {client.SessionId} 的輸入桌面");
            case DeskServiceRules.OpOpen:
                return Guarded(() => DeskServiceRules.OpenReply(agents.Open(client.SessionId)), $"開不了工作階段 {client.SessionId} 的擷取幫手");
            default:
                return new DeskPipeReply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeUnsupported, $"這一版的服務不認得 {op}"));
        }
    }

    private static DeskPipeReply Updating(Func<string> reply)
    {
        try
        {
            return new DeskPipeReply(reply());
        }
        catch (SelfUpdateException ex)
        {
            return new DeskPipeReply(DeskServiceRules.ErrorReply(ex.Code, ex.Message));
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
/// 鎖定畫面大部分時間不動，不必每次搬幾 MB。
/// <para>
/// 安全桌面的輸入（<c>secureOpen</c>、<c>secureInput</c>）：**連得上這條管道不等於可以輸入**——
/// 同一個使用者的任何程式都連得上，讓它們注入安全桌面就等於替它們按 UAC。所以輸入一定要是「看的一方」加密的：
/// <c>secureOpen</c> 收伺服器簽的 <c>deskOpen</c> 信封，以安裝時記下的伺服器身分金鑰驗過，才拿信封裡看的一方的公鑰和自己新產生的金鑰算出這個工作階段的金鑰；
/// <c>secureInput</c> 只注入以那把金鑰解得開的事件。app 只是轉送密文的管道：它沒有金鑰，造不出也改不了輸入
/// </para>
/// </summary>
/// <param name="injector">在安全桌面注入（沒有給＝這個幫手不收安全桌面的輸入）</param>
/// <param name="trust">讀安裝時記下的信任的伺服器（每次 <c>secureOpen</c> 讀一次；沒有回 null）</param>
/// <param name="banner">安全桌面上的提示列（擷取到安全桌面時顯示；沒有給就不顯示）</param>
internal sealed class AgentPipeHandler(ReleaseIdentity identity, bool system, int session, IDesktopCapturer capturer,
    IDesktopInjector? injector = null, Func<DeskServiceTrust?>? trust = null, IDeskBanner? banner = null) : IDeskPipeHandler
{
    private static readonly TimeSpan BusyTimeout = TimeSpan.FromSeconds(10);

    private readonly object secureGate = new();
    private string? secureSession;
    private byte[]? secureKey;
    private long secureLastN;

    /// <summary>驗過的信封裡的發起者（提示列寫出是誰在操作；還沒驗過是 null）</summary>
    private string? secureViewer;

    /// <summary>幫手最近一次擷取的那塊螢幕（輸入的座標相對於它：看的一方看到的就是這一塊）</summary>
    private DeskRect? lastBounds;

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
                    system, session, injector is null ? [DeskServiceRules.FeatureCapture] : [DeskServiceRules.FeatureCapture, DeskServiceRules.FeatureSecureInput])));
            case DeskServiceRules.OpCapture:
                return DeskServiceRules.ParseCaptureRequest(request) is { } capture
                    ? Capture(capture)
                    : new DeskPipeReply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeInvalid, "擷取的範圍或大小不合格"));
            case DeskServiceRules.OpSecureOpen when injector is not null:
                return new DeskPipeReply(SecureOpen(request));
            case DeskServiceRules.OpSecureInput when injector is not null:
                return new DeskPipeReply(SecureInput(request));
            default:
                return new DeskPipeReply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeUnsupported, $"這一版的幫手不認得 {op}"));
        }
    }

    /// <summary>驗信封、建立這個工作階段的金鑰，回自己的公鑰（私鑰算完就丟）。換一個工作階段時先放開上一個還按著的鍵</summary>
    private string SecureOpen(JObject request)
    {
        if (trust?.Invoke() is not { } anchor)
            return DeskServiceRules.ErrorReply(DeskServiceRules.CodeNoTrust, "這台的鎖定畫面支援沒有記下信任的伺服器（安裝時還沒配對）：要重新安裝才能在鎖定畫面輸入");
        if (DeskServiceRules.EnvelopeOf(request) is not { } envelope)
            return DeskServiceRules.ErrorReply(DeskServiceRules.CodeInvalid, "沒有信封");
        if (CommandEnvelope.VerifyDeskOpen(envelope, anchor.HubSpki, anchor.Aud, anchor.DeviceId, out var problem) is not { } proof)
        {
            ServiceLog.Warn("不收的遠端桌面信封：" + problem);
            return DeskServiceRules.ErrorReply(DeskServiceRules.CodeDenied, problem);
        }

        using var own = DeskSealRules.CreateKey();
        var publicKey = DeskSealRules.ExportPublicKey(own);
        var key = DeskSealRules.DeriveKey(own, proof.ViewerKey, proof.SessionId, proof.ViewerKey, publicKey, DeskSealRules.SecureInfo);
        if (key is null)
            return DeskServiceRules.ErrorReply(DeskServiceRules.CodeDenied, "看的一方的公鑰不合格");
        lock (secureGate)
        {
            if (secureKey is not null)
            {
                CryptographicOperations.ZeroMemory(secureKey);
                injector!.ReleaseAll();
            }

            secureSession = proof.SessionId;
            secureKey = key;
            secureLastN = 0;
            secureViewer = proof.IssuerLabel;
        }

        ServiceLog.Info($"安全桌面的輸入：工作階段 {proof.SessionId}（{proof.IssuerKind} {proof.IssuerLabel}）");
        return DeskServiceRules.SecureOpenReply(proof.SessionId, publicKey);
    }

    /// <summary>解開一包、注入目前的安全桌面。解不開（不是這個工作階段、編號沒有比上一包大、驗證碼不對）回 denied；不是安全桌面時解開了也不注入（回 0）</summary>
    private string SecureInput(JObject request)
    {
        if (DeskServiceRules.ParseSecureInput(request) is not { } input)
            return DeskServiceRules.ErrorReply(DeskServiceRules.CodeInvalid, "輸入的格式不對");
        IReadOnlyList<DeskInputEvent>? events;
        DeskRect? bounds;
        lock (secureGate)
        {
            if (secureKey is null || secureSession != input.Session || input.N <= secureLastN)
                return DeskServiceRules.ErrorReply(DeskServiceRules.CodeDenied, "不是目前的工作階段，或這一包已經收過");
            var plain = DeskSealRules.Open(secureKey, input.Session, input.N, input.Ct, DeskSealRules.SecureInfo);
            if (plain is null)
                return DeskServiceRules.ErrorReply(DeskServiceRules.CodeDenied, "解不開：不是看的一方加密的");
            secureLastN = input.N;
            events = ParseEvents(plain);
            CryptographicOperations.ZeroMemory(plain);
            bounds = lastBounds;
        }

        if (events is null)
            return DeskServiceRules.ErrorReply(DeskServiceRules.CodeInvalid, "事件的格式不對");
        // 還沒擷取過（不知道看的一方看的是哪一塊螢幕）、現在不是安全桌面：不注入
        var injected = events.Count > 0 && bounds is { } screen && injector!.Inject(events, screen) ? events.Count : 0;
        return DeskServiceRules.SecureInputReply(injected);
    }

    private static IReadOnlyList<DeskInputEvent>? ParseEvents(byte[] plain)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(plain))) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 };
            return JToken.ReadFrom(reader) is JObject body && body["e2e"] is null ? DeskRules.ParseInput(body) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private DeskPipeReply Capture(DeskServiceCaptureRequest request)
    {
        lock (secureGate)
            lastBounds = new DeskRect(request.X, request.Y, request.W, request.H);
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

            // 有人在看安全桌面：讓坐在這台前面的人知道（提示列一陣子沒被叫就自己收掉）
            if (banner is not null && header is { Result: DeskServiceRules.CaptureOk, Secure: true })
            {
                string? viewer;
                lock (secureGate)
                    viewer = secureViewer;
                banner.Show(SecureDeskBanner.TextFor(viewer));
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
