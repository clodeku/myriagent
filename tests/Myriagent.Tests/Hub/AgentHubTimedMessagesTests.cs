using Myriagent.Agents;
using Myriagent.Schedules;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// Myriagent 的定時訊息（AgentHub.TimedMessages.cs）：排程頁新增、到點送進對話（顯示文字與給代理的文字不同）、一輪結束記下結果、
/// 對話執行中排進佇列不插入、每次開新對話、關機錯過的只補一次、app 剛啟動先等一下、myriagent-schedule 的新增／列出／刪除（只能動自己的）、
/// 存檔。時間以假時鐘推（ScheduleClock），不等真的計時器
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubTimedMessagesTests
{
    private static readonly DateTime Start = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Local);

    private sealed class NoTasks : IScheduleReader
    {
        public ScheduleScan Scan(CancellationToken ct) => new(Start, Array.Empty<ScheduledTaskInfo>(), null, 1, new ScheduleEnvironment("User", "PC", new Dictionary<string, string>()));

        public void Run(string path)
        {
        }

        public void Stop(string path)
        {
        }

        public void SetEnabled(string path, bool enabled)
        {
        }
    }

    private sealed class Clock
    {
        public DateTime Now = Start;
    }

    private static (HubHarness H, Clock Clock) Create()
    {
        var h = new HubHarness();
        var clock = new Clock();
        h.Ui.Invoke(() =>
        {
            h.Hub.ScheduleClock = () => clock.Now;
            h.Hub.AttachSchedules(new NoTasks(), Path.Combine(h.Temp.Root, "schedules"));
        });
        return (h, clock);
    }

    private static void Tick(HubHarness h, Clock clock, DateTime at)
    {
        clock.Now = at;
        h.Ui.Invoke(() => h.Hub.TickTimed());
    }

    private static JArray Timed(HubHarness h) => (JArray)h.Ui.Invoke(() => h.Hub.RenderSchedules())!["timed"]!;

    /// <summary>開一個已經做完一輪的對話（定時訊息只能送進正式對話）</summary>
    private static (string Id, FakeSession Session) Conversation(HubHarness h)
    {
        var (id, s) = h.StartConversation(h.Main, h.Claude, "第一則");
        h.CompleteTurn(s);
        return (id, s);
    }

    [Fact]
    public async Task 排程頁新增_到點送進對話_一輪結束記下結果()
    {
        var (h, clock) = Create();
        using var dispose = h;
        var (id, s) = Conversation(h);

        h.Command(h.Main, "timedSave", new { conversationId = id, text = "整理昨天的訂單異常", when = new { kind = "daily", value = "09:00" } });
        h.Main.Last("toast")!.Value<string>("text").Should().Be("已儲存定時訊息");
        var t = (JObject)Timed(h).Single();
        t.Value<string>("label").Should().Be("整理昨天的訂單異常");
        t.Value<string>("when").Should().Be("每天 09:00");
        t.Value<long>("nextAt").Should().Be(new DateTimeOffset(Start.AddHours(1)).ToUnixTimeMilliseconds());
        t.Value<string>("conversationTitle").Should().NotBeNullOrEmpty();
        File.Exists(Path.Combine(h.Temp.Root, "schedules", "timed.json")).Should().BeTrue();

        Tick(h, clock, Start.AddMinutes(59));
        s.Sent.Count.Should().Be(1, "還沒到");
        Tick(h, clock, Start.AddHours(1).AddSeconds(5));
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "到點送出");
        s.Sent.All[^1].Input.Text.Should().StartWith("（這是 Myriagent 的定時訊息「整理昨天的訂單異常」，每天 09:00，原定 2026/10/26 09:00）");
        h.Items(id).Last(i => i.Kind == AgentItemKind.User).Text.Should().StartWith("（定時訊息：整理昨天的訂單異常）");
        ((JObject)Timed(h).Single()["last"]!).Value<string>("text").Should().Be("已送出");

        h.CompleteTurn(s);
        var last = (JObject)Timed(h).Single()["last"]!;
        last.Value<string>("level").Should().Be("ok");
        last.Value<string>("text").Should().Be("完成");
        Timed(h).Single().Value<long>("nextAt").Should().Be(new DateTimeOffset(Start.AddDays(1).AddHours(1)).ToUnixTimeMilliseconds(), "下一次是明天");

        // 同一分鐘再看一次不會重送
        Tick(h, clock, Start.AddHours(1).AddSeconds(40));
        s.Sent.Count.Should().Be(2);
    }

    [Fact]
    public async Task 對話執行中_排進佇列不插入這一輪()
    {
        var (h, clock) = Create();
        using var dispose = h;
        var (id, s) = Conversation(h);
        h.Command(h.Main, "timedSave", new { conversationId = id, text = "檢查部署", when = new { kind = "at", value = "09:00" } });
        h.Send(h.Main, id, "正在做別的事");
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "開始另一輪");
        h.IsRunning(id).Should().BeTrue();

        Tick(h, clock, Start.AddHours(1).AddSeconds(5));
        s.Steered.Count.Should().Be(0, "不插進這一輪");
        ((JObject)Timed(h).Single()["last"]!).Value<string>("text").Should().Be("已排進佇列");
        Timed(h).Single().Value<bool>("done").Should().BeTrue("一次性的送過就完成");

        h.CompleteTurn(s);
        await h.WaitUntilAsync(() => s.Sent.Count == 3, "這一輪結束後送出排隊的");
        s.Sent.All[^1].Input.Text.Should().Contain("檢查部署");
    }

    [Fact]
    public async Task 每次開新對話_照記下的帳戶與資料夾開一個()
    {
        var (h, clock) = Create();
        using var dispose = h;
        var (id, _) = Conversation(h);
        h.Command(h.Main, "timedSave", new { conversationId = id, text = "日報", label = "訂單日報", newConversation = true, when = new { kind = "weekdays", value = "09:00" } });
        var before = h.Sessions.Count;
        Tick(h, clock, Start.AddHours(1).AddSeconds(5));
        await h.WaitUntilAsync(() => h.Sessions.Count == before + 1, "開了新對話");
        var run = (JObject)((JArray)Timed(h).Single()["runs"]!).Single();
        var newId = run.Value<string>("conversationId")!;
        newId.Should().NotBe(id);
        h.Conversation(newId)!.Title.Should().Be("訂單日報（10/26 09:00）");
        h.Conversation(newId)!.AccountId.Should().Be(h.Claude.Id);
    }

    /// <summary>2026-10-31 實例：帳戶每週額度用完，到點第一輪幾秒就失敗、換帳戶後發文成功，排程頁卻記成失敗</summary>
    [Fact]
    public async Task 額度用完換帳戶接著做_結果記接著做的那一輪()
    {
        var (h, clock) = Create();
        using var dispose = h;
        h.OnUi(() => h.Settings.AutoResumeAfterQuotaReset = false);
        var (id, s1) = Conversation(h);
        h.Command(h.Main, "timedSave", new { conversationId = id, text = "發一篇新貼文", when = new { kind = "daily", value = "09:00" } });
        Tick(h, clock, Start.AddHours(1).AddSeconds(5));
        await h.WaitUntilAsync(() => s1.Sent.Count == 2, "到點送出");

        h.Raise(s1, new AgentQuotaExhaustedEvent(AgentProvider.Claude, DateTimeOffset.Now.AddHours(3)));
        h.CompleteTurn(s1, AgentTurnOutcome.Failed, "You've hit your weekly limit");
        h.Conversation(id)!.AccountId.Should().Be(h.Claude2.Id, "換到還有額度的帳戶接著做");
        ((JObject)Timed(h).Single()["last"]!).Value<string>("text").Should().Be("已送出", "還在接著做，不記成失敗");

        h.CompleteTurn(h.Sessions[^1]);
        var last = (JObject)Timed(h).Single()["last"]!;
        last.Value<string>("level").Should().Be("ok");
        last.Value<string>("text").Should().Be("完成");
    }

    [Fact]
    public async Task 一般的失敗_照樣記成失敗()
    {
        var (h, clock) = Create();
        using var dispose = h;
        var (id, s) = Conversation(h);
        h.Command(h.Main, "timedSave", new { conversationId = id, text = "發一篇新貼文", when = new { kind = "daily", value = "09:00" } });
        Tick(h, clock, Start.AddHours(1).AddSeconds(5));
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "到點送出");

        h.CompleteTurn(s, AgentTurnOutcome.Failed, "代理執行失敗");
        var last = (JObject)Timed(h).Single()["last"]!;
        last.Value<string>("level").Should().Be("fail");
        last.Value<string>("text").Should().Be("失敗");
    }

    [Fact]
    public async Task 關機錯過好幾次_只補一次_啟動後先等一下()
    {
        var (h, clock) = Create();
        using var dispose = h;
        var (id, s) = Conversation(h);
        h.Command(h.Main, "timedSave", new { conversationId = id, text = "每小時看一下", when = new { kind = "every", value = "1h" } });

        // app 重開：重新掛上，先等 20 秒才送
        clock.Now = Start.AddHours(5).AddMinutes(10);
        h.Ui.Invoke(() => h.Hub.AttachSchedules(new NoTasks(), Path.Combine(h.Temp.Root, "schedules")));
        Tick(h, clock, clock.Now.AddSeconds(5));
        s.Sent.Count.Should().Be(1, "剛啟動先等一下");
        Tick(h, clock, clock.Now.AddSeconds(30));
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "補送一次");
        s.Sent.All[^1].Input.Text.Should().Contain("原定 2026/10/26 13:00，電腦或 app 當時沒開，現在補送");
        h.CompleteTurn(s);
        Tick(h, clock, clock.Now.AddMinutes(1));
        s.Sent.Count.Should().Be(2, "錯過的其他幾次不補");
        var run = (JObject)((JArray)Timed(h).Single()["runs"]!).First();
        run.Value<bool>("late").Should().BeTrue();
    }

    [Fact]
    public void Myriagent_schedule_新增列出刪除_只能動自己的()
    {
        var (h, clock) = Create();
        using var dispose = h;
        var (a, _) = Conversation(h);
        var (b, _) = Conversation(h);
        _ = clock;

        var add = h.Ui.Invoke(() => h.Hub.HandleTimedCli(a, JObject.Parse("""{"op":"add","when":{"kind":"every","value":"30m"},"text":"看一下佇列","label":"佇列"}""")));
        add.Value<bool>("ok").Should().BeTrue();
        add.Value<string>("message").Should().StartWith("已建立定時訊息 tm_").And.Contain("每 30 分鐘，下一次 2026/10/26 08:30");
        var tid = add["item"]!.Value<string>("id")!;

        var bad = h.Ui.Invoke(() => h.Hub.HandleTimedCli(a, JObject.Parse("""{"op":"add","when":{"kind":"every","value":"1m"},"text":"太密"}""")));
        bad.Value<bool>("ok").Should().BeFalse();

        var list = h.Ui.Invoke(() => h.Hub.HandleTimedCli(b, JObject.Parse("""{"op":"list"}""")));
        var item = (JObject)((JArray)list["items"]!).Single();
        item.Value<bool>("mine").Should().BeFalse("b 看得到但不是它的");
        h.Ui.Invoke(() => h.Hub.HandleTimedCli(b, JObject.Parse($$"""{"op":"remove","id":"{{tid}}"}"""))).Value<string>("message").Should().Contain("不是這個對話建立的");

        var timed = (JObject)Timed(h).Single();
        timed["createdBy"]!.Value<string>("conversationId").Should().Be(a);
        h.Ui.Invoke(() => h.Hub.HandleTimedCli(a, JObject.Parse($$"""{"op":"pause","id":"{{tid}}"}"""))).Value<bool>("ok").Should().BeTrue();
        Timed(h).Single().Value<bool>("enabled").Should().BeFalse();
        h.Ui.Invoke(() => h.Hub.HandleTimedCli(a, JObject.Parse($$"""{"op":"remove","id":"{{tid}}"}"""))).Value<bool>("ok").Should().BeTrue();
        Timed(h).Should().BeEmpty();
    }

    [Fact]
    public void 立即送一次_不動下一次_刪除與錯誤()
    {
        var (h, clock) = Create();
        using var dispose = h;
        var (id, s) = Conversation(h);
        h.Command(h.Main, "timedSave", new { conversationId = id, text = "x", when = new { kind = "daily", value = "09:00" } });
        var tid = Timed(h).Single().Value<string>("id")!;
        var next = Timed(h).Single().Value<long>("nextAt");
        clock.Now = Start.AddMinutes(1);
        h.Command(h.Main, "timedAction", new { id = tid, action = "run" });
        Timed(h).Single().Value<long>("nextAt").Should().Be(next);

        var mark = h.Main.Mark();
        h.Command(h.Main, "timedSave", new { conversationId = id, text = "", when = new { kind = "daily", value = "09:00" } });
        h.Main.Messages("toast", mark).Single().Value<string>("text").Should().Be("少了要送的文字");
        h.Command(h.Main, "timedSave", new { conversationId = "nope", text = "x", when = new { kind = "daily", value = "09:00" } });
        h.Main.Messages("toast", mark).Last().Value<string>("text").Should().Be("找不到要送進的對話");
        h.Command(h.Main, "timedAction", new { id = tid, action = "delete" });
        Timed(h).Should().BeEmpty();
        _ = s;
    }
}
