using System.Diagnostics;
using System.Globalization;
using Myriagent.Core;
using Myriagent.UI;

namespace Myriagent.Update;

internal enum ApplyOutcome
{
    Switched,
    SwitchedNoRelaunch,
    RolledBack,
    OldProcessHung,
    Invalid,

    /// <summary>
    /// 舊程序結束後、切換前放棄這次（沒有換版）：備份、切換 junction 失敗（啟動舊版），或使用者在空窗中自己開了 app（不啟動任何版本）
    /// </summary>
    Aborted,
}

/// <summary>幫手需要的程序、時間操作（測試換成假的）</summary>
internal interface IApplierPlatform
{
    DateTime UtcNow { get; }

    void Sleep(TimeSpan duration);

    /// <summary>等舊 app（幫手的父程序）結束；逾時回 false（程序不存在、或這個 PID 已被別的程序重用，都算已結束）</summary>
    bool WaitForExit(int pid, TimeSpan timeout);

    /// <summary>只結束這個程序（舊 app，幫手的父程序）</summary>
    void Kill(int pid);

    /// <summary>等 WebView2 放開使用者資料夾（EBWebView\lockfile 可以獨占開啟或不存在）；逾時回 false</summary>
    bool WaitForWebViewRelease(string userDataFolder, TimeSpan timeout);

    /// <summary>啟動程序，回傳 PID；啟動失敗回 null</summary>
    int? Launch(string executable, IReadOnlyList<string> arguments);

    /// <summary>Launch 啟動的程序（新版）是否已經結束；不會丟例外（例外會跳過回滾）</summary>
    bool HasExited(int pid);

    /// <summary>結束整個程序樹（新版，幫手的子程序）</summary>
    void KillTree(int pid);

    /// <summary>這個資料夾已經有 app 的實例（單一實例的 mutex 存在）；只探測、不持有</summary>
    bool IsInstanceRunning(string dataRoot);

    /// <summary>Launch 啟動、已經結束的程序的結束碼；還在跑或讀不到回 null</summary>
    int? ExitCode(int pid);
}

/// <summary>
/// 換版幫手（設計 8.5；由舊版的 exe 以 --apply-update 執行，不搶單一實例、不建立視窗、不載入 WebView2）：
/// 寫 .armed → 等舊程序與 WebView2 → 備份設定 → 切換 current → 啟動新版 → 90 秒健康閘門 → 通過就更新狀態與清理，失敗就切回、還原、啟動舊版。
/// 舊程序結束之後一律再啟動一個版本（結束時換版除外）：切換前失敗就放棄這次，回滾的每一步都盡力而為、不讓例外跳過啟動舊版。
/// 例外是使用者在空窗中自己開了 app（單一實例已經有人）：不切換、不啟動任何版本；新版遇到它而結束（AnotherInstanceExitCode）也不算閘門失敗
/// </summary>
internal sealed class UpdateApplier
{
    public static readonly TimeSpan WaitOldProcess = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan WaitWebView = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan HealthGate = TimeSpan.FromSeconds(90);
    public const int BackupsKept = 3;

    /// <summary>
    /// 以 --after-update 啟動的新版遇到這個資料夾已經有實例（使用者在換版的空窗中自己開的）、叫醒它就結束時的結束碼（Program.Main）：
    /// 幫手看到它就知道不是新版壞掉，不算健康閘門失敗
    /// </summary>
    public const int AnotherInstanceExitCode = 4;
    private static readonly string[] BackupFiles = { "settings.json", "accounts.json" };

    private readonly InstallLayout layout;
    private readonly string dataRoot;
    private readonly IApplierPlatform platform;

    public UpdateApplier(InstallLayout layout, string dataRoot, IApplierPlatform platform)
    {
        this.layout = layout;
        this.dataRoot = dataRoot;
        this.platform = platform;
    }

