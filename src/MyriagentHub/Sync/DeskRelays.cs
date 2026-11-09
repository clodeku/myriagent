using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>
/// 遠端桌面的轉送（docs/sync-protocol.md 14.16，<c>desk1</c>；2026-11-09）：<c>deskOpen</c> 照一般的遠端指令簽發（<see cref="CommandRouter"/>
/// 送出之前在這裡登記工作階段 → 看的一方〔瀏覽器連線或發起的電腦〕與被看的電腦），之後：
/// <list type="bullet">
/// <item>被看的電腦以 <c>DeskUp</c> 送 <c>frame</c>／<c>status</c>／<c>end</c>，轉給看的一方（瀏覽器的 <c>desk</c> 訊息、或發起電腦的 <c>DeskDown</c>）。</item>
/// <item>看的一方（瀏覽器的 <c>desk</c> 指令、或發起電腦的 <c>DeskUp</c>）送 <c>input</c>／<c>ack</c>／<c>config</c>／<c>close</c>，以 <c>DeskDown</c> 轉給被看的電腦。</item>
/// <item>只收登記的那兩邊送的、方向對的種類；<c>end</c>／<c>close</c> 轉送之後就忘掉。看的瀏覽器斷線、發起的電腦連不上超過
/// <see cref="DeskRules.OriginLostLimit"/>、被看的電腦不在了，替另一邊送結束。<see cref="DeskRules.IdleForget"/> 沒有封包的工作階段忘掉。</item>
/// </list>
/// 不保存（伺服器重啟後全部忘掉：兩邊下一個封包就收到 keep false）；同時最多 <see cref="DeskRules.MaxSessions"/> 個、每台被看的電腦 1 個。任何執行緒都能呼叫（一把鎖）
/// </summary>
internal sealed class DeskRelays(ConnectionRegistry registry, IDeviceMessageSender sender, IDeskBrowserSender gateway, TimeProvider time, ILogger<DeskRelays> log)
{
    public const string BrowserMessageType = "desk";

    private readonly object gate = new();

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    private sealed class Entry(string sessionId, string targetDeviceId, BrowserConnection? browser, string? originDeviceId, long nowMs)
    {
        public string SessionId { get; } = sessionId;

        public string TargetDeviceId { get; } = targetDeviceId;

        /// <summary>看的瀏覽器連線（發起的是瀏覽器時）</summary>
        public BrowserConnection? Browser { get; } = browser;

        /// <summary>看的電腦（發起的是桌面時）</summary>
        public string? OriginDeviceId { get; } = originDeviceId;

        public long LastMs { get; set; } = nowMs;

        public long? OriginMissingSinceMs { get; set; }
    }

    /// <summary>目前的工作階段數</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    /// <summary>
    /// 能不能開新的工作階段（<see cref="CommandRouter"/> 簽發 <c>deskOpen</c> 之前）：id 合格且沒用過、總數與這台被看的電腦都還有名額。
    /// 不能時回給使用者看的原因
    /// </summary>
    public string? CheckOpen(string? sessionId, string targetDeviceId)
    {
        if (!DeskRules.IsSessionId(sessionId))
            return "工作階段 id 不合格";
        lock (gate)
        {
            Prune(NowMs());
            if (entries.ContainsKey(sessionId!))
                return "工作階段 id 重複";
            if (entries.Values.Any(e => string.Equals(e.TargetDeviceId, targetDeviceId, StringComparison.Ordinal)))
                return "已經有人在遠端操控那台電腦";
            if (entries.Count >= DeskRules.MaxSessions)
                return "同時進行的遠端桌面太多";
        }

        return null;
    }

