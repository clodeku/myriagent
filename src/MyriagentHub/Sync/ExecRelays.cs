using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Realtime;

namespace MyriagentHub.Sync;

/// <summary>
/// 遠端執行指令的串流轉送（docs/sync-protocol.md 14.11，<c>exec1</c>；2026-10-25）：<c>remoteExec</c> 照一般的遠端指令簽發（<see cref="CommandRouter"/>
/// 寫列、送出之前在這裡登記 cid → 發起的電腦與它的 commandId），之後：
/// <list type="bullet">
/// <item>被操作的電腦以 <c>ExecOutput</c> 送輸出、狀態與結束碼：只收登記的目標電腦送來的，commandId 換成發起時的，以 <c>ExecReply</c> 轉給發起電腦
/// 目前 <c>Order</c> 最大、已證明、協商出 <c>exec1</c> 的連線（重連之後照樣收得到）；不保存。結束（<c>exit</c>）那一段轉送之後就忘掉。</item>
/// <item>發起的電腦連不上超過 <see cref="RemoteExecRules.OriginLostLimit"/>、伺服器不認得這個 cid（重啟、過期、已結束）時回 <c>keep: false</c>，
/// 被操作的電腦停止那個指令（不讓沒人收的指令一直跑）。</item>
/// <item>發起的電腦以 <c>ExecCancel</c> 要求停止：只認它自己下的，轉成 <c>ExecAbort</c> 給目標電腦每條協商出 <c>exec1</c> 的連線。</item>
/// </list>
/// 只在記憶體（伺服器重啟後全部忘掉，被操作的電腦下一段就收到 keep false）；最多 <see cref="MaxEntries"/> 筆，滿了先丟最舊的。任何執行緒都能呼叫（一把鎖）
/// </summary>
internal sealed class ExecRelays(ConnectionRegistry registry, IDeviceMessageSender sender, TimeProvider time, ILogger<ExecRelays> log)
{
    public const int MaxEntries = 1024;

    private readonly object gate = new();

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    private sealed class Entry(string cid, string originDeviceId, string originCommandId, string targetDeviceId, long expiresAtMs, long sequence)
    {
        public string Cid { get; } = cid;

        public string OriginDeviceId { get; } = originDeviceId;

        public string OriginCommandId { get; } = originCommandId;

        public string TargetDeviceId { get; } = targetDeviceId;

        public long ExpiresAtMs { get; } = expiresAtMs;

        public long Sequence { get; } = sequence;

        /// <summary>從什麼時候起發起的電腦沒有能收的連線（null＝有）</summary>
        public long? OriginMissingSinceMs { get; set; }
    }

    private long sequence;

    /// <summary>目前登記的筆數</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    /// <summary>
    /// 登記一個已簽發的 <c>remoteExec</c>（<see cref="CommandRouter"/> 送 <c>CommandDelivery</c> 之前）：期限是指令的上限時間加上
    /// <see cref="RemoteExecRules.RelayGrace"/>（等待同意、最後一段在路上）
    /// </summary>
    public void Register(string cid, string originDeviceId, string originCommandId, string targetDeviceId, int timeoutSeconds)
    {
        var now = NowMs();
        var expires = now + (long)(TimeSpan.FromSeconds(RemoteExecRules.TimeoutSeconds(timeoutSeconds)) + RemoteExecRules.RelayGrace).TotalMilliseconds;
        lock (gate)
        {
            foreach (var stale in entries.Values.Where(e => e.ExpiresAtMs <= now).Select(e => e.Cid).ToList())
                entries.Remove(stale);
            while (entries.Count >= MaxEntries)
                entries.Remove(entries.Values.MinBy(e => e.Sequence)!.Cid);
            entries[cid] = new Entry(cid, originDeviceId, originCommandId, targetDeviceId, expires, ++sequence);
        }
    }

