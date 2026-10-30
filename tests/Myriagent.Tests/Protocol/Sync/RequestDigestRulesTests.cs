using System.Text;
using Myriagent.Agents;
using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 卡片摘要 rd1（WP4.1 步 4；docs/sync-protocol.md 14.5、設計 14.11 D36）：欄位順序、「位元組數:值」的編碼、null 的 <c>-:</c>、
/// 不做 Unicode 正規化都以固定向量斷言（向量另以 Python 的 hashlib 從同一串輸入算出，兩邊的實作必須一致）
/// </summary>
public sealed class RequestDigestRulesTests
{
    private static AgentChatItem ApprovalCard() => new()
    {
        Kind = AgentItemKind.Approval,
        Status = AgentItemStatus.Waiting,
        ToolName = "執行指令",
        ToolKind = "bash",
        Title = "npm test",
        Text = string.Empty,
        Detail = "npm test --silent",
        CanAllowForSession = true,
        RequestKey = "req-1",
    };

    private static AgentChatItem QuestionCard() => new()
    {
        Kind = AgentItemKind.Question,
        Status = AgentItemStatus.Waiting,
        RequestKey = "q-7",
        Diff = [new AgentDiffLine("del", "a"), new AgentDiffLine("add", "b")],
        Questions =
        {
            new AgentQuestion
            {
                Id = "要怎麼彙總？", Header = "彙總", Text = "要怎麼彙總？", MultiSelect = false, AllowOther = true, IsSecret = false,
                Options = { new AgentQuestionOption { Label = "依日期", Description = null, Preview = "2026" } },
            },
        },
    };

    [Fact]
    public void 固定向量_核准卡()
    {
        var item = ApprovalCard();

        Encoding.UTF8.GetString(RequestDigestRules.Input(item)).Should().Be(
            "rd1\n8:Approval12:執行指令4:bash8:npm test0:17:npm test --silent-:4:true-:5:req-1-:1:0",
            "kind、toolName、toolKind、title、text、detail、filePath、canAllowForSession、allowSessionLabel、requestKey、差異（null）、題數（0）");
        RequestDigestRules.Compute(item).Should().Be("rd1:885213452b7ba9f431821959ba12a6dea1b8c792f9318ab277c215b2cdd42e36");
    }

    [Fact]
    public void 固定向量_問卷_差異與題目選項的順序()
    {
        var item = QuestionCard();

        Encoding.UTF8.GetString(RequestDigestRules.Input(item)).Should().Be(
            "rd1\n8:Question-:-:-:0:-:-:4:true-:3:q-7" +
            "1:23:del1:a3:add1:b" +
            "1:118:要怎麼彙總？6:彙總18:要怎麼彙總？5:false4:true5:false1:19:依日期-:4:2026");
        RequestDigestRules.Compute(item).Should().Be("rd1:564b84602c30867e761dc7d4fbd5330a07c4f8afe5d8748d0a0a93c5493bb03d");
    }

    [Fact]
    public void 卡片不在等待_RequestKey是null_沒有摘要_遠端不能回應()
    {
        var item = ApprovalCard();
        item.RequestKey = null;

        RequestDigestRules.Compute(item).Should().BeNull();
        RequestDigestRules.Matches(item, "rd1:885213452b7ba9f431821959ba12a6dea1b8c792f9318ab277c215b2cdd42e36").Should().BeFalse();
    }

    [Fact]
    public void 比對_相同才放行_null與大小寫不同都不放行()
    {
        var item = ApprovalCard();
        var digest = RequestDigestRules.Compute(item)!;

        RequestDigestRules.Matches(item, digest).Should().BeTrue();
        RequestDigestRules.Matches(item, null).Should().BeFalse("本機的 RejectStaleCard 在值是 null 時放行，遠端不放行");
        RequestDigestRules.Matches(item, digest.ToUpperInvariant()).Should().BeFalse();
        RequestDigestRules.Matches(item, digest[..^1]).Should().BeFalse();
        RequestDigestRules.Matches(item, "rd1:" + new string('0', 64)).Should().BeFalse();
    }

