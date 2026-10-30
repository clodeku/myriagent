using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Myriagent.Agents;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 第 4 階段的裝置 hub 訊息（docs/sync-protocol.md 14.1、14.3、14.6；計畫步 8）：新欄位都加在記錄最後、有預設值，
/// 沒宣告功能（null）時 Hello、HelloResponse、device.upsert 的 JSON 與第 3 階段逐位元組相同；新訊息的線上格式與往返；
/// 方法名稱與文件的方法表一致；即時更新的檢查（項目的列舉只收名稱，D6、D9）。
/// </summary>
public sealed partial class Phase4HubMessagesTests
{
    private const long ServerTime = 1_791_580_000_000;
    private const string ConversationId = "5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d";
    private const string Cid = "01J9QZ3K8M5R7T2V4W6X8Y0A1B";

    /// <summary>第 3 階段（WP4.1 之前）的 Hello：逐字寫死，新欄位是 null 時一個位元組都不能變（這個測試先在舊的記錄上跑過）</summary>
    private const string Phase3Hello =
        "{\"protocolVersion\":1,\"deviceId\":\"d_7K3M\",\"bootId\":\"4f2a9c1e0b7d\",\"appVersion\":\"0.3.0\",\"machineName\":\"OFFICE-PC\",\"nonceD\":\"nonce\","
        + "\"observedSpkiSha256\":\"spki\",\"outbox\":{\"nextDeviceSeq\":10443,\"oldestUnackedDeviceSeq\":10391,\"oldestRetainedDeviceSeq\":9120},"
        + "\"cursors\":{\"catalog\":1200,\"usage\":5},\"policy\":{\"remoteControlLevel\":\"Control\",\"allowRemoteFullAccess\":true}}";

    private const string Phase3HelloResponse =
        "{\"serverTime\":1791580000000,\"nonceS\":\"ns\",\"server\":{\"instanceId\":\"inst\",\"restoreGeneration\":2},\"hubSig\":\"sig\",\"hubIdentitySpki\":\"spki\","
        + "\"trustRevs\":{\"registry\":3},\"limits\":{\"maxBatchBytes\":262144,\"maxBatchEvents\":500,\"maxInlinePayload\":131072,\"maxLiveHz\":4,"
        + "\"maxInFlightBatches\":4,\"maxPullEvents\":500,\"maxPullBytes\":1048576},\"minAppVersion\":\"0.1.0\","
        + "\"rotation\":{\"oldSpki\":\"old\",\"newSpki\":\"new\",\"sig\":\"rs\"}}";

    private const string Phase3DeviceUpsert =
        "{\"deviceId\":\"d_1\",\"name\":\"OFFICE-PC\",\"machineName\":\"OFFICE\",\"appVersion\":\"0.3.0\",\"keyStorage\":\"tpm\",\"fingerprint\":\"ABCD-EFGH\","
        + "\"createdAt\":1791580000000,\"lastSeenAt\":1791580001000,\"online\":true,\"policy\":{\"remoteControlLevel\":\"Off\",\"allowRemoteFullAccess\":false},"
        + "\"cli\":{\"Claude\":{\"installed\":true,\"version\":\"2.1.297\",\"installKind\":\"native\",\"latest\":\"2.1.297\",\"updateAvailable\":false,\"runningOnOldVersion\":0,\"restartPending\":0,"
        + "\"checkedAt\":1791580000000}}}";

    [Fact]
    public void 沒宣告功能時_Hello與HelloResponse與device_upsert和第3階段逐位元組相同()
    {
        SyncJson.Serialize(Hello()).Should().Be(Phase3Hello);
        SyncJson.Serialize(Response()).Should().Be(Phase3HelloResponse);
        SyncJson.Serialize(Upsert()).Should().Be(Phase3DeviceUpsert);

        SyncJson.Deserialize<HelloRequest>(Phase3Hello).Should().BeEquivalentTo(Hello(), "第 3 階段的裝置送來的 Hello 照讀");
        SyncJson.Deserialize<HelloResponse>(Phase3HelloResponse).Should().BeEquivalentTo(Response());
        SyncJson.Deserialize<DeviceUpsertPayload>(Phase3DeviceUpsert).Should().BeEquivalentTo(Upsert());
        SyncJson.Deserialize<HelloRequest>(Phase3Hello)!.Features.Should().BeNull();
        SyncJson.Deserialize<HelloResponse>(Phase3HelloResponse)!.Features.Should().BeNull();
        SyncJson.Deserialize<DeviceUpsertPayload>(Phase3DeviceUpsert)!.Features.Should().BeNull();
    }

