using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.Hub;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;
using Xunit.Abstractions;
using static Myriagent.Tests.Integration.HubIntegration;

namespace Myriagent.Tests.Integration;

/// <summary>
/// app 換版時留著還在等背景工作的對話的 CLI 程序（代理主機 H1，設計 docs/plans/2026-11-26-agent-host.md），以這台電腦真的
/// Claude Code／Codex CLI、真的代理主機驗證整條路：對話的程序經主機開 → 這一輪結束、背景指令還在跑 → 排空時放開程序（不等、不停）→
/// resume.json 存檔再讀回 → 舊的 app 交棒主機後結束 → 同一個資料夾建新的中樞、新的主機用戶端、新的會話物件（等於新版啟動）→
/// 主視窗 ready 後接回同一個程序 → 背景指令照常跑完、結果照常回到對話，沒有「上次結束時停掉了背景工作」的接續訊息，之後的訊息沿用同一個程序。
/// 一輪還在執行時換版（H2，設計第 10 節）：指令跑到一半就排空 → 不停下這一輪、放開程序 → 新的中樞還原這一輪再接回 →
/// 指令照常跑完、結果寫進原本那張卡、這一輪照常回覆完，沒有接續訊息。
/// 帳戶與精簡模式同 <see cref="AppUpdateResumeIntegrationTests"/>。會用到訂閱額度：各約 2～3 輪（haiku／gpt-6-luna、low）。
/// 執行：MYRIAGENT_IT=1 dotnet test ... --filter "FullyQualifiedName~AppUpdateDetachIntegrationTests"
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AppUpdateDetachIntegrationTests
{
    private const string Reason = "以本機 Claude Code／Codex 與真的代理主機跑對話，背景指令還在跑時換一個中樞接回（會用到訂閱額度）";
    private const string Token = "tok-it";
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(4);

    private readonly ITestOutputHelper output;

    public AppUpdateDetachIntegrationTests(ITestOutputHelper output) => this.output = output;

    [EnvFact("MYRIAGENT_IT", Reason)]
    public Task Claude_背景指令還在跑時換版_新中樞接回同一個程序_背景指令跑完CLI自己回報_沒有接續訊息()
        => RunAsync(AgentProvider.Claude, "haiku",
            "用 Bash 工具、run_in_background 設成 true，執行 sleep 40 && echo bgdone（只執行這一個指令，不要等它結束、不要查看它），啟動後只回覆 started");

    [EnvFact("MYRIAGENT_IT", Reason)]
    public Task Codex_背景指令還在跑時換版_新中樞接回同一個程序_跑完的結果寫進原本的卡片_請代理接著處理()
        => RunAsync(AgentProvider.Codex, "gpt-6-luna",
            "直接執行這個 PowerShell 指令：`Start-Sleep 40; Set-Content bg.txt done`（不要用 Start-Process、Start-Job 或任何方式另開程序）。"
            + "它要跑約 40 秒：執行時只等 1 秒就好，讓它留在終端機裡繼續跑，不要等它結束、也不要再查看它，立刻只回覆 started 這個字。");

    [EnvFact("MYRIAGENT_IT", Reason)]
    public Task Claude_指令跑到一半換版_這一輪不停下_新中樞接回後照常做完()
        => RunTurnAsync(AgentProvider.Claude, "haiku",
            // Claude 的 Bash 工具不准前景執行 sleep 30（要求改用背景或 Monitor）：用 ping 佔住約 30 秒
            "用 Bash 工具（不要設 run_in_background，要等它結束）執行 ping -n 31 127.0.0.1 > /dev/null && echo fgdone，等它結束之後，只回覆最後印出的那個英文單字",
            "fgdone", "ping -n 31", "我第一則訊息請你執行的指令裡，ping 的 -n 後面是哪個數字？只回覆那個數字", "31");

    [EnvFact("MYRIAGENT_IT", Reason)]
    public Task Codex_指令跑到一半換版_這一輪不停下_新中樞接回後照常做完()
        => RunTurnAsync(AgentProvider.Codex, "gpt-6-luna",
            "直接執行這個 PowerShell 指令（不要用 Start-Process、Start-Job 或任何方式另開程序）：`Start-Sleep 30; Set-Content fg.txt done`。"
            + "一定要等它真的結束（還沒結束就繼續等），結束之後只回覆 finished 這個字。",
            "finished", "fg.txt", "我第一則訊息請你執行的指令裡，要睡幾秒？只回覆那個數字", "30");

    [EnvFact("MYRIAGENT_IT", Reason)]
    public Task Grok_指令跑到一半換版_這一輪不停下_新中樞接回後照常做完()
        => RunTurnAsync(AgentProvider.Grok, "grok-4.6",
            "用終端機工具執行這個指令（不要放到背景，要等它結束）：ping -n 31 127.0.0.1。等它結束之後，只回覆 finished 這個字。",
            "finished", "ping -n 31", "我第一則訊息請你執行的指令裡，ping 的 -n 後面是哪個數字？只回覆那個數字", "31");

    [EnvFact("MYRIAGENT_IT", Reason)]
    public Task Antigravity_指令跑到一半換版_這一輪不停下_新中樞接回後照常做完()
        => RunTurnAsync(AgentProvider.Antigravity, "gemini-3.8-flash",
            "用 run_command 執行這個指令並等它結束（不要放到背景）：ping -n 31 127.0.0.1。等它結束之後，只回覆 finished 這個字。",
            "finished", "ping -n 31", "我第一則訊息請你執行的指令裡，ping 的 -n 後面是哪個數字？只回覆那個數字", "31");

    /// <summary>
    /// Grok 的帳戶同 GrokIntegrationTests：已登入的隔離 GROK_HOME（MYRIAGENT_IT_GROK_HOME，沒設就用探針的 home-a）；
    /// MYRIAGENT_IT_GROK_HOME=default 改用這台系統預設的 ~/.grok（探針的登入過期時用）
    /// </summary>
    private static AgentAccount SeedGrok(HubHarness h)
    {
        var home = Environment.GetEnvironmentVariable("MYRIAGENT_IT_GROK_HOME");
        bool systemDefault = string.Equals(home, "default", StringComparison.OrdinalIgnoreCase);
        var account = new AgentAccount
        {
            Id = "it-grok",
            Provider = AgentProvider.Grok,
            Label = "Grok（整合測試）",
            ConfigDirectory = systemDefault
                ? null
                : home ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "myriagent-probes", "grok", "home-a"),
        };
        if (!systemDefault)
            GrokAcpRules.EnsureIsolatedHome(account.ConfigDirectory!);
        h.Ui.Invoke(() => h.Accounts.Seed(new[] { account }, AccountStatus.Ready));
        return account;
    }

    private static AgentAccount SeedAntigravity(HubHarness h)
    {
        var account = new AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity（本機）" };
        h.Ui.Invoke(() => h.Accounts.Seed(new[] { account }, AccountStatus.Ready));
        return account;
    }

    /// <summary>一輪執行到一半（前景的指令還在跑）時換版：放開、新的中樞還原並接回、這一輪照常做完</summary>
    /// <param name="answer">這一輪最後的回覆要有的字</param>
    /// <param name="command">那個指令的工具卡認得出來的字</param>
    /// <param name="followUp">接回之後再問的一則（確認是同一個程序、記得前文）與回覆要有的字</param>
    private async Task RunTurnAsync(AgentProvider provider, string model, string prompt, string answer, string command, string followUp, string followUpAnswer)
    {
        var sessions = new Sessions();
        using var hostRoot = new TempDirectory("it-detach-turn");
        AgentHostClient NewHost() => new(Path.Combine(AppContext.BaseDirectory, AgentHostClient.ExecutableName), hostRoot.Root, hostRoot.Combine("host.log"));
        using var h = new HubHarness(sessionFactory: sessions.Create);
        var account = provider switch
        {
            AgentProvider.Claude => h.Claude,
            AgentProvider.Codex => SeedCodexDefault(h),
            AgentProvider.Grok => SeedGrok(h),
            AgentProvider.Antigravity => SeedAntigravity(h),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        // 精簡模式（不載入這台的自訂）：Claude、Codex 同其他整合測試；Grok、Antigravity 照平常的
        account.SafeMode = provider is AgentProvider.Claude or AgentProvider.Codex;
        CliLocator.Locate(provider).Should().NotBeNull($"這台電腦要安裝 {provider} CLI");
        var first = NewHost();
        AgentHostClient? second = null;
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachProcessHost(first);
            h.Hub.HandoffPoll = TimeSpan.FromMilliseconds(50);
        });

        bool IsCommandCard(AgentChatItem i) => i.Kind == AgentItemKind.Tool && $"{i.Title}\n{i.Detail}".Contains(command, StringComparison.OrdinalIgnoreCase);

        try
        {
            var id = h.NewConversation(h.Main, account);
            SetOptions(h, id, model, "low");
            var workspace = h.Store.GetDefaultWorkingDirectory(id);
            h.Send(h.Main, id, prompt);
            await WaitUntilAsync(() => sessions.Count == 1 && h.Items(id).Any(i => IsCommandCard(i) && i.Status == AgentItemStatus.Running), "指令開始執行", TurnTimeout);
            var before = sessions[0];
            await WaitUntilAsync(() => before.Session.CanDetachProcess, "執行中的一輪放得開（有檢查點、沒有等回應的請求）", TimeSpan.FromSeconds(20));
            h.IsRunning(id).Should().BeTrue();
            before.Of<AgentTurnCompletedEvent>().Should().BeEmpty("指令還在跑");
            var instance = before.Session.ProcessInstance;
            var card = h.Items(id).Last(IsCommandCard);
            int commandCards = h.Items(id).Count(IsCommandCard);

            // 1. 排空：不等這一輪、也不停下它——放開程序
            var startedAt = DateTime.UtcNow;
            var preparation = await h.OnUi(() => h.Hub.PrepareForAppUpdateAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(30));
            output.WriteLine($"排空：{preparation}（{(DateTime.UtcNow - startedAt).TotalSeconds:F1} 秒）");
            preparation.Ready.Should().BeTrue(preparation.Reason);
            preparation.Held.Should().Be(0, "沒有停下任何一輪");
            before.Session.HasLiveProcess.Should().BeFalse("程序交給主機了");
            before.Of<AgentProcessExitedEvent>().Should().BeEmpty("放開不是結束程序");
            before.Of<AgentTurnCompletedEvent>().Should().BeEmpty("這一輪沒有被結束");
            h.IsRunning(id).Should().BeTrue();
            var built = h.OnUi(() => h.Hub.BuildAppResume(Token, "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));
            ResumeFileStore.Save(AppPaths.Root, built);
            var file = ResumeFileStore.Load(AppPaths.Root)!;
            var attached = file.Attached.Should().ContainSingle(a => a.Id == id, "resume.json 存檔再讀回，接回要用的狀態還在").Subject;
            attached.Turn.Should().NotBeNull("這一輪在中樞的狀態跟著過去");
            attached.Turn!.Statuses.Should().ContainKey(card.Id);
            file.Conversations.Should().BeEmpty();
            output.WriteLine($"接回的狀態：{attached.State!.ToString(Newtonsoft.Json.Formatting.None)[..Math.Min(1500, attached.State.ToString(Newtonsoft.Json.Formatting.None).Length)]}");

            // 2. 舊的 app 結束（中樞收尾、主機交棒），新版啟動：同一個資料夾建新的中樞、新的主機用戶端、新的會話物件
            await h.RestartAsync();
            first.Handoff(TimeSpan.FromMinutes(3)).Should().BeTrue("主機留著放開的程序等新版");
            second = NewHost();
            var newHost = second;
            h.Ui.Invoke(() =>
            {
                h.Hub.AttachProcessHost(newHost);
                h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeIntentRules.Validate(file, Token, DateTime.UtcNow), "0.2.2", RolledBack: false));
            });
            h.Command(h.Main, "ready");
            await WaitUntilAsync(() => sessions.Count == 2 && sessions[1].Session.HasLiveProcess && sessions[1].Of<AgentProcessStartedEvent>().Count > 0, "新的會話接回程序", TimeSpan.FromSeconds(30));
            var after = sessions[1];
            after.Session.ProcessInstance.Should().Be(instance, "同一個程序");
            output.WriteLine($"接回：{(DateTime.UtcNow - startedAt).TotalSeconds:F1} 秒後");

            // 3. 這一輪照常做完：指令的結果寫進原本那張卡，回覆完
            await WaitUntilAsync(() => after.Of<AgentTurnCompletedEvent>().Count >= 1 && !h.IsRunning(id), "接回的那一輪做完", TurnTimeout);
            after.Of<AgentTurnCompletedEvent>().First().Event.Outcome.Should().Be(AgentTurnOutcome.Completed);
            after.Of<AgentTextCompletedEvent>().Last().Event.Text.Should().Contain(answer);
            // 放開前那張卡收到了真正的結果（不是「被中斷、結果未知」）：接回之後的輸出寫進原本那張卡。
            // 代理看了結果再跑一次（Antigravity 的沙箱裡 ping 有時失敗）會多一張新的卡，那不是重複
            var original = h.Items(id).Single(i => i.Id == card.Id);
            original.Status.Should().BeOneOf(new[] { AgentItemStatus.Done, AgentItemStatus.Failed }, "不是「被中斷、結果未知」");
            if (original.Status == AgentItemStatus.Done)
                h.Items(id).Count(IsCommandCard).Should().Be(commandCards, "沒有多出重複的卡片");
            h.Items(id).Last(IsCommandCard).Status.Should().Be(AgentItemStatus.Done);
            if (provider == AgentProvider.Codex)
                File.ReadAllText(Path.Combine(workspace, "fg.txt")).Trim().Should().Be("done", "指令真的跑完了");
            h.Items(id).Should().NotContain(i => i.Kind == AgentItemKind.Notice && i.NoticeKind == AgentNoticeKind.AppUpdate, "這一輪沒有中斷：沒有接續的提示");
            h.Items(id).Where(i => i.Kind != AgentItemKind.Notice).Should().NotContain(i => i.Status == AgentItemStatus.Running || i.Status == AgentItemStatus.Cancelled);
            RunningTurnStore.TryLoad(h.Store.GetConversationDirectory(id)).Record.Should().BeNull("這一輪結束了");

            // 4. 之後的訊息：同一個程序（不重開、記得前文）
            int turns = after.Of<AgentTurnCompletedEvent>().Count;
            h.Send(h.Main, id, followUp);
            await WaitUntilAsync(() => after.Of<AgentTurnCompletedEvent>().Count > turns && !h.IsRunning(id), "接回之後的一輪", TurnTimeout);
            after.Of<AgentTextCompletedEvent>().Last().Event.Text.Should().Contain(followUpAnswer);
            sessions.Count.Should().Be(2, "沒有另開會話");
            after.Of<AgentProcessStartedEvent>().Should().ContainSingle("接回之後沒有重開程序");
            after.Session.ProcessInstance.Should().Be(instance);
        }
        catch (Exception)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                output.WriteLine($"── 會話 {i} ──");
                foreach (var (at, ev) in sessions[i].Of<AgentEvent>().TakeLast(60))
                    output.WriteLine($"{at:HH:mm:ss.fff} {ev.GetType().Name} {ev}");
            }
            WriteLogTail(output);
            throw;
        }
        finally
        {
            second?.Dispose();
            first.Dispose();
        }
    }

    private async Task RunAsync(AgentProvider provider, string model, string prompt)
    {
        var sessions = new Sessions();
        using var hostRoot = new TempDirectory("it-detach-host");
        AgentHostClient NewHost() => new(Path.Combine(AppContext.BaseDirectory, AgentHostClient.ExecutableName), hostRoot.Root, hostRoot.Combine("host.log"));
        using var h = new HubHarness(sessionFactory: sessions.Create);
        var account = provider switch
        {
            AgentProvider.Claude => h.Claude,
            AgentProvider.Codex => SeedCodexDefault(h),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        account.SafeMode = true;
        CliLocator.Locate(provider).Should().NotBeNull($"這台電腦要安裝 {provider} CLI");
        var first = NewHost();
        AgentHostClient? second = null;
        h.Ui.Invoke(() => h.Hub.AttachProcessHost(first));

        try
        {
            var id = h.NewConversation(h.Main, account);
            SetOptions(h, id, model, "low");
            var workspace = h.Store.GetDefaultWorkingDirectory(id);
            h.Send(h.Main, id, prompt);
            await WaitUntilAsync(() => sessions.Count == 1 && sessions[0].Of<AgentTurnCompletedEvent>().Count > 0 && !h.IsRunning(id), "開背景指令的一輪", TurnTimeout);
            var before = sessions[0];
            before.Of<AgentTurnCompletedEvent>().Last().Event.Outcome.Should().Be(AgentTurnOutcome.Completed);
            before.Session.HasLiveBackgroundWork.Should().BeTrue("這一輪結束時背景指令還在跑（模型沒照指示、等完了它的話這裡會失敗）");
            await WaitUntilAsync(() => before.Session.CanDetachProcess, "到了放得開的靜止點", TimeSpan.FromSeconds(20));
            var instance = before.Session.ProcessInstance;
            h.OnUi(() => h.Hub.BackgroundWorkRunning).Should().BeFalse("放得開的背景工作不擋更新");

            // 1. 排空：不等背景指令、也不停掉它——放開程序
            var startedAt = DateTime.UtcNow;
            var preparation = await h.OnUi(() => h.Hub.PrepareForAppUpdateAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(30));
            output.WriteLine($"排空：{preparation}（{(DateTime.UtcNow - startedAt).TotalSeconds:F1} 秒）");
            preparation.Ready.Should().BeTrue(preparation.Reason);
            before.Session.HasLiveProcess.Should().BeFalse("程序交給主機了");
            before.Of<AgentProcessExitedEvent>().Should().BeEmpty("放開不是結束程序");
            h.OnUi(() => h.Hub.DetachedProcessCount).Should().Be(1);
            var built = h.OnUi(() => h.Hub.BuildAppResume(Token, "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));
            ResumeFileStore.Save(AppPaths.Root, built);
            var file = ResumeFileStore.Load(AppPaths.Root)!;
            file.Attached.Should().ContainSingle(a => a.Id == id, "resume.json 存檔再讀回，接回要用的狀態還在");
            file.Conversations.Should().BeEmpty();
            output.WriteLine($"接回的狀態：{file.Attached[0].State}");

            // 2. 舊的 app 結束（中樞收尾、主機交棒），新版啟動：同一個資料夾建新的中樞、新的主機用戶端、新的會話物件
            await h.RestartAsync();
            first.Handoff(TimeSpan.FromMinutes(3)).Should().BeTrue("主機留著放開的程序等新版");
            second = NewHost();
            var newHost = second;
            h.Ui.Invoke(() =>
            {
                h.Hub.AttachProcessHost(newHost);
                h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeIntentRules.Validate(file, Token, DateTime.UtcNow), "0.2.2", RolledBack: false));
            });
            h.Command(h.Main, "ready");
            await WaitUntilAsync(() => sessions.Count == 2 && sessions[1].Session.HasLiveProcess && sessions[1].Of<AgentProcessStartedEvent>().Count > 0, "新的會話接回程序", TimeSpan.FromSeconds(30));
            var after = sessions[1];
            after.Session.ProcessInstance.Should().Be(instance, "同一個程序");
            after.Session.HasLiveBackgroundWork.Should().BeTrue("背景指令沒有被動到");
            h.IsRunning(id).Should().BeFalse();

            // 3. 背景指令照常跑完，結果回到對話
            if (provider == AgentProvider.Claude)
            {
                await WaitUntilAsync(() => after.Of<AgentTurnCompletedEvent>().Count >= 1 && !h.IsRunning(id), "背景指令跑完，CLI 自己開的一輪做完", TurnTimeout);
                after.Of<AgentIdleTurnStartedEvent>().Should().ContainSingle().Which.Event.AfterBackgroundTask.Should().BeTrue();
            }
            else
            {
                await WaitUntilAsync(() => after.Of<AgentBackgroundCommandFinishedEvent>().Count >= 1, "背景指令跑完", TurnTimeout);
                after.Of<AgentBackgroundCommandFinishedEvent>().Single().Event.ExitCode.Should().Be(0);
                await WaitUntilAsync(() => after.Of<AgentTurnCompletedEvent>().Count >= 1 && !h.IsRunning(id), "中樞請代理接著處理的一輪做完", TurnTimeout);
                File.ReadAllText(Path.Combine(workspace, "bg.txt")).Trim().Should().Be("done", "指令真的跑完了");
                h.Items(id).Where(i => i.Kind == AgentItemKind.Tool && $"{i.Title}\n{i.Detail}".Contains("bg.txt", StringComparison.OrdinalIgnoreCase))
                    .Should().OnlyContain(i => i.Status == AgentItemStatus.Done, "結果寫進原本那張卡，不是「被中斷、結果未知」");
            }
            after.Session.HasLiveBackgroundWork.Should().BeFalse();
            after.Of<AgentTurnCompletedEvent>().Last().Event.Outcome.Should().Be(AgentTurnOutcome.Completed);
            h.Items(id).Should().NotContain(i => i.Kind == AgentItemKind.Notice && i.Text != null && i.Text.Contains("停掉了這個對話還在跑的背景工作"),
                "背景工作沒有中斷：不必請代理確認");

            // 4. 之後的訊息：同一個程序（不重開、記得前文）
            int turns = after.Of<AgentTurnCompletedEvent>().Count;
            h.Send(h.Main, id, provider == AgentProvider.Claude
                ? "剛才那個背景指令最後印出了哪個英文單字？只回覆那個單字"
                : "剛才那個指令寫出的檔案叫什麼名字？只回覆檔名");
            await WaitUntilAsync(() => after.Of<AgentTurnCompletedEvent>().Count > turns && !h.IsRunning(id), "接回之後的一輪", TurnTimeout);
            after.Of<AgentTextCompletedEvent>().Last().Event.Text.Should().Contain(provider == AgentProvider.Claude ? "bgdone" : "bg.txt");
            sessions.Count.Should().Be(2, "沒有另開會話");
            after.Of<AgentProcessStartedEvent>().Should().ContainSingle("接回之後沒有重開程序");
            after.Session.ProcessInstance.Should().Be(instance);
        }
        catch (Exception)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                output.WriteLine($"── 會話 {i} ──");
                foreach (var (at, ev) in sessions[i].Of<AgentEvent>().TakeLast(60))
                    output.WriteLine($"{at:HH:mm:ss.fff} {ev.GetType().Name} {ev}");
            }
            WriteLogTail(output);
            throw;
        }
        finally
        {
            second?.Dispose();
            first.Dispose();
        }
    }

    /// <summary>系統預設的 Codex 帳戶（~/.codex）；RestartAsync 會把它帶到新的中樞</summary>
    private static AgentAccount SeedCodexDefault(HubHarness h)
    {
        var account = new AgentAccount { Id = "acc-codex-default", Provider = AgentProvider.Codex, Label = "Codex 本機" };
        h.Ui.Invoke(() => h.Accounts.Seed(new[] { account }, AccountStatus.Ready));
        return account;
    }
}
