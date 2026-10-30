using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>視窗外框跟著這個視窗裡的對話（WindowBorderRules）：等你回應琥珀色優先、執行中帳戶色、都沒有回到主題的灰</summary>
public sealed class WindowBorderRulesTests
{
    private static WindowBorderConversation C(bool running = false, bool waiting = false, bool visible = false, string? color = null)
        => new(running, waiting, visible, color);

    [Fact]
    public void 沒有對話或都閒置_回到主題()
    {
        WindowBorderRules.Decide([]).Should().Be(WindowBorder.None);
        WindowBorderRules.Decide([C(visible: true, color: "#123456"), C()]).Should().Be(WindowBorder.None);
    }

    [Fact]
    public void 有等你回應的_琥珀色優先_不管其他對話在執行()
    {
        WindowBorderRules.Decide([C(running: true, visible: true, color: "#19C3A6"), C(waiting: true)])
            .Should().Be(new WindowBorder(WindowBorderState.Waiting, null));
    }

    [Fact]
    public void 執行中取帳戶色_看得到的分頁優先_否則照開的順序取第一個()
    {
        WindowBorderRules.Decide([C(running: true, color: "#111111"), C(running: true, visible: true, color: "#19c3a6")])
            .Should().Be(new WindowBorder(WindowBorderState.Running, "#19C3A6"), "看得到的分頁優先，顏色寫成大寫");
        WindowBorderRules.Decide([C(running: true, color: "#111111"), C(running: true, color: "#222222")])
            .Should().Be(new WindowBorder(WindowBorderState.Running, "#111111"));
    }

    [Theory]
    [InlineData("#d97757", "#D97757")]
    [InlineData("#ABCDEF", "#ABCDEF")]
    [InlineData("red", null)]
    [InlineData("#12345", null)]
    [InlineData("#12345G", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void 帳戶色只認井號加六位十六進位(string? color, string? want)
        => WindowBorderRules.NormalizeColor(color).Should().Be(want);

    [Fact]
    public void 顏色_主題的灰同標題列_琥珀色深淺各一_執行中是帳戶色_帳戶色不合格用預設()
    {
        WindowBorderRules.Rgb(WindowBorder.None, dark: true).Should().Be(((byte)0x2A, (byte)0x2A, (byte)0x30));
        WindowBorderRules.Rgb(WindowBorder.None, dark: false).Should().Be(((byte)0xDD, (byte)0xDD, (byte)0xE2));
        WindowBorderRules.Rgb(new WindowBorder(WindowBorderState.Waiting, null), dark: true).Should().Be(((byte)0xE2, (byte)0xB3, (byte)0x40));
        WindowBorderRules.Rgb(new WindowBorder(WindowBorderState.Waiting, null), dark: false).Should().Be(((byte)0xC0, (byte)0x8A, (byte)0x12));
        WindowBorderRules.Rgb(new WindowBorder(WindowBorderState.Running, "#19C3A6"), dark: true).Should().Be(((byte)0x19, (byte)0xC3, (byte)0xA6));
        WindowBorderRules.Rgb(new WindowBorder(WindowBorderState.Running, "oops"), dark: false).Should().Be(((byte)0xD9, (byte)0x77, (byte)0x57));
    }
}
