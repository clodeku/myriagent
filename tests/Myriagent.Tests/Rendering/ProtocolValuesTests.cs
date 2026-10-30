using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Rendering;

namespace Myriagent.Tests.Rendering;

/// <summary>畫面協定的值對照（Protocol 的 ProtocolValues）：桌面原本的函式改成轉呼叫這裡，結果不變</summary>
public class ProtocolValuesTests
{
    [Fact]
    public void NoticeKind的協定字串是小駝峰_CliRestartRules轉呼叫同一份對照()
    {
        Enum.GetValues<AgentNoticeKind>().Select(ProtocolValues.ToProtocol).Should().Equal("cliUpdate", "appUpdate", "clear", "accountSwitch", "handoff", "fork", "backgroundTask", "backgroundProgram", "overloadRetry", "sideQuestion");
        foreach (var kind in Enum.GetValues<AgentNoticeKind>().Append((AgentNoticeKind)5))
            CliRestartRules.ToProtocol(kind).Should().Be(ProtocolValues.ToProtocol(kind));
        ProtocolValues.ToProtocol((AgentNoticeKind)5).Should().Be("5", "未定義的值照列舉轉成字串（items.json 以數字寫入時）");
    }

    [Fact]
    public void ItemSource的協定字串是小駝峰()
        => Enum.GetValues<AgentItemSource>().Select(ProtocolValues.ToProtocol).Should().Equal("remote");

    [Fact]
    public void ToUnixMs_UTC與本機時間是同一個時刻_轉換不了的值回Null()
    {
        var utc = new DateTime(2026, 7, 15, 1, 0, 0, DateTimeKind.Utc);

        ProtocolValues.ToUnixMs(utc).Should().Be(1_784_077_200_000);
        ProtocolValues.ToUnixMs(utc.ToLocalTime()).Should().Be(1_784_077_200_000);
        ProtocolValues.ToUnixMs(new DateTime(1971, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Should().Be(31_536_000_000);
        ProtocolValues.ToUnixMs(null).Should().BeNull();
        ProtocolValues.ToUnixMs(DateTime.MinValue).Should().BeNull("壞檔裡的 0001-01-01");
        ProtocolValues.ToUnixMs(DateTime.MaxValue).Should().BeNull();
        ProtocolValues.ToUnixMs(new DateTime(1970, 6, 1)).Should().BeNull("1971 年以前視為無效");
    }

    [Fact]
    public void 中樞的ToUnixMs轉呼叫同一個規則()
    {
        var values = new DateTime?[]
        {
            null, DateTime.MinValue, DateTime.MaxValue, new DateTime(1970, 6, 1), DateTime.Now,
            new DateTime(2026, 7, 15, 1, 0, 0, DateTimeKind.Utc), new DateTime(9000, 12, 31, 23, 0, 0, DateTimeKind.Utc),
        };
        foreach (var value in values)
            AgentHub.ToUnixMs(value).Should().Be(ProtocolValues.ToUnixMs(value));
    }
}
