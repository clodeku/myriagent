using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Realtime;

/// <summary>
/// 伺服器草稿的取用與更新（WP4.2 計畫步 13；D29、6.1）：WP4.5 的草稿指令（setOption、setAccount、setFolder、resetFolder、rename）
/// 與第一次 send 的 conversationCreate 經它。只看得到同一個工作階段的草稿；任何執行緒都能呼叫。
/// </summary>
internal interface IBrowserDrafts
{
    /// <summary>這個工作階段的這份草稿（找到也算「用過」：超過上限時晚一點才被丟）</summary>
    bool TryGet(string sessionId, string draftId, [MaybeNullWhen(false)] out BrowserDraft draft);

    /// <summary>
    /// 以 <paramref name="change"/> 改這份草稿（<c>with</c>；<see cref="BrowserDraft.Id"/> 與 <see cref="BrowserDraft.Used"/> 由這裡維護、改了也不算）。
    /// <paramref name="change"/> 在鎖裡執行：要很快、不可再呼叫這個介面。找不到回 false
    /// </summary>
    bool TryUpdate(string sessionId, string draftId, Func<BrowserDraft, BrowserDraft> change);

    /// <summary>丟掉這份草稿（關分頁、轉成正式對話）；找不到回 false。送不送 <c>conversationClosed</c> 由呼叫端決定</summary>
    bool Remove(string sessionId, string draftId);

    /// <summary>
    /// 這份草稿已經換成正式對話（WP4.6 合併前審查 LIVE-1 的墓碑）：回換成的對話 id。換的那一則可能送到已經斷掉、還沒移除的連線，
    /// 或那時這個工作階段沒有連線；網頁重連後再 open（或對草稿送指令）時改回 <c>replacedBy</c>，不關掉剛送出第一則的分頁
    /// </summary>
    bool TryGetReplaced(string sessionId, string draftId, [NotNullWhen(true)] out string? conversationId);
}

/// <summary>開了一份草稿：新的草稿，與因為超過上限被丟掉的草稿 id（呼叫端送 <c>conversationClosed</c>）</summary>
internal sealed record DraftCreated(BrowserDraft Draft, IReadOnlyList<string> Evicted);

/// <summary>
/// 伺服器草稿（WP4.2 計畫步 13；D29、設計 4.9.2 的伺服器草稿）：只存在記憶體，以瀏覽器的工作階段為鍵，伺服器重開就丟。
/// <list type="bullet">
/// <item>每個工作階段最多 <c>MaxDraftsPerSession</c>（8）份，超過時丟最久沒用的（開、open、改都算用過）。</item>
/// <item>工作階段的最後一條連線移除之後開始計時，<c>DraftGraceSeconds</c>（120 秒）內有連線登記（<see cref="BrowserConnections.Added"/>）就繼續留著，
/// 過了就整批丟掉（之後 open 那個 id 和不存在的對話一樣回 invalid、送 conversationClosed）。過期的在任何一次呼叫、連線登記與移除時順便清掉。</item>
/// <item>草稿不進清單、不進目錄、不參與 WatchChanged 與未讀（id 不是對話 id 的形狀，<see cref="BrowserDraftRules"/>）。</item>
/// <item>換成正式對話的草稿留一筆墓碑（<see cref="TryGetReplaced"/>；和草稿分開存，工作階段的草稿全丟了也還在），
/// <see cref="BrowserDraftRules.ReplacedKeepSeconds"/> 之後或超過 <see cref="BrowserDraftRules.MaxReplacedPerSession"/> 筆時清掉。</item>
/// </list>
/// 狀態以一把鎖保護；連線事件的處理者在鎖裡查 <see cref="BrowserConnections.ActiveSessions"/>（登記表自己的鎖，不會反過來等這把），
/// 所以移除與另一條連線登記同時發生時，不是登記先被看見（不計時），就是計時之後被 Added 取消
/// </summary>
internal sealed partial class BrowserDrafts : IBrowserDrafts, IDisposable
{
    private readonly BrowserConnections connections;
    private readonly IOptions<HubOptions> options;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<string, SessionDrafts> sessions = new(StringComparer.Ordinal);
    private long used;

    public BrowserDrafts(BrowserConnections connections, IOptions<HubOptions> options, TimeProvider time)
    {
        this.connections = connections;
        this.options = options;
        this.time = time;
        connections.Added += OnAdded;
        connections.Removed += OnRemoved;
    }

