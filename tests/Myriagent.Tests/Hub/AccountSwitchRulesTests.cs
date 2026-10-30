using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Usage;

namespace Myriagent.Tests.Hub;

/// <summary>額度用完時自動換帳戶繼續的純規則：哪些帳戶能接手、挑哪一個、提示與接續訊息</summary>
public class AccountSwitchRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8));

    private static readonly IReadOnlyDictionary<string, DateTimeOffset> NoneExhausted = new Dictionary<string, DateTimeOffset>();

    private static UsageWindow Session(double percent, double? resetHours, bool locked = false)
        => new(UsageRules.SessionKey, "5 小時", percent, resetHours.HasValue ? Now.AddHours(resetHours.Value) : null, locked);

    private static UsageWindow Weekly(double percent, double? resetHours, bool locked = false)
        => new(UsageRules.WeeklyKey, "每週", percent, resetHours.HasValue ? Now.AddHours(resetHours.Value) : null, locked);

    private static AccountSwitchCandidate Claude(string id, int order, params UsageWindow[] windows)
        => new(id, AgentProvider.Claude, AccountStatus.Ready, null, windows, order);

    [Fact]
    public void Supports_四種代理都支援_未知列舉值丟例外()
    {
        AgentProviders.All.Where(AccountSwitchRules.Supports).Should().BeEquivalentTo(AgentProviders.All);
        var act = () => AccountSwitchRules.Supports((AgentProvider)999);
        act.Should().Throw<ArgumentOutOfRangeException>("代理種類一律 switch，未知值丟例外");
    }

    [Fact]
    public void Choose_開啟跨代理備援時同家優先_同家無可用才挑其他代理_Antigravity分桶獨立判定()
    {
        var agy3pLocked = new UsageWindow("agy-3p-5h", "5 小時・Claude/GPT", 100, Now.AddHours(4), Locked: true);
        var agyGeminiOk = new UsageWindow("agy-gemini-5h", "5 小時・Gemini", 20, Now.AddHours(1));
        var candidates = new List<AccountSwitchCandidate>
        {
            Claude("claude1", 0, Session(100, 3, locked: true)),
            new("agy1", AgentProvider.Antigravity, AccountStatus.Ready, ["gemini-3.8-pro", "claude-sonnet-4-6"], [agy3pLocked, agyGeminiOk], 1),
            new("codex1", AgentProvider.Codex, AccountStatus.Ready, ["gpt-5.3-codex"], [new UsageWindow("codex:300", "5 小時", 50, Now.AddHours(2))], 2),
        };

        // 未開啟跨代理備援：同家沒人可用回 null
        AccountSwitchRules.Choose(AgentProvider.Claude, "claude1", "opus", candidates, NoneExhausted, Now, allowCrossProvider: false)
            .Should().BeNull();

        // 開啟跨代理備援：不把來源的 "opus" 拿去擋跨家帳戶；Antigravity 的 Gemini 分桶有額度且重置最早（1 小時）→ 選 agy1
        AccountSwitchRules.Choose(AgentProvider.Claude, "claude1", "opus", candidates, NoneExhausted, Now, allowCrossProvider: true)
            .Should().Be(new AccountSwitchChoice("agy1", Now.AddHours(1)));

        // 若有同家合格帳戶，就算重置比較晚也優先選同家
        candidates.Add(Claude("claude2", 3, Session(40, 4.5)));
        AccountSwitchRules.Choose(AgentProvider.Claude, "claude1", "opus", candidates, NoneExhausted, Now, allowCrossProvider: true)
            .Should().Be(new AccountSwitchChoice("claude2", Now.AddHours(4.5)));

        // 跨代理接續訊息寫出原本與接手的代理產品名
        var crossText = AccountSwitchRules.BuildContinuation(AgentProvider.Antigravity, ["執行指令：dotnet test"], false, AgentProvider.Claude);
        crossText.Should().Contain("原本的 Claude Code 帳戶訂閱額度用完").And.Contain("自動改用 Antigravity 帳戶接續");
    }

    [Fact]
    public void Choose_挑還有額度且下一次重置最早的()
    {
        var candidates = new[]
        {
            Claude("a", 0, Session(40, 4), Weekly(30, 100)),
            Claude("b", 1, Session(90, 0.5), Weekly(50, 120)),   // 重置最早
            Claude("c", 2, Session(10, 2), Weekly(20, 48)),
        };
        AccountSwitchRules.Choose(AgentProvider.Claude, "current", null, candidates, NoneExhausted, Now)
            .Should().Be(new AccountSwitchChoice("b", Now.AddMinutes(30)));
    }

    [Fact]
    public void Choose_每週的重置比5小時早時也照最早的()
    {
        var candidates = new[]
        {
            Claude("a", 0, Session(40, 3), Weekly(30, 100)),
            Claude("b", 1, Session(0, null), Weekly(70, 1)),   // 5 小時窗還沒開始，但每週一小時後重置
        };
        AccountSwitchRules.Choose(AgentProvider.Claude, "current", null, candidates, NoneExhausted, Now)!.AccountId.Should().Be("b");
    }

    [Fact]
    public void Choose_排除目前的帳戶_別種代理_沒登入或API計費_最近用完_沒有額度_不支援模型()
    {
        var candidates = new List<AccountSwitchCandidate>
        {
            Claude("current", 0, Session(10, 1)),
            new("codex", AgentProvider.Codex, AccountStatus.Ready, null, Array.Empty<UsageWindow>(), 1),
            new("loggedout", AgentProvider.Claude, AccountStatus.NotLoggedIn, null, null, 2),
            new("apikey", AgentProvider.Claude, AccountStatus.ApiKey, null, null, 3),
            new("checking", AgentProvider.Claude, AccountStatus.Checking, null, null, 4),
            Claude("exhausted", 5, Session(10, 1)),
            Claude("full", 6, Session(100, 1)),
            Claude("locked", 7, Session(60, 1, locked: true)),
            Claude("weeklyfull", 8, Session(10, 1), Weekly(100, 10)),
            new("noopus", AgentProvider.Claude, AccountStatus.Ready, ["default", "sonnet", "haiku"], [Session(10, 1)], 9),
        };
        var exhausted = new Dictionary<string, DateTimeOffset> { ["exhausted"] = Now.AddHours(2) };

        AccountSwitchRules.Choose(AgentProvider.Claude, "current", "opus", candidates, exhausted, Now).Should().BeNull();

        // 加一個合格的就選它
        candidates.Add(Claude("ok", 10, Session(99.9, 4)));
        AccountSwitchRules.Choose(AgentProvider.Claude, "current", "opus", candidates, exhausted, Now)!.AccountId.Should().Be("ok");
    }

    [Fact]
    public void Choose_用完的期限已過_重置時間已過的用滿時間窗_都可以再選()
    {
        var candidates = new[]
        {
            Claude("a", 0, Session(100, -0.1, locked: true)),   // 已經重置（快取還沒更新）
        };
        var exhausted = new Dictionary<string, DateTimeOffset> { ["a"] = Now.AddMinutes(-1) };
        AccountSwitchRules.Choose(AgentProvider.Claude, "current", null, candidates, exhausted, Now)
            .Should().Be(new AccountSwitchChoice("a", null), "重置過的時間窗歸零、下一次重置不明");
    }

    [Fact]
    public void Choose_不知道用量或重置時間的排最後_同時重置取使用率低的_再照帳戶順序()
    {
        var unknown = new AccountSwitchCandidate("unknown", AgentProvider.Claude, AccountStatus.Ready, null, null, 0);
        var later = Claude("later", 1, Session(10, 4));
        AccountSwitchRules.Choose(AgentProvider.Claude, "current", null, [unknown, later], NoneExhausted, Now)!.AccountId.Should().Be("later");
        AccountSwitchRules.Choose(AgentProvider.Claude, "current", null, [unknown], NoneExhausted, Now)
            .Should().Be(new AccountSwitchChoice("unknown", null), "沒有用量資料不算沒額度");

        var busy = Claude("busy", 0, Session(80, 2));
        var idle = Claude("idle", 5, Session(20, 2));
        AccountSwitchRules.Choose(AgentProvider.Claude, "current", null, [busy, idle], NoneExhausted, Now)!.AccountId.Should().Be("idle");

        var first = Claude("first", 0, Session(20, 2));
        var second = Claude("second", 1, Session(20, 2));
        AccountSwitchRules.Choose(AgentProvider.Claude, "current", null, [second, first], NoneExhausted, Now)!.AccountId.Should().Be("first");
    }

    [Fact]
    public void 模型專屬的每週上限只管那個模型()
    {
        var opusFull = new UsageWindow($"{UsageRules.WeeklyKey}:Opus", "每週・Opus", 100, Now.AddDays(2), Locked: true);
        AccountSwitchRules.AppliesToModel(opusFull, "opus").Should().BeTrue();
        AccountSwitchRules.AppliesToModel(opusFull, "claude-opus-4-1").Should().BeTrue();
        AccountSwitchRules.AppliesToModel(opusFull, "sonnet").Should().BeFalse();
        AccountSwitchRules.AppliesToModel(opusFull, null).Should().BeTrue("帳戶預設模型不知道是哪一個，一律算");
        AccountSwitchRules.AppliesToModel(opusFull, "default").Should().BeTrue();
        AccountSwitchRules.AppliesToModel(Session(100, 1), "sonnet").Should().BeTrue();

        AccountSwitchRules.HasQuota([Session(20, 1), opusFull], "sonnet", Now).Should().BeTrue();
        AccountSwitchRules.HasQuota([Session(20, 1), opusFull], "opus", Now).Should().BeFalse();
        AccountSwitchRules.NextReset([Session(20, 3), opusFull], "sonnet", Now).Should().Be(Now.AddHours(3), "不適用的時間窗不看重置時間");
    }

    [Fact]
    public void SupportsModel_帳戶預設或沒回報清單都算_有清單時要有這個模型()
    {
        AccountSwitchRules.SupportsModel(null, "opus").Should().BeTrue();
        AccountSwitchRules.SupportsModel([], "opus").Should().BeTrue();
        AccountSwitchRules.SupportsModel(["default", "sonnet"], "opus").Should().BeFalse();
        AccountSwitchRules.SupportsModel(["default", "Opus"], "opus").Should().BeTrue();
        AccountSwitchRules.SupportsModel(["sonnet"], null).Should().BeTrue();
        AccountSwitchRules.SupportsModel(["sonnet"], "default").Should().BeTrue();
    }

    [Fact]
    public void 用完的帳戶_重置時間與不再選的期限()
    {
        AccountSwitchRules.ExhaustedResetsAt([Session(100, 2, locked: true), Weekly(40, 50)], null, Now).Should().Be(Now.AddHours(2));
        AccountSwitchRules.ExhaustedResetsAt([Session(100, 2), Weekly(100, 50, locked: true)], null, Now).Should().Be(Now.AddHours(50), "取最晚的：兩個都重置才能用");
        AccountSwitchRules.ExhaustedResetsAt([Session(30, 2)], null, Now).Should().BeNull();
        AccountSwitchRules.ExhaustedResetsAt(null, null, Now).Should().BeNull();

        AccountSwitchRules.ExhaustedUntil(Now.AddHours(3), Now).Should().Be(Now.AddHours(3));
        AccountSwitchRules.ExhaustedUntil(null, Now).Should().Be(Now + AccountSwitchRules.UnknownResetHold);
        AccountSwitchRules.ExhaustedUntil(Now.AddMinutes(-5), Now).Should().Be(Now + AccountSwitchRules.UnknownResetHold, "已過的重置時間不可信");
    }

    [Fact]
    public void 提示_寫出誰用完何時重置與改用哪個帳戶()
    {
        var taipei = TimeZoneInfo.CreateCustomTimeZone("test+8", TimeSpan.FromHours(8), "test+8", "test+8");
        AccountSwitchRules.BuildNotice("主帳戶", new DateTimeOffset(2026, 10, 23, 22, 10, 0, TimeSpan.Zero), "副帳戶", Now.AddMinutes(125), Now, taipei)
            .Should().Be("「主帳戶」的額度已用完（10/24 06:10 重置），已自動改用「副帳戶」（2 小時 5 分後重置）繼續這個對話；新帳戶會收到先前對話的節錄，從中斷處接著做。");
        AccountSwitchRules.BuildNotice(null, null, "副帳戶", null, Now, taipei)
            .Should().Be("原帳戶的額度已用完，已自動改用「副帳戶」繼續這個對話；新帳戶會收到先前對話的節錄，從中斷處接著做。");
        AccountSwitchRules.BuildNotice("副帳戶", null, "第三帳戶", null, Now, taipei, homeLabel: "主帳戶")
            .Should().Be("「副帳戶」的額度已用完，已自動改用「第三帳戶」繼續這個對話；新帳戶會收到先前對話的節錄，從中斷處接著做。「主帳戶」的額度恢復後會自動換回去。");
        AccountSwitchRules.BuildNoCandidateNotice(AgentProvider.Codex).Should().StartWith("其他 Codex 帳戶也沒有可用的額度");
    }

    [Fact]
    public void 接續訊息_說明換帳戶_列出做過的步驟_背景工作已停止()
    {
        var text = AccountSwitchRules.BuildContinuation(AgentProvider.Claude, ["執行指令：npm test", "編輯：src/a.ts（未完成）"], backgroundWorkStopped: true);
        text.Should().StartWith("（系統通知：原本的帳戶訂閱額度用完");
        text.Should().Contain("不是使用者中斷");
        text.Should().Contain("〈執行指令：npm test〉、〈編輯：src/a.ts（未完成）〉");
        text.Should().Contain("已完成的步驟不要重做");
        text.Should().Contain("背景工作已隨舊的程序停止");
        text.Should().EndWith("請繼續原本的工作並回報。）");

        var plain = AccountSwitchRules.BuildContinuation(AgentProvider.Codex, [], backgroundWorkStopped: false);
        plain.Should().NotContain("做過的步驟").And.NotContain("背景工作").And.Contain("另一個 Codex 帳戶");
    }

    [Fact]
    public void 中斷前的步驟_最後一則使用者訊息之後的工具_最多12個_標未完成()
    {
        var items = new List<AgentChatItem>
        {
            new() { Kind = AgentItemKind.Tool, ToolName = "讀取檔案", Title = "舊的.cs" },
            new() { Kind = AgentItemKind.User, Text = "修好建置" },
            new() { Kind = AgentItemKind.Assistant, Text = "好" },
            new() { Kind = AgentItemKind.Tool, ToolName = "執行指令", Title = "dotnet build\n第二行" },
            new() { Kind = AgentItemKind.Tool, ToolName = "編輯", Title = "a.cs", Status = AgentItemStatus.Cancelled },
            new() { Kind = AgentItemKind.Tool, ToolName = null, Title = null },
            new() { Kind = AgentItemKind.Error, Text = "You've hit your session limit" },
        };
        AccountSwitchRules.RecentSteps(items).Should().Equal("執行指令：dotnet build", "編輯：a.cs（未完成）", "工具");

        var many = new List<AgentChatItem> { new() { Kind = AgentItemKind.User, Text = "跑" } };
        for (int i = 1; i <= 20; i++)
            many.Add(new AgentChatItem { Kind = AgentItemKind.Tool, ToolName = "執行指令", Title = $"step{i}" });
        var steps = AccountSwitchRules.RecentSteps(many);
        steps.Should().HaveCount(AccountSwitchRules.MaxRecentSteps);
        steps[0].Should().Be("執行指令：step9");
        steps[^1].Should().Be("執行指令：step20");

        var cleared = new List<AgentChatItem>
        {
            new() { Kind = AgentItemKind.Tool, ToolName = "執行指令", Title = "清除前" },
            new() { Kind = AgentItemKind.Notice, Text = AgentHubRules.ClearNoticeText, NoticeKind = AgentNoticeKind.Clear },
            new() { Kind = AgentItemKind.Tool, ToolName = "執行指令", Title = "清除後" },
        };
        AccountSwitchRules.RecentSteps(cleared).Should().Equal("執行指令：清除後");
    }

    // ───── 執行中手動換帳戶（使用者 2026-10-23） ─────

    [Fact]
    public void 執行中換帳戶_只有Claude與Codex_每種代理都有答案()
    {
        foreach (var provider in AgentProviders.All)
            AccountSwitchRules.SupportsSwitchWhileRunning(provider).Should().BeTrue($"{provider}：2026-10-24 起四種都能在執行中換");
        FluentActions.Invoking(() => AccountSwitchRules.SupportsSwitchWhileRunning((AgentProvider)99)).Should().Throw<ArgumentOutOfRangeException>("新種類要明確決定");
    }

    [Fact]
    public void 執行中換帳戶的阻擋原因_四種代理之間都可以互換()
    {
        foreach (var current in AgentProviders.All)
        foreach (var target in AgentProviders.All)
            AccountSwitchRules.WhileRunningBlock(current, target).Should().BeNull($"{current} → {target}：使用者 2026-10-24 要四種都能互換");
        FluentActions.Invoking(() => AccountSwitchRules.WhileRunningBlock(AgentProvider.Claude, (AgentProvider)99)).Should().Throw<ArgumentOutOfRangeException>("新種類要明確決定");
    }

    [Fact]
    public void Claude與Codex互換的接續訊息_寫換了代理_同一種照舊寫換了帳戶()
    {
        var cross = AccountSwitchRules.BuildUserSwitchContinuation(AgentProvider.Codex, ["執行指令：npm test（未完成）"], backgroundWorkStopped: false, AgentProvider.Claude);
        cross.Should().StartWith("（系統通知：使用者在這一輪執行中把這個對話從 Claude Code 改用 Codex，原本的這一輪在中途停下；Clode Myriagent 已改用 Codex 接續這個對話，停下是為了換代理，不是要放棄原本的工作。上面是先前對話的節錄");
        cross.Should().Contain("〈執行指令：npm test（未完成）〉").And.EndWith("請繼續原本的工作並回報。）");
        AccountSwitchRules.BuildUserSwitchContinuation(AgentProvider.Codex, [], backgroundWorkStopped: false, AgentProvider.Codex)
            .Should().Be(AccountSwitchRules.BuildUserSwitchContinuation(AgentProvider.Codex, [], backgroundWorkStopped: false), "同一種代理照舊");

        AccountSwitchRules.BuildChosenAfterQuotaContinuation(AgentProvider.Claude, AgentProvider.Claude, [], false).Split('\n')[0]
            .Should().StartWith("（系統通知：原本的帳戶訂閱額度用完，這一輪在中途停下；Clode Myriagent 已改用使用者事先選的另一個 Claude Code 帳戶接續這個對話，這不是使用者中斷。");
        AccountSwitchRules.BuildChosenAfterQuotaContinuation(AgentProvider.Claude, AgentProvider.Codex, [], false).Split('\n')[0]
            .Should().StartWith("（系統通知：原本的 Claude Code 帳戶訂閱額度用完，這一輪在中途停下；Clode Myriagent 已依使用者事先的選擇改用 Codex 接續這個對話，這不是使用者中斷。");
    }

    [Fact]
    public void 執行中換帳戶的時機_when只有turnEnd是結束時才換_其他都是立即()
    {
        AccountSwitchRules.IsAtTurnEnd("turnEnd").Should().BeTrue();
        foreach (var other in new[] { null, "", "now", "TurnEnd", "turnend", " turnEnd" })
            AccountSwitchRules.IsAtTurnEnd(other).Should().BeFalse(other ?? "null");
        RemoteCommandCatalog.Find("setAccount")!.Fields.Single(f => f.Name == "when").Values
            .Should().Equal([AccountSwitchRules.WhenNow, AccountSwitchRules.WhenTurnEnd], "型錄（Protocol）的名稱同中樞的常數");
    }

    [Theory]
    // 立即換：停下或失敗就接著做；停止還沒生效就做完了只換帳戶（額度用完與否都一樣）
    [InlineData(false, "Interrupted", false, true)]
    [InlineData(false, "Failed", false, true)]
    [InlineData(false, "Completed", false, false)]
    [InlineData(false, "Completed", true, false)]
    // 這一輪結束時才換：只有原帳戶額度用完而失敗時接著做
    [InlineData(true, "Failed", true, true)]
    [InlineData(true, "Failed", false, false)]
    [InlineData(true, "Interrupted", true, false)]
    [InlineData(true, "Interrupted", false, false)]
    [InlineData(true, "Completed", true, false)]
    [InlineData(true, "Completed", false, false)]
    public void ContinuesAfterUserSwitch_立即換停下就接著做_結束時才換只有額度用完失敗才接著做(bool atTurnEnd, string outcome, bool quota, bool expected)
        => AccountSwitchRules.ContinuesAfterUserSwitch(atTurnEnd, Enum.Parse<AgentTurnOutcome>(outcome), quota).Should().Be(expected);

    [Fact]
    public void 結束時才換_額度用完失敗的提示_寫你選的帳戶()
    {
        AccountSwitchRules.BuildChosenAfterQuotaNotice("Claude 主帳戶", " Claude 副帳戶 ")
            .Should().Be("「Claude 主帳戶」的額度已用完，這一輪在中途停下；改用你選的「Claude 副帳戶」繼續，新帳戶會收到先前對話的節錄，從中斷處接著做。");
        AccountSwitchRules.BuildChosenAfterQuotaNotice(null, "新的").Should().StartWith("原帳戶的額度已用完");
    }

    [Fact]
    public void 執行中手動換帳戶的提示與接續訊息_寫使用者換了帳戶_步驟與背景工作同自動換帳戶()
    {
        AccountSwitchRules.BuildUserSwitchNotice("Claude 主帳戶", "Claude 副帳戶")
            .Should().Be("已從「Claude 主帳戶」改用「Claude 副帳戶」繼續：這一輪在中途停下，新帳戶會收到先前對話的節錄，從中斷處接著做。");
        AccountSwitchRules.BuildUserSwitchNotice(null, " 新的 ").Should().StartWith("已從原帳戶改用「新的」繼續");

        var text = AccountSwitchRules.BuildUserSwitchContinuation(AgentProvider.Codex, ["執行指令：npm test（未完成）"], backgroundWorkStopped: true);
        text.Should().StartWith("（系統通知：使用者在這一輪執行中把帳戶換成另一個 Codex 帳戶，舊帳戶的這一輪在中途停下；");
        text.Should().Contain("停下是為了換帳戶，不是要放棄原本的工作。上面是先前對話的節錄");
        text.Should().Contain("〈執行指令：npm test（未完成）〉").And.Contain("原本還在跑的背景工作已隨舊的程序停止").And.EndWith("請繼續原本的工作並回報。）");
        text.Should().NotContain("額度");

        // 額度用完的那一份照舊（共用後半段）
        AccountSwitchRules.BuildContinuation(AgentProvider.Claude, [], backgroundWorkStopped: false).Split('\n')[0]
            .Should().Be("（系統通知：原本的帳戶訂閱額度用完，這一輪在中途停下；Clode Myriagent 已自動改用另一個 Claude Code 帳戶接續這個對話，這不是使用者中斷。上面是先前對話的節錄，你看不到先前的工具結果與讀過的檔案內容。");
    }
    private static QuotaHomeAccount Home(DateTimeOffset? resetsAt, string? model = null, string? switchedModel = null, string? switchedEffort = null)
        => new("acc-home", resetsAt, Now.AddHours(-2), model, "high", AgentPermissionMode.Ask, null, null, switchedModel, switchedEffort);

    private static UsageSnapshot Usage(DateTimeOffset fetchedAt, params UsageWindow[] windows) => new(fetchedAt, "api", windows, null, null, null, false);

    [Fact]
    public void HomeRecovered_重置時間已知_過了寬限而且用量還有額度才換回()
    {
        var reset = Now.AddMinutes(-5);
        var home = Home(reset);
        // 用完時的快照：5 小時窗鎖定到 reset，重置時間一過就歸零
        var stale = Usage(Now.AddHours(-2), new UsageWindow(UsageRules.SessionKey, "5 小時", 100, reset, Locked: true));

        AccountSwitchRules.HomeRecovered(home, AccountStatus.Ready, stale, reset, Now).Should().BeTrue("重置時間過了 5 分鐘、時間窗已歸零");
        AccountSwitchRules.HomeRecovered(home, AccountStatus.Ready, null, null, Now).Should().BeTrue("重置時間已知：沒有用量資料也照重置時間");
        AccountSwitchRules.HomeRecovered(home, AccountStatus.Ready, stale, reset, reset.AddSeconds(30)).Should().BeFalse("寬限 1 分鐘內不換");
        AccountSwitchRules.HomeRecovered(home, AccountStatus.Ready, stale, reset, reset + AccountSwitchRules.ReturnGrace).Should().BeTrue("寬限到了就換");
        AccountSwitchRules.HomeRecovered(Home(Now.AddHours(1)), AccountStatus.Ready, null, null, Now).Should().BeFalse("還沒重置");
    }

    [Fact]
    public void HomeRecovered_未登入_記憶體標著用完_每週或模型專屬上限還鎖著都不換回()
    {
        var home = Home(Now.AddMinutes(-5), model: "opus");
        AccountSwitchRules.HomeRecovered(home, AccountStatus.NotLoggedIn, null, null, Now).Should().BeFalse();
        AccountSwitchRules.HomeRecovered(home, AccountStatus.ApiKey, null, null, Now).Should().BeFalse("API Key 計費不算");
        AccountSwitchRules.HomeRecovered(home, AccountStatus.Ready, null, Now.AddMinutes(10), Now).Should().BeFalse("又用完了一次、記憶體標到 10 分鐘後");
        AccountSwitchRules.HomeRecovered(home, AccountStatus.Ready, Usage(Now, Weekly(100, 30, locked: true)), null, Now)
            .Should().BeFalse("5 小時恢復了、每週的還鎖著");
        AccountSwitchRules.HomeRecovered(home, AccountStatus.Ready, Usage(Now, new UsageWindow(UsageRules.WeeklyKey + ":Opus", "每週 Opus", 100, Now.AddDays(2), Locked: true)), null, Now)
            .Should().BeFalse("這個模型專屬的每週上限還滿著");
        AccountSwitchRules.HomeRecovered(Home(Now.AddMinutes(-5), model: "sonnet"), AccountStatus.Ready,
                Usage(Now, new UsageWindow(UsageRules.WeeklyKey + ":Opus", "每週 Opus", 100, Now.AddDays(2), Locked: true)), null, Now)
            .Should().BeTrue("別的模型的上限不管這個對話");
        AccountSwitchRules.HomeRecovered(home, AccountStatus.Ready, Usage(Now, Session(100, null, locked: true)), null, Now)
            .Should().BeFalse("鎖定、不知道何時重置：等下一次查用量");
    }

    [Fact]
    public void HomeRecovered_不知道重置時間_要等30分鐘而且用完之後查過用量還有額度()
    {
        var home = Home(null);   // 用完在 2 小時前
        var fresh = Usage(Now.AddMinutes(-1), Session(20, 3));

        AccountSwitchRules.HomeRecovered(home, AccountStatus.Ready, fresh, null, Now).Should().BeTrue();
        AccountSwitchRules.HomeRecovered(home, AccountStatus.Ready, null, null, Now).Should().BeFalse("沒有用量資料：沒有正面的證據");
        AccountSwitchRules.HomeRecovered(home, AccountStatus.Ready, Usage(Now.AddHours(-3), Session(20, 3)), null, Now).Should().BeFalse("用完之前查的不算");
        AccountSwitchRules.HomeRecovered(home, AccountStatus.Ready, Usage(Now.AddMinutes(-1)), null, Now).Should().BeFalse("查了但沒有時間窗");
        var justNow = home with { ExhaustedAt = Now.AddMinutes(-10) };
        AccountSwitchRules.HomeRecovered(justNow, AccountStatus.Ready, fresh, null, Now).Should().BeFalse("用完還不到 30 分鐘");
    }

    [Fact]
    public void RestoresHomeModel_跨代理一律還原_同種代理只在使用者沒自己改過時()
    {
        var home = Home(Now, model: "opus", switchedModel: "sonnet", switchedEffort: "medium");
        AccountSwitchRules.RestoresHomeModel(home, crossProvider: true, "gpt-5.5", "high").Should().BeTrue();
        AccountSwitchRules.RestoresHomeModel(home, crossProvider: false, "sonnet", "medium").Should().BeTrue("還是換過去之後的");
        AccountSwitchRules.RestoresHomeModel(home, crossProvider: false, "haiku", "medium").Should().BeFalse("使用者自己換了模型");
        AccountSwitchRules.RestoresHomeModel(home, crossProvider: false, "sonnet", "low").Should().BeFalse("使用者自己換了強度");
        AccountSwitchRules.RestoresHomeModel(Home(Now), crossProvider: false, null, null).Should().BeTrue("都是帳戶預設");
    }

    [Fact]
    public void 換回原帳戶的提示_斜線指令送出前不換()
    {
        AccountSwitchRules.BuildReturnNotice("主帳戶", "副帳戶")
            .Should().Be("「主帳戶」的額度已恢復，已從「副帳戶」換回「主帳戶」繼續這個對話；接下來的訊息會附上先前對話的節錄，讓代理了解前情。");
        AccountSwitchRules.BuildReturnNotice("主帳戶", null).Should().Contain("已從備援帳戶換回「主帳戶」");

        AccountSwitchRules.ReturnsBeforeSend("繼續做").Should().BeTrue();
        AccountSwitchRules.ReturnsBeforeSend("說明 /clear 的用法").Should().BeTrue();
        AccountSwitchRules.ReturnsBeforeSend("/compact").Should().BeFalse();
        AccountSwitchRules.ReturnsBeforeSend("  /clear").Should().BeFalse();
    }

    [Fact]
    public void 背景工作還在跑時換帳戶_when只有明確的now才算確認了現在換()
    {
        AccountSwitchRules.IsExplicitNow("now").Should().BeTrue();
        AccountSwitchRules.IsExplicitNow(null).Should().BeFalse("瀏覽器版、其他電腦看不到背景工作，沒帶 when：等背景工作結束");
        AccountSwitchRules.IsExplicitNow("turnEnd").Should().BeFalse();
        AccountSwitchRules.IsExplicitNow("NOW").Should().BeFalse();
    }

    [Fact]
    public void 背景工作還在跑時換帳戶的提示()
    {
        AccountSwitchRules.BuildDeferredNotice(1, " 副帳戶 ").Should().Be(
            "還有背景工作在跑：換帳戶會關掉程序、背景工作跟著停，所以等它們都結束、代理處理完結果之後才改用「副帳戶」。要現在就換，在帳戶選單再選一次並選「現在換」；選回目前的帳戶就不換。");
        AccountSwitchRules.BuildDeferredNotice(3, "副帳戶").Should().StartWith("還有 3 個背景工作在跑：");
        AccountSwitchRules.BuildDeferredDroppedNotice("副帳戶").Should().Be("背景工作都結束了，但「副帳戶」已經不能用（移除、登出或沒安裝），沒有換帳戶。");
        AccountSwitchRules.BuildDeferredDroppedNotice(null).Should().Contain("但選的帳戶已經不能用");
        AccountSwitchRules.BuildIdleSwitchStoppedNotice("主帳戶", "副帳戶", 2).Should().Be(
            "已從「主帳戶」改用「副帳戶」繼續：還在跑的 2 個背景工作跟著舊的程序停了，新帳戶會收到先前對話的節錄，確認背景工作做到哪裡再接著做。");
        AccountSwitchRules.BuildIdleSwitchStoppedNotice(null, "副帳戶", 1).Should().StartWith("已從原帳戶改用「副帳戶」繼續：還在跑的背景工作");
    }

    [Fact]
    public void 背景工作還在跑時現在換帳戶的接續訊息_列出被停掉的背景工作_跨代理寫成換了代理()
    {
        var text = AccountSwitchRules.BuildIdleSwitchContinuation(AgentProvider.Claude, AgentProvider.Claude, ["執行指令：dotnet build"], ["執行指令：npm test", "Agent：審查"]);
        text.Should().Be(string.Join("\n",
            "（系統通知：上一輪結束後還有背景工作在跑，使用者選了現在就把帳戶換成另一個 Claude Code 帳戶；換帳戶關掉了舊的程序，背景工作跟著停止。Clode Myriagent 已改用新帳戶接續這個對話。上面是先前對話的節錄，你看不到先前的工具結果與讀過的檔案內容。",
            "使用者最後一則訊息之後已經做過的步驟（由舊到新）：〈執行指令：dotnet build〉。",
            "已完成的步驟不要重做；先確認目前的實際狀態（例如相關檔案、git 狀態），再從中斷處繼續原本的工作。",
            "被停掉的背景工作：〈執行指令：npm test〉、〈Agent：審查〉。",
            "原本還在跑的背景工作已隨舊的程序停止，結果未知；可安全重跑才重跑，不可重複的操作（例如部署、發布、付款）不要直接重跑。",
            "使用者沒有另外傳送新訊息；請繼續原本的工作並回報。）"));
        AccountSwitchRules.BuildIdleSwitchContinuation(AgentProvider.Claude, AgentProvider.Codex, [], [])
            .Should().StartWith("（系統通知：上一輪結束後還有背景工作在跑，使用者選了現在就把這個對話從 Claude Code 改用 Codex；換代理關掉了舊的程序")
            .And.NotContain("被停掉的背景工作：", "對不到工具的只說背景工作停了");
        AccountSwitchRules.BuildContinuation(AgentProvider.Codex, [], backgroundWorkStopped: true).Should().NotContain("被停掉的背景工作：", "原本的接續訊息不變");
    }

    [Fact]
    public void BackgroundSteps_工具名稱與摘要第一行_截短_最多12個()
    {
        AccountSwitchRules.BackgroundSteps([new("執行指令", "bash", "npm test\n--watch", null), new("Agent", null, null, null), new(" ", null, "x", null)])
            .Should().Equal("執行指令：npm test", "Agent", "背景工作：x");
        AccountSwitchRules.BackgroundSteps([new("執行指令", "bash", new string('a', 300), null)]).Single()
            .Should().HaveLength(AccountSwitchRules.MaxStepChars + 1).And.EndWith("…");
        AccountSwitchRules.BackgroundSteps(Enumerable.Range(0, 20).Select(i => new AgentBackgroundTool("執行指令", "bash", $"t{i}", null)).ToList())
            .Should().HaveCount(AccountSwitchRules.MaxRecentSteps);
        AccountSwitchRules.BackgroundSteps(null).Should().BeEmpty();
    }
}
