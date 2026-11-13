using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Myriagent.Remote;
using Myriagent.RemoteDesk;
using Myriagent.Update;
using MyriagentDesk;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.RemoteDesk;

/// <summary>
/// 鎖定畫面支援的擷取：共用規則（開幫手、擷取的要求與回覆）、幫手的處理者（真的管道、假的擷取）、
/// app 這端從「連服務、開幫手、連幫手、要畫面」整條路（<see cref="SecureDeskCapture"/>，兩個管道伺服器都在測試程序裡）
/// </summary>
public sealed class DeskServiceCaptureTests
{
    private const string Me = "S-1-5-21-1-2-3-1001";

    private static readonly ReleaseIdentity Version = new(new ReleaseVersion(0, 2, 960), "def5678");

    private sealed class FakeIdentifier(DeskServiceClientInfo info) : IDeskClientIdentifier
    {
        public DeskServiceClientInfo Identify(NamedPipeServerStream pipe) => info;
    }

    /// <summary>假的擷取：整張填同一個值（<see cref="Fill"/>），記下每次的要求</summary>
    private sealed class FakeCapturer : IDesktopCapturer
    {
        public byte Fill { get; set; } = 7;

        public string? Desktop { get; set; } = "Winlogon";

        public bool Fail { get; set; }

        public List<DeskServiceCaptureRequest> Requests { get; } = new();

        public DeskServiceCaptureHeader Capture(DeskServiceCaptureRequest request, byte[] buffer)
        {
            lock (Requests)
                Requests.Add(request);
            if (Fail)
                return new DeskServiceCaptureHeader(DeskServiceRules.CaptureFailed, 0, null, false, 5);
            var bytes = DeskServiceRules.CaptureBytes(request.OutW, request.OutH);
            buffer.AsSpan(0, bytes).Fill(Fill);
            return new DeskServiceCaptureHeader(DeskServiceRules.CaptureOk, bytes, Desktop, DeskServiceRules.IsSecureDesktop(Desktop), 0);
        }
    }

    private sealed class FakeAgents(string pipe) : ISessionAgents
    {
        public int Opens;

        public string Open(int sessionId)
        {
            Interlocked.Increment(ref Opens);
            return pipe;
        }
    }

    private sealed class NoProbe : IDeskSessionProbe
    {
        public DeskServiceProbe Probe(int sessionId) => new(sessionId, "Default", false, 0, []);
    }

    private static string HelperPipe(int session = 1)
        => DeskServiceRules.HelperPipeName(DeskServiceRules.TestServicePrefix + Guid.NewGuid().ToString("N")[..8], session, Guid.NewGuid().ToString("N"));

    private static async Task<DeskPipeServer> StartHelperAsync(string pipe, FakeCapturer capturer, int session = 1, int clientSession = 1)
    {
        var server = new DeskPipeServer(pipe, system: false, new FakeIdentifier(new DeskServiceClientInfo(clientSession, Me, Me)),
            new AgentPipeHandler(Version, system: false, session, capturer));
        server.Start();
        await server.Listening.WaitAsync(TimeSpan.FromSeconds(10));
        return server;
    }

    // ---- 規則 ----

