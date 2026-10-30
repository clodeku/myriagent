using System.Diagnostics;
using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Hub;

// 對話框的燈號（web-protocol 的 activity；規則在 ActivityRules）：這台的本機對話在某個視窗看得到（visible）時取樣——
// 資料流＝app 與那個對話的 CLI 之間管線的位元組（AgentProcess.PipeBytes）、硬碟＝CLI 程序樹的 I/O 量扣掉那些管線、
// CPU＝CLI 程序樹加上它開出、脫離 CLI 的程式（背景程式、父子關係斷掉的工具，帶這個對話的標記）的處理器使用率（輸入框下方的 CPU 波形；
// 這兩個由 ProcessTreeIo 量，只量執行中、有背景工作或有背景程式的對話）、網路＝整台電腦硬體網卡的收送量（NetworkTotals）。有對話在忙時每 250ms、否則每 500ms（只量網路）；
// 上一次做完才排下一次，取樣在背景執行緒、結果回 UI 執行緒送出。只送給看得到那個對話的視窗，副本與遠端的檢視不送。
// 中樞預設沒有取樣來源（不取樣、不送），只有 AppController 以 AttachActivity 接上真的；測試用假的來源並直接呼叫 ActivityTick。只在 UI 執行緒
internal sealed partial class AgentHub
{
    private IActivitySampler? activitySampler;
    private bool activityPending;
    private readonly Dictionary<string, (string Instance, long Pipe)> activityPipes = new(StringComparer.Ordinal);
    private (ulong Rx, ulong Tx)? activityNet;
    private long? activityAtMs;

    /// <summary>自己排下一次取樣（測試設成 false，改以 ActivityTick 一次一次推，不受計時器干擾）</summary>
    internal bool ActivityAutoSchedule { get; set; } = true;

    /// <summary>有一次取樣排了或還在做（測試等它做完）</summary>
    internal bool ActivityPending => activityPending;

