using System.Security.Cryptography;
using Myriagent.Core;
using Microsoft.Data.Sqlite;

namespace Myriagent.Sync;

// WP4.7（媒體、檔案與上傳）步 9：
// - 參照配發：投影器的 IItemMediaHook 是 MediaRefIssuer（正本在 media_refs_local，先寫它、再寫 outbox；D4）。
// - 歷史回補：第一次協商出 media1（kv media_backfill_done 和目前的伺服器實例不同）時，已發布的 full 對話清 items.json 的摘要、記 needs_redo、
//   由新到舊請重整；重整時 RefsMissing 讓摘要相同、缺參照的項目也重新投影。
// - 縮圖（D30）：定稿的項目排進 ThumbnailPublisher；只在協商出 media1、outbox 沒有未完成的列時一次上傳一筆（outbox 忙時 2 秒後再看）；
//   進 Live 時以 E10 由新到舊拉缺漏清單（佇列空出一頁才拉下一頁；游標只在這條連線的記憶體裡）。E10 列出的、伺服器換實例、對話改回 full 時
//   忘掉「這次上傳過」（合併前審查 DI-1，伺服器的縮圖可能沒了）。上傳時原檔已經不在（not_found）的記進 kv（thumb_gone），
//   7 天內 E10 再列出來也不排（2026-10-27：每次連線都對同一批白做一次）。
// 狀態只在引擎執行緒改；上傳與 E10 在背景（Spawn 的 await 回到引擎執行緒）
internal sealed partial class SyncEngine
{
    // ── 以下只在引擎執行緒讀寫 ──
    private ThumbnailPublisher? thumbs;
    private bool thumbBusy;
    private bool thumbPumpPosted;
    private ITimer? thumbTimer;
    private int thumbPullGeneration = -1;
    private string? thumbPullNext;
    private bool thumbPullBusy;

    /// <summary>測試接縫：參照的亂數（null＝RandomNumberGenerator）。啟動前設定</summary>
    internal Func<int, byte[]>? MediaRandomForTest { get; set; }

    /// <summary>投影器的媒體接縫（第一次投影時建）</summary>
    private IItemMediaHook CreateItemMediaHook()
        => new MediaRefIssuer(() => store, NowMs, MediaRandomForTest ?? RandomNumberGenerator.GetBytes, OnMediaRefsFinalized, CreateItemBlobHook());

    /// <summary>投影器配好參照的定稿項目（引擎執行緒、投影途中）：排進縮圖佇列，等這一輪寫完 outbox 才排上傳</summary>
    private void OnMediaRefsFinalized(string wire, IReadOnlyList<LocalMediaRefRow> rows)
    {
        if (stopping || Conversations is null)
            return;
        var publisher = thumbs ??= CreateThumbnailPublisher();
        var now = NowMs();
        foreach (var row in rows)
        {
            // 重整（歷史回補、換實例）重新投影的舊項目：原檔已知不在的不排
            if (!publisher.IsGone(row.Ref, now))
                publisher.Enqueue(row);
        }
        RequestThumbPump();
    }

    /// <summary>
    /// 第一次協商出 media1（或換實例、還原之後）：已發布的 full 對話清 items.json 的摘要、記 needs_redo，由新到舊排進重整佇列；
    /// 記下 kv <c>media_backfill_done</c>（寫進 sync.db 的 needs_redo 之後才記：中途當機下次再來一次）
    /// </summary>
    private void BackfillMediaRefs()
    {
        var s = store!;
        var key = s.PublishServerKey();
        if (s.GetKv(SyncStoreSchema.MediaBackfillDone) == key)
            return;
        var wires = Rows().Values
            .Where(r => r.CreatedWritten && r.DeletedAt is null && r.Blocked is null && ConversationSyncPolicies.Interpret(r.Policy) == ConversationSyncPolicies.Full
                        && !tombstoning.Contains(r.ConversationId))
            .Select(r => r.ConversationId)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (wires.Count > 0)
        {
            var pass = new ConversationPass();
            foreach (var wire in wires)
            {
                pass.Metas[wire] = RowOf(pass, wire) with { ItemsFileSha256 = null };
                pass.Redo[wire] = true;
            }

            if (!FlushPass(pass))
            {
                Log.Warn("媒體參照的歷史回補：記 needs_redo 失敗，下一次連線再試");
                return;
            }

            RequestRedo(wires);
        }

        s.SetKv(SyncStoreSchema.MediaBackfillDone, key);
        Log.Info($"媒體參照的歷史回補：{wires.Count} 個已發布的對話排進重整（由新到舊），缺參照的項目重新投影");
    }

