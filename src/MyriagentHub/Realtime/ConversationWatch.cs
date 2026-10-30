using System.Collections.Concurrent;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Microsoft.Extensions.Options;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Realtime;

/// <summary>
/// 觀看追蹤（WP4.2 計畫步 12；R15，D25、P4、F34）：依瀏覽器連線「看得到」的對話（<see cref="WatchRules"/>）算出每台擁有者的觀看清單，
/// 內容改變時對那台每一條協商出 <c>live1</c> 的已 Prove 連線送 <c>WatchChanged</c>（整份取代）；連線 Prove 時也送一次目前的清單（可能是空的）。
/// <list type="bullet">
/// <item>重算的時機：ClientState 或 <c>visible</c> 指令的清單換了（<see cref="BrowserConnections.StateChanged"/>）、瀏覽器連線移除、對話有變更
/// （<see cref="ConversationChangeFeed.Posted"/>：政策降級、刪除、改回 full、新建）、<see cref="Recompute"/>（WP4.5、第 5 階段）。</item>
/// <item>事件處理者只記下並喚醒（不可阻塞）；背景迴圈合併 50 ms 之後算一次、在背景送（不在連線事件裡同步送：Prove 的事件在 ProveResponse 送到裝置之前觸發，F34），
/// 送失敗只記錄。</item>
/// </list>
/// 狀態以一把鎖保護；送出依 <see cref="FlushAsync"/> 的順序（同一時間只有一個）
/// </summary>
internal sealed class ConversationWatch(
    BrowserConnections browsers,
    ConversationDirectoryCache directory,
    ConnectionRegistry registry,
    ConversationChangeFeed feed,
    IDeviceMessageSender sender,
    IOptions<HubOptions> options,
    TimeProvider time,
    ILogger<ConversationWatch> log,
    MyriagentHub.Auth.CollaboratorDirectory? collaborators = null) : BackgroundService
{
    /// <summary>事件之後等多久才算（合併同一段時間的多次改變）</summary>
    public static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(50);

    private readonly object sync = new();
    private readonly Dictionary<string, IReadOnlyList<string>> current = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<DeviceConnection> proven = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly SemaphoreSlim flushing = new(1, 1);
    private int dirty;
    private int attached;

    /// <summary>標成要重算並喚醒（任何執行緒；不阻塞）</summary>
    public void Recompute()
    {
        Interlocked.Exchange(ref dirty, 1);
        Signal();
    }

    /// <summary>這台裝置目前的觀看清單（測試與營運用；最後一次算的結果）</summary>
    public IReadOnlyList<string> WatchedBy(string deviceId)
    {
        lock (sync)
            return current.TryGetValue(deviceId, out var list) ? list : [];
    }

    /// <summary>訂閱連線與變更的事件（正式由 <see cref="StartAsync"/> 呼叫；測試直接呼叫、以 <see cref="FlushAsync"/> 驅動）</summary>
    public void Attach()
    {
        if (Interlocked.Exchange(ref attached, 1) == 1)
            return;
        browsers.StateChanged += OnBrowserChanged;
        browsers.Removed += OnBrowserChanged;
        feed.Posted += Recompute;
        registry.ConnectionProven += OnProven;
        Recompute();
    }

    public void Detach()
    {
        if (Interlocked.Exchange(ref attached, 0) == 0)
            return;
        browsers.StateChanged -= OnBrowserChanged;
        browsers.Removed -= OnBrowserChanged;
        feed.Posted -= Recompute;
        registry.ConnectionProven -= OnProven;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Attach();
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Detach();
        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// 算一次並送：要重算時以目前的瀏覽器連線與目錄重算每台的清單，改變了的裝置對它每一條 live1 的已 Prove 連線送；再對剛 Prove 的連線送它那台目前的清單
    /// （同一次已經送過的不重送）
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await flushing.WaitAsync(cancellationToken);
        try
        {
            var sends = new List<(DeviceConnection Connection, IReadOnlyList<string> Ids)>();
            lock (sync)
            {
                if (Interlocked.Exchange(ref dirty, 0) == 1)
                {
                    // 協作人的連線只算分享給他的對話（多人協作）
                    var next = WatchRules.ByOwner(browsers.Snapshot().Select(c => collaborators is null ? ViewerState.Of(c)
                        : ViewerState.Of(c, id => collaborators.CanSee(c.CollaboratorId, id))), Find, options.Value.Conversations.MaxWatchIds);
                    foreach (var deviceId in current.Keys.Union(next.Keys, StringComparer.Ordinal).ToList())
                    {
                        var before = current.TryGetValue(deviceId, out var b) ? b : [];
                        var after = next.TryGetValue(deviceId, out var a) ? a : [];
                        if (before.SequenceEqual(after, StringComparer.Ordinal))
                            continue;
                        if (after.Count == 0)
                            current.Remove(deviceId);
                        else
                            current[deviceId] = after;
                        foreach (var connection in registry.ConnectionsOf(deviceId).Where(Accepts))
                            sends.Add((connection, after));
                    }
                }
                while (proven.TryDequeue(out var connection))
                {
                    if (!ReferenceEquals(registry.Get(connection.ConnectionId), connection) || !Accepts(connection)
                        || sends.Any(s => ReferenceEquals(s.Connection, connection)))
                        continue;
                    sends.Add((connection, current.TryGetValue(connection.DeviceId, out var list) ? list : []));
                }
            }
            foreach (var (connection, ids) in sends)
            {
                try
                {
                    await sender.SendAsync(connection.ConnectionId, DeviceHubMethods.WatchChanged, new WatchChanged(ids), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    log.LogWarning(ex, "送 WatchChanged 失敗：{DeviceId} {Connection}", connection.DeviceId, connection.ConnectionId);
                }
            }
        }
        finally
        {
            flushing.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await signal.WaitAsync(stoppingToken);
                await Task.Delay(Coalesce, time, stoppingToken);
                await FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "觀看清單重算失敗");
                Recompute();
            }
        }
    }

    /// <summary>送得到這條連線嗎：已 Prove、協商出 live1（DeviceFeatureRules.RequiredFor）</summary>
    private static bool Accepts(DeviceConnection connection) => connection.IsProven && connection.Allows(DeviceHubMethods.WatchChanged);

    private ConversationEntry? Find(string conversationId) => directory.TryGet(conversationId, out var entry) ? entry : null;

    private void OnBrowserChanged(BrowserConnection connection) => Recompute();

    private void OnProven(DeviceConnection connection)
    {
        if (!connection.Allows(DeviceHubMethods.WatchChanged))
            return;
        proven.Enqueue(connection);
        Signal();
    }

    private void Signal()
    {
        if (signal.CurrentCount > 0)
            return;
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 另一個執行緒同時喚醒了：一次就夠
        }
    }

    public override void Dispose()
    {
        signal.Dispose();
        flushing.Dispose();
        base.Dispose();
    }
}
