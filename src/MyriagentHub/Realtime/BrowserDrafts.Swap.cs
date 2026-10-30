using System.Diagnostics.CodeAnalysis;

namespace MyriagentHub.Realtime;

/// <summary>
/// 草稿換成正式對話（WP4.6 計畫 D41）：拿走 <see cref="BrowserDraft.PendingConversationId"/> 已出現在伺服器的草稿。
/// 和其他方法用同一把鎖；拿走之後草稿就不在了，但留一筆墓碑（合併前審查 LIVE-1）：換的那一則可能排進已經斷掉、還沒移除的連線，
/// 或那時這個工作階段沒有任何連線，網頁重連後 open 那個草稿 id 時以 <see cref="TryGetReplaced"/> 改回 <c>replacedBy</c>
/// </summary>
internal sealed partial class BrowserDrafts
{
    /// <summary>換草稿的墓碑：工作階段 → (草稿 id → 換成的對話)；和草稿分開存（工作階段的草稿全丟了也還在）</summary>
    private readonly Dictionary<string, Dictionary<string, ReplacedDraft>> replaced = new(StringComparer.Ordinal);

    /// <summary><see cref="BrowserDraft.PendingConversationId"/> 等於 <paramref name="conversationId"/> 的草稿：在鎖裡移除並回傳（工作階段、草稿 id），各留一筆墓碑</summary>
    public IReadOnlyList<(string SessionId, string DraftId)> TakeCreated(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        lock (gate)
        {
            Sweep();
            List<(string SessionId, string DraftId)>? taken = null;
            foreach (var (sessionId, session) in sessions)
            {
                foreach (var draft in session.Drafts.Values)
                {
                    if (string.Equals(draft.PendingConversationId, conversationId, StringComparison.Ordinal))
                        (taken ??= []).Add((sessionId, draft.Id));
                }
            }
            if (taken is null)
                return [];
            foreach (var (sessionId, draftId) in taken)
            {
                RemoveLocked(sessionId, draftId);
                TombstoneLocked(sessionId, draftId, conversationId);
            }
            return taken;
        }
    }

    /// <summary>
    /// 整份重讀時（通知溢出，變更可能被丟掉）：鎖外以 <paramref name="exists"/> 檢查所有記著 Pending 的草稿，鎖內再確認還是同一個 Pending 才移除（留墓碑）。
    /// 回傳（工作階段、草稿 id、新對話 id）
    /// </summary>
    public IReadOnlyList<(string SessionId, string DraftId, string ConversationId)> TakePending(Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        List<(string SessionId, string DraftId, string ConversationId)> pending = [];
        lock (gate)
        {
            Sweep();
            foreach (var (sessionId, session) in sessions)
            {
                foreach (var draft in session.Drafts.Values)
                {
                    if (draft.PendingConversationId is { } conversationId)
                        pending.Add((sessionId, draft.Id, conversationId));
                }
            }
        }
        if (pending.Count == 0)
            return [];
        // 查目錄在鎖外（exists 可能慢，也不可在鎖裡呼叫外面的東西）
        var found = pending.Where(p => exists(p.ConversationId)).ToList();
        if (found.Count == 0)
            return [];
        List<(string SessionId, string DraftId, string ConversationId)>? taken = null;
        lock (gate)
        {
            foreach (var candidate in found)
            {
                // 這段時間草稿可能被關掉、被拒絕清掉 Pending、或換成另一次送出：還是同一個 Pending 才拿
                if (sessions.TryGetValue(candidate.SessionId, out var session) && session.Drafts.TryGetValue(candidate.DraftId, out var draft)
                    && string.Equals(draft.PendingConversationId, candidate.ConversationId, StringComparison.Ordinal))
                {
                    RemoveLocked(candidate.SessionId, candidate.DraftId);
                    TombstoneLocked(candidate.SessionId, candidate.DraftId, candidate.ConversationId);
                    (taken ??= []).Add(candidate);
                }
            }
        }
        return taken ?? [];
    }

    public bool TryGetReplaced(string sessionId, string draftId, [NotNullWhen(true)] out string? conversationId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(draftId);
        lock (gate)
        {
            Sweep();
            if (replaced.TryGetValue(sessionId, out var map) && map.TryGetValue(draftId, out var found))
            {
                conversationId = found.ConversationId;
                return true;
            }
        }
        conversationId = null;
        return false;
    }

    /// <summary>所有工作階段的換草稿墓碑數（過期的先清掉；測試用）</summary>
    public int ReplacedCount
    {
        get
        {
            lock (gate)
            {
                Sweep();
                return replaced.Values.Sum(m => m.Count);
            }
        }
    }

    /// <summary>在鎖裡移除一份草稿；工作階段沒有草稿了就整個拿掉（同 <see cref="Remove"/>）</summary>
    private void RemoveLocked(string sessionId, string draftId)
    {
        if (!sessions.TryGetValue(sessionId, out var session) || !session.Drafts.Remove(draftId))
            return;
        if (session.Drafts.Count == 0)
            sessions.Remove(sessionId);
    }

    /// <summary>在鎖裡記一筆墓碑；這個工作階段超過 <see cref="BrowserDraftRules.MaxReplacedPerSession"/> 筆時丟最舊的</summary>
    private void TombstoneLocked(string sessionId, string draftId, string conversationId)
    {
        if (!replaced.TryGetValue(sessionId, out var map))
            replaced[sessionId] = map = new Dictionary<string, ReplacedDraft>(StringComparer.Ordinal);
        map[draftId] = new ReplacedDraft(conversationId, time.GetTimestamp(), ++used);
        var max = BrowserDraftRules.MaxReplacedPerSession(options.Value.Conversations.MaxDraftsPerSession);
        while (map.Count > max)
            map.Remove(map.MinBy(p => p.Value.Order).Key);
    }

    /// <summary>丟掉過期的墓碑（在鎖裡，<see cref="Sweep"/> 呼叫）</summary>
    private void SweepReplaced(long now, int graceSeconds)
    {
        if (replaced.Count == 0)
            return;
        var keep = BrowserDraftRules.ReplacedKeepSeconds(graceSeconds);
        List<string>? emptied = null;
        foreach (var (sessionId, map) in replaced)
        {
            List<string>? expired = null;
            foreach (var (draftId, entry) in map)
            {
                if (BrowserDraftRules.GraceOver(time.GetElapsedTime(entry.At, now), keep))
                    (expired ??= []).Add(draftId);
            }
            if (expired is null)
                continue;
            foreach (var draftId in expired)
                map.Remove(draftId);
            if (map.Count == 0)
                (emptied ??= []).Add(sessionId);
        }
        if (emptied is null)
            return;
        foreach (var sessionId in emptied)
            replaced.Remove(sessionId);
    }

    /// <summary>一筆墓碑：換成的對話、換掉的時間（單調時鐘）、先後（超過上限時丟最小的）</summary>
    private sealed record ReplacedDraft(string ConversationId, long At, long Order);
}
