using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 第 5 階段的對話事件（計畫 5.2、5.3；docs/sync-protocol.md 第 16 節；WP5.1）：伺服器寫的 conv.lease 的形狀（交接、強制接手、還原後的認領）、
/// logs／queue 可以換成 blob 參照再以 TryReadLease 換回；裝置不能送 conv.lease；conv.meta 的 transcript 與 workingDirectoryRef 兩個選用欄位
/// （前一版忽略、沒傳時線上格式逐位元組不變）
/// </summary>
public sealed class Phase5ConversationEventTests
{
    private const string Conv = "5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d";

    private const string DevA = "d_7K3M0000000000000000000000";

    private const string DevB = "d_8P2Q0000000000000000000000";

    private const string Hid = "01J9QZ3K8M5R7T2V4W6X8Y0A1B";

    private const string Sid = "ed703f02-0092-409f-907d-3e6ab753ef6c";

    private static ConversationLeasePayload Transfer() => new(
        DevB, 3, DevA, 2, LeaseReasons.Transfer, Hid, HandoffModes.AfterTurn, false, new HandoffInitiator(HandoffInitiatorKinds.Device, DevB), "OFFICE-PC", "HOME-PC",
        "la_7K3M0000000000000000000000",
        [
            HandoffMessagesTests.Head("cl:" + Sid),
            HandoffMessagesTests.Head(LogIdRules.ClaudeCompanion(Sid, "subagents/agent-1.jsonl")),
            HandoffMessagesTests.Head(LogIdRules.ClaudeCompanion(Sid, "tool-results/toolu_01.txt")) with { RelPath = "tool-results/toolu_01.txt" },
            HandoffMessagesTests.Head(LogIdRules.Attachment(Conv, "截圖.png")) with { RelPath = "截圖.png", RefId = "att1" },
        ],
        Workspace: new HandoffWorkspace("git:github.com/a/b", "src", "feature/x", "1a2b3c4", "origin/feature/x", 2, 0, 3, 1, ["src/a.cs"]),
        Queue: [new HandoffQueueEntry("q1", "下一則", ["att1"]), new HandoffQueueEntry("q2", "（已隱藏）", Secret: true)],
        Skipped: new HandoffSkipped(["big.iso"]),
        CliVersion: "2.1.295");

    private static ConversationLeasePayload Takeover() => new(DevB, 3, DevA, 2, LeaseReasons.Takeover, Hid,
        Initiator: new HandoffInitiator(HandoffInitiatorKinds.Browser, Label: "瀏覽器"), Logs: [HandoffMessagesTests.Head("cl:" + Sid)],
        Transcript: TranscriptStates.Damaged, Fence: new HandoffFence(81234, 5521));

    private static ConversationLeasePayload Restore() => new(DevB, 5, DevA, 1, LeaseReasons.Restore);

    private static string Json(object payload) => ConversationWire.Serialize(payload);

    private static string? Validate(object payload) => ConversationEventRules.Validate(ConversationEventKinds.Lease, payload is string s ? s : Json(payload));

    private static JObject Obj(object payload) => JObject.Parse(Json(payload));

    // ── conv.lease：種類 ──

    [Fact]
    public void conv_lease是meta類_只由伺服器寫()
    {
        ConversationEventKinds.Lease.Should().Be("conv.lease");
        ConversationEventKinds.ClassOf(ConversationEventKinds.Lease).Should().Be(SyncClasses.Meta);
        ConversationEventKinds.Placement(ConversationEventKinds.Lease, Conv).Should().Be((SyncClasses.Meta, "c:" + Conv));
        ConversationEventKinds.All.Where(ConversationEventKinds.IsServerAuthored).Should().Equal(ConversationEventKinds.Lease);
        SyncAuthors.Server.Should().Be("server");
    }

    [Fact]
    public void 裝置寫進outbox的conv_lease一律拒絕()
    {
        var append = new OutboxAppend("c:" + Conv, ConversationEventKinds.Lease, Json(Transfer()), null, Conv, 3);
        OutboxAppendRules.Problem(append).Should().NotBeNull("伺服器寫的對話事件裝置不能送");
        OutboxAppendRules.Problem(append with { Kind = ConversationEventKinds.Meta }).Should().BeNull("陽性對照：同樣的外框換成 conv.meta 可以");
    }

