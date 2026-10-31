using Myriagent.Sync;
using Myriagent.Usage;

namespace Myriagent.Tests.Sync.Rules;

public sealed class UsageSyncRulesTests
{
    private const string Local = "acc-claude";
    private const string Logical = "la_0123456789ABCDEFGHJKMNPQRS";
    private const string Other = "la_ZZZZZZZZZZZZZZZZZZZZZZZZZZ";
    private const string Self = "d_0123456789ABCDEFGHJKMNPQRS";

    private static readonly DateTimeOffset Now = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);

    private static UsagePollSnapshot Poll(SyncEngineState state, bool? on = true, bool ready = true, bool identityOk = true,
        DateTimeOffset? disconnectedSince = null, bool paired = true, bool linked = true)
    {
        var links = linked
            ? new Dictionary<string, UsageLinkState> { [Local] = new(Logical, ready, identityOk) }
            : new Dictionary<string, UsageLinkState>();
        var assignments = on is { } value
            ? new Dictionary<string, UsageAssignment> { [Logical] = new(3, value) }
            : new Dictionary<string, UsageAssignment>();
        return new UsagePollSnapshot(paired, state, disconnectedSince, links, assignments);
    }

    private static UsageSnapshot Snapshot(string source = "api", bool stale = false, DateTimeOffset? fetchedAt = null, IReadOnlyList<UsageWindow>? windows = null) =>
        new(fetchedAt ?? Now.AddMinutes(-1), source, windows ?? [new UsageWindow("session", "5 小時", 42, Now.AddHours(2))], null, null, null, stale);

    private static readonly UsageLinkState ReadyLink = new(Logical, true, true);

    [Fact]
    public void 未配對或暫停或停止或待確認_一律查()
    {
        UsageSyncRules.ShouldPoll(null, Local, Now).Should().BeTrue("沒有快照");
        UsageSyncRules.ShouldPoll(Poll(SyncEngineState.Live, on: false, paired: false), Local, Now).Should().BeTrue("未配對");
        foreach (var state in new[] { SyncEngineState.Paused, SyncEngineState.Stopped, SyncEngineState.NeedsConfirmation })
            UsageSyncRules.ShouldPoll(Poll(state, on: false), Local, Now).Should().BeTrue(state.ToString());
    }

    [Fact]
    public void Live且On_查()
    {
        UsageSyncRules.ShouldPoll(Poll(SyncEngineState.Live, on: true), Local, Now).Should().BeTrue();
        UsageSyncRules.ShouldPoll(Poll(SyncEngineState.Draining, on: true), Local, Now).Should().BeTrue();
        UsageSyncRules.ShouldPoll(Poll(SyncEngineState.Handshaking, on: true), Local, Now).Should().BeTrue();
    }

    [Fact]
    public void Live且Off_不查()
    {
        UsageSyncRules.ShouldPoll(Poll(SyncEngineState.Live, on: false), Local, Now).Should().BeFalse("別台負責查，這台看共享的");
        UsageSyncRules.ShouldPoll(Poll(SyncEngineState.Draining, on: false), Local, Now).Should().BeFalse();
    }

    [Fact]
    public void Live且Unknown_不查()
    {
        UsageSyncRules.ShouldPoll(Poll(SyncEngineState.Live, on: null), Local, Now).Should().BeFalse("還沒收到指派");
        UsageSyncRules.StateOf(Poll(SyncEngineState.Live, on: null), Logical).Should().Be(UsageAssignmentState.Unknown);
    }

    [Fact]
    public void 斷線4分59秒不查_5分鐘起查()
    {
        foreach (var state in new[] { SyncEngineState.Backoff, SyncEngineState.Connecting, SyncEngineState.Idle })
        {
            UsageSyncRules.ShouldPoll(Poll(state, on: false, disconnectedSince: Now.AddSeconds(-299)), Local, Now).Should().BeFalse(state.ToString());
            UsageSyncRules.ShouldPoll(Poll(state, on: false, disconnectedSince: Now.AddMinutes(-5)), Local, Now).Should().BeTrue(state.ToString());
            UsageSyncRules.ShouldPoll(Poll(state, on: true, disconnectedSince: Now.AddSeconds(-10)), Local, Now).Should().BeFalse("斷線時不看指派");
        }

        UsageSyncRules.ShouldPoll(Poll(SyncEngineState.Backoff, on: false, disconnectedSince: null), Local, Now).Should().BeTrue("不知道從何時斷線就當成已經很久");
    }

    [Fact]
    public void 連結不是ready或身分不符_查()
    {
        UsageSyncRules.ShouldPoll(Poll(SyncEngineState.Live, on: false, ready: false), Local, Now).Should().BeTrue();
        UsageSyncRules.ShouldPoll(Poll(SyncEngineState.Live, on: false, identityOk: false), Local, Now).Should().BeTrue();
        UsageSyncRules.ShouldPoll(Poll(SyncEngineState.Live, on: false, linked: false), Local, Now).Should().BeTrue("沒有連結");
        UsageSyncRules.ShouldPoll(Poll(SyncEngineState.Live, on: false), "acc-other", Now).Should().BeTrue("別的本機帳戶沒有連結");
    }

    [Fact]
    public void FromProve_清單沒列的On改成Off()
    {
        var current = new Dictionary<string, UsageAssignment> { [Logical] = new(3, true), [Other] = new(7, false) };

        var result = UsageSyncRules.FromProve(current, []);

        result[Logical].Should().Be(new UsageAssignment(3, false), "term 不變");
        result[Other].Should().Be(new UsageAssignment(7, false));
    }

    [Fact]
    public void FromProve_清單裡的照term合併()
    {
        var current = new Dictionary<string, UsageAssignment> { [Logical] = new(5, false), [Other] = new(2, true) };

        var result = UsageSyncRules.FromProve(current, [new UsageAssign(Logical, 6, true), new UsageAssign(Other, 1, true)]);

        result[Logical].Should().Be(new UsageAssignment(6, true));
        result[Other].Should().Be(new UsageAssignment(2, true), "清單裡 term 較小的丟掉、表裡照舊");
    }

    [Fact]
    public void Merge_term較大才覆蓋_較小的丟掉()
    {
        var current = new Dictionary<string, UsageAssignment> { [Logical] = new(5, true) };

        UsageSyncRules.Merge(current, new UsageAssign(Logical, 4, false))[Logical].Should().Be(new UsageAssignment(5, true), "比看過的小");
        UsageSyncRules.Merge(current, new UsageAssign(Logical, 5, false))[Logical].Should().Be(new UsageAssignment(5, false), "同 term 照收");
        UsageSyncRules.Merge(current, new UsageAssign(Logical, 6, false))[Logical].Should().Be(new UsageAssignment(6, false));
        UsageSyncRules.Merge(current, new UsageAssign(Other, 1, true))[Other].Should().Be(new UsageAssignment(1, true));
        UsageSyncRules.Merge(current, new UsageAssign("bad", 9, true)).Should().NotContainKey("bad");
    }

    [Fact]
    public void ToShared_第9個時間窗丟掉_非法key丟掉_percent夾住_resetsAt為0改null_label清理()
    {
        var windows = new List<UsageWindow>
        {
            new("session", "5 小時" + (char)0x202E, 150_000, Now.AddHours(1)),
            new("weekly", "", -5, DateTimeOffset.FromUnixTimeMilliseconds(0)),
            new("weekly:Opus", new string('週', 80), 12, null, Locked: true),
            new("中文鍵", "x", 1, null),
            new("session", "重複", 1, null),
            new("nan", "x", double.NaN, null),
        };
        for (var i = 0; i < 8; i++)
            windows.Add(new UsageWindow($"codex:{i}", $"窗 {i}", i, null));

        var shared = UsageSyncRules.ToShared(Snapshot(windows: windows))!;

        shared.Windows.Should().HaveCount(8);
        shared.Windows.Select(w => w.Key).Should().Equal("session", "weekly", "weekly_cOpus", "codex_c0", "codex_c1", "codex_c2", "codex_c3", "codex_c4");
        shared.Windows[0].Percent.Should().Be(1000);
        shared.Windows[0].Label.Should().Be("5 小時");
        shared.Windows[1].Percent.Should().Be(0);
        shared.Windows[1].ResetsAt.Should().BeNull();
        shared.Windows[1].Label.Should().Be("weekly", "名稱清理後是空的就用鍵");
        shared.Windows[2].Label.Should().Be(new string('週', 60));
        shared.Windows[2].Locked.Should().BeTrue();
        shared.Windows[0].ResetsAt.Should().Be(Now.AddHours(1).ToUnixTimeMilliseconds());
    }

    [Fact]
    public void ToShared_通過伺服器上限()
    {
        UsageSyncRules.MaxWindows.Should().Be(ServerRulesMirror.MaxWindows);
        UsageSyncRules.MaxWindowKeyLength.Should().Be(ServerRulesMirror.MaxWindowKeyLength);
        UsageSyncRules.MaxPercent.Should().Be(ServerRulesMirror.MaxPercent);
        UsageRules.MaxResetCredits.Should().Be(ServerRulesMirror.MaxResetCredits);

        var lone = ((char)0xD800).ToString();
        var windows = Enumerable.Range(0, 12)
            .Select(i => new UsageWindow(i % 2 == 0 ? $"weekly:model-{i}.5" : $"k_{i}", new string('名', 70) + lone, i * 200.5, Now.AddDays(i)))
            .Append(new UsageWindow(new string('k', 40), "太長的鍵", 1, null))
            .ToList();
        var snapshot = new UsageSnapshot(Now, "api", windows, new string('1', 300) + (char)0x01, new string('n', 700), "錯誤" + (char)0x07 + lone, false);

        var shared = UsageSyncRules.ToShared(snapshot)!;

        ServerRulesMirror.ValidateUsage(new UsagePublish(Logical, 3, shared)).Should().BeNull();
        ServerRulesMirror.ValidateUsage(new UsagePublish(Logical, null, UsageSyncRules.ToShared(snapshot with { Source = "session" })!)).Should().BeNull();
        shared.Windows.Should().NotContain(w => w.Key.Length > 32);
    }

    [Fact]
    public void 免費重置次數_上傳與拉回都帶著_超出範圍的夾住或丟掉_舊版的資料是null()
    {
        var expires = DateTimeOffset.FromUnixTimeSeconds(1794007837);
        var shared = UsageSyncRules.ToShared(Snapshot() with { ResetCredits = 2, ResetCreditExpiresAt = expires })!;
        shared.ResetCredits.Should().Be(2);
        shared.ResetCreditExpiresAt.Should().Be(expires.ToUnixTimeMilliseconds());
        ServerRulesMirror.ValidateUsage(new UsagePublish(Logical, 3, shared)).Should().BeNull();

        var back = UsageSyncRules.FromShared(new UsageSnapshotPayload(Logical, shared, "d_ZZZZZZZZZZZZZZZZZZZZZZZZZZ", 3), Self)!;
        back.ResetCredits.Should().Be(2);
        back.ResetCreditExpiresAt.Should().Be(expires);

        var clamped = UsageSyncRules.ToShared(Snapshot() with { ResetCredits = 5000, ResetCreditExpiresAt = DateTimeOffset.UnixEpoch })!;
        clamped.ResetCredits.Should().Be(UsageRules.MaxResetCredits);
        clamped.ResetCreditExpiresAt.Should().BeNull();
        ServerRulesMirror.ValidateUsage(new UsagePublish(Logical, 3, clamped)).Should().BeNull();
        var bad = UsageSyncRules.FromShared(new UsageSnapshotPayload(Logical, shared with { ResetCredits = -1, ResetCreditExpiresAt = UsageSyncRules.MaxUnixMs + 1 }, "d_ZZZZZZZZZZZZZZZZZZZZZZZZZZ", 3), Self)!;
        bad.ResetCredits.Should().Be(0);
        bad.ResetCreditExpiresAt.Should().BeNull();

        // 舊版的電腦與伺服器不送這兩個欄位
        var old = SyncJson.Deserialize<SharedUsage>("""{"fetchedAt":1,"source":"api","windows":[],"credits":null,"note":null,"error":null,"stale":false}""")!;
        old.ResetCredits.Should().BeNull();
        old.ResetCreditExpiresAt.Should().BeNull();
    }

    [Fact]
    public void ToShared_FetchedAt為MinValue回null()
    {
        UsageSyncRules.ToShared(UsageSnapshot.Empty()).Should().BeNull("Antigravity 沒有額度可查");
        UsageSyncRules.ToShared(Snapshot(fetchedAt: DateTimeOffset.FromUnixTimeMilliseconds(0))).Should().BeNull();
    }

    // 時間窗鍵的跳脫與還原搬進 Protocol（UsageWindowKeyRules，WP3.7 步驟 2），測試在 Protocol/Sync/UsageWindowKeyRulesTests

    [Fact]
    public void Decide_api且On且Live且FetchedAt變了_Termed()
    {
        var s = Snapshot("api");

        UsageSyncRules.Decide(s, UsageAssignmentState.On, live: true, ReadyLink, lastTermedFetchedAt: null, null, Now).Should().Be(UsageUpload.Termed);
        UsageSyncRules.Decide(s, UsageAssignmentState.On, live: true, ReadyLink, lastTermedFetchedAt: s.FetchedAt.AddSeconds(-30), null, Now).Should().Be(UsageUpload.Termed);
        UsageSyncRules.Decide(s, UsageAssignmentState.On, live: true, ReadyLink, lastTermedFetchedAt: s.FetchedAt, null, Now).Should().Be(UsageUpload.None, "同一份已經傳過");
        UsageSyncRules.Decide(s, UsageAssignmentState.On, live: false, ReadyLink, null, null, Now).Should().Be(UsageUpload.None, "不在 Live");
        UsageSyncRules.Decide(s with { Stale = true }, UsageAssignmentState.On, live: true, ReadyLink, null, null, Now).Should().Be(UsageUpload.None, "過時的不傳");
        UsageSyncRules.Decide(s, UsageAssignmentState.On, live: true, ReadyLink with { IdentityOk = false }, null, null, Now).Should().Be(UsageUpload.None);
        UsageSyncRules.Decide(s, UsageAssignmentState.On, live: true, null, null, null, Now).Should().Be(UsageUpload.None, "沒有連結");
    }

    [Fact]
    public void Decide_api但Off_None()
    {
        UsageSyncRules.Decide(Snapshot("api"), UsageAssignmentState.Off, live: true, ReadyLink, null, null, Now).Should().Be(UsageUpload.None);
        UsageSyncRules.Decide(Snapshot("api"), UsageAssignmentState.Unknown, live: true, ReadyLink, null, null, Now).Should().Be(UsageUpload.None);
    }

    [Fact]
    public void Decide_session_60秒內第二次None()
    {
        var s = Snapshot("session");

        UsageSyncRules.Decide(s, UsageAssignmentState.Off, live: true, ReadyLink, null, lastSessionUploadAt: null, Now).Should().Be(UsageUpload.Session, "不帶 term、不看指派");
        UsageSyncRules.Decide(s, UsageAssignmentState.Off, live: true, ReadyLink, null, lastSessionUploadAt: Now.AddSeconds(-59), Now).Should().Be(UsageUpload.None);
        UsageSyncRules.Decide(s, UsageAssignmentState.Off, live: true, ReadyLink, null, lastSessionUploadAt: Now.AddSeconds(-60), Now).Should().Be(UsageUpload.Session);
        UsageSyncRules.Decide(s, UsageAssignmentState.On, live: true, ReadyLink with { Ready = false }, null, null, Now).Should().Be(UsageUpload.None);
    }

    [Fact]
    public void Decide_shared與cache永遠None()
    {
        foreach (var source in new[] { "shared", "cache", "none" })
        {
            UsageSyncRules.Decide(Snapshot(source), UsageAssignmentState.On, live: true, ReadyLink, null, null, Now).Should().Be(UsageUpload.None, source);
        }
    }

    [Fact]
    public void FromShared_自己發的略過_來源為shared()
    {
        var usage = new SharedUsage(Now.ToUnixTimeMilliseconds(), "api",
            [new SharedUsageWindow("weekly_cOpus", "每週・Opus", 77, Now.AddDays(1).ToUnixTimeMilliseconds(), true), new SharedUsageWindow("session", "5 小時", 12, null, false)],
            "10.5", "備註", null, false);

        UsageSyncRules.FromShared(new UsageSnapshotPayload(Logical, usage, Self, 3), Self).Should().BeNull("自己發的");
        var snapshot = UsageSyncRules.FromShared(new UsageSnapshotPayload(Logical, usage, "d_ZZZZZZZZZZZZZZZZZZZZZZZZZZ", 3), Self)!;

        snapshot.Source.Should().Be("shared");
        snapshot.FetchedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds()));
        snapshot.Windows.Select(w => w.Key).Should().Equal("weekly:Opus", "session");
        snapshot.Windows[0].Locked.Should().BeTrue();
        snapshot.Windows[0].ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(Now.AddDays(1).ToUnixTimeMilliseconds()));
        snapshot.Windows[1].ResetsAt.Should().BeNull();
        snapshot.Credits.Should().Be("10.5");
        UsageSyncRules.Decide(snapshot, UsageAssignmentState.On, live: true, ReadyLink, null, null, Now).Should().Be(UsageUpload.None, "共享來的不回傳（避免回音）");
    }

    [Fact]
    public void ForRemoteOnly_驗證後清理成線上格式_自己發的與時間不合格的是null()
    {
        var other = "d_ZZZZZZZZZZZZZZZZZZZZZZZZZZ";
        var fetchedAt = Now.ToUnixTimeMilliseconds();
        var resets = Now.AddDays(1).ToUnixTimeMilliseconds();
        var usage = new SharedUsage(fetchedAt, "api",
            [new SharedUsageWindow("weekly_cOpus", "每週・Opus", 77, resets, true), new SharedUsageWindow("session", "5 小時", 5000, null, false),
             new SharedUsageWindow("nan", "壞的", double.NaN, null, false), new SharedUsageWindow("bad/key", "鍵不合格", 10, null, false)],
            "10.5", "備註", null, false);

        var shared = UsageSyncRules.ForRemoteOnly(new UsageSnapshotPayload(Logical, usage, other, 3), Self)!;
        shared.Source.Should().Be(UsageSyncRules.SourceShared);
        shared.FetchedAt.Should().Be(fetchedAt);
        shared.Windows.Select(w => w.Key).Should().Equal(["weekly_cOpus", "session"], "非有限值與轉不了的鍵丟掉，鍵照線上格式");
        shared.Windows[0].Should().Be(new SharedUsageWindow("weekly_cOpus", "每週・Opus", 77, resets, true));
        shared.Windows[1].Percent.Should().Be(UsageSyncRules.MaxPercent, "百分比夾在上限");
        shared.Credits.Should().Be("10.5");
        shared.Note.Should().Be("備註");
        SyncViewRules.UsageView(shared)["windows"]![0]!.Value<string>("key").Should().Be("weekly:Opus", "卡片上照本機的鍵顯示");

        UsageSyncRules.ForRemoteOnly(new UsageSnapshotPayload(Logical, usage, Self, 3), Self).Should().BeNull("自己發的");
        UsageSyncRules.ForRemoteOnly(new UsageSnapshotPayload(Logical, usage with { FetchedAt = UsageSyncRules.MaxUnixMs + 1 }, other, 3), Self).Should().BeNull("時間超出範圍");
        UsageSyncRules.ForRemoteOnly(new UsageSnapshotPayload(Logical, usage with { FetchedAt = 0 }, other, 3), Self).Should().BeNull("沒有查詢時間");
        var many = usage with { Windows = Enumerable.Range(0, 12).Select(i => new SharedUsageWindow("w" + i, "窗 " + i, i, null, false)).ToList() };
        UsageSyncRules.ForRemoteOnly(new UsageSnapshotPayload(Logical, many, other, 3), Self)!.Windows.Should().HaveCount(8, "時間窗最多 8 個");
    }

    [Fact]
    public void UsageView_還原後撞名時送原本的鍵_與瀏覽器閘道相同()
    {
        // 合併前審查 WK-1：逐一 Decode 時 a__x 與 a_x 都得到 a_x，網頁拿 key 當圓環欄的識別、前一個窗從畫面上消失
        var usage = new SharedUsage(1, "api",
            [new SharedUsageWindow("a__x", "甲", 1, null, false), new SharedUsageWindow("a_x", "乙", 2, null, false),
             new SharedUsageWindow("weekly_cOpus", "每週・Opus", 3, null, false)],
            null, null, null, false);
        var windows = SyncViewRules.UsageView(usage)["windows"]!;
        windows.Select(w => w.Value<string>("key")).Should().Equal(["a__x", "a_x", "weekly:Opus"]);
        windows.Select(w => w.Value<string>("label")).Should().Equal(["甲", "乙", "每週・Opus"]);
    }

    [Fact]
    public void FromShared_時間超出DateTimeOffset範圍_不丟例外()
    {
        var other = "d_ZZZZZZZZZZZZZZZZZZZZZZZZZZ";
        var window = new SharedUsageWindow("session", "5 小時", 12, Now.AddHours(1).ToUnixTimeMilliseconds(), false);

        // 伺服器只檢查 >0：有問題的裝置送來的 9e15、long.MaxValue 照樣進得了事件紀錄
        foreach (var fetchedAt in new[] { long.MaxValue, 9_000_000_000_000_000, UsageSyncRules.MaxUnixMs + 1 })
        {
            var tooLate = new SharedUsage(fetchedAt, "api", [window], null, null, null, false);
            UsageSyncRules.FromShared(new UsageSnapshotPayload(Logical, tooLate, other, 3), Self).Should().BeNull($"fetchedAt={fetchedAt} 轉不成時間：整筆略過");
        }

        var edge = new SharedUsage(UsageSyncRules.MaxUnixMs, "api", [window], null, null, null, false);
        UsageSyncRules.FromShared(new UsageSnapshotPayload(Logical, edge, other, 3), Self)!.FetchedAt.Should().Be(new DateTimeOffset(9999, 12, 31, 23, 59, 59, 999, TimeSpan.Zero), "上限本身收得下");

        var badReset = new SharedUsage(Now.ToUnixTimeMilliseconds(), "api",
            [new SharedUsageWindow("session", "5 小時", 12, long.MaxValue, false), new SharedUsageWindow("weekly", "每週", 30, UsageSyncRules.MaxUnixMs + 1, false), window with { Key = "x" }],
            null, null, null, false);
        var snapshot = UsageSyncRules.FromShared(new UsageSnapshotPayload(Logical, badReset, other, 3), Self)!;
        snapshot.Windows.Select(w => w.Key).Should().Equal("session", "weekly", "x");
        snapshot.Windows[0].ResetsAt.Should().BeNull("resetsAt 轉不成時間：當成沒有，其他欄位照收");
        snapshot.Windows[1].ResetsAt.Should().BeNull();
        snapshot.Windows[2].ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(Now.AddHours(1).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public void ReplaceCurrent_追趕期間本機是api_false_Live時true()
    {
        UsageSyncRules.ReplaceCurrent(catchingUp: true, "api").Should().BeFalse();
        UsageSyncRules.ReplaceCurrent(catchingUp: true, "session").Should().BeFalse();
        UsageSyncRules.ReplaceCurrent(catchingUp: true, "shared").Should().BeTrue();
        UsageSyncRules.ReplaceCurrent(catchingUp: true, "cache").Should().BeTrue();
        UsageSyncRules.ReplaceCurrent(catchingUp: true, null).Should().BeTrue();
        UsageSyncRules.ReplaceCurrent(catchingUp: false, "api").Should().BeTrue();
    }
}
