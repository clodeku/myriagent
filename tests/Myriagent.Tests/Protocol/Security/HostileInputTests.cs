using System.Security.Cryptography;
using Myriagent.Security;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Security;

/// <summary>
/// 攻擊者控制的輸入（被攻破的伺服器送來的名冊、偏好）：一律回失敗，絕不丟出例外讓同步流程崩潰。
/// 審查時實測的坑：Newtonsoft 把超過 long 的整數讀成 BigInteger（型別仍是 Integer）。
/// </summary>
public sealed class HostileInputTests
{
    private const long T0 = 1_791_580_000_000;

    [Fact]
    public void 名冊_孤立代理字元_控制或雙向覆寫字元的名稱_不是字串的欄位_檢查回不合格不丟例外()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = P256KeyRules.ExportSpki(key);
        var id = SyncIds.NewDeviceId();
        // JSON 原文裡的跳脫（解碼後才是雙向覆寫、換行、零寬字元）與原文直接放的孤立代理字元
        var bs = "\\";
        string Json(string name) => "{\"registryRev\":1,\"registry\":[{\"deviceId\":\"" + id + "\",\"name\":\"" + name + "\",\"devicePubKey\":\"" + spki + "\",\"createdAt\":" + T0 + "}]}";
        var escaped = new[] { "OFFICE" + bs + "u202EPC", "OFFICE" + bs + "nPC", "OFFICE" + bs + "u200BPC" };
        foreach (var name in escaped.Append("PC" + (char)0xD800))
        {
            var bundle = ((Func<TrustBundle?>)(() => SyncJson.Deserialize<TrustBundle>(Json(name)))).Should().NotThrow().Which;
            ((Func<bool>)(() => DeviceRegistryRules.IsValid(bundle!.Registry))).Should().NotThrow().Which.Should().BeFalse(name);
        }

        // 跳脫的孤立代理字元：Newtonsoft 解碼時換成 U+FFFD，名稱裡不會留下孤立代理字元（換成的字元可以顯示，名稱合格）
        foreach (var name in new[] { bs + "ud800", "PC" + bs + "udc00" })
        {
            var bundle = ((Func<TrustBundle?>)(() => SyncJson.Deserialize<TrustBundle>(Json(name)))).Should().NotThrow().Which;
            bundle!.Registry!.Single().Name.Any(char.IsSurrogate).Should().BeFalse(name);
            ((Func<bool>)(() => DeviceRegistryRules.IsValid(bundle.Registry))).Should().NotThrow();
        }

        var shaped = "{\"registryRev\":1,\"registry\":[{\"deviceId\":\"" + id + "\",\"name\":\"PC\",\"devicePubKey\":\"not-base64url!\",\"createdAt\":" + T0 + "}]}";
        DeviceRegistryRules.IsValid(SyncJson.Deserialize<TrustBundle>(shaped)!.Registry).Should().BeFalse();
    }

    [Fact]
    public void 偏好_超過long的整數_回不合格而不是丟例外()
    {
        var huge = JToken.Parse("99999999999999999999999");
        huge.Type.Should().Be(JTokenType.Integer, "Newtonsoft 讀成 BigInteger、型別仍是 Integer");
        ((Func<bool>)(() => PrefsRules.IsValid("fontSize", huge))).Should().NotThrow().Which.Should().BeFalse();

        var payload = SyncJson.Deserialize<PrefsUpdatePayload>("{\"key\":\"fontSize\",\"value\":99999999999999999999999,\"baseVersion\":1}")!;
        ((Func<bool>)(() => PrefsRules.IsValid(payload.Key, payload.Value))).Should().NotThrow().Which.Should().BeFalse();
        PrefsRules.IsValid("fontSize", JToken.Parse("14")).Should().BeTrue();
    }

    [Fact]
    public void 顯示名稱_中文與emoji可以_控制字元_格式字元_孤立代理去掉_清理後空的或太長不行()
    {
        LabelRules.Clean("  辦公室電腦 💻 ", 60).Should().Be("辦公室電腦 💻");
        LabelRules.IsClean("辦公室電腦 💻", 60).Should().BeTrue();
        LabelRules.IsClean(" 辦公室電腦", 60).Should().BeFalse("名冊裡的名稱要已經清理過");

        // 去掉而不是整個拒絕：含零寬連接字元的組合 emoji（男人＋ZWJ＋筆電）清理後還是個名稱
        var cases = new[]
        {
            ("PC\u202Eexe", "PCexe"),
            ("PC\u200B", "PC"),
            ("PC\u0007", "PC"),
            ("P\u2028C", "PC"),
            ("PC\ud800", "PC"),
            ("\u200B 辦公室 ", "辦公室"),
            ("\uD83D\uDC68\u200D\uD83D\uDCBB 工作機", "\uD83D\uDC68\uD83D\uDCBB 工作機"),
            (new string('x', 60) + "\u200B", new string('x', 60)),
        };
        foreach (var (raw, clean) in cases)
        {
            LabelRules.Clean(raw, 60).Should().Be(clean, raw);
            LabelRules.IsClean(raw, 60).Should().BeFalse("名冊裡出現要去掉的字元＝不是伺服器清理過的名稱，整份不採用");
        }

        foreach (var bad in new[] { null, "", "   ", "\u200B\u202E \u0007", "\ud800", new string('x', 61) })
            LabelRules.Clean(bad, 60).Should().BeNull(bad ?? "null");
        PairingRules.CleanDeviceName("OFFICE\u202EPC").Should().Be("OFFICEPC");
    }

    [Fact]
    public void 指紋_裝置8字_信任根16字_信任根是裝置指紋的延伸()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = P256KeyRules.ExportSpki(key);
        P256KeyRules.Fingerprint(spki).Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$");
        P256KeyRules.AnchorFingerprint(spki).Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){3}$")
            .And.StartWith(P256KeyRules.Fingerprint(spki))
            .And.Be(FingerprintRules.Anchor(key.ExportSubjectPublicKeyInfo()));
    }
}
