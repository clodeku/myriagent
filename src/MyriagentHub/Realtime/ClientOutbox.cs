using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace MyriagentHub.Realtime;

/// <summary>入列的結果：<see cref="Overflow"/> 時佇列已清空，呼叫端以重送 init 達成 resync（設計 14.6）</summary>
internal enum ClientOutboxResult
{
    Queued,
    Merged,
    Overflow,
}

/// <summary>要送給一條瀏覽器連線的一則宿主訊息（已序列化；同一份字串各連線共用）</summary>
/// <param name="Key">
/// 合併鍵（WP4.2 計畫 D23）：對話的增量以 <see cref="ClientOutboxRules.ItemKey"/>、<see cref="ClientOutboxRules.HeaderKey"/>、
/// <see cref="ClientOutboxRules.ComposerKey"/> 組成；null＝照 type 的規則（<see cref="ClientOutboxRules.MergeKey(string)"/>，第 3 階段的訊息都是 null）
/// </param>
/// <param name="ConversationId">這則訊息屬於哪個對話（<c>conversation</c> 屏障清掉同一個對話排著的增量時看它）；和對話無關的是 null</param>
internal sealed record ClientOutboxMessage(string Type, string Json, string? Key = null, string? ConversationId = null)
{
    public int Bytes { get; } = Encoding.UTF8.GetByteCount(Json);

    /// <summary>
    /// 即時更新的版本（<c>rv</c>，疊加層那一格的 <see cref="LiveSlot.Age"/>；live2，docs/plans/2026-10-23-live-delta-and-replica-live.md 3.4）：
    /// 疊加層的 <c>item</c> 與 <c>itemPatch</c> 才有，持久的 <c>item</c>／<c>removeItem</c> 是 null
    /// </summary>
    public long? Rv { get; init; }

    /// <summary><c>itemPatch</c> 的項目鍵（<see cref="ClientOutboxRules.ItemKey"/>；增量不合併，<see cref="Key"/> 是 null）</summary>
    public string? PatchKey { get; init; }
}

/// <summary>送出佇列的合併規則（設計 4.6、14.6；WP3 計畫 D10；WP4.2 計畫 D23）</summary>
internal static class ClientOutboxRules
{
    public const string InitType = "init";

    /// <summary>open 的回應（整份對話的最後一頁）：對話層的屏障（<see cref="IsConversationBarrier"/>）</summary>
    public const string ConversationType = "conversation";

    /// <summary>對話清單（整份取代，以 type 合併）</summary>
    public const string ConversationsType = "conversations";

    public const string ItemType = "item";

    /// <summary>疊加層的項目增量（宣告了 <c>patch1</c> 的連線；不合併、接在網頁收到的上一則即時版本後面）</summary>
    public const string ItemPatchType = "itemPatch";

    public const string RemoveItemType = "removeItem";

    public const string HeaderType = "header";

    public const string ComposerType = "composer";

    /// <summary>給看不到那個對話的連線的通知（通知類，web-protocol 第六節第 8 點）</summary>
    public const string NotifyType = "notify";

    /// <summary>語音通知（通知類，給每一條連線，看不看得到那個對話都送；網頁依自己的開關決定念不念）</summary>
    public const string SpeakType = "speak";

    /// <summary>copyItem 的回應（通知類，只給發起的連線）</summary>
    public const string ClipboardType = "clipboard";

    /// <summary>
    /// 以 type 合併的種類：整份取代的 accounts、devices、settings、cli 與 WP4.2 的 conversations。同鍵的新訊息取代佇列裡舊的那則、位置不變。
    /// init（另當屏障：清掉之前排著的快照類，見 <see cref="KeptAcrossInit"/>）、toast 與其他種類不合併，回 null
    /// </summary>
    public static string? MergeKey(string type) => type switch
    {
        "accounts" or "devices" or "settings" or "cli" or ConversationsType => type,
        _ => null,
    };

    /// <summary>一則訊息的合併鍵：帶了 <see cref="ClientOutboxMessage.Key"/> 就用它，否則照 type 的規則（<see cref="MergeKey(string)"/>）</summary>
    public static string? MergeKey(ClientOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Key ?? MergeKey(message.Type);
    }

    /// <summary>
    /// 一筆項目的合併鍵：<c>item</c> 與 <c>removeItem</c> <b>共用</b>（後到的整筆取代：移除之後又送回來的、送了又移除的，佇列裡只留最後一則）。
    /// 對話 id 是固定 32 字的十六進位，所以以冒號串接不會和別的鍵撞在一起
    /// </summary>
    public static string ItemKey(string conversationId, string itemId) => "item:" + conversationId + ":" + itemId;

