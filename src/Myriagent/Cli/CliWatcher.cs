using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Myriagent.Agents;
using Myriagent.Core;
using Microsoft.Win32.SafeHandles;

namespace Myriagent.Cli;

/// <summary>CliWatcher 的訊號種類</summary>
internal enum CliWatchSignalKind
{
    /// <summary>安裝開始變動（一波變動的第一個相關事件，或檢查時看到進行中的更新）：暫緩這種 CLI 的新啟動</summary>
    Changing,

    /// <summary>安裝已停止變動（去抖、沒有鎖、npm 沒在跑），可以做完整判定（含 --version）並讀身分；每一波變動只發一次</summary>
    Settled,
}

/// <summary>這次訊號是怎麼來的（記錄與介面文字用）</summary>
internal enum CliWatchTrigger
{
    /// <summary>Start 時的第一次檢查（更新協調以 Old: null 發布第一次讀到的身分）</summary>
    Startup,

    /// <summary>檔案事件</summary>
    FileEvent,

    /// <summary>60 秒的備援輪詢發現執行檔或版本變了（或安裝位置變了）</summary>
    Poll,

    /// <summary>監看中斷（Error、資料夾被刪）後重建，或等待中的資料夾出現了：要全量檢查</summary>
    WatcherRebuilt,

    /// <summary>呼叫端以 Reset 通知安裝位置變了</summary>
    Reset,
}

/// <summary>
/// CliWatcher 的訊號（Signal 事件，背景執行緒、同一時間只有一個、依發生順序）。
/// UpdaterProcessId：這一波看到的更新者 PID（Claude 的 .new／.old／versions .tmp 檔名、locks 的 JSON、npm 程序）；
/// 更新協調用 CliInstallStabilityRules.ResolveOrigin 判斷是不是 app 自己。Detail：記錄用的一句說明
/// </summary>
internal sealed record CliWatchSignal(AgentProvider Provider, CliWatchSignalKind Kind, int? UpdaterProcessId, string? Detail)
{
    /// <summary>這次訊號是怎麼來的</summary>
    public CliWatchTrigger Trigger { get; init; }

    /// <summary>這次監看與檢查用的安裝（currentInstallation 回的；null＝找不到安裝）</summary>
    public CliInstallation? Installation { get; init; }

    /// <summary>
    /// Settled：宣告時看到的安裝（不含 --version）。更新協調照 CliInstallStabilityRules.Evaluate(Snapshot, --version 的輸出) 做完整判定，
    /// 也可以重新 CliInstallInspector.Inspect 一次
    /// </summary>
    public CliInstallSnapshot? Snapshot { get; init; }

    /// <summary>發出訊號的時間（UTC）</summary>
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>CliWatcher 的時間與相依（測試縮短時間、換掉檢查程序與環境變數）</summary>
internal sealed record CliWatcherOptions
{
    public TimeSpan ClaudeQuiet { get; init; } = CliInstallStabilityRules.ClaudeQuietPeriod;

    public TimeSpan NpmQuiet { get; init; } = CliInstallStabilityRules.NpmQuietPeriod;

    public TimeSpan UnusableQuiet { get; init; } = CliInstallStabilityRules.UnusableQuietPeriod;

    public TimeSpan Recheck { get; init; } = CliInstallStabilityRules.RecheckInterval;

    public TimeSpan PollInterval { get; init; } = CliInstallStabilityRules.PollInterval;

    public TimeSpan MaxSettleWait { get; init; } = CliInstallStabilityRules.MaxSettleWait;

    public TimeSpan EchoWindow { get; init; } = CliInstallStabilityRules.EchoWindow;

    /// <summary>讀環境變數（HOME、XDG_*）；null＝真的環境變數</summary>
    public Func<string, string?>? Environment { get; init; }

    /// <summary>檢查安裝的程式；null＝以 Environment 建立預設的（真的程序查詢）</summary>
    public CliInstallInspector? Inspector { get; init; }

    /// <summary>false＝不建立 FileSystemWatcher、只靠輪詢（測試備援輪詢用）</summary>
    public bool FileEvents { get; init; } = true;

    /// <summary>要監看的 CLI</summary>
    public IReadOnlyList<AgentProvider> Providers { get; init; } = AgentProviders.All;
}

/// <summary>
/// 監看 CLI 安裝的變化（設計 7.2.2、7.2.3）：對 CliInstallStabilityRules.BuildWatchPlan 的穩定上層資料夾各開一個 FileSystemWatcher
/// （InternalBufferSize 64 KB），相關事件經 ClassifyEvent 過濾後去抖；安靜下來就以 CliInstallInspector 檢查（不啟動程序），
/// 還在變動就繼續等，否則發一次 Settled。另有 60 秒的便宜輪詢（比對執行檔身分與版本來源的指紋），補 watcher 漏掉的事件、
/// 補建等待中的資料夾。收到 Error 就全量檢查：緩衝溢位時 watcher 照常監看（只是漏了事件），其他錯誤（資料夾被刪、存取被拒）先重建
/// （逐次拉長間隔，連續 5 次之後交給輪詢）；資料夾不存在時改監看最近的上層，等它出現。
/// <para>
/// 只負責偵測：Signal 在背景執行緒依序觸發（同一時間只有一個，處理常式不要阻塞），由更新協調（CliManager，WP1.5）轉成
/// CliManager.PublishVersionChange：Changing → Stable=false；Settled → Evaluate(Snapshot, --version) → TryRead → BuildVersionChange。
/// 狀態以自己的鎖保護，任何執行緒都可以呼叫 Start／Reset／Dispose。
/// </para>
/// </summary>
internal sealed class CliWatcher : IDisposable
{
    private const int BufferSize = 64 * 1024;

