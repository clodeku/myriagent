using System.Net;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;

namespace Myriagent.Tests.Sync;

/// <summary>項目欄位 blob 的上傳（WP4.7 計畫步 10，D15、D31；E5 HEAD → PUT）：假的 E5 是 <see cref="FakeBlobEndpoint"/></summary>
public sealed class BlobUploaderTests : IDisposable
{
    private readonly FakeRelayHandler relay = new();
    private readonly FakeBlobEndpoint blobs;
    private readonly AccessTokenHolder tokens = new();
    private readonly BlobUploader uploader;

    public BlobUploaderTests()
    {
        blobs = new FakeBlobEndpoint(relay);
        tokens.Set("cha_unit", TimeProvider.System.GetTimestamp());
        var hub = new Uri("https://hub.example/");
        uploader = new BlobUploader(new DeviceHttpClient(hub, hub, false, tokens, _ => Task.FromResult(false), TimeProvider.System, relay));
    }

    public void Dispose() => uploader.Dispose();

    private static ConversationBlob Blob(string text)
    {
        var content = Encoding.UTF8.GetBytes(text);
        return new ConversationBlob(Convert.ToHexStringLower(SHA256.HashData(content)), content);
    }

    [Fact]
    public async Task 伺服器沒有就PUT_內容與標頭()
    {
        var blob = Blob("很長的輸出");

        (await uploader.UploadAsync(blob, CancellationToken.None)).Should().Be(BlobUploadOutcome.Confirmed);

        relay.Requests.Select(r => r.Method).Should().Equal(HttpMethod.Head, HttpMethod.Put);
        var put = relay.Requests[1];
        put.Path.Should().Be("/api/v1/device/blobs/" + blob.Sha256);
        put.Body.Should().Equal(blob.Content);
        put.Header("Content-Type").Should().Be("application/octet-stream");
        put.Header("Content-Length").Should().Be(blob.Content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        put.Authorization.Should().Be("Bearer cha_unit");
        blobs.Read(blob.Sha256).Should().Equal(blob.Content);
    }

    [Fact]
    public async Task 伺服器已經有就不傳()
    {
        var blob = Blob("已經有的");
        blobs.Put(blob.Content);

        (await uploader.UploadAsync(blob, CancellationToken.None)).Should().Be(BlobUploadOutcome.Confirmed);

        blobs.Heads.Should().Be(1);
        blobs.Puts.Should().BeEmpty();
    }

    [Fact]
    public async Task DLP拒收回Rejected()
    {
        blobs.PutResult = (_, _) => HttpStatusCode.UnprocessableEntity;

        (await uploader.UploadAsync(Blob("被擋的"), CancellationToken.None)).Should().Be(BlobUploadOutcome.Rejected);
        blobs.Stored.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.InsufficientStorage)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task 伺服器暫時不收回Failed(HttpStatusCode status)
    {
        blobs.PutResult = (_, _) => status;

        (await uploader.UploadAsync(Blob("等一下"), CancellationToken.None)).Should().Be(BlobUploadOutcome.Failed);
    }

    [Fact]
    public async Task HEAD失敗就不PUT()
    {
        blobs.HeadResult = _ => HttpStatusCode.ServiceUnavailable;

        (await uploader.UploadAsync(Blob("x"), CancellationToken.None)).Should().Be(BlobUploadOutcome.Failed);
        blobs.Puts.Should().BeEmpty();
    }

    [Fact]
    public async Task 沒有權杖不送()
    {
        tokens.Clear();

        (await uploader.UploadAsync(Blob("x"), CancellationToken.None)).Should().Be(BlobUploadOutcome.Failed);
        relay.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task 雜湊或大小不合格不送()
    {
        (await uploader.UploadAsync(new ConversationBlob("not-a-sha", [1]), CancellationToken.None)).Should().Be(BlobUploadOutcome.Rejected);
        (await uploader.UploadAsync(new ConversationBlob(new string('a', 64), []), CancellationToken.None)).Should().Be(BlobUploadOutcome.Rejected);
        var big = new byte[ItemBlobRules.MaxItemBlobBytes + 1];
        (await uploader.UploadAsync(new ConversationBlob(Convert.ToHexStringLower(SHA256.HashData(big)), big), CancellationToken.None))
            .Should().Be(BlobUploadOutcome.Rejected);
        relay.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task 連不上回Failed_取消照丟()
    {
        relay.AsyncResponder = (_, _) => throw new HttpRequestException("連不上");
        (await uploader.UploadAsync(Blob("x"), CancellationToken.None)).Should().Be(BlobUploadOutcome.Failed);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await FluentActions.Awaiting(() => uploader.UploadAsync(Blob("y"), cts.Token)).Should().ThrowAsync<OperationCanceledException>();
    }
}
