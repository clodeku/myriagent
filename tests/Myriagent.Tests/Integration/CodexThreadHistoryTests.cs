using Myriagent.Agents;
using Myriagent.Accounts;
using Myriagent.Cli;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Integration;

[Collection(AppPathsCollection.Name)]
public sealed class CodexThreadHistoryTests
{
    [EnvFact("MYRIAGENT_IT", "需要本機 Codex CLI，以臨時帳戶的合成紀錄測試，不呼叫模型")]
    public async Task 舊格式及分頁歷史可以列出並唯讀展開_其他帳戶無法讀取()
    {
        using var temp = new TempDirectory("codex-history");
        using var scope = new AppPathsScope(temp.CreateDirectory("app"));
        var dir = temp.CreateDirectory("account-a");
        var other = temp.CreateDirectory("account-b");
        var folder = Directory.CreateDirectory(Path.Combine(dir, "sessions", "2026", "01", "01"));
        var id = Guid.CreateVersion7().ToString();
        const string stamp = "2026-01-01T12:00:00.000Z";
        string Line(string type, object payload) => JsonConvert.SerializeObject(new { timestamp = stamp, type, payload });
        var lines = new List<string> {
            Line("session_meta", new { id, timestamp = stamp, cwd = temp.Root, originator = "codex_cli_rs", cli_version = "0.172.0", source = "cli", model_provider = "openai" }),
        };
        for (int i = 0; i < 21; i++)
        {
            var turn = Guid.CreateVersion7().ToString();
            lines.AddRange([
                Line("event_msg", new { type = "task_started", turn_id = turn, started_at = 1767268800L }),
                Line("event_msg", new { type = "user_message", message = "查詢測試題", images = Array.Empty<string>(), local_images = Array.Empty<string>(), text_elements = Array.Empty<string>() }),
                Line("event_msg", new { type = "agent_message", message = "查詢測試答覆" }),
                Line("event_msg", new { type = "task_complete", turn_id = turn, last_agent_message = "查詢測試答覆", completed_at = 1767268801L }),
            ]);
        }
        File.WriteAllLines(Path.Combine(folder.FullName, $"rollout-2026-01-01T12-00-00-{id}.jsonl"), lines);
        var launcher = CliLocator.Locate(AgentProvider.Codex)!.Launcher;
        var target = new ExternalAccountScanTarget("a", "測試帳戶", AgentProvider.Codex, dir);
        async Task Verify(string mode)
        {
            await using (var client = await CodexAppServerClient.StartAsync(launcher, env => env["CODEX_HOME"] = dir, CancellationToken.None))
            {
                var (meta, error) = await client.RequestAsync("thread/read", new JObject { ["threadId"] = id }, TimeSpan.FromSeconds(15));
                error.Should().BeNull();
                meta!["thread"]!.Value<string>("historyMode").Should().Be(mode);
            }
            var list = await CodexThreadHistory.ListAsync(launcher, target, 10);
            var candidate = list.Should().NotBeNull().And.ContainSingle(x => x.SessionId == id).Subject;
            var turns = await CodexThreadHistory.PreviewAsync(launcher, dir, id, null, null);
            var rows = (JArray)turns["rows"]!;
            rows.Should().HaveCount(20);
            var more = await CodexThreadHistory.PreviewAsync(launcher, dir, id, null, turns.Value<string>("nextCursor"));
            var last = ((JArray)more["rows"]!).Single().Value<string>("id");
            rows.Values<string>("id").Should().NotContain(last);
            var shown = rows[0]!.Value<string>("id");
            var page = await CodexThreadHistory.PreviewAsync(launcher, dir, id, shown, null);
            page.ToString().Should().Contain("查詢測試題").And.Contain("查詢測試答覆");
            var imported = await CodexThreadHistory.ImportAsync(launcher, candidate);
            imported!.Items.Select(i => i.Kind).Should().ContainInOrder(AgentItemKind.User, AgentItemKind.Assistant);
        }
        await Verify("legacy");
        var migration = await CliRunner.RunAsync(launcher, ["migrate-rollouts", "--apply", "--thread", id, "--json"],
            env => env["CODEX_HOME"] = dir, TimeSpan.FromMinutes(1), workingDirectory: temp.Root);
        migration.ExitCode.Should().Be(0);
        await Verify("paginated");
        Func<Task> outside = () => CodexThreadHistory.PreviewAsync(launcher, other, id, null, null);
        await outside.Should().ThrowAsync<InvalidOperationException>();
    }
}
