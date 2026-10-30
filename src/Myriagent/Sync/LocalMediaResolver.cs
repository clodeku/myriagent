using System.Collections.Concurrent;
using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Newtonsoft.Json;

namespace Myriagent.Sync;

/// <summary>參照對到的檔：<see cref="Sequence"/> 是代理媒體在項目 MediaPaths 裡的序號（從 1 起；附件與抽屜的是 null）</summary>
internal sealed record ResolvedMedia(string Path, string FileName, int? Sequence);

/// <summary>
/// <c>media_refs_local</c> 的列 → 本機的檔（WP4.7 計畫 D26、D45；<see cref="MediaServer"/> 與 <see cref="ThumbnailPublisher"/> 共用，任何執行緒）：
/// <list type="number">
/// <item>經 RunOnUiAsync 問中樞的快照（<see cref="QueryAsync"/>），對話不可見就到此為止。</item>
/// <item>解析路徑（<see cref="Resolve"/>）：附件由 <c>attachment_id</c>；項目已載入時用中樞解析的，沒載入時唯讀讀 items.json（同一份檔重用索引）。</item>
/// <item>Codex 產生的圖片（Q2-A，<see cref="SubstituteGeneratedAsync"/>）改用 <c>attachments\generated\</c> 的複本（第一次用時複製）。</item>
/// <item>守門員的範圍（<see cref="BuildScopeAsync"/>）：原檔與縮圖只收這一個檔，檔案下載是工作資料夾與 attachments\；根目錄的正規化快取 1 分鐘。</item>
/// </list>
/// 不碰 SQLite；中樞的狀態只在快照裡讀
/// </summary>
internal sealed class LocalMediaResolver
{
    private const string ItemsFileName = "items.json";

    private const int ReadBufferBytes = 64 * 1024;

    /// <summary>未載入的對話 items.json 的索引最多留幾個對話（超過就丟最早建的那一個）</summary>
    private const int MaxIndexedConversations = 8;

    /// <summary>
    /// 讀 items.json 的索引一次只有一個（合併前審查 R1：同一個對話的圖片同時進來 20 個轉送時，同一份檔只讀、只解析一次，其他的等同一個結果）。
    /// 寫 <see cref="indexes"/> 與 <see cref="indexOrder"/> 都在這把鎖裡；讀快取不用鎖
    /// </summary>
    private readonly object indexLoadGate = new();

    /// <summary>索引建立的先後（超過上限時丟最早的；只在 <see cref="indexLoadGate"/> 裡用）</summary>
    private readonly Queue<string> indexOrder = new();

    private static readonly JsonSerializer IndexSerializer = CreateIndexSerializer();

    private int indexReads;

    /// <summary>根目錄正規化的快取（GetDriveType、GetLongPathName）留多久</summary>
    private static readonly TimeSpan RootCacheLifetime = TimeSpan.FromMinutes(1);

    private const int MaxCachedRoots = 256;

