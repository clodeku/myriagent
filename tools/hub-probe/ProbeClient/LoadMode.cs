using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyriagentHub.ProbeClient;

/// <summary>load 的參數（README 的 load 一節）</summary>
internal sealed record LoadOptions(
    string? Base,
    string? HubExe,
    string FakeDevice,
    string Work,
    string OwnerUser,
    string PasswordVariable,
    string? OwnerOrigin,
    int Devices,
    int Browsers,
    int Streams,
    int Persistent,
    int Minutes,
    int SampleSeconds,
    int Stalled,
    int IdleMinutes,
    int SeedConversations,
    bool BackfillOnly,
    int? DelayMs,
    int SlackSeconds,
    int? ServerProcessors,
    string? Out,
    bool RelayBurst = false,
    int? RelayCap = null)
{
    /// <summary>
    /// 判定用的全域轉送上限（WP4.9 步 19）：<c>--relay-cap</c> 有給就用它（<c>--hub-exe</c> 時也以 <c>--Hub:Media:MaxConcurrentRelays</c> 傳給自己啟動的伺服器），
    /// 沒給是伺服器的預設 <see cref="LoadRules.DefaultRelayCap"/>
    /// </summary>
    public int EffectiveRelayCap => RelayCap ?? LoadRules.DefaultRelayCap;

    /// <summary>
    /// <c>--relay-burst</c> 最多幾台：每台 21 個 GET 加桌面回覆的 20 個 PUT（HTTP/1.1 各一條連線），10 台約 410 條，
    /// MyriagentHub 的 Kestrel MaxConcurrentConnections 是 512（HubApplication）
    /// </summary>
    public const int MaxRelayDevices = 10;

    /// <summary>串流每一段的間隔（桌面的節奏：每個對話每秒最多 4 則，設計:3187）</summary>
    public const int StreamDelayMs = 250;

    /// <summary>解讀並檢查參數；不合格丟 <see cref="ArgumentException"/>（結束碼 2）</summary>
    public static LoadOptions From(ProbeContext ctx)
    {
        var baseUrl = ctx.Has("base") ? ctx.Opt("base", "") : null;
        var hubExe = ctx.Has("hub-exe") ? ctx.Opt("hub-exe", "") : null;
        if ((baseUrl is null) == (hubExe is null))
            throw new ArgumentException("load 要有 --base（已經在跑的本機 MyriagentHub）或 --hub-exe（自己啟動一個），兩者擇一");
        var fake = ctx.Has("fake-device") ? ctx.Opt("fake-device", "") : throw new ArgumentException("load 要有 --fake-device <FakeDevice.exe>");
        if (!File.Exists(fake))
            throw new ArgumentException("找不到 --fake-device：" + fake);
        if (hubExe is not null && !File.Exists(hubExe))
            throw new ArgumentException("找不到 --hub-exe：" + hubExe);
        var work = ctx.Has("work") ? ctx.Opt("work", "") : throw new ArgumentException("load 要有 --work <工作資料夾>（資料根、劇本與記錄都放這裡）");
        if (Directory.Exists(work) && Directory.EnumerateFileSystemEntries(work).Any())
            throw new ArgumentException("--work 要是不存在或空的資料夾：" + work);
        var passwordVariable = ctx.Has("owner-password-env") ? ctx.Opt("owner-password-env", "") : throw new ArgumentException("load 要有 --owner-password-env <環境變數名稱>（擁有者密碼只從環境變數讀）");
        var backfillOnly = ctx.Has("backfill-only");
        var devices = Range(ctx, "devices", 10, 1, 32);
        var options = new LoadOptions(
            baseUrl,
            hubExe,
            fake,
            work,
            ctx.Opt("owner-user", "admin"),
            passwordVariable,
            ctx.Has("owner-origin") ? ctx.Opt("owner-origin", "") : null,
            devices,
            backfillOnly ? 0 : Range(ctx, "browsers", 5, 0, 20),
            backfillOnly ? 0 : Range(ctx, "streams", 5, 0, 32),
            backfillOnly ? 0 : Range(ctx, "persistent", 1, 0, 32),
            Range(ctx, "minutes", 10, 1, 50),
            Range(ctx, "sample-seconds", 5, 1, 60),
            backfillOnly ? 0 : Range(ctx, "stalled-browser", 1, 0, 3),
            Range(ctx, "idle-minutes", 2, 0, 30),
            Range(ctx, "seed-conversations", 0, 0, 5_000),
            backfillOnly,
            ctx.Has("delay-ms") ? Range(ctx, "delay-ms", 0, 0, 10_000) : null,
            Range(ctx, "slack-seconds", 20, 5, 120),
            ctx.Has("server-processors") ? Range(ctx, "server-processors", 1, 1, 1_024) : null,
            ctx.Has("out") ? ctx.Opt("out", "") : null,
            ctx.Has("relay-burst"),
            ctx.Has("relay-cap") ? Range(ctx, "relay-cap", LoadRules.DefaultRelayCap, 1, 1_024) : null);
        if (options.RelayCap is not null && !options.RelayBurst)
            throw new ArgumentException("--relay-cap 要配 --relay-burst（只有轉送階段用得到）");
        if (options.Streams + options.Persistent > options.Devices)
            throw new ArgumentException($"--streams {options.Streams}＋--persistent {options.Persistent} 超過 --devices {options.Devices}（每台同時只跑一個對話）");
        if (backfillOnly && options.SeedConversations == 0)
            throw new ArgumentException("--backfill-only 要配 --seed-conversations N");
        if (options.RelayBurst && backfillOnly)
            throw new ArgumentException("--relay-burst 不能配 --backfill-only（轉送階段要瀏覽器與串流）");
        if (options.RelayBurst && (options.Browsers < 1 || options.Streams < 1))
            throw new ArgumentException("--relay-burst 要 --browsers ≥1（拿 /m/ 網址、量 S7）與 --streams ≥1（期間其他對話的延遲）");
        if (options.RelayBurst && options.Devices > MaxRelayDevices)
            throw new ArgumentException($"--relay-burst 最多 {MaxRelayDevices} 台（每台 21 個 GET 加 20 個 PUT，伺服器同時最多 512 條連線）：--devices {options.Devices}");
        return options;
    }

    private static int Range(ProbeContext ctx, string key, int fallback, int min, int max)
    {
        int value;
        try
        {
            value = ctx.IntOpt(key, fallback);
        }
        catch (FormatException)
        {
            throw new ArgumentException($"--{key} 要是整數");
        }

        return value >= min && value <= max ? value : throw new ArgumentException($"--{key} 要在 {min}～{max} 之間：{value}");
    }
}

