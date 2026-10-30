using System.Globalization;
using Myriagent.Agents;
using Myriagent.Cli;

namespace Myriagent.UI;

/// <summary>
/// 實機探針的模式（ProbeRunner）。「不找 CLI」（<c>MYRIAGENT_AGENT_PROBE</c> 有設、而且 <c>MYRIAGENT_AGENT_PROBE_NO_CLI=1</c>）給同步的實機量測
/// （WP3.3 第 16 步的 S5、S6）用：CLI 管理員不找這台電腦的 claude／codex／agy／grok（與 npm）、不跑背景檢查、不查最新版、執行指令一律失敗，
/// 帳戶一律「未安裝」（也就不查用量）；探針略過 CLI 偵測與送訊息。只量啟動、閒置、記憶體與同步，不執行任何 CLI、不連任何 CLI 的端點
/// </summary>
internal static class ProbeRules
{
    public const string ProbeVariable = "MYRIAGENT_AGENT_PROBE";

    public const string NoCliVariable = "MYRIAGENT_AGENT_PROBE_NO_CLI";

    /// <summary>
    /// 已配對而且連上時，閒置 CPU 連取幾個 10 秒窗（S6）：心跳與 SignalR keepalive 都是 15 秒，30 秒至少涵蓋兩次；
    /// 只取一窗的話取樣窗落在 Live 之後、第一次心跳之前，量不到任何週期性的同步工作
    /// </summary>
    public const int PairedIdleCpuWindows = 3;

    /// <summary>同步安定（<see cref="Myriagent.Sync.SyncSettleRules"/>）要連續維持多久才算（同 fake-device 的回補等待）</summary>
    public static readonly TimeSpan SettleQuiet = TimeSpan.FromSeconds(1);

    /// <summary>等同步安定最多多久；逾時就在報告記 settleTimedOut（S6 的記憶體判定無效）</summary>
    public static readonly TimeSpan SettleTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 取閒置記憶體之前、安定之後再等多久（WP4.9 D1）：步 5 閒置釋放 DLP 樣式的靜置期（20 秒）再加 5 秒。未配對、連不上的也等同樣久，
    /// 已配對與未配對的取樣時間點才可比
    /// </summary>
    public static readonly TimeSpan IdleSettleDelay = TimeSpan.FromSeconds(25);

    /// <summary>閒置取樣之前怎麼等：只有已配對而且連上時才等同步安定；之後一律再等 <see cref="IdleSettleDelay"/>（不看是否配對）</summary>
    /// <param name="paired">已配對（有引擎）</param>
    /// <param name="notOnline">已配對、但等不到連上</param>
    public static ProbeIdleWait IdleWait(bool paired, bool notOnline) => new(paired && !notOnline, IdleSettleDelay);

    /// <summary>
    /// 第二次閒置取樣（<c>memoryIdleLateMb</c>，閒置 CPU 的窗之後）之前多等幾秒（WP4.9 合併前審查 F1；measure_sync 的 <c>--idle-extra-seconds</c>）：
    /// 第一次取樣落在閒置釋放 DLP 樣式的積極回收之後約 5 秒，是已配對那一邊的低點；要看長時間閒置的穩態就多等
    /// </summary>
    public const string IdleExtraVariable = "MYRIAGENT_AGENT_PROBE_IDLE_EXTRA_SECONDS";

    /// <summary>多等的上限（一天）</summary>
    public static readonly TimeSpan IdleExtraMax = TimeSpan.FromDays(1);

