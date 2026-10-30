using System.Diagnostics;
using System.Net;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using static MyriagentHub.Tests.Sync.ConversationSamples;
using K = Myriagent.Sync.ConversationEventKinds;
using FoldSnapshot = MyriagentHub.Tests.Sync.ConversationEventLogTests.FoldSnapshot;
using P = Myriagent.Sync.ConversationSyncPolicies;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 物化表的分塊重建（WP4.2 計畫步 5；D14、R32、R33）：重建的結果與增量物化逐列相同（步 4 的隨機序列與指紋）、重建途中的新事件、降級與刪除、
/// 分塊上限、啟動時的版本比對與背景重建（不擋健康閘門）、失敗後從頭再做。比對的「增量」一律是另一台主機收同一串事件（同一個時鐘起點、同一台固定 id 的裝置，seq 相同）
/// </summary>
public sealed class ConversationRebuildTests(ITestOutputHelper output)
{
    private const string Device = "d_0000000000000000000000000A";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    // ───── 與增量物化相同 ─────

    [Fact]
    public async Task 重建與增量物化逐列相同()
    {
        await using var host = await HubTestHost.StartAsync(withoutConversationGateway: true);
        var rebuild = host.Services.GetRequiredService<ConversationRebuild>();
        await rebuild.FirstRun.WaitAsync(Wait);
        await ConversationEventLogTests.RunFoldAsync(host);
        var incremental = ConversationEventLogTests.FoldDump(host);
        var directory = host.Services.GetRequiredService<ConversationDirectoryCache>();
        var ids = incremental.Conversations.Select(r => (string)r[0]!).ToList();
        var entriesBefore = ids.Select(id => directory.TryGet(id, out var e) ? e : null).ToList();
        var countsBefore = host.Services.GetRequiredService<ConversationCounters>().Snapshot();
        var feed = host.Services.GetRequiredService<ConversationChangeFeed>();
        ConversationEventLogTests.Drain(feed);

        await rebuild.RebuildAsync("測試", CancellationToken.None);

        var rebuilt = ConversationEventLogTests.FoldDump(host);
        AssertSame(rebuilt, incremental);
        output.WriteLine("增量物化：" + incremental.Describe());
        output.WriteLine("重建之後：" + rebuilt.Describe());
        ids.Select(id => directory.TryGet(id, out var e) ? e : null).Should().Equal(entriesBefore, "目錄快取換上的列與增量的相同");
        var countsAfter = host.Services.GetRequiredService<ConversationCounters>().Snapshot();
        (countsAfter.Count, countsAfter.Deleted, countsAfter.Items, countsAfter.Recovering)
            .Should().Be((countsBefore.Count, countsBefore.Deleted, countsBefore.Items, countsBefore.Recovering));
        directory.RebuildCounts().Should().Be((0, 0));
        ConversationEventLogTests.Drain(feed).Where(c => c.Kind == ConversationChangeKind.Rebuilt).Select(c => c.ConversationId)
            .Should().BeEquivalentTo(ids, "每個對話做完送一次 Rebuilt");
        Version(host).Should().Be("1");
    }

