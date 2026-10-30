using Myriagent.Agents;
using Myriagent.Core;
using Microsoft.Data.Sqlite;

namespace Myriagent.Sync;

// WP4.7（媒體、檔案與上傳）步 10（D14、D15、D31、D32）：項目欄位 blob 的引擎那一側。
// - 投影器（ConversationProjector.Blobs.cs）經 MediaRefIssuer 問這裡：UseBlobs＝協商出 media1（離線時看上一條連線）、項目不是 Running、本次執行沒被拒收過；
//   EnsureUploaded＝blob 都確認在伺服器上（這次執行 HEAD 或 PUT 過、1 小時內）才回 true，否則排進上傳佇列、這次照 WP4.3 送截斷版。
// - 上傳（BlobUploader，E5 HEAD → PUT）：一次一個項目、依序；只在協商出 media1 的連線上傳；對話不再發布內容（刪除、localOnly、降級）的不傳。
//   全部確認後清掉那個項目的來源摘要、重新走那個對話（重整佇列；投影最新的快照，同一個合併鍵入列）。
//   422／413 等拒收：這個項目（與那個 blob）在本次執行不再試，之後照截斷版。暫時失敗：放回佇列最前面，5 分鐘後再試。
// - 佇列帶著 blob 的內容：≤64 個項目、≤32 MB；放不下的記進待補清單（只有鍵），佇列與重整都空了才分批清來源摘要、重新走。
// - RequeueTruncatedForMedia（D32）：這次執行第一次協商出 media1 時（之後換到沒有 media1 的伺服器又換回來時再一次），published_items.truncated=1 的項目
//   進待補清單，依對話由新到舊分批重新投影。
// 狀態只在引擎執行緒改；上傳在背景（Spawn 的 await 回到引擎執行緒）
internal sealed partial class SyncEngine
{
    /// <summary>一個等著上傳的項目（遮蔽之後的 blob 內容）</summary>
    private sealed record ItemBlobJob((string Wire, string ItemId) Key, IReadOnlyList<ConversationBlob> Blobs, long Bytes);

    /// <summary>MediaRefIssuer 轉過來的投影器問題（引擎執行緒）</summary>
    private sealed class ItemBlobHook(SyncEngine engine) : IItemBlobUploads
    {
        public bool UseBlobs(string wireId, AgentChatItem snapshot) => engine.UseItemBlobs(wireId, snapshot);

        public bool EnsureUploaded(string wireId, string itemId, IReadOnlyList<ConversationBlob> blobs) => engine.EnsureItemBlobs(wireId, itemId, blobs);
    }

    /// <summary>佇列與重整都空了才分批補的時候，多久再看一次</summary>
    private static readonly TimeSpan BlobBacklogRecheck = TimeSpan.FromSeconds(2);

    // ── 以下只在引擎執行緒讀寫 ──
    private readonly Dictionary<string, long> blobConfirmed = new(StringComparer.Ordinal);
    private readonly HashSet<string> blobRejectedShas = new(StringComparer.Ordinal);
    private readonly HashSet<(string Wire, string ItemId)> blobRejected = new();
    private readonly HashSet<(string Wire, string ItemId)> blobSettled = new();
    private readonly HashSet<(string Wire, string ItemId)> blobBacklog = new();
    private readonly List<ItemBlobJob> blobPending = new();
    private long blobPendingBytes;
    private ItemBlobJob? blobInFlight;
    private bool blobPumpPosted;
    private bool blobsArmed;
    private bool blobRequeueDone;
    private bool blobRequeueWanted;
    private string? blobServerKey;
    private ITimer? blobRetryTimer;
    private ITimer? blobBacklogTimer;
    private BlobUploader? blobUploader;

    /// <summary>投影器的 blob 接縫（CreateItemMediaHook 交給 MediaRefIssuer）</summary>
    private IItemBlobUploads CreateItemBlobHook() => new ItemBlobHook(this);

    /// <summary>投影器：這一筆要不要先試 blob（見 <see cref="IItemMediaHook.UseBlobs"/>）</summary>
    private bool UseItemBlobs(string wire, AgentChatItem snapshot)
    {
        var key = (wire, snapshot.Id);
        blobBacklog.Remove(key);
        if (stopping || !blobsArmed || snapshot.Status == AgentItemStatus.Running || blobRejected.Contains(key))
        {
            blobSettled.Remove(key);
            return false;
        }

        return true;
    }

