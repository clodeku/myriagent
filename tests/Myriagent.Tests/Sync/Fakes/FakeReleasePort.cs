using System.Collections.Concurrent;
using Myriagent.Sync;
using Myriagent.Usage;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>
/// 測試用的宿主（app 自我更新 U3）：包一個 <see cref="FakeHostPort"/>，另外實作 <see cref="IAppReleasePort"/>（正式的協調者也實作它），
/// 引擎因此在 Hello 宣告 update1。<see cref="FakeHostPort"/> 與 <see cref="FakeCommandPort"/> 本身不實作，既有測試的 Hello 不變
/// </summary>
internal sealed class FakeReleasePort(FakeHostPort inner) : ISyncHostPort, IAppReleasePort
{
    public FakeHostPort Inner { get; } = inner;

    /// <summary>引擎回報的「伺服器說有新版」（依序）</summary>
    public ConcurrentQueue<string> AppReleases { get; } = new();

    public void ReportAppRelease(string release) => AppReleases.Enqueue(release);

    public Task RunOnUiAsync(Action action, CancellationToken ct) => Inner.RunOnUiAsync(action, ct);

    public void PublishStatus(SyncStatusSnapshot status) => Inner.PublishStatus(status);

    public void PublishPollSnapshot(UsagePollSnapshot snapshot) => Inner.PublishPollSnapshot(snapshot);

    public void PingUi() => Inner.PingUi();

    public long UiAliveAt => Inner.UiAliveAt;
}
