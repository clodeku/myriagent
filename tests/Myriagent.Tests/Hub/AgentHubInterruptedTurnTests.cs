using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// app 結束時還在執行的一輪（running.json，AgentHub.InterruptedTurns.cs）：結束前的訊息都存了、下次啟動自動接續；
/// 按了停止、正常結束、高風險操作、中斷太久、連續接續達上限、app 更新處理的、刪掉的對話不自動接續；
/// 這一輪結束了而背景工作還在跑的（等背景工作回報）也算，重新開啟後請代理確認背景工作、接著做
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubInterruptedTurnTests
{
    private static string MarkerPath(HubHarness h, string id) => RunningTurnStore.PathFor(h.Store.GetConversationDirectory(id));

    private static RunningTurnRecord? Marker(HubHarness h, string id) => RunningTurnStore.TryLoad(h.Store.GetConversationDirectory(id)).Record;

    private static void EditMarker(HubHarness h, string id, Action<RunningTurnRecord> edit)
    {
        var record = Marker(h, id)!;
        edit(record);
        RunningTurnStore.Save(h.Store.GetConversationDirectory(id), record);
    }

    private static List<string> SentTexts(HubHarness h) => h.Sessions.SelectMany(s => s.Sent.All).Select(x => x.Input.Text).ToList();

    private static JToken HeaderRestart(HubHarness h, string id) => h.OnUi(() => h.Hub.RenderHeaderRestartForTest(id));

    /// <summary>新的主視窗送 ready（第一次就緒時接續），等接續都送出</summary>
    private static async Task<(int Continued, int NotContinued)> ReadyAsync(HubHarness h)
    {
        (int, int)? result = null;
        h.Ui.Invoke(() => h.Hub.InterruptedResumeDispatched += r => result = r);
        h.Command(h.Main, "ready");
        await h.WaitUntilAsync(() => result != null, "重新啟動後的接續都已送出");
        return result!.Value;
    }

    [Fact]
    public async Task 執行中結束app_結束前的訊息都存了_重新開啟後自動接續()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        h.Raise(s, new AgentTextDeltaEvent("b1", "先讀取資料，"));
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", "npm test"));
        File.Exists(MarkerPath(h, id)).Should().BeTrue("一輪開始就寫 running.json");

        await h.RestartAsync();

        var marker = Marker(h, id)!;
        marker.ClosedByApp.Should().BeTrue();
        marker.InterruptedTools.Should().ContainSingle().Which.Title.Should().Be("npm test");
        marker.AutoResumes.Should().Be(0);
        // 結束前的訊息都在：使用者的訊息、串流到一半的回覆、執行中的工具
        var items = h.Items(id);
        items.Should().Contain(i => i.Kind == AgentItemKind.User && i.Text == "整理報表");
        items.Should().Contain(i => i.Kind == AgentItemKind.Assistant && i.Text == "先讀取資料，");
        items.Should().Contain(i => i.Kind == AgentItemKind.Tool && i.Title == "npm test");

        var result = await ReadyAsync(h);

        result.Should().Be((1, 0));
        h.IsRunning(id).Should().BeTrue();
        var text = h.Sessions[^1].Sent.All.Should().ContainSingle().Subject.Input.Text;
        text.Should().StartWith("（系統通知：Clode Myriagent 上次結束時中斷了 Claude Code 這一輪的工作");
        text.Should().Contain("重啟時你正在執行〈執行指令：npm test〉");
        var notice = h.Items(id).Last();
        notice.Kind.Should().Be(AgentItemKind.Notice);
        notice.NoticeKind.Should().Be(AgentNoticeKind.AppUpdate);
        notice.Text.Should().Be("Clode Myriagent 上次結束時這個對話還在執行，重新開啟後已請代理從中斷處繼續");
        h.Items(id).Should().NotContain(i => i.Kind == AgentItemKind.User && i.Text == text, "接續訊息不顯示成使用者訊息");
        Marker(h, id)!.AutoResumes.Should().Be(1, "接續的這一輪也有 running.json，記著是第幾次自動接續");
        h.Host.Notifications.All.Should().Contain(n => n.Message == "上次中斷的 1 個對話已自動接續");

        // 只執行一次：網頁重新載入不會再接續
        h.Command(h.Main, "ready");
        await h.Ui.DrainAsync();
        SentTexts(h).Should().HaveCount(2);

        // 接續的一輪正常結束：拿掉 running.json，下次啟動不再接續
        h.CompleteTurn(h.Sessions[^1]);
        File.Exists(MarkerPath(h, id)).Should().BeFalse();
    }

    [Fact]
    public async Task app當掉_用最後的快照接續_說明是意外結束()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "改設定檔");
        h.Raise(s, new AgentTextDeltaEvent("b1", "正在改"));
        h.Ui.Invoke(() => h.Hub.RunMaintenance());   // 30 秒一次的維護：存檔、更新快照
        // 維護的紀錄在背景寫（ItemsFileWriter）：模擬當掉之前等它寫完（真的當掉時，最後這一兩毫秒還沒落盤的會少）
        h.OnUi(() => h.Hub.ItemsWriterForTests)?.Drain(TimeSpan.FromSeconds(10)).Should().BeTrue();

        await h.RestartAsync(crash: true);

        var marker = Marker(h, id)!;
        marker.ClosedByApp.Should().BeFalse();
        h.Items(id).Should().Contain(i => i.Kind == AgentItemKind.Assistant && i.Text == "正在改", "維護存過檔的內容當掉後還在");

        (await ReadyAsync(h)).Should().Be((1, 0));

        var text = SentTexts(h).Last();
        text.Should().StartWith("（系統通知：Clode Myriagent 上次沒有正常結束（當掉、被強制結束或斷電），中斷了 Codex 這一輪的工作");
        text.Should().Contain("最後幾十秒做了什麼沒有記錄");
        h.Items(id).Last().Text.Should().Be("Clode Myriagent 上次沒有正常結束（當掉、被強制結束或斷電），這個對話被中斷，重新開啟後已請代理從中斷處繼續");
    }

    [Fact]
    public void 維護只在快照內容變了才重寫running_json()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        var first = File.ReadAllBytes(MarkerPath(h, id));

        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        File.ReadAllBytes(MarkerPath(h, id)).Should().Equal(first, "內容沒變、剛寫過：不重寫");

        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", "npm test"));
        File.ReadAllBytes(MarkerPath(h, id)).Should().Equal(first, "一般的工具不立刻寫，等維護");
        h.Ui.Invoke(() => h.Hub.RunMaintenance());

        Marker(h, id)!.InterruptedTools.Should().ContainSingle().Which.Title.Should().Be("npm test");
    }

    [Fact]
    public async Task 當掉時使用者送出的訊息在_沒有維護也照樣接續()
    {
        using var h = new HubHarness();
        var (id, _) = h.StartConversation(h.Main, h.Claude, "第一則就當掉");

        await h.RestartAsync(crash: true);

        h.Items(id).Should().ContainSingle(i => i.Kind == AgentItemKind.User).Which.Text.Should().Be("第一則就當掉");
        (await ReadyAsync(h)).Should().Be((1, 0));
        h.IsRunning(id).Should().BeTrue();
    }

    [Fact]
    public async Task 一輪已經結束_或按了停止還沒結束就關app_都不接續()
    {
        using var h = new HubHarness();
        var (doneId, done) = h.StartConversation(h.Main, h.Claude, "做完了");
        h.CompleteTurn(done);
        var (stopId, _) = h.StartConversation(h.Main, h.Claude, "按停止");
        h.Command(h.Main, "stop", new { conversationId = stopId });
        h.IsRunning(stopId).Should().BeTrue("CLI 還沒回報這一輪結束");
        File.Exists(MarkerPath(h, doneId)).Should().BeFalse();
        File.Exists(MarkerPath(h, stopId)).Should().BeFalse("按了停止就拿掉 running.json");
        var (runId, _) = h.StartConversation(h.Main, h.Claude, "還在跑");
        var before = SentTexts(h).Count;

        await h.RestartAsync();
        File.Exists(MarkerPath(h, stopId)).Should().BeFalse("結束時按了停止的不寫快照");

        (await ReadyAsync(h)).Should().Be((1, 0));
        SentTexts(h).Should().HaveCount(before + 1);
        h.IsRunning(runId).Should().BeTrue();
        h.IsRunning(doneId).Should().BeFalse();
        h.IsRunning(stopId).Should().BeFalse();
    }

    [Fact]
    public async Task 中斷超過1小時_不自動接續_標頭給繼續_按了才送()
    {
        using var h = new HubHarness();
        var (id, _) = h.StartConversation(h.Main, h.Claude, "很久以前");
        await h.RestartAsync(crash: true);
        var old = new DateTimeOffset(DateTime.Now - TimeSpan.FromHours(2)).ToUnixTimeMilliseconds();
        EditMarker(h, id, r =>
        {
            r.StartedAt = old;
            r.SeenAt = old;
        });
        await h.RestartAsync(crash: true);   // 新的中樞讀改過的 running.json
        var before = SentTexts(h).Count;

        (await ReadyAsync(h)).Should().Be((0, 1));

        SentTexts(h).Should().HaveCount(before);
        h.IsRunning(id).Should().BeFalse();
        File.Exists(MarkerPath(h, id)).Should().BeFalse("決定不接續就拿掉，下次啟動不再處理");
        var notice = h.Items(id).Last();
        notice.NoticeKind.Should().Be(AgentNoticeKind.AppUpdate);
        notice.Text.Should().Contain("沒有自動接續：中斷已超過 1 小時");
        var header = (JObject)HeaderRestart(h, id);
        header.Value<string>("state").Should().Be("notContinued");
        header.Value<bool>("canContinue").Should().BeTrue();
        h.Host.Notifications.All.Should().Contain(n => n.Message.Contains("1 個上次中斷的對話沒有自動接續"));

        h.Command(h.Main, "cliContinue", new { conversationId = id });

        var text = SentTexts(h).Last();
        text.Should().StartWith("（系統通知：Clode Myriagent 上次沒有正常結束");
        text.Should().Contain("使用者看過狀況後按了「繼續」");
        h.Items(id).Last().Text.Should().Be(InterruptedTurnRules.ManualContinueNotice);
        h.Items(id).Last().NoticeKind.Should().Be(AgentNoticeKind.AppUpdate);
        h.IsRunning(id).Should().BeTrue();
    }

    [Fact]
    public async Task 開始高風險操作立刻記下_這時當掉不自動接續_操作結束後再當掉照常接續()
    {
        using var h = new HubHarness();
        var (riskyId, risky) = h.StartConversation(h.Main, h.Claude, "推上去");
        h.Raise(risky, new AgentToolStartedEvent("t1", "執行指令", "bash", "git push origin main", "git push origin main"));
        Marker(h, riskyId)!.RiskOperation.Should().Contain("git push", "不等維護就寫進快照");
        var (safeId, safe) = h.StartConversation(h.Main, h.Claude, "推完了");
        h.Raise(safe, new AgentToolStartedEvent("t2", "執行指令", "bash", "git push origin main", "git push origin main"));
        h.Raise(safe, new AgentToolCompletedEvent("t2", AgentItemStatus.Done, "ok", Array.Empty<string>()));
        Marker(h, safeId)!.RiskOperation.Should().BeNull("操作結束就重寫快照");
        var before = SentTexts(h).Count;

        await h.RestartAsync(crash: true);
        (await ReadyAsync(h)).Should().Be((1, 1));

        SentTexts(h).Should().HaveCount(before + 1);
        h.IsRunning(safeId).Should().BeTrue();
        h.IsRunning(riskyId).Should().BeFalse();
        h.Items(riskyId).Last().Text.Should().Contain("git push origin main").And.Contain("結果未知");
        ((JObject)HeaderRestart(h, riskyId)).Value<bool>("canContinue").Should().BeTrue();
    }

    [Fact]
    public async Task 連續自動接續達上限_不再自動接續()
    {
        using var h = new HubHarness();
        var (id, _) = h.StartConversation(h.Main, h.Claude, "一直當掉");
        for (int i = 1; i <= InterruptedTurnRules.MaxAutoResumes; i++)
        {
            await h.RestartAsync(crash: true);
            (await ReadyAsync(h)).Should().Be((1, 0), $"第 {i} 次重新啟動照常接續");
            Marker(h, id)!.AutoResumes.Should().Be(i);
        }

        await h.RestartAsync(crash: true);
        (await ReadyAsync(h)).Should().Be((0, 1));

        h.IsRunning(id).Should().BeFalse();
        h.Items(id).Last().Text.Should().Contain($"已連續自動接續 {InterruptedTurnRules.MaxAutoResumes} 次都在完成前被中斷");
    }

    [Fact]
    public async Task 使用者自己送出的一輪_接續次數從零算起()
    {
        using var h = new HubHarness();
        var (id, _) = h.StartConversation(h.Main, h.Claude, "第一則");
        await h.RestartAsync();
        await ReadyAsync(h);
        Marker(h, id)!.AutoResumes.Should().Be(1);
        h.CompleteTurn(h.Sessions[^1]);

        h.Send(h.Main, id, "第二則");

        Marker(h, id)!.AutoResumes.Should().Be(0);
    }

    [Fact]
    public async Task 重新開啟後_就緒前使用者已經送出_不再接續上次的()
    {
        using var h = new HubHarness();
        var (id, _) = h.StartConversation(h.Main, h.Claude, "第一則");
        await h.RestartAsync();

        h.Send(h.Main, id, "我自己接著說");
        h.Command(h.Main, "ready");
        await h.Ui.DrainAsync();

        SentTexts(h).Last().Should().Be("我自己接著說");
        SentTexts(h).Should().NotContain(t => t.StartsWith("（系統通知"));
        h.Items(id).Should().NotContain(i => i.Kind == AgentItemKind.Notice && i.NoticeKind == AgentNoticeKind.AppUpdate);
        Marker(h, id)!.AutoResumes.Should().Be(0, "running.json 是使用者這一輪的");
    }

    [Fact]
    public async Task resume列出的對話由app更新處理_這裡只拿掉標記不重複接續()
    {
        using var h = new HubHarness();
        var (id, _) = h.StartConversation(h.Main, h.Claude, "排空停下的");
        await h.RestartAsync(crash: true);
        var before = SentTexts(h).Count;
        var file = new AppResumeFile
        {
            Token = "tok-old",
            From = "0.2.1-aaaaaa1",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Conversations = { new ResumeConversation { Id = id, Provider = AgentProvider.Claude, AccountId = h.Claude.Id, Mode = ResumeMode.Continue } },
        };
        h.Ui.Invoke(() => h.Hub.ScheduleAppResume(new PendingAppResume(file, ResumeIntentRules.Validate(file, token: null, DateTime.UtcNow), "0.2.1", RolledBack: false)));
        h.Command(h.Main, "ready");
        await h.Ui.DrainAsync();

        SentTexts(h).Should().HaveCount(before, "token 不符：app 更新那邊給「繼續」，這裡也不自動接續");
        h.Items(id).Count(i => i.Kind == AgentItemKind.Notice).Should().Be(1, "只有 app 更新那一則提示");
        File.Exists(MarkerPath(h, id)).Should().BeFalse();
    }

    [Fact]
    public void 刪掉執行中的對話_拿掉running_json()
    {
        using var h = new HubHarness();
        var (id, _) = h.StartConversation(h.Main, h.Claude, "要刪的");
        File.Exists(MarkerPath(h, id)).Should().BeTrue();

        h.Command(h.Main, "delete", new { conversationId = id });

        File.Exists(MarkerPath(h, id)).Should().BeFalse();
    }

    // ───────────────────────────── 等背景工作回報的對話 ─────────────────────────────

    private static readonly AgentBackgroundTool DevServer = new("執行指令", "bash", "npm run dev\n-- --port 5173", null);

    /// <summary>會話回報背景工作（同真的會話：HasLiveBackgroundWork、LiveBackgroundTools 跟著清單）</summary>
    private static void Background(HubHarness h, FakeSession s, params AgentBackgroundTool[] tools)
    {
        s.HasLiveBackgroundWork = tools.Length > 0;
        s.LiveBackgroundTools = tools;
        h.Raise(s, new AgentBackgroundWorkEvent(tools.Length, tools));
    }

    [Fact]
    public async Task 這一輪結束時背景工作還在跑_結束app後重新開啟_請代理確認背景工作並接著做()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "開開發伺服器");
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm run dev", "npm run dev"));
        h.Raise(s, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, "背景執行中", Array.Empty<string>()));
        Background(h, s, DevServer);
        h.CompleteTurn(s);

        h.IsRunning(id).Should().BeFalse();
        var waiting = Marker(h, id)!;
        waiting.WaitingBackground.Should().BeTrue("代理回覆完了，還在等背景工作：不刪 running.json");
        waiting.BackgroundWork.Should().BeTrue();
        waiting.BackgroundTasks.Should().Equal("執行指令：npm run dev");
        waiting.InterruptedTools.Should().BeEmpty("這一輪的工具都做完了");
        waiting.ClosedByApp.Should().BeFalse();

        await h.RestartAsync();

        var marker = Marker(h, id)!;
        marker.ClosedByApp.Should().BeTrue();
        marker.WaitingBackground.Should().BeTrue();

        (await ReadyAsync(h)).Should().Be((1, 0));

        h.IsRunning(id).Should().BeTrue();
        var text = h.Sessions[^1].Sent.All.Should().ContainSingle().Subject.Input.Text;
        text.Should().StartWith("（系統通知：Clode Myriagent 上次結束時停掉了 Claude Code 還在跑的背景工作");
        text.Should().Contain("上一輪你已經回覆完，正在等背景工作〈執行指令：npm run dev〉回報");
        text.Should().Contain("不會再有完成通知").And.Contain("重新以背景執行");
        text.Should().NotContain("正在產生回覆", "這一輪早就回覆完，不是被打斷在回覆途中");
        h.Items(id).Last().Text.Should().Be("Clode Myriagent 上次結束時停掉了這個對話還在跑的背景工作，重新開啟後已請代理確認背景工作做到哪裡、接著做");
        var resumed = Marker(h, id)!;
        resumed.WaitingBackground.Should().BeFalse("接續的一輪在執行");
        resumed.AutoResumes.Should().Be(1);
    }

    [Fact]
    public async Task 等背景工作時當掉_照最後的快照接續_說明是意外結束()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        Background(h, s, new AgentBackgroundTool("執行指令", "bash", "dotnet test", null));
        h.CompleteTurn(s);

        await h.RestartAsync(crash: true);

        Marker(h, id)!.ClosedByApp.Should().BeFalse();
        (await ReadyAsync(h)).Should().Be((1, 0));
        var text = SentTexts(h).Last();
        text.Should().StartWith("（系統通知：Clode Myriagent 上次沒有正常結束（當掉、被強制結束或斷電），Claude Code 還在跑的背景工作跟著停了");
        text.Should().Contain("〈執行指令：dotnet test〉");
        h.Items(id).Last().Text.Should().Be("Clode Myriagent 上次沒有正常結束（當掉、被強制結束或斷電），這個對話還在跑的背景工作跟著停了，重新開啟後已請代理確認背景工作做到哪裡、接著做");
    }

    [Fact]
    public void 背景工作自己跑完_拿掉running_json_CLI自己開的一輪照常寫()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "跑測試");
        var tests = new AgentBackgroundTool("執行指令", "bash", "npm test", null);
        Background(h, s, tests, DevServer);
        h.CompleteTurn(s);
        Marker(h, id)!.BackgroundTasks.Should().Equal("執行指令：npm test", "執行指令：npm run dev");

        Background(h, s, DevServer);
        Marker(h, id)!.BackgroundTasks.Should().Equal(new[] { "執行指令：npm run dev" }, "清單變了就更新快照");

        Background(h, s);
        File.Exists(MarkerPath(h, id)).Should().BeFalse("背景工作都跑完了：CLI 會自己開一輪回報，不必等重新開啟");

        h.Raise(s, new AgentIdleTurnStartedEvent(AfterBackgroundTask: true));
        var turn = Marker(h, id)!;
        turn.WaitingBackground.Should().BeFalse();
        turn.AutoResumes.Should().Be(0);
        h.CompleteTurn(s);
        File.Exists(MarkerPath(h, id)).Should().BeFalse();
    }

    [Fact]
    public void 停止或失敗結束_或換了會話_不留等背景工作的標記()
    {
        using var h = new HubHarness();
        var (stopId, stopped) = h.StartConversation(h.Main, h.Claude, "按停止");
        Background(h, stopped, DevServer);
        h.Command(h.Main, "stop", new { conversationId = stopId });
        h.CompleteTurn(stopped, AgentTurnOutcome.Interrupted);
        File.Exists(MarkerPath(h, stopId)).Should().BeFalse("按了停止：背景工作還在跑也不在重新開啟後接續");

        var (failId, failed) = h.StartConversation(h.Main, h.Claude, "失敗");
        Background(h, failed, DevServer);
        h.CompleteTurn(failed, AgentTurnOutcome.Failed, "API 錯誤");
        File.Exists(MarkerPath(h, failId)).Should().BeFalse();

        var (switchId, switched) = h.StartConversation(h.Main, h.Claude, "換帳戶");
        Background(h, switched, DevServer);
        h.CompleteTurn(switched);
        File.Exists(MarkerPath(h, switchId)).Should().BeTrue();
        // 背景工作還在跑時要確認現在換（2026-10-27；沒確認的等背景工作結束才換）：新帳戶隱藏送出接續訊息，那一輪結束後不留標記
        h.Command(h.Main, "setAccount", new { conversationId = switchId, accountId = h.Claude2.Id, when = AccountSwitchRules.WhenNow });
        h.CompleteTurn(h.Sessions[^1]);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        File.Exists(MarkerPath(h, switchId)).Should().BeFalse("換了會話：舊會話的背景工作是使用者自己換帳戶停掉的");
    }

    [Fact]
    public async Task 背景跑的是高風險操作_重新開啟後不自動接續_給繼續()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "推上去");
        Background(h, s, new AgentBackgroundTool("執行指令", "bash", "git push origin main", null));
        h.CompleteTurn(s);
        Marker(h, id)!.RiskOperation.Should().Contain("git push");
        var before = SentTexts(h).Count;

        await h.RestartAsync();
        (await ReadyAsync(h)).Should().Be((0, 1));

        SentTexts(h).Should().HaveCount(before);
        h.Items(id).Last().Text.Should().StartWith("Clode Myriagent 上次結束時停掉了這個對話還在跑的背景工作，這個對話沒有自動接續")
            .And.Contain("git push origin main");
        ((JObject)HeaderRestart(h, id)).Value<bool>("canContinue").Should().BeTrue();

        h.Command(h.Main, "cliContinue", new { conversationId = id });
        SentTexts(h).Last().Should().Contain("正在等背景工作〈執行指令：git push origin main〉回報").And.Contain("使用者看過狀況後按了「繼續」");
    }

    /// <summary>app 更新排空用的 CLI 身分（同 AgentHubAppUpdateTests）：排空要知道對話跑的是哪一版</summary>
    private static async Task<(string Id, FakeSession Session)> WaitingBackgroundForAppUpdateAsync(HubHarness h)
    {
        var cli = CliIdentities.Claude("2.1.295", fileId: 1);
        await h.SwitchCliAsync(AgentProvider.Claude, null, cli);
        await h.SwitchCliAsync(AgentProvider.Codex, null, CliIdentities.Codex("0.160.1", fileId: 1));
        h.Ui.Invoke(() => h.Hub.BackgroundSettle = TimeSpan.Zero);
        var (id, s) = h.StartConversation(h.Main, h.Claude, "開開發伺服器");
        h.ProcessStarted(s, "a1", cli);
        s.SucceedRestarts("a");
        Background(h, s, DevServer);
        h.Raise(s, new AgentSafePointChangedEvent(false, false, HasLiveBackgroundWork: true));
        h.CompleteTurn(s);
        s.IsRunning = false;
        return (id, s);
    }

    /// <summary>等背景工作滿上限（30 分鐘，CliRestartRules.BackgroundWaitLimit）照立即套用停下；停下的程序帶走背景工作（會話回報 0）</summary>
    private static async Task<Task<AppUpdatePreparation>> DrainPastGraceAsync(HubHarness h, FakeSession s)
    {
        var start = DateTime.Now;
        var now = start;
        h.Ui.Invoke(() => h.Hub.RestartClock = () => now);
        var prepare = h.OnUi(() => h.Hub.PrepareForAppUpdateAsync(CancellationToken.None));
        await h.Ui.DrainAsync();
        now = start.AddMinutes(29);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.Ui.DrainAsync();
        s.Restarts.Count.Should().Be(0, "等背景工作：還沒滿 30 分鐘不停");
        now = start.AddMinutes(30);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.Ui.DrainAsync();
        s.Restarts.All.Should().ContainSingle().Which.Mode.Should().Be(AgentRestartMode.Immediate);
        Background(h, s);
        return prepare;
    }

    [Fact]
    public async Task app更新排空逾時停掉背景工作_留下app停掉的快照_新版照它接續()
    {
        using var h = new HubHarness();
        var (id, s) = await WaitingBackgroundForAppUpdateAsync(h);

        var prepare = await DrainPastGraceAsync(h, s);
        (await prepare.WaitAsync(TimeSpan.FromSeconds(5))).Ready.Should().BeTrue();

        var marker = Marker(h, id)!;
        marker.WaitingBackground.Should().BeTrue("背景工作跟著程序沒了也不刪：等背景工作的對話沒有停下待接續的一輪，不進 resume.json");
        marker.ClosedByApp.Should().BeTrue();
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        File.Exists(MarkerPath(h, id)).Should().BeTrue("維護也不刪 app 停掉的");

        await h.RestartAsync();
        (await ReadyAsync(h)).Should().Be((1, 0));
        SentTexts(h).Last().Should().StartWith("（系統通知：Clode Myriagent 上次結束時停掉了 Claude Code 還在跑的背景工作")
            .And.Contain("〈執行指令：npm run dev〉");
    }

    [Fact]
    public async Task 放棄app更新_排空停掉的背景工作就地接續()
    {
        using var h = new HubHarness();
        var (id, s) = await WaitingBackgroundForAppUpdateAsync(h);
        var prepare = await DrainPastGraceAsync(h, s);
        await prepare.WaitAsync(TimeSpan.FromSeconds(5));
        var before = SentTexts(h).Count;

        h.Ui.Invoke(() => h.Hub.AbortAppUpdate("使用者取消"));
        await h.Ui.DrainAsync();

        SentTexts(h).Should().HaveCount(before + 1);
        var text = SentTexts(h).Last();
        text.Should().StartWith("（系統通知：Clode Myriagent 為了套用自身的更新而停下了 Claude Code，現在重新啟動了它");
        text.Should().Contain("正在等背景工作〈執行指令：npm run dev〉回報");
        h.Items(id).Last().Text.Should().Be("Clode Myriagent 為了套用更新停掉了這個對話還在跑的背景工作（這次更新已放棄），已請代理確認背景工作做到哪裡、接著做");
        h.IsRunning(id).Should().BeTrue();
        Marker(h, id)!.WaitingBackground.Should().BeFalse("接續的一輪在執行");
    }

    [Fact]
    public void Windows詢問能不能關機_寫執行中的快照_確定結束時寫正常結束的快照()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "關機前");
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "dotnet build", "dotnet build"));

        h.Ui.Invoke(() => h.Hub.SaveRunningConversations());
        var asked = Marker(h, id)!;
        asked.ClosedByApp.Should().BeFalse("使用者還可能取消關機");
        asked.InterruptedTools.Should().ContainSingle().Which.Title.Should().Be("dotnet build");

        h.Ui.Invoke(() => h.Hub.Dispose());   // WM_ENDSESSION：沒經過 ShutdownAsync 就結束

        Marker(h, id)!.ClosedByApp.Should().BeTrue();
    }
}