    /// <summary>連續中斷幾次以內立即重建（之後交給輪詢）</summary>
    private const int MaxErrorRebuilds = 5;

    private static readonly TimeSpan MaxRebuildDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstRebuildDelay = TimeSpan.FromMilliseconds(200);

    private readonly object gate = new();
    private readonly Func<AgentProvider, CliInstallation?> currentInstallation;
    private readonly CliWatcherOptions options;
    private readonly Func<string, string?> environment;
    private readonly CliInstallInspector inspector;
    private readonly Dictionary<AgentProvider, ProviderState> states = new();
    private readonly Queue<CliWatchSignal> pending = new();
    private System.Threading.Timer? pollTimer;
    private int draining;
    private int polling;
    private bool started;
    private bool disposed;

    /// <summary>
    /// currentInstallation：目前這種 CLI 的安裝（例如 CliManager.Get(p).Installation 或 CliLocator.Locate；會在背景執行緒被呼叫，
    /// Start、Reset、每次輪詢各一次）。pollInterval：備援輪詢的間隔（預設 60 秒）
    /// </summary>
    public CliWatcher(Func<AgentProvider, CliInstallation?> currentInstallation, TimeSpan? pollInterval = null)
        : this(currentInstallation, new CliWatcherOptions { PollInterval = pollInterval ?? CliInstallStabilityRules.PollInterval })
    {
    }

    internal CliWatcher(Func<AgentProvider, CliInstallation?> currentInstallation, CliWatcherOptions options)
    {
        this.currentInstallation = currentInstallation;
        this.options = options;
        environment = options.Environment ?? System.Environment.GetEnvironmentVariable;
        inspector = options.Inspector ?? new CliInstallInspector(environment);
        foreach (var provider in options.Providers)
        {
            var state = new ProviderState(provider);
            state.Timer = new System.Threading.Timer(_ => OnTimer(state), null, Timeout.Infinite, Timeout.Infinite);
            states[provider] = state;
        }
    }

    /// <summary>安裝的變化（背景執行緒、依序、同一時間只有一個）</summary>
    public event Action<CliWatchSignal>? Signal;

    /// <summary>開始監看：在背景建立監看並做第一次檢查（每個有安裝的 CLI 發一次 Startup 的 Settled，或檢查時正在變動就發 Changing），之後每 PollInterval 輪詢一次</summary>
    public void Start()
    {
        lock (gate)
        {
            if (disposed || started)
                return;
            started = true;
            if (options.PollInterval > TimeSpan.Zero && options.PollInterval != Timeout.InfiniteTimeSpan)
                pollTimer = new System.Threading.Timer(_ => PollAll(), null, options.PollInterval, options.PollInterval);
        }
        foreach (var state in states.Values)
            _ = Task.Run(() => Refresh(state, CliWatchTrigger.Startup));
    }

    /// <summary>安裝位置變了（呼叫端重新找到 CLI）：重新取得安裝、重建監看並全量檢查</summary>
    public void Reset(AgentProvider provider)
    {
        if (!states.TryGetValue(provider, out var state))
            return;
        lock (gate)
        {
            if (disposed || !started)
                return;
        }
        _ = Task.Run(() => Refresh(state, CliWatchTrigger.Reset));
    }

    /// <summary>目前真的在監看的資料夾（不含等待資料夾出現的上層；測試與診斷用）</summary>
    internal IReadOnlyList<string> GetWatchedDirectories(AgentProvider provider)
    {
        lock (gate)
            return states.TryGetValue(provider, out var s) ? s.Targets.Where(t => t.Watcher != null).Select(t => t.Target.Directory).ToList() : new List<string>();
    }

    /// <summary>資料夾不存在、改監看上層等它出現的（上層資料夾；測試與診斷用）</summary>
    internal IReadOnlyList<string> GetAnchorDirectories(AgentProvider provider)
    {
        lock (gate)
            return states.TryGetValue(provider, out var s) ? s.Targets.Where(t => t.Anchor != null).Select(t => t.AnchorDirectory!).ToList() : new List<string>();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            pending.Clear();
            pollTimer?.Dispose();
            foreach (var state in states.Values)
            {
                state.Timer?.Dispose();
                foreach (var target in state.Targets)
                    DisposeWatchers(target);
                state.Targets.Clear();
            }
        }
    }

    // ───────────────────────────── 安裝與監看的建立 ─────────────────────────────

    /// <summary>重新取得安裝並檢查（Startup、Reset、輪詢發現安裝位置變了）：安裝變了就重建監看；正在一波變動中就併進那一波，否則立即檢查</summary>
    private void Refresh(ProviderState state, CliWatchTrigger trigger)
    {
        CliInstallation? installation;
        try
        {
            installation = currentInstallation(state.Provider);
        }
        catch (Exception ex)
        {
            Log.Warn($"CLI 監看：取得 {state.Provider} 的安裝失敗：{ex.Message}");
            return;
        }

        bool inBurst;
        lock (gate)
        {
            if (disposed)
                return;
            if (!SameInstallation(installation, state.Installation) || state.Targets.Count == 0)
            {
                foreach (var target in state.Targets)
                    DisposeWatchers(target);
                state.Targets.Clear();
                state.Installation = installation;
                foreach (var target in CliInstallStabilityRules.BuildWatchPlan(installation, environment))
                {
                    var t = new TargetState(target);
                    state.Targets.Add(t);
                    Attach(state, t);
                }
            }
            inBurst = state.Burst;
        }

        if (inBurst)
        {
            RecordActivity(state, trigger, null, "重新取得安裝");
            return;
        }
        if (installation == null && trigger == CliWatchTrigger.Startup)
            return;
        CheckNow(state, trigger);
    }

