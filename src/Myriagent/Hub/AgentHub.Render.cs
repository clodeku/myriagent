using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Rendering;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

internal sealed partial class AgentHub
{
    /// <summary>媒體網址與 /media、/thumb 白名單：項目渲染、輸入框的附件與檔案瀏覽器共用同一份（LocalMediaUrls）</summary>
    private readonly LocalMediaUrls mediaUrls = new();

    private ItemRenderer? itemRenderer;

    /// <summary>項目渲染：Protocol 的共用渲染器（伺服器也用它），網址經 mediaUrls、失敗寫進記錄；第一次渲染時建立</summary>
    private ItemRenderer Renderer => itemRenderer ??= new ItemRenderer(mediaUrls, Log.Error, Log.Warn);

    private BackgroundMarkdown? backgroundMarkdown;

    /// <summary>很長的執行中回覆在背景執行緒轉 Markdown（BackgroundMarkdownRules）；第一次用到時建立，轉好時回 UI 執行緒再送</summary>
    private BackgroundMarkdown BackgroundMarkdownWorker
        => backgroundMarkdown ??= new BackgroundMarkdown((conversationId, itemId) => host.BeginInvoke(() => OnBackgroundMarkdownReady(conversationId, itemId)));

    /// <summary>測試用：背景轉 Markdown 的執行緒（還沒用到是 null）</summary>
    internal BackgroundMarkdown? BackgroundMarkdownForTests => backgroundMarkdown;

