using System.Net;
using System.Net.Http.Headers;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Files;
using Myriagent.Media;
using Myriagent.Remote;

namespace Myriagent.Sync;

/// <summary>一次上傳的結果</summary>
internal enum ThumbOutcome
{
    /// <summary>伺服器存好了（200）</summary>
    Uploaded,

    /// <summary>丟掉這筆（對話看不到、檔案被擋、產不出縮圖、伺服器拒絕或連不上；之後的 E10 會再列出缺的）</summary>
    Dropped,

    /// <summary>
    /// 原檔已經不在（守門員回 not_found；回覆的媒體沒有留存的副本）：丟掉，而且一段時間內不再排（<see cref="ThumbnailPublisher.MarkGone"/>，
    /// 2026-10-27：E10 每次連線都列出同一批，桌面每次都白做一次）
    /// </summary>
    Gone,
}

/// <summary>
/// 定稿媒體的縮圖上傳（WP4.7 計畫 D30 桌面那一半）：
/// <list type="bullet">
/// <item>佇列（引擎執行緒）：≤<see cref="ThumbnailPublishRules.MaxQueue"/>、同一個參照只排一次，滿了丟最舊的；這次啟動上傳過的不再排
/// （E10 列出的、伺服器換實例或對話改回 full 之後照常再排：<see cref="ForgetUploaded"/>、<see cref="ForgetAllUploaded"/>）。
/// 推：項目定稿（不是 Running）時；拉：每次協商出 media1 進 Live 時以 E10 取缺漏清單（由新到舊）。何時取工作由引擎決定（outbox 沒有未完成的列時）。</item>
/// <item>上傳（背景，<see cref="PublishAsync"/>）：經 <see cref="LocalMediaResolver"/> 問中樞、解析路徑（Codex 產生的圖片換成複本）→
/// <see cref="RemoteFileGuard.OpenAsync"/>（縮圖）→ 自己的 <see cref="ThumbnailService"/> 產 320 px PNG（F33：它同步碰檔案系統，所以要先過守門員）→ E8。
/// 遠端政策不影響縮圖（Q1-A）。</item>
/// </list>
/// 所有例外都在這裡記錄、不往外丟（取消除外）
/// </summary>
internal sealed class ThumbnailPublisher : IDisposable
{
    private readonly DeviceHttpClient http;
    private readonly LocalMediaResolver resolver;
    private readonly Func<string, int, CancellationToken, Task<byte[]?>> thumbnail;
    private readonly LinkedList<LocalMediaRefRow> queue = new();
    private readonly HashSet<string> queued = new(StringComparer.Ordinal);
    private readonly HashSet<string> uploaded = new(StringComparer.Ordinal);
    private Dictionary<string, long> gone = new(StringComparer.Ordinal);
    private readonly object thumbnailGate = new();
    private ThumbnailService? thumbnails;
    private int disposed;

    /// <summary>這次啟動記住幾個上傳過的參照（超過就清掉重記；只是少傳幾次重複的）</summary>
    private const int MaxRemembered = 10_000;

    /// <param name="http">自己的 HTTP 用戶端（Dispose 時一起關）</param>
    /// <param name="thumbnail">產縮圖（路徑、像素、取消）；null＝自己的 <see cref="ThumbnailService"/>（第一次用才建，Dispose 時關）</param>
    public ThumbnailPublisher(DeviceHttpClient http, LocalMediaResolver resolver, Func<string, int, CancellationToken, Task<byte[]?>>? thumbnail = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(resolver);
        this.http = http;
        this.resolver = resolver;
        this.thumbnail = thumbnail ?? DefaultThumbnailAsync;
    }

    /// <summary>排著的筆數（引擎執行緒）</summary>
    public int Count => queue.Count;

    /// <summary>佇列滿了丟掉的筆數（記錄與測試用）</summary>
    public int Overflowed { get; private set; }

