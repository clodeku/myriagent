using System.Collections.Concurrent;
using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json;

namespace Myriagent.Hub;

/// <summary>
/// 側欄的對話搜尋（web-protocol 的 <c>searchConversations</c>）在背景執行緒做的部分：載入過的對話用中樞在 UI 執行緒擷取的內文，
/// 沒載入過的讀 items.json，只留搜得到的內文（<see cref="ConversationSearchRules.IsSearchable"/>），依檔案的修改時間與大小快取，
/// 下次沒變就不再讀。只讀：不經 <see cref="JsonFile.TryRead{T}"/>（損毀時會把原檔改名），讀不到或損毀的這次只比標題。執行緒安全
/// </summary>
internal sealed class ConversationSearchIndex
{
    /// <summary>
    /// 一個要搜的對話：<paramref name="Texts"/>＝已在記憶體的內文（載入過的對話、複本）；null 時讀 <paramref name="ItemsPath"/>（null＝沒有內文）
    /// </summary>
    public sealed record Source(string ConversationId, string? Title, IReadOnlyList<string>? Texts, string? ItemsPath);

    /// <summary>符合的對話（照 sources 的順序）與內文片段（只有標題符合時是 null）</summary>
    public sealed record Hit(string ConversationId, string? Snippet);

    private sealed record Cached(long Stamp, long Length, string[] Texts);

    private readonly ConcurrentDictionary<string, Cached> cache = new(StringComparer.Ordinal);

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>快取裡有幾個對話（測試用）</summary>
    internal int CachedCount => cache.Count;

    /// <summary>讀過幾次 items.json（測試用：確認沒變的檔案不重讀）</summary>
    internal int FileReads => Volatile.Read(ref fileReads);

    private int fileReads;

    /// <summary>
    /// 依序搜每個對話，回符合的；取消時丟 <see cref="OperationCanceledException"/>。搜完（沒取消）後把快取裡不在這次清單的對話丟掉
    /// （刪除、改成已載入的不再佔記憶體）
    /// </summary>
    public List<Hit> Search(IReadOnlyList<Source> sources, IReadOnlyList<string> terms, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(terms);
        var hits = new List<Hit>();
        if (terms.Count == 0)
            return hits;
        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<string> texts;
            if (source.Texts is { } inMemory)
                texts = inMemory;
            else if (source.ItemsPath is { } path)
            {
                fileIds.Add(source.ConversationId);
                texts = TextsOf(source.ConversationId, path);
            }
            else
                texts = Array.Empty<string>();
            if (ConversationSearchRules.Match(source.Title, texts, terms) is { } match)
                hits.Add(new Hit(source.ConversationId, match.Snippet));
        }
        foreach (var id in cache.Keys)
        {
            if (!fileIds.Contains(id))
                cache.TryRemove(id, out _);
        }
        return hits;
    }

    /// <summary>items.json 的內文：修改時間與大小都沒變就用快取；讀不到、損毀時用上次的（沒有就是空的），下次再讀</summary>
    private IReadOnlyList<string> TextsOf(string conversationId, string path)
    {
        long stamp, length;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                cache.TryRemove(conversationId, out _);
                return Array.Empty<string>();
            }
            (stamp, length) = (info.LastWriteTimeUtc.Ticks, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return cache.TryGetValue(conversationId, out var stale) ? stale.Texts : Array.Empty<string>();
        }
        if (cache.TryGetValue(conversationId, out var hit) && hit.Stamp == stamp && hit.Length == length)
            return hit.Texts;

        Interlocked.Increment(ref fileReads);
        var texts = ReadTexts(path);
        if (texts is null)
            return hit?.Texts ?? Array.Empty<string>();
        cache[conversationId] = new Cached(stamp, length, texts);
        return texts;
    }

    /// <summary>讀 items.json、只留搜得到的內文；讀不到或損毀回 null（不改名、不記錯誤：開對話時的讀取才處理）</summary>
    internal static string[]? ReadTexts(string path)
    {
        try
        {
            // 中樞存檔是寫暫存檔再取代：讀的時候允許別人寫入與取代，不擋住存檔
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using var reader = new StreamReader(stream, Utf8);
            // 時間欄位不解析成 DateTime（只要種類與文字）
            using var json = new JsonTextReader(reader) { DateParseHandling = DateParseHandling.None };
            var items = JsonSerializer.CreateDefault().Deserialize<List<SearchItem>>(json);
            if (items is null)
                return null;
            var texts = new List<string>(items.Count);
            foreach (var item in items)
            {
                if (item is not null && !string.IsNullOrEmpty(item.Text) && Enum.TryParse<AgentItemKind>(item.Kind, ignoreCase: false, out var kind)
                    && Enum.IsDefined(kind) && ConversationSearchRules.IsSearchable(kind))
                    texts.Add(item.Text);
            }
            return texts.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>items.json 裡搜尋要的兩個欄位（其他欄位略過；種類以字串讀，不認得的種類不算內文、也不讓整份讀不了）</summary>
    private sealed class SearchItem
    {
        [JsonProperty("Kind")]
        public string? Kind { get; set; }

        [JsonProperty("Text")]
        public string? Text { get; set; }
    }
}