/// <summary>每 <c>--sample-seconds</c> 秒讀一次 /readyz（本機迴路免認證）；私有記憶體超過 400 MB 就通知中止（D19）</summary>
internal sealed class ReadyzSampler(ProbeContext ctx, TimeSpan interval, Action<object> emit, Action<double> abort)
{
    private readonly object gate = new();
    private readonly List<LoadSample> samples = [];
    private int errors;

    public int Errors => Volatile.Read(ref errors);

    /// <summary>超過中止門檻時的私有記憶體</summary>
    public double? AbortedAtMb { get; private set; }

    public IReadOnlyList<LoadSample> Samples
    {
        get
        {
            lock (gate)
                return samples.ToList();
        }
    }

    /// <summary>最後一個 /readyz 的 conversations 區塊（回補時看伺服器物化了多少）</summary>
    public JsonNode? LastConversations { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SampleOnceAsync(ct);
            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task SampleOnceAsync(CancellationToken ct)
    {
        try
        {
            using var response = await ctx.Http.GetAsync(ctx.BaseUrl + "/readyz", ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var json = JsonNode.Parse(text);
            var process = json?["process"];
            if (process?["privateMb"]?.GetValue<double>() is not { } privateMb)
            {
                Interlocked.Increment(ref errors);
                return;
            }

            var blobs = json?["blobs"];
            var sample = new LoadSample(at, privateMb, Number(process["cpuPercent"]), Number(process["cpuWindowSeconds"]),
                Number(blobs?["relaysActive"]) is { } relays ? (int)relays : null, Number(blobs?["relayTimeouts"]) is { } timeouts ? (long)timeouts : null,
                Number(process["gcHeapMb"]), Number(process["gcCommittedMb"]));
            lock (gate)
                samples.Add(sample);
            LastConversations = json?["conversations"]?.DeepClone();
            emit(new
            {
                @event = "sample",
                atMs = at,
                privateMb,
                cpuPercent = sample.CpuPercent,
                cpuWindowSeconds = sample.CpuWindowSeconds,
                threads = Number(process["threads"]),
                connections = Number(json?["connections"]?["active"]),
                relaysActive = sample.RelaysActive,
                gcHeapMb = sample.GcHeapMb,
                gcCommittedMb = sample.GcCommittedMb,
                // 伺服器的閒置修剪次數（設計 14.20；沒有這個欄位的舊 MyriagentHub 是 null，只記錄）
                idleTrims = Number(process["idleTrims"]),
            });
            if (LoadRules.ShouldAbort(privateMb) && AbortedAtMb is null)
            {
                AbortedAtMb = privateMb;
                abort(privateMb);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or FormatException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref errors);
        }
    }

    private static double? Number(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var d) ? d : null;
}

/// <summary>
/// <c>load</c>（WP4.9 計畫步 9；S9 的負載工具，也量 Hub 主機上的 S7 與回補負載）：對本機的 MyriagentHub（<c>--base</c>，或 <c>--hub-exe</c> 自己啟動一個）
/// <list type="number">
/// <item>正式站守門（D18），量幾次 /healthz 的來回時間；之後每 <c>--sample-seconds</c> 秒讀 /readyz（私有記憶體 &gt;400 MB 就中止，結束碼 4）。</item>
/// <item>擁有者瀏覽器 <c>--browsers</c> 條（.NET SignalR 用戶端）與卡住的瀏覽器 <c>--stalled-browser</c> 條（原生 WebSocket，D7）各自登入、連上。</item>
/// <item>假裝置 <c>--devices</c> 台分批配對（一批最多 4 台，D6）；全部配對好那一刻起算負載 <c>--minutes</c> 分鐘：<c>--streams</c> 台串流、
/// <c>--persistent</c> 台持久（每秒一個完成的項目），其他只連著。瀏覽器從項目文字的時間標記算即時與持久的延遲（D8）。</item>
/// <item><c>--relay-burst</c>（步 12）：負載期間之後接 2 分鐘的媒體轉送上限階段（<see cref="RelayBurstPhase"/>），串流照跑；摘要多一個 <c>relay</c> 區塊。</item>
/// <item>負載結束：卡住的瀏覽器恢復讀取，看伺服器有沒有把它關掉；等對話都跑完再閒置 <c>--idle-minutes</c> 分鐘（裝置照樣連著）量閒置記憶體。</item>
/// <item>摘要：CPU 依時間窗加權（D16）、私有記憶體最大與 p95、閒置私有記憶體、S7 的 p50／p95／p99、慢用戶端只出現在卡住的那一條、每台裝置的結果；
/// 印成一行 <c>{"event":"summary"}</c> 並寫進 <c>--out</c>（預設工作資料夾的 load-summary.json）。</item>
/// </list>
/// 回補：<c>--seed-conversations N</c> 讓每台啟動前先有 N 個對話（配對後回補上去）；<c>--backfill-only</c> 只量回補（沒有瀏覽器與串流）。
/// <c>--delay-ms</c> 只記錄（延遲由外部的 Node 代理設定；工具量來回時間）
/// </summary>
internal static class LoadMode
{
    private static readonly TimeSpan PerDevicePairTimeout = TimeSpan.FromSeconds(90);

    public static async Task<int> RunAsync(ProbeContext ctx)
    {
        LoadOptions o;
        try
        {
            o = LoadOptions.From(ctx);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Program.ExitUsage;
        }

        var password = Environment.GetEnvironmentVariable(o.PasswordVariable);
        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine($"環境變數 {o.PasswordVariable} 沒有設定擁有者密碼");
            return Program.ExitUsage;
        }

        Directory.CreateDirectory(o.Work);
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        var run = new LoadRun(ctx, o, password, cts);
        try
        {
            return await run.ExecuteAsync();
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            await run.CleanupAsync();
        }
    }

    /// <summary>一次 load 的狀態（清理在 finally 裡做，不論成敗）</summary>
    private sealed class LoadRun(ProbeContext ctx, LoadOptions o, string password, CancellationTokenSource cts)
    {
        private readonly List<BrowserLoadClient> browsers = [];
        private readonly List<(StalledBrowser Browser, Task<int> Stall)> stalled = [];
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private LocalHub? hub;
        private HubStopResult? hubStop;
        private FakeDeviceFleet? fleet;
        private ReadyzSampler? sampler;
        private Task? sampling;
        private CancellationTokenSource? samplingStop;
        private bool browsersClosed;

        /// <summary>轉送階段因私有記憶體超過中止門檻而中止時的 relay 區塊（摘要照樣寫）</summary>
        private object? relayAborted;

        private CancellationToken Ct => cts.Token;

        public async Task<int> ExecuteAsync()
        {
            string baseUrl;
            if (o.HubExe is { } exe)
            {
                IReadOnlyList<string> extra = o.RelayCap is { } cap ? ["--Hub:Media:MaxConcurrentRelays=" + cap.ToString(CultureInfo.InvariantCulture)] : [];
                hub = await LocalHub.StartAsync(exe, Path.Combine(o.Work, "hub-data"), Path.Combine(o.Work, "hub.log"), TimeSpan.FromSeconds(60), Ct, extra);
                baseUrl = hub.BaseUrl;
                ctx.Emit(new { @event = "hub", @base = baseUrl, pid = hub.Pid, exe = hub.ExePath, dataDir = hub.DataDir });
            }
            else
            {
                baseUrl = o.Base!.TrimEnd('/');
            }

            var origin = o.OwnerOrigin ?? baseUrl;
            try
            {
                var facts = await ProbeGuard.VerifyAsync(baseUrl, ProbeGuard.DefaultTimeout, ct: Ct);
                ctx.Emit(new { @event = "guard", @base = baseUrl, environment = facts.Environment });
            }
            catch (ProbeGuardException ex)
            {
                ctx.Emit(new { @event = "guard", refused = true, reason = ex.Message });
                return Program.ExitGuard;
            }

            var probe = ctx.WithBase(baseUrl);
            var rtt = await RoundTripAsync(probe);
            ctx.Emit(new { @event = "rtt", ms = rtt, delayMs = o.DelayMs });

            sampler = new ReadyzSampler(probe, TimeSpan.FromSeconds(o.SampleSeconds), ctx.Emit, mb =>
            {
                ctx.Emit(new { @event = "abort", reason = "memory", privateMb = mb, limitMb = LoadRules.AbortPrivateMb });
                cts.Cancel();
            });
            samplingStop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            sampling = Task.Run(() => sampler.RunAsync(samplingStop.Token));

            try
            {
                return await LoadAsync(baseUrl, origin, rtt);
            }
            catch (OperationCanceledException) when (sampler.AbortedAtMb is not null)
            {
                // 轉送階段中止的（私有記憶體超過 400 MB）：照樣寫摘要，relay 區塊帶 fail 的判定（其他階段中止照舊只印 abort）
                if (relayAborted is not null)
                {
                    var result = new
                    {
                        @event = "summary",
                        mode = "load",
                        @base = baseUrl,
                        hubExe = o.HubExe,
                        aborted = new { reason = "memory", privateMb = sampler.AbortedAtMb, limitMb = LoadRules.AbortPrivateMb },
                        relay = relayAborted,
                        seconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
                    };
                    ctx.Emit(result);
                    await WriteSummaryAsync(result);
                }

                return Program.ExitMemoryAbort;
            }
            catch (FleetException ex)
            {
                ctx.Emit(new { @event = "fleet", error = ex.Message });
                return 1;
            }
        }

        private async Task<int> LoadAsync(string baseUrl, string origin, double? rtt)
        {
            // 瀏覽器先連上（裝置的對話一出現在清單就開）
            for (var i = 0; i < o.Browsers; i++)
            {
                var cookie = await OwnerLogin.CookieAsync(baseUrl, origin, o.OwnerUser, password, Ct);
                var browser = await BrowserLoadClient.ConnectAsync(i, baseUrl, origin, cookie, Ct);
                browsers.Add(browser);
                await browser.StartAsync(TimeSpan.FromSeconds(30), Ct);
            }

            var expected = o.Streams + o.Persistent;
            for (var i = 0; i < o.Stalled; i++)
            {
                var cookie = await OwnerLogin.CookieAsync(baseUrl, origin, o.OwnerUser, password, Ct);
                var browser = await StalledBrowser.ConnectAsync(baseUrl, origin, cookie, Ct);
                stalled.Add((browser, browser.StallWhenReadyAsync(expected, DateTimeOffset.UtcNow.AddMinutes(5), Ct)));
            }

            ctx.Emit(new { @event = "browsers", connected = browsers.Count, stalled = stalled.Count });

            var plan = FleetRules.Plan(o.Devices, o.Streams, o.Persistent);
            var timing = new FleetTiming(o.Minutes, o.IdleMinutes, LoadOptions.StreamDelayMs, o.SlackSeconds, FleetRules.Batches(o.Devices).Count, o.SeedConversations,
                o.RelayBurst ? RelayBurstPhase.PhaseSeconds : 0);
            fleet = new FakeDeviceFleet(o.FakeDevice, o.Work, baseUrl, origin, o.OwnerUser, o.PasswordVariable, password, plan, timing, ctx.Emit);
            var pairStarted = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var loadStart = await fleet.PairAllAsync(PerDevicePairTimeout, Ct);
            ctx.Emit(new { @event = "loadStart", atMs = loadStart, pairSeconds = Math.Round((loadStart - pairStarted) / 1000.0, 2) });

            await Task.Delay(TimeSpan.FromMinutes(o.Minutes), Ct);
            var loadEnd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ctx.Emit(new { @event = "loadEnd", atMs = loadEnd });

            // 媒體轉送上限階段（--relay-burst，WP4.9 步 12）：負載期間之後接 2 分鐘，串流照跑
            var relay = o.RelayBurst ? await RelayPhaseAsync(baseUrl, origin) : null;

            // 卡住的瀏覽器：恢復讀取，看伺服器有沒有關掉它
            var stalledResults = new List<object>();
            var stalledClosed = 0;
            foreach (var (browser, stall) in stalled)
            {
                if (!stall.IsCompleted)
                {
                    stalledResults.Add(new { stalled = false, error = "負載期間一直沒有停下來（沒看到 load 的對話）" });
                    continue;
                }

                if (stall.IsFaulted)
                {
                    stalledResults.Add(new { stalled = false, error = stall.Exception!.GetBaseException().Message });
                    continue;
                }

                var (closed, detail, bytes) = await browser.CheckClosedAsync(TimeSpan.FromSeconds(20));
                if (closed)
                    stalledClosed++;
                stalledResults.Add(new
                {
                    stalled = true,
                    conversations = stall.Result,
                    stalledAtMs = browser.StalledAtMs,
                    pingFailedAtMs = browser.PingFailedAtMs,
                    closedByServer = closed,
                    detail,
                    bytesAfterResume = bytes,
                });
            }

            // 閒置：對話都跑完才開始算（裝置照樣連著）
            var chatsDone = await fleet.WaitChatsAsync(TimeSpan.FromSeconds(o.SlackSeconds * timing.Batches + 120), Ct);
            var idleStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long? idleEnd = null;
            if (o.IdleMinutes > 0)
            {
                ctx.Emit(new { @event = "idleStart", atMs = idleStart, chatsDone });
                await Task.Delay(TimeSpan.FromMinutes(o.IdleMinutes), Ct);
                idleEnd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                ctx.Emit(new { @event = "idleEnd", atMs = idleEnd });
            }

            // 瀏覽器的延遲（只取負載期間收到的）與連線狀態，之後關掉
            var live = new List<double>();
            var persistent = new List<double>();
            var perBrowser = new List<object>();
            foreach (var browser in browsers)
            {
                var (l, p) = browser.Samples(loadStart, loadEnd);
                live.AddRange(l.Select(s => s.LatencyMs));
                persistent.AddRange(p.Select(s => s.LatencyMs));
                perBrowser.Add(new { browser = browser.Describe(), live = LoadRules.Latency(l.Select(s => s.LatencyMs).ToList()), persistent = LoadRules.Latency(p.Select(s => s.LatencyMs).ToList()) });
            }

            var normalClosed = browsers.Count(b => b.ClosedAtMs is not null);
            await CloseBrowsersAsync();

            // 裝置跑完劇本的閒置尾段、等 outbox 清空、解除配對後自己結束；等不到才以 PID 結束
            var exited = await fleet.WaitExitAsync(TimeSpan.FromSeconds(FleetRules.TailSeconds(timing) + 180), Ct);
            if (!exited)
                fleet.KillAll();
            var devices = fleet.Results();
            var exitCodes = fleet.Members.Select(m => m.Exited.Task.IsCompletedSuccessfully ? m.Exited.Task.Result : (int?)null).ToList();

            await samplingStop!.CancelAsync();
            await sampling!;
            if (hub is not null)
                hubStop = await hub.StopAsync();

            var processors = hub is not null ? Environment.ProcessorCount : o.ServerProcessors ?? Environment.ProcessorCount;
            var samples = sampler!.Samples;
            var summary = LoadRules.Summarize(samples, loadStart, loadEnd, o.IdleMinutes > 0 ? idleStart : null, idleEnd, processors);
            var reasons = hub?.Reasons;
            var serverAborts = reasons is null ? (int?)null
                : reasons.GetValueOrDefault(HubLogRules.SlowClient) + reasons.GetValueOrDefault(HubLogRules.SendTimeout) + reasons.GetValueOrDefault(HubLogRules.SendFailed);
            // 卡住的都被伺服器關掉、正常的一條都沒被關；伺服器記錄的中斷（slow_client、send_timeout、send_failed）不多於卡住的條數——
            // 可以是 0：SignalR 傳輸層的送出逾時（HttpConnectionDispatcherOptions.TransportSendTimeout，BrowserRegistration 沒設、預設 10 秒）
            // 比 MyriagentHub 自己的 15 秒與第 3 次溢出先到，它關連線只在 Trace 記一行（步 9 實測：HttpConnectionContext[9]
            // 「…elapsed attempting to send a message to the transport. Closing connection」）
            var onlyStalled = stalledClosed == stalled.Count && normalClosed == 0 && (serverAborts is null || serverAborts <= stalled.Count);
            // 判定：伺服器取樣（Verdict）併上負載真的施加了沒、慢用戶端的條件（WP4.9 合併前審查：只看取樣的話劇本全部失敗、伺服器閒著也會 pass）
            LoadRules.LoadApplied(chatsDone, exited, exitCodes, o.Devices, browsers.Count, o.Streams, o.Persistent, live.Count, persistent.Count, onlyStalled, stalled.Count,
                out var notApplied, out var loadFailures);
            var verdict = LoadRules.WithLoadApplied(LoadRules.Verdict(summary, idleRequired: o.IdleMinutes > 0), notApplied, loadFailures);

            var result = new
            {
                @event = "summary",
                mode = "load",
                @base = baseUrl,
                hubExe = o.HubExe,
                delayMs = o.DelayMs,
                rttMs = rtt,
                processorCount = processors,
                options = new
                {
                    devices = o.Devices,
                    browsers = o.Browsers,
                    streams = o.Streams,
                    persistent = o.Persistent,
                    minutes = o.Minutes,
                    idleMinutes = o.IdleMinutes,
                    sampleSeconds = o.SampleSeconds,
                    stalledBrowsers = o.Stalled,
                    seedConversations = o.SeedConversations,
                    backfillOnly = o.BackfillOnly,
                    streamDelayMs = LoadOptions.StreamDelayMs,
                    slackSeconds = o.SlackSeconds,
                    batches = FleetRules.Batches(o.Devices),
                    relayBurst = o.RelayBurst,
                },
                window = new { pairStartedAtMs = pairStarted, loadStartMs = loadStart, loadEndMs = loadEnd, idleStartMs = o.IdleMinutes > 0 ? idleStart : (long?)null, idleEndMs = idleEnd, chatsDone },
                load = summary,
                verdict = new
                {
                    verdict.Result,
                    verdict.Reasons,
                    notApplied,
                    note = "門檻：私有記憶體 ≤300 MB、平均 CPU ≤5%、閒置 <150 MB；≤5% 只拿 Hub 主機的數字判（D16），本機的判定只當參考；"
                        + "負載沒施加（對話沒跑完、裝置的結束碼不是 0、沒有延遲樣本）判 insufficient，慢用戶端的條件不成立判 fail",
                },
                s7 = new { live = LoadRules.Latency(live), persistent = LoadRules.Latency(persistent), perBrowser },
                relay,
                slowClient = new { onlyStalled, normalBrowsersClosed = normalClosed, stalled = stalledResults, serverLog = reasons },
                conversations = sampler.LastConversations,
                samplerErrors = sampler.Errors,
                devices,
                devicesExited = exited,
                hubStop,
                seconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
            };
            ctx.Emit(result);
            await WriteSummaryAsync(result);
            return 0;
        }

        /// <summary>摘要寫進 <c>--out</c>（預設工作資料夾的 load-summary.json；UTF-8 無 BOM、縮排、中文不跳脫）</summary>
        private Task WriteSummaryAsync(object result)
        {
            var outFile = o.Out ?? Path.Combine(o.Work, "load-summary.json");
            return File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), new System.Text.UTF8Encoding(false));
        }

