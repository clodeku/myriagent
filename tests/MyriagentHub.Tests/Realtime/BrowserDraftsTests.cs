using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using HubOptions = MyriagentHub.Hosting.HubOptions;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 伺服器草稿（WP4.2 計畫步 13；D29）：以 <see cref="ConversationHarness"/>（真的主機＋測試驅動的閘道、測試的連線登記與時鐘）驗證
/// newConversation、草稿的 open／close、斷線寬限、工作階段隔離、上限與淘汰；帳戶取自閘道的帳戶快照（<see cref="FakeSnapshotSource"/>）
/// </summary>
public sealed class BrowserDraftsTests
{
    private const string LaA = "la_0000000000000000000000000A";

    private const string LaB = "la_0000000000000000000000000B";

    private static readonly string C1 = ConversationHarness.ConversationId(1);

    private static readonly string AccountsJson = new JArray(
        new JObject { ["id"] = LaA, ["provider"] = "Claude", ["label"] = "個人" },
        new JObject { ["id"] = LaB, ["provider"] = "Codex", ["label"] = "工作" }).ToString(Formatting.None);

    [Fact]
    public async Task newConversation回草稿_isDraft_paneId原樣()
    {
        await using var h = await StartAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var (b, _) = await h.ReadyAsync("b2");

        (await h.CmdAsync(a, "newConversation", new { accountId = LaB, paneId = "p7" })).Should().BeNull();
        var first = await a.NextAsync();
        first["type"]!.Value<string>().Should().Be("conversation");
        var id = first["conversationId"]!.Value<string>();
        BrowserDraftRules.IsDraftId(id).Should().BeTrue(id);
        first["paneId"]!.Value<string>().Should().Be("p7", "newConversation 帶來的窗格 id 原樣送回");
        first["header"]!["isDraft"]!.Value<bool>().Should().BeTrue();
        first["header"]!["id"]!.Value<string>().Should().Be(id);
        first["header"]!["accountId"]!.Value<string>().Should().Be(LaB);
        first["header"]!["provider"]!.Value<string>().Should().Be("Codex");
        first["header"]!["title"]!.Value<string>().Should().Be("新對話");
        ((JArray)first["items"]!).Should().BeEmpty();
        first["hasMore"]!.Value<bool>().Should().BeFalse();
        first["draft"]!.Type.Should().Be(JTokenType.Null);

        // 沒指定帳戶（網頁自動送的）與不在快照裡的帳戶：用第一個
        (await h.CmdAsync(a, "newConversation", new { accountId = (string?)null, paneId = (string?)null })).Should().BeNull();
        var second = await a.NextAsync();
        second["header"]!["accountId"]!.Value<string>().Should().Be(LaA);
        second["header"]!["provider"]!.Value<string>().Should().Be("Claude");
        second["paneId"]!.Type.Should().Be(JTokenType.Null);
        (await h.CmdAsync(a, "newConversation", new { accountId = "la_不存在", paneId = 7 })).Should().BeNull();
        var third = await a.NextAsync();
        third["header"]!["accountId"]!.Value<string>().Should().Be(LaA);
        third["paneId"]!.Type.Should().Be(JTokenType.Null, "不是字串的 paneId 當成沒帶");
        new[] { id, second["conversationId"]!.Value<string>(), third["conversationId"]!.Value<string>() }.Should().OnlyHaveUniqueItems();

        h.Drafts.Count.Should().Be(3);
        await b.AssertNothingAsync("草稿只回給發起的連線");
        await a.AssertNothingAsync();
    }

    [Fact]
    public async Task 沒有帳戶_newConversation回not_available_不開草稿()
    {
        await using var h = await ConversationHarness.StartAsync();
        var (a, init) = await h.ReadyAsync("b1");
        ((JArray)init["accounts"]!).Should().BeEmpty();

        (await h.CmdAsync(a, "newConversation", new { paneId = "p1" })).Should().Be(BrowserErrorCodes.NotAvailable, "同第 3 階段（網頁自動送的，不提示）");
        h.Drafts.Count.Should().Be(0);
        await a.AssertNothingAsync();
    }

