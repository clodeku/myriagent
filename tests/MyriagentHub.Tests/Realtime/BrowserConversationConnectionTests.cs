using MyriagentHub.Realtime;
using MyriagentHub.Sync;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 一條瀏覽器連線的對話檢視狀態（WP4.2 計畫步 9；D23、D25、D38、D42）：通知類在連線鎖裡看「已送過 init」再入列、open／loadOlder 的待處理與處理中、
/// 增量只給開著的對話與比快照新的 seq、close 與 init 讓處理中的請求作廢、visible 的清單、清單的標記
/// </summary>
public sealed class BrowserConversationConnectionTests
{
    private const string C1 = "0123456789abcdef0123456789abcdef";

    private const string C2 = "fedcba9876543210fedcba9876543210";

    private static readonly IReadOnlyDictionary<BrowserParts, long> NoVersions = new Dictionary<BrowserParts, long>();

    [Fact]
    public void TryEnqueueAfterInit_init之前回null_之後入列()
    {
        var connection = Connection();
        connection.TryEnqueueAfterInit(Notify("n0")).Should().BeNull("還沒送過 init");
        connection.Outbox.Count.Should().Be(0);

        connection.EnqueueInit(Init("i1"), NoVersions);
        connection.TryEnqueueAfterInit(Notify("n1")).Should().Be(ClientOutboxResult.Queued);
        connection.TryEnqueueAfterInit(Clipboard("k1")).Should().Be(ClientOutboxResult.Queued);
        Drain(connection).Should().Equal("i1", "n1", "k1");

        // 溢出後標回還沒送過 init：之後的通知不入列，等重送的 init
        connection.ResetForResync();
        connection.TryEnqueueAfterInit(Notify("n2")).Should().BeNull();
        connection.EnqueueInit(Init("i2"), NoVersions);
        connection.TryEnqueueAfterInit(Notify("n3")).Should().Be(ClientOutboxResult.Queued);
        Drain(connection).Should().Equal("i2", "n3");

        var tiny = new BrowserConnection("c2", "s1", null, () => { }, new ClientOutbox(1, 1 << 20));
        tiny.EnqueueInit(Init("i"), NoVersions);
        tiny.TryEnqueueAfterInit(Notify("n")).Should().Be(ClientOutboxResult.Overflow, "入列的結果原樣回給呼叫端（照溢出的規則處理）");
    }

    [Fact]
    public async Task 與EnqueueInit交錯_notify不會比init先出列()
    {
        // 送出幫浦一直在出列：沒在連線鎖裡看「已送過 init」的話，init 之前排進去的通知會先被送出
        for (var round = 0; round < 1000; round++)
        {
            var connection = Connection();
            using var start = new Barrier(3);
            var sent = new List<string>();
            var notifying = true;
            var pump = Dedicated(() =>
            {
                start.SignalAndWait();
                while (true)
                {
                    var done = !Volatile.Read(ref notifying);
                    while (connection.Outbox.TryDequeue(out var message))
                        sent.Add(message.Json);
                    if (done)
                        return;
                    Thread.SpinWait(20);
                }
            });
            var init = Dedicated(() =>
            {
                start.SignalAndWait();
                connection.EnqueueInit(Init("i"), NoVersions);
            });
            var notify = Dedicated(() =>
            {
                start.SignalAndWait();
                for (var i = 0; i < 20; i++)
                    connection.TryEnqueueAfterInit(Notify("n" + i));
            });
            await Task.WhenAll(init, notify);
            Volatile.Write(ref notifying, false);
            await pump;

            sent.Should().NotBeEmpty();
            sent[0].Should().Be("i", $"第 {round} 輪：通知一定排在 init 後面");
            sent.Skip(1).Select(j => int.Parse(j[1..], System.Globalization.CultureInfo.InvariantCulture)).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        }
    }

