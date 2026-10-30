using Myriagent.Core;
using Myriagent.Hub;
using Microsoft.Data.Sqlite;

namespace Myriagent.Sync;

// WP4.7（媒體、檔案與上傳）步 8 的登記點實作（SyncEngine.Contracts.cs）：
// - Hello 宣告 media1（有對話的盒子、v3 表可用、也宣告了 conv1 時）。
// - Prove 完成：保存 cha_（單調時鐘記收到的時間），13 分鐘後以 RefreshAccessToken 換新（D24）；連線丟掉時清掉、取消那條連線的轉送。
// - MediaRequest：引擎執行緒查 media_refs_local，其餘交給 MediaServer 在背景回 E7（D26）。
// - 進 Live（步 9，SyncEngine.MediaRefs.cs）：媒體參照的歷史回補、E10 拉缺縮圖的清單；
//   步 10（SyncEngine.MediaBlobs.cs）：截斷過的項目改送 blob 版、上傳離線期間排著的項目欄位 blob。
// - 步 11（SyncEngine.RemoteFiles.cs）：Prove 協商出 media1 時把遠端檔案指令的傳輸掛到盒子上、每日修剪抽屜與 fileFetch 的參照。
// - 步 12（SyncEngine.ReplicaMedia.cs）：Prove 協商出 media1 時把複本的媒體來源（E9）掛到盒子上；拉到一頁時先預取複本的 blob（E6）再解碼。
// 只有協商出 media1 的連線才做這些（cha_ 的保存與換新例外：協商出 update1 的連線也做，下載新版用，app 自我更新 U3）；
// 狀態只在引擎執行緒改（權杖的快照給 HTTP 用戶端在任何執行緒讀）
internal sealed partial class SyncEngine
{
    /// <summary>換新失敗（伺服器回錯誤、回的權杖不合格）之後多久再試</summary>
    private static readonly TimeSpan AccessTokenRetryAfter = TimeSpan.FromMinutes(1);

    private readonly AccessTokenHolder accessTokens = new();

    // ── 以下只在引擎執行緒讀寫 ──
    private MediaServer? media;
    private ITimer? accessTokenTimer;
    private int accessTokenTimerToken;
    private Task<bool>? accessTokenRefresh;
    private int accessTokenRefreshGeneration;

    /// <summary>測試接縫：媒體轉送的 HTTP 傳輸（null＝正式的 SocketsHttpHandler）。啟動前設定</summary>
    internal HttpMessageHandler? MediaHttpHandlerForTest { get; set; }

    /// <summary>測試接縫：媒體轉送的守門員（null＝<see cref="RemoteFileGuard.CreateDefault"/>）。啟動前設定</summary>
    internal RemoteFileGuard? MediaGuardForTest { get; set; }

    /// <summary>測試接縫：縮圖（null＝MediaServer 自己的 ThumbnailService）。啟動前設定</summary>
    internal Func<string, int, CancellationToken, Task<byte[]?>>? MediaThumbnailForTest { get; set; }

    /// <summary>測試：目前保存的 cha_（任何執行緒）</summary>
    internal string? AccessTokenForTest => accessTokens.Current?.Token;

    partial void ContributeMediaFeatures(List<string> into)
    {
        // 媒體參照只在發布對話時才發得出去（項目、指令輸出都要伺服器知道這台是擁有者）：沒有 conv1 就不宣告
        if (Conversations is null || store is not { ConversationTablesReady: true } || !into.Contains(DeviceFeatureRules.Conv1))
            return;
        into.Add(DeviceFeatureRules.Media1);
    }