    [Fact]
    public async Task 重建途中有新事件_順序正確()
    {
        var (rebuilt, incremental, steps) = await RunTwinAsync(
            initial: f => f.SendAsync(BigConversation(1200)),
            inject: f => f.SendAsync([
                (K.Item, Item("新的 1100", id: "i1100", ord: 1101, ver: 2), Conv),
                (K.Item, Item("新的 0010", id: "i0010", ord: 11, ver: 2), Conv),
                (K.ItemRemoved, ConversationEventLogTests.Removal("i0700"), Conv),
                (K.Item, Item("新的一則", id: "i9999", ord: 5000), Conv),
                (K.Meta, Meta("改名"), Conv),
                (K.Queue, Queue("重建途中排隊"), Conv),
                (K.Turn, ConversationEventLogTests.Turn(ConversationTurnPhases.Started), Conv),
                (K.Item, Item("另一個對話", id: "o9", ord: 9), OtherConv),
            ]));

        AssertSame(rebuilt, incremental);
        Body(rebuilt, Conv, "i1100").Should().Contain("新的 1100", "還沒重播到的項目：新事件的 seq 比較大，最後套用");
        Body(rebuilt, Conv, "i0010").Should().Contain("新的 0010", "已經重播過的項目：新事件之後再套用");
        Column(rebuilt.Items.Single(r => (string)r[1]! == "i0700"), 12).Should().Be(1L, "移除還沒重播到的項目");
        var row = rebuilt.Conversations.Single(r => (string)r[0]! == Conv);
        (row[9], row[13], row[14], row[15]).Should().Be(("改名", 1200L, 5000L, 1L), "1200＋1－1 則、max_ord 是新的一則、running");
        // 1212 筆加上重建途中的 5 筆 item 類事件
        steps.Skip(1).Select(s => s.Events).Should().Equal(500, 500, 217);
    }

    [Fact]
    public async Task 重建途中降級_不復活舊項目()
    {
        var (rebuilt, incremental, _) = await RunTwinAsync(
            initial: f => f.SendAsync(BigConversation(1200)),
            inject: f => f.SendAsync([(K.Meta, Meta("只同步標題", P.Metadata), Conv)]),
            after: f => f.SendAsync([(K.Meta, Meta("改回完整", P.Full), Conv), (K.Item, Item("改回之後的第一則", id: "n1", ord: 1300), Conv)]));

        AssertSame(rebuilt, incremental);
        rebuilt.Items.Where(r => (string)r[0]! == Conv).Select(r => (string)r[1]!).Should().Equal(["n1"], "降級之前的 1200 則不放回");
        rebuilt.Runtime.Should().NotContain(r => (string)r[0]! == Conv, "降級清掉佇列，改回之後還沒送新的");
        var row = rebuilt.Conversations.Single(r => (string)r[0]! == Conv);
        (row[13], row[14], row[20]).Should().Be((1L, 1300L, P.Full));
    }

    [Fact]
    public async Task 重建途中刪除_之後的事件deleted()
    {
        var (rebuilt, incremental, _) = await RunTwinAsync(
            initial: f => f.SendAsync(BigConversation(1200)),
            inject: f => f.SendAsync([(K.Deleted, new JObject(), Conv)]),
            after: async f =>
            {
                var results = await f.SendAsync([(K.Item, Item("刪除之後"), Conv)], expectAccepted: false);
                results.Single().Rejected.Should().Be(ConversationRejectCodes.Deleted);
            });

        AssertSame(rebuilt, incremental);
        rebuilt.Items.Should().NotContain(r => (string)r[0]! == Conv);
        rebuilt.Runtime.Should().NotContain(r => (string)r[0]! == Conv);
        var row = rebuilt.Conversations.Single(r => (string)r[0]! == Conv);
        (row[13], row[14], row[25]).Should().Be((0L, 1200L, Device), "墓碑保留、max_ord 只增不減");
        row[24].Should().NotBeNull();
    }

    [Fact]
    public async Task 大對話分塊_每個寫入工作不超過500筆事件()
    {
        // 1234 筆 item 類事件（1200 則項目＋12 次佇列＋…）：第一塊只讀 meta 類，之後每塊最多 500 筆，不重疊不漏
        var (rebuilt, incremental, steps) = await RunTwinAsync(initial: f => f.SendAsync(BigConversation(1222)), inject: _ => Task.CompletedTask);

        AssertSame(rebuilt, incremental);
        var itemEvents = BigConversation(1222).Count(e => e.Conversation == Conv && K.ClassOf(e.Kind) == SyncClasses.Item);
        itemEvents.Should().Be(1234);
        var metaEvents = BigConversation(1222).Count(e => e.Conversation == Conv && K.ClassOf(e.Kind) == SyncClasses.Meta);
        steps[0].Should().Be(new RebuildStep(false, metaEvents), "第一塊：只讀這個對話的 meta 類事件");
        steps.Skip(1).Select(s => s.Events).Should().Equal(500, 500, 234);
        steps[^1].Finished.Should().BeTrue();
        steps.Take(steps.Count - 1).Should().OnlyContain(s => !s.Finished);
    }

