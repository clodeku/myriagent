using System.Diagnostics;
using System.Security.Cryptography;
using Myriagent.Security;
using MyriagentTools.AcmeE2e;
using static MyriagentTools.UpdateE2e.E2eInstall;

namespace MyriagentTools.UpdateE2e;

/// <summary>
/// 一次安裝依序跑完六個情境（設計第 11 節）：
/// 竄改的 zip、不認得的金鑰（都不換版）→ 正確的新版（換版、備份、接續檔用完即刪；換版前主視窗最小化，新版藏著主視窗啟動）→ 降版（不換）→
/// 新版一啟動就當掉（兩次健康閘門失敗後列壞版、留在原版）→ 直接開較新版本的執行檔（舊版交棒給它，2026-10-22）
/// </summary>
internal sealed class UpdateScenario
{
    /// <summary>
    /// 依賴的 app 日誌字串（介面字串，app 改字樣這裡要一起改）：Program.cs 的閃退與啟動行（TestRelease.StartupLogLine）、
    /// AppUpdateManager 的「新版 {release} 驗證失敗（…），重新下載一次」「新版 {release} 沒有備妥：{原因}」、AppController 的藏著主視窗啟動
    /// </summary>
    private const string CrashLogLine = "端到端測試：這一版模擬啟動就當掉";

    private const string HiddenStartLogLine = "換版前主視窗藏著：這次啟動不顯示主視窗";

    private readonly UpdateE2eOptions options;
    private readonly E2eInstall install;
    private readonly TestRelease v1 = TestRelease.Of(1);
    private readonly TestRelease v2 = TestRelease.Of(2);
    private readonly TestRelease v3 = TestRelease.Of(3);
    /// <summary>竄改與錯誤金鑰用的假發佈（不建置，見 ReleaseBuilder.FakePackage）</summary>
    private readonly TestRelease tampered = TestRelease.Of(8);
    private readonly TestRelease wrongKey = TestRelease.Of(9);
    private ResultLog log = null!;

