using System.Collections.Concurrent;
using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Security;

namespace Myriagent.RemoteDesk;

/// <summary>
/// 安全桌面（鎖定畫面、UAC 的確認畫面）的畫面與輸入：鎖定畫面支援的幫手（真的經管道問，測試以假的換掉）
/// </summary>
internal interface ISecureDeskCapture : IDisposable
{
    /// <summary>
    /// 把 <paramref name="bounds"/> 的畫面縮成指定大小寫進 <paramref name="bgra"/>。回 <see cref="DeskCapture.Ok"/>（寫了）或
    /// <see cref="DeskCapture.Unchanged"/>（和它上一次給的一樣，沒寫）；用不了（沒裝、連不上、幫手也擷取不到）回 null。只在擷取執行緒呼叫
    /// </summary>
    DeskCapture? Capture(DeskRect bounds, int width, int height, byte[] bgra, bool force);

    /// <summary>
    /// 請幫手建立安全桌面輸入的金鑰：把伺服器簽的 deskOpen 信封交給它驗（它以安裝時記下的伺服器身分金鑰驗），回它這個工作階段的公鑰。
    /// 做不到（還沒連上幫手、幫手不會、沒有信任的伺服器、信封驗不過）回 null。擷取執行緒呼叫
    /// </summary>
    string? OpenSecureInput(CommandEnvelope envelope, string sessionId);

    /// <summary>把一包以服務的金鑰加密的輸入轉給幫手（這裡解不開）。任何執行緒；會等幫手回覆，不要在同步的執行緒直接呼叫</summary>
    void SendSecureInput(string sessionId, long n, string ct);
}

/// <summary>畫面來源現在只能看、不能操作（<see cref="DeskSession"/> 把說明告訴看的一方）</summary>
internal interface IDeskViewOnly
{
    /// <summary>給看的一方的說明；可以操作時是 null</summary>
    string? ViewOnlyNote { get; }
}

/// <summary>
/// 安全桌面的輸入：畫面是安全桌面時，輸入不由 app 注入（一般權限送不進去），而是看的一方以**服務的金鑰**加密、
/// app 原樣轉給服務的幫手。app 沒有那把金鑰：造不出也改不了輸入——所以同一個使用者的其他程式（包括被改掉的 app）不能經它按 UAC
/// </summary>
internal interface IDeskSecureInput
{
    /// <summary>中樞在開始被看時給：這個工作階段的 deskOpen 信封（伺服器簽的原樣；沒有就只能看）</summary>
    void OfferEnvelope(string sessionId, CommandEnvelope? envelope);

    /// <summary>現在的畫面是安全桌面（幫手擷取的）：app 不注入輸入</summary>
    bool SecureActive { get; }

    /// <summary>服務這個工作階段的公鑰（幫手驗過信封之後才有）：告訴看的一方，它才能把安全桌面的輸入加密給服務</summary>
    string? ServicePublicKey { get; }

    /// <summary>轉一包以服務的金鑰加密的輸入（任何執行緒，不等）</summary>
    void SubmitSecureInput(long n, string ct);
}

/// <summary>
/// 平常的擷取（<see cref="DxgiDeskScreen"/>）加上安全桌面：平常的那一個說「鎖定中」或「受保護的畫面」時，改向鎖定畫面支援的幫手要畫面；
/// 幫手給得出來就照常送影格，給不出來（沒裝服務）就和以前一樣回報擷取不到的原因。
/// 兩個來源各自記得「上一張」：換來源的那一次一律要整張，不然一邊說「沒變」時工作階段手上的其實是另一邊的畫面。
/// 安全桌面的輸入見 <see cref="IDeskSecureInput"/>：幫手驗過信封、回了公鑰才能操作，否則只能看（<see cref="ViewOnlyNote"/>）
/// </summary>
internal sealed class SecureDeskScreen(IDeskScreen inner, ISecureDeskCapture secure) : IDeskScreen, IDeskViewOnly, IDeskSecureInput
{
    public const string ViewOnlyText = "那台目前是鎖定畫面或系統管理員確認畫面：看得到，但這次不能操作（那台的鎖定畫面支援要重新安裝或更新，或這裡的網頁太舊）";

    /// <summary>幫手不收（或還沒連上）之後隔多久再試一次建立輸入的金鑰</summary>
    private const int OpenRetryMs = 10_000;

    /// <summary>上一張是幫手給的（擷取執行緒寫，其他執行緒讀）</summary>
    private volatile bool secureActive;

    private volatile string? servicePublic;
    private string? sessionId;
    private CommandEnvelope? envelope;
    private long openRetryAtTick;

    private readonly object senderGate = new();
    private BlockingCollection<(long N, string Ct)>? outgoing;
    private Thread? sender;

    public string? ViewOnlyNote => secureActive && servicePublic is null ? ViewOnlyText : null;

    public bool SecureActive => secureActive;

    public string? ServicePublicKey => servicePublic;

