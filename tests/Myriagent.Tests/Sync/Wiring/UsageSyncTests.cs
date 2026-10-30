using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Engine;
using Myriagent.Usage;

namespace Myriagent.Tests.Sync.Wiring;

/// <summary>用量同步（引擎＋假伺服器）：指派、上傳、拉到的共享用量、查詢閘門的快照（D14：不比較 FetchedAt，只看 seq）</summary>
public sealed class UsageSyncTests
{
    private const long At = 1791600000000;

    private static UsageSnapshot Snapshot(DateTimeOffset at, string source, double percent = 42) =>
        new(at, source, [new UsageWindow("session", "5 小時", percent, at.AddHours(2))], null, null, null, false);

    private static SharedUsage Shared(double percent, long fetchedAt = At) =>
        new(fetchedAt, UsageSyncRules.SourceApi, [new SharedUsageWindow("session", "5 小時", percent, null, false)], null, null, null, false);

    private static SyncSignal.UsageObserved Observed(EngineHarness h, string source, double percent = 42, TimeSpan? offset = null) =>
        new(EngineHarness.LocalAccount, Snapshot(h.Time.GetUtcNow() + (offset ?? TimeSpan.Zero), source, percent));

    private static List<SharedUsageApply> Applied(EngineHarness h) => h.Applier.SharedUsage.SelectMany(x => x).ToList();

    /// <summary>目錄裡有、這台沒有連結的邏輯帳戶（remoteOnly 卡片）</summary>
    private static readonly string Unlinked = SyncIds.NewLogicalAccountId();

    private static AccountUpsertPayload UnlinkedAccount() =>
        new(Unlinked, "Codex", "codex:other@example.com", "其他電腦的 Codex", "#19C3A6", 1, "other@example.com", null, "Pro", 1, []);

    private static List<RemoteUsageApply> Remote(EngineHarness h) => h.Applier.RemoteUsage.SelectMany(x => x.Items).ToList();

