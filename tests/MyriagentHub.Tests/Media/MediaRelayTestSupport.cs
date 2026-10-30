using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Myriagent.Media;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Media;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Media;

/// <summary>假裝置手上的一個檔（以參照找到）</summary>
internal sealed record FakeMediaFile(byte[] Data, string? ContentType = "image/png", string? ETag = "\"e1\"", string? FileName = null, string? Kind = null);

/// <summary>假裝置要 PUT 的回覆（照 <see cref="MediaRelayHeaders"/>）；<see cref="Content"/> 給了就用它當本文（測試提早結束的本文）</summary>
internal sealed record FakeReply(int Status, long? Total = null, ByteRange? Range = null, byte[]? Body = null, string? ContentType = null, string? ETag = null,
    string? FileName = null, string? Kind = null, string? Error = null, HttpContent? Content = null)
{
    /// <summary>照協定從檔案算出回覆：不認得 404、沒帶範圍 200 整份、起點不在檔案裡 416、其他 206（結尾縮到檔尾）</summary>
    public static FakeReply For(FakeMediaFile? file, MediaRequest request)
    {
        if (file is null)
            return new FakeReply(404, Error: MediaErrorCodes.NotFound);
        var total = file.Data.LongLength;
        if (request.Range is null)
            return new FakeReply(200, total, null, file.Data, file.ContentType, file.ETag, file.FileName, file.Kind);
        if (request.Range.Start >= total)
            return new FakeReply(416, total, ContentType: file.ContentType);
        var end = Math.Min(request.Range.End ?? total - 1, total - 1);
        var slice = file.Data.AsSpan((int)request.Range.Start, (int)(end - request.Range.Start + 1)).ToArray();
        return new FakeReply(206, total, new ByteRange(request.Range.Start, end), slice, file.ContentType, file.ETag, file.FileName, file.Kind);
    }
}

