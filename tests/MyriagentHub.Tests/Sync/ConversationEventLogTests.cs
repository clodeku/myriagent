using System.Text.Json;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using static MyriagentHub.Tests.Sync.ConversationSamples;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 對話事件的接收與物化（WP4.2 計畫步 3；R1、R3、R5～R12、D3、D4、D5、D8、D9、D12、D13、D16、D37、D44）：以 EventLogTests 的
/// <c>AppendAsync</c>／<c>Batch</c> 直接驅動 <see cref="EventLog.AppendBatchAsync"/>（F46），斷言 BatchAck、events、rejected_events 與三張物化表。
/// 權杖一律在執行時組字串產生（<see cref="ConversationSamples"/>）
/// </summary>
public sealed partial class ConversationEventLogTests(ITestOutputHelper output)
{
    // ───── 同一交易與授權 ─────

    [Fact]
    public async Task 同一批先created再item_都接受()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);

        var outcome = await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()));

        outcome.Ack.Error.Should().BeNull();
        outcome.Ack.Results.Should().HaveCount(2).And.OnlyContain(r => r.Seq != null && r.Rejected == null, "擁有者檢查經交易查表：同一個交易剛建立的列看得到");
        Text(host, $"SELECT owner_device_id FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(a);
        EventLogTests.Count(host, $"SELECT item_count FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(1);
        EventLogTests.Count(host, $"SELECT COUNT(*) FROM items WHERE conversation_id = '{Conv}'").Should().Be(1);
        EventLogTests.Count(host, $"SELECT last_seq FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(outcome.Ack.Results[1].Seq!.Value);
    }

    [Fact]
    public async Task B寫A的對話_not_owner_帶Owner()
    {
        // 也是重新配對成新身分之後的情況（答 8）：舊身分的對話擁有者不變，新身分送的一律 not_owner
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var b = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()));

        var outcome = await Append(host, b, Ev(1, K.Item, Item()), Ev(2, K.Meta, Meta("改名")), Ev(3, K.Queue, Queue("排隊")));

        outcome.Ack.Results.Should().OnlyContain(r => r.Rejected == ConversationRejectCodes.NotOwner && r.Owner == a && r.Epoch == null && r.Seq == null);
        Text(host, $"SELECT title FROM conversations WHERE conversation_id = '{Conv}'").Should().Be("標題");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM items").Should().Be(0);
        EventLogTests.Count(host, $"SELECT COUNT(*) FROM rejected_events WHERE author = '{b}' AND reason = 'not_owner' AND payload IS NOT NULL")
            .Should().Be(3, "原文 DLP 乾淨：照存供鑑識");
    }

    [Fact]
    public async Task B送A的id的created_not_owner()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var b = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()));

        var outcome = await Append(host, b, Ev(1, K.Created, Meta("搶")));

        outcome.Ack.Results.Single().Should().Be(new BatchEventResult(1, Rejected: ConversationRejectCodes.NotOwner, Owner: a));
        Text(host, $"SELECT owner_device_id || '|' || origin_device_id || '|' || title FROM conversations WHERE conversation_id = '{Conv}'").Should().Be($"{a}|{a}|標題");
    }

    [Fact]
    public async Task epoch2_fenced_帶Epoch1()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()));

        var outcome = await Append(host, a, Ev(2, K.Item, Item(), epoch: 2), Ev(3, K.Created, Meta("新世代"), epoch: 2));

        outcome.Ack.Results.Should().OnlyContain(r => r.Rejected == ConversationRejectCodes.Fenced && r.Epoch == 1 && r.Owner == null);
        EventLogTests.Count(host, $"SELECT epoch FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(1);
    }

    [Fact]
    public async Task 刪除之後_deleted()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()));

        var deleted = await Append(host, a, Ev(3, K.Deleted, new JObject()));
        deleted.Ack.Results.Single().Seq.Should().NotBeNull("{} 的 conv.deleted 就是真正的刪除（提案 C10）");
        var after = await Append(host, a, Ev(4, K.Item, Item()), Ev(5, K.Created, Meta()), Ev(6, K.Deleted, new JObject()), Ev(7, K.Turn, Turn("started")));

        after.Ack.Results.Should().OnlyContain(r => r.Rejected == ConversationRejectCodes.Deleted, "連擁有者再送 created 也是 deleted：不能再改回同步（答 4）");
        Text(host, $"SELECT deleted_by FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(a);
        EventLogTests.Count(host, $"SELECT deleted_at FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(host.Clock.NowMs);
        EventLogTests.Count(host, $"SELECT last_seq FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(deleted.Ack.Results[0].Seq!.Value);
    }

    [Fact]
    public async Task 大寫id_invalid()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var upper = Conv.ToUpperInvariant();

        var outcome = await Append(host, a, Event(K.Created, Json(Meta()), upper, deviceSeq: 1));

        outcome.Ack.Results.Single().Rejected.Should().Be(SyncErrorCodes.Invalid);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM conversations").Should().Be(0);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM rejected_events WHERE payload IS NULL").Should().Be(1, "還沒跑到 DLP 的拒絕不存原文（D8）");
    }

    [Fact]
    public async Task 串流與id不符_invalid()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);

        var outcome = await Append(host, a, Event(K.Created, Json(Meta()), Conv, stream: SyncStreams.Conversation(OtherConv), deviceSeq: 1));

        outcome.Ack.Results.Single().Rejected.Should().Be(SyncErrorCodes.Invalid);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM conversations").Should().Be(0);
    }

    // ───── 第二道 DLP、再遮蔽與被拒的保存（答 5、D8、D44） ─────

    [Fact]
    public async Task 命中_遮蔽後接受_存遮蔽版_sha是裝置的()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var token = GitHub();
        var item = Ev(2, K.Item, Item("金鑰是 " + token + " 請勿外流", ord: 7, ver: 3));

        var outcome = await Append(host, a, Ev(1, K.Created, Meta()), item);

        var seq = outcome.Ack.Results[1].Seq;
        seq.Should().NotBeNull("命中時伺服器遮蔽後接受，不回 dlp（答 5）");
        outcome.Ack.Results[1].Rejected.Should().BeNull();
        var row = Rows(host, $"SELECT enc, payload, lower(hex(payload_sha256)) FROM events WHERE seq = {seq}").Single();
        var stored = Decode((string)row[0]!, (string)row[1]!);
        stored.Should().NotContain(token[4..20]).And.Contain(CredentialLeakRules.Replacement);
        row[2].Should().Be(item.PayloadSha256, "冪等身分是裝置送的原文雜湊（F50）");
        var body = Rows(host, $"SELECT enc, body, ver, ord FROM items WHERE conversation_id = '{Conv}' AND item_id = 'item1'").Single();
        Decode((string)body[0]!, (string)body[1]!).Should().Be(stored, "物化的是存下的版本");
        body[2].Should().Be(3L);
        body[3].Should().Be(7L, "ver、ord 不變");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM rejected_events").Should().Be(0);
        host.Services.GetRequiredService<ConversationCounters>().Snapshot().RemaskedEvents.Should().Be(1);
    }

    [Fact]
    public async Task 沒命中_存原本的位元組()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        // 一筆小的（json）、一筆 8 KB 以上的（gzip）：都和裝置送的逐位元組相同
        var small = Ev(2, K.Item, Item("短的"));
        var large = Ev(3, K.Item, Item(new string('長', 6000), id: "item2", ord: 2));
        large.Enc.Should().Be(PayloadRules.EncGzip);

        var outcome = await Append(host, a, Ev(1, K.Created, Meta()), small, large);

        outcome.Ack.Results.Should().OnlyContain(r => r.Seq != null);
        foreach (var (e, result) in new[] { (small, outcome.Ack.Results[1]), (large, outcome.Ack.Results[2]) })
        {
            var row = Rows(host, $"SELECT enc, payload FROM events WHERE seq = {result.Seq}").Single();
            row[0].Should().Be(e.Enc);
            row[1].Should().Be(e.Payload, "沒命中時存裝置送的原始位元組（SP 5.4）");
        }
        var body = Rows(host, $"SELECT enc, body FROM items WHERE conversation_id = '{Conv}' AND item_id = 'item2'").Single();
        body[0].Should().Be(large.Enc);
        body[1].Should().Be(large.Payload);
        host.Services.GetRequiredService<ConversationCounters>().Snapshot().RemaskedEvents.Should().Be(0);
    }

    [Fact]
    public async Task 遮蔽後同一筆重送_Duplicate回原seq()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var batch = EventLogTests.Batch(Ev(1, K.Created, Meta("標題 " + GitHub())), Ev(2, K.Item, Item("輸出 " + GitHub('b'))));

        var first = await EventLogTests.AppendAsync(host, a, batch);
        var again = await EventLogTests.AppendAsync(host, a, batch);

        first.Ack.Results.Should().OnlyContain(r => r.Seq != null);
        again.Ack.Should().BeEquivalentTo(first.Ack, "存的是遮蔽版、雜湊是裝置送的：重送判成 Duplicate");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM events WHERE author <> 'server'").Should().Be(2);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM rejected_events").Should().Be(0);
    }

    [Fact]
    public async Task 遮蔽後仍命中_invalid_payload是NULL()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()));
        ConversationPrecheckRules.RemaskForTest = (_, payload) => Json(payload);
        BatchOutcome outcome;
        try
        {
            outcome = await Append(host, a, Ev(2, K.Item, Item("又是 " + GitHub())));
        }
        finally
        {
            ConversationPrecheckRules.RemaskForTest = null;
        }

        outcome.Ack.Results.Single().Rejected.Should().Be(SyncErrorCodes.Invalid);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM rejected_events WHERE device_seq = 2 AND payload IS NULL").Should().Be(1, "原文命中：payload 不存（D8）");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM items").Should().Be(0);
    }

    [Fact]
    public async Task 原文命中又not_owner_payload是NULL()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var b = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()));

        var outcome = await Append(host, b, Ev(1, K.Item, Item("有 " + GitHub())), Ev(2, K.Item, Item("乾淨的")));

        outcome.Ack.Results.Should().OnlyContain(r => r.Rejected == ConversationRejectCodes.NotOwner);
        Rows(host, $"SELECT device_seq, payload IS NULL FROM rejected_events WHERE author = '{b}' ORDER BY device_seq")
            .Select(r => ((long)r[0]!, (long)r[1]!)).Should().Equal((1L, 1L), (2L, 0L));
    }

    [Fact]
    public async Task Validate不合格而且有權杖_invalid且payload是NULL()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()));
        var bad = Item("有 " + GitHub());
        bad["status"] = "Bogus";

        var outcome = await Append(host, a, Ev(2, K.Item, bad));

        outcome.Ack.Results.Single().Rejected.Should().Be(SyncErrorCodes.Invalid);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM rejected_events WHERE device_seq = 2 AND payload IS NULL").Should().Be(1);
    }

    [Fact]
    public async Task Validate不合格而且乾淨_payload照存()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()));
        var bad = Item("沒有權杖");
        bad["status"] = "Bogus";
        var e = Ev(2, K.Item, bad);

        var outcome = await Append(host, a, e);

        outcome.Ack.Results.Single().Rejected.Should().Be(SyncErrorCodes.Invalid);
        Rows(host, "SELECT payload, enc FROM rejected_events WHERE device_seq = 2").Single().Should().Equal(e.Payload, e.Enc);
    }

    [Fact]
    public async Task 建構式語法的權杖_invalid_不存進任何表()
    {
        // 合併前審查 DLP-CTOR-1：權杖寫成 new 名稱(…) 的建構式名稱（Validate 合格的 x、不合格的 text）
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()));
        var key = AwsAccessKey();
        var before = EventLogTests.Count(host, "SELECT COUNT(*) FROM events");

        var outcome = await Append(host, a,
            Event(K.Item, RawItem("\"hi\"", ",\"x\":new " + key + "(1)"), deviceSeq: 2),
            Event(K.Item, RawItem("new " + key + "()"), deviceSeq: 3));

        outcome.Ack.Results.Should().OnlyContain(r => r.Rejected == SyncErrorCodes.Invalid && r.Seq == null);
        Rows(host, "SELECT device_seq, payload IS NULL FROM rejected_events ORDER BY device_seq").Select(r => ((long)r[0]!, (long)r[1]!))
            .Should().Equal((2L, 1L), (3L, 1L));
        EventLogTests.Count(host, "SELECT COUNT(*) FROM events").Should().Be(before, "沒有新的事件");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM items").Should().Be(0);
        foreach (var table in new[] { "events", "rejected_events" })
            EventLogTests.Count(host, $"SELECT COUNT(*) FROM {table} WHERE payload LIKE '%{key[4..]}%'").Should().Be(0, table);
    }

    [Fact]
    public async Task 稽核每台每60秒一筆_不含id與權杖()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var token = GitHub('q');
        var events = new List<SyncEvent> { Ev(1, K.Created, Meta()) };
        for (var i = 0; i < 10; i++)
            events.Add(Ev(events.Count + 1, K.Item, Item("輸出 " + token, id: "r" + i, ord: i + 1)));
        for (var i = 0; i < 30; i++)
        {
            var bad = Item("壞的 " + token, id: "x" + i);
            bad["status"] = "Bogus";
            events.Add(Ev(events.Count + 1, K.Item, bad));
        }

        var outcome = await Append(host, a, [.. events]);
        outcome.Ack.Results.Count(r => r.Rejected == SyncErrorCodes.Invalid).Should().Be(30);
        var audit = host.Services.GetRequiredService<ConversationRejectAudit>();
        await audit.FlushAsync();
        await audit.FlushAsync();

        var rows = await AuditRowsAsync(host);
        var conversationRows = rows.Where(r => r.Action is AuditActions.SyncConversationRejected or AuditActions.SyncConversationRemasked).ToList();
        conversationRows.Should().ContainSingle("同一台在一個時間窗只寫一筆（拒收與再遮蔽合成一筆）");
        var row = conversationRows[0];
        row.Action.Should().Be(AuditActions.SyncConversationRejected, "窗內有拒收");
        row.ActorId.Should().Be(a);
        row.Target.Should().BeNull();
        using var detail = JsonDocument.Parse(row.Detail!);
        detail.RootElement.GetProperty("rejected").GetProperty("invalid").GetProperty(K.Item).GetInt32().Should().Be(30);
        detail.RootElement.GetProperty("remasked").GetProperty(K.Item).GetInt32().Should().Be(10);
        foreach (var r in rows)
        {
            var text = string.Join("|", r.ActorId, r.Target, r.Detail);
            text.Should().NotContain(Conv, "稽核不含對話 id");
            text.Should().NotContain(token[4..20], "稽核不含內容");
        }
    }

    [Fact]
    public async Task 只有再遮蔽_稽核的action是remasked()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item("輸出 " + GitHub())));
        await host.Services.GetRequiredService<ConversationRejectAudit>().FlushAsync();

        var row = (await AuditRowsAsync(host)).Single(r => r.Action is AuditActions.SyncConversationRejected or AuditActions.SyncConversationRemasked);
        row.Action.Should().Be(AuditActions.SyncConversationRemasked);
        row.Outcome.Should().Be(AuditOutcomes.Ok);
    }

    [Fact]
    public async Task 記錄裡沒有權杖()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await HubTestHost.StartAsync(services: s => s.AddSingleton<ILoggerProvider>(logs));
        var a = await EventLogTests.AddDeviceAsync(host);
        var b = await EventLogTests.AddDeviceAsync(host);
        var token = GitHub('z');
        var materializer = host.Services.GetRequiredService<ConversationMaterializer>();
        // 毒事件的例外訊息故意帶權杖：記錄只能記型別，不能記訊息
        materializer.FaultForTest = e =>
        {
            if (e.DeviceSeq == 4)
                throw new InvalidCastException("內容 " + token);
        };
        var invalid = Item("壞的 " + token, id: "bad");
        invalid["status"] = "Bogus";

        await Append(host, a, Ev(1, K.Created, Meta("標題 " + token)), Ev(2, K.Item, Item("接受 " + token)), Ev(3, K.Item, invalid), Ev(4, K.Item, Item("毒 " + token, id: "p")));
        await Append(host, b, Ev(1, K.Item, Item("不是擁有者 " + token)));
        ConversationPrecheckRules.RemaskForTest = (_, payload) => Json(payload);
        try
        {
            await Append(host, a, Ev(5, K.Item, Item("遮不掉 " + token)));
        }
        finally
        {
            ConversationPrecheckRules.RemaskForTest = null;
        }
        await host.Services.GetRequiredService<ConversationRejectAudit>().FlushAsync();
        materializer.FaultForTest = null;

        logs.Entries.Should().Contain(l => l.Level == LogLevel.Error && l.Text.Contains(nameof(InvalidCastException)), "確認收得到記錄：毒事件記了一筆錯誤");
        logs.AllText().Should().NotContain(token[4..20]);
        Rows(host, "SELECT COUNT(*) FROM events WHERE payload LIKE '%" + token[4..20] + "%'").Single()[0].Should().Be(0L, "資料庫裡也沒有");
        Rows(host, "SELECT COUNT(*) FROM rejected_events WHERE payload LIKE '%" + token[4..20] + "%'").Single()[0].Should().Be(0L);
    }

    [Fact]
    public async Task Pull拿到遮蔽版()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var token = GitHub();
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item("輸出 " + token)));

        var pulled = host.Services.GetRequiredService<EventLog>().Pull(new PullRequest([new PullCursor(0, Stream: SyncStreams.Conversation(Conv))], 100, 1_000_000));

        pulled.Events.Should().HaveCount(2);
        var item = pulled.Events.Single(e => e.Kind == K.Item);
        Decode(item.Enc, item.Payload).Should().NotContain(token[4..20]).And.Contain(CredentialLeakRules.Replacement);
        item.ConversationId.Should().Be(Conv);
        item.Epoch.Should().Be(1);
    }

    [Fact]
    public async Task Pull的大事件_每個游標只讀到位元組上限_回應同讀滿()
    {
        // 合併前審查 P4SRV-MAT-01：每個游標原本都把最多 maxEvents＋1 筆完整的 payload 讀進記憶體；大的 conv.item（壓不太下去的內容）一個游標就上百 MB
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var rng = new Random(4242);
        var events = new List<SyncEvent> { Ev(1, K.Created, Meta()) };
        for (var i = 1; i <= 60; i++)
        {
            // 只有小寫字母的亂數：gzip 壓不太下去，也不會湊出任何權杖的樣式
            var text = new string(Enumerable.Range(0, 20_000).Select(_ => (char)('a' + rng.Next(26))).ToArray());
            events.Add(Ev(i + 1, K.Item, Item(text, id: "item" + i, ord: i)));
        }
        foreach (var chunk in events.Chunk(20))
            (await Append(host, a, chunk)).Ack.Results.Should().OnlyContain(r => r.Seq != null && r.Rejected == null);

        var log = host.Services.GetRequiredService<EventLog>();
        var stored = Rows(host, $"SELECT seq, cls, length(CAST(payload AS BLOB)) FROM events WHERE stream = '{SyncStreams.Conversation(Conv)}' ORDER BY seq")
            .Select(r => (Seq: (long)r[0]!, Cls: (string)r[1]!, Size: (long)r[2]! + EventLog.PulledEventOverheadBytes)).ToList();
        stored.Should().HaveCount(61);
        stored.Skip(1).Should().OnlyContain(e => e.Size > 10_000, "前提：每筆存下的 payload 都不小");
        const int maxBytes = 50_000;
        // 兩個重疊的游標：串流與 cls=meta（created 在兩邊都有）
        var cursors = new[] { new PullCursor(0, Stream: SyncStreams.Conversation(Conv)), new PullCursor(0, Cls: SyncClasses.Meta) };
        var before = log.PulledRows;

        var pulled = log.Pull(new PullRequest(cursors, 50, maxBytes));

        var read = log.PulledRows - before;
        read.Should().BeLessThanOrEqualTo(2 + 6, "串流的游標讀到累計超過上限的那一筆就停（原本讀 51 筆）；meta 的游標只有 1 筆");
        // 讀滿時的答案：串流游標的前 51 筆加 meta 游標（同一筆 created）依 seq 合併後取前綴
        var expected = stored.Take(51).ToList();
        var count = PullRules.SelectPrefix(expected.Select(e => e.Size), 50, maxBytes);
        pulled.Events.Select(e => e.Seq).Should().Equal(expected.Take(count).Select(e => e.Seq));
        pulled.Events.Should().HaveCountGreaterThan(1).And.HaveCountLessThan(50, "前提：是位元組上限擋下的");
        pulled.More.Should().BeTrue();

        // 一路拉完：游標照每筆的 cls／stream 前進，不跳過、不重複
        var seen = new List<long>();
        var streamAfter = 0L;
        var metaAfter = 0L;
        for (var guard = 0; guard < 100; guard++)
        {
            var page = log.Pull(new PullRequest([new PullCursor(streamAfter, Stream: SyncStreams.Conversation(Conv)), new PullCursor(metaAfter, Cls: SyncClasses.Meta)], 50, maxBytes));
            foreach (var e in page.Events)
            {
                if (!seen.Contains(e.Seq))
                    seen.Add(e.Seq);
                if (e.Stream == SyncStreams.Conversation(Conv))
                    streamAfter = Math.Max(streamAfter, e.Seq);
                if (e.Cls == SyncClasses.Meta)
                    metaAfter = Math.Max(metaAfter, e.Seq);
            }
            if (!page.More)
                break;
        }
        seen.Should().Equal(stored.Select(e => e.Seq));
    }

    // ───── 項目、佇列與一輪 ─────

    [Fact]
    public async Task item依seq整筆取代_ver較小也取代()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);

        var outcome = await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item("第一份", "Running", ver: 5)), Ev(3, K.Item, Item("第二份", "Done", ver: 2)));

        var row = Rows(host, $"SELECT ver, status, final, last_seq, enc, body FROM items WHERE conversation_id = '{Conv}' AND item_id = 'item1'").Single();
        row[0].Should().Be(2L, "持久的 conv.item 不比 ver（D9）");
        row[1].Should().Be("Done");
        row[2].Should().Be(1L);
        row[3].Should().Be(outcome.Ack.Results[2].Seq!.Value);
        Decode((string)row[4]!, (string)row[5]!).Should().Contain("第二份");
        EventLogTests.Count(host, $"SELECT item_count FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(1);
    }

    [Fact]
    public async Task itemRemoved後再送同id_removed歸0_item_count正確()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        long ItemCount() => EventLogTests.Count(host, $"SELECT item_count FROM conversations WHERE conversation_id = '{Conv}'");
        long Removed() => EventLogTests.Count(host, $"SELECT removed FROM items WHERE conversation_id = '{Conv}' AND item_id = 'item1'");

        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Item, Item(id: "item2", ord: 2)));
        ItemCount().Should().Be(2);
        await Append(host, a, Ev(4, K.ItemRemoved, Removal("item1")));
        (ItemCount(), Removed()).Should().Be((1L, 1L));
        await Append(host, a, Ev(5, K.ItemRemoved, Removal("item1")));
        (ItemCount(), Removed()).Should().Be((1L, 1L), "已經移除過的不再減");
        await Append(host, a, Ev(6, K.Item, Item("回來了")));
        (ItemCount(), Removed()).Should().Be((2L, 0L));
        EventLogTests.Count(host, $"SELECT max_ord FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(2);
    }

    [Fact]
    public async Task itemRemoved不存在的項目_照收_不新增列()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);

        var outcome = await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.ItemRemoved, Removal("ghost")));

        outcome.Ack.Results[1].Seq.Should().NotBeNull("提案 C5：接受、不做任何事");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM items").Should().Be(0);
        EventLogTests.Count(host, $"SELECT item_count FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(0);
        EventLogTests.Count(host, $"SELECT last_seq FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(outcome.Ack.Results[1].Seq!.Value);
    }

    [Fact]
    public async Task queue整份取代_version加1()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var second = Queue("乙", "丙");

        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Queue, Queue("甲")));
        EventLogTests.Count(host, $"SELECT version FROM conversation_runtime WHERE conversation_id = '{Conv}'").Should().Be(1);
        await Append(host, a, Ev(3, K.Queue, second));

        Rows(host, $"SELECT queue_json, pending_attachments_json, version, busy FROM conversation_runtime WHERE conversation_id = '{Conv}'").Single()
            .Should().Equal(Json(second), "[]", 2L, 0L);
    }

    [Fact]
    public async Task turn_started設running_completed記last_turn()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var completed = Turn("completed");

        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Turn, Turn("started")));
        Rows(host, $"SELECT running, turn_started_at FROM conversations WHERE conversation_id = '{Conv}'").Single().Should().Equal(1L, host.Clock.NowMs);
        await Append(host, a, Ev(3, K.Turn, completed), Ev(4, K.Attention, new JObject { ["kind"] = "completed" }));

        Rows(host, $"SELECT running, last_turn FROM conversations WHERE conversation_id = '{Conv}'").Single().Should().Equal(0L, Json(completed));
    }

    [Fact]
    public async Task metadata政策下item_invalid()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta(policy: ConversationSyncPolicies.Metadata)), Ev(2, K.Created, Meta(policy: ConversationSyncPolicies.Paused), conversationId: OtherConv));

        var outcome = await Append(host, a,
            Ev(3, K.Item, Item()), Ev(4, K.Queue, Queue("甲")), Ev(5, K.ItemRemoved, Removal("item1")),
            Ev(6, K.Item, Item(), conversationId: OtherConv),
            Ev(7, K.Meta, Meta("只同步標題", ConversationSyncPolicies.Metadata)), Ev(8, K.Turn, Turn("started")));

        outcome.Ack.Results.Take(4).Should().OnlyContain(r => r.Rejected == SyncErrorCodes.Invalid, "item 類在 metadata／paused 下不收（R8）");
        outcome.Ack.Results.Skip(4).Should().OnlyContain(r => r.Seq != null, "meta 類照收");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM items").Should().Be(0);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM conversation_runtime").Should().Be(0);
    }

    // ───── 建立與冪等 ─────

    [Fact]
    public async Task 未知對話的meta當建立_未知對話的item_invalid()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);

        var outcome = await Append(host, a, Ev(1, K.Meta, Meta("從 meta 建立")), Ev(2, K.Item, Item(), conversationId: OtherConv));

        outcome.Ack.Results[0].Seq.Should().NotBeNull();
        outcome.Ack.Results[1].Rejected.Should().Be(SyncErrorCodes.Invalid);
        Text(host, $"SELECT owner_device_id || '|' || origin_device_id || '|' || title FROM conversations WHERE conversation_id = '{Conv}'").Should().Be($"{a}|{a}|從 meta 建立");
        EventLogTests.Count(host, $"SELECT COUNT(*) FROM conversations WHERE conversation_id = '{OtherConv}'").Should().Be(0);
    }

    [Theory]
    [InlineData(K.Turn)]
    [InlineData(K.Attention)]
    [InlineData(K.Deleted)]
    [InlineData(K.ItemRemoved)]
    [InlineData(K.Queue)]
    public async Task 未知對話的turn_attention_deleted_invalid_不建立列(string kind)
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        JObject payload = kind switch
        {
            K.Turn => Turn("started"),
            K.Attention => new JObject { ["kind"] = "question" },
            K.Deleted => new JObject(),
            K.ItemRemoved => Removal("item1"),
            _ => Queue("甲"),
        };

        var outcome = await Append(host, a, Ev(1, kind, payload));

        outcome.Ack.Results.Single().Rejected.Should().Be(SyncErrorCodes.Invalid, "P2：未知對話只有 created／meta 會建立");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM conversations").Should().Be(0, "不建立列（未知對話的 conv.deleted 也不建立墓碑）");
    }

    [Fact]
    public async Task 擁有者再送created_meta整份取代_owner與created_at不變()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var first = Meta("第一版");
        first["createdAt"] = 1_700_000_000_000L;
        var second = Meta("第二版");
        second["createdAt"] = 1_710_000_000_000L;
        second["updatedAt"] = 1_710_000_000_000L;
        second["model"] = "sonnet";

        var outcome = await Append(host, a, Ev(1, K.Created, first), Ev(2, K.Created, second));

        outcome.Ack.Results.Should().OnlyContain(r => r.Seq != null, "P3、提案 C3：同作者同 epoch 的 created 是整份取代、不拒絕");
        Rows(host, $"SELECT owner_device_id, origin_device_id, created_at, updated_at, title, meta_json, last_seq FROM conversations WHERE conversation_id = '{Conv}'").Single()
            .Should().Equal(a, a, 1_700_000_000_000L, 1_710_000_000_000L, "第二版", Json(second), outcome.Ack.Results[1].Seq!.Value);
    }

    [Fact]
    public async Task 顯示時間不晚於收到的時間_沒帶時間用收到的時間()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var future = Meta();
        future["createdAt"] = host.Clock.NowMs + 86_400_000;
        future["updatedAt"] = host.Clock.NowMs + 86_400_000;
        var none = Meta();
        none.Remove("createdAt");
        none.Remove("updatedAt");

        await Append(host, a, Ev(1, K.Created, future), Ev(2, K.Created, none, conversationId: OtherConv));

        Rows(host, $"SELECT created_at, updated_at FROM conversations WHERE conversation_id = '{Conv}'").Single().Should().Equal(host.Clock.NowMs, host.Clock.NowMs);
        Rows(host, $"SELECT created_at, updated_at FROM conversations WHERE conversation_id = '{OtherConv}'").Single().Should().Equal(host.Clock.NowMs, host.Clock.NowMs);
    }

    [Fact]
    public async Task 任何深度的path鍵_invalid()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var top = Item(id: "a");
        top["Path"] = "C:/secret.txt";
        var plan = Item(id: "b");
        plan["plan"] = new JArray(new JObject { ["text"] = "步驟", ["status"] = "done", ["PATH"] = "C:/x" });
        var deep = Item(id: "c");
        deep["questions"] = new JArray(new JObject
        {
            ["id"] = "q", ["text"] = "選", ["multiSelect"] = false, ["allowOther"] = false, ["isSecret"] = false,
            ["options"] = new JArray(new JObject { ["label"] = "甲", ["extra"] = new JObject { ["path"] = "C:/y" } }),
        });

        var outcome = await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, top), Ev(3, K.Item, plan), Ev(4, K.Item, deep));

        outcome.Ack.Results.Skip(1).Should().OnlyContain(r => r.Rejected == SyncErrorCodes.Invalid, "不分大小寫、任何深度（原樣轉發不能帶本機路徑）");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM items").Should().Be(0);
    }

    [Fact]
    public async Task 重送同一deviceSeq_回原結果不重套()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var batch = EventLogTests.Batch(Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Queue, Queue("甲")));
        var first = await EventLogTests.AppendAsync(host, a, batch);
        var lastSeq = EventLogTests.Count(host, $"SELECT last_seq FROM conversations WHERE conversation_id = '{Conv}'");

        var again = await EventLogTests.AppendAsync(host, a, batch);

        again.Ack.Should().BeEquivalentTo(first.Ack);
        EventLogTests.Count(host, $"SELECT last_seq FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(lastSeq);
        EventLogTests.Count(host, $"SELECT version FROM conversation_runtime WHERE conversation_id = '{Conv}'").Should().Be(1, "不重套");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM events WHERE author <> 'server'").Should().Be(3);
    }

    [Fact]
    public async Task 未知對話的item_重送同一deviceSeq_回同一結果不新增rejected列()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var batch = EventLogTests.Batch(Ev(1, K.Item, Item()));

        var first = await EventLogTests.AppendAsync(host, a, batch);
        var again = await EventLogTests.AppendAsync(host, a, batch);

        first.Ack.Results.Single().Rejected.Should().Be(SyncErrorCodes.Invalid);
        again.Ack.Should().BeEquivalentTo(first.Ack, "P2：伺服器端的冪等");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM rejected_events").Should().Be(1);
    }

    // ───── 毒事件與預先檢查的位置（D37、D3） ─────

    [Fact]
    public async Task 毒事件_同批其他事件照收_連線不中止()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var materializer = host.Services.GetRequiredService<ConversationMaterializer>();
        materializer.FaultForTest = e =>
        {
            if (e.DeviceSeq == 2)
                throw new InvalidCastException("壞的資料");
            if (e.DeviceSeq == 5)
                throw new SqliteException("database is locked", 5);
        };

        var outcome = await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Item, Item(id: "item2", ord: 2)));

        outcome.Ack.Error.Should().BeNull();
        outcome.Ack.Results.Select(r => r.Rejected).Should().Equal(null, SyncErrorCodes.Invalid, null);
        outcome.Ack.Results[2].Seq.Should().NotBeNull();
        EventLogTests.Count(host, "SELECT COUNT(*) FROM events WHERE device_seq = 2").Should().Be(0, "毒事件連同 events 那一筆一起回滾");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM rejected_events WHERE device_seq = 2 AND reason = 'invalid' AND payload IS NOT NULL").Should().Be(1);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM items").Should().Be(1);

        // 環境的錯誤（busy）照舊往外丟：整批不生效，由幫浦中斷連線（D37）
        await FluentActions.Awaiting(() => Append(host, a, Ev(4, K.Item, Item(id: "item3", ord: 3)), Ev(5, K.Item, Item(id: "item4", ord: 4))))
            .Should().ThrowAsync<SqliteException>();
        EventLogTests.Count(host, $"SELECT next_device_seq FROM device_streams WHERE device_id = '{a}'").Should().Be(4);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM items").Should().Be(1);
        materializer.FaultForTest = null;
        (await Append(host, a, Ev(4, K.Item, Item(id: "item3", ord: 3)))).Ack.Results.Single().Seq.Should().NotBeNull("之後照常");
    }

    [Fact]
    public async Task 預先檢查不在寫入執行緒()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var writer = host.Services.GetRequiredService<SyncWriter>();
        var writerThread = await writer.WriteAsync(_ => (Id: Environment.CurrentManagedThreadId, Name: Thread.CurrentThread.Name));
        (int Id, string? Name)? seen = null;
        // 注入的再遮蔽加上記號：存下的是呼叫端算好的結果（寫入者裡重算一次的話——不論 AsyncLocal 流不流得進去——不是少了記號、就是 seen 變成寫入者）
        const string marker = "（呼叫端算的）";
        ConversationPrecheckRules.RemaskForTest = (kind, payload) =>
        {
            seen = (Environment.CurrentManagedThreadId, Thread.CurrentThread.Name);
            return ConversationRemaskRules.Remask(kind, payload).Replace(CredentialLeakRules.Replacement, CredentialLeakRules.Replacement + marker, StringComparison.Ordinal);
        };
        BatchOutcome outcome;
        try
        {
            outcome = await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item("有 " + GitHub())));
        }
        finally
        {
            ConversationPrecheckRules.RemaskForTest = null;
        }

        outcome.Ack.Results.Should().OnlyContain(r => r.Seq != null);
        writerThread.Name.Should().Be("sync.db writer");
        seen.Should().NotBeNull("在呼叫端的流程上做（AsyncLocal 的注入點流得過去）");
        seen!.Value.Id.Should().NotBe(writerThread.Id);
        seen.Value.Name.Should().NotBe(writerThread.Name);
        var body = Rows(host, $"SELECT enc, body FROM items WHERE conversation_id = '{Conv}' AND item_id = 'item1'").Single();
        Decode((string)body[0]!, (string)body[1]!).Should().Contain(CredentialLeakRules.Replacement + marker, "寫入者用的是呼叫端的預先檢查結果，不重算");
    }

    // ───── 帳戶、目錄與功能 ─────

    [Fact]
    public async Task logical_account_id_依link對應()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await EventLogTests.AppendAsync(host, a, EventLogTests.Batch(EventLogTests.Ev(1, SyncEventKinds.LinkUpsert, EventLogTests.Link("acc-1", "claude:org1:a@example.com"))));
        var logical = Text(host, $"SELECT logical_account_id FROM account_links WHERE device_id = '{a}' AND local_account_id = 'acc-1'");
        var unknown = Meta();
        unknown["localAccountId"] = "acc-9";

        await Append(host, a, Ev(2, K.Created, Meta()), Ev(3, K.Created, unknown, conversationId: OtherConv));

        logical.Should().StartWith(SyncIds.LogicalAccountPrefix);
        Text(host, $"SELECT logical_account_id FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(logical);
        Rows(host, $"SELECT logical_account_id FROM conversations WHERE conversation_id = '{OtherConv}'").Single()[0].Should().BeNull("沒有對應的本機帳戶");
    }

    [Fact]
    public async Task Find_交易內看不到_提交後看得到()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var directory = host.Services.GetRequiredService<IConversationDirectory>();
        var log = host.Services.GetRequiredService<EventLog>();
        var batch = EventLogTests.Batch(Ev(1, K.Created, Meta(policy: ConversationSyncPolicies.Paused)));

        var inside = await host.Services.GetRequiredService<SyncWriter>().WriteAsync(context =>
        {
            log.AppendBatch(context, new UpstreamContext(a, null, null), batch).Ack.Results.Single().Seq.Should().NotBeNull();
            return directory.Find(Conv);
        });

        inside.Should().BeNull("交易還沒提交：目錄只在 AfterCommit 更新（R11）");
        directory.Find(Conv).Should().Be(new ConversationOwnership(Conv, a, 1, ConversationSyncPolicies.Paused));
        directory.Find(Conv.ToUpperInvariant()).Should().BeNull("id 不合格");
        directory.Find(OtherConv).Should().BeNull("不存在");
    }

    [Fact]
    public async Task 刪除後Find回null()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var cache = host.Services.GetRequiredService<ConversationDirectoryCache>();
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()));
        cache.Find(Conv).Should().NotBeNull();
        cache.TryGet(Conv, out var before).Should().BeTrue();
        before!.ItemCount.Should().Be(1);

        await Append(host, a, Ev(3, K.Deleted, new JObject()));

        cache.Find(Conv).Should().BeNull();
        cache.TryGet(Conv, out var entry).Should().BeTrue("墓碑留在內部的目錄");
        entry!.Deleted.Should().BeTrue();
        cache.OwnedBy(a).Should().BeEmpty();
        var counts = host.Services.GetRequiredService<ConversationCounters>().Snapshot();
        (counts.Count, counts.Deleted).Should().Be((0L, 1L));
    }

    [Fact]
    public async Task 啟動載入既有對話()
    {
        var dir = TempDir.Create();
        try
        {
            string a;
            await using (var first = await HubTestHost.StartAsync(dataDir: dir))
            {
                a = await EventLogTests.AddDeviceAsync(first);
                await Append(first, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Created, Meta(), conversationId: OtherConv), Ev(4, K.Deleted, new JObject(), conversationId: OtherConv));
            }

            await using var second = await HubTestHost.StartAsync(dataDir: dir);
            var cache = second.Services.GetRequiredService<ConversationDirectoryCache>();
            cache.Find(Conv).Should().Be(new ConversationOwnership(Conv, a, 1, ConversationSyncPolicies.Full));
            cache.Find(OtherConv).Should().BeNull("已刪除");
            cache.Count.Should().Be(2);
            cache.OwnedBy(a).Select(e => (e.ConversationId, e.ItemCount)).Should().Equal((Conv, 1));
            var counts = second.Services.GetRequiredService<ConversationCounters>().Snapshot();
            (counts.Count, counts.Deleted, counts.Items).Should().Be((1L, 1L, 1L));
            counts.Devices.Single().Should().Match<ConversationDeviceCounts>(d => d.DeviceId == a && d.Owned == 1 && d.Items == 1);
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task Hello的features是conv1_prov1_live1_update1_cmd1_peer1與media1()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);

        var (hello, _) = await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));

        // 依登記的順序：AddHubRealtime 的 conv1、prov1、live1，AddHubPairing 之後的 AddHubReleases 宣告 update1（U2），AddHubBrowser 裡 AddHubCommands 的 cmd1（WP4.5 步 6），
        // 之後是 AddHubMedia 的 media1（WP4.7 步 4）、AddHubStats 的 stat1（各電腦的即時狀態）、AddHubTokens 的 tok1、tok2（各帳戶的 token 計數、其他電腦的 token 計數）
        hello.Features.Should().Equal(DeviceFeatureRules.Conv1, DeviceFeatureRules.Prov1, DeviceFeatureRules.Live1, DeviceFeatureRules.Live2, DeviceFeatureRules.Update1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Exec1, DeviceFeatureRules.Media1, DeviceFeatureRules.Stat1, DeviceFeatureRules.Tok1, DeviceFeatureRules.Tok2, DeviceFeatureRules.Sched1, DeviceFeatureRules.Draft1, DeviceFeatureRules.Quick1, DeviceFeatureRules.Collab1, DeviceFeatureRules.Xfer1, DeviceFeatureRules.Xfer2, DeviceFeatureRules.Desk1);
    }

    [Fact]
    public async Task withoutDeviceFeatures時_Hello的features是null()
    {
        await using var host = await HubTestHost.StartAsync(withoutDeviceFeatures: true);
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);

        var (hello, _) = await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));

        hello.Features.Should().BeNull("測試輔助拿掉了正式的宣告");
    }

    // ───── 新事件旗標與 recovering（D13、D16） ─────

    [Fact]
    public async Task 對帳重送的舊事件_變更不帶Fresh()
    {
        await using var host = await HubTestHost.StartAsync(withoutConversationGateway: true);
        var a = await EventLogTests.AddDeviceAsync(host);
        var feed = host.Services.GetRequiredService<ConversationChangeFeed>();
        var hello = new OutboxPosition(3, 1, 1);
        var upstream = new UpstreamContext(a, OutboxRules.ResyncFrom(hello), hello);

        var outcome = await host.Services.GetRequiredService<EventLog>().AppendBatchAsync(upstream,
            EventLogTests.Batch(Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Item, Item(id: "item2", ord: 2))));

        outcome.Ack.Results.Should().OnlyContain(r => r.Seq != null);
        var changes = Drain(feed);
        changes.Select(c => (c.Kind, c.Fresh)).Should().Equal(
            (ConversationChangeKind.Meta, false), (ConversationChangeKind.Item, false), (ConversationChangeKind.Item, true));
        changes[2].Should().Match<ConversationChange>(c => c.ItemId == "item2" && c.Ord == 2 && c.Ver == 1 && c.Final && c.Status == "Done" && c.Author == a
            && c.Seq == outcome.Ack.Results[2].Seq && c.Body != null && c.Body.Contains("你好"));
    }

    [Fact]
    public async Task Hello之後的事件_變更帶Fresh()
    {
        await using var host = await HubTestHost.StartAsync(withoutConversationGateway: true);
        var a = await EventLogTests.AddDeviceAsync(host);
        var feed = host.Services.GetRequiredService<ConversationChangeFeed>();

        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Turn, Turn("started")), Ev(3, K.Attention, new JObject { ["kind"] = "plan" }), Ev(4, K.Deleted, new JObject()));

        Drain(feed).Select(c => (c.Kind, c.Fresh, c.TurnPhase, c.AttentionKind)).Should().Equal(
            (ConversationChangeKind.Meta, true, null, null), (ConversationChangeKind.Turn, true, "started", null),
            (ConversationChangeKind.Attention, true, null, "plan"), (ConversationChangeKind.Deleted, true, null, null));
    }

    [Fact]
    public async Task 對帳重送的舊created不解除recovering_Hello之後的created解除()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var log = host.Services.GetRequiredService<EventLog>();
        await Append(host, a, Ev(1, K.Created, Meta()));
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(c => c.Execute("UPDATE conversations SET lease_state = 'recovering'"));
        string LeaseState() => Text(host, $"SELECT lease_state FROM conversations WHERE conversation_id = '{Conv}'");
        var hello = new OutboxPosition(4, 2, 2);
        var upstream = new UpstreamContext(a, OutboxRules.ResyncFrom(hello), hello);

        await log.AppendBatchAsync(upstream, EventLogTests.Batch(Ev(2, K.Created, Meta("對帳重送")), Ev(3, K.Item, Item())));
        LeaseState().Should().Be("recovering", "還原前就在 outbox 的舊 created 照常套用 meta、不解除（D16）");
        Text(host, $"SELECT title FROM conversations WHERE conversation_id = '{Conv}'").Should().Be("對帳重送");

        await log.AppendBatchAsync(upstream, EventLogTests.Batch(Ev(4, K.Created, Meta("Hello 之後"))));
        LeaseState().Should().Be("owned", "deviceSeq 等於 Hello 的 NextDeviceSeq：新的 created 解除");
    }

    [Fact]
    public async Task 擁有者還原後送created_不在對帳模式_解除recovering()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()));
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(c => c.Execute("UPDATE conversations SET lease_state = 'recovering'"));

        await Append(host, a, Ev(2, K.Created, Meta("還原之後")));

        Text(host, $"SELECT lease_state FROM conversations WHERE conversation_id = '{Conv}'").Should().Be("owned", "upstream.ResyncFrom 是 null：不在對帳模式");
        host.Services.GetRequiredService<ConversationDirectoryCache>().TryGet(Conv, out var entry).Should().BeTrue();
        entry!.Recovering.Should().BeFalse("目錄快取在 AfterCommit 換上新的列");
    }

    [Fact]
    public async Task 非擁有者或item或meta_不解除recovering()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var b = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()));
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(c => c.Execute("UPDATE conversations SET lease_state = 'recovering'"));
        string LeaseState() => Text(host, $"SELECT lease_state FROM conversations WHERE conversation_id = '{Conv}'");

        var other = await Append(host, b, Ev(1, K.Created, Meta("別台")));
        other.Ack.Results.Single().Rejected.Should().Be(ConversationRejectCodes.NotOwner);
        LeaseState().Should().Be("recovering", "非擁有者的 created 被拒、不解除");

        var owner = await Append(host, a, Ev(2, K.Item, Item()), Ev(3, K.Meta, Meta("改名")), Ev(4, K.Turn, Turn(ConversationTurnPhases.Started)), Ev(5, K.Queue, Queue("排隊")));
        owner.Ack.Results.Should().OnlyContain(r => r.Seq != null);
        LeaseState().Should().Be("recovering", "只有擁有者的 conv.created 解除（item、meta、turn、queue 都不算）");
        host.Services.GetRequiredService<ConversationCounters>().Snapshot().Recovering.Should().Be(1);
    }

    // ───── 拉取（R12） ─────

    [Fact]
    public async Task stream_c拉得到這個對話的事件_cls_item拉得到()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Queue, Queue("甲")), Ev(4, K.Created, Meta(), conversationId: OtherConv));
        var log = host.Services.GetRequiredService<EventLog>();

        log.Pull(new PullRequest([new PullCursor(0, Stream: SyncStreams.Conversation(Conv))], 100, 1_000_000)).Events.Select(e => e.Kind)
            .Should().Equal(K.Created, K.Item, K.Queue);
        log.Pull(new PullRequest([new PullCursor(0, Cls: SyncClasses.Meta)], 100, 1_000_000)).Events.Select(e => (e.Kind, e.ConversationId))
            .Should().Equal((K.Created, Conv), (K.Created, OtherConv));
        log.Pull(new PullRequest([new PullCursor(0, Cls: SyncClasses.Item)], 100, 1_000_000)).Events.Select(e => e.Kind)
            .Should().Equal(K.Item, K.Queue);
    }

    [Fact]
    public async Task 營運計數_被拒的各原因與每台裝置()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var b = await EventLogTests.AddDeviceAsync(host);
        var created = await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Item, Item(), epoch: 2), Ev(4, K.Item, Item(), conversationId: OtherConv));
        await Append(host, b, Ev(1, K.Item, Item()));

        var counts = host.Services.GetRequiredService<ConversationCounters>().Snapshot();

        counts.Rejected.Should().Contain(ConversationRejectCodes.Fenced, 1).And.Contain(ConversationRejectCodes.NotOwner, 1).And.Contain(SyncErrorCodes.Invalid, 1)
            .And.Contain(ConversationRejectCodes.Deleted, 0).And.Contain(SyncErrorCodes.TooLarge, 0).And.NotContainKey(ConversationRejectCodes.Dlp);
        counts.ConversationEvents.Should().Be(2);
        counts.ConversationEventBytes.Should().BeGreaterThan(0);
        (counts.Count, counts.Items).Should().Be((1L, 1L));
        counts.Devices.Should().ContainSingle().Which.Should().Be(new ConversationDeviceCounts(a, 1, 0, 1, created.Ack.Results[1].Seq!.Value, host.Clock.NowMs));
    }

    // ───── 工具 ─────

    internal static SyncEvent Ev(long deviceSeq, string kind, JToken payload, string conversationId = Conv, long? epoch = 1) =>
        Event(kind, Json(payload), conversationId, epoch, deviceSeq: deviceSeq);

    internal static Task<BatchOutcome> Append(HubTestHost host, string deviceId, params SyncEvent[] events) =>
        EventLogTests.AppendAsync(host, deviceId, EventLogTests.Batch(events));

    internal static JObject Turn(string phase) => phase == ConversationTurnPhases.Started
        ? new JObject { ["phase"] = phase }
        : new JObject { ["phase"] = phase, ["outcome"] = ConversationTurnOutcomes.Completed, ["usage"] = new JObject { ["inputTokens"] = 10, ["outputTokens"] = 5, ["cachedInputTokens"] = 0 } };

    internal static JObject Removal(string itemId) => new() { ["id"] = itemId };

    internal static string Decode(string enc, string payload) =>
        PayloadRules.TryDecode(enc, payload, 1 << 22, out var json) ? json : throw new InvalidOperationException("payload 解不開");

    internal static List<ConversationChange> Drain(ConversationChangeFeed feed)
    {
        var list = new List<ConversationChange>();
        feed.TryDrain(list, out var fullRefresh);
        fullRefresh.Should().BeFalse();
        return list;
    }

    internal static string Text(HubTestHost host, string sql) => (string)Rows(host, sql).Single()[0]!;

    internal static List<object?[]> Rows(HubTestHost host, string sql)
    {
        using var read = host.Services.GetRequiredService<SyncWriter>().OpenRead();
        using var cmd = read.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    internal static async Task<List<AuditRecord>> AuditRowsAsync(HubTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        return await db.Audit.AsNoTracking().OrderBy(a => a.Seq).ToListAsync();
    }
}
