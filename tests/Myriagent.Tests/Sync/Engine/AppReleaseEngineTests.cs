using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Update;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// app 自我更新 U3 的引擎部分：伺服器身分驗過之後的 Hello latestApp、交握完成之後的 ServerNotice{appRelease} 經 IAppReleasePort 告訴協調者；
/// 名稱不合格的不報；下載用的 HTTP 用戶端在引擎停止後是 null
/// </summary>
public sealed class AppReleaseEngineTests
{
    private const string Release = "0.3.120-0123abc";

    private static FakeReleasePort UsePort(EngineHarness h)
    {
        var port = new FakeReleasePort(h.Host);
        h.HostPort = port;
        return port;
    }

    [Fact]
    public async Task Hello的latestApp告訴協調者()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        h.Server.LatestApp = new AppReleaseInfo(Release, "0.3.120");

        await h.StartAsync();
        await CommandEngineTests.WaitUntilAsync(() => !port.AppReleases.IsEmpty, "回報 latestApp");

        port.AppReleases.Should().Equal(Release);
    }

    [Fact]
    public async Task ServerNotice的appRelease告訴協調者_名稱不合格的不報()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        await h.StartAsync();
        var session = h.Server.Current!;

        session.Events.OnServerNotice(new ServerNotice(ServerNoticeKinds.AppRelease, Release: "bad name"));
        session.Events.OnServerNotice(new ServerNotice(ServerNoticeKinds.AppRelease, Release: Release));
        await h.IdleAsync();

        port.AppReleases.Should().Equal(Release);
    }

    [Fact]
    public async Task 下載用的HTTP用戶端_引擎在時有_停止後是null()
    {
        await using var h = new EngineHarness();
        UsePort(h);
        await h.StartAsync();

        h.Engine.ReleaseHttp.Should().NotBeNull();
        h.Engine.ReleaseHttp.Should().BeSameAs(h.Engine.ReleaseHttp, "同一個引擎共用一個用戶端");

        await h.Engine.StopAsync(TimeSpan.FromSeconds(10));
        h.Engine.ReleaseHttp.Should().BeNull();
    }
}
