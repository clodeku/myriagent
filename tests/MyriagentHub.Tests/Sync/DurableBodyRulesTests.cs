using Myriagent.Security;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Sync;

/// <summary>durable 指令的未簽 body（WP4.5 計畫步 8；D15、D27）：不含 conn、iat、exp，以新的連線與時間重簽時 created 與內容摘要不變</summary>
public sealed class DurableBodyRulesTests
{
    private const long Created = 1_791_580_000_000;

    [Fact]
    public void Serialize_鍵順序固定_不含conn_iat_exp_null的省略()
    {
        var body = Body(conv: null, epoch: null, uvAt: Created - 5);
        DurableBodyRules.Serialize(body).Should().Be(
            "{\"aud\":\"https://hub.example.com\",\"cid\":\"01K7Z0000000000000000000AB\",\"dev\":\"dv_a\",\"cmd\":{\"cmd\":\"accountLogout\",\"accountId\":\"acct-a\",\"deviceId\":\"dv_a\"},"
            + "\"tier\":1,\"fullAccess\":false,\"durable\":true,\"created\":1791580000000,\"issuer\":{\"kind\":\"browser\",\"label\":\"Chrome・Windows\"},"
            + "\"auth\":{\"uvAt\":1791579999995,\"defaultPassword\":true}}");
    }

    [Fact]
    public void 還原後換成新的conn_iat_exp_其他不變_ContentDigest相同()
    {
        foreach (var body in new[] { Body(conv: null, epoch: null, uvAt: null), Body(conv: "0123456789abcdef0123456789abcdef", epoch: 3, uvAt: Created - 5) })
        {
            var json = DurableBodyRules.Serialize(body);
            var resigned = DurableBodyRules.Deserialize(json, "nonce-new", Created + 90_000, Created + 150_000);
            (resigned.Conn, resigned.Iat, resigned.Exp).Should().Be(("nonce-new", Created + 90_000, Created + 150_000));
            resigned.Should().BeEquivalentTo(body with { Conn = "nonce-new", Iat = Created + 90_000, Exp = Created + 150_000 },
                o => o.Excluding(b => b.Cmd), "除了 conn、iat、exp 都照舊");
            JToken.DeepEquals(resigned.Cmd, body.Cmd).Should().BeTrue();
            CommandEnvelope.ContentDigest(resigned).Should().Be(CommandEnvelope.ContentDigest(body), "摘要不含 conn、iat、exp");
            DurableBodyRules.Serialize(resigned).Should().Be(json, "再序列化一次逐字相同");
        }
    }

    [Theory]
    [InlineData("不是 JSON")]
    [InlineData("[1,2]")]
    [InlineData("{\"aud\":\"a\"}")]
    [InlineData("{\"aud\":\"a\",\"aud\":\"b\",\"cid\":\"c\",\"dev\":\"d\",\"cmd\":{\"cmd\":\"x\"},\"tier\":1,\"fullAccess\":false,\"durable\":true,\"created\":1,\"issuer\":{\"kind\":\"browser\",\"label\":\"l\"},\"auth\":{\"defaultPassword\":false}}")]
    [InlineData("{\"aud\":\"a\",\"cid\":\"c\",\"dev\":\"d\",\"conn\":\"n\",\"cmd\":{\"cmd\":\"x\"},\"tier\":1,\"fullAccess\":false,\"durable\":true,\"created\":1,\"issuer\":{\"kind\":\"browser\",\"label\":\"l\"},\"auth\":{\"defaultPassword\":false}}")]
    [InlineData("{\"aud\":\"a\",\"cid\":\"c\",\"dev\":\"d\",\"cmd\":{\"cmd\":1},\"tier\":1,\"fullAccess\":false,\"durable\":true,\"created\":1,\"issuer\":{\"kind\":\"browser\",\"label\":\"l\"},\"auth\":{\"defaultPassword\":false}}")]
    [InlineData("{\"aud\":\"a\",\"cid\":\"c\",\"dev\":\"d\",\"cmd\":{\"cmd\":\"x\"},\"tier\":\"1\",\"fullAccess\":false,\"durable\":true,\"created\":1,\"issuer\":{\"kind\":\"browser\",\"label\":\"l\"},\"auth\":{\"defaultPassword\":false}}")]
    [InlineData("{\"aud\":\"a\",\"cid\":\"c\",\"dev\":\"d\",\"cmd\":{\"cmd\":\"x\"},\"tier\":1,\"fullAccess\":false,\"durable\":true,\"created\":1.5,\"issuer\":{\"kind\":\"browser\",\"label\":\"l\"},\"auth\":{\"defaultPassword\":false}}")]
    [InlineData("{\"aud\":\"a\",\"cid\":\"c\",\"dev\":\"d\",\"cmd\":{\"cmd\":\"x\"},\"tier\":1,\"fullAccess\":false,\"durable\":true,\"created\":1,\"issuer\":{\"kind\":\"browser\",\"label\":\"l\",\"x\":1},\"auth\":{\"defaultPassword\":false}}")]
    [InlineData("{\"aud\":\"a\",\"cid\":\"c\",\"dev\":\"d\",\"cmd\":{\"cmd\":\"x\"},\"tier\":1,\"fullAccess\":false,\"durable\":true,\"created\":1,\"issuer\":{\"kind\":\"browser\",\"label\":\"l\"},\"auth\":{}}")]
    [InlineData("{\"aud\":\"a\",\"cid\":\"c\",\"dev\":\"d\",\"cmd\":{\"cmd\":\"x\"},\"tier\":1,\"fullAccess\":\"false\",\"durable\":true,\"created\":1,\"issuer\":{\"kind\":\"browser\",\"label\":\"l\"},\"auth\":{\"defaultPassword\":false}}")]
    public void 不是Serialize寫出的形狀_FormatException(string json)
    {
        FluentActions.Invoking(() => DurableBodyRules.Deserialize(json, "nonce", Created, Created + 60_000)).Should().Throw<FormatException>();
    }

    [Fact]
    public void 正向對照_最小的合格形狀照收()
    {
        const string json = "{\"aud\":\"a\",\"cid\":\"c\",\"dev\":\"d\",\"cmd\":{\"cmd\":\"x\"},\"tier\":1,\"fullAccess\":false,\"durable\":true,\"created\":1,"
            + "\"issuer\":{\"kind\":\"browser\",\"label\":\"l\"},\"auth\":{\"defaultPassword\":false}}";
        var body = DurableBodyRules.Deserialize(json, "nonce", 2, 3);
        (body.Aud, body.Conv, body.Epoch, body.Auth.UvAt, body.Created).Should().Be(("a", (string?)null, (long?)null, (long?)null, 1L));
    }

    private static CommandEnvelopeBody Body(string? conv, long? epoch, long? uvAt) => new(
        "https://hub.example.com", "01K7Z0000000000000000000AB", "dv_a", "nonce-old", conv, epoch,
        new JObject { ["cmd"] = "accountLogout", ["accountId"] = "acct-a", ["deviceId"] = "dv_a" },
        Tier: 1, FullAccess: false, Durable: true, Created: Created, Iat: Created, Exp: Created + 60_000,
        new CommandIssuer("browser", "Chrome・Windows"), new CommandAuth(uvAt, DefaultPassword: true));
}