    /// <summary>投影器：blob 都已確認在伺服器上回 true；否則排上傳回 false（見 <see cref="IItemMediaHook.EnsureUploaded"/>）</summary>
    private bool EnsureItemBlobs(string wire, string itemId, IReadOnlyList<ConversationBlob> blobs)
    {
        var key = (wire, itemId);
        CheckBlobServer();
        if (blobs.Any(b => blobRejectedShas.Contains(b.Sha256)))
        {
            // 同一份內容被伺服器拒收過（D15：這個 SHA 在本次執行不再試）
            blobRejected.Add(key);
            blobSettled.Remove(key);
            DropPendingBlobs(key);
            return false;
        }

        var now = MonoMs();
        var missing = blobs.Where(b => !IsBlobConfirmed(b.Sha256, now)).ToList();
        if (missing.Count == 0)
        {
            DropPendingBlobs(key);
            if (blobSettled.Count >= ItemBlobRules.MaxRemembered)
                blobSettled.Clear();
            blobSettled.Add(key);
            return true;
        }

        blobSettled.Remove(key);
        if (blobInFlight is { } running && running.Key == key && missing.All(b => running.Blobs.Any(r => r.Sha256 == b.Sha256)))
            return false;   // 這一版正在傳：傳完會重新走這個對話
        EnqueueItemBlobs(new ItemBlobJob(key, missing, missing.Sum(b => b.Content.LongLength)));
        return false;
    }

    /// <summary>排一個項目（同一個項目只留最新的一版）；佇列滿了記進待補清單</summary>
    private void EnqueueItemBlobs(ItemBlobJob job)
    {
        DropPendingBlobs(job.Key);
        if (blobPending.Count >= ItemBlobRules.MaxPendingItems || blobPendingBytes + job.Bytes > ItemBlobRules.MaxPendingBytes)
        {
            if (blobBacklog.Count < ItemBlobRules.MaxRemembered)
                blobBacklog.Add(job.Key);
            return;
        }

        blobPending.Add(job);
        blobPendingBytes += job.Bytes;
        RequestBlobPump();
    }

    private void DropPendingBlobs((string Wire, string ItemId) key)
    {
        var index = blobPending.FindIndex(j => j.Key == key);
        if (index < 0)
            return;
        blobPendingBytes -= blobPending[index].Bytes;
        blobPending.RemoveAt(index);
    }

    private bool IsBlobConfirmed(string sha, long now)
        => blobConfirmed.TryGetValue(sha, out var at) && now - at < (long)ItemBlobRules.ConfirmedTtl.TotalMilliseconds;

    private void RememberBlobConfirmed(string sha, long now)
    {
        if (blobConfirmed.Count >= ItemBlobRules.MaxRemembered)
            blobConfirmed.Clear();
        blobConfirmed[sha] = now;
    }

    /// <summary>伺服器換了實例或還原了：確認過的 blob 與送過的 blob 版都不算數</summary>
    private void CheckBlobServer()
    {
        if (store is null)
            return;
        string key;
        try
        {
            key = store.PublishServerKey();
        }
        catch (Exception ex) when (ex is SqliteException or ObjectDisposedException)
        {
            return;
        }

        if (blobServerKey == key)
            return;
        if (blobServerKey is not null)
        {
            blobConfirmed.Clear();
            blobSettled.Clear();
        }

        blobServerKey = key;
    }

    /// <summary>Prove 完成（OnProvenForMedia）：記下這條連線有沒有 media1；換到沒有 media1 的伺服器時丟掉佇列，等換回來時再整批重排</summary>
    private void NoteItemBlobsNegotiated(SyncConnection conn)
    {
        if (!IsCurrent(conn))
            return;
        var media = DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Media1);
        if (!media)
        {
            // 這段期間定稿的項目都照截斷版送：之後協商出 media1 時整批重排（D32）
            if (blobPending.Count > 0 || blobBacklog.Count > 0)
                Log.Info($"伺服器沒有宣告 media1：{blobPending.Count + blobBacklog.Count} 個項目的 blob 先不傳，之後協商出 media1 時重排");
            blobRequeueWanted = true;
            blobPending.Clear();
            blobPendingBytes = 0;
            blobBacklog.Clear();
        }

