using Myriagent.Agents;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Realtime;

/// <summary>伺服器草稿的純函式規則（WP4.2 計畫步 13；D29）</summary>
public sealed class BrowserDraftRulesTests
{
    private static readonly DraftAccount A = new("la_0000000000000000000000000A", AgentProvider.Claude);

    private static readonly DraftAccount B = new("la_0000000000000000000000000B", AgentProvider.Codex);

    [Fact]
    public void 草稿id與對話id格式不重疊()
    {
        var now = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var ids = Enumerable.Range(0, 1000).Select(i => BrowserDraftRules.NewId(now + i)).ToList();
        ids.Should().OnlyHaveUniqueItems();
        foreach (var id in ids)
        {
            id.Should().MatchRegex("^d[0-9A-HJKMNP-TV-Z]{26}$");
            BrowserDraftRules.IsDraftId(id).Should().BeTrue(id);
            ConversationIds.IsWireId(id).Should().BeFalse("草稿 id 不是對話 id（visible、openConversationIds、目錄都不認）：" + id);
        }

        var rng = new Random(1313);
        for (var i = 0; i < 1000; i++)
        {
            var wire = string.Concat(Enumerable.Range(0, 32).Select(_ => "0123456789abcdef"[rng.Next(16)]));
            ConversationIds.IsWireId(wire).Should().BeTrue();
            BrowserDraftRules.IsDraftId(wire).Should().BeFalse(wire);
        }

        BrowserDraftRules.IsDraftId(null).Should().BeFalse();
        BrowserDraftRules.IsDraftId("").Should().BeFalse();
        BrowserDraftRules.IsDraftId("c1").Should().BeFalse("第 3 階段網頁的本機 id");
        BrowserDraftRules.IsDraftId("d01J9Q0000000000000000000AA").Should().BeTrue();
        BrowserDraftRules.IsDraftId("D01J9Q0000000000000000000AA").Should().BeFalse("前綴要小寫 d");
        BrowserDraftRules.IsDraftId("d01j9q0000000000000000000aa").Should().BeFalse("ULID 要大寫");
        BrowserDraftRules.IsDraftId("d_01J9Q0000000000000000000A").Should().BeFalse("裝置 id 的前綴是 d_");
        BrowserDraftRules.IsDraftId("d81J9Q0000000000000000000AA").Should().BeFalse("ULID 的第一個字最多是 7");
        BrowserDraftRules.IsDraftId("d01J9Q0000000000000000000A").Should().BeFalse("少一個字");
        BrowserDraftRules.IsDraftId("d01J9Q0000000000000000000AAA").Should().BeFalse("多一個字");
        BrowserDraftRules.IsDraftId("d01J9Q000000000000000000IAA").Should().BeFalse("I 不在 Crockford 的字母表");
    }

    [Fact]
    public void 帳戶_要求的在快照裡就用它_否則用第一個_沒有帳戶回null()
    {
        BrowserDraftRules.PickAccount(B.Id, [A, B]).Should().Be(B);
        BrowserDraftRules.PickAccount(null, [A, B]).Should().Be(A, "沒指定（網頁自動送的）");
        BrowserDraftRules.PickAccount("la_不存在", [A, B]).Should().Be(A);
        BrowserDraftRules.PickAccount(B.Id.ToLowerInvariant(), [A, B]).Should().Be(A, "完全相同才算");
        BrowserDraftRules.PickAccount(A.Id, []).Should().BeNull("一個帳戶都沒有");
        BrowserDraftRules.PickAccount(null, []).Should().BeNull();
    }

