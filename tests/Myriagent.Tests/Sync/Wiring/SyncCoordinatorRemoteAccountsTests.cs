using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.Sync.Pairing;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Wiring;

/// <summary>
/// 協調者的遠端登入目標（WP4.8 計畫步 6、D21）：FindRemoteLoginTarget 從邏輯帳戶或本機帳戶找出要登入的本機帳戶（沒有就給建立的範本）
/// 與目錄的預期身分；只讀目錄的快照與帳戶清單。假引擎、假的配對資料，不連網
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class SyncCoordinatorRemoteAccountsTests
{
    /// <summary>連到 acc-codex 的 Codex</summary>
    private const string LinkedCodex = "la_1123456789ABCDEFGHJKMNPQRS";

    /// <summary>連到 acc-claude2、acc-claude 另外宣告了它的 Claude（連結優先）</summary>
    private const string LinkedAndClaimed = "la_2123456789ABCDEFGHJKMNPQRS";

    /// <summary>這台沒有連結、沒有宣告的 Codex（要建立）</summary>
    private const string Unlinked = "la_3123456789ABCDEFGHJKMNPQRS";

    /// <summary>連到 acc-claude 的 Codex（本機的種類和目錄不同）</summary>
    private const string KindMismatch = "la_4123456789ABCDEFGHJKMNPQRS";

    /// <summary>連結指著這台已經沒有的本機帳戶</summary>
    private const string LinkedToGone = "la_5123456789ABCDEFGHJKMNPQRS";

    /// <summary>這版不認得的種類（伺服器多出來的）</summary>
    private const string FutureProvider = "la_6123456789ABCDEFGHJKMNPQRS";

    /// <summary>線上寫成 Antigravity 的項目（這兩種不進目錄，線上沒有它們的名稱）</summary>
    private const string AntigravityEntry = "la_7123456789ABCDEFGHJKMNPQRS";

    /// <summary>格式合格、目錄沒有</summary>
    private const string NotInCatalog = "la_9923456789ABCDEFGHJKMNPQRS";

    private sealed class Rig : IDisposable
    {
        public Rig(bool paired = true)
        {
            H = new HubHarness(alwaysPost: true);
            Paths = new SyncPaths(H.Temp.Combine("sync"));
            if (paired)
            {
                Directory.CreateDirectory(Paths.Directory);
                Device = PairingTestData.Device();
                DeviceFile.Save(Paths.DeviceFile, Device);
            }

            Coordinator = H.OnUi(() => new SyncCoordinator(H.Hub, H.Accounts, H.Host, H.Settings, Paths,
                new SyncCoordinatorOptions(false, null, false, "0.5.0", "OFFICE-PC", "user"), Time, _ => Engine));
            H.Ui.Invoke(() =>
            {
                H.Hub.AttachSync(Coordinator);
                // 不找這台真的 CLI（這組測試不登入，只防萬一有查詢）
                H.Accounts.ResolveLauncher = _ => null;
            });
        }

        public HubHarness H { get; }

        public ManualTimeProvider Time { get; } = new();

        public SyncPaths Paths { get; }

        public DeviceFileData? Device { get; }

        public RecordingSyncEngine Engine { get; } = new();

        public SyncCoordinator Coordinator { get; }

        public async Task StartAsync()
        {
            H.Ui.Invoke(() => Coordinator.Start());
            await Coordinator.WhenStarted.WaitAsync(TimeSpan.FromSeconds(10));
            await H.Ui.DrainAsync();
        }

        public void ApplyCatalog(Dictionary<string, AccountUpsertPayload> entries, Dictionary<string, string> links)
            => H.Ui.Invoke(() => Coordinator.ApplyCatalog(new CatalogApply(
                new SyncCatalogView(Device!.DeviceId, entries, new Dictionary<string, DeviceUpsertPayload>(), [], links, new Dictionary<string, DeviceRevokedPayload>()),
                [], null, [])));

        public void Claim(string localAccountId, string? logicalAccountId) => H.Ui.Invoke(() => H.Accounts.Find(localAccountId)!.LogicalAccountId = logicalAccountId);

        public RemoteLoginLookup ByLogical(string logicalAccountId) => H.OnUi(() => Coordinator.FindRemoteLoginTarget(null, logicalAccountId));

        public RemoteLoginLookup ByLocal(string localAccountId) => H.OnUi(() => Coordinator.FindRemoteLoginTarget(localAccountId, null));

        public void Dispose()
        {
            H.Ui.Invoke(() => Coordinator.Dispose());
            H.Dispose();
        }
    }

    /// <summary>只實作必要成員的同步橋替身（沒有覆寫 FindRemoteLoginTarget）</summary>
    private sealed class MinimalBridge : ISyncBridge
    {
        public bool HandleCommand(IAgentView view, string type, JObject msg) => false;

        public void OnAccountUpdated(string accountId)
        {
        }

        public void OnAccountsReordered()
        {
        }

        public void OnRoamingSettingChanged(string key, JToken value)
        {
        }

        public JObject? InitRemote() => null;

        public bool IsStarting => false;

        public JObject? DevicesCli() => null;

        public void DecorateAccounts(JArray accounts)
        {
        }

        public CatalogAccountTemplate? ClaimCatalogAccount(string? logicalAccountId, out string failure)
        {
            failure = "測試";
            return null;
        }

        public string? ExpectedEmail(string localAccountId) => null;
    }

    private static AccountUpsertPayload Entry(string logical, string provider, string identityKey, string label, string color, string? expectedEmail,
        string? expectedOrgId = null, int sortOrder = 0) =>
        new(logical, provider, identityKey, label, color, sortOrder, expectedEmail, expectedOrgId, null, 1, []);

    private static Dictionary<string, AccountUpsertPayload> Catalog() => new()
    {
        [LinkedCodex] = Entry(LinkedCodex, "Codex", "codex:codex@example.test", "目錄的 Codex", "#19C3A6", "codex@example.test"),
        [LinkedAndClaimed] = Entry(LinkedAndClaimed, "Claude", "claude:org-1:work@example.test", "公司的 Claude", "#D97757", "work@example.test", "org-1", 1),
        [Unlinked] = Entry(Unlinked, "Codex", "codex:new@example.test", "新的 Codex", "#abc", "new@example.test", null, 2),
        [KindMismatch] = Entry(KindMismatch, "Codex", "codex:odd@example.test", "種類不同", "#123456", "odd@example.test", null, 3),
        [LinkedToGone] = Entry(LinkedToGone, "Claude", "claude:gone@example.test", "已移除的", "#654321", "gone@example.test", null, 4),
        [FutureProvider] = Entry(FutureProvider, "Gemini", "gemini:x@example.test", "未來的種類", "#111111", null, null, 5),
        [AntigravityEntry] = Entry(AntigravityEntry, "Antigravity", "antigravity:x@example.test", "Antigravity", "#222222", null, null, 6),
    };

    private static Dictionary<string, string> Links() => new()
    {
        ["acc-codex"] = LinkedCodex,
        ["acc-claude2"] = LinkedAndClaimed,
        ["acc-gone"] = LinkedToGone,
    };

    private static async Task<Rig> PairedWithDirectoryAsync()
    {
        var rig = new Rig();
        await rig.StartAsync();
        rig.Claim("acc-claude", LinkedAndClaimed);
        rig.ApplyCatalog(Catalog(), Links());
        return rig;
    }

    [Fact]
    public async Task 連結優先_其次宣告()
    {
        using var rig = await PairedWithDirectoryAsync();

        var linked = rig.ByLogical(LinkedAndClaimed);
        linked.Code.Should().BeNull();
        linked.Target.Should().Be(new RemoteLoginTarget(LinkedAndClaimed, AgentProvider.Claude, "公司的 Claude", "acc-claude2", null,
            new RemoteLoginExpectation("claude:org-1:work@example.test", "work@example.test", "org-1")), "acc-claude2 有目錄的連結，acc-claude 只是宣告");

        // 伺服器拿掉連結之後：宣告的那個
        var links = Links();
        links.Remove("acc-claude2");
        rig.ApplyCatalog(Catalog(), links);
        rig.ByLogical(LinkedAndClaimed).Target!.LocalAccountId.Should().Be("acc-claude", "沒有連結時取宣告");
    }

    [Fact]
    public async Task 兩個本機帳戶宣告_取帳戶清單裡第一個()
    {
        using var rig = await PairedWithDirectoryAsync();
        var links = Links();
        links.Remove("acc-claude2");
        rig.ApplyCatalog(Catalog(), links);
        rig.Claim("acc-claude2", LinkedAndClaimed);

        rig.ByLogical(LinkedAndClaimed).Target!.LocalAccountId.Should().Be("acc-claude", "清單的順序是 acc-claude、acc-claude2、acc-codex");

        rig.H.Ui.Invoke(() => rig.H.Accounts.Reorder(["acc-claude2", "acc-claude", "acc-codex"]));
        rig.ByLogical(LinkedAndClaimed).Target!.LocalAccountId.Should().Be("acc-claude2", "照帳戶清單的順序，不是字典的列舉順序");
    }

    [Fact]
    public async Task 都沒有_給建立範本_名稱顏色與預期信箱取自目錄()
    {
        using var rig = await PairedWithDirectoryAsync();

        var lookup = rig.ByLogical(Unlinked);

        lookup.Code.Should().BeNull();
        lookup.Message.Should().BeNull();
        lookup.Target.Should().Be(new RemoteLoginTarget(Unlinked, AgentProvider.Codex, "新的 Codex", null,
            new CatalogAccountTemplate(Unlinked, AgentProvider.Codex, "新的 Codex", "#abc", "new@example.test"),
            new RemoteLoginExpectation("codex:new@example.test", "new@example.test", null)));
    }

    [Fact]
    public async Task 已有本機帳戶時不給建立範本()
    {
        using var rig = await PairedWithDirectoryAsync();

        var lookup = rig.ByLogical(LinkedCodex);

        lookup.Target.Should().Be(new RemoteLoginTarget(LinkedCodex, AgentProvider.Codex, "目錄的 Codex", "acc-codex", null,
            new RemoteLoginExpectation("codex:codex@example.test", "codex@example.test", null)));
        lookup.Target!.Create.Should().BeNull("acc-codex 已連結，不能再建一個");
    }

    [Fact]
    public async Task 未配對_還沒有目錄_已釋放_都是NotPaired()
    {
        using (var unpaired = new Rig(paired: false))
        {
            await unpaired.StartAsync();
            unpaired.ByLogical(Unlinked).Should().BeSameAs(RemoteLoginLookup.NotPaired);
            unpaired.ByLocal("acc-codex").Should().BeSameAs(RemoteLoginLookup.NotPaired);
        }

        RemoteLoginLookup.NotPaired.Should().Be(new RemoteLoginLookup(null, RemoteResultCodes.Invalid,
            AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.NotPaired)));

        using var rig = new Rig();
        await rig.StartAsync();
        rig.ByLogical(Unlinked).Should().BeSameAs(RemoteLoginLookup.NotPaired, "已配對但還沒收到目錄");
        rig.ApplyCatalog(Catalog(), Links());
        rig.ByLogical(Unlinked).Target.Should().NotBeNull();
        rig.H.Ui.Invoke(() => rig.Coordinator.Dispose());
        rig.ByLogical(Unlinked).Should().BeSameAs(RemoteLoginLookup.NotPaired, "已釋放");
        rig.ByLocal("acc-codex").Should().BeSameAs(RemoteLoginLookup.NotPaired);
    }

    [Fact]
    public async Task 目錄沒有_conflict_格式不對_invalid()
    {
        using var rig = await PairedWithDirectoryAsync();

        rig.ByLogical(NotInCatalog).Should().Be(RemoteLoginLookup.Rejected(RemoteResultCodes.Conflict,
            AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.NotInCatalog)));
        rig.ByLogical("la_x").Should().Be(RemoteLoginLookup.Rejected(RemoteResultCodes.Invalid,
            AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.InvalidId)));
    }

    [Fact]
    public async Task 目錄項目的種類不認得_conflict_Antigravity不能從目錄在這台登入_policy()
    {
        using var rig = await PairedWithDirectoryAsync();

        // 伺服器多出來的種類當成目錄沒有（同 ClaimCatalogAccount）
        rig.ByLogical(FutureProvider).Should().Be(RemoteLoginLookup.Rejected(RemoteResultCodes.Conflict,
            AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.NotInCatalog)));
        // Antigravity 2026-10-22 起進目錄，但一台只有一個 agy 登入：這台沒有連結時不能從目錄在這台登入
        rig.ByLogical(AntigravityEntry).Should().Be(RemoteLoginLookup.Rejected(RemoteResultCodes.Policy,
            AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.UnsupportedProvider)));
    }

    [Fact]
    public async Task 種類不符_conflict_連結宣告與本機id都一樣()
    {
        using var rig = await PairedWithDirectoryAsync();
        var mismatch = RemoteLoginLookup.Rejected(RemoteResultCodes.Conflict, RemoteAccountRules.TargetKindMismatchMessage);

        // 連結：Codex 的邏輯帳戶連到 Claude 的本機帳戶
        var links = Links();
        links["acc-claude"] = KindMismatch;
        rig.ApplyCatalog(Catalog(), links);
        rig.ByLogical(KindMismatch).Should().Be(mismatch, "連結");
        rig.ByLocal("acc-claude").Should().Be(mismatch, "本機 id 的連結");

        // 宣告：Claude 的本機帳戶宣告了 Codex 的邏輯帳戶
        rig.ApplyCatalog(Catalog(), Links());
        rig.Claim("acc-claude", KindMismatch);
        rig.ByLogical(KindMismatch).Should().Be(mismatch, "宣告");
        rig.ByLocal("acc-claude").Should().Be(mismatch, "本機 id 的宣告");
    }

    [Fact]
    public async Task 連結指著已移除的本機帳戶_conflict_不建第二個()
    {
        using var rig = await PairedWithDirectoryAsync();

        rig.ByLogical(LinkedToGone).Should().Be(RemoteLoginLookup.Rejected(RemoteResultCodes.Conflict,
            AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.AlreadyLinkedHere)));
    }

    [Fact]
    public async Task 本機id_有連結或宣告_預期身分與名稱取自目錄()
    {
        using var rig = await PairedWithDirectoryAsync();

        rig.ByLocal("acc-codex").Target.Should().Be(new RemoteLoginTarget(LinkedCodex, AgentProvider.Codex, "目錄的 Codex", "acc-codex", null,
            new RemoteLoginExpectation("codex:codex@example.test", "codex@example.test", null)), "連結");
        rig.ByLocal("acc-claude").Target.Should().Be(new RemoteLoginTarget(LinkedAndClaimed, AgentProvider.Claude, "公司的 Claude", "acc-claude", null,
            new RemoteLoginExpectation("claude:org-1:work@example.test", "work@example.test", "org-1")), "宣告");
    }

    [Fact]
    public async Task 本機id_沒有連結也沒有宣告_或目錄沒有那一筆_Expected是None()
    {
        using var rig = await PairedWithDirectoryAsync();
        var links = Links();
        links.Remove("acc-claude2");
        rig.ApplyCatalog(Catalog(), links);

        rig.ByLocal("acc-claude2").Target.Should().Be(new RemoteLoginTarget(null, AgentProvider.Claude, "Claude 副帳戶", "acc-claude2", null, RemoteLoginExpectation.None),
            "沒有連結也沒有宣告：名稱是本機的");

        rig.Claim("acc-claude2", NotInCatalog);
        rig.ByLocal("acc-claude2").Target.Should().Be(new RemoteLoginTarget(NotInCatalog, AgentProvider.Claude, "Claude 副帳戶", "acc-claude2", null, RemoteLoginExpectation.None),
            "宣告的邏輯帳戶不在目錄裡（已從目錄刪除）");
    }

    [Fact]
    public async Task 本機id_不存在_invalid()
    {
        using var rig = await PairedWithDirectoryAsync();

        rig.ByLocal("acc-none").Should().Be(RemoteLoginLookup.Rejected(RemoteResultCodes.Invalid, "這台電腦沒有這個帳戶"));
    }

    [Fact]
    public async Task 兩個id要恰好給一個_否則丟例外()
    {
        using var rig = await PairedWithDirectoryAsync();

        rig.Invoking(r => r.H.OnUi(() => r.Coordinator.FindRemoteLoginTarget(null, null))).Should().Throw<ArgumentException>();
        rig.Invoking(r => r.H.OnUi(() => r.Coordinator.FindRemoteLoginTarget("acc-codex", LinkedCodex))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 預設實作_替身沒有覆寫時回NotPaired()
    {
        ISyncBridge bridge = new MinimalBridge();

        bridge.FindRemoteLoginTarget(null, Unlinked).Should().BeSameAs(RemoteLoginLookup.NotPaired);
        bridge.FindRemoteLoginTarget("acc-codex", null).Should().BeSameAs(RemoteLoginLookup.NotPaired);
    }
}
