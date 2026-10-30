using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Tests.TestSupport;
using Myriagent.Usage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Accounts;

/// <summary>帳戶管理員（不跑 CLI、不開登入視窗：以 Seed 放入帳戶，AppPaths 指到暫存資料夾）</summary>
[Collection(AppPathsCollection.Name)]
public sealed class AccountManagerTests : IDisposable
{
    private readonly TempDirectory temp = new("accounts");
    private readonly AppPathsScope paths;
    private readonly HttpClient http = new(new StubHttpHandler());
    private readonly CliManager cli;
    private readonly AccountManager manager;
    private readonly AgentAccount claude;
    private readonly AgentAccount codex;
    private int changed;

    public AccountManagerTests()
    {
        paths = new AppPathsScope(temp.Root);
        cli = new CliManager(http, _ => false, () => false, startBackgroundChecks: false);
        manager = new AccountManager(action => action(), cli, http, _ => 0)
        {
            ResolveAntigravityConfigDirectory = _ => temp.Combine(".gemini", "antigravity-cli"),
        };
        manager.Changed += () => changed++;
        claude = new AgentAccount { Id = "c1", Provider = AgentProvider.Claude, Label = "Claude 本機" };
        codex = new AgentAccount { Id = "x1", Provider = AgentProvider.Codex, Label = "Codex 隔離", ConfigDirectory = Path.Combine(AppPaths.AccountsDirectory, "x1", "codex") };
        manager.Seed(new[] { claude, codex }, AccountStatus.Ready);
    }

    public void Dispose()
    {
        manager.Dispose();
        cli.Dispose();
        http.Dispose();
        paths.Dispose();
        temp.Dispose();
    }

    [Fact]
    public void Seed_放入帳戶與狀態()
    {
        manager.Accounts.Select(a => a.Id).Should().Equal("c1", "x1");
        manager.GetState("c1").Status.Should().Be(AccountStatus.Ready);
        manager.GetState("c1").StatusText.Should().Be("已登入");
        manager.GetState("沒有的帳戶").Status.Should().Be(AccountStatus.Checking, "沒見過的帳戶先當作檢查中");
        manager.Find(null).Should().BeNull();
        manager.Find("x1").Should().BeSameAs(codex);
    }

    [Fact]
    public void Update_名稱去空白並限制長度_顏色要合法_存檔並通知()
    {
        manager.Update("x1", "  " + new string('名', 50) + "  ", "#19c3a6", safeMode: true);

        codex.Label.Should().Be(new string('名', 40));
        codex.Color.Should().Be("#19C3A6");
        codex.SafeMode.Should().BeTrue();
        changed.Should().Be(1);
        JsonFile.Read<List<AgentAccount>>(AppPaths.AccountsFile)!.Single(a => a.Id == "x1").SafeMode.Should().BeTrue();

        manager.Update("x1", " ", "不是顏色", null);
        codex.Label.Should().Be(new string('名', 40), "空白名稱不改");
        codex.Color.Should().Be("#19C3A6");
        codex.SafeMode.Should().BeTrue();
        manager.Update("沒有的帳戶", "x", null, null);
    }

    [Fact]
    public void Update_共用設定_預設開_只改隔離帳戶_存檔()
    {
        codex.ShareCustomizations.Should().BeTrue("預設共用");
        JsonConvert.DeserializeObject<AgentAccount>("""{"Id":"old","Provider":"Claude","ConfigDirectory":"D:/accounts/old"}""", JsonFile.Settings)!
            .ShareCustomizations.Should().BeTrue("舊版的 accounts.json 沒有這個欄位");

        manager.Update("x1", null, null, null, shareCustomizations: false);
        codex.ShareCustomizations.Should().BeFalse();
        JsonFile.Read<List<AgentAccount>>(AppPaths.AccountsFile)!.Single(a => a.Id == "x1").ShareCustomizations.Should().BeFalse();

        manager.Update("c1", null, null, null, shareCustomizations: false);
        claude.ShareCustomizations.Should().BeTrue("系統預設帳戶本身就是來源");
        manager.Update("x1", null, null, null, shareCustomizations: true);
        codex.ShareCustomizations.Should().BeTrue();
    }

