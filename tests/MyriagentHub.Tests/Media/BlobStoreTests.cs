using System.Text;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Tests.Media;

/// <summary>伺服器的內容定址 blob 儲存（WP4.7 計畫 D14、D16、D17、D20；IBlobStore 的凍結語意）</summary>
public sealed class BlobStoreTests : IDisposable
{
    private readonly string dir = TempDir.Create();
    private readonly HubPaths paths;
    private readonly FakeDiskSpace disk = new(500L << 30);
    private SyncWriter? writer;

    public BlobStoreTests()
    {
        paths = new HubPaths(dir);
        paths.EnsureCreated();
    }

    public void Dispose()
    {
        writer?.Stop();
        TempDir.TryDelete(dir);
    }

    [Fact]
    public async Task PutAsync_存入後讀得回來()
    {
        var store = await StartAsync();
        var data = Encoding.UTF8.GetBytes("hello blob");
        var sha = MediaBytes.Sha(data);

        (await store.PutAsync(sha, data.Length, "item-body", "text/plain", new MemoryStream(data), CancellationToken.None)).Should().BeTrue();

        store.Exists(sha).Should().BeTrue();
        await using (var read = store.OpenRead(sha))
        {
            read.Should().NotBeNull();
            using var copy = new MemoryStream();
            await read!.CopyToAsync(copy);
            copy.ToArray().Should().Equal(data);
        }

        File.Exists(Path.Combine(paths.BlobsDir, sha[..2], sha)).Should().BeTrue("blobs\\{前 2 碼}\\{sha}");
        store.UsedBytes.Should().Be(data.Length);
        Row(sha).Should().Be(((long)data.Length, "item-body", "text/plain"));
        TmpFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task PutAsync_雜湊不符丟InvalidDataException()
    {
        var store = await StartAsync();
        var data = Encoding.UTF8.GetBytes("真的內容");
        var wrongSha = MediaBytes.Sha(Encoding.UTF8.GetBytes("別的內容"));

        await FluentActions.Awaiting(() => store.PutAsync(wrongSha, data.Length, "upload", null, new MemoryStream(data), CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();

        AssertNothingStored(store);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task PutAsync_長度不符丟InvalidDataException(int delta)
    {
        var store = await StartAsync();
        var data = Encoding.UTF8.GetBytes("長度不符");

        await FluentActions.Awaiting(() => store.PutAsync(MediaBytes.Sha(data), data.Length + delta, "upload", null, new MemoryStream(data), CancellationToken.None))
            .Should().ThrowAsync<InvalidDataException>();

        AssertNothingStored(store);
    }

    [Fact]
    public async Task PutAsync_已存在回false()
    {
        var store = await StartAsync();
        var data = MediaBytes.Random(1000);
        var sha = MediaBytes.Sha(data);
        (await store.PutAsync(sha, data.Length, "upload", null, new MemoryStream(data), CancellationToken.None)).Should().BeTrue();
        var file = Path.Combine(paths.BlobsDir, sha[..2], sha);
        var old = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, old);

        (await store.PutAsync(sha, data.Length, "upload", null, new ThrowingStream(), CancellationToken.None)).Should().BeFalse("已存在：不讀內容、不重寫");

        File.GetLastWriteTimeUtc(file).Should().Be(old);
        store.UsedBytes.Should().Be(data.Length);
        RowCount().Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Put_同一個sha並行兩次(bool knownSha)
    {
        var store = await StartAsync();
        var data = MediaBytes.Random(300_000);
        var sha = MediaBytes.Sha(data);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        void Started()
        {
            if (Interlocked.Increment(ref started) == 2)
                gate.SetResult();
        }

        var puts = Enumerable.Range(0, 2).Select(_ => Task.Run(() => store.PutStreamingAsync(knownSha ? sha : null, data.Length, 1 << 20, "upload", null,
            new ScriptedStream(data, gate.Task, Started), inspect: null, CancellationToken.None))).ToList();
        var results = await Task.WhenAll(puts);

        results.Select(r => r.Outcome).Should().BeEquivalentTo(new[] { BlobPutOutcome.Stored, BlobPutOutcome.Exists });
        results.Should().OnlyContain(r => r.Sha256 == sha && r.Length == data.Length);
        RowCount().Should().Be(1);
        BlobFiles().Should().ContainSingle();
        store.UsedBytes.Should().Be(data.Length, "用量只加一次");
        TmpFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task PutStreaming_inspect拒絕時什麼都不留()
    {
        var store = await StartAsync();
        var data = Encoding.UTF8.GetBytes("要被擋的內容");
        byte[]? inspected = null;

        var result = await store.PutStreamingAsync(null, null, 1024, "item-body", null, new MemoryStream(data), async (tmp, ct) =>
        {
            inspected = await File.ReadAllBytesAsync(tmp, ct);
            return "dlp";
        }, CancellationToken.None);

        result.Should().Be(new BlobPutResult(BlobPutOutcome.Rejected, MediaBytes.Sha(data), data.Length, "dlp"));
        inspected.Should().Equal(data, "檢查在改名之前、以完整的暫存檔呼叫");
        AssertNothingStored(store);
    }

    [Fact]
    public async Task PutStreaming_inspect通過才存()
    {
        var store = await StartAsync();
        var data = Encoding.UTF8.GetBytes("通過檢查");

        var result = await store.PutStreamingAsync(null, null, 1024, "item-body", null, new MemoryStream(data), (_, _) => Task.FromResult<string?>(null), CancellationToken.None);

        result.Should().Be(new BlobPutResult(BlobPutOutcome.Stored, MediaBytes.Sha(data), data.Length));
        store.Exists(MediaBytes.Sha(data)).Should().BeTrue();
    }

    [Fact]
    public async Task PutStreaming_超過上限()
    {
        var store = await StartAsync();
        var endless = new ScriptedStream(MediaBytes.Random(64), endless: true);

        var result = await store.PutStreamingAsync(null, null, 10, "upload", null, endless, inspect: null, CancellationToken.None);

        result.Outcome.Should().Be(BlobPutOutcome.TooLarge);
        endless.BytesRead.Should().Be(11, "讀到第 11 個位元組就停");
        AssertNothingStored(store);
    }

    [Fact]
    public async Task PutStreaming_宣告長度超過上限不讀內容()
    {
        var store = await StartAsync();

        var result = await store.PutStreamingAsync(null, 11, 10, "upload", null, new ThrowingStream(), inspect: null, CancellationToken.None);

        result.Outcome.Should().Be(BlobPutOutcome.TooLarge);
        AssertNothingStored(store);
    }

    [Fact]
    public async Task PutStreaming_不知道雜湊時邊收邊算()
    {
        var store = await StartAsync();
        var data = MediaBytes.Random(600_000, seed: 11);

        var result = await store.PutStreamingAsync(null, null, 1 << 20, "upload", "application/pdf", new ScriptedStream(data), inspect: null, CancellationToken.None);

        result.Should().Be(new BlobPutResult(BlobPutOutcome.Stored, MediaBytes.Sha(data), data.Length));
        Row(result.Sha256!).Should().Be(((long)data.Length, "upload", "application/pdf"));
    }

    [Fact]
    public async Task PutStreaming_空的內容()
    {
        var store = await StartAsync();

        var result = await store.PutStreamingAsync(null, 0, 10, "upload", null, Stream.Null, inspect: null, CancellationToken.None);

        result.Should().Be(new BlobPutResult(BlobPutOutcome.Stored, MediaBytes.Sha([]), 0));
    }

    [Fact]
    public async Task PutStreaming_取消時刪暫存檔()
    {
        var store = await StartAsync();
        using var cancel = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var put = store.PutStreamingAsync(null, null, 1024, "upload", null, new ScriptedStream(MediaBytes.Random(100), gate.Task, () => cancel.Cancel()), inspect: null, cancel.Token);

        await FluentActions.Awaiting(() => put).Should().ThrowAsync<OperationCanceledException>();

        AssertNothingStored(store);
        store.ReservedBytes.Should().Be(0, "預留的位元組要放掉");
    }

    [Fact]
    public async Task Exists_以列為準()
    {
        var store = await StartAsync();
        var data = Encoding.UTF8.GetBytes("只有檔沒有列");
        var sha = MediaBytes.Sha(data);
        var file = Path.Combine(paths.BlobsDir, sha[..2], sha);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, data);

        store.Exists(sha).Should().BeFalse("當機留下的有檔沒列不算存在");

        (await store.PutAsync(sha, data.Length, "upload", null, new MemoryStream(data), CancellationToken.None)).Should().BeTrue("補上列");
        store.Exists(sha).Should().BeTrue();
        BlobFiles().Should().ContainSingle();
        store.UsedBytes.Should().Be(data.Length);
        TmpFiles().Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("..")]
    public async Task 不合格的sha一律不存在(string? sha)
    {
        var store = await StartAsync();

        store.Exists(sha!).Should().BeFalse();
        store.OpenRead(sha!).Should().BeNull();
        await FluentActions.Awaiting(() => store.PutAsync(sha!, 0, "upload", null, Stream.Null, CancellationToken.None)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task 配額到拒收門檻回QuotaExceeded()
    {
        var store = await StartAsync(new MediaOptions { QuotaBytes = 100, AlertPercent = 50, RefusePercent = 95, MinFreeBytes = 1 });
        var first = MediaBytes.Random(90, seed: 1);
        (await store.PutStreamingAsync(null, first.Length, 1000, "upload", null, new MemoryStream(first), null, CancellationToken.None))
            .Outcome.Should().Be(BlobPutOutcome.Stored, "90 位元組還沒到 95%");
        store.Level().Should().Be(BlobQuotaLevel.Warn);

        var second = MediaBytes.Random(5, seed: 2);
        (await store.PutStreamingAsync(null, second.Length, 1000, "upload", null, new ThrowingStream(), null, CancellationToken.None))
            .Outcome.Should().Be(BlobPutOutcome.QuotaExceeded, "存了之後是 95%");
        await FluentActions.Awaiting(() => store.PutAsync(MediaBytes.Sha(second), second.Length, "upload", null, new MemoryStream(second), CancellationToken.None))
            .Should().ThrowAsync<BlobStoreFullException>().Where(e => e.Reason == BlobPutOutcome.QuotaExceeded);

        var small = MediaBytes.Random(4, seed: 3);
        (await store.PutStreamingAsync(null, small.Length, 1000, "upload", null, new MemoryStream(small), null, CancellationToken.None))
            .Outcome.Should().Be(BlobPutOutcome.Stored, "94 位元組還收");
        RowCount().Should().Be(2);
        TmpFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task 不知道長度時以上限預留()
    {
        var store = await StartAsync(new MediaOptions { QuotaBytes = 100, AlertPercent = 50, RefusePercent = 95, MinFreeBytes = 1 });
        var data = MediaBytes.Random(10);

        (await store.PutStreamingAsync(null, null, 95, "upload", null, new ThrowingStream(), null, CancellationToken.None))
            .Outcome.Should().Be(BlobPutOutcome.QuotaExceeded, "長度未知時以上限 95 位元組算");
        (await store.PutStreamingAsync(null, null, 94, "upload", null, new MemoryStream(data), null, CancellationToken.None))
            .Outcome.Should().Be(BlobPutOutcome.Stored);
    }

    [Fact]
    public async Task 進行中的寫入算進配額()
    {
        var store = await StartAsync(new MediaOptions { QuotaBytes = 100, AlertPercent = 50, RefusePercent = 95, MinFreeBytes = 1 });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var big = MediaBytes.Random(60, seed: 4);
        var slow = Task.Run(() => store.PutStreamingAsync(null, big.Length, 1000, "upload", null, new ScriptedStream(big, gate.Task, () => reading.SetResult()), null, CancellationToken.None));
        await reading.Task;
        store.ReservedBytes.Should().Be(60);

        var other = MediaBytes.Random(40, seed: 5);
        (await store.PutStreamingAsync(null, other.Length, 1000, "upload", null, new ThrowingStream(), null, CancellationToken.None))
            .Outcome.Should().Be(BlobPutOutcome.QuotaExceeded, "預留的 60 加上 40 超過拒收門檻");

        gate.SetResult();
        (await slow).Outcome.Should().Be(BlobPutOutcome.Stored);
        store.ReservedBytes.Should().Be(0);
        store.UsedBytes.Should().Be(60);
    }

    [Fact]
    public async Task 磁碟可用空間低於下限回DiskLow()
    {
        var store = await StartAsync(new MediaOptions { MinFreeBytes = 1000 });
        var data = MediaBytes.Random(10);

        disk.Free = 1009;
        (await store.PutStreamingAsync(null, data.Length, 100, "upload", null, new ThrowingStream(), null, CancellationToken.None))
            .Outcome.Should().Be(BlobPutOutcome.DiskLow, "存了之後只剩 999");
        disk.Free = -1;
        (await store.PutStreamingAsync(null, data.Length, 100, "upload", null, new ThrowingStream(), null, CancellationToken.None))
            .Outcome.Should().Be(BlobPutOutcome.DiskLow, "量不到可用空間時不寫");
        store.Level().Should().Be(BlobQuotaLevel.Refuse);

        disk.Free = 1010;
        (await store.PutStreamingAsync(null, data.Length, 100, "upload", null, new MemoryStream(data), null, CancellationToken.None))
            .Outcome.Should().Be(BlobPutOutcome.Stored);
        store.Level().Should().Be(BlobQuotaLevel.Ok);
    }

    [Fact]
    public async Task Initialize_載入用量並清掉上次的暫存檔()
    {
        var store = await StartAsync();
        var a = MediaBytes.Random(123, seed: 21);
        var b = MediaBytes.Random(456, seed: 22);
        await store.PutAsync(MediaBytes.Sha(a), a.Length, "upload", null, new MemoryStream(a), CancellationToken.None);
        await store.PutAsync(MediaBytes.Sha(b), b.Length, "thumb", "image/png", new MemoryStream(b), CancellationToken.None);
        var leftover = Path.Combine(paths.TmpDir, BlobStore.TmpPrefix + "crashed");
        var others = Path.Combine(paths.TmpDir, "not-a-blob.tmp");
        await File.WriteAllTextAsync(leftover, "x");
        await File.WriteAllTextAsync(others, "y");

        var again = new BlobStore(paths, writer!, Options.Create(new HubOptions()), disk, NullLogger<BlobStore>.Instance);
        again.Initialize();

        again.UsedBytes.Should().Be(a.Length + b.Length);
        File.Exists(leftover).Should().BeFalse();
        File.Exists(others).Should().BeTrue("只清自己的 blob-* 暫存檔");
    }

    private async Task<BlobStore> StartAsync(MediaOptions? media = null)
    {
        writer = new SyncWriter(paths, TimeProvider.System, NullLogger<SyncWriter>.Instance);
        await writer.InitializeAsync(CancellationToken.None);
        var store = new BlobStore(paths, writer, Options.Create(new HubOptions { Media = media ?? new MediaOptions() }), disk, NullLogger<BlobStore>.Instance);
        store.Initialize();
        return store;
    }

    private void AssertNothingStored(BlobStore store)
    {
        BlobFiles().Should().BeEmpty("blobs\\ 沒有檔");
        TmpFiles().Should().BeEmpty("tmp\\ 是空的");
        RowCount().Should().Be(0, "blobs 表 0 列");
        store.UsedBytes.Should().Be(0);
    }

    private IEnumerable<string> BlobFiles() => Directory.EnumerateFiles(paths.BlobsDir, "*", SearchOption.AllDirectories);

    private IEnumerable<string> TmpFiles() => Directory.EnumerateFiles(paths.TmpDir, "*", SearchOption.TopDirectoryOnly);

    private long RowCount()
    {
        using var connection = writer!.OpenRead();
        return (long)SqliteSetup.Scalar(connection, "SELECT COUNT(*) FROM blobs")!;
    }

    private (long Size, string Kind, string? ContentType) Row(string sha)
    {
        using var connection = writer!.OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT size, kind, content_type FROM blobs WHERE sha256 = $sha";
        cmd.Parameters.AddWithValue("$sha", sha);
        using var reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue();
        return (reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }
}
