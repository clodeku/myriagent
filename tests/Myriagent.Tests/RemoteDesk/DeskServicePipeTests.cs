using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Myriagent.Remote;
using Myriagent.RemoteDesk;
using Myriagent.Update;
using MyriagentDesk;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.RemoteDesk;

/// <summary>
/// 鎖定畫面支援的管道：服務的 <see cref="DeskPipeServer"/>（前景模式：只開給執行測試的使用者，沒有系統權限）對 app 的 <see cref="DeskServiceClient"/>，
/// 真的具名管道、暫時的名稱。用戶端是誰、輸入桌面是假的（要驗不收的情況與安全桌面的回覆）；真的身分查詢另外驗一次
/// </summary>
public sealed class DeskServicePipeTests
{
    private const string Me = "S-1-5-21-1-2-3-1001";

    private static readonly ReleaseIdentity Version = new(new ReleaseVersion(0, 2, 950), "abc1234");

    private sealed class FakeIdentifier(DeskServiceClientInfo info) : IDeskClientIdentifier
    {
        public int Calls;

        public DeskServiceClientInfo Identify(NamedPipeServerStream pipe)
        {
            Interlocked.Increment(ref Calls);
            return info;
        }
    }

    private sealed class FakeProbe : IDeskSessionProbe
    {
        public Func<int, DeskServiceProbe> Answer { get; set; } = session => new DeskServiceProbe(session, "Winlogon", true, 0, ["Default", "Winlogon"]);

        public List<int> Sessions { get; } = new();

        public DeskServiceProbe Probe(int sessionId)
        {
            lock (Sessions)
                Sessions.Add(sessionId);
            return Answer(sessionId);
        }
    }

    private sealed class FakeAgents : ISessionAgents
    {
        public Func<int, string> Answer { get; set; } = session => DeskServiceRules.HelperPipeName(DeskServiceRules.ServiceName, session, new string('a', 32));

        public List<int> Sessions { get; } = new();

        public string Open(int sessionId)
        {
            lock (Sessions)
                Sessions.Add(sessionId);
            return Answer(sessionId);
        }
    }

    private static string NewPipe() => "desk-test-" + Guid.NewGuid().ToString("N")[..12];

    private static async Task<(DeskPipeServer Server, string Pipe)> StartAsync(IDeskClientIdentifier identifier, IDeskSessionProbe probe, ISessionAgents? agents = null)
    {
        var pipe = NewPipe();
        var server = new DeskPipeServer(pipe, system: false, identifier, new ServicePipeHandler(Version, system: false, probe, agents ?? new FakeAgents()));
        server.Start();
        await server.Listening.WaitAsync(TimeSpan.FromSeconds(10));
        return (server, pipe);
    }

    [Fact]
    public async Task hello與probe_同一條連線來回_只看用戶端自己的工作階段()
    {
        var identifier = new FakeIdentifier(new DeskServiceClientInfo(7, Me, Me));
        var probe = new FakeProbe();
        var (server, pipe) = await StartAsync(identifier, probe);
        using var dispose = server;

        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        var hello = client.Hello();
        hello.Should().BeEquivalentTo(new DeskServiceHello(DeskServiceRules.ProtocolVersion, "0.2.950", "abc1234", false, 7, [DeskServiceRules.FeatureProbe, DeskServiceRules.FeatureCapture]));
        client.Probe().Should().BeEquivalentTo(new DeskServiceProbe(7, "Winlogon", true, 0, ["Default", "Winlogon"]));
        client.Probe().Session.Should().Be(7);

        probe.Sessions.Should().Equal(7, 7);
        identifier.Calls.Should().Be(1, "一條連線只查一次是誰");
    }

