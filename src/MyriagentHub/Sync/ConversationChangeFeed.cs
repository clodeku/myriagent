using System.Collections.Concurrent;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Sync;

/// <summary>變更的種類（<see cref="ConversationChange.Kind"/>）</summary>
internal enum ConversationChangeKind
{
    /// <summary><c>conv.created</c>／<c>conv.meta</c>：列的中繼資料（含政策）換了</summary>
    Meta,

    Turn,

    Attention,

    /// <summary>在伺服器上刪除（墓碑；內容已在同一個交易清掉，答 4、D34）</summary>
    Deleted,

    Item,

    ItemRemoved,

    Queue,

    /// <summary>
    /// 政策從 full 降成 metadata／paused，清掉了物化的內容（WP4.2 步 4、答 4）：緊接在同一個 seq 的 <see cref="Meta"/> 之後送；
    /// 開著這個對話的瀏覽器要 resync，疊加層與 WatchChanged 要清掉（步 10～12）
    /// </summary>
    Purged,

    /// <summary>重建做完了一個對話（WP4.2 步 5）</summary>
    Rebuilt,
}

/// <summary>
/// 一筆提交之後的對話變更（閘道、即時疊加層、未讀的來源；WP4.2 計畫 D13）。<see cref="Body"/> 是存下的版本（命中 DLP 時是遮蔽版，D44），
/// 只在 16 KB 以內才附上，否則讀的一方自己讀 <c>items</c>。
/// </summary>
/// <param name="Author">寫這筆事件的裝置（擁有者）</param>
/// <param name="Final">項目的狀態不會再變（D9）</param>
/// <param name="Body"><c>conv.item</c> 的 payload JSON（解碼後）；超過 <see cref="ConversationChangeFeed.MaxInlineBodyBytes"/> 時 null</param>
/// <param name="AttentionKind"><c>conv.attention</c> 的 kind（D28）</param>
/// <param name="Speech"><c>conv.attention</c> 的 speech（語音通知念的句子；null＝擁有者沒帶，閘道自己組只有標題的句子；空字串＝不念）</param>
/// <param name="Status">項目的狀態名稱（疊加層的清除規則，提案 C4）</param>
/// <param name="TurnPhase"><c>conv.turn</c> 的 phase（未讀，D45）</param>
/// <param name="Fresh">新的上行事件（<see cref="ConversationApplyRules.IsFresh"/>）：對帳重送的舊事件與重建都不帶（未讀，D45）</param>
/// <param name="ReceivedAt">
/// 伺服器收到這筆事件的時間（Unix 毫秒，同 <c>events.created_at</c>）：閘道以附上的 <see cref="Body"/> 渲染時，項目沒有 createdAt 就當成建立時間
/// （同讀 <c>items</c> 時的 <c>ConversationItemRow.ReceivedAt</c>，WP4.2 步 10）；只有 <c>conv.item</c> 帶
/// </param>
internal sealed record ConversationChange(string ConversationId, ConversationChangeKind Kind, long Seq, string Author,
    string? ItemId = null, long? Ord = null, long? Ver = null, bool Final = false, string? Body = null, string? AttentionKind = null,
    string? Status = null, string? TurnPhase = null, bool Fresh = false, long ReceivedAt = 0, string? Speech = null);

/// <summary>
/// 對話的變更通知（WP4.2 計畫 D13）：寫入者的 AfterCommit 以 <see cref="Post"/> 放進有界的佇列（O(1)），消費者（閘道迴圈）以
/// <see cref="TryDrain"/> 一次取走。超過則數或位元組上限時丟掉這一筆並設「整份重讀」旗標，消費者看到旗標就重讀快照，不會漏掉狀態。
/// <see cref="Posted"/> 的處理者只能喚醒消費者，不能在寫入執行緒上做事。
/// </summary>
internal sealed class ConversationChangeFeed(IOptions<HubOptions> options)
{
    /// <summary>變更裡附上 body 的上限（UTF-16 字數×2 估計的位元組；超過的由讀的一方自己讀）</summary>
    public const int MaxInlineBodyBytes = 16 * 1024;

    /// <summary>每則變更除了 body 之外的估計開銷</summary>
    public const int EntryOverheadBytes = 200;

    private readonly ConcurrentQueue<(ConversationChange Change, long Bytes)> queue = new();

    private long entries;

    private long bytes;

    private int fullRefresh;

    /// <summary>有新的變更或設了「整份重讀」（在寫入執行緒上觸發：處理者只能喚醒）</summary>
    public event Action? Posted;

    /// <summary>佇列裡目前的則數（測試與營運用）</summary>
    public long Pending => Interlocked.Read(ref entries);

    /// <summary>有變更等著取、或設了「整份重讀」（閘道決定這一輪要不要處理，WP4.2 步 10）</summary>
    public bool HasPending => Interlocked.Read(ref entries) > 0 || Volatile.Read(ref fullRefresh) != 0;

    /// <summary>放進一筆（只在 AfterCommit）；滿了就丟掉並設「整份重讀」</summary>
    public void Post(ConversationChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var settings = options.Value.Conversations;
        var size = EntryOverheadBytes + (change.Body?.Length ?? 0) * 2L;
        if (Interlocked.Read(ref entries) >= settings.ChangeFeedMaxEntries || Interlocked.Read(ref bytes) + size > settings.ChangeFeedMaxBytes)
        {
            Interlocked.Exchange(ref fullRefresh, 1);
        }
        else
        {
            Interlocked.Increment(ref entries);
            Interlocked.Add(ref bytes, size);
            queue.Enqueue((change, size));
        }
        Posted?.Invoke();
    }

    /// <summary>取走目前所有的變更（依提交順序）；<paramref name="fullRefresh"/>＝之前有變更因為滿了被丟掉，要整份重讀。有取到或要重讀時回 true</summary>
    public bool TryDrain(List<ConversationChange> into, out bool fullRefresh)
    {
        ArgumentNullException.ThrowIfNull(into);
        var any = false;
        // 先清旗標再取：清旗標之後才被丟掉的會再設一次，下一輪看得到
        fullRefresh = Interlocked.Exchange(ref this.fullRefresh, 0) == 1;
        while (queue.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref entries);
            Interlocked.Add(ref bytes, -item.Bytes);
            into.Add(item.Change);
            any = true;
        }
        return any || fullRefresh;
    }
}
