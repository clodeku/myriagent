using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 秘密回答（Codex 非同步提問的 IsSecret 題目）在畫面、存檔與佇列一律是遮蔽後的文字，CLI 收原文（設計 14.11 D20、契約 R3；
/// WP4.3 計畫步 6）。這是宣告 conv1 的組建必須帶著的合併條件（D38），和步 10 的 conv1 閘門測試在同一個測試專案
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubSecretAnswerTests
{
    private const string Hidden = "（已隱藏）";

    private static AgentQuestion[] Questions() =>
    [
        new AgentQuestion { Id = "user", Text = "帳號" },
        new AgentQuestion { Id = "pwd", Text = "密碼", IsSecret = true },
    ];

    /// <summary>執行時產生的秘密答案（repo 裡沒有固定的值）</summary>
    private static string NewSecret() => "答案-" + Guid.NewGuid().ToString("N")[..16];

    private static (string Id, FakeSession Session, AgentChatItem Card) Asked(HubHarness h)
    {
        var (id, s) = h.StartConversation(h.Main, h.Codex, "先問兩題");
        h.Raise(s, new AgentQuestionEvent("async:m1", Questions(), AnswerAsMessage: true));
        return (id, s, h.Items(id).Single(i => i.Kind == AgentItemKind.Question));
    }

    private static void Answer(HubHarness h, string id, AgentChatItem card, string secret)
        => h.Command(h.Main, "answer", new
        {
            conversationId = id,
            itemId = card.Id,
            answers = new Dictionary<string, string[]> { ["user"] = ["alice"], ["pwd"] = [secret] },
        });

    /// <summary>對話資料夾裡每個檔的內容都不含 <paramref name="secret"/>（佇列檔另外看，見各測試）</summary>
    private static void FolderHasNo(HubHarness h, string id, string secret, params string[] except)
    {
        var dir = h.Store.GetConversationDirectory(id);
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(p => !except.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            .ToList();
        files.Should().Contain(p => Path.GetFileName(p) == "items.json", "陽性對照：紀錄檔確實寫了");
        foreach (var file in files)
            File.ReadAllText(file).Should().NotContain(secret, $"{Path.GetFileName(file)} 不能有秘密原文");
    }

    private static void ViewHasNo(FakeView view, string secret, int since = 0)
        => view.All().Skip(since).Select(m => m.ToString()).Should().NotContain(m => m.Contains(secret, StringComparison.Ordinal), "畫面只看遮蔽後的文字");

    [Fact]
    public void 閒置作答_CLI收到原文_畫面與存檔只有已隱藏()
    {
        using var h = new HubHarness();
        var (id, s, card) = Asked(h);
        h.CompleteTurn(s);
        var secret = NewSecret();
        var mark = h.Main.Mark();

        Answer(h, id, card, secret);

        s.Sent.Count.Should().Be(2, "沒在執行：直接當下一則訊息送出");
        s.Sent.All[1].Input.Text.Should().Be("我的回答：\n帳號：alice\n密碼：" + secret, "CLI 收原文");
        h.Items(id).Last(i => i.Kind == AgentItemKind.User).Text.Should().Be("我的回答：\n帳號：alice\n密碼：" + Hidden);
        card.Answers["pwd"].Should().Equal(Hidden);

        h.CompleteTurn(s);
        File.ReadAllText(Path.Combine(h.Store.GetConversationDirectory(id), "items.json")).Should().Contain("密碼：" + Hidden, "陽性對照：遮蔽後的文字存進紀錄");
        FolderHasNo(h, id, secret);
        ViewHasNo(h.Main, secret, mark);
    }

    [Fact]
    public void 執行中作答_佇列的DisplayText已隱藏_送出時CLI收原文()
    {
        using var h = new HubHarness();
        var (id, s, card) = Asked(h);
        var secret = NewSecret();
        var cli = "我的回答：\n帳號：alice\n密碼：" + secret;
        var display = "我的回答：\n帳號：alice\n密碼：" + Hidden;
        var mark = h.Main.Mark();

        Answer(h, id, card, secret);

        s.Sent.Count.Should().Be(1, "這一輪還在執行：排進佇列");
        h.Queue(id).Should().Equal(cli);
        h.Main.Last("composer", id)!["composer"]!["queue"]!.Single().Value<string>("text").Should().Be(display, "輸入區的佇列只顯示遮蔽後的文字");
        var saved = QueueStore.Load(h.Store.GetConversationDirectory(id))!.Queue.Single();
        saved.DisplayText.Should().Be(display, "queue.json 帶著遮蔽後的文字（同步的佇列快照用它）");
        saved.Text.Should().Be(cli, "要送給 CLI 的原文只留在這台的 queue.json（重開之後照樣送得出去），不進畫面、紀錄與同步");

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be(cli, "送出時 CLI 收原文");
        h.Items(id).Last(i => i.Kind == AgentItemKind.User).Text.Should().Be(display);
        h.CompleteTurn(s);
        FolderHasNo(h, id, secret, QueueStore.FileName);
        ViewHasNo(h.Main, secret, mark);
    }

    [Fact]
    public void 一般題目_CLI文字與舊的組法逐位元組相同()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Codex, "先問三題");
        var questions = new[]
        {
            new AgentQuestion { Id = "fmt", Text = "要哪種格式？" },
            new AgentQuestion { Id = "when", Text = "什麼時候：今天或明天" },
            new AgentQuestion { Id = "skip", Text = "備註" },
        };
        h.Raise(s, new AgentQuestionEvent("async:m2", questions, AnswerAsMessage: true));
        h.CompleteTurn(s);
        var card = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);
        var answers = new Dictionary<string, string[]> { ["fmt"] = ["Excel", "CSV"], ["when"] = ["今天"] };

        h.Command(h.Main, "answer", new { conversationId = id, itemId = card.Id, answers });

        // WP4.3 之前中樞的組法（AgentHub.AnswerAsync）
        var old = "我的回答：\n" + string.Join("\n", questions.Select(q => $"{q.Text}：{string.Join("、", answers.TryGetValue(q.Id, out var v) ? v : Array.Empty<string>())}"));
        old.Should().Be("我的回答：\n要哪種格式？：Excel、CSV\n什麼時候：今天或明天：今天\n備註：");
        s.Sent.All[1].Input.Text.Should().Be(old);
        h.Items(id).Last(i => i.Kind == AgentItemKind.User).Text.Should().Be(old, "沒有秘密題目時顯示文字與 CLI 文字相同");
    }

    [Fact]
    public async Task 標題不含秘密()
    {
        using var h = new HubHarness();
        var (id, s, card) = Asked(h);
        var title = h.Conversation(id)!.Title;
        var secret = NewSecret();
        var mark = h.Main.Mark();

        Answer(h, id, card, secret);
        h.CompleteTurn(s);
        h.CompleteTurn(s);
        await h.Main.WaitForAsync("conversations", since: mark);
        await h.Main.WaitForAsync("header", since: mark, conversationId: id);

        h.Conversation(id)!.Title.Should().Be(title).And.NotContain(secret);
        File.ReadAllText(Path.Combine(h.Store.GetConversationDirectory(id), "meta.json")).Should().NotContain(secret);
        ViewHasNo(h.Main, secret, mark);
    }
}
