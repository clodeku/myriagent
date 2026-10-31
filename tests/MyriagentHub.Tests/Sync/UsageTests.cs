using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using static MyriagentHub.Tests.Sync.EventLogTests;

namespace MyriagentHub.Tests.Sync;

/// <summary>共享用量的指派規則（sync-protocol 6.4）</summary>
public sealed class UsageRulesTests
{
    private const long Now = 10_000_000;

    [Fact]
    public void 候選_連結正常_身分相符_在線_心跳60秒內_Claude權杖還有5分鐘以上()
    {
        var ok = Candidate("d_A");
        UsageRules.IsCandidate(ok, "Claude", Now).Should().BeTrue();
        UsageRules.IsCandidate(ok with { Status = "notLoggedIn" }, "Claude", Now).Should().BeFalse();
        UsageRules.IsCandidate(ok with { IdentityOk = false }, "Claude", Now).Should().BeFalse();
        UsageRules.IsCandidate(ok with { Online = false }, "Claude", Now).Should().BeFalse();
        UsageRules.IsCandidate(ok with { LastHeartbeatAt = Now - 60_001 }, "Claude", Now).Should().BeFalse();
        UsageRules.IsCandidate(ok with { LastHeartbeatAt = Now - 60_000 }, "Claude", Now).Should().BeTrue();
        UsageRules.IsCandidate(ok with { AccessExpiresAt = Now + 299_999 }, "Claude", Now).Should().BeFalse();
        UsageRules.IsCandidate(ok with { AccessExpiresAt = Now + 300_000 }, "Claude", Now).Should().BeTrue();
        UsageRules.IsCandidate(ok with { AccessExpiresAt = Now + 1 }, "Codex", Now).Should().BeTrue("Codex 不看存取權杖");
    }

    [Fact]
    public void 選人_執行中對話優先_其次上一任_再來deviceId最小()
    {
        var a = Candidate("d_A");
        var b = Candidate("d_B");
        var c = Candidate("d_C");
        UsageRules.Choose([c, b, a], null, "Claude", Now).Should().Be("d_A");
        UsageRules.Choose([c, b, a], "d_B", "Claude", Now).Should().Be("d_B", "上一任還合格就不換");
        UsageRules.Choose([c, b, a with { Online = false }], "d_A", "Claude", Now).Should().Be("d_B");
        UsageRules.Choose([c with { RunningSessions = 2 }, b, a], "d_B", "Claude", Now).Should().Be("d_C", "有執行中對話的優先");
        UsageRules.Choose([c with { RunningSessions = 1 }, b with { RunningSessions = 1 }, a], "d_C", "Claude", Now).Should().Be("d_C");
        UsageRules.Choose([a with { Status = "error" }], "d_A", "Claude", Now).Should().BeNull();
        UsageRules.Choose([], "", "Claude", Now).Should().BeNull();
    }

    [Fact]
    public void 選人_每台的Claude權杖都快到期_仍指派一台讓它換發()
    {
        var expired = Candidate("d_A") with { AccessExpiresAt = Now - 1 };
        var expiring = Candidate("d_B") with { AccessExpiresAt = Now + 299_999 };
        var fresh = Candidate("d_C") with { AccessExpiresAt = Now + 300_000 };
        UsageRules.Choose([expiring, expired], null, "Claude", Now).Should().Be("d_A", "都過期了照一般的順序挑，不能沒有人查（也就沒有人換發）");
        UsageRules.Choose([expiring, expired], "d_B", "Claude", Now).Should().Be("d_B", "上一任照樣不換");
        UsageRules.Choose([expiring, expired with { RunningSessions = 1 }], "d_B", "Claude", Now).Should().Be("d_A", "有執行中對話的優先");
        UsageRules.Choose([fresh, expired], "d_A", "Claude", Now).Should().Be("d_C", "有權杖還有效的就交給它");
        UsageRules.Choose([expired with { Online = false }, expiring with { IdentityOk = false }], null, "Claude", Now).Should().BeNull("不在線、身分不符的照樣不指派");
    }

