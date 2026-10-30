using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Schedules;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// Myriagent 的定時訊息（排程頁的 S4，設計 docs/plans/2026-10-26-schedule-manager.md 第 8 節；擁有者 2026-10-26 決定這一版一起做）：
// 在指定的時間對某個對話送一則訊息（或每次開新對話），取代 Myriagent 停用的 Claude CronCreate（程序關掉就失效），四種代理都能用、app 重開也不會丟。
// 定義在 <資料根目錄>\schedules\timed.json；計時器排到最近的一則（最多一小時，維護計時器每 30 秒也補看一次，睡眠或改時間之後照樣接上），
// 到點時照一般的送出（對話執行中就排進佇列，不插進這一輪）；錯過的重複訊息只補一次、一次性的錯過超過 12 小時不補（TimedMessageRules）。
// 建立：代理以 myriagent-schedule（AgentHub.RemoteExec 的同一條本機管道、同一個對話權杖）、使用者在排程頁新增（timedSave）。只在 app 開著時送。
// 跟著排程頁一起掛（AttachSchedules）；沒掛時什麼都不做
internal sealed partial class AgentHub
{
    private List<TimedMessage>? timedMessages;
    private System.Threading.Timer? timedTimer;
    private DateTime timedReadyAt = DateTime.MinValue;

    /// <summary>送出之後在等這一輪結束的（對話 id → 那一則、那一筆紀錄）</summary>
    private readonly Dictionary<string, (TimedMessage Message, TimedRun Run, DateTime At)> timedPending = new(StringComparer.Ordinal);

    /// <summary>myriagent-schedule.exe 在 app 的資料夾（給代理說明用；AppController 設）</summary>
    internal bool TimedToolAvailable { get; set; }

    /// <summary>計時器第一次可以送的時間（app 剛啟動時先等帳戶與畫面就緒）</summary>
    internal static readonly TimeSpan TimedStartupDelay = TimeSpan.FromSeconds(20);

    private string TimedPath => Path.Combine(scheduleDirectory!, "timed.json");

    /// <summary>AttachSchedules 之後：讀定時訊息、排計時器（app 剛啟動，先等一下才送）</summary>
    private void LoadTimedMessages()
    {
        timedMessages = JsonFile.Read<List<TimedMessage>>(TimedPath) ?? new List<TimedMessage>();
        foreach (var m in timedMessages)
            m.Runs ??= new List<TimedRun>();
        timedReadyAt = ScheduleClock() + TimedStartupDelay;
        timedTimer = new System.Threading.Timer(_ => host.BeginInvoke(TickTimed), null, Timeout.Infinite, Timeout.Infinite);
        ArmTimedTimer();
    }

    private void SaveTimedMessages()
    {
        if (timedMessages == null)
            return;
        try
        {
            JsonFile.Write(TimedPath, timedMessages);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("定時訊息：存 timed.json 失敗", ex);
        }
    }

