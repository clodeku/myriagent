using Myriagent.Security;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 交握完成前的第 4 階段訊息（D49；sync-protocol 14.4 的早到緩衝）：依到達順序留著；指令最多 16 則、溢出的只記 id（ULID、最多 64 個）回 busy；
/// 其他最多 32 則、滿了丟最舊的那則（指令不受影響）
/// </summary>
public sealed class SyncEarlyMessagesTests
{
    private static int next;

    private static CommandDelivery Command(string? id = null) =>
        new(id ?? SyncIds.NewUlid(1791580000000 + Interlocked.Increment(ref next)), new CommandEnvelope(CommandEnvelope.Version, "{}", "sig"));

    private static WatchChanged Watch(int n) => new([n.ToString("x32")]);

    [Fact]
    public void 上限照sync_protocol的14點4()
    {
        SyncEarlyMessages.MaxCommands.Should().Be(16);
        SyncEarlyMessages.MaxOthers.Should().Be(32);
        SyncEarlyMessages.MaxBusy.Should().Be(64);
    }

    [Fact]
    public void 依到達順序取出_取完就空了()
    {
        var buffer = new SyncEarlyMessages();
        var c1 = Command();
        var w = Watch(1);
        var s = new SnapshotRequest(new string('a', 32), SnapshotReasons.Restore);
        var m = new MediaRequest("relay-1", "0123456789ABCDEFGHJKMNPQRS", MediaPurposes.Thumb);
        var c2 = Command();

        foreach (var message in new object[] { c1, w, s, m, c2 })
            buffer.Add(message).Should().BeTrue();

        buffer.Count.Should().Be(5);
        var (messages, busy) = buffer.TakeAll();
        messages.Should().Equal(c1, w, s, m, c2);
        busy.Should().BeEmpty();
        buffer.Count.Should().Be(0);
        buffer.TakeAll().Messages.Should().BeEmpty();
    }

    [Fact]
    public void 第5階段的訊息算其他類_依到達順序()
    {
        var buffer = new SyncEarlyMessages();
        var request = new HandoffRequest("01J9QZ3K8M5R7T2V4W6X8Y0A1B", new string('a', 32), 1, HandoffModes.Now, "d_8P2Q0000000000000000000000", null, 1,
            new HandoffInitiator(HandoffInitiatorKinds.Device, "d_8P2Q0000000000000000000000"));
        var update = new HandoffUpdate("01J9QZ3K8M5R7T2V4W6X8Y0A1B", new string('a', 32), HandoffKinds.Takeover, HandoffStates.Committed, "d_7K3M0000000000000000000000", "d_8P2Q0000000000000000000000");
        var repair = new LogRepair(new string('0', 64), "cl:a", LogRepairReasons.Damaged);
        var c = Command();

        foreach (var message in new object[] { request, c, update, repair })
            buffer.Add(message).Should().BeTrue();

        buffer.TakeAll().Messages.Should().Equal(request, c, update, repair);
        for (var i = 0; i < SyncEarlyMessages.MaxOthers + 1; i++)
            buffer.Add(repair);
        buffer.Dropped.Should().Be(1, "其他類滿了丟最舊的");
    }

    [Fact]
    public void 指令最多16則_之後的只記id回busy_其他照收()
    {
        var buffer = new SyncEarlyMessages();
        var kept = Enumerable.Range(0, SyncEarlyMessages.MaxCommands).Select(_ => Command()).ToList();
        foreach (var c in kept)
            buffer.Add(c).Should().BeTrue();

        var over1 = Command();
        var over2 = Command();
        buffer.Add(over1).Should().BeFalse("第 17 則");
        buffer.Add(Watch(1)).Should().BeTrue("指令滿了不影響其他");
        buffer.Add(over2).Should().BeFalse();

        var (messages, busy) = buffer.TakeAll();
        messages.OfType<CommandDelivery>().Should().Equal(kept);
        messages.Should().HaveCount(SyncEarlyMessages.MaxCommands + 1);
        busy.Should().Equal(over1.CommandId, over2.CommandId);
    }

    [Fact]
    public void busy最多64個_id不是ULID的不記_都算丟掉()
    {
        var buffer = new SyncEarlyMessages();
        for (var i = 0; i < SyncEarlyMessages.MaxCommands; i++)
            buffer.Add(Command());

        buffer.Add(Command("not-a-ulid")).Should().BeFalse();
        buffer.Add(Command(string.Empty)).Should().BeFalse();
        buffer.Dropped.Should().Be(2, "不是 ULID 的不回 busy（伺服器的 RemoteResultRules 也不收）");
        var overflow = Enumerable.Range(0, SyncEarlyMessages.MaxBusy + 3).Select(_ => Command()).ToList();
        foreach (var c in overflow)
            buffer.Add(c).Should().BeFalse();

        buffer.Dropped.Should().Be(5);
        var (_, busy) = buffer.TakeAll();
        busy.Should().Equal(overflow.Take(SyncEarlyMessages.MaxBusy).Select(c => c.CommandId));
        buffer.Dropped.Should().Be(0, "取完歸零");
    }

    [Fact]
    public void 其他最多32則_滿了丟最舊的那則_指令留著()
    {
        var buffer = new SyncEarlyMessages();
        var command = Command();
        buffer.Add(Watch(0));
        buffer.Add(command);
        for (var i = 1; i < SyncEarlyMessages.MaxOthers + 5; i++)
            buffer.Add(Watch(i)).Should().BeTrue("其他類一律留下最新的");

        buffer.Dropped.Should().Be(5);
        var (messages, busy) = buffer.TakeAll();
        busy.Should().BeEmpty();
        messages.Should().HaveCount(SyncEarlyMessages.MaxOthers + 1);
        messages[0].Should().BeSameAs(command, "丟的是最舊的其他類，指令不動");
        messages.Skip(1).Cast<WatchChanged>().Select(w => w.ConversationIds[0])
            .Should().Equal(Enumerable.Range(5, SyncEarlyMessages.MaxOthers).Select(i => i.ToString("x32")));
    }

    [Fact]
    public void 整份取代的TokensUpdate與StatsUpdate每種只留最新的_PeerReply算其他類()
    {
        var buffer = new SyncEarlyMessages();
        var tokens1 = new TokensUpdate([]);
        var stats = new StatsUpdate([]);
        var reply = new PeerReply(SyncIds.NewUlid(1791580000000), new Newtonsoft.Json.Linq.JObject { ["type"] = "commandResult" });
        var c = Command();
        var tokens2 = new TokensUpdate([new TokenTallyRow("la_0123456789ABCDEFGHJKMNPQRS", "2026-10-23", 1, 0, 1)]);

        foreach (var message in new object[] { tokens1, stats, reply, c, tokens2 })
            buffer.Add(message).Should().BeTrue();

        buffer.TakeAll().Messages.Should().Equal(new object[] { stats, reply, c, tokens2 }, "舊的 TokensUpdate 拿掉，新的排在到達的位置");
        buffer.Dropped.Should().Be(0);
    }

    [Fact]
    public void null不收_不是第4階段的訊息丟例外()
    {
        var buffer = new SyncEarlyMessages();

        buffer.Add(null!).Should().BeFalse();
        buffer.Count.Should().Be(0);
        FluentActions.Invoking(() => buffer.Add(new ServerNotice("alert"))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => buffer.Add(new Notify(1, ["catalog"]))).Should().Throw<ArgumentException>();
    }
}
