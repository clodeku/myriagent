using Myriagent.Core;
using Myriagent.Git;
using Myriagent.Launch;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 執行列（web-protocol 的 launchStatus／launchAction／launchSelect／launchLog／launchRescan）：對話的工作資料夾是可以執行的視窗程式專案
// （LaunchRules：.NET 的 WinExe、Electron）時，網頁在輸入區上方顯示啟動（建置並執行）、暫停、停止、重新建置並執行。
// 偵測以資料夾為單位快取（同一個資料夾的對話共用），開分頁、換資料夾、這一輪結束（代理可能剛建立專案）、畫面上看得到的每分鐘重新偵測；
// 執行狀態以目標（專案檔）為單位：不同資料夾偵測到同一個專案時是同一份（一起顯示執行中）。
// 程序由 ILaunchProcessStarter 開：建置是 app 的子程序（app 結束時結束它）。執行分兩種模式（2026-10-25 使用者要求，每個專案各自記得）：
// Debug 模式以 Debug 組態建置，執行的程式也是 app 的子程序（工作物件設 KILL_ON_JOB_CLOSE），app 關閉、當掉或重新啟動時一起結束；
// Release 模式以 Release 組態建置，執行的程式和 app 脫鉤（2026-10-23 使用者要求），app 結束、當掉或重新啟動都不影響它——
// 開著的記在狀態資料夾的 runs.json（PID＋建立時間、輸出檔），下次啟動接回（照樣顯示執行中、輸出接著看），停止與重新建置並執行時先結束它。
// 每個資料夾選的專案與每個專案的模式記在 prefs.json（使用者明確選過的才記），app 重新啟動後照舊。
// 沒掛（AttachLaunch；測試與 HubHarness 預設）時什麼都不做、也不送 launchStatus；
// 副本（其他電腦的對話）、要開在其他電腦的草稿與遠端指令都不處理——只有這台的視窗能在這台啟動程式。狀態只在 UI 執行緒改，輸出的緩衝自己加鎖
internal sealed partial class AgentHub
{
    private ILaunchScanner? launchScanner;
    private ILaunchProcessStarter? launchStarter;
    private readonly Dictionary<string, LaunchFolderState> launchFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>執行過的目標（鍵是 LaunchTarget.Id）</summary>
    private readonly Dictionary<string, LaunchRun> launchRuns = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource launchCancel = new();

    /// <summary>執行列的狀態資料夾（runs.json 與執行的程式的輸出檔；AttachLaunch 給）</summary>
    private string? launchStateDirectory;

    /// <summary>上次寫進 runs.json 的內容（沒變就不重寫；null＝檔案不存在）</summary>
    private string? launchStateWritten;

    /// <summary>記得的選擇（prefs.json：每個資料夾選的專案、每個專案的執行模式）</summary>
    private LaunchPrefsFile launchPrefs = new();

    /// <summary>現在時間（UTC；測試可換）</summary>
    internal Func<DateTime> LaunchClock { get; set; } = () => DateTime.UtcNow;

    /// <summary>%ComSpec%（Electron 經 cmd 執行 npm）</summary>
    internal string? LaunchComSpec { get; set; } = Environment.GetEnvironmentVariable("ComSpec");

    private sealed class LaunchFolderState
    {
        public IReadOnlyList<LaunchTarget> Targets = [];

        /// <summary>偵測完過至少一次</summary>
        public bool Known;

        public DateTime? ScannedAt;
        public bool Scanning;

        /// <summary>偵測途中又被要求（換了資料夾、這一輪結束、使用者要求）：偵測完再來一次</summary>
        public bool Again;

        /// <summary>選的目標（執行列的下拉選單）</summary>
        public string? Selected;
    }

    private sealed class LaunchRun(LaunchTarget target)
    {
        public LaunchTarget Target = target;
        public LaunchPhase Phase = LaunchPhase.Idle;

