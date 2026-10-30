using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// 歷史裡秘密回答的清除（WP4.3 計畫 D18、R3）：從卡片的結構重組顯示文字，不從文字反推；認不準時換成固定的佔位文字
/// </summary>
public sealed class SecretAnswerScrubRulesTests
{
    private const string Hidden = "（已隱藏）";

    private static int next;

    private static AgentQuestion Q(string text, bool secret = false) => new() { Id = "q" + Interlocked.Increment(ref next), Text = text, IsSecret = secret };

    /// <summary>已作答的非同步問卷卡片（中樞存的答案：秘密題目是「（已隱藏）」）與當時送給 CLI 的原文</summary>
    private static (AgentChatItem Card, string CliText) Answered(params (AgentQuestion Question, string[] Values)[] answers)
    {
        var card = new AgentChatItem
        {
            Kind = AgentItemKind.Question,
            Status = AgentItemStatus.Done,
            AnswerAsMessage = true,
            Decision = "answered",
            Questions = answers.Select(a => a.Question).ToList(),
            Answers = answers.Where(a => a.Values.Length > 0)
                .ToDictionary(a => a.Question.Id, a => a.Question.IsSecret ? new List<string> { Hidden } : a.Values.ToList()),
        };
        var raw = answers.Where(a => a.Values.Length > 0).ToDictionary(a => a.Question.Id, a => (IReadOnlyList<string>)a.Values);
        return (card, AnswerMessageRules.Compose(card.Questions, raw).CliText);
    }

    private static AgentChatItem User(string text) => new() { Kind = AgentItemKind.User, Text = text };

    private static string Display(AgentChatItem card)
        => AnswerMessageRules.Compose(card.Questions, card.Answers.ToDictionary(a => a.Key, a => (IReadOnlyList<string>)a.Value)).DisplayText;

    [Fact]
    public void 分頁呼叫_每一種切法的結果與整份清單相同()
    {
        // 引擎依 Ord 一頁一頁取快照（WP4.3 步 10）：跨頁的候選卡片靠傳入的清單接著認領
        var (secret1, cli1) = Answered((Q("密碼", secret: true), ["pw-一"]));
        var (plain, cli2) = Answered((Q("要哪一個"), ["甲"]));
        var (secret2, _) = Answered((Q("金鑰", secret: true), ["key-二"]));
        var (secret3, cli3) = Answered((Q("權杖", secret: true), ["tok-三"]));
        var items = new List<AgentChatItem>
        {
            new() { Kind = AgentItemKind.Assistant, Text = "請回答" }, secret1, User(cli1), plain, User(cli2), secret2, secret3, User(cli3), User("我的回答：\n自己打的"),
        };
        var full = SecretAnswerScrubRules.ScrubHistory(items);
        full.Should().NotBeEmpty();

        for (var size = 1; size <= items.Count; size++)
            Paged(items, size).Should().BeEquivalentTo(full, $"每頁 {size} 筆");
    }

    /// <summary>每頁 <paramref name="size"/> 筆、狀態接著傳（同引擎的分頁）</summary>
    private static Dictionary<string, string> Paged(IReadOnlyList<AgentChatItem> items, int size)
    {
        var state = new SecretScrubState();
        var paged = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var start = 0; start < items.Count; start += size)
        {
            foreach (var (id, text) in SecretAnswerScrubRules.ScrubHistory(items.Skip(start).Take(size).ToList(), state))
                paged[id] = text;
        }

