using System.ComponentModel;
using System.Diagnostics;
using Myriagent.Remote;
using MyriagentDesk;

namespace Myriagent.Tests.RemoteDesk;

/// <summary>
/// 服務在使用者的工作階段開幫手的那一段（<see cref="SessionProcess"/>）：複製自己的權杖、以它建立程序、把標準輸出接回來、逾時就結束它。
/// 目標是這個測試程序自己的工作階段——權杖不必換工作階段，所以沒有系統權限也跑得了；驗的是建立程序的參數、管線的繼承與讀到結尾、逾時。
/// 「把權杖換到別的工作階段」那一步要系統權限，只有 <c>MYRIAGENT_DESKSVC_IT=1</c> 驗得到
/// </summary>
public sealed class DeskServiceSessionProcessTests
{
    private static string Exe => Path.Combine(AppContext.BaseDirectory, DeskServiceRules.ExeName);

    private static int Session => Process.GetCurrentProcess().SessionId;

    [Fact]
    public void 在自己的工作階段開幫手_收得到它印的那一行()
    {
        var (exit, output) = SessionProcess.Run(Session, Exe, "session-probe", TimeSpan.FromSeconds(30));
        exit.Should().Be(0);
        var probe = DeskServiceRules.ParseHelperProbe(output.Trim(), Session);
        probe.Should().NotBeNull(output);
        (probe!.Desktop is not null || probe.Error != 0).Should().BeTrue();
    }

    [Fact]
    public void 結束碼與輸出照實帶回來()
    {
        var (exit, output) = SessionProcess.Run(Session, Exe, "version", TimeSpan.FromSeconds(30));
        exit.Should().Be(0);
        output.Trim().Should().Be(SelfVersion.Current.Release);

        // 用法錯誤：結束碼 2，說明寫在標準錯誤（和標準輸出接到同一條管線）
        var (bad, text) = SessionProcess.Run(Session, Exe, "bogus", TimeSpan.FromSeconds(30));
        bad.Should().Be((int)DeskServiceExit.Usage);
        text.Should().Contain("MyriagentDesk service");
    }

    [Fact]
    public void 逾時_結束那個程序_丟TimeoutException()
    {
        var ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var watch = Stopwatch.StartNew();
        FluentActions.Invoking(() => SessionProcess.Run(Session, ping, "-n 60 127.0.0.1", TimeSpan.FromMilliseconds(600)))
            .Should().Throw<TimeoutException>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "不會等到那個程序自己結束");
    }

    [Fact]
    public void 執行檔不存在_丟Win32Exception()
        => FluentActions.Invoking(() => SessionProcess.Run(Session, Path.Combine(AppContext.BaseDirectory, "no-such-helper.exe"), "x", TimeSpan.FromSeconds(5)))
            .Should().Throw<Win32Exception>();

    [Fact]
    public void 換到別的工作階段_沒有系統權限做不到_丟Win32Exception()
    {
        if (ProcessIdentity.IsSystem())
            return;
        // 工作階段 0 是服務的：一般權限（連系統管理員也一樣）不能把權杖換過去
        FluentActions.Invoking(() => SessionProcess.Run(Session == 0 ? 1 : 0, Exe, "version", TimeSpan.FromSeconds(5)))
            .Should().Throw<Win32Exception>();
    }
}