    public static string HeaderKey(string conversationId) => "header:" + conversationId;

    public static string ComposerKey(string conversationId) => "composer:" + conversationId;

    /// <summary>
    /// init 屏障要不要保留這種排著的訊息（保留的依原順序排到 init 後面）：只有不是快照一部分的通知——toast（對使用者指令的回應，清掉等於靜默遺失）、
    /// WP4.2 的 notify 與 clipboard、語音通知的 speak、WP4.5 的遠端指令結果與輸出（<c>commandResult</c>、<c>sendRejected</c>、<c>accountLogin</c>：
    /// 每個指令至多一則，清掉就再也不會來）、WP4.7 的 fileRef（D44）。快照的各部分（init、整份取代的種類、對話的快照與增量、resync）、
    /// <c>control</c>（init 之後閘道會再送一則）與不認得的種類一律清掉：留下來會疊在新的快照上（init 之後網頁重新 open 畫面上的分頁）。
    /// 新增的通知類要明確列在這裡。
    /// 溢出時整個佇列清空，通知類也一起掉（同第 3 階段的 toast）
    /// </summary>
    public static bool KeptAcrossInit(string type) => type switch
    {
        "toast" or NotifyType or SpeakType or ClipboardType or "fileRef" => true,
        CommandMessageRules.CommandResultType or CommandMessageRules.SendRejectedType or MyriagentHub.Sync.CommandOutputRules.AccountLogin => true,
        _ => false,
    };

    /// <summary>
    /// 對話層的屏障（設計 4.6；D23）：<c>conversation</c> 是那個對話的完整快照，入列前清掉排在它前面、同一個對話的增量
    /// （<see cref="IsConversationIncrement"/>）。其他種類（olderItems、conversationClosed、resync、通知類）不清
    /// </summary>
    public static bool IsConversationBarrier(string type) => type == ConversationType;

    /// <summary>被 <c>conversation</c> 屏障清掉的對話增量：item、itemPatch、removeItem、header、composer</summary>
    public static bool IsConversationIncrement(string type) => type is ItemType or ItemPatchType or RemoveItemType or HeaderType or ComposerType;

    /// <summary>這則訊息改的是哪一筆項目的即時版本（<see cref="ItemKey"/>）：item／removeItem 的合併鍵、itemPatch 的 <see cref="ClientOutboxMessage.PatchKey"/>；其他回 null</summary>
    public static string? RvKey(ClientOutboxMessage message) => message.Type switch
    {
        ItemType or RemoveItemType => message.Key,
        ItemPatchType => message.PatchKey,
        _ => null,
    };
}

/// <summary>
/// 一條瀏覽器連線的送出佇列（內部加鎖：閘道迴圈入列、送出幫浦出列）。同鍵的訊息合併（<see cref="ClientOutboxRules.MergeKey(ClientOutboxMessage)"/>：
/// 原地取代、位置不變）、init 是屏障（通知類保留、排到 init 後面）、<c>conversation</c> 是對話層的屏障（清掉同一個對話排著的增量）、
/// 有則數與位元組上限：加入後超過上限而且佇列原本不是空的就清空並回 <see cref="ClientOutboxResult.Overflow"/>
/// （佇列是空的時單則照收，一則很大的訊息不會無限溢出；屏障清掉的不算在「原本」裡）。<see cref="Complete"/> 之後的訊息直接丟掉。
/// </summary>
internal sealed class ClientOutbox(int maxMessages, long maxBytes)
{
    private readonly object gate = new();
    private readonly LinkedList<ClientOutboxMessage> queue = new();

    /// <summary>
    /// 每筆項目最後入列的即時版本（<see cref="ClientOutboxRules.ItemKey"/> → rv）：網頁照入列順序收，所以這就是網頁手上的版本。
    /// 持久的 item／removeItem、對話層的屏障與 resync、init、清空時拿掉（之後先送整筆）
    /// </summary>
    private readonly Dictionary<string, long> liveRv = new(StringComparer.Ordinal);
    private TaskCompletionSource signal = NewSignal();
    private long bytes;
    private bool completed;

    public int Count
    {
        get
        {
            lock (gate)
                return queue.Count;
        }
    }

    public long Bytes
    {
        get
        {
            lock (gate)
                return bytes;
        }
    }

