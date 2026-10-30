using Myriagent.Security;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Pairing;

public sealed class PairingFlowRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expiry = Now.AddMinutes(10);

    private static PairPollResult Error(string code, int? interval = null) => new PairPollResult.Error(new PairError(code, interval));

    [Fact]
    public void slow_down_間隔加5或取伺服器的較大值()
    {
        PairingFlowRules.Next(Error(PairingErrors.SlowDown), 2, Now, Expiry).Should().Be(((PairStep)new PairStep.Wait(TimeSpan.FromSeconds(7)), 7));
        PairingFlowRules.Next(Error(PairingErrors.SlowDown, 12), 2, Now, Expiry).Should().Be(((PairStep)new PairStep.Wait(TimeSpan.FromSeconds(12)), 12));
        PairingFlowRules.Next(Error(PairingErrors.SlowDown, 3), 7, Now, Expiry).Should().Be(((PairStep)new PairStep.Wait(TimeSpan.FromSeconds(12)), 12));
        PairingFlowRules.Next(Error(PairingErrors.AuthorizationPending), 7, Now, Expiry).Should().Be(((PairStep)new PairStep.Wait(TimeSpan.FromSeconds(7)), 7), "之後沿用新的間隔");
    }

    [Fact]
    public void 本機到期後_寬限5分鐘內繼續輪詢()
    {
        var after = Expiry.AddMinutes(4).AddSeconds(59);

        PairingFlowRules.Next(Error(PairingErrors.AuthorizationPending), 2, after, Expiry).Step.Should().Be(new PairStep.Wait(TimeSpan.FromSeconds(2)));
        PairingFlowRules.Next(new PairPollResult.NetworkError("x"), 2, after, Expiry).Step.Should().Be(new PairStep.Wait(TimeSpan.FromSeconds(2)));
        PairingFlowRules.Next(Error(PairingErrors.AuthorizationPending), 2, Expiry.AddSeconds(1), Expiry).Step.Should().BeOfType<PairStep.Wait>("本機到期不算，伺服器說了才算");
    }

    [Fact]
    public void 寬限過後_Expired()
    {
        var past = Expiry + PairingFlowRules.ApprovedGrace;

        PairingFlowRules.Next(Error(PairingErrors.AuthorizationPending), 2, past, Expiry).Step.Should().BeOfType<PairStep.Expired>();
        PairingFlowRules.Next(new PairPollResult.NetworkError("x"), 2, past, Expiry).Step.Should().BeOfType<PairStep.Expired>();
        PairingFlowRules.Next(new PairPollResult.RateLimited(null), 2, past, Expiry).Step.Should().BeOfType<PairStep.Expired>();
        PairingFlowRules.Next(Error(PairingErrors.ExpiredToken), 2, Now, Expiry).Step.Should().BeOfType<PairStep.Expired>("伺服器說到期就結束");
    }

    [Fact]
    public void 回429_照RetryAfter等()
    {
        PairingFlowRules.Next(new PairPollResult.RateLimited(TimeSpan.FromSeconds(45)), 2, Now, Expiry).Should().Be(((PairStep)new PairStep.Wait(TimeSpan.FromSeconds(45)), 2));
        PairingFlowRules.Next(new PairPollResult.RateLimited(null), 2, Now, Expiry).Step.Should().Be(new PairStep.Wait(TimeSpan.FromSeconds(2)), "沒帶 Retry-After 照間隔");
        PairingFlowRules.Next(new PairPollResult.RateLimited(TimeSpan.FromMilliseconds(100)), 5, Now, Expiry).Step.Should().Be(new PairStep.Wait(TimeSpan.FromSeconds(5)), "不短於間隔");
    }

    [Fact]
    public void 終止的錯誤_拒絕與其他代碼()
    {
        PairingFlowRules.Next(Error(PairingErrors.AccessDenied), 2, Now, Expiry).Step.Should().BeOfType<PairStep.Denied>();
        foreach (var code in new[] { PairingErrors.InvalidGrant, PairingErrors.InvalidRequest, SyncErrorCodes.Protocol, PairingErrors.TooManyPending, "future_code" })
            PairingFlowRules.Next(Error(code), 2, Now, Expiry).Step.Should().Be(new PairStep.Fatal(code));
        var approval = PairingTestData.Approval(SyncIds.NewDeviceId(), PairingTestData.NewSpki());
        PairingFlowRules.Next(new PairPollResult.Approved(approval), 2, Expiry.AddHours(1), Expiry).Step.Should().Be(new PairStep.Approved(approval), "核准了就算本機早過期也收");
    }

    [Fact]
    public void ErrorText_每個代碼都有中文說明()
    {
        foreach (var code in new[]
        {
            PairingErrors.ExpiredToken, PairingErrors.AccessDenied, PairingErrors.InvalidGrant, PairingErrors.TooManyPending, PairingErrors.InvalidRequest,
            SyncErrorCodes.Protocol, PairingCodes.RateLimited, PairingCodes.BadResponse, PairingCodes.NetworkError, PairingCodes.InvalidDeviceName,
            PairingCodes.KeyFailed, PairingCodes.InvalidApproval, PairingCodes.CommitFailed,
        })
        {
            PairingFlowRules.ErrorText(code).Should().NotContain(code, $"{code} 要有自己的說明");
        }

        PairingFlowRules.ErrorText(PairingCodes.CommitFailed).Should().Contain("撤銷");
        PairingFlowRules.ErrorText("xyz").Should().Contain("xyz");
    }

    [Fact]
    public void ValidateApproval_合格_null()
    {
        var spki = PairingTestData.NewSpki();
        var id = SyncIds.NewDeviceId();

        PairingFlowRules.ValidateApproval(PairingTestData.Approval(id, spki), spki).Should().BeNull();
    }

    [Fact]
    public void ValidateApproval_名冊沒有自己_失敗()
    {
        var spki = PairingTestData.NewSpki();
        var id = SyncIds.NewDeviceId();
        var someoneElse = new DeviceRegistryEntry(SyncIds.NewDeviceId(), "HOME-PC", PairingTestData.NewSpki(), 1791500000000);

        PairingFlowRules.ValidateApproval(PairingTestData.Approval(id, spki, registry: [someoneElse]), spki).Should().NotBeNull();
        PairingFlowRules.ValidateApproval(PairingTestData.Approval(id, spki, registry: [new DeviceRegistryEntry(id, "OFFICE-PC", PairingTestData.NewSpki(), 1)]), spki)
            .Should().NotBeNull("自己那一筆的公鑰不是送出的那把");
        PairingFlowRules.ValidateApproval(PairingTestData.Approval(id, spki) with { Trust = new TrustBundle(1, null) }, spki).Should().NotBeNull("沒有附名冊");
        PairingFlowRules.ValidateApproval(PairingTestData.Approval(id, spki, registry: [new DeviceRegistryEntry(id, "OFFICE-PC", spki, 1, 2, DeviceRegistryRules.RevokedRetired)]), spki)
            .Should().NotBeNull("自己已被撤銷");
    }

    [Fact]
    public void ValidateApproval_hubSpki不合格_失敗()
    {
        var spki = PairingTestData.NewSpki();
        var id = SyncIds.NewDeviceId();

        PairingFlowRules.ValidateApproval(PairingTestData.Approval(id, spki, hubSpki: "AAAA"), spki).Should().NotBeNull();
        PairingFlowRules.ValidateApproval(PairingTestData.Approval(id, spki) with { Server = new ServerInstance("", 0) }, spki).Should().NotBeNull();
        PairingFlowRules.ValidateApproval(PairingTestData.Approval(id, spki) with { DeviceId = "d_short" }, spki).Should().NotBeNull();
    }

    [Fact]
    public void ValidateApproval_權杖前綴不對_失敗()
    {
        var spki = PairingTestData.NewSpki();
        var id = SyncIds.NewDeviceId();

        PairingFlowRules.ValidateApproval(PairingTestData.Approval(id, spki, token: "cha_" + new string('x', 40)), spki).Should().NotBeNull();
        PairingFlowRules.ValidateApproval(PairingTestData.Approval(id, spki, token: "chd_"), spki).Should().NotBeNull();
        var problem = PairingFlowRules.ValidateApproval(PairingTestData.Approval(id, spki, token: "bad_secret_value"), spki);
        problem.Should().NotBeNull().And.NotContain("bad_secret_value", "原因不含權杖");
    }
}
