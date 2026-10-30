namespace Myriagent.Sync;

// WP4.7（媒體、檔案與上傳）步 12：複本（其他電腦的對話）在這台顯示時要的同步那一側（D38、D39）。
// - 第一次協商出 media1 時建 ReplicaMediaSource（E9 的代理）、掛到對話的盒子上（中樞與 AgentWebView 經發布者取用）；引擎停止時拿下來、Dispose。
// - 複本的 blob 預取：拉到一頁、解碼之前（PullPageAsync 呼叫 PrefetchReplicaBlobsAsync），把這一頁要的串流裡其他電腦的 conv.item 的 blob 以 E6 取回
//   （每頁同時 ≤4 個、合計 ≤32 MB）；PlanConversationApply 以取回的內容解碼並存進 replica_items。暫時取不到或這一頁放不下的，那個串流從那筆起停住、
//   游標不越過它：放不下的馬上再拉下一頁，暫時取不到的 30 秒後再拉（新的連線本來就會重拉打開的串流）。
// 只有協商出 media1 的連線才取；沒有 media1 的連線（伺服器回滾）要取的那筆起一律停住、不排重試，等協商出 media1 的連線重拉（合併前審查 DI-3、INT-6）。
// 狀態只在引擎執行緒改（HTTP 在背景）
internal sealed partial class SyncEngine
{
    // ── 以下只在引擎執行緒讀寫 ──
    private ReplicaMediaSource? replicaMedia;
    private ReplicaBlobPrefetcher? replicaPrefetcher;
    private ITimer? replicaBlobRetryTimer;

    /// <summary>這一頁的預取結果（<see cref="ReplicaBlobPage"/>）與它屬於哪一頁（換頁、放棄的頁的結果不用）</summary>
    private (ReplicaPullPage Page, ReplicaBlobPage Result)? replicaBlobs;

    /// <summary>暫時取不到 blob 而停住的複本（對話 id）：<see cref="ReplicaBlobRules.RetryAfter"/> 之後重拉</summary>
    private readonly HashSet<string> replicaBlobRetry = new(StringComparer.Ordinal);

    /// <summary>測試接縫：複本的 blob 預取（null＝正式的 E6）。啟動前設定</summary>
    internal IReplicaBlobPrefetcher? ReplicaBlobPrefetcherForTest { get; set; }

    /// <summary>測試接縫：每頁取回的 blob 合計上限（預設 <see cref="ReplicaBlobRules.MaxPageBytes"/>；測試用小的內容量出分頁）。啟動前設定</summary>
    internal long ReplicaBlobMaxPageBytesForTest { get; set; } = ReplicaBlobRules.MaxPageBytes;

    /// <summary>協商出 media1（OnProvenForMedia）：建 E9 的代理、掛到盒子上</summary>
    private void OpenReplicaMedia()
    {
        if (Conversations is not { } box || stopping)
            return;
        replicaMedia ??= new ReplicaMediaSource(
            new DeviceHttpClient(server, pairedServer, args.DevTrustEnv, accessTokens, RefreshAccessTokenForHttpAsync, time, MediaHttpHandlerForTest), time);
        box.ReplicaMedia = replicaMedia;
    }

    /// <summary>引擎停止（CloseMedia）：從盒子拿下來（只拿自己掛的）、取消進行中的請求與預取</summary>
    private void CloseReplicaMedia()
    {
        CancelTimer(ref replicaBlobRetryTimer);
        replicaBlobRetry.Clear();
        replicaBlobs = null;
        if (replicaMedia is { } source)
        {
            Conversations?.ClearReplicaMedia(source);
            source.Dispose();
            replicaMedia = null;
        }

        replicaPrefetcher?.Dispose();
        replicaPrefetcher = null;
    }

