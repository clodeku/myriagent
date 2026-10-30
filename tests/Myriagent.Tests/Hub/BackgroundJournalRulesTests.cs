using Myriagent.Agents;
using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>背景活動日誌的純函式規則（docs/background-journal.md）</summary>
public sealed class BackgroundJournalRulesTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 9, 8, 7, 654, TimeSpan.FromHours(8));

    [Fact]
    public void 一筆的形狀_時間事件對話在前_資料接著_值是null的不寫_app的程序與版本在最後()
    {
        var entry = BackgroundJournalRules.Entry("bg.start", At, 1234, "0.2.789",
            new JournalConversation("c1", "修\n部署", "Claude", "acc-1"),
            new JObject { ["tool"] = "執行指令", ["taskId"] = null, ["title"] = (string?)null, ["count"] = 1 });

        entry.Properties().Select(p => p.Name).Should().Equal("t", "ev", "conv", "convTitle", "prov", "acct", "tool", "count", "appPid", "ver");
        entry.Value<string>("t").Should().Be("2026-10-27T09:08:07.654+08:00");
        entry.Value<string>("convTitle").Should().Be("修⏎部署");
        entry.Value<int>("appPid").Should().Be(1234);
    }

    [Fact]
    public void 資料和信封同名的欄位改名_不蓋掉信封()
    {
        var entry = BackgroundJournalRules.Entry("bg.start", At, 7, "1.0", new JournalConversation("c1", "對話標題", "Codex", null),
            new JObject { ["title"] = "npm run dev", ["conv"] = "別的", ["appPid"] = 99, ["ev"] = "x" });
        entry.Value<string>("convTitle").Should().Be("對話標題");
        entry.Value<string>("title").Should().Be("npm run dev", "工具的標題不是信封的欄位");
        (entry.Value<string>("conv"), entry.Value<int>("appPid"), entry.Value<string>("ev")).Should().Be(("c1", 7, "bg.start"));
        (entry.Value<string>("data.conv"), entry.Value<int>("data.appPid"), entry.Value<string>("data.ev")).Should().Be(("別的", 99, "x"));
    }

    [Fact]
    public void 沒有對話的事件_草稿沒有標題_沒有帳戶與版本就不寫()
    {
        BackgroundJournalRules.Entry("app.start", At, 1, null, null, null).Properties().Select(p => p.Name).Should().Equal("t", "ev", "appPid");
        BackgroundJournalRules.Entry("turn.start", At, 1, "1.0", new JournalConversation("d1", null, "Codex", ""), null)
            .Properties().Select(p => p.Name).Should().Equal("t", "ev", "conv", "prov", "appPid", "ver");
    }

    [Fact]
    public void 文字_遮掉憑證_換行攤平_太長截短_空的是null()
    {
        BackgroundJournalRules.Clip(null).Should().BeNull();
        BackgroundJournalRules.Clip("  \n ").Should().BeNull();
        BackgroundJournalRules.Clip(" a\r\nb\nc ").Should().Be("a ⏎ b⏎c");
        BackgroundJournalRules.Clip(new string('x', 20), 10).Should().Be(new string('x', 9) + "…");

        var token = "ghp_" + new string('A', 36);
        var masked = BackgroundJournalRules.Clip("git push https://" + token + "@github.com/x/y", 600)!;
        masked.Should().NotContain(token, "命令列可能帶權杖");
        masked.Should().Contain("github.com/x/y");
    }

    [Fact]
    public void 背景工作_帶task_id與停止id_空的欄位不寫()
    {
        BackgroundJournalRules.Tool(new AgentBackgroundTool("執行指令", "bash", "npm run dev", "npm run dev --port 5173", "b1", "b1"))
            .Should().BeEquivalentTo(new JObject
            {
                ["tool"] = "執行指令", ["kind"] = "bash", ["title"] = "npm run dev", ["detail"] = "npm run dev --port 5173", ["taskId"] = "b1", ["stopId"] = "b1",
            });
        BackgroundJournalRules.Tool(new AgentBackgroundTool("子代理", null, null, null)).Properties().Select(p => p.Name).Should().Equal("tool");
    }

    [Fact]
    public void 清單的差異_有id以id配對_沒有以內容配對_同樣的出現兩次各配一次()
    {
        var a = new AgentBackgroundTool("執行指令", "bash", "npm run dev", null, StopId: "s1");
        var aRenamed = a with { Title = "npm run dev（第二次回報改了標題）" };
        var b = new AgentBackgroundTool("執行指令", "bash", "sleep 60", null);
        var b2 = b;
        var c = new AgentBackgroundTool("子代理", "agent", "研究", null);

        var (started, ended) = BackgroundJournalRules.Diff([a, b], [aRenamed, b, c]);
        started.Should().Equal(2);
        ended.Should().BeEmpty("標題變了但停止用的 id 一樣：同一個背景工作");

        (started, ended) = BackgroundJournalRules.Diff([b, b2, c], [b]);
        started.Should().BeEmpty();
        ended.Should().Equal(1, 2);

        (started, ended) = BackgroundJournalRules.Diff([], [a]);
        started.Should().Equal(0);
        ended.Should().BeEmpty();
    }

    [Theory]
    [InlineData("agent", true)]
    [InlineData("bash", false)]
    [InlineData(null, false)]
    public void 子代理是agent種類的工具卡(string? kind, bool expected) => BackgroundJournalRules.IsSubagentTool(kind).Should().Be(expected);

    [Fact]
    public void 背景程式結束_分類過的一律記_沒分類的跑了30秒以上才記()
    {
        BackgroundJournalRules.ShouldLogProgramExit("confirmed", TimeSpan.FromSeconds(1)).Should().BeTrue();
        BackgroundJournalRules.ShouldLogProgramExit("cliTask", null).Should().BeTrue();
        BackgroundJournalRules.ShouldLogProgramExit(null, TimeSpan.FromSeconds(29)).Should().BeFalse();
        BackgroundJournalRules.ShouldLogProgramExit(null, TimeSpan.FromSeconds(30)).Should().BeTrue();
        BackgroundJournalRules.ShouldLogProgramExit(null, null).Should().BeFalse();
    }

    [Fact]
    public void 檔名與保留_30天前的刪_不是日誌的檔名不動()
    {
        var today = new DateTime(2026, 10, 27, 15, 0, 0);
        BackgroundJournalRules.FileName(today).Should().Be("2026-10-27.jsonl");
        BackgroundJournalRules.RolledFileName(today).Should().Be("2026-10-27.1.jsonl");
        BackgroundJournalRules.IsExpired("2026-09-27.jsonl", today).Should().BeFalse("剛好 30 天");
        BackgroundJournalRules.IsExpired("2026-09-26.jsonl", today).Should().BeTrue();
        BackgroundJournalRules.IsExpired("2026-09-26.1.jsonl", today).Should().BeTrue();
        BackgroundJournalRules.IsExpired("notes.jsonl", today).Should().BeFalse();
        BackgroundJournalRules.IsExpired("2026-09-01.log", today).Should().BeFalse();
    }

    [Fact]
    public void 秒數取小數一位_負的當0()
    {
        BackgroundJournalRules.Seconds(TimeSpan.FromMilliseconds(12_345)).Should().Be(12.3);
        BackgroundJournalRules.Seconds(TimeSpan.FromSeconds(-3)).Should().Be(0);
        BackgroundJournalRules.Seconds(null).Should().BeNull();
    }
}