    /// <summary>
    /// 排一筆（引擎執行緒）：只收項目的參照；已經排著或這次上傳過的不排（回 false）。滿了先丟最舊的
    /// </summary>
    public bool Enqueue(LocalMediaRefRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsLooseFile || !row.IsWellFormed || !ConversationEventRules.IsMediaRef(row.Ref) || queued.Contains(row.Ref) || uploaded.Contains(row.Ref))
            return false;
        if (queue.Count >= ThumbnailPublishRules.MaxQueue)
        {
            var oldest = queue.First!.Value;
            queue.RemoveFirst();
            queued.Remove(oldest.Ref);
            Overflowed++;
        }

        queue.AddLast(row);
        queued.Add(row.Ref);
        return true;
    }

    /// <summary>取最舊的一筆（引擎執行緒；沒有回 null）</summary>
    public LocalMediaRefRow? TryTake()
    {
        if (queue.First is not { } first)
            return null;
        queue.RemoveFirst();
        queued.Remove(first.Value.Ref);
        return first.Value;
    }

    /// <summary>上傳成功（引擎執行緒）：這次啟動不再排它</summary>
    public void MarkUploaded(string mediaRef)
    {
        if (uploaded.Count >= MaxRemembered)
            uploaded.Clear();
        uploaded.Add(mediaRef);
    }

    /// <summary>
    /// 伺服器說它缺這張（E10 列出，以伺服器為準；引擎執行緒）：忘掉「這次上傳過」，之後照常排（合併前審查 DI-1：還原、降級再改回 full 之後
    /// 伺服器的 <c>thumb_blob</c> 沒了，桌面不重開也要補得回去）
    /// </summary>
    public void ForgetUploaded(string mediaRef) => uploaded.Remove(mediaRef);

    /// <summary>伺服器換了實例（還原、重建）或有對話改回 full（重新投影；引擎執行緒）：上傳過的全部忘掉，定稿時推的重排照常上傳（DI-1）</summary>
    public void ForgetAllUploaded() => uploaded.Clear();

    /// <summary>
    /// 讀回記著的原檔不在的參照（sync.db 的 kv，<see cref="ThumbnailPublishRules.ParseGone"/>；引擎執行緒）：到期的丟掉。
    /// 伺服器那邊的狀態（換實例、改回 full）和檔案在不在無關，<see cref="ForgetAllUploaded"/> 不清它
    /// </summary>
    public void LoadGone(string? json, long nowMs)
    {
        gone = ThumbnailPublishRules.ParseGone(json);
        ThumbnailPublishRules.PruneGone(gone, nowMs);
    }

    /// <summary>這個參照的原檔已知不在、還沒到再試的時間（引擎執行緒）</summary>
    public bool IsGone(string mediaRef, long nowMs) => ThumbnailPublishRules.IsGone(gone, mediaRef, nowMs);

    /// <summary>上傳時原檔已經不在（引擎執行緒）：記下來；回傳要不要存回 kv（本來沒記著或已到期）</summary>
    public bool MarkGone(string mediaRef, long nowMs)
    {
        if (IsGone(mediaRef, nowMs))
            return false;
        gone[mediaRef] = nowMs;
        ThumbnailPublishRules.PruneGone(gone, nowMs);
        return true;
    }

    /// <summary>記著的原檔不在的參照（kv 的值）</summary>
    public string SerializeGone() => ThumbnailPublishRules.SerializeGone(gone);

    /// <summary>上傳一筆（背景；<paramref name="ct"/> 是連線的取消）：失敗都回 <see cref="ThumbOutcome.Dropped"/>，取消照丟</summary>
    public async Task<ThumbOutcome> PublishAsync(LocalMediaRefRow row, string deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        try
        {
            var snapshot = await resolver.QueryAsync(row, deviceId, ct).ConfigureAwait(false);
            if (snapshot is not { Visible: true } || resolver.Resolve(snapshot, row) is not { } target)
                return Drop(row, "對話看不到或項目已經不在");
            if (await resolver.SubstituteGeneratedAsync(snapshot, row, target, ct).ConfigureAwait(false) is not { } path)
                return Drop(row, "Codex 產生的圖片沒有複本", target.Path, warn: true);
            var scope = await resolver.BuildScopeAsync(snapshot, path, ct).ConfigureAwait(false);
            var opened = await resolver.Guard.OpenAsync(path, RemoteFilePurpose.Thumb, scope, long.MaxValue, ct).ConfigureAwait(false);
            if (opened.File is not { } file)
            {
                // 在別的對話資料夾裡（延伸對話引用的）的檔，守門員先判範圍、回 forbidden，不看在不在：已經被刪掉的也照「原檔不在」記下來，
                // 不然每次連線、每 6 小時都再試一次（2026-11-02 實機）。forbidden 一定是本機的固定或抽取式磁碟（網路磁碟先判成 drive），看在不在不會對外連線
                bool vanished = opened.Reason == RemoteFileGuardReasons.Forbidden && AgentAttachmentRules.IsLocalDrivePath(path) && !File.Exists(path);
                if (opened.Reason == RemoteFileGuardReasons.NotFound || vanished)
                {
                    // 原檔已經被刪掉（回覆的媒體沒有留存的副本）：不是暫時的，記下來一段時間內不再試
                    Drop(row, $"守門員拒絕（{opened.Reason}）：原檔已經不在，{ThumbnailPublishRules.GoneRetryAfter.TotalDays:0} 天內不再試", path);
                    return ThumbOutcome.Gone;
                }
                return Drop(row, $"守門員拒絕（{opened.Reason}）", path, MediaDiagnosticsRules.IsUnexpectedGuardReason(opened.Reason));
            }

            byte[]? png;
            using (file)
                png = await thumbnail(file.FinalPath, MediaServerRules.ThumbPixels, ct).ConfigureAwait(false);
            if (png is not { Length: > 0 } || png.LongLength > MediaServerRules.MaxThumbBytes)
                return Drop(row, png is { Length: > 0 } ? "縮圖超過 512 KB" : "產不出縮圖", path, warn: true);

            using var response = await http.SendAsync(() => Put(row, png), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response is null)
                return Drop(row, "沒有可用的存取權杖");
            if (response.StatusCode == HttpStatusCode.OK)
                return ThumbOutcome.Uploaded;
            return Drop(row, $"伺服器回 {(int)response.StatusCode}", path, warn: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            return Drop(row, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("上傳縮圖失敗（程式錯誤）", ex);
            return ThumbOutcome.Dropped;
        }
    }

    /// <summary>E10 的一頁（背景）：回應不合格、伺服器拒絕或連不上回 null</summary>
    public async Task<MissingThumbPage?> FetchMissingAsync(string? before, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ThumbnailPublishRules.MissingPath(before)),
                HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (response is null)
                return null;
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Log.Info($"缺縮圖的清單：伺服器回 {(int)response.StatusCode}");
                return null;
            }

            var page = ThumbnailPublishRules.ParseMissing(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (page is null)
                Log.Warn("缺縮圖的清單格式不對：略過");
            return page;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            Log.Info($"取缺縮圖的清單失敗：{ex.Message}");
            return null;
        }
    }

    /// <summary>測試：自己的 ThumbnailService（第一次用時建；和中樞、媒體轉送的不是同一個實例，D30）</summary>
    internal ThumbnailService OwnThumbnails()
    {
        lock (thumbnailGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return thumbnails ??= new ThumbnailService();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        lock (thumbnailGate)
        {
            thumbnails?.Dispose();
            thumbnails = null;
        }

        http.Dispose();
    }

    private static HttpRequestMessage Put(LocalMediaRefRow row, byte[] png)
    {
        var content = new ByteArrayContent(png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var message = new HttpRequestMessage(HttpMethod.Put, ThumbnailPublishRules.PutPath(row.Ref)) { Content = content };
        message.Headers.TryAddWithoutValidation(MediaRelayHeaders.Conversation, row.ConversationId);
        return message;
    }

    private static ThumbOutcome Drop(LocalMediaRefRow row, string reason, string? path = null, bool warn = false)
    {
        MediaDiagnostics.Refused("縮圖上傳", row.Ref, row, MediaPurposes.Thumb, reason, path, warn);
        return ThumbOutcome.Dropped;
    }

    private async Task<byte[]?> DefaultThumbnailAsync(string path, int size, CancellationToken ct)
        => await OwnThumbnails().GetAsync(path, size).WaitAsync(ct).ConfigureAwait(false);
}