    /// <summary>
    /// 拉到一頁（化簡之後、解碼之前；PullPageAsync 呼叫）：這一頁要的串流裡、其他電腦寫的、不是本機對話的 conv.item 的 blob 取回記憶體。
    /// 沒有複本的頁、沒有 blob 的頁不等待（同步完成）。沒協商出 media1 時不取：帶 blob 參照的那筆起那個串流停住（不排 30 秒的重試，
    /// 協商出 media1 的連線本來就重拉打開的串流；合併前審查 DI-3、INT-6）。連線斷了、引擎停止時直接返回（呼叫端照 IsCurrent 結束這一頁）
    /// </summary>
    private async Task PrefetchReplicaBlobsAsync(SyncConnection conn, IReadOnlyList<PulledEvent> events)
    {
        replicaBlobs = null;
        if (events.Count == 0 || replicaPage is not { } page || !ReferenceEquals(page.Owner, conn) || !ReplicasReady(conn))
            return;
        var streams = page.Requested.Where(c => c?.Stream is not null).Select(c => c.Stream!).ToHashSet(StringComparer.Ordinal);
        if (streams.Count == 0)
            return;
        var self = device!.Current.DeviceId;
        var plan = ReplicaBlobRules.Plan(events, e => streams.Contains(e.Stream) && !string.Equals(e.Author, self, StringComparison.Ordinal)
                                                      && e.ConversationId is { } id && !IsLocalConversation(id), ReplicaBlobMaxPageBytesForTest);
        if (plan.Wants.Count == 0 && plan.BudgetHolds.Count == 0)
            return;
        if (!DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Media1))
        {
            // 伺服器回滾到 WP4.7 之前：照永久取不到前進的話，存進 replica_items 的是參照版本、游標越過它，伺服器再升級回來也不會回頭重取
            replicaBlobs = (page, new ReplicaBlobPage(ReplicaBlobPage.Empty.Resolve, ReplicaBlobRules.HoldsWithoutMedia(plan)));
            return;
        }

        IReadOnlyDictionary<string, ReplicaBlobFetch> results = new Dictionary<string, ReplicaBlobFetch>(StringComparer.Ordinal);
        if (plan.Wants.Count > 0)
        {
            var prefetcher = ReplicaBlobPrefetcherForTest ?? (replicaPrefetcher ??= ReplicaBlobPrefetcher.Create(
                new DeviceHttpClient(server, pairedServer, args.DevTrustEnv, accessTokens, RefreshAccessTokenForHttpAsync, time, MediaHttpHandlerForTest), time));
            try
            {
                results = await prefetcher.FetchAsync(plan.Wants, conn.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!IsCurrent(conn) || !ReferenceEquals(replicaPage, page))
            return;
        replicaBlobs = (page, new ReplicaBlobPage(ReplicaBlobRules.Resolver(results), ReplicaBlobRules.Holds(plan, results)));
    }

    /// <summary>PlanConversationApply 用：這一頁的預取結果（取走，之後不再佔記憶體）；沒有預取的頁是 <see cref="ReplicaBlobPage.Empty"/></summary>
    private ReplicaBlobPage TakeReplicaBlobs(ReplicaPullPage page)
    {
        var taken = replicaBlobs is { } blobs && ReferenceEquals(blobs.Page, page) ? blobs.Result : ReplicaBlobPage.Empty;
        replicaBlobs = null;
        return taken;
    }

    /// <summary>這一頁要套用的對話事件：停住的串流裡、停住那筆起只經串流拉到的拿掉（meta 類照常）</summary>
    private static IReadOnlyList<PulledEvent> ReplicaEventsToApply(ReplicaPullPage page, ReplicaBlobPage blobs, IReadOnlyList<PulledEvent> events)
        => ReplicaBlobRules.WithoutHeld(events, blobs.Holds, page.Requested.Where(c => c?.Stream is null && c?.Cls is not null).Select(c => c.Cls!).ToHashSet(StringComparer.Ordinal));

    /// <summary>
    /// 計畫好這一頁之後（引擎執行緒，EndConversationPage 之前）：這一頁放不下的串流馬上再拉（這一頁還在拉它，重排之後 EndPage 留著它）；
    /// 暫時取不到的這一頁結束後先不排回去（伺服器還有 more 也一樣），30 秒後再拉——沒有 media1 的連線不排（怎麼拉都取不到；新的連線、
    /// 新的 item 通知照樣會拉）
    /// </summary>
    private void AfterReplicaBlobPlan(SyncConnection conn, ReplicaBlobPage blobs)
    {
        var media = DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Media1);
        foreach (var hold in blobs.Holds.Values)
        {
            if (hold.Transient)
            {
                replicas.Defer(hold.ConversationId);
                if (media)
                    replicaBlobRetry.Add(hold.ConversationId);
            }
            else
            {
                replicas.Repend(hold.ConversationId);
            }
        }

        if (replicaBlobRetry.Count == 0 || replicaBlobRetryTimer is not null)
            return;
        replicaBlobRetryTimer = CreateTimer(ReplicaBlobRules.RetryAfter, () =>
        {
            CancelTimer(ref replicaBlobRetryTimer);
            var ids = replicaBlobRetry.ToList();
            replicaBlobRetry.Clear();
            if (stopping || current is not { } live || !IsCurrent(live) || !live.Proven || !ReplicasReady(live))
                return;
            foreach (var id in ids)
                replicas.Repend(id);
            RequestPull(live);
        });
    }
}