    public void OfferEnvelope(string sessionId, CommandEnvelope? envelope)
    {
        this.sessionId = sessionId;
        this.envelope = envelope;
    }

    public IReadOnlyList<DeskScreenInfo> Screens() => inner.Screens();

    public DeskRect VirtualBounds() => inner.VirtualBounds();

    public (int W, int H) OutputSize(DeskRect bounds, int? maxWidth) => inner.OutputSize(bounds, maxWidth);

    public DeskCapture Capture(DeskRect bounds, int width, int height, byte[] bgra, bool force)
    {
        // 從幫手換回來的那一次要整張
        var result = inner.Capture(bounds, width, height, bgra, force || secureActive);
        if (result is not (DeskCapture.Locked or DeskCapture.Failed))
        {
            secureActive = false;
            return result;
        }

        // 換到幫手的那一次也要整張
        var fromHelper = secure.Capture(bounds, width, height, bgra, force || !secureActive);
        if (fromHelper is not { } captured)
        {
            // 幫手用不了（連線斷了、它重開了）：它那一端的金鑰也沒了，下次連上要重新建立
            secureActive = false;
            servicePublic = null;
            return result;
        }

        secureActive = true;
        EnsureSecureInput();
        return captured;
    }

    /// <summary>擷取執行緒：安全桌面第一次出現時（之後每隔一陣子再試）請幫手驗信封、建立輸入的金鑰</summary>
    private void EnsureSecureInput()
    {
        if (servicePublic is not null || envelope is not { } proof || sessionId is not { } session)
            return;
        var now = Environment.TickCount64;
        if (now < openRetryAtTick)
            return;
        openRetryAtTick = now + OpenRetryMs;
        servicePublic = secure.OpenSecureInput(proof, session);
    }

    public void SubmitSecureInput(long n, string ct)
    {
        if (servicePublic is null || sessionId is not { } session)
            return;
        BlockingCollection<(long N, string Ct)> queue;
        lock (senderGate)
        {
            if (outgoing is null)
            {
                // 轉給幫手要等它回覆：收封包的是同步的執行緒，不能在那裡等，另開一條專用的照順序送
                outgoing = new BlockingCollection<(long N, string Ct)>(256);
                var own = outgoing;
                sender = new Thread(() =>
                {
                    foreach (var (number, sealedText) in own.GetConsumingEnumerable())
                        secure.SendSecureInput(session, number, sealedText);
                }) { IsBackground = true, Name = "遠端桌面 安全桌面的輸入" };
                sender.Start();
            }

            queue = outgoing;
        }

        // 排太多（幫手卡住）就丟：輸入晚了幾秒才到比不到更糟
        queue.TryAdd((n, ct));
    }

    /// <summary>游標：安全桌面上的游標這裡讀不到（讀到的是使用者桌面的），不送</summary>
    public DeskCursorState? Cursor() => secureActive ? null : inner.Cursor();

    public DeskCursorImage? CursorImage() => secureActive ? null : inner.CursorImage();

    /// <summary>安全桌面時 app 不注入：一般權限送的輸入進不了安全桌面，鎖定時還會白白叫醒那台的螢幕</summary>
    public void Inject(IReadOnlyList<DeskInputEvent> events, DeskRect screen, DeskRect virtualScreen)
    {
        if (!secureActive)
            inner.Inject(events, screen, virtualScreen);
    }

    public void ReleaseAll() => inner.ReleaseAll();

    public void CaptureEnded() => inner.CaptureEnded();

    public void Dispose()
    {
        lock (senderGate)
            outgoing?.CompleteAdding();
        sender?.Join(TimeSpan.FromSeconds(2));
        try
        {
            secure.Dispose();
        }
        finally
        {
            inner.Dispose();
        }
    }
}

/// <summary>
/// 真的向鎖定畫面支援要畫面：先連服務（<c>open</c>：在這個工作階段開擷取用的幫手），再直接連幫手、每次 <c>capture</c>。
/// 連線留著重複用；斷了、逾時就丟掉，隔一下再連。沒裝服務時連試都不試（每 30 秒看一次安裝紀錄在不在），
/// 連不上時也不每一格都試——擷取迴圈每秒好幾次，不能每次都等逾時。
/// 擷取與安全桌面的輸入共用同一條連線（一次一個要求）：輸入最多等一次擷取的時間
/// </summary>
/// <param name="tick">時鐘（毫秒；測試給假的）</param>
internal sealed class SecureDeskCapture(Func<bool> installed, string servicePipe, Func<long>? tick = null) : ISecureDeskCapture
{
    private const int RetryNotInstalledMs = 30_000;
    private const int RetryFailedMs = 5_000;

    /// <summary>
    /// 畫面沒變之後隔多久才再問幫手：鎖定畫面大部分時間不動，幫手每問一次就整張擷取、比對一次（4K 約幾十毫秒），
    /// 工作階段的迴圈每秒問十幾次太浪費。有變化時照迴圈的步調問；要求整張的一律馬上問
    /// </summary>
    public const int UnchangedIntervalMs = 250;

