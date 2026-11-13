using System.Text;
using Myriagent.Remote;
using Myriagent.Update;

namespace MyriagentDesk;

/// <summary>
/// 進入點：一個 exe、幾種模式（<see cref="DeskServiceArgs"/>）。
/// <c>service</c>：Windows 的服務控制管理員啟動的服務本體（LocalSystem）。
/// <c>install</c>／<c>uninstall</c>：app 以 UAC 啟動的安裝器（要系統管理員；結束碼是 <see cref="DeskServiceExit"/>）。
/// <c>session-probe</c>：服務在使用者的工作階段裡開的幫手，印出目前的輸入桌面就結束。
/// <c>session-agent --pipe 名稱</c>：擷取用的幫手——開自己的管道、照要求擷取目前的輸入桌面，沒人連了自己結束。
/// <c>query</c>：診斷——以一般權限連到裝好的服務，印出它的 hello 與 probe 的回覆（它看到的輸入桌面）。
/// <c>run --pipe 名稱</c>：開發與測試用的前景模式——同一套管道與協定，但以執行它的使用者的權限（碰不到安全桌面），標準輸入關了就結束
/// </summary>
internal static class Program
{
    /// <summary>同時只跑一個安裝器（所有服務名稱共用：安裝很快，不必分開）</summary>
    private const string InstallMutex = @"Global\MyriagentDesk-install";

