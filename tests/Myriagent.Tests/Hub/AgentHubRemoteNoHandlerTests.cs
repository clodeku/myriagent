using Myriagent.Remote;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 「沒有處理者」的回歸組（WP4.4 計畫步 5、R8）：授權者依擁有者指示放行之後，拿掉登記的處理者，代理輸入與刪除照 WP4.1 的語意回 unsupported、
/// 什麼都不做（不走 HandleCommand：代理輸入要帶 RemoteOrigin，遠端刪除要移到回收區）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteNoHandlerTests
{
    [Fact]
    public async Task 拿掉處理者之後_send與delete回unsupported_什麼都沒做()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h, allowFullAccess: true);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        h.Ui.Invoke(() => h.Hub.ClearRemoteHandlersForTest());
        var sent = s.Sent.Count;
        var items = h.Items(id).Count;

        var send = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("send", id, new { text = "從手機送的" }), tier: 2, fullAccess: true));
        var delete = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("delete", id), tier: 2));

        RemoteEntrySamples.ShouldBeRejected(send, RemoteResultCodes.Unsupported, RemoteGateSteps.Handler);
        RemoteEntrySamples.ShouldBeRejected(delete, RemoteResultCodes.Unsupported, RemoteGateSteps.Handler);
        s.Sent.Count.Should().Be(sent, "沒有處理者的代理輸入不走 HandleCommand");
        h.Items(id).Should().HaveCount(items);
        h.Queue(id).Should().BeEmpty();
        h.Conversation(id).Should().NotBeNull("遠端刪除不能以本機的 Delete 當退路");
    }
}
