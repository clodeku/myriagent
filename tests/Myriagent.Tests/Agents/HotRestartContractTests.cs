using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 熱更新契約（WP1.0）：新的模型欄位向後相容（沒有值不寫進檔案、舊檔讀得回來）、重開要求與安全點事件的語意。
/// 兩個會話類別的熱更新行為已由 WP1.2／WP1.3 實作（閒置、沒有程序時重開回 Restarted 等），測試在 ClaudeSessionRestartTests、CodexAgentSessionTests
/// </summary>
public sealed class HotRestartContractTests
{
    [Fact]
    public void 卡片的程序實例與Notice種類_存檔讀回_沒有值時不寫進檔案()
    {
        var card = new AgentChatItem { Kind = AgentItemKind.Approval, Status = AgentItemStatus.Waiting, ProcessInstance = "0f8c2b6e4d1a4f0e9b7c3a5d2e1f0a9b" };
        var notice = new AgentChatItem { Kind = AgentItemKind.Notice, Text = "已用 Claude Code 2.1.297 自動接續", NoticeKind = AgentNoticeKind.CliUpdate };
        var plain = new AgentChatItem { Kind = AgentItemKind.Notice, Text = "一般提示" };

        var json = JsonConvert.SerializeObject(new[] { card, notice, plain }, JsonFile.Settings);
        var back = JsonConvert.DeserializeObject<List<AgentChatItem>>(json, JsonFile.Settings)!;

        json.Should().Contain("\"NoticeKind\": \"CliUpdate\"", "列舉存成名稱");
        back[0].ProcessInstance.Should().Be(card.ProcessInstance);
        back[1].NoticeKind.Should().Be(AgentNoticeKind.CliUpdate);
        back[2].NoticeKind.Should().BeNull();
        back[2].ProcessInstance.Should().BeNull();
        JsonConvert.SerializeObject(plain, JsonFile.Settings).Should().NotContain("NoticeKind").And.NotContain("ProcessInstance", "舊版的 items.json 格式不變");

        var old = JsonConvert.DeserializeObject<AgentChatItem>("""{ "Id": "i1", "Kind": "Approval", "Status": "Waiting" }""", JsonFile.Settings)!;
        old.ProcessInstance.Should().BeNull("舊資料沒有實例：不做比對");
    }

    [Fact]
    public void 對話的最後CLI版本_存檔讀回_沒有值時不寫進檔案()
    {
        var meta = new AgentConversation { Title = "t", LastCliVersion = "2.1.297" };
        var json = JsonConvert.SerializeObject(meta, JsonFile.Settings);
        JsonConvert.DeserializeObject<AgentConversation>(json, JsonFile.Settings)!.LastCliVersion.Should().Be("2.1.297");
        JsonConvert.SerializeObject(new AgentConversation(), JsonFile.Settings).Should().NotContain("LastCliVersion");
    }

    [Fact]
    public void 重開要求_沒給接續訊息時用預設文字_明說不是使用者拒絕()
    {
        var request = new AgentRestartRequest(AgentRestartReason.CliUpdated, AgentRestartMode.SafePoint, "2.1.295", "2.1.297",
            AutoContinue: true, ContinuationText: null, InterruptedTools: Array.Empty<AgentInterruptedTool>(), Settings: null);
        request.EffectiveContinuationText.Should().Be(AgentRestartRequest.DefaultContinuationText);
        AgentRestartRequest.DefaultContinuationText.Should().Contain("不是使用者拒絕").And.Contain("結果未知").And.Contain("尚未傳送新訊息");
        (request with { ContinuationText = "  " }).EffectiveContinuationText.Should().Be(AgentRestartRequest.DefaultContinuationText);
        (request with { ContinuationText = "（系統通知：…）" }).EffectiveContinuationText.Should().Be("（系統通知：…）");

        AgentRestartResult.Unsupported.Outcome.Should().Be(AgentRestartOutcome.NotSupported);
        AgentRestartResult.Unsupported.PreviousProcessInstance.Should().BeNull();
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    public void 安全點事件_三個旗標都沒有才是安全點(bool toolRunning, bool waitingForUser, bool background, bool safe)
        => new AgentSafePointChangedEvent(toolRunning, waitingForUser, background).IsAtSafePoint.Should().Be(safe);

    [Fact]
    public void 新增的參數都有預設值_既有的建構方式不變()
    {
        new AgentProcessExitedEvent(0, null, true).ProcessInstance.Should().BeNull();
        var settings = new AgentSessionSettings("s", false, null, "a", _ => { }, null, null, null, AgentPermissionMode.Ask, Path.GetTempPath(), Path.GetTempPath(), string.Empty, true);
        settings.Cli.Should().BeNull("身分不明：不因此重開程序");
        (settings with { Cli = CliIdentities.Claude("2.1.297") }).Cli!.Version.Should().Be("2.1.297");
    }
}