    /// <summary>計時器排到最近一則（最多一小時；還在啟動的等待期就排到等待結束）</summary>
    private void ArmTimedTimer()
    {
        if (timedTimer == null || timedMessages == null || disposed)
            return;
        var now = ScheduleClock();
        var next = timedMessages.Select(TimedMessageRules.NextDue).Where(d => d != null).Select(d => d!.Value).DefaultIfEmpty(DateTime.MaxValue).Min();
        if (next < timedReadyAt)
            next = timedReadyAt;
        var wait = next == DateTime.MaxValue ? TimeSpan.FromHours(1) : next - now;
        if (wait < TimeSpan.Zero)
            wait = TimeSpan.Zero;
        if (wait > TimeSpan.FromHours(1))
            wait = TimeSpan.FromHours(1);
        try
        {
            timedTimer.Change(wait + TimeSpan.FromMilliseconds(200), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>到點的送出（計時器、維護計時器；UI 執行緒）</summary>
    internal void TickTimed()
    {
        if (timedMessages == null || disposed)
            return;
        var now = ScheduleClock();
        if (now < timedReadyAt)
        {
            ArmTimedTimer();
            return;
        }
        bool changed = false;
        foreach (var m in timedMessages.ToList())
        {
            var (due, scheduled, late) = TimedMessageRules.Decide(m, now);
            switch (due)
            {
                case TimedDue.Fire:
                    FireTimed(m, scheduled, late, now);
                    changed = true;
                    break;
                case TimedDue.Missed:
                    m.LastScheduledAt = scheduled;
                    m.Done = true;
                    TimedMessageRules.AddRun(m, new TimedRun { ScheduledAt = scheduled, Outcome = "missed" });
                    Toast($"定時訊息「{TimedMessageRules.LabelOf(m)}」原定 {scheduled:M/d HH:mm}，錯過太久沒有補送");
                    changed = true;
                    break;
            }
        }
        // 等一輪結束的記號太久沒結束（被拒、對話被刪）就放掉
        foreach (var (id, p) in timedPending.ToList())
        {
            if (now - p.At > TimeSpan.FromHours(6))
                timedPending.Remove(id);
        }
        if (changed)
        {
            SaveTimedMessages();
            PostSchedules(force: true);
        }
        ArmTimedTimer();
    }

    /// <summary>送出一則：對話在就照一般的送出（執行中排進佇列）；每次開新對話的先開一個</summary>
    private void FireTimed(TimedMessage m, DateTime scheduled, bool late, DateTime now)
    {
        m.LastScheduledAt = scheduled;
        if (m.Cron == null)
            m.Done = true;
        var run = new TimedRun { ScheduledAt = scheduled, FiredAt = now };
        TimedMessageRules.AddRun(m, run);
        try
        {
            var echo = new ScheduleEchoView(this);
            ConversationState? c;
            if (m.NewConversation)
            {
                c = CreateTimedConversation(echo, m, now, out var problem);
                if (c == null)
                {
                    run.Outcome = "error";
                    run.Error = problem;
                    Toast($"定時訊息「{TimedMessageRules.LabelOf(m)}」沒有送出：{problem}", isError: true);
                    return;
                }
            }
            else if (m.ConversationId == null || !conversations.TryGetValue(m.ConversationId, out c) || c.IsReplica || c.IsDraft)
            {
                run.Outcome = "error";
                run.Error = "對話已經刪除";
                m.Enabled = false;
                Toast($"定時訊息「{TimedMessageRules.LabelOf(m)}」的對話已經刪除，已停用", isError: true);
                return;
            }
            run.ConversationId = c.Meta.Id;
            var text = TimedMessageRules.AgentText(m, scheduled, late);
            var display = TimedMessageRules.DisplayText(m, late);
            if (c.Running)
            {
                c.Queue.Add(new QueuedMessage { Text = text, DisplayText = display });
                PostComposer(c);
                run.Outcome = "queued";
            }
            else
            {
                run.Outcome = "sent";
                timedPending[c.Meta.Id] = (m, run, now);
                _ = SendAsync(echo, c, text, displayText: display);
            }
            Log.Info($"定時訊息 {m.Id} 送進對話 {c.Meta.Id}（原定 {scheduled:yyyy-MM-dd HH:mm}{(late ? "，補送" : "")}）");
        }
        catch (Exception ex)
        {
            Log.Error($"定時訊息 {m.Id} 送出失敗", ex);
            run.Outcome = "error";
            run.Error = ex.Message;
        }
    }

    /// <summary>每次開新對話：照記下的帳戶、模型、推理強度、權限模式、資料夾開一個正式對話</summary>
    private ConversationState? CreateTimedConversation(IAgentView view, TimedMessage m, DateTime now, out string? problem)
    {
        problem = null;
        var account = accounts.Find(m.AccountId);
        if (account == null)
        {
            problem = "帳戶已經移除";
            return null;
        }
        if (accounts.GetState(account.Id).Status is AccountStatus.NotLoggedIn or AccountStatus.NotInstalled)
        {
            problem = $"「{account.Label}」{AccountRules.DescribeStatus(account.Provider, accounts.GetState(account.Id).Status)}";
            return null;
        }
        var c = CreateDraft(view, account);
        c.ViewIds.Remove(view.Id);
        if (m.Model != null)
            c.Meta.Model = m.Model;
        if (m.Effort != null)
            c.Meta.Effort = m.Effort;
        if (m.PermissionMode != null && Enum.TryParse<AgentPermissionMode>(m.PermissionMode, out var mode))
            c.Meta.PermissionMode = AgentPermissionModes.Normalize(account.Provider, mode);
        if (m.Folder != null && Directory.Exists(m.Folder))
            c.Meta.WorkingDirectory = m.Folder;
        c.IsDraft = false;
        c.Meta.Title = $"{TimedMessageRules.LabelOf(m)}（{now:M/d HH:mm}）";
        c.Meta.CreatedAt = now;
        c.Meta.UpdatedAt = now;
        c.LastActivity = now;
        SaveConversation(c, saveItems: true);
        MarkListDirty();
        MarkAccountsDirty();
        return c;
    }

    /// <summary>一輪結束（HookTurnCompleted）：定時訊息送出的那一輪記下結果</summary>
    private void OnTimedTurnCompleted(ConversationState c, AgentTurnOutcome outcome)
    {
        if (!timedPending.Remove(c.Meta.Id, out var p))
            return;
        p.Run.Outcome = TimedMessageRules.OutcomeName(outcome == AgentTurnOutcome.Failed, outcome == AgentTurnOutcome.Interrupted);
        SaveTimedMessages();
        PostSchedules(force: true);
    }

    /// <summary>
    /// 新增或修改（排程頁的 timedSave、myriagent-schedule add）：檢查時間、文字、對話；成功回 null 與那一則，失敗回原因。
    /// <paramref name="from"/>＝建立它的對話（代理）；每次開新對話時帳戶、模型、權限模式、資料夾照 <paramref name="template"/>
    /// </summary>
    private (TimedMessage? Message, string? Problem) SaveTimed(string? id, string? text, string? label, string? whenKind, string? whenValue,
        string? conversationId, bool newConversation, ConversationState? from)
    {
        if (timedMessages == null)
            return (null, "這台的 Clode Myriagent 沒有開定時訊息");
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return (null, "少了要送的文字");
        if (text.Length > TimedMessageRules.MaxTextLength)
            return (null, $"文字超過 {TimedMessageRules.MaxTextLength} 字");
        var now = ScheduleClock();
        var (cron, at, problem) = TimedMessageRules.ParseWhen(whenKind ?? "", whenValue, now);
        if (problem != null)
            return (null, problem);
        if (conversationId == null || !conversations.TryGetValue(conversationId, out var target) || target.IsReplica)
            return (null, "找不到要送進的對話");
        if (target.IsDraft && !newConversation)
            return (null, "這個對話還是草稿：先送出第一則再設定時訊息");
        var m = id != null ? timedMessages.FirstOrDefault(x => x.Id == id) : null;
        if (id != null && m == null)
            return (null, "找不到這則定時訊息");
        if (m == null)
        {
            if (timedMessages.Count >= TimedMessageRules.MaxMessages)
                return (null, $"定時訊息最多 {TimedMessageRules.MaxMessages} 則");
            m = new TimedMessage { Id = "tm_" + Guid.NewGuid().ToString("N")[..8], CreatedAt = now };
            if (from != null)
            {
                m.CreatedBy = from.Meta.Id;
                m.CreatedByTitle = from.Meta.Title;
                m.CreatedByProvider = from.Meta.Provider.ToString();
            }
            timedMessages.Add(m);
        }
        m.Text = text;
        m.Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        m.Cron = cron;
        m.At = at;
        m.Enabled = true;
        m.Done = false;
        m.ConversationId = target.Meta.Id;
        m.NewConversation = newConversation;
        // 時間改了：下一次從現在之後算（不補建立以前或改之前的）
        m.LastScheduledAt = now;
        if (newConversation)
        {
            m.AccountId = target.Meta.AccountId;
            m.Model = target.Meta.Model;
            m.Effort = target.Meta.Effort;
            m.PermissionMode = target.Meta.PermissionMode.ToString();
            m.Folder = target.Meta.WorkingDirectory;
        }
        SaveTimedMessages();
        ArmTimedTimer();
        PostSchedules(force: true);
        return (m, null);
    }

    /// <summary>timedAction：run（立即送一次）、pause、resume、delete</summary>
    private string? TimedAction(string? id, string? action)
    {
        if (timedMessages == null)
            return "這台的 Clode Myriagent 沒有開定時訊息";
        var m = timedMessages.FirstOrDefault(x => x.Id == id);
        if (m == null)
            return "找不到這則定時訊息";
        var now = ScheduleClock();
        switch (action)
        {
            case "run":
            {
                // 立即送一次不動下一次的時間
                var keep = (m.LastScheduledAt, m.Done);
                FireTimed(m, now, false, now);
                m.LastScheduledAt = keep.LastScheduledAt;
                m.Done = keep.Done;
                break;
            }
            case "pause":
                m.Enabled = false;
                break;
            case "resume":
                m.Enabled = true;
                m.Done = false;
                m.LastScheduledAt = now;
                break;
            case "delete":
                timedMessages.Remove(m);
                break;
            default:
                return "不認得的操作";
        }
        SaveTimedMessages();
        ArmTimedTimer();
        PostSchedules(force: true);
        return null;
    }

    /// <summary>
    /// myriagent-schedule 的要求（RemoteExecService 的管道收到、轉到 UI 執行緒）：<paramref name="conversationId"/> 是權杖驗過的呼叫者。
    /// op：add（when {kind, value}、text、label、new）、list、remove／pause／resume（id；只能動這個對話建立或送進這個對話的）
    /// </summary>
    internal JObject HandleTimedCli(string conversationId, JObject args)
    {
        JObject Error(string message) => new() { ["ok"] = false, ["message"] = message };
        if (timedMessages == null)
            return Error("這台的 Clode Myriagent 沒有開定時訊息");
        conversations.TryGetValue(conversationId, out var caller);
        var op = args.Value<string>("op");
        switch (op)
        {
            case "add":
            {
                var when = args["when"] as JObject;
                var (m, problem) = SaveTimed(null, args.Value<string>("text"), args.Value<string>("label"), when?.Value<string>("kind"), when?.Value<string>("value"),
                    conversationId, args.Value<bool?>("new") == true, caller);
                if (m == null)
                    return Error(problem!);
                return new JObject { ["ok"] = true, ["message"] = $"已建立定時訊息 {m.Id}：{TimedMessageRules.Describe(m)}，下一次 {TimedMessageRules.NextDue(m):yyyy/M/d HH:mm}", ["item"] = RenderTimedCli(m, conversationId) };
            }
            case "list":
                return new JObject { ["ok"] = true, ["items"] = new JArray(timedMessages.Select(m => RenderTimedCli(m, conversationId))) };
            case "remove":
            case "pause":
            case "resume":
            {
                var m = timedMessages.FirstOrDefault(x => x.Id == args.Value<string>("id"));
                if (m == null)
                    return Error("找不到這則定時訊息（用 myriagent-schedule list 看 id）");
                if (m.ConversationId != conversationId && m.CreatedBy != conversationId)
                    return Error("這則不是這個對話建立的，請使用者在排程頁管理");
                var problem = TimedAction(m.Id, op == "remove" ? "delete" : op);
                return problem != null ? Error(problem) : new JObject { ["ok"] = true, ["message"] = op switch { "remove" => "已刪除", "pause" => "已暫停", _ => "已繼續" } + " " + m.Id };
            }
            default:
                return Error("不認得的操作：用 add、list、remove、pause、resume");
        }
    }

    private JObject RenderTimedCli(TimedMessage m, string caller) => new()
    {
        ["id"] = m.Id,
        ["label"] = TimedMessageRules.LabelOf(m),
        ["when"] = TimedMessageRules.Describe(m),
        ["next"] = TimedMessageRules.NextDue(m) is { } n ? n.ToString("yyyy/M/d HH:mm") : null,
        ["enabled"] = m.Enabled && !m.Done,
        ["newConversation"] = m.NewConversation,
        ["mine"] = m.ConversationId == caller || m.CreatedBy == caller,
        ["conversation"] = m.ConversationId != null && conversations.TryGetValue(m.ConversationId, out var c) ? c.Meta.Title : null,
    };

    /// <summary>排程頁的 timed（ScheduleView.timed）</summary>
    private JArray RenderTimed()
    {
        if (timedMessages == null)
            return new JArray();
        return new JArray(timedMessages.OrderBy(m => TimedMessageRules.NextDue(m) ?? DateTime.MaxValue).Select(m =>
        {
            conversations.TryGetValue(m.ConversationId ?? "", out var target);
            var last = m.Runs.Count > 0 ? m.Runs[^1] : null;
            return new JObject
            {
                ["id"] = m.Id,
                ["label"] = TimedMessageRules.LabelOf(m),
                ["text"] = m.Text,
                ["when"] = TimedMessageRules.Describe(m),
                ["cron"] = m.Cron,
                ["at"] = m.At is { } at ? Ms(at) : null,
                ["enabled"] = m.Enabled,
                ["done"] = m.Done,
                ["newConversation"] = m.NewConversation,
                ["conversationId"] = m.ConversationId,
                ["conversationTitle"] = target?.Meta.Title,
                ["conversationExists"] = target != null,
                ["provider"] = (m.NewConversation ? accounts.Find(m.AccountId)?.Provider : target?.Meta.Provider)?.ToString(),
                ["folder"] = m.NewConversation ? m.Folder : target?.Meta.WorkingDirectory,
                ["nextAt"] = TimedMessageRules.NextDue(m) is { } n ? Ms(n) : null,
                ["upcoming"] = new JArray(TimedMessageRules.Upcoming(m, ScheduleClock(), ScheduleClock().AddHours(24)).Select(x => (JToken)Ms(x))),
                ["createdAt"] = Ms(m.CreatedAt),
                ["createdBy"] = m.CreatedBy == null ? null : new JObject
                {
                    ["conversationId"] = m.CreatedBy,
                    ["title"] = m.CreatedByTitle,
                    ["provider"] = m.CreatedByProvider,
                    ["exists"] = conversations.ContainsKey(m.CreatedBy),
                },
                ["last"] = last == null ? null : new JObject { ["level"] = TimedMessageRules.LevelOf(last.Outcome), ["text"] = TimedMessageRules.OutcomeText(last), ["at"] = Ms(last.FiredAt ?? last.ScheduledAt) },
                ["runs"] = new JArray(m.Runs.AsEnumerable().Reverse().Select(r => new JObject
                {
                    ["at"] = Ms(r.FiredAt ?? r.ScheduledAt),
                    ["scheduledAt"] = Ms(r.ScheduledAt),
                    ["late"] = r.FiredAt is { } f && f - r.ScheduledAt > TimeSpan.FromMinutes(2),
                    ["conversationId"] = r.ConversationId,
                    ["exists"] = r.ConversationId != null && conversations.ContainsKey(r.ConversationId),
                    ["level"] = TimedMessageRules.LevelOf(r.Outcome),
                    ["text"] = TimedMessageRules.OutcomeText(r),
                })),
            };
        }));
    }

    /// <summary>給代理的說明多一段：myriagent-schedule 的用法（接上了而且 app 的資料夾有 myriagent-schedule.exe 才給）</summary>
    private string TimedInstructions()
        => timedMessages != null && TimedToolAvailable ? TimedHubRules.InstructionsHint : string.Empty;

    private void StopTimed()
    {
        timedTimer?.Dispose();
        timedTimer = null;
    }
}

/// <summary>定時訊息在中樞的說明文字</summary>
internal static class TimedHubRules
{
    public const string ToolFileName = "myriagent-schedule.exe";

    public const string InstructionsHint =
        "\n\n要在之後的某個時間或定期讓代理接著做事（例如每天 09:00 整理報表、明天 14:00 檢查部署有沒有成功），用 myriagent-schedule（已在 PATH）建立 Myriagent 的定時訊息：" +
        "`myriagent-schedule add --daily 09:00 -- 要做的事`（也可以 --weekdays HH:mm、--every 30m、--at \"2026-10-27 14:00\"、--cron \"0 9 * * 1-5\"；加 --new 每次開新對話，" +
        "前文才不會越堆越長）、`myriagent-schedule list`、`myriagent-schedule remove <id>`。時間到了 Myriagent 會把文字送進這個對話，app 沒開時錯過的重複訊息只補一次。" +
        "不要為這種事另裝 Windows 排程，除非使用者要的是 app 沒開也要跑的工作。";
}
