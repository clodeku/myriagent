using System.Net;
using System.Security.Cryptography;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 引擎那一側的遠端檔案傳輸（WP4.7 計畫步 11；D22、D33、D36）：協商出 media1 時掛到盒子上、停止時拿下來；抽屜與 fileFetch 的參照在引擎執行緒
/// 寫進 media_refs_local（抽屜 29 天內重用、fileFetch 每次新配）；第一次協商與之後每天修剪過期的列；上傳的檔以 E6 下載並驗大小與 SHA-256
/// </summary>
public sealed class RemoteFileTransferTests
{
    private static readonly string[] AllFeatures = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Media1];

    private const string Conv = MediaTestFiles.ConversationId;

    private static readonly long Day = (long)TimeSpan.FromDays(1).TotalMilliseconds;

    private sealed class Rig : IAsyncDisposable
    {
        public Rig()
        {
            Box = new ConversationChangeBox();
            // 心跳拉長：撥快一天時只剩換權杖與修剪的計時器
            H = new EngineHarness(heartbeat: TimeSpan.FromHours(6), conversations: Box) { Policy = new DevicePolicy("View", false) };
            Ui = new FakeConversationUi(Box, () => H.Engine);
            Box.UiPort = Ui;
            Box.MediaScope = new FakeMediaScope();
            H.Server.Features = AllFeatures;
            _ = H.Server.Conversations;
        }

        public EngineHarness H { get; }

        public ConversationChangeBox Box { get; }

        public FakeConversationUi Ui { get; }

        public FakeRelayHandler Relay { get; } = new();

        public TempDirectory Files { get; } = new("remote-transfer");

        public Task StartAsync() => H.StartAsync(e =>
        {
            e.MediaHttpHandlerForTest = Relay;
            e.MediaGuardForTest = MediaTestFiles.Guard();
        });

        public IRemoteFileTransfer Transfer => Box.RemoteFiles ?? throw new InvalidOperationException("盒子上沒有傳輸");

        public Task<IReadOnlyList<LocalMediaRefRow>> RowsAsync() => H.Inspect(v => v.Store!.LoadMediaRefs(Conv));

        public Task AddRowsAsync(params LocalMediaRefRow[] rows) => H.Inspect(v =>
        {
            v.Store!.AddMediaRefs(rows);
            return 0;
        });

        public async ValueTask DisposeAsync()
        {
            await H.DisposeAsync();
            Files.Dispose();
        }
    }

    private static LocalMediaRefRow Row(string item, string path, long createdAt) => new(MediaTestFiles.NewId(), Conv, item, null, path, createdAt);

    private static RemoteUpload Upload(byte[] content, string? sha = null)
        => new("upload-1", "a.bin", content.Length, sha ?? Convert.ToHexStringLower(SHA256.HashData(content)));

    private static HttpResponseMessage Blob(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    // ── 掛上與拿下 ──

    [Fact]
    public async Task 協商出media1時盒子上有傳輸_重開之後換新的_舊的不能再用()
    {
        await using var rig = new Rig();
        await rig.StartAsync();

        var first = rig.Box.RemoteFiles;
        first.Should().NotBeNull();
        first!.CanTransfer.Should().BeTrue("Prove 給了 cha_");

        await rig.H.RestartAsync();

        rig.Box.RemoteFiles.Should().NotBeNull().And.NotBeSameAs(first);
        first.CanTransfer.Should().BeFalse("停止時 Dispose");
        await FluentActions.Awaiting(() => first.RegisterRefsAsync(Conv, MediaServerRules.ListItemId, [@"C:\p\a.png"], CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task 伺服器沒有media1_盒子上沒有傳輸()
    {
        await using var rig = new Rig();
        rig.H.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];
        await rig.StartAsync();

        rig.Box.RemoteFiles.Should().BeNull();
    }

    // ── 參照 ──

    [Fact]
    public async Task 抽屜參照寫進media_refs_local_29天內重用_不新增列()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var now = rig.H.Time.NowMs;

        var first = await rig.Transfer.RegisterRefsAsync(Conv, MediaServerRules.ListItemId, [@"C:\p\a.png", @"C:\p\b.mp4"], CancellationToken.None);

        first.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        var rows = await rig.RowsAsync();
        rows.Select(r => (r.Ref, r.ItemId, r.MediaPath, r.AttachmentId, r.CreatedAt)).Should().BeEquivalentTo(
        [
            (first[0], MediaServerRules.ListItemId, @"C:\p\a.png", (string?)null, now),
            (first[1], MediaServerRules.ListItemId, @"C:\p\b.mp4", (string?)null, now),
        ]);

        var again = await rig.Transfer.RegisterRefsAsync(Conv, MediaServerRules.ListItemId, [@"C:\P\A.PNG"], CancellationToken.None);

        again.Should().Equal(first[0]);
        (await rig.RowsAsync()).Should().HaveCount(2, "重用，沒有新列");
    }

    [Fact]
    public async Task 抽屜參照超過29天配新的_fileFetch每次都新配()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var now = rig.H.Time.NowMs;
        var old = Row(MediaServerRules.ListItemId, @"C:\p\old.png", now - 29 * Day);
        var recent = Row(MediaServerRules.ListItemId, @"C:\p\recent.png", now - 28 * Day);
        await rig.AddRowsAsync(old, recent);

        var refs = await rig.Transfer.RegisterRefsAsync(Conv, MediaServerRules.ListItemId, [@"C:\p\old.png", @"C:\p\recent.png"], CancellationToken.None);

        refs[0].Should().NotBe(old.Ref);
        refs[1].Should().Be(recent.Ref);

        var file1 = await rig.Transfer.RegisterRefsAsync(Conv, MediaServerRules.FileItemId, [@"C:\p\a.txt"], CancellationToken.None);
        var file2 = await rig.Transfer.RegisterRefsAsync(Conv, MediaServerRules.FileItemId, [@"C:\p\a.txt"], CancellationToken.None);
        file1.Single().Should().NotBe(file2.Single());
        (await rig.RowsAsync()).Count(r => r.ItemId == MediaServerRules.FileItemId).Should().Be(2);
    }

    [Fact]
    public async Task 不合格的種類與對話id丟ArgumentException_空清單不排進引擎()
    {
        await using var rig = new Rig();
        await rig.StartAsync();

        await FluentActions.Awaiting(() => rig.Transfer.RegisterRefsAsync(Conv, "item-1", [@"C:\p\a.png"], CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => rig.Transfer.RegisterRefsAsync(Conv.ToUpperInvariant(), MediaServerRules.ListItemId, [@"C:\p\a.png"], CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        (await rig.Transfer.RegisterRefsAsync(Conv, MediaServerRules.ListItemId, [], CancellationToken.None)).Should().BeEmpty();
        (await rig.RowsAsync()).Should().BeEmpty();
    }

    // ── 修剪 ──

    [Fact]
    public async Task 協商出media1時修剪一次_之後每天一次_項目的列不動()
    {
        await using var rig = new Rig();
        var now = rig.H.Time.NowMs;
        var listOld = Row(MediaServerRules.ListItemId, @"C:\p\a.png", now - 31 * Day);
        var listKeep = Row(MediaServerRules.ListItemId, @"C:\p\b.png", now - Day);
        var fileOld = Row(MediaServerRules.FileItemId, @"C:\p\a.txt", now - 16 * 60_000);
        var fileKeep = Row(MediaServerRules.FileItemId, @"C:\p\b.txt", now - 60_000);
        var item = new LocalMediaRefRow(MediaTestFiles.NewId(), Conv, "item-1", "att-1", null, now - 100 * Day);
        using (var store = SyncStore.Open(rig.H.Paths.DatabaseFile, rig.H.DeviceId))
            store.AddMediaRefs([listOld, listKeep, fileOld, fileKeep, item]);

        await rig.StartAsync();

        (await rig.RowsAsync()).Select(r => r.Ref).Should().BeEquivalentTo([listKeep.Ref, fileKeep.Ref, item.Ref], "第一次協商出 media1 時修剪");

        await rig.H.AdvanceAsync(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));

        (await rig.RowsAsync()).Select(r => r.Ref).Should().BeEquivalentTo([listKeep.Ref, item.Ref], "每日修剪：fileFetch 的列過了 15 分鐘");
    }

    // ── 上傳的下載（E6） ──

    [Fact]
    public async Task 下載上傳的檔_帶cha_大小與雜湊都對才留下()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var content = RandomNumberGenerator.GetBytes(100_000);
        var upload = Upload(content);
        rig.Relay.Responder = put => put.Path == "/api/v1/device/blobs/" + upload.Sha256 ? Blob(content) : null;
        var target = rig.Files.Combine("a.bin");

        await rig.Transfer.DownloadUploadAsync(upload, target, CancellationToken.None);

        File.ReadAllBytes(target).Should().Equal(content);
        var request = rig.Relay.Requests.Should().ContainSingle(r => r.Path.StartsWith("/api/v1/device/blobs/", StringComparison.Ordinal)).Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Authorization.Should().Be("Bearer cha_test");
    }

    [Fact]
    public async Task 雜湊不符_大小不符_找不到都丟InvalidDataException_不留下檔案()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var content = RandomNumberGenerator.GetBytes(1000);
        var other = RandomNumberGenerator.GetBytes(1000);
        var cases = new (RemoteUpload Upload, HttpResponseMessage? Response)[]
        {
            (Upload(content, Convert.ToHexStringLower(SHA256.HashData(other))), Blob(content)),
            (Upload(content) with { Size = 999 }, Blob(content)),
            (Upload(content) with { Size = 1001 }, Blob(content)),
            (Upload(content), null),
        };

        for (var i = 0; i < cases.Length; i++)
        {
            var (upload, response) = cases[i];
            rig.Relay.Responder = _ => response;
            var target = rig.Files.Combine($"case{i}.bin");

            await FluentActions.Awaiting(() => rig.Transfer.DownloadUploadAsync(upload, target, CancellationToken.None))
                .Should().ThrowAsync<InvalidDataException>($"第 {i} 種");
            File.Exists(target).Should().BeFalse($"第 {i} 種");
        }
    }

    [Fact]
    public async Task 伺服器回500丟IOException_形狀不合格的上傳不送請求()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var content = RandomNumberGenerator.GetBytes(10);
        rig.Relay.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        await FluentActions.Awaiting(() => rig.Transfer.DownloadUploadAsync(Upload(content), rig.Files.Combine("a.bin"), CancellationToken.None))
            .Should().ThrowAsync<IOException>();

        var before = rig.Relay.Requests.Count;
        await FluentActions.Awaiting(() => rig.Transfer.DownloadUploadAsync(Upload(content) with { UploadId = @"..\x" }, rig.Files.Combine("b.bin"), CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();
        rig.Relay.Requests.Count.Should().Be(before);
    }
}
