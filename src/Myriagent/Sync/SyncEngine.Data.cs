using System.Globalization;
using Myriagent.Core;
using Myriagent.Usage;

namespace Myriagent.Sync;

// 收件匣、outbox 的送出與確認、拉取、對帳、共享用量（sync-protocol 5、6、9）
internal sealed partial class SyncEngine
{
    // 用量（引擎執行緒）：本機最後一次觀察到的快照（共享用量要不要蓋目前值看它的來源）、上次上傳的時間
    private readonly Dictionary<string, UsageSnapshot> observed = new(StringComparer.Ordinal);

    /// <summary>伺服器換了實例（BeginServerChange）：下一次在 UI 執行緒套用時以 reset 送 remoteOnly 的共享用量（即使沒有新的），清掉換實例前收的</summary>
    private bool remoteUsageReset;
    private readonly Dictionary<string, DateTimeOffset> lastTermedFetchedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> lastSessionUploadAt = new(StringComparer.Ordinal);

    // 發布側（引擎執行緒）：最後一次收到的本機帳戶、CLI 與 app 更新狀態的快照。目錄換了（連結、detached、伺服器換實例、全部重送）時拿它們重算一次
    private IReadOnlyList<LocalAccountSnapshot>? lastAccounts;
    private bool lastAccountsReadOnly;
    private CliStatePayload? lastCli;
    private AppStatePayload? lastApp;

    // ── 收件匣 ──

    /// <summary>
    /// 一次取完收件匣：快照類（帳戶、CLI）更新後重算發布、修改類照順序算出要送的事件，outbox 的新增合成一個交易（一次 fsync）；
    /// 寫入成功之後才記 published 摘要（寫入失敗時下次照樣再送）
    /// </summary>
    private void ProcessInbox()
    {
        if (!opened)
            return;
        var signals = inbox.TakeAll();
        if (signals.Count == 0 || store is null || stopping)
            return;
        var appends = new List<OutboxAppend>();
        var republish = false;
        foreach (var signal in signals)
        {
            switch (signal)
            {
                case SyncSignal.Append append:
                    appends.Add(append.Item);
                    break;
                case SyncSignal.FullResync:
                    ClearPublished();
                    republish = true;
                    break;
                case SyncSignal.UsageObserved usage:
                    OnUsageObserved(usage);
                    break;
                case SyncSignal.Accounts accounts:
                    lastAccounts = accounts.Snapshot;
                    lastAccountsReadOnly = accounts.ListReadOnly;
                    republish = true;
                    break;
                case SyncSignal.Cli cli:
                    lastCli = cli.State;
                    republish = true;
                    break;
                case SyncSignal.App app:
                    lastApp = app.State;
                    republish = true;
                    break;
                case SyncSignal.AccountEdited edited:
                    if (catalog!.PlanAccountEdited(edited.LocalAccountId, edited.Label, edited.Color, PlanView(appends)) is { } update)
                        appends.Add(update);
                    break;
                case SyncSignal.AccountsReordered reordered:
                    appends.AddRange(catalog!.PlanReorder(reordered.LocalOrder, PlanView(appends)));
                    break;
                case SyncSignal.PrefChanged pref:
                    if (catalog!.PlanPrefChanged(pref.Key, pref.Value, PlanView(appends)) is { } prefs)
                        appends.Add(prefs);
                    break;
                case SyncSignal.DeviceRenamed renamed:
                    if (catalog!.PlanDeviceRenamed(renamed.DeviceId, renamed.Name, trust!.Registry, PlanView(appends)) is { } rename)
                        appends.Add(rename);
                    else
                        Log.Info("裝置改名沒有送出（名稱不合格、裝置不在目錄裡或已撤銷、或名稱沒變）");
                    break;
                default:
                    // 後續工作包自己定義的訊號（對話變化等，SyncEngine.Contracts.cs）；沒人處理才是不認得的
                    if (!TryHandleConversationSignal(signal, appends))
                        Log.Warn($"不認得的同步訊號：{signal.GetType().Name}");
                    break;
            }
        }

        var plan = republish ? PlanSnapshots() : PublishPlan.Empty;
        Write(new PublishPlan([.. plan.Appends, .. appends], plan.Published));
    }

    /// <summary>
    /// 修改類事件規劃時看的 outbox（含這一輪收件匣準備要寫的）：合併鍵還沒送出的列（改回目錄的原值時要寫一筆蓋掉它；
    /// account.update 把它的欄位併進新的一筆），與已送出、結果還沒拉回的筆數（baseVersion 要加上它們）
    /// </summary>
    private OutboxPlanView PlanView(List<OutboxAppend> batch) => new(key => PendingJson(batch, key), UnpulledCount);