    /// <summary>
    /// 對目標資料夾建立 FileSystemWatcher；資料夾不存在就監看最近的上層、等下一段資料夾出現（呼叫端持有 gate）。回傳是不是監看到目標本身
    /// </summary>
    private bool Attach(ProviderState state, TargetState target)
    {
        if (!options.FileEvents)
            return false;
        var directory = target.Target.Directory;
        try
        {
            // mkdir -p 進行中時，上層的下一段可能在「檢查不存在」與「開始監看上層」之間剛好建好（那個事件就收不到了）：
            // 開始監看上層後再確認一次，已經出現就往下一層重來
            for (int attempt = 0; attempt < 16; attempt++)
            {
                if (Directory.Exists(directory))
                {
                    target.Watcher = CreateWatcher(directory, target.Target.Filter, target.Target.IncludeSubdirectories,
                        NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        e => OnFileEvent(state, target, e), (ex, raising) => OnWatcherError(state, target, ex, raising));
                    return true;
                }

                // 最近的上層與下一段名稱：上層出現了這一段（mkdir、改名）就重建
                var parent = directory.TrimEnd('\\', '/');
                string? segment = null;
                while (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    segment = Path.GetFileName(parent);
                    parent = Path.GetDirectoryName(parent);
                }
                if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(segment))
                    return false;
                var next = segment;
                target.AnchorDirectory = parent;
                target.Anchor = CreateWatcher(parent, next, false, NotifyFilters.DirectoryName,
                    e => OnAnchorEvent(state, target, next, e), (ex, raising) => OnWatcherError(state, target, ex, raising));
                if (!Directory.Exists(Path.Combine(parent, next)))
                    return false;
                DisposeWatchers(target);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Log.Warn($"CLI 監看：無法監看 {directory}：{ex.Message}");
            DisposeWatchers(target);
        }
        return false;
    }

    private static FileSystemWatcher CreateWatcher(string directory, string filter, bool includeSubdirectories, NotifyFilters notifyFilter,
        Action<FileSystemEventArgs> onEvent, Action<Exception?, bool> onError)
    {
        var watcher = new FileSystemWatcher(directory, filter)
        {
            IncludeSubdirectories = includeSubdirectories,
            InternalBufferSize = BufferSize,
            NotifyFilter = notifyFilter,
        };
        watcher.Created += (_, e) => onEvent(e);
        watcher.Changed += (_, e) => onEvent(e);
        watcher.Deleted += (_, e) => onEvent(e);
        watcher.Renamed += (_, e) => onEvent(e);
        // 第二個參數：Error 之後它還在不在監看（真的錯誤時 .NET 先把 EnableRaisingEvents 設成 false 才觸發 Error）
        watcher.Error += (sender, e) => onError(e.GetException(), sender is FileSystemWatcher w && w.EnableRaisingEvents);
        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
        return watcher;
    }

    private static void DisposeWatchers(TargetState target)
    {
        target.Watcher?.Dispose();
        target.Watcher = null;
        target.Anchor?.Dispose();
        target.Anchor = null;
        target.AnchorDirectory = null;
    }

    private static bool SameInstallation(CliInstallation? a, CliInstallation? b)
        => a == null || b == null
            ? a == null && b == null
            : a.Provider == b.Provider && a.Kind == b.Kind
              && string.Equals(a.DisplayPath, b.DisplayPath, StringComparison.OrdinalIgnoreCase)
              && string.Equals(a.NativeExecutable, b.NativeExecutable, StringComparison.OrdinalIgnoreCase)
              && string.Equals(a.Launcher.FileName, b.Launcher.FileName, StringComparison.OrdinalIgnoreCase);

    // ───────────────────────────── 事件 ─────────────────────────────

    private void OnFileEvent(ProviderState state, TargetState target, FileSystemEventArgs e)
    {
        var oldName = (e as RenamedEventArgs)?.OldName;
        if (CliInstallStabilityRules.ClassifyEvent(target.Target, e.ChangeType, e.Name, oldName) is not { } ev)
            return;
        var pid = ev.UpdaterProcessId;
        // 版本鎖的 PID 在 JSON 裡：建立或改寫時讀一下（小檔，FileShare.ReadWrite|Delete）
        if (pid == null && target.Target.Area == CliWatchArea.ClaudeLocks && e.ChangeType != WatcherChangeTypes.Deleted && e.Name != null)
        {
            var json = CliBinaryIdentityRules.ReadSharedText(Path.Combine(target.Target.Directory, e.Name), 64 * 1024);
            pid = CliInstallStabilityRules.ParseLock(e.Name, false, json, DateTime.UtcNow).ProcessId;
        }
        RecordActivity(state, CliWatchTrigger.FileEvent, pid, ev.Description);
    }

    private void OnAnchorEvent(ProviderState state, TargetState target, string segment, FileSystemEventArgs e)
    {
        if (!string.Equals(e.Name, segment, StringComparison.OrdinalIgnoreCase) || e.ChangeType == WatcherChangeTypes.Deleted)
            return;
        _ = Task.Run(() => Rebuild(state, target, $"{Path.Combine(target.AnchorDirectory ?? string.Empty, segment)} 出現了", lostEvents: false));
    }

