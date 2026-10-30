using System.Diagnostics;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Sync;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.UI;

/// <summary>
/// 實機探針（環境變數 MYRIAGENT_AGENT_PROBE=輸出資料夾 才啟用；搭配 --data 暫存資料夾使用，不碰正常資料）：
/// 等介面與帳戶狀態就緒後截圖（啟動、儀表板）、量啟動時間與記憶體，
/// 在真實 WebView2 以 fetch 驗證 /thumb 與 /media 的 Range 回應，
/// 再對 Claude、Codex、Grok 已登入的帳戶各開一個對話送一則極短訊息（最便宜的模型、低推理強度），等回覆後截圖，
/// Grok 另在同一個對話操作一次核准卡；最後寫 report.json 並結束。
/// MYRIAGENT_AGENT_PROBE_SEND=0 時不送訊息（不用額度）；MYRIAGENT_AGENT_PROBE_NO_CLI=1 時不找、不執行任何 CLI（ProbeRules：略過 CLI 偵測與送訊息）。
/// </summary>
internal sealed class ProbeRunner
{
    private readonly AppController app;
    private readonly MainForm form;
    private readonly string outDir;
    private readonly JObject report = new();
    private readonly Stopwatch clock;

    private ProbeRunner(AppController app, MainForm form, string outDir, Stopwatch clock)
    {
        this.app = app;
        this.form = form;
        this.outDir = outDir;
        this.clock = clock;
    }

    public static void AttachIfRequested(AppController app, MainForm form, Stopwatch clock)
    {
        var outDir = Environment.GetEnvironmentVariable("MYRIAGENT_AGENT_PROBE");
        if (string.IsNullOrWhiteSpace(outDir))
            return;
        Directory.CreateDirectory(outDir);
        // 探針絕不自動更新使用者的 CLI（只在 MYRIAGENT_AGENT_PROBE_UPDATE=1 時保留設定）
        if (Environment.GetEnvironmentVariable("MYRIAGENT_AGENT_PROBE_UPDATE") != "1")
            app.Settings.AutoUpdateCli = false;
        var runner = new ProbeRunner(app, form, outDir, clock);
        bool started = false;
        form.ViewLoaded += () =>
        {
            if (started)
                return;
            started = true;
            runner.report["navigationCompletedMs"] = clock.ElapsedMilliseconds;
            _ = runner.RunAsync();
        };
    }

    private readonly JArray consoleErrors = new();

