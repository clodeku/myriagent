using Myriagent.Remote;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Remote;

/// <summary>電腦選單的喇叭音量與螢幕（DeviceControlRules；2026-11-17）：參數檢查、結果的形狀與伺服器的投影</summary>
public sealed class DeviceControlRulesTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("100", 100)]
    [InlineData("45", 45)]
    [InlineData("101", null)]
    [InlineData("-1", null)]
    [InlineData(" 5", null)]
    [InlineData("4.5", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void 音量的字串(string? text, int? expected) => DeviceControlRules.ParseVolume(text).Should().Be(expected);

    [Fact]
    public void 參數檢查()
    {
        DeviceControlRules.Problem("audioGet", null, null).Should().BeNull();
        DeviceControlRules.Problem("screenOff", null, null).Should().BeNull();
        DeviceControlRules.Problem("audioSet", "30", null).Should().BeNull();
        DeviceControlRules.Problem("audioSet", null, true).Should().BeNull();
        DeviceControlRules.Problem("audioSet", null, null).Should().Contain("音量或靜音");
        DeviceControlRules.Problem("audioSet", "300", null).Should().Contain("0～100");
        DeviceControlRules.Problem("reboot", null, null).Should().Contain("不認得");
    }

    [Fact]
    public void 投影只留已知的鍵_形狀不對丟掉()
    {
        var ok = DeviceControlRules.State("dc-1", "audioSet", 35, false, null);
        ok["extra"] = "x";
        DeviceControlRules.Project(ok)!.ToString(Newtonsoft.Json.Formatting.None)
            .Should().Be("""{"type":"deviceControlState","requestId":"dc-1","action":"audioSet","volume":35,"muted":false,"error":null}""");
        DeviceControlRules.Project(DeviceControlRules.State(null, "screenOff", null, null, "關不了"))!.Value<string>("error").Should().Be("關不了");

        JObject Bad(string key, JToken value)
        {
            var m = DeviceControlRules.State("dc-1", "audioGet", 1, true, null);
            m[key] = value;
            return m;
        }

        DeviceControlRules.Project(Bad("volume", 101)).Should().BeNull();
        DeviceControlRules.Project(Bad("volume", "5")).Should().BeNull();
        DeviceControlRules.Project(Bad("muted", "yes")).Should().BeNull();
        DeviceControlRules.Project(Bad("action", "reboot")).Should().BeNull();
        DeviceControlRules.Project(Bad("requestId", 5)).Should().BeNull();
        DeviceControlRules.Project(Bad("error", new string('x', 301))).Should().BeNull();
    }
}