    /// <summary>裝置 hub 的 <c>ExecOutput</c>（被操作的電腦）：見類別說明</summary>
    public async Task<ExecOutputResult> ReceiveOutputAsync(DeviceConnection? connection, ExecChunk? chunk, CancellationToken cancellationToken)
    {
        if (connection is null || !connection.IsProvenSyncing || !connection.Allows(DeviceHubMethods.ExecOutput))
            return new ExecOutputResult(false);
        if (!RemoteExecRules.IsValidChunk(chunk))
        {
            // 自己的桌面送的：程式錯誤，丟掉這一段但不停下指令
            log.LogWarning("電腦 {DeviceId} 送來格式不合格的遠端執行輸出：略過", connection.DeviceId);
            return new ExecOutputResult(true);
        }

        var now = NowMs();
        string? replyConnection;
        Entry entry;
        lock (gate)
        {
            if (!entries.TryGetValue(chunk!.CommandId, out var found) || !string.Equals(found.TargetDeviceId, connection.DeviceId, StringComparison.Ordinal))
                return new ExecOutputResult(false);
            if (found.ExpiresAtMs <= now)
            {
                entries.Remove(found.Cid);
                return new ExecOutputResult(false);
            }

            entry = found;
            replyConnection = ReplyConnection(entry.OriginDeviceId);
            if (replyConnection is null)
            {
                entry.OriginMissingSinceMs ??= now;
                if (chunk.Kind == RemoteExecRules.KindExit || now - entry.OriginMissingSinceMs.Value >= (long)RemoteExecRules.OriginLostLimit.TotalMilliseconds)
                {
                    entries.Remove(entry.Cid);
                    if (chunk.Kind != RemoteExecRules.KindExit)
                    {
                        log.LogInformation("遠端執行指令 {Cid} 的發起電腦 {Origin} 連不上太久：請 {Target} 停止", entry.Cid, entry.OriginDeviceId, entry.TargetDeviceId);
                        return new ExecOutputResult(false);
                    }
                }

                return new ExecOutputResult(true);
            }

            entry.OriginMissingSinceMs = null;
            if (chunk.Kind == RemoteExecRules.KindExit)
                entries.Remove(entry.Cid);
        }

        try
        {
            await sender.SendAsync(replyConnection, DeviceHubMethods.ExecReply, chunk with { CommandId = entry.OriginCommandId }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "遠端執行指令 {Cid} 的輸出送不到發起的電腦 {Origin}", entry.Cid, entry.OriginDeviceId);
        }

        return new ExecOutputResult(true);
    }

    /// <summary>裝置 hub 的 <c>ExecCancel</c>（發起的電腦）：只認它自己下、還在登記的指令</summary>
    public async Task<ExecCancelResult> CancelAsync(DeviceConnection? connection, ExecCancel? request, CancellationToken cancellationToken)
    {
        if (connection is null || !connection.IsProvenSyncing || !connection.Allows(DeviceHubMethods.ExecCancel)
            || request is null || !SyncIds.IsUlid(request.CommandId))
            return new ExecCancelResult(false);

        Entry? entry;
        lock (gate)
        {
            entry = entries.Values.FirstOrDefault(e => string.Equals(e.OriginDeviceId, connection.DeviceId, StringComparison.Ordinal)
                                                       && string.Equals(e.OriginCommandId, request.CommandId, StringComparison.Ordinal));
        }

        if (entry is null)
            return new ExecCancelResult(false);

        var targets = registry.ConnectionsOf(entry.TargetDeviceId).Where(c => c.IsProvenSyncing && c.Allows(DeviceHubMethods.ExecAbort)).ToList();
        foreach (var target in targets)
        {
            try
            {
                await sender.SendAsync(target.ConnectionId, DeviceHubMethods.ExecAbort, new ExecAbort(entry.Cid), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "遠端執行指令 {Cid} 的停止要求送不到 {Target}", entry.Cid, entry.TargetDeviceId);
            }
        }

        return new ExecCancelResult(targets.Count > 0);
    }

    /// <summary>發起的電腦目前收得到 <c>ExecReply</c> 的連線：已證明、協商出 exec1、<c>Order</c> 最大的</summary>
    private string? ReplyConnection(string deviceId) =>
        registry.ConnectionsOf(deviceId).Where(c => c.IsProvenSyncing && c.Allows(DeviceHubMethods.ExecReply))
            .OrderByDescending(c => c.Order).FirstOrDefault()?.ConnectionId;

    private long NowMs() => time.GetUtcNow().ToUnixTimeMilliseconds();
}
