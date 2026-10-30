using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>對話存檔：conversations\{對話 Id}\（meta.json、items.json），不再依使用者分資料夾</summary>
public sealed class ConversationStoreTests : IDisposable
{
    private readonly TempDirectory temp = new("store");

    public void Dispose() => temp.Dispose();

    [Fact]
    public void 存檔與讀回_對話資料與項目來回一致_新到舊排序()
    {
        var store = new ConversationStore(temp.Root);
        var older = new AgentConversation { Title = "舊", AccountId = "a2", Provider = AgentProvider.Codex, UpdatedAt = new DateTime(2026, 10, 1) };
        var newer = new AgentConversation
        {
            Title = "新", AccountId = "a1", Provider = AgentProvider.Claude, SessionId = "s-1", SessionStarted = true, SessionAccountId = "a1",
            PermissionMode = AgentPermissionMode.AcceptEdits, Model = "opus", Effort = "high", WorkingDirectory = @"C:\repo", UpdatedAt = new DateTime(2026, 10, 8),
        };
        store.SaveConversation(older);
        store.SaveConversation(newer);
        store.SaveItems(newer.Id, new[]
        {
            new AgentChatItem { Kind = AgentItemKind.User, Text = "你好", Attachments = { new AgentAttachment { FileName = "a.png", Kind = AgentAttachmentKind.Image, ModelImagePaths = { @"C:\x\a.jpg" } } } },
            new AgentChatItem { Kind = AgentItemKind.Question, Status = AgentItemStatus.Done, Questions = { new AgentQuestion { Id = "q", Text = "q", Options = { new AgentQuestionOption { Label = "A" } } } }, Answers = { ["q"] = new List<string> { "A" } }, RequestKey = "不存檔" },
            new AgentChatItem { Kind = AgentItemKind.Tool, ToolKind = "edit", FilePath = @"C:\repo\a.cs", Diff = new List<AgentDiffLine> { new("del", "a"), new("add", "b") } },
            new AgentChatItem { Kind = AgentItemKind.Plan, PlanSteps = { new AgentPlanStep { Text = "讀取", Status = "completed" } } },
        });

        var reopened = new ConversationStore(temp.Root);
        reopened.LoadConversations().Select(c => c.Title).Should().Equal("新", "舊");
        var meta = reopened.LoadConversations()[0];
        meta.AccountId.Should().Be("a1");
        meta.Provider.Should().Be(AgentProvider.Claude);
        meta.SessionStarted.Should().BeTrue();
        meta.SessionAccountId.Should().Be("a1");
        meta.PermissionMode.Should().Be(AgentPermissionMode.AcceptEdits);
        meta.Model.Should().Be("opus");
        meta.WorkingDirectory.Should().Be(@"C:\repo");

        var items = reopened.LoadItems(newer.Id);
        items.Should().HaveCount(4);
        items[0].Attachments.Single().ModelImagePaths.Should().Equal(@"C:\x\a.jpg");
        items[1].Answers["q"].Should().Equal("A");
        items[1].RequestKey.Should().BeNull("請求鍵只在執行中有效，不寫進檔案");
        items[2].Diff.Should().Equal(new AgentDiffLine("del", "a"), new AgentDiffLine("add", "b"));
        items[3].PlanSteps.Single().Status.Should().Be("completed");

        var json = File.ReadAllText(Path.Combine(temp.Root, newer.Id, "meta.json"));
        json.Should().Contain("\"Provider\": \"Claude\"", "列舉存成文字，日後增減列舉值不會錯位");
        json.Should().Contain("\"PermissionMode\": \"AcceptEdits\"");
        reopened.LoadItems(older.Id).Should().BeEmpty("沒有 items.json 就是空的");
    }

