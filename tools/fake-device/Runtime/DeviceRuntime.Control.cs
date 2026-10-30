using Newtonsoft.Json.Linq;

namespace FakeDevice;

// 控制通道與劇本共用的操作（WP4.9 步 11）：遠端回合的劇本佇列、對話別名的解析、遠端操作政策（WP4.6 的 policy op 搬到這裡，控制通道的 setPolicy 用同一份）
internal sealed partial class DeviceRuntime
{
    /// <summary>網頁或手機送來的訊息（不是 chat 送的）要照哪份劇本回：依序取用（turnGate 保護）</summary>
    private readonly Queue<ScriptedTurn> remoteTurns = new();

    /// <summary>已經被會話取走的遠端回合劇本數（turnGate 保護）</summary>
    private int remoteTurnsTaken;

    /// <summary>
    /// 排一份遠端回合的劇本（任何執行緒）：下一次不是 chat 送出的一輪（網頁、手機的 send，或排隊訊息接著送）照它回；回傳排好之後佇列裡有幾份。
    /// 劇本裡的卡片由網頁回應（chat 才替使用者回應），沒有回應就停在那裡
    /// </summary>
    public int EnqueueRemoteTurn(ScriptedTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        lock (turnGate)
        {
            remoteTurns.Enqueue(turn);
            return remoteTurns.Count;
        }
    }

    /// <summary>遠端回合的劇本：還在排的、已被取走的</summary>
    public (int Queued, int Taken) RemoteTurns
    {
        get
        {
            lock (turnGate)
                return (remoteTurns.Count, remoteTurnsTaken);
        }
    }

    /// <summary>對話的 id：32 個十六進位字元直接當 id，其他的是 chat 用過的別名；中樞的清單裡沒有它丟 <see cref="DeviceRuntimeException"/></summary>
    public Task<string> ResolveConversationIdAsync(string key) => OnUi(() =>
    {
        var id = key.Length == 32 && key.All(Uri.IsHexDigit) ? key.ToLowerInvariant() : conversationAliases.GetValueOrDefault(key);
        if (id is null || Hub.GetConversation(id) is null)
            throw new DeviceRuntimeException($"沒有對話 {key}（別名要先以 chat 或 stream 開過）");
        return id;
    });

    /// <summary>這個別名或 id 已經對到一個對話（<see cref="ResolveConversationIdAsync"/> 找得到）</summary>
    public Task<bool> HasConversationAsync(string key) => OnUi(() =>
    {
        var id = key.Length == 32 && key.All(Uri.IsHexDigit) ? key.ToLowerInvariant() : conversationAliases.GetValueOrDefault(key);
        return id is not null && Hub.GetConversation(id) is not null;
    });

    /// <summary>
    /// 這台的遠端操作政策（WP4.6 步 13 的 policy op；網頁送給中樞的 syncSetPolicy，經協調者的 SetPolicy 存設定、更新引擎的 hello 政策）。
    /// 參數不合格時 SetPolicy 只記一行、整個忽略，所以回讀設定、和要求的不同就丟 <see cref="DeviceRuntimeException"/>；回傳套用後的值
    /// </summary>
    public async Task<(string Level, bool FullAccess, bool Notify)> SetPolicyAsync(string level, bool? fullAccess, bool? notify)
    {
        var args = new JObject { ["remoteControlLevel"] = level };
        if (fullAccess is { } full)
            args["allowRemoteFullAccess"] = full;
        if (notify is { } wantNotify)
            args["notifyRemoteActions"] = wantNotify;
        await CommandAsync("syncSetPolicy", args);
        var applied = await OnUi(() => (Settings.Sync.RemoteControlLevel, Settings.Sync.AllowRemoteFullAccess, Settings.Sync.NotifyRemoteActions));
        if (!string.Equals(applied.RemoteControlLevel, level, StringComparison.Ordinal)
            || (fullAccess is { } f && f != applied.AllowRemoteFullAccess)
            || (notify is { } n && n != applied.NotifyRemoteActions))
            throw new DeviceRuntimeException(
                $"syncSetPolicy 沒有套用（參數不合格：level 只能是 Off、View、Control，fullAccess、notify 要是 true／false）；現在是 {applied.RemoteControlLevel}、fullAccess {applied.AllowRemoteFullAccess}、notify {applied.NotifyRemoteActions}");
        return (applied.RemoteControlLevel, applied.AllowRemoteFullAccess, applied.NotifyRemoteActions);
    }
}
