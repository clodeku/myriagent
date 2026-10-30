using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>app 結束時還在執行的一輪：下次啟動要不要自動接續、提示文字、接續訊息（InterruptedTurnRules、ContinuationRules 的 AppRestart）與 running.json 的讀寫</summary>
public sealed class InterruptedTurnRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static RunningTurnRecord Record(TimeSpan ago, bool closedByApp = true, string? risk = null, int autoResumes = 0)
    {
        var seen = new DateTimeOffset(Now - ago).ToUnixTimeMilliseconds();
        return new RunningTurnRecord
        {
            StartedAt = seen - 60_000,
            SeenAt = seen,
            ClosedByApp = closedByApp,
            RiskOperation = risk,
            AutoResumes = autoResumes,
        };
    }

    [Fact]
    public void 剛中斷的_可以自動接續()
        => InterruptedTurnRules.WhyNotContinue(Record(TimeSpan.FromMinutes(5)), Now).Should().BeNull();

    [Fact]
    public void 中斷超過1小時_不自動接續()
    {
        InterruptedTurnRules.WhyNotContinue(Record(TimeSpan.FromMinutes(59)), Now).Should().BeNull();
        InterruptedTurnRules.WhyNotContinue(Record(TimeSpan.FromMinutes(61)), Now).Should().Be(CliNoContinueReason.TooOld);
    }

    [Fact]
    public void 中斷的時間取最後一次確認還在執行的時間_沒有就用開始的時間()
    {
        var record = Record(TimeSpan.FromHours(3));
        record.SeenAt = 0;
        InterruptedTurnRules.InterruptedAtUtc(record).Should().Be(Now - TimeSpan.FromHours(3) - TimeSpan.FromMinutes(1));
        InterruptedTurnRules.WhyNotContinue(record, Now).Should().Be(CliNoContinueReason.TooOld);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(long.MaxValue, 0L)]
    [InlineData(-5L, -5L)]
    public void 時間不明或超出範圍_當成太舊_不丟例外(long startedAt, long seenAt)
    {
        var record = new RunningTurnRecord { StartedAt = startedAt, SeenAt = seenAt };
        InterruptedTurnRules.InterruptedAtUtc(record).Should().BeNull();
        InterruptedTurnRules.WhyNotContinue(record, Now).Should().Be(CliNoContinueReason.TooOld);
    }

    [Fact]
    public void 被打斷的是高風險操作_不自動接續_提示寫出操作並跳脫Markdown()
    {
        var record = Record(TimeSpan.FromMinutes(1), risk: "執行指令：git push origin *main*");

        InterruptedTurnRules.WhyNotContinue(record, Now).Should().Be(CliNoContinueReason.RiskyTool);
        InterruptedTurnRules.DescribeNotContinued(record, CliNoContinueReason.RiskyTool, markdown: false).Should().Be(
            "Clode Myriagent 上次結束時這個對話還在執行，這個對話沒有自動接續：上一個操作〈執行指令：git push origin *main*〉結果未知，請確認後按「繼續」或送出新訊息");
        InterruptedTurnRules.DescribeNotContinued(record, CliNoContinueReason.RiskyTool, markdown: true).Should().Contain(@"git push origin \*main\*");
    }

    [Fact]
    public void 連續自動接續達上限_不再自動接續()
    {
        InterruptedTurnRules.WhyNotContinue(Record(TimeSpan.FromMinutes(1), autoResumes: InterruptedTurnRules.MaxAutoResumes - 1), Now).Should().BeNull();
        var record = Record(TimeSpan.FromMinutes(1), closedByApp: false, autoResumes: InterruptedTurnRules.MaxAutoResumes);

        InterruptedTurnRules.WhyNotContinue(record, Now).Should().Be(CliNoContinueReason.ContinuationFailed);
        InterruptedTurnRules.DescribeNotContinued(record, CliNoContinueReason.ContinuationFailed, markdown: false).Should().Be(
            "Clode Myriagent 上次沒有正常結束（當掉、被強制結束或斷電），這個對話被中斷，這個對話沒有自動接續：已連續自動接續 3 次都在完成前被中斷，請確認狀況後按「繼續」或送出新訊息");
    }

    [Fact]
    public void 自動接續的提示_分辨正常結束與意外結束()
    {
        InterruptedTurnRules.ContinuedNotice(Record(TimeSpan.Zero, closedByApp: true))
            .Should().Be("Clode Myriagent 上次結束時這個對話還在執行，重新開啟後已請代理從中斷處繼續");
        InterruptedTurnRules.ContinuedNotice(Record(TimeSpan.Zero, closedByApp: false))
            .Should().Be("Clode Myriagent 上次沒有正常結束（當掉、被強制結束或斷電），這個對話被中斷，重新開啟後已請代理從中斷處繼續");
    }

    [Fact]
    public void 系統匣通知_只在有接續或沒接續的對話時()
    {
        InterruptedTurnRules.Summary(0, 0).Should().BeNull();
        InterruptedTurnRules.Summary(2, 0).Should().Be("上次中斷的 2 個對話已自動接續");
        InterruptedTurnRules.Summary(1, 1).Should().Be("上次中斷的 1 個對話已自動接續；1 個上次中斷的對話沒有自動接續，請到對話裡確認後按「繼續」");
    }

    [Fact]
    public void 接續訊息_app正常結束_照快照寫出被打斷的工具與等回應的請求()
    {
        var record = Record(TimeSpan.Zero);
        record.InterruptedTools.Add(new AgentInterruptedTool("執行指令", "npm test", TimeSpan.FromSeconds(42)));
        record.PendingRequests.Add("執行指令：git status");
        var context = InterruptedTurnRules.Context(AgentProvider.Claude, record, "2.1.297");

        context.AppRestart.Should().Be(AppRestartKind.Closed);
        var text = ContinuationRules.Build(context);
        text.Should().StartWith("（系統通知：Clode Myriagent 上次結束時中斷了 Claude Code 這一輪的工作，現在 app 重新開啟、重新啟動了 Claude Code，這不是使用者拒絕或中斷。\n");
        text.Should().Contain("重啟時你正在執行〈執行指令：npm test〉，已執行 42 秒，結果未知");
        text.Should().Contain("重啟時你正在等使用者回應〈執行指令：git status〉");
        text.Should().EndWith("使用者在重啟後尚未傳送新訊息；請繼續原本的工作並回報。）");
    }

    [Fact]
    public void 接續訊息_意外結束而且沒記到工具_不說沒有工具在執行_請先確認最後的操作()
    {
        var text = ContinuationRules.Build(InterruptedTurnRules.Context(AgentProvider.Codex, Record(TimeSpan.Zero, closedByApp: false), null));

        text.Should().Be(
            "（系統通知：Clode Myriagent 上次沒有正常結束（當掉、被強制結束或斷電），中斷了 Codex 這一輪的工作，現在 app 重新開啟、重新啟動了 Codex，這不是使用者拒絕或中斷。\n"
            + "最後記錄到的狀態沒有工具在執行，但 app 是意外結束的，最後幾十秒做了什麼沒有記錄；請先確認最後一個操作是否已生效（例如檢查相關檔案、git 狀態或程序），再從中斷處繼續，不要重做已完成的步驟。\n"
            + "使用者在重啟後尚未傳送新訊息；請繼續原本的工作並回報。）");
    }

    [Fact]
    public void 接續訊息_app正常結束而且沒有工具_照安全點的說法()
        => ContinuationRules.Build(InterruptedTurnRules.Context(AgentProvider.Claude, Record(TimeSpan.Zero), null))
            .Should().Contain("重啟時你正在產生回覆，沒有任何工具在執行");

    private static RunningTurnRecord WaitingRecord(bool closedByApp, params string[] tasks)
    {
        var record = Record(TimeSpan.Zero, closedByApp);
        record.BackgroundWork = true;
        record.WaitingBackground = true;
        record.BackgroundTasks.AddRange(tasks);
        return record;
    }

    [Fact]
    public void 等背景工作回報的對話_提示分辨正常結束_意外結束與放棄的更新()
    {
        InterruptedTurnRules.ContinuedNotice(WaitingRecord(closedByApp: true))
            .Should().Be("Clode Myriagent 上次結束時停掉了這個對話還在跑的背景工作，重新開啟後已請代理確認背景工作做到哪裡、接著做");
        InterruptedTurnRules.ContinuedNotice(WaitingRecord(closedByApp: false))
            .Should().Be("Clode Myriagent 上次沒有正常結束（當掉、被強制結束或斷電），這個對話還在跑的背景工作跟著停了，重新開啟後已請代理確認背景工作做到哪裡、接著做");
        InterruptedTurnRules.ContinuedNotice(WaitingRecord(closedByApp: true), abandonedUpdate: true)
            .Should().Be("Clode Myriagent 為了套用更新停掉了這個對話還在跑的背景工作（這次更新已放棄），已請代理確認背景工作做到哪裡、接著做");
        InterruptedTurnRules.DescribeNotContinued(WaitingRecord(closedByApp: true), CliNoContinueReason.TooOld, markdown: false)
            .Should().StartWith("Clode Myriagent 上次結束時停掉了這個對話還在跑的背景工作，這個對話沒有自動接續：中斷已超過 1 小時");
        InterruptedTurnRules.WhyNotContinue(WaitingRecord(closedByApp: true), Now).Should().BeNull("等背景工作也算在進行，照同一套規則接續");
    }

    [Fact]
    public void 接續訊息_等背景工作回報的對話_列出背景工作_請先確認再照原計畫等它們回報()
    {
        var text = ContinuationRules.Build(InterruptedTurnRules.Context(AgentProvider.Claude, WaitingRecord(closedByApp: true, "執行指令：npm run dev", "子代理：檢查連結"), null));

        text.Should().Be(
            "（系統通知：Clode Myriagent 上次結束時停掉了 Claude Code 還在跑的背景工作，現在 app 重新開啟、重新啟動了 Claude Code，這不是使用者拒絕或中斷。\n"
            + "上一輪你已經回覆完，正在等背景工作〈執行指令：npm run dev〉、〈子代理：檢查連結〉回報；它們在重啟時已經停止，結果未知，也不會再有完成通知。"
            + "請先確認它們做到哪裡（例如輸出檔、程序、git 狀態），需要的話重新以背景執行（可安全重跑才重跑，不可重複的操作例如部署、發布、付款不要直接重跑），再照原本的計畫等它們完成並回報。\n"
            + "使用者在重啟後尚未傳送新訊息。）");

        var unexpected = ContinuationRules.Build(InterruptedTurnRules.Context(AgentProvider.Claude, WaitingRecord(closedByApp: false), null) with { UserRequested = true });
        unexpected.Should().StartWith("（系統通知：Clode Myriagent 上次沒有正常結束（當掉、被強制結束或斷電），Claude Code 還在跑的背景工作跟著停了");
        unexpected.Should().Contain("正在等背景工作回報；", "記不到是哪些背景工作時不列");
        unexpected.Should().NotContain("最後幾十秒做了什麼沒有記錄", "這一輪早就回覆完了");
        unexpected.Should().EndWith("使用者看過狀況後按了「繼續」，沒有另外傳送新訊息。）");
    }

    [Fact]
    public void running_json_來回一致_缺清單或含null的照樣讀得懂()
    {
        using var temp = new TempDirectory("running");
        var dir = temp.Combine("conv");
        RunningTurnStore.TryLoad(dir).Status.Should().Be(JsonReadStatus.Missing);

        var record = Record(TimeSpan.Zero, risk: "執行指令：npm publish", autoResumes: 2);
        record.InterruptedTools.Add(new AgentInterruptedTool("執行指令", "npm publish", TimeSpan.FromSeconds(3)));
        record.PendingRequests.Add("問題：要發布嗎？");
        record.BackgroundWork = true;
        record.WaitingBackground = true;
        record.BackgroundTasks.Add("執行指令：npm run dev");
        record.CliVersion = "2.1.297";
        RunningTurnStore.Save(dir, record);
        var (status, loaded) = RunningTurnStore.TryLoad(dir);

        status.Should().Be(JsonReadStatus.Ok);
        loaded.Should().BeEquivalentTo(record);

        File.WriteAllText(RunningTurnStore.PathFor(dir), """{"StartedAt":1,"SeenAt":2,"InterruptedTools":null,"PendingRequests":[null," ","問題"],"BackgroundTasks":[null,""]}""");
        var (_, odd) = RunningTurnStore.TryLoad(dir);
        odd!.InterruptedTools.Should().BeEmpty();
        odd.PendingRequests.Should().Equal("問題");
        odd.BackgroundTasks.Should().BeEmpty();
        odd.WaitingBackground.Should().BeFalse("比這個欄位舊的版本寫的：當成一般的執行中");

        RunningTurnStore.Delete(dir);
        File.Exists(RunningTurnStore.PathFor(dir)).Should().BeFalse();
        RunningTurnStore.Delete(dir);   // 本來就沒有也不丟例外
        RunningTurnStore.Delete(temp.Combine("gone"));   // 資料夾不在也不丟例外
    }
}
