using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Security.Cryptography;
using Myriagent.Media;
using Myriagent.Sync;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Media;

/// <summary>轉送為什麼沒有拿到裝置的回覆</summary>
internal enum RelayFailure
{
    /// <summary>拿到了（<see cref="RelayOutcome.Reply"/>；可能是裝置的拒絕）</summary>
    None,

    /// <summary>來源電腦不在線、沒有協商出 media1，或連線在回覆之前關閉（503）</summary>
    Offline,

    /// <summary>這台來源同時的轉送已滿，或所有來源合計的已滿，排隊等滿 <see cref="MediaRelay.QueueWait"/> 還是沒有名額（503＋Retry-After）</summary>
    Busy,

    /// <summary>首位元組逾時（504）</summary>
    Timeout,

    /// <summary>裝置的回覆不合格（502）</summary>
    BadReply,
}

/// <summary>
/// 一次轉送的結果：<see cref="Reply"/> 是驗過的裝置回覆，<see cref="Body"/> 是裝置 PUT 的本文（管線直通、有背壓；
/// 裝置提早結束時讀取丟例外）。用完一定要 <see cref="Dispose"/>：釋放名額、關閉管線（裝置還在送的話它的 PUT 收到 410）
/// </summary>
internal sealed class RelayOutcome(RelayFailure failure, MediaReply? reply, Stream? body, Action release) : IDisposable
{
    public RelayFailure Failure { get; } = failure;

    public MediaReply? Reply { get; } = reply;

    public Stream? Body { get; } = body;

    public void Dispose() => release();
}

/// <summary>
/// 媒體轉送（WP4.7 計畫 D11、D12、D27；設計 4.10:1047-1062；sync-protocol 14.3）：伺服器對來源電腦送 <c>MediaRequest</c>（H1），
/// 電腦以 <c>PUT /api/v1/device/relay/{relayId}</c>（E7）回覆，位元組經記憶體管線直接轉給瀏覽器（不落地）。
/// <list type="bullet">
/// <item>只送給這台最新、已證明、協商出 media1 的連線；沒有這樣的連線是 <see cref="RelayFailure.Offline"/>。</item>
/// <item>每台來源同時 ≤<see cref="MediaRelayRules.MaxConcurrentPerDevice"/> 個（名額依裝置計）、所有來源合計 ≤<see cref="MediaOptions.MaxConcurrentRelays"/> 個，
/// 再來的依到達的順序排隊、名額空出來就交給它（還沒送 <c>MediaRequest</c>），等滿 <see cref="QueueWait"/> 才是 <see cref="RelayFailure.Busy"/>。</item>
/// <item>首位元組（E7 到達）等 <see cref="MediaOptions.FirstByteTimeoutSeconds"/> 秒（注入的 <see cref="TimeProvider"/>），逾時算進 <see cref="Timeouts"/>。</item>
/// <item>每個 <c>relayId</c> 只收一次、只收發給這台的；瀏覽器已離開（中斷或逾時）的 PUT 回 410，名額在瀏覽器離開時就釋放。</item>
/// <item>記下每個轉送走的連線：那條連線關閉時，還在等回覆的轉送改成 Offline（另一條連線上的不受影響）；已經在傳的照它自己的 HTTP 連線走完。</item>
/// </list>
/// 不限流（擁有者指示：自用）；只有容量上限
/// </summary>
internal sealed class MediaRelay : IDisposable
{
    /// <summary>給 PUT 的本文的管線緩衝：超過就讓裝置等（瀏覽器讀得慢時的背壓）</summary>
    internal const long PauseWriterBytes = 1024 * 1024;

    private const long ResumeWriterBytes = 512 * 1024;

    /// <summary>
    /// 管線的區段大小，也是每圈最多從 PUT 的本文搬多少。寫進管線時不帶大小（<c>Write</c>＝<c>GetSpan()</c>），先填滿目前這段剩下的空間才配下一段，
    /// 暫停時一條管線佔住約 <see cref="PauseWriterBytes"/> 加最多兩段。不能帶大小（舊的 <c>GetMemory(64 KB)</c>）：目前這段剩下的不夠時每次都另配一段，
    /// Kestrel 給 PUT 本文的每次讀取常只有約 4 KB，暫停的管線就佔 1 MB ÷ 4 KB × 64 KB＝16 MB（WP4.9 步 12：一台 20 個轉送，受管理堆積 61 → 373 MB，
    /// 兩三台就 Out of memory）。64 KB 小於大型物件堆積的門檻，同 <see cref="MediaResponder"/> 給瀏覽器的複製緩衝
    /// </summary>
    internal const int SegmentBytes = 64 * 1024;

