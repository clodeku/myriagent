using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Json;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Accounts;

/// <summary>
/// Codex 裝置碼登入的流程（WP3.4 第 11 步）：中樞收 accountLogin {method:"deviceCode"}、帳戶管理員起假的 app-server（FakeCodexAppServer，
/// 照 G5 實測的回應形狀），驗證碼顯示在這台的 accountLogin 訊息、取消或逾時就撤回；驗證碼不進記錄檔、AccountView 與同步 payload（R20）。
/// 一律以假 app-server 為 ResolveLauncher（D18），開瀏覽器換成計數器：不跑真的 codex、不開瀏覽器、不連網
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class CodexDeviceCodeLoginTests
{
    private const int Slow = 40_000;

    private sealed class Rig : IDisposable
    {
        private int opened;

        public Rig(string scenario, TimeSpan? timeout = null, bool alwaysPost = false)
        {
            H = new HubHarness(alwaysPost: alwaysPost);
            Fake = new FakeCodexAppServer(scenario);
            H.Ui.Invoke(() =>
            {
                // Claude 也指到假的 app-server：萬一落進主控台登入，calls.txt 會有 args=auth login…
                H.Accounts.ResolveLauncher = p => p switch
                {
                    AgentProvider.Codex or AgentProvider.Claude => Fake.Launcher,
                    _ => null,
                };
                H.Accounts.OpenBrowser = _ => Interlocked.Increment(ref opened);
                H.Accounts.DeviceCodeTimeout = timeout ?? TimeSpan.FromSeconds(10);
            });
        }

        public HubHarness H { get; }

        public FakeCodexAppServer Fake { get; }

        public int Opened => Volatile.Read(ref opened);

        public string Codex => H.Codex.Id;

        public void Login(string accountId, object? method = null)
            => H.Command(H.Main, "accountLogin", method is null ? new { accountId } : new { accountId, method });

        public IReadOnlyList<JObject> LoginMessages(string accountId)
            => H.Main.Messages("accountLogin").Where(m => m.Value<string>("accountId") == accountId).ToList();

        public Task<JObject> ShownAsync() => H.Main.WaitForAsync("accountLogin", m => m.Value<string>("accountId") == Codex && m["deviceCode"] is JObject, timeoutMs: Slow);

        public Task<JObject> DoneAsync(string accountId) => H.Main.WaitForAsync("accountLogin", m => m.Value<string>("accountId") == accountId && m.Value<bool>("done"), timeoutMs: Slow);

        public AccountRuntimeState State(string id) => H.OnUi(() => H.Accounts.GetState(id));

        /// <summary>記錄檔（AppPaths.LogDirectory 底下的每個檔）的全文</summary>
        public string LogText()
        {
            Log.Flush();
            var dir = AppPaths.LogDirectory;
            return Directory.Exists(dir)
                ? string.Join("\n", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => File.ReadAllText(f)))
                : string.Empty;
        }

        public void Dispose()
        {
            try
            {
                // 保險：還在進行的登入一律取消，等 app-server 關掉
                H.Ui.Invoke(() =>
                {
                    foreach (var account in H.Accounts.Accounts.ToList())
                        H.Accounts.CancelLogin(account.Id);
                });
                H.Ui.DrainAsync(5).Wait(TimeSpan.FromSeconds(15));
            }
            finally
            {
                H.Dispose();
                Fake.Dispose();
            }
        }
    }

    private static bool HasCode(JToken token)
    {
        var text = token.ToString(Formatting.None);
        return text.Contains(FakeCodexAppServer.UserCode, StringComparison.OrdinalIgnoreCase) || text.Contains(FakeCodexAppServer.LoginId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 合格_accountLogin訊息帶deviceCode_message含驗證碼_0次開瀏覽器()
    {
        using var rig = new Rig("ok");
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        rig.Login(rig.Codex, "deviceCode");
        var shown = await rig.ShownAsync();

        var code = (JObject)shown["deviceCode"]!;
        code.Value<string>("userCode").Should().Be(FakeCodexAppServer.UserCode);
        code.Value<string>("verificationUrl").Should().Be(FakeCodexAppServer.VerificationUrl);
        code.Value<long>("expiresAt").Should().BeInRange(before + 15 * 60_000 - 1000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 15 * 60_000, "沒有到期欄位時是 15 分鐘");
        shown.Value<bool>("done").Should().BeFalse();
        shown.Value<string>("message").Should().Be($"請在手機或其他電腦打開 {FakeCodexAppServer.VerificationUrl}，輸入驗證碼 {FakeCodexAppServer.UserCode}（15 分鐘內有效）");
        shown.ToString().Should().NotContain(FakeCodexAppServer.LoginId, "loginId 不給網頁");
        rig.Fake.Calls.Should().Contain("account/login/start type=chatgptDeviceCode").And.Contain(c => c.StartsWith("args=app-server"));
        rig.State(rig.Codex).DeviceCode.Should().NotBeNull();
        rig.Opened.Should().Be(0, "裝置碼登入不開瀏覽器（使用者可能在別台裝置核准）");

        // 帳戶卡（每 500 ms 合併一次）：登入中、不能再選裝置碼登入、沒有驗證碼
        await Task.Delay(700);
        var card = rig.H.Main.Last("accounts")!["accounts"]!.Children<JObject>().Single(a => a.Value<string>("id") == rig.Codex);
        card.Value<bool>("loginBusy").Should().BeTrue();
        card.Value<bool>("deviceCodeLogin").Should().BeFalse("登入中不再提供");
        foreach (var accounts in rig.H.Main.Messages("accounts"))
            HasCode(accounts).Should().BeFalse("AccountView 不帶驗證碼");

        rig.H.Ui.Invoke(() => rig.H.Accounts.CancelLogin(rig.Codex));
        await rig.DoneAsync(rig.Codex);
    }

    [Fact]
    public async Task 完成_再查一次狀態_done_success()
    {
        using var rig = new Rig("ok");
        rig.Login(rig.Codex, "deviceCode");
        await rig.ShownAsync();
        rig.Fake.Complete();

        var done = await rig.DoneAsync(rig.Codex);
        done.Value<bool?>("success").Should().BeTrue();
        done.Value<string>("message").Should().Be("「Codex 帳戶」已登入");
        done["deviceCode"].Should().BeNull("done 的訊息不帶驗證碼");
        rig.State(rig.Codex).DeviceCode.Should().BeNull("結束就清掉");
        rig.State(rig.Codex).Status.Should().Be(AccountStatus.Ready);
        rig.H.OnUi(() => rig.H.Codex.Email).Should().Be(FakeCodexAppServer.Email, "登入後的 account/read 更新 email");
        rig.Fake.Calls.Should().Contain("account/read", "完成後再查一次狀態");
        rig.Fake.Calls.Should().NotContain(c => c.StartsWith("account/login/cancel"), "完成了不必取消");
        rig.Opened.Should().Be(0);

        // 結束之後帳戶卡又可以選裝置碼登入（隔離帳戶）
        var card = (await rig.H.Main.WaitForAsync("accounts", m => m["accounts"]!.Children<JObject>()
            .Any(a => a.Value<string>("id") == rig.Codex && !a.Value<bool>("loginBusy")), timeoutMs: 3000))["accounts"]!
            .Children<JObject>().Single(a => a.Value<string>("id") == rig.Codex);
        card.Value<bool>("deviceCodeLogin").Should().BeTrue();
    }

    [Fact]
    public async Task 使用者取消_送cancel帶loginId()
    {
        using var rig = new Rig("ok");
        rig.Login(rig.Codex, "deviceCode");
        await rig.ShownAsync();
        rig.H.Command(rig.H.Main, "accountLoginCancel", new { accountId = rig.Codex });

        var done = await rig.DoneAsync(rig.Codex);
        done.Value<string>("message").Should().Be("已取消登入");
        done.Value<bool?>("success").Should().BeFalse();
        rig.Fake.Calls.Should().Contain("account/login/cancel loginId=" + FakeCodexAppServer.LoginId);
        rig.State(rig.Codex).DeviceCode.Should().BeNull();
        rig.Opened.Should().Be(0);
    }

    [Fact]
    public async Task 逾時_送cancel()
    {
        using var rig = new Rig("ok", timeout: TimeSpan.FromMilliseconds(300));
        rig.Login(rig.Codex, "deviceCode");
        await rig.ShownAsync();

        var done = await rig.DoneAsync(rig.Codex);
        done.Value<bool?>("success").Should().BeFalse();
        rig.Fake.Calls.Should().Contain("account/login/cancel loginId=" + FakeCodexAppServer.LoginId);
        var expired = rig.LoginMessages(rig.Codex).Single(m => m.Value<string>("message") == "驗證碼已過期，登入沒有完成");
        expired["deviceCode"].Should().BeNull("過期之後不再帶驗證碼");
        rig.State(rig.Codex).DeviceCode.Should().BeNull();
    }

    [Theory]
    [InlineData("evil_host", true)]
    [InlineData("http", true)]
    [InlineData("missing_fields", true)]
    public async Task 主機不符或欄位不合格_送cancel_0次顯示驗證碼_0次開瀏覽器(string scenario, bool hasLoginId)
    {
        using var rig = new Rig(scenario);
        rig.Login(rig.Codex, "deviceCode");
        var done = await rig.DoneAsync(rig.Codex);

        done.Value<bool?>("success").Should().BeFalse();
        rig.LoginMessages(rig.Codex).Should().Contain(m => m.Value<string>("message") == "Codex 回傳的登入資訊不合格，已取消");
        rig.LoginMessages(rig.Codex).Should().OnlyContain(m => m["deviceCode"] == null && !HasCode(m), "不合格的回應不顯示驗證碼");
        if (hasLoginId)
            rig.Fake.Calls.Should().Contain("account/login/cancel loginId=" + FakeCodexAppServer.LoginId);
        rig.Fake.Calls.Should().NotContain(c => c.StartsWith("args=login"), "不退回主控台登入");
        rig.Opened.Should().Be(0);
        var log = rig.LogText();
        log.Should().Contain("Codex 裝置碼登入的回應不合格，已取消（欄位：loginId、type");
        log.Should().NotContain(FakeCodexAppServer.UserCode).And.NotContain(FakeCodexAppServer.LoginId).And.NotContain("evil", "記錄檔只記欄位名稱");
    }

    [Fact]
    public async Task 不支援_結束並說明_不開主控台()
    {
        using var rig = new Rig("unsupported");
        rig.Login(rig.Codex, "deviceCode");
        var done = await rig.DoneAsync(rig.Codex);

        done.Value<bool?>("success").Should().BeFalse();
        rig.LoginMessages(rig.Codex).Should().Contain(m => m.Value<string>("message") == "這個版本的 Codex 不支援裝置碼登入，請更新 Codex 或改用一般登入");
        rig.Fake.Calls.Should().Contain("account/login/start type=chatgptDeviceCode");
        rig.Fake.Calls.Should().NotContain(c => c.StartsWith("args=login"), "不退回主控台登入");
        rig.Fake.Calls.Should().NotContain(c => c.StartsWith("account/login/cancel"), "沒有 loginId 可以取消");
        rig.Opened.Should().Be(0);
    }

    [Fact]
    public async Task 系統預設已登入_不起app_server()
    {
        using var rig = new Rig("ok");
        rig.H.Ui.Invoke(() => rig.H.Codex.ConfigDirectory = null);   // 改成系統預設帳戶（~/.codex；狀態是 Ready）
        rig.Login(rig.Codex, "deviceCode");

        var done = await rig.DoneAsync(rig.Codex);
        done.Value<bool?>("success").Should().BeFalse();
        done.Value<string>("message").Should().Contain("預設的 Codex 帳戶").And.Contain("新增");
        rig.Fake.Started.Should().BeFalse("calls.txt 不存在：沒起 app-server");
        rig.H.OnUi(() => rig.H.Accounts.LastLoginRequest).Should().Be((LoginMethod.DeviceCode, false));
        rig.State(rig.Codex).LoginBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Claude帶method_deviceCode_明確拒絕_不開主控台()
    {
        using var rig = new Rig("ok");
        rig.Login(rig.H.Claude2.Id, "deviceCode");

        var done = await rig.DoneAsync(rig.H.Claude2.Id);
        done.Value<bool?>("success").Should().BeFalse();
        done.Value<string>("message").Should().Be("只有 Codex、Grok 帳戶能用裝置碼登入");
        await Task.Delay(500);
        rig.Fake.Started.Should().BeFalse("不落進主控台登入（假 CLI 一次都沒跑）");
        rig.LoginMessages(rig.H.Claude2.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task 記錄檔與AccountView不含驗證碼()
    {
        // complete_fail：Codex 的錯誤文字含驗證碼——寫進記錄檔前要遮掉
        using var rig = new Rig("complete_fail");
        rig.Login(rig.Codex, "deviceCode");
        var done = await rig.DoneAsync(rig.Codex);
        await Task.Delay(700);   // 帳戶卡合併送出

        done.Value<bool?>("success").Should().BeFalse();
        rig.LoginMessages(rig.Codex).Should().Contain(m => m.Value<string>("message") == "登入沒有完成：請先在 ChatGPT 的安全性設定開啟裝置碼登入，或改用一般登入");
        rig.LoginMessages(rig.Codex).Should().Contain(m => m["deviceCode"] is JObject, "正向對照：驗證碼確實出現過（只在這台的 accountLogin）");

        var log = rig.LogText();
        log.Should().Contain("Codex 裝置碼登入沒有完成：device code [已遮蔽] rejected", "正向對照：Codex 的錯誤有寫進記錄檔");
        log.Should().NotContain(FakeCodexAppServer.UserCode).And.NotContain(FakeCodexAppServer.LoginId);
        rig.H.Main.Messages("accounts").Should().NotBeEmpty();
        foreach (var accounts in rig.H.Main.Messages("accounts"))
            HasCode(accounts).Should().BeFalse("AccountView 不帶驗證碼");
        foreach (var toast in rig.H.Main.Messages("toast"))
            HasCode(toast).Should().BeFalse();
    }

    [Fact]
    public async Task SyncMirrorView與link_upsert不含驗證碼()
    {
        using var rig = new Rig("ok", alwaysPost: true);
        var engine = new RecordingSyncEngine();
        var bridge = new HubBridge(engine);
        var mirror = rig.H.OnUi(() => new SyncMirrorView(rig.H.Accounts, bridge));
        rig.H.Ui.Invoke(() => rig.H.Hub.AttachSyncView(mirror));
        rig.Login(rig.Codex, "deviceCode");
        await rig.ShownAsync();

        // 驗證碼顯示期間重畫帳戶卡：鏡像收到的 accounts 照樣不含驗證碼
        rig.H.Ui.Invoke(() => rig.H.Hub.RefreshSyncDecorations());
        await rig.H.Ui.DrainAsync();
        await Task.Delay(700);
        rig.State(rig.Codex).DeviceCode.Should().NotBeNull("檢查的當下驗證碼還在顯示");

        var snapshots = engine.Of<SyncSignal.Accounts>().SelectMany(s => s.Snapshot).ToList();
        snapshots.Should().Contain(s => s.Id == rig.Codex && s.LoginBusy, "正向對照：鏡像收到了登入中的 Codex 帳戶");
        foreach (var snapshot in snapshots)
        {
            JsonConvert.SerializeObject(snapshot, ProtocolJson.Settings).Should().NotContain(FakeCodexAppServer.UserCode).And.NotContain(FakeCodexAppServer.LoginId);
            if (CatalogSyncRules.BuildLinkUpsert(snapshot, "la_0123456789ABCDEFGHJKMNPQRS", null) is { } payload)
                JsonConvert.SerializeObject(payload, ProtocolJson.Settings).Should().NotContain(FakeCodexAppServer.UserCode).And.NotContain(FakeCodexAppServer.LoginId);
        }
        foreach (var signal in engine.Posted)
            JsonConvert.SerializeObject(signal, ProtocolJson.Settings).Should().NotContain(FakeCodexAppServer.UserCode);

        rig.H.Ui.Invoke(() => rig.H.Accounts.CancelLogin(rig.Codex));
        await rig.DoneAsync(rig.Codex);
    }

    private sealed class FixedPolicy(bool answer) : IUsagePollPolicy
    {
        public bool ShouldPoll(AgentAccount account) => answer;
    }

    /// <summary>P33 B73（以前只有規則測試）：Codex 的用量查詢經過閘門——擋下時只送 account/read，放行時送一次 account/rateLimits/read</summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Codex用量閘門_擋下時不送rateLimits_read_放行時送一次(bool allow, int expected)
    {
        using var rig = new Rig("ok");
        rig.Fake.Complete();   // account/read 回已登入
        rig.H.Ui.Invoke(() => rig.H.Accounts.UsagePollPolicy = new FixedPolicy(allow));
        await rig.H.OnUi(() => rig.H.Accounts.RefreshAsync(rig.Codex, includeStatus: true));

        rig.Fake.Count("account/read").Should().Be(1);
        rig.Fake.Count("account/rateLimits/read").Should().Be(expected);
        rig.State(rig.Codex).Status.Should().Be(AccountStatus.Ready);
    }
}
