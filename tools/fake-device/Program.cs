using System.Text;
using Myriagent.Core;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FakeDevice;

/// <summary>
/// 無頭的假裝置（第 3 階段版）。用法見 README.md：
/// <code>
/// FakeDevice --data &lt;資料根&gt; pair --server https://127.0.0.1:埠 [--name 名稱] [--dev-trust] [--owner-user admin --owner-password-env 變數] [--owner-origin https://localhost] [--no-approve] [--no-drain]
/// FakeDevice --data &lt;資料根&gt; run [--script 劇本.json] [--control-stdin] [--fake-codex 情境] [--dev-trust]
/// FakeDevice --data &lt;資料根&gt; session --server https://127.0.0.1:埠 --owner-user admin --owner-password-env 變數 --script 劇本.json [--ephemeral-key] [--fake-codex 情境] [--dev-trust]
/// FakeDevice --data &lt;資料根&gt; status [--dev-trust] [--wait 秒] [--wait-backfill 秒]
/// FakeDevice --data &lt;資料根&gt; unpair
/// FakeDevice --data &lt;資料根&gt; backlog --count 1000 [--dev-trust] [--no-wait]
/// </code>
/// 結果以單行 JSON 印到標準輸出（--no-approve 的配對碼也是），進度與錯誤印到標準錯誤。結束碼：0 成功、1 失敗、2 用法錯誤、3 資料根已有 app 或 fake-device 在執行
/// </summary>
internal static class Program
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Utf8;
        var stdout = new StreamWriter(Console.OpenStandardOutput(), Utf8) { AutoFlush = true };
        var stderr = new StreamWriter(Console.OpenStandardError(), Utf8) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stderr);

        CommandLine command;
        try
        {
            command = CommandLine.Parse(args);
        }
        catch (FakeDeviceException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(CommandLine.Usage);
            return 2;
        }

        // 正規化成完整路徑（同 app 的 Program：結尾斜線、正反斜線不同的寫法視為同一個資料夾）
        var root = ShellRules.NormalizeDataRoot(command.Data);
        var defaultRoot = ShellRules.NormalizeDataRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Myriagent"));
        if (string.Equals(root, defaultRoot, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"不能用正常使用的資料夾（{defaultRoot}）：請以 --data 指定暫存資料夾");
            return 2;
        }

        // --fake-codex（WP4.9 步 13）：資料根要在 TEMP 底下，在建資料根之前檢查（不合格時什麼都不建）
        if (command.Value("--fake-codex") is not null && FakeLauncherRules.CheckDataRoot(root, Path.GetTempPath()) is { } badRoot)
        {
            Console.Error.WriteLine(badRoot);
            return 2;
        }

        AppPaths.Override(root);
        Directory.CreateDirectory(root);
        // 與 app 相同的單一實例鎖（每個資料根一個）：同一個資料根不能同時跑 app 與 fake-device。不等前一個實例結束
        using var instance = SingleInstance.Acquire(ShellRules.InstanceSuffix(root, defaultRoot), null, TimeSpan.FromSeconds(1), TimeSpan.Zero);
        if (instance is null)
        {
            Console.Error.WriteLine($"資料根 {root} 已經有 Clode Myriagent 或 fake-device 在執行");
            return 3;
        }

        Log.Info($"fake-device {command.Verb}：資料根 {root}");
        try
        {
            var result = RunAsync(command, root).GetAwaiter().GetResult();
            Console.Out.WriteLine(result.ToString(Formatting.None));
            return 0;
        }
        catch (FakeDeviceException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Error("fake-device 失敗", ex);
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
        finally
        {
            Log.Flush();
        }
    }

    private static async Task<JObject> RunAsync(CommandLine command, string root)
    {
        // 已配對的資料根：啟動執行環境就會照 device.json 連線，所以每個指令都先過正式站守門（合併前審查 F4；unpair 只看網址、不連線）
        try
        {
            if (await PairedServerGuard.VerifyAsync(new SyncPaths(Path.Combine(root, "sync")), connect: command.Verb != "unpair", PairedServerGuard.VerifyTimeout) is { } facts)
                Console.Error.WriteLine($"守門：這個資料根配對的伺服器是本機的 MyriagentHub（environment {facts.Environment}）");
        }
        catch (DeviceRuntimeException ex)
        {
            throw new FakeDeviceException(ex.Message);
        }

        // --fake-codex（WP4.9 步 13；資料根已在 Main 檢查過）：假 app-server 的子程序不繼承 PSModulePath（啟動器帶不了環境，從這個程序的環境剝掉）
        var fakeCodex = command.Value("--fake-codex");
        if (fakeCodex is not null)
            StripInheritedEnvironment();

        var options = new DeviceRuntimeOptions
        {
            DataRoot = root,
            DevTrust = command.Has("--dev-trust"),
            MachineName = Environment.MachineName,
            WindowsUser = Environment.UserName,
            AppVersion = command.Value("--app-version") ?? DeviceRuntime.DefaultAppVersion,
            // session --ephemeral-key：裝置金鑰只在記憶體（不碰 CNG 的金鑰存放區）；引擎一律經 TransportMeter（status 的 batchAck、liveRelay.sent）
            EphemeralKey = command.Has("--ephemeral-key"),
            MeasureTransport = true,
            FakeCodex = fakeCodex,
        };
        // run、session：劇本最前面的 seedConversations 在中樞啟動之前寫進資料根（中樞啟動時才讀對話資料夾）。
        // run --control-stdin 可以不給劇本（WP4.9 步 11：指令都從標準輸入來）
        JObject? script = null;
        JArray? seeded = null;
        if (command.Verb is "run" or "session")
        {
            var scriptPath = command.Value("--script");
            if (scriptPath is null && !command.Has("--control-stdin"))
                throw new FakeDeviceException(command.Verb == "run" ? "run 要有 --script 或 --control-stdin" : "session 要有 --script");
            if (scriptPath is not null)
            {
                script = FakeScript.Load(scriptPath);
                seeded = FakeScript.SeedBeforeStart(root, script);
            }
        }

        await using var device = await DeviceRuntime.StartAsync(options);
        try
        {
            return command.Verb switch
            {
                "pair" => await PairAsync(device, command),
                "run" => await RunScriptAsync(device, command, script, seeded),
                "session" => await SessionAsync(device, command, script!, seeded!),
                "status" => await StatusAsync(device, command),
                "unpair" => await UnpairAsync(device),
                "backlog" => await BacklogAsync(device, command),
                _ => throw new FakeDeviceException($"不認得的指令：{command.Verb}"),
            };
        }
        catch (DeviceRuntimeException ex)
        {
            // Runtime 的配對、解除配對與正式站守門的失敗（訊息給使用者看，結束碼 1）
            throw new FakeDeviceException(ex.Message);
        }
        finally
        {
            await device.StopAsync();
            if (device.Host.Errors.Count > 0)
                Console.Error.WriteLine($"UI 執行緒有 {device.Host.Errors.Count} 個未處理的例外（見 {AppPaths.LogDirectory}）");
            if (device.Usage.OtherRequests > 0)
                Console.Error.WriteLine($"有 {device.Usage.OtherRequests} 個不是用量端點的 HTTP 請求被擋下（假裝置不連外網）");
        }
    }

    // ───────────────────────────── pair ─────────────────────────────

    /// <summary>
    /// pair 與 session 的伺服器網址：先過正式站守門（WP4.9 步 8，D18：只收 https＋迴路、拒絕 example.com，再讀一次 /readyz 與憑證，
    /// 正式服務、hub.example.com 的憑證或公開 CA 簽發一律拒絕），再照 app 的規則（只收 https、不帶路徑）
    /// </summary>
    private static async Task<Uri> GuardedServerAsync(CommandLine command)
    {
        var server = command.Value("--server") ?? throw new FakeDeviceException($"{command.Verb} 要有 --server");
        if (LocalHubTargetRules.CheckUrl(server) is { } problem)
            throw new FakeDeviceException($"伺服器網址不合格（{server}）：{problem}");
        var serverUri = DevTrustRules.ValidateServerUrl(server) ?? throw new FakeDeviceException($"伺服器網址不合格（只收 https、不帶路徑）：{server}");
        var facts = await LocalHubTarget.VerifyAsync(serverUri, TimeSpan.FromSeconds(15));
        Console.Error.WriteLine($"守門：{serverUri.GetLeftPart(UriPartial.Authority)} 是本機的 MyriagentHub（environment {facts.Environment}）");
        return serverUri;
    }

    /// <summary>自動核准的擁有者帳密（密碼只從環境變數讀）</summary>
    private static (string User, string Password) OwnerCredentials(CommandLine command, string hint)
    {
        var ownerUser = command.Value("--owner-user");
        var passwordVariable = command.Value("--owner-password-env");
        if (ownerUser is null || passwordVariable is null)
            throw new FakeDeviceException($"自動核准要有 --owner-user 與 --owner-password-env（密碼只從環境變數讀）{hint}");
        var password = Environment.GetEnvironmentVariable(passwordVariable);
        if (string.IsNullOrEmpty(password))
            throw new FakeDeviceException($"環境變數 {passwordVariable} 沒有設定擁有者密碼");
        return (ownerUser, password);
    }

    /// <summary>以擁有者身分登入並核准這個配對碼</summary>
    private static async Task ApproveAsync(CommandLine command, Uri serverUri, (string User, string Password) owner, string userCode)
    {
        using var api = new OwnerApi(serverUri, command.Value("--owner-origin") ?? serverUri.GetLeftPart(UriPartial.Authority), devTrust: command.Has("--dev-trust"));
        await api.LoginAsync(owner.User, owner.Password);
        await api.ApproveAsync(userCode);
        Console.Error.WriteLine("擁有者已核准");
    }

    private static async Task<JObject> PairAsync(DeviceRuntime device, CommandLine command)
    {
        if (await device.IsPairedAsync())
            throw new FakeDeviceException("已經配對（sync\\device.json 存在）：要重新配對先執行 unpair");
        var serverUri = await GuardedServerAsync(command);
        var name = command.Value("--name") ?? $"fake-{Environment.MachineName}";
        var noApprove = command.Has("--no-approve");
        (string User, string Password)? owner = noApprove ? null : OwnerCredentials(command, "；不核准請加 --no-approve");

        var pairing = await device.StartPairingAsync(serverUri, name);
        Console.Error.WriteLine($"配對碼 {pairing.UserCode}，裝置指紋 {pairing.DeviceFingerprint}，核准網址 {pairing.VerificationUriComplete}");
        if (owner is null)
        {
            // 給 hub-e2e：一行 JSON，之後等瀏覽器核准
            Console.Out.WriteLine(new JObject
            {
                ["userCode"] = pairing.UserCode,
                ["deviceFingerprint"] = pairing.DeviceFingerprint,
                ["verificationUri"] = pairing.VerificationUri,
                ["verificationUriComplete"] = pairing.VerificationUriComplete,
                ["expiresAt"] = pairing.ExpiresAt,
            }.ToString(Formatting.None));
        }
        else
        {
            await ApproveAsync(command, serverUri, owner.Value, pairing.UserCode);
        }

        // 等配對提交（device.json 寫好、引擎建好）並連上；--no-approve 時最多等到配對碼到期
        var expires = DateTimeOffset.FromUnixTimeMilliseconds(pairing.ExpiresAt) - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        var waitPaired = noApprove ? (expires > TimeSpan.Zero ? expires : TimeSpan.FromSeconds(5)) : command.Seconds("--timeout", 60);
        await device.WaitPairedLiveAsync(waitPaired);

        // --no-drain（WP4.9 步 2，measure_sync 的 head-paired-backfilling）：device.json 已寫好、連上了，不等 outbox 清空；
        // 只等到回補開始（發布者回報了進度）或確定沒有要回補的就結束，app 接著在回補途中啟動
        var noDrain = command.Has("--no-drain");
        bool? drained = null;
        int? rows = null;
        int? rejected = null;
        SyncBackfillProgress? backfill;
        if (noDrain)
        {
            backfill = await WaitBackfillReportedAsync(device, command.Seconds("--timeout", 60));
        }
        else
        {
            var (d, r, j) = await device.WaitDrainedAsync(command.Seconds("--timeout", 60));
            (drained, rows, rejected) = (d, r, j);
            backfill = (await device.BackfillAsync()).Progress;
        }

        var status = await device.StatusAsync();
        return new JObject
        {
            ["command"] = "pair",
            ["deviceId"] = status?.DeviceId,
            ["deviceFingerprint"] = status?.DeviceFingerprint,
            ["hubFingerprint"] = status?.HubFingerprint,
            ["server"] = status?.Server,
            ["state"] = status?.State.ToString(),
            ["noDrain"] = noDrain,
            ["drained"] = drained,
            ["outboxRows"] = rows,
            ["rejected"] = rejected,
            // 發布者的回補進度（還剩幾個對話、約多少位元組）；null＝沒有要回補的、已經做完，或沒有 conv1
            ["backfill"] = backfill is null ? null : new JObject { ["conversationsLeft"] = backfill.ConversationsLeft, ["bytesLeft"] = backfill.BytesLeft },
        };
    }

    /// <summary>
    /// pair --no-drain：等發布者回報回補的進度（重整的清單完整、排了要上傳的對話），回傳那一刻的進度；
    /// 同步安定連續 1 秒（沒有要回補的、或已經做完）或逾時就回 null
    /// </summary>
    private static async Task<SyncBackfillProgress?> WaitBackfillReportedAsync(DeviceRuntime device, TimeSpan timeout)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? quietSince = null;
        while (watch.Elapsed <= timeout)
        {
            var progress = (await device.BackfillAsync()).Progress;
            if (progress is not null)
                return progress;
            var settled = await device.EngineAsync() is ISyncSettleProbe engine
                          && SyncSettleRules.IsSettled((await engine.SettleSnapshotAsync()) with { BackfillDone = true });
            if (settled)
            {
                quietSince ??= watch.Elapsed;
                if (SyncSettleRules.QuietEnough(watch.Elapsed - quietSince.Value, TimeSpan.FromSeconds(1)))
                    return null;
            }
            else
            {
                quietSince = null;
            }

            await Task.Delay(50);
        }

        Console.Error.WriteLine($"pair --no-drain：{timeout.TotalSeconds:0} 秒內沒有等到回補的進度");
        return null;
    }

    // ───────────────────────────── run ─────────────────────────────

    /// <summary>
    /// 跑劇本（有給的話）；<c>--control-stdin</c>（WP4.9 步 11）時接著讀標準輸入的控制指令，每行一個 JSON、每個回一行 JSON 到標準輸出
    /// （格式見 README 與 <see cref="FakeControl.RunChannelAsync"/>），quit 或輸入結束才往下。劇本裡開始的背景串流接著留在控制通道裡；
    /// 沒有控制通道時等它們自己跑完。之後同沒有控制通道：已配對時等 outbox 清空，最後一行是這個指令的結果
    /// </summary>
    private static async Task<JObject> RunScriptAsync(DeviceRuntime device, CommandLine command, JObject? script, JArray? seeded)
    {
        var paired = await device.IsPairedAsync();
        if (paired && !await device.WaitUntilAsync(() => device.Sync.Status?.State == SyncEngineState.Live, command.Seconds("--timeout", 60)))
            Console.Error.WriteLine("還沒連上伺服器：劇本照跑，修改先排進 outbox");
        await using var control = new FakeControl(device);
        var steps = script is null ? new JArray() : await FakeScript.RunAsync(device, script, seeded, control);
        var result = new JObject { ["command"] = "run", ["paired"] = paired, ["steps"] = steps };
        if (command.Has("--control-stdin"))
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), Utf8, detectEncodingFromByteOrderMarks: true);
            result["control"] = await control.RunChannelAsync(stdin, Console.Out);
        }
        else if (await control.FinishStreamsAsync(stop: false, FakeControl.ScriptStreamsTimeout) is { Count: > 0 } streams)
        {
            result["streams"] = streams;
        }

        // 控制通道的 unpair（WP4.9 步 17）可能已經解除配對（FF8 看匯出的稽核紀錄、FF10 收掉被撤銷的裝置）：這時沒有 outbox 可等，照常結束
        var stillPaired = paired && await device.IsPairedAsync();
        result["unpaired"] = paired && !stillPaired;
        if (stillPaired)
        {
            var (drained, rows, rejected) = await device.WaitDrainedAsync(command.Seconds("--timeout", 60));
            result["drained"] = drained;
            result["outboxRows"] = rows;
            result["rejected"] = rejected;
            if (!drained)
                throw new FakeDeviceException($"劇本跑完了，但 outbox 沒有在時限內清空（{rows} 列）");
        }

        result["status"] = await DescribeAsync(device);
        return result;
    }

    // ───────────────────────────── status ─────────────────────────────

    private static async Task<JObject> StatusAsync(DeviceRuntime device, CommandLine command)
    {
        var paired = await device.IsPairedAsync();
        if (paired)
            await device.WaitUntilAsync(() => device.Sync.Status?.State == SyncEngineState.Live, command.Seconds("--wait", 15));
        // --wait-backfill：等回補做完（measure_sync 在 S6 取樣之前用；沒有 conv1 時清空就算完成）
        (bool Done, double Seconds)? backfill = null;
        if (command.Value("--wait-backfill") is not null)
        {
            if (!paired)
                throw new FakeDeviceException("沒有配對：--wait-backfill 要先 pair");
            backfill = await device.WaitBackfillDoneAsync(command.Seconds("--wait-backfill", 600));
        }

        var result = await DescribeAsync(device);
        result["command"] = "status";
        if (backfill is { } waited)
        {
            result["backfillDone"] = waited.Done;
            result["backfillSeconds"] = waited.Seconds;
            if (!waited.Done)
                throw new FakeDeviceException($"回補沒有在時限內做完（{waited.Seconds} 秒）：{result.ToString(Formatting.None)}");
        }

        return result;
    }

    private static Task<JObject> DescribeAsync(DeviceRuntime device) => device.DescribeAsync();

    /// <summary>從這個程序的環境剝掉 <see cref="FakeLauncherRules.StrippedVariables"/>（之後啟動的假 app-server 繼承剝過的環境）</summary>
    private static void StripInheritedEnvironment()
    {
        var names = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(FakeLauncherRules.IsStrippedVariable).ToList();
        foreach (var name in names)
            Environment.SetEnvironmentVariable(name, null);
        if (names.Count > 0)
            Console.Error.WriteLine($"--fake-codex：假 app-server 不繼承 {string.Join("、", names)}");
    }

    // ───────────────────────────── unpair ─────────────────────────────

    private static async Task<JObject> UnpairAsync(DeviceRuntime device)
    {
        // 協調者停引擎、在背景刪金鑰、pending 檔、sync.db 與 device.json（最後刪）
        var deviceId = await device.UnpairAsync(TimeSpan.FromSeconds(20));
        return new JObject { ["command"] = "unpair", ["deviceId"] = deviceId, ["note"] = "只刪本機的配對資料；伺服器上的裝置要由擁有者撤銷" };
    }

    // ───────────────────────────── session ─────────────────────────────

    /// <summary>
    /// 同一個程序裡配對、跑劇本、解除配對（WP4.9 步 8；負載工具的假裝置群用 <c>--ephemeral-key</c>：裝置金鑰只在記憶體，不碰 CNG 的金鑰存放區）。
    /// 一律自動核准（要 --owner-user 與 --owner-password-env）；跑完劇本等 outbox 清空、記下狀態，不論成敗都解除配對（只刪本機的配對資料）。
    /// 輸出 <c>{"command":"session","pair":{…},"steps":[…],"drained":…,"status":{…},"unpair":{…}}</c>；失敗時結束碼 1，錯誤的前面照樣印出已有的部分
    /// </summary>
    private static async Task<JObject> SessionAsync(DeviceRuntime device, CommandLine command, JObject script, JArray seeded)
    {
        if (await device.IsPairedAsync())
            throw new FakeDeviceException("已經配對（sync\\device.json 存在）：session 要從沒有配對的資料根開始（先 unpair）");
        var serverUri = await GuardedServerAsync(command);
        var owner = OwnerCredentials(command, "；session 一律自動核准");
        var name = command.Value("--name") ?? $"fake-{Environment.MachineName}";
        var timeout = command.Seconds("--timeout", 60);
        var result = new JObject { ["command"] = "session", ["server"] = serverUri.GetLeftPart(UriPartial.Authority), ["ephemeralKey"] = device.UsesEphemeralKey };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Exception? failure = null;
        try
        {
            var pairing = await device.StartPairingAsync(serverUri, name);
            Console.Error.WriteLine($"配對碼 {pairing.UserCode}，裝置指紋 {pairing.DeviceFingerprint}");
            await ApproveAsync(command, serverUri, owner, pairing.UserCode);
            await device.WaitPairedLiveAsync(timeout);
            var status = await device.StatusAsync();
            var (_, file) = DeviceFile.Load(device.Paths.DeviceFile);
            result["pair"] = new JObject
            {
                ["deviceId"] = status?.DeviceId,
                ["deviceFingerprint"] = status?.DeviceFingerprint,
                ["hubFingerprint"] = status?.HubFingerprint,
                ["state"] = status?.State.ToString(),
                // 記憶體金鑰時 CNG 的金鑰存放區沒有這個名稱的檔（測試以它確認）
                ["keyName"] = file?.KeyName,
                ["keyStorage"] = file?.KeyStorage,
                ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 2),
            };

            result["steps"] = await FakeScript.RunAsync(device, script, seeded);
            var (drained, rows, rejected) = await device.WaitDrainedAsync(timeout);
            result["drained"] = drained;
            result["outboxRows"] = rows;
            result["rejected"] = rejected;
            result["status"] = await device.DescribeAsync();
            if (!drained)
                throw new FakeDeviceException($"劇本跑完了，但 outbox 沒有在時限內清空（{rows} 列）");
        }
        catch (Exception ex) when (ex is FakeDeviceException or DeviceRuntimeException)
        {
            failure = ex;
        }
        finally
        {
            // 不論成敗都解除配對（記憶體金鑰在程序結束時就沒了，留下 device.json 只會是壞掉的配對）
            if (await device.IsPairedAsync())
            {
                try
                {
                    result["unpair"] = new JObject { ["deviceId"] = await device.UnpairAsync(TimeSpan.FromSeconds(20)) };
                }
                catch (DeviceRuntimeException ex)
                {
                    result["unpair"] = new JObject { ["error"] = ex.Message };
                    failure ??= ex;
                }
            }

            result["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 2);
        }

        if (failure is not null)
        {
            Console.Out.WriteLine(result.ToString(Formatting.None));
            throw new FakeDeviceException(failure.Message);
        }

        return result;
    }

    // ───────────────────────────── backlog ─────────────────────────────

    /// <summary>
    /// 直接在 outbox 排 N 筆不合併的 cli.state（同 S5 的積壓）。--no-wait：寫進 sync.db 就結束（下次啟動再補送，S5 的暖啟動量測用）；
    /// 否則等全部送完並回報花的時間。最後請引擎全部重送一次帳戶與 CLI（積壓的 cli.state 是假的內容，讓伺服器回到這台真正的 CLI 狀態）
    /// </summary>
    private static async Task<JObject> BacklogAsync(DeviceRuntime device, CommandLine command)
    {
        var count = command.Int("--count", 1000);
        if (count is < 1 or > 100_000)
            throw new FakeDeviceException("--count 要在 1～100000 之間");
        if (await device.EngineAsync() is not { } engine)
            throw new FakeDeviceException("沒有配對：backlog 要先 pair");
        // 以 deviceSeq 計（已完成滿 24 小時的列會被修剪，列數不可靠）
        var before = await engine.InvokeAsync(v => v.Outbox.Count == 0 ? 0 : v.Outbox.Max(e => e.DeviceSeq));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 1; i <= count; i++)
            engine.Post(new SyncSignal.Append(new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.CliState, BacklogPayload(i), null)));
        if (!await WaitAsync(async () => await engine.InvokeAsync(v => v.Outbox.Count(e => e.DeviceSeq > before)) >= count, command.Seconds("--timeout", 60)))
            throw new FakeDeviceException("積壓沒有在時限內寫進 outbox");
        var queued = watch.Elapsed;
        // 積壓全部進了 outbox 才要求重送：同一輪收件匣裡快照的重送排在直接寫入的前面，太早送的話伺服器最後看到的是假的 cli.state
        engine.Post(new SyncSignal.FullResync());
        // 引擎執行緒依序處理：這個查詢回來時，上面的重送已經寫進 outbox（--no-wait 時也不會丟）
        var queuedRows = await engine.InvokeAsync(v => v.Outbox.Count(e => e.DeviceSeq > before));
        var result = new JObject
        {
            ["command"] = "backlog",
            ["count"] = count,
            ["queuedSeconds"] = Math.Round(queued.TotalSeconds, 2),
            ["resyncRows"] = queuedRows - count,
        };
        if (command.Has("--no-wait"))
            return result;

        var (drained, rows, rejected) = await device.WaitDrainedAsync(command.Seconds("--timeout", 120));
        result["drained"] = drained;
        result["drainedSeconds"] = Math.Round(watch.Elapsed.TotalSeconds, 2);
        result["outboxRows"] = rows;
        result["rejected"] = rejected;
        if (!drained)
            throw new FakeDeviceException("積壓沒有在時限內送完");
        return result;
    }

    /// <summary>不合併的 cli.state（每筆內容不同；和 MyriagentHub.Tests 的 SoakTests 同一種事件）</summary>
    private static string BacklogPayload(long n) => SyncJson.Serialize(new CliStatePayload(new Dictionary<string, DeviceCliView>
    {
        ["Claude"] = new(true, "0.0.0-backlog", "native", null, false, 0, 0, null, n),
    }));

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                return false;
            await Task.Delay(100);
        }

        return true;
    }
}

