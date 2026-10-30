using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 多視窗（2026-10-25 使用者要求：像 Chrome 一樣同時開多個視窗，對話分頁可以在視窗之間拖曳搬移）。
// 同一個程序、同一個中樞開多個完整視窗（IWindowView）：對話與代理程序都在中樞，視窗只是顯示，搬移分頁＝換一個視窗顯示，不碰程序與紀錄。
// 每個視窗有自己的分頁版面（主視窗存 settings.Layout、其他視窗存 settings.Windows[].Layout，WindowKey 對應）。
// 拖曳：來源在 dragstart 送 tabDrag（記下來源與輸入框的字，通知其他視窗有分頁在拖），
// 放在另一個視窗上由目標送 tabMoveIn；沒有人接（放在視窗外、對方沒接住）時來源的 tabDragEnd 請宿主看游標在哪裡：另一個視窗就搬過去、視窗外就開新視窗搬進去。
// 搬移一律先把目標加進 ViewIds 再請來源拿掉（草稿在搬移途中不會因為沒有視窗顯示而被丟掉）。
// 沒有接上宿主（AttachWindows；測試與 HubHarness 預設）時不開新視窗、不看游標；視窗之間的 tabMoveIn 照樣處理
internal sealed partial class AgentHub
{
    private IWindowHost? windowHost;

    /// <summary>最近一次拖曳中的分頁（來源的 tabDrag；TabDragTtl 內有效）</summary>
    private TabDragInfo? tabDrag;

    /// <summary>新開的視窗就緒時要搬進去的對話（鍵是 WindowKey；init 的 movedIn）</summary>
    private readonly Dictionary<string, PendingMoveIn> pendingMoveIns = new(StringComparer.Ordinal);

    /// <summary>上次送給宿主的各視窗外框（只在有變的時候送）</summary>
    private Dictionary<string, WindowBorder> sentBorders = new(StringComparer.Ordinal);