    partial void OnProvenForMedia(SyncConnection conn, ProveResponse prove)
    {
        NoteItemBlobsNegotiated(conn);
        if (!IsCurrent(conn) || !UsesAccessToken(conn))
            return;
        if (AccessTokenRules.IsUsable(prove.AccessToken))
        {
            accessTokens.Set(prove.AccessToken, time.GetTimestamp());
            ScheduleAccessTokenRefresh(conn, AccessTokenRules.RefreshAfter);
        }
        else
        {
            Log.Warn("Prove 回的存取權杖不合格：先不回覆媒體轉送、不下載新版，稍後再換");
            accessTokens.Clear();
            ScheduleAccessTokenRefresh(conn, AccessTokenRetryAfter);
        }

        if (!DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Media1))
            return;

        media ??= CreateMediaServer();
        // 步 11：中樞的遠端檔案指令（抽屜與 fileFetch 的參照、上傳的下載）
        OpenRemoteFiles();
        // 步 12：複本的圖片、影片與縮圖經 E9 取（中樞與 AgentWebView 經發布者拿到）
        OpenReplicaMedia();
    }

    /// <summary>這條連線保存、換新存取權杖：媒體轉送（media1）與下載新版（app 自我更新 U3 的 update1，含只更新的連線）共用</summary>
    private static bool UsesAccessToken(SyncConnection conn) =>
        DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Media1) || DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Update1);

    partial void OnConnectionClosedForMedia(SyncConnection conn)
    {
        accessTokens.Clear();
        CancelTimer(ref accessTokenTimer);
        accessTokenTimerToken++;
        accessTokenRefresh = null;
        media?.CancelConnection(conn.Generation);
        CloseThumbPull();
    }

    partial void OnOnlineForMedia(SyncConnection conn)
    {
        if (!IsCurrent(conn) || !DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Media1) || Conversations is null
            || store is not { ConversationTablesReady: true })
            return;
        try
        {
            BackfillMediaRefs();
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            Log.Error("媒體參照的歷史回補失敗（下一次連線再試）", ex);
        }

        // 步 10：截斷過的項目改送 blob 版（D32），離線期間排著的 blob 開始上傳
        OnOnlineForItemBlobs();

        // 排回引擎執行緒：拉取可能在交握途中就完成、先進 Live，這時 Prove 的 cha_ 還沒存（交握做完才輪到這裡）
        thread.Post(() =>
        {
            if (!stopping && IsCurrent(conn))
                StartThumbPull(conn);
        });
    }

    partial void OnMediaRequest(SyncConnection conn, MediaRequest request)
    {
        // 不認得的參照也交給 MediaServer 立刻回 404（伺服器不必等滿 15 秒，D26）
        LocalMediaRefRow? row = null;
        if (store is { ConversationTablesReady: true } s && ConversationEventRules.IsMediaRef(request.Ref))
        {
            try
            {
                row = s.FindMediaRef(request.Ref);
            }
            catch (SqliteException ex)
            {
                Log.Warn($"查媒體參照失敗（回 404）：{ex.Message}");
            }
        }

        var server = media ??= CreateMediaServer();
        var job = new MediaJob(conn.Generation, request, row, policy, device!.Current.DeviceId);
        Spawn(() => server.HandleAsync(job, conn.Token));
    }

    /// <summary>引擎停止（CloseResources）：換新的計時器、權杖、轉送、縮圖上傳與 HTTP 用戶端一起收掉</summary>
    private void CloseMedia()
    {
        CancelTimer(ref accessTokenTimer);
        accessTokenTimerToken++;
        accessTokenRefresh = null;
        accessTokens.Clear();
        media?.Dispose();
        media = null;
        CloseReleaseHttp();
        CloseThumbs();
        CloseItemBlobs();
        CloseRemoteFiles();
        CloseReplicaMedia();
    }

    private MediaServer CreateMediaServer()
    {
        var client = new DeviceHttpClient(server, pairedServer, args.DevTrustEnv, accessTokens, RefreshAccessTokenForHttpAsync, time, MediaHttpHandlerForTest);
        return new MediaServer(client, QueryMediaScopeAsync, MediaGuardForTest ?? RemoteFileGuard.CreateDefault(), MediaThumbnailForTest);
    }

    /// <summary>媒體轉送問中樞（任何執行緒）：經 RunOnUiAsync 在 UI 執行緒取快照；沒有掛中樞（發布者還沒掛上）回 null</summary>
    private async Task<MediaScopeSnapshot?> QueryMediaScopeAsync(MediaScopeQuery query, CancellationToken ct)
    {
        if (Conversations is not { } box)
            return null;
        MediaScopeSnapshot? snapshot = null;
        await args.Host.RunOnUiAsync(() => snapshot = box.MediaScope?.GetMediaScope(query), ct).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>HTTP 用戶端要換權杖（任何執行緒；401 或權杖快到期）：排進引擎執行緒，與計時器的換新共用同一次</summary>
    private Task<bool> RefreshAccessTokenForHttpAsync(CancellationToken ct)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread.Post(() =>
        {
            if (current is not { Proven: true } conn || !IsCurrent(conn) || !UsesAccessToken(conn))
            {
                done.TrySetResult(false);
                return;
            }

            RefreshAccessToken(conn).ContinueWith(t => done.TrySetResult(t.Status == TaskStatus.RanToCompletion && t.Result), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        });
        return done.Task.WaitAsync(ct);
    }

    /// <summary>換一個新的 cha_（引擎執行緒）：同一條連線正在換就共用那一次</summary>
    private Task<bool> RefreshAccessToken(SyncConnection conn)
    {
        if (accessTokenRefresh is { IsCompleted: false } running && accessTokenRefreshGeneration == conn.Generation)
            return running;
        var task = RefreshAccessTokenCoreAsync(conn);
        accessTokenRefresh = task;
        accessTokenRefreshGeneration = conn.Generation;
        return task;
    }

    private async Task<bool> RefreshAccessTokenCoreAsync(SyncConnection conn)
    {
        busy++;
        try
        {
            AccessTokenResponse response;
            try
            {
                response = await conn.Session!.RefreshAccessTokenAsync(conn.Token);
            }
            catch (SyncTransportException ex) when (ex.Failure.Kind == SyncFailureKind.ClientBug)
            {
                Log.Warn($"伺服器拒絕換存取權杖（{ex.Failure.Code}）：這條連線不再換（程式錯誤）");
                return false;
            }
            catch (SyncTransportException ex)
            {
                // 連線本身斷了會另外收到 OnClosed；這裡只影響媒體，不為了它重連
                Log.Info($"換存取權杖失敗（{ex.Failure.Code}）：{AccessTokenRetryAfter.TotalMinutes:0} 分鐘後再試");
                if (IsCurrent(conn))
                    ScheduleAccessTokenRefresh(conn, AccessTokenRetryAfter);
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (!IsCurrent(conn))
                return false;
            if (response is null || !AccessTokenRules.IsUsable(response.AccessToken))
            {
                Log.Warn($"伺服器換的存取權杖不合格：{AccessTokenRetryAfter.TotalMinutes:0} 分鐘後再試");
                ScheduleAccessTokenRefresh(conn, AccessTokenRetryAfter);
                return false;
            }

            // expiresAt 只記錄、不和本機時鐘比（D24）：到期一律從收到的時間算
            accessTokens.Set(response.AccessToken, time.GetTimestamp());
            ScheduleAccessTokenRefresh(conn, AccessTokenRules.RefreshAfter);
            return true;
        }
        finally
        {
            busy--;
        }
    }

    /// <summary>排（或重排）這條連線的換新計時器；回呼排回引擎執行緒，連線換了或計時器重排過就不做</summary>
    private void ScheduleAccessTokenRefresh(SyncConnection conn, TimeSpan due)
    {
        CancelTimer(ref accessTokenTimer);
        var token = ++accessTokenTimerToken;
        accessTokenTimer = CreateTimer(due, () =>
        {
            if (token != accessTokenTimerToken || !IsCurrent(conn))
                return;
            CancelTimer(ref accessTokenTimer);
            _ = RefreshAccessToken(conn);
        });
    }
}
