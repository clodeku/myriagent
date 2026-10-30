using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 伺服器記的網頁未讀（WP4.2 計畫步 12A；答 6、D45、D46）：以 <see cref="ConversationHarness"/>（真的主機＋測試驅動的閘道）逐輪驗證標記、清除、
/// 清單重送與 sync.db v3 的列；重啟後還在的那一則以真的主機、正式的閘道與瀏覽器 hub 驗證
/// </summary>
public sealed class BrowserUnreadTests
{
    private static readonly string C1 = ConversationHarness.ConversationId(1);

    private static readonly string C2 = ConversationHarness.ConversationId(2);

    private static readonly string C3 = ConversationHarness.ConversationId(3);

    private static JObject Attention(string kind) => new() { ["kind"] = kind };

    private static JObject Completed() => ConversationEventLogTests.Turn(ConversationTurnPhases.Completed);

    [Fact]
    public async Task 沒人看見時完成_標未讀()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1, "一");
        await h.CreateAsync(device, C2, "二");
        await h.CreateAsync(device, C3, "三");
        var (a, init) = await h.ReadyAsync("b1");
        ((JArray)init["conversations"]!).Select(c => c["unread"]!.Value<bool>()).Should().OnlyContain(u => !u, "還沒有未讀");

        var first = await h.PublishAsync(device, (K.Attention, Attention("completed"), C1), (K.Turn, Completed(), C2),
            (K.Turn, ConversationEventLogTests.Turn(ConversationTurnPhases.Started), C3));
        await h.RoundAsync();

        var items = await NextListAsync(a);
        Unread(items, C1).Should().BeTrue("unread 取自伺服器的未讀集合：注意事件 completed");
        Unread(items, C2).Should().BeTrue("一輪結束");
        Unread(items, C3).Should().BeFalse("一輪開始不標");
        h.Unread.Ids().Should().Equal(new[] { C1, C2 }.Order(StringComparer.Ordinal));
        h.Unread.Since(C1)!.Value.Seq.Should().Be(first.Ack.Results[0].Seq!.Value);
        h.Unread.Since(C1)!.Value.At.Should().Be(h.Time.GetUtcNow().ToUnixTimeMilliseconds(), "標未讀時閘道的時間");
        h.Counters.Snapshot().Unread.Should().Be(2, "ops/status 的 unread（只有計數）");

        // 已經是未讀：留第一次的 seq
        await h.PublishAsync(device, (K.Attention, Attention("question"), C1));
        await h.RoundAsync();
        h.Unread.Since(C1)!.Value.Seq.Should().Be(first.Ack.Results[0].Seq!.Value);

        await FlushAsync(h.Host);
        UnreadRows(h.Host).Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [C1] = first.Ack.Results[0].Seq!.Value,
            [C2] = first.Ack.Results[1].Seq!.Value,
        }, "sync.db v3 的 conversation_unread（伺服器重啟後還在）");
    }

    [Fact]
    public async Task 沒有瀏覽器連線時_照樣標未讀_之後的init帶未讀()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        await h.PublishAsync(device, (K.Attention, Attention("failed"), C1));

        await h.RunAsync();
        h.Unread.Contains(C1).Should().BeTrue("沒有連線＝沒人看見（取走變更時照標，不丟掉）");

        var (_, init) = await h.ReadyAsync("b1");
        Unread((JArray)init["conversations"]!, C1).Should().BeTrue();
    }

    [Fact]
    public async Task 看得到但沒focus_標未讀_不送notify()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        h.SetState(a, visible: true, [C1], focused: false);
        await h.RunAsync();
        await a.TakeAsync(0);

        await h.PublishAsync(device, (K.Attention, Attention("completed"), C1));
        await h.RoundAsync();

        var messages = await a.TakeAsync(0);
        messages.Select(m => m["type"]!.Value<string>()).Should().NotContain("notify", "看得到（visible＋openConversationIds）就不收 notify（D28）");
        Unread(LastList(messages), C1).Should().BeTrue("沒有 focus 不是看見：未讀留到它取得 focus");

        h.SetState(a, visible: true, [C1], focused: true);
        await h.RunAsync();
        Unread(await NextListAsync(a), C1).Should().BeFalse("取得 focus 就清");
        h.Unread.Count.Should().Be(0);
    }

    [Fact]
    public async Task 開始看見_清未讀_重送清單()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1, "一");
        await h.CreateAsync(device, C2, "二");
        await h.CreateAsync(device, C3, "三");
        var (a, _) = await h.ReadyAsync("b1");
        var (b, _) = await h.ReadyAsync("b2");
        h.SetState(a, visible: true, [], focused: true);
        h.SetState(b, visible: true, [], focused: true);
        await h.PublishAsync(device, (K.Attention, Attention("completed"), C1), (K.Attention, Attention("approval"), C2), (K.Attention, Attention("plan"), C3));
        await h.RoundAsync();
        h.Unread.Ids().Should().Equal(C1, C2, C3);
        await a.TakeAsync(0);
        await b.TakeAsync(0);
        await FlushAsync(h.Host);
        UnreadRows(h.Host).Keys.Should().BeEquivalentTo([C1, C2, C3]);

        // visible 指令：畫面上有它（不必等 50 ms 的節奏）
        (await h.CmdAsync(a, "visible", new { conversationIds = new[] { C1 } })).Should().BeNull();
        await h.RunAsync();
        var items = await NextListAsync(a);
        (Unread(items, C1), Unread(items, C2), Unread(items, C3)).Should().Be((false, true, true));
        Unread(await NextListAsync(b), C1).Should().BeFalse("清單是全站一份：每條連線都重送");

        // 分頁取得 focus：沒有 focus 時不清，取得之後才清
        h.SetState(b, visible: true, [C2], focused: false);
        await h.RunAsync();
        h.Unread.Contains(C2).Should().BeTrue("看得到但沒有 focus 不是看見");
        await a.AssertNothingAsync("未讀沒變：清單不重送");
        h.SetState(b, visible: true, [C2], focused: true);
        await h.RunAsync();
        Unread(await NextListAsync(a), C2).Should().BeFalse();

        // open：只是開著不算看見（同 D25）；網頁接著回報畫面上有它（visible）才清
        await h.CmdAsync(a, "open", new { conversationId = C3 });
        await h.RoundAsync();
        (await a.TakeAsync(0)).Select(m => m["type"]!.Value<string>()).Should().Contain("conversation").And.NotContain("conversations");
        h.Unread.Contains(C3).Should().BeTrue("open 本身不改變看見");
        (await h.CmdAsync(a, "visible", new { conversationIds = new[] { C1, C3 } })).Should().BeNull();
        await h.RunAsync();
        Unread(await NextListAsync(a), C3).Should().BeFalse();
        h.Unread.Count.Should().Be(0);
        h.Counters.Snapshot().Unread.Should().Be(0);

        await FlushAsync(h.Host);
        UnreadRows(h.Host).Should().BeEmpty("清除也寫進 sync.db");
    }

    [Fact]
    public async Task 看見的分頁_不標未讀()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        await h.CreateAsync(device, C2);
        var (a, _) = await h.ReadyAsync("b1");
        h.SetState(a, visible: true, [C1], focused: true);
        var waiting = h.Add("還沒送過init");
        waiting.Connection.SetState(new BrowserClientState(true, true, [C2], 0));
        await h.RunAsync();

        await h.PublishAsync(device, (K.Attention, Attention("completed"), C1), (K.Turn, Completed(), C2));
        await h.RoundAsync();

        h.Unread.Count.Should().Be(0, "openConversationIds 裡有它而且在前景（還沒送過 init 的分頁也在畫面上）");
    }

    [Fact]
    public async Task 對帳重送的舊事件_不標未讀()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.ReadyAsync("b1");
        var log = h.Host.Services.GetRequiredService<EventLog>();
        var hello = new OutboxPosition(4, 1, 1);
        var upstream = new UpstreamContext(device, OutboxRules.ResyncFrom(hello), hello);

        (await log.AppendBatchAsync(upstream, EventLogTests.Batch(
            ConversationEventLogTests.Ev(1, K.Created, ConversationSamples.Meta(), C1),
            ConversationEventLogTests.Ev(2, K.Attention, Attention("completed"), C1),
            ConversationEventLogTests.Ev(3, K.Turn, Completed(), C1)))).Ack.Results.Should().OnlyContain(r => r.Seq != null);
        await h.RoundAsync();
        h.Unread.Count.Should().Be(0, "Hello 之前就在 outbox 的舊事件（還原後重送）不標");

        (await log.AppendBatchAsync(upstream, EventLogTests.Batch(ConversationEventLogTests.Ev(4, K.Attention, Attention("completed"), C1))))
            .Ack.Results.Should().OnlyContain(r => r.Seq != null);
        await h.RoundAsync();
        h.Unread.Contains(C1).Should().BeTrue("Hello 之後才寫的照標");
    }

    [Fact]
    public async Task 重建_不碰未讀()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 2);
        var marked = await h.PublishAsync(device, (K.Attention, Attention("completed"), C1));
        await h.RoundAsync();
        await FlushAsync(h.Host);
        UnreadRows(h.Host).Should().ContainKey(C1);

        await h.Host.Services.GetRequiredService<ConversationRebuild>().RebuildAsync("測試", CancellationToken.None);
        await h.RoundAsync();

        h.Unread.Since(C1)!.Value.Seq.Should().Be(marked.Ack.Results[0].Seq!.Value, "重建的變更不帶 Fresh，也不清未讀");
        await FlushAsync(h.Host);
        UnreadRows(h.Host).Should().Equal(new Dictionary<string, long> { [C1] = marked.Ack.Results[0].Seq!.Value });
        var (_, init) = await h.ReadyAsync("b1");
        Unread((JArray)init["conversations"]!, C1).Should().BeTrue();
    }

    [Fact]
    public async Task 政策降級_未讀留著()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        await h.PublishAsync(device, (K.Attention, Attention("completed"), C1));
        await h.RoundAsync();

        await h.PublishAsync(device, (K.Meta, ConversationSamples.Meta("只同步標題", ConversationSyncPolicies.Metadata), C1));
        await h.RoundAsync();

        h.Unread.Contains(C1).Should().BeTrue("降級只清內容（答 4）：清單照樣列這個對話，未讀的點照畫");
        await FlushAsync(h.Host);
        UnreadRows(h.Host).Should().ContainKey(C1);
    }

    [Fact]
    public async Task 刪除_同交易刪未讀列()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        await h.CreateAsync(device, C2);
        var (a, _) = await h.ReadyAsync("b1");
        await h.PublishAsync(device, (K.Attention, Attention("completed"), C1), (K.Attention, Attention("completed"), C2));
        await h.RoundAsync();
        await FlushAsync(h.Host);
        UnreadRows(h.Host).Keys.Should().BeEquivalentTo([C1, C2]);
        await a.TakeAsync(0);

        await h.PublishAsync(device, (K.Deleted, new JObject(), C1));
        UnreadRows(h.Host).Keys.Should().BeEquivalentTo([C2], "刪除的同一個交易刪掉未讀列（閘道還沒取到變更）");

        await h.RoundAsync();
        h.Unread.Ids().Should().Equal(C2);
        h.Counters.Snapshot().Unread.Should().Be(1);
        (await NextListAsync(a)).Select(i => i["id"]!.Value<string>()).Should().Equal(C2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 變更通知溢出時刪除_整份重讀時補做_未讀拿掉(bool withConnection)
    {
        // 合併前審查 P4SRV-MAT-04：通知滿了被丟掉的 Deleted 不會再取到；不補的話已刪除的對話一直留在集合裡（ops/status 的 unread 多算到重開服務）
        await using var h = await ConversationHarness.StartAsync(settings: Settings(("Hub:Conversations:ChangeFeedMaxEntries", "2")));
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        await h.CreateAsync(device, C2);
        if (withConnection)
            await h.ReadyAsync("b1");
        else
            await h.RunAsync();
        await h.PublishAsync(device, (K.Attention, Attention("completed"), C1));
        await h.RoundAsync();
        h.Unread.Ids().Should().Equal(C1);

        // 通知先排滿（兩則），C1 的刪除被丟掉、只設了整份重讀
        await h.PublishAsync(device, (K.Turn, ConversationEventLogTests.Turn(ConversationTurnPhases.Started), C2), (K.Turn, ConversationEventLogTests.Turn(ConversationTurnPhases.Started), C2));
        await h.PublishAsync(device, (K.Deleted, new JObject(), C1));
        h.Feed.Pending.Should().Be(2, "前提：刪除的變更沒排進去");
        await h.RoundAsync();

        h.Unread.Contains(C1).Should().BeFalse(withConnection ? "閘道整份重讀時照目錄補做" : "沒有連線時取走就丟，同樣補做");
        h.Unread.Count.Should().Be(0);
        h.Counters.Snapshot().Unread.Should().Be(0);
        await FlushAsync(h.Host);
        UnreadRows(h.Host).Should().BeEmpty();
    }

    [Fact]
    public async Task 標記與刪除交錯_不留孤兒列()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        await h.ReadyAsync("b1");

        // 注意事件與刪除都已提交、閘道才取到：標記的寫入排在刪除的交易之後
        await h.PublishAsync(device, (K.Attention, Attention("completed"), C1));
        await h.PublishAsync(device, (K.Deleted, new JObject(), C1));
        await h.RoundAsync();

        h.Unread.Contains(C1).Should().BeFalse("依提交順序：先標、再因為刪除拿掉");
        await FlushAsync(h.Host);
        UnreadRows(h.Host).Should().BeEmpty("標記以 INSERT … SELECT 確認對話沒刪除：不插入孤兒列");

        // 直接標一個已刪除的對話（同樣的寫入）：sync.db 照樣沒有它
        h.Unread.Mark(C1, 99, 1).Should().BeTrue();
        await FlushAsync(h.Host);
        UnreadRows(h.Host).Should().BeEmpty();
    }

    [Fact]
    public async Task 重啟後未讀還在()
    {
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var dir = TempDir.Create();
        try
        {
            await using (var host = await HubTestHost.StartAsync(dataDir: dir, deleteOnDispose: false, clock: new ManualTimeProvider(start)))
            {
                var device = await EventLogTests.AddDeviceAsync(host);
                await ConversationEventLogTests.Append(host, device, ConversationEventLogTests.Ev(1, K.Created, ConversationSamples.Meta()),
                    ConversationEventLogTests.Ev(2, K.Attention, Attention("completed")));
                // 沒有瀏覽器連線：正式的閘道取走變更時照標，寫入不等
                for (var waited = 0; waited < 5000 && UnreadRows(host).Count == 0; waited += 20)
                    await Task.Delay(20);
                UnreadRows(host).Keys.Should().Equal(ConversationSamples.Conv);
            }

            await using (var host = await HubTestHost.StartAsync(dataDir: dir, deleteOnDispose: false, clock: new ManualTimeProvider(start)))
            {
                host.Services.GetRequiredService<ConversationUnread>().Contains(ConversationSamples.Conv).Should().BeTrue("啟動時從 sync.db 載入");
                host.Services.GetRequiredService<ConversationCounters>().Snapshot().Unread.Should().Be(1);
                await using var owner = await LoginAndConnectAsync(host);
                var init = await owner.Hub.ReadyAsync();
                Unread((JArray)init["conversations"]!, ConversationSamples.Conv).Should().BeTrue();
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 集合的版本_每改一次加1_沒改不動()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        await h.CreateAsync(device, C2);
        var start = h.Unread.Version;

        h.Unread.Mark(C1, 5, 50).Should().BeTrue();
        h.Unread.Version.Should().Be(start + 1, "清單的快取看版本決定要不要重組");
        h.Unread.Mark(C1, 6, 60).Should().BeFalse("已經是未讀");
        h.Unread.Version.Should().Be(start + 1);
        h.Unread.Clear(C2).Should().BeFalse("不是未讀");
        h.Unread.Forget(C2).Should().BeFalse();
        h.Unread.Version.Should().Be(start + 1);
        h.Unread.Mark(C2, 7, 70).Should().BeTrue();
        h.Unread.Clear(C1).Should().BeTrue();
        h.Unread.Forget(C2).Should().BeTrue();
        h.Unread.Version.Should().Be(start + 4);
        h.Unread.Count.Should().Be(0);
        h.Counters.Snapshot().Unread.Should().Be(0);
    }

    [Fact]
    public async Task 啟動載入_只載入還在的對話()
    {
        const string device = "d_0000000000000000000000000B";
        var dir = TempDir.Create();
        try
        {
            await using (var host = await HubTestHost.StartAsync(dataDir: dir, deleteOnDispose: false))
            {
                await ConversationEventLogTests.AddFixedDeviceAsync(host, device);
                await ConversationEventLogTests.Append(host, device, ConversationEventLogTests.Ev(1, K.Created, ConversationSamples.Meta(), C1),
                    ConversationEventLogTests.Ev(2, K.Created, ConversationSamples.Meta(), C2), ConversationEventLogTests.Ev(3, K.Deleted, new JObject(), C1));
                // 不該有的列（照理不會出現：刪除的交易刪掉它、標記確認對話還在）直接寫進去
                await host.Services.GetRequiredService<SyncWriter>().WriteAsync(c => c.Execute(
                    "INSERT INTO conversation_unread(conversation_id, since_seq, since_at) VALUES($a, 1, 1), ($b, 2, 2), ($c, 3, 3)",
                    ("$a", C1), ("$b", C2), ("$c", C3)));
            }

            await using (var host = await HubTestHost.StartAsync(dataDir: dir, deleteOnDispose: false))
            {
                var unread = host.Services.GetRequiredService<ConversationUnread>();
                unread.Ids().Should().Equal([C2], "已刪除（C1）與不存在（C3）的對話不載入");
                unread.Since(C2).Should().Be((2L, 2L));
                host.Services.GetRequiredService<ConversationCounters>().Snapshot().Unread.Should().Be(1);
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    // ───── 工具 ─────

    /// <summary>目前收到的訊息裡最後一份清單的 items（要有重送）</summary>
    private static async Task<JArray> NextListAsync(SendRecorder recorder) => LastList(await recorder.TakeAsync(0));

    private static JArray LastList(IReadOnlyList<JObject> messages)
    {
        var list = messages.LastOrDefault(m => m["type"]!.Value<string>() == "conversations");
        list.Should().NotBeNull("未讀改了要重送 conversations；收到的是 " + string.Join(",", messages.Select(m => m["type"])));
        return (JArray)list!["items"]!;
    }

    private static bool Unread(JArray items, string conversationId) =>
        items.Single(i => i["id"]!.Value<string>() == conversationId)["unread"]!.Value<bool>();

    /// <summary>寫入者是先進先出：排一個空的工作、等它提交，之前排的寫入（未讀的標記與清除）都已提交</summary>
    private static Task FlushAsync(HubTestHost host) => host.Services.GetRequiredService<SyncWriter>().WriteAsync(_ => { });

    /// <summary>sync.db 的 conversation_unread：對話 id → since_seq</summary>
    private static Dictionary<string, long> UnreadRows(HubTestHost host) =>
        ConversationEventLogTests.Rows(host, "SELECT conversation_id, since_seq FROM conversation_unread")
            .ToDictionary(r => (string)r[0]!, r => (long)r[1]!, StringComparer.Ordinal);
}