    [Fact]
    public void 即時增量的頻率_加在limits的最後_舊版忽略()
    {
        var response = Response() with { Limits = SyncLimits.Default };
        var json = SyncJson.Serialize(response);
        json.Should().Be(Phase3HelloResponse.Replace("\"maxPullBytes\":1048576}", "\"maxPullBytes\":1048576,\"maxLiveDeltaHz\":8}"));
        SyncJson.Deserialize<HelloResponse>(json)!.Limits.Should().Be(SyncLimits.Default);
        SyncJson.Deserialize<HelloResponse>(Phase3HelloResponse)!.Limits.MaxLiveDeltaHz.Should().BeNull("舊伺服器沒有這個欄位＝不提供");
    }

    [Fact]
    public void 功能宣告_加在最後_往返不變()
    {
        string[] features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1];

        var hello = Hello() with { Features = features };
        SyncJson.Serialize(hello).Should().Be(Phase3Hello[..^1] + ",\"features\":[\"conv1\",\"cmd1\"]}");
        SyncJson.Deserialize<HelloRequest>(SyncJson.Serialize(hello))!.Features.Should().Equal(features);

        var response = Response() with { Features = features };
        SyncJson.Serialize(response).Should().Be(Phase3HelloResponse[..^1] + ",\"features\":[\"conv1\",\"cmd1\"]}", "在 rotation 之後");
        SyncJson.Deserialize<HelloResponse>(SyncJson.Serialize(response))!.Features.Should().Equal(features);