    /// <param name="options">StartupOptions.Parse 的結果，Error 必須是 null</param>
    public ApplyOutcome Run(StartupOptions options)
    {
        if (options.Token is not { } token || options.To is not { } to || options.WaitPid is not { } waitPid)
            throw new ArgumentException("換版幫手的參數不完整（先檢查 StartupOptions.Error）", nameof(options));
        var from = CurrentRelease();
        Directory.CreateDirectory(layout.HealthDirectory);
        File.WriteAllText(layout.ArmedFile(token), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        try
        {
            return Apply(from, to, token, waitPid, options.NoRelaunch);
        }
        finally
        {
            DeleteSignalFiles(token);
        }
    }

    /// <summary>備份、切換 junction、更新狀態時可以接住的失敗（檔案被鎖、沒有權限、等不到狀態檔的鎖、結束程序樹的彙總例外）</summary>
    private static bool IsRecoverable(Exception ex)
        => ex is IOException or UnauthorizedAccessException or TimeoutException or AggregateException;

    private ApplyOutcome Apply(string? from, string to, string token, int waitPid, bool noRelaunch)
    {
        Log.Info($"換版幫手：{from ?? "?"} → {to}{(noRelaunch ? "（結束時換版，不重新啟動）" : string.Empty)}");

        if (!platform.WaitForExit(waitPid, WaitOldProcess))
        {
            Log.Warn($"舊程序 {waitPid} 60 秒內沒有結束，強制結束、不換版");
            platform.Kill(waitPid);
            if (!noRelaunch)
                LaunchOld(token);
            return ApplyOutcome.OldProcessHung;
        }
        if (from == null || !File.Exists(layout.Executable(to)) || !File.Exists(layout.Executable(from)))
        {
            Log.Warn($"換版幫手：目前版或目標版不存在（{from ?? "沒有 current"} → {to}），不換版");
            if (!noRelaunch)
                LaunchOld(token);
            return ApplyOutcome.Invalid;
        }
        if (!platform.WaitForWebViewRelease(Path.Combine(dataRoot, "WebView2"), WaitWebView))
            Log.Warn("WebView2 10 秒內沒有放開使用者資料夾，照常換版");
        // 使用者在換版的空窗（舊 app 結束之後）自己重新開了 app：它從 current（舊版）執行、已經拿到單一實例，也讀過 resume.json。
        // 不能在它底下改指向 current（之後才載入的組件會來自新版），重新啟動的新版也只會叫醒它就結束（會被誤判成健康閘門失敗）。
        // 放棄這次：不切換、保留備妥、不啟動任何版本（緊接在切換之前探測，探測不持有 mutex）
        if (platform.IsInstanceRunning(dataRoot))
        {
            Log.Warn("另一個 Clode Myriagent 正在執行（換版的空窗中重新開啟），這次不換版");
            TryStep("記錄放棄這次", () => UpdaterStateStore.Update(layout, s =>
            {
                s.LastResult = $"另一個 Clode Myriagent 正在執行，這次沒有換到 {UpdaterStateRules.VersionOf(to)}，之後再套用";
                s.LastResultAt = DateTime.Now;
                return s;
            }));
            return ApplyOutcome.Aborted;
        }

        string backup;
        try
        {
            backup = BackUp(to);
            RetargetWithRetry(layout, layout.VersionDirectory(to), platform.Sleep);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // 切換是單一一次 FSCTL 呼叫：失敗時 current 仍指著舊版
            Log.Error("換版前置步驟失敗，放棄這次", ex);
            TryStep("記錄放棄這次", () => UpdaterStateStore.Update(layout, s =>
            {
                s.LastResult = $"換版到 {to} 失敗，已放棄這次";
                s.LastResultAt = DateTime.Now;
                return s;
            }));
            if (!noRelaunch)
                LaunchOld(token);
            return ApplyOutcome.Aborted;
        }

        if (noRelaunch)
        {
            CommitSwitch(from, to);
            return ApplyOutcome.SwitchedNoRelaunch;
        }

        var pid = platform.Launch(layout.CurrentExecutable, new[] { "--after-update", token });
        if (pid != null && WaitHealthy(token, pid.Value))
        {
            Log.Info($"新版 {to} 通過健康閘門");
            CommitSwitch(from, to);
            return ApplyOutcome.Switched;
        }
        if (pid is { } exitedPid && platform.ExitCode(exitedPid) == AnotherInstanceExitCode)
        {
            // 切換之後、新版拿到單一實例之前，使用者自己開的實例搶先拿到（新版叫醒它就結束）：不是新版壞掉。
            // 不算健康閘門失敗、不還原設定（那個實例正在用），也不切回（它多半就是從切換後的 current 啟動的）
            Log.Warn($"新版 {to} 啟動時已經有別的實例在執行，換版算完成，不回滾");
            CommitSwitch(from, to);
            return ApplyOutcome.Switched;
        }

        RollBack(from, to, token, pid, backup);
        return ApplyOutcome.RolledBack;
    }

    /// <summary>current 目前指向的 release（資料夾名稱）；沒有 current 或讀不到回 null（之後當成目前版不存在）</summary>
    private string? CurrentRelease()
    {
        try
        {
            var target = Junction.GetTarget(layout.Current);
            return target == null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"讀不到 {layout.Current} 的目標：{ex.Message}");
            return null;
        }
    }

