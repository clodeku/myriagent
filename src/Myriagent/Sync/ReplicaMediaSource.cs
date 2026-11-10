using System.Globalization;
using System.Net;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Media;

namespace Myriagent.Sync;

/// <summary>
/// 複本的媒體來源（WP4.7 計畫步 12，D38；<see cref="IReplicaMediaSource"/> 的實作）：以 E9 <c>GET /api/v1/device/media/{key}?purpose=media|thumb[&amp;s=]</c>
/// （存取權杖，自己的 <see cref="DeviceHttpClient"/>）向伺服器要擁有者電腦上的圖片、影片與縮圖，內容整份緩衝在記憶體才交給 WebView2
/// （WebView2 在哪條執行緒讀串流沒驗過，網路串流可能凍結視窗）。
/// <list type="bullet">
/// <item>原檔一次最多 8 MB：網頁的 Range 縮到 8 MB 之內（<see cref="ReplicaMediaRules.RequestRange"/>），沒帶 Range 時要前 8 MB、正好是整份時改回 200；
/// 伺服器回的比 8 MB 多時只留前 8 MB、回 206 與實際的 Content-Range。</item>
/// <item>縮圖只收 PNG、JPEG、≤512 KB；收到的放進記憶體快取 5 分鐘（<see cref="ReplicaThumbCache"/>），重畫、切換分頁不再重抓。</item>
/// <item>逾時 <see cref="FetchTimeout"/>（20 秒）；鍵不合格、沒有權杖、伺服器拒絕、內容長度不符、連不上一律回 null（網頁 404）。</item>
/// </list>
/// 引擎在第一次協商出 media1 時建立、掛到對話的盒子上（<see cref="ConversationChangeBox.ReplicaMedia"/>），停止時 Dispose。任何執行緒都可以呼叫
/// </summary>
internal sealed class ReplicaMediaSource : IReplicaMediaSource, IPeerUploader, IPeerFileSource, IDisposable
{
    /// <summary>上傳一個檔最多等多久（512 MB 在慢速上傳也要送得完）</summary>
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(60);

    /// <summary>上傳回應的本文最多讀多少（JSON 只有 id、雜湊與大小）</summary>
    private const int MaxUploadReplyBytes = 64 * 1024;

    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    private const string OctetStream = "application/octet-stream";

    private const int CopyBufferBytes = 64 * 1024;

    private readonly DeviceHttpClient http;
    private readonly TimeProvider time;
    private readonly ReplicaThumbCache thumbs;
    private readonly CancellationTokenSource stopping = new();
    private int disposed;

    /// <param name="http">自己的 HTTP 用戶端（Dispose 時一起關）</param>
    public ReplicaMediaSource(DeviceHttpClient http, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(time);
        this.http = http;
        this.time = time;
        thumbs = new ReplicaThumbCache(time);
    }

    /// <summary>測試：縮圖快取</summary>
    internal ReplicaThumbCache Thumbs => thumbs;

