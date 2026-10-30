using System.Security.Cryptography;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>遠端指令入口的測試共用（WP4.4 步 5 起）：驗過的信封內容、指令、這台的政策</summary>
internal static class RemoteEntrySamples
{
    public const string IssuerLabel = "Chrome・Windows";

    private static long nextCid = 1_791_600_000_000;

    /// <summary>新的指令 id（ULID，每次不同）</summary>
    public static string NewCid() => SyncIds.NewUlid(Interlocked.Increment(ref nextCid));

    /// <summary>
    /// 驗過的信封內容：路由 Owner 的指令以 <c>cmd.conversationId</c> 當 conv（epoch 1），路由 Device 的不帶；dev 是 <see cref="RemoteSamples.DeviceId"/>，
    /// 時間都在伺服器的同一秒（5f 的時間窗是 null，一律新鮮）
    /// </summary>
    public static CommandEnvelopeBody Body(JObject cmd, int tier = 1, bool fullAccess = false, string? cid = null)
    {
        var conv = cmd.Value<string>("conversationId");
        return new CommandEnvelopeBody("https://hub.test", cid ?? NewCid(), RemoteSamples.DeviceId, "conn-1", conv, conv is null ? null : 1, cmd, tier, fullAccess,
            false, 2_000, 2_000, 32_000, new CommandIssuer(CommandIssuerKinds.Browser, IssuerLabel), new CommandAuth(1_000, false));
    }

    public static JObject Cmd(string cmd, string? conversationId = null, object? args = null)
    {
        var command = HubHarness.MakeCommand(cmd, args);
        if (conversationId != null)
            command["conversationId"] = conversationId;
        return command;
    }

    /// <summary>這台允許遠端操作（Control）；allowFullAccess＝也允許完全自動</summary>
    public static void AllowRemote(HubHarness h, bool allowFullAccess = false, bool paused = false)
        => h.Ui.Invoke(() =>
        {
            h.Settings.Sync.RemoteControlLevel = SyncSettings.LevelControl;
            h.Settings.Sync.AllowRemoteFullAccess = allowFullAccess;
            h.Settings.Sync.RemotePaused = paused;
        });

    /// <summary>經中樞的遠端指令入口執行（UI 執行緒），等閘門的結果</summary>
    public static async Task<RemoteOutcome> RunAsync(HubHarness h, CommandEnvelopeBody body, Recorder<JObject>? output = null)
        => await h.Ui.Invoke(() => h.Hub.ExecuteRemoteCommandAsync(body, m => output?.Add(m))).WaitAsync(TimeSpan.FromSeconds(10));

    public static void ShouldBeRejected(RemoteOutcome outcome, string code, string step)
    {
        outcome.State.Should().Be(RemoteResultStates.Rejected, $"{code}／{step}：{outcome.Message}");
        outcome.Code.Should().Be(code, outcome.Message);
        outcome.Step.Should().Be(step, outcome.Message);
        outcome.Message.Should().NotBeNullOrWhiteSpace();
    }
}