    /// <summary>
    /// 名額滿了排隊最多等多久（2026-10-28）：以前滿了直接回 503 加 Retry-After，但瀏覽器的 <c>&lt;img&gt;</c> 不照 Retry-After 重試，
    /// 圖多的對話一打開就是一張張破圖（一台同時 20 個；網頁的圖片有延遲載入，Chrome 照樣先載畫面上下一大段的圖，一晚上每小時約 20 張）
    /// </summary>
    internal static readonly TimeSpan QueueWait = TimeSpan.FromSeconds(10);

    private const int Waiting = 0;
    private const int Accepted = 1;
    private const int Gone = 2;

    private readonly ConnectionRegistry connections;
    private readonly IDeviceMessageSender sender;
    private readonly IOptions<HubOptions> options;
    private readonly TimeProvider time;
    private readonly ILogger<MediaRelay> log;
    private readonly ConcurrentDictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> perDevice = new(StringComparer.Ordinal);
    private readonly LinkedList<Waiter> waiters = new();
    private readonly Lock gate = new();
    private int active;
    private long timeouts;

    public MediaRelay(ConnectionRegistry connections, IDeviceMessageSender sender, IOptions<HubOptions> options, TimeProvider time, ILogger<MediaRelay> log)
    {
        this.connections = connections;
        this.sender = sender;
        this.options = options;
        this.time = time;
        this.log = log;
        connections.ConnectionRemoved += OnConnectionRemoved;
    }

    /// <summary>佔著名額的轉送數（等回覆或正在傳）</summary>
    public int Active => Volatile.Read(ref active);

    /// <summary>排隊等名額的轉送數</summary>
    public int Queued
    {
        get
        {
            lock (gate)
                return waiters.Count;
        }
    }

    /// <summary>首位元組逾時的累計次數（ops/status 的 <c>relayTimeouts</c>，步 5）</summary>
    public long Timeouts => Interlocked.Read(ref timeouts);

    /// <summary>
    /// 轉送管線的區段來源：並行的轉送之間重用、最後一個區段還回來就全部交給 GC（<see cref="RelaySegmentPool"/>；不用共用的 ArrayPool，
    /// 轉送都結束之後區段不會留在池裡，S9 閒置記憶體的修正，設計 14.20）
    /// </summary>
    internal RelaySegmentPool Segments { get; } = new();

    /// <summary>測試注入點：之後開始的轉送，管線向這個記憶體池要區段（null＝<see cref="Segments"/>），量暫停的管線佔住多少記憶體</summary>
    internal MemoryPool<byte>? PipePoolForTest { get; set; }

