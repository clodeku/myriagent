namespace Myriagent.Sync;

/// <summary>對話事件的 BatchAck 結果要怎麼處理（WP4.3 計畫 D14、6.4 的拒絕碼表）</summary>
internal enum RejectAction
{
    /// <summary>當成伺服器收下（確認；<c>conv.created</c> 記 <c>created_acked</c>、<c>conv.deleted</c> 清 <c>needs_redo</c>）</summary>
    Accepted,

    /// <summary>封鎖這個對話（之後不再寫它的事件）</summary>
    Block,

    /// <summary>封鎖，並併進「這些對話不能同步」的一則彙總提示（<c>not_owner</c>，以及補送 <c>conv.created</c> 三次仍被擋）</summary>
    BlockNotOwner,

    /// <summary>經 UI 把對話改成 paused、加一則提示（<c>dlp</c> 是保留碼，伺服器不回；防禦性處理）</summary>
    PauseDlp,

    /// <summary>記成要重整（沒有 conv1 的連線：舊伺服器擋下的事件，下一次 conv1 連線再補）</summary>
    Redo,

    /// <summary>伺服器還不認得這個對話：清被拒那筆的摘要、記 <c>needs_redo</c>，隔 <see cref="RejectDecision.RetryAfterMs"/> 補一筆新的 <c>conv.created</c></summary>
    RetryCreated,

    /// <summary>程式錯誤：同一版本必然再錯，封鎖到 app 版本或伺服器實例改變為止</summary>
    Bug,
}

/// <param name="RetryAfterMs">只有 <see cref="RejectAction.RetryCreated"/> 有值：多久之後補送 <c>conv.created</c></param>
internal readonly record struct RejectDecision(RejectAction Action, long? RetryAfterMs);

/// <summary>
/// 對話事件被伺服器擋下時的處置（6.4；docs/sync-protocol.md 14.2）。被拒原因只有兩組：<see cref="SyncErrorCodes"/> 的
/// <c>invalid</c>、<c>too_large</c>，以及 <see cref="ConversationRejectCodes"/> 的四個碼；其他（含 <c>conflict</c>、<c>unknown_conversation</c>）都是程式錯誤。
/// <c>BatchEventResult</c> 的 <c>Epoch</c>、<c>Owner</c> 只記錄，不拿來判斷
/// </summary>
internal static class ConversationRejectRules
{
    /// <summary><c>created_acked=0</c> 的 <c>invalid</c> 最多補送幾次 <c>conv.created</c>（第 4 次封鎖）</summary>
    public const int MaxCreatedRetries = 3;

    /// <summary>補送 <c>conv.created</c> 的間隔：第 1、2、3 次分別隔 1、10、60 分鐘</summary>
    public static IReadOnlyList<long> CreatedRetryDelaysMs { get; } = [60_000, 600_000, 3_600_000];

    /// <param name="code">被拒原因；null＝接受</param>
    /// <param name="kind">事件種類（<see cref="ConversationEventKinds"/>；<c>conv.deleted</c> 有自己的成功判定）</param>
    /// <param name="conv1">送出這筆的連線協商出 <c>conv1</c></param>
    /// <param name="createdAcked">這個對話的 <c>conv.created</c> 已被這個伺服器確認過（換實例、還原時重設）</param>
    /// <param name="createdRetries">已經補送過幾次 <c>conv.created</c></param>
    public static RejectDecision Classify(string? code, string kind, bool conv1, bool createdAcked, int createdRetries)
    {
        if (code is null)
            return Of(RejectAction.Accepted);
        var cls = ConversationEventKinds.ClassOf(kind);
        if (cls is null)
            return Of(RejectAction.Bug);
        var deleted = string.Equals(kind, ConversationEventKinds.Deleted, StringComparison.Ordinal);
        switch (code)
        {
            case ConversationRejectCodes.NotOwner:
                return Of(RejectAction.BlockNotOwner);
            case ConversationRejectCodes.Fenced:
                return Of(RejectAction.Block);
            case ConversationRejectCodes.Deleted:
                return Of(deleted ? RejectAction.Accepted : RejectAction.Block);
            case ConversationRejectCodes.Dlp:
                return Of(cls == SyncClasses.Item ? RejectAction.PauseDlp : RejectAction.Block);
            case SyncErrorCodes.Invalid:
                // 1. 沒有 conv1（舊伺服器、或這台沒宣告）：一律記重整，conv.deleted 也是（回滾期間的刪除不能當成完成）
                if (!conv1)
                    return Of(RejectAction.Redo);
                // 2. 伺服器不認得的對話的刪除：本來就不在伺服器上
                if (deleted)
                    return Of(RejectAction.Accepted);
                // 3. 未知對話的非建立事件一律 invalid（6.3 的 C7）：補送 created，第 4 次封鎖
                if (!createdAcked)
                {
                    var tries = Math.Max(0, createdRetries);
                    return tries < MaxCreatedRetries
                        ? new RejectDecision(RejectAction.RetryCreated, CreatedRetryDelaysMs[tries])
                        : Of(RejectAction.BlockNotOwner);
                }

                // 4. 伺服器認得這個對話還擋下：同一版本必然再錯
                return Of(RejectAction.Bug);
            case SyncErrorCodes.TooLarge:
                return Of(conv1 ? RejectAction.Bug : RejectAction.Redo);
            default:
                return Of(RejectAction.Bug);
        }
    }

    private static RejectDecision Of(RejectAction action) => new(action, null);
}
