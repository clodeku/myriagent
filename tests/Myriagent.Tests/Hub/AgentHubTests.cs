using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 中樞的流程（假的宿主、視窗與代理會話；真的 AccountManager 以 Seed 放帳戶）：
/// 多視窗路由、草稿、送出與佇列、換帳戶續接、問卷與核准、計畫確認、權限模式、版面、刪除、帳戶移除、額度合併、存檔。
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubTests
{
    private static string? ItemKind(JObject message) => message["item"]?.Value<string>("kind");

    /// <summary>JSON 的 null（Json.NET 對 null 字串可能產生型別為 String、值為 null 的 JValue）</summary>
    private static bool IsNull(JToken? token) => token == null || token.Type == JTokenType.Null || token is JValue { Value: null };

    // ───── 多視窗路由與草稿 ─────

    [Fact]
    public async Task 兩個視窗_只有開著該對話的視窗收到項目與標頭更新_關閉後不再收到()
    {
        using var h = new HubHarness();
        var a = h.Main;
        var b = h.AddView("second");
        var (id, s) = h.StartConversation(a, h.Claude, "第一則");

        await a.WaitForAsync("header", m => m["header"]!.Value<bool>("running"), conversationId: id);
        b.Messages("item", conversationId: id).Should().BeEmpty("B 沒有開這個對話");
        b.Messages("header", conversationId: id).Should().BeEmpty();

        var markA = a.Mark();
        var markB = b.Mark();
        h.Raise(s, new AgentTextDeltaEvent("m:1", "哈囉"));
        await a.WaitForAsync("item", m => ItemKind(m) == "Assistant", markA, conversationId: id);
        b.Messages("item", markB, id).Should().BeEmpty("同一次合併送出只送給開著對話的視窗");

        // B 打開這個對話：收到整個對話，之後的更新兩邊都收到
        h.Command(b, "open", new { conversationId = id });
        var opened = b.Last("conversation", id)!;
        opened["items"]!.Select(i => i.Value<string>("kind")).Should().Equal("User", "Assistant");
        opened["header"]!.Value<bool>("isDraft").Should().BeFalse();

        markA = a.Mark();
        markB = b.Mark();
        h.Raise(s, new AgentTextDeltaEvent("m:1", "，世界"));
        await a.WaitForAsync("item", m => m["item"]!.Value<string>("html")!.Contains("哈囉，世界"), markA, conversationId: id);
        await b.WaitForAsync("item", m => m["item"]!.Value<string>("html")!.Contains("哈囉，世界"), markB, conversationId: id);

        // A 關掉分頁：對話繼續在背景執行，只有 B 收到更新
        h.Command(a, "close", new { conversationId = id });
        markA = a.Mark();
        markB = b.Mark();
        h.Raise(s, new AgentTextCompletedEvent("m:1", "哈囉，世界！"));
        h.CompleteTurn(s);
        await b.WaitForAsync("header", m => !m["header"]!.Value<bool>("running"), markB, conversationId: id);
        await b.WaitForAsync("item", m => m["item"]!.Value<string>("status") == "Done", markB, conversationId: id);
        await Task.Delay(150);
        a.Messages("item", markA, id).Should().BeEmpty("A 已經關掉這個對話");
        a.Messages("header", markA, id).Should().BeEmpty();
        h.Conversation(id).Should().NotBeNull("關分頁不會刪除已開始的對話");
    }

    [Fact]
    public async Task 草稿_所有視窗都關掉後才丟棄_附件資料夾一起刪除()
    {
        using var h = new HubHarness();
        var a = h.Main;
        var b = h.AddView("second");
        var draft = h.NewConversation(a, h.Claude);
        h.Command(b, "open", new { conversationId = draft });

        var file = h.Temp.WriteFile(@"in\說明.txt", "內容");
        var mark = a.Mark();
        h.Command(a, "addFiles", new { conversationId = draft }, new[] { file });
        await a.WaitForAsync("composer", m => m["composer"]!["attachments"]!.Count() == 1, mark, conversationId: draft);
        var conversationDir = h.Store.GetConversationDirectory(draft);
        Directory.Exists(Path.Combine(conversationDir, "attachments")).Should().BeTrue();

        h.Command(a, "close", new { conversationId = draft });
        h.Conversation(draft).Should().NotBeNull("B 還開著");
        Directory.Exists(conversationDir).Should().BeTrue();

        h.Command(b, "close", new { conversationId = draft });
        h.Conversation(draft).Should().BeNull("沒有視窗顯示的草稿丟棄");
        await h.WaitUntilAsync(() => !Directory.Exists(conversationDir), "store 不能留下草稿的資料夾（在背景刪除）");
        h.Store.LoadConversations().Should().BeEmpty();
    }

    [Fact]
    public void 草稿_視窗關閉_DetachView時丟棄()
    {
        using var h = new HubHarness();
        var popoutLike = h.AddView("closing");
        var draft = h.NewConversation(popoutLike, h.Codex);

        h.Ui.Invoke(() => h.Hub.DetachView(popoutLike.Id));

        h.Conversation(draft).Should().BeNull();
    }

    [Fact]
    public void Ready_送出Init_含帳戶對話CLI與設定()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "ready", new { mode = "main" });

        var init = h.Main.Last("init")!;
        init.Value<string>("mode").Should().Be("main");
        IsNull(init["popoutConversationId"]).Should().BeTrue();
        init["accounts"]!.Select(x => x.Value<string>("id")).Should().Equal("acc-claude", "acc-claude2", "acc-codex");
        var claude = init["accounts"]![0]!;
        claude.Value<bool>("isSystemDefault").Should().BeTrue();
        claude.Value<string>("status").Should().Be("ready");
        claude.Value<string>("statusText").Should().Be("已登入");
        IsNull(claude["usage"]).Should().BeTrue("還沒有用量資料");
        init["accounts"]![1]!.Value<string>("configDirectory").Should().Be(h.Claude2.ConfigDirectory);
        init["conversations"]!.Should().BeEmpty();
        init["cli"]!["Claude"]!.Value<string>("provider").Should().Be("Claude");
        init["cli"]!["Codex"].Should().NotBeNull();
        init["settings"]!.Value<string>("theme").Should().Be("dark");
        init["settings"]!.Value<string>("instructions").Should().Be(AgentHubRules.DefaultInstructions);
        init["settings"]!.Value<string>("continueText").Should().Be(AgentHubRules.DefaultContinueText);
        IsNull(init["layout"]).Should().BeTrue();
    }

    [Fact]
    public void NewConversation_草稿不列入清單_Claude預先產生SessionId_Codex沒有()
    {
        using var h = new HubHarness();
        var claudeDraft = h.NewConversation(h.Main, h.Claude);
        var header = h.Main.Last("conversation", claudeDraft)!["header"]!;
        header.Value<bool>("isDraft").Should().BeTrue();
        header.Value<string>("provider").Should().Be("Claude");
        header.Value<string>("accountId").Should().Be("acc-claude");
        header["permissionModes"]!.Should().HaveCount(5);
        h.Main.Last("conversation", claudeDraft)!.Value<string>("paneId").Should().Be("p1", "原樣送回窗格 id");
        Guid.TryParse(h.Conversation(claudeDraft)!.SessionId, out _).Should().BeTrue("Claude 的 session id 由 app 預先產生");

        var codexDraft = h.NewConversation(h.Main, h.Codex);
        h.Conversation(codexDraft)!.SessionId.Should().BeNull("Codex 的 thread id 由 thread/start 決定");
        h.Main.Last("conversation", codexDraft)!["header"]!["permissionModes"]!.Should().HaveCount(3);
        h.OnUi(() => h.Settings.LastAccountId).Should().Be("acc-codex");

        h.Command(h.Main, "ready");
        h.Main.Last("init")!["conversations"]!.Should().BeEmpty("草稿不列入清單");
    }

    // ───── 送出 ─────

    [Fact]
    public async Task Send_建立使用者項目_存檔_帶正確的會話設定()
    {
        using var h = new HubHarness();
        var id = h.NewConversation(h.Main, h.Claude2);
        h.Send(h.Main, id, "  幫我整理報表\n第二行  ");

        var s = h.Sessions.Single();
        s.Provider.Should().Be(AgentProvider.Claude);
        var (input, settings) = s.Sent.All.Single();
        input.Text.Should().Be("幫我整理報表\n第二行", "前後空白去掉");
        input.Attachments.Should().BeEmpty();
        settings.SessionId.Should().Be(h.Conversation(id)!.SessionId);
        settings.SessionStarted.Should().BeFalse();
        settings.HistoryRecap.Should().BeNull("第一則訊息沒有前文");
        settings.UseLocalCustomizations.Should().BeTrue("帳戶沒開精簡模式");
        settings.PermissionMode.Should().Be(AgentPermissionMode.Ask);
        settings.AccountKey.Should().Be($"acc-claude2|{h.Claude2.ConfigDirectory}");
        settings.CodexHome.Should().BeNull();
        settings.WorkingDirectory.Should().Be(h.Store.GetDefaultWorkingDirectory(id));
        Directory.Exists(settings.WorkingDirectory).Should().BeTrue();
        settings.ConversationDirectory.Should().Be(h.Store.GetConversationDirectory(id));
        settings.Instructions.Should().Contain("AskUserQuestion").And.Contain(settings.WorkingDirectory).And.Contain(h.Store.GetAttachmentDirectory(id));

        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["ANTHROPIC_API_KEY"] = "sk", ["CLAUDECODE"] = "1", ["PATH"] = "x" };
        settings.ConfigureEnvironment(env);
        env.Should().ContainKey("CLAUDE_CONFIG_DIR").WhoseValue.Should().Be(h.Claude2.ConfigDirectory);
        env.Keys.Should().BeEquivalentTo(new[] { "PATH", "CLAUDE_CONFIG_DIR", Myriagent.Background.BackgroundProgramRules.MarkerVariable },
            "API Key 與父 session 的變數剝除；多了這個對話的背景程式標記");
        env[Myriagent.Background.BackgroundProgramRules.MarkerVariable].Should().Be(id);

        var meta = h.Conversation(id)!;
        meta.Title.Should().Be("幫我整理報表");
        h.Items(id).Single().Kind.Should().Be(AgentItemKind.User);
        File.Exists(Path.Combine(h.Store.GetConversationDirectory(id), "meta.json")).Should().BeTrue();
        File.Exists(Path.Combine(h.Store.GetConversationDirectory(id), "items.json")).Should().BeTrue();
        await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(i => i.Value<string>("id") == id));
        var header = (await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("running"), conversationId: id))["header"]!;
        header.Value<string>("title").Should().Be("幫我整理報表");
        header["startedAt"]!.Type.Should().Be(JTokenType.Integer);
        // 對話上方常駐的「最近的指令」
        header["lastPrompt"]!.Value<string>("id").Should().Be(h.Items(id).Single().Id);
        header["lastPrompt"]!.Value<string>("text").Should().Be("幫我整理報表\n第二行");
    }

    [Fact]
    public void Send_Codex隔離帳戶_帶CodexHome與CODEX_HOME()
    {
        using var h = new HubHarness();
        var (_, s) = h.StartConversation(h.Main, h.Codex, "列出檔案");

        var settings = s.Sent.All.Single().Settings;
        s.Provider.Should().Be(AgentProvider.Codex);
        settings.SessionId.Should().BeNull();
        settings.CodexHome.Should().Be(h.Codex.ConfigDirectory);
        settings.Instructions.Should().Contain("request_user_input");
        var env = new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "sk" };
        settings.ConfigureEnvironment(env);
        env.Should().ContainKey("CODEX_HOME").WhoseValue.Should().Be(h.Codex.ConfigDirectory);
        env.Should().NotContainKey("OPENAI_API_KEY");
    }

    [Fact]
    public void Send_空白訊息不送_未登入或工作資料夾不存在時退回原文()
    {
        using var h = new HubHarness(status: AccountStatus.NotLoggedIn);
        var id = h.NewConversation(h.Main, h.Claude);

        h.Send(h.Main, id, "   ");
        h.Sessions.Should().BeEmpty();
        h.Main.Messages("sendRejected").Should().BeEmpty("空白訊息直接忽略");

        h.Send(h.Main, id, "還沒登入");
        h.Main.Last("sendRejected")!.Value<string>("text").Should().Be("還沒登入");
        h.Main.Last("toast")!.Value<string>("text").Should().Contain("尚未登入 Claude 帳戶");
        h.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_自訂的工作資料夾被刪掉時退回_不偷偷重建()
    {
        using var h = new HubHarness();
        var folder = h.Temp.CreateDirectory("proj");
        var id = h.NewConversation(h.Main, h.Claude);
        await h.SetFolderAsync(h.Main, id, folder);
        Directory.Delete(folder);
        var mark = h.Main.Mark();

        h.Send(h.Main, id, "做事");

        (await h.Main.WaitForAsync("sendRejected", since: mark, conversationId: id)).Value<string>("text").Should().Be("做事");
        h.Main.Last("toast")!.Value<string>("text").Should().StartWith("工作資料夾不存在");
        h.Sessions.Should().BeEmpty();
        Directory.Exists(folder).Should().BeFalse("使用者選的資料夾不存在時不偷偷重建");
    }

    [Fact]
    public void Send_執行中送出排進佇列_這一輪完成後自動送出()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "第一則");

        h.Send(h.Main, id, "接著幫我跑測試");

        s.Sent.Count.Should().Be(1, "執行中不直接送");
        h.Queue(id).Should().Equal("接著幫我跑測試");
        var composer = h.Main.Last("composer", id)!["composer"]!;
        composer["queue"]!.Should().ContainSingle();
        composer["queue"]![0]!.Value<string>("text").Should().Be("接著幫我跑測試");

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("接著幫我跑測試");
        h.Queue(id).Should().BeEmpty();
        h.Main.Last("composer", id)!["composer"]!["queue"]!.Should().BeEmpty();
        h.Items(id).Count(i => i.Kind == AgentItemKind.User).Should().Be(2);
        h.OnUi(() => h.Hub.RunningCount).Should().Be(1, "佇列訊息送出後又在執行中");
    }

    [Fact]
    public void Send_被停止時佇列暫停不送()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "第一則");
        h.Send(h.Main, id, "排隊的第二則");

        h.Command(h.Main, "stop", new { conversationId = id });
        s.Interrupts.Should().Be(1);
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        s.Sent.Count.Should().Be(1);
        h.Queue(id).Should().Equal("排隊的第二則");
        h.Items(id).Last().Text.Should().Be("已停止這一輪（排隊的訊息暫停，按送出繼續）");
        h.OnUi(() => h.Hub.RunningCount).Should().Be(0);
    }

    [Fact]
    public void Send_失敗時佇列暫停並提示_空白送出從佇列第一則接著送()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "第一則");
        h.Send(h.Main, id, "排隊的第二則");
        h.Send(h.Main, id, "   ");
        h.Queue(id).Should().Equal(new[] { "排隊的第二則" }, "執行中空白送出不做事");

        h.CompleteTurn(s, AgentTurnOutcome.Failed, "程序意外結束");

        s.Sent.Count.Should().Be(1, "失敗時不自動送排隊的訊息");
        h.Items(id).TakeLast(2).Select(i => (i.Kind, i.Text)).Should().Equal(
            (AgentItemKind.Error, "程序意外結束"), (AgentItemKind.Notice, "排隊的訊息已暫停，按送出繼續"));
        h.Queue(id).Should().Equal("排隊的第二則");

        h.Send(h.Main, id, "");

        s.Sent.Count.Should().Be(2, "佇列暫停時空白送出＝從佇列第一則接著送");
        s.Sent.All[1].Input.Text.Should().Be("排隊的第二則");
        h.Queue(id).Should().BeEmpty();
        h.Main.Last("composer", id)!["composer"]!["queue"]!.Should().BeEmpty();
        h.Main.Messages("sendRejected").Should().BeEmpty();
    }

    [Fact]
    public void Send_佇列是空的時空白送出不做事()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "第一則");
        h.CompleteTurn(s);

        h.Send(h.Main, id, "");

        s.Sent.Count.Should().Be(1);
        h.Items(id).Count(i => i.Kind == AgentItemKind.User).Should().Be(1);
    }

    [Fact]
    public async Task CancelQueued_排隊訊息帶的附件還回輸入區()
    {
        using var h = new HubHarness();
        var (id, _) = h.StartConversation(h.Main, h.Claude, "第一則");
        var file = h.Temp.WriteFile(@"in\資料.csv", "a,b");
        var mark = h.Main.Mark();
        h.Command(h.Main, "addFiles", new { conversationId = id }, new[] { file });
        await h.Main.WaitForAsync("composer", m => m["composer"]!["attachments"]!.Count() == 1, mark, conversationId: id);

        h.Send(h.Main, id, "排隊並附檔");

        var composer = h.Main.Last("composer", id)!["composer"]!;
        composer["attachments"]!.Should().BeEmpty("附件跟著排隊的訊息");
        composer["queue"]![0]!.Value<string>("text").Should().Be("排隊並附檔（附 1 個檔案）");

        h.Command(h.Main, "cancelQueued", new { conversationId = id, id = composer["queue"]![0]!.Value<string>("id") });

        h.Queue(id).Should().BeEmpty();
        h.Main.Last("composer", id)!["composer"]!["attachments"]!.Should().ContainSingle("取消排隊時附件還回輸入區");
    }

    // ───── 換帳戶 ─────

    [Fact]
    public void SetAccount_已開始的對話換帳戶後加提示_重開Session_下一則附前文節錄()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude, "我最喜歡的水果是芒果");
        var firstSessionId = h.Conversation(id)!.SessionId!;
        h.Raise(s1, new AgentTextCompletedEvent("m:1", "好的，我記住了。"));
        h.CompleteTurn(s1);
        h.Conversation(id)!.SessionStarted.Should().BeTrue();

        var mark = h.Main.Mark();
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id });

        var meta = h.Conversation(id)!;
        meta.AccountId.Should().Be(h.Claude2.Id);
        meta.SessionStarted.Should().BeFalse("原 session 在另一個帳戶，續接不了");
        meta.SessionAccountId.Should().BeNull();
        meta.SessionId.Should().NotBeNullOrEmpty().And.NotBe(firstSessionId);
        s1.Disposed.Should().BeTrue("關掉原本帳戶的程序");
        var notice = h.Items(id).Last();
        notice.Kind.Should().Be(AgentItemKind.Notice);
        notice.Text.Should().Contain("改用「Claude 副帳戶」").And.Contain("節錄");
        h.Main.Messages("conversation", mark, id).Should().ContainSingle("重送整個對話給開著它的視窗");

        h.Send(h.Main, id, "我最喜歡什麼水果？");

        var s2 = h.Sessions[^1];
        s2.Should().NotBeSameAs(s1);
        var (input, settings) = s2.Sent.All.Single();
        input.Text.Should().StartWith("（系統說明：原本的對話紀錄已無法續接");
        input.Text.Should().Contain("【使用者】我最喜歡的水果是芒果").And.Contain("【代理】好的，我記住了。");
        input.Text.Should().EndWith("我最喜歡什麼水果？");
        input.Text.Should().NotContain("改用「Claude 副帳戶」", "提示不列入節錄");
        settings.SessionStarted.Should().BeFalse();
        settings.SessionId.Should().Be(meta.SessionId);
        settings.HistoryRecap.Should().NotBeNull();
        settings.AccountKey.Should().StartWith(h.Claude2.Id);
        h.Items(id).Last().Text.Should().Be("我最喜歡什麼水果？", "畫面上的使用者訊息不含節錄");
    }

    [Fact]
    public void SetAccount_執行中換到Grok_先停下這一輪_結束後以Grok開新對話接續()
    {
        using var h = new HubHarness();
        var grok = new AgentAccount { Id = "acc-grok", Provider = AgentProvider.Grok, Label = "Grok 帳戶", Color = "#9CA3AF", ConfigDirectory = h.Temp.Combine("accounts", "acc-grok", "grok") };
        h.Ui.Invoke(() => h.Accounts.Seed(new[] { h.Claude, h.Claude2, h.Codex, grok }, AccountStatus.Ready));
        var (id, s) = h.StartConversation(h.Main, h.Claude);

        // 2026-10-24 起四種代理之間都能在執行中換（AgentHubRunningAccountSwitchTests）
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = grok.Id });

        h.Main.Messages("toast").Should().BeEmpty();
        s.Interrupts.Should().Be(1);
        h.Conversation(id)!.AccountId.Should().Be(h.Claude.Id, "這一輪結束之前還沒換");
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        (h.Conversation(id)!.AccountId, h.Conversation(id)!.Provider).Should().Be((grok.Id, AgentProvider.Grok));
        s.Disposed.Should().BeTrue();
        h.IsRunning(id).Should().BeTrue("Grok 接續的一輪在執行");
    }

    [Fact]
    public void SetAccount_草稿直接換_換到Codex時模型與權限改用Codex的預設()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "setConversationDefault", new { provider = "Codex", key = "permission", value = "FullAccess" });
        h.Command(h.Main, "setConversationDefault", new { provider = "Codex", key = "effort", model = "default", value = "xhigh" });
        var draft = h.NewConversation(h.Main, h.Claude);
        h.Command(h.Main, "setOption", new { conversationId = draft, key = "model", value = "opus" });
        h.Command(h.Main, "setOption", new { conversationId = draft, key = "permission", value = "AcceptEdits" });

        h.Command(h.Main, "setAccount", new { conversationId = draft, accountId = h.Codex.Id });

        var meta = h.Conversation(draft)!;
        meta.Provider.Should().Be(AgentProvider.Codex);
        meta.SessionId.Should().BeNull();
        meta.Model.Should().BeNull("Codex 帳戶的預設模型");
        meta.Effort.Should().Be("xhigh", "換到另一家：套用設定頁這個模型的預設推理強度");
        meta.PermissionMode.Should().Be(AgentPermissionMode.FullAccess, "換到另一家：套用設定頁的 Codex 預設權限模式");
        h.Items(draft).Should().BeEmpty("草稿不加提示");
        h.Main.Last("conversation", draft)!["header"]!.Value<string>("provider").Should().Be("Codex");
    }

    // ───── 事件、問卷、核准 ─────

    [Fact]
    public async Task 事件_串流文字工具問卷核准與完成_存檔後讀回()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "做事");

        h.Raise(s, new AgentSessionStartedEvent(h.Conversation(id)!.SessionId!, "claude-opus-5-5", null));
        h.Raise(s, new AgentThinkingDeltaEvent("m:0", "想一下"));
        h.Raise(s, new AgentTextDeltaEvent("m:1", "好的，"));
        h.Raise(s, new AgentTextDeltaEvent("m:1", "開始"));
        h.Raise(s, new AgentTextCompletedEvent("m:1", "好的，開始處理。"));
        var diff = new[] { new AgentDiffLine("del", "a"), new AgentDiffLine("add", "b"), new AgentDiffLine("add", "c") };
        h.Raise(s, new AgentToolStartedEvent("t1", "修改檔案", "edit", @"C:\w\a.cs", null, @"C:\w\a.cs", diff));
        h.Raise(s, new AgentToolOutputEvent("t1", "已修改"));
        h.Raise(s, new AgentToolCompletedEvent("t1", AgentItemStatus.Done, null, Array.Empty<string>()));
        h.Raise(s, new AgentQuestionEvent("q-1", new[]
        {
            new AgentQuestion { Id = "格式？", Text = "格式？", Options = { new AgentQuestionOption { Label = "Excel" }, new AgentQuestionOption { Label = "CSV" } } },
            new AgentQuestion { Id = "pwd", Text = "密碼", IsSecret = true },
        }, AnswerAsMessage: false));
        h.Raise(s, new AgentApprovalEvent("p-1", "寫入檔案", "write", @"C:\out\a.txt", "內容", "t2", CanAllowForSession: true, FilePath: @"C:\out\a.txt"));

        var items = h.Items(id);
        items.Select(i => i.Kind).Should().Equal(
            AgentItemKind.User, AgentItemKind.Thinking, AgentItemKind.Assistant, AgentItemKind.Tool, AgentItemKind.Question, AgentItemKind.Approval);
        items[1].Status.Should().Be(AgentItemStatus.Done, "開始回覆時思考就算結束");
        items[2].Text.Should().Be("好的，開始處理。");
        items[2].Status.Should().Be(AgentItemStatus.Done);
        items[3].Output.Should().Be("已修改");
        items[3].Status.Should().Be(AgentItemStatus.Done);
        items[3].ToolKind.Should().Be("edit");
        items[3].FilePath.Should().Be(@"C:\w\a.cs");
        items[3].Diff.Should().Equal(diff);
        items[5].FilePath.Should().Be(@"C:\out\a.txt");
        h.Conversation(id)!.SessionStarted.Should().BeTrue();

        var toolMessage = await h.Main.WaitForAsync("item", m => ItemKind(m) == "Tool", conversationId: id);
        toolMessage["item"]!["diffStats"]!.Value<int>("add").Should().Be(2);
        toolMessage["item"]!["diffStats"]!.Value<int>("del").Should().Be(1);
        toolMessage["item"]!.Value<string>("toolKind").Should().Be("edit");
        toolMessage["item"]!["diff"]!.Should().HaveCount(3);
        var header = await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("status") == "等待你核准", conversationId: id);
        header["header"]!.Value<bool>("running").Should().BeTrue();

        // 回答問卷：密碼類答案只送給代理
        var question = items[4];
        h.Command(h.Main, "answer", new { conversationId = id, itemId = question.Id, answers = new Dictionary<string, string[]> { ["格式？"] = new[] { "Excel" }, ["pwd"] = new[] { "abc123" } } });
        var answer = s.Answers.All.Single();
        answer.Key.Should().Be("q-1");
        answer.Answers["格式？"].Should().Equal("Excel");
        answer.Answers["pwd"].Should().Equal(new[] { "abc123" }, "代理要收到真正的答案");
        question.Status.Should().Be(AgentItemStatus.Done);
        question.Decision.Should().Be("answered");
        question.Answers["pwd"].Should().Equal(new[] { "（已隱藏）" }, "密碼類答案不留在畫面與存檔");

        // 核准：本對話都允許
        var approval = items[5];
        h.Command(h.Main, "approve", new { conversationId = id, itemId = approval.Id, choice = "allowSession" });
        s.Approvals.All.Single().Should().Be(("p-1", AgentApprovalChoice.AllowForSession, (string?)null));
        approval.Decision.Should().Be("allowSession");
        approval.Status.Should().Be(AgentItemStatus.Done);

        // 一輪結束：存檔、不再執行中、顯示用量
        var mark = h.Main.Mark();
        h.CompleteTurn(s, usage: new AgentUsage(100, 20, 50, null));
        var done = await h.Main.WaitForAsync("header", m => !m["header"]!.Value<bool>("running"), mark, conversationId: id);
        done["header"]!.Value<string>("lastTurn").Should().Be("輸入 100・輸出 20 tokens");

        var reloaded = new ConversationStore(h.Store.RootDirectory).LoadItems(id);
        reloaded.Should().HaveCount(6);
        reloaded[4].Answers["pwd"].Should().Equal("（已隱藏）");
        reloaded[3].Diff.Should().Equal(diff);
    }

    [Fact]
    public void 失敗的一輪_加錯誤項目_執行中的工具標為取消_下一則附前文()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "a", started: false);
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "sleep 100", null));
        h.CompleteTurn(s, AgentTurnOutcome.Failed, "程序意外結束");

        var items = h.Items(id);
        items.Last().Kind.Should().Be(AgentItemKind.Error);
        items.Last().Text.Should().Be("程序意外結束");
        items.Single(i => i.Kind == AgentItemKind.Tool).Status.Should().Be(AgentItemStatus.Cancelled);

        h.Send(h.Main, id, "b");
        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Settings.HistoryRecap.Should().Contain("【使用者】a", "上一則沒建立成功時要能補前文");
        s.Sent.All[1].Input.Text.Should().StartWith("（系統說明", "session 還沒建立成功，前文直接附在訊息前面");

        h.Command(h.Main, "stop", new { conversationId = id });
        s.Interrupts.Should().Be(1);
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);
        h.Items(id).Last().Text.Should().Be("已停止這一輪");
    }

    [Fact]
    public void 停止_等待中的卡片立即取消_之後按允許也不送()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "做事");
        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "rm -r x", null, "t1", CanAllowForSession: true));
        h.Raise(s, new AgentQuestionEvent("q-1", new[] { new AgentQuestion { Id = "q", Text = "要嗎？" } }, AnswerAsMessage: false));
        var approval = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        h.OnUi(() => h.Hub.WaitingCount).Should().Be(1);

        h.Command(h.Main, "stop", new { conversationId = id });

        approval.Status.Should().Be(AgentItemStatus.Cancelled, "停止後 CLI 已收到拒絕，卡片不能再按「允許」");
        h.Items(id).Single(i => i.Kind == AgentItemKind.Question).Status.Should().Be(AgentItemStatus.Cancelled);
        h.Command(h.Main, "approve", new { conversationId = id, itemId = approval.Id, choice = "allow" });
        s.Approvals.Count.Should().Be(0);
    }

    [Fact]
    public void Codex拒絕核准附說明_這一輪完成後自動把說明當下一則訊息送出()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "刪除暫存檔");
        h.Raise(s, new AgentApprovalEvent("0", "執行指令", "bash", "rm -rf tmp", "rm -rf tmp", "exec-1", CanAllowForSession: true));
        var approval = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);

        h.Command(h.Main, "approve", new { conversationId = id, itemId = approval.Id, choice = "deny", message = "  請改用資源回收筒  " });

        s.Approvals.All.Single().Should().Be(("0", AgentApprovalChoice.Deny, (string?)"請改用資源回收筒"));
        approval.Status.Should().Be(AgentItemStatus.Declined);
        approval.Decision.Should().Be("deny");
        approval.Output.Should().Be("你的說明：請改用資源回收筒");
        s.Sent.Count.Should().Be(1, "這一輪還沒結束");

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("（我剛才拒絕了那個操作）請改用資源回收筒");
    }

    [Fact]
    public void Claude拒絕核准附說明_說明放進拒絕回應_不另外送訊息()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "刪除暫存檔");
        h.Raise(s, new AgentApprovalEvent("perm-1", "執行指令", "bash", "rm -rf tmp", null, "toolu_1", CanAllowForSession: true));
        var approval = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);

        h.Command(h.Main, "approve", new { conversationId = id, itemId = approval.Id, choice = "deny", message = "改用 git rm" });
        h.CompleteTurn(s);

        s.Approvals.All.Single().Feedback.Should().Be("改用 git rm");
        s.Sent.Count.Should().Be(1);
    }

    [Fact]
    public void Codex拒絕核准附說明_使用者按了停止就不送說明()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "刪除暫存檔");
        h.Raise(s, new AgentApprovalEvent("0", "執行指令", "bash", "rm", null, "exec-1", CanAllowForSession: true));
        var approval = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        h.Command(h.Main, "approve", new { conversationId = id, itemId = approval.Id, choice = "deny", message = "不要刪" });

        h.Command(h.Main, "stop", new { conversationId = id });
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        s.Sent.Count.Should().Be(1);
        h.Items(id).Last().Text.Should().Be("已停止這一輪");
    }

    [Fact]
    public void Codex拒絕讓這一輪以Interrupted結束_沒按停止就當正常結束_說明照常送出()
    {
        // 實測 Codex 指令核准只有 cancel 可以拒絕，會讓這一輪以 interrupted 結束；使用者沒按停止，不能當成「已停止」
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "刪除暫存檔");
        h.Raise(s, new AgentApprovalEvent("0", "執行指令", "bash", "rm", null, "exec-1", CanAllowForSession: true));
        var approval = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        h.Command(h.Main, "approve", new { conversationId = id, itemId = approval.Id, choice = "deny", message = "改用資源回收筒" });

        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("（我剛才拒絕了那個操作）改用資源回收筒");
        h.Items(id).Should().NotContain(i => i.Text.StartsWith("已停止這一輪"), "使用者沒有按停止");
    }

    [Fact]
    public void 拒絕但沒附說明_這一輪結束後提示並照常送排隊的訊息()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "刪除暫存檔");
        h.Send(h.Main, id, "接著整理資料夾");
        h.Raise(s, new AgentApprovalEvent("0", "執行指令", "bash", "rm", null, "exec-1", CanAllowForSession: true));
        var approval = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        h.Command(h.Main, "approve", new { conversationId = id, itemId = approval.Id, choice = "deny" });

        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        h.Items(id).Should().Contain(i => i.Kind == AgentItemKind.Notice && i.Text == "你拒絕了這個操作，這一輪已結束");
        s.Sent.Count.Should().Be(2, "排隊的訊息照常送出");
        s.Sent.All[1].Input.Text.Should().Be("接著整理資料夾");
    }

    [Fact]
    public async Task 計畫確認_IsPlan核准畫成PlanApproval卡_同意後切到自動接受編輯()
    {
        using var h = new HubHarness();
        var id = h.NewConversation(h.Main, h.Claude);
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "Plan" });
        h.Main.Last("conversation", id).Should().NotBeNull();
        h.Send(h.Main, id, "規劃重構");
        var s = h.Sessions.Single();
        s.Sent.All.Single().Settings.PermissionMode.Should().Be(AgentPermissionMode.Plan);

        var mark = h.Main.Mark();
        h.Raise(s, new AgentApprovalEvent("plan-1", "確認執行計畫", "plan", "代理已規劃好做法，請確認是否照計畫執行",
            "## 計畫\n\n1. 先改 A\n2. 再改 B", "toolu_plan", CanAllowForSession: false, IsPlan: true));

        var card = h.Items(id).Last();
        card.Kind.Should().Be(AgentItemKind.PlanApproval);
        card.Status.Should().Be(AgentItemStatus.Waiting);
        var message = (await h.Main.WaitForAsync("item", m => ItemKind(m) == "PlanApproval", mark, conversationId: id))["item"]!;
        message.Value<string>("detailHtml").Should().Contain("<h2>計畫</h2>").And.Contain("<li>先改 A</li>");
        IsNull(message["detail"]).Should().BeTrue("計畫內容只走 detailHtml");
        message.Value<string>("toolKind").Should().Be("plan");
        message.Value<bool>("canAllowForSession").Should().BeFalse();
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("status") == "等待你確認計畫", mark, conversationId: id);

        h.Command(h.Main, "approvePlan", new { conversationId = id, itemId = card.Id, choice = "acceptEdits" });

        s.PlanResponses.All.Single().Should().Be(("plan-1", AgentPlanChoice.AcceptEdits, (string?)null));
        card.Decision.Should().Be("acceptEdits");
        card.Status.Should().Be(AgentItemStatus.Done);

        mark = h.Main.Mark();
        h.Raise(s, new AgentPermissionModeChangedEvent(AgentPermissionMode.AcceptEdits));
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("permission") == "AcceptEdits", mark, conversationId: id);
        h.Conversation(id)!.PermissionMode.Should().Be(AgentPermissionMode.AcceptEdits);
    }

    [Fact]
    public void 計畫確認_繼續規劃附說明_卡片標為拒絕()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "規劃");
        h.Raise(s, new AgentApprovalEvent("plan-2", "確認執行計畫", "plan", "請確認", "1. 改 A", null, CanAllowForSession: false, IsPlan: true));
        var card = h.Items(id).Last();

        h.Command(h.Main, "approvePlan", new { conversationId = id, itemId = card.Id, choice = "keepPlanning", message = " 先寫測試 " });

        s.PlanResponses.All.Single().Choice.Should().Be(AgentPlanChoice.KeepPlanning);
        card.Status.Should().Be(AgentItemStatus.Declined);
        card.Output.Should().Be("你的說明：先寫測試");
        h.Command(h.Main, "approve", new { conversationId = id, itemId = card.Id, choice = "allow" });
        s.Approvals.Count.Should().Be(0, "計畫確認卡不能用一般核准回應");
    }

    [Fact]
    public void 待辦清單_同一輪更新同一筆_全部完成時標為完成()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);

        h.Raise(s, new AgentPlanEvent(null, new[] { new AgentPlanStep { Text = "讀取", Status = "inProgress" }, new AgentPlanStep { Text = "寫入", Status = "pending" } }));
        h.Raise(s, new AgentPlanEvent("快好了", new[] { new AgentPlanStep { Text = "讀取", Status = "completed" }, new AgentPlanStep { Text = "寫入", Status = "completed" } }));

        var plan = h.Items(id).Single(i => i.Kind == AgentItemKind.Plan);
        plan.PlanSteps.Select(p => p.Status).Should().Equal("completed", "completed");
        plan.Text.Should().Be("快好了");
        plan.Status.Should().Be(AgentItemStatus.Done);
    }

    // 非同步提問（執行中作答排進佇列、取消後重新作答、結束後直接送出）見 AgentHubRegressionTests

    [Fact]
    public void 略過問卷_通知會話並標為略過()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentQuestionEvent("q-9", new[] { new AgentQuestion { Id = "q", Text = "要嗎？" } }, AnswerAsMessage: false));
        var question = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);

        h.Command(h.Main, "answer", new { conversationId = id, itemId = question.Id, answers = new Dictionary<string, string[]> { ["q"] = new[] { " " } } });
        h.Main.Last("toast")!.Value<string>("text").Should().Be("請先選擇或輸入答案");

        h.Command(h.Main, "skipQuestion", new { conversationId = id, itemId = question.Id });

        s.Declined.All.Should().Equal("q-9");
        question.Status.Should().Be(AgentItemStatus.Declined);
        question.Decision.Should().Be("skip");
    }

    [Fact]
    public void CLI自行撤銷請求_卡片改為已結束()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex);
        h.Raise(s, new AgentApprovalEvent("7", "執行指令", "bash", "ls", null, "e1", CanAllowForSession: true));

        h.Raise(s, new AgentRequestResolvedEvent("7"));

        h.Items(id).Single(i => i.Kind == AgentItemKind.Approval).Status.Should().Be(AgentItemStatus.Cancelled);
    }

    [Fact]
    public void 切到完全自動後會話請中樞放行等待中的卡片_當成允許並註明_已處理或不存在的卡片略過()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex);
        h.Raise(s, new AgentApprovalEvent("7", "執行指令（子代理）", "bash", "ls", null, "e1", CanAllowForSession: true));
        h.Raise(s, new AgentApprovalEvent("8", "執行指令", "bash", "dir", null, "e2", CanAllowForSession: true));
        var denied = h.Items(id).Single(i => i.RequestKey == "8");
        h.Command(h.Main, "approve", new { conversationId = id, itemId = denied.Id, choice = "deny" });

        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "FullAccess" });
        h.Raise(s, new AgentAutoApproveEvent("7"));
        h.Raise(s, new AgentAutoApproveEvent("7"));
        h.Raise(s, new AgentAutoApproveEvent("8"));
        h.Raise(s, new AgentAutoApproveEvent("沒有這張卡"));

        var card = h.Items(id).Single(i => i.RequestKey == "7");
        card.Status.Should().Be(AgentItemStatus.Done);
        card.Decision.Should().Be("allow");
        card.Output.Should().Be("已切換為完全自動，自動允許");
        denied.Status.Should().Be(AgentItemStatus.Declined, "使用者已經回應的卡片不能被改掉");
        s.Approvals.All.Should().Equal(("8", AgentApprovalChoice.Deny, (string?)null), ("7", AgentApprovalChoice.Allow, (string?)null));
    }

    [Fact]
    public void 提示與錯誤事件_模型與指令清單存進設定並更新標頭()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);

        h.Raise(s, new AgentNoticeEvent("注意事項", IsError: false));
        h.Raise(s, new AgentNoticeEvent("出錯了", IsError: true));
        h.Raise(s, new AgentModelsEvent(new[] { new AgentModelOption("haiku", "Haiku 5.5", null, new[] { "low" }) }));
        h.Raise(s, new AgentCommandsEvent(new[] { new AgentSlashCommand("compact", "壓縮對話", "") }));
        h.Raise(s, new AgentAccountInfoEvent("me@example.com", "Claude Max"));

        h.Items(id).TakeLast(2).Select(i => i.Kind).Should().Equal(AgentItemKind.Notice, AgentItemKind.Error);
        h.OnUi(() => h.Settings.Models[h.Claude.Id].Single().Value).Should().Be("haiku");
        h.OnUi(() => h.Settings.Commands[h.Claude.Id].Single().Name).Should().Be("compact");
        h.OnUi(() => h.Accounts.Find(h.Claude.Id)!.Email).Should().Be("me@example.com");

        h.Command(h.Main, "open", new { conversationId = id });
        var header = h.Main.Last("conversation", id)!["header"]!;
        header["models"]!.Select(m => m.Value<string>("value")).Should().Equal("default", "haiku");
        header["commands"]!.Select(c => c.Value<string>("name")).Should().Equal(new[] { "compact", "clear", "btw" }, "CLI 的清單沒有 clear 時中樞補在最後、Claude 再補 /btw（設定只存 CLI 回報的）");
    }

    // ───── 媒體白名單 ─────

    [Fact]
    public async Task 媒體白名單_只放行畫面上出現過的檔案()
    {
        using var h = new HubHarness();
        var image = h.Temp.WriteFile(@"out\chart.png", "png");
        var (id, s) = h.StartConversation(h.Main, h.Claude, "畫圖");

        h.OnUi(() => h.Hub.IsMediaAllowed(image)).Should().BeFalse();
        h.Raise(s, new AgentTextCompletedEvent("m:0", $"圖表在 {image}"));
        await h.Main.WaitForAsync("item", m => m["item"]!["media"] != null, conversationId: id);

        h.Items(id).Last().MediaPaths.Should().Equal(image);
        h.OnUi(() => h.Hub.IsMediaAllowed(image)).Should().BeTrue("畫面上出現過的媒體才放行");
        h.OnUi(() => h.Hub.IsThumbAllowed(image)).Should().BeTrue();
        h.OnUi(() => h.Hub.IsMediaAllowed(image.Replace('\\', '/'))).Should().BeTrue("正斜線的寫法是同一個檔案");
        h.OnUi(() => h.Hub.IsMediaAllowed(Path.Combine(Path.GetDirectoryName(image)!, "..", "out", "chart.png"))).Should().BeTrue("含 .. 的寫法正規化後相同");
        h.OnUi(() => h.Hub.IsMediaAllowed(@"C:\Windows\win.ini")).Should().BeFalse();
        h.OnUi(() => h.Hub.IsMediaAllowed(@"\\evil\share\chart.png")).Should().BeFalse();
    }

    [Fact]
    public async Task 回覆裡的相對路徑連結與圖片_以對話的工作資料夾解析()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "改程式");
        var cwd = h.Store.GetDefaultWorkingDirectory(id);
        var link = AgentMarkdownRenderer.OpenUrl(Path.Combine(cwd, "src", "app.js"));
        var image = Path.Combine(cwd, "shots", "home.png");

        h.Raise(s, new AgentTextCompletedEvent("m:0", "已修改 [app.js:42](src/app.js#L42)，截圖 ![首頁](shots/home.png)"));
        var item = (await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("html")!.Contains("/open?path="), conversationId: id))["item"]!;

        item.Value<string>("html").Should().Contain($"href=\"{link}\"").And.Contain($"src=\"{AgentMarkdownRenderer.MediaUrl(image)}\"");
        h.OnUi(() => h.Hub.IsMediaAllowed(image)).Should().BeTrue("相對路徑的圖片也登記到媒體白名單");

        h.Command(h.Main, "open", new { conversationId = id });
        h.Main.Last("conversation", id)!["items"]!.Last()!.Value<string>("html").Should().Contain($"href=\"{link}\"", "整個對話重送時同樣解析");
    }

    [Fact]
    public void 工具產出的媒體_只收本機存在的檔案()
    {
        using var h = new HubHarness();
        var image = h.Temp.WriteFile(@"out\shot.png", "png");
        var (id, s) = h.StartConversation(h.Main, h.Codex);
        h.Raise(s, new AgentToolStartedEvent("ig_1", "生成圖片", "image", "生成中…", null));

        h.Raise(s, new AgentToolCompletedEvent("ig_1", AgentItemStatus.Done, "一隻貓", new[] { image, @"C:\不存在\x.png", @"\\evil\share\y.png" }));

        var tool = h.Items(id).Single(i => i.Kind == AgentItemKind.Tool);
        tool.MediaPaths.Should().Equal(image);
        tool.Output.Should().Be("一隻貓");
    }

    // ───── 對話設定 ─────

    [Fact]
    public async Task SetOption_模型強度權限只改這個對話_模型記成帳戶預設_Codex的規劃模式換成每步詢問()
    {
        using var h = new HubHarness();
        var (id, _) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "setOption", new { conversationId = id, key = "model", value = "opus" });
        h.Command(h.Main, "setOption", new { conversationId = id, key = "effort", value = "high" });
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "FullAccess" });
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "不存在" });

        var meta = h.Conversation(id)!;
        meta.Model.Should().Be("opus");
        meta.Effort.Should().Be("high");
        meta.PermissionMode.Should().Be(AgentPermissionMode.FullAccess);
        h.OnUi(() => h.Settings.DefaultsFor(h.Claude.Id).Model).Should().Be("opus", "新對話沿用上次選的模型");
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("model") == "opus" && m["header"]!.Value<string>("effort") == "high"
            && m["header"]!.Value<string>("permission") == "FullAccess", conversationId: id);
        new ConversationStore(h.Store.RootDirectory).LoadConversations().Single().Model.Should().Be("opus", "已開始的對話立即存檔");

        h.Command(h.Main, "setOption", new { conversationId = id, key = "model", value = "default" });
        h.Conversation(id)!.Model.Should().BeNull("default＝帳戶預設");

        var codex = h.NewConversation(h.Main, h.Codex);
        h.Command(h.Main, "setOption", new { conversationId = codex, key = "permission", value = "Plan" });
        h.Conversation(codex)!.PermissionMode.Should().Be(AgentPermissionMode.Ask);
        h.Command(h.Main, "setOption", new { conversationId = codex, key = "permission", value = "AcceptEdits" });
        h.Conversation(codex)!.PermissionMode.Should().Be(AgentPermissionMode.Auto, "Codex 沒有自動接受編輯，換成自動");
        var fresh = h.Conversation(h.NewConversation(h.Main, h.Claude))!;
        fresh.Model.Should().BeNull("新對話沿用帳戶預設（已改回 default）");
        fresh.Effort.Should().BeNull("對話裡改的推理強度不影響新對話（設定頁沒設＝CLI 預設）");
        fresh.PermissionMode.Should().Be(AgentPermissionMode.Ask, "對話裡改的權限模式不影響新對話（設定頁的預設）");
    }

    [Fact]
    public async Task SetOption_換到不支援目前推理強度的模型_強度清掉_下一輪不帶舊值()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentModelsEvent(new[]
        {
            new AgentModelOption("default", "預設", null, new[] { "low", "medium", "high", "xhigh", "max" }),
            new AgentModelOption("opus", "Opus", null, new[] { "low", "medium", "high", "xhigh", "max" }),
            new AgentModelOption("sonnet", "Sonnet", null, new[] { "low", "medium", "high" }),
            new AgentModelOption("haiku", "Haiku", null, Array.Empty<string>()),
        }));

        h.Command(h.Main, "setOption", new { conversationId = id, key = "model", value = "opus" });
        h.Command(h.Main, "setOption", new { conversationId = id, key = "effort", value = "high" });
        h.Command(h.Main, "setOption", new { conversationId = id, key = "model", value = "sonnet" });
        h.Conversation(id)!.Effort.Should().Be("high", "新模型也支援就保留");

        h.Command(h.Main, "setOption", new { conversationId = id, key = "model", value = "opus" });
        h.Command(h.Main, "setOption", new { conversationId = id, key = "effort", value = "max" });
        h.Command(h.Main, "setOption", new { conversationId = id, key = "model", value = "sonnet" });
        h.Conversation(id)!.Effort.Should().BeNull("Sonnet 沒有 max：清掉（CLI 預設）");
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("model") == "sonnet" && m["header"]!.Value<string>("effort") == "default", conversationId: id);

        h.Command(h.Main, "setOption", new { conversationId = id, key = "effort", value = "high" });
        h.Command(h.Main, "setOption", new { conversationId = id, key = "model", value = "haiku" });
        h.Conversation(id)!.Effort.Should().BeNull("Haiku 不支援推理強度（網頁會隱藏推理按鈕）");

        h.CompleteTurn(s);
        h.Send(h.Main, id, "第二則");
        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Settings.Model.Should().Be("haiku");
        s.Sent.All[1].Settings.Effort.Should().BeNull("畫面沒有顯示推理強度，就不能把舊值傳給 CLI");
    }

    [Fact]
    public void CopyItem_代理回覆複製Markdown原文_其他項目或找不到不複製()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理成表格");
        h.Raise(s, new AgentTextCompletedEvent("m:0", "## 結果\n\n| 項目 | 前 |\n|---|---|\n| 載入 | 2.1 秒 |\n\n```cs\nvar x = 1;\n```"));
        var reply = h.Items(id).Single(i => i.Kind == AgentItemKind.Assistant);
        var user = h.Items(id).First(i => i.Kind == AgentItemKind.User);

        h.Command(h.Main, "copyItem", new { conversationId = id, itemId = reply.Id });
        h.Command(h.Main, "copyItem", new { conversationId = id, itemId = user.Id });
        h.Command(h.Main, "copyItem", new { conversationId = id, itemId = "不存在" });
        h.Command(h.Main, "copyItem", new { conversationId = "不存在", itemId = reply.Id });

        h.Host.Copied.All.Should().Equal(reply.Text);
        reply.Text.Should().StartWith("## 結果").And.Contain("| 載入 | 2.1 秒 |").And.Contain("```cs");
    }

    [Fact]
    public void Rename_改標題並限制長度_空白不改()
    {
        using var h = new HubHarness();
        var (id, _) = h.StartConversation(h.Main, h.Claude, "原本的標題");

        h.Command(h.Main, "rename", new { conversationId = id, title = "  新的標題  " });
        h.Conversation(id)!.Title.Should().Be("新的標題");
        h.Command(h.Main, "rename", new { conversationId = id, title = "   " });
        h.Conversation(id)!.Title.Should().Be("新的標題");
        h.Command(h.Main, "rename", new { conversationId = id, title = new string('長', 100) });
        h.Conversation(id)!.Title.Should().HaveLength(80);
        new ConversationStore(h.Store.RootDirectory).LoadConversations().Single().Title.Should().HaveLength(80);
    }

    [Fact]
    public async Task 工作資料夾_立即生效_背景確認存在後記進最近清單_不存在就還原_執行中不能換_恢復預設()
    {
        using var h = new HubHarness();
        var folder = h.Temp.CreateDirectory("proj");
        var id = h.NewConversation(h.Main, h.Claude);

        await h.SetFolderAsync(h.Main, id, folder);
        h.OnUi(() => h.Settings.RecentFolders.ToList()).Should().Equal(folder);
        AppSettings.Load().RecentFolders.Should().Equal(folder);
        h.Command(h.Main, "setFolder", new { conversationId = id, path = @"\\server\share" });
        h.Main.Last("toast")!.Value<string>("text").Should().Be(@"不能用這個資料夾：\\server\share", "UNC 路徑連存在都不查");
        h.Conversation(id)!.WorkingDirectory.Should().Be(folder);

        var missing = h.Temp.Combine("不存在的資料夾");
        var mark = h.Main.Mark();
        h.Command(h.Main, "setFolder", new { conversationId = id, path = missing });
        (await h.Main.WaitForAsync("toast", since: mark)).Value<string>("text").Should().Be($"找不到資料夾：{missing}");
        await h.WaitUntilAsync(() => h.Hub.GetConversation(id)!.WorkingDirectory == folder, "不存在的資料夾還原成原本的");
        h.OnUi(() => h.Settings.RecentFolders.ToList()).Should().Equal(new[] { folder }, "不存在的資料夾不記進最近清單");

        h.Send(h.Main, id, "做事");
        await h.WaitUntilAsync(() => h.Sessions.Count == 1, "送出前在背景確認資料夾存在");
        h.Sessions.Single().Sent.All.Single().Settings.WorkingDirectory.Should().Be(folder);
        h.Command(h.Main, "setFolder", new { conversationId = id, path = h.Temp.Root });
        h.Main.Last("toast")!.Value<string>("text").Should().Be("代理執行中不能更換工作資料夾");
        h.Conversation(id)!.WorkingDirectory.Should().Be(folder, "執行中不能更換工作資料夾");

        h.CompleteTurn(h.Sessions.Single());
        h.Command(h.Main, "resetFolder", new { conversationId = id });
        h.Conversation(id)!.WorkingDirectory.Should().BeNull();

        var next = h.NewConversation(h.Main, h.Claude);
        h.Conversation(next)!.WorkingDirectory.Should().Be(folder, "新對話預設用最近使用的資料夾");
    }

    [Fact]
    public async Task 工作資料夾_設定後緊接著送出就用新資料夾()
    {
        // 實機探針抓到：原本等背景確認完才生效，緊接著送出時設定會被丟掉
        using var h = new HubHarness();
        var folder = h.Temp.CreateDirectory("proj");
        var id = h.NewConversation(h.Main, h.Claude);

        h.Command(h.Main, "setFolder", new { conversationId = id, path = folder });
        h.Conversation(id)!.WorkingDirectory.Should().Be(folder, "立即生效");
        h.Send(h.Main, id, "馬上送出");

        await h.WaitUntilAsync(() => h.Sessions.Count == 1, "送出前在背景確認資料夾存在");
        h.Sessions.Single().Sent.All.Single().Settings.WorkingDirectory.Should().Be(folder);
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("customFolder") && m["header"]!.Value<string>("workingDirectory") == folder, conversationId: id);
    }

    [Fact]
    public async Task ChooseFolder_用宿主的資料夾對話框_取消就不改()
    {
        using var h = new HubHarness();
        var folder = h.Temp.CreateDirectory("picked");
        var id = h.NewConversation(h.Main, h.Claude);

        h.Command(h.Main, "chooseFolder", new { conversationId = id });
        h.Conversation(id)!.WorkingDirectory.Should().BeNull("對話框取消（回 null）");

        h.Host.FolderToPick = folder;
        h.Command(h.Main, "chooseFolder", new { conversationId = id });

        await h.WaitUntilAsync(() => h.Hub.GetConversation(id)!.WorkingDirectory == folder, "選好的資料夾生效");
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("customFolder") && m["header"]!.Value<string>("workingDirectory") == folder, conversationId: id);
    }

    [Fact]
    public async Task 對話清單_標示是否用對話自己的預設工作區()
    {
        // 刪除對話會連同預設工作區（代理寫出的檔案）一起刪掉：側欄的刪除確認要知道這個對話用的是不是預設工作區
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "寫個整理報表的小工具");
        h.CompleteTurn(s);

        var item = (await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(i => i.Value<string>("id") == id)))["items"]!.Single(i => i.Value<string>("id") == id);
        item.Value<bool?>("customFolder").Should().Be(false);
        item.Value<string>("workingDirectory").Should().Be(h.Store.GetDefaultWorkingDirectory(id));

        var folder = h.Temp.CreateDirectory("proj");
        var mark = h.Main.Mark();
        await h.SetFolderAsync(h.Main, id, folder);
        await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(i => i.Value<string>("id") == id && i.Value<bool?>("customFolder") == true && i.Value<string>("workingDirectory") == folder), mark);
    }

    // ───── 版面、刪除、帳戶 ─────

    [Fact]
    public void Layout_主視窗的版面存進設定_彈出視窗的不存_Ready時還原()
    {
        using var h = new HubHarness();
        var layout = new { panes = new[] { new { id = "p1", tabs = new[] { "c1", "c2" }, active = "c1" } }, focusedPane = "p1", sidebarCollapsed = true, view = "chat" };

        h.Command(h.Main, "layout", new { layout });

        h.OnUi(() => h.Settings.Layout!.Value<string>("focusedPane")).Should().Be("p1");
        var saved = AppSettings.Load();
        saved.Layout!["panes"]![0]!["tabs"]!.Values<string>().Should().Equal("c1", "c2");
        saved.Layout.Value<bool>("sidebarCollapsed").Should().BeTrue();

        var popout = h.AddPopout("popout", "c1");
        h.Command(popout, "layout", new { layout = new { focusedPane = "彈出視窗的" } });
        h.OnUi(() => h.Settings.Layout!.Value<string>("focusedPane")).Should().Be("p1");

        h.Command(h.Main, "ready");
        h.Main.Last("init")!["layout"]!.Value<string>("focusedPane").Should().Be("p1");
        h.Command(popout, "ready");
        var popoutInit = popout.Last("init")!;
        popoutInit.Value<string>("mode").Should().Be("popout");
        popoutInit.Value<string>("popoutConversationId").Should().Be("c1");
        IsNull(popoutInit["layout"]).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_所有視窗收到ConversationClosed_資料夾刪除_會話關閉()
    {
        using var h = new HubHarness();
        var b = h.AddView("second");
        var (id, s) = h.StartConversation(h.Main, h.Claude, "要刪的對話");
        h.CompleteTurn(s);
        h.Command(b, "open", new { conversationId = id });
        var mark = h.Main.Mark();

        h.Command(h.Main, "delete", new { conversationId = id });

        h.Main.Last("conversationClosed")!.Value<string>("conversationId").Should().Be(id);
        b.Last("conversationClosed")!.Value<string>("conversationId").Should().Be(id);
        h.Host.Deleted.All.Should().Equal(new[] { id }, "宿主要關掉顯示這個對話的彈出視窗");
        s.Disposed.Should().BeTrue();
        h.Conversation(id).Should().BeNull();
        await h.WaitUntilAsync(() => !Directory.Exists(h.Store.GetConversationDirectory(id)), "對話資料夾在背景刪除");
        await h.Main.WaitForAsync("conversations", m => m["items"]!.All(i => i.Value<string>("id") != id), mark);
    }

    [Fact]
    public async Task 執行中刪除_送出失敗晚到時不寫回磁碟()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "啟動中的對話");
        h.CompleteTurn(s);
        var gate = new TaskCompletionSource();
        s.SendGate = gate;
        h.Send(h.Main, id, "第二則");

        h.Command(h.Main, "delete", new { conversationId = id });
        await h.WaitUntilAsync(() => !Directory.Exists(h.Store.GetConversationDirectory(id)), "對話資料夾在背景刪除");
        gate.SetException(new ObjectDisposedException("CodexAgentSession"));
        await h.Ui.DrainAsync();
        await Task.Delay(100);

        Directory.Exists(h.Store.GetConversationDirectory(id)).Should().BeFalse("送出失敗晚到時不能把已刪除的對話寫回磁碟");
        h.Store.LoadConversations().Should().BeEmpty();
        h.Main.Messages("item", conversationId: id).Should().NotContain(m => m["item"]!.Value<string>("kind") == "Error" && m["item"]!.Value<string>("html")!.Contains("送出失敗"),
            "對話已刪除，不再回報送出失敗");
    }

    [Fact]
    public async Task 帳戶被移除_標頭標示帳戶不見_送出被拒_換帳戶後可繼續()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude2, "副帳戶的對話");
        h.CompleteTurn(s);
        var mark = h.Main.Mark();

        h.Command(h.Main, "accountRemove", new { accountId = h.Claude2.Id, deleteData = false });

        await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("accountMissing"), mark, conversationId: id);
        h.Main.Last("toast")!.Value<string>("text").Should().Be("已移除「Claude 副帳戶」");
        s.Disposed.Should().BeTrue();
        h.OnUi(() => h.Accounts.Find(h.Claude2.Id)).Should().BeNull();
        h.Conversation(id).Should().NotBeNull("對話保留在歷史");
        await h.Main.WaitForAsync("accounts", m => m["accounts"]!.Count() == 2, mark, timeoutMs: 3000);

        mark = h.Main.Mark();
        h.Send(h.Main, id, "還能送嗎");
        h.Main.Messages("sendRejected", mark, id).Single().Value<string>("text").Should().Be("還能送嗎");
        h.Main.Last("toast")!.Value<string>("text").Should().Be("這個對話的帳戶已被移除，請先換一個帳戶");

        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude.Id });
        h.Main.Last("conversation", id)!["header"]!.Value<bool>("accountMissing").Should().BeFalse();
    }

    [Fact]
    public async Task 帳戶被移除_執行中的對話在本機收尾_草稿關閉_可一併刪除隔離資料夾()
    {
        using var h = new HubHarness();
        Directory.CreateDirectory(h.Claude2.ConfigDirectory!);
        File.WriteAllText(Path.Combine(h.Claude2.ConfigDirectory!, ".credentials.json"), "{}");
        var (running, s) = h.StartConversation(h.Main, h.Claude2, "跑很久的工作");
        h.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", null));
        h.Raise(s, new AgentTextDeltaEvent("m:1", ""));
        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "rm x", null, "t2", CanAllowForSession: true));
        var draft = h.NewConversation(h.Main, h.Claude2);

        h.Command(h.Main, "accountRemove", new { accountId = h.Claude2.Id, deleteData = true });

        var items = h.Items(running);
        items.Last().Should().Match<AgentChatItem>(i => i.Kind == AgentItemKind.Notice && i.Text == "帳戶已移除，這一輪已停止");
        items.Single(i => i.Kind == AgentItemKind.Tool).Status.Should().Be(AgentItemStatus.Cancelled, "CLI 不會再回報結果，執行中的工具改為已停止");
        items.Single(i => i.Kind == AgentItemKind.Approval).Status.Should().Be(AgentItemStatus.Cancelled, "等待中的核准失效");
        items.Should().NotContain(i => i.Kind == AgentItemKind.Assistant, "空白的回覆區塊移除");
        h.OnUi(() => h.Hub.RunningCount).Should().Be(0);
        h.OnUi(() => h.Hub.WaitingCount).Should().Be(0);
        s.Disposed.Should().BeTrue();
        new ConversationStore(h.Store.RootDirectory).LoadItems(running).Last().Text.Should().Be("帳戶已移除，這一輪已停止", "收尾後存檔");
        h.Conversation(draft).Should().BeNull();
        h.Main.Messages("conversationClosed").Select(m => m.Value<string>("conversationId")).Should().Contain(draft);
        await h.WaitUntilAsync(() => !Directory.Exists(Path.GetDirectoryName(h.Claude2.ConfigDirectory)), "隔離帳戶的資料夾（含登入憑證）在背景一併刪除");
        JsonFileAccounts().Should().NotContain(h.Claude2.Id);

        static IEnumerable<string> JsonFileAccounts()
            => (JsonFile.Read<List<AgentAccount>>(AppPaths.AccountsFile) ?? new List<AgentAccount>()).Select(a => a.Id);
    }

    [Fact]
    public void 帳戶操作_重新命名顏色精簡模式與排序()
    {
        using var h = new HubHarness();

        h.Command(h.Main, "accountUpdate", new { accountId = h.Codex.Id, label = "  工作用 Codex  ", color = "#4fa3f7", safeMode = true });
        h.Command(h.Main, "accountUpdate", new { accountId = h.Codex.Id, color = "紅色" });
        h.Command(h.Main, "accountReorder", new { ids = new[] { h.Codex.Id, h.Claude.Id } });

        var codex = h.OnUi(() => h.Accounts.Find(h.Codex.Id)!);
        codex.Label.Should().Be("工作用 Codex");
        codex.Color.Should().Be("#4FA3F7", "不合法的顏色不改");
        codex.SafeMode.Should().BeTrue();
        h.OnUi(() => h.Accounts.Accounts.Select(a => a.Id).ToList()).Should().Equal(h.Codex.Id, h.Claude.Id, h.Claude2.Id);

        var (_, s) = h.StartConversation(h.Main, h.Codex);
        s.Sent.All.Single().Settings.UseLocalCustomizations.Should().BeFalse("精簡模式不載入使用者的外掛、MCP");
    }

    [Fact]
    public void 共用設定_隔離帳戶啟動前準備並在AccountKey標記_關掉後不帶()
    {
        using var h = new HubHarness();
        using var temp = new TempDirectory("hub-shared");
        var source = temp.CreateDirectory(".claude");
        h.OnUi(() => h.Accounts.Shared = new SharedCustomizations(_ => source, () => temp.Combine(".claude.json")));

        var (id, s) = h.StartConversation(h.Main, h.Claude2);
        var settings = s.Sent.All.Single().Settings;
        settings.AccountKey.Should().Be($"acc-claude2|{h.Claude2.ConfigDirectory}|shared", "開關改變時程序要重開");
        settings.PrepareShared.Should().NotBeNull();
        settings.PrepareShared!(settings.WorkingDirectory).ClaudeMemoryDirectory
            .Should().Be(SharedCustomizationRules.SharedMemoryDirectory(source, settings.WorkingDirectory), "工作資料夾不在 git 裡，照 CLI 用工作資料夾");
        h.Command(h.Main, "ready");
        var views = (JArray)h.Main.Last("init")!["accounts"]!;
        views.Single(a => (string?)a["id"] == h.Claude2.Id)["shareCustomizations"]!.Value<bool>().Should().BeTrue("預設共用");
        views.Single(a => (string?)a["id"] == h.Claude.Id)["shareCustomizations"]!.Value<bool>().Should().BeFalse("系統預設帳戶本身就是來源");

        h.Command(h.Main, "accountUpdate", new { accountId = h.Claude2.Id, shareCustomizations = false });
        h.OnUi(() => h.Accounts.Find(h.Claude2.Id)!.ShareCustomizations).Should().BeFalse();
        h.CompleteTurn(s);
        h.Send(h.Main, id, "第二則");

        var next = s.Sent.All.Last().Settings;
        next.PrepareShared.Should().BeNull();
        next.AccountKey.Should().Be($"acc-claude2|{h.Claude2.ConfigDirectory}");
    }

    [Fact]
    public void 共用設定_沒接上SharedCustomizations時一律不共用()
    {
        using var h = new HubHarness();

        var (_, s) = h.StartConversation(h.Main, h.Claude2);

        s.Sent.All.Single().Settings.PrepareShared.Should().BeNull("測試與實機探針不碰真的 ~/.claude");
    }

    [Fact]
    public void 開啟帳戶資料夾_建立後在檔案總管顯示()
    {
        using var h = new HubHarness();

        h.Command(h.Main, "accountOpenFolder", new { accountId = h.Codex.Id });

        Directory.Exists(h.Codex.ConfigDirectory).Should().BeTrue();
        h.Host.Revealed.All.Should().Equal(h.Codex.ConfigDirectory);
    }

    // ───── 額度 ─────

    [Fact]
    public async Task Claude額度事件_合併進帳戶用量並送出帳戶訊息()
    {
        using var h = new HubHarness();
        var weeklyReset = DateTimeOffset.FromUnixTimeSeconds(1792015200);
        h.Ui.Invoke(() => h.Accounts.SeedUsage(h.Claude.Id, new UsageSnapshot(DateTimeOffset.UtcNow.AddMinutes(-10), "api", new[]
        {
            new UsageWindow("session", "5 小時", 10, DateTimeOffset.FromUnixTimeSeconds(1791430000)),
            new UsageWindow("weekly", "每週", 30, weeklyReset),
            new UsageWindow("weekly:Fable", "每週・Fable", 3, weeklyReset),
        }, null, null, null, false)));
        var (_, s) = h.StartConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Raise(s, new AgentRateLimitEvent(AgentProvider.Claude, JObject.Parse("""{"status":"allowed","resetsAt":1791436800,"unifiedWindows":{"five_hour":{"utilization":0.15,"resetsAt":1791436800},"seven_day":{"utilization":0.05}}}""")));

        var usage = h.OnUi(() => h.Accounts.GetUsage(h.Claude.Id))!;
        usage.Source.Should().Be("session");
        usage.Stale.Should().BeFalse();
        usage.Windows.Select(w => $"{w.Key}:{w.Percent}").Should().Equal("session:15", "weekly:5", "weekly:Fable:3");
        usage.Windows[0].ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1791436800));
        usage.Windows[1].ResetsAt.Should().Be(weeklyReset, "事件沒帶重置時間時沿用舊值");

        var accounts = await h.Main.WaitForAsync("accounts",
            m => m["accounts"]!.First(a => a.Value<string>("id") == h.Claude.Id)["usage"] is JObject u && u.Value<string>("source") == "session",
            mark, timeoutMs: 3000);
        var rendered = accounts["accounts"]!.First(a => a.Value<string>("id") == h.Claude.Id)["usage"]!;
        var windows = rendered["windows"]!.ToList();
        windows.Select(w => w.Value<string>("key")).Should().Equal("session", "weekly", "weekly:Fable");
        windows[0].Value<double>("percent").Should().Be(15);
        windows[0].Value<string>("label").Should().Be("5 小時");
        windows[0].Value<string>("severity").Should().Be("ok");
        windows[0].Value<long>("resetsAt").Should().Be(1791436800L * 1000);
        windows[0]["history"]!.Should().ContainSingle("記錄了一個走勢點");
        rendered.Value<bool>("stale").Should().BeFalse();
    }

    [Fact]
    public async Task Codex額度事件_合併並保留點數()
    {
        using var h = new HubHarness();
        h.Ui.Invoke(() => h.Accounts.SeedUsage(h.Codex.Id, new UsageSnapshot(DateTimeOffset.UtcNow.AddMinutes(-10), "api",
            new[] { new UsageWindow("codex:10080", "每週", 9, DateTimeOffset.FromUnixTimeSeconds(1791959804)) }, "12,345.68", null, null, false)));
        var (_, s) = h.StartConversation(h.Main, h.Codex);
        var mark = h.Main.Mark();

        h.Raise(s, new AgentRateLimitEvent(AgentProvider.Codex, JObject.Parse("""{"limitId":"codex","primary":{"usedPercent":96,"windowDurationMins":10080,"resetsAt":1791959804},"secondary":null}""")));

        var usage = h.OnUi(() => h.Accounts.GetUsage(h.Codex.Id))!;
        usage.Windows.Single().Percent.Should().Be(96);
        usage.Credits.Should().Be("12,345.68", "更新沒帶點數時沿用");
        var accounts = await h.Main.WaitForAsync("accounts",
            m => m["accounts"]!.First(a => a.Value<string>("id") == h.Codex.Id)["usage"] is JObject u && u["windows"]!.FirstOrDefault()?.Value<double>("percent") == 96,
            mark, timeoutMs: 3000);
        var codex = accounts["accounts"]!.First(a => a.Value<string>("id") == h.Codex.Id)!;
        codex["usage"]!["windows"]![0]!.Value<string>("severity").Should().Be("max");
        codex["usage"]!.Value<string>("credits").Should().Be("12,345.68");
        codex["sessions"]!.Value<int>("running").Should().Be(1);
    }

    // ───── 通知 ─────

    [Fact]
    public async Task 通知_視窗不在前景時提問送系統通知並標未讀()
    {
        using var h = new HubHarness();
        h.Host.IsForeground = false;
        var (id, s) = h.StartConversation(h.Main, h.Claude);

        h.Raise(s, new AgentQuestionEvent("q-1", new[] { new AgentQuestion { Id = "q", Text = "要用哪個格式？\n第二行" } }, AnswerAsMessage: false));

        var notification = h.Host.Notifications.All.Single();
        notification.Title.Should().Be("代理有問題想問你（Claude 主帳戶）");
        notification.Message.Should().Be("要用哪個格式？");
        notification.ConversationId.Should().Be(id);
        h.Host.AttentionRequests.Should().BeGreaterThan(0);
        await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(i => i.Value<string>("id") == id && i.Value<bool>("unread") && i.Value<bool>("waiting")));
    }

    [Fact]
    public void 通知_對話開在前景視窗時不打擾_關閉通知設定時不送()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });   // 網頁回報這個分頁正顯示在畫面上
        h.Raise(s, new AgentApprovalEvent("p", "執行指令", "bash", "ls", null, null, CanAllowForSession: true));
        h.Host.Notifications.Count.Should().Be(0);

        h.Host.IsForeground = false;
        h.Command(h.Main, "setting", new { key = "notifications", value = false });
        h.CompleteTurn(s);
        h.Host.Notifications.Count.Should().Be(0);
        h.Host.AttentionRequests.Should().BeGreaterThan(0, "仍會閃爍工作列");
    }

    [Fact]
    public async Task 通知_使用者在另一個視窗操作時_顯示這個對話的視窗不算看到()
    {
        // 主視窗關到系統匣，使用者在另一個視窗（彈出視窗）操作；主視窗分頁裡的對話要求核准
        using var h = new HubHarness();
        var other = h.AddView("popout");
        h.Host.ForegroundViewId = other.Id;
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });   // 主視窗的畫面上正顯示這個對話的分頁（網頁以 visible 回報）

        h.Raise(s, new AgentApprovalEvent("p", "執行指令", "bash", "ls", null, null, CanAllowForSession: true));

        h.Host.Notifications.All.Should().ContainSingle().Which.ConversationId.Should().Be(id);
        h.Host.AttentionViews.All.Should().ContainSingle().Which.Should().Equal(new[] { h.Main.Id }, "閃顯示這個對話的視窗");
        await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(i => i.Value<string>("id") == id && i.Value<bool>("unread") && i.Value<bool>("waiting")));

        // 使用者切回顯示這個對話的主視窗：之後的核准就算看到了，不再通知
        h.Host.ForegroundViewId = h.Main.Id;
        h.Raise(s, new AgentApprovalEvent("p2", "執行指令", "bash", "dir", null, null, CanAllowForSession: true));
        h.Host.Notifications.Count.Should().Be(1);
        h.Host.AttentionViews.Count.Should().Be(1);
    }

    [Fact]
    public async Task 未讀_回報visible的視窗不在前景時不清未讀_切到那個視窗才清()
    {
        // 對話開在彈出視窗、正顯示在它的畫面上，使用者在主視窗操作：完成算沒看到；
        // 背景的彈出視窗重送 visible（例如切分頁）也不清，要那個視窗回到前景（網頁在視窗取得焦點時重送）才清
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var popout = h.AddPopout("popout", id);
        h.Command(popout, "open", new { conversationId = id });
        h.Host.ForegroundViewId = h.Main.Id;
        h.Command(popout, "visible", new { conversationIds = new[] { id } });

        h.CompleteTurn(s);

        await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(i => i.Value<string>("id") == id && i.Value<bool>("unread")));
        h.Host.Notifications.All.Should().ContainSingle().Which.ConversationId.Should().Be(id);

        h.Command(popout, "visible", new { conversationIds = new[] { id } });
        await Task.Delay(150);
        await h.Ui.DrainAsync();
        h.Main.Last("conversations")!["items"]!.Single(i => i.Value<string>("id") == id).Value<bool>("unread").Should().BeTrue("彈出視窗還在背景，使用者沒看到");

        h.Host.ForegroundViewId = popout.Id;
        var mark = h.Main.Mark();
        h.Command(popout, "visible", new { conversationIds = new[] { id } });
        await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(i => i.Value<string>("id") == id && !i.Value<bool>("unread")), mark);
    }

    [Fact]
    public void 通知_這一輪失敗時_閃顯示這個對話的視窗()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var popout = h.AddPopout("popout", id);
        h.Command(popout, "open", new { conversationId = id });

        h.CompleteTurn(s, AgentTurnOutcome.Failed, "程序結束");

        h.Host.AttentionViews.All.Should().ContainSingle().Which.Should().BeEquivalentTo(new[] { h.Main.Id, popout.Id });
    }

    // ───── 設定與宿主操作 ─────

    [Fact]
    public void Setting_主題字級說明_存檔並廣播()
    {
        using var h = new HubHarness();
        var b = h.AddView("second");

        h.Command(h.Main, "setting", new { key = "theme", value = "light" });
        h.Command(h.Main, "setting", new { key = "theme", value = "粉紅" });
        h.Command(h.Main, "setting", new { key = "fontSize", value = 30 });
        h.Command(h.Main, "setting", new { key = "instructions", value = "  " + AgentHubRules.DefaultInstructions + "  " });
        h.Command(h.Main, "setting", new { key = "enterToSend", value = false });

        h.Host.Themes.All.Should().Equal("light");
        var settings = h.OnUi(() => h.Settings);
        settings.Theme.Should().Be("light");
        settings.FontSize.Should().Be(AppSettings.MaxFontSize);
        settings.Instructions.Should().BeNull("改回預設內容就存成 null");
        settings.EnterToSend.Should().BeFalse();
        b.Last("settings")!["settings"]!.Value<int>("fontSize").Should().Be(18);
        AppSettings.Load().Theme.Should().Be("light");
    }

    [Fact]
    public void Setting_繼續鈕的指令_只收字串_預設存成null_存檔並廣播()
    {
        using var h = new HubHarness();
        var b = h.AddView("second");
        h.OnUi(() => h.Settings.ContinueText).Should().BeNull("預設");

        h.Command(h.Main, "setting", new { key = "continueText", value = "  做完了嗎？沒做完就接著做  " });
        h.OnUi(() => h.Settings.ContinueText).Should().Be("做完了嗎？沒做完就接著做");
        b.Last("settings")!["settings"]!.Value<string>("continueText").Should().Be("做完了嗎？沒做完就接著做");
        AppSettings.Load().ContinueText.Should().Be("做完了嗎？沒做完就接著做");

        var mark = b.Mark();
        h.Command(h.Main, "setting", new { key = "continueText", value = 42 });
        h.OnUi(() => h.Settings.ContinueText).Should().Be("做完了嗎？沒做完就接著做", "不是字串不改");
        b.Messages("settings", mark).Should().BeEmpty("不是字串也不廣播");

        h.Command(h.Main, "setting", new { key = "continueText", value = "" });
        h.OnUi(() => h.Settings.ContinueText).Should().BeNull("清空就回到預設");
        b.Last("settings")!["settings"]!.Value<string>("continueText").Should().Be(AgentHubRules.DefaultContinueText);
        AppSettings.Load().ContinueText.Should().BeNull();
    }

    [Fact]
    public void Setting_動態效果_預設開_只收布林_存檔並廣播()
    {
        using var h = new HubHarness();
        h.OnUi(() => h.Settings.VisualEffects).Should().BeTrue("預設開");

        h.Command(h.Main, "setting", new { key = "visualEffects", value = false });
        h.OnUi(() => h.Settings.VisualEffects).Should().BeFalse();
        h.Main.Last("settings")!["settings"]!.Value<bool>("visualEffects").Should().BeFalse();
        AppSettings.Load().VisualEffects.Should().BeFalse();

        h.Command(h.Main, "setting", new { key = "visualEffects", value = true });
        h.OnUi(() => h.Settings.VisualEffects).Should().BeTrue();
        h.Command(h.Main, "setting", new { key = "visualEffects", value = "true" });
        h.OnUi(() => h.Settings.VisualEffects).Should().BeFalse("不是布林就當成關掉");
    }

    [Fact]
    public async Task 開檔與連結_白名單用預設程式開_其他只在檔案總管選取_UNC不開()
    {
        using var h = new HubHarness();
        var pdf = h.Temp.WriteFile(@"docs\報告.pdf");
        var exe = h.Temp.WriteFile(@"docs\setup.exe");

        h.Command(h.Main, "openPath", new { path = pdf });
        h.Command(h.Main, "openPath", new { path = exe });
        h.Command(h.Main, "openPath", new { path = @"\\evil\share\x.pdf" });
        h.Command(h.Main, "openLink", new { href = "https://example.com/a" });
        h.Command(h.Main, "openLink", new { href = "javascript:alert(1)" });
        h.Command(h.Main, "openLink", new { href = AgentMarkdownRenderer.OpenUrl(pdf) });
        h.Command(h.Main, "revealPath", new { path = pdf });
        h.Command(h.Main, "copyText", new { text = "複製這段" });

        // 本機路徑的存在檢查在背景執行緒（離線的網路磁碟不卡畫面），確認後才交給宿主，順序不固定
        await h.WaitUntilAsync(() => h.Host.Opened.Count == 3 && h.Host.Revealed.Count == 2, "背景確認存在後開檔與在檔案總管選取");
        h.Host.Opened.All.Should().BeEquivalentTo(new[] { pdf, "https://example.com/a", pdf });
        h.Host.Revealed.All.Should().BeEquivalentTo(new[] { exe, pdf });
        h.Host.Copied.All.Should().Equal("複製這段");
        h.Main.Messages("toast").Select(t => t.Value<string>("text")).Should().Contain("只能開啟這台電腦磁碟上的檔案");
    }

    [Fact]
    public void Popout_只有已開始的對話能彈出()
    {
        using var h = new HubHarness();
        var draft = h.NewConversation(h.Main, h.Claude);
        h.Command(h.Main, "popout", new { conversationId = draft });
        h.Host.Popouts.Count.Should().Be(0);

        var (id, _) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "popout", new { conversationId = id });
        h.Host.Popouts.All.Should().Equal(id);
    }

    [Fact]
    public async Task 檔案瀏覽與檔案提示_回應給發出要求的視窗()
    {
        using var h = new HubHarness();
        var folder = h.Temp.CreateDirectory("proj");
        h.Temp.WriteFile(@"proj\src\App.cs");
        h.Temp.WriteFile(@"proj\logo.png");
        var id = h.NewConversation(h.Main, h.Claude);
        await h.SetFolderAsync(h.Main, id, folder);

        h.Command(h.Main, "listFiles", new { requestId = "r1", conversationId = id, path = (string?)null });
        var files = await h.Main.WaitForAsync("files", m => m.Value<string>("requestId") == "r1");
        files.Value<string>("root").Should().Be(folder);
        files["entries"]!.Select(e => e.Value<string>("name")).Should().Equal("src", "logo.png");
        files["entries"]![1]!.Value<string>("thumbUrl").Should().StartWith("https://myriagent.local/thumb?path=");
        h.OnUi(() => h.Hub.IsMediaAllowed(Path.Combine(folder, "logo.png"))).Should().BeTrue("檔案瀏覽器列出的圖片可以直接開");

        h.Command(h.Main, "fileSearch", new { requestId = "r2", conversationId = id, query = "app" });
        var search = await h.Main.WaitForAsync("fileSearch", m => m.Value<string>("requestId") == "r2");
        search["results"]!.Single().Value<string>("relativePath").Should().Be(@"src\App.cs");

        h.Command(h.Main, "listFiles", new { requestId = "r3", conversationId = id, path = @"\\server\share" });
        var error = await h.Main.WaitForAsync("files", m => m.Value<string>("requestId") == "r3");
        error.Value<string>("error").Should().NotBeNullOrWhiteSpace();
        error["entries"]!.Should().BeEmpty();
    }

    // ───── 執行狀態、未讀、關閉 ─────

    [Fact]
    public void 執行中計數_依代理種類與帳戶()
    {
        using var h = new HubHarness();
        var (_, s) = h.StartConversation(h.Main, h.Claude);

        h.OnUi(() => h.Hub.HasRunningSessions(AgentProvider.Claude)).Should().BeTrue();
        h.OnUi(() => h.Hub.HasRunningSessions(AgentProvider.Codex)).Should().BeFalse();
        h.OnUi(() => h.Hub.RunningForAccount(h.Claude.Id)).Should().Be(1);
        h.OnUi(() => h.Hub.RunningForAccount(h.Claude2.Id)).Should().Be(0);
        h.Host.Tray.All.Last().Running.Should().Be(1, "系統匣顯示執行中數量");

        h.CompleteTurn(s);

        h.OnUi(() => h.Hub.HasRunningSessions(AgentProvider.Claude)).Should().BeFalse("CLI 自動更新只在沒有執行中的對話時進行");
        h.Host.Tray.All.Last().Running.Should().Be(0);
    }

    [Fact]
    public void 換帳戶後_舊會話晚到的事件一律忽略()
    {
        using var h = new HubHarness();
        var (id, s1) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s1);
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id });
        var count = h.Items(id).Count;

        h.Raise(s1, new AgentTextCompletedEvent("late:1", "舊程序晚到的回覆"));
        h.Raise(s1, new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, "舊程序結束", null));

        h.Items(id).Should().HaveCount(count);
        h.Items(id).Should().NotContain(i => i.Text.Contains("舊程序"));
    }

    [Fact]
    public async Task 背景完成的對話標為未讀_開分頁還不算看到_顯示在畫面上才清掉()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Command(h.Main, "close", new { conversationId = id });
        h.CompleteTurn(s);
        await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(i => i.Value<string>("id") == id && i.Value<bool>("unread")));

        // 還原版面時背景分頁也會 open：還沒顯示在畫面上，未讀保留
        h.Command(h.Main, "open", new { conversationId = id });
        await Task.Delay(150);
        await h.Ui.DrainAsync();
        h.Main.Last("conversations")!["items"]!.Single(i => i.Value<string>("id") == id).Value<bool>("unread").Should().BeTrue();

        var mark = h.Main.Mark();
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });

        await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(i => i.Value<string>("id") == id && !i.Value<bool>("unread")), mark);
    }

    [Fact]
    public void 代理產出的媒體事件_檔案存在才加一筆()
    {
        using var h = new HubHarness();
        var video = h.Temp.WriteFile(@"out\demo.mp4", "mp4");
        var (id, s) = h.StartConversation(h.Main, h.Claude);

        h.Raise(s, new AgentMediaEvent(video));
        h.Raise(s, new AgentMediaEvent(@"C:\不存在\x.png"));
        h.Raise(s, new AgentMediaEvent(@"\\evil\share\y.png"));

        var media = h.Items(id).Where(i => i.MediaPaths.Count > 0).ToList();
        media.Should().ContainSingle();
        media[0].Kind.Should().Be(AgentItemKind.Assistant);
        media[0].MediaPaths.Should().Equal(video);
    }

    [Fact]
    public async Task ShutdownAsync_關掉所有會話_刪除草稿_之後的指令不處理()
    {
        using var h = new HubHarness();
        var (running, sRunning) = h.StartConversation(h.Main, h.Claude, "還在跑");
        var (idle, sIdle) = h.StartConversation(h.Main, h.Codex, "已完成");
        h.CompleteTurn(sIdle);
        var draft = h.NewConversation(h.Main, h.Claude2);
        var file = h.Temp.WriteFile(@"in\a.txt");
        var mark = h.Main.Mark();
        h.Command(h.Main, "addFiles", new { conversationId = draft }, new[] { file });
        await h.Main.WaitForAsync("composer", m => m["composer"]!["attachments"]!.Count() == 1, mark, conversationId: draft);

        await h.Ui.Invoke(() => h.Hub.ShutdownAsync());

        sRunning.Disposed.Should().BeTrue("執行中的直接結束");
        sIdle.Closes.Should().Be(1, "閒置的正常關閉");
        sIdle.Disposed.Should().BeTrue();
        Directory.Exists(h.Store.GetConversationDirectory(draft)).Should().BeFalse("未送出的草稿刪除");
        new ConversationStore(h.Store.RootDirectory).LoadItems(running).Should().ContainSingle("執行中的對話先存檔");
        h.Store.LoadConversations().Select(c => c.Id).Should().BeEquivalentTo(new[] { running, idle });

        var before = h.Main.Mark();
        h.Command(h.Main, "ready");
        h.Main.Mark().Should().Be(before, "關閉後不再處理網頁指令");
    }

    // ───── 重新載入與分頁 ─────

    [Fact]
    public void 重新載入_上次沒收尾的等待卡片與執行中工具改為取消_非同步提問保留()
    {
        AgentConversation? meta = null;
        using var h = new HubHarness(store =>
        {
            meta = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "舊對話", SessionId = Guid.NewGuid().ToString(), SessionStarted = true };
            store.SaveConversation(meta);
            store.SaveItems(meta.Id, new[]
            {
                new AgentChatItem { Kind = AgentItemKind.Approval, Status = AgentItemStatus.Waiting, Title = "rm" },
                new AgentChatItem { Kind = AgentItemKind.Tool, Status = AgentItemStatus.Running, Title = "ls" },
                new AgentChatItem { Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Running, Text = "寫到一半" },
                new AgentChatItem { Kind = AgentItemKind.Question, Status = AgentItemStatus.Waiting, AnswerAsMessage = true },
            });
        });

        h.Command(h.Main, "open", new { conversationId = meta!.Id });

        var items = h.Items(meta.Id);
        items[0].Status.Should().Be(AgentItemStatus.Cancelled);
        items[1].Status.Should().Be(AgentItemStatus.Cancelled);
        items[2].Status.Should().Be(AgentItemStatus.Done);
        items[3].Status.Should().Be(AgentItemStatus.Waiting, "非同步提問改用下一則訊息回答，重開後仍可回答");
        h.Main.Last("conversation", meta.Id)!["header"]!.Value<string>("title").Should().Be("舊對話");
    }

    [Fact]
    public void 長對話分頁載入_開啟時只送最後80筆_往上捲再載()
    {
        AgentConversation? meta = null;
        using var h = new HubHarness(store =>
        {
            meta = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "長對話" };
            store.SaveConversation(meta);
            store.SaveItems(meta.Id, Enumerable.Range(1, 85).Select(i => new AgentChatItem { Kind = AgentItemKind.User, Text = $"第 {i} 則" }).ToList());
        });

        h.Command(h.Main, "open", new { conversationId = meta!.Id });
        var conversation = h.Main.Last("conversation", meta.Id)!;
        conversation["items"]!.Should().HaveCount(AgentHub.PageSize);
        conversation.Value<bool>("hasMore").Should().BeTrue();
        var firstShown = conversation["items"]![0]!.Value<string>("id");

        h.Command(h.Main, "loadOlder", new { conversationId = meta.Id, beforeId = firstShown });

        var older = h.Main.Last("olderItems", meta.Id)!;
        older["items"]!.Should().HaveCount(5);
        older["items"]![0]!.Value<string>("html").Should().Be("第 1 則");
        older.Value<bool>("hasMore").Should().BeFalse();
        // beforeId 原樣送回：網頁只採用自己還在等的那一頁（WP4.6 合併前審查 LIVE-2）
        older.Value<string>("beforeId").Should().Be(firstShown);
    }

    [Fact]
    public void 最近的指令_長的一輪不在已載入的那一頁時由標頭附上()
    {
        AgentConversation? meta = null;
        var prompt = new AgentChatItem { Kind = AgentItemKind.User, Text = "把報表改成月結\n並寄給會計", CreatedAt = new DateTime(2026, 10, 21, 14, 5, 0) };
        prompt.Attachments.Add(new AgentAttachment { FileName = "報表.xlsx", StoredPath = @"C:\x\報表.xlsx" });
        using var h = new HubHarness(store =>
        {
            meta = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "長的一輪" };
            store.SaveConversation(meta);
            var items = new List<AgentChatItem> { new() { Kind = AgentItemKind.User, Text = "更早的指令" }, prompt };
            items.AddRange(Enumerable.Range(1, AgentHub.PageSize + 5).Select(i => new AgentChatItem { Kind = AgentItemKind.Tool, Title = $"第 {i} 步", Status = AgentItemStatus.Done }));
            store.SaveItems(meta.Id, items);
        });

        h.Command(h.Main, "open", new { conversationId = meta!.Id });
        var conversation = h.Main.Last("conversation", meta.Id)!;
        conversation["items"]!.Select(i => i.Value<string>("kind")).Should().NotContain("User", "那則指令不在最後一頁");
        var last = conversation["header"]!["lastPrompt"]!;
        last.Value<string>("id").Should().Be(prompt.Id);
        last.Value<string>("text").Should().Be("把報表改成月結\n並寄給會計");
        last.Value<long>("time").Should().Be(AgentHub.ToUnixMs(prompt.CreatedAt)!.Value);
        last.Value<int>("attachments").Should().Be(1);
        last["seq"]!.Type.Should().Be(JTokenType.Integer);
    }

    [Fact]
    public void 歷史指令_問了才給完整文字_只回給問的視窗_app重開後照樣在()
    {
        // 歷史指令的來源是對話的 items：存檔之後新開一個中樞（＝app 重開）照樣拿得到，不在已載入的那一頁也拿得到
        AgentConversation? meta = null;
        var longText = "很長的指令" + new string('長', AgentHubRules.LastPromptMaxChars + 50);
        var withFile = new AgentChatItem { Kind = AgentItemKind.User, Text = "附檔的指令", CreatedAt = new DateTime(2026, 10, 25, 9, 0, 0) };
        withFile.Attachments.Add(new AgentAttachment { FileName = "報表.xlsx", StoredPath = @"C:\x\報表.xlsx" });
        using var h = new HubHarness(store =>
        {
            meta = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "歷史指令" };
            store.SaveConversation(meta);
            var items = Enumerable.Range(1, 12).Select(i => new AgentChatItem { Kind = AgentItemKind.User, Text = $"第 {i} 則" }).ToList();
            items.Add(withFile);
            items.Add(new AgentChatItem { Kind = AgentItemKind.User, Text = longText });
            items.AddRange(Enumerable.Range(1, AgentHub.PageSize + 5).Select(i => new AgentChatItem { Kind = AgentItemKind.Tool, Title = $"第 {i} 步", Status = AgentItemStatus.Done }));
            store.SaveItems(meta.Id, items);
        });

        h.Command(h.Main, "open", new { conversationId = meta!.Id });
        var other = h.AddView("other");
        var mark = h.Main.Mark();
        h.Command(h.Main, "promptHistory", new { conversationId = meta.Id, requestId = "ph1" });
        var reply = h.Main.Messages("promptHistory", mark, meta.Id).Should().ContainSingle().Subject;
        reply.Value<string>("requestId").Should().Be("ph1");
        var prompts = (JArray)reply["prompts"]!;
        prompts.Select(p => p.Value<string>("text")).Should().Equal(
            [longText, "附檔的指令", .. Enumerable.Range(5, 8).Reverse().Select(i => $"第 {i} 則")], "最新的在前、最多 10 則，完整文字（不像 lastPrompt 截斷）");
        prompts[1]!.Value<int>("attachments").Should().Be(1);
        prompts[1]!.Value<long>("time").Should().Be(AgentHub.ToUnixMs(withFile.CreatedAt)!.Value);
        prompts[1]!.Value<string>("id").Should().Be(withFile.Id);
        other.Messages("promptHistory").Should().BeEmpty("只回給問的視窗");

        h.Command(h.Main, "promptHistory", new { conversationId = "nope", requestId = "ph2" });
        h.Main.Messages("promptHistory").Should().HaveCount(1, "不認得的對話不回");
    }

    [Fact]
    public async Task PromptHistory_其他電腦的對話_照這台有的副本項目回()
    {
        // 2026-11-12：副本的指令白名單漏了 promptHistory，網頁等不到回覆、空等 1.5 秒才用已載入的項目開清單
        using var h = new HubHarness();
        var replica = new string('b', 32);
        h.Ui.Invoke(() =>
        {
            h.Hub.ShowReplicas = true;
            h.Hub.ApplyReplicaDelta(new ReplicaDelta(
                [new ReplicaMeta(replica, "d_" + new string('B', 26), new AgentConversation { Id = replica, Provider = AgentProvider.Claude, Title = "別台的對話", AccountId = string.Empty },
                    Running: false, Waiting: false, ItemsAvailable: true)],
                [],
                [new ReplicaItems(replica, Reset: true,
                    [
                        new AgentChatItem { Kind = AgentItemKind.User, Text = "別台送的第一則" },
                        new AgentChatItem { Kind = AgentItemKind.Assistant, Text = "回覆" },
                        new AgentChatItem { Kind = AgentItemKind.User, Text = "別台送的第二則" },
                    ], [])]));
        });
        await h.Ui.DrainAsync();
        h.OnUi(() => h.Hub.IsReplicaConversation(replica)).Should().BeTrue("前提：是其他電腦的對話");
        var mark = h.Main.Mark();

        h.Command(h.Main, "promptHistory", new { conversationId = replica, requestId = "ph-r" });

        var reply = h.Main.Messages("promptHistory", mark, replica).Should().ContainSingle().Subject;
        reply.Value<string>("requestId").Should().Be("ph-r");
        ((JArray)reply["prompts"]!).Select(p => p.Value<string>("text")).Should().Equal("別台送的第二則", "別台送的第一則");
        h.Main.Messages("toast", mark).Should().BeEmpty("只讀，不提示唯讀");
    }

    [Fact]
    public async Task 項目帶建立順序_系統時間往回調後新項目仍排在最後()
    {
        // 網頁只載入最後 80 筆時，靠 seq 分辨「新項目」與「還沒載入的較早項目」。
        // 以前比 time（建立當下的系統時間）：時鐘往回調之後，新項目的 time 比已載入的第一筆還早，全被網頁當成較早項目丟掉
        AgentConversation? meta = null;
        var clockAhead = DateTime.Now.AddDays(1);
        using var h = new HubHarness(store =>
        {
            meta = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "時鐘快了一天時的對話", SessionId = Guid.NewGuid().ToString(), SessionStarted = true };
            store.SaveConversation(meta);
            store.SaveItems(meta.Id, Enumerable.Range(1, 85).Select(i => new AgentChatItem { Kind = AgentItemKind.User, Text = $"第 {i} 則", CreatedAt = clockAhead.AddMinutes(i) }).ToList());
        });

        h.Command(h.Main, "open", new { conversationId = meta!.Id });
        var page = h.Main.Last("conversation", meta.Id)!["items"]!;
        page.Select(i => i.Value<long?>("seq")).Should().Equal(Enumerable.Range(6, 80).Select(i => (long?)i), "依存檔順序從 1 編號，最後一頁是第 6～85 筆");
        h.Command(h.Main, "loadOlder", new { conversationId = meta.Id, beforeId = page[0]!.Value<string>("id") });
        h.Main.Last("olderItems", meta.Id)!["items"]!.Select(i => i.Value<long?>("seq")).Should().Equal(1L, 2L, 3L, 4L, 5L);

        h.Send(h.Main, meta.Id, "時鐘校正後送出");

        var added = (await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("html")!.Contains("時鐘校正後送出"), conversationId: meta.Id))["item"]!;
        added.Value<long>("time").Should().BeLessThan(page[0]!.Value<long>("time"), "新項目的系統時間比已載入的第一筆還早（重現時鐘往回調）");
        added.Value<long?>("seq").Should().Be(86, "建立順序不受系統時間影響，接在最後一筆之後");
    }

    [Fact]
    public void 項目帶建立順序_紀錄檔讀不到期間加的項目_讀得到後接在最後也重新編號()
    {
        // 紀錄檔暫時讀不到時加進暫時清單的項目（換帳戶的提示）已經編了號，讀得到後接在載入的內容後面：
        // 建立順序要跟著清單順序往後重編，否則它的 seq 比載入的內容小，網頁會把它當成還沒載入的較早項目
        AgentConversation? meta = null;
        string? itemsPath = null;
        using var h = new HubHarness(store =>
        {
            meta = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "被占用的對話", SessionId = Guid.NewGuid().ToString(), SessionStarted = true };
            store.SaveConversation(meta);
            store.SaveItems(meta.Id, Enumerable.Range(1, 3).Select(i => new AgentChatItem { Kind = AgentItemKind.User, Text = $"第 {i} 則" }).ToList());
            itemsPath = Path.Combine(store.GetConversationDirectory(meta.Id), "items.json");
        });

        using (new FileStream(itemsPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            h.Command(h.Main, "open", new { conversationId = meta!.Id });
            h.Command(h.Main, "setAccount", new { conversationId = meta.Id, accountId = h.Claude2.Id });
        }

        h.Command(h.Main, "open", new { conversationId = meta.Id });

        var items = h.Items(meta.Id);
        items.Select(i => i.Kind).Should().Equal(AgentItemKind.User, AgentItemKind.User, AgentItemKind.User, AgentItemKind.Notice);
        items.Select(i => i.Seq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        var page = h.Main.Last("conversation", meta.Id)!["items"]!;
        page.Select(i => i.Value<long>("seq")).Should().Equal(items.Select(i => i.Seq), "送給網頁的 seq 與清單順序一致");
    }

    // ───── 損毀與讀不到的紀錄檔 ─────

    [Fact]
    public void 損毀的ItemsJson_原檔改名備份不被覆寫_畫面加錯誤說明_之後照常存新檔()
    {
        const string broken = "[{\"Kind\":\"User\",\"Text\":\"寫到一半就當";
        AgentConversation? meta = null;
        string? itemsPath = null;
        using var h = new HubHarness(store =>
        {
            meta = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "壞掉的對話", SessionId = Guid.NewGuid().ToString(), SessionStarted = true };
            store.SaveConversation(meta);
            itemsPath = Path.Combine(store.GetConversationDirectory(meta.Id), "items.json");
            File.WriteAllText(itemsPath, broken);
        });

        h.Command(h.Main, "open", new { conversationId = meta!.Id });

        var notice = h.Items(meta.Id).Single();
        notice.Kind.Should().Be(AgentItemKind.Error);
        notice.Text.Should().Contain("紀錄檔已損毀").And.Contain(".corrupt-");
        var backup = Directory.GetFiles(Path.GetDirectoryName(itemsPath)!, "items.json.corrupt-*").Should().ContainSingle().Subject;
        File.ReadAllText(backup).Should().Be(broken, "原本的內容完整保留在備份檔");

        h.Send(h.Main, meta.Id, "繼續");

        File.ReadAllText(backup).Should().Be(broken, "之後的存檔寫到新的 items.json，不碰備份檔");
        var saved = new ConversationStore(h.Store.RootDirectory).TryLoadItems(meta.Id);
        saved.Status.Should().Be(JsonReadStatus.Ok);
        saved.Items.Select(i => i.Kind).Should().Equal(AgentItemKind.Error, AgentItemKind.User);
    }

    [Fact]
    public void 讀不到的ItemsJson_不標記已載入_不用空清單覆寫原檔_之後讀得到再載入()
    {
        AgentConversation? meta = null;
        string? itemsPath = null;
        using var h = new HubHarness(store =>
        {
            meta = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "被占用的對話" };
            store.SaveConversation(meta);
            store.SaveItems(meta.Id, new[] { new AgentChatItem { Kind = AgentItemKind.User, Text = "原本的內容" } });
            itemsPath = Path.Combine(store.GetConversationDirectory(meta.Id), "items.json");
        });
        var original = File.ReadAllBytes(itemsPath!);

        using (new FileStream(itemsPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            h.Command(h.Main, "open", new { conversationId = meta!.Id });
            h.Main.Last("toast")!.Value<string>("text").Should().Contain("暫時讀不到");
            h.Command(h.Main, "rename", new { conversationId = meta.Id, title = "改名會存 meta，但不能存 items" });
        }

        File.ReadAllBytes(itemsPath!).Should().Equal(original, "讀不到時絕不能用空清單覆寫原檔");
        h.Items(meta.Id).Select(i => i.Text).Should().Equal("原本的內容");
    }

    // ───── 其他 ─────

    [Fact]
    public void Ready_網頁重新載入時清掉這個視窗之前開著的對話_沒人顯示的草稿丟棄()
    {
        using var h = new HubHarness();
        var b = h.AddView("second");
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var draft = h.NewConversation(h.Main, h.Codex);
        var shared = h.NewConversation(h.Main, h.Claude2);
        h.Command(b, "open", new { conversationId = shared });

        h.Command(h.Main, "ready");

        h.Conversation(draft).Should().BeNull("只有這個視窗顯示的草稿丟棄");
        h.Conversation(shared).Should().NotBeNull("其他視窗還開著");
        h.Conversation(id).Should().NotBeNull();
        var mark = h.Main.Mark();
        h.Raise(s, new AgentTextCompletedEvent("m:1", "之後的更新"));
        Thread.Sleep(200);
        h.Main.Messages("item", mark, id).Should().BeEmpty("網頁重新載入後要重新 open 才會收到更新");
    }

    [Fact]
    public void 附加檔案_網路共用路徑不碰檔案系統_直接提示()
    {
        using var h = new HubHarness();
        var id = h.NewConversation(h.Main, h.Claude);

        h.Command(h.Main, "addFiles", new { conversationId = id }, new[] { @"\\evil\share\a.png", "//evil/share/b.png" });

        h.Main.Last("toast")!.Value<string>("text").Should().Contain("網路共用資料夾");
        h.Main.Messages("composer", conversationId: id).Should().BeEmpty("沒有任何檔案進入匯入流程");
    }

    [Fact]
    public async Task 核准卡_畫面帶上本對話都允許的範圍說明()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);

        h.Raise(s, new AgentApprovalEvent("p-1", "執行指令", "bash", "npm test", null, "t1", CanAllowForSession: true, AllowSessionLabel: "本對話都允許 Bash(npm test:*)"));
        h.Raise(s, new AgentApprovalEvent("p-2", "執行指令", "bash", "ls", null, "t2", CanAllowForSession: true));

        h.Items(id).Single(i => i.RequestKey == "p-1").AllowSessionLabel.Should().Be("本對話都允許 Bash(npm test:*)");
        var first = await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("title") == "npm test", conversationId: id);
        first["item"]!.Value<string>("allowSessionLabel").Should().Be("本對話都允許 Bash(npm test:*)");
        var second = await h.Main.WaitForAsync("item", m => m["item"]!.Value<string>("title") == "ls", conversationId: id);
        second["item"]!["allowSessionLabel"].Should().BeNull("沒有說明就不帶，網頁用預設文字");
    }

    [Fact]
    public void SetOption_權限選預設時回到設定頁的預設()
    {
        using var h = new HubHarness();
        var first = h.NewConversation(h.Main, h.Claude);
        h.Command(h.Main, "setOption", new { conversationId = first, key = "permission", value = "FullAccess" });
        var second = h.NewConversation(h.Main, h.Claude);
        h.Conversation(second)!.PermissionMode.Should().Be(AgentPermissionMode.Ask, "對話裡的選擇不影響新對話");
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "permission", value = "AcceptEdits" });

        h.Command(h.Main, "setOption", new { conversationId = first, key = "permission", value = "default" });

        h.Conversation(first)!.PermissionMode.Should().Be(AgentPermissionMode.AcceptEdits, "「default」＝設定頁目前的預設");
        h.Conversation(second)!.PermissionMode.Should().Be(AgentPermissionMode.Ask, "改設定頁的預設不動已開的對話");
    }

    [Fact]
    public async Task accountLogin_method_deviceCode轉給假app_server()
    {
        using var h = new HubHarness();
        using var fake = new FakeCodexAppServer("ok");
        h.Ui.Invoke(() =>
        {
            h.Accounts.ResolveLauncher = p => p switch { AgentProvider.Codex => fake.Launcher, _ => null };
            h.Accounts.OpenBrowser = _ => throw new InvalidOperationException("裝置碼登入不開瀏覽器");
        });

        h.Command(h.Main, "accountLogin", new { accountId = h.Codex.Id, method = "deviceCode" });

        await fake.WaitForAsync(c => c.Contains("account/login/start type=chatgptDeviceCode"));
        h.OnUi(() => h.Accounts.LastLoginRequest).Should().Be((LoginMethod.DeviceCode, false));
        await h.Main.WaitForAsync("accountLogin", m => m["deviceCode"] is JObject, timeoutMs: 30000);
        h.Command(h.Main, "accountLoginCancel", new { accountId = h.Codex.Id });
        await h.Main.WaitForAsync("accountLogin", m => m.Value<bool>("done"), timeoutMs: 30000);
        fake.Calls.Should().Contain("account/login/cancel loginId=" + FakeCodexAppServer.LoginId);
    }

    [Theory]
    [InlineData("DeviceCode")]
    [InlineData("default")]
    [InlineData(1)]
    [InlineData(true)]
    public async Task accountLogin_method是其他值_忽略(object method)
    {
        using var h = new HubHarness();
        using var fake = new FakeCodexAppServer("ok");
        h.Ui.Invoke(() => h.Accounts.ResolveLauncher = _ => fake.Launcher);

        h.Command(h.Main, "accountLogin", new { accountId = h.Codex.Id, method });
        await h.Ui.DrainAsync();
        await Task.Delay(300);

        h.OnUi(() => h.Accounts.LastLoginRequest).Should().BeNull("看不懂的 method 整個指令忽略，不落進別種登入");
        h.Main.Messages("accountLogin").Should().BeEmpty();
        fake.Started.Should().BeFalse();
    }

    [Fact]
    public async Task accountLogin_沒帶method或是null_照舊一般登入()
    {
        using var h = new HubHarness();
        h.Ui.Invoke(() => h.Accounts.ResolveLauncher = _ => null);

        h.Command(h.Main, "accountLogin", new { accountId = h.Codex.Id });
        h.OnUi(() => h.Accounts.LastLoginRequest).Should().Be((LoginMethod.Default, false));
        await h.Main.WaitForAsync("accountLogin", m => m.Value<bool>("done"));

        // 第二次登入也以「尚未安裝」結束：有這則 done 才證明 method:null 真的進了 LoginAsync（LastLoginRequest 第一次就已經是同一個值）
        var mark = h.Main.Mark();
        h.Command(h.Main, "accountLogin", new { accountId = h.Codex.Id, method = (string?)null });
        await h.Main.WaitForAsync("accountLogin", m => m.Value<bool>("done"), since: mark);
        h.OnUi(() => h.Accounts.LastLoginRequest).Should().Be((LoginMethod.Default, false), "JSON null 當成沒帶");
    }

    [Fact]
    public async Task ready_裝置碼登入進行中_對這個視窗重送驗證碼_彈出視窗與沒有登入時不送()
    {
        using var h = new HubHarness();
        using var fake = new FakeCodexAppServer("ok");
        h.Ui.Invoke(() =>
        {
            h.Accounts.ResolveLauncher = p => p switch { AgentProvider.Codex => fake.Launcher, _ => null };
            h.Accounts.OpenBrowser = _ => throw new InvalidOperationException("裝置碼登入不開瀏覽器");
        });
        var popout = h.AddPopout("popout", "c1");

        var mark = h.Main.Mark();
        h.Command(h.Main, "ready");
        h.Main.Messages("accountLogin", mark).Should().BeEmpty("沒有進行中的裝置碼登入");

        h.Command(h.Main, "accountLogin", new { accountId = h.Codex.Id, method = "deviceCode" });
        await h.Main.WaitForAsync("accountLogin", m => m["deviceCode"] is JObject, timeoutMs: 30000);

        // 網頁重新載入（渲染程序重啟、重新整理）：init 之後重送一則進度，帶 deviceCode
        mark = h.Main.Mark();
        h.Command(h.Main, "ready");
        var after = h.Main.All().Skip(mark).Select(m => m.Value<string>("type")).ToList();
        after.IndexOf("init").Should().BeGreaterThanOrEqualTo(0);
        after.IndexOf("accountLogin").Should().BeGreaterThan(after.IndexOf("init"), "init 之後才重送（網頁在 init 重置登入狀態）");
        var replay = h.Main.Messages("accountLogin", mark).Single();
        replay.Value<string>("accountId").Should().Be(h.Codex.Id);
        replay.Value<bool>("done").Should().BeFalse();
        replay["success"]!.Type.Should().Be(JTokenType.Null);
        replay["deviceCode"]!.Value<string>("userCode").Should().Be(FakeCodexAppServer.UserCode);
        replay["deviceCode"]!.Value<string>("verificationUrl").Should().Be(FakeCodexAppServer.VerificationUrl);
        replay.Value<string>("message").Should().Contain(FakeCodexAppServer.UserCode);

        var popoutMark = popout.Mark();
        h.Command(popout, "ready");
        popout.Messages("accountLogin", popoutMark).Should().BeEmpty("彈出視窗不顯示帳戶卡");

        h.Command(h.Main, "accountLoginCancel", new { accountId = h.Codex.Id });
        await h.Main.WaitForAsync("accountLogin", m => m.Value<bool>("done"), timeoutMs: 30000);
        mark = h.Main.Mark();
        h.Command(h.Main, "ready");
        h.Main.Messages("accountLogin", mark).Should().BeEmpty("登入結束後驗證碼已清掉");
    }
}