    public ClientOutboxResult Enqueue(ClientOutboxMessage message)
    {
        lock (gate)
        {
            if (completed)
                return ClientOutboxResult.Queued;
            var result = ClientOutboxResult.Queued;
            var wasEmpty = queue.Count == 0;
            if (message.Type == ClientOutboxRules.InitType)
            {
                // 屏障：init 是完整快照，之前排著的快照類都不必送了；toast 這類通知保留、依原順序排到 init 後面——只搬得動還排著的，
                // 已經出列的管不到（第一則 init 之前沒有 toast，靠的是閘道在連線鎖裡標成送過 init 並入列、toast 也在同一把鎖裡檢查再入列）。
                // init 一律排得進去、不算溢出（閘道先標成已送過 init、不看入列的結果，靠的就是這點）
                var kept = new List<ClientOutboxMessage>();
                foreach (var queued in queue)
                {
                    if (ClientOutboxRules.KeptAcrossInit(queued.Type))
                        kept.Add(queued);
                }
                ClearLocked();
                wasEmpty = true;
                Add(message);
                foreach (var queued in kept)
                    Add(queued);
            }
            else if (message.Type == ClientOutboxRules.ItemPatchType)
            {
                // 增量不合併（閘道只在佇列裡沒有同一筆還沒送的訊息時才入列，見 CanPatch）
                Add(message);
            }
            else
            {
                if (ClientOutboxRules.IsConversationBarrier(message.Type) && message.ConversationId is { } conversationId)
                {
                    // 對話層的屏障：排在前面的同一個對話的增量都包含在這份快照裡了（閘道讀快照之前已經取走那些變更）。
                    // 清掉之後佇列是空的就等於從空的佇列開始（單則照收）
                    RemoveIncrementsLocked(conversationId);
                    wasEmpty = queue.Count == 0;
                }
                if ((ClientOutboxRules.IsConversationBarrier(message.Type) || message.Type == ConversationGatewayRules.ResyncType) && message.ConversationId is { } reset)
                    ForgetRvLocked(reset);
                if (ClientOutboxRules.MergeKey(message) is { } key && Find(key) is { } existing)
                {
                    bytes += message.Bytes - existing.Value.Bytes;
                    existing.Value = message;
                    result = ClientOutboxResult.Merged;
                }
                else
                {
                    Add(message);
                }
            }

            if (!wasEmpty && (queue.Count > maxMessages || bytes > maxBytes))
            {
                ClearLocked();
                return ClientOutboxResult.Overflow;
            }
            if (ClientOutboxRules.RvKey(message) is { } rvKey)
            {
                if (message.Rv is { } rv)
                    liveRv[rvKey] = rv;
                else
                    liveRv.Remove(rvKey);
            }
            Pulse();
            return result;
        }
    }

    /// <summary>
    /// 這則現在入列會不會溢出（不改佇列；規則同 <see cref="Enqueue"/>：init 一律排得進去、對話層的屏障先清掉同一個對話的增量、同鍵的原地取代、
    /// 佇列〔屏障清掉之後〕是空的時單則照收）。閘道以它決定 open 的快照這一輪要不要先留著（合併前審查 F2）；判斷之後送出幫浦只會讓佇列變小
    /// </summary>
    public bool WouldOverflow(ClientOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (gate)
        {
            if (completed || message.Type == ClientOutboxRules.InitType)
                return false;
            var count = queue.Count;
            var total = bytes;
            var barrier = ClientOutboxRules.IsConversationBarrier(message.Type) ? message.ConversationId : null;
            var key = ClientOutboxRules.MergeKey(message);
            var merged = false;
            foreach (var queued in queue)
            {
                if (barrier is not null && ClientOutboxRules.IsConversationIncrement(queued.Type)
                    && string.Equals(queued.ConversationId, barrier, StringComparison.Ordinal))
                {
                    count--;
                    total -= queued.Bytes;
                }
                else if (!merged && key is not null && string.Equals(ClientOutboxRules.MergeKey(queued), key, StringComparison.Ordinal))
                {
                    merged = true;
                    total -= queued.Bytes;
                    count--;
                }
            }
            if (count == 0 && !merged)
                return false;
            return count + 1 > maxMessages || total + message.Bytes > maxBytes;
        }
    }

