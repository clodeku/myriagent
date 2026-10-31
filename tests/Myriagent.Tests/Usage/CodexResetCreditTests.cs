using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Tests.Agents;
using Myriagent.Tests.Hub;
using Myriagent.Tests.TestSupport;
using Myriagent.Usage;
using FakeDevice;
using Newtonsoft.Json.Linq;
using Xunit.Sdk;

namespace Myriagent.Tests.Usage;

/// <summary>
/// Codex 的「使用重置」（account/rateLimitResetCredit/consume）：規則，與帳戶管理員以假的 app-server（PowerShell 腳本，不連網、不用掉任何東西）跑的流程
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class CodexResetCreditTests : IDisposable
{
    /// <summary>
    /// 假的 codex app-server：initialize 回空結果；consume 把 idempotencyKey 記進 calls.txt、回 outcome.txt 的結果（沒有就 reset）；
    /// account/rateLimits/read 回 after.txt 的 availableCount（沒有就 0）。只有 ASCII（Windows PowerShell 以系統碼頁讀沒有 BOM 的腳本）
    /// </summary>
    private const string FakeScript = """
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        $calls = Join-Path $PSScriptRoot 'calls.txt'
        function Record([string]$text) { [System.IO.File]::AppendAllText($calls, $text + [string][char]10) }
        function ReadOr([string]$name, [string]$fallback) {
            $path = Join-Path $PSScriptRoot $name
            if (Test-Path -LiteralPath $path) { return ([System.IO.File]::ReadAllText($path)).Trim() }
            return $fallback
        }
        while ($true) {
            $line = $in.ReadLine()
            if ($line -eq $null) { break }
            if ($line.Trim().Length -eq 0) { continue }
            $msg = $line | ConvertFrom-Json
            if ($msg.id -eq $null) { continue }
            $id = $msg.id
            switch ($msg.method) {
                'initialize' { $out.WriteLine('{"id":' + $id + ',"result":{}}') }
                'account/rateLimitResetCredit/consume' {
                    Record ('consume key=' + [string]$msg.params.idempotencyKey + ' credit=' + [string]$msg.params.creditId)
                    $outcome = ReadOr 'outcome.txt' 'reset'
                    if ($outcome -eq 'error') { $out.WriteLine('{"id":' + $id + ',"error":{"code":-32603,"message":"fake failure"}}') }
                    else { $out.WriteLine('{"id":' + $id + ',"result":{"outcome":"' + $outcome + '"}}') }
                }
                'account/rateLimits/read' {
                    Record 'rateLimits/read'
                    $after = ReadOr 'after.txt' '0'
                    $out.WriteLine('{"id":' + $id + ',"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":3,"windowDurationMins":10080,"resetsAt":1794000000},"secondary":null},"rateLimitResetCredits":{"availableCount":' + $after + ',"credits":[]}}}')
                }
                default { $out.WriteLine('{"id":' + $id + ',"error":{"code":-32601,"message":"unknown"}}') }
            }
        }
        """;

    private readonly TempDirectory temp = new("codex-reset-credit");
    private readonly AppPathsScope paths;
    private readonly UiThread ui = new();
    private readonly HttpClient http = new(new StubHttpHandler());
    private readonly CliManager cli;
    private readonly AccountManager manager;
    private readonly string fakeDir;
    private readonly AgentAccount codex;
    private int changed;

    public CodexResetCreditTests()
    {
        paths = new AppPathsScope(temp.Root);
        fakeDir = temp.CreateDirectory("fake-codex");
        var script = Path.Combine(fakeDir, "fake-codex.ps1");
        File.WriteAllText(script, FakeScript.ReplaceLineEndings("\n"));
        var launcher = FakeLauncherRules.Launcher(AgentProvider.Codex, script, CodexAgentSessionTests.PowerShell)
                       ?? throw new InvalidOperationException("假 app-server 的啟動器沒有通過檢查：" + FakeLauncherRules.Check(AgentProvider.Codex, script,
                           CodexAgentSessionTests.PowerShell, FakeLauncherEnvironment.Current()));
        cli = new CliManager(http, _ => false, () => false, startBackgroundChecks: false);
        codex = new AgentAccount { Id = "x1", Provider = AgentProvider.Codex, Label = "Codex 隔離", ConfigDirectory = Path.Combine(AppPaths.AccountsDirectory, "x1", "codex") };
        manager = ui.Invoke(() =>
        {
            var created = new AccountManager(Post, cli, http, _ => 0) { ResolveLauncher = p => p == AgentProvider.Codex ? launcher : null };
            created.Seed(new[] { codex, new AgentAccount { Id = "c1", Provider = AgentProvider.Claude, Label = "Claude" } }, AccountStatus.Ready);
            created.Changed += () => changed++;
            return created;
        });
    }

    public void Dispose()
    {
        try
        {
            ui.DrainAsync(5).Wait(TimeSpan.FromSeconds(10));
            ui.Invoke(manager.Dispose);
        }
        finally
        {
            ui.Dispose();
            cli.Dispose();
            http.Dispose();
            paths.Dispose();
            temp.Dispose();
        }
        if (!ui.Errors.IsEmpty)
            throw new XunitException("UI 執行緒有未處理的例外：" + string.Join("\n", ui.Errors.Select(e => e.ToString())));
    }

    private void Post(Action action)
    {
        if (ui.IsCurrent)
            action();
        else
            ui.Post(action);
    }

    private string[] Calls()
    {
        var path = Path.Combine(fakeDir, "calls.txt");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    private void Seed(int? resetCredits) => ui.Invoke(() => manager.SeedUsage("x1",
        new UsageSnapshot(DateTimeOffset.UtcNow.AddMinutes(-5), "api", [new UsageWindow("codex:10080", "每週", 100, null, Locked: true)], null, null, null, false, resetCredits)));

    private CodexResetCreditResult Use(string accountId)
    {
        var task = ui.Invoke(() => manager.UseCodexResetCreditAsync(accountId));
        task.Wait(TimeSpan.FromSeconds(60)).Should().BeTrue("假 app-server 應該很快回應");
        return task.Result;
    }

    [Fact]
    public void 使用重置_送consume帶新的idempotencyKey_重讀用量_進行中時標記忙碌()
    {
        Seed(2);
        File.WriteAllText(Path.Combine(fakeDir, "after.txt"), "1");
        var busySeen = false;
        ui.Invoke(() => manager.Changed += () => busySeen |= manager.IsResetCreditBusy("x1"));

        var result = Use("x1");

        result.Should().Be(new CodexResetCreditResult(true, "已使用 1 次免費重置，額度已重置", "success"));
        var consume = Calls().Where(c => c.StartsWith("consume ", StringComparison.Ordinal)).Should().ContainSingle().Subject;
        Guid.TryParse(consume["consume key=".Length..consume.IndexOf(" credit=", StringComparison.Ordinal)], out _).Should().BeTrue(consume);
        consume.Should().EndWith(" credit=", "不指定哪一次，由後端選");
        Calls().Should().Contain("rateLimits/read");
        busySeen.Should().BeTrue();
        ui.Invoke(() => manager.IsResetCreditBusy("x1")).Should().BeFalse();
        var usage = ui.Invoke(() => manager.GetUsage("x1"))!;
        usage.ResetCredits.Should().Be(1, "重讀到的次數");
        usage.Windows.Single().Percent.Should().Be(3);
        usage.Windows.Single().Locked.Should().BeFalse();

        Use("x1");
        var keys = Calls().Where(c => c.StartsWith("consume ", StringComparison.Ordinal)).ToList();
        keys.Should().HaveCount(2).And.OnlyHaveUniqueItems("每按一次一個新的 idempotencyKey");
    }

    [Fact]
    public void 使用重置_沒有次數或不是Codex或沒登入_不起程序()
    {
        Seed(0);
        Use("x1").Should().Be(new CodexResetCreditResult(false, "沒有可用的免費重置次數", "info"));
        Use("c1").Text.Should().Be("只有 Codex 帳戶有免費重置");
        Use("沒有").Text.Should().Be("找不到這個帳戶");
        Seed(1);
        ui.Invoke(() => manager.GetState("x1").Status = AccountStatus.NotLoggedIn);
        Use("x1").Text.Should().Be("這個帳戶還沒登入");
        Calls().Should().BeEmpty();
    }

    [Fact]
    public void 使用重置_Codex回錯誤或沒有需要重置_照樣重讀用量()
    {
        Seed(null);
        File.WriteAllText(Path.Combine(fakeDir, "outcome.txt"), "error");
        File.WriteAllText(Path.Combine(fakeDir, "after.txt"), "2");

        var result = Use("x1");

        result.Reset.Should().BeFalse();
        result.Level.Should().Be("error");
        result.Text.Should().Contain("fake failure");
        ui.Invoke(() => manager.GetUsage("x1"))!.ResetCredits.Should().Be(2);

        File.WriteAllText(Path.Combine(fakeDir, "outcome.txt"), "nothingToReset");
        Use("x1").Should().Be(new CodexResetCreditResult(false, "Codex 回報目前沒有需要重置的額度", "info"));
    }

    [Theory]
    [InlineData("reset", true, "success")]
    [InlineData("alreadyRedeemed", true, "success")]
    [InlineData("nothingToReset", false, "info")]
    [InlineData("noCredit", false, "info")]
    [InlineData("somethingNew", false, "error")]
    public void Describe_依outcome(string outcome, bool reset, string level)
    {
        var result = CodexResetCreditRules.Describe(new JObject { ["outcome"] = outcome }, null);
        result.Reset.Should().Be(reset);
        result.Level.Should().Be(level);
    }

    [Fact]
    public void Describe_錯誤_沒有outcome_outcome不是字串()
    {
        CodexResetCreditRules.Describe(null, "逾時").Should().Match<CodexResetCreditResult>(r => !r.Reset && r.Level == "error" && r.Text.Contains("逾時"));
        CodexResetCreditRules.Describe(new JObject(), null).Text.Should().Contain("沒有 outcome");
        CodexResetCreditRules.Describe(JObject.Parse("""{"outcome":null}"""), null).Level.Should().Be("error");
        CodexResetCreditRules.Describe(JObject.Parse("""{"outcome":1}"""), null).Level.Should().Be("error");
    }

    [Fact]
    public void Block_依序檢查_次數不知道照送()
    {
        CodexResetCreditRules.Block(AgentProvider.Codex, AccountStatus.Ready, null, false, false, true).Should().BeNull();
        CodexResetCreditRules.Block(AgentProvider.Codex, AccountStatus.Ready, 3, false, false, true).Should().BeNull();
        CodexResetCreditRules.Block(AgentProvider.Codex, AccountStatus.Ready, 0, false, false, true).Should().Be("沒有可用的免費重置次數");
        CodexResetCreditRules.Block(AgentProvider.Codex, AccountStatus.Ready, 3, true, false, true).Should().Be("正在使用免費重置，請稍候");
        CodexResetCreditRules.Block(AgentProvider.Codex, AccountStatus.Ready, 3, false, true, true).Should().Be("Codex CLI 正在更新，請稍後再試");
        CodexResetCreditRules.Block(AgentProvider.Codex, AccountStatus.Ready, 3, false, false, false).Should().Be("這台電腦沒有安裝 Codex CLI");
        CodexResetCreditRules.Block(AgentProvider.Codex, AccountStatus.Error, 3, false, false, true).Should().Be("這個帳戶還沒登入");
        CodexResetCreditRules.Block(AgentProvider.Grok, AccountStatus.Ready, 3, false, false, true).Should().Be("只有 Codex 帳戶有免費重置");
        CodexResetCreditRules.Block(null, AccountStatus.Ready, 3, false, false, true).Should().Be("找不到這個帳戶");
    }

    [Fact]
    public void BuildParams_只有idempotencyKey()
    {
        var key = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        CodexResetCreditRules.BuildParams(key).ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"idempotencyKey":"0f8fad5b-d9cb-469f-a165-70867728950e"}""");
    }
}
