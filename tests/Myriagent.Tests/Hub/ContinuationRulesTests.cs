using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>CLI 熱更新後的接續訊息（設計 7.7）：安全點與工具情境兩種文字、等使用者回應時寫明操作還沒執行、背景工作、按「繼續」</summary>
public class ContinuationRulesTests
{
    private static readonly AgentInterruptedTool NpmTest = new("執行指令", "npm test", TimeSpan.FromSeconds(42));

    private static ContinuationContext Context(AgentProvider provider = AgentProvider.Claude, string? from = "2.1.295", string? to = "2.1.297",
        AgentInterruptedTool[]? tools = null, string[]? pending = null, bool background = false, bool userRequested = false)
        => new(provider, from, to, tools ?? Array.Empty<AgentInterruptedTool>(), pending ?? Array.Empty<string>(), background, userRequested);

    [Fact]
    public void 安全點重開_寫明沒有工具在執行_從中斷處繼續_不是使用者拒絕()
    {
        var text = ContinuationRules.Build(Context());

        text.Should().Be(
            "（系統通知：Clode Myriagent 已把 Claude Code 由 2.1.295 更新為 2.1.297 並重新啟動，這不是使用者拒絕或中斷。\n"
            + "重啟時你正在產生回覆，沒有任何工具在執行；請從中斷處繼續，不要重做已完成的工具步驟。\n"
            + "使用者在重啟後尚未傳送新訊息；請繼續原本的工作並回報。）");
        text.Should().NotContain("結果未知", "安全點重開沒有被打斷的工具");
        text.Should().NotContain("背景工作");
    }

    [Fact]
    public void 工具情境_寫明正在執行什麼_已執行多久_結果未知_先確認再決定要不要重跑()
    {
        var text = ContinuationRules.Build(Context(tools: new[] { NpmTest }, background: true));

        text.Should().Be(
            "（系統通知：Clode Myriagent 已把 Claude Code 由 2.1.295 更新為 2.1.297 並重新啟動，這不是使用者拒絕或中斷。\n"
            + "重啟時你正在執行〈執行指令：npm test〉，已執行 42 秒，結果未知；請先確認它是否已生效（例如檢查相關檔案、git 狀態或程序），可安全重跑才重跑。已完成的步驟不要重做。\n"
            + "重啟時還在跑的背景工作已停止，結果未知；請先確認它們做到哪裡，可安全重跑才重跑，不可重複的操作（例如部署、發布、付款）不要直接重跑。\n"
            + "使用者在重啟後尚未傳送新訊息；請繼續原本的工作並回報。）");
    }

    [Fact]
    public void 工具情境_多個工具各自列出經過時間()
    {
        var text = ContinuationRules.Build(Context(tools: new[]
        {
            NpmTest,
            new AgentInterruptedTool("修改檔案", @"C:\repo\a.cs", TimeSpan.FromSeconds(185)),
        }));

        text.Should().Contain(@"重啟時你正在執行〈執行指令：npm test〉（已執行 42 秒）、〈修改檔案：C:\repo\a.cs〉（已執行 3 分 5 秒），結果未知");
    }

    [Fact]
    public void 等使用者回應時被重開_寫明操作還沒執行_要重新提出()
    {
        // E-16：Codex 停在待核准時被關（interrupt 或直接殺）都不會重送舊的核准請求；寫明指令還沒執行，代理會重新要求核准
        var text = ContinuationRules.Build(Context(AgentProvider.Codex, "0.160.1", "0.162.0", pending: new[] { "執行指令：Set-Content e16.txt e16" }));

        text.Should().StartWith("（系統通知：Clode Myriagent 已把 Codex 由 0.160.1 更新為 0.162.0 並重新啟動，這不是使用者拒絕或中斷。");
        text.Should().Contain("重啟時你正在等使用者回應〈執行指令：Set-Content e16.txt e16〉，使用者還沒有回覆，等核准的操作也都還沒有執行；如果仍然需要，請重新提出（會再詢問使用者）。");
        text.Should().NotContain("正在產生回覆", "不是安全點情境");
        text.Should().NotContain("結果未知", "還沒執行的操作沒有結果未知的問題");
    }

    [Fact]
    public void 工具與等核准同時被打斷_兩段都寫()
    {
        var text = ContinuationRules.Build(Context(tools: new[] { NpmTest }, pending: new[] { "執行指令：git push", " " }));

        text.Should().Contain("〈執行指令：npm test〉，已執行 42 秒，結果未知");
        text.Should().Contain("正在等使用者回應〈執行指令：git push〉，");
        text.Split('\n').Should().HaveCount(4, "空白的請求略過");
    }

