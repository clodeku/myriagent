using System.ComponentModel;
using MyriagentDesk;

namespace Myriagent.Tests.RemoteDesk;

/// <summary>
/// 真的服務控制管理員（<see cref="ScmServiceControl"/>）裡不需要系統管理員的部分：查狀態（一般使用者查得到）、
/// 沒有系統管理員時建立與啟動都被 Windows 拒絕（不會默默成功、也不會當掉）。只查、不改：用一個不存在的暫時名稱與 Windows 內建的事件記錄服務。
/// 真的建立、啟動、停止、移除只有 <c>MYRIAGENT_DESKSVC_IT=1</c>（系統管理員）驗得到
/// </summary>
public sealed class DeskServiceScmTests
{
    private const string Missing = "MyriagentDeskTest-00000000";

    [Fact]
    public void 查狀態_沒裝的是NotInstalled_Windows內建的服務查得到執行中()
    {
        var control = new ScmServiceControl();
        control.Query(Missing).Should().Be(ServiceRunState.NotInstalled);
        control.Query("EventLog").Should().Be(ServiceRunState.Running, "事件記錄服務一定在執行");
    }

    [Fact]
    public void 沒裝的服務_停止與移除不做事_啟動丟例外()
    {
        var control = new ScmServiceControl();
        control.Stop(Missing, TimeSpan.FromSeconds(1));
        control.Delete(Missing);
        FluentActions.Invoking(() => control.Start(Missing, TimeSpan.FromSeconds(1))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void 沒有系統管理員_建立服務被拒()
    {
        if (ProcessIdentity.IsElevated())
            return; // 以系統管理員跑測試時這個情況驗不了（也不能讓它真的建立）
        FluentActions.Invoking(() => new ScmServiceControl().CreateOrUpdate(Missing, "x", "x", "\"C:\\nowhere\\MyriagentDesk.exe\" service"))
            .Should().Throw<Win32Exception>().Which.NativeErrorCode.Should().Be(5);
        new ScmServiceControl().Query(Missing).Should().Be(ServiceRunState.NotInstalled);
    }
}
