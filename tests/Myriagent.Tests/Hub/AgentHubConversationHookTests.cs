using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 對話變化的掛鉤（IConversationChangeSink，設計 14.11 D47；計畫步 9）：每個掛鉤的觸發點與次數，包括沒有檢視時的 MarkItemDirty；
/// 接收者丟例外不影響中樞；沒有接收者時一切照舊（既有的中樞測試）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubConversationHookTests
{
    private sealed record Call(string Hook, string? ConversationId, string? Detail = null);

    /// <summary>記錄每次呼叫；onDeleting 在 OnConversationDeleting 當下執行（確認對話還在中樞裡）</summary>
    private sealed class RecordingSink(Action<string>? onDeleting = null) : IConversationChangeSink
    {
        public Recorder<Call> Calls { get; } = new();

        public int Count(string hook, string? conversationId = null, string? detail = null)
            => Calls.All.Count(c => c.Hook == hook && (conversationId == null || c.ConversationId == conversationId) && (detail == null || c.Detail == detail));

        public int Mark() => Calls.Count;

        public IReadOnlyList<Call> Since(int mark) => Calls.All.Skip(mark).ToList();

        public void OnItemChanged(ConversationHookInfo conversation, AgentChatItem item) => Calls.Add(new(nameof(OnItemChanged), conversation.Meta.Id, item.Id));

        public void OnItemRemoved(ConversationHookInfo conversation, AgentChatItem item) => Calls.Add(new(nameof(OnItemRemoved), conversation.Meta.Id, item.Id));

        public void OnConversationSaved(ConversationHookInfo conversation, bool itemsSaved)
            => Calls.Add(new(nameof(OnConversationSaved), conversation.Meta.Id, itemsSaved ? "items" : "meta"));

        public void OnConversationDeleting(ConversationHookInfo conversation)
        {
            onDeleting?.Invoke(conversation.Meta.Id);
            Calls.Add(new(nameof(OnConversationDeleting), conversation.Meta.Id));
        }

        public void OnHeaderChanged(ConversationHookInfo conversation) => Calls.Add(new(nameof(OnHeaderChanged), conversation.Meta.Id));

        public void OnRunningCountsChanged() => Calls.Add(new(nameof(OnRunningCountsChanged), null));

        public void OnQueueChanged(ConversationHookInfo conversation) => Calls.Add(new(nameof(OnQueueChanged), conversation.Meta.Id));

        public void OnComposerChanged(ConversationHookInfo conversation) => Calls.Add(new(nameof(OnComposerChanged), conversation.Meta.Id));

        /// <summary>每次 OnTurnStarted／OnTurnCompleted 呼叫當下的 Running（開始時要是 true、結束時要是 false）</summary>
        public Recorder<bool> RunningAtStart { get; } = new();

        public Recorder<bool> RunningAtCompletion { get; } = new();

        /// <summary>依序的 OnTurnStarted（send／cli）與 OnTurnCompleted（結果）</summary>
        public IReadOnlyList<string> Turns(int since = 0) => Since(since)
            .Where(c => c.Hook is nameof(OnTurnStarted) or nameof(OnTurnCompleted))
            .Select(c => c.Hook == nameof(OnTurnStarted) ? "started:" + c.Detail : "completed:" + c.Detail!.Split(':')[0])
            .ToList();

        public void OnTurnStarted(ConversationHookInfo conversation, bool startedByCli)
        {
            RunningAtStart.Add(conversation.Running);
            Calls.Add(new(nameof(OnTurnStarted), conversation.Meta.Id, startedByCli ? "cli" : "send"));
        }

        public void OnTurnCompleted(ConversationHookInfo conversation, AgentTurnOutcome outcome, AgentUsage? usage)
        {
            RunningAtCompletion.Add(conversation.Running);
            Calls.Add(new(nameof(OnTurnCompleted), conversation.Meta.Id, $"{outcome}:{usage?.OutputTokens}"));
        }

        /// <summary>每次 OnAttention 帶的語音句子（同順序）</summary>
        public Recorder<VoiceLine> Speeches { get; } = new();

        public void OnAttention(ConversationHookInfo conversation, string? kind, string? itemId, VoiceLine speech)
        {
            Speeches.Add(speech);
            Calls.Add(new(nameof(OnAttention), conversation.Meta.Id, $"{kind}:{itemId}"));
        }
    }

    /// <summary>每個方法都丟例外</summary>
    private sealed class ThrowingSink : IConversationChangeSink
    {
        public int Calls;

        private void Boom()
        {
            Calls++;
            throw new InvalidOperationException("接收者壞了");
        }

        public void OnItemChanged(ConversationHookInfo conversation, AgentChatItem item) => Boom();

        public void OnItemRemoved(ConversationHookInfo conversation, AgentChatItem item) => Boom();

        public void OnConversationSaved(ConversationHookInfo conversation, bool itemsSaved) => Boom();

        public void OnConversationDeleting(ConversationHookInfo conversation) => Boom();

        public void OnHeaderChanged(ConversationHookInfo conversation) => Boom();

        public void OnRunningCountsChanged() => Boom();

        public void OnQueueChanged(ConversationHookInfo conversation) => Boom();

        public void OnComposerChanged(ConversationHookInfo conversation) => Boom();

        public void OnTurnStarted(ConversationHookInfo conversation, bool startedByCli) => Boom();

        public void OnTurnCompleted(ConversationHookInfo conversation, AgentTurnOutcome outcome, AgentUsage? usage) => Boom();

        public void OnAttention(ConversationHookInfo conversation, string? kind, string? itemId, VoiceLine speech) => Boom();
    }

    private static RecordingSink Attach(HubHarness h, Action<string>? onDeleting = null)
    {
        var sink = new RecordingSink(onDeleting);
        h.Ui.Invoke(() => h.Hub.AttachConversationSink(sink));
        return sink;
    }

    [Fact]
    public void 提醒帶語音句子_完整版有說明_標題版沒有_背景工作還在跑時不念_不管這台的語音開關()
    {
        using var h = new HubHarness();
        var sink = Attach(h);
        h.Command(h.Main, "setting", new { key = "voiceNotifications", value = false });   // 這台的喇叭關掉，網頁版照樣要有句子
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var title = h.Conversation(id)!.Title;

        h.Raise(s, new AgentQuestionEvent("q-1", new[] { new AgentQuestion { Id = "q", Text = "要用哪個格式？" } }, AnswerAsMessage: false));
        h.Raise(s, new AgentTextCompletedEvent("b1", "月報已經匯出成 Excel。"));
        s.HasLiveBackgroundWork = true;
        h.CompleteTurn(s);
        s.HasLiveBackgroundWork = false;
        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));
        h.Raise(s, new AgentTextCompletedEvent("b2", "測試全部通過。"));
        h.CompleteTurn(s);

        sink.Speeches.All.Should().Equal(
            new VoiceLine($"「{title}」有問題想問你：要用哪個格式？", $"「{title}」有問題想問你"),
            VoiceLine.Silent,
            new VoiceLine($"「{title}」已完成：測試全部通過", $"「{title}」已完成"));
        h.Host.Spoken.Count.Should().Be(0, "這台的語音開關關著");
    }

    [Fact]
    public void 帳戶的模型清單改了_這個帳戶的對話通知標頭改變_發布的快照帶這個帳戶的清單()
    {
        // 發布的 meta 帶模型清單（其他電腦與瀏覽器版改模型時照它列選單）：清單變了要重發，沒變不動；別的帳戶的對話不受影響
        using var h = new HubHarness();
        var sink = Attach(h);
        var (claude, session) = h.StartConversation(h.Main, h.Claude);
        var (codex, _) = h.StartConversation(h.Main, h.Codex);
        var source = (IConversationPublishSource)h.Hub;
        h.OnUi(() => source.SnapshotMeta([codex])).Single().Models!.Select(m => m.Value)
            .Should().Equal(AgentHub.DefaultModels(AgentProvider.Codex).Select(m => m.Value), "CLI 還沒回報時是內建的預設選單");

        var mark = sink.Mark();
        h.Raise(session, new AgentModelsEvent([new AgentModelOption("haiku", "Haiku 5.5", null, ["low"]), new AgentModelOption("opus", "Opus", "複雜工作", ["high"])]));

        sink.Since(mark).Should().Contain(c => c.Hook == nameof(IConversationChangeSink.OnHeaderChanged) && c.ConversationId == claude);
        sink.Since(mark).Should().NotContain(c => c.ConversationId == codex, "別的帳戶的清單沒變");
        var snapshot = h.OnUi(() => source.SnapshotMeta([claude])).Single();
        snapshot.Models!.Select(m => (m.Value, m.Label)).Should().Equal(("haiku", "Haiku 5.5"), ("opus", "Opus"));

        var again = sink.Mark();
        h.Raise(session, new AgentModelsEvent([new AgentModelOption("haiku", "Haiku 5.5", null, ["low"]), new AgentModelOption("opus", "Opus", "複雜工作", ["high"])]));
        sink.Since(again).Should().NotContain(c => c.Hook == nameof(IConversationChangeSink.OnHeaderChanged), "清單沒變（每次啟動 CLI 都會回報一次）不重發");
    }

    [Fact]
    public void 送出_一輪開始_項目_存檔_執行數_標頭_輸入區()
    {
        using var h = new HubHarness();
        var sink = Attach(h);

        var (id, _) = h.StartConversation(h.Main, h.Claude);

        var user = h.Items(id).Single(i => i.Kind == AgentItemKind.User);
        sink.Count(nameof(IConversationChangeSink.OnTurnStarted), id, "send").Should().Be(1);
        sink.Count(nameof(IConversationChangeSink.OnTurnStarted)).Should().Be(1);
        sink.Count(nameof(IConversationChangeSink.OnItemChanged), id, user.Id).Should().BeGreaterThanOrEqualTo(1);
        sink.Count(nameof(IConversationChangeSink.OnConversationSaved), id, "items").Should().BeGreaterThanOrEqualTo(1);
        sink.Count(nameof(IConversationChangeSink.OnRunningCountsChanged)).Should().BeGreaterThanOrEqualTo(1);
        sink.Count(nameof(IConversationChangeSink.OnHeaderChanged), id).Should().BeGreaterThanOrEqualTo(1);
        sink.Count(nameof(IConversationChangeSink.OnComposerChanged), id).Should().BeGreaterThanOrEqualTo(1);
        sink.Count(nameof(IConversationChangeSink.OnTurnCompleted)).Should().Be(0);
        h.OnUi(() => h.Hub.IsRunning(id)).Should().BeTrue();
    }

    [Fact]
    public async Task 沒有檢視開著這個對話_項目改變照樣呼叫_畫面不送()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "close", new { conversationId = id });
        var sink = Attach(h);
        var mark = h.Main.Mark();

        h.Raise(s, new AgentTextDeltaEvent("b1", "你"));
        var reply = h.Items(id).Single(i => i.Kind == AgentItemKind.Assistant);
        var first = sink.Count(nameof(IConversationChangeSink.OnItemChanged), id, reply.Id);
        h.Raise(s, new AgentTextDeltaEvent("b1", "好"));

        first.Should().BeGreaterThanOrEqualTo(1, "新增的項目（AddItem 經 MarkItemDirty）");
        sink.Count(nameof(IConversationChangeSink.OnItemChanged), id, reply.Id).Should().Be(first + 1, "之後每個片段一次");
        await Task.Delay(150);
        h.Main.Messages("item", mark, id).Should().BeEmpty("沒有檢視開著這個對話，畫面不送");
    }

    [Fact]
    public void 一輪結束_注意_CLI自己開始的一輪()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var sink = Attach(h);

        h.Raise(s, new AgentQuestionEvent("q-1", new[] { new AgentQuestion { Id = "q", Text = "要嗎？" } }, AnswerAsMessage: false));
        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "npm test", "npm test", "t1", CanAllowForSession: true));
        h.Raise(s, new AgentApprovalEvent("plan-1", "確認執行計畫", "plan", "請確認", "1. 改 A", null, CanAllowForSession: false, IsPlan: true));
        var items = h.Items(id);
        var question = items.Single(i => i.Kind == AgentItemKind.Question);
        var approval = items.Single(i => i.Kind == AgentItemKind.Approval);
        var plan = items.Single(i => i.Kind == AgentItemKind.PlanApproval);

        sink.Since(0).Where(c => c.Hook == nameof(IConversationChangeSink.OnAttention)).Select(c => c.Detail).Should().Equal(
            $"{ConversationAttentionKinds.Question}:{question.Id}", $"{ConversationAttentionKinds.Approval}:{approval.Id}", $"{ConversationAttentionKinds.Plan}:{plan.Id}");

        var mark = sink.Mark();
        h.CompleteTurn(s, usage: new AgentUsage(10, 20, 0, null));
        var completed = sink.Since(mark);
        completed.Count(c => c.Hook == nameof(IConversationChangeSink.OnTurnCompleted)).Should().Be(1);
        completed.Single(c => c.Hook == nameof(IConversationChangeSink.OnTurnCompleted)).Detail.Should().Be("Completed:20");
        completed.Where(c => c.Hook == nameof(IConversationChangeSink.OnAttention)).Select(c => c.Detail).Should().Equal($"{ConversationAttentionKinds.Completed}:");
        completed.Should().Contain(c => c.Hook == nameof(IConversationChangeSink.OnConversationSaved) && c.Detail == "items");
        completed.Should().Contain(c => c.Hook == nameof(IConversationChangeSink.OnRunningCountsChanged));

        mark = sink.Mark();
        h.Raise(s, new AgentTurnStartedByCliEvent(AgentAutoTurnKind.Continuation));
        sink.Since(mark).Where(c => c.Hook == nameof(IConversationChangeSink.OnTurnStarted)).Select(c => c.Detail).Should().Equal("cli");
    }

    [Fact]
    public void 閒置時CLI自己開的一輪_開始與結束各記一次_送出比事件先到時不再記開始()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        var sink = Attach(h);

        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));
        h.CompleteTurn(s);

        sink.Turns().Should().Equal("started:cli", "completed:Completed");
        sink.RunningAtStart.All.Should().Equal(true);
        sink.RunningAtCompletion.All.Should().Equal(false);

        // 中樞已送出（會話讓這則接手了 CLI 自己那一輪）之後才收到事件：同一輪，不再發一次開始
        var mark = sink.Mark();
        h.Send(h.Main, id, "第二則");
        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: false));
        sink.Turns(mark).Should().Equal("started:send");
    }

    [Fact]
    public void 使用者按停止_結果是Interrupted_拒絕核准而結束的算Completed()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex);
        var sink = Attach(h);

        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "rm x", "rm x", "t1", CanAllowForSession: true));
        var approval = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        h.Command(h.Main, "approve", new { conversationId = id, itemId = approval.Id, choice = "deny" });
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        h.Send(h.Main, id, "再一次");
        h.Command(h.Main, "stop", new { conversationId = id });
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        sink.Since(0).Where(c => c.Hook == nameof(IConversationChangeSink.OnTurnCompleted)).Select(c => c.Detail).Should().Equal("Completed:", "Interrupted:");
    }

    [Fact]
    public void 佇列改變一次呼叫一次_取消也是()
    {
        using var h = new HubHarness();
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        var sink = Attach(h);

        h.Send(h.Main, id, "第二則");
        sink.Count(nameof(IConversationChangeSink.OnQueueChanged), id).Should().Be(1);
        sink.Count(nameof(IConversationChangeSink.OnComposerChanged), id).Should().BeGreaterThanOrEqualTo(1);

        // 沒變的輸入區重送不算佇列改變
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        sink.Count(nameof(IConversationChangeSink.OnQueueChanged), id).Should().Be(1);

        var queuedId = h.Main.Last("composer", id)!["composer"]!["queue"]!.Single()["id"]!.ToString();
        h.Command(h.Main, "cancelQueued", new { conversationId = id, id = queuedId });
        sink.Count(nameof(IConversationChangeSink.OnQueueChanged), id).Should().Be(2);
    }

    [Fact]
    public void 項目移除_改名存meta_刪除在移除之前()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        bool? stillThere = null;
        var sink = Attach(h, deleted => stillThere = h.Hub.GetConversation(deleted) != null);

        // 空白的回覆在完成時移除
        h.Raise(s, new AgentTextDeltaEvent("b1", "x"));
        var reply = h.Items(id).Single(i => i.Kind == AgentItemKind.Assistant);
        h.Raise(s, new AgentTextCompletedEvent("b1", " "));
        sink.Count(nameof(IConversationChangeSink.OnItemRemoved), id, reply.Id).Should().Be(1);
        h.CompleteTurn(s);

        var mark = sink.Mark();
        h.Command(h.Main, "rename", new { conversationId = id, title = "新名字" });
        var renamed = sink.Since(mark);
        renamed.Where(c => c.Hook == nameof(IConversationChangeSink.OnConversationSaved)).Select(c => c.Detail).Should().Equal("meta");
        renamed.Should().Contain(c => c.Hook == nameof(IConversationChangeSink.OnHeaderChanged) && c.ConversationId == id);

        h.Command(h.Main, "delete", new { conversationId = id });
        sink.Count(nameof(IConversationChangeSink.OnConversationDeleting), id).Should().Be(1);
        stillThere.Should().BeTrue("呼叫時對話還在中樞裡");
        h.Conversation(id).Should().BeNull();
    }

    [Fact]
    public void 草稿與副本的旗標跟著傳()
    {
        using var h = new HubHarness();
        var infos = new Recorder<ConversationHookInfo>();
        var sink = new FlagSink(infos);
        h.Ui.Invoke(() => h.Hub.AttachConversationSink(sink));

        var draft = h.NewConversation(h.Main, h.Claude);
        h.Command(h.Main, "rename", new { conversationId = draft, title = "草稿" });
        infos.All.Should().Contain(i => i.Meta.Id == draft && i.IsDraft && !i.IsReplica);

        var (id, session) = h.StartConversation(h.Main, h.Claude);
        h.Ui.Invoke(() => h.Hub.MarkReplicaForTest(id));
        // 複本不收改名這類網頁指令（WP4.3 步 15：唯讀）：改以會話事件觸發標頭的掛鉤
        h.Raise(session, new AgentPermissionModeChangedEvent(AgentPermissionMode.Plan));
        infos.All.Last(i => i.Meta.Id == id).Should().Match<ConversationHookInfo>(i => i.IsReplica && !i.IsDraft && i.Running);
    }

    /// <summary>只記標頭掛鉤的對話資訊</summary>
    private sealed class FlagSink(Recorder<ConversationHookInfo> infos) : IConversationChangeSink
    {
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

        public void OnHeaderChanged(ConversationHookInfo conversation) => infos.Add(conversation);

        public void OnRunningCountsChanged()
        {
        }

        public void OnQueueChanged(ConversationHookInfo conversation)
        {
        }

        public void OnComposerChanged(ConversationHookInfo conversation)
        {
        }

        public void OnTurnStarted(ConversationHookInfo conversation, bool startedByCli)
        {
        }

        public void OnTurnCompleted(ConversationHookInfo conversation, AgentTurnOutcome outcome, AgentUsage? usage)
        {
        }

        public void OnAttention(ConversationHookInfo conversation, string? kind, string? itemId, VoiceLine speech)
        {
        }
    }

    [Fact]
    public void 接收者丟例外_中樞照常()
    {
        using var h = new HubHarness();
        var sink = new ThrowingSink();
        h.Ui.Invoke(() => h.Hub.AttachConversationSink(sink));

        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentTextDeltaEvent("b1", "好"));
        h.CompleteTurn(s);
        h.Command(h.Main, "rename", new { conversationId = id, title = "改名" });

        sink.Calls.Should().BeGreaterThan(5);
        h.Conversation(id)!.Title.Should().Be("改名");
        h.IsRunning(id).Should().BeFalse();
        h.Items(id).Should().Contain(i => i.Kind == AgentItemKind.Assistant && i.Text == "好");
    }

    // ───────────────────────────── 一輪的開始與結束成對（合併前審查） ─────────────────────────────

    private static readonly Myriagent.Cli.CliBinaryIdentity ClaudeOld = CliIdentities.Claude("2.1.295", fileId: 1);

    private static readonly Myriagent.Cli.CliBinaryIdentity ClaudeNew = CliIdentities.Claude("2.1.297", fileId: 2);

    [Fact]
    public async Task 正常結束_結束在Running改回false之後_接著送的下一輪之前()
    {
        using var h = new HubHarness();
        var sink = Attach(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Send(h.Main, id, "排隊的第二則");

        h.CompleteTurn(s);
        await h.Ui.DrainAsync();

        sink.Turns().Should().Equal("started:send", "completed:Completed", "started:send");
        sink.RunningAtStart.All.Should().Equal(true, true);
        sink.RunningAtCompletion.All.Should().Equal(false);
        h.IsRunning(id).Should().BeTrue("排隊的那則接著送出");
    }

    [Fact]
    public async Task 送出失敗_開始之後一定有一個Failed的結束()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        var sink = Attach(h);
        s.SendFailures.Enqueue(new InvalidOperationException("程序起不來"));

        h.Send(h.Main, id, "第二則");
        await h.Ui.DrainAsync();

        sink.Turns().Should().Equal("started:send", "completed:Failed");
        sink.RunningAtCompletion.All.Should().Equal(false);
        h.IsRunning(id).Should().BeFalse();
        h.Items(id).Last().Kind.Should().Be(AgentItemKind.Error);
    }

    [Fact]
    public async Task 換版不接續_這一輪在本機收尾_以Interrupted結束()
    {
        using var h = new HubHarness();
        await h.SwitchCliAsync(AgentProvider.Claude, null, ClaudeOld);
        var (id, s) = h.StartConversation(h.Main, h.Claude, "做事");
        h.ProcessStarted(s, "p1", ClaudeOld);
        s.SucceedRestarts();
        var sink = Attach(h);
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", "npm test"));
        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);
        h.Command(h.Main, "stop", new { conversationId = id });

        h.Command(h.Main, "cliApplyNow", new { conversationId = id });
        await h.Ui.DrainAsync();

        h.IsRunning(id).Should().BeFalse("沒有接續：這一輪在本機收尾");
        sink.Turns().Should().Equal("completed:Interrupted");
        sink.RunningAtCompletion.All.Should().Equal(false);
    }

    [Fact]
    public async Task 換版自動接續_接續的一輪與原本的算同一輪_只有一次開始一次結束()
    {
        using var h = new HubHarness();
        await h.SwitchCliAsync(AgentProvider.Claude, null, ClaudeOld);
        var sink = Attach(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude, "做事");
        h.ProcessStarted(s, "p1", ClaudeOld);
        s.SafePoint = true;
        s.SucceedRestarts();

        await h.SwitchCliAsync(AgentProvider.Claude, ClaudeOld, ClaudeNew);
        await h.Ui.DrainAsync();
        s.Restarts.All.Should().ContainSingle().Which.AutoContinue.Should().BeTrue();
        h.IsRunning(id).Should().BeTrue();
        h.CompleteTurn(s);
        await h.Ui.DrainAsync();

        sink.Turns().Should().Equal("started:send", "completed:Completed");
    }

    [Fact]
    public async Task 載入時收尾的卡片與工具_經過項目掛鉤()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        await h.RestartAsync();
        // 上次沒收尾就結束（app 當掉）：紀錄檔裡還有等待中的核准卡與執行中的工具
        var items = h.Store.LoadItems(id);
        var card = new AgentChatItem { Id = "c0f3c9a52b7e94d0c8a61f2d4e5b6c7d", Kind = AgentItemKind.Approval, Status = AgentItemStatus.Waiting, ToolName = "執行指令" };
        var tool = new AgentChatItem { Id = "d0f3c9a52b7e94d0c8a61f2d4e5b6c7d", Kind = AgentItemKind.Tool, Status = AgentItemStatus.Running, ToolName = "執行指令" };
        h.Store.SaveItems(id, items.Append(card).Append(tool).ToList());
        var sink = Attach(h);

        var loaded = h.Items(id);

        loaded.Single(i => i.Id == card.Id).Status.Should().Be(AgentItemStatus.Cancelled);
        loaded.Single(i => i.Id == tool.Id).Status.Should().Be(AgentItemStatus.Cancelled);
        sink.Count(nameof(IConversationChangeSink.OnItemChanged), id, card.Id).Should().Be(1, "WP4.3 要發新的持久 conv.item，伺服器上的卡才不會一直停在等待");
        sink.Count(nameof(IConversationChangeSink.OnItemChanged), id, tool.Id).Should().Be(1);
        sink.Count(nameof(IConversationChangeSink.OnItemChanged), id).Should().Be(2, "沒改狀態的項目不呼叫");
    }

    [Fact]
    public void 拿掉接收者之後不再呼叫()
    {
        using var h = new HubHarness();
        var sink = Attach(h);
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        var mark = sink.Mark();

        h.Ui.Invoke(() => h.Hub.AttachConversationSink(null));
        h.Command(h.Main, "rename", new { conversationId = id, title = "改名" });

        sink.Mark().Should().Be(mark);
    }
}
