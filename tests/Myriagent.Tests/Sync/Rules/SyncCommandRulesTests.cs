using System.Security.Cryptography;
using System.Text;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// 電腦端遠端指令（WP4.4 計畫步 1）的純規則：配對網址 → aud（D5，與伺服器的 NormalizeOrigin 同一張固定向量表）、
/// Hello 時鐘的可用期限與重新 Hello（D4）、commands_seen 的去重判斷（D8）、結果的保留與序列化（D9）、
/// 驗信封失敗的結果（D7：時鐘不可用也是 expired、message 一律 null）、遠端操作紀錄的遮蔽與欄位（D12）、修剪的時機（D11）與匯出位置（D33）
/// </summary>
public sealed class SyncCommandRulesTests : IDisposable
{
    private const string Aud = "https://hub.example.com";
    private const string Conn = "conn-AbC123";
    private const string Cid = "01J9QZ3K8M5R7T2V4W6X8Y0A1B";
    private const long Created = 1_791_580_000_000;

    /// <summary>每毫秒一個刻度：時鐘的讀數就是毫秒</summary>
    private const long Ms = 1_000;

    private const long Hour = 60L * 60 * 1000;

    private static readonly string DigestA = new('a', 64);
    private static readonly string DigestB = new('b', 64);

    /// <summary>收到時的伺服器時間界線：iat 之後半秒到一秒半</summary>
    private static readonly ServerTimeBounds Now = new(Created + 1_000, Created + 2_000);

