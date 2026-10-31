using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 設定頁「模型設定」：每家一個預設模型與預設權限模式、每個模型一個預設推理強度（setConversationDefault）；新對話一律套用，
/// 對話內修改只影響那個對話；舊版「沿用上次選擇」的升級遷移
/// </summary>
public class AgentHubConversationDefaultsTests
{
    [Fact]
    public void 新對話套用設定頁的預設_換模型時推理強度換成該模型的預設_對話內修改不外溢()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "permission", value = "Auto" });
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "effort", model = "default", value = "high" });
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "effort", model = "opus", value = "max" });

        var first = h.NewConversation(h.Main, h.Claude);
        var meta = h.Conversation(first)!;
        meta.Model.Should().BeNull();
        meta.Effort.Should().Be("high", "預設模型那一列的設定值");
        meta.PermissionMode.Should().Be(AgentPermissionMode.Auto);

        h.Command(h.Main, "setOption", new { conversationId = first, key = "model", value = "opus" });
        h.Conversation(first)!.Effort.Should().Be("max", "換模型：推理強度換成那個模型的預設");
        h.Command(h.Main, "setOption", new { conversationId = first, key = "effort", value = "low" });
        h.Command(h.Main, "setOption", new { conversationId = first, key = "permission", value = "Ask" });
        h.Command(h.Main, "setOption", new { conversationId = first, key = "model", value = "sonnet" });
        h.Conversation(first)!.Effort.Should().Be("low", "sonnet 沒有設定值：沿用這個對話目前的");

        var other = h.Conversation(h.NewConversation(h.Main, h.Claude2))!;
        other.Effort.Should().Be("high", "同一家的其他帳戶也套用");
        other.PermissionMode.Should().Be(AgentPermissionMode.Auto);
        var next = h.Conversation(h.NewConversation(h.Main, h.Claude))!;
        next.Model.Should().Be("sonnet", "預設模型沒設定＝沿用這個帳戶上次選的");
        next.Effort.Should().BeNull("sonnet 沒有設定值＝CLI 預設；對話裡改的 low 不外溢");
        next.PermissionMode.Should().Be(AgentPermissionMode.Auto, "對話裡改的權限模式不影響新對話");

        h.Command(h.Main, "setOption", new { conversationId = first, key = "permission", value = "default" });
        h.Conversation(first)!.PermissionMode.Should().Be(AgentPermissionMode.Auto, "default＝回到設定頁的預設");

        // 存檔（讀回一致）與廣播
        h.OnUi(() => h.Settings.ConversationDefaults!["Claude"].Efforts.ToDictionary(p => p.Key, p => p.Value))
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["default"] = "high", ["opus"] = "max" });
        var json = h.OnUi(() => JsonConvert.SerializeObject(h.Settings, JsonFile.Settings));
        var reloaded = JsonConvert.DeserializeObject<AppSettings>(json, JsonFile.Settings)!;
        reloaded.ConversationDefaults!["Claude"].PermissionMode.Should().Be(AgentPermissionMode.Auto);
        reloaded.ConversationDefaults["Claude"].Efforts["opus"].Should().Be("max");

        var cd = h.Main.Last("settings")!["settings"]!["conversationDefaults"]!;
        cd["Claude"]!.Value<string>("permission").Should().Be("Auto");
        var claudeModels = cd["Claude"]!["models"]!.ToList();
        claudeModels.Select(m => m.Value<string>("value")).Should().StartWith("default", "最前面一定有預設模型");
        claudeModels.Single(m => m.Value<string>("value") == "opus").Value<string>("effort").Should().Be("max");
        claudeModels.Single(m => m.Value<string>("value") == "sonnet").Value<string?>("effort").Should().BeNull("沒設定＝CLI 預設");
        claudeModels.Single(m => m.Value<string>("value") == "opus")["efforts"]!.Values<string>().Should().Contain("max");
        cd["Claude"]!["permissionModes"]!.Select(m => m.Value<string>("value")).Should().Equal("Ask", "AcceptEdits", "Plan", "Auto", "FullAccess");
        cd["Codex"]!["permissionModes"]!.Select(m => m.Value<string>("value")).Should().Equal("Ask", "Auto", "FullAccess");
        cd["Codex"]!.Value<string>("permission").Should().Be("Ask");
    }

    [Fact]
    public void 預設模型_新對話與換到另一家套用_帳戶沒有這個模型用帳戶預設_沿用上次選擇照舊()
    {
        using var h = new HubHarness();
        var five = new[] { "low", "medium", "high", "xhigh", "max" };
        h.Ui.Invoke(() =>
        {
            h.Settings.Models[h.Claude.Id] = new List<AgentModelOption>
            {
                new("default", "預設", null, five), new("opus", "Opus 5.5", null, five), new("sonnet", "Sonnet 5.5", null, five),
                new("claude-opus-9", "Opus 9", null, new[] { "low", "high", "max" }),
            };
            h.Settings.Models[h.Claude2.Id] = new List<AgentModelOption> { new("default", "預設", null, five), new("opus", "Opus 5.5", null, five) };
        });
        h.Command(h.Main, "ready", new { mode = "main" });
        h.Main.Last("init")!["settings"]!["conversationDefaults"]!["Claude"]!.Value<string?>("model").Should().BeNull("沒設定過＝沿用上次選擇");

        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "model", value = "opus" });
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "effort", model = "opus", value = "max" });
        var first = h.NewConversation(h.Main, h.Claude);
        h.Conversation(first)!.Model.Should().Be("opus");
        h.Conversation(first)!.Effort.Should().Be("max", "推理強度照預設模型那一列");
        h.Conversation(h.NewConversation(h.Main, h.Claude2))!.Model.Should().Be("opus", "同一家的其他帳戶也套用");

        h.Command(h.Main, "setOption", new { conversationId = first, key = "model", value = "sonnet" });
        h.Conversation(h.NewConversation(h.Main, h.Claude))!.Model.Should().Be("opus", "對話裡換的模型不影響新對話");

        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "model", value = "claude-opus-9" });
        h.Conversation(h.NewConversation(h.Main, h.Claude))!.Model.Should().Be("claude-opus-9");
        h.Conversation(h.NewConversation(h.Main, h.Claude2))!.Model.Should().BeNull("這個帳戶沒有這個模型：帳戶預設模型");

        // 換到另一家的帳戶：同新對話
        var codexDraft = h.NewConversation(h.Main, h.Codex);
        h.Command(h.Main, "setAccount", new { conversationId = codexDraft, accountId = h.Claude.Id });
        h.Conversation(codexDraft)!.Model.Should().Be("claude-opus-9");
        h.Conversation(codexDraft)!.Effort.Should().BeNull("claude-opus-9 沒有設定推理強度");

        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "model", value = "default" });
        h.Conversation(h.NewConversation(h.Main, h.Claude))!.Model.Should().BeNull("帳戶預設模型");

        // 存檔（讀回一致）與廣播
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "model", value = "opus" });
        var json = h.OnUi(() => JsonConvert.SerializeObject(h.Settings, JsonFile.Settings));
        JsonConvert.DeserializeObject<AppSettings>(json, JsonFile.Settings)!.ConversationDefaults!["Claude"].Model.Should().Be("opus");
        var cd = h.Main.Last("settings")!["settings"]!["conversationDefaults"]!;
        cd["Claude"]!.Value<string>("model").Should().Be("opus");
        cd["Codex"]!.Value<string?>("model").Should().BeNull();

        // 改回沿用上次選擇：對話裡換的模型一直都有記下來
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "model", value = "" });
        h.OnUi(() => h.Settings.ConversationDefaults!["Claude"].Model).Should().BeNull();
        h.Conversation(h.NewConversation(h.Main, h.Claude))!.Model.Should().Be("sonnet", "這個帳戶上次在對話裡選的");
        h.Conversation(h.NewConversation(h.Main, h.Claude2))!.Model.Should().BeNull("這個帳戶沒選過");
    }

    [Fact]
    public void 不合法的設定值不改也不廣播()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "setConversationDefault", new { provider = "Codex", key = "permission", value = "Auto" });
        var mark = h.Main.Mark();

        h.Command(h.Main, "setConversationDefault", new { provider = "Codex", key = "permission", value = "Plan" });
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "permission", value = "不存在" });
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "effort", model = "gpt-6", value = "low" });
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "effort", model = "opus", value = "ultra" });
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "model", value = "gpt-6" });
        h.Command(h.Main, "setConversationDefault", new { provider = "Gemini", key = "permission", value = "Auto" });
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "theme", value = "dark" });

        h.Main.Messages("settings", mark).Should().BeEmpty();
        h.OnUi(() => h.Settings.ConversationDefaults!["Codex"].PermissionMode).Should().Be(AgentPermissionMode.Auto);
        h.OnUi(() => h.Settings.ConversationDefaults!["Claude"].Efforts.Count).Should().Be(0);
        h.OnUi(() => h.Settings.ConversationDefaults!["Claude"].Model).Should().BeNull();
    }

    [Fact]
    public async Task CLI回報新的模型清單_設定頁的模型清單跟著更新()
    {
        using var h = new HubHarness();
        var (_, s) = h.StartConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Raise(s, new AgentModelsEvent(new[]
        {
            new AgentModelOption("default", "預設", null, new[] { "low", "high" }),
            new AgentModelOption("claude-opus-9", "Opus 9", null, new[] { "low", "high", "max" }),
        }));

        var settings = await h.Main.WaitForAsync("settings", since: mark);
        settings["settings"]!["conversationDefaults"]!["Claude"]!["models"]!.Select(m => m.Value<string>("value"))
            .Should().Contain("claude-opus-9");
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "effort", model = "claude-opus-9", value = "max" });
        h.OnUi(() => h.Settings.ConversationDefaults!["Claude"].Efforts["claude-opus-9"]).Should().Be("max", "新回報的模型也能設定");
    }

    [Fact]
    public void 升級遷移_權限模式沿用舊版記住的選擇_舊欄位不再寫回()
    {
        using var h = new HubHarness(loadSettings: () => JsonConvert.DeserializeObject<AppSettings>("""
            {
              "LastAccountId": "acc-claude2",
              "AccountDefaults": {
                "acc-claude": { "Model": "opus", "Effort": "high", "PermissionMode": "Plan" },
                "acc-claude2": { "PermissionMode": "Auto" },
                "acc-codex": { "PermissionMode": "FullAccess" }
              }
            }
            """, JsonFile.Settings)!);
        h.Command(h.Main, "ready", new { mode = "main" });   // 網頁就緒：第一次渲染設定時遷移

        h.Main.Last("init")!["settings"]!["conversationDefaults"]!["Claude"]!.Value<string>("permission").Should().Be("Auto");
        h.OnUi(() => h.Settings.ConversationDefaults!["Claude"].PermissionMode).Should().Be(AgentPermissionMode.Auto, "上次用的帳戶記住的");
        h.OnUi(() => h.Settings.ConversationDefaults!["Codex"].PermissionMode).Should().Be(AgentPermissionMode.FullAccess);
        h.OnUi(() => h.Settings.DefaultsFor(h.Claude.Id).Model).Should().Be("opus", "模型照舊沿用");
        var saved = JObject.Parse(h.OnUi(() => JsonConvert.SerializeObject(h.Settings, JsonFile.Settings)));
        saved["AccountDefaults"]!["acc-claude"]!["PermissionMode"].Should().BeNull("遷移完不再寫回舊欄位");
        saved["AccountDefaults"]!["acc-claude"]!["Effort"].Should().BeNull();
        h.Conversation(h.NewConversation(h.Main, h.Codex))!.PermissionMode.Should().Be(AgentPermissionMode.FullAccess);
    }

    [Fact]
    public void 升級遷移_照實際啟動順序先建中樞再載入帳戶_仍沿用舊版的選擇()
    {
        // AppController 先建 AgentHub 才呼叫 Accounts.Initialize：建構式裡看不到帳戶，遷移要等第一次用到時才做
        using var h = new HubHarness(seedAccountsAfterHub: true, loadSettings: () => JsonConvert.DeserializeObject<AppSettings>("""
            {
              "LastAccountId": "acc-codex",
              "AccountDefaults": {
                "acc-claude": { "PermissionMode": "Auto" },
                "acc-codex": { "PermissionMode": "FullAccess" }
              }
            }
            """, JsonFile.Settings)!);
        h.Command(h.Main, "ready", new { mode = "main" });

        h.Main.Last("init")!["settings"]!["conversationDefaults"]!["Codex"]!.Value<string>("permission").Should().Be("FullAccess");
        h.OnUi(() => h.Settings.ConversationDefaults!["Claude"].PermissionMode).Should().Be(AgentPermissionMode.Auto);
        h.OnUi(() => h.Settings.ConversationDefaults!["Codex"].PermissionMode).Should().Be(AgentPermissionMode.FullAccess);
        h.Conversation(h.NewConversation(h.Main, h.Claude))!.PermissionMode.Should().Be(AgentPermissionMode.Auto);
    }

    [Fact]
    public void 已遷移過不重做_缺的那一家補上預設()
    {
        using var again = new HubHarness(loadSettings: () => JsonConvert.DeserializeObject<AppSettings>("""
            {
              "ConversationDefaults": { "Claude": { "PermissionMode": "Ask", "Efforts": { "opus": "max" } } },
              "AccountDefaults": { "acc-claude": { "PermissionMode": "FullAccess" } }
            }
            """, JsonFile.Settings)!);
        again.Command(again.Main, "ready", new { mode = "main" });
        again.OnUi(() => again.Settings.ConversationDefaults!["Claude"].PermissionMode).Should().Be(AgentPermissionMode.Ask, "已遷移過：不再拿舊欄位覆蓋");
        again.OnUi(() => again.Settings.ConversationDefaults!["Claude"].Efforts["opus"]).Should().Be("max");
        again.OnUi(() => again.Settings.ConversationDefaults!.ContainsKey("Codex")).Should().BeTrue("缺的那一家補上預設");
    }

    [Fact]
    public void 設定檔裡不認得的權限模式不讓整份設定失效()
    {
        var settings = JsonConvert.DeserializeObject<AppSettings>("""
            {
              "Theme": "light",
              "ConversationDefaults": { "Claude": { "PermissionMode": "Yolo", "Efforts": { "opus": "max" } } },
              "AccountDefaults": { "acc-claude": { "PermissionMode": "Yolo" } }
            }
            """, JsonFile.Settings)!;

        settings.Theme.Should().Be("light");
        settings.ConversationDefaults!["Claude"].PermissionMode.Should().Be(AgentPermissionMode.Ask);
        settings.ConversationDefaults["Claude"].Efforts["opus"].Should().Be("max");
        settings.AccountDefaults["acc-claude"].PermissionMode.Should().BeNull();
    }

    [Fact]
    public void 自動壓縮上限_每家各自設定_夾進範圍_已開的對話下一則也套用_不支援的代理不收()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "ready", new { mode = "main" });
        var defaults = h.Main.Last("init")!["settings"]!["conversationDefaults"]!;
        defaults["Claude"]!["autoCompactRange"]!.Value<long>("min").Should().Be(67_000);
        defaults["Grok"]!["autoCompactRange"]!.Type.Should().Be(JTokenType.Null, "Grok 只有百分比，不提供");
        defaults["Antigravity"]!["autoCompactRange"]!.Type.Should().Be(JTokenType.Null);
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        session.Sent.All[^1].Settings.AutoCompactTokens.Should().BeNull("預設照 CLI");

        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "autoCompact", value = "400k" });
        h.OnUi(() => h.Settings.ConversationDefaults!["Claude"].AutoCompactTokens).Should().Be(400_000);
        h.Main.Last("settings")!["settings"]!["conversationDefaults"]!["Claude"]!.Value<long?>("autoCompact").Should().Be(400_000);

        h.CompleteTurn(session);
        h.Send(h.Main, id, "第二則");
        session.Sent.All[^1].Settings.AutoCompactTokens.Should().Be(400_000, "已開的對話下一則也套用");

        var (_, codex) = h.StartConversation(h.Main, h.Codex);
        codex.Sent.All[^1].Settings.AutoCompactTokens.Should().BeNull("每家各自設定");

        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "autoCompact", value = "5M" });
        h.OnUi(() => h.Settings.ConversationDefaults!["Claude"].AutoCompactTokens).Should().Be(967_000, "夾進範圍");
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "autoCompact", value = "abc" });
        h.OnUi(() => h.Settings.ConversationDefaults!["Claude"].AutoCompactTokens).Should().Be(967_000, "看不懂的不改");
        h.Command(h.Main, "setConversationDefault", new { provider = "Grok", key = "autoCompact", value = "400k" });
        h.OnUi(() => h.Settings.ConversationDefaults!["Grok"].AutoCompactTokens).Should().BeNull();
        h.Command(h.Main, "setConversationDefault", new { provider = "Claude", key = "autoCompact", value = "" });
        h.OnUi(() => h.Settings.ConversationDefaults!["Claude"].AutoCompactTokens).Should().BeNull("清空回到 CLI 預設");
    }
}
