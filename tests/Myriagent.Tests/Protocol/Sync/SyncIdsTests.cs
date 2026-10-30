using Myriagent.Security;
using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>識別碼、指紋與配對碼的格式（設計 3.5、6.3、8.2.4）</summary>
public sealed class SyncIdsTests
{
    [Fact]
    public void CrockfordBase32_位元流由高到低_不足5位元補0_沒有易混淆字母()
    {
        CrockfordBase32.Encode([]).Should().BeEmpty();
        CrockfordBase32.Encode([0x00]).Should().Be("00");
        CrockfordBase32.Encode([0xFF]).Should().Be("ZW");
        // 00000 00100 00001 00000 00110 00001 00000 00101
        CrockfordBase32.Encode([0x01, 0x02, 0x03, 0x04, 0x05]).Should().Be("04106105");
        CrockfordBase32.Alphabet.Should().NotContainAny("I", "L", "O", "U");
    }

    [Fact]
    public void deviceId與邏輯帳戶id_前綴加26字()
    {
        var device = SyncIds.NewDeviceId();
        device.Should().MatchRegex("^d_[0-9A-HJKMNP-TV-Z]{26}$");
        SyncIds.IsDeviceId(device).Should().BeTrue();
        SyncIds.IsLogicalAccountId(device).Should().BeFalse();
        SyncIds.IsDeviceId(SyncIds.NewDeviceId()).Should().BeTrue();
        SyncIds.NewDeviceId().Should().NotBe(device);

        var account = SyncIds.NewLogicalAccountId();
        account.Should().MatchRegex("^la_[0-9A-HJKMNP-TV-Z]{26}$");
        SyncIds.IsLogicalAccountId(account).Should().BeTrue();

        SyncIds.IsDeviceId("d_" + new string('I', 26)).Should().BeFalse("I 不在字母表");
        SyncIds.IsDeviceId("d_" + new string('0', 25)).Should().BeFalse();
        SyncIds.IsDeviceId("D_" + new string('0', 26)).Should().BeFalse();
        SyncIds.IsDeviceId(null).Should().BeFalse();
    }

    [Fact]
    public void ULID_時間部分與規格範例一致_依時間排序()
    {
        // ULID 規格的範例：01ARYZ6S41TSV4RRFFQ69G5FAV 的時間部分 01ARYZ6S41＝1469918176385
        SyncIds.NewUlid(1469918176385)[..10].Should().Be("01ARYZ6S41");
        var earlier = SyncIds.NewUlid(1_791_580_000_000);
        var later = SyncIds.NewUlid(1_791_580_000_001);
        string.CompareOrdinal(earlier, later).Should().BeNegative();
        SyncIds.IsUlid(earlier).Should().BeTrue();
        SyncIds.IsUlid("8" + earlier[1..]).Should().BeFalse("第一個字 ≤ 7");
        SyncIds.IsUlid(earlier[..25]).Should().BeFalse();
    }

    [Fact]
    public void 指紋_SHA256的Base32前8字_XXXX_XXXX()
    {
        var fingerprint = FingerprintRules.Of("公鑰"u8);
        fingerprint.Should().MatchRegex("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$");
        FingerprintRules.Of("公鑰"u8).Should().Be(fingerprint, "同樣的位元組同樣的指紋");
        FingerprintRules.Of("公鑰 "u8).Should().NotBe(fingerprint);
    }

    [Fact]
    public void 配對碼_8個無母音字母_輸入時容忍小寫空白連字號()
    {
        var code = PairingRules.NewUserCode();
        code.Should().MatchRegex("^[BCDFGHJKLMNPQRSTVWXZ]{4}-[BCDFGHJKLMNPQRSTVWXZ]{4}$");
        PairingRules.NormalizeUserCode(code).Should().Be(code);
        PairingRules.NormalizeUserCode("wdjb mjht").Should().Be("WDJB-MJHT");
        PairingRules.NormalizeUserCode("WDJBMJHT").Should().Be("WDJB-MJHT");
        PairingRules.NormalizeUserCode("WDJB-MJHA").Should().BeNull("A 是母音");
        PairingRules.NormalizeUserCode("WDJB-MJH").Should().BeNull();
        PairingRules.NormalizeUserCode("WDJB-MJHTX").Should().BeNull();
        PairingRules.NormalizeUserCode(null).Should().BeNull();
        PairingRules.NormalizeUserCode(new string('B', 40)).Should().BeNull();
    }

    [Fact]
    public void 裝置碼_256位元_輪詢間隔_slow_down加5秒_太快的判斷()
    {
        var deviceCode = PairingRules.NewDeviceCode();
        PairingRules.IsDeviceCode(deviceCode).Should().BeTrue();
        deviceCode.Should().HaveLength(43);
        PairingRules.IsDeviceCode(deviceCode[..42]).Should().BeFalse();
        PairingRules.IsDeviceCode(Base64UrlRules.Encode(new byte[16])).Should().BeFalse();

        PairingRules.NextInterval(2, new PairError(PairingErrors.SlowDown)).Should().Be(7);
        PairingRules.NextInterval(2, new PairError(PairingErrors.SlowDown, Interval: 10)).Should().Be(10);
        PairingRules.NextInterval(2, new PairError(PairingErrors.AuthorizationPending)).Should().Be(2);

        PairingRules.IsTooFast(null, 1_000, 2).Should().BeFalse();
        PairingRules.IsTooFast(0, 1_999, 2).Should().BeTrue();
        PairingRules.IsTooFast(0, 2_000, 2).Should().BeFalse();
    }

    [Fact]
    public void 裝置名稱_去頭尾空白_1到60字_去掉控制字元()
    {
        PairingRules.CleanDeviceName("  OFFICE-PC ").Should().Be("OFFICE-PC");
        PairingRules.CleanDeviceName("辦公室電腦").Should().Be("辦公室電腦");
        PairingRules.CleanDeviceName("   ").Should().BeNull();
        PairingRules.CleanDeviceName(new string('x', 61)).Should().BeNull();
        PairingRules.CleanDeviceName("A\nB").Should().Be("AB");
        PairingRules.CleanDeviceName("\n\t").Should().BeNull("去掉之後是空的");
        PairingRules.IsKeyStorage("tpm").Should().BeTrue();
        PairingRules.IsKeyStorage("TPM").Should().BeFalse();
    }
}
