using Myriagent.Agents;
using Myriagent.Sync;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Wiring;

/// <summary>UI 執行緒→引擎的收件匣：快照類只留最新、修改類不丟且照順序</summary>
public sealed class SyncInboxTests
{
    private static SyncSignal.Accounts Accounts(string label) =>
        new([new LocalAccountSnapshot("acc-1", AgentProvider.Claude, false, label, "#D97757", 0, "ready", null, "me@example.com", "org", null, null, false,
            new LinkSessions(0, 0, 0), 0)], false);

    private static SyncSignal.Cli Cli(string version) =>
        new(new CliStatePayload(new Dictionary<string, DeviceCliView>
        {
            ["Claude"] = new(true, version, "native", null, false, 0, 0, null, null),
        }));

    private static SyncSignal.UsageObserved Usage(string id, double percent) =>
        new(id, new UsageSnapshot(DateTimeOffset.UnixEpoch.AddDays(20000), UsageSyncRules.SourceApi, [new UsageWindow("session", "5 小時", percent, null)], null, null, null, false));

    [Fact]
    public void 快照類只留最新()
    {
        var inbox = new SyncInbox();
        var a1 = Accounts("一");
        var a2 = Accounts("二");
        var c1 = Cli("1.0.0");
        var c2 = Cli("2.0.0");

        inbox.Post(a1).Should().BeTrue("原本是空的：呼叫端要喚醒引擎");
        inbox.Post(c1).Should().BeFalse();
        inbox.Post(a2).Should().BeFalse();
        inbox.Post(c2).Should().BeFalse();
        inbox.Count.Should().Be(2, "槽位合併：永不溢出");

        inbox.TakeAll().Should().Equal(a2, c2);
        inbox.Count.Should().Be(0);
        inbox.TakeAll().Should().BeEmpty();
        inbox.Post(a1).Should().BeTrue("取完之後又是空的");
    }

    [Fact]
    public void UsageObserved每個帳戶留最新()
    {
        var inbox = new SyncInbox();
        var a1 = Usage("acc-a", 10);
        var b1 = Usage("acc-b", 20);
        var a2 = Usage("acc-a", 30);

        inbox.Post(a1);
        inbox.Post(b1);
        inbox.Post(a2);

        inbox.Count.Should().Be(2);
        inbox.TakeAll().Should().Equal([a2, b1], "每個帳戶一格，照第一次出現的順序");
    }

    [Fact]
    public void 修改類不丟且照順序()
    {
        var inbox = new SyncInbox();
        var edits = new SyncSignal[]
        {
            new SyncSignal.AccountEdited("acc-1", "一", "#111111"),
            new SyncSignal.AccountsReordered(["acc-2", "acc-1"]),
            new SyncSignal.PrefChanged("theme", new JValue("dark")),
            new SyncSignal.DeviceRenamed("d_X", "家裡的電腦"),
            new SyncSignal.AccountEdited("acc-1", "二", "#222222"),
            new SyncSignal.PrefChanged("theme", new JValue("light")),
            new SyncSignal.FullResync(),
            new SyncSignal.Append(new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.CliState, "{}", null)),
        };
        var snapshot = Accounts("快照");

        inbox.Post(edits[0]).Should().BeTrue();
        foreach (var edit in edits.Skip(1))
            inbox.Post(edit).Should().BeFalse();
        inbox.Post(snapshot);

        inbox.Count.Should().Be(edits.Length + 1);
        var all = inbox.TakeAll();
        all[0].Should().BeSameAs(snapshot, "快照類在前");
        all.Skip(1).Should().Equal(edits, "修改類照到達順序、一筆都不合併");
    }
}
