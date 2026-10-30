using System.Threading.Channels;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Realtime;
using MyriagentHub.Trust;

namespace MyriagentHub.Sync;

/// <summary>為什麼要看一台電腦的 durable 列（D27）</summary>
internal enum DurableReason
{
    /// <summary>一條協商出 cmd1 的連線完成 Prove：送 pending 的，以及送在別條連線上的 delivered（重簽換成這條連線）</summary>
    Proven,

    /// <summary>router 寫入 pending 列、提交之後那台已有可以送的連線（補「掃描先跑完、交易後提交」的競態）</summary>
    Kick,

    /// <summary>每 60 秒重掃（維護迴圈）：只送 pending 的（不看這條連線的重送次數）</summary>
    Rescan,

    /// <summary>電腦回 busy／expired 之後 5 秒：同一條連線、同一個 cid 再送一次</summary>
    Retry,

    /// <summary>
    /// router 直接送出 durable 列之後再看一次（合併前審查 P4R-STATE-01）：只送送在別條連線上（或伺服器不記得送在哪）的 delivered 列。
    /// 補「那台的新連線在列提交之前就做完 Prove 的掃描、router 卻把列送到舊連線」的競態
    /// </summary>
    Recheck,
}

/// <summary>排進背景迴圈的一件工作：那台電腦、指定的連線（只有 <see cref="DurableReason.Retry"/> 一定要那條）與 cid（只有重送有）</summary>
internal sealed record DurableWork(string DeviceId, string? ConnectionId, DurableReason Reason, string? CommandId = null);