    private void OnWatcherError(ProviderState state, TargetState target, Exception? error, bool stillRaising)
    {
        if (stillRaising && CliInstallStabilityRules.WatcherSurvivesError(error))
        {
            // 緩衝溢位（npm 一次解壓幾千個檔）：FileSystemWatcher 會繼續監看，只是中間的事件漏了——當成一個事件，安靜下來後全量檢查
            RecordActivity(state, CliWatchTrigger.WatcherRebuilt, null, $"{target.Target.Directory} 的事件太多（緩衝溢位），安靜下來後全量檢查");
            return;
        }

        int attempt;
        lock (gate)
        {
            if (disposed || !state.Targets.Contains(target))
                return;
            attempt = ++target.ConsecutiveErrors;
            // 壞掉的 watcher 先停掉（重建在它自己的回呼之外做）
            DisposeWatchers(target);
        }
        if (attempt > MaxErrorRebuilds)
        {
            // 一直出錯（權限、網路磁碟…）：不再立即重建，交給輪詢每分鐘試一次，免得一直發 Changing／Settled
            Log.Warn($"CLI 監看：{target.Target.Directory} 連續 {attempt} 次中斷，改由定期檢查重試：{error?.Message}");
            return;
        }
        // 連續出錯（資料夾還在刪除中）就逐次拉長間隔
        var delay = TimeSpan.FromTicks(Math.Min(FirstRebuildDelay.Ticks << (attempt - 1), MaxRebuildDelay.Ticks));
        _ = Task.Delay(delay).ContinueWith(
            _ => Rebuild(state, target, $"監看 {target.Target.Directory} 中斷（{error?.Message ?? "原因不明"}）", lostEvents: true),
            TaskScheduler.Default);
    }

    /// <summary>
    /// 重建一個目標的監看（Error、等待中的資料夾出現了、輪詢發現資料夾不見或回來了）。監看到目標本身時，或之前的監看中斷（lostEvents，
    /// 中間可能漏了事件、或資料夾不見了）時，當成一個事件做全量檢查；只是改等更深一層的資料夾就不算
    /// </summary>
    private void Rebuild(ProviderState state, TargetState target, string reason, bool lostEvents)
    {
        bool attached;
        bool gaveUp;
        lock (gate)
        {
            if (disposed || !state.Targets.Contains(target))
                return;
            // 一直出錯、改由輪詢重試的：重新接上不算事件（同一次輪詢的指紋比對會抓到真的變化），免得每分鐘發一次 Changing
            gaveUp = target.ConsecutiveErrors > MaxErrorRebuilds;
            DisposeWatchers(target);
            attached = Attach(state, target);
        }
        if (lostEvents || (attached && !gaveUp))
            RecordActivity(state, CliWatchTrigger.WatcherRebuilt, null, attached ? $"{reason}，已重建監看" : $"{reason}，等資料夾出現");
    }

    /// <summary>
    /// 一個相關事件：第一個發 Changing 並開始這一波，之後的只重新計時（安靜 QuietPeriod 後檢查）。
    /// 上一波宣告穩定後 EchoWindow 內的檔案事件先查安裝（見 EchoCheck），真的在變才開一波
    /// </summary>
    private void RecordActivity(ProviderState state, CliWatchTrigger trigger, int? updaterPid, string reason, bool echoChecked = false)
    {
        lock (gate)
        {
            if (disposed)
                return;
            var now = Stopwatch.GetTimestamp();
            if (!echoChecked && !state.Burst && trigger is (CliWatchTrigger.FileEvent or CliWatchTrigger.WatcherRebuilt) && now < state.EchoUntil)
            {
                // 上一波剛宣告穩定又有事件：多半是晚到的事件（慢機器的事件佇列、緩衝溢位）。先查安裝，真的在變才開一波；
                // 查的期間再有事件就記下來，查完再查一次
                state.EchoEvent ??= (trigger, updaterPid, reason);
                if (state.EchoChecking)
                {
                    state.EchoRecheck = true;
                    return;
                }
                state.EchoChecking = true;
                state.EchoRecheck = false;
                _ = Task.Run(() => EchoCheck(state));
                return;
            }
            state.Generation++;
            state.LastActivityAt = now;
            // 有事件：之後照「最後一個事件後安靜 QuietPeriod」判斷，不需要重查變穩定時的額外確認（見 OnTimer）
            state.LastCheckChanging = false;
            if (!state.Burst)
            {
                state.Burst = true;
                state.BurstTrigger = trigger;
                state.BurstStartedAt = now;
                state.UpdaterPid = updaterPid;
                Enqueue(new CliWatchSignal(state.Provider, CliWatchSignalKind.Changing, updaterPid, reason) { Trigger = trigger, Installation = state.Installation });
            }
            else
            {
                state.UpdaterPid ??= updaterPid;
            }
            Schedule(state, QuietPeriod(state));
        }
        Drain();
    }

    /// <summary>
    /// 宣告穩定後不久的事件：馬上查安裝（不等去抖）。正在變動、或和上次宣告的不同＝真的有新的變化，照一般事件開一波（馬上發 Changing）；
    /// 和上次宣告的一樣＝晚到的事件，不另開一波。查的期間又有事件就再查一次
    /// </summary>
    private void EchoCheck(ProviderState state)
    {
        while (true)
        {
            CliInstallation? installation;
            lock (gate)
            {
                if (disposed)
                    return;
                installation = state.Installation;
                state.EchoRecheck = false;
            }

            var (snapshot, verdict) = Inspect(state.Provider, installation);
            (CliWatchTrigger Trigger, int? UpdaterPid, string Reason) echo;
            lock (gate)
            {
                if (disposed)
                    return;
                if (state.Burst)
                {
                    // 查的期間輪詢或其他路徑已經開了一波：交給它
                    state.EchoChecking = false;
                    state.EchoEvent = null;
                    return;
                }
                var changed = verdict.Condition != CliInstallCondition.Stable || snapshot.Fingerprint != state.Settled;
                if (!changed)
                {
                    if (state.EchoRecheck)
                        continue;
                    state.EchoChecking = false;
                    Log.Info($"CLI 監看：{state.Provider} 宣告穩定後的事件（{state.EchoEvent?.Reason}）沒有改變安裝，不另發一波");
                    state.EchoEvent = null;
                    return;
                }
                state.EchoChecking = false;
                echo = state.EchoEvent ?? (CliWatchTrigger.FileEvent, null, verdict.Detail ?? "宣告穩定後安裝又變了");
                state.EchoEvent = null;
            }
            RecordActivity(state, echo.Trigger, echo.UpdaterPid ?? verdict.UpdaterProcessId, echo.Reason, echoChecked: true);
            return;
        }
    }

