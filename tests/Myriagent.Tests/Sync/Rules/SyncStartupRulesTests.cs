using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

public sealed class SyncStartupRulesTests
{
    [Fact]
    public void 延後2點5秒_備援10秒()
    {
        SyncStartupRules.StartDelay.Should().Be(TimeSpan.FromMilliseconds(2500));
        SyncStartupRules.FallbackDelay.Should().Be(TimeSpan.FromSeconds(10));
        SyncStartupRules.FallbackDelay.Should().BeGreaterThan(SyncStartupRules.StartDelay, "備援只在一直沒有 ViewLoaded 時才派上用場");
    }

    [Fact]
    public void 結束時等3秒_保險比它長()
    {
        SyncStartupRules.ExitStopTimeout.Should().Be(TimeSpan.FromSeconds(3));
        SyncStartupRules.ExitStopGuard.Should().BeGreaterThan(SyncStartupRules.ExitStopTimeout);
    }
}
