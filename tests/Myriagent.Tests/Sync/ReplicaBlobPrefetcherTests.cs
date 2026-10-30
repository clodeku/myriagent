using System.Net;
using System.Security.Cryptography;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync;

/// <summary>
/// 複本的 blob 預取（WP4.7 計畫步 12，D39）：每頁同時 ≤4 個、合計 ≤32 MB（計畫限制）；E6 <c>GET /api/v1/device/blobs/{sha256}</c> 的結果分成取到、
/// 永久取不到、暫時取不到；暫時取不到的同一個 blob 要 5 次而且滿 2 分鐘才改算永久取不到。假的 E6 是 <see cref="FakeBlobEndpoint"/>
/// </summary>
public sealed class ReplicaBlobPrefetcherTests : IDisposable
{
    private readonly FakeRelayHandler relay = new();
    private readonly FakeBlobEndpoint blobs;
    private readonly AccessTokenHolder tokens = new();
    private readonly ReplicaBlobPrefetcher prefetcher;

    public ReplicaBlobPrefetcherTests()
    {
        blobs = new FakeBlobEndpoint(relay);
        tokens.Set("cha_unit", TimeProvider.System.GetTimestamp());
        var hub = new Uri("https://hub.example/");
        prefetcher = ReplicaBlobPrefetcher.Create(new DeviceHttpClient(hub, hub, false, tokens, _ => Task.FromResult(false), TimeProvider.System, relay), TimeProvider.System);
    }

    public void Dispose() => prefetcher.Dispose();

    private static byte[] Content(int seed, int length)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static ReplicaBlobWant Want(byte[] content) => new(Convert.ToHexStringLower(SHA256.HashData(content)), content.LongLength);

    private static HttpResponseMessage Ok(byte[] content, long? contentLength = null)
    {
        var body = new ByteArrayContent(content);
        if (contentLength is { } length)
            body.Headers.ContentLength = length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
    }

    // ── E6 ──

    [Fact]
    public async Task 取到_大小與SHA256都對()
    {
        var a = Content(1, 1000);
        var b = Content(2, 2000);
        blobs.Put(a);
        blobs.Put(b);

        var results = await prefetcher.FetchAsync([Want(a), Want(b), Want(a)], CancellationToken.None);

        blobs.Gets.Should().BeEquivalentTo(new[] { Want(a).Sha256, Want(b).Sha256 }, "同一個只取一次");
        results.Should().HaveCount(2);
        results[Want(a).Sha256].Should().BeEquivalentTo(ReplicaBlobFetch.Fetched(a));
        results[Want(b).Sha256].Outcome.Should().Be(ReplicaBlobOutcome.Fetched);
        relay.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get && r.Authorization == "Bearer cha_unit" && r.Path.StartsWith("/api/v1/device/blobs/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("404", "Missing")]
    [InlineData("length-header", "Missing")]
    [InlineData("tampered", "Missing")]
    [InlineData("extra", "Missing")]
    [InlineData("short", "Failed")]
    [InlineData("500", "Failed")]
    [InlineData("503", "Failed")]
    [InlineData("206", "Failed")]
    [InlineData("throw", "Failed")]
    public async Task 回應分類(string kind, string expected)
    {
        var content = Content(3, 1000);
        var tampered = (byte[])content.Clone();
        tampered[10] ^= 0xFF;
        blobs.GetResult = _ => kind switch
        {
            "404" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "length-header" => Ok(content, 999),
            "tampered" => Ok(tampered),
            "extra" => Ok([.. content, 0x00], content.LongLength),
            "short" => Ok(content[..500], content.LongLength),
            "500" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "503" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "206" => new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(content) },
            "throw" => throw new HttpRequestException("連不上"),
            _ => null,
        };

        var results = await prefetcher.FetchAsync([Want(content)], CancellationToken.None);

        results.Should().ContainSingle().Which.Value.Outcome.Should().Be(Enum.Parse<ReplicaBlobOutcome>(expected));
        results.Single().Value.Content.Should().BeNull();
    }

    [Theory]
    [InlineData("ABCDEF", 10)]
    [InlineData("valid", 0)]
    [InlineData("valid", (4L * 1024 * 1024) + 1)]
    public async Task 參照不合格_不送_永久取不到(string sha, long length)
    {
        var want = new ReplicaBlobWant(sha == "valid" ? new string('a', 64) : sha, length);

        (await prefetcher.FetchAsync([want], CancellationToken.None))[want.Sha256].Outcome.Should().Be(ReplicaBlobOutcome.Missing);
        relay.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task 沒有權杖_暫時取不到_不送()
    {
        tokens.Clear();
        var content = Content(4, 10);
        blobs.Put(content);

        (await prefetcher.FetchAsync([Want(content)], CancellationToken.None))[Want(content).Sha256].Outcome.Should().Be(ReplicaBlobOutcome.Failed);
        relay.Requests.Should().BeEmpty();
    }

    // ── 同時數與合計 ──

    [Fact]
    public async Task 同時最多4個()
    {
        var contents = Enumerable.Range(0, 10).Select(i => Content(100 + i, 1000)).ToList();
        foreach (var c in contents)
            blobs.Put(c);
        blobs.GetDelay = TimeSpan.FromMilliseconds(80);

        var results = await prefetcher.FetchAsync(contents.Select(Want).ToList(), CancellationToken.None);

        results.Values.Should().OnlyContain(r => r.Outcome == ReplicaBlobOutcome.Fetched).And.HaveCount(10);
        blobs.MaxConcurrentGets.Should().Be(ReplicaBlobRules.MaxConcurrent, "有排滿而且不超過");
    }

    [Fact]
    public async Task 預取_合計上限()
    {
        // 一頁 10 個 4 MB 的 blob：這一頁只取前 8 個（32 MB），第 9 筆起那個串流停住、下一頁再取
        const int fourMb = 4 * 1024 * 1024;
        var wants = Enumerable.Range(0, 10).Select(i => new ReplicaBlobWant(Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(i))), fourMb)).ToList();
        var events = wants.Select((w, i) =>
        {
            var payload = new JObject { ["id"] = "i" + i, ["ord"] = i + 1, ["output"] = new JObject { ["blob"] = w.Sha256, ["length"] = w.Length } };
            var (enc, text) = PayloadRules.Encode(payload.ToString(Formatting.None));
            const string id = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8";
            return new PulledEvent(i + 1, SyncStreams.Conversation(id), SyncClasses.Item, SyncIds.NewDeviceId(), ConversationEventKinds.Item, enc, text, 0, id, 1);
        }).ToList();
        var plan = ReplicaBlobRules.Plan(events, _ => true);

        var inFlight = 0;
        var maxInFlight = 0;
        long outstanding = 0;
        long maxOutstanding = 0;
        var gate = new object();
        using var counting = new ReplicaBlobPrefetcher(async (want, ct) =>
        {
            lock (gate)
            {
                maxInFlight = Math.Max(maxInFlight, ++inFlight);
                outstanding += want.Length;
                maxOutstanding = Math.Max(maxOutstanding, outstanding);
            }

            await Task.Delay(30, ct);
            lock (gate)
                inFlight--;
            return ReplicaBlobFetch.Missing;
        });

        var results = await counting.FetchAsync(plan.Wants, CancellationToken.None);

        plan.Wants.Should().HaveCount(8);
        results.Should().HaveCount(8);
        maxInFlight.Should().Be(ReplicaBlobRules.MaxConcurrent);
        maxOutstanding.Should().BeLessThanOrEqualTo(ReplicaBlobRules.MaxPageBytes, "這一頁取回的內容合計 ≤32 MB");
        plan.BudgetHolds.Values.Should().ContainSingle().Which.Seq.Should().Be(9);
    }

