using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.ConversationSamples;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 伺服器的再遮蔽（WP4.2 計畫步 2、D44；答 5）：每例都斷言遮蔽後的走遍檢查是假、Validate 通過（<see cref="Remask"/>）
/// </summary>
public sealed class ConversationRemaskRulesTests
{
    private static readonly string R = CredentialLeakRules.Replacement;

    [Fact]
    public void 每個字串的PEM區塊整段換掉()
    {
        var complete = "前文 " + PemBegin() + "\n" + PemBody() + "\n" + PemEnd() + " 後文";
        ConversationRemaskRules.MaskPrivateKeyBlocks(complete).Should().Be("前文 " + R + " 後文");

        var unterminated = "前文 " + PemBegin() + "\n" + PemBody() + "\n" + PemBody('B');
        ConversationRemaskRules.MaskPrivateKeyBlocks(unterminated).Should().Be("前文 " + R, "沒有結尾就換到最後");

        var two = "a " + PemBegin() + "\nx\n" + PemEnd() + " b " + PemBegin() + "\ny";
        ConversationRemaskRules.MaskPrivateKeyBlocks(two).Should().Be("a " + R + " b " + R);

        ConversationRemaskRules.MaskPrivateKeyBlocks("沒有私鑰 " + PemEnd()).Should().Be("沒有私鑰 " + PemEnd(), "只有結尾標記不換");
        ConversationRemaskRules.MaskPrivateKeyBlocks(string.Empty).Should().BeEmpty();

        // 經 Remask：任何字串（這裡是項目的 title）
        var item = Item();
        item["title"] = complete;
        var masked = Remask(ConversationEventKinds.Item, item);
        masked["title"]!.Value<string>().Should().Be("前文 " + R + " 後文");
    }