    [Fact]
    public void 上傳的格式_來源與term相符_時間窗有上限()
    {
        var api = new UsagePublish(SyncIds.NewLogicalAccountId(), 3, Usage(UsageRules.SourceApi));
        UsageRules.Validate(api).Should().BeNull();
        UsageRules.Validate(new UsagePublish(api.LogicalAccountId, null, Usage(UsageRules.SourceSession))).Should().BeNull();
        UsageRules.Validate(api with { Term = null }).Should().NotBeNull("不帶 term 的只能是對話中的事件");
        UsageRules.Validate(api with { Term = 0 }).Should().NotBeNull();
        UsageRules.Validate(api with { LogicalAccountId = "x" }).Should().NotBeNull();
        UsageRules.Validate(api with { Usage = Usage(UsageRules.SourceApi, double.NaN) }).Should().NotBeNull();
        UsageRules.Validate(api with { Usage = Usage(UsageRules.SourceApi, -1) }).Should().NotBeNull();
        var window = api.Usage.Windows[0];
        UsageRules.Validate(api with { Usage = api.Usage with { Windows = [window, window] } }).Should().NotBeNull("鍵重複");
        UsageRules.Validate(api with { Usage = api.Usage with { Windows = Enumerable.Range(0, 9).Select(i => window with { Key = "w" + i }).ToList() } }).Should().NotBeNull();
        UsageRules.Validate(api with { Usage = api.Usage with { Windows = [window with { Key = "a b" }] } }).Should().NotBeNull();
        UsageRules.Validate(api with { Usage = api.Usage with { Windows = [window with { Label = "5\n小時" }] } }).Should().NotBeNull();
        UsageRules.Validate(api with { Usage = api.Usage with { Error = new string('x', 501) } }).Should().NotBeNull();
        UsageRules.Validate(null).Should().NotBeNull();
        UsageRules.Validate(api with { Usage = api.Usage with { ResetCredits = 0, ResetCreditExpiresAt = 1 } }).Should().BeNull();
        UsageRules.Validate(api with { Usage = api.Usage with { ResetCredits = UsageRules.MaxResetCredits } }).Should().BeNull();
        UsageRules.Validate(api with { Usage = api.Usage with { ResetCredits = -1 } }).Should().NotBeNull();
        UsageRules.Validate(api with { Usage = api.Usage with { ResetCredits = UsageRules.MaxResetCredits + 1 } }).Should().NotBeNull();
        UsageRules.Validate(api with { Usage = api.Usage with { ResetCreditExpiresAt = 0 } }).Should().NotBeNull();
    }

    [Fact]
    public void 裝置跳脫後的鍵伺服器都收()
    {
        // 時間窗鍵的線上格式是桌面與伺服器共用的 UsageWindowKeyRules（Protocol）：跳脫得出來的鍵一定通過 Validate
        UsageRules.MaxWindowKeyLength.Should().Be(UsageWindowKeyRules.MaxLength);
        var window = Usage(UsageRules.SourceApi).Windows[0];
        foreach (var key in new[] { "weekly:Opus", "weekly:claude-opus-4.5", "codex:10080", "a b", "x_y", new string(':', 16) })
        {
            var encoded = UsageWindowKeyRules.Encode(key)!;
            var publish = new UsagePublish(SyncIds.NewLogicalAccountId(), 3, Usage(UsageRules.SourceApi) with { Windows = [window with { Key = encoded }] });
            UsageRules.Validate(publish).Should().BeNull(key);
        }
    }

    internal static SharedUsage Usage(string source, double percent = 42) =>
        new(1000, source, [new SharedUsageWindow("five_hour", "5 小時", percent, 2000, false)], null, null, null, false);

    private static PollerCandidate Candidate(string deviceId) => new(deviceId, "ready", true, null, 0, true, Now - 1000);
}