    [Fact]
    public async Task 重建重播遮蔽版()
    {
        await using var host = await StartWithDeviceAsync();
        var f = new Feeder(host);
        var token = GitHub('q');
        await f.SendAsync([(K.Created, Meta(), Conv), (K.Item, Item("外洩 " + token), Conv), (K.Item, Item("乾淨", id: "item2", ord: 2), Conv)]);
        var before = ConversationEventLogTests.FoldDump(host);

        await host.Services.GetRequiredService<ConversationRebuild>().RebuildAsync("測試", CancellationToken.None);

        var after = ConversationEventLogTests.FoldDump(host);
        AssertSame(after, before);
        var body = Body(after, Conv, "item1");
        body.Should().NotContain(token[4..20], "重播的是 events 存下的遮蔽版（D44）").And.Contain(CredentialLeakRules.Replacement).And.Contain("外洩");
    }

    [Fact]
    public async Task 重建_已刪除的對話不重播項目()
    {
        await using var host = await StartWithDeviceAsync();
        var f = new Feeder(host);
        await f.SendAsync([
            (K.Created, Meta(), Conv), (K.Item, Item("一", id: "a", ord: 1), Conv), (K.Item, Item("二", id: "b", ord: 7), Conv), (K.Queue, Queue("排隊"), Conv),
            (K.Deleted, new JObject(), Conv),
            (K.Created, Meta("另一個"), OtherConv), (K.Item, Item("留著", id: "c", ord: 1), OtherConv),
        ]);
        // 故意放回已刪除對話的內容：重建要清掉、不能從事件放回
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(c =>
        {
            c.Execute($"INSERT INTO conversation_runtime(conversation_id, queue_json, pending_attachments_json, version) VALUES('{Conv}', '[]', '[]', 1)");
            c.Execute($"UPDATE conversations SET max_ord = 0, item_count = 5 WHERE conversation_id = '{Conv}'");
        });

        await host.Services.GetRequiredService<ConversationRebuild>().RebuildAsync("測試", CancellationToken.None);

        var after = ConversationEventLogTests.FoldDump(host);
        after.Items.Select(r => ((string)r[0]!, (string)r[1]!)).Should().Equal((OtherConv, "c"));
        after.Runtime.Should().BeEmpty();
        var row = after.Conversations.Single(r => (string)r[0]! == Conv);
        (row[13], row[14], row[25]).Should().Be((0L, 7L, Device), "列還在、max_ord 照事件算回 7、內容不重播");
        row[24].Should().NotBeNull();
    }

    [Fact]
    public async Task 重建保留recovering與第4階段不寫的欄位()
    {
        await using var host = await StartWithDeviceAsync();
        var f = new Feeder(host);
        await f.SendAsync([(K.Created, Meta(), Conv), (K.Item, Item(), Conv)]);
        // 還原的標記與之後的階段才寫的欄位（事件推不出來）
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(c => c.Execute(
            "UPDATE conversations SET lease_state = 'recovering', header_json = '{\"h\":1}', status_text = '狀態', transcript = 'missing', forked_from = 'x', fork_point_seq = 3 "
            + $"WHERE conversation_id = '{Conv}'"));
        var before = ConversationEventLogTests.Rows(host, $"SELECT lease_state, header_json, status_text, transcript, forked_from, fork_point_seq FROM conversations WHERE conversation_id = '{Conv}'").Single();

        await host.Services.GetRequiredService<ConversationRebuild>().RebuildAsync("測試", CancellationToken.None);

        ConversationEventLogTests.Rows(host, $"SELECT lease_state, header_json, status_text, transcript, forked_from, fork_point_seq FROM conversations WHERE conversation_id = '{Conv}'")
            .Single().Should().Equal(before);
        host.Services.GetRequiredService<ConversationDirectoryCache>().TryGet(Conv, out var entry).Should().BeTrue();
        entry!.Recovering.Should().BeTrue("目錄換上的列照樣是 recovering");
    }

