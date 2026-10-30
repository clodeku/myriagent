using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 側欄的對話搜尋（標題與內文；web-protocol 的 searchConversations／conversationSearch）：網頁照舊即時比對標題，另外請宿主搜內文。
// 清單裡的每個對話（同 RenderConversationList 列的那些）在 UI 執行緒擷取來源——載入過的與複本用記憶體裡的內文，
// 其他的給 items.json 的路徑——再到背景執行緒比對（ConversationSearchIndex，讀過的檔依修改時間快取）。
// 同一個視窗的新搜尋取消上一個；回覆只給發出的視窗。只讀，不載入對話（不觸發載入時的收尾與同步）
internal sealed partial class AgentHub
{
    private readonly ConversationSearchIndex searchIndex = new();

    /// <summary>各視窗進行中的搜尋（新的一個取消舊的）</summary>
    private readonly Dictionary<string, CancellationTokenSource> searchRuns = new(StringComparer.Ordinal);

    /// <summary>測試用：搜尋的快取（讀過幾次 items.json）</summary>
    internal ConversationSearchIndex SearchIndex => searchIndex;

    private void SearchConversations(IAgentView view, string? requestId, string? query)
    {
        CancelSearch(view.Id);
        var terms = ConversationSearchRules.Terms(query);
        if (terms.Count == 0)
        {
            PostSearchResult(view, requestId, query, Array.Empty<ConversationSearchIndex.Hit>());
            return;
        }

        var sources = new List<ConversationSearchIndex.Source>();
        foreach (var c in ListedConversations())
        {
            // 複本一律在記憶體（政策不是 full 時沒有項目，只比標題）；本機的對話載入過才用記憶體裡的（比檔案新）
            IReadOnlyList<string>? texts = c.ItemsLoaded
                ? c.Items.Where(i => ConversationSearchRules.IsSearchable(i.Kind) && !string.IsNullOrEmpty(i.Text)).Select(i => i.Text).ToArray()
                : null;
            sources.Add(new ConversationSearchIndex.Source(c.Meta.Id, c.Meta.Title, texts, texts is null && !c.IsReplica ? store.GetItemsPath(c.Meta.Id) : null));
        }

        var cts = new CancellationTokenSource();
        searchRuns[view.Id] = cts;
        _ = RunSearchAsync(view, requestId, query, terms, sources, cts);
    }

    private async Task RunSearchAsync(IAgentView view, string? requestId, string? query, IReadOnlyList<string> terms,
        List<ConversationSearchIndex.Source> sources, CancellationTokenSource cts)
    {
        List<ConversationSearchIndex.Hit> hits;
        try
        {
            hits = await Task.Run(() => searchIndex.Search(sources, terms, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // 搜尋本身出錯（不該發生）：記下來，回空的結果，網頁不會一直停在「搜尋內文中」
            Log.Error("搜尋對話內文失敗", ex);
            hits = new List<ConversationSearchIndex.Hit>();
        }
        finally
        {
            if (searchRuns.TryGetValue(view.Id, out var current) && current == cts)
                searchRuns.Remove(view.Id);
            cts.Dispose();
        }
        // 已被新的搜尋取代（取消晚於比對完成）或視窗已關：不送
        if (disposed || cts.IsCancellationRequested || !views.ContainsKey(view.Id))
            return;
        PostSearchResult(view, requestId, query, hits);
    }

    private static void PostSearchResult(IAgentView view, string? requestId, string? query, IReadOnlyList<ConversationSearchIndex.Hit> hits)
    {
        Post(view, new JObject
        {
            ["type"] = "conversationSearch",
            ["requestId"] = requestId,
            ["query"] = query ?? string.Empty,
            ["hits"] = new JArray(hits.Select(h => new JObject
            {
                ["conversationId"] = h.ConversationId,
                ["snippet"] = h.Snippet,
            })),
        });
    }

    /// <summary>取消這個視窗進行中的搜尋（新的搜尋、視窗關閉）</summary>
    private void CancelSearch(string viewId)
    {
        if (searchRuns.Remove(viewId, out var cts))
            cts.Cancel();
    }
}
