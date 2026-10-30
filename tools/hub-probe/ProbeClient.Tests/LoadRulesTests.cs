namespace MyriagentHub.ProbeClient.Tests;

/// <summary>
/// S9 的摘要與判定（WP4.9 計畫步 9、D16）：門檻的邊界、CPU 依時間窗加權（直接平均的突變會紅）、重複窗只算一次、核心數換算、
/// 涵蓋率不足判 insufficient、閒置記憶體取後半的中位數；另外是延遲摘要、記憶體中止門檻與伺服器記錄的中斷原因。
/// 轉送階段（WP4.9 步 12）：每台恰好第 21 個被拒、其餘 20 個完整完成、名額收回；任一台多收或少拒會紅；期間 S7 p95 與私有記憶體的邊界；慢速讀取的節奏
/// </summary>
public sealed class LoadRulesTests
{
    private static LoadSummary Summary(double maxMb = 100, double? cpu = 1, double? idleMb = 80, double coverage = 1, int samples = 10, int processors = 24) =>
        new(samples, new CpuSummary(cpu, cpu is { } c ? c / 100 * processors : null, coverage * 600, 600, coverage, samples, 0), maxMb, maxMb, idleMb is null ? 0 : 5, idleMb, processors);

    [Fact]
    public void 判定_私有記憶體300點0過_300點1不過()
    {
        LoadRules.Verdict(Summary(maxMb: 300.0), idleRequired: true).Result.Should().Be(LoadRules.Pass);
        var fail = LoadRules.Verdict(Summary(maxMb: 300.1), idleRequired: true);
        fail.Result.Should().Be(LoadRules.Fail);
        fail.Reasons.Should().ContainSingle().Which.Should().Contain("300.1");
    }

    [Fact]
    public void 判定_CPU5點0過_5點01不過()
    {
        LoadRules.Verdict(Summary(cpu: 5.0), idleRequired: true).Result.Should().Be(LoadRules.Pass);
        LoadRules.Verdict(Summary(cpu: 5.01), idleRequired: true).Result.Should().Be(LoadRules.Fail);
    }

    [Fact]
    public void 判定_閒置149點9過_150點0不過()
    {
        LoadRules.Verdict(Summary(idleMb: 149.9), idleRequired: true).Result.Should().Be(LoadRules.Pass);
        LoadRules.Verdict(Summary(idleMb: 150.0), idleRequired: true).Result.Should().Be(LoadRules.Fail);
    }

    [Fact]
    public void 判定_涵蓋率89趴是insufficient_90趴才算()
    {
        LoadRules.Verdict(Summary(coverage: 0.89), idleRequired: true).Result.Should().Be(LoadRules.Insufficient);
        LoadRules.Verdict(Summary(coverage: 0.90), idleRequired: true).Result.Should().Be(LoadRules.Pass);
        // 不足的優先：記憶體也超過時照樣是 insufficient（數字不可信，不下 fail）
        LoadRules.Verdict(Summary(maxMb: 999, coverage: 0.5), idleRequired: true).Result.Should().Be(LoadRules.Insufficient);
    }

    [Fact]
    public void 判定_樣本不足_沒有CPU_要閒置卻沒有閒置的樣本都是insufficient()
    {
        LoadRules.Verdict(Summary(samples: 1), idleRequired: false).Result.Should().Be(LoadRules.Insufficient);
        LoadRules.Verdict(Summary(cpu: null), idleRequired: false).Result.Should().Be(LoadRules.Insufficient);
        LoadRules.Verdict(Summary(idleMb: null), idleRequired: true).Result.Should().Be(LoadRules.Insufficient);
        LoadRules.Verdict(Summary(idleMb: null), idleRequired: false).Result.Should().Be(LoadRules.Pass, "沒有要閒置（--idle-minutes 0）就不看");
    }

    [Fact]
    public void CPU_兩個窗2秒10趴與8秒0趴_加權是2趴不是直接平均的5趴()
    {
        LoadSample[] samples = [new(2_000, 50, 10, 2), new(10_000, 60, 0, 8)];
        var cpu = LoadRules.Cpu(samples, 0, 10_000, 32);
        cpu.Percent.Should().Be(2.0);
        cpu.Coverage.Should().Be(1.0);
        cpu.Windows.Should().Be(2);
        cpu.Duplicates.Should().Be(0);
    }

