using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Security;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

// 協調者的遠端指令部分（WP4.4 計畫 D2、D3、D31～D33）：引擎經同一代的 EnginePort（IRemoteCommandPort）把驗過信封、記過 executing 的指令
// 送回 UI 執行緒進中樞閘門；sync 的 remoteSupported；睡眠恢復讓引擎重新 Hello；解除配對前匯出遠端操作紀錄；啟動時的遠端操作提醒（D30）。
// 協調者只碰引擎的介面（ISyncEngineCommands），不碰 SQLite 與 SignalR（S6）
internal sealed partial class SyncCoordinator
{
    /// <summary>解除配對前匯出遠端操作紀錄的時限（D33）：逾時只記錄，解除配對照常</summary>
    internal static readonly TimeSpan RemoteAuditExportTimeout = TimeSpan.FromSeconds(10);

    private const string NotStartedStopping = "這台電腦的同步正在停止，指令沒有執行，請稍後再試";

    private const string NotStartedUnpaired = "這台電腦已解除配對或正在關閉同步，指令沒有執行";

    /// <summary>引擎回報的「這台能不能執行遠端指令」與回報它的世代（舊一代的不算；還沒回報是 null，D31）。UI 執行緒</summary>
    private (int Gen, bool Supported)? commandsSupported;

    /// <summary>
    /// 從睡眠或休眠恢復（任何執行緒）：回 UI 執行緒，讓引擎的伺服器時鐘失效並重新 Hello（D32；退避中的引擎照 RetryNow、保留 notBefore）。
    /// 沒有遠端指令功能的引擎（測試替身）照舊立刻重試
    /// </summary>
    public void OnSystemResumed() => host.BeginInvoke(() =>
    {
        if (disposed)
            return;
        switch (engine)
        {
            case ISyncEngineCommands commands:
                commands.OnSystemResumed();
                break;
            case { } other:
                other.RetryNow();
                break;
        }
    });

    /// <summary>
    /// 程式啟動、讀完配對資料並建好引擎之後（OnLoaded，只有一次）：遠端操作不是「不接受」就在系統匣提醒一次（契約 D26、計畫 D30）。
    /// 政策照 Hello 回報的（暫停遠端操作時是 Off，不提醒）；不看「通知遠端操作」：讓被別的程式改了的設定看得見。失敗只記錄
    /// </summary>
    private void RemindRemoteControlOnStartup()
    {
        try
        {
            if (engine is null)
                return;
            var policy = SyncCoordinatorRules.HelloPolicy(settings.Sync);
            if (RemoteNotifyRules.StartupReminder(policy.RemoteControlLevel, policy.AllowRemoteFullAccess) is not { } message)
                return;
            Log.Info($"啟動提醒：遠端操作是 {policy.RemoteControlLevel}（允許完全自動：{policy.AllowRemoteFullAccess}）");
            host.Notify(RemoteNotifyRules.StartupReminderTitle, message, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"啟動時的遠端操作提醒失敗：{ex.Message}");
        }
    }

    /// <summary>
    /// sync 的第 4 階段選用欄位（D31）；未配對與配對中 remoteSupported 是 null（不輸出）。〔WP4.6 D26〕已配對時加 local.notifyRemoteActions、
    /// 回補中加 backfill（發布者的進度）
    /// </summary>
    private SyncStatusExtras RemoteExtras() => new(NotifyRemoteActions: bridge is null ? null : settings.Sync.NotifyRemoteActions, Backfill: Backfill, RemoteSupported: RemoteSupported(),
        PeerLive: engine is null ? null : PeerLiveNow(), AllowRemoteExec: bridge is null || remoteExec is null ? null : settings.Sync.AllowRemoteExec);

    /// <summary>
    /// 這台的程式能不能執行遠端指令（web-protocol 的 <c>sync.remoteSupported</c>，D31）：這一代的引擎回報過就照它（D6：有 port、aud 合格、
    /// sync.db 有指令的表）；還沒回報（離線、還沒 Hello）時看配對網址的 aud 合不合格。UI 執行緒
    /// </summary>
    private bool? RemoteSupported()
    {
        if (engine is null || device is not { } paired)
            return null;
        return commandsSupported is { } reported && reported.Gen == Volatile.Read(ref generation)
            ? reported.Supported
            : SyncCommandRules.Audience(paired.ServerUrl) is not null;
    }

    /// <summary>引擎每次 Hello 後的回報（任何執行緒）：回 UI 執行緒記下（舊一代的丟掉）；值變了就重送 sync</summary>
    private void OnCommandsSupported(int gen, bool supported) => host.BeginInvoke(() =>
    {
        if (disposed || engine is null || gen != Volatile.Read(ref generation))
            return;
        var before = RemoteSupported();
        commandsSupported = (gen, supported);
        if (RemoteSupported() != before && CurrentStatus() is not null)
            PostStatus();
    });

