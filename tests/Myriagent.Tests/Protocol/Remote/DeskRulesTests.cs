using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Remote;

/// <summary>遠端桌面的純規則（Protocol 的 Remote/DeskRules.cs；docs/sync-protocol.md 14.16）</summary>
public sealed class DeskRulesTests
{
    private const string Id = "01JDESKRULES00000000000000";

    private static JObject Events(params JObject[] events) => new() { ["events"] = new JArray(events) };

    [Fact]
    public void 封包的方向與格式()
    {
        DeskRules.Check(new DeskPacket(Id, DeskRules.Frame, 1, new JObject()), fromTarget: true).Should().BeNull();
        DeskRules.Check(new DeskPacket(Id, DeskRules.Frame, 1, new JObject()), fromTarget: false).Should().NotBeNull("看的一方不能送影格");
        DeskRules.Check(new DeskPacket(Id, DeskRules.Input, 1, Events(new JObject { ["t"] = "mm", ["x"] = 0.5, ["y"] = 0.5 })), fromTarget: true)
            .Should().NotBeNull("被看的電腦不能送輸入");
        DeskRules.Check(new DeskPacket("short", DeskRules.Ack, 1, new JObject { ["seq"] = 1 }), fromTarget: false).Should().NotBeNull("id 至少 8 個字");
        DeskRules.Check(new DeskPacket(Id, DeskRules.Ack, -1, new JObject { ["seq"] = 1 }), fromTarget: false).Should().NotBeNull();
        DeskRules.Check(new DeskPacket(Id, DeskRules.Ack, 1, new JObject()), fromTarget: false).Should().NotBeNull("ack 要有 seq");
        DeskRules.Check(new DeskPacket(Id, DeskRules.Frame, 1, new JObject { ["d"] = new string('a', DeskRules.MaxPacketBytes) }), fromTarget: true)
            .Should().Be("封包太大");
        DeskRules.Check(null, fromTarget: true).Should().NotBeNull();
    }

    [Fact]
    public void 輸入事件的格式_確認可以併在輸入裡()
    {
        var parsed = DeskRules.ParseInput(Events(
            new JObject { ["t"] = "mm", ["x"] = 0, ["y"] = 1 },
            new JObject { ["t"] = "md", ["x"] = 0.25, ["y"] = 0.75, ["b"] = 2 },
            new JObject { ["t"] = "wh", ["dy"] = -240 },
            new JObject { ["t"] = "kd", ["k"] = 0x41 },
            new JObject { ["t"] = "tx", ["s"] = "你好\n" }))!;
        parsed.Select(e => e.Type).Should().Equal("mm", "md", "wh", "kd", "tx");
        parsed[1].Button.Should().Be(2);
        parsed[2].Dy.Should().Be(-240);
        parsed[4].Text.Should().Be("你好\n");

        DeskRules.ParseInput(new JObject { ["ack"] = 7 }).Should().BeEmpty("只有確認");
        DeskRules.AckOf(new JObject { ["ack"] = 7 }).Should().Be(7);
        DeskRules.AckOf(new JObject { ["seq"] = 3 }).Should().Be(3);
        DeskRules.ParseInput(new JObject()).Should().BeNull("事件與確認都沒有");
        DeskRules.ParseInput(Events(new JObject { ["t"] = "mm", ["x"] = 1.5, ["y"] = 0 })).Should().BeNull("座標超出 0～1");
        DeskRules.ParseInput(Events(new JObject { ["t"] = "md", ["x"] = 0, ["y"] = 0, ["b"] = 3 })).Should().BeNull();
        DeskRules.ParseInput(Events(new JObject { ["t"] = "kd", ["k"] = 0 })).Should().BeNull();
        DeskRules.ParseInput(Events(new JObject { ["t"] = "tx", ["s"] = "a\u0001" })).Should().BeNull("控制字元");
        DeskRules.ParseInput(Events(new JObject { ["t"] = "xx" })).Should().BeNull();
        DeskRules.ParseInput(new JObject { ["events"] = new JArray(Enumerable.Range(0, DeskRules.MaxEvents + 1).Select(_ => new JObject { ["t"] = "kd", ["k"] = 65 })) })
            .Should().BeNull("太多事件");
    }

    [Fact]
    public void 設定()
    {
        DeskRules.ParseConfig(new JObject { ["quality"] = "sharp", ["screen"] = 1, ["maxWidth"] = 1280, ["key"] = true })
            .Should().Be(new DeskConfig("sharp", 1, true, 1280));
        DeskRules.ParseConfig(new JObject()).Should().Be(new DeskConfig(null, null, false));
        DeskRules.ParseConfig(new JObject { ["quality"] = "ultra" }).Should().BeNull();
        DeskRules.ParseConfig(new JObject { ["maxWidth"] = 100 }).Should().BeNull();
        DeskRules.ParseConfig(new JObject { ["maxWidth"] = 3840 })!.MaxWidth.Should().Be(3840, "放大看細節時可以要求到 3840");
        DeskRules.ParseConfig(new JObject { ["maxWidth"] = 3841 }).Should().BeNull();
        DeskRules.ParseConfig(new JObject { ["screen"] = "1" }).Should().BeNull();
    }