    // ───────────────────────────── 檢查 ─────────────────────────────

    /// <summary>立即檢查（Startup、Reset）：正在變動就開始一波（Changing），否則直接 Settled</summary>
    private void CheckNow(ProviderState state, CliWatchTrigger trigger)
    {
        CliInstallation? installation;
        long generation;
        lock (gate)
        {
            if (disposed || state.Burst)
                return;
            installation = state.Installation;
            generation = state.Generation;
        }

        var (snapshot, verdict) = Inspect(state.Provider, installation);
        lock (gate)
        {
            // 檢查期間有事件：交給那一波
            if (disposed || state.Burst || state.Generation != generation)
                return;
            if (verdict.Condition == CliInstallCondition.Changing)
            {
                var now = Stopwatch.GetTimestamp();
                state.Burst = true;
                state.BurstTrigger = trigger;
                state.BurstStartedAt = now;
                state.LastActivityAt = now;
                state.UpdaterPid = verdict.UpdaterProcessId;
                state.LastCheckChanging = true;
                state.ConfirmGeneration = null;
                Enqueue(new CliWatchSignal(state.Provider, CliWatchSignalKind.Changing, verdict.UpdaterProcessId, verdict.Detail)
                {
                    Trigger = trigger,
                    Installation = installation,
                    Snapshot = snapshot,
                });
                Schedule(state, options.Recheck);
            }
            else
            {
                state.Settled = snapshot.Fingerprint;
                state.HasSettled = true;
                Enqueue(new CliWatchSignal(state.Provider, CliWatchSignalKind.Settled, verdict.UpdaterProcessId, verdict.Detail)
                {
                    Trigger = trigger,
                    Installation = installation,
                    Snapshot = snapshot,
                });
            }
        }
        Drain();
    }

    /// <summary>
    /// 去抖計時器：安靜夠久就檢查；還在變動繼續等；重查才看到變穩定的再安靜一段確認；不能用的結論要 30 秒沒有事件才宣告；
    /// 否則發 Settled 結束這一波
    /// </summary>
    private void OnTimer(ProviderState state)
    {
        CliInstallation? installation;
        long generation;
        lock (gate)
        {
            state.TimerArmed = false;
            if (disposed || !state.Burst || state.Checking)
                return;
            var quiet = QuietPeriod(state);
            var sinceActivity = Stopwatch.GetElapsedTime(state.LastActivityAt);
            if (sinceActivity < quiet)
            {
                Schedule(state, quiet - sinceActivity);
                return;
            }
            state.Checking = true;
            installation = state.Installation;
            generation = state.Generation;
        }

        var (snapshot, verdict) = Inspect(state.Provider, installation);
        lock (gate)
        {
            state.Checking = false;
            if (disposed || !state.Burst)
                return;
            var quiet = QuietPeriod(state);
            if (state.Generation != generation)
            {
                // 檢查期間又有事件：重新計時
                Schedule(state, quiet);
                return;
            }
            var sinceActivity = Stopwatch.GetElapsedTime(state.LastActivityAt);
            var overdue = Stopwatch.GetElapsedTime(state.BurstStartedAt) >= options.MaxSettleWait;
            if (!overdue && verdict.Condition == CliInstallCondition.Changing)
            {
                state.UpdaterPid ??= verdict.UpdaterProcessId;
                state.LastCheckChanging = true;
                state.ConfirmGeneration = null;
                Schedule(state, options.Recheck);
                return;
            }
            // 上一次檢查還在變動（版本鎖、npm 程序），之後沒收到事件、這次重查卻穩定了：造成變化的檔案事件可能還在路上
            // （刪版本鎖的事件比每秒一次的重查晚到）。直接宣告的話，晚到的事件會在宣告後再開一波 Changing／Settled。
            // 先再安靜一個去抖時間：期間有事件就照一般流程重新計時，沒有事件、再查仍穩定才宣告
            if (!overdue && state.LastCheckChanging && state.ConfirmGeneration != generation)
            {
                state.ConfirmGeneration = generation;
                Schedule(state, quiet);
                return;
            }
            if (!overdue && verdict.Condition is CliInstallCondition.Broken or CliInstallCondition.Missing && sinceActivity < options.UnusableQuiet)
            {
                Schedule(state, options.UnusableQuiet - sinceActivity);
                return;
            }
            var detail = overdue && verdict.Condition == CliInstallCondition.Changing
                ? $"等了 {options.MaxSettleWait.TotalMinutes:0} 分鐘安裝仍在變動：{verdict.Detail}"
                : verdict.Detail;
            state.Burst = false;
            state.LastCheckChanging = false;
            state.ConfirmGeneration = null;
            state.Settled = snapshot.Fingerprint;
            state.HasSettled = true;
            // 這一波的事件可能還有晚到的：EchoWindow 內的事件先查安裝再決定要不要開新的一波
            state.EchoUntil = Stopwatch.GetTimestamp() + (long)(options.EchoWindow.TotalSeconds * Stopwatch.Frequency);
            Enqueue(new CliWatchSignal(state.Provider, CliWatchSignalKind.Settled, state.UpdaterPid ?? verdict.UpdaterProcessId, detail)
            {
                Trigger = state.BurstTrigger,
                Installation = installation,
                Snapshot = snapshot,
            });
        }
        Drain();
    }

