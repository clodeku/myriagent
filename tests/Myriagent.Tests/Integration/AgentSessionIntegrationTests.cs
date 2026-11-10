using System.Collections.Concurrent;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Myriagent.Usage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Myriagent.Tests.Integration;

/// <summary>
/// 以這台電腦真的 Claude Code／Codex CLI（預設帳戶）跑會話類別：串流文字、看圖、問卷作答後模型引用答案、指令核准、跨程序續接記得前文。
/// 會用到訂閱額度：模型選最便宜的（Claude haiku、Codex gpt-6-luna）、推理強度 low、提示詞盡量短。
/// 執行：MYRIAGENT_IT=1 dotnet test ... --filter "FullyQualifiedName~Integration"
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentSessionIntegrationTests : IDisposable
{
    private const string Reason = "實際呼叫本機 Claude／Codex CLI（會用到訂閱額度）";
    private const string ClaudeModel = "haiku";
    private const string CodexModel = "gpt-6-luna";
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(4);

    private readonly ITestOutputHelper output;
    private readonly TempDirectory temp = new("it-sessions");

    public AgentSessionIntegrationTests(ITestOutputHelper output) => this.output = output;

    public void Dispose() => temp.Dispose();

    private string Workspace => Path.Combine(temp.Root, "workspace");

    private AgentSessionSettings Settings(AgentProvider provider, string? sessionId, bool started, string model,
        AgentPermissionMode mode = AgentPermissionMode.Ask, string? recap = null)
    {
        Directory.CreateDirectory(Workspace);
        var launcher = CliLocator.Locate(provider)?.Launcher;
        launcher.Should().NotBeNull($"這台電腦要安裝 {provider} CLI");
        var account = new AgentAccount { Provider = provider, Label = "本機" };
        return new AgentSessionSettings(
            sessionId, started, launcher, $"it|{provider}", env => AccountRules.ApplyEnvironment(account, env), null,
            model, "low", mode, Workspace, temp.Root,
            AgentHubRules.BuildAgentInstructions(null, provider, Workspace, Path.Combine(temp.Root, "attachments")),
            UseLocalCustomizations: false, recap);
    }

    /// <summary>藍色 32×32 PNG</summary>
    private AgentAttachment BluePng()
    {
        var path = Path.Combine(temp.Root, "blue.png");
        using (var bmp = new System.Drawing.Bitmap(32, 32))
        {
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.Clear(System.Drawing.Color.Blue);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        return new AgentAttachment { FileName = "blue.png", StoredPath = path, Kind = AgentAttachmentKind.Image, ModelImagePaths = { path } };
    }

    /// <summary>跑一輪：問卷一律選最後一個選項、核准依 decide（預設一律允許）；回傳本輪事件</summary>
    private async Task<List<AgentEvent>> RunTurnAsync(IAgentSession session, AgentTurnInput input, AgentSessionSettings settings,
        Func<AgentApprovalEvent, AgentApprovalChoice>? decide = null)
    {
        var events = new BlockingCollection<AgentEvent>();
        void Handler(AgentEvent ev) => events.Add(ev);
        session.EventReceived += Handler;
        var collected = new List<AgentEvent>();
        try
        {
            await session.SendAsync(input, settings);
            var deadline = DateTime.UtcNow + TurnTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!events.TryTake(out var ev, TimeSpan.FromSeconds(1)))
                    continue;
                collected.Add(ev);
                output.WriteLine($"{ev.GetType().Name}: {Describe(ev)}");
                switch (ev)
                {
                    case AgentQuestionEvent q when !q.AnswerAsMessage:
                        await session.AnswerQuestionAsync(q.RequestKey, q.Questions.ToDictionary(
                            x => x.Id,
                            x => (IReadOnlyList<string>)new[] { x.Options.Count > 0 ? x.Options[^1].Label : "紅茶" }));
                        break;
                    case AgentApprovalEvent { IsPlan: false } a:
                        await session.RespondApprovalAsync(a.RequestKey, decide?.Invoke(a) ?? AgentApprovalChoice.Allow, null);
                        break;
                    case AgentTurnCompletedEvent:
                        return collected;
                }
            }
            throw new TimeoutException("這一輪沒有在時限內結束");
        }
        finally
        {
            session.EventReceived -= Handler;
        }
    }

    private static string Describe(AgentEvent ev) => ev switch
    {
        AgentTextCompletedEvent t => t.Text,
        AgentToolStartedEvent t => $"{t.ToolName}（{t.ToolKind}）{t.Title}",
        AgentQuestionEvent q => string.Join(" / ", q.Questions.Select(x => x.Text)),
        AgentApprovalEvent a => $"{a.ToolName}（{a.ToolKind}）{a.Title}",
        AgentTurnCompletedEvent c => $"{c.Outcome} {c.ErrorMessage} 輸入 {c.Usage?.InputTokens} 輸出 {c.Usage?.OutputTokens}",
        AgentNoticeEvent n => n.Message,
        AgentSessionStartedEvent s => $"{s.SessionId} {s.Model} {s.Detail}",
        _ => string.Empty,
    };

    private static string AllText(IEnumerable<AgentEvent> events)
        => string.Join("\n", events.OfType<AgentTextCompletedEvent>().Select(t => t.Text));

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Codex_原生規劃確認後執行_回報整輪差異()
    {
        var settings = Settings(AgentProvider.Codex, null, false, CodexModel, AgentPermissionMode.FullAccess) with { CodexMode = "plan" };
        using var session = new CodexAgentSession();
        var plan = await RunTurnAsync(session, new AgentTurnInput("請規劃在工作資料夾建立 plan-probe.txt，唯一內容為 PLAN_EXECUTED。需求已確定，不必問問題、不使用工具。只提出簡短計畫，等我確認再寫檔。", []), settings);
        plan.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
        plan.OfType<AgentCodexPlanEvent>().Should().Contain(p => p.Completed && p.Text.Contains("plan-probe.txt"));
        File.Exists(Path.Combine(Workspace, "plan-probe.txt")).Should().BeFalse();
        var thread = plan.OfType<AgentSessionStartedEvent>().Last().SessionId;
        var execute = await RunTurnAsync(session, new AgentTurnInput("計畫確認，現在用 apply_patch 建立 plan-probe.txt；檔案內容只有 PLAN_EXECUTED，完成後簡短回報。", []),
            settings with { SessionId = thread, SessionStarted = true, CodexMode = "default" });
        execute.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
        (await File.ReadAllTextAsync(Path.Combine(Workspace, "plan-probe.txt"))).Trim().Should().Be("PLAN_EXECUTED");
        execute.OfType<AgentTurnDiffEvent>().Should().Contain(d => d.Diff.Contains("plan-probe.txt"));
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Codex_原生控制_技能選取_標準速度_審查與手動壓縮()
    {
        var settings = Settings(AgentProvider.Codex, null, false, CodexModel, AgentPermissionMode.FullAccess);
        var skillPath = Path.Combine(Workspace, ".agents", "skills", "myriagent-control-probe", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
        await File.WriteAllTextAsync(skillPath, "---\nname: myriagent-control-probe\ndescription: 測試指定技能的結構化傳入\n---\n這是隔離測試。回覆時包含 SKILL_WIRED_OK，不要使用工具。\n");
        using var session = new CodexAgentSession();
        var inspect = await session.CodexControlAsync("inspect", new JObject(), settings);
        inspect.Value<string>("skillsError").Should().BeNull();
        var skill = inspect["skills"]!.OfType<JObject>().Single(s => s.Value<string>("name") == "myriagent-control-probe");
        settings = settings with { CodexServiceTier = "default", CodexSkills = [new("myriagent-control-probe", skill.Value<string>("path")!)] };
        var first = await RunTurnAsync(session, new AgentTurnInput("依指定技能回覆即可。", []), settings);
        first.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
        AllText(first).Should().Contain("SKILL_WIRED_OK");
        settings = settings with { SessionId = first.OfType<AgentSessionStartedEvent>().First().SessionId, SessionStarted = true, CodexSkills = null };
        var compact = await RunTurnAsync(session, new AgentTurnInput("/compact", []), settings);
        compact.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
        var review = await RunTurnAsync(session, new AgentTurnInput("/review 這是原生審查協定測試。沒有程式碼修改，不使用工具，回報沒有發現問題即可。", []), settings);
        review.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.IsRunning.Should().BeFalse();
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_串流文字_看圖_問卷_核准_跨程序續接()
    {
        var sessionId = Guid.NewGuid().ToString();

        using (var session = new ClaudeAgentSession())
        {
            var events = await RunTurnAsync(session, new AgentTurnInput(
                "一句話說圖片顏色。然後用 AskUserQuestion 問我單選題：咖啡或紅茶。得到答案後用 Bash 執行 mkdir it_dir，最後一句話說我選了什麼。",
                new[] { BluePng() }), Settings(AgentProvider.Claude, sessionId, started: false, ClaudeModel));

            events.OfType<AgentSessionStartedEvent>().Should().NotBeEmpty();
            events.OfType<AgentTextDeltaEvent>().Should().NotBeEmpty("回覆要以串流片段送出");
            events.OfType<AgentQuestionEvent>().Should().ContainSingle();
            events.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
            AllText(events).Should().Contain("藍");
            AllText(events).Should().Contain("紅茶");
            Directory.Exists(Path.Combine(Workspace, "it_dir")).Should().BeTrue("核准後指令要真的執行");
            output.WriteLine($"核准卡：{events.OfType<AgentApprovalEvent>().Count()} 張");
        }

        // 新程序以 --resume 續接，要記得前文
        using (var resumed = new ClaudeAgentSession())
        {
            var events = await RunTurnAsync(resumed, new AgentTurnInput("我剛才選了什麼飲料？只回答飲料名稱。", Array.Empty<AgentAttachment>()),
                Settings(AgentProvider.Claude, sessionId, started: true, ClaudeModel));
            events.OfType<AgentNoticeEvent>().Should().NotContain(n => n.Message.Contains("找不到原本的 Claude 對話紀錄"));
            events.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
            AllText(events).Should().Contain("紅茶");
        }
    }

    /// <summary>
    /// 上下文用量提示（docs/protocol-notes.md「上下文用量」）：一則很短的訊息，串流的大小與 result 的 modelUsage 視窗都要有；
    /// 這一輪之後會話送 get_context_usage，補上自動壓縮門檻（比視窗小）
    /// </summary>
    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_上下文用量_每次呼叫的大小與modelUsage的視窗_這一輪之後補上自動壓縮門檻()
    {
        using var session = new ClaudeAgentSession();
        var all = new BlockingCollection<AgentEvent>();
        session.EventReceived += all.Add;
        await RunTurnAsync(session, new AgentTurnInput("只回覆 OK，不要用任何工具", Array.Empty<AgentAttachment>()),
            Settings(AgentProvider.Claude, Guid.NewGuid().ToString(), started: false, ClaudeModel));

        var seen = new List<AgentEvent>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !seen.Any(e => e is AgentContextUsageEvent { AutoCompactTokens: > 0 }))
        {
            if (all.TryTake(out var ev, TimeSpan.FromMilliseconds(200)))
                seen.Add(ev);
        }
        var context = seen.OfType<AgentContextUsageEvent>().Aggregate((ContextUsage?)null, ContextUsageRules.Apply);
        output.WriteLine($"上下文：{context}");
        context.Should().NotBeNull();
        context!.WindowTokens.Should().BeGreaterThanOrEqualTo(100_000);
        context.UsedTokens.Should().BeInRange(1_000, context.WindowTokens!.Value, "只有系統提示與工具，約兩萬多");
        context.AutoCompactTokens.Should().BeInRange(1, context.WindowTokens.Value - 1, "get_context_usage 的 autoCompactThreshold");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_找不到原本的Session時_改開新的並附上前文節錄()
    {
        using var session = new ClaudeAgentSession();
        var settings = Settings(AgentProvider.Claude, Guid.NewGuid().ToString(), started: true, ClaudeModel,
            recap: "（系統說明：以下是先前對話的節錄）\n【使用者】我最喜歡的水果是芒果\n（節錄結束）\n\n");

        var events = await RunTurnAsync(session, new AgentTurnInput("我最喜歡什麼水果？只回答水果名稱。", Array.Empty<AgentAttachment>()), settings);

        events.OfType<AgentNoticeEvent>().Should().Contain(n => n.Message.Contains("找不到原本的 Claude 對話紀錄"));
        events.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
        AllText(events).Should().Contain("芒果");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_檔案編輯按本對話都允許_改為自動接受編輯_之後的編輯不再詢問_設定檔仍要核准()
    {
        // 舊審查發現：本機把編輯類整類放行，連 CLI 刻意升級詢問的 .claude 設定檔也自動同意；改為照 CLI 的建議切到自動接受編輯
        var sessionId = Guid.NewGuid().ToString();
        using var session = new ClaudeAgentSession();
        var first = await RunTurnAsync(session, new AgentTurnInput("用 Write 工具建立 a.txt，內容是 a。完成後只回答「完成」。", Array.Empty<AgentAttachment>()),
            Settings(AgentProvider.Claude, sessionId, started: false, ClaudeModel), _ => AgentApprovalChoice.AllowForSession);

        first.OfType<AgentApprovalEvent>().First().AllowSessionLabel.Should().Contain("自動接受編輯", "每步詢問下 CLI 對編輯建議改為自動接受編輯");
        first.OfType<AgentPermissionModeChangedEvent>().Last().Mode.Should().Be(AgentPermissionMode.AcceptEdits);
        first.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
        File.Exists(Path.Combine(Workspace, "a.txt")).Should().BeTrue();

        // 中樞收到模式改變就把對話改成自動接受編輯，下一輪照這個模式送出
        var second = await RunTurnAsync(session, new AgentTurnInput("用 Write 工具建立 b.txt，內容是 b。完成後只回答「完成」。", Array.Empty<AgentAttachment>()),
            Settings(AgentProvider.Claude, sessionId, started: true, ClaudeModel, AgentPermissionMode.AcceptEdits));
        second.OfType<AgentApprovalEvent>().Should().BeEmpty("工作資料夾內的編輯不再詢問");
        File.Exists(Path.Combine(Workspace, "b.txt")).Should().BeTrue();

        // 工作資料夾內的 .claude 設定檔：CLI 的 safety check 仍要詢問（這裡拒絕），不能被自動放行
        var third = await RunTurnAsync(session, new AgentTurnInput("用 Write 工具把 {} 寫進 .claude/settings.local.json。完成後只回答「完成」。", Array.Empty<AgentAttachment>()),
            Settings(AgentProvider.Claude, sessionId, started: true, ClaudeModel, AgentPermissionMode.AcceptEdits), _ => AgentApprovalChoice.Deny);
        output.WriteLine($"設定檔的核准卡：{third.OfType<AgentApprovalEvent>().Count()} 張");
        File.Exists(Path.Combine(Workspace, ".claude", "settings.local.json")).Should().BeFalse("寫 .claude 設定檔要經使用者核准（已拒絕）");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_換了工作資料夾_以逐字稿路徑續接_仍記得前文()
    {
        // 舊審查發現：CLI 依資料夾存逐字稿，換資料夾後以 id 續接必定失敗、上下文被換成節錄
        var sessionId = Guid.NewGuid().ToString();
        var other = Path.Combine(temp.Root, "other");
        Directory.CreateDirectory(other);
        using var session = new ClaudeAgentSession();
        var first = await RunTurnAsync(session, new AgentTurnInput("記住代號：紅鶴77。只回答「好」。", Array.Empty<AgentAttachment>()),
            Settings(AgentProvider.Claude, sessionId, started: false, ClaudeModel));
        first.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);

        var moved = Settings(AgentProvider.Claude, sessionId, started: true, ClaudeModel) with { WorkingDirectory = other };
        var second = await RunTurnAsync(session, new AgentTurnInput("剛才請你記住的代號是什麼？只回答代號。", Array.Empty<AgentAttachment>()), moved);

        second.OfType<AgentNoticeEvent>().Should().NotContain(n => n.Message.Contains("找不到原本的 Claude 對話紀錄"));
        second.OfType<AgentSessionStartedEvent>().First().SessionId.Should().Be(sessionId, "同一個 session 續接");
        second.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
        AllText(second).Should().Contain("紅鶴77");
    }

    /// <summary>
    /// 延伸成新對話（AgentHub.Fork.cs）：原對話的逐字稿當下複製一份，新對話以 --resume 副本 --fork-session --session-id 事先決定的 sid 分支——
    /// 新的代理記得延伸當下的前文、看不到原對話之後才說的，之後的新程序照常以自己的 sid 續接
    /// </summary>
    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_延伸成新對話_從逐字稿副本分支_記得延伸當下的前文_看不到原對話之後的()
    {
        var sessionId = Guid.NewGuid().ToString();
        var original = Settings(AgentProvider.Claude, sessionId, started: false, ClaudeModel);
        string copy;
        using (var session = new ClaudeAgentSession())
        {
            var first = await RunTurnAsync(session, new AgentTurnInput("記住代號：藍鯨42。只回答「好」。", Array.Empty<AgentAttachment>()), original);
            first.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
            var transcript = session.TranscriptFor(original with { SessionStarted = true });
            transcript.Should().NotBeNull();
            copy = Path.Combine(temp.Root, "fork", ConversationForkRules.SourceFolderName, Path.GetFileName(transcript!));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(transcript!, copy);

            // 延伸之後原對話接著說的，新對話不會知道
            var later = await RunTurnAsync(session, new AgentTurnInput("代號改成：白鷺13。只回答「好」。", Array.Empty<AgentAttachment>()),
                original with { SessionStarted = true });
            later.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
        }

        var forkId = Guid.NewGuid().ToString();
        var fork = Settings(AgentProvider.Claude, forkId, started: true, ClaudeModel) with { ForkSource = copy };
        using (var forked = new ClaudeAgentSession())
        {
            var events = await RunTurnAsync(forked, new AgentTurnInput("現在請你記住的代號是什麼？只回答代號。", Array.Empty<AgentAttachment>()), fork);
            events.OfType<AgentNoticeEvent>().Should().NotContain(n => n.Message.Contains("找不到原本的 Claude 對話紀錄"));
            events.OfType<AgentSessionStartedEvent>().First().SessionId.Should().Be(forkId, "分支的 sid 是事先給的");
            events.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
            AllText(events).Should().Contain("藍鯨42").And.NotContain("白鷺13");
        }

        // 分支好了：新程序照常以自己的 sid 續接（中樞已清掉來源）
        using (var resumed = new ClaudeAgentSession())
        {
            var events = await RunTurnAsync(resumed, new AgentTurnInput("再說一次代號，只回答代號。", Array.Empty<AgentAttachment>()), fork with { ForkSource = null });
            events.OfType<AgentNoticeEvent>().Should().NotContain(n => n.Message.Contains("找不到原本的 Claude 對話紀錄"));
            events.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
            AllText(events).Should().Contain("藍鯨42");
        }
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_執行中切到完全自動_下一輪不再跳核准()
    {
        // 舊審查發現：程序啟動時沒帶 --allow-dangerously-skip-permissions，執行中切到完全自動（set_permission_mode bypassPermissions）會被拒，核准卡照樣跳出
        var sessionId = Guid.NewGuid().ToString();
        using var session = new ClaudeAgentSession();
        var first = await RunTurnAsync(session, new AgentTurnInput("只回答「好」。", Array.Empty<AgentAttachment>()),
            Settings(AgentProvider.Claude, sessionId, started: false, ClaudeModel));
        first.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);

        var second = await RunTurnAsync(session, new AgentTurnInput("用 Bash 執行 mkdir full_access_dir，完成後只回答「完成」。", Array.Empty<AgentAttachment>()),
            Settings(AgentProvider.Claude, sessionId, started: true, ClaudeModel, AgentPermissionMode.FullAccess));

        second.OfType<AgentApprovalEvent>().Should().BeEmpty("完全自動不該再跳核准");
        second.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
        Directory.Exists(Path.Combine(Workspace, "full_access_dir")).Should().BeTrue();
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_一輪中途換模型與推理強度_工具之後的呼叫就用新模型_下一輪不重開程序()
    {
        var sessionId = Guid.NewGuid().ToString();
        using var session = new ClaudeAgentSession();
        var settings = Settings(AgentProvider.Claude, sessionId, started: false, ClaudeModel, AgentPermissionMode.FullAccess);
        var events = new BlockingCollection<AgentEvent>();
        void Handler(AgentEvent ev) => events.Add(ev);
        session.EventReceived += Handler;
        bool? switched = null;
        string? instance = null;
        try
        {
            await session.SendAsync(new AgentTurnInput("用 Bash 執行 sleep 6，完成後只回答「完成」。", Array.Empty<AgentAttachment>()), settings);
            var deadline = DateTime.UtcNow + TurnTimeout;
            AgentTurnCompletedEvent? completed = null;
            while (completed == null && DateTime.UtcNow < deadline)
            {
                if (!events.TryTake(out var ev, TimeSpan.FromSeconds(1)))
                    continue;
                output.WriteLine($"{ev.GetType().Name}: {Describe(ev)}");
                if (ev is AgentToolStartedEvent && switched == null)
                {
                    instance = session.ProcessInstance;
                    switched = await session.SetModelAsync("sonnet", null);
                    output.WriteLine($"即時換模型：{switched}");
                }
                completed = ev as AgentTurnCompletedEvent;
            }
            completed.Should().NotBeNull("這一輪要在時限內結束");
            completed!.Outcome.Should().Be(AgentTurnOutcome.Completed);
        }
        finally
        {
            session.EventReceived -= Handler;
        }
        switched.Should().BeTrue("工具執行中送 set_model（與 apply_flag_settings 清回預設強度），CLI 立即回應成功");

        // 逐字稿：工具結果之前的呼叫是 haiku，之後的是 sonnet
        var transcript = Directory.EnumerateFiles(Path.Combine(AccountRules.ResolveConfigDirectory(new AgentAccount { Provider = AgentProvider.Claude }), "projects"),
            sessionId + ".jsonl", SearchOption.AllDirectories).Single();
        var models = File.ReadAllLines(transcript)
            .Where(l => l.TrimStart().StartsWith('{'))
            .Select(JObject.Parse)
            .Where(o => o.Value<string>("type") == "assistant" && o["message"] is JObject)
            .Select(o => o["message"]!.Value<string>("model") ?? string.Empty)
            .Where(m => m.StartsWith("claude-", StringComparison.Ordinal))
            .Distinct()
            .ToList();
        output.WriteLine("逐字稿的模型：" + string.Join("、", models));
        models.Should().HaveCount(2);
        models[0].Should().Contain("haiku");
        models[1].Should().Contain("sonnet", "換模型之後、同一輪的下一次呼叫就用新模型");

        // 下一輪照中樞的設定（已是新模型、預設強度）：同一個程序，init 回報新模型
        var second = await RunTurnAsync(session, new AgentTurnInput("只回答「好」。", Array.Empty<AgentAttachment>()),
            settings with { SessionStarted = true, Model = "sonnet", Effort = null });
        second.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
        second.OfType<AgentSessionStartedEvent>().First().Model.Should().Contain("sonnet");
        session.ProcessInstance.Should().Be(instance, "已即時套用的模型與強度不算設定改變，不重開程序");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_Ultracode_模型清單列出_閒置時開啟後逐字稿有提醒_換走不重開程序()
    {
        var sessionId = Guid.NewGuid().ToString();
        using var session = new ClaudeAgentSession();
        // 啟動帶 --effort low 與同值的環境變數（app 指定強度時一定帶，這時只改 effortLevel 不會生效）
        var settings = Settings(AgentProvider.Claude, sessionId, started: false, ClaudeModel);
        var first = await RunTurnAsync(session, new AgentTurnInput("只回答「好」。", Array.Empty<AgentAttachment>()), settings);
        first.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
        var models = first.OfType<AgentModelsEvent>().Should().ContainSingle("initialize 與 get_settings 都到了才回報一次").Subject.Models;
        output.WriteLine("模型與強度：" + string.Join("；", models.Select(m => m.Value + "=" + string.Join(",", m.Efforts))));
        models.Single(m => m.Value == ClaudeModel).Efforts.Should().Contain("ultracode", "這台的帳戶有開動態工作流程（Max 方案預設開），模型支援 xhigh");
        var instance = session.ProcessInstance;

        (await session.SetModelAsync(ClaudeModel, "ultracode")).Should().BeTrue();
        var second = await RunTurnAsync(session, new AgentTurnInput("只回答「好」。", Array.Empty<AgentAttachment>()),
            settings with { SessionStarted = true, Effort = "ultracode" });
        second.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);

        var transcript = Directory.EnumerateFiles(Path.Combine(AccountRules.ResolveConfigDirectory(new AgentAccount { Provider = AgentProvider.Claude }), "projects"),
            sessionId + ".jsonl", SearchOption.AllDirectories).Single();
        var attachments = File.ReadAllLines(transcript)
            .Where(l => l.TrimStart().StartsWith('{'))
            .Select(JObject.Parse)
            .Where(o => o.Value<string>("type") == "attachment" && o["attachment"] is JObject)
            .Select(o => o["attachment"]!.Value<string>("type") ?? string.Empty)
            .ToList();
        output.WriteLine("逐字稿的 attachment：" + string.Join("、", attachments.Distinct()));
        attachments.Should().Contain("ultra_effort_enter", "開啟 ultracode 之後的下一輪，CLI 對模型加上 ultracode 的提醒（動態工作流程）");

        (await session.SetModelAsync(ClaudeModel, "low")).Should().BeTrue();
        var third = await RunTurnAsync(session, new AgentTurnInput("只回答「好」。", Array.Empty<AgentAttachment>()),
            settings with { SessionStarted = true, Effort = "low" });
        third.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.ProcessInstance.Should().Be(instance, "即時套用的強度（含 ultracode）不算設定改變，不重開程序");
    }

    /// <summary>
    /// 送出一則要跑一個工具的訊息，工具開始後 1.5 秒插入 steerText，收到這一輪結束後再等 3 秒確認沒有第二個結束；回傳（插入成功與否、本輪事件、之後多出來的事件）
    /// </summary>
    private async Task<(bool Steered, List<AgentEvent> Events, List<AgentEvent> After)> RunSteeredTurnAsync(
        IAgentSession session, string prompt, AgentSessionSettings settings, string steerText)
    {
        var events = new BlockingCollection<AgentEvent>();
        void Handler(AgentEvent ev) => events.Add(ev);
        session.EventReceived += Handler;
        try
        {
            await session.SendAsync(new AgentTurnInput(prompt, Array.Empty<AgentAttachment>()), settings);
            var collected = new List<AgentEvent>();
            bool? steered = null;
            var deadline = DateTime.UtcNow + TurnTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!events.TryTake(out var ev, TimeSpan.FromSeconds(1)))
                    continue;
                collected.Add(ev);
                output.WriteLine($"{ev.GetType().Name}: {Describe(ev)}");
                if (ev is AgentToolStartedEvent && steered == null)
                {
                    await Task.Delay(1500);
                    steered = await session.SteerAsync(new AgentTurnInput(steerText, Array.Empty<AgentAttachment>()));
                    output.WriteLine($"插入：{steered}");
                }
                if (ev is AgentTurnCompletedEvent)
                {
                    var after = new List<AgentEvent>();
                    var quiet = DateTime.UtcNow.AddSeconds(3);
                    while (DateTime.UtcNow < quiet)
                    {
                        if (events.TryTake(out var late, TimeSpan.FromMilliseconds(200)))
                            after.Add(late);
                    }
                    return (steered ?? false, collected, after);
                }
            }
            throw new TimeoutException("這一輪沒有在時限內結束");
        }
        finally
        {
            session.EventReceived -= Handler;
        }
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_工具執行中插入訊息_在工具結果之後併進同一輪_只結束一次()
    {
        using var session = new ClaudeAgentSession();
        var (steered, events, after) = await RunSteeredTurnAsync(session,
            "用 Bash 執行 sleep 6 && echo slept > steer.txt（只執行這一個指令），然後只回覆 done",
            Settings(AgentProvider.Claude, Guid.NewGuid().ToString(), started: false, ClaudeModel, AgentPermissionMode.FullAccess),
            "補充：回覆的最後加上 BANANA 這個字");

        steered.Should().BeTrue();
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        AllText(events).Should().Contain("BANANA", "插入的訊息在工具結果之後交給模型，同一輪就回應");
        after.OfType<AgentTurnCompletedEvent>().Should().BeEmpty("併進同一輪，不會再有第二輪");
        session.IsRunning.Should().BeFalse();
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_背景工作完成後CLI自己開的一輪_一開始就標成執行中_它的result結束這一輪()
    {
        using var session = new ClaudeAgentSession();
        var all = new BlockingCollection<AgentEvent>();
        bool? runningAtStart = null;
        session.EventReceived += ev =>
        {
            // 在讀取執行緒上、事件發出的當下記：那一輪很短時，測試執行緒讀到的可能已經結束
            if (ev is AgentIdleTurnStartedEvent)
                runningAtStart = session.IsRunning;
            all.Add(ev);
        };

        // 背景工作要撐過這一輪的 result（太早完成會併進使用者這一輪，CLI 就不會自己開一輪）
        var first = await RunTurnAsync(session, new AgentTurnInput(
                "用 Bash 工具、run_in_background 設成 true，執行 sleep 20 && echo bgdone（只執行這一個指令，不要等它結束），啟動後只回覆 started",
                Array.Empty<AgentAttachment>()),
            Settings(AgentProvider.Claude, Guid.NewGuid().ToString(), started: false, ClaudeModel, AgentPermissionMode.FullAccess));
        first.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.IsRunning.Should().BeFalse();
        session.HasLiveBackgroundWork.Should().BeTrue("背景工作還在跑");

        while (all.TryTake(out var ev) && ev is not AgentTurnCompletedEvent)
        {
        }
        var own = new List<AgentEvent>();
        var deadline = DateTime.UtcNow + TurnTimeout;
        while (DateTime.UtcNow < deadline && own.LastOrDefault() is not AgentTurnCompletedEvent)
        {
            if (!all.TryTake(out var ev, TimeSpan.FromSeconds(1)))
                continue;
            own.Add(ev);
            output.WriteLine($"{ev.GetType().Name}: {Describe(ev)}");
        }

        own.OfType<AgentIdleTurnStartedEvent>().Should().ContainSingle("CLI 自己開那一輪的 system/init：會話標成執行中")
            .Which.AfterBackgroundTask.Should().BeTrue("之前收到了背景工作完成的通知");
        runningAtStart.Should().BeTrue();
        var startAt = own.FindIndex(e => e is AgentIdleTurnStartedEvent);
        var firstOutput = own.FindIndex(e => e is AgentTextDeltaEvent or AgentTextCompletedEvent or AgentThinkingDeltaEvent or AgentToolStartedEvent);
        firstOutput.Should().BeGreaterThan(startAt, "第一個輸出之前就標成執行中（畫面在回覆出來之前就轉圈）");
        own.OfType<AgentTurnCompletedEvent>().Should().ContainSingle("origin 是 task-notification 的 result 結束這一輪，不會一直卡在執行中")
            .Which.Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.IsRunning.Should().BeFalse();
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Codex_背景指令_這一輪結束時還在跑就回報背景工作_跑完回報結束碼與耗時_不會自己開一輪()
    {
        using var session = new CodexAgentSession();
        var all = new BlockingCollection<AgentEvent>();
        session.EventReceived += all.Add;

        // 同 tools/probes/codex_background_probe.py 的 session 模式：要它留在終端機裡跑（用 Start-Process 另開的程序 Codex 不追蹤）
        var first = await RunTurnAsync(session, new AgentTurnInput(
                "直接執行這個 PowerShell 指令：`Start-Sleep 20; Set-Content bg.txt done`（不要用 Start-Process、Start-Job 或任何方式另開程序）。"
                + "它要跑約 20 秒：執行時只等 1 秒就好，讓它留在終端機裡繼續跑，不要等它結束、也不要再查看它，立刻只回覆 started 這個字。",
                Array.Empty<AgentAttachment>()),
            Settings(AgentProvider.Codex, null, started: false, CodexModel, AgentPermissionMode.FullAccess));

        var background = first.OfType<AgentBackgroundWorkEvent>().Should().ContainSingle("這一輪結束時指令還在跑（模型沒照指示等完了它的話，這裡會失敗：看輸出的事件）").Subject;
        background.Count.Should().Be(1);
        var toolId = background.ToolIds.Should().ContainSingle().Subject;
        first.FindIndex(e => e is AgentBackgroundWorkEvent).Should().BeLessThan(first.FindIndex(e => e is AgentTurnCompletedEvent));
        first.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.HasLiveBackgroundWork.Should().BeTrue();
        session.IsAtSafePoint.Should().BeFalse();

        while (all.TryTake(out var ev) && ev is not AgentTurnCompletedEvent)
        {
        }
        var after = new List<AgentEvent>();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline && after.LastOrDefault() is not AgentBackgroundCommandFinishedEvent)
        {
            if (!all.TryTake(out var ev, TimeSpan.FromSeconds(1)))
                continue;
            after.Add(ev);
            output.WriteLine($"{ev.GetType().Name}: {Describe(ev)}");
        }

        after.OfType<AgentToolCompletedEvent>().Should().ContainSingle(t => t.ToolId == toolId, "一輪結束後才送來的 item/completed 照樣結束那張卡");
        var finished = after.OfType<AgentBackgroundCommandFinishedEvent>().Should().ContainSingle().Subject;
        finished.ToolId.Should().Be(toolId);
        finished.ExitCode.Should().Be(0);
        finished.Duration.Should().BeGreaterThan(TimeSpan.FromSeconds(15));
        after.OfType<AgentBackgroundWorkEvent>().Should().ContainSingle().Which.Count.Should().Be(0);
        after.Should().NotContain(e => e is AgentTurnStartedByCliEvent || e is AgentIdleTurnStartedEvent || e is AgentTurnCompletedEvent,
            "Codex 不會自己開一輪（接續由中樞隱藏送出）");
        File.Exists(Path.Combine(Workspace, "bg.txt")).Should().BeTrue();
        session.HasLiveBackgroundWork.Should().BeFalse();
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Codex_工具執行中以turnSteer插入訊息_併進同一輪_只結束一次()
    {
        using var session = new CodexAgentSession();
        var (steered, events, after) = await RunSteeredTurnAsync(session,
            "用 PowerShell 執行 `Start-Sleep 6; Set-Content steer.txt slept`（只執行這一個指令），然後只回覆 done",
            Settings(AgentProvider.Codex, null, started: false, CodexModel, AgentPermissionMode.FullAccess),
            "補充：回覆的最後加上 BANANA 這個字");

        steered.Should().BeTrue();
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        AllText(events).Should().Contain("BANANA");
        after.OfType<AgentTurnCompletedEvent>().Should().BeEmpty();
        session.IsRunning.Should().BeFalse();
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Codex_執行指令中按停止_這一輪結束為已停止()
    {
        using var session = new CodexAgentSession();
        var events = new BlockingCollection<AgentEvent>();
        session.EventReceived += ev => events.Add(ev);
        await session.SendAsync(new AgentTurnInput("執行 ping -n 60 127.0.0.1 並回報結果。", Array.Empty<AgentAttachment>()),
            Settings(AgentProvider.Codex, null, started: false, CodexModel, AgentPermissionMode.FullAccess));

        var deadline = DateTime.UtcNow + TurnTimeout;
        AgentTurnCompletedEvent? completed = null;
        bool interrupted = false;
        while (DateTime.UtcNow < deadline && completed == null)
        {
            if (!events.TryTake(out var ev, TimeSpan.FromSeconds(1)))
                continue;
            if (ev is AgentToolStartedEvent && !interrupted)
            {
                await Task.Delay(3000);
                await session.InterruptAsync();
                interrupted = true;
            }
            completed = ev as AgentTurnCompletedEvent;
        }

        interrupted.Should().BeTrue("要在指令執行中按停止");
        completed.Should().NotBeNull();
        completed!.Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        session.IsRunning.Should().BeFalse();
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Codex_串流文字_看圖_問卷_核准_跨程序續接()
    {
        string threadId;

        using (var session = new CodexAgentSession())
        {
            var events = await RunTurnAsync(session, new AgentTurnInput(
                "依序做：1) 一句話說圖片顏色；2) 用 request_user_input 問我單選題：咖啡或紅茶；3) 在目前資料夾執行 mkdir it_codex；4) 一句話說我選了什麼。",
                new[] { BluePng() }), Settings(AgentProvider.Codex, null, started: false, CodexModel));

            threadId = events.OfType<AgentSessionStartedEvent>().First().SessionId;
            events.OfType<AgentTextDeltaEvent>().Should().NotBeEmpty("回覆要以串流片段送出");
            events.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
            AllText(events).Should().Contain("藍");
            events.OfType<AgentApprovalEvent>().Should().NotBeEmpty("每步詢問模式下執行指令要先核准");
            Directory.Exists(Path.Combine(Workspace, "it_codex")).Should().BeTrue();
            output.WriteLine($"有問卷：{events.OfType<AgentQuestionEvent>().Any()}（模型可能直接用文字問，不列為失敗）");
        }

        using (var resumed = new CodexAgentSession())
        {
            var events = await RunTurnAsync(resumed, new AgentTurnInput("這段對話裡你看過的圖片是什麼顏色？只回答顏色。", Array.Empty<AgentAttachment>()),
                Settings(AgentProvider.Codex, threadId, started: true, CodexModel));
            events.OfType<AgentNoticeEvent>().Should().NotContain(n => n.Message.Contains("找不到原本的 Codex 對話紀錄"));
            events.OfType<AgentSessionStartedEvent>().First().SessionId.Should().Be(threadId, "以 thread/resume 續接同一個 thread");
            AllText(events).Should().Contain("藍");
        }
    }

    /// <summary>上下文用量提示：thread/tokenUsage/updated 的 last.totalTokens 與 modelContextWindow</summary>
    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Codex_上下文用量_last的totalTokens與modelContextWindow()
    {
        using var session = new CodexAgentSession();
        var events = await RunTurnAsync(session, new AgentTurnInput("只回覆 OK，不要執行任何指令", Array.Empty<AgentAttachment>()),
            Settings(AgentProvider.Codex, null, started: false, CodexModel));
        var context = events.OfType<AgentContextUsageEvent>().Aggregate((ContextUsage?)null, ContextUsageRules.Apply);
        output.WriteLine($"上下文：{context}");
        context.Should().NotBeNull();
        context!.WindowTokens.Should().BeGreaterThan(100_000);
        context.UsedTokens.Should().BeInRange(1_000, context.WindowTokens!.Value);
        context.AutoCompactTokens.Should().BeNull("Codex 不回報自動壓縮門檻");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Codex_準備期間按停止_這一輪不會開始()
    {
        using var session = new CodexAgentSession();
        var events = new BlockingCollection<AgentEvent>();
        session.EventReceived += ev => events.Add(ev);
        var send = session.SendAsync(new AgentTurnInput("執行 mkdir should_not_exist 後回答完成。", Array.Empty<AgentAttachment>()),
            Settings(AgentProvider.Codex, null, started: false, CodexModel, AgentPermissionMode.FullAccess));
        await session.InterruptAsync();
        await send;

        AgentTurnCompletedEvent? completed = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (completed == null && DateTime.UtcNow < deadline)
        {
            if (events.TryTake(out var ev, TimeSpan.FromSeconds(1)))
                completed = ev as AgentTurnCompletedEvent;
        }

        completed.Should().NotBeNull();
        completed!.Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        session.IsRunning.Should().BeFalse();
        await Task.Delay(3000);
        Directory.Exists(Path.Combine(Workspace, "should_not_exist")).Should().BeFalse();
    }

    /// <summary>等條件成立（會話的狀態在讀取執行緒上改變）</summary>
    private static async Task WaitForAsync(Func<bool> condition, string because, double seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("沒有等到：" + because);
            await Task.Delay(100);
        }
    }

    /// <summary>跑一輪並等到 Remote Control 開啟的事件（開啟在一輪進行中完成，事件可能比 TurnCompleted 晚到）</summary>
    private async Task<(List<AgentEvent> Events, AgentRemoteControlEvent Opened)> RunTurnWithRemoteAsync(IAgentSession session, string text, AgentSessionSettings settings)
    {
        var opened = new TaskCompletionSource<AgentRemoteControlEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(AgentEvent ev)
        {
            if (ev is AgentRemoteControlEvent remote)
                opened.TrySetResult(remote);
        }
        session.EventReceived += Handler;
        try
        {
            var events = await RunTurnAsync(session, new AgentTurnInput(text, Array.Empty<AgentAttachment>()), settings);
            var remote = await opened.Task.WaitAsync(TimeSpan.FromSeconds(60));
            output.WriteLine($"Remote Control：{remote.BridgeSessionId} {remote.SessionUrl}");
            return (events, remote);
        }
        finally
        {
            session.EventReceived -= Handler;
        }
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_工作階段名稱_啟動時照對話標題命名_活著的程序改名_續接的程序照新名稱_每一輪照常完成()
    {
        var sessionId = Guid.NewGuid().ToString();
        var titles = new ConcurrentQueue<string>();
        void OnTitle(AgentEvent ev)
        {
            if (ev is AgentSessionTitleEvent t)
                titles.Enqueue(t.Title);
        }

        using (var session = new ClaudeAgentSession())
        {
            session.EventReceived += OnTitle;
            var events = await RunTurnAsync(session, new AgentTurnInput("只回覆 OK 兩個字母", Array.Empty<AgentAttachment>()),
                Settings(AgentProvider.Claude, sessionId, started: false, ClaudeModel) with { Title = "Myriagent 整合測試 名稱" });
            events.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed, "使用者的訊息之前先改名不影響這一輪");
            await WaitForAsync(() => titles.Contains("Myriagent 整合測試 名稱"), "啟動時改名的回送");

            await session.RenameAsync("Myriagent 整合測試 改名後");
            await WaitForAsync(() => titles.Contains("Myriagent 整合測試 改名後"), "活著的程序立即改名");
        }

        // app 重開：新程序續接，啟動時照（中樞記的）新名稱命名
        titles.Clear();
        using (var resumed = new ClaudeAgentSession())
        {
            resumed.EventReceived += OnTitle;
            var events = await RunTurnAsync(resumed, new AgentTurnInput("只回覆 OK 兩個字母", Array.Empty<AgentAttachment>()),
                Settings(AgentProvider.Claude, sessionId, started: true, ClaudeModel) with { Title = "Myriagent 整合測試 改名後" });
            events.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
            await WaitForAsync(() => titles.Contains("Myriagent 整合測試 改名後"), "續接的程序照新名稱");
            output.WriteLine("續接時收到的名稱：" + string.Join("、", titles));
            titles.Should().OnlyContain(t => t == "Myriagent 整合測試 改名後", "續接時 CLI 報的既有名稱與宿主的改名都是新名稱");
        }

        // 程序不在時改名（例如從其他電腦改）：續接時 CLI 先報 session 存的「改名後」、才回送新名稱；舊的不能交給中樞
        titles.Clear();
        using (var renamedOffline = new ClaudeAgentSession())
        {
            renamedOffline.EventReceived += OnTitle;
            var events = await RunTurnAsync(renamedOffline, new AgentTurnInput("只回覆 OK 兩個字母", Array.Empty<AgentAttachment>()),
                Settings(AgentProvider.Claude, sessionId, started: true, ClaudeModel) with { Title = "Myriagent 整合測試 不在時改名" });
            events.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
            await WaitForAsync(() => titles.Contains("Myriagent 整合測試 不在時改名"), "續接的程序照不在時改的名稱");
            output.WriteLine("不在時改名、續接時收到的名稱：" + string.Join("、", titles));
            titles.Should().OnlyContain(t => t == "Myriagent 整合測試 不在時改名", "CLI 先報的舊名稱（session 存的）不交給中樞");
        }
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_RemoteControl_一輪進行中開啟_新程序接回同一個遠端工作階段_關閉後再開()
    {
        var sessionId = Guid.NewGuid().ToString();
        var remote = new AgentRemoteControlOptions("Myriagent 整合測試", null);
        string bridge;

        using (var session = new ClaudeAgentSession())
        {
            var (events, opened) = await RunTurnWithRemoteAsync(session, "只回覆 OK 兩個字母",
                Settings(AgentProvider.Claude, sessionId, started: false, ClaudeModel) with { RemoteControl = remote });

            events.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed, "一輪進行中開 Remote Control 不影響這一輪");
            events.OfType<AgentNoticeEvent>().Should().BeEmpty("開得起來就不提示");
            opened.BridgeSessionId.Should().StartWith("cse_");
            opened.SessionUrl.Should().StartWith("https://claude.ai/code/");
            await WaitForAsync(() => session.HasRemoteControl, "連上後算開著");
            bridge = opened.BridgeSessionId;
        }

        // app 重開：新的會話物件以中樞記的 id 接回
        using (var resumed = new ClaudeAgentSession())
        {
            var (_, reopened) = await RunTurnWithRemoteAsync(resumed, "只回覆 OK 兩個字母",
                Settings(AgentProvider.Claude, sessionId, started: true, ClaudeModel) with { RemoteControl = remote with { ReattachSessionId = bridge } });
            reopened.BridgeSessionId.Should().Be(bridge, "手機上一直是同一個遠端工作階段");
            await WaitForAsync(() => resumed.HasRemoteControl, "接回後算開著");

            await resumed.SetRemoteControlAsync(null);
            resumed.HasRemoteControl.Should().BeFalse("關閉後閒置照常回收");

            var again = new TaskCompletionSource<AgentRemoteControlEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            resumed.EventReceived += ev =>
            {
                if (ev is AgentRemoteControlEvent r)
                    again.TrySetResult(r);
            };
            await resumed.SetRemoteControlAsync(remote);
            var reenabled = await again.Task.WaitAsync(TimeSpan.FromSeconds(60));
            output.WriteLine($"關閉後再開：{reenabled.BridgeSessionId}（{(reenabled.BridgeSessionId == bridge ? "接回同一個" : "開了新的")}）");
            await WaitForAsync(() => resumed.HasRemoteControl, "再開後算開著");
        }
    }

    [EnvFact("MYRIAGENT_IT_REMOTE", "實際呼叫本機 Claude CLI，並以預設帳戶的權杖經 claude.ai 的工作階段 API 送訊息（等同從網頁送出；權杖只讀不刷新）")]
    public async Task Claude_RemoteControl_從網頁送來的訊息_開始新的一輪_網頁與app幾乎同時送出時app這則等自己的回覆才結束()
    {
        using var session = new ClaudeAgentSession();
        var settings = Settings(AgentProvider.Claude, Guid.NewGuid().ToString(), started: false, ClaudeModel)
            with { RemoteControl = new AgentRemoteControlOptions("Myriagent 整合測試（遠端訊息）", null) };
        var (_, opened) = await RunTurnWithRemoteAsync(session, "只回覆 OK 兩個字母", settings);
        await WaitForAsync(() => session.HasRemoteControl, "連上");
        settings = settings with { SessionStarted = true };

        var events = new BlockingCollection<AgentEvent>();
        session.EventReceived += events.Add;

        // 閒置時從網頁送一則：顯示訊息、開始新的一輪
        await PostRemoteMessageAsync(opened.BridgeSessionId, "只回覆 R 一個字母");
        var idle = await CollectUntilAsync(events, e => e is AgentTurnCompletedEvent);
        idle.OfType<AgentRemoteUserMessageEvent>().Single().Should().Be(new AgentRemoteUserMessageEvent("只回覆 R 一個字母", 0, 0, StartsTurn: true));
        idle.OfType<AgentTextCompletedEvent>().Select(t => t.Text.Trim()).Should().Contain("R");
        idle.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);

        // 網頁先送、app 0.3 秒後也送（中樞可能還沒收到遠端訊息的事件）：誰先開始都一樣，第一個結束事件要在 app 這則的回覆之後
        await PostRemoteMessageAsync(opened.BridgeSessionId, "只回覆 X 一個字母");
        await Task.Delay(300);
        await session.SendAsync(new AgentTurnInput("只回覆 H 一個字母", Array.Empty<AgentAttachment>()), settings);
        var mine = await CollectUntilAsync(events, e => e is AgentTurnCompletedEvent);
        mine.OfType<AgentTextCompletedEvent>().Select(t => t.Text.Trim()).Should().Contain("H",
            "遠端那一輪的 result 不能結束 app 這一輪（否則 app 這則的回覆會落在閒置的對話上）");
        mine.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);

        // app 這則先開始時，網頁那則排在後面另開一輪：等它做完再結束
        var all = mine.ToList();
        if (!all.OfType<AgentTextCompletedEvent>().Any(t => t.Text.Trim() == "X"))
            all.AddRange(await CollectUntilAsync(events, e => e is AgentTurnCompletedEvent));
        all.OfType<AgentRemoteUserMessageEvent>().Single().Text.Should().Be("只回覆 X 一個字母");
        all.OfType<AgentTextCompletedEvent>().Select(t => t.Text.Trim()).Should().Contain("X");
    }

    [EnvFact("MYRIAGENT_IT_REMOTE", "實際呼叫本機 Claude CLI，並以預設帳戶的權杖經 claude.ai 的工作階段 API 送訊息與停止（等同從網頁操作；權杖只讀不刷新）")]
    public async Task Claude_RemoteControl_從網頁按停止是已停止_app這則排在遠端那一輪後面時按停止也會停下()
    {
        using var session = new ClaudeAgentSession();
        var settings = Settings(AgentProvider.Claude, Guid.NewGuid().ToString(), started: false, ClaudeModel)
            with { RemoteControl = new AgentRemoteControlOptions("Myriagent 整合測試（遠端停止）", null) };
        var (_, opened) = await RunTurnWithRemoteAsync(session, "只回覆 OK 兩個字母", settings);
        await WaitForAsync(() => session.HasRemoteControl, "連上");
        settings = settings with { SessionStarted = true };

        var events = new BlockingCollection<AgentEvent>();
        session.EventReceived += events.Add;
        const string LongTask = "用 Bash 工具執行 sleep 8（只執行這一個指令），然後只回覆 done";

        // 網頁送一則要跑 8 秒的指令，工具執行中從網頁按停止：CLI 插進停止的標記後回 error_during_execution
        await PostRemoteMessageAsync(opened.BridgeSessionId, LongTask);
        await WaitForRunningToolAsync(session, events);
        await PostRemoteInterruptAsync(opened.BridgeSessionId);
        var stopped = await CollectUntilAsync(events, e => e is AgentTurnCompletedEvent);
        stopped.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted, "在網頁或手機上按停止不是失敗");

        // 再送一則；工具執行中 app 送出（排在遠端那則後面）再按停止：中斷停的是遠端那一輪，接著開始的 app 這則要再停一次
        await PostRemoteMessageAsync(opened.BridgeSessionId, LongTask);
        await WaitForRunningToolAsync(session, events);
        await session.SendAsync(new AgentTurnInput("只回覆 H 一個字母", Array.Empty<AgentAttachment>()), settings);
        await Task.Delay(1000);
        await session.InterruptAsync();
        var queued = await CollectUntilAsync(events, e => e is AgentTurnCompletedEvent);
        queued.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        queued.OfType<AgentTextCompletedEvent>().Select(t => t.Text.Trim()).Should().NotContain("H", "app 這則開始時再停一次，不照常回覆");
        session.IsRunning.Should().BeFalse();
    }

    /// <summary>等遠端那一輪的 Bash 開始執行（或跳核准）：之後 3 秒內的核准卡一律允許（卡片與核准的先後不一定、sleep 也可能不必核准），讓 sleep 跑起來</summary>
    private async Task WaitForRunningToolAsync(IAgentSession session, BlockingCollection<AgentEvent> events)
    {
        var pending = (await CollectUntilAsync(events, e => e is AgentApprovalEvent { ToolKind: "bash" } or AgentToolStartedEvent { ToolKind: "bash" }))
            .OfType<AgentApprovalEvent>().ToList();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            foreach (var approval in pending)
                await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
            pending.Clear();
            if (DateTime.UtcNow >= deadline)
                return;
            if (events.TryTake(out var ev, TimeSpan.FromMilliseconds(200)))
            {
                output.WriteLine($"{ev.GetType().Name}: {Describe(ev)}");
                if (ev is AgentApprovalEvent next)
                    pending.Add(next);
            }
        }
    }

    /// <summary>讀事件直到 until 成立（含該事件）</summary>
    private async Task<List<AgentEvent>> CollectUntilAsync(BlockingCollection<AgentEvent> events, Func<AgentEvent, bool> until)
    {
        var collected = new List<AgentEvent>();
        var deadline = DateTime.UtcNow + TurnTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!events.TryTake(out var ev, TimeSpan.FromSeconds(1)))
                continue;
            collected.Add(ev);
            output.WriteLine($"{ev.GetType().Name}: {Describe(ev)}");
            if (until(ev))
                return collected;
        }
        throw new TimeoutException("沒有等到事件；收到：" + string.Join("、", collected.Select(e => e.GetType().Name)));
    }

    /// <summary>
    /// 照 CLI 對遠端工作階段送事件的格式（2.1.301，見 tools/probes/claude_remote_replay_probe.py）以網頁的身分送一則訊息
    /// </summary>
    private static Task PostRemoteMessageAsync(string bridgeSessionId, string text)
        => PostRemoteEventAsync(bridgeSessionId, new JObject
        {
            ["type"] = "user",
            ["message"] = new JObject { ["role"] = "user", ["content"] = text },
            ["parent_tool_use_id"] = JValue.CreateNull(),
            ["session_id"] = "session_" + bridgeSessionId["cse_".Length..],
            ["uuid"] = Guid.NewGuid().ToString(),
        });

    /// <summary>以網頁的身分按停止（執行檔裡 interruptRemoteSession 的格式）</summary>
    private static Task PostRemoteInterruptAsync(string bridgeSessionId)
        => PostRemoteEventAsync(bridgeSessionId, new JObject
        {
            ["type"] = "control_request",
            ["request_id"] = Guid.NewGuid().ToString(),
            ["request"] = new JObject { ["subtype"] = "interrupt" },
            ["uuid"] = Guid.NewGuid().ToString(),
        });

    /// <summary>以網頁的身分對遠端工作階段送一個事件：權杖與組織從預設帳戶讀（只讀，快過期就失敗——刷新會輪換 refresh token，CLI 會被登出）</summary>
    private static async Task PostRemoteEventAsync(string bridgeSessionId, JObject payload)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var oauth = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(home, ".claude", ".credentials.json")))["claudeAiOauth"] as JObject;
        var token = oauth?.Value<string>("accessToken");
        token.Should().NotBeNullOrEmpty();
        DateTimeOffset.FromUnixTimeMilliseconds(oauth!.Value<long?>("expiresAt") ?? 0).Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(2),
            "權杖快過期：先讓 CLI 自己更新（測試不刷新權杖）");
        var org = (JObject.Parse(await File.ReadAllTextAsync(Path.Combine(home, ".claude.json")))["oauthAccount"] as JObject)?.Value<string>("organizationUuid");
        org.Should().NotBeNullOrEmpty();

        var body = new JObject { ["events"] = new JArray(new JObject { ["payload"] = payload }) };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.anthropic.com/v1/code/sessions/{bridgeSessionId}/events")
        {
            Content = new StringContent(body.ToString(Formatting.None), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", "ccr-byoc-2025-07-29");
        request.Headers.Add("x-organization-uuid", org);
        // 伺服器依它填 client_platform：ios、android、web_claude_ai、desktop_app 才是真人送的（其他會被 CLI 降級成 peer 訊息）
        request.Headers.Add("anthropic-client-platform", "web_claude_ai");
        using var response = await http.SendAsync(request);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }
}