    [Fact]
    public async Task close丟棄_送conversationClosed()
    {
        await using var h = await StartAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var (b, _) = await h.ReadyAsync("b2");
        var (other, _) = await h.ReadyAsync("b3", "s2");
        var draft = await NewDraftAsync(h, a);

        (await h.CmdAsync(other, "close", new { conversationId = draft })).Should().BeNull("別的工作階段的草稿：接受、不處理");
        h.Drafts.TryGet("s1", draft, out _).Should().BeTrue();
        await other.AssertNothingAsync();

        (await h.CmdAsync(a, "close", new { conversationId = draft })).Should().BeNull();
        Closed(await a.NextAsync()).Should().Be(draft);
        Closed(await b.NextAsync()).Should().Be(draft, "同一個登入的其他分頁可能開著它");
        await other.AssertNothingAsync();
        h.Drafts.TryGet("s1", draft, out _).Should().BeFalse();
        h.Drafts.Count.Should().Be(0);

        // 已經丟了：close 照樣接受、不再送；open 同不存在的對話
        (await h.CmdAsync(a, "close", new { conversationId = draft })).Should().BeNull();
        await a.AssertNothingAsync();
        (await h.CmdAsync(a, "open", new { conversationId = draft })).Should().Be(BrowserErrorCodes.Invalid);
        Closed(await a.NextAsync()).Should().Be(draft);
        await b.AssertNothingAsync();
    }

    [Fact]
    public async Task 斷線2分鐘內同工作階段open回同一份_超過就沒有()
    {
        await using var h = await StartAsync();
        var (a, _) = await h.ReadyAsync("b1");
        (await h.CmdAsync(a, "newConversation", new { accountId = LaB, paneId = "p1" })).Should().BeNull();
        var created = await a.NextAsync();
        var draft = created["conversationId"]!.Value<string>()!;

        h.Connections.Remove("b1");
        h.Time.Advance(TimeSpan.FromSeconds(119));
        var (again, _) = await h.ReadyAsync("b2");
        (await h.CmdAsync(again, "open", new { conversationId = draft })).Should().BeNull();
        var reopened = await again.NextAsync();
        reopened["type"]!.Value<string>().Should().Be("conversation");
        reopened["conversationId"]!.Value<string>().Should().Be(draft);
        reopened["paneId"]!.Type.Should().Be(JTokenType.Null, "open 的回應不帶 paneId");
        JToken.DeepEquals(reopened["header"], created["header"]).Should().BeTrue("同一份草稿");
        (await h.CmdAsync(again, "loadOlder", new { conversationId = draft, beforeId = "i1" })).Should().BeNull("草稿沒有較早的項目：接受、不處理");
        await again.AssertNothingAsync();

        h.Connections.Remove("b2");
        h.Time.Advance(TimeSpan.FromSeconds(120));
        var (late, _) = await h.ReadyAsync("b3");
        (await h.CmdAsync(late, "open", new { conversationId = draft })).Should().Be(BrowserErrorCodes.Invalid, "寬限過了：同不存在的對話");
        Closed(await late.NextAsync()).Should().Be(draft);
        h.Drafts.Count.Should().Be(0);
    }

    [Fact]
    public async Task 寬限內重新連上之後一直連著_寬限過了照樣在()
    {
        await using var h = await StartAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var draft = await NewDraftAsync(h, a);

        h.Connections.Remove("b1");
        h.Time.Advance(TimeSpan.FromSeconds(60));
        var (b, _) = await h.ReadyAsync("b2");
        h.Time.Advance(TimeSpan.FromMinutes(10));
        h.Drafts.TryGet("s1", draft, out _).Should().BeTrue("重新連上之後不再計時（連線登記時取消）");

        // 同一個工作階段還有別的連線時，移除其中一條不計時
        await h.ReadyAsync("b3");
        h.Connections.Remove("b3");
        h.Time.Advance(TimeSpan.FromMinutes(10));
        (await h.CmdAsync(b, "open", new { conversationId = draft })).Should().BeNull();
        (await b.NextAsync())["conversationId"]!.Value<string>().Should().Be(draft);

        // 最後一條移除才計時；寬限過了沒有任何呼叫也照樣在下一次呼叫時清掉
        h.Connections.Remove("b2");
        h.Time.Advance(TimeSpan.FromSeconds(120));
        h.Drafts.Count.Should().Be(0);
    }