/// <summary>用量查詢的指派與共享用量的寫入（真的 sync.db、連線登記）</summary>
public sealed class UsageCoordinatorTests
{
    [Fact]
    public async Task 在線且合格的裝置中指派一台_term遞增_矩陣標負責查詢_沒心跳60秒改派_新舊各送一次()
    {
        await using var host = await HubTestHost.StartAsync();
        var (coordinator, sent) = Coordinator(host);
        var a = await AddDeviceAsync(host);
        var b = await AddDeviceAsync(host);
        var (first, second) = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
        await LinkAsync(host, a, 1);
        await LinkAsync(host, b, 1);
        var la = LatestAccount(host).LogicalAccountId;

        (await coordinator.ReassessAsync()).Should().BeEmpty("沒有在線的裝置");

        Online(host, first, "c1");
        Online(host, second, "c2");
        await coordinator.ReassessAsync();
        sent.Should().Equal((first, new UsageAssign(la, 1, true)));
        coordinator.AssignmentsFor(first).Should().Equal(new UsageAssign(la, 1, true));
        LatestAccount(host).Links.Single(l => l.UsagePoller).DeviceId.Should().Be(first);

        // 第二台持續心跳、第一台 61 秒沒有（背景的定期重算也可能先做掉：送出的訊息一樣）
        sent.Clear();
        host.Clock.Advance(TimeSpan.FromSeconds(61));
        Connections(host).Heartbeat(second, host.Clock.NowMs);
        await coordinator.ReassessAsync();
        sent.Should().Equal((first, new UsageAssign(la, 2, false)), (second, new UsageAssign(la, 2, true)));
        coordinator.AssignmentsFor(first).Should().BeEmpty();
        LatestAccount(host).Links.Single(l => l.UsagePoller).DeviceId.Should().Be(second);

        // 第一台恢復心跳：上一任（第二台）還合格就不換回去
        sent.Clear();
        Connections(host).Heartbeat(first, host.Clock.NowMs);
        await coordinator.ReassessAsync();
        sent.Should().BeEmpty();

        // 都離線：沒有人負責（term 照樣前進）
        Connections(host).Remove("c1");
        Connections(host).Remove("c2");
        await coordinator.ReassessAsync();
        sent.Should().Equal((second, new UsageAssign(la, 3, false)));
        Count(host, $"SELECT COUNT(*) FROM usage_pollers WHERE device_id = '' AND term = 3").Should().Be(1);
    }

    [Fact]
    public async Task 有執行中對話的那台優先_連結變不合格就改派()
    {
        await using var host = await HubTestHost.StartAsync();
        var (coordinator, sent) = Coordinator(host);
        var a = await AddDeviceAsync(host);
        var b = await AddDeviceAsync(host);
        var (first, second) = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
        await LinkAsync(host, first, 1);
        await LinkAsync(host, second, 1, running: 1);
        var la = LatestAccount(host).LogicalAccountId;
        Online(host, second, "c2");
        Online(host, first, "c1");

        await coordinator.ReassessAsync();
        coordinator.AssignmentsFor(second).Should().Equal(new UsageAssign(la, 1, true));
        sent.Should().Equal((second, new UsageAssign(la, 1, true)));

        // 第二台的帳戶登出了
        await LinkAsync(host, second, 2, status: "notLoggedIn", logical: la);
        await coordinator.ReassessAsync();
        coordinator.AssignmentsFor(first).Should().Equal(new UsageAssign(la, 2, true));
    }