    /// <summary>所有工作階段的草稿數（過期的先清掉；測試與營運用）</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                Sweep();
                return sessions.Values.Sum(s => s.Drafts.Count);
            }
        }
    }

    /// <summary>
    /// 開一份草稿（newConversation）：帳戶照 <see cref="BrowserDraftRules.PickAccount"/>，一個帳戶都沒有回 null；超過上限時丟最久沒用的，id 在回傳值裡。
    /// <paramref name="deviceId"/> 是 newConversation 選填的電腦（WP4.5 計畫 K8；呼叫端已確認是裝置 id 的形狀）
    /// </summary>
    public DraftCreated? Create(string sessionId, string? requestedAccountId, IReadOnlyList<DraftAccount> accounts, string? deviceId = null)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (BrowserDraftRules.PickAccount(requestedAccountId, accounts) is not { } account)
            return null;
        var max = options.Value.Conversations.MaxDraftsPerSession;
        lock (gate)
        {
            Sweep();
            if (!sessions.TryGetValue(sessionId, out var session))
            {
                sessions[sessionId] = session = new SessionDrafts();
                // 發起的連線剛好在這之前移除（它的 Removed 看不到這個新的工作階段）：照樣計時，不留下沒人會清的草稿
                if (!connections.ActiveSessions().Contains(sessionId, StringComparer.Ordinal))
                    session.EmptySince = time.GetTimestamp();
            }
            string id;
            do
            {
                id = BrowserDraftRules.NewId(time.GetUtcNow().ToUnixTimeMilliseconds());
            }
            while (session.Drafts.ContainsKey(id));
            var draft = new BrowserDraft(id, account.Id, account.Provider) { DeviceId = deviceId, Used = ++used };
            session.Drafts[id] = draft;
            var evicted = new List<string>();
            foreach (var old in BrowserDraftRules.Evict(session.Drafts.Values.ToList(), max))
            {
                session.Drafts.Remove(old.Id);
                evicted.Add(old.Id);
            }
            return new DraftCreated(draft, evicted);
        }
    }

    public bool TryGet(string sessionId, string draftId, [MaybeNullWhen(false)] out BrowserDraft draft)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(draftId);
        lock (gate)
        {
            Sweep();
            if (sessions.TryGetValue(sessionId, out var session) && session.Drafts.TryGetValue(draftId, out var found))
            {
                draft = session.Drafts[draftId] = found with { Used = ++used };
                return true;
            }
        }
        draft = null;
        return false;
    }

    public bool TryUpdate(string sessionId, string draftId, Func<BrowserDraft, BrowserDraft> change)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(draftId);
        ArgumentNullException.ThrowIfNull(change);
        lock (gate)
        {
            Sweep();
            if (!sessions.TryGetValue(sessionId, out var session) || !session.Drafts.TryGetValue(draftId, out var found))
                return false;
            var next = change(found) ?? throw new InvalidOperationException("草稿的修改回傳 null");
            session.Drafts[draftId] = next with { Id = found.Id, Used = ++used };
            return true;
        }
    }

    public bool Remove(string sessionId, string draftId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(draftId);
        lock (gate)
        {
            Sweep();
            if (!sessions.TryGetValue(sessionId, out var session) || !session.Drafts.Remove(draftId))
                return false;
            if (session.Drafts.Count == 0)
                sessions.Remove(sessionId);
            return true;
        }
    }

    /// <summary>這個工作階段目前的連線（草稿被丟時每一條都送 <c>conversationClosed</c>：同一個登入的其他分頁可能開著它）</summary>
    public IReadOnlyList<BrowserConnection> ConnectionsOf(string sessionId) =>
        connections.Snapshot().Where(c => string.Equals(c.SessionId, sessionId, StringComparison.Ordinal)).ToList();

    public void Dispose()
    {
        connections.Added -= OnAdded;
        connections.Removed -= OnRemoved;
    }

    /// <summary>連線登記：過期的先清掉；這個工作階段在寬限內重新連上，就不再計時</summary>
    private void OnAdded(BrowserConnection connection)
    {
        lock (gate)
        {
            Sweep();
            if (sessions.TryGetValue(connection.SessionId, out var session))
                session.EmptySince = null;
        }
    }

    /// <summary>連線移除：這個工作階段沒有連線了就開始計時</summary>
    private void OnRemoved(BrowserConnection connection)
    {
        lock (gate)
        {
            if (sessions.TryGetValue(connection.SessionId, out var session) && !connections.ActiveSessions().Contains(connection.SessionId, StringComparer.Ordinal))
                session.EmptySince = time.GetTimestamp();
            Sweep();
        }
    }

    /// <summary>丟掉寬限已過的工作階段的草稿與過期的換草稿墓碑（在鎖裡）</summary>
    private void Sweep()
    {
        var grace = options.Value.Conversations.DraftGraceSeconds;
        var now = time.GetTimestamp();
        SweepReplaced(now, grace);
        List<string>? expired = null;
        foreach (var (sessionId, session) in sessions)
        {
            if (session.EmptySince is { } since && BrowserDraftRules.GraceOver(time.GetElapsedTime(since, now), grace))
                (expired ??= []).Add(sessionId);
        }
        if (expired is null)
            return;
        foreach (var sessionId in expired)
            sessions.Remove(sessionId);
    }

    private sealed class SessionDrafts
    {
        public Dictionary<string, BrowserDraft> Drafts { get; } = new(StringComparer.Ordinal);

        /// <summary>最後一條連線移除的時間（單調時鐘）；有連線時是 null</summary>
        public long? EmptySince { get; set; }
    }
}
