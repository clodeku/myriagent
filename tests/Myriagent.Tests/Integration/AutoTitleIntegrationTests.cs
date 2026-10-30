using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;

namespace Myriagent.Tests.Integration;

/// <summary>
/// 自動重新命名的真實 CLI（AutoTitleRunner）：以系統預設帳戶跑一次 claude -p（sonnet）與 codex exec（--ephemeral），
/// 回得出一行整理過的標題（會用到一點點訂閱額度）。執行：MYRIAGENT_IT=1 dotnet test ... --filter "FullyQualifiedName~AutoTitleIntegration"
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AutoTitleIntegrationTests
{
    private const string Reason = "實際執行本機 Claude／Codex CLI 產生標題（用一點點訂閱額度）";

    private readonly ITestOutputHelper output;

    public AutoTitleIntegrationTests(ITestOutputHelper output) => this.output = output;

    private static string Excerpt() => AutoTitleRules.BuildExcerpt(new List<AgentChatItem>
    {
        new() { Kind = AgentItemKind.User, Text = "幫我把 sales.csv 依月份加總，畫成長條圖存成 chart.png" },
        new() { Kind = AgentItemKind.Tool, ToolName = "執行指令", Title = "python plot_sales.py" },
        new() { Kind = AgentItemKind.Assistant, Text = "已依月份加總並畫好長條圖：C:\\work\\chart.png" },
    }, running: false)!;

    private async Task<AutoTitleResult> RunAsync(AgentProvider provider)
    {
        var installation = CliLocator.Locate(provider);
        installation.Should().NotBeNull($"這台電腦要安裝 {provider} CLI 才能跑整合測試");
        var account = new AgentAccount { Provider = provider, Label = "系統預設" };
        var request = new AutoTitleRequest(AutoTitleRules.MethodFor(provider), installation!.Launcher, env => AccountRules.ApplyCommandEnvironment(account, env), Excerpt());
        var started = DateTime.UtcNow;
        var result = await AutoTitleRunner.RunAsync(request, CancellationToken.None);
        output.WriteLine($"{provider}：{(DateTime.UtcNow - started).TotalSeconds:F1} 秒，標題「{result.Title}」，錯誤：{result.Error}");
        return result;
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_sonnet_回一行標題()
    {
        var result = await RunAsync(AgentProvider.Claude);

        result.Error.Should().BeNull();
        result.Title.Should().NotBeNullOrWhiteSpace();
        result.Title!.Length.Should().BeLessThanOrEqualTo(AutoTitleRules.MaxTitleLength);
        result.Title.Should().NotContain("\n");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Codex_exec_回一行標題_不留回覆檔()
    {
        var result = await RunAsync(AgentProvider.Codex);

        result.Error.Should().BeNull();
        result.Title.Should().NotBeNullOrWhiteSpace();
        result.Title!.Length.Should().BeLessThanOrEqualTo(AutoTitleRules.MaxTitleLength);
        var directory = Path.Combine(AppPaths.CacheDirectory, "autotitle");
        Directory.GetFiles(directory, "codex-*.txt").Should().BeEmpty("回覆檔讀完就刪");
    }
}
