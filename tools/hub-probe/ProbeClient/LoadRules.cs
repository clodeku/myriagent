namespace MyriagentHub.ProbeClient;

/// <summary>/readyz 的一次取樣（load 的取樣器）：時間是用戶端收到回應的 UTC 毫秒；CPU 是伺服器距上一次取樣的平均與那段時間（秒）</summary>
/// <param name="RelaysActive"><c>blobs.relaysActive</c>：伺服器佔著名額的媒體轉送數（WP4.9 步 12 只記錄）</param>
/// <param name="RelayTimeouts"><c>blobs.relayTimeouts</c>：首位元組逾時的累計次數</param>
/// <param name="GcHeapMb"><c>process.gcHeapMb</c>（受管理堆積；MyriagentHub 的 GC 硬上限是 512 MB）</param>
/// <param name="GcCommittedMb"><c>process.gcCommittedMb</c>（上一次 GC 結束時 GC 已提交的空間；閒置修剪之前的 MyriagentHub 沒有這個欄位，是 null）</param>
internal sealed record LoadSample(long AtMs, double PrivateMb, double? CpuPercent, double? CpuWindowSeconds, int? RelaysActive = null, long? RelayTimeouts = null,
    double? GcHeapMb = null, double? GcCommittedMb = null);

/// <summary>轉送階段（WP4.9 步 12）的一個 <c>/m/</c> 請求</summary>
/// <param name="Index">第幾個（0 起）：0～19 先送，全部拿到標頭（名額都佔住、本文還沒讀）才送 20（＝第 21 個）</param>
/// <param name="Status">HTTP 狀態碼；連不上或逾時是 null</param>
/// <param name="Error">錯誤回應的 <c>error</c> 代碼（<c>busy</c>、<c>offline</c>…）；不是錯誤回應是 null</param>
/// <param name="RetryAfter">回應帶 <c>Retry-After</c></param>
/// <param name="Bytes">讀到的本文位元組數</param>
/// <param name="Complete">本文讀完、長度等於 <c>Content-Length</c> 也等於預期，而且有預期的 SHA-256 時相同</param>
/// <param name="HeadersMs">送出到拿到標頭（毫秒）</param>
/// <param name="DoneMs">送出到讀完（毫秒）</param>
/// <param name="Failure">例外、逾時或內容不符的說明</param>
internal sealed record RelayRequestResult(int Index, int? Status, string? Error, bool RetryAfter, long Bytes, bool Complete, double? HeadersMs, double? DoneMs, string? Failure);

/// <param name="Device">裝置名稱（load-NN）</param>
/// <param name="Url">瀏覽器從媒體短對話的項目拿到的 <c>/m/</c> 網址；沒拿到是 null（沒有發請求）</param>
/// <param name="Requests">發出的請求（依 Index）</param>
/// <param name="AfterRelease">20 個都讀完之後再一個帶 Range 的請求（名額收回了才是 206）；沒有發是 null</param>
internal sealed record RelayDeviceResult(string Device, string? Url, IReadOnlyList<RelayRequestResult> Requests, RelayRequestResult? AfterRelease);

/// <summary>CPU 依時間窗加權的平均（WP4.9 計畫 D16）</summary>
/// <param name="Percent">加權平均（%，同 /readyz：已除以處理器數）；沒有可用的時間窗是 null</param>
/// <param name="Cores">換算的核心數（Percent / 100 × 處理器數）</param>
/// <param name="CoveredSeconds">時間窗落在量測期間內的合計秒數</param>
/// <param name="PeriodSeconds">量測期間的秒數</param>
/// <param name="Coverage">CoveredSeconds / PeriodSeconds（0～1）</param>
/// <param name="Windows">算進去的時間窗數</param>
/// <param name="Duplicates">沿用上一次值而略過的重複窗（同一組 cpuPercent＋cpuWindowSeconds 連續出現）</param>
internal sealed record CpuSummary(double? Percent, double? Cores, double CoveredSeconds, double PeriodSeconds, double Coverage, int Windows, int Duplicates);

