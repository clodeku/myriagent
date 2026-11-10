using System.Net;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync;

/// <summary>分段下載遇到暫時的失敗：間隔漸長、連續失敗超過三分鐘才放棄；502／503／504 是暫時的</summary>
public sealed class PeerDownloadRetryRulesTests
{
    [Fact]
    public void NextDelay_間隔2_2_4_4_8秒_之後每15秒()
    {
        var delays = Enumerable.Range(0, 7).Select(i => PeerDownloadRetryRules.NextDelay(i, TimeSpan.Zero)!.Value.TotalSeconds);
        delays.Should().Equal(2, 2, 4, 4, 8, 15, 15);
    }

    [Fact]
    public void NextDelay_再等就超過三分鐘_放棄()
    {
        PeerDownloadRetryRules.NextDelay(6, TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(15)).Should().Be(TimeSpan.FromSeconds(15));
        PeerDownloadRetryRules.NextDelay(6, TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(14)).Should().BeNull();
        PeerDownloadRetryRules.NextDelay(-1, TimeSpan.Zero).Should().BeNull();
    }

    [Fact]
    public void IsTransientStatus_只有502_503_504()
    {
        PeerDownloadRetryRules.IsTransientStatus(HttpStatusCode.BadGateway).Should().BeTrue();
        PeerDownloadRetryRules.IsTransientStatus(HttpStatusCode.ServiceUnavailable).Should().BeTrue();
        PeerDownloadRetryRules.IsTransientStatus(HttpStatusCode.GatewayTimeout).Should().BeTrue();
        PeerDownloadRetryRules.IsTransientStatus(HttpStatusCode.NotFound).Should().BeFalse();
        PeerDownloadRetryRules.IsTransientStatus(HttpStatusCode.Forbidden).Should().BeFalse();
        PeerDownloadRetryRules.IsTransientStatus(HttpStatusCode.PartialContent).Should().BeFalse();
    }
}