    [Fact]
    public async Task 發起的連線已經不在_照樣計時()
    {
        // newConversation 處理到一半連線就移除了（它的 Removed 看不到新的工作階段）：開的時候就開始計時，不留下沒人會清的草稿
        await using var h = await StartAsync();
        var created = h.Drafts.Create("s-gone", null, BrowserDraftRules.AccountsOf(AccountsJson));
        created.Should().NotBeNull();
        h.Drafts.Count.Should().Be(1);
        h.Time.Advance(TimeSpan.FromSeconds(120));
        h.Drafts.Count.Should().Be(0);

        // 有連線的工作階段不計時
        var (a, _) = await h.ReadyAsync("b1");
        await NewDraftAsync(h, a);
        h.Time.Advance(TimeSpan.FromMinutes(10));
        h.Drafts.Count.Should().Be(1);
    }

    [Fact]
    public async Task 別的工作階段拿不到()
    {
        await using var h = await StartAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var (other, _) = await h.ReadyAsync("b2", "s2");
        var draft = await NewDraftAsync(h, a);

        (await h.CmdAsync(other, "open", new { conversationId = draft })).Should().Be(BrowserErrorCodes.Invalid);
        Closed(await other.NextAsync()).Should().Be(draft);
        await a.AssertNothingAsync("草稿還在，發起的工作階段不受影響");

        IBrowserDrafts drafts = h.Drafts;
        drafts.TryGet("s2", draft, out _).Should().BeFalse();
        drafts.TryUpdate("s2", draft, d => d with { Model = "x" }).Should().BeFalse();
        drafts.Remove("s2", draft).Should().BeFalse();
        drafts.TryGet("s1", draft, out var mine).Should().BeTrue();
        mine!.Model.Should().BeNull("別的工作階段改不到");
    }

    [Fact]
    public async Task 草稿不在清單也不在WatchChanged()
    {
        await using var h = await StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        var draft = await NewDraftAsync(h, a);

        h.SetState(a, visible: true, [C1, draft]);
        (await h.CmdAsync(a, "visible", new { conversationIds = new[] { draft, C1 } })).Should().BeNull();
        await h.RoundAsync();
        await h.Watch.FlushAsync();

        h.DeviceSender.Watches(live).Should().NotBeEmpty().And.Subject.Last().Should().Equal(C1);
        h.Directory.Find(draft).Should().BeNull("草稿不進目錄");
        h.Host.Services.GetRequiredService<IConversationDirectory>().Find(draft).Should().BeNull();
        var lists = (await a.TakeAsync(0)).Where(m => m["type"]!.Value<string>() == "conversations").ToList();
        lists.SelectMany(m => (JArray)m["items"]!).Select(i => i["id"]!.Value<string>()).Should().NotContain(draft, "草稿不進清單");
        var (_, init) = await h.ReadyAsync("b2");
        ((JArray)init["conversations"]!).Select(i => i["id"]!.Value<string>()).Should().Equal(C1);
    }

    [Fact]
    public async Task 第9份草稿_最久沒用的被丟且送conversationClosed()
    {
        await using var h = await StartAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var (b, _) = await h.ReadyAsync("b2");
        var (other, _) = await h.ReadyAsync("b3", "s2");
        var otherDraft = await NewDraftAsync(h, other);
        var drafts = new List<string>();
        for (var i = 0; i < 8; i++)
            drafts.Add(await NewDraftAsync(h, a));
        await b.AssertNothingAsync("8 份還沒超過");

        // 第 1 份 open 過（用過）：最久沒用的變成第 2 份
        (await h.CmdAsync(a, "open", new { conversationId = drafts[0] })).Should().BeNull();
        (await a.NextAsync())["conversationId"]!.Value<string>().Should().Be(drafts[0]);
        (await h.CmdAsync(a, "newConversation", new { paneId = "p9" })).Should().BeNull();
        var messages = await a.TakeAsync(2);
        Closed(messages[0]).Should().Be(drafts[1], "被丟的先送 conversationClosed");
        messages[1]["type"]!.Value<string>().Should().Be("conversation");
        messages[1]["paneId"]!.Value<string>().Should().Be("p9");
        Closed(await b.NextAsync()).Should().Be(drafts[1]);
        await other.AssertNothingAsync("別的工作階段的草稿不受影響");

        h.Drafts.Count.Should().Be(9, "這個工作階段 8 份、另一個 1 份");
        h.Drafts.TryGet("s1", drafts[1], out _).Should().BeFalse();
        h.Drafts.TryGet("s1", drafts[0], out _).Should().BeTrue();
        h.Drafts.TryGet("s2", otherDraft, out _).Should().BeTrue();
        (await h.CmdAsync(a, "open", new { conversationId = drafts[1] })).Should().Be(BrowserErrorCodes.Invalid);
    }