/// <param name="Samples">負載期間的取樣數</param>
/// <param name="MaxPrivateMb">負載期間最大的私有記憶體</param>
/// <param name="P95PrivateMb">負載期間私有記憶體的 p95（nearest-rank）</param>
/// <param name="IdleSamples">閒置期間的取樣數</param>
/// <param name="IdlePrivateMb">閒置的私有記憶體：閒置期間後半的取樣的中位數（剛結束負載時還沒回收，前半不算）</param>
/// <param name="ProcessorCount">伺服器的處理器數（換算核心數用）</param>
/// <param name="IdleGcHeapMb">閒置的受管理堆積（<c>gcHeapMb</c>），取法同 <paramref name="IdlePrivateMb"/>；沒有這個欄位的取樣不算，全部沒有是 null（只記錄）</param>
/// <param name="IdleGcCommittedMb">閒置的 GC 已提交空間（<c>gcCommittedMb</c>），取法同上（只記錄；設計 14.20 的閒置修剪）</param>
internal sealed record LoadSummary(int Samples, CpuSummary Cpu, double? MaxPrivateMb, double? P95PrivateMb, int IdleSamples, double? IdlePrivateMb, int ProcessorCount,
    double? IdleGcHeapMb = null, double? IdleGcCommittedMb = null);

/// <summary>延遲的摘要（毫秒；同 hub-e2e 的 stats-rules.mjs summarize：nearest-rank 百分位數、平均取一位小數）</summary>
internal sealed record LatencySummary(int Count, double? Min, double? Max, double? Mean, double? P50, double? P95, double? P99);

/// <summary>判定：pass、fail 或 insufficient（樣本不足、CPU 時間窗涵蓋不到 90%、要閒置卻沒有閒置的樣本），附原因</summary>
internal sealed record LoadVerdict(string Result, IReadOnlyList<string> Reasons);

/// <summary>
/// S9 的負載摘要與判定（WP4.9 計畫步 9、D16；純函式）。門檻（設計:188、:2370）：私有記憶體 ≤300 MB、平均 CPU ≤5%、閒置 &lt;150 MB；
/// ≤5% 只拿 Hub 主機的數字判（開發機處理器多、百分比偏低），本機的判定只當參考。CPU 不直接平均樣本：/readyz 的 cpuPercent 是「距上一次取樣」的平均，
/// 時間窗會被記憶體保險絲與其他讀取切短（OpsMetrics.cs），所以依時間窗加權、去掉沿用上一次值的重複窗，並回報涵蓋率
/// </summary>
internal static class LoadRules
{
    public const double MaxPrivateMb = 300.0;

    public const double MaxCpuPercent = 5.0;

    /// <summary>閒置要「小於」這個值</summary>
    public const double MaxIdlePrivateMb = 150.0;

    public const double MinCoverage = 0.90;

    public const int MinSamples = 2;

    /// <summary>私有記憶體超過這個值 load 自己中止（D19：結束碼 4）</summary>
    public const double AbortPrivateMb = 400.0;

    public const string Pass = "pass";

    public const string Fail = "fail";

    public const string Insufficient = "insufficient";

    public static bool ShouldAbort(double privateMb) => privateMb > AbortPrivateMb;

    /// <summary>每台來源同時的轉送數（MediaRelayRules.MaxConcurrentPerDevice，設計:1061；伺服器與桌面都是 20）</summary>
    public const int RelayConcurrentPerDevice = 20;

    /// <summary>轉送階段每台發的請求數：滿額再多一個</summary>
    public const int RelayRequestsPerDevice = RelayConcurrentPerDevice + 1;

    /// <summary>
    /// 伺服器所有來源合計同時的轉送上限的預設值（<c>Hub:Media:MaxConcurrentRelays</c>，MediaOptions；main 9fbd077 起）：
    /// 超過的照每台的上限一樣回 503 <c>busy</c> 加 Retry-After
    /// </summary>
    public const int DefaultRelayCap = 64;

