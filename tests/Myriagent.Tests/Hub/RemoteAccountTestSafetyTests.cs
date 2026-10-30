using System.Text.RegularExpressions;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// WP4.8 D30 的靜態掃描：經閘門送帳戶與 CLI 指令（accountLogin、accountLoginDevice、accountLogout、cliInstall、cliUpdate）的測試，
/// 處理者會真的登入、登出、安裝——一定要用假程式。同一個測試檔同時出現這五個指令名稱之一（字串常值）與遠端指令的入口
/// （RunAsync(、ExecuteRemoteCommandAsync、ExecuteRemoteAsync）時，要嘛用 RemoteAccountRig，要嘛同時覆寫 ResolveLauncher 與 createCli；
/// 以 IsolateGate 拿掉處理者的檔案（閘門測試）除外。不然 HubHarness 的預設 CliManager 會找到這台真的 CLI，
/// 系統預設的 acc-claude 會在使用者真的 ~/.claude 跑 auth logout、cliInstall 會開真的安裝視窗
/// </summary>
public sealed class RemoteAccountTestSafetyTests
{
    private static readonly string[] AccountCommands = ["accountLogin", "accountLoginDevice", "accountLogout", "cliInstall", "cliUpdate"];

    private static readonly string[] RemoteEntrances = ["RunAsync(", "ExecuteRemoteCommandAsync", "ExecuteRemoteAsync"];

    /// <summary>這個檔經閘門送帳戶或 CLI 指令（指令名稱以字串常值出現，加上遠端指令的入口）</summary>
    internal static bool SendsAccountCommands(string source)
        => AccountCommands.Any(c => source.Contains('"' + c + '"', StringComparison.Ordinal))
           && RemoteEntrances.Any(e => source.Contains(e, StringComparison.Ordinal));

    /// <summary>
    /// 用了假程式：建立 RemoteAccountRig，或同時指定帳戶管理員找 CLI 的接縫（<c>ResolveLauncher =</c>）與 CliManager（<c>createCli:</c>）；或呼叫 IsolateGate 拿掉處理者。
    /// 只看去掉註解之後的程式碼、而且要是呼叫的形狀（審查 CRED-1：註解裡提到 RemoteAccountRig 不算）。去註解是保守的（字串裡的 <c>//</c>、<c>/*</c> 也會切掉後面的字，
    /// 只會讓這裡更容易判成沒有用假程式）；<see cref="SendsAccountCommands"/> 照舊看原始碼（註解裡的指令名稱也算有送）
    /// </summary>
    internal static bool UsesFakes(string source)
    {
        var code = StripComments(source);
        return code.Contains("new RemoteAccountRig(", StringComparison.Ordinal)
               || code.Contains("ResolveLauncher =", StringComparison.Ordinal) && code.Contains("createCli:", StringComparison.Ordinal)
               || code.Contains("IsolateGate(", StringComparison.Ordinal);
    }

    /// <summary>去掉 <c>/* … */</c> 與 <c>// …</c>（到行尾）的註解（不分析字串常值，見 <see cref="UsesFakes"/>）</summary>
    internal static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//.*$", string.Empty, RegexOptions.Multiline);
    }

    internal static bool Violates(string source) => SendsAccountCommands(source) && !UsesFakes(source);

    private static List<(string Name, string Source)> TestSources()
    {
        var root = Path.Combine(MyriagentHubExe.RepoRoot(), "tests", "Myriagent.Tests");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(path => (Path.GetRelativePath(root, path), File.ReadAllText(path)))
            .ToList();
    }

    [Fact]
    public void 送帳戶與CLI遠端指令的測試檔都用假程式()
    {
        var sources = TestSources();
        sources.Should().HaveCountGreaterThan(100, "前提：掃到整個測試專案");

        var sending = sources.Where(s => SendsAccountCommands(s.Source)).Select(s => s.Name).ToList();
        sending.Should().Contain([Path.Combine("Hub", "AgentHubRemoteLoginTests.cs"), Path.Combine("Hub", "AgentHubRemoteLogoutCliTests.cs"),
            Path.Combine("Hub", "RemoteCommandCoverageTests.cs")], "陽性對照：掃描找得到本包經閘門送帳戶指令的測試");

        sources.Where(s => Violates(s.Source)).Select(s => s.Name).Should().BeEmpty(
            "經閘門送帳戶或 CLI 指令的測試檔要用 RemoteAccountRig（或同時覆寫 ResolveLauncher 與 createCli），不然會跑到這台真的 CLI");
    }

    [Fact]
    public void 掃描規則_陽性對照()
    {
        const string sends = """
            using var h = new HubHarness();
            var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteSamples.Command("accountLogout", ("accountId", "acc-claude"))));
            """;
        Violates(sends).Should().BeTrue("預設的 HubHarness 會找到真的 CLI");
        Violates(sends + "\nh.Accounts.ResolveLauncher = _ => null;").Should().BeTrue("只換掉找 CLI 的接縫不夠：cliInstall 走 CliManager");
        Violates(sends + "\nh.Accounts.ResolveLauncher = _ => null;\nnew HubHarness(createCli: http => fake);").Should().BeFalse();
        Violates(sends.Replace("new HubHarness()", "new RemoteAccountRig()", StringComparison.Ordinal)).Should().BeFalse();
        Violates(sends + "\nIsolateGate(h);").Should().BeFalse("拿掉處理者的閘門測試不會執行帳戶指令");
        // 審查 CRED-1：只在註解裡提到假程式不算
        Violates("// 一律經 RemoteAccountRig\n" + sends).Should().BeTrue("只在註解提到 rig");
        Violates("/* new RemoteAccountRig() */\n" + sends).Should().BeTrue("區塊註解裡的 rig");
        Violates(sends + "\n// IsolateGate(h);").Should().BeTrue("註解掉的 IsolateGate");
        Violates(sends + "\n// h.Accounts.ResolveLauncher = _ => null;\n// new HubHarness(createCli: http => fake);").Should().BeTrue("註解掉的兩個接縫");
        Violates(sends + "\n/// <see cref=\"RemoteAccountRig\"/> 的 ResolveLauncher 與 createCli: 都換掉").Should().BeTrue("XML 說明裡的名稱");
        Violates(sends + "\nvar rig = new RemoteAccountRig(); // 一律經 rig").Should().BeFalse("行尾註解之前的程式照算");
        Violates(sends.Replace("RunAsync(", "Prepare(", StringComparison.Ordinal)).Should().BeFalse("沒有經過遠端指令的入口");
        Violates("""var name = "cliInstall"; await h.Hub.ExecuteRemoteAsync(context, command, _ => { });""").Should().BeTrue();
        Violates("""await h.Hub.ExecuteRemoteCommandAsync(body, _ => { }); // "cliUpdate" """).Should().BeTrue();
        Violates("""await RunAsync(h, "accountRefresh");""").Should().BeFalse("不是這五個指令");
    }

    [Fact]
    public void 覆蓋率測試用rig()
    {
        // 型錄的每個指令都經閘門跑一次（指令名稱來自型錄，不是字串常值）：掃描看不出來，另外釘住
        var source = File.ReadAllText(Path.Combine(MyriagentHubExe.RepoRoot(), "tests", "Myriagent.Tests", "Hub", "RemoteCommandCoverageTests.cs"));
        source.Should().Contain("new RemoteAccountRig(");
        source.Should().NotContain("new HubHarness(", "覆蓋率測試的中樞一律是 rig 的");
    }
}
