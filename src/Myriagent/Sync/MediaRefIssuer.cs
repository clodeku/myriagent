using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Microsoft.Data.Sqlite;

namespace Myriagent.Sync;

/// <summary>
/// 投影時配發媒體參照（WP4.7 計畫 D3～D5；<see cref="IItemMediaHook"/> 的實作，引擎執行緒、不加鎖）：
/// 規則在 <see cref="MediaRefIssueRules"/>，正本在 <c>media_refs_local</c>（items.json 不存參照）。新參照先寫 <c>media_refs_local</c>（自己的交易），
/// 投影器之後才寫 outbox：寫 outbox 失敗或當機時，下一次投影沿用同一個參照。<c>media_refs_local</c> 用不了（表不在、SQLite 錯誤）時照樣投影、不帶新參照。
/// 定稿（不是 Running）而且有參照的項目交給 <paramref name="finalized"/>（縮圖上傳，D30）。項目欄位 blob 的兩個方法（步 10）轉給 <paramref name="blobs"/>
/// </summary>
/// <param name="store">目前的 sync.db（null＝還沒開或已關）</param>
/// <param name="nowMs">列的建立時間（Unix ms，本機時鐘，只給修剪用）</param>
/// <param name="random">亂數位元組（正式是 RandomNumberGenerator）</param>
/// <param name="finalized">定稿的項目與它的參照列（線上的對話 id、列）；null＝不上傳縮圖</param>
/// <param name="blobs">項目欄位 blob 的引擎那一側（步 10）；null＝一律照 WP4.3 截斷</param>
internal sealed class MediaRefIssuer(Func<SyncStore?> store, Func<long> nowMs, Func<int, byte[]> random,
    Action<string, IReadOnlyList<LocalMediaRefRow>>? finalized = null, IItemBlobUploads? blobs = null) : IItemMediaHook
{
    /// <summary>寫進 media_refs_local 的新參照數（記錄與測試用）</summary>
    public int Issued { get; private set; }

    public AgentChatItem AssignRefs(string wireId, AgentChatItem snapshot)
    {
        ArgumentNullException.ThrowIfNull(wireId);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!MediaRefIssueRules.HasDisplayableMedia(snapshot) || Ready() is not { } s)
            return snapshot;

        IReadOnlyList<LocalMediaRefRow> existing;
        IReadOnlyList<AgentMediaRef> refs;
        IReadOnlyList<LocalMediaRefRow> added;
        try
        {
            existing = s.LoadMediaRefs(wireId, snapshot.Id);
            refs = MediaRefIssueRules.Merge(snapshot, wireId, existing, () => MediaRefIssueRules.NewRef(random), nowMs(), out added);
            if (added.Count > 0)
            {
                s.AddMediaRefs(added);
                Issued += added.Count;
            }
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or ObjectDisposedException)
        {
            Log.Warn($"對話 {wireId} 的項目配媒體參照失敗（這次不帶參照，之後重整再配）：{ex.Message}");
            return snapshot;
        }

        if (refs.Count == 0)
            return snapshot;
        var copy = ChatItemCopyRules.Copy(snapshot);
        copy.MediaRefs = refs.ToList();
        if (snapshot.Status != AgentItemStatus.Running && finalized is not null)
        {
            var rows = new List<LocalMediaRefRow>(existing.Count + added.Count);
            rows.AddRange(existing.Where(r => refs.Any(m => string.Equals(m.Ref, r.Ref, StringComparison.Ordinal))));
            rows.AddRange(added);
            finalized(wireId, rows);
        }

        return copy;
    }

    public bool RefsMissing(string wireId, AgentChatItem item)
    {
        ArgumentNullException.ThrowIfNull(wireId);
        ArgumentNullException.ThrowIfNull(item);
        if (!MediaRefIssueRules.HasDisplayableMedia(item) || Ready() is not { } s)
            return false;
        try
        {
            return MediaRefIssueRules.Missing(item, wireId, s.LoadMediaRefs(wireId, item.Id));
        }
        catch (Exception ex) when (ex is SqliteException or ObjectDisposedException)
        {
            Log.Warn($"讀對話 {wireId} 的媒體參照失敗（當成不缺）：{ex.Message}");
            return false;
        }
    }

    public void Forget(string wireId, string? itemId)
    {
        ArgumentNullException.ThrowIfNull(wireId);
        if (Ready() is not { } s)
            return;
        try
        {
            var removed = s.RemoveMediaRefs(wireId, itemId);
            if (removed > 0)
                Log.Info(itemId is null ? $"對話 {wireId} 刪除或改成只在這台電腦：刪掉 {removed} 個媒體參照" : $"對話 {wireId} 的項目移除：刪掉 {removed} 個媒體參照");
        }
        catch (Exception ex) when (ex is SqliteException or ObjectDisposedException)
        {
            Log.Warn($"刪對話 {wireId} 的媒體參照失敗（請求時仍會依對話的狀態擋下）：{ex.Message}");
        }
    }

    public bool UseBlobs(string wireId, AgentChatItem snapshot) => blobs?.UseBlobs(wireId, snapshot) ?? false;

    public bool EnsureUploaded(string wireId, string itemId, IReadOnlyList<ConversationBlob> pending)
        => blobs?.EnsureUploaded(wireId, itemId, pending) ?? false;

    private SyncStore? Ready() => store() is { ConversationTablesReady: true } s ? s : null;
}