    [Theory]
    [InlineData("2.1.295", "2.1.297", "Clode Myriagent 已把 Claude Code 由 2.1.295 更新為 2.1.297 並重新啟動")]
    [InlineData(null, "2.1.297", "Clode Myriagent 已把 Claude Code 更新為 2.1.297 並重新啟動")]
    [InlineData("2.1.297", "2.1.297", "Clode Myriagent 為了更新 Claude Code 而重新啟動了它")]
    [InlineData("2.1.295", null, "Clode Myriagent 為了更新 Claude Code 而重新啟動了它")]
    [InlineData(null, null, "Clode Myriagent 為了更新 Claude Code 而重新啟動了它")]
    public void 第一句_依版本是否已知與有沒有換版(string? from, string? to, string expected)
        => ContinuationRules.Build(Context(from: from, to: to)).Should().StartWith($"（系統通知：{expected}，這不是使用者拒絕或中斷。");

    [Fact]
    public void 使用者按繼續_結尾寫明是使用者看過狀況後要求繼續()
    {
        var text = ContinuationRules.Build(Context(tools: new[] { new AgentInterruptedTool("執行指令", "git push origin main", TimeSpan.FromSeconds(3)) }, userRequested: true));

        text.Should().EndWith("使用者看過狀況後按了「繼續」，沒有另外傳送新訊息；請從中斷處繼續原本的工作並回報。）");
        text.Should().Contain("〈執行指令：git push origin main〉，已執行 3 秒，結果未知；請先確認它是否已生效");
        text.Should().NotContain("尚未傳送新訊息");
    }

    [Fact]
    public void 工具摘要只取第一行_太長截斷_沒有摘要只寫名稱()
    {
        var longCommand = new string('x', 300);
        var text = ContinuationRules.Build(Context(tools: new[]
        {
            new AgentInterruptedTool("執行指令", "npm run build\nnpm test", TimeSpan.FromSeconds(1)),
            new AgentInterruptedTool("執行指令", longCommand, TimeSpan.FromSeconds(1)),
            new AgentInterruptedTool("子代理", null, TimeSpan.FromSeconds(1)),
        }));

        text.Should().Contain("〈執行指令：npm run build〉").And.NotContain("npm test");
        text.Should().Contain("〈子代理〉");
        text.Should().NotContain(longCommand);
        text.Should().Contain("〈" + ("執行指令：" + longCommand)[..ContinuationRules.MaxOperationChars] + "…〉");
    }

    [Fact]
    public void app更新的接續訊息_說明是app本身更新並重開()
    {
        var text = ContinuationRules.Build(new ContinuationContext(AgentProvider.Codex, "0.162.0", "0.162.0", Array.Empty<AgentInterruptedTool>(),
            Array.Empty<string>(), false, AppFrom: "0.2.1523", AppTo: "0.2.1530"));
        text.Should().StartWith("（系統通知：Clode Myriagent 已由 0.2.1523 更新為 0.2.1530 並重新啟動");
        text.Should().Contain("不是使用者拒絕");
        text.Should().NotContain("更新 Codex");
    }

    [Fact]
    public void app更新的接續訊息_等你回應的請求要重新提出()
    {
        var text = ContinuationRules.Build(new ContinuationContext(AgentProvider.Claude, null, null, Array.Empty<AgentInterruptedTool>(),
            new[] { "執行指令：git status" }, false, AppFrom: "0.2.1", AppTo: "0.2.2"));
        text.Should().Contain("〈執行指令：git status〉");
        text.Should().Contain("請重新提出");
    }

    [Fact]
    public void app版本沒變_說明是為了app更新停下後重新啟動()
    {
        var text = ContinuationRules.Build(new ContinuationContext(AgentProvider.Claude, "2.1.0", "2.1.0", Array.Empty<AgentInterruptedTool>(),
            Array.Empty<string>(), false, AppFrom: "0.2.5", AppTo: "0.2.5"));
        text.Should().StartWith("（系統通知：Clode Myriagent 為了套用自身的更新而停下了 Claude Code，現在重新啟動了它");
    }

    [Fact]
    public void 預設接續訊息與中樞的版本講同一件事()
    {
        // 會話沒有拿到中樞的訊息時用契約的預設文字：兩者都要明說不是使用者拒絕、使用者沒有送新訊息
        var text = ContinuationRules.Build(Context());
        foreach (var key in new[] { "不是使用者拒絕", "尚未傳送新訊息" })
        {
            text.Should().Contain(key);
            AgentRestartRequest.DefaultContinuationText.Should().Contain(key);
        }
    }
}
