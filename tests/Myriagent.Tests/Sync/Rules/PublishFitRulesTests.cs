using System.Text;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// 太大的項目一律截斷（WP4.3 計畫 D16）：截斷在權杖 DLP 之前，留開頭的欄位去掉切點前的權杖字元、留結尾的輸出加中樞的省略前綴，
/// 截完之後經 MaskItem、TryItem、Validate 一定合格。權杖在執行時組字串產生（照契約 D44）
/// </summary>
public sealed class PublishFitRulesTests
{
    private const string Alnum = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private const string ItemId = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8";

    // 前綴拆開拼（原始碼裡沒有完整的前綴＋本體）
    private static readonly string Ghp = "gh" + "p_";

    private static string Token(Random random) => Ghp + new string(Enumerable.Range(0, 36).Select(_ => Alnum[random.Next(Alnum.Length)]).ToArray());

    private static int Bytes(string? s) => s == null ? 0 : Encoding.UTF8.GetByteCount(s);

    private static AgentChatItem Tool(string? text = null, string? output = null, string? detail = null, string? title = null, List<AgentDiffLine>? diff = null)
        => new()
        {
            Id = ItemId,
            Ord = 3,
            Kind = AgentItemKind.Tool,
            Status = AgentItemStatus.Done,
            Text = text ?? string.Empty,
            Output = output,
            Detail = detail,
            Title = title,
            Diff = diff,
        };

    private static AgentChatItem Fit(AgentChatItem item, out IReadOnlyList<string> truncated)
        => PublishFitRules.Truncate(item, PublishFitRules.FieldBudgetBytes, PublishFitRules.ItemBudgetBytes, out truncated);

