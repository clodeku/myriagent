using Myriagent.Core;
using Myriagent.UI;
using Myriagent.Update;

namespace Myriagent;

internal static class Program
{
    /// <summary>
    /// 進入點。單一實例：已經在執行就叫醒它（帶到前面）後結束；它正在結束時等它結束後由這個實例接手（見 SingleInstance）。
    /// 參數 --data &lt;資料夾&gt;：改用指定的資料夾存帳戶與對話（實機探針用，不影響正常資料）。
    /// app 自我更新的參數見 StartupOptions：--apply-update 是換版幫手（不搶單一實例、不建立視窗）
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // 帳戶委派的 MCP 橋接（docs/account-delegation.md）不是桌面實例：不讀設定、不取得單一實例鎖，也不啟動視窗
        if (args.Length == 2 && args[0] == "--delegation-mcp")
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));
            using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            return Agents.DelegationMcpBridge.RunAsync(args[1], input, output).GetAwaiter().GetResult();
        }
        var startup = System.Diagnostics.Stopwatch.StartNew();
        var options = StartupOptions.Parse(args);
        // 資料夾正規化成完整路徑並固定下來：相對路徑不會隨工作目錄改變，結尾斜線、正反斜線不同的寫法視為同一個資料夾
        var root = ShellRules.NormalizeDataRoot(options.DataRoot ?? AppPaths.Root);
        AppPaths.Override(root);

        if (options.ApplyUpdate)
            return RunApplier(options);
        if (options.InstallHere)
            return InstallHere();

        var layout = InstallLayout.Resolve();
        var managedRelease = InstallLayoutRules.ManagedRelease(AppContext.BaseDirectory, layout, Junction.GetTarget(layout.Current));
        // 和開始選單捷徑同一個 AppUserModelID：工作列釘選綁在捷徑，不綁版本資料夾裡的 exe；要在任何視窗建立之前設定（設計 8.1）
        if (managedRelease != null)
            NativeMethods.SetAppUserModelId(ShellLink.AppUserModelId);
        if (options.Uninstall)
            return Uninstall(layout, managedRelease);

        // 不同資料夾（--data 或 MYRIAGENT_AGENT_DATA）視為不同實例：探針可以和正常使用的 app 同時跑
        var instanceSuffix = ShellRules.InstanceSuffix(root, AppPaths.DefaultRoot);
        var self = AppVersionInfo.Current;
        // 新版自動接手舊版（InstanceHandoverRules）：已在執行的實例比這一版舊就請它交棒。換版幫手啟動的（--after-update／--after-update-failed）
        // 與舊版交棒後啟動的（--handed-over）不再請任何實例交棒；--handed-over 等舊版收尾代理程序、放開 mutex 的時間比較長
        var launchedByUpdate = options.AfterUpdateToken != null || options.AfterUpdateFailedToken != null || options.HandedOverToken != null;
        var decision = LaunchDecision.Activate;
        // 使用者再開一次 app（不是換版幫手、交棒、開機自動啟動）：已在執行的實例像 Chrome 一樣開一個新視窗（多視窗；對方不懂就照原本叫醒）
        using var instance = SingleInstance.Acquire(instanceSuffix, out var outcome, NativeMethods.AllowAnyForegroundWindow,
            prepareHandover: launchedByUpdate ? null : () => InstanceHandover.Prepare(root, self, out decision),
            takeoverTimeout: options.HandedOverToken != null ? InstanceHandoverRules.HandedOverTakeoverTimeout : null,
            requestNewWindow: !launchedByUpdate && !options.AutoStart);
        if (instance == null)
        {
            if (outcome == AcquireOutcome.HandedOver)
                Log.Info($"已在執行的較舊實例接下交棒：它排空對話後會啟動 {self.Release}");
            else if (decision == LaunchDecision.ExplainOldInstance)
                ExplainOldInstance(self);
            Log.Flush();
            // 換版幫手啟動的新版遇到使用者在換版的空窗中自己開的實例：叫醒它之後以專屬結束碼告訴幫手不是新版壞掉
            return options.AfterUpdateToken != null ? UpdateApplier.AnotherInstanceExitCode : 0;
        }

        Log.Cleanup();
        Log.Info($"啟動 Clode Myriagent {AppVersionInfo.Display}，資料夾 {AppPaths.Root}{(managedRelease == null ? "（開發版）" : $"，安裝 {managedRelease}")}");
        // 閃退保護：拿到單一實例之後才算一次啟動（叫醒既有實例的不算）
        if (managedRelease != null)
        {
            using var guardPlatform = new WindowsApplierPlatform();
            if (LaunchGuard.RollBackIfCrashing(layout, managedRelease, guardPlatform.Launch))
                return 0;
            if (UpdateEnvironment.E2ECrashRelease == managedRelease)
            {
                Log.Warn("端到端測試：這一版模擬啟動就當掉");
                Log.Flush();
                return 3;
            }
        }
        // 記下自己（版本與執行檔）：之後開的較新版本據此請這一份交棒
        InstanceHandover.MarkRunning(root, self);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("未處理的 UI 例外", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("未處理的例外", e.ExceptionObject as Exception);
            Log.Flush();   // 程序接著就會結束，不能等背景寫檔
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("未觀察的工作例外", e.Exception);
            e.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        // InstanceSuffix 對預設資料夾回空字串：和單一實例的判斷一致（MYRIAGENT_AGENT_PROBE* 探針都經 MYRIAGENT_AGENT_DATA 指到別的資料夾）
        using var app = new AppController(startup, new AppStartup(options, layout, managedRelease, UsesDefaultData: instanceSuffix.Length == 0));
        // 正在結束（收尾代理程序）時不回應喚醒：新啟動的實例會等這個實例結束（instance 放開 mutex）後接手
        instance.Listen(() => !app.Exiting, () => app.BeginInvoke(app.ShowMain), app.AcceptHandover, () => app.BeginInvoke(app.OpenWindowFromLaunch));
        Application.Run(app);
        InstanceHandover.ClearRunning(root);
        return 0;
    }

    /// <summary>
    /// 已在執行的是還不懂交棒的舊版（沒有 instance.json）：它的視窗已叫到前面，告訴使用者打開的不是這一版、怎麼改用這一版。
    /// 置頂顯示（舊版的視窗剛拿到前景）
    /// </summary>
    private static void ExplainOldInstance(ReleaseIdentity self)
    {
        Log.Info($"已在執行的實例不懂交棒（沒有 instance.json）：說明怎麼改用 {self.Release}");
        ApplicationConfiguration.Initialize();
        MessageBox.Show(InstanceHandoverRules.OldInstanceMessage(self.Version), "Clode Myriagent",
            MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
    }

    /// <summary>換版幫手：參數不完整（結束碼 2）就什麼都不做；其他結果結束碼 0（幫手自己已經處理好啟動哪一版）</summary>
    private static int RunApplier(StartupOptions options)
    {
        if (options.Error != null)
        {
            Log.Warn($"換版幫手的參數不合法：{options.Error}");
            Log.Flush();
            return 2;
        }
        try
        {
            using var platform = new WindowsApplierPlatform();
            var outcome = new UpdateApplier(InstallLayout.Resolve(), AppPaths.Root, platform).Run(options);
            Log.Info($"換版幫手結束：{outcome}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("換版幫手失敗", ex);
            return 1;
        }
        finally
        {
            Log.Flush();
        }
    }

    /// <summary>--install-here：不問就把自己安裝到受管理的位置（端到端測試；端到端時不建捷徑與移除項目），不啟動安裝好的那份</summary>
    private static int InstallHere()
    {
        var identity = AppVersionInfo.Current;
        if (identity.IsDev)
        {
            Log.Warn("開發版不能安裝");
            Log.Flush();
            return 2;
        }
        try
        {
            SelfInstaller.Install(AppContext.BaseDirectory, InstallLayout.Resolve(), identity, registerShell: !UpdateEnvironment.IsE2E);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("安裝失敗", ex);
            return 1;
        }
        finally
        {
            Log.Flush();
        }
    }

    /// <summary>
    /// --uninstall（「應用程式」的移除項目執行 current\Myriagent.exe --uninstall）：只有從受管理的位置執行才移除，
    /// 開發版（dotnet run、bin\Release）帶這個參數不動真正的安裝（結束碼 2）。其他結束碼見 SelfInstaller.Uninstall，失敗為 1
    /// </summary>
    private static int Uninstall(InstallLayout layout, string? managedRelease)
    {
        if (managedRelease == null)
        {
            Log.Warn("不是從安裝位置執行，不移除");
            Log.Flush();
            return 2;
        }
        var interactive = !UpdateEnvironment.IsE2E;
        if (interactive)
            ApplicationConfiguration.Initialize();   // 確認對話框要有視覺樣式與 DPI 感知（這條路徑不會再走到 Main 後面的初始化）
        try
        {
            return SelfInstaller.Uninstall(layout, AppPaths.Root, interactive);
        }
        catch (Exception ex)
        {
            Log.Error("移除失敗", ex);
            if (interactive)
                MessageBox.Show($"移除失敗：{ex.Message}", "移除 Clode Myriagent", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            Log.Flush();
        }
    }
}