    /// <summary>
    /// 向 <paramref name="deviceId"/> 要 <paramref name="mediaRef"/> 的位元組，等它的 PUT 到達。<paramref name="maxBodyBytes"/> 是這個轉送的本文上限
    /// （原檔與檔案每段 8 MB、縮圖 512 KB）。<paramref name="cancellationToken"/> 是瀏覽器的請求：取消時丟 <see cref="OperationCanceledException"/>、立即釋放名額
    /// </summary>
    public async Task<RelayOutcome> StartAsync(string deviceId, string mediaRef, string purpose, MediaRange? range, long maxBodyBytes, CancellationToken cancellationToken)
    {
        SweepTombstones();
        if (ConnectionOf(deviceId) is null)
            return Failed(RelayFailure.Offline);
        if (!await AcquireAsync(deviceId, cancellationToken))
            return Failed(RelayFailure.Busy);

        // 排隊期間連線可能斷了或換了一條：拿到名額之後再找一次
        var connection = ConnectionOf(deviceId);
        if (connection is null)
        {
            ReturnSlot(deviceId);
            return Failed(RelayFailure.Offline);
        }

        var slot = new Slot(NewRelayId(), deviceId, connection.ConnectionId, range, maxBodyBytes, CreatePipe(PipePoolForTest ?? Segments));
        slots[slot.RelayId] = slot;
        try
        {
            await sender.SendAsync(connection.ConnectionId, DeviceHubMethods.MediaRequest, new MediaRequest(slot.RelayId, mediaRef, purpose, range), cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            log.LogDebug(ex, "MediaRequest 送不出去（連線已斷）：{DeviceId}", deviceId);
            MarkGone(slot);
            return Failed(RelayFailure.Offline);
        }
        catch (OperationCanceledException)
        {
            MarkGone(slot);
            throw;
        }

        SlotReply result;
        try
        {
            result = await slot.Reply.Task.WaitAsync(TimeSpan.FromSeconds(options.Value.Media.FirstByteTimeoutSeconds), time, cancellationToken);
        }
        catch (TimeoutException)
        {
            if (MarkGone(slot))
            {
                Interlocked.Increment(ref timeouts);
                log.LogInformation("媒體轉送逾時（來源電腦沒有回覆）：{DeviceId} {RelayId}", deviceId, slot.RelayId);
                return Failed(RelayFailure.Timeout);
            }

            // 逾時與 PUT 同時到：PUT 已經接下，回覆緊接著就會設定
            result = await slot.Reply.Task;
        }
        catch (OperationCanceledException)
        {
            if (!MarkGone(slot))
                Finish(slot);
            throw;
        }

        if (result.Failure != RelayFailure.None)
        {
            Finish(slot);
            return Failed(result.Failure);
        }

        return new RelayOutcome(RelayFailure.None, result.Reply, result.Reply!.HasBody ? slot.Pipe.Reader.AsStream() : null, () => Finish(slot));
    }

    /// <summary>這台最新、已證明、協商出 media1 的連線（沒有回 null）</summary>
    private DeviceConnection? ConnectionOf(string deviceId)
        => connections.ConnectionsOf(deviceId).Where(c => c.IsProven && c.Allows(DeviceHubMethods.MediaRequest)).MaxBy(c => c.Order);

    /// <summary>
    /// 拿一個名額：有空位就直接拿（排著的都是那台已滿或合計已滿的，輪不到它們），否則排到隊尾，名額空出來時依到達的順序交給還能用的
    /// （<see cref="ReturnSlot"/>），等滿 <see cref="QueueWait"/> 回 false。瀏覽器離開時退出隊伍、丟 <see cref="OperationCanceledException"/>
    /// </summary>
    private async Task<bool> AcquireAsync(string deviceId, CancellationToken cancellationToken)
    {
        Waiter waiter;
        lock (gate)
        {
            if (HasRoom(deviceId))
            {
                Take(deviceId);
                return true;
            }

            waiter = new Waiter(deviceId);
            waiter.Node = waiters.AddLast(waiter);
        }

        try
        {
            await waiter.Granted.Task.WaitAsync(QueueWait, time, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            bool stillQueued;
            lock (gate)
            {
                stillQueued = waiter.Node.List != null;
                if (stillQueued)
                    waiters.Remove(waiter.Node);
            }

            if (ex is TimeoutException)
                return !stillQueued;   // 逾時的那一刻剛好拿到名額：照用
            if (!stillQueued)
                ReturnSlot(deviceId);
            throw;
        }
    }

    private bool HasRoom(string deviceId)
        => perDevice.GetValueOrDefault(deviceId) < MediaRelayRules.MaxConcurrentPerDevice && active < options.Value.Media.MaxConcurrentRelays;

    private void Take(string deviceId)
    {
        perDevice[deviceId] = perDevice.GetValueOrDefault(deviceId) + 1;
        active++;
    }

    /// <summary>還一個名額，再依到達的順序交給排著的、那台與合計都還有空位的（在鎖外叫醒，接續不在鎖裡跑）</summary>
    private void ReturnSlot(string deviceId)
    {
        List<Waiter>? granted = null;
        lock (gate)
        {
            active--;
            var left = perDevice.GetValueOrDefault(deviceId) - 1;
            if (left > 0)
                perDevice[deviceId] = left;
            else
                perDevice.Remove(deviceId);

            for (var node = waiters.First; node != null && active < options.Value.Media.MaxConcurrentRelays;)
            {
                var next = node.Next;
                if (HasRoom(node.Value.DeviceId))
                {
                    Take(node.Value.DeviceId);
                    waiters.Remove(node);
                    (granted ??= []).Add(node.Value);
                }

                node = next;
            }
        }

        if (granted == null)
            return;
        foreach (var waiter in granted)
            waiter.Granted.TrySetResult();
    }

    /// <summary>
    /// E7：裝置回覆一個轉送（標頭照 <see cref="MediaReplyRules"/>，本文轉給等著的瀏覽器）。回 204（送完）、404（不認得、不是發給這台的、已經回覆過）、
    /// 410（瀏覽器已經離開）、400（回覆不合格、本文長度不符）、413（宣告的本文超過上限）
    /// </summary>
    public async Task<IResult> AcceptAsync(string deviceId, string relayId, HttpContext http)
    {
        if (!IsRelayId(relayId) || !slots.TryGetValue(relayId, out var slot) || !string.Equals(slot.DeviceId, deviceId, StringComparison.Ordinal))
            return MediaResults.Error(http, StatusCodes.Status404NotFound, MediaApiErrors.NotFound);
        if (Interlocked.CompareExchange(ref slot.State, Accepted, Waiting) != Waiting)
        {
            if (Volatile.Read(ref slot.State) == Gone && slots.TryRemove(new KeyValuePair<string, Slot>(relayId, slot)))
                return MediaResults.Error(http, StatusCodes.Status410Gone, MediaApiErrors.Gone);
            return MediaResults.Error(http, StatusCodes.Status404NotFound, MediaApiErrors.NotFound);
        }

        if (!RequestBodyLimits.TryAllow(http, slot.MaxBody))
        {
            log.LogWarning("裝置的轉送回覆超過上限：{DeviceId} {RelayId} {Length}", deviceId, relayId, http.Request.ContentLength);
            slot.Reply.TrySetResult(new SlotReply(null, RelayFailure.BadReply));
            return MediaResults.Error(http, StatusCodes.Status413PayloadTooLarge, MediaApiErrors.TooLarge);
        }

        var reply = MediaReplyRules.Parse(Headers(http.Request), slot.Range, slot.MaxBody);
        if (reply is null)
        {
            log.LogWarning("裝置的轉送回覆不合格：{DeviceId} {RelayId} X-Status={Status}", deviceId, relayId, http.Request.Headers[MediaRelayHeaders.Status].ToString());
            slot.Reply.TrySetResult(new SlotReply(null, RelayFailure.BadReply));
            return MediaResults.Error(http, StatusCodes.Status400BadRequest, MediaApiErrors.Invalid);
        }

        slot.Reply.TrySetResult(new SlotReply(reply, RelayFailure.None));
        if (!reply.HasBody)
            return Results.NoContent();
        return await PumpAsync(slot, reply, http);
    }

    /// <summary>這條連線關閉：還在等回覆的轉送改成 Offline（瀏覽器的 <c>/m/</c> 圖片可以退回縮圖）；已經在傳的不動</summary>
    public void CancelConnection(string connectionId)
    {
        foreach (var slot in slots.Values)
        {
            if (string.Equals(slot.ConnectionId, connectionId, StringComparison.Ordinal) && MarkGone(slot))
                slot.Reply.TrySetResult(new SlotReply(null, RelayFailure.Offline));
        }
    }

    public void Dispose() => connections.ConnectionRemoved -= OnConnectionRemoved;

    /// <summary>轉送 id：128 位元亂數的 Crockford Base32（26 字）</summary>
    internal static bool IsRelayId(string? value) => value is { Length: SyncIds.RandomChars } && CrockfordBase32.IsValid(value);

    private static string NewRelayId() => CrockfordBase32.Encode(RandomNumberGenerator.GetBytes(16));

    private static RelayOutcome Failed(RelayFailure failure) => new(failure, null, null, static () => { });

    /// <summary>一個轉送的管線：未讀 ≥<see cref="PauseWriterBytes"/> 時讓裝置等（背壓），區段固定 <see cref="SegmentBytes"/>、向 <paramref name="pool"/> 要</summary>
    private static Pipe CreatePipe(MemoryPool<byte> pool) =>
        new(new PipeOptions(pool, pauseWriterThreshold: PauseWriterBytes, resumeWriterThreshold: ResumeWriterBytes, minimumSegmentSize: SegmentBytes,
            useSynchronizationContext: false));

    private static MediaReplyHeaders Headers(HttpRequest request)
    {
        static string? Value(HttpRequest r, string name) => r.Headers.TryGetValue(name, out var v) && v.Count == 1 ? v.ToString() : null;
        return new MediaReplyHeaders(Value(request, MediaRelayHeaders.Status), Value(request, MediaRelayHeaders.TotalLength), Value(request, "Content-Range"),
            request.ContentLength, Value(request, "Content-Type"), Value(request, "ETag"), Value(request, MediaRelayHeaders.FileName),
            Value(request, MediaRelayHeaders.ContentKind), Value(request, MediaRelayHeaders.Error));
    }

    /// <summary>
    /// 把 PUT 的本文搬進管線：送完 204；瀏覽器不讀了（關閉管線）410；長度不符 400；裝置自己中斷時讓瀏覽器那邊讀取失敗。
    /// 直接讀 Kestrel 的 <c>BodyReader</c>，每圈最多搬 <see cref="SegmentBytes"/>、以不帶大小的 <c>Write</c> 接著目前這段寫（見 <see cref="SegmentBytes"/>）。
    /// 瀏覽器離開時以 <c>CancelPendingRead</c> 叫醒等著的讀取、<c>AdvanceTo</c> 之後回 410，不用取消權杖：Kestrel 的本文讀取被權杖取消之後
    /// 一直算「讀取中」，回應結束時排空本文失敗，記一行 Kestrel[67] 錯誤並關掉連線（WP4.9 步 12 的負載中止時好幾行；影片拖動時也會）
    /// </summary>
    private async Task<IResult> PumpAsync(Slot slot, MediaReply reply, HttpContext http)
    {
        var writer = slot.Pipe.Writer;
        var body = http.Request.BodyReader;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, slot.Abandoned.Token);
        using var abandoned = slot.Abandoned.Token.Register(static state => WakeReader((PipeReader)state!), body);
        long copied = 0;
        try
        {
            while (true)
            {
                var result = await body.ReadAsync(http.RequestAborted);
                var buffer = result.Buffer;
                if (result.IsCanceled)
                {
                    body.AdvanceTo(buffer.Start);
                    await writer.CompleteAsync(new IOException("瀏覽器已經離開"));
                    return MediaResults.Error(http, StatusCodes.Status410Gone, MediaApiErrors.Gone);
                }

                var chunk = buffer.Length > SegmentBytes ? buffer.Slice(0, SegmentBytes) : buffer;
                copied += chunk.Length;
                if (copied > reply.BodyLength)
                {
                    body.AdvanceTo(buffer.Start);
                    await writer.CompleteAsync(new InvalidDataException("裝置送的本文比宣告的長"));
                    return MediaResults.Error(http, StatusCodes.Status400BadRequest, MediaApiErrors.Invalid);
                }

                foreach (var segment in chunk)
                    writer.Write(segment.Span);
                body.AdvanceTo(chunk.End);
                var ended = result.IsCompleted && chunk.Length == buffer.Length;
                var flushed = await writer.FlushAsync(linked.Token);
                if (flushed.IsCompleted)
                {
                    await writer.CompleteAsync();
                    return MediaResults.Error(http, StatusCodes.Status410Gone, MediaApiErrors.Gone);
                }

                if (ended)
                    break;
            }

            if (copied != reply.BodyLength)
            {
                await writer.CompleteAsync(new InvalidDataException("裝置送的本文比宣告的短"));
                return MediaResults.Error(http, StatusCodes.Status400BadRequest, MediaApiErrors.Invalid);
            }

            await writer.CompleteAsync();
            return Results.NoContent();
        }
        catch (OperationCanceledException) when (slot.Abandoned.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
        {
            await writer.CompleteAsync(new IOException("瀏覽器已經離開"));
            return MediaResults.Error(http, StatusCodes.Status410Gone, MediaApiErrors.Gone);
        }
        catch (BadHttpRequestException ex)
        {
            await writer.CompleteAsync(new IOException("裝置的轉送本文不完整", ex));
            return MediaResults.Error(http, ex.StatusCode, MediaApiRules.BadRequestCode(ex.StatusCode));
        }
        catch (Exception ex) when (MediaResults.ClientGone(http, ex))
        {
            await writer.CompleteAsync(new IOException("裝置中斷了轉送", ex));
            // 連線已經斷了：中止它，Kestrel 才不會試著排空讀到一半的本文（重設的連線上排空失敗同樣記 Kestrel[67]）
            http.Abort();
            return Results.Empty;
        }
        catch (Exception ex)
        {
            await writer.CompleteAsync(ex);
            throw;
        }
    }

    /// <summary>瀏覽器離開：叫醒等著的本文讀取（回 IsCanceled）。請求已經結束或連線已中止時 Kestrel 的讀取端丟例外，那時本來就沒有在等的讀取</summary>
    private static void WakeReader(PipeReader body)
    {
        try
        {
            body.CancelPendingRead();
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException)
        {
        }
    }

    private void OnConnectionRemoved(DeviceConnection connection) => CancelConnection(connection.ConnectionId);

    /// <summary>
    /// 還在等回覆的轉送改成「瀏覽器已離開」：釋放名額、留著墓碑（之後到的 PUT 回 410）。已經被 PUT 接下的回 false（呼叫端照常用完再 <see cref="Finish"/>）
    /// </summary>
    private bool MarkGone(Slot slot)
    {
        if (Interlocked.CompareExchange(ref slot.State, Gone, Waiting) != Waiting)
            return false;
        slot.GoneAt = time.GetUtcNow().ToUnixTimeMilliseconds();
        Release(slot);
        slot.Abandoned.Cancel();
        return true;
    }

    /// <summary>轉送結束（瀏覽器用完或放棄）：釋放名額、拿掉登記、關閉管線的讀取端（裝置還在送就收到 410）</summary>
    private void Finish(Slot slot)
    {
        if (Interlocked.Exchange(ref slot.Finished, 1) == 1)
            return;
        Release(slot);
        slots.TryRemove(new KeyValuePair<string, Slot>(slot.RelayId, slot));
        slot.Pipe.Reader.Complete();
        slot.Abandoned.Cancel();
    }

    private void Release(Slot slot)
    {
        if (Interlocked.Exchange(ref slot.Held, 0) == 0)
            return;
        ReturnSlot(slot.DeviceId);
    }

    /// <summary>墓碑（瀏覽器已離開、PUT 還沒到）留首位元組逾時的 4 倍，之後到的 PUT 回 404</summary>
    private void SweepTombstones()
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var keep = options.Value.Media.FirstByteTimeoutSeconds * 4000L;
        foreach (var slot in slots.Values)
        {
            if (Volatile.Read(ref slot.State) == Gone && now - slot.GoneAt >= keep)
                slots.TryRemove(new KeyValuePair<string, Slot>(slot.RelayId, slot));
        }
    }

    private readonly record struct SlotReply(MediaReply? Reply, RelayFailure Failure);

    /// <summary>排隊等名額的一個轉送：<see cref="Node"/> 不在隊伍裡（List 是 null）＝已經拿到名額</summary>
    private sealed class Waiter
    {
        public Waiter(string deviceId) => DeviceId = deviceId;

        public string DeviceId { get; }

        public TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<Waiter> Node { get; set; } = null!;
    }

    private sealed class Slot(string relayId, string deviceId, string connectionId, MediaRange? range, long maxBody, Pipe pipe)
    {
        public int State = Waiting;

        public int Held = 1;

        public int Finished;

        public long GoneAt;

        public string RelayId { get; } = relayId;

        public string DeviceId { get; } = deviceId;

        public string ConnectionId { get; } = connectionId;

        public MediaRange? Range { get; } = range;

        public long MaxBody { get; } = maxBody;

        public TaskCompletionSource<SlotReply> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Pipe Pipe { get; } = pipe;

        public CancellationTokenSource Abandoned { get; } = new();
    }
}