    private string? PendingJson(List<OutboxAppend> batch, string key)
    {
        for (var i = batch.Count - 1; i >= 0; i--)
        {
            if (batch[i].CoalesceKey == key)
                return batch[i].Json;
        }

        var entry = outbox.LastOrDefault(e => e.State == OutboxState.Pending && e.CoalesceKey == key);
        if (entry is null)
            return null;
        // 自己寫的列一定解得開；萬一解不開仍算「有沒送出的列」（不帶欄位）
        return PayloadRules.TryDecode(entry.Enc, entry.Payload, MaxOwnPayloadBytes, out var json) ? json : "{}";
    }

    /// <summary>
    /// 這個合併鍵已送出、伺服器的結果還沒拉回的列：在途或要重送的（Sent、Retry），與已提交而且伺服器的 seq 還沒被目錄游標越過的
    /// （伺服器物化後發的 account.upsert／prefs.set 緊接在裝置事件之後，游標越過裝置事件的 seq 才可能拉到它）。
    /// 被拒的、結果不明（Prove 對齊時標成已提交、沒有 seq）的不算
    /// </summary>
    private int UnpulledCount(string key)
    {
        var cursor = Cursor(SyncClasses.Catalog);
        return outbox.Count(e => e.CoalesceKey == key
            && (e.State is OutboxState.Sent or OutboxState.Retry || (e.State == OutboxState.Acked && e.ServerSeq is { } seq && seq >= cursor)));
    }

    /// <summary>解自己寫進 outbox 的列的上限（實際受伺服器的 maxInlinePayload 限制，遠小於這個值）</summary>
    private const int MaxOwnPayloadBytes = 16 << 20;

    /// <summary>以最後的帳戶、CLI 與 app 快照重算要發布的（目錄換了、published 清掉之後）</summary>
    private void Republish()
    {
        if (store is null || stopping || catalog is null)
            return;
        Write(PlanSnapshots());
    }

    private PublishPlan PlanSnapshots()
    {
        if (lastAccounts is null && lastCli is null && lastApp is null)
            return PublishPlan.Empty;
        IReadOnlyDictionary<string, PublishedLink> published;
        try
        {
            published = store!.LoadPublished();
        }
        catch (Exception ex)
        {
            Log.Error("讀取 published 摘要失敗", ex);
            return PublishPlan.Empty;
        }

        var prov1 = CanPublishProv1();
        var plan = lastAccounts is null ? PublishPlan.Empty : catalog!.PlanAccounts(lastAccounts, lastAccountsReadOnly, published, prov1);
        if (lastCli is not null)
            plan = plan.Concat(CatalogSync.PlanCli(CliStateRules.ForServer(lastCli, prov1), published));
        return lastApp is null ? plan : plan.Concat(CatalogSync.PlanApp(lastApp, published));
    }

    /// <summary>寫進 outbox（一個交易），成功才記 published（一個交易）</summary>
    private void Write(PublishPlan plan)
    {
        if (plan.Forget.Count > 0)
        {
            try
            {
                store!.SetPublishedMany(plan.Forget.Select(key => (key, (PublishedLink?)null)).ToList());
            }
            catch (Exception ex)
            {
                // 下一次規劃再忘一次（摘要留著只會讓 prov1 回來時少送一次，下一次帳戶狀態改變就會送）
                Log.Error("忘掉延後帳戶的 published 摘要失敗", ex);
            }
        }

        // 這一輪收件匣的對話事件的發布狀態（SyncEngine.Conversations.cs）與 outbox 的列同一個交易寫（WP4.3 計畫 D11）
        if (plan.Appends.Count == 0 || !AppendOutbox(plan.Appends, TakeConversationPass()) || plan.Published.Count == 0)
            return;
        try
        {
            store!.SetPublishedMany(plan.Published);
        }
        catch (Exception ex)
        {
            // 下次快照會再送一次同樣的內容（伺服器照常套用，結果相同）
            Log.Error("記錄 published 摘要失敗", ex);
        }
    }

    private bool AppendOutbox(IReadOnlyList<OutboxAppend> items, ConversationPass? conversations = null)
    {
        IReadOnlyList<OutboxEntry> written;
        var scrubbed = new List<OutboxEntry>();
        try
        {
            written = store!.AppendMany(items, NowMs(), conversations?.ToWrite(), scrubbed);
        }
        catch (Exception ex)
        {
            Log.Error($"寫入 outbox 失敗（這 {items.Count} 筆沒有送出）", ex);
            ConversationWriteFailed(conversations, items);
            return false;
        }

        // 政策降級改過的待送列（WP4.3 合併前審查 P1）先接回記憶體，再接這一批寫的
        foreach (var entry in scrubbed)
            Upsert(entry);
        ConversationWriteCommitted(conversations);
        foreach (var entry in written)
            Upsert(entry);
        UpdateStatus();
        if (current is { Proven: true } conn)
            ScheduleSend(conn);
        return true;
    }