    // ── conv.lease：合格的形狀 ──

    [Fact]
    public void 交接_強制接手_還原後的認領_都合格()
    {
        Validate(Transfer()).Should().BeNull();
        Validate(Transfer() with { Mode = HandoffModes.Now, AutoContinue = true, Interruption = new HandoffInterruption(["Bash"], 1, false) }).Should().BeNull();
        Validate(Takeover()).Should().BeNull();
        Validate(Restore()).Should().BeNull();
        Validate(Restore() with { Epoch = 2 }).Should().BeNull("認領只要 epoch 變大");
        Validate(Transfer() with { Logs = [] }).Should().BeNull("沒有日誌（逐字稿 missing 時）");
        Validate(Transfer() with { Transcript = "future" }).Should().BeNull("不認得的逐字稿狀態照收（讀的一方當成不能搬）");
        Validate(Obj(Transfer()).With("later", new JObject { ["x"] = 1 })).Should().BeNull("未知的欄位照舊忽略");
    }

    public static TheoryData<string, string> BadLeases()
    {
        var data = new TheoryData<string, string>();
        void Add(string because, JObject o) => data.Add(because, o.ToString(Newtonsoft.Json.Formatting.None));

        Add("reason 不認得", Obj(Transfer()).With("reason", "release"));
        Add("owner 不是 deviceId", Obj(Transfer()).With("owner", "server"));
        Add("owner 等於 prevOwner", Obj(Transfer()).With("owner", DevA));
        Add("沒有 prevOwner", Obj(Transfer()).Without("prevOwner"));
        Add("交接的 epoch 不是＋1", Obj(Transfer()).With("epoch", 4));
        Add("epoch 沒變", Obj(Restore()).With("epoch", 1));
        Add("prevEpoch 是 0", Obj(Transfer()).With("prevEpoch", 0).With("epoch", 1));
        Add("交接沒有 handoffId", Obj(Transfer()).Without("handoffId"));
        Add("handoffId 不是 ULID", Obj(Transfer()).With("handoffId", "h-1"));
        Add("還原帶了 handoffId", Obj(Restore()).With("handoffId", Hid));
        Add("交接沒有 mode", Obj(Transfer()).Without("mode"));
        Add("mode 不認得", Obj(Transfer()).With("mode", "later"));
        Add("強制接手帶了 mode", Obj(Takeover()).With("mode", HandoffModes.Now));
        Add("afterTurn 帶了 autoContinue", Obj(Transfer()).With("autoContinue", true));
        Add("autoContinue 不是布林", Obj(Transfer()).With("mode", "now").With("autoContinue", "yes"));
        Add("還原帶了 initiator", Obj(Restore()).With("initiator", new JObject { ["kind"] = "device" }));
        Add("initiator 的 kind 不認得", Obj(Transfer()).With("initiator", new JObject { ["kind"] = "phone" }));
        Add("名稱太長", Obj(Transfer()).With("toName", new string('n', 61)));
        Add("邏輯帳戶不合格", Obj(Transfer()).With("logicalAccountId", "acc-1"));
        Add("強制接手沒有 fence", Obj(Takeover()).Without("fence"));
        Add("交接帶了 fence", Obj(Transfer()).With("fence", new JObject { ["atSeq"] = 1, ["lastDeviceSeq"] = 0 }));
        Add("fence 的 atSeq 是 0", Obj(Takeover()).With("fence", new JObject { ["atSeq"] = 0, ["lastDeviceSeq"] = 0 }));
        Add("logs 不是陣列", Obj(Transfer()).With("logs", "x"));
        Add("logKey 對不上", Obj(Transfer()).WithLog(0, "logKey", new string('0', 64)));
        Add("kind 對不上", Obj(Transfer()).WithLog(0, "kind", LogKinds.ClaudeFile));
        Add("伴隨檔標成主檔", Obj(Transfer()).WithLog(2, "main", true));
        Add("gen 是 0", Obj(Transfer()).WithLog(0, "gen", 0));
        Add("length 是負的", Obj(Transfer()).WithLog(0, "length", -1));
        Add("chain 不是 32 位元組", Obj(Transfer()).WithLog(0, "chain", Convert.ToBase64String(new byte[16])));
        Add("fullSha256 大寫", Obj(Transfer()).WithLog(0, "fullSha256", new string('A', 64)));
        Add("relPath 與 logId 不符", Obj(Transfer()).WithLog(3, "relPath", "other.png"));
        Add("refId 不合格", Obj(Transfer()).WithLog(3, "refId", "a/b"));
        Add("logId 有 ..", Obj(Transfer()).WithLog(0, "logId", "fa:" + Conv + "/../x"));
        Add("同一個日誌兩次", Obj(Transfer()).With("logs", new JArray(Obj(Transfer())["logs"]![0]!.DeepClone(), Obj(Transfer())["logs"]![0]!.DeepClone())));
        Add("queue 的 id 不合格", Obj(Transfer()).With("queue", new JArray(new JObject { ["id"] = "q/1", ["text"] = "x" })));
        Add("queue 沒有 text", Obj(Transfer()).With("queue", new JArray(new JObject { ["id"] = "q1" })));
        Add("queue 的附件 id 不合格", Obj(Transfer()).With("queue", new JArray(new JObject { ["id"] = "q1", ["text"] = "x", ["attachments"] = new JArray("a b") })));
        Add("queue 超過 50 則", Obj(Transfer()).With("queue", new JArray(Enumerable.Range(0, 51).Select(i => new JObject { ["id"] = "q" + i, ["text"] = "x" }))));
        Add("dirtyFiles 超過 20 個", Obj(Transfer()).With("workspace", new JObject { ["dirtyFiles"] = new JArray(Enumerable.Range(0, 21).Select(i => "f" + i)) }));
        Add("ahead 是負的", Obj(Transfer()).With("workspace", new JObject { ["ahead"] = -1 }));
        Add("interruption 沒有 tools", Obj(Transfer()).With("interruption", new JObject { ["pendingRequests"] = 0, ["backgroundStopped"] = false }));
        Add("skipped 的檔名是空的", Obj(Transfer()).With("skipped", new JObject { ["workspaceFiles"] = new JArray("") }));
        Add("cliVersion 太長", Obj(Transfer()).With("cliVersion", new string('1', 200)));
        Add("transcript 不是名稱", Obj(Transfer()).With("transcript", "held!"));
        return data;
    }

