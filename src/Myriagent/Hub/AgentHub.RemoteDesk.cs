using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.RemoteDesk;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 遠端桌面（docs/sync-protocol.md 14.16、計畫 docs/sync-protocol.md 14.15 第 2 階段；2026-11-09 擁有者要求）：
// 被看的一方：deskOpen 照一般的遠端指令過閘門（本機政策要 Control），處理者另外拒絕以系統管理員執行的 app、同時只收一個工作階段，接下就開始擷取
// （DeskSession）並顯示提示列；政策不再是「可以操作」、同步斷了、app 結束、提示列的「中斷」都立刻停止。
// 看的一方：網頁送的 deskOpen 照桌面遠端操作送出（AgentHub.Peer.cs），這裡記下工作階段是哪個視窗開的；網頁的 desk 封包交給同步、伺服器轉來的封包只給那個視窗。
// 中樞預設沒接（deskOpen 回 unsupported、不宣告 desk1），只有 AppController 以 AttachRemoteDesk 接上真的；中樞測試用假的畫面與提示列
internal sealed partial class AgentHub
{
    private IDeskTransport? deskTransport;

    private Func<IDeskScreen>? deskScreenFactory;

    private Func<string, Action, IDeskIndicator?>? deskIndicatorFactory;

    /// <summary>這台被看的工作階段（同時只有一個；UI 執行緒）</summary>
    private DeskSession? deskSession;

    private IDeskScreen? deskScreen;

    private IDeskIndicator? deskIndicator;

    private System.Threading.Timer? deskPolicyTimer;

    /// <summary>
    /// 被看的工作階段給同步執行緒的快照（輸入不排進 UI 執行緒：UI 執行緒忙的時候滑鼠鍵盤會延遲）與當時的政策（UI 執行緒每 2 秒更新）
    /// </summary>
    private volatile DeskSession? deskSessionShared;

    private volatile bool deskAllowedShared;

    /// <summary>這台在看的工作階段 → 開它的視窗（UI 執行緒）</summary>
    private readonly Dictionary<string, string> deskViewers = new(StringComparer.Ordinal);

    /// <summary>
    /// 接上遠端桌面（AppController 在建立同步之前呼叫）：<paramref name="transport"/> 送封包，<paramref name="screen"/> 建立擷取與輸入，
    /// <paramref name="indicator"/> 在 UI 執行緒顯示提示列（參數是給人看的文字與「中斷」的動作）
    /// </summary>
    internal void AttachRemoteDesk(IDeskTransport transport, Func<IDeskScreen> screen, Func<string, Action, IDeskIndicator?> indicator)
    {
        deskTransport = transport ?? throw new ArgumentNullException(nameof(transport));
        deskScreenFactory = screen ?? throw new ArgumentNullException(nameof(screen));
        deskIndicatorFactory = indicator ?? throw new ArgumentNullException(nameof(indicator));
    }

    /// <summary>給同步協調者的轉接（收到的東西排回 UI 執行緒）</summary>
    internal IDeskEndpoint DeskEndpoint => new DeskEndpointAdapter(this);

    /// <summary>這台正在被遠端操控（app 自我更新不在這時自動重啟；UI 執行緒讀）</summary>
    internal bool RemoteDeskActive => deskSession is { Stopped: false };

    /// <summary>開始或結束被遠端操控（UI 執行緒）：螢幕關著時網頁本來不畫，被看的期間要照常畫（ScreenPresence）</summary>
    internal Action? RemoteDeskActiveChanged { get; set; }

    /// <summary>這台看的工作階段數（測試用）</summary>
    internal int DeskViewerCount => deskViewers.Count;

    private void RegisterDeskRemoteHandlers(List<IRemoteCommandHandler> into) => into.Add(new DeskRemoteHandler(this));

    /// <summary>看的一方：deskOpen 交給同步之後記下開它的視窗（AgentHub.Peer.cs）</summary>
    private void NoteDeskOpen(IAgentView view, JObject command)
    {
        if (command.Value<string>("requestId") is { } sessionId && DeskRules.IsSessionId(sessionId))
            deskViewers[sessionId] = view.Id;
    }

    /// <summary>網頁的 <c>desk</c>（看的一方）：只收這個視窗開的工作階段；已經不在的回一則 end</summary>
    private void HandleDeskCommand(IAgentView view, JObject command)
    {
        var sessionId = command.Value<string>("sessionId");
        var packet = new DeskPacket(sessionId ?? string.Empty, command.Value<string>("kind") ?? string.Empty,
            command["seq"] is JValue { Type: JTokenType.Integer } seq ? (long)seq : 0, command["body"] as JObject);
        if (DeskRules.Check(packet, fromTarget: false) is not null)
            return;
        if (!deskViewers.TryGetValue(packet.SessionId, out var viewId) || viewId != view.Id || deskTransport is not { } transport)
        {
            if (packet.Kind != DeskRules.Close)
                Post(view, DeskMessage(new DeskPacket(packet.SessionId, DeskRules.End, 0, DeskRules.EndBody("gone", "遠端桌面已經結束"))));
            return;
        }

        if (packet.Kind == DeskRules.Close)
            deskViewers.Remove(packet.SessionId);
        transport.SendDeskUp(packet);
    }

