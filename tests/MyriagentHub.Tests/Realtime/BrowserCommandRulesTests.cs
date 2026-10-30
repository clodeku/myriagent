using System.Text.RegularExpressions;
using MyriagentHub.Realtime;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 瀏覽器模式的指令分類（web-protocol 第六節第 9 點）：web-protocol 第二節的每一個 cmd 都有分類，網頁原始碼送出的 cmd 都在表裡，
/// 網頁新增 cmd 時這裡會紅、逼著先補 web-protocol 再補分類；不認得的名稱一律不分類（呼叫端回 invalid）
/// </summary>
public sealed partial class BrowserCommandRulesTests
{
    /// <summary>第 5 階段規劃、還不在 web-protocol 第二節的指令（回 not_available；addUploaded、accountLoginDevice 第 4 階段起在表裡）</summary>
    private static readonly string[] PlannedCommands = ["takeover", "handoffCancel"];

    [Fact]
    public void web_protocol的每個cmd都有瀏覽器分類()
    {
        var names = WebProtocolCommands();
        names.Count.Should().BeGreaterThanOrEqualTo(60, "確認讀到的是 web-protocol 第二節的表");
        foreach (var name in names)
            BrowserCommandRules.Classify(name).Should().NotBeNull($"web-protocol 的 {name} 要在 BrowserCommandRules 有分類");
    }

    [Fact]
    public void 網頁原始碼送出的cmd都在web_protocol的表裡()
    {
        var table = WebProtocolCommands();
        var sent = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(RepoFiles.Combine("src", "Myriagent", "Web"), "*.js"))
        {
            var source = File.ReadAllText(file);
            foreach (var regex in new[] { CmdLiteral(), CtxPost(), CmdTernary() })
            {
                foreach (Match match in regex.Matches(source))
                {
                    for (var g = 1; g < match.Groups.Count; g++)
                    {
                        if (match.Groups[g].Success)
                            sent.Add(match.Groups[g].Value);
                    }
                }
            }
        }