    /// <summary>掛上燈號的取樣來源（建好中樞之後；只掛一次）</summary>
    internal void AttachActivity(IActivitySampler sampler)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        activitySampler = sampler;
        ScheduleActivity();
    }

    /// <summary>看得到的本機對話（不含副本）與看得到它的本機視窗</summary>
    private List<(ConversationState Conversation, List<IAgentView> Views)> ActivityTargets()
    {
        var list = new List<(ConversationState, List<IAgentView>)>();
        foreach (var c in conversations.Values)
        {
            if (c.IsReplica || c.VisibleViewIds.Count == 0)
                continue;
            var targets = c.VisibleViewIds.Select(id => views.TryGetValue(id, out var v) ? v : null)
                .Where(v => v != null && v is not RemoteResponseView).Select(v => v!).ToList();
            if (targets.Count > 0)
                list.Add((c, targets));
        }
        return list;
    }

    private static bool ActivityBusy(ConversationState c) => c.Session is { } s && (s.IsRunning || s.HasLiveBackgroundWork);

    /// <summary>
    /// 要量程序樹的對話：忙碌中（執行中、CLI 有背景工作），或有它開出、脫離 CLI 還在跑的程式（背景程式的掃描找到的；這一輪結束了也照量——
    /// 2026-10-26 使用者要求：代理開的背景工作、子代理這些程序都要加總算進這個代理的 CPU）
    /// </summary>
    private bool ActivityMeasured(ConversationState c) => ActivityBusy(c) || backgroundScan.Programs.ContainsKey(c.Meta.Id);

    /// <summary>排下一次取樣（沒有來源、已經排了、沒有看得到的本機對話時不排）</summary>
    private void ScheduleActivity()
    {
        if (activitySampler == null || activityPending || disposed || !ActivityAutoSchedule)
            return;
        var targets = ActivityTargets();
        if (targets.Count == 0)
        {
            activityAtMs = null; // 下次重新開始：第一次的間隔照排程算，不拿很久以前的時間
            return;
        }
        activityPending = true;
        var interval = ActivityRules.IntervalMs(targets.Any(t => ActivityMeasured(t.Conversation)));
        _ = Task.Delay(interval).ContinueWith(_ => host.BeginInvoke(ActivityTick), TaskScheduler.Default);
    }

    /// <summary>取一次樣（UI 執行緒收集要量的對話，背景執行緒量，結果回 UI 執行緒送出）</summary>
    internal void ActivityTick()
    {
        var sampler = activitySampler;
        if (sampler == null || disposed)
        {
            activityPending = false;
            return;
        }
        var targets = ActivityTargets();
        if (targets.Count == 0)
        {
            activityPending = false;
            activityAtMs = null;
            return;
        }
        activityPending = true;
        var pipes = new Dictionary<string, AgentActivitySource>(StringComparer.Ordinal);
        var roots = new List<ActivityRoot>();
        foreach (var (c, _) in targets)
        {
            var source = c.Session?.ActivitySource;
            if (source is { } s)
                pipes[c.Meta.Id] = s;
            // CLI 程序沒在跑（例如 app 重開後還沒送過訊息）但它開的程式還在跑時，只量那些程式（PID 0）
            if (ActivityMeasured(c))
                roots.Add(new ActivityRoot(c.Meta.Id, source?.ProcessId ?? 0, source?.Instance ?? string.Empty));
        }
        _ = Task.Run(() => (Trees: sampler.SampleTrees(roots), Net: sampler.ReadNetwork()))
            .ContinueWith(t => host.BeginInvoke(() => OnActivitySampled(t, targets, pipes)), TaskScheduler.Default);
    }

    private void OnActivitySampled(
        Task<(IReadOnlyDictionary<string, TreeActivity> Trees, (ulong Rx, ulong Tx)? Net)> sampled,
        List<(ConversationState Conversation, List<IAgentView> Views)> targets,
        Dictionary<string, AgentActivitySource> pipes)
    {
        activityPending = false;
        if (disposed)
            return;
        if (sampled.IsFaulted)
        {
            Log.Warn("對話框燈號取樣失敗：" + sampled.Exception?.GetBaseException().Message);
            ScheduleActivity();
            return;
        }
        var (trees, net) = sampled.Result;
        var nowMs = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        var ms = activityAtMs is { } at ? (int)Math.Clamp(nowMs - at, 1, 10_000) : ActivityRules.BusyIntervalMs;
        activityAtMs = nowMs;

        // 資料流：同一個程序實例比上次多的管線位元組（換程序、剛開始量時只取基準）；硬碟：程序樹的增量扣掉這段管線；CPU：程序樹的使用率（只取了基準時 null）
        var deltas = new Dictionary<string, (long Data, long Disk, double? Cpu, long? Mem)>(StringComparer.Ordinal);
        foreach (var (id, source) in pipes)
        {
            long? before = activityPipes.TryGetValue(id, out var last) && last.Instance == source.Instance ? last.Pipe : null;
            var data = ActivityRules.CounterDelta(before, source.PipeBytes);
            activityPipes[id] = (source.Instance, source.PipeBytes);
            var tree = trees.TryGetValue(id, out var t) ? t : default;
            deltas[id] = (data, tree.Io > 0 ? ActivityRules.DiskDelta(tree.Io, data) : 0, tree.Cpu, tree.Mem);
        }
        foreach (var (id, tree) in trees)
        {
            if (!pipes.ContainsKey(id))
                deltas[id] = (0, 0, tree.Cpu, tree.Mem); // 沒有 CLI 程序：只有它開的程式的 CPU 與記憶體
        }
        foreach (var id in activityPipes.Keys.Where(k => !pipes.ContainsKey(k)).ToList())
            activityPipes.Remove(id);

        (long Rx, long Tx)? netDelta = null;
        if (net is { } n)
        {
            if (activityNet is { } prev)
                netDelta = (ActivityRules.CounterDelta(prev.Rx, n.Rx), ActivityRules.CounterDelta(prev.Tx, n.Tx));
            activityNet = n;
        }

        // 每個視窗只放它看得到的對話
        var byView = new Dictionary<string, (IAgentView View, List<(string Id, long Data, long Disk, double? Cpu, long? Mem)> Items)>(StringComparer.Ordinal);
        foreach (var (c, list) in targets)
        {
            var d = deltas.TryGetValue(c.Meta.Id, out var x) ? x : (0, 0, null, null);
            foreach (var view in list)
            {
                if (!byView.TryGetValue(view.Id, out var entry))
                    byView[view.Id] = entry = (view, new List<(string, long, long, double?, long?)>());
                entry.Items.Add((c.Meta.Id, d.Data, d.Disk, d.Cpu, d.Mem));
            }
        }
        foreach (var (view, items) in byView.Values)
        {
            if (ActivityRules.Render(items, netDelta, ms) is { } message)
                view.PostToWeb(message);
        }
        ScheduleActivity();
    }
}
