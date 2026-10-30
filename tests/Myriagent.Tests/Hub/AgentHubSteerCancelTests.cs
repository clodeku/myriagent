using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 取消插進這一輪的訊息（cancelSteer）：Claude 的使用者訊息帶中樞給的 uuid（CliUuid），插入後輸入區有 steered、取消成功就移除那則並放回輸入框，
/// 失敗只提示；這一輪結束就不能再取消
/// </summary>
public class AgentHubSteerCancelTests
{
    private static JObject? Steered(JObject composerMessage) => composerMessage["composer"]?["steered"] as JObject;

    [Fact]
    public async Task 送出與插入都帶中樞給的uuid_取消成功移除那則並放回輸入框()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude, "第一則");
        var first = h.Items(id).Single(i => i.Kind == AgentItemKind.User);
        first.CliUuid.Should().NotBeNullOrEmpty();
        session.Sent.All[0].Input.MessageUuid.Should().Be(first.CliUuid, "寫給 CLI 的 uuid 就是項目記的");

        session.CanSteer = true;
        var mark = h.Main.Mark();
        h.Send(h.Main, id, "順便把測試也跑一下");
        await h.WaitUntilAsync(() => session.Steered.Count == 1, "插進這一輪");
        var steered = h.Items(id).Last(i => i.Kind == AgentItemKind.User);
        steered.Text.Should().Be("順便把測試也跑一下");
        steered.CliUuid.Should().Be(session.Steered.All[0].MessageUuid);
        var composer = await h.Main.WaitForAsync("composer", m => Steered(m) != null, mark, conversationId: id);
        Steered(composer)!.Value<string>("id").Should().Be(steered.Id);

        mark = h.Main.Mark();
        h.Command(h.Main, "cancelSteer", new { conversationId = id, id = steered.Id });
        var back = await h.Main.WaitForAsync("steerCancelled", null, mark, conversationId: id);
        back.Value<string>("text").Should().Be("順便把測試也跑一下");
        session.SteerCancels.All.Should().Equal(steered.CliUuid);
        h.Items(id).Should().NotContain(i => i.Id == steered.Id);
        h.Main.Messages("removeItem", mark, id).Should().ContainSingle(m => m.Value<string>("id") == steered.Id);
        h.Main.Messages("composer", mark, id).Last().Should().Match<JObject>(m => Steered(m) == null);
    }

    [Fact]
    public async Task 已經讀走的取消不了_只提示_這一輪結束後不能再取消()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude, "第一則");
        session.CanSteer = true;
        session.CancelSteerResult = false;
        h.Send(h.Main, id, "插入的");
        await h.WaitUntilAsync(() => session.Steered.Count == 1, "插進這一輪");
        var steered = h.Items(id).Last(i => i.Kind == AgentItemKind.User);

        var mark = h.Main.Mark();
        h.Command(h.Main, "cancelSteer", new { conversationId = id, id = steered.Id });
        await h.Main.WaitForAsync("toast", m => m.Value<string>("text")!.Contains("取消不了"), mark);
        h.Items(id).Should().Contain(i => i.Id == steered.Id);
        h.Main.Messages("steerCancelled", mark).Should().BeEmpty();

        // 再插一則、這一輪結束：不能再取消
        h.Send(h.Main, id, "又插一則");
        await h.WaitUntilAsync(() => session.Steered.Count == 2, "插進這一輪");
        h.CompleteTurn(session);
        mark = h.Main.Mark();
        var again = h.Items(id).Last(i => i.Kind == AgentItemKind.User);
        h.Command(h.Main, "cancelSteer", new { conversationId = id, id = again.Id });
        await Task.Delay(100);
        session.SteerCancels.Count.Should().Be(1);
    }

    [Fact]
    public void 其他代理不帶uuid()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Codex, "第一則");
        h.Items(id).Single(i => i.Kind == AgentItemKind.User).CliUuid.Should().BeNull();
        session.Sent.All[0].Input.MessageUuid.Should().BeNull();
    }
}
