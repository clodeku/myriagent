using System.Security.Cryptography;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 遠端的 answer（WP4.4 計畫步 11；D21）：題目 id 先以 RemoteCardRules.TryMapAnswers 對回原題（線上的 id 經過權杖遮蔽），
/// 再呼叫本機的 AnswerAsync——Claude 的問卷直接回 CLI；當成訊息送出的回答（Codex 非同步提問）是代理輸入，入列與送出都帶這個指令的來源，
/// 秘密答案在畫面、存檔與佇列快照照樣遮蔽、CLI 收原文。同步的前段結束後卡片不再等待、Decision 是 answered 才回 done，否則 stale；
/// 沒有任何對得回的答案回 invalid。這台的政策是 Control、不允許遠端完全自動
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteAnswerTests
{
    private const string Hidden = "（已隱藏）";

    /// <summary>執行時拼出來的假權杖（repo 裡沒有完整的字串）</summary>
    private static readonly string FakeToken = "sk-" + "ant-" + "api03-" + new string('Q', 40);

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>執行時產生的秘密答案（repo 裡沒有固定的值）</summary>
    private static string NewSecret() => "答案-" + Guid.NewGuid().ToString("N")[..16];

    /// <summary>執行中的 Claude 對話，畫出等待中的問卷（題目 id 就是題目文字）</summary>
    private static (string Id, FakeSession Session, AgentChatItem Card) ClaudeAsked(HubHarness h, string questionId = "要怎麼彙總？", string? instance = null)
    {
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        if (instance != null)
            h.ProcessStarted(s, instance, null);
        h.Raise(s, new AgentQuestionEvent("q-1", [new AgentQuestion { Id = questionId, Text = questionId }], AnswerAsMessage: false));
        return (id, s, h.Items(id).Single(i => i.Kind == AgentItemKind.Question));
    }

    /// <summary>執行中的 Codex 對話，畫出當成訊息送出的非同步提問（第二題是秘密）</summary>
    private static (string Id, FakeSession Session, AgentChatItem Card) CodexAsked(HubHarness h)
    {
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Codex, "先問兩題");
        h.Raise(s, new AgentQuestionEvent("async:m1", [
            new AgentQuestion { Id = "user", Text = "帳號" },
            new AgentQuestion { Id = "pwd", Text = "密碼", IsSecret = true },
        ], AnswerAsMessage: true));
        return (id, s, h.Items(id).Single(i => i.Kind == AgentItemKind.Question));
    }

    private static Task<RemoteOutcome> RemoteAnswerAsync(HubHarness h, string conversationId, AgentChatItem card, object answers, string? cid = null)
        => RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("answer", conversationId,
            new { itemId = card.Id, answers, requestDigest = card.RequestDigest }), cid: cid));

    private static AgentChatItem Card(HubHarness h, string conversationId, string itemId) => h.Items(conversationId).Single(i => i.Id == itemId);

    private static void ViewHasNo(FakeView view, string secret, int since)
        => view.All().Skip(since).Select(m => m.ToString()).Should().NotContain(m => m.Contains(secret, StringComparison.Ordinal), "畫面只看遮蔽後的文字");

    [Fact]
    public async Task 遠端answer_Claude的問卷_CLI收到答案_卡片answered_done()
    {
        using var h = new HubHarness();
        var (id, s, card) = ClaudeAsked(h);

        var outcome = await RemoteAnswerAsync(h, id, card, new Dictionary<string, string[]> { ["要怎麼彙總？"] = ["依日期", "依月份"] });

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var answered = Card(h, id, card.Id);
        (answered.Status, answered.Decision).Should().Be((AgentItemStatus.Done, "answered"));
        answered.Answers["要怎麼彙總？"].Should().Equal("依日期", "依月份");
        var (key, answers) = s.Answers.All.Should().ContainSingle().Subject;
        key.Should().Be("q-1");
        answers.Keys.Should().Equal("要怎麼彙總？");
        answers["要怎麼彙總？"].Should().Equal("依日期", "依月份");
        s.Sent.Count.Should().Be(1, "Claude 的問卷直接回 CLI，不當成訊息");
    }

    /// <summary>每題的補充說明（2026-11-05）：本機與遠端都交給會話（Claude 放進 annotations.notes），畫面與存檔多一筆「補充說明：…」</summary>
    [Fact]
    public async Task answer帶補充說明_本機與遠端都交給會話_畫面多一筆補充()
    {
        using var h = new HubHarness();
        var (id, s, card) = ClaudeAsked(h);

        h.Command(h.Main, "answer", new { conversationId = id, itemId = card.Id, answers = new Dictionary<string, string[]> { ["要怎麼彙總？"] = ["依日期"] },
            notes = new Dictionary<string, string[]> { ["要怎麼彙總？"] = ["  只要上個月  "] } });
        await h.WaitUntilAsync(() => s.Answers.Count == 1, "本機作答");
        s.Answers.All.Single().Answers["要怎麼彙總？"].Should().Equal(["依日期"], "答案本身不混進補充");
        s.AnswerNotes.All.Single().Should().BeEquivalentTo(new Dictionary<string, string> { ["要怎麼彙總？"] = "只要上個月" });
        Card(h, id, card.Id).Answers["要怎麼彙總？"].Should().Equal("依日期", QuestionNoteRules.DisplayPrefix + "只要上個月");

        using var h2 = new HubHarness();
        var (id2, s2, card2) = ClaudeAsked(h2);
        var outcome = await RemoteEntrySamples.RunAsync(h2, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("answer", id2,
            new { itemId = card2.Id, answers = new Dictionary<string, string[]> { ["要怎麼彙總？"] = ["依月份"] },
                notes = new Dictionary<string, string[]> { ["要怎麼彙總？"] = ["含退貨"] }, requestDigest = card2.RequestDigest })));
        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        s2.AnswerNotes.All.Single().Should().BeEquivalentTo(new Dictionary<string, string> { ["要怎麼彙總？"] = "含退貨" });

        // 補充對不回題目：整個 answer 不收
        using var h3 = new HubHarness();
        var (id3, s3, card3) = ClaudeAsked(h3);
        var bad = await RemoteEntrySamples.RunAsync(h3, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("answer", id3,
            new { itemId = card3.Id, answers = new Dictionary<string, string[]> { ["要怎麼彙總？"] = ["依月份"] },
                notes = new Dictionary<string, string[]> { ["不是這題"] = ["x"] }, requestDigest = card3.RequestDigest })));
        bad.State.Should().NotBe(RemoteResultStates.Done);
        s3.Answers.Count.Should().Be(0);
    }

    [Fact]
    public async Task answer帶補充說明_當成訊息送出時併進訊息_密碼題不收補充()
    {
        using var h = new HubHarness();
        var (id, s, card) = CodexAsked(h);
        h.CompleteTurn(s);
        var before = s.Sent.Count;

        h.Command(h.Main, "answer", new { conversationId = id, itemId = card.Id, answers = new Dictionary<string, string[]> { ["user"] = ["alice"], ["pwd"] = ["p@ss"] },
            notes = new Dictionary<string, string[]> { ["user"] = ["是測試帳號"], ["pwd"] = ["不該出現"] } });
        await h.WaitUntilAsync(() => s.Sent.Count == before + 1, "答案當成下一則訊息送出");

        var text = s.Sent.All[^1].Input.Text;
        text.Should().Contain("帳號：alice、" + QuestionNoteRules.DisplayPrefix + "是測試帳號");
        text.Should().NotContain("不該出現");
    }

    [Fact]
    public async Task 遠端answer_遮蔽過的題目id對回原題_原文的id對不到()
    {
        using var h = new HubHarness();
        var original = $"把 {FakeToken} 存在哪裡？";
        var (id, s, card) = ClaudeAsked(h, original);
        var wire = CredentialLeakRules.Mask(original)!;
        wire.Should().NotBe(original, "前提：題目文字含權杖，線上看到的是遮蔽後的 id");
        wire.Should().NotContain(FakeToken);

        // 對照：拿原文的 id 回答（線上不會有）對不到任何題目
        var unmasked = await RemoteAnswerAsync(h, id, card, new Dictionary<string, string[]> { [original] = ["保險箱"] });
        RemoteEntrySamples.ShouldBeRejected(unmasked, RemoteResultCodes.Invalid, RemoteGateSteps.Card);
        s.Answers.Count.Should().Be(0);

        var outcome = await RemoteAnswerAsync(h, id, card, new Dictionary<string, string[]> { [wire] = ["保險箱"] });

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var answers = s.Answers.All.Should().ContainSingle().Subject.Answers;
        answers.Keys.Should().Equal(original);
        answers[original].Should().Equal("保險箱");
        Card(h, id, card.Id).Decision.Should().Be("answered");
    }

    [Fact]
    public async Task 遠端answer_當成訊息執行中_入列帶來源_秘密答案畫面遮蔽_CLI收原文()
    {
        using var h = new HubHarness();
        var (id, s, card) = CodexAsked(h);
        var secret = NewSecret();
        var cli = "我的回答：\n帳號：alice\n密碼：" + secret;
        var display = "我的回答：\n帳號：alice\n密碼：" + Hidden;
        var cid = RemoteEntrySamples.NewCid();
        var mark = h.Main.Mark();

        var outcome = await RemoteAnswerAsync(h, id, card, new Dictionary<string, string[]> { ["user"] = ["alice"], ["pwd"] = [secret] }, cid);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var origin = new RemoteOrigin(cid, h.Conversation(id)!.PermissionMode, outcome.Tier, outcome.FullAccess);
        var answered = Card(h, id, card.Id);
        (answered.Status, answered.Decision).Should().Be((AgentItemStatus.Done, "answered"));
        answered.Answers["pwd"].Should().Equal(Hidden);
        s.Sent.Count.Should().Be(1, "這一輪還在執行：排進佇列");
        var entry = h.OnUi(() => h.Hub.QueueEntriesForTest(id)).Single();
        (entry.Id, entry.Text, entry.Origin, entry.Held).Should().Be(("r" + cid, cli, origin, false), "回答是代理輸入：入列帶這個指令的來源");
        var saved = QueueStore.Load(h.Store.GetConversationDirectory(id))!.Queue.Single();
        (saved.DisplayText, saved.RemoteOrigin).Should().Be((display, origin));

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(2, "模式沒變：這一輪結束後照常送出");
        s.Sent.All[1].Input.Text.Should().Be(cli, "CLI 收原文");
        h.Items(id).Last(i => i.Kind == AgentItemKind.User).Text.Should().Be(display);
        ViewHasNo(h.Main, secret, mark);
    }

    [Fact]
    public async Task 遠端answer_當成訊息執行中_入列後模式調高就留下()
    {
        using var h = new HubHarness();
        var (id, s, card) = CodexAsked(h);
        var cid = RemoteEntrySamples.NewCid();

        var outcome = await RemoteAnswerAsync(h, id, card, new Dictionary<string, string[]> { ["user"] = ["alice"] }, cid);
        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);

        // 入列之後本機把模式調高：取出時照來源重查，留在佇列（結果早已是 done）
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "FullAccess" });
        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(1);
        var held = h.OnUi(() => h.Hub.QueueEntriesForTest(id)).Single();
        (held.Id, held.Held).Should().Be(("r" + cid, true));
        held.Origin!.CommandId.Should().Be(cid);
    }

    [Fact]
    public async Task 遠端answer_當成訊息閒置時直接送出_不寫這台的持久設定()
    {
        using var h = new HubHarness();
        var (id, s, card) = CodexAsked(h);
        h.CompleteTurn(s);
        h.Ui.Invoke(() => h.Settings.Save());
        var saved = Sha256(AppPaths.SettingsFile);
        var recent = h.OnUi(() => h.Settings.RecentFolders.ToList());

        var outcome = await RemoteAnswerAsync(h, id, card, new Dictionary<string, string[]> { ["user"] = ["alice"], ["pwd"] = ["p"] });

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        s.Sent.Count.Should().Be(2, "沒在執行：直接當下一則訊息送出");
        s.Sent.All[1].Input.Text.Should().Be("我的回答：\n帳號：alice\n密碼：p");
        h.Items(id).Last(i => i.Kind == AgentItemKind.User).Text.Should().Be("我的回答：\n帳號：alice\n密碼：" + Hidden);
        h.Queue(id).Should().BeEmpty();
        h.OnUi(() => h.Settings.RecentFolders.ToList()).Should().Equal(recent);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.Ui.DrainAsync();
        Sha256(AppPaths.SettingsFile).Should().Be(saved, "遠端不寫這台的持久設定（D23）");
    }

    [Fact]
    public async Task 遠端answer_當成訊息閒置時_CLI更新中延後_延後的那則帶來源()
    {
        using var h = new HubHarness();
        var (id, s, card) = CodexAsked(h);
        h.CompleteTurn(s);
        h.Ui.Invoke(() => h.Cli.Update(AgentProvider.Codex, state => state with { InstallChanging = true }));
        var cid = RemoteEntrySamples.NewCid();

        var outcome = await RemoteAnswerAsync(h, id, card, new Dictionary<string, string[]> { ["user"] = ["alice"] }, cid);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        s.Sent.Count.Should().Be(1, "CLI 正在更新：延後");
        var entry = h.OnUi(() => h.Hub.QueueEntriesForTest(id)).Single();
        (entry.Id, entry.Origin).Should().Be(("r" + cid, new RemoteOrigin(cid, h.Conversation(id)!.PermissionMode, outcome.Tier, outcome.FullAccess)),
            "閒置時直接送出的回答也帶這個指令的來源（延後時看得到）");

        h.Ui.Invoke(() => h.Cli.Update(AgentProvider.Codex, state => state with { InstallChanging = false }));
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "更新完成後送出");
        s.Sent.All[1].Input.Text.Should().Be("我的回答：\n帳號：alice\n密碼：");
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"不存在的題目":["x"]}""")]
    [InlineData("""{"user":[" ",""]}""")]
    [InlineData("""{"user":["alice"],"不存在的題目":["x"]}""")]
    public async Task 遠端answer_沒有任何對得回的答案_閘門回invalid_卡片不動(string answersJson)
    {
        using var h = new HubHarness();
        var (id, s, card) = CodexAsked(h);

        var outcome = await RemoteAnswerAsync(h, id, card, Newtonsoft.Json.Linq.JObject.Parse(answersJson));

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Invalid, RemoteGateSteps.Card);
        Card(h, id, card.Id).Status.Should().Be(AgentItemStatus.Waiting);
        h.Queue(id).Should().BeEmpty();
        s.Sent.Count.Should().Be(1);
    }

    [Fact]
    public async Task 處理者_沒有任何對得回的答案_invalid_沒授權也是invalid_卡片不動()
    {
        using var h = new HubHarness();
        var (id, s, card) = CodexAsked(h);
        var handlers = h.OnUi(() => h.Hub.RemoteHandlersForTest);

        // 閘門之外直接交給處理者（5h 已擋的情況，處理者自己也不送出）
        foreach (var (answers, authorized) in new[] { ("""{}""", true), ("""{"不存在的題目":["x"]}""", true), ("""{"user":["alice"]}""", false) })
        {
            var context = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, id, 1, 1, false, null, 2_000, false, RemoteEntrySamples.IssuerLabel);
            if (authorized)
                context.MarkAuthorized();
            var command = RemoteEntrySamples.Cmd("answer", id, new { itemId = card.Id, answers = Newtonsoft.Json.Linq.JObject.Parse(answers), requestDigest = card.RequestDigest });
            h.OnUi(() => handlers.Any(handler => handler.TryHandle(context, h.Main, "answer", command))).Should().BeTrue("answer 是這一包的指令");
            RemoteEntrySamples.ShouldBeRejected(await context.Completion.WaitAsync(TimeSpan.FromSeconds(5)), RemoteResultCodes.Invalid, RemoteGateSteps.Handler);
        }

        Card(h, id, card.Id).Status.Should().Be(AgentItemStatus.Waiting);
        h.Queue(id).Should().BeEmpty();
        s.Sent.Count.Should().Be(1);
    }

    [Fact]
    public async Task 遠端answer_卡片已經在本機回答_stale()
    {
        using var h = new HubHarness();
        var (id, s, card) = ClaudeAsked(h);
        h.Command(h.Main, "answer", new { conversationId = id, itemId = card.Id, answers = new Dictionary<string, string[]> { ["要怎麼彙總？"] = ["依月份"] } });

        var outcome = await RemoteAnswerAsync(h, id, card, new Dictionary<string, string[]> { ["要怎麼彙總？"] = ["依日期"] });

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Stale, RemoteGateSteps.Card);
        Card(h, id, card.Id).Answers["要怎麼彙總？"].Should().Equal("依月份");
        s.Answers.Count.Should().Be(1);
    }

    [Fact]
    public async Task 遠端answer_RejectStaleCard作廢_stale()
    {
        using var h = new HubHarness();
        var (id, s, card) = ClaudeAsked(h, instance: "p1");
        // 產生卡片的程序已不是事件流上的目前程序：AnswerAsync 的 RejectStaleCard 作廢、不轉送
        h.Ui.Invoke(() => card.ProcessInstance = "p-old");

        var outcome = await RemoteAnswerAsync(h, id, card, new Dictionary<string, string[]> { ["要怎麼彙總？"] = ["依日期"] });

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Stale, RemoteGateSteps.Handler);
        Card(h, id, card.Id).Status.Should().Be(AgentItemStatus.Cancelled);
        s.Answers.Count.Should().Be(0);
    }
}
