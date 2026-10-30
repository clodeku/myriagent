using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.Sync.Pairing;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Wiring;

/// <summary>
/// UI 端的對話發布者（WP4.3 計畫步 7，D1、D2、D6、D12、D21、D33）：掛鉤只記髒、合併後才在 UI 執行緒複製放進盒子、
/// 標記之前先放待送的項目、盒子的上限與溢出、引擎要的中繼資料與項目頁（依 Ord 分頁）、釋放後什麼都不做。
/// 真的中樞（HubHarness）＋帶盒子的假引擎＋可撥的時鐘（計時器只在撥快時觸發）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class ConversationPublisherTests
{
    /// <summary>包住中樞的快照來源：記下 BeginInvoke 的次數與各方法的呼叫（含呼叫時是不是在 UI 執行緒）</summary>
    private sealed class CountingSource(IConversationPublishSource inner, UiThread ui) : IConversationPublishSource
    {
        private int beginInvokes;

        public int BeginInvokes => Volatile.Read(ref beginInvokes);

        public Recorder<string> Calls { get; } = new();

        public Recorder<int> MetaSliceSizes { get; } = new();

        /// <summary>每次 SnapshotItems 收到的 take</summary>
        public Recorder<int> ItemTakes { get; } = new();

        /// <summary>每次呼叫時是不是在 UI 執行緒（BeginInvoke 以外的方法）</summary>
        public Recorder<bool> OnUi { get; } = new();

        public string ConversationsRoot => inner.ConversationsRoot;

        public void BeginInvoke(Action action)
        {
            Interlocked.Increment(ref beginInvokes);
            inner.BeginInvoke(action);
        }

        public IReadOnlyList<string> ConversationIds()
        {
            Record(nameof(ConversationIds));
            return inner.ConversationIds();
        }

        public IReadOnlyList<PublishMetaSnapshot> SnapshotMeta(IReadOnlyList<string> ids)
        {
            Record(nameof(SnapshotMeta));
            MetaSliceSizes.Add(ids.Count);
            return inner.SnapshotMeta(ids);
        }

        public PublishItemsPage? SnapshotItems(string conversationId, long afterOrd, int take, OrdHint? hint)
        {
            Record(nameof(SnapshotItems));
            ItemTakes.Add(take);
            return inner.SnapshotItems(conversationId, afterOrd, take, hint);
        }

        public PublishQueueSnapshot? SnapshotQueue(string conversationId)
        {
            Record(nameof(SnapshotQueue));
            return inner.SnapshotQueue(conversationId);
        }

        public void SetWatched(IReadOnlySet<string> conversationIds)
        {
            Record(nameof(SetWatched));
            inner.SetWatched(conversationIds);
        }

        public void PauseForDlp(string conversationId)
        {
            Record(nameof(PauseForDlp));
            inner.PauseForDlp(conversationId);
        }

        public bool MarkServerDeleted(string conversationId, long atMs)
        {
            Record(nameof(MarkServerDeleted));
            return inner.MarkServerDeleted(conversationId, atMs);
        }

        public void ShowPublishNotice(PublishNoticeKind kind, int count)
        {
            Record(nameof(ShowPublishNotice));
            inner.ShowPublishNotice(kind, count);
        }

        public void ApplyReplicaDelta(ReplicaDelta delta)
        {
            Record(nameof(ApplyReplicaDelta));
            inner.ApplyReplicaDelta(delta);
        }

        private void Record(string call)
        {
            Calls.Add(call);
            OnUi.Add(ui.IsCurrent);
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(Action<ConversationStore>? seedStore = null)
        {
            H = new HubHarness(seedStore: seedStore);
            Engine = new RecordingSyncEngine { Conversations = Box };
            Source = new CountingSource(H.Hub, H.Ui);
            Publisher = H.OnUi(() => ConversationPublisher.TryCreate(Source, Engine, Time))!;
            H.Ui.Invoke(() => H.Hub.AttachConversationSink(Publisher));
        }

        public HubHarness H { get; }

        public ManualTimeProvider Time { get; } = new();

        public ConversationChangeBox Box { get; } = new();

        public RecordingSyncEngine Engine { get; }

        public CountingSource Source { get; }

        public ConversationPublisher Publisher { get; }

        /// <summary>引擎那一側看到的入口（測試執行緒呼叫＝引擎執行緒呼叫）</summary>
        public IConversationUiPort Port => Publisher;

        public int Wakes => Engine.Of<ConversationWake>().Count;

        /// <summary>在 UI 執行緒撥快：計時器的回呼經 BeginInvoke 就地 flush</summary>
        public void Advance(int ms) => H.Ui.Invoke(() => Time.Advance(TimeSpan.FromMilliseconds(ms)));

        public List<ConversationChange> Take() => Box.TakeAll().ToList();

        /// <summary>開一個對話、送出第一則，再把開頭的變化都送完、取走</summary>
        public (string Id, FakeSession Session) Started(AgentAccount account)
        {
            var started = H.StartConversation(H.Main, account);
            Advance(5_000);
            Take();
            return started;
        }

        public void Dispose() => H.Dispose();
    }

    private static AgentConversation Seeded(string title = "上次的對話") => new()
    {
        AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = title, SessionId = Guid.NewGuid().ToString(), SessionStarted = true,
    };

    private static ConversationHookInfo Info(HubHarness h, string id) => h.OnUi(() =>
    {
        var meta = h.Hub.GetConversation(id)!;
        return new ConversationHookInfo(meta, false, false, h.Hub.IsRunning(id));
    });

    private static async Task<PublishItemsPage> PageAsync(Rig rig, string id, long afterOrd, int take)
    {
        rig.Port.RequestItems(id, afterOrd, take, null);
        await rig.H.Ui.DrainAsync();
        return rig.Take().OfType<ItemsPageChange>().Single(c => c.ConversationId == id && c.AfterOrd == afterOrd).Page!;
    }

    // ───── 只記髒、合併、喚醒 ─────

    [Fact]
    public void 串流200個片段_盒子每項目一筆_只喚醒一次()
    {
        using var rig = new Rig();
        var (id, s) = rig.Started(rig.H.Claude);
        var wakes = rig.Wakes;

        for (var i = 0; i < 200; i++)
            rig.H.Raise(s, new AgentTextDeltaEvent("b1", "字"));

        rig.Box.Count.Should().Be(0, "掛鉤裡只記髒，flush 由計時器在合併之後做");
        rig.Wakes.Should().Be(wakes);

        rig.Advance(PublishTimingRules.UnwatchedRunningFlushMs);
        var reply = rig.H.Items(id).Single(i => i.Kind == AgentItemKind.Assistant);
        var changes = rig.Take();
        changes.OfType<ItemChanged>().Where(c => c.Item.Id == reply.Id).Should().ContainSingle("同一個項目的 200 個片段合成一筆")
            .Which.Item.Text.Should().HaveLength(200);
        changes.OfType<ItemChanged>().Single(c => c.Item.Id == reply.Id).Item.Should().NotBeSameAs(reply, "盒子裡是 UI 執行緒複製的快照");
        rig.Wakes.Should().Be(wakes + 1, "盒子從空變成非空才喚醒一次");
    }

    [Fact]
    public void OnItemChanged的配置與文字長度無關()
    {
        using var rig = new Rig();
        var (id, _) = rig.Started(rig.H.Claude);
        var info = Info(rig.H, id);
        var small = new AgentChatItem { Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Running, Text = new string('a', 1024) };
        var large = new AgentChatItem { Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Running, Text = new string('b', 1024 * 1024) };

        long Measure(AgentChatItem item) => rig.H.OnUi(() =>
        {
            // 暖身：第一次建立這個項目的待送槽與計時器
            rig.Publisher.OnItemChanged(info, item);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1_000; i++)
                rig.Publisher.OnItemChanged(info, item);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        });

        var forSmall = Measure(small);
        var forLarge = Measure(large);

        Math.Abs(forLarge - forSmall).Should().BeLessThan(256, $"掛鉤裡不複製、不序列化（1 KB：{forSmall} B，1 MB：{forLarge} B）");
        rig.Box.Count.Should().Be(0);
    }

    [Fact]
    public async Task 被看的50毫秒內合併成一次flush_沒被看的1秒()
    {
        using var rig = new Rig();
        var (seen, s1) = rig.Started(rig.H.Claude);
        var (unseen, s2) = rig.Started(rig.H.Claude2);
        rig.Port.SetWatched(new HashSet<string> { seen });
        await rig.H.Ui.DrainAsync();
        rig.H.OnUi(() => rig.H.Hub.RemoteWatched).Should().BeEquivalentTo([seen], "觀看集合也推給中樞（IsSeenRemotely 用）");

        // 狀態文字的標頭變化不急：沒被看的只有串流時照樣等 1 秒
        for (var i = 0; i < 5; i++)
        {
            rig.H.Raise(s1, new AgentTextDeltaEvent("b1", "看"));
            rig.H.Raise(s2, new AgentTextDeltaEvent("b1", "沒"));
        }

        rig.Advance(PublishTimingRules.WatchedFlushMs - 1);
        rig.Box.Count.Should().Be(0, "被看的也要合併 50 ms");
        rig.Advance(1);
        var first = rig.Take();
        first.OfType<ItemChanged>().Should().ContainSingle().Which.ConversationId.Should().Be(seen);

        rig.Advance(PublishTimingRules.UnwatchedRunningFlushMs - PublishTimingRules.WatchedFlushMs - 1);
        rig.Box.Count.Should().Be(0, "沒被看的執行中項目合併 1 秒");
        rig.Advance(1);
        rig.Take().OfType<ItemChanged>().Should().ContainSingle().Which.ConversationId.Should().Be(unseen);
        rig.Time.ActiveTimers.Should().Be(0, "沒有待送的變化時不留計時器");
    }

    [Fact]
    public void 沒被看的項目離開Running_50毫秒內就送()
    {
        using var rig = new Rig();
        var (id, s) = rig.Started(rig.H.Claude);
        rig.H.Raise(s, new AgentTextDeltaEvent("b1", "串流中"));
        rig.Advance(PublishTimingRules.UnwatchedRunningFlushMs);
        rig.Take();

        rig.H.Raise(s, new AgentTextDeltaEvent("b1", "，"));
        rig.H.Raise(s, new AgentTextCompletedEvent("b1", "串流中，完成"));
        rig.Advance(PublishTimingRules.WatchedFlushMs);

        rig.Take().OfType<ItemChanged>().Should().ContainSingle(c => c.ConversationId == id)
            .Which.Item.Status.Should().Be(AgentItemStatus.Done, "離開 Running 要立刻持久，不等 1 秒");
    }

    [Fact]
    public void 移除_存檔_佇列_沒被看也50毫秒內就送_只有標頭等1秒()
    {
        using var rig = new Rig();
        var (id, _) = rig.Started(rig.H.Claude);
        var info = Info(rig.H, id);
        var item = rig.H.Items(id).First();

        void Expect<T>(Action hook, int delayMs, string because) where T : ConversationChange
        {
            rig.H.Ui.Invoke(hook);
            rig.Advance(delayMs - 1);
            rig.Box.Count.Should().Be(0, because);
            rig.Advance(1);
            rig.Take().OfType<T>().Should().ContainSingle(c => c.ConversationId == id, because);
        }

        Expect<ItemRemovedChange>(() => rig.Publisher.OnItemRemoved(info, item), PublishTimingRules.WatchedFlushMs, "移除要立刻持久");
        Expect<MetaChanged>(() => rig.Publisher.OnConversationSaved(info, itemsSaved: false), PublishTimingRules.WatchedFlushMs, "存檔的中繼資料（政策降級）要立刻送");
        Expect<QueueChanged>(() => rig.Publisher.OnQueueChanged(info), PublishTimingRules.WatchedFlushMs, "佇列要立刻送");
        Expect<MetaChanged>(() => rig.Publisher.OnHeaderChanged(info), PublishTimingRules.UnwatchedRunningFlushMs, "標頭常常只是狀態文字：沒被看的等 1 秒");
    }

    // ───── 標記（D6、D33） ─────

    [Fact]
    public void 一輪開始前先放待送的項目()
    {
        using var rig = new Rig();
        var (id, s) = rig.Started(rig.H.Claude);
        rig.H.CompleteTurn(s);
        rig.Advance(5_000);
        rig.Take();

        rig.H.Raise(s, new AgentNoticeEvent("兩輪之間的提示", false));
        var notice = rig.H.Items(id).Last();
        rig.H.Send(rig.H.Main, id, "第二則");

        var changes = rig.Take();
        var user = rig.H.Items(id).Last(i => i.Kind == AgentItemKind.User);
        var started = changes.FindIndex(c => c is TurnStartedChange { StartedByCli: false } && c.ConversationId == id);
        started.Should().BeGreaterThanOrEqualTo(0, "一輪開始的標記當下就放，不等計時器");
        changes.FindIndex(c => c is ItemChanged ic && ic.Item.Id == notice.Id).Should().BeInRange(0, started - 1, "標記之前先放這個對話待送的項目");
        changes.FindIndex(c => c is ItemChanged ic && ic.Item.Id == user.Id).Should().BeInRange(0, started - 1);
        changes.Skip(started + 1).OfType<MetaChanged>().First().Snapshot.Running.Should().BeTrue("一輪開始之後接一筆中繼資料");
    }

    [Fact]
    public void 一輪結束_待送的項目在前_之後接中繼資料()
    {
        using var rig = new Rig();
        var (id, s) = rig.Started(rig.H.Claude);
        rig.H.Raise(s, new AgentTextDeltaEvent("b1", "回覆"));

        rig.H.CompleteTurn(s, usage: new AgentUsage(1, 2, 0, null));

        var changes = rig.Take();
        var completed = changes.FindIndex(c => c is TurnCompletedChange { Outcome: AgentTurnOutcome.Completed } && c.ConversationId == id);
        completed.Should().BeGreaterThanOrEqualTo(0);
        changes.FindIndex(c => c is ItemChanged { Item.Kind: AgentItemKind.Assistant }).Should().BeInRange(0, completed - 1);
        ((TurnCompletedChange)changes[completed]).Usage!.OutputTokens.Should().Be(2);
        changes.Skip(completed + 1).OfType<MetaChanged>().First().Snapshot.Running.Should().BeFalse();
        changes.Skip(completed + 1).OfType<AttentionChange>().Select(c => c.Kind).Should().Equal(ConversationAttentionKinds.Completed);
    }

    [Fact]
    public void 一輪失敗_另放failed提醒()
    {
        using var rig = new Rig();
        var (id, s) = rig.Started(rig.H.Claude);

        rig.H.CompleteTurn(s, AgentTurnOutcome.Failed, "CLI 結束了");

        var changes = rig.Take();
        var completed = changes.FindIndex(c => c is TurnCompletedChange { Outcome: AgentTurnOutcome.Failed } && c.ConversationId == id);
        completed.Should().BeGreaterThanOrEqualTo(0);
        changes[completed + 1].Should().Be(new AttentionChange(id, ConversationAttentionKinds.Failed, null), "失敗不經 NotifyAttention，由發布者補 failed 的提醒（契約:654）");
    }

    [Fact]
    public void 沒有started的completed照放()
    {
        using var rig = new Rig();
        var (id, s) = rig.Started(rig.H.Claude);
        rig.H.CompleteTurn(s);
        rig.Advance(5_000);
        rig.Take();
        var info = Info(rig.H, id);

        rig.H.Ui.Invoke(() => rig.Publisher.OnTurnCompleted(info, AgentTurnOutcome.Interrupted, null));

        rig.Take().Should().ContainSingle(c => c is TurnCompletedChange).Which.Should().Be(new TurnCompletedChange(id, AgentTurnOutcome.Interrupted, null),
            "CLI 自己接續的一輪可能沒有 started（F6、D33）");
    }

    [Fact]
    public void 提醒前先放待送的項目()
    {
        using var rig = new Rig();
        var (id, s) = rig.Started(rig.H.Claude);
        rig.H.Raise(s, new AgentTextDeltaEvent("b1", "還沒送"));
        var info = Info(rig.H, id);

        rig.H.Ui.Invoke(() => rig.Publisher.OnAttention(info, ConversationAttentionKinds.Question, "q1", VoiceLine.Silent));

        var changes = rig.Take();
        var attention = changes.FindIndex(c => c is AttentionChange { Kind: ConversationAttentionKinds.Question });
        attention.Should().BeGreaterThan(0, "提醒的標記當下就放，之前先放這個對話待送的項目");
        changes.Take(attention).OfType<ItemChanged>().Should().Contain(c => c.Item.Kind == AgentItemKind.Assistant);
    }

    [Fact]
    public async Task 草稿與複本不進盒子()
    {
        using var rig = new Rig();
        var (id, s) = rig.Started(rig.H.Claude);

        var draft = rig.H.NewConversation(rig.H.Main, rig.H.Claude);
        rig.H.Command(rig.H.Main, "rename", new { conversationId = draft, title = "草稿" });
        rig.H.Ui.Invoke(() => rig.H.Hub.MarkReplicaForTest(id));
        rig.H.Raise(s, new AgentNoticeEvent("副本的提示", false));
        rig.H.Command(rig.H.Main, "rename", new { conversationId = id, title = "副本" });
        rig.H.CompleteTurn(s);
        rig.Advance(5_000);

        rig.Take().Should().NotContain(c => c.ConversationId == draft || c.ConversationId == id);
        rig.Port.RequestMeta([draft, id]);
        await rig.H.Ui.DrainAsync();
        var batch = rig.Take().OfType<MetaBatch>().Single();
        batch.Metas.Should().HaveCount(2, "引擎要的快照照樣回（旗標在快照裡）");
        batch.Metas.Single(m => m.Meta.Id == draft).IsDraft.Should().BeTrue();
        batch.Metas.Single(m => m.Meta.Id == id).IsReplica.Should().BeTrue();

        var (other, _) = rig.Started(rig.H.Codex);
        rig.Port.RequestMeta(null);
        await rig.H.Ui.DrainAsync();
        rig.Take().OfType<MetaBatch>().Single().AllIds.Should().Equal([other], "重整的 id 集合不含草稿與副本");
    }

    [Fact]
    public void 草稿與副本_每個掛鉤都略過()
    {
        using var rig = new Rig();
        var (id, _) = rig.Started(rig.H.Claude);
        var item = rig.H.Items(id).First();
        var meta = Info(rig.H, id).Meta;

        // 中樞傳來的旗標：草稿、副本各一次
        foreach (var info in new[] { new ConversationHookInfo(meta, true, false, false), new ConversationHookInfo(meta, false, true, false) })
        {
            rig.H.Ui.Invoke(() =>
            {
                rig.Publisher.OnItemChanged(info, item);
                rig.Publisher.OnItemRemoved(info, item);
                rig.Publisher.OnConversationSaved(info, itemsSaved: true);
                rig.Publisher.OnHeaderChanged(info);
                rig.Publisher.OnQueueChanged(info);
                rig.Publisher.OnTurnStarted(info, startedByCli: false);
                rig.Publisher.OnTurnCompleted(info, AgentTurnOutcome.Failed, null);
                rig.Publisher.OnAttention(info, ConversationAttentionKinds.Question, null, VoiceLine.Silent);
                rig.Publisher.OnConversationDeleting(info);
            });
        }

        rig.Time.ActiveTimers.Should().Be(0, "連記髒都不記");
        rig.Advance(5_000);
        rig.Box.Count.Should().Be(0);
    }

    [Fact]
    public void 刪除_沒載入項目也放Deleting_沒送的變化丟掉()
    {
        var seeded = Seeded();
        using var rig = new Rig(store => store.SaveConversation(seeded));
        var (id, s) = rig.Started(rig.H.Claude);
        rig.H.CompleteTurn(s);
        rig.Advance(5_000);
        rig.Take();
        rig.H.Raise(s, new AgentNoticeEvent("沒送的", false));
        rig.H.OnUi(() => rig.H.Hub.GetConversation(seeded.Id)).Should().NotBeNull();

        rig.H.Command(rig.H.Main, "delete", new { conversationId = seeded.Id });
        rig.H.Command(rig.H.Main, "delete", new { conversationId = id });

        rig.Take().Should().Equal(new DeletingChange(seeded.Id), new DeletingChange(id));
        rig.Advance(5_000);
        rig.Take().Should().NotContain(c => c.ConversationId == id, "刪除時丟掉這個對話還沒送的變化");
    }

    // ───── 盒子（D2） ─────

    private static ItemChanged Item(string conversationId, string itemId, string text = "x")
        => new(conversationId, new AgentChatItem { Id = itemId, Kind = AgentItemKind.Notice, Status = AgentItemStatus.Done, Text = text });

    /// <summary>一筆變化的描述（項目是參照型別，紀錄的相等比的是參照，所以比對內容用這個）</summary>
    private static string Describe(ConversationChange change) => change switch
    {
        ItemChanged c => $"item:{c.ConversationId}:{c.Item.Id}:{c.Item.Text}",
        ItemRemovedChange c => $"removed:{c.ConversationId}:{c.ItemId}",
        TurnCompletedChange c => $"completed:{c.ConversationId}:{c.Outcome}",
        TurnStartedChange c => $"started:{c.ConversationId}",
        DeletingChange c => $"deleting:{c.ConversationId}",
        NeedsSnapshot c => $"snapshot:{c.ConversationId}",
        _ => change.GetType().Name + ":" + change.ConversationId,
    };

    [Fact]
    public void 盒子滿10000筆_改記NeedsSnapshot_快照回應不算上限()
    {
        var box = new ConversationChangeBox();
        box.Put(Item("a", "i0")).Should().BeTrue("原本是空的");
        for (var i = 1; i < ConversationChangeBox.MaxChanges; i++)
            box.Put(Item("a", "i" + i)).Should().BeFalse();
        box.ChangeCount.Should().Be(ConversationChangeBox.MaxChanges, "剛好滿：都收");
        box.Put(Item("a", "i0", "新的內容")).Should().BeFalse();
        box.ChangeCount.Should().Be(ConversationChangeBox.MaxChanges, "併進既有的槽不算新的一筆");

        // 快照回應不算上限
        for (var i = 0; i < 2 * ConversationChangeBox.MaxChanges; i++)
            box.Put(new ItemsPageChange("a", i, null));
        box.ChangeCount.Should().Be(ConversationChangeBox.MaxChanges);

        box.Put(Item("a", "多一筆"));
        box.ChangeCount.Should().Be(1, "溢出：丟掉這個對話的變更類，只記一筆 NeedsSnapshot");
        box.Put(Item("a", "之後的"));
        box.Put(new TurnStartedChange("a", false));
        box.ChangeCount.Should().Be(1, "溢出的對話之後的變更類都不收（要整份重取快照）");
        box.Put(Item("b", "別的對話"));
        box.Put(new DeletingChange("a"));

        var all = box.TakeAll();
        all.OfType<ItemsPageChange>().Should().HaveCount(2 * ConversationChangeBox.MaxChanges);
        all.Where(c => c is not ItemsPageChange).Select(Describe).Should().Equal("snapshot:a", "item:b:別的對話:x", "deleting:a");
        box.Count.Should().Be(0);
        box.Put(Item("a", "取走之後")).Should().BeTrue("取走之後重新開始");
    }

    [Fact]
    public void 盒子_同一個槽留最新_位置取第一次的_標記之後另開一槽()
    {
        var box = new ConversationChangeBox();
        box.Put(Item("a", "1", "舊"));
        box.Put(Item("a", "2"));
        box.Put(Item("a", "1", "新"));
        box.Put(new TurnCompletedChange("a", AgentTurnOutcome.Completed, null));
        box.Put(Item("a", "1", "標記之後"));
        box.Put(new ItemRemovedChange("a", "2"));
        box.Put(Item("b", "1"));

        box.TakeAll().Select(Describe).Should().Equal(
            "item:a:1:新",
            "item:a:2:x",
            "completed:a:Completed",
            "item:a:1:標記之後",
            "removed:a:2",
            "item:b:1:x");
    }

    [Fact]
    public void 盒子滿了_刪除的標記照收()
    {
        var box = new ConversationChangeBox();
        for (var i = 0; i < ConversationChangeBox.MaxChanges; i++)
            box.Put(Item("a", "i" + i));

        box.Put(new DeletingChange("b"));

        box.ChangeCount.Should().Be(ConversationChangeBox.MaxChanges + 1);
        box.TakeAll()[^1].Should().Be(new DeletingChange("b"), "刪除之後沒有快照可取，一定要收");
    }

    // ───── 引擎要的快照（D12） ─────

    [Fact]
    public async Task 要求meta_id集合在同一次UI呼叫取完_每批50個()
    {
        var seeded = Enumerable.Range(0, 120).Select(i => Seeded($"對話 {i}")).ToList();
        using var rig = new Rig(store =>
        {
            foreach (var conversation in seeded)
                store.SaveConversation(conversation);
        });
        var before = rig.Source.BeginInvokes;

        rig.Port.RequestMeta(null);
        await rig.H.Ui.DrainAsync();

        rig.Source.BeginInvokes.Should().Be(before + 1 + 3, "先一次取完整的 id 集合，再每批 50 個");
        rig.Source.Calls.All.Count(c => c == nameof(IConversationPublishSource.ConversationIds)).Should().Be(1);
        rig.Source.MetaSliceSizes.All.Should().Equal(50, 50, 20);
        rig.Source.OnUi.All.Should().OnlyContain(onUi => onUi, "都在 UI 執行緒");
        var batches = rig.Take().OfType<MetaBatch>().ToList();
        batches.Should().HaveCount(3);
        batches[0].AllIds.Should().BeEquivalentTo(seeded.Select(c => c.Id), "第一批附完整的 id 集合");
        batches.Skip(1).Should().OnlyContain(b => b.AllIds == null);
        batches.SelectMany(b => b.Metas).Select(m => m.Meta.Id).Should().BeEquivalentTo(seeded.Select(c => c.Id));
        batches.SelectMany(b => b.Metas).Should().OnlyContain(m => !m.ItemsLoaded && m.EffectivePolicy == ConversationSyncPolicies.Full && !m.ServerDeleted);
        rig.H.OnUi(() => seeded.All(c => rig.H.Hub.GetConversation(c.Id) != null)).Should().BeTrue();
    }

    [Fact]
    public async Task 要求meta_沒有任何對話_回一批空的附空集合()
    {
        using var rig = new Rig();

        rig.Port.RequestMeta(null);
        await rig.H.Ui.DrainAsync();

        var batch = rig.Take().OfType<MetaBatch>().Single();
        batch.Metas.Should().BeEmpty();
        batch.AllIds.Should().BeEmpty();
    }

    [Fact]
    public async Task 項目依Ord分頁_途中移除項目_不跳號_第一頁有完整id集合()
    {
        using var rig = new Rig();
        var (id, s) = rig.Started(rig.H.Claude);
        rig.H.Raise(s, new AgentTextDeltaEvent("b1", "x"));
        for (var i = 0; i < 25; i++)
            rig.H.Raise(s, new AgentNoticeEvent($"提示 {i}", false));
        var removed = rig.H.Items(id).Single(i => i.Kind == AgentItemKind.Assistant);

        var first = await PageAsync(rig, id, 0, 10);

        first.OrdsReady.Should().BeTrue();
        first.Items.Should().HaveCount(10);
        first.More.Should().BeTrue();
        first.AllIds.Should().Equal(rig.H.Items(id).Select(i => i.Id), "第一頁附完整的項目 id 集合（清單順序）");
        first.Items.Should().Contain(i => i.Id == removed.Id);
        first.Items.Single(i => i.Id == removed.Id).Should().NotBeSameAs(removed, "頁裡是 UI 執行緒複製的快照");
        first.LastOrd.Should().Be(first.Items[^1].Ord!.Value);

        rig.H.Raise(s, new AgentTextCompletedEvent("b1", " "));
        rig.H.Items(id).Should().NotContain(i => i.Id == removed.Id, "空白的回覆在完成時移除（第一頁裡的一筆）");

        var pages = new List<PublishItemsPage> { first };
        while (pages[^1].More)
        {
            var next = await PageAsync(rig, id, pages[^1].LastOrd, 10);
            next.AllIds.Should().BeNull("只有第一頁附完整的 id 集合");
            pages.Add(next);
        }

        var sent = pages.SelectMany(p => p.Items).ToList();
        rig.H.Items(id).Select(i => i.Id).Should().BeSubsetOf(sent.Select(i => i.Id), "依 Ord 分頁：途中移除項目也不會跳過還沒送的");
        sent.Select(i => i.Id).Should().OnlyHaveUniqueItems();
        sent.Select(i => i.Ord!.Value).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task 項目檔損毀過_第一頁的id集合不拿來推斷刪除()
    {
        using var rig = new Rig();
        var (id, s) = rig.Started(rig.H.Claude);
        rig.H.Raise(s, new AgentNoticeEvent("提示", false));
        (await PageAsync(rig, id, 0, 10)).RemovalsTrusted.Should().BeTrue();

        // 載入時損毀的 items.json 被 JsonFile 改名保留：清單只剩錯誤項目，之後存的新檔也不完整（重開之後記憶體的旗標就沒了，要靠檔案判斷）
        var dir = rig.H.Store.GetConversationDirectory(id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "items.json.corrupt-20261016-090000"), "[{");

        var first = await PageAsync(rig, id, 0, 10);
        first.RemovalsTrusted.Should().BeFalse("清單可能少了東西：不推斷刪除（合併前審查 F2）");
        first.AllIds.Should().NotBeNull("其他項目照常比對、發布");
    }

    [Fact]
    public async Task 項目頁_沒載入的不讀檔回null_草稿與找不到的也是()
    {
        var seeded = Seeded();
        using var rig = new Rig(store =>
        {
            store.SaveConversation(seeded);
            store.SaveItems(seeded.Id, [new AgentChatItem { Kind = AgentItemKind.User, Text = "上次的訊息" }]);
        });
        var draft = rig.H.NewConversation(rig.H.Main, rig.H.Claude);

        foreach (var id in new[] { seeded.Id, draft, "沒有這個對話" })
            rig.Port.RequestItems(id, 0, PublishTimingRules.ItemsPage, null);
        await rig.H.Ui.DrainAsync();

        rig.Take().OfType<ItemsPageChange>().Should().OnlyContain(c => c.Page == null).And.HaveCount(3);
        rig.H.OnUi(() => ((IConversationPublishSource)rig.H.Hub).SnapshotMeta([seeded.Id])).Single().ItemsLoaded
            .Should().BeFalse("沒載入的對話不在 UI 執行緒讀檔（D10）");
    }

    [Fact]
    public async Task 項目頁_take超過500筆以500筆計()
    {
        using var rig = new Rig();
        var (id, s) = rig.Started(rig.H.Claude);
        for (var i = 0; i < 3; i++)
            rig.H.Raise(s, new AgentNoticeEvent($"提示 {i}", false));

        rig.Port.RequestItems(id, 0, 100_000, null);
        rig.Port.RequestItems(id, 0, 0, null);
        await rig.H.Ui.DrainAsync();

        rig.Source.ItemTakes.All.Should().Equal([PublishTimingRules.ItemsPage, 1], "take 夾在 1～500（UI 執行緒的預算）");
        rig.Take().OfType<ItemsPageChange>().Select(c => c.Page!.Items.Count).Should().Equal(4, 1);
    }

    // ───── 佇列、伺服器刪除、dlp、提示、進度 ─────

    [Fact]
    public void 佇列快照用DisplayText_重開後認不出卡片的舊答案換成佔位()
    {
        var seeded = Seeded();
        var oldAnswer = "我的回答：\n密碼：舊的秘密-" + Guid.NewGuid().ToString("N")[..8];
        using var rig = new Rig(store =>
        {
            store.SaveConversation(seeded);
            QueueStore.Save(store.GetConversationDirectory(seeded.Id), new QueueSnapshot(
                [new QueuedMessageData("q-old", oldAnswer, []), new QueuedMessageData("q-plain", "一般的排隊訊息", [])], [], []));
        });

        // 執行中作答（秘密題目）：佇列帶遮蔽後的 DisplayText
        var (id, s) = rig.Started(rig.H.Codex);
        rig.H.Raise(s, new AgentQuestionEvent("async:m1", [new AgentQuestion { Id = "pwd", Text = "密碼", IsSecret = true }], AnswerAsMessage: true));
        var card = rig.H.Items(id).Single(i => i.Kind == AgentItemKind.Question);
        var secret = "答案-" + Guid.NewGuid().ToString("N")[..16];
        rig.H.Command(rig.H.Main, "answer", new { conversationId = id, itemId = card.Id, answers = new Dictionary<string, string[]> { ["pwd"] = [secret] } });
        rig.H.Ui.Invoke(() => rig.Publisher.OnQueueChanged(Info(rig.H, seeded.Id)));
        rig.Advance(PublishTimingRules.WatchedFlushMs);

        var queues = rig.Take().OfType<QueueChanged>().ToList();
        queues.Single(q => q.ConversationId == id).Snapshot.Entries.Select(e => e.DisplayText).Should().Equal("我的回答：\n密碼：（已隱藏）");
        queues.Single(q => q.ConversationId == seeded.Id).Snapshot.Entries.Should().Equal(
            ("q-old", SecretAnswerScrubRules.AmbiguousPlaceholder), ("q-plain", "一般的排隊訊息"));
        queues.SelectMany(q => q.Snapshot.Entries).Should().NotContain(e => e.DisplayText.Contains(secret) || e.DisplayText.Contains(oldAnswer));
    }

    [Fact]
    public async Task 有伺服器刪除旁檔_快照的有效政策是localOnly_ServerDeleted為真()
    {
        var deleted = Seeded("伺服器刪除過的");
        var normal = Seeded("一般的");
        using var rig = new Rig(store =>
        {
            store.SaveConversation(deleted);
            store.SaveConversation(normal);
            store.TryWriteServerDeleted(deleted.Id, 1_000).Should().BeTrue();
        });

        rig.Port.RequestMeta([deleted.Id, normal.Id]);
        await rig.H.Ui.DrainAsync();

        var metas = rig.Take().OfType<MetaBatch>().Single().Metas;
        metas.Single(m => m.Meta.Id == deleted.Id).Should().Match<PublishMetaSnapshot>(m => m.EffectivePolicy == ConversationSyncPolicies.LocalOnly && m.ServerDeleted);
        metas.Single(m => m.Meta.Id == normal.Id).Should().Match<PublishMetaSnapshot>(m => m.EffectivePolicy == ConversationSyncPolicies.Full && !m.ServerDeleted);
        metas.Single(m => m.Meta.Id == deleted.Id).Meta.Should().NotBeSameAs(rig.H.Conversation(deleted.Id), "中繼資料是複本");
    }

    [Fact]
    public async Task MarkServerDeleted_在UI執行緒寫旁檔_結果放回盒子()
    {
        var seeded = Seeded();
        using var rig = new Rig(store => store.SaveConversation(seeded));
        var path = Path.Combine(rig.H.Store.GetConversationDirectory(seeded.Id), ConversationStore.ServerDeletedFileName);

        rig.Port.MarkServerDeleted(seeded.Id);
        rig.Port.MarkServerDeleted("沒有這個對話");
        await rig.H.Ui.DrainAsync();

        rig.Take().OfType<ServerDeletedWritten>().Should().Equal(new ServerDeletedWritten(seeded.Id, true), new ServerDeletedWritten("沒有這個對話", false));
        rig.Source.OnUi.All.Should().OnlyContain(onUi => onUi, "寫旁檔在 UI 執行緒");
        JObject.Parse(File.ReadAllText(path)).Value<long>("at").Should().Be(rig.Time.NowMs, "時間取發布者的時鐘");
        Directory.Exists(rig.H.Store.GetConversationDirectory("沒有這個對話")).Should().BeFalse("找不到的對話不建資料夾");
    }

    [Fact]
    public async Task PauseForDlp_比paused寬的改成paused存檔_更嚴格的不動()
    {
        var full = Seeded("完整");
        var local = Seeded("只在這台");
        local.SyncPolicy = ConversationSyncPolicies.LocalOnly;
        using var rig = new Rig(store =>
        {
            store.SaveConversation(full);
            store.SaveConversation(local);
        });

        rig.Port.PauseForDlp(full.Id);
        rig.Port.PauseForDlp(local.Id);
        rig.Port.PauseForDlp("沒有這個對話");
        await rig.H.Ui.DrainAsync();

        rig.H.Conversation(full.Id)!.SyncPolicy.Should().Be(ConversationSyncPolicies.Paused);
        rig.H.Store.LoadConversations().Single(c => c.Id == full.Id).SyncPolicy.Should().Be(ConversationSyncPolicies.Paused, "經 SaveConversation 存檔");
        rig.H.Conversation(local.Id)!.SyncPolicy.Should().Be(ConversationSyncPolicies.LocalOnly, "已經比 paused 嚴格的不動");
        rig.Advance(PublishTimingRules.WatchedFlushMs);
        rig.Take().OfType<MetaChanged>().Should().Contain(m => m.ConversationId == full.Id && m.Snapshot.EffectivePolicy == ConversationSyncPolicies.Paused,
            "改了政策：存檔的掛鉤帶出新的中繼資料（D25）");
    }

    [Fact]
    public async Task Notify_彙總成一則toast_0個不提示()
    {
        using var rig = new Rig();
        var mark = rig.H.Main.Mark();

        rig.Port.Notify(PublishNoticeKind.NotOwner, 100);
        rig.Port.Notify(PublishNoticeKind.Blocked, 0);
        await rig.H.Ui.DrainAsync();

        rig.H.Main.Messages("toast", mark).Select(m => m.Value<string>("text")).Should().Equal(PublishNoticeRules.Text(PublishNoticeKind.NotOwner, 100));
    }

    [Fact]
    public async Task ReportProgress_在UI執行緒更新_沒變不通知()
    {
        using var rig = new Rig();
        var changed = 0;
        rig.H.Ui.Invoke(() => rig.Publisher.ProgressChanged += () => changed++);
        var progress = new SyncBackfillProgress(3, 1_000);
        var pause = new BackfillPause(BackfillHold.LowDisk, 10, 0);

        rig.Port.ReportProgress(progress, pause);
        rig.Port.ReportProgress(progress with { }, pause with { });
        rig.Port.ReportProgress(null, null);
        await rig.H.Ui.DrainAsync();

        changed.Should().Be(2);
        rig.H.OnUi(() => rig.Publisher.Progress).Should().BeNull();
        rig.H.OnUi(() => rig.Publisher.Pause).Should().BeNull();
    }

    // ───── 掛上、釋放 ─────

    [Fact]
    public async Task 再掛一次_舊的被釋放_計時器不再放東西()
    {
        using var rig = new Rig();
        var (_, s) = rig.Started(rig.H.Claude);
        rig.H.Raise(s, new AgentTextDeltaEvent("b1", "還沒送"));
        rig.Time.ActiveTimers.Should().Be(1, "陽性對照：有待送的變化時排著計時器");

        var second = rig.H.OnUi(() => ConversationPublisher.TryCreate(rig.Source, rig.Engine, rig.Time))!;
        rig.H.Ui.Invoke(() => rig.H.Hub.AttachConversationSink(second));

        rig.Publisher.IsDisposed.Should().BeTrue("換掉的接收者被釋放");
        rig.Time.ActiveTimers.Should().Be(0, "舊的計時器停掉");
        rig.Advance(5_000);
        rig.Box.Count.Should().Be(0, "舊的發布者沒送的變化丟掉，不再放進盒子");
        rig.Box.UiPort.Should().BeSameAs(second, "舊的釋放時不清掉新的入口");

        rig.Port.RequestMeta(null);
        rig.Port.MarkServerDeleted("x");
        await rig.H.Ui.DrainAsync();
        rig.Box.Count.Should().Be(0, "釋放之後引擎經舊入口要的東西都不做");

        rig.H.Ui.Invoke(() => rig.H.Hub.AttachConversationSink(second));
        second.IsDisposed.Should().BeFalse("同一個再掛一次什麼都不做");
        rig.H.Ui.Invoke(() => rig.H.Hub.AttachConversationSink(null));
        second.IsDisposed.Should().BeTrue("拿掉時也釋放");
        rig.Box.UiPort.Should().BeNull();
    }

    private sealed class PlainEngine : ISyncEngine
    {
        public long HeartbeatsSent => 0;

        public void Start()
        {
        }

        public void Post(SyncSignal signal)
        {
        }

        public void PostLive(LiveRelay relay)
        {
        }

        public void SetPaused(bool paused)
        {
        }

        public void RetryNow()
        {
        }

        public void ConfirmHubIdentity(string fingerprint)
        {
        }

        public void UpdatePolicy(DevicePolicy policy)
        {
        }

        public Task StopAsync(TimeSpan timeout) => Task.CompletedTask;

        public void Cancel()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void 引擎不支援對話_TryCreate回null()
    {
        using var h = new HubHarness();
        var time = new ManualTimeProvider();

        h.OnUi(() => ConversationPublisher.TryCreate(h.Hub, new PlainEngine(), time)).Should().BeNull("不是 IConversationEngine");
        h.OnUi(() => ConversationPublisher.TryCreate(h.Hub, new RecordingSyncEngine(), time)).Should().BeNull("沒有盒子");

        var box = new ConversationChangeBox();
        var publisher = h.OnUi(() => ConversationPublisher.TryCreate(h.Hub, new RecordingSyncEngine { Conversations = box }, time));
        publisher.Should().NotBeNull();
        box.UiPort.Should().BeSameAs(publisher);
        box.ConversationsRoot.Should().Be(h.Store.RootDirectory);
        ConversationPublisher.RequiredDlpVersion.Should().Be(2, "宣告 conv1 的必要 DLP 版本（D38）");
    }

    [Fact]
    public void 掛上時盒子帶中樞的媒體快照入口_釋放時清掉_舊的不清新的()
    {
        using var h = new HubHarness();
        var time = new ManualTimeProvider();
        var box = new ConversationChangeBox();
        var engine = new RecordingSyncEngine { Conversations = box };

        // WP4.7：媒體轉送經盒子在 UI 執行緒問中樞（IMediaScopeSource），與發布者同生命週期
        var first = h.OnUi(() => ConversationPublisher.TryCreate(h.Hub, engine, time))!;
        box.MediaScope.Should().BeSameAs(h.Hub);
        var second = h.OnUi(() => ConversationPublisher.TryCreate(h.Hub, engine, time))!;
        h.Ui.Invoke(first.Dispose);
        box.MediaScope.Should().BeSameAs(h.Hub, "舊的釋放時不清掉新的入口");
        h.Ui.Invoke(second.Dispose);
        box.MediaScope.Should().BeNull();
        box.UiPort.Should().BeNull();
    }

    [Fact]
    public async Task 正式的引擎工廠_帶對話盒子_發布者掛得上()
    {
        using var h = new HubHarness();
        // 只建物件、不 Start（不開 sync.db、不連線）
        var engine = SyncEngineLoader.Create(new SyncEngineCreateArgs(
            new SyncPaths(h.Temp.Combine("sync")),
            PairingTestData.Device(),
            new FakeHostPort(h.Ui, TimeProvider.System),
            new RecordingApplier(h.Ui),
            new DevicePolicy("none", false),
            DevTrustEnv: false,
            ServerOverride: null,
            "test",
            MachineName: null,
            WindowsUser: null));
        try
        {
            engine.Should().BeAssignableTo<IConversationEngine>().Which.Conversations.Should().NotBeNull("正式的工廠一律帶盒子（D3），協調者才會掛上發布者");
            var publisher = h.OnUi(() => ConversationPublisher.TryCreate(h.Hub, engine, new ManualTimeProvider()));
            publisher.Should().NotBeNull();
            h.Ui.Invoke(publisher!.Dispose);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }
}
