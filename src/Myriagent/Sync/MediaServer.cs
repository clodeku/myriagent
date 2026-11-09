using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Files;
using Myriagent.Hub;
using Myriagent.Media;
using Myriagent.Remote;

namespace Myriagent.Sync;

/// <summary>一個轉送（引擎執行緒組好，之後在背景用）</summary>
/// <param name="Generation">收到請求的連線世代（<see cref="MediaServer.CancelConnection"/> 依它取消）</param>
/// <param name="Row"><c>media_refs_local</c> 的列；null＝不認得的參照</param>
/// <param name="Policy">這條連線 Hello 帶的遠端政策（暫停時是 Off）</param>
/// <param name="DeviceId">這台的裝置 id（對話的擁有者要是它）</param>
internal sealed record MediaJob(int Generation, MediaRequest Request, LocalMediaRefRow? Row, DevicePolicy Policy, string DeviceId);

/// <summary>
/// 桌面回覆媒體轉送（WP4.7 計畫 D26、D28、D36）：伺服器以 <c>MediaRequest</c> 要位元組，這裡以 <c>PUT /api/v1/device/relay/{relayId}</c>（E7）回。
/// <list type="number">
/// <item>引擎執行緒（<see cref="HandleAsync"/>）：同時超過 20 個的回 503（不佔名額），其他佔一個名額，之後全部在背景（不卡引擎執行緒）。</item>
/// <item>背景：參照不明或用途不符 404 → 經 RunOnUiAsync 取中樞的快照，對話不可見 404 → 解析路徑（<see cref="LocalMediaResolver"/>：附件由 <c>attachment_id</c>；
/// 項目沒載入時唯讀讀 items.json）→ 遠端政策（Q1-A）→ Codex 產生的圖片換成複本（Q2-A）→ <see cref="RemoteFileGuard.OpenAsync"/>
/// → 依用途回原檔的一段、縮圖（自己的 <see cref="ThumbnailService"/>，320 px PNG）
/// 或檔案（開頭就證明是二進位的依範圍串流；其他整份讀進記憶體、文字跑 DLP，命中 403 <c>dlp</c>）。</item>
/// </list>
/// 連線丟掉時 <see cref="CancelConnection"/> 取消那條連線上的轉送（另一條連線上的不受影響）；所有例外都在這裡記錄、不往外丟
/// </summary>
internal sealed class MediaServer : IDisposable
{
    private const int CopyBufferBytes = 64 * 1024;

    private readonly DeviceHttpClient http;
    private readonly LocalMediaResolver resolver;
    private readonly RemoteFileGuard guard;
    private readonly Func<string, int, CancellationToken, Task<byte[]?>> thumbnail;
    private readonly ConcurrentDictionary<long, Relay> relays = new();
    private readonly SemaphoreSlim fileGate = new(1, 1);
    private readonly object thumbnailGate = new();
    private ThumbnailService? thumbnails;
    private long nextRelay;
    private int active;
    private int disposed;

    private sealed record Relay(int Generation, CancellationTokenSource Cancel);

    /// <summary>要回的東西；<see cref="Body"/> 每次呼叫都產生新的本文（401 重送時再呼叫一次）</summary>
    private sealed record Reply(int Status, string? Error = null, long? Total = null, ByteRange? Range = null, string? ContentType = null,
        string? ETag = null, string? FileName = null, string? ContentKind = null, Func<HttpContent>? Body = null);

    /// <param name="scope">經 RunOnUiAsync 問中樞（對話不在回 null）</param>
    /// <param name="thumbnail">產縮圖（路徑、像素、取消；取不到回 null）；null＝自己的 <see cref="ThumbnailService"/>（第一次用才建，Dispose 時關）</param>
    public MediaServer(DeviceHttpClient http, Func<MediaScopeQuery, CancellationToken, Task<MediaScopeSnapshot?>> scope, RemoteFileGuard guard,
        Func<string, int, CancellationToken, Task<byte[]?>>? thumbnail = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(guard);
        this.http = http;
        resolver = new LocalMediaResolver(scope, guard);
        this.guard = guard;
        this.thumbnail = thumbnail ?? DefaultThumbnailAsync;
    }

    /// <summary>佔著名額的轉送數（不含回 503 的）</summary>
    public int Active => Volatile.Read(ref active);

    /// <summary>測試：參照的解析（Codex 產生圖片的複製次數）</summary>
    internal LocalMediaResolver Resolver => resolver;

