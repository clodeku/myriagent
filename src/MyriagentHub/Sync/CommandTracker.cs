using System.Diagnostics.CodeAnalysis;

namespace MyriagentHub.Sync;

/// <summary>
/// 記憶體裡追蹤的一個已送出的指令（WP4.5 計畫 D16、D20～D23）：結果與輸出要轉給哪條瀏覽器連線、以哪個 commandId 回、
/// 輸出要核對的對話 id 與 requestId、帳戶 id 要換回什麼、被拒時要不要還原文字
/// </summary>
/// <param name="CommandId">伺服器的 cid（信封的 <c>cid</c>）</param>
/// <param name="BrowserConnectionId">發起的瀏覽器連線</param>
/// <param name="BrowserCommandId">瀏覽器送 Cmd 時帶的 commandId（<c>commandResult.commandId</c>）</param>
/// <param name="CmdName">指令名稱</param>
/// <param name="TargetDeviceId">目標電腦（只收這台的結果與輸出）</param>
/// <param name="ExpectedConversationId"><c>sendRejected.conversationId</c> 要等於的線上 id（<see cref="OutputContext.ExpectedConversationId"/>）</param>
/// <param name="BrowserConversationId">瀏覽器看到的對話 id（草稿的第一次 send 是草稿 id）</param>
/// <param name="RequestId">指令帶的 <c>requestId</c></param>
/// <param name="BrowserAccountId"><c>accountLogin.accountId</c> 要換回的 id</param>
/// <param name="RestoreText">被拒時要還回輸入框的文字（<c>send</c> 的 text；其他是 null）</param>
/// <param name="FanOut">所有電腦登出的一列（最終結果不送 <c>commandResult</c>，改以各台的 toast 告知，D26）</param>
/// <param name="DeviceName">目標電腦的名稱（toast 用）</param>
/// <param name="DeadlineAt">結果期限（列的 <c>expires_at</c>）</param>
/// <param name="DraftSessionId">
/// 草稿的第一次 send（<c>conversationCreate</c>）：草稿所屬的工作階段（<see cref="BrowserConversationId"/> 是草稿 id）；rejected 時清掉草稿記下的
/// <c>SentCommandId</c>（步 9、D33）。其他是 null
/// </param>
/// <param name="OriginDeviceId">
/// 桌面遠端操作（sync-protocol 14.7）：發起的電腦；結果與輸出以 <c>PeerReply</c> 送回它（<see cref="BrowserConnectionId"/> 是發起時的裝置連線，
/// <see cref="BrowserCommandId"/> 是桌面的 commandId）。瀏覽器發起的是 null
/// </param>
internal sealed record TrackedCommand(string CommandId, string BrowserConnectionId, string BrowserCommandId, string CmdName,
    string TargetDeviceId, string? ExpectedConversationId, string? BrowserConversationId, string? RequestId, string? BrowserAccountId,
    string? RestoreText, bool FanOut, string? DeviceName, long DeadlineAt, string? DraftSessionId = null, string? OriginDeviceId = null);

/// <summary>
/// 已送出的指令的記憶體追蹤（WP4.5 計畫 D16、D20）：<c>CommandRouter</c> 在列提交之後登記，收結果與輸出的一方（步 7）以 cid 找。
/// 最多 <see cref="MaxEntries"/> 筆，滿了先移除最舊的（它的結果只寫進列，不轉送）；伺服器重啟後是空的（重啟前送出的指令的輸出直接丟掉）。
/// 結束後 <see cref="CommandStateRules.OutputGraceMs"/> 內還收輸出；轉送過的 <c>sendRejected</c>／<c>toast</c> 記下來，同一個 cid 不再另送同一種（D22、D23）。
/// 任何執行緒都能呼叫（一把鎖）
/// </summary>
internal sealed class CommandTracker
{
    public const int MaxEntries = 4096;

    private readonly object gate = new();

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>登記的先後（最舊的在前；被移除的留在佇列裡，取出時略過）</summary>
    private readonly Queue<(string CommandId, long Sequence)> order = new();

    private long sequence;

    /// <summary>目前追蹤的筆數</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    /// <summary>登記；同一個 cid 已經在的話取代。滿了先移除最舊的</summary>
    public void Add(TrackedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (gate)
        {
            var entry = new Entry(command, ++sequence);
            entries[command.CommandId] = entry;
            order.Enqueue((command.CommandId, entry.Sequence));
            while (entries.Count > MaxEntries && order.TryDequeue(out var oldest))
            {
                if (entries.TryGetValue(oldest.CommandId, out var existing) && existing.Sequence == oldest.Sequence)
                    entries.Remove(oldest.CommandId);
            }
            // 被取代或移除的留下的佇列項目：佇列比字典大很多時整理一次（不讓它無限長）
            if (order.Count > MaxEntries * 2)
                Compact();
        }
    }

    public bool TryGet(string commandId, [MaybeNullWhen(false)] out TrackedCommand command)
    {
        lock (gate)
        {
            if (entries.TryGetValue(commandId, out var entry))
            {
                command = entry.Command;
                return true;
            }
        }
        command = null;
        return false;
    }

    /// <summary>結束的時間（null＝還沒結束）</summary>
    public long? CompletedAt(string commandId)
    {
        lock (gate)
            return entries.TryGetValue(commandId, out var entry) ? entry.CompletedAt : null;
    }

    /// <summary>標成已結束（第一次的時間為準）：之後 <see cref="CommandStateRules.OutputGraceMs"/> 內還收輸出</summary>
    public void MarkCompleted(string commandId, long now)
    {
        lock (gate)
        {
            if (entries.TryGetValue(commandId, out var entry))
                entry.CompletedAt ??= now;
        }
    }

    /// <summary>轉送過這一種輸出（D20：<c>sendRejected</c>、<c>toast</c>）</summary>
    public void MarkForwarded(string commandId, string messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        lock (gate)
        {
            if (entries.TryGetValue(commandId, out var entry))
                entry.Forwarded.Add(messageType);
        }
    }

    /// <summary>這個 cid 轉送過這一種輸出嗎（D22、D23：轉送過就不再另送同一種）</summary>
    public bool WasForwarded(string commandId, string messageType)
    {
        lock (gate)
            return entries.TryGetValue(commandId, out var entry) && entry.Forwarded.Contains(messageType);
    }

    /// <summary>移除結束超過 <see cref="CommandStateRules.OutputGraceMs"/>、或期限過了 <see cref="CommandStateRules.OutputGraceMs"/> 的</summary>
    public void Prune(long now)
    {
        lock (gate)
        {
            var expired = entries.Values
                .Where(e => (e.CompletedAt is { } done && now - done > CommandStateRules.OutputGraceMs)
                            || now - e.Command.DeadlineAt > CommandStateRules.OutputGraceMs)
                .Select(e => e.Command.CommandId)
                .ToList();
            foreach (var commandId in expired)
                entries.Remove(commandId);
            if (expired.Count > 0)
                Compact();
        }
    }

    /// <summary>佇列只留字典裡還在的項目（鎖裡呼叫）</summary>
    private void Compact()
    {
        var live = order.Where(o => entries.TryGetValue(o.CommandId, out var e) && e.Sequence == o.Sequence).ToList();
        order.Clear();
        foreach (var item in live)
            order.Enqueue(item);
    }

    private sealed class Entry(TrackedCommand command, long sequence)
    {
        public TrackedCommand Command { get; } = command;

        public long Sequence { get; } = sequence;

        public long? CompletedAt { get; set; }

        public HashSet<string> Forwarded { get; } = new(StringComparer.Ordinal);
    }
}
