using System.Security.Cryptography;
using Myriagent.Core;
using Microsoft.Data.Sqlite;

namespace Myriagent.Sync;

// WP4.7（媒體、檔案與上傳）步 11：中樞的遠端檔案指令要的同步那一側（D22、D33、D36）。
// - 第一次協商出 media1 時建 RemoteFileTransfer、掛到對話的盒子上（中樞經發布者取用）；引擎停止時拿下來、Dispose（進行中的下載取消）。
// - 抽屜（@list）與 fileFetch（@file）的參照：在引擎執行緒依序寫 media_refs_local（29 天內同一個檔重用，LooseMediaRefRules）。
// - 每日修剪：@list 超過 30 天、@file 超過 15 分鐘的列（第一次協商出 media1 時修一次，之後每天一次）。
// 狀態只在引擎執行緒改；下載在背景（權杖與 HTTP 用戶端同媒體轉送）
internal sealed partial class SyncEngine
{
    // ── 以下只在引擎執行緒讀寫 ──
    private RemoteFileTransfer? remoteFiles;
    private ITimer? looseRefPruneTimer;

    /// <summary>協商出 media1（OnProvenForMedia）：建傳輸、掛到盒子上；修剪還沒排就先修一次再排每日的</summary>
    private void OpenRemoteFiles()
    {
        if (Conversations is not { } box || stopping)
            return;
        if (remoteFiles is null)
        {
            var client = new DeviceHttpClient(server, pairedServer, args.DevTrustEnv, accessTokens, RefreshAccessTokenForHttpAsync, time, MediaHttpHandlerForTest);
            remoteFiles = new RemoteFileTransfer(RegisterLooseRefsAsync, client, accessTokens);
        }

        box.RemoteFiles = remoteFiles;
        if (looseRefPruneTimer is null)
        {
            PruneLooseMediaRefs();
            ScheduleLooseRefPrune();
        }
    }

    /// <summary>引擎停止（CloseMedia）：從盒子拿下來（只拿自己掛的）、取消進行中的下載、停修剪的計時器</summary>
    private void CloseRemoteFiles()
    {
        CancelTimer(ref looseRefPruneTimer);
        if (remoteFiles is not { } transfer)
            return;
        Conversations?.ClearRemoteFiles(transfer);
        transfer.Dispose();
        remoteFiles = null;
    }

    /// <summary>傳輸的登記（任何執行緒）：排進引擎執行緒做，寫完 media_refs_local 才完成</summary>
    private Task<IReadOnlyList<string>> RegisterLooseRefsAsync(string conversationId, string kind, IReadOnlyList<string> fullPaths, CancellationToken ct)
    {
        var done = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var paths = fullPaths.ToList();
        thread.Post(() =>
        {
            if (ct.IsCancellationRequested)
            {
                done.TrySetCanceled(ct);
                return;
            }

            try
            {
                done.TrySetResult(RegisterLooseRefs(conversationId, kind, paths));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task.WaitAsync(ct);
    }

    /// <summary>引擎執行緒：配或重用參照、寫進 media_refs_local（一個交易）。資料庫不能用、引擎在停止丟 <see cref="InvalidOperationException"/></summary>
    private IReadOnlyList<string> RegisterLooseRefs(string conversationId, string kind, IReadOnlyList<string> fullPaths)
    {
        if (stopping || store is not { ConversationTablesReady: true } s)
            throw new InvalidOperationException("同步資料庫還不能用，或同步引擎正在停止");
        var now = NowMs();
        try
        {
            var existing = kind == MediaServerRules.ListItemId ? s.LoadListRefs(conversationId, LooseMediaRefRules.ListReuseSince(now)) : [];
            var random = MediaRandomForTest ?? RandomNumberGenerator.GetBytes;
            var refs = LooseMediaRefRules.Assign(kind, conversationId, fullPaths, existing, now, () => FreshMediaRef(s, random), out var added);
            s.AddMediaRefs(added);
            return refs;
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException($"寫不進媒體參照：{ex.Message}", ex);
        }
    }

    /// <summary>新的參照；和 media_refs_local 已有的撞到就重抽（128 位元亂數，實際上不會撞）</summary>
    private static string FreshMediaRef(SyncStore s, Func<int, byte[]> random)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var value = MediaRefIssueRules.NewRef(random);
            if (s.FindMediaRef(value) is null)
                return value;
        }

        throw new InvalidOperationException("配不出不重複的媒體參照（亂數來源有問題）");
    }

    /// <summary>每日修剪：抽屜超過 30 天、fileFetch 超過 15 分鐘的列（D33）；失敗只記錄</summary>
    private void PruneLooseMediaRefs()
    {
        if (stopping || store is not { ConversationTablesReady: true } s)
            return;
        try
        {
            var now = NowMs();
            var removed = s.PruneMediaRefs(LooseMediaRefRules.ListPruneBefore(now), LooseMediaRefRules.FilePruneBefore(now), LooseMediaRefRules.XferPruneBefore(now));
            if (removed > 0)
                Log.Info($"修剪了 {removed} 個過期的抽屜與檔案下載參照");
        }
        catch (Exception ex) when (ex is SqliteException or ObjectDisposedException)
        {
            Log.Warn($"修剪抽屜與檔案下載的參照失敗（明天再試）：{ex.Message}");
        }
    }

    private void ScheduleLooseRefPrune()
    {
        CancelTimer(ref looseRefPruneTimer);
        looseRefPruneTimer = CreateTimer(LooseMediaRefRules.PruneInterval, () =>
        {
            if (stopping || looseRefPruneTimer is null)
                return;
            PruneLooseMediaRefs();
            ScheduleLooseRefPrune();
        });
    }
}