        return paged;
    }

    [Fact]
    public void 後面的非秘密卡先答_前面的秘密卡後答_秘密原文不外送()
    {
        // 合併前審查 P2：Codex 同時發出兩張非同步提問，使用者先答後面的 B（一般題目），再答前面的 A（秘密題目）；舊版以原文組訊息
        var (a, aCli) = Answered((Q("API 金鑰", secret: true), ["hunter2-secret-VERIFYP2"]));
        var (b, bCli) = Answered((Q("要哪一個"), ["甲"]));
        var msgB = User(bCli);
        var msgA = User(aCli);
        aCli.Should().Contain("hunter2-secret-VERIFYP2", "前提：舊版的訊息帶秘密原文");
        var items = new List<AgentChatItem> { a, b, msgB, msgA };

        var full = SecretAnswerScrubRules.ScrubHistory(items);

        full.Should().NotContainKey(msgB.Id, "逐字等於 B 重組的文字：就是 B 的答案，照原文");
        full[msgA.Id].Should().Be(Display(a)).And.NotContain("hunter2");
        // 分成 [A,B]、[msgB,msgA] 兩頁（以及每一種切法）結果相同
        for (var size = 1; size <= items.Count; size++)
            Paged(items, size).Should().BeEquivalentTo(full, $"每頁 {size} 筆");
    }

    [Fact]
    public void 出現過秘密卡片後_對不上的我的回答一律換成佔位()
    {
        // 使用者自己打了「我的回答：」開頭、搶先認領了秘密卡片：之後真正的秘密答案（原文）沒有候選可認領，也不照原文送
        var (a, aCli) = Answered((Q("密碼", secret: true), ["s3cret-typed"]));
        var typed = User("我的回答：\n自己打的");
        var msgA = User(aCli);
        var result = SecretAnswerScrubRules.ScrubHistory([a, typed, msgA]);
        result[typed.Id].Should().Be(Display(a));
        result[msgA.Id].Should().Be(SecretAnswerScrubRules.AmbiguousPlaceholder);

        // 非秘密卡的答案組法不同（對不上）：兩則都不照原文送
        var (s, sCli) = Answered((Q("權杖", secret: true), ["tok-drift"]));
        var (p, _) = Answered((Q("要哪一個"), ["乙"]));
        var drifted = User("我的回答：\n要哪一個： 乙");
        var raw = User(sCli);
        var both = SecretAnswerScrubRules.ScrubHistory([s, p, drifted, raw]);
        both[drifted.Id].Should().Be(SecretAnswerScrubRules.AmbiguousPlaceholder);
        both[raw.Id].Should().Be(SecretAnswerScrubRules.AmbiguousPlaceholder);
        both.Values.Should().NotContain(v => v.Contains("tok-drift"));

        // 沒出現過秘密卡片時照舊：使用者自己打的字照原文
        var (plain, plainCli) = Answered((Q("要哪一個"), ["丙"]));
        SecretAnswerScrubRules.ScrubHistory([plain, User("我的回答：\n自己打的"), User(plainCli)]).Should().BeEmpty();
    }

    [Fact]
    public void 佔位文字逐字釘住()
    {
        SecretAnswerScrubRules.AmbiguousPlaceholder.Should().Be("我的回答：\n（含已隱藏的回答，內容未同步）");
        SecretAnswerScrubRules.AmbiguousPlaceholder.Should().StartWith(AnswerMessageRules.Prefix);
    }

    [Fact]
    public void 秘密答案換成已隱藏()
    {
        var (card, cli) = Answered((Q("API 金鑰", secret: true), ["hunter2-秘密"]), (Q("要哪一個"), ["甲"]));
        var user = User(cli);
        cli.Should().Contain("hunter2-秘密", "WP4.3 之前的中樞把原文存成項目的文字");

        var result = SecretAnswerScrubRules.ScrubHistory([new AgentChatItem { Kind = AgentItemKind.Assistant, Text = "請回答" }, card, user]);

        result.Should().ContainSingle();
        result[user.Id].Should().Be("我的回答：\nAPI 金鑰：" + Hidden + "\n要哪一個：甲");
        result[user.Id].Should().NotContain("hunter2");
    }

    [Fact]
    public void 多行秘密答案整則換掉()
    {
        var (card, cli) = Answered((Q("私鑰", secret: true), ["第一行\n第二行\n要哪一個：乙"]), (Q("要哪一個"), ["甲"]));
        var user = User(cli);

        var result = SecretAnswerScrubRules.ScrubHistory([card, user]);

        result[user.Id].Should().Be("我的回答：\n私鑰：" + Hidden + "\n要哪一個：甲", "答案裡的換行與假的題目行都不影響");
        result[user.Id].Should().NotContain("第二行");
    }

    [Fact]
    public void 題目含全形冒號()
    {
        var (card, cli) = Answered((Q("帳號：密碼", secret: true), ["admin：p@ss"]), (Q("環境：正式或測試"), ["測試"]));
        var user = User(cli);

        var result = SecretAnswerScrubRules.ScrubHistory([card, user]);

        result[user.Id].Should().Be("我的回答：\n帳號：密碼：" + Hidden + "\n環境：正式或測試：測試");
        result[user.Id].Should().NotContain("p@ss");
    }

    [Fact]
    public void 答案含頓號()
    {
        var (card, cli) = Answered((Q("通關密語", secret: true), ["甲、乙、丙"]), (Q("選項"), ["一、二", "三"]));
        var user = User(cli);

        var result = SecretAnswerScrubRules.ScrubHistory([card, user]);

        result[user.Id].Should().Be("我的回答：\n通關密語：" + Hidden + "\n選項：一、二、三");
    }

    [Fact]
    public void 一般題目照舊()
    {
        var (card, cli) = Answered((Q("要哪一個"), ["甲"]), (Q("備註"), ["沒有"]));
        var user = User(cli);

        SecretAnswerScrubRules.ScrubHistory([card, user]).Should().BeEmpty("沒有秘密題目：照原文送");
    }

    [Fact]
    public void 兩張候選含秘密_換成佔位()
    {
        // 執行中先後回答兩張卡片：兩則答案排進佇列，這一輪結束後依序送出
        var (secret, secretCli) = Answered((Q("密碼", secret: true), ["s3cret"]));
        var (plain, plainCli) = Answered((Q("要哪一個"), ["甲"]));
        var first = User(secretCli);
        var second = User(plainCli);

        var result = SecretAnswerScrubRules.ScrubHistory([secret, plain, first, second]);

        result[first.Id].Should().Be(SecretAnswerScrubRules.AmbiguousPlaceholder, "之前有兩張沒被認領的候選、其中一張含秘密：認不準是哪一張");
        result.Should().NotContainKey(second.Id, "第二則認領剩下的那張（沒有秘密）");

        // 秘密的那張在後面：第一則逐字等於一般卡片重組的文字（就是它的答案、照原文），第二則認領秘密的那張、從卡片重組
        var (plain2, plainCli2) = Answered((Q("要哪一個"), ["乙"]));
        var (secret2, secretCli2) = Answered((Q("密碼", secret: true), ["t0ken"]));
        var u1 = User(plainCli2);
        var u2 = User(secretCli2);
        var swapped = SecretAnswerScrubRules.ScrubHistory([plain2, secret2, u1, u2]);
        swapped.Should().NotContainKey(u1.Id);
        swapped[u2.Id].Should().Be(Display(secret2)).And.NotContain("t0ken");

        // 兩張都沒有秘密：不用佔位
        var (a, aCli) = Answered((Q("甲題"), ["1"]));
        var (b, bCli) = Answered((Q("乙題"), ["2"]));
        SecretAnswerScrubRules.ScrubHistory([a, b, User(aCli), User(bCli)]).Should().BeEmpty();
    }

    [Fact]
    public void 開頭不是我的回答_不動()
    {
        var (card, _) = Answered((Q("密碼", secret: true), ["s3cret"]));
        var typed = User("我的答案是 s3cret");
        var noNewline = User("我的回答：s3cret");

        SecretAnswerScrubRules.ScrubHistory([card, typed, noNewline]).Should().BeEmpty("只認以「我的回答：\\n」開頭的 User 項目");

        // 卡片在後面（使用者先自己打了同樣開頭的字）：之前沒有候選，照原文
        var early = User("我的回答：\n自己打的");
        SecretAnswerScrubRules.ScrubHistory([early, card]).Should().BeEmpty();

        // 不是候選的卡片：還沒作答、不是非同步提問、助理的項目
        var waiting = Answered((Q("密碼", secret: true), ["x"])).Card;
        waiting.Status = AgentItemStatus.Waiting;
        waiting.Decision = null;
        var sync = Answered((Q("密碼", secret: true), ["x"])).Card;
        sync.AnswerAsMessage = false;
        SecretAnswerScrubRules.ScrubHistory([waiting, sync, User("我的回答：\n密碼：x")]).Should().BeEmpty();
    }

    [Fact]
    public void 重複執行結果相同()
    {
        var (card, cli) = Answered((Q("密碼", secret: true), ["s3cret"]), (Q("備註"), ["好"]));
        var user = User(cli);
        var items = new List<AgentChatItem> { card, user };

        var once = SecretAnswerScrubRules.ScrubHistory(items);
        user.Text = once[user.Id];
        var twice = SecretAnswerScrubRules.ScrubHistory(items);

        twice[user.Id].Should().Be(once[user.Id]);
        SecretAnswerScrubRules.ScrubHistory(items).Should().BeEquivalentTo(twice);
    }

    [Fact]
    public void 結果等於Compose的DisplayText()
    {
        var questions = new (AgentQuestion, string[])[]
        {
            (Q("第一題"), ["甲", "乙"]),
            (Q("秘密題", secret: true), ["x"]),
            (Q("沒回答的題目"), []),
            (Q("沒回答的秘密題", secret: true), []),
        };
        var (card, cli) = Answered(questions);
        var user = User(cli);

        var result = SecretAnswerScrubRules.ScrubHistory([card, user]);

        result[user.Id].Should().Be(Display(card));
        result[user.Id].Should().Be("我的回答：\n第一題：甲、乙\n秘密題：" + Hidden + "\n沒回答的題目：\n沒回答的秘密題：");
    }

    [Fact]
    public void 存檔裡的null清單與null元素_不丟例外()
    {
        var (card, cli) = Answered((Q("密碼", secret: true), ["s"]));
        card.Questions.Add(null!);
        card.Answers["null"] = null!;
        var user = User(cli);

        var result = SecretAnswerScrubRules.ScrubHistory([null!, card, new AgentChatItem { Kind = AgentItemKind.User, Text = null! }, user]);

        result[user.Id].Should().Be("我的回答：\n密碼：" + Hidden);
    }

    [Fact]
    public void 佇列沒有DisplayText時從卡片重組()
    {
        var (card, cli) = Answered((Q("密碼", secret: true), ["s3cret"]), (Q("備註"), ["好"]));

        SecretAnswerScrubRules.QueuedDisplay(card, null, cli).Should().Be(Display(card)).And.NotContain("s3cret");
        SecretAnswerScrubRules.QueuedDisplay(card, "中樞給的顯示文字", cli).Should().Be("中樞給的顯示文字", "有 DisplayText 一律用它");
        SecretAnswerScrubRules.QueuedDisplay(null, "已遮蔽", "原文").Should().Be("已遮蔽");
        SecretAnswerScrubRules.QueuedDisplay(null, null, "一般的訊息").Should().Be("一般的訊息");
        SecretAnswerScrubRules.QueuedDisplay(null, null, cli).Should().Be(SecretAnswerScrubRules.AmbiguousPlaceholder,
            "重開程式後答案與卡片的關聯不存檔：認不出是哪一張卡片的答案");
    }
}