        blobsArmed = media;
        CheckBlobServer();
    }

    /// <summary>
    /// 協商出 media1 進 Live（OnOnlineForMedia）：這次執行第一次（或換回有 media1 的伺服器）時截斷過的項目進待補清單（D32），再排一次上傳
    /// </summary>
    private void OnOnlineForItemBlobs()
    {
        if (!blobRequeueDone || blobRequeueWanted)
            RequeueTruncatedForMedia();
        RequestBlobPump();
    }

    /// <summary>
    /// D32：<c>published_items.truncated=1</c>、對話還在發布內容的項目進待補清單（這次執行已送過 blob 版、被拒收、正在排、執行中的除外）；
    /// 之後佇列與重整都空了，依對話由新到舊分批清來源摘要、重新投影
    /// </summary>
    private void RequeueTruncatedForMedia()
    {
        if (store is not { ConversationTablesReady: true } s || Conversations is null)
            return;
        IReadOnlyList<(string ConversationId, string ItemId)> truncated;
        try
        {
            truncated = s.LoadTruncatedItems();
        }
        catch (Exception ex) when (ex is SqliteException or ObjectDisposedException)
        {
            Log.Warn($"讀截斷過的項目失敗（下一次連線再試）：{ex.Message}");
            return;
        }

        blobRequeueDone = true;
        blobRequeueWanted = false;
        var added = 0;
        foreach (var key in truncated)
        {
            if (blobBacklog.Count >= ItemBlobRules.MaxRemembered)
                break;
            if (blobSettled.Contains(key) || blobRejected.Contains(key) || runningItems.ContainsKey(key) || IsBlobQueued(key) || !ItemBlobsAllowed(key.ConversationId, needMeta: false))
                continue;
            if (blobBacklog.Add(key))
                added++;
        }

        if (added > 0)
            Log.Info($"項目欄位 blob：{added} 筆截斷過的項目排進重送（依對話由新到舊分批）");
    }

    private bool IsBlobQueued((string Wire, string ItemId) key) => blobInFlight?.Key == key || blobPending.Exists(j => j.Key == key);

    /// <summary>這個對話還在發布項目（寫過 created、沒刪、沒擋、不在寫旁檔、政策 full；<paramref name="needMeta"/> 時另要這次啟動的中繼資料，不是草稿或複本）</summary>
    private bool ItemBlobsAllowed(string wire, bool needMeta)
    {
        if (!Rows().TryGetValue(wire, out var row) || !row.CreatedWritten || row.DeletedAt is not null || row.Blocked is not null || tombstoning.Contains(wire))
            return false;
        if (lastMeta.TryGetValue(wire, out var meta))
            return PublishesItems(wire, meta, row);
        return !needMeta && ConversationSyncPolicies.Interpret(row.Policy) == ConversationSyncPolicies.Full;
    }

    private void RequestBlobPump()
    {
        if (blobPumpPosted || stopping)
            return;
        blobPumpPosted = true;
        thread.Post(PumpItemBlobs);
    }

    /// <summary>上傳下一個項目；佇列空了而且重整也空了，從待補清單取下一批</summary>
    private void PumpItemBlobs()
    {
        blobPumpPosted = false;
        if (stopping || blobInFlight is not null || blobRetryTimer is not null)
            return;
        if (current is not { Proven: true } conn || !IsCurrent(conn) || !DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Media1)
            || store is not { ConversationTablesReady: true } || Conversations is null)
            return;
        while (blobPending.Count > 0)
        {
            var job = blobPending[0];
            blobPending.RemoveAt(0);
            blobPendingBytes -= job.Bytes;
            if (!ItemBlobsAllowed(job.Key.Wire, needMeta: true))
                continue;   // 對話刪除、改成 localOnly 或降級了：不再外送它的內容
            StartItemBlobUpload(conn, job);
            return;
        }

        if (blobBacklog.Count > 0)
            FeedBlobBacklog();
    }

    private void StartItemBlobUpload(SyncConnection conn, ItemBlobJob job)
    {
        blobInFlight = job;
        var uploader = blobUploader ??= CreateBlobUploader();
        Spawn(async () =>
        {
            var outcome = BlobUploadOutcome.Confirmed;
            string? rejectedSha = null;
            var confirmed = new List<string>(job.Blobs.Count);
            try
            {
                foreach (var blob in job.Blobs)
                {
                    var result = await uploader.UploadAsync(blob, conn.Token);
                    if (result != BlobUploadOutcome.Confirmed)
                    {
                        outcome = result;
                        rejectedSha = result == BlobUploadOutcome.Rejected ? blob.Sha256 : null;
                        break;
                    }

                    confirmed.Add(blob.Sha256);
                }
            }
            catch (OperationCanceledException)
            {
                outcome = BlobUploadOutcome.Failed;
            }
            finally
            {
                if (ReferenceEquals(blobInFlight, job))
                    blobInFlight = null;
            }

            OnItemBlobsUploaded(conn, job, outcome, confirmed, rejectedSha);
        });
    }

    /// <summary>一個項目傳完（引擎執行緒）：全部確認就重新走那個對話；拒收的記下來；暫時失敗的放回最前面（連線還在時 5 分鐘後再試）</summary>
    private void OnItemBlobsUploaded(SyncConnection conn, ItemBlobJob job, BlobUploadOutcome outcome, IReadOnlyList<string> confirmed, string? rejectedSha)
    {
        if (stopping)
            return;
        var now = MonoMs();
        foreach (var sha in confirmed)
            RememberBlobConfirmed(sha, now);
        switch (outcome)
        {
            case BlobUploadOutcome.Confirmed:
                RewalkItemBlobs([job.Key]);
                break;
            case BlobUploadOutcome.Rejected:
                blobRejected.Add(job.Key);
                if (rejectedSha is not null)
                    blobRejectedShas.Add(rejectedSha);
                blobSettled.Remove(job.Key);
                DropPendingBlobs(job.Key);
                break;
            case BlobUploadOutcome.Failed:
                // 這段期間又有新版的就不放回（新版已在佇列）；連線斷了等下一條 media1 連線，連線還在（伺服器暫時不收）5 分鐘後再試
                if (!blobPending.Exists(j => j.Key == job.Key))
                {
                    var rest = job.Blobs.Where(b => !confirmed.Contains(b.Sha256, StringComparer.Ordinal)).ToList();
                    var retry = new ItemBlobJob(job.Key, rest, rest.Sum(b => b.Content.LongLength));
                    blobPending.Insert(0, retry);
                    blobPendingBytes += retry.Bytes;
                }

                if (IsCurrent(conn) && !conn.Token.IsCancellationRequested)
                    blobRetryTimer ??= CreateTimer(ItemBlobRules.RetryAfter, () =>
                    {
                        CancelTimer(ref blobRetryTimer);
                        RequestBlobPump();
                    });
                break;
            default:
                throw new InvalidOperationException("不認得的上傳結果");
        }

        RequestBlobPump();
    }

    /// <summary>
    /// 佇列空了：重整也空了（一般的重整與上一批都投影完了）才從待補清單取下一批（≤<see cref="ItemBlobRules.MaxPendingItems"/> 個，依對話由新到舊），
    /// 不然 2 秒後再看
    /// </summary>
    private void FeedBlobBacklog()
    {
        if (refreshNeeded || refreshing || activeJob is not null || reconcileQueue.Count > 0 || walks.Count > 0)
        {
            blobBacklogTimer ??= CreateTimer(BlobBacklogRecheck, () =>
            {
                CancelTimer(ref blobBacklogTimer);
                RequestBlobPump();
            });
            return;
        }

        var batch = blobBacklog
            .OrderByDescending(k => UpdatedAtMs(lastMeta.GetValueOrDefault(k.Wire)))
            .ThenBy(k => k.Wire, StringComparer.Ordinal)
            .ThenBy(k => k.ItemId, StringComparer.Ordinal)
            .Take(ItemBlobRules.MaxPendingItems)
            .ToList();
        foreach (var key in batch)
            blobBacklog.Remove(key);
        RewalkItemBlobs(batch.Where(k => !blobSettled.Contains(k) && !blobRejected.Contains(k) && !runningItems.ContainsKey(k) && ItemBlobsAllowed(k.Wire, needMeta: true))
            .ToList());
        if (blobBacklog.Count > 0)
            RequestBlobPump();
    }

    /// <summary>清掉這些項目的來源摘要（線上摘要保留）、重新走它們的對話：重新投影最新的快照，內容沒變的不重送</summary>
    private void RewalkItemBlobs(IReadOnlyList<(string Wire, string ItemId)> items)
    {
        if (items.Count == 0 || stopping || store is not { ConversationTablesReady: true } s || Conversations is null)
            return;
        try
        {
            s.ForgetSourceDigests(items);
        }
        catch (Exception ex) when (ex is SqliteException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Warn($"清項目的來源摘要失敗（這些項目之後重整再補）：{ex.Message}");
            return;
        }

        var rows = Rows();
        foreach (var wire in items.Select(i => i.Wire).Distinct(StringComparer.Ordinal))
        {
            if (rows.TryGetValue(wire, out var row))
                rows[wire] = row with { ItemsFileSha256 = null };
            if (walks.TryGetValue(wire, out var walk))
                walk.Again = true;
            else
                EnqueueReconcile(new ReconcileCandidate(wire, ReconcileReason.Redo, UpdatedAtMs(lastMeta.GetValueOrDefault(wire)), Estimate(wire)));
        }
    }

    private BlobUploader CreateBlobUploader()
        => new(new DeviceHttpClient(server, pairedServer, args.DevTrustEnv, accessTokens, RefreshAccessTokenForHttpAsync, time, MediaHttpHandlerForTest));

    /// <summary>引擎停止（CloseMedia）：佇列、計時器、上傳的 HTTP 用戶端一起收掉</summary>
    private void CloseItemBlobs()
    {
        CancelTimer(ref blobRetryTimer);
        CancelTimer(ref blobBacklogTimer);
        blobPending.Clear();
        blobPendingBytes = 0;
        blobBacklog.Clear();
        blobInFlight = null;
        blobUploader?.Dispose();
        blobUploader = null;
    }
}