    /// <summary>
    /// 轉送階段應該被接受（200、完整讀完）的請求數：每台最多 <see cref="RelayConcurrentPerDevice"/> 個、全部合計最多 <paramref name="relayCap"/> 個
    /// （每台的 20 個都在任何一個讀完之前送出、佔著名額，所以合計剛好是兩者取小）
    /// </summary>
    public static int ExpectedAcceptedRelays(int devices, int relayCap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(devices);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(relayCap, 0);
        return Math.Min(devices * RelayConcurrentPerDevice, relayCap);
    }

    /// <summary>忙碌時的錯誤代碼（MediaApiErrors.Busy；503 加 Retry-After，P47:1265）</summary>
    public const string RelayBusyCode = "busy";

    /// <summary>名額收回之後那一個 Range 請求的位元組數</summary>
    public const int RelayAfterReleaseBytes = 64 * 1024;

    /// <summary>S7：即時延遲 p95 的上限（設計:186）</summary>
    public const double MaxLiveP95Ms = 500.0;

    /// <summary>記憶體保險絲的告警線（MyriagentHub 的 Hub:MemoryFuse:WarnMb 預設 400 MB）：轉送階段的私有記憶體要「小於」它</summary>
    public const double FuseWarnMb = 400.0;

    /// <summary>
    /// 慢速讀取的節奏（純函式）：已讀 <paramref name="bytesRead"/>／<paramref name="totalBytes"/> 時，照 <paramref name="span"/> 讀完的進度
    /// 應該已經過了多久；讀得比進度快就回要等的時間，否則 0
    /// </summary>
    public static TimeSpan RelayPaceDelay(long bytesRead, long totalBytes, TimeSpan elapsed, TimeSpan span)
    {
        if (totalBytes <= 0 || span <= TimeSpan.Zero || bytesRead <= 0)
            return TimeSpan.Zero;
        var due = span * ((double)Math.Min(bytesRead, totalBytes) / totalBytes);
        return due > elapsed ? due - elapsed : TimeSpan.Zero;
    }

    /// <summary>
    /// 轉送階段的判定（WP4.9 計畫步 12、WP4.7 R6；純函式）：
    /// <list type="bullet">
    /// <item>每台恰好第 21 個被拒：前 20 個都是 200 而且完整讀完，第 21 個（20 個都拿到標頭之後才送）是 503、<c>busy</c>、帶 Retry-After；
    /// 讀完之後再一個 Range 請求是 206（名額收回）。任一台多收（第 21 個也給了）或少收（前 20 個有被拒或沒讀完）都是 fail。</item>
    /// <item>全域上限（<paramref name="relayCap"/>，<c>Hub:Media:MaxConcurrentRelays</c>）：台數 × 20 超過它時，哪幾台拿到名額看到達順序，
    /// 改成合計判——前 20 個裡完整完成的合計要剛好 <see cref="ExpectedAcceptedRelays"/> 個，其餘每一個都要是 503 <c>busy</c> 加 Retry-After
    /// （被全域上限擋下），第 21 個與名額收回照每台判。沒超過時照每台各 20 個判。</item>
    /// <item>期間其他對話（串流）的即時延遲 p95 ≤ <see cref="MaxLiveP95Ms"/>；伺服器的私有記憶體最大值 &lt; <see cref="FuseWarnMb"/>。</item>
    /// <item>insufficient：台數不對、有一台沒拿到網址、請求數不是 21、期間沒有即時延遲的樣本、沒有記憶體取樣（不能判）。</item>
    /// <item><paramref name="serverLost"/>（伺服器在期間內結束、或期間的 /readyz 一次都讀不到）一律 fail，優先於 insufficient：
    /// 伺服器倒了不是「樣本不足」。</item>
    /// </list>
    /// </summary>
    /// <param name="serverLost">伺服器在期間內沒了的說明（結束碼、/readyz 失敗次數）；沒事是 null</param>
    /// <param name="relayCap">伺服器所有來源合計同時的轉送上限（預設 <see cref="DefaultRelayCap"/>）</param>
    public static LoadVerdict RelayVerdict(IReadOnlyList<RelayDeviceResult> devices, int expectedDevices, LatencySummary live, double? maxPrivateMb, string? serverLost = null,
        int relayCap = DefaultRelayCap)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(relayCap, 0);
        if (serverLost is not null)
            return new LoadVerdict(Fail, [serverLost]);
        var reasons = new List<string>();
        if (devices.Count != expectedDevices)
            reasons.Add($"轉送階段有 {devices.Count} 台的結果（要 {expectedDevices} 台）");
        foreach (var device in devices)
        {
            if (device.Url is null)
                reasons.Add($"{device.Device}：沒有拿到 /m/ 網址");
            else if (device.Requests.Count != RelayRequestsPerDevice)
                reasons.Add($"{device.Device}：發了 {device.Requests.Count} 個請求（要 {RelayRequestsPerDevice} 個）");
        }