    [Fact]
    public void 比例座標換成虛擬桌面的絕對座標()
    {
        var virtualScreen = new DeskRect(-1920, 0, 3840, 1080);
        DeskRules.ToAbsolute(0, 0, new DeskRect(-1920, 0, 1920, 1080), virtualScreen).Should().Be((0, 0));
        DeskRules.ToAbsolute(1, 1, new DeskRect(0, 0, 1920, 1080), virtualScreen).Should().Be((65535, 65535));
        var (x, _) = DeskRules.ToAbsolute(0, 0.5, new DeskRect(0, 0, 1920, 1080), virtualScreen);
        x.Should().BeCloseTo(32776, 2, "第二個螢幕的左邊在虛擬桌面的正中間");
        DeskRules.ToAbsolute(5, -1, new DeskRect(0, 0, 100, 100), new DeskRect(0, 0, 100, 100)).Should().Be((65535, 0), "超出的夾回");
    }

    [Fact]
    public void 傳送大小與GPU縮小的層級()
    {
        DeskRules.SendSize(3840, 2160).Should().Be((1920, 1080));
        DeskRules.SendSize(1920, 1080).Should().Be((1920, 1080));
        DeskRules.SendSize(3840, 2160, 1280).Should().Be((1280, 720));
        DeskRules.SendSize(1366, 768).Should().Be((1366, 768));
        DeskRules.MipLevel(3840, null).Should().Be(1);
        DeskRules.MipLevel(3072, null).Should().Be(1);
        DeskRules.MipLevel(1920, null).Should().Be(0);
        DeskRules.MipLevel(2560, null).Should().Be(1);
        DeskRules.MipLevel(3072, 1280).Should().Be(1, "1536 ≤ 1280×1.25");
        DeskRules.MipLevel(3840, 1280).Should().Be(2);
        DeskRules.MipSize(3840, 2160, 2).Should().Be((960, 540));
        DeskRules.SendSize(3840, 2160, 3840).Should().Be((3840, 2160), "放大時要求原尺寸");
        DeskRules.SendSize(3840, 2160, 2560).Should().Be((2560, 1440));
        DeskRules.MipLevel(3840, 3840).Should().Be(0);
        DeskRules.MipLevel(3072, 2560).Should().Be(0);
        DeskRules.QualityFor("auto", 50, 3840).Should().Be(55);
        DeskRules.QualityFor("auto", 900, 3840).Should().Be(35);
        DeskRules.QualityFor("auto", 50, 1920).Should().Be(70);
    }

    [Fact]
    public void 變動的格子合併成矩形_太高的切段()
    {
        // 3×3：第一列全變、第二列中間、第三列全變
        bool[] changed = [true, true, true, false, true, false, true, true, true];
        DeskRules.MergeTiles(changed, 3, 3).Should().Equal(new DeskRect(0, 0, 3, 1), new DeskRect(1, 1, 1, 1), new DeskRect(0, 2, 3, 1));
        // 整個畫面：一個矩形
        DeskRules.MergeTiles(Enumerable.Repeat(true, 4 * 10).ToArray(), 4, 10).Should().Equal(new DeskRect(0, 0, 4, 10));
        DeskRules.SplitTall(new DeskRect(0, 0, 4, 10)).Should().Equal(new DeskRect(0, 0, 4, 4), new DeskRect(0, 4, 4, 4), new DeskRect(0, 8, 4, 2));
        DeskRules.ToPixels(new DeskRect(1, 1, 2, 2), 150, 100).Should().Be(new DeskRect(64, 64, 86, 36), "裁到畫面大小");
    }

    [Fact]
    public void 拆包_流量控制_畫質與節奏()
    {
        DeskRules.SplitBySize([100, 200, 300, 50], 350).Should().BeEquivalentTo(new List<List<int>> { new() { 0, 1 }, new() { 2, 3 } }, o => o.WithStrictOrdering());
        DeskRules.SplitBySize([900], 350).Should().ContainSingle().Which.Should().Equal(0);
        DeskRules.CanSend(1, 1000).Should().BeTrue();
        DeskRules.CanSend(2, 0).Should().BeFalse();
        DeskRules.CanSend(0, DeskRules.MaxInflightBytes).Should().BeFalse();
        DeskRules.Quality("auto", 50).Should().Be(70);
        DeskRules.Quality("auto", 300).Should().Be(55);
        DeskRules.Quality("auto", 900).Should().Be(40);
        DeskRules.Quality("sharp", 900).Should().Be(80);
        DeskRules.IntervalMs("smooth", 900).Should().Be(66);
        DeskRules.IntervalMs("auto", 50).Should().Be(100);
        DeskRules.SmoothRtt(0, 120).Should().Be(120);
        DeskRules.SmoothRtt(100, 200).Should().Be(125);
        DeskRules.EncodedSize(3).Should().Be(4 + 64);
    }
}
