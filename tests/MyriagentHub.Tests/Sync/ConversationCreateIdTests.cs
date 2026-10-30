using Myriagent.Remote;
using Myriagent.Sync;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 瀏覽器建立的新對話的線上 id（WP4.5 計畫步 1；D34、K1、6.4 C1）：<see cref="RemoteConversationIdRules.FromCommandId"/> 把伺服器產生的
/// 指令 id（ULID）的 128 位元依大端轉成 32 個小寫十六進位。WP4.4 在桌面的測試專案另有自己的向量測試（同一份 Pr 檔）
/// </summary>
public sealed class ConversationCreateIdTests
{
    [Theory]
    [InlineData("01J9Q0000000000000000000AA", "01926e0000000000000000000000014a")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAV", "01563e3ab5d3d6764c61efb99302bd5b")]
    [InlineData("7ZZZZZZZZZZZZZZZZZZZZZZZZZ", "ffffffffffffffffffffffffffffffff")]
    [InlineData("00000000000000000000000000", "00000000000000000000000000000000")]
    public void 向量(string commandId, string expected)
    {
        RemoteConversationIdRules.FromCommandId(commandId).Should().Be(expected);
        ConversationIds.IsWireId(expected).Should().BeTrue();
    }

    [Theory]
    [InlineData("01j9q0000000000000000000aa")]
    [InlineData("01J9Q0000000000000000000A")]
    [InlineData("01J9Q0000000000000000000AAA")]
    [InlineData("81J9Q0000000000000000000AA")]
    [InlineData("01J9Q0000000000000000000AI")]
    [InlineData("01J9Q0000000000000000000AL")]
    [InlineData("01J9Q0000000000000000000AO")]
    [InlineData("01J9Q0000000000000000000AU")]
    [InlineData("")]
    [InlineData(null)]
    public void 不是ULID回null(string? commandId) => RemoteConversationIdRules.FromCommandId(commandId).Should().BeNull();

    [Fact]
    public void 所有結果都是線上對話id_不同的cid得到不同的id()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 2000; i++)
        {
            var cid = SyncIds.NewUlid(1_791_000_000_000 + i / 4);
            var id = RemoteConversationIdRules.FromCommandId(cid);
            ConversationIds.IsWireId(id).Should().BeTrue(cid);
            ids.Add(id!).Should().BeTrue("ULID 到 128 位元是雙射，不同的 cid 一定得到不同的 id");
        }
    }

    [Fact]
    public void 時間在前_id的前12個十六進位是毫秒時間()
    {
        const long unixMs = 1_791_580_000_000;
        var id = RemoteConversationIdRules.FromCommandId(SyncIds.NewUlid(unixMs))!;
        Convert.ToInt64(id[..12], 16).Should().Be(unixMs);
    }
}
