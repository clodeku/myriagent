using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Health;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 儀表板的健康度（web-protocol 的 health／healthCheck／healthFix）：沒掛掃描器不送；分析中先送 running；30 秒內不重做（force 才重做）；
/// 只送給主視窗；「用代理合併／精簡／整理」開草稿、輸入框預先填好說明、工作資料夾是使用者資料夾。掃描器是假的（不讀任何真的設定資料夾）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubHealthTests
{
    private sealed class FakeScanner : IHealthScanner
    {
        public ConcurrentQueue<IReadOnlyList<HealthAccountInput>> Calls { get; } = new();

        public TaskCompletionSource? Gate { get; set; }

        public HealthScanResult Scan(IReadOnlyList<HealthAccountInput> accounts, CancellationToken ct)
        {
            Calls.Enqueue(accounts);
            Gate?.Task.Wait(ct);
            var result = new HealthScanResult { CheckedAt = DateTime.UtcNow };
            foreach (var a in accounts)
            {
                var scan = new AccountScan { Account = a, MainInstructionPath = Path.Combine(a.ConfigDirectory, "CLAUDE.md") };
                if (a.Provider == AgentProvider.Claude && a.IsSystemDefault)
                    scan.Instructions.Add(new InstructionFileStat(@"C:\h\.claude\CLAUDE.md", "main", new TextMeasure(40_000, 20_000, 300, 9_000), null));
                if (a.Id == "acc-claude2")
                {
                    scan.MemoryIndexes = new List<MemoryIndexStat>
                    {
                        new("C--repo-app", @"C:\h2\projects\C--repo-app\memory\MEMORY.md", 126, 25_497),
                        new("C--repo-small", @"C:\h2\projects\C--repo-small\memory\MEMORY.md", 12, 900),
                    };
                }
                else if (a.Provider == AgentProvider.Claude)
                {
                    scan.MemoryIndexes = new List<MemoryIndexStat> { new("C--repo-ok", @"C:\h\.claude\projects\C--repo-ok\memory\MEMORY.md", 40, 3_000) };
                }
                result.Accounts.Add(scan);
            }
            var claude = new InstructionDoc
            {
                Key = @"c:\h\.claude\claude.md", Provider = AgentProvider.Claude, Label = "Claude Code", Path = @"C:\h\.claude\CLAUDE.md",
                Exists = true, Text = "- 一律用繁體中文回覆\n- 部署後要 curl 驗證\n",
            };
            claude.AccountIds.Add("acc-claude");
            var codex = new InstructionDoc { Key = @"c:\h\.codex\agents.md", Provider = AgentProvider.Codex, Label = "Codex", Path = @"C:\h\.codex\AGENTS.md", Exists = true };
            result.Docs.Add(claude);
            result.Docs.Add(codex);
            return result;
        }
    }

    private static (HubHarness H, FakeScanner Scanner) Create()
    {
        var h = new HubHarness();
        var scanner = new FakeScanner();
        h.Ui.Invoke(() => h.Hub.AttachHealth(scanner));
        return (h, scanner);
    }

    private static Task<JObject> WaitDoneAsync(FakeView view, int since = 0)
        => view.WaitForAsync("health", m => m["health"]?.Value<bool>("running") == false && m["health"]?["checkedAt"]?.Type == JTokenType.Integer, since, timeoutMs: 4000);

    [Fact]
    public void 沒掛掃描器_不送health_init是null()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "healthCheck", new { force = true });
        h.Main.Messages("health").Should().BeEmpty();
        var other = h.AddView("other");
        h.Command(other, "ready");
        other.Last("init")!["health"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task MCP檢查順便拿到CLI的清單_重新評估Claude帳戶_多一項hooks()
    {
        var (h, _) = Create();
        using var dispose = h;
        var mark = h.Main.Mark();
        h.Command(h.Main, "healthCheck");
        var before = (JObject)(await WaitDoneAsync(h.Main, mark))["health"]!;
        ((JArray)before["accounts"]!["acc-claude"]!["items"]!).Should().NotContain(i => i.Value<string>("key") == "hooks");

        var inventory = ClaudeCliInventory.Parse(null,
            JObject.Parse("""{"hooks":[{"event":"PreToolUse","source":"pluginHook","pluginName":"autopilot"},{"event":"SessionStart","source":"userSettings"}]}"""), null);
        h.OnSessionCreated = s =>
        {
            s.McpStatus = () => new AgentMcpStatusResult(Array.Empty<AgentMcpServer>(), null);
            s.Inventory = () => inventory;
        };
        mark = h.Main.Mark();
        h.Command(h.Main, "mcpCheck", new { force = true });

        var after = await h.Main.WaitForAsync("health", m => m["health"]?["accounts"]?["acc-claude"]?["items"] is JArray items
            && items.Any(i => i.Value<string>("key") == "hooks"), mark, timeoutMs: 4000);
        var hooks = ((JArray)after["health"]!["accounts"]!["acc-claude"]!["items"]!).Single(i => i.Value<string>("key") == "hooks");
        hooks.Value<string>("value").Should().Be("2 個・每次呼叫工具 1 個");
        ((JArray)after["health"]!["accounts"]!["acc-codex"]!["items"]!).Should().NotContain(i => i.Value<string>("key") == "hooks", "只有 Claude 用 CLI 的清單");
    }

    [Fact]
    public async Task 分析_先送running再送結果_帳戶與指示檔()
    {
        var (h, scanner) = Create();
        using var dispose = h;
        var mark = h.Main.Mark();
        h.Command(h.Main, "healthCheck");
        h.Main.Messages("health", mark).First()["health"]!.Value<bool>("running").Should().BeTrue();
        var done = (JObject)(await WaitDoneAsync(h.Main, mark))["health"]!;

        scanner.Calls.Single().Select(a => a.Id).Should().Equal("acc-claude", "acc-claude2", "acc-codex");
        var claude = (JObject)done["accounts"]!["acc-claude"]!;
        claude.Value<string>("level").Should().Be("warn");
        claude.Value<string>("summary").Should().Be("CLAUDE.md 偏大");
        claude.Value<bool>("canTrim").Should().BeTrue();
        done["accounts"]!["acc-codex"]!.Value<string>("level").Should().Be("ok");
        var ins = (JObject)done["instructions"]!;
        ins.Value<string>("verdict").Should().Be("needsSync");
        ins.Value<bool>("mergeable").Should().BeTrue();

        // 新開的視窗：init 帶目前的結果；彈出視窗沒有
        var other = h.AddView("other");
        h.Command(other, "ready");
        other.Last("init")!["health"]!["accounts"]!["acc-claude"].Should().NotBeNull();
    }

    [Fact]
    public async Task 三十秒內不重做_只重送_force才重做()
    {
        var (h, scanner) = Create();
        using var dispose = h;
        h.Command(h.Main, "healthCheck");
        await WaitDoneAsync(h.Main);

        var other = h.AddView("other");
        var mark = h.Main.Mark();
        var otherMark = other.Mark();
        h.Command(other, "healthCheck");
        await h.Ui.DrainAsync();
        scanner.Calls.Should().HaveCount(1);
        other.Messages("health", otherMark).Should().ContainSingle("只重送給送指令的視窗");
        h.Main.Messages("health", mark).Should().BeEmpty();

        h.Command(h.Main, "healthCheck", new { force = true });
        await WaitDoneAsync(h.Main, mark);
        scanner.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task 帳戶變了就不受三十秒限制()
    {
        var (h, scanner) = Create();
        using var dispose = h;
        h.Command(h.Main, "healthCheck");
        await WaitDoneAsync(h.Main);
        h.Command(h.Main, "accountUpdate", new { accountId = "acc-codex", safeMode = true });
        var mark = h.Main.Mark();
        h.Command(h.Main, "healthCheck");
        await WaitDoneAsync(h.Main, mark);
        scanner.Calls.Should().HaveCount(2);
        scanner.Calls.Last().Single(a => a.Id == "acc-codex").SafeMode.Should().BeTrue();
    }

    [Fact]
    public async Task 分析中再要force_分析完再做一次()
    {
        var (h, scanner) = Create();
        using var dispose = h;
        scanner.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Command(h.Main, "healthCheck");
        await h.WaitUntilAsync(() => scanner.Calls.Count == 1, "開始分析");
        h.Command(h.Main, "healthCheck", new { force = true });
        h.Command(h.Main, "healthCheck", new { force = true });
        scanner.Gate.SetResult();
        await h.WaitUntilAsync(() => scanner.Calls.Count == 2, "再做一次");
        await WaitDoneAsync(h.Main);
        await h.Ui.DrainAsync();
        scanner.Calls.Should().HaveCount(2, "排隊的只做一次");
    }

    [Fact]
    public void 彈出視窗不送()
    {
        var (h, _) = Create();
        using var dispose = h;
        var id = h.NewConversation(h.Main, h.Claude);
        var pop = h.AddPopout("pop", id);
        h.Command(pop, "ready");
        h.Command(h.Main, "healthCheck");
        pop.Messages("health").Should().BeEmpty();
        pop.Last("init")!["health"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task 用代理合併_開草稿_輸入框填好說明_工作資料夾是使用者資料夾()
    {
        var (h, _) = Create();
        using var dispose = h;
        h.Command(h.Main, "healthCheck");
        await WaitDoneAsync(h.Main);
        var mark = h.Main.Mark();
        h.Command(h.Main, "healthFix", new { task = "merge", accountId = "acc-codex", paneId = "p1" });
        var conv = h.Main.Messages("conversation", mark).Single();
        conv.Value<string>("paneId").Should().Be("p1");
        conv["header"]!.Value<bool>("isDraft").Should().BeTrue();
        conv["header"]!.Value<string>("accountId").Should().Be("acc-codex");
        conv["header"]!.Value<string>("workingDirectory").Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var draft = conv.Value<string>("draft");
        draft.Should().StartWith("請幫我合併並同步這台電腦各個 AI CLI 的全域指示檔");
        draft.Should().Contain(@"Codex：C:\h\.codex\AGENTS.md — 空的");
        h.Sessions.Should().BeEmpty("不自動送出");

        // 草稿的字只送一次
        h.Command(h.Main, "open", new { conversationId = conv.Value<string>("conversationId") });
        var all = h.Main.Messages("conversation", mark);
        all.Should().HaveCount(2);
        all[1].Value<string>("draft").Should().BeNull();
    }

    [Fact]
    public async Task 用代理精簡_與各種不能做的情形()
    {
        var (h, _) = Create();
        using var dispose = h;
        var mark = h.Main.Mark();
        h.Command(h.Main, "healthFix", new { task = "merge", accountId = "acc-claude", paneId = "p1" });
        h.Main.Messages("toast", mark).Single().Value<string>("text").Should().Contain("請先重新分析");

        h.Command(h.Main, "healthCheck");
        await WaitDoneAsync(h.Main);
        mark = h.Main.Mark();
        h.Command(h.Main, "healthFix", new { task = "trim", accountId = "acc-claude2", targetAccountId = "acc-claude", paneId = "p1" });
        var conv = h.Main.Messages("conversation", mark).Single();
        conv["header"]!.Value<string>("accountId").Should().Be("acc-claude2");
        conv.Value<string>("draft").Should().StartWith("請幫我精簡「Claude 主帳戶」（Claude Code）");

        mark = h.Main.Mark();
        h.Command(h.Main, "healthFix", new { task = "trim", accountId = "acc-claude", targetAccountId = "acc-codex", paneId = "p1" });
        h.Command(h.Main, "healthFix", new { task = "merge", accountId = "nope", paneId = "p1" });
        h.Command(h.Main, "healthFix", new { task = "bogus", accountId = "acc-claude", paneId = "p1" });
        h.Main.Messages("conversation", mark).Should().BeEmpty();
        h.Main.Messages("toast", mark).Select(t => t.Value<string>("text")).Should().Equal("這個帳戶沒有可以精簡的全域指示，請先重新分析", "找不到要用的帳戶");
    }

    [Fact]
    public async Task 用代理整理記憶索引_只列超過或接近上限的_沒有就提示()
    {
        var (h, _) = Create();
        using var dispose = h;
        var mark = h.Main.Mark();
        h.Command(h.Main, "healthFix", new { task = "memory", accountId = "acc-claude", targetAccountId = "acc-claude2", paneId = "p1" });
        h.Main.Messages("toast", mark).Single().Value<string>("text").Should().Be("這個帳戶的記憶索引沒有超過或接近上限，請先重新分析", "還沒分析過");

        h.Command(h.Main, "healthCheck");
        var done = (JObject)(await WaitDoneAsync(h.Main))["health"]!;
        done["accounts"]!["acc-claude2"]!.Value<bool>("canTrimMemory").Should().BeTrue();
        done["accounts"]!["acc-claude"]!.Value<bool>("canTrimMemory").Should().BeFalse();
        done["accounts"]!["acc-codex"]!.Value<bool>("canTrimMemory").Should().BeFalse();

        mark = h.Main.Mark();
        h.Command(h.Main, "healthFix", new { task = "memory", accountId = "acc-codex", targetAccountId = "acc-claude2", paneId = "p2" });
        var conv = h.Main.Messages("conversation", mark).Single();
        conv.Value<string>("paneId").Should().Be("p2");
        conv["header"]!.Value<string>("accountId").Should().Be("acc-codex");
        conv["header"]!.Value<bool>("isDraft").Should().BeTrue();
        conv["header"]!.Value<string>("workingDirectory").Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var draft = conv.Value<string>("draft");
        draft.Should().StartWith("請幫我整理「Claude 副帳戶」（Claude Code）的自動記憶索引");
        draft.Should().Contain(@"C--repo-app：C:\h2\projects\C--repo-app\memory\MEMORY.md — 126 行、24.9 KB（超過上限）");
        draft.Should().NotContain("C--repo-small", "在上限內的不列");
        h.Sessions.Should().BeEmpty("不自動送出");

        mark = h.Main.Mark();
        h.Command(h.Main, "healthFix", new { task = "memory", accountId = "acc-claude", targetAccountId = "acc-claude", paneId = "p1" });
        h.Command(h.Main, "healthFix", new { task = "memory", accountId = "acc-claude", targetAccountId = "acc-codex", paneId = "p1" });
        h.Main.Messages("conversation", mark).Should().BeEmpty();
        h.Main.Messages("toast", mark).Select(t => t.Value<string>("text")).Should().Equal(
            "這個帳戶的記憶索引沒有超過或接近上限，請先重新分析", "這個帳戶的記憶索引沒有超過或接近上限，請先重新分析");
    }
}