    [Fact]
    public void Antigravity_支援精簡模式_一台電腦只能有一個帳戶()
    {
        var agy = new AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity（本機）" };
        manager.Seed(new[] { agy }, AccountStatus.Ready);

        manager.Update("local-antigravity", null, null, safeMode: true);
        agy.SafeMode.Should().BeTrue("agy 1.4.6 支援 --disable-slash-commands 精簡模式");
        JsonFile.Read<List<AgentAccount>>(AppPaths.AccountsFile)!.Single(a => a.Id == "local-antigravity").SafeMode.Should().BeTrue();

        var add = () => manager.Add(AgentProvider.Antigravity, "第二個", null);
        add.Should().Throw<InvalidOperationException>().WithMessage("*一台電腦只能登入一個帳戶*");
        manager.EnsureSystemDefault(AgentProvider.Antigravity).Should().BeTrue();
        manager.Accounts.Count(a => a.Provider == AgentProvider.Antigravity).Should().Be(1, "已經有就不再加");
    }

    [Fact]
    public void Reorder_照指定順序_沒列到的接在後面_不認得的略過()
    {
        manager.Reorder(new[] { "x1", "不存在" });

        manager.Accounts.Select(a => a.Id).Should().Equal("x1", "c1");
        manager.Accounts.Select(a => a.Order).Should().Equal(0, 1);
    }

    [Fact]
    public void UpdateAccountInfo_Email會更新_方案只在還沒有時補上()
    {
        manager.UpdateAccountInfo("c1", "me@example.com", "Claude Max");
        claude.Email.Should().Be("me@example.com");
        claude.Plan.Should().NotBeNullOrEmpty();
        changed.Should().Be(1);

        claude.Plan = "Max 20x";
        manager.UpdateAccountInfo("c1", "me@example.com", "Claude Pro");
        claude.Plan.Should().Be("Max 20x", "憑證檔讀到的詳細方案優先");
        changed.Should().Be(1, "沒有變化不通知");
    }

    [Fact]
    public void Remove_隔離帳戶在背景刪除資料夾_系統預設帳戶不刪()
    {
        Directory.CreateDirectory(codex.ConfigDirectory!);
        File.WriteAllText(Path.Combine(codex.ConfigDirectory!, "auth.json"), "{}");

        manager.Remove("x1", deleteData: true).Should().BeTrue();

        manager.Find("x1").Should().BeNull();
        JsonFile.Read<List<AgentAccount>>(AppPaths.AccountsFile)!.Select(a => a.Id).Should().Equal("c1");
        SpinWait.SpinUntil(() => !Directory.Exists(Path.Combine(AppPaths.AccountsDirectory, "x1")), TimeSpan.FromSeconds(5))
            .Should().BeTrue("隔離帳戶的資料夾（含登入憑證）在背景刪除");
        manager.Remove("c1", deleteData: true).Should().BeTrue("系統預設帳戶的 ~/.claude 一律不刪，只移出清單");
        manager.Accounts.Should().BeEmpty();
        manager.Remove("沒有的帳戶", deleteData: true).Should().BeTrue();
    }

    [Fact]
    public void Remove_不刪資料時資料夾留著()
    {
        Directory.CreateDirectory(codex.ConfigDirectory!);

        manager.Remove("x1", deleteData: false);

        Thread.Sleep(200);
        Directory.Exists(codex.ConfigDirectory).Should().BeTrue();
    }

    [Fact]
    public void GetDeletableAccountRoot_只認App建立的Accounts子資料夾()
    {
        AccountManager.GetDeletableAccountRoot(codex).Should().Be(Path.Combine(AppPaths.AccountsDirectory, "x1"));
        AccountManager.GetDeletableAccountRoot(claude).Should().BeNull("系統預設帳戶");
        AccountManager.GetDeletableAccountRoot(new AgentAccount { Id = "x1", Provider = AgentProvider.Codex, ConfigDirectory = Path.Combine(AppPaths.AccountsDirectory, "x1", "codex") + "\\" })
            .Should().Be(Path.Combine(AppPaths.AccountsDirectory, "x1"), "結尾斜線不影響");
        AccountManager.GetDeletableAccountRoot(new AgentAccount { Id = "x1", Provider = AgentProvider.Codex, ConfigDirectory = Path.Combine(AppPaths.AccountsDirectory, "別人", "codex") })
            .Should().BeNull("資料夾名稱要等於帳戶 Id");
        AccountManager.GetDeletableAccountRoot(new AgentAccount { Id = "x1", Provider = AgentProvider.Codex, ConfigDirectory = @"D:\my-codex\x1\codex" })
            .Should().BeNull("不在 app 的 accounts 資料夾底下（手動改過的設定）");
        AccountManager.GetDeletableAccountRoot(new AgentAccount { Id = "accounts", Provider = AgentProvider.Codex, ConfigDirectory = AppPaths.AccountsDirectory })
            .Should().BeNull("不能刪到 accounts 資料夾本身或它的上一層");
    }

