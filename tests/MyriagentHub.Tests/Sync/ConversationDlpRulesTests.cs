using Myriagent.Agents;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.ConversationSamples;

namespace MyriagentHub.Tests.Sync;

/// <summary>伺服器端的第二道權杖 DLP（WP4.2 計畫步 2、D8）：走遍解析後的字串值與屬性名稱</summary>
public sealed class ConversationDlpRulesTests
{
    [Fact]
    public void 乾淨的payload_不算()
    {
        ConversationDlpRules.HasLeak(Item()).Should().BeFalse();
        ConversationDlpRules.HasLeak(Meta()).Should().BeFalse();
        ConversationDlpRules.HasLeak(Queue("一", "二")).Should().BeFalse();
        ConversationDlpRules.HasLeak(new JObject()).Should().BeFalse();
        ConversationDlpRules.HasLeakText("{}").Should().BeFalse();
    }

    [Fact]
    public void 字串值裡的權杖_算()
    {
        ConversationDlpRules.HasLeak(Item("權杖是 " + GitHub())).Should().BeTrue();
        ConversationDlpRules.HasLeak(new JValue(GitHub())).Should().BeTrue("根本身是字串也檢查");
    }

    [Fact]
    public void 屬性名稱裡的權杖也算()
    {
        var item = Item();
        item[GitHub()] = 1;
        CredentialLeakRules.HasLeak(Json(Item())).Should().BeFalse("對照：同一份沒有那個鍵");
        ConversationDlpRules.HasLeak(item).Should().BeTrue();
    }

    [Fact]
    public void 陣列裡的巢狀字串也算()
    {
        var item = Item();
        item["questions"] = new JArray(new JObject
        {
            ["id"] = "q",
            ["text"] = "題目",
            ["options"] = new JArray(new JObject { ["label"] = "選項", ["description"] = "說明 " + GitHub() }),
        });
        ConversationDlpRules.HasLeak(item).Should().BeTrue();
        ConversationDlpRules.HasLeak(new JArray(new JArray("a", new JArray("b", GitHub())))).Should().BeTrue();
    }

    [Fact]
    public void JSON跳脫寫法的權杖也算()
    {
        // 原文裡的權杖第一個字寫成 \u 跳脫：對原文掃描比對不到，解析後才看得到
        var raw = "{\"id\":\"item1\",\"ord\":1,\"ver\":1,\"kind\":\"Assistant\",\"status\":\"Done\",\"text\":\"" + EscapeFirst(GitHub()) + "\"}";
        CredentialLeakRules.HasLeak(raw).Should().BeFalse("前提：原文比對不到");
        ConversationEventRules.TryParseStrict(raw, out var token).Should().BeTrue();
        ConversationDlpRules.HasLeak(token!).Should().BeTrue();
        ConversationDlpRules.HasLeakText(raw).Should().BeTrue("解析失敗時的檢查也讀解碼後的字串");
    }

    [Fact]
    public void 欄位名稱加值的權杖也算()
    {
        var value = "abcdefgh" + "12345678";
        var item = Item();
        item["access_" + "token"] = value;
        CredentialLeakRules.HasLeak(value).Should().BeFalse("前提：值本身看不出來");
        CredentialLeakRules.HasLeak("access_" + "token").Should().BeFalse("前提：名稱本身看不出來");
        ConversationDlpRules.HasLeak(item).Should().BeTrue("組成 \"名稱\":\"值\" 才比對得到");
    }

    [Fact]
    public void 字串裡的JSON以跳脫寫的權杖也算()
    {
        var output = "{\"k\":\"" + EscapeFirst(GitHub()) + "\"}";
        CredentialLeakRules.HasLeak(output).Should().BeFalse("前提：字串本身比對不到");
        var item = Item();
        item["output"] = output;
        ConversationDlpRules.HasLeak(item).Should().BeTrue();
        ConversationDlpRules.HasLeak(Item("{\"k\":\"沒有權杖\"}")).Should().BeFalse("字串裡的 JSON 是乾淨的");
    }

    [Fact]
    public void 遮蔽過的副本_不算()
    {
        ConversationDlpRules.HasLeak(Item("權杖是 " + CredentialLeakRules.Replacement)).Should().BeFalse();
        var item = Item();
        item[CredentialLeakRules.Replacement] = CredentialLeakRules.Replacement;
        item["access_" + "token"] = CredentialLeakRules.Replacement;
        ConversationDlpRules.HasLeak(item).Should().BeFalse();
    }

    [Fact]
    public void HasLeakText_解析失敗的文字也逐一讀出字串()
    {
        ConversationDlpRules.HasLeakText("not json " + GitHub()).Should().BeTrue();
        ConversationDlpRules.HasLeakText("not json").Should().BeFalse();
        // 重複的鍵（嚴格解析失敗）：兩個值都讀
        var duplicate = "{\"text\":\"" + EscapeFirst(GitHub()) + "\",\"text\":\"ok\"}";
        ConversationEventRules.TryParseStrict(duplicate, out _).Should().BeFalse("前提：嚴格解析失敗");
        ConversationDlpRules.HasLeakText(duplicate).Should().BeTrue();
        // 讀到語法錯誤為止，前面的照樣檢查
        ConversationDlpRules.HasLeakText("{\"a\":\"" + EscapeFirst(GitHub()) + "\", oops").Should().BeTrue();
        ConversationDlpRules.HasLeakText("{\"" + EscapeFirst(GitHub()) + "\":1}").Should().BeTrue("屬性名稱");
        ConversationDlpRules.HasLeakText("{\"access_" + "token\":\"abcdefgh" + "12345678\",").Should().BeTrue("名稱＋值");
    }

