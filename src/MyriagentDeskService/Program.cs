using System.Text;
using Myriagent.Remote;
using Myriagent.Update;

namespace MyriagentDesk;

/// <summary>
/// 進入點：一個 exe、幾種模式（<see cref="DeskServiceArgs"/>）。
/// <c>service</c>：Windows 的服務控制管理員啟動的服務本體（LocalSystem）。
/// <c>install</c>／<c>uninstall</c>：app 以 UAC 啟動的安裝器（要系統管理員；結束碼是 <see cref="DeskServiceExit"/>）。
/// <c>session-probe</c>：服務在使用者的工作階段裡開的幫手，印出目前的輸入桌面就結束。
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
                case DeskServiceMode.Run:
                    return RunForeground(command.Pipe!);
                case DeskServiceMode.Install:
                    return (int)Elevated(command.ServiceName, (installer, programFiles) => installer.Install(new DeskInstallOptions(
                        command.ServiceName, AppContext.BaseDirectory, programFiles, SelfVersion.Current, ReleaseKeys.Production, DateTimeOffset.UtcNow)));
                case DeskServiceMode.Uninstall:
                    return (int)Elevated(command.ServiceName, (installer, programFiles) => installer.Uninstall(command.ServiceName, programFiles));
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

    private static int RunForeground(string pipe)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("不知道自己的執行檔在哪裡");
        using var server = new DeskPipeServer(pipe, system: false, SelfVersion.Current, new NativeClientIdentifier(system: false), new HelperSessionProbe(system: false, exe));
        server.Start();
        ServiceLog.Info($"前景模式：管道 {pipe}，{SelfVersion.Current.Release}（標準輸入關閉或按 Enter 結束）");
        Console.In.ReadLine();
        return 0;
    }
}
