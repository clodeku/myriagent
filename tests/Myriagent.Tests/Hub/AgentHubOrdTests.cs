using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 中樞配發與保留 Ord（WP4.3 計畫步 5，D8、D9）：五個條件都成立才配、先保留後配、64 一段、sync-ord.json 旁檔先寫，
/// 當機與舊版剝除之後都不重用；未配對（沒掛接收者）、草稿、已刪除與複本的對話不寫任何檔（S6）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubOrdTests
{
    /// <summary>只記 OnItemChanged 呼叫當下項目的 Ord 的接收者（其他什麼都不做；主要是讓中樞看到「掛著接收者」）</summary>
    private sealed class NullSink : IConversationChangeSink
    {
        public Recorder<(string ItemId, long? Ord)> Changed { get; } = new();

        public void OnItemChanged(ConversationHookInfo conversation, AgentChatItem item) => Changed.Add((item.Id, item.Ord));

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

    private static NullSink Attach(HubHarness h)
    {
        var sink = new NullSink();
        h.Ui.Invoke(() => h.Hub.AttachConversationSink(sink));
        return sink;
    }

    private static bool Prepare(HubHarness h, string id, OrdHint? hint = null) => h.OnUi(() => h.Hub.PrepareOrdsForPublish(id, hint));

    /// <summary>存一個只有 meta.json 的對話（還沒有任何項目）</summary>
    private static AgentConversation Seeded(string title = "上次的對話") => new()
    {
        AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = title, SessionId = Guid.NewGuid().ToString(), SessionStarted = true,
    };

    private static string Dir(HubHarness h, string id) => h.Store.GetConversationDirectory(id);

    /// <summary>磁碟上的保留旁檔（沒有是 null）</summary>
    private static long? Sidecar(HubHarness h, string id)
    {
        var path = Path.Combine(Dir(h, id), ConversationStore.OrdFileName);
        return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)).Value<long>("reserved") : null;
    }

    /// <summary>磁碟上 meta.json 的 NextOrd</summary>
    private static long? MetaNextOrd(HubHarness h, string id)
        => JObject.Parse(File.ReadAllText(Path.Combine(Dir(h, id), "meta.json")))["NextOrd"]?.Value<long?>();

    /// <summary>對話資料夾裡所有檔案的相對路徑（排序）</summary>
    private static List<string> Files(HubHarness h, string id)
    {
        var dir = Dir(h, id);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(dir, p)).Order(StringComparer.Ordinal).ToList()
            : new List<string>();
    }

    private static void Notices(HubHarness h, FakeSession session, int count, string prefix = "提示")
    {
        for (var i = 0; i < count; i++)
            h.Raise(session, new AgentNoticeEvent($"{prefix} {i}", false));
    }

    private static List<long?> Ords(HubHarness h, string id) => h.Items(id).Select(i => i.Ord).ToList();

    private static FakeSession SendToSeeded(HubHarness h, string id, string text)
    {
        var before = h.Sessions.Count;
        h.Send(h.Main, id, text);
        h.Sessions.Count.Should().Be(before + 1, "送出時建立會話");
        return h.Sessions[^1];
    }

    [Fact]
    public void 未掛接收者_新增項目不配Ord_不寫旁檔()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        var before = Files(h, id);

        h.Send(h.Main, id, "第二則");
        Notices(h, s, 3);
        h.CompleteTurn(s);

        Prepare(h, id).Should().BeFalse("沒掛接收者（未配對）不準備");
        Ords(h, id).Should().OnlyContain(o => o == null, "未配對時不配 Ord");
        Files(h, id).Should().Equal(before, "S6：資料夾的檔案清單前後相同");
        Sidecar(h, id).Should().BeNull();
        MetaNextOrd(h, id).Should().BeNull();
    }

    [Fact]
    public void 沒有準備過_AddItem不配()
    {
        using var h = new HubHarness();
        Attach(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        Notices(h, s, 3);
        h.CompleteTurn(s);

        Ords(h, id).Should().OnlyContain(o => o == null, "OrdsReady 為 false 時不配（引擎要快照時才準備）");
        Sidecar(h, id).Should().BeNull("沒準備過不寫旁檔");
        MetaNextOrd(h, id).Should().BeNull();
    }

    [Fact]
    public void 準備後依新增順序配_第一次保留64()
    {
        var conversation = Seeded();
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var id = conversation.Id;
        Attach(h);

        Prepare(h, id).Should().BeTrue();
        Sidecar(h, id).Should().Be(65, "第一次保留 1～64");
        MetaNextOrd(h, id).Should().Be(65);

        var s = SendToSeeded(h, id, "第一則");
        Notices(h, s, 3);

        Ords(h, id).Should().Equal(1L, 2L, 3L, 4L);
        h.Items(id).Select(i => i.Kind).Should().Equal(AgentItemKind.User, AgentItemKind.Notice, AgentItemKind.Notice, AgentItemKind.Notice);
        Sidecar(h, id).Should().Be(65, "還沒用完這一段，不再保留");
    }

    [Fact]
    public void 新增的項目_接收者第一次看到時已有Ord()
    {
        var conversation = Seeded();
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var id = conversation.Id;
        var sink = Attach(h);
        Prepare(h, id).Should().BeTrue();

        var s = SendToSeeded(h, id, "第一則");
        Notices(h, s, 2);

        var items = h.Items(id);
        items.Should().HaveCount(3);
        foreach (var item in items)
            sink.Changed.All.First(c => c.ItemId == item.Id).Ord.Should().Be(item.Ord, "AddItem 先配 Ord 再呼叫掛鉤（接收者不必再等下一次變化）");
    }

    [Fact]
    public async Task 旁檔先寫()
    {
        var conversation = Seeded();
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var id = conversation.Id;
        Attach(h);
        Prepare(h, id).Should().BeTrue();
        var s = SendToSeeded(h, id, "第一則");
        Notices(h, s, 63);
        Ords(h, id).Should().Equal(Enumerable.Range(1, 64).Select(i => (long?)i));

        // 寫好旁檔之後當掉（meta.json 還沒改）：這一段沒保留成功，這一筆不配
        h.Store.FaultInjection = point =>
        {
            if (point == "SaveOrdReservation:sidecar")
                throw new IOException("模擬寫好旁檔之後當掉");
        };
        Notices(h, s, 1, "保留失敗時新增");
        h.Store.FaultInjection = null;

        h.Items(id)[^1].Ord.Should().BeNull("保留沒成功就不配（先保留、後配）");
        Sidecar(h, id).Should().Be(129, "旁檔已經寫好");
        MetaNextOrd(h, id).Should().Be(65, "meta.json 在旁檔之後才寫：保留值只在旁檔");

        // 保留失敗之後回到「沒準備過」：之後新增的也先不配，下次準備時依清單順序一起補（不會出現後面的項目 Ord 比較小）
        Notices(h, s, 1, "保留恢復之後");
        h.Items(id)[^1].Ord.Should().BeNull("要重新準備才配");

        h.CompleteTurn(s);
        await h.RestartAsync();
        Attach(h);
        Prepare(h, id).Should().BeTrue();

        var ords = Ords(h, id);
        ords.Take(64).Should().Equal(Enumerable.Range(1, 64).Select(i => (long?)i), "已配的不變");
        ords[64].Should().BeGreaterThanOrEqualTo(129, "重新載入後保留值來自旁檔，之後配的不重用 1～128");
        ords.Should().OnlyHaveUniqueItems().And.BeInAscendingOrder("清單順序上遞增");
    }

    [Fact]
    public void 保留失敗_準備回false_不配()
    {
        var conversation = Seeded();
        using var h = new HubHarness(seedStore: store =>
        {
            store.SaveConversation(conversation);
            store.SaveItems(conversation.Id, new[]
            {
                new AgentChatItem { Kind = AgentItemKind.User, Text = "舊的一則" },
                new AgentChatItem { Kind = AgentItemKind.Assistant, Text = "舊的回覆", Status = AgentItemStatus.Done },
            });
        });
        var id = conversation.Id;
        Attach(h);

        h.Store.FaultInjection = point =>
        {
            if (point == "SaveOrdReservation:sidecar")
                throw new IOException("模擬保留失敗");
        };
        Prepare(h, id).Should().BeFalse("保留沒成功，這次的快照不能用");
        Ords(h, id).Should().OnlyContain(o => o == null, "先保留、後配：保留失敗時一個都不配");
        MetaNextOrd(h, id).Should().BeNull();

        Sidecar(h, id).Should().Be(67, "旁檔已經寫好：1～66 當成可能已配出去");

        h.Store.FaultInjection = null;
        Prepare(h, id).Should().BeTrue();
        Ords(h, id).Should().Equal(67L, 68L);
        Sidecar(h, id).Should().Be(133);
        MetaNextOrd(h, id).Should().Be(133);
    }

    [Fact]
    public void 重開_從保留上限接著配_沒有重用()
    {
        var conversation = Seeded();
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var id = conversation.Id;
        Attach(h);
        Prepare(h, id).Should().BeTrue();
        var s = SendToSeeded(h, id, "第一則");

        // 每配一個都已經在旁檔的保留範圍內（當機時不會重用）
        for (var i = 0; i < 69; i++)
        {
            Notices(h, s, 1);
            var last = h.Items(id)[^1].Ord;
            last.Should().NotBeNull();
            last!.Value.Should().BeLessThan(Sidecar(h, id)!.Value, "配出去的 Ord 一定小於已寫進旁檔的保留上限");
        }
        var seen = Ords(h, id);
        seen.Should().Equal(Enumerable.Range(1, 70).Select(i => (long?)i));
        var reserved = Sidecar(h, id)!.Value;

        // 不存 items.json 就當掉：以同一份存檔另建一個中樞
        var sessions = new List<FakeSession>();
        var view = new FakeView("crash");
        var hub2 = h.Ui.Invoke(() =>
        {
            var hub = new AgentHub(h.Store, h.Host, h.Accounts, h.Cli, h.Settings, provider =>
            {
                var session = new FakeSession(provider);
                sessions.Add(session);
                return session;
            }, File.Exists);
            hub.AttachView(view);
            hub.AttachConversationSink(new NullSink());
            return hub;
        });
        try
        {
            h.Ui.Invoke(() => hub2.PrepareOrdsForPublish(id, null)).Should().BeTrue();
            h.Ui.Invoke(() => hub2.HandleCommand(view, HubHarness.MakeCommand("send", new { conversationId = id, text = "重開之後" })));
            h.Ui.Invoke(() => sessions.Single().Raise(new AgentNoticeEvent("重開之後的提示", false)));

            var after = h.Ui.Invoke(() => hub2.GetItems(id).Select(i => i.Ord).ToList());
            var fresh = after.Skip(after.Count - 2).ToList();
            fresh.Should().OnlyContain(o => o >= reserved, "從保留上限之後接著配");
            fresh.Intersect(seen).Should().BeEmpty("當機前配過的不重用");
            after.Should().OnlyHaveUniqueItems();
        }
        finally
        {
            h.Ui.Invoke(hub2.Dispose);
        }
    }

    [Fact]
    public async Task 舊版剝掉Ord與NextOrd_hint沿用原本的Ord()
    {
        var conversation = Seeded();
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var id = conversation.Id;
        Attach(h);
        Prepare(h, id).Should().BeTrue();
        var s = SendToSeeded(h, id, "第一則");
        Notices(h, s, 4);
        h.CompleteTurn(s);
        var original = h.Items(id).ToDictionary(i => i.Id, i => i.Ord!.Value);
        original.Values.Should().OnlyHaveUniqueItems().And.HaveCountGreaterThanOrEqualTo(5);

        // 舊版讀進來再存回：items.json 的 Ord 與 meta.json 的 NextOrd 都被剝掉（旁檔它不認得、不碰）
        var dir = Dir(h, id);
        var itemsPath = Path.Combine(dir, "items.json");
        var items = JArray.Parse(File.ReadAllText(itemsPath));
        foreach (var item in items.Cast<JObject>())
            item.Remove("Ord");
        File.WriteAllText(itemsPath, items.ToString());
        var metaPath = Path.Combine(dir, "meta.json");
        var meta = JObject.Parse(File.ReadAllText(metaPath));
        meta.Remove("NextOrd").Should().BeTrue();
        File.WriteAllText(metaPath, meta.ToString());

        await h.RestartAsync();
        Attach(h);
        h.Items(id).Should().OnlyContain(i => i.Ord == null, "陽性對照：載入的項目確實沒有 Ord");
        Prepare(h, id, new OrdHint(original, original.Values.Max())).Should().BeTrue();

        h.Items(id).ToDictionary(i => i.Id, i => i.Ord!.Value).Should().Equal(original, "published_items 記得的 Ord 照用");
        Sidecar(h, id).Should().BeGreaterThan(original.Values.Max());
    }

    [Fact]
    public void 讀不到期間加的項目與損毀時的Error項目_準備後依清單順序補配()
    {
        var corrupt = Seeded("紀錄檔損毀");
        var locked = Seeded("紀錄檔被占用");
        using var h = new HubHarness(seedStore: store =>
        {
            store.SaveConversation(corrupt);
            File.WriteAllText(Path.Combine(store.GetConversationDirectory(corrupt.Id), "items.json"), "[{\"Kind\":\"User\",\"Text\":\"斷電時寫到一半");
            store.SaveConversation(locked);
            store.SaveItems(locked.Id, new[]
            {
                new AgentChatItem { Kind = AgentItemKind.User, Text = "舊的一則" },
                new AgentChatItem { Kind = AgentItemKind.Assistant, Text = "舊的回覆", Status = AgentItemStatus.Done },
            });
        });
        Attach(h);

        // 損毀：載入時另加一筆沒有 Ord 的 Error 項目，準備時補配
        Prepare(h, corrupt.Id).Should().BeTrue();
        h.Items(corrupt.Id).Select(i => (i.Kind, i.Ord)).Should().Equal((AgentItemKind.Error, (long?)1));

        // 讀不到的期間換帳戶：提示先放在暫時清單（沒有 Ord），讀到之後接在載入的內容後面
        using (new FileStream(Path.Combine(Dir(h, locked.Id), "items.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Prepare(h, locked.Id).Should().BeFalse("項目讀不到時不準備");
            h.Command(h.Main, "setAccount", new { conversationId = locked.Id, accountId = h.Claude2.Id });
            Ords(h, locked.Id).Should().ContainSingle().Which.Should().BeNull("讀不到的期間加的項目沒有 Ord");
        }

        Prepare(h, locked.Id).Should().BeTrue();
        h.Items(locked.Id).Select(i => i.Kind).Should().Equal(AgentItemKind.User, AgentItemKind.Assistant, AgentItemKind.Notice);
        Ords(h, locked.Id).Should().Equal(1L, 2L, 3L);

        var session = SendToSeeded(h, locked.Id, "讀得到之後送的");
        h.CompleteTurn(session);
        Ords(h, locked.Id).Take(4).Should().Equal(1L, 2L, 3L, 4L);
        h.Store.LoadItems(locked.Id).Select(i => i.Ord).Should().Equal(Ords(h, locked.Id), "補配的 Ord 隨下一次存檔寫進 items.json");
    }

    [Fact]
    public async Task 草稿與已刪除的對話_準備Ord不建資料夾不寫旁檔()
    {
        using var h = new HubHarness();
        Attach(h);

        var draft = h.NewConversation(h.Main, h.Claude);
        Prepare(h, draft).Should().BeFalse("草稿不準備");
        Directory.Exists(Dir(h, draft)).Should().BeFalse("草稿不建資料夾");

        // 第二道：草稿的資料夾裡萬一有 meta.json（例如殘留），照樣不寫旁檔
        h.Store.SaveConversation(new AgentConversation { Id = draft, AccountId = h.Claude.Id, Provider = AgentProvider.Claude, Title = "殘留" });
        Prepare(h, draft).Should().BeFalse("草稿不準備");
        Sidecar(h, draft).Should().BeNull("草稿不寫旁檔");

        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        h.Command(h.Main, "delete", new { conversationId = id });
        await h.WaitUntilAsync(() => !Directory.Exists(Dir(h, id)), "對話資料夾刪掉");

        Prepare(h, id).Should().BeFalse("已刪除的對話不準備");
        h.Store.TrySaveOrdReservation(id, 65).Should().BeFalse("meta.json 不存在");
        Directory.Exists(Dir(h, id)).Should().BeFalse("已刪除的對話不會多出只有旁檔的資料夾");
    }

    [Fact]
    public void 複本_不配不寫()
    {
        var replica = Seeded("其他電腦的對話");
        var local = Seeded("本機的對話");
        using var h = new HubHarness(seedStore: store =>
        {
            store.SaveConversation(replica);
            store.SaveConversation(local);
        });
        Attach(h);

        h.OnUi(() =>
        {
            h.Hub.MarkReplicaForTest(replica.Id);
            return 0;
        });
        Prepare(h, replica.Id).Should().BeFalse("複本不準備");
        Sidecar(h, replica.Id).Should().BeNull("複本不寫旁檔");
        MetaNextOrd(h, replica.Id).Should().BeNull();

        // 準備過之後才變成複本（第二道）：之後新增的項目不配
        Prepare(h, local.Id).Should().BeTrue();
        var s = SendToSeeded(h, local.Id, "第一則");
        h.OnUi(() =>
        {
            h.Hub.MarkReplicaForTest(local.Id);
            return 0;
        });
        Notices(h, s, 1);
        Ords(h, local.Id).Should().Equal(new long?[] { 1, null }, "複本不配");
    }

    [Fact]
    public void 拿掉接收者再掛回_要重新準備_清單順序的Ord遞增()
    {
        var conversation = Seeded();
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        var id = conversation.Id;
        Attach(h);
        Prepare(h, id).Should().BeTrue();
        var s = SendToSeeded(h, id, "第一則");

        h.Ui.Invoke(() => h.Hub.AttachConversationSink(null));
        Notices(h, s, 1, "解除配對期間");
        Attach(h);
        Notices(h, s, 1, "重新配對之後");

        Ords(h, id).Should().Equal(new long?[] { 1, null, null }, "換了接收者要重新準備，才不會讓前面沒有 Ord 的項目之後配到比較大的值");
        Prepare(h, id).Should().BeTrue();
        var ords = Ords(h, id).Select(o => o!.Value).ToList();
        ords.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        ords[0].Should().Be(1);
    }
}
