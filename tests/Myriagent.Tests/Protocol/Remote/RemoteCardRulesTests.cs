using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Remote;

/// <summary>
/// 遠端回應卡片（WP4.1 步 6；閘門 5h、設計 14.11 D36、docs/sync-protocol.md 14.5）與問卷答案改成訊息時的文字（D20）
/// </summary>
public sealed class RemoteCardRulesTests
{
    /// <summary>測試用的假權杖（執行時拼起來，原始碼裡沒有完整的樣子）</summary>
    private static readonly string FakeToken = "sk-" + "ant-" + "api03-" + new string('Q', 40);

    private static AgentChatItem Approval(bool canAllowForSession = true) => new()
    {
        Id = "i1",
        Kind = AgentItemKind.Approval,
        Status = AgentItemStatus.Waiting,
        ToolName = "執行指令",
        Title = "npm test",
        Detail = "npm test --silent",
        CanAllowForSession = canAllowForSession,
        RequestKey = "req-1",
    };

    private static AgentChatItem Plan() => new()
    {
        Id = "i2",
        Kind = AgentItemKind.PlanApproval,
        Status = AgentItemStatus.Waiting,
        Text = "計畫",
        RequestKey = "plan-1",
    };

    private static AgentChatItem Question(params AgentQuestion[] questions)
    {
        var item = new AgentChatItem { Id = "i3", Kind = AgentItemKind.Question, Status = AgentItemStatus.Waiting, RequestKey = "q-1" };
        item.Questions.AddRange(questions);
        return item;
    }

    private static AgentQuestion Q(string id, string? text = null, bool secret = false) => new() { Id = id, Text = text ?? id, IsSecret = secret };

    private static JObject Cmd(string cmd, AgentChatItem card, params (string Key, JToken? Value)[] fields)
    {
        var command = RemoteSamples.Command(cmd, ("itemId", card.Id), ("requestDigest", RequestDigestRules.Compute(card)));
        foreach (var (key, value) in fields)
        {
            if (value == null)
                command.Remove(key);
            else
                command[key] = value;
        }

        return command;
    }

    [Fact]
    public void 卡片指令與種類()
    {
        RemoteCardRules.IsCardCommand("approve").Should().BeTrue();
        RemoteCardRules.IsCardCommand("approvePlan").Should().BeTrue();
        RemoteCardRules.IsCardCommand("answer").Should().BeTrue();
        RemoteCardRules.IsCardCommand("skipQuestion").Should().BeFalse("只帶 itemId，不比摘要");
        RemoteCardRules.IsCardCommand("send").Should().BeFalse();
        RemoteCardRules.IsCardCommand(null).Should().BeFalse();
        RemoteCardRules.CardKind("approve").Should().Be(AgentItemKind.Approval);
        RemoteCardRules.CardKind("approvePlan").Should().Be(AgentItemKind.PlanApproval);
        RemoteCardRules.CardKind("answer").Should().Be(AgentItemKind.Question);
        RemoteCardRules.CardKind("send").Should().BeNull();
        RemoteCommandCatalog.All.Where(s => s.IsCard).Should().OnlyContain(s => RemoteCardRules.CardKind(s.Name) != null);
    }

    [Fact]
    public void 摘要相符才通過_不是卡片指令的一律通過()
    {
        var card = Approval();
        RemoteCardRules.Check("approve", Cmd("approve", card, ("choice", "allow")), card).Should().BeNull();
        RemoteCardRules.Check("approvePlan", Cmd("approvePlan", Plan(), ("choice", "manual")), Plan()).Should().BeNull();
        RemoteCardRules.Check("send", RemoteSamples.Command("send"), null).Should().BeNull();
        RemoteCardRules.Check("skipQuestion", RemoteSamples.Command("skipQuestion"), null).Should().BeNull();
    }

