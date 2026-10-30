using Myriagent.Core;
using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

// 協調者給這台網頁的同步檢視（WP4.6 計畫步 9；D23、D26、D27、D29、D44）：發布者掛上時打開副本的顯示、回補進度改變時重送 sync、
// 設定頁的遠端操作紀錄（以 (at, seq) 翻頁、匯出）與回收區（清單、還原）。讀紀錄在引擎執行緒、讀回收區在執行緒集區，
// 結果一律經 host.BeginInvoke 回 UI 執行緒，只送給發問的檢視（D27）。不用 InterceptCommandExtension（第 4 階段沒有實作，O8）。
// 擁有者：WP4.6（本檔）。只在 UI 執行緒改狀態；只碰引擎的介面（ISyncEngineCommands），不碰 SQLite 與 SignalR（S6）
internal sealed partial class SyncCoordinator
{
    private const string ExportUnpairedText = "這台還沒配對，沒有遠端操作紀錄可以匯出";

    private const string ExportNoDatabaseText = "同步資料庫還沒開啟，沒有遠端操作紀錄可以匯出";

    private const string ExportTimeoutText = "匯出逾時，請稍後再試";

    internal const string TrashRestoredText = "已從回收區還原";

    internal const string TrashRestoreFailedText = "還原不了：同名的對話已存在或已不在回收區";

    /// <summary>目前掛在中樞的發布者（WP4.3；沒有配對、或引擎不發布對話時是 null）。UI 執行緒</summary>
    private ConversationPublisher? publisher;

    /// <summary>回補的進度（WP4.3 的發布者；沒有在回補、沒有發布者時是 null）：sync.backfill 與系統匣。UI 執行緒</summary>
    internal SyncBackfillProgress? Backfill => publisher?.Progress;

    /// <summary>回補因磁碟保護上限暫停中（只在系統匣顯示，D29）。UI 執行緒</summary>
    internal bool BackfillPaused => publisher?.Pause?.SinceMs is not null;

    /// <summary>
    /// 中樞換接收者之前（D23；CreateEngine 與解除配對以 <c>hub.AttachConversationSink(AttachPublisher(…))</c> 呼叫）：記下發布者、訂閱回補進度
    /// （改變就重送 sync）、照有沒有發布者設 <see cref="AgentHub.ShowReplicas"/>，回傳同一個發布者。先設 ShowReplicas 再掛接收者：
    /// 打開的那一刻還沒有接收者，不向任何人要副本；掛上時中樞的 OnReplicaSinkChanged 向新的接收者要整份清單一次。
    /// 開著的本機對話重送標頭（conversationSync 跟著接收者出現或消失，合併送出時接收者已經換好）
    /// </summary>
    private ConversationPublisher? AttachPublisher(ConversationPublisher? next)
    {
        if (!ReferenceEquals(publisher, next))
        {
            if (publisher is not null)
                publisher.ProgressChanged -= OnBackfillProgressChanged;
            publisher = next;
            if (next is not null)
                next.ProgressChanged += OnBackfillProgressChanged;
        }

        hub.ShowReplicas = next is not null;
        // 沒有發布者（建不起來、解除配對）就不會有複本：本來就沒在顯示時 ShowReplicas 不會變，這裡另外收掉等待
        if (next is null)
            hub.SettleReplicas();
        hub.RefreshSyncHeaders();
        return next;
    }

    /// <summary>回補進度或暫停改變（UI 執行緒，發布者的事件）：有狀態時重送 sync（含 backfill；系統匣跟著 StatusChanged 更新）</summary>
    private void OnBackfillProgressChanged()
    {
        if (!disposed && CurrentStatus() is not null)
            PostStatus();
    }

    /// <summary>
    /// 這台網頁的本機同步指令（D27；HandleCommand 在擋掉鏡像與同步檢視之後呼叫）：回 true＝已處理。
    /// 伺服器因 <c>sync</c> 開頭一律回 desktop_only（瀏覽器送不到這裡）
    /// </summary>
    private bool HandleLocalViewCommand(IAgentView view, string type, JObject msg)
    {
        switch (type)
        {
            case "syncLoadRemoteAudit":
                LoadRemoteAudit(view, msg);
                return true;
            case "syncExportRemoteAudit":
                _ = ExportRemoteAuditAsync(view.Id);
                return true;
            case "syncListTrash":
                ListTrash(view.Id);
                return true;
            case "syncRestoreTrash":
                RestoreTrash(view, msg);
                return true;
            default:
                return false;
        }
    }

    // ───────────────────────────── 遠端操作紀錄（D44） ─────────────────────────────

    /// <summary>
    /// <c>syncLoadRemoteAudit {before?, beforeSeq?, limit?}</c>：參數不合格整個指令忽略（不回覆）；沒有引擎（未配對）回空的一頁；
    /// 否則在引擎執行緒多讀一筆判斷 hasMore，回 UI 執行緒再送給發問的檢視
    /// </summary>
    private void LoadRemoteAudit(IAgentView view, JObject msg)
    {
        if (LocalSyncViewRules.ParseAuditRequest(msg) is not { } request)
        {
            Log.Info("syncLoadRemoteAudit 的參數不合格：忽略");
            return;
        }

        if (engine is not ISyncEngineCommands commands)
        {
            hub.PostToViewIfOpen(view.Id, LocalSyncViewRules.AuditPage([], request.Limit));
            return;
        }

        _ = LoadRemoteAuditAsync(view.Id, commands, request.Before, request.BeforeSeq, request.Limit);
    }

