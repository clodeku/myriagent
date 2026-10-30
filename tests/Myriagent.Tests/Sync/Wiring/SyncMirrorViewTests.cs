using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Wiring;

/// <summary>中樞的同步鏡像：從中樞本來就送的 accounts、cli 訊息組出同步快照（HubHarness 的宿主照正式環境一律 Post）</summary>
[Collection(AppPathsCollection.Name)]
public sealed class SyncMirrorViewTests
{
    /// <summary>包住真的鏡像：記下收到的每則訊息（深拷貝）與鏡像處理前後的文字</summary>
    private sealed class SpyMirror(SyncMirrorView inner) : IAgentView
    {
        private readonly List<(JObject Copy, string Before, string After)> received = [];

        public string Id => inner.Id;

        public bool IsPopout => inner.IsPopout;

        public IReadOnlyList<(JObject Copy, string Before, string After)> Received
        {
            get
            {
                lock (received)
                    return received.ToList();
            }
        }

        public IReadOnlyList<JObject> Of(string type) => Received.Select(r => r.Copy).Where(m => m.Value<string>("type") == type).ToList();

        public void PostToWeb(JObject message)
        {
            var before = message.ToString();
            inner.PostToWeb(message);
            lock (received)
                received.Add(((JObject)message.DeepClone(), before, message.ToString()));
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(Func<AppSettings>? loadSettings = null)
        {
            H = new HubHarness(loadSettings: loadSettings, alwaysPost: true);
            Engine = new RecordingSyncEngine();
            Bridge = new HubBridge(Engine);
            Mirror = H.OnUi(() => new SyncMirrorView(H.Accounts, Bridge));
            Spy = new SpyMirror(Mirror);
        }

        public HubHarness H { get; }

        public RecordingSyncEngine Engine { get; }

        public HubBridge Bridge { get; }

        public SyncMirrorView Mirror { get; }

        public SpyMirror Spy { get; }

        public void Attach() => H.Ui.Invoke(() => H.Hub.AttachSyncView(Spy));

        public IReadOnlyList<SyncSignal.Accounts> Accounts => Engine.Of<SyncSignal.Accounts>();

        public IReadOnlyList<SyncSignal.Cli> Cli => Engine.Of<SyncSignal.Cli>();

        public IReadOnlyList<SyncSignal.UsageObserved> Usage => Engine.Of<SyncSignal.UsageObserved>();

        public void Dispose() => H.Dispose();
    }

    private static UsageSnapshot Api(DateTimeOffset at, double percent = 42) =>
        new(at, UsageSyncRules.SourceApi, [new UsageWindow("session", "5 小時", percent, at.AddHours(2))], null, null, null, false);

