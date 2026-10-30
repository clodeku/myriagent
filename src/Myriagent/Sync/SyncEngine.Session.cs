using System.Globalization;
using System.Security.Cryptography;
using Myriagent.Core;
using Myriagent.Security;

namespace Myriagent.Sync;

// 連線、交握（Hello／Prove）、退避、伺服器→裝置的訊息、名冊（sync-protocol 3、4、7、9、10）
internal sealed partial class SyncEngine
{
    /// <summary>伺服器通知的文字放進 status.detail 前截到這個長度（去掉控制字元；網頁一律以 textContent 顯示）</summary>
    private const int MaxNoticeText = 200;

    /// <summary>交握完成前暫存的伺服器通知上限（多的丟掉）</summary>
    private const int MaxEarlyNotices = 8;

    private static readonly SyncFailure HubSignatureInvalid = new(SyncFailureKind.Transient, "hub_identity", "伺服器身分驗證失敗（簽章不符）");

    private static readonly SyncFailure RepinFailed = new(SyncFailureKind.Transient, "repin_failed", "伺服器身分已輪換，但本機存檔失敗");

    private static readonly SyncFailure AckTimeoutFailure = new(SyncFailureKind.Transient, "ack_timeout", "伺服器太久沒有確認送出的資料");

    private static readonly SyncFailure BadResponse = new(SyncFailureKind.Transient, "bad_response", "伺服器的回應格式不正確");

    private static readonly SyncFailure LocalFailure = new(SyncFailureKind.Transient, "local", "本機同步處理失敗");

    private static readonly SyncFailure ShutdownFailure = new(SyncFailureKind.Transient, ServerNoticeKinds.Shutdown, "伺服器正在重新啟動");

    // ── 連線 ──

    private bool IsCurrent(SyncConnection conn) => ReferenceEquals(current, conn) && !stopping;

    /// <summary>傳輸的回呼（任意執行緒）→ 排進引擎執行緒；到了才看是不是目前的連線</summary>
    private void Deliver(SyncConnection conn, Action handler) => thread.Post(() =>
    {
        if (!IsCurrent(conn))
            return;
        try
        {
            handler();
        }
        catch (Exception ex)
        {
            Log.Error("同步引擎處理伺服器訊息失敗", ex);
            Fail(conn, LocalFailure);
        }
    });

    private void Connect()
    {
        if (stopping || store is null || state is SyncEngineState.Stopped or SyncEngineState.NeedsConfirmation)
            return;
        if (paused)
        {
            SetState(SyncEngineState.Paused);
            return;
        }

        CancelBackoff();
        if (current is { } old)
            Drop(old);
        // 金鑰之前暫時打不開（或簽章暫時性失敗後關掉了）：先重開，還是不行就照退避再等（不連伺服器）
        switch (OpenSigner(out var keyFailure))
        {
            case KeyOpen.Ready:
                break;
            case KeyOpen.Unavailable:
                EnterBackoff(keyFailure);
                return;
            case KeyOpen.Stopped:
                return;
            default:
                throw new InvalidOperationException("不認得的開金鑰結果");
        }

        var conn = new SyncConnection(this, ++connectionGeneration, lifetime.Token);
        current = conn;
        SetState(SyncEngineState.Connecting);
        Spawn(() => RunConnectionAsync(conn), conn);
    }

    private async Task RunConnectionAsync(SyncConnection conn)
    {
        ISyncSession session;
        try
        {
            session = await args.Transport.ConnectAsync(new SyncConnectRequest(server, pairedServer, deviceToken!, args.DevTrustEnv), conn, conn.Token);
        }
        catch (SyncTransportException ex)
        {
            Fail(conn, ex.Failure);
            return;
        }

        if (!IsCurrent(conn))
        {
            await DisposeQuietlyAsync(session);
            return;
        }

        conn.Session = session;
        conn.InHandshake = true;
        SetState(SyncEngineState.Handshaking);
        if (await HelloAsync(conn, session) is not { } hello)
            return;
        if (await ProveAsync(conn, session, hello) is not { } prove)
            return;
        if (prove.UpdateOnly == true)
        {
            // 只更新的連線（app 自我更新 U3，SyncEngine.Releases.cs）：伺服器只放行心跳與換存取權杖，名冊拉不到、也用不到（不送、不收）
            CompleteUpdateOnlyHandshake(conn, prove);
            return;
        }
        // 名冊版本不同（Hello 帶的，或交握中收到的 TrustChanged）→ 先拉名冊：自己被撤銷就不送任何東西
        if ((hello.TrustRevs.Registry != trust!.RegistryRev || conn.EarlyTrustRev is { } early && early != trust.RegistryRev)
            && !await RefreshTrustAsync(conn))
            return;
        if (!IsCurrent(conn))
            return;
        CompleteHandshake(conn, prove);
    }

