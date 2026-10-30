using Myriagent.Accounts;
using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Accounts;

/// <summary>Codex 裝置碼登入（WP3.4 第 10 步）的規則：網址主機、驗證碼、期限、可登入的資料夾、進度文字</summary>
public class CodexDeviceCodeRulesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_792_000_000_000);
    private static readonly long NowMs = Now.ToUnixTimeMilliseconds();
    private static readonly long Max = NowMs + 15 * 60_000;

    private const string LoginId = "6f1c2a9e-0b7d-4c55-9a1e-3f2b8c7d6e5a";
    private const string Url = "https://auth.openai.com/codex/device";

    /// <summary>G5 實測（codex 0.163.0）的回應形狀：type、loginId（UUID）、userCode（10 字，A-Z 0-9 '-'）、verificationUrl；沒有到期欄位</summary>
    private static JObject Measured() => new()
    {
        ["type"] = "chatgptDeviceCode",
        ["loginId"] = LoginId,
        ["userCode"] = "ABCD-EFGH2",
        ["verificationUrl"] = Url,
    };

    private static JObject With(params (string Key, JToken? Value)[] changes)
    {
        var o = Measured();
        foreach (var (key, value) in changes)
        {
            if (value is null)
                o.Remove(key);
            else
                o[key] = value;
        }
        return o;
    }

    [Fact]
    public void StartParams_是chatgptDeviceCode_每次是新的物件()
    {
        var p = CodexDeviceCodeRules.StartParams();
        p.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"type":"chatgptDeviceCode"}""");
        p["type"] = "chatgpt";
        CodexDeviceCodeRules.StartParams()["type"]!.Value<string>().Should().Be("chatgptDeviceCode", "改了上一個不影響下一個");
    }

    [Fact]
    public void IsAllowedVerificationUrl_只收auth_openai_com的https()
    {
        CodexDeviceCodeRules.IsAllowedVerificationUrl(Url).Should().BeTrue();
        CodexDeviceCodeRules.IsAllowedVerificationUrl("HTTPS://AUTH.OPENAI.COM/codex/device").Should().BeTrue("主機不分大小寫");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("https://auth.openai.com:443/codex/device").Should().BeTrue("寫出預設埠也可以");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("https://auth.openai.com/codex/device?user_code=ABCD-EFGH").Should().BeTrue("RFC 8628 的 verification_uri_complete");

        CodexDeviceCodeRules.IsAllowedVerificationUrl("http://auth.openai.com/codex/device").Should().BeFalse("http");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("http://auth.openai.com").Should().BeFalse();
        CodexDeviceCodeRules.IsAllowedVerificationUrl("https://auth.openai.com.evil.x/codex/device").Should().BeFalse("別的主機");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("https://evil.x/?auth.openai.com").Should().BeFalse("主機名稱只在查詢字串");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("https://xauth.openai.com/codex/device").Should().BeFalse();
        CodexDeviceCodeRules.IsAllowedVerificationUrl("https://openai.com/codex/device").Should().BeFalse("上層網域不算");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("https://auth.openai.com./codex/device").Should().BeFalse("結尾的點是另一個主機名稱");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("https://user@auth.openai.com/codex/device").Should().BeFalse("帶帳號");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("https://user:pw@auth.openai.com/codex/device").Should().BeFalse("帶帳號密碼");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("https://auth.openai.com:8443/codex/device").Should().BeFalse("非預設埠");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("/codex/device").Should().BeFalse("相對網址");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("auth.openai.com/codex/device").Should().BeFalse("沒有協定");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("javascript:alert(1)").Should().BeFalse();
        CodexDeviceCodeRules.IsAllowedVerificationUrl("https://auth.openai.com/codex/device x").Should().BeFalse("含空白");
        CodexDeviceCodeRules.IsAllowedVerificationUrl(Url + (char)10).Should().BeFalse("含換行");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("https://auth.openai.com/" + new string('a', 2100)).Should().BeFalse("太長");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("https://auth.openai.com/" + (char)0x00E9).Should().BeFalse("非 ASCII");
        CodexDeviceCodeRules.IsAllowedVerificationUrl("").Should().BeFalse();
        CodexDeviceCodeRules.IsAllowedVerificationUrl(null).Should().BeFalse();
    }

    [Fact]
    public void IsValidUserCode_4到32字_只有大寫英數與連字號()
    {
        CodexDeviceCodeRules.IsValidUserCode("ABCD-EFGH").Should().BeTrue();
        CodexDeviceCodeRules.IsValidUserCode("ABCD-EFGH2").Should().BeTrue("G5 實測是 10 字");
        CodexDeviceCodeRules.IsValidUserCode("AB12").Should().BeTrue("最短 4 字");
        CodexDeviceCodeRules.IsValidUserCode(new string('A', 32)).Should().BeTrue("最長 32 字");

        CodexDeviceCodeRules.IsValidUserCode("ABC").Should().BeFalse("太短");
        CodexDeviceCodeRules.IsValidUserCode(new string('A', 33)).Should().BeFalse("太長");
        CodexDeviceCodeRules.IsValidUserCode("").Should().BeFalse();
        CodexDeviceCodeRules.IsValidUserCode(null).Should().BeFalse();
        CodexDeviceCodeRules.IsValidUserCode("abcd-efgh").Should().BeFalse("小寫");
        CodexDeviceCodeRules.IsValidUserCode("ABCD EFGH").Should().BeFalse("空白");
        CodexDeviceCodeRules.IsValidUserCode("ABCD" + (char)9 + "EFGH").Should().BeFalse("tab");
        CodexDeviceCodeRules.IsValidUserCode("ABCD" + (char)0).Should().BeFalse("控制字元");
        CodexDeviceCodeRules.IsValidUserCode("ABCD" + (char)10).Should().BeFalse("換行");
        CodexDeviceCodeRules.IsValidUserCode((char)0x00C4 + "BCD").Should().BeFalse("非 ASCII 的大寫");
        CodexDeviceCodeRules.IsValidUserCode(new string((char)0xFF21, 4)).Should().BeFalse("全形");
        CodexDeviceCodeRules.IsValidUserCode("<b>ABCD</b>").Should().BeFalse("HTML");
        CodexDeviceCodeRules.IsValidUserCode("ABCD_EFGH").Should().BeFalse("底線");
    }

    [Fact]
    public void ExpiresAt_伺服器的到期與15分鐘取較早者()
    {
        CodexDeviceCodeRules.ExpiresAt(Now, null, null).Should().Be(Max, "都沒有就是 15 分鐘（G5 實測沒有到期欄位）");
        // 毫秒的絕對時間
        CodexDeviceCodeRules.ExpiresAt(Now, NowMs + 30 * 60_000, null).Should().Be(Max, "伺服器給 30 分鐘取 15 分鐘");
        CodexDeviceCodeRules.ExpiresAt(Now, NowMs + 5 * 60_000, null).Should().Be(NowMs + 5 * 60_000, "伺服器給 5 分鐘取 5 分鐘");
        // 秒數（RFC 8628 的 expires_in）
        CodexDeviceCodeRules.ExpiresAt(Now, null, 30 * 60).Should().Be(Max);
        CodexDeviceCodeRules.ExpiresAt(Now, null, 5 * 60).Should().Be(NowMs + 5 * 60_000);
        // 兩個都給：取較早者
        CodexDeviceCodeRules.ExpiresAt(Now, NowMs + 10 * 60_000, 5 * 60).Should().Be(NowMs + 5 * 60_000);
        CodexDeviceCodeRules.ExpiresAt(Now, NowMs + 4 * 60_000, 5 * 60).Should().Be(NowMs + 4 * 60_000);
        // 絕對時間其實是秒（10 位數）：換成毫秒
        CodexDeviceCodeRules.ExpiresAt(Now, NowMs / 1000 + 5 * 60, null).Should().Be(NowMs + 5 * 60_000, "秒的時間戳記換成毫秒");
        // 不合理的值不採用
        CodexDeviceCodeRules.ExpiresAt(Now, 0, null).Should().Be(Max);
        CodexDeviceCodeRules.ExpiresAt(Now, -5, null).Should().Be(Max);
        CodexDeviceCodeRules.ExpiresAt(Now, AccountIdentityRules.MaxUnixMs + 1, null).Should().Be(Max, "超過 9999 年");
        CodexDeviceCodeRules.ExpiresAt(Now, long.MaxValue, long.MaxValue).Should().Be(Max, "溢位不採用");
        CodexDeviceCodeRules.ExpiresAt(Now, null, 0).Should().Be(Max);
        CodexDeviceCodeRules.ExpiresAt(Now, null, -60).Should().Be(Max);
        // 伺服器說已經過期：照實回傳（ParseStart 會把它當成不合格）
        CodexDeviceCodeRules.ExpiresAt(Now, NowMs - 60_000, null).Should().Be(NowMs - 60_000);
    }

    [Fact]
    public void ParseStart_G5實測的形狀()
    {
        var code = CodexDeviceCodeRules.ParseStart(Measured(), Now);
        code.Should().NotBeNull();
        code!.LoginId.Should().Be(LoginId);
        code.UserCode.Should().Be("ABCD-EFGH2");
        code.VerificationUrl.Should().Be(new Uri(Url));
        code.ExpiresAt.Should().Be(Max);
    }

    [Fact]
    public void ParseStart_verificationUri的寫法_到期欄位_error是JSON_null()
    {
        var uri = CodexDeviceCodeRules.ParseStart(With(("verificationUrl", null), ("verificationUri", Url)), Now);
        uri.Should().NotBeNull("RFC 8628 的 verification_uri 寫法");
        uri!.VerificationUrl.Should().Be(new Uri(Url));

        CodexDeviceCodeRules.ParseStart(With(("expiresAt", NowMs + 5 * 60_000)), Now)!.ExpiresAt.Should().Be(NowMs + 5 * 60_000);
        CodexDeviceCodeRules.ParseStart(With(("expiresIn", 300)), Now)!.ExpiresAt.Should().Be(NowMs + 5 * 60_000);
        CodexDeviceCodeRules.ParseStart(With(("expiresAt", JValue.CreateNull())), Now)!.ExpiresAt.Should().Be(Max, "到期是 JSON null 當成沒給");
        CodexDeviceCodeRules.ParseStart(With(("expiresIn", "300")), Now)!.ExpiresAt.Should().Be(Max, "到期是字串當成沒給");

        CodexDeviceCodeRules.ParseStart(With(("error", JValue.CreateNull())), Now).Should().NotBeNull("error 是 JSON null 不影響");
        CodexDeviceCodeRules.ParseStart(With(("type", null)), Now).Should().NotBeNull("沒有 type 也可以（設計 6.4 只列三個欄位）");
    }

    [Fact]
    public void ParseStart_缺欄位_型別不對_主機不合格_都是null()
    {
        CodexDeviceCodeRules.ParseStart(null, Now).Should().BeNull();
        CodexDeviceCodeRules.ParseStart(new JObject(), Now).Should().BeNull();

        CodexDeviceCodeRules.ParseStart(With(("loginId", null)), Now).Should().BeNull("缺 loginId");
        CodexDeviceCodeRules.ParseStart(With(("userCode", null)), Now).Should().BeNull("缺 userCode");
        CodexDeviceCodeRules.ParseStart(With(("verificationUrl", null)), Now).Should().BeNull("缺網址");

        CodexDeviceCodeRules.ParseStart(With(("loginId", 123)), Now).Should().BeNull("loginId 不是字串");
        CodexDeviceCodeRules.ParseStart(With(("loginId", JValue.CreateNull())), Now).Should().BeNull("loginId 是 JSON null");
        CodexDeviceCodeRules.ParseStart(With(("loginId", "")), Now).Should().BeNull();
        CodexDeviceCodeRules.ParseStart(With(("loginId", "a b")), Now).Should().BeNull("loginId 含空白");
        CodexDeviceCodeRules.ParseStart(With(("loginId", new string('a', 129))), Now).Should().BeNull("loginId 太長");
        CodexDeviceCodeRules.ParseStart(With(("userCode", 12345678)), Now).Should().BeNull("userCode 不是字串");
        CodexDeviceCodeRules.ParseStart(With(("userCode", "abcd-efgh")), Now).Should().BeNull("userCode 小寫");
        CodexDeviceCodeRules.ParseStart(With(("verificationUrl", new JObject { ["href"] = Url })), Now).Should().BeNull("網址不是字串");
        CodexDeviceCodeRules.ParseStart(With(("type", "chatgpt")), Now).Should().BeNull("回的是一般登入");
        CodexDeviceCodeRules.ParseStart(With(("type", 1)), Now).Should().BeNull();

        CodexDeviceCodeRules.ParseStart(With(("verificationUrl", "https://auth.openai.com.evil.x/codex/device")), Now).Should().BeNull("主機不合格");
        CodexDeviceCodeRules.ParseStart(With(("verificationUrl", "http://auth.openai.com/codex/device")), Now).Should().BeNull("http");
        CodexDeviceCodeRules.ParseStart(With(("verificationUrl", "https://evil.x/codex/device"), ("verificationUri", Url)), Now).Should().BeNull(
            "verificationUrl 不合格時不改用 verificationUri");

        CodexDeviceCodeRules.ParseStart(With(("expiresAt", NowMs - 1)), Now).Should().BeNull("伺服器說已經過期");
        CodexDeviceCodeRules.ParseStart(With(("expiresAt", NowMs)), Now).Should().BeNull("剛好到期");
    }

    [Fact]
    public void CanUseDeviceCode_Codex與Grok_隔離帳戶或未登入的系統預設()
    {
        static AgentAccount Account(AgentProvider provider, bool isolated)
            => new() { Id = "a1", Provider = provider, ConfigDirectory = isolated ? @"C:\data\accounts\a1" : null };

        foreach (var deviceCodeProvider in new[] { AgentProvider.Codex, AgentProvider.Grok })
        {
            foreach (var status in Enum.GetValues<AccountStatus>())
                CodexDeviceCodeRules.CanUseDeviceCode(Account(deviceCodeProvider, true), status).Should().BeTrue($"{deviceCodeProvider} 隔離帳戶（{status}）一律可以");

            CodexDeviceCodeRules.CanUseDeviceCode(Account(deviceCodeProvider, false), AccountStatus.NotLoggedIn).Should().BeTrue("系統預設帳戶本來就未登入");
            foreach (var status in Enum.GetValues<AccountStatus>().Where((s) => s != AccountStatus.NotLoggedIn))
                CodexDeviceCodeRules.CanUseDeviceCode(Account(deviceCodeProvider, false), status).Should().BeFalse(
                    $"{deviceCodeProvider} 系統預設帳戶（{status}）不登：會換掉使用者原本的登入");
        }

        foreach (var provider in new[] { AgentProvider.Claude, AgentProvider.Antigravity })
        {
            foreach (var isolated in new[] { true, false })
                CodexDeviceCodeRules.CanUseDeviceCode(Account(provider, isolated), AccountStatus.NotLoggedIn).Should().BeFalse($"{provider} 沒有裝置碼登入");
        }

        var unknown = () => CodexDeviceCodeRules.CanUseDeviceCode(Account((AgentProvider)999, true), AccountStatus.NotLoggedIn);
        unknown.Should().Throw<ArgumentOutOfRangeException>("新種類要明確分類");
    }

    [Fact]
    public void ProgressText_只含驗證碼網址與分鐘數()
    {
        var code = new CodexDeviceCode(LoginId, "ABCD-EFGH2", new Uri(Url), Max);
        CodexDeviceCodeRules.ProgressText(code, Now).Should().Be("請在手機或其他電腦打開 https://auth.openai.com/codex/device，輸入驗證碼 ABCD-EFGH2（15 分鐘內有效）");
        CodexDeviceCodeRules.ProgressText(code, Now).Should().NotContain(LoginId);
        CodexDeviceCodeRules.ProgressText(code with { ExpiresAt = NowMs + 4 * 60_000 + 1 }, Now).Should().Contain("（5 分鐘內有效）", "不足一分鐘進位");
        CodexDeviceCodeRules.ProgressText(code with { ExpiresAt = NowMs + 1000 }, Now).Should().Contain("（1 分鐘內有效）");
        CodexDeviceCodeRules.ProgressText(code with { ExpiresAt = NowMs - 1000 }, Now).Should().Contain("（1 分鐘內有效）", "最少寫 1 分鐘");
    }

    [Fact]
    public void Refusal_非Codex與已登入的系統預設各有說明_可以用時是null()
    {
        static AgentAccount Account(AgentProvider provider, bool isolated)
            => new() { Id = "a1", Provider = provider, ConfigDirectory = isolated ? @"C:\data\accounts\a1" : null };

        CodexDeviceCodeRules.Refusal(Account(AgentProvider.Codex, true), AccountStatus.Ready).Should().BeNull();
        CodexDeviceCodeRules.Refusal(Account(AgentProvider.Codex, false), AccountStatus.NotLoggedIn).Should().BeNull();
        CodexDeviceCodeRules.Refusal(Account(AgentProvider.Codex, false), AccountStatus.Ready).Should().Contain("預設").And.Contain("新增");
        CodexDeviceCodeRules.Refusal(Account(AgentProvider.Grok, true), AccountStatus.Ready).Should().BeNull();
        CodexDeviceCodeRules.Refusal(Account(AgentProvider.Grok, false), AccountStatus.Ready).Should().Contain("Grok").And.Contain("新增");
        foreach (var provider in new[] { AgentProvider.Claude, AgentProvider.Antigravity })
            CodexDeviceCodeRules.Refusal(Account(provider, true), AccountStatus.NotLoggedIn).Should().Be("只有 Codex、Grok 帳戶能用裝置碼登入");
        var unknown = () => CodexDeviceCodeRules.Refusal(Account((AgentProvider)999, true), AccountStatus.NotLoggedIn);
        unknown.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ParseLoginMethod_沒帶是Default_deviceCode_其他值看不懂()
    {
        CodexDeviceCodeRules.ParseLoginMethod(null).Should().Be(LoginMethod.Default);
        CodexDeviceCodeRules.ParseLoginMethod(JValue.CreateNull()).Should().Be(LoginMethod.Default, "JSON null 當成沒帶");
        CodexDeviceCodeRules.ParseLoginMethod("deviceCode").Should().Be(LoginMethod.DeviceCode);

        CodexDeviceCodeRules.ParseLoginMethod("DeviceCode").Should().BeNull("大小寫不同");
        CodexDeviceCodeRules.ParseLoginMethod("default").Should().BeNull("協定只有 deviceCode 一個值");
        CodexDeviceCodeRules.ParseLoginMethod("").Should().BeNull();
        CodexDeviceCodeRules.ParseLoginMethod(1).Should().BeNull();
        CodexDeviceCodeRules.ParseLoginMethod(new JObject()).Should().BeNull();
    }

    [Fact]
    public void Redact_遮掉驗證碼與loginId_限制長度()
    {
        var code = new CodexDeviceCode(LoginId, "ABCD-EFGH2", new Uri(Url), Max);
        CodexDeviceCodeRules.Redact("device code abcd-efgh2 expired (" + LoginId.ToUpperInvariant() + ")", code)
            .Should().Be("device code [已遮蔽] expired ([已遮蔽])", "不分大小寫");
        CodexDeviceCodeRules.Redact(new string('x', 500), code)!.Length.Should().Be(CodexDeviceCodeRules.MaxLoggedErrorLength);
        CodexDeviceCodeRules.Redact(null, code).Should().BeNull();
        CodexDeviceCodeRules.Redact("  ", code).Should().BeNull();
    }

    [Fact]
    public void FieldNames_只回欄位名稱()
    {
        var names = CodexDeviceCodeRules.FieldNames(With(("verificationUrl", "https://evil.x/")));
        names.Should().Equal("loginId", "type", "userCode", "verificationUrl");
        string.Join(",", names).Should().NotContain("ABCD").And.NotContain("evil").And.NotContain(LoginId);
        CodexDeviceCodeRules.FieldNames(null).Should().BeEmpty();
        CodexDeviceCodeRules.FieldNames(new JObject()).Should().BeEmpty();

        var many = new JObject();
        for (var i = 0; i < 50; i++)
            many["f" + i.ToString("00")] = i;
        many["a" + new string('x', 200)] = 1;   // 排序在最前面：一定會被截短
        var capped = CodexDeviceCodeRules.FieldNames(many);
        capped.Count.Should().Be(CodexDeviceCodeRules.MaxFieldNames, "記錄檔不會被塞爆");
        capped.Should().OnlyContain((n) => n.Length <= CodexDeviceCodeRules.MaxFieldNameLength);
        capped[0].Should().Be("a" + new string('x', CodexDeviceCodeRules.MaxFieldNameLength - 1));
    }
}