    [Theory]
    [MemberData(nameof(BadLeases))]
    public void 不合格的conv_lease(string because, string json) => Validate(json).Should().Be(SyncErrorCodes.Invalid, because);

    [Fact]
    public void logs最多2000筆()
    {
        var logs = Enumerable.Range(0, ConversationEventRules.MaxLeaseLogs).Select(i => HandoffMessagesTests.Head(LogIdRules.ClaudeCompanion(Sid, $"tool-results/t{i}.txt"))).ToList();
        ConversationEventRules.CheckLeaseShape(Obj(Transfer() with { Logs = logs }), allowBlobs: false).Should().BeNull();
        logs.Add(HandoffMessagesTests.Head(LogIdRules.ClaudeCompanion(Sid, "tool-results/extra.txt")));
        ConversationEventRules.CheckLeaseShape(Obj(Transfer() with { Logs = logs }), allowBlobs: false).Should().NotBeNull();
    }

    // ── conv.lease：blob 參照 ──

    [Fact]
    public void logs與queue可以換成blob參照_TryReadLease換回再驗()
    {
        var logs = Enumerable.Range(0, ConversationEventRules.MaxLeaseLogs).Select(i => HandoffMessagesTests.Head(LogIdRules.ClaudeCompanion(Sid, $"tool-results/t{i}.txt"))).ToList();
        var lease = Transfer() with { Logs = logs };
        Validate(lease).Should().Be(SyncErrorCodes.TooLarge, "2,000 筆遠超過 128 KB：要換成 blob");

        var blobs = new Dictionary<string, byte[]>();
        var payload = Obj(lease);
        foreach (var name in ConversationEventRules.LeaseBlobFields)
            payload[name] = BlobRef(payload[name]!, blobs);
        Validate(payload.ToString(Newtonsoft.Json.Formatting.None)).Should().BeNull("blob 參照的外形合格");

        ConversationWire.TryReadLease(payload, sha => blobs.GetValueOrDefault(sha), out var back).Should().BeTrue();
        back!.Logs.Should().HaveCount(ConversationEventRules.MaxLeaseLogs).And.Equal(logs);
        back.Queue.Should().BeEquivalentTo(lease.Queue);
        back.Owner.Should().Be(DevB);
        payload["logs"]!.Type.Should().Be(JTokenType.Object, "TryReadLease 不改呼叫端的物件");
    }

