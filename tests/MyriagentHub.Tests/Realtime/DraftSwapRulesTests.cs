using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Realtime;

/// <summary>伺服器草稿換成正式對話的純函式規則（WP4.6 計畫步 7；D17、D41）</summary>
public sealed class DraftSwapRulesTests
{
    private static readonly string C1 = Id(1);

    private static readonly string C2 = Id(2);

    private static readonly string C3 = Id(3);

    private const string Draft = "d01J9Q0000000000000000000AA";

    [Fact]
    public void CreatedIds_只取Meta與對話id形狀_去重保序()
    {
        var drained = new List<ConversationChange>
        {
            Change(C2, ConversationChangeKind.Turn),
            Change(C2, ConversationChangeKind.Meta),
            Change(C1, ConversationChangeKind.Item),
            Change(C3, ConversationChangeKind.Meta),
            Change(C2, ConversationChangeKind.Meta),
            Change(Draft, ConversationChangeKind.Meta),
            Change("C1-not-wire", ConversationChangeKind.Meta),
            Change(C1, ConversationChangeKind.Meta),
            Change(C1, ConversationChangeKind.Attention),
            Change(C3, ConversationChangeKind.Deleted),
        };
        DraftSwapRules.CreatedIds(drained).Should().Equal(C2, C3, C1);
        DraftSwapRules.CreatedIds([]).Should().BeEmpty();
        DraftSwapRules.CreatedIds([Change(C1, ConversationChangeKind.Turn), Change(C2, ConversationChangeKind.Item), Change(C3, ConversationChangeKind.ItemRemoved),
            Change(C1, ConversationChangeKind.Purged), Change(C2, ConversationChangeKind.Rebuilt), Change(C3, ConversationChangeKind.Queue)]).Should().BeEmpty("只有中繼資料的變更才是建立");
        DraftSwapRules.CreatedIds([Change(Id(0xabc).ToUpperInvariant(), ConversationChangeKind.Meta)]).Should().BeEmpty("大寫不是線上的對話 id");
        DraftSwapRules.CreatedIds([Change(Id(0xabc), ConversationChangeKind.Meta)]).Should().Equal(Id(0xabc));
    }

    [Fact]
    public void ClosedReplaced_鍵與值逐一相同_合併鍵是null()
    {
        var message = DraftSwapRules.ClosedReplaced(Draft, C1);
        message.Type.Should().Be("conversationClosed");
        message.Key.Should().BeNull("同 ClosedMessage：不合併");
        message.ConversationId.Should().Be(Draft, "訊息屬於草稿");
        ClientOutboxRules.MergeKey(message).Should().BeNull();
        ClientOutboxRules.KeptAcrossInit(message.Type).Should().BeFalse("同 conversationClosed（風險 R16）");
        message.Json.Should().Be("{\"type\":\"conversationClosed\",\"conversationId\":\"" + Draft + "\",\"replacedBy\":\"" + C1 + "\"}");
        var json = JObject.Parse(message.Json);
        json.Properties().Select(p => p.Name).Should().Equal("type", "conversationId", "replacedBy");

        // 沒有 replacedBy 的 conversationClosed 照舊：和 ClosedMessage 只差 replacedBy
        var plain = JObject.Parse(ConversationGatewayRules.ClosedMessage(Draft).Json);
        json.Remove(DraftSwapRules.ReplacedByKey);
        JToken.DeepEquals(json, plain).Should().BeTrue();
    }

    private static ConversationChange Change(string conversationId, ConversationChangeKind kind) => new(conversationId, kind, 1, "d_author");

    private static string Id(int n) => n.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
}