    [Fact]
    public void EnqueueInit清掉對話檢視狀態()
    {
        var connection = Connection();
        connection.EnqueueInit(Init("i1"), NoVersions);
        connection.TryRequestConversation(Open(C1)).Should().BeTrue();
        connection.TryRequestConversation(new ConversationRequest(ConversationRequestKind.LoadOlder, C2, "item9")).Should().BeTrue();
        var opened = connection.TakeConversationRequests(1).Single();
        connection.CompleteConversationRequest(opened, Snapshot(C1, "s1"), 10).Should().Be(ClientOutboxResult.Queued);
        var inFlight = connection.TakeConversationRequests(1).Single();
        connection.TryRequestConversation(Open(C2)).Should().BeTrue();
        connection.TryEnqueueConversationList(List("l1"), new ConversationListMark("hash", -480, 123)).Should().Be(ClientOutboxResult.Queued);
        connection.SetVisibleConversations([C1, C2]).Should().BeTrue();
        connection.OpenConversationIds().Should().Equal(C1);
        connection.PendingConversationRequests.Should().Be(1);

        connection.EnqueueInit(Init("i2"), NoVersions);

        connection.OpenConversationIds().Should().BeEmpty("網頁收到 init 會重新 open 畫面上的分頁");
        connection.TryGetOpenConversation(C1, out _).Should().BeFalse();
        connection.PendingConversationRequests.Should().Be(0);
        connection.TakeConversationRequests(8).Should().BeEmpty();
        connection.CompleteConversationRequest(inFlight, OlderItems(C2, "o1"), null).Should().BeNull("處理中的請求作廢");
        connection.ConversationList.Should().BeNull();
        connection.TryEnqueueConversationUpdate(Item(C1, "item1", "x"), 11).Should().BeNull("不再推增量給沒有重新 open 的對話");
        connection.VisibleConversationIds().Should().Equal([C1, C2], "visible 描述畫面上顯示什麼，init 不改變它（網頁內容沒變時不重送）");
        Drain(connection).Should().Equal("i2");
    }

