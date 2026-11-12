using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Files;
using Myriagent.Hub;
using Myriagent.Sync;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.UI;

/// <summary>
/// 一個視窗裡的 WebView2：提供嵌入的介面網頁（https://myriagent.local/，含 CSP）、/media（本機圖片影片，支援 Range）、
/// /thumb（Windows 外殼縮圖，背景產生不卡 UI）、/m/ 與 /t/（複本的圖片影片與縮圖，經伺服器向擁有者電腦要）、把網頁指令轉給中樞、攔下站外導覽改用預設瀏覽器開。
/// 所有視窗共用同一個 WebView2 環境（同一個瀏覽器程序，省記憶體）。
/// </summary>
internal class AgentWebView : IAgentView
{
    /// <summary>網頁的內容安全政策：只執行本頁腳本、圖片與影片只從本頁來源（含 /media、/thumb）載入</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; " +
        "font-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; object-src 'none'";

    /// <summary>/media 開檔最多等多久：網路磁碟離線時開檔會卡到 SMB 逾時（約 40 秒），先回 404，晚到的檔案直接關掉</summary>
    private static readonly TimeSpan MediaOpenTimeout = TimeSpan.FromSeconds(10);

    /// <summary>複本的 /m/、/t/ 最多等多久（來源自己 20 秒逾時，這裡是保險）</summary>
    private static readonly TimeSpan ReplicaFetchTimeout = TimeSpan.FromSeconds(25);

    private static Task<CoreWebView2Environment>? sharedEnvironment;

    private readonly Form owner;
    private readonly WebView2 webView;
    private readonly AgentHub hub;
    private readonly ThumbnailService thumbnails;
    private readonly UnresponsiveTracker unresponsive = new();
    private CoreWebView2Environment? environment;
    private bool attached;
    private bool reloadPromptOpen;
    private bool darkTheme = true; // 送出 index.html 時寫進 <html data-theme>（見 WebAssetRules.ApplyInitialTheme）

