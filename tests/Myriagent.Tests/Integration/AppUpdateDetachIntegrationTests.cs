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
/// 帳戶與精簡模式同 <see cref="AppUpdateResumeIntegrationTests"/>。會用到訂閱額度：各約 3 輪（haiku／gpt-6-luna、low）。
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