    [Fact]
    public void 建構式_HasNonStandardToken為真_名稱是權杖時HasLeak也為真()
    {
        // 合併前審查 DLP-CTOR-1：Newtonsoft 把 new 名稱(…) 解析成 JConstructor，走遍檢查原本不看它的名稱
        var key = AwsAccessKey();
        CredentialLeakRules.HasLeak(key).Should().BeTrue("前提：AKIA 加 16 碼是存取金鑰的樣式");
        var withKey = RawItem("\"hi\"", ",\"x\":new " + key + "(1)");
        ConversationEventRules.TryParseStrict(withKey, out var token).Should().BeTrue("前提：嚴格解析也收建構式");
        token!["x"]!.Type.Should().Be(JTokenType.Constructor);
        ConversationDlpRules.HasNonStandardToken(token).Should().BeTrue();
        ConversationDlpRules.HasLeak(token).Should().BeTrue("兜底：建構式的名稱也檢查");

        ConversationEventRules.TryParseStrict(RawItem("\"hi\"", ",\"x\":new Foo(1)"), out var plain).Should().BeTrue();
        ConversationDlpRules.HasNonStandardToken(plain!).Should().BeTrue("沒有權杖也不是標準 JSON");
        ConversationDlpRules.HasLeak(plain!).Should().BeFalse();
        ConversationEventRules.TryParseStrict(RawItem("\"hi\"", ",\"x\":undefined"), out var undefined).Should().BeTrue();
        ConversationDlpRules.HasNonStandardToken(undefined!).Should().BeTrue();

        ConversationDlpRules.HasNonStandardToken(Item()).Should().BeFalse("SyncJson 的輸出");
        ConversationDlpRules.HasNonStandardToken(new JValue("new Foo(1)")).Should().BeFalse("字串裡的文字不是建構式");

        var duplicate = "{\"x\":new " + key + "(1),\"x\":1}";
        ConversationEventRules.TryParseStrict(duplicate, out _).Should().BeFalse("前提：重複的鍵");
        ConversationDlpRules.HasLeakText(duplicate).Should().BeTrue("解析失敗時：原文與讀取器讀到的建構式名稱");
    }

    [Fact]
    public void MaskItem遮蔽過的投影_不算()
    {
        var original = LeakyItem();
        Projection(original).Should().BeTrue("同一份不遮蔽時算");
        Projection(CredentialLeakRules.MaskItem(original)).Should().BeFalse();
    }

    [Fact]
    public void 截斷前綴_執行中尾端_差異裡的私鑰_遮蔽後都不算_原文都算()
    {
        var truncated = new AgentChatItem
        {
            Id = "i1", Kind = AgentItemKind.Tool, Status = AgentItemStatus.Done, Ord = 1,
            Output = OmittedPrefix() + "p_" + new string('z', 30) + " " + GitHub() + "\n第二行",
        };
        var running = new AgentChatItem
        {
            Id = "i2", Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Running, Ord = 2,
            Text = "權杖：" + GitHubFragment(24),
        };
        var diff = new AgentChatItem
        {
            Id = "i3", Kind = AgentItemKind.Tool, Status = AgentItemStatus.Done, Ord = 3,
            Diff = [new("add", PemBegin()), new("add", PemBody()), new("add", PemBody('B')), new("add", PemEnd()), new("ctx", "之後")],
        };
        foreach (var item in new[] { truncated, running, diff })
        {
            Projection(item).Should().BeTrue($"{item.Id}：原文算");
            Projection(CredentialLeakRules.MaskItem(item)).Should().BeFalse($"{item.Id}：遮蔽後不算");
        }
    }

    private static AgentChatItem LeakyItem() => new()
    {
        Id = "i9",
        Kind = AgentItemKind.Question,
        Status = AgentItemStatus.Waiting,
        Ord = 9,
        Text = "看 " + GitHub(),
        ToolName = "tool " + GitHub('b'),
        Title = "標題 " + GitHub('c'),
        Detail = "{\"k\":\"" + GitHub('d') + "\"}",
        Output = "輸出 " + GitHub('e'),
        FilePath = "C:/x/" + GitHub('f'),
        Questions = [new AgentQuestion { Id = "題 " + GitHub('g'), Text = "題目 " + GitHub('h'), Options = [new AgentQuestionOption { Label = GitHub('i') }] }],
        Answers = new Dictionary<string, List<string>> { ["題 " + GitHub('g')] = ["答 " + GitHub('j')] },
        Diff = [new("add", "x = " + GitHub('k'))],
        PlanSteps = [new AgentPlanStep { Text = "步驟 " + GitHub('l'), Status = "pending" }],
    };

    /// <summary>項目的線上投影（TryItem → 原文 → 嚴格解析）有沒有命中第二道</summary>
    private static bool Projection(AgentChatItem item)
    {
        ConversationWire.TryItem(item, 1, out var wire).Should().BeTrue();
        var json = ConversationWire.Serialize(wire!);
        ConversationEventRules.TryParseStrict(json, out var token).Should().BeTrue();
        return ConversationDlpRules.HasLeak(token!);
    }
}