        sent.Count.Should().BeGreaterThanOrEqualTo(50, "三種樣式要抓得到網頁送出的指令（避免空轉通過）");
        sent.Should().OnlyContain(name => table.Contains(name), "網頁送出的指令都要先寫進 web-protocol 第二節");
    }

    [Fact]
    public void 分類集合只含web_protocol或設計4_9_3的名稱()
    {
        var table = WebProtocolCommands();
        BrowserCommandRules.ServerCommands.Should().OnlyContain(n => table.Contains(n));
        BrowserCommandRules.IgnoredCommands.Should().OnlyContain(n => table.Contains(n));
        BrowserCommandRules.DesktopOnlyCommands.Should().OnlyContain(n => table.Contains(n));
        BrowserCommandRules.NotAvailableCommands.Where(n => !table.Contains(n)).Should().BeEquivalentTo(PlannedCommands);
        BrowserCommandRules.SilentCommands.Should().OnlyContain(n => table.Contains(n));
    }

    [Fact]
    public void 四類集合互不重疊()
    {
        var sets = new[]
        {
            BrowserCommandRules.ServerCommands,
            BrowserCommandRules.IgnoredCommands,
            BrowserCommandRules.DesktopOnlyCommands,
            BrowserCommandRules.NotAvailableCommands,
        };
        for (var i = 0; i < sets.Length; i++)
        {
            for (var j = i + 1; j < sets.Length; j++)
                sets[i].Intersect(sets[j]).Should().BeEmpty($"第 {i} 類與第 {j} 類不可重疊");
        }
    }

    [Theory]
    [InlineData("ready", "Server")]
    [InlineData("setting", "Server")]
    [InlineData("deviceRename", "Server")]
    [InlineData("visible", "Ignored")]
    [InlineData("openFolder", "DesktopOnly")]
    [InlineData("appUpdateCheck", "DesktopOnly")]
    [InlineData("draftSnapshot", "DesktopOnly")]
    [InlineData("send", "NotAvailable")]
    [InlineData("takeover", "NotAvailable")]
    public void 指令照表分類(string cmd, string expected) =>
        BrowserCommandRules.Classify(cmd).Should().Be(Enum.Parse<BrowserCommandClass>(expected));

    [Fact]
    public void 第4階段契約的新指令在表裡且照舊不開放()
    {
        // WP4.1 只立契約：web-protocol 第二節多了四列，瀏覽器模式的結果與第 3 階段相同（遠端指令一律 not_available）
        var table = WebProtocolCommands();
        foreach (var name in new[] { "addUploaded", "accountLoginDevice", "fileFetch", "syncSetConversationPolicy" })
            table.Should().Contain(name, $"{name} 要寫進 web-protocol 第二節的表");
        BrowserCommandRules.Classify("addUploaded").Should().Be(BrowserCommandClass.NotAvailable);
        BrowserCommandRules.Classify("accountLoginDevice").Should().Be(BrowserCommandClass.NotAvailable);
        BrowserCommandRules.Classify("fileFetch").Should().Be(BrowserCommandClass.NotAvailable);
        BrowserCommandRules.Classify("syncSetConversationPolicy").Should().Be(BrowserCommandClass.DesktopOnly);
        BrowserCommandRules.DesktopOnlyCommands.Should().Contain("syncSetConversationPolicy", "明列，不只靠 sync 前綴");
        table.Should().NotContain(PlannedCommands, "第 5 階段的指令只寫在附註");
    }

    [Theory]
    [InlineData("syncPair")]
    [InlineData("syncSetPolicy")]
    [InlineData("syncConfirm")]
    [InlineData("syncSomethingNew")]
    public void sync開頭的指令一律desktop_only(string cmd) =>
        BrowserCommandRules.Classify(cmd).Should().Be(BrowserCommandClass.DesktopOnly);

    [Theory]
    [InlineData("sync")]
    [InlineData("syncing")]
    [InlineData("")]
    [InlineData("Ready")]
    [InlineData("READY")]
    [InlineData("toString")]
    [InlineData("__proto__")]
    [InlineData("setSyncPolicy")]
    [InlineData(null)]
    public void 不認得的cmd回null(string? cmd) => BrowserCommandRules.Classify(cmd).Should().BeNull();

    [Fact]
    public void Instructions等本機設定是桌面專屬鍵()
    {
        // SEC-24：只能在電腦本機改的設定，瀏覽器送來一律 desktop_only
        foreach (var key in new[] { "instructions", "closeToTray", "autoUpdateCli", "autoUpdateApp", "cliRestartPolicy", "claudeRemoteControl", "autoSwitchAccount", "autoSwitchCrossProvider", "shareCrossCliCustomizations" })
            BrowserCommandRules.DesktopOnlySettingKeys.Contains(key).Should().BeTrue(key);
        BrowserCommandRules.DesktopOnlySettingKeys.Contains("theme").Should().BeFalse();
        BrowserCommandRules.DesktopOnlySettingKeys.Contains("Instructions").Should().BeFalse("比對大小寫有別；其他寫法落到 invalid");
    }

    [Theory]
    [InlineData("newConversation", "not_available")]
    [InlineData("listFiles", "not_available")]
    [InlineData("fileSearch", "not_available")]
    [InlineData("visible", "not_available")]
    [InlineData("draftSnapshot", "desktop_only")]
    [InlineData("layout", "invalid")]
    [InlineData("send", "rate_limited")]
    [InlineData("send", "too_many_connections")]
    public void 自動送出的指令與限流不送toast(string cmd, string error) =>
        BrowserCommandRules.Toast(cmd, error).Should().BeNull();

    [Fact]
    public void 明確動作被拒時送toast()
    {
        var send = BrowserCommandRules.Toast("send", BrowserErrorCodes.NotAvailable);
        send.Should().NotBeNull();
        send!.Value.Level.Should().Be("error");
        send.Value.Text.Should().NotBeNullOrWhiteSpace();

        BrowserCommandRules.Toast("openFolder", BrowserErrorCodes.DesktopOnly)!.Value.Text.Should().Contain("電腦");
        BrowserCommandRules.Toast("accountUpdate", BrowserErrorCodes.Conflict)!.Value.Text.Should().Contain("刪除");
        BrowserCommandRules.Toast("setting", BrowserErrorCodes.Invalid)!.Value.Text.Should().Contain("格式");
        BrowserCommandRules.Toast("setting", BrowserErrorCodes.Unavailable)!.Value.Text.Should().Contain("稍後");
    }

    [Fact]
    public void Owner路由的conflict說對話不在了_帳戶指令照舊說帳戶()
    {
        // WP4.5 D4：Owner 路由的對話不存在、已刪除或不再同步時 Cmd 回 conflict（web-protocol「目標已刪除」），提示要說對話，不是帳戶
        BrowserCommandRules.ConversationGoneToast.Should().Contain("對話");
        foreach (var cmd in new[] { "send", "stop", "approve", "rename", "delete", "setAccount", "addUploaded", "fileFetch" })
        {
            var toast = BrowserCommandRules.Toast(cmd, BrowserErrorCodes.Conflict);
            toast.Should().NotBeNull(cmd);
            toast!.Value.Text.Should().Be(BrowserCommandRules.ConversationGoneToast, cmd);
            toast.Value.Level.Should().Be("error");
        }

        foreach (var cmd in new[] { "accountUpdate", "accountReorder", "accountLogout", "accountRefresh", "accountLoginDevice", "cliCheck" })
            BrowserCommandRules.Toast(cmd, BrowserErrorCodes.Conflict)!.Value.Text.Should().Be("這個帳戶已從目錄刪除", cmd);
        BrowserCommandRules.Toast("listFiles", BrowserErrorCodes.Conflict).Should().BeNull("自動送出的指令照舊不提示");
    }

    [Fact]
    public void 同一cmd十秒內只送一則toast()
    {
        const long t = 1_791_580_000_000;
        BrowserCommandRules.ShouldToast(null, t).Should().BeTrue();
        BrowserCommandRules.ShouldToast(t, t).Should().BeFalse();
        BrowserCommandRules.ShouldToast(t, t + 9_999).Should().BeFalse();
        BrowserCommandRules.ShouldToast(t, t + 10_000).Should().BeTrue();
        BrowserCommandRules.ShouldToast(t, t - 60_000).Should().BeTrue("時鐘被往回調時不壓住提示");
    }

    [Fact]
    public void 目錄的結果對應瀏覽器的錯誤碼()
    {
        BrowserCommandRules.ErrorFor(null).Should().BeNull("接受");
        BrowserCommandRules.ErrorFor(Myriagent.Sync.SyncErrorCodes.Invalid).Should().Be(BrowserErrorCodes.Invalid);
        BrowserCommandRules.ErrorFor(MyriagentHub.Sync.CatalogRejections.Conflict).Should().Be(BrowserErrorCodes.Conflict);
        var unknown = () => BrowserCommandRules.ErrorFor("seq_regression");
        unknown.Should().Throw<ArgumentOutOfRangeException>("新的拒絕原因要先決定瀏覽器看到什麼");
    }

    [Fact]
    public void 帳戶修改的稽核只記改了哪些欄位()
    {
        BrowserCommandRules.UpdatedFields("工作", null).Should().Equal(["label"]);
        BrowserCommandRules.UpdatedFields(null, "#D97757").Should().Equal(["color"]);
        BrowserCommandRules.UpdatedFields("工作", "#D97757").Should().Equal(["label", "color"]);
        BrowserCommandRules.UpdatedFields(null, null).Should().BeEmpty();
    }

    [Fact]
    public void toast訊息的形狀()
    {
        var json = Newtonsoft.Json.Linq.JObject.Parse(BrowserCommandRules.ToastJson("伺服器暫時無法處理，請稍後再試", "error"));
        json.Properties().Select(p => p.Name).Should().Equal(["type", "text", "level"]);
        json["type"]!.ToString().Should().Be("toast");
        json["text"]!.ToString().Should().Be("伺服器暫時無法處理，請稍後再試");
        json["level"]!.ToString().Should().Be("error");
        BrowserCommandRules.ToastJson("<b>\"x\"</b>", "error").Should().Contain("\\\"x\\\"", "文字以 JSON 字串跳脫");
    }

    /// <summary>web-protocol 第二節（「## 二、」到「## 三、」）表格第一欄的反引號名稱</summary>
    internal static HashSet<string> WebProtocolCommands()
    {
        var text = File.ReadAllText(RepoFiles.Combine("docs", "web-protocol.md"));
        var start = text.IndexOf("## 二、", StringComparison.Ordinal);
        var end = text.IndexOf("## 三、", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        end.Should().BeGreaterThan(start);
        var section = text[start..end];
        return TableCommand().Matches(section).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^\|\s*`([A-Za-z]+)`\s*\|", RegexOptions.Multiline)]
    private static partial Regex TableCommand();

    [GeneratedRegex(@"\bcmd:\s*['""]([A-Za-z]+)['""]")]
    private static partial Regex CmdLiteral();

    /// <summary>items.js 的 <c>ctx.post('approve', …)</c></summary>
    [GeneratedRegex(@"\bctx\.post\(\s*['""]([A-Za-z]+)['""]")]
    private static partial Regex CtxPost();

    /// <summary>dashboard.js 的 <c>cmd: busy ? 'accountLoginCancel' : 'accountLogin'</c></summary>
    [GeneratedRegex(@"\bcmd:\s*[^,}]*\?\s*['""]([A-Za-z]+)['""]\s*:\s*['""]([A-Za-z]+)['""]")]
    private static partial Regex CmdTernary();
}
