using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 代理執行中送出的訊息直接插進這一輪（IAgentSession.SteerAsync），插不進去才排進佇列等這一輪結束：
/// 插入成功的加成使用者訊息、不再另外送；按了停止不插；插入途中這一輪剛好結束時不送兩次；取消插入中的那則；多則依序插入
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubSteerTests
{
    private static List<string> UserTexts(HubHarness h, string id)
        => h.Items(id).Where(i => i.Kind == AgentItemKind.User).Select(i => i.Text).ToList();

    [Fact]
    public async Task 執行中送出_插進這一輪_加成使用者訊息並存檔_不排隊_這一輪結束後不再送()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        s.CanSteer = true;
        var header = await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("running"), conversationId: id);
        header["header"]!.Value<bool>("steer").Should().BeTrue("網頁據此把送出鈕寫成「插入這一輪」");

        var mark = h.Main.Mark();
        h.Send(h.Main, id, "順便把圖表改成長條圖");

        s.Steered.All.Select(i => i.Text).Should().Equal("順便把圖表改成長條圖");
        h.Main.Messages("composer", mark, id).Should().OnlyContain(m => !m["composer"]!["queue"]!.Any(), "插進去了就不在佇列出現（不閃一下）");
        h.Queue(id).Should().BeEmpty("插進去了就不用等這一輪結束");
        UserTexts(h, id).Should().Equal("整理報表", "順便把圖表改成長條圖");
        h.IsRunning(id).Should().BeTrue("還是同一輪");
        h.Store.LoadItems(id).Where(i => i.Kind == AgentItemKind.User).Select(i => i.Text).Should().Contain("順便把圖表改成長條圖");

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(1, "已經插進那一輪，不能再當成下一則送出");
        h.IsRunning(id).Should().BeFalse();
    }

    [Fact]
    public void 插不進去_留在佇列_這一輪正常完成後照常送出()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "整理報表");
        s.CanSteer = false;

        h.Send(h.Main, id, "下一則");

        s.SteerAttempts.Should().Be(1);
        h.Queue(id).Should().Equal("下一則");
        UserTexts(h, id).Should().Equal("整理報表");

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("下一則");
        h.Queue(id).Should().BeEmpty();
    }

    [Fact]
    public void 按了停止之後送出_不插入_排進佇列跟著暫停()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        s.CanSteer = true;
        h.Command(h.Main, "stop", new { conversationId = id });

        h.Send(h.Main, id, "停止後送的");

        s.SteerAttempts.Should().Be(0, "使用者要停掉這一輪，不能再把訊息插進去");
        h.Queue(id).Should().Equal("停止後送的");
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);
        s.Sent.Count.Should().Be(1, "停止後排隊的訊息不自動送出");
        h.Items(id).Last().Text.Should().Be("已停止這一輪（排隊的訊息暫停，按送出繼續）");
    }

    [Fact]
    public async Task 插入途中這一輪正常結束_沒插進去就照常送出_只送一次()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "整理報表");
        s.CanSteer = true;
        s.SteerGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var mark = h.Main.Mark();
        h.Send(h.Main, id, "補充");
        await h.Main.WaitForAsync("composer", m => m["composer"]!.Value<string>("steering") != null, mark, conversationId: id);   // 等久了才顯示「正在插入這一輪」
        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(1, "插入中的那則可能已經送到代理：等插入有了結果再決定，不能先當下一則送出");
        h.Queue(id).Should().Equal("補充");

        s.SteerGate.SetResult(false);   // codex 回「no active turn to steer」
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "沒插進去：照常當下一則送出");

        s.Sent.All[1].Input.Text.Should().Be("補充");
        h.Queue(id).Should().BeEmpty();
        UserTexts(h, id).Should().Equal("整理報表", "補充");
    }

    [Fact]
    public async Task 插入途中這一輪正常結束_其實插進去了_加成使用者訊息_不再送_照常通知完成()
    {
        using var h = new HubHarness();
        h.Host.IsForeground = false;
        var (id, s) = h.StartConversation(h.Main, h.Codex, "整理報表");
        s.CanSteer = true;
        s.SteerGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        h.Send(h.Main, id, "補充");
        h.CompleteTurn(s);
        h.Host.Notifications.Count.Should().Be(0, "還不知道插入的那則要不要接著送");

        s.SteerGate.SetResult(true);
        await h.WaitUntilAsync(() => h.Queue(id).Count == 0, "插入有了結果");
        await h.Ui.DrainAsync();

        s.Sent.Count.Should().Be(1);
        UserTexts(h, id).Should().Equal("整理報表", "補充");
        h.Host.Notifications.All.Should().ContainSingle(n => n.Title.StartsWith("代理已完成"));
    }

    [Fact]
    public async Task 插入途中這一輪失敗_佇列暫停_空白送出不把插入中的那則再送一次()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        s.CanSteer = true;
        s.SteerGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        h.Send(h.Main, id, "補充");
        h.CompleteTurn(s, AgentTurnOutcome.Failed, "連線中斷");
        h.Send(h.Main, id, "");   // 佇列暫停：空白送出＝從第一則接著送

        s.Sent.Count.Should().Be(1, "第一則還在插入（可能已經送到代理），不能當成下一則再送");
        h.Queue(id).Should().Equal("補充");

        s.SteerGate.SetResult(true);
        await h.WaitUntilAsync(() => h.Queue(id).Count == 0, "插入有了結果");

        s.Sent.Count.Should().Be(1);
        UserTexts(h, id).Should().Equal("整理報表", "補充");
    }

    [Fact]
    public async Task 取消正在插入的那則_取消等待_移出佇列_附件還回輸入區()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        s.CanSteer = true;
        s.SteerGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var file = h.Temp.WriteFile(@"in\附件.txt", "a");
        h.Command(h.Main, "addFiles", new { conversationId = id }, new[] { file });
        await h.Main.WaitForAsync("composer", m => m["composer"]!["attachments"]!.Count() == 1, conversationId: id);

        var mark = h.Main.Mark();
        h.Send(h.Main, id, "附上檔案");
        var steering = await h.Main.WaitForAsync("composer", m => m["composer"]!.Value<string>("steering") != null, mark, conversationId: id);
        var queued = steering["composer"]!["queue"]!.Single().Value<string>("id");
        queued.Should().Be(steering["composer"]!.Value<string>("steering"));
        h.Command(h.Main, "cancelQueued", new { conversationId = id, id = queued });
        await h.Ui.DrainAsync();

        h.Queue(id).Should().BeEmpty();
        s.Steered.Count.Should().Be(0, "插入前的等待被取消，沒有送到代理");
        UserTexts(h, id).Should().Equal("整理報表");
        h.Main.Last("composer", id)!["composer"]!["attachments"]!.Should().ContainSingle("附件還回輸入區");
        h.Main.Last("composer", id)!["composer"]!.Value<string>("steering").Should().BeNull();
    }

    [Fact]
    public async Task 插入中又送一則_等前一則插完依序接著插()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        s.CanSteer = true;
        s.SteerGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        h.Send(h.Main, id, "第一則補充");
        h.Send(h.Main, id, "第二則補充");
        s.SteerAttempts.Should().Be(1, "同一個對話同時只插一則，後到的排在後面");
        h.Queue(id).Should().Equal("第一則補充", "第二則補充");

        s.SteerGate.SetResult(true);
        await h.WaitUntilAsync(() => h.Queue(id).Count == 0, "依序插完");

        s.Steered.All.Select(i => i.Text).Should().Equal("第一則補充", "第二則補充");
        UserTexts(h, id).Should().Equal("整理報表", "第一則補充", "第二則補充");
    }

    [Fact]
    public void 非同步提問_停下這一輪途中回答_不插進這一輪_停下後當下一則訊息送出_卡片是已回答()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "先問一題");
        s.CanSteer = true;
        h.Raise(s, new AgentQuestionEvent("async:m1", new[] { new AgentQuestion { Id = "m1:0", Text = "要哪種格式？", Options = { new AgentQuestionOption { Label = "Excel" } } } }, AnswerAsMessage: true));
        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);
        s.Interrupts.Should().Be(1, "Codex 的非同步提問：停下這一輪等回答");

        h.Command(h.Main, "answer", new { conversationId = id, itemId = question.Id, answers = new Dictionary<string, string[]> { ["m1:0"] = new[] { "Excel" } } });

        s.Steered.All.Should().BeEmpty("這一輪正在停下，插進去會隨著停下一起丟掉");
        h.Queue(id).Should().Equal("我的回答：\n要哪種格式？：Excel");
        question.Status.Should().Be(AgentItemStatus.Done);
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);
        s.Sent.Count.Should().Be(2, "停下後當下一則訊息送出");
        s.Sent.All[1].Input.Text.Should().Be("我的回答：\n要哪種格式？：Excel");
        h.Queue(id).Should().BeEmpty();
    }

    [Fact]
    public void 非同步提問_停下途中回答秘密題目_停下後送出_代理收原文_使用者訊息與存檔只有遮蔽後的文字()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "先問一題");
        s.CanSteer = true;
        h.Raise(s, new AgentQuestionEvent("async:m2", new[] { new AgentQuestion { Id = "m2:0", Text = "資料庫密碼？", IsSecret = true } }, AnswerAsMessage: true));
        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);

        h.Command(h.Main, "answer", new { conversationId = id, itemId = question.Id, answers = new Dictionary<string, string[]> { ["m2:0"] = new[] { "s3cret-pw" } } });
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        s.Sent.All[1].Input.Text.Should().Contain("s3cret-pw", "代理要收到真正的答案");
        var user = h.Items(id).Last(i => i.Kind == AgentItemKind.User);
        user.Text.Should().NotContain("s3cret-pw").And.Contain("（已隱藏）", "畫面、存檔與同步只看遮蔽後的文字（D20）");
        h.Store.LoadItems(id).Should().NotContain(i => i.Text != null && i.Text.Contains("s3cret-pw"));
    }
}