    /// <summary>接上多視窗的宿主（AppController）</summary>
    public void AttachWindows(IWindowHost host)
    {
        windowHost = host;
        sentBorders = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 各視窗的外框（WindowBorderRules）：每個檢視看自己開著的對話（ViewIds 有它的；還沒切過去的背景分頁不算），
    /// 和上次送的一樣就不送。系統匣更新時（執行中、等你回應的數量變了、每秒的維護）順便算
    /// </summary>
    private void UpdateWindowBorders()
    {
        if (windowHost == null || disposed)
            return;
        var borders = new Dictionary<string, WindowBorder>(StringComparer.Ordinal);
        foreach (var view in views.Values)
        {
            var open = conversations.Values
                .Where(c => c.ViewIds.Contains(view.Id))
                .Select(c => new WindowBorderConversation(c.Running, AwaitsUser(c), c.VisibleViewIds.Contains(view.Id), accounts.Find(c.Meta.AccountId)?.Color));
            borders[view.Id] = WindowBorderRules.Decide(open);
        }
        if (borders.Count == sentBorders.Count && borders.All(kv => sentBorders.TryGetValue(kv.Key, out var old) && old == kv.Value))
            return;
        sentBorders = borders;
        windowHost.UpdateWindowBorders(borders);
    }

    /// <summary>開著這個對話分頁的 view（點通知時宿主找顯示它的視窗；還沒切過去的背景分頁不算）</summary>
    public IReadOnlyCollection<string> ViewIdsOf(string conversationId)
        => conversations.TryGetValue(conversationId, out var c) ? c.ViewIds.ToList() : Array.Empty<string>();

    /// <summary>這個視窗的版面：主視窗 settings.Layout、其他視窗 settings.Windows[].Layout、彈出視窗沒有</summary>
    private JObject? LayoutFor(IAgentView view)
    {
        if (view.IsPopout)
            return null;
        if (view is IWindowView { IsPrimaryWindow: false } window)
            return settings.FindWindow(window.WindowKey)?.Layout?.DeepClone() as JObject;
        return settings.Layout?.DeepClone() as JObject;
    }

    private void SaveLayout(IAgentView view, JObject layout)
    {
        if (view.IsPopout)
            return;
        if (view is IWindowView { IsPrimaryWindow: false } window)
        {
            // 視窗的設定由宿主開窗時建立；找不到（已經被關掉）就不存
            if (settings.FindWindow(window.WindowKey) is not { } entry)
                return;
            entry.Layout = layout;
        }
        else
        {
            settings.Layout = layout;
        }
        settings.Save();
    }

    /// <summary>init 的 movedIn：這個視窗是為了搬進某個對話而開的，網頁照還原草稿的方式放好分頁、不另開空白的新對話</summary>
    private JArray PendingMoveInIds(IAgentView view)
        => view is IWindowView window && pendingMoveIns.TryGetValue(window.WindowKey, out var pending) && conversations.ContainsKey(pending.ConversationId)
            ? new JArray(pending.ConversationId)
            : new JArray();

    /// <summary>init 送出之後：把等著搬進這個新視窗的對話搬過來</summary>
    private void RunPendingMoveIn(IAgentView view)
    {
        if (view is not IWindowView window || !pendingMoveIns.Remove(window.WindowKey, out var pending))
            return;
        if (conversations.TryGetValue(pending.ConversationId, out var c))
            MoveConversationToView(c, view, paneId: null, pending.Text, pending.SourceViewId);
    }

    /// <summary>視窗相關的指令（web-protocol 第二節的 newWindow、tabDrag、tabDragEnd、tabMoveIn、moveToNewWindow、closeWindow）</summary>
    private void HandleWindowCommand(IAgentView view, string cmd, JObject command)
    {
        // 只有這台的完整視窗能開窗、搬分頁（彈出視窗、遠端與同步的檢視一律不處理）
        if (!WindowRules.IsWindow(view))
            return;
        var conversationId = command.Value<string>("conversationId");
        var c = conversationId != null && conversations.TryGetValue(conversationId, out var found) ? found : null;
        switch (cmd)
        {
            case "newWindow":
                windowHost?.OpenWindow(WindowRules.NewKey(), view.Id, atCursor: false);
                break;
            case "tabDrag":
                if (c == null)
                    return;
                tabDrag = new TabDragInfo(c.Meta.Id, view.Id, WindowRules.CarriedText(command.Value<string>("text")), DateTime.UtcNow);
                BroadcastTabDrag(view, c.Meta.Id);
                break;
            case "tabDragEnd":
                BroadcastTabDrag(view, null);
                if (c != null && command.Value<bool?>("dropped") != true)
                    DropTabOutside(view, c);
                break;
            case "tabMoveIn":
                if (c == null)
                    return;
                var drag = WindowRules.DragApplies(tabDrag, c.Meta.Id, DateTime.UtcNow) ? tabDrag : null;
                MoveConversationToView(c, view, command.Value<string>("paneId"), drag?.Text, drag?.SourceViewId);
                break;
            case "moveToNewWindow":
                if (c != null)
                    MoveToNewWindow(view, c, WindowRules.CarriedText(command.Value<string>("text")), atCursor: false);
                break;
            case "closeWindow":
                if (view is IWindowView { IsPrimaryWindow: false })
                    windowHost?.CloseWindow(view.Id);
                break;
        }
    }

    /// <summary>通知其他完整視窗：有分頁正在拖（conversationId）或拖完了（null），它們的網頁據此接住從別的視窗拖來的分頁</summary>
    private void BroadcastTabDrag(IAgentView source, string? conversationId)
    {
        var message = new JObject { ["type"] = "tabDrag", ["conversationId"] = conversationId };
        foreach (var view in views.Values.ToList())
        {
            if (view.Id != source.Id && WindowRules.IsWindow(view))
                Post(view, message);
        }
    }

    /// <summary>來源放開了分頁而沒有任何網頁接住：請宿主看游標在哪裡（另一個視窗就搬過去、視窗外就開新視窗）</summary>
    private void DropTabOutside(IAgentView source, ConversationState c)
    {
        if (windowHost == null)
            return;
        var text = WindowRules.DragApplies(tabDrag, c.Meta.Id, DateTime.UtcNow) ? tabDrag!.Text : null;
        var target = windowHost.LocateTabDrop(source.Id);
        switch (target.Kind)
        {
            case TabDropKind.None:
                break;
            case TabDropKind.Window:
                if (target.ViewId != null && target.ViewId != source.Id && views.TryGetValue(target.ViewId, out var view) && WindowRules.IsWindow(view))
                    MoveConversationToView(c, view, paneId: null, text, source.Id);
                break;
            case TabDropKind.NewWindow:
                MoveToNewWindow(source, c, text, atCursor: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target.Kind, "不認得的放開位置");
        }
    }

    /// <summary>開新視窗，等它的網頁就緒（RunPendingMoveIn）再把對話搬進去</summary>
    private void MoveToNewWindow(IAgentView source, ConversationState c, string? text, bool atCursor)
    {
        if (windowHost == null)
            return;
        var key = WindowRules.NewKey();
        pendingMoveIns[key] = new PendingMoveIn(c.Meta.Id, source.Id, text);
        if (!windowHost.OpenWindow(key, source.Id, atCursor))
            pendingMoveIns.Remove(key);
    }

    /// <summary>
    /// 把對話搬到 target 視窗顯示：先加進目標（草稿不會在途中被丟掉）、送整份 conversation（輸入框的字以 draft 帶過去），
    /// 再請其他完整視窗與拖曳的來源拿掉這個分頁（conversationMoved）。目標的視窗帶到前面、切到這個分頁
    /// </summary>
    private void MoveConversationToView(ConversationState c, IAgentView target, string? paneId, string? text, string? dragSourceViewId)
    {
        var sources = WindowRules.SourcesToClear(c.ViewIds, id => views.TryGetValue(id, out var v) && WindowRules.IsWindow(v), target.Id, dragSourceViewId);
        var wasOpen = c.ViewIds.Count > 0;
        c.ViewIds.Add(target.Id);
        c.LastActivity = DateTime.Now;
        if (text != null)
            composerDrafts[c.Meta.Id] = text;
        PostConversation(target, c, paneId);
        if (!wasOpen)
            OnConversationOpened(c);
        RequestAntigravityWorkspaceScan(c);
        foreach (var id in sources)
        {
            c.ViewIds.Remove(id);
            c.VisibleViewIds.Remove(id);
            c.HeaderListsSent.Remove(id);
            if (views.TryGetValue(id, out var source))
                Post(source, new JObject { ["type"] = "conversationMoved", ["conversationId"] = c.Meta.Id });
        }
        Post(target, new JObject { ["type"] = "focusConversation", ["conversationId"] = c.Meta.Id });
        windowHost?.ActivateWindow(target.Id);
        Log.Info($"對話 {c.Meta.Id} 搬到另一個視窗（{(sources.Count == 0 ? "沒有來源" : $"來源 {sources.Count} 個")}）");
        MarkListDirty();
        MarkAccountsDirty();
    }
}
