using System.Text;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 裝置 hub 的線上格式（docs/sync-protocol.md）：欄位名稱與文件一致（camelCase、字典鍵照原樣、clientDataJSON）、null 省略、
/// 不認得的欄位忽略、看起來像日期的字串不被轉換；payload 的編碼與雜湊；漫遊偏好的白名單。
/// </summary>
public sealed class SyncWireFormatTests
{
    [Fact]
    public void Hello_欄位名稱與文件一致_字典鍵照原樣()
    {
        var hello = new HelloRequest(
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

        var json = JObject.Parse(SyncJson.Serialize(hello));

        json.Properties().Select(p => p.Name).Should().Equal(
            "protocolVersion", "deviceId", "bootId", "appVersion", "machineName", "nonceD", "observedSpkiSha256", "outbox", "cursors", "policy");
        json["outbox"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("{\"nextDeviceSeq\":10443,\"oldestUnackedDeviceSeq\":10391,\"oldestRetainedDeviceSeq\":9120}");
        json["cursors"]!["catalog"]!.Value<long>().Should().Be(1200);
        SyncJson.Deserialize<HelloRequest>(SyncJson.Serialize(hello)).Should().BeEquivalentTo(hello);
    }

    [Fact]
    public void CLI狀態的鍵是供應商名稱_不轉成camelCase()
    {
        var payload = new CliStatePayload(new Dictionary<string, DeviceCliView>
        {
            ["Claude"] = new(true, "2.1.297", "native", "2.1.297", false, 0, 0, null, 1_791_580_000_000),
        });

        SyncJson.Serialize(payload).Should().Contain("\"Claude\":{\"installed\":true,\"version\":\"2.1.297\"");
    }

    [Fact]
    public void 信任資料_名冊與版本_camelCase往返不變_沒有變動時名冊是null()
    {
        var entry = new DeviceRegistryEntry("d_1", "OFFICE-PC", "spki", 1_791_580_000_000, 1_791_590_000_000, DeviceRegistryRules.RevokedRetired);
        var json = SyncJson.Serialize(new TrustBundle(3, [entry]));

        json.Should().Be("{\"registryRev\":3,\"registry\":[{\"deviceId\":\"d_1\",\"name\":\"OFFICE-PC\",\"devicePubKey\":\"spki\",\"createdAt\":1791580000000,\"revokedAt\":1791590000000,\"revokedReason\":\"retired\"}]}");
        SyncJson.Deserialize<TrustBundle>(json).Should().BeEquivalentTo(new TrustBundle(3, [entry]));
        SyncJson.Deserialize<TrustBundle>("{\"registryRev\":3}")!.Registry.Should().BeNull();
        SyncJson.Serialize(new TrustChanged(4)).Should().Be("{\"registry\":4}");
        SyncJson.Serialize(new GetTrustRequest(2)).Should().Be("{\"registryAfter\":2}");
    }

    [Fact]
    public void 不認得的欄位忽略_缺的欄位是預設值_日期樣的字串不轉換()
    {
        var ack = SyncJson.Deserialize<BatchAck>("{\"batchId\":\"b\",\"acceptedThrough\":3,\"results\":[{\"deviceSeq\":3,\"seq\":9,\"futureField\":1}],\"newer\":{\"x\":1}}")!;
        ack.Should().BeEquivalentTo(new BatchAck("b", 3, [new BatchEventResult(3, Seq: 9)]));
        ack.Error.Should().BeNull();

        var pulled = SyncJson.Deserialize<PulledEvent>("{\"seq\":1,\"stream\":\"catalog\",\"cls\":\"catalog\",\"author\":\"server\",\"kind\":\"prefs.set\",\"enc\":\"json\",\"payload\":\"2026-10-15T00:00:00Z\",\"createdAt\":2}")!;
        pulled.Payload.Should().Be("2026-10-15T00:00:00Z");
    }

    [Fact]
    public void null欄位省略()
    {
        SyncJson.Serialize(new ServerNotice("shutdown", RetryAfterSec: 30)).Should().Be("{\"kind\":\"shutdown\",\"retryAfterSec\":30}");
        SyncJson.Serialize(new BatchEventResult(3, Rejected: "conflict")).Should().Be("{\"deviceSeq\":3,\"rejected\":\"conflict\"}");
    }

    // U2 之前（1724718）的線上格式：每個既有欄位都有值、app 自我更新 U2 加的選用欄位都沒有值。下面的測試逐字比對，
    // 舊版桌面與伺服器看到的形狀完全不變（這三串是在 U2 之前的 Protocol 上序列化出來的）
    private const string HelloResponseBeforeU2Json =
        """{"serverTime":1791580000000,"nonceS":"nonceS","server":{"instanceId":"9f1c","restoreGeneration":0},"hubSig":"hubSig","hubIdentitySpki":"hubSpki","trustRevs":{"registry":7},"limits":{"maxBatchBytes":262144,"maxBatchEvents":500,"maxInlinePayload":131072,"maxLiveHz":4,"maxInFlightBatches":4,"maxPullEvents":500,"maxPullBytes":1048576},"minAppVersion":"0.1.0","rotation":{"oldSpki":"old","newSpki":"new","sig":"rs"},"features":["conv1"]}""";

    private const string ProveResponseBeforeU2Json =
        """{"accessToken":"cha_x","accessTokenExpiresAt":1791580900000,"nextDeviceSeqExpected":10391,"resync":false,"usageAssignments":[{"logicalAccountId":"la_1","term":4,"on":true}],"tokenRotation":"chd_y"}""";

    private const string LatestAppJson = """{"release":"0.2.300-ab12cd3","version":"0.2.300"}""";

    private static HelloResponse HelloResponseBeforeU2() => new(1_791_580_000_000, "nonceS", new ServerInstance("9f1c", 0), "hubSig", "hubSpki",
        new TrustRevs(7), SyncLimits.Default with { MaxLiveDeltaHz = null }, "0.1.0", new HubIdentityRotation("old", "new", "rs"), [DeviceFeatureRules.Conv1]);

    private static ProveResponse ProveResponseBeforeU2() => new("cha_x", 1_791_580_900_000, 10391, false, [new UsageAssign("la_1", 4, true)], "chd_y");

    [Fact]
    public void 新增的選用欄位是null時不出現在線上格式()
    {
        SyncJson.Serialize(HelloResponseBeforeU2()).Should().Be(HelloResponseBeforeU2Json);
        SyncJson.Serialize(HelloResponseBeforeU2() with { Rotation = null, Features = null }).Should().Be(
            """{"serverTime":1791580000000,"nonceS":"nonceS","server":{"instanceId":"9f1c","restoreGeneration":0},"hubSig":"hubSig","hubIdentitySpki":"hubSpki","trustRevs":{"registry":7},"limits":{"maxBatchBytes":262144,"maxBatchEvents":500,"maxInlinePayload":131072,"maxLiveHz":4,"maxInFlightBatches":4,"maxPullEvents":500,"maxPullBytes":1048576},"minAppVersion":"0.1.0"}""");
        SyncJson.Serialize(ProveResponseBeforeU2()).Should().Be(ProveResponseBeforeU2Json);
        // live2（2026-10-23）的上限只在新伺服器出現，接在最後；舊桌面忽略不認得的欄位
        SyncJson.Serialize(SyncLimits.Default).Should().EndWith(""","maxPullBytes":1048576,"maxLiveDeltaHz":8}""");
        SyncJson.Deserialize<SyncLimits>("""{"maxBatchBytes":262144,"maxBatchEvents":500,"maxInlinePayload":131072,"maxLiveHz":4,"maxInFlightBatches":4,"maxPullEvents":500,"maxPullBytes":1048576}""")!
            .MaxLiveDeltaHz.Should().BeNull("舊伺服器沒有這個欄位＝不提供");
        SyncJson.Serialize(new ServerNotice(ServerNoticeKinds.Shutdown, 30)).Should().Be("""{"kind":"shutdown","retryAfterSec":30}""");
        SyncJson.Serialize(new ServerNotice(ServerNoticeKinds.Alert, Text: "憑證快到期")).Should().Be("""{"kind":"alert","text":"憑證快到期"}""");
        SyncJson.Serialize(new ServerNotice(ServerNoticeKinds.MinAppVersion, Text: "請更新到 0.3.0")).Should().Be("""{"kind":"minAppVersion","text":"請更新到 0.3.0"}""");
    }

    [Fact]
    public void appRelease通知的線上格式()
    {
        var notice = new ServerNotice(ServerNoticeKinds.AppRelease, Release: "0.2.300-ab12cd3");

        SyncJson.Serialize(notice).Should().Be("""{"kind":"appRelease","release":"0.2.300-ab12cd3"}""");
        SyncJson.Deserialize<ServerNotice>("""{"kind":"appRelease","release":"0.2.300-ab12cd3"}""").Should().Be(notice);
    }

    [Fact]
    public void updateOnly只在true時出現()
    {
        // 伺服器只送 true 或 null（不送 false，D15）：不是只更新的連線，線上格式跟 U2 之前一樣
        SyncJson.Serialize(ProveResponseBeforeU2() with { UpdateOnly = true }).Should().Be(ProveResponseBeforeU2Json[..^1] + ""","updateOnly":true}""");
        JObject.Parse(SyncJson.Serialize(ProveResponseBeforeU2())).ContainsKey("updateOnly").Should().BeFalse();
        SyncJson.Deserialize<ProveResponse>(ProveResponseBeforeU2Json[..^1] + ""","updateOnly":true}""")!.UpdateOnly.Should().BeTrue();
    }

    [Fact]
    public void latestApp在features之後()
    {
        var hello = HelloResponseBeforeU2() with { Features = [DeviceFeatureRules.Update1], LatestApp = new AppReleaseInfo("0.2.300-ab12cd3", "0.2.300") };
        var json = SyncJson.Serialize(hello);

        JObject.Parse(json).Properties().Select(p => p.Name).TakeLast(2).Should().Equal("features", "latestApp");
        json.Should().EndWith(""","features":["update1"],"latestApp":""" + LatestAppJson + "}");
        SyncJson.Serialize(hello with { Features = null }).Should().EndWith(""","rotation":{"oldSpki":"old","newSpki":"new","sig":"rs"},"latestApp":""" + LatestAppJson + "}",
            "伺服器沒宣告功能時 latestApp 接在 rotation 之後");
    }

    [Fact]
    public void latest回應的sig是物件_manifest是base64()
    {
        using var key = ReleaseFixtures.NewKey();
        var manifest = ReleaseFixtures.ManifestBytes("0.2.300-ab12cd3", [ReleaseFixtures.File("Myriagent.exe", "exe")]);
        var signature = ReleaseManifestRules.Sign(manifest, key);
        var json = SyncJson.Serialize(new LatestAppRelease("0.2.300-ab12cd3", "0.2.300", "ab12cd3", Convert.ToBase64String(manifest), signature, 123));

        var wire = JObject.Parse(json);
        wire.Properties().Select(p => p.Name).Should().Equal("release", "version", "build", "manifest", "sig", "zipBytes");
        wire["sig"]!.Type.Should().Be(JTokenType.Object, "sig 是有 kid 與 sig 兩個欄位的物件，不是 .manifest.sig 的字串");
        wire["sig"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be($$"""{"kid":"{{signature.Kid}}","sig":"{{signature.Sig}}"}""");

        // U3 的 HubReleaseSource：manifest 以標準 base64 還原成原文位元組，sig 以 SerializeSignature 組回 .manifest.sig 的內容
        var latest = SyncJson.Deserialize<LatestAppRelease>(json)!;
        var original = Convert.FromBase64String(latest.Manifest);
        original.Should().Equal(manifest, "manifest 是原文位元組，不重新序列化");
        var signatureFile = ReleaseManifestRules.SerializeSignature(latest.Sig);
        signatureFile.Length.Should().BeLessThanOrEqualTo(ReleaseManifestRules.MaxSignatureBytes);
        ReleaseManifestRules.TryParseSignature(signatureFile, out var parsed).Should().BeTrue();
        parsed.Should().Be(signature);
        ReleaseManifestRules.VerifySignature(original, parsed, [ReleaseFixtures.KeyOf(key)], []).Should().BeNull();
        latest.ZipBytes.Should().Be(123);

        // 之後加的欄位（1.4 的不變式只允許加選用欄位）：U3 的桌面照樣讀得到
        SyncJson.Deserialize<LatestAppRelease>(json[..^1] + ""","future":{"x":1}}""").Should().BeEquivalentTo(latest);
    }

    [Fact]
    public void 新欄位往返_舊JSON沒有新欄位也讀得到()
    {
        var hello = HelloResponseBeforeU2() with { LatestApp = new AppReleaseInfo("0.2.300-ab12cd3", "0.2.300") };
        var helloBack = SyncJson.Deserialize<HelloResponse>(SyncJson.Serialize(hello))!;
        helloBack.LatestApp.Should().Be(hello.LatestApp);
        helloBack.Should().BeEquivalentTo(hello);
        SyncJson.Deserialize<ProveResponse>(SyncJson.Serialize(ProveResponseBeforeU2() with { UpdateOnly = true }))!.UpdateOnly.Should().BeTrue();
        var notice = new ServerNotice(ServerNoticeKinds.AppRelease, Release: "0.2.300-ab12cd3");
        SyncJson.Deserialize<ServerNotice>(SyncJson.Serialize(notice)).Should().Be(notice);

        // U2 之前的伺服器送的 JSON：新欄位一律讀成 null
        var response = SyncJson.Deserialize<HelloResponse>(HelloResponseBeforeU2Json)!;
        response.LatestApp.Should().BeNull();
        response.Should().BeEquivalentTo(HelloResponseBeforeU2());
        SyncJson.Deserialize<ProveResponse>(ProveResponseBeforeU2Json)!.UpdateOnly.Should().BeNull();
        SyncJson.Deserialize<ServerNotice>("""{"kind":"shutdown","retryAfterSec":30}""")!.Release.Should().BeNull();
    }

    [Fact]
    public void payload_小的照原文_1KB以上gzip_雜湊一律是原文的SHA256()
    {
        const string small = "{\"label\":\"工作用 Max\"}";
        var (enc, payload) = PayloadRules.Encode(small);
        enc.Should().Be(PayloadRules.EncJson);
        payload.Should().Be(small);
        PayloadRules.Sha256Hex(small).Should().MatchRegex("^[0-9a-f]{64}$");
        PayloadRules.IsSha256Hex(PayloadRules.Sha256Hex(small)).Should().BeTrue();
        PayloadRules.IsSha256Hex(PayloadRules.Sha256Hex(small).ToUpperInvariant()).Should().BeFalse("一律小寫");

        var big = "{\"text\":\"" + new string('字', 4000) + "\"}";
        Encoding.UTF8.GetByteCount(big).Should().BeGreaterThan(PayloadRules.GzipThresholdBytes);
        var (bigEnc, bigPayload) = PayloadRules.Encode(big);
        bigEnc.Should().Be(PayloadRules.EncGzip);
        bigPayload.Length.Should().BeLessThan(big.Length);
        PayloadRules.TryDecode(bigEnc, bigPayload, 128 * 1024, out var decoded).Should().BeTrue();
        decoded.Should().Be(big);
        PayloadRules.TryDecode(enc, payload, 128 * 1024, out var same).Should().BeTrue();
        same.Should().Be(small);
    }

    [Fact]
    public void payload_一般大小的對話項目也壓縮_壓完沒變短的照原文()
    {
        PayloadRules.GzipThresholdBytes.Should().Be(1024);

        // 典型的對話項目（2～3 KB 的 JSON，文字有重複的詞）：以前未滿 8 KB 一律原樣送
        var item = "{\"id\":\"it-1\",\"kind\":\"Assistant\",\"status\":\"Done\",\"text\":\"" +
            string.Concat(Enumerable.Range(0, 40).Select(i => $"第 {i} 步：修改 src/Myriagent/Sync/SyncEngine.cs 並重新建置。")) + "\"}";
        Encoding.UTF8.GetByteCount(item).Should().BeInRange(1024, 8 * 1024);
        var (enc, payload) = PayloadRules.Encode(item);
        enc.Should().Be(PayloadRules.EncGzip);
        payload.Length.Should().BeLessThan(Encoding.UTF8.GetByteCount(item) / 2);
        PayloadRules.TryDecode(enc, payload, 128 * 1024, out var decoded).Should().BeTrue();
        decoded.Should().Be(item);

        // 壓不下去的內容（亂數的 base64）：gzip＋base64 反而變長，照原文
        var bytes = new byte[1500];
        new Random(1).NextBytes(bytes);
        var noise = Convert.ToBase64String(bytes);
        var random = "{\"output\":\"" + noise + "\"}";
        Encoding.UTF8.GetByteCount(random).Should().BeGreaterThan(PayloadRules.GzipThresholdBytes);
        var (rawEnc, rawPayload) = PayloadRules.Encode(random);
        rawEnc.Should().Be(PayloadRules.EncJson);
        rawPayload.Should().Be(random);

        // 剛好在門檻下的照原文
        var under = "{\"t\":\"" + new string('a', PayloadRules.GzipThresholdBytes - 9) + "\"}";
        Encoding.UTF8.GetByteCount(under).Should().Be(PayloadRules.GzipThresholdBytes - 1);
        PayloadRules.Encode(under).Enc.Should().Be(PayloadRules.EncJson);
    }

    [Fact]
    public void payload解碼_超過上限_格式錯誤_不認得的編碼_一律失敗()
    {
        var (enc, payload) = PayloadRules.Encode("{\"x\":\"" + new string('a', 200_000) + "\"}");
        PayloadRules.TryDecode(enc, payload, 128 * 1024, out _).Should().BeFalse("解壓後超過上限（gzip 炸彈）");
        PayloadRules.TryDecode(PayloadRules.EncGzip, "不是 base64", 1024, out _).Should().BeFalse();
        PayloadRules.TryDecode(PayloadRules.EncGzip, Convert.ToBase64String("not gzip"u8.ToArray()), 1024, out _).Should().BeFalse();
        PayloadRules.TryDecode("brotli", "{}", 1024, out _).Should().BeFalse();
        PayloadRules.TryDecode(PayloadRules.EncJson, new string('a', 2000), 1024, out _).Should().BeFalse();
        PayloadRules.TryDecode(PayloadRules.EncJson, null, 1024, out _).Should().BeFalse();
    }

    [Fact]
    public void 漫遊偏好只接受白名單的四個鍵與合法的值()
    {
        PrefsRules.IsValid("theme", "light").Should().BeTrue();
        PrefsRules.IsValid("theme", "neon").Should().BeFalse();
        PrefsRules.IsValid("fontSize", 14).Should().BeTrue();
        PrefsRules.IsValid("fontSize", 11).Should().BeFalse();
        PrefsRules.IsValid("fontSize", 14.5).Should().BeFalse();
        PrefsRules.IsValid("fontSize", "14").Should().BeFalse();
        PrefsRules.IsValid("enterToSend", false).Should().BeTrue();
        PrefsRules.IsValid("notifications", "true").Should().BeFalse();
        PrefsRules.IsValid("instructions", "rm -rf").Should().BeFalse("Instructions 只能在每台電腦本機改（SEC-24）");
        PrefsRules.IsValid("cliRestartPolicy", "NextSend").Should().BeFalse();
        PrefsRules.IsValid(null, null).Should().BeFalse();
        PrefsRules.Keys.Should().Equal("theme", "fontSize", "enterToSend", "notifications");
    }

    [Fact]
    public void 事件種類_裝置只能寫輸入_伺服器寫結果_各自的類別與串流()
    {
        foreach (var kind in new[] { SyncEventKinds.LinkUpsert, SyncEventKinds.LinkRemoved, SyncEventKinds.AccountUpdate, SyncEventKinds.PrefsUpdate, SyncEventKinds.DeviceUpdate, SyncEventKinds.CliState, SyncEventKinds.AppState })
        {
            SyncEventKinds.IsDeviceAuthored(kind).Should().BeTrue(kind);
            SyncEventKinds.IsServerAuthored(kind).Should().BeFalse(kind);
            SyncEventKinds.Placement(kind).Should().Be((SyncClasses.Catalog, SyncStreams.Catalog));
        }

        foreach (var kind in new[] { SyncEventKinds.AccountUpsert, SyncEventKinds.AccountDeleted, SyncEventKinds.DeviceUpsert, SyncEventKinds.DeviceRevoked, SyncEventKinds.PrefsSet })
        {
            SyncEventKinds.IsDeviceAuthored(kind).Should().BeFalse(kind);
            SyncEventKinds.IsServerAuthored(kind).Should().BeTrue(kind);
            SyncEventKinds.Placement(kind).Should().Be((SyncClasses.Catalog, SyncStreams.Catalog));
        }

        SyncEventKinds.IsDeviceAuthored(SyncEventKinds.UsageSnapshot).Should().BeFalse("用量經 UsagePublish，由伺服器發布");
        SyncEventKinds.Placement(SyncEventKinds.UsageSnapshot).Should().Be((SyncClasses.Usage, SyncStreams.Usage));
        SyncEventKinds.Placement("conv.created").Should().BeNull("第 4 階段才有");
        SyncStreams.Conversation("5f3a").Should().Be("c:5f3a");
    }

    [Fact]
    public void app_state與device_upsert_app的線上格式()
    {
        SyncEventKinds.AppState.Should().Be("app.state");
        new[] { AppStatePhases.Idle, AppStatePhases.Downloading, AppStatePhases.Staged, AppStatePhases.Waiting, AppStatePhases.Draining, AppStatePhases.Applying, AppStatePhases.RolledBack, AppStatePhases.Blocked }
            .Should().Equal("idle", "downloading", "staged", "waiting", "draining", "applying", "rolledBack", "blocked");
        AppStatePhases.IsKnown("rolledBack").Should().BeTrue();
        AppStatePhases.IsKnown("RolledBack").Should().BeFalse("線上的值大小寫固定");
        AppStatePhases.IsKnown("updating").Should().BeFalse();
        AppStatePhases.IsKnown(null).Should().BeFalse();

        var state = new AppStatePayload("0.2.300", "0.2.300-ab12cd3", AppStatePhases.Staged, "0.2.301-0123abc", null, 1_792_000_000_000);
        const string stateJson = "{\"version\":\"0.2.300\",\"release\":\"0.2.300-ab12cd3\",\"phase\":\"staged\",\"staged\":\"0.2.301-0123abc\",\"checkedAt\":1792000000000}";
        SyncJson.Serialize(state).Should().Be(stateJson, "lastError 是 null 時省略");
        SyncJson.Deserialize<AppStatePayload>(stateJson).Should().Be(state);
        SyncJson.Deserialize<AppStatePayload>("{\"version\":\"0.2.0\",\"phase\":\"idle\",\"checkedAt\":5,\"newerField\":1}")
            .Should().Be(new AppStatePayload("0.2.0", null, AppStatePhases.Idle, null, null, 5), "開發版沒有 release；不認得的欄位忽略");

        // device.upsert 的 app 是最後一個欄位（在第 4 階段的 features 之後）；沒有時跟 U2 之前逐字相同，舊桌面看到的形狀不變
        const string before = "{\"deviceId\":\"d_1\",\"name\":\"OFFICE-PC\",\"appVersion\":\"0.2.300\",\"keyStorage\":\"software\",\"fingerprint\":\"ABCD-EFGH\",\"createdAt\":1,\"online\":true}";
        var upsert = new DeviceUpsertPayload("d_1", "OFFICE-PC", null, "0.2.300", "software", "ABCD-EFGH", 1, null, true, null, null);
        SyncJson.Serialize(upsert).Should().Be(before);
        SyncJson.Serialize(upsert with { App = state }).Should().Be(before[..^1] + ",\"app\":" + stateJson + "}");
        string[] features = [DeviceFeatureRules.Conv1];
        SyncJson.Serialize(upsert with { Features = features, App = state })
            .Should().Be(before[..^1] + ",\"features\":[\"conv1\"],\"app\":" + stateJson + "}", "app 在 features 之後");
        SyncJson.Deserialize<DeviceUpsertPayload>(before)!.App.Should().BeNull("U2 之前的 device.upsert 沒有 app");
        SyncJson.Deserialize<DeviceUpsertPayload>(SyncJson.Serialize(upsert with { App = state }))!.App.Should().Be(state);
    }

    [Theory]
    [InlineData("quotes")]
    [InlineData("controls")]
    [InlineData("separators")]
    [InlineData("cjk")]
    public void 批次的位元組估算是上限_JSON跳脫膨脹也算進去(string shape)
    {
        // 最壞情況：payload 全是要跳脫的字元（引號與反斜線變兩倍、控制字元與行分隔變 6 位元組）
        var unit = shape switch
        {
            "quotes" => "\"\\",
            "controls" => new string([(char)1, '\n', (char)0x1F]),
            "separators" => new string([(char)0x85, (char)0x2028, (char)0x2029]),
            _ => "同步",
        };
        var payload = string.Concat(Enumerable.Repeat(unit, 3000));
        var events = Enumerable.Range(1, 5)
            .Select(seq => new SyncEvent(seq, SyncStreams.Catalog, SyncEventKinds.LinkUpsert, PayloadRules.EncJson, new string('a', 64), payload))
            .ToList();

        var wire = Encoding.UTF8.GetByteCount(SyncJson.Serialize(new Batch("01J9Q0000000000000000000000", events)));

        wire.Should().BeLessThanOrEqualTo(events.Sum(e => PayloadRules.WireSize(e.Payload)));
    }
}