    private void ClearPublished()
    {
        try
        {
            store!.ClearPublished();
        }
        catch (Exception ex)
        {
            Log.Error("清除 published 摘要失敗", ex);
        }
    }

    // ── outbox 的記憶體副本（依 deviceSeq 排序，和 sync.db 一致） ──
    //
    // 瘦身（WP4.3 計畫 D31、步 9）：已完成的對話列只留骨架（OutboxSliceRules.Skeleton；要重送時 SendNext 先從 sync.db 讀回），
    // BatchAck 只處理在途的尾段；第一個未完成的列從 outboxDoneHint 起找。第 3 階段的列不換骨架，行為不變

    /// <summary>
    /// 這個位置之前的列都已完成：找第一個未完成的列從這裡起，確認的成本不跟著保留的列數長。
    /// 只在載入、<see cref="CommitOutbox"/>（換掉尾段）、<see cref="Prune"/>（刪掉最前面的列）、<see cref="Upsert"/>（插在它前面）時改
    /// </summary>
    private int outboxDoneHint;

    /// <summary>找第一個未完成的列與確認的尾段走過的 outbox 列數（測試看它不跟著保留的列數長）</summary>
    private long outboxWork;

    /// <summary>骨架共用的串流與對話 id（同一個對話的列共用一份字串）；太多就清掉重來（只影響記憶體）</summary>
    private readonly Dictionary<string, string> sharedOutboxStrings = new(StringComparer.Ordinal);

    private const int MaxSharedOutboxStrings = 65_536;

    private string ShareOutboxString(string value)
    {
        if (sharedOutboxStrings.TryGetValue(value, out var shared))
            return shared;
        if (sharedOutboxStrings.Count >= MaxSharedOutboxStrings)
            sharedOutboxStrings.Clear();
        sharedOutboxStrings[value] = value;
        return value;
    }

    /// <summary>第一個未完成的列（從 <see cref="outboxDoneHint"/> 起找，找到的位置記回去）；全部完成回 <c>Count</c></summary>
    private int FirstActive()
    {
        var start = Math.Min(outboxDoneHint, outbox.Count);
        var first = OutboxSliceRules.FirstActiveIndex(outbox, start);
        outboxWork += first - start;
        outboxDoneHint = first;
        return first;
    }

    private void Upsert(OutboxEntry entry)
    {
        var index = IndexOf(entry.DeviceSeq);
        if (index >= 0)
            outbox[index] = entry;
        else
            outbox.Insert(~index, entry);
        // 新的列都接在最後、改的都是未完成的列（在提示之後）；萬一不是，提示退回這一列
        var at = index >= 0 ? index : ~index;
        if (at < outboxDoneHint)
            outboxDoneHint = at;
    }

    private int IndexOf(long deviceSeq)
    {
        int lo = 0, hi = outbox.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var seq = outbox[mid].DeviceSeq;
            if (seq == deviceSeq)
                return mid;
            if (seq < deviceSeq)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return ~lo;
    }

    /// <summary>
    /// ApplyProve／ApplyAck 對 <paramref name="from"/> 起的尾段算出的結果（同樣長度與順序；ApplyProve 是整份）：改變的列寫回 sync.db（一個交易），
    /// 成功才接回記憶體（<see cref="OutboxSliceRules.Splice"/>），之後把尾段裡已完成的對話列換成骨架——寫入失敗就什麼都不換
    /// </summary>
    private void CommitOutbox(IReadOnlyList<OutboxEntry> updated, int from = 0)
    {
        if (from < 0 || from + updated.Count != outbox.Count)
            throw new InvalidOperationException($"outbox 的尾段不對：從 {from} 起 {updated.Count} 列，outbox 有 {outbox.Count} 列");
        var changed = new List<OutboxEntry>();
        var firstActive = -1;
        for (var i = 0; i < updated.Count; i++)
        {
            if (updated[i].DeviceSeq != outbox[from + i].DeviceSeq)
                throw new InvalidOperationException($"outbox 的尾段順序不對：第 {from + i} 列是 {outbox[from + i].DeviceSeq}，結果是 {updated[i].DeviceSeq}");
            if (!Equals(updated[i], outbox[from + i]))
                changed.Add(updated[i]);
            if (firstActive < 0 && !updated[i].IsDone)
                firstActive = i;
        }

        store!.SaveOutbox(changed);
        OutboxSliceRules.Splice(outbox, from, updated);
        outboxDoneHint = from + (firstActive < 0 ? updated.Count : firstActive);
        for (var i = from; i < outbox.Count; i++)
        {
            var entry = outbox[i];
            if (OutboxSliceRules.CanSkeleton(entry) && !OutboxSliceRules.IsSkeleton(entry))
                outbox[i] = OutboxSliceRules.Skeleton(entry, ShareOutboxString);
        }
    }

