using Myriagent.UI;
using Myriagent.Update;

namespace Myriagent.Tests.UI;

/// <summary>開機時自動啟動的規則：誰能設定、Run 的值、工作管理員的停用、開機後主視窗藏不藏</summary>
public sealed class AutoStartRulesTests
{
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]   // 開發版、解壓縮後直接執行
    [InlineData(true, false, false, false)]   // --data 的探針
    [InlineData(true, true, true, false)]     // update-e2e 的暫存安裝
    public void 只有受管理的安裝_預設資料夾_非端到端能設定(bool managed, bool usesDefaultData, bool e2e, bool expected)
        => AutoStartRules.IsAvailable(managed, usesDefaultData, e2e).Should().Be(expected);

    [Fact]
    public void Run的值_路徑加引號並帶開機參數_啟動參數認得它()
    {
        var exe = @"C:\Users\Lin\AppData\Local\Programs\Myriagent\current\Myriagent.exe";
        var command = AutoStartRules.Command(exe);
        command.Should().Be($"\"{exe}\" --autostart");
        StartupOptions.Parse(new[] { AutoStartRules.Argument }).AutoStart.Should().BeTrue();
        new InstallLayout(@"C:\x\Programs\Myriagent").CurrentExecutable.Should().EndWith(@"\current\Myriagent.exe", "指向 current，換版只改 junction");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[0], true)]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 0x5A, 0x1B, 0x2C, 0x3D, 0x4E, 0x5F, 0xD9, 0x01 }, false)]
    [InlineData(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    public void 工作管理員的開關_第一個位元組奇數是停用_沒動過是啟用(byte[]? approved, bool expected)
        => AutoStartRules.IsApproved(approved).Should().Be(expected);

    [Theory]
    [InlineData("\"C:\\x\\Myriagent.exe\" --autostart", null, true)]
    [InlineData("\"C:\\x\\Myriagent.exe\" --autostart", new byte[] { 0x03, 0, 0, 0 }, false)]
    [InlineData(null, null, false)]
    [InlineData("", null, false)]
    [InlineData(null, new byte[] { 0x02, 0, 0, 0 }, false)]   // 只剩工作管理員的紀錄：Run 沒有值就不會啟動
    public void 會啟動_Run有值而且沒被停用(string? run, byte[]? approved, bool expected)
        => AutoStartRules.IsEnabled(run, approved).Should().Be(expected);

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]    // 不縮到系統匣的人：開啟視窗
    [InlineData(false, true, false)]    // 自己開的：照常顯示
    [InlineData(false, false, false)]
    public void 開機啟動且縮到系統匣時才藏主視窗(bool autoStart, bool closeToTray, bool expected)
        => AutoStartRules.StartHidden(autoStart, closeToTray).Should().Be(expected);

    [Fact]
    public void 自動啟動後鎖定_只在這次是autostart而且設定開著時_自動登入只認1()
    {
        AutoStartRules.LockAfterStart(autoStart: true, autoStartLock: true).Should().BeTrue();
        AutoStartRules.LockAfterStart(autoStart: false, autoStartLock: true).Should().BeFalse("換版重開、手動開不鎖");
        AutoStartRules.LockAfterStart(autoStart: true, autoStartLock: false).Should().BeFalse();
        AutoStartRules.AutoLogon("1").Should().BeTrue();
        AutoStartRules.AutoLogon(" 1 ").Should().BeTrue();
        AutoStartRules.AutoLogon("0").Should().BeFalse();
        AutoStartRules.AutoLogon("").Should().BeFalse();
        AutoStartRules.AutoLogon(null).Should().BeNull();
    }
}
