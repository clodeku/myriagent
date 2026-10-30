using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Schedules;
using Myriagent.Sync;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 排程頁（web-protocol 的 schedules／scheduleWatch／scheduleRefresh／scheduleAction／scheduleAgent，設計 docs/plans/2026-10-26-schedule-manager.md）：
// 這台的 Windows 排程（\Microsoft\ 以外）在背景執行緒讀（IScheduleReader），回 UI 執行緒比對上一次：建立、修改、刪除、啟用、停用記成變更紀錄，
// 這段期間有對話的工具指令碰到排程（ScheduleOriginRules）就記成那個對話做的、排程算「代理建立」；上次執行的結果比對成 Myriagent 自己的執行紀錄
// （runs.jsonl），列入管理的排程新的一串失敗開始時念一句、送系統通知、自動請代理診斷（只診斷不修改；次數與額度有上限，ScheduleRunRules）。
// 什麼時候讀：排程頁開著的視窗（scheduleWatch）有就每 30 秒，沒有每 5 分鐘（維護計時器），一輪結束時這一輪的工具指令碰到排程就立刻讀，
// 使用者按「重新掃描」、快速操作之後也讀。「交給代理」照健康度的作法開草稿、預先填好說明，不自動送出；每個排程一個管理對話，之後沿用。
// 沒掛讀取器（AttachSchedules；測試與 HubHarness 預設）時什麼都不做、init 的 schedules 是 null（網頁不顯示排程頁）。只給這台的主視窗與額外視窗
internal sealed partial class AgentHub
{
    /// <summary>排程頁開著時多久讀一次</summary>
    internal static readonly TimeSpan ScheduleWatchedInterval = TimeSpan.FromSeconds(30);

    /// <summary>排程頁沒開時多久讀一次（還是要知道排程有沒有失敗）</summary>
    internal static readonly TimeSpan ScheduleIdleInterval = TimeSpan.FromMinutes(5);

    /// <summary>工具指令碰到排程的記號留多久（一輪跑很久也夠；一輪結束後再讀一次就清掉）</summary>
    private static readonly TimeSpan ScheduleTouchLifetime = TimeSpan.FromHours(3);

