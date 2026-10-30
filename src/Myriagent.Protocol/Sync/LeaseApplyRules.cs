namespace Myriagent.Sync;

/// <summary>這台和一個對話的關係（<see cref="LeaseApplyRules.Decide"/> 的輸入）</summary>
internal enum LeaseLocalRole
{
    /// <summary>這台不認得這個對話（沒有本機資料夾、也沒有複本）</summary>
    Unknown,

    /// <summary>本機擁有（本機的對話資料夾在、不是複本）</summary>
    Owned,

    /// <summary>其他電腦的對話（<c>replica_meta</c>）</summary>
    Replica,
}

/// <summary>電腦拉到 <c>conv.lease</c> 之後要做的事（計畫 5.2 的表）</summary>
internal enum LeaseApplyAction
{
    /// <summary>不理（作者不是伺服器、比已知的舊、已經是這個擁有者與 epoch）</summary>
    Ignore,

    /// <summary>新擁有者是這台：匯入（7.3，WP5.7）</summary>
    Import,

    /// <summary>這台交出去的（<c>handoffs_local</c> 有同一個 handoffId 的 source）：交出完成、變成複本（7.2 的 A7，WP5.6）</summary>
    BecomeReplica,

    /// <summary>這台擁有、卻被換給別台（強制接手，或這台不知道的交接）：圍欄（7.4，WP5.8）</summary>
    Fence,

    /// <summary>複本：更新 <c>replica_meta</c> 的擁有者與 epoch，之後只收新擁有者、epoch ≥ 新值的事件</summary>
    UpdateReplica,

    /// <summary>不認得的對話：照第 4 階段（只影響對話清單）</summary>
    ListOnly,
}

/// <summary>
/// <see cref="LeaseApplyRules.Decide"/> 的輸入：這台的 deviceId、事件的作者（要是 <see cref="SyncAuthors.Server"/>）、這台和對話的關係、
/// 這台已知的 epoch（本機擁有時是 <c>LeaseEpoch ?? 1</c>，複本是 <c>replica_meta.epoch</c>；不認得是 null）、<c>handoffs_local</c> 有沒有同一個 handoffId 的 source
/// </summary>
internal sealed record LeaseApplyContext(string SelfDeviceId, string? Author, LeaseLocalRole Role, long? KnownEpoch, bool SourceHandoffMatches);

/// <summary>
/// 電腦端套用 <c>conv.lease</c> 的純函式（第 5 階段計畫 5.2）：伺服器是擁有者換手唯一的來源（D2），拉到之後依這台的角色決定動作。
/// 作者不是伺服器的一律不理（裝置送的 conv.lease 伺服器本來就拒收，這裡再擋一次）；epoch 沒有比這台已知的新也不理（重送、重拉）
/// </summary>
internal static class LeaseApplyRules
{
    /// <summary>
    /// 依序：作者不是伺服器 → Ignore；epoch 不比這台已知的新 → Ignore；新擁有者是這台 → Import；本機擁有、新擁有者不是這台：
    /// 有同一個 handoffId 的 source → BecomeReplica，否則 → Fence；複本 → UpdateReplica；不認得 → ListOnly
    /// </summary>
    public static LeaseApplyAction Decide(LeaseApplyContext context, ConversationLeasePayload lease)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lease);
        if (!string.Equals(context.Author, SyncAuthors.Server, StringComparison.Ordinal))
            return LeaseApplyAction.Ignore;
        if (context.KnownEpoch is { } known && lease.Epoch <= known)
            return LeaseApplyAction.Ignore;
        if (string.Equals(lease.Owner, context.SelfDeviceId, StringComparison.Ordinal))
            return LeaseApplyAction.Import;
        return context.Role switch
        {
            LeaseLocalRole.Owned => context.SourceHandoffMatches ? LeaseApplyAction.BecomeReplica : LeaseApplyAction.Fence,
            LeaseLocalRole.Replica => LeaseApplyAction.UpdateReplica,
            LeaseLocalRole.Unknown => LeaseApplyAction.ListOnly,
            _ => throw new ArgumentOutOfRangeException(nameof(context), context.Role, "不認得的本機角色"),
        };
    }
}