    private readonly Func<MediaScopeQuery, CancellationToken, Task<MediaScopeSnapshot?>> scope;
    private readonly ConcurrentDictionary<string, ItemsIndex> indexes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string? Normalized, long Until)> roots = new(StringComparer.Ordinal);
    private readonly GeneratedMediaCopier copier;

    private sealed record ItemsIndex(long Length, long LastWriteTicks, IReadOnlyDictionary<string, AgentChatItem> Items)
    {
        /// <summary>同一份檔（長度與修改時間都相同）</summary>
        public bool Matches(long length, long lastWriteTicks) => Length == length && LastWriteTicks == lastWriteTicks;
    }

    /// <param name="scope">經 RunOnUiAsync 問中樞（對話不在回 null）</param>
    /// <param name="guard">守門員（開檔、正規化根目錄、Codex 產生圖片的原檔）</param>
    public LocalMediaResolver(Func<MediaScopeQuery, CancellationToken, Task<MediaScopeSnapshot?>> scope, RemoteFileGuard guard)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(guard);
        this.scope = scope;
        Guard = guard;
        copier = new GeneratedMediaCopier(guard);
    }

    public RemoteFileGuard Guard { get; }

    /// <summary>Codex 產生圖片的複製（測試看複製了幾次）</summary>
    internal GeneratedMediaCopier Copier => copier;

    /// <summary>測試：讀過幾次 items.json（建索引的次數）</summary>
    internal int IndexReadsForTest => Volatile.Read(ref indexReads);

    /// <summary>測試接縫：建索引之前呼叫（在鎖裡；測試拿來拉長同時進來的時間窗）</summary>
    internal Action? IndexReadingForTest { get; set; }

    /// <summary>問中樞這個參照的對話快照</summary>
    public Task<MediaScopeSnapshot?> QueryAsync(LocalMediaRefRow row, string deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        return scope(new MediaScopeQuery(row.ConversationId, row.ItemId, row.AttachmentId, row.MediaPath, deviceId), ct);
    }

    /// <summary>參照對到的檔（項目或附件已經不在、items.json 讀不了回 null）；只對看得到的快照呼叫</summary>
    public ResolvedMedia? Resolve(MediaScopeSnapshot snapshot, LocalMediaRefRow row)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsLooseFile)
            return new ResolvedMedia(row.MediaPath!, Path.GetFileName(row.MediaPath!), null);
        if (snapshot.ItemsLoaded)
        {
            if (snapshot.Resolved is not { } resolved)
                return null;
            if (row.AttachmentId is not null)
                return new ResolvedMedia(resolved.Path, resolved.FileName, null);
            return new ResolvedMedia(KeptOr(snapshot.KeptPath, resolved.Path), resolved.FileName, snapshot.MediaSequence);
        }
        return ResolveFromFile(snapshot.ConversationDirectory, row);
    }

    /// <summary>
    /// Codex 產生的圖片（Q2-A）：代理媒體的路徑在產生圖片的根目錄底下時，回 <c>attachments\generated\</c> 的複本（沒有就先複製；做不到回 null）；
    /// 其他的照原路徑。呼叫端在遠端政策的判斷之後才呼叫（被擋的請求不碰檔案系統）
    /// </summary>
    public async Task<string?> SubstituteGeneratedAsync(MediaScopeSnapshot snapshot, LocalMediaRefRow row, ResolvedMedia target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(target);
        if (row.IsLooseFile || row.AttachmentId is not null)
            return target.Path;
        var generatedRoots = new List<string>();
        foreach (var root in GeneratedMediaRules.Roots(snapshot.Folders))
        {
            if (await NormalizeAsync(root, ct).ConfigureAwait(false) is { } normalized)
                generatedRoots.Add(normalized);
        }

        // 比對前先照守門員的字串層正規化（大寫磁碟代號、分隔字元）；不合格的照原樣比（之後開檔會被擋）
        var candidate = RemoteFileGuardRules.CheckSyntax(target.Path, out var normalizedPath) == null ? normalizedPath! : target.Path;
        if (!GeneratedMediaRules.IsGenerated(candidate, generatedRoots))
            return target.Path;
        if (target.Sequence is not { } sequence || GeneratedMediaRules.CopyPath(snapshot.AttachmentDirectory, row.ItemId, sequence, target.Path) is not { } copy)
        {
            Log.Info("Codex 產生的圖片算不出複本的路徑（序號或副檔名不合格）：不給");
            return null;
        }

        return await copier.EnsureCopyAsync(candidate, copy, generatedRoots, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 守門員的範圍：原檔與縮圖只收 <paramref name="path"/> 這一個檔（項目或抽屜登記過的），檔案下載是工作資料夾與 attachments\；禁止清單的資料夾一律先正規化
    /// （正規化不了的原樣保留，規則 fail-closed），範圍的根正規化不了就不算。繼承的 CODEX_HOME 和帳戶設定資料夾一樣進禁止清單
    /// </summary>
    public async Task<RemoteFileScope> BuildScopeAsync(MediaScopeSnapshot snapshot, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var folders = await RemoteScopeBuilder.FoldersAsync(snapshot.Folders, snapshot.ConversationDirectory, snapshot.DefaultWorkingDirectory,
            snapshot.AttachmentDirectory, NormalizeAsync, ct).ConfigureAwait(false);
        var listRoots = await RemoteScopeBuilder.RootsAsync([snapshot.WorkingDirectory, snapshot.AttachmentDirectory], NormalizeAsync, ct).ConfigureAwait(false);
        return new RemoteFileScope(snapshot.ConversationId, listRoots, new HashSet<string>([path], StringComparer.OrdinalIgnoreCase), folders);
    }

    /// <summary>根目錄正規化（同一個值 1 分鐘內重用：每個轉送都要算七八個根，GetDriveType 不必每次問）</summary>
    private async Task<string?> NormalizeAsync(string path, CancellationToken ct)
    {
        var now = Environment.TickCount64;
        if (roots.TryGetValue(path, out var cached) && cached.Until > now)
            return cached.Normalized;
        var normalized = await Guard.NormalizeRootAsync(path, ct).ConfigureAwait(false);
        if (roots.Count >= MaxCachedRoots)
            roots.Clear();
        roots[path] = (normalized, now + (long)RootCacheLifetime.TotalMilliseconds);
        return normalized;
    }

    /// <summary>項目沒載入（中樞不在 UI 執行緒讀檔）：唯讀讀 items.json 找參照對到的檔；同一份檔（長度與修改時間相同）重用索引</summary>
    private ResolvedMedia? ResolveFromFile(string conversationDirectory, LocalMediaRefRow row)
    {
        var path = Path.Combine(conversationDirectory, ItemsFileName);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            var length = info.Length;
            var ticks = info.LastWriteTimeUtc.Ticks;
            if (!indexes.TryGetValue(path, out var index) || !index.Matches(length, ticks))
                index = LoadIndex(path, length, ticks);

            var item = index.Items.GetValueOrDefault(row.ItemId);
            if (MediaServerRules.Resolve(item, row.AttachmentId, row.MediaPath) is not { } resolved)
                return null;
            if (row.AttachmentId is not null)
                return new ResolvedMedia(resolved.Path, resolved.FileName, null);
            var kept = KeptOr(MediaKeepRules.KeptPath(item!.KeptMedia, conversationDirectory, row.MediaPath), resolved.Path);
            return new ResolvedMedia(kept, resolved.FileName, GeneratedMediaRules.Sequence(item.MediaPaths, row.MediaPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Log.Warn($"讀對話的 items.json 失敗（媒體回 404）：{ex.Message}");
            return null;
        }
    }

    /// <summary>這台替代理媒體留的副本還在就給副本（原檔被刪或被覆寫也看得到當時那一張，<see cref="MediaKeepRules"/>），否則照原路徑</summary>
    private static string KeptOr(string? keptPath, string original) => keptPath is not null && File.Exists(keptPath) ? keptPath : original;

    /// <summary>
    /// 建索引（單一飛行）：在鎖裡再查一次快取，同一份檔已經有了就直接用；否則讀檔、寫回快取。新的對話超過上限時丟最早建的那一個（不整個清掉，
    /// 換對話瀏覽時其他的不必重讀）
    /// </summary>
    private ItemsIndex LoadIndex(string path, long length, long lastWriteTicks)
    {
        lock (indexLoadGate)
        {
            if (indexes.TryGetValue(path, out var cached) && cached.Matches(length, lastWriteTicks))
                return cached;
            IndexReadingForTest?.Invoke();
            Interlocked.Increment(ref indexReads);
            var index = ReadIndex(path, length, lastWriteTicks);
            if (!indexes.ContainsKey(path))
            {
                while (indexes.Count >= MaxIndexedConversations && indexOrder.TryDequeue(out var oldest))
                    indexes.TryRemove(oldest, out _);
                indexOrder.Enqueue(path);
            }

            indexes[path] = index;
            return index;
        }
    }

    /// <summary>
    /// 只留每個項目的 id、附件、媒體路徑與留下的副本：以串流反序列化成 <see cref="IndexEntry"/>（不產生整份檔的字串、不建文字與工具輸出的物件，
    /// 其他欄位略過；合併前審查 R1）
    /// </summary>
    private static ItemsIndex ReadIndex(string path, long length, long lastWriteTicks)
    {
        List<IndexEntry>? entries;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ReadBufferBytes, FileOptions.SequentialScan))
        using (var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
        using (var json = new JsonTextReader(reader))
            entries = IndexSerializer.Deserialize<List<IndexEntry>>(json);
        var map = new Dictionary<string, AgentChatItem>(StringComparer.Ordinal);
        foreach (var entry in entries ?? [])
        {
            if (entry?.Id is not { } id)
                continue;
            map[id] = new AgentChatItem { Id = id, Attachments = entry.Attachments ?? [], MediaPaths = entry.MediaPaths ?? [], KeptMedia = entry.KeptMedia };
        }

        return new ItemsIndex(length, lastWriteTicks, map);
    }

    /// <summary>存檔的設定（同 items.json 的 <see cref="JsonFile.Settings"/>）；和 JsonConvert.DeserializeObject 一樣檢查陣列之後沒有多餘的內容</summary>
    private static JsonSerializer CreateIndexSerializer()
    {
        var serializer = JsonSerializer.Create(JsonFile.Settings);
        serializer.CheckAdditionalContent = true;
        return serializer;
    }

    /// <summary>items.json 一個項目裡索引要的欄位（名稱同 <see cref="AgentChatItem"/>）</summary>
    private sealed class IndexEntry
    {
        public string? Id { get; set; }

        public List<AgentAttachment>? Attachments { get; set; }

        public List<string>? MediaPaths { get; set; }

        public Dictionary<string, string>? KeptMedia { get; set; }
    }
}