    /// <summary>伺服器轉來的封包（UI 執行緒）</summary>
    internal void OnDeskDown(DeskPacket packet)
    {
        if (deskSession is { } session && session.SessionId == packet.SessionId)
        {
            if (DeskRules.Check(packet, fromTarget: false) is not null)
                return;
            // 每個看的一方的封包都再看一次政策：改掉「可以操作」就停
            if (!DeskAllowed())
            {
                session.Stop("policy", "那台不再允許遠端操作，遠端桌面已經結束", notifyViewer: true);
                return;
            }

            session.OnPacket(packet);
            return;
        }

        if (deskViewers.TryGetValue(packet.SessionId, out var viewId))
        {
            if (DeskRules.Check(packet, fromTarget: true) is not null)
                return;
            if (packet.Kind == DeskRules.End)
                deskViewers.Remove(packet.SessionId);
            if (views.TryGetValue(viewId, out var view))
                Post(view, DeskMessage(packet));
            else
                deskViewers.Remove(packet.SessionId);
        }
    }

    /// <summary>伺服器不再轉送、或同步斷了（UI 執行緒）</summary>
    internal void OnDeskDropped(string sessionId)
    {
        if (deskSession is { } session && session.SessionId == sessionId)
        {
            session.Stop("dropped", "和伺服器的連線中斷，遠端桌面已經結束", notifyViewer: false);
            return;
        }

        if (deskViewers.Remove(sessionId, out var viewId) && views.TryGetValue(viewId, out var view))
            Post(view, DeskMessage(new DeskPacket(sessionId, DeskRules.End, 0, DeskRules.EndBody("dropped", "和伺服器的連線中斷，遠端桌面已經結束"))));
    }

    /// <summary>同步不能搬封包了：所有工作階段結束（UI 執行緒）</summary>
    internal void OnDeskLive(bool live)
    {
        if (live)
            return;
        deskSession?.Stop("dropped", "和伺服器的連線中斷，遠端桌面已經結束", notifyViewer: false);
        foreach (var sessionId in deskViewers.Keys.ToList())
            OnDeskDropped(sessionId);
    }

    /// <summary>視窗關了：它看的工作階段替它結束</summary>
    private void ForgetDeskView(string viewId)
    {
        foreach (var (sessionId, owner) in deskViewers.ToList())
        {
            if (owner != viewId)
                continue;
            deskViewers.Remove(sessionId);
            deskTransport?.SendDeskUp(new DeskPacket(sessionId, DeskRules.Close, 0, DeskRules.EndBody("viewerGone", "看的一方關掉了視窗")));
        }
    }

    /// <summary>app 結束：停止被看的工作階段、結束在看的</summary>
    private void DisposeRemoteDesk()
    {
        deskSession?.Stop("appExit", "那台的 Clode Myriagent 結束了", notifyViewer: true);
        foreach (var sessionId in deskViewers.Keys.ToList())
            deskTransport?.SendDeskUp(new DeskPacket(sessionId, DeskRules.Close, 0, DeskRules.EndBody("viewerGone", "看的一方結束了")));
        deskViewers.Clear();
        EndDeskTarget();
    }

    /// <summary>這台的遠端操作政策是「可以操作」（暫停中不算）</summary>
    private bool DeskAllowed() => SyncCoordinatorRules.HelloPolicy(settings.Sync).RemoteControlLevel == SyncSettings.LevelControl;

    private static JObject DeskMessage(DeskPacket packet) => new()
    {
        ["type"] = "desk",
        ["sessionId"] = packet.SessionId,
        ["kind"] = packet.Kind,
        ["seq"] = packet.Seq,
        ["body"] = packet.Body ?? new JObject(),
    };