    private ThumbnailPublisher CreateThumbnailPublisher()
    {
        var client = new DeviceHttpClient(server, pairedServer, args.DevTrustEnv, accessTokens, RefreshAccessTokenForHttpAsync, time, MediaHttpHandlerForTest);
        var publisher = new ThumbnailPublisher(client, new LocalMediaResolver(QueryMediaScopeAsync, MediaGuardForTest ?? RemoteFileGuard.CreateDefault()), MediaThumbnailForTest);
        try
        {
            publisher.LoadGone(store?.GetKv(SyncStoreSchema.ThumbGone), NowMs());
        }
        catch (SqliteException ex)
        {
            Log.Warn($"讀原檔不在的縮圖參照失敗（照常上傳）：{ex.Message}");
        }
        return publisher;
    }

    /// <summary>上傳時原檔已經不在：記下來並存回 sync.db 的 kv（引擎執行緒）；存不進去只是下次啟動再白試一次</summary>
    private void MarkThumbGone(ThumbnailPublisher publisher, string mediaRef)
    {
        if (!publisher.MarkGone(mediaRef, NowMs()) || store is not { } s)
            return;
        try
        {
            s.SetKv(SyncStoreSchema.ThumbGone, publisher.SerializeGone());
        }
        catch (SqliteException ex)
        {
            Log.Warn($"記原檔不在的縮圖參照失敗：{ex.Message}");
        }
    }

    /// <summary>排一次縮圖的排程（排回引擎執行緒：投影途中呼叫時，這一輪的 outbox 寫完才看）</summary>
    private void RequestThumbPump()
    {
        if (thumbPumpPosted || stopping)
            return;
        thumbPumpPosted = true;
        thread.Post(PumpThumbs);
    }

    /// <summary>
    /// 上傳下一筆：協商出 media1 的連線、沒有正在上傳的、outbox 沒有未完成的列（一般事件優先；忙時 2 秒後再看）。
    /// 佇列空了而且這條連線的 E10 還有下一頁時拉下一頁
    /// </summary>
    private void PumpThumbs()
    {
        thumbPumpPosted = false;
        if (stopping || thumbBusy || thumbs is not { } publisher)
            return;
        if (current is not { Proven: true } conn || !IsCurrent(conn) || !DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Media1))
            return;
        if (publisher.Count == 0)
        {
            PullMoreThumbs(conn);
            return;
        }

        if (CountNotDone() > 0)
        {
            thumbTimer ??= CreateTimer(ThumbnailPublishRules.RecheckInterval, () =>
            {
                CancelTimer(ref thumbTimer);
                PumpThumbs();
            });
            return;
        }

