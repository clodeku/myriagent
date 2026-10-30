using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 伺服器草稿換成正式對話（WP4.6 計畫步 7；D17、D41）：以 <see cref="ConversationHarness"/>（真的主機＋測試驅動的閘道）驗證三個時機——
/// 即時（這一輪的 conv.created）、通知溢出的整份重讀、open 草稿時對話已建立。WP4.5 第一次 send 在列提交之前記下的
/// <see cref="BrowserDraft.PendingConversationId"/> 這裡直接以 <see cref="IBrowserDrafts.TryUpdate"/> 記（同 CommandRouter.Drafts 的做法）；
/// 經真的指令路由與電腦的那一份在 DraftCreateTests 的 <c>conv_created比done先到_草稿仍對得上</c>
/// </summary>
public sealed class BrowserGatewayDraftSwapTests
{
    private const string LaA = "la_0000000000000000000000000A";

    private static readonly string C1 = ConversationHarness.ConversationId(1);

    private static readonly string C2 = ConversationHarness.ConversationId(2);

    private static readonly string AccountsJson = new JArray(new JObject { ["id"] = LaA, ["provider"] = "Claude", ["label"] = "個人" }).ToString(Formatting.None);

    [Fact]
    public async Task 草稿換成正式對話_同工作階段每條連線收到replacedBy()
    {
        await using var h = await StartAsync();
        var device = await h.AddDeviceAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var (b, _) = await h.ReadyAsync("b2");
        var draft = await NewDraftAsync(h, a);
        Pend(h, "s1", draft, C1);

        await h.CreateAsync(device, C1, "第一則");
        await h.RoundAsync();

        var first = await a.TakeAsync(0);
        Replaced(first).Should().Equal((draft, C1));
        Replaced(await b.TakeAsync(0)).Should().Equal([(draft, C1)], "同一個登入的其他分頁也開著它");
        Closed(first).Should().BeEmpty("只有帶 replacedBy 的那一則");
        LastList(first).Select(i => i["id"]!.Value<string>()).Should().Equal(C1);
        h.Drafts.TryGet("s1", draft, out _).Should().BeFalse("換掉之後草稿就不在了");

        // 之後同一個對話再改中繼資料：不再送
        await h.PublishAsync(device, (K.Meta, ConversationSamples.Meta("改名", ConversationSyncPolicies.Full), C1));
        await h.RoundAsync();
        Replaced(await a.TakeAsync(0)).Should().BeEmpty();
        Replaced(await b.TakeAsync(0)).Should().BeEmpty();
    }

    [Fact]
    public async Task conv_created比done先到_仍換得到()
    {
        // Pending 在列提交時就記下（WP4.5 計畫 :1110）：conv.created 到的時候指令還沒有結果（done 之後才來），草稿照樣換
        await using var h = await StartAsync();
        var device = await h.AddDeviceAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var draft = await NewDraftAsync(h, a);
        Pend(h, "s1", draft, C1);
        h.Drafts.TryGet("s1", draft, out var reserved).Should().BeTrue();
        reserved!.SentCommandId.Should().NotBeNull("送出了、還沒有結果");

        await h.CreateAsync(device, C1);
        await h.RoundAsync();
        var messages = await a.TakeAsync(0);
        Replaced(messages).Should().Equal((draft, C1));
        messages.Select(m => m["type"]!.Value<string>()).Should().NotContain("commandResult", "換草稿不看指令的結果");
    }

    [Fact]
    public async Task 同工作階段兩個分頁都開著草稿_兩條都收到_別的工作階段的連線收不到()
    {
        await using var h = await StartAsync();
        var device = await h.AddDeviceAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var (b, _) = await h.ReadyAsync("b2");
        var (other, _) = await h.ReadyAsync("b3", "s2");
        var draft = await NewDraftAsync(h, a);
        // b 也開著同一份草稿（同一個工作階段）
        (await h.CmdAsync(b, "open", new { conversationId = draft })).Should().BeNull();
        (await b.NextAsync())["conversationId"]!.Value<string>().Should().Be(draft);
        var otherDraft = await NewDraftAsync(h, other);
        Pend(h, "s1", draft, C1);

        await h.CreateAsync(device, C1);
        await h.RoundAsync();

        Replaced(await a.TakeAsync(0)).Should().Equal((draft, C1));
        Replaced(await b.TakeAsync(0)).Should().Equal((draft, C1));
        var others = await other.TakeAsync(0);
        Replaced(others).Should().BeEmpty("別的工作階段（別的登入）收不到");
        Closed(others).Should().BeEmpty();
        h.Drafts.TryGet("s2", otherDraft, out _).Should().BeTrue("別的工作階段的草稿不動");
    }