/// <summary>
/// 中樞的遠端指令入口（WP4.4 計畫步 5；D13、D16、D34、對齊 A9）：ExecuteRemoteCommandAsync 以信封的內容進閘門；授權者依擁有者指示放行，
/// 退路指令可以從遠端執行；閘門 5c 的帳戶一定是這台的本機帳戶（邏輯 id 或不存在回 invalid、在 5d 之前、什麼都不做）；
/// 已經有結果的 context 不能進閘門；回給瀏覽器的對話 id 是線上 id
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteEntryTests
{
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>對話資料夾數（還沒有任何對話時資料夾不存在，是 0）</summary>
    private static int ConversationFolders(HubHarness h) => Directory.Exists(h.Store.RootDirectory) ? Directory.GetDirectories(h.Store.RootDirectory).Length : 0;

    /// <summary>記下帳戶管理員要了哪些代理種類的 CLI（一律回 null：不跑真的 CLI）</summary>
    private static Recorder<AgentProvider> NoLauncher(HubHarness h)
    {
        var asked = new Recorder<AgentProvider>();
        h.Ui.Invoke(() => h.Accounts.ResolveLauncher = provider =>
        {
            asked.Add(provider);
            return null;
        });
        return asked;
    }

    [Fact]
    public void 授權者_預設是依擁有者指示放行()
    {
        OwnerTrustedRemoteAuthorizer.Instance.Authorize(null!, "delete", new JObject(), null!, new RemoteTierResult(2, true)).Should().BeNull();
    }

    [Fact]
    public async Task 遠端stop_停下執行中的代理_結果done()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.IsRunning(id).Should().BeTrue();

        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("stop", id), tier: 0));

        outcome.Should().Be(new RemoteOutcome(RemoteResultStates.Done, null, null, 0, false, null));
        await h.WaitUntilAsync(() => s.Interrupts == 1, "FakeSession 收到停止");
    }

    [Fact]
    public async Task 遠端rename_改名_結果done_settings的雜湊不變()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        await h.Ui.DrainAsync();
        h.Ui.Invoke(() => h.Settings.Save());
        var saved = Sha256(AppPaths.SettingsFile);
        h.Ui.Invoke(() => h.Settings.Save());
        Sha256(AppPaths.SettingsFile).Should().Be(saved, "前提：同一份設定連續存兩次內容相同");

        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("rename", id, new { title = "遠端改的" })));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        outcome.Tier.Should().Be(1);
        h.Conversation(id)!.Title.Should().Be("遠端改的");
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.Ui.DrainAsync();
        Sha256(AppPaths.SettingsFile).Should().Be(saved, "遠端改名不寫這台的持久設定（D15）");
    }

    // accountLogout 的本機 id 與邏輯 id／不存在的情境移到 AgentHubRemoteLogoutCliTests（WP4.8 步 8：有處理者、回報真的結果，一律經 RemoteAccountRig）
    [Theory]
    [InlineData("accountRemove", 2)]
    [InlineData("accountRefresh", 1)]
    [InlineData("accountLoginCancel", 1)]
    public async Task 帳戶指令_本機id_走退路_done(string cmd, int tier)
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var asked = NoLauncher(h);
        Directory.CreateDirectory(h.Codex.ConfigDirectory!);

        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteSamples.Command(cmd, ("accountId", "acc-codex")), tier));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        outcome.Tier.Should().Be(tier);
        switch (cmd)
        {
            case "accountRefresh":
                await h.WaitUntilAsync(() => asked.All.Contains(AgentProvider.Codex), "重新整理走到帳戶管理員（launcher 是 null，不跑 CLI）");
                break;
            case "accountRemove":
                h.OnUi(() => h.Accounts.Find("acc-codex")).Should().BeNull("本機帳戶照退路移除");
                Directory.Exists(h.Codex.ConfigDirectory!).Should().BeTrue("遠端移除帳戶一律保留資料夾（deleteData 當成 false）");
                break;
        }
    }

    [Theory]
    [InlineData("accountRemove", 2)]
    [InlineData("accountRefresh", 1)]
    [InlineData("accountLoginCancel", 1)]
    public async Task 帳戶指令_邏輯id或不存在_invalid_在5d之前_什麼都沒做(string cmd, int tier)
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h, allowFullAccess: true);
        var asked = NoLauncher(h);
        var accounts = h.OnUi(() => h.Accounts.Accounts.Select(a => a.Id).ToList());

        foreach (var accountId in new[] { RemoteSamples.LogicalAccountId, "acc-missing", "ACC-CODEX" })
        {
            var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteSamples.Command(cmd, ("accountId", accountId)), tier));
            RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Invalid, RemoteGateSteps.Conversation);
            outcome.Tier.Should().Be(tier, "在 5d 之前停下：層級是信封宣告的");
        }

        await h.Ui.DrainAsync();
        h.OnUi(() => h.Accounts.Accounts.Select(a => a.Id).ToList()).Should().Equal(accounts);
        asked.Count.Should().Be(0, "沒有走到帳戶管理員（D16：退路的帳戶指令不回假的 done）");
    }

    [Fact]
    public async Task accountRefresh_沒帶帳戶或null_是全部帳戶_不查帳戶()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        NoLauncher(h);

        var omitted = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteSamples.Command("accountRefresh")));
        var explicitNull = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteSamples.Command("accountRefresh", ("accountId", JValue.CreateNull()))));

        omitted.State.Should().Be(RemoteResultStates.Done, omitted.Message);
        explicitNull.State.Should().Be(RemoteResultStates.Done, explicitNull.Message);
    }

    [Fact]
    public async Task setAccount_帳戶不存在_invalid_對話不變()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h, allowFullAccess: true);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);

        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("setAccount", id, new { accountId = RemoteSamples.LogicalAccountId }), tier: 2));

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Invalid, RemoteGateSteps.Conversation);
        h.Conversation(id)!.AccountId.Should().Be("acc-claude");
    }

    [Fact]
    public async Task conversationCreate_帳戶不存在_invalid_在5d之前()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h, allowFullAccess: true);
        var asked = new List<string>();
        h.Ui.Invoke(() => h.Hub.AccountPreApprovedForTest = (account, _) =>
        {
            asked.Add(account.Id);
            return false;
        });
        var folders = ConversationFolders(h);

        foreach (var accountId in new[] { RemoteSamples.LogicalAccountId, "acc-missing" })
        {
            var create = RemoteSamples.Command(RemoteCommandCatalog.ConversationCreate, ("accountId", accountId), ("permissionMode", "Ask"), ("text", "hi"));
            var body = RemoteEntrySamples.Body(create);
            RemoteEntrySamples.ShouldBeRejected(await RemoteEntrySamples.RunAsync(h, body), RemoteResultCodes.Invalid, RemoteGateSteps.Conversation);
            h.Conversation(RemoteConversationIdRules.FromCommandId(body.Cid)!).Should().BeNull();
        }

        asked.Should().BeEmpty("5d 沒有跑（帳戶層級的預先放行偵測沒被問）");
        ConversationFolders(h).Should().Be(folders, "什麼都沒建");

        // 對照：本機帳戶過得了 5c，授權者放行之後由步 9 的處理者建立
        var local = RemoteSamples.Command(RemoteCommandCatalog.ConversationCreate, ("accountId", "acc-claude"), ("permissionMode", "Ask"), ("text", "hi"));
        var localBody = RemoteEntrySamples.Body(local);
        var created = await RemoteEntrySamples.RunAsync(h, localBody);
        created.State.Should().Be(RemoteResultStates.Done, created.Message);
        h.Conversation(RemoteConversationIdRules.FromCommandId(localBody.Cid)!).Should().NotBeNull();
        asked.Should().Equal("acc-claude");
    }

    [Fact]
    public async Task 授權之後沒有處理者的accountAdd仍回unsupported_step是handler()
    {
        // 原本拿 listFiles 當例子；WP4.7 合併後（WP4.7 計畫步 13）檔案指令有處理者了，改用還沒有處理者的帳戶登入（WP4.8）
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h, allowFullAccess: true);
        h.StartConversation(h.Main, h.Claude);
        var spec = RemoteCommandCatalog.Find("accountAdd")!;

        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteSamples.Required(spec), tier: spec.BaseTier));

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Unsupported, RemoteGateSteps.Handler);
    }

    [Fact]
    public async Task Begin_已有結果的context丟例外()
    {
        var rejected = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, null, null, 0, false, null, 2_000, false, RemoteEntrySamples.IssuerLabel);
        rejected.Reject(RemoteResultCodes.Busy, "測試", RemoteGateSteps.Route);
        FluentActions.Invoking(() => rejected.Begin(_ => { }, AgentHub.RemoteOutputWindow)).Should().Throw<InvalidOperationException>();

        // 經閘門：已經完成的 context 不會再跑一次（例如逾時之後才到的完成信號所屬的那一個）
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        var done = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, id, 1, 1, false, null, 2_000, false, RemoteEntrySamples.IssuerLabel);
        done.Complete();

        await FluentActions.Awaiting(() => h.Ui.Invoke(() => h.Hub.ExecuteRemoteAsync(done, RemoteEntrySamples.Cmd("rename", id, new { title = "不會改" }), _ => { })))
            .Should().ThrowAsync<InvalidOperationException>();
        h.Conversation(id)!.Title.Should().Be("第一則");
    }

    [Fact]
    public async Task 輸出的conversationId是線上id_資料夾名大寫也一樣()
    {
        const string upper = "ABCDEF0123456789ABCDEF0123456789";
        using var h = new HubHarness(seedStore: store => store.SaveConversation(new AgentConversation
        {
            Id = upper, AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "大寫的資料夾",
        }), status: AccountStatus.NotLoggedIn);
        var wire = upper.ToLowerInvariant();
        var output = new Recorder<JObject>();
        var mark = h.Main.Mark();
        var context = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, wire, 1, 1, false, null, 2_000, false, RemoteEntrySamples.IssuerLabel);

        // 帳戶未登入：RejectSend 先送 sendRejected（帶本機的資料夾名）再 toast
        h.Ui.Invoke(() => h.Hub.HandleCommandAsRemoteForTest(context, RemoteEntrySamples.Cmd("send", upper, new { text = "從手機送的" }), output.Add));
        await h.Ui.DrainAsync();

        output.All.Select(m => m.Value<string>("type")).Should().Equal("sendRejected", "toast");
        var rejected = output.All[0];
        rejected.Value<string>("conversationId").Should().Be(wire, "伺服器以指令的線上 id 核對 sendRejected（對齊 A9）");
        rejected.Value<string>("text").Should().Be("從手機送的");
        rejected.Properties().Select(p => p.Name).Should().BeEquivalentTo(new[] { "type", "conversationId", "text" }, "遠端的形狀不變：原因只給本機的視窗（伺服器照輸出白名單轉送）");
        h.Main.Messages("sendRejected", mark).Should().BeEmpty("只回給發起的瀏覽器");
    }
}
