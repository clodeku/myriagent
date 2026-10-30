using System.Security.Cryptography;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Pairing;
using MyriagentHub.Realtime;

namespace MyriagentHub.Tests.Pairing;

/// <summary>配對的伺服器端規則：輪詢的判斷、開始配對的協定檢查（與 Hello 共用同一個協定範圍）</summary>
public sealed class PairingServerRulesTests
{
    private const long Now = 10_000_000;

    [Fact]
    public void 輪詢的判斷_拒絕先於到期_核准後寬限5分鐘_太快回slow_down()
    {
        PairingServerRules.Poll(null, Now).Should().Be(PollVerdict.InvalidGrant);
        PairingServerRules.Poll(Record() with { ConsumedAt = Now }, Now).Should().Be(PollVerdict.InvalidGrant);
        PairingServerRules.Poll(Record() with { DeniedAt = Now, ExpiresAt = Now - 1 }, Now).Should().Be(PollVerdict.AccessDenied);
        PairingServerRules.Poll(Record() with { ExpiresAt = Now }, Now).Should().Be(PollVerdict.Expired);
        PairingServerRules.Poll(Record() with { ApprovedAt = Now - 1, ExpiresAt = Now - 1 }, Now).Should().Be(PollVerdict.Approved);
        PairingServerRules.Poll(Record() with { ApprovedAt = Now - 1, ExpiresAt = Now - PairingServerRules.ConsumeGraceMs - 1 }, Now).Should().Be(PollVerdict.Expired);
        PairingServerRules.Poll(Record() with { LastPollAt = Now - 1999 }, Now).Should().Be(PollVerdict.SlowDown);
        PairingServerRules.Poll(Record() with { LastPollAt = Now - 2000 }, Now).Should().Be(PollVerdict.Pending);
        PairingServerRules.Poll(Record() with { ApprovedAt = Now, LastPollAt = Now }, Now).Should().Be(PollVerdict.Approved, "核准後不必等間隔");
    }

    [Fact]
    public void 開始配對的協定檢查_支援範圍內才通過_太舊的app照舊回protocol()
    {
        PairingServerRules.CheckStart(Start(), "0.1.0").Should().BeNull();
        PairingServerRules.CheckStart(Start(protocolVersion: SyncProtocol.Version + 1), "0.1.0").Should().Be(SyncErrorCodes.Protocol, "比伺服器新（伺服器回滾）");
        PairingServerRules.CheckStart(Start(protocolVersion: SyncProtocol.MinSupportedVersion - 1), "0.1.0").Should().Be(SyncErrorCodes.Protocol, "比支援的最低版本舊");
        PairingServerRules.CheckStart(Start(appVersion: "0.1.5"), "0.2.0").Should().Be(SyncErrorCodes.Protocol, "配對不開放只更新：太舊的 app 先更新再配對（D10）");
        PairingServerRules.CheckStart(null, "0.1.0").Should().Be(PairingErrors.InvalidRequest);
    }

    [Fact]
    public void 配對與Hello用同一個協定範圍()
    {
        for (var protocol = -1; protocol <= SyncProtocol.Version + 2; protocol++)
        {
            var supported = SyncProtocol.IsSupported(protocol);
            (PairingServerRules.CheckStart(Start(protocolVersion: protocol), "0.1.0") is null).Should().Be(supported, "配對，protocolVersion {0}", protocol);
            (DeviceHubRules.Gate(protocol, "0.2.0", null, "0.1.0") == HelloGate.Full).Should().Be(supported, "Hello，protocolVersion {0}", protocol);
        }
    }

    private static PairStartRequest Start(int protocolVersion = SyncProtocol.Version, string appVersion = "0.2.0")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new PairStartRequest("辦公室電腦", "OFFICE-PC", "User", P256KeyRules.ExportSpki(key), PairingRules.KeyStorageSoftware, appVersion, protocolVersion);
    }

    private static PairingRequestRecordView Record() => new();

    /// <summary>以 record 的 with 寫法組測試資料，轉成實體再判斷</summary>
    private sealed record PairingRequestRecordView(long ExpiresAt = Now + 600_000, long? ApprovedAt = null, long? DeniedAt = null, long? ConsumedAt = null, long? LastPollAt = null, int IntervalSeconds = 2)
    {
        public static implicit operator PairingRequestRecord(PairingRequestRecordView v) => new()
        {
            ExpiresAt = v.ExpiresAt,
            ApprovedAt = v.ApprovedAt,
            DeniedAt = v.DeniedAt,
            ConsumedAt = v.ConsumedAt,
            LastPollAt = v.LastPollAt,
            IntervalSeconds = v.IntervalSeconds,
        };
    }
}
