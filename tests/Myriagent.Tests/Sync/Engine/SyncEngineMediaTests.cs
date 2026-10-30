using System.Net;
using System.Text;
using Myriagent.Hub;
using Myriagent.Media;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 引擎的媒體轉送（WP4.7 計畫步 8，D24、D26）：media1 的宣告、cha_ 依單調時鐘 13 分鐘換新（不看伺服器的 expiresAt）、
/// 401 換一次再試一次、MediaRequest 查 media_refs_local 後由 MediaServer 回 E7、連線丟掉時清掉權杖並取消那條連線的轉送。
/// 伺服器是 <see cref="FakeSyncServer"/>，E7 是 <see cref="FakeRelayHandler"/>，中樞是 <see cref="FakeMediaScope"/>
/// </summary>
public sealed class SyncEngineMediaTests
{
    private static readonly string[] AllFeatures = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Media1];

    /// <summary>引擎＋盒子＋假 UI＋假中樞的媒體快照＋假 E7（預設遠端政策 View）</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public Rig()
        {
            Box = new ConversationChangeBox();
            H = new EngineHarness(conversations: Box) { Policy = new DevicePolicy("View", false) };
            Ui = new FakeConversationUi(Box, () => H.Engine);
            Box.UiPort = Ui;
            Box.MediaScope = Scope;
            H.Server.Features = AllFeatures;
            _ = H.Server.Conversations;
        }

        public EngineHarness H { get; }

        public ConversationChangeBox Box { get; }

        public FakeConversationUi Ui { get; }

        public FakeMediaScope Scope { get; } = new();

        public FakeRelayHandler Relay { get; } = new();

        public MediaTestFiles Files { get; } = new();

        public Func<string, int, CancellationToken, Task<byte[]?>>? Thumbnail { get; set; }

        public Task StartAsync(Action<SyncEngine>? beforeStart = null) => H.StartAsync(e =>
        {
            e.MediaHttpHandlerForTest = Relay;
            e.MediaGuardForTest = MediaTestFiles.Guard();
            e.MediaThumbnailForTest = Thumbnail;
            beforeStart?.Invoke(e);
        });

        /// <summary>登記一個附件的參照（直接寫進 media_refs_local）</summary>
        public async Task<LocalMediaRefRow> RefAsync(string attachmentId = "att-1")
        {
            var row = new LocalMediaRefRow(MediaTestFiles.NewId(), MediaTestFiles.ConversationId, "item-1", attachmentId, null, 1791600000000);
            await H.Inspect(v =>
            {
                v.Store!.AddMediaRefs([row]);
                return 0;
            });
            return row;
        }

        /// <summary>伺服器送一個 MediaRequest，等引擎與轉送都做完</summary>
        public async Task<RelayPut> RequestAsync(string reference, string purpose, MediaRange? range = null)
        {
            var relayId = MediaTestFiles.NewId();
            H.Server.Current!.Media(new MediaRequest(relayId, reference, purpose, range));
            await H.IdleAsync();
            return Relay.Puts.Should().ContainSingle(p => p.RelayId == relayId).Subject;
        }

        public async ValueTask DisposeAsync()
        {
            await H.DisposeAsync();
            Files.Dispose();
        }
    }

    // ── 宣告 ──

    [Fact]
    public async Task 有盒子_Hello宣告conv1_live1_media1_協商出media1()
    {
        await using var rig = new Rig();
        await rig.StartAsync();

        rig.H.Server.Hellos.Single().Features.Should().Equal(DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Live2, DeviceFeatureRules.Prov1, DeviceFeatureRules.Media1);
        (await rig.H.Inspect(v => v.Features)).Should().Equal(AllFeatures);
        rig.H.Engine.AccessTokenForTest.Should().Be("cha_test", "Prove 回的權杖");
    }

    [Fact]
    public async Task 也宣告cmd1時_media1排在最後()
    {
        await using var rig = new Rig();
        rig.H.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Media1];

        // WP4.4 不在這個分支：以測試接縫代替它宣告 cmd1
        await rig.StartAsync(e => e.ContractProbeForTest = (hook, arg) =>
        {
            if (hook == "ContributeCommandFeatures")
                ((List<string>)arg!).Add(DeviceFeatureRules.Cmd1);
        });

        rig.H.Server.Hellos.Single().Features.Should().Equal(DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Live2, DeviceFeatureRules.Prov1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Media1);
    }

    [Fact]
    public async Task 伺服器沒有media1_不存權杖也不換()
    {
        await using var rig = new Rig();
        rig.H.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];
        await rig.StartAsync();

        await rig.H.AdvanceAsync(TimeSpan.FromMinutes(30));

        rig.H.Engine.AccessTokenForTest.Should().BeNull();
        rig.H.Server.AccessTokenRefreshes.Should().Be(0);
        rig.H.Server.Calls.Should().NotContain(c => c.EndsWith(":RefreshAccessToken", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DLP版本不夠而不宣告conv1時_也不宣告media1()
    {
        await using var rig = new Rig();
        rig.H.DlpVersion = ConversationPublisher.RequiredDlpVersion - 1;
        await rig.StartAsync();

        var features = rig.H.Server.Hellos.Single().Features ?? [];
        features.Should().NotContain(DeviceFeatureRules.Conv1, "陽性對照：DLP 版本不夠");
        features.Should().NotContain(DeviceFeatureRules.Media1, "參照只在發布對話時發得出去");
        rig.H.Engine.AccessTokenForTest.Should().BeNull();
    }

    [Fact]
    public async Task 沒有盒子_不宣告media1()
    {
        await using var h = new EngineHarness();
        h.Server.Features = AllFeatures;
        await h.StartAsync();

        h.Server.Hellos.Single().Features.Should().BeNull();
        h.Engine.AccessTokenForTest.Should().BeNull();
    }

    // ── cha_ 的換新（D24） ──

    [Fact]
    public async Task 假時鐘走到13分鐘時剛好換1次權杖_之後每13分鐘一次()
    {
        await using var rig = new Rig();
        await rig.StartAsync();

        await rig.H.AdvanceAsync(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(59));
        rig.H.Server.AccessTokenRefreshes.Should().Be(0);
        rig.H.Engine.AccessTokenForTest.Should().Be("cha_test");

        await rig.H.AdvanceAsync(1);
        rig.H.Server.AccessTokenRefreshes.Should().Be(1);
        rig.H.Engine.AccessTokenForTest.Should().Be("cha_test-1");

        await rig.H.AdvanceAsync(TimeSpan.FromMinutes(13));
        rig.H.Server.AccessTokenRefreshes.Should().Be(2);
        rig.H.Engine.AccessTokenForTest.Should().Be("cha_test-2");
    }

    [Fact]
    public async Task 伺服器的expiresAt比本機時鐘早1小時也不影響()
    {
        await using var rig = new Rig();
        rig.H.Server.AccessTokenExpiryOffsetMs = -3_600_000;
        await rig.StartAsync();
        var row = await rig.RefAsync();
        var path = rig.Files.Write(Path.Combine("data", "conversations", MediaTestFiles.ConversationId, "attachments", "a.png"), MediaTestFiles.Png);
        rig.Scope.Answer = _ => rig.Files.Snapshot(resolved: (path, "a.png"));

        var put = await rig.RequestAsync(row.Ref, MediaPurposes.Media, new MediaRange(0, 3));
        await rig.H.AdvanceAsync(TimeSpan.FromMinutes(12));

        put.Status.Should().Be(206);
        put.Authorization.Should().Be("Bearer cha_test", "到期只從收到的時間算，不和本機時鐘比");
        rig.H.Server.AccessTokenRefreshes.Should().Be(0);
        await rig.H.AdvanceAsync(TimeSpan.FromMinutes(1));
        rig.H.Server.AccessTokenRefreshes.Should().Be(1);
    }

    [Fact]
    public async Task 換新失敗_連線照常_1分鐘後再試()
    {
        await using var rig = new Rig();
        rig.H.Server.AccessTokenFailure = n => n == 1 ? EngineHarness.Transient() : null;
        await rig.StartAsync();

        await rig.H.AdvanceAsync(TimeSpan.FromMinutes(13));
        rig.H.Server.AccessTokenRefreshes.Should().Be(1);
        (await rig.H.StateAsync()).Should().Be(SyncEngineState.Live, "權杖只影響媒體，不為它重連");
        rig.H.Server.Sessions.Should().ContainSingle();
        rig.H.Engine.AccessTokenForTest.Should().Be("cha_test", "換不到時沿用舊的（伺服器 15 分鐘內照收）");

        await rig.H.AdvanceAsync(TimeSpan.FromMinutes(1));
        rig.H.Server.AccessTokenRefreshes.Should().Be(2);
        rig.H.Engine.AccessTokenForTest.Should().Be("cha_test-2");
    }

    [Fact]
    public async Task 收到401時換一次再試一次_第二次的結果照回()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        rig.Relay.Statuses.Enqueue(HttpStatusCode.Unauthorized);
        rig.Relay.Statuses.Enqueue(HttpStatusCode.Unauthorized);

        rig.H.Server.Current!.Media(new MediaRequest(MediaTestFiles.NewId(), MediaTestFiles.NewId(), MediaPurposes.Thumb));
        await rig.H.IdleAsync();

        rig.H.Server.AccessTokenRefreshes.Should().Be(1, "只換一次");
        rig.Relay.Puts.Should().HaveCount(2, "第二次的 401 不再試");
        rig.Relay.Puts[0].Authorization.Should().Be("Bearer cha_test");
        rig.Relay.Puts[1].Authorization.Should().Be("Bearer cha_test-1");
        rig.Relay.Puts[1].RelayId.Should().Be(rig.Relay.Puts[0].RelayId);
        rig.Relay.Puts[1].Status.Should().Be(404, "重送的是同一個回覆");

        // 下一個轉送：401 之後換到的新權杖送得過
        rig.Relay.Statuses.Enqueue(HttpStatusCode.Unauthorized);
        var relayId = MediaTestFiles.NewId();
        rig.H.Server.Current!.Media(new MediaRequest(relayId, MediaTestFiles.NewId(), MediaPurposes.Thumb));
        await rig.H.IdleAsync();
        rig.H.Server.AccessTokenRefreshes.Should().Be(2);
        rig.Relay.Puts.Where(p => p.RelayId == relayId).Select(p => p.Authorization).Should().Equal("Bearer cha_test-1", "Bearer cha_test-2");
        rig.Relay.Statuses.Should().BeEmpty();
        rig.H.Engine.AccessTokenForTest.Should().Be("cha_test-2");
    }

    // ── MediaRequest ──

    [Fact]
    public async Task 不認得的參照立刻回404_不問中樞()
    {
        await using var rig = new Rig();
        await rig.StartAsync();

        var put = await rig.RequestAsync(MediaTestFiles.NewId(), MediaPurposes.Media, new MediaRange(0, 10));
        var malformed = await rig.RequestAsync("not-a-ref", MediaPurposes.Thumb);

        put.Status.Should().Be(404);
        put.Header(MediaRelayHeaders.Error).Should().Be(MediaErrorCodes.NotFound);
        put.Path.Should().Be("/api/v1/device/relay/" + put.RelayId);
        malformed.Status.Should().Be(404);
        rig.Scope.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task 範圍2到5_經UI執行緒問中樞_本文是第2到5個位元組()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var row = await rig.RefAsync();
        var path = rig.Files.Write(Path.Combine("data", "conversations", MediaTestFiles.ConversationId, "attachments", "photo.png"), Encoding.ASCII.GetBytes("0123456789"));
        rig.Scope.Answer = _ => rig.Files.Snapshot(resolved: (path, "螢幕截圖.png"));

        var put = await rig.RequestAsync(row.Ref, MediaPurposes.Media, new MediaRange(2, 5));

        put.Status.Should().Be(206);
        Encoding.ASCII.GetString(put.Body).Should().Be("2345");
        put.Header("Content-Range").Should().Be("bytes 2-5/10");
        put.Authorization.Should().Be("Bearer cha_test");
        var (query, thread) = rig.Scope.Queries.Should().ContainSingle().Subject;
        query.Should().Be(new MediaScopeQuery(MediaTestFiles.ConversationId, "item-1", "att-1", null, rig.H.DeviceId));
        thread.Should().Be(rig.H.Ui.Invoke(() => Environment.CurrentManagedThreadId), "中樞的狀態只在 UI 執行緒讀");
    }

    [Fact]
    public async Task 中樞還沒掛上_回404()
    {
        await using var rig = new Rig();
        rig.Box.MediaScope = null;
        await rig.StartAsync();
        var row = await rig.RefAsync();

        (await rig.RequestAsync(row.Ref, MediaPurposes.Media, new MediaRange(0, 1))).Status.Should().Be(404);
    }

    [Fact]
    public async Task 引擎的遠端政策Off_原檔403policy_縮圖照常()
    {
        await using var rig = new Rig();
        rig.H.Policy = new DevicePolicy("Off", false);
        rig.Thumbnail = (_, _, _) => Task.FromResult<byte[]?>(MediaTestFiles.Png);
        await rig.StartAsync();
        var row = await rig.RefAsync();
        var path = rig.Files.Write(Path.Combine("data", "conversations", MediaTestFiles.ConversationId, "attachments", "photo.png"), MediaTestFiles.Png);
        rig.Scope.Answer = _ => rig.Files.Snapshot(resolved: (path, "photo.png"));

        var media = await rig.RequestAsync(row.Ref, MediaPurposes.Media, new MediaRange(0, 3));
        var thumb = await rig.RequestAsync(row.Ref, MediaPurposes.Thumb);

        rig.H.Server.Hellos.Single().Policy.Should().Be(new DevicePolicy("Off", false));
        media.Status.Should().Be(403);
        media.Header(MediaRelayHeaders.Error).Should().Be(MediaErrorCodes.Policy);
        thumb.Status.Should().Be(200);
        thumb.Body.Should().Equal(MediaTestFiles.Png);
    }

    [Fact]
    public async Task 連線丟掉_清掉權杖並取消那條連線的轉送_重連後拿新權杖()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var row = await rig.RefAsync();
        var path = rig.Files.Write(Path.Combine("data", "conversations", MediaTestFiles.ConversationId, "attachments", "photo.png"), MediaTestFiles.Png);
        rig.Scope.Answer = _ => rig.Files.Snapshot(resolved: (path, "photo.png"));
        rig.Relay.Hold = _ => true;
        var relayId = MediaTestFiles.NewId();

        rig.H.Server.Current!.Media(new MediaRequest(relayId, row.Ref, MediaPurposes.Media, new MediaRange(0, 3)));
        await rig.Relay.WaitForHeldAsync(1);
        rig.H.Server.Current.Drop();
        await rig.H.IdleAsync();

        rig.Relay.Cancelled.Should().Equal(relayId);
        rig.H.Engine.AccessTokenForTest.Should().BeNull();
        (await rig.H.StateAsync()).Should().Be(SyncEngineState.Backoff);

        await rig.H.AdvanceAsync(1);
        (await rig.H.StateAsync()).Should().Be(SyncEngineState.Live);
        rig.H.Engine.AccessTokenForTest.Should().Be("cha_test");
        rig.H.Server.AccessTokenRefreshes.Should().Be(0, "舊連線的計時器取消了");
    }

    [Fact]
    public async Task 引擎停止時取消進行中的轉送()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        rig.Relay.Hold = _ => true;
        var relayId = MediaTestFiles.NewId();

        rig.H.Server.Current!.Media(new MediaRequest(relayId, MediaTestFiles.NewId(), MediaPurposes.Thumb));
        await rig.Relay.WaitForHeldAsync(1);
        await rig.H.Engine.StopAsync(TimeSpan.FromSeconds(5));

        rig.Relay.Cancelled.Should().Equal(relayId);
        rig.H.Engine.AccessTokenForTest.Should().BeNull();
    }
}
