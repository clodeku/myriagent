using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 多電腦同步的掛鉤（WP3.3）：同步協調者（ISyncBridge）與鏡像視窗。
// 中樞只碰同步的介面與純函式規則（S6：未配對時 AgentHub 不能碰到同步的組件）；沒有同步橋時一切照舊
internal sealed partial class AgentHub
{
    /// <summary>同步鏡像的 view id（SyncMirrorView.ViewId 取這個值；常數定義在中樞這邊，中樞不參考鏡像的型別）</summary>
    internal const string SyncViewId = "sync-mirror";

    private ISyncBridge? sync;

    /// <summary>掛上同步協調者（AppController 建好中樞與帳戶之後；未配對時也掛，配對指令要經它）</summary>
    internal void AttachSync(ISyncBridge bridge) => sync = bridge;

    /// <summary>
    /// 掛上同步鏡像：之後和一般視窗一樣收到 accounts、cli 等訊息；掛上時只對它立刻送一份 accounts 與 cli（鏡像不送 ready、拿不到 init）
    /// </summary>
    internal void AttachSyncView(IAgentView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        AttachView(view);
        TryPost([view], () => new JObject { ["type"] = "accounts", ["accounts"] = RenderAccounts() });
        TryPost([view], () => new JObject { ["type"] = "cli", ["cli"] = RenderCli(), ["devicesCli"] = sync?.DevicesCli() });
    }

    /// <summary>解除同步鏡像（解除配對時）</summary>
    internal void DetachSyncView() => DetachView(SyncViewId);

    /// <summary>
    /// 網頁的 setting 指令：照舊套用，再把改變的漫遊鍵（PrefsRules 的四個）交給同步（fontSize 一律 JSON 整數）。
    /// 伺服器來的值走 <see cref="ApplyRoamingSetting"/>，不經這裡，所以不會回送
    /// </summary>
    private void ChangeSettingWithSync(string? key, JToken? value)
    {
        if (sync is not { } bridge)
        {
            ChangeSetting(key, value);
            return;
        }

        var before = PrefsSyncRules.Snapshot(settings);
        ChangeSetting(key, value);
        foreach (var (changedKey, changedValue) in PrefsSyncRules.Changed(before, PrefsSyncRules.Snapshot(settings)))
            bridge.OnRoamingSettingChanged(changedKey, changedValue);
    }

    /// <summary>套用伺服器的漫遊偏好（不經 HandleCommand，不回送）；theme 一樣經宿主換標題列顏色</summary>
    internal void ApplyRoamingSetting(string key, JToken value) => ChangeSetting(key, value);

    /// <summary>sync 訊息（null＝解除配對、取消配對）</summary>
    internal void PostSync(JObject? status) => PostToAll(new JObject { ["type"] = "sync", ["sync"] = status });

    internal void PostDevices(JArray devices) => PostToAll(new JObject { ["type"] = "devices", ["devices"] = devices });

    /// <summary>目錄換了：帳戶卡（〔同步〕欄位）與 cli（devicesCli）重送一次（照既有的節流）；開著的副本重送標頭</summary>
    internal void RefreshSyncDecorations()
    {
        MarkAccountsDirty();
        cliDirty = true;
        // 目錄的連結變了時，tokens 裡哪些帳戶加上其他電腦的計數也跟著變（TokenViewRules）
        tokensViewDirty = true;
        // 副本標頭的 replica.accountId 是以目錄的連結換出來的（目錄比副本晚到、那台的帳戶重新連結時才對得上）
        foreach (var c in conversations.Values)
        {
            if (c.IsReplica && c.ViewIds.Count > 0)
                dirtyHeaders.Add(c.Meta.Id);
        }
        ScheduleFlush();
    }

    /// <summary>同步指令的錯誤提示（view＝送指令的那個視窗；null＝全部）</summary>
    internal void SyncToast(string text, IAgentView? view = null) => Toast(text, isError: true, view);

    /// <summary>系統匣提示文字的同步段（TraySyncRules.TrayText；未配對 null），附加在執行中、等你回應之後</summary>
    internal string? SyncTrayText { get; private set; }

    /// <summary>同步狀態改變（AppController 收到協調者的 StatusChanged）：文字變了才重設系統匣提示</summary>
    internal void SetSyncTrayText(string? text)
    {
        if (disposed || text == SyncTrayText)
            return;
        SyncTrayText = text;
        UpdateTray();
    }
}