/// <summary>
/// 假的來源電腦：經真的 Kestrel 連裝置 hub、Hello 宣告功能（預設 conv1＋media1）、Prove 拿存取權杖 <c>cha_</c>，
/// 收到的 <c>MediaRequest</c> 以 <c>PUT /api/v1/device/relay/{relayId}</c> 回覆。可以手動一則一則回（<see cref="NextRequestAsync"/>），
/// 或以 <see cref="Serve"/> 自動照 <see cref="Files"/> 回
/// </summary>
internal sealed class FakeMediaDevice : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly HttpClient http;
    private Task serving = Task.CompletedTask;

    private FakeMediaDevice(TestDevice device, DeviceHubClient client, string accessToken, HttpClient http, bool ownsDevice)
    {
        Device = device;
        Client = client;
        AccessToken = accessToken;
        this.http = http;
        OwnsDevice = ownsDevice;
    }

    public TestDevice Device { get; }

    public string Id => Device.Id;

    public DeviceHubClient Client { get; }

    public string AccessToken { get; }

    /// <summary>參照 → 檔案（<see cref="Serve"/> 預設的回覆照它）</summary>
    public ConcurrentDictionary<string, FakeMediaFile> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>自動回覆時收到的所有 MediaRequest（依到達順序）</summary>
    public ConcurrentQueue<MediaRequest> Seen { get; } = new();

    /// <summary>自動回覆時每個 PUT 的狀態碼</summary>
    public ConcurrentQueue<HttpStatusCode> Replies { get; } = new();

    /// <summary>自動回覆時處理者丟出的例外</summary>
    public ConcurrentQueue<Exception> Errors { get; } = new();

    private bool OwnsDevice { get; }

    /// <param name="device">同一台的第二條連線時傳既有的裝置（不傳＝新配對一台）</param>
    public static async Task<FakeMediaDevice> ConnectAsync(MediaHost host, TestDevice? device = null, IReadOnlyList<string>? features = null)
    {
        var owns = device is null;
        device ??= await TestDevice.AddAsync(host.Host);
        var (client, seen) = await DeviceHubClient.ConnectKestrelAsync(host.Host, device.Token);
        var (_, prove) = await client.HandshakeAsync(device, HandshakeRules.SpkiSha256(seen), features: features ?? [DeviceFeatureRules.Conv1, DeviceFeatureRules.Media1]);
        return new FakeMediaDevice(device, client, prove.AccessToken, host.Client(), owns);
    }

    /// <summary>下一則 MediaRequest（手動模式；其他訊息略過）</summary>
    public Task<MediaRequest> NextRequestAsync(int timeoutMs = 10_000) => Client.WaitForAsync<MediaRequest>(timeoutMs: timeoutMs);

    /// <summary>開始自動回覆：每則 MediaRequest 各自在背景以 <paramref name="handler"/>（預設照 <see cref="Files"/>）回</summary>
    public void Serve(Func<MediaRequest, Task>? handler = null)
    {
        handler ??= async request =>
        {
            using var response = await ReplyAsync(request);
            Replies.Enqueue(response.StatusCode);
        };
        serving = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in Client.Received.Reader.ReadAllAsync(stop.Token))
                {
                    if (message is not MediaRequest request)
                        continue;
                    Seen.Enqueue(request);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await handler(request);
                        }
                        catch (Exception ex)
                        {
                            Errors.Enqueue(ex);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>照 <see cref="Files"/> 回覆這則請求</summary>
    public Task<HttpResponseMessage> ReplyAsync(MediaRequest request) =>
        PutAsync(request.RelayId, FakeReply.For(Files.GetValueOrDefault(request.Ref), request));

    /// <summary>以這台的存取權杖 PUT 轉送的回覆</summary>
    public Task<HttpResponseMessage> PutAsync(string relayId, FakeReply reply, string? accessToken = null) => SendAsync(BuildPut(relayId, reply, accessToken));

    /// <summary>送自己組的請求（例如 <see cref="MediaHost.Unread"/> 包過的）</summary>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => SendOwnedAsync(request);

    /// <summary>轉送回覆的 PUT（標頭照 <see cref="MediaRelayHeaders"/>）</summary>
    public HttpRequestMessage BuildPut(string relayId, FakeReply reply, string? accessToken = null)
    {
        var content = reply.Content ?? new ByteArrayContent(reply.Body ?? []);
        var request = MediaHost.Device(HttpMethod.Put, "/api/v1/device/relay/" + relayId, accessToken ?? AccessToken, content);
        Add(request.Headers, MediaRelayHeaders.Status, reply.Status.ToString(CultureInfo.InvariantCulture));
        if (reply.Total is { } total)
            Add(request.Headers, MediaRelayHeaders.TotalLength, total.ToString(CultureInfo.InvariantCulture));
        if (reply.Range is { } range)
            content.Headers.ContentRange = new ContentRangeHeaderValue(range.Start, range.End, reply.Total!.Value);
        if (reply.ContentType is not null)
            content.Headers.TryAddWithoutValidation("Content-Type", reply.ContentType).Should().BeTrue();
        if (reply.ETag is not null)
            Add(request.Headers, "ETag", reply.ETag);
        if (reply.FileName is not null)
            Add(request.Headers, MediaRelayHeaders.FileName, MediaHeaderRules.EncodeFileName(reply.FileName));
        if (reply.Kind is not null)
            Add(request.Headers, MediaRelayHeaders.ContentKind, reply.Kind);
        if (reply.Error is not null)
            Add(request.Headers, MediaRelayHeaders.Error, reply.Error);
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await serving;
        await Client.DisposeAsync();
        http.Dispose();
        if (OwnsDevice)
            Device.Dispose();
    }

    private async Task<HttpResponseMessage> SendOwnedAsync(HttpRequestMessage request)
    {
        using (request)
            return await http.SendAsync(request);
    }

    private static void Add(HttpRequestHeaders headers, string name, string value)
    {
        if (!headers.TryAddWithoutValidation(name, value))
            throw new InvalidOperationException("標頭加不進請求：" + name);
    }
}