    /// <summary>背景轉好了一份（UI 執行緒）：那一筆還在就標成要送、現在就合併送出（不再等 50 ms 的計時器）</summary>
    private void OnBackgroundMarkdownReady(string conversationId, string itemId)
    {
        if (disposed || !conversations.TryGetValue(conversationId, out var c) || c.ViewIds.Count == 0)
            return;
        if (!dirtyItems.TryGetValue(conversationId, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            dirtyItems[conversationId] = set;
        }
        set.Add(itemId);
        FlushNow();
    }

    /// <summary>
    /// 很長的執行中回覆在這一輪合併要做什麼（BackgroundMarkdownRules.Next）：送出背景轉好的回 true（或已經排了、在等）；
    /// 回 false＝照常在 UI 執行緒轉（背景轉換失敗）。<paramref name="keepDirty"/>＝送的是稍早的內容，這一筆留到下一輪再排
    /// </summary>
    private bool FlushInBackground(ConversationState c, AgentChatItem item, List<IAgentView> targets, long now, out bool keepDirty)
    {
        keepDirty = false;
        var worker = BackgroundMarkdownWorker;
        var conversationId = c.Meta.Id;
        var text = item.Text ?? string.Empty;
        var result = worker.Take(conversationId, item.Id);
        switch (BackgroundMarkdownRules.Next(result?.Text, result is { Markdown: null }, text, worker.IsPending(conversationId, item.Id)))
        {
            case BackgroundMarkdownRules.Step.RenderNow:
                return false;
            case BackgroundMarkdownRules.Step.Request:
                worker.Request(conversationId, item.Id, text, GetWorkingDirectory(c));
                return true;
            case BackgroundMarkdownRules.Step.Wait:
                return true;
            default:
                c.StreamFlushedAtMs[item.Id] = now;
                foreach (var link in result!.Links)
                    mediaUrls.AllowMarkdownLink(link);
                var markdown = result.Markdown;
                TryPost(targets, () => new JObject { ["type"] = "item", ["conversationId"] = conversationId, ["item"] = Renderer.Render(item, GetWorkingDirectory(c), markdown) });
                keepDirty = !string.Equals(result.Text, text, StringComparison.Ordinal);
                return true;
        }
    }

    /// <summary>
    /// 媒體端點（/media）只提供畫面上真的出現過的本機檔案（LocalMediaUrls.IsMediaAllowed）。
    /// WebResourceRequested 在 UI 執行緒觸發，與登記同一條執行緒，不需要鎖。
    /// </summary>
    public bool IsMediaAllowed(string path) => mediaUrls.IsMediaAllowed(path);

    /// <summary>縮圖端點（/thumb）只提供登記過的檔案（附件與檔案瀏覽器列出的圖片／影片）</summary>
    public bool IsThumbAllowed(string path) => mediaUrls.IsThumbAllowed(path);

    /// <summary>登記與查詢用的正規化路徑（見 LocalMediaUrls.NormalizePath）</summary>
    internal static string? NormalizePath(string? path) => LocalMediaUrls.NormalizePath(path);

    private string AllowMedia(string path) => mediaUrls.MediaUrl(path);

    /// <param name="version">檔案版本（會被覆寫的檔案才需要，見 AgentMarkdownRenderer.ThumbUrl）</param>
    private string AllowThumb(string path, int size, string? version = null) => mediaUrls.ThumbUrl(path, size, version);

    /// <summary>檔案已不在畫面上（例如移除附件）：撤銷登記</summary>
    private void RevokeMedia(string path) => mediaUrls.Revoke(path);

    /// <summary>實機探針用：登記測試檔並取得網址</summary>
    internal string AllowThumbForProbe(string path, int size) => AllowThumb(path, size);

    internal string AllowMediaForProbe(string path) => AllowMedia(path);

    // ───────────────────────────── 合併送出 ─────────────────────────────

    private void MarkItemDirty(ConversationState c, AgentChatItem item)
    {
        item.UpdatedAt = DateTime.Now;
        c.ItemsUnsaved = true;
        // 卡片的狀態決定清單與帳戶卡的「等待你回應」（例如略過一則非同步提問）：一起更新
        if (item.Kind is AgentItemKind.Question or AgentItemKind.Approval or AgentItemKind.PlanApproval)
        {
            MarkListDirty();
            MarkAccountsDirty();
        }
        // 同步的掛鉤在「沒有檢視就不送畫面」之前：沒有開著的對話也要發布（D47）
        HookItemChanged(c, item);
        if (c.ViewIds.Count == 0)
            return;
        if (!dirtyItems.TryGetValue(c.Meta.Id, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            dirtyItems[c.Meta.Id] = set;
        }
        set.Add(item.Id);
        ScheduleFlush();
    }

    private void MarkHeaderDirty(ConversationState c)
    {
        HookHeaderChanged(c);
        if (c.ViewIds.Count > 0)
        {
            dirtyHeaders.Add(c.Meta.Id);
            ScheduleFlush();
        }
        // 帳戶卡只用到標頭變化裡的對話數（開著、執行中、等你回應）：執行中的工具每開始、結束一次標頭就變一次，
        // 以前每次都連帶重算帳戶卡（含用量走勢）並讓網頁重畫整個側欄；數字和上次送出的一樣就不必
        if (!renderedSessionCounts.TryGetValue(c.Meta.AccountId, out var shown) || shown != SessionCounts(c.Meta.AccountId))
            MarkAccountsDirty();
    }

    /// <summary>
    /// 只有給人看的標頭內容變了（這一輪即時的 token 數）：重送標頭給開著的檢視，不經同步的掛鉤（同步帶的是一輪結束時的用量）、不重算帳戶卡
    /// </summary>
    private void MarkHeaderViewDirty(ConversationState c)
    {
        if (c.ViewIds.Count == 0)
            return;
        dirtyHeaders.Add(c.Meta.Id);
        ScheduleFlush();
    }

    /// <summary>上次送出的帳戶卡上，各帳戶的對話數</summary>
    private readonly Dictionary<string, (int Open, int Running, int Waiting, int Background, int Programs, int Subagents)> renderedSessionCounts = new(StringComparer.Ordinal);

    /// <summary>
    /// 帳戶卡的對話數：開著（有檢視）、執行中、等你回應、等背景工作回報（代理這一輪已回覆完畢、背景工作還在跑；同系統匣的 BackgroundWaitingCount）、
    /// 背景程式（這一輪結束了、但代理開的程式脫離 CLI 還在跑，AgentHub.BackgroundPrograms.cs）、執行中的子代理（個數，不是對話數；AgentHub.SubagentCount.cs）。草稿不算
    /// </summary>
    private (int Open, int Running, int Waiting, int Background, int Programs, int Subagents) SessionCounts(string accountId)
    {
        int open = 0, running = 0, waiting = 0, background = 0, programs = 0, subagents = 0;
        foreach (var c in conversations.Values)
        {
            if (c.Meta.AccountId != accountId || c.IsDraft)
                continue;
            if (c.ViewIds.Count > 0)
                open++;
            if (c.Running)
                running++;
            else if (BackgroundOf(c) != null)
                background++;
            if (AwaitsUser(c))
                waiting++;
            if (HasPrograms(c))
                programs++;
            subagents += LiveSubagents(c);
        }
        return (open, running, waiting, background, programs, subagents);
    }

    private void MarkListDirty()
    {
        listDirty = true;
        ScheduleFlush();
    }

    private DateTime listGroupDate = DateTime.Now.Date;

    /// <summary>
    /// 對話清單的日期分組（今天、昨天…）是送出當下算好的，網頁照用：跨日時重送一次，
    /// app 整晚開著、沒有任何對話活動時，隔天才不會還把昨天的對話列在「今天」底下（維護計時器每 30 秒呼叫）
    /// </summary>
    internal void RefreshListOnNewDay(DateTime now)
    {
        if (now.Date == listGroupDate)
            return;
        listGroupDate = now.Date;
        MarkListDirty();
    }

    private void MarkAccountsDirty()
    {
        accountsDirty = true;
        ScheduleFlush();
    }

    /// <summary>串流期間合併成每 50 毫秒送一次（Markdown 重新轉換與網頁重繪都不必每個字做一次）</summary>
    private void ScheduleFlush()
    {
        if (flushScheduled || disposed)
            return;
        flushScheduled = true;
        _ = Task.Delay(50).ContinueWith(_ => host.BeginInvoke(Flush), TaskScheduler.Default);
    }

    /// <summary>
    /// 現在就合併送出，不等上面的計時器：使用者剛送出的訊息先上畫面，再做存檔這類慢的事（整份 items.json 重寫並落盤，
    /// 大的對話好幾 MB）。計時器經執行緒集區才回到 UI 執行緒，集區被占滿（很多 CLI 程序、電腦負載高）時會拖上好幾秒。
    /// 已排定的計時器照樣會到，那時沒有要送的就什麼都不做
    /// </summary>
    private void FlushNow()
    {
        if (!disposed)
            Flush();
    }

    private const int AccountsFlushIntervalMs = 500;

    /// <summary>上次送帳戶卡片的時間（Environment.TickCount64：單調時鐘，系統時間往回調或換時區都不會讓節流卡住）</summary>
    private long? lastAccountsFlushMs;
    private bool accountsFlushScheduled;

    private void Flush()
    {
        flushScheduled = false;
        if (disposed)
            return;

        // 每則訊息各自防護：某一筆渲染失敗不影響其他更新，也不會讓同一筆卡住後續每一次合併
        Dictionary<string, HashSet<string>>? deferred = null;
        var now = Environment.TickCount64;
        try
        {
            foreach (var (conversationId, ids) in dirtyItems.ToList())
            {
                if (!conversations.TryGetValue(conversationId, out var c) || c.ViewIds.Count == 0)
                    continue;
                // 背景分頁每 500 ms 才送一次（HiddenFlushRules）：沒送的留著，下一輪再看
                var shownAnywhere = c.VisibleViewIds.Count > 0;
                if (HiddenFlushRules.Defer(shownAnywhere, c.ViewIds.All(viewsReportingVisibility.Contains), c.HiddenFlushedAtMs, now))
                {
                    (deferred ??= new(StringComparer.Ordinal))[conversationId] = ids;
                    continue;
                }
                c.HiddenFlushedAtMs = shownAnywhere ? null : now;
                var targets = ViewsOf(c).ToList();
                HashSet<string>? later = null;
                foreach (var item in c.Items.Where(i => ids.Contains(i.Id)).ToList())
                {
                    // 很長的執行中項目依長度降頻（StreamFlushRules）：每送一次都要重新轉換整則 Markdown；沒送的留著，下一輪再看
                    var running = item.Status == AgentItemStatus.Running;
                    if (StreamFlushRules.Defer(running, item.Text?.Length ?? 0, c.StreamFlushedAtMs.TryGetValue(item.Id, out var last) ? last : null, now))
                    {
                        (later ??= new(StringComparer.Ordinal)).Add(item.Id);
                        continue;
                    }
                    // 很長的執行中回覆在背景執行緒轉 Markdown（BackgroundMarkdownRules）：轉好時回呼再送；背景轉換失敗才在這裡照常轉
                    if (BackgroundMarkdownRules.Eligible(item.Kind, running, c.IsReplica, item.Text?.Length ?? 0))
                    {
                        if (FlushInBackground(c, item, targets, now, out var keepDirty))
                        {
                            if (keepDirty)
                                (later ??= new(StringComparer.Ordinal)).Add(item.Id);
                            continue;
                        }
                    }
                    else
                    {
                        // 在這裡照常轉、送出（結束了、變短、整份重送之後）：背景還沒送的稍早內容作廢
                        backgroundMarkdown?.Forget(conversationId, item.Id);
                    }
                    if (running)
                        c.StreamFlushedAtMs[item.Id] = now;
                    else
                        c.StreamFlushedAtMs.Remove(item.Id);
                    TryPost(targets, () => new JObject { ["type"] = "item", ["conversationId"] = conversationId, ["item"] = RenderItemOf(c, item, ShownWorkingDirectory(c)) });
                }
                if (later is not null)
                    (deferred ??= new(StringComparer.Ordinal))[conversationId] = later;
            }
        }
        finally
        {
            dirtyItems.Clear();
            if (deferred is not null)
            {
                foreach (var (conversationId, ids) in deferred)
                    dirtyItems[conversationId] = ids;
                ScheduleFlush();
            }
        }

        // 標頭也照背景分頁的節流（HiddenFlushRules）：執行中的狀態文字、上下文用量一秒好幾次，網頁每次都重畫整個標頭與輸入區
        // （看不到的分頁也是），宿主每次也要重算標頭；切回來時（visible）立刻送
        List<string>? deferredHeaders = null;
        try
        {
            foreach (var conversationId in dirtyHeaders.ToList())
            {
                if (!conversations.TryGetValue(conversationId, out var c))
                    continue;
                var shownAnywhere = c.VisibleViewIds.Count > 0;
                if (HiddenFlushRules.Defer(shownAnywhere, c.ViewIds.All(viewsReportingVisibility.Contains), c.HiddenHeaderFlushedAtMs, now))
                {
                    (deferredHeaders ??= new()).Add(conversationId);
                    continue;
                }
                c.HiddenHeaderFlushedAtMs = shownAnywhere ? null : now;
                PostHeader(c);
            }
        }
        finally
        {
            dirtyHeaders.Clear();
            if (deferredHeaders is not null)
            {
                foreach (var conversationId in deferredHeaders)
                    dirtyHeaders.Add(conversationId);
                ScheduleFlush();
            }
        }

        var listChanged = listDirty;
        if (listDirty)
        {
            listDirty = false;
            TryPost(views.Values.ToList(), () => new JObject { ["type"] = "conversations", ["items"] = RenderConversationList(), ["replicasPending"] = replicasPending });
        }

        if (cliDirty)
        {
            cliDirty = false;
            TryPost(views.Values.ToList(), () => new JObject { ["type"] = "cli", ["cli"] = RenderCli(), ["devicesCli"] = sync?.DevicesCli() });
        }

        FlushTokens();

        if (accountsDirty)
            FlushAccounts();

        // 清單變了（等你回應、執行中）：系統匣、工作列與各視窗的外框跟著更新（以前要等執行中的數量變了或 30 秒的維護）；
        // 其他變動（開分頁、搬分頁）只重算外框，和上次一樣就不送
        if (listChanged)
            UpdateTray();
        else
            UpdateWindowBorders();
    }

    /// <summary>帳戶卡片（含走勢線）最多每 500 毫秒送一次；用獨立的排程，不拖慢對話串流的 50 毫秒合併</summary>
    private void FlushAccounts()
    {
        if (disposed || !accountsDirty)
            return;
        var wait = AccountsFlushWait(lastAccountsFlushMs, Environment.TickCount64);
        if (wait > TimeSpan.Zero)
        {
            if (!accountsFlushScheduled)
            {
                accountsFlushScheduled = true;
                _ = Task.Delay(wait).ContinueWith(_ => host.BeginInvoke(() =>
                {
                    accountsFlushScheduled = false;
                    FlushAccounts();
                }), TaskScheduler.Default);
            }
            return;
        }
        accountsDirty = false;
        lastAccountsFlushMs = Environment.TickCount64;
        TryPost(views.Values.ToList(), () => new JObject { ["type"] = "accounts", ["accounts"] = RenderAccounts() });
    }

    /// <summary>帳戶卡片節流：距上次送出不到 500 毫秒就等到滿 500 毫秒；等待時間一定在 0～500 毫秒之間（傳入單調時鐘的毫秒數）</summary>
    internal static TimeSpan AccountsFlushWait(long? lastFlushMs, long nowMs)
        => lastFlushMs is { } last
            ? TimeSpan.FromMilliseconds(Math.Clamp(AccountsFlushIntervalMs - (nowMs - last), 0, AccountsFlushIntervalMs))
            : TimeSpan.Zero;

    /// <summary>
    /// 標頭更新送給開著這個對話的每個 view：兩份清單（指令、模型）和上次送給那個 view 的相同時省略（HeaderListRules，網頁沿用上一份）
    /// </summary>
    private void PostHeader(ConversationState c)
    {
        var targets = ViewsOf(c).ToList();
        if (targets.Count == 0)
            return;
        JObject header;
        string signature;
        try
        {
            header = RenderHeader(c);
            signature = HeaderListRules.Signature(header);
        }
        catch (Exception ex)
        {
            Log.Error("渲染畫面訊息失敗", ex);
            return;
        }
        JObject? full = null, slim = null;
        foreach (var view in targets)
        {
            var omit = HeaderListRules.CanOmit(signature, c.HeaderListsSent.TryGetValue(view.Id, out var last) ? last : null);
            var message = omit
                ? slim ??= HeaderMessage(c, HeaderListRules.WithoutLists(header))
                : full ??= HeaderMessage(c, header);
            view.PostToWeb(message);
            c.HeaderListsSent[view.Id] = signature;
        }
    }

    private static JObject HeaderMessage(ConversationState c, JObject header) =>
        new() { ["type"] = "header", ["conversationId"] = c.Meta.Id, ["header"] = header };

    private static void TryPost(IReadOnlyList<IAgentView> targets, Func<JObject> render)
    {
        if (targets.Count == 0)
            return;
        JObject message;
        try
        {
            message = render();
        }
        catch (Exception ex)
        {
            Log.Error("渲染畫面訊息失敗", ex);
            return;
        }
        foreach (var view in targets)
            view.PostToWeb(message);
    }

    /// <summary>本機時間 → Unix 毫秒；轉換不了的值回 null（規則在 Protocol 的 ProtocolValues，伺服器也用）</summary>
    internal static long? ToUnixMs(DateTime? value) => ProtocolValues.ToUnixMs(value);

    /// <summary>
    /// 整份送一個對話。<paramref name="reload"/>＝中樞主動重送（不是 open 的回覆，例如複本整份換掉）：網頁沒有這個分頁時丟掉、不算 open 的回覆
    /// （第二輪合併前審查 R1；docs/web-protocol.md 的 conversation）
    /// </summary>
    private void PostConversation(IAgentView view, ConversationState c, string? paneId, bool reload = false)
    {
        var items = EnsureItems(c);
        int start = Math.Max(0, items.Count - PageSize);
        var workingDirectory = ShownWorkingDirectory(c);
        var header = RenderHeader(c);
        // 整份重送的是目前的內容：背景還沒送的稍早內容作廢（晚到會蓋掉比較新的畫面），下一次送畫面時再排
        if (backgroundMarkdown is not null)
        {
            foreach (var running in items.Skip(start).Where(i => i.Status == AgentItemStatus.Running))
                backgroundMarkdown.Forget(c.Meta.Id, running.Id);
        }
        var message = new JObject
        {
            ["type"] = "conversation",
            ["conversationId"] = c.Meta.Id,
            ["paneId"] = paneId,
            ["header"] = header,
            ["items"] = new JArray(items.Skip(start).Select(i => RenderItemOf(c, i, workingDirectory))),
            ["hasMore"] = start > 0,
            ["composer"] = RenderComposer(c),
            ["draft"] = TakeComposerDraft(c.Meta.Id),
        };
        if (reload)
            message["reload"] = true;
        // 快照一律帶整份清單；送出時記下指紋，之後的標頭更新沒變就省略（HeaderListRules）
        c.HeaderListsSent[view.Id] = HeaderListRules.Signature(header);
        Post(view, message);
        // 工作資料夾的 git 狀態（AgentHub.Git.cs）：在 conversation 之後送，網頁才有這個分頁
        OnGitConversationPosted(view, c);
        // 執行列（AgentHub.Launch.cs）：同樣在 conversation 之後
        OnLaunchConversationPosted(view, c);
        // 不清掉待送的項目：同一個對話可能還開在其他視窗，它們仍需要這些更新（重送給這個視窗只是覆蓋成同樣內容）
    }

    private void PostComposer(ConversationState c, string? busyText = null)
    {
        PersistQueueIfChanged(c);
        if (busyText != null)
            c.ImportingText = busyText;
        HookComposerChanged(c);
        var message = new JObject { ["type"] = "composer", ["conversationId"] = c.Meta.Id, ["composer"] = RenderComposer(c) };
        foreach (var view in ViewsOf(c))
            Post(view, message);
    }

    private void Toast(string text, bool isError = false, IAgentView? view = null, string? level = null)
    {
        var message = new JObject { ["type"] = "toast", ["text"] = text, ["level"] = level ?? (isError ? "error" : "info") };
        if (view != null)
            Post(view, message);
        else
            PostToAll(message);
    }

    /// <summary>啟動時要告訴使用者的事（帳戶清單、設定檔、對話資料檔讀不到或損毀）：主視窗介面第一次就緒時各顯示一次</summary>
    private IEnumerable<string> StartupWarnings()
    {
        if (accounts.StartupWarning is { } accountsWarning)
            yield return accountsWarning;
        if (settings.StartupWarning is { } settingsWarning)
            yield return settingsWarning;
        if (store.StartupWarning is { } conversationsWarning)
            yield return conversationsWarning;
    }

    // ───────────────────────────── 渲染 ─────────────────────────────

    private JObject RenderSettings()
        => new()
        {
            ["theme"] = settings.Theme,
            ["fontSize"] = settings.FontSize,
            ["visualEffects"] = settings.VisualEffects,
            ["closeToTray"] = settings.CloseToTray,
            ["autoStart"] = RenderAutoStart(),
            ["notifications"] = settings.Notifications,
            ["voiceNotifications"] = settings.VoiceNotifications,
            // 排程頁的自動診斷（AgentHub.Schedules.cs）：沒掛排程讀取器時是 null，設定頁不顯示
            ["scheduleAutoDiagnose"] = SchedulesAttached ? settings.ScheduleAutoDiagnose : null,
            ["voiceSpeed"] = VoiceTuningSetting.Speed,
            ["voicePitch"] = VoiceTuningSetting.PitchSemitones,
            ["autoUpdateCli"] = settings.AutoUpdateCli,
            ["autoUpdateApp"] = settings.AutoUpdateApp,
            ["cliRestartPolicy"] = CliRestartRules.ToProtocol(settings.CliRestartPolicy),
            ["enterToSend"] = settings.EnterToSend,
            ["claudeRemoteControl"] = settings.ClaudeRemoteControl,
            ["claudeIdleTokenRefresh"] = settings.ClaudeIdleTokenRefresh,
            ["claudeFallbackModel"] = settings.ClaudeFallbackModel,
            ["claudeIdleResumeWarning"] = settings.ClaudeIdleResumeWarning,
            ["autoResumeAfterQuota"] = settings.AutoResumeAfterQuotaReset,
            ["autoSwitchAccount"] = settings.AutoSwitchAccountOnLimit,
            ["autoSwitchCrossProvider"] = settings.AutoSwitchCrossProviderOnLimit,
            ["shareCrossCliCustomizations"] = settings.ShareCrossCliCustomizations,
            ["instructions"] = settings.Instructions ?? AgentHubRules.DefaultInstructions,
            ["continueText"] = settings.ContinueText ?? AgentHubRules.DefaultContinueText,
            ["conversationDefaults"] = new JObject(AgentProviders.All.Select(p => new JProperty(p.ToString(), RenderConversationDefaults(p)))),
        };

    /// <summary>
    /// 設定頁「模型設定」的一家：預設模型（null＝沿用上次選擇）、預設權限模式與可選的模式、
    /// 每個模型（各帳戶回報的聯集）的預設推理強度（null＝CLI 預設）、自動壓縮上限
    /// </summary>
    private JObject RenderConversationDefaults(AgentProvider provider)
    {
        var defaults = ProviderDefaults(provider);
        return new JObject
        {
            ["model"] = defaults.Model,
            ["permission"] = ConversationDefaultsRules.PermissionFor(provider, defaults).ToString(),
            ["permissionModes"] = RenderPermissionModes(provider),
            ["models"] = RenderProviderModels(provider, defaults),
            // 自動壓縮上限（AutoCompactRules）：不支援的代理兩個都是 null，網頁不顯示那一列
            ["autoCompact"] = AutoCompactRules.ForProvider(provider, defaults.AutoCompactTokens),
            ["autoCompactRange"] = AutoCompactRules.RangeOf(provider) is { } range ? new JObject { ["min"] = range.Min, ["max"] = range.Max } : null,
            // 以百分比設定的（Grok）：同上
            ["autoCompactPercent"] = AutoCompactRules.PercentForProvider(provider, defaults.AutoCompactPercent),
            ["autoCompactPercentRange"] = AutoCompactRules.PercentRangeOf(provider) is { } pct ? new JObject { ["min"] = pct.Min, ["max"] = pct.Max } : null,
        };
    }

    private JArray RenderProviderModels(AgentProvider provider, ProviderConversationDefaults defaults)
    {
        var models = ProviderModels(provider);
        // 「預設」對應的模型：這一家第一個帳戶的（各帳戶的設定檔可能不同，Codex 看那個帳戶的 config.toml）
        var account = accounts.Accounts.FirstOrDefault(a => a.Provider == provider);
        var list = RenderModelList(models, DefaultModelLabel(provider, account != null ? ModelOptions(account.Id, provider) : models, account, replica: false));
        foreach (var row in list.OfType<JObject>())
        {
            var value = row.Value<string>("value")!;
            var efforts = models.First(m => m.Value == value).Efforts;
            row["effort"] = defaults.Efforts.TryGetValue(value, out var effort) && efforts.Contains(effort, StringComparer.Ordinal) ? effort : null;
        }
        return list;
    }

    /// <summary>模型選單；"default" 那一項另帶 resolved（實際是哪個模型，DefaultModelRules；不知道是 null）</summary>
    private static JArray RenderModelList(IReadOnlyList<AgentModelOption> models, string? resolvedDefault)
        => new(models.Select(m =>
        {
            var row = new JObject
            {
                ["value"] = m.Value,
                ["label"] = m.Label,
                ["description"] = m.Description,
                ["efforts"] = new JArray(m.Efforts),
            };
            if (string.Equals(m.Value, "default", StringComparison.OrdinalIgnoreCase))
                row["resolved"] = resolvedDefault;
            return row;
        }));

    /// <summary>Codex 帳戶 config.toml 的 model（路徑 → 讀的時間、值）：渲染標頭很頻繁，30 秒內不重讀</summary>
    private readonly Dictionary<string, (DateTime ReadAt, string? Model)> codexConfiguredModels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>「預設」實際是哪個模型（DefaultModelRules）。其他電腦的對話（replica）看不到那台的 Codex 設定檔，Codex 不顯示</summary>
    private string? DefaultModelLabel(AgentProvider provider, IReadOnlyList<AgentModelOption> models, AgentAccount? account, bool replica)
    {
        if (provider == AgentProvider.Codex && (replica || account == null))
            return null;
        string? configured = null;
        if (provider == AgentProvider.Codex)
        {
            var home = CodexConfigRules.ResolveHome(account!.IsSystemDefault ? null : account.ConfigDirectory);
            var now = DateTime.UtcNow;
            if (!codexConfiguredModels.TryGetValue(home, out var cached) || now - cached.ReadAt > TimeSpan.FromSeconds(30))
            {
                cached = (now, DefaultModelRules.ParseCodexConfiguredModel(CodexConfigRules.ReadConfig(home)));
                codexConfiguredModels[home] = cached;
            }
            configured = cached.Model;
        }
        return DefaultModelRules.ResolvedLabel(provider, models, configured);
    }

    private static JArray RenderPermissionModes(AgentProvider provider)
        => new(AgentPermissionModes.For(provider).Select(o => new JObject
        {
            ["value"] = o.Mode.ToString(),
            ["label"] = o.Label,
            ["description"] = o.Description,
        }));

    private JArray RenderAccounts()
    {
        var result = new JArray();
        renderedProgramsSig = ProgramsSignature();
        foreach (var account in accounts.Accounts)
        {
            var state = accounts.GetState(account.Id);
            var sessions = SessionCounts(account.Id);
            renderedSessionCounts[account.Id] = sessions;
            var programs = sessions.Programs > 0 ? RenderAccountPrograms(account.Id) : null;
            result.Add(new JObject
            {
                ["id"] = account.Id,
                ["provider"] = account.Provider.ToString(),
                ["label"] = account.Label,
                ["color"] = account.Color,
                ["isSystemDefault"] = account.IsSystemDefault,
                ["configDirectory"] = AccountRules.ResolveConfigDirectory(account),
                ["email"] = account.Email,
                ["plan"] = account.Plan,
                ["status"] = StatusName(state.Status),
                ["statusText"] = state.StatusText,
                ["safeMode"] = account.SafeMode && AccountRules.SupportsSafeMode(account.Provider),
                // 隔離帳戶共用系統預設帳戶的設定（帳戶選單的勾選；系統預設帳戶本身就是來源，一律 false）
                ["shareCustomizations"] = account.ShareCustomizations && !account.IsSystemDefault && AccountRules.SupportsSharedCustomizations(account.Provider),
                ["loginBusy"] = state.LoginBusy,
                ["resetCreditBusy"] = accounts.IsResetCreditBusy(account.Id),   // Codex 的「使用重置」進行中
                ["deviceCodeLogin"] = CodexDeviceCodeRules.CanUseDeviceCode(account, state.Status) && !state.LoginBusy,   // 〔帳戶目錄〕帳戶選單的「用裝置碼登入」
                ["loginExpiresAt"] = AccountIdentityRules.ShownLoginExpiresAt(state.Status, state.LoginExpiresAt),       // 登入到期（Claude），剩 3 天內帳戶卡提醒重新登入
                ["usage"] = RenderUsage(account.Id),
                ["sessions"] = new JObject
                {
                    ["open"] = sessions.Open,
                    ["running"] = sessions.Running,
                    ["waiting"] = sessions.Waiting,
                    ["background"] = sessions.Background,
                    ["programs"] = sessions.Programs,
                    ["subagents"] = sessions.Subagents,
                },
            });
            // 背景程式的說明（帳戶卡滑過的提示）：沒有就不帶
            if (programs != null)
                ((JObject)result[result.Count - 1])["programs"] = programs;
            // 子代理徽章的說明（第十一批：哪幾個對話各有幾個子代理在跑）：沒有就不帶
            if (sessions.Subagents > 0)
                ((JObject)result[result.Count - 1])["subagentList"] = RenderAccountSubagents(account.Id);
        }
        // 〔同步〕logicalId、remoteOnly、devices（未配對時不加）
        sync?.DecorateAccounts(result);
        return result;
    }

    /// <summary>支援的 CLI 與各自能做的事（網頁依此決定新增帳戶的選項、精簡模式、CLI 卡；順序＝AgentProviders.All）</summary>
    private static JArray RenderProviders()
        => new(AgentProviders.All.Select(p => new JObject
        {
            ["key"] = p.ToString(),
            ["name"] = AgentProviders.ProductName(p),
            ["isolatedAccounts"] = AccountRules.SupportsIsolatedAccounts(p),
            ["safeMode"] = AccountRules.SupportsSafeMode(p),
            ["sharedCustomizations"] = AccountRules.SupportsSharedCustomizations(p),
        }));

    private static string StatusName(AccountStatus status) => status switch
    {
        AccountStatus.Checking => "checking",
        AccountStatus.Ready => "ready",
        AccountStatus.NotLoggedIn => "notLoggedIn",
        AccountStatus.ApiKey => "apiKey",
        AccountStatus.NotInstalled => "notInstalled",
        _ => "error",
    };

    private JObject? RenderUsage(string accountId)
    {
        var snapshot = accounts.GetUsage(accountId);
        if (snapshot == null)
            return null;
        var windows = new JArray();
        foreach (var w in snapshot.Windows)
        {
            var history = new JArray(accounts.GetHistory(accountId, w.Key).Select(p => new JArray((long)p[0], p[1])));
            windows.Add(new JObject
            {
                ["key"] = w.Key,
                ["label"] = w.Label,
                ["percent"] = w.Percent,
                ["resetsAt"] = w.ResetsAt?.ToUnixTimeMilliseconds(),
                ["severity"] = UsageRules.Severity(w.Percent, w.Locked),
                ["history"] = history,
            });
        }
        return new JObject
        {
            ["fetchedAt"] = snapshot.FetchedAt == DateTimeOffset.MinValue ? null : snapshot.FetchedAt.ToUnixTimeMilliseconds(),
            ["stale"] = snapshot.Stale,
            ["source"] = snapshot.Source,
            ["error"] = snapshot.Error,
            ["windows"] = windows,
            ["credits"] = snapshot.Credits,
            ["resetCredits"] = snapshot.ResetCredits,
            ["resetCreditExpiresAt"] = snapshot.ResetCreditExpiresAt?.ToUnixTimeMilliseconds(),
            ["note"] = snapshot.Note,
        };
    }

    /// <summary>對話清單最多列幾個本機的對話</summary>
    internal const int ConversationListMax = 1000;

    /// <summary>對話清單最多列幾個其他電腦的對話（複本）：和本機的上限分開算，複本一多也不擠掉本機的對話（合併前審查 R5）</summary>
    internal const int ReplicaListMax = 1000;

    /// <summary>對話清單列的對話（不含草稿；本機與複本各有名額），依更新時間新到舊；側欄的搜尋也只搜這些</summary>
    private List<ConversationState> ListedConversations()
    {
        // 臨時對話（AgentHub.QuickChat.cs）不列在清單與搜尋
        var shown = conversations.Values.Where(c => !c.IsDraft && !QuickChatRules.IsEphemeral(c.Meta)).ToList();
        var locals = shown.Where(c => !c.IsReplica).OrderByDescending(c => c.Meta.UpdatedAt).Take(ConversationListMax);
        // 複本的 UpdatedAt 是擁有者電腦的時鐘：時鐘超前的電腦的複本永遠排在前面，所以名額要分開
        var replicas = shown.Where(c => c.IsReplica).OrderByDescending(c => c.Meta.UpdatedAt).Take(ReplicaListMax);
        return locals.Concat(replicas).OrderByDescending(c => c.Meta.UpdatedAt).ToList();
    }

    private JArray RenderConversationList()
    {
        var now = DateTime.Now;
        var list = new JArray();
        foreach (var c in ListedConversations())
        {
            var summary = new JObject
            {
                ["id"] = c.Meta.Id,
                ["accountId"] = c.Meta.AccountId,
                ["provider"] = c.Meta.Provider.ToString(),
                ["title"] = c.Meta.Title,
                ["group"] = AgentHubRules.GetGroupLabel(c.Meta.UpdatedAt, now),
                ["updatedAt"] = ToUnixMs(c.Meta.UpdatedAt),
                ["workingDirectory"] = ShownWorkingDirectory(c),
                ["customFolder"] = !string.IsNullOrWhiteSpace(c.Meta.WorkingDirectory),
                ["running"] = c.Running,
                ["waiting"] = AwaitsUser(c),
                // 有存活的背景工作（代理這一輪結束了也算還在進行）：分頁與側欄在沒有執行中時以「等背景工作回報」的樣貌顯示
                ["background"] = BackgroundOf(c) != null,
                // 這個對話另開、還在跑的程式數（只有桌面這台的對話）：刪除前問要不要一併停止（AgentHub.BackgroundPrograms.cs）
                ["programCount"] = c.IsReplica || c.IsDraft ? 0 : ShownPrograms(c).Count,
                ["unread"] = c.Unread,
                // 正在自動重新命名（autoTitle）：側欄這一列閃爍自動重新命名的圖示、右鍵選單的「自動重新命名」停用
                ["titling"] = c.Titling,
            };
            // 帳戶委派的子對話（主對話的 id）與延伸成新對話的來源（第十批：側欄把子對話掛在主對話底下、標頭寫延伸自哪裡）；沒有就省略
            if (c.Meta.ParentConversationId != null)
                summary["parentId"] = c.Meta.ParentConversationId;
            if (c.Meta.ForkedFrom != null)
            {
                summary["forkedFrom"] = c.Meta.ForkedFrom;
                summary["forkedFromTitle"] = c.Meta.ForkedFromTitle;
            }
            // 〔WP4.6〕副本帶擁有者電腦與那邊的狀態（AgentHub.LocalSyncView.cs）
            DecorateSummary(c, summary);
            list.Add(summary);
        }
        return list;
    }

    /// <summary>
    /// 對話有沒有等使用者回應的卡片（問卷、核准、計畫確認），與網頁分頁的「!」同一個判定：含 Codex 非同步提問
    /// （這一輪結束後仍等著回答）。清單、帳戶卡與系統匣都用它；閒置回收另用 WaitingCount（非同步提問不需要程序活著）
    /// </summary>
    private static bool AwaitsUser(ConversationState c)
        => c.Items.Any(i => i.Status == AgentItemStatus.Waiting && i.Kind is AgentItemKind.Question or AgentItemKind.Approval or AgentItemKind.PlanApproval);

    private JObject RenderCli()
    {
        JObject Render(AgentProvider provider)
        {
            var s = cli.Get(provider);
            var view = new JObject
            {
                ["provider"] = provider.ToString(),
                ["installed"] = s.Installed,
                ["checking"] = s.Checking,
                ["version"] = s.Version,
                ["latest"] = s.Latest,
                ["latestError"] = s.LatestError,
                ["updateAvailable"] = s.UpdateAvailable,
                // WinGet 的 portable 套件（以 winget upgrade 更新）另外標出；agy 與 grok 其他的都是官方安裝程式裝的原生執行檔
                // （以各自的 update 指令更新，內部歸在 Other），對使用者來說就是原生安裝
                ["installKind"] = s.Installation == null ? null : s.Installation.Winget != null ? "winget" : s.Installation.Kind switch
                {
                    CliInstallKind.Native => "native",
                    _ when provider is AgentProvider.Antigravity or AgentProvider.Grok => "native",
                    CliInstallKind.Npm => "npm",
                    CliInstallKind.Managed => "managed",
                    _ => "other",
                },
                ["path"] = s.Installation?.DisplayPath,
                ["autoUpdate"] = settings.AutoUpdateCli,
                ["busy"] = s.Busy,
                ["progress"] = s.Progress,
                ["lastCheckedAt"] = ToUnixMs(s.LastCheckedAt),
                ["checkedAt"] = ToUnixMs(s.CheckedAt),
                ["error"] = s.ActionError ?? s.Error,
            };
            // 〔熱更新〕安裝狀態與換版彙總（AgentHub.Restart.cs）
            AddCliRestartFields(view, provider, s);
            return view;
        }

        return new JObject(AgentProviders.All.Select(p => new JProperty(p.ToString(), Render(p))));
    }

    private JObject RenderHeader(ConversationState c)
    {
        var account = accounts.Find(c.Meta.AccountId);
        var shownTokens = TurnTokenRules.Shown(c.Running, c.TurnTokens, c.LastUsage);
        var models = ModelOptions(c);
        var agyWorkspace = GetCachedAntigravityWorkspace(c, account);
        // /clear 由中樞接住（AgentHubRules.CliHandlesClear）：CLI 的清單沒有也列出來；Antigravity 開精簡模式（--disable-slash-commands）時不列 CLI 斜線指令與 Skills
        IReadOnlyList<AgentSlashCommand> baseCommands = account is { SafeMode: true } && c.Meta.Provider == AgentProvider.Antigravity
            ? Array.Empty<AgentSlashCommand>()
            : settings.Commands.TryGetValue(c.Meta.AccountId, out var cmds)
                ? cmds
                : c.Meta.Provider == AgentProvider.Antigravity
                    ? AntigravityStreamRules.BuiltinWorkflowCommands
                    : Array.Empty<AgentSlashCommand>();
        if (c.Meta.Provider == AgentProvider.Antigravity && agyWorkspace.Commands.Count > 0)
            baseCommands = AntigravityWorkspaceScanner.MergeCommands(baseCommands, agyWorkspace.Commands);
        // /btw 也由中樞接住（旁支提問，AgentHub.SideQuestion.cs）：CLI 的清單沒有它（只在互動介面），支援的代理補上
        var commands = AgentHubRules.WithSideQuestionCommand(AgentHubRules.WithClearCommand(baseCommands), c.Meta.Provider);
        if (c.Meta.Provider == AgentProvider.Codex)
            commands = commands.Concat(new[] { new AgentSlashCommand("compact", "壓縮較早的對話內容", null), new AgentSlashCommand("review", "審查程式碼修改", "[commit 雜湊／base 分支／審查說明]") }).ToList();
        var header = new JObject
        {
            ["id"] = c.Meta.Id,
            ["accountId"] = c.Meta.AccountId,
            ["provider"] = c.Meta.Provider.ToString(),
            // 草稿一律「新對話」；臨時對話的草稿是「臨時對話」
            ["title"] = c.IsDraft && !QuickChatRules.IsEphemeral(c.Meta) ? "新對話" : c.Meta.Title,
            ["model"] = c.Meta.Model ?? "default",
            ["effort"] = c.Meta.Effort ?? "default",
            ["permission"] = AgentPermissionModes.Normalize(c.Meta.Provider, c.Meta.PermissionMode).ToString(),
            ["permissionModes"] = RenderPermissionModes(c.Meta.Provider),
            ["models"] = RenderModelList(EnsureDefaultModel(models).ToList(), DefaultModelLabel(c.Meta.Provider, models,
                c.IsReplica ? null : accounts.Find(c.Meta.AccountId), c.IsReplica)),
            ["commands"] = new JArray(commands.Select(x => new JObject
            {
                ["name"] = x.Name,
                ["description"] = x.Description,
                ["argumentHint"] = x.ArgumentHint,
            })),
            ["workingDirectory"] = ShownWorkingDirectory(c),
            ["customFolder"] = !string.IsNullOrWhiteSpace(c.Meta.WorkingDirectory),
            ["recentFolders"] = new JArray(validRecentFolders.Take(10)),
            ["isDraft"] = c.IsDraft,
            ["running"] = c.Running,
            // 執行中送出的訊息直接插進這一輪（不支援的代理照舊排隊）：網頁據此寫送出鈕與提示
            ["steer"] = AgentHubRules.SupportsSteering(c.Meta.Provider),
            ["status"] = c.StatusText,
            // 正在自動重新命名（autoTitle）：標題後面轉圈
            ["titling"] = c.Titling,
            ["startedAt"] = c.Running ? ToUnixMs(c.TurnStartedAt) : null,
            // 存活的背景工作（BackgroundWorkRules.View；沒有是 null）：代理這一輪結束了、在等它們回報時，網頁以不同的樣貌顯示還在進行
            ["background"] = RenderBackground(c),
            // 代理另開、脫離 CLI 還在跑的程式（AgentHub.BackgroundPrograms.cs；沒有是 null）：網頁在晶片列顯示，可以停止
            ["programs"] = RenderConversationPrograms(c),
            // 輸入框下方那一行的 token 數：執行中是這一輪到目前為止的即時累計（turnLive），其他是上一輪（TurnTokenRules）
            ["lastTurn"] = TurnTokenRules.Text(shownTokens.Usage),
            ["turnLive"] = shownTokens.Live,
            // 上下文用量提示（ContextUsageRules；用量或視窗不明、Antigravity、副本與草稿是 null）
            ["context"] = ContextUsageRules.ToView(c.ContextUsage),
            ["accountMissing"] = account == null,
            // 本機 Claude／Codex 一般模式的主對話可以 @ 其他 AI 帳戶委派工作（AgentHub.Delegation.cs）
            ["accountDelegation"] = SupportsAccountDelegation(c),
            // 執行中選了另一個帳戶、還沒換過去（立即換的在停下、或等這一輪結束；AgentHub.AccountSwitch.cs）：網頁在帳戶晶片標出
            ["pendingAccount"] = RenderPendingAccount(c),
            ["restart"] = RenderHeaderRestart(c),
            ["lastPrompt"] = RenderLastPrompt(c),
        };
        if (!c.IsReplica && c.Meta.Provider == AgentProvider.Antigravity && account is not { SafeMode: true })
        {
            header["agent"] = c.Meta.Agent ?? "default";
            header["sessionStarted"] = c.Meta.SessionStarted;
            if (agyWorkspace.Agents.Count > 0 || !string.IsNullOrWhiteSpace(c.Meta.Agent))
            {
                var agentItems = new JArray
                {
                    new JObject
                    {
                        ["value"] = "default",
                        ["label"] = "預設",
                        ["description"] = "Antigravity 內建主代理",
                    },
                };
                foreach (var a in agyWorkspace.Agents)
                {
                    agentItems.Add(new JObject
                    {
                        ["value"] = a.Name,
                        ["label"] = a.Label,
                        ["description"] = a.Description,
                    });
                }
                header["agents"] = agentItems;
            }
        }
        // Claude 的主代理與輸出風格（AgentHub.ClaudeCustomizations.cs）、進行中的目標（AgentHub.Goal.cs）
        RenderClaudeCustomizations(c, account, header);
        RenderGoal(c, header);
        // 等額度重置後自動接著做（AgentHub.QuotaWait.cs；沒有就不帶）
        if (RenderQuotaWait(c) is { } quotaWait)
            header["quotaWait"] = quotaWait;
        // Claude 的提示快取從什麼時候開始閒置與快取過期後整份重送的額度成本（送出前的提醒，網頁 claudeoptrules.cacheResumeWarning；
        // 執行中、副本不帶；CLI 沒回報成本時不帶 coldResume、網頁不提醒）
        if (c.Meta.Provider == AgentProvider.Claude && !c.Running && !c.IsReplica && !c.IsDraft)
        {
            header["idleSince"] = ToUnixMs(c.LastActivity);
            if (c.Meta.ColdResume is { } coldResume)
                header["coldResume"] = new JObject { ["perMillion"] = coldResume.PercentPerMillionTokens, ["minPercent"] = coldResume.MinPercent };
        }
        // 前景有指令或子代理在跑、可以移到背景（AgentHub.MoveToBackground.cs；不能就不帶）
        if (CanMoveToBackground(c))
            header["canBackground"] = true;
        // 子代理的即時用量（AgentHub.Subagents.cs；沒有就不帶）
        if (RenderSubagentUsages(c) is { } subagents)
            header["subagents"] = subagents;
        // 臨時對話（AgentHub.QuickChat.cs；不是就不帶）：網頁在回覆上顯示「朗讀」、空白畫面改成臨時對話的說法
        if (QuickChatRules.IsEphemeral(c.Meta))
            header["ephemeral"] = true;
        RenderCodexControls(c, header);
        // 〔WP4.6〕副本的欄位、本機對話的同步政策（AgentHub.LocalSyncView.cs）
        DecorateHeader(c, header);
        // 〔桌面遠端操作〕要在其他電腦開的新對話（AgentHub.PeerDrafts.cs）
        DecoratePeerDraftHeader(c, header);
        return header;
    }

    /// <summary>
    /// 對話上方常駐的「最近的指令」（docs/web-protocol.md 的 Header.lastPrompt）：網頁只載入最後一頁，長的一輪做完之後重開對話時
    /// 那則訊息可能不在已載入的項目裡，所以由這裡附上。項目還沒載入或沒有時為 null（網頁從已載入的項目找）
    /// </summary>
    private static JObject? RenderLastPrompt(ConversationState c)
    {
        if (!c.ItemsLoaded || AgentHubRules.LastPrompt(c.Items) is not { } item)
            return null;
        return new JObject
        {
            ["id"] = item.Id,
            ["seq"] = item.Seq,
            ["time"] = ToUnixMs(item.CreatedAt),
            ["text"] = AgentHubRules.LastPromptText(item.Text),
            ["attachments"] = item.Attachments.Count,
        };
    }

    /// <summary>這個對話的帳戶可選的模型：CLI 回報過就用回報的清單，否則用內建的預設選單</summary>
    /// <remarks>
    /// 其他電腦的對話（副本）：擁有者發布的清單（從這台改那台的模型時照它列；那台的帳戶在這台沒有意義），擁有者沒送（舊版）時用內建的預設選單
    /// </remarks>
    private IReadOnlyList<AgentModelOption> ModelOptions(ConversationState c)
        => c.IsReplica
            ? c.ReplicaModels is { Count: > 0 } published ? published : DefaultModels(c.Meta.Provider)
            : ModelOptions(c.Meta.AccountId, c.Meta.Provider);

    private IReadOnlyList<AgentModelOption> ModelOptions(string accountId, AgentProvider provider)
        => settings.Models.TryGetValue(accountId, out var cached) && cached.Count > 0 ? cached : DefaultModels(provider);

    /// <summary>設定頁「模型設定」列出的模型：這一家各帳戶回報的聯集，最前面一定有「預設」</summary>
    private List<AgentModelOption> ProviderModels(AgentProvider provider)
        => EnsureDefaultModel(ConversationDefaultsRules.ProviderModels(
            accounts.Accounts.Where(a => a.Provider == provider)
                .Select(a => settings.Models.TryGetValue(a.Id, out var models) ? models : new List<AgentModelOption>()),
            DefaultModels(provider))).ToList();

    /// <summary>模型清單最前面一定有「預設」</summary>
    private static IEnumerable<AgentModelOption> EnsureDefaultModel(IReadOnlyList<AgentModelOption> models)
    {
        if (!models.Any(m => m.Value.Equals("default", StringComparison.OrdinalIgnoreCase)))
            yield return new AgentModelOption("default", "預設", "帳戶預設模型", models.FirstOrDefault()?.Efforts ?? Array.Empty<string>());
        foreach (var m in models)
            yield return m;
    }

    /// <summary>CLI 還沒回報模型清單前的預設選單</summary>
    internal static List<AgentModelOption> DefaultModels(AgentProvider provider)
    {
        switch (provider)
        {
            case AgentProvider.Claude:
            {
                var efforts = new[] { "low", "medium", "high", "xhigh", "max" };
                return new List<AgentModelOption>
                {
                    new("default", "預設（建議）", "帳戶預設模型", efforts),
                    new("opus", "Opus", "複雜工作與日常任務", efforts),
                    new("fable", "Fable", "最強，適合最困難的工作", efforts),
                    new("sonnet", "Sonnet", "較簡單的工作，省額度", efforts),
                    new("haiku", "Haiku", "最快，簡單問答", efforts),
                };
            }
            case AgentProvider.Codex:
            {
                var codexEfforts = new[] { "low", "medium", "high", "xhigh", "max" };
                return new List<AgentModelOption>
                {
                    new("default", "預設", "Codex 設定檔的預設模型", codexEfforts),
                };
            }
            case AgentProvider.Antigravity:
                return new List<AgentModelOption>
                {
                    new("default", "預設", "agy 設定的預設模型（在 agy 互動介面以 /model 選的）", AntigravityStreamRules.DefaultModelEfforts),
                };
            case AgentProvider.Grok:
                return new List<AgentModelOption>
                {
                    new("default", "預設", "grok 的預設模型", new[] { "low", "medium", "high", "xhigh" }),
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        }
    }

    private JObject RenderComposer(ConversationState c)
        => new()
        {
            ["attachments"] = new JArray(c.PendingAttachments.Select(Renderer.RenderAttachment)),
            ["busy"] = c.ImportingCount > 0,
            ["busyText"] = c.ImportingCount > 0 ? c.ImportingText ?? "正在處理附件…" : null,
            ["queue"] = new JArray(c.Queue.Select(q => new JObject
            {
                ["id"] = q.Id,
                ["text"] = q.Attachments.Count > 0 ? $"{q.DisplayText ?? q.Text}（附 {q.Attachments.Count} 個檔案）".Trim() : q.DisplayText ?? q.Text,
            })),
            // 正在插進這一輪的那則（佇列最前面）；沒有為 null
            ["steering"] = c.SteeringMessage?.Id,
            // 已插進這一輪、還可以試著取消的那則（AgentHub.SteerCancel.cs）；沒有為 null
            ["steered"] = RenderSteered(c),
        };

    /// <summary>
    /// 一筆項目 → 畫面資料（Protocol 的共用渲染器 ItemRenderer；失敗時改送一筆錯誤說明，同一筆只記一次）
    /// </summary>
    /// <param name="workingDirectory">對話的工作資料夾（回覆裡的相對路徑連結以此解析）</param>
    /// <remarks>internal：渲染黃金檔測試（ItemRendererGoldenTests）直接呼叫（只能在 UI 執行緒）</remarks>
    internal JObject RenderItem(AgentChatItem item, string workingDirectory) => Renderer.Render(item, workingDirectory);
}