    [Fact]
    public void RecoverIsolatedAccounts_從資料夾找回App建立過的帳戶()
    {
        var claudeId = new string('a', 32);
        var codexId = "0123456789abcdef0123456789ABCDEF";
        Directory.CreateDirectory(Path.Combine(AppPaths.AccountsDirectory, claudeId, "claude"));
        Directory.CreateDirectory(Path.Combine(AppPaths.AccountsDirectory, codexId, "codex"));
        Directory.CreateDirectory(Path.Combine(AppPaths.AccountsDirectory, "not-an-id", "claude"));
        Directory.CreateDirectory(Path.Combine(AppPaths.AccountsDirectory, new string('b', 32), "其他"));

        var recovered = AccountManager.RecoverIsolatedAccounts();

        recovered.Select(a => (a.Id, a.Provider)).Should().BeEquivalentTo(new[] { (claudeId, AgentProvider.Claude), (codexId, AgentProvider.Codex) });
        var restored = recovered.Single(a => a.Id == claudeId);
        restored.ConfigDirectory.Should().Be(Path.Combine(AppPaths.AccountsDirectory, claudeId, "claude"));
        restored.Label.Should().Be("Claude 帳戶（還原 aaaaaa）");
        restored.IsSystemDefault.Should().BeFalse();
    }

    [Fact]
    public void RecoverIsolatedAccounts_沒有Accounts資料夾回空清單()
    {
        Directory.Exists(AppPaths.AccountsDirectory).Should().BeFalse();
        AccountManager.RecoverIsolatedAccounts().Should().BeEmpty();
    }

    [Fact]
    public void 移除時沒刪乾淨的資料夾_不會被當成帳戶還原_下次啟動再刪()
    {
        var id = new string('c', 32);
        var dir = Path.Combine(AppPaths.AccountsDirectory, id);
        Directory.CreateDirectory(Path.Combine(dir, "codex"));
        File.WriteAllText(dir + ".deleting", id);
        var other = Path.Combine(AppPaths.AccountsDirectory, "not-an-id");
        Directory.CreateDirectory(other);
        File.WriteAllText(other + ".deleting", "x");

        AccountManager.RecoverIsolatedAccounts().Should().BeEmpty("已移除、等著刪的資料夾不是帳戶");

        AccountManager.RetryPendingDeletes();

        Directory.Exists(dir).Should().BeFalse("啟動時再刪一次");
        File.Exists(dir + ".deleting").Should().BeFalse("刪掉了就清掉標記");
        Directory.Exists(other).Should().BeTrue("名稱不是帳戶 Id 的一律不碰");
    }

    private static UsageSnapshot SnapshotWith(params string[] keys)
        => new(DateTimeOffset.UtcNow, "api", keys.Select(k => new UsageWindow(k, k, 10, null)).ToList(), null, null, null, false);

