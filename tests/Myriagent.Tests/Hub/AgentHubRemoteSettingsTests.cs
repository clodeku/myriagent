using System.Security.Cryptography;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 遠端的 setOption、setAccount、setFolder（WP4.4 計畫步 6；D17、D18、D23）：處理者先看授權；只改這個對話，不寫這台的持久設定
/// （帳戶預設模型、上次使用的帳戶、最近資料夾；settings.json 的雜湊不變）；資料夾在背景檢查磁碟種類與禁止清單；
/// 換到不進同步的代理種類回 policy；提示只回發起者。這台的政策是 Control、不允許遠端完全自動
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteSettingsTests
{
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// 存一次設定、記下雜湊（前提：同一份設定連續存兩次內容相同，計畫 R10），再在記憶體裡改一個與遠端無關的值、不存：
    /// 遠端操作要是存了設定，檔案就會帶著它（存了內容相同的設定也抓得到）
    /// </summary>
    private static (string Hash, int FontSize) SavedSettingsHash(HubHarness h)
    {
        h.Ui.Invoke(() => h.Settings.Save());
        var saved = Sha256(AppPaths.SettingsFile);
        h.Ui.Invoke(() => h.Settings.Save());
        Sha256(AppPaths.SettingsFile).Should().Be(saved, "前提：同一份設定連續存兩次內容相同");
        var fontSize = h.OnUi(() => h.Settings.FontSize);
        h.Ui.Invoke(() => h.Settings.FontSize = fontSize + 1);
        return (saved, fontSize);
    }

    /// <summary>遠端操作之後：沒有存設定，記憶體裡的設定也沒變（改回那個值再存一次，雜湊還是一樣）</summary>
    private static async Task SettingsShouldBeUnchanged(HubHarness h, (string Hash, int FontSize) saved)
    {
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.Ui.DrainAsync();
        Sha256(AppPaths.SettingsFile).Should().Be(saved.Hash, "遠端操作不存這台的設定（D23）");
        h.Ui.Invoke(() =>
        {
            h.Settings.FontSize = saved.FontSize;
            h.Settings.Save();
        });
        Sha256(AppPaths.SettingsFile).Should().Be(saved.Hash, "記憶體裡的設定也沒變（帳戶預設、上次使用的帳戶、最近資料夾）");
    }

    /// <summary>閒置的 Claude 對話（政策 Control、不允許遠端完全自動）</summary>
    private static (string Id, FakeSession Session) IdleConversation(HubHarness h)
    {
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        return (id, s);
    }

    private static Task<RemoteOutcome> RunAsync(HubHarness h, string cmd, string conversationId, object args, int tier = 1, Recorder<JObject>? output = null)
        => RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd(cmd, conversationId, args), tier), output);

    /// <summary>長檔名、正規化之後的資料夾（探針套用的形式）</summary>
    private static string Normalized(string path)
        => RemoteFolderRules.TryNormalize(WindowsRemoteFolderFileSystem.Instance.LongPath(path), out var normalized) ? normalized : throw new InvalidOperationException(path);

    [Fact]
    public async Task 處理者_沒授權就Reject_什麼都沒改()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        var (id, _) = IdleConversation(h);
        var before = h.Conversation(id)!;
        var (model, account, workingDirectory) = (before.Model, before.AccountId, before.WorkingDirectory);
        var handlers = h.OnUi(() => h.Hub.RemoteHandlersForTest);
        handlers.Should().NotBeEmpty("WP4.4 的對話處理者有登記");

        foreach (var command in new[]
                 {
                     RemoteEntrySamples.Cmd("setOption", id, new { key = "model", value = "遠端改的模型" }),
                     RemoteEntrySamples.Cmd("setAccount", id, new { accountId = "acc-claude2" }),
                     RemoteEntrySamples.Cmd("setFolder", id, new { path = folder.CreateDirectory("proj") }),
                 })
        {
            // 沒經過閘門：context 沒有 MarkAuthorized
            var context = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, id, 1, 1, false, null, 2_000, false, RemoteEntrySamples.IssuerLabel);
            var name = command.Value<string>("cmd")!;
            h.OnUi(() => handlers.Any(handler => handler.TryHandle(context, h.Main, name, command))).Should().BeTrue(name + " 是這一包的指令");
            var outcome = await context.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Invalid, RemoteGateSteps.Handler);
        }

        await h.Ui.DrainAsync();
        var after = h.Conversation(id)!;
        (after.Model, after.AccountId, after.WorkingDirectory).Should().Be((model, account, workingDirectory));
    }

    [Fact]
    public async Task 處理者_不是這一包的指令回false()
    {
        using var h = new HubHarness();
        var (id, _) = IdleConversation(h);
        var handlers = h.OnUi(() => h.Hub.RemoteHandlersForTest);
        var context = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, id, 1, 1, false, null, 2_000, false, RemoteEntrySamples.IssuerLabel);

        h.OnUi(() => handlers.Any(handler => handler.TryHandle(context, h.Main, "rename", RemoteEntrySamples.Cmd("rename", id, new { title = "x" })))).Should().BeFalse();
        context.IsCompleted.Should().BeFalse("沒接下就不回報");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task 遠端setOption_model只改對話_帳戶預設與settings雜湊不變()
    {
        using var h = new HubHarness();
        var (id, _) = IdleConversation(h);
        var defaults = h.OnUi(() => h.Settings.DefaultsFor("acc-claude").Model);
        var saved = SavedSettingsHash(h);

        var outcome = await RunAsync(h, "setOption", id, new { key = "model", value = "claude-remote-test" });

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        outcome.Tier.Should().Be(1);
        h.Conversation(id)!.Model.Should().Be("claude-remote-test");
        h.OnUi(() => h.Settings.DefaultsFor("acc-claude").Model).Should().Be(defaults, "遠端選的模型不記成帳戶的預設（ConversationOnly）");
        await SettingsShouldBeUnchanged(h, saved);
        h.OnUi(() => h.Store.LoadConversations().Single(m => m.Id == id).Model).Should().Be("claude-remote-test", "對話照樣存檔");
    }

    [Fact]
    public async Task 遠端setOption_effort只改對話_settings雜湊不變()
    {
        using var h = new HubHarness();
        var (id, _) = IdleConversation(h);
        var saved = SavedSettingsHash(h);

        var outcome = await RunAsync(h, "setOption", id, new { key = "effort", value = "high" });

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        h.Conversation(id)!.Effort.Should().Be("high");
        await SettingsShouldBeUnchanged(h, saved);
    }

    [Fact]
    public void 本機setOption_model照舊記成帳戶預設()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);

        h.Command(h.Main, "setOption", new { conversationId = id, key = "model", value = "claude-local-test" });

        h.Conversation(id)!.Model.Should().Be("claude-local-test");
        h.OnUi(() => h.Settings.DefaultsFor("acc-claude").Model).Should().Be("claude-local-test", "本機的行為不變");
    }

    [Fact]
    public async Task 遠端setOption_permission_完全自動_AllowRemoteFullAccess關閉時policy_打開時套用()
    {
        using var h = new HubHarness();
        var (id, _) = IdleConversation(h);
        h.Conversation(id)!.PermissionMode.Should().Be(AgentPermissionMode.Ask);

        var denied = await RunAsync(h, "setOption", id, new { key = "permission", value = "FullAccess" });

        RemoteEntrySamples.ShouldBeRejected(denied, RemoteResultCodes.Policy, RemoteGateSteps.Policy);
        denied.Tier.Should().Be(2);
        denied.FullAccess.Should().BeTrue();
        h.Conversation(id)!.PermissionMode.Should().Be(AgentPermissionMode.Ask);

        RemoteEntrySamples.AllowRemote(h, allowFullAccess: true);
        var allowed = await RunAsync(h, "setOption", id, new { key = "permission", value = "FullAccess" });

        allowed.State.Should().Be(RemoteResultStates.Done, allowed.Message);
        h.Conversation(id)!.PermissionMode.Should().Be(AgentPermissionMode.FullAccess);
    }

    [Fact]
    public async Task 遠端setAccount_不寫LastAccountId_執行中換到Grok也可以()
    {
        using var h = new HubHarness();
        var grok = new AgentAccount { Id = "acc-grok", Provider = AgentProvider.Grok, Label = "Grok 帳戶", Color = "#9CA3AF", ConfigDirectory = h.Temp.Combine("accounts", "acc-grok", "grok") };
        h.Ui.Invoke(() => h.Accounts.Seed(new[] { h.Claude, h.Claude2, h.Codex, grok }, AccountStatus.Ready));
        var (id, s) = IdleConversation(h);
        h.OnUi(() => h.Settings.LastAccountId).Should().Be("acc-claude");
        var saved = SavedSettingsHash(h);

        var outcome = await RunAsync(h, "setAccount", id, new { accountId = "acc-claude2" }, tier: 2);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        h.Conversation(id)!.AccountId.Should().Be("acc-claude2");
        h.OnUi(() => h.Settings.LastAccountId).Should().Be("acc-claude", "遠端換帳戶不改「上次使用的帳戶」（D23）");
        await SettingsShouldBeUnchanged(h, saved);

        // 同一個帳戶：直接 done
        (await RunAsync(h, "setAccount", id, new { accountId = "acc-claude2" }, tier: 2)).State.Should().Be(RemoteResultStates.Done);

        // 執行中換到 Grok 也可以（2026-10-24 起四種互換，見 AgentHubRunningAccountSwitchTests）：done，先停下這一輪，結束時換過去
        h.Send(h.Main, id, "第二則");
        h.IsRunning(id).Should().BeTrue();
        var running = await RunAsync(h, "setAccount", id, new { accountId = "acc-grok" }, tier: 2);

        running.State.Should().Be(RemoteResultStates.Done, running.Message);
        var s2 = h.Sessions[^1];
        s2.Interrupts.Should().Be(1);
        h.Conversation(id)!.AccountId.Should().Be("acc-claude2", "這一輪結束之前還沒換");
        h.CompleteTurn(s2, AgentTurnOutcome.Interrupted);
        (h.Conversation(id)!.AccountId, h.Conversation(id)!.Provider).Should().Be(("acc-grok", AgentProvider.Grok));
        h.OnUi(() => h.Settings.LastAccountId).Should().NotBe("acc-grok", "遠端換帳戶（含接續的送出）不改「上次使用的帳戶」（D23；中間那則是本機送的）");
        _ = s;
    }

    [Fact]
    public async Task 遠端setAccount_換到另一家_那一家的預設是完全自動_AllowRemoteFullAccess關閉時policy_打開時done且紀錄fullAccess()
    {
        using var h = new HubHarness();
        var (id, _) = IdleConversation(h);
        h.Command(h.Main, "setConversationDefault", new { provider = "Codex", key = "permission", value = "FullAccess" });
        var before = h.Conversation(id)!;
        (before.AccountId, before.Provider, before.PermissionMode).Should().Be(("acc-claude", AgentProvider.Claude, AgentPermissionMode.Ask));

        var denied = await RunAsync(h, "setAccount", id, new { accountId = "acc-codex" }, tier: 2);

        // 換到另一家時模式改成那一家設定頁的預設（SetAccount）：換成完全自動等於遠端改完全自動，要「允許遠端完全自動」（合併前審查 GATE-1）
        RemoteEntrySamples.ShouldBeRejected(denied, RemoteResultCodes.Policy, RemoteGateSteps.Policy);
        (denied.Tier, denied.FullAccess).Should().Be((2, true));
        var after = h.Conversation(id)!;
        (after.AccountId, after.Provider, after.PermissionMode).Should().Be(("acc-claude", AgentProvider.Claude, AgentPermissionMode.Ask), "被拒時什麼都不改");

        RemoteEntrySamples.AllowRemote(h, allowFullAccess: true);
        var allowed = await RunAsync(h, "setAccount", id, new { accountId = "acc-codex" }, tier: 2);

        allowed.State.Should().Be(RemoteResultStates.Done, allowed.Message);
        allowed.FullAccess.Should().BeTrue("遠端操作紀錄記的是換過去的模式要的 fullAccess");
        var switched = h.Conversation(id)!;
        (switched.AccountId, switched.Provider, switched.PermissionMode).Should().Be(("acc-codex", AgentProvider.Codex, AgentPermissionMode.FullAccess));
    }

    [Fact]
    public async Task 遠端setAccount_模式不變就不要求允許遠端完全自動_同一家沿用_另一家的預設是每步詢問()
    {
        using var h = new HubHarness();
        var (id, _) = IdleConversation(h);
        h.Command(h.Main, "setConversationDefault", new { provider = "Codex", key = "permission", value = "Ask" });
        // 本機把這個對話改成完全自動（不是遠端改的）
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "FullAccess" });
        h.Conversation(id)!.PermissionMode.Should().Be(AgentPermissionMode.FullAccess);

        var sameProvider = await RunAsync(h, "setAccount", id, new { accountId = "acc-claude2" }, tier: 2);

        sameProvider.State.Should().Be(RemoteResultStates.Done, sameProvider.Message);
        (sameProvider.Tier, sameProvider.FullAccess).Should().Be((2, false), "同一家沿用原本的模式：沒有改成完全自動");
        (h.Conversation(id)!.AccountId, h.Conversation(id)!.PermissionMode).Should().Be(("acc-claude2", AgentPermissionMode.FullAccess));

        var otherProvider = await RunAsync(h, "setAccount", id, new { accountId = "acc-codex" }, tier: 2);

        otherProvider.State.Should().Be(RemoteResultStates.Done, otherProvider.Message);
        (otherProvider.Tier, otherProvider.FullAccess).Should().Be((2, false), "換成每步詢問：層級照 setAccount 的 2");
        (h.Conversation(id)!.AccountId, h.Conversation(id)!.PermissionMode).Should().Be(("acc-codex", AgentPermissionMode.Ask));
    }

    [Fact]
    public void 本機setAccount_照舊記成上次使用的帳戶()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);

        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = "acc-codex" });

        h.Conversation(id)!.AccountId.Should().Be("acc-codex");
        h.OnUi(() => h.Settings.LastAccountId).Should().Be("acc-codex", "本機的行為不變");
    }

    [Theory]
    [InlineData("Antigravity")]
    [InlineData("Grok")]
    public async Task 遠端setAccount_換到Antigravity與Grok_照常套用(string providerName)
    {
        var provider = Enum.Parse<AgentProvider>(providerName);
        using var h = new HubHarness();
        var (id, _) = IdleConversation(h);
        var account = provider switch
        {
            AgentProvider.Antigravity => new AgentAccount { Id = "local-antigravity", Provider = provider, Label = "Antigravity", Color = "#4285F4" },
            AgentProvider.Grok => new AgentAccount { Id = "acc-grok", Provider = provider, Label = "Grok", Color = "#111111", ConfigDirectory = h.Temp.Combine("accounts", "acc-grok", "grok") },
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
        h.Ui.Invoke(() => h.Accounts.Seed([account], AccountStatus.Ready));

        var outcome = await RunAsync(h, "setAccount", id, new { accountId = account.Id }, tier: 2);

        // 2026-10-22 起四種都同步：換過去對話照樣同步（沒有 prov1 時由同步引擎延後），不再以 policy 擋
        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var after = h.Conversation(id)!;
        after.AccountId.Should().Be(account.Id);
        after.Provider.Should().Be(provider);
    }

    [Theory]
    [InlineData("NotLoggedIn", false)]
    [InlineData("NotInstalled", false)]
    [InlineData("NotLoggedIn", true)]
    public async Task 遠端setAccount_只能換成這台已登入的帳戶_沒登入或沒安裝回conflict_什麼都不改(string statusName, bool running)
    {
        var status = Enum.Parse<AccountStatus>(statusName);
        using var h = new HubHarness();
        var (id, _) = IdleConversation(h);
        var other = new AgentAccount
        {
            Id = "acc-claude3", Provider = AgentProvider.Claude, Label = "第三個 Claude", Color = "#123456",
            ConfigDirectory = h.Temp.Combine("accounts", "acc-claude3", "claude"),
        };
        h.Ui.Invoke(() => h.Accounts.Seed([other], status));
        if (running)
            h.Send(h.Main, id, "第二則");
        var interrupts = h.Sessions[^1].Interrupts;

        var outcome = await RunAsync(h, "setAccount", id, new { accountId = other.Id }, tier: 2);

        // 2026-10-25 使用者要求：其他電腦只能把對話換成這台登入的帳戶（網頁只列那些；畫面可能是舊的，這台再擋一次）
        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        outcome.Message.Should().Contain("第三個 Claude");
        h.Conversation(id)!.AccountId.Should().Be("acc-claude", "被拒時不換");
        h.Sessions[^1].Interrupts.Should().Be(interrupts, "執行中的這一輪也不停下");
    }

    [Fact]
    public async Task 遠端setFolder_存在才套用_不進最近資料夾_settings雜湊不變()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        var (id, _) = IdleConversation(h);
        var proj = folder.CreateDirectory("proj");
        var recent = h.OnUi(() => h.Settings.RecentFolders.ToList());
        var saved = SavedSettingsHash(h);

        var outcome = await RunAsync(h, "setFolder", id, new { path = proj });

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        h.Conversation(id)!.WorkingDirectory.Should().Be(Normalized(proj));
        h.OnUi(() => h.Settings.RecentFolders.ToList()).Should().Equal(recent, "遠端選的資料夾不進最近資料夾（NoRememberFolder）");
        await SettingsShouldBeUnchanged(h, saved);
        h.OnUi(() => h.Store.LoadConversations().Single(m => m.Id == id).WorkingDirectory).Should().Be(Normalized(proj), "對話照樣存檔");
    }

    [Fact]
    public async Task 遠端setFolder_不存在_conflict_原資料夾不變()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        var (id, _) = IdleConversation(h);
        var proj = folder.CreateDirectory("proj");
        (await RunAsync(h, "setFolder", id, new { path = proj })).State.Should().Be(RemoteResultStates.Done);

        var outcome = await RunAsync(h, "setFolder", id, new { path = folder.Combine("missing") });

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        outcome.Message.Should().Contain("找不到資料夾");
        h.Conversation(id)!.WorkingDirectory.Should().Be(Normalized(proj));
    }

    [Theory]
    [InlineData(@"\\srv\share\proj")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\COM1")]
    [InlineData(@"relative\proj")]
    [InlineData(@"C:proj")]
    public async Task 遠端setFolder_UNC或裝置路徑或相對路徑_invalid(string path)
    {
        using var h = new HubHarness();
        var (id, _) = IdleConversation(h);

        var outcome = await RunAsync(h, "setFolder", id, new { path });

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Invalid, RemoteGateSteps.Handler);
        h.Conversation(id)!.WorkingDirectory.Should().BeNull();
    }

    [Fact]
    public async Task 遠端setFolder_禁止的資料夾_policy()
    {
        using var h = new HubHarness();
        var (id, _) = IdleConversation(h);
        Directory.CreateDirectory(h.Claude2.ConfigDirectory!);

        foreach (var forbidden in new[] { h.Temp.Root, h.Temp.CreateDirectory("logs"), h.Claude2.ConfigDirectory!, h.Store.GetConversationDirectory(id) })
        {
            var outcome = await RunAsync(h, "setFolder", id, new { path = forbidden });
            RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Policy, RemoteGateSteps.Handler);
        }

        h.Conversation(id)!.WorkingDirectory.Should().BeNull();

        // 對照：這個對話自己的 workspace 在資料根目錄裡，但可以給遠端
        var workspace = h.Store.GetDefaultWorkingDirectory(id);
        Directory.CreateDirectory(workspace);
        (await RunAsync(h, "setFolder", id, new { path = workspace })).State.Should().Be(RemoteResultStates.Done);
    }

    [FileSystemFact(FileSystemFeature.Junction)]
    public async Task 遠端setFolder_workspace裡指到資料根目錄的junction_policy_和直接選資料根目錄是同一個拒絕()
    {
        // 代理在自己的 workspace 裡不用管理員權限就能建 junction：字串上在允許的子樹裡，實際是整個資料根目錄（帳戶、同步、別的對話）
        using var h = new HubHarness();
        using var junctions = new TestJunctions();
        var (id, _) = IdleConversation(h);
        Directory.CreateDirectory(h.Claude2.ConfigDirectory!);
        var workspace = Directory.CreateDirectory(h.Store.GetDefaultWorkingDirectory(id)).FullName;
        var link = junctions.Create(Path.Combine(workspace, "link-data"), h.Temp.Root);
        var direct = await RunAsync(h, "setFolder", id, new { path = h.Temp.Root });
        RemoteEntrySamples.ShouldBeRejected(direct, RemoteResultCodes.Policy, RemoteGateSteps.Handler);

        foreach (var path in new[] { link, Path.Combine(link, "accounts"), Path.Combine(link, "accounts", "acc-claude2", "claude"), Path.Combine(link, "conversations", id) })
        {
            var outcome = await RunAsync(h, "setFolder", id, new { path });
            RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Policy, RemoteGateSteps.Handler);
            (outcome.State, outcome.Code, outcome.Step, outcome.Tier).Should().Be((direct.State, direct.Code, direct.Step, direct.Tier),
                path + "：遠端操作紀錄記下的結果和直接選資料根目錄一樣");
        }

        h.Conversation(id)!.WorkingDirectory.Should().BeNull();
    }

    [FileSystemFact(FileSystemFeature.Junction)]
    public async Task 遠端setFolder_範圍外的junction指到帳戶設定或對話清單_policy_指到一般資料夾的套用真實路徑()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        using var junctions = new TestJunctions();
        var (id, _) = IdleConversation(h);
        Directory.CreateDirectory(h.Claude2.ConfigDirectory!);

        foreach (var target in new[] { h.Claude2.ConfigDirectory!, h.Store.RootDirectory })
        {
            var link = junctions.Create(folder.Combine("link-" + Path.GetFileName(target)), target);
            RemoteEntrySamples.ShouldBeRejected(await RunAsync(h, "setFolder", id, new { path = link }), RemoteResultCodes.Policy, RemoteGateSteps.Handler);
        }

        h.Conversation(id)!.WorkingDirectory.Should().BeNull();

        // 對照：指到一般的資料夾照樣可以，套用的是實際的資料夾（之後 junction 改指向也影響不到這個對話）
        var proj = folder.CreateDirectory(@"real\proj");
        var viaLink = Path.Combine(junctions.Create(folder.Combine("link-real"), folder.Combine("real")), "proj");
        var done = await RunAsync(h, "setFolder", id, new { path = viaLink });
        done.State.Should().Be(RemoteResultStates.Done, done.Message);
        h.Conversation(id)!.WorkingDirectory.Should().Be(Normalized(proj));

        // workspace 裡指到 workspace 裡的別處也可以
        var workspace = Directory.CreateDirectory(h.Store.GetDefaultWorkingDirectory(id)).FullName;
        var real = Directory.CreateDirectory(Path.Combine(workspace, "real")).FullName;
        var inWorkspace = await RunAsync(h, "setFolder", id, new { path = junctions.Create(Path.Combine(workspace, "link-in"), real) });
        inWorkspace.State.Should().Be(RemoteResultStates.Done, inWorkspace.Message);
        h.Conversation(id)!.WorkingDirectory.Should().Be(Normalized(real));
    }

    [Fact]
    public async Task 遠端setFolder_執行中_conflict()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        RemoteEntrySamples.AllowRemote(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        h.IsRunning(id).Should().BeTrue();

        var outcome = await RunAsync(h, "setFolder", id, new { path = folder.CreateDirectory("proj") });

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        h.Conversation(id)!.WorkingDirectory.Should().BeNull();
    }

    /// <summary>磁碟種類回指定的值、或卡住（測試放開之前）</summary>
    private sealed class FakeFolderFileSystem(RemoteDriveKind kind, ManualResetEventSlim? hang = null) : IRemoteFolderFileSystem
    {
        public RemoteDriveKind DriveKind(string fullPath)
        {
            hang?.Wait(TimeSpan.FromSeconds(30));
            return kind;
        }

        public string? LongPath(string fullPath) => WindowsRemoteFolderFileSystem.Instance.LongPath(fullPath);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public string? FinalPath(string normalizedPath) => WindowsRemoteFolderFileSystem.Instance.FinalPath(normalizedPath);
    }

    [Fact]
    public async Task 遠端setFolder_網路磁碟policy_逾時conflict()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        var (id, _) = IdleConversation(h);
        var proj = folder.CreateDirectory("proj");

        h.Ui.Invoke(() => h.Hub.RemoteFolderFileSystem = new FakeFolderFileSystem(RemoteDriveKind.Network));
        RemoteEntrySamples.ShouldBeRejected(await RunAsync(h, "setFolder", id, new { path = proj }), RemoteResultCodes.Policy, RemoteGateSteps.Handler);

        using var hang = new ManualResetEventSlim();
        try
        {
            h.Ui.Invoke(() =>
            {
                h.Hub.RemoteFolderFileSystem = new FakeFolderFileSystem(RemoteDriveKind.Fixed, hang);
                h.Hub.RemoteFolderTimeout = TimeSpan.FromMilliseconds(100);
            });
            var timedOut = await RunAsync(h, "setFolder", id, new { path = proj });
            RemoteEntrySamples.ShouldBeRejected(timedOut, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
            timedOut.Message.Should().Contain("逾時");
        }
        finally
        {
            hang.Set();
        }

        h.Conversation(id)!.WorkingDirectory.Should().BeNull();
    }

    [Fact]
    public async Task 遠端setFolder_檢查期間對話被刪_conflict()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        var (id, _) = IdleConversation(h);
        using var hang = new ManualResetEventSlim();
        h.Ui.Invoke(() => h.Hub.RemoteFolderFileSystem = new FakeFolderFileSystem(RemoteDriveKind.Fixed, hang));

        var pending = RunAsync(h, "setFolder", id, new { path = folder.CreateDirectory("proj") });
        h.Command(h.Main, "delete", new { conversationId = id });
        hang.Set();
        var outcome = await pending;

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        h.Conversation(id).Should().BeNull();
    }

    [Fact]
    public async Task 遠端的提示只回發起者_本機視窗收不到()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        s.CanSwitchModeLive = false;
        var output = new Recorder<JObject>();
        var mark = h.Main.Mark();

        // 執行中換權限模式、這一輪套用不了：提示「下一則訊息才生效」給送指令的檢視
        var outcome = await RunAsync(h, "setOption", id, new { key = "permission", value = "Plan" }, output: output);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        h.Conversation(id)!.PermissionMode.Should().Be(AgentPermissionMode.Plan);
        output.All.Select(m => m.Value<string>("type")).Should().Equal("toast");
        output.All[0].Value<string>("text").Should().Contain("下一則訊息");
        await h.Ui.DrainAsync();
        h.Main.Messages("toast", mark).Should().BeEmpty("本機視窗收不到遠端指令的提示");
    }

    [Fact]
    public async Task 遠端在執行中換模型_套用到這一輪_做不到時提示只回發起者()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var (claude, sc) = h.StartConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        // Claude：執行中立即套用（瀏覽器版、其他電腦都走這裡），沒有提示
        var output = new Recorder<JObject>();
        var outcome = await RunAsync(h, "setOption", claude, new { key = "model", value = "sonnet" }, output: output);
        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        sc.ModelChanges.All.Select(x => x.Model).Should().Equal("sonnet");
        await h.Ui.DrainAsync();
        output.All.Should().BeEmpty();

        // Codex：一輪中途換不了，提示「下一則訊息才生效」給送指令的檢視
        var (codex, sx) = h.StartConversation(h.Main, h.Codex);
        output = new Recorder<JObject>();
        outcome = await RunAsync(h, "setOption", codex, new { key = "model", value = "gpt-x" }, output: output);
        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        sx.ModelChanges.All.Select(x => x.Model).Should().Equal("gpt-x");
        h.Conversation(codex)!.Model.Should().Be("gpt-x");
        await h.Ui.DrainAsync();
        output.All.Select(m => m.Value<string>("type")).Should().Equal("toast");
        output.All[0].Value<string>("text").Should().Contain("模型從下一則訊息開始生效");
        h.Main.Messages("toast", mark).Should().BeEmpty("本機視窗收不到遠端指令的提示");
    }
}
