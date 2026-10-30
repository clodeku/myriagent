using Myriagent.Agents;
using Myriagent.Tests.Agents;
using FakeDevice;

namespace Myriagent.Tests.FakeDevice;

/// <summary>
/// 假 Codex app-server 啟動器的規則（WP4.9 計畫步 13，D11）：只在「代理是 Codex」「腳本是 TEMP 底下的 .ps1」「執行檔是 System32 的 Windows PowerShell」
/// 三者都成立時交出；使用者真的 .codex、走出 TEMP 的路徑、其他代理、別的 PowerShell 一律拒絕；ResolveLauncher 只把 Codex 交給假啟動器；剝掉 PSModulePath
/// </summary>
public sealed class FakeLauncherRulesTests
{
    private const string Profile = @"C:\Users\u";

    private const string Temp = @"C:\Users\u\AppData\Local\Temp";

    private const string PowerShell = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

    private static readonly FakeLauncherEnvironment Env = new(Temp + @"\", PowerShell, Profile);

    [Fact]
    public void TEMP底下的腳本_System32的PowerShell_交出假啟動器()
    {
        var launcher = FakeLauncherRules.Launcher(AgentProvider.Codex, Temp + @"\x\s.ps1", PowerShell, Env);
        launcher.Should().NotBeNull();
        launcher!.FileName.Should().Be(PowerShell);
        launcher.PrefixArguments.Should().Equal("-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Temp + @"\x\s.ps1");
        FakeLauncherRules.Check(AgentProvider.Codex, Temp + @"\x\s.ps1", PowerShell, Env).Should().BeNull();

        // 正規化之後比：大小寫、多餘的 .\ 與結尾斜線不影響；腳本交出的是正規化之後的完整路徑
        var odd = FakeLauncherRules.Launcher(AgentProvider.Codex, Temp.ToUpperInvariant() + @"\x\.\s.PS1", @"c:\windows\system32\windowspowershell\v1.0\POWERSHELL.EXE", Env);
        odd.Should().NotBeNull();
        odd!.PrefixArguments[^1].Should().Be(Temp.ToUpperInvariant() + @"\x\s.PS1");
        odd.FileName.Should().Be(@"c:\windows\system32\windowspowershell\v1.0\POWERSHELL.EXE", "執行檔照給的路徑");
    }

    [Theory]
    [InlineData(Profile + @"\.codex\fake.ps1", "TEMP")]
    [InlineData(Profile + @"\.claude\fake.ps1", "TEMP")]
    [InlineData(Temp + @"\..\x\s.ps1", "TEMP")]
    [InlineData(Temp + @"\..\..\..\.codex\s.ps1", "TEMP")]
    [InlineData(Temp + @"2\s.ps1", "TEMP")]
    [InlineData(Temp + @".ps1", "TEMP")]
    [InlineData(Temp + @"\x\s.cmd", ".ps1")]
    [InlineData(Temp + @"\x\s.ps1.txt", ".ps1")]
    [InlineData(@"x\s.ps1", "完整路徑")]
    [InlineData(@"\\server\share\s.ps1", "UNC")]
    [InlineData(@"\\?\C:\Users\u\AppData\Local\Temp\s.ps1", "UNC")]
    [InlineData(@"//server/share/s.ps1", "UNC")]
    [InlineData(Temp + @"\s.ps1:evil", "替代資料流")]
    [InlineData(Temp + @"\*.ps1", "不合法的字元")]
    [InlineData("", "空的")]
    public void 腳本不在TEMP底下或不是ps1_拒絕(string script, string reason)
    {
        FakeLauncherRules.Check(AgentProvider.Codex, script, PowerShell, Env).Should().Contain(reason);
        FakeLauncherRules.Launcher(AgentProvider.Codex, script, PowerShell, Env).Should().BeNull();
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("Antigravity")]
    [InlineData("Grok")]
    public void 其他代理沒有假的CLI(string name)
    {
        var provider = Enum.Parse<AgentProvider>(name);
        FakeLauncherRules.Check(provider, Temp + @"\x\s.ps1", PowerShell, Env).Should().Contain("沒有假的 CLI");
        FakeLauncherRules.Launcher(provider, Temp + @"\x\s.ps1", PowerShell, Env).Should().BeNull();
    }

    [Fact]
    public void 代理種類一律switch_未知值丟例外_每一種都有判斷()
    {
        var unknown = () => FakeLauncherRules.Check((AgentProvider)999, Temp + @"\x\s.ps1", PowerShell, Env);
        unknown.Should().Throw<ArgumentOutOfRangeException>();
        var resolve = () => FakeLauncherRules.Resolve((AgentProvider)999, null);
        resolve.Should().Throw<ArgumentOutOfRangeException>();
        foreach (var provider in AgentProviders.All)
        {
            var check = () => FakeLauncherRules.Check(provider, Temp + @"\x\s.ps1", PowerShell, Env);
            check.Should().NotThrow($"{provider} 要有明確的判斷");
            var none = () => FakeLauncherRules.Resolve(provider, null);
            none.Should().NotThrow();
            FakeLauncherRules.Resolve(provider, null).Should().BeNull("沒有 --fake-codex 時每一種都找不到 CLI");
        }
    }

    [Theory]
    [InlineData(@"C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData(Temp + @"\powershell.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"powershell.exe")]
    [InlineData(@"\\server\share\powershell.exe")]
    public void 不是System32的WindowsPowerShell_拒絕(string powershell)
    {
        FakeLauncherRules.Check(AgentProvider.Codex, Temp + @"\x\s.ps1", powershell, Env).Should().NotBeNull();
        FakeLauncherRules.Launcher(AgentProvider.Codex, Temp + @"\x\s.ps1", powershell, Env).Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\", "磁碟根目錄")]
    [InlineData(Profile, "包住了使用者資料夾")]
    [InlineData(@"C:\Users", "包住了使用者資料夾")]
    [InlineData(@"temp", "完整路徑")]
    public void TEMP本身不合理時_不交出(string temp, string reason)
    {
        var environment = Env with { TempRoot = temp };
        var script = Path.IsPathFullyQualified(temp) ? Path.Combine(temp, "x", "s.ps1") : Temp + @"\x\s.ps1";
        FakeLauncherRules.Check(AgentProvider.Codex, script, PowerShell, environment).Should().Contain(reason);
        FakeLauncherRules.Launcher(AgentProvider.Codex, script, PowerShell, environment).Should().BeNull();
    }

    [Fact]
    public void ResolveLauncher只把Codex交給假啟動器()
    {
        var fake = new AgentLauncher(PowerShell, FakeLauncherRules.PrefixArguments(Temp + @"\x\s.ps1"));
        FakeLauncherRules.Resolve(AgentProvider.Codex, fake).Should().BeSameAs(fake);
        FakeLauncherRules.Resolve(AgentProvider.Claude, fake).Should().BeNull();
        FakeLauncherRules.Resolve(AgentProvider.Antigravity, fake).Should().BeNull();
        FakeLauncherRules.Resolve(AgentProvider.Grok, fake).Should().BeNull();
        FakeLauncherRules.Resolve(AgentProvider.Codex, null).Should().BeNull();
    }

    [Fact]
    public void 剝掉PSModulePath_不分大小寫_其他照留()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PSModulePath"] = @"C:\Program Files\PowerShell\7\Modules",
            ["psmodulepath"] = "x",
            ["CODEX_HOME"] = @"C:\x",
            ["PATH"] = @"C:\Windows",
            ["PSModulePathX"] = "keep",
        };
        FakeLauncherRules.StripEnvironment(environment);
        environment.Keys.Should().BeEquivalentTo("CODEX_HOME", "PATH", "PSModulePathX");
        FakeLauncherRules.IsStrippedVariable("PSMODULEPATH").Should().BeTrue();
        FakeLauncherRules.IsStrippedVariable("PATH").Should().BeFalse();
        FakeLauncherRules.StrippedVariables.Should().Equal("PSModulePath");
    }

    [Fact]
    public void 資料根要在TEMP底下()
    {
        FakeLauncherRules.CheckDataRoot(Temp + @"\fd\home", Temp).Should().BeNull();
        FakeLauncherRules.CheckDataRoot(Temp, Temp).Should().Contain("TEMP", "TEMP 本身不算底下");
        FakeLauncherRules.CheckDataRoot(@"C:\fd\home", Temp).Should().Contain("logout");
        FakeLauncherRules.CheckDataRoot(Temp + @"\..\fd", Temp).Should().Contain("TEMP");
        FakeLauncherRules.CheckDataRoot(@"\\server\fd", Temp).Should().Contain("UNC");
        FakeLauncherRules.FolderOf(Temp + @"\fd\home").Should().Be(Temp + @"\fd\home\fake-codex");
    }

    [Fact]
    public void 這台電腦的環境_System32的PowerShell與TEMP()
    {
        FakeLauncherRules.SystemPowerShell.Should().BeEquivalentTo(CodexAgentSessionTests.PowerShell);
        var current = FakeLauncherEnvironment.Current();
        current.TempRoot.Should().Be(Path.GetTempPath());
        current.SystemPowerShell.Should().Be(FakeLauncherRules.SystemPowerShell);
        var script = Path.Combine(Path.GetTempPath(), "fake-launcher-rules", "s.ps1");
        FakeLauncherRules.Launcher(AgentProvider.Codex, script, CodexAgentSessionTests.PowerShell).Should().NotBeNull("這台的 TEMP 底下的腳本");
        FakeLauncherRules.Launcher(AgentProvider.Codex, Path.Combine(current.UserProfile, ".codex", "s.ps1"), CodexAgentSessionTests.PowerShell)
            .Should().BeNull("使用者真的 .codex 不在 TEMP 底下");
    }
}