    public AgentWebView(Form owner, WebView2 webView, AgentHub hub, ThumbnailService thumbnails, bool isPopout)
    {
        this.owner = owner;
        this.webView = webView;
        this.hub = hub;
        this.thumbnails = thumbnails;
        IsPopout = isPopout;
        // 沒有人看得到畫面（鎖定、螢幕關閉；SetScreenUnseen）：每個視窗的網頁一起停畫，視窗關掉就不再聽
        ScreenUnseenChanged += ApplyHidden;
        owner.Disposed += (_, _) => ScreenUnseenChanged -= ApplyHidden;
        ApplyHidden();
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public bool IsPopout { get; }

    /// <summary>載入完成（網頁送出 ready 之前）時觸發</summary>
    public event Action? Loaded;

    public WebView2 Control => webView;

    /// <summary>提早開始建立 WebView2 環境（啟動瀏覽器程序最花時間），與 app 其他初始化並行</summary>
    public static void PrewarmEnvironment() => _ = GetEnvironmentAsync();

    private static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        return sharedEnvironment ??= Create();

        static async Task<CoreWebView2Environment> Create()
        {
            Directory.CreateDirectory(AppPaths.WebViewDataDirectory);
            var options = new CoreWebView2EnvironmentOptions
            {
                Language = "zh-TW",
                // 介面只載入本機嵌入網頁：關掉 SmartScreen 與背景網路服務，少一些程序與記憶體
                AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection --disable-background-networking",
            };
            return await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDataDirectory, options);
        }
    }

    /// <summary>視窗藏到系統匣時降低 WebView2 記憶體用量，顯示時恢復</summary>
    public void SetMemoryTarget(bool low)
    {
        try
        {
            if (webView.CoreWebView2 != null)
                webView.CoreWebView2.MemoryUsageTargetLevel = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
        }
    }

    private bool focusedBeforeHide;
    private bool minimized;

    /// <summary>這個檢視把控制項藏起來了（最小化，或沒有人看得到畫面）</summary>
    private bool hiddenByState;

    /// <summary>沒有人看得到這個工作階段的畫面（鎖定、螢幕關閉；ScreenPresence 判斷，AppController 轉進來）。UI 執行緒</summary>
    internal static bool ScreenUnseen { get; private set; }

    private static event Action? ScreenUnseenChanged;

    /// <summary>畫面看不看得到變了：每個視窗的網頁跟著停畫或恢復（UI 執行緒）</summary>
    internal static void SetScreenUnseen(bool unseen)
    {
        if (ScreenUnseen == unseen)
            return;
        ScreenUnseen = unseen;
        ScreenUnseenChanged?.Invoke();
    }

    /// <summary>
    /// 視窗最小化時讓網頁知道看不到：WebView2 是子視窗，收不到最小化，頁面的 document.hidden 一直是 false，
    /// 持續的動畫、電腦狀態面板的每秒取樣照跑（2026-10-23 實測：最小化的視窗渲染程序照樣吃掉約一顆核心）。
    /// WinForms 的 WebView2 控制項把 Visible 傳給 CoreWebView2Controller.IsVisible；還原時設回看得見，焦點原本在網頁裡的交回網頁。
    /// 電腦鎖定、螢幕關閉時（ScreenUnseen，2026-11-12）走同一條路：沒有人看得到，網頁照樣不畫
    /// </summary>
    public void SetMinimized(bool minimized)
    {
        this.minimized = minimized;
        ApplyHidden();
    }

    private void ApplyHidden()
    {
        var hide = minimized || ScreenUnseen;
        if (hide == hiddenByState || webView.IsDisposed)
            return;
        hiddenByState = hide;
        if (hide)
            focusedBeforeHide = webView.ContainsFocus;
        webView.Visible = !hide;
        if (!hide && focusedBeforeHide)
            webView.Focus();
    }

    public async Task InitializeAsync(string query, bool dark)
    {
        darkTheme = dark;
        webView.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(0x18, 0x18, 0x1B) : System.Drawing.Color.FromArgb(0xF7, 0xF7, 0xF8);
        environment = await GetEnvironmentAsync();
        await webView.EnsureCoreWebView2Async(environment);
        if (owner.IsDisposed)
            return;

        var core = webView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached || Environment.GetEnvironmentVariable("MYRIAGENT_AGENT_DEVTOOLS") == "1";
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreDefaultScriptDialogsEnabled = true;
        core.Settings.IsZoomControlEnabled = false;
        // 觸控的左右滑是叫出與收起兩側面板（Web/drawer.js），不是上一頁／下一頁（單頁程式，沒有要回去的頁面）
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = core.Settings.AreDevToolsEnabled;
        core.AddWebResourceRequestedFilter($"{AgentMarkdownRenderer.Origin}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += (_, _) => Loaded?.Invoke();
        core.NewWindowRequested += OnNewWindowRequested;
        // 輸入區的語音訊息（錄音）：本頁要用麥克風時直接允許，不跳 WebView2 的詢問（其他權限照預設）
        core.PermissionRequested += OnPermissionRequested;
        core.ProcessFailed += OnProcessFailed;
        core.DocumentTitleChanged += OnDocumentTitleChanged;

        hub.AttachView(this);
        attached = true;
        core.Navigate($"{AgentMarkdownRenderer.Origin}/index.html{query}");
    }

    public void SetBackground(bool dark)
    {
        darkTheme = dark;
        try
        {
            webView.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(0x18, 0x18, 0x1B) : System.Drawing.Color.FromArgb(0xF7, 0xF7, 0xF8);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
        }
    }

    public void Detach()
    {
        if (!attached)
            return;
        attached = false;
        hub.DetachView(Id);
    }

    // ───────────────────────────── IAgentView ─────────────────────────────

    public void PostToWeb(JObject message)
    {
        if (owner.IsDisposed || webView.IsDisposed || webView.CoreWebView2 == null)
            return;
        try
        {
            webView.CoreWebView2.PostWebMessageAsJson(message.ToString(Formatting.None));
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ObjectDisposedException)
        {
        }
    }

    // ───────────────────────────── 資源 ─────────────────────────────

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (environment == null || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)
            || !uri.Host.Equals(AgentMarkdownRenderer.Host, StringComparison.OrdinalIgnoreCase))
            return;

        // WP4.7 步 12：複本（其他電腦的對話）的 /m/、/t/ 先比對前綴（本機的 /media、/thumb、/open 照舊）
        var replica = ReplicaMediaRouteRules.Match(uri);
        if (replica.Kind != ReplicaRouteKind.None)
        {
            ServeReplica(e, replica);
            return;
        }

        switch (uri.AbsolutePath.ToLowerInvariant())
        {
            case "/media":
                ServeMedia(e, GetQueryValue(uri, "path"));
                return;
            case "/thumb":
                ServeThumbnail(e, GetQueryValue(uri, "path"), int.TryParse(GetQueryValue(uri, "size"), out var size) ? size : 160);
                return;
            case "/open":
                e.Response = NotFound();
                return;
        }

        var name = uri.AbsolutePath.TrimStart('/');
        if (name.Length == 0)
            name = "index.html";
        var contentType = WebAssets.ContentType(name);
        var stream = contentType == null || name.Contains('/') ? null : WebAssets.Open(name);
        if (stream == null)
        {
            e.Response = NotFound();
            return;
        }

        var headers = $"Content-Type: {contentType}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff";
        if (name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            headers += $"\r\nContent-Security-Policy: {ContentSecurityPolicy}";
            stream = WithInitialTheme(stream);
        }
        e.Response = environment.CreateWebResourceResponse(stream, 200, "OK", headers);
    }

    /// <summary>index.html 的 &lt;html data-theme&gt; 換成目前主題：init 到達前的第一個影格就是正確的深淺色</summary>
    private MemoryStream WithInitialTheme(Stream source)
    {
        using (source)
        using (var reader = new StreamReader(source, Encoding.UTF8))
            return new MemoryStream(Encoding.UTF8.GetBytes(WebAssetRules.ApplyInitialTheme(reader.ReadToEnd(), darkTheme)));
    }

    /// <summary>
    /// 本機圖片／影片：只給中樞登記過、本機磁碟上、白名單格式的檔案；有 Range 標頭就回 206 部分內容。
    /// 開檔在背景執行緒：磁碟代號也可能是對應的網路磁碟，離線時開檔會卡到 SMB 逾時，不能卡住 UI 執行緒（所有視窗會一起凍結）
    /// </summary>
    private void ServeMedia(CoreWebView2WebResourceRequestedEventArgs e, string? path)
    {
        var contentType = path == null ? null : AgentAttachmentRules.GetMediaContentType(path);
        if (path == null || contentType == null || !AgentAttachmentRules.IsLocalDrivePath(path) || !hub.IsMediaAllowed(path))
        {
            e.Response = NotFound();
            return;
        }

        var rangeHeader = RangeHeaderOf(e);
        _ = ServeInBackgroundAsync(e, "讀取媒體檔", () => Task.FromResult(PrepareMedia(path, contentType, rangeHeader)), MediaOpenTimeout);
    }

    private static string? RangeHeaderOf(CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            if (e.Request.Headers.Contains("Range"))
                return e.Request.Headers.GetHeader("Range");
        }
        catch (COMException)
        {
        }

        return null;
    }

    /// <summary>
    /// 複本的原檔與縮圖（WP4.7 步 12，D38）：鍵不合格、中樞沒登記這個鍵、沒有媒體來源（沒顯示複本、沒協商出 media1）直接 404，來源不呼叫；
    /// 其餘在背景向伺服器要（E9，內容整份緩衝在記憶體，來源自己 20 秒逾時），好了回 UI 執行緒建立回應
    /// </summary>
    private void ServeReplica(CoreWebView2WebResourceRequestedEventArgs e, ReplicaRoute route)
    {
        var range = route is { Kind: ReplicaRouteKind.Valid, Purpose: MediaPurposes.Media } ? RangeHeaderOf(e) : null;
        if (RouteReplica(route, range, hub.ReplicaMediaFor) is not { } work)
        {
            e.Response = NotFound();
            return;
        }

        _ = ServeInBackgroundAsync(e, "取複本的媒體", work, ReplicaFetchTimeout);
    }

    /// <summary>
    /// 複本路由要做的事（UI 執行緒呼叫；<paramref name="sourceFor"/> 是中樞的 <see cref="AgentHub.ReplicaMediaFor"/>）：
    /// 不合格的路由、沒有來源回 null（404，來源不呼叫）；否則回在背景執行的準備工作
    /// </summary>
    internal static Func<Task<PreparedResponse?>>? RouteReplica(ReplicaRoute route, string? rangeHeader, Func<string, string, IReplicaMediaSource?> sourceFor)
    {
        if (route.Kind != ReplicaRouteKind.Valid || route.Key is not { } key || route.Purpose is not { } purpose
            || sourceFor(key, purpose) is not { } source)
            return null;
        var range = purpose == MediaPurposes.Media ? rangeHeader : null;
        return () => PrepareReplicaAsync(source, key, purpose, route.Size, range, CancellationToken.None);
    }

    /// <summary>向來源要內容並準備回應（背景執行緒）；來源回 null、狀態碼或標頭不合格回 null（404）</summary>
    internal static async Task<PreparedResponse?> PrepareReplicaAsync(IReplicaMediaSource source, string key, string purpose, int? size, string? rangeHeader,
        CancellationToken ct)
    {
        if (await source.FetchAsync(key, purpose, size, rangeHeader, ct).ConfigureAwait(false) is not { } reply
            || ReplicaMediaRouteRules.Headers(reply, purpose == MediaPurposes.Media) is not { } headers)
            return null;
        return new PreparedResponse(new MemoryStream(reply.Body, writable: false), reply.Status, ReplicaMediaRouteRules.ReasonPhrase(reply.Status)!, headers);
    }

    /// <summary>開檔並依 Range 準備 200／206 回應（在背景執行緒呼叫）；找不到、是資料夾、沒有權限回 null（404）</summary>
    internal static PreparedResponse? PrepareMedia(string path, string contentType, string? rangeHeader)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            long length = stream.Length;
            if (RangeRules.Parse(rangeHeader, length) is { } r)
            {
                long count = r.End - r.Start + 1;
                return new PreparedResponse(new SubStream(stream, r.Start, count), 206, "Partial Content",
                    $"Content-Type: {contentType}\r\nContent-Length: {count}\r\nContent-Range: bytes {r.Start}-{r.End}/{length}\r\nAccept-Ranges: bytes\r\nCache-Control: no-cache\r\nX-Content-Type-Options: nosniff");
            }

            return new PreparedResponse(stream, 200, "OK",
                $"Content-Type: {contentType}\r\nContent-Length: {length}\r\nAccept-Ranges: bytes\r\nCache-Control: no-cache\r\nX-Content-Type-Options: nosniff");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            stream?.Dispose();
            return null;
        }
    }

    /// <summary>縮圖在背景執行緒產生，完成後回 UI 執行緒建立回應</summary>
    private void ServeThumbnail(CoreWebView2WebResourceRequestedEventArgs e, string? path, int size)
    {
        if (path == null || !AgentAttachmentRules.IsLocalDrivePath(path) || !hub.IsThumbAllowed(path))
        {
            e.Response = NotFound();
            return;
        }

        _ = ServeInBackgroundAsync(e, "縮圖", async () => await thumbnails.GetAsync(path, size) is { } bytes
            ? new PreparedResponse(new MemoryStream(bytes), 200, "OK", "Content-Type: image/png\r\nCache-Control: max-age=300\r\nX-Content-Type-Options: nosniff")
            : null, timeout: null);
    }

    /// <summary>
    /// 回應內容在背景執行緒準備（deferral），好了再回到 UI 執行緒建立回應物件：WebResourceRequested 在 UI 執行緒觸發，await 之後回到同一條執行緒。
    /// timeout：準備太久就先回 404，晚到的內容直接釋放；null＝等到好為止
    /// </summary>
    private async Task ServeInBackgroundAsync(CoreWebView2WebResourceRequestedEventArgs e, string what, Func<Task<PreparedResponse?>> prepare, TimeSpan? timeout)
    {
        var deferral = e.GetDeferral();
        var work = Task.Run(prepare);
        PreparedResponse? prepared = null;
        try
        {
            prepared = timeout is { } limit ? await work.WaitAsync(limit) : await work;
        }
        catch (TimeoutException) when (timeout != null)
        {
            Log.Warn($"{what}逾時（超過 {timeout.Value.TotalSeconds:0} 秒，網路磁碟可能離線）");
            _ = work.ContinueWith(t => t.Result?.Content?.Dispose(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Log.Warn($"{what}失敗：{ex.Message}");
        }

        try
        {
            if (prepared != null && !owner.IsDisposed)
            {
                e.Response = environment!.CreateWebResourceResponse(prepared.Content, prepared.StatusCode, prepared.ReasonPhrase, prepared.Headers);
                prepared = null;   // 內容已交給 WebView2
            }
            else
            {
                e.Response = NotFound();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ObjectDisposedException)
        {
        }
        finally
        {
            prepared?.Content?.Dispose();
            try
            {
                deferral.Complete();
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
            }
        }
    }

    /// <summary>在背景準備好的回應（UI 執行緒只負責把它變成 WebView2 的回應物件）</summary>
    internal sealed record PreparedResponse(Stream? Content, int StatusCode, string ReasonPhrase, string Headers);

    private CoreWebView2WebResourceResponse NotFound()
        => environment!.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");

    internal static string? GetQueryValue(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = eq < 0 ? string.Empty : part[(eq + 1)..];
            try
            {
                return Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return null;
            }
        }
        return null;
    }

    // ───────────────────────────── 訊息與導覽 ─────────────────────────────

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !source.Host.Equals(AgentMarkdownRenderer.Host, StringComparison.OrdinalIgnoreCase))
            return;

        JObject command;
        try
        {
            command = JObject.Parse(e.WebMessageAsJson);
        }
        catch (JsonException)
        {
            return;
        }

        // 拖放檔案：網頁以 postMessageWithAdditionalObjects 附上 File 物件，這裡取得本機路徑
        var paths = new List<string>();
        try
        {
            if (e.AdditionalObjects != null)
            {
                foreach (var item in e.AdditionalObjects)
                {
                    if (item is CoreWebView2File file && !string.IsNullOrWhiteSpace(file.Path))
                        paths.Add(file.Path);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
        }

        // 延到事件處理結束後才執行：指令可能開選檔對話框，在 WebView2 事件處理中開強制回應視窗容易卡住
        owner.BeginInvoke(() => hub.HandleCommand(this, command, paths));
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var index = $"{AgentMarkdownRenderer.Origin}/index.html";
        if (e.Uri.StartsWith(index, StringComparison.OrdinalIgnoreCase) || e.Uri.Equals($"{AgentMarkdownRenderer.Origin}/", StringComparison.OrdinalIgnoreCase))
            return;

        // 其他導覽（漏網的連結、沒被網頁接住的拖放）一律取消，交給中樞判斷要不要用預設程式開
        e.Cancel = true;
        var href = e.Uri;
        if (!href.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            owner.BeginInvoke(() => hub.HandleCommand(this, new JObject { ["cmd"] = "openLink", ["href"] = href }));
    }

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind == CoreWebView2PermissionKind.Microphone && IsAppOrigin(e.Uri))
            e.State = CoreWebView2PermissionState.Allow;
    }

    /// <summary>是 app 自己的頁面（<c>https://myriagent.local</c>，不分大小寫）；其他來源（不該出現）不給權限</summary>
    internal static bool IsAppOrigin(string? uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
           && string.Equals(parsed.GetLeftPart(UriPartial.Authority), AgentMarkdownRenderer.Origin, StringComparison.OrdinalIgnoreCase);

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        var href = e.Uri;
        owner.BeginInvoke(() => hub.HandleCommand(this, new JObject { ["cmd"] = "openLink", ["href"] = href }));
    }

    /// <summary>WebView2 的瀏覽器程序結束（所有共用同一環境的視窗都會收到）：視窗要重建 WebView2 或關閉</summary>
    public event Action? BrowserCrashed;

    /// <summary>瀏覽器程序結束後環境就不能再用：清掉快取，下次建立新的</summary>
    public static void ResetEnvironment() => sharedEnvironment = null;

    /// <summary>
    /// 只有瀏覽器程序與主框架的渲染程序結束需要復原；GPU、utility 等程序 WebView2 會自動重建（所有視窗共用，重新載入會讓每個視窗的草稿與輸入框文字一起消失）。
    /// 渲染程序沒有回應可能只是暫時忙碌：持續一段時間才問使用者要不要重新載入
    /// </summary>
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (owner.IsDisposed)
            return;
        Log.Warn($"WebView2 程序失敗：{e.ProcessFailedKind}");
        switch (ShellRules.RecoveryFor(e.ProcessFailedKind))
        {
            case WebViewRecovery.RecreateWebView:
                ResetEnvironment();
                owner.BeginInvoke(() => BrowserCrashed?.Invoke());
                break;
            case WebViewRecovery.Reload:
                unresponsive.Reset();
                Reload();
                break;
            case WebViewRecovery.AskReload:
                // 對話框不在 WebView2 事件處理中開（強制回應視窗在這裡容易卡住）
                if (unresponsive.Report(DateTime.UtcNow))
                    owner.BeginInvoke(AskReload);
                break;
        }
    }

    private void Reload()
    {
        try
        {
            webView.Reload();
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
        }
    }

    /// <summary>渲染程序持續沒有回應：問使用者要重新載入還是繼續等（重新載入會丟掉還沒送出的新對話與輸入框文字）</summary>
    private void AskReload()
    {
        if (reloadPromptOpen || owner.IsDisposed)
            return;
        reloadPromptOpen = true;
        try
        {
            var answer = MessageBox.Show(owner,
                "介面沒有回應。\n\n要重新載入介面嗎？對話紀錄與執行中的代理不受影響，但輸入框裡還沒送出的文字和還沒送出的新對話會遺失。\n\n選「否」繼續等待。",
                ShellRules.AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (owner.IsDisposed)
                return;
            if (answer == DialogResult.Yes)
            {
                unresponsive.Reset();
                Reload();
            }
            else
            {
                unresponsive.Dismissed(DateTime.UtcNow);
            }
        }
        finally
        {
            reloadPromptOpen = false;
        }
    }

    /// <summary>視窗標題跟著網頁標題（目前對話的標題）：多個彈出視窗在工作列與 Alt+Tab 才分得出來</summary>
    private void OnDocumentTitleChanged(object? sender, object e)
    {
        if (owner.IsDisposed || sender is not CoreWebView2 core)
            return;
        try
        {
            owner.Text = ShellRules.WindowTitle(core.DocumentTitle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
        }
    }
}

/// <summary>彈出視窗的 WebView2（只顯示一個對話）</summary>
/// <summary>完整視窗（主視窗或另外開的視窗，多視窗）的 WebView2：版面以 WindowKey 存，分頁可以在視窗之間拖曳搬移</summary>
internal sealed class MainWebView : AgentWebView, IWindowView
{
    public MainWebView(Form owner, WebView2 webView, AgentHub hub, ThumbnailService thumbnails, string windowKey, bool isPrimaryWindow)
        : base(owner, webView, hub, thumbnails, isPopout: false)
    {
        WindowKey = windowKey;
        IsPrimaryWindow = isPrimaryWindow;
    }

    public string WindowKey { get; }

    public bool IsPrimaryWindow { get; }
}

internal sealed class PopoutWebView : AgentWebView, IPopoutView
{
    public PopoutWebView(Form owner, WebView2 webView, AgentHub hub, ThumbnailService thumbnails, string conversationId)
        : base(owner, webView, hub, thumbnails, isPopout: true)
    {
        ConversationId = conversationId;
    }

    public string ConversationId { get; }
}
