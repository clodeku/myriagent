using System.Text;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;

namespace Myriagent.Tests.Integration;

/// <summary>
/// 調整自動重新命名的指示與節錄用的比較（不是回歸測試、不做斷言）：以真實對話的副本跑真的 CLI，把每個對話的最新要求與產生的標題寫成表格。
/// MYRIAGENT_AUTOTITLE_EVAL_DIR 指到放「&lt;對話 id&gt;.json」（對話資料夾裡 items.json 的副本；不要指向原檔，讀壞的檔會被改名備份）的資料夾，
/// MYRIAGENT_AUTOTITLE_MODELS 是逗號分隔的 Claude 模型（haiku、sonnet…），寫 codex 表示跑 codex exec（預設只跑 AutoTitleRules.ClaudeModel）；
/// 結果寫進同一個資料夾的 results-&lt;模型&gt;.md、給模型的節錄寫成 &lt;對話 id&gt;.excerpt.txt。用系統預設帳戶（~/.claude、~/.codex），每個對話各跑一次（用一點訂閱額度）。執行：
/// MYRIAGENT_AUTOTITLE_EVAL=1 MYRIAGENT_AUTOTITLE_EVAL_DIR=&lt;資料夾&gt; dotnet test tests/Myriagent.Tests/Myriagent.Tests.csproj -c Release --filter "FullyQualifiedName~AutoTitleEval"
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AutoTitleEvalTests
{
    private readonly ITestOutputHelper output;

    public AutoTitleEvalTests(ITestOutputHelper output) => this.output = output;

    [EnvFact("MYRIAGENT_AUTOTITLE_EVAL", "以真實對話的副本比較產生的標題（用一點訂閱額度）")]
    public async Task 以真實對話的副本比較產生的標題()
    {
        var directory = Environment.GetEnvironmentVariable("MYRIAGENT_AUTOTITLE_EVAL_DIR");
        Directory.Exists(directory).Should().BeTrue("MYRIAGENT_AUTOTITLE_EVAL_DIR 要指到放對話副本的資料夾");
        var models = (Environment.GetEnvironmentVariable("MYRIAGENT_AUTOTITLE_MODELS") ?? AutoTitleRules.ClaudeModel)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var files = Directory.GetFiles(directory!, "*.json").Where(f => !Path.GetFileName(f).StartsWith("results", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        files.Should().NotBeEmpty();

        foreach (var model in models)
        {
            var provider = model == "codex" ? AgentProvider.Codex : AgentProvider.Claude;
            var installation = CliLocator.Locate(provider);
            installation.Should().NotBeNull($"這台電腦要安裝 {provider} CLI");
            var account = new AgentAccount { Provider = provider, Label = "系統預設" };
            using var gate = new SemaphoreSlim(4);
            var rows = await Task.WhenAll(files.Select(async file =>
            {
                var items = JsonFile.Read<List<AgentChatItem>>(file) ?? new List<AgentChatItem>();
                var excerpt = AutoTitleRules.BuildExcerpt(items, running: false);
                var task = AutoTitleRules.LatestTaskIndex(items);
                var latest = task >= 0 ? OneLine(items[task].Text, 60) : "";
                if (excerpt != null)
                    await File.WriteAllTextAsync(Path.ChangeExtension(file, ".excerpt.txt"), excerpt);
                if (excerpt == null)
                    return (Id: Path.GetFileNameWithoutExtension(file), Latest: latest, Title: "（沒有節錄）", Seconds: 0.0);
                await gate.WaitAsync();
                try
                {
                    var started = DateTime.UtcNow;
                    AutoTitleResult result;
                    if (provider == AgentProvider.Claude)
                    {
                        var run = await CliRunner.RunAsync(installation!.Launcher, AutoTitleRules.BuildClaudeArgs(model), env => AccountRules.ApplyCommandEnvironment(account, env),
                            AutoTitleRules.Timeout, CancellationToken.None, Path.GetTempPath(), input: excerpt);
                        result = AutoTitleRules.ParseClaude(run);
                    }
                    else
                    {
                        result = await AutoTitleRunner.RunAsync(new AutoTitleRequest(AutoTitleMethod.CodexExec, installation!.Launcher,
                            env => AccountRules.ApplyCommandEnvironment(account, env), excerpt), CancellationToken.None);
                    }
                    return (Id: Path.GetFileNameWithoutExtension(file), Latest: latest, Title: result.Title ?? $"（失敗：{result.Error}）",
                        Seconds: (DateTime.UtcNow - started).TotalSeconds);
                }
                finally
                {
                    gate.Release();
                }
            }));

            var sb = new StringBuilder($"# {model}\n\n| 對話 | 最新的要求 | 標題 | 秒 |\n|---|---|---|---|\n");
            foreach (var row in rows)
                sb.Append($"| {row.Id[..Math.Min(8, row.Id.Length)]} | {row.Latest.Replace("|", "／")} | {row.Title.Replace("|", "／")} | {row.Seconds:F1} |\n");
            await File.WriteAllTextAsync(Path.Combine(directory!, $"results-{model}.md"), sb.ToString());
            output.WriteLine(sb.ToString());
        }
    }

    private static string OneLine(string? text, int max)
    {
        var line = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return line.Length <= max ? line : line[..max] + "…";
    }
}