    [Fact]
    public void blob拿不到_雜湊不符_或內容不合格_TryReadLease失敗()
    {
        var blobs = new Dictionary<string, byte[]>();
        var payload = Obj(Transfer());
        payload["logs"] = BlobRef(payload["logs"]!, blobs);
        var sha = (string)payload["logs"]!["blob"]!;

        ConversationWire.TryReadLease(payload, _ => null, out _).Should().BeFalse("拿不到");
        ConversationWire.TryReadLease(payload, _ => Encoding.UTF8.GetBytes("[]"), out _).Should().BeFalse("雜湊不符");

        var badLogs = new JArray(new JObject { ["logId"] = "cl:" + Sid, ["logKey"] = new string('0', 64) });
        var bad = new Dictionary<string, byte[]>();
        var withBad = Obj(Transfer());
        withBad["logs"] = BlobRef(badLogs, bad);
        ConversationWire.TryReadLease(withBad, s => bad.GetValueOrDefault(s), out _).Should().BeFalse("換回之後照樣驗形狀");

        var nested = new Dictionary<string, byte[]>();
        var withNested = Obj(Transfer());
        withNested["logs"] = BlobRef(new JObject { ["blob"] = sha, ["length"] = 2 }, nested);
        ConversationWire.TryReadLease(withNested, s => nested.GetValueOrDefault(s), out _).Should().BeFalse("blob 的內容要是陣列，不能再是 blob 參照");

        ConversationWire.TryReadLease(Obj(Transfer()), _ => null, out var direct).Should().BeTrue("沒有 blob 的照樣讀");
        direct!.Should().BeEquivalentTo(Transfer());
    }

    private static JObject BlobRef(JToken content, Dictionary<string, byte[]> blobs)
    {
        var bytes = Encoding.UTF8.GetBytes(content.ToString(Newtonsoft.Json.Formatting.None));
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        blobs[sha] = bytes;
        return new JObject { ["blob"] = sha, ["length"] = bytes.LongLength };
    }

    // ── conv.meta 的兩個新欄位 ──

    private static readonly DateTime Time = new(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);

    private static AgentConversation Custom() => new()
    {
        Title = "x", AccountId = "acc-1", Provider = AgentProvider.Claude, WorkingDirectory = @"C:\repo\proj\src", CreatedAt = Time, UpdatedAt = Time,
        WorkingDirectoryRef = new PathRef(PathRefKinds.Git, "git:github.com/example-user/proj", "src", "proj"),
    };

    [Fact]
    public void 沒傳時線上格式不變_傳了才帶_而且在最後()
    {
        var meta = Custom();
        var plain = ConversationWire.Serialize(ConversationWire.Meta(meta, "Claude", false, false));
        plain.Should().NotContain("transcript").And.NotContain("workingDirectoryRef", "WP5.3／5.4 才傳值：第 4 階段的投影逐位元組不變");

        var payload = ConversationWire.Meta(meta, "Claude", false, false, transcript: TranscriptStates.Missing, workingDirectoryRef: meta.WorkingDirectoryRef);
        var json = ConversationWire.Serialize(payload);
        JObject.Parse(json).Properties().Select(p => p.Name).TakeLast(2).Should().Equal("transcript", "workingDirectoryRef");
        json.Should().EndWith("""
            "transcript":"missing","workingDirectoryRef":{"kind":"git","repoKey":"git:github.com/example-user/proj","sub":"src","display":"proj"}}
            """);
        ConversationEventRules.Validate(ConversationEventKinds.Meta, json).Should().BeNull();
    }