    private async Task LoadRemoteAuditAsync(string viewId, ISyncEngineCommands commands, long? before, long? beforeSeq, int limit)
    {
        IReadOnlyList<RemoteAuditEntry> rows;
        try
        {
            // 多讀一筆判斷 hasMore；sync.db 一次最多給 AuditMaxLimit 筆，limit 就是上限時另外以 (at, seq) 探下一筆
            var take = Math.Min(limit + 1, LocalSyncViewRules.AuditMaxLimit);
            rows = await commands.LoadRemoteAuditAsync(before, take, CancellationToken.None, beforeSeq).ConfigureAwait(false);
            if (take == limit && rows.Count == limit)
            {
                var oldest = rows[^1];
                var next = await commands.LoadRemoteAuditAsync(oldest.At, 1, CancellationToken.None, oldest.Seq).ConfigureAwait(false);
                rows = [.. rows, .. next];
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"讀遠端操作紀錄失敗：{ex.Message}");
            rows = [];
        }

        var page = LocalSyncViewRules.AuditPage(rows, limit);
        host.BeginInvoke(() =>
        {
            if (!disposed)
                hub.PostToViewIfOpen(viewId, page);
        });
    }

    /// <summary>
    /// <c>syncExportRemoteAudit</c>（D28）：匯出到解除配對時那個檔的同一個資料夾、檔名加時間（不蓋掉解除配對的那一份）；
    /// 在引擎執行緒寫、10 秒逾時，回 <c>remoteAuditExport {path, count, error}</c> 給發問的檢視
    /// </summary>
    private async Task ExportRemoteAuditAsync(string viewId)
    {
        JObject result;
        if (engine is not ISyncEngineCommands commands || device is not { } paired)
        {
            result = LocalSyncViewRules.AuditExportView(null, 0, ExportUnpairedText);
        }
        else
        {
            using var timeout = new CancellationTokenSource(RemoteAuditExportTimeout, time);
            try
            {
                var path = LocalSyncViewRules.ManualAuditExportPath(SyncCommandRules.RemoteAuditExportPath(paths, paired.DeviceId), time.GetLocalNow());
                var count = await commands.ExportRemoteAuditAsync(path, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                // sync.db 沒開時引擎回 0、不寫檔：沒有檔案就不給路徑（「在檔案總管顯示」找不到）
                result = File.Exists(path)
                    ? LocalSyncViewRules.AuditExportView(path, count, null)
                    : LocalSyncViewRules.AuditExportView(null, 0, ExportNoDatabaseText);
                Log.Info($"手動匯出遠端操作紀錄 {count} 列：{path}");
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                Log.Warn("手動匯出遠端操作紀錄逾時");
                result = LocalSyncViewRules.AuditExportView(null, 0, ExportTimeoutText);
            }
            catch (Exception ex)
            {
                Log.Warn($"手動匯出遠端操作紀錄失敗：{ex.Message}");
                result = LocalSyncViewRules.AuditExportView(null, 0, "匯出失敗：" + ex.Message);
            }
        }

        host.BeginInvoke(() =>
        {
            if (!disposed)
                hub.PostToViewIfOpen(viewId, result);
        });
    }

    // ───────────────────────────── 回收區（D27） ─────────────────────────────

    /// <summary>
    /// <c>syncListTrash</c>：回收區在 UI 執行緒取得（中樞的狀態），列舉資料夾與讀資訊檔在執行緒集區（ConversationTrash 可在任何執行緒呼叫），
    /// 回 UI 執行緒再送 <c>trash</c> 給發問的檢視。配對與否都可以看（解除配對之後回收區照樣在）
    /// </summary>
    private void ListTrash(string viewId)
    {
        var trash = hub.Trash;
        _ = Task.Run(trash.List).ContinueWith(t =>
        {
            IReadOnlyList<TrashEntry> entries = [];
            if (t.Status == TaskStatus.RanToCompletion)
                entries = t.Result;
            else
                Log.Warn($"讀回收區清單失敗：{t.Exception?.GetBaseException().Message}");
            var view = LocalSyncViewRules.TrashView(entries);
            host.BeginInvoke(() =>
            {
                if (!disposed)
                    hub.PostToViewIfOpen(viewId, view);
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// <c>syncRestoreTrash {conversationId}</c>：在 UI 執行緒 <see cref="AgentHub.RestoreFromTrash"/>（搬回與載入是中樞的狀態）；
    /// 成功、失敗各一則提示給送指令的視窗，之後重送一次清單（失敗多半是它已不在回收區，清單要跟著更新）。id 不是字串時忽略
    /// </summary>
    private void RestoreTrash(IAgentView view, JObject msg)
    {
        if (msg["conversationId"] is not JValue { Type: JTokenType.String, Value: string conversationId })
        {
            Log.Info("syncRestoreTrash 沒有 conversationId：忽略");
            return;
        }

        if (hub.RestoreFromTrash(conversationId))
            hub.LocalViewToast(view, TrashRestoredText, isError: false);
        else
            hub.LocalViewToast(view, TrashRestoreFailedText, isError: true);
        ListTrash(view.Id);
    }
}
