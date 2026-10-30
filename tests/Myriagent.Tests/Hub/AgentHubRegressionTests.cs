using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 審查找出的中樞問題（hub-core）的回歸測試：未讀與通知、讀不到的紀錄檔、送出與佇列的交錯、非同步提問、執行中切換權限模式、
/// Codex 拒絕附的說明、恢復預設工作區、登入提示、工具輸出截斷、背景說明代換。
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRegressionTests
{
    private static JToken? Summary(JObject conversations, string id)
        => conversations["items"]!.FirstOrDefault(i => i.Value<string>("id") == id);

    private static bool? IsUnread(JObject conversations, string id) => Summary(conversations, id)?.Value<bool>("unread");

    /// <summary>等合併送出（約 50 ms）做完，回傳最後一份對話清單</summary>
    private static async Task<JObject> LatestListAsync(HubHarness h)
    {
        await Task.Delay(150);
        await h.Ui.DrainAsync();
        return h.Main.Last("conversations")!;
    }

    /// <summary>同一個 UI 工作裡依序處理多個網頁指令（模擬連按兩下：第二個指令在第一個的背景檢查回來之前就處理）</summary>
    private static void CommandsInOneTick(HubHarness h, params JObject[] commands)
        => h.Ui.Invoke(() =>
        {
            foreach (var command in commands)
                h.Hub.HandleCommand(h.Main, command);
        });

    // ───── 未讀與通知（hub-core#1） ─────

    [Fact]
    public async Task 未讀_前景時藏在其他分頁後面的對話完成_標未讀並通知_切過去看到才清掉()
    {
        using var h = new HubHarness();
        var (a, sa) = h.StartConversation(h.Main, h.Claude, "A 的工作");
        var (b, sb) = h.StartConversation(h.Main, h.Claude, "B 的工作");
        // 同一個窗格開著兩個分頁，使用者正在看 A（網頁切分頁時以 visible 回報畫面上的對話）
        h.Command(h.Main, "visible", new { conversationIds = new[] { a } });

        h.CompleteTurn(sb);

        await h.Main.WaitForAsync("conversations", m => IsUnread(m, b) == true);
        h.Host.Notifications.All.Should().ContainSingle(n => n.ConversationId == b, "B 的分頁藏在後面，使用者看不到它完成");

        h.CompleteTurn(sa);
        IsUnread(await LatestListAsync(h), a).Should().BeFalse("正在看的對話完成不算未讀");
        h.Host.Notifications.All.Should().NotContain(n => n.ConversationId == a, "正在看的對話完成不打擾");

        var mark = h.Main.Mark();
        h.Command(h.Main, "visible", new { conversationIds = new[] { b } });

        await h.Main.WaitForAsync("conversations", m => IsUnread(m, b) == false, mark);
    }

    [Fact]
    public async Task 未讀_背景時完成的對話_回到視窗看到後清掉_之後切到別的分頁也不會再亮()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        h.Host.IsForeground = false;

        h.CompleteTurn(s);

        await h.Main.WaitForAsync("conversations", m => IsUnread(m, id) == true);
        h.Host.Notifications.All.Should().ContainSingle();

        // 視窗還在背景時網頁重送（例如別的視窗關掉了分頁）：使用者還沒看到，不清
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        IsUnread(await LatestListAsync(h), id).Should().BeTrue();

        // 回到視窗：網頁在視窗取得焦點時重送畫面上的對話
        h.Host.IsForeground = true;
        var mark = h.Main.Mark();
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        await h.Main.WaitForAsync("conversations", m => IsUnread(m, id) == false, mark);

        h.Command(h.Main, "visible", new { conversationIds = Array.Empty<string>() });
        IsUnread(await LatestListAsync(h), id).Should().BeFalse("看過的對話切到別的分頁後不會又變成未讀");
    }

    // ───── 讀不到的紀錄檔（hub-core#2） ─────

    [Fact]
    public void 讀不到的ItemsJson_送出被退回_讀得到後保留期間加的提示並照常續接()
    {
        AgentConversation? meta = null;
        string? itemsPath = null;
        using var h = new HubHarness(store =>
        {
            meta = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "被占用的對話", SessionId = Guid.NewGuid().ToString(), SessionStarted = true };
            store.SaveConversation(meta);
            store.SaveItems(meta.Id, new[] { new AgentChatItem { Kind = AgentItemKind.User, Text = "原本的內容" } });
            itemsPath = Path.Combine(store.GetConversationDirectory(meta.Id), "items.json");
        });

        using (new FileStream(itemsPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            h.Command(h.Main, "open", new { conversationId = meta!.Id });
            var mark = h.Main.Mark();

            h.Send(h.Main, meta.Id, "新的問題");

            h.Sessions.Should().BeEmpty("紀錄檔讀不到時不能在暫時的空清單上開始這一輪（之後讀得到時這一輪會被檔案內容整個蓋掉）");
            h.Main.Messages("sendRejected", mark, meta.Id).Single().Value<string>("text").Should().Be("新的問題", "退回原文");
            h.Main.Messages("toast", mark).Select(t => t.Value<string>("text")).Should().ContainSingle()
                .Which.Should().Contain("暫時讀不到").And.Contain("沒有送出");

            // 讀不到的期間換帳戶：提示先加在暫時的清單上
            h.Command(h.Main, "setAccount", new { conversationId = meta.Id, accountId = h.Claude2.Id });
        }

        h.Send(h.Main, meta.Id, "新的問題");

        h.Sessions.Single().Sent.All.Single().Input.Text.Should().EndWith("新的問題");
        var items = h.Items(meta.Id);
        items.Select(i => i.Kind).Should().Equal(AgentItemKind.User, AgentItemKind.Notice, AgentItemKind.User);
        items[0].Text.Should().Be("原本的內容");
        items[1].Text.Should().Contain("改用「Claude 副帳戶」", "讀不到的期間加的項目接在載入的內容後面，不能被丟掉");
        items[2].Text.Should().Be("新的問題");
        var saved = new ConversationStore(h.Store.RootDirectory).TryLoadItems(meta.Id);
        saved.Status.Should().Be(JsonReadStatus.Ok);
        saved.Items.Select(i => i.Text).Should().Equal(items.Select(i => i.Text));
    }

    // ───── 送出與佇列的交錯（hub-core#3、#4） ─────

    [Fact]
    public async Task 自訂工作資料夾_檢查資料夾期間連按兩次送出_附件只送一次不會多排一則()
    {
        using var h = new HubHarness();
        var folder = h.Temp.CreateDirectory("proj");
        var id = h.NewConversation(h.Main, h.Claude);
        await h.SetFolderAsync(h.Main, id, folder);
        var file = h.Temp.WriteFile(@"in\資料.csv", "a,b");
        var mark = h.Main.Mark();
        h.Command(h.Main, "addFiles", new { conversationId = id }, new[] { file });
        await h.Main.WaitForAsync("composer", m => m["composer"]!["attachments"]!.Count() == 1, mark, conversationId: id);

        // 網頁第二下送出時附件晶片還在（輸入框已清空），送 text=""；兩則都要在背景檢查資料夾
        CommandsInOneTick(h,
            HubHarness.MakeCommand("send", new { conversationId = id, text = "請整理這份資料" }),
            HubHarness.MakeCommand("send", new { conversationId = id, text = "" }));

        h.Main.Last("composer", id)!["composer"]!["attachments"]!.Should().BeEmpty("送出時附件就離開輸入區，等待期間不能再被送一次或被按 x 刪掉");
        await h.WaitUntilAsync(() => h.Sessions.Count == 1 && h.Sessions[0].Sent.Count == 1, "第一則在背景確認資料夾後送出");
        await Task.Delay(200);
        await h.Ui.DrainAsync();
        h.Queue(id).Should().BeEmpty("同一個附件不能再排成一則只有附件的訊息");
        var s = h.Sessions.Single();
        s.Sent.All.Single().Input.Text.Should().Be("請整理這份資料");
        s.Sent.All.Single().Input.Attachments.Should().ContainSingle();

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(1, "這一輪完成後不會把同一個附件再送一次");
    }

    [Fact]
    public async Task 自訂工作資料夾_送出前發現資料夾不見了_附件還回輸入區()
    {
        using var h = new HubHarness();
        var folder = h.Temp.CreateDirectory("proj");
        var id = h.NewConversation(h.Main, h.Claude);
        await h.SetFolderAsync(h.Main, id, folder);
        var file = h.Temp.WriteFile(@"in\資料.csv", "a,b");
        var mark = h.Main.Mark();
        h.Command(h.Main, "addFiles", new { conversationId = id }, new[] { file });
        await h.Main.WaitForAsync("composer", m => m["composer"]!["attachments"]!.Count() == 1, mark, conversationId: id);
        Directory.Delete(folder);
        mark = h.Main.Mark();

        h.Send(h.Main, id, "請整理這份資料");

        (await h.Main.WaitForAsync("sendRejected", since: mark, conversationId: id)).Value<string>("text").Should().Be("請整理這份資料");
        h.Main.Last("composer", id)!["composer"]!["attachments"]!.Should().ContainSingle("沒送出的附件要還回輸入區");
        h.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task 佇列暫停時空白續送_檢查資料夾期間另一則先開始執行_第一則留在佇列不會遺失()
    {
        using var h = new HubHarness();
        var folder = h.Temp.CreateDirectory("proj");
        var id = h.NewConversation(h.Main, h.Codex);
        await h.SetFolderAsync(h.Main, id, folder);
        h.Send(h.Main, id, "第一則");
        await h.WaitUntilAsync(() => h.Sessions.Count == 1, "送出第一則");
        var s = h.Sessions.Single();
        h.Raise(s, new AgentSessionStartedEvent("thread-1", null, null));
        h.Raise(s, new AgentQuestionEvent("async:m1", new[] { new AgentQuestion { Id = "m1:0", Text = "要哪種格式？", Options = { new AgentQuestionOption { Label = "Excel" } } } }, AnswerAsMessage: true));
        h.Send(h.Main, id, "排隊A");
        await h.WaitUntilAsync(() => h.Hub.GetQueue(id).Count == 1, "執行中送出排進佇列");
        h.Command(h.Main, "stop", new { conversationId = id });
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);
        h.Queue(id).Should().Equal("排隊A");
        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);

        // 空白送出（從佇列接著送）還在背景檢查資料夾時，回答非同步提問先開始了新的一輪
        CommandsInOneTick(h,
            HubHarness.MakeCommand("send", new { conversationId = id, text = "" }),
            HubHarness.MakeCommand("answer", new { conversationId = id, itemId = question.Id, answers = new Dictionary<string, string[]> { ["m1:0"] = new[] { "Excel" } } }));

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("我的回答：\n要哪種格式？：Excel");
        await Task.Delay(300);
        await h.Ui.DrainAsync();
        h.Queue(id).Should().Equal(new[] { "排隊A" }, "另一則已在執行：這則留在佇列，不能被移出佇列卻沒送出");

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(3);
        s.Sent.All[2].Input.Text.Should().Be("排隊A", "那一輪正常完成後照常送出");
        h.Queue(id).Should().BeEmpty();
    }

    // ───── 非同步提問在執行中作答（hub-core#5） ─────

    [Fact]
    public async Task 非同步提問_執行中回答_卡片更新為已回答_排進佇列_這一輪結束後當下一則訊息送出且保留輸入區附件()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "先問一題");
        h.Raise(s, new AgentQuestionEvent("async:m1", new[] { new AgentQuestion { Id = "m1:0", Text = "要哪種格式？", Options = { new AgentQuestionOption { Label = "Excel" } } } }, AnswerAsMessage: true));
        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);
        var mark = h.Main.Mark();

        h.Command(h.Main, "answer", new { conversationId = id, itemId = question.Id, answers = new Dictionary<string, string[]> { ["m1:0"] = new[] { "Excel" } } });

        // 網頁送出答案時已把卡片鎖住，宿主一定要送回更新的卡片（卡片寫著「回答會當作下一則訊息送出」）
        question.Status.Should().Be(AgentItemStatus.Done);
        question.Decision.Should().Be("answered");
        await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("id") == question.Id && m["item"]!.Value<string>("status") == "Done", mark, conversationId: id);
        h.Main.Messages("toast", mark).Should().BeEmpty();
        s.Sent.Count.Should().Be(1, "這一輪還在執行");
        h.Queue(id).Should().Equal("我的回答：\n要哪種格式？：Excel");

        var pending = h.Temp.WriteFile(@"in\報表.csv", "a,b");
        mark = h.Main.Mark();
        h.Command(h.Main, "addFiles", new { conversationId = id }, new[] { pending });
        await h.Main.WaitForAsync("composer", m => m["composer"]!["attachments"]!.Count() == 1, mark, conversationId: id);

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("我的回答：\n要哪種格式？：Excel");
        s.Sent.All[1].Input.Attachments.Should().BeEmpty();
        h.Main.Last("composer", id)!["composer"]!["attachments"]!.Should().ContainSingle("回答提問不能把使用者已附、還沒送出的檔案丟掉");
        s.Answers.Count.Should().Be(0, "非同步提問不經會話的作答 API");
    }

    [Fact]
    public void 非同步提問_排隊的回答取消後卡片回到可作答_這一輪結束後回答直接送出()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "先問一題");
        h.Raise(s, new AgentQuestionEvent("async:m1", new[] { new AgentQuestion { Id = "m1:0", Text = "要哪種格式？", Options = { new AgentQuestionOption { Label = "Excel" }, new AgentQuestionOption { Label = "CSV" } } } }, AnswerAsMessage: true));
        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);
        h.Command(h.Main, "answer", new { conversationId = id, itemId = question.Id, answers = new Dictionary<string, string[]> { ["m1:0"] = new[] { "Excel" } } });
        var queued = h.Main.Last("composer", id)!["composer"]!["queue"]!.Single().Value<string>("id");

        h.Command(h.Main, "cancelQueued", new { conversationId = id, id = queued });

        question.Status.Should().Be(AgentItemStatus.Waiting, "排隊的回答取消了，代理沒收到答案，要能重新作答");
        question.Decision.Should().BeNull();
        question.Answers.Should().BeEmpty();

        h.CompleteTurn(s);
        s.Sent.Count.Should().Be(1);
        question.Status.Should().Be(AgentItemStatus.Waiting, "非同步提問在這一輪結束後仍可回答");

        h.Command(h.Main, "answer", new { conversationId = id, itemId = question.Id, answers = new Dictionary<string, string[]> { ["m1:0"] = new[] { "CSV" } } });

        s.Sent.Count.Should().Be(2, "沒在執行時直接送出");
        s.Sent.All[1].Input.Text.Should().Be("我的回答：\n要哪種格式？：CSV");
        question.Status.Should().Be(AgentItemStatus.Done);
    }

    // ───── 執行中切換權限模式（hub-core#6） ─────

    [Fact]
    public async Task 執行中切換權限模式_Claude立即套用到這一輪_做不到時提示下一則訊息才生效()
    {
        using var h = new HubHarness();
        var (claude, sc) = h.StartConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Command(h.Main, "setOption", new { conversationId = claude, key = "permission", value = "FullAccess" });

        sc.ModeChanges.All.Should().Equal(new[] { AgentPermissionMode.FullAccess }, "標頭立即顯示新模式，這一輪也要照新模式執行");
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("permission") == "FullAccess", mark, conversationId: claude);
        h.Main.Messages("toast", mark).Should().BeEmpty();

        // 即時切換失敗（例如 CLI 沒回應）：明說這一輪仍用原本的模式
        sc.CanSwitchModeLive = false;
        h.Command(h.Main, "setOption", new { conversationId = claude, key = "permission", value = "Ask" });
        (await h.Main.WaitForAsync("toast", since: mark)).Value<string>("text").Should().Contain("下一則訊息開始生效");

        // Codex 的政策隨 turn/start 帶上，這一輪中途改不了
        var (codex, sx) = h.StartConversation(h.Main, h.Codex);
        mark = h.Main.Mark();
        h.Command(h.Main, "setOption", new { conversationId = codex, key = "permission", value = "FullAccess" });
        sx.ModeChanges.All.Should().Equal(AgentPermissionMode.FullAccess);
        (await h.Main.WaitForAsync("toast", since: mark)).Value<string>("text").Should().Contain("下一則訊息開始生效");

        // 沒在執行：下一則訊息本來就帶新的模式，不必即時切換
        h.CompleteTurn(sc);
        h.Command(h.Main, "setOption", new { conversationId = claude, key = "permission", value = "AcceptEdits" });
        sc.ModeChanges.Count.Should().Be(2);
        h.Send(h.Main, claude, "下一則");
        sc.Sent.All[^1].Settings.PermissionMode.Should().Be(AgentPermissionMode.AcceptEdits);
    }

    // ───── 執行中換模型與推理強度 ─────

    [Fact]
    public async Task 執行中換模型_Claude立即套用到這一輪_做不到時提示下一則訊息才生效()
    {
        using var h = new HubHarness();
        var (claude, sc) = h.StartConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Command(h.Main, "setOption", new { conversationId = claude, key = "model", value = "sonnet" });

        sc.ModelChanges.All.Select(x => x.Model).Should().Equal(new[] { "sonnet" }, "標頭立即顯示新模型，這一輪也要從下一次模型呼叫起改用");
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("model") == "sonnet", mark, conversationId: claude);
        h.Main.Messages("toast", mark).Should().BeEmpty();

        h.Command(h.Main, "setOption", new { conversationId = claude, key = "effort", value = "high" });
        sc.ModelChanges.All[^1].Should().Be(("sonnet", "high"), "推理強度也即時套用，一併帶上目前的模型");
        h.Main.Messages("toast", mark).Should().BeEmpty();

        // 再選一次同一個模型：設定沒變，不必送
        h.Command(h.Main, "setOption", new { conversationId = claude, key = "model", value = "sonnet" });
        sc.ModelChanges.Count.Should().Be(2);

        // 即時切換失敗（例如 CLI 不認得這個模型）：明說這一輪仍用原本的模型
        sc.CanSwitchModelLive = false;
        h.Command(h.Main, "setOption", new { conversationId = claude, key = "model", value = "opus" });
        (await h.Main.WaitForAsync("toast", since: mark)).Value<string>("text").Should().Contain("模型從下一則訊息開始生效");
        mark = h.Main.Mark();
        h.Command(h.Main, "setOption", new { conversationId = claude, key = "effort", value = "low" });
        (await h.Main.WaitForAsync("toast", since: mark)).Value<string>("text").Should().Contain("強度從下一則訊息開始生效");

        // Codex 的模型隨 turn/start 帶上，這一輪中途改不了
        var (codex, sx) = h.StartConversation(h.Main, h.Codex);
        mark = h.Main.Mark();
        h.Command(h.Main, "setOption", new { conversationId = codex, key = "model", value = "gpt-x" });
        sx.ModelChanges.All.Select(x => x.Model).Should().Equal("gpt-x");
        (await h.Main.WaitForAsync("toast", since: mark)).Value<string>("text").Should().Contain("下一則訊息開始生效");

        // 沒在執行：照樣交給會話（Claude 閒置的程序也即時套用，下一輪不必重開），做不到也不必提示
        h.CompleteTurn(sx);
        mark = h.Main.Mark();
        h.Command(h.Main, "setOption", new { conversationId = codex, key = "model", value = "gpt-y" });
        sx.ModelChanges.Count.Should().Be(2);
        h.Send(h.Main, codex, "下一則");
        sx.Sent.All[^1].Settings.Model.Should().Be("gpt-y");
        h.Main.Messages("toast", mark).Should().BeEmpty();
    }

    [Fact]
    public async Task 執行中換模型_等回應期間這一輪結束或又換了模型_不提示()
    {
        using var h = new HubHarness();
        var (claude, sc) = h.StartConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        // 第一次切換晚回 false，期間又換了一次（立即成功）：以最後選的為準，前一次的結果不提示
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sc.ModelSwitchGate = first;
        h.Command(h.Main, "setOption", new { conversationId = claude, key = "model", value = "sonnet" });
        sc.ModelSwitchGate = null;
        h.Command(h.Main, "setOption", new { conversationId = claude, key = "model", value = "opus" });
        first.SetResult(false);
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("model") == "opus", mark, conversationId: claude);
        await Task.Delay(100);
        sc.ModelChanges.All.Select(x => x.Model).Should().Equal("sonnet", "opus");
        h.Main.Messages("toast", mark).Should().BeEmpty("等待期間又換了模型：以最後選的為準，前一次的結果不提示");

        var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sc.ModelSwitchGate = second;
        h.Command(h.Main, "setOption", new { conversationId = claude, key = "model", value = "haiku" });
        h.CompleteTurn(sc);
        second.SetResult(false);
        await Task.Delay(100);
        h.Main.Messages("toast", mark).Should().BeEmpty("等待期間這一輪結束了：下一則訊息本來就用新模型");
    }

    // ───── Codex 拒絕附的說明（hub-core#7、#9） ─────

    [Fact]
    public void Codex拒絕附說明後這一輪失敗_說明排到佇列最前面跟著暫停_按送出才接著送()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "刪除暫存檔");
        h.Send(h.Main, id, "排隊B");
        h.Raise(s, new AgentApprovalEvent("0", "執行指令", "bash", "rm -rf tmp", null, "exec-1", CanAllowForSession: true));
        var approval = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        h.Command(h.Main, "approve", new { conversationId = id, itemId = approval.Id, choice = "deny", message = "改用資源回收筒" });

        h.CompleteTurn(s, AgentTurnOutcome.Failed, "You've hit your usage limit");

        s.Sent.Count.Should().Be(1, "失敗時說明與佇列一起暫停，不自動送出");
        h.OnUi(() => h.Hub.IsRunning(id)).Should().BeFalse();
        h.Queue(id).Should().Equal("（我剛才拒絕了那個操作）改用資源回收筒", "排隊B");
        h.Items(id).TakeLast(2).Select(i => i.Text).Should().Equal("You've hit your usage limit", "排隊的訊息已暫停，按送出繼續");

        h.Send(h.Main, id, "");

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("（我剛才拒絕了那個操作）改用資源回收筒", "按送出時說明先送");
        h.CompleteTurn(s);
        s.Sent.All[2].Input.Text.Should().Be("排隊B");
    }

    [Fact]
    public void Codex同一輪拒絕多次並附說明_每則說明依序送給代理()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "重構");
        h.Raise(s, new AgentApprovalEvent("1", "修改檔案", "edit", @"C:\w\a.cs", null, "fc-1", CanAllowForSession: true));
        var first = h.Items(id).Last();
        h.Command(h.Main, "approve", new { conversationId = id, itemId = first.Id, choice = "deny", message = "不要改 A 的公開介面" });
        // 檔案修改的拒絕不會結束這一輪（decline：The agent will continue the turn），同一輪可以再拒絕
        h.Raise(s, new AgentApprovalEvent("2", "修改檔案", "edit", @"C:\w\b.cs", null, "fc-2", CanAllowForSession: true));
        var second = h.Items(id).Last();
        h.Command(h.Main, "approve", new { conversationId = id, itemId = second.Id, choice = "deny", message = "B 也先不要動" });

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("（我剛才拒絕了這幾個操作）\n1. 「修改檔案：C:\\w\\a.cs」：不要改 A 的公開介面\n2. 「修改檔案：C:\\w\\b.cs」：B 也先不要動");
    }

    // ───── 背景說明（hub-core#8、#13） ─────

    [Fact]
    public void Codex的背景說明_工作資料夾註明以每一輪的cwd為準()
    {
        using var h = new HubHarness();
        var (_, s) = h.StartConversation(h.Main, h.Codex);

        var settings = s.Sent.All.Single().Settings;
        settings.Instructions.Should().Contain("工作資料夾：" + settings.WorkingDirectory + AgentHubRules.CodexWorkingDirectoryNote + "\n");
    }

    [Fact]
    public async Task 工作資料夾路徑含預留字樣_背景說明照原樣代入()
    {
        using var h = new HubHarness();
        var folder = h.Temp.CreateDirectory("{attachments}");
        var id = h.NewConversation(h.Main, h.Claude);
        await h.SetFolderAsync(h.Main, id, folder);

        h.Send(h.Main, id, "做事");

        await h.WaitUntilAsync(() => h.Sessions.Count == 1, "送出前在背景確認資料夾存在");
        h.Sessions.Single().Sent.All.Single().Settings.Instructions.Should().Contain($"工作資料夾：{folder}\n", "代入的路徑不能再被當成樣板代換");
    }

    // ───── 恢復預設工作區（hub-core#10） ─────

    [Fact]
    public async Task 恢復預設工作區_對話清單也更新工作資料夾()
    {
        using var h = new HubHarness();
        var folder = h.Temp.CreateDirectory("proj");
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        await h.SetFolderAsync(h.Main, id, folder);
        await h.Main.WaitForAsync("conversations", m => Summary(m, id)?.Value<string>("workingDirectory") == folder);
        var mark = h.Main.Mark();

        h.Command(h.Main, "resetFolder", new { conversationId = id });

        h.Conversation(id)!.WorkingDirectory.Should().BeNull();
        await h.Main.WaitForAsync("conversations", m => Summary(m, id)?.Value<string>("workingDirectory") == h.Store.GetDefaultWorkingDirectory(id), mark);
    }

    // ───── 登入提示（hub-core#11） ─────

    [Fact]
    public async Task 帳戶資料夾刪不乾淨的提示_只由網頁顯示一次_宿主不另外送toast()
    {
        using var h = new HubHarness();
        Directory.CreateDirectory(h.Claude2.ConfigDirectory!);
        var locked = Path.Combine(h.Claude2.ConfigDirectory!, ".credentials.json");
        File.WriteAllText(locked, "{}");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var mark = h.Main.Mark();

            h.Command(h.Main, "accountRemove", new { accountId = h.Claude2.Id, deleteData = true });

            var login = await h.Main.WaitForAsync("accountLogin", m => m.Value<bool>("done"), mark, timeoutMs: 10000);
            var message = login.Value<string>("message");
            message.Should().Contain("沒有完全刪除");
            await h.Ui.DrainAsync();
            h.Main.Messages("toast", mark).Select(t => t.Value<string>("text")).Should().NotContain(message,
                "網頁收到 done 的 accountLogin 會自己顯示提示，宿主再發 toast 就跳兩次");
        }
    }

    // ───── 工具輸出截斷（hub-core#12） ─────

    [Fact]
    public void 工具輸出串流_多次截斷後省略字數照累計()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", null));

        for (int i = 0; i < 100; i++)
            h.Raise(s, new AgentToolOutputEvent("t1", new string('a', 999) + "\n"));

        var output = h.Items(id).Single(i => i.Kind == AgentItemKind.Tool).Output!;
        output.Should().StartWith("…（前 88,000 字省略）\n", "總共 10 萬字、保留 1 萬 2 千字");
        output.Length.Should().Be("…（前 88,000 字省略）\n".Length + AgentHubRules.MaxToolOutputChars);
    }
}