        /// <summary>這次開始的時間（建置與執行合起來；閒置是 null）</summary>
        public DateTime? Since;

        public LaunchEnd? Last;
        public readonly LaunchLog Log = new(LaunchRules.LogCapacity);

        /// <summary>現在的程序（建置或執行）</summary>
        public ILaunchProcess? Process;

        /// <summary>這一次的執行模式（開始時照這個專案選的模式定下來，中途換模式下一次才套用；接回的是 Release）</summary>
        public LaunchMode Mode = LaunchRules.DefaultMode;

        /// <summary>使用者按了停止：程序結束時記成「已停止」而不是建置失敗或結束碼</summary>
        public bool StopRequested;

        /// <summary>按了重新建置並執行：程序結束後重新開始</summary>
        public bool RestartRequested;

        /// <summary>有新的輸出、狀態還沒送（1）：讀輸出的執行緒只排一次</summary>
        public int OutputPending;

        /// <summary>現在的程序是 Release 模式執行的程式（和 app 脫鉤、記在 runs.json）；建置中、閒置、Debug 模式是 false</summary>
        public bool Detached;

        /// <summary>執行的程式的輸出檔（狀態資料夾裡的檔名）</summary>
        public string? LogFile;
    }

    /// <summary>
    /// 掛上專案偵測與程序（AppController；只掛一次）。沒掛時不偵測、不送。<paramref name="stateDirectory"/> 放 runs.json、prefs.json 與輸出檔；
    /// 掛上時讀回記得的選擇、接回上次開著、還在執行的程式
    /// </summary>
    internal void AttachLaunch(ILaunchScanner scanner, ILaunchProcessStarter starter, string stateDirectory)
    {
        launchScanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        launchStarter = starter ?? throw new ArgumentNullException(nameof(starter));
        launchStateDirectory = stateDirectory ?? throw new ArgumentNullException(nameof(stateDirectory));
        LoadLaunchPrefs();
        RestoreLaunchRuns();
    }

    /// <summary>
    /// 執行列正在建置（或停止中、可能接著重新建置）：app 自我更新不自動重啟——結束 app 會中斷建置、之後也不會執行。
    /// 執行中、暫停中的程式不算（Debug 模式的另由 <see cref="LaunchDebugging"/> 說明）。UI 執行緒
    /// </summary>
    internal bool LaunchBuilding => launchRuns.Values.Any(r => LaunchRules.BlocksAppRestart(r.Phase));

    /// <summary>
    /// 執行列有 Debug 模式執行中（或暫停中）的程式：它是 app 的子程序，app 重新啟動會一起結束它——app 自我更新不自動重啟（按「立即重啟」照樣開始）。
    /// Release 模式的不算（和 app 脫鉤，重新啟動後接回）。UI 執行緒
    /// </summary>
    internal bool LaunchDebugging => launchRuns.Values.Any(r => LaunchRules.DiesWithApp(r.Phase, r.Mode));

    /// <summary>這個對話要偵測的資料夾（快取的鍵）；不處理的對話回 null</summary>
    private string? LaunchFolderOf(ConversationState c)
    {
        if (launchScanner == null || c.IsReplica || (c.IsDraft && c.PeerDeviceId != null))
            return null;
        return GitStatusRules.FolderKey(GetWorkingDirectory(c));
    }

    /// <summary>
    /// 這個資料夾列出的目標：偵測到的，加上執行中、但這次沒偵測到的（專案檔暫時讀不到、被改壞）——還開著的程式要停得掉
    /// </summary>
    private IReadOnlyList<LaunchTarget> LaunchTargetsOf(string key, LaunchFolderState e)
    {
        var extra = launchRuns.Values
            .Where(r => r.Phase != LaunchPhase.Idle && LaunchRules.IsInside(r.Target.ProjectPath, key) && e.Targets.All(t => t.Id != r.Target.Id))
            .Select(r => r.Target)
            .ToList();
        return extra.Count == 0 ? e.Targets : LaunchRules.Order(e.Targets.Concat(extra));
    }

