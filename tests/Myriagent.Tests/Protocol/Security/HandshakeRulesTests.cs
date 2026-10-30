using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Myriagent.Security;
using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Security;

/// <summary>
/// Hello／Prove 的雙向簽章與伺服器身分釘選（設計 4.2、SEC-09、SEC-10 的規則部分；整合測試在 WP3.2／3.3）：
/// 換了身分金鑰、TLS 看到的 SPKI 不同（中間人）、nonce 或 deviceId 不同、沒有裝置私鑰，簽章一律驗不過。
/// </summary>
public sealed class HandshakeRulesTests
{
    private const long ServerTime = 1_791_580_000_000;

    private readonly string deviceId = SyncIds.NewDeviceId();
    private readonly string nonceD = HandshakeRules.NewNonce();
    private readonly string nonceS = HandshakeRules.NewNonce();
    private readonly string spki = Base64UrlRules.Encode(SHA256.HashData("葉憑證"u8));

    [Fact]
    public void 伺服器簽_裝置以釘選的公鑰驗_任何欄位不同都驗不過()
    {
        using var hub = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pinned = P256KeyRules.ExportSpki(hub);
        var sig = P256KeyRules.Sign(hub, HandshakeRules.HubSignedData(deviceId, nonceD, nonceS, spki, ServerTime));

        HandshakeRules.VerifyHub(pinned, deviceId, nonceD, nonceS, spki, ServerTime, sig).Should().BeTrue();
        HandshakeRules.VerifyHub(pinned, deviceId, nonceD, nonceS, Base64UrlRules.Encode(SHA256.HashData("中間人的憑證"u8)), ServerTime, sig).Should().BeFalse("中間人：裝置看到的 SPKI 不同");
        HandshakeRules.VerifyHub(pinned, deviceId, HandshakeRules.NewNonce(), nonceS, spki, ServerTime, sig).Should().BeFalse("重放舊的 hubSig");
        HandshakeRules.VerifyHub(pinned, SyncIds.NewDeviceId(), nonceD, nonceS, spki, ServerTime, sig).Should().BeFalse();
        HandshakeRules.VerifyHub(pinned, deviceId, nonceD, nonceS, spki, ServerTime + 1, sig).Should().BeFalse();
        var impostorSig = P256KeyRules.Sign(impostor, HandshakeRules.HubSignedData(deviceId, nonceD, nonceS, spki, ServerTime));
        HandshakeRules.VerifyHub(pinned, deviceId, nonceD, nonceS, spki, ServerTime, impostorSig).Should().BeFalse("換了身分金鑰（SEC-10）");
    }

