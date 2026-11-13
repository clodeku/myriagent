using System.Security.Cryptography;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 中樞閘門 ExecuteRemoteAsync（設計 8.11、14.11 D11；docs/sync-protocol.md 14.6；計畫步 9）：WP4.1 的預設授權者下型錄的每個指令都回 unsupported、
/// 什麼都不改；以測試授權者逐步驗 5a～5j 的拒絕碼與步驟、先失敗的先回報；回應檢視在 HandleCommand 的三處處理；遠端來源的佇列訊息送出前再查（D13）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteGateTests
{
    private const string CommandId = "01J9QZ3K8M5R7T2V4W6X8Y0A1B";

    private const string OtherDeviceId = "d_01J9QZ3K8M5R7T2V4W6X8Y0A1C";

    private const string QuestionId = "要怎麼彙總？";

    /// <summary>記錄閘門交給授權者的內容；回 <paramref name="result"/>（null＝允許）</summary>
    private sealed class RecordingAuthorizer(string? result = null) : IRemoteCommandAuthorizer
    {
        public Recorder<(string Cmd, RemoteTierFacts Facts, RemoteTierResult Tier, bool DefaultPassword, long? UvAt)> Calls { get; } = new();

        public string? Authorize(RemoteViewContext context, string cmd, JObject command, RemoteTierFacts facts, RemoteTierResult tier)
        {
            Calls.Add((cmd, facts, tier, context.DefaultPassword, context.UvAt));
            return result;
        }
    }

    /// <summary>測試用的處理者：<paramref name="body"/> 回 true＝接下</summary>
    private sealed class FuncHandler(Func<RemoteViewContext, IAgentView, string, JObject, bool> body) : IRemoteCommandHandler
    {
        public Recorder<(string Cmd, JObject Command, bool Authorized)> Calls { get; } = new();

        public bool TryHandle(RemoteViewContext context, IAgentView view, string cmd, JObject command)
        {
            Calls.Add((cmd, (JObject)command.DeepClone(), context.Authorized));
            return body(context, view, cmd, command);
        }
    }

    /// <summary>只記錄 HandleCommand 交給同步的指令</summary>
    private sealed class RecordingSyncBridge : ISyncBridge
    {
        public Recorder<(string ViewId, string Type)> Commands { get; } = new();

        public bool HandleCommand(IAgentView view, string type, JObject msg)
        {
            Commands.Add((view.Id, type));
            return true;
        }

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

    private sealed record Cards(string Id, FakeSession Session, AgentChatItem Approval, AgentChatItem Plan, AgentChatItem Question);

    // ───────────────────────────── 共用 ─────────────────────────────

    private static RemoteViewContext Context(string? conversationId, int tier = 0, bool fullAccess = false, long? epoch = 1, bool defaultPassword = false,
        long? uvAt = 1_000, string deviceId = RemoteSamples.DeviceId, string commandId = CommandId)
        => new(commandId, deviceId, conversationId, conversationId == null ? null : epoch, tier, fullAccess, uvAt, 2_000, defaultPassword, "Chrome・Windows");

    private static async Task<RemoteOutcome> ExecuteAsync(HubHarness h, RemoteViewContext context, JObject command, Recorder<JObject>? output = null)
    {
        var task = h.Ui.Invoke(() => h.Hub.ExecuteRemoteAsync(context, command, m => output?.Add(m)));
        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// 照 WP4.1 的語意跑閘門（WP4.4 計畫步 5、R8）：預設授權者換回 DenyAll、拿掉登記的處理者、預先放行一律不明（null）。
    /// 每個 HubHarness 建好後先呼叫，斷言不因 WP4.4 的授權者、處理者與偵測而改變；測試之後自己設的授權者與接縫照舊覆蓋
    /// </summary>
    private static void IsolateGate(HubHarness h)
        => h.Ui.Invoke(() =>
        {
            h.Hub.SetRemoteAuthorizerForTest(DenyAllRemoteAuthorizer.Instance);
            h.Hub.ClearRemoteHandlersForTest();
            h.Hub.ConversationPreApprovedForTest = _ => null;
            h.Hub.AccountPreApprovedForTest = (_, _) => null;
        });

    /// <summary>這台允許遠端操作（Control）；allowFullAccess＝也允許完全自動</summary>
    private static void AllowRemote(HubHarness h, bool allowFullAccess = true, string level = SyncSettings.LevelControl, bool paused = false)
        => h.Ui.Invoke(() =>
        {
            h.Settings.Sync.RemoteControlLevel = level;
            h.Settings.Sync.AllowRemoteFullAccess = allowFullAccess;
            h.Settings.Sync.RemotePaused = paused;
        });

    /// <summary>開一個執行中的 Claude 對話，畫出等待中的核准卡、計畫確認卡、問卷</summary>
    private static Cards StartWithCards(HubHarness h, bool canAllowForSession = true)
    {
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "npm test", "npm test", "t1", CanAllowForSession: canAllowForSession));
        h.Raise(s, new AgentApprovalEvent("plan-1", "確認執行計畫", "plan", "請確認", "1. 改 A", null, CanAllowForSession: false, IsPlan: true));
        h.Raise(s, new AgentQuestionEvent("q-1", new[] { new AgentQuestion { Id = QuestionId, Text = QuestionId } }, AnswerAsMessage: false));
        var items = h.Items(id);
        return new Cards(id, s, items.Single(i => i.Kind == AgentItemKind.Approval), items.Single(i => i.Kind == AgentItemKind.PlanApproval),
            items.Single(i => i.Kind == AgentItemKind.Question));
    }

    /// <summary>型錄裡這個指令的合格內容（對話、卡片、帳戶都對得上 <paramref name="cards"/>）</summary>
    private static JObject ValidCommand(RemoteCommandSpec spec, Cards cards)
    {
        var command = RemoteSamples.Required(spec);
        if (spec.Route == RemoteRoute.Owner)
            command["conversationId"] = ConversationIds.ToWire(cards.Id);
        if (command.ContainsKey("accountId"))
            command["accountId"] = "acc-claude";
        var card = spec.Name switch
        {
            "approve" => cards.Approval,
            "approvePlan" => cards.Plan,
            "answer" => cards.Question,
            _ => null,
        };
        if (card != null)
        {
            command["itemId"] = card.Id;
            command["requestDigest"] = RequestDigestRules.Compute(card);
        }

        if (spec.Name == "answer")
            command["answers"] = new JObject { [QuestionId] = new JArray("依日期") };
        return command;
    }

    private static RemoteViewContext ContextFor(RemoteCommandSpec spec, Cards cards)
        => Context(spec.Route == RemoteRoute.Owner ? ConversationIds.ToWire(cards.Id) : null, spec.BaseTier);

    private static JObject Cmd(string cmd, string? conversationId = null, object? args = null)
    {
        var command = HubHarness.MakeCommand(cmd, args);
        if (conversationId != null)
            command["conversationId"] = conversationId;
        return command;
    }

    /// <summary>暫存資料夾裡每個檔案的 SHA-256（記錄檔除外：背景執行緒寫、與閘門無關）</summary>
    private static Dictionary<string, string> Snapshot(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => Path.GetRelativePath(root, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))), StringComparer.OrdinalIgnoreCase);

    private static void ShouldBeRejected(RemoteOutcome outcome, string code, string step)
    {
        outcome.State.Should().Be(RemoteResultStates.Rejected, $"{code}／{step}：{outcome.Message}");
        outcome.Code.Should().Be(code, outcome.Message);
        outcome.Step.Should().Be(step, outcome.Message);
        outcome.Message.Should().NotBeNullOrWhiteSpace();
    }

    // ───────────────────────────── 預設授權者 ─────────────────────────────

    [Fact]
    public async Task 預設授權者_型錄每個指令都過了前面的檢查也回unsupported_檔案與狀態都不變()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var cards = StartWithCards(h);
        // 先跑一次（第一次取用對話預設值的遷移只改記憶體），再存檔，讓維護計時器沒有東西要存
        await ExecuteAsync(h, Context(ConversationIds.ToWire(cards.Id), 1), Cmd("rename", ConversationIds.ToWire(cards.Id), new { title = "x" }));
        h.Ui.Invoke(() =>
        {
            h.Settings.Save();
            h.Hub.RunMaintenance();
        });
        // 維護把執行中對話的 items.json 交給背景執行緒寫（ItemsFileWriter）：等它寫完再快照，不然會讀到寫到一半的 .tmp（被鎖）或前後不同的內容
        h.OnUi(() => h.Hub.ItemsWriterForTests)?.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();
        var before = Snapshot(h.Temp.Root);
        var title = h.Conversation(cards.Id)!.Title;
        var queue = h.Queue(cards.Id);
        var itemCount = h.Items(cards.Id).Count;
        var sent = cards.Session.Sent.Count;

        foreach (var spec in RemoteCommandCatalog.All)
        {
            var outcome = await ExecuteAsync(h, ContextFor(spec, cards), ValidCommand(spec, cards));
            outcome.Should().Be(outcome with { State = RemoteResultStates.Rejected, Code = RemoteResultCodes.Unsupported, Step = RemoteGateSteps.Authorize },
                $"{spec.Name} 在 WP4.4 之前一律不執行（{outcome.Message}）");
            outcome.Tier.Should().BeGreaterThanOrEqualTo(spec.BaseTier, spec.Name);
        }

        h.Conversation(cards.Id)!.Title.Should().Be(title);
        h.Queue(cards.Id).Should().Equal(queue);
        h.Items(cards.Id).Should().HaveCount(itemCount);
        h.Items(cards.Id).Where(i => i.Kind is AgentItemKind.Approval or AgentItemKind.PlanApproval or AgentItemKind.Question)
            .Should().OnlyContain(i => i.Status == AgentItemStatus.Waiting, "卡片都沒有被回應");
        cards.Session.Sent.Count.Should().Be(sent);
        cards.Session.Approvals.Count.Should().Be(0);
        cards.Session.PlanResponses.Count.Should().Be(0);
        cards.Session.Answers.Count.Should().Be(0);
        h.Accounts.Accounts.Select(a => a.Id).Should().Equal("acc-claude", "acc-claude2", "acc-codex");
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        h.OnUi(() => h.Hub.ItemsWriterForTests)?.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();
        Snapshot(h.Temp.Root).Should().Equal(before, "遠端指令沒有寫任何對話或設定檔");
    }

    [Fact]
    public async Task 照實際的建立順序_建構式不取帳戶_閘門之後才查()
    {
        using var h = new HubHarness(seedAccountsAfterHub: true);
        IsolateGate(h);
        AllowRemote(h);
        var create = RemoteSamples.Command(RemoteCommandCatalog.ConversationCreate, ("accountId", "acc-claude"), ("permissionMode", "Ask"));
        var authorizer = new RecordingAuthorizer();
        h.Ui.Invoke(() => h.Hub.SetRemoteAuthorizerForTest(authorizer));

        var outcome = await ExecuteAsync(h, Context(null, 1), create);

        outcome.State.Should().Be(RemoteResultStates.Rejected);
        outcome.Step.Should().Be(RemoteGateSteps.Handler, "授權者放行之後沒有處理者（conversationCreate 不走 HandleCommand）");
        authorizer.Calls.All.Single().Facts.TargetMode.Should().Be(AgentPermissionMode.Ask, "帳戶在中樞建立之後才載入，閘門照樣查得到");
    }

    // ───────────────────────────── 5a～5i ─────────────────────────────

    [Theory]
    [InlineData("openPath")]
    [InlineData("syncPause")]
    [InlineData("syncSomethingNew")]
    [InlineData("ready")]
    [InlineData("newConversation")]
    [InlineData("copyItem")]
    [InlineData("takeover")]
    [InlineData("handoffCancel")]
    [InlineData("Send")]
    [InlineData("nope")]
    public async Task 步驟5a_不是電腦執行的指令一律invalid(string cmd)
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        var authorizer = new RecordingAuthorizer();
        h.Ui.Invoke(() => h.Hub.SetRemoteAuthorizerForTest(authorizer));

        ShouldBeRejected(await ExecuteAsync(h, Context(id, 1), Cmd(cmd, id)), RemoteResultCodes.Invalid, RemoteGateSteps.Route);
        authorizer.Calls.Count.Should().Be(0);
    }

    [Fact]
    public async Task 步驟5a_cmd不是字串或沒有()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);

        ShouldBeRejected(await ExecuteAsync(h, Context(null), new JObject()), RemoteResultCodes.Invalid, RemoteGateSteps.Route);
        ShouldBeRejected(await ExecuteAsync(h, Context(null), new JObject { ["cmd"] = 3 }), RemoteResultCodes.Invalid, RemoteGateSteps.Route);
    }

    [Fact]
    public async Task 步驟5b_形狀不對一律invalid_比對話條件先回報()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);

        // 不認得的鍵、數值列舉、送進代理的文字太長
        ShouldBeRejected(await ExecuteAsync(h, Context(id, 1), Cmd("send", id, new { text = "x", extra = 1 })), RemoteResultCodes.Invalid, RemoteGateSteps.Shape);
        ShouldBeRejected(await ExecuteAsync(h, Context(id, 1), Cmd("setOption", id, new { key = "permission", value = "4" })), RemoteResultCodes.Invalid, RemoteGateSteps.Shape);
        ShouldBeRejected(await ExecuteAsync(h, Context(id, 1), Cmd("send", id, new { text = new string('x', RemoteCommandShapeRules.MaxAgentTextLength + 1) })),
            RemoteResultCodes.Invalid, RemoteGateSteps.Shape);
        // 對話也不存在：先回報形狀
        var missing = new string('e', 32);
        ShouldBeRejected(await ExecuteAsync(h, Context(missing, 1), Cmd("send", missing, new { text = "x", extra = 1 })), RemoteResultCodes.Invalid, RemoteGateSteps.Shape);
        // 線上的對話 id 只收小寫（D38）
        ShouldBeRejected(await ExecuteAsync(h, Context(id.ToUpperInvariant(), 1), Cmd("rename", id.ToUpperInvariant(), new { title = "x" })),
            RemoteResultCodes.Invalid, RemoteGateSteps.Shape);
    }

    [Fact]
    public async Task 步驟5c_對話條件不符是not_owner_比本機政策先回報()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        var draft = h.NewConversation(h.Main, h.Claude);
        var (other, _) = h.StartConversation(h.Main, h.Claude2);
        var missing = new string('e', 32);
        var rename = (string conversationId) => Cmd("rename", conversationId, new { title = "x" });

        ShouldBeRejected(await ExecuteAsync(h, Context(null), rename(id)), RemoteResultCodes.NotOwner, RemoteGateSteps.Conversation);
        ShouldBeRejected(await ExecuteAsync(h, Context(other, 1), rename(id)), RemoteResultCodes.NotOwner, RemoteGateSteps.Conversation);
        ShouldBeRejected(await ExecuteAsync(h, Context(missing, 1), rename(missing)), RemoteResultCodes.NotOwner, RemoteGateSteps.Conversation);
        ShouldBeRejected(await ExecuteAsync(h, Context(draft, 1), rename(draft)), RemoteResultCodes.NotOwner, RemoteGateSteps.Conversation);
        ShouldBeRejected(await ExecuteAsync(h, Context(id, 1, epoch: 2), rename(id)), RemoteResultCodes.NotOwner, RemoteGateSteps.Conversation);

        h.Ui.Invoke(() => h.Hub.GetConversation(other)!.OwnerDeviceId = OtherDeviceId);
        ShouldBeRejected(await ExecuteAsync(h, Context(other, 1), rename(other)), RemoteResultCodes.NotOwner, RemoteGateSteps.Conversation);
        h.Ui.Invoke(() =>
        {
            h.Hub.GetConversation(other)!.OwnerDeviceId = RemoteSamples.DeviceId;
            h.Hub.GetConversation(other)!.LeaseEpoch = 3;
        });
        ShouldBeRejected(await ExecuteAsync(h, Context(other, 1), rename(other)), RemoteResultCodes.NotOwner, RemoteGateSteps.Conversation);
        ShouldBeRejected(await ExecuteAsync(h, Context(other, 1, epoch: 3), rename(other)), RemoteResultCodes.Unsupported, RemoteGateSteps.Authorize);

        h.Ui.Invoke(() => h.Hub.MarkReplicaForTest(id));
        ShouldBeRejected(await ExecuteAsync(h, Context(id, 1), rename(id)), RemoteResultCodes.NotOwner, RemoteGateSteps.Conversation);

        // 路由 Device：不能帶對話、deviceId 有帶要是這台
        ShouldBeRejected(await ExecuteAsync(h, Context(other, 1, epoch: 3), Cmd("cliCheck")), RemoteResultCodes.NotOwner, RemoteGateSteps.Conversation);
        ShouldBeRejected(await ExecuteAsync(h, Context(null), Cmd("cliCheck", args: new { deviceId = OtherDeviceId })), RemoteResultCodes.NotOwner, RemoteGateSteps.Conversation);
        ShouldBeRejected(await ExecuteAsync(h, Context(null), Cmd("cliCheck", args: new { deviceId = RemoteSamples.DeviceId })), RemoteResultCodes.Unsupported, RemoteGateSteps.Authorize);

        // 本機政策也不允許（Off）：先回報對話條件
        AllowRemote(h, level: SyncSettings.LevelOff);
        ShouldBeRejected(await ExecuteAsync(h, Context(missing, 1), rename(missing)), RemoteResultCodes.NotOwner, RemoteGateSteps.Conversation);
    }

    [Fact]
    public async Task 步驟5c_同步政策不是full是policy_metadata與paused只收stop()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        var stop = Cmd("stop", id);
        var rename = Cmd("rename", id, new { title = "x" });

        foreach (var policy in new[] { ConversationSyncPolicies.Metadata, ConversationSyncPolicies.Paused })
        {
            h.Ui.Invoke(() => h.Hub.GetConversation(id)!.SyncPolicy = policy);
            ShouldBeRejected(await ExecuteAsync(h, Context(id, 1), rename), RemoteResultCodes.Policy, RemoteGateSteps.Conversation);
            ShouldBeRejected(await ExecuteAsync(h, Context(id, 0), stop), RemoteResultCodes.Unsupported, RemoteGateSteps.Authorize);
        }

        foreach (var policy in new[] { ConversationSyncPolicies.LocalOnly, "Full", "later" })
        {
            h.Ui.Invoke(() => h.Hub.GetConversation(id)!.SyncPolicy = policy);
            ShouldBeRejected(await ExecuteAsync(h, Context(id, 0), stop), RemoteResultCodes.Policy, RemoteGateSteps.Conversation);
        }

        h.Ui.Invoke(() => h.Hub.GetConversation(id)!.SyncPolicy = null);
        ShouldBeRejected(await ExecuteAsync(h, Context(id, 1), rename), RemoteResultCodes.Unsupported, RemoteGateSteps.Authorize);
    }

    [Fact]
    public async Task 步驟5c_對話資料夾名是大寫也找得到_處理者收到本機的id()
    {
        const string upper = "ABCDEF0123456789ABCDEF0123456789";
        using var h = new HubHarness(seedStore: store => store.SaveConversation(new AgentConversation
        {
            Id = upper, AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "大寫的資料夾",
        }));
        IsolateGate(h);
        AllowRemote(h);
        var handler = new FuncHandler((context, _, _, _) =>
        {
            context.Complete();
            return true;
        });
        h.Ui.Invoke(() =>
        {
            h.Hub.SetRemoteAuthorizerForTest(new RecordingAuthorizer());
            h.Hub.AddRemoteHandlerForTest(handler);
        });
        var wire = upper.ToLowerInvariant();

        var outcome = await ExecuteAsync(h, Context(wire, 1), Cmd("rename", wire, new { title = "x" }));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        handler.Calls.All.Single().Command.Value<string>("conversationId").Should().Be(upper);
        handler.Calls.All.Single().Authorized.Should().BeTrue();
    }

    [Fact]
    public async Task 步驟5d_層級取信封與這台較高者_事實不明時電腦取最高()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var cards = StartWithCards(h);
        var wire = ConversationIds.ToWire(cards.Id)!;
        var authorizer = new RecordingAuthorizer(RemoteResultCodes.Unsupported);
        h.Ui.Invoke(() => h.Hub.SetRemoteAuthorizerForTest(authorizer));

        // 伺服器說層級 0；電腦不知道有沒有預先放行規則（WP4.1 沒有偵測）→ 代理輸入是 2＋FullAccess
        await ExecuteAsync(h, Context(wire, 0), Cmd("send", wire, new { text = "x" }));
        // 伺服器說 2；rename 是 1 → 取 2
        await ExecuteAsync(h, Context(wire, 2), Cmd("rename", wire, new { title = "x" }));
        // 核准卡沒分類風險（null）→ 電腦當成非一般，allow 是 2
        await ExecuteAsync(h, Context(wire, 1), ValidCommand(RemoteCommandCatalog.Find("approve")!, cards));
        // 改成完全自動：TargetMode 照代理種類正規化之後的模式
        await ExecuteAsync(h, Context(wire, 0), Cmd("setOption", wire, new { key = "permission", value = "FullAccess" }));
        // 列檔：工作資料夾內是 1、範圍外是 2（工作資料夾直接設成固定的長路徑：CI 的 TEMP 是 8.3 短路徑，RemoteFolderRules 不收）
        h.Ui.Invoke(() => h.Hub.GetConversation(cards.Id)!.WorkingDirectory = @"C:\work\proj");
        await ExecuteAsync(h, Context(wire, 0), Cmd("listFiles", wire, new { requestId = "r1", path = @"C:\work\proj\src" }));
        await ExecuteAsync(h, Context(wire, 0), Cmd("listFiles", wire, new { requestId = "r2", path = @"C:\Windows" }));
        // 選資料夾：WP4.1 不查磁碟種類（FolderForbidden 不明）→ 2
        await ExecuteAsync(h, Context(wire, 0), Cmd("setFolder", wire, new { path = @"C:\repo" }));

        var calls = authorizer.Calls.All;
        calls.Select(c => (c.Cmd, c.Tier.Tier, c.Tier.FullAccess)).Should().Equal(
            ("send", 2, true), ("rename", 2, false), ("approve", 2, false), ("setOption", 2, true), ("listFiles", 1, false), ("listFiles", 2, false),
            ("setFolder", 2, false));
        calls[0].Facts.Mode.Should().Be(AgentPermissionMode.Ask);
        calls[0].Facts.PreApproved.Should().BeNull();
        calls[2].Facts.Risk.Should().BeNull();
        calls[3].Facts.TargetMode.Should().Be(AgentPermissionMode.FullAccess);
        calls[4].Facts.PathInScope.Should().BeTrue();
        calls[5].Facts.PathInScope.Should().BeFalse();
        calls[6].Facts.FolderKnown.Should().BeFalse();
        calls[6].Facts.FolderForbidden.Should().BeNull();
    }

    [Fact]
    public async Task 步驟5d_setOption的default照設定頁的預設()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        var authorizer = new RecordingAuthorizer(RemoteResultCodes.Unsupported);
        h.Ui.Invoke(() => h.Hub.SetRemoteAuthorizerForTest(authorizer));

        await ExecuteAsync(h, Context(id, 1), Cmd("setOption", id, new { key = "permission", value = "default" }));
        await ExecuteAsync(h, Context(id, 1), Cmd("setOption", id, new { key = "permission", value = "" }));
        await ExecuteAsync(h, Context(id, 1), Cmd("setOption", id, new { key = "model", value = "default" }));

        authorizer.Calls.All.Select(c => (c.Facts.TargetMode, c.Tier.Tier)).Should().Equal((AgentPermissionMode.Ask, 1), (AgentPermissionMode.Ask, 1), (null, 1));
    }

    [Theory]
    [InlineData(SyncSettings.LevelOff, true, false)]
    [InlineData(SyncSettings.LevelView, true, false)]
    [InlineData(SyncSettings.LevelControl, false, false)]
    [InlineData(SyncSettings.LevelControl, true, true)]
    [InlineData("control", true, false)]
    public async Task 步驟5e_本機政策不允許是policy(string level, bool allowFullAccess, bool paused)
    {
        using var h = new HubHarness();
        IsolateGate(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        AllowRemote(h, allowFullAccess, level, paused);

        // send 在電腦是 2＋FullAccess（預先放行規則不明）
        ShouldBeRejected(await ExecuteAsync(h, Context(id, 1), Cmd("send", id, new { text = "x" })), RemoteResultCodes.Policy, RemoteGateSteps.Policy);
    }

    [Fact]
    public async Task 步驟5e_政策比卡片先回報_Control不需要完全自動的照樣過()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        var cards = StartWithCards(h);
        var wire = ConversationIds.ToWire(cards.Id)!;
        var stale = ValidCommand(RemoteCommandCatalog.Find("approve")!, cards);
        stale["requestDigest"] = "rd1:" + new string('0', 64);

        ShouldBeRejected(await ExecuteAsync(h, Context(wire, 1), stale), RemoteResultCodes.Policy, RemoteGateSteps.Policy);

        AllowRemote(h, allowFullAccess: false);
        ShouldBeRejected(await ExecuteAsync(h, Context(wire, 1), stale), RemoteResultCodes.Stale, RemoteGateSteps.Card);
        ShouldBeRejected(await ExecuteAsync(h, Context(wire, 1), Cmd("rename", wire, new { title = "x" })), RemoteResultCodes.Unsupported, RemoteGateSteps.Authorize);
    }

    [Fact]
    public async Task 步驟5f與5g_時間窗是null_預設密碼不拒絕_旗標照樣交給授權者()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        var authorizer = new RecordingAuthorizer(RemoteResultCodes.Unsupported);
        h.Ui.Invoke(() => h.Hub.SetRemoteAuthorizerForTest(authorizer));

        // 從來沒輸入過密碼（uvAt null）、擁有者密碼還是預設值，層級 2 也照樣過（Q2、Q5-B）
        await ExecuteAsync(h, Context(id, 2, uvAt: null, defaultPassword: true), Cmd("delete", id));
        await ExecuteAsync(h, Context(id, 1, uvAt: 0, defaultPassword: false), Cmd("rename", id, new { title = "x" }));

        authorizer.Calls.All.Select(c => (c.Cmd, c.DefaultPassword, c.UvAt)).Should().Equal(("delete", true, (long?)null), ("rename", false, 0L));
        RemoteAuthRules.Tier1WindowMs.Should().BeNull();
        RemoteAuthRules.Tier2WindowMs.Should().BeNull();
    }

    [Fact]
    public async Task 步驟5h_卡片摘要不符或卡片已處理是stale_選項不允許是invalid()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var cards = StartWithCards(h, canAllowForSession: false);
        var wire = ConversationIds.ToWire(cards.Id)!;
        var approve = RemoteCommandCatalog.Find("approve")!;
        var answer = RemoteCommandCatalog.Find("answer")!;

        var wrongDigest = ValidCommand(approve, cards);
        wrongDigest["requestDigest"] = "rd1:" + new string('0', 64);
        ShouldBeRejected(await ExecuteAsync(h, Context(wire, 2), wrongDigest), RemoteResultCodes.Stale, RemoteGateSteps.Card);

        var notFound = ValidCommand(approve, cards);
        notFound["itemId"] = "missing";
        ShouldBeRejected(await ExecuteAsync(h, Context(wire, 2), notFound), RemoteResultCodes.Stale, RemoteGateSteps.Card);

        var wrongKind = ValidCommand(approve, cards);
        wrongKind["itemId"] = cards.Plan.Id;
        wrongKind["requestDigest"] = RequestDigestRules.Compute(cards.Plan);
        ShouldBeRejected(await ExecuteAsync(h, Context(wire, 2), wrongKind), RemoteResultCodes.Stale, RemoteGateSteps.Card);

        var allowSession = ValidCommand(approve, cards);
        allowSession["choice"] = "allowSession";
        ShouldBeRejected(await ExecuteAsync(h, Context(wire, 2), allowSession), RemoteResultCodes.Invalid, RemoteGateSteps.Card);

        var unknownAnswer = ValidCommand(answer, cards);
        unknownAnswer["answers"] = new JObject { ["別的題目"] = new JArray("x") };
        ShouldBeRejected(await ExecuteAsync(h, Context(wire, 2), unknownAnswer), RemoteResultCodes.Invalid, RemoteGateSteps.Card);

        ShouldBeRejected(await ExecuteAsync(h, Context(wire, 2), ValidCommand(approve, cards)), RemoteResultCodes.Unsupported, RemoteGateSteps.Authorize);

        // 本機已經回應了：卡片不在等待
        h.Command(h.Main, "approve", new { conversationId = cards.Id, itemId = cards.Approval.Id, choice = "deny" });
        ShouldBeRejected(await ExecuteAsync(h, Context(wire, 2), ValidCommand(approve, cards)), RemoteResultCodes.Stale, RemoteGateSteps.Card);
    }

    [Fact]
    public async Task 步驟5h_產生卡片的程序換了是stale()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.ProcessStarted(s, "proc-1", null);
        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "npm test", "npm test", "t1", CanAllowForSession: true));
        var card = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        var command = Cmd("approve", id, new { itemId = card.Id, choice = "deny", requestDigest = RequestDigestRules.Compute(card) });
        ShouldBeRejected(await ExecuteAsync(h, Context(id, 1), command), RemoteResultCodes.Unsupported, RemoteGateSteps.Authorize);

        // 新程序一啟動，舊程序的等待卡就作廢（OnProcessStarted）
        h.ProcessStarted(s, "proc-2", null);

        ShouldBeRejected(await ExecuteAsync(h, Context(id, 1), command), RemoteResultCodes.Stale, RemoteGateSteps.Card);
    }

    [Fact]
    public async Task 步驟5i_授權者的代碼原樣回報_不認得的代碼當成unsupported()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        var rename = Cmd("rename", id, new { title = "x" });

        h.Ui.Invoke(() => h.Hub.SetRemoteAuthorizerForTest(new RecordingAuthorizer(RemoteResultCodes.RateLimited)));
        ShouldBeRejected(await ExecuteAsync(h, Context(id, 1), rename), RemoteResultCodes.RateLimited, RemoteGateSteps.Authorize);

        h.Ui.Invoke(() => h.Hub.SetRemoteAuthorizerForTest(new RecordingAuthorizer("nope")));
        ShouldBeRejected(await ExecuteAsync(h, Context(id, 1), rename), RemoteResultCodes.Unsupported, RemoteGateSteps.Authorize);

        h.Ui.Invoke(() => h.Hub.SetRemoteAuthorizerForTest(DenyAllRemoteAuthorizer.Instance));
        ShouldBeRejected(await ExecuteAsync(h, Context(id, 1), rename), RemoteResultCodes.Unsupported, RemoteGateSteps.Authorize);
        h.Conversation(id)!.Title.Should().Be("第一則");
    }

    // ───────────────────────────── 5j ─────────────────────────────

    [Fact]
    public async Task 步驟5j_處理者接下_套過限制_輸出照白名單()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        var handler = new FuncHandler((context, view, cmd, _) =>
        {
            if (cmd == "accountRemove")
            {
                context.Complete();
                return true;
            }

            if (cmd != "send")
                return false;
            view.PostToWeb(new JObject { ["type"] = "toast", ["text"] = "排進佇列了" });
            view.PostToWeb(new JObject { ["type"] = "conversations", ["items"] = new JArray() });
            context.Complete(new JObject { ["type"] = "sendRejected", ["conversationId"] = id, ["text"] = "x" });
            return true;
        });
        h.Ui.Invoke(() =>
        {
            h.Hub.SetRemoteAuthorizerForTest(new RecordingAuthorizer());
            h.Hub.AddRemoteHandlerForTest(handler);
        });
        var output = new Recorder<JObject>();

        var outcome = await ExecuteAsync(h, Context(id, 2, fullAccess: true), Cmd("send", id, new { text = "x" }), output);

        outcome.Should().Be(new RemoteOutcome(RemoteResultStates.Done, null, null, 2, true, null));
        output.All.Select(m => m.Value<string>("type")).Should().Equal("toast", "sendRejected");
        handler.Calls.All.Single().Authorized.Should().BeTrue();

        var remove = await ExecuteAsync(h, Context(null, 2, commandId: "01J9QZ3K8M5R7T2V4W6X8Y0A1C"), Cmd("accountRemove", args: new { accountId = "acc-codex", deleteData = true }));
        remove.State.Should().Be(RemoteResultStates.Done);
        handler.Calls.All.Last().Command.Value<bool>("deleteData").Should().BeFalse("遠端移除帳戶一律保留資料夾（D15）");
        h.Accounts.Find("acc-codex").Should().NotBeNull("處理者沒有真的移除");
    }

    [Fact]
    public async Task 步驟5j_沒有完成信號_逾時是unknown()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        h.Ui.Invoke(() =>
        {
            h.Hub.RemoteCompletionTimeout = TimeSpan.FromMilliseconds(100);
            h.Hub.SetRemoteAuthorizerForTest(new RecordingAuthorizer());
            h.Hub.AddRemoteHandlerForTest(new FuncHandler((_, _, _, _) => true));
        });

        var outcome = await ExecuteAsync(h, Context(id, 1), Cmd("rename", id, new { title = "x" }));

        outcome.State.Should().Be(RemoteResultStates.Unknown);
        outcome.Code.Should().BeNull();
        outcome.Step.Should().Be(RemoteGateSteps.Handler);
    }

    [Fact]
    public async Task 步驟5j_處理者丟例外是unknown_可能已經執行()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        h.Ui.Invoke(() =>
        {
            h.Hub.SetRemoteAuthorizerForTest(new RecordingAuthorizer());
            h.Hub.AddRemoteHandlerForTest(new FuncHandler((_, _, _, _) => throw new InvalidOperationException("壞了")));
        });

        var outcome = await ExecuteAsync(h, Context(id, 1), Cmd("rename", id, new { title = "x" }));

        outcome.State.Should().Be(RemoteResultStates.Unknown);
        outcome.Step.Should().Be(RemoteGateSteps.Handler);
    }

    [Fact]
    public async Task 步驟5j_沒有處理者_退路指令走HandleCommand_其他是unsupported()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Ui.Invoke(() => h.Hub.SetRemoteAuthorizerForTest(new RecordingAuthorizer()));

        var rename = await ExecuteAsync(h, Context(id, 1), Cmd("rename", id, new { title = "遠端改的" }));
        rename.State.Should().Be(RemoteResultStates.Done, rename.Message);
        h.Conversation(id)!.Title.Should().Be("遠端改的");

        var sent = s.Sent.Count;
        ShouldBeRejected(await ExecuteAsync(h, Context(id, 2, fullAccess: true, commandId: "01J9QZ3K8M5R7T2V4W6X8Y0A1C"), Cmd("send", id, new { text = "x" })),
            RemoteResultCodes.Unsupported, RemoteGateSteps.Handler);
        s.Sent.Count.Should().Be(sent, "沒有處理者的代理輸入不走 HandleCommand（沒有 RemoteOrigin，D13）");
        ShouldBeRejected(await ExecuteAsync(h, Context(id, 2, commandId: "01J9QZ3K8M5R7T2V4W6X8Y0A1D"), Cmd("delete", id)), RemoteResultCodes.Unsupported, RemoteGateSteps.Handler);
        h.Conversation(id).Should().NotBeNull("遠端刪除要移到回收區（D10），本機的 Delete 不能當退路");
    }

    [Fact]
    public async Task 一個context只能經閘門一次()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        var context = Context(null);
        await ExecuteAsync(h, context, Cmd("cliCheck"));

        await FluentActions.Awaiting(() => h.Ui.Invoke(() => h.Hub.ExecuteRemoteAsync(context, Cmd("cliCheck"), _ => { })))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void 退路指令_都在型錄_不是卡片代理輸入或要套限制的()
    {
        string[] needHandler = ["send", "voiceMessage", "answer", "approve", "approvePlan", "delete", "setOption", "setAccount", "setFolder", "listFiles", "fileSearch", "fileFetch",
            "addUploaded", "accountAdd", "accountLogin", "accountLoginDevice", RemoteCommandCatalog.AccountLoginCode, "accountLogout", "cliInstall", "cliUpdate", RemoteCommandCatalog.ConversationCreate,
            RemoteCommandCatalog.RemoteExec, RemoteCommandCatalog.ScheduleAction, RemoteCommandCatalog.ScheduleAgent, RemoteCommandCatalog.TimedSave, RemoteCommandCatalog.TimedAction, RemoteCommandCatalog.ListFolders,
            RemoteCommandCatalog.DeviceBrowse, RemoteCommandCatalog.DeviceFetch, RemoteCommandCatalog.DevicePut, RemoteCommandCatalog.DeviceTree, RemoteCommandCatalog.DeviceFetchMany, RemoteCommandCatalog.DeskOpen];

        AgentHub.RemoteFallbackCommands.Should().OnlyContain(name => RemoteCommandCatalog.Find(name) != null);
        AgentHub.RemoteFallbackCommands.Select(n => RemoteCommandCatalog.Find(n)!)
            .Should().OnlyContain(spec => !spec.IsCard && (spec.Limits & ~RemoteLimits.KeepAccountData) == RemoteLimits.None);
        AgentHub.RemoteFallbackCommands.Concat(needHandler).Should().BeEquivalentTo(RemoteCommandCatalog.All.Select(s => s.Name),
            "型錄的每個指令不是走退路，就是要等 WP4.4／4.7／4.8 的處理者");
    }

    // ───────────────────────────── HandleCommand 對回應檢視的處理 ─────────────────────────────

    [Fact]
    public void 回應檢視_預設分支不轉給同步()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        var bridge = new RecordingSyncBridge();
        h.Ui.Invoke(() => h.Hub.AttachSync(bridge));

        h.Ui.Invoke(() => h.Hub.HandleCommandAsRemoteForTest(Context(null), Cmd("syncPause"), _ => { }));
        h.Ui.Invoke(() => h.Hub.HandleCommandAsRemoteForTest(Context(null, commandId: "01J9QZ3K8M5R7T2V4W6X8Y0A1C"), Cmd("deviceRename", args: new { name = "x" }), _ => { }));
        bridge.Commands.Count.Should().Be(0);

        h.Command(h.Main, "syncPause");
        bridge.Commands.All.Should().Equal(("main", "syncPause"));
    }

    [Fact]
    public async Task 回應檢視_例外不廣播toast_結果是unknown()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        var context = Context(null);
        var mark = h.Main.Mark();
        h.Host.CopyFailure = new IOException("剪貼簿被占用");

        h.Ui.Invoke(() => h.Hub.HandleCommandAsRemoteForTest(context, Cmd("copyText", args: new { text = "x" }), _ => { }));

        var outcome = await context.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        outcome.State.Should().Be(RemoteResultStates.Unknown);
        outcome.Message.Should().Contain("剪貼簿被占用");
        outcome.Step.Should().Be(RemoteGateSteps.Handler);
        await Task.Delay(100);
        h.Main.Messages("toast", mark).Should().BeEmpty("遠端指令的錯誤只回給發起的瀏覽器");

        // 本機的同一個指令照舊廣播
        h.Command(h.Main, "copyText", new { text = "x" });
        h.Main.Messages("toast", mark).Should().ContainSingle(m => m.Value<string>("text")!.Contains("剪貼簿被占用"));
    }

    [Fact]
    public void 回應檢視_只收給它的訊息_廣播不會到它()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        var output = new Recorder<JObject>();
        var mark = h.Main.Mark();

        // 執行中選了這一輪結束後才換帳戶：提示只給送指令的檢視（Toast(…, view)）
        h.Ui.Invoke(() => h.Hub.HandleCommandAsRemoteForTest(Context(id, 1), Cmd("setAccount", id, new { accountId = "acc-codex", when = "turnEnd" }), output.Add));
        output.All.Select(m => m.Value<string>("type")).Should().Equal(["toast"]);
        output.All[0].Value<string>("text").Should().Be("目前步驟完成後改用「Codex 帳戶」繼續");
        h.Main.Messages("toast", mark).Should().BeEmpty();

        // 執行中不能換資料夾：這個提示是廣播（Toast 沒有指定檢視），只到這台的視窗
        h.Ui.Invoke(() => h.Hub.HandleCommandAsRemoteForTest(Context(id, 1, commandId: "01J9QZ3K8M5R7T2V4W6X8Y0A1C"), Cmd("setFolder", id, new { path = @"C:\repo" }), output.Add));
        output.Count.Should().Be(1);
        h.Main.Messages("toast", mark).Should().ContainSingle();
    }

    // ───────────────────────────── 卡片摘要與風險（D36、D9） ─────────────────────────────

    [Fact]
    public void 卡片建立時填摘要_風險沒分類是null()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        var cards = StartWithCards(h);

        foreach (var card in new[] { cards.Approval, cards.Plan, cards.Question })
        {
            card.RequestDigest.Should().NotBeNull().And.StartWith("rd1:");
            card.RequestDigest.Should().Be(RequestDigestRules.Compute(card));
            card.Risk.Should().BeNull("WP4.4 的 ClassifyRemoteRisk 之前沒有分類");
        }
    }

    // ───────────────────────────── 遠端來源的佇列訊息（D13） ─────────────────────────────

    private static RemoteOrigin Origin(int tier, bool fullAccess, AgentPermissionMode mode = AgentPermissionMode.Ask) => new(CommandId, mode, tier, fullAccess);

    [Fact]
    public void 遠端來源的佇列訊息_政策不允許就留在佇列_本機看得到提示()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        h.Ui.Invoke(() => h.Hub.EnqueueRemoteForTest(id, "遠端的訊息", Origin(2, true)));
        var sent = s.Sent.Count;

        // 佇列暫停時的空白送出＝從佇列接著送：本機政策是 Off
        h.Send(h.Main, id, "");

        s.Sent.Count.Should().Be(sent);
        h.Queue(id).Should().Equal("遠端的訊息");
        h.IsRunning(id).Should().BeFalse();
        h.Items(id).Last().Kind.Should().Be(AgentItemKind.Notice);
        h.Items(id).Last().Text.Should().Contain("遠端");
    }

    [Fact]
    public void 遠端來源的佇列訊息_這一輪結束接著送時也查()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        // 入列時是層級 1（沒有完全自動），現在的對話因為預先放行規則不明而要 2＋FullAccess
        h.Ui.Invoke(() => h.Hub.EnqueueRemoteForTest(id, "遠端的訊息", Origin(1, false)));

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(1);
        h.Queue(id).Should().Equal("遠端的訊息");
    }

    [Fact]
    public void 遠端來源的佇列訊息_允許時送出_CLI收原文_畫面是顯示文字()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        h.Ui.Invoke(() => h.Hub.EnqueueRemoteForTest(id, "我的回答：\n密碼：hunter2", Origin(2, true), displayText: "我的回答：\n密碼：（已隱藏）"));

        h.Send(h.Main, id, "");

        s.Sent.All.Last().Input.Text.Should().Be("我的回答：\n密碼：hunter2");
        h.Items(id).Last(i => i.Kind == AgentItemKind.User).Text.Should().Be("我的回答：\n密碼：（已隱藏）");
        h.Queue(id).Should().BeEmpty();
    }

    [Fact]
    public void 遠端來源的佇列訊息_模式被調高就不送()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        h.Ui.Invoke(() => h.Hub.EnqueueRemoteForTest(id, "遠端的訊息", Origin(2, true)));
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "FullAccess" });

        h.Send(h.Main, id, "");

        s.Sent.Count.Should().Be(1);
        h.Queue(id).Should().Equal("遠端的訊息");
    }

    [Fact]
    public void 輸入區的佇列顯示遮蔽後的文字()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Ui.Invoke(() => h.Hub.EnqueueRemoteForTest(id, "我的回答：\n密碼：hunter2", Origin(2, true), displayText: "我的回答：\n密碼：（已隱藏）"));

        var composer = h.Main.Messages("composer", mark, id).Last();
        composer["composer"]!["queue"]!.Single()["text"]!.ToString().Should().Be("我的回答：\n密碼：（已隱藏）");
    }

    // ───────────────────────────── 合併前審查 ─────────────────────────────

    [Fact]
    public async Task 步驟5d_conversationCreate以帳戶層級偵測預先放行_沒有規則是層級1_預設政策也到得了授權者()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h, allowFullAccess: false);
        var authorizer = new RecordingAuthorizer(RemoteResultCodes.Unsupported);
        var asked = new List<(string AccountId, string? Folder)>();
        h.Ui.Invoke(() =>
        {
            h.Hub.SetRemoteAuthorizerForTest(authorizer);
            h.Hub.AccountPreApprovedForTest = (account, folder) =>
            {
                asked.Add((account.Id, folder));
                return false;
            };
        });
        var create = RemoteSamples.Command(RemoteCommandCatalog.ConversationCreate, ("accountId", "acc-claude"), ("permissionMode", "Ask"), ("text", "hi"));

        ShouldBeRejected(await ExecuteAsync(h, Context(null, 1), create), RemoteResultCodes.Unsupported, RemoteGateSteps.Authorize);

        var call = authorizer.Calls.All.Single();
        call.Facts.PreApproved.Should().BeFalse("新對話是那個帳戶的偵測結果");
        (call.Tier.Tier, call.Tier.FullAccess).Should().Be((1, false), "Ask 模式、沒有預先放行規則的帶文字建立是層級 1（Q3-A）");
        asked.Should().ContainSingle().Which.Should().Be(("acc-claude", (string?)null), "沒給 folder＝對話自己的新 workspace");
    }

    [Fact]
    public async Task 步驟5d_conversationCreate偵測不到預先放行規則_電腦當成有_預設政策在5e拒絕()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h, allowFullAccess: false);
        var authorizer = new RecordingAuthorizer();
        string? askedFolder = "沒問";
        h.Ui.Invoke(() =>
        {
            h.Hub.SetRemoteAuthorizerForTest(authorizer);
            h.Hub.AccountPreApprovedForTest = (_, folder) =>
            {
                askedFolder = folder;
                return null;
            };
        });
        var create = RemoteSamples.Command(RemoteCommandCatalog.ConversationCreate, ("accountId", "acc-claude"), ("permissionMode", "Ask"), ("text", "hi"), ("folder", @"C:\repo"));

        ShouldBeRejected(await ExecuteAsync(h, Context(null, 1), create), RemoteResultCodes.Policy, RemoteGateSteps.Policy);

        authorizer.Calls.Count.Should().Be(0);
        askedFolder.Should().Be(@"C:\repo", "有給資料夾時連同那個資料夾的專案設定一起看");
    }

    [Fact]
    public async Task 步驟5d_列檔的範圍是工作資料夾與attachments_對話資料夾裡的queue與items不在範圍內()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        var authorizer = new RecordingAuthorizer(RemoteResultCodes.Unsupported);
        h.Ui.Invoke(() => h.Hub.SetRemoteAuthorizerForTest(authorizer));
        var conversationDir = h.Store.GetConversationDirectory(id);
        var attachment = Path.Combine(h.Store.GetAttachmentDirectory(id), "chart.png");

        await ExecuteAsync(h, Context(id, 0), Cmd("listFiles", id, new { requestId = "r1", path = Path.Combine(conversationDir, "queue.json") }));
        await ExecuteAsync(h, Context(id, 0), Cmd("listFiles", id, new { requestId = "r2", path = Path.Combine(conversationDir, "items.json") }));
        await ExecuteAsync(h, Context(id, 0), Cmd("listFiles", id, new { requestId = "r3", path = attachment }));

        var calls = authorizer.Calls.All;
        calls[0].Facts.PathInScope.Should().BeFalse("queue.json 是 CLI 原文（含秘密回答），不在遠端的範圍");
        calls[1].Facts.PathInScope.Should().BeFalse();
        // 暫存資料夾是 8.3 短路徑時（CI 的 TEMP）規則不收任何路徑，範圍內的也是 false
        calls[2].Facts.PathInScope.Should().Be(RemoteFolderRules.TryNormalize(attachment, out _));
    }

    [Fact]
    public async Task 退路指令_skipQuestion只略過問卷_核准卡不受影響也不回應CLI()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Codex);
        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "npm test", "npm test", "t1", CanAllowForSession: true));
        var approval = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        h.Ui.Invoke(() => h.Hub.SetRemoteAuthorizerForTest(new RecordingAuthorizer()));

        await ExecuteAsync(h, Context(id, 1), Cmd("skipQuestion", id, new { itemId = approval.Id }));
        h.Command(h.Main, "skipQuestion", new { conversationId = id, itemId = approval.Id });
        await h.Ui.DrainAsync();

        var card = h.Items(id).Single(i => i.Id == approval.Id);
        card.Status.Should().Be(AgentItemStatus.Waiting, "skipQuestion 不帶摘要，核准卡要走 approve");
        card.Decision.Should().BeNull();
        s.Declined.Count.Should().Be(0);
        s.Approvals.Count.Should().Be(0);
        s.Answers.Count.Should().Be(0);
    }

    [Fact]
    public async Task 遠端來源的佇列訊息_送出不改這台的上次帳戶與最近資料夾()
    {
        using var h = new HubHarness();
        IsolateGate(h);
        AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        var picked = h.Temp.CreateDirectory("remote-picked");
        h.Ui.Invoke(() =>
        {
            h.Hub.GetConversation(id)!.WorkingDirectory = picked;
            h.Settings.LastAccountId = "acc-codex";
        });
        var recent = h.OnUi(() => h.Settings.RecentFolders.ToList());
        h.Ui.Invoke(() => h.Hub.EnqueueRemoteForTest(id, "遠端的訊息", Origin(2, true)));

        // 自訂的工作資料夾：送出前在背景確認資料夾存在，送出的是工作資料夾換過之後的程序
        h.Send(h.Main, id, "");
        await h.WaitUntilAsync(() => h.Sessions[^1].Sent.All.Any(x => x.Input.Text == "遠端的訊息"), "遠端的訊息在背景確認資料夾後送出");

        h.OnUi(() => h.Settings.LastAccountId).Should().Be("acc-codex", "遠端不寫這台的持久設定（D15）");
        h.OnUi(() => h.Settings.RecentFolders.ToList()).Should().Equal(recent, "遠端選的資料夾不因送出變成「已知」");

        // 對照：本機的送出照常更新
        h.CompleteTurn(h.Sessions[^1]);
        h.Send(h.Main, id, "本機的訊息");
        await h.WaitUntilAsync(() => h.Sessions[^1].Sent.All.Any(x => x.Input.Text == "本機的訊息"), "本機的訊息送出");
        h.OnUi(() => h.Settings.LastAccountId).Should().Be("acc-claude");
        h.OnUi(() => h.Settings.RecentFolders.ToList()).Should().Contain(picked);
    }
}
