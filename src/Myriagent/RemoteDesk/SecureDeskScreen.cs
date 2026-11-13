using Myriagent.Core;
using Myriagent.Remote;

namespace Myriagent.RemoteDesk;

/// <summary>
/// 安全桌面（鎖定畫面、UAC 的確認畫面）的畫面來源：鎖定畫面支援的幫手（真的經管道問，測試以假的換掉）。
/// 只在擷取執行緒呼叫
/// </summary>
internal interface ISecureDeskCapture : IDisposable
{
    /// <summary>
    /// 把 <paramref name="bounds"/> 的畫面縮成指定大小寫進 <paramref name="bgra"/>。回 <see cref="DeskCapture.Ok"/>（寫了）或
    /// <see cref="DeskCapture.Unchanged"/>（和它上一次給的一樣，沒寫）；用不了（沒裝、連不上、幫手也擷取不到）回 null
    /// </summary>
    DeskCapture? Capture(DeskRect bounds, int width, int height, byte[] bgra, bool force);
}

/// <summary>畫面來源現在只能看、不能操作（<see cref="DeskSession"/> 不注入輸入，並把說明告訴看的一方）</summary>
internal interface IDeskViewOnly
{
    /// <summary>給看的一方的說明；可以操作時是 null</summary>
    string? ViewOnlyNote { get; }
}

/// <summary>
/// 平常的擷取（<see cref="DxgiDeskScreen"/>）加上安全桌面：平常的那一個說「鎖定中」或「受保護的畫面」時，改向鎖定畫面支援的幫手要畫面；
/// 幫手給得出來就照常送影格（只能看——在安全桌面輸入還沒有做），給不出來（沒裝服務）就和以前一樣回報擷取不到的原因。
/// 兩個來源各自記得「上一張」：換來源的那一次一律要整張，不然一邊說「沒變」時工作階段手上的其實是另一邊的畫面
/// </summary>
internal sealed class SecureDeskScreen(IDeskScreen inner, ISecureDeskCapture secure) : IDeskScreen, IDeskViewOnly
{
    public const string ViewOnlyText = "那台目前是鎖定畫面或系統管理員確認畫面：看得到，但還不能操作（開發中）";

    /// <summary>上一張是幫手給的（擷取執行緒寫，注入輸入的執行緒讀）</summary>
    private volatile bool secureActive;

    public string? ViewOnlyNote => secureActive ? ViewOnlyText : null;

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
            secureActive = false;
            return result;
        }

        secureActive = true;
        return captured;
    }

    /// <summary>游標：安全桌面上的游標這裡讀不到（讀到的是使用者桌面的），不送</summary>
    public DeskCursorState? Cursor() => secureActive ? null : inner.Cursor();

    public DeskCursorImage? CursorImage() => secureActive ? null : inner.CursorImage();

    /// <summary>只能看的時候不注入：一般權限送的輸入進不了安全桌面，鎖定時還會白白叫醒那台的螢幕</summary>
    public void Inject(IReadOnlyList<DeskInputEvent> events, DeskRect screen, DeskRect virtualScreen)
    {
        if (!secureActive)
            inner.Inject(events, screen, virtualScreen);
    }

    public void ReleaseAll() => inner.ReleaseAll();

    public void CaptureEnded() => inner.CaptureEnded();

    public void Dispose()
    {
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
/// 連不上時也不每一格都試——擷取迴圈每秒好幾次，不能每次都等逾時
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
    private DeskServiceClient? helper;
    private long retryAtTick;
    private long quietUntilTick;
    private (DeskRect Bounds, int W, int H) quietFor;
    private string? lastDesktop;

    public DeskCapture? Capture(DeskRect bounds, int width, int height, byte[] bgra, bool force)
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
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                Log.Info("鎖定畫面支援連不上，稍後再試：" + ex.Message);
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
        lastDesktop = null;
        quietUntilTick = 0;
    }

    public void Dispose() => Drop();
}