    [Fact]
    public async Task 上傳_只採用目前負責那台目前term的_不帶term的要有連結且不能太頻繁_寫最新與歷史並發布()
    {
        await using var host = await HubTestHost.StartAsync();
        var (coordinator, _) = Coordinator(host);
        var a = await AddDeviceAsync(host);
        var b = await AddDeviceAsync(host);
        var outsider = await AddDeviceAsync(host);
        await LinkAsync(host, a, 1);
        await LinkAsync(host, b, 1);
        var la = LatestAccount(host).LogicalAccountId;
        Online(host, a, "c1");
        await coordinator.ReassessAsync();
        var api = UsageRulesTests.Usage(UsageRules.SourceApi, 30);

        (await coordinator.PublishAsync(a, new UsagePublish(la, 1, api))).Should().Be(UsagePublishResult.Accepted);
        (await coordinator.PublishAsync(a, new UsagePublish(la, 7, api))).Should().Be(UsagePublishResult.Ignored, "不是目前的 term");
        (await coordinator.PublishAsync(b, new UsagePublish(la, 1, api))).Should().Be(UsagePublishResult.Ignored, "不是負責的那台");
        (await coordinator.PublishAsync(a, new UsagePublish(la, null, api))).Should().Be(UsagePublishResult.Invalid);

        var session = UsageRulesTests.Usage(UsageRules.SourceSession, 55);
        (await coordinator.PublishAsync(b, new UsagePublish(la, null, session))).Should().Be(UsagePublishResult.Accepted, "連結正常的任何一台都能分享對話中的事件");
        (await coordinator.PublishAsync(b, new UsagePublish(la, null, session))).Should().Be(UsagePublishResult.Ignored, "太頻繁");
        (await coordinator.PublishAsync(outsider, new UsagePublish(la, null, session))).Should().Be(UsagePublishResult.Ignored, "沒有連結這個帳戶");
        host.Clock.Advance(TimeSpan.FromSeconds(60));
        (await coordinator.PublishAsync(b, new UsagePublish(la, null, session with { Windows = [session.Windows[0] with { Percent = 60 }] }))).Should().Be(UsagePublishResult.Accepted);

        var snapshots = ServerPayloads<UsageSnapshotPayload>(host, SyncEventKinds.UsageSnapshot);
        snapshots.Select(s => (s.SourceDeviceId, s.Term, s.Usage.Windows[0].Percent)).Should().Equal((a, 1L, 30.0), (b, (long?)null, 55.0), (b, (long?)null, 60.0));
        Count(host, $"SELECT COUNT(*) FROM usage_points WHERE logical_account_id = '{la}'").Should().Be(2, "同一個時間點、同一個時間窗只留最後一筆");
        Count(host, $"SELECT COUNT(*) FROM usage_latest WHERE logical_account_id = '{la}' AND source = 'session' AND source_device_id = '{b}'").Should().Be(1);

        // 改登別的帳號的那台不採用
        await LinkAsync(host, b, 2, identity: "claude:org1:other@example.com", logical: la);
        host.Clock.Advance(TimeSpan.FromSeconds(60));
        (await coordinator.PublishAsync(b, new UsagePublish(la, null, session))).Should().Be(UsagePublishResult.Ignored);

        // 歷史點保留 14 天（期間 a 的心跳逾時過，背景重算可能已經改派、term 前進了：以目前的指派上傳）
        host.Clock.Advance(TimeSpan.FromDays(15));
        Connections(host).Heartbeat(a, host.Clock.NowMs);
        await coordinator.ReassessAsync();
        var term = coordinator.AssignmentsFor(a).Single().Term;
        (await coordinator.PublishAsync(a, new UsagePublish(la, term, api))).Should().Be(UsagePublishResult.Accepted);
        Count(host, $"SELECT COUNT(*) FROM usage_points WHERE logical_account_id = '{la}'").Should().Be(1);
    }

