using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Rendering;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// WP4.7（媒體、檔案與上傳）步 12：複本（其他電腦的對話）在這台顯示圖片、影片與附件縮圖（D38）。
// - 有媒體來源（引擎協商出 media1 後經發布者交出的 IReplicaMediaSource）時，複本的項目先以 ReplicaMediaRules.WithMedia 把有參照的附件與代理媒體換成複本路徑，
//   再以 ReplicaMediaUrls 渲染（網址是 https://myriagent.local/m/{鍵}、/t/{鍵}?s=，產生時登記鍵）；沒有來源時照 WP4.3 用 NullMediaUrlBuilder（沒有網址）。
// - AgentWebView 的 /m/、/t/ 只轉送這裡登記過的鍵（ReplicaMediaFor）；關掉顯示、換了接收者時登記清掉。
// 中樞不碰同步引擎（S6）：來源只經介面。只在 UI 執行緒
internal sealed partial class AgentHub
{
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
        var rendered = CurrentReplicaMediaSource() is not null && ReplicaMediaRules.WithMedia(item, c.ReplicaOwnerDeviceId) is { } shown
            ? ReplicaMediaRules.HidePaths(ReplicaMediaRenderer.Render(shown, null))
            : ReplicaRenderer.Render(item, null);
        // 桌面遠端操作（sync-protocol 14.5、14.7）：回應副本的卡片要帶摘要
        PeerHubRules.DecorateReplicaCard(rendered, item);
        return rendered;
    }

    /// <summary>所有複本都移除了（關掉顯示、換了接收者）：登記過的鍵不再轉送</summary>
    private void ForgetReplicaMedia() => replicaMediaUrls?.Clear();
}
