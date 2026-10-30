using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 第 5 階段的訊息形狀（計畫 5.2、5.5、5.9；docs/sync-protocol.md 第 16 節；WP5.1）：線上一律 camelCase、null 省略，欄位名稱照計畫；
/// 代碼表與各列舉的值；不認得的欄位照舊忽略（新版多送的）
/// </summary>
public sealed class HandoffMessagesTests
{
    private const string Hid = "01J9QZ3K8M5R7T2V4W6X8Y0A1B";

    private const string Conv = "5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d";

    private const string DevA = "d_7K3M0000000000000000000000";

    private const string DevB = "d_8P2Q0000000000000000000000";

    private static IEnumerable<string> Keys(object message) => JObject.Parse(SyncJson.Serialize(message)).Properties().Select(p => p.Name);

    [Fact]
    public void 代碼表照計畫5點9()
    {
        HandoffCodes.All.Should().Equal(
            "busy", "owner_online", "owner_offline", "target_offline", "target_outdated", "no_account", "policy", "unsupported_provider", "not_full",
            "not_found", "deleted", "same_device", "log_mismatch", "events_missing", "log_damaged", "expired", "cancelled", "server_restart", "not_available");
        HandoffCodes.All.Should().OnlyHaveUniqueItems().And.OnlyContain(c => HandoffCodes.IsKnown(c));
        HandoffCodes.IsKnown("Busy").Should().BeFalse();
        HandoffCodes.IsKnown(null).Should().BeFalse();
    }

    [Fact]
    public void 列舉的值()
    {
        HandoffKinds.All.Should().Equal("transfer", "takeover");
        HandoffModes.All.Should().Equal("afterTurn", "now");
        HandoffStates.All.Should().Equal("requested", "accepted", "draining", "ready", "committed", "cancelled", "aborted", "expired");
        LeaseReasons.All.Should().Equal("transfer", "takeover", "restore");
        TranscriptStates.MetaValues.Should().Equal("missing", "held", "unportable");
        TranscriptStates.LeaseValues.Should().Equal("missing", "held", "unportable", "damaged");
        TranscriptStates.PreviewValues.Should().Equal("ok", "missing", "held", "unportable", "damaged", "none");
        HandoffTargetReasons.All.Should().Equal("offline", "outdated", "no_account", "policy", "owner");
        HandoffProgressStates.SourceStates.Should().Equal("accepted", "draining", "aborted");
        HandoffProgressStates.TargetStates.Should().Equal("importing", "needsFolder", "imported", "importFailed");
    }

    [Fact]
    public void 進度的狀態要配角色()
    {
        foreach (var state in HandoffProgressStates.SourceStates)
        {
            HandoffProgressStates.IsValid(HandoffRoles.Source, state).Should().BeTrue(state);
            HandoffProgressStates.IsValid(HandoffRoles.Target, state).Should().BeFalse(state);
        }

        foreach (var state in HandoffProgressStates.TargetStates)
        {
            HandoffProgressStates.IsValid(HandoffRoles.Target, state).Should().BeTrue(state);
            HandoffProgressStates.IsValid(HandoffRoles.Source, state).Should().BeFalse(state);
        }

        HandoffProgressStates.IsValid(HandoffRoles.Fork, HandoffProgressStates.Accepted).Should().BeFalse("fork 只在本機");
        HandoffProgressStates.IsValid(null, HandoffProgressStates.Accepted).Should().BeFalse();
    }

    [Fact]
    public void 裝置到伺服器的請求_欄位名稱照計畫5點5()
    {
        SyncJson.Serialize(new HandoffPreviewRequest(Conv)).Should().Be($$"""{"conversationId":"{{Conv}}"}""");
        SyncJson.Serialize(new HandoffStartRequest(Hid, Conv, DevB, HandoffModes.AfterTurn, Force: false, AutoContinue: false)).Should().Be(
            $$"""{"commandId":"{{Hid}}","conversationId":"{{Conv}}","toDeviceId":"{{DevB}}","mode":"afterTurn","force":false,"autoContinue":false}""");
        SyncJson.Serialize(new HandoffCancelRequest(Hid)).Should().Be($$"""{"handoffId":"{{Hid}}"}""");
        SyncJson.Serialize(new HandoffProgress(Hid, HandoffRoles.Source, HandoffProgressStates.Accepted)).Should().Be(
            $$"""{"handoffId":"{{Hid}}","role":"source","state":"accepted"}""", "null 省略");
        Keys(new HandoffReady(Hid, 5521, [Head()], TranscriptStates.Held, new HandoffWorkspace(Branch: "main"), new HandoffInterruption(["Bash"], 1, false),
                [new HandoffQueueEntry("q1", "下一則")], new HandoffSkipped(["big.iso"]), "2.1.295"))
            .Should().Equal("handoffId", "finalDeviceSeq", "logs", "transcript", "workspace", "interruption", "queue", "skipped", "cliVersion");
    }