    private void LaunchOld(string token) => platform.Launch(layout.CurrentExecutable, new[] { "--after-update-failed", token });

    private bool WaitHealthy(string token, int pid)
    {
        var deadline = platform.UtcNow + HealthGate;
        while (platform.UtcNow < deadline)
        {
            if (File.Exists(layout.HealthFile(token)))
                return true;
            if (platform.HasExited(pid))
                return false;
            platform.Sleep(TimeSpan.FromMilliseconds(250));
        }
        return File.Exists(layout.HealthFile(token));
    }

    /// <summary>
    /// 健康閘門失敗：結束新版的程序樹、切回舊版、還原設定、更新狀態、啟動舊版；每一步各自盡力而為。
    /// 切不回舊版時 current 還指著新版：狀態寫成「新版已連續啟動失敗到門檻」，下次從捷徑啟動時閃退保護（LaunchGuard）會切回舊版；這次直接從舊版資料夾啟動
    /// </summary>
    private void RollBack(string from, string to, string token, int? pid, string backup)
    {
        Log.Warn($"新版 {to} 沒有通過健康閘門，退回 {from}");
        if (pid is { } newPid)
            TryStep("結束新版的程序樹", () => platform.KillTree(newPid));
        bool restored = TryStep("切回舊版", () => RetargetWithRetry(layout, layout.VersionDirectory(from), platform.Sleep));
        TryStep("還原設定", () => Restore(backup));
        if (restored)
        {
            TryStep("記錄退回", () => UpdaterStateStore.Update(layout, s => UpdaterStateRules.OnGateFailed(s, to, from)));
            LaunchOld(token);
            return;
        }
        TryStep("記錄退回（current 還指著新版）", () => UpdaterStateStore.Update(layout, s =>
        {
            s = UpdaterStateRules.OnSwitched(s, from, to);
            s.GateFailures[to] = s.GateFailures.GetValueOrDefault(to) + 1;
            s.LaunchFailures[to] = UpdaterStateRules.LaunchFailureThreshold;
            s.LastResult = $"新版 {UpdaterStateRules.VersionOf(to)} 啟動失敗，切回 {UpdaterStateRules.VersionOf(from)} 沒有成功，下次啟動時退回";
            return s;
        }));
        platform.Launch(layout.Executable(from), new[] { "--after-update-failed", token });
    }

    /// <summary>新版已經換好（通過健康閘門或結束時換版）：更新狀態並刪掉舊版；失敗只記錄，絕不因此回滾</summary>
    private void CommitSwitch(string from, string to)
    {
        try
        {
            var state = UpdaterStateStore.Update(layout, s => UpdaterStateRules.OnSwitched(s, from, to));
            var installed = Directory.EnumerateDirectories(layout.VersionsDirectory).Select(Path.GetFileName).OfType<string>().ToList();
            var deleted = UpdaterStateRules.DeleteVersions(layout, UpdaterStateRules.VersionsToDelete(state, installed, running: from));
            if (deleted.Count > 0)
                Log.Info($"已刪除舊版：{string.Join("、", deleted)}");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Log.Error($"已換到 {to}，但更新 updater-state.json 或刪除舊版失敗", ex);
        }
    }