    [Fact]
    public async Task 沒有PendingConversationId的草稿_conv_meta不換()
    {
        await using var h = await StartAsync();
        var device = await h.AddDeviceAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var plain = await NewDraftAsync(h, a);
        var elsewhere = await NewDraftAsync(h, a);
        Pend(h, "s1", elsewhere, C2);

        await h.CreateAsync(device, C1);
        await h.PublishAsync(device, (K.Meta, ConversationSamples.Meta("改名", ConversationSyncPolicies.Full), C1));
        await h.RoundAsync();

        var messages = await a.TakeAsync(0);
        Replaced(messages).Should().BeEmpty();
        Closed(messages).Should().BeEmpty();
        h.Drafts.TryGet("s1", plain, out _).Should().BeTrue("還沒送出的草稿不動");
        h.Drafts.TryGet("s1", elsewhere, out var waiting).Should().BeTrue("等的是別的對話");
        waiting!.PendingConversationId.Should().Be(C2);
    }

    [Fact]
    public async Task 通知溢出整份重讀_仍換得到()
    {
        await using var h = await StartAsync(settings: Settings(("Hub:Conversations:ChangeFeedMaxEntries", "3")));
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C2, 1);
        await h.RunAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var draft = await NewDraftAsync(h, a);
        Pend(h, "s1", draft, C1);

        // 一批 4 筆：前 3 筆的變更進得去，最後的 conv.created 因為通知滿了被丟掉
        await h.PublishAsync(device,
            (K.Item, ConversationSamples.Item("2", id: "item2", ord: 2), C2), (K.Item, ConversationSamples.Item("3", id: "item3", ord: 3), C2),
            (K.Item, ConversationSamples.Item("4", id: "item4", ord: 4), C2), (K.Created, ConversationSamples.Meta("新對話", ConversationSyncPolicies.Full), C1));
        await h.RoundAsync();