    private (CliInstallSnapshot Snapshot, CliInstallVerdict Verdict) Inspect(AgentProvider provider, CliInstallation? installation)
    {
        var now = DateTime.UtcNow;
        try
        {
            var snapshot = inspector.Inspect(provider, installation, now);
            return (snapshot, CliInstallStabilityRules.Evaluate(snapshot, null));
        }
        catch (Exception ex)
        {
            // 檢查本身出錯（不該發生：讀檔錯誤在裡面都已處理）：當成還在變動，稍後再查
            Log.Warn($"CLI 監看：檢查 {provider} 的安裝失敗：{ex.Message}");
            var snapshot = installation == null
                ? CliInstallSnapshot.NotInstalled(provider, now)
                : new CliInstallSnapshot(provider, installation.Kind, installation.NativeExecutable, now);
            return (snapshot, new CliInstallVerdict(CliInstallCondition.Changing, null, $"檢查安裝失敗：{ex.Message}"));
        }
    }

    private TimeSpan QuietPeriod(ProviderState state)
        => state.Installation?.Kind == CliInstallKind.Npm ? options.NpmQuiet : options.ClaudeQuiet;

    /// <summary>排定 delay 後檢查（呼叫端持有 gate）：已經排定更早的就不動（計時器觸發時會依最後一個事件的時間重新排）</summary>
    private static void Schedule(ProviderState state, TimeSpan delay)
    {
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;
        var due = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        if (state.TimerArmed && state.TimerDueAt <= due)
            return;
        state.TimerArmed = true;
        state.TimerDueAt = due;
        try
        {
            state.Timer?.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ───────────────────────────── 備援輪詢 ─────────────────────────────

    private void PollAll()
    {
        if (Interlocked.Exchange(ref polling, 1) != 0)
            return;
        try
        {
            foreach (var state in states.Values)
                Poll(state);
        }
        catch (Exception ex)
        {
            Log.Warn($"CLI 監看：輪詢失敗：{ex.Message}");
        }
        finally
        {
            Volatile.Write(ref polling, 0);
        }
    }

    /// <summary>
    /// 便宜的輪詢（不啟動程序）：安裝位置變了 → 重新取得；資料夾消失或回來了 → 重建；
    /// 沒有在變動、指紋（執行檔身分＋版本來源）和上次宣告的不同 → 當成一個事件（漏掉的變化）
    /// </summary>
    private void Poll(ProviderState state)
    {
        CliInstallation? installation;
        try
        {
            installation = currentInstallation(state.Provider);
        }
        catch (Exception ex)
        {
            Log.Warn($"CLI 監看：取得 {state.Provider} 的安裝失敗：{ex.Message}");
            return;
        }

        bool installationChanged;
        var rebuild = new List<(TargetState Target, bool LostEvents)>();
        lock (gate)
        {
            if (disposed)
                return;
            installationChanged = !SameInstallation(installation, state.Installation);
            if (!installationChanged && options.FileEvents)
            {
                foreach (var target in state.Targets)
                {
                    if (target.Watcher != null && !Directory.Exists(target.Target.Directory))
                        rebuild.Add((target, true));     // 資料夾不見了，watcher 卻沒有丟 Error
                    else if (target.Watcher == null && (target.Anchor == null || Directory.Exists(target.Target.Directory)))
                        rebuild.Add((target, false));    // 之前建不起來、或等待中的資料夾已經出現（漏了上層的事件）
                    else if (target.Watcher != null)
                        target.ConsecutiveErrors = 0;    // 撐過一輪輪詢：之後的中斷重新計次
                }
            }
        }
        if (installationChanged)
        {
            Refresh(state, CliWatchTrigger.Poll);
            return;
        }
        foreach (var (target, lostEvents) in rebuild)
            Rebuild(state, target, $"定期檢查發現 {target.Target.Directory} 的監看要重建", lostEvents);

        bool skip;
        lock (gate)
            skip = disposed || state.Burst || !state.HasSettled || state.Checking;
        if (skip)
            return;
        var fingerprint = inspector.ReadFingerprint(installation);
        lock (gate)
        {
            if (disposed || state.Burst || !state.HasSettled || fingerprint == state.Settled)
                return;
        }
        RecordActivity(state, CliWatchTrigger.Poll, null, "定期檢查發現執行檔或版本來源變了");
    }

    // ───────────────────────────── 訊號 ─────────────────────────────

    private void Enqueue(CliWatchSignal signal)
    {
        pending.Enqueue(signal);
        Log.Info($"CLI 監看：{signal.Provider} {(signal.Kind == CliWatchSignalKind.Changing ? "安裝變動" : "安裝已穩定")}（{signal.Trigger}）{signal.Detail}"
                 + (signal.UpdaterProcessId is int pid ? $"，更新者 PID {pid}" : string.Empty));
    }

    /// <summary>依序送出排隊的訊號：同一時間只有一個執行緒在送，處理常式不會被同時呼叫</summary>
    private void Drain()
    {
        while (true)
        {
            if (Interlocked.CompareExchange(ref draining, 1, 0) != 0)
                return;
            try
            {
                while (true)
                {
                    CliWatchSignal next;
                    lock (gate)
                    {
                        if (disposed || pending.Count == 0)
                            break;
                        next = pending.Dequeue();
                    }
                    try
                    {
                        Signal?.Invoke(next);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"CLI 監看：處理訊號失敗：{ex.Message}");
                    }
                }
            }
            finally
            {
                Volatile.Write(ref draining, 0);
            }
            lock (gate)
            {
                if (disposed || pending.Count == 0)
                    return;
            }
        }
    }

    private sealed class ProviderState
    {
        public ProviderState(AgentProvider provider) => Provider = provider;

        public AgentProvider Provider { get; }

        public CliInstallation? Installation { get; set; }

        public List<TargetState> Targets { get; } = new();

        public System.Threading.Timer? Timer { get; set; }

        /// <summary>一波變動進行中（已發 Changing、還沒發 Settled）</summary>
        public bool Burst { get; set; }

        public CliWatchTrigger BurstTrigger { get; set; }

        public long BurstStartedAt { get; set; }

        public long LastActivityAt { get; set; }

        /// <summary>每個相關事件加一：檢查期間有沒有新事件</summary>
        public long Generation { get; set; }

        public int? UpdaterPid { get; set; }

        public bool TimerArmed { get; set; }

        public long TimerDueAt { get; set; }

        public bool Checking { get; set; }

        /// <summary>這一波最近一次的結論來自「檢查看到還在變動」、之後還沒收到事件（重查變穩定時要再安靜確認，見 OnTimer）</summary>
        public bool LastCheckChanging { get; set; }

        /// <summary>正在確認「重查變穩定」：開始確認時的事件代數；期間沒有新事件、再查仍穩定才宣告</summary>
        public long? ConfirmGeneration { get; set; }

        /// <summary>上一波宣告穩定後，這個時間（Stopwatch）之前的檔案事件先查安裝再決定（0＝沒有）</summary>
        public long EchoUntil { get; set; }

        /// <summary>正在查「宣告穩定後的事件」；EchoRecheck＝查的期間又有事件，查完要再查一次</summary>
        public bool EchoChecking { get; set; }

        public bool EchoRecheck { get; set; }

        /// <summary>觸發查安裝的第一個事件（真的變了時用它開一波）</summary>
        public (CliWatchTrigger Trigger, int? UpdaterPid, string Reason)? EchoEvent { get; set; }

        /// <summary>上次宣告時的指紋（輪詢比對用）</summary>
        public CliInstallFingerprint Settled { get; set; }

        public bool HasSettled { get; set; }
    }

    private sealed class TargetState
    {
        public TargetState(CliWatchTarget target) => Target = target;

        public CliWatchTarget Target { get; }

        public FileSystemWatcher? Watcher { get; set; }

        /// <summary>目標資料夾不存在時，監看最近的上層等它出現</summary>
        public FileSystemWatcher? Anchor { get; set; }

        public string? AnchorDirectory { get; set; }

        public int ConsecutiveErrors { get; set; }
    }
}

/// <summary>
/// 讀安裝現況（不啟動程序；CliWatcher 安靜下來後、更新協調做完整判定前）：原生執行檔的身分欄位與版本資訊、npm 套件的 package.json、
/// Claude 的換檔暫存檔與版本鎖（含持有者是否還在）、在跑的 npm。所有檔案都以 FileShare.ReadWrite|Delete 讀，不擋 CLI 換檔
/// </summary>
internal sealed class CliInstallInspector
{
    private readonly Func<string, string?> environment;
    private readonly Func<int, CliProcessInfo?> processInfo;
    private readonly Func<IReadOnlyList<CliNpmProcess>> npmProcesses;

