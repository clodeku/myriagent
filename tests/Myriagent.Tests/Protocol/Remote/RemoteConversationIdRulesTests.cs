using Myriagent.Remote;
using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Remote;

/// <summary>
/// 瀏覽器建立的新對話的線上 id 由指令 id（cid）推得（WP4.4 計畫 D37；與 WP4.5 共用同一個檔）：ULID 的 128 位元依大端轉成
/// 32 個小寫十六進位。固定向量與 WP4.5 的 <c>ConversationCreateIdTests</c> 是同一張表——兩包算出的 id 不同，伺服器的草稿就對不到電腦建立的對話
/// </summary>
public sealed class RemoteConversationIdRulesTests
{
    public static TheoryData<string, string> Vectors() => new()
    {
        { "01J9Q0000000000000000000AA", "01926e0000000000000000000000014a" },
        { "01ARZ3NDEKTSV4RRFFQ69G5FAV", "01563e3ab5d3d6764c61efb99302bd5b" }, // ULID 規格範例的 UUID 形式
        { "7ZZZZZZZZZZZZZZZZZZZZZZZZZ", "ffffffffffffffffffffffffffffffff" },
        { "00000000000000000000000000", "00000000000000000000000000000000" },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void 固定向量(string commandId, string expected)
    {
        var id = RemoteConversationIdRules.FromCommandId(commandId);

        id.Should().Be(expected);
        ConversationIds.IsWireId(id).Should().BeTrue("推得的 id 要是合格的線上對話 id");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01J9Q0000000000000000000A")] // 25 字
    [InlineData("01J9Q0000000000000000000AAA")] // 27 字
    [InlineData("81J9Q0000000000000000000AA")] // 第一個字大於 7（超過 128 位元）
    [InlineData("01J9Q0000000000000000000AI")]
    [InlineData("01J9Q0000000000000000000AL")]
    [InlineData("01J9Q0000000000000000000AO")]
    [InlineData("01J9Q0000000000000000000AU")]
    [InlineData("01j9q0000000000000000000aa")] // 小寫
    public void 不是ULID回null(string? commandId)
    {
        RemoteConversationIdRules.FromCommandId(commandId).Should().BeNull();
    }

    [Fact]
    public void 一千個隨機cid不重複()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var cids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 1000; i++)
        {
            var cid = SyncIds.NewUlid(1_791_580_000_000 + i / 10);
            if (!cids.Add(cid))
                continue;
            var id = RemoteConversationIdRules.FromCommandId(cid);
            ConversationIds.IsWireId(id).Should().BeTrue();
            ids.Add(id!).Should().BeTrue("不同的 cid 一定得到不同的 id（雙射）");
        }

        ids.Should().HaveCount(cids.Count).And.HaveCountGreaterThan(990);
    }
}