/// <summary>命令列：--data 一定要有；第一個不是選項的字是指令</summary>
internal sealed class CommandLine
{
    public const string Usage = """
        用法：
          FakeDevice --data <資料根> pair --server https://127.0.0.1:<埠> [--name 名稱] [--dev-trust] [--owner-user admin --owner-password-env 變數名稱] [--owner-origin https://localhost] [--no-approve] [--no-drain] [--timeout 秒]
          FakeDevice --data <資料根> run [--script <劇本.json>] [--control-stdin] [--fake-codex <情境>] [--dev-trust] [--timeout 秒]（--script 與 --control-stdin 至少一個）
          FakeDevice --data <資料根> session --server https://127.0.0.1:<埠> --owner-user admin --owner-password-env 變數名稱 --script <劇本.json> [--ephemeral-key] [--fake-codex <情境>] [--dev-trust] [--name 名稱] [--owner-origin https://localhost] [--timeout 秒]
          FakeDevice --data <資料根> status [--dev-trust] [--wait 秒] [--wait-backfill 秒]
          FakeDevice --data <資料根> unpair
          FakeDevice --data <資料根> backlog --count 1000 [--dev-trust] [--no-wait] [--timeout 秒]
        全域：--app-version <版本>（Hello 宣告的 app 版本，預設 0.1.0）
        --fake-codex <情境>：Codex 的 CLI 換成假 app-server（ok、evil_host、http、missing_fields、unsupported、complete_fail、init_fail；資料根要在 TEMP 底下）
        伺服器只收這台電腦自己啟動的 MyriagentHub（https＋127.0.0.1／localhost／[::1]，連上之後再以 /readyz 與憑證確認不是正式站）
        """;