    private LaunchRunSnapshot? LaunchSnapshotOf(string targetId)
        => launchRuns.TryGetValue(targetId, out var r)
            ? new LaunchRunSnapshot(r.Phase, r.Since, r.Last, r.Log.Seq, r.Log.Epoch, r.Phase == LaunchPhase.Idle ? null : r.Mode)
            : null;

    /// <summary>這個目標選的執行模式（沒選過是 Debug）</summary>
    private LaunchMode LaunchModeOf(string targetId) => LaunchRules.ModeFromPrefs(launchPrefs, targetId);

    /// <summary>送這個對話的執行列（還沒偵測完、沒有目標時 launch 是 null）；only 是 null＝開著它的所有視窗</summary>
    private void PostLaunch(ConversationState c, IAgentView? only = null)
    {
        if (launchScanner == null || c.IsReplica)
            return;
        var targets = (only != null ? new[] { only } : ViewsOf(c)).Where(v => v is not RemoteResponseView).ToList();
        if (targets.Count == 0)
            return;
        var key = LaunchFolderOf(c);
        var entry = key != null && launchFolders.TryGetValue(key, out var e) && e.Known ? e : null;
        TryPost(targets, () => new JObject
        {
            ["type"] = "launchStatus",
            ["conversationId"] = c.Meta.Id,
            ["launch"] = entry != null ? LaunchRules.Render(key!, LaunchTargetsOf(key!, entry), entry.Selected, LaunchSnapshotOf, LaunchModeOf) : null,
        });
    }

    /// <summary>送出整份對話之後（PostConversation）：先送快取的，太舊就重新偵測</summary>
    private void OnLaunchConversationPosted(IAgentView view, ConversationState c)
    {
        if (launchScanner == null || view is RemoteResponseView || c.IsReplica)
            return;
        PostLaunch(c, view);
        RequestLaunchScan(c, LaunchScanTrigger.Open);
    }

    /// <summary>換了工作資料夾：先送新資料夾快取的（沒有就是 null，藏起執行列），再偵測</summary>
    private void OnLaunchFolderChanged(ConversationState c)
    {
        if (launchScanner == null)
            return;
        PostLaunch(c);
        RequestLaunchScan(c, LaunchScanTrigger.User);
    }

    /// <summary>這一輪結束：代理可能剛建立或改了專案</summary>
    private void OnLaunchTurnCompleted(ConversationState c)
    {
        if (launchScanner != null && c.ViewIds.Count > 0)
            RequestLaunchScan(c, LaunchScanTrigger.Changed);
    }

    /// <summary>維護計時器：畫面上看得到的對話（專案可能是在 app 外面建立的；同一個資料夾的只偵測一次）</summary>
    private void TickLaunch()
    {
        if (launchScanner == null)
            return;
        foreach (var c in conversations.Values.Where(c => c.VisibleViewIds.Count > 0).ToList())
            RequestLaunchScan(c, LaunchScanTrigger.Periodic);
        // 執行的程式之後開的子程序（dotnet run 開的程式、Electron 的各個程序）補記進 runs.json：app 當掉時下次也接得回整棵樹
        SaveLaunchRuns();
    }

    private void RequestLaunchScan(ConversationState? c, LaunchScanTrigger trigger)
    {
        if (c == null || disposed || LaunchFolderOf(c) is not { } key)
            return;
        if (!launchFolders.TryGetValue(key, out var e))
            launchFolders[key] = e = new LaunchFolderState { Selected = LaunchRules.SelectedFromPrefs(launchPrefs, key) };
        if (e.Scanning)
        {
            if (trigger is LaunchScanTrigger.Changed or LaunchScanTrigger.User)
                e.Again = true;
            return;
        }
        if (LaunchRules.ShouldScan(trigger, e.ScannedAt, LaunchClock()))
            _ = ScanLaunchAsync(key, e);
    }