    [Fact]
    public async Task 不收的用戶端_回denied就斷線_不做任何事()
    {
        var probe = new FakeProbe();
        var (server, pipe) = await StartAsync(new FakeIdentifier(new DeskServiceClientInfo(1, "S-1-5-21-9-9-9-1002", Me)), probe);
        using var dispose = server;

        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        var reply = client.Call(DeskServiceRules.ProbeRequest());
        DeskServiceRules.ErrorOf(reply)!.Code.Should().Be(DeskServiceRules.CodeDenied);
        FluentActions.Invoking(() => client.Call(DeskServiceRules.HelloRequest())).Should().Throw<IOException>("服務回了 denied 就關掉連線");
        probe.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task 看不懂的要求與不認得的op_回錯誤_連線留著()
    {
        var (server, pipe) = await StartAsync(new FakeIdentifier(new DeskServiceClientInfo(1, Me, Me)), new FakeProbe());
        using var dispose = server;

        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        DeskServiceRules.ErrorOf(client.Call("not json"))!.Code.Should().Be(DeskServiceRules.CodeInvalid);
        // capture 是幫手的指令，服務本體不認得
        DeskServiceRules.ErrorOf(client.Call(new JObject { ["op"] = "capture" }.ToString(Newtonsoft.Json.Formatting.None)))!.Code.Should().Be(DeskServiceRules.CodeUnsupported);
        // 要求裡自己帶工作階段編號沒有用：服務只看管道查出來的那個
        var probe = DeskServiceRules.ParseProbe(client.Call(new JObject { ["op"] = "probe", ["session"] = 99 }.ToString(Newtonsoft.Json.Formatting.None)))!;
        probe.Session.Should().Be(1);
        client.Hello().Version.Should().Be("0.2.950");
    }

    [Fact]
    public async Task 幫手開不起來_回failed_不讓連線斷掉()
    {
        var probe = new FakeProbe { Answer = _ => throw new TimeoutException("工作階段 1 的幫手逾時") };
        var (server, pipe) = await StartAsync(new FakeIdentifier(new DeskServiceClientInfo(1, Me, Me)), probe);
        using var dispose = server;

        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        var error = DeskServiceRules.ErrorOf(client.Call(DeskServiceRules.ProbeRequest()))!;
        error.Code.Should().Be(DeskServiceRules.CodeFailed);
        error.Message.Should().Contain("逾時");
        FluentActions.Invoking(() => client.Probe()).Should().Throw<InvalidOperationException>().WithMessage("*failed*");
        client.Hello().Proto.Should().Be(DeskServiceRules.ProtocolVersion);
    }

    [Fact]
    public async Task open_開用戶端自己那個工作階段的幫手_回它的管道_開不了回failed()
    {
        var agents = new FakeAgents();
        var (server, pipe) = await StartAsync(new FakeIdentifier(new DeskServiceClientInfo(3, Me, Me)), new FakeProbe(), agents);
        using var dispose = server;

        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        client.Open().Should().Be(DeskServiceRules.HelperPipeName(DeskServiceRules.ServiceName, 3, new string('a', 32)));
        agents.Sessions.Should().Equal(3);

        agents.Answer = _ => throw new TimeoutException("擷取幫手沒有在時間內準備好");
        FluentActions.Invoking(() => client.Open()).Should().Throw<InvalidOperationException>().WithMessage("*failed*");

        // 服務回了不合格的管道名稱（不該發生）：用戶端不拿去連
        agents.Answer = _ => @"..\evil";
        FluentActions.Invoking(() => client.Open()).Should().Throw<InvalidOperationException>();
        client.Hello().Proto.Should().Be(DeskServiceRules.ProtocolVersion, "連線還在");
    }

    [Fact]
    public async Task 一行太長_服務直接斷線()
    {
        var (server, pipe) = await StartAsync(new FakeIdentifier(new DeskServiceClientInfo(1, Me, Me)), new FakeProbe());
        using var dispose = server;

        using var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        raw.Connect(5000);
        var big = Encoding.UTF8.GetBytes(new string('x', DeskServiceRules.MaxLineChars * 3 + 8192));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var closed = false;
        try
        {
            await raw.WriteAsync(big, timeout.Token);
            await raw.FlushAsync(timeout.Token);
            closed = await raw.ReadAsync(new byte[16], timeout.Token) == 0;
        }
        catch (IOException)
        {
            closed = true;
        }
        closed.Should().BeTrue("沒有換行的超長資料不會一直被收下去");
    }

    [Fact]
    public async Task 同時好幾條連線_各自回覆()
    {
        var (server, pipe) = await StartAsync(new FakeIdentifier(new DeskServiceClientInfo(1, Me, Me)), new FakeProbe());
        using var dispose = server;

        var versions = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(10));
            client.Probe();
            return client.Hello().Version;
        })));
        versions.Should().AllBe("0.2.950");
    }

    [Fact]
    public async Task 真的身分查詢_前景模式_工作階段與使用者是這個測試程序的()
    {
        var (server, pipe) = await StartAsync(new NativeClientIdentifier(system: false), new FakeProbe());
        using var dispose = server;

        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        client.Hello().Session.Should().Be(Process.GetCurrentProcess().SessionId, "管道查出來的工作階段就是用戶端程序的");
        client.Probe().Session.Should().Be(Process.GetCurrentProcess().SessionId, "同一個使用者：收");
        using var me = WindowsIdentity.GetCurrent();
        ProcessIdentity.UserSid().Should().Be(me.User!.Value);
    }

    [Fact]
    public async Task 系統權限用的存取控制_互動登入的使用者連得上_但不能再開一個同名的管道()
    {
        // 和真的服務同一份存取控制清單（拒絕網路、LocalSystem 完全控制、互動登入的使用者只能讀寫）。測試程序不是 LocalSystem：
        // 第一個管道建得起來（新物件不檢查清單），之後的建不了——正好驗「一般使用者不能冒充服務再開一個」；連上的那一條照常服務
        var pipe = NewPipe();
        using var server = new DeskPipeServer(pipe, system: true, new FakeIdentifier(new DeskServiceClientInfo(1, Me, Me)),
            new ServicePipeHandler(Version, system: true, new FakeProbe(), new FakeAgents()));
        server.Start();
        await server.Listening.WaitAsync(TimeSpan.FromSeconds(10));

        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        var hello = client.Hello();
        hello.System.Should().BeTrue();
        hello.Session.Should().Be(1);
        client.Probe().Desktop.Should().Be("Winlogon");

        FluentActions.Invoking(() =>
        {
            using var squat = new NamedPipeServerStream(pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }).Should().Throw<UnauthorizedAccessException>("只有讀寫權限的使用者不能建立這個管道的另一個實體");
    }

    [Fact]
    public void 沒有服務_連線逾時()
        => FluentActions.Invoking(() => DeskServiceClient.Connect(NewPipe(), TimeSpan.FromMilliseconds(300))).Should().Throw<TimeoutException>();
}

