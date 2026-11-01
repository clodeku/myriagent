using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Rendering;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 回覆的媒體「留一份」（<see cref="MediaKeepRules"/>，2026-10-25 使用者要求）：一則回覆、工具或代理送來的媒體完成時排進 <see cref="MediaKeeper"/>，
/// 背景複製好回 UI 執行緒記進 <see cref="AgentChatItem.KeptMedia"/>、重送那一筆給開著的畫面；之後渲染一律換成副本（代理媒體的路徑、
/// 回覆 Markdown 引用的本機圖片）。只有這台的對話；中樞預設沒有接（不留），只有 AppController 以 <see cref="AttachMediaKeeper"/> 接上。
/// 項目已經記了副本時，渲染照樣換成副本（看的是資料，不看有沒有接）
/// </summary>
internal sealed partial class AgentHub
{
    private MediaKeeper? mediaKeeper;
    private KeptMediaUrls? keptMediaUrls;
    private ItemRenderer? keptMediaRenderer;

    /// <summary>接上留媒體副本的背景執行緒（AppController；測試自己接）</summary>
    public void AttachMediaKeeper()
        => mediaKeeper ??= new MediaKeeper((conversationId, itemId, kept) => host.BeginInvoke(() => OnMediaKept(conversationId, itemId, kept)), Log.Warn);

    /// <summary>測試用：留媒體副本的背景執行緒（沒接是 null）</summary>
    internal MediaKeeper? MediaKeeperForTests => mediaKeeper;

    /// <summary>
    /// 這一筆的媒體確定了（回覆文字完成、工具完成、代理送來媒體）：排進背景留副本。<paramref name="markdown"/>＝另外找回覆 Markdown 引用的本機圖片
    /// （只有文字裡有圖片語法時才交給背景解析）
    /// </summary>
    private void KeepMediaLater(ConversationState c, AgentChatItem item, bool markdown)
    {
        if (mediaKeeper is null || disposed || c.IsDraft || c.IsReplica)
            return;
        var mediaPaths = item.MediaPaths ?? [];
        var text = markdown && item.Text is { } t && t.Contains("![", StringComparison.Ordinal) ? t : null;
        if (mediaPaths.Count == 0 && text is null)
            return;
        mediaKeeper.Enqueue(new MediaKeeper.Job(c.Meta.Id, item.Id, store.GetConversationDirectory(c.Meta.Id), mediaPaths.ToList(), text,
            text is null ? null : GetWorkingDirectory(c), item.KeptMedia?.Keys.ToList() ?? []));
    }

    /// <summary>
    /// 延伸出來的對話補留其他對話資料夾裡的媒體（<see cref="MediaKeepRules.NeedsForkKeep"/>）：只留 conversations 底下、
    /// 不在自己資料夾與工作資料夾裡的（工作資料夾是原對話的工作區時，遠端本來就放行、新對話之後也繼續用它）
    /// </summary>
    private MediaKeeper.Job ForkKeepJob(string conversationId, AgentChatItem item, string conversationDirectory, string? workingDirectory)
        => new(conversationId, item.Id, conversationDirectory, item.MediaPaths?.ToList() ?? [], MediaKeepRules.MarkdownTextOf(item), workingDirectory,
            item.KeptMedia?.Keys.ToList() ?? [], OnlyUnder: store.RootDirectory, ExceptUnder: workingDirectory);

    /// <summary>這次執行看過的延伸對話（每個對話只補一次）</summary>
    private readonly HashSet<string> forkMediaChecked = new(StringComparer.Ordinal);

    /// <summary>
    /// 修正之前延伸出來的對話（2026-11-01）：載入紀錄時，引用其他對話資料夾裡媒體的項目排進背景補留一份（同延伸時的 <see cref="ForkKeepJob"/>），
    /// 留好照常經 <see cref="OnMediaKept"/> 記進項目；之後遠端取檔與縮圖給的是副本。每次執行每個對話只看一次；沒有接上（測試）不做
    /// </summary>
    private void RepairForkMediaLater(ConversationState c)
    {
        if (mediaKeeper is null || disposed || c.IsDraft || c.IsReplica || c.Meta.ForkedFrom is null || !forkMediaChecked.Add(c.Meta.Id))
            return;
        var directory = store.GetConversationDirectory(c.Meta.Id);
        var workingDirectory = GetWorkingDirectory(c);
        foreach (var item in c.Items)
        {
            if (item is not null && MediaKeepRules.NeedsForkKeep(item, store.RootDirectory, directory, workingDirectory))
                mediaKeeper.Enqueue(ForkKeepJob(c.Meta.Id, item, directory, workingDirectory));
        }
    }