    /// <summary>真的環境變數與程序查詢</summary>
    public static CliInstallInspector Default { get; } = new(System.Environment.GetEnvironmentVariable);

    public CliInstallInspector(Func<string, string?> environment)
        : this(environment, CliProcessQueries.TryGetProcessInfo, CliProcessQueries.FindNpmProcesses)
    {
    }

    /// <summary>測試用：換掉程序查詢（鎖的持有者、npm 程序）</summary>
    internal CliInstallInspector(Func<string, string?> environment, Func<int, CliProcessInfo?> processInfo, Func<IReadOnlyList<CliNpmProcess>> npmProcesses)
    {
        this.environment = environment;
        this.processInfo = processInfo;
        this.npmProcesses = npmProcesses;
    }

    /// <summary>讀安裝現況；installation 為 null＝沒有安裝</summary>
    public CliInstallSnapshot Inspect(AgentProvider provider, CliInstallation? installation, DateTime nowUtc)
    {
        if (installation == null)
            return CliInstallSnapshot.NotInstalled(provider, nowUtc);
        var exe = installation.NativeExecutable ?? installation.Launcher.FileName;
        var snapshot = new CliInstallSnapshot(provider, installation.Kind, exe, nowUtc) { Executable = CliBinaryIdentityRules.TryReadStamp(exe) };
        var plan = CliInstallStabilityRules.BuildWatchPlan(installation, environment);
        string? DirectoryOf(CliWatchArea area) => plan.FirstOrDefault(t => t.Area == area)?.Directory;

        switch (installation.Kind)
        {
            case CliInstallKind.Native when provider == AgentProvider.Claude:
            {
                var exeName = Path.GetFileName(exe);
                snapshot = snapshot with
                {
                    FileVersion = snapshot.ExecutableExists ? CliBinaryIdentityRules.ReadClaudeFileVersion(exe) : null,
                    ExecutableEntries = ListNames(DirectoryOf(CliWatchArea.ClaudeBin), exeName + ".*", directories: false),
                    VersionEntries = ListNames(DirectoryOf(CliWatchArea.ClaudeVersions), "*.tmp.*", directories: false),
                    Locks = ReadLocks(DirectoryOf(CliWatchArea.ClaudeLocks)),
                };
                break;
            }
            case CliInstallKind.Npm when CliLocator.GetNpmPrefix(installation) is { } prefix:
            {
                var packageRoot = CliLocator.NpmPackageRoot(provider, prefix);
                var packageJson = Path.Combine(packageRoot, "package.json");
                var packageName = provider switch
                {
                    AgentProvider.Claude => "claude-code",
                    AgentProvider.Codex => "codex",
                    _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "這種 CLI 沒有 npm 版"),
                };
                snapshot = snapshot with
                {
                    PackageMissing = !File.Exists(packageJson),
                    RetiredPackages = ListNames(Path.GetDirectoryName(packageRoot), "." + packageName + "-*", directories: true),
                    NpmProcesses = npmProcesses(),
                };
                if (provider == AgentProvider.Codex)
                {
                    var (package, vendor) = CliBinaryIdentityRules.ReadCodexPackageVersions(packageRoot);
                    snapshot = snapshot with { PackageVersion = package, VendorVersion = vendor };
                }
                else
                {
                    snapshot = snapshot with
                    {
                        PackageVersion = CliBinaryIdentityRules.ReadPackageVersion(packageJson),
                        FileVersion = snapshot.ExecutableExists && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            ? CliBinaryIdentityRules.ReadClaudeFileVersion(exe)
                            : null,
                    };
                }
                break;
            }
            default:
                if (provider == AgentProvider.Claude && snapshot.ExecutableExists)
                    snapshot = snapshot with { FileVersion = CliBinaryIdentityRules.ReadClaudeFileVersion(exe) };
                break;
        }
        return snapshot;
    }