    /// <summary>ApplyAck 只對在途的尾段（<see cref="OutboxSliceRules.AckStart"/> 起；結果與整份處理逐筆相同）</summary>
    private (int From, IReadOnlyList<OutboxEntry> Updated, OutboxVerdict Verdict) ApplyAckToTail(BatchAck ack, long nowMs)
    {
        FirstActive();
        var from = OutboxSliceRules.AckStart(outbox, ack, outboxDoneHint);
        var tail = outbox.GetRange(from, outbox.Count - from);
        outboxWork += tail.Count;
        var (updated, verdict) = OutboxRules.ApplyAck(tail, ack, nowMs);
        return (from, updated, verdict);
    }

    private bool HasSendable()
    {
        for (var i = FirstActive(); i < outbox.Count; i++)
        {
            if (outbox[i].State is OutboxState.Pending or OutboxState.Retry)
                return true;
        }

        return false;
    }

    /// <summary>還沒完成的列數（狀態列的待送筆數）</summary>
    private int CountNotDone()
    {
        var count = 0;
        for (var i = FirstActive(); i < outbox.Count; i++)
        {
            if (!outbox[i].IsDone)
                count++;
        }

        return count;
    }

    /// <summary>
    /// 送出前把下一批可能選到的骨架從 sync.db 讀回內容（最多一批的筆數，<see cref="OutboxSliceRules.ReloadNeeded"/>）。
    /// 讀不回（sync.db 被鎖）照第 3 階段的本機失敗處理：斷線、照退避重連後再試（R19）
    /// </summary>
    private bool ReloadSkeletons(SyncConnection conn)
    {
        var needed = OutboxSliceRules.ReloadNeeded(outbox, limits.MaxBatchEvents, FirstActive());
        if (needed.Count == 0)
            return true;
        IReadOnlyList<OutboxEntry> stored;
        try
        {
            stored = store!.LoadOutboxPayloads(needed);
        }
        catch (Exception ex)
        {
            Log.Error($"outbox 有 {needed.Count} 筆要重送，從同步資料庫讀回內容失敗", ex);
            Fail(conn, LocalFailure);
            return false;
        }

        foreach (var entry in stored)
        {
            var index = IndexOf(entry.DeviceSeq);
            outbox[index] = OutboxSliceRules.Restore(outbox[index], entry);
        }

        return true;
    }

    // ── 送出 ──

    /// <summary>排一次送出：批次之間至少隔 BatchInterval（sync-protocol 5.1）；已經排了、正在送就不重排</summary>
    private void ScheduleSend(SyncConnection conn)
    {
        if (!IsCurrent(conn) || !conn.Proven || conn.UpdateOnly || conn.Sending || sendTimer is not null || !HasSendable())
            return;
        var delay = SyncTimingRules.SendDelay(lastBatchAt, time.GetUtcNow(), options.BatchInterval, SyncTimingRules.BatchCoalesce);
        var token = ++sendToken;
        sendTimer = CreateTimer(delay, () =>
        {
            if (token != sendToken)
                return;
            CancelTimer(ref sendTimer);
            SendNext(conn);
        });
    }

    /// <summary>
    /// 送一批：NextBatch 選出的列先標成在途、寫回 sync.db，之後才 PublishBatch（送過的內容凍結，OutboxRules.MarkSent）。
    /// 在途最多 Hello 給的批數；這一批送出後還有待送的就排下一次
    /// </summary>
    private void SendNext(SyncConnection conn)
    {
        if (!IsCurrent(conn) || !conn.Proven || conn.UpdateOnly || conn.Sending)
            return;
        if (!ReloadSkeletons(conn))
            return;
        // 沒有 conv1 的連線：待送的對話列先換成 {}（WP4.3 計畫 D13；SyncEngine.Conversations.cs）
        if (!ScrubConversationRowsIfNeeded(conn))
            return;
        // 有 conv1、沒有 prov1 的連線：Grok、Antigravity 的對話的待送列記成要換成 {}，下一步一起補寫（SyncEngine.Prov1.cs）
        OweProv1Scrub(conn);
        // 寫入失敗留下的政策降級清除先補寫，補不成就不送（WP4.3 第二輪合併前審查 PRIV-4、F1）
        if (!PayScrubOwed(conn))
            return;
        // 第一個未完成的列之前都已完成（不會被選進批次）：只把尾段交給 NextBatch，不必每次從頭掃
        var active = FirstActive();
        var batch = OutboxRules.NextBatch(outbox.GetRange(active, outbox.Count - active), inFlight.Count, limits);
        // 這次啟動還沒看過中繼資料的對話的待送列：先要它現在的政策、處理完才送（WP4.3 第二輪合併前審查 F1）
        var allowed = ConversationSendLimit(conn, batch);
        if (allowed < batch.Count)
            batch = batch.Take(allowed).ToList();
        if (batch.Count == 0)
            return;
        if (batch.Any(OutboxSliceRules.IsSkeleton))
        {
            Log.Error("要送出的列還是骨架、沒有讀回內容（程式錯誤）：這一批不送");
            Fail(conn, LocalFailure);
            return;
        }

        var sent = batch.Select(OutboxRules.MarkSent).ToList();
        try
        {
            store!.SaveOutbox(sent);
        }
        catch (Exception ex)
        {
            Log.Error("outbox 標成在途時寫入失敗", ex);
            Fail(conn, LocalFailure);
            return;
        }

        foreach (var entry in sent)
            Upsert(entry);
        var batchId = SyncIds.NewUlid(NowMs());
        var wasIdle = inFlight.Count == 0;
        inFlight[batchId] = new InFlightBatch(sendGeneration, sent[0].DeviceSeq, sent[^1].DeviceSeq);
        lastBatchAt = time.GetUtcNow();
        if (wasIdle)
            RestartAckTimer(conn);
        conn.Sending = true;
        var events = sent.Select(e => e.ToEvent()).ToList();
        Spawn(async () =>
        {
            try
            {
                await conn.Session!.PublishBatchAsync(new Batch(batchId, events), conn.Token);
            }
            catch (SyncTransportException ex)
            {
                conn.Sending = false;
                Fail(conn, ex.Failure);
                return;
            }

            conn.Sending = false;
            ScheduleSend(conn);
        }, conn);
    }