    private readonly Func<long> tick = tick ?? (() => Environment.TickCount64);

    /// <summary>連線一次只給一個要求用（擷取執行緒與送輸入的執行緒）</summary>
    private readonly object gate = new();
    private DeskServiceClient? helper;
    private bool helperSecureInput;
    private long retryAtTick;
    private long quietUntilTick;
    private (DeskRect Bounds, int W, int H) quietFor;
    private string? lastDesktop;
    private string? lastSecureProblem;

    public DeskCapture? Capture(DeskRect bounds, int width, int height, byte[] bgra, bool force)
    {
        lock (gate)
        {
            var now = tick();
            if (!force && helper is not null && now < quietUntilTick && quietFor == (bounds, width, height))
                return DeskCapture.Unchanged;
            if (helper is null)
            {
                if (now < retryAtTick)
                    return null;
                if (!installed())
                {
                    retryAtTick = now + RetryNotInstalledMs;
                    return null;
                }

                try
                {
                    helper = ConnectHelper();
                    helperSecureInput = helper.Hello().Features.Contains(DeskServiceRules.FeatureSecureInput);
                }
                catch (Exception ex) when (IsConnectionError(ex))
                {
                    Log.Info("鎖定畫面支援連不上，稍後再試：" + ex.Message);
                    Drop();
                    retryAtTick = now + RetryFailedMs;
                    return null;
                }
            }

            try
            {
                // 這個要求是整張的時候，幫手也要當成整張（它自己記的「上一張」可能是別的大小或別的時候的）
                var header = helper.Capture(new DeskServiceCaptureRequest(bounds.X, bounds.Y, bounds.W, bounds.H, width, height, force), bgra);
                if (header.Desktop != lastDesktop)
                {
                    lastDesktop = header.Desktop;
                    Log.Info($"鎖定畫面支援擷取的桌面：{header.Desktop ?? "（開不了）"}{(header.Error != 0 ? $"（錯誤 {header.Error}）" : string.Empty)}");
                }

                quietFor = (bounds, width, height);
                quietUntilTick = header.Result == DeskServiceRules.CaptureUnchanged ? tick() + UnchangedIntervalMs : 0;
                return header.Result switch
                {
                    DeskServiceRules.CaptureOk => DeskCapture.Ok,
                    DeskServiceRules.CaptureUnchanged => DeskCapture.Unchanged,
                    // 幫手也擷取不到（一般權限的前景模式、桌面開不了）：照原本的原因回報
                    _ => null,
                };
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                Log.Info("鎖定畫面支援的連線斷了，稍後再連：" + ex.Message);
                Drop();
                retryAtTick = tick() + RetryFailedMs;
                return null;
            }
        }
    }

    public string? OpenSecureInput(CommandEnvelope envelope, string sessionId)
    {
        lock (gate)
        {
            if (helper is null || !helperSecureInput)
                return null;
            try
            {
                var publicKey = helper.SecureOpen(envelope, sessionId);
                lastSecureProblem = null;
                Log.Info($"鎖定畫面支援：工作階段 {sessionId} 可以在安全桌面輸入");
                return publicKey;
            }
            catch (InvalidOperationException ex)
            {
                // 幫手不收（沒有信任的伺服器、信封驗不過）：同一個原因只記一次
                if (ex.Message != lastSecureProblem)
                {
                    lastSecureProblem = ex.Message;
                    Log.Warn("鎖定畫面支援不收安全桌面的輸入：" + ex.Message);
                }

                return null;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                Log.Info("鎖定畫面支援的連線斷了，稍後再連：" + ex.Message);
                Drop();
                retryAtTick = tick() + RetryFailedMs;
                return null;
            }
        }
    }

    public void SendSecureInput(string sessionId, long n, string ct)
    {
        lock (gate)
        {
            if (helper is null)
                return;
            try
            {
                helper.SecureInput(sessionId, n, ct);
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message != lastSecureProblem)
                {
                    lastSecureProblem = ex.Message;
                    Log.Warn("鎖定畫面支援沒有收下這包輸入：" + ex.Message);
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                Log.Info("鎖定畫面支援的連線斷了，稍後再連：" + ex.Message);
                Drop();
                retryAtTick = tick() + RetryFailedMs;
            }
        }
    }

    private DeskServiceClient ConnectHelper()
    {
        string pipe;
        using (var service = DeskServiceClient.Connect(servicePipe, TimeSpan.FromMilliseconds(500)))
        {
            if (!service.Hello().Features.Contains(DeskServiceRules.FeatureCapture))
                throw new InvalidOperationException("裝的服務還不會擷取（要更新）");
            pipe = service.Open();
        }

        return DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(3));
    }

    private static bool IsConnectionError(Exception ex)
        => ex is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException;

    private void Drop()
    {
        helper?.Dispose();
        helper = null;
        helperSecureInput = false;
        lastDesktop = null;
        quietUntilTick = 0;
    }

    public void Dispose()
    {
        lock (gate)
            Drop();
    }
}