    /// <summary>執行一步，可接住的失敗只記錄；回傳是否成功</summary>
    private static bool TryStep(string step, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Log.Error($"換版幫手：{step}失敗", ex);
            return false;
        }
    }

    /// <summary>改指向失敗（防毒、索引服務暫時鎖住）退避重試 3 次（設計 15 節 P0-3）；閃退保護切回前一版也用它</summary>
    internal static void RetargetWithRetry(InstallLayout layout, string target, Action<TimeSpan> sleep)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Junction.Retarget(layout.Current, target);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                sleep(TimeSpan.FromMilliseconds(200 << attempt));
            }
        }
    }

    /// <summary>
    /// 備份 settings.json、accounts.json 到 backups\pre-&lt;新 release&gt;\，只留最近 3 份（這次的一定留：同一版再試一次時資料夾是舊的）；回傳備份資料夾
    /// </summary>
    private string BackUp(string to)
    {
        var root = Path.Combine(dataRoot, "backups");
        var name = "pre-" + to;
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        foreach (var file in BackupFiles)
        {
            var source = Path.Combine(dataRoot, file);
            if (File.Exists(source))
                File.Copy(source, Path.Combine(dir, file), overwrite: true);
        }
        var older = new DirectoryInfo(root).EnumerateDirectories("pre-*")
            .Where(d => !string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.CreationTimeUtc)
            .Skip(BackupsKept - 1);
        foreach (var old in older)
        {
            try
            {
                old.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"刪不掉舊備份 {old.FullName}：{ex.Message}");
            }
        }
        return dir;
    }

    private void Restore(string backup)
    {
        foreach (var file in BackupFiles)
        {
            var saved = Path.Combine(backup, file);
            if (File.Exists(saved))
                File.Copy(saved, Path.Combine(dataRoot, file), overwrite: true);
        }
    }

    /// <summary>每條結束路徑都清掉這次的訊號檔（.armed 與健康訊號）：幫手接手之後沒有別人會刪</summary>
    private void DeleteSignalFiles(string token)
    {
        foreach (var file in new[] { layout.ArmedFile(token), layout.HealthFile(token) })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"刪不掉 {file}：{ex.Message}");
            }
        }
    }
}

/// <summary>
/// 真正的平台（幫手程序用）。PID 可能被別的程序重用，所以：
/// 幫手啟動的程序（新版）留著 Launch 拿到的 Process 不釋放，HasExited、KillTree 都用它（持有控制代碼時 Windows 不會重用這個 PID）；
/// 舊 app 是幫手的父程序、一定比幫手早啟動，同一個 PID 上比幫手晚啟動的程序就不是它（舊 app 已經結束）
/// </summary>
internal sealed class WindowsApplierPlatform : IApplierPlatform, IDisposable
{
    /// <summary>Launch 啟動的程序（PID → Process）：到 Dispose 才釋放</summary>
    private readonly Dictionary<int, Process> launched = new();
    private readonly DateTime selfStartTime;

    public WindowsApplierPlatform()
    {
        using var self = Process.GetCurrentProcess();
        selfStartTime = self.StartTime;
    }