    /// <summary>以 DevTools 協定收集網頁的主控台錯誤與未捕捉例外（真正 WebView2 裡的「主控台 0 錯誤」驗證）</summary>
    private async Task WatchConsoleAsync(CoreWebView2 core)
    {
        await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
        core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, e) =>
            consoleErrors.Add("exception: " + Truncate(e.ParameterObjectAsJson));
        core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, e) =>
        {
            var json = JObject.Parse(e.ParameterObjectAsJson);
            if (json.Value<string>("type") is "error" or "assert")
                consoleErrors.Add("console: " + Truncate(json["args"]?.ToString(Formatting.None)));
        };
        core.GetDevToolsProtocolEventReceiver("Log.entryAdded").DevToolsProtocolEventReceived += (_, e) =>
        {
            var entry = JObject.Parse(e.ParameterObjectAsJson)["entry"] as JObject;
            if (entry?.Value<string>("level") == "error")
                consoleErrors.Add($"log: {entry.Value<string>("source")} {Truncate(entry.Value<string>("text"))} {entry.Value<string>("url")}");
        };
    }

    private static string Truncate(string? text) => text == null ? string.Empty : text.Length <= 400 ? text : text[..400] + "…";

    private JObject DescribeCli(Agents.AgentProvider provider)
    {
        var s = app.Cli.Get(provider);
        return new JObject
        {
            ["installed"] = s.Installed,
            ["version"] = s.Version,
            ["latest"] = s.Latest,
            ["updateAvailable"] = s.UpdateAvailable,
            ["kind"] = s.Installation?.Kind.ToString(),
            ["path"] = s.Installation?.DisplayPath,
            ["error"] = s.ActionError ?? s.Error,
        };
    }

    private async Task RunAsync()
    {
        try
        {
            var core = form.View.Control.CoreWebView2;
            await WatchConsoleAsync(core);
            core.Reload();   // 監看裝好後重新載入，載入期間的錯誤也收得到
            await Task.Delay(300);
            await WaitForAsync(async () => await EvalBoolAsync(core, "document.readyState === 'complete' && document.body && document.body.children.length > 0"), TimeSpan.FromSeconds(20));
            report["uiReadyMs"] = clock.ElapsedMilliseconds;

            // 帳戶狀態檢查完（不再是 checking）
            await WaitForAsync(() => Task.FromResult(app.Accounts.Accounts.All(a => app.Accounts.GetState(a.Id).Status != AccountStatus.Checking)), TimeSpan.FromSeconds(60));
            await Task.Delay(1500);
            report["accounts"] = new JArray(app.Accounts.Accounts.Select(a => new JObject
            {
                ["label"] = a.Label,
                ["provider"] = a.Provider.ToString(),
                ["status"] = app.Accounts.GetState(a.Id).Status.ToString(),
                ["plan"] = a.Plan,
                ["windows"] = new JArray((app.Accounts.GetUsage(a.Id)?.Windows ?? Array.Empty<Usage.UsageWindow>()).Select(w => $"{w.Label} {w.Percent:0.#}%")),
            }));
            await CaptureAsync(core, "01-startup.png");

            form.View.PostToWeb(new JObject { ["type"] = "showView", ["view"] = "dashboard" });
            await Task.Delay(1500);
            await CaptureAsync(core, "02-dashboard.png");
            form.View.PostToWeb(new JObject { ["type"] = "showView", ["view"] = "chat" });
            await Task.Delay(800);

            // 已配對時先等同步連上（最多 30 秒），閒置記憶體才含同步引擎。進 Live 當下先記一次（含啟動重整的暫態，舊量法的取樣點）；
            // 已配對而且連上時再等重整與回補安定，之後不論是否配對都再等 IdleSettleDelay 才取閒置記憶體（WP4.9 D1）
            var syncNotOnline = await WaitForSyncAsync();
            MeasureMemory("memoryAtLiveMb");
            var idleWait = ProbeRules.IdleWait(app.Sync.IsPaired, syncNotOnline);
            if (idleWait.Settle)
                await WaitForSettleAsync();
            await Task.Delay(idleWait.Delay);
            var appPrivateMb = MeasureMemory("memoryIdleMb");
            var allocatedBefore = GC.GetTotalAllocatedBytes(false);
            var lateClock = Stopwatch.StartNew();
            var heartbeats = await MeasureIdleCpuAsync(pairedOnline: app.Sync.IsPaired && !syncNotOnline);
            // 第二次閒置取樣（WP4.9 合併前審查 F1）：第一次落在閒置釋放 DLP 樣式的積極回收之後約 5 秒（已配對那一邊的低點），
            // CPU 窗（與 MYRIAGENT_AGENT_PROBE_IDLE_EXTRA_SECONDS 多等的時間）之後再取一次，記下其間的配置速率與 gen0 預算
            var extra = ProbeRules.IdleExtraDelay(Environment.GetEnvironmentVariable(ProbeRules.IdleExtraVariable));
            if (extra > TimeSpan.Zero)
                await Task.Delay(extra);
            var allocated = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
            var lateElapsed = lateClock.Elapsed;
            MeasureMemory("memoryIdleLateMb");
            report["idleLate"] = DescribeIdleLate(ProbeRules.DescribeIdleLate(extra, lateElapsed, allocated, ProbeRules.Gen0MaxBudgetBytes(GC.GetConfigurationVariables())));
            report["sync"] = DescribeSync(appPrivateMb, syncNotOnline, heartbeats);
            await CheckEndpointsAsync(core);

            var noCli = ProbeRules.NoCliRequested();
            if (noCli)
            {
                report["cli"] = $"略過（{ProbeRules.NoCliVariable}=1：不找、不執行任何 CLI）";
            }
            else
            {
                // 更新偵測（探針已關閉自動更新：只查版本與 npm registry 最新版，不會動到使用者的 CLI）
                await Task.Run(app.Cli.CheckAllAsync);
                report["cli"] = new JObject
                {
                    ["claude"] = DescribeCli(Agents.AgentProvider.Claude),
                    ["codex"] = DescribeCli(Agents.AgentProvider.Codex),
                    ["antigravity"] = DescribeCli(Agents.AgentProvider.Antigravity),
                    ["grok"] = DescribeCli(Agents.AgentProvider.Grok),
                };
            }
            await Task.Delay(800);
            form.View.PostToWeb(new JObject { ["type"] = "showView", ["view"] = "dashboard" });
            await Task.Delay(1200);
            await core.ExecuteScriptAsync("window.scrollTo(0, document.body.scrollHeight); document.querySelectorAll('*').forEach(e => { if (e.scrollHeight > e.clientHeight + 50 && getComputedStyle(e).overflowY !== 'visible') e.scrollTop = e.scrollHeight; })");
            await Task.Delay(600);
            await CaptureAsync(core, "08-dashboard-cli.png");
            form.View.PostToWeb(new JObject { ["type"] = "showView", ["view"] = "chat" });
            await Task.Delay(600);

            if (!noCli && Environment.GetEnvironmentVariable("MYRIAGENT_AGENT_PROBE_SEND") != "0")
            {
                await SendAndWaitAsync(core, AgentProvider.Claude, "haiku", "low", "03-claude-reply.png");
                await SendAndWaitAsync(core, AgentProvider.Codex, "gpt-5.6-luna", "low", "04-codex-reply.png");
                var grok = await SendAndWaitAsync(core, AgentProvider.Grok, "grok-4.6", "low", "05-grok-reply.png");
                if (grok != null)
                    await ApproveOnceAsync(core, grok, "grok", "09-grok-approval.png");
                MeasureMemory("memoryAfterSessionsMb");
                await CheckFileDrawerAsync(core);
            }
            report["ok"] = true;
        }
        catch (Exception ex)
        {
            report["ok"] = false;
            report["error"] = ex.ToString();
        }
        finally
        {
            report["consoleErrors"] = consoleErrors;
            File.WriteAllText(Path.Combine(outDir, "report.json"), report.ToString(Formatting.Indented));
            Log.Info($"實機探針完成：{outDir}");
            if (Environment.GetEnvironmentVariable("MYRIAGENT_AGENT_PROBE_KEEP") != "1")
                app.ExitImmediately();
        }
    }

    /// <summary>開對話送一則極短訊息並等回覆；回傳對話 id（沒有已登入的帳戶時為 null）</summary>
    private async Task<string?> SendAndWaitAsync(CoreWebView2 core, AgentProvider provider, string model, string effort, string screenshot)
    {
        var account = app.Accounts.Accounts.FirstOrDefault(a => a.Provider == provider && app.Accounts.GetState(a.Id).Status == AccountStatus.Ready);
        var key = provider.ToString().ToLowerInvariant();
        if (account == null)
        {
            report[key] = "沒有已登入的帳戶，略過";
            return null;
        }

        var workDir = Path.Combine(outDir, "work-" + key);
        Directory.CreateDirectory(workDir);
        var view = form.View;
        app.Hub.HandleCommand(view, new JObject { ["cmd"] = "newConversation", ["accountId"] = account.Id });
        var id = app.Hub.LatestDraftId(view.Id) ?? throw new InvalidOperationException("沒有建立草稿");
        app.Hub.HandleCommand(view, new JObject { ["cmd"] = "setFolder", ["conversationId"] = id, ["path"] = workDir });
        app.Hub.HandleCommand(view, new JObject { ["cmd"] = "setOption", ["conversationId"] = id, ["key"] = "model", ["value"] = model });
        app.Hub.HandleCommand(view, new JObject { ["cmd"] = "setOption", ["conversationId"] = id, ["key"] = "effort", ["value"] = effort });
        app.Hub.HandleCommand(view, new JObject { ["cmd"] = "send", ["conversationId"] = id, ["text"] = "這是連線測試。請只回覆兩個字：收到" });

        var sw = Stopwatch.StartNew();
        await WaitForAsync(() => Task.FromResult(app.Hub.GetItems(id).Any(i => i.Kind == AgentItemKind.Assistant) && !app.Hub.IsRunning(id)), TimeSpan.FromMinutes(3));
        var items = app.Hub.GetItems(id);
        report[key] = new JObject
        {
            ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            ["reply"] = items.LastOrDefault(i => i.Kind == AgentItemKind.Assistant)?.Text,
            ["errors"] = new JArray(items.Where(i => i.Kind == AgentItemKind.Error).Select(i => i.Text)),
            ["sessionStarted"] = app.Hub.GetConversation(id)?.SessionStarted,
        };
        await Task.Delay(1200);
        await CaptureAsync(core, screenshot);
        return id;
    }

    /// <summary>
    /// 在同一個對話要代理建立一個檔案（預設的「每次詢問」會出核准卡），核准卡出現後截圖、經中樞的 approve 指令允許一次，
    /// 等這一輪結束並確認檔案建好了。
    /// </summary>
    private async Task ApproveOnceAsync(CoreWebView2 core, string id, string key, string screenshot)
    {
        var view = form.View;
        var result = (JObject)report[key]!;
        var workDir = app.Hub.GetConversation(id)?.WorkingDirectory ?? outDir;
        var target = Path.Combine(workDir, "probe-approval.txt");
        var before = app.Hub.GetItems(id).Count;
        app.Hub.HandleCommand(view, new JObject { ["cmd"] = "send", ["conversationId"] = id, ["text"] = "請在目前資料夾建立 probe-approval.txt，內容只有 ok 兩個字，完成後回覆「好了」。" });

        var sw = Stopwatch.StartNew();
        AgentChatItem? card = null;
        await WaitForAsync(() =>
        {
            card = app.Hub.GetItems(id).FirstOrDefault(i => i.Kind == AgentItemKind.Approval && i.Status == AgentItemStatus.Waiting);
            // 送出後要等到出現新的回覆（剛送出時這一輪可能還沒開始，不能只看「沒有在執行」）
            return Task.FromResult(card != null || (!app.Hub.IsRunning(id) && app.Hub.GetItems(id).Skip(before).Any(i => i.Kind is AgentItemKind.Assistant or AgentItemKind.Error)));
        }, TimeSpan.FromMinutes(3));
        if (card == null)
        {
            result["approval"] = "這一輪沒有出現核准卡";
            return;
        }
        await Task.Delay(800);
        await CaptureAsync(core, screenshot);
        app.Hub.HandleCommand(view, new JObject { ["cmd"] = "approve", ["conversationId"] = id, ["itemId"] = card.Id, ["choice"] = "allow" });
        await WaitForAsync(() => Task.FromResult(!app.Hub.IsRunning(id) && app.Hub.GetItems(id).Skip(before).Any(i => i.Kind is AgentItemKind.Assistant or AgentItemKind.Error)), TimeSpan.FromMinutes(3));
        var items = app.Hub.GetItems(id);
        result["approval"] = new JObject
        {
            ["seconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1),
            ["card"] = card.Title,
            ["decision"] = card.Decision,
            ["fileCreated"] = File.Exists(target),
            ["reply"] = items.LastOrDefault(i => i.Kind == AgentItemKind.Assistant)?.Text,
            ["errors"] = new JArray(items.Where(i => i.Kind == AgentItemKind.Error).Select(i => i.Text)),
        };
    }

    /// <summary>在真實 WebView2 裡以 fetch 驗證縮圖與 Range（用 app 圖示產生的測試圖與 ffmpeg 產生的測試影片）</summary>
    private async Task CheckEndpointsAsync(CoreWebView2 core)
    {
        var png = Path.Combine(outDir, "probe-image.png");
        using (var icon = new Icon(AppIcons.Window, 256, 256))
        using (var bitmap = icon.ToBitmap())
            bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        var result = new JObject();
        var thumbUrl = app.Hub.AllowThumbForProbe(png, 160);
        result["thumb"] = await FetchAsync(core, thumbUrl, null);

        var mp4 = Path.Combine(outDir, "probe-video.mp4");
        var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg", $"-hide_banner -loglevel error -y -f lavfi -i testsrc=duration=3:size=320x240:rate=25 -pix_fmt yuv420p \"{mp4}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (ffmpeg != null)
        {
            await ffmpeg.WaitForExitAsync();
            if (File.Exists(mp4))
            {
                var mediaUrl = app.Hub.AllowMediaForProbe(mp4);
                result["mediaFull"] = await FetchAsync(core, mediaUrl, null);
                result["mediaRange"] = await FetchAsync(core, mediaUrl, "bytes=100-199");
            }
        }
        report["endpoints"] = result;
    }

    /// <summary>
    /// 真實 WebView2 裡的檔案抽屜與媒體：把測試圖與測試影片放進目前對話的工作資料夾，打開抽屜（Windows 外殼縮圖），
    /// 點影片播放並拖到第 2 秒（驗證 Range 讓影片可以跳轉），點圖片開燈箱，各截一張圖
    /// </summary>
    private async Task CheckFileDrawerAsync(CoreWebView2 core)
    {
        var workDir = Path.Combine(outDir, "work-codex");
        var png = Path.Combine(outDir, "probe-image.png");
        var mp4 = Path.Combine(outDir, "probe-video.mp4");
        if (!Directory.Exists(workDir) || !File.Exists(png) || !File.Exists(mp4))
        {
            report["drawer"] = "略過（沒有工作資料夾或測試素材）";
            return;
        }
        File.Copy(png, Path.Combine(workDir, "測試圖片.png"), overwrite: true);
        File.Copy(mp4, Path.Combine(workDir, "測試影片.mp4"), overwrite: true);

        const string Visible = "(el => el && el.offsetParent !== null)";
        await core.ExecuteScriptAsync($"(() => {{ const b = [...document.querySelectorAll('button[aria-label=\"檔案瀏覽器\"]')].find({Visible}); if (b) b.click(); }})()");
        await Task.Delay(2500);
        var drawer = await EvalJsonAsync(core, "(() => { const items = [...document.querySelectorAll('.fe')].filter(e => e.offsetParent !== null); " +
            "const imgs = [...document.querySelectorAll('.fe-thumb img')].filter(i => i.offsetParent !== null); " +
            "return JSON.stringify({ entries: items.length, thumbs: imgs.length, thumbsLoaded: imgs.filter(i => i.complete && i.naturalWidth > 0).length, names: items.map(e => e.textContent.trim()).slice(0, 5) }); })()");
        await CaptureAsync(core, "05-file-drawer.png");

        await core.ExecuteScriptAsync($"(() => {{ const v = [...document.querySelectorAll('.fe.k-video')].find({Visible}); if (v) v.click(); }})()");
        await Task.Delay(2000);
        await core.ExecuteScriptAsync("(() => { const v = document.querySelector('.vp-video'); if (v) { v.pause(); v.currentTime = 2; } })()");
        await Task.Delay(1500);
        var video = await EvalJsonAsync(core, "(() => { const v = document.querySelector('.vp-video'); return JSON.stringify(v ? { readyState: v.readyState, duration: v.duration, currentTime: v.currentTime, error: v.error && v.error.code } : null); })()");
        await CaptureAsync(core, "06-video-player.png");
        await core.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");
        await Task.Delay(600);

        await core.ExecuteScriptAsync($"(() => {{ const v = [...document.querySelectorAll('.fe.k-image')].find({Visible}); if (v) v.click(); }})()");
        await Task.Delay(1500);
        var lightbox = await EvalJsonAsync(core, "(() => { const i = [...document.querySelectorAll('img')].filter(x => x.offsetParent !== null && x.naturalWidth > 0 && x.getBoundingClientRect().width > 120 && !x.closest('.fe')); return JSON.stringify({ largeImages: i.length }); })()");
        await CaptureAsync(core, "07-lightbox.png");
        await core.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");

        report["drawer"] = new JObject { ["list"] = drawer, ["video"] = video, ["lightbox"] = lightbox };
    }

    private static async Task<JToken> EvalJsonAsync(CoreWebView2 core, string script)
    {
        var raw = await core.ExecuteScriptAsync(script);
        try
        {
            var text = JsonConvert.DeserializeObject<string>(raw);
            return text == null ? JValue.CreateNull() : JToken.Parse(text);
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>ExecuteScriptAsync 不會等 Promise：結果先寫進 window.__probe，再輪詢讀回</summary>
    private static async Task<JToken> FetchAsync(CoreWebView2 core, string url, string? range)
    {
        var key = Guid.NewGuid().ToString("N");
        var headers = range == null ? "{}" : JsonConvert.SerializeObject(new Dictionary<string, string> { ["Range"] = range });
        var script = $"window.__probe = window.__probe || {{}}; fetch({JsonConvert.SerializeObject(url)}, {{ headers: {headers} }})" +
                     ".then(r => r.arrayBuffer().then(b => ({ status: r.status, length: b.byteLength, type: r.headers.get('content-type'), range: r.headers.get('content-range') })))" +
                     $".catch(e => ({{ error: String(e) }})).then(x => {{ window.__probe['{key}'] = JSON.stringify(x); }}); 0";
        await core.ExecuteScriptAsync(script);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            var raw = await core.ExecuteScriptAsync($"window.__probe && window.__probe['{key}'] || null");
            if (raw != "null")
            {
                var text = JsonConvert.DeserializeObject<string>(raw);
                return text == null ? JValue.CreateNull() : JToken.Parse(text);
            }
            await Task.Delay(100);
        }
        return "逾時";
    }

    /// <summary>同步的延後啟動做完（讀完 device.json）之後，已配對就等到連上（最多 30 秒）；回傳「已配對但逾時沒連上」</summary>
    private async Task<bool> WaitForSyncAsync()
    {
        try
        {
            await app.Sync.WhenStarted.WaitAsync(SyncStartupRules.FallbackDelay + TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            report["syncStartTimeout"] = true;
            return false;
        }

        if (!app.Sync.IsPaired)
            return false;
        try
        {
            await WaitForAsync(() => Task.FromResult(app.Sync.Status?.State == SyncEngineState.Live), TimeSpan.FromSeconds(30));
            return false;
        }
        catch (TimeoutException)
        {
            return true;
        }
    }

    /// <summary>
    /// 等同步安定（重整做完、佇列空、outbox 全部確認、沒有在途批次、回補進度是 null），連續 <see cref="ProbeRules.SettleQuiet"/> 才算；
    /// 每 100 ms 看一次，最多 <see cref="ProbeRules.SettleTimeout"/>。報告記 settleSeconds 與 settleTimedOut（逾時＝S6 的記憶體判定無效）
    /// </summary>
    private async Task WaitForSettleAsync()
    {
        var watch = Stopwatch.StartNew();
        TimeSpan? quietSince = null;
        var settled = false;
        while (watch.Elapsed < ProbeRules.SettleTimeout)
        {
            if (app.Sync.Engine is not ISyncSettleProbe probe)
            {
                report["settleError"] = "引擎沒有安定的快照（未配對、或不是同步引擎）";
                break;
            }

            var snapshot = await probe.SettleSnapshotAsync();
            // 回到 UI 執行緒才讀發布者（同 fake-device 的 BackfillAsync）：沒有掛發布者也算回補做完
            var backfillDone = app.Hub.ConversationSink is not ConversationPublisher publisher || publisher.Progress is null;
            if (SyncSettleRules.IsSettled(snapshot with { BackfillDone = backfillDone }))
            {
                quietSince ??= watch.Elapsed;
                if (SyncSettleRules.QuietEnough(watch.Elapsed - quietSince.Value, ProbeRules.SettleQuiet))
                {
                    settled = true;
                    break;
                }
            }
            else
            {
                quietSince = null;
            }

            await Task.Delay(100);
        }

        report["settleSeconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1);
        report["settleTimedOut"] = !settled;
    }

    /// <summary>report.json 的 sync 段：未配對時 loadedForbidden 應該是空的（S6）</summary>
    private JObject DescribeSync(double appPrivateMb, bool notOnline, long? heartbeatsDuringSample)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? string.Empty).ToList();
        var modules = new List<string>();
        try
        {
            using var self = Process.GetCurrentProcess();
            foreach (ProcessModule module in self.Modules)
            {
                using (module)
                    modules.Add(module.ModuleName);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            report["syncModulesError"] = ex.Message;
        }

        return ProbeSyncReport.Build(assemblies, modules, app.Sync.Status, app.Sync.IsPaired, appPrivateMb, notOnline, heartbeatsDuringSample);
    }

    /// <summary>
    /// 記憶體：app 程序＋它的 WebView2 子程序（工作集與私有位元組各自加總；工作集會重複計算共用頁面）；回傳 app 程序自己的私有記憶體（MB，也記在 appPrivateMb）。
    /// gc 是 app 程序的 GC 細項（同一次 GC.Collect 之後；WP4.9 步 1）
    /// </summary>
    private double MeasureMemory(string key)
    {
        GC.Collect();
        var heapBytes = GC.GetTotalMemory(false);
        var gcInfo = GC.GetGCMemoryInfo();
        var self = Process.GetCurrentProcess();
        self.Refresh();
        var gc = ProbeRules.DescribeGc(heapBytes, gcInfo.TotalCommittedBytes, gcInfo.FragmentedBytes, self.PrivateMemorySize64);
        long workingSet = self.WorkingSet64;
        long privateBytes = self.PrivateMemorySize64;
        var detail = new JArray { new JObject { ["name"] = "Myriagent", ["ws"] = Mb(self.WorkingSet64), ["private"] = Mb(self.PrivateMemorySize64) } };
        int webviews = 0;
        foreach (var p in Process.GetProcessesByName("msedgewebview2"))
        {
            using (p)
            {
                try
                {
                    // 只算這個 app 的 WebView2（父程序鏈上有自己）
                    if (ParentChainContains(p.Id, self.Id))
                    {
                        workingSet += p.WorkingSet64;
                        privateBytes += p.PrivateMemorySize64;
                        webviews++;
                        detail.Add(new JObject { ["name"] = $"msedgewebview2#{p.Id}", ["ws"] = Mb(p.WorkingSet64), ["private"] = Mb(p.PrivateMemorySize64) });
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        report[key] = new JObject
        {
            ["workingSetMb"] = Mb(workingSet),
            ["privateMb"] = Mb(privateBytes),
            ["webViewProcesses"] = webviews,
            ["detail"] = detail,
            ["appPrivateMb"] = Mb(self.PrivateMemorySize64),
            ["gc"] = new JObject
            {
                ["heapMb"] = gc.HeapMb,
                ["committedMb"] = gc.CommittedMb,
                ["fragmentedMb"] = gc.FragmentedMb,
                ["nativeEstimateMb"] = gc.NativeEstimateMb,
            },
        };
        return Mb(self.PrivateMemorySize64);
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);

    private static JObject DescribeIdleLate(ProbeIdleLate late) => new()
    {
        ["extraSeconds"] = late.ExtraSeconds,
        ["elapsedSeconds"] = late.ElapsedSeconds,
        ["allocatedMb"] = late.AllocatedMb,
        ["allocMbPerHour"] = late.AllocMbPerHour,
        ["gen0MaxBudgetMb"] = late.Gen0MaxBudgetMb,
        ["gen0BudgetFillMinutes"] = late.Gen0BudgetFillMinutes,
    };

    private static bool ParentChainContains(int pid, int ancestor)
    {
        for (int depth = 0; depth < 4 && pid > 0; depth++)
        {
            pid = ParentProcess(pid);
            if (pid == ancestor)
                return true;
        }
        return false;
    }

    private static int ParentProcess(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero)
            return 0;
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            return NtQueryInformationProcess(handle, 0, ref info, System.Runtime.InteropServices.Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId
                : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// 閒置 CPU（10 秒一窗，以整台電腦的處理器數正規化）：report 的 idleCpuWindows 是每一窗、idleCpuPercent 取最大值。
    /// 已配對而且連上時連取 <see cref="ProbeRules.PairedIdleCpuWindows"/> 窗（30 秒涵蓋至少兩次 15 秒的心跳與 SignalR keepalive；
    /// 一進 Live 就只取一窗的話，第一次心跳在 Live＋15 秒，週期性的同步工作根本落不進取樣窗），回傳取樣期間送出的心跳數；其他情況一窗、回 null
    /// </summary>
    private async Task<long?> MeasureIdleCpuAsync(bool pairedOnline)
    {
        var windows = pairedOnline ? ProbeRules.PairedIdleCpuWindows : 1;
        var beatsBefore = app.Sync.HeartbeatsSent;
        var values = new JArray();
        double max = 0;
        using var self = Process.GetCurrentProcess();
        for (var i = 0; i < windows; i++)
        {
            self.Refresh();
            var before = self.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(10));
            self.Refresh();
            var used = self.TotalProcessorTime - before;
            var percent = Math.Round(used.TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100, 2);
            values.Add(percent);
            max = Math.Max(max, percent);
        }

        report["idleCpuPercent"] = max;
        report["idleCpuWindows"] = values;
        return pairedOnline ? (app.Sync.HeartbeatsSent ?? 0) - (beatsBefore ?? 0) : null;
    }

    private async Task CaptureAsync(CoreWebView2 core, string name)
    {
        await using var file = File.Create(Path.Combine(outDir, name));
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
    }

    private static async Task<bool> EvalBoolAsync(CoreWebView2 core, string expression)
        => (await core.ExecuteScriptAsync(expression)).Trim() == "true";

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition())
                return;
            await Task.Delay(250);
        }
        throw new TimeoutException($"等待逾時（{timeout.TotalSeconds:0} 秒）");
    }
}