    [Fact]
    public void 卡片不在_種類不對_不在等待_沒有RequestKey_摘要不符都是stale()
    {
        var command = Cmd("approve", Approval(), ("choice", "allow"));
        RemoteCardRules.Check("approve", command, null).Should().Be(RemoteResultCodes.Stale, "找不到卡片");

        var cases = new Dictionary<string, Action<AgentChatItem>>
        {
            ["已處理"] = c => c.Status = AgentItemStatus.Done,
            ["RequestKey 是 null"] = c => c.RequestKey = null,
            ["程序重開換了 RequestKey"] = c => c.RequestKey = "req-2",
            ["內容變了"] = c => c.Detail = "npm test --silent && rm -rf /",
            ["種類不對"] = c => c.Kind = AgentItemKind.PlanApproval,
        };
        foreach (var (name, change) in cases)
        {
            var card = Approval();
            change(card);
            RemoteCardRules.Check("approve", command, card).Should().Be(RemoteResultCodes.Stale, name);
        }

        var digestless = (JObject)command.DeepClone();
        digestless.Remove("requestDigest");
        RemoteCardRules.Check("approve", digestless, Approval()).Should().Be(RemoteResultCodes.Stale, "本機的 RejectStaleCard 在 null 時放行，遠端不放行");
        var plan = Plan();
        RemoteCardRules.Check("approvePlan", Cmd("approvePlan", Approval(), ("choice", "manual")), plan).Should().Be(RemoteResultCodes.Stale, "別張卡的摘要");
    }

    [Fact]
    public void 卡片不讓本對話都允許卻選了allowSession是invalid()
    {
        var card = Approval(canAllowForSession: false);
        RemoteCardRules.Check("approve", Cmd("approve", card, ("choice", "allowSession")), card).Should().Be(RemoteResultCodes.Invalid);
        RemoteCardRules.Check("approve", Cmd("approve", card, ("choice", "allow")), card).Should().BeNull();
        var allowed = Approval(canAllowForSession: true);
        RemoteCardRules.Check("approve", Cmd("approve", allowed, ("choice", "allowSession")), allowed).Should().BeNull();
    }

    [Fact]
    public void 答案對回原題()
    {
        var card = Question(Q("要怎麼彙總？"), Q("q2", "密碼？", secret: true));
        var answers = new JObject { ["要怎麼彙總？"] = new JArray("依日期", " ", "依月份"), ["q2"] = new JArray("hunter2") };
        RemoteCardRules.Check("answer", Cmd("answer", card, ("answers", answers)), card).Should().BeNull();
        RemoteCardRules.TryMapAnswers(card, answers, out var mapped).Should().BeTrue();
        mapped!.Keys.Should().BeEquivalentTo("要怎麼彙總？", "q2");
        mapped["要怎麼彙總？"].Should().Equal("依日期", "依月份");
    }

    [Fact]
    public void 題目id含權杖_線上看到的是遮蔽後的id_照樣對回原題()
    {
        var original = $"把 {FakeToken} 存在哪裡？";
        CredentialLeakRules.HasLeak(original).Should().BeTrue();
        var card = Question(Q(original), Q("q2"));
        var wire = CredentialLeakRules.MaskItem(card);
        var wireId = wire.Questions[0].Id!;
        wireId.Should().NotContain(FakeToken);

        RemoteCardRules.TryMapAnswers(card, new JObject { [wireId] = new JArray("vault") }, out var mapped).Should().BeTrue();
        mapped!.Should().ContainKey(original).WhoseValue.Should().Equal("vault");
        RemoteCardRules.TryMapAnswers(card, new JObject { [original] = new JArray("vault") }, out _).Should().BeFalse("線上不會有原文的 id");
    }

    [Fact]
    public void 兩題遮蔽後相同_分不出是哪一題_不收()
    {
        var a = $"{FakeToken} 甲？";
        var b = $"{FakeToken} 乙？";
        CredentialLeakRules.Mask(a).Should().NotBe(CredentialLeakRules.Mask(b), "權杖後面接的字不同");
        var c = "金鑰 " + FakeToken;
        var d = "金鑰 " + FakeToken.Replace('Q', 'R');
        CredentialLeakRules.Mask(c).Should().Be(CredentialLeakRules.Mask(d));
        var card = Question(Q(c), Q(d), Q("q3"));
        RemoteCardRules.TryMapAnswers(card, new JObject { [CredentialLeakRules.Mask(c)] = new JArray("x") }, out _).Should().BeFalse();
        RemoteCardRules.TryMapAnswers(card, new JObject { ["q3"] = new JArray("x") }, out var mapped).Should().BeTrue("其他題照常");
        mapped!.Keys.Should().Equal("q3");
    }