    /// <summary>有在途批次時才計時：AckTimeout 內沒收到 BatchAck 就斷線重連</summary>
    private void RestartAckTimer(SyncConnection conn)
    {
        CancelTimer(ref ackTimer);
        var token = ++ackToken;
        if (inFlight.Count == 0)
            return;
        ackTimer = CreateTimer(options.AckTimeout, () =>
        {
            if (token != ackToken || !IsCurrent(conn) || inFlight.Count == 0)
                return;
            CancelTimer(ref ackTimer);
            Log.Warn($"BatchAck 超過 {options.AckTimeout.TotalSeconds:0} 秒沒來：斷線重連");
            Fail(conn, AckTimeoutFailure);
        });
    }

    private void OnBatchAck(SyncConnection conn, BatchAck ack)
    {
        if (ack?.BatchId is null || !inFlight.Remove(ack.BatchId, out var info))
        {
            Log.Warn($"收到不認得的 BatchAck（{CatalogSyncRules.CleanText(ack?.BatchId, 64)}）：忽略");
            return;
        }

        RestartAckTimer(conn);
        var nowMs = NowMs();
        switch (SendGenerationRules.OnAck(info.Generation, sendGeneration, ack))
        {
            case BatchAckAction.DropStale:
                // 舊送出世代的 gap：這幾列已經被上一次 Rewind 改回重送，不再 Rewind
                break;
            case BatchAckAction.Stop:
            {
                var (_, _, verdict) = ApplyAckToTail(ack, nowMs);
                Log.Warn($"伺服器整批拒收（{ack.Error?.Code}）：停止同步");
                Stop(SyncFailureRules.StopReasonFor(verdict) ?? SyncStopReason.InvalidBatch);
                return;
            }

            case BatchAckAction.Reconnect:
            {
                var (from, updated, _) = ApplyAckToTail(ack, nowMs);
                CommitOutbox(updated, from);
                AfterBatchResults(conn, ack);
                Log.Warn($"伺服器回應不認得的批次錯誤（{CatalogSyncRules.CleanText(ack.Error?.Code, 64)}）：斷線重連");
                Fail(conn, new SyncFailure(SyncFailureKind.Transient, ack.Error?.Code, "伺服器回應不認得的批次錯誤"));
                return;
            }

            case BatchAckAction.Apply:
            {
                var (from, updated, verdict) = ApplyAckToTail(ack, nowMs);
                if (SyncFailureRules.StopReasonFor(verdict) is { } reason)
                {
                    Stop(reason);
                    return;
                }

                CommitOutbox(updated, from);
                AfterBatchResults(conn, ack);
                if (verdict == OutboxVerdict.Reconnect)
                {
                    Fail(conn, new SyncFailure(SyncFailureKind.Transient, ack.Error?.Code, "伺服器回應不完整的批次錯誤"));
                    return;
                }

                if (ack.Error is not null)
                {
                    // gap：從 expected 起重送；之後回來的舊世代 gap 只減在途數（D10）
                    sendGeneration++;
                    Log.Info($"批次跳號（gap）：從 deviceSeq {ack.Error.Expected} 起重送");
                }

                break;
            }

            default:
                throw new InvalidOperationException("不認得的 BatchAck 處理");
        }

        Prune();
        CheckReconcileDone(conn);
        CheckLive(conn);
        UpdateStatus();
        ScheduleSend(conn);
    }