        Replaced(await a.TakeAsync(0)).Should().Equal([(draft, C1)], "整份重讀時改查目錄");
        h.Drafts.TryGet("s1", draft, out _).Should().BeFalse();
    }

    [Fact]
    public async Task 重連後open草稿_對話已建立_回replacedBy不回草稿()
    {
        await using var h = await StartAsync();
        var device = await h.AddDeviceAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var draft = await NewDraftAsync(h, a);
        Pend(h, "s1", draft, C1);

        // 沒有任何連線時建立：閘道取走變更丟掉（DiscardConversations），草稿還在（寬限內）
        h.Connections.Remove("b1");
        await h.CreateAsync(device, C1);
        await h.RunAsync();
        h.Feed.Pending.Should().Be(0, "沒有連線：變更取走丟掉");
        h.Drafts.TryGet("s1", draft, out _).Should().BeTrue();

        var (b, _) = await h.ReadyAsync("b2");
        var (c, _) = await h.ReadyAsync("b3");
        (await h.CmdAsync(b, "open", new { conversationId = draft })).Should().BeNull("換掉了，不是錯誤");
        var reply = await b.TakeAsync(0);
        Replaced(reply).Should().Equal((draft, C1));
        reply.Should().NotContain(m => m["type"]!.Value<string>() == "conversation" && m["conversationId"]!.Value<string>() == draft, "不再回草稿");
        Replaced(await c.TakeAsync(0)).Should().Equal([(draft, C1)], "同一個工作階段的每一條連線");
        h.Drafts.TryGet("s1", draft, out _).Should().BeFalse();
    }

    [Fact]
    public async Task 換掉之後再open草稿id_墓碑還在時再回replacedBy_過期後才只回conversationClosed()
    {
        // 換草稿的那一則剛好被新的 init 清掉（風險 R16）：墓碑還在時網頁重新 open 草稿照樣換成新對話（合併前審查 LIVE-1）；
        // 墓碑過期（10 分鐘）之後才同不存在的對話，只關掉草稿（新對話照常在清單）
        await using var h = await StartAsync();
        var device = await h.AddDeviceAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var draft = await NewDraftAsync(h, a);
        Pend(h, "s1", draft, C1);
        await h.CreateAsync(device, C1);
        await h.RoundAsync();
        Replaced(await a.TakeAsync(0)).Should().Equal((draft, C1));

        (await h.CmdAsync(a, "open", new { conversationId = draft })).Should().BeNull("換掉了，不是錯誤");
        (await a.NextAsync()).ToString(Formatting.None).Should().Be("{\"type\":\"conversationClosed\",\"conversationId\":\"" + draft + "\",\"replacedBy\":\"" + C1 + "\"}");

        h.Time.Advance(TimeSpan.FromSeconds(BrowserDraftRules.ReplacedKeepMinSeconds));
        (await h.CmdAsync(a, "open", new { conversationId = draft })).Should().Be(BrowserErrorCodes.Invalid);
        (await a.NextAsync()).ToString(Formatting.None).Should().Be("{\"type\":\"conversationClosed\",\"conversationId\":\"" + draft + "\"}");
    }

    [Fact]
    public async Task 工作階段唯一的連線先移除_別的工作階段在線時換掉_重連後open草稿回replacedBy()
    {
        // 合併前審查 LIVE-1：手機切到背景、連線斷了，桌機的另一個登入還在線，閘道照常換草稿；換的那一則沒有連線可送，
        // 手機重連（同一個工作階段）後 open 草稿要拿到 replacedBy，不是關掉剛送出第一則的分頁
        await using var h = await StartAsync();
        var device = await h.AddDeviceAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var (other, _) = await h.ReadyAsync("b9", "s2");
        var draft = await NewDraftAsync(h, a);
        Pend(h, "s1", draft, C1);
        h.Connections.Remove("b1");

        await h.CreateAsync(device, C1);
        await h.RoundAsync();
        h.Drafts.TryGet("s1", draft, out _).Should().BeFalse("別的工作階段在線：閘道照常換掉草稿");
        Replaced(await other.TakeAsync(0)).Should().BeEmpty("別的登入收不到");

        var (b, _) = await h.ReadyAsync("b2");
        (await h.CmdAsync(b, "open", new { conversationId = draft })).Should().BeNull("換掉了，不是錯誤");
        var reply = await b.TakeAsync(0);
        Replaced(reply).Should().Equal((draft, C1));
        Closed(reply).Should().BeEmpty();
        reply.Should().NotContain(m => m["type"]!.Value<string>() == "conversation" && m["conversationId"]!.Value<string>() == draft, "不再回草稿");
    }

    [Fact]
    public async Task 換的那一則排進斷掉的連線_新連線open草稿照樣回replacedBy()
    {
        // 合併前審查 LIVE-1：OS 切斷 WebSocket、伺服器要等 keepalive 逾時才移除連線，這段時間換草稿的那一則只排進死連線的佇列
        await using var h = await StartAsync();
        var device = await h.AddDeviceAsync();
        var (dead, _) = await h.ReadyAsync("b1");
        var draft = await NewDraftAsync(h, dead);
        Pend(h, "s1", draft, C1);
        await h.CreateAsync(device, C1);
        await h.RoundAsync();
        h.Drafts.TryGet("s1", draft, out _).Should().BeFalse();

        // 死連線的訊息不讀；同一個工作階段的新連線（重連）送 init 後 open 草稿
        var (fresh, _) = await h.ReadyAsync("b2");
        (await h.CmdAsync(fresh, "open", new { conversationId = draft })).Should().BeNull();
        Replaced(await fresh.TakeAsync(0)).Should().Equal((draft, C1));
    }

    [Fact]
    public async Task 墓碑換成的對話不在目錄_open草稿同不存在的對話()
    {
        // 換成的新對話被刪掉（目錄找不到）：不帶 replacedBy（網頁換過去也只會再被關掉），同不存在的對話
        await using var h = await StartAsync();
        var device = await h.AddDeviceAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var draft = await NewDraftAsync(h, a);
        Pend(h, "s1", draft, C2);
        // C2 從沒建立：手動拿走草稿（同整份重讀時查目錄說存在、之後被刪）
        h.Drafts.TakePending(id => id == C2).Should().ContainSingle();
        h.Drafts.TryGetReplaced("s1", draft, out var replacedBy).Should().BeTrue();
        replacedBy.Should().Be(C2);

        (await h.CmdAsync(a, "open", new { conversationId = draft })).Should().Be(BrowserErrorCodes.Invalid, "換成的對話不在目錄：同不存在的對話");
        (await a.NextAsync()).ToString(Formatting.None).Should().Be("{\"type\":\"conversationClosed\",\"conversationId\":\"" + draft + "\"}");
    }

    [Fact]
    public async Task 草稿的open_Pending的對話還不在_照舊回草稿()
    {
        // 送出了、電腦還沒建立（或建立失敗）：open 照舊回同一份草稿（WP4.5：再送 send 回 busy）
        await using var h = await StartAsync();
        await h.AddDeviceAsync();
        var (a, _) = await h.ReadyAsync("b1");
        var draft = await NewDraftAsync(h, a);
        Pend(h, "s1", draft, C1);

        (await h.CmdAsync(a, "open", new { conversationId = draft })).Should().BeNull();
        var reply = await a.NextAsync();
        (reply["type"]!.Value<string>(), reply["conversationId"]!.Value<string>()).Should().Be(("conversation", draft));
        h.Drafts.TryGet("s1", draft, out _).Should().BeTrue();
    }

    [Fact]
    public async Task ClientState的openConversationIds換掉之後_舊對話有事件會標未讀並送notify()
    {
        // D2 的伺服器端效果（審查 2）：網頁切走之後傳輸層重送 ClientState，舊的對話不再算看得到
        await using var h = await StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1, "一");
        await h.CreateAsync(device, C2, "二");
        var (a, _) = await h.ReadyAsync("b1");
        h.SetState(a, visible: true, [C1], focused: true);
        await h.RunAsync();
        await a.TakeAsync(0);

        await h.PublishAsync(device, (K.Attention, new JObject { ["kind"] = "completed" }, C1));
        await h.RoundAsync();
        var seen = await a.TakeAsync(0);
        seen.Select(m => m["type"]!.Value<string>()).Should().NotContain("notify", "看得到的對話不送 notify");
        h.Unread.Contains(C1).Should().BeFalse("看得到而且 focus：不標未讀");

        h.SetState(a, visible: true, [C2], focused: true);
        await h.RunAsync();
        await a.TakeAsync(0);
        await h.PublishAsync(device, (K.Attention, new JObject { ["kind"] = "completed" }, C1));
        await h.RoundAsync();
        var away = await a.TakeAsync(0);
        var notify = away.Where(m => m["type"]!.Value<string>() == "notify").ToList();
        notify.Should().ContainSingle("切走之後舊的對話看不到了");
        (notify[0]["conversationId"]!.Value<string>(), notify[0]["kind"]!.Value<string>()).Should().Be((C1, "completed"));
        h.Unread.Contains(C1).Should().BeTrue();
        LastList(away).Single(i => i["id"]!.Value<string>() == C1)["unread"]!.Value<bool>().Should().BeTrue();
    }

    // ───── 工具 ─────

    private static async Task<ConversationHarness> StartAsync(IDictionary<string, string?>? settings = null)
    {
        var h = await ConversationHarness.StartAsync(settings: settings);
        h.Source.Current = h.Source.Current with { AccountsJson = AccountsJson };
        return h;
    }

    /// <summary>newConversation 並收下 conversation，回傳草稿 id</summary>
    private static async Task<string> NewDraftAsync(ConversationHarness h, SendRecorder recorder)
    {
        (await h.CmdAsync(recorder, "newConversation", new { accountId = LaA })).Should().BeNull();
        var message = await recorder.NextAsync();
        message["type"]!.Value<string>().Should().Be("conversation");
        return message["conversationId"]!.Value<string>()!;
    }

    /// <summary>同 WP4.5 第一次 send 在列提交之前的記法（CommandRouter.Drafts）：SentCommandId 與 PendingConversationId 一起設</summary>
    private static void Pend(ConversationHarness h, string sessionId, string draftId, string conversationId) =>
        h.Drafts.TryUpdate(sessionId, draftId, d => d with { SentCommandId = SyncIds.NewUlid(1_760_000_000_000), PendingConversationId = conversationId }).Should().BeTrue();

    /// <summary>帶 replacedBy 的 conversationClosed：(草稿 id, 新對話 id)</summary>
    private static List<(string Draft, string Conversation)> Replaced(IEnumerable<JObject> messages) =>
        messages.Where(m => m["type"]!.Value<string>() == "conversationClosed" && m["replacedBy"] is not null)
            .Select(m => (m["conversationId"]!.Value<string>()!, m["replacedBy"]!.Value<string>()!)).ToList();

    /// <summary>沒有 replacedBy 的 conversationClosed 的對話 id</summary>
    private static List<string> Closed(IEnumerable<JObject> messages) =>
        messages.Where(m => m["type"]!.Value<string>() == "conversationClosed" && m["replacedBy"] is null).Select(m => m["conversationId"]!.Value<string>()!).ToList();

    /// <summary>最後一份 conversations 的 items</summary>
    private static JArray LastList(IEnumerable<JObject> messages) =>
        (JArray)messages.Last(m => m["type"]!.Value<string>() == "conversations")["items"]!;
}
