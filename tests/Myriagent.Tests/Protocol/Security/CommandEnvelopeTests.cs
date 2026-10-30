using System.Security.Cryptography;
using System.Text;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Security;

/// <summary>
/// 遠端指令的信封 myriagent-hub-cmd-v1（docs/sync-protocol.md 14.4、設計 14.11 D2、D16、D17）：測試金鑰簽驗、十步的驗證順序
/// （每一步各一個失敗案例、先失敗的先回報）、durable 與時間的界線、重簽後內容摘要不變、簽發者標籤的 Cc／Cf 字元。
/// </summary>
public sealed class CommandEnvelopeTests : IDisposable
{
    private const string Aud = "https://hub.example.com";
    private const string Conn = "conn-AbC123";
    private const string Cid = "01J9QZ3K8M5R7T2V4W6X8Y0A1B";
    private const string OtherCid = "01J9QZ3K8M5R7T2V4W6X8Y0A1C";
    private const string OtherConversation = "0123456789abcdef0123456789abcdef";
    private const long Created = 1_791_580_000_000;

    /// <summary>收到時的伺服器時間界線：iat 之後半秒到一秒半（往返一秒）</summary>
    private static readonly ServerTimeBounds Now = new(Created + 1_000, Created + 2_000);

    private readonly ECDsa hub = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        hub.Dispose();
        impostor.Dispose();
    }

    private CommandEnvelopeTarget Target => new(P256KeyRules.ExportSpki(hub), Aud, RemoteSamples.DeviceId, Conn);

    [Fact]
    public void 測試金鑰簽驗_驗過得到同一份內容與摘要()
    {
        var body = Body();
        var envelope = Sign(body);

        envelope.V.Should().Be("myriagent-hub-cmd-v1");
        var verdict = CommandEnvelope.Verify(envelope, Cid, Target, Now);

        verdict.Ok.Should().BeTrue(verdict.Detail);
        verdict.Step.Should().BeNull();
        verdict.Code.Should().BeNull();
        JToken.DeepEquals(verdict.Body!.Cmd, body.Cmd).Should().BeTrue();
        (verdict.Body with { Cmd = body.Cmd }).Should().Be(body);
        verdict.ContentSha256.Should().Be(CommandEnvelope.ContentDigest(body)).And.MatchRegex("^[0-9a-f]{64}$");
        verdict.CountsAsTampering.Should().BeFalse();
    }

    [Fact]
    public void 簽的位元組是用途前綴加換行加body原文_body的鍵順序固定_null的選用欄位省略()
    {
        var body = Body(Stop()) with { Tier = 0 };
        var json = CommandEnvelope.SerializeBody(body);

        json.Should().Be(
            "{\"aud\":\"https://hub.example.com\",\"cid\":\"01J9QZ3K8M5R7T2V4W6X8Y0A1B\",\"dev\":\"d_01J9QZ3K8M5R7T2V4W6X8Y0A1B\",\"conn\":\"conn-AbC123\","
            + "\"conv\":\"5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d\",\"epoch\":1,\"cmd\":{\"cmd\":\"stop\",\"conversationId\":\"5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d\"},"
            + "\"tier\":0,\"fullAccess\":false,\"durable\":false,\"created\":1791580000000,\"iat\":1791580000500,\"exp\":1791580030500,"
            + "\"issuer\":{\"kind\":\"browser\",\"label\":\"Chrome・Windows\"},\"auth\":{\"uvAt\":1791579940000,\"defaultPassword\":false}}");
        Encoding.UTF8.GetString(CommandEnvelope.SignedData(json)).Should().Be("myriagent-hub-cmd-v1\n" + json);

        var device = CommandEnvelope.SerializeBody(Body(Logout()) with { Conv = null, Epoch = null, Auth = new CommandAuth(null, true) });
        device.Should().NotContain("\"conv\"").And.NotContain("\"epoch\"").And.NotContain("\"uvAt\"");
        device.Should().EndWith("\"auth\":{\"defaultPassword\":true}}");
    }

    [Fact]
    public void 內容摘要是固定欄位的JSON陣列的SHA256()
    {
        var body = Body(Stop()) with { Tier = 0 };
        var array = "[\"https://hub.example.com\",\"01J9QZ3K8M5R7T2V4W6X8Y0A1B\",\"d_01J9QZ3K8M5R7T2V4W6X8Y0A1B\",\"5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d\",1,"
                    + "{\"cmd\":\"stop\",\"conversationId\":\"5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d\"},0,false,false,1791580000000,"
                    + "{\"kind\":\"browser\",\"label\":\"Chrome・Windows\"},{\"uvAt\":1791579940000,\"defaultPassword\":false}]";

        CommandEnvelope.ContentDigest(body).Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(array))));

        var device = Body(Logout()) with { Conv = null, Epoch = null, Auth = new CommandAuth(null, true) };
        var deviceArray = "[\"https://hub.example.com\",\"01J9QZ3K8M5R7T2V4W6X8Y0A1B\",\"d_01J9QZ3K8M5R7T2V4W6X8Y0A1B\",null,null,"
                          + "{\"cmd\":\"accountLogout\",\"accountId\":\"acc-1\"},1,false,false,1791580000000,"
                          + "{\"kind\":\"browser\",\"label\":\"Chrome・Windows\"},{\"uvAt\":null,\"defaultPassword\":true}]";
        CommandEnvelope.ContentDigest(device).Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deviceArray))));
    }

    public static TheoryData<string> Steps() => new(CommandEnvelopeSteps.Order);

    [Theory]
    [MemberData(nameof(Steps))]
    public void 驗證的每一步各有一個失敗案例(string step)
    {
        var draft = Draft.From(Body(), hub);
        Mutators[step](draft, this);

        var verdict = draft.Verify(Target);

        verdict.Step.Should().Be(step, verdict.Detail);
        verdict.Code.Should().Be(RemoteResultCodes.Invalid);
        verdict.Ok.Should().BeFalse();
        (verdict.Body != null).Should().Be(CommandEnvelopeSteps.Order.ToList().IndexOf(step) > CommandEnvelopeSteps.Order.ToList().IndexOf(CommandEnvelopeSteps.Parse),
            "解析過了才有內容");
    }

    [Fact]
    public void 先失敗的步驟先回報_任兩步同時失敗時回報前面那一步()
    {
        var order = CommandEnvelopeSteps.Order;
        Mutators.Keys.Should().BeEquivalentTo(order);
        for (var i = 0; i < order.Count; i++)
        {
            for (var j = i + 1; j < order.Count; j++)
            {
                var draft = Draft.From(Body(), hub);
                Mutators[order[i]](draft, this);
                Mutators[order[j]](draft, this);

                draft.Verify(Target).Step.Should().Be(order[i], $"{order[i]} 與 {order[j]} 同時失敗");
            }
        }
    }

    [Fact]
    public void 竄改計數只算簽章與aud_dev不符()
    {
        var counted = new List<string>();
        foreach (var step in CommandEnvelopeSteps.Order)
        {
            var draft = Draft.From(Body(), hub);
            Mutators[step](draft, this);
            if (draft.Verify(Target).CountsAsTampering)
                counted.Add(step);
        }

        counted.Should().Equal(CommandEnvelopeSteps.Signature, CommandEnvelopeSteps.Audience, CommandEnvelopeSteps.Device);
    }

    [Fact]
    public void 版本_信封是null或版本不同()
    {
        CommandEnvelope.Verify(null, Cid, Target, Now).Step.Should().Be(CommandEnvelopeSteps.Version);
        var envelope = Sign(Body());
        CommandEnvelope.Verify(envelope with { V = "MYRIAGENT-HUB-CMD-V1" }, Cid, Target, Now).Step.Should().Be(CommandEnvelopeSteps.Version);
        CommandEnvelope.Verify(envelope with { V = null! }, Cid, Target, Now).Step.Should().Be(CommandEnvelopeSteps.Version);
    }

    [Fact]
    public void 大小_UTF8剛好128KB可以_多一個位元組或有不成對的代理字元不行()
    {
        var baseline = CommandEnvelope.SerializeBody(Body(Approve(pad: string.Empty)));
        var room = CommandEnvelope.MaxBodyBytes - Encoding.UTF8.GetByteCount(baseline);
        // 用三個位元組的中文字加 ASCII 補到剛好：位元組數算的是 UTF-8，不是字元數
        var pad = new string('字', room / 3) + new string('a', room % 3);
        var exact = Body(Approve(pad));
        Encoding.UTF8.GetByteCount(CommandEnvelope.SerializeBody(exact)).Should().Be(CommandEnvelope.MaxBodyBytes);

        CommandEnvelope.Verify(Sign(exact), Cid, Target, Now).Ok.Should().BeTrue();
        CommandEnvelope.TrySign(Body(Approve(pad + "a")), Signer(hub), out var tooLarge).Should().BeFalse();
        tooLarge.Should().BeNull();

        var oversized = CommandEnvelope.SerializeBody(Body(Approve(pad + "a")));
        CommandEnvelope.Verify(new CommandEnvelope(CommandEnvelope.Version, oversized, LenientSign(oversized)), Cid, Target, Now).Step.Should().Be(CommandEnvelopeSteps.Size);

        var lone = ((char)0xD800).ToString();
        var surrogate = CommandEnvelope.SerializeBody(Body(Approve("x" + lone)));
        CommandEnvelope.TrySign(Body(Approve("x" + lone)), Signer(hub), out _).Should().BeFalse("簽不出唯一的位元組");
        CommandEnvelope.Verify(new CommandEnvelope(CommandEnvelope.Version, surrogate, LenientSign(surrogate)), Cid, Target, Now).Step.Should().Be(CommandEnvelopeSteps.Size,
            "U+D800 與 U+FFFD 會編成同樣的位元組");
        CommandEnvelope.Verify(new CommandEnvelope(CommandEnvelope.Version, null!, "x"), Cid, Target, Now).Step.Should().Be(CommandEnvelopeSteps.Size);
    }

    [Fact]
    public void 簽章_別的金鑰_改一個字_DER格式_null都驗不過()
    {
        var envelope = Sign(Body());
        var data = CommandEnvelope.SignedData(envelope.Body);
        var der = Base64UrlRules.Encode(hub.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        Check(Sign(Body(), impostor)).Should().Be(CommandEnvelopeSteps.Signature, "冒充伺服器（X1）");
        Check(envelope with { Body = envelope.Body.Replace("\"tier\":1", "\"tier\":2", StringComparison.Ordinal) }).Should().Be(CommandEnvelopeSteps.Signature, "途中改層級");
        Check(envelope with { Sig = der }).Should().Be(CommandEnvelopeSteps.Signature, "只收 P1363");
        Check(envelope with { Sig = null! }).Should().Be(CommandEnvelopeSteps.Signature);
        CommandEnvelope.Verify(envelope, Cid, Target with { HubSpki = P256KeyRules.ExportSpki(impostor) }, Now).Step.Should().Be(CommandEnvelopeSteps.Signature, "釘的是別把");
        CommandEnvelope.Verify(envelope, Cid, Target with { HubSpki = "不是公鑰" }, Now).Step.Should().Be(CommandEnvelopeSteps.Signature);
    }

    public static TheoryData<string> BrokenBodies() => new(
        "{\"aud\"|{\"aud\":\"x\",\"aud\"",                       // 重複的鍵（頂層）
        "\"cmd\":\"approve\"|\"cmd\":\"approve\",\"cmd\":\"stop\"", // 重複的鍵（巢狀在 cmd 裡）
        "{|{/*註解*/",
        "\"tier\":1|\"tier\"/*註解*/:1",
        "}}|}} {}",                                              // 結尾多餘的內容（body 結尾是 auth 與 body 的兩個右大括號）
        "\"tier\":1|\"tier\":\"1\"",
        "\"tier\":1|\"tier\":1.0",
        "\"tier\":1|\"tier\":99999999999",
        "\"created\":1791580000000|\"created\":1791580000000.5",
        "\"created\":1791580000000|\"created\":123456789012345678901234567890",
        "\"fullAccess\":false|\"fullAccess\":\"false\"",
        "\"durable\":false|\"durable\":0",
        "\"aud\":\"https://hub.example.com\"|\"aud\":null",
        "\"aud\":\"https://hub.example.com\"|\"aud\":1",
        "\"conv\":\"5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d\"|\"conv\":1",
        "\"epoch\":1|\"epoch\":\"1\"",
        "\"iat\":1791580000500,|",                               // 少了必填的鍵
        "{\"aud\"|{\"extra\":1,\"aud\"",                         // 不認得的鍵
        "\"cmd\":{\"cmd\":\"approve\"|\"cmd\":{\"cmd\":1",
        "\"label\":\"Chrome・Windows\"}|\"label\":\"Chrome・Windows\",\"by\":\"x\"}",
        "\"label\":\"Chrome・Windows\"|\"label\":7",
        "\"defaultPassword\":false|\"defaultPassword\":false,\"x\":1",
        "\"uvAt\":1791579940000,\"defaultPassword\":false|\"uvAt\":1791579940000",
        "\"uvAt\":1791579940000|\"uvAt\":\"1791579940000\"");

    [Theory]
    [MemberData(nameof(BrokenBodies))]
    public void 解析_只收一種解讀_鍵與型別照協定(string edit)
    {
        var parts = edit.Split('|');
        var json = CommandEnvelope.SerializeBody(Body());
        json.Should().Contain(parts[0]);
        var index = json.IndexOf(parts[0], StringComparison.Ordinal);
        var broken = json[..index] + parts[1] + json[(index + parts[0].Length)..];

        var verdict = Check(new CommandEnvelope(CommandEnvelope.Version, broken, Signer(hub)(CommandEnvelope.SignedData(broken))));

        verdict.Should().Be(CommandEnvelopeSteps.Parse, broken);
    }

    [Fact]
    public void 解析_深度32可以_33不行_選用欄位可以是null()
    {
        Check(Sign(Body(Approve(nest: 30)))).Should().BeNull("body、cmd 兩層加 30 層陣列＝32");
        Check(Sign(Body(Approve(nest: 31)))).Should().Be(CommandEnvelopeSteps.Parse);

        var json = CommandEnvelope.SerializeBody(Body(Logout()) with { Conv = null, Epoch = null })
            .Replace("\"conn\":\"conn-AbC123\"", "\"conn\":\"conn-AbC123\",\"conv\":null,\"epoch\":null", StringComparison.Ordinal)
            .Replace("\"uvAt\":1791579940000", "\"uvAt\":null", StringComparison.Ordinal);
        var verdict = CommandEnvelope.Verify(new CommandEnvelope(CommandEnvelope.Version, json, Signer(hub)(CommandEnvelope.SignedData(json))), Cid, Target, Now);
        verdict.Ok.Should().BeTrue(verdict.Detail);
        verdict.Body!.Conv.Should().BeNull();
        verdict.Body.Auth.UvAt.Should().BeNull();
    }

    [Fact]
    public void 綁定_aud_dev_conn_各自不符()
    {
        CommandEnvelope.Verify(Sign(Body()), Cid, Target with { Aud = "https://hub.example.com/" }, Now).Step.Should().Be(CommandEnvelopeSteps.Audience);
        CommandEnvelope.Verify(Sign(Body()), Cid, Target with { DeviceId = "d_01J9QZ3K8M5R7T2V4W6X8Y0A1C" }, Now).Step.Should().Be(CommandEnvelopeSteps.Device, "把 A 電腦的信封送到 B（X2）");
        CommandEnvelope.Verify(Sign(Body()), Cid, Target with { ConnectionId = "conn-new" }, Now).Step.Should().Be(CommandEnvelopeSteps.Connection);
    }

    [Fact]
    public void cid_要是ULID而且等於外層的commandId()
    {
        Check(Sign(Body()), commandId: null).Should().Be(CommandEnvelopeSteps.CommandId);
        Check(Sign(Body()), commandId: OtherCid).Should().Be(CommandEnvelopeSteps.CommandId);
        Check(Sign(Body() with { Cid = Cid.ToLowerInvariant() }), commandId: Cid.ToLowerInvariant()).Should().Be(CommandEnvelopeSteps.CommandId, "不是 ULID");
        Check(Sign(Body() with { Cid = "81J9QZ3K8M5R7T2V4W6X8Y0A1B" }), commandId: "81J9QZ3K8M5R7T2V4W6X8Y0A1B").Should().Be(CommandEnvelopeSteps.CommandId, "第一個字 ≤7");
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(int.MaxValue, false)]
    public void tier要在0到2(int tier, bool ok)
        => Check(Sign(Body() with { Tier = tier })).Should().Be(ok ? null : CommandEnvelopeSteps.TierRange);

    [Fact]
    public void 時間_信封自己的時間不合理是invalid()
    {
        // exp − iat：剛好 60 秒可以
        TimeCheck(Body() with { Iat = Created, Exp = Created + 60_000 }, new ServerTimeBounds(Created, Created + 10)).Should().Be((null, null));
        TimeCheck(Body() with { Iat = Created, Exp = Created + 60_001 }, new ServerTimeBounds(Created, Created + 10)).Should().Be((CommandEnvelopeSteps.Time, RemoteResultCodes.Invalid));
        TimeCheck(Body() with { Exp = Created + 499 }, Now).Should().Be((CommandEnvelopeSteps.Time, RemoteResultCodes.Invalid), "exp 早於 iat");
        TimeCheck(Body() with { Iat = Created - 1 }, Now).Should().Be((CommandEnvelopeSteps.Time, RemoteResultCodes.Invalid), "iat 早於 created");

        // 非 durable：exp − created 剛好 60 秒可以
        TimeCheck(Body() with { Iat = Created + 10_000, Exp = Created + 60_000 }, Now).Should().Be((null, null));
        TimeCheck(Body() with { Iat = Created + 10_000, Exp = Created + 60_001 }, Now).Should().Be((CommandEnvelopeSteps.Time, RemoteResultCodes.Invalid));

        // durable 只限型錄標了 DurableAllowed 的（目前只有 accountLogout）
        RemoteCommandCatalog.All.Where(s => s.DurableAllowed).Select(s => s.Name).Should().Equal("accountLogout");
        TimeCheck(Body() with { Durable = true }, Now).Should().Be((CommandEnvelopeSteps.Time, RemoteResultCodes.Invalid));
        TimeCheck(Body(Logout()) with { Conv = null, Epoch = null, Durable = true }, Now).Should().Be((null, null));
    }

    [Fact]
    public void 時間_durable以原始的created算7天上限()
    {
        const long sevenDays = 7L * 24 * 60 * 60 * 1000;
        var resigned = Body(Logout()) with { Conv = null, Epoch = null, Durable = true, Iat = Created + sevenDays - 10_000, Exp = Created + sevenDays + 20_000 };

        TimeCheck(resigned, new ServerTimeBounds(Created + sevenDays - 1_000, Created + sevenDays)).Should().Be((null, null), "剛好 7 天");
        TimeCheck(resigned, new ServerTimeBounds(Created + sevenDays - 999, Created + sevenDays + 1)).Should().Be((CommandEnvelopeSteps.Time, RemoteResultCodes.Expired));
        TimeCheck(resigned with { Durable = false }, new ServerTimeBounds(Created + sevenDays - 1_000, Created + sevenDays)).Should().Be(
            (CommandEnvelopeSteps.Time, RemoteResultCodes.Invalid), "不是 durable 就要在 created＋60 秒內到期");
    }

    [Fact]
    public void 時間_伺服器時間的上界晚於exp是過期_iat晚於下界加15秒是還沒生效()
    {
        var body = Body();
        TimeCheck(body, new ServerTimeBounds(body.Exp - 1_000, body.Exp)).Should().Be((null, null), "上界剛好等於 exp");
        TimeCheck(body, new ServerTimeBounds(body.Exp - 1_000, body.Exp + 1)).Should().Be((CommandEnvelopeSteps.Time, RemoteResultCodes.Expired));
        TimeCheck(body, new ServerTimeBounds(body.Iat - 15_000, body.Iat)).Should().Be((null, null), "iat 剛好是下界＋15 秒");
        TimeCheck(body, new ServerTimeBounds(body.Iat - 15_001, body.Iat)).Should().Be((CommandEnvelopeSteps.Time, RemoteResultCodes.Expired));
    }

    [Fact]
    public void 時間_伺服器時間推算不可用時只收stop()
    {
        TimeCheck(Body(Stop()) with { Tier = 0 }, null).Should().Be((null, null));
        TimeCheck(Body(), null).Should().Be((CommandEnvelopeSteps.Time, RemoteResultCodes.Expired));
        TimeCheck(Body(Logout()) with { Conv = null, Epoch = null, Durable = true }, null).Should().Be((CommandEnvelopeSteps.Time, RemoteResultCodes.Expired));
        TimeCheck(Body(Stop()) with { Tier = 0, Exp = Created + 60_001 }, null).Should().Be((CommandEnvelopeSteps.Time, RemoteResultCodes.Invalid), "信封自己的時間照樣檢查");
    }

    [Fact]
    public void 時間_極端值不溢位()
    {
        TimeCheck(Body() with { Created = long.MinValue, Iat = long.MinValue, Exp = long.MaxValue }, Now).Should().Be((CommandEnvelopeSteps.Time, RemoteResultCodes.Invalid));
        TimeCheck(Body() with { Created = long.MaxValue - 10, Iat = long.MaxValue - 5, Exp = long.MaxValue }, new ServerTimeBounds(long.MaxValue, long.MaxValue))
            .Should().Be((null, null));
        TimeCheck(Body() with { Created = long.MaxValue - 10, Iat = long.MaxValue - 5, Exp = long.MaxValue }, new ServerTimeBounds(long.MinValue, long.MinValue))
            .Should().Be((CommandEnvelopeSteps.Time, RemoteResultCodes.Expired), "下界＋15 秒不溢位");
    }

    [Fact]
    public void 簽發者_kind要是browser_標籤的Cc_Cf_Zl_Zp與不成對的代理字元拒絕()
    {
        Check(Sign(Body() with { Issuer = new CommandIssuer("server", "Chrome") })).Should().Be(CommandEnvelopeSteps.Issuer);
        Check(Sign(Body() with { Issuer = new CommandIssuer("Browser", "Chrome") })).Should().Be(CommandEnvelopeSteps.Issuer);
        Check(Sign(Body() with { Issuer = new CommandIssuer("browser", "Chrome" + (char)0x202E + "swodniW") })).Should().Be(CommandEnvelopeSteps.Issuer, "U+202E 由右至左覆寫");

        foreach (var bad in new[] { 0x202E, 0x200B, 0x200E, 0xFEFF, 0x00AD, 0x000A, 0x0000, 0x007F, 0x0085, 0x2028, 0x2029 })
            CommandEnvelope.IsValidIssuerLabel("Chrome" + (char)bad).Should().BeFalse($"U+{bad:X4}");
        CommandEnvelope.IsValidIssuerLabel("a" + (char)0xD800).Should().BeFalse("不成對的高代理");
        CommandEnvelope.IsValidIssuerLabel((char)0xDC00 + "a").Should().BeFalse("不成對的低代理");
        CommandEnvelope.IsValidIssuerLabel(null).Should().BeFalse();

        var emoji = char.ConvertFromUtf32(0x1F600);
        CommandEnvelope.IsValidIssuerLabel(string.Empty).Should().BeTrue();
        CommandEnvelope.IsValidIssuerLabel("Edge・macOS（公司）").Should().BeTrue();
        CommandEnvelope.IsValidIssuerLabel(new string('a', 64)).Should().BeTrue();
        CommandEnvelope.IsValidIssuerLabel(new string('a', 65)).Should().BeFalse();
        CommandEnvelope.IsValidIssuerLabel(string.Concat(Enumerable.Repeat(emoji, 64))).Should().BeTrue("以 Unicode 字元計，不是 UTF-16 單位");
        CommandEnvelope.IsValidIssuerLabel(string.Concat(Enumerable.Repeat(emoji, 65))).Should().BeFalse();
    }

    [Fact]
    public void 簽發者_device只在連線協商出peer1時接受_大小寫要相同()
    {
        var device = Sign(Body() with { Issuer = new CommandIssuer(CommandIssuerKinds.Device, "DEMO-PC-3"), Auth = new CommandAuth(null, false) });
        Check(device).Should().Be(CommandEnvelopeSteps.Issuer, "舊版桌面（沒有 peer1）不認得另一台桌面發起的信封");
        CommandEnvelope.Verify(device, Cid, Target with { AcceptsDeviceIssuer = true }, Now).Step.Should().BeNull("桌面遠端操作（14.7）");
        CommandEnvelope.Verify(Sign(Body() with { Issuer = new CommandIssuer("Device", "DEMO-PC-3") }), Cid, Target with { AcceptsDeviceIssuer = true }, Now)
            .Step.Should().Be(CommandEnvelopeSteps.Issuer);
        CommandEnvelope.Verify(Sign(Body() with { Issuer = new CommandIssuer(CommandIssuerKinds.Device, "MYRIAGENT" + (char)0x202E) }), Cid, Target with { AcceptsDeviceIssuer = true }, Now)
            .Step.Should().Be(CommandEnvelopeSteps.Issuer, "標籤的規則照舊");
        CommandEnvelope.Verify(Sign(Body()), Cid, Target with { AcceptsDeviceIssuer = true }, Now).Step.Should().BeNull("瀏覽器照舊接受");
        CommandEnvelope.IsAcceptedIssuerKind(CommandIssuerKinds.Browser, false).Should().BeTrue();
        CommandEnvelope.IsAcceptedIssuerKind(CommandIssuerKinds.Device, false).Should().BeFalse();
        CommandEnvelope.IsAcceptedIssuerKind(CommandIssuerKinds.Device, true).Should().BeTrue();
        CommandEnvelope.IsAcceptedIssuerKind(null, true).Should().BeFalse();
    }

    [Fact]
    public void 對象_路由Owner要帶conv_cmd的conversationId要等於conv()
    {
        Check(Sign(Body() with { Conv = null, Epoch = null })).Should().Be(CommandEnvelopeSteps.Target, "對話指令要帶 conv");
        Check(Sign(Body(Approve(conversationId: OtherConversation)))).Should().Be(CommandEnvelopeSteps.Target, "送錯對話（X3）");
        Check(Sign(Body(Approve(conversationId: null)))).Should().Be(CommandEnvelopeSteps.Target);
        Check(Sign(Body() with { Conv = RemoteSamples.ConversationId.ToUpperInvariant() })).Should().Be(CommandEnvelopeSteps.Target, "大寫的 id 一律拒絕");
        Check(Sign(Body() with { Epoch = 0 })).Should().Be(CommandEnvelopeSteps.Target);
        Check(Sign(Body() with { Epoch = null })).Should().Be(CommandEnvelopeSteps.Target, "conv 與 epoch 要一起帶");
        Check(Sign(Body() with { Conv = null })).Should().Be(CommandEnvelopeSteps.Target);
        Check(Sign(Body() with { Epoch = 7 })).Should().BeNull("epoch 對不對是閘門 5c 的事");
    }

    [Fact]
    public void 對象_路由Device不帶conv_deviceId有帶時要等於dev()
    {
        var logout = Body(Logout()) with { Conv = null, Epoch = null };
        Check(Sign(logout)).Should().BeNull();
        Check(Sign(Body(Logout()))).Should().Be(CommandEnvelopeSteps.Target, "路由 Device 的指令不帶 conv");

        Check(Sign(logout with { Cmd = Logout(deviceId: RemoteSamples.DeviceId) })).Should().BeNull();
        Check(Sign(logout with { Cmd = Logout(deviceId: "d_01J9QZ3K8M5R7T2V4W6X8Y0A1C") })).Should().Be(CommandEnvelopeSteps.Target, "瀏覽器選的是別台");
        var numeric = Logout();
        numeric["deviceId"] = 1;
        Check(Sign(logout with { Cmd = numeric })).Should().Be(CommandEnvelopeSteps.Target);

        var create = new JObject { ["cmd"] = RemoteCommandCatalog.ConversationCreate, ["accountId"] = "acc-1", ["permissionMode"] = "Ask" };
        Check(Sign(logout with { Cmd = create })).Should().BeNull("conversationCreate 不帶 conv，改驗 dev");
    }

    [Fact]
    public void 對象_型錄外的指令只看一致_路由由閘門5a判斷()
    {
        var ready = new JObject { ["cmd"] = "ready" };
        RemoteCommandCatalog.Find("ready").Should().BeNull();
        Check(Sign(Body(ready) with { Conv = null, Epoch = null })).Should().BeNull("信封只看本身與一致；伺服器自己處理的指令由 5a 拒絕");

        var withConversation = new JObject { ["cmd"] = "mystery", ["conversationId"] = RemoteSamples.ConversationId };
        Check(Sign(Body(withConversation) with { Conv = null, Epoch = null })).Should().Be(CommandEnvelopeSteps.Target, "有 conversationId 就要帶 conv");
        Check(Sign(Body(withConversation))).Should().BeNull();
    }

    [Fact]
    public void durable重簽_換conn與iat_exp後內容摘要不變_舊信封在新連線不能用()
    {
        var first = Body(Logout()) with { Conv = null, Epoch = null, Durable = true };
        var later = Created + 3L * 24 * 60 * 60 * 1000;
        var resigned = first with { Conn = "conn-after-hello", Iat = later, Exp = later + 30_000 };
        var bounds = new ServerTimeBounds(later + 500, later + 1_000);

        var oldEnvelope = Sign(first);
        var newEnvelope = Sign(resigned);

        CommandEnvelope.ContentDigest(resigned).Should().Be(CommandEnvelope.ContentDigest(first));
        newEnvelope.Sig.Should().NotBe(oldEnvelope.Sig);
        var target = Target with { ConnectionId = "conn-after-hello" };
        CommandEnvelope.Verify(oldEnvelope, Cid, target, bounds).Step.Should().Be(CommandEnvelopeSteps.Connection);
        var verdict = CommandEnvelope.Verify(newEnvelope, Cid, target, bounds);
        verdict.Ok.Should().BeTrue(verdict.Detail);
        verdict.ContentSha256.Should().Be(CommandEnvelope.ContentDigest(first), "電腦以它在 commands_seen 認出同一個指令");
    }

    [Fact]
    public void 內容摘要涵蓋不隨重簽改變的每個欄位()
    {
        var body = Body();
        var changed = new Dictionary<string, CommandEnvelopeBody>
        {
            ["aud"] = body with { Aud = "https://other.example" },
            ["cid"] = body with { Cid = OtherCid },
            ["dev"] = body with { Dev = "d_01J9QZ3K8M5R7T2V4W6X8Y0A1C" },
            ["conv"] = body with { Conv = OtherConversation },
            ["epoch"] = body with { Epoch = 2 },
            ["cmd"] = body with { Cmd = Approve(choice: "deny") },
            ["cmd 的鍵順序"] = body with { Cmd = new JObject(body.Cmd.Properties().Reverse().Select(p => new JProperty(p.Name, p.Value))) },
            ["tier"] = body with { Tier = 2 },
            ["fullAccess"] = body with { FullAccess = true },
            ["durable"] = body with { Durable = true },
            ["created"] = body with { Created = Created + 1 },
            ["issuer.kind"] = body with { Issuer = body.Issuer with { Kind = "server" } },
            ["issuer.label"] = body with { Issuer = body.Issuer with { Label = "Firefox" } },
            ["auth.uvAt"] = body with { Auth = body.Auth with { UvAt = null } },
            ["auth.defaultPassword"] = body with { Auth = body.Auth with { DefaultPassword = true } },
        };
        var digests = changed.ToDictionary(p => p.Key, p => CommandEnvelope.ContentDigest(p.Value));
        var original = CommandEnvelope.ContentDigest(body);

        foreach (var (field, digest) in digests)
            digest.Should().NotBe(original, field);
        digests.Values.Distinct().Should().HaveCount(digests.Count);
        CommandEnvelope.ContentDigest(body with { Conn = "x", Iat = 1, Exp = 2 }).Should().Be(original, "conn、iat、exp 不在摘要裡");
        CommandEnvelope.ContentDigest(body with { Conv = null, Epoch = null }).Should().NotBe(CommandEnvelope.ContentDigest(body with { Conv = null, Epoch = 1 }));
    }

    [Fact]
    public void TrySign_把要簽的位元組交給簽章委派()
    {
        byte[]? seen = null;
        var body = Body();

        CommandEnvelope.TrySign(body, data =>
        {
            seen = data.ToArray();
            return P256KeyRules.Sign(hub, data);
        }, out var envelope).Should().BeTrue();

        envelope!.Body.Should().Be(CommandEnvelope.SerializeBody(body));
        seen.Should().Equal(CommandEnvelope.SignedData(envelope.Body));
        CommandEnvelope.Verify(SyncJson.Deserialize<CommandEnvelope>(SyncJson.Serialize(envelope))!, Cid, Target, Now).Ok.Should().BeTrue("線上 {v, body, sig} 往返");
        SyncJson.Serialize(envelope).Should().StartWith("{\"v\":\"myriagent-hub-cmd-v1\",\"body\":");

        var called = false;
        CommandEnvelope.TrySign(body with { Issuer = new CommandIssuer("browser", new string('x', CommandEnvelope.MaxBodyBytes)) }, _ =>
        {
            called = true;
            return string.Empty;
        }, out _).Should().BeFalse();
        called.Should().BeFalse("太大的不簽");
    }

    [Fact]
    public void VerifyHash_與Verify對同一份內容等價()
    {
        var data = Encoding.UTF8.GetBytes("大內容");
        var spki = P256KeyRules.ExportSpki(hub);
        var sig = P256KeyRules.Sign(hub, data);
        var hash = SHA256.HashData(data);
        var der = Base64UrlRules.Encode(hub.SignHash(hash, DSASignatureFormat.Rfc3279DerSequence));

        P256KeyRules.Verify(spki, data, sig).Should().BeTrue();
        P256KeyRules.VerifyHash(spki, hash, sig).Should().BeTrue();
        P256KeyRules.VerifyHash(spki, SHA256.HashData("別的內容"u8), sig).Should().BeFalse();
        P256KeyRules.VerifyHash(spki, hash.AsSpan(0, 31), sig).Should().BeFalse("只收 SHA-256 的 32 位元組");
        P256KeyRules.VerifyHash(spki, SHA512.HashData(data), sig).Should().BeFalse();
        P256KeyRules.VerifyHash(P256KeyRules.ExportSpki(impostor), hash, sig).Should().BeFalse();
        P256KeyRules.VerifyHash(spki, hash, der).Should().BeFalse("只收 P1363");
        P256KeyRules.VerifyHash(spki, hash, null).Should().BeFalse();
        P256KeyRules.VerifyHash(null, hash, sig).Should().BeFalse();
    }

    /// <summary>每一步各一個只讓那一步失敗的改法（彼此不衝突，任兩個可以同時套用）</summary>
    private static readonly Dictionary<string, Action<Draft, CommandEnvelopeTests>> Mutators = new()
    {
        [CommandEnvelopeSteps.Version] = (d, _) => d.Version = "myriagent-hub-cmd-v2",
        [CommandEnvelopeSteps.Size] = (d, _) => d.Cmd["pad"] = new string('a', CommandEnvelope.MaxBodyBytes),
        [CommandEnvelopeSteps.Signature] = (d, t) => d.Key = t.impostor,
        [CommandEnvelopeSteps.Parse] = (d, _) => d.Cmd["deep"] = JArray.Parse(new string('[', 40) + new string(']', 40)),
        [CommandEnvelopeSteps.Audience] = (d, _) => d.Json["aud"] = "https://evil.example",
        [CommandEnvelopeSteps.Device] = (d, _) => d.Json["dev"] = "d_01J9QZ3K8M5R7T2V4W6X8Y0A1C",
        [CommandEnvelopeSteps.Connection] = (d, _) => d.Json["conn"] = "conn-other",
        [CommandEnvelopeSteps.CommandId] = (d, _) => d.CommandId = OtherCid,
        [CommandEnvelopeSteps.TierRange] = (d, _) => d.Json["tier"] = 3,
        [CommandEnvelopeSteps.Time] = (d, _) => d.Json["exp"] = d.Json["iat"]!.Value<long>() + 60_001,
        [CommandEnvelopeSteps.Issuer] = (d, _) => d.Json["issuer"]!["kind"] = "server",
        [CommandEnvelopeSteps.Target] = (d, _) => d.Cmd["conversationId"] = OtherConversation,
    };

    private static CommandEnvelopeBody Body(JObject? cmd = null) => new(
        Aud,
        Cid,
        RemoteSamples.DeviceId,
        Conn,
        RemoteSamples.ConversationId,
        1,
        cmd ?? Approve(),
        1,
        false,
        false,
        Created,
        Created + 500,
        Created + 30_500,
        new CommandIssuer(CommandIssuerKinds.Browser, "Chrome・Windows"),
        new CommandAuth(Created - 60_000, false));

    private static JObject Approve(string? pad = null, int nest = 0, string? conversationId = RemoteSamples.ConversationId, string choice = "allow")
    {
        var command = new JObject { ["cmd"] = "approve" };
        if (conversationId != null)
            command["conversationId"] = conversationId;
        command["itemId"] = "a1b2c3";
        command["choice"] = choice;
        command["requestDigest"] = RemoteSamples.Digest;
        if (pad != null)
            command["message"] = pad;
        if (nest > 0)
            command["deep"] = JArray.Parse(new string('[', nest) + new string(']', nest));
        return command;
    }

    private static JObject Stop() => new() { ["cmd"] = RemoteCommandCatalog.Stop, ["conversationId"] = RemoteSamples.ConversationId };

    private static JObject Logout(string? deviceId = null)
    {
        var command = new JObject { ["cmd"] = "accountLogout", ["accountId"] = "acc-1" };
        if (deviceId != null)
            command["deviceId"] = deviceId;
        return command;
    }

    private static EnvelopeSigner Signer(ECDsa key) => data => P256KeyRules.Sign(key, data);

    private CommandEnvelope Sign(CommandEnvelopeBody body, ECDsa? key = null)
    {
        CommandEnvelope.TrySign(body, Signer(key ?? hub), out var envelope).Should().BeTrue();
        return envelope!;
    }

    /// <summary>不經過嚴格編碼直接簽（模擬伺服器以外的人產生的信封）</summary>
    private string LenientSign(string body) => P256KeyRules.Sign(hub, Encoding.UTF8.GetBytes(CommandEnvelope.Version + "\n" + body));

    private string? Check(CommandEnvelope envelope, string? commandId = Cid) => CommandEnvelope.Verify(envelope, commandId, Target, Now).Step;

    private (string? Step, string? Code) TimeCheck(CommandEnvelopeBody body, ServerTimeBounds? bounds)
    {
        var verdict = CommandEnvelope.Verify(Sign(body), Cid, Target, bounds);
        return (verdict.Step, verdict.Code);
    }

    /// <summary>還沒簽的信封：改 body 的 JSON、版本、簽章的金鑰或外層的 commandId 之後再簽、再驗</summary>
    private sealed class Draft
    {
        public required JObject Json { get; init; }

        public required ECDsa Key { get; set; }

        public string Version { get; set; } = CommandEnvelope.Version;

        public string CommandId { get; set; } = Cid;

        public JObject Cmd => (JObject)Json["cmd"]!;

        public static Draft From(CommandEnvelopeBody body, ECDsa key) => new() { Json = JObject.Parse(CommandEnvelope.SerializeBody(body)), Key = key };

        public CommandEnvelopeVerdict Verify(CommandEnvelopeTarget target)
        {
            var json = Json.ToString(Formatting.None);
            var sig = P256KeyRules.Sign(Key, Encoding.UTF8.GetBytes(CommandEnvelope.Version + "\n" + json));
            return CommandEnvelope.Verify(new CommandEnvelope(Version, json, sig), CommandId, target, Now);
        }
    }
}
