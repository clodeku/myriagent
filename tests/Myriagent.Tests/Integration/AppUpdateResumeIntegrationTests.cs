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
/// app 自我更新的跨程序接續（設計 9.5、11.2），以這台電腦真的 Claude Code／Codex CLI（系統預設帳戶）驗證：
/// 工具執行中開始排空 → 工具結束後的安全點停下、程序結束 → resume.json 存檔再讀回 → 同一個資料夾建新的中樞與新的會話物件（模擬新版啟動）→
/// 主視窗 ready 後以隱藏送出接續同一個 CLI 對話（不靠 CLI 的 RESUME 環境變數）→ 那一輪做完、寫檔不重做、提示是 AppUpdate、使用者看不到接續訊息。
/// Claude 另外確認下一則訊息記得排空前的前文，以及等核准時排空（沒開 Remote Control 的對話）：卡片作廢、接續後 CLI 重新提出、核准後才寫檔。
/// 需要登入的部分用系統預設帳戶（使用者既有的 ~/.claude、~/.codex；同 IT-H1～H3，不改設定、只多一筆對話紀錄）。
/// 帳戶開精簡模式（同會話層的整合測試 UseLocalCustomizations: false）：使用者的外掛、hooks、CLAUDE.md、MCP 與使用者層的權限放行規則
/// 不會多出工具呼叫、也不會替核准測試先做決定；精簡模式不影響訂閱登入與 --resume／thread/resume。
/// 會用到訂閱額度：Claude haiku 約 5 輪、Codex gpt-6-luna 約 2 輪，推理強度 low。
/// 執行：MYRIAGENT_IT=1 dotnet test ... --filter "FullyQualifiedName~Integration"
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AppUpdateResumeIntegrationTests
{
    private const string Reason = "以本機 Claude Code／Codex 跑真的對話，排空後換一個中樞接續（會用到訂閱額度）";
    private const string QuickGuardedWrite = "if (Test-Path done.txt) { 'exists' } else { Set-Content done.txt done; 'written' }";
    private const string Token = "tok-it";
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(4);

    private readonly ITestOutputHelper output;

    public AppUpdateResumeIntegrationTests(ITestOutputHelper output) => this.output = output;

    [EnvFact("MYRIAGENT_IT", Reason)]
    public Task Claude_排空停在工具之後_新中樞隱藏送出接續_寫檔不重做_記得前文()
        => RunAsync(AgentProvider.Claude, "haiku", rememberFirst: true);

    [EnvFact("MYRIAGENT_IT", Reason)]
    public Task Codex_排空停在工具之後_新中樞隱藏送出接續_寫檔不重做()
        => RunAsync(AgentProvider.Codex, "gpt-6-luna", rememberFirst: false);

    /// <summary>
    /// 等核准也是 app 更新的安全點（設計 9.2）：停下時卡片作廢，新的中樞接續後 CLI 重新提出，核准之後才寫檔。
    /// 只限沒開 Remote Control 的對話：開著的等回應不算安全點（手機上可能正要回應），排空滿 10 分鐘才照立即套用停下
    /// （設計第 16 節第 25 項，由 AgentHubAppUpdateTests 涵蓋），所以這裡先關掉預設開著的 Remote Control
    /// </summary>
    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_等核准時排空_新中樞接續後重新提出核准_核准後寫檔一次()
    {
        var sessions = new Sessions();
        using var h = new HubHarness(sessionFactory: sessions.Create);
        CliLocator.Locate(AgentProvider.Claude).Should().NotBeNull("這台電腦要安裝 Claude Code CLI");
        h.Claude.SafeMode = true;   // 使用者層的權限放行規則與 hooks 不能先替這張核准卡做決定
        h.Command(h.Main, "setting", new { key = "claudeRemoteControl", value = false });   // RestartAsync 沿用同一個設定物件：新的中樞也是關的
        var id = h.NewConversation(h.Main, h.Claude);
        SetOptions(h, id, "haiku", "low");
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = nameof(AgentPermissionMode.Ask) });
        var workspace = h.Store.GetDefaultWorkingDirectory(id);
        h.Send(h.Main, id, $"請用 PowerShell 工具執行一次 `{QuickGuardedWrite}`，執行完用一句話回報輸出。");
        await WaitUntilAsync(() => PendingApproval(h, id) != null, "要求核准", TurnTimeout);
        var oldCard = PendingApproval(h, id)!;

        var preparation = await h.OnUi(() => h.Hub.PrepareForAppUpdateAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(60));
        output.WriteLine($"排空：{preparation}");
        preparation.Ready.Should().BeTrue(preparation.Reason);
        File.Exists(Path.Combine(workspace, "done.txt")).Should().BeFalse("沒有核准就沒有執行");
        var file = SaveAndLoadResume(h);
        file.Conversations.Should().ContainSingle(c => c.Id == id).Which.Mode.Should().Be(ResumeMode.Continue);

        await h.RestartAsync();
        (await ResumeAsync(h, file)).Continued.Should().Be(1);
        try
        {
            await WaitUntilAsync(() => PendingApproval(h, id) is { } card && card.Id != oldCard.Id, "接續後重新要求核准", TurnTimeout);
            h.Items(id).Single(i => i.Id == oldCard.Id).Status.Should().NotBe(AgentItemStatus.Waiting, "停下前的卡片已作廢，不能再按");
            h.Command(h.Main, "approve", new { conversationId = id, itemId = PendingApproval(h, id)!.Id, choice = "allow" });
            await WaitUntilAsync(() => sessions.Count == 2 && sessions[1].Of<AgentTurnCompletedEvent>().Count > 0 && !h.IsRunning(id), "核准後這一輪做完", TurnTimeout);
            File.ReadAllText(Path.Combine(workspace, "done.txt")).Trim().Should().Be("done");
        }
        catch (Exception)
        {
            Dump(h, id, sessions[0], sessions.Count > 1 ? sessions[1] : null);
            throw;
        }
    }

    private async Task RunAsync(AgentProvider provider, string model, bool rememberFirst)
    {
        var sessions = new Sessions();
        using var h = new HubHarness(sessionFactory: sessions.Create);
        var account = provider switch
        {
            AgentProvider.Claude => h.Claude,   // ConfigDirectory＝null：系統預設帳戶（~/.claude）
            AgentProvider.Codex => SeedCodexDefault(h),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        account.SafeMode = true;   // RestartAsync 帶過去的是同一個帳戶物件，新的中樞也是精簡模式
        CliLocator.Locate(provider).Should().NotBeNull($"這台電腦要安裝 {provider} CLI");

        var id = h.NewConversation(h.Main, account);
        SetOptions(h, id, model, "low");
        var workspace = h.Store.GetDefaultWorkingDirectory(id);
        if (rememberFirst)
        {
            h.Send(h.Main, id, "記住代號：藍鯨42。只回答「好」。");
            await WaitUntilAsync(() => sessions.Count == 1 && !h.IsRunning(id), "記代號的一輪", TurnTimeout);
        }
        h.Send(h.Main, id, $"請用 PowerShell 工具執行一次 `{GuardedWrite}`，執行完用三句話說明這個指令做了什麼，最後一行只寫 DONE。");
        // 只認寫檔的那個工具：記代號的一輪若用了別的工具，不能讓「開始執行工具」「工具結束才停」提早成立
        await WaitUntilAsync(() => sessions.Count == 1 && sessions[0].Of<AgentToolStartedEvent>().Any(e => IsGuardedWrite(e.Event)), "開始執行寫檔的工具", TurnTimeout);
        var before = sessions[0];
        var guarded = before.Of<AgentToolStartedEvent>().First(e => IsGuardedWrite(e.Event)).Event;

        // 1. 排空：工具執行中不打斷，結束後的安全點停下、程序結束（app 更新不在這個程序接續）
        var preparation = await h.OnUi(() => h.Hub.PrepareForAppUpdateAsync(CancellationToken.None)).WaitAsync(TurnTimeout);
        output.WriteLine($"排空：{preparation}");
        preparation.Ready.Should().BeTrue(preparation.Reason);
        before.Of<AgentToolCompletedEvent>().Should().Contain(e => e.Event.ToolId == guarded.ToolId, "工具執行中不打斷，等它結束才停");
        before.Session.HasLiveProcess.Should().BeFalse("停下的程序已經結束");
        var file = SaveAndLoadResume(h);
        file.Conversations.Should().ContainSingle(c => c.Id == id).Which.Mode.Should().Be(ResumeMode.Continue);

        // 2. 新版啟動：同一個資料夾建新的中樞、新的會話物件；主視窗 ready 後接續
        await h.RestartAsync();
        (await ResumeAsync(h, file)).Continued.Should().Be(1);
        var resumedAt = DateTime.UtcNow;   // ResumeAsync 每 100 毫秒輪詢一次，誤差在 0.1 秒內

        try
        {
            await WaitUntilAsync(() => sessions.Count == 2 && sessions[1].Of<AgentTurnCompletedEvent>().Count > 0 && !h.IsRunning(id), "接續的一輪做完", TurnTimeout);
            var after = sessions[1];
            output.WriteLine($"接續送出 → 那一輪做完：{(after.Of<AgentTurnCompletedEvent>()[0].At - resumedAt).TotalSeconds:F0} 秒；接續後工具 {after.Of<AgentToolCompletedEvent>().Count} 次（exists {after.Of<AgentToolCompletedEvent>().Count(e => e.Event.Output?.Contains("exists") == true)}、written {after.Of<AgentToolCompletedEvent>().Count(e => e.Event.Output?.Contains("written") == true)}）");
            after.Of<AgentTurnCompletedEvent>().Last().Event.Outcome.Should().Be(AgentTurnOutcome.Completed);
            File.ReadAllText(Path.Combine(workspace, "done.txt")).Trim().Should().Be("done");
            before.Of<AgentToolCompletedEvent>().Concat(after.Of<AgentToolCompletedEvent>()).Count(e => e.Event.Output?.Contains("written") == true)
                .Should().Be(1, "先檢查再寫：接續後即使重跑也只會回報 exists");
            var items = h.Items(id);
            items.Should().Contain(i => i.Kind == AgentItemKind.Notice && i.NoticeKind == AgentNoticeKind.AppUpdate
                && i.Text == "Clode Myriagent 已更新為 0.2.2，已請代理從中斷處繼續");
            items.Where(i => i.Kind == AgentItemKind.User).Should().NotContain(i => i.Text != null && i.Text.StartsWith("（系統通知：Clode Myriagent"),
                "接續訊息是隱藏送出，使用者看不到");

            if (rememberFirst)
            {
                h.Send(h.Main, id, "剛才請你記住的代號是什麼？只回答代號。");
                await WaitUntilAsync(() => after.Of<AgentTurnCompletedEvent>().Count >= 2 && !h.IsRunning(id), "問代號的一輪", TurnTimeout);
                after.Of<AgentTextCompletedEvent>().Last().Event.Text.Should().Contain("藍鯨42", "排空前後是同一個 CLI 對話");
            }
        }
        catch (Exception)
        {
            Dump(h, id, before, sessions.Count > 1 ? sessions[1] : null);
            throw;
        }
    }

    /// <summary>寫檔的那個工具（Claude 的 Title 是指令第一行、Detail 含說明與指令；Codex 的 Detail 是完整指令）</summary>
    private static bool IsGuardedWrite(AgentToolStartedEvent e)
        => $"{e.Title}\n{e.Detail}".Contains("done.txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>交棒前寫 resume.json、新版啟動時讀回（存讀往返也一起驗）</summary>
    private static AppResumeFile SaveAndLoadResume(HubHarness h)
    {
        var built = h.OnUi(() => h.Hub.BuildAppResume(Token, "0.2.1-aaaaaa1", "0.2.2-bbbbbb2"));
        ResumeFileStore.Save(AppPaths.Root, built);
        var file = ResumeFileStore.Load(AppPaths.Root);
        file.Should().NotBeNull("resume.json 存檔後讀得回來");
        return file!;
    }

    /// <summary>新的中樞：排定接續、主視窗 ready，等接續都送出</summary>
    private async Task<AppResumeSummary> ResumeAsync(HubHarness h, AppResumeFile file)
    {
        AppResumeSummary? summary = null;
        h.Ui.Invoke(() =>
        {
            h.Hub.AppResumeDispatched += s => summary = s;
            h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeIntentRules.Validate(file, Token, DateTime.UtcNow), "0.2.2", RolledBack: false));
        });
        h.Command(h.Main, "ready");
        await WaitUntilAsync(() => summary != null, "接續都已送出", TimeSpan.FromSeconds(30));
        output.WriteLine($"接續：{summary}");
        return summary!;
    }

    private static AgentChatItem? PendingApproval(HubHarness h, string conversationId)
        => h.Items(conversationId).LastOrDefault(i => i.Kind == AgentItemKind.Approval && i.Status == AgentItemStatus.Waiting);

    /// <summary>系統預設的 Codex 帳戶（~/.codex）；RestartAsync 會把它帶到新的中樞</summary>
    private static AgentAccount SeedCodexDefault(HubHarness h)
    {
        var account = new AgentAccount { Id = "acc-codex-default", Provider = AgentProvider.Codex, Label = "Codex 本機" };
        h.Ui.Invoke(() => h.Accounts.Seed(new[] { account }, AccountStatus.Ready));
        return account;
    }

    /// <summary>失敗時的診斷：對話的項目、排空前後兩個會話的事件、app 的記錄檔（只在測試的暫存資料夾）</summary>
    private void Dump(HubHarness h, string conversationId, Recorded before, Recorded? after)
    {
        foreach (var item in h.Items(conversationId))
            output.WriteLine($"[item] {item.Kind} {item.Status} {item.NoticeKind} {item.ToolName} {ClaudeStreamRules.FirstLine(item.Text ?? item.Output ?? string.Empty)}");
        foreach (var (label, recorded) in new[] { ("排空前", before), ("接續後", after) })
        {
            if (recorded == null)
                continue;
            foreach (var (at, ev) in recorded.Of<AgentEvent>())
                output.WriteLine($"[{label}] {at:HH:mm:ss.fff} {ev.GetType().Name}");
        }
        WriteLogTail(output);
    }
}
