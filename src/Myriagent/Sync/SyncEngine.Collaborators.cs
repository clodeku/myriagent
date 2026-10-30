using Myriagent.Core;

namespace Myriagent.Sync;

/// <summary>
/// 引擎 → 協調者的多人協作（collab1；協調者的 EnginePort 實作，引擎以 <c>args.Host as ICollaboratorSyncPort</c> 取得）：
/// 伺服器送來的協作人清單與分享（整份取代），協調者在 UI 執行緒交給中樞（AgentHub.Collaborators.cs）
/// </summary>
internal interface ICollaboratorSyncPort
{
    void CollaboratorsUpdated(CollaboratorsState state);
}

/// <summary>協調者給引擎的要求（<c>engine as ISyncEngineCollaborators</c>）：替擁有者送一則管理指令（任何執行緒）</summary>
internal interface ISyncEngineCollaborators
{
    Task<CollabCmdResult> CollabCmdAsync(CollabCmdRequest request, CancellationToken ct);
}

// 多人協作（sync-protocol 14.14，collab1）：伺服器在連線證明完成時與名冊改變時送 CollabUpdate（整份取代），這裡只轉給協調者；
// 擁有者在這台的設定頁或對話選單管理協作人時，經目前這條協商出 collab1 的連線 Invoke CollabCmd。沒有連線、或連線沒有 collab1 時回 unavailable
internal sealed partial class SyncEngine : ISyncEngineCollaborators
{
    private ICollaboratorSyncPort? CollaboratorsPort => args.Host as ICollaboratorSyncPort;

    public Task<CollabCmdResult> CollabCmdAsync(CollabCmdRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var done = new TaskCompletionSource<CollabCmdResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread.Post(() =>
        {
            if (current is not { Proven: true, UpdateOnly: false } conn || conn.Session is not { } session || !DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Collab1))
            {
                done.TrySetResult(new CollabCmdResult(false, CollaboratorCodes.Unavailable));
                return;
            }
            Spawn(async () =>
            {
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(conn.Token, ct);
                    done.TrySetResult(await session.CollabCmdAsync(request, linked.Token));
                }
                catch (Exception ex)
                {
                    if (ex is not OperationCanceledException)
                        Log.Warn($"協作人的管理指令沒有送到伺服器：{ex.Message}");
                    done.TrySetResult(new CollabCmdResult(false, CollaboratorCodes.Unavailable));
                }
            }, conn);
        });
        return done.Task;
    }

    /// <summary><c>CollabUpdate</c>：格式不合格略過，否則交給協調者</summary>
    private void OnCollabUpdate(SyncConnection conn, CollaboratorsState state)
    {
        if (CollaboratorsPort is not { } port || conn.UpdateOnly || !IsCurrent(conn) || !DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Collab1))
            return;
        if (state.List is null || state.Shares is null || state.List.Count > CollaboratorRules.MaxCollaborators)
        {
            Log.Warn("伺服器送來格式不合格的 CollabUpdate：略過");
            return;
        }
        try
        {
            port.CollaboratorsUpdated(state);
        }
        catch (Exception ex)
        {
            Log.Error("套用協作人清單失敗", ex);
        }
    }
}