    [Fact]
    public async Task 帳戶改名_送出一次Accounts()
    {
        using var rig = new Rig();
        rig.Attach();
        await rig.H.Ui.DrainAsync();
        rig.Accounts.Should().ContainSingle("掛上時送一份");
        var mark = rig.H.Main.Mark();

        rig.H.Command(rig.H.Main, "accountUpdate", new { accountId = "acc-claude", label = "公司帳號" });
        await rig.H.Main.WaitForAsync("accounts", m => m["accounts"]!.Any(a => a.Value<string>("label") == "公司帳號"), mark);
        await rig.H.Ui.DrainAsync();
        // 再送一次一樣的內容（下一次節流到期、或重新掛上）：和上次相同就不送
        rig.Attach();
        await rig.H.Ui.DrainAsync();

        rig.Accounts.Should().HaveCount(2);
        rig.Accounts[^1].Snapshot.Single(a => a.Id == "acc-claude").Label.Should().Be("公司帳號");
        rig.Accounts[^1].ListReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task 同一份JObject_鏡像處理前後ToString相同()
    {
        using var rig = new Rig();
        rig.H.OnUi(() =>
        {
            rig.H.Accounts.SeedUsage("acc-claude", Api(DateTimeOffset.UtcNow));
            return 0;
        });
        rig.Attach();
        rig.H.Command(rig.H.Main, "accountUpdate", new { accountId = "acc-codex", label = "Codex 工作" });
        await rig.H.Main.WaitForAsync("accounts", m => m["accounts"]!.Any(a => a.Value<string>("label") == "Codex 工作"));
        await rig.H.Ui.DrainAsync();

        // 另外直接餵一則含 remoteOnly、型別不對的欄位的訊息
        var crafted = (JObject)rig.Spy.Of("accounts")[^1].DeepClone();
        ((JArray)crafted["accounts"]!).Insert(0, new JObject { ["id"] = "la_remote", ["provider"] = "Claude", ["remoteOnly"] = true });
        ((JArray)crafted["accounts"]!).Add(new JObject { ["id"] = 5, ["provider"] = 7, ["sessions"] = "x" });
        rig.H.Ui.Invoke(() => rig.Spy.PostToWeb(crafted));

        var handled = rig.Spy.Received.Where(r => r.Copy.Value<string>("type") is "accounts" or "cli").ToList();
        handled.Count.Should().BeGreaterThanOrEqualTo(4);
        handled.Should().OnlyContain(r => r.Before == r.After, "中樞把同一個 JObject 發給所有視窗，鏡像不能改它");
    }

    [Fact]
    public async Task CLI狀態沒變_不送Cli()
    {
        using var rig = new Rig();
        rig.Attach();
        rig.Attach();
        await rig.H.Ui.DrainAsync();

        rig.Cli.Should().ContainSingle("第二次的內容相同");
        rig.Accounts.Should().ContainSingle();
        var payload = rig.Cli.Single().State;
        payload.Providers.Keys.Should().BeEquivalentTo(["Claude", "Codex", "Antigravity", "Grok"], "四種都交給引擎，送出前再依 prov1 篩（CliStateRules.ForServer）");

        var changed = (JObject)rig.Spy.Of("cli")[^1].DeepClone();
        changed["cli"]!["Codex"]!["version"] = "0.99.0";
        rig.H.Ui.Invoke(() => rig.Spy.PostToWeb(changed));

        rig.Cli.Should().HaveCount(2);
        rig.Cli[^1].State.Providers["Codex"].Version.Should().Be("0.99.0");
    }

    [Fact]
    public async Task 用量FetchedAt改變_才送UsageObserved()
    {
        using var rig = new Rig();
        var first = DateTimeOffset.FromUnixTimeMilliseconds(1791600000000);
        rig.H.OnUi(() =>
        {
            rig.H.Accounts.SeedUsage("acc-claude", Api(first));
            return 0;
        });
        rig.Attach();
        rig.Attach();
        await rig.H.Ui.DrainAsync();

        rig.Usage.Should().ContainSingle("fetchedAt 沒變就不再送");
        rig.Usage[0].LocalAccountId.Should().Be("acc-claude");
        rig.Usage[0].Snapshot.FetchedAt.Should().Be(first);

        var second = first.AddMinutes(5);
        var mark = rig.H.Main.Mark();
        rig.H.OnUi(() =>
        {
            rig.H.Accounts.ApplySharedUsage([("acc-claude", Api(second, 50), second, true)]);
            return 0;
        });
        await rig.H.Main.WaitForAsync("accounts",
            m => m["accounts"]!.Any(a => a["usage"] is JObject u && u.Value<long?>("fetchedAt") == second.ToUnixTimeMilliseconds()), mark);
        await rig.H.Ui.DrainAsync();

        rig.Usage.Should().HaveCount(2);
        rig.Usage[1].Snapshot.FetchedAt.Should().Be(second);
        rig.Usage[1].Snapshot.Source.Should().Be(UsageSyncRules.SourceApi);
    }

    [Fact]
    public void remoteOnly項目_略過()
    {
        using var rig = new Rig();
        var message = new JObject
        {
            ["type"] = "accounts",
            ["accounts"] = new JArray
            {
                new JObject { ["id"] = "acc-claude", ["provider"] = "Claude", ["label"] = "Claude 主帳戶", ["color"] = "#D97757", ["status"] = "ready" },
                new JObject { ["id"] = "la_REMOTE", ["provider"] = "Claude", ["label"] = "別台的帳戶", ["remoteOnly"] = true, ["status"] = "ready" },
                new JObject { ["id"] = "acc-codex", ["provider"] = "Codex", ["label"] = "Codex 帳戶", ["color"] = "#19C3A6", ["status"] = "ready" },
                new JObject { ["id"] = "acc-future", ["provider"] = "FutureAgent", ["status"] = "ready" },
            },
        };

        rig.H.Ui.Invoke(() => rig.Mirror.PostToWeb(message));

        var snapshot = rig.Accounts.Should().ContainSingle().Subject.Snapshot;
        snapshot.Select(a => (a.Id, a.SortOrder)).Should().Equal(("acc-claude", 0), ("acc-codex", 1));
        snapshot.Select(a => a.Provider).Should().Equal(AgentProvider.Claude, AgentProvider.Codex);
    }

    [Fact]
    public async Task 鏡像收不到啟動警告toast()
    {
        using var rig = new Rig(loadSettings: () =>
        {
            File.WriteAllText(AppPaths.SettingsFile, """{ "Theme": "light" }""");
            using var locked = new FileStream(AppPaths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return AppSettings.Load();
        });
        rig.Attach();

        rig.H.Command(rig.H.Main, "ready");
        await rig.H.Ui.DrainAsync();

        rig.H.Main.Messages("toast").Should().Contain(t => t.Value<string>("text")!.Contains("設定檔暫時讀不到"));
        rig.Spy.Of("toast").Should().BeEmpty("鏡像永遠不送 ready，啟動警告只給第一個就緒的視窗");
        rig.Spy.Of("init").Should().BeEmpty();
    }

    [Fact]
    public void AttachSyncView_立刻收到accounts與cli()
    {
        using var rig = new Rig();

        rig.Attach();

        rig.Spy.Received.Select(r => r.Copy.Value<string>("type")).Should().Equal("accounts", "cli");
        rig.Spy.Of("accounts")[0]["accounts"]!.Select(a => a.Value<string>("id")).Should().Equal("acc-claude", "acc-claude2", "acc-codex");
        rig.Accounts.Should().ContainSingle();
        rig.Cli.Should().ContainSingle();
    }

    [Fact]
    public async Task SortOrder是清單索引_CheckedAt取自AccountManager()
    {
        using var rig = new Rig();
        var checkedAt = new DateTime(2026, 10, 16, 9, 30, 0, DateTimeKind.Local);
        rig.H.OnUi(() =>
        {
            rig.H.Accounts.GetState("acc-claude2").CheckedAt = checkedAt;
            rig.H.Accounts.GetState("acc-claude").OrgId = "org-1";
            rig.H.Accounts.GetState("acc-claude").AccessExpiresAt = 1791700000000;
            return 0;
        });
        rig.Attach();
        var mark = rig.H.Main.Mark();

        rig.H.Command(rig.H.Main, "accountReorder", new { ids = new[] { "acc-codex", "acc-claude", "acc-claude2" } });
        await rig.H.Main.WaitForAsync("accounts", m => m["accounts"]![0]!.Value<string>("id") == "acc-codex", mark);
        await rig.H.Ui.DrainAsync();

        var snapshot = rig.Accounts[^1].Snapshot;
        snapshot.Select(a => (a.Id, a.SortOrder)).Should().Equal(("acc-codex", 0), ("acc-claude", 1), ("acc-claude2", 2));
        snapshot.Single(a => a.Id == "acc-claude2").CheckedAt.Should().Be(new DateTimeOffset(checkedAt).ToUnixTimeMilliseconds());
        snapshot.Single(a => a.Id == "acc-codex").CheckedAt.Should().Be(0, "還沒檢查過");
        var claude = snapshot.Single(a => a.Id == "acc-claude");
        claude.OrgId.Should().Be("org-1");
        claude.AccessExpiresAt.Should().Be(1791700000000);
        claude.IsSystemDefault.Should().BeTrue();
        claude.Provider.Should().Be(AgentProvider.Claude);
        snapshot.Single(a => a.Id == "acc-claude2").IsSystemDefault.Should().BeFalse();
    }

    [Fact]
    public void 快照帶LoginExpiresAt與宣告的邏輯帳戶()
    {
        using var rig = new Rig();
        rig.H.OnUi(() =>
        {
            rig.H.Accounts.GetState("acc-claude").LoginExpiresAt = 1794000000000;
            rig.H.Accounts.Find("acc-codex")!.LogicalAccountId = "la_0123456789ABCDEFGHJKMNPQRS";
            return 0;
        });

        rig.Attach();

        var snapshot = rig.Accounts.Should().ContainSingle().Subject.Snapshot;
        var claude = snapshot.Single(a => a.Id == "acc-claude");
        claude.LoginExpiresAt.Should().Be(1794000000000);
        claude.ClaimedLogicalAccountId.Should().BeNull();
        var codex = snapshot.Single(a => a.Id == "acc-codex");
        codex.ClaimedLogicalAccountId.Should().Be("la_0123456789ABCDEFGHJKMNPQRS");
        codex.LoginExpiresAt.Should().BeNull();
        snapshot.Single(a => a.Id == "acc-claude2").ClaimedLogicalAccountId.Should().BeNull();
    }
}