    [Fact]
    public void 壞掉或Id不符的Meta略過_刪除整個資料夾()
    {
        var store = new ConversationStore(temp.Root);
        var good = new AgentConversation { Title = "好" };
        store.SaveConversation(good);
        var attachments = store.GetAttachmentDirectory(good.Id);
        Directory.CreateDirectory(attachments);
        File.WriteAllText(Path.Combine(attachments, "a.txt"), "x");

        var badDir = Path.Combine(temp.Root, "bad");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "meta.json"), "{不是 JSON");
        var mismatched = new AgentConversation { Title = "搬錯資料夾" };
        Directory.CreateDirectory(Path.Combine(temp.Root, "other"));
        File.WriteAllText(Path.Combine(temp.Root, "other", "meta.json"), Newtonsoft.Json.JsonConvert.SerializeObject(mismatched));
        Directory.CreateDirectory(Path.Combine(temp.Root, "empty"));

        store.LoadConversations().Select(c => c.Title).Should().Equal("好");
        store.DeleteConversation(good.Id).Should().BeTrue();
        Directory.Exists(Path.Combine(temp.Root, good.Id)).Should().BeFalse("附件與工作資料夾一起刪");
        store.LoadConversations().Should().BeEmpty();
        store.DeleteConversation("never-existed").Should().BeTrue("本來就沒有也算刪乾淨");
    }

    [Fact]
    public void 損毀的Meta_紀錄還在時以資料夾名稱重建並存回_提示使用者()
    {
        var store = new ConversationStore(temp.Root);
        var original = new AgentConversation { Title = "修正登入流程", AccountId = "a1", Provider = AgentProvider.Codex };
        store.SaveConversation(original);
        store.SaveItems(original.Id, new[]
        {
            new AgentChatItem { Kind = AgentItemKind.Notice, Text = "提示" },
            new AgentChatItem { Kind = AgentItemKind.User, Text = "幫我修正登入流程\n第二行" },
        });
        var metaPath = Path.Combine(temp.Root, original.Id, "meta.json");
        File.WriteAllText(metaPath, "{\"Id\":\"" + original.Id + "\",\"Title\":\"斷電時寫到一半");

        var loaded = store.LoadConversations();

        var rebuilt = loaded.Should().ContainSingle("meta 壞了但紀錄還在，對話不能從清單消失").Subject;
        rebuilt.Id.Should().Be(original.Id);
        rebuilt.Title.Should().Be("幫我修正登入流程", "標題取第一則使用者訊息");
        rebuilt.AccountId.Should().BeEmpty("帳戶不明：畫面標示帳戶已移除，換帳戶後以前文節錄續接");
        rebuilt.SessionStarted.Should().BeFalse();
        store.StartupWarning.Should().Contain("已損毀").And.Contain("還原");
        Directory.GetFiles(Path.Combine(temp.Root, original.Id), "meta.json.corrupt-*").Should().ContainSingle("原檔備份保留");
        store.LoadItems(original.Id).Should().HaveCount(2, "紀錄不受影響");

        var reopened = new ConversationStore(temp.Root);
        reopened.LoadConversations().Single().Title.Should().Be("幫我修正登入流程", "重建的 meta 已存回，下次啟動照常讀到");
        reopened.StartupWarning.Should().BeNull();
    }

    [Fact]
    public void 讀不到的Meta_這次略過但原檔不動_提示使用者()
    {
        var store = new ConversationStore(temp.Root);
        var conversation = new AgentConversation { Title = "被占用" };
        store.SaveConversation(conversation);
        var metaPath = Path.Combine(temp.Root, conversation.Id, "meta.json");
        var before = File.ReadAllBytes(metaPath);

        using (new FileStream(metaPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            store.LoadConversations().Should().BeEmpty();
            store.StartupWarning.Should().Contain("暫時讀不到");
        }

        File.ReadAllBytes(metaPath).Should().Equal(before);
        store.LoadConversations().Single().Title.Should().Be("被占用");
        store.StartupWarning.Should().BeNull();
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789ABCDEF", true)]
    [InlineData("bad", false)]
    [InlineData("0123456789abcdef0123456789abcdeg", false)]
    [InlineData(null, false)]
    public void 只有對話Id格式的資料夾才還原(string? name, bool expected)
        => ConversationStoreRules.IsConversationFolderName(name).Should().Be(expected);

    [Fact]
    public void 重建Meta_沒有使用者訊息時用預設標題_時間取紀錄檔的修改時間()
    {
        var time = new DateTime(2026, 10, 9, 21, 30, 0);
        var meta = ConversationStoreRules.RebuildMeta("0123456789abcdef0123456789abcdef", null, time);
        meta.Title.Should().Be(ConversationStoreRules.RecoveredTitle);
        meta.CreatedAt.Should().Be(time);
        meta.UpdatedAt.Should().Be(time);
        meta.WorkingDirectory.Should().BeNull("回到對話自己的 workspace");
        ConversationStoreRules.RebuildMeta("0123456789abcdef0123456789abcdef", "  \n  第一行標題  ", time).Title.Should().Be("第一行標題");
    }

    [Fact]
    public void 重建Meta_同步政策不明_取最嚴格的localOnly()
    {
        var meta = ConversationStoreRules.RebuildMeta("0123456789abcdef0123456789abcdef", "第一行", new DateTime(2026, 10, 9));

        meta.SyncPolicy.Should().Be(Myriagent.Sync.ConversationSyncPolicies.LocalOnly, "原本可能是 localOnly，不能因為 meta.json 壞掉就被當成 full 發布");
        Myriagent.Sync.ConversationSyncPolicies.Interpret(meta.SyncPolicy).Should().Be("localOnly");
    }

    // ───────────────────────────── 同步政策的旁檔（合併前審查 persist/P1） ─────────────────────────────

    /// <summary>WP4.1 之前（5ffa49b）的 AgentConversation：沒有 SyncPolicy、沒有 JsonExtensionData——讀進來再存回就刪掉不認得的欄位</summary>
    private sealed class LegacyConversation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Title { get; set; } = "新對話";

        public string AccountId { get; set; } = string.Empty;

        public AgentProvider Provider { get; set; }

        public string? SessionId { get; set; }

        public bool SessionStarted { get; set; }

        public string? SessionAccountId { get; set; }

        public string? Model { get; set; }

        public string? Effort { get; set; }

        public AgentPermissionMode PermissionMode { get; set; } = AgentPermissionMode.Ask;

        public string? WorkingDirectory { get; set; }

        public string? LastCliVersion { get; set; }

        public string? RemoteControlSessionId { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    private static string? SidecarPolicy(string dir)
    {
        var path = Path.Combine(dir, ConversationStore.SyncPolicyFileName);
        return File.Exists(path) ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path))["policy"]?.ToString() : null;
    }

    [Fact]
    public void 同步政策_舊版讀進來再存回刪掉了SyncPolicy_旁檔保住_載入時補回()
    {
        var store = new ConversationStore(temp.Root);
        var conversation = new AgentConversation { Title = "機密專案", AccountId = "a1", Provider = AgentProvider.Claude, SyncPolicy = Myriagent.Sync.ConversationSyncPolicies.LocalOnly };
        store.SaveConversation(conversation);
        var dir = store.GetConversationDirectory(conversation.Id);
        SidecarPolicy(dir).Should().Be("localOnly");

        // 退回 WP4.1 之前的版本：照它的模型讀進來、改名、整份存回
        var metaPath = Path.Combine(dir, "meta.json");
        var legacy = Myriagent.Core.JsonFile.Read<LegacyConversation>(metaPath)!;
        legacy.Title = "舊版改的名";
        Myriagent.Core.JsonFile.Write(metaPath, legacy);
        File.ReadAllText(metaPath).Should().NotContain("SyncPolicy", "舊版存回時刪掉了不認得的欄位");

        var loaded = new ConversationStore(temp.Root).LoadConversations().Single();

        loaded.Title.Should().Be("舊版改的名");
        loaded.SyncPolicy.Should().Be("localOnly", "旁檔比 meta.json 的 null（＝full）嚴格");
        Myriagent.Sync.SyncProviderRules.ConversationPolicy(loaded).Should().Be("localOnly");
    }

    [Fact]
    public void 同步政策_旁檔只在不是full時存在_改回full刪掉_null不動()
    {
        var store = new ConversationStore(temp.Root);
        var conversation = new AgentConversation { Title = "x", AccountId = "a1", Provider = AgentProvider.Codex };
        var dir = store.GetConversationDirectory(conversation.Id);

        store.SaveConversation(conversation);
        SidecarPolicy(dir).Should().BeNull("沒設過（null＝full）不寫旁檔：既有的對話資料夾不多任何檔案");

        foreach (var policy in new[] { "metadata", "paused", "localOnly", "未來的新值" })
        {
            conversation.SyncPolicy = policy;
            store.SaveConversation(conversation);
            SidecarPolicy(dir).Should().Be(policy, "原字串照存（不認得的值解讀成 localOnly）");
        }

        conversation.SyncPolicy = "full";
        store.SaveConversation(conversation);
        SidecarPolicy(dir).Should().BeNull("改回 full 刪掉旁檔");
        new ConversationStore(temp.Root).LoadConversations().Single().SyncPolicy.Should().Be("full");

        conversation.SyncPolicy = "metadata";
        store.SaveConversation(conversation);
        conversation.SyncPolicy = null;
        store.SaveConversation(conversation);
        SidecarPolicy(dir).Should().Be("metadata", "null 可能是舊版存回時刪掉的，不動旁檔");
        new ConversationStore(temp.Root).LoadConversations().Single().SyncPolicy.Should().Be("metadata");
    }

    [Theory]
    [InlineData("paused", "metadata", "paused")]
    [InlineData("metadata", "localOnly", "localOnly")]
    [InlineData("full", "paused", "paused")]
    [InlineData("localOnly", "paused", "localOnly")]
    [InlineData(null, "未來的新值", "未來的新值")]
    public void 同步政策_兩邊不一致時取較嚴格的(string? meta, string sidecar, string expected)
    {
        var store = new ConversationStore(temp.Root);
        var conversation = new AgentConversation { Title = "x", AccountId = "a1", SyncPolicy = meta };
        store.SaveConversation(conversation);
        File.WriteAllText(Path.Combine(store.GetConversationDirectory(conversation.Id), ConversationStore.SyncPolicyFileName), "{\"policy\":\"" + sidecar + "\"}");

        new ConversationStore(temp.Root).LoadConversations().Single().SyncPolicy.Should().Be(expected);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"policy\":\"\"}")]
    [InlineData("{}")]
    [InlineData("null")]
    public void 同步政策_旁檔損毀或是空的_當成localOnly(string content)
    {
        var store = new ConversationStore(temp.Root);
        var conversation = new AgentConversation { Title = "x", AccountId = "a1", SyncPolicy = "full" };
        store.SaveConversation(conversation);
        File.WriteAllText(Path.Combine(store.GetConversationDirectory(conversation.Id), ConversationStore.SyncPolicyFileName), content);

        new ConversationStore(temp.Root).LoadConversations().Single().SyncPolicy.Should().Be("localOnly", "fail-closed");
    }

    [Fact]
    public void 重建Meta_有旁檔時用旁檔的政策()
    {
        var store = new ConversationStore(temp.Root);
        var original = new AgentConversation { Title = "修正登入流程", AccountId = "a1", SyncPolicy = "metadata" };
        store.SaveConversation(original);
        store.SaveItems(original.Id, new[] { new AgentChatItem { Kind = AgentItemKind.User, Text = "幫我修正登入流程" } });
        File.WriteAllText(Path.Combine(temp.Root, original.Id, "meta.json"), "{\"Id\":\"" + original.Id + "\",\"Title\":\"斷電");

        store.LoadConversations().Single().SyncPolicy.Should().Be("metadata");
        ConversationStoreRules.RebuildMeta(original.Id, "x", DateTime.Now, "paused").SyncPolicy.Should().Be("paused");
        ConversationStoreRules.RebuildMeta(original.Id, "x", DateTime.Now, null).SyncPolicy.Should().Be("localOnly", "沒有旁檔：不明，取最嚴格的");
    }

    [Fact]
    public void 同步政策的規則_嚴格度_合併_要不要旁檔()
    {
        Myriagent.Sync.ConversationSyncPolicies.Strictness(null).Should().Be(0);
        Myriagent.Sync.ConversationSyncPolicies.Strictness("full").Should().Be(0);
        Myriagent.Sync.ConversationSyncPolicies.Strictness("metadata").Should().Be(1);
        Myriagent.Sync.ConversationSyncPolicies.Strictness("paused").Should().Be(2);
        Myriagent.Sync.ConversationSyncPolicies.Strictness("localOnly").Should().Be(3);
        Myriagent.Sync.ConversationSyncPolicies.Strictness("Full").Should().Be(3, "不認得的（含大小寫不同）是 localOnly");

        ConversationStoreRules.MergeSyncPolicy("metadata", null).Should().Be("metadata", "沒有旁檔不改");
        ConversationStoreRules.MergeSyncPolicy(null, "paused").Should().Be("paused");
        ConversationStoreRules.MergeSyncPolicy("localOnly", "metadata").Should().Be("localOnly");
        ConversationStoreRules.MergeSyncPolicy("paused", "paused").Should().Be("paused");

        ConversationStoreRules.NeedsSyncPolicySidecar(null).Should().BeFalse();
        ConversationStoreRules.NeedsSyncPolicySidecar("full").Should().BeFalse();
        ConversationStoreRules.NeedsSyncPolicySidecar("metadata").Should().BeTrue();
        ConversationStoreRules.NeedsSyncPolicySidecar("Full").Should().BeTrue("不認得的值照存");
    }

    [Fact]
    public void 草稿殘留的清理_有旁檔的資料夾不算草稿()
    {
        var store = new ConversationStore(temp.Root);
        var id = Guid.NewGuid().ToString("N");
        var dir = store.GetConversationDirectory(id);
        Directory.CreateDirectory(Path.Combine(dir, "attachments"));
        Directory.CreateDirectory(Path.Combine(dir, "workspace"));
        store.FindDraftLeftovers().Should().Equal(id);

        File.WriteAllText(Path.Combine(dir, ConversationStore.SyncPolicyFileName), "{\"policy\":\"localOnly\"}");
        store.FindDraftLeftovers().Should().BeEmpty("存過 meta.json 的對話刪到一半留下的：不清理");
    }

    // ───────────────────────────── Ord 保留的旁檔（WP4.3 計畫步 5，D9） ─────────────────────────────

    [Theory]
    [InlineData("{")]
    [InlineData("{\"reserved\":0}")]
    [InlineData("{\"reserved\":-3}")]
    [InlineData("{\"reserved\":\"65\"}")]
    [InlineData("{\"reserved\":1.5}")]
    [InlineData("{\"reserved\":99999999999999999999999}")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[65]")]
    public void Ord旁檔_沒有是null_損毀回Corrupt(string content)
    {
        var store = new ConversationStore(temp.Root);
        var conversation = new AgentConversation { Title = "x", AccountId = "a1" };
        store.SaveConversation(conversation);
        var dir = store.GetConversationDirectory(conversation.Id);

        store.ReadOrdReservation(conversation.Id).Should().Be(new Myriagent.Sync.OrdReservation(null, false), "沒有旁檔");

        store.TrySaveOrdReservation(conversation.Id, 65).Should().BeTrue();
        store.ReadOrdReservation(conversation.Id).Should().Be(new Myriagent.Sync.OrdReservation(65, false));
        ConversationStore.ReadOrdReservationFile(dir).Should().Be(new Myriagent.Sync.OrdReservation(65, false), "執行緒集區用的靜態版本讀到同一個值");

        var path = Path.Combine(dir, ConversationStore.OrdFileName);
        File.WriteAllText(path, content);
        ConversationStore.ReadOrdReservationFile(dir).Should().Be(new Myriagent.Sync.OrdReservation(null, true), "不知道保留到哪裡：當成損毀，下一個 Ord 多跳");
        File.ReadAllText(path).Should().Be(content, "只讀：損毀的旁檔不改名、不改內容（下一次保留時整份蓋掉）");
        Directory.GetFiles(dir, ConversationStore.OrdFileName + ".corrupt-*").Should().BeEmpty();

        store.TrySaveOrdReservation(conversation.Id, 129).Should().BeTrue();
        store.ReadOrdReservation(conversation.Id).Should().Be(new Myriagent.Sync.OrdReservation(129, false), "蓋掉損毀的旁檔");
    }

    [Fact]
    public void Ord旁檔_讀不到也當成損毀()
    {
        var store = new ConversationStore(temp.Root);
        var conversation = new AgentConversation { Title = "x", AccountId = "a1" };
        store.SaveConversation(conversation);
        store.TrySaveOrdReservation(conversation.Id, 65).Should().BeTrue();
        var path = Path.Combine(store.GetConversationDirectory(conversation.Id), ConversationStore.OrdFileName);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            store.ReadOrdReservation(conversation.Id).Should().Be(new Myriagent.Sync.OrdReservation(null, true));

        store.ReadOrdReservation(conversation.Id).Should().Be(new Myriagent.Sync.OrdReservation(65, false), "原檔不動");
    }

    [Fact]
    public void meta不存在_TrySave回false_不建資料夾()
    {
        var store = new ConversationStore(temp.Root);
        var id = Guid.NewGuid().ToString("N");

        store.MetaExists(id).Should().BeFalse();
        store.TrySaveOrdReservation(id, 65).Should().BeFalse("草稿、已刪除、從沒存過的對話不保留");
        Directory.Exists(store.GetConversationDirectory(id)).Should().BeFalse("不建資料夾");

        // 只有附件資料夾（草稿殘留）也一樣
        Directory.CreateDirectory(store.GetAttachmentDirectory(id));
        store.TrySaveOrdReservation(id, 65).Should().BeFalse();
        File.Exists(Path.Combine(store.GetConversationDirectory(id), ConversationStore.OrdFileName)).Should().BeFalse();
        store.FindDraftLeftovers().Should().Equal(new[] { id }, "草稿殘留的判斷不受影響");

        FluentActions.Invoking(() => store.TrySaveOrdReservation(id, 0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    // ───────────────────────────── 伺服器刪除的旁檔（WP4.3 計畫步 6，D25） ─────────────────────────────

    [Fact]
    public void 伺服器刪除旁檔_沒有是false_有是true_損毀也是true_不改名()
    {
        var store = new ConversationStore(temp.Root);
        var conversation = new AgentConversation { Title = "x", AccountId = "a1" };
        store.SaveConversation(conversation);
        var dir = store.GetConversationDirectory(conversation.Id);
        var path = Path.Combine(dir, ConversationStore.ServerDeletedFileName);

        store.IsServerDeleted(conversation.Id).Should().BeFalse("沒有旁檔");
        ConversationStore.ReadServerDeletedFile(dir).Should().BeFalse();

        store.TryWriteServerDeleted(conversation.Id, 1791600000000).Should().BeTrue();
        Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path)).Value<long>("at").Should().Be(1791600000000, "內容只是資訊");
        store.IsServerDeleted(conversation.Id).Should().BeTrue();

        foreach (var content in new[] { "{", "", "null", "{\"at\":\"x\"}" })
        {
            File.WriteAllText(path, content);
            store.IsServerDeleted(conversation.Id).Should().BeTrue($"內容損毀（{content}）照樣算已刪除：只看檔案在不在");
            File.ReadAllText(path).Should().Be(content, "不讀內容、不改名");
        }

        // 萬一被改名成損毀備份（例如之後有人經 JsonFile 讀過）也算
        File.Move(path, path + ".corrupt-20261019-120000");
        store.IsServerDeleted(conversation.Id).Should().BeTrue("改名成 .corrupt-* 的也算，刪除過的對話不會「復活」");
        ConversationStore.ReadServerDeletedFile(dir).Should().BeTrue();

        ConversationStore.ReadServerDeletedFile(Path.Combine(temp.Root, "沒有這個資料夾")).Should().BeFalse("資料夾不存在");
    }

    [Fact]
    public void TryWriteServerDeleted_meta不存在回false_不建資料夾()
    {
        var store = new ConversationStore(temp.Root);
        var id = Guid.NewGuid().ToString("N");

        store.TryWriteServerDeleted(id, 1).Should().BeFalse();
        Directory.Exists(store.GetConversationDirectory(id)).Should().BeFalse("不建資料夾");
        store.IsServerDeleted(id).Should().BeFalse();

        Directory.CreateDirectory(store.GetAttachmentDirectory(id));
        store.TryWriteServerDeleted(id, 1).Should().BeFalse("只有附件資料夾（草稿）也不寫");
        File.Exists(Path.Combine(store.GetConversationDirectory(id), ConversationStore.ServerDeletedFileName)).Should().BeFalse();
    }

    [Fact]
    public void 伺服器刪除旁檔不影響草稿殘留判斷()
    {
        var store = new ConversationStore(temp.Root);
        var draft = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(store.GetAttachmentDirectory(draft));
        Directory.CreateDirectory(store.GetDefaultWorkingDirectory(draft));
        var deleted = new AgentConversation { Title = "已從伺服器刪除", AccountId = "a1" };
        store.SaveConversation(deleted);
        store.TryWriteServerDeleted(deleted.Id, 1).Should().BeTrue();
        store.TryWriteServerDeleted(draft, 1).Should().BeFalse();

        store.FindDraftLeftovers().Should().Equal(new[] { draft }, "草稿照樣清理；有 meta.json 與旁檔的對話不是草稿");
        store.LoadConversations().Select(c => c.Id).Should().Equal(deleted.Id);

        // meta.json 刪到一半（只剩旁檔）的資料夾：不認得的檔案，不當成草稿清掉
        File.Delete(Path.Combine(store.GetConversationDirectory(deleted.Id), "meta.json"));
        store.FindDraftLeftovers().Should().Equal(new[] { draft });
    }

    [Fact]
    public void 根目錄不存在時_清單為空()
        => new ConversationStore(Path.Combine(temp.Root, "沒有這個資料夾")).LoadConversations().Should().BeEmpty();

    [Fact]
    public void 子資料夾位置()
    {
        var store = new ConversationStore(temp.Root);
        store.GetConversationDirectory("c1").Should().Be(Path.Combine(temp.Root, "c1"));
        store.GetAttachmentDirectory("c1").Should().Be(Path.Combine(temp.Root, "c1", "attachments"));
        store.GetModelImageDirectory("c1").Should().Be(Path.Combine(temp.Root, "c1", "attachments", ".model"));
        store.GetDefaultWorkingDirectory("c1").Should().Be(Path.Combine(temp.Root, "c1", "workspace"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    [InlineData("a..b")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a:b")]
    public void 對話Id不安全時拒絕(string id)
        => FluentActions.Invoking(() => new ConversationStore(temp.Root).GetConversationDirectory(id)).Should().Throw<ArgumentException>();

    [Fact]
    public void 根目錄不可為空()
        => FluentActions.Invoking(() => new ConversationStore(" ")).Should().Throw<ArgumentException>();
}