    /// <summary>Hello 並檢查伺服器身分；中斷（退避、待確認）時回 null</summary>
    private async Task<HelloResponse?> HelloAsync(SyncConnection conn, ISyncSession session)
    {
        var d = device!.Current;
        var nonceD = HandshakeRules.NewNonce();
        conn.NonceD = nonceD;
        var observed = session.ObservedSpkiSha256;
        var helloCursors = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [SyncClasses.Catalog] = Cursor(SyncClasses.Catalog),
            [SyncClasses.Usage] = Cursor(SyncClasses.Usage),
        };
        CollectHelloCursors(helloCursors);
        // 第 4 階段的功能（SyncEngine.Contracts.cs）：沒有登記就是 null，Hello 與第 3 階段逐位元組相同
        conn.DeclaredFeatures = CollectHelloFeatures();
        var request = new HelloRequest(
            SyncProtocol.Version,
            d.DeviceId,
            args.BootId,
            args.AppVersion,
            args.MachineName,
            args.WindowsUser,
            nonceD,
            observed,
            store!.SeenServer,
            OutboxRules.Position(outbox, store.NextDeviceSeq),
            helloCursors,
            policy,
            conn.DeclaredFeatures);
        HelloResponse hello;
        var helloSent = time.GetTimestamp();
        try
        {
            hello = await session.HelloAsync(request, conn.Token);
        }
        catch (SyncTransportException ex)
        {
            Fail(conn, ex.Failure);
            return null;
        }

        var helloReceived = time.GetTimestamp();
        if (!IsCurrent(conn))
            return null;
        if (hello is null)
        {
            Fail(conn, BadResponse);
            return null;
        }

