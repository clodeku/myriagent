using Myriagent.Sync;
using MyriagentHub.Hosting;
using Newtonsoft.Json;

namespace MyriagentHub.Sync;

/// <summary>
/// 輸入框的草稿（draft1；docs/sync-protocol.md 14.17）：每個對話最後一份。給人看的、可丟，所以不進 sync.db（不寫事件、不改結構，
/// 部署失敗回滾到上一版也不受影響）：放在記憶體，變了之後 <see cref="SaveDelay"/> 寫一次 <c>db\composer-drafts.json</c>（暫存檔再換名），
/// 伺服器重開時讀回來（壞掉、讀不到就當成沒有）。超過 <see cref="ComposerDraftRules.MaxAge"/> 沒再改的丟掉、最多
/// <see cref="ComposerDraftRules.MaxDrafts"/> 個（丟最舊的）。
/// <see cref="Changed"/> 在鎖外、呼叫 <see cref="Publish"/> 的執行緒上觸發：訂閱者只能入列或排工作，不可阻塞。任何執行緒都能呼叫
/// </summary>
internal sealed class ComposerDraftStore(HubPaths paths, TimeProvider time, ILogger<ComposerDraftStore> log) : IHostedService
{
    /// <summary>變了之後多久寫檔（期間的變動併成一次）</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    public const string FileName = "composer-drafts.json";

    private readonly object gate = new();
    private Dictionary<string, ComposerDraftView>? drafts;
    private long lastRev;
    private bool saveScheduled;
    private int saveVersion;
    private int savedVersion;

    /// <summary>一則草稿變了（文字是空字串＝清掉）；<c>origin</c> 是送來的那條連線（裝置連線 id 加 <c>d:</c>、瀏覽器連線 id 加 <c>b:</c>），轉送時略過它</summary>
    public event Action<ComposerDraftView, string?>? Changed;

    internal string FilePath => Path.Combine(paths.DbDir, FileName);

    /// <summary>目前所有沒清掉的草稿（依 rev 由舊到新）</summary>
    public IReadOnlyList<ComposerDraftView> Snapshot()
    {
        lock (gate)
        {
            var map = Loaded();
            Prune(map, time.GetUtcNow().ToUnixTimeMilliseconds());
            return map.Values.OrderBy(d => d.Rev).ToList();
        }
    }

    /// <summary>
    /// 一則草稿（已經過 <see cref="ComposerDraftRules.Check"/>）：和目前的相同、或清掉一則本來就沒有的，回 null（不觸發）；否則存下、觸發 <see cref="Changed"/>、回傳新的那份
    /// </summary>
    public ComposerDraftView? Publish(string conversationId, string text, string? from, string? origin)
    {
        ComposerDraftView view;
        var empty = ComposerDraftRules.IsEmpty(text);
        // 疑似權杖：不存、不轉（瀏覽器打的；桌面送之前自己就擋了）
        if (!ComposerDraftRules.Syncable(text))
            return null;
        lock (gate)
        {
            var map = Loaded();
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            map.TryGetValue(conversationId, out var current);
            if (empty ? current is null : current is not null && string.Equals(current.Text, text, StringComparison.Ordinal))
                return null;
            lastRev = Math.Max(lastRev + 1, now);
            view = new ComposerDraftView(conversationId, empty ? "" : text, lastRev, now, ComposerDraftRules.FromLabel(from));
            if (empty)
                map.Remove(conversationId);
            else
                map[conversationId] = view;
            Prune(map, now);
            ScheduleSave();
        }
        try
        {
            Changed?.Invoke(view, origin);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "輸入框草稿的 Changed 處理者出錯");
        }
        return view;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>停機：還沒寫的照寫</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        SaveNow();
        return Task.CompletedTask;
    }

    /// <summary>寫檔（排定的時間到了、停機；測試直接呼叫）：沒有變動不寫</summary>
    internal void SaveNow()
    {
        string json;
        int version;
        lock (gate)
        {
            saveScheduled = false;
            if (drafts is null || saveVersion == savedVersion)
                return;
            version = saveVersion;
            json = JsonConvert.SerializeObject(new SavedDrafts(lastRev, drafts.Values.OrderBy(d => d.Rev).ToList()), Formatting.None);
        }
        try
        {
            Directory.CreateDirectory(paths.DbDir);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
            lock (gate)
            {
                if (savedVersion < version)
                    savedVersion = version;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "輸入框草稿寫檔失敗（下次變動再寫）");
        }
    }

    private void ScheduleSave()
    {
        saveVersion++;
        if (saveScheduled)
            return;
        saveScheduled = true;
        _ = SaveLaterAsync();
    }

    private async Task SaveLaterAsync()
    {
        try
        {
            await Task.Delay(SaveDelay, time);
        }
        catch (Exception)
        {
            // 不會發生（沒有取消）；照寫
        }
        SaveNow();
    }

    /// <summary>第一次用到時讀檔（在鎖裡）</summary>
    private Dictionary<string, ComposerDraftView> Loaded()
    {
        if (drafts is not null)
            return drafts;
        drafts = new Dictionary<string, ComposerDraftView>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(FilePath) && JsonConvert.DeserializeObject<SavedDrafts>(File.ReadAllText(FilePath)) is { } saved)
            {
                lastRev = Math.Max(0, saved.LastRev);
                foreach (var d in saved.Drafts ?? [])
                {
                    if (d is null || ComposerDraftRules.Check(d.ConversationId, d.Text) is not null || ComposerDraftRules.IsEmpty(d.Text))
                        continue;
                    drafts[d.ConversationId] = d with { From = ComposerDraftRules.FromLabel(d.From) };
                    lastRev = Math.Max(lastRev, d.Rev);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.LogWarning(ex, "讀不到輸入框草稿的檔案，當成沒有");
            drafts.Clear();
        }
        return drafts;
    }

    private void Prune(Dictionary<string, ComposerDraftView> map, long nowMs)
    {
        var limit = nowMs - (long)ComposerDraftRules.MaxAge.TotalMilliseconds;
        var removed = false;
        foreach (var old in map.Values.Where(d => d.UpdatedAt < limit).Select(d => d.ConversationId).ToList())
            removed |= map.Remove(old);
        if (map.Count > ComposerDraftRules.MaxDrafts)
        {
            foreach (var old in map.Values.OrderBy(d => d.Rev).Take(map.Count - ComposerDraftRules.MaxDrafts).Select(d => d.ConversationId).ToList())
                removed |= map.Remove(old);
        }
        if (removed)
            ScheduleSave();
    }

    private sealed record SavedDrafts(long LastRev, IReadOnlyList<ComposerDraftView>? Drafts);
}
