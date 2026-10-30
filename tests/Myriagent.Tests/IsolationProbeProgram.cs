using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Myriagent.Core;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Isolation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests;

/// <summary>
/// 測試組件的進入點（csproj 的 <c>GenerateProgramFile=false</c>；testhost 載入測試組件時不呼叫它）：S6 的子程序探針（D6 第二道防線）。
/// 以 <c>Myriagent.Tests.exe --isolation-probe &lt;情境&gt; --data &lt;暫存資料夾&gt;</c>（沒有 apphost 時 <c>dotnet Myriagent.Tests.dll …</c>）執行，
/// 跑完情境後在 stdout 輸出一行 JSON：<c>{"assemblies":[…],"nativeModules":[…]}</c>（已載入的組件名稱與原生模組檔名）。
/// 每個情境是各自的 NoInlining 方法，Main 只做分派：JIT 編譯 Main 時不會連帶載入別的情境碰到的組件。
/// <list type="bullet">
/// <item><c>baseline</c>：以 HubHarness 建中樞、跑完 ready／init 就結束（不建任何同步元件）。</item>
/// <item><c>unpaired</c>：同 baseline，另外掛上未配對的 SyncCoordinator（AttachSync、用量閘門）並 Start、等它讀完 device.json。兩者的組件集合必須相同。</item>
/// <item><c>unpaired-commands</c>：同 unpaired，之後依序送每一種 sync 指令：對 <see cref="PairedOfflineServer"/> 的 syncPair（連不上而失敗，
/// 真的建一把軟體 CNG 金鑰、失敗後刪掉）、取消、暫停、重試、確認、改政策、deviceRename、解除配對。不得載入禁止的組件與 e_sqlite3。</item>
/// <item><c>control</c>：陽性對照，碰 SignalR 用戶端的型別、開一條記憶體 SqliteConnection。</item>
/// <item><c>paired-offline</c>：已配對（測試先在資料根放好 device.json、sync.db 與記憶體金鑰的 PKCS#8）但伺服器連不上：
/// 經載入邊界建正式的引擎，連 <see cref="PairedOfflineServer"/>（沒有人在聽的埠）直到進入退避，證明已配對的路徑真的載入 SignalR 與 SQLite。
/// 探針不繼承 MYRIAGENT_SYNC_SERVER，伺服器以 ServerOverride 直接給。</item>
/// </list>
/// </summary>
internal static class IsolationProbeProgram
{
    public const string Flag = "--isolation-probe";

    /// <summary>情境做完之後再等一下，讓中樞的合併送出、帳戶卡節流這類短計時器跑完（兩個情境等一樣久，比的是同一個時間點）</summary>
    private const int SettleMilliseconds = 600;

    /// <summary>情境結束時中樞的記錄檔裡共幾行、其中的 WARN 與 ERROR（中樞的資料根在結束時就刪掉，所以先收起來）</summary>
    private static int logLineCount;
    private static readonly List<string> LogProblems = [];

    /// <summary>paired-offline 連的伺服器（discard 埠：沒有人在聽，連線立刻被拒）</summary>
    public const string PairedOfflineServer = "https://127.0.0.1:9";

    /// <summary>paired-offline 的記憶體金鑰（PKCS#8 base64），放在資料根</summary>
    public const string PairedOfflineKeyFile = "probe-key.txt";