    /// <summary>修剪：最前面連續一段已完成滿 24 小時的列。對帳期間不修剪（伺服器只在第一個跳號接受缺口，D15 第 5 步）</summary>
    private void Prune()
    {
        if (store is null || store.IsReconciling)
            return;
        var prunable = OutboxRules.Prunable(outbox, NowMs());
        if (prunable.Count == 0)
            return;
        store.DeleteOutbox(prunable);
        outbox.RemoveRange(0, prunable.Count);
        outboxDoneHint = Math.Max(0, outboxDoneHint - prunable.Count);
    }

    /// <summary>D15 第 4 步：對帳開始時記下的序號以前的列都完成、而且這條連線拉到 more:false → 寫 seen_server、reconcile=0</summary>
    private void CheckReconcileDone(SyncConnection conn)
    {
        if (store is not { IsReconciling: true } s || !IsCurrent(conn) || !conn.Proven || !conn.PulledToEnd)
            return;
        var through = s.ReconcileThroughSeq ?? 0;
        if (outbox.Any(e => e.DeviceSeq <= through && !e.IsDone))
            return;
        s.CompleteServerChange();
        Log.Info($"伺服器換實例後的對帳完成（{s.SeenServer?.InstanceId}／第 {s.SeenServer?.RestoreGeneration} 次還原）");
        Prune();
    }

