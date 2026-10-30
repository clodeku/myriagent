namespace Myriagent.Sync;

// 各帳戶的 token 計數（tok1；docs/sync-protocol.md 14.9）：每台電腦把 app 經手的對話用掉的 token 依「邏輯帳戶 × 這台的本機日期」上傳，
// 伺服器每台每格存一列（sync.db 的 token_tallies），瀏覽器版的帳戶卡顯示所有電腦的合計；桌面另外收其他電腦的合計（tok2，14.10）加上這台的。
// 只供顯示：不寫持久事件，不影響任何判斷

/// <summary>
/// 一格：這台在 <c>day</c>（這台的本機日期 yyyy-MM-dd）以 <c>logicalAccountId</c> 用掉的 token。慣例同 web-protocol 的 TokenCount：
/// <c>input</c> 含快取，<c>cached</c> 是其中的快取讀取，總數＝<c>input</c>＋<c>output</c>
/// </summary>
internal sealed record TokenTallyRow(string LogicalAccountId, string Day, long Input, long Cached, long Output);

/// <summary>
/// 裝置 → 伺服器（Invoke，<c>tok1</c>）：<c>full</c>＝這是這台目前的全部（伺服器先刪掉這台的所有格再寫入）；否則只有新的與變了的格（逐格取代）。
/// 沒有回傳內容；格式不合格回 <c>invalid</c>
/// </summary>
internal sealed record TokensPublish(bool Full, IReadOnlyList<TokenTallyRow> Rows);

/// <summary>
/// 伺服器 → 裝置（Send，<c>tok2</c>；docs/sync-protocol.md 14.10）：收件這台以外、沒撤銷的電腦在帳戶目錄裡的帳戶（沒刪除的）每一格的合計
/// （同一個邏輯帳戶、同一天的各台相加；<c>day</c> 是送出那台的本機日期），保留天數內、整份取代。桌面加上這台自己的計數，帳戶卡顯示所有電腦的合計
/// </summary>
internal sealed record TokensUpdate(IReadOnlyList<TokenTallyRow> Rows);

/// <summary>桌面本機的一格（本機帳戶 × 本機日期；<see cref="TokenTallySyncRules.ToRows"/> 依目錄的連結換成 <see cref="TokenTallyRow"/>）</summary>
internal sealed record LocalTokenTally(string LocalAccountId, string Day, long Input, long Cached, long Output);
