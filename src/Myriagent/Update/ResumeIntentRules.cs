using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Update;

internal enum ResumeValidity
{
    Ok,

    /// <summary>不是這次換版寫的（一般啟動、上次排空到一半當掉）：列出的對話都不接續、給「繼續」</summary>
    TokenMismatch,

    /// <summary>超過 1 小時（同 CLI 熱更新的 TooOld）</summary>
    TooOld,

    Missing,
}

/// <summary>新版啟動後怎麼處理 resume.json（設計 9.5）</summary>
internal static class ResumeIntentRules
{
    /// <summary>按「繼續」時加的提示（app 更新後沒有自動接續的對話）</summary>
    public const string ManualContinueNotice = "已請代理從中斷處繼續（app 更新後的接續訊息，不是你送的）";

    /// <summary>新版啟動時 resume.json 讀不到（被別的程式開著）：系統匣通知的補充說明。檔案留著，下次啟動以 token 不符給「繼續」</summary>
    public const string UnreadableResumeNotice = "換版前停下的對話這次沒有自動接續（接續檔讀不到），下次開啟 app 時可以按「繼續」";

    /// <summary>token 不符時的說明開頭（上次排空到一半程式就結束了）</summary>
    private const string IncompleteUpdate = "上次 app 更新沒有完成（程式在換版前結束），這個對話被中斷後沒有自動接續";

    private static readonly long MinUnixMilliseconds = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();

    private static readonly long MaxUnixMilliseconds = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>新版接續時加的提示（隱藏送出的那一則）</summary>
    /// <param name="rolledBack">新版啟動失敗、退回原版後接續（--after-update-failed）</param>
    public static string ContinuedNotice(string? appTo, bool rolledBack = false)
        => $"{AppPrefix(appTo, rolledBack)}，已請代理從中斷處繼續";

    /// <summary>提示的開頭：「Clode Myriagent 已更新為 0.2.2」「新版啟動失敗，Clode Myriagent 已退回 0.2.1」</summary>
    private static string AppPrefix(string? appTo, bool rolledBack)
    {
        var version = string.IsNullOrWhiteSpace(appTo) ? null : appTo.Trim();
        if (rolledBack)
            return version == null ? "新版啟動失敗，Clode Myriagent 已退回原本的版本" : $"新版啟動失敗，Clode Myriagent 已退回 {version}";
        return version == null ? "Clode Myriagent 已重新啟動" : $"Clode Myriagent 已更新為 {version}";
    }

    /// <param name="token">--after-update／--after-update-failed 帶的 token；一般啟動為 null</param>
    public static ResumeValidity Validate(AppResumeFile? file, string? token, DateTime nowUtc)
    {
        if (file == null)
            return ResumeValidity.Missing;
        if (string.IsNullOrEmpty(token) || !string.Equals(file.Token, token, StringComparison.Ordinal))
            return ResumeValidity.TokenMismatch;
        // 手改或損毀（仍是合法 JSON）的建立時間可能超出可表示的範圍：當成太舊，不讓啟動當掉
        if (file.CreatedAt < MinUnixMilliseconds || file.CreatedAt > MaxUnixMilliseconds)
            return ResumeValidity.TooOld;
        var created = DateTimeOffset.FromUnixTimeMilliseconds(file.CreatedAt).UtcDateTime;
        return nowUtc - created > CliRestartRules.MaxInterruptionAge ? ResumeValidity.TooOld : ResumeValidity.Ok;
    }

    public static bool ShouldContinue(ResumeConversation entry, ResumeValidity validity)
        => validity == ResumeValidity.Ok && entry.Mode == ResumeMode.Continue;

    /// <summary>
    /// 換版幫手啟動的這一次要不要藏著主視窗（設計 9.3）：--after-update／--after-update-failed 帶的 token 與 resume.json 相符，
    /// 而且交棒時主視窗藏著（Ui.MainHidden）。一般啟動（使用者自己開的）、token 不符、沒有檔案或讀不到都照常顯示；
    /// 不看建立時間（太舊只影響接續，不影響這次是不是無人值守的換版）
    /// </summary>
    public static bool StartMainHidden(AppResumeFile? file, string? startupToken)
        => file is { Ui.MainHidden: true } && !string.IsNullOrEmpty(startupToken) && string.Equals(file.Token, startupToken, StringComparison.Ordinal);

    /// <summary>
    /// 啟動清理要刪的草稿殘留（設計 9.3）：resume.json 列出的草稿對話（app 更新前沒送出的）不是殘留，要留給還原；
    /// resume.json 在但讀不到時不知道哪些要留，這次一個都不刪（下次啟動再清）
    /// </summary>
    public static IReadOnlyList<string> DraftLeftoversToDelete(IEnumerable<string> leftovers, StartupResume startup)
    {
        if (startup.Unreadable)
            return Array.Empty<string>();
        var keep = startup.File?.Ui.NewConversations.Select(d => d.DraftId).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        return leftovers.Where(id => !keep.Contains(id)).ToList();
    }