/// <summary>
/// 真的 <c>MyriagentDesk.exe</c>（建置輸出）以前景模式執行：命令列、管道、在自己的工作階段開幫手看輸入桌面，整條走一次。
/// 沒有系統權限：服務回報 system=false，鎖定時輸入桌面開不了（錯誤碼）也是正常的回覆
/// </summary>
public sealed class DeskServiceExeTests
{
    private static string Exe => Path.Combine(AppContext.BaseDirectory, DeskServiceRules.ExeName);

    [Fact]
    public void version_印出這一版()
    {
        var (exit, output) = Run("version");
        exit.Should().Be(0);
        ReleaseVersion.TryParse(output.Trim().Split('-')[0], out _).Should().BeTrue(output);
    }

    [Fact]
    public void install_沒有系統管理員權限_什麼都不做_結束碼是NotElevated()
    {
        if (ProcessIdentity.IsElevated())
            return; // 以系統管理員跑測試時這個情況驗不了（也不能讓它真的去裝）
        Run("install", "--name", "MyriagentDeskTest-0000").Exit.Should().Be((int)DeskServiceExit.NotElevated);
        Run("uninstall", "--name", "MyriagentDeskTest-0000").Exit.Should().Be((int)DeskServiceExit.NotElevated);
        // 自己更新的第二步只由服務（系統權限）從安裝資料夾的 incoming 執行：別人從別處執行什麼都不做
        Run("self-update", "--name", "MyriagentDeskTest-0000").Exit.Should().Be((int)DeskServiceExit.NotElevated);
    }

    [Fact]
    public void 用法錯誤_結束碼2()
        => Run("install", "--from", @"C:\somewhere").Exit.Should().Be((int)DeskServiceExit.Usage);

    [Fact]
    public void session_probe_印出一行看得懂的結果()
    {
        var (exit, output) = Run("session-probe");
        exit.Should().Be(0);
        var probe = DeskServiceRules.ParseHelperProbe(output.Trim(), 1);
        probe.Should().NotBeNull(output);
        (probe!.Desktop is not null || probe.Error != 0).Should().BeTrue();
    }

    [Fact]
    public async Task 前景模式_app的用戶端連得上_hello與probe()
    {
        var pipe = "desk-exe-" + Guid.NewGuid().ToString("N")[..12];
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(Exe)
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
        _ = process.StandardError.ReadToEndAsync();
        try
        {
            DeskServiceClient? client = null;
            var deadline = Environment.TickCount64 + 20_000;
            while (client is null)
            {
                try
                {
                    client = DeskServiceClient.Connect(pipe, TimeSpan.FromMilliseconds(500));
                }
                catch (TimeoutException) when (Environment.TickCount64 < deadline && !process.HasExited)
                {
                    await Task.Delay(100);
                }
            }

            using (client)
            {
                var hello = client.Hello();
                hello.System.Should().BeFalse("前景模式沒有系統權限");
                hello.Session.Should().Be(Process.GetCurrentProcess().SessionId);
                hello.Features.Should().Contain(DeskServiceRules.FeatureProbe);
                var probe = client.Probe();
                probe.Session.Should().Be(hello.Session);
                (probe.Desktop is not null || probe.Error != 0).Should().BeTrue("看得到就有名稱，看不到（鎖定中）就有錯誤碼");
            }

            // 標準輸入關了就結束
            process.StandardInput.Close();
            process.WaitForExit(10_000).Should().BeTrue();
            process.ExitCode.Should().Be(0);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static (int Exit, string Output) Run(params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("MyriagentDesk.exe 沒有結束");
        }
        return (process.ExitCode, output.Result);
    }
}