    public DateTime UtcNow => DateTime.UtcNow;

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);

    public bool WaitForExit(int pid, TimeSpan timeout)
    {
        using var process = OpenOldApp(pid);
        if (process == null)
            return true;   // 已經不在了（或 PID 已被別的程序重用）
        try
        {
            return process.WaitForExit(timeout);
        }
        catch (InvalidOperationException)
        {
            return true;   // 找到之後才結束
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 等不了就當成沒結束：不切換、強制結束後啟動舊版（例外跳出去的話，舊 app 結束後沒有任何版本會再啟動）
            Log.Warn($"等程序 {pid} 結束失敗：{ex.Message}");
            return false;
        }
    }

    public void Kill(int pid)
    {
        using var process = OpenOldApp(pid);
        if (process == null)
        {
            Log.Info($"程序 {pid} 已經結束（或 PID 已被別的程序重用），不用結束");
            return;
        }
        try
        {
            // 只結束這個程序：幫手是舊 app 的子程序，Kill(entireProcessTree: true) 會丟 InvalidOperationException；
            // CLI 子程序由舊 app 的 ChildProcessJob（KILL_ON_JOB_CLOSE）收掉，WebView2 在宿主程序結束時跟著結束
            process.Kill();
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"結束程序 {pid} 失敗：{ex.Message}");
        }
    }

    public bool WaitForWebViewRelease(string userDataFolder, TimeSpan timeout)
    {
        var lockFile = Path.Combine(userDataFolder, "EBWebView", "lockfile");
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                if (!File.Exists(lockFile))
                    return true;
                using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    return true;
            }
            catch (IOException)
            {
                // 還有 WebView2 程序開著
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
            if (DateTime.UtcNow >= deadline)
                return false;
            Thread.Sleep(200);
        }
    }

    public int? Launch(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
            foreach (var a in arguments)
                info.ArgumentList.Add(a);
            var process = Process.Start(info);
            if (process == null)
                return null;
            // 不釋放：HasExited、KillTree 用這個控制代碼，新版提早結束後這個 PID 也不會被別的程序拿走
            launched[process.Id] = process;
            return process.Id;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"啟動 {executable} 失敗：{ex.Message}");
            return null;
        }
    }

    public bool HasExited(int pid)
    {
        if (launched.TryGetValue(pid, out var own))
        {
            try
            {
                return own.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 自己啟動的程序照理不會發生：看不出來就當成還在跑，交給健康閘門的 90 秒逾時
                Log.Warn($"讀不到程序 {pid} 的狀態：{ex.Message}");
                return false;
            }
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;   // 不在了，或打不開（系統或其他帳戶的程序）：不是幫手啟動的那個，PID 已被重用
        }
    }

    public void KillTree(int pid)
    {
        var owned = launched.TryGetValue(pid, out var process);
        try
        {
            if (!owned)
            {
                // 幫手只對自己啟動的新版呼叫；查不到才以 PID 重新開。比幫手早啟動的一定不是幫手的子程序，不動它
                process = Process.GetProcessById(pid);
                if (process.StartTime < selfStartTime)
                {
                    Log.Warn($"程序 {pid} 比換版幫手早啟動，不是幫手啟動的新版，不結束");
                    return;
                }
            }
            // Kill(true) 把各程序的失敗彙總成 AggregateException：接住，免得跳過回滾
            process!.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"結束程序樹 {pid} 失敗：{ex.Message}");
        }
        finally
        {
            if (!owned)
                process?.Dispose();
        }
    }

    /// <summary>單一實例的 mutex 名稱照 Program.Main：預設資料夾沒有後綴，--data／MYRIAGENT_AGENT_DATA 各自一組（ShellRules.InstanceSuffix）</summary>
    public bool IsInstanceRunning(string dataRoot) => SingleInstance.IsRunning(ShellRules.InstanceSuffix(dataRoot, AppPaths.DefaultRoot));

    public int? ExitCode(int pid)
    {
        if (!launched.TryGetValue(pid, out var process))
            return null;
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"讀不到程序 {pid} 的結束碼：{ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var process in launched.Values)
            process.Dispose();
        launched.Clear();
    }

    /// <summary>
    /// 以 PID 找舊 app（幫手的父程序）。不在了、打不開（系統或其他帳戶的程序），或比幫手晚啟動（父程序一定比子程序早啟動），
    /// 都表示舊 app 已經結束、PID 被別的程序重用：回 null，絕不等它或結束它
    /// </summary>
    private Process? OpenOldApp(int pid)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            if (process.StartTime <= selfStartTime)
                return process;
            Log.Info($"PID {pid} 上的程序比換版幫手晚啟動，不是舊 app（舊 app 已經結束）");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 不在了，或打不開
        }
        process?.Dispose();
        return null;
    }
}