    /// <summary>輪詢用的便宜指紋（同 Inspect 的 Fingerprint，但不列資料夾、不查程序）</summary>
    public CliInstallFingerprint ReadFingerprint(CliInstallation? installation)
    {
        if (installation == null)
            return default;
        var exe = installation.NativeExecutable ?? installation.Launcher.FileName;
        var stamp = CliBinaryIdentityRules.TryReadStamp(exe);
        if (installation.Kind == CliInstallKind.Npm && CliLocator.GetNpmPrefix(installation) is { } prefix)
        {
            var packageRoot = CliLocator.NpmPackageRoot(installation.Provider, prefix);
            if (installation.Provider == AgentProvider.Codex)
            {
                var (package, vendor) = CliBinaryIdentityRules.ReadCodexPackageVersions(packageRoot);
                return new CliInstallFingerprint(stamp, null, package, vendor);
            }
            var fileVersion = stamp != null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? CliBinaryIdentityRules.ReadClaudeFileVersion(exe) : null;
            return new CliInstallFingerprint(stamp, fileVersion, CliBinaryIdentityRules.ReadPackageVersion(Path.Combine(packageRoot, "package.json")), null);
        }
        var version = installation.Provider == AgentProvider.Claude && stamp != null ? CliBinaryIdentityRules.ReadClaudeFileVersion(exe) : null;
        return new CliInstallFingerprint(stamp, version, null, null);
    }

    private static IReadOnlyList<string> ListNames(string? directory, string pattern, bool directories)
    {
        if (string.IsNullOrEmpty(directory))
            return Array.Empty<string>();
        try
        {
            var entries = directories ? Directory.EnumerateDirectories(directory, pattern) : Directory.EnumerateFiles(directory, pattern);
            return entries.Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }

    private IReadOnlyList<ClaudeLockObservation> ReadLocks(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
            return Array.Empty<ClaudeLockObservation>();
        var result = new List<ClaudeLockObservation>();
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*.lock"))
            {
                var name = Path.GetFileName(path);
                bool isDirectory = Directory.Exists(path);
                DateTime written;
                try
                {
                    written = isDirectory ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                var json = isDirectory ? null : CliBinaryIdentityRules.ReadSharedText(path, 64 * 1024);
                var installLock = CliInstallStabilityRules.ParseLock(name, isDirectory, json, written);
                result.Add(new ClaudeLockObservation(installLock, installLock.ProcessId is int pid ? processInfo(pid) : null));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return result;
    }
}

/// <summary>
/// 查其他程序（不用 WMI：大量 Win32_Process 查詢曾把 WmiPrvSE 打到 400% CPU）：Process.GetProcesses（NtQuerySystemInformation）、
/// QueryFullProcessImageName、NtQueryInformationProcess(ProcessCommandLineInformation)。只在 CliWatcher 安靜後的檢查時呼叫
/// </summary>
internal static class CliProcessQueries
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int MaxCommandLineBytes = 1 << 20;

    /// <summary>程序的建立時間與執行檔路徑；已結束、不存在、打不開回 null</summary>
    public static CliProcessInfo? TryGetProcessInfo(int processId)
    {
        if (processId <= 0)
            return null;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return null;
            return new CliProcessInfo(processId, process.StartTime.ToUniversalTime(), ProcessImage.TryGetPath(processId));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>命令列含 npm-cli.js 的 node 程序（npm.cmd、npm.ps1 最後都是「node … npm-cli.js 參數」）</summary>
    public static IReadOnlyList<CliNpmProcess> FindNpmProcesses()
    {
        var result = new List<CliNpmProcess>();
        foreach (var process in Process.GetProcessesByName("node"))
        {
            using (process)
            {
                var commandLine = TryGetCommandLine(process.Id);
                if (commandLine != null && commandLine.Contains("npm-cli.js", StringComparison.OrdinalIgnoreCase))
                    result.Add(new CliNpmProcess(process.Id, commandLine));
            }
        }
        return result;
    }

    /// <summary>程序的命令列（NtQueryInformationProcess 的 ProcessCommandLineInformation，Windows 8.1 起；只要 PROCESS_QUERY_LIMITED_INFORMATION）</summary>
    internal static string? TryGetCommandLine(int processId)
    {
        using var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle.IsInvalid)
            return null;
        NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);
        if (length <= 0 || length > MaxCommandLineBytes)
            return null;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _) < 0)
                return null;
            var text = Marshal.PtrToStructure<UNICODE_STRING>(buffer);
            return text.Buffer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle processHandle, int processInformationClass, IntPtr processInformation, int processInformationLength, out int returnLength);
}