    [Fact]
    public void open請求依到達順序_同樣的不重複記_有上限()
    {
        var connection = Connection();
        connection.TryRequestConversation(Open(C1)).Should().BeTrue();
        connection.TryRequestConversation(Older(C1, "b1")).Should().BeTrue();
        connection.TryRequestConversation(Open(C1)).Should().BeTrue("同樣的請求已經在等：不重複記");
        connection.TryRequestConversation(Older(C1, "b1")).Should().BeTrue();
        connection.TryRequestConversation(Older(C1, "b2")).Should().BeTrue();
        connection.TryRequestConversation(Open(C2)).Should().BeTrue();
        connection.PendingConversationRequests.Should().Be(4);

        connection.TakeConversationRequests(3).Should().Equal(Open(C1), Older(C1, "b1"), Older(C1, "b2"));
        connection.TakeConversationRequests(3).Should().Equal(Open(C2));
        connection.TakeConversationRequests(3).Should().BeEmpty();
        connection.TryRequestConversation(Open(C1)).Should().BeTrue("處理中的不算在等：重新 open 要再讀一次快照");
        connection.PendingConversationRequests.Should().Be(1);

        var full = Connection();
        for (var i = 0; i < BrowserConnection.MaxPendingConversationRequests; i++)
            full.TryRequestConversation(Older(C1, "b" + i)).Should().BeTrue();
        full.TryRequestConversation(Open(C2)).Should().BeFalse("滿了");
        full.TryRequestConversation(Older(C1, "b0")).Should().BeTrue("已經在等的照樣算接受");
        full.TakeConversationRequests(1).Should().ContainSingle();
        full.TryRequestConversation(Open(C2)).Should().BeTrue();
        BrowserConnection.MaxPendingConversationRequests.Should().Be(128);
        FluentActions.Invoking(() => full.TakeConversationRequests(-1)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 取走的open完成_標成開著_之後只推比快照新的seq()
    {
        var connection = Initialized();
        connection.TryEnqueueConversationUpdate(Item(C1, "item1", "x0"), 5).Should().BeNull("還沒開著");
        connection.TryRequestConversation(Open(C1));
        var request = connection.TakeConversationRequests(8).Single();
        connection.TryEnqueueConversationUpdate(Item(C1, "item1", "x1"), 5).Should().BeNull("處理中：快照還沒排進去");
        connection.CompleteConversationRequest(request, Snapshot(C1, "s1"), 10).Should().Be(ClientOutboxResult.Queued);
        connection.CompleteConversationRequest(request, Snapshot(C1, "s1-again"), 10).Should().BeNull("同一個請求只完成一次");
        connection.TryGetOpenConversation(C1, out var seq).Should().BeTrue();
        seq.Should().Be(10);

        connection.TryEnqueueConversationUpdate(Item(C1, "item1", "x9"), 9).Should().BeNull("快照已包含");
        connection.TryEnqueueConversationUpdate(Item(C1, "item1", "x10"), 10).Should().BeNull("等於快照的 seq 也已包含");
        connection.TryEnqueueConversationUpdate(Item(C1, "item1", "x11"), 11).Should().Be(ClientOutboxResult.Queued);
        connection.TryEnqueueConversationUpdate(Item(C1, "item1", "x12"), 12).Should().Be(ClientOutboxResult.Merged);
        connection.TryEnqueueConversationUpdate(Item(C1, "item2", "live"), null).Should().Be(ClientOutboxResult.Queued, "即時更新不比 seq");
        connection.TryEnqueueConversationUpdate(Item(C2, "item1", "other"), 99).Should().BeNull("別的對話沒開著");
        Drain(connection).Should().Equal("s1", "x12", "live");

        // 重新 open：新的快照 seq 取代
        connection.TryRequestConversation(Open(C1));
        connection.CompleteConversationRequest(connection.TakeConversationRequests(8).Single(), Snapshot(C1, "s2"), 20).Should().Be(ClientOutboxResult.Queued);
        connection.TryEnqueueConversationUpdate(Item(C1, "item1", "x15"), 15).Should().BeNull();
        connection.OpenConversationIds().Should().Equal(C1);

        FluentActions.Invoking(() => connection.TryEnqueueConversationUpdate(new ClientOutboxMessage("item", "{}"), 30))
            .Should().Throw<ArgumentException>("增量要帶 ConversationId");
    }

    [Fact]
    public void RequestReopen_擋掉帶seq的增量_排一個open_完成時寫回真正的快照seq()
    {
        // 合併前審查 F1：增量太多時閘道改成重開
        var connection = Initialized();
        connection.RequestReopen(C1).Should().BeFalse("沒開著");
        connection.PendingConversationRequests.Should().Be(0);
        connection.TryRequestConversation(Open(C1));
        connection.CompleteConversationRequest(connection.TakeConversationRequests(8).Single(), Snapshot(C1, "s1"), 10);

        connection.RequestReopen(C1).Should().BeTrue();
        connection.RequestReopen(C1).Should().BeTrue("同樣的請求已經在等：不重複排");
        connection.PendingConversationRequests.Should().Be(1);
        connection.TryGetOpenConversation(C1, out var reopening).Should().BeTrue("仍然開著（close、刪除照常處理）");
        reopening.Should().Be(BrowserConnection.ReopeningSeq);
        connection.TryEnqueueConversationUpdate(Item(C1, "item1", "x11"), 11).Should().BeNull("重開完成之前帶 seq 的增量都擋掉");
        connection.TryEnqueueConversationUpdate(Item(C1, "item2", "live"), null).Should().Be(ClientOutboxResult.Queued, "即時更新照常（快照的屏障會清掉）");

        connection.CompleteConversationRequest(connection.TakeConversationRequests(8).Single(), Snapshot(C1, "s2"), 30).Should().Be(ClientOutboxResult.Queued);
        connection.TryGetOpenConversation(C1, out var seq).Should().BeTrue();
        seq.Should().Be(30);
        connection.TryEnqueueConversationUpdate(Item(C1, "item1", "x31"), 31).Should().Be(ClientOutboxResult.Queued);
        Drain(connection).Should().Equal("s1", "s2", "x31");

        // 待處理的請求已滿：排不進去就不重開（呼叫端照常推增量）
        for (var i = 0; i < BrowserConnection.MaxPendingConversationRequests; i++)
            connection.TryRequestConversation(Older(C2, "b" + i)).Should().BeTrue();
        connection.RequestReopen(C1).Should().BeFalse();
        connection.TryGetOpenConversation(C1, out var unchanged).Should().BeTrue();
        unchanged.Should().Be(30);
        connection.CloseConversation(C1);
        connection.RequestReopen(C1).Should().BeFalse("關掉之後");
    }

    [Fact]
    public void loadOlder不改開著的狀態_對話不在了的open標成不開著()
    {
        var connection = Initialized();
        connection.TryRequestConversation(Older(C1, "b1"));
        connection.CompleteConversationRequest(connection.TakeConversationRequests(8).Single(), OlderItems(C1, "o1"), 7).Should().Be(ClientOutboxResult.Queued);
        connection.OpenConversationIds().Should().BeEmpty("olderItems 不會把對話標成開著");

        connection.TryRequestConversation(Open(C1));
        connection.CompleteConversationRequest(connection.TakeConversationRequests(8).Single(), Snapshot(C1, "s1"), 10);
        connection.TryRequestConversation(Older(C1, "b2"));
        connection.CompleteConversationRequest(connection.TakeConversationRequests(8).Single(), OlderItems(C1, "o2"), 30);
        connection.TryGetOpenConversation(C1, out var seq).Should().BeTrue();
        seq.Should().Be(10, "loadOlder 不改快照的 seq");

        // 閘道讀快照時對話已經被刪：回 conversationClosed、標成不開著
        connection.TryRequestConversation(Open(C1));
        connection.CompleteConversationRequest(connection.TakeConversationRequests(8).Single(), Closed(C1), null).Should().Be(ClientOutboxResult.Queued);
        connection.OpenConversationIds().Should().BeEmpty();
        Drain(connection).Should().Equal("o1", "s1", "o2", "closed");
    }

    [Fact]
    public void close之後_增量不入列_還在等與處理中的請求作廢()
    {
        var connection = Initialized();
        connection.TryRequestConversation(Open(C1));
        connection.CompleteConversationRequest(connection.TakeConversationRequests(8).Single(), Snapshot(C1, "s1"), 10);
        connection.TryRequestConversation(Older(C1, "b1"));
        var inFlight = connection.TakeConversationRequests(8).Single();
        connection.TryRequestConversation(Open(C1));
        connection.TryRequestConversation(Open(C2));

        connection.CloseConversation(C1).Should().BeTrue();
        connection.TryEnqueueConversationUpdate(Item(C1, "item1", "x"), 11).Should().BeNull();
        connection.CompleteConversationRequest(inFlight, OlderItems(C1, "o1"), null).Should().BeNull("晚到的回覆不送");
        connection.TakeConversationRequests(8).Should().Equal([Open(C2)], "別的對話的請求照留");
        connection.CloseConversation(C1).Should().BeFalse("沒有任何改變");

        // open 處理中被 close：快照晚到也不標成開著
        connection.TryRequestConversation(Open(C1));
        var opening = connection.TakeConversationRequests(8).Single();
        connection.CloseConversation(C1).Should().BeTrue("處理中的請求也算");
        connection.CompleteConversationRequest(opening, Snapshot(C1, "late"), 20).Should().BeNull();
        connection.OpenConversationIds().Should().BeEmpty();
        Drain(connection).Should().Equal("s1");
    }

    [Fact]
    public void 溢出後標回還沒送過init_增量清單與通知都不入列_等重送的init()
    {
        // 溢出清空佇列、標回還沒送過 init 之後，開著的對話要到重送的 init 才清掉：這段期間入列的增量會比 init 先送到網頁
        var connection = Initialized();
        connection.TryRequestConversation(Open(C1));
        connection.CompleteConversationRequest(connection.TakeConversationRequests(8).Single(), Snapshot(C1, "s1"), 10);
        Drain(connection);

        connection.ResetForResync();
        connection.TryEnqueueConversationUpdate(Item(C1, "item1", "x"), 11).Should().BeNull();
        connection.TryEnqueueConversationUpdate(Item(C1, "item2", "live"), null).Should().BeNull();
        connection.TryEnqueueConversationList(List("l"), new ConversationListMark("h", 0, 1)).Should().BeNull();
        connection.TryEnqueueAfterInit(Notify("n")).Should().BeNull();
        connection.Outbox.Count.Should().Be(0);

        connection.EnqueueInit(Init("i2"), NoVersions);
        connection.OpenConversationIds().Should().BeEmpty();
        connection.TryEnqueueConversationUpdate(Item(C1, "item1", "y"), 12).Should().BeNull("網頁重新 open 之前不推");
        Drain(connection).Should().Equal("i2");
    }

    [Fact]
    public void 還沒送過init或溢出時_open不標成開著()
    {
        var connection = Connection();
        connection.TryRequestConversation(Open(C1));
        var request = connection.TakeConversationRequests(8).Single();
        connection.CompleteConversationRequest(request, Snapshot(C1, "s1"), 10).Should().BeNull("還沒送過 init");
        connection.OpenConversationIds().Should().BeEmpty();
        connection.Outbox.Count.Should().Be(0);

        var tiny = new BrowserConnection("c2", "s1", null, () => { }, new ClientOutbox(1, 1 << 20));
        tiny.EnqueueInit(Init("i"), NoVersions);
        tiny.TryRequestConversation(Open(C1));
        tiny.CompleteConversationRequest(tiny.TakeConversationRequests(8).Single(), Snapshot(C1, "s1"), 10).Should().Be(ClientOutboxResult.Overflow);
        tiny.OpenConversationIds().Should().BeEmpty("溢出之後要重送 init，網頁沒收到這份快照");
    }

    [Fact]
    public void visible只留對話id_去重_有上限_沒變回false()
    {
        var connection = Connection();
        connection.VisibleConversationIds().Should().BeEmpty();
        connection.SetVisibleConversations([C2, "d01ARZ3NDEKTSV4RRFFQ69G5FAV", C1, C2, null, "", C1.ToUpperInvariant()]).Should().BeTrue();
        connection.VisibleConversationIds().Should().Equal([C1, C2], "只留對話 id 形狀的（草稿、大寫、空的不算）、去重、依 id 排序");
        connection.SetVisibleConversations([C1, C2]).Should().BeFalse("內容沒變");
        connection.SetVisibleConversations([C2, C1]).Should().BeFalse("順序不算");
        connection.SetVisibleConversations([]).Should().BeTrue("切到儀表板");
        connection.VisibleConversationIds().Should().BeEmpty();

        var many = Enumerable.Range(0, 300).Select(i => i.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        connection.SetVisibleConversations(many).Should().BeTrue();
        connection.VisibleConversationIds().Should().HaveCount(BrowserConnection.MaxVisibleConversations).And.Equal(many.Take(100));
        BrowserConnection.MaxVisibleConversations.Should().Be(100);
    }

    [Fact]
    public void 清單_init之前不送_排進去才記_溢出不記_init帶的清單另外記()
    {
        var mark = new ConversationListMark("h1", -480, 1_000);
        var connection = Connection();
        connection.TryEnqueueConversationList(List("l0"), mark).Should().BeNull("還沒送過 init");
        connection.ConversationList.Should().BeNull();

        connection.EnqueueInit(Init("i"), NoVersions);
        connection.MarkConversationListSent(mark);
        connection.ConversationList.Should().Be(mark, "init 帶的清單");
        var next = new ConversationListMark("h2", 0, 2_000);
        connection.TryEnqueueConversationList(List("l1"), next).Should().Be(ClientOutboxResult.Queued);
        connection.ConversationList.Should().Be(next);
        connection.TryEnqueueConversationList(List("l2"), mark).Should().Be(ClientOutboxResult.Merged, "清單以 type 合併");
        Drain(connection).Should().Equal("i", "l2");

        var tiny = new BrowserConnection("c2", "s1", null, () => { }, new ClientOutbox(1, 1 << 20));
        tiny.EnqueueInit(Init("i"), NoVersions);
        tiny.TryEnqueueConversationList(List("l1"), mark).Should().Be(ClientOutboxResult.Overflow);
        tiny.ConversationList.Should().BeNull("溢出：網頁沒收到");
    }

    /// <summary>各自的執行緒（不占執行緒集區：三方要同時在 Barrier 會合）</summary>
    private static Task Dedicated(Action action) => Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static BrowserConnection Connection() => new("c1", "s1", null, () => { }, new ClientOutbox(500, 2 * 1024 * 1024));

    private static BrowserConnection Initialized()
    {
        var connection = Connection();
        connection.EnqueueInit(Init("i"), NoVersions);
        connection.Outbox.TryDequeue(out _).Should().BeTrue();
        return connection;
    }

    private static ConversationRequest Open(string conversationId) => new(ConversationRequestKind.Open, conversationId);

    private static ConversationRequest Older(string conversationId, string beforeId) => new(ConversationRequestKind.LoadOlder, conversationId, beforeId);

    private static ClientOutboxMessage Init(string json) => new(ClientOutboxRules.InitType, json);

    private static ClientOutboxMessage Notify(string json) => new(ClientOutboxRules.NotifyType, json, ConversationId: C1);

    private static ClientOutboxMessage Clipboard(string json) => new(ClientOutboxRules.ClipboardType, json, ConversationId: C1);

    private static ClientOutboxMessage List(string json) => new(ClientOutboxRules.ConversationsType, json);

    private static ClientOutboxMessage Snapshot(string conversationId, string json) => new(ClientOutboxRules.ConversationType, json, ConversationId: conversationId);

    private static ClientOutboxMessage OlderItems(string conversationId, string json) => new("olderItems", json, ConversationId: conversationId);

    private static ClientOutboxMessage Closed(string conversationId) => new("conversationClosed", "closed", ConversationId: conversationId);

    private static ClientOutboxMessage Item(string conversationId, string itemId, string json) =>
        new(ClientOutboxRules.ItemType, json, ClientOutboxRules.ItemKey(conversationId, itemId), conversationId);

    private static List<string> Drain(BrowserConnection connection)
    {
        var result = new List<string>();
        while (connection.Outbox.TryDequeue(out var message))
            result.Add(message.Json);
        return result;
    }
}