/// <summary>
/// 手動撥的計時器時鐘（轉送的首位元組逾時用；主機的 <see cref="ManualTimeProvider"/> 只撥牆上時鐘，計時器是真的時間）：
/// <see cref="Advance"/> 時觸發到期的計時器。只給 <c>MediaRelay</c> 用，不換掉主機的時鐘（其他背景工作的 Task.Delay 照真的時間）
/// </summary>
internal sealed class ManualTimerProvider : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<Timer> timers = [];
    private long ticks = new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

    public override long GetTimestamp() => Interlocked.Read(ref ticks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>還沒觸發、沒被釋放的計時器數</summary>
    public int Pending
    {
        get
        {
            lock (gate)
                return timers.Count;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<Timer> due;
        lock (gate)
        {
            Interlocked.Add(ref ticks, by.Ticks);
            var now = Interlocked.Read(ref ticks);
            due = timers.Where(t => t.DueAt <= now).ToList();
            foreach (var timer in due)
                timers.Remove(timer);
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class Timer(ManualTimerProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                owner.timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                    return true;
                DueAt = Interlocked.Read(ref owner.ticks) + dueTime.Ticks;
                owner.timers.Add(this);
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner.gate)
                owner.timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// 媒體測試的對話與參照：對話與項目經 EventLog 建立（同正式的收件與物化，索引器從事件建 <c>media_refs</c>）；
/// <see cref="RefAsync"/> 直接寫一列（轉送測試不必走項目），寫之前等索引器第一輪（重建的對帳會刪掉沒有項目的 <c>item:*</c> 列）做完
/// </summary>
internal static class MediaSeed
{
    private static readonly ConcurrentDictionary<string, long> DeviceSeqs = new(StringComparer.Ordinal);

    /// <summary>新的媒體參照（26 字 Crockford）</summary>
    public static string NewRef() => CrockfordBase32.Encode(RandomNumberGenerator.GetBytes(16));

    /// <summary>這台建立一個對話（預設完整同步）；回傳對話 id</summary>
    public static async Task<string> ConversationAsync(MediaHost host, string deviceId, string policy = ConversationSyncPolicies.Full)
    {
        var conversationId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await PublishAsync(host, deviceId, K.Created, conversationId, ConversationSamples.Meta(policy: policy));
        return conversationId;
    }

    /// <summary>改對話的同步政策（conv.meta）</summary>
    public static Task SetPolicyAsync(MediaHost host, string deviceId, string conversationId, string policy) =>
        PublishAsync(host, deviceId, K.Meta, conversationId, ConversationSamples.Meta(policy: policy));

    /// <summary>
    /// 一筆項目（conv.item）：<paramref name="attachmentRefs"/> 各成一個圖片附件、<paramref name="mediaRefs"/> 各成一個代理產出的圖片；
    /// <paramref name="edit"/> 可以再改 payload（例如把 attachments 換成 blob 參照）
    /// </summary>
    public static Task ItemAsync(MediaHost host, string deviceId, string conversationId, string itemId, IReadOnlyList<string>? attachmentRefs = null,
        IReadOnlyList<string>? mediaRefs = null, Action<JObject>? edit = null, long ver = 1)
    {
        var item = ConversationSamples.Item(id: itemId, ver: ver);
        if (attachmentRefs is not null)
            item["attachments"] = Attachments(attachmentRefs);
        if (mediaRefs is not null)
            item["media"] = new JArray(mediaRefs.Select((r, i) => new JObject { ["kind"] = "image", ["name"] = "out" + i.ToString(CultureInfo.InvariantCulture) + ".png", ["ref"] = r }));
        edit?.Invoke(item);
        return PublishAsync(host, deviceId, K.Item, conversationId, item);
    }

    /// <summary>附件陣列（圖片，參照照順序）</summary>
    public static JArray Attachments(IReadOnlyList<string> refs) => new(refs.Select((r, i) => new JObject
    {
        ["id"] = "a" + i.ToString(CultureInfo.InvariantCulture),
        ["name"] = "photo" + i.ToString(CultureInfo.InvariantCulture) + ".png",
        ["kind"] = "Image",
        ["size"] = 100,
        ["ref"] = r,
    }));

    /// <summary>很多筆項目（一批 50 筆送）</summary>
    public static async Task ItemsAsync(MediaHost host, string deviceId, string conversationId, IReadOnlyList<JObject> items)
    {
        foreach (var chunk in items.Chunk(50))
        {
            var events = chunk.Select(item => ConversationEventLogTests.Ev(DeviceSeqs.AddOrUpdate(deviceId, 1, (_, n) => n + 1), K.Item, item, conversationId)).ToArray();
            var outcome = await ConversationEventLogTests.Append(host.Host, deviceId, events);
            outcome.Ack.Results.Should().OnlyContain(r => r.Rejected == null, "項目應該被接受");
        }
    }

    /// <summary><c>media_refs</c> 的列（依裝置、參照排序）</summary>
    public static List<(string Device, string Ref, string Conversation, string Purpose, string? Thumb)> Rows(MediaHost host, string? conversationId = null)
    {
        using var connection = host.Services.GetRequiredService<SyncWriter>().OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT device_id, ref, conversation_id, purpose, thumb_blob FROM media_refs "
                          + (conversationId is null ? string.Empty : "WHERE conversation_id = $c ") + "ORDER BY device_id, ref";
        if (conversationId is not null)
            cmd.Parameters.AddWithValue("$c", conversationId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, string, string, string, string?)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return rows;
    }

    /// <summary><c>blob_refs</c> 的列（某一種引用）</summary>
    public static List<(string Sha, string RefId)> BlobRefs(MediaHost host, string refKind)
    {
        using var connection = host.Services.GetRequiredService<SyncWriter>().OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sha256, ref_id FROM blob_refs WHERE ref_kind = $k ORDER BY sha256, ref_id";
        cmd.Parameters.AddWithValue("$k", refKind);
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    /// <summary><c>blobs</c> 的一列的某個整數欄（沒有列回 null）</summary>
    public static long? BlobColumn(MediaHost host, string sha256, string column)
    {
        using var connection = host.Services.GetRequiredService<SyncWriter>().OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + column + " FROM blobs WHERE sha256 = $s";
        cmd.Parameters.AddWithValue("$s", sha256);
        return cmd.ExecuteScalar() is long value ? value : null;
    }

    /// <summary>存一份 blob（回傳 SHA-256）</summary>
    public static async Task<string> StoreBlobAsync(MediaHost host, byte[] content, string kind)
    {
        var sha = MediaBytes.Sha(content);
        using var stream = new MemoryStream(content);
        await host.Services.GetRequiredService<BlobStore>().PutAsync(sha, content.Length, kind, null, stream, CancellationToken.None);
        return sha;
    }

    /// <summary>E8：以存取權杖上傳縮圖（<paramref name="conversationId"/> 是 null 時不帶對話標頭）</summary>
    public static async Task<HttpResponseMessage> PutThumbAsync(MediaHost host, string accessToken, string mediaRef, string? conversationId, HttpContent content)
    {
        using var client = host.Client();
        using var request = MediaHost.Device(HttpMethod.Put, "/api/v1/device/thumbs/" + mediaRef, accessToken, content);
        if (conversationId is not null)
            request.Headers.TryAddWithoutValidation(MediaRelayHeaders.Conversation, conversationId).Should().BeTrue();
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    /// <summary>移除一筆項目（conv.itemRemoved）</summary>
    public static Task RemoveItemAsync(MediaHost host, string deviceId, string conversationId, string itemId) =>
        PublishAsync(host, deviceId, K.ItemRemoved, conversationId, ConversationEventLogTests.Removal(itemId));

    /// <summary>刪除對話（conv.deleted；localOnly 也是這個）</summary>
    public static Task DeleteAsync(MediaHost host, string deviceId, string conversationId) =>
        PublishAsync(host, deviceId, K.Deleted, conversationId, new JObject());

    /// <summary>等索引器追上目前最新的事件（上限 10 秒）</summary>
    public static async Task IndexedAsync(MediaHost host)
    {
        var indexer = host.Services.GetRequiredService<MediaRefIndexer>();
        await indexer.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        (await indexer.CatchUpAsync(TimeSpan.FromSeconds(10), CancellationToken.None)).Should().BeTrue("索引器應該追上最新的事件");
    }

    /// <summary>寫一列 <c>media_refs</c>（等索引器第一輪做完之後）；回傳網址的鍵 <c>{deviceId}-{ref}</c></summary>
    public static async Task<string> RefAsync(MediaHost host, string deviceId, string conversationId, string mediaRef, string purpose = "item:item1",
        string? thumbBlob = null, long? createdAt = null)
    {
        await host.Services.GetRequiredService<MediaRefIndexer>().Ready.WaitAsync(TimeSpan.FromSeconds(10));
        var at = createdAt ?? host.Host.Clock.NowMs;
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(c => c.Execute(
            "INSERT INTO media_refs(device_id, ref, conversation_id, purpose, thumb_blob, created_at, last_used_at) VALUES($d, $r, $c, $p, $t, $at, $at)",
            ("$d", deviceId), ("$r", mediaRef), ("$c", conversationId), ("$p", purpose), ("$t", thumbBlob), ("$at", at)));
        return MediaKeyRules.Format(deviceId, mediaRef);
    }

    private static async Task PublishAsync(MediaHost host, string deviceId, string kind, string conversationId, JObject payload)
    {
        var seq = DeviceSeqs.AddOrUpdate(deviceId, 1, (_, n) => n + 1);
        var outcome = await ConversationEventLogTests.Append(host.Host, deviceId, ConversationEventLogTests.Ev(seq, kind, payload, conversationId));
        outcome.Ack.Results.Should().OnlyContain(r => r.Rejected == null, $"{kind} 應該被接受");
    }
}

/// <summary>先給 <paramref name="prefix"/>，之後讀取丟 <see cref="IOException"/>（裝置的本文提早結束）</summary>
internal sealed class FailingStream(byte[] prefix) : Stream
{
    private int position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (position >= prefix.Length)
            throw new IOException("假裝置的本文中斷");
        var n = Math.Min(buffer.Length, prefix.Length - position);
        prefix.AsSpan(position, n).CopyTo(buffer.Span);
        position += n;
        return ValueTask.FromResult(n);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// 裝置 PUT 的本文，每次讀取最多給 <paramref name="chunk"/> 個位元組（Kestrel 給 PUT 本文的每次讀取常只有約 4 KB，WP4.9 步 12）；
/// <see cref="Delivered"/> 是已經給出的位元組數（看管線有沒有讓裝置等）
/// </summary>
internal sealed class TrickleStream(byte[] data, int chunk) : Stream
{
    private long position;

    public long Delivered => Interlocked.Read(ref position);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var at = (int)Interlocked.Read(ref position);
        var n = Math.Min(Math.Min(buffer.Length, chunk), data.Length - at);
        data.AsSpan(at, n).CopyTo(buffer.Span);
        Interlocked.Add(ref position, n);
        return ValueTask.FromResult(n);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>裝置 PUT 的本文：先送前 <paramref name="first"/> 個位元組，等 <paramref name="gate"/> 完成才送其餘的（讓伺服器在讀取途中等著）</summary>
internal sealed class GatedContent(byte[] data, int first, Task gate) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        await stream.WriteAsync(data.AsMemory(0, first));
        await stream.FlushAsync();
        await gate;
        await stream.WriteAsync(data.AsMemory(first));
    }

    protected override bool TryComputeLength(out long length)
    {
        length = data.Length;
        return true;
    }
}

/// <summary>
/// 記帳的記憶體池（大小照 <c>ArrayPool&lt;byte&gt;.Shared</c> 進位到 2 的次方）：<see cref="Outstanding"/> 是借出還沒還的位元組，
/// <see cref="Peak"/> 是它的最大值。給 <c>MediaRelay.PipePoolForTest</c>，量一條轉送的管線佔住多少記憶體
/// </summary>
internal sealed class CountingMemoryPool : MemoryPool<byte>
{
    private long outstanding;
    private long peak;

    public long Outstanding => Interlocked.Read(ref outstanding);

    public long Peak => Interlocked.Read(ref peak);

    public override int MaxBufferSize => 1024 * 1024;

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        var size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(minBufferSize <= 0 ? 4096 : minBufferSize));
        var now = Interlocked.Add(ref outstanding, size);
        long seen;
        while (now > (seen = Interlocked.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
        {
        }

        return new Owner(this, new byte[size]);
    }

    protected override void Dispose(bool disposing)
    {
    }

    private sealed class Owner(CountingMemoryPool pool, byte[] array) : IMemoryOwner<byte>
    {
        private byte[]? data = array;

        public Memory<byte> Memory => data ?? throw new ObjectDisposedException(nameof(Owner));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref data, null) is { } returned)
                Interlocked.Add(ref pool.outstanding, -returned.Length);
        }
    }
}

/// <summary>瀏覽器那一邊的小工具</summary>
internal static class MediaBrowser
{
    public static HttpRequestMessage Get(string path, string? range = null, string? fetchDest = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (range is not null)
            request.Headers.TryAddWithoutValidation("Range", range).Should().BeTrue();
        if (fetchDest is not null)
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", fetchDest).Should().BeTrue();
        return request;
    }

    /// <summary>讀完本文；連線被中止（伺服器 Abort、長度不符）回 null</summary>
    public static async Task<byte[]?> ReadOrAbortedAsync(HttpClient client, HttpRequestMessage request)
    {
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return null;
        }
    }

    /// <summary>回應標頭的原始值（不經 .NET 的解析重排；回應標頭與內容標頭都找）；沒有是空字串</summary>
    public static string Header(HttpResponseMessage response, string name) =>
        response.Headers.NonValidated.TryGetValues(name, out var values) ? string.Join(",", values)
        : response.Content.Headers.NonValidated.TryGetValues(name, out var content) ? string.Join(",", content) : string.Empty;

    /// <summary>
    /// 輪詢直到條件成立（寬鬆上限 30 秒，看經過的時間不數次數）：GitHub 的 runner 跑這個專案比開發機慢十倍以上，
    /// 10 秒內裝置的 8 MB PUT 有時還沒結束；條件成立就立刻返回，上限只在真的失敗時才等滿
    /// </summary>
    public static async Task EventuallyAsync(Func<bool> condition, string because)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(50);
        condition().Should().BeTrue(because);
    }
}
