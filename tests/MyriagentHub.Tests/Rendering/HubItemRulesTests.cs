using System.Globalization;
using Myriagent.Agents;
using Myriagent.Rendering;
using Myriagent.Sync;
using MyriagentHub.Rendering;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Rendering;

/// <summary>伺服器渲染的純函式規則（WP4.2 計畫步 7、D18、D20）</summary>
public sealed class HubItemRulesTests
{
    private const long Received = 1_784_080_000_000;

    [Theory]
    [InlineData(1_784_080_000_123L)]
    [InlineData(1_793_500_200_000L)] // 2026-11-01 前後：美國夏令時間結束、本機時間重複的那一小時（以 UTC 轉就沒有歧義）
    [InlineData(1_774_746_000_999L)] // 2026-03-29 歐洲夏令時間開始
    [InlineData(31_536_000_000L)] // 1971-01-01
    public void ToChatItem_時間以UTC往返逐毫秒相同(long ms)
    {
        var chat = HubItemRules.ToChatItem(Item(createdAt: ms), Received)!;

        ProtocolValues.ToUnixMs(chat.CreatedAt).Should().Be(ms);
        chat.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void ToChatItem_createdAt是null_用收到的時間_轉換不了的是MinValue()
    {
        ProtocolValues.ToUnixMs(HubItemRules.ToChatItem(Item(createdAt: null), Received)!.CreatedAt).Should().Be(Received);
        HubItemRules.ToChatItem(Item(createdAt: long.MaxValue), Received)!.CreatedAt.Should().Be(DateTime.MinValue);
        ProtocolValues.ToUnixMs(HubItemRules.ToChatItem(Item(createdAt: long.MaxValue), Received)!.CreatedAt).Should().BeNull();
        HubItemRules.ToTime(-1).Should().Be(DateTime.UnixEpoch.AddMilliseconds(-1));
    }

    [Theory]
    [InlineData("Foo", "Done")]
    [InlineData("user", "Done")]
    [InlineData("5", "Done")]
    [InlineData("Assistant", "done")]
    [InlineData("Assistant", "2")]
    public void ToChatItem_種類或狀態不是列舉的名稱_回null(string kind, string status)
        => HubItemRules.ToChatItem(Item() with { Kind = kind, Status = status }, Received).Should().BeNull();

    [Fact]
    public void ToChatItem_附件媒體清空_Seq等於Ord_RequestKey是null()
    {
        var wire = Item() with
        {
            Ord = 321,
            Attachments = [new WireAttachment("a1", "a.png", "Image", 10, null, null, null)],
            Media = [new WireMedia(null, "image", "b.png")],
            RequestDigest = "rd1:x",
            Risk = "High",
            NoticeKind = "CliUpdate",
        };

        var chat = HubItemRules.ToChatItem(wire, Received)!;

        chat.Attachments.Should().BeEmpty();
        chat.MediaPaths.Should().BeEmpty();
        chat.Seq.Should().Be(321);
        chat.Ord.Should().Be(321);
        chat.RequestKey.Should().BeNull();
        chat.RequestDigest.Should().Be("rd1:x");
        chat.Risk.Should().Be(AgentRemoteRisk.High);
        chat.NoticeKind.Should().Be(AgentNoticeKind.CliUpdate);
    }

    [Theory]
    [InlineData("Extreme")]
    [InlineData("high")]
    [InlineData("1")]
    public void ToChatItem_不認得的risk與noticeKind_當成沒有(string value)
    {
        var chat = HubItemRules.ToChatItem(Item() with { Risk = value, NoticeKind = value }, Received)!;

        chat.Risk.Should().BeNull();
        chat.NoticeKind.Should().BeNull();
    }

    [Fact]
    public void ToChatItem_答案轉回字典_重複的id取第一個()
    {
        var chat = HubItemRules.ToChatItem(Item() with
        {
            Answers = [new WireAnswer("q1", ["甲"]), new WireAnswer("q2", []), new WireAnswer("q1", ["乙"])],
        }, Received)!;

        chat.Answers.Keys.Should().Equal("q1", "q2");
        chat.Answers["q1"].Should().Equal("甲");
        chat.Answers["q2"].Should().BeEmpty();
    }

    [Fact]
    public void ToChatItem_空的欄位是空字串或空清單()
    {
        var chat = HubItemRules.ToChatItem(Item(), Received)!;

        chat.Text.Should().BeEmpty();
        chat.Questions.Should().BeEmpty();
        chat.PlanSteps.Should().BeEmpty();
        chat.Diff.Should().BeNull();
        chat.CanAllowForSession.Should().BeTrue();
    }

    [Fact]
    public void FormatSize_zhTW下與桌面逐字相同_其他文化特性也不變()
    {
        long[] sizes = [0, 1, 1023, 1024, 1536, 186_368, 1_048_575, 1_048_576, 1_258_291, 5_505_024, 1_073_741_823, 2L * 1024 * 1024 * 1024, 3_489_660_928, long.MaxValue];
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-TW");
            foreach (var size in sizes)
                HubItemRules.FormatSize(size).Should().Be(AgentAttachmentRules.FormatSize(size), size.ToString(CultureInfo.InvariantCulture));

            var zh = sizes.Select(HubItemRules.FormatSize).ToList();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            sizes.Select(HubItemRules.FormatSize).Should().Equal(zh);
            HubItemRules.FormatSize(1536).Should().Be("1.5 KB");
            AgentAttachmentRules.FormatSize(1536).Should().Be("1,5 KB", "對照：桌面的寫法跟著文化特性");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Shrink_只留尾段_前綴累計省略的字數_切點那一行換掉()
    {
        var text = string.Concat(Enumerable.Range(0, 100).Select(i => $"{i:000}\n"));

        var shrunk = HubItemRules.Shrink(Item() with { Text = text }, 10).Text!;

        shrunk.Should().Be($"…（前 390 字省略）\n{CredentialLeakRules.Replacement}\n098\n099\n");
        var again = HubItemRules.Shrink(Item() with { Text = "…（前 1,234 字省略）\n" + text }, 10).Text!;
        again.Should().StartWith("…（前 1,624 字省略）\n");
    }

    [Fact]
    public void Shrink_三欄各自只留尾段_其他欄位與沒超過的不動()
    {
        var wire = Item() with { Text = "短", Output = new string('o', 50) + "\n輸出尾", Detail = new string('d', 50) + "\n說明尾", Title = new string('t', 100) };

        var shrunk = HubItemRules.Shrink(wire, 20);

        shrunk.Text.Should().Be("短");
        shrunk.Output.Should().Be($"…（前 34 字省略）\n{CredentialLeakRules.Replacement}\n輸出尾");
        shrunk.Detail.Should().Be($"…（前 34 字省略）\n{CredentialLeakRules.Replacement}\n說明尾");
        shrunk.Title.Should().Be(wire.Title);
        HubItemRules.Shrink(Item(), 20).Should().Be(Item());
    }

    [Fact]
    public void Shrink_結果沒有落單的代理字元()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        foreach (var keep in Enumerable.Range(1, 12))
        {
            var text = "前\n" + string.Concat(Enumerable.Repeat(emoji, 10)) + "\n" + string.Concat(Enumerable.Repeat(emoji, 3));

            var shrunk = HubItemRules.Shrink(Item() with { Text = text }, keep).Text!;

            for (var i = 0; i < shrunk.Length; i++)
            {
                if (char.IsLowSurrogate(shrunk[i]))
                    (i > 0 && char.IsHighSurrogate(shrunk[i - 1])).Should().BeTrue($"留 {keep} 字");
                if (char.IsHighSurrogate(shrunk[i]))
                    (i + 1 < shrunk.Length && char.IsLowSurrogate(shrunk[i + 1])).Should().BeTrue($"留 {keep} 字");
            }
        }
    }

    [Theory]
    [InlineData(256 * 1024, 16384)]
    [InlineData(16, 1)]
    [InlineData(1, 1)]
    public void ShrinkChars依上限(int maxBytes, int expected) => HubItemRules.ShrinkChars(maxBytes).Should().Be(expected);

    [Fact]
    public void ShouldShrinkFirst_三欄加起來的字數超過上限()
    {
        HubItemRules.ShouldShrinkFirst(Item() with { Text = new string('a', 50), Output = new string('b', 50) }, 100).Should().BeFalse();
        HubItemRules.ShouldShrinkFirst(Item() with { Text = new string('a', 50), Output = new string('b', 50), Detail = "c" }, 100).Should().BeTrue();
        HubItemRules.ShouldShrinkFirst(Item() with { Title = new string('t', 500) }, 100).Should().BeFalse("標題不會被縮短，不算");
    }

    [Theory]
    [InlineData("Assistant", "Running", "Assistant", "Running")]
    [InlineData("User", "Done", "User", "Done")]
    [InlineData("Approval", "Waiting", "Notice", "Waiting")]
    [InlineData("Tool", "Failed", "Notice", "Failed")]
    [InlineData("Plan", "Done", "Notice", "Done")]
    [InlineData(null, null, "Notice", "Done")]
    [InlineData("Unknown", "Weird", "Notice", "Done")]
    public void Placeholder_文字類照原本的種類_其他是Notice(string? kind, string? status, string expectedKind, string expectedStatus)
    {
        var placeholder = HubItemRules.Placeholder("i1", kind, status, 42, 1_784_080_000_000, HubItemRules.TooLargeMessage);

        placeholder.Properties().Select(p => p.Name).Should().Equal("id", "kind", "status", "html", "time", "seq");
        placeholder["kind"]!.Value<string>().Should().Be(expectedKind);
        placeholder["status"]!.Value<string>().Should().Be(expectedStatus);
        placeholder["html"]!.Value<string>().Should().Be("內容太大，暫時無法在瀏覽器顯示");
        placeholder["time"]!.Value<long>().Should().Be(1_784_080_000_000);
        placeholder["seq"]!.Value<long>().Should().Be(42);
        HubItemRules.Placeholder("i1", kind, status, 42, null, "x")["time"]!.Type.Should().Be(JTokenType.Null);
        HubItemRules.Placeholder("i1", kind, status, 42, long.MaxValue, "x")["time"]!.Type.Should().Be(JTokenType.Null);
    }

    private static WireItem Item(long? createdAt = 1_784_080_000_000) => new(
        Id: "item1",
        Ord: 7,
        Ver: 1,
        Kind: "Assistant",
        Status: "Done",
        CreatedAt: createdAt,
        UpdatedAt: createdAt);
}