    private readonly ECDsa hub = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        hub.Dispose();
        impostor.Dispose();
    }

    // ---- aud（D5）----

    /// <summary>D5 的固定向量（WP4.5 步 6 的 NormalizeOrigin 測試是同一張表，逐字相同）</summary>
    public static TheoryData<string, string?> AudienceVectors() => new()
    {
        { "https://hub.example.com", "https://hub.example.com" },
        { "HTTPS://Hub.Example.COM:443/", "https://hub.example.com" },
        { "https://hub.example.com:8443", "https://hub.example.com:8443" },
        { "https://bücher.example", "https://xn--bcher-kva.example" },
        { "https://[::1]:8443", "https://[::1]:8443" },
        { "http://localhost:5000", "http://localhost:5000" },
        { "https://hub.example.com/x", null },
        { "https://hub.example.com?x=1", null },
        { "https://hub.example.com#x", null },
        { "https://u:p@hub.example.com", null },
        { "ftp://hub.example.com", null },
        { "", null },
    };

    [Theory]
    [MemberData(nameof(AudienceVectors))]
    public void Audience_固定向量(string serverUrl, string? expected)
    {
        SyncCommandRules.Audience(serverUrl).Should().Be(expected);
    }

    [Fact]
    public void Audience_null與空白回null()
    {
        SyncCommandRules.Audience(null).Should().BeNull();
        SyncCommandRules.Audience("   ").Should().BeNull();
        SyncCommandRules.Audience("hub.example.com").Should().BeNull("沒有 scheme 不是絕對網址");
    }

    // ---- 伺服器時鐘（D4）----

    [Fact]
    public void UsableBounds_年齡剛好12小時可用_多1毫秒不可用()
    {
        SyncCommandRules.MaxClockAgeMs.Should().Be(12 * Hour);
        var clock = ServerClockRules.HelloClock(Created, sentTimestamp: 1_000, receivedTimestamp: 1_200, Ms);
        var received = 1_200L;

        SyncCommandRules.ClockAgeMs(clock, received + 12 * Hour).Should().Be(12 * Hour);
        SyncCommandRules.UsableBounds(clock, received + 12 * Hour).Should().Be(ServerClockRules.Bounds(clock, received + 12 * Hour))
            .And.Be(new ServerTimeBounds(Created + 12 * Hour, Created + 12 * Hour + 200));
        SyncCommandRules.UsableBounds(clock, received + 12 * Hour + 1).Should().BeNull("年齡超過 12 小時：單調時鐘在睡眠時可能停止");
        SyncCommandRules.UsableBounds(clock, received).Should().Be(new ServerTimeBounds(Created, Created + 200));
    }

    [Fact]
    public void UsableBounds_時鐘null或現在早於收到時戳回null()
    {
        var clock = ServerClockRules.HelloClock(Created, 1_000, 1_200, Ms);

        SyncCommandRules.UsableBounds(null, 1_200).Should().BeNull();
        SyncCommandRules.UsableBounds(clock, 1_199).Should().BeNull("單調時鐘倒退");
        SyncCommandRules.ClockAgeMs(null, 1_200).Should().BeNull();
        SyncCommandRules.ClockAgeMs(clock, 1_199).Should().BeNull();
        SyncCommandRules.ClockAgeMs(clock! with { Frequency = 0 }, 1_200).Should().BeNull();
        SyncCommandRules.UsableBounds(new ServerClock(Created, 0, 30_001, Ms), 30_001).Should().BeNull("往返超過 30 秒照 ServerClockRules 不可用");
    }

    [Fact]
    public void ClockAgeMs_毫秒以外的頻率無條件進位()
    {
        const long frequency = 10_000_000;
        var clock = ServerClockRules.HelloClock(Created, 0, 0, frequency);

        SyncCommandRules.ClockAgeMs(clock, 1).Should().Be(1);
        SyncCommandRules.ClockAgeMs(clock, 10_000).Should().Be(1);
        SyncCommandRules.ClockAgeMs(clock, 10_001).Should().Be(2);
    }

    [Fact]
    public void ShouldRefreshClock_滿6小時且閒置才是true_有執行中是false()
    {
        SyncCommandRules.ClockRefreshMs.Should().Be(6 * Hour);
        var clock = ServerClockRules.HelloClock(Created, 0, 100, Ms);

        SyncCommandRules.ShouldRefreshClock(clock, 100 + 6 * Hour - 1, inFlight: 0).Should().BeFalse();
        SyncCommandRules.ShouldRefreshClock(clock, 100 + 6 * Hour, inFlight: 0).Should().BeTrue();
        SyncCommandRules.ShouldRefreshClock(clock, 100 + 6 * Hour, inFlight: 1).Should().BeFalse("有執行中的指令就等它們做完");
        SyncCommandRules.ShouldRefreshClock(clock, 100 + 13 * Hour, inFlight: 2).Should().BeFalse();
        SyncCommandRules.ShouldRefreshClock(clock, 99, inFlight: 0).Should().BeTrue("單調時鐘倒退：時鐘不可信，換一個");
        SyncCommandRules.ShouldRefreshClock(null, 100 + 6 * Hour, inFlight: 0).Should().BeFalse("沒有時鐘就沒有可換的");
    }

    [Fact]
    public void ReHelloAllowed_單調時戳滿一分鐘才放行_沒重新Hello過或時鐘倒退一律放行()
    {
        SyncCommandRules.ReHelloMinIntervalMs.Should().Be(60_000);
        // 頻率 1000：時戳就是毫秒
        SyncCommandRules.ReHelloAllowed(null, 5_000, 1_000).Should().BeTrue("還沒重新 Hello 過");
        SyncCommandRules.ReHelloAllowed(5_000, 5_000 + 59_999, 1_000).Should().BeFalse("59.999 秒");
        SyncCommandRules.ReHelloAllowed(5_000, 5_000 + 60_000, 1_000).Should().BeTrue("剛好 60 秒");
        SyncCommandRules.ReHelloAllowed(5_000, 4_999, 1_000).Should().BeTrue("單調時鐘倒退：放行，不讓時鐘不可用的連線一直回 expired（合併前審查 T3）");
        SyncCommandRules.ReHelloAllowed(5_000, 5_000, 0).Should().BeTrue("頻率不合格：放行");
        // 頻率是 Stopwatch 常見的 10 MHz，時戳很大時也不溢位
        var big = long.MaxValue - 700_000_000;
        SyncCommandRules.ReHelloAllowed(big, big + 599_999_999, 10_000_000).Should().BeFalse();
        SyncCommandRules.ReHelloAllowed(big, big + 600_000_000, 10_000_000).Should().BeTrue();
    }

    // ---- 去重（D8）----

    [Fact]
    public void Decide_五種結果()
    {
        var done = new CommandResult(Cid, RemoteResultStates.Done);
        var cached = new CommandResult(Cid, RemoteResultStates.Rejected, RemoteResultCodes.Stale, "卡片已被處理");
        var executing = new CommandSeenRow(DigestA, SyncCommandRules.ExecutingState, null, Created);
        var completed = new CommandSeenRow(DigestA, RemoteResultStates.Done, SyncCommandRules.SerializeResult(done), Created);

        SyncCommandRules.Decide(DigestA, null, inFlight: false, cached: null).Should().Be(new CommandSeenDecision(CommandSeenKind.New, null));
        SyncCommandRules.Decide(DigestB, completed, inFlight: false, cached: null).Should().Be(new CommandSeenDecision(CommandSeenKind.Conflict, null));
        SyncCommandRules.Decide(DigestB, executing, inFlight: true, cached: null).Kind.Should().Be(CommandSeenKind.Conflict, "摘要不同優先於進行中");
        SyncCommandRules.Decide(DigestA, executing, inFlight: true, cached: cached).Should().Be(new CommandSeenDecision(CommandSeenKind.InFlight, null));
        SyncCommandRules.Decide(DigestA, executing, inFlight: false, cached: cached).Should().Be(new CommandSeenDecision(CommandSeenKind.Duplicate, cached),
            "不在進行中表、有快取：完成時寫入失敗留下的列，回快取的結果");
        SyncCommandRules.Decide(DigestA, executing, inFlight: false, cached: null).Should().Be(new CommandSeenDecision(CommandSeenKind.Unknown, null),
            "不在進行中表、沒有快取：當機留下的，回 unknown、不重做");
        SyncCommandRules.Decide(DigestA, completed, inFlight: false, cached: null).Should().Be(new CommandSeenDecision(CommandSeenKind.Duplicate, done));
        SyncCommandRules.Decide(DigestA, completed with { ResultJson = "{壞掉" }, inFlight: false, cached: null).Kind.Should().Be(CommandSeenKind.Unknown);
        SyncCommandRules.Decide(DigestA, completed with { ResultJson = null }, inFlight: false, cached: null).Kind.Should().Be(CommandSeenKind.Unknown);
    }

    [Fact]
    public void KeepsSeenRow_只有rejected加busy不留()
    {
        foreach (var code in RemoteResultCodes.All)
        {
            var keeps = SyncCommandRules.KeepsSeenRow(new CommandResult(Cid, RemoteResultStates.Rejected, code));
            keeps.Should().Be(code != RemoteResultCodes.Busy, code);
        }

        SyncCommandRules.KeepsSeenRow(new CommandResult(Cid, RemoteResultStates.Done)).Should().BeTrue();
        SyncCommandRules.KeepsSeenRow(new CommandResult(Cid, RemoteResultStates.Unknown)).Should().BeTrue();
    }

    [Fact]
    public void SerializeResult_往返相同_壞JSON回null()
    {
        var results = new[]
        {
            new CommandResult(Cid, RemoteResultStates.Done, Tier: 2, FullAccess: true),
            new CommandResult(Cid, RemoteResultStates.Rejected, RemoteResultCodes.Conflict, "資料夾不存在：D:\\專案", 1, false),
            new CommandResult(Cid, RemoteResultStates.Unknown),
        };
        foreach (var result in results)
        {
            var json = SyncCommandRules.SerializeResult(result);
            SyncCommandRules.ParseResult(Cid, json).Should().Be(result);
        }

        SyncCommandRules.SerializeResult(new CommandResult(Cid, RemoteResultStates.Unknown)).Should().Be("{\"commandId\":\"" + Cid + "\",\"state\":\"unknown\"}");

        SyncCommandRules.ParseResult(Cid, null).Should().BeNull();
        SyncCommandRules.ParseResult(Cid, "").Should().BeNull();
        SyncCommandRules.ParseResult(Cid, "{壞掉").Should().BeNull();
        SyncCommandRules.ParseResult(Cid, "[]").Should().BeNull();
        SyncCommandRules.ParseResult(Cid, "null").Should().BeNull();
        SyncCommandRules.ParseResult(Cid, "{\"commandId\":\"" + Cid + "\",\"state\":\"rejected\"}").Should().BeNull("rejected 沒有 code 不合格");
        SyncCommandRules.ParseResult(Cid, "{\"commandId\":\"" + Cid + "\",\"state\":\"maybe\"}").Should().BeNull();
        SyncCommandRules.ParseResult("01J9QZ3K8M5R7T2V4W6X8Y0A1C", SyncCommandRules.SerializeResult(results[0])).Should().BeNull("commandId 不同");
    }

    // ---- 驗信封失敗的結果（D7）----

    [Fact]
    public void VerifyFailureResult_外層不是ULID回null()
    {
        var verdict = Verify(Sign(Body()), Now, Target() with { Aud = "https://other.example" });
        verdict.Step.Should().Be(CommandEnvelopeSteps.Audience);

        SyncCommandRules.VerifyFailureResult(null, verdict).Should().BeNull();
        SyncCommandRules.VerifyFailureResult("not-a-ulid", verdict).Should().BeNull();
        SyncCommandRules.VerifyFailureResult(Cid.ToLowerInvariant(), verdict).Should().BeNull();
    }

    [Fact]
    public void VerifyFailureResult_時間步_時鐘不可用與過期都是expired_lifetime不合理是invalid()
    {
        var unavailable = Verify(Sign(Body()), null);
        unavailable.Step.Should().Be(CommandEnvelopeSteps.Time);
        Expect(SyncCommandRules.VerifyFailureResult(Cid, unavailable), RemoteResultCodes.Expired, tier: 1,
            "時鐘不可用照契約回 expired（SP 14.4、對齊 A3），不是 busy");

        var expired = Verify(Sign(Body()), new ServerTimeBounds(Created + 40_000, Created + 41_000));
        expired.Step.Should().Be(CommandEnvelopeSteps.Time);
        Expect(SyncCommandRules.VerifyFailureResult(Cid, expired), RemoteResultCodes.Expired, tier: 1, "界線可用而且已過期");

        var lifetime = Verify(Sign(Body() with { Exp = Created + 500 + 60_001 }), Now);
        lifetime.Step.Should().Be(CommandEnvelopeSteps.Time);
        Expect(SyncCommandRules.VerifyFailureResult(Cid, lifetime), RemoteResultCodes.Invalid, tier: 1, "信封自己的時間不合理");
    }

    [Fact]
    public void VerifyFailureResult_簽章是invalid且沒有tier_aud是invalid且tier是宣告值()
    {
        var signature = Verify(Sign(Body(), impostor), Now);
        signature.Step.Should().Be(CommandEnvelopeSteps.Signature);
        Expect(SyncCommandRules.VerifyFailureResult(Cid, signature), RemoteResultCodes.Invalid, tier: null, "沒解析過就沒有宣告的層級");

        var aud = Verify(Sign(Body() with { Tier = 2 }), Now, Target() with { Aud = "https://hub.example.com:8443" });
        aud.Step.Should().Be(CommandEnvelopeSteps.Audience);
        aud.Detail.Should().NotBeNullOrEmpty();
        Expect(SyncCommandRules.VerifyFailureResult(Cid, aud), RemoteResultCodes.Invalid, tier: 2, "解析過的信封宣告值");

        var tierRange = Verify(Sign(Body() with { Tier = 3 }), Now);
        tierRange.Step.Should().Be(CommandEnvelopeSteps.TierRange);
        Expect(SyncCommandRules.VerifyFailureResult(Cid, tierRange), RemoteResultCodes.Invalid, tier: null, "不在 0～2 的層級放進結果會讓整則不合格");
    }

    [Fact]
    public void VerifyFailureResult_合格的verdict丟例外()
    {
        var ok = Verify(Sign(Body()), Now);
        ok.Ok.Should().BeTrue(ok.Detail);

        var act = () => SyncCommandRules.VerifyFailureResult(Cid, ok);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void UnknownResult_合格的unknown()
    {
        var result = SyncCommandRules.UnknownResult(Cid);

        result.Should().Be(new CommandResult(Cid, RemoteResultStates.Unknown));
        RemoteResultRules.Check(result).Should().BeNull();
    }

    // ---- 遠端操作紀錄（D12）----

    [Fact]
    public void MaskBodyForAudit_send的text只留字數_不含權杖原文()
    {
        var token = FakeToken();
        CredentialLeakRules.HasLeak(token).Should().BeTrue("樣本要是 DLP 認得的權杖樣式");
        var text = "請用這把金鑰呼叫 https://example.test/v1：" + token;
        var body = Body(new JObject { ["cmd"] = "send", ["conversationId"] = RemoteSamples.ConversationId, ["text"] = text });

        var masked = SyncCommandRules.MaskBodyForAudit(body)!;

        masked.Should().NotContain(token).And.NotContain("example.test").And.NotContain("請用這把金鑰");
        var root = JObject.Parse(masked);
        Same(root["cmd"]!["text"], new JObject { ["redacted"] = text.Length });
        root["cmd"]!["cmd"]!.Value<string>().Should().Be("send");
        root["cmd"]!["conversationId"]!.Value<string>().Should().Be(RemoteSamples.ConversationId);
        root["cid"]!.Value<string>().Should().Be(Cid);
        root["tier"]!.Value<int>().Should().Be(1);
        root["issuer"]!["label"]!.Value<string>().Should().Be("Chrome・Windows");
        root.ContainsKey("truncated").Should().BeFalse();
    }

    [Fact]
    public void MaskBodyForAudit_附言與回答只留長度_新對話的上傳只留數量()
    {
        var approve = Body(new JObject
        {
            ["cmd"] = "approve", ["conversationId"] = RemoteSamples.ConversationId, ["itemId"] = "a1b2c3", ["choice"] = "deny",
            ["requestDigest"] = RemoteSamples.Digest, ["message"] = "不要刪 production 的資料表",
        });
        var plan = Body(new JObject
        {
            ["cmd"] = "approvePlan", ["conversationId"] = RemoteSamples.ConversationId, ["itemId"] = "a1b2c3", ["choice"] = "keepPlanning",
            ["requestDigest"] = RemoteSamples.Digest, ["message"] = "先列出要改的檔案",
        });
        var answers = new JObject { ["要怎麼彙總？"] = new JArray("依日期", "依客戶") };
        var answer = Body(new JObject
        {
            ["cmd"] = "answer", ["conversationId"] = RemoteSamples.ConversationId, ["itemId"] = "a1b2c3", ["requestDigest"] = RemoteSamples.Digest,
            ["answers"] = answers,
        });
        var create = Body(new JObject
        {
            ["cmd"] = "conversationCreate", ["accountId"] = "acc-1", ["title"] = "新的分析", ["text"] = "幫我整理上週的訂單",
            ["uploads"] = new JArray(RemoteSamples.Upload(), RemoteSamples.Upload()),
        }) with { Conv = null, Epoch = null };

        Same(Cmd(approve)["message"], new JObject { ["redacted"] = "不要刪 production 的資料表".Length });
        Cmd(approve)["choice"]!.Value<string>().Should().Be("deny");
        SyncCommandRules.MaskBodyForAudit(approve).Should().NotContain("production");
        Same(Cmd(plan)["message"], new JObject { ["redacted"] = "先列出要改的檔案".Length });
        Same(Cmd(answer)["answers"], new JObject { ["redacted"] = answers.ToString(Newtonsoft.Json.Formatting.None).Length });
        SyncCommandRules.MaskBodyForAudit(answer).Should().NotContain("依日期").And.NotContain("要怎麼彙總");
        Same(Cmd(create)["text"], new JObject { ["redacted"] = "幫我整理上週的訂單".Length });
        Same(Cmd(create)["uploads"], new JObject { ["count"] = 2 });
        Cmd(create)["title"]!.Value<string>().Should().Be("新的分析", "沒有權杖的字串原樣");
        SyncCommandRules.MaskBodyForAudit(create).Should().NotContain("報告.pdf");
        var root = JObject.Parse(SyncCommandRules.MaskBodyForAudit(create)!);
        root.ContainsKey("conv").Should().BeFalse();
        root.ContainsKey("epoch").Should().BeFalse();
    }

    [Fact]
    public void MaskBodyForAudit_其他字串經DLP遮蔽_issuer原樣()
    {
        var token = FakeToken();
        var body = Body(new JObject { ["cmd"] = "rename", ["conversationId"] = RemoteSamples.ConversationId, ["title"] = "金鑰 " + token })
            with { Issuer = new CommandIssuer(CommandIssuerKinds.Browser, "Safari・iOS") };

        var masked = SyncCommandRules.MaskBodyForAudit(body)!;

        masked.Should().NotContain(token);
        Cmd(body)["title"]!.Value<string>().Should().Be(CredentialLeakRules.Mask("金鑰 " + token)).And.Contain(CredentialLeakRules.Replacement);
        Same(JObject.Parse(masked)["issuer"], new JObject { ["kind"] = "browser", ["label"] = "Safari・iOS" });
    }

    [Fact]
    public void MaskBodyForAudit_超過16384字截斷並有truncated_null回null()
    {
        SyncCommandRules.MaskBodyForAudit(null).Should().BeNull();

        var body = Body(new JObject { ["cmd"] = "rename", ["conversationId"] = RemoteSamples.ConversationId, ["title"] = new string('長', 20_000) });
        var masked = SyncCommandRules.MaskBodyForAudit(body)!;

        masked.Length.Should().BeLessThanOrEqualTo(SyncCommandRules.MaxAuditBodyChars);
        var root = JObject.Parse(masked);
        root["truncated"]!.Value<bool>().Should().BeTrue();
        root["cmd"]!["title"]!.Value<string>()!.Length.Should().BeLessThan(20_000);
        root["issuer"]!["label"]!.Value<string>().Should().Be("Chrome・Windows");

        var small = SyncCommandRules.MaskBodyForAudit(Body())!;
        JObject.Parse(small).ContainsKey("truncated").Should().BeFalse();
    }

    [Fact]
    public void IssuerLabelFromMasked_取得_壞JSON回null()
    {
        var masked = SyncCommandRules.MaskBodyForAudit(Body() with { Issuer = new CommandIssuer(CommandIssuerKinds.Browser, "Edge・Android") });

        SyncCommandRules.IssuerLabelFromMasked(masked).Should().Be("Edge・Android");
        SyncCommandRules.IssuerLabelFromMasked(null).Should().BeNull();
        SyncCommandRules.IssuerLabelFromMasked("").Should().BeNull();
        SyncCommandRules.IssuerLabelFromMasked("{壞掉").Should().BeNull();
        SyncCommandRules.IssuerLabelFromMasked("[1]").Should().BeNull();
        SyncCommandRules.IssuerLabelFromMasked("{\"cid\":\"x\"}").Should().BeNull();
        SyncCommandRules.IssuerLabelFromMasked("{\"issuer\":{\"label\":5}}").Should().BeNull();

        // 第 9 步被拒的信封可能帶由右至左覆寫字元：紀錄畫面不顯示
        var spoofed = SyncCommandRules.MaskBodyForAudit(Body() with { Issuer = new CommandIssuer(CommandIssuerKinds.Browser, "Chrome" + (char)0x202E + "swodniW") });
        SyncCommandRules.IssuerLabelFromMasked(spoofed).Should().BeNull();
    }

    [Fact]
    public void SignedSha256_與手算相同()
    {
        var envelope = Sign(Body());
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("myriagent-hub-cmd-v1\n" + envelope.Body)));

        SyncCommandRules.SignedSha256(envelope).Should().Be(expected).And.MatchRegex("^[0-9a-f]{64}$");
        SyncCommandRules.SignedSha256(null).Should().BeNull();
        SyncCommandRules.SignedSha256(envelope with { Body = null! }).Should().BeNull();
        SyncCommandRules.SignedSha256(envelope with { Body = "{\"a\":\"" + (char)0xD800 + "\"}" }).Should().MatchRegex("^[0-9a-f]{64}$", "不成對的代理字元照樣算得出來");
    }

    [Fact]
    public void ClipSig_200字()
    {
        SyncCommandRules.ClipSig(null).Should().BeNull();
        SyncCommandRules.ClipSig(new string('s', 200)).Should().Be(new string('s', 200));
        SyncCommandRules.ClipSig(new string('s', 201)).Should().Be(new string('s', 200));
        SyncCommandRules.ClipSig(new string('s', 500)).Should().HaveLength(SyncCommandRules.MaxSigLength);
    }

    // ---- 修剪與匯出（D11、D33）----

    [Fact]
    public void PruneDue_null是true_23小時59分是false_24小時是true()
    {
        const long last = 1_791_580_000_000;

        SyncCommandRules.PruneDue(null, last).Should().BeTrue("第一次一定做");
        SyncCommandRules.PruneDue(last, last + 23 * Hour + 59 * 60_000).Should().BeFalse();
        SyncCommandRules.PruneDue(last, last + 24 * Hour - 1).Should().BeFalse();
        SyncCommandRules.PruneDue(last, last + 24 * Hour).Should().BeTrue();
        SyncCommandRules.PruneDue(last, last - 1).Should().BeTrue("時間倒退：不讓修剪停住");
    }

    [Fact]
    public void RemoteAuditPageWhere_第一頁_只帶at_帶at與seq()
    {
        SyncCommandRules.RemoteAuditPageWhere(null, null).Should().BeEmpty("第一頁");
        SyncCommandRules.RemoteAuditPageWhere(null, 42).Should().BeEmpty("沒有 at 時 beforeSeq 不看");
        SyncCommandRules.RemoteAuditPageWhere(1_791_580_000_000, null).Should().Be("WHERE at < $before", "WPR 的 before 是 at");
        SyncCommandRules.RemoteAuditPageWhere(1_791_580_000_000, 42).Should().Be("WHERE (at < $before OR (at = $before AND seq < $beforeSeq))",
            "以 (at, seq) 翻頁（WP4.6 D44）");
    }

    [Fact]
    public void 保留期是durable的7天加1天與180天()
    {
        SyncCommandRules.SeenRetentionMs.Should().Be(8 * 24 * Hour);
        SyncCommandRules.AuditRetentionMs.Should().Be(180 * 24 * Hour);
        SyncCommandRules.PruneIntervalMs.Should().Be(24 * Hour);
    }

    [Fact]
    public void RemoteAuditExportPath_在SyncPaths的上層()
    {
        var root = Path.Combine(Path.GetTempPath(), "myriagent-wp44-paths");
        var expected = Path.Combine(root, "remote-audit", RemoteSamples.DeviceId + ".jsonl");

        SyncCommandRules.RemoteAuditExportPath(new SyncPaths(Path.Combine(root, "sync")), RemoteSamples.DeviceId).Should().Be(expected);
        SyncCommandRules.RemoteAuditExportPath(new SyncPaths(Path.Combine(root, "sync") + Path.DirectorySeparatorChar), RemoteSamples.DeviceId)
            .Should().Be(expected, "結尾的分隔字元不影響");

        var bad = () => SyncCommandRules.RemoteAuditExportPath(new SyncPaths(Path.Combine(root, "sync")), "..\\evil");
        bad.Should().Throw<ArgumentException>("deviceId 不合格就不能變成路徑的一部分");
    }

    // ---- 輔助 ----

    private CommandEnvelopeTarget Target() => new(P256KeyRules.ExportSpki(hub), Aud, RemoteSamples.DeviceId, Conn);

    private CommandEnvelopeVerdict Verify(CommandEnvelope envelope, ServerTimeBounds? bounds, CommandEnvelopeTarget? target = null)
        => CommandEnvelope.Verify(envelope, Cid, target ?? Target(), bounds);

    private CommandEnvelope Sign(CommandEnvelopeBody body, ECDsa? key = null)
    {
        var signer = key ?? hub;
        CommandEnvelope.TrySign(body, data => P256KeyRules.Sign(signer, data), out var envelope).Should().BeTrue();
        return envelope!;
    }

    private static void Expect(CommandResult? result, string code, int? tier, string because)
    {
        result.Should().NotBeNull(because);
        result!.CommandId.Should().Be(Cid);
        result.State.Should().Be(RemoteResultStates.Rejected);
        result.Code.Should().Be(code, because);
        result.Tier.Should().Be(tier, because);
        result.Message.Should().BeNull("步驟與細節只進紀錄，瀏覽器看伺服器依代碼給的提示");
        result.FullAccess.Should().BeNull();
        RemoteResultRules.Check(result).Should().BeNull("回給伺服器的結果一定合格");
    }

    private static void Same(JToken? actual, JToken expected)
        => JToken.DeepEquals(actual, expected).Should().BeTrue($"{actual?.ToString(Newtonsoft.Json.Formatting.None)} 應該是 {expected.ToString(Newtonsoft.Json.Formatting.None)}");

    private static JObject Cmd(CommandEnvelopeBody body) => (JObject)JObject.Parse(SyncCommandRules.MaskBodyForAudit(body)!)["cmd"]!;

    private static CommandEnvelopeBody Body(JObject? cmd = null) => new(
        Aud,
        Cid,
        RemoteSamples.DeviceId,
        Conn,
        RemoteSamples.ConversationId,
        1,
        cmd ?? new JObject { ["cmd"] = "rename", ["conversationId"] = RemoteSamples.ConversationId, ["title"] = "週報" },
        1,
        false,
        false,
        Created,
        Created + 500,
        Created + 30_500,
        new CommandIssuer(CommandIssuerKinds.Browser, "Chrome・Windows"),
        new CommandAuth(Created - 60_000, false));

    /// <summary>DLP 認得的假權杖：前綴在執行時拼、本體是固定種子的亂數（不是任何真的憑證）</summary>
    private static string FakeToken()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random(4404);
        var tail = new string(Enumerable.Range(0, 80).Select(_ => chars[random.Next(chars.Length)]).ToArray());
        return string.Concat("sk", "-", "ant", "-", "api03", "-", tail);
    }
}