    [Fact]
    public async Task 不帶term的上傳_連結不正常的不收_同時送兩個只採用一個()
    {
        await using var host = await HubTestHost.StartAsync();
        var (coordinator, _) = Coordinator(host);
        var a = await AddDeviceAsync(host);
        var b = await AddDeviceAsync(host);
        await LinkAsync(host, a, 1);
        var la = LatestAccount(host).LogicalAccountId;
        await LinkAsync(host, b, 1, status: "notLoggedIn", logical: la);
        var session = UsageRulesTests.Usage(UsageRules.SourceSession, 55);

        (await coordinator.PublishAsync(b, new UsagePublish(la, null, session))).Should().Be(UsagePublishResult.Ignored, "這台的帳戶登出了");

        // 重連時新舊兩條連線同時送：節流在寫入者上，只有一個被採用
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => coordinator.PublishAsync(a, new UsagePublish(la, null, session))));
        results.Count(r => r == UsagePublishResult.Accepted).Should().Be(1);
        ServerPayloads<UsageSnapshotPayload>(host, SyncEventKinds.UsageSnapshot).Should().HaveCount(1);
    }

    [Fact]
    public async Task usagePollsPerHour_只算被採用的帶term上傳_一小時後過期_ops_status只帶邏輯帳戶id()
    {
        await using var host = await HubTestHost.StartAsync();
        var (coordinator, _) = Coordinator(host);
        var a = await AddDeviceAsync(host);
        var b = await AddDeviceAsync(host);
        await LinkAsync(host, a, 1);
        await LinkAsync(host, b, 1);
        var la = LatestAccount(host).LogicalAccountId;
        Online(host, a, "c1");
        var api = UsageRulesTests.Usage(UsageRules.SourceApi, 30);
        var session = UsageRulesTests.Usage(UsageRules.SourceSession, 55);
        var stats = host.Services.GetRequiredService<UsagePollStats>();
        using var local = host.CreateClient(ip: "127.0.0.1", cookies: false);

        // 撥快之後先讓 a 心跳、重算（背景的定期重算可能已經因為心跳逾時改派過）：以目前的 term 上傳
        async Task<long> TermAsync()
        {
            Connections(host).Heartbeat(a, host.Clock.NowMs);
            await coordinator.ReassessAsync();
            return coordinator.AssignmentsFor(a).Single().Term;
        }

        var term = await TermAsync();
        (await coordinator.PublishAsync(a, new UsagePublish(la, term, api))).Should().Be(UsagePublishResult.Accepted);
        host.Clock.Advance(TimeSpan.FromMinutes(20));
        term = await TermAsync();
        (await coordinator.PublishAsync(a, new UsagePublish(la, term, api))).Should().Be(UsagePublishResult.Accepted);
        (await coordinator.PublishAsync(a, new UsagePublish(la, term + 7, api))).Should().Be(UsagePublishResult.Ignored, "不是目前的 term：不算");
        (await coordinator.PublishAsync(b, new UsagePublish(la, term, api))).Should().Be(UsagePublishResult.Ignored, "不是負責的那台：不算");
        (await coordinator.PublishAsync(b, new UsagePublish(la, null, session))).Should().Be(UsagePublishResult.Accepted, "對話中的事件被採用，但不是查詢：不算");

        var summary = stats.Snapshot(host.Clock.NowMs);
        summary.Max.Should().Be(2);
        summary.Accounts.Should().Equal(new UsagePollCount(la, 2));

        // ops/status（本機的 /readyz 內容相同）：最大值與各帳戶的次數，只有邏輯帳戶 id
        var status = await local.GetJsonAsync("/readyz");
        var polls = status.GetProperty("usagePollsPerHour");
        polls.GetProperty("max").GetInt32().Should().Be(2);
        var account = polls.GetProperty("accounts").EnumerateArray().Should().ContainSingle().Subject;
        account.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("logicalAccountId", "count");
        account.GetProperty("logicalAccountId").GetString().Should().Be(la);
        account.GetProperty("count").GetInt32().Should().Be(2);
        polls.GetRawText().Should().NotContain("@", "不含 email");

        // 第一筆滿一小時就過期
        host.Clock.Advance(TimeSpan.FromMinutes(40));
        stats.Snapshot(host.Clock.NowMs).Accounts.Should().Equal(new UsagePollCount(la, 1));
        host.Clock.Advance(TimeSpan.FromMinutes(20));
        var empty = (await local.GetJsonAsync("/readyz")).GetProperty("usagePollsPerHour");
        empty.GetProperty("max").GetInt32().Should().Be(0);
        empty.GetProperty("accounts").GetArrayLength().Should().Be(0);
    }

    private static (UsageCoordinator Coordinator, List<(string, UsageAssign)> Sent) Coordinator(HubTestHost host)
    {
        var coordinator = host.Services.GetRequiredService<UsageCoordinator>();
        var sent = new List<(string, UsageAssign)>();
        coordinator.Assigned += (deviceId, message) =>
        {
            lock (sent)
                sent.Add((deviceId, message));
        };
        return (coordinator, sent);
    }

    private static ConnectionRegistry Connections(HubTestHost host) => host.Services.GetRequiredService<ConnectionRegistry>();

    private static void Online(HubTestHost host, string deviceId, string connectionId) =>
        Connections(host).Add(new DeviceConnection(connectionId, deviceId, () => { }, new UpstreamContext(deviceId, null, null)), host.Clock.NowMs);

    private static async Task LinkAsync(HubTestHost host, string deviceId, long deviceSeq, int running = 0, string status = "ready", string identity = "claude:org1:a@example.com", string? logical = null)
    {
        var link = Link("acct-" + deviceId, identity, logical) with { Status = status, Sessions = new LinkSessions(1, running, 0) };
        var outcome = await AppendAsync(host, deviceId, Batch(Ev(deviceSeq, SyncEventKinds.LinkUpsert, link)));
        outcome.Ack.Results.Single().Rejected.Should().BeNull();
    }
}
