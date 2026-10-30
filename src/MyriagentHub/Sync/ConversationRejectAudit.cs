using System.Collections.Concurrent;
using MyriagentHub.Audit;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Sync;

/// <summary>
/// 對話事件的被拒與再遮蔽的彙總稽核（WP4.2 計畫 D8、答 5）：<see cref="Add"/>／<see cref="AddRemasked"/> 只累加計數（O(1)，寫入者的 AfterCommit 呼叫）；
/// 每 <c>Hub:Conversations:RejectAuditWindowSeconds</c>（60）秒對有計數的裝置各寫<b>一筆</b>稽核：窗內有拒收時 action 是
/// <see cref="AuditActions.SyncConversationRejected"/>，只有再遮蔽時是 <see cref="AuditActions.SyncConversationRemasked"/>；
/// detail 是 <c>{rejected:{原因:{種類:筆數}}, remasked:{種類:筆數}}</c>，不含對話 id 與任何內容。大量被拒時不會灌爆 hub.db。停止時把剩下的寫完。
/// </summary>
internal sealed class ConversationRejectAudit(IOptions<HubOptions> options, AuditLog audit, TimeProvider time, ILogger<ConversationRejectAudit> log)
    : BackgroundService
{
    private readonly ConcurrentDictionary<string, Window> windows = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim flushing = new(1, 1);

    /// <summary>一筆被拒的對話事件（<paramref name="reason"/> 是拒絕碼、<paramref name="kind"/> 是對話事件的種類：都是固定的名稱，不含內容）</summary>
    public void Add(string deviceId, string reason, string kind) => windows.GetOrAdd(deviceId, _ => new Window()).Add(reason, kind);

    /// <summary>一筆再遮蔽後接受的對話事件</summary>
    public void AddRemasked(string deviceId, string kind) => windows.GetOrAdd(deviceId, _ => new Window()).Add(null, kind);

    /// <summary>把目前累計的寫成稽核（每台一筆；時間窗到了、停止時與測試呼叫）</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await flushing.WaitAsync(cancellationToken);
        try
        {
            foreach (var deviceId in windows.Keys.Order(StringComparer.Ordinal).ToList())
            {
                if (!windows.TryGetValue(deviceId, out var window) || window.Take() is not { } counts)
                    continue;
                var action = counts.Rejected.Count > 0 ? AuditActions.SyncConversationRejected : AuditActions.SyncConversationRemasked;
                var outcome = counts.Rejected.Count > 0 ? AuditOutcomes.Denied : AuditOutcomes.Ok;
                await audit.WriteAsync(new AuditEntry(AuditActors.Device, deviceId, null, action, null, outcome,
                    new { rejected = counts.Rejected, remasked = counts.Remasked }), cancellationToken);
            }
        }
        finally
        {
            flushing.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.Conversations.RejectAuditWindowSeconds);
        try
        {
            using var timer = new PeriodicTimer(interval, time);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await FlushAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 服務停止
        }
        finally
        {
            try
            {
                await FlushAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "對話事件的彙總稽核在停止時沒有寫完");
            }
        }
    }

    private sealed record Counts(SortedDictionary<string, SortedDictionary<string, int>> Rejected, SortedDictionary<string, int> Remasked);

    /// <summary>一台裝置在目前時間窗的計數（以自己的鎖保護；只有計數，不含內容）</summary>
    private sealed class Window
    {
        private readonly Lock gate = new();

        private SortedDictionary<string, SortedDictionary<string, int>> rejected = new(StringComparer.Ordinal);

        private SortedDictionary<string, int> remasked = new(StringComparer.Ordinal);

        /// <param name="reason">null＝再遮蔽後接受</param>
        public void Add(string? reason, string kind)
        {
            lock (gate)
            {
                SortedDictionary<string, int> target;
                if (reason is null)
                {
                    target = remasked;
                }
                else if (!rejected.TryGetValue(reason, out target!))
                {
                    target = new SortedDictionary<string, int>(StringComparer.Ordinal);
                    rejected[reason] = target;
                }
                target[kind] = target.TryGetValue(kind, out var n) ? n + 1 : 1;
            }
        }

        /// <summary>取走目前的計數並換成空的；沒有任何計數回 null</summary>
        public Counts? Take()
        {
            lock (gate)
            {
                if (rejected.Count == 0 && remasked.Count == 0)
                    return null;
                var taken = new Counts(rejected, remasked);
                rejected = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
                remasked = new SortedDictionary<string, int>(StringComparer.Ordinal);
                return taken;
            }
        }
    }
}