        if (publisher.TryTake() is not { } row)
            return;
        thumbBusy = true;
        var deviceId = device!.Current.DeviceId;
        Spawn(async () =>
        {
            try
            {
                var outcome = await publisher.PublishAsync(row, deviceId, conn.Token);
                if (outcome == ThumbOutcome.Uploaded && ReferenceEquals(publisher, thumbs))
                    publisher.MarkUploaded(row.Ref);
                else if (outcome == ThumbOutcome.Gone && ReferenceEquals(publisher, thumbs))
                    MarkThumbGone(publisher, row.Ref);
            }
            finally
            {
                thumbBusy = false;
                if (ReferenceEquals(publisher, thumbs))
                    RequestThumbPump();
            }
        });
    }

    /// <summary>進 Live（協商出 media1）：E10 從第一頁拉起</summary>
    private void StartThumbPull(SyncConnection conn)
    {
        thumbPullGeneration = conn.Generation;
        thumbPullNext = null;
        FetchThumbPage(conn, null);
    }

    /// <summary>佇列空出一頁、這條連線的 E10 還有下一頁：拉下一頁</summary>
    private void PullMoreThumbs(SyncConnection conn)
    {
        if (thumbPullBusy || thumbPullGeneration != conn.Generation || thumbPullNext is not { } next || thumbs is not { } publisher
            || !ThumbnailPublishRules.MayPull(publisher.Count))
            return;
        FetchThumbPage(conn, next);
    }

    private void FetchThumbPage(SyncConnection conn, string? before)
    {
        if (thumbPullBusy)
            return;
        var publisher = thumbs ??= CreateThumbnailPublisher();
        thumbPullBusy = true;
        thumbPullNext = null;
        Spawn(async () =>
        {
            MissingThumbPage? page;
            try
            {
                page = await publisher.FetchMissingAsync(before, conn.Token);
            }
            finally
            {
                thumbPullBusy = false;
            }

            if (stopping || !IsCurrent(conn) || thumbPullGeneration != conn.Generation || !ReferenceEquals(publisher, thumbs) || page is null)
                return;
            var added = 0;
            var gone = 0;
            var now = NowMs();
            foreach (var missing in page.Refs)
            {
                // 以伺服器為準：伺服器還原、對話降級再改回 full 之後縮圖沒了，這次啟動上傳過的也要再傳（合併前審查 DI-1）
                if (FindItemRef(missing) is not { } row)
                    continue;
                // 原檔已知不在（之前上傳時守門員回 not_found）：伺服器照舊會列，桌面不再白做
                if (publisher.IsGone(row.Ref, now))
                {
                    gone++;
                    continue;
                }
                publisher.ForgetUploaded(row.Ref);
                if (publisher.Enqueue(row))
                    added++;
            }

            thumbPullNext = page.Next;
            // 只剩原檔不在的不記（每次連線都會列出同一批）
            if (added > 0)
                Log.Info($"伺服器缺 {page.Refs.Count} 張縮圖：排了 {added} 張{(gone > 0 ? $"（{gone} 張原檔已經不在，不再試）" : string.Empty)}{(page.Next is null ? string.Empty : "（還有下一頁）")}");
            RequestThumbPump();
        });
    }

    /// <summary>E10 列的參照在 media_refs_local 而且是同一個對話的同一個項目（不是就略過：已經刪掉或伺服器的資料比較舊）</summary>
    private LocalMediaRefRow? FindItemRef(MissingThumb missing)
    {
        if (store is not { ConversationTablesReady: true } s)
            return null;
        try
        {
            return s.FindMediaRef(missing.Ref) is { IsLooseFile: false } row
                   && string.Equals(row.ConversationId, missing.ConversationId, StringComparison.Ordinal)
                   && string.Equals(row.ItemId, missing.ItemId, StringComparison.Ordinal)
                ? row
                : null;
        }
        catch (SqliteException ex)
        {
            Log.Warn($"查缺縮圖的參照失敗：{ex.Message}");
            return null;
        }
    }

    /// <summary>連線丟掉：這條連線的 E10 游標作廢（佇列留著，下一條 media1 連線再傳）</summary>
    private void CloseThumbPull()
    {
        CancelTimer(ref thumbTimer);
        thumbPullGeneration = -1;
        thumbPullNext = null;
    }

    /// <summary>引擎停止：縮圖的佇列、計時器、ThumbnailService 與 HTTP 用戶端一起收掉</summary>
    private void CloseThumbs()
    {
        CloseThumbPull();
        thumbs?.Dispose();
        thumbs = null;
    }
}