    [Fact]
    public void 幫手的管道名稱_服務名稱加工作階段加亂數_其他的不認()
    {
        var name = DeskServiceRules.HelperPipeName("MyriagentDesk", 2, new string('a', 32));
        name.Should().Be("MyriagentDesk-s2-" + new string('a', 32));
        DeskServiceRules.IsHelperPipeName(name).Should().BeTrue();
        DeskServiceRules.IsHelperPipeName(DeskServiceRules.HelperPipeName("MyriagentDeskTest-0a1b", 12, new string('0', 32))).Should().BeTrue();

        DeskServiceRules.IsHelperPipeName("MyriagentDesk").Should().BeFalse();
        DeskServiceRules.IsHelperPipeName("Spooler-s2-" + new string('a', 32)).Should().BeFalse("不是這個服務的");
        DeskServiceRules.IsHelperPipeName("MyriagentDesk-s2-" + new string('a', 31)).Should().BeFalse();
        DeskServiceRules.IsHelperPipeName("MyriagentDesk-s2-" + new string('A', 32)).Should().BeFalse();
        DeskServiceRules.IsHelperPipeName("MyriagentDesk-sx-" + new string('a', 32)).Should().BeFalse();
        DeskServiceRules.IsHelperPipeName(@"MyriagentDesk-s2-..\" + new string('a', 29)).Should().BeFalse();
        DeskServiceRules.IsHelperPipeName(null).Should().BeFalse();
    }

    [Fact]
    public void 幫手收不收_還要和幫手在同一個工作階段()
    {
        DeskServiceRules.AdmitHelper(new DeskServiceClientInfo(2, Me, Me), helperSession: 2, helperIsSystem: true).Should().BeNull();
        DeskServiceRules.AdmitHelper(new DeskServiceClientInfo(3, Me, Me), 2, true).Should().NotBeNull("另一個工作階段登入的人不能經這個幫手看這邊的畫面");
        DeskServiceRules.AdmitHelper(new DeskServiceClientInfo(2, "S-1-5-21-9-9-9-1002", Me), 2, true).Should().NotBeNull();
    }

    [Fact]
    public void open的回覆_只收合格的幫手管道名稱()
    {
        var pipe = DeskServiceRules.HelperPipeName("MyriagentDesk", 1, new string('b', 32));
        DeskServiceRules.ParseOpen(DeskServiceRules.OpenReply(pipe)).Should().Be(pipe);
        DeskServiceRules.ParseOpen(DeskServiceRules.OpenReply(@"..\other")).Should().BeNull();
        DeskServiceRules.ParseOpen(DeskServiceRules.ErrorReply("failed", "x")).Should().BeNull();
        DeskServiceRules.ParseOpen("{\"ok\":true}").Should().BeNull();
    }

    [Fact]
    public void 擷取的要求_來回一致_超出範圍的不收()
    {
        var request = new DeskServiceCaptureRequest(-1920, 0, 3840, 2160, 1920, 1080, true);
        DeskServiceRules.TryParseRequest(DeskServiceRules.CaptureRequest(request), out var op, out var json).Should().BeTrue();
        op.Should().Be(DeskServiceRules.OpCapture);
        DeskServiceRules.ParseCaptureRequest(json!).Should().Be(request);

        JObject With(string key, JToken value)
        {
            var o = JObject.Parse(DeskServiceRules.CaptureRequest(request));
            o[key] = value;
            return o;
        }

        DeskServiceRules.ParseCaptureRequest(With("outW", 0)).Should().BeNull();
        DeskServiceRules.ParseCaptureRequest(With("outW", DeskServiceRules.MaxCaptureWidth + 1)).Should().BeNull("幫手照輸出大小配置緩衝，一定要有上限");
        DeskServiceRules.ParseCaptureRequest(With("outH", DeskServiceRules.MaxCaptureHeight + 1)).Should().BeNull();
        DeskServiceRules.ParseCaptureRequest(With("w", 0)).Should().BeNull();
        DeskServiceRules.ParseCaptureRequest(With("h", -5)).Should().BeNull();
        DeskServiceRules.ParseCaptureRequest(With("x", 1_000_000)).Should().BeNull();
        DeskServiceRules.ParseCaptureRequest(With("y", "0")).Should().BeNull();
        DeskServiceRules.ParseCaptureRequest(With("outW", 1.5)).Should().BeNull();
        DeskServiceRules.ParseCaptureRequest(With("force", "yes"))!.Force.Should().BeFalse("force 要是布林的 true");
        DeskServiceRules.CaptureBytes(DeskServiceRules.MaxCaptureWidth, DeskServiceRules.MaxCaptureHeight).Should().BePositive("最大的那張也不會溢位");
    }

    [Fact]
    public void 擷取的回覆_ok的位元組數要正好是要的那張_其他結果是0()
    {
        var ok = new DeskServiceCaptureHeader(DeskServiceRules.CaptureOk, 800, "Winlogon", true, 0);
        DeskServiceRules.ParseCaptureReply(DeskServiceRules.CaptureReply(ok), 800).Should().Be(ok);
        DeskServiceRules.ParseCaptureReply(DeskServiceRules.CaptureReply(ok), 801).Should().BeNull("大小對不上：後面的資料對不齊");
        DeskServiceRules.ParseCaptureReply(DeskServiceRules.CaptureReply(ok with { Bytes = 0 }), 800).Should().BeNull();

        var unchanged = new DeskServiceCaptureHeader(DeskServiceRules.CaptureUnchanged, 0, "Winlogon", true, 0);
        DeskServiceRules.ParseCaptureReply(DeskServiceRules.CaptureReply(unchanged), 800).Should().Be(unchanged);
        DeskServiceRules.ParseCaptureReply(DeskServiceRules.CaptureReply(unchanged with { Bytes = 800 }), 800).Should().BeNull();

        var failed = new DeskServiceCaptureHeader(DeskServiceRules.CaptureFailed, 0, null, false, 5);
        DeskServiceRules.ParseCaptureReply(DeskServiceRules.CaptureReply(failed), 800).Should().Be(failed);
        DeskServiceRules.ParseCaptureReply(DeskServiceRules.CaptureReply(failed with { Result = "maybe" }), 800).Should().BeNull();
        DeskServiceRules.ParseCaptureReply(DeskServiceRules.ErrorReply("invalid", "x"), 800).Should().BeNull();
    }

    [Fact]
    public void 命令列_幫手一定要給合格的管道名稱()
    {
        var pipe = DeskServiceRules.HelperPipeName("MyriagentDesk", 1, new string('c', 32));
        DeskServiceArgs.Parse(["session-agent", "--pipe", pipe]).Should().Be(new DeskServiceCommand(DeskServiceMode.SessionAgent, "MyriagentDesk", pipe, null));
        DeskServiceArgs.Parse(["session-agent"]).Mode.Should().Be(DeskServiceMode.Usage);
        DeskServiceArgs.Parse(["session-agent", "--pipe", "anything"]).Mode.Should().Be(DeskServiceMode.Usage);
        DeskServiceArgs.Parse(["install", "--pipe", pipe]).Mode.Should().Be(DeskServiceMode.Usage);
    }

    // ---- 幫手的處理者（真的管道） ----

    [Fact]
    public async Task 擷取_第一張整張_沒變回unchanged_變了或要求整張再送()
    {
        var pipe = HelperPipe();
        var capturer = new FakeCapturer();
        using var server = await StartHelperAsync(pipe, capturer);
        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        client.Hello().Features.Should().Equal(DeskServiceRules.FeatureCapture);

        var request = new DeskServiceCaptureRequest(0, 0, 640, 480, 320, 240, false);
        var buffer = new byte[320 * 240 * 4];
        var first = client.Capture(request, buffer);
        first.Should().Be(new DeskServiceCaptureHeader(DeskServiceRules.CaptureOk, buffer.Length, "Winlogon", true, 0));
        buffer.Should().OnlyContain(b => b == 7);

        Array.Clear(buffer);
        client.Capture(request, buffer).Result.Should().Be(DeskServiceRules.CaptureUnchanged);
        buffer.Should().OnlyContain(b => b == 0, "沒變的時候不送資料、不動緩衝");

        client.Capture(request with { Force = true }, buffer).Result.Should().Be(DeskServiceRules.CaptureOk, "要求整張就算沒變也送");
        buffer.Should().OnlyContain(b => b == 7);

        capturer.Fill = 9;
        client.Capture(request, buffer).Result.Should().Be(DeskServiceRules.CaptureOk);
        buffer.Should().OnlyContain(b => b == 9);
        client.Capture(request, buffer).Result.Should().Be(DeskServiceRules.CaptureUnchanged);

        // 換了範圍或大小：和上一張不能比，一定整張
        var smaller = new byte[160 * 120 * 4];
        client.Capture(request with { OutW = 160, OutH = 120 }, smaller).Result.Should().Be(DeskServiceRules.CaptureOk);
        client.Capture(request with { X = 640 }, buffer).Result.Should().Be(DeskServiceRules.CaptureOk);

        // 換了桌面（解鎖、出現 UAC）：內容剛好一樣也整張
        client.Capture(request with { X = 640 }, buffer).Result.Should().Be(DeskServiceRules.CaptureUnchanged);
        capturer.Desktop = "Default";
        var switched = client.Capture(request with { X = 640 }, buffer);
        switched.Result.Should().Be(DeskServiceRules.CaptureOk);
        switched.Secure.Should().BeFalse();
    }

    [Fact]
    public async Task 擷取不到_回failed_之後成功的一定整張_不合格的要求回invalid()
    {
        var pipe = HelperPipe();
        var capturer = new FakeCapturer();
        using var server = await StartHelperAsync(pipe, capturer);
        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        var request = new DeskServiceCaptureRequest(0, 0, 64, 64, 32, 32, false);
        var buffer = new byte[32 * 32 * 4];

        client.Capture(request, buffer).Result.Should().Be(DeskServiceRules.CaptureOk);
        capturer.Fail = true;
        var failed = client.Capture(request, buffer);
        failed.Result.Should().Be(DeskServiceRules.CaptureFailed);
        failed.Error.Should().Be(5);
        capturer.Fail = false;
        client.Capture(request, buffer).Result.Should().Be(DeskServiceRules.CaptureOk, "失敗之後不能說和上一張一樣");

        var bad = JObject.Parse(DeskServiceRules.CaptureRequest(request));
        bad["outW"] = 100_000;
        DeskServiceRules.ErrorOf(client.Call(bad.ToString(Newtonsoft.Json.Formatting.None)))!.Code.Should().Be(DeskServiceRules.CodeInvalid);
        capturer.Requests.Should().HaveCount(3, "不合格的要求沒有送到擷取");
        // 服務本體的指令幫手不認得
        DeskServiceRules.ErrorOf(client.Call(DeskServiceRules.OpenRequest()))!.Code.Should().Be(DeskServiceRules.CodeUnsupported);
    }

    [Fact]
    public async Task 另一個工作階段的用戶端_幫手不收()
    {
        var pipe = HelperPipe(session: 2);
        var capturer = new FakeCapturer();
        using var server = await StartHelperAsync(pipe, capturer, session: 2, clientSession: 3);
        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        var reply = client.Call(DeskServiceRules.CaptureRequest(new DeskServiceCaptureRequest(0, 0, 64, 64, 32, 32, false)));
        DeskServiceRules.ErrorOf(reply)!.Code.Should().Be(DeskServiceRules.CodeDenied);
        capturer.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task 大的畫面_分好幾次讀也對得齊_連續幾張不串在一起()
    {
        var pipe = HelperPipe();
        var capturer = new FakeCapturer();
        using var server = await StartHelperAsync(pipe, capturer);
        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        var request = new DeskServiceCaptureRequest(0, 0, 1920, 1080, 1920, 1080, true);
        var buffer = new byte[1920 * 1080 * 4];
        for (byte fill = 1; fill <= 3; fill++)
        {
            capturer.Fill = fill;
            client.Capture(request, buffer).Result.Should().Be(DeskServiceRules.CaptureOk);
            buffer[0].Should().Be(fill);
            buffer[^1].Should().Be(fill);
            buffer.AsSpan().IndexOfAnyExcept(fill).Should().Be(-1);
        }
        client.Hello().Version.Should().Be("0.2.960", "畫面讀完之後下一行還是對齊的");
    }

    // ---- app 這端整條路 ----

    private static async Task<(DeskPipeServer Service, DeskPipeServer Helper, string ServicePipe, FakeCapturer Capturer, FakeAgents Agents)> StartBothAsync()
    {
        var helperPipe = HelperPipe();
        var capturer = new FakeCapturer();
        var helper = await StartHelperAsync(helperPipe, capturer);
        var agents = new FakeAgents(helperPipe);
        var servicePipe = "desk-test-" + Guid.NewGuid().ToString("N")[..12];
        var service = new DeskPipeServer(servicePipe, system: false, new FakeIdentifier(new DeskServiceClientInfo(1, Me, Me)),
            new ServicePipeHandler(Version, system: false, new NoProbe(), agents));
        service.Start();
        await service.Listening.WaitAsync(TimeSpan.FromSeconds(10));
        return (service, helper, servicePipe, capturer, agents);
    }

    [Fact]
    public async Task app要畫面_連服務開幫手再連幫手_之後重複用同一條連線()
    {
        var (service, helper, servicePipe, capturer, agents) = await StartBothAsync();
        using var a = service;
        using var b = helper;
        // 每問一次時鐘就前進一秒：不受「沒變之後隔一下才再問」影響
        var clock = 0L;
        using var capture = new SecureDeskCapture(() => true, servicePipe, () => clock += 1000);
        var buffer = new byte[100 * 50 * 4];
        var bounds = new DeskRect(10, 20, 400, 200);

        capture.Capture(bounds, 100, 50, buffer, force: true).Should().Be(DeskCapture.Ok);
        buffer.Should().OnlyContain(v => v == 7);
        capturer.Requests.Single().Should().Be(new DeskServiceCaptureRequest(10, 20, 400, 200, 100, 50, true));
        capture.Capture(bounds, 100, 50, buffer, force: false).Should().Be(DeskCapture.Unchanged);
        capturer.Fill = 8;
        capture.Capture(bounds, 100, 50, buffer, force: false).Should().Be(DeskCapture.Ok);
        agents.Opens.Should().Be(1, "幫手的連線留著用，不是每張都重開");

        // 幫手也擷取不到：回 null（呼叫的人照原本的原因回報），連線留著
        capturer.Fail = true;
        capture.Capture(bounds, 100, 50, buffer, force: false).Should().BeNull();
        capturer.Fail = false;
        capture.Capture(bounds, 100, 50, buffer, force: false).Should().Be(DeskCapture.Ok);
        agents.Opens.Should().Be(1);
    }

    [Fact]
    public async Task app要畫面_沒變之後隔一下才再問幫手_要求整張或換了範圍馬上問()
    {
        var (service, helper, servicePipe, capturer, _) = await StartBothAsync();
        using var a = service;
        using var b = helper;
        var clock = 1000L;
        using var capture = new SecureDeskCapture(() => true, servicePipe, () => clock);
        var buffer = new byte[100 * 50 * 4];
        var bounds = new DeskRect(0, 0, 400, 200);

        capture.Capture(bounds, 100, 50, buffer, force: true).Should().Be(DeskCapture.Ok);
        capture.Capture(bounds, 100, 50, buffer, force: false).Should().Be(DeskCapture.Unchanged);
        capturer.Requests.Should().HaveCount(2);

        // 還在安靜的期間：不問幫手，直接當沒變（就算畫面其實變了，最多晚四分之一秒看到）
        capturer.Fill = 9;
        clock += SecureDeskCapture.UnchangedIntervalMs - 1;
        capture.Capture(bounds, 100, 50, buffer, force: false).Should().Be(DeskCapture.Unchanged);
        capturer.Requests.Should().HaveCount(2);

        // 要求整張、換了範圍：馬上問
        capture.Capture(bounds, 100, 50, buffer, force: true).Should().Be(DeskCapture.Ok);
        buffer[0].Should().Be(9);
        capture.Capture(bounds, 100, 50, buffer, force: false).Should().Be(DeskCapture.Unchanged);
        capture.Capture(bounds with { X = 400 }, 100, 50, buffer, force: false).Should().Be(DeskCapture.Ok);

        // 過了那段時間：照常問
        capture.Capture(bounds with { X = 400 }, 100, 50, buffer, force: false).Should().Be(DeskCapture.Unchanged);
        var asked = capturer.Requests.Count;
        clock += SecureDeskCapture.UnchangedIntervalMs;
        capturer.Fill = 10;
        capture.Capture(bounds with { X = 400 }, 100, 50, buffer, force: false).Should().Be(DeskCapture.Ok);
        capturer.Requests.Should().HaveCount(asked + 1);
    }

    [Fact]
    public void app要畫面_沒裝服務_連試都不試()
    {
        var asked = 0;
        using var capture = new SecureDeskCapture(() =>
        {
            asked++;
            return false;
        }, "desk-test-" + Guid.NewGuid().ToString("N")[..12]);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
            capture.Capture(new DeskRect(0, 0, 64, 64), 32, 32, new byte[32 * 32 * 4], force: true).Should().BeNull();
        asked.Should().Be(1, "沒裝的時候隔一陣子才再看一次安裝紀錄");
        watch.ElapsedMilliseconds.Should().BeLessThan(1000, "不能讓擷取迴圈每一格都等連線逾時");
    }

    [Fact]
    public void app要畫面_裝了但服務沒在執行_連不上就先不試()
    {
        using var capture = new SecureDeskCapture(() => true, "desk-test-" + Guid.NewGuid().ToString("N")[..12]);
        var buffer = new byte[32 * 32 * 4];
        var watch = Stopwatch.StartNew();
        capture.Capture(new DeskRect(0, 0, 64, 64), 32, 32, buffer, force: true).Should().BeNull();
        var first = watch.ElapsedMilliseconds;
        for (var i = 0; i < 20; i++)
            capture.Capture(new DeskRect(0, 0, 64, 64), 32, 32, buffer, force: true).Should().BeNull();
        (watch.ElapsedMilliseconds - first).Should().BeLessThan(500, "第一次連不上之後的幾秒內不再試");
    }

    [Fact]
    public async Task app要畫面_幫手斷了_丟掉連線_下次再開()
    {
        var (service, helper, servicePipe, _, agents) = await StartBothAsync();
        using var a = service;
        var clock = 0L;
        using var capture = new SecureDeskCapture(() => true, servicePipe, () => clock += 1000);
        var buffer = new byte[32 * 32 * 4];
        capture.Capture(new DeskRect(0, 0, 64, 64), 32, 32, buffer, force: true).Should().Be(DeskCapture.Ok);

        helper.Dispose();
        // 幫手結束了：這次（或下一次）要畫面時發現連線斷了，回 null
        await Task.Delay(200);
        DeskCapture? after = DeskCapture.Ok;
        for (var i = 0; i < 3 && after is not null; i++)
            after = capture.Capture(new DeskRect(0, 0, 64, 64), 32, 32, buffer, force: false);
        after.Should().BeNull();
        agents.Opens.Should().Be(1, "斷了之後隔一下才重開，不是馬上");
    }
}

/// <summary>
/// 真的 <c>MyriagentDesk.exe</c> 的擷取：前景模式的服務開幫手（一般權限、在自己的工作階段），app 的用戶端連上幫手、真的以 GDI 擷取螢幕的一角。
/// 沒有系統權限：擷取的是使用者的桌面（鎖定時內容是黑的或舊的，這裡只驗流程與大小，不驗內容）
/// </summary>
public sealed class DeskServiceCaptureExeTests
{
    [Fact]
    public async Task 前景模式_open開幫手_幫手真的擷取一張()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, DeskServiceRules.ExeName);
        var pipe = "desk-exe-" + Guid.NewGuid().ToString("N")[..12];
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--pipe");
        process.StartInfo.ArgumentList.Add(pipe);
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            DeskServiceClient? service = null;
            var deadline = Environment.TickCount64 + 20_000;
            while (service is null)
            {
                try
                {
                    service = DeskServiceClient.Connect(pipe, TimeSpan.FromMilliseconds(500));
                }
                catch (TimeoutException) when (Environment.TickCount64 < deadline && !process.HasExited)
                {
                    await Task.Delay(100);
                }
            }

            string helperPipe;
            using (service)
            {
                service.Hello().Features.Should().Contain(DeskServiceRules.FeatureCapture);
                helperPipe = service.Open();
                service.Open().Should().Be(helperPipe, "同一個工作階段的幫手還活著就沿用");
            }

            DeskServiceRules.IsHelperPipeName(helperPipe).Should().BeTrue();
            using var helper = DeskServiceClient.Connect(helperPipe, TimeSpan.FromSeconds(5));
            var hello = helper.Hello();
            hello.System.Should().BeFalse();
            hello.Session.Should().Be(Process.GetCurrentProcess().SessionId);

            var request = new DeskServiceCaptureRequest(0, 0, 320, 200, 160, 100, true);
            var buffer = new byte[160 * 100 * 4];
            var header = helper.Capture(request, buffer);
            if (header.Result == DeskServiceRules.CaptureOk)
            {
                header.Bytes.Should().Be(buffer.Length);
                header.Desktop.Should().NotBeNullOrEmpty();
                // 第二張不要求整張：畫面沒變就是 unchanged，有變就是 ok，兩種都對
                helper.Capture(request with { Force = false }, buffer).Result.Should().BeOneOf(DeskServiceRules.CaptureOk, DeskServiceRules.CaptureUnchanged);
            }
            else
            {
                // 這台現在是安全桌面（一般權限開不了）：照實回 failed 與錯誤碼
                header.Result.Should().Be(DeskServiceRules.CaptureFailed);
                header.Bytes.Should().Be(0);
            }
        }
        finally
        {
            // 標準輸入關了服務就結束，並把它開的幫手一起結束
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // 已經結束了
            }

            if (!process.WaitForExit(10_000))
                process.Kill(entireProcessTree: true);
            _ = stderr;
        }
    }
}
