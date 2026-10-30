using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.Sync.Pairing;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>中樞的同步掛鉤（ISyncBridge）：轉送的時機與內容、未配對時一切照舊、伺服器的值不回送</summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubSyncHookTests
{
    /// <summary>記錄中樞呼叫了什麼；呼叫當下順便讀 AccountManager（確認是套用之後才轉）</summary>
    private sealed class FakeSyncBridge(HubHarness h) : ISyncBridge
    {
        public List<(string ViewId, string Type, JObject Msg)> Commands { get; } = [];

        public List<(string Id, string Label)> Updated { get; } = [];

        public List<IReadOnlyList<string>> Reordered { get; } = [];

        public List<(string Key, JToken Value)> Roaming { get; } = [];

        public JObject? Remote { get; set; }

        public JObject? Cli { get; set; }

        public bool HandleCommand(IAgentView view, string type, JObject msg)
        {
            Commands.Add((view.Id, type, (JObject)msg.DeepClone()));
            return true;
        }

        public void OnAccountUpdated(string accountId) => Updated.Add((accountId, h.Accounts.Find(accountId)!.Label));

        public void OnAccountsReordered() => Reordered.Add(h.Accounts.Accounts.Select(a => a.Id).ToList());

        public void OnRoamingSettingChanged(string key, JToken value) => Roaming.Add((key, value.DeepClone()));

        public JObject? InitRemote() => Remote;

        public bool IsStarting { get; set; }

        public JObject? DevicesCli() => Cli;

        public void DecorateAccounts(JArray accounts)
        {
            foreach (var item in accounts.OfType<JObject>())
                item["logicalId"] = "la_FAKE";
        }

        /// <summary>ClaimCatalogAccount 收到的 id（不是字串時是 null）</summary>
        public List<string?> Claims { get; } = [];

        /// <summary>
        /// 「在這台登入」的結果：預設照 AccountIdentityRules.CheckCatalogClaim（當成已配對、目錄裡是 Claude、這台還沒連結也沒宣告），
        /// 通過時回 <see cref="Template"/>
        /// </summary>
        public Func<string?, (CatalogAccountTemplate? Template, string Failure)>? Claim { get; set; }

        public CatalogAccountTemplate Template { get; set; } = new(ValidLogical, AgentProvider.Claude, "目錄的帳戶", "#abc", "me@example.com");

        /// <summary>ExpectedEmail 的結果（本機帳戶 id → 信箱）</summary>
        public Dictionary<string, string> Emails { get; } = [];

        public CatalogAccountTemplate? ClaimCatalogAccount(string? logicalAccountId, out string failure)
        {
            Claims.Add(logicalAccountId);
            if (Claim is { } custom)
            {
                var (template, text) = custom(logicalAccountId);
                failure = text;
                return template;
            }

            var check = AccountIdentityRules.CheckCatalogClaim(logicalAccountId, true, AgentProvider.Claude, false, false);
            failure = check == CatalogClaimCheck.Ok ? string.Empty : AccountIdentityRules.DescribeClaimFailure(check);
            return check == CatalogClaimCheck.Ok ? Template with { LogicalAccountId = logicalAccountId! } : null;
        }

        public string? ExpectedEmail(string localAccountId) => Emails.TryGetValue(localAccountId, out var email) ? email : null;
    }

    private const string ValidLogical = "la_0123456789ABCDEFGHJKMNPQRS";

    /// <summary>假的 claude（.cmd）：每次呼叫的參數記進 calls.txt；auth login 等 30 秒（模擬登入視窗開著）</summary>
    private const string FakeClaudeScript = """
        @echo off
        >>"%~dp0calls.txt" echo %*
        if "%~1"=="auth" if "%~2"=="login" ping -n 30 127.0.0.1 >nul
        exit /b 0
        """;

    private static (HubHarness H, FakeSyncBridge Bridge) WithBridge()
    {
        var h = new HubHarness();
        var bridge = new FakeSyncBridge(h);
        h.Ui.Invoke(() => h.Hub.AttachSync(bridge));
        return (h, bridge);
    }

    // ───── 在這台登入（WP3.4：accountAdd {logicalAccountId}、accountLogin 帶預期信箱） ─────

    [Fact]
    public async Task accountAdd帶logicalAccountId_名稱顏色取自目錄_宣告存進accounts_json_開始登入()
    {
        var (h, bridge) = WithBridge();
        using var _ = h;
        h.Ui.Invoke(() => h.Accounts.ResolveLauncher = _ => null);
        var mark = h.Main.Mark();

        h.Command(h.Main, "accountAdd", new { provider = "Codex", label = "網頁的名稱", color = "#000000", logicalAccountId = ValidLogical });

        bridge.Claims.Should().Equal(ValidLogical);
        var added = h.OnUi(() => h.Accounts.Accounts.Single(a => a.LogicalAccountId == ValidLogical));
        added.Provider.Should().Be(AgentProvider.Claude, "種類取自目錄，不看網頁的 provider");
        added.Label.Should().Be("目錄的帳戶");
        added.Color.Should().Be("#AABBCC", "目錄的 #RGB 展開成 #RRGGBB");
        added.IsSystemDefault.Should().BeFalse("一律建隔離帳戶");
        h.OnUi(() => h.Accounts.Accounts.Count).Should().Be(4);
        var saved = JsonFile.Read<List<AgentAccount>>(AppPaths.AccountsFile)!.Single(a => a.Id == added.Id);
        saved.LogicalAccountId.Should().Be(ValidLogical, "宣告存進 accounts.json");
        var login = await h.Main.WaitForAsync("accountLogin", m => m.Value<string>("accountId") == added.Id && m.Value<bool>("done"), mark, timeoutMs: 5000);
        login.Value<string>("message").Should().Contain("尚未安裝", "接著開始登入（測試沒有 CLI，立刻以未安裝結束）");
        h.Main.Messages("toast", mark).Should().BeEmpty();
    }

    [Fact]
    public void 宣告被拒_不新增_只對送指令的視窗toast()
    {
        var (h, bridge) = WithBridge();
        using var _ = h;
        h.Ui.Invoke(() => h.Accounts.ResolveLauncher = _ => null);
        var second = h.AddView("second");
        bridge.Claim = _ => (null, "這台已經登入這個帳戶了");
        var mainMark = h.Main.Mark();
        var secondMark = second.Mark();

        h.Command(second, "accountAdd", new { provider = "Claude", logicalAccountId = ValidLogical });

        h.OnUi(() => h.Accounts.Accounts.Count).Should().Be(3);
        second.Messages("toast", secondMark).Should().ContainSingle().Which.Value<string>("text").Should().Be("這台已經登入這個帳戶了");
        second.Messages("toast", secondMark)[0].Value<string>("level").Should().Be("error");
        h.Main.Messages("toast", mainMark).Should().BeEmpty("只對送指令的視窗提示");
    }

    [Fact]
    public void logicalAccountId不是字串或格式不對_不新增_不落回一般新增()
    {
        var (h, bridge) = WithBridge();
        using var _ = h;
        h.Ui.Invoke(() => h.Accounts.ResolveLauncher = _ => null);
        var mark = h.Main.Mark();

        h.Command(h.Main, "accountAdd", new { provider = "Claude", label = "x", logicalAccountId = 123 });
        h.Command(h.Main, "accountAdd", new { provider = "Claude", label = "x", logicalAccountId = new { id = ValidLogical } });
        h.Command(h.Main, "accountAdd", new { provider = "Claude", label = "x", logicalAccountId = "la_PROBE0000000000000000000000" });
        h.Command(h.Main, "accountAdd", new { provider = "Claude", label = "x", logicalAccountId = "" });

        bridge.Claims.Should().Equal(null, null, "la_PROBE0000000000000000000000", "");
        h.OnUi(() => h.Accounts.Accounts.Count).Should().Be(3, "帶了 logicalAccountId 就一律走宣告，不合格不新增");
        h.Main.Messages("toast", mark).Select(t => t.Value<string>("text"))
            .Should().Equal(Enumerable.Repeat(AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.InvalidId), 4));
    }

    [Fact]
    public void 沒有同步橋_帶logicalAccountId_不新增()
    {
        using var h = new HubHarness();
        h.Ui.Invoke(() => h.Accounts.ResolveLauncher = _ => null);

        h.Command(h.Main, "accountAdd", new { provider = "Claude", label = "x", logicalAccountId = ValidLogical });

        h.OnUi(() => h.Accounts.Accounts.Count).Should().Be(3);
        h.Main.Messages("toast").Should().ContainSingle().Which.Value<string>("text")
            .Should().Be(AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.NotPaired));
    }

    [Fact]
    public void logicalAccountId是null或沒帶_照舊新增()
    {
        var (h, bridge) = WithBridge();
        using var _ = h;
        h.Ui.Invoke(() => h.Accounts.ResolveLauncher = _ => null);

        h.Command(h.Main, "accountAdd", new { provider = "Codex", label = "一般新增", logicalAccountId = (string?)null });
        h.Command(h.Main, "accountAdd", new { provider = "Claude", label = "沒帶宣告" });

        bridge.Claims.Should().BeEmpty();
        var labels = h.OnUi(() => h.Accounts.Accounts.Skip(3).Select(a => (a.Label, a.Provider, a.LogicalAccountId)).ToList());
        labels.Should().Equal(("一般新增", AgentProvider.Codex, (string?)null), ("沒帶宣告", AgentProvider.Claude, (string?)null));
    }

    [Fact]
    public async Task 重新登入已對應的Claude帳戶_auth_login帶目錄的信箱()
    {
        var (h, bridge) = WithBridge();
        using var _ = h;
        var fakeDir = h.Temp.CreateDirectory("fake-claude");
        File.WriteAllText(Path.Combine(fakeDir, "fake.cmd"), FakeClaudeScript.ReplaceLineEndings("\r\n"));
        var launcher = new AgentLauncher(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", Path.Combine(fakeDir, "fake.cmd")]);
        Directory.CreateDirectory(h.Claude2.ConfigDirectory!);
        h.Ui.Invoke(() => h.Accounts.ResolveLauncher = _ => launcher);
        bridge.Emails["acc-claude2"] = "me@example.com";
        var mark = h.Main.Mark();

        h.Command(h.Main, "accountLogin", new { accountId = "acc-claude2" });
        await WaitUntilAsync(() => Calls(fakeDir).Contains("auth login --claudeai --email me@example.com"), "登入帶目錄的信箱");
        h.Ui.Invoke(() => h.Accounts.CancelLogin("acc-claude2"));
        await h.Main.WaitForAsync("accountLogin", m => m.Value<string>("accountId") == "acc-claude2" && m.Value<bool>("done"), mark, timeoutMs: 20000);

        Calls(fakeDir).Where(c => c.StartsWith("auth login", StringComparison.Ordinal)).Should().ContainSingle();
    }

    /// <summary>假 claude 被呼叫的參數（批次檔可能正在附加，用共用模式讀）</summary>
    private static List<string> Calls(string fakeDir)
    {
        var file = Path.Combine(fakeDir, "calls.txt");
        if (!File.Exists(file))
            return [];
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new Xunit.Sdk.XunitException($"{timeoutMs} ms 內沒有等到：{because}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public void 未知指令_轉給同步橋()
    {
        var (h, bridge) = WithBridge();
        using var _ = h;

        h.Command(h.Main, "syncRetry");
        h.Command(h.Main, "deviceRename", new { deviceId = "d_X", name = "家裡" });
        h.Command(h.Main, "ready");

        bridge.Commands.Select(c => (c.ViewId, c.Type)).Should().Equal(("main", "syncRetry"), ("main", "deviceRename"));
        bridge.Commands[1].Msg.Value<string>("name").Should().Be("家裡");
        h.Main.Messages("toast").Should().BeEmpty();
    }

    [Fact]
    public void 沒有同步橋_未知指令照舊忽略()
    {
        using var h = new HubHarness();

        h.Command(h.Main, "syncPair", new { deviceName = "OFFICE-PC" });
        h.Command(h.Main, "whatever");
        h.Command(h.Main, "ready");

        h.Main.Messages("toast").Should().BeEmpty();
        h.Ui.Errors.Should().BeEmpty();
        var init = h.Main.Last("init")!;
        init["remote"]!.Type.Should().Be(JTokenType.Null);
        init["devicesCli"]!.Type.Should().Be(JTokenType.Null);
        init.Value<bool>("syncStarting").Should().BeFalse("沒有同步橋就沒有啟動中");
    }

    [Fact]
    public void accountUpdate_處理後轉OnAccountUpdated()
    {
        var (h, bridge) = WithBridge();
        using var _ = h;

        h.Command(h.Main, "accountUpdate", new { accountId = "acc-claude2", label = "副帳戶改名" });
        h.Command(h.Main, "accountUpdate", new { label = "沒有 id" });

        bridge.Updated.Should().Equal([("acc-claude2", "副帳戶改名")], "AccountManager 套用之後才轉（讀到的是新名稱）");
    }

    [Fact]
    public void accountReorder_處理後轉OnAccountsReordered()
    {
        var (h, bridge) = WithBridge();
        using var _ = h;

        h.Command(h.Main, "accountReorder", new { ids = new[] { "acc-codex", "acc-claude" } });

        bridge.Reordered.Should().ContainSingle().Which.Should().Equal("acc-codex", "acc-claude", "acc-claude2");
    }

    [Fact]
    public void setting_fontSize_轉JSON整數()
    {
        var (h, bridge) = WithBridge();
        using var _ = h;

        h.Command(h.Main, "setting", new { key = "fontSize", value = "16" });

        var (key, value) = bridge.Roaming.Should().ContainSingle().Subject;
        key.Should().Be("fontSize");
        value.Type.Should().Be(JTokenType.Integer);
        value.Value<long>().Should().Be(16);
        h.Settings.FontSize.Should().Be(16);
    }

    [Fact]
    public void setting_非漫遊鍵不轉()
    {
        var (h, bridge) = WithBridge();
        using var _ = h;

        h.Command(h.Main, "setting", new { key = "closeToTray", value = false });
        h.Command(h.Main, "setting", new { key = "autoUpdateCli", value = false });
        h.Command(h.Main, "setting", new { key = "instructions", value = "自訂說明" });

        bridge.Roaming.Should().BeEmpty();
        h.Settings.CloseToTray.Should().BeFalse();
    }

    [Fact]
    public void setting_值沒變不轉()
    {
        var (h, bridge) = WithBridge();
        using var _ = h;

        h.Command(h.Main, "setting", new { key = "theme", value = "dark" });
        h.Command(h.Main, "setting", new { key = "theme", value = "purple" });
        h.Command(h.Main, "setting", new { key = "enterToSend", value = true });
        h.Command(h.Main, "setting", new { key = "notifications", value = false });

        bridge.Roaming.Select(r => (r.Key, r.Value.ToString())).Should().Equal(("notifications", "False"));
    }

    [Fact]
    public void ApplyRoamingSetting_套用主題_不觸發OnRoamingSettingChanged()
    {
        var (h, bridge) = WithBridge();
        using var _ = h;

        h.Ui.Invoke(() =>
        {
            h.Hub.ApplyRoamingSetting("theme", new JValue("light"));
            h.Hub.ApplyRoamingSetting("fontSize", new JValue(17L));
        });

        h.Settings.Theme.Should().Be("light");
        h.Settings.FontSize.Should().Be(17);
        h.Host.Themes.All.Should().Equal("light");
        h.Main.Last("settings")!["settings"]!.Value<string>("theme").Should().Be("light");
        bridge.Roaming.Should().BeEmpty("伺服器的值不回送");
    }

    [Fact]
    public async Task 未配對_AccountView沒有logicalId等欄位_init的remote與devicesCli為null()
    {
        using var h = new HubHarness(alwaysPost: true);
        var paths = new SyncPaths(h.Temp.Combine("sync"));
        var coordinator = h.OnUi(() => new SyncCoordinator(h.Hub, h.Accounts, h.Host, h.Settings, paths,
            new SyncCoordinatorOptions(false, null, false, "0.5.0", "OFFICE-PC", "user"), TimeProvider.System,
            engineFactory: _ => throw new InvalidOperationException("未配對不該建引擎")));
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachSync(coordinator);
            coordinator.Start();
        });
        await coordinator.WhenStarted.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Ui.DrainAsync();

        h.Command(h.Main, "ready");
        var init = h.Main.Last("init")!;

        init["remote"]!.Type.Should().Be(JTokenType.Null);
        init["devicesCli"]!.Type.Should().Be(JTokenType.Null);
        foreach (var account in init["accounts"]!.Cast<JObject>())
        {
            account.Property("logicalId").Should().BeNull();
            account.Property("remoteOnly").Should().BeNull();
            account.Property("devices").Should().BeNull();
        }

        h.OnUi(() => coordinator.ShouldPoll(h.Claude)).Should().BeTrue("未配對時一律查");
    }

    [Fact]
    public async Task 有同步橋_cli訊息帶devicesCli()
    {
        var (h, bridge) = WithBridge();
        using var _ = h;
        bridge.Cli = new JObject { ["d_HOME"] = new JObject { ["Claude"] = new JObject { ["installed"] = true, ["version"] = "2.1.300" } } };
        bridge.Remote = new JObject { ["mode"] = "desktop" };
        var mark = h.Main.Mark();

        h.Ui.Invoke(() => h.Hub.RefreshSyncDecorations());
        var cli = await h.Main.WaitForAsync("cli", since: mark);
        var accounts = await h.Main.WaitForAsync("accounts", since: mark);
        h.Command(h.Main, "ready");

        cli["devicesCli"]!["d_HOME"]!["Claude"]!.Value<string>("version").Should().Be("2.1.300");
        accounts["accounts"]!.Should().OnlyContain(a => a.Value<string>("logicalId") == "la_FAKE");
        var init = h.Main.Last("init")!;
        JToken.DeepEquals(init["devicesCli"], bridge.Cli).Should().BeTrue();
        init["remote"]!.Value<string>("mode").Should().Be("desktop");
        init.Value<bool>("syncStarting").Should().BeFalse();

        bridge.IsStarting = true;
        h.Command(h.Main, "ready");
        h.Main.Last("init")!.Value<bool>("syncStarting").Should().BeTrue("init.syncStarting 照同步橋的 IsStarting");
    }

    [Fact]
    public void SetSyncTrayText_附加在系統匣提示文字_null時拿掉()
    {
        using var h = new HubHarness();

        h.Ui.Invoke(() => h.Hub.SetSyncTrayText("同步 離線（3 筆待送）"));
        var withSync = h.Host.Tray.All[^1].Tooltip;
        h.Ui.Invoke(() => h.Hub.SetSyncTrayText(null));
        var without = h.Host.Tray.All[^1].Tooltip;

        withSync.Should().StartWith("Clode Myriagent｜同步 離線（3 筆待送）");
        withSync.Length.Should().BeLessThanOrEqualTo(127);
        without.Should().NotContain("同步");
        h.OnUi(() => h.Hub.SyncTrayText).Should().BeNull();
    }

    [Fact]
    public async Task RemoteOnly功能打開_accounts在本機帳戶之後多一筆合成項目()
    {
        using var h = new HubHarness(alwaysPost: true);
        var paths = new SyncPaths(h.Temp.Combine("sync"));
        Directory.CreateDirectory(paths.Directory);
        var device = PairingTestData.Device();
        DeviceFile.Save(paths.DeviceFile, device);
        var engine = new RecordingSyncEngine();
        var coordinator = h.OnUi(() => new SyncCoordinator(h.Hub, h.Accounts, h.Host, h.Settings, paths,
            new SyncCoordinatorOptions(false, null, false, "0.5.0", "OFFICE-PC", "user"), TimeProvider.System, engineFactory: _ => engine));
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachSync(coordinator);
            coordinator.Start();
        });
        await coordinator.WhenStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var linked = SyncIds.NewLogicalAccountId();
        var remote = SyncIds.NewLogicalAccountId();
        AccountUpsertPayload Account(string logical, string? local) => new(logical, "Claude", $"claude:org:{logical}@example.com", "目錄的帳戶", "#D97757", 0,
            null, null, null, 1,
            local is null ? [] : [new AccountLinkView(device.DeviceId, local, CatalogSyncRules.ConfigKindIsolated, "ready", null, null, null, null, null, null, false, true, null, null, false)]);
        var view = new SyncCatalogView(device.DeviceId,
            new Dictionary<string, AccountUpsertPayload> { [linked] = Account(linked, "acc-claude2"), [remote] = Account(remote, null) },
            new Dictionary<string, DeviceUpsertPayload>(),
            [],
            new Dictionary<string, string> { ["acc-claude2"] = linked },
            new Dictionary<string, DeviceRevokedPayload>());
        var mark = h.Main.Mark();

        h.Ui.Invoke(() => coordinator.ApplyCatalog(new CatalogApply(view, [], null, [])));
        var accounts = await h.Main.WaitForAsync("accounts", m => m["accounts"]!.Any(a => a.Value<string>("logicalId") == linked), mark);

        SyncFeatures.RemoteOnlyAccounts.Should().BeTrue();
        accounts["accounts"]!.Select(a => a.Value<string>("id")).Should().Equal("acc-claude", "acc-claude2", "acc-codex", remote);
        accounts["accounts"]!.Take(3).Should().OnlyContain(a => a.Value<bool>("remoteOnly") == false);
        var synthetic = accounts["accounts"]![3]!;
        synthetic.Value<bool>("remoteOnly").Should().BeTrue();
        synthetic.Value<string>("logicalId").Should().Be(remote, "合成項目的 id 就是邏輯帳戶 id");
        accounts["accounts"]![0]!.Value<string>("logicalId").Should().BeNull("沒連結");
        h.Ui.Invoke(() => coordinator.Dispose());
    }
}