/// <summary>
/// durable 指令（所有電腦登出）的重簽與補送（WP4.5 計畫 D26、D27；sync-protocol 14.4）：背景迴圈＋<see cref="Channel{T}"/>，一次處理一件。
/// <list type="bullet">
/// <item>觸發：<see cref="OnProven"/>（<c>ConnectionProven</c> 的處理者，只寫進 channel、不同步送：ProveResponse 還沒到裝置）、<see cref="Kick"/>（router 提交 pending 列之後）、
/// <see cref="Recheck"/>（router 直接送出 delivered 列之後）、<see cref="Rescan"/>（每 60 秒）、<see cref="ScheduleRetry"/>＋<see cref="RunDue"/>（busy／expired 之後 5 秒）。</item>
/// <item>每件工作：選那台的連線（<see cref="CommandRouteRules.ChooseConnection"/>，中斷身分公鑰不是目前的；重送只用原本那條）、以唯讀連線讀還沒結束的 durable 列、
/// 已過 7 天的標成 expired；其他照原因挑出來，以 <see cref="DurableBodyRules.Deserialize"/> 換成這條連線的 <c>conn</c>、新的 <c>iat</c>／<c>exp</c>
/// （<see cref="CommandStateRules.ResignExp"/>；<c>created</c> 與內容摘要不變）重簽、核對 <see cref="CommandEnvelope.ContentDigest"/> 等於 <c>content_sha256</c>、
/// 以那條連線 Hello 時的公鑰自驗，在一個寫入交易裡標成 delivered，提交之後才送。</item>
/// <item>記憶體狀態（一把鎖；伺服器重啟後是空的，啟動掃描已把 delivered 改回 pending）：每個 cid 送在哪條連線、每條連線每個 cid 的重送次數
/// （最多 <see cref="CommandStateRules.MaxBusyRetries"/> 次，之後等下一次 Prove 或 60 秒的重掃）、排定的重送。連線移除時清掉那條連線的。</item>
/// </list>
/// 送出失敗只記錄（列已是 delivered：電腦重連、下次 Prove 時重簽再送）。記錄只寫 cid 與裝置，不寫指令內容
/// </summary>
internal sealed class DurableCommands(
    SyncWriter writer,
    CommandStore store,
    ConnectionRegistry registry,
    HubIdentity identity,
    IDeviceMessageSender sender,
    CommandCounters counters,
    TimeProvider time,
    ILogger<DurableCommands> log) : BackgroundService
{
    private readonly Channel<DurableWork> queue = Channel.CreateUnbounded<DurableWork>(new UnboundedChannelOptions { SingleReader = true });

    private readonly object gate = new();

    /// <summary>cid → 送在哪條裝置連線（SignalR 的連線 id）</summary>
    private readonly Dictionary<string, string> deliveredOn = new(StringComparer.Ordinal);

    /// <summary>（連線, cid）→ 收到 busy／expired 的次數</summary>
    private readonly Dictionary<(string ConnectionId, string CommandId), int> retries = new();

    /// <summary>cid → 排定的重送</summary>
    private readonly Dictionary<string, ScheduledRetry> scheduled = new(StringComparer.Ordinal);

    /// <summary>排進 channel、還沒做完的工作數（測試等它歸零）</summary>
    private int outstanding;

    /// <summary><c>ConnectionProven</c> 的處理者（啟動工作訂閱，D28）：協商出 cmd1 的連線才排；只寫進 channel（處理者要很快、不可阻塞）</summary>
    public void OnProven(DeviceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Allows(DeviceHubMethods.CommandDelivery))
            Enqueue(new DurableWork(connection.DeviceId, connection.ConnectionId, DurableReason.Proven));
    }

    /// <summary><c>ConnectionRemoved</c> 的處理者：清掉那條連線的重送次數、排定的重送與送出紀錄</summary>
    public void OnRemoved(DeviceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var id = connection.ConnectionId;
        lock (gate)
        {
            foreach (var key in retries.Keys.Where(k => k.ConnectionId == id).ToList())
                retries.Remove(key);
            foreach (var commandId in scheduled.Where(p => p.Value.ConnectionId == id).Select(p => p.Key).ToList())
                scheduled.Remove(commandId);
            foreach (var commandId in deliveredOn.Where(p => p.Value == id).Select(p => p.Key).ToList())
                deliveredOn.Remove(commandId);
        }
    }

    /// <summary>router 提交 pending 列之後（D27）：那台現在有已證明、協商出 cmd1 的連線才排（沒有的話等它 Prove）</summary>
    public void Kick(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        if (HasDeliverableConnection(deviceId))
            Enqueue(new DurableWork(deviceId, null, DurableReason.Kick));
    }

    /// <summary>router 直接送出的 durable 列（D26）：記下送在哪條連線（同一條連線之後的 Prove 不重送）</summary>
    public void MarkSent(string commandId, string connectionId)
    {
        lock (gate)
            deliveredOn[commandId] = connectionId;
    }

    /// <summary>
    /// router 在 <see cref="MarkSent"/> 之後呼叫（合併前審查 P4R-STATE-01）：那台現在有已證明、協商出 cmd1 的連線才排。背景迴圈只有一個讀者，這件工作一定排在
    /// 先跑完的 Prove 工作之後：最好的連線就是 router 送的那條時什麼都不送；有更新的連線（它的 Prove 掃描在列提交之前就跑完、什麼都沒讀到）時，
    /// 以同一個 cid 換成那條連線重簽送出（電腦以 cid＋內容摘要去重）
    /// </summary>
    public void Recheck(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        if (HasDeliverableConnection(deviceId))
            Enqueue(new DurableWork(deviceId, null, DurableReason.Recheck));
    }

    /// <summary>列到了最終狀態（收到結果、到期）：清掉它的記憶體狀態</summary>
    public void Completed(string commandId)
    {
        lock (gate)
        {
            deliveredOn.Remove(commandId);
            scheduled.Remove(commandId);
            foreach (var key in retries.Keys.Where(k => k.CommandId == commandId).ToList())
                retries.Remove(key);
        }
    }

    /// <summary>
    /// 電腦以 busy／expired 拒絕了 durable 列（D18、D27；列已回到 pending）：同一條連線第 1～<see cref="CommandStateRules.MaxBusyRetries"/> 次時排在
    /// <see cref="CommandStateRules.BusyRetryDelayMs"/> 之後重送，超過就不排（等下一次 Prove，或每 60 秒的重掃再送一次）。回傳有沒有排
    /// </summary>
    public bool ScheduleRetry(string deviceId, string connectionId, string commandId)
    {
        var now = Now();
        lock (gate)
        {
            deliveredOn.Remove(commandId);
            var key = (connectionId, commandId);
            var count = retries.GetValueOrDefault(key) + 1;
            retries[key] = count;
            if (count > CommandStateRules.MaxBusyRetries)
            {
                scheduled.Remove(commandId);
                return false;
            }
            scheduled[commandId] = new ScheduledRetry(deviceId, connectionId, now + CommandStateRules.BusyRetryDelayMs);
            return true;
        }
    }

    /// <summary>到期的重送排進 channel（維護迴圈每輪呼叫，<paramref name="now"/> 是 <see cref="TimeProvider"/> 的時間）；回傳排了幾件</summary>
    public int RunDue(long now)
    {
        List<(string CommandId, ScheduledRetry Retry)> due;
        lock (gate)
        {
            due = scheduled.Where(p => p.Value.DueAt <= now).Select(p => (p.Key, p.Value)).ToList();
            foreach (var (commandId, _) in due)
                scheduled.Remove(commandId);
        }
        foreach (var (commandId, retry) in due)
            Enqueue(new DurableWork(retry.DeviceId, retry.ConnectionId, DurableReason.Retry, commandId));
        return due.Count;
    }

    /// <summary>
    /// 重掃（D27、D30；維護迴圈每 60 秒）：pending 的 durable 列數記進 <c>pendingDurable</c>，有 pending 列、現在有可以送的連線的電腦各排一件；回傳排了幾件
    /// </summary>
    public int Rescan()
    {
        IReadOnlyList<(string DeviceId, long Count)> pending;
        using (var read = writer.OpenRead())
            pending = store.PendingDurable(read);
        counters.SetPendingDurable(pending.Sum(p => p.Count));
        var queued = 0;
        foreach (var (deviceId, _) in pending)
        {
            if (!HasDeliverableConnection(deviceId))
                continue;
            Enqueue(new DurableWork(deviceId, null, DurableReason.Rescan));
            queued++;
        }
        return queued;
    }

    /// <summary>等 channel 裡的工作都做完（測試用）</summary>
    internal async Task WaitIdleAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (Volatile.Read(ref outstanding) > 0)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("durable 指令的背景工作沒有在時限內做完");
            await Task.Delay(10);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessAsync(work, stoppingToken);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && stoppingToken.IsCancellationRequested))
                {
                    // 一件工作的最外層：失敗的列維持原狀（pending／delivered），下一次 Prove 或重掃再試
                    log.LogError(ex, "durable 遠端指令的重簽送出失敗：{DeviceId}（{Reason}）", work.DeviceId, work.Reason);
                }
                finally
                {
                    Interlocked.Decrement(ref outstanding);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>一件工作（見類別說明）</summary>
    internal async Task ProcessAsync(DurableWork work, CancellationToken cancellationToken)
    {
        if (ChooseConnection(work) is not { } connection)
            return;
        var now = Now();
        IReadOnlyList<DurableRow> rows;
        using (var read = writer.OpenRead())
            rows = store.OpenDurable(read, work.DeviceId);

        var expired = new List<string>();
        var candidates = new List<DurableRow>();
        lock (gate)
        {
            foreach (var row in rows)
            {
                if (CommandStateRules.OnDurableExpiry(row.State, durable: true, row.ExpiresAt, now) is not null)
                    expired.Add(row.CommandId);
                else if (Selects(work, row, connection.ConnectionId))
                    candidates.Add(row);
            }
        }

        var signed = new List<(string CommandId, CommandEnvelope Envelope)>();
        foreach (var row in candidates)
        {
            var (envelope, abort) = Resign(row, work.DeviceId, connection, now);
            if (abort)
            {
                // 簽章與檢查之間伺服器身分金鑰輪換了：中斷這條連線，電腦重連（依輪換聲明改釘）、下次 Prove 再送
                Abort(connection, "重簽與檢查之間伺服器身分金鑰輪換了");
                return;
            }
            if (envelope is not null)
                signed.Add((row.CommandId, envelope));
        }
        if (expired.Count == 0 && signed.Count == 0)
            return;

        var (expiredNow, delivered) = await writer.WriteAsync(context =>
        {
            var e = store.ExpireDurable(context, expired);
            var d = new List<(string CommandId, CommandEnvelope Envelope)>(signed.Count);
            foreach (var item in signed)
            {
                if (store.MarkDelivered(context, item.CommandId))
                    d.Add(item);
            }
            return (e, d);
        }, cancellationToken);

        foreach (var commandId in expiredNow)
        {
            counters.Expired();
            Completed(commandId);
        }
        if (expiredNow.Count > 0)
            log.LogInformation("電腦 {DeviceId} 有 {Count} 個 durable 遠端指令超過 7 天，標成 expired", work.DeviceId, expiredNow.Count);
        lock (gate)
        {
            foreach (var (commandId, _) in delivered)
            {
                deliveredOn[commandId] = connection.ConnectionId;
                scheduled.Remove(commandId);
            }
        }
        foreach (var (commandId, envelope) in delivered)
        {
            counters.Resigned();
            await SendAsync(connection, new CommandDelivery(commandId, envelope));
        }
        if (delivered.Count > 0)
            log.LogInformation("重簽並送出 {Count} 個 durable 遠端指令給電腦 {DeviceId}（{Reason}，連線 {Connection}）", delivered.Count, work.DeviceId, work.Reason, connection.ConnectionId);
    }

    /// <summary>這件工作要送哪些列（鎖裡呼叫）</summary>
    private bool Selects(DurableWork work, DurableRow row, string connectionId) => work.Reason switch
    {
        // 新的連線：pending 的，以及送在別條連線上（或伺服器不記得送在哪）的 delivered
        DurableReason.Proven => row.State == CommandStates.Pending
            || (row.State == CommandStates.Delivered && !(deliveredOn.TryGetValue(row.CommandId, out var on) && on == connectionId)),
        // 只送 pending：排著重送的、這條連線已經重送滿的不送（等下一次 Prove 或 60 秒的重掃）
        DurableReason.Kick => row.State == CommandStates.Pending && !scheduled.ContainsKey(row.CommandId)
            && retries.GetValueOrDefault((connectionId, row.CommandId)) <= CommandStateRules.MaxBusyRetries,
        // 60 秒的重掃不看重送次數（WP4.8 合併前審查 RS-1）：電腦回 busy 的原因可能持續很久（這個帳戶正在那台登入、那台的 CLI 正在更新），
        // 一直連著的電腦不會再 Prove；之後的 busy 也不再排 5 秒的重送（ScheduleRetry 超過次數回 false），所以是每 60 秒送一次，直到完成或 7 天到期
        DurableReason.Rescan => row.State == CommandStates.Pending && !scheduled.ContainsKey(row.CommandId),
        DurableReason.Retry => row.State == CommandStates.Pending && string.Equals(row.CommandId, work.CommandId, StringComparison.Ordinal),
        // 只送送在別條連線上（或伺服器不記得送在哪）的 delivered；pending 的照舊由 Kick、Rescan、Retry、Prove 送（不繞過 busy 的延遲與次數上限）
        DurableReason.Recheck => row.State == CommandStates.Delivered
            && !(deliveredOn.TryGetValue(row.CommandId, out var sentOn) && sentOn == connectionId),
        _ => throw new ArgumentOutOfRangeException(nameof(work), work.Reason, "未知的原因"),
    };

    /// <summary>
    /// 選連線：重送只用原本那條（還在、已證明、有 cmd1、能簽）；其他照 <see cref="CommandRouteRules.ChooseConnection"/>（D7），中斷協商出 cmd1 但身分公鑰不是目前的連線
    /// </summary>
    private DeviceConnection? ChooseConnection(DurableWork work)
    {
        var spki = identity.Spki;
        if (work.Reason == DurableReason.Retry)
        {
            if (work.ConnectionId is not { } id || registry.Get(id) is not { } original || !string.Equals(original.DeviceId, work.DeviceId, StringComparison.Ordinal)
                || !original.IsProven || !original.Allows(DeviceHubMethods.CommandDelivery))
                return null;
            if (original.CanSignEnvelopes(spki))
                return original;
            Abort(original, "Hello 時的伺服器身分公鑰不是目前的");
            return null;
        }
        var candidates = registry.ConnectionsOf(work.DeviceId);
        var choice = CommandRouteRules.ChooseConnection(candidates
            .Select(c => new ConnectionFact(c.ConnectionId, c.Order, c.IsProven, c.Allows(DeviceHubMethods.CommandDelivery), c.CanSignEnvelopes(spki)))
            .ToList());
        foreach (var abort in choice.Abort)
            Abort(candidates.First(c => c.ConnectionId == abort), "Hello 時的伺服器身分公鑰不是目前的");
        return choice.ConnectionId is { } chosen ? candidates.First(c => c.ConnectionId == chosen) : null;
    }

    /// <summary>
    /// 重簽一列（D13、D14、D27）：換成這條連線的 conn、新的 iat／exp。格式不對、和列不一致、簽不出、自驗失敗（簽章以外的步）都記錯誤、不送；
    /// 自驗失敗在簽章這一步回 abort
    /// </summary>
    private (CommandEnvelope? Envelope, bool Abort) Resign(DurableRow row, string deviceId, DeviceConnection connection, long now)
    {
        if (row.UnsignedBody is null)
        {
            log.LogError("durable 遠端指令 {CommandId} 沒有 unsigned_body，不送", row.CommandId);
            return (null, false);
        }
        CommandEnvelopeBody body;
        try
        {
            body = DurableBodyRules.Deserialize(row.UnsignedBody, connection.EnvelopeConn!, now, CommandStateRules.ResignExp(row.CreatedAt, now));
        }
        catch (FormatException ex)
        {
            log.LogError("durable 遠端指令 {CommandId} 的 unsigned_body 格式不對，不送：{Reason}", row.CommandId, ex.Message);
            return (null, false);
        }
        if (!string.Equals(body.Cid, row.CommandId, StringComparison.Ordinal) || !string.Equals(body.Dev, deviceId, StringComparison.Ordinal) || !body.Durable
            || body.Created != row.CreatedAt || !string.Equals(CommandEnvelope.ContentDigest(body), row.ContentSha256, StringComparison.Ordinal))
        {
            log.LogError("durable 遠端指令 {CommandId} 的 unsigned_body 和列不一致（cid、dev、durable、created 或內容摘要），不送", row.CommandId);
            return (null, false);
        }
        if (!CommandEnvelope.TrySign(body, identity.Sign, out var envelope))
        {
            log.LogError("durable 遠端指令 {CommandId} 的信封簽不出來", row.CommandId);
            return (null, false);
        }
        var verdict = CommandEnvelope.Verify(envelope, row.CommandId, new CommandEnvelopeTarget(connection.HelloHubSpki!, body.Aud, deviceId, connection.EnvelopeConn!, connection.Allows(DeviceHubMethods.PeerReply)),
            new ServerTimeBounds(now, now));
        if (verdict.Ok)
            return (envelope, false);
        if (verdict.Step == CommandEnvelopeSteps.Signature)
            return (null, true);
        log.LogError("durable 遠端指令 {CommandId} 重簽後自驗失敗（程式錯誤）：{Step} {Detail}", row.CommandId, verdict.Step, verdict.Detail);
        return (null, false);
    }

    private async Task SendAsync(DeviceConnection connection, CommandDelivery delivery)
    {
        try
        {
            await sender.SendAsync(connection.ConnectionId, DeviceHubMethods.CommandDelivery, delivery, CancellationToken.None);
            counters.Delivered();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "durable 遠端指令 {CommandId} 送不到電腦的連線 {Connection}（下次 Prove 重簽再送）", delivery.CommandId, connection.ConnectionId);
        }
    }

    private void Abort(DeviceConnection connection, string reason)
    {
        log.LogInformation("中斷裝置連線 {Connection}（{DeviceId}）：{Reason}", connection.ConnectionId, connection.DeviceId, reason);
        counters.Aborted();
        try
        {
            connection.Abort();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "中斷裝置連線失敗：{Connection}", connection.ConnectionId);
        }
    }

    private bool HasDeliverableConnection(string deviceId) =>
        registry.ConnectionsOf(deviceId).Any(c => c.IsProven && c.Allows(DeviceHubMethods.CommandDelivery));

    private void Enqueue(DurableWork work)
    {
        Interlocked.Increment(ref outstanding);
        if (!queue.Writer.TryWrite(work))
            Interlocked.Decrement(ref outstanding);
    }

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>排定的重送：那台、原本那條連線、什麼時候（<see cref="TimeProvider"/> 的毫秒）</summary>
    private sealed record ScheduledRetry(string DeviceId, string ConnectionId, long DueAt);
}
