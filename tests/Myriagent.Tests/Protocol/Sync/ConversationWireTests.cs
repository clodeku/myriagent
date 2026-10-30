using System.Reflection;
using Myriagent.Agents;
using Myriagent.Rendering;
using Myriagent.Sync;
using Myriagent.Tests.Rendering;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 對話事件的線上投影（WP4.1 步 3；docs/sync-protocol.md 14.2、設計 14.11 D31、D32）：桌面模型 → payload。
/// 不送只在記憶體的欄位（程序實例、請求鍵、seq）與任何本機路徑；任何項目與對話都產生得出合格的 payload（ConversationEventRules.Validate）
/// </summary>
public sealed class ConversationWireTests
{
    private static readonly DateTime Time = new(2026, 7, 15, 1, 0, 0, DateTimeKind.Utc);

    private static readonly long TimeMs = ProtocolValues.ToUnixMs(Time)!.Value;

    // ───────────────────────────── 項目 ─────────────────────────────

    /// <summary>AgentChatItem 的每個屬性都要決定上不上線（新增欄位時這裡會變紅）</summary>
    [Fact]
    public void 項目的每個欄位都有決定_線上名稱或明確不送()
    {
        var decided = new Dictionary<string, string>
        {
            ["Id"] = "id", ["Kind"] = "kind", ["Status"] = "status", ["Text"] = "text", ["ToolName"] = "toolName", ["ToolKind"] = "toolKind",
            ["Title"] = "title", ["Detail"] = "detail", ["Output"] = "output", ["Diff"] = "diff", ["FilePath"] = "filePath",
            ["Attachments"] = "attachments", ["Questions"] = "questions", ["Answers"] = "answers", ["Decision"] = "decision",
            ["CanAllowForSession"] = "canAllowForSession", ["AllowSessionLabel"] = "allowSessionLabel", ["AnswerAsMessage"] = "answerAsMessage",
            ["MediaPaths"] = "media（只有檔名、種類與 ref，沒有路徑）", ["PlanSteps"] = "plan", ["CreatedAt"] = "createdAt", ["UpdatedAt"] = "updatedAt",
            ["NoticeKind"] = "noticeKind", ["Ord"] = "ord", ["RequestDigest"] = "requestDigest", ["Risk"] = "risk", ["Author"] = "author", ["ResolvedBy"] = "resolvedBy",
            ["MediaRefs"] = "attachments[].ref、media[].ref（本機的 AttachmentId、MediaPath 不送）",
            ["ProcessInstance"] = "不送", ["RequestKey"] = "不送", ["Seq"] = "不送",
            ["KeptMedia"] = "不送（這台替回覆留的媒體副本，路徑只在這台有意義）",
            ["Source"] = "不送（WP4.3 計畫 D34：送上線要改同步契約）",
            ["CliUuid"] = "不送（只有這台的 Claude 逐字稿認得，回溯與取消插入用）",
        };
        typeof(AgentChatItem).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)
            .Should().BeEquivalentTo(decided.Keys, "AgentChatItem 新增的欄位要先決定上不上線（D32），再更新這張表與 ConversationWire");