    [Fact]
    public void CreateSystemDefaults_第一次用固定Id_帳戶清單遺失時從用量歷史找回舊版的隨機Id()
    {
        manager.CreateSystemDefaults(Array.Empty<AgentAccount>()).Select(a => (a.Id, a.Provider, a.IsSystemDefault))
            .Should().Equal(("local-claude", AgentProvider.Claude, true), ("local-codex", AgentProvider.Codex, true), ("local-antigravity", AgentProvider.Antigravity, true), ("local-grok", AgentProvider.Grok, true));

        var claudeLegacy = new string('a', 32);
        var codexLegacy = new string('b', 32);
        var isolated = new string('c', 32);
        var store = new UsageHistoryStore(AppPaths.UsageDirectory);
        store.Record(claudeLegacy, SnapshotWith("session", "weekly", "weekly:Fable"));
        store.Record(codexLegacy, SnapshotWith("codex:10080"));
        store.Record(isolated, SnapshotWith("session", "weekly"));
        store.Flush();
        var recovered = new[] { new AgentAccount { Id = isolated, Provider = AgentProvider.Claude, ConfigDirectory = Path.Combine(AppPaths.AccountsDirectory, isolated, "claude") } };

        manager.CreateSystemDefaults(recovered).Select(a => a.Id).Should().Equal(new[] { claudeLegacy, codexLegacy, "local-antigravity", "local-grok" },
            "用量歷史檔在移除帳戶時會一起刪掉：不屬於隔離帳戶的就是原本的本機預設帳戶，對話接得回來");
    }

    [Fact]
    public void CreateSystemDefaults_找到不只一個可能的舊Id或已有固定Id時用固定Id()
    {
        var store = new UsageHistoryStore(AppPaths.UsageDirectory);
        store.Record(new string('a', 32), SnapshotWith("session"));
        store.Record(new string('d', 32), SnapshotWith("weekly"));
        store.Record("local-codex", SnapshotWith("codex:10080"));
        store.Record(new string('b', 32), SnapshotWith("codex:300"));
        store.Flush();

        manager.CreateSystemDefaults(Array.Empty<AgentAccount>()).Select(a => a.Id).Should().Equal(new[] { "local-claude", "local-codex", "local-antigravity", "local-grok" },
            "兩個 Claude 歷史分不出是哪個就不猜；Codex 已經有固定 Id 的歷史就沿用它");
    }

    [Fact]
    public void ReportSessionRateLimit_Claude被拒時鎖定並合併_記錄走勢點()
    {
        manager.SeedUsage("c1", new UsageSnapshot(DateTimeOffset.UtcNow.AddMinutes(-5), "api",
            new[] { new UsageWindow("weekly:Fable", "每週・Fable", 3, null) }, null, "已啟用額外用量", null, false));

        manager.ReportSessionRateLimit("c1", AgentProvider.Claude, JObject.Parse("""{"status":"rejected","rateLimitType":"five_hour","resetsAt":1791436800,"unifiedWindows":{"five_hour":{"utilization":1.0,"resetsAt":1791436800}}}"""));

        var usage = manager.GetUsage("c1")!;
        usage.Source.Should().Be("session");
        usage.Note.Should().Be("已啟用額外用量", "沿用原本的說明");
        usage.Windows.Select(w => $"{w.Key}:{w.Percent}:{w.Locked}").Should().Equal("session:100:True", "weekly:Fable:3:False");
        UsageRules.Severity(usage.Windows[0].Percent, usage.Windows[0].Locked).Should().Be("max");
        manager.GetHistory("c1", "session").Should().ContainSingle();
        changed.Should().Be(1);
    }

    [Fact]
    public void ReportSessionRateLimit_沒有可用資料或帳戶不存在時不動()
    {
        manager.ReportSessionRateLimit("c1", AgentProvider.Claude, JObject.Parse("""{"status":"allowed"}"""));
        manager.ReportSessionRateLimit("不存在", AgentProvider.Codex, JObject.Parse("""{"primary":{"usedPercent":5,"windowDurationMins":300}}"""));

        manager.GetUsage("c1").Should().BeNull();
        manager.GetUsage("不存在").Should().BeNull();
        changed.Should().Be(0);
    }

    [Fact]
    public void Dispose時把用量歷史寫回磁碟()
    {
        manager.ReportSessionRateLimit("x1", AgentProvider.Codex, JObject.Parse("""{"limitId":"codex","primary":{"usedPercent":42,"windowDurationMins":10080,"resetsAt":1791959804}}"""));
        File.Exists(Path.Combine(AppPaths.UsageDirectory, "x1.json")).Should().BeFalse("平常由計時器定期寫入");

        manager.Dispose();

        File.Exists(Path.Combine(AppPaths.UsageDirectory, "x1.json")).Should().BeTrue();
        new UsageHistoryStore(AppPaths.UsageDirectory).GetLastSnapshot("x1")!.Windows.Single().Percent.Should().Be(42);
    }