    /// <summary>截斷→遮蔽→投影→序列化→共用驗證（引擎發布時的順序）</summary>
    private static string? Validate(AgentChatItem fitted)
    {
        ConversationWire.TryItem(CredentialLeakRules.MaskItem(fitted), 1, out var wire).Should().BeTrue();
        return ConversationEventRules.Validate(ConversationEventKinds.Item, ConversationWire.Serialize(wire!));
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])))
                return true;
            if (char.IsLowSurrogate(s[i]) && (i == 0 || !char.IsHighSurrogate(s[i - 1])))
                return true;
        }

        return false;
    }

    [Fact]
    public void 常數逐字釘住()
    {
        PublishFitRules.FieldBudgetBytes.Should().Be(30 * 1024);
        PublishFitRules.ItemBudgetBytes.Should().Be(120 * 1024);
        PublishFitRules.MaxHalvings.Should().Be(3);
        PublishFitRules.HeadMarker(12345).Should().Be("…（後 12,345 字未同步，完整內容在擁有者電腦）");
        PublishFitRules.TruncatableFields.Should().Equal("text", "detail", "title", "output", "diff", "plan");
        (PublishFitRules.FieldBudgetBytes + 1024).Should().BeLessThan(ConversationEventRules.MaxInlineStringBytes, "留遮蔽變長的餘裕");
        PublishFitRules.ItemBudgetBytes.Should().BeLessThan(ConversationEventRules.MaxPayloadBytes);
    }

    [Fact]
    public void 沒有超過_回傳原本的實例()
    {
        var item = Tool("短的文字", "短的輸出", "細節", "標題", [new AgentDiffLine("add", "+ x")]);

        var fitted = Fit(item, out var truncated);

        fitted.Should().BeSameAs(item);
        truncated.Should().BeEmpty();
    }

    [Fact]
    public void text超過30KB_留開頭加說明()
    {
        var original = string.Concat(Enumerable.Range(0, 4000).Select(i => $"第 {i} 段說明文字。\n"));
        Bytes(original).Should().BeGreaterThan(PublishFitRules.FieldBudgetBytes);
        var item = Tool(original);

        var fitted = Fit(item, out var truncated);

        truncated.Should().Equal("text");
        fitted.Should().NotBeSameAs(item);
        item.Text.Should().Be(original, "原本的項目不動");
        Bytes(fitted.Text).Should().BeLessThanOrEqualTo(PublishFitRules.FieldBudgetBytes);
        var markerAt = fitted.Text.LastIndexOf('…');
        var kept = fitted.Text[..markerAt];
        original.Should().StartWith(kept);
        fitted.Text[markerAt..].Should().Be(PublishFitRules.HeadMarker(original.Length - kept.Length));
        Bytes(fitted.Text).Should().BeGreaterThan(PublishFitRules.FieldBudgetBytes - 64, "盡量留滿預算");

        foreach (var (name, make) in new (string, Func<string, AgentChatItem>)[] { ("detail", s => Tool(detail: s)), ("title", s => Tool(title: s)) })
        {
            var other = Fit(make(original), out var fields);
            fields.Should().Equal(name);
            var value = name == "detail" ? other.Detail! : other.Title!;
            value.Should().EndWith(PublishFitRules.HeadMarker(original.Length - value.LastIndexOf('…')));
        }
    }

    [Fact]
    public void 切點落在權杖中間_切點前的權杖字元去掉()
    {
        var random = new Random(20261019);
        var budget = PublishFitRules.FieldBudgetBytes;
        for (var offset = 0; offset <= 45; offset++)
        {
            var token = Token(random);
            var filler = new string('中', 12_000);
            // 切點（預算減掉說明）落在權杖裡第 offset 字附近
            var markerBytes = Bytes(PublishFitRules.HeadMarker(filler.Length + 4000));
            var lead = new string('中', (budget - markerBytes - offset) / 3);
            var text = lead + " " + token + " 之後" + filler;

            var fitted = PublishFitRules.Truncate(Tool(text), budget, PublishFitRules.ItemBudgetBytes, out _);
            var masked = CredentialLeakRules.MaskItem(fitted);

            for (var w = 0; w + 6 <= token.Length; w++)
                masked.Text.Should().NotContain(token.Substring(w, 6), $"offset {offset}：權杖的片段不能留下");
            CredentialLeakRules.HasLeak(masked.Text).Should().BeFalse();
            fitted.Text.Should().EndWith(PublishFitRules.HeadMarker(text.Length - fitted.Text.LastIndexOf('…')));
        }
    }

    [Fact]
    public void output留結尾_加前綴_MaskTruncated換掉第一行()
    {
        var original = string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i}: data-{i % 7}\n"));
        Bytes(original).Should().BeGreaterThan(PublishFitRules.FieldBudgetBytes);

        var fitted = Fit(Tool(output: original), out var truncated);

        truncated.Should().Equal("output");
        Bytes(fitted.Output).Should().BeLessThanOrEqualTo(PublishFitRules.FieldBudgetBytes);
        var prefix = AgentHubRules.OmittedPrefixLength(fitted.Output);
        prefix.Should().BeGreaterThan(0, "加中樞的「…（前 N 字省略）」前綴");
        var body = fitted.Output![prefix..];
        original.Should().EndWith(body);
        AgentHubRules.OmittedCount(fitted.Output).Should().Be(original.Length - body.Length);

        var masked = CredentialLeakRules.MaskItem(fitted).Output!;
        var firstLine = body.IndexOf('\n');
        masked.Should().Be(fitted.Output[..prefix] + CredentialLeakRules.Replacement + body[firstLine..], "切點所在的第一行整行換掉");

        // 中樞已經截過一次（只留尾段）：省略的字數累計
        var hubTrimmed = AgentHubRules.OmittedPrefix(5_000) + original;
        var again = Fit(Tool(output: hubTrimmed), out _);
        var againBody = again.Output![AgentHubRules.OmittedPrefixLength(again.Output)..];
        AgentHubRules.OmittedCount(again.Output).Should().Be(5_000 + original.Length - againBody.Length);
        original.Should().EndWith(againBody);
    }

    [Fact]
    public void 切掉前綴後形成權杖樣式_遮蔽後HasLeak為假()
    {
        var random = new Random(77);
        var budget = PublishFitRules.FieldBudgetBytes;
        var head = new string('h', 25_000) + "\n";
        // 前綴的位元組（省略的字數都是 5 位數，長度固定）；尾段的長度決定切點落在權杖那一行的哪裡
        var prefixBytes = Bytes(AgentHubRules.OmittedPrefix(55_000));
        var line = "x" + Token(random);
        var baseTail = budget - prefixBytes - 1 - line.Length;
        var cuts = 0;
        for (var tail = baseTail - 5; tail <= baseTail + line.Length + 5; tail++)
        {
            // 「x」接權杖：前一個字元是英數字時不算權杖（前綴樣式的邊界）；切點剛好切掉 x 時剩下的就成了權杖樣式
            var body = head + line + "\n" + new string(' ', tail);

            var fitted = PublishFitRules.Truncate(Tool(output: body), budget, PublishFitRules.ItemBudgetBytes, out _);
            var kept = fitted.Output![AgentHubRules.OmittedPrefixLength(fitted.Output)..];
            if (kept.Length > tail + 1 && kept.Length <= tail + 1 + line.Length)
                cuts++;
            var masked = CredentialLeakRules.MaskItem(fitted).Output!;

            CredentialLeakRules.HasLeak(masked).Should().BeFalse($"尾段 {tail}");
            if (kept.Length > tail + 1 && kept.Length <= tail + 1 + line.Length)
            {
                // 切點在那一行裡：切掉 x 之後剩下的片段（可能已經是權杖樣式）整行換掉
                for (var w = 1; w + 6 <= line.Length; w++)
                    masked.Should().NotContain(line.Substring(w, 6), $"尾段 {tail}");
            }
            else
            {
                // 切點在那一行之前：x 還在，前一個字元是英數字的不算權杖（前綴樣式的邊界），照原樣
                masked.Contains(line, StringComparison.Ordinal).Should().Be(kept.Length > tail + 1 + line.Length, $"尾段 {tail}");
            }
        }

        cuts.Should().BeGreaterThan(line.Length - 2, "切點掃過權杖那一行的每個位置（避免空轉通過）");
    }

    [Fact]
    public void 整筆超過120KB_從最大的欄位開始截()
    {
        // 字與空白交錯（整段都是權杖字元的話，留開頭時切點前的整段都會被 SafeLength 去掉）
        string Of(char c, int bytes) => string.Concat(Enumerable.Repeat(c + " ", bytes / 2)) + (bytes % 2 == 1 ? " " : string.Empty);
        var item = Tool(
            text: Of('t', 29 * 1024),
            output: Of('o', 29 * 1024 + 512),
            detail: Of('d', 27 * 1024),
            title: Of('i', 1024),
            diff: Enumerable.Range(0, 250).Select(i => new AgentDiffLine("add", "+" + Of('f', 60))).ToList());
        item.PlanSteps = Enumerable.Range(0, 200).Select(i => new AgentPlanStep { Text = Of('p', 60), Status = "pending" }).ToList();
        PublishFitRules.EstimateBytes(item).Should().BeGreaterThan(PublishFitRules.ItemBudgetBytes, "每個欄位都沒超過 30 KB，整筆超過 120 KB");

        var fitted = Fit(item, out var truncated);

        PublishFitRules.EstimateBytes(fitted).Should().BeLessThanOrEqualTo(PublishFitRules.ItemBudgetBytes);
        truncated.Should().Contain("output", "最大的欄位先截");
        fitted.Title.Should().Be(item.Title, "小的欄位不動");
        (Bytes(item.Output) - Bytes(fitted.Output)).Should().BeGreaterThan(15 * 1024, "超過的部分幾乎都從最大的欄位截");
        Bytes(fitted.Text).Should().BeGreaterThan(29 * 1024 - 512, "其他欄位最多只再截一點點");
        Bytes(fitted.Detail).Should().BeGreaterThan(27 * 1024 - 512);
        Validate(fitted).Should().BeNull();
    }

    [Fact]
    public void 截完TryItem後Validate通過()
    {
        var random = new Random(3);
        string Mixed(int chars) => new(Enumerable.Range(0, chars).Select(_ => random.Next(10) switch
        {
            0 => '"',
            1 => '\\',
            2 => '\n',
            3 => (char)random.Next(1, 0x20),
            4 => '中',
            _ => Alnum[random.Next(Alnum.Length)],
        }).ToArray());

        var emoji = char.ConvertFromUtf32(0x1F600);
        var cases = new (string Name, AgentChatItem Item)[]
        {
            ("超長的中文 text", Tool(string.Concat(Enumerable.Repeat("很長的回覆內容，", 40_000)))),
            ("1 MB 的 output", Tool(output: new string('x', 1 << 20))),
            ("跳脫字元很多", Tool(Mixed(200_000), Mixed(200_000), Mixed(100_000), Mixed(50_000))),
            ("表情符號", Tool(string.Concat(Enumerable.Repeat(emoji, 50_000)), string.Concat(Enumerable.Repeat(emoji, 50_000)))),
            ("一萬行差異", Tool(diff: Enumerable.Range(0, 10_000).Select(i => new AgentDiffLine(i % 2 == 0 ? "add" : "del", Mixed(80))).ToList())),
            ("一行就超過的差異", Tool(diff: [new AgentDiffLine("add", new string('y', 200_000)), new AgentDiffLine("ctx", "後面")])),
            ("所有欄位都很大", Tool(Mixed(60_000), Mixed(60_000), Mixed(60_000), Mixed(60_000),
                Enumerable.Range(0, 3_000).Select(_ => new AgentDiffLine("ctx", Mixed(100))).ToList())),
            ("很長的待辦清單", new AgentChatItem
            {
                Id = ItemId, Ord = 1, Kind = AgentItemKind.Plan,
                PlanSteps = Enumerable.Range(0, 5_000).Select(i => new AgentPlanStep { Text = "步驟 " + i + Mixed(40), Status = "pending" }).ToList(),
            }),
        };

        foreach (var (name, item) in cases)
        {
            var fitted = Fit(item, out var truncated);
            truncated.Should().NotBeEmpty(name);
            Validate(fitted).Should().BeNull(name);
            PublishFitRules.EstimateBytes(fitted).Should().BeLessThanOrEqualTo(PublishFitRules.ItemBudgetBytes, name);
        }
    }

    [Fact]
    public void 中文多位元組邊界_不切開代理字元對()
    {
        var emoji = char.ConvertFromUtf32(0x1F600);
        // 表情符號、中文字都是 3／4 位元組：前面墊 0～8 個 ASCII，切點會落在每一種位置（含代理字元對的中間）
        for (var shift = 0; shift < 9; shift++)
        {
            var text = new string('a', shift) + string.Concat(Enumerable.Repeat(emoji + "中", 20_000));

            var fitted = Fit(Tool(text, output: text, detail: text), out _);

            HasLoneSurrogate(fitted.Text).Should().BeFalse($"text（shift {shift}）");
            HasLoneSurrogate(fitted.Output!).Should().BeFalse($"output（shift {shift}）");
            HasLoneSurrogate(fitted.Detail!).Should().BeFalse($"detail（shift {shift}）");
            Bytes(fitted.Text).Should().BeLessThanOrEqualTo(PublishFitRules.FieldBudgetBytes);
        }
    }

    [Fact]
    public void 回報截斷的欄位()
    {
        var big = new string('z', 40_000);
        var item = Tool(big, diff: Enumerable.Range(0, 2_000).Select(i => new AgentDiffLine("add", "+ 第 " + i + " 行")).ToList());
        item.PlanSteps = [new AgentPlanStep { Text = "小的" }];

        var fitted = Fit(item, out var truncated);

        truncated.Should().Equal("text", "diff");
        fitted.Diff!.Count.Should().BeLessThan(item.Diff!.Count);
        fitted.Diff[^1].T.Should().Be("ctx");
        fitted.Diff[^1].S.Should().StartWith("…（後 ");
        fitted.Diff[..^1].Should().Equal(item.Diff.Take(fitted.Diff.Count - 1), "留前面的行，元素照原樣");
        fitted.PlanSteps.Should().BeSameAs(item.PlanSteps);
        item.Diff.Should().HaveCount(2_000, "原本的清單不動");
    }

    [Fact]
    public void 清單的說明元素記省略的字數()
    {
        var lines = Enumerable.Range(0, 1_000).Select(i => new AgentDiffLine("add", "+" + new string('d', 99))).ToList();
        var fitted = PublishFitRules.Truncate(Tool(diff: lines), 10_000, PublishFitRules.ItemBudgetBytes, out _);

        var keptChars = fitted.Diff![..^1].Sum(d => (long)d.S.Length);
        fitted.Diff[^1].S.Should().Be(PublishFitRules.HeadMarker(lines.Sum(d => (long)d.S.Length) - keptChars));

        var steps = Enumerable.Range(0, 1_000).Select(i => new AgentPlanStep { Text = new string('p', 99), Status = "completed" }).ToList();
        var plan = PublishFitRules.Truncate(new AgentChatItem { Id = ItemId, Ord = 1, Kind = AgentItemKind.Plan, PlanSteps = steps }, 10_000, PublishFitRules.ItemBudgetBytes, out var fields);
        fields.Should().Equal("plan");
        plan.PlanSteps[^1].Text.Should().StartWith("…（後 ");
        plan.PlanSteps[^1].Status.Should().Be("pending");
        plan.PlanSteps[..^1].Should().OnlyContain(s => s.Status == "completed");
    }

    [Fact]
    public void Oversized保留Id與Ord()
    {
        var item = PublishTestItems.Full();

        var placeholder = PublishFitRules.Oversized(item);

        placeholder.Id.Should().Be(item.Id);
        placeholder.Kind.Should().Be(item.Kind);
        placeholder.Status.Should().Be(item.Status);
        placeholder.Ord.Should().Be(item.Ord);
        placeholder.CreatedAt.Should().Be(item.CreatedAt);
        placeholder.UpdatedAt.Should().Be(item.UpdatedAt);
        placeholder.Text.Should().Be(PublishFitRules.OversizedText);
        placeholder.Questions.Should().BeEmpty();
        placeholder.Answers.Should().BeEmpty();
        placeholder.Diff.Should().BeNull();
        placeholder.Attachments.Should().BeEmpty();
        placeholder.Output.Should().BeNull();
        Validate(placeholder).Should().BeNull();
    }
}