        var attachment = new Dictionary<string, string>
        {
            ["Id"] = "id", ["FileName"] = "name", ["Kind"] = "kind", ["SizeBytes"] = "size", ["DurationSeconds"] = "duration",
            ["ModelImagePaths"] = "frames（只有張數）", ["StoredPath"] = "不送", ["FrameSeconds"] = "不送",
            ["Waveform"] = "不送（語音訊息的錄音波形只在錄的那台電腦顯示）",
        };
        typeof(AgentAttachment).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Should().BeEquivalentTo(attachment.Keys);
    }

    [Fact]
    public void 核准卡的線上格式_欄位名稱固定()
    {
        var item = new AgentChatItem
        {
            Id = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8", Ord = 5, Kind = AgentItemKind.Approval, Status = AgentItemStatus.Waiting,
            ToolKind = "bash", ToolName = "執行指令", Title = "npm test", Detail = "npm test --silent", FilePath = @"C:\repo\a.cs",
            CanAllowForSession = false, AllowSessionLabel = "本對話都允許 Bash(npm test:*)", CreatedAt = Time, UpdatedAt = Time,
            ProcessInstance = "p-1", RequestKey = "req-1", Seq = 42, RequestDigest = "rd1:" + new string('a', 64), Risk = AgentRemoteRisk.High,
        };

        ConversationWire.TryItem(item, ver: 2, out var wire).Should().BeTrue();

        ConversationWire.Serialize(wire!).Should().Be(
            $$"""{"id":"0f3c9a52b7e94d0c8a61f2d4e5b6c7d8","ord":5,"ver":2,"kind":"Approval","status":"Waiting","createdAt":{{TimeMs}},"updatedAt":{{TimeMs}},"toolName":"執行指令","toolKind":"bash","title":"npm test","detail":"npm test --silent","filePath":"C:\\repo\\a.cs","canAllowForSession":false,"allowSessionLabel":"本對話都允許 Bash(npm test:*)","answerAsMessage":false,"requestDigest":"rd1:{{new string('a', 64)}}","risk":"High"}""");
    }

    [Fact]
    public void 語料的每一筆_都產生得出合格的payload_而且沒有本機路徑與只在記憶體的欄位()
    {
        var checkedItems = 0;
        foreach (var corpus in ItemRendererCorpus.All())
        {
            for (int i = 0; i < corpus.Items.Count; i++)
            {
                var item = corpus.Items[i];
                item.Ord = i + 1;
                item.ProcessInstance ??= "proc-" + i;
                item.RequestKey = "req-" + i;
                var ok = ConversationWire.TryItem(item, ver: 1, out var wire);
                if (!Enum.IsDefined(item.Kind) || !Enum.IsDefined(item.Status))
                {
                    ok.Should().BeFalse($"{item.Id}：種類或狀態是沒定義的數值，不上線");
                    continue;
                }

                ok.Should().BeTrue(item.Id);
                ConversationEventRules.TryFit(wire!, out _).Should().BeTrue(item.Id);
                ConversationEventRules.Validate(ConversationEventKinds.Item, ConversationWire.Serialize(wire!), out var detail).Should().BeNull($"{item.Id}：{detail}");

                // 卡片的標題、說明、檔案路徑本來就照原樣上線（工具讀寫的檔案），只看附件、媒體與其他結構化欄位裡有沒有本機路徑
                var shown = new[] { "text", "title", "detail", "output", "filePath" };
                var strings = wire!.Properties().Where(p => !shown.Contains(p.Name)).SelectMany(p => p.Value is JContainer container ? container.DescendantsAndSelf() : [p.Value])
                    .OfType<JValue>().Where(v => v.Type == JTokenType.String).Select(v => (string)v!).ToHashSet();
                var local = (item.Attachments ?? new()).Where(a => a != null).SelectMany(a => (a.ModelImagePaths ?? new()).Append(a.StoredPath))
                    .Concat(item.MediaPaths ?? new())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Append(item.ProcessInstance)
                    .Append(item.RequestKey);
                strings.Should().NotIntersectWith(local, $"{item.Id}：附件、媒體的本機路徑與程序實例、請求鍵不上線");
                wire.DescendantsAndSelf().OfType<JProperty>().Select(p => p.Name)
                    .Should().NotContain(name => ConversationEventRules.ForbiddenItemKeys.Contains(name), item.Id);
                checkedItems++;
            }
        }

        checkedItems.Should().BeGreaterThan(80, "語料的項目幾乎都要走過");
    }

    [Fact]
    public void 不能上線的項目回false()
    {
        var ok = new AgentChatItem { Id = "a1", Ord = 1, Kind = AgentItemKind.User, Text = "x" };
        ConversationWire.TryItem(ok, 1, out _).Should().BeTrue();

        ConversationWire.TryItem(new AgentChatItem { Id = "a1", Kind = AgentItemKind.User }, 1, out _).Should().BeFalse("還沒配 Ord");
        ConversationWire.TryItem(new AgentChatItem { Id = "a1", Ord = 0, Kind = AgentItemKind.User }, 1, out _).Should().BeFalse();
        ConversationWire.TryItem(ok, 0, out _).Should().BeFalse("ver 從 1 起");
        ConversationWire.TryItem(new AgentChatItem { Id = "a/1", Ord = 1 }, 1, out _).Should().BeFalse("id 不合格");
        ConversationWire.TryItem(new AgentChatItem { Id = "a1", Ord = 1, Kind = (AgentItemKind)99 }, 1, out _).Should().BeFalse();
        ConversationWire.TryItem(new AgentChatItem { Id = "a1", Ord = 1, Status = (AgentItemStatus)42 }, 1, out _).Should().BeFalse();
    }

    [Fact]
    public void 秘密題目的答案在線上一律隱藏()
    {
        var item = new AgentChatItem
        {
            Id = "q1", Ord = 3, Kind = AgentItemKind.Question, Status = AgentItemStatus.Done, Decision = "answered",
            Questions =
            {
                new AgentQuestion { Id = "token", Text = "API 金鑰？", IsSecret = true },
                new AgentQuestion { Id = "name", Text = "名稱？" },
            },
            Answers = { ["token"] = ["原文不該上線"], ["name"] = ["myriagent"] },
        };

        ConversationWire.TryItem(item, 1, out var wire).Should().BeTrue();

        wire!.ToString().Should().NotContain("原文不該上線");
        wire["answers"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""[{"id":"token","values":["（已隱藏）"]},{"id":"name","values":["myriagent"]}]""");
    }

    [Fact]
    public void 附件與媒體只帶這台發的ref_沒發的沒有ref_格式不對的不送()
    {
        const string AttachmentRef = "01J9Q0000000000000000000AB";
        const string MediaRef = "01J9Q0000000000000000000CD";
        var item = new AgentChatItem
        {
            Id = "m1", Ord = 1, Kind = AgentItemKind.User, Text = "看圖",
            Attachments =
            {
                new AgentAttachment { Id = "x1", FileName = "截圖.png", StoredPath = @"C:\Users\Lin\AppData\Local\Myriagent\conversations\c\attachments\截圖.png", Kind = AgentAttachmentKind.Image, SizeBytes = 2048 },
                new AgentAttachment { Id = "x2", FileName = "錄影.mp4", StoredPath = @"C:\a\錄影.mp4", Kind = AgentAttachmentKind.Video, SizeBytes = 9, ModelImagePaths = { @"C:\a\1.jpg", @"C:\a\2.jpg" }, DurationSeconds = 35 },
                new AgentAttachment { Id = "x3", FileName = "未知.bin", StoredPath = @"C:\a\未知.bin", Kind = (AgentAttachmentKind)7, SizeBytes = -1 },
            },
            MediaPaths = { @"C:\repo\out\chart.png", @"C:\repo\out\demo.mp4" },
            MediaRefs =
            [
                new AgentMediaRef { Ref = AttachmentRef, AttachmentId = "x1" },
                new AgentMediaRef { Ref = MediaRef, MediaPath = @"C:\repo\out\chart.png" },
                new AgentMediaRef { Ref = @"C:\a\錄影.mp4", AttachmentId = "x2" },
            ],
        };

        ConversationWire.TryItem(item, 1, out var wire).Should().BeTrue();

        wire!["attachments"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be(
            $$"""[{"id":"x1","name":"截圖.png","kind":"Image","size":2048,"ref":"{{AttachmentRef}}"},{"id":"x2","name":"錄影.mp4","kind":"Video","size":9,"frames":2,"duration":35.0},{"id":"x3","name":"未知.bin","kind":"Document","size":0}]""");
        wire["media"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be(
            $$"""[{"ref":"{{MediaRef}}","kind":"image","name":"chart.png"},{"kind":"video","name":"demo.mp4"}]""");
        ConversationEventRules.Validate(ConversationEventKinds.Item, wire.ToString()).Should().BeNull();
    }

    [Fact]
    public void 存檔裡的null清單與null元素_略過不丟例外()
    {
        var item = new AgentChatItem
        {
            Id = "n1", Ord = 1, Kind = AgentItemKind.Tool, Text = null!, ToolKind = "edit",
            Attachments = null!, Questions = null!, Answers = null!, MediaPaths = null!, PlanSteps = null!,
            Diff = [new AgentDiffLine("add", "x"), null!, new AgentDiffLine("hunk", null!), new AgentDiffLine("change", "y")],
        };

        ConversationWire.TryItem(item, 1, out var wire).Should().BeTrue();

        wire!["diff"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""[{"t":"add","s":"x"},{"t":"hunk","s":""},{"t":"ctx","s":"y"}]""");
        wire.Properties().Select(p => p.Name).Should().NotContain(["text", "attachments", "questions", "answers", "media", "plan"]);
        ConversationEventRules.Validate(ConversationEventKinds.Item, wire.ToString()).Should().BeNull();
    }

    [Fact]
    public void 讀回的投影與原項目一致()
    {
        var item = new AgentChatItem
        {
            Id = "r1", Ord = 7, Kind = AgentItemKind.Question, Status = AgentItemStatus.Waiting, CreatedAt = Time, UpdatedAt = Time.AddSeconds(5),
            Questions = { new AgentQuestion { Id = "要怎麼彙總？", Header = "彙總", Text = "要怎麼彙總？", MultiSelect = true, AllowOther = false, Options = { new AgentQuestionOption { Label = "依日期", Description = "照日期", Preview = "2026-07-15" } } } },
            Answers = { ["要怎麼彙總？"] = ["依日期"] },
            AnswerAsMessage = true, NoticeKind = null, RequestDigest = "rd1:" + new string('b', 64),
            PlanSteps = { new AgentPlanStep { Text = "讀取報表", Status = "inProgress" } },
        };

        ConversationWire.TryItem(item, 3, out var wire).Should().BeTrue();
        ConversationEventRules.TryParseStrict(ConversationWire.Serialize(wire!), out var parsed).Should().BeTrue();
        ConversationWire.TryReadItem((JObject)parsed!, _ => null, out var read).Should().BeTrue();

        read.Should().BeEquivalentTo(new WireItem(
            Id: "r1", Ord: 7, Ver: 3, Kind: "Question", Status: "Waiting", CreatedAt: TimeMs, UpdatedAt: TimeMs + 5000,
            Questions: [new WireQuestion("要怎麼彙總？", "彙總", "要怎麼彙總？", true, false, false, [new WireQuestionOption("依日期", "照日期", "2026-07-15")])],
            Answers: [new WireAnswer("要怎麼彙總？", ["依日期"])],
            AnswerAsMessage: true, Plan: [new WirePlanStep("讀取報表", "inProgress")], RequestDigest: "rd1:" + new string('b', 64)));
    }

    // ───────────────────────────── 中繼資料 ─────────────────────────────

    [Fact]
    public void 中繼資料的線上格式_預設workspace不送路徑()
    {
        var meta = new AgentConversation
        {
            Id = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8", Title = "修正登入流程", AccountId = "a1", Provider = AgentProvider.Claude,
            SessionId = "不上線的-session", PermissionMode = AgentPermissionMode.Plan, CreatedAt = Time, UpdatedAt = Time,
            RemoteControlSessionId = "不上線的-rc", LastCliVersion = "2.1.297",
        };

        var payload = ConversationWire.Meta(meta, "Claude", running: true, waiting: false);

        ConversationWire.Serialize(payload).Should().Be(
            $$"""{"provider":"Claude","localAccountId":"a1","title":"修正登入流程","permissionMode":"Plan","customFolder":false,"syncPolicy":"full","createdAt":{{TimeMs}},"updatedAt":{{TimeMs}},"running":true,"waiting":false}""");
        ConversationEventRules.Validate(ConversationEventKinds.Created, ConversationWire.Serialize(payload)).Should().BeNull();
    }

    [Fact]
    public void 中繼資料_自訂資料夾與模型_病態值也產生得出合格的payload()
    {
        var custom = ConversationWire.Meta(new AgentConversation
        {
            Title = "x", AccountId = "acc-1", Provider = AgentProvider.Codex, Model = "gpt-5.5", Effort = "high", WorkingDirectory = @"D:\work\proj", SyncPolicy = "metadata",
        }, "Codex", false, true);
        custom.CustomFolder.Should().BeTrue();
        custom.WorkingDirectory.Should().Be(@"D:\work\proj");
        custom.SyncPolicy.Should().Be("metadata");
        Valid(custom).Should().BeNull();

        var odd = ConversationWire.Meta(new AgentConversation
        {
            Title = new string('題', 300) + "𠮷", AccountId = string.Empty, PermissionMode = (AgentPermissionMode)9,
            Model = new string('m', 500), WorkingDirectory = @"C:\" + new string('p', 2000), CreatedAt = DateTime.MinValue,
        }, "Claude", false, false);
        odd.Title.Should().HaveLength(ConversationEventRules.MaxTitleLength).And.EndWith("…");
        odd.LocalAccountId.Should().BeNull("帳戶不明（meta.json 重建）");
        odd.PermissionMode.Should().BeNull("沒定義的數值是不明");
        odd.Model.Should().HaveLength(ConversationEventRules.MaxShortTextLength);
        odd.CustomFolder.Should().BeTrue();
        odd.WorkingDirectory.Should().BeNull("路徑太長不送");
        odd.CreatedAt.Should().BeNull();
        Valid(odd).Should().BeNull();
    }

    [Fact]
    public void 中繼資料_不認得的同步政策是localOnly_不該發布_送了也被擋()
    {
        var payload = ConversationWire.Meta(new AgentConversation { Title = "x", SyncPolicy = "public" }, "Claude", false, false);

        payload.SyncPolicy.Should().Be("localOnly");
        ConversationSyncPolicies.Publishes(payload.SyncPolicy, ConversationEventKinds.Meta).Should().BeFalse();
        Valid(payload).Should().Be("invalid");
    }

    [Fact]
    public void 截斷不切開代理字元對()
    {
        ConversationWire.Clamp("abc", 3).Should().Be("abc");
        ConversationWire.Clamp("abcd", 3).Should().Be("ab…");
        ConversationWire.Clamp("a𠮷bc", 3).Should().Be("a…", "𠮷（U+20BB7）是兩個 UTF-16 字元，不能只留前半");
        ConversationWire.Clamp("ab𠮷c", 4).Should().Be("ab…");
        ConversationWire.Clamp("ab𠮷c", 5).Should().Be("ab𠮷c");
    }

    // ───────────────────────────── 佇列、一輪、注意、刪除 ─────────────────────────────

    [Fact]
    public void 佇列快照_文字截斷_超過則數的算進more_id不合格的不帶()
    {
        var entries = Enumerable.Range(0, 60).Select(i => ("q" + i, i == 0 ? new string('長', 1000) : "訊息 " + i)).Append(("壞 id", "x")).ToList();

        var payload = ConversationWire.Queue(entries);

        payload.Entries.Should().HaveCount(ConversationEventRules.MaxQueueEntries);
        payload.More.Should().Be(11);
        payload.Entries[0].DisplayText.Should().HaveLength(ConversationEventRules.MaxQueueTextLength).And.EndWith("…");
        payload.Entries[1].Should().Be(new ConversationQueueEntry("q1", "訊息 1"));
        Valid(ConversationEventKinds.Queue, payload).Should().BeNull();
        ConversationWire.Serialize(ConversationWire.Queue([("q1", "接著幫我跑測試")])).Should().Be("""{"entries":[{"id":"q1","displayText":"接著幫我跑測試"}],"more":0}""");
    }

    [Fact]
    public void 佇列快照_跳脫字元膨脹也放得進上限()
    {
        // 控制字元在 JSON 裡是 \u0001（6 位元組）：50 則 × 600 字會膨脹到 180 KB，從後面少帶直到放得下
        var control = new string((char)1, 2000);
        var payload = ConversationWire.Queue(Enumerable.Range(0, 50).Select(i => ("q" + i, control)));

        payload.Entries.Count.Should().BeLessThan(50);
        (payload.Entries.Count + payload.More).Should().Be(50);
        System.Text.Encoding.UTF8.GetByteCount(ConversationWire.Serialize(payload)).Should().BeLessThanOrEqualTo(ConversationEventRules.MaxPayloadBytes);
        Valid(ConversationEventKinds.Queue, payload).Should().BeNull();
    }

    [Fact]
    public void 一輪_注意_刪除的線上格式()
    {
        ConversationWire.Serialize(new ConversationTurnPayload(ConversationTurnPhases.Started)).Should().Be("""{"phase":"started"}""");
        ConversationWire.Serialize(new ConversationTurnPayload(ConversationTurnPhases.Completed, ConversationTurnOutcomes.From(AgentTurnOutcome.Interrupted), new ConversationTurnUsage(12345, 2100, 0, 0.5)))
            .Should().Be("""{"phase":"completed","outcome":"interrupted","usage":{"inputTokens":12345,"outputTokens":2100,"cachedInputTokens":0,"costUsd":0.5}}""");
        ConversationWire.Serialize(new ConversationAttentionPayload(ConversationAttentionKinds.Approval, "i1")).Should().Be("""{"kind":"approval","itemId":"i1"}""");
        ConversationWire.Serialize(new ConversationDeletedPayload()).Should().Be("{}");
        ConversationWire.Serialize(new ConversationItemRemovedPayload("i1")).Should().Be("""{"id":"i1"}""");

        foreach (var outcome in Enum.GetValues<AgentTurnOutcome>())
        {
            var json = ConversationWire.Serialize(new ConversationTurnPayload(ConversationTurnPhases.Completed, ConversationTurnOutcomes.From(outcome)));
            ConversationEventRules.Validate(ConversationEventKinds.Turn, json).Should().BeNull(outcome.ToString());
        }

        FluentActions.Invoking(() => ConversationTurnOutcomes.From((AgentTurnOutcome)9)).Should().Throw<ArgumentOutOfRangeException>();
    }

    // ───────────────────────────── 模型清單（meta 的 models，2026-10-23） ─────────────────────────────

    [Fact]
    public void 中繼資料_模型清單_照順序送_沒有清單時JSON不變()
    {
        var meta = new AgentConversation { Title = "x", AccountId = "a1", Provider = AgentProvider.Claude, Model = "opus" };
        AgentModelOption[] models =
        [
            new("default", "預設（建議）", "帳戶預設模型", ["low", "high"]),
            new("opus", "Opus", "複雜工作", ["low", "medium", "high"]),
            new("haiku", "Haiku", null, []),
        ];

        var payload = ConversationWire.Meta(meta, "Claude", false, false, models);

        var json = ConversationWire.Serialize(payload);
        json.Should().EndWith(
            ""","running":false,"waiting":false,"models":[{"value":"default","label":"預設（建議）","description":"帳戶預設模型","efforts":["low","high"]},{"value":"opus","label":"Opus","description":"複雜工作","efforts":["low","medium","high"]},{"value":"haiku","label":"Haiku"}]}""");
        Valid(payload).Should().BeNull();
        ConversationWire.Serialize(ConversationWire.Meta(meta, "Claude", false, false)).Should().NotContain("models", "沒有清單（舊的呼叫端、副本）不送這個鍵");
        ConversationWire.Serialize(ConversationWire.Meta(meta, "Claude", false, false, [])).Should().NotContain("models");
        ConversationWire.Serialize(ConversationWire.Meta(meta, "Claude", false, false, models)).Should().Be(json, "同樣的輸入產生同樣的內容（發布者以摘要判斷要不要重送）");
    }

    [Fact]
    public void 中繼資料_模型清單_病態值整理成合格的payload()
    {
        var random = new Random(71);
        var ghp = "gh" + "p_" + RandomBody(random, 36);
        var models = new List<AgentModelOption>
        {
            new(string.Empty, "空的 value", null, []),
            new(new string('v', ConversationEventRules.MaxShortTextLength + 1), "太長的 value 截斷了就送不回 setOption", null, []),
            new(ghp, "value 遮蔽後會變的不送", null, []),
            new("opus", string.Empty, new string('說', 400) + " " + ghp, ["high", "high", string.Empty, new string('e', 200), .. Enumerable.Range(0, 30).Select(i => "e" + i)]),
            new("opus", "重複的 value 只留第一個", null, []),
            new("sonnet", new string('名', 300) + ghp, null, null!),
            null!,
        };
        models.AddRange(Enumerable.Range(0, 40).Select(i => new AgentModelOption("m" + i, "M" + i, null, [])));

        var payload = ConversationWire.Meta(new AgentConversation { Title = "x", AccountId = "a1" }, "Claude", false, false, models);

        var list = payload.Models!;
        list.Should().HaveCount(ConversationEventRules.MaxModelOptions);
        list.Select(m => m.Value).Take(3).Should().Equal("opus", "sonnet", "m0");
        list[0].Label.Should().Be("opus", "label 空的用 value");
        list[0].Description!.Length.Should().BeLessThanOrEqualTo(ConversationEventRules.MaxModelDescriptionLength);
        list[0].Efforts.Should().HaveCount(ConversationEventRules.MaxModelEfforts).And.OnlyHaveUniqueItems().And.StartWith("high");
        list[1].Label.Length.Should().BeLessThanOrEqualTo(ConversationEventRules.MaxShortTextLength);
        list[1].Efforts.Should().BeNull("沒有推理強度時省略");
        var json = ConversationWire.Serialize(payload);
        json.Should().NotContain(ghp);
        CredentialLeakRules.HasLeak(json).Should().BeFalse();
        Valid(payload).Should().BeNull();
    }

    [Fact]
    public void 中繼資料_模型清單_整份超過上限時從後面少帶()
    {
        // 每項都到上限、字是控制字元（JSON 跳脫成六個位元組）：32 項放不進 128 KB
        var ctl = (char)1;
        var models = Enumerable.Range(0, ConversationEventRules.MaxModelOptions).Select(i => new AgentModelOption(
            "m" + i, new string(ctl, ConversationEventRules.MaxShortTextLength), new string(ctl, ConversationEventRules.MaxModelDescriptionLength),
            Enumerable.Range(0, ConversationEventRules.MaxModelEfforts).Select(e => new string(ctl, ConversationEventRules.MaxShortTextLength - 3) + e.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)).ToList())).ToList();
        var full = new ConversationMetaPayload("Claude", "a1", new string('題', ConversationEventRules.MaxTitleLength), "Ask", "opus", "high", false, null, "full", null, null,
            false, false, ConversationWire.ModelOptions(models));
        System.Text.Encoding.UTF8.GetByteCount(ConversationWire.Serialize(full)).Should().BeGreaterThan(ConversationEventRules.MaxPayloadBytes, "前提：整份清單放不下");

        var payload = ConversationWire.Meta(new AgentConversation { Title = new string('題', ConversationEventRules.MaxTitleLength), AccountId = "a1", Model = "opus", Effort = "high" },
            "Claude", false, false, models);

        payload.Models.Should().NotBeNullOrEmpty().And.HaveCountLessThan(models.Count);
        payload.Models!.Select(m => m.Value).Should().Equal(models.Take(payload.Models!.Count).Select(m => m.Value), "從後面少帶");
        System.Text.Encoding.UTF8.GetByteCount(ConversationWire.Serialize(payload)).Should().BeLessThanOrEqualTo(ConversationEventRules.MaxPayloadBytes);
        Valid(payload).Should().BeNull();
    }

    // ───────────────────────────── 權杖 DLP 與 blob（合併前審查） ─────────────────────────────

    private static string RandomBody(Random random, int length)
        => new(Enumerable.Range(0, length).Select(_ => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"[random.Next(62)]).ToArray());

    [Fact]
    public void 中繼資料與佇列_標題模型強度資料夾與顯示文字都先遮蔽_截斷處的半截權杖也去掉_仍然合格()
    {
        var random = new Random(53);
        var ghpPrefix = "gh" + "p_";
        var ghp = ghpPrefix + RandomBody(random, 36);
        var proj = "sk" + "-proj-" + RandomBody(random, 120);

        var full = ConversationWire.Meta(new AgentConversation
        {
            Title = Myriagent.Hub.AgentHubRules.MakeTitle("用 " + proj + " 呼叫 API"), AccountId = "a1", Provider = AgentProvider.Codex,
            Model = "gpt " + ghp, Effort = ghp, WorkingDirectory = @"D:\keys\" + ghp,
        }, "Codex", false, false);
        var json = ConversationWire.Serialize(full);
        json.Should().NotContain(proj[..14]).And.NotContain(ghp);
        CredentialLeakRules.HasLeak(json).Should().BeFalse();
        full.CustomFolder.Should().BeTrue();
        full.WorkingDirectory.Should().StartWith(@"D:\keys\");
        Valid(full).Should().BeNull();

        // 標題截斷在權杖中間（MakeTitle 的「…」）：剩下的不夠長、比對不到，要先去掉
        var cut = ConversationWire.Meta(new AgentConversation { Title = "設定 " + ghpPrefix + RandomBody(random, 10) + "…", AccountId = "a1" }, "Claude", false, false);
        cut.Title.Should().Be("設定 …");
        Valid(cut).Should().BeNull();

        var queue = ConversationWire.Queue([("q1", "我的金鑰是 " + ghp + " 請幫我設定"), ("q2", new string('長', 590) + " " + ghp)]);
        var queueJson = ConversationWire.Serialize(queue);
        queueJson.Should().NotContain(ghpPrefix, "完整的權杖遮蔽、截斷處的半截權杖去掉");
        queue.Entries[0].DisplayText.Should().Be("我的金鑰是 " + CredentialLeakRules.Replacement + " 請幫我設定");
        queue.Entries[1].DisplayText.Length.Should().BeLessThanOrEqualTo(ConversationEventRules.MaxQueueTextLength);
        Valid(ConversationEventKinds.Queue, queue).Should().BeNull();
    }

    /// <summary>把投影的 attachments 換成 blob（內容是 <paramref name="content"/>），以 TryReadItem 讀回</summary>
    private static bool ReadWithAttachmentsBlob(JObject wire, JArray content)
    {
        var payload = (JObject)wire.DeepClone();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content.ToString(Newtonsoft.Json.Formatting.None));
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        payload["attachments"] = new JObject { ["blob"] = sha, ["length"] = bytes.LongLength };
        ConversationEventRules.Validate(ConversationEventKinds.Item, ConversationWire.Serialize(payload)).Should().BeNull("blob 參照本身合格（內容不在 payload 的驗證範圍）");
        return ConversationWire.TryReadItem(payload, blob => blob == sha ? bytes : null, out _);
    }

    [Fact]
    public void 讀回換成blob的欄位_內容照共用驗證檢查形狀_禁止的欄位_id不合格_種類不認得都回false()
    {
        var item = new AgentChatItem
        {
            Id = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8", Ord = 3, Kind = AgentItemKind.User, Status = AgentItemStatus.Done, Text = "看這張圖", CreatedAt = Time, UpdatedAt = Time,
            Attachments = { new AgentAttachment { Id = "a1b2c3", FileName = "chart.png", Kind = AgentAttachmentKind.Image, SizeBytes = 10 } },
        };
        ConversationWire.TryItem(item, 1, out var wire).Should().BeTrue();
        var attachments = (JArray)wire!["attachments"]!;

        ReadWithAttachmentsBlob(wire, attachments).Should().BeTrue("對照：合格的內容讀得回來");

        var forbidden = (JArray)attachments.DeepClone();
        ((JObject)forbidden[0])["storedPath"] = @"C:\Users\Lin\secret.png";
        ReadWithAttachmentsBlob(wire, forbidden).Should().BeFalse("不准上線的本機路徑欄位");

        var badId = (JArray)attachments.DeepClone();
        ((JObject)badId[0])["id"] = "../../x";
        ReadWithAttachmentsBlob(wire, badId).Should().BeFalse("附件 id 不合格");

        var badKind = (JArray)attachments.DeepClone();
        ((JObject)badKind[0])["kind"] = "Hologram";
        ReadWithAttachmentsBlob(wire, badKind).Should().BeFalse("附件種類不認得");
    }

    private static string? Valid(ConversationMetaPayload payload) => ConversationEventRules.Validate(ConversationEventKinds.Meta, ConversationWire.Serialize(payload));

    private static string? Valid(string kind, object payload) => ConversationEventRules.Validate(kind, ConversationWire.Serialize(payload));
}