    /// <summary>
    /// 這筆項目現在可以送接在 <paramref name="baseRv"/> 後面的增量：最後入列的即時版本就是它，而且佇列裡沒有同一筆還沒送的訊息
    /// （有的話改送整筆、以同一個鍵合併；增量從不和排著的訊息合併，網頁收到的增量一定接在它收到的上一則後面）
    /// </summary>
    public bool CanPatch(string itemKey, long baseRv)
    {
        ArgumentNullException.ThrowIfNull(itemKey);
        lock (gate)
        {
            if (completed || !liveRv.TryGetValue(itemKey, out var rv) || rv != baseRv)
                return false;
            foreach (var queued in queue)
            {
                if (string.Equals(ClientOutboxRules.RvKey(queued), itemKey, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
    }

    public bool TryDequeue([MaybeNullWhen(false)] out ClientOutboxMessage message)
    {
        lock (gate)
        {
            if (queue.First is not { } first)
            {
                message = null;
                return false;
            }
            queue.RemoveFirst();
            bytes -= first.Value.Bytes;
            message = first.Value;
            return true;
        }
    }

    /// <summary>有訊息時回 true；<see cref="Complete"/> 之後而且空了回 false</summary>
    public async ValueTask<bool> WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (queue.Count > 0)
                    return true;
                if (completed)
                    return false;
                wait = signal.Task;
            }
            await wait.WaitAsync(cancellationToken);
        }
    }

    public void Clear()
    {
        lock (gate)
            ClearLocked();
    }

    /// <summary>不再收新訊息；佇列裡已有的照樣出列，空了之後 <see cref="WaitAsync"/> 回 false</summary>
    public void Complete()
    {
        lock (gate)
        {
            completed = true;
            Pulse();
        }
    }

    /// <summary>
    /// 送出幫浦：依序送出；單則超過 <paramref name="timeout"/> 沒送完或丟例外就呼叫 <paramref name="abort"/>（"send_timeout"／"send_failed"）、
    /// 停收並結束——一條卡住的連線不拖累其他連線。<see cref="Complete"/> 之後送完已排的就結束；<paramref name="cancellationToken"/> 取消時直接結束。
    /// 不丟例外（最外層攔截，不讓背景工作的錯誤停掉伺服器）
    /// </summary>
    public static async Task PumpAsync(ClientOutbox outbox, Func<string, CancellationToken, Task> send, TimeSpan timeout,
        Action<string> abort, CancellationToken cancellationToken)
    {
        string? reason = null;
        try
        {
            while (reason is null && await outbox.WaitAsync(cancellationToken))
            {
                while (reason is null && outbox.TryDequeue(out var message))
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(timeout);
                    try
                    {
                        await send(message.Json, deadline.Token).WaitAsync(timeout, cancellationToken);
                    }
                    catch (TimeoutException)
                    {
                        reason = "send_timeout";
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        reason = "send_timeout";
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        reason = "send_failed";
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (reason is null)
            return;
        outbox.Complete();
        outbox.Clear();
        try
        {
            abort(reason);
        }
        catch (Exception)
        {
            // 中斷連線本身失敗（連線已經不在）沒有別的事可做
        }
    }

    private void Add(ClientOutboxMessage message)
    {
        queue.AddLast(message);
        bytes += message.Bytes;
    }

    private LinkedListNode<ClientOutboxMessage>? Find(string key)
    {
        for (var node = queue.First; node is not null; node = node.Next)
        {
            if (string.Equals(ClientOutboxRules.MergeKey(node.Value), key, StringComparison.Ordinal))
                return node;
        }
        return null;
    }

    /// <summary>清掉排著的、同一個對話的增量（<see cref="ClientOutboxRules.IsConversationIncrement"/>）；在鎖內呼叫</summary>
    private void RemoveIncrementsLocked(string conversationId)
    {
        for (var node = queue.First; node is not null;)
        {
            var next = node.Next;
            if (ClientOutboxRules.IsConversationIncrement(node.Value.Type)
                && string.Equals(node.Value.ConversationId, conversationId, StringComparison.Ordinal))
            {
                bytes -= node.Value.Bytes;
                queue.Remove(node);
            }
            node = next;
        }
    }

    private void ClearLocked()
    {
        queue.Clear();
        bytes = 0;
        liveRv.Clear();
    }

    private void ForgetRvLocked(string conversationId)
    {
        if (liveRv.Count == 0)
            return;
        var prefix = ClientOutboxRules.ItemKey(conversationId, string.Empty);
        foreach (var key in liveRv.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            liveRv.Remove(key);
    }

    /// <summary>喚醒等待者（在鎖內呼叫；續體非同步執行，不會在鎖內跑到別人的程式）</summary>
    private void Pulse()
    {
        var current = signal;
        signal = NewSignal();
        current.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