    [Fact]
    public void CPU_連續兩個相同的窗只算一次()
    {
        // 第二個是伺服器沿用上一次的值（查得太密）：算兩次的話是 (20+20+0)/12 = 3.33%
        LoadSample[] samples = [new(2_000, 50, 10, 2), new(2_100, 50, 10, 2), new(10_000, 60, 0, 8)];
        var cpu = LoadRules.Cpu(samples, 0, 10_000, 32);
        cpu.Percent.Should().Be(2.0);
        cpu.Duplicates.Should().Be(1);
        cpu.Windows.Should().Be(2);
        // 不相鄰的相同值是兩個不同的窗
        LoadSample[] apart = [new(2_000, 50, 10, 2), new(4_000, 50, 0, 2), new(6_000, 50, 10, 2)];
        LoadRules.Cpu(apart, 0, 6_000, 32).Duplicates.Should().Be(0);
    }

    [Fact]
    public void CPU_核心數是百分比除以100乘處理器數()
    {
        LoadSample[] samples = [new(2_000, 50, 10, 2), new(10_000, 60, 0, 8)];
        LoadRules.Cpu(samples, 0, 10_000, 32).Cores.Should().Be(0.64);
        LoadRules.Cpu(samples, 0, 10_000, 24).Cores.Should().Be(0.48);
        LoadRules.Summarize(samples, 0, 10_000, null, null, 24).Cpu.Cores.Should().Be(0.48);
    }

    [Fact]
    public void CPU_窗只算落在量測期間裡的那一段_涵蓋率89趴判insufficient()
    {
        // [0, 2000] 的窗只有 [1000, 2000] 在期間裡：(10×1 + 0×8) / 9
        LoadSample[] samples = [new(2_000, 50, 10, 2), new(10_000, 60, 0, 8)];
        var clipped = LoadRules.Cpu(samples, 1_000, 10_000, 32);
        clipped.Percent.Should().Be(1.11);
        clipped.Coverage.Should().Be(1.0);
        clipped.CoveredSeconds.Should().Be(9);

        // [0, 1000] 與 [2100, 10000]：中間 1.1 秒沒有窗
        var summary = LoadRules.Summarize([new(1_000, 50, 1, 1), new(10_000, 60, 2, 7.9)], 0, 10_000, null, null, 32);
        summary.Cpu.Coverage.Should().Be(0.89);
        LoadRules.Verdict(summary, idleRequired: false).Result.Should().Be(LoadRules.Insufficient);
        // 窗長 0、沒有 CPU 的樣本不算
        LoadRules.Cpu([new(1_000, 50, null, null), new(2_000, 50, 5, 0)], 0, 2_000, 32).Percent.Should().BeNull();
    }

    [Fact]
    public void 記憶體_只看負載期間_最大與p95()
    {
        var samples = Enumerable.Range(1, 20).Select(i => new LoadSample(i * 1_000, i * 10, 1, 1)).Append(new LoadSample(99_000, 999, 1, 1)).ToList();
        var summary = LoadRules.Summarize(samples, 1_000, 20_000, null, null, 32);
        summary.Samples.Should().Be(20);
        summary.MaxPrivateMb.Should().Be(200, "期間之後的 999 MB 不算");
        summary.P95PrivateMb.Should().Be(190);
        summary.IdleSamples.Should().Be(0);
        summary.IdlePrivateMb.Should().BeNull();
    }

    [Fact]
    public void 閒置_取閒置期間後半的中位數()
    {
        // 剛結束負載時還沒回收（200、180），後半（120、110、100）的中位數是 110
        LoadSample[] samples = [new(1_000, 250, 1, 1), new(10_000, 200, 1, 1), new(11_000, 180, 1, 1), new(12_000, 120, 1, 1), new(13_000, 110, 1, 1), new(14_000, 100, 1, 1)];
        var summary = LoadRules.Summarize(samples, 0, 1_000, 10_000, 14_000, 32);
        summary.IdleSamples.Should().Be(5);
        summary.IdlePrivateMb.Should().Be(110);
        LoadRules.Summarize([new(1_000, 250, 1, 1), new(10_000, 130, 1, 1)], 0, 1_000, 10_000, 14_000, 32).IdlePrivateMb.Should().Be(130, "一個樣本時就是它");
        summary.IdleGcHeapMb.Should().BeNull("沒有 gcHeapMb 的取樣");
        summary.IdleGcCommittedMb.Should().BeNull("閒置修剪之前的 MyriagentHub 沒有 gcCommittedMb");
    }