    [Fact]
    public void 屬性名稱命中_加n不撞名()
    {
        var item = Item();
        item[GitHub('a')] = 1;
        item[GitHub('b')] = 2;
        var masked = Remask(ConversationEventKinds.Item, item);
        masked[R]!.Value<int>().Should().Be(1);
        masked[R + "#2"]!.Value<int>().Should().Be(2, "遮蔽後同名：第二個加 #2，值不丟");
        masked.Properties().Select(p => p.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void 屬性名稱命中_三個同名依序加n()
    {
        var meta = Meta();
        meta[GitHub('a')] = "x";
        meta[GitHub('b')] = "y";
        meta[GitHub('c')] = "z";
        var masked = Remask(ConversationEventKinds.Meta, meta);
        masked[R]!.Value<string>().Should().Be("x");
        masked[R + "#2"]!.Value<string>().Should().Be("y");
        masked[R + "#3"]!.Value<string>().Should().Be("z");
    }

    [Fact]
    public void meta的title結尾省略號_MaskCut()
    {
        // 半截權杖緊接在「…」前面：不到樣式的長度下限，Mask 比對不到；MaskCut 先去掉「…」前面的權杖字元
        var meta = Meta("說明 " + GitHubFragment(5) + "…");
        meta["workingDirectory"] = "D:/repo/" + GitHub();
        meta["customFolder"] = true;
        meta["model"] = "模型 " + GitHubFragment(3) + "…";
        var masked = Remask(ConversationEventKinds.Meta, meta);
        masked["title"]!.Value<string>().Should().Be("說明 …");
        masked["model"]!.Value<string>().Should().Be("模型 …");
        masked["workingDirectory"]!.Value<string>().Should().Be("D:/repo/" + R);
        masked["provider"]!.Value<string>().Should().Be("Claude", "其他欄位不變");

        // 沒有「…」的照一般遮蔽：短片段不動
        Remask(ConversationEventKinds.Created, Meta("說明 " + GitHubFragment(5)))["title"]!.Value<string>().Should().Be("說明 " + GitHubFragment(5));
    }

    [Fact]
    public void 執行中的項目_SafeLength()
    {
        var text = "結果：" + GitHubFragment(10);
        CredentialLeakRules.HasLeak(text).Should().BeFalse("前提：片段本身比對不到");

        var running = Remask(ConversationEventKinds.Item, Item(text, status: "Running"));
        running["text"]!.Value<string>().Should().Be("結果：", "執行中：尾端可能還沒傳完的權杖字元去掉");

        var done = Remask(ConversationEventKinds.Item, Item(text, status: "Done"));
        done["text"]!.Value<string>().Should().Be(text, "完成的項目不去尾端");

        var waiting = Remask(ConversationEventKinds.Item, Item(text, status: "Waiting"));
        waiting["text"]!.Value<string>().Should().Be(text, "只有 Running（同 MaskItem）");

        var detail = Item(status: "Running");
        detail["detail"] = "細節 " + GitHubFragment(8);
        detail["output"] = "輸出 " + GitHubFragment(8);
        detail["title"] = "標題 " + GitHubFragment(8);
        var maskedDetail = Remask(ConversationEventKinds.Item, detail);
        maskedDetail["detail"]!.Value<string>().Should().Be("細節 ");
        maskedDetail["output"]!.Value<string>().Should().Be("輸出 ");
        maskedDetail["title"]!.Value<string>().Should().Be("標題 " + GitHubFragment(8), "只有 text、detail、output");
    }

    [Fact]
    public void 截斷前綴的輸出_MaskTruncated()
    {
        var item = Item(status: "Done");
        item["output"] = OmittedPrefix() + "p_" + new string('z', 36) + " 尾巴\n第二行";
        var masked = Remask(ConversationEventKinds.Item, item);
        masked["output"]!.Value<string>().Should().Be(OmittedPrefix() + R + "\n第二行", "前綴之後到第一個換行整段換掉");
    }

    [Fact]
    public void JSON的工具輸出_MaskJsonStrings()
    {
        var item = Item(status: "Done");
        item["output"] = "{\"k\":\"" + EscapeFirst(GitHub()) + "\",\"n\":1}";
        item["detail"] = "{\"" + EscapeFirst(GitHub('b')) + "\":true}";
        var masked = Remask(ConversationEventKinds.Item, item);
        var output = JObject.Parse(masked["output"]!.Value<string>()!);
        output["k"]!.Value<string>().Should().Be(R, "解碼後的字串遮蔽");
        output["n"]!.Value<int>().Should().Be(1);
        JObject.Parse(masked["detail"]!.Value<string>()!).Properties().Single().Name.Should().Be(R, "解碼後的屬性名稱遮蔽");

        // 其他欄位（一般的遮蔽）裡的 JSON 也一樣
        var other = Item();
        other["filePath"] = "[\"" + EscapeFirst(GitHub('c')) + "\"]";
        JArray.Parse(Remask(ConversationEventKinds.Item, other)["filePath"]!.Value<string>()!)[0]!.Value<string>().Should().Be(R);
    }

    [Fact]
    public void diff跨行的私鑰_開頭之後每行整行換掉()
    {
        var item = Item(text: null, status: "Done");
        item["kind"] = "Tool";
        item["diff"] = new JArray(
            Line("hunk", "@@ -1 +1 @@"),
            Line("add", "金鑰：" + PemBegin()),
            Line("add", PemBody()),
            Line("del", PemBody('B')),
            Line("add", PemEnd() + " 結尾之後"),
            Line("ctx", "之後 " + GitHub()),
            Line("ctx", "乾淨"));
        var masked = Remask(ConversationEventKinds.Item, item);
        masked["diff"]!.Select(d => d["s"]!.Value<string>()).Should().Equal(
            "@@ -1 +1 @@", "金鑰：" + R, R, R, R, "之後 " + R, "乾淨");
        masked["diff"]!.Select(d => d["t"]!.Value<string>()).Should().Equal("hunk", "add", "add", "del", "add", "ctx", "ctx");

        // 沒有結尾：開頭之後全部換掉；同一行開了又關的不算
        var open = Item(text: null);
        open["diff"] = new JArray(Line("add", PemBegin() + "x" + PemEnd()), Line("add", "保留"), Line("add", PemBegin()), Line("add", "a"), Line("add", "b"));
        Remask(ConversationEventKinds.Item, open)["diff"]!.Select(d => d["s"]!.Value<string>()).Should().Equal(R, "保留", R, R, R);
    }

    [Fact]
    public void diff的私鑰_拿掉跨行規則時本體會留下()
    {
        // 對照：單行的樣式只認得開頭那一行，本體 base64 本身不會被任何樣式比對到——所以一定要跨行處理
        CredentialLeakRules.HasLeak(PemBody()).Should().BeFalse();
        CredentialLeakRules.Mask(PemBody()).Should().Be(PemBody());
    }

    [Fact]
    public void queue的displayText同meta()
    {
        var queue = Queue("貼上 " + GitHubFragment(4) + "…", "整個 " + GitHub(), "乾淨");
        var masked = Remask(ConversationEventKinds.Queue, queue);
        masked["entries"]!.Select(e => e["displayText"]!.Value<string>()).Should().Equal("貼上 …", "整個 " + R, "乾淨");
        masked["entries"]!.Select(e => e["id"]!.Value<string>()).Should().Equal("q0", "q1", "q2");
    }

    [Fact]
    public void 其他種類_每個字串都遮蔽()
    {
        var attention = new JObject { ["kind"] = "question", ["itemId"] = "item1", ["note"] = "多送的 " + GitHub() };
        Remask(ConversationEventKinds.Attention, attention)["note"]!.Value<string>().Should().Be("多送的 " + R);
        var turn = new JObject { ["phase"] = "started", [GitHub()] = "x" };
        Remask(ConversationEventKinds.Turn, turn)[R]!.Value<string>().Should().Be("x");
    }

    [Fact]
    public void 欄位名稱加值_整個值換掉()
    {
        var item = Item();
        item["access_" + "token"] = "abcdefgh" + "12345678";
        var masked = Remask(ConversationEventKinds.Item, item);
        masked["access_" + "token"]!.Value<string>().Should().Be(R);
        masked["text"]!.Value<string>().Should().Be("你好");
    }

    [Fact]
    public void 遮蔽是冪等的_再遮一次相同()
    {
        var samples = new (string Kind, JObject Payload)[]
        {
            (ConversationEventKinds.Item, Item("看 " + GitHub(), status: "Running")),
            (ConversationEventKinds.Meta, Meta("說明 " + GitHubFragment(5) + "…")),
            (ConversationEventKinds.Queue, Queue("整個 " + GitHub())),
            (ConversationEventKinds.Item, WithDiff()),
            (ConversationEventKinds.Item, WithLeakyNames()),
        };
        foreach (var (kind, payload) in samples)
        {
            var once = ConversationRemaskRules.Remask(kind, payload);
            ConversationEventRules.TryParseStrict(once, out var parsed).Should().BeTrue();
            ConversationRemaskRules.Remask(kind, (JObject)parsed!).Should().Be(once, kind);
        }
    }

    [Fact]
    public void ord與ver不變_也不改傳入的物件()
    {
        var item = Item("看 " + GitHub(), ord: 7, ver: 3);
        var before = Json(item);
        var masked = Remask(ConversationEventKinds.Item, item);
        masked["ord"]!.Value<long>().Should().Be(7);
        masked["ver"]!.Value<long>().Should().Be(3);
        masked["id"]!.Value<string>().Should().Be("item1");
        Json(item).Should().Be(before, "純函式：不改呼叫端的物件");
    }

    [Fact]
    public void 鍵的順序照原本()
    {
        var item = Item("看 " + GitHub());
        var masked = Remask(ConversationEventKinds.Item, item);
        masked.Properties().Select(p => p.Name).Should().Equal(item.Properties().Select(p => p.Name));
    }

    private static JObject Line(string t, string s) => new() { ["t"] = t, ["s"] = s };

    private static JObject WithDiff()
    {
        var item = Item(text: null);
        item["diff"] = new JArray(Line("add", PemBegin()), Line("add", PemBody()), Line("add", PemEnd()));
        return item;
    }

    private static JObject WithLeakyNames()
    {
        var item = Item();
        item[GitHub('a')] = 1;
        item[GitHub('b')] = 2;
        item["access_" + "token"] = "abcdefgh" + "12345678";
        return item;
    }

    /// <summary>再遮蔽並斷言結果合格：走遍檢查是假、Validate 通過、嚴格解析得出物件</summary>
    private static JObject Remask(string kind, JObject payload)
    {
        var json = ConversationRemaskRules.Remask(kind, payload);
        ConversationEventRules.Validate(kind, json).Should().BeNull("遮蔽後仍合格（{0}）", kind);
        ConversationEventRules.TryParseStrict(json, out var token).Should().BeTrue();
        var result = token.Should().BeOfType<JObject>().Subject;
        ConversationDlpRules.HasLeak(result).Should().BeFalse("遮蔽後的走遍檢查是假（{0}）", kind);
        return result;
    }
}