    [Fact]
    public void 使用者看得到的每個欄位與請求鍵_改了摘要就變()
    {
        var baseline = RequestDigestRules.Compute(QuestionCard());
        var changes = new Dictionary<string, Action<AgentChatItem>>
        {
            ["kind"] = i => i.Kind = AgentItemKind.Approval,
            ["toolName"] = i => i.ToolName = "x",
            ["toolKind"] = i => i.ToolKind = "x",
            ["title"] = i => i.Title = "x",
            ["text"] = i => i.Text = "x",
            ["detail"] = i => i.Detail = "x",
            ["filePath"] = i => i.FilePath = @"C:\x",
            ["canAllowForSession"] = i => i.CanAllowForSession = false,
            ["allowSessionLabel"] = i => i.AllowSessionLabel = "x",
            ["requestKey"] = i => i.RequestKey = "q-8",
            ["diff 的一行"] = i => i.Diff![1] = new AgentDiffLine("add", "c"),
            ["diff 的種類"] = i => i.Diff![1] = new AgentDiffLine("ctx", "b"),
            ["diff 少一行"] = i => i.Diff!.RemoveAt(1),
            ["diff 是 null"] = i => i.Diff = null,
            ["diff 是空的"] = i => i.Diff = [],
            ["題目 id"] = i => i.Questions[0].Id = "x",
            ["題目 header"] = i => i.Questions[0].Header = null,
            ["題目文字"] = i => i.Questions[0].Text = "x",
            ["multiSelect"] = i => i.Questions[0].MultiSelect = true,
            ["allowOther"] = i => i.Questions[0].AllowOther = false,
            ["isSecret"] = i => i.Questions[0].IsSecret = true,
            ["選項 label"] = i => i.Questions[0].Options[0].Label = "依金額",
            ["選項 description"] = i => i.Questions[0].Options[0].Description = "",
            ["選項 preview"] = i => i.Questions[0].Options[0].Preview = null,
            ["多一個選項"] = i => i.Questions[0].Options.Add(new AgentQuestionOption { Label = "x" }),
            ["多一題"] = i => i.Questions.Add(new AgentQuestion { Id = "q2", Text = "q2" }),
        };

        var digests = new HashSet<string> { baseline! };
        foreach (var (name, change) in changes)
        {
            var item = QuestionCard();
            change(item);
            var digest = RequestDigestRules.Compute(item);
            digest.Should().NotBe(baseline, name);
            digests.Add(digest!).Should().BeTrue($"{name} 的摘要與其他改法相同");
        }
    }

    [Fact]
    public void 不在摘要裡的欄位_改了摘要不變()
    {
        var baseline = RequestDigestRules.Compute(ApprovalCard());
        var item = ApprovalCard();
        item.Status = AgentItemStatus.Done;
        item.Output = "已核准";
        item.Decision = "allow";
        item.Seq = 99;
        item.ProcessInstance = "p-2";
        item.Ord = 5;
        item.Risk = AgentRemoteRisk.High;
        item.RequestDigest = "rd1:舊的";
        item.UpdatedAt = DateTime.Now.AddDays(1);

        RequestDigestRules.Compute(item).Should().Be(baseline, "狀態、結果、順序、風險與時間不是使用者核准的內容");
    }

    [Fact]
    public void 值的邊界不會混淆_空字串與null不同()
    {
        var a = ApprovalCard();
        a.Title = "ab";
        a.Text = "c";
        var b = ApprovalCard();
        b.Title = "a";
        b.Text = "bc";
        RequestDigestRules.Compute(a).Should().NotBe(RequestDigestRules.Compute(b), "「位元組數:值」讓相鄰的欄位不會互相吃");

        var empty = ApprovalCard();
        empty.FilePath = string.Empty;
        RequestDigestRules.Compute(empty).Should().NotBe(RequestDigestRules.Compute(ApprovalCard()), "空字串是 0:、null 是 -:");
    }

    [Fact]
    public void 不做Unicode正規化()
    {
        var composed = ApprovalCard();
        composed.Title = "caf" + (char)0xE9;
        var decomposed = ApprovalCard();
        decomposed.Title = "cafe" + (char)0x301;

        RequestDigestRules.Compute(composed).Should().NotBe(RequestDigestRules.Compute(decomposed));
        Encoding.UTF8.GetString(RequestDigestRules.Input(composed)).Should().Contain("5:caf" + (char)0xE9, "é 是兩個 UTF-8 位元組");
        Encoding.UTF8.GetString(RequestDigestRules.Input(decomposed)).Should().Contain("6:cafe" + (char)0x301);
    }

    [Fact]
    public void 存檔裡的null元素_照樣算得出摘要_而且和沒有那一項不同()
    {
        var withNull = QuestionCard();
        withNull.Questions.Add(null!);
        withNull.Diff!.Add(null!);
        withNull.Questions[0].Options.Add(null!);

        var digest = RequestDigestRules.Compute(withNull);

        digest.Should().StartWith("rd1:").And.HaveLength(68);
        digest.Should().NotBe(RequestDigestRules.Compute(QuestionCard()));
    }
}