    /// <summary>
    /// 引擎送來的遠端指令（任何執行緒，D3）：經 BeginInvoke 回 UI 執行緒，確認還是同一代、協調者沒有釋放才進中樞閘門。
    /// 還沒開始就取消、換了世代或已釋放：確定沒有執行，回 rejected／busy、Started=false；已經開始就照樣等閘門的結果，不理會取消
    /// （至多一次要分清「沒執行」與「不明」，契約 D21）。<paramref name="ct"/> 是引擎生命期的權杖
    /// </summary>
    private Task<RemoteCommandRun> ExecuteRemote(int gen, CommandEnvelopeBody body, Action<JObject> output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(output);
        var done = new TaskCompletionSource<RemoteCommandRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested)
        {
            done.TrySetResult(NotStarted(body, NotStartedStopping));
            return done.Task;
        }

        // 0＝還沒開始、1＝開始了、-1＝不開始（取消）：先到的為準
        var state = 0;
        var registration = ct.Register(() =>
        {
            if (Interlocked.CompareExchange(ref state, -1, 0) == 0)
                done.TrySetResult(NotStarted(body, NotStartedStopping));
        });
        try
        {
            host.BeginInvoke(() =>
            {
                registration.Dispose();
                if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                    return;
                if (disposed || gen != Volatile.Read(ref generation))
                {
                    done.TrySetResult(NotStarted(body, NotStartedUnpaired));
                    return;
                }

                _ = RunRemoteGateAsync(body, output, done);
            });
        }
        catch (Exception ex)
        {
            // 視窗已經關閉、排不進 UI 執行緒：確定沒有執行
            registration.Dispose();
            Log.Warn($"遠端指令 {body.Cid} 排不進 UI 執行緒，不執行：{ex.Message}");
            if (Interlocked.CompareExchange(ref state, -1, 0) == 0)
                done.TrySetResult(NotStarted(body, NotStartedStopping));
        }

        return done.Task;
    }

    /// <summary>進中樞閘門（UI 執行緒）；政策與暫停在同一個 UI 工作裡讀（只進遠端操作紀錄，閘門 5e 自己讀同一份設定）</summary>
    private async Task RunRemoteGateAsync(CommandEnvelopeBody body, Action<JObject> output, TaskCompletionSource<RemoteCommandRun> done)
    {
        var policy = SyncCoordinatorRules.HelloPolicy(settings.Sync).RemoteControlLevel;
        var paused = settings.Sync.RemotePaused;
        try
        {
            var outcome = await hub.ExecuteRemoteCommandAsync(body, output);
            done.TrySetResult(new RemoteCommandRun(outcome, policy, paused, Started: true));
        }
        catch (Exception ex)
        {
            // 閘門開始之後的例外：引擎把它當成 unknown（指令可能已經執行）
            done.TrySetException(ex);
        }
    }

    /// <summary>確定沒有進閘門的結果（busy：什麼都沒執行，durable 由伺服器重簽再試；停在第一步）</summary>
    private static RemoteCommandRun NotStarted(CommandEnvelopeBody body, string message) =>
        new(new RemoteOutcome(RemoteResultStates.Rejected, RemoteResultCodes.Busy, message, body.Tier, body.FullAccess, RemoteGateSteps.Route), null, null, Started: false);

    /// <summary>
    /// 解除配對前把遠端操作紀錄匯出成 <c>{資料根}\remote-audit\{deviceId}.jsonl</c>（D33；之後 sync.db 會被刪掉）：在停止引擎之前、
    /// 10 秒逾時；失敗或逾時只記錄，解除配對照常。協調者不碰 SQLite，匯出在引擎執行緒做
    /// </summary>
    private async Task ExportBeforeUnpairAsync(ISyncEngine stopping, DeviceFileData old)
    {
        if (stopping is not ISyncEngineCommands commands)
            return;
        var path = SyncCommandRules.RemoteAuditExportPath(paths, old.DeviceId);
        using var timeout = new CancellationTokenSource(RemoteAuditExportTimeout, time);
        try
        {
            var count = await commands.ExportRemoteAuditAsync(path, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            Log.Info($"解除配對前匯出遠端操作紀錄 {count} 列：{path}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Log.Warn($"解除配對前匯出遠端操作紀錄逾時（{RemoteAuditExportTimeout.TotalSeconds:0} 秒）：照常解除配對");
        }
        catch (Exception ex)
        {
            Log.Warn($"解除配對前匯出遠端操作紀錄失敗（照常解除配對）：{ex.Message}");
        }
    }

    /// <summary>一個引擎的遠端指令入口：只轉給同一代（解除配對後的舊一代確定不執行）</summary>
    private sealed partial class EnginePort : IRemoteCommandPort
    {
        public Task<RemoteCommandRun> ExecuteRemoteAsync(CommandEnvelopeBody body, Action<JObject> output, CancellationToken ct) =>
            owner.ExecuteRemote(gen, body, output, ct);

        public void ReportCommandsSupported(bool supported) => owner.OnCommandsSupported(gen, supported);
    }
}
