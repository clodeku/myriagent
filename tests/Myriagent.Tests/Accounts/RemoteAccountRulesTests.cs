using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Remote;

namespace Myriagent.Tests.Accounts;

/// <summary>WP4.8 步 1：遠端登入前置條件、身分比對、登出與 CLI 指令的純規則（RemoteAccountRules）</summary>
public class RemoteAccountRulesTests
{
    /// <summary>全部放行的 Codex（隔離、受管、未登入、CLI 已安裝）；每列以它為基準只改一兩個事實</summary>
    private static RemoteLoginFacts Codex() => new(AgentProvider.Codex, Exists: true, IsSystemDefault: false, ManagedFolder: true,
        AccountStatus.NotLoggedIn, LoginExpired: false, AccountLoginBusy: false, AnyLoginActive: false, CliPaused: false, CliInstalled: true);

    /// <summary>全部放行的 Claude（系統預設也可以：在那台開主控台）</summary>
    private static RemoteLoginFacts Claude() => Codex() with { Provider = AgentProvider.Claude };

    /// <summary>D6 的優先順序表：名稱 → (事實, 登入方式, 期望的代碼（null＝放行）, 訊息要含的字（null＝不檢查）)</summary>
    private static readonly Dictionary<string, (RemoteLoginFacts Facts, LoginMethod Method, string? Code, string? Contains)> LoginRows = new()
    {
        ["Codex_全部放行"] = (Codex(), LoginMethod.DeviceCode, null, null),
        ["Claude_全部放行"] = (Claude(), LoginMethod.Default, null, null),
        ["Claude_系統預設也放行"] = (Claude() with { IsSystemDefault = true, ManagedFolder = false }, LoginMethod.Default, null, null),
        ["新建帳戶的事實_放行"] = (Codex() with { Exists = false }, LoginMethod.DeviceCode, null, null),
        // 設計 2026-10-22-grok-antigravity-sync 的 P3：Grok 比照 Codex（D5），Antigravity 比照 Claude（D4，系統預設也可以）
        ["Grok_全部放行"] = (Codex() with { Provider = AgentProvider.Grok }, LoginMethod.DeviceCode, null, null),
        ["Antigravity_全部放行"] = (Claude() with { Provider = AgentProvider.Antigravity }, LoginMethod.Default, null, null),
        ["Antigravity_系統預設也放行"] = (Claude() with { Provider = AgentProvider.Antigravity, IsSystemDefault = true, ManagedFolder = false }, LoginMethod.Default, null, null),
        ["2_Antigravity帶裝置碼"] = (Claude() with { Provider = AgentProvider.Antigravity }, LoginMethod.DeviceCode, RemoteResultCodes.Invalid, "裝置碼"),
        ["3_Grok沒帶裝置碼"] = (Codex() with { Provider = AgentProvider.Grok }, LoginMethod.Default, RemoteResultCodes.Invalid, "Grok Build"),
        ["4_Grok系統預設"] = (Codex() with { Provider = AgentProvider.Grok, IsSystemDefault = true, ManagedFolder = false }, LoginMethod.DeviceCode, RemoteResultCodes.Policy, "Grok Build"),
        ["2_Claude帶裝置碼"] = (Claude(), LoginMethod.DeviceCode, RemoteResultCodes.Invalid, "裝置碼"),
        ["3_Codex沒帶裝置碼"] = (Codex(), LoginMethod.Default, RemoteResultCodes.Invalid, "裝置碼"),
        ["3在4之前_Codex系統預設沒帶裝置碼"] = (Codex() with { IsSystemDefault = true, ManagedFolder = false }, LoginMethod.Default, RemoteResultCodes.Invalid, null),
        ["4_Codex系統預設"] = (Codex() with { IsSystemDefault = true, ManagedFolder = false }, LoginMethod.DeviceCode, RemoteResultCodes.Policy, "預設的設定資料夾"),
        ["4_Codex系統預設_即使ManagedFolder是true"] = (Codex() with { IsSystemDefault = true }, LoginMethod.DeviceCode, RemoteResultCodes.Policy, "預設的設定資料夾"),
        ["4_Codex不是受管資料夾"] = (Codex() with { ManagedFolder = false }, LoginMethod.DeviceCode, RemoteResultCodes.Policy, "app 管理的資料夾"),
        ["4在5之前_Codex系統預設已登入"] = (Codex() with { IsSystemDefault = true, ManagedFolder = false, Status = AccountStatus.Ready }, LoginMethod.DeviceCode, RemoteResultCodes.Policy, null),
        ["4在6之前_Codex系統預設登入中"] = (Codex() with { IsSystemDefault = true, ManagedFolder = false, AccountLoginBusy = true }, LoginMethod.DeviceCode, RemoteResultCodes.Policy, "預設的設定資料夾"),
        ["5_Ready"] = (Codex() with { Status = AccountStatus.Ready }, LoginMethod.DeviceCode, RemoteResultCodes.Conflict, "已經登入"),
        ["5_ApiKey"] = (Claude() with { Status = AccountStatus.ApiKey }, LoginMethod.Default, RemoteResultCodes.Conflict, "API Key"),
        ["5_Checking"] = (Codex() with { Status = AccountStatus.Checking }, LoginMethod.DeviceCode, RemoteResultCodes.Conflict, "檢查"),
        ["5在6之前_Ready登入中"] = (Codex() with { Status = AccountStatus.Ready, AccountLoginBusy = true }, LoginMethod.DeviceCode, RemoteResultCodes.Conflict, "已經登入"),
        ["5_Claude登入已到期_放行"] = (Claude() with { Status = AccountStatus.Ready, LoginExpired = true }, LoginMethod.Default, null, null),
        ["5_Error_放行"] = (Codex() with { Status = AccountStatus.Error }, LoginMethod.DeviceCode, null, null),
        ["6_這個帳戶正在登入"] = (Codex() with { AccountLoginBusy = true }, LoginMethod.DeviceCode, RemoteResultCodes.Conflict, "正在那台登入"),
        ["6在7之前"] = (Codex() with { AccountLoginBusy = true, AnyLoginActive = true }, LoginMethod.DeviceCode, RemoteResultCodes.Conflict, "正在那台登入"),
        ["7_這台正在登入別的帳戶"] = (Codex() with { AnyLoginActive = true }, LoginMethod.DeviceCode, RemoteResultCodes.Conflict, "另一個帳戶"),
        ["7在8之前"] = (Codex() with { AnyLoginActive = true, CliPaused = true }, LoginMethod.DeviceCode, RemoteResultCodes.Conflict, "另一個帳戶"),
        ["8_CLI正在更新"] = (Codex() with { CliPaused = true }, LoginMethod.DeviceCode, RemoteResultCodes.Conflict, "正在更新"),
        ["8在9之前"] = (Codex() with { CliPaused = true, CliInstalled = false }, LoginMethod.DeviceCode, RemoteResultCodes.Conflict, "正在更新"),
        ["9_沒有安裝CLI"] = (Claude() with { CliInstalled = false }, LoginMethod.Default, RemoteResultCodes.Conflict, "還沒有安裝"),
    };