    private async Task ScanLaunchAsync(string key, LaunchFolderState e)
    {
        var scanner = launchScanner!;
        var ct = launchCancel.Token;
        e.Scanning = true;
        try
        {
            while (!disposed)
            {
                IReadOnlyList<LaunchTarget>? found = null;
                try
                {
                    found = await Task.Run(() => scanner.ScanAsync(key, ct), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn($"偵測可以執行的專案失敗（{key}）：{ex.Message}");
                }
                if (disposed)
                    return;
                e.ScannedAt = LaunchClock();
                // 失敗時留著上次的；第一次就失敗當成沒有目標（藏起執行列）
                var targets = found ?? e.Targets;
                var changed = !e.Known || !e.Targets.SequenceEqual(targets);
                e.Targets = targets;
                e.Known = true;
                var selected = LaunchRules.Select(LaunchTargetsOf(key, e), e.Selected);
                changed |= selected != e.Selected;
                e.Selected = selected;
                // 閒置的目標換成最新偵測到的內容（多目標的架構、npm 指令碼可能改了）
                foreach (var t in targets)
                {
                    if (launchRuns.TryGetValue(t.Id, out var run) && run.Phase == LaunchPhase.Idle)
                        run.Target = t;
                }
                if (changed)
                    PostLaunchFolder(key);
                if (!e.Again)
                    break;
                e.Again = false;
            }
        }
        finally
        {
            e.Scanning = false;
        }
    }

    /// <summary>送給開著工作資料夾是這個的對話的視窗</summary>
    private void PostLaunchFolder(string key)
    {
        foreach (var c in conversations.Values.Where(c => c.ViewIds.Count > 0).ToList())
        {
            if (string.Equals(LaunchFolderOf(c), key, StringComparison.OrdinalIgnoreCase))
                PostLaunch(c);
        }
    }

    /// <summary>這個目標的狀態變了：送給列出它的每個對話（不同資料夾可能列出同一個專案）</summary>
    private void PostLaunchTarget(string targetId)
    {
        if (disposed)
            return;
        foreach (var c in conversations.Values.Where(c => c.ViewIds.Count > 0).ToList())
        {
            if (LaunchFolderOf(c) is { } key && launchFolders.TryGetValue(key, out var e) && e.Known
                && LaunchTargetsOf(key, e).Any(t => t.Id == targetId))
                PostLaunch(c);
        }
    }

    // ───── 網頁指令 ─────

    /// <summary>
    /// launchAction／launchSelect／launchLog／launchRescan：targetId 只認這個對話的資料夾列出的（網頁不能指定任意路徑）。
    /// 選目標（launchSelect、launchAction）記進 prefs.json；launchSelect 帶 mode 時一併換這個目標的執行模式（下一次啟動才套用）
    /// </summary>
    private void HandleLaunchCommand(IAgentView view, string cmd, ConversationState? c, JObject command)
    {
        if (launchScanner == null || view is RemoteResponseView || c == null || LaunchFolderOf(c) is not { } key)
            return;
        if (cmd == "launchRescan")
        {
            RequestLaunchScan(c, LaunchScanTrigger.User);
            return;
        }
        if (!launchFolders.TryGetValue(key, out var e) || !e.Known)
            return;
        var targetId = command.Value<string>("targetId");
        if (LaunchTargetsOf(key, e).FirstOrDefault(t => t.Id == targetId) is not { } target)
            return;
        switch (cmd)
        {
            case "launchSelect":
                var selectedChanged = e.Selected != target.Id;
                e.Selected = target.Id;
                var prefsChanged = LaunchRules.RememberSelection(launchPrefs, key, target.Id);
                var modeChanged = false;
                if (LaunchRules.ParseMode(command.Value<string>("mode")) is { } mode && LaunchModeOf(target.Id) != mode)
                {
                    modeChanged = LaunchRules.RememberMode(launchPrefs, target.Id, mode);
                    Log.Info($"執行列：{target.ProjectPath} 改成 {LaunchRules.Configuration(mode)} 模式");
                }
                if (prefsChanged || modeChanged)
                    SaveLaunchPrefs();
                // 模式是專案的：列出這個專案的對話都要更新（含這個資料夾的）
                if (modeChanged)
                    PostLaunchTarget(target.Id);
                else if (selectedChanged)
                    PostLaunchFolder(key);
                break;
            case "launchLog":
                var slice = launchRuns.TryGetValue(target.Id, out var run)
                    ? run.Log.Read(command.Value<long?>("epoch") ?? -1, command.Value<long?>("after") ?? 0)
                    : new LaunchLogSlice([], 0, 0, Reset: true);
                Post(view, LaunchRules.RenderLog(c.Meta.Id, target.Id, slice));
                break;
            case "launchAction":
                if (e.Selected != target.Id)
                {
                    e.Selected = target.Id;
                    PostLaunchFolder(key);
                }
                if (LaunchRules.RememberSelection(launchPrefs, key, target.Id))
                    SaveLaunchPrefs();
                LaunchAct(target, command.Value<string>("action"));
                break;
        }
    }

    private void LaunchAct(LaunchTarget target, string? action)
    {
        if (!launchRuns.TryGetValue(target.Id, out var run))
            launchRuns[target.Id] = run = new LaunchRun(target);
        else if (run.Phase == LaunchPhase.Idle)
            run.Target = target;
        switch (LaunchRules.Plan(run.Phase, action))
        {
            case LaunchStep.None:
                return;
            case LaunchStep.Start:
                StartLaunch(run);
                break;
            case LaunchStep.Stop:
                StopLaunch(run, restart: false);
                break;
            case LaunchStep.Restart:
                StopLaunch(run, restart: true);
                break;
            case LaunchStep.Pause:
                if (run.Process?.Suspend() == true)
                {
                    run.Phase = LaunchPhase.Paused;
                    SaveLaunchRuns();
                }
                else
                    Toast("無法暫停這個程式", isError: true);
                break;
            case LaunchStep.Resume:
                if (run.Process?.Resume() == true)
                {
                    run.Phase = LaunchPhase.Running;
                    SaveLaunchRuns();
                }
                else
                    Toast("無法繼續這個程式", isError: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
        PostLaunchTarget(run.Target.Id);
    }

    /// <summary>新的一次：照這個專案現在選的模式；清空輸出，有建置步驟就先建置</summary>
    private void StartLaunch(LaunchRun run)
    {
        run.StopRequested = false;
        run.RestartRequested = false;
        run.Last = null;
        run.Mode = LaunchModeOf(run.Target.Id);
        run.Log.Restart();
        run.Since = LaunchClock();
        Log.Info($"執行列：開始 {run.Target.ProjectPath}（{LaunchRules.Configuration(run.Mode)} 模式）");
        if (LaunchRules.BuildCommand(run.Target, run.Mode) is { } build)
        {
            run.Phase = LaunchPhase.Building;
            if (StartLaunchProcess(run, build, launchStarter!.Build) is { } process)
                _ = WatchLaunchBuildAsync(run, process);
        }
        else
        {
            StartLaunchRun(run);
        }
    }

    /// <summary>
    /// 執行（建置成功之後）：Debug 模式是 app 的子程序（輸出經管線，app 結束時一起結束、不記進 runs.json）；
    /// Release 模式和 app 脫鉤，輸出寫進這次的輸出檔，開起來就記進 runs.json
    /// </summary>
    private void StartLaunchRun(LaunchRun run)
    {
        run.Phase = LaunchPhase.Running;
        run.Since = LaunchClock();
        var command = LaunchRules.RunCommand(run.Target, LaunchComSpec, run.Mode);
        if (run.Mode == LaunchMode.Debug)
        {
            if (StartLaunchProcess(run, command, launchStarter!.RunAttached) is { } attached)
                _ = WatchLaunchRunAsync(run, attached);
            return;
        }
        var logFile = LaunchRules.LogFileName(run.Target.Id, LaunchClock());
        var logPath = Path.Combine(launchStateDirectory!, logFile);
        if (StartLaunchProcess(run, command, (c, onLine) => launchStarter!.Run(c, logPath, onLine)) is { } process)
        {
            run.Detached = true;
            run.LogFile = logFile;
            SaveLaunchRuns();
            _ = WatchLaunchRunAsync(run, process);
        }
    }

    /// <summary>開程序；開不起來記成「無法啟動」並回 null</summary>
    private ILaunchProcess? StartLaunchProcess(LaunchRun run, LaunchCommand command, Func<LaunchCommand, Action<string>, ILaunchProcess> start)
    {
        var epoch = run.Log.Epoch;
        AppendLaunchLine(run, epoch, LaunchRules.DisplayCommand(command));
        try
        {
            var process = start(command, line => AppendLaunchLine(run, epoch, line));
            run.Process = process;
            return process;
        }
        catch (Exception ex)
        {
            Log.Warn($"執行列：無法啟動 {command.FileName}（{run.Target.ProjectPath}）：{ex.Message}");
            AppendLaunchLine(run, epoch, ex.Message);
            run.Process = null;
            FinishLaunch(run, LaunchRules.StartFailed(ex.Message, LaunchClock()));
            return null;
        }
    }

    private async Task WatchLaunchBuildAsync(LaunchRun run, ILaunchProcess process)
    {
        var code = await AwaitLaunchExitAsync(process);
        if (!TakeLaunchProcess(run, process))
            return;
        if (run.StopRequested)
            FinishLaunch(run, LaunchRules.Stopped(LaunchClock()));
        else if (code != 0)
            FinishLaunch(run, LaunchRules.BuildFailed(code, run.Log.Snapshot(), LaunchClock()));
        else
        {
            StartLaunchRun(run);
            PostLaunchTarget(run.Target.Id);
        }
    }

    private async Task WatchLaunchRunAsync(LaunchRun run, ILaunchProcess process)
    {
        var code = await AwaitLaunchExitAsync(process);
        if (!TakeLaunchProcess(run, process))
            return;
        FinishLaunch(run, run.StopRequested ? LaunchRules.Stopped(LaunchClock()) : LaunchRules.Exited(code, LaunchClock()));
    }

    private static async Task<int> AwaitLaunchExitAsync(ILaunchProcess process)
    {
        try
        {
            return await process.Exited;
        }
        catch (Exception ex)
        {
            Log.Warn($"執行列：等程序結束失敗：{ex.Message}");
            return -1;
        }
    }

    /// <summary>程序結束（UI 執行緒）：還是這個目標現在的程序才處理；釋放它</summary>
    private bool TakeLaunchProcess(LaunchRun run, ILaunchProcess process)
    {
        process.Dispose();
        if (disposed || run.Process != process)
            return false;
        run.Process = null;
        return true;
    }

    private void FinishLaunch(LaunchRun run, LaunchEnd end)
    {
        run.Phase = LaunchPhase.Idle;
        run.Since = null;
        run.Last = end;
        run.StopRequested = false;
        Log.Info($"執行列：{run.Target.ProjectPath} {end.Text}");
        if (run.Detached)
        {
            // 程式結束了：不必再記得它；輸出已經在記憶體裡（畫面上照樣看得到），輸出檔刪掉
            run.Detached = false;
            DeleteLaunchLog(run.LogFile);
            run.LogFile = null;
            SaveLaunchRuns();
        }
        if (run.RestartRequested)
        {
            run.RestartRequested = false;
            StartLaunch(run);
        }
        PostLaunchTarget(run.Target.Id);
    }

    /// <summary>
    /// 停止（建置中或執行中，暫停中的也一樣；app 重新啟動前開、接回的程式也是）；restart＝結束後重新建置並執行——
    /// 一律等這個程式真的結束了才開始建置（否則它鎖著輸出的 exe，建置會失敗）
    /// </summary>
    private void StopLaunch(LaunchRun run, bool restart)
    {
        if (restart)
            run.RestartRequested = true;
        if (run.Phase == LaunchPhase.Stopping)
            return;
        run.StopRequested = true;
        run.Phase = LaunchPhase.Stopping;
        run.Process?.Kill();
    }

    /// <summary>加一行輸出（任何執行緒）：停掉的上一次晚到的不加；狀態（logSeq）稍後合併送一次</summary>
    private void AppendLaunchLine(LaunchRun run, long epoch, string line)
    {
        if (!run.Log.AppendIf(epoch, line) || Interlocked.Exchange(ref run.OutputPending, 1) != 0)
            return;
        host.BeginInvoke(() => _ = FlushLaunchOutputAsync(run));
    }

    private async Task FlushLaunchOutputAsync(LaunchRun run)
    {
        try
        {
            await Task.Delay(LaunchRules.OutputPostDelay, launchCancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Volatile.Write(ref run.OutputPending, 0);
        PostLaunchTarget(run.Target.Id);
    }

    /// <summary>
    /// 中樞關閉：停掉偵測，結束建置（被打斷之後也不會執行）與 Debug 模式執行的程式；Release 模式執行的程式不結束——最後一次記下它的程序
    /// （含之後開的子程序）、放開控制代碼，下次啟動接回
    /// </summary>
    private void StopLaunchAll()
    {
        try
        {
            launchCancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        SaveLaunchRuns();
        foreach (var run in launchRuns.Values)
        {
            if (run.Process is not { } process)
                continue;
            run.Process = null;
            try
            {
                if (!run.Detached)
                    process.Kill();
                process.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"執行列：結束程序失敗：{ex.Message}");
            }
        }
    }

    // ───── 和 app 脫鉤的程式：記下來、接回 ─────

    /// <summary>
    /// 掛上時：讀 runs.json，還在執行的（PID 與建立時間都對得上）接回成執行中或暫停中、從輸出檔接著讀，都不在了的忘掉；
    /// 沒被記得的輸出檔（上次結束前沒刪到的）刪掉
    /// </summary>
    private void RestoreLaunchRuns()
    {
        var dir = launchStateDirectory!;
        var (status, file, _) = JsonFile.TryRead<LaunchRunsFile>(Path.Combine(dir, LaunchRules.StateFileName));
        if (status == JsonReadStatus.Unreadable)
            Log.Warn("執行列：讀不到 runs.json，上次開著的程式這次接不回來");
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in file?.Runs ?? [])
        {
            if (record == null || LaunchRules.TargetFromRecord(record) is not { } target || launchRuns.ContainsKey(target.Id))
                continue;
            var logFile = LaunchRules.IsLogFileName(record.LogFile) ? record.LogFile : null;
            var run = new LaunchRun(target);
            run.Log.Restart();
            var epoch = run.Log.Epoch;
            run.Log.Append(LaunchRules.ReattachedNote);
            ILaunchProcess? process = null;
            try
            {
                process = launchStarter!.Reattach(record.Processes ?? [], logFile != null ? Path.Combine(dir, logFile) : null,
                    line => AppendLaunchLine(run, epoch, line));
            }
            catch (Exception ex)
            {
                Log.Warn($"執行列：接回 {target.ProjectPath} 失敗：{ex.Message}");
            }
            if (process == null)
                continue;
            run.Process = process;
            run.Detached = true;
            run.Mode = LaunchMode.Release;
            run.LogFile = logFile;
            run.Phase = record.Paused ? LaunchPhase.Paused : LaunchPhase.Running;
            run.Since = LaunchRules.SinceFromRecord(record) ?? LaunchClock();
            launchRuns[target.Id] = run;
            if (logFile != null)
                keep.Add(logFile);
            Log.Info($"執行列：接回 {target.ProjectPath}（{(record.Paused ? "暫停中" : "執行中")}）");
            _ = WatchLaunchRunAsync(run, process);
        }
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var path in Directory.EnumerateFiles(dir, "*.log"))
                {
                    var name = Path.GetFileName(path);
                    if (LaunchRules.IsLogFileName(name) && !keep.Contains(name))
                        DeleteLaunchLog(name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"執行列：清理輸出檔失敗：{ex.Message}");
        }
        // 檔案在：一定重寫一次（忘掉不在了的）
        launchStateWritten = status == JsonReadStatus.Missing ? null : string.Empty;
        SaveLaunchRuns();
    }

    /// <summary>
    /// 寫 runs.json：開著的執行的程式各自現在的程序（主程序與之後開的子程序）、輸出檔、暫停中與否；沒有就刪檔。內容沒變不重寫（維護計時器每 30 秒呼叫）
    /// </summary>
    private void SaveLaunchRuns()
    {
        if (launchStateDirectory is not { } dir)
            return;
        var records = new List<LaunchRunRecord>();
        foreach (var run in launchRuns.Values.OrderBy(r => r.Target.Id, StringComparer.Ordinal))
        {
            if (!run.Detached || run.Process is not { } process)
                continue;
            IReadOnlyList<LaunchProcessRef> processes;
            try
            {
                processes = process.Processes();
            }
            catch (Exception ex)
            {
                Log.Warn($"執行列：讀程序清單失敗：{ex.Message}");
                processes = [];
            }
            // 讀不到（剛好結束、還沒收到結束通知）：不記這一筆，結束時本來也要忘掉它
            if (processes.Count > 0)
                records.Add(LaunchRules.ToRecord(run.Target, run.Since, run.Phase == LaunchPhase.Paused, run.LogFile, processes));
        }
        var path = Path.Combine(dir, LaunchRules.StateFileName);
        try
        {
            if (records.Count == 0)
            {
                if (launchStateWritten != null)
                {
                    File.Delete(path);
                    launchStateWritten = null;
                }
                return;
            }
            var file = new LaunchRunsFile { Runs = records };
            var text = JsonConvert.SerializeObject(file, JsonFile.Settings);
            if (text == launchStateWritten)
                return;
            JsonFile.Write(path, file);
            launchStateWritten = text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"執行列：寫 runs.json 失敗：{ex.Message}");
        }
    }

    // ───── 記得的選擇（prefs.json）─────

    /// <summary>掛上時讀 prefs.json；讀不到（壞掉）當成沒記過</summary>
    private void LoadLaunchPrefs()
    {
        var (status, file, _) = JsonFile.TryRead<LaunchPrefsFile>(Path.Combine(launchStateDirectory!, LaunchRules.PrefsFileName));
        if (status == JsonReadStatus.Unreadable)
            Log.Warn("執行列：讀不到 prefs.json，選的專案與執行模式回到預設");
        launchPrefs = file ?? new LaunchPrefsFile();
        launchPrefs.Folders ??= [];
        launchPrefs.Targets ??= [];
    }

    /// <summary>寫 prefs.json（使用者選專案、換模式、按按鈕時；內容沒變時呼叫端不叫）</summary>
    private void SaveLaunchPrefs()
    {
        if (launchStateDirectory is not { } dir)
            return;
        try
        {
            JsonFile.Write(Path.Combine(dir, LaunchRules.PrefsFileName), launchPrefs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"執行列：寫 prefs.json 失敗：{ex.Message}");
        }
    }

    private void DeleteLaunchLog(string? logFile)
    {
        if (launchStateDirectory == null || logFile == null || !LaunchRules.IsLogFileName(logFile))
            return;
        try
        {
            File.Delete(Path.Combine(launchStateDirectory, logFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"執行列：刪除輸出檔 {logFile} 失敗：{ex.Message}");
        }
    }
}