    [Fact]
    public void ReportTurnCompleted_不存在的帳戶不處理()
    {
        manager.ReportTurnCompleted("不存在");
        manager.ReportTurnCompleted("c1");
        changed.Should().Be(0, "只排定下一次查詢，不立即通知");
    }

    [Fact]
    public async Task RefreshAntigravity_第一次查模型與額度_模型已知時只跑一次usage_同步閘門擋下時只查models()
    {
        var agy = new AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity（本機）" };
        manager.Seed(new[] { agy }, AccountStatus.Checking);

        var callsFile = temp.Combine("agy-calls.txt");
        var emailFile = temp.WriteFile("agy-email.txt", "first@example.com");
        var script = temp.WriteFile("fake-agy.ps1", """
            $joined = $args -join ' '
            Add-Content -Path $env:FAKE_AGY_CALLS -Value $joined -Encoding UTF8
            $email = (Get-Content -Path $env:FAKE_AGY_EMAIL -Raw).Trim()
            for ($i = 0; $i -lt $args.Length - 1; $i++) {
                if ($args[$i] -eq '--log-file') {
                    Set-Content -Path $args[$i + 1] -Value "I1023 22:00:00.000000 1 server_oauth.go:229] OAuth: authenticated successfully as $email" -Encoding UTF8
                }
            }
            if ($args -contains 'models') {
                Write-Output "gemini-3.8-flash-low`tGemini 3.8 Flash (Low)"
                exit 0
            }
            if ($args -contains '/usage') {
                Write-Output '{"conversation_id":"","status":"SUCCESS","command":{"name":"usage","data":{"groups":[{"name":"Gemini Models","buckets":[{"id":"gemini-5h","window":"5h","remaining_fraction":0.75,"reset_time":"2026-10-24T00:00:00Z"},{"id":"gemini-weekly","window":"weekly","remaining_fraction":0.9,"reset_time":"2026-10-30T00:00:00Z"}]}]}}}'
                exit 0
            }
            exit 1
            """);
        Environment.SetEnvironmentVariable("FAKE_AGY_CALLS", callsFile);
        Environment.SetEnvironmentVariable("FAKE_AGY_EMAIL", emailFile);
        try
        {
            var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            manager.ResolveLauncher = p => p == AgentProvider.Antigravity
                ? new AgentLauncher(ps, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
                : null;

            bool needModels = true;
            AccountCapabilities? discovered = null;
            manager.NeedsCapabilities = id => id == agy.Id && needModels;
            manager.CapabilitiesDiscovered += (_, cap) => { discovered = cap; needModels = false; };

            // 1. 第一次還沒有模型清單：跑 models（帶 --log-file）＋ -p /usage
            await manager.RefreshAsync(agy.Id, includeStatus: true);
            manager.GetState(agy.Id).Status.Should().Be(AccountStatus.Ready);
            agy.Email.Should().Be("first@example.com");
            discovered.Should().NotBeNull();
            discovered!.Models.Select(m => m.Value).Should().Equal("gemini-3.8-flash");
            var u1 = manager.GetUsage(agy.Id)!;
            u1.Source.Should().Be("api");
            u1.Windows.Select(w => (w.Key, w.Label, w.Percent)).Should().Equal(
                ("agy-gemini-5h", "5 小時・Gemini", 25),
                ("agy-gemini-weekly", "每週・Gemini", 10));

            var calls1 = File.ReadAllLines(callsFile);
            calls1.Should().HaveCount(2);
            calls1[0].Should().Contain("--log-file").And.EndWith("models");
            calls1[1].Should().Be("-p /usage --output-format json");

            // 2. 第二次模型清單已知：只跑一次 --log-file ... -p /usage，同時更新信箱與額度
            File.Delete(callsFile);
            File.WriteAllText(emailFile, "second@example.com");
            await manager.RefreshAsync(agy.Id, includeStatus: true);
            agy.Email.Should().Be("second@example.com");
            var calls2 = File.ReadAllLines(callsFile);
            calls2.Should().ContainSingle();
            calls2[0].Should().Contain("--log-file").And.EndWith("-p /usage --output-format json");

            // 3. 多電腦同步由別台負責查用量：只跑 models、不跑 /usage，保留共享用量
            File.Delete(callsFile);
            var shared = new UsageSnapshot(DateTimeOffset.UtcNow, "shared",
                new[] { new UsageWindow("agy-gemini-5h", "5 小時・Gemini", 42, null) }, null, null, null, false);
            manager.SeedUsage(agy.Id, shared);
            manager.UsagePollPolicy = new NeverPollPolicy();
            await manager.RefreshAsync(agy.Id, includeStatus: true);
            var calls3 = File.ReadAllLines(callsFile);
            calls3.Should().ContainSingle();
            calls3[0].Should().EndWith("models");
            manager.GetUsage(agy.Id)!.Source.Should().Be("shared");
            manager.GetUsage(agy.Id)!.Windows.Single().Percent.Should().Be(42);

            // 4. 一輪對話完成後 10 秒排定重查
            manager.SeedNextPoll(agy.Id, DateTimeOffset.UtcNow.AddMinutes(15));
            manager.ReportTurnCompleted(agy.Id);
            manager.GetNextPoll(agy.Id)!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(10), TimeSpan.FromSeconds(5));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_AGY_CALLS", null);
            Environment.SetEnvironmentVariable("FAKE_AGY_EMAIL", null);
        }
    }

