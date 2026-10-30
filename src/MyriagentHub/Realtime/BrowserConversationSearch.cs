using System.Collections.Concurrent;
using Myriagent.Hub;
using MyriagentHub.Rendering;
using MyriagentHub.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>
/// 瀏覽器版側欄的對話搜尋（標題與內容；web-protocol 的 <c>searchConversations</c>／<c>conversationSearch</c>，規則同桌面的
/// <see cref="ConversationSearchRules"/>）。範圍是瀏覽器的對話清單（<see cref="ConversationReader.List"/>：沒刪除的、最多
/// <see cref="ConversationViewRules.MaxListItems"/> 個）；內容是各對話沒移除的使用者訊息、代理回覆與計畫確認的原文
/// （<see cref="HubItemRenderer.TryReadSearchText"/>：blob 換回、第二道 DLP，讀不到的不算），依對話的 <c>last_seq</c> 快取在記憶體——
/// 項目新增、修改、移除都會讓它變大，沒變就不再讀。
/// <para>
/// hub 的指令處理只登記（O(1)，同 D38）：比對在執行緒集區，全部的搜尋共用一個閘門依序執行（同時讀 sync.db 的只有一個）；
/// 同一條連線的新搜尋取消上一個（被取消的不回）。結果以 <c>conversationSearch</c> 只回給發起的連線（已送過 init 才入列，合併鍵讓排著的只留最新的）。
/// 只讀：不寫資料庫、不稽核；記錄只寫例外，不寫查詢與內容
/// </para>
/// </summary>
internal sealed class BrowserConversationSearch(BrowserGateway gateway, ConversationReader reader, IBlobStore blobs, ILogger<BrowserConversationSearch> log)
    : IDisposable
{
    /// <summary>回覆的 type（也是送出佇列的合併鍵）</summary>
    public const string ResultType = "conversationSearch";

    /// <summary>requestId 最多幾個字（網頁送 <c>cs</c>＋流水號）</summary>
    public const int MaxRequestIdLength = 64;

    private sealed record Cached(long Stamp, string[] Texts);

    private readonly ConcurrentDictionary<string, Cached> cache = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, CancellationTokenSource> runs = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim gate = new(1, 1);

    private int conversationReads;

    private int disposed;

    /// <summary>測試用：讀過幾次對話的項目（快取沒命中的對話數）</summary>
    internal int ConversationReads => Volatile.Read(ref conversationReads);

    /// <summary>測試用：快取裡有幾個對話</summary>
    internal int CachedCount => cache.Count;

    /// <summary>測試用：最近一次搜尋的工作（等它結束）</summary>
    internal Task LastRun { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// 開始一次搜尋（hub 的指令處理；同一條連線的上一次取消）。沒有關鍵字時立刻回空的結果
    /// </summary>
    public void Start(BrowserConnection connection, string requestId, string query)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(requestId);
        ArgumentNullException.ThrowIfNull(query);
        if (runs.TryRemove(connection.ConnectionId, out var previous))
            Cancel(previous);
        var terms = ConversationSearchRules.Terms(query);
        if (terms.Count == 0)
        {
            gateway.SendAfterInit(connection, Message(requestId, query, []));
            return;
        }
        var cts = new CancellationTokenSource();
        runs[connection.ConnectionId] = cts;
        LastRun = Task.Run(() => RunAsync(connection, requestId, query, terms, cts));
    }

    private async Task RunAsync(BrowserConnection connection, string requestId, string query, IReadOnlyList<string> terms, CancellationTokenSource cts)
    {
        List<(string ConversationId, string? Snippet)> hits;
        try
        {
            await gate.WaitAsync(cts.Token);
            try
            {
                hits = Search(terms, cts.Token);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // 不該發生（讀不到的項目只是不算）：記下來、回空的結果，網頁不會一直停在「搜尋內容中」
            log.LogError(ex, "瀏覽器的對話搜尋失敗（連線 {Connection}）", connection.ConnectionId);
            hits = [];
        }
        finally
        {
            runs.TryRemove(new KeyValuePair<string, CancellationTokenSource>(connection.ConnectionId, cts));
            cts.Dispose();
        }
        // 已被同一條連線的新搜尋取代（取消晚於比對完成）
        if (cts.IsCancellationRequested || Volatile.Read(ref disposed) != 0)
            return;
        // 協作人只找得到分享給他的對話（多人協作）
        if (connection.IsCollaborator)
            hits = hits.Where(h => gateway.CanSeeConversation(connection, h.ConversationId)).ToList();
        gateway.SendAfterInit(connection, Message(requestId, query, hits));
    }

    /// <summary>清單裡的每個對話：快取的內容（last_seq 變了先重讀）＋標題，照清單的順序比對</summary>
    internal List<(string ConversationId, string? Snippet)> Search(IReadOnlyList<string> terms, CancellationToken cancellationToken)
    {
        var rows = reader.List().Rows;
        var stale = rows.Where(r => !cache.TryGetValue(r.ConversationId, out var c) || c.Stamp != r.LastSeq).ToList();
        if (stale.Count > 0)
        {
            var stamps = stale.ToDictionary(r => r.ConversationId, r => r.LastSeq, StringComparer.Ordinal);
            reader.ReadSearchItems(stale.Select(r => r.ConversationId).ToList(), (conversationId, items) =>
            {
                Interlocked.Increment(ref conversationReads);
                var texts = new List<string>(items.Count);
                foreach (var (enc, body) in items)
                {
                    if (HubItemRenderer.TryReadSearchText(blobs, enc, body, out var text) == ItemTextOutcome.Ok && text.Length > 0)
                        texts.Add(text);
                }
                // 戳記是讀清單時的 last_seq：項目在兩次讀之間又變了，下次 last_seq 比它大，會再讀一次
                cache[conversationId] = new Cached(stamps[conversationId], texts.ToArray());
            }, cancellationToken);
        }

        var listed = new HashSet<string>(rows.Select(r => r.ConversationId), StringComparer.Ordinal);
        foreach (var id in cache.Keys)
        {
            if (!listed.Contains(id))
                cache.TryRemove(id, out _);
        }

        var hits = new List<(string, string?)>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var texts = cache.TryGetValue(row.ConversationId, out var cached) ? cached.Texts : [];
            if (ConversationSearchRules.Match(row.Title, texts, terms) is { } match)
                hits.Add((row.ConversationId, match.Snippet));
        }
        return hits;
    }

    /// <summary><c>{type:"conversationSearch", requestId, query, hits:[{conversationId, snippet}]}</c>（web-protocol 第一節；片段是純文字，網頁以文字節點顯示）</summary>
    internal static ClientOutboxMessage Message(string requestId, string query, IReadOnlyList<(string ConversationId, string? Snippet)> hits) =>
        new(ResultType, new JObject
        {
            ["type"] = ResultType,
            ["requestId"] = requestId,
            ["query"] = query,
            ["hits"] = new JArray(hits.Select(h => new JObject { ["conversationId"] = h.ConversationId, ["snippet"] = h.Snippet })),
        }.ToString(Formatting.None), Key: ResultType);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        foreach (var cts in runs.Values)
            Cancel(cts);
    }

    /// <summary>取消；那次搜尋剛好結束、已經釋放時不理（它在釋放前會先自己從 runs 拿掉，但兩邊可能同時）</summary>
    private static void Cancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
