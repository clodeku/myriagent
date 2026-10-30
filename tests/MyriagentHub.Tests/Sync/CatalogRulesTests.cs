using System.IO.Compression;
using System.Text;
using Myriagent.Sync;
using MyriagentHub.Sync;

namespace MyriagentHub.Tests.Sync;

/// <summary>上行事件與帳戶目錄的格式規則：裝置寫的內容一律不可信</summary>
public sealed class CatalogRulesTests
{
    [Theory]
    [InlineData("claude:org1:a@example.com", "Claude", true)]
    [InlineData("claude:a@example.com", "Claude", true)]
    [InlineData("codex:a@example.com", "Codex", true)]
    [InlineData("codex:a@example.com", "Claude", false)]
    [InlineData("claude:", "Claude", false)]
    [InlineData("claude:a b@example.com", "Claude", false)]
    [InlineData("claude:a\n@example.com", "Claude", false)]
    [InlineData("gemini:a@example.com", "Gemini", false)]
    [InlineData("grok:a@example.com", "Grok", true)]
    [InlineData("antigravity:a@example.com", "Antigravity", true)]
    [InlineData("grok:a@example.com", "Antigravity", false)]
    [InlineData("antigravity:", "Antigravity", false)]
    [InlineData("codex:a@example.com", "Grok", false)]
    public void 身分鍵_前綴與供應商相符_沒有空白控制字元(string key, string provider, bool expected) =>
        CatalogRules.IsIdentityKey(key, provider).Should().Be(expected);

