using System.Net;
using System.Text.Json;
using MyriagentHub.Admin;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Ops;
using MyriagentHub.Releases;
using MyriagentHub.Tests.Releases;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Ops;

/// <summary>管理收件匣（只能在主機上以管理員寫入）：reset-owner、drain、backup-now、verify-audit、app-releases、app-release-withdraw；服務沒在跑時逾時</summary>
public sealed class AdminInboxTests
{
    private const string ChangedPassword = "changed-password-1";

    [Fact]
    public async Task reset_owner把密碼改回預設_撤銷所有工作階段()
    {
        await using var owner = await OwnerHost.StartAsync();
        using var client = owner.Host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        using (var changed = await OwnerFlows.ChangePasswordAsync(client, OwnerAccount.DefaultPassword, ChangedPassword))
            changed.StatusCode.Should().Be(HttpStatusCode.OK);

        using var output = new StringWriter();
        var code = await AdminCommandLine.RunAsync(["reset-owner", "--data", owner.Host.DataDir, "--wait", "20"], output);
        code.Should().Be(AdminExitCodes.Ok, output.ToString());
        var result = JsonDocument.Parse(output.ToString()).RootElement;
        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.GetProperty("data").GetProperty("userName").GetString().Should().Be(OwnerAccount.UserName);
        result.GetProperty("data").GetProperty("revokedSessions").GetInt32().Should().Be(1);

        using (var me = await client.GetAsync("/api/v1/auth/me"))
            me.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "所有瀏覽器工作階段都撤銷");
        using (var old = await OwnerFlows.LoginAsync(client, ChangedPassword))
            old.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "改過的密碼不再有效");
        using (var reset = await OwnerFlows.LoginAsync(client))
            reset.StatusCode.Should().Be(HttpStatusCode.OK, "密碼改回預設");
        (Directory.GetFiles(owner.Host.Paths.AdminInboxDir, "*.json")).Should().BeEmpty("處理完的指令檔刪除");
    }

    [Fact]
    public async Task owner_reset是reset_owner的別名()
    {
        await using var owner = await OwnerHost.StartAsync();
        using var client = owner.Host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        (await OwnerFlows.ChangePasswordAsync(client, OwnerAccount.DefaultPassword, ChangedPassword)).Dispose();
        using var output = new StringWriter();
        var code = await AdminCommandLine.RunAsync(["owner-reset", "--data", owner.Host.DataDir, "--wait", "20"], output);
        code.Should().Be(AdminExitCodes.Ok, output.ToString());
        using var login = await OwnerFlows.LoginAsync(owner.Host.CreateClient());
        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task drain讓readyz回503_undrain恢復()
    {
        await using var owner = await OwnerHost.StartAsync();
        using var local = owner.Host.CreateClient(ip: "127.0.0.1", cookies: false);
        using (var ready = await local.GetAsync("/readyz"))
            ready.StatusCode.Should().Be(HttpStatusCode.OK);

        (await AdminCommandLine.RunAsync(["drain", "--data", owner.Host.DataDir, "--wait", "20"], TextWriter.Null)).Should().Be(AdminExitCodes.Ok);
        using (var draining = await local.GetAsync("/readyz"))
        {
            draining.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await draining.Content.ReadAsStringAsync()).Should().Contain("\"draining\":true");
        }
        using (var health = await local.GetAsync("/healthz"))
            health.StatusCode.Should().Be(HttpStatusCode.OK, "healthz 只看健康，不看排空");

        (await AdminCommandLine.RunAsync(["undrain", "--data", owner.Host.DataDir, "--wait", "20"], TextWriter.Null)).Should().Be(AdminExitCodes.Ok);
        using var again = await local.GetAsync("/readyz");
        again.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task backup_now與verify_audit()
    {
        await using var owner = await OwnerHost.StartAsync();
        using var output = new StringWriter();
        (await AdminCommandLine.RunAsync(["backup-now", "--data", owner.Host.DataDir, "--wait", "30"], output)).Should().Be(AdminExitCodes.Ok, output.ToString());
        var folder = JsonDocument.Parse(output.ToString()).RootElement.GetProperty("data").GetProperty("folder").GetString()!;
        File.Exists(Path.Combine(folder, "manifest.json")).Should().BeTrue();

        using var verify = new StringWriter();
        (await AdminCommandLine.RunAsync(["verify-audit", "--data", owner.Host.DataDir, "--wait", "20"], verify)).Should().Be(AdminExitCodes.Ok, verify.ToString());
    }

    [Fact]
    public async Task stop讓伺服器正常結束_先寫結果檔()
    {
        await using var host = await HubTestHost.StartAsync();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.App.Lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
        using var output = new StringWriter();
        (await AdminCommandLine.RunAsync(["stop", "--data", host.DataDir, "--wait", "20"], output)).Should().Be(AdminExitCodes.Ok, output.ToString());
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task 服務沒在跑時等不到結果_結束碼4_暫時性指令收回_reset_owner留給服務啟動後處理()
    {
        var dir = TempDir.Create();
        try
        {
            using var output = new StringWriter();
            (await AdminCommandLine.RunAsync(["drain", "--data", dir, "--wait", "1"], output)).Should().Be(AdminExitCodes.Timeout);
            Directory.GetFiles(Path.Combine(dir, "admin-inbox"), "*.json").Should().BeEmpty("drain 不該留到下次啟動才生效");

            (await AdminCommandLine.RunAsync(["reset-owner", "--data", dir, "--wait", "1"], TextWriter.Null)).Should().Be(AdminExitCodes.Timeout);
            Directory.GetFiles(Path.Combine(dir, "admin-inbox"), "*.json").Should().ContainSingle("reset-owner 是刻意的管理動作，服務啟動後處理");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 放太久的暫時性指令作廢不執行()
    {
        await using var owner = await OwnerHost.StartAsync();
        var id = Guid.NewGuid().ToString("N");
        var stale = new MyriagentHub.Ops.AdminCommand(id, "drain", DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds());
        var file = Path.Combine(owner.Host.Paths.AdminInboxDir, MyriagentHub.Ops.AdminInboxRules.FileName(DateTimeOffset.UtcNow, id));
        await File.WriteAllTextAsync(file + ".tmp", System.Text.Json.JsonSerializer.Serialize(stale, MyriagentHub.Ops.StatusJson.Options));
        File.Move(file + ".tmp", file);
        var resultFile = Path.Combine(owner.Host.Paths.AdminResultsDir, id + ".json");
        for (var i = 0; i < 100 && !File.Exists(resultFile); i++)
            await Task.Delay(100);
        var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile)).RootElement;
        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        owner.Host.Services.GetRequiredService<MyriagentHub.Ops.DrainState>().IsDraining.Should().BeFalse();
    }

    [Fact]
    public async Task 不認得的指令_用法錯誤結束碼2()
    {
        using var output = new StringWriter();
        (await AdminCommandLine.RunAsync(["format-c"], output)).Should().Be(AdminExitCodes.Usage);
        (await AdminCommandLine.RunAsync([], TextWriter.Null)).Should().Be(AdminExitCodes.Usage);
        (await AdminCommandLine.RunAsync(["restore"], TextWriter.Null)).Should().Be(AdminExitCodes.Usage, "restore 一定要 --from");
    }

    [Fact]
    public async Task 收件匣裡格式錯誤的檔案被丟掉_不影響之後的指令()
    {
        await using var owner = await OwnerHost.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(owner.Host.Paths.AdminInboxDir, "00000000000000000-bad.json"), "{ not json");
        using var output = new StringWriter();
        (await AdminCommandLine.RunAsync(["verify-audit", "--data", owner.Host.DataDir, "--wait", "20"], output)).Should().Be(AdminExitCodes.Ok, output.ToString());
        File.Exists(Path.Combine(owner.Host.Paths.AdminInboxDir, "00000000000000000-bad.json")).Should().BeFalse();
    }

    [Fact]
    public async Task preflight_對資料庫副本試跑遷移_不改線上檔案()
    {
        await using var owner = await OwnerHost.StartAsync();
        var work = TempDir.Create("myriagenthub-preflight-temp");
        try
        {
            var before = MyriagentHub.Ops.BackupService.Inspect(owner.Host.Paths.HubDb).Tables;
            var result = await Preflight.RunAsync(owner.Host.Paths, work, CancellationToken.None);
            result.Checks.Should().Contain(c => c.Name == "db.migrate_copy" && c.Ok);
            result.PendingMigrations.Should().BeEmpty("服務啟動時已套用所有遷移");
            result.Checks.Single(c => c.Name == "config.server_domain").Ok.Should().BeTrue();
            result.Checks.Should().Contain(c => c.Name == "data.readable" && c.Ok);
            // WAL 的檢查點會合法地改動主檔位元組，所以比對內容（各表筆數）而不是檔案雜湊
            MyriagentHub.Ops.BackupService.Inspect(owner.Host.Paths.HubDb).Tables.Should().BeEquivalentTo(before, "preflight 只對副本遷移，不改線上資料庫");
            Directory.GetFileSystemEntries(work).Should().BeEmpty("副本放在暫存資料夾，用完即刪");
            Directory.GetFileSystemEntries(owner.Host.Paths.TmpDir).Should().BeEmpty("不在資料根放任何東西");
        }
        finally
        {
            TempDir.TryDelete(work);
        }
    }

    [Fact]
    public async Task cert_renew的force只用在cert_renew_ACME沒啟用時回失敗並說明()
    {
        using var usage = new StringWriter();
        var nowhere = Path.Combine(Path.GetTempPath(), "myriagenthub-none-" + Guid.NewGuid().ToString("N"));
        (await AdminCommandLine.RunAsync(["selftest", "--force", "--data", nowhere], usage)).Should().Be(AdminExitCodes.Usage, usage.ToString());
        Directory.Exists(nowhere).Should().BeFalse("用法錯誤時什麼都不寫");

        await using var host = await HubTestHost.StartAsync();
        using var output = new StringWriter();
        (await AdminCommandLine.RunAsync(["cert-renew", "--force", "--data", host.DataDir, "--wait", "20"], output)).Should().Be(AdminExitCodes.Failed, output.ToString());
        var result = JsonDocument.Parse(output.ToString()).RootElement;
        result.GetProperty("message").GetString().Should().Contain("ACME 沒有啟用");
        result.GetProperty("data").GetProperty("forceRenew").GetBoolean().Should().BeTrue("--force 有傳到服務");
        result.GetProperty("data").GetProperty("completed").GetBoolean().Should().BeTrue("等到那一輪檢查做完才回報");
    }

    [Fact]
    public async Task app_devices列出各電腦的app版本()
    {
        await using var owner = await OwnerHost.StartAsync();
        using (var empty = new StringWriter())
        {
            (await AdminCommandLine.RunAsync(["app-devices", "--data", owner.Host.DataDir, "--wait", "20"], empty)).Should().Be(AdminExitCodes.Ok, empty.ToString());
            var none = JsonDocument.Parse(empty.ToString()).RootElement;
            none.GetProperty("message").GetString().Should().Be("還沒有配對任何電腦");
            none.GetProperty("data").GetArrayLength().Should().Be(0);
        }
        using var online = await TestDevice.AddAsync(owner.Host, "桌機");
        using var never = await TestDevice.AddAsync(owner.Host, "筆電");
        await using var client = await DeviceHubClient.ConnectAsync(owner.Host, online.Token);
        await client.HandshakeAsync(online, DeviceHubClient.ObservedSpki(owner.Host), appVersion: "0.2.740");

        using var output = new StringWriter();
        (await AdminCommandLine.RunAsync(["app-devices", "--data", owner.Host.DataDir, "--wait", "20"], output)).Should().Be(AdminExitCodes.Ok, output.ToString());
        output.ToString().Should().NotContain("桌機", "非 ASCII 一律跳脫：開發機的發佈腳本經 ssh（cp950 主控台）讀它");
        var result = JsonDocument.Parse(output.ToString()).RootElement;
        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.GetProperty("command").GetString().Should().Be("app-devices");
        result.GetProperty("message").GetString().Should().Be("共 2 台電腦");
        var data = result.GetProperty("data").EnumerateArray().ToDictionary(d => d.GetProperty("deviceId").GetString()!);
        data.Keys.Should().BeEquivalentTo([online.Id, never.Id]);
        data[online.Id].EnumerateObject().Select(p => p.Name).Should().Equal("deviceId", "name", "appVersion", "lastHelloAt", "online", "updateOnly");
        data[online.Id].GetProperty("name").GetString().Should().Be("桌機");
        data[online.Id].GetProperty("appVersion").GetString().Should().Be("0.2.740", "Hello 回報的版本");
        data[online.Id].GetProperty("online").GetBoolean().Should().BeTrue();
        data[never.Id].GetProperty("appVersion").GetString().Should().Be("0.1.0", "還沒連過的用配對時的版本");
        data[never.Id].GetProperty("lastHelloAt").ValueKind.Should().Be(JsonValueKind.Null);
        data[never.Id].GetProperty("online").GetBoolean().Should().BeFalse();
        AdminInboxRules.IsTransient("app-devices").Should().BeTrue("只是列出：逾時就收回");
    }

    [Fact]
    public async Task app_releases列出版本()
    {
        await using var owner = await OwnerHost.StartAsync();
        using (var empty = new StringWriter())
        {
            (await AdminCommandLine.RunAsync(["app-releases", "--data", owner.Host.DataDir, "--wait", "20"], empty)).Should().Be(AdminExitCodes.Ok, empty.ToString());
            var none = JsonDocument.Parse(empty.ToString()).RootElement;
            none.GetProperty("message").GetString().Should().Be("還沒有收到任何版本");
            none.GetProperty("data").GetArrayLength().Should().Be(0);
        }
        var older = await AppReleaseOwnerEndpointTests.SeedReleaseAsync(owner.Host, "0.2.10-aaaaaa0", owner.Host.Clock.NowMs - 60_000);
        var newer = await AppReleaseOwnerEndpointTests.SeedReleaseAsync(owner.Host, "0.2.11-bbbbbb1", owner.Host.Clock.NowMs);

        using var output = new StringWriter();
        (await AdminCommandLine.RunAsync(["app-releases", "--data", owner.Host.DataDir, "--wait", "20"], output)).Should().Be(AdminExitCodes.Ok, output.ToString());
        var result = JsonDocument.Parse(output.ToString()).RootElement;
        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.GetProperty("command").GetString().Should().Be("app-releases");
        result.GetProperty("message").GetString().Should().Contain(newer.Release, "訊息寫出目前提供的版本");
        var data = result.GetProperty("data").EnumerateArray().ToList();
        data.Select(r => r.GetProperty("release").GetString()).Should().Equal([newer.Release, older.Release], "收件時間由新到舊");
        data[0].EnumerateObject().Select(p => p.Name).Should().Equal("release", "version", "build", "commit", "zipBytes", "kid", "state", "ingestedAt", "withdrawnAt");
        data[1].GetProperty("state").GetString().Should().Be(AppReleaseStates.Available);
        data[1].GetProperty("zipBytes").GetInt64().Should().Be(older.ZipBytes);
    }

    [Fact]
    public async Task app_release_withdraw撤回並稽核()
    {
        await using var owner = await OwnerHost.StartAsync();
        var older = await AppReleaseOwnerEndpointTests.SeedReleaseAsync(owner.Host, "0.2.10-aaaaaa0", owner.Host.Clock.NowMs - 60_000);
        var newer = await AppReleaseOwnerEndpointTests.SeedReleaseAsync(owner.Host, "0.2.11-bbbbbb1", owner.Host.Clock.NowMs);
        var store = owner.Host.Services.GetRequiredService<AppReleaseStore>();
        var audit = owner.Host.Services.GetRequiredService<AuditLog>();

        using var output = new StringWriter();
        (await AdminCommandLine.RunAsync(["app-release-withdraw", newer.Release, "--data", owner.Host.DataDir, "--wait", "20"], output)).Should().Be(AdminExitCodes.Ok, output.ToString());
        var data = JsonDocument.Parse(output.ToString()).RootElement.GetProperty("data");
        data.GetProperty("release").GetString().Should().Be(newer.Release);
        data.GetProperty("state").GetString().Should().Be(AppReleaseStates.Withdrawn);
        data.GetProperty("alreadyWithdrawn").GetBoolean().Should().BeFalse();
        data.GetProperty("latest").GetString().Should().Be(older.Release);
        store.Latest!.Release.Should().Be(older.Release);
        (await store.FindAsync(newer.Release, CancellationToken.None))!.State.Should().Be(AppReleaseStates.Withdrawn);

        var entries = await audit.ListAsync(null, 200);
        var row = entries.Should().ContainSingle(e => e.Action == AuditActions.AppReleaseWithdrawn).Which;
        row.ActorType.Should().Be(AuditActors.Admin);
        row.ActorId.Should().BeNull();
        row.Ip.Should().BeNull();
        row.Target.Should().Be(newer.Release);
        row.Outcome.Should().Be(AuditOutcomes.Ok);
        entries.Should().Contain(e => e.Action == AuditActions.AdminCommand && e.Target == "app-release-withdraw" && e.Outcome == AuditOutcomes.Ok);

        // 再撤回一次：成功、alreadyWithdrawn，稽核不再多一筆；不存在的版本：服務回失敗（結束碼 1）
        using var again = new StringWriter();
        (await AdminCommandLine.RunAsync(["app-release-withdraw", newer.Release, "--data", owner.Host.DataDir, "--wait", "20"], again)).Should().Be(AdminExitCodes.Ok, again.ToString());
        JsonDocument.Parse(again.ToString()).RootElement.GetProperty("data").GetProperty("alreadyWithdrawn").GetBoolean().Should().BeTrue();
        using var missing = new StringWriter();
        (await AdminCommandLine.RunAsync(["app-release-withdraw", "0.2.99-fffffff", "--data", owner.Host.DataDir, "--wait", "20"], missing)).Should().Be(AdminExitCodes.Failed, missing.ToString());
        (await audit.ListAsync(null, 200)).Count(e => e.Action == AuditActions.AppReleaseWithdrawn).Should().Be(1);
    }

    [Fact]
    public async Task app_release_withdraw沒帶release是格式錯()
    {
        // CLI：第二個參數不是合法的 release 就是用法錯誤，什麼都不寫。
        // 每一組都帶 --data（指向不存在的暫存路徑）：不帶時資料根是真的 C:\ProgramData\MyriagentHub，萬一驗證壞掉就會寫進去（沒帶 release 的情況由第一組涵蓋）
        var nowhere = Path.Combine(Path.GetTempPath(), "myriagenthub-none-" + Guid.NewGuid().ToString("N"));
        foreach (var args in new[]
        {
            new[] { "app-release-withdraw", "--data", nowhere },
            new[] { "app-release-withdraw", "0.2.10-AAAAAA0", "--data", nowhere },
            new[] { "app-release-withdraw", "latest", "--data", nowhere },
        })
        {
            using var usage = new StringWriter();
            (await AdminCommandLine.RunAsync(args, usage)).Should().Be(AdminExitCodes.Usage, string.Join(' ', args));
        }
        Directory.Exists(nowhere).Should().BeFalse("用法錯誤時什麼都不寫");

        // 收件匣：沒帶 release 是格式錯誤（指令照樣解析出來，結果檔寫失敗）；帶了標準寫法的才通過
        AdminInboxRules.Parse("""{"id":"abc","command":"app-release-withdraw","requestedAt":1}""", out var missing).Should().NotBeNull();
        missing.Should().Be("app-release-withdraw 要帶標準寫法的 release（例如 0.2.1523-ab12cd3）");
        AdminInboxRules.Parse("""{"id":"abc","command":"app-release-withdraw","requestedAt":1,"release":"0.2.10-AAAAAA0"}""", out var nonCanonical).Should().NotBeNull();
        nonCanonical.Should().NotBeNull("build 要小寫");
        AdminInboxRules.Parse("""{"id":"abc","command":"app-release-withdraw","requestedAt":1,"release":"0.2.10-aaaaaa0"}""", out var none)!.Release.Should().Be("0.2.10-aaaaaa0");
        none.Should().BeNull();

        await using var owner = await OwnerHost.StartAsync();
        var record = await AppReleaseOwnerEndpointTests.SeedReleaseAsync(owner.Host, "0.2.10-aaaaaa0", owner.Host.Clock.NowMs);
        var result = await WriteCommandAsync(owner.Host, new AdminCommand(Guid.NewGuid().ToString("N"), "app-release-withdraw", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.GetProperty("message").GetString().Should().Be("app-release-withdraw 要帶標準寫法的 release（例如 0.2.1523-ab12cd3）");
        (await owner.Host.Services.GetRequiredService<AppReleaseStore>().FindAsync(record.Release, CancellationToken.None))!.State
            .Should().Be(AppReleaseStates.Available, "格式錯的指令不執行");
    }

    [Fact]
    public async Task release參數只能用在app_release_withdraw()
    {
        foreach (var command in new[] { "drain", "app-releases", "verify-audit", "reset-owner" })
        {
            var parsed = AdminInboxRules.Parse($$"""{"id":"abc","command":"{{command}}","requestedAt":1,"release":"0.2.10-aaaaaa0"}""", out var error);
            parsed!.Command.Should().Be(command);
            error.Should().Be("release 只能用在 app-release-withdraw", command);
        }
        AdminInboxRules.Parse("""{"id":"abc","command":"drain","requestedAt":1}""", out var none).Should().NotBeNull();
        none.Should().BeNull("沒帶 release 的照舊");

        // 帶了 release 的 drain 不執行：結果是格式錯誤、沒有進入排空
        await using var owner = await OwnerHost.StartAsync();
        var result = await WriteCommandAsync(owner.Host,
            new AdminCommand(Guid.NewGuid().ToString("N"), "drain", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Release: "0.2.10-aaaaaa0"));
        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.GetProperty("message").GetString().Should().Be("release 只能用在 app-release-withdraw");
        owner.Host.Services.GetRequiredService<DrainState>().IsDraining.Should().BeFalse();
    }

    [Fact]
    public async Task app_release_withdraw不是暫時性指令()
    {
        AdminInboxRules.IsTransient("app-release-withdraw").Should().BeFalse();
        AdminInboxRules.IsTransient("app-releases").Should().BeTrue("只是列出：逾時就收回");
        var now = DateTimeOffset.UtcNow;
        AdminInboxRules.IsExpired(new AdminCommand("abc", "app-release-withdraw", now.AddMinutes(-30).ToUnixTimeMilliseconds(), Release: "0.2.10-aaaaaa0"), now)
            .Should().BeFalse("刻意的管理動作，放多久都照樣執行");

        var dir = TempDir.Create();
        try
        {
            AppReleaseRecord record;
            await using (var first = await HubTestHost.StartAsync(dataDir: dir))
                record = await AppReleaseOwnerEndpointTests.SeedReleaseAsync(first, "0.2.10-aaaaaa0", first.Clock.NowMs);

            // 服務停著：app-releases 逾時收回；app-release-withdraw 逾時留在收件匣
            using (var list = new StringWriter())
            {
                (await AdminCommandLine.RunAsync(["app-releases", "--data", dir, "--wait", "1"], list)).Should().Be(AdminExitCodes.Timeout);
                JsonDocument.Parse(list.ToString()).RootElement.GetProperty("withdrawn").GetBoolean().Should().BeTrue();
            }
            using (var output = new StringWriter())
            {
                (await AdminCommandLine.RunAsync(["app-release-withdraw", record.Release, "--data", dir, "--wait", "1"], output)).Should().Be(AdminExitCodes.Timeout);
                JsonDocument.Parse(output.ToString()).RootElement.GetProperty("withdrawn").GetBoolean().Should().BeFalse();
            }
            var pending = Directory.GetFiles(Path.Combine(dir, "admin-inbox"), "*.json");
            pending.Should().ContainSingle("app-release-withdraw 留到服務啟動後處理");
            var id = JsonDocument.Parse(await File.ReadAllTextAsync(pending[0])).RootElement.GetProperty("id").GetString()!;

            // 服務下次啟動照樣執行
            await using var second = await HubTestHost.StartAsync(dataDir: dir);
            var resultFile = Path.Combine(second.Paths.AdminResultsDir, id + ".json");
            for (var i = 0; i < 100 && !File.Exists(resultFile); i++)
                await Task.Delay(100);
            var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile)).RootElement;
            result.GetProperty("ok").GetBoolean().Should().BeTrue(result.ToString());
            result.GetProperty("command").GetString().Should().Be("app-release-withdraw");
            (await second.Services.GetRequiredService<AppReleaseStore>().FindAsync(record.Release, CancellationToken.None))!.State.Should().Be(AppReleaseStates.Withdrawn);
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task CLI用法列出兩個新指令()
    {
        using var output = new StringWriter();
        (await AdminCommandLine.RunAsync(["help"], output)).Should().Be(AdminExitCodes.Usage);
        var usage = JsonDocument.Parse(output.ToString()).RootElement.GetProperty("usage").EnumerateArray().Select(u => u.GetString()!).ToList();
        usage.Should().Contain(u => u.StartsWith("MyriagentHub.exe admin app-releases [--data <資料根>] [--wait <秒>]", StringComparison.Ordinal));
        usage.Should().Contain(u => u.StartsWith("MyriagentHub.exe admin app-release-withdraw <release> [--data <資料根>] [--wait <秒>]", StringComparison.Ordinal));
        AdminInboxRules.Commands.Where(c => c.StartsWith("app-release", StringComparison.Ordinal)).Should().Equal("app-releases", "app-release-withdraw");
        AdminInboxRules.Canonical("app-releases").Should().Be("app-releases");
        AdminInboxRules.Canonical(" APP-RELEASE-WITHDRAW ").Should().Be("app-release-withdraw");
    }

    /// <summary>直接把指令檔寫進收件匣（暫存檔＋改名，同 CLI）並等結果檔（最多 10 秒）</summary>
    private static async Task<JsonElement> WriteCommandAsync(HubTestHost host, AdminCommand command)
    {
        var file = Path.Combine(host.Paths.AdminInboxDir, AdminInboxRules.FileName(DateTimeOffset.UtcNow, command.Id));
        await File.WriteAllTextAsync(file + ".tmp", JsonSerializer.Serialize(command, StatusJson.Options));
        File.Move(file + ".tmp", file);
        var resultFile = Path.Combine(host.Paths.AdminResultsDir, command.Id + ".json");
        for (var i = 0; i < 100 && !File.Exists(resultFile); i++)
            await Task.Delay(100);
        return JsonDocument.Parse(await File.ReadAllTextAsync(resultFile)).RootElement;
    }
}
