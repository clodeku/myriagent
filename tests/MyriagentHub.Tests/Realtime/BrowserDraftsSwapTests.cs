using Myriagent.Agents;
using MyriagentHub.Realtime;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 草稿換成正式對話時拿走草稿（WP4.6 計畫步 7；D41）：<see cref="BrowserDrafts.TakeCreated"/> 與 <see cref="BrowserDrafts.TakePending"/>。
/// 只用草稿本身（測試的連線登記與時鐘）；PendingConversationId 照 WP4.5 的做法以 <see cref="IBrowserDrafts.TryUpdate"/> 記下
/// </summary>
public sealed class BrowserDraftsSwapTests : IDisposable
{
    private static readonly IReadOnlyList<DraftAccount> Accounts = [new("la_0000000000000000000000000A", AgentProvider.Claude)];

    private static readonly string C1 = Id(1);

    private static readonly string C2 = Id(2);

    private readonly BrowserDrafts drafts;

    private readonly FakeTimestampProvider time = new(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));

    public BrowserDraftsSwapTests()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new HubOptions());
        var connections = new BrowserConnections(options, NullLogger<BrowserConnections>.Instance);
        drafts = new BrowserDrafts(connections, options, time);
    }

    public void Dispose() => drafts.Dispose();

    [Fact]
    public void TakeCreated_只拿Pending相同的_拿了就移除()
    {
        var sent = NewDraft("s1");
        var other = NewDraft("s1");
        Pend("s1", sent, C1);

        drafts.TakeCreated(C1).Should().Equal(("s1", sent));
        drafts.TryGet("s1", sent, out _).Should().BeFalse("拿了就移除：之後 open 同不存在的對話");
        drafts.TakeCreated(C1).Should().BeEmpty("第二次拿不到");
        drafts.TryGet("s1", other, out _).Should().BeTrue("同一個工作階段的其他草稿不動");
        drafts.Count.Should().Be(1);
    }

    [Fact]
    public void TakeCreated_沒有相符回空清單_其他草稿不動()
    {
        var a = NewDraft("s1");
        var b = NewDraft("s2");
        Pend("s2", b, C2);

        drafts.TakeCreated(C1).Should().BeEmpty();
        drafts.Count.Should().Be(2);
        drafts.TryGet("s1", a, out var left).Should().BeTrue();
        left!.PendingConversationId.Should().BeNull();
        drafts.TryGet("s2", b, out var pending).Should().BeTrue();
        pending!.PendingConversationId.Should().Be(C2, "別的對話 id 不拿");

        // 最後一份草稿拿走時工作階段整個拿掉（同 Remove）：之後再開照常
        drafts.TakeCreated(C2).Should().Equal(("s2", b));
        drafts.Count.Should().Be(1);
        NewDraft("s2");
        drafts.Count.Should().Be(2);
    }

    [Fact]
    public void TakePending_exists為false的留著_Pending換了不拿()
    {
        var created = NewDraft("s1");
        var missing = NewDraft("s1");
        var changed = NewDraft("s2");
        var plain = NewDraft("s2");
        Pend("s1", created, C1);
        Pend("s1", missing, Id(9));
        Pend("s2", changed, C2);
        var asked = new List<string>();

        var taken = drafts.TakePending(id =>
        {
            asked.Add(id);
            // 查目錄的這段時間（鎖外）：changed 被拒絕清掉又送了一次，換成別的對話 id
            if (id == C2)
                Pend("s2", changed, Id(7));
            return id == C1 || id == C2;
        });

        taken.Should().Equal(("s1", created, C1));
        asked.Should().BeEquivalentTo([C1, Id(9), C2], "只問記著 Pending 的草稿");
        drafts.TryGet("s1", created, out _).Should().BeFalse();
        drafts.TryGet("s1", missing, out var stay).Should().BeTrue("對話還不在：留著（再送 send 照舊 busy）");
        stay!.PendingConversationId.Should().Be(Id(9));
        drafts.TryGet("s2", changed, out var again).Should().BeTrue("Pending 換了不拿");
        again!.PendingConversationId.Should().Be(Id(7));
        drafts.TryGet("s2", plain, out _).Should().BeTrue();

        // 都不在目錄：一份都不拿；沒有記著 Pending 的草稿時根本不問
        drafts.TakePending(_ => false).Should().BeEmpty();
        drafts.Count.Should().Be(3);
        drafts.Remove("s1", missing).Should().BeTrue();
        drafts.Remove("s2", changed).Should().BeTrue();
        drafts.TakePending(_ => throw new InvalidOperationException("不該問")).Should().BeEmpty();
    }

    [Fact]
    public void 換掉的草稿留墓碑_工作階段的草稿全丟了也查得到_10分鐘後清掉()
    {
        // 合併前審查 LIVE-1：換的那一則可能送進斷掉的連線，網頁重連後 open 草稿要查得到換成哪個對話
        var sent = NewDraft("s1");
        Pend("s1", sent, C1);
        drafts.TakeCreated(C1).Should().Equal(("s1", sent));
        drafts.Count.Should().Be(0, "工作階段的最後一份草稿拿走了");

        drafts.TryGetReplaced("s1", sent, out var replacedBy).Should().BeTrue();
        replacedBy.Should().Be(C1);
        drafts.TryGetReplaced("s2", sent, out _).Should().BeFalse("別的工作階段（別的登入）查不到");
        drafts.TryGetReplaced("s1", NewDraft("s1"), out _).Should().BeFalse("還在的草稿沒有墓碑");

        // 草稿的斷線寬限（120 秒）過了、工作階段的草稿整批丟掉：墓碑照樣在
        time.Advance(TimeSpan.FromSeconds(BrowserDraftRules.ReplacedKeepMinSeconds - 1));
        drafts.Count.Should().Be(0);
        drafts.TryGetReplaced("s1", sent, out _).Should().BeTrue("還沒到期");
        time.Advance(TimeSpan.FromSeconds(1));
        drafts.TryGetReplaced("s1", sent, out _).Should().BeFalse("留滿 10 分鐘就清掉");
        drafts.ReplacedCount.Should().Be(0);
    }

    [Fact]
    public void TakePending拿走的也留墓碑_每個工作階段最多留草稿上限的兩倍()
    {
        var first = NewDraft("s1");
        Pend("s1", first, C1);
        drafts.TakePending(id => id == C1).Should().Equal(("s1", first, C1));
        drafts.TryGetReplaced("s1", first, out var by).Should().BeTrue();
        by.Should().Be(C1);

        var max = BrowserDraftRules.MaxReplacedPerSession(new HubOptions().Conversations.MaxDraftsPerSession);
        var last = string.Empty;
        for (var i = 0; i < max; i++)
        {
            last = NewDraft("s1");
            Pend("s1", last, Id(100 + i));
            drafts.TakeCreated(Id(100 + i)).Should().Equal(("s1", last));
        }
        drafts.ReplacedCount.Should().Be(max, "超過上限時丟最舊的");
        drafts.TryGetReplaced("s1", first, out _).Should().BeFalse("最舊的那一筆被丟掉");
        drafts.TryGetReplaced("s1", last, out var newest).Should().BeTrue();
        newest.Should().Be(Id(100 + max - 1));
    }

    private string NewDraft(string sessionId) => drafts.Create(sessionId, null, Accounts)!.Draft.Id;

    private void Pend(string sessionId, string draftId, string conversationId) =>
        drafts.TryUpdate(sessionId, draftId, d => d with { SentCommandId = "01J9Q00000000000000000000" + conversationId[^1], PendingConversationId = conversationId })
            .Should().BeTrue();

    private static string Id(int n) => n.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
}
