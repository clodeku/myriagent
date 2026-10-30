using Myriagent.Agents;
using Myriagent.Background;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 對話開出的背景程式（2026-10-23 使用者要求；規則在 Background/BackgroundProgramRules.cs）：這一輪結束了、但代理開的程式脫離 CLI 還在跑的對話，
/// 帳戶卡另外標出來（AccountView 的 sessions.programs 與 programs，docs/web-protocol.md），也經同步給其他電腦（link.upsert 的 sessions.programs）。
/// 只算程序掃描找到、帶這個對話標記、已經脫離 CLI 的程式（代理以 nohup、Start-Process 開了就不管的，CLI 不會回報、跑完也不會叫回代理）；
/// CLI 自己追蹤的背景工作（Claude 的 run_in_background、Codex 一輪結束時還在跑的指令）是 sessions.background 的「等背景工作回報」（AgentHub.Background.cs），
/// 它們的程序還在 CLI 底下、掃描不會算進來。中樞預設沒有掃描器（測試不列真的程序），只有 AppController 以 AttachBackgroundPrograms 接上。
/// 對話裡也看得到（2026-10-24 使用者要求：分開顯示、可以停止、自己結束時提醒）：標頭的 programs 列出這個對話另開、還在跑的程式（執行中也列；
/// 網頁在標頭的晶片列顯示「另開的程式」、選單裡可以停止，programStop），程式自己結束時在對話裡加一則提示（AgentNoticeKind.BackgroundProgram；
/// 使用者按停止的不加、不叫回代理）。
/// 掃描找到的只是候選：只有撐過一輪結束的才算（<see cref="confirmedPrograms"/>），對得上 CLI 背景工作的不算（<see cref="cliTaskPrograms"/>）。
/// 2026-10-24 核對：Git Bash 經 timeout、env 開的前景指令和 CLI 的父子關係會斷掉，以前執行途中就被列出來、結束時加「已結束」提示（192 則裡 132 則在執行途中）
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>
        /// 最近一輪結束的時間（Stopwatch.GetTimestamp；一輪開始時清成 null，沒執行過是 0）：在它之後才開始的掃描裡、對話不在執行中時還在的程式才算撐過了一輪
        /// </summary>
        public long? ProgramsIdleSince = 0;

        /// <summary>
        /// 同一個時間點的牆上時間（FILETIME 的 UTC 刻度，和程序的建立時間比）：之後才建立的程式是閒置時開出來的（別的背景工作的步驟）。
        /// 這個 app 還沒結束過這個對話的一輪時是 app 啟動的時間（之前就在跑的不算閒置時開出）
        /// </summary>
        public long ProgramsIdleSinceFileTime = HubStartedFileTime;

        /// <summary>這一輪是 CLI 自己開的（背景工作跑完、叫回代理；AgentIdleTurnStartedEvent）：這時程式結束不加提示，代理正在處理結果</summary>
        public bool ProgramsTurnByCli;
    }

    /// <summary>這個 app 啟動的時間（FILETIME 的 UTC 刻度）</summary>
    private static readonly long HubStartedFileTime = DateTime.UtcNow.ToFileTimeUtc();

    private IBackgroundProgramSource? backgroundSource;

    /// <summary>最近一次掃描的結果（UI 執行緒）</summary>
    private BackgroundScan backgroundScan = BackgroundScan.Empty;

    /// <summary>上次送出的帳戶卡上，背景程式的說明（對話、程式）的摘要：變了才重送帳戶卡</summary>
    private string renderedProgramsSig = string.Empty;

    /// <summary>
    /// 撐過一輪結束的程式（id）：一輪結束之後才開始的掃描裡、對話不在執行中時還在的，以及接續它們的子程序（換了根）。只有它們才列在標頭與帳戶卡、
    /// 結束時才提示——前景指令一定在一輪結束前跑完（CLI 會等），父子關係斷掉的前景程式撐不過一輪的結束
    /// </summary>
    private readonly HashSet<string> confirmedPrograms = new(StringComparer.Ordinal);

    /// <summary>
    /// 對得上 CLI 自己追蹤的背景工作的程式（id；BackgroundProgramRules.IsCliTask），以及 CLI 有背景工作在跑時、閒置中才開出來的程式：
    /// 歸等背景工作回報，不列、不算、結束時不提示
    /// </summary>
    private readonly HashSet<string> cliTaskPrograms = new(StringComparer.Ordinal);

    /// <summary>
    /// 列出、但結束時不提示的程式（id）：對話閒置中才開出來的——是別的背景工作的一個步驟（2026-10-24 實測：nohup bash chain.sh 這種 MSYS 的迴圈
    /// 本身不帶對話標記、看不到，看得到的是它一步步開的原生程式，每一步都是新的根，以前每一步結束都加一則「已結束」）
    /// </summary>
    private readonly HashSet<string> quietPrograms = new(StringComparer.Ordinal);

    internal void AttachBackgroundPrograms(IBackgroundProgramSource source)
    {
        backgroundSource = source ?? throw new ArgumentNullException(nameof(source));
        source.Start(scan => host.BeginInvoke(() => OnBackgroundScan(scan)));
    }

    /// <summary>一輪開始（HookTurnStarted）：之後的掃描找到的新程式要等這一輪結束才算</summary>
    private static void OnProgramsTurnStarted(ConversationState c, bool startedByCli)
    {
        c.ProgramsIdleSince = null;
        c.ProgramsTurnByCli = startedByCli;
    }

    /// <summary>一輪結束（HookTurnCompleted）：記下時間，立刻重掃（代理可能剛開了程式）</summary>
    private void OnProgramsTurnCompleted(ConversationState c)
    {
        c.ProgramsIdleSince = System.Diagnostics.Stopwatch.GetTimestamp();
        c.ProgramsIdleSinceFileTime = DateTime.UtcNow.ToFileTimeUtc();
        backgroundSource?.RequestScan();
    }

    /// <summary>
    /// 掃描結果回到 UI 執行緒（15 秒或一輪結束時）：記下來、判斷哪些撐過了一輪，帳戶卡的數字或說明變了就重送；列出的程式變了的對話重送標頭；
    /// 撐過一輪的程式自己結束時在它的對話裡加一則提示
    /// </summary>
    internal void OnBackgroundScan(BackgroundScan scan)
    {
        if (disposed)
            return;
        var before = backgroundScan;
        var ids = before.Programs.Keys.Union(scan.Programs.Keys).ToList();
        var shownBefore = ids.ToDictionary(id => id, ShownSignature, StringComparer.Ordinal);
        backgroundScan = scan;
        TrackPrograms(before, scan);
        // 背景活動日誌：剛分類的程式與結束的程式（AgentHub.Journal.cs）
        JournalPrograms(scan);
        foreach (var exit in scan.Exits)
        {
            if (exit.StoppedByUser || !confirmedPrograms.Contains(exit.Program.Id) || quietPrograms.Contains(exit.Program.Id)
                || !conversations.TryGetValue(exit.ConversationId, out var c) || c.IsReplica || c.IsDraft)
                continue;
            // CLI 還有背景工作在等（代理以背景工作輪詢它）、或 CLI 已經叫回代理在處理：CLI 會告訴代理，不另外提示（2026-10-24 使用者要求：以前反應兩次）
            if (BackgroundOf(c) != null || (c.Running && c.ProgramsTurnByCli))
                continue;
            AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = BackgroundProgramRules.ExitNotice(exit), NoticeKind = AgentNoticeKind.BackgroundProgram });
        }
        var alive = scan.Programs.Values.SelectMany(l => l).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        confirmedPrograms.RemoveWhere(id => !alive.Contains(id));
        cliTaskPrograms.RemoveWhere(id => !alive.Contains(id));
        quietPrograms.RemoveWhere(id => !alive.Contains(id));

        var countsChanged = accounts.Accounts.Any(a => !renderedSessionCounts.TryGetValue(a.Id, out var shown) || shown != SessionCounts(a.Id));
        if (countsChanged || ProgramsSignature() != renderedProgramsSig)
            MarkAccountsDirty();
        foreach (var id in ids)
        {
            if (conversations.TryGetValue(id, out var c) && ShownSignature(id) != shownBefore[id])
            {
                MarkHeaderDirty(c);
                MarkListDirty();   // 清單的 programCount（刪除前問要不要一併停止）
            }
        }
    }

    /// <summary>
    /// 新出現的候選：對得上這個對話的 CLI 背景工作的歸 CLI；這個對話不在執行中、而且掃描是在最近一輪結束之後才開始的，就撐過了一輪；
    /// 父程序是上一次已經算數（或歸 CLI）的程式的（根結束了、子程序接著當根）跟著它。閒置中才開出來的（不是接續）是別的背景工作的步驟：
    /// CLI 有背景工作在跑時歸 CLI，否則列出、但結束時不提示
    /// </summary>
    private void TrackPrograms(BackgroundScan before, BackgroundScan scan)
    {
        foreach (var (id, programs) in scan.Programs)
        {
            if (!conversations.TryGetValue(id, out var c))
                continue;
            if (!c.Running && c.ProgramsIdleSince == null)
            {
                // 沒經過結束掛鉤就停下的：從現在算起
                c.ProgramsIdleSince = System.Diagnostics.Stopwatch.GetTimestamp();
                c.ProgramsIdleSinceFileTime = DateTime.UtcNow.ToFileTimeUtc();
            }
            var settled = !c.Running && c.ProgramsIdleSince is { } since && scan.TakenAt >= since;
            var tasks = BackgroundOf(c)?.Tools.SelectMany(t => new[] { t.Title, t.Detail }).ToList();
            var earlier = before.Programs.TryGetValue(id, out var list) ? list : [];
            foreach (var p in programs)
            {
                if (confirmedPrograms.Contains(p.Id) || cliTaskPrograms.Contains(p.Id))
                    continue;
                var parent = earlier.FirstOrDefault(e => e.Pid == p.ParentPid && e.CreatedAt <= p.CreatedAt);
                var continues = parent != null && confirmedPrograms.Contains(parent.Id);
                var idleBorn = settled && !continues && p.CreatedAt > c.ProgramsIdleSinceFileTime;
                if ((parent != null && cliTaskPrograms.Contains(parent.Id)) || (tasks != null && (idleBorn || BackgroundProgramRules.IsCliTask(p, tasks))))
                    cliTaskPrograms.Add(p.Id);
                else if (settled || continues)
                {
                    confirmedPrograms.Add(p.Id);
                    if (idleBorn || (continues && quietPrograms.Contains(parent!.Id)))
                        quietPrograms.Add(p.Id);
                }
            }
        }
    }

    /// <summary>這個對話列出的程式（撐過一輪、不是 CLI 的背景工作），依建立時間舊到新</summary>
    private IReadOnlyList<BackgroundProgram> ShownPrograms(ConversationState c)
        => backgroundScan.Programs.TryGetValue(c.Meta.Id, out var programs) ? programs.Where(p => confirmedPrograms.Contains(p.Id)).ToList() : [];

    private string ShownSignature(string conversationId)
        => backgroundScan.Programs.TryGetValue(conversationId, out var programs)
            ? string.Join(',', programs.Where(p => confirmedPrograms.Contains(p.Id)).Select(p => p.Id))
            : string.Empty;

    /// <summary>
    /// 標頭的 programs（docs/web-protocol.md 的 Header.programs）：這個對話另開、撐過一輪結束還在跑的程式，依建立時間舊到新、最多
    /// <see cref="BackgroundProgramRules.MaxHeaderPrograms"/> 個；沒有（或副本、草稿、沒接掃描器）是 null。執行中也列之前的輪次留下的（只有帳戶卡把執行中的算成執行中）
    /// </summary>
    private JArray? RenderConversationPrograms(ConversationState c)
    {
        if (c.IsReplica || c.IsDraft || ShownPrograms(c) is not { Count: > 0 } programs)
            return null;
        return new JArray(programs.Take(BackgroundProgramRules.MaxHeaderPrograms).Select(p => new JObject
        {
            ["id"] = p.Id,
            ["pid"] = p.Pid,
            ["name"] = p.Name,
            ["command"] = BackgroundProgramRules.ShortCommand(p.CommandLine, p.Name, BackgroundProgramRules.MaxHeaderCommandLength),
            ["startedAt"] = p.CreatedAt > 0 ? ToUnixMs(DateTime.FromFileTimeUtc(p.CreatedAt).ToLocalTime()) : null,
        }));
    }

    /// <summary>
    /// programStop（對話標頭的「另開的程式」選單）：結束那個程式與它的子孫。只認這個對話最近一次掃描列出的 id（網頁不能指定 PID）；
    /// 已經不在清單、或結束不了時只對送指令的視窗提示
    /// </summary>
    private void StopProgram(IAgentView view, ConversationState? c, string? programId)
    {
        if (c == null || c.IsReplica || backgroundSource is not { } source || string.IsNullOrEmpty(programId))
            return;
        var program = ShownPrograms(c).FirstOrDefault(p => p.Id == programId);
        if (program == null)
        {
            Toast("這個程式已經結束了", view: view);
            return;
        }
        var stopEntry = ProgramEntry(program);
        stopEntry["by"] = "user";
        Journal("prog.stop", c, stopEntry);
        _ = StopProgramAsync(source, view, program);
    }

    private async Task StopProgramAsync(IBackgroundProgramSource source, IAgentView view, BackgroundProgram program)
    {
        bool stopped;
        try
        {
            stopped = await source.StopAsync(program).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"停止背景程式 {program.Pid} 失敗：{ex.Message}");
            stopped = false;
        }
        if (!stopped)
        {
            var command = BackgroundProgramRules.ShortCommand(program.CommandLine, program.Name);
            host.BeginInvoke(() =>
            {
                if (!disposed)
                    Toast($"沒有停止「{command}」：它可能已經結束了，或沒有權限結束它", isError: true, view: view);
            });
        }
    }

    /// <summary>
    /// 刪除對話時一併停止它另開的程式（delete 的 stopPrograms，2026-10-24 使用者要求：以前刪除後程式照跑、在 Myriagent 裡從此看不到）。
    /// 只停列出的（撐過一輪、不是 CLI 的背景工作）；停止照 programStop，看不到的 MSYS 迴圈不會跟著停
    /// </summary>
    private void StopConversationPrograms(ConversationState c)
    {
        if (backgroundSource is not { } source || c.IsReplica)
            return;
        foreach (var program in ShownPrograms(c))
        {
            var stopEntry = ProgramEntry(program);
            stopEntry["by"] = "delete";
            Journal("prog.stop", c, stopEntry);
            _ = StopProgramQuietlyAsync(source, program);
        }
    }

    private static async Task StopProgramQuietlyAsync(IBackgroundProgramSource source, BackgroundProgram program)
    {
        try
        {
            await source.StopAsync(program).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"刪除對話時停止背景程式 {program.Pid} 失敗：{ex.Message}");
        }
    }

    /// <summary>這一輪結束了、但這個對話另開的程式還在跑（只算這台的對話；草稿、複本、執行中的不算）</summary>
    private bool HasPrograms(ConversationState c)
        => !c.IsDraft && !c.IsReplica && !c.Running && ShownPrograms(c).Count > 0;

    /// <summary>
    /// 帳戶卡的背景程式說明：有背景程式的對話依更新時間新到舊，每個對話列標題與程式的命令列（最多
    /// <see cref="BackgroundProgramRules.MaxListedPrograms"/> 個，more＝還有幾個沒列）
    /// </summary>
    private JArray RenderAccountPrograms(string accountId)
    {
        var list = new JArray();
        foreach (var c in conversations.Values.Where(c => c.Meta.AccountId == accountId && HasPrograms(c)).OrderByDescending(c => c.Meta.UpdatedAt))
        {
            var found = ShownPrograms(c);
            list.Add(new JObject
            {
                ["conversationId"] = c.Meta.Id,
                ["title"] = c.Meta.Title,
                ["programs"] = new JArray(found.Take(BackgroundProgramRules.MaxListedPrograms).Select(p => BackgroundProgramRules.ShortCommand(p.CommandLine, p.Name))),
                ["more"] = Math.Max(0, found.Count - BackgroundProgramRules.MaxListedPrograms),
            });
        }
        return list;
    }

    /// <summary>所有帳戶的背景程式說明的摘要（帳戶卡是不是要重送）</summary>
    private string ProgramsSignature()
        => string.Join('\n', accounts.Accounts.Select(a => a.Id + '\t' + RenderAccountPrograms(a.Id).ToString(Newtonsoft.Json.Formatting.None)));

    private void StopBackground()
    {
        try
        {
            backgroundSource?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"停止背景程式的掃描失敗：{ex.Message}");
        }
    }
}
