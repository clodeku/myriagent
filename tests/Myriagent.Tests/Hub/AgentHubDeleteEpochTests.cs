using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 刪除的 epoch（第 5 階段計畫 6、WP5.1）：中樞刪除對話時，發布者放進盒子的刪除標記帶刪除當下的 epoch（<c>LeaseEpoch ?? 1</c>）。
/// 接手後的對話 epoch 不是 1，引擎沒有中繼資料快照時就以它送 conv.deleted（不然會被伺服器 fenced）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubDeleteEpochTests
{
    [Theory]
    [InlineData(null, 1L)]
    [InlineData(3L, 3L)]
    public void 刪除標記帶刪除當下的epoch(long? leaseEpoch, long expected)
    {
        var conversation = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "接手過來的對話", LeaseEpoch = leaseEpoch };
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var box = new ConversationChangeBox();
        var engine = new RecordingSyncEngine { Conversations = box };
        var publisher = h.OnUi(() => ConversationPublisher.TryCreate(h.Hub, engine, new ManualTimeProvider()))!;
        h.Ui.Invoke(() => h.Hub.AttachConversationSink(publisher));
        box.TakeAll();

        h.Command(h.Main, "delete", new { conversationId = conversation.Id });

        var deleting = box.TakeAll().OfType<DeletingChange>().Should().ContainSingle().Subject;
        deleting.ConversationId.Should().Be(conversation.Id);
        deleting.Epoch.Should().Be(expected);
        h.Ui.Invoke(publisher.Dispose);
    }
}