    [Fact]
    public async Task 上限照設定()
    {
        await using var h = await StartAsync(o => o.Conversations.MaxDraftsPerSession = 2);
        var (a, _) = await h.ReadyAsync("b1");
        var first = await NewDraftAsync(h, a);
        await NewDraftAsync(h, a);
        (await h.CmdAsync(a, "newConversation", new { })).Should().BeNull();
        Closed((await a.TakeAsync(2))[0]).Should().Be(first);
        h.Drafts.Count.Should().Be(2);
    }

    [Fact]
    public async Task WP45的介面_改了之後open帶新的值_id與順序由草稿維護()
    {
        await using var h = await StartAsync(o => o.Conversations.MaxDraftsPerSession = 2);
        var (a, _) = await h.ReadyAsync("b1");
        var first = await NewDraftAsync(h, a);
        var second = await NewDraftAsync(h, a);
        IBrowserDrafts drafts = h.Drafts;

        drafts.TryUpdate("s1", first, d => d with { Id = "d_改不了", Used = -1, Model = "claude-x", Effort = "high", WorkingDirectory = @"C:\proj", CustomFolder = true })
            .Should().BeTrue();

        // 改過的算用過（順序由這裡維護，change 給的 Used 不算）：下一份丟的是 second
        (await h.CmdAsync(a, "newConversation", new { })).Should().BeNull();
        Closed((await a.TakeAsync(2))[0]).Should().Be(second);

        drafts.TryGet("s1", first, out var changed).Should().BeTrue();
        changed!.Id.Should().Be(first, "id 不能改");
        (changed.Model, changed.Effort, changed.WorkingDirectory, changed.CustomFolder).Should().Be(("claude-x", "high", @"C:\proj", true));
        (await h.CmdAsync(a, "open", new { conversationId = first })).Should().BeNull();
        var header = (await a.NextAsync())["header"]!;
        header["model"]!.Value<string>().Should().Be("claude-x");
        header["workingDirectory"]!.Value<string>().Should().Be(@"C:\proj");

        FluentActions.Invoking(() => drafts.TryUpdate("s1", first, _ => null!)).Should().Throw<InvalidOperationException>();
        drafts.Remove("s1", first).Should().BeTrue();
        drafts.Remove("s1", first).Should().BeFalse();
        await a.AssertNothingAsync("Remove 不送 conversationClosed（由呼叫端決定）");
    }

    // ───── 工具 ─────

    private static async Task<ConversationHarness> StartAsync(Action<HubOptions>? configure = null)
    {
        var h = await ConversationHarness.StartAsync(configure);
        h.Source.Current = h.Source.Current with { AccountsJson = AccountsJson };
        return h;
    }

    /// <summary>newConversation 並收下 conversation，回傳草稿 id</summary>
    private static async Task<string> NewDraftAsync(ConversationHarness h, SendRecorder recorder)
    {
        (await h.CmdAsync(recorder, "newConversation", new { })).Should().BeNull();
        var message = await recorder.NextAsync();
        message["type"]!.Value<string>().Should().Be("conversation");
        return message["conversationId"]!.Value<string>()!;
    }

    private static string? Closed(JObject message)
    {
        message["type"]!.Value<string>().Should().Be("conversationClosed");
        return message["conversationId"]!.Value<string>();
    }
}