    [Fact]
    public void 裝置簽_伺服器以登記的公鑰驗_沒有私鑰或轉送到別的伺服器都驗不過()
    {
        using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var thief = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var registered = P256KeyRules.ExportSpki(device);
        var sig = P256KeyRules.Sign(device, HandshakeRules.DeviceSignedData(deviceId, nonceS, nonceD, spki));

        HandshakeRules.VerifyDevice(registered, deviceId, nonceS, nonceD, spki, sig).Should().BeTrue();
        HandshakeRules.VerifyDevice(registered, deviceId, nonceS, nonceD, spki, P256KeyRules.Sign(thief, HandshakeRules.DeviceSignedData(deviceId, nonceS, nonceD, spki)))
            .Should().BeFalse("只有權杖沒有私鑰（SEC-09）");
        HandshakeRules.VerifyDevice(registered, deviceId, nonceS, nonceD, Base64UrlRules.Encode(SHA256.HashData("真伺服器"u8)), sig)
            .Should().BeFalse("中間人把裝置的簽章轉送給真伺服器：真伺服器的 SPKI 不同");
        HandshakeRules.VerifyDevice(registered, deviceId, HandshakeRules.NewNonce(), nonceD, spki, sig).Should().BeFalse();

        var der = Base64UrlRules.Encode(device.SignData(HandshakeRules.DeviceSignedData(deviceId, nonceS, nonceD, spki), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        HandshakeRules.VerifyDevice(registered, deviceId, nonceS, nonceD, spki, der).Should().BeFalse("只收 P1363 的 64 位元組");
        HandshakeRules.VerifyDevice(registered, deviceId, nonceS, nonceD, spki, null).Should().BeFalse();
        HandshakeRules.VerifyDevice("不是公鑰", deviceId, nonceS, nonceD, spki, sig).Should().BeFalse();
    }

    [Fact]
    public void 簽的內容_用途前綴與欄位以換行串接()
    {
        System.Text.Encoding.UTF8.GetString(HandshakeRules.HubSignedData("d_A", "n1", "n2", "s", 42))
            .Should().Be("myriagent-hub-hello-v1\nd_A\nn1\nn2\ns\n42");
        System.Text.Encoding.UTF8.GetString(HandshakeRules.DeviceSignedData("d_A", "n2", "n1", "s"))
            .Should().Be("myriagent-device-hello-v1\nd_A\nn2\nn1\ns");
    }

    [Fact]
    public void 身分金鑰輪換_舊金鑰簽的聲明才改釘()
    {
        using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var oldSpki = P256KeyRules.ExportSpki(oldKey);
        var newSpki = P256KeyRules.ExportSpki(newKey);
        var sig = P256KeyRules.Sign(oldKey, HandshakeRules.RotationSignedData(oldSpki, newSpki));

        HandshakeRules.AcceptRotation(oldSpki, oldSpki, newSpki, sig).Should().Be(newSpki);
        HandshakeRules.AcceptRotation(oldSpki, oldSpki, newSpki, P256KeyRules.Sign(newKey, HandshakeRules.RotationSignedData(oldSpki, newSpki)))
            .Should().BeNull("新金鑰替自己簽的不算（伺服器重建時舊金鑰已遺失，要本機確認新指紋）");
        HandshakeRules.AcceptRotation(P256KeyRules.ExportSpki(other), oldSpki, newSpki, sig).Should().BeNull("釘的不是聲明裡的舊金鑰");
        HandshakeRules.AcceptRotation(newSpki, oldSpki, newSpki, sig).Should().BeNull("已經改釘過");
        HandshakeRules.AcceptRotation(oldSpki, oldSpki, "不是公鑰", sig).Should().BeNull();
    }

    [Fact]
    public void HelloResponse的伺服器身分_相同_已輪換_要本機確認_無效()
    {
        using var hub = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rebuilt = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pinned = P256KeyRules.ExportSpki(hub);
        var nextSpki = P256KeyRules.ExportSpki(next);
        var rotation = new HubIdentityRotation(pinned, nextSpki, P256KeyRules.Sign(hub, HandshakeRules.RotationSignedData(pinned, nextSpki)));

        Check(Response(hub)).Should().Be((HubIdentityOutcome.Ok, pinned));
        Check(Response(hub, rotation: rotation)).Should().Be((HubIdentityOutcome.Ok, pinned), "聲明一律附最近一次的，宣稱的公鑰沒換就照舊");
        Check(Response(next, rotation: rotation)).Should().Be((HubIdentityOutcome.Rotated, nextSpki));
        Check(Response(next)).Should().Be((HubIdentityOutcome.NeedsLocalConfirmation, nextSpki), "沒有輪換聲明：要本機確認新指紋");
        Check(Response(rebuilt, rotation: rotation)).Should().Be((HubIdentityOutcome.NeedsLocalConfirmation, P256KeyRules.ExportSpki(rebuilt)), "聲明輪換到的不是它");

        Check(Response(hub, observed: Base64UrlRules.Encode(SHA256.HashData("中間人的憑證"u8)))).Should().Be((HubIdentityOutcome.Invalid, null), "伺服器簽的 SPKI 不是裝置看到的");
        Check(Response(next, rotation: rotation, observed: Base64UrlRules.Encode(SHA256.HashData("中間人的憑證"u8)))).Should().Be((HubIdentityOutcome.Invalid, null));
        Check(Response(next) with { HubIdentitySpki = pinned }).Should().Be((HubIdentityOutcome.Invalid, null), "宣稱是釘的那把、簽章卻是別把");
        Check(Response(hub) with { HubIdentitySpki = "不是公鑰" }).Should().Be((HubIdentityOutcome.Invalid, null));
        var badNonce = Response(hub) with { NonceS = "AAAA", HubSig = P256KeyRules.Sign(hub, HandshakeRules.HubSignedData(deviceId, nonceD, "AAAA", spki, ServerTime)) };
        Check(badNonce).Should().Be((HubIdentityOutcome.Invalid, null), "伺服器的 nonce 不是 32 位元組亂數：裝置接著要簽它（Prove），不收");
        HandshakeRules.AcceptRotation(pinned, pinned, nextSpki, null).Should().BeNull();

        (HubIdentityOutcome, string?) Check(HelloResponse response) => HandshakeRules.CheckHubIdentity(pinned, deviceId, nonceD, spki, response);
    }

    private HelloResponse Response(ECDsa signer, HubIdentityRotation? rotation = null, string? observed = null) =>
        new(
            ServerTime,
            nonceS,
            new ServerInstance("i", 0),
            P256KeyRules.Sign(signer, HandshakeRules.HubSignedData(deviceId, nonceD, nonceS, observed ?? spki, ServerTime)),
            P256KeyRules.ExportSpki(signer),
            new TrustRevs(1),
            SyncLimits.Default,
            "0.3.0",
            rotation);

    [Fact]
    public void nonce與SPKI雜湊的格式_時鐘差超過2分鐘才警告()
    {
        HandshakeRules.IsNonce(HandshakeRules.NewNonce()).Should().BeTrue();
        HandshakeRules.IsNonce(Base64UrlRules.Encode(new byte[16])).Should().BeFalse();
        HandshakeRules.IsNonce("AAAA=").Should().BeFalse();

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=hub.example.com", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var hash = HandshakeRules.SpkiSha256(certificate);
        hash.Should().Be(Base64UrlRules.Encode(SHA256.HashData(key.ExportSubjectPublicKeyInfo())), "憑證輪替但金鑰不換時 SPKI 雜湊不變");
        HandshakeRules.IsSpkiSha256(hash).Should().BeTrue();

        HandshakeRules.IsClockSkewed(ServerTime, ServerTime - 120_000).Should().BeFalse();
        HandshakeRules.IsClockSkewed(ServerTime, ServerTime + 120_001).Should().BeTrue();
        HandshakeRules.IsClockSkewed(long.MaxValue, long.MinValue).Should().BeTrue("極端值相減不溢位");
        HandshakeRules.IsClockSkewed(long.MinValue, long.MaxValue).Should().BeTrue();
        HandshakeRules.IsClockSkewed(long.MinValue, long.MinValue).Should().BeFalse();
    }
}