    [Fact]
    public async Task RefreshAntigravity_關閉自動更新與隔離空工作目錄_單次逾時自動重試成功_檢查失敗後短間隔重查恢復()
    {
        var agy = new AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity（本機）" };
        manager.Seed(new[] { agy }, AccountStatus.Checking);
        manager.AntigravityCommandTimeout = TimeSpan.FromMilliseconds(500);
        manager.UsagePollPolicy = new NeverPollPolicy();
        manager.NeedsCapabilities = _ => true;

        var fakeDir = temp.CreateDirectory("fake-agy-retry");
        var cmdPath = Path.Combine(fakeDir, "fake-agy.cmd");
        File.WriteAllText(cmdPath, """
            @echo off
            >>"%~dp0meta.txt" echo %AGY_CLI_DISABLE_AUTO_UPDATE%^|%CD%
            if exist "%~dp0hang-once.flag" (
              del /q "%~dp0hang-once.flag" >nul 2>&1
              ping -n 10 127.0.0.1 >nul
              exit /b 1
            )
            if exist "%~dp0hang-always.flag" (
              ping -n 10 127.0.0.1 >nul
              exit /b 1
            )
            if "%~1"=="--log-file" (
              >"%~2" echo I1023 22:00:00.000000 1 server_oauth.go:229] OAuth: authenticated successfully as retry@example.com
            )
            echo gemini-3.8-flash-low	Gemini 3.8 Flash (Low)
            exit /b 0
            """.ReplaceLineEndings("\r\n"));
        manager.ResolveLauncher = p => p == AgentProvider.Antigravity
            ? new AgentLauncher(Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/d", "/c", cmdPath })
            : null;

        // 1. 第一次卡住逾時，自動重試第二次成功：不亮紅燈，並帶 AGY_CLI_DISABLE_AUTO_UPDATE=true 與空的快取工作資料夾
        File.WriteAllText(Path.Combine(fakeDir, "hang-once.flag"), "1");
        await manager.RefreshAsync(agy.Id, includeStatus: true);
        manager.GetState(agy.Id).Status.Should().Be(AccountStatus.Ready);
        agy.Email.Should().Be("retry@example.com");

        var meta = File.ReadAllLines(Path.Combine(fakeDir, "meta.txt")).Where(l => l.Length > 0).ToList();
        meta.Should().HaveCount(2, "第一次逾時後自動重試一次");
        var expectedWorkDir = Path.Combine(AppPaths.CacheDirectory, "agy-empty");
        meta.Should().OnlyContain(l => string.Equals(l, "true|" + expectedWorkDir, StringComparison.OrdinalIgnoreCase));

        // 2. 兩次都逾時：記成檢查失敗，下次輪詢排在 1 分鐘退避重試（不等閒置的 15 分鐘）
        File.WriteAllText(Path.Combine(fakeDir, "hang-always.flag"), "1");
        await manager.RefreshAsync(agy.Id, includeStatus: true);
        manager.GetState(agy.Id).Status.Should().Be(AccountStatus.Error);
        manager.GetState(agy.Id).StatusText.Should().Be("檢查失敗：agy models 逾時沒有回應");
        manager.GetNextPoll(agy.Id)!.Value.Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(2));

        // 3. 輪詢到期（Tick）自動重查並恢復為已登入，之後回到正常的閒置輪詢間隔
        File.Delete(Path.Combine(fakeDir, "hang-always.flag"));
        manager.SeedNextPoll(agy.Id, DateTimeOffset.UtcNow.AddSeconds(-1));
        manager.Tick();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (manager.GetState(agy.Id).Status != AccountStatus.Ready && DateTime.UtcNow < deadline)
            await Task.Delay(30);
        manager.GetState(agy.Id).Status.Should().Be(AccountStatus.Ready);
        manager.GetNextPoll(agy.Id)!.Value.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(10));
    }

    [Fact]
    public async Task RefreshAntigravity_探索本機Skills進Commands_額度窗鎖定時自動補查credits_ReportQuotaExhausted立即重查()
    {
        var agy = new AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity（本機）" };
        manager.Seed(new[] { agy }, AccountStatus.Checking);
        temp.WriteFile(@".gemini\config\skills\my-skill\SKILL.md", "---\nname: my-skill\ndescription: 自訂技能\n---\n");

        var callsFile = temp.Combine("agy-locked-calls.txt");
        var script = temp.WriteFile("fake-agy-credits.ps1", """
            $joined = $args -join ' '
            Add-Content -Path $env:FAKE_AGY_CREDITS_CALLS -Value $joined -Encoding UTF8
            for ($i = 0; $i -lt $args.Length - 1; $i++) {
                if ($args[$i] -eq '--log-file') {
                    Set-Content -Path $args[$i + 1] -Value "I1023 22:00:00.000000 1 server_oauth.go:229] OAuth: authenticated successfully as locked@example.com" -Encoding UTF8
                }
            }
            if ($args -contains 'models') {
                Write-Output "gemini-3.8-flash-low`tGemini 3.8 Flash (Low)"
                exit 0
            }
            if ($args -contains '/usage') {
                Write-Output '{"conversation_id":"","status":"SUCCESS","command":{"name":"usage","data":{"groups":[{"name":"Gemini Models","buckets":[{"id":"gemini-5h","window":"5h","remaining_fraction":0,"reset_time":"2026-10-24T00:00:00Z"}]}]}}}'
                exit 0
            }
            if ($args -contains '/credits') {
                Write-Output '{"conversation_id":"","status":"SUCCESS","command":{"name":"credits","data":{"remaining_credits":250}}}'
                exit 0
            }
            exit 1
            """);
        Environment.SetEnvironmentVariable("FAKE_AGY_CREDITS_CALLS", callsFile);
        try
        {
            var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            manager.ResolveLauncher = p => p == AgentProvider.Antigravity
                ? new AgentLauncher(ps, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
                : null;

            AccountCapabilities? discovered = null;
            bool needCap = true;
            manager.NeedsCapabilities = id => id == agy.Id && needCap;
            manager.CapabilitiesDiscovered += (_, cap) => { discovered = cap; needCap = false; };

            await manager.RefreshAsync(agy.Id, includeStatus: true);
            discovered.Should().NotBeNull();
            discovered!.Commands.Select(c => c.Name).Should().Contain(new[] { "plan", "grill-me", "my-skill" });

            var snap = manager.GetUsage(agy.Id)!;
            snap.Windows.Single().Locked.Should().BeTrue();
            snap.Credits.Should().Be("AI 點數餘額 250", "有額度窗鎖定時才補查一次 /credits");

            File.Delete(callsFile);
            manager.SeedNextPoll(agy.Id, DateTimeOffset.UtcNow.AddMinutes(15));
            manager.ReportQuotaExhausted(agy.Id);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(callsFile) && DateTime.UtcNow < deadline)
                await Task.Delay(30);
            File.Exists(callsFile).Should().BeTrue("對話收到額度耗盡訊號時立即觸發背景重查");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_AGY_CREDITS_CALLS", null);
        }
    }

    private sealed class NeverPollPolicy : IUsagePollPolicy
    {
        public bool ShouldPoll(AgentAccount account) => false;
    }
}
