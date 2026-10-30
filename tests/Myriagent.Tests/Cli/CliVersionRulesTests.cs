using Myriagent.Cli;

namespace Myriagent.Tests.Cli;

/// <summary>CLI 版本字串的解析與比較（兩種 CLI 的實際 --version 輸出、npm registry 回應）</summary>
public class CliVersionRulesTests
{
    [Theory]
    [InlineData("2.1.295 (Claude Code)\n", "2.1.295")]
    [InlineData("codex-cli 0.160.1\n", "0.160.1")]
    [InlineData("codex-cli 0.163.0-alpha.3", "0.163.0-alpha.3")]
    [InlineData("v20.11.1", "20.11.1")]
    [InlineData("版本 1.2", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseVersion_取第一個三段版本號(string? output, string? expected)
        => CliVersionRules.ParseVersion(output).Should().Be(expected);

    [Theory]
    [InlineData("0.162.0", "0.160.1", 1)]
    [InlineData("0.160.1", "0.162.0", -1)]
    [InlineData("2.1.295", "2.1.295", 0)]
    [InlineData("2.1.300", "2.1.295", 1)]
    [InlineData("10.0.0", "9.99.99", 1)]
    [InlineData("1.0.0", "1.0.0-beta", 1)]
    [InlineData("1.0.0-beta", "1.0.0", -1)]
    [InlineData("1.0.0-beta", "1.0.0-alpha", 1)]
    [InlineData("1.0.1-alpha", "1.0.0", 1)]
    [InlineData("codex-cli 0.162.0", "0.160.1", 1)]
    [InlineData(null, "0.0.1", -1)]
    [InlineData("看不懂", null, 0)]
    public void Compare_數字逐段比較_正式版大於同號預發布版(string? a, string? b, int expectedSign)
        => Math.Sign(CliVersionRules.Compare(a, b)).Should().Be(expectedSign);

    [Theory]
    [InlineData("0.162.0", "0.160.1", true)]
    [InlineData("2.1.295", "2.1.295", false)]
    [InlineData("2.1.294", "2.1.295", false)]
    [InlineData("0.163.0", "0.163.0-alpha.3", true)]
    [InlineData(null, "0.160.1", false)]
    [InlineData("0.162.0", null, false)]
    [InlineData("最新", "0.160.1", false)]
    public void IsNewer_最新版比目前新才提示更新(string? latest, string? current, bool expected)
        => CliVersionRules.IsNewer(latest, current).Should().Be(expected);

    /// <summary>研究實測：initialize 回應 {"userAgent":"cli-update-probe/0.160.1 (Windows 10.0.26300; x86_64) …"}，斜線後是 Codex 版本、不是 clientInfo 的版本</summary>
    [Theory]
    [InlineData("cli-update-probe/0.160.1 (Windows 10.0.26300; x86_64) WindowsTerminal", "0.160.1")]
    [InlineData("myriagent/0.162.0 (Windows 10.0.26300; x86_64)", "0.162.0")]
    [InlineData("myriagent/0.162.0-alpha.2 (Windows 10.0.26300; x86_64)", "0.162.0-alpha.2")]
    [InlineData("  myriagent/0.163.1  ", "0.163.1")]
    [InlineData("myriagent/0.163.1", "0.163.1")]
    [InlineData("myriagent/0.163.1;x", "0.163.1")]
    [InlineData("myriagent/0.16", null)]
    [InlineData("myriagent/0.163.1x (Windows)", null)]
    [InlineData("Windows 10.0.26300 myriagent/0.1.2", null)]
    [InlineData("0.160.1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseCodexUserAgent_取開頭名稱斜線後的版本(string? userAgent, string? expected)
        => CliVersionRules.ParseCodexUserAgent(userAgent).Should().Be(expected);

    /// <summary>研究實測（04-version-sources.ps1）：claude.exe 的 FileVersion 是四段「2.1.295.0」，--version 是「2.1.295 (Claude Code)」</summary>
    [Theory]
    [InlineData("2.1.295.0", "2.1.295")]
    [InlineData("2.1.296.0", "2.1.296")]
    [InlineData("2.1.300-beta.1", "2.1.300")]
    [InlineData("codex-cli 0.160.1", "0.160.1")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CoreVersion_只留xyz(string? version, string? expected)
        => CliVersionRules.CoreVersion(version).Should().Be(expected);

    [Theory]
    [InlineData("2.1.295.0", "2.1.295 (Claude Code)\n", true, true)]
    [InlineData("2.1.296.0", "2.1.295 (Claude Code)", true, false)]
    [InlineData("2.1.300.0", "2.1.300-beta.1 (Claude Code)", true, true)]
    [InlineData("0.160.1", "codex-cli 0.160.1\n", false, true)]
    [InlineData("0.162.0", "codex-cli 0.160.1", false, false)]
    [InlineData("0.163.0-alpha.3", "codex-cli 0.163.0-ALPHA.3", false, true)]
    [InlineData("0.163.0-alpha.3", "codex-cli 0.163.0", false, false)]
    [InlineData(null, "0.160.1", false, false)]
    [InlineData("看不懂", "看不懂", false, false)]
    public void SameRelease_檔案版本資訊只比xyz_其他連預發布字尾一起比(string? a, string? b, bool coreOnly, bool expected)
        => CliVersionRules.SameRelease(a, b, coreOnly).Should().Be(expected);

    /// <summary>這台電腦的 npm 版 Codex 0.160.1：package.json、平台套件 package.json、vendor\x86_64-pc-windows-msvc\codex-package.json 原文</summary>
    [Fact]
    public void ParsePackageJsonVersion_讀套件與VendorCodexPackageJson_平台套件去掉平台字尾()
    {
        const string package = """
            {
              "name": "@openai/codex",
              "version": "0.160.1",
              "description": "Codex CLI is a coding agent from OpenAI that runs locally on your computer.",
              "license": "Apache-2.0",
              "bin": {
                "codex": "bin/codex.js"
              },
              "type": "module",
              "optionalDependencies": {
                "@openai/codex-win32-x64": "npm:@openai/codex@0.160.1-win32-x64"
              }
            }
            """;
        const string platform = """
            {
              "name": "@openai/codex",
              "version": "0.160.1-win32-x64",
              "license": "Apache-2.0",
              "os": [
                "win32"
              ],
              "cpu": [
                "x64"
              ]
            }
            """;
        const string vendor = """
            {
              "layoutVersion": 1,
              "version": "0.160.1",
              "target": "x86_64-pc-windows-msvc",
              "variant": "codex",
              "entrypoint": "bin/codex.exe",
              "resourcesDir": "codex-resources",
              "pathDir": "codex-path"
            }
            """;

        CliVersionRules.ParsePackageJsonVersion(package).Should().Be("0.160.1");
        CliVersionRules.ParsePackageJsonVersion(vendor).Should().Be("0.160.1");
        CliVersionRules.ParsePackageJsonVersion(platform, platformPackage: true).Should().Be("0.160.1", "平台套件的「-win32-x64」不是預發布字尾");
        CliVersionRules.ParsePackageJsonVersion("""{"version": 1}""").Should().BeNull();
        CliVersionRules.ParsePackageJsonVersion("[]").Should().BeNull();
        CliVersionRules.ParsePackageJsonVersion("{").Should().BeNull();
        CliVersionRules.ParsePackageJsonVersion(null).Should().BeNull();
    }

    [Theory]
    [InlineData("0.160.1-win32-x64", "0.160.1")]
    [InlineData("0.162.0-win32-arm64", "0.162.0")]
    [InlineData("0.162.0-linux-x64-musl", "0.162.0")]
    [InlineData("0.163.0-alpha.3", "0.163.0-alpha.3")]
    [InlineData("0.163.0-alpha.3-win32-x64", "0.163.0-alpha.3")]
    [InlineData(" 0.160.1 ", "0.160.1")]
    [InlineData("win32-x64", null)]
    [InlineData(null, null)]
    public void StripPlatformSuffix_只去掉平台字尾(string? version, string? expected)
        => CliVersionRules.StripPlatformSuffix(version).Should().Be(expected);

    [Fact]
    public void ParseNpmLatest_讀Registry回應的Version()
    {
        CliVersionRules.ParseNpmLatest("""{"name":"@openai/codex","version":"0.162.0","bin":{"codex":"bin/codex.js"}}""").Should().Be("0.162.0");
        CliVersionRules.ParseNpmLatest("""{"name":"@anthropic-ai/claude-code","version":"2.1.296"}""").Should().Be("2.1.296");
        CliVersionRules.ParseNpmLatest("""{"name":"x"}""").Should().BeNull();
        CliVersionRules.ParseNpmLatest("""{"version":"latest"}""").Should().BeNull();
        CliVersionRules.ParseNpmLatest("<html>503</html>").Should().BeNull();
        CliVersionRules.ParseNpmLatest("[]").Should().BeNull();
        CliVersionRules.ParseNpmLatest(" ").Should().BeNull();
        CliVersionRules.ParseNpmLatest(null).Should().BeNull();
    }
}