    /// <summary>開始被看（處理者確認過之後；UI 執行緒）</summary>
    private void StartDeskTarget(string sessionId, string viewerLabel)
    {
        var transport = deskTransport!;
        deskScreen = deskScreenFactory!();
        var session = new DeskSession(sessionId, viewerLabel, deskScreen, transport.SendDeskUp,
            (s, reason) => host.BeginInvoke(() => OnDeskTargetEnded(s, reason)));
        deskSession = session;
        deskAllowedShared = true;
        deskSessionShared = session;
        RemoteDeskActiveChanged?.Invoke();
        var who = string.IsNullOrWhiteSpace(viewerLabel) ? "其他電腦" : viewerLabel;
        try
        {
            deskIndicator = deskIndicatorFactory!(who + " 正在遠端操控這台電腦", () => session.Stop("targetClosed", "那台的人中斷了遠端桌面", notifyViewer: true));
        }
        catch (Exception ex)
        {
            Log.Error("遠端桌面的提示列顯示不出來", ex);
        }

        // 政策可能在沒有任何封包的時候被改掉：每 2 秒看一次
        deskPolicyTimer = new System.Threading.Timer(_ => host.BeginInvoke(() =>
        {
            deskAllowedShared = DeskAllowed();
            if (deskSession is { Stopped: false } live && !deskAllowedShared)
                live.Stop("policy", "那台不再允許遠端操作，遠端桌面已經結束", notifyViewer: true);
        }), null, 2000, 2000);
        Log.Info($"遠端桌面 {sessionId} 開始：{who}");
        PostToAll(PeerFileRules.Toast(who + " 開始遠端操控這台電腦（右上角的提示列可以中斷）", "info"));
        session.Start();
    }

    private void OnDeskTargetEnded(DeskSession session, string reason)
    {
        if (!ReferenceEquals(deskSession, session))
            return;
        Log.Info($"遠端桌面 {session.SessionId} 結束：{reason}");
        EndDeskTarget();
        PostToAll(PeerFileRules.Toast("遠端操控已經結束", "info"));
    }

    private void EndDeskTarget()
    {
        deskPolicyTimer?.Dispose();
        deskPolicyTimer = null;
        deskSession = null;
        deskSessionShared = null;
        RemoteDeskActiveChanged?.Invoke();
        try
        {
            deskIndicator?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"遠端桌面的提示列關不掉：{ex.Message}");
        }

        deskIndicator = null;
        deskScreen?.Dispose();
        deskScreen = null;
    }

    /// <summary><c>deskOpen</c> 的處理者（其他指令回 false）</summary>
    private sealed class DeskRemoteHandler(AgentHub hub) : IRemoteCommandHandler
    {
        public bool TryHandle(RemoteViewContext context, IAgentView view, string cmd, JObject command)
        {
            if (cmd != RemoteCommandCatalog.DeskOpen)
                return false;
            if (!context.Authorized)
            {
                context.Reject(RemoteResultCodes.Invalid, "這個指令沒有經過這台電腦的遠端操作檢查", RemoteGateSteps.Handler);
                return true;
            }

            if (hub.deskTransport is null || hub.deskScreenFactory is null)
            {
                context.Reject(RemoteResultCodes.Unsupported, "這台的 Clode Myriagent 不能遠端桌面", RemoteGateSteps.Handler);
                return true;
            }

            if (hub.DeviceFilesElevated)
            {
                context.Reject(RemoteResultCodes.Policy, DeskHubRules.ElevatedMessage, RemoteGateSteps.Handler);
                return true;
            }

            if (command.Value<string>("requestId") is not { } sessionId || !DeskRules.IsSessionId(sessionId))
            {
                context.Reject(RemoteResultCodes.Invalid, "遠端桌面的工作階段 id 不合格", RemoteGateSteps.Handler);
                return true;
            }

            if (hub.deskSession is { Stopped: false })
            {
                context.Reject(RemoteResultCodes.Conflict, "已經有人在遠端操控這台電腦", RemoteGateSteps.Handler);
                return true;
            }

            hub.StartDeskTarget(sessionId, context.IssuerLabel);
            context.Complete();
            return true;
        }
    }

    /// <summary>同步協調者 → 中樞（任何執行緒，排回 UI 執行緒）</summary>
    private sealed class DeskEndpointAdapter(AgentHub hub) : IDeskEndpoint
    {
        public void OnDeskDown(DeskPacket packet)
        {
            // 被看的一方：輸入、確認直接在同步的執行緒交給工作階段（DeskSession 自己加鎖、SendInput 哪個執行緒都可以）
            if (hub.deskSessionShared is { Stopped: false } session && session.SessionId == packet.SessionId && hub.deskAllowedShared
                && packet.Kind != DeskRules.Close && DeskRules.Check(packet, fromTarget: false) is null)
            {
                session.OnPacket(packet);
                return;
            }

            hub.host.BeginInvoke(() => hub.OnDeskDown(packet));
        }

        public void OnDeskDropped(string sessionId) => hub.host.BeginInvoke(() => hub.OnDeskDropped(sessionId));

        public void OnDeskLive(bool live) => hub.host.BeginInvoke(() => hub.OnDeskLive(live));
    }
}

/// <summary>遠端桌面在中樞的文字（純函式）</summary>
internal static class DeskHubRules
{
    public const string ElevatedMessage = "那台的 Clode Myriagent 以系統管理員身分執行，不接受遠端桌面（請以一般權限重新開啟）";
}