    /// <summary>測試：自己的 ThumbnailService（第一次用時建；和中樞、縮圖上傳的不是同一個實例，D30）</summary>
    internal ThumbnailService OwnThumbnails()
    {
        lock (thumbnailGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return thumbnails ??= new ThumbnailService();
        }
    }

    /// <summary>
    /// 引擎執行緒呼叫：轉送 id 不合格的直接丟掉（放不進網址）；名額滿了回 503。回傳的工作在背景跑完（引擎拿來算進行中的工作），一律不丟例外
    /// </summary>
    public Task HandleAsync(MediaJob job, CancellationToken connection)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (Volatile.Read(ref disposed) != 0)
            return Task.CompletedTask;
        if (!MediaServerRules.IsRelayId(job.Request.RelayId))
        {
            Log.Warn("伺服器送來的媒體轉送 id 不合格：略過（伺服器 15 秒後自己逾時）");
            return Task.CompletedTask;
        }

        var counted = Interlocked.Increment(ref active) <= MediaRelayRules.MaxConcurrentPerDevice;
        if (!counted)
            Interlocked.Decrement(ref active);
        var id = Interlocked.Increment(ref nextRelay);
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(connection);
        cancel.CancelAfter(MediaServerRules.RelayTimeout);
        relays[id] = new Relay(job.Generation, cancel);
        return Task.Run(() => ProcessAsync(job, counted, id, cancel));
    }

    /// <summary>這條連線丟掉了：取消它上面還在跑的轉送（任何執行緒）</summary>
    public void CancelConnection(int generation)
    {
        foreach (var relay in relays.Values)
        {
            if (relay.Generation != generation)
                continue;
            try
            {
                relay.Cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 剛好結束
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        foreach (var relay in relays.Values)
        {
            try
            {
                relay.Cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        lock (thumbnailGate)
        {
            thumbnails?.Dispose();
            thumbnails = null;
        }

        http.Dispose();
    }

    private async Task ProcessAsync(MediaJob job, bool counted, long id, CancellationTokenSource cancel)
    {
        var ct = cancel.Token;
        GuardedFile? file = null;
        try
        {
            var reply = counted ? await PrepareAsync(job, opened => file = opened, ct).ConfigureAwait(false) : new Reply(503);
            await SendAsync(job.Request.RelayId, reply, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Info($"媒體轉送 {job.Request.RelayId} 取消（連線丟掉、停止或逾時）");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            Log.Warn($"媒體轉送 {job.Request.RelayId} 失敗：{ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error($"媒體轉送 {job.Request.RelayId} 失敗（程式錯誤）", ex);
        }
        finally
        {
            file?.Dispose();
            relays.TryRemove(id, out _);
            cancel.Dispose();
            if (counted)
                Interlocked.Decrement(ref active);
        }
    }

    /// <summary>判斷並準備回覆；開好的檔交給 <paramref name="keep"/>（送完才關）</summary>
    private async Task<Reply> PrepareAsync(MediaJob job, Action<GuardedFile> keep, CancellationToken ct)
    {
        var request = job.Request;
        var row = job.Row;
        if (row is null || !MediaServerRules.PurposeMatches(request.Purpose, row.ItemId) || !MediaServerRules.IsValidRange(request.Range))
        {
            Refused(request, row, row is null ? "這台不認得這個參照" : MediaServerRules.IsValidRange(request.Range) ? "用途和參照不符" : "範圍不合格", warn: true);
            return Rejected(MediaServerRules.Decide(known: false, visible: false, job.Policy, request.Purpose)!.Value);
        }

        var snapshot = await resolver.QueryAsync(row, job.DeviceId, ct).ConfigureAwait(false);
        if (snapshot is not { Visible: true })
        {
            Refused(request, row, snapshot is null ? "對話不在（已刪除，或中樞還沒掛上）" : "對話看不到（草稿、複本、別台擁有或同步政策不是 full）");
            return Rejected(MediaServerRules.Decide(known: true, visible: false, job.Policy, request.Purpose)!.Value);
        }

        if (resolver.Resolve(snapshot, row) is not { } target)
        {
            Refused(request, row, "對不到檔（項目已不在、或項目裡沒有這個媒體）");
            return Rejected(new MediaVerdict(404, MediaErrorCodes.NotFound));
        }

        var fileName = target.FileName;

        if (MediaServerRules.Decide(known: true, visible: true, job.Policy, request.Purpose) is { } refused)
        {
            Refused(request, row, $"這台的遠端政策不給原檔（{refused.Error}）", target.Path);
            // 政策擋下的原檔帶型別（不碰檔案系統）：伺服器據此把圖片改回縮圖（D29）
            return Rejected(refused, request.Purpose == MediaPurposes.Media ? AgentAttachmentRules.GetMediaContentType(target.Path) : null);
        }

        // 電腦狀態面板的檔案傳輸（@xfer）：範圍是整台的本機磁碟，原檔依範圍串流（不做文字 DLP、沒有 20 MB 的上限）
        if (row.IsTransfer)
            return await TransferAsync(job, target, snapshot, keep, ct).ConfigureAwait(false);

        // Codex 產生的圖片只服務 attachments\generated\ 的複本（Q2-A；第一次用時複製）
        if (await resolver.SubstituteGeneratedAsync(snapshot, row, target, ct).ConfigureAwait(false) is not { } path)
        {
            Refused(request, row, "Codex 產生的圖片沒有複本（原檔不在、太大或複製失敗）", target.Path, warn: true);
            return Rejected(new MediaVerdict(404, MediaErrorCodes.NotFound));
        }

        var fileScope = await resolver.BuildScopeAsync(snapshot, path, ct).ConfigureAwait(false);
        if (row.ItemId == MediaServerRules.ListItemId && !RemoteFolderRules.IsInScope(path, fileScope.ListRoots))
        {
            // 抽屜的參照發出之後工作資料夾換了：舊資料夾的檔不再給（D33）
            Refused(request, row, "抽屜的檔已不在目前的範圍（工作資料夾換了）", path);
            return Rejected(new MediaVerdict(403, MediaErrorCodes.Forbidden));
        }

        var opened = await guard.OpenAsync(path, MediaServerRules.GuardPurpose(request.Purpose), fileScope, MediaServerRules.MaxBytes(request.Purpose), ct)
            .ConfigureAwait(false);
        if (opened.File is not { } file)
        {
            Refused(request, row, $"守門員拒絕（{opened.Reason}）", path, MediaDiagnosticsRules.IsUnexpectedGuardReason(opened.Reason));
            return Rejected(MediaServerRules.AfterGuard(opened.Reason ?? RemoteFileGuardReasons.Unreadable));
        }

        keep(file);
        return request.Purpose switch
        {
            MediaPurposes.Thumb => await ThumbAsync(job, file, ct).ConfigureAwait(false),
            MediaPurposes.Media => Original(file, fileName, request.Range),
            MediaPurposes.File => await FileAsync(file, fileName, request.Range, ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(job), request.Purpose, "不認得的媒體用途"),
        };
    }

    private static Reply Rejected(MediaVerdict verdict, string? contentType = null) => new(verdict.Status, verdict.Error, ContentType: contentType);

    /// <summary>檔案傳輸的一段：守門員以整台本機磁碟的範圍（<see cref="DeviceTransferScope"/>）開檔，回原檔的一段（一律下載）</summary>
    private async Task<Reply> TransferAsync(MediaJob job, ResolvedMedia target, MediaScopeSnapshot snapshot, Action<GuardedFile> keep, CancellationToken ct)
    {
        var request = job.Request;
        var scope = await DeviceTransferScope.BuildAsync(snapshot.Folders, DeviceTransferRules.FixedDriveRoots(), guard, ct).ConfigureAwait(false);
        var opened = await guard.OpenAsync(target.Path, RemoteFilePurpose.File, scope, long.MaxValue, ct).ConfigureAwait(false);
        if (opened.File is not { } file)
        {
            Refused(request, job.Row, $"守門員拒絕（{opened.Reason}）", target.Path, MediaDiagnosticsRules.IsUnexpectedGuardReason(opened.Reason));
            return Rejected(MediaServerRules.AfterGuard(opened.Reason ?? RemoteFileGuardReasons.Unreadable));
        }

        keep(file);
        return Original(file, target.FileName, request.Range, MediaContentKinds.Binary);
    }

    private static void Refused(MediaRequest request, LocalMediaRefRow? row, string reason, string? path = null, bool warn = false)
        => MediaDiagnostics.Refused("媒體轉送", request.Ref, row, request.Purpose, reason, path, warn);

    /// <summary>
    /// 原檔的一段（串流：每次序列化從起點重新讀，同一個轉送的本文一次只有一份在送）；<paramref name="kind"/> 是檔案下載的內容種類（原檔不帶）
    /// </summary>
    private static Reply Original(GuardedFile file, string fileName, MediaRange? requested, string? kind = null)
    {
        var total = file.Length;
        var segment = MediaServerRules.Segment(requested, total);
        var type = AgentAttachmentRules.GetMediaContentType(kind == null ? file.FinalPath : fileName);
        var etag = MediaServerRules.ETag(total, file.LastWriteTimeUtc);
        var gate = new SemaphoreSlim(1, 1);
        return segment.Status switch
        {
            206 => new Reply(206, Total: total, Range: segment.Range, ContentType: type, ETag: etag, FileName: fileName, ContentKind: kind,
                Body: () => new FileRangeContent(file.Stream, segment.Range!.Value.Start, segment.Range.Value.Length, gate)),
            200 => new Reply(200, Total: total, ContentType: type, ETag: etag, FileName: fileName, ContentKind: kind,
                Body: () => new FileRangeContent(file.Stream, 0, total, gate)),
            416 => new Reply(416, Total: total, ContentType: type, ETag: etag, FileName: fileName, ContentKind: kind),
            _ => new Reply(413, MediaErrorCodes.TooLarge, Total: total),
        };
    }

    /// <summary>縮圖：自己的 ThumbnailService 產 320 px PNG（守門員已經放行），超過 512 KB 回 413、產不出來 404</summary>
    private async Task<Reply> ThumbAsync(MediaJob job, GuardedFile file, CancellationToken ct)
    {
        var png = await thumbnail(file.FinalPath, MediaServerRules.ThumbPixels, ct).ConfigureAwait(false);
        if (png is null || png.Length == 0)
        {
            Refused(job.Request, job.Row, "產不出縮圖（格式解不開或檔案壞了）", file.FinalPath, warn: true);
            return Rejected(new MediaVerdict(404, MediaErrorCodes.NotFound));
        }

        if (png.Length > MediaServerRules.MaxThumbBytes)
        {
            Refused(job.Request, job.Row, "縮圖超過 512 KB", file.FinalPath, warn: true);
            return Rejected(new MediaVerdict(413, MediaErrorCodes.TooLarge));
        }
        return new Reply(200, Total: png.Length, ContentType: "image/png", Body: () => new ByteArrayContent(png));
    }

    /// <summary>
    /// 檔案下載（fileFetch 的參照）：開頭就證明是二進位的（<see cref="MediaServerRules.IsProvenBinary"/>，≤512 MB）不掃、依範圍串流（同原檔）；
    /// 其他（≤20 MB，超過 413）整份讀進記憶體（一次一個）、文字類整份跑 <see cref="MediaServerRules.HasLeak"/>（命中 403 <c>dlp</c>），
    /// 再從同一份內容回要的範圍（掃過的就是送出的）。只有嚴格 UTF-8 的標成文字（預覽），其他一律二進位（下載）
    /// </summary>
    private async Task<Reply> FileAsync(GuardedFile file, string fileName, MediaRange? requested, CancellationToken ct)
    {
        var prefix = await file.ReadPrefixAsync(MediaServerRules.BinaryProbeBytes, ct).ConfigureAwait(false);
        if (MediaServerRules.IsProvenBinary(fileName, prefix))
            return Original(file, fileName, requested, MediaContentKinds.Binary);
        if (file.Length > MediaServerRules.MaxFileBytes)
            return Rejected(new MediaVerdict(413, MediaErrorCodes.TooLarge));

        byte[] content;
        bool text;
        await fileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            content = new byte[checked((int)file.Length)];
            file.Stream.Position = 0;
            await file.Stream.ReadExactlyAsync(content, ct).ConfigureAwait(false);
            text = MediaServerRules.IsText(fileName, content);
            if (text && MediaServerRules.HasLeak(content))
            {
                Log.Warn("要下載的檔案含疑似憑證：拒絕（dlp）");
                return Rejected(new MediaVerdict(403, MediaErrorCodes.Dlp));
            }
        }
        finally
        {
            fileGate.Release();
        }

        var total = content.LongLength;
        var segment = MediaServerRules.Segment(requested, total);
        // 文字預覽（伺服器以 text/plain; charset=utf-8 inline 回）只給嚴格的 UTF-8：UTF-16 等其他編碼的文字檔照樣掃過，但以下載給
        // （瀏覽器看到 BOM 會照 BOM 解讀，預覽的就不是掃過的那一份解碼；合併前審查 F1）
        var kind = text && MediaServerRules.IsStrictUtf8(content) ? MediaContentKinds.Text : MediaContentKinds.Binary;
        var type = AgentAttachmentRules.GetMediaContentType(fileName);
        var etag = MediaServerRules.ETag(total, file.LastWriteTimeUtc);
        return segment.Status switch
        {
            206 => new Reply(206, Total: total, Range: segment.Range, ContentType: type, ETag: etag, FileName: fileName, ContentKind: kind,
                Body: () => new ByteArrayContent(content, checked((int)segment.Range!.Value.Start), checked((int)segment.Range.Value.Length))),
            200 => new Reply(200, Total: total, ContentType: type, ETag: etag, FileName: fileName, ContentKind: kind, Body: () => new ByteArrayContent(content)),
            416 => new Reply(416, Total: total, ContentType: type, ETag: etag, FileName: fileName, ContentKind: kind),
            _ => new Reply(413, MediaErrorCodes.TooLarge, Total: total),
        };
    }

    /// <summary>以 E7 回覆；拒絕的不帶本文（<c>Content-Length: 0</c>）</summary>
    private async Task SendAsync(string relayId, Reply reply, CancellationToken ct)
    {
        using var response = await http.SendAsync(() => Build(relayId, reply), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response is null)
        {
            Log.Info($"媒體轉送 {relayId}：沒有可用的存取權杖，不回覆（伺服器 15 秒後自己逾時）");
            return;
        }

        switch (response.StatusCode)
        {
            case HttpStatusCode.NoContent or HttpStatusCode.OK:
                return;
            case HttpStatusCode.Gone:
                Log.Info($"媒體轉送 {relayId}：瀏覽器已經離開");
                return;
            case HttpStatusCode.NotFound:
                Log.Info($"媒體轉送 {relayId}：伺服器已經不等這個轉送（逾時或已回覆）");
                return;
            default:
                Log.Warn($"媒體轉送 {relayId}：伺服器回 {(int)response.StatusCode}");
                return;
        }
    }

    private static HttpRequestMessage Build(string relayId, Reply reply)
    {
        var content = reply.Body?.Invoke() ?? new ByteArrayContent([]);
        var message = new HttpRequestMessage(HttpMethod.Put, MediaServerRules.RelayPath(relayId)) { Content = content };
        var headers = message.Headers;
        headers.TryAddWithoutValidation(MediaRelayHeaders.Status, reply.Status.ToString(CultureInfo.InvariantCulture));
        if (reply.Total is { } total)
            headers.TryAddWithoutValidation(MediaRelayHeaders.TotalLength, total.ToString(CultureInfo.InvariantCulture));
        if (reply.Range is { } range)
            content.Headers.ContentRange = new ContentRangeHeaderValue(range.Start, range.End, reply.Total!.Value);
        if (reply.ContentType is { } type)
            content.Headers.TryAddWithoutValidation("Content-Type", type);
        if (reply.ETag is { } etag)
            headers.TryAddWithoutValidation("ETag", etag);
        if (reply.FileName is { } name)
            headers.TryAddWithoutValidation(MediaRelayHeaders.FileName, MediaHeaderRules.EncodeFileName(name));
        if (reply.ContentKind is { } kind)
            headers.TryAddWithoutValidation(MediaRelayHeaders.ContentKind, kind);
        if (reply.Error is { } error)
            headers.TryAddWithoutValidation(MediaRelayHeaders.Error, error);
        return message;
    }

    private async Task<byte[]?> DefaultThumbnailAsync(string path, int size, CancellationToken ct)
    {
        ThumbnailService service;
        lock (thumbnailGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            service = thumbnails ??= new ThumbnailService();
        }

        return await service.GetAsync(path, size).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 守門員開好的檔的一段（不擁有串流）：每次序列化都從 <paramref name="start"/> 重新定位、正好送 <paramref name="length"/> 個位元組；
    /// 同一個轉送共用 <paramref name="gate"/>（401 重送時，上一次還沒停下的傳送不會和這次搶同一個檔案位置）。檔案中途變短丟 <see cref="IOException"/>
    /// </summary>
    private sealed class FileRangeContent(FileStream stream, long start, long length, SemaphoreSlim gate) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => SerializeToStreamAsync(target, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream target, TransportContext? context, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
            try
            {
                stream.Position = start;
                var left = length;
                while (left > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(CopyBufferBytes, left)), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        throw new IOException("檔案在轉送途中變短了");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    left -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                gate.Release();
            }
        }

        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
        }
    }
}
