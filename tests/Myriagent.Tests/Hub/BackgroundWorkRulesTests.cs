using Myriagent.Agents;
using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

public sealed class BackgroundWorkStoppedTurnNoticeTests
{
    [Fact]
    public void 停止的提示_排隊與背景工作()
    {
        BackgroundWorkRules.StoppedTurnNotice(false, 0).Should().Be("已停止這一輪");
        BackgroundWorkRules.StoppedTurnNotice(true, 0).Should().Be("已停止這一輪（排隊的訊息暫停，按送出繼續）");
        BackgroundWorkRules.StoppedTurnNotice(false, 1).Should().Be("已停止這一輪。背景工作繼續執行，完成時 Claude 會收到通知並接著處理；要停下按狀態列的「停止」。");
        BackgroundWorkRules.StoppedTurnNotice(true, 3).Should().StartWith("已停止這一輪（排隊的訊息暫停，按送出繼續）。3 個背景工作繼續執行");
    }
}

public sealed class BackgroundWorkRulesTests
{
    [Fact]
    public void View_數量與工具_摘要只取第一行_重複的只列一次_最多五個_沒有背景工作是null()
    {
        var tools = new List<AgentBackgroundTool>
        {
            new("執行指令", "bash", "  npm test\nnpm run lint", "詳細"),
            new("執行指令", "bash", "npm test", null),
            new("子代理", "agent", null, null),
        };
        tools.AddRange(Enumerable.Range(1, 6).Select(i => new AgentBackgroundTool("執行指令", "bash", "sleep " + i, null)));

        var view = BackgroundWorkRules.View(12, tools)!;

        view.Value<int>("count").Should().Be(12, "數量照會話回報的（對不到工具的也算）");
        var tasks = view["tasks"]!.Cast<JObject>().ToList();
        tasks.Should().HaveCount(BackgroundWorkRules.MaxTasks);
        tasks[0].Value<string>("title").Should().Be("npm test", "一行摘要");
        tasks[1].Value<string>("tool").Should().Be("子代理", "同一個工具與摘要只列一次");
        tasks[1].Value<string>("title").Should().BeNull("沒有摘要的是 null（網頁改寫工具名稱）");
        tasks[2].Value<string>("title").Should().Be("sleep 1");

        BackgroundWorkRules.View(0, tools).Should().BeNull();
        BackgroundWorkRules.View(-1, Array.Empty<AgentBackgroundTool>()).Should().BeNull();
        BackgroundWorkRules.View(1, Array.Empty<AgentBackgroundTool>())!["tasks"]!.Should().BeEmpty();
    }

