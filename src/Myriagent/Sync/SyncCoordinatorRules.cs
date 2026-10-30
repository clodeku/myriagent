using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>同步協調者的純函式規則（宿主端同步協定：web-protocol 的 sync* 指令、SyncStatus、devices）</summary>
internal static class SyncCoordinatorRules
{
    /// <summary>devices 訊息最多每 500 毫秒一次（web-protocol）</summary>
    public static readonly TimeSpan DevicesThrottle = TimeSpan.FromMilliseconds(500);

    /// <summary>解除配對時等引擎停止的時間（逾時就取消）</summary>
    public static readonly TimeSpan UnpairStopTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Hello 回報的遠端操作政策：暫停遠端操作時一律 Off／false，否則取設定值（不認得的層級當成 Off）。允許遠端執行指令只在層級是 Control 而且開著時是 true，
    /// 其他是 null（線上省略）
    /// </summary>
    public static DevicePolicy HelloPolicy(SyncSettings settings)
    {
        if (settings.RemotePaused)
            return new DevicePolicy(SyncSettings.LevelOff, false);
        var level = SyncSettings.IsLevel(settings.RemoteControlLevel) ? settings.RemoteControlLevel : SyncSettings.LevelOff;
        return new DevicePolicy(level, settings.AllowRemoteFullAccess, level == SyncSettings.LevelControl && settings.AllowRemoteExec ? true : null);
    }

    /// <summary>
    /// syncSetPolicy 的參數：remoteControlLevel（Off／View／Control）、allowRemoteFullAccess（布林），任一可省略（沿用目前的值）；
    /// 有給但不合格（不認得的層級、型別不對）就整個忽略，回 null
    /// </summary>
    public static (string Level, bool AllowFullAccess)? ParsePolicy(JObject command, SyncSettings current)
    {
        var level = current.RemoteControlLevel;
        var allow = current.AllowRemoteFullAccess;
        switch (command["remoteControlLevel"])
        {
            case null:
                break;
            case JValue { Type: JTokenType.String, Value: string text } when SyncSettings.IsLevel(text):
                level = text;
                break;
            default:
                return null;
        }

        switch (command["allowRemoteFullAccess"])
        {
            case null:
                break;
            case JValue { Type: JTokenType.Boolean, Value: bool flag }:
                allow = flag;
                break;
            default:
                return null;
        }

        return (SyncSettings.IsLevel(level) ? level : SyncSettings.LevelOff, allow);
    }

    /// <summary>
    /// syncSetPolicy 的 allowRemoteExec（docs/sync-protocol.md 14.11）：沒給沿用目前的值，布林照收；其他型別（含 JSON 的 null）回 null（整個指令忽略，同 <see cref="ParsePolicy"/>）
    /// </summary>
    public static bool? ParseAllowRemoteExec(JObject command, SyncSettings current) => command["allowRemoteExec"] switch
    {
        null => current.AllowRemoteExec,
        JValue { Type: JTokenType.Boolean, Value: bool flag } => flag,
        _ => null,
    };

    /// <summary>
    /// syncSetPolicy 的 notifyRemoteActions（D8；web-protocol〔遠端〕，WP4.6 起接上）：沒給沿用目前的值，布林照收；
    /// 其他型別（含 JSON 的 null）回 null（同 <see cref="ParsePolicy"/>，整個指令忽略）。另寫一個、不改 ParsePolicy 的 tuple（既有測試照舊）
    /// </summary>
    public static bool? ParseNotifyRemoteActions(JObject command, SyncSettings current) => command["notifyRemoteActions"] switch
    {
        null => current.NotifyRemoteActions,
        JValue { Type: JTokenType.Boolean, Value: bool flag } => flag,
        _ => null,
    };

    /// <summary>syncPair 的 server：使用者自己架設的 Hub 網址（一定要給），要通過 ValidateServerUrl；沒給或不合格回 null</summary>
    public static Uri? PairServer(JToken? server) => server switch
    {
        JValue { Type: JTokenType.String, Value: string text } => DevTrustRules.ValidateServerUrl(text),
        _ => null,
    };

    /// <summary>給人看的伺服器網址（只有 scheme、host、port）：MYRIAGENT_SYNC_SERVER 覆寫時是覆寫的</summary>
    public static string ShownServer(Uri? serverOverride, string pairedServer) =>
        (serverOverride ?? new Uri(pairedServer)).GetLeftPart(UriPartial.Authority);

    /// <summary>已配對、引擎還沒發布狀態之前的快照（connecting；deviceId 與兩個指紋取自 device.json）</summary>
    public static SyncStatusSnapshot Initial(DeviceFileData device, Uri? serverOverride) => new(
        SyncEngineState.Idle,
        SyncStopReason.None,
        serverOverride is null ? null : SyncStatusRules.DevModeNote,
        ShownServer(serverOverride, device.ServerUrl),
        0,
        null,
        null,
        device.DeviceId,
        device.DeviceFingerprint,
        device.HubFingerprint,
        null,
        null,
        serverOverride is not null);

    /// <summary>還沒拿到配對碼時的配對進度（userCode 等是空字串）</summary>
    public static PairingStatus PairingStarting { get; } = new(string.Empty, string.Empty, string.Empty, 0, null);

    /// <summary>配對中的快照（state 一律 pairing；還沒有 deviceId 與伺服器指紋）</summary>
    public static SyncStatusSnapshot Pairing(Uri pairedServer, Uri? serverOverride, PairingStatus pairing, string? deviceFingerprint) => new(
        SyncEngineState.Idle,
        SyncStopReason.None,
        serverOverride is null ? null : SyncStatusRules.DevModeNote,
        ShownServer(serverOverride, pairedServer.AbsoluteUri),
        0,
        null,
        null,
        null,
        deviceFingerprint,
        null,
        pairing,
        null,
        serverOverride is not null);

    /// <summary>devices 要等多久才送：從沒送過或距上次已滿 500 毫秒→null（立刻送），否則等到滿 500 毫秒</summary>
    public static TimeSpan? DevicesDelay(DateTimeOffset? lastSentAt, DateTimeOffset now)
    {
        if (lastSentAt is not { } last || now - last >= DevicesThrottle)
            return null;
        return last + DevicesThrottle - now;
    }
}
