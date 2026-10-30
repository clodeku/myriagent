using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>
/// 瀏覽器閘道的電腦狀態（stat1；web-protocol 的 <c>deviceStats</c>）：看板發布新的一份（<see cref="DeviceStatsBoard.SnapshotPublished"/>，最多每秒一份）
/// 或有連線開始看（<c>monitor {on:true}</c>）時，對已送過 init、開著電腦狀態、還沒看過這一份的連線送 <c>deviceStats</c>（整份取代；
/// 以固定的合併鍵排進送出佇列，排著的只留最新的一則）。每一份只組一次 JSON，各連線共用
/// </summary>
internal sealed partial class BrowserGateway
{
    /// <summary>送出佇列的合併鍵（同一條連線排著的 deviceStats 只留最新的）</summary>
    internal const string DeviceStatsType = "deviceStats";

    private (long Version, ClientOutboxMessage Message)? statsMessage;

    /// <summary>看板發布了新的一份：只喚醒（處理常式在轉送迴圈的執行緒上）</summary>
    public void WakeStats() => Wake();

    /// <summary>monitor 指令：值變了就喚醒（開啟時下一輪立刻送目前的一份）</summary>
    public void SetMonitoring(BrowserConnection connection, bool on)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!connection.SetMonitoring(on))
            return;
        // 主機的負載只在有人看時取樣：轉送迴圈要知道
        stats?.WatchersChanged();
        Wake();
    }

    /// <summary>一輪：對該收的連線排進 deviceStats；溢出要重送 init 時回 true</summary>
    private bool RunStats(IReadOnlyList<BrowserConnection> all, long nowMs)
    {
        if (stats?.Published is not { } snapshot)
            return false;
        var resync = false;
        foreach (var connection in all)
        {
            if (!connection.InitSent || !connection.Monitoring || connection.StatsSeen == snapshot.Version)
                continue;
            var (queued, again) = Enqueue(connection, StatsMessage(snapshot), nowMs);
            resync |= again;
            if (queued)
                connection.MarkStatsSeen(snapshot.Version);
        }
        return resync;
    }

    private ClientOutboxMessage StatsMessage(DeviceStatsSnapshot snapshot)
    {
        if (statsMessage is { } cached && cached.Version == snapshot.Version)
            return cached.Message;
        var message = new JObject
        {
            ["type"] = DeviceStatsType,
            ["devices"] = new JArray(snapshot.Entries.Select(e => DeviceStatsRules.WebView(e.DeviceId, self: false, name: null, e.Seq, e.AgeMs, e.Stats))),
            ["hub"] = snapshot.Hub is { } hub ? DeviceStatsRules.HubWebView(hub, hub.AgeMs) : null,
        };
        var built = new ClientOutboxMessage(DeviceStatsType, message.ToString(Formatting.None), Key: DeviceStatsType);
        statsMessage = (snapshot.Version, built);
        return built;
    }
}
