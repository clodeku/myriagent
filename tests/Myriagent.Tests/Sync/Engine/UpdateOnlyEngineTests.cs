using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// app 自我更新 U3 第二步的引擎部分（sync-protocol 1.4、第 11 節）：宣告 update1、只更新的連線（只有心跳與存取權杖，不送、不拉、不拉名冊，
/// 換到一般連線後補送）、protocol 每小時重試、draining 照停機通知的規則延後、停機通知的抖動、app.state 的發布
/// </summary>
public sealed class UpdateOnlyEngineTests
{
    private static readonly AppStatePayload App = new("0.3.120", "0.3.120-0123abc", AppStatePhases.Idle, null, null, 1_700_000_000_000);

    private static FakeReleasePort UseReleasePort(EngineHarness h)
    {
        var port = new FakeReleasePort(h.Host);
        h.HostPort = port;
        h.Server.Features = [DeviceFeatureRules.Update1];
        return port;
    }

    private static List<JObject> Accepted(EngineHarness h, string kind) =>
        h.Server.Accepted.Where(e => e.Kind == kind).Select(e =>
        {
            PayloadRules.TryDecode(e.Enc, e.Payload, 1 << 20, out var json).Should().BeTrue();
            return JObject.Parse(json);
        }).ToList();

    [Fact]
    public async Task 有發佈port的宿主才宣告update1_協商出update1時保存存取權杖()
    {
        await using (var plain = new EngineHarness())
        {
            await plain.StartAsync();
            plain.Server.Hellos.Single().Features.Should().BeNull("沒有 IAppReleasePort 的宿主照舊不宣告");
        }

        await using var h = new EngineHarness();
        UseReleasePort(h);
        await h.StartAsync();

        h.Server.Hellos.Single().Features.Should().Equal(DeviceFeatureRules.Update1);
        (await h.Inspect(v => v.Features)).Should().Equal(DeviceFeatureRules.Update1);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live, "版本夠新時宣告了 update1 也是一般的連線");
        h.Engine.AccessTokenForTest.Should().Be("cha_test", "下載新版用的權杖：沒有 media1 也保存");
    }

    [Fact]
    public async Task 只更新的連線_只有心跳_不送不拉不拉名冊_換成一般連線後補送()
    {
        await using var h = new EngineHarness();
        UseReleasePort(h);
        h.Server.UpdateOnly = true;
        await h.StartAsync();
        var session = h.Server.Current!;

        (await h.StateAsync()).Should().Be(SyncEngineState.UpdateOnly);
        h.Host.LastStatus!.Detail.Should().Be(SyncStatusRules.UpdateOnlyText);
        h.Engine.AccessTokenForTest.Should().Be("cha_test", "只更新的連線也能下載新版");

        h.Engine.Post(new SyncSignal.App(App));
        session.Events.OnTrustChanged(new TrustChanged(5));
        session.Notify(SyncClasses.Catalog);
        await h.IdleAsync();
        await h.AdvanceAsync(40);

        h.Server.Heartbeats.Should().NotBeEmpty();
        h.Server.Batches.Should().BeEmpty("只更新的連線不送批次");
        h.Server.Pulls.Should().BeEmpty("也不拉取");
        h.Server.TrustRequests.Should().BeEmpty("也不拉名冊");
        h.Server.UpdateOnlyViolations.Should().BeEmpty("只呼叫心跳與換權杖");
        (await h.StateAsync()).Should().Be(SyncEngineState.UpdateOnly);
        (await h.Inspect(v => v.Outbox)).Should().Contain(e => e.Kind == SyncEventKinds.AppState, "outbox 照常保留");

        // 換版之後（這裡以伺服器改判模擬）：一般的連線補送
        h.Server.UpdateOnly = false;
        session.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(2);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        Accepted(h, SyncEventKinds.AppState).Should().ContainSingle().Which.Value<string>("release").Should().Be("0.3.120-0123abc");
    }

    [Fact]
    public async Task protocol_不再停止_一小時後重試_伺服器升回來就連上()
    {
        await using var h = new EngineHarness();
        UseReleasePort(h);
        h.Server.HelloError = n => n == 1 ? SyncErrorCodes.Protocol : null;
        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff, "U3 起 protocol 不停止");
        h.Engine.BackoffHistory.Should().Equal(SyncBackoffRules.ProtocolRetry);
        h.Host.LastStatus!.Detail.Should().Contain("伺服器的版本比這個 app 舊").And.Contain("60 分鐘後重試");

        await h.AdvanceAsync(TimeSpan.FromMinutes(59));
        h.Server.ConnectAttempts.Should().Be(1);
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task draining_照停機通知的規則延後30秒_不是錯誤()
    {
        await using var h = new EngineHarness();
        UseReleasePort(h);
        h.Server.HelloError = n => n <= 2 ? SyncErrorCodes.Draining : null;
        await h.StartAsync();

        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);
        h.Host.LastStatus!.Detail.Should().StartWith("伺服器正在更新");
        h.Engine.BackoffHistory.Should().Equal(TimeSpan.FromSeconds(30));

        await h.AdvanceAsync(30);
        h.Engine.BackoffHistory.Should().Equal(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        await h.AdvanceAsync(30);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task app_state_內容沒變不送_伺服器換實例後重送()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();

        h.Engine.Post(new SyncSignal.App(App));
        await h.IdleAsync();
        await h.AdvanceAsync(1);
        h.Engine.Post(new SyncSignal.App(App));
        await h.IdleAsync();
        await h.AdvanceAsync(1);

        var sent = Accepted(h, SyncEventKinds.AppState).Should().ContainSingle("內容相同不再送").Subject;
        sent.Value<string>("version").Should().Be("0.3.120");
        sent.Value<string>("phase").Should().Be(AppStatePhases.Idle);

        h.Engine.Post(new SyncSignal.App(App with { Phase = AppStatePhases.Downloading }));
        await h.IdleAsync();
        await h.AdvanceAsync(1);
        Accepted(h, SyncEventKinds.AppState).Should().HaveCount(2);

        h.Server.Restore(nextDeviceSeqAtBackup: h.Server.NextDeviceSeq, highAtBackup: h.Server.High);
        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(2);

        Accepted(h, SyncEventKinds.AppState).Should().HaveCount(3, "伺服器換了實例：published 清掉，以最後的狀態重送一次");
        Accepted(h, SyncEventKinds.AppState)[^1].Value<string>("phase").Should().Be(AppStatePhases.Downloading);
    }
}
