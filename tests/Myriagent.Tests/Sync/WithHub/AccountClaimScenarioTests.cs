using System.Diagnostics;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Sync;
using Myriagent.Tests.Sync.LongRun;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;
using Xunit.Sdk;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>
/// WP3.4 第 5 步：「在這台登入」對 MyriagentHub 子程序的情境。每台裝置是完整的 <see cref="DeviceRuntime"/>（中樞、帳戶管理員、協調者、引擎都是真的，
/// 帳戶管理員拿不到任何 CLI：登入一律以「未安裝」結束），A 機的帳戶讓伺服器建立邏輯帳戶，B 機以 <c>accountAdd {logicalAccountId}</c> 宣告它。
/// 斷言看 B 機引擎的目錄快取（伺服器發回的 account.upsert）與伺服器 sync.db 的唯讀查詢：宣告只連結、不另建邏輯帳戶，換了身分只標成不符（設計 6.2、WPR accountAdd）
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class AccountClaimScenarioTests(MyriagentHubFixture fixture) : IClassFixture<MyriagentHubFixture>
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private HubDb Db => fixture.Db;

    [Fact]
    public async Task Codex_在這台登入_連結到同一個邏輯帳戶_換身分只標不符()
    {
        await using var a = await StartAsync("宣告 A 機", LongRunDevice.Account("claim-a", AgentProvider.Codex, "claim-a@example.test"));
        var logical = await WaitForAsync(() => Db.LogicalAccountId("codex:claim-a@example.test"), "A 機的帳戶建立了邏輯帳戶");
        await using var b = await StartAsync("宣告 B 機");
        await WaitClaimableAsync(b, logical);
        var count = Db.LogicalAccountCount();

        // 網頁的 provider、label、color 一律不看（取自目錄）
        await b.Runtime.CommandAsync("accountAdd", new JObject { ["logicalAccountId"] = logical, ["provider"] = "Claude", ["label"] = "網頁的名稱" });
        var local = await ClaimedAsync(b, logical);
        local.Provider.Should().Be(AgentProvider.Codex);
        local.Label.Should().Be("Codex claim-a", "名稱取自目錄");

        await b.WaitUntilAsync(v => SelfLink(v, logical, b.DeviceId) is { IdentityOk: true }, Wait, "B 機連結到同一個邏輯帳戶、身分相符（還沒登入）");
        Db.LinkIdentityOk(logical, b.DeviceId).Should().BeTrue();
        Db.LogicalAccountCount().Should().Be(count, "宣告只連結，不另建邏輯帳戶");
        (await b.Inspect(v => SelfLink(v, logical, b.DeviceId)!.LocalAccountId)).Should().Be(local.Id);

        // B 機登進了別的帳號：只把連結標成身分不符，邏輯帳戶不換、不另建
        await b.Runtime.OnUi(() =>
        {
            local.Email = "other@example.test";
            b.Runtime.Hub.RefreshSyncDecorations();
        });
        await b.WaitUntilAsync(v => SelfLink(v, logical, b.DeviceId) is { IdentityOk: false }, Wait, "換了身分之後標成不符");
        Db.LinkIdentityOk(logical, b.DeviceId).Should().BeFalse();
        Db.LogicalAccountCount().Should().Be(count);
        Db.LogicalAccountId("codex:other@example.test").Should().BeNull("身分不符不建新的邏輯帳戶");
        (await a.Inspect(v => v.Catalog?.Accounts.ContainsKey(logical))).Should().BeTrue("A 機的連結不受影響");
    }

    [Fact]
    public async Task Claude_org不明_email和目錄不同_標成身分不符()
    {
        await using var a = await StartAsync("宣告 A 機（Claude）", LongRunDevice.Account("claim-c", AgentProvider.Claude, "c@example.test", "org-claim"));
        var logical = await WaitForAsync(() => Db.LogicalAccountId("claude:org-claim:c@example.test"), "A 機的 Claude 帳戶建立了邏輯帳戶");
        await using var b = await StartAsync("宣告 B 機（Claude）");
        await WaitClaimableAsync(b, logical);
        var count = Db.LogicalAccountCount();

        await b.Runtime.CommandAsync("accountAdd", new JObject { ["logicalAccountId"] = logical });
        var local = await ClaimedAsync(b, logical);
        local.Provider.Should().Be(AgentProvider.Claude);
        (await b.Runtime.OnUi(() => b.Runtime.Sync.ExpectedEmail(local.Id))).Should().Be("c@example.test", "重新登入帶目錄的預期信箱");
        await b.WaitUntilAsync(v => SelfLink(v, logical, b.DeviceId) is { IdentityOk: true }, Wait, "B 機以宣告連結（org、email 都不知道）");

        // org 不明、email 和目錄相同（大小寫不同）：不送身分鍵，維持相符
        await SetEmailAsync(b, local, "C@Example.test");
        await b.WaitUntilAsync(v => SelfLink(v, logical, b.DeviceId) is { Email: "C@Example.test", IdentityOk: true }, Wait, "email 相同時維持相符");
        Db.LinkIdentityOk(logical, b.DeviceId).Should().BeTrue();

        // org 不明、email 不同：送 claude:{email}，伺服器標成不符（保守模式登錯帳號也會被發現，D11）
        await SetEmailAsync(b, local, "wrong@example.test");
        await b.WaitUntilAsync(v => SelfLink(v, logical, b.DeviceId) is { Email: "wrong@example.test", IdentityOk: false }, Wait, "email 不同時標成不符");
        Db.LinkIdentityOk(logical, b.DeviceId).Should().BeFalse();
        Db.LogicalAccountCount().Should().Be(count, "身分不符只標記，不另建邏輯帳戶");
    }

    [Fact]
    public async Task Claude_org不明_email相同但org提示和目錄不同_標成身分不符()
    {
        await using var a = await StartAsync("org 提示 A 機", LongRunDevice.Account("claim-h", AgentProvider.Claude, "h@example.test", "org-team"));
        var logical = await WaitForAsync(() => Db.LogicalAccountId("claude:org-team:h@example.test"), "A 機的 Claude 帳戶建立了邏輯帳戶");
        await using var b = await StartAsync("org 提示 B 機");
        await WaitClaimableAsync(b, logical);
        var count = Db.LogicalAccountCount();

        await b.Runtime.CommandAsync("accountAdd", new JObject { ["logicalAccountId"] = logical });
        var local = await ClaimedAsync(b, logical);
        await SetEmailAsync(b, local, "h@example.test");
        await b.WaitUntilAsync(v => SelfLink(v, logical, b.DeviceId) is { Email: "h@example.test", IdentityOk: true }, Wait, "email 相同、沒有 org 提示：相符");

        // SEC-1：保守模式登入時選了同一個信箱的另一個 org（CLI 設定的 organizationUuid 是 org-personal）：送 claude:{email}，標成不符
        await SetOrgIdHintAsync(b, local, "org-personal");
        await b.WaitUntilAsync(v => SelfLink(v, logical, b.DeviceId) is { IdentityOk: false }, Wait, "同一個信箱、不同 org：標成不符");
        Db.LinkIdentityOk(logical, b.DeviceId).Should().BeFalse();
        Db.LogicalAccountCount().Should().Be(count, "身分不符只標記，不另建邏輯帳戶");
        Db.LogicalAccountId("claude:h@example.test").Should().BeNull("身分不符不建新的邏輯帳戶");
    }

    [Fact]
    public async Task 重複的accountAdd_只產生一個本機帳戶()
    {
        await using var a = await StartAsync("重複宣告 A 機", LongRunDevice.Account("claim-twice", AgentProvider.Codex, "claim-twice@example.test"));
        var logical = await WaitForAsync(() => Db.LogicalAccountId("codex:claim-twice@example.test"), "A 機的帳戶建立了邏輯帳戶");
        await using var b = await StartAsync("重複宣告 B 機");
        await WaitClaimableAsync(b, logical);

        // 同一個 UI 工作裡連送兩次（中間不可能先拉到連結）：第二次因為這台已經有帳戶宣告它而被拒
        await b.Runtime.OnUi(() =>
        {
            b.Runtime.Hub.HandleCommand(b.Runtime.View, new JObject { ["cmd"] = "accountAdd", ["logicalAccountId"] = logical });
            b.Runtime.Hub.HandleCommand(b.Runtime.View, new JObject { ["cmd"] = "accountAdd", ["logicalAccountId"] = logical });
        });
        var local = await ClaimedAsync(b, logical);

        (await b.Runtime.OnUi(() => b.Runtime.Accounts.Accounts.Count)).Should().Be(1, "第二次被拒，不落回一般新增");
        (await b.Runtime.OnUi(() => b.Runtime.View.Toasts.ToList()))
            .Should().ContainSingle(t => t == AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.AlreadyClaimedHere), "拒絕時對送指令的視窗提示");

        // 連結之後再宣告：這台已經連到它
        await b.WaitUntilAsync(v => SelfLink(v, logical, b.DeviceId) is { IdentityOk: true }, Wait, "B 機連結到這個邏輯帳戶");
        await b.Runtime.WaitUntilAsync(() => b.Runtime.Sync.ClaimCatalogAccount(logical, out _) is null, Wait);
        await b.Runtime.CommandAsync("accountAdd", new JObject { ["logicalAccountId"] = logical });
        (await b.Runtime.OnUi(() => b.Runtime.Accounts.Accounts.Select(x => x.Id).ToList())).Should().Equal(local.Id);
        (await b.Runtime.OnUi(() => b.Runtime.View.Toasts.Last()))
            .Should().Be(AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.AlreadyLinkedHere));
    }

    private Task<LongRunDevice> StartAsync(string name, params FakeAccountSpec[] accounts) =>
        LongRunDevice.PairAndStartAsync(fixture.Hub, fixture.Owner, name, accounts, HubEngine.FastBackoff(0.2, 0.5, 1));

    /// <summary>等 B 機的協調者拿到含這個邏輯帳戶的目錄（宣告檢查讀的是 UI 執行緒上的檢視）</summary>
    private static async Task WaitClaimableAsync(LongRunDevice device, string logical)
    {
        if (!await device.Runtime.WaitUntilAsync(() => device.Runtime.Sync.ClaimCatalogAccount(logical, out _) is not null, Wait))
            throw new XunitException($"{device.Name}：{Wait.TotalSeconds:0} 秒內目錄沒有出現可宣告的 {logical}");
    }

    /// <summary>宣告了這個邏輯帳戶的本機帳戶（中樞在 UI 執行緒上新增）</summary>
    private static async Task<AgentAccount> ClaimedAsync(LongRunDevice device, string logical)
    {
        AgentAccount? found = null;
        if (!await device.Runtime.WaitUntilAsync(() => (found = device.Runtime.Accounts.Accounts.FirstOrDefault(x => x.LogicalAccountId == logical)) is not null, Wait))
            throw new XunitException($"{device.Name}：沒有宣告 {logical} 的本機帳戶");
        return found!;
    }

    private static Task SetEmailAsync(LongRunDevice device, AgentAccount account, string email) => device.Runtime.OnUi(() =>
    {
        account.Email = email;
        device.Runtime.Hub.RefreshSyncDecorations();
    });

    /// <summary>保守模式的 org 提示（AccountRuntimeState.OrgIdHint；正式流程由 Claude 的狀態檢查設定，這台拿不到 CLI，直接設）</summary>
    private static Task SetOrgIdHintAsync(LongRunDevice device, AgentAccount account, string? hint) => device.Runtime.OnUi(() =>
    {
        device.Runtime.Accounts.GetState(account.Id).OrgIdHint = hint;
        device.Runtime.Hub.RefreshSyncDecorations();
    });

    private static AccountLinkView? SelfLink(SyncEngine.EngineView view, string logical, string deviceId) =>
        view.Catalog?.Accounts.TryGetValue(logical, out var account) == true ? (account.Links ?? []).FirstOrDefault(l => l?.DeviceId == deviceId) : null;

    private static async Task<string> WaitForAsync(Func<string?> probe, string because)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (probe() is { } value)
                return value;
            if (watch.Elapsed > Wait)
                throw new XunitException($"{Wait.TotalSeconds:0} 秒內沒有等到「{because}」");
            await Task.Delay(100);
        }
    }
}