    [Fact]
    public void 答案對不回題目的都是invalid()
    {
        var card = Question(Q("q1"), Q("q2"));
        var bad = new Dictionary<string, JObject>
        {
            ["不認得的題目"] = new() { ["q9"] = new JArray("x") },
            ["沒有非空白的答案"] = new() { ["q1"] = new JArray(" ", "") },
            ["空物件"] = new(),
            ["值不是陣列"] = new() { ["q1"] = "x" },
            ["值不是字串"] = new() { ["q1"] = new JArray(1) },
            ["大小寫不同"] = new() { ["Q1"] = new JArray("x") },
        };
        foreach (var (name, answers) in bad)
        {
            RemoteCardRules.TryMapAnswers(card, answers, out var mapped).Should().BeFalse(name);
            mapped.Should().BeNull();
            RemoteCardRules.Check("answer", Cmd("answer", card, ("answers", answers)), card).Should().Be(RemoteResultCodes.Invalid, name);
        }

        RemoteCardRules.TryMapAnswers(card, null, out _).Should().BeFalse();
        var withNull = Question(Q("q1"));
        withNull.Questions.Add(null!);
        withNull.Questions.Add(new AgentQuestion { Id = null!, Text = "沒有 id" });
        RemoteCardRules.TryMapAnswers(withNull, new JObject { ["q1"] = new JArray("x") }, out _).Should().BeTrue("存檔裡的 null 題目與沒有 id 的題目略過");
    }

    [Fact]
    public void 改以訊息送出的答案_CLI收到原文_顯示把秘密換成已隱藏()
    {
        var questions = new List<AgentQuestion> { Q("q1", "要怎麼彙總？"), Q("q2", "密碼？", secret: true), Q("q3", "還有嗎？"), Q("q4", "第二個密碼？", secret: true) };
        var answers = new Dictionary<string, IReadOnlyList<string>>
        {
            ["q1"] = ["依日期", "依月份"],
            ["q2"] = ["hunter2"],
        };
        var (cli, display) = AnswerMessageRules.Compose(questions, answers);

        // 與現在的中樞（AgentHub.AnswerAsync 的 AnswerAsMessage）逐字相同的算法
        var hub = "我的回答：\n" + string.Join("\n", questions.Select(q => $"{q.Text}：{string.Join("、", answers.TryGetValue(q.Id!, out var v) ? v : [])}"));
        cli.Should().Be(hub);
        cli.Should().Be("我的回答：\n要怎麼彙總？：依日期、依月份\n密碼？：hunter2\n還有嗎？：\n第二個密碼？：");
        display.Should().Be("我的回答：\n要怎麼彙總？：依日期、依月份\n密碼？：（已隱藏）\n還有嗎？：\n第二個密碼？：", "沒回答的秘密題目不顯示「已隱藏」");
        display.Should().NotContain("hunter2");

        var withNull = new List<AgentQuestion> { null!, Q("q1", "要怎麼彙總？"), new() { Id = null!, Text = "沒有 id" } };
        AnswerMessageRules.Compose(withNull, answers).CliText.Should().Be("我的回答：\n要怎麼彙總？：依日期、依月份\n沒有 id：");
    }

    [Fact]
    public void 改以訊息送出的答案_中樞改用Compose()
    {
        // WP4.3 步 6（契約 R3）把中樞 AnswerAsync 自己組的那一段換成 Compose；CLI 收到的內容與舊的組法逐位元組相同，
        // 由 AgentHubSecretAnswerTests.一般題目_CLI文字與舊的組法逐位元組相同 證明
        var hub = File.ReadAllText(Path.Combine(TestSupport.MyriagentHubExe.RepoRoot(), "src", "Myriagent", "Hub", "AgentHub.cs"));
        // 2026-11-05 起傳的是併進補充說明的答案（QuestionNoteRules.Merge；沒有補充時和 answers 相同）
        hub.Should().Contain("AnswerMessageRules.Compose(item.Questions, shown)");
        hub.Should().NotContain("$\"{q.Text}：{string.Join(\"、\", ", "舊的組法（項目與佇列也用含秘密原文的文字）已換掉");
    }
}
