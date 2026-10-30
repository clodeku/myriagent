using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 對話框的燈號（web-protocol 的 activity；AgentHub.Activity.cs）的純規則：取樣間隔、各計數器的增量、硬碟扣掉管線、CPU 使用率、訊息的形狀。
/// 資料流＝app 與這個對話的 CLI 之間管線的位元組；硬碟＝CLI 程序樹的 I/O 量扣掉那些管線；CPU＝CLI 程序樹用掉的處理器時間；網路＝整台電腦硬體網卡的收送量
/// </summary>
internal static class ActivityRules
{
    /// <summary>有對話在執行（量資料流與硬碟）時的取樣間隔</summary>
    public const int BusyIntervalMs = 250;

    /// <summary>只量整台電腦的網路時的取樣間隔</summary>
    public const int IdleIntervalMs = 500;

    public static int IntervalMs(bool anyBusy) => anyBusy ? BusyIntervalMs : IdleIntervalMs;

    /// <summary>
    /// 程序表（找 CLI 的子孫）多久重列一次：列一次整台電腦的程序表很貴（2026-10-24 實測 877 個程序要 45 ms，以前每 250 ms 列一次＝主程式每秒約 180 ms），
    /// 中間的取樣沿用上次的表、只對已知的子孫讀 I/O 計數器。兩次重列之間才開出的工具晚一點才看到，它啟動以來的量照樣整個算進來（<see cref="TreeDelta"/>）
    /// </summary>
    public const int ProcessTableRefreshMs = 3000;

    /// <summary>這次取樣要不要重列程序表：還沒有表、出現新的根程序（剛開始執行的對話、CLI 重開）、或距上次重列滿 <see cref="ProcessTableRefreshMs"/></summary>
    public static bool RefreshProcessTable(bool hasTable, long lastMs, long nowMs, bool newRoot)
        => !hasTable || newRoot || nowMs - lastMs >= ProcessTableRefreshMs;

    /// <summary>
    /// 程序樹的 I/O 增量：這次的每個程序（PID＋啟動時間）比上次多的位元組加總。上次沒有的程序在已經取過基準（primed）時整個算進來
    /// （兩次取樣之間才啟動的工具，量都發生在這段時間），還沒取過基準時一律 0（剛開始量時不把程序啟動以來的量當成一陣暴衝）。計數器變小（不該發生）當 0
    /// </summary>
    public static long TreeDelta(IReadOnlyDictionary<ProcessStamp, ulong> previous, IReadOnlyDictionary<ProcessStamp, ulong> current, bool primed)
    {
        if (!primed)
            return 0;
        ulong sum = 0;
        foreach (var (stamp, value) in current)
        {
            if (previous.TryGetValue(stamp, out var before))
                sum += value >= before ? value - before : 0;
            else
                sum += value;
        }
        return sum > long.MaxValue ? long.MaxValue : (long)sum;
    }

    /// <summary>
    /// 程序樹用掉的 CPU 時間（核心＋使用者，100 奈秒為單位）的增量（輸入框下方的 CPU 波形，2026-10-26 使用者要求）：同一個程序（PID＋啟動時間）算差；
    /// 上次沒有的程序在已經取過基準時，這段時間（previousAt 到 now）才啟動的整個算進來，更早就啟動的依它活著的時間裡這段時間佔的比例攤——
    /// 程序表每 <see cref="ProcessTableRefreshMs"/> 才重列，晚看到的工具（例如跑了 3 秒的建置）啟動以來用的 CPU 整個算進這 250 ms 會冒出一根假的尖峰。
    /// 還沒取過基準時 0，計數器變小（不該發生）當 0
    /// </summary>
    public static long TreeCpuDelta(IReadOnlyDictionary<ProcessStamp, ulong> previous, IReadOnlyDictionary<ProcessStamp, ulong> current, bool primed,
        long previousAtUtcTicks, long nowUtcTicks)
    {
        if (!primed)
            return 0;
        var window = Math.Max(0, nowUtcTicks - previousAtUtcTicks);
        double sum = 0;
        foreach (var (stamp, value) in current)
        {
            if (previous.TryGetValue(stamp, out var before))
                sum += value >= before ? value - before : 0;
            else if (stamp.StartTimeUtcTicks >= previousAtUtcTicks)
                sum += value;
            else
            {
                var lived = nowUtcTicks - stamp.StartTimeUtcTicks;
                sum += lived > 0 ? value * Math.Min(1.0, (double)window / lived) : 0;
            }
        }
        return sum >= long.MaxValue ? long.MaxValue : (long)sum;
    }

    /// <summary>
    /// CPU 使用率：這段時間用掉的 CPU 時間除以經過時間乘邏輯處理器數（同工作管理員「處理程序」頁：100% 是所有核心都滿載），
    /// 0～100、取到小數一位；經過時間或處理器數不合理時 0
    /// </summary>
    public static double CpuPercent(long cpuTicks, long elapsedTicks, int processors)
        => elapsedTicks <= 0 || processors <= 0 || cpuTicks <= 0
            ? 0
            : Math.Round(Math.Clamp(cpuTicks * 100.0 / (elapsedTicks * (double)processors), 0, 100), 1);

    /// <summary>單調遞增計數器的增量：沒有上一個值、或變小（重新開始）時 0</summary>
    public static long CounterDelta(long? previous, long current) => previous is { } p && current >= p ? current - p : 0;

    /// <summary>網路計數器的增量（同 <see cref="CounterDelta(long?, long)"/>，無號）</summary>
    public static long CounterDelta(ulong? previous, ulong current)
        => previous is { } p && current >= p ? (long)Math.Min(current - p, (ulong)long.MaxValue) : 0;

    /// <summary>硬碟＝程序樹的 I/O 增量扣掉 app 與 CLI 之間管線的增量（程序的 I/O 計數器含管線），不小於 0</summary>
    public static long DiskDelta(long treeDelta, long pipeDelta) => Math.Max(0, treeDelta - pipeDelta);

    /// <summary>
    /// 一個視窗的 activity 訊息：只放有動靜的對話（資料流或硬碟大於 0）與量了程序樹的對話（cpu 或 mem 不是 null：執行中、有背景工作或背景程式，
    /// 0 也放——波形要畫平的；mem 是記憶體的位元組，取基準那次就有），網路是整台電腦的（收送都是 0 時不放）；ms 是這次取樣涵蓋的時間（網頁換算成每秒）。
    /// 什麼都沒有時回 null（不送）
    /// </summary>
    public static JObject? Render(IEnumerable<(string Id, long Data, long Disk, double? Cpu, long? Mem)> conversations, (long Rx, long Tx)? net, int ms)
    {
        var items = conversations.Where(c => c.Data > 0 || c.Disk > 0 || c.Cpu is not null || c.Mem is not null).ToList();
        var hasNet = net is { } n && (n.Rx > 0 || n.Tx > 0);
        if (items.Count == 0 && !hasNet)
            return null;
        return new JObject
        {
            ["type"] = "activity",
            ["ms"] = Math.Max(1, ms),
            ["conversations"] = new JArray(items.Select(c =>
            {
                var o = new JObject { ["id"] = c.Id, ["data"] = c.Data, ["disk"] = c.Disk };
                if (c.Cpu is { } cpu)
                    o["cpu"] = cpu;
                if (c.Mem is { } mem)
                    o["mem"] = mem;
                return o;
            })),
            ["net"] = hasNet ? new JObject { ["rx"] = net!.Value.Rx, ["tx"] = net.Value.Tx } : null,
        };
    }
}