        var upsert = Upsert() with { Features = features };
        SyncJson.Serialize(upsert).Should().Be(Phase3DeviceUpsert[..^1] + ",\"features\":[\"conv1\",\"cmd1\"]}");
        SyncJson.Deserialize<DeviceUpsertPayload>(SyncJson.Serialize(upsert))!.Features.Should().Equal(features);
    }

    [Fact]
    public void 指令的投遞_信封往返後照樣驗得過()
    {
        using var hub = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var body = new CommandEnvelopeBody("https://hub.example.com", Cid, "d_01J9QZ3K8M5R7T2V4W6X8Y0A1B", "conn-1", ConversationId, 1,
            new JObject { ["cmd"] = "rename", ["conversationId"] = ConversationId, ["title"] = "2026-10-18T00:00:00Z" }, 1, false, false,
            ServerTime, ServerTime, ServerTime + 30_000, new CommandIssuer(CommandIssuerKinds.Browser, "Chrome・Windows"), new CommandAuth(ServerTime, false));
        CommandEnvelope.TrySign(body, data => P256KeyRules.Sign(hub, data), out var envelope).Should().BeTrue();

        var json = SyncJson.Serialize(new CommandDelivery(Cid, envelope!));
        json.Should().StartWith("{\"commandId\":\"01J9QZ3K8M5R7T2V4W6X8Y0A1B\",\"envelope\":{\"v\":\"myriagent-hub-cmd-v1\",\"body\":\"{\\\"aud\\\":");
        JObject.Parse(json)["envelope"]!.ToObject<JObject>()!.Properties().Select(p => p.Name).Should().Equal("v", "body", "sig");

        var delivery = SyncJson.Deserialize<CommandDelivery>(json)!;
        delivery.Envelope.Body.Should().Be(envelope!.Body, "body 是字串，原樣往返（日期樣的字串不轉換）");
        var target = new CommandEnvelopeTarget(P256KeyRules.ExportSpki(hub), body.Aud, body.Dev, body.Conn);
        CommandEnvelope.Verify(delivery.Envelope, delivery.CommandId, target, new ServerTimeBounds(ServerTime, ServerTime + 500)).Ok.Should().BeTrue();
    }

    [Fact]
    public void 結果與輸出_null省略_不認得的欄位忽略()
    {
        SyncJson.Serialize(new CommandResult(Cid, "done")).Should().Be("{\"commandId\":\"01J9QZ3K8M5R7T2V4W6X8Y0A1B\",\"state\":\"done\"}");
        var rejected = new CommandResult(Cid, "rejected", "step_up_required", "要先重新輸入密碼", 2, true);
        SyncJson.Serialize(rejected).Should().Be(
            "{\"commandId\":\"01J9QZ3K8M5R7T2V4W6X8Y0A1B\",\"state\":\"rejected\",\"code\":\"step_up_required\",\"message\":\"要先重新輸入密碼\",\"tier\":2,\"fullAccess\":true}");
        SyncJson.Deserialize<CommandResult>(SyncJson.Serialize(rejected)).Should().Be(rejected);
        SyncJson.Deserialize<CommandResult>("{\"commandId\":\"01J9QZ3K8M5R7T2V4W6X8Y0A1B\",\"state\":\"done\",\"later\":{\"x\":1}}").Should().Be(new CommandResult(Cid, "done"));

        var message = new JObject { ["type"] = "files", ["requestId"] = "k21.r12", ["at"] = "2026-10-18T00:00:00Z", ["entries"] = new JArray() };
        var output = SyncJson.Serialize(new CommandOutput(Cid, message));
        output.Should().Be("{\"commandId\":\"01J9QZ3K8M5R7T2V4W6X8Y0A1B\",\"message\":{\"type\":\"files\",\"requestId\":\"k21.r12\",\"at\":\"2026-10-18T00:00:00Z\",\"entries\":[]}}");
        JToken.DeepEquals(SyncJson.Deserialize<CommandOutput>(output)!.Message, message).Should().BeTrue("網頁訊息原樣轉發，日期樣的字串不轉換");
    }

    [Fact]
    public void 即時更新_觀看_快照_媒體的線上格式()
    {
        var relay = new LiveRelay(ConversationId, 1, 5, 2, "{\"id\":\"a\"}");
        SyncJson.Serialize(relay).Should().Be("{\"conversationId\":\"5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d\",\"epoch\":1,\"ord\":5,\"ver\":2,\"item\":\"{\\\"id\\\":\\\"a\\\"}\"}");
        SyncJson.Deserialize<LiveRelay>(SyncJson.Serialize(relay)).Should().Be(relay);

        SyncJson.Serialize(new WatchChanged([ConversationId])).Should().Be("{\"conversationIds\":[\"5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d\"]}");
        SyncJson.Deserialize<WatchChanged>("{\"conversationIds\":[]}")!.ConversationIds.Should().BeEmpty();
        SyncJson.Serialize(new SnapshotRequest(ConversationId, SnapshotReasons.Open)).Should().Be("{\"conversationId\":\"5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d\",\"reason\":\"open\"}");
        SnapshotReasons.All.Should().Equal("restore", "open");

        var media = new MediaRequest("r_1", "01J9QZ3K8M5R7T2V4W6X8Y0A1B", MediaPurposes.Thumb, new MediaRange(0, 1023));
        SyncJson.Serialize(media).Should().Be("{\"relayId\":\"r_1\",\"ref\":\"01J9QZ3K8M5R7T2V4W6X8Y0A1B\",\"purpose\":\"thumb\",\"range\":{\"start\":0,\"end\":1023}}");
        SyncJson.Deserialize<MediaRequest>(SyncJson.Serialize(media)).Should().Be(media);
        SyncJson.Serialize(media with { Range = null }).Should().NotContain("range");
        SyncJson.Serialize(new MediaRange(4096)).Should().Be("{\"start\":4096}", "end 是 null＝到結尾");
        MediaPurposes.All.Should().Equal("media", "thumb", "file");
    }

    [Fact]
    public void 方法名稱與文件的方法表一致_方向也相同()
    {
        var constants = typeof(DeviceHubMethods).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);
        constants.Should().AllSatisfy(p => p.Value.Should().Be(p.Key, "常數名稱就是方法名稱"));
        DeviceHubMethods.DeviceToServer.Concat(DeviceHubMethods.ServerToDevice).Should().BeEquivalentTo(constants.Values).And.OnlyHaveUniqueItems();

        var sp = File.ReadAllText(Path.Combine(MyriagentHubExe.RepoRoot(), "docs", "sync-protocol.md"));
        var rows = MethodRow().Matches(sp).Select(m => (Direction: m.Groups[1].Value, Name: m.Groups[2].Value)).ToList();
        rows.Where(r => r.Direction == "裝置→伺服器").Select(r => r.Name).Should().BeEquivalentTo(DeviceHubMethods.DeviceToServer, "sync-protocol 第 4 節與 14.3、14.6 的表");
        rows.Where(r => r.Direction == "伺服器→裝置").Select(r => r.Name).Should().BeEquivalentTo(DeviceHubMethods.ServerToDevice);
    }

    [Fact]
    public void 即時更新的檢查_合格的項目可以_項目的列舉只收名稱()
    {
        var item = ApprovalJson();
        LiveRules.CheckRelay(Relay(item), out var detail).Should().BeNull(detail);
        item.Should().Contain("\"risk\":\"High\"");

        LiveRules.CheckRelay(Relay(item.Replace("\"risk\":\"High\"", "\"risk\":1", StringComparison.Ordinal)), out _).Should().Be(SyncErrorCodes.Invalid, "數值列舉一律拒絕（D6、D9）");
        LiveRules.CheckRelay(Relay(item.Replace("\"risk\":\"High\"", "\"risk\":\"1\"", StringComparison.Ordinal)), out _).Should().Be(SyncErrorCodes.Invalid);
        LiveRules.CheckRelay(Relay(item.Replace("\"kind\":\"Approval\"", "\"kind\":5", StringComparison.Ordinal)), out _).Should().Be(SyncErrorCodes.Invalid);
        LiveRules.CheckRelay(Relay(item.Replace("\"risk\":\"High\"", "\"risk\":\"Critical\"", StringComparison.Ordinal)), out _).Should().BeNull("新版的名稱照收，讀的一方當成沒有值");
    }

    [Fact]
    public void 即時更新的檢查_外層與項目不一致_格式不對_太大()
    {
        var item = ApprovalJson();

        LiveRules.CheckRelay(Relay(item) with { Ord = 6 }, out _).Should().Be(SyncErrorCodes.Invalid, "item 的 ord 是 5");
        LiveRules.CheckRelay(Relay(item) with { Ver = 3 }, out _).Should().Be(SyncErrorCodes.Invalid);
        LiveRules.CheckRelay(Relay(item) with { ConversationId = ConversationId.ToUpperInvariant() }, out _).Should().Be(SyncErrorCodes.Invalid);
        LiveRules.CheckRelay(Relay(item) with { Epoch = 0 }, out _).Should().Be(SyncErrorCodes.Invalid);
        LiveRules.CheckRelay(Relay(item) with { Item = null! }, out _).Should().Be(SyncErrorCodes.Invalid);
        LiveRules.CheckRelay(null, out _).Should().Be(SyncErrorCodes.Invalid);
        LiveRules.CheckRelay(Relay("{\"id\":\"a\"," + item[1..]), out _).Should().Be(SyncErrorCodes.Invalid, "重複的鍵");
        LiveRules.CheckRelay(Relay(item[..^1] + ",\"storedPath\":\"C:\\\\x\"}"), out _).Should().Be(SyncErrorCodes.Invalid, "本機路徑的欄位");
        // 每個字串都在內嵌上限（32 KB）以內、整筆也在持久事件的上限（128 KB）以內，只有即時更新的 64 KB 擋得到
        var pads = string.Concat(Enumerable.Range(0, 3).Select(i => $",\"pad{i}\":\"" + new string('a', 30_000) + "\""));
        ConversationEventRules.Validate(ConversationEventKinds.Item, item[..^1] + pads + "}").Should().BeNull();
        LiveRules.CheckRelay(Relay(item[..^1] + pads + "}"), out _).Should().Be(SyncErrorCodes.TooLarge);
        // 即時更新沒有 blob 可用：單一字串的上限也是 64 KB（不是持久事件的 32 KB 內嵌上限）
        LiveRules.CheckRelay(Relay(item[..^1] + ",\"pad\":\"" + new string('a', 40_000) + "\"}"), out _).Should().BeNull("單一字串 40 KB 仍在即時更新的 64 KB 以內");
    }

    private static LiveRelay Relay(string item) => new(ConversationId, 1, 5, 2, item);

    private static string ApprovalJson()
    {
        var item = new AgentChatItem
        {
            Id = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8", Ord = 5, Kind = AgentItemKind.Approval, Status = AgentItemStatus.Waiting, ToolName = "執行指令",
            Title = "npm test", RequestKey = "req-1", RequestDigest = "rd1:" + new string('a', 64), Risk = AgentRemoteRisk.High,
        };
        ConversationWire.TryItem(item, ver: 2, out var wire).Should().BeTrue();
        return ConversationWire.Serialize(wire!);
    }

    private static HelloRequest Hello() => new(
        SyncProtocol.Version,
        "d_7K3M",
        "4f2a9c1e0b7d",
        "0.3.0",
        "OFFICE-PC",
        null,
        "nonce",
        "spki",
        null,
        new OutboxPosition(10443, 10391, 9120),
        new Dictionary<string, long> { ["catalog"] = 1200, ["usage"] = 5 },
        new DevicePolicy("Control", true));

    // 沒協商出 live2 的連線，伺服器的 limits 不帶 maxLiveDeltaHz（DeviceHubRules.HelloLimits）
    private static HelloResponse Response() => new(
        ServerTime, "ns", new ServerInstance("inst", 2), "sig", "spki", new TrustRevs(3), SyncLimits.Default with { MaxLiveDeltaHz = null }, "0.1.0",
        new HubIdentityRotation("old", "new", "rs"));

    private static DeviceUpsertPayload Upsert() => new(
        "d_1", "OFFICE-PC", "OFFICE", "0.3.0", "tpm", "ABCD-EFGH", ServerTime, ServerTime + 1_000, true, new DevicePolicy("Off", false),
        new Dictionary<string, DeviceCliView> { ["Claude"] = new(true, "2.1.297", "native", "2.1.297", false, 0, 0, null, ServerTime) });

    [GeneratedRegex(@"^\|\s*(裝置→伺服器|伺服器→裝置)\s*\|\s*`([A-Za-z]+)`\s*\|", RegexOptions.Multiline)]
    private static partial Regex MethodRow();
}
