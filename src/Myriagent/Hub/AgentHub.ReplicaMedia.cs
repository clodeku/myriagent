using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Rendering;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// WP4.7（媒體、檔案與上傳）步 12：複本（其他電腦的對話）在這台顯示圖片、影片與附件縮圖（D38）。
// - 有媒體來源（引擎協商出 media1 後經發布者交出的 IReplicaMediaSource）時，複本的項目先以 ReplicaMediaRules.WithMedia 把有參照的附件與代理媒體換成複本路徑，
//   再以 ReplicaMediaUrls 渲染（網址是 https://myriagent.local/m/{鍵}、/t/{鍵}?s=，產生時登記鍵）；沒有來源時照 WP4.3 用 NullMediaUrlBuilder（沒有網址）。
// - AgentWebView 的 /m/、/t/ 只轉送這裡登記過的鍵（ReplicaMediaFor）；關掉顯示、換了接收者時登記清掉。
// - 來源晚到（2026-11-18 使用者回報：桌面載入其他電腦的對話時，圖片影片有時沒顯示）：來源要等同步連線協商出 media1 才有，app 剛啟動、
//   換版重開時，還原的分頁常在那之前就先畫好了（複本的清單與項目是引擎從本機的 sync.db 讀的，不必等連線）。那時畫的項目沒有媒體網址，
//   以前之後也不會補。現在畫的時候沒有來源、項目又帶媒體參照就記在對話上（ReplicaMediaMissed），來源掛上時（ReplicaMediaReady）補送那些項目。
// 中樞不碰同步引擎（S6）：來源只經介面。只在 UI 執行緒
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>這個複本有項目是在還沒有媒體來源時畫出去的（帶媒體參照卻沒有網址）：來源掛上時補送（<see cref="AgentHub.ReplicaMediaReady"/>）</summary>
        public bool ReplicaMediaMissed;
    }

    private ReplicaMediaUrls? replicaMediaUrls;
    private ItemRenderer? replicaMediaRenderer;

    /// <summary>測試接縫：複本的媒體來源（null＝照發布者交出的）</summary>
    internal IReplicaMediaSource? ReplicaMediaSourceForTest { get; set; }

    private ReplicaMediaUrls ReplicaMediaUrls => replicaMediaUrls ??= new ReplicaMediaUrls();

    /// <summary>複本有媒體網址時的渲染器（第一次用時建立）</summary>
    private ItemRenderer ReplicaMediaRenderer => replicaMediaRenderer ??= new ItemRenderer(ReplicaMediaUrls, Log.Error, Log.Warn);

    private IReplicaMediaSource? CurrentReplicaMediaSource()
        => ReplicaMediaSourceForTest ?? (conversationSink as IReplicaMediaSourceProvider)?.ReplicaMediaSource;

    /// <summary>
    /// AgentWebView 的 <c>/m/</c>、<c>/t/</c>（UI 執行緒）：顯示複本中、這個鍵為這個用途登記過、而且有媒體來源才回來源；否則 null（404，來源不呼叫）
    /// </summary>
    internal IReplicaMediaSource? ReplicaMediaFor(string key, string purpose)
    {
        if (!showReplicas || disposed || replicaMediaUrls is not { } urls || !urls.IsAllowed(key, purpose))
            return null;
        return CurrentReplicaMediaSource();
    }

    /// <summary>複本的項目：有媒體來源而且有可用的參照時以複本路徑與 <see cref="ReplicaMediaUrls"/> 渲染，否則照 WP4.3（<see cref="NullMediaUrlBuilder"/>）</summary>
    private JObject RenderReplicaItem(ConversationState c, AgentChatItem item)
    {
        var hasSource = CurrentReplicaMediaSource() is not null;
        // 還沒有來源（同步連線還沒協商好）：先照沒有網址的畫，記下來等來源掛上再補
        if (!hasSource && item.MediaRefs is { Count: > 0 })
            c.ReplicaMediaMissed = true;
        var rendered = hasSource && ReplicaMediaRules.WithMedia(item, c.ReplicaOwnerDeviceId) is { } shown
            ? ReplicaMediaRules.HidePaths(ReplicaMediaRenderer.Render(shown, null))
            : ReplicaRenderer.Render(item, null);
        // 桌面遠端操作（sync-protocol 14.5、14.7）：回應副本的卡片要帶摘要
        PeerHubRules.DecorateReplicaCard(rendered, item);
        return rendered;
    }

    /// <summary>
    /// 複本的媒體來源掛上了（同步連線協商出 media1；UI 執行緒，發布者轉來）：先前沒有來源時畫出去的複本，把帶媒體參照的項目重新渲染、
    /// 補送給開著它的視窗（網頁就地補上圖片與影片；還沒載入的較早項目網頁會略過，之後 loadOlder 拿到的本來就有網址）。
    /// 一次要補超過一頁就整個對話重送（同 <see cref="ApplyReplicaItems"/>）。沒有對話記著就什麼都不做
    /// </summary>
    internal void ReplicaMediaReady()
    {
        if (!showReplicas || disposed || CurrentReplicaMediaSource() is null)
            return;
        foreach (var c in conversations.Values.Where(c => c.IsReplica && c.ReplicaMediaMissed).ToList())
        {
            c.ReplicaMediaMissed = false;
            if (c.ViewIds.Count == 0)
                continue;
            var withMedia = c.Items.Where(i => i.MediaRefs is { Count: > 0 }).ToList();
            if (withMedia.Count == 0)
                continue;
            var targets = ViewsOf(c).ToList();
            if (withMedia.Count > PageSize)
            {
                foreach (var view in targets)
                    PostConversation(view, c, null, reload: true);
                continue;
            }

            foreach (var item in withMedia)
                TryPost(targets, () => new JObject { ["type"] = "item", ["conversationId"] = c.Meta.Id, ["item"] = RenderItemOf(c, item, null) });
        }
    }

    /// <summary>所有複本都移除了（關掉顯示、換了接收者）：登記過的鍵不再轉送</summary>
    private void ForgetReplicaMedia() => replicaMediaUrls?.Clear();
}