    public static int Main(string[] args)
    {
        var command = DeskServiceArgs.Parse(args);
        try
        {
            switch (command.Mode)
            {
                case DeskServiceMode.Service:
                    return ServiceHost.Run();
                case DeskServiceMode.SessionProbe:
                    return SessionProbe();
                case DeskServiceMode.SessionAgent:
                    return SessionAgent(command.Pipe!);
                case DeskServiceMode.Run:
                    return RunForeground(command.Pipe!);
                case DeskServiceMode.Install:
                    return (int)Elevated(command.ServiceName, (installer, programFiles) => installer.Install(new DeskInstallOptions(
                        command.ServiceName, AppContext.BaseDirectory, programFiles, SelfVersion.Current, ReleaseKeys.Production, DateTimeOffset.UtcNow, command.Trust)));
                case DeskServiceMode.Uninstall:
                    return (int)Elevated(command.ServiceName, (installer, programFiles) => installer.Uninstall(command.ServiceName, programFiles));
                case DeskServiceMode.SelfUpdate:
                    return (int)Elevated(command.ServiceName, (installer, programFiles) => SelfUpdate(installer, programFiles, command.ServiceName));
                case DeskServiceMode.Query:
                    return Query(DeskServiceRules.PipeNameOf(command.ServiceName));
                case DeskServiceMode.Version:
                    Console.Out.WriteLine(SelfVersion.Current.Release);
                    return 0;
                default:
                    Console.Error.WriteLine(command.Error);
                    Console.Error.WriteLine(DeskServiceArgs.Usage);
                    return (int)DeskServiceExit.Usage;
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Error("未預期的錯誤", ex);
            return (int)DeskServiceExit.Unexpected;
        }
    }

    private static DeskServiceExit Elevated(string serviceName, Func<DeskServiceInstaller, string, DeskServiceExit> action)
    {
        if (!ProcessIdentity.IsElevated())
            return DeskServiceExit.NotElevated;
        using var mutex = new Mutex(false, InstallMutex);
        var owned = false;
        try
        {
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // 上一個安裝器沒放開就結束了：現在是我們的
                owned = true;
            }
            if (!owned)
                return DeskServiceExit.Busy;

            // 位置問 Windows（已知資料夾），不看環境變數：以 UAC 啟動的程序拿到的環境變數是呼叫的人給的
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrEmpty(programFiles) || !Path.IsPathFullyQualified(programFiles))
                return DeskServiceExit.Unexpected;
            ServiceLog.UseDirectory(Path.Combine(DeskServiceRules.InstallRoot(programFiles, serviceName), "logs"));
            return action(new DeskServiceInstaller(new ScmServiceControl()), programFiles);
        }
        finally
        {
            if (owned)
                mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// 服務自己更新的第二步（服務啟動的、系統權限）：只從安裝資料夾的 incoming 執行——那是服務驗過簽章才寫進去的、只有系統管理員寫得了。
    /// 從別的地方執行一律不做（不讓人拿這個模式把任意資料夾的檔裝成系統服務）。裝的是自己這一份，安裝器再驗一次簽章與版本
    /// </summary>
    private static DeskServiceExit SelfUpdate(DeskServiceInstaller installer, string programFiles, string serviceName)
    {
        var incoming = Path.Combine(DeskServiceRules.InstallRoot(programFiles, serviceName), DeskServiceRules.IncomingFolder);
        var own = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        if (!ProcessIdentity.IsSystem() || !string.Equals(own, Path.GetFullPath(incoming), StringComparison.OrdinalIgnoreCase))
        {
            ServiceLog.Error($"self-update 只由服務從 {incoming} 執行（現在在 {own}）");
            return DeskServiceExit.Usage;
        }

        return installer.Install(new DeskInstallOptions(serviceName, AppContext.BaseDirectory, programFiles, SelfVersion.Current, ReleaseKeys.Production,
            DateTimeOffset.UtcNow, SelfUpdate: true));
    }

    private static int SessionProbe()
    {
        var (name, error) = InputDesktop.Read();
        // 直接寫位元組：沒有主控台的程序設不了 Console 的編碼
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(Encoding.UTF8.GetBytes(DeskServiceRules.HelperProbeLine(name, error, InputDesktop.List()) +"\n"));
        stdout.Flush();
        return 0;
    }

    /// <summary>連到服務、印出兩個回覆（各一行 JSON）；連不上或逾時回 1</summary>
    private static int Query(string pipeName)
    {
        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
            pipe.Connect(3000);
            var reader = new PipeLineReader(pipe);
            foreach (var request in new[] { DeskServiceRules.HelloRequest(), DeskServiceRules.ProbeRequest() })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                pipe.Write(Encoding.UTF8.GetBytes(request + '\n'));
                pipe.Flush();
                var reply = reader.ReadLineAsync(timeout.Token).GetAwaiter().GetResult();
                if (reply is null)
                    break;
                Console.Out.WriteLine(reply);
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("連不上服務：" + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// 擷取用的幫手（服務在使用者的工作階段裡開的，系統權限；前景模式開的是一般權限）。沒有連線超過 <see cref="DeskServiceRules.HelperIdleExit"/> 就結束：
    /// app 不擷取了、服務停了、app 當掉，都不會留下一個系統權限的程序掛著
    /// </summary>
    private static int SessionAgent(string pipe)
    {
        DesktopCapturer.UsePhysicalPixels();
        var system = ProcessIdentity.IsSystem();
        // 安裝資料夾（執行檔在 <安裝資料夾>\p-xxxx\）：記錄與信任的伺服器都在那裡。只有系統權限的幫手才讀——
        // 前景模式（一般權限）不在 Program Files 裡，沒有信任的伺服器，也注入不了安全桌面
        var root = system && Environment.ProcessPath is { } exe ? Path.GetDirectoryName(Path.GetDirectoryName(exe)!) : null;
        if (root is not null)
            ServiceLog.UseDirectory(Path.Combine(root, "logs"));
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        using var capturer = new DesktopCapturer();
        using var injector = new DesktopInjector();
        // 安全桌面上的提示列只有系統權限的幫手畫得上去（前景模式看不到安全桌面：不必開）
        using var banner = system ? new SecureDeskBanner() : null;
        using var server = new DeskPipeServer(pipe, system, new NativeClientIdentifier(system),
            new AgentPipeHandler(SelfVersion.Current, system, session, capturer, injector, () => ReadTrust(root), banner));
        server.Start();
        ServiceLog.Info($"擷取幫手啟動：工作階段 {session}，系統權限 {system}");
        while (server.ActiveConnections > 0 || Environment.TickCount64 - server.LastActivityTick < DeskServiceRules.HelperIdleExit.TotalMilliseconds)
            Thread.Sleep(500);
        ServiceLog.Info($"擷取幫手結束：工作階段 {session}（沒有連線）");
        return 0;
    }

    /// <summary>安裝時記下的信任的伺服器（<c>trust.json</c>）；沒有、讀不了、格式不對回 null</summary>
    private static DeskServiceTrust? ReadTrust(string? root)
    {
        if (root is null)
            return null;
        try
        {
            var path = Path.Combine(root, DeskServiceRules.TrustFile);
            return File.Exists(path) && new FileInfo(path).Length <= 16 * 1024 ? DeskServiceRules.ParseTrust(File.ReadAllText(path, Encoding.UTF8)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int RunForeground(string pipe)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("不知道自己的執行檔在哪裡");
        using var agents = new SessionAgents(exe, DeskServiceRules.ServiceName);
        using var server = new DeskPipeServer(pipe, system: false, new NativeClientIdentifier(system: false),
            new ServicePipeHandler(SelfVersion.Current, system: false, new HelperSessionProbe(system: false, exe), agents));
        server.Start();
        ServiceLog.Info($"前景模式：管道 {pipe}，{SelfVersion.Current.Release}（標準輸入關閉或按 Enter 結束）");
        Console.In.ReadLine();
        return 0;
    }
}