    private static readonly Regex EnabledElement = new(@"<Enabled>\s*(true|false)\s*</Enabled>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private IScheduleReader? scheduleReader;
    private string? scheduleDirectory;
    private ScheduleStore? scheduleStore;
    private ScheduleScan? scheduleScan;
    private bool scheduleScanning;
    private bool scheduleScanAgain;
    private DateTime scheduleLastScanAt = DateTime.MinValue;
    private string? scheduleSentSignature;
    private readonly HashSet<string> scheduleWatchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScheduleTouch> scheduleTouches = new(StringComparer.Ordinal);
    private readonly HashSet<string> scheduleBusyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource scheduleCancel = new();

    /// <summary>現在時間（本機；測試可換）</summary>
    internal Func<DateTime> ScheduleClock { get; set; } = () => DateTime.Now;

    /// <summary>這台的名稱（排程頁與說明裡的電腦名稱；測試可換）</summary>
    internal string ScheduleMachineName { get; set; } = Environment.MachineName;

    /// <summary>一個對話這一輪的工具指令碰到了排程：讀到的變更記成它做的</summary>
    private sealed class ScheduleTouch
    {
        public required string ConversationId;
        public string? Title;
        public string? Provider;
        public readonly List<ScheduleCommandHit> Hits = new();
        public DateTime At;

        /// <summary>這一輪已經結束：結束後的那次讀取做完就清掉</summary>
        public bool TurnEnded;
    }

    /// <summary>掛上排程的讀取器與紀錄資料夾（AppController；只掛一次）。沒掛時不讀、不送</summary>
    internal void AttachSchedules(IScheduleReader reader, string directory)
    {
        scheduleReader = reader ?? throw new ArgumentNullException(nameof(reader));
        scheduleDirectory = directory ?? throw new ArgumentNullException(nameof(directory));
        // Myriagent 的定時訊息（S4，AgentHub.TimedMessages.cs）跟著排程頁一起掛
        LoadTimedMessages();
    }

    private bool SchedulesAttached => scheduleReader != null;

    /// <summary>維護計時器：排程頁開著 30 秒、沒開 5 分鐘讀一次</summary>
    private void TickSchedules()
    {
        if (!SchedulesAttached || disposed || scheduleScanning)
            return;
        scheduleWatchers.RemoveWhere(id => !views.ContainsKey(id));
        var interval = scheduleWatchers.Count > 0 ? ScheduleWatchedInterval : ScheduleIdleInterval;
        if (ScheduleClock() - scheduleLastScanAt >= interval - TimeSpan.FromSeconds(1))
            RequestScheduleScan();
        // 回溯（AgentHub.ScheduleTranscripts.cs）：時候到了在背景掃對話紀錄；匯入之後的外部工作階段改連到 Myriagent 對話
        TickScheduleTranscripts();
        RelinkTranscriptOrigins();
    }

    /// <summary>scheduleWatch：排程頁顯示中（on）或離開（off）。開啟時上次讀取超過 30 秒就馬上讀，否則重送目前的結果</summary>
    private void SetScheduleWatch(IAgentView view, bool on)
    {
        if (!SchedulesAttached)
            return;
        if (!on)
        {
            scheduleWatchers.Remove(view.Id);
            return;
        }
        scheduleWatchers.Add(view.Id);
        if (scheduleScan == null || ScheduleClock() - scheduleLastScanAt >= ScheduleWatchedInterval)
            RequestScheduleScan();
        else
            TryPost(new[] { view }, RenderSchedulesMessage);
    }

    /// <summary>開始讀一次（讀取中就排一次）；第一次讀之前先在背景載入紀錄</summary>
    internal void RequestScheduleScan()
    {
        if (scheduleReader is not { } reader || disposed)
            return;
        if (scheduleScanning)
        {
            scheduleScanAgain = true;
            return;
        }
        scheduleScanning = true;
        scheduleScanAgain = false;
        var directory = scheduleDirectory!;
        var needLoad = scheduleStore == null;
        var ct = scheduleCancel.Token;
        PostSchedules(force: true);
        _ = Task.Run(() =>
            {
                var store = needLoad ? ScheduleStore.Load(directory) : null;
                return (Store: store, Scan: reader.Scan(ct));
            }, ct)
            .ContinueWith(t => host.BeginInvoke(() => OnSchedulesScanned(t)), TaskScheduler.Default);
    }

    private void OnSchedulesScanned(Task<(ScheduleStore? Store, ScheduleScan Scan)> task)
    {
        scheduleScanning = false;
        if (disposed)
            return;
        if (task.IsCompletedSuccessfully)
        {
            scheduleStore ??= task.Result.Store;
            try
            {
                ApplyScheduleScan(task.Result.Scan);
                // 新出現的排程：對話紀錄裡已經找過的出處（AgentHub.ScheduleTranscripts.cs）
                ApplyTranscriptOrigins();
            }
            catch (Exception ex)
            {
                Log.Error("排程：處理掃描結果失敗", ex);
            }
        }
        else if (!task.IsCanceled)
        {
            Log.Error("排程：掃描失敗", task.Exception?.GetBaseException() ?? new InvalidOperationException("排程掃描失敗"));
        }
        scheduleLastScanAt = ScheduleClock();
        PostSchedules(force: false);
        if (scheduleScanAgain)
            RequestScheduleScan();
    }

    /// <summary>定義的指紋：XML 拿掉啟用狀態（啟用、停用另外記）</summary>
    internal static string ScheduleFingerprint(string? xml)
    {
        if (string.IsNullOrEmpty(xml))
            return "";
        var normalized = EnabledElement.Replace(xml, "").Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    /// <summary>
    /// 讀到一次結果：和上一次比出變更（第一次只建基準）、記成碰到排程的對話做的、觀察執行結果、通知新的一串失敗與自動診斷
    /// </summary>
    private void ApplyScheduleScan(ScheduleScan scan)
    {
        var store = scheduleStore;
        if (store == null)
            return;
        var now = ScheduleClock();
        var state = store.State;
        bool baseline = !state.Baseline;
        bool dirty = false;
        if (scan.Error != null && scan.Tasks.Count == 0)
        {
            scheduleScan = scan;
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alerts = new List<(ScheduledTaskInfo Task, ScheduleRunRecord Run)>();
        foreach (var t in scan.Tasks)
        {
            seen.Add(t.Path);
            if (t.Xml == null)
                continue;
            var fp = ScheduleFingerprint(t.Xml);
            if (!baseline)
            {
                if (!state.Fingerprints.TryGetValue(t.Path, out var before))
                    dirty |= RecordScheduleChange(t.Path, "created", now);
                else if (before != fp)
                    dirty |= RecordScheduleChange(t.Path, "changed", now);
                if (state.EnabledStates.TryGetValue(t.Path, out var wasEnabled) && wasEnabled != t.Enabled)
                    dirty |= RecordScheduleChange(t.Path, t.Enabled ? "enabled" : "disabled", now);
            }
            if (!state.Fingerprints.TryGetValue(t.Path, out var old) || old != fp)
            {
                state.Fingerprints[t.Path] = fp;
                dirty = true;
            }
            if (!state.EnabledStates.TryGetValue(t.Path, out var e) || e != t.Enabled)
            {
                state.EnabledStates[t.Path] = t.Enabled;
                dirty = true;
            }

            // 執行紀錄
            var previous = store.LastRunOf(t.Path);
            var run = ScheduleRunRules.Observe(t.Path, previous, t.LastRun, t.LastResult, t.Definition?.Triggers ?? Array.Empty<ScheduleTrigger>(), now);
            if (run != null)
            {
                store.AppendRun(run);
                if (ScheduleRunRules.ShouldAlert(run, previous, now) && ScheduleRunRules.IsManaged(OriginOf(t, scan.Environment)))
                    alerts.Add((t, run));
            }
        }
        // 在工作排程器裡不見了：刪除（讀取失敗的那次不算）
        if (scan.Error == null)
        {
            foreach (var gone in state.Fingerprints.Keys.Where(p => !seen.Contains(p)).ToList())
            {
                if (!baseline)
                    RecordScheduleChange(gone, "deleted", now);
                state.Fingerprints.Remove(gone);
                state.EnabledStates.Remove(gone);
                dirty = true;
            }
        }
        if (baseline)
        {
            state.Baseline = true;
            dirty = true;
        }

        // 碰過排程的對話：這一輪已經結束的，結束後這次讀完就清掉；太久的也清掉
        foreach (var (id, touch) in scheduleTouches.ToList())
        {
            if (touch.TurnEnded || now - touch.At > ScheduleTouchLifetime)
                scheduleTouches.Remove(id);
        }
        scheduleScan = scan;
        if (dirty)
            store.Save();
        foreach (var (t, run) in alerts)
            AlertScheduleFailure(t, run, scan.Environment);
    }

    /// <summary>記一筆變更；這段期間有對話的工具指令碰到排程就記成它做的（名稱對得上的優先），建立與修改另外記成來源</summary>
    private bool RecordScheduleChange(string path, string kind, DateTime now)
    {
        var store = scheduleStore!;
        var touch = FindScheduleTouch(path);
        var change = new ScheduleChangeEntry
        {
            Path = path,
            At = now,
            Kind = kind,
            ConversationId = touch?.ConversationId,
            Title = touch?.Title,
            Provider = touch?.Provider,
        };
        store.AddChange(change);
        if (touch != null && kind is "created" or "changed" && !store.State.Origins.ContainsKey(path))
        {
            store.State.Origins[path] = new ScheduleOriginEntry
            {
                Source = "myriagent",
                Verb = kind,
                At = now,
                ConversationId = touch.ConversationId,
                Title = touch.Title,
                Provider = touch.Provider,
            };
        }
        if (kind == "deleted")
        {
            store.State.Origins.Remove(path);
            store.State.Links.Remove(path);
        }
        return true;
    }

    private ScheduleTouch? FindScheduleTouch(string path)
    {
        if (scheduleTouches.Count == 0)
            return null;
        var named = scheduleTouches.Values.Where(t => t.Hits.Any(h => ScheduleOriginRules.NameMatches(h.Name, path))).OrderByDescending(t => t.At).FirstOrDefault();
        if (named != null)
            return named;
        // 看不出名稱的（變數、COM）：只有一個對話在碰排程時才算它的
        var unnamed = scheduleTouches.Values.Where(t => t.Hits.Any(h => h.Name == null && ScheduleOriginRules.Mutates(h.Verb))).ToList();
        return unnamed.Count == 1 ? unnamed[0] : null;
    }

    /// <summary>工具開始時（AgentHub.Events.cs）：指令碰到排程就記下這個對話（副本不記：工具是在別台執行的）</summary>
    private void OnScheduleToolStarted(ConversationState c, AgentChatItem item)
    {
        if (!SchedulesAttached || c.IsReplica)
            return;
        var text = item.Title + "\n" + item.Detail;
        if (!ScheduleOriginRules.MayTouch(text))
            return;
        var hits = ScheduleOriginRules.Parse(text);
        if (hits.Count == 0)
            return;
        if (!scheduleTouches.TryGetValue(c.Meta.Id, out var touch) || touch.TurnEnded)
            scheduleTouches[c.Meta.Id] = touch = new ScheduleTouch { ConversationId = c.Meta.Id };
        touch.Title = c.Meta.Title;
        touch.Provider = c.Meta.Provider.ToString();
        touch.At = ScheduleClock();
        foreach (var h in hits)
        {
            if (!touch.Hits.Contains(h))
                touch.Hits.Add(h);
        }
    }

    /// <summary>一輪結束（HookTurnCompleted）：這一輪碰過排程就立刻重讀，讀完清掉記號</summary>
    private void OnSchedulesTurnCompleted(ConversationState c)
    {
        if (!SchedulesAttached || !scheduleTouches.TryGetValue(c.Meta.Id, out var touch))
            return;
        touch.Title = c.Meta.Title;
        touch.TurnEnded = true;
        RequestScheduleScan();
    }

    /// <summary>排程的分類：不再列出 → 有證據 → 納入管理 → 規則判斷</summary>
    private ScheduleOrigin OriginOf(ScheduledTaskInfo t, ScheduleEnvironment env)
    {
        var state = scheduleStore?.State;
        if (state != null && state.Ignored.Contains(t.Path, StringComparer.OrdinalIgnoreCase))
            return ScheduleOrigin.Ignored;
        if (state != null && state.Origins.ContainsKey(t.Path))
            return ScheduleOrigin.Agent;
        if (state != null && state.Adopted.Contains(t.Path, StringComparer.OrdinalIgnoreCase))
            return ScheduleOrigin.Adopted;
        if (t.Definition == null)
            return ScheduleOrigin.Other;
        return ScheduleClassifyRules.Classify(new ScheduleClassifyInput(t.Path, t.Definition.Author, t.Definition.Actions, t.RepoRoot != null, env));
    }

    /// <summary>列入管理的排程新的一串失敗：念一句、系統通知、自動請代理診斷</summary>
    private void AlertScheduleFailure(ScheduledTaskInfo t, ScheduleRunRecord run, ScheduleEnvironment env)
    {
        var result = ScheduleResultRules.Classify(run.Code);
        Log.Info($"排程 {t.Path} 失敗：{result.Text}（{run.At:yyyy-MM-dd HH:mm:ss}）");
        if (settings.VoiceNotifications)
            host.Speak($"{ScheduleMachineName} 的排程 {t.Name} 失敗，{result.Text}", VoiceTuningSetting, null);
        if (settings.Notifications)
            host.Notify($"排程失敗：{t.Name}", $"{run.At:M/d HH:mm} {result.Text}", null);
        AutoDiagnoseSchedule(t, run, env);
    }

    /// <summary>自動請代理診斷（只診斷不修改）：管理對話在就送進去（執行中排進佇列），不在就開一個；條件不符時記下原因給明細顯示</summary>
    private void AutoDiagnoseSchedule(ScheduledTaskInfo t, ScheduleRunRecord run, ScheduleEnvironment env)
    {
        var store = scheduleStore!;
        var state = store.State;
        var now = ScheduleClock();
        state.Diagnoses = ScheduleRunRules.Prune(state.Diagnoses.Select(d => new ScheduleDiagnoseRecord(d.Path, d.At, d.ConversationId)), now)
            .Select(d => new ScheduleDiagnoseEntry { Path = d.Path, At = d.At, ConversationId = d.ConversationId }).ToList();
        var existing = ManagementConversationOf(t.Path);
        var account = existing != null ? accounts.Find(existing.Meta.AccountId) : PickScheduleAccount(null);
        var block = ScheduleRunRules.DiagnoseBlock(t.Path, OriginOf(t, env), settings.ScheduleAutoDiagnose,
            state.NoAutoDiagnose.Contains(t.Path, StringComparer.OrdinalIgnoreCase),
            state.Diagnoses.Select(d => new ScheduleDiagnoseRecord(d.Path, d.At, d.ConversationId)).ToList(), now,
            account != null && ScheduleAccountUsable(account), account != null ? WeeklyRemaining(account) : null);
        if (block != null)
        {
            state.DiagnoseBlocks[t.Path] = block;
            store.Save();
            Log.Info($"排程 {t.Path} 不自動診斷：{block}");
            return;
        }
        var brief = BriefOf(t, env);
        var prompt = ScheduleAgentPromptRules.Build("auto", brief);
        var display = ScheduleAgentPromptRules.AutoDisplay(brief, run);
        var echo = new ScheduleEchoView(this);
        var c = existing ?? CreateManagementConversation(echo, account!, t, brief.WorkFolder);
        // 能規劃的代理用規劃模式（只讀取與分析、最後給計畫）；Codex 沒有，只靠說明約束
        if (AgentPermissionModes.For(c.Meta.Provider).Any(o => o.Mode == AgentPermissionMode.Plan))
            SetOption(echo, c, "permission", nameof(AgentPermissionMode.Plan), persistAccountDefaults: false);
        if (c.Running)
        {
            c.Queue.Add(new QueuedMessage { Text = prompt, DisplayText = display });
            PostComposer(c);
        }
        else
        {
            _ = SendAsync(echo, c, prompt, displayText: display);
        }
        state.Diagnoses.Add(new ScheduleDiagnoseEntry { Path = t.Path, At = now, ConversationId = c.Meta.Id });
        state.DiagnoseBlocks.Remove(t.Path);
        store.Save();
        Log.Info($"排程 {t.Path} 已自動請代理診斷（對話 {c.Meta.Id}）");
    }

    /// <summary>自動診斷送出用的檢視：不在視窗清單裡，提示轉給所有視窗（同遠端新對話的 RemoteCreateEchoView）</summary>
    private sealed class ScheduleEchoView(AgentHub hub) : IAgentView
    {
        public string Id { get; } = "schedule:auto";

        public bool IsPopout => false;

        public void PostToWeb(JObject message)
        {
            if (message.Value<string>("type") is "toast" or "sendRejected")
                hub.PostToAll(message);
        }
    }

    /// <summary>開一個正式的管理對話（自動診斷用；不是草稿、存檔、記成這個排程的管理對話）</summary>
    private ConversationState CreateManagementConversation(IAgentView view, AgentAccount account, ScheduledTaskInfo t, string? folder)
    {
        var c = CreateDraft(view, account);
        c.ViewIds.Remove(view.Id);
        if (folder != null && Directory.Exists(folder))
            c.Meta.WorkingDirectory = folder;
        var now = DateTime.Now;
        c.IsDraft = false;
        c.Meta.Title = $"排程 {t.Name} 的管理";
        c.Meta.CreatedAt = now;
        c.Meta.UpdatedAt = now;
        c.LastActivity = now;
        SaveConversation(c, saveItems: true);
        MarkListDirty();
        MarkAccountsDirty();
        scheduleStore!.State.Links[t.Path] = c.Meta.Id;
        return c;
    }

    /// <summary>這個排程的管理對話（還在、不是副本）；草稿也算（使用者剛開還沒送出）</summary>
    private ConversationState? ManagementConversationOf(string path)
        => scheduleStore?.State.Links.TryGetValue(path, out var id) == true && conversations.TryGetValue(id, out var c) && !c.IsReplica ? c : null;

    /// <summary>交給代理用的帳戶：指定的、排程頁上次用的、第一個可用的 Claude、第一個可用的</summary>
    private AgentAccount? PickScheduleAccount(string? requested)
    {
        var ready = accounts.Accounts.Where(ScheduleAccountUsable).ToList();
        return ready.FirstOrDefault(a => a.Id == requested)
               ?? ready.FirstOrDefault(a => a.Id == scheduleStore?.State.AccountId)
               ?? ready.FirstOrDefault(a => a.Provider == AgentProvider.Claude)
               ?? ready.FirstOrDefault();
    }

    private bool ScheduleAccountUsable(AgentAccount a)
        => accounts.GetState(a.Id).Status is AccountStatus.Ready or AccountStatus.ApiKey
           && !(exhaustedAccounts.TryGetValue(a.Id, out var until) && until > DateTimeOffset.Now);

    /// <summary>每週額度剩下的百分比（不知道是 null；有額度窗鎖住了是 0）</summary>
    private double? WeeklyRemaining(AgentAccount a)
    {
        var usage = accounts.GetUsage(a.Id);
        if (usage == null)
            return null;
        if (usage.Windows.Any(w => w.Locked))
            return 0;
        var weekly = usage.Windows.FirstOrDefault(w => w.Key == UsageRules.WeeklyKey);
        return weekly == null ? null : Math.Max(0, 100 - weekly.Percent);
    }

    private ScheduleBrief BriefOf(ScheduledTaskInfo t, ScheduleEnvironment env)
    {
        var state = scheduleStore!.State;
        state.Origins.TryGetValue(t.Path, out var origin);
        var changes = state.Changes.Where(c => c.Path.Equals(t.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        var folder = t.Definition != null ? ScheduleClassifyRules.WorkFolder(t.Definition.Actions, env) : null;
        return new ScheduleBrief(t, ScheduleMachineName, OriginOf(t, env), origin, scheduleStore.RunsOf(t.Path), changes, folder ?? t.RepoRoot);
    }

    /// <summary>scheduleAction：run、stop、enable、disable 在背景經讀取器做，做完重讀；ignore、unignore、adopt、unadopt、autoDiagnose 只改這台的選擇</summary>
    private void ScheduleAction(IAgentView view, string? path, string? action, bool? on)
    {
        if (scheduleReader is not { } reader || scheduleStore is not { } store || string.IsNullOrWhiteSpace(path) || action == null)
            return;
        var t = scheduleScan?.Tasks.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (t == null)
        {
            Toast("找不到這個排程，請重新掃描", isError: true, view);
            return;
        }
        var state = store.State;
        void Toggle(List<string> list, bool add)
        {
            list.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (add)
                list.Add(t.Path);
        }
        switch (action)
        {
            case "run":
            case "stop":
            case "enable":
            case "disable":
            {
                if (!scheduleBusyPaths.Add(t.Path))
                    return;
                PostSchedules(force: true);
                var target = t.Path;
                _ = Task.Run(() =>
                    {
                        switch (action)
                        {
                            case "run":
                                reader.Run(target);
                                break;
                            case "stop":
                                reader.Stop(target);
                                break;
                            default:
                                reader.SetEnabled(target, action == "enable");
                                break;
                        }
                    })
                    .ContinueWith(task => host.BeginInvoke(() =>
                    {
                        scheduleBusyPaths.Remove(target);
                        if (disposed)
                            return;
                        if (task.Exception?.GetBaseException() is { } ex)
                        {
                            Log.Error($"排程：{action} {target} 失敗", ex);
                            Toast($"排程 {t.Name}：{ex.Message}", isError: true, view);
                        }
                        else
                        {
                            Toast(action switch { "run" => $"已開始執行 {t.Name}", "stop" => $"已停止 {t.Name}", "enable" => $"已啟用 {t.Name}", _ => $"已停用 {t.Name}" }, view: view);
                        }
                        RequestScheduleScan();
                    }), TaskScheduler.Default);
                return;
            }
            case "ignore":
                Toggle(state.Ignored, true);
                break;
            case "unignore":
                Toggle(state.Ignored, false);
                break;
            case "adopt":
                Toggle(state.Adopted, true);
                break;
            case "unadopt":
                Toggle(state.Adopted, false);
                break;
            case "autoDiagnose":
                Toggle(state.NoAutoDiagnose, on == false);
                break;
            case "importOrigin":
                // 出處是外部的工作階段：匯入成 Myriagent 對話（AgentHub.ScheduleTranscripts.cs）
                ImportScheduleOrigin(view, t.Path, null);
                return;
            default:
                return;
        }
        store.Save();
        PostSchedules(force: true);
    }

    /// <summary>
    /// scheduleAgent：交給代理。有 path 的以那個排程的管理對話（沒有就用選的帳戶開草稿，資料夾是排程的工作資料夾）、預先填好說明；
    /// 不自動送出。task 是 ScheduleAgentPromptRules 的種類（不含 auto）或 create（新增排程：沒有 path，在選的資料夾開草稿）
    /// </summary>
    private void ScheduleAgent(IAgentView view, string? path, string? task, string? accountId, string? folder, string? paneId)
    {
        if (!SchedulesAttached || scheduleStore is not { } store || task == null)
            return;
        if (task == "create")
        {
            var account = PickScheduleAccount(accountId);
            if (account == null)
            {
                Toast("沒有可用的帳戶，請先登入一個帳戶", isError: true, view);
                return;
            }
            store.State.AccountId = account.Id;
            store.Save();
            var draft = CreateDraft(view, account);
            if (!string.IsNullOrWhiteSpace(folder))
                draft.Meta.WorkingDirectory = folder;
            composerDrafts[draft.Meta.Id] = ScheduleCreatePrompt(ScheduleMachineName);
            PostConversation(view, draft, paneId);
            MarkAccountsDirty();
            return;
        }
        if (!ScheduleAgentPromptRules.IsTask(task) || task == "auto")
            return;
        var t = scheduleScan?.Tasks.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (t == null)
        {
            Toast("找不到這個排程，請重新掃描", isError: true, view);
            return;
        }
        var brief = BriefOf(t, scheduleScan!.Environment);
        var prompt = ScheduleAgentPromptRules.Build(task, brief);
        if (ManagementConversationOf(t.Path) is { } existing)
        {
            composerDrafts[existing.Meta.Id] = prompt;
            Open(view, existing, paneId);
            return;
        }
        var chosen = PickScheduleAccount(accountId);
        if (chosen == null)
        {
            Toast("沒有可用的帳戶，請先登入一個帳戶", isError: true, view);
            return;
        }
        store.State.AccountId = chosen.Id;
        var state = CreateDraft(view, chosen);
        if (brief.WorkFolder != null)
            state.Meta.WorkingDirectory = brief.WorkFolder;
        state.Meta.Title = $"排程 {t.Name} 的管理";
        store.State.Links[t.Path] = state.Meta.Id;
        store.Save();
        composerDrafts[state.Meta.Id] = prompt;
        PostConversation(view, state, paneId);
        MarkAccountsDirty();
    }

    internal static string ScheduleCreatePrompt(string machine)
        => $"這是 Clode Myriagent 排程頁交給你的工作：幫我在這台電腦（{machine}）建立一個 Windows 排程。\n\n"
           + "## 要做的事\n\n（請在這裡寫要定時做什麼、多久一次，例如「每天 03:00 備份這個資料夾的資料庫」）\n\n"
           + "## 規則\n\n"
           + "- 用 `schtasks` 或 PowerShell 的 Register-ScheduledTask 建立；要執行的腳本放在這個工作資料夾（有 git 就一起提交）。\n"
           + "- 預設以目前的使用者、只在登入時執行；要「不論是否登入」、系統管理員權限或電腦睡眠時喚醒，先問我。\n"
           + "- 腳本要寫記錄檔（失敗時找得到原因），結束碼 0 才代表成功。\n"
           + "- 建好之後手動執行一次確認成功；Myriagent 會在這一輪結束後自動掃描，排程頁會顯示這個排程與這個對話。\n";

    /// <summary>送給這台的主視窗與額外視窗（彈出視窗、同步鏡像、瀏覽器版不送）；force＝內容沒變也送（狀態改了：讀取中、快速操作中）</summary>
    private void PostSchedules(bool force)
    {
        if (!SchedulesAttached)
            return;
        var message = RenderSchedulesMessage();
        // 多電腦同步的摘要（AgentHub.ScheduleRemote.cs）：沒有視窗也照樣更新
        UpdateScheduleSyncSnapshot((JObject)message["schedules"]!);
        var targets = views.Values.Where(v => v.Id != SyncViewId && !v.IsPopout && v is not RemoteResponseView).ToList();
        if (targets.Count == 0)
            return;
        var signature = message.ToString(Newtonsoft.Json.Formatting.None);
        if (!force && signature == scheduleSentSignature)
            return;
        scheduleSentSignature = signature;
        foreach (var v in targets)
            v.PostToWeb(message);
    }

    private JObject RenderSchedulesMessage() => new() { ["type"] = "schedules", ["schedules"] = RenderSchedules() };

    /// <summary>init 與 schedules 訊息的 schedules（ScheduleView）；沒掛讀取器是 null</summary>
    internal JObject? RenderSchedules()
    {
        if (!SchedulesAttached)
            return null;
        var scan = scheduleScan;
        var now = ScheduleClock();
        var entries = new JArray();
        int failing = 0;
        if (scan != null && scheduleStore != null)
        {
            foreach (var t in scan.Tasks)
            {
                var entry = RenderScheduleEntry(t, scan.Environment, now);
                entries.Add(entry);
                // last 沒有結果時是 JSON 的 null（JValue，不是 C# 的 null）：先確認是物件
                if (entry.Value<bool>("managed") && entry["last"] is JObject last && last.Value<string>("level") == "fail")
                    failing++;
            }
        }
        return new JObject
        {
            ["machine"] = ScheduleMachineName,
            ["loaded"] = scan != null,
            ["scanning"] = scheduleScanning,
            ["scannedAt"] = scan != null ? Ms(scan.ScannedAt) : null,
            ["error"] = scan?.Error,
            ["entries"] = entries,
            ["failing"] = failing,
            ["autoDiagnose"] = settings.ScheduleAutoDiagnose,
            ["accountId"] = scheduleStore?.State.AccountId,
            // Myriagent 的定時訊息（AgentHub.TimedMessages.cs）
            ["timed"] = RenderTimed(),
            // 其他電腦最後回報的（S3，AgentHub.ScheduleRemote.cs）；未配對是空的
            ["devices"] = ScheduleSyncRules.DevicesView(otherSchedules),
        };
    }

    private JObject RenderScheduleEntry(ScheduledTaskInfo t, ScheduleEnvironment env, DateTime now)
    {
        var store = scheduleStore!;
        var state = store.State;
        var d = t.Definition;
        var origin = OriginOf(t, env);
        var runs = store.RunsOf(t.Path);
        var last = runs.Count > 0 ? runs[^1] : null;
        // 還沒有自己的紀錄（剛裝好、還沒執行過）：用工作排程器的上次結果
        var lastResult = last != null ? ScheduleResultRules.Classify(last.Code)
            : t.LastRun != null && ScheduleResultRules.IsFinal(t.LastResult) ? ScheduleResultRules.Classify(t.LastResult) : null;
        state.Origins.TryGetValue(t.Path, out var originEntry);
        var management = ManagementConversationOf(t.Path);
        var diagnosis = state.Diagnoses.Where(x => x.Path.Equals(t.Path, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.At).FirstOrDefault();
        var workFolder = d != null ? ScheduleClassifyRules.WorkFolder(d.Actions, env) : null;
        return new JObject
        {
            ["path"] = t.Path,
            ["name"] = t.Name,
            ["folder"] = t.Folder,
            ["state"] = ScheduleStateName(t),
            ["enabled"] = t.Enabled,
            ["busy"] = scheduleBusyPaths.Contains(t.Path),
            ["origin"] = OriginName(origin),
            ["managed"] = ScheduleRunRules.IsManaged(origin),
            ["trigger"] = d != null ? ScheduleTriggerRules.Describe(d.Triggers) : null,
            ["action"] = d != null ? ScheduleClassifyRules.ActionText(d.Actions) : null,
            ["workFolder"] = workFolder ?? t.RepoRoot,
            ["repoRoot"] = t.RepoRoot,
            ["runAs"] = d != null ? ScheduleAgentPromptRules.RunAsText(d) : null,
            ["conditions"] = d != null ? new JArray(ScheduleConditions(d)) : new JArray(),
            ["author"] = d?.Author,
            ["description"] = d?.Description,
            ["created"] = d?.Created is { } created ? Ms(created) : null,
            ["lastRun"] = t.LastRun is { } lr ? Ms(lr) : null,
            ["last"] = lastResult != null ? new JObject { ["code"] = lastResult.Code, ["level"] = lastResult.LevelName, ["text"] = lastResult.Text } : null,
            ["nextRun"] = t.NextRun is { } nr && t.Enabled ? Ms(nr) : null,
            ["missed"] = t.MissedRuns,
            ["streak"] = ScheduleRunRules.ConsecutiveFailures(runs),
            ["runs"] = new JArray(runs.Reverse().Take(30).Select(r =>
            {
                var c = ScheduleResultRules.Classify(r.Code);
                return new JObject { ["at"] = Ms(r.At), ["code"] = r.Code, ["level"] = c.LevelName, ["text"] = c.Text, ["gap"] = r.Gap };
            })),
            ["upcoming"] = new JArray((d != null && t.Enabled ? ScheduleTriggerRules.Occurrences(d.Triggers, now, now.AddHours(24)) : Array.Empty<DateTime>()).Select(x => (JToken)Ms(x))),
            ["provenance"] = originEntry == null ? null : new JObject
            {
                ["source"] = originEntry.Source,
                ["verb"] = originEntry.Verb,
                ["at"] = Ms(originEntry.At),
                ["conversationId"] = originEntry.ConversationId,
                ["title"] = originEntry.Title,
                ["provider"] = originEntry.Provider,
                ["exists"] = originEntry.ConversationId != null && conversations.ContainsKey(originEntry.ConversationId),
                ["importable"] = originEntry.TranscriptPath != null && originEntry.SessionId != null,
                ["cwd"] = originEntry.Cwd,
            },
            ["management"] = management == null ? null : new JObject
            {
                ["conversationId"] = management.Meta.Id,
                ["title"] = management.Meta.Title,
                ["provider"] = management.Meta.Provider.ToString(),
                ["running"] = management.Running,
                ["isDraft"] = management.IsDraft,
            },
            ["changes"] = new JArray(state.Changes.Where(c => c.Path.Equals(t.Path, StringComparison.OrdinalIgnoreCase)).Reverse().Take(10).Select(c => new JObject
            {
                ["at"] = Ms(c.At),
                ["kind"] = c.Kind,
                ["text"] = ScheduleAgentPromptRules.ChangeText(c.Kind),
                ["conversationId"] = c.ConversationId,
                ["title"] = c.Title,
                ["provider"] = c.Provider,
                ["exists"] = c.ConversationId != null && conversations.ContainsKey(c.ConversationId),
            })),
            ["autoDiagnose"] = !state.NoAutoDiagnose.Contains(t.Path, StringComparer.OrdinalIgnoreCase),
            ["diagnoseBlock"] = state.DiagnoseBlocks.TryGetValue(t.Path, out var block) ? block : null,
            ["lastDiagnosis"] = diagnosis == null ? null : new JObject
            {
                ["at"] = Ms(diagnosis.At),
                ["conversationId"] = diagnosis.ConversationId,
                ["exists"] = diagnosis.ConversationId != null && conversations.ContainsKey(diagnosis.ConversationId),
            },
            ["error"] = t.Error,
            ["xml"] = t.Xml,
        };
    }

    private static long Ms(DateTime local) => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUnixTimeMilliseconds();

    internal static string OriginName(ScheduleOrigin origin) => origin switch
    {
        ScheduleOrigin.Agent => "agent",
        ScheduleOrigin.Adopted => "adopted",
        ScheduleOrigin.Suspected => "suspected",
        ScheduleOrigin.Other => "other",
        ScheduleOrigin.Ignored => "ignored",
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null),
    };

    private static string ScheduleStateName(ScheduledTaskInfo t)
    {
        if (t.Definition == null)
            return "error";
        return t.State switch
        {
            ScheduledTaskState.Running => "running",
            ScheduledTaskState.Disabled => "disabled",
            ScheduledTaskState.Queued => "queued",
            ScheduledTaskState.Ready => "ready",
            _ => t.Enabled ? "ready" : "disabled",
        };
    }

    /// <summary>明細的「條件」：會讓排程不執行或提早停止的設定</summary>
    internal static IEnumerable<string> ScheduleConditions(ScheduleDefinition d)
    {
        if (d.DisallowStartIfOnBatteries)
            yield return "使用電池時不啟動";
        if (d.StopIfGoingOnBatteries)
            yield return "改用電池時停止";
        if (d.RunOnlyIfNetworkAvailable)
            yield return "有網路才執行";
        if (d.RunOnlyIfIdle)
            yield return "電腦閒置時才執行";
        if (d.StartWhenAvailable)
            yield return "錯過時盡快補跑";
        if (d.WakeToRun)
            yield return "喚醒電腦來執行";
        if (d.TimeLimit is { } limit)
            yield return "逾時" + ScheduleTriggerRules.SpanText(limit);
        switch (d.MultipleInstances)
        {
            case "Parallel":
                yield return "可以同時執行多個";
                break;
            case "Queue":
                yield return "上一次沒結束就排隊";
                break;
            case "StopExisting":
                yield return "上一次沒結束就先停掉";
                break;
        }
        if (d.Hidden)
            yield return "隱藏";
    }

    /// <summary>中樞關閉：停掉進行中的掃描</summary>
    private void StopSchedules()
    {
        StopTimed();
        try
        {
            scheduleCancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