        // 先驗伺服器身分，驗過了才相信回應裡的其他欄位（伺服器換實例會清掉本機的目錄快取，不能讓冒牌的回應觸發）
        var (outcome, pin) = HandshakeRules.CheckHubIdentity(trust!.PinnedHubSpki, d.DeviceId, nonceD, observed, hello);
        switch (outcome)
        {
            case HubIdentityOutcome.Ok:
                break;
            case HubIdentityOutcome.Rotated:
                if (pin is null || !trust.TryRepin(pin))
                {
                    Log.Warn("伺服器身分輪換了，但改釘存檔失敗：中斷並退避（下次 Hello 會再帶輪換聲明）");
                    Fail(conn, RepinFailed);
                    return null;
                }

                Log.Info($"伺服器身分已輪換（舊金鑰簽過聲明），改釘新的指紋 {trust.HubFingerprint}");
                break;
            case HubIdentityOutcome.NeedsLocalConfirmation:
                candidateSpki = pin;
                confirmFingerprint = P256KeyRules.AnchorFingerprint(pin!);
                Log.Warn($"伺服器身分換了、沒有可信的輪換聲明：等使用者在本機比對新指紋 {confirmFingerprint}");
                Drop(conn);
                SetState(SyncEngineState.NeedsConfirmation);
                return null;
            case HubIdentityOutcome.Invalid:
                Log.Warn("伺服器身分驗證失敗（hubSig 驗不過）：中斷並退避");
                Fail(conn, HubSignatureInvalid);
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "不認得的伺服器身分判斷");
        }

        if (hello.Server is null || hello.TrustRevs is null || hello.Limits is null || string.IsNullOrEmpty(hello.Server.InstanceId))
        {
            Fail(conn, BadResponse);
            return null;
        }

        // 伺服器身分驗過才相信它宣告的功能（D45）：雙方的交集；沒宣告的一方照第 3 階段。nonceS 在 hubSig 的範圍內，當成信封的 conn
        conn.EnvelopeConn = hello.NonceS;
        conn.Features = DeviceFeatureRules.Negotiate(conn.DeclaredFeatures, hello.Features);
        RecordHelloClock(conn, hello.ServerTime, helloSent, helloReceived);
        // app 自我更新 U3（SyncEngine.Releases.cs）：伺服器目前提供的桌面版
        NoteLatestApp(hello.LatestApp);
        if (conn.Features.Count > 0)
            Log.Info($"與伺服器協商出的功能：{string.Join("、", conn.Features)}");

        var nowMs = NowMs();
        clockSkewMs = HandshakeRules.IsClockSkewed(hello.ServerTime, nowMs) ? hello.ServerTime - nowMs : null;
        if (clockSkewMs is { } skew)
            Log.Warn($"伺服器時間與本機差 {skew} ms：請同步 Windows 時間（排序不看時鐘，同步照常）");
        limits = Sanitize(hello.Limits);
        store.SetKv(SyncStoreSchema.LastHelloAt, nowMs.ToString(CultureInfo.InvariantCulture));
        if (store.SeenServer != hello.Server)
        {
            // D15 第 1 步：伺服器換了實例（還原或重建）——游標歸 0、清空目錄快取與 published，seen_server 等對帳完成才改
            Log.Info($"伺服器換了實例（{hello.Server.InstanceId}／還原第 {hello.Server.RestoreGeneration} 次）：重設游標與目錄快取，開始對帳");
            store.BeginServerChange(hello.Server);
            cursors = NoCursors;
            catalog!.Reset();
            // published 已清空：link.upsert、cli.state 以新的 deviceSeq 重送（sync-protocol 9；Pending 的列 ApplyProve 不動，交握完照常送出）
            Republish();
            // 對話的發布狀態已在 BeginServerChange 的交易裡重設（WP4.3 計畫 D22）：引擎記憶體裡的快取跟著丟掉
            OnServerChangedForConversations();
            // 還原之後伺服器的縮圖（thumb_blob）可能沒了：這次啟動上傳過的不再算數（WP4.7 合併前審查 DI-1）
            thumbs?.ForgetAllUploaded();
            // 用量指派的 term 只在同一個伺服器實例（還原世代）內可比較：還原後 usage_pollers 的 term 會倒退，留著舊表的話
            // Merge 會把較小的 term 一律丟掉（零台或兩台查詢）。清空，由這次 Prove 的清單與之後的 UsageAssign 重建
            assignments = NoAssignments;
            lastTermedFetchedAt.Clear();
            PublishPoll();
            // remoteOnly 卡片的共享用量（只在協調者的記憶體）是舊實例的：下一次套用時先清掉（WP3.4 D15）
            remoteUsageReset = true;
        }

        return hello;
    }

    /// <summary>Prove、換存輪換的權杖、以 ApplyProve 對齊 outbox；中斷時回 null</summary>
    private async Task<ProveResponse?> ProveAsync(SyncConnection conn, ISyncSession session, HelloResponse hello)
    {
        var d = device!.Current;
        string signature;
        try
        {
            signature = signer!.Sign(HandshakeRules.DeviceSignedData(d.DeviceId, hello.NonceS, conn.NonceD!, session.ObservedSpkiSha256));
        }
        catch (ObjectDisposedException ex)
        {
            Log.Error("裝置金鑰簽章失敗（金鑰已關閉）", ex);
            Stop(SyncStopReason.KeyMissing);
            return null;
        }
        catch (CryptographicException ex)
        {
            // 和開金鑰一樣分類：暫時性的（TPM 忙碌）關掉這個金鑰、斷線退避，下次 Connect 重開；不存在的停止
            signer?.Dispose();
            signer = null;
            if (OnKeyError("裝置金鑰簽章失敗", ex, out var keyFailure) == KeyOpen.Unavailable)
                Fail(conn, keyFailure!);
            return null;
        }

        // 金鑰確實能用：之前那一連串失敗歸零
        keyStreak = DeviceKeyStreak.None;

        ProveResponse prove;
        try
        {
            prove = await session.ProveAsync(new ProveRequest(signature), conn.Token);
        }
        catch (SyncTransportException ex)
        {
            Fail(conn, ex.Failure);
            return null;
        }

        if (!IsCurrent(conn))
            return null;
        if (prove is null)
        {
            Fail(conn, BadResponse);
            return null;
        }

        if (prove.TokenRotation is { } rotated)
        {
            if (rotated.StartsWith(SyncProtocol.DeviceTokenPrefix, StringComparison.Ordinal) && TrySaveToken(rotated))
            {
                deviceToken = rotated;
                Log.Info("裝置權杖已輪換");
            }
            else
            {
                // 舊權杖再有效 24 小時，下次 Prove 會再發一個新的（sync-protocol 3）
                Log.Warn("伺服器輪換了裝置權杖，但存檔失敗：沿用舊的");
            }
        }

        var (updated, verdict) = OutboxRules.ApplyProve(outbox, prove.NextDeviceSeqExpected, store!.NextDeviceSeq, prove.Resync, NowMs());
        if (SyncFailureRules.StopReasonFor(verdict) is { } reason)
        {
            Stop(reason);
            return null;
        }

        if (verdict != OutboxVerdict.Continue)
        {
            Fail(conn, BadResponse);
            return null;
        }

        CommitOutbox(updated);
        if (prove.Resync)
            Log.Info($"伺服器在對帳模式（還原後）：重送 outbox 保留的 {outbox.Count(e => e.State == OutboxState.Retry)} 筆");
        return prove;
    }

    /// <summary>交握完成：伺服器通知、用量指派、送出世代、進 Draining、開心跳、先拉一次、開始送</summary>
    private void CompleteHandshake(SyncConnection conn, ProveResponse prove)
    {
        conn.InHandshake = false;
        conn.Proven = true;
        lastFailure = null;
        pullWarning = null;
        // 通知的文字只屬於驗證過的連線：每條重新開始，再採用 Prove 之後、交握完成之前到的（伺服器 Prove 後就把連線加進群組）
        noticeDetail = null;
        foreach (var notice in conn.EarlyNotices)
            OnServerNotice(conn, notice);
        conn.EarlyNotices.Clear();
        // 指派：Prove 的清單照 term 合併，表裡 On 而清單沒列的改 Off；再合併比 Prove 先到的（term 較大的勝）
        assignments = UsageSyncRules.FromProve(assignments, prove.UsageAssignments);
        foreach (var assign in conn.EarlyAssigns)
            assignments = UsageSyncRules.Merge(assignments, assign);
        conn.EarlyAssigns.Clear();
        sendGeneration++;
        inFlight.Clear();
        lastBatchAt = null;
        SetState(SyncEngineState.Draining);
        StartHeartbeat(conn);
        if (conn.EarlyTrustRev is { } rev && rev != trust!.RegistryRev)
            StartTrustRefresh(conn);
        conn.EarlyTrustRev = null;
        // Grok、Antigravity 的閘門（prov1）：目錄依它重算、第一次有 prov1 時補拉對話中繼資料，都要在第一次拉取與送出之前
        OnProvenForProv1(conn);
        RequestPull(conn);
        SendNext(conn);
        AfterProveForMedia(conn, prove);
        AfterHandshake(conn);
        // 各電腦的即時狀態（SyncEngine.Stats.cs）：暫存的第 4 階段訊息處理完之後才開始送
        if (IsCurrent(conn))
            StartStats(conn);
        // 各帳戶的 token 計數（SyncEngine.Tokens.cs）
        if (IsCurrent(conn))
            StartTokens(conn);
        // 各電腦的排程（SyncEngine.Schedules.cs）
        if (IsCurrent(conn))
            StartSchedules(conn);
    }

    private bool TrySaveToken(string token)
    {
        string protectedToken;
        try
        {
            protectedToken = DeviceSecrets.Protect(token);
        }
        catch (CryptographicException ex)
        {
            Log.Warn($"保護新的裝置權杖失敗：{ex.Message}");
            return false;
        }

        return device!.TryUpdate(d => d with { ProtectedDeviceToken = protectedToken });
    }

    /// <summary>增量模式每個對話每秒最多幾則（伺服器給更高的也不超過；只影響這台自己送多少）</summary>
    internal const int MaxLiveDeltaHzCap = 16;

    private static SyncLimits Sanitize(SyncLimits l) => new(
        Math.Max(1, l.MaxBatchBytes),
        Math.Max(1, l.MaxBatchEvents),
        Math.Max(1, l.MaxInlinePayload),
        Math.Max(1, l.MaxLiveHz),
        Math.Max(1, l.MaxInFlightBatches),
        Math.Max(1, l.MaxPullEvents),
        Math.Max(1, l.MaxPullBytes),
        l.MaxLiveDeltaHz is > 0 and var deltaHz ? Math.Min(deltaHz, MaxLiveDeltaHzCap) : null);

    private long Cursor(string cls) => cursors.TryGetValue(cls, out var value) ? value : 0;

    // ── 失敗、斷線、退避 ──

    /// <summary>連線失敗：401／403 停止，其他斷線後退避（protocol 每小時重試、draining 照停機通知的規則延後，U3）</summary>
    private void Fail(SyncConnection conn, SyncFailure failure)
    {
        if (!IsCurrent(conn))
            return;
        if (SyncFailureRules.IsTerminal(failure.Kind))
        {
            lastFailure = failure;
            Log.Warn($"同步連線被拒（{failure.Code}）：{failure.Detail}");
            Stop(SyncFailureRules.StopReasonFor(failure.Kind));
            return;
        }

        // 只有交握完成的連線收得到 shutdown（OnServerNotice），交握失敗（hub_identity、repin_failed、not_proven）絕不被它取代
        if (conn.ShutdownNotice && conn.Proven)
            failure = ShutdownFailure;
        else if (failure.Code == SyncErrorCodes.Draining)
            DelayForDraining();
        else if (failure.Kind == SyncFailureKind.Protocol)
            Log.Warn($"同步連線被拒（{failure.Code}）：{failure.Detail}，{SyncBackoffRules.ProtocolRetry.TotalMinutes:0} 分鐘後再試");
        else if (failure.Kind == SyncFailureKind.ClientBug)
            Log.Warn($"伺服器判定這台送出的請求格式錯誤（{failure.Code}）：斷線後退避重連");
        else
            Log.Info($"同步連線中斷：{failure.Detail}");
        Drop(conn);
        EnterBackoff(failure);
    }

    /// <summary>丟掉這條連線（之後它的回呼一律忽略）：取消它的呼叫、停掉連線的計時器、在途批次作廢（重連後由 Prove 重新對齊）</summary>
    private void Drop(SyncConnection conn)
    {
        if (!ReferenceEquals(current, conn))
            return;
        current = null;
        var session = conn.Session;
        conn.Cancel();
        var now = time.GetUtcNow();
        if (liveSince is { } since && SyncBackoffRules.ShouldResetAttempts(now - since))
            attempt = 0;
        liveSince = null;
        if (conn.Proven)
            disconnectedSince ??= now;
        CancelTimer(ref heartbeatTimer);
        CancelTimer(ref ackTimer);
        CancelTimer(ref sendTimer);
        ackToken++;
        sendToken++;
        inFlight.Clear();
        sendGeneration++;
        if (session is not null)
            Spawn(() => DisposeQuietlyAsync(session));
        AfterConnectionClosed(conn);
        StopStats(conn);
        StopTokens(conn);
        StopSchedules(conn);
    }

    private void EnterBackoff(SyncFailure? failure)
    {
        if (stopping || state is SyncEngineState.Stopped or SyncEngineState.NeedsConfirmation)
            return;
        if (paused)
        {
            SetState(SyncEngineState.Paused);
            return;
        }

        lastFailure = failure;
        var now = time.GetUtcNow();
        var delay = SyncBackoffRules.DelayAfter(failure, attempt, options.Jitter(), options.BackoffSteps, now, notBefore);
        attempt++;
        lock (historyGate)
        {
            if (backoffHistory.Count >= MaxBackoffHistory)
                backoffHistory.RemoveAt(0);
            backoffHistory.Add(delay);
        }
        ScheduleBackoff(delay);
        SetState(SyncEngineState.Backoff);
    }

    private void ScheduleBackoff(TimeSpan delay)
    {
        CancelTimer(ref backoffTimer);
        var token = ++backoffToken;
        backoffDue = time.GetUtcNow() + delay;
        backoffTimer = CreateTimer(delay, () =>
        {
            if (token != backoffToken)
                return;
            CancelTimer(ref backoffTimer);
            backoffDue = null;
            if (state == SyncEngineState.Backoff)
                Connect();
        });
    }

    private void CancelBackoff()
    {
        CancelTimer(ref backoffTimer);
        backoffToken++;
        backoffDue = null;
    }

    // ── 伺服器 → 裝置 ──

    private void OnClosed(SyncConnection conn, Exception? error)
    {
        var failure = error is SyncTransportException transport
            ? transport.Failure
            : SyncFailureRules.Classify(null, null, false, conn.InHandshake);
        Fail(conn, failure);
    }

    private void OnNotify(SyncConnection conn, Notify notify)
    {
        var classes = notify?.Cls ?? [];
        var pull = classes.Contains(SyncClasses.Catalog) || classes.Contains(SyncClasses.Usage);
        // 對話的類別與串流（WP4.3）：交握完成後才問（之前的交握完成後本來就會先拉一次）
        if (notify is not null && conn.Proven)
            pull = NotifyNeedsPull(conn, notify, pull);
        if (!pull)
            return;
        if (!conn.Proven)
        {
            // Prove 之前：交握完成後本來就會先拉一次
            return;
        }

        RequestPull(conn);
    }

    private void OnUsageAssign(SyncConnection conn, UsageAssign assign)
    {
        if (assign is null)
            return;
        if (!conn.Proven)
        {
            conn.EarlyAssigns.Add(assign);
            return;
        }

        var next = UsageSyncRules.Merge(assignments, assign);
        if (ReferenceEquals(next, assignments))
            return;
        assignments = next;
        PublishPoll();
    }

    private void OnTrustChanged(SyncConnection conn, TrustChanged changed)
    {
        if (changed is null)
            return;
        if (!conn.Proven)
        {
            conn.EarlyTrustRev = changed.Registry;
            return;
        }

        if (changed.Registry != trust!.RegistryRev)
            StartTrustRefresh(conn);
    }

    private void OnServerNotice(SyncConnection conn, ServerNotice notice)
    {
        if (notice is null)
            return;
        if (!conn.Proven)
        {
            // 伺服器身分還沒驗過（中間人可以在 Hello 回應前送）：先暫存，交握完成才採用；交握失敗就隨連線丟掉，
            // 不讓它改 status.detail、延後重連，或把 hub_identity 的失敗蓋成「伺服器正在重新啟動」
            if (conn.EarlyNotices.Count < MaxEarlyNotices)
                conn.EarlyNotices.Add(notice);
            return;
        }

        switch (notice.Kind)
        {
            case ServerNoticeKinds.Shutdown:
                // 伺服器要停機：下一次嘗試不早於 retryAfterSec ×（1 ± 20%）（U3 加上抖動，各電腦不在同一刻重連），
                // 不主動斷線、之後的斷線不當成錯誤顯示
                notBefore = SyncTimingRules.NoticeNotBefore(time.GetUtcNow(), notice.RetryAfterSec, options.Jitter());
                conn.ShutdownNotice = true;
                Log.Info($"伺服器通知即將停機：{notBefore:O} 之後再重連");
                break;
            case ServerNoticeKinds.CertExpiring or ServerNoticeKinds.MinAppVersion or ServerNoticeKinds.Alert:
            {
                var text = CatalogSyncRules.CleanText(notice.Text, MaxNoticeText) ?? notice.Kind;
                noticeDetail = text;
                Log.Warn($"伺服器通知（{notice.Kind}）：{text}");
                UpdateStatus();
                break;
            }

            default:
                if (!TryHandleServerNotice(conn, notice))
                    Log.Info($"略過不認得的伺服器通知：{CatalogSyncRules.CleanText(notice.Kind, 64)}");
                break;
        }
    }

    // ── 名冊 ──

    private void StartTrustRefresh(SyncConnection conn)
    {
        // 只更新的連線（U3）拉不到名冊（GetTrust 回 update_required），也不送、不收需要名冊的東西
        if (conn.UpdateOnly)
            return;
        if (conn.TrustRefreshing)
        {
            conn.TrustRefreshAgain = true;
            return;
        }

        conn.TrustRefreshing = true;
        Spawn(async () =>
        {
            try
            {
                do
                {
                    conn.TrustRefreshAgain = false;
                    if (!await RefreshTrustAsync(conn))
                        return;
                }
                while (conn.TrustRefreshAgain && IsCurrent(conn));
            }
            finally
            {
                conn.TrustRefreshing = false;
            }
        }, conn);
    }

    /// <summary>GetTrust → TrustStore.ApplyBundle；停止或斷線時回 false</summary>
    private async Task<bool> RefreshTrustAsync(SyncConnection conn)
    {
        TrustBundle bundle;
        try
        {
            bundle = await conn.Session!.GetTrustAsync(new GetTrustRequest(trust!.RegistryRev), conn.Token);
        }
        catch (SyncTransportException ex)
        {
            Fail(conn, ex.Failure);
            return false;
        }

        if (!IsCurrent(conn))
            return false;
        switch (trust.ApplyBundle(bundle))
        {
            case TrustCheck.Accept:
                Log.Info($"裝置名冊更新到第 {trust.RegistryRev} 版");
                ApplyViewToUi();
                DropRemovedOwnerReplicas(conn);
                return true;
            case TrustCheck.Unchanged:
                return true;
            case TrustCheck.Invalid:
                Log.Warn("伺服器給的裝置名冊格式不正確：沿用上一份");
                return true;
            case TrustCheck.SelfRevoked:
                Stop(SyncStopReason.SelfRevoked);
                return false;
            case TrustCheck.NotInRegistry:
                Stop(SyncStopReason.NotInRegistry);
                return false;
            default:
                throw new InvalidOperationException("不認得的名冊判斷");
        }
    }

    /// <summary>目錄檢視（devices、名冊）交給 UI 執行緒（不帶外觀、順序與偏好）</summary>
    private void ApplyViewToUi()
    {
        if (catalog is null || trust is null)
            return;
        var apply = new CatalogApply(catalog.View(trust.Registry), [], null, []);
        Spawn(async () =>
        {
            try
            {
                await args.Host.RunOnUiAsync(() => args.Applier.ApplyCatalog(apply), lifetime.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error("在 UI 執行緒套用同步目錄失敗", ex);
            }
        });
    }

    // ── 心跳 ──

    private void StartHeartbeat(SyncConnection conn)
    {
        CancelTimer(ref heartbeatTimer);
        heartbeatTimer = time.CreateTimer(_ => thread.Post(() => OnHeartbeat(conn)), null, options.HeartbeatInterval, options.HeartbeatInterval);
    }

    private void OnHeartbeat(SyncConnection conn)
    {
        if (!IsCurrent(conn) || !conn.Proven)
            return;
        // 先排一個寫存活時間的小工作到 UI 執行緒，再帶上 UI 最後一次跑到它的時間（兩個陣列在第 4 階段由 WP4.3 的登記點填，沒有就是空的）
        args.Host.PingUi();
        var (running, waiting) = CollectHeartbeat();
        var beat = new Heartbeat(args.Host.UiAliveAt, running, waiting);
        Spawn(async () =>
        {
            try
            {
                await conn.Session!.HeartbeatAsync(beat, conn.Token);
                Interlocked.Increment(ref heartbeatsSent);
            }
            catch (SyncTransportException ex)
            {
                Fail(conn, ex.Failure);
            }
        }, conn);
    }
}