    [Fact]
    public void 投影_只送認得的逐字稿狀態_資料夾參照只在自訂資料夾時()
    {
        var meta = Custom();
        ConversationWire.Meta(meta, "Claude", false, false, transcript: TranscriptStates.Held).Transcript.Should().Be("held");
        ConversationWire.Meta(meta, "Claude", false, false, transcript: TranscriptStates.Damaged).Transcript.Should().BeNull("damaged 只在伺服器判定");
        ConversationWire.Meta(meta, "Claude", false, false, transcript: "whatever").Transcript.Should().BeNull();

        meta.WorkingDirectory = null;
        ConversationWire.Meta(meta, "Claude", false, false, workingDirectoryRef: meta.WorkingDirectoryRef).WorkingDirectoryRef.Should().BeNull("對話自己的資料夾不帶");

        var custom = Custom();
        ConversationWire.Meta(custom, "Claude", false, false, workingDirectoryRef: new PathRef(PathRefKinds.Path)).WorkingDirectoryRef.Should().Be(new PathRef("path"));
        ConversationWire.Meta(custom, "Claude", false, false, workingDirectoryRef: new PathRef(PathRefKinds.Git)).WorkingDirectoryRef.Should().BeNull("git 一定要有 repoKey");
        ConversationWire.Meta(custom, "Claude", false, false, workingDirectoryRef: new PathRef("svn", "x")).WorkingDirectoryRef.Should().BeNull("這版不認得的種類不送");
        ConversationWire.Meta(custom, "Claude", false, false, workingDirectoryRef: new PathRef(PathRefKinds.Git, new string('k', 513))).WorkingDirectoryRef.Should().BeNull("太長");
        ConversationWire.Meta(custom, "Claude", false, false, workingDirectoryRef: new PathRef(PathRefKinds.Git, "git:a/b", Display: new string('名', 300)))
            .WorkingDirectoryRef!.Display.Should().HaveLength(ConversationEventRules.MaxTitleLength);
    }

    [Fact]
    public void 驗證_transcript是名稱_workingDirectoryRef只在自訂資料夾時()
    {
        var baseJson = Obj(ConversationWire.Meta(Custom(), "Claude", false, false));
        string? Check(JObject o) => ConversationEventRules.Validate(ConversationEventKinds.Meta, o.ToString(Newtonsoft.Json.Formatting.None));

        Check(baseJson.With("transcript", "held")).Should().BeNull();
        Check(baseJson.With("transcript", "someday")).Should().BeNull("新版的值照收");
        Check(baseJson.With("transcript", 1)).Should().Be("invalid");
        Check(baseJson.With("transcript", "")).Should().Be("invalid");

        Check(baseJson.With("workingDirectoryRef", new JObject { ["kind"] = "git", ["repoKey"] = "git:a/b" })).Should().BeNull();
        Check(baseJson.With("workingDirectoryRef", new JObject { ["kind"] = "path" })).Should().BeNull();
        Check(baseJson.With("workingDirectoryRef", new JObject { ["kind"] = "svn" })).Should().BeNull("新版的種類照收");
        Check(baseJson.With("workingDirectoryRef", new JObject { ["kind"] = "git" })).Should().Be("invalid", "git 一定要有 repoKey");
        Check(baseJson.With("workingDirectoryRef", "git")).Should().Be("invalid");
        Check(baseJson.With("workingDirectoryRef", new JObject { ["kind"] = "path", ["sub"] = "" })).Should().Be("invalid");
        Check(baseJson.With("customFolder", false).Without("workingDirectory").With("workingDirectoryRef", new JObject { ["kind"] = "path" }))
            .Should().Be("invalid", "只有自訂資料夾才有參照");
    }
}

internal static class Phase5JsonExtensions
{
    public static JObject With(this JObject o, string name, JToken value)
    {
        var copy = (JObject)o.DeepClone();
        copy[name] = value;
        return copy;
    }

    public static JObject Without(this JObject o, string name)
    {
        var copy = (JObject)o.DeepClone();
        copy.Remove(name);
        return copy;
    }

    /// <summary>logs[index] 的一個欄位換成 <paramref name="value"/></summary>
    public static JObject WithLog(this JObject o, int index, string name, JToken value)
    {
        var copy = (JObject)o.DeepClone();
        copy["logs"]![index]![name] = value;
        return copy;
    }
}