    public static int Main(string[] args)
    {
        var error = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
        var scenario = ArgAfter(args, Flag);
        var data = ArgAfter(args, "--data");
        if (scenario is null || data is null)
        {
            error.WriteLine($"用法：Myriagent.Tests {Flag} <baseline|unpaired|unpaired-commands|control|paired-offline> --data <暫存資料夾>");
            return 2;
        }

        try
        {
            Directory.CreateDirectory(data);
            AppPaths.Override(data);
            switch (scenario)
            {
                case "baseline":
                    RunBaseline();
                    break;
                case "unpaired":
                    RunUnpaired();
                    break;
                case "unpaired-commands":
                    RunUnpairedCommands();
                    break;
                case "control":
                    RunControl();
                    break;
                case "paired-offline":
                    RunPairedOffline(data);
                    break;
                default:
                    error.WriteLine($"不認得的情境：{scenario}");
                    return 2;
            }

            var report = Report();
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            output.WriteLine(report);
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.ToString());
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunBaseline() => RunHub(null);

    /// <summary>第 3 階段的未配對路徑：照 AppController 的接法掛上未配對的 SyncCoordinator 並 Start</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunUnpaired() => RunHub(harness => MountCoordinator(harness));

    /// <summary>未配對時網頁送來的每一種 sync 指令（配對連不上而失敗）</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunUnpairedCommands() => RunHub(harness =>
    {
        var coordinator = MountCoordinator(harness);
        var main = harness.Main;
        harness.Command(main, "ready");
        main.WaitForAsync("init", timeoutMs: 15000).GetAwaiter().GetResult();
        var mark = main.Mark();
        harness.Command(main, "syncPair", new { deviceName = "PROBE-PC", server = PairedOfflineServer });
        var failed = main.WaitForAsync("sync", m => m["sync"]?["pairing"]?.Value<string>("error") is not null, mark, timeoutMs: 20000).GetAwaiter().GetResult();
        // 一定要是「連不上伺服器」：金鑰建立失敗、參數不合格等在送出 HTTP 之前就結束的失敗，證明不了「真的走過配對、仍不載入 SignalR／SQLite」
        var expected = PairingFlowRules.ErrorText(PairingCodes.NetworkError);
        var errorText = failed["sync"]!["pairing"]!.Value<string>("error");
        if (errorText != expected)
            throw new InvalidOperationException($"配對失敗的原因應該是「{expected}」，實際是「{errorText}」");
        harness.Command(main, "syncCancelPair");
        harness.Command(main, "syncPause", new { paused = true });
        harness.Command(main, "syncPause", new { paused = false });
        harness.Command(main, "syncRetry");
        harness.Command(main, "syncConfirm", new { kind = "hubIdentity", fingerprint = "AAAA-BBBB-CCCC-DDDD" });
        harness.Command(main, "syncSetPolicy", new { remoteControlLevel = "View", allowRemoteFullAccess = false });
        harness.Command(main, "deviceRename", new { deviceId = "d_PROBE", name = "探針" });
        // 「在這台登入」（WP3.4）：未配對一定被拒，而且不能落回一般新增（不跑真的 CLI：ResolveLauncher 一律 null）
        harness.Ui.Invoke(() => harness.Accounts.ResolveLauncher = _ => null);
        var before = harness.OnUi(() => harness.Accounts.Accounts.Count);
        harness.Command(main, "accountAdd", new { provider = "Claude", logicalAccountId = "la_0123456789ABCDEFGHJKMNPQRS" });
        harness.Ui.DrainAsync().GetAwaiter().GetResult();
        var after = harness.OnUi(() => harness.Accounts.Accounts.Count);
        if (after != before)
            throw new InvalidOperationException($"未配對時「在這台登入」應該被拒，帳戶數卻從 {before} 變成 {after}");
        harness.Command(main, "syncUnpair");
        harness.Ui.DrainAsync().GetAwaiter().GetResult();
        if (coordinator.IsPaired)
            throw new InvalidOperationException("探針不該配對成功");
        // 失敗的配對在背景刪掉這次建的金鑰與 pending-key.json（PairingRecovery.Abandon）：等它做完，沒刪就是留下了孤兒金鑰
        var pendingKey = SyncPaths.FromAppPaths().PendingKeyFile;
        var watch = Stopwatch.StartNew();
        while (File.Exists(pendingKey) && watch.Elapsed < TimeSpan.FromSeconds(10))
            Thread.Sleep(50);
        if (File.Exists(pendingKey))
            throw new InvalidOperationException("配對失敗後 10 秒 pending-key.json 還在（裝置金鑰沒有清掉）");
    });

    /// <summary>照 AppController 的接法：中樞掛同步橋、帳戶管理員掛用量閘門，Start 並等它讀完 device.json（明確給選項，不讀環境變數）</summary>
    private static SyncCoordinator MountCoordinator(HubHarness harness)
    {
        var options = new SyncCoordinatorOptions(false, null, false, "0.0.0", "PROBE-PC", "probe");
        var coordinator = harness.OnUi(() => new SyncCoordinator(harness.Hub, harness.Accounts, harness.Host, harness.Settings, SyncPaths.FromAppPaths(), options, TimeProvider.System));
        harness.Ui.Invoke(() =>
        {
            harness.Hub.AttachSync(coordinator);
            harness.Accounts.UsagePollPolicy = coordinator;
            coordinator.Start();
        });
        if (!coordinator.WhenStarted.Wait(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("SyncCoordinator 15 秒內沒有啟動完");
        if (coordinator.IsPaired)
            throw new InvalidOperationException("探針的資料根不該有 device.json");
        return coordinator;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunControl()
    {
        IsolationControlUnmarked.Describe();
        if (IsolationControlCatchLocal.Open("Data Source=:memory:") is null)
            throw new InvalidOperationException("記憶體 SQLite 開不起來");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunPairedOffline(string data) => PairedOfflineProbe.Run(data);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunHub(Action<HubHarness>? mount)
    {
        using var harness = new HubHarness();
        mount?.Invoke(harness);
        harness.Command(harness.Main, "ready");
        harness.Main.WaitForAsync("init", timeoutMs: 15000).GetAwaiter().GetResult();
        harness.Ui.DrainAsync().GetAwaiter().GetResult();
        Thread.Sleep(SettleMilliseconds);
        harness.Ui.DrainAsync().GetAwaiter().GetResult();
        CollectLog();
    }

    /// <summary>基準與各情境都走這裡（組件集合的比較不受影響）：寫一行標記證明收得到，再讀回記錄檔</summary>
    private static void CollectLog()
    {
        Log.Info("探針：情境結束");
        Log.Flush();
        if (!Directory.Exists(AppPaths.LogDirectory))
            return;
        foreach (var file in Directory.EnumerateFiles(AppPaths.LogDirectory, "*.log").Order(StringComparer.Ordinal))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                logLineCount++;
                if (line.Contains("[WARN]", StringComparison.Ordinal) || line.Contains("[ERROR]", StringComparison.Ordinal))
                    LogProblems.Add(line);
            }
        }
    }

    /// <summary>先把清單取好才組 JSON（組 JSON 本身載入的組件不算）</summary>
    private static string Report()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? string.Empty)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        using var process = Process.GetCurrentProcess();
        var modules = process.Modules.Cast<ProcessModule>()
            .Select(m => m.ModuleName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var json = new JObject
        {
            ["assemblies"] = new JArray(assemblies),
            ["nativeModules"] = new JArray(modules),
            ["logLines"] = logLineCount,
            ["logProblems"] = new JArray(LogProblems),
        };
        return json.ToString(Formatting.None);
    }

    private static string? ArgAfter(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