    public async Task<ReplicaMediaReply?> FetchAsync(string key, string purpose, int? size, string? rangeHeader, CancellationToken ct)
    {
        if (Volatile.Read(ref disposed) != 0 || ReplicaMediaRules.RequestPath(key, purpose, size) is not { } path)
            return null;
        var isMedia = purpose == MediaPurposes.Media;
        if (!isMedia && thumbs.TryGet(path, out var cached, out var cachedType))
            return new ReplicaMediaReply(200, cachedType, cached, null);
        var (range, whole) = isMedia ? ReplicaMediaRules.RequestRange(rangeHeader) : (null, false);

        using var timeout = new CancellationTokenSource(FetchTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, stopping.Token);
        var token = linked.Token;
        try
        {
            using var response = await http.SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, path);
                if (range is not null)
                    request.Headers.TryAddWithoutValidation("Range", range);
                return request;
            }, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response is null)
                return null;
            if (isMedia)
                return await ReadMediaAsync(response, whole, token).ConfigureAwait(false);
            var thumb = await ReadThumbAsync(response, token).ConfigureAwait(false);
            if (thumb is not null)
                thumbs.Put(path, thumb.Body, thumb.ContentType);
            return thumb;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (timeout.IsCancellationRequested)
                Log.Info($"取複本的媒體逾時（{FetchTimeout.TotalSeconds:0} 秒）：{key}");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            Log.Info($"取複本的媒體失敗：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 桌面遠端操作的上傳（<see cref="IPeerUploader"/>）：PUT <c>/api/v1/device/uploads/{uploadId}</c>，本文是檔案的位元組（401 換權杖重送時重新開檔）；
    /// 回伺服器的狀態碼與本文
    /// </summary>
    public Task<PeerUploadResult> UploadAsync(string uploadId, string path, CancellationToken ct) => UploadAsync(uploadId, path, null, ct);

    public async Task<PeerUploadResult> UploadAsync(string uploadId, string path, Action<long, long>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (Volatile.Read(ref disposed) != 0 || !Media.UploadIdRules.IsValid(uploadId))
            return new PeerUploadResult(0, null);
        using var timeout = new CancellationTokenSource(UploadTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, stopping.Token);
        using var transfer = FileTransferActivity.Begin();
        var opened = new List<FileStream>();
        try
        {
            using var response = await http.SendAsync(() =>
            {
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, CopyBufferBytes, useAsync: true);
                opened.Add(file);
                var content = new StreamContent(progress is null ? file : new ProgressReadStream(file, progress), CopyBufferBytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(OctetStream);
                content.Headers.ContentLength = file.Length;
                return new HttpRequestMessage(HttpMethod.Put, ReplicaMediaRules.DeviceUploadPrefix + Uri.EscapeDataString(uploadId)) { Content = content };
            }, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (response is null)
                return new PeerUploadResult(0, null);
            var (body, _) = await ReadUpToAsync(response, MaxUploadReplyBytes, linked.Token).ConfigureAwait(false);
            return new PeerUploadResult((int)response.StatusCode, System.Text.Encoding.UTF8.GetString(body));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Info($"上傳到伺服器逾時或停止：{uploadId}");
            return new PeerUploadResult(0, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Info($"上傳到伺服器失敗：{ex.Message}");
            return new PeerUploadResult(0, null);
        }
        finally
        {
            foreach (var file in opened)
                await file.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 桌面遠端操作的 fileFetch（<see cref="IPeerFileSource"/>）：E11 整份（不帶 Range；伺服器以多段接成一個 200），≤<see cref="PeerFileRules.MaxBytes"/>，
    /// 逾時 <see cref="UploadTimeout"/> 的十分之一（6 分鐘）
    /// </summary>
    public async Task<PeerFile?> FetchFileAsync(string key, CancellationToken ct)
    {
        if (Volatile.Read(ref disposed) != 0 || !MediaKeyRules.TryParse(key, out _, out _))
            return null;
        using var timeout = new CancellationTokenSource(UploadTimeout / 10, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, stopping.Token);
        try
        {
            using var response = await http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ReplicaMediaRules.DeviceMediaPrefix + key + "?purpose=" + MediaPurposes.File),
                HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (response is null || response.StatusCode != HttpStatusCode.OK)
                return null;
            var (body, more) = await ReadUpToAsync(response, PeerFileRules.MaxBytes, linked.Token).ConfigureAwait(false);
            if (more || response.Content.Headers.ContentLength is { } declared && declared != body.LongLength)
                return null;
            var name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
            return new PeerFile(body, name);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Info($"取其他電腦的檔案逾時或停止：{key}");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            Log.Info($"取其他電腦的檔案失敗：{ex.Message}");
            return null;
        }
    }

    /// <summary>分段下載的每一段最多等多久（8 MB 在慢速網路也要送得完）</summary>
    public static readonly TimeSpan SegmentTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 電腦狀態面板的檔案傳輸（<see cref="IPeerFileSource.DownloadAsync"/>）：E11 每段 ≤8 MB 的 Range，依序寫進 <paramref name="target"/>；
    /// 每段核對起點、總長與 ETag 都和第一段相同（檔案在途中被改就停）。暫時的失敗（連線中斷、本文提早結束、逾時、502／503／504、
    /// 沒有權杖）從斷掉的位置接著試，連續失敗超過 <see cref="PeerDownloadRetryRules.FailBudget"/> 才放棄（2026-11-10：來源電腦在下載途中換版重開）
    /// </summary>
    public async Task<PeerDownload?> DownloadAsync(string key, Stream target, Action<long, long>? progress, CancellationToken ct, PeerDownloadCursor? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Volatile.Read(ref disposed) != 0 || !MediaKeyRules.TryParse(key, out _, out _))
            return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
        using var transfer = FileTransferActivity.Begin();
        var path = ReplicaMediaRules.DeviceMediaPrefix + key + "?purpose=" + MediaPurposes.File;
        // 接著暫停的位置（cursor）：起點、總長、ETag、檔名都沿用上次的，第一段照樣核對（檔案在暫停期間變了就停）
        long offset = cursor?.Offset ?? 0;
        long? total = cursor?.Total;
        string? name = cursor?.FileName;
        string? etag = cursor?.ETag;
        var buffer = new byte[CopyBufferBytes];
        var failures = 0;
        long? failingSince = null;
        try
        {
            while (total is null || offset < total)
            {
                var end = offset + MediaRelayRules.MaxSegmentBytes - 1;
                if (total is { } known)
                    end = Math.Min(end, known - 1);
                var attemptFrom = offset;
                string? transient = null;
                try
                {
                    using var timeout = new CancellationTokenSource(SegmentTimeout, time);
                    using var segment = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, timeout.Token);
                    var from = offset;
                    var to = end;
                    using var response = await http.SendAsync(() =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, path);
                        request.Headers.TryAddWithoutValidation("Range", "bytes=" + from.ToString(CultureInfo.InvariantCulture) + "-" + to.ToString(CultureInfo.InvariantCulture));
                        return request;
                    }, HttpCompletionOption.ResponseHeadersRead, segment.Token).ConfigureAwait(false);
                    if (response is null)
                        transient = "這台目前沒有連上伺服器";
                    else if (PeerDownloadRetryRules.IsTransientStatus(response.StatusCode))
                        transient = "HTTP " + (int)response.StatusCode;
                    else if (await ReadSegmentAsync(response) is { } stop)
                    {
                        if (stop == SegmentStop.Fatal)
                            return null;
                        if (stop == SegmentStop.Empty)
                            break;
                        transient = "本文提早結束";
                    }
                }
                catch (OperationCanceledException) when (!linked.IsCancellationRequested)
                {
                    transient = "逾時";
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    transient = ex.Message;
                }

                if (offset > attemptFrom)
                {
                    progress?.Invoke(offset, total ?? offset);
                    failures = 0;
                    failingSince = null;
                }
                if (transient is null)
                    continue;

                failingSince ??= time.GetTimestamp();
                if (PeerDownloadRetryRules.NextDelay(failures, time.GetElapsedTime(failingSince.Value)) is not { } delay)
                {
                    Log.Info($"分段下載其他電腦的檔案失敗（{transient}；連續 {failures + 1} 次、已到 {offset} 位元組）：{key}");
                    return null;
                }
                Log.Info($"分段下載其他電腦的檔案暫時失敗（{transient}），{delay.TotalSeconds:0} 秒後從 {offset} 位元組接著下載：{key}");
                failures++;
                await Task.Delay(delay, time, linked.Token).ConfigureAwait(false);
            }

            await target.FlushAsync(linked.Token).ConfigureAwait(false);
            return new PeerDownload(name, total ?? 0);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Info($"分段下載其他電腦的檔案逾時或停止：{key}");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            Log.Info($"分段下載其他電腦的檔案失敗：{ex.Message}");
            return null;
        }
        finally
        {
            // 暫停（取消）、失敗、完成都寫回到哪裡了：繼續時從這裡接著要
            if (cursor is not null)
            {
                cursor.Offset = offset;
                cursor.Total = total;
                cursor.ETag = etag;
                cursor.FileName = name;
            }
        }

        // 一段的回應：核對範圍與 ETag、把本文寫進 target（每讀到一塊就把 offset 往前推：中途斷了，下一次從這裡接著要）。
        // 寫完整段回 null；空的檔回 Empty；範圍、總長或 ETag 對不上回 Fatal；本文提早結束回 Short
        async Task<SegmentStop?> ReadSegmentAsync(HttpResponseMessage response)
        {
            long length;
            switch (response.StatusCode)
            {
                case HttpStatusCode.PartialContent when response.Content.Headers.ContentRange is { From: { } start, To: { } stop, Length: { } size }
                                                       && start == offset && stop >= start && size > 0:
                    if (total is { } t && t != size)
                        return SegmentStop.Fatal;
                    total = size;
                    length = stop - start + 1;
                    break;
                case HttpStatusCode.OK when offset == 0 && response.Content.Headers.ContentLength is { } whole:
                    total = whole;
                    length = whole;
                    break;
                // 空的檔：伺服器對 (0, …) 回 416 bytes */0
                case HttpStatusCode.RequestedRangeNotSatisfiable when offset == 0 && UnsatisfiedLength(ContentRangeOf(response)) == 0:
                    total = 0;
                    length = 0;
                    break;
                default:
                    Log.Info($"分段下載其他電腦的檔案被拒（{(int)response.StatusCode}）：{key}");
                    return SegmentStop.Fatal;
            }

            var tag = response.Headers.ETag?.Tag;
            if (etag is null)
                etag = tag;
            else if (!string.Equals(etag, tag, StringComparison.Ordinal))
                return SegmentStop.Fatal;
            name ??= response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
            if (length == 0)
                return SegmentStop.Empty;

            await using var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            long got = 0;
            while (got < length)
            {
                var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - got)), linked.Token).ConfigureAwait(false);
                if (read == 0)
                    return SegmentStop.Short;
                await target.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                got += read;
                offset += read;
            }

            return null;
        }
    }

    /// <summary>分段下載的一段沒有寫完整段的原因</summary>
    private enum SegmentStop
    {
        /// <summary>空的檔（沒有本文，下載完成）</summary>
        Empty,

        /// <summary>範圍、總長或 ETag 對不上、被拒：放棄</summary>
        Fatal,

        /// <summary>本文提早結束：從斷掉的位置接著試</summary>
        Short,
    }

    /// <summary>上傳的本文：讀的時候回報已送出的位元組（電腦狀態面板的檔案傳輸）</summary>
    private sealed class ProgressReadStream(FileStream inner, Action<long, long> progress) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Report(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Report(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Report(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

        private int Report(int read)
        {
            progress(inner.Position, inner.Length);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        thumbs.Clear();
        try
        {
            stopping.Cancel();
        }
        catch (AggregateException ex)
        {
            Log.Warn($"取消複本的媒體請求時出錯：{ex.Message}");
        }

        http.Dispose();
    }

    /// <summary>原檔：200、206、416 才收（其他回 null）</summary>
    private static async Task<ReplicaMediaReply?> ReadMediaAsync(HttpResponseMessage response, bool whole, CancellationToken ct)
    {
        var contentType = MediaHeaderRules.SafeMediaContentType(response.Content.Headers.ContentType?.ToString()) ?? OctetStream;
        var max = ReplicaMediaRules.MaxMediaBytes;
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
            {
                var declared = response.Content.Headers.ContentLength;
                var (body, more) = await ReadUpToAsync(response, max, ct).ConfigureAwait(false);
                if (!more)
                    return declared is null || declared == body.LongLength ? new ReplicaMediaReply(200, contentType, body, null) : null;
                // 伺服器回整份而且超過 8 MB：只留前 8 MB（總長要知道才組得出 Content-Range）
                if (declared is not { } total || total <= max)
                    return null;
                return new ReplicaMediaReply(206, contentType, body, HttpRangeRules.ContentRange(new ByteRange(0, max - 1), total));
            }

            case HttpStatusCode.PartialContent:
            {
                if (!HttpRangeRules.TryParseContentRange(ContentRangeOf(response), out var range, out var total))
                    return null;
                var expected = range.Length;
                if (response.Content.Headers.ContentLength is { } declared && declared != expected)
                    return null;
                var keep = Math.Min(expected, max);
                var (body, more) = await ReadUpToAsync(response, keep, ct).ConfigureAwait(false);
                if (body.LongLength != keep || (more && keep == expected))
                    return null;
                var kept = new ByteRange(range.Start, range.Start + keep - 1);
                if (whole && kept.Start == 0 && kept.End == total - 1)
                    return new ReplicaMediaReply(200, contentType, body, null);
                return new ReplicaMediaReply(206, contentType, body, HttpRangeRules.ContentRange(kept, total));
            }

            case HttpStatusCode.RequestedRangeNotSatisfiable:
                return UnsatisfiedLength(ContentRangeOf(response)) is { } length
                    ? new ReplicaMediaReply(416, contentType, [], HttpRangeRules.Unsatisfied(length))
                    : null;
            default:
                return null;
        }
    }

    /// <summary>縮圖：200、PNG 或 JPEG、≤512 KB 才收</summary>
    private static async Task<ReplicaMediaReply?> ReadThumbAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.OK)
            return null;
        var contentType = MediaHeaderRules.SafeMediaContentType(response.Content.Headers.ContentType?.ToString());
        if (contentType is not ("image/png" or "image/jpeg"))
            return null;
        var declared = response.Content.Headers.ContentLength;
        if (declared > ReplicaMediaRules.MaxThumbBytes)
            return null;
        var (body, more) = await ReadUpToAsync(response, ReplicaMediaRules.MaxThumbBytes, ct).ConfigureAwait(false);
        if (more || (declared is { } length && length != body.LongLength))
            return null;
        return new ReplicaMediaReply(200, contentType, body, null);
    }

    /// <summary>最多讀 <paramref name="max"/> 個位元組；<c>More</c>＝後面還有（多讀 1 個位元組看得出來）</summary>
    private static async Task<(byte[] Body, bool More)> ReadUpToAsync(HttpResponseMessage response, long max, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[CopyBufferBytes];
        while (buffer.Length < max)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, max - buffer.Length)), ct).ConfigureAwait(false);
            if (read == 0)
                return (buffer.ToArray(), false);
            buffer.Write(chunk, 0, read);
        }

        var more = await stream.ReadAsync(chunk.AsMemory(0, 1), ct).ConfigureAwait(false) > 0;
        return (buffer.ToArray(), more);
    }

    private static string? ContentRangeOf(HttpResponseMessage response)
        => response.Content.Headers.TryGetValues("Content-Range", out var values) ? string.Join(",", values) : null;

    /// <summary>416 的 <c>Content-Range: bytes */N</c> 的 N（只收十進位）</summary>
    private static long? UnsatisfiedLength(string? header)
    {
        const string prefix = "bytes */";
        if (header is null || !header.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return long.TryParse(header.Trim()[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var length) ? length : null;
    }
}
