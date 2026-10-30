using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 標題右鍵選單的「自動重新命名」（autoTitle）：中樞把節錄交給產生器（測試換成假的，不跑 CLI）、產生期間標頭 titling、結果照 rename 改名；
/// 失敗或不能呼叫模型時改用退而求其次的標題；產生期間標題被改過不蓋掉；草稿、重複按、對話已刪除不做事
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubAutoTitleTests
{
    private static readonly AgentLauncher FakeLauncher = new(@"C:\fake\cli.exe", Array.Empty<string>());

    /// <summary>假產生器（中樞在背景執行緒呼叫）：記下收到的要求，結果由測試決定什麼時候給</summary>
    private sealed class FakeGenerator
    {
        private readonly List<AutoTitleRequest> requests = new();

        public IReadOnlyList<AutoTitleRequest> Requests
        {
            get
            {
                lock (requests)
                    return requests.ToList();
            }
        }

        public TaskCompletionSource<AutoTitleResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>依帳戶立即回的結果（設了就不用 Result）</summary>
        public Func<AutoTitleRequest, AutoTitleResult>? Respond { get; set; }

        public Task<AutoTitleResult> RunAsync(AutoTitleRequest request, CancellationToken ct)
        {
            lock (requests)
                requests.Add(request);
            return Respond != null ? Task.FromResult(Respond(request)) : Result.Task;
        }

        /// <summary>等到收到第 count 個要求，回傳它</summary>
        public async Task<AutoTitleRequest> WaitAsync(int count = 1)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Requests.Count < count)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"等不到第 {count} 個產生標題的要求");
                await Task.Delay(10);
            }
            return Requests[count - 1];
        }
    }

    private static FakeGenerator Install(HubHarness h)
    {
        var generator = new FakeGenerator();
        h.Hub.TitleGenerator = generator.RunAsync;
        h.Hub.TitleLauncher = _ => FakeLauncher;
        return generator;
    }

    private static Task<JObject> WaitTitleAsync(HubHarness h, string id, string title, int since)
        => h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("title") == title && m["header"]!.Value<bool>("titling") == false, since,
            timeoutMs: 5000, conversationId: id);

    [Fact]
    public async Task Claude對話_節錄交給haiku產生_產生期間轉圈_結果改成標題並送給會話()
    {
        using var h = new HubHarness();
        var generator = Install(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude, "幫我修登入頁的錯誤");
        h.Raise(s, new AgentTextCompletedEvent("b1", "找到原因了，正在修正。"));
        h.CompleteTurn(s);
        var mark = h.Main.Mark();

        h.Command(h.Main, "autoTitle", new { conversationId = id });

        var request = await generator.WaitAsync();
        request.Method.Should().Be(AutoTitleMethod.ClaudePrint);
        request.Launcher.Should().Be(FakeLauncher);
        request.AccountLabel.Should().Be("Claude 主帳戶", "先用對話自己的帳戶");
        request.Excerpt.Should().Contain(AutoTitleRules.LatestRequestLabel + "幫我修登入頁的錯誤").And.Contain("【代理】找到原因了，正在修正。");
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("titling"), mark, conversationId: id); // 產生期間標題後面轉圈

        generator.Result.SetResult(new AutoTitleResult("修正登入頁錯誤", null));

        await WaitTitleAsync(h, id, "修正登入頁錯誤", mark);
        h.Conversation(id)!.Title.Should().Be("修正登入頁錯誤");
        h.Store.LoadConversations().Single(c => c.Id == id).Title.Should().Be("修正登入頁錯誤", "存檔（同步也看 meta）");
        s.Renames.All.Should().Equal("修正登入頁錯誤");
        await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(c => c.Value<string>("title") == "修正登入頁錯誤"), mark);
    }

    [Fact]
    public async Task 側欄對沒開著的對話產生_從存檔節錄_清單的titling先轉圈再換成新標題()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "幫我修登入頁的錯誤");
        h.Raise(s, new AgentTextCompletedEvent("b1", "找到原因了，正在修正。"));
        h.CompleteTurn(s);
        await h.RestartAsync(); // 重開 app：對話沒開著、紀錄還沒載入
        h.Command(h.Main, "ready");
        await h.Ui.DrainAsync();
        var generator = Install(h);
        var mark = h.Main.Mark();

        h.Command(h.Main, "autoTitle", new { conversationId = id });

        (await generator.WaitAsync()).Excerpt.Should().Contain(AutoTitleRules.LatestRequestLabel + "幫我修登入頁的錯誤").And.Contain("【代理】找到原因了，正在修正。");
        await h.Main.WaitForAsync("conversations", m => Summary(m, id)?.Value<bool>("titling") == true, mark); // 側欄這一列轉圈
        generator.Result.SetResult(new AutoTitleResult("修正登入頁錯誤", null));

        await h.Main.WaitForAsync("conversations", m => Summary(m, id) is { } c && c.Value<string>("title") == "修正登入頁錯誤" && !c.Value<bool>("titling"),
            mark, timeoutMs: 5000);
        h.Store.LoadConversations().Single(c => c.Id == id).Title.Should().Be("修正登入頁錯誤");
        h.Ui.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Claude對話的程序開著_先在程序裡產生_不另開CLI()
    {
        using var h = new HubHarness();
        var generator = Install(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude, "幫我修登入頁的錯誤");
        h.CompleteTurn(s);
        s.LiveTitle = "修正登入頁錯誤";
        var mark = h.Main.Mark();

        h.Command(h.Main, "autoTitle", new { conversationId = id });

        await WaitTitleAsync(h, id, "修正登入頁錯誤", mark);
        s.TitleRequests.All.Should().ContainSingle().Which.Should().Contain("幫我修登入頁的錯誤");
        generator.Requests.Should().BeEmpty("程序裡產生了就不另開 CLI");
    }

    [Fact]
    public async Task 程序內沒有新的標題_改用CLI產生()
    {
        using var h = new HubHarness();
        var generator = Install(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude, "幫我修登入頁的錯誤");
        h.CompleteTurn(s);
        s.LiveTitle = "";   // 同一個程序第二次問：CLI 回快取的舊標題，會話當成沒有
        generator.Respond = _ => new AutoTitleResult("修正登入頁錯誤", null);
        var mark = h.Main.Mark();

        h.Command(h.Main, "autoTitle", new { conversationId = id });

        await WaitTitleAsync(h, id, "修正登入頁錯誤", mark);
        s.TitleRequests.All.Should().ContainSingle();
        generator.Requests.Should().ContainSingle().Which.Method.Should().Be(AutoTitleMethod.ClaudePrint);
    }

    private static JToken? Summary(JObject conversations, string id) => conversations["items"]!.FirstOrDefault(c => c.Value<string>("id") == id);

    [Fact]
    public async Task Codex對話_用codex_exec產生()
    {
        using var h = new HubHarness();
        var generator = Install(h);
        var (id, s) = h.StartConversation(h.Main, h.Codex, "把報表匯出成 Excel");
        h.CompleteTurn(s);
        var mark = h.Main.Mark();

        h.Command(h.Main, "autoTitle", new { conversationId = id });
        (await generator.WaitAsync()).Method.Should().Be(AutoTitleMethod.CodexExec);
        generator.Result.SetResult(new AutoTitleResult("匯出報表", null));

        await WaitTitleAsync(h, id, "匯出報表", mark);
    }

    [Fact]
    public async Task 自己的帳戶失敗_借用同種的其他帳戶產生()
    {
        using var h = new HubHarness();
        var generator = Install(h);
        generator.Respond = r => r.AccountLabel == "Claude 主帳戶"
            ? new AutoTitleResult(null, "結束碼 1：You've hit your session limit")
            : new AutoTitleResult("修正登入頁錯誤", null);
        var (id, s) = h.StartConversation(h.Main, h.Claude, "幫我修登入頁的錯誤");
        h.CompleteTurn(s);
        var mark = h.Main.Mark();

        h.Command(h.Main, "autoTitle", new { conversationId = id });

        await WaitTitleAsync(h, id, "修正登入頁錯誤", mark);
        generator.Requests.Select(r => (r.AccountLabel, r.Method)).Should().Equal(("Claude 主帳戶", AutoTitleMethod.ClaudePrint), ("Claude 副帳戶", AutoTitleMethod.ClaudePrint));
        generator.Requests[1].Excerpt.Should().Be(generator.Requests[0].Excerpt);
        h.Main.Messages("toast").Should().BeEmpty("借用成功就不必說明");
    }

    [Fact]
    public async Task 每個帳戶都失敗_最多試三個_說明自己帳戶的原因()
    {
        using var h = new HubHarness();
        var generator = Install(h);
        generator.Respond = r => new AutoTitleResult(null, $"{r.AccountLabel} 失敗");
        var (id, s) = h.StartConversation(h.Main, h.Codex, "把報表匯出成 Excel");
        h.CompleteTurn(s);
        var mark = h.Main.Mark();

        h.Command(h.Main, "autoTitle", new { conversationId = id });

        await generator.WaitAsync(3);
        var toast = await h.Main.WaitForAsync("toast", m => m.Value<string>("text")!.Contains("Codex 帳戶 失敗"), mark, timeoutMs: 5000);
        toast.Value<string>("text").Should().Contain("已改用最近的訊息命名");
        await WaitTitleAsync(h, id, "把報表匯出成 Excel", mark);
        generator.Requests.Select(r => r.AccountLabel).Should().Equal("Codex 帳戶", "Claude 主帳戶", "Claude 副帳戶");
    }

    [Fact]
    public async Task 模型失敗_改用進行中的待辦步驟並說明原因()
    {
        using var h = new HubHarness();
        var generator = Install(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理專案");
        h.Raise(s, new AgentPlanEvent(null, new[]
        {
            new AgentPlanStep { Text = "盤點檔案", Status = "completed" },
            new AgentPlanStep { Text = "改寫 README", Status = "inProgress" },
        }));
        var mark = h.Main.Mark();

        h.Command(h.Main, "autoTitle", new { conversationId = id });
        (await generator.WaitAsync()).Excerpt.Should().StartWith("（代理正在執行這一輪）").And.Contain("- [進行中] 改寫 README");
        generator.Result.SetResult(new AutoTitleResult(null, "結束碼 1：Not logged in"));

        await WaitTitleAsync(h, id, "改寫 README", mark);
        var toast = await h.Main.WaitForAsync("toast", m => m.Value<string>("text")!.Contains("Not logged in"), mark, timeoutMs: 5000);
        toast.Value<string>("text").Should().Contain("已改用最近的訊息命名");
    }

    [Fact]
    public void 找不到CLI_不呼叫模型_直接用最後一則使用者訊息()
    {
        using var h = new HubHarness();
        var generator = Install(h);
        h.Hub.TitleLauncher = _ => null;
        var (id, s) = h.StartConversation(h.Main, h.Claude, "第一個工作");
        h.CompleteTurn(s);
        h.Send(h.Main, id, "幫我把圖表改成長條圖\n細節之後再說");
        h.CompleteTurn(s);

        h.Command(h.Main, "autoTitle", new { conversationId = id });

        h.Conversation(id)!.Title.Should().Be("幫我把圖表改成長條圖", "不必等背景");
        generator.Requests.Should().BeEmpty();
        s.Renames.All.Should().Equal("幫我把圖表改成長條圖");
    }

    [Fact]
    public async Task 產生期間手動改了名稱_不蓋掉()
    {
        using var h = new HubHarness();
        var generator = Install(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        h.CompleteTurn(s);
        var mark = h.Main.Mark();

        h.Command(h.Main, "autoTitle", new { conversationId = id });
        await generator.WaitAsync();
        h.Command(h.Main, "rename", new { conversationId = id, title = "我自己取的" });
        generator.Result.SetResult(new AutoTitleResult("AI 取的", null));

        await WaitTitleAsync(h, id, "我自己取的", mark);
        await h.Ui.DrainAsync();
        h.Conversation(id)!.Title.Should().Be("我自己取的");
        s.Renames.All.Should().Equal("我自己取的");
    }

    [Fact]
    public async Task 產生期間再按_不重複產生()
    {
        using var h = new HubHarness();
        var generator = Install(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        h.CompleteTurn(s);
        var mark = h.Main.Mark();

        h.Command(h.Main, "autoTitle", new { conversationId = id });
        h.Command(h.Main, "autoTitle", new { conversationId = id });
        await generator.WaitAsync();
        await Task.Delay(100);
        generator.Requests.Should().ContainSingle();
        generator.Result.SetResult(new AutoTitleResult("月報", null));
        await WaitTitleAsync(h, id, "月報", mark);

        // 結束後可以再按
        h.Command(h.Main, "autoTitle", new { conversationId = id });
        await generator.WaitAsync(2);
    }

    [Fact]
    public void 草稿與不存在的對話_不做事()
    {
        using var h = new HubHarness();
        var generator = Install(h);
        var draft = h.NewConversation(h.Main, h.Claude);

        h.Command(h.Main, "autoTitle", new { conversationId = draft });
        h.Command(h.Main, "autoTitle", new { conversationId = "nope" });

        generator.Requests.Should().BeEmpty();
        h.Main.Messages("toast").Should().BeEmpty();
    }

    [Fact]
    public async Task 產生期間對話被刪除_結果不處理()
    {
        using var h = new HubHarness();
        var generator = Install(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        h.CompleteTurn(s);

        h.Command(h.Main, "autoTitle", new { conversationId = id });
        await generator.WaitAsync();
        h.Command(h.Main, "delete", new { conversationId = id });
        generator.Result.SetResult(new AutoTitleResult("月報", null));
        await Task.Delay(200);
        await h.Ui.DrainAsync();

        h.Conversation(id).Should().BeNull();
        h.Ui.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Antigravity與Grok對話_各自先用自己的CLI產生標題_臨時對話殘留清理只接受合法GUID()
    {
        using var h = new HubHarness();
        var agy = new Myriagent.Accounts.AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity（本機）", Color = "#7C8CF8" };
        var grok = new Myriagent.Accounts.AgentAccount { Id = "acc-grok", Provider = AgentProvider.Grok, Label = "Grok 帳戶", Color = "#A3B83A", ConfigDirectory = h.Temp.Combine("accounts", "acc-grok", "grok") };
        h.Ui.Invoke(() => h.Accounts.Seed(new[] { h.Claude, h.Codex, agy, grok }, Myriagent.Accounts.AccountStatus.Ready));
        var generator = Install(h);

        var (agyId, sAgy) = h.StartConversation(h.Main, agy, "規劃資料表遷移");
        h.CompleteTurn(sAgy);
        var mark1 = h.Main.Mark();
        h.Command(h.Main, "autoTitle", new { conversationId = agyId });
        var req1 = await generator.WaitAsync(1);
        req1.Method.Should().Be(AutoTitleMethod.AntigravityPrint);
        req1.AccountLabel.Should().Be("Antigravity（本機）");
        generator.Result.SetResult(new AutoTitleResult("資料表遷移規劃", null));
        await WaitTitleAsync(h, agyId, "資料表遷移規劃", mark1);

        generator.Respond = _ => new AutoTitleResult("API 限流測試", null);
        var (grokId, sGrok) = h.StartConversation(h.Main, grok, "寫 API 限流測試");
        h.CompleteTurn(sGrok);
        var mark2 = h.Main.Mark();
        h.Command(h.Main, "autoTitle", new { conversationId = grokId });
        await WaitTitleAsync(h, grokId, "API 限流測試", mark2);
        generator.Requests[^1].Method.Should().Be(AutoTitleMethod.GrokPrint);

        // 驗證暫存資料夾下的殘留清理與防目錄穿越
        var fakeAgyHome = h.Temp.CreateDirectory("fake-agy-home");
        var validGuid = "11111111-2222-3333-4444-555555555555";
        h.Temp.WriteFile($@"fake-agy-home\conversations\{validGuid}.db", "db");
        h.Temp.WriteFile($@"fake-agy-home\conversations\{validGuid}.db-wal", "wal");
        h.Temp.WriteFile($@"fake-agy-home\brain\{validGuid}\.system_generated\logs\transcript.jsonl", "{}");
        h.Temp.WriteFile(@"fake-agy-home\keep.db", "keep");

        AutoTitleRunner.CleanupAntigravityArtifacts(fakeAgyHome, @"..\keep");
        File.Exists(Path.Combine(fakeAgyHome, "keep.db")).Should().BeTrue("非 GUID 一律拒絕刪除");

        AutoTitleRunner.CleanupAntigravityArtifacts(fakeAgyHome, validGuid);
        File.Exists(Path.Combine(fakeAgyHome, "conversations", $"{validGuid}.db")).Should().BeFalse();
        File.Exists(Path.Combine(fakeAgyHome, "conversations", $"{validGuid}.db-wal")).Should().BeFalse();
        Directory.Exists(Path.Combine(fakeAgyHome, "brain", validGuid)).Should().BeFalse();

        var fakeGrokHome = h.Temp.CreateDirectory("fake-grok-home");
        h.Temp.WriteFile($@"fake-grok-home\sessions\C--cache-autotitle\{validGuid}\summary.json", "{}");
        h.Temp.WriteFile(@"fake-grok-home\sessions\C--cache-autotitle\prompt_history.jsonl", "{}\n");
        AutoTitleRunner.CleanupGrokSession(fakeGrokHome, validGuid);
        Directory.Exists(Path.Combine(fakeGrokHome, "sessions", "C--cache-autotitle", validGuid)).Should().BeFalse();
        Directory.Exists(Path.Combine(fakeGrokHome, "sessions", "C--cache-autotitle")).Should().BeFalse("只剩 prompt_history.jsonl 的空標題工作資料夾一併清掉");

        // 逾時無 stdout 時能從 agy --log-file 記錄回退擷取對話 UUID 以清除殘留
        var fakeLog = h.Temp.WriteFile("agy-timeout.log", $"I1024 server.go:100] created conversation_id={validGuid} mode=plan\n");
        AutoTitleRunner.TryExtractAntigravityConversationId(fakeLog).Should().Be(validGuid);
    }
}
