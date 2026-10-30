namespace Myriagent.Sync;

/// <summary>
/// 交握完成前收到的第 4 階段伺服器訊息（D49；sync-protocol 14.4 的早到緩衝）：依到達順序留著，交握完成時依序處理
/// （同 12.2 的作法：伺服器身分驗過、Prove 完成之前不呼叫任何 hub 方法）。
/// <list type="bullet">
/// <item>指令（<see cref="CommandDelivery"/>）最多留 <see cref="MaxCommands"/> 則；再來的只記 commandId（最多 <see cref="MaxBusy"/> 個），
/// 交握完成後回 <c>busy</c>（協商出 <c>cmd1</c> 才回；id 不是 ULID 的不回）；超過的連 id 都不記（伺服器等不到結果，照逾時處理）。</item>
/// <item>其他（<see cref="WatchChanged"/>、<see cref="SnapshotRequest"/>、<see cref="MediaRequest"/>、<see cref="PeerReply"/>，第 5 階段的 <see cref="HandoffRequest"/>、
/// <see cref="HandoffUpdate"/>、<see cref="LogRepair"/>）最多留 <see cref="MaxOthers"/> 則，滿了丟最舊的那則（指令不受影響）。交接的訊息丟了不要緊：
/// 伺服器在交握完成（ConnectionProven）時重送進行中的 HandoffRequest（D17），LogRepair 在下一次維護再送。</item>
/// <item>整份取代的即時資料（<see cref="TokensUpdate"/>、<see cref="StatsUpdate"/>）每種只留最新的一則（2026-10-23：伺服器在 ConnectionProven 就送 TokensUpdate，
/// 常常比 ProveResponse 在這裡處理完還早到；0.2.621～0.2.624 不收它、丟例外，每次 app 啟動都斷線重連一次）。</item>
/// </list>
/// 每條連線一個、只在引擎執行緒使用（不加鎖）；連線中斷就隨連線丟掉
/// </summary>
internal sealed class SyncEarlyMessages
{
    public const int MaxCommands = 16;

    public const int MaxOthers = 32;

    public const int MaxBusy = 64;

    private readonly List<object> messages = [];
    private readonly List<string> busy = [];
    private int commands;

    /// <summary>暫存的訊息數（指令與其他）</summary>
    public int Count => messages.Count;

    /// <summary>因為緩衝滿了丟掉的訊息數（其他類丟最舊的、超過 <see cref="MaxBusy"/> 的指令）；只供記錄</summary>
    public int Dropped { get; private set; }

    /// <summary>
    /// 放一則（<see cref="CommandDelivery"/>、<see cref="WatchChanged"/>、<see cref="SnapshotRequest"/>、<see cref="MediaRequest"/>、<see cref="PeerReply"/>、
    /// <see cref="HandoffRequest"/>、<see cref="HandoffUpdate"/>、<see cref="LogRepair"/>、<see cref="TokensUpdate"/>、<see cref="StatsUpdate"/>；null 不收）。
    /// 回傳這則是不是留下了（溢出的指令回 false：它的 commandId 記進 busy 名單）
    /// </summary>
    public bool Add(object message)
    {
        switch (message)
        {
            case null:
                return false;
            case CommandDelivery delivery:
                if (commands < MaxCommands)
                {
                    commands++;
                    messages.Add(delivery);
                    return true;
                }

                if (busy.Count < MaxBusy && SyncIds.IsUlid(delivery.CommandId))
                    busy.Add(delivery.CommandId);
                else
                    Dropped++;
                return false;
            case TokensUpdate or StatsUpdate or SchedulesUpdate or CollaboratorsState:
                // 整份取代：同種的舊的那則已經沒用了
                messages.RemoveAll(m => m.GetType() == message.GetType());
                messages.Add(message);
                return true;
            case WatchChanged or SnapshotRequest or MediaRequest or PeerReply or HandoffRequest or HandoffUpdate or LogRepair:
                if (messages.Count - commands >= MaxOthers)
                {
                    messages.RemoveAt(messages.FindIndex(m => m is not CommandDelivery));
                    Dropped++;
                }

                messages.Add(message);
                return true;
            default:
                throw new ArgumentException($"不是第 4 階段的伺服器訊息：{message.GetType().Name}", nameof(message));
        }
    }

    /// <summary>一次取完（依到達順序）與溢出要回 busy 的指令 id；之後緩衝是空的（<see cref="Dropped"/> 也歸零）</summary>
    public (IReadOnlyList<object> Messages, IReadOnlyList<string> Busy) TakeAll()
    {
        var result = (messages.ToList(), busy.ToList());
        messages.Clear();
        busy.Clear();
        commands = 0;
        Dropped = 0;
        return result;
    }
}
