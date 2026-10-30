using Myriagent.Agents;
using Myriagent.Background;
using Myriagent.Core;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 背景活動日誌（docs/background-journal.md；2026-10-27 使用者要求：app 常要更新重開，常被背景程式卡住、背景工作被強迫中止、
// 背景工作無限跑讓代理一直等——先把每個對話開的子代理、背景工作、背景程式與換版的排空記清楚，供之後除錯與修正）。
// 只記錄、不改任何行為；中樞預設沒接（不記），只有 AppController 以 AttachBackgroundJournal 接上真的檔案。只在 UI 執行緒
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>日誌：還沒結束的子代理工具卡（id → 開始時間、標題），結束時算跑了多久；程序結束時還開著的記成跟著結束</summary>
        public readonly Dictionary<string, (DateTime Started, string? Title)> JournalSubagents = new(StringComparer.Ordinal);

        /// <summary>日誌：這一輪開始的時間（turn.end 記跑了多久）</summary>
        public DateTime? JournalTurnStarted;
    }

    private IBackgroundJournal? journal;

    /// <summary>日誌：記過的背景程式（id → 分類），結束時帶上分類、之後忘掉</summary>
    private readonly Dictionary<string, string> journaledPrograms = new(StringComparer.Ordinal);

    private DateTime lastDrainSnapshot = DateTime.MinValue;
    private DateTime lastBackgroundSnapshot = DateTime.MinValue;

    private static readonly int JournalPid = Environment.ProcessId;

    /// <summary>接上背景活動日誌（AppController；測試接假的）</summary>
    internal void AttachBackgroundJournal(IBackgroundJournal backgroundJournal) => journal = backgroundJournal;

    /// <summary>給 app 層級的事件（啟動、換版的協調器）寫一筆；沒接日誌時不做事</summary>
    internal void JournalApp(string ev, JObject? data = null) => Journal(ev, null, data);

    private void Journal(string ev, ConversationState? c, JObject? data = null)
    {
        if (journal is not { } j)
            return;
        try
        {
            var conversation = c == null ? null : new JournalConversation(c.Meta.Id, c.IsDraft ? null : c.Meta.Title, c.Meta.Provider.ToString(), c.Meta.AccountId);
            j.Write(BackgroundJournalRules.Entry(ev, DateTimeOffset.Now, JournalPid, AppVersionInfo.VersionText, conversation, data));
        }
        catch (Exception ex)
        {
            Log.Warn($"背景活動日誌記不下 {ev}：{ex.Message}");
        }
    }

    // ───────────────────────────── 會話事件（OnSessionEvent 一開始，改狀態之前） ─────────────────────────────

    private void JournalSessionEventCore(ConversationState c, AgentEvent ev)
    {
        if (journal == null || c.IsReplica)
            return;
        switch (ev)
        {
            case AgentBackgroundWorkEvent work:
                JournalBackgroundWork(c, work);
                break;
            case AgentBackgroundCommandFinishedEvent finished:
                Journal("bg.finished", c, new JObject
                {
                    ["toolId"] = finished.ToolId,
                    ["title"] = BackgroundJournalRules.Clip(finished.Title),
                    ["status"] = finished.Status.ToString(),
                    ["exitCode"] = finished.ExitCode,
                    ["sec"] = BackgroundJournalRules.Seconds(finished.Duration),
                    ["stoppedByUser"] = finished.StoppedByUser ? true : null,
                });
                break;
            case AgentTaskLifecycleEvent task:
                Journal(task.Ended ? "task.end" : "task.start", c, new JObject
                {
                    ["taskId"] = task.TaskId,
                    ["type"] = task.TaskType,
                    ["desc"] = BackgroundJournalRules.Clip(task.Description),
                    ["toolId"] = task.ToolUseId,
                    ["background"] = task.Backgrounded,
                    ["status"] = task.Status,
                    ["summary"] = BackgroundJournalRules.Clip(task.Summary),
                });
                break;
            case AgentToolStartedEvent tool when BackgroundJournalRules.IsSubagentTool(tool.ToolKind):
                c.JournalSubagents[tool.ToolId] = (DateTime.Now, tool.Title);
                Journal("sub.start", c, new JObject
                {
                    ["toolId"] = tool.ToolId,
                    ["tool"] = tool.ToolName,
                    ["title"] = BackgroundJournalRules.Clip(tool.Title),
                    ["prompt"] = BackgroundJournalRules.Clip(tool.Detail, BackgroundJournalRules.MaxCommand),
                    ["open"] = c.JournalSubagents.Count,
                });
                break;
            case AgentToolCompletedEvent done when c.JournalSubagents.Remove(done.ToolId, out var sub):
                Journal("sub.end", c, new JObject
                {
                    ["toolId"] = done.ToolId,
                    ["title"] = BackgroundJournalRules.Clip(sub.Title),
                    ["status"] = done.Status.ToString(),
                    ["sec"] = BackgroundJournalRules.Seconds(DateTime.Now - sub.Started),
                    ["result"] = BackgroundJournalRules.Clip(done.Output),
                });
                break;
            case AgentSubagentUsageEvent { Usage.Done: true } usage:
                Journal("sub.usage", c, new JObject
                {
                    ["toolId"] = usage.Usage.ToolUseId,
                    ["tokens"] = usage.Usage.TotalTokens,
                    ["toolUses"] = usage.Usage.ToolUses,
                    ["sec"] = Math.Round(usage.Usage.DurationMs / 1000.0, 1),
                });
                break;
            case AgentIdleTurnStartedEvent idle:
                Journal("turn.cliStart", c, new JObject
                {
                    ["afterBackground"] = idle.AfterBackgroundTask,
                    ["threadGoal"] = idle.ForThreadGoal ? true : null,
                    ["alreadyRunning"] = c.Running ? true : null,
                });
                break;
            case AgentProcessStartedEvent started:
                Journal("proc.start", c, new JObject { ["instance"] = started.ProcessInstance, ["cli"] = started.Cli?.Version });
                break;
            case AgentProcessExitedEvent exited:
                JournalProcessExited(c, exited);
                break;
        }
    }

    /// <summary>
    /// 背景工作的清單變了：和同一個會話上一次回報的比，新出現的記 bg.start、消失的記 bg.end（帶跑了多久）；只有數量變了（對不到工具的）記 bg.count。
    /// 換了會話（舊程序結束、重開）時舊會話還列著的記成跟著舊會話結束
    /// </summary>
    private void JournalBackgroundWork(ConversationState c, AgentBackgroundWorkEvent ev)
    {
        var now = DateTime.Now;
        var sameSession = ReferenceEquals(c.BackgroundWorkSession, c.Session);
        var previous = c.BackgroundWork;
        if (!sameSession && previous is { Count: > 0 })
        {
            Journal("bg.end", c, new JObject
            {
                ["how"] = "sessionReplaced",
                ["count"] = previous.Count,
                ["tools"] = BackgroundJournalRules.Tools(previous.Tools),
                ["sec"] = BackgroundJournalRules.Seconds(c.BackgroundSince is { } since ? now - since : null),
            });
            previous = null;
        }
        var before = previous?.Tools ?? Array.Empty<AgentBackgroundTool>();
        var (started, ended) = BackgroundJournalRules.Diff(before, ev.Tools);
        foreach (var i in ended)
        {
            var entry = BackgroundJournalRules.Tool(before[i]);
            entry["count"] = ev.Count;
            entry["sec"] = BackgroundJournalRules.Seconds(i < c.BackgroundStarts.Count ? now - c.BackgroundStarts[i] : null);
            entry["running"] = c.Running;
            Journal("bg.end", c, entry);
        }
        foreach (var i in started)
        {
            var entry = BackgroundJournalRules.Tool(ev.Tools[i]);
            entry["count"] = ev.Count;
            entry["running"] = c.Running;
            Journal("bg.start", c, entry);
        }
        if (started.Count == 0 && ended.Count == 0 && (previous?.Count ?? 0) != ev.Count)
            Journal("bg.count", c, new JObject { ["count"] = ev.Count, ["before"] = previous?.Count ?? 0, ["listed"] = ev.Tools.Count, ["running"] = c.Running });
    }

    /// <summary>
    /// 中樞自己關掉這個對話的 CLI 程序（閒置回收、換帳戶、刪除、清除、回溯、移除帳戶；2026-10-27）：會話這時不發 AgentProcessExitedEvent，
    /// 以前日誌裡只看得到之後 CLI 重開時回報「被上一個程序丟下的背景工作」，看不出程序是誰、為什麼關的。沒有活著的程序不記；
    /// 還開著的子代理記成跟著結束（同 proc.exit）。換版重開記在 restart.*、app 結束記在 app.exit，不經這裡。一律在關掉之前呼叫
    /// </summary>
    /// <param name="why">idle、accountSwitch、delete、clear、rewind、accountRemoved</param>
    /// <param name="now">閒置多久的基準（維護傳它的現在時間；null＝現在）</param>
    private void JournalProcessClose(ConversationState c, string why, JObject? extra = null, DateTime? now = null)
    {
        if (journal == null || c.IsReplica || c.Session is not { HasLiveProcess: true } session)
            return;
        var live = BackgroundOf(c);
        var entry = new JObject
        {
            ["why"] = why,
            ["instance"] = c.Restart.Instance,
            ["running"] = c.Running ? true : null,
            ["idleSec"] = BackgroundJournalRules.Seconds((now ?? DateTime.Now) - c.LastActivity),
            ["bgCount"] = live?.Count,
            ["bg"] = live is { Tools.Count: > 0 } ? BackgroundJournalRules.Tools(live.Tools) : null,
            ["subagents"] = c.JournalSubagents.Count > 0 ? new JArray(c.JournalSubagents.Values.Select(s => BackgroundJournalRules.Clip(s.Title))) : null,
            ["remoteControl"] = session.HasRemoteControl ? true : null,
        };
        if (extra != null)
            entry.Merge(extra);
        Journal("proc.close", c, entry);
        var closedAt = DateTime.Now;
        foreach (var (id, sub) in c.JournalSubagents.ToList())
        {
            Journal("sub.end", c, new JObject
            {
                ["toolId"] = id,
                ["title"] = BackgroundJournalRules.Clip(sub.Title),
                ["status"] = "processClosed",
                ["sec"] = BackgroundJournalRules.Seconds(closedAt - sub.Started),
            });
        }
        c.JournalSubagents.Clear();
    }

    /// <summary>CLI 程序結束：當時還在跑的背景工作與子代理一起沒了（背景工作的 bg.end 之後由會話回報的 0 記），子代理在這裡記成跟著結束</summary>
    private void JournalProcessExited(ConversationState c, AgentProcessExitedEvent exited)
    {
        var live = ReferenceEquals(c.BackgroundWorkSession, c.Session) && c.BackgroundWork is { Count: > 0 } work ? work : null;
        Journal("proc.exit", c, new JObject
        {
            ["instance"] = exited.ProcessInstance,
            ["exitCode"] = exited.ExitCode,
            ["expected"] = exited.Expected,
            ["running"] = c.Running,
            ["bgCount"] = live?.Count,
            ["bg"] = live is { Tools.Count: > 0 } ? BackgroundJournalRules.Tools(live.Tools) : null,
            ["subagents"] = c.JournalSubagents.Count > 0 ? new JArray(c.JournalSubagents.Values.Select(s => BackgroundJournalRules.Clip(s.Title))) : null,
            ["stderr"] = exited.Expected ? null : BackgroundJournalRules.Clip(exited.StderrTail, BackgroundJournalRules.MaxCommand),
        });
        var now = DateTime.Now;
        foreach (var (id, sub) in c.JournalSubagents.ToList())
        {
            Journal("sub.end", c, new JObject
            {
                ["toolId"] = id,
                ["title"] = BackgroundJournalRules.Clip(sub.Title),
                ["status"] = "processExited",
                ["sec"] = BackgroundJournalRules.Seconds(now - sub.Started),
            });
        }
        c.JournalSubagents.Clear();
    }

    // ───────────────────────────── 一輪的開始與結束 ─────────────────────────────

    private void JournalTurnStartedCore(ConversationState c, bool startedByCli)
    {
        c.JournalTurnStarted = DateTime.Now;
        if (c.IsReplica)
            return;
        Journal("turn.start", c, new JObject { ["byCli"] = startedByCli ? true : null, ["bgCount"] = BackgroundOf(c)?.Count });
    }

    /// <summary>一輪結束：結果、跑了多久，還在跑的背景工作與背景程式（之後代理就在等它們）</summary>
    private void JournalTurnCompletedCore(ConversationState c, AgentTurnOutcome outcome)
    {
        var since = c.JournalTurnStarted;
        c.JournalTurnStarted = null;
        if (c.IsReplica || journal == null)
            return;
        var work = BackgroundOf(c);
        var programs = ShownPrograms(c);
        Journal("turn.end", c, new JObject
        {
            ["outcome"] = outcome.ToString(),
            ["sec"] = BackgroundJournalRules.Seconds(since is { } s ? DateTime.Now - s : null),
            ["bgCount"] = work?.Count,
            ["bg"] = work is { Tools.Count: > 0 } ? BackgroundJournalRules.Tools(work.Tools) : null,
            ["programs"] = programs.Count > 0 ? ProgramList(programs) : null,
            ["subagents"] = c.JournalSubagents.Count > 0 ? c.JournalSubagents.Count : null,
        });
    }

    // ───────────────────────────── 背景程式 ─────────────────────────────

    /// <summary>
    /// 掃描結果（OnBackgroundScan，分類之後）：剛被分類的程式記 prog.found（confirmed＝撐過一輪、列在畫面上；cliTask＝CLI 背景工作的程序；
    /// quiet＝閒置中開的、結束不提示），結束的記 prog.exit（分類過的一律記，沒分類的跑了 30 秒以上才記）
    /// </summary>
    private void JournalProgramsCore(BackgroundScan scan)
    {
        if (journal == null)
            return;
        foreach (var (id, programs) in scan.Programs)
        {
            conversations.TryGetValue(id, out var c);
            foreach (var p in programs)
            {
                var cls = ProgramClass(p.Id);
                if (cls == null || journaledPrograms.TryGetValue(p.Id, out var known) && known == cls)
                    continue;
                journaledPrograms[p.Id] = cls;
                var entry = ProgramEntry(p);
                entry["class"] = cls;
                if (c == null)
                    entry["conversationId"] = id;
                Journal("prog.found", c, entry);
            }
        }
        foreach (var exit in scan.Exits)
        {
            journaledPrograms.Remove(exit.Program.Id, out var cls);
            // 兩者都是 FILETIME 刻度（100 奈秒，同 TimeSpan 的刻度）
            TimeSpan? lifetime = exit.Program.CreatedAt > 0 && exit.ExitedAt is long end && end >= exit.Program.CreatedAt
                ? TimeSpan.FromTicks(end - exit.Program.CreatedAt)
                : null;
            if (!BackgroundJournalRules.ShouldLogProgramExit(cls, lifetime))
                continue;
            conversations.TryGetValue(exit.ConversationId, out var c);
            var entry = ProgramEntry(exit.Program);
            entry["class"] = cls;
            entry["exitCode"] = exit.ExitCode;
            entry["sec"] = BackgroundJournalRules.Seconds(lifetime);
            entry["stoppedByUser"] = exit.StoppedByUser ? true : null;
            if (c == null)
                entry["conversationId"] = exit.ConversationId;
            Journal("prog.exit", c, entry);
        }
    }

    private string? ProgramClass(string programId)
        => cliTaskPrograms.Contains(programId) ? "cliTask"
            : quietPrograms.Contains(programId) ? "quiet"
            : confirmedPrograms.Contains(programId) ? "confirmed"
            : null;

    private static JObject ProgramEntry(BackgroundProgram p) => new()
    {
        ["pid"] = p.Pid,
        ["name"] = p.Name,
        ["cmd"] = BackgroundJournalRules.Clip(p.CommandLine, BackgroundJournalRules.MaxCommand),
        ["parentPid"] = p.ParentPid,
        ["startedAt"] = p.CreatedAt > 0 ? DateTime.FromFileTimeUtc(p.CreatedAt).ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss") : null,
    };

    private static JArray ProgramList(IEnumerable<BackgroundProgram> programs)
        => new(programs.Select(p => new JObject
        {
            ["pid"] = p.Pid,
            ["name"] = p.Name,
            ["cmd"] = BackgroundJournalRules.Clip(p.CommandLine, 200),
            ["sec"] = p.CreatedAt > 0 ? BackgroundJournalRules.Seconds(DateTime.UtcNow - DateTime.FromFileTimeUtc(p.CreatedAt)) : null,
        }));

    // ───────────────────────────── 換版（CLI 熱更新、app 自我更新） ─────────────────────────────

    /// <summary>換版的計畫改了在等什麼（SetWaiting）：等什麼、等哪個工具或卡片、已經等了多久</summary>
    private void JournalRestartWaitingCore(ConversationState c, RestartPlan plan)
        => Journal("restart.wait", c, new JObject
        {
            ["state"] = plan.Waiting?.ToString(),
            ["subject"] = BackgroundJournalRules.Clip(plan.WaitingSubject),
            ["appUpdate"] = plan.AppUpdate ? true : null,
            ["drain"] = plan.Drain ? true : null,
            ["waitedSec"] = BackgroundJournalRules.Seconds(RestartClock() - plan.Since),
            ["running"] = c.Running,
            ["bg"] = BackgroundOf(c) is { Tools.Count: > 0 } work ? BackgroundJournalRules.Tools(work.Tools) : null,
        });

    /// <summary>背景工作等滿上限、照立即套用停下（之後的 restart.call 會停掉它們）</summary>
    private void JournalRestartExpiredCore(ConversationState c, RestartPlan plan, bool forced)
        => Journal("restart.expired", c, new JObject
        {
            ["waitedSec"] = BackgroundJournalRules.Seconds(RestartClock() - plan.Since),
            ["forced"] = forced ? true : null,
            ["waiting"] = forced ? plan.Waiting?.ToString() : null,
            ["appUpdate"] = plan.AppUpdate ? true : null,
            ["bgCount"] = BackgroundOf(c)?.Count,
            ["bg"] = BackgroundOf(c) is { Tools.Count: > 0 } work ? BackgroundJournalRules.Tools(work.Tools) : null,
            ["programs"] = ShownPrograms(c) is { Count: > 0 } programs ? ProgramList(programs) : null,
        });

    /// <summary>停下或重開一個對話的程序：被打斷的工具、沒回應的請求、被停掉的背景工作、會不會自動接續</summary>
    private void JournalRestartCallCore(ConversationState c, RestartPlan plan, AgentRestartRequest request, bool stopOnly)
        => Journal("restart.call", c, new JObject
        {
            ["stopOnly"] = stopOnly,
            ["reason"] = request.Reason.ToString(),
            ["mode"] = request.Mode.ToString(),
            ["from"] = request.FromVersion,
            ["to"] = request.ToVersion,
            ["autoContinue"] = request.AutoContinue,
            ["noContinue"] = plan.NoContinueReason?.ToString(),
            ["running"] = c.Running,
            ["waitedSec"] = BackgroundJournalRules.Seconds(RestartClock() - plan.Since),
            ["interrupted"] = plan.InterruptedTools.Count > 0
                ? new JArray(plan.InterruptedTools.Select(t => new JObject { ["tool"] = t.ToolName, ["title"] = BackgroundJournalRules.Clip(t.Title), ["sec"] = BackgroundJournalRules.Seconds(t.Elapsed) }))
                : null,
            ["pending"] = plan.PendingRequests.Count > 0 ? new JArray(plan.PendingRequests.Select(p => BackgroundJournalRules.Clip(p))) : null,
            ["hadBackground"] = plan.HadBackground ? true : null,
            ["bg"] = c.Session?.LiveBackgroundTools is { Count: > 0 } tools ? BackgroundJournalRules.Tools(tools) : null,
            ["subagents"] = c.JournalSubagents.Count > 0 ? new JArray(c.JournalSubagents.Values.Select(s => BackgroundJournalRules.Clip(s.Title))) : null,
            ["risk"] = plan.Risk?.RuleId,
        });

    /// <summary>
    /// 維護計時器（30 秒）：換版排空中每 2 分鐘記一次還在等的對話（restart.snapshot）；平常有背景工作或背景程式時每 10 分鐘記一次（bg.snapshot），
    /// 無限跑的背景工作從這裡看得出來
    /// </summary>
    private void TickJournalCore(DateTime now)
    {
        if (journal == null)
            return;
        var plans = conversations.Values.Where(c => c.Restart.Plan != null && !c.IsReplica).ToList();
        if (plans.Count > 0 && now - lastDrainSnapshot >= BackgroundJournalRules.DrainSnapshotInterval)
        {
            lastDrainSnapshot = now;
            JournalApp("restart.snapshot", new JObject
            {
                ["barriers"] = barriers.Count > 0 ? new JArray(barriers.Values.Select(b => new JObject
                {
                    ["provider"] = b.Provider.ToString(),
                    ["appUpdate"] = b.AppUpdate,
                    ["sec"] = BackgroundJournalRules.Seconds(RestartClock() - b.StartedAt),
                })) : null,
                ["conversations"] = new JArray(plans.Select(c =>
                {
                    var plan = c.Restart.Plan!;
                    return new JObject
                    {
                        ["conv"] = c.Meta.Id,
                        ["title"] = BackgroundJournalRules.Clip(c.Meta.Title, 60),
                        ["state"] = plan.Waiting?.ToString(),
                        ["subject"] = BackgroundJournalRules.Clip(plan.WaitingSubject),
                        ["waitedSec"] = BackgroundJournalRules.Seconds(RestartClock() - plan.Since),
                        ["running"] = c.Running,
                        ["inFlight"] = plan.InFlight ? true : null,
                        ["held"] = plan.Held ? true : null,
                        ["drained"] = plan.Drained ? true : null,
                        ["bg"] = BackgroundOf(c) is { Tools.Count: > 0 } work ? BackgroundJournalRules.Tools(work.Tools) : null,
                    };
                })),
            });
        }
        if (now - lastBackgroundSnapshot >= BackgroundJournalRules.BackgroundSnapshotInterval)
        {
            var busy = conversations.Values
                .Where(c => !c.IsReplica && !c.IsDraft && (BackgroundOf(c) != null || ShownPrograms(c).Count > 0 || c.JournalSubagents.Count > 0))
                .ToList();
            if (busy.Count == 0)
                return;
            lastBackgroundSnapshot = now;
            JournalApp("bg.snapshot", new JObject
            {
                ["conversations"] = new JArray(busy.Select(c =>
                {
                    var work = BackgroundOf(c);
                    var programs = ShownPrograms(c);
                    return new JObject
                    {
                        ["conv"] = c.Meta.Id,
                        ["title"] = BackgroundJournalRules.Clip(c.Meta.Title, 60),
                        ["running"] = c.Running,
                        ["bgSec"] = BackgroundJournalRules.Seconds(work != null && c.BackgroundSince is { } since ? DateTime.Now - since : null),
                        ["bg"] = work is { Tools.Count: > 0 } ? BackgroundJournalRules.Tools(work.Tools) : work != null ? new JArray() : null,
                        ["bgCount"] = work?.Count,
                        ["programs"] = programs.Count > 0 ? ProgramList(programs) : null,
                        ["subagents"] = c.JournalSubagents.Count > 0 ? c.JournalSubagents.Count : null,
                    };
                })),
            });
        }
    }

    /// <summary>app 結束（或 Windows 問能不能關機）時還在執行或在等背景工作的對話：結束會停掉的都在這一筆</summary>
    private void JournalAppExitCore(bool closedByApp)
    {
        if (journal == null)
            return;
        var live = conversations.Values
            .Where(c => !c.IsReplica && !c.IsDraft && (c.Running || BackgroundOf(c) != null || ShownPrograms(c).Count > 0 || c.JournalSubagents.Count > 0))
            .ToList();
        JournalApp(closedByApp ? "app.exit" : "app.exitQuery", new JObject
        {
            ["conversations"] = new JArray(live.Select(c =>
            {
                var work = BackgroundOf(c);
                var programs = ShownPrograms(c);
                return new JObject
                {
                    ["conv"] = c.Meta.Id,
                    ["title"] = BackgroundJournalRules.Clip(c.Meta.Title, 60),
                    ["running"] = c.Running,
                    ["stopRequested"] = c.StopRequested ? true : null,
                    ["bgCount"] = work?.Count,
                    ["bg"] = work is { Tools.Count: > 0 } ? BackgroundJournalRules.Tools(work.Tools) : null,
                    ["programs"] = programs.Count > 0 ? ProgramList(programs) : null,
                    ["subagents"] = c.JournalSubagents.Count > 0 ? new JArray(c.JournalSubagents.Values.Select(s => BackgroundJournalRules.Clip(s.Title))) : null,
                };
            })),
        });
    }

    // ───────────────────────────── 日誌出錯不能影響中樞：每個掛鉤各自接住例外 ─────────────────────────────

    private void JournalSessionEvent(ConversationState c, AgentEvent ev)
    {
        if (journal == null || c.IsReplica)
            return;
        try
        {
            JournalSessionEventCore(c, ev);
        }
        catch (Exception ex)
        {
            JournalFailed(nameof(JournalSessionEvent), ex);
        }
    }

    private void JournalTurnStarted(ConversationState c, bool startedByCli)
    {
        try
        {
            JournalTurnStartedCore(c, startedByCli);
        }
        catch (Exception ex)
        {
            JournalFailed(nameof(JournalTurnStarted), ex);
        }
    }

    private void JournalTurnCompleted(ConversationState c, AgentTurnOutcome outcome)
    {
        if (journal == null)
            return;
        try
        {
            JournalTurnCompletedCore(c, outcome);
        }
        catch (Exception ex)
        {
            JournalFailed(nameof(JournalTurnCompleted), ex);
        }
    }

    private void JournalPrograms(BackgroundScan scan)
    {
        if (journal == null)
            return;
        try
        {
            JournalProgramsCore(scan);
        }
        catch (Exception ex)
        {
            JournalFailed(nameof(JournalPrograms), ex);
        }
    }

    private void JournalRestartWaiting(ConversationState c, RestartPlan plan)
    {
        if (journal == null)
            return;
        try
        {
            JournalRestartWaitingCore(c, plan);
        }
        catch (Exception ex)
        {
            JournalFailed(nameof(JournalRestartWaiting), ex);
        }
    }

    private void JournalRestartExpired(ConversationState c, RestartPlan plan, bool forced = false)
    {
        if (journal == null)
            return;
        try
        {
            JournalRestartExpiredCore(c, plan, forced);
        }
        catch (Exception ex)
        {
            JournalFailed(nameof(JournalRestartExpired), ex);
        }
    }

    /// <summary>強制更新滿 10 分鐘、還沒停下的對話照立即套用停下（restart.expired 帶 forced，之後的 restart.call 列出被停掉的）</summary>
    private void JournalRestartForced(ConversationState c, RestartPlan plan) => JournalRestartExpired(c, plan, forced: true);

    private void JournalRestartCall(ConversationState c, RestartPlan plan, AgentRestartRequest request, bool stopOnly)
    {
        if (journal == null)
            return;
        try
        {
            JournalRestartCallCore(c, plan, request, stopOnly);
        }
        catch (Exception ex)
        {
            JournalFailed(nameof(JournalRestartCall), ex);
        }
    }

    private void TickJournal(DateTime now)
    {
        if (journal == null)
            return;
        try
        {
            TickJournalCore(now);
        }
        catch (Exception ex)
        {
            JournalFailed(nameof(TickJournal), ex);
        }
    }

    private void JournalAppExit(bool closedByApp)
    {
        if (journal == null)
            return;
        try
        {
            JournalAppExitCore(closedByApp);
        }
        catch (Exception ex)
        {
            JournalFailed(nameof(JournalAppExit), ex);
        }
    }

    private static void JournalFailed(string hook, Exception ex) => Log.Warn($"背景活動日誌（{hook}）出錯，這一筆不記：{ex.Message}");
}
