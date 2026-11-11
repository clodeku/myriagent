using System.Text.RegularExpressions;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Myriagent.Tests.Integration;

/// <summary>
/// 以這台電腦真的 CLI 驗證帳戶隔離與預設帳戶的用量查詢（唯讀：不送訊息、不耗額度、不更新權杖）。
/// 執行：MYRIAGENT_IT=1 dotnet test ... --filter "FullyQualifiedName~Integration"
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AccountIntegrationTests : IDisposable
{
    private const string Reason = "實際執行本機 Claude／Codex CLI 與用量端點（唯讀，不耗額度）";

    private readonly ITestOutputHelper output;
    private readonly TempDirectory temp = new("it-accounts");

    public AccountIntegrationTests(ITestOutputHelper output) => this.output = output;

    public void Dispose() => temp.Dispose();

    private static string Mask(string? text) => Regex.Replace(text ?? string.Empty, @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", "***@***");

    private static AgentLauncher Launcher(AgentProvider provider)
    {
        var installation = CliLocator.Locate(provider);
        installation.Should().NotBeNull($"這台電腦要安裝 {provider} CLI 才能跑整合測試");
        return installation!.Launcher;
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_空的CLAUDE_CONFIG_DIR_AuthStatus判定未登入()
    {
        var configDir = temp.CreateDirectory("claude-empty");
        var account = new AgentAccount { Provider = AgentProvider.Claude, Label = "隔離測試", ConfigDirectory = configDir };

        var result = await CliRunner.RunAsync(Launcher(AgentProvider.Claude), new[] { "auth", "status", "--json" },
            env => AccountRules.ApplyEnvironment(account, env), TimeSpan.FromSeconds(60));
        output.WriteLine($"結束碼 {result.ExitCode}：{Mask(result.Combined)}");

        var (status, email, plan) = AccountRules.EvaluateClaudeAuthStatus(result.Stdout + "\n" + result.Stderr);
        status.Should().Be(AccountStatus.NotLoggedIn);
        email.Should().BeNull();
        plan.Should().BeNull();
        var json = JObject.Parse(result.Stdout[result.Stdout.IndexOf('{')..(result.Stdout.LastIndexOf('}') + 1)]);
        Path.GetFullPath(json.Value<string>("configDirectory")!).TrimEnd('\\').Should().BeEquivalentTo(Path.GetFullPath(configDir).TrimEnd('\\'), "CLI 實際使用的是隔離的設定資料夾");
        File.Exists(Path.Combine(configDir, ".credentials.json")).Should().BeFalse();
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Codex_空的CODEX_HOME_AccountRead判定未登入()
    {
        var home = temp.CreateDirectory("codex-empty");
        var account = new AgentAccount { Provider = AgentProvider.Codex, Label = "隔離測試", ConfigDirectory = home };

        await using (var client = await CodexAppServerClient.StartAsync(Launcher(AgentProvider.Codex), env => AccountRules.ApplyEnvironment(account, env), CancellationToken.None))
        {
            var (result, error) = await client.RequestAsync("account/read", new JObject { ["refreshToken"] = false }, TimeSpan.FromSeconds(30));
            output.WriteLine($"account/read：{Mask(result?.ToString(Newtonsoft.Json.Formatting.None))} 錯誤：{error}");
            error.Should().BeNull();
            AccountRules.EvaluateCodexAccount(result).Status.Should().Be(AccountStatus.NotLoggedIn);

            var (limits, limitsError) = await client.RequestAsync("account/rateLimits/read", null, TimeSpan.FromSeconds(30));
            output.WriteLine($"account/rateLimits/read：{limits} 錯誤：{limitsError}");
            limitsError.Should().NotBeNull("未登入時查用量回錯誤（實測「codex account authentication required」）");
        }

        File.Exists(Path.Combine(home, "auth.json")).Should().BeFalse();
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude預設帳戶_用量端點成功且解析出時間窗_或權杖過期()
    {
        var account = new AgentAccount { Provider = AgentProvider.Claude, Label = "本機" };
        var credentialFile = AccountRules.GetClaudeCredentialFile(account);
        output.WriteLine($"憑證檔：{credentialFile}");
        using var http = new HttpClient();

        var (outcome, json, error) = await new ClaudeUsageClient(http).FetchAsync(credentialFile, CancellationToken.None);

        output.WriteLine($"結果：{outcome}（{error ?? "沒有錯誤"}）");
        outcome.Should().BeOneOf(new[] { ClaudeUsageOutcome.Success, ClaudeUsageOutcome.TokenExpired }, "原因：{0}", error);
        if (outcome == ClaudeUsageOutcome.TokenExpired)
        {
            output.WriteLine("權杖已過期：本程式不自己更新權杖，等 CLI 下次執行時更新，視為可接受的結果");
            return;
        }

        var (windows, note) = UsageRules.ParseClaudeUsage(json);
        foreach (var w in windows)
            output.WriteLine($"{w.Key}｜{w.Label}｜{w.Percent}%｜{UsageRules.DescribeReset(w.ResetsAt, DateTimeOffset.UtcNow)}｜{UsageRules.Severity(w.Percent, w.Locked)}");
        output.WriteLine($"說明：{note ?? "（無）"}");
        windows.Should().NotBeEmpty("至少一個時間窗");
        windows.Should().Contain(w => w.Key == UsageRules.SessionKey, "訂閱帳戶都有 5 小時時間窗");
        windows.Should().OnlyContain(w => w.Percent >= 0 && w.Percent <= 100);
    }

    /// <summary>能力探查順便以 set_model＋get_settings 問出每個模型的預設強度（2026-11-12；不送訊息、不耗額度）</summary>
    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude預設帳戶_能力探查帶各模型的預設強度()
    {
        using var paths = new AppPathsScope(temp.CreateDirectory("data"));
        var account = new AgentAccount { Provider = AgentProvider.Claude, Label = "本機" };

        var caps = await CapabilityProbe.ProbeClaudeAsync(Launcher(AgentProvider.Claude), account, CancellationToken.None);

        if (caps == null)
        {
            output.WriteLine("權杖快到期或存在認證管理員：守門不跑短命的 claude，視為可接受的結果");
            return;
        }
        foreach (var m in caps.Models)
            output.WriteLine($"{m.Value}｜強度 {string.Join(",", m.Efforts)}｜預設 {m.DefaultEffort ?? "（無）"}");
        var withEfforts = caps.Models.Where(m => m.Efforts.Count > 0).ToList();
        withEfforts.Should().NotBeEmpty();
        withEfforts.Should().OnlyContain(m => m.DefaultEffort != null && m.Efforts.Contains(m.DefaultEffort), "每個有強度可選的模型都問得到預設強度，而且在可選的強度裡");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Codex預設帳戶_RateLimitsRead成功且解析出時間窗()
    {
        var account = new AgentAccount { Provider = AgentProvider.Codex, Label = "本機" };

        await using var client = await CodexAppServerClient.StartAsync(Launcher(AgentProvider.Codex), env => AccountRules.ApplyEnvironment(account, env), CancellationToken.None);
        var (accountResult, accountError) = await client.RequestAsync("account/read", new JObject { ["refreshToken"] = false }, TimeSpan.FromSeconds(30));
        accountError.Should().BeNull();
        var (status, _, plan) = AccountRules.EvaluateCodexAccount(accountResult);
        output.WriteLine($"帳戶狀態：{status}，方案：{plan}");
        status.Should().Be(AccountStatus.Ready, "這台電腦的預設 Codex 帳戶要以 ChatGPT 登入");

        var (limits, error) = await client.RequestAsync("account/rateLimits/read", null, TimeSpan.FromSeconds(30));

        error.Should().BeNull();
        var (windows, credits) = UsageRules.ParseCodexRateLimits(limits);
        foreach (var w in windows)
            output.WriteLine($"{w.Key}｜{w.Label}｜{w.Percent}%｜{UsageRules.DescribeReset(w.ResetsAt, DateTimeOffset.UtcNow)}");
        output.WriteLine($"點數：{credits ?? "（無）"}");
        windows.Should().NotBeEmpty("至少一個時間窗");
    }
}
