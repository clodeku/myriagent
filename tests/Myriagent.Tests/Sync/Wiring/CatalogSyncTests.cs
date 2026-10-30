using Myriagent.Agents;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Engine;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Wiring;

/// <summary>
/// 目錄的發布側（引擎＋假伺服器）：本機帳戶清單→link.upsert／link.removed、cli.state、使用者在這台的修改→account.update／prefs.update／device.update。
/// 最後一個測試把中樞的鏡像接到真的引擎（用到 HubHarness，所以整個類別放在 AppPaths 的集合裡循序執行）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class CatalogSyncTests
{
    private const long At = 1791600000000;

    private static LocalAccountSnapshot Local(
        string id = EngineHarness.LocalAccount,
        AgentProvider provider = AgentProvider.Claude,
        string label = "工作",
        string status = "ready",
        string? email = "me@example.com",
        string? orgId = "org",
        long checkedAt = At,
        int sortOrder = 0) =>
        new(id, provider, false, label, "#D97757", sortOrder, status, null, email, provider == AgentProvider.Claude ? orgId : null, "Max", null, false,
            new LinkSessions(0, 0, 0), checkedAt);

    private static LocalAccountSnapshot Codex(string id = "acc-2", string email = "dev@example.com") =>
        Local(id, AgentProvider.Codex, "Codex", email: email, orgId: null, sortOrder: 1);

    private static JObject Decode(SyncEvent e)
    {
        PayloadRules.TryDecode(e.Enc, e.Payload, 1 << 20, out var json).Should().BeTrue();
        return JObject.Parse(json);
    }

    private static List<JObject> Accepted(EngineHarness h, string kind) =>
        h.Server.Accepted.Where(e => e.Kind == kind).Select(Decode).ToList();

    private static async Task PostAsync(EngineHarness h, params SyncSignal[] signals)
    {
        foreach (var signal in signals)
            h.Engine.Post(signal);
        await h.IdleAsync();
        await h.AdvanceAsync(1);
    }

    private static SyncSignal.Accounts Accounts(params LocalAccountSnapshot[] accounts) => new(accounts, false);

    private static DeviceUpsertPayload DeviceView(string deviceId, string name) =>
        new(deviceId, name, "PC", "0.5.0", PairingRules.KeyStorageSoftware, "ABCD-EFGH", At, At, true, new DevicePolicy("none", false), null);

    /// <summary>第二個連結到這台的邏輯帳戶（本機 acc-2）</summary>
    private static AccountUpsertPayload SecondLinked(EngineHarness h, string logical, int sortOrder = 1, long version = 1)
    {
        var first = h.LinkedAccount(sortOrder: sortOrder, version: version);
        return first with
        {
            LogicalAccountId = logical,
            IdentityKey = "claude:org:two@example.com",
            Label = "第二個",
            Links = [first.Links[0] with { LocalAccountId = "acc-2", Email = "two@example.com" }],
        };
    }

    [Fact]
    public async Task 第一次上線_Claude與Codex帳戶各一筆link_upsert_Antigravity沒有()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();

        await PostAsync(h, Accounts(Local(), Codex(), Local("local-antigravity", AgentProvider.Antigravity, "Antigravity", email: null)));

        var upserts = Accepted(h, SyncEventKinds.LinkUpsert);
        upserts.Select(p => p.Value<string>("localAccountId")).Should().Equal(EngineHarness.LocalAccount, "acc-2");
        upserts.Select(p => p.Value<string>("provider")).Should().Equal("Claude", "Codex");
        upserts.Select(p => p.Value<string>("identityKey")).Should().Equal("claude:org:me@example.com", "codex:dev@example.com");
        upserts.Should().OnlyContain(p => p["logicalAccountId"] == null, "還沒連結");
        (await h.Inspect(v => v.Store!.LoadPublished().Keys.Order(StringComparer.Ordinal).ToList())).Should().Equal(EngineHarness.LocalAccount, "acc-2");
        h.Server.Accepted.Should().HaveCount(2);
    }

    [Fact]
    public async Task 只有CheckedAt改變_不再送()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        await PostAsync(h, Accounts(Local()));

        await PostAsync(h, Accounts(Local(checkedAt: At + 60_000)));
        await PostAsync(h, Accounts(Local(checkedAt: At + 120_000)));

        Accepted(h, SyncEventKinds.LinkUpsert).Should().ContainSingle();
        await PostAsync(h, Accounts(Local(status: "notLoggedIn")));
        Accepted(h, SyncEventKinds.LinkUpsert).Should().HaveCount(2, "狀態變了要送");
    }

    [Fact]
    public async Task 已連結帳戶_套用伺服器的名稱後_不再送link_upsert()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount(label: "工作"));
        await h.StartAsync();
        await PostAsync(h, Accounts(Local(label: "本機的名稱")));
        var first = Accepted(h, SyncEventKinds.LinkUpsert).Should().ContainSingle().Subject;
        first.Value<string>("logicalAccountId").Should().Be(EngineHarness.Logical);

        // 別台改了名稱：伺服器發 account.upsert，這台套用（AccountManager.ApplyCatalogAppearance）後下一份帳戶清單帶新的名稱
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount(label: "伺服器的名稱", version: 2));
        h.Server.Current!.Notify(SyncClasses.Catalog);
        await h.IdleAsync();
        h.Applier.Catalogs.Last().Appearance.Should().ContainSingle().Which.Should().Be((EngineHarness.LocalAccount, "伺服器的名稱", "#D97757"));
        await PostAsync(h, Accounts(Local(label: "伺服器的名稱", sortOrder: 3)));

        Accepted(h, SyncEventKinds.LinkUpsert).Should().ContainSingle("已連結帳戶的摘要不含名稱、顏色、順序");
    }

    [Fact]
    public async Task checking_不送()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();

        await PostAsync(h, Accounts(Local(status: "checking")));

        h.Server.Accepted.Should().BeEmpty();
        (await h.Inspect(v => v.Store!.LoadPublished().Count)).Should().Be(0);
    }

    [Fact]
    public async Task 刪掉帳戶_送link_removed()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        await PostAsync(h, Accounts(Local(), Codex()));

        await PostAsync(h, Accounts(Local()));

        Accepted(h, SyncEventKinds.LinkRemoved).Should().ContainSingle().Which.Value<string>("localAccountId").Should().Be("acc-2");
        (await h.Inspect(v => v.Store!.LoadPublished().Keys.ToList())).Should().Equal(EngineHarness.LocalAccount);
        await PostAsync(h, Accounts(Local()));
        Accepted(h, SyncEventKinds.LinkRemoved).Should().ContainSingle("只送一次");
    }

    [Fact]
    public async Task 清單唯讀_不送link_removed()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        await PostAsync(h, Accounts(Local(), Codex()));

        await PostAsync(h, new SyncSignal.Accounts([Local()], ListReadOnly: true));

        Accepted(h, SyncEventKinds.LinkRemoved).Should().BeEmpty("accounts.json 讀不到時清單不完整，不算刪除");
        (await h.Inspect(v => v.Store!.LoadPublished().Count)).Should().Be(2);
    }

    [Fact]
    public async Task pending的link_upsert被link_removed取代_伺服器只收到link_removed()
    {
        await using var h = new EngineHarness();
        h.Server.ConnectFailure = _ => EngineHarness.Transient();
        await h.StartAsync();
        h.Engine.Post(Accounts(Local(), Codex()));
        await h.IdleAsync();
        h.Engine.Post(Accounts(Local()));
        await h.IdleAsync();
        (await h.Inspect(v => v.Outbox.Count)).Should().Be(2, "acc-2 的 link.upsert 還沒送出，被 link.removed 取代（合併鍵 link:acc-2）");

        h.Server.ConnectFailure = null;
        await h.AdvanceAsync(20);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        var events = h.Server.Accepted.Select(e => (e.Kind, Decode(e).Value<string>("localAccountId"))).ToList();
        events.Should().Equal((SyncEventKinds.LinkUpsert, EngineHarness.LocalAccount), (SyncEventKinds.LinkRemoved, "acc-2"));
    }

    [Fact]
    public async Task account_deleted後_停送該帳戶()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        await h.StartAsync();
        await PostAsync(h, Accounts(Local()));
        Accepted(h, SyncEventKinds.LinkUpsert).Should().ContainSingle();

        h.Server.Publish(SyncEventKinds.AccountDeleted, new AccountDeletedPayload(EngineHarness.Logical, 2));
        h.Server.Current!.Notify(SyncClasses.Catalog);
        await h.IdleAsync();
        (await h.Inspect(v => v.Detached)).Should().ContainKey(EngineHarness.LocalAccount).WhoseValue.Should().Be(EngineHarness.Logical);
        (await h.Inspect(v => v.Store!.LoadPublished().ContainsKey(EngineHarness.LocalAccount))).Should().BeFalse();

        await PostAsync(h, Accounts(Local(status: "notLoggedIn")));
        await PostAsync(h, Accounts(Local(status: "error", email: "other@example.com")));

        Accepted(h, SyncEventKinds.LinkUpsert).Should().ContainSingle("送了會讓伺服器復活被刪的邏輯帳戶");
        Accepted(h, SyncEventKinds.LinkRemoved).Should().BeEmpty();
    }

    [Fact]
    public async Task 之後又被復活_恢復發布()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        await h.StartAsync();
        await PostAsync(h, Accounts(Local()));
        h.Server.Publish(SyncEventKinds.AccountDeleted, new AccountDeletedPayload(EngineHarness.Logical, 2));
        h.Server.Current!.Notify(SyncClasses.Catalog);
        await h.IdleAsync();
        await PostAsync(h, Accounts(Local(status: "notLoggedIn")));
        Accepted(h, SyncEventKinds.LinkUpsert).Should().ContainSingle();

        // 別台的 link.upsert 讓伺服器復活了同一個邏輯帳戶（seq 更大的 account.upsert）
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount(version: 3));
        h.Server.Current!.Notify(SyncClasses.Catalog);
        await h.IdleAsync();
        await h.AdvanceAsync(1);

        (await h.Inspect(v => v.Detached!.Count)).Should().Be(0);
        var upserts = Accepted(h, SyncEventKinds.LinkUpsert);
        upserts.Should().HaveCount(2, "解除 detached 後以最後的快照重新發布");
        upserts[1].Value<string>("status").Should().Be("notLoggedIn");
        upserts[1].Value<string>("logicalAccountId").Should().Be(EngineHarness.Logical);
    }

    [Fact]
    public async Task 伺服器重設後_摘要清空並全部重送()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        var cli = CliStateRules.Build(new Dictionary<AgentProvider, DeviceCliView> { [AgentProvider.Claude] = new(true, "2.1.300", "native", null, false, 0, 0, null, null) });
        await PostAsync(h, Accounts(Local()), new SyncSignal.Cli(cli));
        Accepted(h, SyncEventKinds.LinkUpsert).Should().ContainSingle();
        Accepted(h, SyncEventKinds.CliState).Should().ContainSingle();
        (await h.Inspect(v => v.Store!.LoadPublished().Count)).Should().Be(2, "帳戶與 #cli");

        // 伺服器從備份還原（沒有遺失這台的事件）：換了實例，D15 清空 published，以最後的快照重送
        h.Server.Restore(nextDeviceSeqAtBackup: h.Server.NextDeviceSeq, highAtBackup: h.Server.High);
        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(2);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        Accepted(h, SyncEventKinds.LinkUpsert).Should().HaveCount(2);
        Accepted(h, SyncEventKinds.CliState).Should().HaveCount(2);
        (await h.Inspect(v => v.Store!.LoadPublished().Count)).Should().Be(2);
        h.Server.DoubleApplied.Should().Be(0);

        // FullResync 也一樣
        await PostAsync(h, new SyncSignal.FullResync());
        Accepted(h, SyncEventKinds.LinkUpsert).Should().HaveCount(3);
        Accepted(h, SyncEventKinds.CliState).Should().HaveCount(3);
    }

    [Fact]
    public async Task cli_state只含兩個供應商()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        var view = new DeviceCliView(true, "1.3.5", "native", null, false, 0, 0, null, null);
        var cli = CliStateRules.Build(new Dictionary<AgentProvider, DeviceCliView>
        {
            [AgentProvider.Antigravity] = view,
            [AgentProvider.Codex] = view with { Version = "0.50.0" },
            [AgentProvider.Claude] = view with { Version = "2.1.300" },
        });

        await PostAsync(h, new SyncSignal.Cli(cli));
        await PostAsync(h, new SyncSignal.Cli(CliStateRules.Build(new Dictionary<AgentProvider, DeviceCliView>
        {
            [AgentProvider.Claude] = view with { Version = "2.1.300" },
            [AgentProvider.Codex] = view with { Version = "0.50.0" },
        })));

        var payload = Accepted(h, SyncEventKinds.CliState).Should().ContainSingle("內容相同（Antigravity 本來就不送）不再送").Subject;
        ((JObject)payload["providers"]!).Properties().Select(p => p.Name).Should().Equal("Claude", "Codex");
        payload["providers"]!["Claude"]!.Value<string>("version").Should().Be("2.1.300");
    }

    [Fact]
    public async Task 帳戶改名_account_update用套用後的值與目錄版本()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount(version: 5));
        await h.StartAsync();

        await PostAsync(h,
            new SyncSignal.AccountEdited(EngineHarness.LocalAccount, "工作", "#d97757"),
            new SyncSignal.AccountEdited("acc-unlinked", "沒連結", "#123456"));
        h.Server.Accepted.Should().BeEmpty("和目錄的值相同（顏色正規化後）、沒連結的都不送");

        await PostAsync(h, new SyncSignal.AccountEdited(EngineHarness.LocalAccount, "公司帳號", "#abc"));

        var update = Accepted(h, SyncEventKinds.AccountUpdate).Should().ContainSingle().Subject;
        update.Value<string>("logicalAccountId").Should().Be(EngineHarness.Logical);
        update.Value<long>("baseVersion").Should().Be(5);
        update.Value<string>("label").Should().Be("公司帳號");
        update.Value<string>("color").Should().Be("#AABBCC");
        update["sortOrder"].Should().BeNull();
    }

    [Fact]
    public async Task 改名後改回原值_蓋掉還沒送出的修改()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount(label: "工作", version: 2));
        await h.StartAsync();
        h.Server.ConnectFailure = _ => EngineHarness.Transient();
        h.Server.Current!.Drop();
        await h.IdleAsync();

        h.Engine.Post(new SyncSignal.AccountEdited(EngineHarness.LocalAccount, "改過", "#D97757"));
        await h.IdleAsync();
        h.Engine.Post(new SyncSignal.AccountEdited(EngineHarness.LocalAccount, "工作", "#D97757"));
        await h.IdleAsync();
        h.Server.ConnectFailure = null;
        await h.AdvanceAsync(20);

        var update = Accepted(h, SyncEventKinds.AccountUpdate).Should().ContainSingle().Subject;
        update.Value<string>("label").Should().Be("工作", "改回目錄的原值時，還沒送出的「改過」要被蓋掉");
    }

    [Fact]
    public async Task 重排_account_update重新分配sortOrder()
    {
        await using var h = new EngineHarness();
        var second = SyncIds.NewLogicalAccountId();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount(sortOrder: 0, version: 4));
        h.Server.Publish(SyncEventKinds.AccountUpsert, SecondLinked(h, second, sortOrder: 1, version: 7));
        await h.StartAsync();

        await PostAsync(h, new SyncSignal.AccountsReordered(["acc-2", "acc-local-only", EngineHarness.LocalAccount]));

        var updates = Accepted(h, SyncEventKinds.AccountUpdate);
        updates.Select(u => (u.Value<string>("logicalAccountId"), u.Value<int>("sortOrder"), u.Value<long>("baseVersion")))
            .Should().Equal((second, 0, 7L), (EngineHarness.Logical, 1, 4L));
        updates.Should().OnlyContain(u => u["label"] == null && u["color"] == null);

        // 伺服器套用後發 account.upsert（新順序、新版本）；之後同樣的順序和目錄相同，不再送
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount(sortOrder: 1, version: 5));
        h.Server.Publish(SyncEventKinds.AccountUpsert, SecondLinked(h, second, sortOrder: 0, version: 8));
        h.Server.Current!.Notify(SyncClasses.Catalog);
        await h.IdleAsync();
        h.Applier.Catalogs.Last().LocalOrder.Should().Equal("acc-2", EngineHarness.LocalAccount);
        await PostAsync(h, new SyncSignal.AccountsReordered(["acc-2", EngineHarness.LocalAccount]));
        Accepted(h, SyncEventKinds.AccountUpdate).Should().HaveCount(2);
    }

    /// <summary>
    /// 假伺服器照 MyriagentHub 的 Catalog.UpdateAccount 物化 account.update：baseVersion 不是目前版本就 conflict；接受時欄位 COALESCE、版本加一、
    /// 發 account.upsert（seq 緊接在裝置事件之後，不主動通知：引擎拉到之前目錄快取還是舊的版本）
    /// </summary>
    private static Dictionary<string, AccountUpsertPayload> VersionedAccounts(EngineHarness h, params AccountUpsertPayload[] accounts)
    {
        var rows = accounts.ToDictionary(a => a.LogicalAccountId, StringComparer.Ordinal);
        foreach (var row in accounts)
            h.Server.Publish(SyncEventKinds.AccountUpsert, row);
        h.Server.Materialize = e =>
        {
            if (e.Kind != SyncEventKinds.AccountUpdate)
                return null;
            var update = Decode(e).ToObject<AccountUpdatePayload>(Newtonsoft.Json.JsonSerializer.Create(SyncJson.Settings))!;
            var row = rows[update.LogicalAccountId];
            if (update.BaseVersion != row.Version)
                return "conflict";
            row = row with
            {
                Label = update.Label ?? row.Label,
                Color = update.Color ?? row.Color,
                SortOrder = update.SortOrder ?? row.SortOrder,
                Version = row.Version + 1,
            };
            rows[update.LogicalAccountId] = row;
            h.Server.Publish(SyncEventKinds.AccountUpsert, row);
            return null;
        };
        return rows;
    }

    [Fact]
    public async Task 離線時先改名再重排_同一個帳戶合成一筆_上線後伺服器全部接受()
    {
        await using var h = new EngineHarness();
        var second = SyncIds.NewLogicalAccountId();
        var rows = VersionedAccounts(h, h.LinkedAccount(sortOrder: 0, version: 5), SecondLinked(h, second, sortOrder: 1, version: 7));
        await h.StartAsync();
        h.Server.ConnectFailure = _ => EngineHarness.Transient();
        h.Server.Current!.Drop();
        await h.IdleAsync();

        h.Engine.Post(new SyncSignal.AccountEdited(EngineHarness.LocalAccount, "公司帳號", "#abc"));
        await h.IdleAsync();
        h.Engine.Post(new SyncSignal.AccountsReordered(["acc-2", EngineHarness.LocalAccount]));
        await h.IdleAsync();

        var pending = (await h.Inspect(v => v.Outbox)).Where(e => e.Kind == SyncEventKinds.AccountUpdate).ToList();
        pending.Should().HaveCount(2, "每個邏輯帳戶最多一筆（改名與重排合成一筆）");
        h.Server.ConnectFailure = null;
        await h.AdvanceAsync(20);

        (await h.Inspect(v => v.Outbox)).Where(e => e.Kind == SyncEventKinds.AccountUpdate).Should().OnlyContain(e => e.State == OutboxState.Acked, "沒有 conflict");
        rows[EngineHarness.Logical].Label.Should().Be("公司帳號");
        rows[EngineHarness.Logical].SortOrder.Should().Be(1);
        rows[second].SortOrder.Should().Be(0);
    }

    [Fact]
    public async Task 前一筆已送出還沒拉回_下一筆的baseVersion加上它()
    {
        await using var h = new EngineHarness();
        var second = SyncIds.NewLogicalAccountId();
        var rows = VersionedAccounts(h, h.LinkedAccount(sortOrder: 0, version: 5), SecondLinked(h, second, sortOrder: 1, version: 7));
        await h.StartAsync();

        // 改名送出、被接受（版本 6），伺服器發的 account.upsert 還沒拉回；接著重排
        await PostAsync(h, new SyncSignal.AccountEdited(EngineHarness.LocalAccount, "公司帳號", "#abc"));
        (await h.Inspect(v => v.Catalog!.Accounts[EngineHarness.Logical].Version)).Should().Be(5, "前提：目錄快取還是舊的版本");
        await PostAsync(h, new SyncSignal.AccountsReordered(["acc-2", EngineHarness.LocalAccount]));
        // 再改一次顏色（前兩筆都還沒拉回）
        await PostAsync(h, new SyncSignal.AccountEdited(EngineHarness.LocalAccount, "公司帳號", "#123456"));

        var updates = (await h.Inspect(v => v.Outbox)).Where(e => e.Kind == SyncEventKinds.AccountUpdate).ToList();
        updates.Should().OnlyContain(e => e.State == OutboxState.Acked, "baseVersion 加上已送出、還沒拉回的筆數：伺服器每一筆都接受");
        rows[EngineHarness.Logical].Should().Match<AccountUpsertPayload>(r => r.Label == "公司帳號" && r.Color == "#123456" && r.SortOrder == 1 && r.Version == 8);
        rows[second].SortOrder.Should().Be(0);

        // 拉回之後以目錄的版本為準（不再加已拉回的那幾筆）
        h.Server.Current!.Notify(SyncClasses.Catalog);
        await h.IdleAsync();
        (await h.Inspect(v => v.Catalog!.Accounts[EngineHarness.Logical].Version)).Should().Be(8);
        await PostAsync(h, new SyncSignal.AccountEdited(EngineHarness.LocalAccount, "改回來", "#123456"));
        (await h.Inspect(v => v.Outbox)).Where(e => e.Kind == SyncEventKinds.AccountUpdate).Should().OnlyContain(e => e.State == OutboxState.Acked);
        rows[EngineHarness.Logical].Label.Should().Be("改回來");
    }

    [Fact]
    public async Task sortOrder同分_拉到account_upsert不打亂本機順序()
    {
        await using var h = new EngineHarness();
        var second = SyncIds.NewLogicalAccountId();
        // 本機清單裡邏輯 id 較大的那個排前面：同分時依邏輯 id 排的話會被對調
        var (front, back) = string.CompareOrdinal(EngineHarness.Logical, second) > 0 ? (EngineHarness.LocalAccount, "acc-2") : ("acc-2", EngineHarness.LocalAccount);
        LocalAccountSnapshot Snapshot(string id, int index) => id == EngineHarness.LocalAccount
            ? Local(id, sortOrder: index)
            : Local(id, label: "第二個", email: "two@example.com", sortOrder: index);
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount(sortOrder: 0));
        h.Server.Publish(SyncEventKinds.AccountUpsert, SecondLinked(h, second, sortOrder: 0));

        await h.StartAsync(engine => engine.Post(Accounts(Snapshot(front, 0), Snapshot(back, 1))));

        h.Applier.Catalogs.Last(c => c.LocalOrder is not null).LocalOrder.Should().Equal([front, back], "同分照這台目前的順序");

        // 別台改了連結狀態（伺服器重發 account.upsert，sortOrder 還是 0）：照樣不動
        h.Server.Publish(SyncEventKinds.AccountUpsert, SecondLinked(h, second, sortOrder: 0, version: 2));
        h.Server.Current!.Notify(SyncClasses.Catalog);
        await h.IdleAsync();
        h.Applier.Catalogs.Last(c => c.LocalOrder is not null).LocalOrder.Should().Equal([front, back]);
    }

    [Fact]
    public async Task 同一台兩個本機帳戶身分相同_只發布一個_另一個不被目錄的名稱蓋掉()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        var twin = Local("acc-twin", label: "我的分身", status: "ready");

        // 都還沒連結：只發布清單裡前面的那個（兩個都送的話伺服器以 (邏輯帳戶, 裝置) 互相覆蓋，連結來回翻轉）
        await PostAsync(h, Accounts(Local(), twin));
        await PostAsync(h, Accounts(Local(), twin with { Status = "notLoggedIn" }));
        await PostAsync(h, Accounts(Local(), twin with { Status = "ready", Sessions = new LinkSessions(1, 1, 0) }));
        Accepted(h, SyncEventKinds.LinkUpsert).Select(u => u.Value<string>("localAccountId")).Should().OnlyContain(id => id == EngineHarness.LocalAccount);

        // 伺服器把邏輯帳戶連到 acc-1：分身照樣不送、目錄的名稱只套到 acc-1
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount(label: "目錄的名稱", version: 2));
        h.Server.Current!.Notify(SyncClasses.Catalog);
        await h.IdleAsync();
        await PostAsync(h, Accounts(Local(), twin with { Status = "error" }));

        Accepted(h, SyncEventKinds.LinkUpsert).Select(u => u.Value<string>("localAccountId")).Should().OnlyContain(id => id == EngineHarness.LocalAccount);
        h.Applier.Catalogs.SelectMany(c => c.Appearance).Select(a => a.LocalAccountId).Should().OnlyContain(id => id == EngineHarness.LocalAccount);
        (await h.Inspect(v => v.Store!.LoadPublished().ContainsKey("acc-twin"))).Should().BeFalse("沒發布就不記摘要");
        UsageSyncRules.ShouldPoll(h.Host.LastPoll, "acc-twin", h.Time.GetUtcNow()).Should().BeTrue("用量閘門照「沒有連結」：查本機");
    }

    [Fact]
    public async Task setting_prefs_update的baseVersion取目錄版本_沒設過為0()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.PrefsSet, new PrefsSetPayload("theme", "dark", 3));
        await h.StartAsync();

        await PostAsync(h,
            new SyncSignal.PrefChanged("theme", new JValue("light")),
            new SyncSignal.PrefChanged("fontSize", new JValue(15L)),
            new SyncSignal.PrefChanged("model", new JValue("opus")),
            new SyncSignal.PrefChanged("fontSize", new JValue("15")));

        var updates = Accepted(h, SyncEventKinds.PrefsUpdate);
        updates.Select(u => (u.Value<string>("key"), u["value"]!.ToString(), u.Value<long>("baseVersion")))
            .Should().Equal(("theme", "light", 3L), ("fontSize", "15", 0L));
        updates[1]["value"]!.Type.Should().Be(JTokenType.Integer, "fontSize 一律是 JSON 整數；字串的被 PrefsRules 擋下");
    }

    [Fact]
    public async Task deviceRename_未知或已撤銷的裝置不送()
    {
        await using var h = new EngineHarness();
        var home = SyncIds.NewDeviceId();
        var gone = SyncIds.NewDeviceId();
        h.Server.Publish(SyncEventKinds.DeviceUpsert, DeviceView(home, "HOME-PC"));
        h.Server.Publish(SyncEventKinds.DeviceUpsert, DeviceView(gone, "OLD-PC"));
        h.Server.Publish(SyncEventKinds.DeviceRevoked, new DeviceRevokedPayload(gone, At, DeviceRegistryRules.RevokedCompromised));
        await h.StartAsync();

        await PostAsync(h,
            new SyncSignal.DeviceRenamed(SyncIds.NewDeviceId(), "不存在"),
            new SyncSignal.DeviceRenamed(gone, "撤銷的"),
            new SyncSignal.DeviceRenamed(home, "   "),
            new SyncSignal.DeviceRenamed(home, "HOME-PC"),
            new SyncSignal.DeviceRenamed(home, "  家裡的電腦  "));

        var update = Accepted(h, SyncEventKinds.DeviceUpdate).Should().ContainSingle().Subject;
        update.Value<string>("deviceId").Should().Be(home);
        update.Value<string>("name").Should().Be(PairingRules.CleanDeviceName("  家裡的電腦  "));
    }

    [Fact]
    public async Task Claude沒有orgId_未連結_不發布()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();

        await PostAsync(h, Accounts(Local(orgId: null)));

        h.Server.Accepted.Should().BeEmpty("沒有 org 的 Claude 帳戶身分不確定，沒連結也沒發布過就不送（Q1）");
    }

    [Fact]
    public async Task Claude沒有orgId_已連結_email相同沿用_不同送claude冒號email()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        await PostAsync(h, Accounts(Local()));

        await PostAsync(h, Accounts(Local(orgId: null, status: "notLoggedIn")));
        await PostAsync(h, Accounts(Local(orgId: null, email: "Other@Example.com")));

        Accepted(h, SyncEventKinds.LinkUpsert).Select(p => p.Value<string>("identityKey"))
            .Should().Equal("claude:org:me@example.com", "claude:org:me@example.com", "claude:other@example.com");
    }

    [Fact]
    public async Task 登入到期改變_重送link_upsert()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        await PostAsync(h, Accounts(Local() with { LoginExpiresAt = 1794000000000 }));

        await PostAsync(h, Accounts(Local(checkedAt: At + 60_000) with { LoginExpiresAt = 1794000000000 }));
        Accepted(h, SyncEventKinds.LinkUpsert).Should().ContainSingle("登入到期沒變");

        await PostAsync(h, Accounts(Local() with { LoginExpiresAt = 1796000000000 }));
        await PostAsync(h, Accounts(Codex() with { LoginExpiresAt = 1796000000000 }, Local() with { LoginExpiresAt = 1796000000000 }));

        var upserts = Accepted(h, SyncEventKinds.LinkUpsert);
        upserts.Where(p => p.Value<string>("provider") == "Claude").Select(p => p.Value<long?>("loginExpiresAt"))
            .Should().Equal(1794000000000, 1796000000000);
        upserts.Single(p => p.Value<string>("provider") == "Codex")["loginExpiresAt"].Should().BeNull("Codex 一律不送登入到期");
    }

    // ── 「在這台登入」的宣告（WP3.4 第 5 步） ──

    private const string ClaimX = "la_0123456789ABCDEFGHJKMNPQRS";

    private const string ClaimY = "la_0123456789ABCDEFGHJKMNPQRT";

    /// <summary>目錄裡一個只連到別台的邏輯帳戶</summary>
    private static AccountUpsertPayload OtherDeviceAccount(string logical, string provider, string identityKey, string email) =>
        new(logical, provider, identityKey, "別台的帳戶", "#5B8DEF", 5, email, null, null, 1,
        [
            new AccountLinkView(SyncIds.NewDeviceId(), "acc-other", CatalogSyncRules.ConfigKindIsolated, "ready", null, email, null, null,
                null, null, false, true, null, null, false),
        ]);

    [Fact]
    public async Task 宣告的新帳戶_還沒登入也送link_upsert帶logicalAccountId()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, OtherDeviceAccount(ClaimX, "Codex", "codex:claim@example.com", "claim@example.com"));
        await h.StartAsync();

        await PostAsync(h, Accounts(Codex() with { Email = null, Status = "notLoggedIn", ClaimedLogicalAccountId = ClaimX }));

        var upsert = Accepted(h, SyncEventKinds.LinkUpsert).Should().ContainSingle().Subject;
        upsert.Value<string>("logicalAccountId").Should().Be(ClaimX);
        upsert["identityKey"].Should().BeNull("還沒登入：身分不知道，靠宣告連結");
        upsert.Value<string>("status").Should().Be("notLoggedIn");

        // 宣告了目錄沒有的（或格式不對的）不帶：沒有身分就不送
        await using var other = new EngineHarness();
        await other.StartAsync();
        await PostAsync(other, Accounts(Codex() with { Email = null, ClaimedLogicalAccountId = ClaimX }));
        other.Server.Accepted.Should().BeEmpty("目錄裡沒有這個邏輯帳戶");
    }

    [Fact]
    public async Task 兩個本機帳戶宣告同一個_只有清單第一個帶()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, OtherDeviceAccount(ClaimX, "Codex", "codex:claim@example.com", "claim@example.com"));
        await h.StartAsync();
        var first = Codex("acc-a", "claim@example.com") with { ClaimedLogicalAccountId = ClaimX };
        var second = Codex("acc-b", "other@example.com") with { ClaimedLogicalAccountId = ClaimX };

        await PostAsync(h, Accounts(first, second));

        var upserts = Accepted(h, SyncEventKinds.LinkUpsert);
        upserts.Select(p => (p.Value<string>("localAccountId"), p.Value<string>("logicalAccountId"), p.Value<string>("identityKey")))
            .Should().Equal(("acc-a", ClaimX, "codex:claim@example.com"), ("acc-b", null, "codex:other@example.com"));

        // 順序對調：換成另一個帶（清單裡的第一個）；兩個的摘要都變了，各重送一次
        await PostAsync(h, Accounts(second, first));
        Accepted(h, SyncEventKinds.LinkUpsert).Skip(2).Select(p => (p.Value<string>("localAccountId"), p.Value<string>("logicalAccountId")))
            .Should().BeEquivalentTo([("acc-b", ClaimX), ("acc-a", (string?)null)]);
    }

    [Fact]
    public async Task 目錄已把它對到別的_以目錄為準()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, h.LinkedAccount());
        h.Server.Publish(SyncEventKinds.AccountUpsert, OtherDeviceAccount(ClaimX, "Claude", "claude:org:me@example.com", "me@example.com"));
        await h.StartAsync();

        await PostAsync(h, Accounts(Local() with { ClaimedLogicalAccountId = ClaimX }, Local("acc-2", email: "two@example.com", sortOrder: 1) with { ClaimedLogicalAccountId = EngineHarness.Logical }));

        var upserts = Accepted(h, SyncEventKinds.LinkUpsert);
        upserts.Single(p => p.Value<string>("localAccountId") == EngineHarness.LocalAccount).Value<string>("logicalAccountId")
            .Should().Be(EngineHarness.Logical, "目錄的對應優先（和伺服器相同：先找這個本機帳戶已有的連結）");
        upserts.Single(p => p.Value<string>("localAccountId") == "acc-2")["logicalAccountId"]
            .Should().BeNull("這台已經有本機帳戶連到這個邏輯帳戶：別的帳戶宣告它不帶（否則伺服器把那一格蓋成 acc-2），照身分鍵連結");
    }

    [Fact]
    public async Task 宣告的Claude帳戶_org不明而且email和目錄不同_送claude_email()
    {
        await using var h = new EngineHarness();
        h.Server.Publish(SyncEventKinds.AccountUpsert, OtherDeviceAccount(ClaimX, "Claude", "claude:org-x:c@example.com", "c@example.com"));
        h.Server.Publish(SyncEventKinds.AccountUpsert, OtherDeviceAccount(ClaimY, "Claude", "claude:org-y:d@example.com", "d@example.com"));
        await h.StartAsync();

        await PostAsync(h, Accounts(
            Local("acc-a", orgId: null, email: "wrong@example.com") with { ClaimedLogicalAccountId = ClaimX },
            Local("acc-b", orgId: null, email: "D@Example.com") with { ClaimedLogicalAccountId = ClaimY }));

        var upserts = Accepted(h, SyncEventKinds.LinkUpsert);
        upserts.Select(p => (p.Value<string>("localAccountId"), p.Value<string>("logicalAccountId"), p.Value<string>("identityKey")))
            .Should().Equal(("acc-a", ClaimX, "claude:wrong@example.com"), ("acc-b", ClaimY, null));
    }

    [Fact]
    public async Task link_upsert的payload不含權杖()
    {
        const string access = "sk-ant-oat01-FIXTURE-ACCESS-0123456789";
        const string refresh = "sk-ant-ort01-FIXTURE-REFRESH-0123456789";
        const string codexToken = "eyFIXTURE-CODEX-ID-TOKEN-0123456789";
        await using var engine = new EngineHarness();
        await engine.StartAsync();
        using var hub = new HubHarness(alwaysPost: true);
        Directory.CreateDirectory(hub.Claude2.ConfigDirectory!);
        File.WriteAllText(Path.Combine(hub.Claude2.ConfigDirectory!, ".credentials.json"),
            new JObject { ["claudeAiOauth"] = new JObject { ["accessToken"] = access, ["refreshToken"] = refresh, ["expiresAt"] = 1791700000000 } }.ToString());
        Directory.CreateDirectory(hub.Codex.ConfigDirectory!);
        File.WriteAllText(Path.Combine(hub.Codex.ConfigDirectory!, "auth.json"),
            new JObject { ["tokens"] = new JObject { ["id_token"] = codexToken, ["access_token"] = access, ["refresh_token"] = refresh } }.ToString());
        hub.OnUi(() =>
        {
            hub.Claude2.Email = "two@example.com";
            hub.Claude2.Plan = "Claude Max";
            hub.Accounts.GetState("acc-claude2").OrgId = "org-2";
            hub.Accounts.GetState("acc-claude2").AccessExpiresAt = 1791700000000;
            hub.Codex.Email = "dev@example.com";
            return 0;
        });
        var bridge = new HubBridge(engine.Engine);
        var mirror = hub.OnUi(() => new SyncMirrorView(hub.Accounts, bridge));

        hub.Ui.Invoke(() => hub.Hub.AttachSyncView(mirror));
        await hub.Ui.DrainAsync();
        await engine.IdleAsync();
        await engine.AdvanceAsync(1);

        var upserts = Accepted(engine, SyncEventKinds.LinkUpsert);
        upserts.Select(p => p.Value<string>("localAccountId")).Should().Equal("acc-claude2", "acc-codex");
        upserts[0].Value<long?>("accessExpiresAt").Should().Be(1791700000000);
        foreach (var e in engine.Server.Accepted)
        {
            var json = Decode(e).ToString();
            json.Should().NotContain("FIXTURE", $"{e.Kind} 的 payload 不能帶憑證");
            json.Should().NotContain("Token", $"{e.Kind} 的 payload 不能有權杖欄位");
        }
    }
}
