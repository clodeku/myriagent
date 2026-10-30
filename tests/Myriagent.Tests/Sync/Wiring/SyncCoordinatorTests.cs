using System.Net;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.Sync.Pairing;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Sdk;

namespace Myriagent.Tests.Sync.Wiring;

/// <summary>
/// 宿主端的同步協調者：假的 engineFactory 與 pairingCommit、配對用 ScriptedHttpHandler（沒預備的請求一律丟例外，不連網）、
/// 記憶體裡的裝置金鑰、可撥的時鐘；中樞的宿主照正式環境一律 Post
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class SyncCoordinatorTests
{
    private const long At = 1791600000000;

    private sealed class Rig : IDisposable
    {
        /// <param name="realCommit">配對提交用正式的 SyncEngineLoader.CompletePairing（建 sync.db、寫 device.json）</param>
        /// <param name="remoteOnly">remoteOnly 合成卡片的開關（null＝照 SyncFeatures）</param>
        /// <param name="conversations">假引擎的對話盒子（null＝不支援對話發布的引擎）</param>
        public Rig(bool paired = false, Action<SyncPaths>? beforeCreate = null, bool realCommit = false, Func<bool>? remoteOnly = null,
            ConversationChangeBox? conversations = null)
        {
            Engine = new RecordingSyncEngine { Conversations = conversations };
            H = new HubHarness(alwaysPost: true);
            Paths = new SyncPaths(H.Temp.Combine("sync"));
            Http = new ScriptedHttpHandler(Time);
            if (paired)
            {
                Directory.CreateDirectory(Paths.Directory);
                Device = PairingTestData.Device();
                DeviceFile.Save(Paths.DeviceFile, Device);
            }

            beforeCreate?.Invoke(Paths);
            Coordinator = H.OnUi(() => new SyncCoordinator(H.Hub, H.Accounts, H.Host, H.Settings, Paths,
                new SyncCoordinatorOptions(false, null, false, "0.5.0", "OFFICE-PC", "user"), Time,
                args =>
                {
                    Created.Add(args);
                    return Engine;
                },
                (paths, response, device) =>
                {
                    Commits.Add(device);
                    if (realCommit)
                        return SyncEngineLoader.CompletePairing(paths, response, device);
                    DeviceFile.Save(paths.DeviceFile, device);
                    return new PairingCommitResult(device);
                },
                Http,
                (_, _) =>
                {
                    var signer = new InMemoryDeviceSigner();
                    Signers.Add(signer);
                    return signer;
                })
            {
                RemoteOnlyEnabled = remoteOnly ?? (() => SyncFeatures.RemoteOnlyAccounts),
            });
            H.Ui.Invoke(() => H.Hub.AttachSync(Coordinator));
        }

        public HubHarness H { get; }

        public FakeView Main => H.Main;

        public ManualTimeProvider Time { get; } = new();

        public SyncPaths Paths { get; }

        public ScriptedHttpHandler Http { get; }

        public DeviceFileData? Device { get; }

        public RecordingSyncEngine Engine { get; }

        public List<SyncEngineCreateArgs> Created { get; } = [];

        public List<DeviceFileData> Commits { get; } = [];

        public List<InMemoryDeviceSigner> Signers { get; } = [];

        public SyncCoordinator Coordinator { get; }

        public async Task StartAsync()
        {
            H.Ui.Invoke(() => Coordinator.Start());
            await Coordinator.WhenStarted.WaitAsync(TimeSpan.FromSeconds(10));
            await H.Ui.DrainAsync();
        }

        public void Command(string cmd, object? args = null) => H.Command(Main, cmd, args);

        public T OnUi<T>(Func<T> read) => H.OnUi(read);

        public void Dispose()
        {
            H.Ui.Invoke(() => Coordinator.Dispose());
            foreach (var signer in Signers)
                signer.Dispose();
            H.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new XunitException($"{timeoutMs} ms 內沒有等到：{because}");
            await Task.Delay(10);
        }
    }

    private static DeviceUpsertPayload DeviceView(string deviceId, string name) =>
        new(deviceId, name, "PC", "0.5.0", PairingRules.KeyStorageSoftware, "ABCD-EFGH", At, At, true, new DevicePolicy("Off", false), null);

    private static CatalogApply Catalog(string selfDeviceId, params DeviceUpsertPayload[] devices) => new(
        new SyncCatalogView(selfDeviceId, new Dictionary<string, AccountUpsertPayload>(), devices.ToDictionary(d => d.DeviceId), [],
            new Dictionary<string, string>(), new Dictionary<string, DeviceRevokedPayload>()),
        [],
        null,
        []);

    private static bool IsNullSync(JObject m) => m["sync"]?.Type == JTokenType.Null;

    // ───── 帳戶目錄（WP3.4）：在這台登入 ─────

    private const string Unlinked = "la_0123456789ABCDEFGHJKMNPQRS";

    private const string LinkedCodex = "la_1123456789ABCDEFGHJKMNPQRS";

    private const string ClaimedClaude = "la_2123456789ABCDEFGHJKMNPQRS";

    private const string FutureProvider = "la_3123456789ABCDEFGHJKMNPQRS";

    private static AccountUpsertPayload Entry(string logical, string provider, string label, string? expectedEmail, int sortOrder = 0) =>
        new(logical, provider, $"{provider.ToLowerInvariant()}:{logical}", label, "#abc", sortOrder, expectedEmail, null, null, 1, []);

    /// <summary>
    /// 目錄：Unlinked（Claude，這台沒連結）、LinkedCodex（連到 acc-codex）、ClaimedClaude（acc-claude2 宣告了）、FutureProvider（不認得的種類）。
    /// linkUnlinkedTo＝伺服器把 Unlinked 連到這台的哪個本機帳戶；withoutUnlinked＝目錄裡沒有 Unlinked（刪除了）
    /// </summary>
    private static CatalogApply DirectoryCatalog(string selfDeviceId, string? linkUnlinkedTo = null, bool withoutUnlinked = false)
    {
        var accounts = new Dictionary<string, AccountUpsertPayload>
        {
            [Unlinked] = Entry(Unlinked, "Claude", "公司的 Claude", "Work@Example.com", 0),
            [LinkedCodex] = Entry(LinkedCodex, "Codex", "Codex 帳戶", "codex@example.com", 1),
            [ClaimedClaude] = Entry(ClaimedClaude, "Claude", "宣告的 Claude", "claimed@example.com", 2),
            [FutureProvider] = Entry(FutureProvider, "Gemini", "未來的種類", null, 3),
        };
        if (withoutUnlinked)
            accounts.Remove(Unlinked);
        var links = new Dictionary<string, string> { ["acc-codex"] = LinkedCodex };
        if (linkUnlinkedTo is not null)
            links[linkUnlinkedTo] = Unlinked;
        return new CatalogApply(
            new SyncCatalogView(selfDeviceId, accounts, new Dictionary<string, DeviceUpsertPayload>(), [], links, new Dictionary<string, DeviceRevokedPayload>()),
            [],
            null,
            []);
    }

    private static async Task<Rig> PairedWithDirectoryAsync(Func<bool>? remoteOnly = null)
    {
        var rig = new Rig(paired: true, remoteOnly: remoteOnly);
        await rig.StartAsync();
        rig.H.Ui.Invoke(() =>
        {
            rig.H.Accounts.ResolveLauncher = _ => null;
            rig.H.Accounts.Find("acc-claude2")!.LogicalAccountId = ClaimedClaude;
            rig.Coordinator.ApplyCatalog(DirectoryCatalog(rig.Device!.DeviceId));
        });
        await rig.H.Ui.DrainAsync();
        return rig;
    }

    private static (CatalogAccountTemplate? Template, string Failure) Claim(Rig rig, string? logical) =>
        rig.OnUi(() => (rig.Coordinator.ClaimCatalogAccount(logical, out var failure), failure));

    [Fact]
    public async Task ClaimCatalogAccount_未配對_目錄沒有_不支援的種類_已連結_已宣告_成功()
    {
        using (var unpaired = new Rig())
        {
            await unpaired.StartAsync();
            Claim(unpaired, Unlinked).Should().Be(((CatalogAccountTemplate?)null, AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.NotPaired)));
        }

        using var rig = await PairedWithDirectoryAsync();

        Claim(rig, "la_x").Should().Be(((CatalogAccountTemplate?)null, AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.InvalidId)));
        Claim(rig, null).Failure.Should().Be(AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.InvalidId));
        Claim(rig, "la_9923456789ABCDEFGHJKMNPQRS").Failure.Should().Be(AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.NotInCatalog));
        Claim(rig, FutureProvider).Failure.Should().Be(AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.NotInCatalog),
            "伺服器多出來、這版不認得的種類當成目錄沒有（Antigravity、Grok 不會出現在目錄裡，規則另有測試）");
        Claim(rig, LinkedCodex).Failure.Should().Be(AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.AlreadyLinkedHere));
        Claim(rig, ClaimedClaude).Failure.Should().Be(AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.AlreadyClaimedHere));

        var (template, failure) = Claim(rig, Unlinked);
        failure.Should().BeEmpty();
        template.Should().Be(new CatalogAccountTemplate(Unlinked, AgentProvider.Claude, "公司的 Claude", "#abc", "Work@Example.com"));
    }

    [Fact]
    public async Task ExpectedEmail_連結或宣告的帳戶回目錄信箱_其他回null()
    {
        using (var unpaired = new Rig())
        {
            await unpaired.StartAsync();
            unpaired.OnUi(() => unpaired.Coordinator.ExpectedEmail("acc-codex")).Should().BeNull("未配對");
        }

        using var rig = await PairedWithDirectoryAsync();

        rig.OnUi(() => rig.Coordinator.ExpectedEmail("acc-codex")).Should().Be("codex@example.com", "目錄的連結");
        rig.OnUi(() => rig.Coordinator.ExpectedEmail("acc-claude2")).Should().Be("claimed@example.com", "宣告的邏輯帳戶");
        rig.OnUi(() => rig.Coordinator.ExpectedEmail("acc-claude")).Should().BeNull("沒有對應");
        rig.OnUi(() => rig.Coordinator.ExpectedEmail("不存在")).Should().BeNull();
        rig.H.Ui.Invoke(() => rig.H.Accounts.Find("acc-claude")!.LogicalAccountId = LinkedCodex);
        rig.OnUi(() => rig.Coordinator.ExpectedEmail("acc-claude")).Should().BeNull("種類不同的對應不算");
    }

    [Fact]
    public async Task 宣告後_RemoteOnlyEnabled打開時不列這個邏輯帳戶的remoteOnly卡()
    {
        using var rig = await PairedWithDirectoryAsync(remoteOnly: () => true);
        var mark = rig.Main.Mark();

        rig.H.Ui.Invoke(() => rig.H.Hub.RefreshSyncDecorations());
        var accounts = await rig.Main.WaitForAsync("accounts", m => m["accounts"]!.Any(a => a.Value<bool>("remoteOnly")), mark);

        var remoteOnly = accounts["accounts"]!.Where(a => a.Value<bool>("remoteOnly")).Select(a => a.Value<string>("id")).ToList();
        remoteOnly.Should().Contain(Unlinked);
        remoteOnly.Should().NotContain(ClaimedClaude, "這台已經有宣告它的本機卡片");
        remoteOnly.Should().NotContain(LinkedCodex);
    }

    private static SharedUsage RemoteShared(double percent) =>
        new(At, UsageSyncRules.SourceShared, [new SharedUsageWindow("session", "5 小時", percent, At + 3_600_000, false)], null, null, null, false);

    /// <summary>引擎的 UI 套用端（同一代的 EnginePort）在 UI 執行緒套用 remoteOnly 的共享用量</summary>
    private static Task ApplyRemoteAsync(Rig rig, bool reset, params RemoteUsageApply[] items)
    {
        var created = rig.Created.Single();
        return created.Host.RunOnUiAsync(() => created.Applier.ApplyRemoteUsage(items, reset), CancellationToken.None);
    }

    private static JObject? RemoteCard(JObject accounts, string logical) =>
        accounts["accounts"]!.Children<JObject>().SingleOrDefault(a => a.Value<bool>("remoteOnly") && a.Value<string>("id") == logical);

    [Fact]
    public async Task RemoteOnlyEnabled打開時_remoteOnly卡帶共享用量_source為shared()
    {
        using var rig = await PairedWithDirectoryAsync(remoteOnly: () => true);
        var mark = rig.Main.Mark();

        await ApplyRemoteAsync(rig, false, new RemoteUsageApply(Unlinked, RemoteShared(30)), new RemoteUsageApply(LinkedCodex, RemoteShared(80)));
        var accounts = await rig.Main.WaitForAsync("accounts", m => RemoteCard(m, Unlinked)?["usage"] is JObject, mark);

        var usage = (JObject)RemoteCard(accounts, Unlinked)!["usage"]!;
        usage.Value<string>("source").Should().Be("shared");
        usage["windows"]![0]!.Value<double>("percent").Should().Be(30);
        var codex = accounts["accounts"]!.Children<JObject>().Single(a => a.Value<string>("id") == "acc-codex");
        (codex["usage"]?.ToString() ?? "").Should().NotContain("80", "連結了的帳戶照本機卡片的用量，remoteOnly 的用量不套到它身上");
        rig.OnUi(() => rig.Coordinator.RemoteUsageOf(Unlinked)).Should().NotBeNull();
    }

    [Fact]
    public async Task remoteOnly卡關著時_用量照樣收下但不重畫()
    {
        using var rig = await PairedWithDirectoryAsync(remoteOnly: () => false);
        await rig.H.Ui.DrainAsync();
        await Task.Delay(700);
        var mark = rig.Main.Mark();

        await ApplyRemoteAsync(rig, false, new RemoteUsageApply(Unlinked, RemoteShared(30)));
        await rig.H.Ui.DrainAsync();
        await Task.Delay(700);

        rig.OnUi(() => rig.Coordinator.RemoteUsageOf(Unlinked)).Should().NotBeNull("開關打開後就有值");
        rig.Main.Messages("accounts", mark).Should().BeEmpty("沒有 remoteOnly 卡片時不必重送帳戶卡");
    }

    [Fact]
    public async Task reset後remoteOnly卡的用量是null()
    {
        using var rig = await PairedWithDirectoryAsync(remoteOnly: () => true);
        await ApplyRemoteAsync(rig, false, new RemoteUsageApply(Unlinked, RemoteShared(30)));
        await rig.Main.WaitForAsync("accounts", m => RemoteCard(m, Unlinked)?["usage"] is JObject);
        var mark = rig.Main.Mark();

        await ApplyRemoteAsync(rig, true);
        var accounts = await rig.Main.WaitForAsync("accounts", m => RemoteCard(m, Unlinked) is { } card && card["usage"]!.Type == JTokenType.Null, mark);

        RemoteCard(accounts, Unlinked)!["usage"]!.Type.Should().Be(JTokenType.Null);
        rig.OnUi(() => rig.Coordinator.RemoteUsageOf(Unlinked)).Should().BeNull();

        // reset 帶項目：先清再套
        await ApplyRemoteAsync(rig, true, new RemoteUsageApply(Unlinked, RemoteShared(45)));
        rig.OnUi(() => rig.Coordinator.RemoteUsageOf(Unlinked))!.Windows.Single().Percent.Should().Be(45);
    }

    [Fact]
    public async Task 連結到這台之後清掉remoteOnly的舊用量_解除連結後卡片等下一筆()
    {
        using var rig = await PairedWithDirectoryAsync(remoteOnly: () => true);
        await ApplyRemoteAsync(rig, false, new RemoteUsageApply(Unlinked, RemoteShared(30)));
        rig.OnUi(() => rig.Coordinator.RemoteUsageOf(Unlinked)).Should().NotBeNull();

        // 伺服器把 Unlinked 連到這台的 acc-claude：之後的快照走 ApplySharedUsage，remoteOnly 的那筆會停在連結之前，清掉
        rig.H.Ui.Invoke(() => rig.Coordinator.ApplyCatalog(DirectoryCatalog(rig.Device!.DeviceId, linkUnlinkedTo: "acc-claude")));
        rig.OnUi(() => rig.Coordinator.RemoteUsageOf(Unlinked)).Should().BeNull("連結到這台之後不留 remoteOnly 的舊值");
        await rig.H.Ui.DrainAsync();
        var mark = rig.Main.Mark();

        // 解除連結：remoteOnly 卡片回來，用量是 null（等下一筆），不是連結之前的 30%
        rig.H.Ui.Invoke(() => rig.Coordinator.ApplyCatalog(DirectoryCatalog(rig.Device!.DeviceId)));
        var accounts = await rig.Main.WaitForAsync("accounts", m => RemoteCard(m, Unlinked) is not null, mark);
        RemoteCard(accounts, Unlinked)!["usage"]!.Type.Should().Be(JTokenType.Null, "解除連結後不拿連結之前的舊用量頂替");
    }

    [Fact]
    public async Task 目錄刪掉的邏輯帳戶_remoteOnly的用量一起清掉()
    {
        using var rig = await PairedWithDirectoryAsync(remoteOnly: () => true);
        await ApplyRemoteAsync(rig, false, new RemoteUsageApply(Unlinked, RemoteShared(30)));

        rig.H.Ui.Invoke(() => rig.Coordinator.ApplyCatalog(DirectoryCatalog(rig.Device!.DeviceId, withoutUnlinked: true)));

        rig.OnUi(() => rig.Coordinator.RemoteUsageOf(Unlinked)).Should().BeNull("不在目錄裡的不留");
    }

    [Fact]
    public async Task 解除配對後清空remoteOnly的共享用量()
    {
        using var rig = await PairedWithDirectoryAsync(remoteOnly: () => true);
        var port = rig.Created.Single();
        await ApplyRemoteAsync(rig, false, new RemoteUsageApply(Unlinked, RemoteShared(30)));
        rig.OnUi(() => rig.Coordinator.RemoteUsageOf(Unlinked)).Should().NotBeNull();
        var mark = rig.Main.Mark();

        rig.Command("syncUnpair");
        await rig.Main.WaitForAsync("sync", IsNullSync, mark);

        rig.OnUi(() => rig.Coordinator.RemoteUsageOf(Unlinked)).Should().BeNull();
        // 舊引擎晚到的套用丟掉（不同代）
        var late = () => port.Host.RunOnUiAsync(() => port.Applier.ApplyRemoteUsage([new RemoteUsageApply(Unlinked, RemoteShared(55))], false), CancellationToken.None);
        await late.Should().ThrowAsync<OperationCanceledException>();
        rig.OnUi(() => rig.Coordinator.RemoteUsageOf(Unlinked)).Should().BeNull();
    }

    [Fact]
    public void 建構子不碰檔案()
    {
        using var rig = new Rig(paired: true, beforeCreate: paths => File.WriteAllText(paths.PendingKeyFile, "{}"));
        using var unpaired = new Rig();

        File.Exists(rig.Paths.PendingKeyFile).Should().BeTrue("PairingRecovery 在 Start 才跑");
        rig.Created.Should().BeEmpty();
        rig.OnUi(() => rig.Coordinator.Status).Should().BeNull();
        rig.OnUi(() => rig.Coordinator.IsPaired).Should().BeFalse();
        Directory.Exists(unpaired.Paths.Directory).Should().BeFalse();
    }

    [Fact]
    public async Task 未配對_Start不建引擎()
    {
        using var rig = new Rig();

        await rig.StartAsync();
        rig.Command("syncRetry");
        rig.Command("syncConfirm", new { kind = "hubIdentity", fingerprint = "AAAA-BBBB-CCCC-DDDD" });
        rig.Command("ready");

        rig.Created.Should().BeEmpty();
        rig.OnUi(() => rig.Coordinator.IsPaired).Should().BeFalse();
        rig.OnUi(() => rig.Coordinator.Status).Should().BeNull();
        rig.OnUi(() => rig.Coordinator.InitRemote()).Should().BeNull();
        rig.OnUi(() => rig.Coordinator.DevicesCli()).Should().BeNull();
        rig.Main.Last("init")!["remote"]!.Type.Should().Be(JTokenType.Null);
        rig.Main.Last("init")!.Value<bool>("syncStarting").Should().BeFalse("讀完 device.json 之後就不是啟動中");
        rig.Main.Messages("sync").Should().BeEmpty();
        Directory.Exists(rig.Paths.Directory).Should().BeFalse("未配對時不建同步資料夾");
    }

    [Fact]
    public async Task 啟動中送的init_syncStarting為true_讀完沒有配對送一則sync_null()
    {
        using var rig = new Rig();

        rig.Command("ready");
        var init = rig.Main.Last("init")!;
        init["remote"]!.Type.Should().Be(JTokenType.Null);
        init.Value<bool>("syncStarting").Should().BeTrue("還沒讀 device.json：網頁不能當成未配對");
        rig.OnUi(() => rig.Coordinator.IsStarting).Should().BeTrue();

        await rig.StartAsync();

        rig.OnUi(() => rig.Coordinator.IsStarting).Should().BeFalse();
        IsNullSync(rig.Main.Messages("sync").Should().ContainSingle().Subject).Should().BeTrue("讀完確定沒有配對：結束網頁的「啟動中」");
        var mark = rig.Main.Mark();
        rig.Command("ready");
        rig.Main.Last("init")!.Value<bool>("syncStarting").Should().BeFalse();
        rig.Main.Messages("sync", mark).Should().BeEmpty("之後照舊：未配對時不送 sync");
    }

    [Fact]
    public async Task 已配對_啟動中送的init_syncStarting為true_讀完送狀態而不是sync_null()
    {
        using var rig = new Rig(paired: true);

        rig.Command("ready");
        rig.Main.Last("init")!.Value<bool>("syncStarting").Should().BeTrue();
        rig.Main.Last("init")!["remote"]!.Type.Should().Be(JTokenType.Null, "還沒讀 device.json");

        await rig.StartAsync();

        var syncs = rig.Main.Messages("sync");
        syncs.Should().NotBeEmpty();
        syncs.Should().OnlyContain(m => !IsNullSync(m), "已配對：CreateEngine 送狀態，不送 sync:null");
        rig.Command("ready");
        rig.Main.Last("init")!.Value<bool>("syncStarting").Should().BeFalse();
        rig.Main.Last("init")!["remote"]!.Type.Should().Be(JTokenType.Object);
    }

    [Fact]
    public async Task syncPair_狀態pairing含userCode與指紋_init_remote不是null()
    {
        using var rig = new Rig();
        rig.Http.Respond(HttpStatusCode.OK, SyncJson.Serialize(PairingTestData.Start()));
        await rig.StartAsync();
        var mark = rig.Main.Mark();

        rig.Command("syncPair", new { deviceName = "OFFICE-PC", server = PairingTestData.Server });
        var first = await rig.Main.WaitForAsync("sync", since: mark);
        var shown = await rig.Main.WaitForAsync("sync", m => m["sync"]?["pairing"]?.Value<string>("userCode") == "BCDF-GHJK", mark);
        rig.Command("ready");

        first["sync"]!.Value<string>("state").Should().Be("pairing", "按下配對就是 pairing（還沒拿到配對碼）");
        var sync = shown["sync"]!;
        sync.Value<string>("state").Should().Be("pairing");
        sync.Value<string>("server").Should().Be(PairingTestData.Server);
        sync.Value<string>("deviceFingerprint").Should().Be(P256KeyRules.Fingerprint(rig.Signers.Single().PublicSpki));
        sync.Value<string>("deviceId").Should().BeNull();
        sync["pairing"]!.Value<string>("verificationUriComplete").Should().Be(PairingTestData.Server + "/#pair=BCDF-GHJK");
        sync["pairing"]!.Value<long>("expiresAt").Should().Be(rig.Time.NowMs + 600_000);
        sync["pairing"]!.Value<string>("error").Should().BeNull();
        var remote = rig.Main.Last("init")!["remote"]!;
        remote.Type.Should().Be(JTokenType.Object, "配對中 init.remote 就不是 null");
        remote.Value<string>("mode").Should().Be("desktop");
        remote["sync"]!.Value<string>("state").Should().Be("pairing");
        remote["devices"]!.Should().BeEmpty();
        rig.Http.Requests.Single().Uri.AbsoluteUri.Should().Be(PairingTestData.Server + "/api/v1/pair/start");

        rig.Command("syncCancelPair");
    }

    [Fact]
    public async Task syncPair_server不是https_忽略並回報錯誤()
    {
        using var rig = new Rig();
        await rig.StartAsync();

        rig.Command("syncPair", new { deviceName = "OFFICE-PC", server = "http://hub.test" });
        rig.Command("syncPair", new { deviceName = "OFFICE-PC", server = 5 });
        rig.Command("syncPair", new { deviceName = "OFFICE-PC", server = "https://hub.test/path?x=1" });
        await rig.H.Ui.DrainAsync();

        rig.Main.Messages("toast").Should().HaveCount(3).And.OnlyContain(t => t.Value<string>("level") == "error" && t.Value<string>("text")!.Contains("https"));
        rig.Http.Requests.Should().BeEmpty();
        rig.Main.Messages("sync").Should().BeEmpty();
        rig.OnUi(() => rig.Coordinator.Status).Should().BeNull();
        rig.Signers.Should().BeEmpty("沒有建金鑰");
    }

    [Fact]
    public async Task 配對失敗_error保留到取消()
    {
        using var rig = new Rig();
        rig.Http.Throw(new HttpRequestException("連不上"));
        await rig.StartAsync();
        var mark = rig.Main.Mark();

        rig.Command("syncPair", new { deviceName = "OFFICE-PC", server = PairingTestData.Server });
        var failed = await rig.Main.WaitForAsync("sync", m => m["sync"]?["pairing"]?.Value<string>("error") is not null, mark);
        await WaitUntilAsync(() => !File.Exists(rig.Paths.PendingKeyFile), "失敗後刪掉 pending 檔");
        await rig.H.Ui.DrainAsync();
        await Task.Delay(100);
        await rig.H.Ui.DrainAsync();

        failed["sync"]!.Value<string>("state").Should().Be("pairing");
        rig.OnUi(() => rig.Coordinator.Status!.Pairing!.Error).Should().NotBeNullOrEmpty("錯誤一直留著");
        rig.Command("ready");
        rig.Main.Last("init")!["remote"]!["sync"]!["pairing"]!.Value<string>("error").Should().Be(failed["sync"]!["pairing"]!.Value<string>("error"));

        var beforeCancel = rig.Main.Mark();
        rig.Command("syncCancelPair");
        await rig.H.Ui.DrainAsync();

        IsNullSync(rig.Main.Messages("sync", beforeCancel).Should().ContainSingle().Subject).Should().BeTrue("取消後送 sync=null");
        rig.OnUi(() => rig.Coordinator.Status).Should().BeNull();
        rig.OnUi(() => rig.Coordinator.InitRemote()).Should().BeNull();
    }

    [Fact]
    public async Task syncCancelPair_刪金鑰_送sync_null()
    {
        using var rig = new Rig();
        rig.Http.Respond(HttpStatusCode.OK, SyncJson.Serialize(PairingTestData.Start()));
        await rig.StartAsync();
        var mark = rig.Main.Mark();
        rig.Command("syncPair", new { deviceName = "OFFICE-PC", server = PairingTestData.Server });
        await rig.Main.WaitForAsync("sync", m => m["sync"]?["pairing"]?.Value<string>("userCode") == "BCDF-GHJK", mark);
        File.Exists(rig.Paths.PendingKeyFile).Should().BeTrue("配對中有 pending 檔記著金鑰名稱");
        var beforeCancel = rig.Main.Mark();

        rig.Command("syncCancelPair");
        await rig.Main.WaitForAsync("sync", IsNullSync, beforeCancel);
        await WaitUntilAsync(() => !File.Exists(rig.Paths.PendingKeyFile), "取消後 PairingSession 刪金鑰與 pending 檔");
        await WaitUntilAsync(() => rig.Signers.Single().IsDisposed, "金鑰關掉");
        await rig.H.Ui.DrainAsync();

        rig.OnUi(() => rig.Coordinator.Status).Should().BeNull();
        rig.OnUi(() => rig.Coordinator.InitRemote()).Should().BeNull();
        rig.Created.Should().BeEmpty();
        rig.Commits.Should().BeEmpty();
        rig.Main.Messages("sync", beforeCancel).Should().OnlyContain(m => IsNullSync(m), "取消後不再送配對進度");
    }

    [Fact]
    public async Task 配對成功_建引擎並掛上鏡像()
    {
        using var rig = new Rig();
        var deviceId = SyncIds.NewDeviceId();
        rig.Http.Respond(HttpStatusCode.OK, SyncJson.Serialize(PairingTestData.Start(interval: 2)));
        rig.Http.RespondWith(HttpStatusCode.OK, () => SyncJson.Serialize(PairingTestData.Approval(deviceId, rig.Signers.Single().PublicSpki)));
        await rig.StartAsync();
        var mark = rig.Main.Mark();

        rig.Command("syncPair", new { deviceName = "OFFICE-PC", server = PairingTestData.Server });
        await rig.Main.WaitForAsync("sync", m => m["sync"]?["pairing"]?.Value<string>("userCode") == "BCDF-GHJK", mark);
        await rig.Time.WaitForTimersAsync();
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => rig.Created.Count == 1, "核准後建引擎");
        await rig.H.Ui.DrainAsync();

        rig.Commits.Should().ContainSingle().Which.DeviceId.Should().Be(deviceId);
        rig.Created.Single().Device.DeviceId.Should().Be(deviceId);
        rig.Engine.Starts.Should().Be(1);
        rig.OnUi(() => rig.Coordinator.IsPaired).Should().BeTrue();
        var sync = rig.Main.Messages("sync", mark).Last()["sync"]!;
        sync.Value<string>("state").Should().Be("connecting");
        sync.Value<string>("deviceId").Should().Be(deviceId);
        sync["pairing"]!.Type.Should().Be(JTokenType.Null);
        rig.Engine.Of<SyncSignal.Accounts>().Should().NotBeEmpty("掛上鏡像後立刻交出帳戶清單");
        rig.Engine.Of<SyncSignal.Cli>().Should().ContainSingle();
    }

    [Fact]
    public async Task device_json損毀而sync_db還在_重新配對成功_不留pending檔()
    {
        // 損毀的 device.json 當成未配對（可以重新配對）：提交時要刪掉殘留的 sync.db，否則 SyncStore.Create 必定失敗、每次重試都多一台孤兒裝置
        using var rig = new Rig(beforeCreate: paths =>
        {
            Directory.CreateDirectory(paths.Directory);
            using (SyncStore.Create(paths.DatabaseFile, SyncIds.NewDeviceId(), At, new ServerInstance("old", 0), new TrustBundle(1, [])))
            {
            }

            File.WriteAllText(paths.DeviceFile, "{ 損毀");
        }, realCommit: true);
        var deviceId = SyncIds.NewDeviceId();
        rig.Http.Respond(HttpStatusCode.OK, SyncJson.Serialize(PairingTestData.Start(interval: 2)));
        rig.Http.RespondWith(HttpStatusCode.OK, () => SyncJson.Serialize(PairingTestData.Approval(deviceId, rig.Signers.Single().PublicSpki)));
        await rig.StartAsync();
        rig.OnUi(() => rig.Coordinator.IsPaired).Should().BeFalse("損毀的 device.json 當成未配對");
        var mark = rig.Main.Mark();

        rig.Command("syncPair", new { deviceName = "OFFICE-PC", server = PairingTestData.Server });
        await rig.Main.WaitForAsync("sync", m => m["sync"]?["pairing"]?.Value<string>("userCode") == "BCDF-GHJK", mark);
        await rig.Time.WaitForTimersAsync();
        rig.Time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => rig.Created.Count == 1, "核准後提交成功、建引擎");
        await WaitUntilAsync(() => !File.Exists(rig.Paths.PendingKeyFile), "配對完成刪掉 pending 檔");
        await rig.H.Ui.DrainAsync();

        rig.Created.Single().Device.DeviceId.Should().Be(deviceId);
        DeviceFile.Load(rig.Paths.DeviceFile).Data!.DeviceId.Should().Be(deviceId);
        using (var store = SyncStore.Open(rig.Paths.DatabaseFile, deviceId))
            store.GetKv(SyncStoreSchema.DeviceId).Should().Be(deviceId);
        rig.Main.Messages("sync", mark).Where(m => m["sync"] is JObject sync && sync["pairing"] is JObject p && p.Value<string>("error") != null)
            .Should().BeEmpty("沒有 commit_failed");
    }

    [Fact]
    public async Task 已配對_Start只建一次引擎()
    {
        using var rig = new Rig(paired: true);

        await rig.StartAsync();
        rig.H.Ui.Invoke(() => rig.Coordinator.Start());
        await rig.H.Ui.DrainAsync();
        rig.Command("ready");

        rig.Created.Should().ContainSingle();
        rig.Engine.Starts.Should().Be(1);
        var args = rig.Created.Single();
        args.Device.Should().Be(rig.Device);
        args.Policy.Should().Be(new DevicePolicy("Off", false));
        args.Paths.Should().Be(rig.Paths);
        args.Host.Should().NotBeSameAs(rig.Coordinator, "每個引擎拿自己那一代的宿主");
        rig.OnUi(() => rig.Coordinator.IsPaired).Should().BeTrue();
        rig.OnUi(() => rig.Coordinator.Status!.DeviceId).Should().Be(rig.Device!.DeviceId);
        var init = rig.Main.Last("init")!;
        init["remote"]!.Value<string>("deviceId").Should().Be(rig.Device.DeviceId);
        init["remote"]!["sync"]!.Value<string>("state").Should().Be("connecting");
        init["remote"]!["sync"]!.Value<string>("hubFingerprint").Should().Be(rig.Device.HubFingerprint);
        init["devicesCli"]!.Type.Should().Be(JTokenType.Object, "已配對：目錄還沒到時是空物件");
        rig.Engine.Of<SyncSignal.Accounts>().Should().ContainSingle();
        rig.Engine.Paused.Should().BeEmpty("設定沒有暫停");
    }

    [Fact]
    public async Task 已配對再syncPair_忽略()
    {
        using var rig = new Rig(paired: true);
        await rig.StartAsync();
        var status = rig.OnUi(() => rig.Coordinator.Status);

        rig.Command("syncPair", new { deviceName = "OFFICE-PC", server = PairingTestData.Server });
        await rig.H.Ui.DrainAsync();

        rig.Http.Requests.Should().BeEmpty();
        rig.Signers.Should().BeEmpty();
        rig.OnUi(() => rig.Coordinator.Status).Should().Be(status);
        rig.Main.Messages("toast").Should().BeEmpty();
    }

    [Fact]
    public async Task syncUnpair_停引擎並刪三樣_解除鏡像_送sync_null()
    {
        using var rig = new Rig(paired: true);
        await rig.StartAsync();
        foreach (var file in new[] { rig.Paths.DatabaseFile, rig.Paths.DatabaseFile + "-wal", rig.Paths.DatabaseFile + "-shm", rig.Paths.PendingKeyFile })
            File.WriteAllText(file, "x");
        var accountsBefore = rig.Engine.Of<SyncSignal.Accounts>().Count;
        var mark = rig.Main.Mark();

        rig.Command("syncUnpair");
        await rig.Main.WaitForAsync("sync", IsNullSync, mark);
        var devices = await rig.Main.WaitForAsync("devices", since: mark);

        rig.Engine.Stopped.Should().BeTrue();
        devices["devices"]!.Should().BeEmpty();
        foreach (var file in new[] { rig.Paths.DeviceFile, rig.Paths.DatabaseFile, rig.Paths.DatabaseFile + "-wal", rig.Paths.DatabaseFile + "-shm", rig.Paths.PendingKeyFile })
            File.Exists(file).Should().BeFalse($"{Path.GetFileName(file)} 要刪掉");
        rig.OnUi(() => rig.Coordinator.IsPaired).Should().BeFalse();
        rig.OnUi(() => rig.Coordinator.Status).Should().BeNull();
        rig.OnUi(() => rig.Coordinator.ShouldPoll(rig.H.Claude)).Should().BeTrue("用量閘門回到一律查");
        rig.H.Accounts.Accounts.Should().HaveCount(3, "本機帳戶不動");

        // 鏡像已解除：之後帳戶改名不再交給引擎
        var afterUnpair = rig.Main.Mark();
        rig.Command("accountUpdate", new { accountId = "acc-claude", label = "解除後改名" });
        await rig.Main.WaitForAsync("accounts", m => m["accounts"]!.Any(a => a.Value<string>("label") == "解除後改名"), afterUnpair);
        await rig.H.Ui.DrainAsync();
        rig.Engine.Of<SyncSignal.Accounts>().Should().HaveCount(accountsBefore);
        rig.Engine.Of<SyncSignal.AccountEdited>().Should().BeEmpty();
        rig.Main.Last("accounts")!["accounts"]!.Should().OnlyContain(a => ((JObject)a).Property("logicalId") == null);
    }

    [Fact]
    public async Task syncConfirm_轉給引擎()
    {
        using var rig = new Rig(paired: true);
        await rig.StartAsync();

        rig.Command("syncConfirm", new { kind = "hubIdentity", fingerprint = "AAAA-BBBB-CCCC-DDDD" });
        rig.Command("syncConfirm", new { kind = "ownerKey", fingerprint = "EEEE" });
        rig.Command("syncConfirm", new { kind = "hubIdentity" });
        rig.Command("syncRetry");
        rig.Command("syncPause", new { paused = true });

        rig.Engine.Confirmed.Should().Equal("AAAA-BBBB-CCCC-DDDD");
        rig.Engine.Retries.Should().Be(1);
        rig.Engine.Paused.Should().Equal(true);
        rig.H.Settings.Sync.Paused.Should().BeTrue();
    }

    [Fact]
    public async Task syncSetPolicy_存設定並通知引擎_不認得的層級忽略()
    {
        using var rig = new Rig(paired: true);
        await rig.StartAsync();

        rig.Command("syncSetPolicy", new { remoteControlLevel = "View" });
        rig.Command("syncSetPolicy", new { remoteControlLevel = "Admin", allowRemoteFullAccess = true });
        rig.Command("syncSetPolicy", new { allowRemoteFullAccess = "yes" });
        rig.Command("syncSetPolicy", new { allowRemoteFullAccess = true });
        rig.H.Ui.Invoke(() => rig.Coordinator.SetRemotePaused(true));

        rig.Engine.Policies.Should().Equal(new DevicePolicy("View", false), new DevicePolicy("View", true), new DevicePolicy("Off", false));
        rig.H.Settings.Sync.RemoteControlLevel.Should().Be("View");
        rig.H.Settings.Sync.AllowRemoteFullAccess.Should().BeTrue();
        rig.H.Settings.Sync.RemotePaused.Should().BeTrue();
        var saved = JObject.Parse(File.ReadAllText(AppPaths.SettingsFile));
        saved["Sync"]!.Value<string>("RemoteControlLevel").Should().Be("View");
        saved["Sync"]!.Value<bool>("RemotePaused").Should().BeTrue();
    }

    // ───── sync.local（WP3.4 D14）：設定頁不靠伺服器的回音 ─────

    private static Func<JObject, bool> Local(Func<JObject, bool> predicate) =>
        m => m["sync"] is JObject sync && sync["local"] is JObject local && predicate(local);

    [Fact]
    public async Task syncSetPolicy後立刻送sync_local是新值()
    {
        using var rig = new Rig(paired: true);
        await rig.StartAsync();
        rig.Command("ready");
        rig.Main.Last("init")!["remote"]!["sync"]!["local"]!.Value<string>("remoteControlLevel").Should().Be("Off", "init.remote 一開始就帶本機設定");
        var mark = rig.Main.Mark();

        rig.Command("syncSetPolicy", new { remoteControlLevel = "Control", allowRemoteFullAccess = true });

        var sync = await rig.Main.WaitForAsync("sync", Local(l => l.Value<string>("remoteControlLevel") == "Control"), mark);
        sync["sync"]!["local"]!.Value<bool>("allowRemoteFullAccess").Should().BeTrue();
        sync["sync"]!["local"]!.Value<bool>("paused").Should().BeFalse();

        // 不合格的參數：設定沒改、不重送
        var afterBad = rig.Main.Mark();
        rig.Command("syncSetPolicy", new { remoteControlLevel = "Admin" });
        await rig.H.Ui.DrainAsync();
        rig.Main.Messages("sync", afterBad).Should().BeEmpty();
    }

    [Fact]
    public async Task 系統匣暫停遠端操作後_sync的local_remotePaused為true()
    {
        using var rig = new Rig(paired: true);
        await rig.StartAsync();
        rig.Command("syncSetPolicy", new { remoteControlLevel = "View" });
        var mark = rig.Main.Mark();

        rig.H.Ui.Invoke(() => rig.Coordinator.SetRemotePaused(true));

        var sync = await rig.Main.WaitForAsync("sync", Local(l => l.Value<bool>("remotePaused")), mark);
        sync["sync"]!["local"]!.Value<string>("remoteControlLevel").Should().Be("View", "政策本身不變（回報給伺服器的是 Off，設定頁仍顯示本機的值）");
        rig.Engine.Policies.Last().Should().Be(new DevicePolicy("Off", false));
    }

    [Fact]
    public async Task syncPause後送sync()
    {
        using var rig = new Rig(paired: true);
        await rig.StartAsync();
        var mark = rig.Main.Mark();

        rig.Command("syncPause", new { paused = true });
        await rig.Main.WaitForAsync("sync", Local(l => l.Value<bool>("paused")), mark);

        var resumed = rig.Main.Mark();
        rig.H.Ui.Invoke(() => rig.Coordinator.SetPaused(false));
        await rig.Main.WaitForAsync("sync", Local(l => !l.Value<bool>("paused")), resumed);
        rig.Engine.Paused.Should().Equal(true, false);
    }

    [Fact]
    public async Task 未配對時改政策_不送sync()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        var mark = rig.Main.Mark();

        rig.Command("syncSetPolicy", new { remoteControlLevel = "View" });
        rig.Command("syncPause", new { paused = true });
        rig.H.Ui.Invoke(() => rig.Coordinator.SetRemotePaused(true));
        await rig.H.Ui.DrainAsync();

        rig.Main.Messages("sync", mark).Should().BeEmpty("未配對時不送 sync:null（設定照樣存）");
        rig.H.Settings.Sync.RemoteControlLevel.Should().Be("View");
        rig.H.Settings.Sync.Paused.Should().BeTrue();
        rig.H.Settings.Sync.RemotePaused.Should().BeTrue();
    }

    [Fact]
    public async Task 設定是暫停_啟動時就暫停_系統匣的繼續同步存設定_狀態改變通知系統匣()
    {
        using var rig = new Rig(paired: true);
        rig.H.Ui.Invoke(() => rig.H.Settings.Sync.Paused = true);
        var changes = 0;
        rig.H.Ui.Invoke(() => rig.Coordinator.StatusChanged += () => changes++);
        await rig.StartAsync();

        rig.Engine.Paused.Should().Equal([true], "設定是暫停：引擎一開始就不連線");
        rig.Engine.Starts.Should().Be(1);
        rig.H.Ui.Invoke(() => rig.Coordinator.SetPaused(false));
        rig.Engine.Paused.Should().Equal(true, false);
        rig.H.Settings.Sync.Paused.Should().BeFalse();
        JObject.Parse(File.ReadAllText(AppPaths.SettingsFile))["Sync"]!.Value<bool>("Paused").Should().BeFalse();

        var before = rig.OnUi(() => changes);
        rig.Coordinator.PublishStatus(SyncCoordinatorRules.Initial(rig.Device!, null) with { State = SyncEngineState.Live, Pending = 0 });
        await rig.H.Ui.DrainAsync();

        rig.OnUi(() => changes).Should().BeGreaterThan(before);
        TraySyncRules.TrayText(rig.OnUi(() => rig.Coordinator.Status), rig.OnUi(() => rig.Coordinator.IsPaired)).Should().Be("同步 已連線");
    }

    [Fact]
    public async Task deviceRename_清理後送Edit_不合格忽略()
    {
        using var rig = new Rig(paired: true);
        await rig.StartAsync();
        var home = SyncIds.NewDeviceId();
        var name = "  家裡的電腦" + (char)0x0007 + "  ";

        rig.Command("deviceRename", new { deviceId = home, name });
        rig.Command("deviceRename", new { deviceId = home, name = "   " });
        rig.Command("deviceRename", new { deviceId = "not-a-device", name = "名稱" });
        rig.Command("deviceRename", new { deviceId = home });

        var renamed = rig.Engine.Of<SyncSignal.DeviceRenamed>().Should().ContainSingle().Subject;
        renamed.DeviceId.Should().Be(home);
        renamed.Name.Should().Be(PairingRules.CleanDeviceName(name)).And.Be("家裡的電腦");
    }

    [Fact]
    public async Task devices_500ms節流()
    {
        using var rig = new Rig(paired: true);
        await rig.StartAsync();
        var self = rig.Device!.DeviceId;
        var home = DeviceView(SyncIds.NewDeviceId(), "HOME-PC");
        var laptop = DeviceView(SyncIds.NewDeviceId(), "LAPTOP");
        var mark = rig.Main.Mark();

        rig.H.Ui.Invoke(() => rig.Coordinator.ApplyCatalog(Catalog(self, home)));
        rig.H.Ui.Invoke(() => rig.Coordinator.ApplyCatalog(Catalog(self, home, laptop)));
        rig.H.Ui.Invoke(() => rig.Coordinator.ApplyCatalog(Catalog(self, home, laptop with { Online = false })));
        await rig.H.Ui.DrainAsync();
        rig.Main.Messages("devices", mark).Should().ContainSingle("第一次立刻送，之後 500 ms 內的合併成一次");

        rig.Time.Advance(TimeSpan.FromMilliseconds(500));
        await rig.H.Ui.DrainAsync();
        var sent = rig.Main.Messages("devices", mark);
        sent.Should().HaveCount(2);
        sent[1]["devices"]!.Select(d => d.Value<string>("name")).Should().Equal("HOME-PC", "LAPTOP");
        sent[1]["devices"]![1]!.Value<bool>("online").Should().BeFalse("送的是最新的一份");

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.H.Ui.Invoke(() => rig.Coordinator.ApplyCatalog(Catalog(self, home, laptop with { Online = false })));
        await rig.H.Ui.DrainAsync();
        rig.Main.Messages("devices", mark).Should().HaveCount(2, "內容沒變不送");
    }

    [Fact]
    public async Task ShouldPoll_依快照判斷_未配對為true()
    {
        using var unpaired = new Rig();
        await unpaired.StartAsync();
        unpaired.OnUi(() => unpaired.Coordinator.ShouldPoll(unpaired.H.Claude)).Should().BeTrue();

        using var rig = new Rig(paired: true);
        await rig.StartAsync();
        var logical = SyncIds.NewLogicalAccountId();
        var snapshot = new UsagePollSnapshot(true, SyncEngineState.Live, null,
            new Dictionary<string, UsageLinkState> { ["acc-claude"] = new(logical, true, true) },
            new Dictionary<string, UsageAssignment> { [logical] = new(3, false) });
        var port = rig.Created.Single().Host;

        port.PublishPollSnapshot(snapshot);

        rig.OnUi(() => rig.Coordinator.ShouldPoll(rig.H.Claude)).Should().BeFalse("Live、指派 Off");
        rig.OnUi(() => rig.Coordinator.ShouldPoll(rig.H.Claude2)).Should().BeTrue("沒有連結的帳戶照常查");

        var mark = rig.Main.Mark();
        rig.Command("syncUnpair");
        await rig.Main.WaitForAsync("sync", IsNullSync, mark);
        port.PublishPollSnapshot(snapshot);
        rig.OnUi(() => rig.Coordinator.ShouldPoll(rig.H.Claude)).Should().BeTrue("解除配對後，舊引擎晚到的快照丟掉");
    }

    [Fact]
    public async Task OnNetworkChanged_從背景執行緒呼叫_在UI執行緒轉給引擎()
    {
        using var rig = new Rig(paired: true);
        await rig.StartAsync();
        var uiThread = rig.OnUi(() => Environment.CurrentManagedThreadId);

        await Task.Run(() => rig.Coordinator.OnNetworkChanged());
        await rig.H.Ui.DrainAsync();

        rig.Engine.RetryThreads.Should().Equal(uiThread);
    }

    // ───── syncSetConversationPolicy（WP4.3 計畫步 6，D35） ─────

    private static JObject PolicyCommand(string id, JToken policy) => new() { ["cmd"] = "syncSetConversationPolicy", ["conversationId"] = id, ["policy"] = policy };

    [Fact]
    public async Task syncSetConversationPolicy轉給中樞()
    {
        using var rig = new Rig(paired: true);
        await rig.StartAsync();
        var (id, s) = rig.H.StartConversation(rig.Main, rig.H.Claude);
        rig.H.CompleteTurn(s);

        rig.Command("syncSetConversationPolicy", new { conversationId = id, policy = "metadata" });
        rig.H.Conversation(id)!.SyncPolicy.Should().Be("metadata", "網頁的指令經中樞轉給協調者，再由中樞檢查與存檔");
        rig.H.Store.LoadConversations().Single(c => c.Id == id).SyncPolicy.Should().Be("metadata");

        rig.OnUi(() => rig.Coordinator.HandleCommand(rig.Main, "syncSetConversationPolicy", PolicyCommand(id, "localOnly"))).Should().BeTrue();
        rig.H.Conversation(id)!.SyncPolicy.Should().Be("localOnly");

        // 值的型別不對：當成沒有值（中樞不收），不丟例外
        rig.OnUi(() => rig.Coordinator.HandleCommand(rig.Main, "syncSetConversationPolicy", PolicyCommand(id, new JObject()))).Should().BeTrue();
        rig.OnUi(() => rig.Coordinator.HandleCommand(rig.Main, "syncSetConversationPolicy", PolicyCommand(id, 1))).Should().BeTrue();
        rig.H.Conversation(id)!.SyncPolicy.Should().Be("localOnly");
    }

    [Fact]
    public async Task syncSetConversationPolicy_瀏覽器檢視的不收()
    {
        using var rig = new Rig(paired: true);
        await rig.StartAsync();
        var (id, s) = rig.H.StartConversation(rig.Main, rig.H.Claude);
        rig.H.CompleteTurn(s);

        rig.OnUi(() => rig.Coordinator.HandleCommand(new FakeView(AgentHub.SyncViewId), "syncSetConversationPolicy", PolicyCommand(id, "localOnly")))
            .Should().BeFalse("同步鏡像（瀏覽器檢視）送來的不收");

        rig.H.Conversation(id)!.SyncPolicy.Should().BeNull("沒有改");
    }

    // ───── 對話發布者的掛上與拿掉（WP4.3 計畫步 7，D4） ─────

    [Fact]
    public async Task 支援對話的引擎_建立時掛上_解除配對時拿掉()
    {
        var box = new ConversationChangeBox();
        using var rig = new Rig(paired: true, conversations: box);
        await rig.StartAsync();

        var publisher = rig.OnUi(() => rig.H.Hub.ConversationSink).Should().BeOfType<ConversationPublisher>("引擎有對話盒子：建立時掛上發布者").Subject;
        box.UiPort.Should().BeSameAs(publisher);
        box.ConversationsRoot.Should().Be(rig.H.Store.RootDirectory);

        var (id, s) = rig.H.StartConversation(rig.Main, rig.H.Claude);
        await WaitUntilAsync(() => box.Count > 0, "一輪開始的標記當下就放進盒子");
        box.TakeAll().Should().Contain(c => c is TurnStartedChange && c.ConversationId == id);
        rig.Engine.Of<ConversationWake>().Should().NotBeEmpty("盒子從空變成非空時喚醒引擎");

        var mark = rig.Main.Mark();
        rig.Command("syncUnpair");
        await rig.Main.WaitForAsync("sync", IsNullSync, mark);

        rig.OnUi(() => rig.H.Hub.ConversationSink).Should().BeNull("解除配對時拿掉");
        publisher.IsDisposed.Should().BeTrue();
        box.UiPort.Should().BeNull();
        box.TakeAll();
        rig.H.CompleteTurn(s);
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        await rig.H.Ui.DrainAsync();
        box.Count.Should().Be(0, "拿掉之後中樞的變化不再進盒子");
    }

    [Fact]
    public async Task RecordingSyncEngine_不掛()
    {
        using var rig = new Rig(paired: true);
        await rig.StartAsync();

        rig.OnUi(() => rig.H.Hub.ConversationSink).Should().BeNull("引擎沒有對話盒子：不掛發布者，中樞一切照舊");
        var (_, s) = rig.H.StartConversation(rig.Main, rig.H.Claude);
        rig.H.CompleteTurn(s);
        await rig.H.Ui.DrainAsync();
        rig.Engine.Of<ConversationWake>().Should().BeEmpty();
    }
}