    [Theory]
    [InlineData("#D97757", true)]
    [InlineData("#abc", true)]
    [InlineData("D97757", false)]
    [InlineData("#D9775", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData("red", false)]
    public void 顏色只收井字號加3或6位十六進位(string color, bool expected) => CatalogRules.IsColor(color).Should().Be(expected);

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0x09, false)]
    [InlineData(0x7F, false)]
    [InlineData(0xD800, false)]
    [InlineData(0xDC00, false)]
    [InlineData(0x202E, true)]
    [InlineData(0x200D, true)]
    [InlineData(0xFEFF, true)]
    public void 顯示文字_只拒絕控制字元與單獨的代理字元_格式字元留給清理(int codepoint, bool expected)
    {
        // 字元以碼位在執行時組出來：屬性裡的單獨代理字元會被編譯器換成 U+FFFD
        var text = codepoint < 0 ? "OFFICE-PC" : "evil" + (char)codepoint + "txt.exe";
        CatalogRules.IsOptionalText(text, 64).Should().Be(expected);
    }

    [Fact]
    public void 顯示文字_清理去掉格式字元與補充平面的標籤字元_一般文字與emoji保留()
    {
        CatalogRules.IsOptionalText(null, 64).Should().BeTrue();
        CatalogRules.IsOptionalText(new string('x', 65), 64).Should().BeFalse();
        var emoji = char.ConvertFromUtf32(0x1F600);
        CatalogRules.IsOptionalText("王小明 " + emoji, 64).Should().BeTrue();

        CatalogRules.CleanText(null, 64).Should().BeNull();
        CatalogRules.CleanText("evil" + (char)0x202E + "txt.exe", 64).Should().Be("eviltxt.exe");
        CatalogRules.CleanText((char)0xFEFF + "2.1.0", 64).Should().Be("2.1.0", "擷取 CLI 輸出時常見的 BOM");
        CatalogRules.CleanText("a" + char.ConvertFromUtf32(0xE0041) + "b", 64).Should().Be("ab", "補充平面的格式字元（標籤字元）也去掉");
        CatalogRules.CleanText("王小明 " + emoji, 64).Should().Be("王小明 " + emoji);
        CatalogRules.CleanText((char)0x200B + " ", 64).Should().BeNull("清理後是空的");
    }

    [Fact]
    public void link_upsert的格式檢查()
    {
        var ok = EventLogTests.Link("acct-1", "claude:org1:a@example.com");
        CatalogRules.ValidateLink(ok).Should().BeNull();
        CatalogRules.ValidateLink(ok with { LocalAccountId = "../../x" }).Should().NotBeNull();
        CatalogRules.ValidateLink(ok with { Provider = "Gemini" }).Should().NotBeNull();
        CatalogRules.ValidateLink(ok with { IdentityKey = "codex:a@example.com" }).Should().NotBeNull("身分鍵與供應商不符");
        CatalogRules.ValidateLink(ok with { LogicalAccountId = "not-an-id" }).Should().NotBeNull();
        CatalogRules.ValidateLink(ok with { Status = "ready!" }).Should().NotBeNull();
        CatalogRules.ValidateLink(ok with { StatusText = new string('x', CatalogRules.MaxTextLength + 1) }).Should().NotBeNull();
        CatalogRules.ValidateLink(ok with { Sessions = new LinkSessions(-1, 0, 0) }).Should().NotBeNull();
        CatalogRules.ValidateLink(ok with { Sessions = new LinkSessions(0, 0, 0, -1) }).Should().NotBeNull("等背景工作回報的對話數一樣不能是負數");
        CatalogRules.ValidateLink(ok with { Sessions = new LinkSessions(0, 0, 0, 0, 3) }).Should().BeNull("背景程式數（選用）");
        CatalogRules.ValidateLink(ok with { Sessions = new LinkSessions(0, 0, 0, 0, -1) }).Should().NotBeNull("背景程式數一樣不能是負數");
        CatalogRules.ValidateLink(ok with { Label = "   " }).Should().NotBeNull();
        CatalogRules.ValidateLink(ok with { SortOrder = CatalogRules.MaxSortOrder + 1 }).Should().NotBeNull();
        CatalogRules.ValidateLink(null).Should().NotBeNull();
    }

    [Fact]
    public void 四種代理都進目錄_Grok與Antigravity以信箱當身分鍵()
    {
        new[] { "Claude", "Codex", "Grok", "Antigravity" }.Should().OnlyContain(p => CatalogRules.IsProvider(p));
        new[] { "claude", "Gemini", "", null }.Should().OnlyContain(p => !CatalogRules.IsProvider(p));
        var grok = EventLogTests.Link("acct-g", "grok:a@example.com") with { Provider = "Grok" };
        CatalogRules.ValidateLink(grok).Should().BeNull();
        CatalogRules.ValidateLink(grok with { IdentityKey = null }).Should().BeNull("沒有信箱的 Grok 登入（X 帳號）沒有身分鍵");
        var agy = EventLogTests.Link("local-antigravity", "antigravity:a@example.com") with { Provider = "Antigravity", ConfigKind = "systemDefault" };
        CatalogRules.ValidateLink(agy).Should().BeNull();
        CatalogRules.ValidateLink(agy with { IdentityKey = "grok:a@example.com" }).Should().NotBeNull("身分鍵與供應商不符");
    }

    [Fact]
    public void cli_state最多四種_不認得的種類整筆拒收()
    {
        var view = new DeviceCliView(true, "1.0.0", "npm", "1.0.0", false, 0, 0, null, 1);
        var four = new Dictionary<string, DeviceCliView> { ["Claude"] = view, ["Codex"] = view, ["Antigravity"] = view, ["Grok"] = view };
        CatalogRules.ValidateCli(new CliStatePayload(four)).Should().BeNull();
        CatalogRules.ValidateCli(new CliStatePayload(new Dictionary<string, DeviceCliView>(four) { ["Gemini"] = view })).Should().NotBeNull();
        CatalogRules.ValidateCli(new CliStatePayload(new Dictionary<string, DeviceCliView> { ["claude"] = view })).Should().NotBeNull();
    }

    [Fact]
    public void ValidateApp真值表()
    {
        var ok = new AppStatePayload("0.2.300", "0.2.300-ab12cd3", AppStatePhases.Staged, "0.2.301-0123abc", null, 1000);
        CatalogRules.ValidateApp(ok).Should().BeNull();
        CatalogRules.ValidateApp(ok with { Release = null, Staged = null }).Should().BeNull("開發版沒有 release 名稱、也沒有備妥的版本");
        CatalogRules.ValidateApp(ok with { LastError = new string('x', CatalogRules.MaxTextLength) }).Should().BeNull();
        // 字元以碼位在執行時組出來：格式字元（雙向覆寫）不拒絕，存進 device_app 時去掉
        CatalogRules.ValidateApp(ok with { LastError = "新版" + (char)0x202E + "啟動失敗" }).Should().BeNull();
        foreach (var phase in new[] { AppStatePhases.Idle, AppStatePhases.Downloading, AppStatePhases.Staged, AppStatePhases.Waiting, AppStatePhases.Draining, AppStatePhases.Applying, AppStatePhases.RolledBack, AppStatePhases.Blocked })
            CatalogRules.ValidateApp(ok with { Phase = phase }).Should().BeNull(phase);

        CatalogRules.ValidateApp(null).Should().NotBeNull();
        foreach (var version in new string?[] { null, "", "0.2", "0.2.300.1", "0.02.300", " 0.2.300", "0.2.300 ", "0.2.300-ab12cd3", "v0.2.300" })
            CatalogRules.ValidateApp(ok with { Version = version! }).Should().NotBeNull("version「" + version + "」");
        foreach (var release in new[] { "", "0.2.300", "0.2.300-dev", "0.2.300-AB12CD3", "0.2.300-ab12cd", " 0.2.300-ab12cd3" })
        {
            CatalogRules.ValidateApp(ok with { Release = release }).Should().NotBeNull("release「" + release + "」");
            CatalogRules.ValidateApp(ok with { Staged = release }).Should().NotBeNull("staged「" + release + "」");
        }
        foreach (var phase in new string?[] { null, "", "Idle", "rolledback", "updating" })
            CatalogRules.ValidateApp(ok with { Phase = phase! }).Should().NotBeNull("phase「" + phase + "」");
        CatalogRules.ValidateApp(ok with { LastError = new string('x', CatalogRules.MaxTextLength + 1) }).Should().NotBeNull();
        CatalogRules.ValidateApp(ok with { LastError = "第一行\n第二行" }).Should().NotBeNull("換行是控制字元");
        CatalogRules.ValidateApp(ok with { LastError = "壞的" + (char)0xD800 }).Should().NotBeNull("單獨的代理字元");
        CatalogRules.ValidateApp(ok with { CheckedAt = 0 }).Should().NotBeNull();
        CatalogRules.ValidateApp(ok with { CheckedAt = -1 }).Should().NotBeNull();
    }

    [Fact]
    public void payload_原文與gzip還原_太大與格式錯誤分開回()
    {
        const string json = "{\"a\":\"臺灣\"}";
        CatalogRules.CheckPayload(PayloadRules.EncJson, json, 100, out var plain).Should().Be(PayloadCheck.Ok);
        plain.Should().Be(json);
        CatalogRules.CheckPayload(PayloadRules.EncGzip, Gzip(Encoding.UTF8.GetBytes(json)), 100, out var unzipped).Should().Be(PayloadCheck.Ok);
        unzipped.Should().Be(json);

        CatalogRules.CheckPayload(PayloadRules.EncJson, json, 5, out _).Should().Be(PayloadCheck.TooLarge);
        CatalogRules.CheckPayload(PayloadRules.EncGzip, Gzip(new byte[10 * 1024 * 1024]), 128 * 1024, out _).Should().Be(PayloadCheck.TooLarge, "讀到超過就停");
        CatalogRules.CheckPayload(PayloadRules.EncGzip, "不是 base64", 100, out _).Should().Be(PayloadCheck.Invalid);
        CatalogRules.CheckPayload(PayloadRules.EncGzip, Convert.ToBase64String([1, 2, 3]), 100, out _).Should().Be(PayloadCheck.Invalid);
        CatalogRules.CheckPayload(PayloadRules.EncGzip, Gzip([0xC3, 0x28]), 100, out _).Should().Be(PayloadCheck.Invalid, "不合法的 UTF-8");
        CatalogRules.CheckPayload("br", json, 100, out _).Should().Be(PayloadCheck.Invalid);
        CatalogRules.CheckPayload(PayloadRules.EncJson, null, 100, out _).Should().Be(PayloadCheck.Invalid);
    }

    private static string Gzip(byte[] bytes)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(bytes);
        return Convert.ToBase64String(buffer.ToArray());
    }
}