        /// <summary>
        /// 媒體轉送上限階段（<see cref="RelayBurstPhase"/>）：瀏覽器 0 從各台的媒體短對話拿 <c>/m/</c> 網址（最多等 60 秒），自己登入一次，
        /// 跑完之後補足 <see cref="RelayBurstPhase.PhaseSeconds"/> 秒；摘要的 <c>relay</c> 區塊含每台的結果、期間的 S7、私有記憶體與判定（<see cref="LoadRules.RelayVerdict"/>）
        /// </summary>
        private async Task<object> RelayPhaseAsync(string baseUrl, string origin)
        {
            var members = fleet!.Members;
            var wait = Stopwatch.StartNew();
            IReadOnlyDictionary<string, string> urls;
            while (true)
            {
                urls = browsers[0].RelayMediaUrls();
                if (members.All(m => urls.ContainsKey(m.Device.Name)) || wait.Elapsed > TimeSpan.FromSeconds(60))
                    break;
                await Task.Delay(500, Ct);
            }

            var cookie = await OwnerLogin.CookieAsync(baseUrl, origin, o.OwnerUser, password, Ct);
            var targets = members.Select(m => new RelayTarget(m.Device.Name, urls.GetValueOrDefault(m.Device.Name),
                m.RelayMedia ?? throw new InvalidOperationException($"{m.Device.Name} 沒有轉送階段的媒體檔"))).ToList();
            var errorsBefore = sampler!.Errors;
            var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ctx.Emit(new { @event = "relayStart", atMs = start, devices = targets.Count, urls = targets.Count(t => t.Url is not null), urlWaitSeconds = Math.Round(wait.Elapsed.TotalSeconds, 1) });
            IReadOnlyList<RelayDeviceResult> results;
            long burstEnd;
            try
            {
                results = await RelayBurstPhase.RunAsync(baseUrl, cookie, targets, ctx.Emit, Ct);
                burstEnd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var left = start + RelayBurstPhase.PhaseSeconds * 1000L - burstEnd;
                if (left > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(left), Ct);
            }
            catch (OperationCanceledException) when (sampler.AbortedAtMb is { } abortedMb)
            {
                // 私有記憶體超過 400 MB、load 中止（D19）：記下到中止為止看到的，判定 fail
                var abortedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var seen = sampler.Samples.Where(s => s.AtMs >= start).ToList();
                var abortVerdict = LoadRules.RelayAbortedVerdict(abortedMb);
                ctx.Emit(new { @event = "relayVerdict", abortVerdict.Result, abortVerdict.Reasons });
                relayAborted = new
                {
                    startMs = start,
                    abortedAtMs = abortedAt,
                    fileBytes = FleetRules.RelayMediaBytes,
                    perDevice = LoadRules.RelayConcurrentPerDevice,
                    requestsPerDevice = LoadRules.RelayRequestsPerDevice,
                    maxPrivateMb = abortedMb,
                    fuseWarnMb = LoadRules.FuseWarnMb,
                    samples = seen.Select(s => new { s.AtMs, s.PrivateMb, s.GcHeapMb, s.RelaysActive }).ToList(),
                    baseline = sampler.Samples.LastOrDefault(s => s.AtMs < start) is { } before ? new { before.AtMs, before.PrivateMb, before.GcHeapMb, before.RelaysActive } : null,
                    verdict = new { abortVerdict.Result, abortVerdict.Reasons },
                };
                throw;
            }

            var end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ctx.Emit(new { @event = "relayEnd", atMs = end, burstSeconds = Math.Round((burstEnd - start) / 1000.0, 1) });

            var live = LoadRules.Latency(browsers.SelectMany(b => b.Samples(start, end).Live.Select(s => s.LatencyMs)).ToList());
            var window = sampler!.Samples.Where(s => s.AtMs >= start && s.AtMs <= end).ToList();
            double? maxPrivateMb = window.Count > 0 ? window.Max(s => s.PrivateMb) : null;
            var relays = window.Where(s => s.RelaysActive is not null).Select(s => s.RelaysActive!.Value).ToList();
            var timeouts = sampler.Samples.Where(s => s.RelayTimeouts is not null && s.AtMs <= end).ToList();
            var timeoutsBefore = timeouts.LastOrDefault(s => s.AtMs < start)?.RelayTimeouts;
            var timeoutsAfter = timeouts.LastOrDefault()?.RelayTimeouts;
            // 伺服器在期間內倒了（自己啟動的看結束碼；--base 看 /readyz 是不是一次都讀不到）：判 fail，不是「樣本不足」
            var readyzErrors = sampler.Errors - errorsBefore;
            var hubExit = hub?.ExitCode;
            var serverLost = hubExit is { } code ? $"MyriagentHub 在轉送期間結束（結束碼 {code}；記錄見 hub.log）"
                : window.Count == 0 && readyzErrors > 0 ? $"轉送期間 /readyz 一次都讀不到（失敗 {readyzErrors} 次）"
                : null;
            var relayCap = o.EffectiveRelayCap;
            var verdict = LoadRules.RelayVerdict(results, members.Count, live, maxPrivateMb, serverLost, relayCap);
            ctx.Emit(new { @event = "relayVerdict", verdict.Result, verdict.Reasons });
            return new
            {
                startMs = start,
                burstEndMs = burstEnd,
                endMs = end,
                fileBytes = FleetRules.RelayMediaBytes,
                perDevice = LoadRules.RelayConcurrentPerDevice,
                requestsPerDevice = LoadRules.RelayRequestsPerDevice,
                readSeconds = RelayBurstPhase.ReadSeconds,
                relayCap,
                relayCapGiven = o.RelayCap is not null,
                // 台數 × 20 超過全域上限時每台的第 21 個先撞上全域上限（同一個 503 busy）：這一輪驗不到每台的上限（WP4.9 合併前審查）
                perDeviceCapVerified = LoadRules.PerDeviceCapVerified(members.Count, relayCap),
                expectedAccepted = LoadRules.ExpectedAcceptedRelays(members.Count, relayCap),
                accepted = results.Sum(d => d.Requests.Count(r => r.Index < LoadRules.RelayConcurrentPerDevice && r.Status == 200 && r.Complete)),
                s7Live = live,
                samples = window.Count,
                maxPrivateMb,
                fuseWarnMb = LoadRules.FuseWarnMb,
                server = new
                {
                    exitCode = hubExit,
                    readyzErrors,
                    maxRelaysActive = relays.Count > 0 ? relays.Max() : (int?)null,
                    maxGcHeapMb = window.Where(s => s.GcHeapMb is not null).Select(s => s.GcHeapMb).Max(),
                    relayTimeoutsDuring = timeoutsBefore is { } b && timeoutsAfter is { } a ? a - b : (long?)null,
                },
                devices = results,
                verdict = new
                {
                    verdict.Result,
                    verdict.Reasons,
                    note = "每台恰好第 21 個被拒（503 busy＋Retry-After）、其餘 20 個完整讀完（台數 × 20 超過全域上限 relayCap 時改成合計剛好 relayCap 個完整讀完、其餘都是 503 busy）、"
                        + "所有台讀完之後名額收回（206）；期間串流的 S7 p95 ≤500 ms、私有記憶體 <保險絲告警線",
                },
            };
        }

