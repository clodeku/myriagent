using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// Claude CLI 2.1.332 的整合（2026-11-25）：代理主動通知使用者（AgentHub.NotifyUser.cs）、安全機制換模型（AgentHub.ModelRefusal.cs）、
/// 沒人看的對話（AgentHub.StreamWatch.cs）、插入的訊息被讀到（AgentHub.SteerCancel.cs）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubClaudeSignalsTests
{
    private const string ReplicaId = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8";

    private static string TitleOf(HubHarness h, string id) => h.Conversation(id)!.Title;

    private static Task<JObject> HeaderAsync(HubHarness h, string id, Func<JObject, bool> pred, int mark)
        => h.Main.WaitForAsync("header", m => m["header"] is JObject hd && pred(hd), mark, conversationId: id);

    private sealed class AttentionSink : IConversationChangeSink
    {
        public Recorder<(string? Kind, string? ItemId, VoiceLine Speech)> Attentions { get; } = new();

        public void OnItemChanged(ConversationHookInfo conversation, AgentChatItem item)
        {
        }

        public void OnItemRemoved(ConversationHookInfo conversation, AgentChatItem item)
        {
        }

        public void OnConversationSaved(ConversationHookInfo conversation, bool itemsSaved)
        {
        }

        public void OnConversationDeleting(ConversationHookInfo conversation)
        {
        }

        public void OnHeaderChanged(ConversationHookInfo conversation)
        {
        }

        public void OnComposerChanged(ConversationHookInfo conversation)
        {
        }

        public void OnQueueChanged(ConversationHookInfo conversation)
        {
        }

        public void OnRunningCountsChanged()
        {
        }

        public void OnTurnStarted(ConversationHookInfo conversation, bool startedByCli)
        {
        }

        public void OnTurnCompleted(ConversationHookInfo conversation, AgentTurnOutcome outcome, AgentUsage? usage)
        {
        }

        public void OnAttention(ConversationHookInfo conversation, string? kind, string? itemId, VoiceLine speech) => Attentions.Add((kind, itemId, speech));
    }

    // ───── 代理主動通知使用者 ─────

    [Fact]
    public void 代理通知使用者_念出來_沒看到就跳系統通知_同步出去標成通知()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var sink = new AttentionSink();
        h.Ui.Invoke(() => h.Hub.AttachConversationSink(sink));
        h.Host.IsForeground = false;
        var mark = h.Main.Mark();

        h.Raise(s, new AgentNotifyUserEvent("網站跳出人工驗證：請到店 1 的 Chrome 完成"));

        var full = $"「{TitleOf(h, id)}」通知你：網站跳出人工驗證：請到店 1 的 Chrome 完成";
        h.Host.Spoken.All.Should().Equal(full);
        h.Host.Notifications.All.Should().ContainSingle().Which.Should().Match<(string Title, string Message, string? ConversationId)>(
            n => n.Title.StartsWith("代理通知你") && n.Message.Contains("人工驗證") && n.ConversationId == id);
        h.Main.Messages("flash", mark, id).Should().ContainSingle("這個對話開始閃");
        sink.Attentions.All.Should().Equal((ConversationAttentionKinds.Question, null, new VoiceLine(full, $"「{TitleOf(h, id)}」有事通知你") { Notice = true }));
    }

    [Fact]
    public void 代理通知使用者_正看著那個對話_照樣念與閃_不跳系統通知()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });

        h.Raise(s, new AgentNotifyUserEvent("做完了"));

        h.Host.Spoken.Count.Should().Be(1);
        h.Host.Notifications.Count.Should().Be(0);
    }

    private static ReplicaDelta Replica(params ReplicaNotice[] notices)
    {
        var json = ConversationWire.Serialize(new ConversationMetaPayload("Claude", "acc-owner", "擁有者的對話", "Ask", null, null, false, null, ConversationSyncPolicies.Full,
            1791500000000, 1791500060000, false, false));
        return new ReplicaDelta([ReplicaRules.ToMeta(new ReplicaMetaRow(ReplicaId, "d_01J9Q0000000000000000000OW", 3, json, false, 7))!], [], [], Full: true, Notices: notices);
    }

    [Fact]
    public void 其他電腦的代理通知_這台跳系統通知與閃_不念_太舊的不跳()
    {
        using var h = new HubHarness();
        h.Host.IsForeground = false;
        h.Ui.Invoke(() => h.Hub.ShowReplicas = true);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var mark = h.Main.Mark();

        h.Ui.Invoke(() => h.Hub.ApplyReplicaDelta(Replica(new ReplicaNotice(ReplicaId, "「擁有者的對話」通知你：要驗證", now - 1000))));

        h.Host.Notifications.All.Should().Equal(("其他電腦的代理通知你", "「擁有者的對話」通知你：要驗證", ReplicaId));
        h.Host.Spoken.Count.Should().Be(0, "那台的喇叭已經念了");
        h.Main.Messages("flash", mark, ReplicaId).Should().ContainSingle();

        h.Ui.Invoke(() => h.Hub.ApplyReplicaDelta(Replica(new ReplicaNotice(ReplicaId, "昨天的", now - 86_400_000))));
        h.Host.Notifications.Count.Should().Be(1, "離線期間累積的舊通知不跳");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-599_000, true)]
    [InlineData(599_000, true)]
    [InlineData(-601_000, false)]
    [InlineData(601_000, false)]
    public void 複本通知_前後十分鐘內才跳(long offsetMs, bool expected) => ReplicaNoticeRules.IsFresh(1_800_000_000_000 + offsetMs, 1_800_000_000_000).Should().Be(expected);

    [Fact]
    public void 複本通知的句子_帶了照用_沒帶用只有標題的_不是通知沒有()
    {
        var meta = ConversationWire.Serialize(new ConversationMetaPayload("Claude", "a", "標題", "Ask", null, null, false, null, ConversationSyncPolicies.Full, 1, 2, false, false));
        ReplicaRules.NoticeText("""{"kind":"question","speech":"「標題」通知你：乙","notice":true}""", meta).Should().Be("「標題」通知你：乙");
        ReplicaRules.NoticeText("""{"kind":"question","notice":true}""", meta).Should().Be("「標題」有事通知你");
        ReplicaRules.NoticeText("""{"kind":"question","speech":"","notice":true}""", meta).Should().Be("「標題」有事通知你");
        ReplicaRules.NoticeText("""{"kind":"question","speech":"「標題」有問題想問你：乙"}""", meta).Should().BeNull();
    }

    // ───── 安全機制換模型 ─────

    private static AgentModelRefusalEvent Switched(string from = "claude-fable-5-2", string to = "claude-opus-4-8")
        => new(from, to, SessionSwitched: true, "攔下了");

    [Fact]
    public async Task 安全機制換模型_提示與標頭_按換回送set_model_成功就收掉()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        s.ProcessInstance = "p1";
        var mark = h.Main.Mark();

        h.Raise(s, Switched());
        var header = await HeaderAsync(h, id, hd => hd["modelSwitch"] is JObject, mark);
        header["header"]!["modelSwitch"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"from":"Fable 5.2","to":"Opus 4.8"}""");
        h.Items(id).Should().ContainSingle(i => i.Kind == AgentItemKind.Notice && i.Text == "攔下了");

        mark = h.Main.Mark();
        h.Command(h.Main, "modelSwitchBack", new { conversationId = id });
        await HeaderAsync(h, id, hd => hd["modelSwitch"] == null, mark);
        h.Main.Messages("toast", mark).Should().BeEmpty();
    }

    [Fact]
    public async Task 換回失敗_提示並留著()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        s.ProcessInstance = "p1";
        s.CanSwitchModelLive = false;
        var mark = h.Main.Mark();
        h.Raise(s, Switched());
        await HeaderAsync(h, id, hd => hd["modelSwitch"] is JObject, mark);

        mark = h.Main.Mark();
        h.Command(h.Main, "modelSwitchBack", new { conversationId = id });
        (await h.Main.WaitForAsync("toast", null, mark)).Value<string>("text").Should().Contain("沒有換回");
        h.Raise(s, new AgentTextCompletedEvent("b", "嗯"));
        h.Raise(s, new AgentPermissionModeChangedEvent(AgentPermissionMode.Plan));
        (await HeaderAsync(h, id, hd => hd.Value<string>("permission") == "Plan", mark))["header"]!["modelSwitch"].Should().NotBeNull();
    }

    [Fact]
    public async Task 實際的回覆是原本的模型_程序重開_回到原本的_自己選模型_都收掉()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        s.ProcessInstance = "p1";

        async Task SwitchAsync()
        {
            var m = h.Main.Mark();
            h.Raise(s, Switched());
            await HeaderAsync(h, id, hd => hd["modelSwitch"] is JObject, m);
        }

        await SwitchAsync();
        h.Raise(s, new AgentReplyModelEvent("claude-opus-4-8[1m]"));
        h.OnUi(() => h.Hub.HasModelSwitch(id)).Should().BeTrue("還是接手的模型");
        h.Raise(s, new AgentReplyModelEvent("claude-fable-5-2"));
        h.OnUi(() => h.Hub.HasModelSwitch(id)).Should().BeFalse();

        await SwitchAsync();
        h.Raise(s, new AgentModelRefusalEvent("claude-fable-5-2", "claude-opus-4-8", SessionSwitched: false, "只有子代理"));
        h.Raise(s, new AgentModelRefusalEvent("claude-opus-4-8", null, SessionSwitched: false, "沒有接手的"));
        h.OnUi(() => h.Hub.HasModelSwitch(id)).Should().BeTrue("對話本身沒變");
        h.Raise(s, new AgentModelRefusalEvent("claude-fable-5-2", null, SessionSwitched: false, "換回", Reverted: true));
        h.OnUi(() => h.Hub.HasModelSwitch(id)).Should().BeFalse();

        await SwitchAsync();
        s.ProcessInstance = "p2";
        h.Raise(s, new AgentProcessStartedEvent("p2", null));
        h.OnUi(() => h.Hub.HasModelSwitch(id)).Should().BeFalse("新程序照對話設定的模型");

        await SwitchAsync();
        h.Command(h.Main, "setOption", new { conversationId = id, key = "model", value = "default" });
        h.OnUi(() => h.Hub.HasModelSwitch(id)).Should().BeFalse("選同一個模型也算換回");
    }

    [Fact]
    public void 接手的模型又被攔_換回的仍是最早的模型()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        s.ProcessInstance = "p1";
        h.Raise(s, Switched());
        h.Raise(s, Switched("claude-opus-4-8", "claude-sonnet-5-5"));
        h.OnUi(() => h.Hub.ModelSwitchOf(id)).Should().Be(("claude-fable-5-2", "claude-sonnet-5-5"));
    }

    [Fact]
    public async Task 撤回的回覆與思考從對話拿掉_工具卡與不認得的不動()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentThinkingDeltaEvent("m:0", "想"));
        h.Raise(s, new AgentTextDeltaEvent("m:1", "被撤回的"));
        h.Raise(s, new AgentToolStartedEvent("t1", "讀取檔案", "read", "a.txt", null));
        h.Raise(s, new AgentTextDeltaEvent("n:0", "留著的"));
        var gone = h.Items(id).Where(i => i.Text is "想" or "被撤回的").Select(i => i.Id).ToList();
        gone.Should().HaveCount(2);
        var mark = h.Main.Mark();

        h.Raise(s, new AgentBlocksRetractedEvent(["m:0", "m:1", "nope"]));

        h.Items(id).Select(i => i.Id).Should().NotContain(gone);
        h.Items(id).Should().Contain(i => i.Text == "留著的").And.Contain(i => i.Kind == AgentItemKind.Tool);
        await h.WaitUntilAsync(() => h.Main.Messages("removeItem", mark, id).Count == 2, "畫面跟著拿掉", 5000);
    }

    // ───── 沒人看的對話 ─────

    [Fact]
    public async Task 看不到就告訴會話沒人看_看得到立刻說有人看_沒變不重送()
    {
        using var h = new HubHarness();
        h.Ui.Invoke(() => h.Hub.StreamUnwatchDelay = TimeSpan.FromMilliseconds(50));
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        s.WatchedChanges.Count.Should().Be(0, "這個視窗還沒回報看得到哪些：當成有人看");

        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        s.WatchedChanges.Count.Should().Be(0, "本來就當成有人看");
        h.Command(h.Main, "visible", new { conversationIds = Array.Empty<string>() });
        await h.WaitUntilAsync(() => s.WatchedChanges.Count == 1, "沒人看", 5000);
        s.WatchedChanges.All.Should().Equal(false);
        h.Command(h.Main, "visible", new { conversationIds = Array.Empty<string>() });
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        s.WatchedChanges.All.Should().Equal(false, true);
    }

    [Fact]
    public async Task 很快切走又切回_不說沒人看()
    {
        using var h = new HubHarness();
        h.Ui.Invoke(() => h.Hub.StreamUnwatchDelay = TimeSpan.FromMilliseconds(400));
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        h.Command(h.Main, "visible", new { conversationIds = Array.Empty<string>() });
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        await Task.Delay(900);
        s.WatchedChanges.Count.Should().Be(0);
    }

    [Fact]
    public async Task 瀏覽器版正看著_算有人看_政策要是full()
    {
        using var h = new HubHarness();
        h.Ui.Invoke(() => h.Hub.StreamUnwatchDelay = TimeSpan.FromMilliseconds(20));
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Ui.Invoke(() => h.Conversation(id)!.SyncPolicy = ConversationSyncPolicies.Full);
        h.Command(h.Main, "visible", new { conversationIds = Array.Empty<string>() });
        await h.WaitUntilAsync(() => s.WatchedChanges.Count == 1, "沒人看", 5000);

        h.Ui.Invoke(() => ((IConversationPublishSource)h.Hub).SetWatched(new HashSet<string> { id }));
        s.WatchedChanges.All.Should().Equal(false, true);
        h.Ui.Invoke(() => ((IConversationPublishSource)h.Hub).SetWatched(new HashSet<string>()));
        await h.WaitUntilAsync(() => s.WatchedChanges.Count == 3, "又沒人看", 5000);
        s.WatchedChanges.All.Should().Equal(false, true, false);
    }

    // ───── 插入的訊息被讀到 ─────

    [Fact]
    public async Task 插入的訊息被讀到_收掉那一行_別則的不動()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude, "第一則");
        session.CanSteer = true;
        var mark = h.Main.Mark();
        h.Send(h.Main, id, "順便跑測試");
        await h.Main.WaitForAsync("composer", m => m["composer"]?["steered"] is JObject, mark, conversationId: id);
        var uuid = session.Steered.All[0].MessageUuid!;

        h.Raise(session, new AgentSteerReadEvent(Guid.NewGuid().ToString()));
        mark = h.Main.Mark();
        h.Raise(session, new AgentSteerReadEvent(uuid));
        var composer = await h.Main.WaitForAsync("composer", null, mark, conversationId: id);
        composer["composer"]!["steered"]!.Type.Should().Be(JTokenType.Null);
    }

    // ───── 給代理的說明 ─────

    [Fact]
    public void 給Claude的說明帶通知的用法_其他代理沒有()
    {
        using var h = new HubHarness();
        var (_, claude) = h.StartConversation(h.Main, h.Claude);
        var (_, codex) = h.StartConversation(h.Main, h.Codex);
        claude.Sent.All[0].Settings.Instructions.Should().Contain("PushNotification").And.Contain("不必重送");
        codex.Sent.All[0].Settings.Instructions.Should().NotContain("PushNotification");
    }
}