    [Fact]
    public void 閒置_受管理堆積與已提交也取後半的中位數_沒有欄位的取樣不算()
    {
        LoadSample[] samples =
        [
            new(10_000, 200, 1, 1, GcHeapMb: 90, GcCommittedMb: 150),
            new(11_000, 180, 1, 1, GcHeapMb: 80, GcCommittedMb: 140),
            new(12_000, 120, 1, 1, GcHeapMb: 40, GcCommittedMb: 60),
            new(13_000, 110, 1, 1, GcHeapMb: 38, GcCommittedMb: null),
            new(14_000, 100, 1, 1, GcHeapMb: 36, GcCommittedMb: 50),
        ];
        var summary = LoadRules.Summarize(samples, 0, 1_000, 10_000, 14_000, 32);
        summary.IdlePrivateMb.Should().Be(110);
        summary.IdleGcHeapMb.Should().Be(38, "後半是 40、38、36");
        summary.IdleGcCommittedMb.Should().Be(50, "後半有欄位的是 60、50（nearest-rank 的中位數取較小的那個）");
    }

    [Fact]
    public void 延遲摘要_nearest_rank()
    {
        var latency = LoadRules.Latency(Enumerable.Range(1, 100).Select(i => (double)i).ToList());
        latency.Count.Should().Be(100);
        latency.P50.Should().Be(50);
        latency.P95.Should().Be(95);
        latency.P99.Should().Be(99);
        latency.Mean.Should().Be(50.5);
        latency.Min.Should().Be(1);
        latency.Max.Should().Be(100);
        LoadRules.Latency([]).Should().Be(new LatencySummary(0, null, null, null, null, null, null));
        LoadRules.Percentile([7], 99).Should().Be(7);
        FluentActions.Invoking(() => LoadRules.Percentile([], 50)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => LoadRules.Percentile([1], 0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 記憶體超過400MB才中止()
    {
        LoadRules.ShouldAbort(400.0).Should().BeFalse();
        LoadRules.ShouldAbort(400.1).Should().BeTrue();
    }

    // ───── 負載真的施加了沒（WP4.9 合併前審查）：伺服器閒著也會 pass 的那幾種情形 ─────

    private static readonly int?[] AllZero = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>10 台、5 個瀏覽器、5 台串流、1 台持久、1 個卡住的瀏覽器（load 的預設）；每個參數可以單獨蓋掉</summary>
    private static (IReadOnlyList<string> NotApplied, IReadOnlyList<string> Failures) Applied(bool chatsDone = true, bool exited = true, IReadOnlyList<int?>? codes = null,
        int browsers = 5, int streams = 5, int persistent = 1, int live = 4000, int persisted = 600, bool onlyStalled = true, int stalled = 1)
    {
        LoadRules.LoadApplied(chatsDone, exited, codes ?? AllZero, 10, browsers, streams, persistent, live, persisted, onlyStalled, stalled, out var notApplied, out var failures);
        return (notApplied, failures);
    }

    private static LoadVerdict Combined((IReadOnlyList<string> NotApplied, IReadOnlyList<string> Failures) applied, LoadVerdict? server = null) =>
        LoadRules.WithLoadApplied(server ?? LoadRules.Verdict(Summary(), idleRequired: true), applied.NotApplied, applied.Failures);

    [Fact]
    public void 負載施加了_伺服器的判定照舊()
    {
        var applied = Applied();
        applied.NotApplied.Should().BeEmpty();
        applied.Failures.Should().BeEmpty();
        Combined(applied).Result.Should().Be(LoadRules.Pass);
        Combined(applied, LoadRules.Verdict(Summary(maxMb: 300.1), idleRequired: true)).Result.Should().Be(LoadRules.Fail);
        Combined(applied, LoadRules.Verdict(Summary(coverage: 0.5), idleRequired: true)).Result.Should().Be(LoadRules.Insufficient);
    }

    [Fact]
    public void 對話沒有全部跑完_伺服器閒著也不是pass_是insufficient()
    {
        // 假裝置的 chat 全部失敗（例如 publish 被拒）時伺服器幾乎閒置：取樣的判定是 pass
        var verdict = Combined(Applied(chatsDone: false));
        verdict.Result.Should().Be(LoadRules.Insufficient);
        verdict.Reasons.Should().ContainSingle().Which.Should().Contain("跑完對話");
    }

    [Fact]
    public void 裝置沒有自己結束或結束碼不是0_是insufficient()
    {
        Combined(Applied(exited: false)).Result.Should().Be(LoadRules.Insufficient);
        var one = AllZero.ToArray();
        one[3] = 1;
        var bad = Combined(Applied(codes: one));
        bad.Result.Should().Be(LoadRules.Insufficient);
        bad.Reasons.Should().ContainSingle().Which.Should().Contain("第 4 台 1");
        var none = AllZero.ToArray();
        none[9] = null;
        Combined(Applied(codes: none)).Reasons.Should().ContainSingle().Which.Should().Contain("第 10 台 沒有結束碼");
        Combined(Applied(codes: AllZero.Take(9).ToList())).Reasons.Should().ContainSingle().Which.Should().Contain("9 個（要 10 個）");
    }

    [Fact]
    public void 有串流或持久的對話卻沒有延遲樣本_是insufficient_沒有瀏覽器就不要求()
    {
        Combined(Applied(live: 0)).Reasons.Should().ContainSingle().Which.Should().Contain("即時延遲");
        Combined(Applied(persisted: 0)).Reasons.Should().ContainSingle().Which.Should().Contain("持久延遲");
        Combined(Applied(live: 0)).Result.Should().Be(LoadRules.Insufficient);
        Combined(Applied(browsers: 0, live: 0, persisted: 0)).Result.Should().Be(LoadRules.Pass, "--backfill-only 與沒有瀏覽器時量不到延遲");
        Combined(Applied(streams: 0, persistent: 0, live: 0, persisted: 0)).Result.Should().Be(LoadRules.Pass);
    }

    [Fact]
    public void 卡住的瀏覽器沒被關或正常的被關_是fail_優先於insufficient()
    {
        var verdict = Combined(Applied(onlyStalled: false));
        verdict.Result.Should().Be(LoadRules.Fail);
        verdict.Reasons.Should().ContainSingle().Which.Should().Contain("慢用戶端");
        Combined(Applied(onlyStalled: false, chatsDone: false)).Result.Should().Be(LoadRules.Fail);
        Combined(Applied(onlyStalled: false), LoadRules.Verdict(Summary(coverage: 0.5), idleRequired: true)).Result.Should().Be(LoadRules.Fail);
    }

    [Fact]
    public void 轉送_台數乘20超過全域上限時驗不到每台的上限()
    {
        // 10 台＝200 > 64：每台的第 21 個先撞上已滿的全域上限（同一個 503 busy）；3 台＝60 ≤64 才分得出每台的上限
        LoadRules.PerDeviceCapVerified(3, 64).Should().BeTrue();
        LoadRules.PerDeviceCapVerified(4, 64).Should().BeFalse();
        LoadRules.PerDeviceCapVerified(10, 64).Should().BeFalse();
        LoadRules.PerDeviceCapVerified(3, 60).Should().BeTrue();
        LoadRules.PerDeviceCapVerified(3, 59).Should().BeFalse();
    }

    // ───── 轉送階段（WP4.9 步 12）─────

    private static RelayRequestResult Done(int index) => new(index, 200, null, false, 8 * 1024 * 1024, true, 12.5, 90_000, null);

    private static RelayRequestResult Busy(int index, string? error = "busy", bool retryAfter = true, int status = 503) => new(index, status, error, retryAfter, 0, false, 3.1, 3.5, null);

    private static RelayRequestResult Released() => new(LoadRules.RelayRequestsPerDevice, 206, null, false, LoadRules.RelayAfterReleaseBytes, true, 5, 6, null);

    private static RelayDeviceResult Device(string name, Func<int, RelayRequestResult>? request = null, RelayRequestResult? after = null, bool noAfter = false) =>
        new(name, $"/m/d_{name}-REF", Enumerable.Range(0, LoadRules.RelayRequestsPerDevice)
            .Select(i => request?.Invoke(i) ?? (i < LoadRules.RelayConcurrentPerDevice ? Done(i) : Busy(i))).ToList(), noAfter ? null : after ?? Released());

    private static LatencySummary Live(double p95, int count = 400) => new(count, 100, p95, 150, 150, p95, p95);

    private static IReadOnlyList<RelayDeviceResult> Fleet(int count = 3) => Enumerable.Range(1, count).Select(i => Device($"load-{i:00}")).ToList();

    [Fact]
    public void 轉送_每台恰好第21個被拒_其餘20個完成_名額收回_延遲與記憶體在線內才過()
    {
        LoadRules.RelayConcurrentPerDevice.Should().Be(20, "MediaRelayRules.MaxConcurrentPerDevice（設計:1061）");
        LoadRules.RelayRequestsPerDevice.Should().Be(21);
        var verdict = LoadRules.RelayVerdict(Fleet(), 3, Live(480), 350);
        verdict.Result.Should().Be(LoadRules.Pass, string.Join("；", verdict.Reasons));
        verdict.Reasons.Should().BeEmpty();
    }

    [Fact]
    public void 轉送_任一台多收_第21個也給了_就紅()
    {
        var devices = Fleet().ToList();
        devices[1] = Device("load-02", Done);
        var verdict = LoadRules.RelayVerdict(devices, 3, Live(100), 200);
        verdict.Result.Should().Be(LoadRules.Fail);
        verdict.Reasons.Should().ContainSingle().Which.Should().Contain("load-02").And.Contain("多收");
    }

    [Fact]
    public void 轉送_任一台少收_前20個有一個被拒或沒讀完_就紅()
    {
        var rejectedEarly = Fleet().ToList();
        rejectedEarly[0] = Device("load-01", i => i == 7 ? Busy(i) : i < LoadRules.RelayConcurrentPerDevice ? Done(i) : Busy(i));
        var verdict = LoadRules.RelayVerdict(rejectedEarly, 3, Live(100), 200);
        verdict.Result.Should().Be(LoadRules.Fail);
        verdict.Reasons.Should().ContainSingle().Which.Should().Contain("load-01").And.Contain("19 個").And.Contain("第 8 個");

        var truncated = Fleet().ToList();
        truncated[2] = Device("load-03", i => i == 0 ? Done(i) with { Complete = false, Bytes = 1_000_000, Failure = "SHA-256 和檔案不同" } : i < LoadRules.RelayConcurrentPerDevice ? Done(i) : Busy(i));
        LoadRules.RelayVerdict(truncated, 3, Live(100), 200).Reasons.Should().ContainSingle().Which.Should().Contain("load-03").And.Contain("SHA-256");

        var noResponse = Fleet().ToList();
        noResponse[2] = Device("load-03", i => i == 19 ? new RelayRequestResult(i, null, null, false, 0, false, null, null, "30 秒內沒有拿到標頭") : i < LoadRules.RelayConcurrentPerDevice ? Done(i) : Busy(i));
        LoadRules.RelayVerdict(noResponse, 3, Live(100), 200).Result.Should().Be(LoadRules.Fail);
    }

    [Fact]
    public void 轉送_第21個要是503_busy_加RetryAfter_其他的拒絕都紅()
    {
        foreach (var wrong in new[] { Busy(20, error: "offline"), Busy(20, retryAfter: false), Busy(20, status: 504, error: "timeout"), Busy(20, error: null) })
        {
            var devices = Fleet().ToList();
            devices[0] = Device("load-01", i => i < LoadRules.RelayConcurrentPerDevice ? Done(i) : wrong);
            var verdict = LoadRules.RelayVerdict(devices, 3, Live(100), 200);
            verdict.Result.Should().Be(LoadRules.Fail, $"{wrong.Status} {wrong.Error} RetryAfter={wrong.RetryAfter}");
            verdict.Reasons.Should().ContainSingle().Which.Should().Contain("第 21 個");
        }
    }

    [Fact]
    public void 轉送_讀完之後名額沒有收回_就紅()
    {
        var devices = Fleet().ToList();
        devices[1] = Device("load-02", after: Busy(LoadRules.RelayRequestsPerDevice));
        LoadRules.RelayVerdict(devices, 3, Live(100), 200).Reasons.Should().ContainSingle().Which.Should().Contain("load-02").And.Contain("名額沒有收回");
        devices[1] = Device("load-02", noAfter: true);
        LoadRules.RelayVerdict(devices, 3, Live(100), 200).Result.Should().Be(LoadRules.Fail);
        devices[1] = Device("load-02", after: Released() with { Complete = false });
        LoadRules.RelayVerdict(devices, 3, Live(100), 200).Result.Should().Be(LoadRules.Fail);
    }

    [Fact]
    public void 轉送_期間S7的p95_500過_500點1不過()
    {
        LoadRules.RelayVerdict(Fleet(), 3, Live(500.0), 200).Result.Should().Be(LoadRules.Pass);
        var fail = LoadRules.RelayVerdict(Fleet(), 3, Live(500.1), 200);
        fail.Result.Should().Be(LoadRules.Fail);
        fail.Reasons.Should().ContainSingle().Which.Should().Contain("500.1");
    }

    [Fact]
    public void 轉送_私有記憶體要小於保險絲告警線400MB()
    {
        LoadRules.FuseWarnMb.Should().Be(LoadRules.AbortPrivateMb, "保險絲告警線＝Hub:MemoryFuse:WarnMb 預設 400，也是 load 自己中止的門檻");
        LoadRules.RelayVerdict(Fleet(), 3, Live(100), 399.9).Result.Should().Be(LoadRules.Pass);
        var fail = LoadRules.RelayVerdict(Fleet(), 3, Live(100), 400.0);
        fail.Result.Should().Be(LoadRules.Fail);
        fail.Reasons.Should().ContainSingle().Which.Should().Contain("400");
    }

    [Fact]
    public void 轉送_台數不對_沒拿到網址_請求數不對_沒有延遲或記憶體樣本都是insufficient()
    {
        LoadRules.RelayVerdict(Fleet(2), 3, Live(100), 200).Result.Should().Be(LoadRules.Insufficient);
        var noUrl = Fleet().ToList();
        noUrl[0] = new RelayDeviceResult("load-01", null, [], null);
        LoadRules.RelayVerdict(noUrl, 3, Live(100), 200).Reasons.Should().ContainSingle().Which.Should().Contain("load-01").And.Contain("網址");
        var fewer = Fleet().ToList();
        fewer[0] = fewer[0] with { Requests = fewer[0].Requests.Take(20).ToList() };
        LoadRules.RelayVerdict(fewer, 3, Live(100), 200).Result.Should().Be(LoadRules.Insufficient);
        LoadRules.RelayVerdict(Fleet(), 3, LoadRules.Latency([]), 200).Result.Should().Be(LoadRules.Insufficient);
        LoadRules.RelayVerdict(Fleet(), 3, Live(100), null).Result.Should().Be(LoadRules.Insufficient);
        // 不足的優先：多收同時發生也是 insufficient（不能判）
        var both = Fleet(2).Append(Device("load-03", Done)).ToList();
        LoadRules.RelayVerdict(both, 4, Live(100), 200).Result.Should().Be(LoadRules.Insufficient);
    }

    /// <summary>台數 × 20 超過全域上限時的一組結果：照順序把 accepted 個名額發給各台（每台最多 20 個），其餘前 20 個是 503 busy</summary>
    private static IReadOnlyList<RelayDeviceResult> CappedFleet(int count, int accepted)
    {
        var left = accepted;
        return Enumerable.Range(1, count).Select(n =>
        {
            var mine = Math.Min(left, LoadRules.RelayConcurrentPerDevice);
            left -= mine;
            return Device($"load-{n:00}", i => i < mine ? Done(i) : Busy(i));
        }).ToList();
    }

    [Fact]
    public void 轉送_應接受的數量是每台20個與全域上限取小()
    {
        LoadRules.DefaultRelayCap.Should().Be(64, "MediaOptions.MaxConcurrentRelays 的預設（main 9fbd077）");
        LoadRules.ExpectedAcceptedRelays(3, 64).Should().Be(60);
        LoadRules.ExpectedAcceptedRelays(4, 64).Should().Be(64);
        LoadRules.ExpectedAcceptedRelays(10, 64).Should().Be(64);
        LoadRules.ExpectedAcceptedRelays(10, 30).Should().Be(30);
        LoadRules.ExpectedAcceptedRelays(0, 64).Should().Be(0);
        FluentActions.Invoking(() => LoadRules.ExpectedAcceptedRelays(3, 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => LoadRules.RelayVerdict(Fleet(), 3, Live(100), 200, relayCap: 0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 轉送_超過全域上限_合計剛好上限個完成_其餘503busy_就過()
    {
        var verdict = LoadRules.RelayVerdict(CappedFleet(10, 64), 10, Live(300), 350);
        verdict.Result.Should().Be(LoadRules.Pass, string.Join("；", verdict.Reasons));

        // 名額怎麼分給各台不影響判定（到達順序決定）：每台 6 或 7 個
        var spread = Enumerable.Range(1, 10).Select(n => Device($"load-{n:00}", i => i < (n <= 4 ? 7 : 6) ? Done(i) : Busy(i))).ToList();
        LoadRules.RelayVerdict(spread, 10, Live(300), 350).Result.Should().Be(LoadRules.Pass);

        // 較小的上限（--relay-cap 30，三台就超過）
        LoadRules.RelayVerdict(CappedFleet(3, 30), 3, Live(300), 350, relayCap: 30).Result.Should().Be(LoadRules.Pass);
        // 三台在預設上限底下照舊每台 20 個：只收 30 個是少收
        LoadRules.RelayVerdict(CappedFleet(3, 30), 3, Live(300), 350).Result.Should().Be(LoadRules.Fail);
    }

    [Fact]
    public void 轉送_超過全域上限_合計多收或少收就紅()
    {
        var more = LoadRules.RelayVerdict(CappedFleet(10, 65), 10, Live(300), 350);
        more.Result.Should().Be(LoadRules.Fail);
        more.Reasons.Should().ContainSingle().Which.Should().Contain("合計 65 個").And.Contain("多收");

        var fewer = LoadRules.RelayVerdict(CappedFleet(10, 63), 10, Live(300), 350);
        fewer.Result.Should().Be(LoadRules.Fail);
        fewer.Reasons.Should().ContainSingle().Which.Should().Contain("合計 63 個").And.Contain("少收");
    }

    [Fact]
    public void 轉送_超過全域上限_沒拿到名額的不是503busy_或第21個給了_就紅()
    {
        var timeout = CappedFleet(10, 64).ToList();
        timeout[9] = Device("load-10", i => i == 3 ? Busy(i, status: 504, error: "timeout") : Busy(i));
        var verdict = LoadRules.RelayVerdict(timeout, 10, Live(300), 350);
        verdict.Result.Should().Be(LoadRules.Fail);
        verdict.Reasons.Should().ContainSingle().Which.Should().Contain("load-10").And.Contain("第 4 個").And.Contain("504");

        var extraGiven = CappedFleet(10, 64).ToList();
        extraGiven[8] = Device("load-09", i => i < LoadRules.RelayConcurrentPerDevice ? Busy(i) : Done(i));
        LoadRules.RelayVerdict(extraGiven, 10, Live(300), 350).Reasons.Should().Contain(r => r.Contains("load-09") && r.Contains("多收"));

        var notReleased = CappedFleet(10, 64).ToList();
        notReleased[9] = notReleased[9] with { AfterRelease = Busy(LoadRules.RelayRequestsPerDevice) };
        LoadRules.RelayVerdict(notReleased, 10, Live(300), 350).Reasons.Should().ContainSingle().Which.Should().Contain("名額沒有收回");
    }

    [Fact]
    public void 轉送_伺服器在期間內倒了是fail_不是insufficient()
    {
        var crashed = Fleet().Select(d => d with
        {
            Requests = d.Requests.Select(r => new RelayRequestResult(r.Index, null, null, false, 0, false, null, null, "連線被拒")).ToList(),
            AfterRelease = null,
        }).ToList();
        var verdict = LoadRules.RelayVerdict(crashed, 3, LoadRules.Latency([]), null, "MyriagentHub 在轉送期間結束（結束碼 -532462766）");
        verdict.Result.Should().Be(LoadRules.Fail, "沒有記憶體與延遲的樣本是因為伺服器倒了");
        verdict.Reasons.Should().ContainSingle().Which.Should().Contain("結束碼");
        LoadRules.RelayVerdict(crashed, 3, LoadRules.Latency([]), null).Result.Should().Be(LoadRules.Insufficient, "沒有說伺服器倒了時照舊");

        // 私有記憶體超過中止門檻、load 自己中止（D19）：fail，原因寫出數字與兩條線
        var aborted = LoadRules.RelayAbortedVerdict(477);
        aborted.Result.Should().Be(LoadRules.Fail);
        aborted.Reasons.Should().ContainSingle().Which.Should().Contain("477").And.Contain("400");
    }

    [Fact]
    public void 慢速讀取的節奏_讀得比進度快才等()
    {
        var span = TimeSpan.FromSeconds(90);
        const long total = 8 * 1024 * 1024;
        LoadRules.RelayPaceDelay(total / 2, total, TimeSpan.FromSeconds(10), span).Should().Be(TimeSpan.FromSeconds(35), "一半的進度在 45 秒");
        LoadRules.RelayPaceDelay(total / 2, total, TimeSpan.FromSeconds(50), span).Should().Be(TimeSpan.Zero, "落後就不等");
        LoadRules.RelayPaceDelay(total * 2, total, TimeSpan.Zero, span).Should().Be(span, "超過總長以總長算");
        LoadRules.RelayPaceDelay(0, total, TimeSpan.Zero, span).Should().Be(TimeSpan.Zero);
        LoadRules.RelayPaceDelay(100, 0, TimeSpan.Zero, span).Should().Be(TimeSpan.Zero);
        LoadRules.RelayPaceDelay(100, total, TimeSpan.Zero, TimeSpan.Zero).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void 伺服器記錄的中斷原因()
    {
        HubLogRules.Classify("      瀏覽器連線 60 秒內第 3 次溢出，中斷：0HNABC").Should().Be(HubLogRules.SlowClient);
        HubLogRules.Classify("      瀏覽器連線送出失敗，中斷：0HNABC send_timeout").Should().Be(HubLogRules.SendTimeout);
        HubLogRules.Classify("      瀏覽器連線送出失敗，中斷：0HNABC send_failed").Should().Be(HubLogRules.SendFailed);
        HubLogRules.Classify("      瀏覽器連線的送出佇列溢出，清空並重送 init：0HNABC").Should().Be(HubLogRules.Resync);
        HubLogRules.Classify("info: MyriagentHub.Realtime.BrowserHub[0]").Should().BeNull();
        HubLogRules.Classify(null).Should().BeNull();
    }

    [Fact]
    public void 記錄檔的中斷原因_只看JSON行的訊息()
    {
        string[] lines =
        [
            """{"t":"2026-10-21T04:31:00.0000000+00:00","l":"Warning","c":"MyriagentHub.Realtime.BrowserGateway","m":"瀏覽器連線的送出佇列溢出，清空並重送 init：0HNA"}""",
            """{"t":"2026-10-21T04:31:01.0000000+00:00","l":"Warning","c":"MyriagentHub.Realtime.BrowserGateway","m":"瀏覽器連線 60 秒內第 3 次溢出，中斷：0HNA"}""",
            """{"t":"2026-10-21T04:31:02.0000000+00:00","l":"Warning","c":"MyriagentHub.Realtime.BrowserConnections","m":"瀏覽器連線送出失敗，中斷：0HNB send_timeout"}""",
            """{"l":"Information","c":"x","m":"瀏覽器連線 60 秒內第 3 次溢出","x":"送出失敗，中斷 只在例外欄位"}""",
            """{"l":"Warning","m":null}""",
            """{"l":"Warning","m":7}""",
            "瀏覽器連線 60 秒內第 3 次溢出，中斷：主控台那種不是 JSON 的行",
            """{"m":"瀏覽器連線 60 秒內第 3 次溢出，中斷：壞掉的""",
            "",
        ];
        var counts = HubLogRules.CountFileLog(lines);
        counts.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [HubLogRules.Resync] = 1,
            [HubLogRules.SlowClient] = 1,
            [HubLogRules.SendTimeout] = 1,
        });
    }
}