    [Fact]
    public async Task 沒連結的邏輯帳戶_usage_snapshot交給ApplyRemoteUsage_依seq取最後一筆()
    {
        await using var h = new EngineHarness();
        var other = SyncIds.NewDeviceId();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        h.Server.Publish(SyncEventKinds.AccountUpsert, UnlinkedAccount());
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(Unlinked, Shared(10, At + 600_000), other, 1));
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(Unlinked, Shared(20, At + 1_000), SyncIds.NewDeviceId(), 2));
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(SyncIds.NewLogicalAccountId(), Shared(77), other, 2));

        await h.StartAsync();

        var applied = Remote(h).Should().ContainSingle("同一個邏輯帳戶只留 seq 最大的一筆；目錄裡沒有的邏輯帳戶不收").Subject;
        applied.LogicalAccountId.Should().Be(Unlinked);
        applied.Usage.Windows.Single().Percent.Should().Be(20, "依 seq，不看來源電腦的 fetchedAt");
        applied.Usage.Source.Should().Be(UsageSyncRules.SourceShared);
        Applied(h).Should().BeEmpty("沒連結的不走 ApplySharedUsage");
        h.Applier.OffUiCalls.Should().Be(0);

        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(Unlinked, Shared(30), other, 3));
        h.Server.Current!.Notify(SyncClasses.Usage);
        await h.IdleAsync();
        Remote(h).Last().Usage.Windows.Single().Percent.Should().Be(30, "Live 之後拉到的也交給 ApplyRemoteUsage");
        h.Applier.RemoteUsage.Last().Reset.Should().BeFalse();
    }

    [Fact]
    public async Task 沒連結的_自己發的與格式不對的略過()
    {
        await using var h = new EngineHarness();
        var other = SyncIds.NewDeviceId();
        h.Server.Publish(SyncEventKinds.AccountUpsert, UnlinkedAccount());
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(Unlinked, Shared(10), other, 1));
        // 之後的三筆都不合格：留下的仍是第一筆
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(Unlinked, Shared(99), h.DeviceId, 2));
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(Unlinked, Shared(98, 9_000_000_000_000_000), other, 3));
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(Unlinked, Shared(97, 0), other, 4));

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Live, "壞的單筆略過、拉取照常");
        Remote(h).Should().ContainSingle().Which.Usage.Windows.Single().Percent.Should().Be(10);
    }

    [Fact]
    public async Task 有連結的照舊走ApplySharedUsage_不進remoteOnly()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        h.Server.Publish(SyncEventKinds.AccountUpsert, UnlinkedAccount());
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(EngineHarness.Logical, Shared(30), SyncIds.NewDeviceId(), 2));

        await h.StartAsync();

        Applied(h).Should().ContainSingle().Which.LocalAccountId.Should().Be(EngineHarness.LocalAccount);
        Remote(h).Should().BeEmpty();
    }

    [Fact]
    public async Task 換實例後第一次套用帶reset_沒有新的共享用量也送()
    {
        await using var h = new EngineHarness();
        var other = SyncIds.NewDeviceId();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        h.Server.Publish(SyncEventKinds.AccountUpsert, UnlinkedAccount());
        var highBeforeUsage = h.Server.High;
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(Unlinked, Shared(10), other, 1));
        await h.StartAsync();
        Remote(h).Should().ContainSingle();
        var before = h.Applier.RemoteUsage.Count;

        // 伺服器還原到共享用量之前的備份（換了實例）：游標歸 0 重拉，拉不到那筆用量
        h.Server.Restore(nextDeviceSeqAtBackup: h.Server.NextDeviceSeq, highAtBackup: highBeforeUsage);
        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(1);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        var after = h.Applier.RemoteUsage.Skip(before).ToList();
        after.Should().NotBeEmpty("換實例後的第一次套用一定送 remoteOnly 的用量");
        after[0].Reset.Should().BeTrue();
        after[0].Items.Should().BeEmpty("還原後沒有那筆用量：只清掉舊的");

        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(Unlinked, Shared(40), other, 2));
        h.Server.Current!.Notify(SyncClasses.Usage);
        await h.IdleAsync();
        h.Applier.RemoteUsage.Last().Reset.Should().BeFalse("只有換實例後的第一次帶 reset");
        h.Applier.RemoteUsage.Last().Items.Single().Usage.Windows.Single().Percent.Should().Be(40);
    }

    [Fact]
    public async Task On_新api快照_上傳帶term()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        h.Server.ProveAssignments.Add(new UsageAssign(EngineHarness.Logical, 3, true));
        await h.StartAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);

        h.Engine.Post(Observed(h, UsageSyncRules.SourceApi));
        await h.IdleAsync();
        h.Engine.Post(Observed(h, UsageSyncRules.SourceApi, 50));
        await h.IdleAsync();

        var published = h.Server.UsagePublishes.Should().ContainSingle("同一個 FetchedAt 只上傳一次").Subject;
        published.LogicalAccountId.Should().Be(EngineHarness.Logical);
        published.Term.Should().Be(3);
        published.Usage.Source.Should().Be(UsageSyncRules.SourceApi);
        published.Usage.Windows.Should().ContainSingle().Which.Percent.Should().Be(42);

        h.Engine.Post(Observed(h, UsageSyncRules.SourceApi, 60, TimeSpan.FromMinutes(5)));
        await h.IdleAsync();
        h.Server.UsagePublishes.Should().HaveCount(2, "新的 FetchedAt 再上傳");
    }

    [Fact]
    public async Task Off_不上傳帶term()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        await h.StartAsync();

        h.Engine.Post(Observed(h, UsageSyncRules.SourceApi));
        await h.IdleAsync();
        h.Server.UsagePublishes.Should().BeEmpty("沒有指派（Unknown）");

        h.Server.Current!.Assign(EngineHarness.Logical, 4, false);
        await h.IdleAsync();
        h.Engine.Post(Observed(h, UsageSyncRules.SourceApi, 50, TimeSpan.FromMinutes(1)));
        await h.IdleAsync();

        h.Server.UsagePublishes.Should().BeEmpty("指派 Off：不是負責查詢的那台");
        (await h.Inspect(v => v.Assignments[EngineHarness.Logical])).Should().Be(new UsageAssignment(4, false));
    }

    [Fact]
    public async Task session來源_60秒最多一次()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        await h.StartAsync();

        h.Engine.Post(Observed(h, UsageSyncRules.SourceSession));
        await h.IdleAsync();
        await h.AdvanceAsync(30);
        h.Engine.Post(Observed(h, UsageSyncRules.SourceSession, 50));
        await h.IdleAsync();
        h.Server.UsagePublishes.Should().ContainSingle("60 秒內第二次不送");
        h.Server.UsagePublishes.Single().Term.Should().BeNull("對話中的 rate limit 不帶 term");

        await h.AdvanceAsync(31);
        h.Engine.Post(Observed(h, UsageSyncRules.SourceSession, 60));
        await h.IdleAsync();

        h.Server.UsagePublishes.Should().HaveCount(2);
        h.Server.UsagePublishes.Last().Usage.Windows.Single().Percent.Should().Be(60);
    }

    [Fact]
    public async Task 套用shared後_不觸發任何上傳()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        h.Server.ProveAssignments.Add(new UsageAssign(EngineHarness.Logical, 3, true));
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(EngineHarness.Logical, Shared(30), SyncIds.NewDeviceId(), 2));
        await h.StartAsync();
        var applied = Applied(h).Should().ContainSingle().Subject;

        // 協調者把共享用量套進 AccountManager 之後，鏡像看到 fetchedAt 改變會交回同一份（Source=shared）
        h.Engine.Post(new SyncSignal.UsageObserved(applied.LocalAccountId, applied.Snapshot));
        await h.IdleAsync();
        await h.AdvanceAsync(61);
        h.Engine.Post(new SyncSignal.UsageObserved(applied.LocalAccountId, applied.Snapshot with { FetchedAt = applied.Snapshot.FetchedAt.AddMinutes(1) }));
        await h.IdleAsync();

        h.Server.UsagePublishes.Should().BeEmpty("shared 來源的不再上傳（不會回音）");
    }

    [Fact]
    public async Task 收到usage_snapshot_自己的略過_其餘ApplySharedUsage來源shared_歷史點用createdAt()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        var createdAt = At + 123_456;
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(EngineHarness.Logical, Shared(30), SyncIds.NewDeviceId(), 2), createdAt);
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(EngineHarness.Logical, Shared(99), h.DeviceId, 2), createdAt + 1);
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(SyncIds.NewLogicalAccountId(), Shared(77), SyncIds.NewDeviceId(), 2), createdAt + 2);

        await h.StartAsync();

        var applied = Applied(h).Should().ContainSingle("自己發的、這台沒連結的邏輯帳戶都略過").Subject;
        applied.LocalAccountId.Should().Be(EngineHarness.LocalAccount);
        applied.Snapshot.Source.Should().Be(UsageSyncRules.SourceShared);
        applied.Snapshot.Windows.Single().Percent.Should().Be(30);
        applied.PointTime.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(createdAt), "歷史點用伺服器收到的時間，不用來源電腦的 fetchedAt");
        applied.Snapshot.FetchedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(At), "FetchedAt 只供顯示");
    }

    [Fact]
    public async Task 來源電腦時鐘快10分鐘_仍依seq套用()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        var fast = SyncIds.NewDeviceId();
        var normal = SyncIds.NewDeviceId();
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(EngineHarness.Logical, Shared(10, At + 600_000), fast, 1), At);
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(EngineHarness.Logical, Shared(20, At + 1_000), normal, 2), At + 2_000);
        await h.StartAsync();

        Applied(h).Select(a => a.Snapshot.Windows.Single().Percent).Should().Equal(10, 20);
        Applied(h).Should().OnlyContain(a => a.ReplaceCurrent, "本機沒有自己查的資料");

        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(EngineHarness.Logical, Shared(30, At + 900_000), fast, 3), At + 3_000);
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(EngineHarness.Logical, Shared(40, At + 5_000), normal, 4), At + 5_000);
        h.Server.Current!.Notify(SyncClasses.Usage);
        await h.IdleAsync();

        Applied(h).Select(a => a.Snapshot.Windows.Single().Percent).Should().Equal([10, 20, 30, 40], "照 seq；最後套用的是 seq 最大的，即使它的 fetchedAt 比較舊");
        Applied(h).Last().ReplaceCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task 共享用量的時間超出範圍_略過該筆_游標越過_目錄照常套用並Live()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        // 有問題的已配對裝置送的（伺服器只檢查 >0）：fetchedAt 9e15、resetsAt long.MaxValue 都轉不成 DateTimeOffset
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(EngineHarness.Logical, Shared(30, 9_000_000_000_000_000), SyncIds.NewDeviceId(), null));
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(EngineHarness.Logical,
            Shared(40) with { Windows = [new SharedUsageWindow("session", "5 小時", 40, long.MaxValue, false)] }, SyncIds.NewDeviceId(), null));
        var last = h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(EngineHarness.Logical, Shared(50), SyncIds.NewDeviceId(), 2));

        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Live, "壞掉的那筆不讓拉取丟例外、斷線重連");
        h.Server.Sessions.Should().ContainSingle();
        h.Engine.BackoffHistory.Should().BeEmpty();
        (await h.Inspect(v => v.Cursors[SyncClasses.Usage])).Should().Be(last, "游標越過壞掉的那筆");
        h.RawCursors()[SyncClasses.Usage].Should().Be(last);
        (await h.Inspect(v => v.Catalog!.Accounts.Count)).Should().Be(1, "同一頁的目錄照常提交");
        h.Applier.Catalogs.Should().NotBeEmpty();
        var applied = Applied(h);
        applied.Select(a => a.Snapshot.Windows.Single().Percent).Should().Equal(40, 50);
        applied[0].Snapshot.Windows.Single().ResetsAt.Should().BeNull();
    }

    [Fact]
    public async Task 追趕期間_本機有api資料_shared只進歷史()
    {
        await using var h = new EngineHarness();
        h.Server.ConnectFailure = _ => EngineHarness.Transient();
        await h.StartAsync();
        h.Engine.Post(Observed(h, UsageSyncRules.SourceApi));
        await h.IdleAsync();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(EngineHarness.Logical, Shared(30), SyncIds.NewDeviceId(), 2));

        h.Server.ConnectFailure = null;
        await h.AdvanceAsync(20);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        Applied(h).Should().ContainSingle().Which.ReplaceCurrent.Should().BeFalse("追趕積壓時本機自己查的值不被舊的共享值蓋掉");

        h.Server.Publish(SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(EngineHarness.Logical, Shared(40), SyncIds.NewDeviceId(), 3));
        h.Server.Current!.Notify(SyncClasses.Usage);
        await h.IdleAsync();
        Applied(h).Last().ReplaceCurrent.Should().BeTrue("Live 之後一律取代");
    }

    [Fact]
    public async Task Prove清單沒列_原本On改成Off_Live時不查()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        h.Server.ProveAssignments.Add(new UsageAssign(EngineHarness.Logical, 3, true));
        await h.StartAsync();
        var account = new AgentAccount { Id = EngineHarness.LocalAccount, Provider = AgentProvider.Claude };
        UsageSyncRules.ShouldPoll(h.Host.LastPoll, account.Id, h.Time.GetUtcNow()).Should().BeTrue("On：這台負責查");

        h.Server.ProveAssignments.Clear();
        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(2);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.Host.LastPoll!.Assignments[EngineHarness.Logical].Should().Be(new UsageAssignment(3, false), "term 不變、改成 Off");
        UsageSyncRules.ShouldPoll(h.Host.LastPoll, account.Id, h.Time.GetUtcNow()).Should().BeFalse("Live 而且不是負責查詢的那台");
    }

    [Fact]
    public async Task Prove前到的UsageAssign_照term合併()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        h.Server.ProveAssignments.Add(new UsageAssign(EngineHarness.Logical, 5, false));
        h.Server.BeforeProveReturns = s => s.Assign(EngineHarness.Logical, 7, true);

        await h.StartAsync();

        (await h.Inspect(v => v.Assignments[EngineHarness.Logical])).Should().Be(new UsageAssignment(7, true), "term 較大的勝");
        h.Engine.Post(Observed(h, UsageSyncRules.SourceApi));
        await h.IdleAsync();
        h.Server.UsagePublishes.Should().ContainSingle().Which.Term.Should().Be(7);
    }

    [Fact]
    public async Task 伺服器還原後term倒退_指派表清空_以這次Prove的清單重建()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        h.Server.ProveAssignments.Add(new UsageAssign(EngineHarness.Logical, 5, true));
        await h.StartAsync();
        (await h.Inspect(v => v.Assignments[EngineHarness.Logical])).Should().Be(new UsageAssignment(5, true));

        // 伺服器還原到 term=1 的備份（usage_pollers 跟著倒退，term 只在同一個實例／還原世代內可比較）
        h.Server.Restore(nextDeviceSeqAtBackup: h.Server.NextDeviceSeq, highAtBackup: h.Server.High);
        h.Server.ProveAssignments.Clear();
        h.Server.ProveAssignments.Add(new UsageAssign(EngineHarness.Logical, 1, true));
        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(1);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        (await h.Inspect(v => v.Assignments[EngineHarness.Logical])).Should().Be(new UsageAssignment(1, true), "舊的 term 5 不能把還原後的 term 1 丟掉");
        h.Host.LastPoll!.Assignments[EngineHarness.Logical].Should().Be(new UsageAssignment(1, true));
        h.Engine.Post(Observed(h, UsageSyncRules.SourceApi));
        await h.IdleAsync();
        h.Server.UsagePublishes.Should().ContainSingle().Which.Term.Should().Be(1, "伺服器只認目前的 term");
    }

    [Fact]
    public async Task 伺服器還原後Prove沒列_之後的小term指派照收()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        h.Server.ProveAssignments.Add(new UsageAssign(EngineHarness.Logical, 5, true));
        await h.StartAsync();

        h.Server.Restore(nextDeviceSeqAtBackup: h.Server.NextDeviceSeq, highAtBackup: h.Server.High);
        h.Server.ProveAssignments.Clear();
        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(1);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        (await h.Inspect(v => v.Assignments.ContainsKey(EngineHarness.Logical))).Should().BeFalse("換實例後清空，Prove 沒列＝還沒有指派");

        h.Server.Current!.Assign(EngineHarness.Logical, 2, true);
        await h.IdleAsync();

        (await h.Inspect(v => v.Assignments[EngineHarness.Logical])).Should().Be(new UsageAssignment(2, true));
    }

    [Fact]
    public async Task PollSnapshot_以Volatile發布_UI執行緒讀得到()
    {
        await using var h = new EngineHarness();
        var gate = new UsagePollGate(h.Time);
        h.Host.Gate = gate;
        var account = new AgentAccount { Id = EngineHarness.LocalAccount, Provider = AgentProvider.Claude };
        h.Ui.Invoke(() => gate.ShouldPoll(account)).Should().BeTrue("還沒有快照：照本機原本的頻率查");
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        h.Server.ProveAssignments.Add(new UsageAssign(EngineHarness.Logical, 3, true));

        await h.StartAsync();
        h.Ui.Invoke(() => gate.ShouldPoll(account)).Should().BeTrue("Live、指派 On");

        h.Server.Current!.Assign(EngineHarness.Logical, 4, false);
        await h.IdleAsync();
        h.Ui.Invoke(() => gate.ShouldPoll(account)).Should().BeFalse("Live、指派 Off");
        h.Ui.Invoke(() => gate.Snapshot).Should().BeSameAs(h.Host.LastPoll);

        h.Server.ConnectFailure = _ => EngineHarness.Transient();
        h.Server.Current!.Drop();
        await h.IdleAsync();
        h.Ui.Invoke(() => gate.ShouldPoll(account)).Should().BeFalse("斷線未滿 5 分鐘：等負責的那台");
        await h.AdvanceAsync(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        h.Ui.Invoke(() => gate.ShouldPoll(account)).Should().BeTrue("斷線滿 5 分鐘：自己查（離線優先）");

        gate.Publish(null);
        h.Ui.Invoke(() => gate.ShouldPoll(account)).Should().BeTrue();
    }
}
