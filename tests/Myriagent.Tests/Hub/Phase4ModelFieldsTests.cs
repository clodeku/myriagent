using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Json;
using Myriagent.Remote;
using Myriagent.Rendering;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 第 4 階段的模型欄位與持久化相容（WP4.1 計畫 5.2）：項目的 Ord、RequestDigest、Risk、MediaRefs，對話的 SyncPolicy、OwnerDeviceId、LeaseEpoch，
/// 佇列訊息與拒絕附言的 RemoteOrigin、DisplayText。全部可為 null、null 時不寫進檔案：舊版寫的 meta.json、items.json、queue.json
/// 讀進來再存回要逐位元組不變（回滾到舊版、再升級都讀得懂），網頁的 Item 與黃金檔不變
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class Phase4ModelFieldsTests : IDisposable
{
    private const string ConversationId = "5f3a0c1e2b4d4f6a8b9c0d1e2f3a4b5c";

    /// <summary>新欄位在 JSON 裡的名稱（null 時一個都不能出現）</summary>
    private static readonly string[] NewFieldNames = ["Ord", "RequestDigest", "Risk", "MediaRefs", "SyncPolicy", "OwnerDeviceId", "LeaseEpoch", "RemoteOrigin", "DisplayText"];

    private readonly TempDirectory temp = new("phase4-model");

    public void Dispose() => temp.Dispose();

    [Fact]
    public void 舊版meta_json_讀進來再存回_逐位元組不變()
    {
        var store = new ConversationStore(temp.Root);
        var path = WriteOld(Path.Combine(temp.Root, ConversationId, "meta.json"), OldMeta);

        var meta = store.LoadConversations().Should().ContainSingle().Subject;
        meta.SyncPolicy.Should().BeNull("舊檔沒有：null＝full");
        meta.OwnerDeviceId.Should().BeNull();
        meta.LeaseEpoch.Should().BeNull();
        meta.RemoteControlSessionId.Should().Be("session_01ABC", "陽性對照：舊欄位照讀");
        var before = File.ReadAllBytes(path);

        store.SaveConversation(meta);

        File.ReadAllBytes(path).Should().Equal(before, "舊版寫的 meta.json 存回時不多任何欄位");
    }

    [Fact]
    public void 舊版items_json_讀進來再存回_逐位元組不變()
    {
        var store = new ConversationStore(temp.Root);
        Directory.CreateDirectory(Path.Combine(temp.Root, ConversationId));
        var path = WriteOld(Path.Combine(temp.Root, ConversationId, "items.json"), OldItems);

        var (items, status, _) = store.TryLoadItems(ConversationId);
        status.Should().Be(JsonReadStatus.Ok);
        items.Should().HaveCount(5);
        items.Should().OnlyContain(i => i.Ord == null && i.RequestDigest == null && i.Risk == null && i.MediaRefs == null);
        items[0].ProcessInstance.Should().Be("p-1", "陽性對照：舊欄位照讀");
        items[4].NoticeKind.Should().Be(AgentNoticeKind.CliUpdate);
        var before = File.ReadAllBytes(path);

        store.SaveItems(ConversationId, items);

        File.ReadAllBytes(path).Should().Equal(before, "舊版寫的 items.json 存回時不多任何欄位");
    }

    [Fact]
    public void 舊版queue_json_讀進來再存回_逐位元組不變()
    {
        var dir = Path.Combine(temp.Root, ConversationId);
        var path = WriteOld(Path.Combine(dir, QueueStore.FileName), OldQueue);

        var (status, snapshot) = QueueStore.TryLoad(dir);
        status.Should().Be(JsonReadStatus.Ok);
        snapshot!.Queue.Should().ContainSingle().Which.Should().Match<QueuedMessageData>(q => q.RemoteOrigin == null && q.DisplayText == null);
        snapshot.PendingFeedback.Should().ContainSingle().Which.Should().Be(new DeniedOperation("執行指令", "git push", "先不要推"));
        var before = File.ReadAllBytes(path);

        QueueStore.Save(dir, snapshot);

        File.ReadAllBytes(path).Should().Equal(before, "舊版寫的 queue.json 存回時不多任何欄位");
    }

    [Fact]
    public void 新欄位是null時不寫進檔案()
    {
        var json = string.Join("\n",
            JsonConvert.SerializeObject(new AgentChatItem { Kind = AgentItemKind.Approval, Attachments = { new AgentAttachment { FileName = "a.png" } } }, ProtocolJson.Settings),
            JsonConvert.SerializeObject(new AgentConversation(), ProtocolJson.Settings),
            JsonConvert.SerializeObject(new QueueSnapshot(new() { new("q1", "下一則", new()) }, new(), new() { new DeniedOperation("執行指令", "git push", "先不要推") }), ProtocolJson.Settings));

        foreach (var name in NewFieldNames)
            json.Should().NotContain($"\"{name}\"", $"{name} 是 null 時不寫（舊檔逐位元組不變）");
        json.Should().Contain("\"Kind\": \"Approval\"", "陽性對照：序列化的確實是這些物件").And.Contain("\"Feedback\": \"先不要推\"");
    }

    [Fact]
    public void 對話的NextOrd_null時不寫_存讀往返_舊檔讀成null()
    {
        // WP4.1 步 4 才加的欄位（OrdRules 的下一個 Ord；刪掉的項目的 Ord 不重用，所以要記在 meta.json）
        JsonConvert.SerializeObject(new AgentConversation(), ProtocolJson.Settings).Should().NotContain("\"NextOrd\"");

        var store = new ConversationStore(temp.Root);
        var meta = new AgentConversation { Title = "排序", NextOrd = 42 };
        store.SaveConversation(meta);
        File.ReadAllText(Path.Combine(temp.Root, meta.Id, "meta.json")).Should().Contain("\"NextOrd\": 42");
        store.LoadConversations().Single(c => c.Id == meta.Id).NextOrd.Should().Be(42);

        WriteOld(Path.Combine(temp.Root, ConversationId, "meta.json"), OldMeta);
        store.LoadConversations().Single(c => c.Id == ConversationId).NextOrd.Should().BeNull("舊檔沒有：還沒配過");
    }

    [Fact]
    public void 項目與對話的新欄位存讀往返()
    {
        var store = new ConversationStore(temp.Root);
        var meta = new AgentConversation { Title = "同步的對話", AccountId = "a1", SyncPolicy = "localOnly", OwnerDeviceId = "d_7K3M0000000000000000000000", LeaseEpoch = 3 };
        var attachment = new AgentAttachment { FileName = "截圖.png", StoredPath = temp.Combine("截圖.png"), Kind = AgentAttachmentKind.Image };
        var item = new AgentChatItem
        {
            Kind = AgentItemKind.Approval,
            Status = AgentItemStatus.Waiting,
            ToolName = "執行指令",
            Title = "git push",
            Attachments = { attachment },
            MediaPaths = { @"C:\out\chart.png" },
            Ord = 42,
            RequestDigest = "rd1:" + new string('0', 64),
            Risk = AgentRemoteRisk.High,
            MediaRefs = new()
            {
                new AgentMediaRef { Ref = "01J9Q0000000000000000000AB", AttachmentId = attachment.Id },
                new AgentMediaRef { Ref = "01J9Q0000000000000000000CD", MediaPath = @"C:\out\chart.png" },
            },
        };
        store.SaveConversation(meta);
        store.SaveItems(meta.Id, new[] { item });

        var reopened = new ConversationStore(temp.Root);
        var loadedMeta = reopened.LoadConversations().Single();
        loadedMeta.SyncPolicy.Should().Be("localOnly");
        loadedMeta.OwnerDeviceId.Should().Be("d_7K3M0000000000000000000000");
        loadedMeta.LeaseEpoch.Should().Be(3);
        var loaded = reopened.LoadItems(meta.Id).Single();
        loaded.Ord.Should().Be(42);
        loaded.RequestDigest.Should().Be(item.RequestDigest);
        loaded.Risk.Should().Be(AgentRemoteRisk.High);
        loaded.MediaRefs.Should().BeEquivalentTo(item.MediaRefs);

        var json = File.ReadAllText(Path.Combine(temp.Root, meta.Id, "items.json"));
        json.Should().Contain("\"Risk\": \"High\"", "列舉存成名稱");
        json.Should().Contain("\"Ord\": 42");
    }

    [Fact]
    public void 佇列與拒絕附言的新欄位存讀往返()
    {
        var dir = temp.Combine("queue");
        var origin = new RemoteOrigin("01J9Q0000000000000000000EF", AgentPermissionMode.AcceptEdits, 2, true);
        var snapshot = new QueueSnapshot(
            new() { new("q1", "CLI 收到的原文", new(), origin, "畫面看到的文字") },
            new(),
            new() { new DeniedOperation("執行指令", "git push", "先不要推", origin, "先不要推（遠端）") });

        QueueStore.Save(dir, snapshot);
        var back = QueueStore.Load(dir)!;

        var queued = back.Queue.Single();
        queued.RemoteOrigin.Should().Be(origin);
        queued.DisplayText.Should().Be("畫面看到的文字");
        queued.Text.Should().Be("CLI 收到的原文");
        back.PendingFeedback.Single().Should().Be(snapshot.PendingFeedback.Single());
        File.ReadAllText(Path.Combine(dir, QueueStore.FileName)).Should().Contain("\"EffectiveMode\": \"AcceptEdits\"", "列舉存成名稱");
    }

    [Fact]
    public void 不認得的列舉名稱讀成null_整份檔不當成損毀()
    {
        // 之後的版本新增的風險、權限模式：舊版讀成 null（最嚴格：風險當成非一般、佇列不取出），不把整份檔改名備份
        var store = new ConversationStore(temp.Root);
        Directory.CreateDirectory(Path.Combine(temp.Root, ConversationId));
        File.WriteAllText(Path.Combine(temp.Root, ConversationId, "items.json"), """[ { "Id": "i1", "Kind": "Approval", "Status": "Waiting", "Text": "", "Risk": "Extreme" } ]""");
        var (items, itemsStatus, backup) = store.TryLoadItems(ConversationId);
        itemsStatus.Should().Be(JsonReadStatus.Ok);
        backup.Should().BeNull();
        items.Single().Risk.Should().BeNull();

        var dir = Path.Combine(temp.Root, ConversationId);
        File.WriteAllText(Path.Combine(dir, QueueStore.FileName),
            """{ "Queue": [ { "Id": "q1", "Text": "下一則", "Attachments": [], "RemoteOrigin": { "CommandId": "01J9Q0000000000000000000EF", "EffectiveMode": "Turbo", "Tier": 1, "FullAccess": false } } ] }""");
        var (queueStatus, snapshot) = QueueStore.TryLoad(dir);
        queueStatus.Should().Be(JsonReadStatus.Ok);
        var origin = snapshot!.Queue.Single().RemoteOrigin;
        origin.Should().NotBeNull();
        origin!.EffectiveMode.Should().BeNull();
        origin.CommandId.Should().Be("01J9Q0000000000000000000EF");
        origin.Tier.Should().Be(1);
    }

    [Fact]
    public void 新欄位不影響網頁的Item()
    {
        var renderer = new ItemRenderer(new LocalMediaUrls());
        AgentChatItem Card() => new()
        {
            Id = "i1",
            Kind = AgentItemKind.Approval,
            Status = AgentItemStatus.Waiting,
            ToolName = "執行指令",
            ToolKind = "bash",
            Title = "git push",
            Detail = "git push origin main",
            Seq = 7,
            CreatedAt = new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Local),
            UpdatedAt = new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Local),
        };
        var plain = Card();
        var withFields = Card();
        withFields.Ord = 99;
        withFields.RequestDigest = "rd1:" + new string('f', 64);
        withFields.Risk = AgentRemoteRisk.High;
        withFields.MediaRefs = new() { new AgentMediaRef { Ref = "01J9Q0000000000000000000AB", MediaPath = @"C:\out\chart.png" } };

        var expected = renderer.Render(plain, temp.Root).ToString(Formatting.None);
        var actual = renderer.Render(withFields, temp.Root).ToString(Formatting.None);

        actual.Should().Be(expected, "ItemRenderer 不輸出第 4 階段的欄位（瀏覽器模式的 requestDigest、risk 由伺服器的渲染加，WP4.2）");
        expected.Should().Contain("git push", "陽性對照：渲染的確實是這張卡");
    }

    [Fact]
    public async Task 佇列的遠端來源與顯示文字_中樞載入後再存檔不遺失()
    {
        var conversation = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "上次的對話", SessionId = Guid.NewGuid().ToString(), SessionStarted = true };
        var origin = new RemoteOrigin("01J9Q0000000000000000000EF", AgentPermissionMode.Ask, 1, false);
        using var h = new HubHarness(seedStore: store =>
        {
            store.SaveConversation(conversation);
            QueueStore.Save(store.GetConversationDirectory(conversation.Id), new QueueSnapshot(new() { new("q-remote", "遠端排的", new(), origin, "遠端排的（顯示）") }, new(), new()));
        });
        var id = conversation.Id;
        h.Queue(id).Should().Equal("遠端排的");

        // 佇列變動讓中樞重寫 queue.json：載入的那一則的來源與顯示文字要照樣寫回
        h.Send(h.Main, id, "這一輪");
        h.Send(h.Main, id, "接在後面");
        await h.WaitUntilAsync(() => h.Queue(id).Count == 2, "執行中送出排進佇列");

        var saved = QueueStore.Load(h.Store.GetConversationDirectory(id))!.Queue;
        saved.Select(q => q.Text).Should().Equal("遠端排的", "接在後面");
        saved[0].Id.Should().Be("q-remote");
        saved[0].RemoteOrigin.Should().Be(origin);
        saved[0].DisplayText.Should().Be("遠端排的（顯示）");
        saved[1].RemoteOrigin.Should().BeNull("本機送的沒有遠端來源");
        saved[1].DisplayText.Should().BeNull();
    }

    /// <summary>舊版寫的檔：換成這台的換行與本機時區的時間（Newtonsoft 的縮排輸出），UTF-8 不帶 BOM</summary>
    private static string WriteOld(string path, string template)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = template
            .Replace("{T1}", LocalTime(new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Local)), StringComparison.Ordinal)
            .Replace("{T2}", LocalTime(new DateTime(2026, 10, 8, 11, 30, 0, DateTimeKind.Local)), StringComparison.Ordinal)
            .ReplaceLineEndings(Environment.NewLine);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
        return path;
    }

    private static string LocalTime(DateTime local) => JsonConvert.SerializeObject(local, ProtocolJson.Settings);

    private const string OldMeta = """
        {
          "Id": "5f3a0c1e2b4d4f6a8b9c0d1e2f3a4b5c",
          "Title": "修正登入流程",
          "AccountId": "acc-claude",
          "Provider": "Claude",
          "SessionId": "6d1f2a3b-4c5d-4e6f-8a9b-0c1d2e3f4a5b",
          "SessionStarted": true,
          "SessionAccountId": "acc-claude",
          "Model": "opus",
          "Effort": "high",
          "PermissionMode": "AcceptEdits",
          "WorkingDirectory": "C:\\repo\\proj",
          "LastCliVersion": "2.1.297",
          "RemoteControlSessionId": "session_01ABC",
          "CreatedAt": {T1},
          "UpdatedAt": {T2}
        }
        """;

    private const string OldItems = """
        [
          {
            "Id": "a1b2c3d4e5f60718293a4b5c6d7e8f90",
            "Kind": "Approval",
            "Status": "Waiting",
            "Text": "",
            "ToolName": "執行指令",
            "ToolKind": "bash",
            "Title": "npm test",
            "Detail": "npm test -- --watch=false",
            "Output": "你的說明：先跑單元測試",
            "FilePath": "C:\\repo\\proj\\package.json",
            "Attachments": [],
            "Questions": [],
            "Answers": {},
            "Decision": "deny",
            "CanAllowForSession": false,
            "AllowSessionLabel": "本對話都允許 Bash(npm test:*)",
            "AnswerAsMessage": false,
            "MediaPaths": [],
            "PlanSteps": [],
            "CreatedAt": {T1},
            "UpdatedAt": {T2},
            "ProcessInstance": "p-1"
          },
          {
            "Id": "b1b2c3d4e5f60718293a4b5c6d7e8f90",
            "Kind": "User",
            "Status": "Done",
            "Text": "請看這張圖",
            "Attachments": [
              {
                "Id": "c1b2c3d4e5f60718293a4b5c6d7e8f90",
                "FileName": "截圖.png",
                "StoredPath": "C:\\Users\\U\\AppData\\Local\\Myriagent\\conversations\\5f3a0c1e2b4d4f6a8b9c0d1e2f3a4b5c\\attachments\\截圖.png",
                "Kind": "Image",
                "SizeBytes": 182000,
                "ModelImagePaths": [
                  "C:\\Users\\U\\AppData\\Local\\Myriagent\\conversations\\5f3a0c1e2b4d4f6a8b9c0d1e2f3a4b5c\\attachments\\.model\\截圖.jpg"
                ],
                "FrameSeconds": []
              }
            ],
            "Questions": [],
            "Answers": {},
            "CanAllowForSession": true,
            "AnswerAsMessage": false,
            "MediaPaths": [
              "C:\\out\\chart.png"
            ],
            "PlanSteps": [],
            "CreatedAt": {T1},
            "UpdatedAt": {T1}
          },
          {
            "Id": "d1b2c3d4e5f60718293a4b5c6d7e8f90",
            "Kind": "Tool",
            "Status": "Done",
            "Text": "",
            "ToolName": "編輯檔案",
            "ToolKind": "edit",
            "Title": "a.cs",
            "Diff": [
              {
                "T": "hunk",
                "S": "@@ -1 +1 @@"
              },
              {
                "T": "del",
                "S": "舊的一行"
              },
              {
                "T": "add",
                "S": "新的一行"
              }
            ],
            "FilePath": "C:\\repo\\proj\\a.cs",
            "Attachments": [],
            "Questions": [],
            "Answers": {},
            "CanAllowForSession": true,
            "AnswerAsMessage": false,
            "MediaPaths": [],
            "PlanSteps": [
              {
                "Text": "讀取",
                "Status": "completed"
              }
            ],
            "CreatedAt": {T2},
            "UpdatedAt": {T2}
          },
          {
            "Id": "e1b2c3d4e5f60718293a4b5c6d7e8f90",
            "Kind": "Question",
            "Status": "Done",
            "Text": "",
            "Attachments": [],
            "Questions": [
              {
                "Id": "要怎麼彙總？",
                "Header": "彙總",
                "Text": "要怎麼彙總？",
                "Options": [
                  {
                    "Label": "依日期",
                    "Description": "每天一列"
                  }
                ],
                "MultiSelect": false,
                "AllowOther": true,
                "IsSecret": false
              }
            ],
            "Answers": {
              "要怎麼彙總？": [
                "依日期"
              ]
            },
            "Decision": "answered",
            "CanAllowForSession": true,
            "AnswerAsMessage": false,
            "MediaPaths": [],
            "PlanSteps": [],
            "CreatedAt": {T2},
            "UpdatedAt": {T2},
            "ProcessInstance": "p-1"
          },
          {
            "Id": "f1b2c3d4e5f60718293a4b5c6d7e8f90",
            "Kind": "Notice",
            "Status": "Done",
            "Text": "已用 Claude Code 2.1.297 自動接續",
            "Attachments": [],
            "Questions": [],
            "Answers": {},
            "CanAllowForSession": true,
            "AnswerAsMessage": false,
            "MediaPaths": [],
            "PlanSteps": [],
            "CreatedAt": {T2},
            "UpdatedAt": {T2},
            "NoticeKind": "CliUpdate"
          }
        ]
        """;

    private const string OldQueue = """
        {
          "Queue": [
            {
              "Id": "q1",
              "Text": "接著幫我跑測試",
              "Attachments": [
                {
                  "Id": "c2b2c3d4e5f60718293a4b5c6d7e8f90",
                  "FileName": "資料.csv",
                  "StoredPath": "C:\\Users\\U\\AppData\\Local\\Myriagent\\conversations\\5f3a0c1e2b4d4f6a8b9c0d1e2f3a4b5c\\attachments\\資料.csv",
                  "Kind": "Document",
                  "SizeBytes": 12,
                  "ModelImagePaths": [],
                  "FrameSeconds": []
                }
              ]
            }
          ],
          "PendingAttachments": [],
          "PendingFeedback": [
            {
              "ToolName": "執行指令",
              "Title": "git push",
              "Feedback": "先不要推"
            }
          ]
        }
        """;
}