    /// <summary>登記一個已簽發的 <c>deskOpen</c>（送 <c>CommandDelivery</c> 之前）</summary>
    public void Register(string sessionId, string targetDeviceId, CommandOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var entry = origin switch
        {
            BrowserCommandOrigin browser => new Entry(sessionId, targetDeviceId, browser.Connection, null, NowMs()),
            DeviceCommandOrigin device => new Entry(sessionId, targetDeviceId, null, device.Device, NowMs()),
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin.GetType().Name, "未知的發起者"),
        };
        lock (gate)
            entries[sessionId] = entry;
    }

    /// <summary>指令沒有送出或被那台拒絕（<c>commandResult</c> 不是 done）：忘掉工作階段</summary>
    public void Forget(string? sessionId)
    {
        if (sessionId is null)
            return;
        lock (gate)
            entries.Remove(sessionId);
    }

    /// <summary>裝置 hub 的 <c>DeskUp</c>：被看的電腦或看的電腦送來的封包</summary>
    public async Task<DeskUpResult> ReceiveFromDeviceAsync(DeviceConnection? connection, DeskPacket? packet, CancellationToken cancellationToken)
    {
        if (connection is null || !connection.IsProvenSyncing || !connection.Allows(DeviceHubMethods.DeskUp) || packet is null || !DeskRules.IsSessionId(packet.SessionId))
            return new DeskUpResult(false);
        var now = NowMs();
        Entry? entry;
        bool fromTarget;
        lock (gate)
        {
            if (!entries.TryGetValue(packet.SessionId, out entry))
                return new DeskUpResult(false);
            if (string.Equals(entry.TargetDeviceId, connection.DeviceId, StringComparison.Ordinal))
                fromTarget = true;
            else if (entry.OriginDeviceId is { } origin && string.Equals(origin, connection.DeviceId, StringComparison.Ordinal))
                fromTarget = false;
            else
                return new DeskUpResult(false);
            entry.LastMs = now;
        }

        if (DeskRules.Check(packet, fromTarget) is { } problem)
        {
            // 自己的桌面送的：程式錯誤，丟掉這一包但不結束
            log.LogWarning("電腦 {DeviceId} 送來不合格的遠端桌面封包（{Problem}）：略過", connection.DeviceId, problem);
            return new DeskUpResult(true);
        }

        return fromTarget
            ? new DeskUpResult(await ToViewerAsync(entry, packet, now, cancellationToken))
            : new DeskUpResult(await ToTargetAsync(entry, packet, cancellationToken));
    }

    /// <summary>瀏覽器的 <c>desk</c> 指令（看的一方）：只收這條連線開的工作階段。回 false＝工作階段已經不在（網頁結束畫面）</summary>
    public async Task<bool> ReceiveFromBrowserAsync(BrowserConnection connection, DeskPacket packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Entry? entry;
        lock (gate)
        {
            if (!entries.TryGetValue(packet.SessionId, out entry) || !ReferenceEquals(entry.Browser, connection))
                return false;
            entry.LastMs = NowMs();
        }

        if (DeskRules.Check(packet, fromTarget: false) is not null)
            return true;
        return await ToTargetAsync(entry, packet, cancellationToken);
    }

    /// <summary>瀏覽器連線斷了：它在看的工作階段替它送 close 給被看的電腦</summary>
    public async Task BrowserGoneAsync(BrowserConnection connection)
    {
        List<Entry> gone;
        lock (gate)
        {
            gone = entries.Values.Where(e => ReferenceEquals(e.Browser, connection)).ToList();
            foreach (var e in gone)
                entries.Remove(e.SessionId);
        }

        foreach (var e in gone)
            await SendToTargetAsync(e, new DeskPacket(e.SessionId, DeskRules.Close, 0, DeskRules.EndBody("viewerGone", "看的一方離開了")), CancellationToken.None);
    }

    private async Task<bool> ToViewerAsync(Entry entry, DeskPacket packet, long now, CancellationToken cancellationToken)
    {
        if (entry.Browser is { } browser)
        {
            if (packet.Kind == DeskRules.End)
                Forget(entry.SessionId);
            gateway.Send(browser, BrowserMessage(packet));
            return packet.Kind != DeskRules.End;
        }

        var reply = ConnectionOf(entry.OriginDeviceId!);
        if (reply is null)
        {
            bool lost;
            lock (gate)
            {
                entry.OriginMissingSinceMs ??= now;
                lost = packet.Kind == DeskRules.End || now - entry.OriginMissingSinceMs.Value >= (long)DeskRules.OriginLostLimit.TotalMilliseconds;
                if (lost)
                    entries.Remove(entry.SessionId);
            }

            if (lost && packet.Kind != DeskRules.End)
                log.LogInformation("遠端桌面 {Session} 的看的電腦 {Origin} 連不上太久：請 {Target} 停止", entry.SessionId, entry.OriginDeviceId, entry.TargetDeviceId);
            return !lost;
        }

        lock (gate)
        {
            entry.OriginMissingSinceMs = null;
            if (packet.Kind == DeskRules.End)
                entries.Remove(entry.SessionId);
        }

        await SendAsync(reply, packet, cancellationToken);
        return packet.Kind != DeskRules.End;
    }

    private async Task<bool> ToTargetAsync(Entry entry, DeskPacket packet, CancellationToken cancellationToken)
    {
        if (!await SendToTargetAsync(entry, packet, cancellationToken))
        {
            // 被看的電腦不在了：告訴看的一方
            Forget(entry.SessionId);
            var end = new DeskPacket(entry.SessionId, DeskRules.End, 0, DeskRules.EndBody("targetGone", "那台電腦離線了"));
            if (entry.Browser is { } browser)
                gateway.Send(browser, BrowserMessage(end));
            return false;
        }

        if (packet.Kind == DeskRules.Close)
            Forget(entry.SessionId);
        return packet.Kind != DeskRules.Close;
    }

    private async Task<bool> SendToTargetAsync(Entry entry, DeskPacket packet, CancellationToken cancellationToken)
    {
        var target = ConnectionOf(entry.TargetDeviceId);
        if (target is null)
            return false;
        await SendAsync(target, packet, cancellationToken);
        return true;
    }

    private async Task SendAsync(string connectionId, DeskPacket packet, CancellationToken cancellationToken)
    {
        try
        {
            await sender.SendAsync(connectionId, DeviceHubMethods.DeskDown, packet, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "遠端桌面 {Session} 的封包送不出去", packet.SessionId);
        }
    }

    /// <summary>瀏覽器的 <c>desk</c> 訊息</summary>
    internal static ClientOutboxMessage BrowserMessage(DeskPacket packet)
    {
        var json = new JObject
        {
            ["type"] = BrowserMessageType,
            ["sessionId"] = packet.SessionId,
            ["kind"] = packet.Kind,
            ["seq"] = packet.Seq,
            ["body"] = packet.Body ?? new JObject(),
        };
        return new ClientOutboxMessage(BrowserMessageType, json.ToString(Formatting.None));
    }

    /// <summary>那台電腦收得到 <c>DeskDown</c> 的連線：已證明、協商出 desk1、<c>Order</c> 最大的</summary>
    private string? ConnectionOf(string deviceId) =>
        registry.ConnectionsOf(deviceId).Where(c => c.IsProvenSyncing && c.Allows(DeviceHubMethods.DeskDown))
            .OrderByDescending(c => c.Order).FirstOrDefault()?.ConnectionId;

    private void Prune(long now)
    {
        foreach (var stale in entries.Values.Where(e => now - e.LastMs >= (long)DeskRules.IdleForget.TotalMilliseconds).Select(e => e.SessionId).ToList())
            entries.Remove(stale);
    }

    private long NowMs() => time.GetUtcNow().ToUnixTimeMilliseconds();
}

/// <summary>遠端桌面送給看的瀏覽器（預設經 <see cref="BrowserGateway.SendAfterInit"/>；測試換成記錄的）</summary>
internal interface IDeskBrowserSender
{
    void Send(BrowserConnection connection, ClientOutboxMessage message);
}

internal sealed class GatewayDeskBrowserSender(BrowserGateway gateway) : IDeskBrowserSender
{
    public void Send(BrowserConnection connection, ClientOutboxMessage message) => gateway.SendAfterInit(connection, message);
}
