using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Myriagent.Tests.Integration;

/// <summary>
/// 以這台電腦真的 CLI 與 winget 驗證 WinGet 安裝的認法與查詢（只讀：只跑 --version、grok update --check、winget list，不更新任何東西）：
/// app 認出的安裝方式和 grok 自己回報的 installer 一致；WinGet 安裝的 CLI，真的 winget list --upgrade-available 的輸出解析得出結果。
/// 執行：MYRIAGENT_IT=1 dotnet test ... --filter "FullyQualifiedName~WingetIntegration"
/// </summary>
public sealed class WingetIntegrationTests
{
    private const string Reason = "實際執行本機的 grok 與 winget（只查詢，不更新、不耗額度）";

    private readonly ITestOutputHelper output;

    public WingetIntegrationTests(ITestOutputHelper output) => this.output = output;

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Grok_app認出的安裝方式和grok_update_check回報的installer一致()
    {
        var installation = CliLocator.Locate(AgentProvider.Grok);
        installation.Should().NotBeNull("這台電腦要安裝 Grok Build CLI");
        output.WriteLine($"{installation!.DisplayPath}：WinGet {installation.Winget?.ToString() ?? "否"}");

        var result = await CliRunner.RunAsync(installation.Launcher, new[] { "update", "--check", "--json" }, AccountRules.ApplyToolEnvironment, TimeSpan.FromSeconds(60));
        output.WriteLine($"grok update --check --json（結束碼 {result.ExitCode}）：{result.Stdout.Trim()}");
        var json = JObject.Parse(result.Stdout[result.Stdout.IndexOf('{')..(result.Stdout.LastIndexOf('}') + 1)]);
        var installer = json.Value<string>("installer");
        (installation.Winget != null).Should().Be(installer == "winget", $"grok 自己說安裝方式是 {installer}");
        if (installation.Winget != null)
            installation.Winget.Id.Should().Be("xAI.GrokBuild");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task WinGet安裝的CLI_真的winget_list輸出解析得出有沒有新版()
    {
        var winget = CliLocator.LocateWinget();
        winget.Should().NotBeNull("這台電腦要有 winget");
        var checkedAny = false;
        foreach (var provider in AgentProviders.All)
        {
            if (CliLocator.Locate(provider) is not { Winget: { } package } installation)
                continue;
            checkedAny = true;
            var result = await CliRunner.RunAsync(new AgentLauncher(winget!, Array.Empty<string>()), CliWingetRules.ListUpgradeArguments(package.Id),
                AccountRules.ApplyToolEnvironment, TimeSpan.FromMinutes(2));
            var check = CliWingetRules.ParseUpgradeCheck(result.ExitCode, result.Stdout, result.Stderr, package.Id);
            output.WriteLine($"{provider} {installation.DisplayPath}（{package.Id}）：結束碼 {CliWingetRules.FormatExitCode(result.ExitCode)}，{check}");
            output.WriteLine(result.Stdout.Trim());
            check.Error.Should().BeNull();
            if (check.Available)
                check.Version.Should().NotBeNull("有新版的那一列要讀得出可用的版本");
        }
        if (!checkedAny)
            output.WriteLine("這台電腦沒有 WinGet 安裝的 CLI：只驗證了找得到 winget");
    }
}
