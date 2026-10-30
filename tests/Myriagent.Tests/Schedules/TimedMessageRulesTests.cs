using Myriagent.Schedules;
using MyriagentSchedule;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Schedules;

/// <summary>Myriagent 的定時訊息：cron 的解析、下一次、中文摘要、時間的寫法、到點的判斷（錯過只補一次、一次性的太久不補）、myriagent-schedule 的參數</summary>
public sealed class TimedMessageRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 10, 0, 0, DateTimeKind.Local); // 週一

    [Theory]
    [InlineData("0 9 * * *", "2026-10-26 10:00", "2026-10-27 09:00", "每天 09:00")]
    [InlineData("30 8 * * 1-5", "2026-10-30 09:00", "2026-11-02 08:30", "平日 08:30")]
    [InlineData("0 9 * * 0,6", "2026-10-26 10:00", "2026-10-31 09:00", "週末 09:00")]
    [InlineData("0 9 * * MON,WED", "2026-10-26 10:00", "2026-10-28 09:00", "每週一、三 09:00")]
    [InlineData("*/15 * * * *", "2026-10-26 10:07", "2026-10-26 10:15", "每 15 分鐘")]
    [InlineData("5 * * * *", "2026-10-26 10:07", "2026-10-26 11:05", "每小時的 :05")]
    [InlineData("0 3 1 * *", "2026-10-26 10:00", "2026-11-01 03:00", "每月 1 日 03:00")]
    [InlineData("0 0 29 2 *", "2026-10-26 10:00", "2028-02-29 00:00", "cron：0 0 29 2 *")]
    [InlineData("0 9 1 * 1", "2026-10-26 10:00", "2026-11-01 09:00", "cron：0 9 1 * 1")]
    [InlineData("0 0 * * 7", "2026-10-26 10:00", "2026-11-01 00:00", "每週日 00:00")]
    public void Cron_NextAndDescribe(string cron, string after, string next, string text)
    {
        var spec = CronSpec.Parse(cron, out var problem)!;
        problem.Should().BeNull();
        spec.Next(DateTime.Parse(after)).Should().Be(DateTime.Parse(next));
        spec.Describe().Should().Be(text);
    }

    [Theory]
    [InlineData("0 9 * *")]
    [InlineData("60 9 * * *")]
    [InlineData("0 24 * * *")]
    [InlineData("0 9 32 * *")]
    [InlineData("0 9 * * 8")]
    [InlineData("a b c d e")]
    [InlineData("*/0 * * * *")]
    [InlineData("5-1 * * * *")]
    public void Cron_Invalid(string cron)
    {
        CronSpec.Parse(cron, out var problem).Should().BeNull();
        problem.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Cron_ImpossibleDateHasNoNext()
        => CronSpec.Parse("0 0 30 2 *", out _)!.Next(Now).Should().BeNull();

    [Theory]
    [InlineData("daily", "09:00", "0 9 * * *", null, null)]
    [InlineData("weekdays", "18:30", "30 18 * * 1-5", null, null)]
    [InlineData("every", "30m", "*/30 * * * *", null, null)]
    [InlineData("every", "2h", "0 */2 * * *", null, null)]
    [InlineData("cron", "0 9 * * 1-5", "0 9 * * 1-5", null, null)]
    [InlineData("at", "14:00", null, "2026-10-26 14:00", null)]
    [InlineData("at", "09:00", null, "2026-10-27 09:00", null)]
    [InlineData("at", "2026-10-27 14:00", null, "2026-10-27 14:00", null)]
    [InlineData("at", "10/27 14:00", null, "2026-10-27 14:00", null)]
    [InlineData("at", "10/1 14:00", null, "2027-10-01 14:00", null)]
    [InlineData("at", "2026-10-25 14:00", null, null, "時間已經過了")]
    [InlineData("every", "7m", null, null, "每幾分鐘要能整除 60，而且至少 5 分鐘（例如 5、10、15、30）")]
    [InlineData("every", "2m", null, null, "每幾分鐘要能整除 60，而且至少 5 分鐘（例如 5、10、15、30）")]
    [InlineData("every", "5h", null, null, "每幾小時要能整除 24（例如 1、2、3、4、6、8、12）")]
    [InlineData("cron", "* * * * *", null, null, "兩次之間至少要 5 分鐘")]
    [InlineData("daily", "9:0", null, null, "時間看不懂：用 HH:mm（例如 09:00）")]
    [InlineData("daily", "", null, null, "少了時間")]
    [InlineData("hourly", "1", null, null, "不認得的時間寫法")]
    public void ParseWhen(string kind, string value, string? cron, string? at, string? problem)
    {
        var r = TimedMessageRules.ParseWhen(kind, value, Now);
        r.Problem.Should().Be(problem);
        r.Cron.Should().Be(cron);
        r.At.Should().Be(at == null ? null : DateTime.Parse(at));
    }

    [Fact]
    public void Decide_OnTimeLateMissedAndCatchUpOnce()
    {
        var daily = new TimedMessage { Cron = "0 9 * * *", CreatedAt = Now.AddDays(-3), LastScheduledAt = new DateTime(2026, 10, 25, 9, 0, 0) };
        TimedMessageRules.NextDue(daily).Should().Be(new DateTime(2026, 10, 26, 9, 0, 0));
        TimedMessageRules.Decide(daily, new DateTime(2026, 10, 26, 8, 59, 0)).Due.Should().Be(TimedDue.Wait);
        var onTime = TimedMessageRules.Decide(daily, new DateTime(2026, 10, 26, 9, 0, 30));
        onTime.Should().Be((TimedDue.Fire, new DateTime(2026, 10, 26, 9, 0, 0), false));

        // 關機三天：只補一次，記成最後一次錯過的
        var late = TimedMessageRules.Decide(daily, new DateTime(2026, 10, 28, 11, 0, 0));
        late.Should().Be((TimedDue.Fire, new DateTime(2026, 10, 28, 9, 0, 0), true));

        var once = new TimedMessage { At = new DateTime(2026, 10, 26, 9, 0, 0), CreatedAt = Now.AddDays(-1) };
        TimedMessageRules.Decide(once, Now).Should().Be((TimedDue.Fire, new DateTime(2026, 10, 26, 9, 0, 0), true));
        TimedMessageRules.Decide(once, Now.AddHours(12)).Due.Should().Be(TimedDue.Missed);
        once.Done = true;
        TimedMessageRules.Decide(once, Now).Due.Should().Be(TimedDue.Wait);
        daily.Enabled = false;
        TimedMessageRules.NextDue(daily).Should().BeNull();
    }

    [Fact]
    public void Texts_AndUpcoming()
    {
        var m = new TimedMessage { Text = "\n整理昨天的訂單異常\n寫成報表", Cron = "0 9 * * 1-5", CreatedAt = Now };
        TimedMessageRules.LabelOf(m).Should().Be("整理昨天的訂單異常");
        TimedMessageRules.Describe(m).Should().Be("平日 09:00");
        TimedMessageRules.AgentText(m, new DateTime(2026, 10, 27, 9, 0, 0), true)
            .Should().StartWith("（這是 Myriagent 的定時訊息「整理昨天的訂單異常」，平日 09:00，原定 2026/10/27 09:00，電腦或 app 當時沒開，現在補送）\n\n");
        TimedMessageRules.DisplayText(m, false).Should().StartWith("（定時訊息：整理昨天的訂單異常）\n");
        TimedMessageRules.Upcoming(m, Now, Now.AddHours(24)).Should().Equal(new DateTime(2026, 10, 27, 9, 0, 0));
        TimedMessageRules.Upcoming(new TimedMessage { Cron = "*/30 * * * *", CreatedAt = Now }, Now, Now.AddHours(2)).Should().HaveCount(4);
        TimedMessageRules.Describe(new TimedMessage { At = new DateTime(2026, 10, 27, 14, 0, 0) }).Should().Be("一次：2026/10/27 14:00");
        TimedMessageRules.LevelOf("completed").Should().Be("ok");
        TimedMessageRules.LevelOf("error").Should().Be("fail");
        TimedMessageRules.OutcomeText(new TimedRun { Outcome = "error", Error = "對話已經刪除" }).Should().Be("沒送出：對話已經刪除");
    }

    [Fact]
    public void Cli_ParsesArgumentsAndBuildsRequest()
    {
        var a = CliArgs.Parse(new[] { "add", "--daily", "09:00", "--new", "--label", "日報", "--", "整理", "昨天的訂單" });
        a.Error.Should().BeNull();
        a.Op.Should().Be("add");
        a.WhenKind.Should().Be("daily");
        a.Text.Should().Be("整理 昨天的訂單");
        a.NewConversation.Should().BeTrue();
        var req = System.Text.Json.Nodes.JsonNode.Parse(CliArgs.Request(a, "c1", "tok"))!;
        req["op"]!.GetValue<string>().Should().Be("timed");
        req["timed"]!["when"]!["value"]!.GetValue<string>().Should().Be("09:00");
        req["timed"]!["new"]!.GetValue<bool>().Should().BeTrue();

        CliArgs.Parse(new[] { "add", "--daily", "09:00" }).Error.Should().Contain("文字");
        CliArgs.Parse(new[] { "add", "--", "x" }).Error.Should().Contain("時間");
        CliArgs.Parse(new[] { "add", "--daily", "09:00", "--every", "5m", "--", "x" }).Error.Should().Contain("擇一");
        CliArgs.Parse(new[] { "remove" }).Error.Should().Contain("id");
        CliArgs.Parse(new[] { "remove", "tm_1" }).Id.Should().Be("tm_1");
        CliArgs.Parse(new[] { "frob" }).Error.Should().Contain("不認得");
        CliArgs.Parse(Array.Empty<string>()).Help.Should().BeTrue();

        var outw = new StringWriter();
        var errw = new StringWriter();
        CliArgs.Print("{\"t\":\"timed\",\"ok\":false,\"message\":\"時間已經過了\"}", outw, errw, false).Should().Be(1);
        errw.ToString().Should().Contain("時間已經過了");
        CliArgs.Print("{\"t\":\"timed\",\"ok\":true,\"items\":[{\"id\":\"tm_1\",\"label\":\"日報\",\"when\":\"每天 09:00\",\"next\":\"2026/10/27 09:00\",\"enabled\":true,\"mine\":true,\"newConversation\":false,\"conversation\":\"訂單\"}]}", outw, errw, false)
            .Should().Be(0);
        outw.ToString().Should().Contain("* tm_1  日報｜每天 09:00，下一次 2026/10/27 09:00｜送進「訂單」");

        // myriagent-schedule 的要求，app 的管道照 myriagent-remote 的規則收（op:"timed" 要帶內容）
        var (request, problem) = Myriagent.RemoteExec.RemoteExecPipeRules.ParseRequest(CliArgs.Request(a, "c1", "tok"));
        problem.Should().BeNull();
        request!.Op.Should().Be("timed");
        request.Timed!.Value<string>("op").Should().Be("add");
        Myriagent.RemoteExec.RemoteExecPipeRules.ParseRequest("{\"v\":1,\"op\":\"timed\",\"conversation\":\"c\",\"token\":\"t\"}").Problem.Should().Be("少了定時訊息的內容");
    }
}