        if (live.Count == 0)
            reasons.Add("轉送期間沒有即時延遲的樣本（S7）");
        if (maxPrivateMb is null)
            reasons.Add("轉送期間沒有記憶體取樣");
        if (reasons.Count > 0)
            return new LoadVerdict(Insufficient, reasons);

        var capped = devices.Count * RelayConcurrentPerDevice > relayCap;
        var acceptedTotal = 0;
        foreach (var device in devices)
        {
            var held = device.Requests.Where(r => r.Index < RelayConcurrentPerDevice).OrderBy(r => r.Index).ToList();
            var extra = device.Requests.SingleOrDefault(r => r.Index == RelayConcurrentPerDevice);
            var completed = held.Count(IsAccepted);
            acceptedTotal += completed;
            if (held.Count != RelayConcurrentPerDevice)
            {
                reasons.Add($"{device.Device}：前 {RelayConcurrentPerDevice} 個只有 {held.Count} 個有結果");
            }
            else if (!capped && completed != RelayConcurrentPerDevice)
            {
                var bad = held.Where(r => !IsAccepted(r)).Select(r => $"第 {r.Index + 1} 個 {Describe(r)}");
                reasons.Add($"{device.Device}：前 {RelayConcurrentPerDevice} 個只有 {completed} 個完整完成（{string.Join("、", bad)}）");
            }
            else if (capped && held.Where(r => !IsAccepted(r)).ToList() is { Count: > 0 } other && other.Any(r => !IsBusy(r)))
            {
                var bad = other.Where(r => !IsBusy(r)).Select(r => $"第 {r.Index + 1} 個 {Describe(r)}");
                reasons.Add($"{device.Device}：前 {RelayConcurrentPerDevice} 個裡沒有完整完成的不是被全域上限 {relayCap} 擋下的 503 busy 加 Retry-After（{string.Join("、", bad)}）");
            }

            if (extra is null || !IsBusy(extra))
            {
                var what = extra?.Status == 200 ? "多收：伺服器也轉送了" : "不是 503 busy 加 Retry-After";
                reasons.Add($"{device.Device}：第 {RelayRequestsPerDevice} 個沒有照規定被拒（{what}；{(extra is null ? "沒有" : Describe(extra))}）");
            }

            if (device.AfterRelease is not { Status: 206, Complete: true })
                reasons.Add($"{device.Device}：讀完之後名額沒有收回（Range 請求 {(device.AfterRelease is null ? "沒有發" : Describe(device.AfterRelease))}）");
        }

        if (capped && acceptedTotal != ExpectedAcceptedRelays(devices.Count, relayCap))
        {
            var what = acceptedTotal > relayCap ? "多收：超過全域上限" : "少收";
            reasons.Add($"{devices.Count} 台合計 {acceptedTotal} 個完整完成（全域上限 {relayCap}：要剛好 {relayCap} 個；{what}）");
        }