    /// <summary>Draining → Live：拉到 more:false、沒有在途批次、沒有待送的列</summary>
    private void CheckLive(SyncConnection conn)
    {
        if (state != SyncEngineState.Draining || !IsCurrent(conn) || !conn.Proven || !conn.PulledToEnd || conn.Pulling
            || inFlight.Count > 0 || HasSendable())
            return;
        var now = time.GetUtcNow();
        liveSince = now;
        disconnectedSince = null;
        lastSyncAt = now.ToUnixTimeMilliseconds();
        try
        {
            store!.SetKv(SyncStoreSchema.LastSyncAt, lastSyncAt.Value.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Log.Warn($"記錄最後同步時間失敗：{ex.Message}");
        }

        SetState(SyncEngineState.Live);
        AfterOnline(conn);
    }

    // ── 拉取 ──

    private void RequestPull(SyncConnection conn)
    {
        if (!IsCurrent(conn) || !conn.Proven || conn.UpdateOnly)
            return;
        conn.PullRequested = true;
        if (conn.Pulling)
            return;
        conn.Pulling = true;
        Spawn(() => PullLoopAsync(conn), conn);
    }

    private async Task PullLoopAsync(SyncConnection conn)
    {
        try
        {
            while (conn.PullRequested && IsCurrent(conn))
            {
                conn.PullRequested = false;
                bool more;
                do
                {
                    if (await PullPageAsync(conn) is not { } hasMore)
                        return;
                    more = hasMore;
                }
                while (more);

                conn.PulledToEnd = true;
            }
        }
        finally
        {
            conn.Pulling = false;
            // 對話的複本（WP4.3）：沒寫完的頁不再寫，延後的中樞要求這時處理（合併前審查 R2）
            AbandonConversationPage(conn);
        }

        CheckReconcileDone(conn);
        CheckLive(conn);
    }

    /// <summary>
    /// 拉一頁：化簡 → 在 UI 執行緒套用目錄與共享用量 → 套用完才把目錄快取與游標提交進 sync.db（套用丟例外就不前進）。
    /// 回傳 more；斷線、停止、套用失敗時回 null
    /// </summary>
    private async Task<bool?> PullPageAsync(SyncConnection conn)
    {
        var pullCursors = new List<PullCursor> { new(Cursor(SyncClasses.Catalog), SyncClasses.Catalog), new(Cursor(SyncClasses.Usage), SyncClasses.Usage) };
        CollectPullCursors(pullCursors);
        var request = new PullRequest(pullCursors, limits.MaxPullEvents, limits.MaxPullBytes);
        PullResponse response;
        try
        {
            response = await conn.Session!.PullAsync(request, conn.Token);
        }
        catch (SyncTransportException ex)
        {
            Fail(conn, ex.Failure);
            return null;
        }

        if (!IsCurrent(conn))
            return null;
        if (response is null)
        {
            Fail(conn, BadResponse);
            return null;
        }

        var events = response.Events ?? [];
        var delta = PullReduceRules.Reduce(events, PullReduceRules.ServerPayloadLimit(limits));
        if (delta.IgnoredKinds.Count > 0)
        {
            var kinds = string.Join("、", delta.IgnoredKinds.Select(k => CatalogSyncRules.CleanText(k, 64) ?? "?"));
            pullWarning = $"略過了不認得或解不開的伺服器事件（{kinds}）";
            Log.Warn($"拉取略過了不認得或解不開的事件：{kinds}");
        }

        var page = catalog!.Prepare(delta, trust!.Registry, lastAccounts?.Select(a => a.Id).ToList());
        var (usage, remoteUsage) = SharedUsageFor(delta, page);
        var resetRemote = remoteUsageReset;
        var catalogChanged = delta.Accounts.Count > 0 || delta.Devices.Count > 0 || delta.Revoked.Count > 0 || delta.Prefs.Count > 0;
        // 對話事件（WP4.3 的登記點，SyncEngine.Contracts.cs）：UI 上的部分與目錄同一次套用，其餘在提交之後（對話游標照整頁前進，步 14）
        BeginConversationPage(events);
        // WP4.7 步 12：複本的 blob 在解碼這一頁之前取回（D39；沒有要取的頁不等待）
        await PrefetchReplicaBlobsAsync(conn, delta.Conversations);
        if (!IsCurrent(conn))
            return null;
        var conversationApply = CollectConversationApply(delta.Conversations);
        var conversationUi = conversationApply is { Ui.Count: > 0 } ? conversationApply.Value.Ui : null;
        if (catalogChanged || usage.Count > 0 || remoteUsage.Count > 0 || resetRemote || conversationUi is not null)
        {
            try
            {
                await args.Host.RunOnUiAsync(() =>
                {
                    if (catalogChanged)
                        args.Applier.ApplyCatalog(page.Apply);
                    if (usage.Count > 0)
                        args.Applier.ApplySharedUsage(usage);
                    if (remoteUsage.Count > 0 || resetRemote)
                        args.Applier.ApplyRemoteUsage(remoteUsage, resetRemote);
                    if (conversationUi is not null)
                    {
                        foreach (var action in conversationUi)
                            action();
                    }
                }, conn.Token);
                // 套用成功才放下旗標（套用失敗、這頁重拉時再帶一次 reset）
                if (resetRemote)
                    remoteUsageReset = false;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Log.Error("在 UI 執行緒套用伺服器的目錄或用量失敗（游標不前進，下次重拉）", ex);
                pullWarning = "套用伺服器的資料失敗，稍後重試";
                UpdateStatus();
                return null;
            }

            if (!IsCurrent(conn))
                return null;
        }

        // v1 的 cursors 只放第 3 階段的類別，對話的游標在 conversation_cursors（步 14；沒有盒子的引擎照 WP4.1 的做法）
        var next = Conversations is null ? PullReduceRules.AdvanceEveryClass(cursors, events) : PullReduceRules.AdvanceCursors(cursors, events);
        store!.CommitPull(delta, next);
        cursors = next;
        catalog.Accept(page);
        var nowMs = NowMs();
        foreach (var logical in page.Undetach)
            store.ClearDetached(logical);
        foreach (var (local, logical) in page.Detach)
            store.SetDetached(local, logical, nowMs);
        if (page.Detach.Count > 0)
        {
            // 解除連結的帳戶不再送 link.upsert（送了會讓伺服器復活被刪的邏輯帳戶）；摘要清掉，之後被復活時才會重新發布
            store.SetPublishedMany(page.Detach.Select(d => (d.LocalAccountId, (PublishedLink?)null)).ToList());
        }

        if (conversationApply is { AfterCommit: { } afterCommit })
        {
            foreach (var action in afterCommit)
                action();
        }

        // 已移除的電腦擁有的複本（設計 14.22）：這一頁的複本寫完之後（名冊更新時另外做）
        DropRemovedOwnerReplicas(conn);

        if (page.SelfRevoked)
        {
            Stop(SyncStopReason.SelfRevoked);
            return null;
        }

        if (delta.Accounts.Count > 0)
        {
            // 連結或 detached 換了：以最後的快照重算一次（新連結的帳戶摘要改成已連結的算法、被復活的恢復發布）
            Republish();
        }

        if (catalogChanged)
        {
            PublishPoll();
            // token 計數依目錄的連結換成邏輯帳戶（SyncEngine.Tokens.cs）
            RequestTokens();
        }
        UpdateStatus();
        // 打開的複本串流還沒輪到的也要再拉一頁（步 14）
        return EndConversationPage(response.More);
    }

    // ── 共享用量（D14） ──

    /// <summary>
    /// 拉到的 usage.snapshot（依 seq）→ 這台連結到該邏輯帳戶的本機帳戶；自己發的略過；追趕期間本機有自己查的就只記歷史。
    /// 這台沒有連結、目錄裡有的邏輯帳戶（remoteOnly 卡片，WP3.4）照樣驗證後收集，每個邏輯帳戶只留 seq 最大的一筆
    /// </summary>
    private (List<SharedUsageApply> Linked, List<RemoteUsageApply> Remote) SharedUsageFor(PullDelta delta, CatalogPage page)
    {
        var result = new List<SharedUsageApply>();
        if (delta.UsagePoints.Count == 0)
            return (result, []);
        var remote = new Dictionary<string, (long Seq, SharedUsage Usage)>(StringComparer.Ordinal);
        var selfDeviceId = device!.Current.DeviceId;
        var catchingUp = state != SyncEngineState.Live;
        var view = page.Apply.View;
        var locals = view.LocalToLogical
            .GroupBy(p => p.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Key).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        foreach (var point in delta.UsagePoints)
        {
            if (point.CreatedAt is <= 0 or > UsageSyncRules.MaxUnixMs)
                continue;
            var logical = point.Payload.LogicalAccountId;
            var linked = locals.TryGetValue(logical, out var ids);
            // 這台沒有連結、目錄裡也沒有的邏輯帳戶（已刪除、還沒拉到）不收：remoteOnly 卡片只列目錄裡的
            if (!linked && !view.Accounts.ContainsKey(logical))
                continue;
            UsageSnapshot? snapshot = null;
            SharedUsage? remoteUsage = null;
            DateTimeOffset pointTime;
            try
            {
                if (linked)
                    snapshot = UsageSyncRules.FromShared(point.Payload, selfDeviceId);
                else
                    remoteUsage = UsageSyncRules.ForRemoteOnly(point.Payload, selfDeviceId);
                pointTime = DateTimeOffset.FromUnixTimeMilliseconds(point.CreatedAt);
            }
            catch (ArgumentException ex)
            {
                // 壞掉的單筆略過、游標照常前進：一筆毒丸不能讓所有電腦的拉取永遠卡在同一頁
                Log.Warn($"略過格式不正確的共享用量（seq {point.Seq}）：{ex.Message}");
                pullWarning = "略過了格式不正確的共享用量";
                continue;
            }

            if (!linked)
            {
                if (remoteUsage is not null && (!remote.TryGetValue(logical, out var previous) || previous.Seq < point.Seq))
                    remote[logical] = (point.Seq, remoteUsage);
                continue;
            }

            if (snapshot is null)
                continue;
            foreach (var id in ids!)
            {
                var source = observed.TryGetValue(id, out var mine) ? mine.Source : null;
                result.Add(new SharedUsageApply(id, snapshot, pointTime, UsageSyncRules.ReplaceCurrent(catchingUp, source)));
            }
        }

        return (result, remote.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new RemoteUsageApply(r.Key, r.Value.Usage)).ToList());
    }