    [Fact]
    public void View_開始時間_工作各自的_同一個說明取最早的_外層是最早的含列不下的_都對不到工具用since()
    {
        var t0 = new DateTime(2026, 10, 25, 1, 0, 0, DateTimeKind.Local);
        long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
        var tools = new List<AgentBackgroundTool>
        {
            new("執行指令", "bash", "npm test", null),
            new("子代理", "agent", "檢查連結", null),
            new("執行指令", "bash", "npm test", "另一個"),
        };
        tools.AddRange(Enumerable.Range(1, 4).Select(i => new AgentBackgroundTool("執行指令", "bash", "sleep " + i, null)));
        var starts = new List<DateTime> { t0.AddMinutes(5), t0.AddMinutes(3), t0.AddMinutes(2), t0.AddMinutes(4), t0.AddMinutes(6), t0.AddMinutes(7), t0.AddMinutes(1) };

        var view = BackgroundWorkRules.View(8, tools, starts, since: t0)!;

        var tasks = view["tasks"]!.Cast<JObject>().ToList();
        tasks.Should().HaveCount(BackgroundWorkRules.MaxTasks);
        tasks[0].Value<long>("startedAt").Should().Be(Ms(t0.AddMinutes(2)), "同一個說明只列一次：開始時間取最早的那一個");
        tasks[1].Value<long>("startedAt").Should().Be(Ms(t0.AddMinutes(3)));
        tasks[2].Value<long>("startedAt").Should().Be(Ms(t0.AddMinutes(4)));
        view.Value<long>("startedAt").Should().Be(Ms(t0.AddMinutes(1)), "外層是最早開始的（列不下的 sleep 4 也算）；對到工具時不用 since");

        BackgroundWorkRules.View(2, Array.Empty<AgentBackgroundTool>(), Array.Empty<DateTime>(), since: t0)!.Value<long>("startedAt")
            .Should().Be(Ms(t0), "都對不到工具：用這個會話開始有背景工作的時間");
        var unknown = BackgroundWorkRules.View(1, tools.Take(1).ToList())!;
        unknown["startedAt"]!.Type.Should().Be(JTokenType.Null, "不明是 null");
        ((JObject)unknown["tasks"]![0]!)["startedAt"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void Starts_上一次就有的沿用_重複的各對各的_新的用工具卡時間_沒有卡片或卡片比現在晚用現在()
    {
        var t0 = new DateTime(2026, 10, 25, 1, 0, 0, DateTimeKind.Local);
        var now = t0.AddMinutes(10);
        var test = new AgentBackgroundTool("執行指令", "bash", "npm test", null);
        var dev = new AgentBackgroundTool("執行指令", "bash", "npm run dev", null);
        var agent = new AgentBackgroundTool("子代理", "agent", "檢查連結", null);

        var first = BackgroundWorkRules.Starts(Array.Empty<AgentBackgroundTool>(), Array.Empty<DateTime>(), new[] { test, dev }, t0);
        first.Should().Equal(t0, t0);

        // 又開了一個 npm test 與子代理（順序也變了）：還在跑的 npm run dev、npm test 沿用（說明相同的依序對上），
        // 新的 npm test 用卡片的時間、子代理的卡片時間比現在晚就用現在
        var second = BackgroundWorkRules.Starts(new[] { test, dev }, new[] { t0.AddMinutes(-3), t0 }, new[] { dev, test, test, agent }, now,
            i => i == 2 ? t0.AddMinutes(8) : i == 3 ? now.AddMinutes(1) : null);
        second.Should().Equal(t0, t0.AddMinutes(-3), t0.AddMinutes(8), now);

        BackgroundWorkRules.Starts(new[] { test }, Array.Empty<DateTime>(), new[] { test }, now)
            .Should().Equal(new[] { now }, "上一次的開始時間不齊（換了會話給空的）：當成新的");
    }

    [Fact]
    public void Labels_工具名稱加一行摘要_同一個只列一次_最多5個()
    {
        var tools = new List<AgentBackgroundTool>
        {
            new("執行指令", "bash", "npm test\n-- --watch", null),
            new("執行指令", "bash", "npm test", null),
            new("子代理", "agent", null, null),
            new(" ", "bash", "sleep 1", null),
        };
        tools.AddRange(Enumerable.Range(1, 5).Select(i => new AgentBackgroundTool("執行指令", "bash", $"job {i}", null)));

        BackgroundWorkRules.Labels(tools).Should().Equal("執行指令：npm test", "子代理", "工具：sleep 1", "執行指令：job 1", "執行指令：job 2");
        BackgroundWorkRules.Labels(Array.Empty<AgentBackgroundTool>()).Should().BeEmpty();
    }

    [Fact]
    public void CompletedTitle_背景工作還在跑就不是已完成()
    {
        BackgroundWorkRules.CompletedTitle(false).Should().Be("代理已完成");
        BackgroundWorkRules.CompletedTitle(true).Should().Be("代理已回覆，背景工作還在執行");
    }

    private static AgentBackgroundCommandFinishedEvent Finished(string? title, AgentItemStatus status, int? exitCode, double? seconds)
        => new("t", title, status, exitCode, seconds is { } s ? TimeSpan.FromSeconds(s) : null);

    [Fact]
    public void Describe_指令_結束碼_耗時_拒絕的寫沒有執行()
    {
        BackgroundWorkRules.Describe(Finished("npm test", AgentItemStatus.Done, 0, 26.9)).Should().Be("`npm test`（結束碼 0，執行了 27 秒）", "秒數四捨五入");
        BackgroundWorkRules.Describe(Finished("npm test\n-- --watch", AgentItemStatus.Failed, 1, 125)).Should().Be("`npm test`（結束碼 1，執行了 2 分 5 秒）", "只取第一行");
        BackgroundWorkRules.Describe(Finished("build", AgentItemStatus.Done, null, 3_725)).Should().Be("`build`（已結束，執行了 1 小時 2 分）");
        BackgroundWorkRules.Describe(Finished(null, AgentItemStatus.Failed, null, null)).Should().Be("背景指令（失敗）");
        BackgroundWorkRules.Describe(Finished("rm -rf x", AgentItemStatus.Declined, null, null)).Should().Be("`rm -rf x`（沒有執行）");
        BackgroundWorkRules.Describe(Finished("x", AgentItemStatus.Done, 0, 0.2)).Should().Be("`x`（結束碼 0，執行了 1 秒）", "不到一秒寫 1 秒");
    }

    [Fact]
    public void ContinuationText_說明是系統通知_列出跑完的與還在跑的_請代理接著做()
    {
        var text = BackgroundWorkRules.ContinuationText(
            new[] { Finished("npm test", AgentItemStatus.Failed, 1, 30) },
            new[] { new AgentBackgroundTool("執行指令", "bash", "npm run dev", null), new AgentBackgroundTool("執行指令", "bash", null, null) });

        text.Split('\n').Should().Equal(
            "（系統通知：你先前放到背景執行的指令已經結束，這不是使用者送的訊息。）",
            "已結束：`npm test`（結束碼 1，執行了 30 秒）。",
            "還在背景執行：`npm run dev`（結束時會再通知你，不必等它）。",
            "請查看已結束的指令的輸出與結果，接著完成原本的工作；如果不需要再做什麼，簡短說明結果即可。");
        BackgroundWorkRules.ContinuationText(new[] { Finished("a", AgentItemStatus.Done, 0, null) }, Array.Empty<AgentBackgroundTool>())
            .Should().NotContain("還在背景執行");
        BackgroundWorkRules.ContinuationNotice(new[] { Finished("a", AgentItemStatus.Done, 0, null), Finished("b", AgentItemStatus.Failed, 2, null) })
            .Should().Be("背景指令已結束：`a`（結束碼 0）、`b`（結束碼 2），代理接著處理。");
    }

    [Fact]
    public void NotContinuedNotice_說明為什麼沒有自動接續()
    {
        var finished = new[] { Finished("a", AgentItemStatus.Done, 0, null) };
        BackgroundWorkRules.NotContinuedNotice(finished, queuePaused: true).Should().Be("背景指令已結束：`a`（結束碼 0）。排隊的訊息暫停中，這次沒有讓代理自動接著處理，按送出繼續。");
        BackgroundWorkRules.NotContinuedNotice(finished, queuePaused: false).Should().Be($"背景指令已結束：`a`（結束碼 0）。代理已連續自動接著處理 {BackgroundWorkRules.MaxAutoContinues} 次，這次沒有自動接續；要它處理就送一則訊息。");
    }

    [Fact]
    public void View_能單獨停止的帶stopId_停止的提示列出停了什麼()
    {
        var tools = new List<AgentBackgroundTool>
        {
            new("執行指令", "bash", "npm run dev", null, TaskId: "b1", StopId: "b1"),
            new("子代理", "agent", "整理", null, StopId: "a1"),
            new("背景工作", "bash", "sleep 30", null),
        };
        var tasks = BackgroundWorkRules.View(3, tools)!["tasks"]!.Cast<JObject>().ToList();
        tasks[0].Value<string>("stopId").Should().Be("b1");
        tasks.Select(t => t.Value<string>("key")).Should().Equal(tools.Select(BackgroundTaskDetailRules.Key), "「詳細」以 key 問這一個");
        tasks[1].Value<string>("stopId").Should().Be("a1", "子代理看不到輸出，但照樣停得了");
        tasks[1].ContainsKey("taskId").Should().BeFalse();
        tasks[2].ContainsKey("stopId").Should().BeFalse("Antigravity、Grok：只能全部停止");

        BackgroundWorkRules.StoppedNotice(1, tools.Take(1).ToList()).Should().Be("已強制停止背景工作：執行指令：npm run dev（代理不會自動接著處理）。");
        BackgroundWorkRules.StoppedNotice(3, tools.Take(2).ToList()).Should().Be("已強制停止 3 個背景工作：執行指令：npm run dev、子代理：整理 等（代理不會自動接著處理）。",
            "全部停止：數量照標頭的 count，對不到工具的只算數");
        BackgroundWorkRules.StoppedNotice(2, Array.Empty<AgentBackgroundTool>()).Should().Be("已強制停止 2 個背景工作（代理不會自動接著處理）。");
    }
}