    /// <summary>多等的時間：0～86400 的十進位整數（不帶正負號與空白），其他一律 0</summary>
    public static TimeSpan IdleExtraDelay(string? value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds <= IdleExtraMax.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;

    /// <summary>GC 設定裡的 gen0 預算上限（<c>GC.GetConfigurationVariables()</c> 的 <c>GCGen0MaxBudget</c>，位元組）；沒有或不是正整數是 null</summary>
    public static long? Gen0MaxBudgetBytes(IReadOnlyDictionary<string, object> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (!variables.TryGetValue("GCGen0MaxBudget", out var value))
            return null;
        return value switch
        {
            long l when l > 0 => l,
            ulong u when u is > 0 and <= long.MaxValue => (long)u,
            int i when i > 0 => i,
            uint u32 when u32 > 0 => u32,
            string s when long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// 兩次閒置取樣之間（report 的 <c>idleLate</c>）：多等的秒數、實際經過的秒數、配置量與速率（MB／小時）、gen0 預算，
    /// 與照這個速率要幾分鐘配置滿一個 gen0 預算（gen0 的已提交空間最多長回這麼多；速率或預算不明是 null）
    /// </summary>
    /// <param name="extra">多等的時間（<see cref="IdleExtraDelay"/>）</param>
    /// <param name="elapsed">第一次取樣之後到第二次取樣之前實際經過的時間</param>
    /// <param name="allocatedBytes">這段期間的配置（<c>GC.GetTotalAllocatedBytes(false)</c> 的差）</param>
    /// <param name="gen0MaxBudgetBytes"><see cref="Gen0MaxBudgetBytes"/></param>
    public static ProbeIdleLate DescribeIdleLate(TimeSpan extra, TimeSpan elapsed, long allocatedBytes, long? gen0MaxBudgetBytes)
    {
        var allocated = Math.Max(0, allocatedBytes);
        double? perHour = elapsed > TimeSpan.Zero ? Math.Round(allocated / 1048576.0 / elapsed.TotalHours, 1, MidpointRounding.AwayFromZero) : null;
        double? fillMinutes = elapsed > TimeSpan.Zero && allocated > 0 && gen0MaxBudgetBytes is > 0
            ? Math.Round(gen0MaxBudgetBytes.Value / (allocated / elapsed.TotalMinutes), 1, MidpointRounding.AwayFromZero)
            : null;
        return new ProbeIdleLate(
            Math.Round(extra.TotalSeconds, 1, MidpointRounding.AwayFromZero),
            Math.Round(elapsed.TotalSeconds, 1, MidpointRounding.AwayFromZero),
            Mb(allocated),
            perHour,
            gen0MaxBudgetBytes is > 0 ? Mb(gen0MaxBudgetBytes.Value) : null,
            fillMinutes);
    }

    /// <summary>GC 的細項（MB，四捨五入到一位）：受管理堆積、已提交、碎片，與「私有記憶體減已提交」的原生估計（不會是負數）</summary>
    /// <param name="heapBytes">受管理堆積（GC 之後的 <c>GC.GetTotalMemory(false)</c>）</param>
    /// <param name="committedBytes">GC 已提交的位元組（<c>GCMemoryInfo.TotalCommittedBytes</c>）</param>
    /// <param name="fragmentedBytes">碎片（<c>GCMemoryInfo.FragmentedBytes</c>）</param>
    /// <param name="privateBytes">app 程序自己的私有位元組</param>
    public static ProbeGcBreakdown DescribeGc(long heapBytes, long committedBytes, long fragmentedBytes, long privateBytes) => new(
        Mb(heapBytes),
        Mb(committedBytes),
        Mb(fragmentedBytes),
        Mb(Math.Max(0, privateBytes - committedBytes)));

    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1, MidpointRounding.AwayFromZero);

    /// <summary>只在探針模式下才認（一般執行設了也沒用）</summary>
    public static bool NoCli(string? probeDirectory, string? noCli) => !string.IsNullOrWhiteSpace(probeDirectory) && noCli == "1";

    public static bool NoCliRequested() => NoCli(Environment.GetEnvironmentVariable(ProbeVariable), Environment.GetEnvironmentVariable(NoCliVariable));

    /// <summary>「不找 CLI」的 CLI 管理員選項（找安裝一律沒有、執行指令一律失敗、不查最新版）</summary>
    public static CliManagerOptions NoCliOptions() => new()
    {
        Locate = NoInstallation,
        LocateNpm = NoNpm,
        Run = RefuseRun,
        FetchLatestOnStart = false,
    };

    private static CliInstallation? NoInstallation(AgentProvider provider) => null;

    private static string? NoNpm() => null;

    private static Task<CliRunResult> RefuseRun(AgentLauncher launcher, IReadOnlyList<string> arguments, Action<IDictionary<string, string?>>? environment,
        TimeSpan timeout, bool killWithApp, Action<int>? started, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"{NoCliVariable}=1：探針不執行任何 CLI 指令");
}

/// <summary>閒置取樣之前的等待（<see cref="ProbeRules.IdleWait"/>）</summary>
/// <param name="Settle">先等同步安定</param>
/// <param name="Delay">之後再等多久</param>
internal sealed record ProbeIdleWait(bool Settle, TimeSpan Delay);

/// <summary>report.json 記憶體取樣的 gc 細項（MB）</summary>
internal sealed record ProbeGcBreakdown(double HeapMb, double CommittedMb, double FragmentedMb, double NativeEstimateMb);

/// <summary>report.json 的 idleLate：兩次閒置取樣之間（<see cref="ProbeRules.DescribeIdleLate"/>）</summary>
internal sealed record ProbeIdleLate(double ExtraSeconds, double ElapsedSeconds, double AllocatedMb, double? AllocMbPerHour, double? Gen0MaxBudgetMb,
    double? Gen0BudgetFillMinutes);
