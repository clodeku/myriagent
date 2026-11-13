using System.Diagnostics;
using System.Text;
using Myriagent.Remote;
using Myriagent.RemoteDesk;
using MyriagentDesk;

namespace Myriagent.Tests.RemoteDesk;

/// <summary>
/// 真的安裝（手動整合測試，<c>MYRIAGENT_DESKSVC_IT=1</c> 才跑、而且要以系統管理員執行測試）：以暫時的服務名稱
/// <c>MyriagentDeskTest-xxxxxxxx</c> 裝進真的 Program Files、由 Windows 以 LocalSystem 啟動，從這個（使用者工作階段裡的）程序連上管道，
/// 確認服務是系統權限、在這個工作階段開得了幫手、看得到輸入桌面與視窗站裡的桌面；最後一定移除。絕不碰真的 <c>MyriagentDesk</c> 服務。
/// 這是唯一會驗到「工作階段 0 的服務在使用者的工作階段開系統權限的程序」那一段的測試——其他測試都沒有系統權限
/// </summary>
public sealed class DeskServiceSystemIntegrationTests
{
    private static string Exe => Path.Combine(AppContext.BaseDirectory, DeskServiceRules.ExeName);

    [Fact]
    public void 安裝暫時的服務_系統權限_看得到這個工作階段的輸入桌面_移除()
    {
        if (Environment.GetEnvironmentVariable("MYRIAGENT_DESKSVC_IT") != "1")
            return;
        ProcessIdentity.IsElevated().Should().BeTrue("MYRIAGENT_DESKSVC_IT=1 要以系統管理員執行測試（安裝服務要系統管理員）");
        Process.GetCurrentProcess().SessionId.Should().NotBe(0, "要在使用者的工作階段裡跑（ssh 的工作階段是 0，服務不收）");

        var name = DeskServiceRules.TestServicePrefix + Guid.NewGuid().ToString("N")[..8];
        var root = DeskServiceRules.InstallRoot(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), name);
        try
        {
            Run("install", "--name", name).Should().Be(0, "安裝器的記錄在 " + Path.Combine(root, "logs"));
            var install = DeskServiceRules.ParseInstall(File.ReadAllText(Path.Combine(root, DeskServiceRules.InstallFile)))!;
            install.Service.Should().Be(name);
            Directory.GetFiles(Path.Combine(root, install.Folder)).Select(Path.GetFileName).Should().BeEquivalentTo(DeskServiceRules.PayloadFiles);

            using var client = DeskServiceClient.Connect(DeskServiceRules.PipeNameOf(name), TimeSpan.FromSeconds(10));
            var hello = client.Hello();
            hello.System.Should().BeTrue("服務由 Windows 以 LocalSystem 啟動");
            hello.Session.Should().Be(Process.GetCurrentProcess().SessionId);

            var probe = client.Probe();
            probe.Session.Should().Be(hello.Session);
            probe.Error.Should().Be(0, "系統權限的幫手開得了輸入桌面");
            probe.Desktop.Should().NotBeNullOrEmpty();
            probe.Desktops.Should().Contain("Winlogon", "系統權限列得出安全桌面（一般權限只列得出 Default）");
        }
        finally
        {
            Run("uninstall", "--name", name).Should().Be(0);
            Directory.Exists(root).Should().BeFalse("移除之後不留檔案");
        }
    }

    private static int Run(params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("MyriagentDesk.exe 沒有結束");
        }
        return process.ExitCode;
    }
}