    private static readonly HashSet<string> Verbs = new(StringComparer.Ordinal) { "pair", "run", "session", "status", "unpair", "backlog" };

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "--dev-trust", "--no-approve", "--no-wait", "--no-drain", "--ephemeral-key", "--control-stdin" };

    private static readonly HashSet<string> Valued = new(StringComparer.Ordinal)
    {
        "--data", "--server", "--name", "--owner-user", "--owner-password-env", "--owner-origin", "--script", "--wait", "--count", "--timeout", "--wait-backfill",
        "--app-version", "--fake-codex",
    };

    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> flags = new(StringComparer.Ordinal);

    public string Verb { get; private set; } = string.Empty;

    public string Data => values["--data"];

    public static CommandLine Parse(string[] args)
    {
        var line = new CommandLine();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (Flags.Contains(arg))
            {
                line.flags.Add(arg);
            }
            else if (Valued.Contains(arg))
            {
                if (i + 1 >= args.Length)
                    throw new FakeDeviceException($"{arg} 後面要有值");
                line.values[arg] = args[++i];
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new FakeDeviceException($"不認得的選項：{arg}");
            }
            else if (line.Verb.Length == 0)
            {
                line.Verb = arg;
            }
            else
            {
                throw new FakeDeviceException($"多出來的參數：{arg}");
            }
        }

        if (!line.values.ContainsKey("--data"))
            throw new FakeDeviceException("一定要有 --data <資料根>（不碰正常使用的資料夾）");
        if (line.Verb.Length == 0)
            throw new FakeDeviceException("沒有指令");
        if (!Verbs.Contains(line.Verb))
            throw new FakeDeviceException($"不認得的指令：{line.Verb}");
        // 記憶體金鑰在程序結束時就沒了：只給同一個程序裡配對、跑、解除配對的 session
        if (line.Has("--ephemeral-key") && line.Verb != "session")
            throw new FakeDeviceException("--ephemeral-key 只能用在 session（程序結束金鑰就沒了，留下的配對不能再用）");
        // 控制通道（WP4.9 步 11）：只有 run 讀標準輸入的指令
        if (line.Has("--control-stdin") && line.Verb != "run")
            throw new FakeDeviceException("--control-stdin 只能用在 run");
        if (line.Value("--app-version") is { } version && !IsAppVersion(version))
            throw new FakeDeviceException($"--app-version 要是 1～64 個英數字、點、加號或減號：{version}");
        // 假 Codex app-server（WP4.9 步 13）：只有會跑劇本或控制通道的 run、session 用得到
        if (line.Value("--fake-codex") is { } scenario)
        {
            if (line.Verb is not ("run" or "session"))
                throw new FakeDeviceException("--fake-codex 只能用在 run、session");
            if (FakeCodexAppServerScript.CheckScenarioName(scenario) is { } problem)
                throw new FakeDeviceException("--fake-codex：" + problem);
        }

        return line;
    }

    private static bool IsAppVersion(string value) =>
        value.Length is >= 1 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-');

    public bool Has(string flag) => flags.Contains(flag);

    public string? Value(string key) => values.GetValueOrDefault(key);

    public int Int(string key, int fallback) =>
        values.TryGetValue(key, out var text) ? int.TryParse(text, out var n) ? n : throw new FakeDeviceException($"{key} 要是整數：{text}") : fallback;

    public TimeSpan Seconds(string key, int fallback) => TimeSpan.FromSeconds(Math.Clamp(Int(key, fallback), 1, 3600));
}