    [Fact]
    public async Task 重建失敗_放棄進行中的_標回等著重建_之後從頭再做()
    {
        await using var host = await StartWithDeviceAsync();
        var f = new Feeder(host);
        await f.SendAsync(BigConversation(30));
        var expected = ConversationEventLogTests.FoldDump(host);
        var hooks = host.Services.GetRequiredService<ConversationRebuildHooks>();
        var directory = host.Services.GetRequiredService<ConversationDirectoryCache>();
        var rebuild = host.Services.GetRequiredService<ConversationRebuild>();
        hooks.AfterChunk = (_, _) => throw new InvalidOperationException("測試：第一塊之後失敗");

        var act = () => rebuild.RebuildAsync("測試", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        directory.RebuildCounts().Should().Be((0, 2), "內容已經清掉、還沒重做：兩個對話都標回等著重建");
        directory.IsRebuilding(Conv).Should().BeTrue();
        // 放棄之後新的 item 類事件照常物化（寫入執行緒上的重建進度已清掉）
        await f.SendAsync([(K.Item, Item("失敗之後", id: "late", ord: 99), Conv)]);
        EventLogTests.Count(host, $"SELECT COUNT(*) FROM items WHERE conversation_id = '{Conv}' AND item_id = 'late'").Should().Be(1);

        hooks.AfterChunk = null;
        await rebuild.RebuildAsync("測試", CancellationToken.None);

        directory.RebuildCounts().Should().Be((0, 0));
        var after = ConversationEventLogTests.FoldDump(host);
        after.Items.Select(r => (string)r[1]!).Should().BeEquivalentTo(expected.Items.Select(r => (string)r[1]!).Append("late"));
        after.Items.Where(r => (string)r[1]! != "late").Select(r => r.ToArray()).Should().BeEquivalentTo(expected.Items, o => o.WithStrictOrdering());
    }

    // ───── 啟動時的版本比對與背景重建 ─────

    [Fact]
    public async Task 新的資料庫_只寫版本鍵_不重建()
    {
        var calls = 0;
        var hooks = new ConversationRebuildHooks { AfterChunk = (_, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; } };
        await using var host = await HubTestHost.StartAsync(services: s => s.AddSingleton(hooks));

        await host.Services.GetRequiredService<ConversationRebuild>().FirstRun.WaitAsync(Wait);

        Version(host).Should().Be(ConversationMaterializer.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        calls.Should().Be(0);
        host.Services.GetRequiredService<ConversationDirectoryCache>().RebuildCounts().Should().Be((0, 0));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData(null)]
    public async Task 版本不同_重新啟動後背景重建_改壞的物化表回到增量的結果(string? stored)
    {
        // "2"＝從較新的 WP4.2 組建回滾（版本較新的資料也重建）、"0"＝較舊的、null＝版本鍵不見了
        var dir = TempDir.Create();
        try
        {
            FoldSnapshot expected;
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                await host.Services.GetRequiredService<ConversationRebuild>().FirstRun.WaitAsync(Wait);
                await ConversationEventLogTests.RunFoldAsync(host);
                expected = ConversationEventLogTests.FoldDump(host);
            }
            expected.Conversations.Should().Contain(r => r[24] != null, "前提：有已刪除的對話（改壞時拿掉它的墓碑）");
            var bogus = new string('9', 32);
            Corrupt(new HubPaths(dir).SyncDb, stored, bogus, expected);

            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                await host.Services.GetRequiredService<ConversationRebuild>().FirstRun.WaitAsync(Wait);

                AssertSame(ConversationEventLogTests.FoldDump(host), expected);
                Version(host).Should().Be("1");
                var directory = host.Services.GetRequiredService<ConversationDirectoryCache>();
                directory.TryGet(bogus, out _).Should().BeFalse("沒有任何事件的列在重建時刪掉");
                directory.RebuildCounts().Should().Be((0, 0));
                var counts = host.Services.GetRequiredService<ConversationCounters>().Snapshot();
                (counts.Count + counts.Deleted).Should().Be(expected.Conversations.Count);
                counts.Items.Should().Be(expected.Conversations.Where(r => r[24] is null).Sum(r => (long)r[13]!));
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 版本相等_重新啟動不重建()
    {
        // 陰性對照：改壞的物化表在版本相等時原樣留著（證明上一個測試的修正來自重建）
        var dir = TempDir.Create();
        try
        {
            await using (var host = await StartWithDeviceAsync(dir))
            {
                await new Feeder(host).SendAsync(BigConversation(5));
            }
            using (var connection = SqliteSetup.Open(new HubPaths(dir).SyncDb))
                Exec(connection, $"UPDATE conversations SET title = '改壞' WHERE conversation_id = '{Conv}'");
            var calls = 0;
            var hooks = new ConversationRebuildHooks { AfterChunk = (_, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; } };

            await using (var host = await HubTestHost.StartAsync(dataDir: dir, services: s => s.AddSingleton(hooks)))
            {
                await host.Services.GetRequiredService<ConversationRebuild>().FirstRun.WaitAsync(Wait);

                ConversationEventLogTests.Text(host, $"SELECT title FROM conversations WHERE conversation_id = '{Conv}'").Should().Be("改壞");
                calls.Should().Be(0);
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 版本鍵缺_背景重建_readyz在1秒內200_重建中的對話有旗標()
    {
        var dir = TempDir.Create();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using (var host = await StartWithDeviceAsync(dir))
            {
                await new Feeder(host).SendAsync(BigConversation(20));
            }
            using (var connection = SqliteSetup.Open(new HubPaths(dir).SyncDb))
                Exec(connection, $"DELETE FROM server_meta WHERE key = '{ConversationMaterializer.VersionKey}'");
            var entered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hooks = new ConversationRebuildHooks
            {
                AfterChunk = async (id, _) =>
                {
                    entered.TrySetResult(id);
                    await gate.Task;
                },
            };

            await using var started = await HubTestHost.StartAsync(dataDir: dir, services: s => s.AddSingleton(hooks));
            var first = await entered.Task.WaitAsync(Wait);
            using var local = started.CreateClient(ip: "127.0.0.1", cookies: false);
            var clock = Stopwatch.StartNew();
            using var response = await local.GetAsync("/readyz");
            clock.Stop();

            response.StatusCode.Should().Be(HttpStatusCode.OK, "重建在背景，不擋健康閘門");
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
            var conversations = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("conversations");
            (conversations.GetProperty("rebuilding").GetInt32(), conversations.GetProperty("rebuildPending").GetInt32()).Should().Be((1, 1));
            var directory = started.Services.GetRequiredService<ConversationDirectoryCache>();
            directory.IsRebuilding(first).Should().BeTrue("Header 顯示「伺服器正在整理這個對話」（步 8 接上）");
            directory.IsRebuilding(first == Conv ? OtherConv : Conv).Should().BeTrue("等著重建的也算");
            Version(started).Should().BeNull("全部做完才寫版本鍵");

            gate.TrySetResult();
            await started.Services.GetRequiredService<ConversationRebuild>().FirstRun.WaitAsync(Wait);

            directory.RebuildCounts().Should().Be((0, 0));
            Version(started).Should().Be("1");
        }
        finally
        {
            gate.TrySetResult();
            TempDir.TryDelete(dir);
        }
    }

    // ───── 工具 ─────

    /// <summary>
    /// 同一串事件送進兩台主機：A 在 <paramref name="initial"/> 之後重建，重建 <see cref="Conv"/> 的第一塊重播之後送 <paramref name="inject"/>；
    /// B 不重建、依序送 <paramref name="initial"/> 與 <paramref name="inject"/>。兩邊再各送 <paramref name="after"/>。回傳兩邊的指紋與 A 重建 <see cref="Conv"/> 的每一塊
    /// </summary>
    private static async Task<(FoldSnapshot Rebuilt, FoldSnapshot Incremental, List<RebuildStep> Steps)> RunTwinAsync(
        Func<Feeder, Task> initial, Func<Feeder, Task> inject, Func<Feeder, Task>? after = null)
    {
        FoldSnapshot rebuilt;
        var steps = new List<RebuildStep>();
        await using (var a = await StartWithDeviceAsync())
        {
            var feeder = new Feeder(a);
            await initial(feeder);
            var directory = a.Services.GetRequiredService<ConversationDirectoryCache>();
            var injected = false;
            a.Services.GetRequiredService<ConversationRebuildHooks>().AfterChunk = async (id, step) =>
            {
                if (id != Conv)
                    return;
                steps.Add(step);
                if (steps.Count == 2)
                {
                    directory.IsRebuilding(Conv).Should().BeTrue();
                    await inject(feeder);
                    injected = true;
                }
            };
            await a.Services.GetRequiredService<ConversationRebuild>().RebuildAsync("測試", CancellationToken.None);
            injected.Should().BeTrue("重建途中送了新事件");
            directory.IsRebuilding(Conv).Should().BeFalse();
            if (after is not null)
                await after(feeder);
            rebuilt = ConversationEventLogTests.FoldDump(a);
        }

        await using var b = await StartWithDeviceAsync();
        var twin = new Feeder(b);
        await initial(twin);
        await inject(twin);
        if (after is not null)
            await after(twin);
        return (rebuilt, ConversationEventLogTests.FoldDump(b), steps);
    }

    /// <summary>
    /// 一個大對話（<see cref="Conv"/>：<paramref name="items"/> 則項目、每 100 則一次佇列、每 300 則一輪 turn）與一個小對話（<see cref="OtherConv"/>：3 則）
    /// </summary>
    private static List<(string Kind, JObject Payload, string Conversation)> BigConversation(int items)
    {
        var events = new List<(string, JObject, string)> { (K.Created, Meta(), Conv), (K.Created, Meta("另一個"), OtherConv) };
        for (var i = 0; i < items; i++)
        {
            events.Add((K.Item, Item($"第 {i} 則", id: $"i{i:D4}", ord: i + 1), Conv));
            if (i % 100 == 99)
                events.Add((K.Queue, Queue($"排隊 {i}"), Conv));
            if (i % 300 == 0)
                events.Add((K.Turn, ConversationEventLogTests.Turn(i % 600 == 0 ? ConversationTurnPhases.Started : ConversationTurnPhases.Completed), Conv));
            if (i < 3)
                events.Add((K.Item, Item($"小對話 {i}", id: $"o{i}", ord: i + 1), OtherConv));
        }
        return events;
    }

    private static async Task<HubTestHost> StartWithDeviceAsync(string? dir = null)
    {
        var host = await HubTestHost.StartAsync(dataDir: dir);
        await host.Services.GetRequiredService<ConversationRebuild>().FirstRun.WaitAsync(Wait);
        await ConversationEventLogTests.AddFixedDeviceAsync(host, Device);
        return host;
    }

    /// <summary>改壞物化表（服務已停止）：版本鍵、標題、項目數、多出與少掉的項目、佇列、整列不見的對話、沒有任何事件的對話列</summary>
    private static void Corrupt(string syncDb, string? stored, string bogus, FoldSnapshot expected)
    {
        using var connection = SqliteSetup.Open(syncDb);
        Exec(connection, stored is null
            ? $"DELETE FROM server_meta WHERE key = '{ConversationMaterializer.VersionKey}'"
            : $"UPDATE server_meta SET value = '{stored}' WHERE key = '{ConversationMaterializer.VersionKey}'");
        var first = (string)expected.Conversations[0][0]!;
        Exec(connection, $"UPDATE conversations SET title = '改壞', item_count = 999, max_ord = 0, running = 1, last_seq = 0 WHERE conversation_id = '{first}'");
        Exec(connection, "DELETE FROM items WHERE rowid IN (SELECT rowid FROM items ORDER BY rowid LIMIT 3)");
        Exec(connection, $"INSERT INTO items(conversation_id, item_id, ord, last_seq, epoch, ver, kind, status, final, origin_device_id, body) "
            + $"VALUES('{first}', 'bogus', 1, 1, 1, 1, 'Assistant', 'Done', 1, '{Device}', '{{}}')");
        Exec(connection, "DELETE FROM conversation_runtime");
        // 墓碑與政策不見了（重建要從 conv.deleted 與 meta 推回來）
        Exec(connection, "UPDATE conversations SET deleted_at = NULL, deleted_by = NULL, sync_policy = 'paused'");
        // 整列不見的對話：只出現在 events，重建要從事件建回來
        var second = (string)expected.Conversations[1][0]!;
        Exec(connection, $"DELETE FROM items WHERE conversation_id = '{second}'");
        Exec(connection, $"DELETE FROM conversations WHERE conversation_id = '{second}'");
        Exec(connection, "INSERT INTO conversations(conversation_id, provider, origin_device_id, owner_device_id, epoch, meta_json, title, created_at, updated_at, last_seq) "
            + $"VALUES('{bogus}', 'Claude', '{Device}', '{Device}', 1, '{{}}', '沒有事件', 1, 1, 1)");
    }

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string? Version(HubTestHost host)
    {
        using var read = host.Services.GetRequiredService<SyncWriter>().OpenRead();
        return SqliteSetup.Scalar(read, $"SELECT value FROM server_meta WHERE key = '{ConversationMaterializer.VersionKey}'") as string;
    }

    private static void AssertSame(FoldSnapshot actual, FoldSnapshot expected)
    {
        actual.Conversations.Select(r => (string)r[0]!).Should().Equal(expected.Conversations.Select(r => (string)r[0]!));
        for (var i = 0; i < expected.Conversations.Count; i++)
            actual.Conversations[i].Should().Equal(expected.Conversations[i], $"conversations 的 {expected.Conversations[i][0]}");
        actual.Items.Select(r => ((string)r[0]!, (string)r[1]!)).Should().Equal(expected.Items.Select(r => ((string)r[0]!, (string)r[1]!)));
        for (var i = 0; i < expected.Items.Count; i++)
            actual.Items[i].Should().Equal(expected.Items[i], $"items 的 {expected.Items[i][0]}／{expected.Items[i][1]}");
        actual.Runtime.Count.Should().Be(expected.Runtime.Count);
        for (var i = 0; i < expected.Runtime.Count; i++)
            actual.Runtime[i].Should().Equal(expected.Runtime[i], $"conversation_runtime 的 {expected.Runtime[i][0]}");
        actual.Sha.Should().Be(expected.Sha);
    }

    private static string Body(FoldSnapshot snapshot, string conversationId, string itemId)
    {
        var row = snapshot.Items.Single(r => (string)r[0]! == conversationId && (string)r[1]! == itemId);
        return ConversationEventLogTests.Decode((string)row[10]!, (string)row[11]!);
    }

    private static object? Column(object?[] row, int index) => row[index];

    /// <summary>以固定 id 的裝置依序送事件（每批最多 200 筆、deviceSeq 連續）；預設斷言全部被接受</summary>
    private sealed class Feeder(HubTestHost host)
    {
        private long next = 1;

        public async Task<List<BatchEventResult>> SendAsync(IEnumerable<(string Kind, JObject Payload, string Conversation)> events, bool expectAccepted = true)
        {
            var results = new List<BatchEventResult>();
            foreach (var batch in events.Chunk(200))
            {
                var outcome = await ConversationEventLogTests.Append(host, Device,
                    [.. batch.Select(e => ConversationEventLogTests.Ev(next++, e.Kind, e.Payload, e.Conversation))]);
                outcome.Ack.Error.Should().BeNull();
                if (expectAccepted)
                    outcome.Ack.Results.Should().OnlyContain(r => r.Seq != null && r.Rejected == null);
                results.AddRange(outcome.Ack.Results);
            }
            return results;
        }
    }
}