    /// <summary>
    /// 沒有自動接續的原因（決定標頭給不給「繼續」）。順序同 CliRestartRules.WhyNotContinue：使用者按了停止、本來就閒置的照原因，
    /// 再來是太舊；其他照停下時記的原因（token 不符時也保留，高風險操作的警告不能丟），沒記原因（本來可以接續）的是 Unknown
    /// </summary>
    public static CliNoContinueReason ReasonOf(ResumeConversation entry, ResumeValidity validity)
    {
        if (entry.WhyNot is CliNoContinueReason.UserStopped or CliNoContinueReason.Idle)
            return entry.WhyNot.Value;
        if (validity == ResumeValidity.TooOld)
            return CliNoContinueReason.TooOld;
        return entry.WhyNot ?? CliNoContinueReason.Unknown;
    }

    /// <summary>沒有自動接續的提示</summary>
    /// <param name="markdown">
    /// true：對話裡的 Notice（Markdown，操作摘要跳脫，同 CliRestartRules.NotContinuedNotice）；
    /// false：標頭 HeaderRestart.detail（純文字，不跳脫）
    /// </param>
    public static string DescribeNotContinued(ResumeConversation entry, ResumeValidity validity, string? appTo, bool rolledBack = false, bool markdown = false)
    {
        var reason = ReasonOf(entry, validity);
        var operation = entry.RiskOperation == null ? null : markdown ? CliRestartRules.EscapeMarkdown(entry.RiskOperation) : entry.RiskOperation;
        if (validity is ResumeValidity.TokenMismatch or ResumeValidity.Missing)
        {
            // 停下時記了原因的照原因說明（例如高風險操作結果未知）；本來可以接續的請使用者按「繼續」
            return entry.WhyNot == null
                ? $"{IncompleteUpdate}。按「繼續」請代理從中斷處接著做。"
                : $"{IncompleteUpdate}：{CliRestartRules.DescribeNoContinue(reason, operation)}";
        }
        return $"{AppPrefix(appTo, rolledBack)}，這個對話沒有自動接續：{CliRestartRules.DescribeNoContinue(reason, operation)}";
    }

    /// <summary>
    /// 讀到的 resume.json 正規化（合法 JSON，但手改、損毀或別的版本寫的）：清單的 null 換成空清單、清單裡的 null 項目丟掉、
    /// Ui 為 null 換成空的；缺必要欄位（對話、延後送出與 Remote Control 接回的 id、輸入框草稿與彈出視窗的對話 id、草稿對話的 id）的項目丟掉，
    /// 其他缺的文字補成空字串。
    /// 接續在主視窗 ready 時執行：不能因為一個 resume.json 丟例外，擋住健康閘門等的 MainViewReady。
    /// 就地修正讀檔剛建立的物件並回傳它：不逐欄位複製類別，日後新增的欄位不會在這裡漏掉（位置記錄以 with 換掉）
    /// </summary>
    public static AppResumeFile Normalize(AppResumeFile raw)
    {
        raw.Token ??= string.Empty;
        raw.From ??= string.Empty;
        raw.Conversations = Present(raw.Conversations).Where(c => !string.IsNullOrWhiteSpace(c.Id)).ToList();
        foreach (var c in raw.Conversations)
        {
            c.AccountId ??= string.Empty;
            c.InterruptedTools = Present(c.InterruptedTools).ToList();
            c.PendingRequests = Present(c.PendingRequests).ToList();
        }
        raw.DeferredSends = Present(raw.DeferredSends).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        raw.RemoteControl = Present(raw.RemoteControl).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();

        var ui = raw.Ui ?? new ResumeUi();
        ui.Popouts = Present(ui.Popouts).Where(p => !string.IsNullOrWhiteSpace(p.ConversationId)).ToList();
        ui.Drafts = Present(ui.Drafts).Where(d => !string.IsNullOrWhiteSpace(d.ConversationId))
            .Select(d => d with { Text = d.Text ?? string.Empty })
            .ToList();
        ui.NewConversations = Present(ui.NewConversations).Where(n => !string.IsNullOrWhiteSpace(n.DraftId))
            .Select(n => n with { AccountId = n.AccountId ?? string.Empty, Text = n.Text ?? string.Empty, Attachments = Present(n.Attachments).ToList() })
            .ToList();
        raw.Ui = ui;
        return raw;
    }

    /// <summary>清單本身是 null 當成空的，null 項目略過</summary>
    private static IEnumerable<T> Present<T>(List<T>? items) where T : class => items?.Where(i => i != null) ?? Enumerable.Empty<T>();

    /// <summary>release 名稱（0.2.1-aaaaaa1）的版本部分；不是 release 名稱就原樣回傳</summary>
    public static string? VersionOfRelease(string? release)
        => release == null ? null : UpdaterStateRules.VersionOf(release);

    /// <summary>第 index 個（同一種代理裡的順序）要等多久才送出接續：照同時重開上限分批，每批隔 3 秒</summary>
    public static TimeSpan StartDelay(int indexWithinProvider, AgentProvider provider)
        => TimeSpan.FromSeconds(3 * (indexWithinProvider / CliRestartRules.MaxConcurrentStarts(provider)));
}