    /// <summary>
    /// 背景留好了（UI 執行緒）：記進項目、重送給開著的畫面（網址換成副本；線上的內容沒變，不經同步的掛鉤），這一輪結束了就在背景存紀錄
    /// （執行中的由定期維護存）。對話已經刪掉、項目已經不在或紀錄沒載入就不記（副本留在資料夾裡，跟著對話一起刪）
    /// </summary>
    private void OnMediaKept(string conversationId, string itemId, IReadOnlyDictionary<string, string> kept)
    {
        if (disposed || !conversations.TryGetValue(conversationId, out var c) || c.IsReplica || !c.ItemsLoaded)
            return;
        var item = c.Items.FirstOrDefault(i => i is not null && string.Equals(i.Id, itemId, StringComparison.Ordinal));
        if (item is null)
            return;
        item.KeptMedia ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (original, relative) in kept)
            item.KeptMedia[original] = relative;
        c.ItemsUnsaved = true;
        if (c.ViewIds.Count > 0)
        {
            if (!dirtyItems.TryGetValue(c.Meta.Id, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                dirtyItems[c.Meta.Id] = set;
            }
            set.Add(item.Id);
            ScheduleFlush();
        }
        if (!c.Running)
            SaveItemsInBackground(c);
    }

    /// <summary>只存紀錄（快照交給背景寫，同定期維護）：中繼資料沒變，不經存檔的同步掛鉤</summary>
    private void SaveItemsInBackground(ConversationState c)
    {
        if (c.IsDraft || c.IsReplica || !c.ItemsLoaded || !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c))
            return;
        var snapshot = new List<AgentChatItem>(c.Items.Count);
        foreach (var item in c.Items)
            snapshot.Add(item?.CloneForSave()!);
        ItemsWriter.Enqueue(c.Meta.Id, store.GetItemsPath(c.Meta.Id), snapshot);
        c.ItemsUnsaved = false;
    }

    /// <summary>測試用：照畫面的方式渲染一個對話的一筆項目（UI 執行緒）</summary>
    internal JObject RenderItemForTests(string conversationId, string itemId)
    {
        var c = conversations[conversationId];
        return RenderItemOf(c, c.Items.Single(i => i.Id == itemId), null);
    }

    /// <summary>
    /// 這台的項目：留了副本（而且副本還在）就把代理媒體的路徑與 Markdown 引用的本機圖片換成副本再渲染（畫面、另存新檔、在檔案總管顯示都是副本），
    /// 沒有的照常渲染
    /// </summary>
    private JObject RenderLocalItem(ConversationState c, AgentChatItem item, string workingDirectory)
    {
        if (item.KeptMedia is not { Count: > 0 } kept)
            return Renderer.Render(item, workingDirectory);
        var directory = store.GetConversationDirectory(c.Meta.Id);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (original, relative) in kept)
        {
            if (MediaKeepRules.Normalize(original) is { } key && MediaKeepRules.FullPath(directory, relative) is { } full && File.Exists(full))
                map[key] = full;
        }
        if (map.Count == 0)
            return Renderer.Render(item, workingDirectory);

        var shown = item;
        if (item.MediaPaths is { Count: > 0 } media && media.Any(p => KeptMediaUrls.Swap(map, p) is not null))
        {
            shown = ChatItemCopyRules.Copy(item);
            shown.MediaPaths = media.Select(p => KeptMediaUrls.Swap(map, p) ?? p).ToList();
        }

        keptMediaUrls ??= new KeptMediaUrls(mediaUrls);
        keptMediaRenderer ??= new ItemRenderer(keptMediaUrls, Log.Error, Log.Warn);
        keptMediaUrls.Map = map;
        try
        {
            return keptMediaRenderer.Render(shown, workingDirectory);
        }
        finally
        {
            keptMediaUrls.Map = null;
        }
    }

    /// <summary>
    /// 渲染留了副本的項目用的網址：Markdown 引用的本機圖片換成副本（網址與白名單都是副本），其他照 <see cref="LocalMediaUrls"/>。
    /// 只在 UI 執行緒、一次渲染一筆（<see cref="Map"/> 渲染前設、渲染後清掉）
    /// </summary>
    private sealed class KeptMediaUrls(LocalMediaUrls inner) : IMediaUrlBuilder
    {
        /// <summary>原檔（正規化後）→ 副本完整路徑</summary>
        public IReadOnlyDictionary<string, string>? Map { get; set; }

        /// <summary>原檔對到的副本；沒有回 null</summary>
        public static string? Swap(IReadOnlyDictionary<string, string> map, string? path)
            => MediaKeepRules.Normalize(path) is { } key && map.TryGetValue(key, out var full) ? full : null;

        private string Shown(string path) => Map is { } map ? Swap(map, path) ?? path : path;

        public string MediaUrl(string path) => inner.MediaUrl(Shown(path));

        public string ThumbUrl(string path, int size, string? version = null) => inner.ThumbUrl(Shown(path), size, version);

        public string MarkdownImageUrl(string path) => inner.MarkdownImageUrl(Shown(path));

        public string MarkdownLinkUrl(string path) => inner.MarkdownLinkUrl(path);

        public void AllowMarkdownImage(string path) => inner.AllowMarkdownImage(Shown(path));

        public void AllowDocument(string path) => inner.AllowDocument(path);
    }
}