        if (live.P95 > MaxLiveP95Ms)
            reasons.Add($"轉送期間串流的即時延遲 p95 {live.P95} ms（上限 {MaxLiveP95Ms} ms）");
        if (maxPrivateMb >= FuseWarnMb)
            reasons.Add($"轉送期間伺服器的私有記憶體最大 {maxPrivateMb} MB（要小於保險絲告警線 {FuseWarnMb} MB）");
        return new LoadVerdict(reasons.Count == 0 ? Pass : Fail, reasons);
    }

    /// <summary>
    /// 轉送期間私有記憶體超過 load 的中止門檻（<see cref="AbortPrivateMb"/>，D19）、load 自己中止時的判定：一律 fail（已經超過保險絲告警線），
    /// 請求的結果不完整（中止時取消了）
    /// </summary>
    public static LoadVerdict RelayAbortedVerdict(double privateMb) =>
        new(Fail, [$"轉送期間伺服器的私有記憶體 {privateMb} MB 超過 load 的中止門檻 {AbortPrivateMb} MB（保險絲告警線 {FuseWarnMb} MB），load 中止（D19）"]);

    /// <summary>被接受：200 而且完整讀完</summary>
    private static bool IsAccepted(RelayRequestResult r) => r.Status == 200 && r.Complete;

    /// <summary>照規定被拒：503 <c>busy</c> 加 Retry-After（每台與全域的上限都是這個回應）</summary>
    private static bool IsBusy(RelayRequestResult r) => r.Status == 503 && r.Error == RelayBusyCode && r.RetryAfter;

    private static string Describe(RelayRequestResult r) =>
        (r.Status is { } status ? status.ToString(System.Globalization.CultureInfo.InvariantCulture) : "沒有回應")
        + (r.Error is { } error ? " " + error : "")
        + (r.Failure is { } failure ? "：" + failure : "")
        + (r.Status is 200 or 206 && !r.Complete ? $"（讀到 {r.Bytes} 位元組、不完整）" : "");

    /// <summary>
    /// CPU 的加權平均：每個取樣的時間窗是 [AtMs − 窗長, AtMs]，只算落在 [<paramref name="startMs"/>, <paramref name="endMs"/>] 裡的那一段；
    /// 和前一個取樣的 (cpuPercent, cpuWindowSeconds) 完全相同的是沿用上一次的值（查得太密時伺服器不重算），只算一次
    /// </summary>
    public static CpuSummary Cpu(IReadOnlyList<LoadSample> samples, long startMs, long endMs, int processorCount)
    {
        ArgumentNullException.ThrowIfNull(samples);
        double weighted = 0;
        double covered = 0;
        var windows = 0;
        var duplicates = 0;
        LoadSample? previous = null;
        foreach (var sample in samples)
        {
            var before = previous;
            previous = sample;
            if (sample.CpuPercent is not { } percent || sample.CpuWindowSeconds is not { } window || !double.IsFinite(percent) || !double.IsFinite(window) || window <= 0)
                continue;
            if (before is { CpuPercent: { } p, CpuWindowSeconds: { } w } && p.Equals(percent) && w.Equals(window))
            {
                duplicates++;
                continue;
            }

            var from = Math.Max(sample.AtMs - window * 1000, startMs);
            var to = Math.Min(sample.AtMs, endMs);
            var seconds = (to - from) / 1000.0;
            if (seconds <= 0)
                continue;
            weighted += percent * seconds;
            covered += seconds;
            windows++;
        }

        var period = Math.Max(0, endMs - startMs) / 1000.0;
        double? average = covered > 0 ? Math.Round(weighted / covered, 2) : null;
        double? cores = average is { } a ? Math.Round(a / 100 * processorCount, 3) : null;
        var coverage = period > 0 ? Math.Round(Math.Min(1, covered / period), 4) : 0;
        return new CpuSummary(average, cores, Math.Round(covered, 3), period, coverage, windows, duplicates);
    }

    /// <summary>
    /// 負載期間 [<paramref name="loadStartMs"/>, <paramref name="loadEndMs"/>] 的 CPU 與記憶體，閒置期間（有給時）的私有記憶體。
    /// CPU 用全部的取樣再依時間窗裁切（期間結束後第一個取樣的窗也涵蓋期間的最後一段）
    /// </summary>
    public static LoadSummary Summarize(IReadOnlyList<LoadSample> samples, long loadStartMs, long loadEndMs, long? idleStartMs, long? idleEndMs, int processorCount)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var load = samples.Where(s => s.AtMs >= loadStartMs && s.AtMs <= loadEndMs).Select(s => s.PrivateMb).ToList();
        var idle = idleStartMs is { } from && idleEndMs is { } to
            ? samples.Where(s => s.AtMs >= from && s.AtMs <= to).OrderBy(s => s.AtMs).ToList()
            : [];
        // 閒置：後半的中位數（一個樣本時就是它）；受管理堆積與已提交只看後半裡有這個欄位的取樣
        var settled = idle.Skip(idle.Count / 2).ToList();
        static double? Median(IEnumerable<double?> values)
        {
            var known = values.Where(v => v is not null).Select(v => v!.Value).ToList();
            return known.Count > 0 ? Percentile(known, 50) : null;
        }

        return new LoadSummary(
            load.Count,
            Cpu(samples, loadStartMs, loadEndMs, processorCount),
            load.Count > 0 ? load.Max() : null,
            load.Count > 0 ? Percentile(load, 95) : null,
            idle.Count,
            Median(settled.Select(s => (double?)s.PrivateMb)),
            processorCount,
            Median(settled.Select(s => s.GcHeapMb)),
            Median(settled.Select(s => s.GcCommittedMb)));
    }

    /// <summary>
    /// 負載真的施加了沒（WP4.9 合併前審查；<see cref="Verdict"/> 只看伺服器取樣，假裝置的 chat 全部失敗、伺服器閒著時也會 pass）：
    /// 回 <paramref name="notApplied"/>（負載沒施加，判定改 insufficient）與 <paramref name="failures"/>（慢用戶端的條件不成立，判定改 fail）。
    /// notApplied：不是每台都跑完對話（chatsDone）、裝置沒有全部自己結束、有一台的結束碼不是 0（或沒有結束碼）、有串流與瀏覽器卻沒有任何即時延遲的樣本、
    /// 有持久的對話與瀏覽器卻沒有任何持久延遲的樣本。failures：<paramref name="onlyStalled"/> 不成立（卡住的沒被關、正常的被關、或伺服器記錄的中斷多於卡住的條數）
    /// </summary>
    public static void LoadApplied(bool chatsDone, bool devicesExited, IReadOnlyList<int?> exitCodes, int devices, int browsers, int streams, int persistent,
        int liveSamples, int persistentSamples, bool onlyStalled, int stalled, out IReadOnlyList<string> notApplied, out IReadOnlyList<string> failures)
    {
        ArgumentNullException.ThrowIfNull(exitCodes);
        var missing = new List<string>();
        if (!chatsDone)
            missing.Add("不是每台都跑完對話（chat 步驟沒有全部完成：劇本失敗或逾時）");
        if (!devicesExited)
            missing.Add("假裝置沒有全部自己結束（等不到，已以 PID 結束）");
        if (exitCodes.Count != devices)
            missing.Add($"假裝置的結束碼有 {exitCodes.Count} 個（要 {devices} 個）");
        var bad = exitCodes.Select((code, i) => (code, i)).Where(x => x.code != 0).Select(x => $"第 {x.i + 1} 台 {(x.code is { } c ? c.ToString(System.Globalization.CultureInfo.InvariantCulture) : "沒有結束碼")}").ToList();
        if (bad.Count > 0)
            missing.Add($"假裝置的結束碼不是 0：{string.Join("、", bad)}");
        if (streams > 0 && browsers > 0 && liveSamples == 0)
            missing.Add($"有 {streams} 台串流、{browsers} 個瀏覽器，負載期間卻沒有任何即時延遲的樣本");
        if (persistent > 0 && browsers > 0 && persistentSamples == 0)
            missing.Add($"有 {persistent} 台持久的對話、{browsers} 個瀏覽器，負載期間卻沒有任何持久延遲的樣本");
        notApplied = missing;
        failures = onlyStalled
            ? Array.Empty<string>()
            : new[] { $"慢用戶端：不是只有卡住的 {stalled} 條被伺服器關掉（卡住的沒被關、正常的被關、或伺服器記錄的中斷多於卡住的條數）" };
    }

    /// <summary>
    /// 伺服器取樣的判定（<see cref="Verdict"/>）併上 <see cref="LoadApplied"/>：有 failures 或原本是 fail 就是 fail；否則有 notApplied 或原本是 insufficient 就是 insufficient；
    /// 都沒有才是 pass。原因依序是原本的、notApplied、failures
    /// </summary>
    public static LoadVerdict WithLoadApplied(LoadVerdict verdict, IReadOnlyList<string> notApplied, IReadOnlyList<string> failures)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(notApplied);
        ArgumentNullException.ThrowIfNull(failures);
        var reasons = verdict.Reasons.Concat(notApplied).Concat(failures).ToList();
        var result = verdict.Result == Fail || failures.Count > 0 ? Fail
            : verdict.Result == Insufficient || notApplied.Count > 0 ? Insufficient
            : Pass;
        return new LoadVerdict(result, reasons);
    }

    /// <summary>
    /// 轉送階段有沒有驗到每台的上限（第 21 個被拒）：台數 × 20 超過全域上限時，每台的第 21 個一定先撞上已滿的全域上限、回同一個 503 busy，
    /// 分不出每台的上限（WP4.9 合併前審查）；那時每台上限的證據是 MyriagentHub.Tests 的 <c>MediaRelayTests.第21個同時轉送回503_釋放後再試成功</c>
    /// </summary>
    public static bool PerDeviceCapVerified(int devices, int relayCap) => devices * RelayConcurrentPerDevice <= relayCap;

    public static LoadVerdict Verdict(LoadSummary summary, bool idleRequired)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var reasons = new List<string>();
        if (summary.Samples < MinSamples)
            reasons.Add($"負載期間只有 {summary.Samples} 個取樣（至少 {MinSamples} 個）");
        if (summary.Cpu.Percent is null || summary.Cpu.Coverage < MinCoverage)
            reasons.Add($"CPU 的時間窗只涵蓋量測期間的 {summary.Cpu.Coverage:P0}（至少 {MinCoverage:P0}）");
        if (idleRequired && summary.IdlePrivateMb is null)
            reasons.Add("沒有閒置期間的取樣");
        if (reasons.Count > 0)
            return new LoadVerdict(Insufficient, reasons);

        if (summary.MaxPrivateMb > MaxPrivateMb)
            reasons.Add($"私有記憶體最大 {summary.MaxPrivateMb} MB（上限 {MaxPrivateMb} MB）");
        if (summary.Cpu.Percent > MaxCpuPercent)
            reasons.Add($"平均 CPU {summary.Cpu.Percent}%（上限 {MaxCpuPercent}%）");
        if (summary.IdlePrivateMb >= MaxIdlePrivateMb)
            reasons.Add($"閒置私有記憶體 {summary.IdlePrivateMb} MB（要小於 {MaxIdlePrivateMb} MB）");
        return new LoadVerdict(reasons.Count == 0 ? Pass : Fail, reasons);
    }

    /// <summary>延遲的摘要；沒有樣本時只有 Count＝0</summary>
    public static LatencySummary Latency(IReadOnlyCollection<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var finite = values.Where(double.IsFinite).ToList();
        if (finite.Count == 0)
            return new LatencySummary(0, null, null, null, null, null, null);
        return new LatencySummary(finite.Count, finite.Min(), finite.Max(), Math.Round(finite.Average(), 1),
            Percentile(finite, 50), Percentile(finite, 95), Percentile(finite, 99));
    }

    /// <summary>百分位數（nearest-rank：排序後第 ceil(p/100 × N) 個；同 stats-rules.mjs 的 percentile）；空的丟例外</summary>
    public static double Percentile(IReadOnlyCollection<double> values, double p)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            throw new ArgumentException("沒有樣本", nameof(values));
        if (!(p > 0 && p <= 100))
            throw new ArgumentOutOfRangeException(nameof(p), p, "p 要在 (0, 100]");
        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(p / 100 * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }
}