    [Fact]
    public void 伺服器的回應與送給裝置的訊息_欄位名稱照計畫5點5()
    {
        Keys(new HandoffPreviewResult("Claude", DevA, false, 1791587000000, true, TranscriptStates.Ok, 1791586000000, "2.1.295",
                [new HandoffTarget(DevB, "HOME-PC", true)], null))
            .Should().Equal("provider", "ownerDeviceId", "ownerOnline", "ownerLastSeenAt", "canForce", "transcript", "transcriptAt", "cliVersion", "targets");
        SyncJson.Serialize(new HandoffStartResult(Hid)).Should().Be($$"""{"handoffId":"{{Hid}}"}""");
        SyncJson.Serialize(new HandoffStartResult(null, HandoffCodes.Busy)).Should().Be("""{"error":"busy"}""");
        SyncJson.Serialize(new HandoffAck(true)).Should().Be("""{"ok":true}""");
        Keys(new HandoffRequest(Hid, Conv, 2, HandoffModes.AfterTurn, DevB, "HOME-PC", 1791588800000, new HandoffInitiator(HandoffInitiatorKinds.Browser, Label: "瀏覽器")))
            .Should().Equal("handoffId", "conversationId", "epoch", "mode", "toDeviceId", "toName", "deadlineAt", "initiator");
        Keys(new HandoffUpdate(Hid, Conv, HandoffKinds.Transfer, HandoffStates.Draining, DevA, DevB, HandoffModes.AfterTurn, 1791588800000, null, "OFFICE-PC 有 3 個未提交的檔案"))
            .Should().Equal("handoffId", "conversationId", "kind", "state", "fromDeviceId", "toDeviceId", "mode", "deadlineAt", "detail");
        SyncJson.Serialize(new LogRepair(LogIdRules.LogKey("cl:a"), "cl:a", LogRepairReasons.Damaged)).Should().Be(
            $$"""{"logKey":"{{LogIdRules.LogKey("cl:a")}}","logId":"cl:a","reason":"damaged"}""");
    }

    [Fact]
    public void 往返與新版多送的欄位照舊忽略()
    {
        var ready = new HandoffReady(Hid, 5521, [Head()], Queue: [new HandoffQueueEntry("q1", "下一則", ["att-1"], Secret: true)]);
        var back = SyncJson.Deserialize<HandoffReady>(SyncJson.Serialize(ready))!;
        back.Logs.Should().ContainSingle().Which.Should().Be(ready.Logs[0]);
        back.Queue.Should().ContainSingle().Which.Should().BeEquivalentTo(ready.Queue![0]);

        SyncJson.Deserialize<HandoffStartResult>("""{"handoffId":"x","later":{"a":1}}""").Should().Be(new HandoffStartResult("x"));
        SyncJson.Deserialize<HandoffUpdate>(SyncJson.Serialize(new HandoffUpdate(Hid, Conv, HandoffKinds.Takeover, HandoffStates.Committed, DevA, DevB, ImportState: "imported")))!
            .ImportState.Should().Be("imported");
    }

    [Fact]
    public void conv_lease的payload_欄位名稱照計畫5點2()
    {
        var lease = new ConversationLeasePayload(DevB, 3, DevA, 2, LeaseReasons.Transfer, Hid, HandoffModes.AfterTurn, false,
            new HandoffInitiator(HandoffInitiatorKinds.Device, DevB), "OFFICE-PC", "HOME-PC", "la_7K3M0000000000000000000000", [Head()], null,
            new HandoffWorkspace("git:github.com/a/b", "src", "feature/x", "1a2b3c4", "origin/feature/x", 2, 0, 3, 1, ["src/a.cs"]),
            null, [new HandoffQueueEntry("q1", "…", ["att-1"])], new HandoffSkipped(["big.iso"], []), null, "2.1.295");

        Keys(lease).Should().Equal(
            "owner", "epoch", "prevOwner", "prevEpoch", "reason", "handoffId", "mode", "autoContinue", "initiator", "fromName", "toName", "logicalAccountId",
            "logs", "workspace", "queue", "skipped", "cliVersion");
        var json = JObject.Parse(SyncJson.Serialize(lease));
        ((JObject)json["logs"]![0]!).Properties().Select(p => p.Name).Should().Equal("logId", "logKey", "kind", "main", "gen", "length", "chain", "fullSha256");
        ((JObject)json["workspace"]!).Properties().Select(p => p.Name).Should().Equal(
            "repoKey", "sub", "branch", "head", "upstream", "ahead", "behind", "dirty", "untracked", "dirtyFiles");
        ((JObject)json["initiator"]!).Properties().Select(p => p.Name).Should().Equal("kind", "deviceId");
    }

    internal static LogHead Head(string logId = "cl:ed703f02-0092-409f-907d-3e6ab753ef6c", long length = 129629)
        => new(logId, LogIdRules.LogKey(logId), LogIdRules.KindOf(logId)!, LogIdRules.KindOf(logId) is LogKinds.ClaudeMain or LogKinds.CodexRollout, 1, length,
            LogChainRules.ToWire(LogChainRules.Initial(logId, 1)), new string('a', 64));
}