    // ── 暫時取不到的上限 ──

    [Fact]
    public async Task 暫時取不到_5次而且滿2分鐘才改算永久取不到_取到時重算()
    {
        var time = new ManualTimeProvider();
        var outcome = ReplicaBlobFetch.Failed;
        using var flaky = new ReplicaBlobPrefetcher((_, _) => Task.FromResult(outcome), time: time);
        var want = new ReplicaBlobWant(new string('b', 64), 10);

        async Task<ReplicaBlobOutcome> Once() => (await flaky.FetchAsync([want], CancellationToken.None))[want.Sha256].Outcome;

        for (var i = 0; i < 6; i++)
            (await Once()).Should().Be(ReplicaBlobOutcome.Failed, $"第 {i + 1} 次還不到 2 分鐘（通知一直來的串流不能幾秒就用完次數）");
        time.Advance(ReplicaBlobPrefetcher.MinTransientSpan);
        (await Once()).Should().Be(ReplicaBlobOutcome.Missing, "次數與時間都到了");
        (await Once()).Should().Be(ReplicaBlobOutcome.Failed, "改算永久之後重新計");

        // 滿 2 分鐘但次數不到 5 也不算
        time.Advance(TimeSpan.FromMinutes(10));
        (await Once()).Should().Be(ReplicaBlobOutcome.Failed);

        // 取到一次就清掉
        outcome = ReplicaBlobFetch.Fetched([1]);
        (await Once()).Should().Be(ReplicaBlobOutcome.Fetched);
        outcome = ReplicaBlobFetch.Failed;
        for (var i = 0; i < 4; i++)
            (await Once()).Should().Be(ReplicaBlobOutcome.Failed);
        time.Advance(TimeSpan.FromMinutes(10));
        (await Once()).Should().Be(ReplicaBlobOutcome.Missing, "取到之後重新算：第 5 次、滿 2 分鐘");
    }

    // ── 取消與停止 ──

    [Fact]
    public async Task 呼叫端取消_丟OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        using var slow = new ReplicaBlobPrefetcher(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ReplicaBlobFetch.Missing;
        });

        var pending = slow.FetchAsync([new ReplicaBlobWant(new string('c', 64), 1)], cts.Token);
        cts.Cancel();

        await FluentActions.Awaiting(() => pending).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task 停止時_沒取完的算暫時取不到_之後一律暫時取不到()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new ReplicaBlobPrefetcher(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ReplicaBlobFetch.Missing;
        });
        var wants = new[] { new ReplicaBlobWant(new string('d', 64), 1), new ReplicaBlobWant(new string('e', 64), 1) };

        var pending = slow.FetchAsync(wants, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        slow.Dispose();

        var results = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        results.Values.Should().HaveCount(2).And.OnlyContain(r => r.Outcome == ReplicaBlobOutcome.Failed);
        (await slow.FetchAsync(wants, CancellationToken.None)).Values.Should().OnlyContain(r => r.Outcome == ReplicaBlobOutcome.Failed);
    }
}