    /// <summary>本機用量：負責查詢的那台在 Live 時帶 term 上傳；對話中的 rate limit 不帶 term、每帳戶 60 秒最多一次（只看本機快照的來源）</summary>
    private void OnUsageObserved(SyncSignal.UsageObserved signal)
    {
        observed[signal.LocalAccountId] = signal.Snapshot;
        if (current is not { Proven: true, UpdateOnly: false } conn || catalog is null)
            return;
        catalog.LinkStates().TryGetValue(signal.LocalAccountId, out var link);
        var assignment = link is not null && assignments.TryGetValue(link.LogicalAccountId, out var a)
            ? a.On ? UsageAssignmentState.On : UsageAssignmentState.Off
            : UsageAssignmentState.Unknown;
        var now = time.GetUtcNow();
        var decision = UsageSyncRules.Decide(
            signal.Snapshot,
            assignment,
            state == SyncEngineState.Live,
            link,
            lastTermedFetchedAt.TryGetValue(signal.LocalAccountId, out var termed) ? termed : null,
            lastSessionUploadAt.TryGetValue(signal.LocalAccountId, out var session) ? session : null,
            now);
        if (decision == UsageUpload.None || link is null || UsageSyncRules.ToShared(signal.Snapshot) is not { } shared)
            return;
        long? term;
        switch (decision)
        {
            case UsageUpload.Termed:
                term = assignments[link.LogicalAccountId].Term;
                lastTermedFetchedAt[signal.LocalAccountId] = signal.Snapshot.FetchedAt;
                break;
            case UsageUpload.Session:
                term = null;
                lastSessionUploadAt[signal.LocalAccountId] = now;
                break;
            default:
                throw new InvalidOperationException("不認得的用量上傳判斷");
        }

        var publish = new UsagePublish(link.LogicalAccountId, term, shared);
        Spawn(async () =>
        {
            try
            {
                await conn.Session!.UsagePublishAsync(publish, conn.Token);
            }
            catch (SyncTransportException ex) when (ex.Failure.Kind == SyncFailureKind.ClientBug)
            {
                // 格式錯誤時伺服器丟 invalid（sync-protocol 的表沒列）：只記錄、不重連
                Log.Warn($"伺服器拒收這台的用量（{ex.Failure.Code}）：只記錄、不重連");
            }
            catch (SyncTransportException ex)
            {
                Fail(conn, ex.Failure);
            }
        }, conn);
    }
}