    public UpdateScenario(UpdateE2eOptions options)
    {
        this.options = options;
        install = new E2eInstall(options.WorkDir);
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("只能在 Windows 執行");
            return 2;
        }
        var total = Stopwatch.StartNew();
        if (RealLocations.Overlaps(options.WorkDir))
        {
            Console.Error.WriteLine("工作資料夾不能是真正的安裝位置或 app 資料夾，也不能在它們裡面或包含它們");
            return 2;
        }
        if (Directory.Exists(options.WorkDir) && Directory.EnumerateFileSystemEntries(options.WorkDir).Any())
        {
            Console.Error.WriteLine($"工作資料夾 {options.WorkDir} 已有內容（通過後會整個刪掉）：請指定不存在或空的資料夾");
            return 2;
        }
        ChildProcessJob job;
        try
        {
            job = ChildProcessJob.Create();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"建立 Job Object 失敗（app 程序沒辦法隨本程式結束）：{ex.Message}");
            return 2;
        }
        Directory.CreateDirectory(options.WorkDir);
        log = new ResultLog(Path.Combine(options.WorkDir, "result.json"), new
        {
            tool = "update-e2e",
            startedAt = DateTimeOffset.Now,
            repo = options.RepoRoot,
            work = options.WorkDir,
            releases = new[] { v1.Release, v2.Release, v3.Release },
        });
        RealLocations? realBefore = null;
        try
        {
            realBefore = RealLocations.Capture(TestBuilds());
            await RunStepsAsync(job, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.Step("中止", false, "使用者中止（Ctrl+C）");
        }
        catch (Exception ex)
        {
            log.Step("例外", false, ex.Message, new { exception = ex.ToString() });
        }
        finally
        {
            job.Dispose();   // 結束所有 app、換版幫手與 WebView2 子程序
            await Task.Delay(1000, CancellationToken.None);
            var left = install.RunningApps();
            log.Step("清理", left.Count == 0, left.Count == 0 ? "沒有留下 app 程序" : $"還有 {left.Count} 個 app 程序", new { left });
            if (realBefore != null)
                CheckRealLocations(realBefore);
            if (!log.AnyFailed && !options.Keep)
                RemoveWorkDir();
        }
        Console.WriteLine(log.AnyFailed
            ? $"有檢查失敗，結果在 {log.Path}（{total.Elapsed.TotalMinutes:F1} 分鐘）"
            : $"全部通過（{total.Elapsed.TotalMinutes:F1} 分鐘）");
        return log.AnyFailed ? 1 : 0;
    }

    private async Task RunStepsAsync(ChildProcessJob job, CancellationToken ct)
    {
        // 1. 這次的測試金鑰（只寫進這三個建置）、另一把不被信任的金鑰
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = P256KeyRules.ExportSpki(key);
        var packages = new Dictionary<string, ReleasePackage>();
        foreach (var release in new[] { v1, v2, v3 })
        {
            var watch = Stopwatch.StartNew();
            var publish = await ReleaseBuilder.PublishAsync(options.RepoRoot, release, spki, options.WorkDir, ct);
            var product = FileVersionInfo.GetVersionInfo(Path.Combine(publish, "Myriagent.dll")).ProductVersion;
            var ok = product != null && product.StartsWith($"{release.Version}+{release.Build}", StringComparison.Ordinal);
            log.Step($"建置 {release.Release}", ok, $"ProductVersion {product}（{watch.Elapsed.TotalSeconds:F0} 秒）");
            if (!ok)
                return;
            packages[release.Release] = ReleaseBuilder.Package(publish, release, Path.Combine(options.WorkDir, "packages", release.Release));
        }

        // 2. 安裝 v1（--install-here；端到端時不建開始選單捷徑、不寫移除項目）
        install.SeedData();
        var installer = StartApp(job, Path.Combine(options.WorkDir, "publish", v1.Release, "Myriagent.exe"), "--install-here");
        var exited = await WaitAsync(() => installer.HasExited, TimeSpan.FromSeconds(60), ct);
        var state = install.State();
        log.Step("安裝 v1", exited && installer.ExitCode == 0 && install.CurrentTarget() == v1.Release && Str(state, "Current") == v1.Release,
            $"結束碼 {(exited ? installer.ExitCode : -1)}，current → {install.CurrentTarget()}", new { state });
        if (log.AnyFailed)
            return;

        // 3. 竄改：來源放簽章之後被改過 zip 的假發佈，從 current 啟動 v1。內容錯誤重新下載一次，再錯就列壞版
        ReleaseBuilder.PublishToSource(install.SourceDir, ReleaseBuilder.FakePackage(tampered, options.WorkDir), key, tamperZip: true);
        StartApp(job, install.CurrentExe);
        var started = await WaitAsync(() => install.CountInLogs(v1.StartupLogLine) >= 1, TimeSpan.FromSeconds(60), ct);
        log.Step("啟動 v1", started, started ? "日誌有啟動行" : "60 秒內沒看到啟動行");
        if (!started)
            return;
        await ExpectNotInstalledAsync("竄改的 zip", tampered, current: v1, reason: "下載的檔案雜湊不符", ct);
        var tamperState = install.State();
        log.Step("竄改的 zip 列壞版", InList(tamperState, "Bad", tampered.Release) && install.CountInLogs($"新版 {tampered.Release} 驗證失敗") == 1,
            $"Bad：{Raw(tamperState, "Bad")}", new { tamperState });

        // 4. 不認得的金鑰：不下載，這次執行不再重試同一版（日誌只有一次拒絕）
        ReleaseBuilder.PublishToSource(install.SourceDir, ReleaseBuilder.FakePackage(wrongKey, options.WorkDir), otherKey);
        await ExpectNotInstalledAsync("不認得的金鑰", wrongKey, current: v1, reason: "簽章不符", ct);
        var wrongKeyLogs = install.CountInLogs($"新版 {wrongKey.Release} 沒有備妥：簽章不符");
        log.Step("不認得的金鑰不重試", wrongKeyLogs == 1, $"拒絕 {wrongKeyLogs} 次（app 已再檢查兩次）");
        if (log.AnyFailed)
            return;

        // 5. 正確的 v2：下載、驗證、排空（沒有對話＝立刻）、換版幫手切換、新版健康、接續檔用完即刪。
        // 換版前先把 v1 的主視窗最小化（設計 9.3：主視窗藏著時，無人值守的換版不能把它叫出來）：v2 要藏著主視窗啟動，照樣送 ready 通過健康閘門
        if (!await MinimizeMainWindowAsync(ct))
            return;
        var watchSwitch = Stopwatch.StartNew();
        ReleaseBuilder.PublishToSource(install.SourceDir, packages[v2.Release], key);
        var switched = await WaitAsync(() => Str(install.State(), "Current") == v2.Release, options.Timeout, ct);
        var v2Started = switched && await WaitAsync(() => install.CountInLogs(v2.StartupLogLine) >= 1, TimeSpan.FromSeconds(15), ct);
        var single = switched && await WaitAsync(() => install.RunningApps().Count == 1, TimeSpan.FromSeconds(30), ct);
        var resumeGone = switched && await WaitAsync(() => !File.Exists(Path.Combine(install.DataRoot, "update", "resume.json")), TimeSpan.FromSeconds(30), ct);
        state = install.State();
        var backup = File.Exists(Path.Combine(install.DataRoot, "backups", "pre-" + v2.Release, "settings.json"));
        log.Step("換版到 v2",
            switched && v2Started && single && resumeGone && backup && Str(state, "Previous") == v1.Release && install.CurrentTarget() == v2.Release
                && Str(state, "LastResult") == $"已更新到 {v2.Version}",
            $"{watchSwitch.Elapsed.TotalSeconds:F0} 秒；current → {install.CurrentTarget()}，app 程序 {install.RunningApps().Count} 個",
            new { switched, v2Started, single, resumeGone, backup, state });
        if (log.AnyFailed)
            return;

        // v2 藏著主視窗（上面健康閘門已通過：藏著的 WebView2 照樣送 ready）；再啟動一次 app（第二個實例叫醒它）時照常顯示
        var v2Pid = install.RunningApps().FirstOrDefault();
        var stayedHidden = await StaysAsync(() => AppWindows.Visible(v2Pid).Count == 0, TimeSpan.FromSeconds(3), ct);
        var hiddenLogged = install.CountInLogs(HiddenStartLogLine) == 1;
        var waker = StartApp(job, install.CurrentExe);
        var wakerExited = await WaitAsync(() => waker.HasExited, TimeSpan.FromSeconds(30), ct);
        var shown = wakerExited && await WaitAsync(() => AppWindows.Visible(v2Pid).Count > 0, TimeSpan.FromSeconds(15), ct);
        log.Step("v2 藏著主視窗啟動",
            stayedHidden && hiddenLogged && wakerExited && waker.ExitCode == 0 && shown,
            !stayedHidden ? "v2 啟動時顯示了主視窗" : !hiddenLogged ? "日誌沒有藏著主視窗啟動的那一行" : shown ? "藏著啟動；再次啟動 app 時顯示主視窗" : "再次啟動 app 也叫不出主視窗",
            new { v2Pid, stayedHidden, hiddenLogged, wakerExited, wakerExit = wakerExited ? waker.ExitCode : (int?)null, shown });
        if (log.AnyFailed)
            return;

        // 5b. 這台的 .NET 被移除（.NET 更新刪掉了 app 啟動時用的修補版，2026-10-27：之後的新版都解壓不了）：沒有已備妥的版本時，
        //     閒置就交棒給同一版自己（--handed-over 目前這一版的 exe），新程序照 resume.json 接續；current 不變、主視窗照常顯示
        var beforeRestart = install.RunningApps().FirstOrDefault();
        var v2StartsBefore = install.CountInLogs(v2.StartupLogLine);
        var watchRestart = Stopwatch.StartNew();
        install.MarkRuntimeRemoved(beforeRestart, "10.0.0");
        var selfRestarted = await WaitAsync(() => install.RunningApps() is [var pid] && pid != beforeRestart
            && install.CountInLogs(v2.StartupLogLine) == v2StartsBefore + 1, TimeSpan.FromSeconds(90), ct);
        install.ClearRuntimeRemoved();
        var restartResumeGone = selfRestarted && await WaitAsync(() => !File.Exists(Path.Combine(install.DataRoot, "update", "resume.json")), TimeSpan.FromSeconds(30), ct);
        var restartShown = selfRestarted && await WaitAsync(() => install.RunningApps() is [var pid] && AppWindows.Visible(pid).Count > 0, TimeSpan.FromSeconds(15), ct);
        var restartLogged = install.CountInLogs("重新啟動 app（這台的 .NET 已更新）：排空對話") == 1;
        var notAgain = selfRestarted && await StaysAsync(() => install.RunningApps().Count == 1 && install.CountInLogs(v2.StartupLogLine) == v2StartsBefore + 1,
            TimeSpan.FromSeconds(5), ct);
        log.Step(".NET 被移除後重新啟動成同一版",
            selfRestarted && restartResumeGone && restartShown && restartLogged && notAgain && install.CurrentTarget() == v2.Release
                && Str(install.State(), "Current") == v2.Release,
            $"{watchRestart.Elapsed.TotalSeconds:F0} 秒；{(selfRestarted ? "重新啟動" : "沒有重新啟動")}，current → {install.CurrentTarget()}",
            new { beforeRestart, selfRestarted, restartResumeGone, restartShown, restartLogged, notAgain });
        if (log.AnyFailed)
            return;

        // 6. 降版：來源回到 v1（簽章正確）也不裝
        ReleaseBuilder.PublishToSource(install.SourceDir, packages[v1.Release], key);
        await ExpectNotInstalledAsync("降版", v1, current: v2, reason: null, ct);

        // 7. v3 一啟動就當掉：第一次健康閘門失敗只回滾，第二次列壞版；之後留在 v2、只剩一個 app
        var watchCrash = Stopwatch.StartNew();
        ReleaseBuilder.PublishToSource(install.SourceDir, packages[v3.Release], key);
        var bad = await WaitAsync(() => InList(install.State(), "Bad", v3.Release), options.Timeout, ct);
        var settled = bad && await WaitAsync(() => install.RunningApps().Count == 1 && Str(install.State(), "Current") == v2.Release, TimeSpan.FromSeconds(60), ct);
        // 交棒時 v2 的主視窗開著（上一步叫出來了）：退回的 v2 照常顯示主視窗
        var rolledBackShown = settled && await WaitAsync(() => install.RunningApps() is [var pid] && AppWindows.Visible(pid).Count > 0, TimeSpan.FromSeconds(15), ct);
        var crashes = install.CountInLogs(CrashLogLine);
        state = install.State();
        log.Step("v3 閃退回滾",
            bad && settled && rolledBackShown && crashes == 2 && CountOf(state, "GateFailures", v3.Release) == 2 && install.CurrentTarget() == v2.Release
                && Str(state, "LastResult") == $"新版 {v3.Version} 啟動失敗，已退回 {v2.Version}" && install.CountInLogs(HiddenStartLogLine) == 1,
            $"{watchCrash.Elapsed.TotalSeconds:F0} 秒；當掉 {crashes} 次，current → {install.CurrentTarget()}",
            new { bad, settled, rolledBackShown, crashes, state });

        // 8. 沒有殘留：versions 沒有 .partial-*、staging 沒有下載到一半的檔
        var staging = Path.Combine(install.Root, "staging");
        var partials = Directory.EnumerateDirectories(Path.Combine(install.Root, "versions"), ".partial-*")
            .Concat(Directory.Exists(staging) ? Directory.EnumerateFiles(staging, "*.partial") : Enumerable.Empty<string>())
            .ToList();
        log.Step("沒有殘留", partials.Count == 0, partials.Count == 0 ? "沒有 .partial" : $"{partials.Count} 個 .partial", new { partials });
        if (log.AnyFailed)
            return;

        // 9. 新版自動接手舊版（2026-10-22）：v2 執行中時直接開 v3 的發佈資料夾（不是受管理的位置，不會模擬閃退）。
        //    v3 讀 instance.json 看到 v2 較舊 → 請它交棒後結束；v2 排空、以 --handed-over 啟動 v3 並結束；v3 等 v2 放開單一實例後接手、照 resume.json 接續。
        //    交棒不碰安裝：current 仍是 v2
        var v3Exe = Path.Combine(options.WorkDir, "publish", v3.Release, "Myriagent.exe");
        var v3StartsBefore = install.CountInLogs(v3.StartupLogLine);
        var watchHandover = Stopwatch.StartNew();
        var requester = StartApp(job, v3Exe);
        var requesterExited = await WaitAsync(() => requester.HasExited, TimeSpan.FromSeconds(30), ct);
        var accepted = await WaitAsync(() => install.CountInLogs($"接下交棒請求：{v3.Release}") == 1, TimeSpan.FromSeconds(15), ct);
        var v2Gone = accepted && await WaitAsync(() => install.RunningApps().Count == 0, TimeSpan.FromSeconds(60), ct);
        var v3Running = v2Gone && await WaitAsync(() => ProcessesOf(v3Exe).Count == 1 && install.CountInLogs(v3.StartupLogLine) == v3StartsBefore + 1,
            TimeSpan.FromSeconds(60), ct);
        var handoverResumeGone = v3Running && await WaitAsync(() => !File.Exists(Path.Combine(install.DataRoot, "update", "resume.json")), TimeSpan.FromSeconds(30), ct);
        var instanceFile = Path.Combine(install.DataRoot, "update", "instance.json");
        var markedV3 = v3Running && await WaitAsync(() => File.Exists(instanceFile) && ReadAllTextShared(instanceFile).Contains(v3.Release, StringComparison.Ordinal),
            TimeSpan.FromSeconds(15), ct);
        log.Step("新版自動接手舊版",
            requesterExited && requester.ExitCode == 0 && accepted && v2Gone && v3Running && handoverResumeGone && markedV3 && install.CurrentTarget() == v2.Release,
            $"{watchHandover.Elapsed.TotalSeconds:F0} 秒；請求的 v3 {(requesterExited ? $"結束碼 {requester.ExitCode}" : "沒有結束")}，"
                + $"{(accepted ? "v2 接下交棒" : "v2 沒有接下")}，{(v3Running ? "v3 接手執行" : "v3 沒有接手")}，current → {install.CurrentTarget()}",
            new { requesterExited, requesterExit = requesterExited ? requester.ExitCode : (int?)null, accepted, v2Gone, v3Running, handoverResumeGone, markedV3 });
    }

    /// <summary>從這個執行檔執行中的程序（不在暫存安裝底下的，例如發佈資料夾的 v3）</summary>
    private static IReadOnlyList<int> ProcessesOf(string exe)
    {
        var list = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                        list.Add(process.Id);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // 剛結束或拿不到模組：不算
                }
            }
        }
        return list;
    }

    /// <summary>讀 app 正在寫的檔（共用讀寫）；讀不到回空字串</summary>
    private static string ReadAllTextShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>來源換成不該裝的版本後等 app 完整檢查一次：沒有換版、沒有備妥、新版本沒有留下資料夾；reason 不是 null 時日誌要有拒絕原因</summary>
    private async Task ExpectNotInstalledAsync(string name, TestRelease offered, TestRelease current, string? reason, CancellationToken ct)
    {
        var checkedTwice = await WaitForChecksAsync(2, ct);
        var state = install.State();
        var isNew = offered.Release != v1.Release;   // 降版提供的 v1 本來就在 versions\ 裡當前一版
        var noFolder = !isNew || (!Directory.Exists(install.VersionDir(offered.Release)) && !Directory.Exists(install.PartialVersionDir(offered.Release)));
        var logged = reason == null || install.CountInLogs($"新版 {offered.Release} 沒有備妥：{reason}") >= 1;
        log.Step(name,
            checkedTwice && noFolder && logged && Str(state, "Current") == current.Release && Str(state, "Staged") == null && install.CurrentTarget() == current.Release,
            checkedTwice ? $"current 仍是 {install.CurrentTarget()}" : "60 秒內 app 沒有再檢查兩次",
            new { checkedTwice, noFolder, logged, state });
    }

    /// <summary>
    /// 等 LastCheckAt 換過 count 次（檢查一開始就寫它，同時只跑一個檢查）：看到第二個新值時，第一個在來源換掉之後才開始的檢查已經跑完
    /// </summary>
    private async Task<bool> WaitForChecksAsync(int count, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { Raw(install.State(), "LastCheckAt") };
        return await WaitAsync(() =>
        {
            seen.Add(Raw(install.State(), "LastCheckAt"));
            return seen.Count - 1 >= count;
        }, TimeSpan.FromSeconds(60), ct);
    }

    /// <summary>從外部把唯一執行中的 app（v1）的主視窗最小化：交棒時 AppController 會把「主視窗藏著」記進 resume.json</summary>
    private async Task<bool> MinimizeMainWindowAsync(CancellationToken ct)
    {
        var apps = install.RunningApps();
        var window = IntPtr.Zero;
        var found = apps.Count == 1 && await WaitAsync(() => (window = AppWindows.Visible(apps[0]).FirstOrDefault()) != IntPtr.Zero, TimeSpan.FromSeconds(30), ct);
        var minimized = found && AppWindows.Minimize(window);
        log.Step("v1 主視窗最小化", minimized,
            minimized ? "已最小化（換版前主視窗藏著）" : found ? "最小化失敗" : $"找不到 v1 的主視窗（app 程序 {apps.Count} 個）");
        return minimized;
    }

    private Process StartApp(ChildProcessJob job, string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        install.ApplyEnvironment(info, crashRelease: v3.Release);
        var process = Process.Start(info) ?? throw new InvalidOperationException($"無法啟動 {exe}");
        if (!job.TryAdd(process))
            log.Step("Job Object", null, $"無法把 {process.Id} 加進 Job Object，結束時可能要手動關閉");
        return process;
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(500, ct);
        }
        return condition();
    }

    /// <summary>condition 在 duration 內一直成立（每 250 毫秒看一次）</summary>
    private static async Task<bool> StaysAsync(Func<bool> condition, TimeSpan duration, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            if (!condition())
                return false;
            await Task.Delay(250, ct);
        }
        return condition();
    }

    /// <summary>所有測試版本的 build（含不建置的假發佈）：真正的 app 日誌裡出現的次數不能變</summary>
    private IReadOnlyList<string> TestBuilds() => new[] { v1, v2, v3, tampered, wrongKey }.Select(r => r.Build).ToList();

    /// <summary>真正的安裝位置、app 資料夾、開始選單捷徑與移除項目和開始前一樣（見 RealLocations）</summary>
    private void CheckRealLocations(RealLocations before)
    {
        try
        {
            var after = RealLocations.Capture(TestBuilds());
            var changed = after.ChangedSince(before);
            log.Step("真正的安裝與資料沒有變動", changed.Count == 0,
                changed.Count == 0 ? $"{string.Join("、", after.Parts.Keys)}都和開始前一樣" : $"有變動：{string.Join("、", changed)}",
                new
                {
                    changed,
                    before = changed.ToDictionary(k => k, k => before.Parts.GetValueOrDefault(k)),
                    after = changed.ToDictionary(k => k, k => after.Parts[k]),
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Step("真正的安裝與資料沒有變動", false, $"讀不到：{ex.Message}", new { exception = ex.ToString() });
        }
    }

    /// <summary>先拆 current（junction 本身），再刪整個工作資料夾；刪不掉就留著並說一聲</summary>
    private void RemoveWorkDir()
    {
        try
        {
            if (Directory.Exists(install.Current))
                Directory.Delete(install.Current);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(options.WorkDir, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(500);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"工作資料夾刪不掉（{ex.Message}），留在 {options.WorkDir}");
        }
    }
}