        /// <summary>/healthz 來回幾次的中位數（毫秒；第一次含 TLS 交握不算）</summary>
        private async Task<double?> RoundTripAsync(ProbeContext probe)
        {
            var times = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                var start = Stopwatch.GetTimestamp();
                try
                {
                    using var response = await probe.Http.GetAsync(probe.BaseUrl + "/healthz", Ct);
                    await response.Content.ReadAsByteArrayAsync(Ct);
                }
                catch (HttpRequestException)
                {
                    continue;
                }

                if (i > 0)
                    times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            return times.Count > 0 ? Math.Round(LoadRules.Percentile(times, 50), 2) : null;
        }

        private async Task CloseBrowsersAsync()
        {
            if (browsersClosed)
                return;
            browsersClosed = true;
            foreach (var browser in browsers)
            {
                try
                {
                    await browser.DisposeAsync();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Console.Error.WriteLine($"關閉瀏覽器 {browser.Index} 失敗：{ex.Message}");
                }
            }

            foreach (var (browser, _) in stalled)
                await browser.DisposeAsync();
        }

        /// <summary>不論成敗：關瀏覽器、以 PID 結束還在跑的裝置、停取樣、停自己啟動的 MyriagentHub（admin stop，帶 --data）</summary>
        public async Task CleanupAsync()
        {
            await CloseBrowsersAsync();
            if (fleet is not null)
                await fleet.DisposeAsync();
            if (samplingStop is not null)
            {
                await samplingStop.CancelAsync();
                if (sampling is not null)
                {
                    try
                    {
                        await sampling;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                samplingStop.Dispose();
            }

            if (hub is not null)
            {
                var stop = hubStop ?? await hub.StopAsync();
                if (hubStop is null)
                    ctx.Emit(new { @event = "hubStop", stop.Graceful, stop.AdminExitCode, stop.OrphanKeys, stop.DeletedKeys });
                await hub.DisposeAsync();
            }
        }
    }
}
