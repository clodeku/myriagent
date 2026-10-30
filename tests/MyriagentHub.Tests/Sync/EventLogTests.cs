using System.IO.Compression;
using System.Text;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 持久事件的寫入與拉取（設計 4.3、sync-protocol 第 5～6 節）：批次依到達順序處理、提交之後才確認、重送冪等、缺口與倒退整批不生效、
/// 被擋的仍佔 deviceSeq；目錄（邏輯帳戶自動連結、樂觀並行、偏好白名單、CLI 狀態、app 的更新狀態）與事件同一個交易；對帳模式；拉取的前綴保證
/// </summary>
public sealed class EventLogTests
{
    private const string Identity = "claude:org1:a@example.com";

    [Fact]
    public async Task 接受的事件配發seq並寫進events_同一批重送回原結果_不重複寫入()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        var batch = Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", Identity)), Ev(2, SyncEventKinds.PrefsUpdate, Pref("theme", "dark", 0)));

        var first = await AppendAsync(host, device, batch);
        first.Ack.Error.Should().BeNull();
        first.Ack.AcceptedThrough.Should().Be(2);
        first.Ack.Results.Should().HaveCount(2).And.OnlyContain(r => r.Seq != null && r.Rejected == null);

        var again = await AppendAsync(host, device, batch);
        again.Ack.Should().BeEquivalentTo(first.Ack, "重送（BatchAck 遺失）回同樣的結果");
        Count(host, "SELECT COUNT(*) FROM events WHERE author <> 'server'").Should().Be(2);
        Count(host, $"SELECT next_device_seq FROM device_streams WHERE device_id = '{device}'").Should().Be(3);
    }

    [Fact]
    public async Task 第一筆比期待的大_整批回gap與expected_沒有任何一筆生效()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        await AppendAsync(host, device, Batch(Ev(1, SyncEventKinds.PrefsUpdate, Pref("theme", "dark", 0))));

        var outcome = await AppendAsync(host, device, Batch(Ev(3, SyncEventKinds.PrefsUpdate, Pref("fontSize", 14, 0)), Ev(4, SyncEventKinds.PrefsUpdate, Pref("enterToSend", true, 0))));

        outcome.Ack.Error.Should().Be(new BatchError(SyncErrorCodes.Gap, 2));
        outcome.Ack.AcceptedThrough.Should().Be(1);
        outcome.Ack.Results.Should().BeEmpty();
        Count(host, "SELECT COUNT(*) FROM prefs").Should().Be(1, "缺口那批的物化一併不生效");
        Count(host, "SELECT COUNT(*) FROM events WHERE author <> 'server'").Should().Be(1);
    }

    [Fact]
    public async Task 同一個deviceSeq內容不同_seq_regression_整批不生效並記稽核()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        await AppendAsync(host, device, Batch(Ev(1, SyncEventKinds.PrefsUpdate, Pref("theme", "dark", 0))));

        // 倒退的裝置從 1 重新編號：第 1 筆內容不同（後面接一筆新的也一樣整批不生效）
        var outcome = await AppendAsync(host, device, Batch(Ev(1, SyncEventKinds.PrefsUpdate, Pref("theme", "light", 1)), Ev(2, SyncEventKinds.PrefsUpdate, Pref("fontSize", 15, 0))));

        outcome.Ack.Error!.Code.Should().Be(SyncErrorCodes.SeqRegression);
        outcome.Ack.AcceptedThrough.Should().Be(1);
        Count(host, "SELECT COUNT(*) FROM prefs").Should().Be(1);
        Count(host, $"SELECT next_device_seq FROM device_streams WHERE device_id = '{device}'").Should().Be(2);
        await WaitForAuditAsync(host, AuditActions.SyncSeqRegression);
    }

    [Fact]
    public async Task 批次結構錯誤_整批invalid_不佔deviceSeq()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);

        var outcome = await AppendAsync(host, device, Batch(Ev(1, SyncEventKinds.PrefsUpdate, Pref("theme", "dark", 0)), Ev(3, SyncEventKinds.PrefsUpdate, Pref("fontSize", 14, 0))));

        outcome.Ack.Error!.Code.Should().Be(SyncErrorCodes.Invalid);
        outcome.Ack.AcceptedThrough.Should().Be(0);
        Count(host, "SELECT COUNT(*) FROM events").Should().Be(0);
        Count(host, "SELECT COUNT(*) FROM rejected_events").Should().Be(0);
    }

    [Fact]
    public async Task 不是裝置能寫的種類_串流不符_帶對話欄位_雜湊不符_都被擋_仍佔deviceSeq_重送回同樣的原因()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        var good = Ev(5, SyncEventKinds.PrefsUpdate, Pref("theme", "dark", 0));
        var batch = Batch(
            Ev(1, SyncEventKinds.AccountUpsert, new { logicalAccountId = "la_x" }),
            Ev(2, SyncEventKinds.PrefsUpdate, Pref("theme", "dark", 0), stream: SyncStreams.Usage),
            Ev(3, SyncEventKinds.PrefsUpdate, Pref("theme", "dark", 0)) with { ConversationId = "c1", Epoch = 1 },
            good with { DeviceSeq = 4, PayloadSha256 = new string('0', 64) },
            good);

        var outcome = await AppendAsync(host, device, batch);

        outcome.Ack.Error.Should().BeNull();
        outcome.Ack.AcceptedThrough.Should().Be(5);
        outcome.Ack.Results.Take(4).Should().OnlyContain(r => r.Rejected == SyncErrorCodes.Invalid && r.Seq == null);
        outcome.Ack.Results[4].Seq.Should().NotBeNull("被擋的不影響同批後面的");
        Count(host, "SELECT COUNT(*) FROM rejected_events").Should().Be(4);
        Count(host, "SELECT COUNT(*) FROM events WHERE author <> 'server'").Should().Be(1);

        (await AppendAsync(host, device, batch)).Ack.Should().BeEquivalentTo(outcome.Ack);
        Count(host, "SELECT COUNT(*) FROM rejected_events").Should().Be(4);
    }

    [Fact]
    public async Task gzip解壓後超過上限_too_large_原文不留()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        var log = host.Services.GetRequiredService<EventLog>();
        var json = "{\"key\":\"theme\",\"value\":\"" + new string('a', log.Limits.MaxInlinePayload) + "\",\"baseVersion\":0}";
        var bomb = new SyncEvent(1, SyncStreams.Catalog, SyncEventKinds.PrefsUpdate, PayloadRules.EncGzip, PayloadRules.Sha256Hex(json), Gzip(json));

        var outcome = await AppendAsync(host, device, Batch(bomb));

        outcome.Ack.Results.Single().Rejected.Should().Be(SyncErrorCodes.TooLarge);
        Count(host, "SELECT COUNT(*) FROM rejected_events WHERE reason = 'too_large' AND payload IS NULL").Should().Be(1);
    }

    [Fact]
    public async Task link_upsert_新身分建立邏輯帳戶_第二台同身分連到同一個_改登別的帳號標identityOk_false()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await AddDeviceAsync(host);
        var b = await AddDeviceAsync(host);

        await AppendAsync(host, a, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-a", Identity, label: "工作"))));
        var account = LatestAccount(host);
        account.Label.Should().Be("工作");
        account.Links.Should().ContainSingle(l => l.DeviceId == a && l.IdentityOk);

        await AppendAsync(host, b, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-b", Identity, label: "另一台取的名字"))));
        var shared = LatestAccount(host);
        shared.LogicalAccountId.Should().Be(account.LogicalAccountId, "identityKey 相同就連到同一個邏輯帳戶");
        shared.Label.Should().Be("工作", "名稱以建立時的為準");
        shared.Links.Select(l => l.DeviceId).Should().BeEquivalentTo([a, b]);
        Count(host, "SELECT COUNT(*) FROM logical_accounts").Should().Be(1);

        // b 這個本機帳戶改登別的帳號：連結沿用（對應不變），標身分不符
        await AppendAsync(host, b, Batch(Ev(2, SyncEventKinds.LinkUpsert, Link("acct-b", "claude:org1:other@example.com", logical: account.LogicalAccountId))));
        LatestAccount(host).Links.Single(l => l.DeviceId == b).IdentityOk.Should().BeFalse();
        Count(host, "SELECT COUNT(*) FROM logical_accounts").Should().Be(1);
        await WaitForAuditAsync(host, AuditActions.AccountLinked, expected: 2);
    }

    [Fact]
    public async Task link_upsert_不知道身分時不建立帳戶_link_removed解除連結()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);

        var unknown = await AppendAsync(host, device, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", identity: null))));
        unknown.Ack.Results.Single().Seq.Should().NotBeNull("接受、只是沒有對應");
        Count(host, "SELECT COUNT(*) FROM logical_accounts").Should().Be(0);

        await AppendAsync(host, device, Batch(Ev(2, SyncEventKinds.LinkUpsert, Link("acct-1", Identity))));
        LatestAccount(host).Links.Should().ContainSingle();

        await AppendAsync(host, device, Batch(Ev(3, SyncEventKinds.LinkRemoved, new LinkRemovedPayload("acct-1"))));
        LatestAccount(host).Links.Should().BeEmpty();
        Count(host, "SELECT COUNT(*) FROM account_links").Should().Be(0);
    }

    [Fact]
    public async Task account_update_版本相符才套用_不符回conflict_帳戶不存在回invalid且事件一併回滾()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        await AppendAsync(host, device, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", Identity))));
        var account = LatestAccount(host);
        var serverEvents = Count(host, "SELECT COUNT(*) FROM events WHERE author = 'server'");

        var outcome = await AppendAsync(host, device, Batch(
            Ev(2, SyncEventKinds.AccountUpdate, new AccountUpdatePayload(account.LogicalAccountId, account.Version, "改名", "#123456", 3)),
            Ev(3, SyncEventKinds.AccountUpdate, new AccountUpdatePayload(account.LogicalAccountId, account.Version, "慢一步", null, null)),
            Ev(4, SyncEventKinds.AccountUpdate, new AccountUpdatePayload(SyncIds.NewLogicalAccountId(), 1, "不存在", null, null))));

        outcome.Ack.Results.Select(r => r.Rejected).Should().Equal(null, CatalogRejections.Conflict, SyncErrorCodes.Invalid);
        var updated = LatestAccount(host);
        updated.Label.Should().Be("改名");
        updated.Color.Should().Be("#123456");
        updated.SortOrder.Should().Be(3);
        updated.Version.Should().Be(account.Version + 1);
        Count(host, "SELECT COUNT(*) FROM events WHERE author = 'server'").Should().Be(serverEvents + 1, "被擋的不發布任何伺服器事件");
        Count(host, "SELECT COUNT(*) FROM events WHERE author <> 'server'").Should().Be(2, "被擋的事件本身也不在 events");
        Count(host, "SELECT COUNT(*) FROM rejected_events").Should().Be(2);
    }

    [Fact]
    public async Task prefs_update_只收白名單_版本相符才套用()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);

        var outcome = await AppendAsync(host, device, Batch(
            Ev(1, SyncEventKinds.PrefsUpdate, Pref("theme", "dark", 0)),
            Ev(2, SyncEventKinds.PrefsUpdate, Pref("theme", "light", 0)),
            Ev(3, SyncEventKinds.PrefsUpdate, Pref("theme", "light", 1)),
            Ev(4, SyncEventKinds.PrefsUpdate, Pref("instructions", "rm -rf", 0)),
            Ev(5, SyncEventKinds.PrefsUpdate, Pref("fontSize", 99, 0))));

        outcome.Ack.Results.Select(r => r.Rejected).Should().Equal(null, CatalogRejections.Conflict, null, SyncErrorCodes.Invalid, SyncErrorCodes.Invalid);
        var set = ServerPayloads<PrefsSetPayload>(host, SyncEventKinds.PrefsSet);
        set.Select(p => ((string?)p.Value, p.Version)).Should().Equal(("dark", 1L), ("light", 2L));
    }

    [Fact]
    public async Task cli_state與device_update發布device_upsert_名稱清理()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        var cli = new CliStatePayload(new Dictionary<string, DeviceCliView>
        {
            ["Claude"] = new(true, "2.1.0", "native", "2.2.0", true, 1, 0, null, 1000),
        });

        var outcome = await AppendAsync(host, device, Batch(
            Ev(1, SyncEventKinds.CliState, cli),
            Ev(2, SyncEventKinds.DeviceUpdate, new DeviceUpdatePayload(device, "  客廳\u0007電腦  ")),
            Ev(3, SyncEventKinds.CliState, new CliStatePayload(new Dictionary<string, DeviceCliView> { ["Gemini"] = cli.Providers["Claude"] }))));

        outcome.Ack.Results.Select(r => r.Rejected).Should().Equal(null, null, SyncErrorCodes.Invalid);
        var upserts = ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert);
        upserts.Should().HaveCount(2);
        upserts[0].Cli!["Claude"].Version.Should().Be("2.1.0");
        upserts[1].Name.Should().Be("客廳電腦");
        upserts[1].Cli.Should().ContainKey("Claude", "改名不動 CLI 狀態");
        upserts[1].Fingerprint.Should().MatchRegex("^[0-9A-Z]{4}-[0-9A-Z]{4}$", "裝置指紋 8 字");
    }

    [Fact]
    public async Task app_state寫入device_app並發出含app的device_upsert()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        var staged = new AppStatePayload("0.2.300", "0.2.300-ab12cd3", AppStatePhases.Staged, "0.2.301-0123abc", null, 1000);
        // 第二筆以匿名物件送：lastError 夾雙向覆寫字元（以碼位在執行時組出來），另帶一個伺服器不認得的欄位
        var rolledBack = new
        {
            version = "0.2.300",
            release = "0.2.300-ab12cd3",
            phase = AppStatePhases.RolledBack,
            lastError = "新版" + (char)0x202E + "啟動失敗",
            checkedAt = 2000L,
            futureField = "不轉存",
        };
        var cleaned = new AppStatePayload("0.2.300", "0.2.300-ab12cd3", AppStatePhases.RolledBack, null, "新版啟動失敗", 2000);

        var outcome = await AppendAsync(host, device, Batch(Ev(1, SyncEventKinds.AppState, staged), Ev(2, SyncEventKinds.AppState, rolledBack)));

        outcome.Ack.Results.Should().HaveCount(2).And.OnlyContain(r => r.Seq != null && r.Rejected == null);
        Count(host, "SELECT COUNT(*) FROM device_app").Should().Be(1, "每台一列，新的取代舊的");
        Count(host, $"SELECT updated_at FROM device_app WHERE device_id = '{device}'").Should().Be(host.Clock.NowMs, "伺服器收到的時間，不是裝置的 checkedAt");
        using (var read = host.Services.GetRequiredService<SyncWriter>().OpenRead())
        {
            SqliteSetup.Scalar(read, $"SELECT state_json FROM device_app WHERE device_id = '{device}'")
                .Should().Be(SyncJson.Serialize(cleaned), "存整理過的內容：去掉格式字元、不認得的欄位不存");
        }
        var upserts = ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert);
        upserts.Should().HaveCount(2);
        upserts[0].App.Should().Be(staged);
        upserts[1].App.Should().Be(cleaned);

        // 之後因為別的事發布的 device.upsert（改名）也帶最新的 app
        await AppendAsync(host, device, Batch(Ev(3, SyncEventKinds.DeviceUpdate, new DeviceUpdatePayload(device, "客廳電腦"))));
        var renamed = ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert)[^1];
        renamed.Name.Should().Be("客廳電腦");
        renamed.App.Should().Be(cleaned);
    }

    [Fact]
    public async Task 格式不對的app_state回invalid不寫入()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        var ok = new AppStatePayload("0.2.300", null, AppStatePhases.Idle, null, null, 1000);

        var outcome = await AppendAsync(host, device, Batch(
            Ev(1, SyncEventKinds.AppState, ok with { Phase = "updating" }),
            Ev(2, SyncEventKinds.AppState, ok with { Version = "0.2" }),
            Ev(3, SyncEventKinds.AppState, ok with { Release = "0.2.300-dev" }),
            Ev(4, SyncEventKinds.AppState, ok with { CheckedAt = 0 }),
            Ev(5, SyncEventKinds.AppState, new[] { 1, 2 }),
            Ev(6, SyncEventKinds.AppState, new { version = "0.2.300", phase = "idle", checkedAt = "不是數字" }),
            Ev(7, SyncEventKinds.AppState, ok, stream: SyncStreams.Usage)));

        outcome.Ack.Error.Should().BeNull();
        outcome.Ack.AcceptedThrough.Should().Be(7);
        outcome.Ack.Results.Should().HaveCount(7).And.OnlyContain(r => r.Rejected == SyncErrorCodes.Invalid && r.Seq == null);
        Count(host, "SELECT COUNT(*) FROM rejected_events WHERE kind = 'app.state'").Should().Be(7, "被擋的仍佔 deviceSeq");
        Count(host, "SELECT COUNT(*) FROM events WHERE kind = 'app.state'").Should().Be(0, "物化拒絕時連事件一起回滾");
        Count(host, "SELECT COUNT(*) FROM device_app").Should().Be(0);
        ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert).Should().BeEmpty("被擋的不發布 device.upsert");

        // 被擋過之後，合格的照常收
        var accepted = await AppendAsync(host, device, Batch(Ev(8, SyncEventKinds.AppState, ok)));
        accepted.Ack.Results.Single().Seq.Should().NotBeNull();
        ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert).Single().App.Should().Be(ok);
    }

    [Fact]
    public async Task device_app的內容壞掉_device_upsert照樣發布_沒有app()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(c =>
            c.Execute("INSERT OR REPLACE INTO device_app(device_id, state_json, updated_at) VALUES($d, '{', 1)", ("$d", device)));

        var outcome = await AppendAsync(host, device, Batch(Ev(1, SyncEventKinds.DeviceUpdate, new DeviceUpdatePayload(device, "客廳電腦"))));

        outcome.Ack.Results.Single().Seq.Should().NotBeNull("讀不出來的 app 狀態不擋 device.upsert（每次 Hello 都經 EmitDevice）");
        var upsert = ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert).Single();
        upsert.Name.Should().Be("客廳電腦");
        upsert.App.Should().BeNull();
    }

    [Fact]
    public async Task 對帳模式_缺口從保留窗起點接受_舊事件缺的補上_追上Hello的位置後解除()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        var writer = host.Services.GetRequiredService<SyncWriter>();
        await AppendAsync(host, device, Batch(Ev(1, SyncEventKinds.PrefsUpdate, Pref("theme", "dark", 0)), Ev(2, SyncEventKinds.PrefsUpdate, Pref("fontSize", 14, 0))));
        // 還原：備份裡沒有第 2 筆（期待值仍是 3 的情況由不一致的資料造成）、這台標成要對帳
        await writer.WriteAsync(c =>
        {
            c.Execute("DELETE FROM events WHERE author = $d AND device_seq = 2", ("$d", device));
            c.Execute("UPDATE device_streams SET needs_resync = 1 WHERE device_id = $d", ("$d", device));
        });

        // 一般模式下這筆是 Stale：不套用、回成已提交（不帶 seq）
        var missing = Ev(2, SyncEventKinds.PrefsUpdate, Pref("notifications", true, 0));
        var stale = await AppendAsync(host, device, Batch(missing));
        stale.Ack.Results.Single().Should().Be(new BatchEventResult(2));
        Count(host, "SELECT COUNT(*) FROM prefs WHERE key = 'notifications'").Should().Be(0);

        // 對帳模式：Hello 說 next=8、保留窗從 2 起；伺服器期待 3。2 是 Missing（補上）、3 起照常、7 之後追上
        var hello = new OutboxPosition(8, 2, 2);
        var upstream = new UpstreamContext(device, OutboxRules.ResyncFrom(hello), hello);
        var events = new[] { missing }
            .Concat(Enumerable.Range(3, 5).Select(i => Ev(i, SyncEventKinds.PrefsUpdate, Pref("enterToSend", i % 2 == 0, i - 3))))
            .ToArray();
        var outcome = await host.Services.GetRequiredService<EventLog>().AppendBatchAsync(upstream, Batch(events));

        outcome.Ack.Results.Should().OnlyContain(r => r.Seq != null && r.Rejected == null);
        outcome.ResyncDone.Should().BeTrue();
        Count(host, $"SELECT needs_resync FROM device_streams WHERE device_id = '{device}'").Should().Be(0);
        Count(host, $"SELECT next_device_seq FROM device_streams WHERE device_id = '{device}'").Should().Be(8);
        await WaitForAuditAsync(host, AuditActions.SyncResync, expected: 2);
    }

    [Fact]
    public async Task 對帳模式_同一批先補上Missing再遇到倒退_整批回滾_提交後的通知也丟掉()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        var writer = host.Services.GetRequiredService<SyncWriter>();
        await AppendAsync(host, device, Batch(Ev(1, SyncEventKinds.PrefsUpdate, Pref("theme", "dark", 0)), Ev(2, SyncEventKinds.PrefsUpdate, Pref("fontSize", 14, 0))));
        await writer.WriteAsync(c =>
        {
            c.Execute("DELETE FROM events WHERE author = $d AND device_seq = 1", ("$d", device));
            c.Execute("UPDATE device_streams SET needs_resync = 1 WHERE device_id = $d", ("$d", device));
        });
        var notifier = host.Services.GetRequiredService<SyncNotifier>();
        var high = notifier.High;
        var committed = new List<long>();
        void OnCommitted(string cls, long seq)
        {
            lock (committed)
                committed.Add(seq);
        }
        notifier.EventCommitted += OnCommitted;

        // 1 是 Missing（套用、配發 seq、登記提交後的通知）；2 的內容與伺服器存的不同＝倒退
        var hello = new OutboxPosition(3, 1, 1);
        var upstream = new UpstreamContext(device, OutboxRules.ResyncFrom(hello), hello);
        var outcome = await host.Services.GetRequiredService<EventLog>().AppendBatchAsync(upstream,
            Batch(Ev(1, SyncEventKinds.PrefsUpdate, Pref("notifications", true, 0)), Ev(2, SyncEventKinds.PrefsUpdate, Pref("fontSize", 18, 0))));
        notifier.EventCommitted -= OnCommitted;

        outcome.Ack.Error!.Code.Should().Be(SyncErrorCodes.SeqRegression);
        Count(host, $"SELECT COUNT(*) FROM events WHERE author = '{device}' AND device_seq = 1").Should().Be(0, "補上的那筆一起回滾");
        Count(host, "SELECT COUNT(*) FROM prefs WHERE key = 'notifications'").Should().Be(0);
        lock (committed)
            committed.Should().BeEmpty("回滾的事件不發通知");
        notifier.High.Should().Be(high);
        await WaitForAuditAsync(host, AuditActions.SyncSeqRegression);
    }

    [Fact]
    public async Task 撤銷的裝置_批次不寫入()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        var directory = host.Services.GetRequiredService<DeviceDirectory>();
        // 撤銷先標目錄、才排 RevokeDevice：在那之前已排隊的批次在寫入者上看得到撤銷
        directory.Put(directory.Get(device)! with { RevokedAt = host.Clock.NowMs, RevokedReason = "compromised" });

        await FluentActions.Awaiting(() => AppendAsync(host, device, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", Identity)))))
            .Should().ThrowAsync<DeviceRevokedException>();
        Count(host, $"SELECT COUNT(*) FROM events WHERE author = '{device}'").Should().Be(0);
        Count(host, "SELECT COUNT(*) FROM account_links").Should().Be(0);
    }

    [Fact]
    public async Task 對帳模式_備份比保留窗舊_從起點接受缺口()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        await AppendAsync(host, device, Batch(Ev(1, SyncEventKinds.PrefsUpdate, Pref("theme", "dark", 0))));

        var hello = new OutboxPosition(12, 10, 10);
        var upstream = new UpstreamContext(device, OutboxRules.ResyncFrom(hello), hello);
        var log = host.Services.GetRequiredService<EventLog>();

        var accepted = await log.AppendBatchAsync(upstream, Batch(Ev(10, SyncEventKinds.PrefsUpdate, Pref("fontSize", 14, 0)), Ev(11, SyncEventKinds.PrefsUpdate, Pref("fontSize", 15, 1))));
        accepted.Ack.Error.Should().BeNull("從保留窗起點來的缺口補不回來，接受");
        accepted.Ack.Results.Should().OnlyContain(r => r.Seq != null);
        accepted.ResyncDone.Should().BeTrue();
        Count(host, $"SELECT next_device_seq FROM device_streams WHERE device_id = '{device}'").Should().Be(12);

        var later = await log.AppendBatchAsync(upstream, Batch(Ev(14, SyncEventKinds.PrefsUpdate, Pref("fontSize", 16, 2))));
        later.Ack.Error.Should().Be(new BatchError(SyncErrorCodes.Gap, 12), "期待值已越過起點：之後的缺口照常要從期待值重送");
        await WaitForAuditAsync(host, AuditActions.SyncResync);
    }

    [Fact]
    public async Task 拉取_各游標拿到前綴_依seq合併_筆數與位元組上限_more與high()
    {
        await using var host = await HubTestHost.StartAsync();
        var device = await AddDeviceAsync(host);
        var writer = host.Services.GetRequiredService<SyncWriter>();
        var notifier = host.Services.GetRequiredService<ISyncNotifier>();
        // 目錄與用量交錯：catalog 1、usage 2、catalog 3、usage 4…
        for (var i = 0; i < 6; i++)
        {
            if (i % 2 == 0)
                await AppendAsync(host, device, Batch(Ev(i / 2 + 1, SyncEventKinds.PrefsUpdate, Pref("fontSize", 12 + i / 2, i / 2))));
            else
                await writer.WriteAsync(c => SyncEventStore.InsertServer(c, notifier, SyncEventKinds.UsageSnapshot, new { n = i }));
        }
        var log = host.Services.GetRequiredService<EventLog>();
        var both = new[] { new PullCursor(0, Cls: SyncClasses.Catalog), new PullCursor(0, Cls: SyncClasses.Usage) };

        var all = log.Pull(new PullRequest(both, 100, 1_000_000));
        all.More.Should().BeFalse();
        all.Events.Select(e => e.Seq).Should().BeInAscendingOrder();
        all.High.Should().Be(all.Events[^1].Seq);
        var catalogCount = all.Events.Count(e => e.Cls == SyncClasses.Catalog);

        var page = log.Pull(new PullRequest(both, 3, 1_000_000));
        page.More.Should().BeTrue();
        page.Events.Select(e => e.Seq).Should().Equal(all.Events.Take(3).Select(e => e.Seq), "前綴：不會先給後面的 seq 而跳過前面的");

        var usageOnly = log.Pull(new PullRequest([new PullCursor(page.Events.Where(e => e.Cls == SyncClasses.Usage).Select(e => e.Seq).DefaultIfEmpty(0).Max(), Cls: SyncClasses.Usage)], 100, 1_000_000));
        usageOnly.Events.Should().OnlyContain(e => e.Cls == SyncClasses.Usage);

        var tiny = log.Pull(new PullRequest(both, 100, 1));
        tiny.Events.Should().HaveCount(1, "位元組上限再小也至少給一筆");
        tiny.More.Should().BeTrue();

        var byStream = log.Pull(new PullRequest([new PullCursor(0, Stream: SyncStreams.Catalog)], 100, 1_000_000));
        byStream.Events.Should().HaveCount(catalogCount);

        foreach (var bad in new PullRequest?[]
        {
            null,
            new([], 10, 10),
            new([new PullCursor(0)], 10, 10),
            new([new PullCursor(0, Cls: SyncClasses.Catalog, Stream: SyncStreams.Catalog)], 10, 10),
            new([new PullCursor(0, Cls: "secrets")], 10, 10),
            new([new PullCursor(-1, Cls: SyncClasses.Catalog)], 10, 10),
        })
            FluentActions.Invoking(() => log.Pull(bad)).Should().Throw<PullRequestException>();
    }

    // ───── 工具 ─────

    internal static async Task<string> AddDeviceAsync(HubTestHost host, string name = "測試電腦")
    {
        var record = new DeviceRecord
        {
            Id = SyncIds.NewDeviceId(),
            Name = name,
            TokenHash = Guid.NewGuid().ToByteArray(),
            DevicePubkey = [1, 2, 3, 4],
            KeyStorage = "software",
            CreatedAt = 1,
        };
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            db.Devices.Add(record);
            await db.SaveChangesAsync();
        }
        host.Services.GetRequiredService<DeviceDirectory>().Put(DeviceInfo.From(record));
        return record.Id;
    }

    internal static Task<BatchOutcome> AppendAsync(HubTestHost host, string deviceId, Batch batch) =>
        host.Services.GetRequiredService<EventLog>().AppendBatchAsync(new UpstreamContext(deviceId, null, null), batch);

    internal static Batch Batch(params SyncEvent[] events) => new(SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), events);

    internal static SyncEvent Ev(long deviceSeq, string kind, object payload, string? stream = null)
    {
        var json = SyncJson.Serialize(payload);
        var (enc, encoded) = PayloadRules.Encode(json);
        return new SyncEvent(deviceSeq, stream ?? SyncStreams.Catalog, kind, enc, PayloadRules.Sha256Hex(json), encoded);
    }

    internal static LinkUpsertPayload Link(string local, string? identity, string? logical = null, string label = "工作帳號") =>
        new(local, "Claude", "isolated", identity, logical, "ready", null, "a@example.com", "org1", "max", null, null, false, new LinkSessions(1, 0, 0), label, "#D97757", 0, 1000);

    internal static PrefsUpdatePayload Pref(string key, object value, long baseVersion) => new(key, JToken.FromObject(value), baseVersion);

    internal static long Count(HubTestHost host, string sql)
    {
        using var read = host.Services.GetRequiredService<SyncWriter>().OpenRead();
        return (long)SqliteSetup.Scalar(read, sql)!;
    }

    internal static List<T> ServerPayloads<T>(HubTestHost host, string kind)
    {
        var pulled = host.Services.GetRequiredService<EventLog>().Pull(new PullRequest([new PullCursor(0, Cls: SyncClasses.Catalog), new PullCursor(0, Cls: SyncClasses.Usage)], 500, 4_000_000));
        return pulled.Events.Where(e => e.Kind == kind && e.Author == SyncEventStore.ServerAuthor)
            .Select(e => PayloadRules.TryDecode(e.Enc, e.Payload, 1 << 22, out var json) ? SyncJson.Deserialize<T>(json)! : throw new InvalidOperationException("payload 解不開"))
            .ToList();
    }

    internal static AccountUpsertPayload LatestAccount(HubTestHost host) => ServerPayloads<AccountUpsertPayload>(host, SyncEventKinds.AccountUpsert)[^1];

    internal static async Task WaitForAuditAsync(HubTestHost host, string action, int expected = 1)
    {
        // 稽核在提交之後丟到執行緒池寫
        for (var i = 0; i < 100; i++)
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            if (await db.Audit.CountAsync(a => a.Action == action) >= expected)
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException("等不到稽核：" + action);
    }

    private static string Gzip(string json)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(json));
        return Convert.ToBase64String(buffer.ToArray());
    }
}
