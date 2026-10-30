using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 遠端動作的本機通知（WP4.4 計畫步 10、D30；契約 D8）：層級 1、2 的遠端動作 done 之後，對話正開在前景的視窗只在那些視窗提示一行，
/// 否則系統匣（每分鐘最多一則，點了開那個對話）；「通知遠端操作」關閉、層級 0、被拒的都不通知。這台的政策是 Control
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteNotifyTests
{
    private const string Label = RemoteEntrySamples.IssuerLabel;

    /// <summary>一個閒置的對話（第一輪已結束）</summary>
    private static string Idle(HubHarness h)
    {
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        return id;
    }

    private static Task<RemoteOutcome> RemoteAsync(HubHarness h, string cmd, string? conversationId, object? args = null, int tier = 1)
        => RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd(cmd, conversationId, args), tier));

    private static IReadOnlyList<(string Title, string Message, string? ConversationId)> NewNotifications(HubHarness h, int since)
        => h.Host.Notifications.All.Skip(since).ToList();

    private static List<string> Toasts(FakeView view, int since) => view.Messages("toast", since).Select(m => m.Value<string>("text")!).ToList();

    private static void ShouldBeDone(RemoteOutcome outcome) => outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);

    [Fact]
    public async Task 遠端send之後系統匣一則_點了開那個對話()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var id = Idle(h);
        var title = h.Conversation(id)!.Title;
        var notified = h.Host.Notifications.Count;
        var mark = h.Main.Mark();

        ShouldBeDone(await RemoteAsync(h, "send", id, new { text = "遠端送的" }));

        NewNotifications(h, notified).Should().Equal((RemoteNotifyRules.ActionTitle, $"{Label} 在『{title}』送出了訊息", id));
        Toasts(h.Main, mark).Should().NotContain(t => t.Contains(Label), "對話沒有開在前景的視窗，不在視窗裡提示");
    }

    [Fact]
    public async Task 對話開在前景的視窗只收到toast_視窗不在前景時改成系統匣()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var other = h.AddView("other");
        var id = Idle(h);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        var title = h.Conversation(id)!.Title;
        var notified = h.Host.Notifications.Count;
        var mark = h.Main.Mark();
        var otherMark = other.Mark();

        ShouldBeDone(await RemoteAsync(h, "rename", id, new { title = "遠端改的" }));

        Toasts(h.Main, mark).Should().Equal($"{Label} 在『{title}』改了對話名稱");
        h.Main.Messages("toast", mark).Single().Value<string>("level").Should().Be("info");
        Toasts(other, otherMark).Should().BeEmpty("只給開著這個對話的前景視窗");
        NewNotifications(h, notified).Should().BeEmpty("看得到的視窗已提示，不另外跳系統匣");

        h.Host.IsForeground = false;
        mark = h.Main.Mark();
        ShouldBeDone(await RemoteAsync(h, "rename", id, new { title = "又改了" }));

        Toasts(h.Main, mark).Should().BeEmpty();
        NewNotifications(h, notified).Should().Equal((RemoteNotifyRules.ActionTitle, $"{Label} 在『遠端改的』改了對話名稱", id));
    }

    [Fact]
    public async Task NotifyRemoteActions關閉時沒有()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        h.Ui.Invoke(() => h.Settings.Sync.NotifyRemoteActions = false);
        var id = Idle(h);
        var notified = h.Host.Notifications.Count;
        var mark = h.Main.Mark();

        ShouldBeDone(await RemoteAsync(h, "rename", id, new { title = "遠端改的" }));
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        ShouldBeDone(await RemoteAsync(h, "rename", id, new { title = "又改了" }));

        NewNotifications(h, notified).Should().BeEmpty();
        Toasts(h.Main, mark).Should().BeEmpty();
    }

    [Fact]
    public async Task 系統匣60秒內第二個只有一則_滿60秒再一則()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var id = Idle(h);
        var original = h.Conversation(id)!.Title;
        var now = new long[] { 5_000_000 };
        h.Ui.Invoke(() => h.Hub.RemoteNotifyClock = () => Volatile.Read(ref now[0]));
        var notified = h.Host.Notifications.Count;

        ShouldBeDone(await RemoteAsync(h, "rename", id, new { title = "第一次" }));
        ShouldBeDone(await RemoteAsync(h, "rename", id, new { title = "第二次" }));
        Volatile.Write(ref now[0], now[0] + RemoteNotifyRules.TrayIntervalMs - 1);
        ShouldBeDone(await RemoteAsync(h, "rename", id, new { title = "第三次" }));
        NewNotifications(h, notified).Should().HaveCount(1, "60 秒內的其他遠端動作只記錄");

        Volatile.Write(ref now[0], now[0] + 1);
        ShouldBeDone(await RemoteAsync(h, "rename", id, new { title = "第四次" }));
        NewNotifications(h, notified).Select(n => n.Message).Should().Equal(
            $"{Label} 在『{original}』改了對話名稱",
            $"{Label} 在『第三次』改了對話名稱");
    }

    [Fact]
    public async Task 層級0的stop與被拒的不通知()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        var notified = h.Host.Notifications.Count;
        var mark = h.Main.Mark();

        ShouldBeDone(await RemoteAsync(h, "stop", id, tier: 0));
        await h.WaitUntilAsync(() => s.Interrupts == 1, "FakeSession 收到停止");
        h.Ui.Invoke(() => h.Settings.Sync.RemoteControlLevel = SyncSettings.LevelView);
        var rejected = await RemoteAsync(h, "rename", id, new { title = "不允許" });
        rejected.State.Should().Be(RemoteResultStates.Rejected);

        NewNotifications(h, notified).Should().BeEmpty();
        Toasts(h.Main, mark).Should().NotContain(t => t.Contains(Label));
    }

    [Fact]
    public async Task 遠端delete_用刪除之前的標題通知_沒有對話可開()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var id = Idle(h);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        var title = h.Conversation(id)!.Title;
        var notified = h.Host.Notifications.Count;

        ShouldBeDone(await RemoteAsync(h, "delete", id, tier: 2));

        NewNotifications(h, notified).Should().Equal((RemoteNotifyRules.ActionTitle, $"{Label} 在『{title}』刪除了對話（已移到回收區）", (string?)null));
        await h.WaitUntilAsync(() => h.Hub.Trash.Contains(id), "資料夾搬進回收區");
    }

    [Fact]
    public async Task conversationCreate_以新對話的標題通知_點了開新對話()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var notified = h.Host.Notifications.Count;
        var create = RemoteSamples.Command(RemoteCommandCatalog.ConversationCreate, ("accountId", "acc-claude"), ("permissionMode", "Ask"), ("title", "整理報表"));
        var body = RemoteEntrySamples.Body(create);

        ShouldBeDone(await RemoteEntrySamples.RunAsync(h, body));

        var id = RemoteConversationIdRules.FromCommandId(body.Cid)!;
        h.Conversation(id).Should().NotBeNull();
        NewNotifications(h, notified).Should().Equal((RemoteNotifyRules.ActionTitle, $"{Label} 開了新對話『整理報表』", id));
    }

    [Fact]
    public async Task 帳戶指令以執行之前的帳戶名稱通知()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        h.Ui.Invoke(() => h.Accounts.ResolveLauncher = _ => null);
        var notified = h.Host.Notifications.Count;
        var now = new long[] { 5_000_000 };
        h.Ui.Invoke(() => h.Hub.RemoteNotifyClock = () => Volatile.Read(ref now[0]));

        // accountLogout 有 WP4.8 的處理者（會跑登出的 CLI）：它的通知在 AgentHubRemoteLogoutCliTests 經 RemoteAccountRig 測；這裡用退路的 accountRefresh
        ShouldBeDone(await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteSamples.Command("accountRefresh", ("accountId", "acc-claude2")), tier: 2)));
        Volatile.Write(ref now[0], now[0] + RemoteNotifyRules.TrayIntervalMs);
        ShouldBeDone(await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteSamples.Command("accountRemove", ("accountId", "acc-codex")), tier: 2)));

        h.OnUi(() => h.Accounts.Find("acc-codex")).Should().BeNull("前提：帳戶已移除");
        NewNotifications(h, notified).Should().Equal(
            (RemoteNotifyRules.ActionTitle, $"{Label} 重新整理了帳戶的狀態『Claude 副帳戶』", (string?)null),
            (RemoteNotifyRules.ActionTitle, $"{Label} 移除了帳戶（資料夾保留）『Codex 帳戶』", (string?)null));
    }
}