    public static TheoryData<string> LoginRowNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in LoginRows.Keys)
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(LoginRowNames))]
    public void CheckLogin_優先順序表(string row)
    {
        var (facts, method, code, contains) = LoginRows[row];
        var d = RemoteAccountRules.CheckLogin(facts, method);
        d.Code.Should().Be(code, row);
        if (code is null)
        {
            d.Allowed.Should().BeTrue(row);
            d.Message.Should().BeNull(row);
            return;
        }
        d.Allowed.Should().BeFalse(row);
        RemoteResultCodes.All.Should().Contain(d.Code!, "代碼要是認得的值");
        d.Message.Should().NotBeNullOrWhiteSpace(row);
        d.Message!.Length.Should().BeLessThanOrEqualTo(1000, row);
        d.Message.Should().NotContainEquivalentOf("http", row);
        d.Message.Should().NotContain("@", row);
        if (contains is not null)
            d.Message.Should().Contain(contains, row);
    }

    [Fact]
    public void CheckLogin_Codex系統預設_未登入也是policy()
    {
        // 本機規則 CanUseDeviceCode 允許「本來就未登入的系統預設帳戶」；從網頁一律不行（擁有者指示 3、D4）
        var account = new AgentAccount { Provider = AgentProvider.Codex, ConfigDirectory = null };
        CodexDeviceCodeRules.CanUseDeviceCode(account, AccountStatus.NotLoggedIn).Should().BeTrue("對照：本機規則放行");

        var facts = Codex() with { IsSystemDefault = account.IsSystemDefault, ManagedFolder = false, Status = AccountStatus.NotLoggedIn };
        var d = RemoteAccountRules.CheckLogin(facts, LoginMethod.DeviceCode);
        d.Code.Should().Be(RemoteResultCodes.Policy);
        d.Message.Should().Contain("預設的設定資料夾").And.Contain("隔離帳戶");
    }

    [Fact]
    public void CheckLogin_Codex隔離但不是受管資料夾_policy()
    {
        // 手改過的 accounts.json：ConfigDirectory 有值、但不在 accounts\{id} 底下
        var d = RemoteAccountRules.CheckLogin(Codex() with { IsSystemDefault = false, ManagedFolder = false }, LoginMethod.DeviceCode);
        d.Code.Should().Be(RemoteResultCodes.Policy);
        d.Message.Should().Contain("隔離帳戶").And.NotContain("預設");
    }

    [Fact]
    public void CheckLogin_Ready沒到期_ApiKey_Checking_conflict_Ready已到期_NotLoggedIn_Error_放行()
    {
        RemoteAccountRules.CheckLogin(Claude() with { Status = AccountStatus.Ready }, LoginMethod.Default).Code.Should().Be(RemoteResultCodes.Conflict, "Ready 沒到期");
        RemoteAccountRules.CheckLogin(Claude() with { Status = AccountStatus.ApiKey }, LoginMethod.Default).Code.Should().Be(RemoteResultCodes.Conflict, "ApiKey 不換成訂閱登入");
        RemoteAccountRules.CheckLogin(Codex() with { Status = AccountStatus.ApiKey }, LoginMethod.DeviceCode).Code.Should().Be(RemoteResultCodes.Conflict, "Codex 的 ApiKey 也一樣");
        RemoteAccountRules.CheckLogin(Claude() with { Status = AccountStatus.Checking }, LoginMethod.Default).Code.Should().Be(RemoteResultCodes.Conflict, "Checking");

        RemoteAccountRules.CheckLogin(Claude() with { Status = AccountStatus.Ready, LoginExpired = true }, LoginMethod.Default).Allowed.Should().BeTrue("Claude 登入已到期：網頁的「在那台重新登入」");
        RemoteAccountRules.CheckLogin(Claude() with { Status = AccountStatus.NotLoggedIn }, LoginMethod.Default).Allowed.Should().BeTrue();
        RemoteAccountRules.CheckLogin(Claude() with { Status = AccountStatus.Error }, LoginMethod.Default).Allowed.Should().BeTrue();
        RemoteAccountRules.CheckLogin(Codex() with { Status = AccountStatus.NotLoggedIn }, LoginMethod.DeviceCode).Allowed.Should().BeTrue();
        RemoteAccountRules.CheckLogin(Codex() with { Status = AccountStatus.Error }, LoginMethod.DeviceCode).Allowed.Should().BeTrue();
    }

    [Fact]
    public void CheckLogin_Grok只用裝置碼_Antigravity只開視窗()
    {
        RemoteAccountRules.CheckLogin(Codex() with { Provider = AgentProvider.Grok }, LoginMethod.DeviceCode).Allowed.Should().BeTrue();
        RemoteAccountRules.CheckLogin(Codex() with { Provider = AgentProvider.Grok }, LoginMethod.Default).Code.Should().Be(RemoteResultCodes.Invalid);
        RemoteAccountRules.CheckLogin(Claude() with { Provider = AgentProvider.Antigravity }, LoginMethod.Default).Allowed.Should().BeTrue();
        RemoteAccountRules.CheckLogin(Claude() with { Provider = AgentProvider.Antigravity }, LoginMethod.DeviceCode).Code.Should().Be(RemoteResultCodes.Invalid);
    }

    [Fact]
    public void CheckLogin_未知種類丟例外()
    {
        var unknown = (AgentProvider)99;
        FluentActions.Invoking(() => RemoteAccountRules.CheckLogin(Codex() with { Provider = unknown }, LoginMethod.DeviceCode))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => RemoteAccountRules.CheckLogout(new RemoteLogoutFacts(unknown, AccountStatus.Ready, false, false, true)))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => RemoteAccountRules.CheckCliAction("cliInstall", unknown, new RemoteCliFacts(false, false, false)))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => RemoteAccountRules.StartTimeoutMessage(unknown)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => RemoteAccountRules.CheckLogin(Codex(), (LoginMethod)99)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CheckCodexIdentity_大小寫不同_Match_不同信箱_Mismatch_任一邊是null_Unknown()
    {
        const string key = "codex:lin.dev@example.test";
        RemoteAccountRules.CheckCodexIdentity(key, "lin.dev@example.test").Should().Be(RemoteIdentityVerdict.Match);
        RemoteAccountRules.CheckCodexIdentity(key, "Lin.Dev@Example.TEST").Should().Be(RemoteIdentityVerdict.Match, "鍵一律小寫，和伺服器同一個鍵");
        RemoteAccountRules.CheckCodexIdentity(key, "  lin.dev@example.test ").Should().Be(RemoteIdentityVerdict.Match, "前後空白不算");
        RemoteAccountRules.CheckCodexIdentity(key, "other@example.test").Should().Be(RemoteIdentityVerdict.Mismatch);
        RemoteAccountRules.CheckCodexIdentity("claude:lin.dev@example.test", "lin.dev@example.test").Should().Be(RemoteIdentityVerdict.Mismatch, "前綴不同就是不同的鍵");

        RemoteAccountRules.CheckCodexIdentity(null, "lin.dev@example.test").Should().Be(RemoteIdentityVerdict.Unknown, "目錄沒有鍵");
        RemoteAccountRules.CheckCodexIdentity(" ", "lin.dev@example.test").Should().Be(RemoteIdentityVerdict.Unknown);
        RemoteAccountRules.CheckCodexIdentity(key, null).Should().Be(RemoteIdentityVerdict.Unknown, "account/read 沒有信箱");
        RemoteAccountRules.CheckCodexIdentity(key, "  ").Should().Be(RemoteIdentityVerdict.Unknown);
        RemoteAccountRules.CheckCodexIdentity(null, null).Should().Be(RemoteIdentityVerdict.Unknown, "兩邊都不明不能當成相符");
    }

    [Fact]
    public void CheckClaudeIdentity_鍵有org段時只比信箱段_信箱不分大小寫_鍵沒有信箱段且ExpectedEmail是null_Unknown()
    {
        var withOrg = new RemoteLoginExpectation("claude:org-A:lin.dev@example.test", "lin.dev@example.test", "org-B");
        RemoteAccountRules.CheckClaudeIdentity(withOrg, "lin.dev@example.test").Should().Be(RemoteIdentityVerdict.Match, "org 段和 OrgId 都不拿來判斷");
        RemoteAccountRules.CheckClaudeIdentity(withOrg, "LIN.DEV@EXAMPLE.TEST").Should().Be(RemoteIdentityVerdict.Match, "不分大小寫");
        RemoteAccountRules.CheckClaudeIdentity(withOrg, "other@example.test").Should().Be(RemoteIdentityVerdict.Mismatch);

        var keyWins = new RemoteLoginExpectation("claude:org-A:lin.dev@example.test", "someone.else@example.test", null);
        RemoteAccountRules.ClaudeExpectedEmail(keyWins).Should().Be("lin.dev@example.test", "鍵有信箱段就用它");
        RemoteAccountRules.CheckClaudeIdentity(keyWins, "lin.dev@example.test").Should().Be(RemoteIdentityVerdict.Match);

        var noOrg = new RemoteLoginExpectation("claude:lin.dev@example.test", null, null);
        RemoteAccountRules.CheckClaudeIdentity(noOrg, "Lin.Dev@example.test").Should().Be(RemoteIdentityVerdict.Match);

        var otherPrefix = new RemoteLoginExpectation("codex:lin.dev@example.test", "expected@example.test", null);
        RemoteAccountRules.ClaudeExpectedEmail(otherPrefix).Should().Be("expected@example.test", "鍵不是 claude: 開頭才用 Email");

        var emptySegment = new RemoteLoginExpectation("claude:org-A:", null, null);
        RemoteAccountRules.ClaudeExpectedEmail(emptySegment).Should().BeNull();
        RemoteAccountRules.CheckClaudeIdentity(emptySegment, "lin.dev@example.test").Should().Be(RemoteIdentityVerdict.Unknown, "鍵沒有信箱段而且 Email 是 null");
        RemoteAccountRules.CheckClaudeIdentity(RemoteLoginExpectation.None, "lin.dev@example.test").Should().Be(RemoteIdentityVerdict.Unknown, "目錄不明");
        RemoteAccountRules.CheckClaudeIdentity(new RemoteLoginExpectation("claude:org-A:", "expected@example.test", null), "expected@example.test")
            .Should().Be(RemoteIdentityVerdict.Match, "鍵沒有信箱段時改用 Email");
        RemoteAccountRules.CheckClaudeIdentity(withOrg, null).Should().Be(RemoteIdentityVerdict.Unknown, "讀不到設定檔");
    }

    [Fact]
    public void CheckLogout_表()
    {
        RemoteLogoutFacts F(AgentProvider p = AgentProvider.Codex, AccountStatus s = AccountStatus.Ready, bool paused = false, bool busy = false, bool installed = true) =>
            new(p, s, paused, busy, installed);

        RemoteAccountRules.CheckLogout(F()).Allowed.Should().BeTrue();
        RemoteAccountRules.CheckLogout(F(AgentProvider.Claude)).Allowed.Should().BeTrue();
        RemoteAccountRules.CheckLogout(F(paused: true)).Code.Should().Be(RemoteResultCodes.Busy, "更新中：什麼都沒執行，稍後可以重送");
        RemoteAccountRules.CheckLogout(F(busy: true)).Code.Should().Be(RemoteResultCodes.Busy, "登入中");
        RemoteAccountRules.CheckLogout(F(installed: false)).Code.Should().Be(RemoteResultCodes.Conflict, "沒安裝");
        RemoteAccountRules.CheckLogout(F(paused: true, installed: false)).Code.Should().Be(RemoteResultCodes.Busy, "更新中先於沒安裝");
        var agy = RemoteAccountRules.CheckLogout(F(AgentProvider.Antigravity));
        agy.Code.Should().Be(RemoteResultCodes.Policy, "agy 沒有登出子指令（D4）");
        agy.Message.Should().Be(RemoteAccountRules.AntigravityLogoutMessage);
        RemoteAccountRules.CheckLogout(F(AgentProvider.Antigravity, paused: true)).Code.Should().Be(RemoteResultCodes.Policy, "沒有登出指令最先");
        RemoteAccountRules.CheckLogout(F(AgentProvider.Grok)).Allowed.Should().BeTrue("Grok 有 grok logout");
        RemoteAccountRules.CheckLogout(F(AgentProvider.Grok, paused: true)).Code.Should().Be(RemoteResultCodes.Busy);

        foreach (var d in new[] { RemoteAccountRules.CheckLogout(F(paused: true)), RemoteAccountRules.CheckLogout(F(busy: true)), RemoteAccountRules.CheckLogout(F(installed: false)) })
            d.Message.Should().NotBeNullOrWhiteSpace();

        RemoteAccountRules.IsAlreadyLoggedOut(F(s: AccountStatus.NotLoggedIn)).Should().BeTrue("未登入就直接 done、不跑 CLI");
        RemoteAccountRules.CheckLogout(F(s: AccountStatus.NotLoggedIn)).Allowed.Should().BeTrue();
        foreach (var s in new[] { AccountStatus.Ready, AccountStatus.ApiKey, AccountStatus.Error, AccountStatus.Checking, AccountStatus.NotInstalled })
            RemoteAccountRules.IsAlreadyLoggedOut(F(s: s)).Should().BeFalse(s.ToString());
    }

    [Fact]
    public void CheckCliAction_表()
    {
        RemoteCliFacts F(bool busy = false, bool installed = true, bool broken = false) => new(busy, installed, broken);

        // P3 起四種都能從遠端安裝與更新（會在那台開安裝視窗）
        RemoteAccountRules.CheckCliAction("cliInstall", AgentProvider.Grok, F(installed: false)).Allowed.Should().BeTrue();
        RemoteAccountRules.CheckCliAction("cliUpdate", AgentProvider.Antigravity, F()).Allowed.Should().BeTrue();
        RemoteAccountRules.CheckCliAction("cliUpdate", AgentProvider.Grok, F(busy: true)).Code.Should().Be(RemoteResultCodes.Conflict, "Busy");

        RemoteAccountRules.CheckCliAction("cliUpdate", AgentProvider.Claude, F(busy: true)).Code.Should().Be(RemoteResultCodes.Conflict, "Busy");
        RemoteAccountRules.CheckCliAction("cliInstall", AgentProvider.Codex, F(busy: true, installed: false)).Code.Should().Be(RemoteResultCodes.Conflict, "Busy");

        RemoteAccountRules.CheckCliAction("cliUpdate", AgentProvider.Codex, F(installed: false)).Code.Should().Be(RemoteResultCodes.Conflict, "沒安裝不能更新（免得落進安裝）");
        RemoteAccountRules.CheckCliAction("cliUpdate", AgentProvider.Claude, F()).Allowed.Should().BeTrue();
        RemoteAccountRules.CheckCliAction("cliUpdate", AgentProvider.Claude, F(broken: true)).Allowed.Should().BeTrue("損壞也能更新");

        RemoteAccountRules.CheckCliAction("cliInstall", AgentProvider.Claude, F()).Code.Should().Be(RemoteResultCodes.Conflict, "已安裝沒損壞不重裝");
        RemoteAccountRules.CheckCliAction("cliInstall", AgentProvider.Claude, F(broken: true)).Allowed.Should().BeTrue("損壞可以重新安裝");
        RemoteAccountRules.CheckCliAction("cliInstall", AgentProvider.Codex, F(installed: false)).Allowed.Should().BeTrue();

        foreach (var d in new[]
                 {
                     RemoteAccountRules.CheckCliAction("cliInstall", AgentProvider.Grok, F()),
                     RemoteAccountRules.CheckCliAction("cliUpdate", AgentProvider.Claude, F(busy: true)),
                     RemoteAccountRules.CheckCliAction("cliUpdate", AgentProvider.Codex, F(installed: false)),
                     RemoteAccountRules.CheckCliAction("cliInstall", AgentProvider.Claude, F()),
                 })
            d.Message.Should().NotBeNullOrWhiteSpace();

        FluentActions.Invoking(() => RemoteAccountRules.CheckCliAction("cliCheck", AgentProvider.Claude, F())).Should().Throw<ArgumentOutOfRangeException>("cmd 不認得");
        FluentActions.Invoking(() => RemoteAccountRules.CheckCliAction("", AgentProvider.Claude, F())).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void StatusTextWithNote_有note用note()
    {
        var note = RemoteAccountRules.MismatchNote(loggedOut: true);
        RemoteAccountRules.StatusTextWithNote(note, "尚未登入 ChatGPT 帳戶").Should().Be(note);
        RemoteAccountRules.StatusTextWithNote(null, "尚未登入 ChatGPT 帳戶").Should().Be("尚未登入 ChatGPT 帳戶");
        RemoteAccountRules.StatusTextWithNote("  ", "已登入").Should().Be("已登入", "空白的說明不算");
    }

    [Fact]
    public void 期限_比閘門的30秒短至少5秒()
    {
        var gate = TimeSpan.FromSeconds(30);
        RemoteAccountRules.StartDeadline.Should().BePositive().And.BeLessThanOrEqualTo(gate - TimeSpan.FromSeconds(5));
        RemoteAccountRules.LogoutDeadline.Should().BePositive().And.BeLessThanOrEqualTo(gate - TimeSpan.FromSeconds(5));
        RemoteAccountRules.IdentityHoldRetry.Should().BePositive();
        RemoteAccountRules.StartTimeoutMessage(AgentProvider.Codex).Should().Contain(((int)RemoteAccountRules.StartDeadline.TotalSeconds).ToString(), "訊息的秒數跟著期限");
    }

    [Fact]
    public void 所有訊息_不含信箱樣式與網址()
    {
        var texts = new List<string>
        {
            RemoteAccountRules.ConsoleOpenedMessage,
            RemoteAccountRules.LogoutFailedMessage,
            RemoteAccountRules.LoginNotStartedMessage,
            RemoteAccountRules.LoginCanceledMessage,
            RemoteAccountRules.TargetKindMismatchMessage,
            RemoteAccountRules.MismatchNote(true),
            RemoteAccountRules.MismatchNote(false),
            RemoteAccountRules.MismatchNotifyText("工作用 Codex", true),
            RemoteAccountRules.MismatchNotifyText("工作用 Codex", false),
        };
        foreach (var p in AgentProviders.All)
            texts.Add(RemoteAccountRules.StartTimeoutMessage(p));
        foreach (var (facts, method, _, _) in LoginRows.Values)
        {
            if (RemoteAccountRules.CheckLogin(facts, method).Message is { } m)
                texts.Add(m);
        }
        foreach (var p in AgentProviders.All)
        {
            foreach (var paused in new[] { false, true })
            {
                foreach (var busy in new[] { false, true })
                {
                    if (RemoteAccountRules.CheckLogout(new RemoteLogoutFacts(p, AccountStatus.Ready, paused, busy, CliInstalled: !busy)).Message is { } m)
                        texts.Add(m);
                }
            }
            foreach (var cmd in new[] { "cliInstall", "cliUpdate" })
            {
                foreach (var facts in new[] { new RemoteCliFacts(true, true, false), new RemoteCliFacts(false, false, false), new RemoteCliFacts(false, true, false) })
                {
                    if (RemoteAccountRules.CheckCliAction(cmd, p, facts).Message is { } m)
                        texts.Add(m);
                }
            }
        }

        texts.Should().HaveCountGreaterThan(20);
        foreach (var t in texts)
        {
            t.Should().NotBeNullOrWhiteSpace();
            t.Length.Should().BeLessThanOrEqualTo(1000, t);
            t.Should().NotContain("@", t);
            t.Should().NotContainEquivalentOf("http", t);
        }
        RemoteAccountRules.MismatchNote(true).Should().NotBe(RemoteAccountRules.MismatchNote(false));
        RemoteAccountRules.MismatchNotifyText("工作用 Codex", true).Should().Contain("工作用 Codex");
    }
}
