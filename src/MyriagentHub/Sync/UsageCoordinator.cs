using System.Collections.Concurrent;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Realtime;
using Newtonsoft.Json;

namespace MyriagentHub.Sync;

/// <summary>UsagePublish 的處理結果：<see cref="Ignored"/>＝合法但不採用（舊 term、不是負責的那台、沒有連結、太頻繁），裝置不必知道</summary>
internal enum UsagePublishResult
{
    Accepted,
    Ignored,
    Invalid,
}

/// <summary>
/// 共享用量（sync-protocol 6.4）：
/// <list type="bullet">
/// <item>指派：每個邏輯帳戶由一台查官方用量（<see cref="UsageRules.Choose"/>），換人時 term＋1、寫 usage_pollers、發布 account.upsert（矩陣的「負責查詢」），
/// 提交之後對新舊兩台送 UsageAssign。每 15 秒、裝置上下線、目錄有變動時重算；負責的那台 60 秒沒心跳就改派。</item>
/// <item>上傳：帶 term 的只採用目前負責那台、目前 term 的；不帶 term 的（對話中的 rate limit 事件）任何有連結且身分相符的裝置都可以，每台每帳戶有最短間隔。
/// 寫 usage_latest、usage_points（14 天）後發布 usage.snapshot。被採用的帶 term 上傳在提交之後記進 <see cref="UsagePollStats"/>（ops/status 的 usagePollsPerHour）。</item>
/// </list>
/// </summary>
internal sealed class UsageCoordinator(
    SyncWriter writer,
    Catalog catalog,
    ConnectionRegistry connections,
    DeviceDirectory devices,
    SyncNotifier notifier,
    UsagePollStats polls,
    ILogger<UsageCoordinator> log) : BackgroundService
{
    /// <summary>定期重算的間隔（真實時間）：心跳逾時 60 秒，15 秒內會發現</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim wake = new(0, 1);

    private readonly ConcurrentDictionary<(string DeviceId, string LogicalAccountId), long> lastSessionPublish = new();

    /// <summary>指派變了（已提交）：對這台送 UsageAssign（裝置 hub 訂閱；不在線的收不到，重連時 ProveResponse 帶完整的清單）</summary>
    public event Action<string, UsageAssign>? Assigned;

    /// <summary>要求盡快重算（合併：已經排了就不再排）</summary>
    public void RequestReassess()
    {
        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已經排了
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnPresence(string _) => RequestReassess();
        void OnCommitted(string cls, long _)
        {
            if (cls == SyncClasses.Catalog)
                RequestReassess();
        }
        connections.PresenceChanged += OnPresence;
        notifier.EventCommitted += OnCommitted;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await wake.WaitAsync(Interval, stoppingToken);
                try
                {
                    await ReassessAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "用量查詢的指派重算失敗");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 服務停止
        }
        finally
        {
            connections.PresenceChanged -= OnPresence;
            notifier.EventCommitted -= OnCommitted;
        }
    }

    /// <summary>重算所有邏輯帳戶的負責裝置；回傳這次送出的 UsageAssign。完成時，之前（任何一次重算）提交的指派都已送出</summary>
    public Task<IReadOnlyList<(string DeviceId, UsageAssign Message)>> ReassessAsync(CancellationToken cancellationToken = default) =>
        writer.WriteAsync(Reassess, cancellationToken);

    /// <summary>這台目前負責的帳戶（ProveResponse 的 usageAssignments）</summary>
    public IReadOnlyList<UsageAssign> AssignmentsFor(string deviceId)
    {
        using var connection = writer.OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT logical_account_id, term FROM usage_pollers WHERE device_id = $d ORDER BY logical_account_id";
        cmd.Parameters.AddWithValue("$d", deviceId);
        using var reader = cmd.ExecuteReader();
        var list = new List<UsageAssign>();
        while (reader.Read())
            list.Add(new UsageAssign(reader.GetString(0), reader.GetInt64(1), true));
        return list;
    }

    public async Task<UsagePublishResult> PublishAsync(string deviceId, UsagePublish? publish, CancellationToken cancellationToken = default)
    {
        if (UsageRules.Validate(publish) is not null)
            return UsagePublishResult.Invalid;
        // 節流的檢查與記錄都在寫入者（單一執行緒）上：同一台重連時新舊兩條連線同時送，也不會兩次都被採用
        return await writer.WriteAsync(context => Publish(context, deviceId, publish!), cancellationToken);
    }

    private IReadOnlyList<(string DeviceId, UsageAssign Message)> Reassess(SyncWriteContext context)
    {
        var now = context.Now;
        var accounts = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = context.Command("SELECT logical_account_id, provider FROM logical_accounts WHERE deleted_at IS NULL"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                accounts[reader.GetString(0)] = reader.GetString(1);
        }
        var pollers = new Dictionary<string, (string DeviceId, long Term)>(StringComparer.Ordinal);
        using (var cmd = context.Command("SELECT logical_account_id, device_id, term FROM usage_pollers"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                pollers[reader.GetString(0)] = (reader.GetString(1), reader.GetInt64(2));
        }
        var candidates = new Dictionary<string, List<PollerCandidate>>(StringComparer.Ordinal);
        using (var cmd = context.Command("SELECT logical_account_id, device_id, status, identity_ok, access_expires_at, sessions_json FROM account_links"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var deviceId = reader.GetString(1);
                if (devices.Get(deviceId) is not { Revoked: false })
                    continue;
                var candidate = new PollerCandidate(
                    deviceId,
                    reader.GetString(2),
                    reader.GetInt64(3) != 0,
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? 0 : RunningSessions(reader.GetString(5)),
                    // 只更新的連線不算（sync-protocol 1.4）：它的 UsagePublish 被 hub 的關卡擋下，指派給它的帳戶就沒人查
                    connections.IsSyncing(deviceId),
                    connections.LastHeartbeat(deviceId));
                var laId = reader.GetString(0);
                if (!candidates.TryGetValue(laId, out var list))
                    candidates[laId] = list = [];
                list.Add(candidate);
            }
        }

        var changes = new List<(string, UsageAssign)>();
        foreach (var laId in accounts.Keys.Union(pollers.Keys).Order(StringComparer.Ordinal))
        {
            pollers.TryGetValue(laId, out var current);
            var chosen = accounts.TryGetValue(laId, out var provider)
                ? UsageRules.Choose(candidates.GetValueOrDefault(laId) ?? [], current.DeviceId, provider, now)
                : null;
            var next = chosen ?? string.Empty;
            if ((current.DeviceId ?? string.Empty) == next)
                continue;
            var term = current.Term + 1;
            context.Execute(
                "INSERT INTO usage_pollers(logical_account_id, device_id, term, assigned_at) VALUES($id, $d, $term, $now) "
                + "ON CONFLICT(logical_account_id) DO UPDATE SET device_id = excluded.device_id, term = excluded.term, assigned_at = excluded.assigned_at",
                ("$id", laId), ("$d", next), ("$term", term), ("$now", now));
            if (provider is not null)
                catalog.EmitAccount(context, laId);
            if (!string.IsNullOrEmpty(current.DeviceId))
                changes.Add((current.DeviceId, new UsageAssign(laId, term, false)));
            if (chosen is not null)
                changes.Add((chosen, new UsageAssign(laId, term, true)));
        }
        // 提交之後、依提交順序送出（在寫入執行緒上：訂閱者只能把訊息排進連線，不可阻塞）
        if (changes.Count > 0)
        {
            context.AfterCommit(() =>
            {
                foreach (var (deviceId, message) in changes)
                    Assigned?.Invoke(deviceId, message);
            });
        }
        return changes;
    }

    private UsagePublishResult Publish(SyncWriteContext context, string deviceId, UsagePublish publish)
    {
        var laId = publish.LogicalAccountId;
        if (context.Scalar("SELECT COUNT(*) FROM logical_accounts WHERE logical_account_id = $id AND deleted_at IS NULL", ("$id", laId)) is not 1L)
            return UsagePublishResult.Ignored;
        // 這台必須連結著這個帳戶、連結正常而且身分相符（改登別的帳號的那台，查到的是別人的用量）
        if (context.Scalar("SELECT identity_ok FROM account_links WHERE logical_account_id = $id AND device_id = $d AND status = $ready",
                ("$id", laId), ("$d", deviceId), ("$ready", UsageRules.LinkReady)) is not 1L)
            return UsagePublishResult.Ignored;
        var key = (deviceId, laId);
        if (publish.Term is null && lastSessionPublish.TryGetValue(key, out var last) && context.Now - last < UsageRules.SessionPublishMinIntervalMs)
            return UsagePublishResult.Ignored;
        if (publish.Term is { } term)
        {
            var isPoller = context.Scalar("SELECT COUNT(*) FROM usage_pollers WHERE logical_account_id = $id AND device_id = $d AND term = $term",
                ("$id", laId), ("$d", deviceId), ("$term", term)) is 1L;
            if (!isPoller)
                return UsagePublishResult.Ignored;
        }

        var usage = publish.Usage;
        context.Execute(
            "INSERT INTO usage_latest(logical_account_id, snapshot_json, source_device_id, source, fetched_at) VALUES($id, $json, $d, $source, $fetched) "
            + "ON CONFLICT(logical_account_id) DO UPDATE SET snapshot_json = excluded.snapshot_json, source_device_id = excluded.source_device_id, "
            + "source = excluded.source, fetched_at = excluded.fetched_at",
            ("$id", laId), ("$json", SyncJson.Serialize(usage)), ("$d", deviceId), ("$source", usage.Source), ("$fetched", usage.FetchedAt));
        // 歷史點以伺服器收到的時間記（不看裝置的時鐘）
        foreach (var window in usage.Windows)
        {
            context.Execute("INSERT OR REPLACE INTO usage_points(logical_account_id, window_key, at, percent) VALUES($id, $key, $at, $percent)",
                ("$id", laId), ("$key", window.Key), ("$at", context.Now), ("$percent", window.Percent));
        }
        context.Execute("DELETE FROM usage_points WHERE logical_account_id = $id AND at < $cut", ("$id", laId), ("$cut", context.Now - UsageRules.PointsRetentionMs));
        // 寫入者上依序處理：同一個合併交易裡的下一個上傳就看得到（提交後才記的話，同一批的兩個都會被採用）
        if (publish.Term is null)
        {
            lastSessionPublish[key] = context.Now;
        }
        else
        {
            // usagePollsPerHour：負責那台每查一次官方用量就帶 term 上傳一次；提交之後才記（合併交易回滾的不算）
            var at = context.Now;
            context.AfterCommit(() => polls.Record(laId, at));
        }

        SyncEventStore.InsertServer(context, notifier, SyncEventKinds.UsageSnapshot, new UsageSnapshotPayload(laId, usage, deviceId, publish.Term));
        return UsagePublishResult.Accepted;
    }

    private static int RunningSessions(string json)
    {
        try
        {
            return SyncJson.Deserialize<LinkSessions>(json)?.Running ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    public override void Dispose()
    {
        wake.Dispose();
        base.Dispose();
    }
}