    [Fact]
    public void 超過上限丟最久沒用的()
    {
        var drafts = Enumerable.Range(1, 9).Select(i => new BrowserDraft("d" + i, A.Id, A.Provider) { Used = i * 10 }).ToList();
        BrowserDraftRules.Evict(drafts, 8).Select(d => d.Id).Should().Equal("d1");
        BrowserDraftRules.Evict(drafts, 6).Select(d => d.Id).Should().Equal("d1", "d2", "d3");
        BrowserDraftRules.Evict(drafts.Take(8).ToList(), 8).Should().BeEmpty("沒超過");
        BrowserDraftRules.Evict(drafts, 9).Should().BeEmpty();

        // 順序看 Used、不看放進來的順序；同值依 id
        var shuffled = new[] { drafts[4], drafts[0] with { Used = 100 }, drafts[8], drafts[2] };
        BrowserDraftRules.Evict(shuffled, 2).Select(d => d.Id).Should().Equal("d3", "d5");
        var tied = new[] { new BrowserDraft("db", A.Id, A.Provider) { Used = 1 }, new BrowserDraft("da", A.Id, A.Provider) { Used = 1 } };
        BrowserDraftRules.Evict(tied, 1).Select(d => d.Id).Should().Equal("da");
        FluentActions.Invoking(() => BrowserDraftRules.Evict(drafts, 0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 寬限剛好到就算過了()
    {
        BrowserDraftRules.GraceOver(TimeSpan.FromSeconds(119.999), 120).Should().BeFalse();
        BrowserDraftRules.GraceOver(TimeSpan.FromSeconds(120), 120).Should().BeTrue();
        BrowserDraftRules.GraceOver(TimeSpan.FromSeconds(121), 120).Should().BeTrue();
        BrowserDraftRules.GraceOver(TimeSpan.Zero, 1).Should().BeFalse();
    }

    [Fact]
    public void 換草稿的墓碑_至少留10分鐘也不短於寬限_每個工作階段最多草稿上限的兩倍()
    {
        // WP4.6 合併前審查 LIVE-1
        BrowserDraftRules.ReplacedKeepSeconds(120).Should().Be(600);
        BrowserDraftRules.ReplacedKeepSeconds(0).Should().Be(600);
        BrowserDraftRules.ReplacedKeepSeconds(900).Should().Be(900, "寬限內重連的網頁一定拿得到");
        BrowserDraftRules.MaxReplacedPerSession(8).Should().Be(16);
        BrowserDraftRules.MaxReplacedPerSession(1).Should().Be(2);
        BrowserDraftRules.MaxReplacedPerSession(0).Should().Be(2, "設定不合理時至少留 2 筆");
    }

    [Fact]
    public void 帳戶快照的id與provider_依原本的順序_不合格的略過()
    {
        var json = new JArray
        {
            new JObject { ["id"] = B.Id, ["provider"] = "Codex", ["label"] = "工作" },
            new JObject { ["id"] = "la_x", ["provider"] = "Unknown" },
            new JObject { ["id"] = "", ["provider"] = "Claude" },
            new JObject { ["id"] = 1, ["provider"] = "Claude" },
            new JObject { ["provider"] = "Claude" },
            new JObject { ["id"] = "la_y", ["provider"] = "claude" },
            new JObject { ["id"] = "la_z" },
            "不是物件",
            new JObject { ["id"] = A.Id, ["provider"] = "Claude" },
        }.ToString();
        BrowserDraftRules.AccountsOf(json).Should().Equal(B, A);
        BrowserDraftRules.AccountsOf("[]").Should().BeEmpty();
        BrowserDraftRules.AccountsOf("{").Should().BeEmpty("JSON 不合格");
        BrowserDraftRules.AccountsOf("{}").Should().BeEmpty("不是陣列");
        foreach (var provider in AgentProviders.All)
        {
            BrowserDraftRules.AccountsOf(new JArray(new JObject { ["id"] = "la_p", ["provider"] = provider.ToString() }).ToString())
                .Should().Equal(new DraftAccount("la_p", provider));
        }
    }

    [Fact]
    public void paneId_字串原樣_其他當成沒帶()
    {
        BrowserDraftRules.PaneId("p1").Should().Be("p1");
        BrowserDraftRules.PaneId(new string('p', BrowserDraftRules.MaxPaneIdLength)).Should().HaveLength(BrowserDraftRules.MaxPaneIdLength);
        BrowserDraftRules.PaneId(new string('p', BrowserDraftRules.MaxPaneIdLength + 1)).Should().BeNull("太長");
        BrowserDraftRules.PaneId("").Should().BeNull();
        BrowserDraftRules.PaneId(null).Should().BeNull();
        BrowserDraftRules.PaneId(JValue.CreateNull()).Should().BeNull();
        BrowserDraftRules.PaneId(7).Should().BeNull("不是字串");
        BrowserDraftRules.PaneId(new JObject()).Should().BeNull();
    }

    [Fact]
    public void 標頭_同正式對話的鍵_isDraft_新對話_沒有狀態列與裝置()
    {
        var draft = new BrowserDraft("d01J9Q0000000000000000000AA", B.Id, AgentProvider.Codex);
        var header = BrowserDraftRules.Header(draft);
        var formal = ConversationViewRules.Header(new ConversationHeaderRow(ConversationIdOf(1), "d_0000000000000000000000000A", A.Id, AgentProvider.Claude, "標題",
            null, null, null, false, null, false, null, null, null, null, ConversationSyncPolicies.Full, 1, 1), new ConversationDeviceInfo("d_1", "電腦", true), false);
        header.Properties().Select(p => p.Name).Should().Equal(formal.Properties().Select(p => p.Name), "網頁照同一組鍵畫標頭");

        header["id"]!.Value<string>().Should().Be(draft.Id);
        header["accountId"]!.Value<string>().Should().Be(B.Id);
        header["provider"]!.Value<string>().Should().Be("Codex");
        header["title"]!.Value<string>().Should().Be("新對話");
        header["isDraft"]!.Value<bool>().Should().BeTrue();
        header["running"]!.Value<bool>().Should().BeFalse();
        header["status"]!.Type.Should().Be(JTokenType.Null, "沒有擁有者裝置不是「電腦離線」");
        header["model"]!.Value<string>().Should().Be(ConversationViewRules.DefaultValue);
        header["effort"]!.Value<string>().Should().Be(ConversationViewRules.DefaultValue);
        header["permission"]!.Value<string>().Should().Be(ConversationViewRules.Permission(AgentProvider.Codex, null).ToString());
        header["accountMissing"]!.Value<bool>().Should().BeFalse();
        header["deviceId"]!.Type.Should().Be(JTokenType.Null);
        header["deviceName"]!.Type.Should().Be(JTokenType.Null);
        header["deviceOnline"]!.Value<bool>().Should().BeFalse();

        // WP4.5 改過的欄位照樣帶出
        var changed = BrowserDraftRules.Header(draft with { Model = "gpt-x", Effort = "high", WorkingDirectory = @"C:\work", CustomFolder = true });
        changed["model"]!.Value<string>().Should().Be("gpt-x");
        changed["effort"]!.Value<string>().Should().Be("high");
        changed["workingDirectory"]!.Value<string>().Should().Be(@"C:\work");
        changed["customFolder"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public void conversation訊息_沒有項目_paneId原樣_draft是null()
    {
        var draft = new BrowserDraft("d01J9Q0000000000000000000AA", A.Id, AgentProvider.Claude);
        var message = BrowserDraftRules.Message(draft, "p7");
        message.Type.Should().Be("conversation");
        message.ConversationId.Should().Be(draft.Id, "對話層的屏障（D23）");
        var json = JObject.Parse(message.Json);
        json.Properties().Select(p => p.Name).Should().Equal("type", "conversationId", "paneId", "header", "items", "hasMore", "composer", "draft");
        json["paneId"]!.Value<string>().Should().Be("p7");
        ((JArray)json["items"]!).Should().BeEmpty();
        json["hasMore"]!.Value<bool>().Should().BeFalse();
        json["draft"]!.Type.Should().Be(JTokenType.Null);
        JToken.DeepEquals(json["header"], BrowserDraftRules.Header(draft)).Should().BeTrue();
        JToken.DeepEquals(json["composer"], ConversationViewRules.Composer(null)).Should().BeTrue("空的輸入區");
        JObject.Parse(BrowserDraftRules.Message(draft, null).Json)["paneId"]!.Type.Should().Be(JTokenType.Null);

        // 正式對話的 conversation 不帶 paneId（照舊）
        JObject.Parse(ConversationGatewayRules.ConversationMessage(ConversationIdOf(1), "{}", [], false, "{}").Json)["paneId"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void 草稿標頭帶deviceId_沒有時是null()
    {
        // WP4.6 步 7（D16）：newConversation 選的電腦，網頁的「電腦：OFFICE-PC」晶片照它查名稱與在線；名稱與在線不填
        var draft = new BrowserDraft("d01J9Q0000000000000000000AA", A.Id, AgentProvider.Claude) { DeviceId = "d_0000000000000000000000000A" };
        var header = BrowserDraftRules.Header(draft);
        header["deviceId"]!.Value<string>().Should().Be("d_0000000000000000000000000A");
        header["deviceName"]!.Type.Should().Be(JTokenType.Null);
        header["deviceOnline"]!.Value<bool>().Should().BeFalse();
        header["status"]!.Type.Should().Be(JTokenType.Null, "有電腦也不顯示「電腦離線」：草稿還沒有對話在那台");
        header.Properties().Select(p => p.Name).Should().Equal(BrowserDraftRules.Header(draft with { DeviceId = null }).Properties().Select(p => p.Name), "鍵與順序不變");
        BrowserDraftRules.Header(draft with { DeviceId = null })["deviceId"]!.Type.Should().Be(JTokenType.Null);
        JObject.Parse(BrowserDraftRules.Message(draft, "p1").Json)["header"]!["deviceId"]!.Value<string>().Should().Be("d_0000000000000000000000000A", "newConversation 的回應與 open 草稿都帶");
    }

    private static string ConversationIdOf(int n) => n.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
}
