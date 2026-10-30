using System.Security.Cryptography;
using System.Text;
using Myriagent.Tests.Agents;
using Myriagent.Tests.TestSupport;
using FakeDevice;

namespace Myriagent.Tests.FakeDevice;

/// <summary>
/// 假 Codex app-server 的腳本產生器（WP4.9 計畫步 13，D11）：從測試的 FakeCodexAppServer 搬到 tools/fake-device 之後，每個情境（與幾組參數）
/// 產生的腳本 SHA-256 等於搬移前記下的值（搬移前以舊的 FakeCodexAppServer 寫出的檔記下，證明是同一份）；測試的 FakeCodexAppServer 寫出的檔與啟動器照舊
/// </summary>
public sealed class FakeCodexAppServerScriptTests
{
    /// <summary>
    /// 搬移前（feat/p4-e2e 3970ffe 的 tests/Myriagent.Tests/TestSupport/FakeCodexAppServer.cs）以 <c>new FakeCodexAppServer(…)</c> 寫出的腳本：
    /// 名稱、參數（情境、email、startDelayMs、logoutExitCode、logoutDelayMs、readDelayMs）、檔名、位元組數、SHA-256
    /// </summary>
    public static TheoryData<string, string, string, int, int, int, int, string, int, string> PreMove => new()
    {
        { "ok", "ok", FakeCodexAppServerScript.Email, 0, 0, 0, 0, "fake-codex-ok.ps1", 5911, "1847e3945b49c3b1edf98132f54d485f5c0a5efb50d9873fd2d7c5ce41c315f3" },
        { "evil_host", "evil_host", FakeCodexAppServerScript.Email, 0, 0, 0, 0, "fake-codex-evil_host.ps1", 5918, "ad00d708ef9d839d1df1e61d9f86ff87a87928ce4a86da932b93d0bc3a2a2110" },
        { "http", "http", FakeCodexAppServerScript.Email, 0, 0, 0, 0, "fake-codex-http.ps1", 5913, "66a43d3bff590d4f4acce08fe509a08f905a31654cce01019c51cb5247bc1af8" },
        { "missing_fields", "missing_fields", FakeCodexAppServerScript.Email, 0, 0, 0, 0, "fake-codex-missing_fields.ps1", 5923, "3394f3013dfde6e12e04f5086c3116af070b0f5129313c1b93e6b71cb2d7818d" },
        { "unsupported", "unsupported", FakeCodexAppServerScript.Email, 0, 0, 0, 0, "fake-codex-unsupported.ps1", 5920, "7627fd0c8e02003a5ab1a233b298e6a22905beb2803b8d32e00c8a6412de0995" },
        { "complete_fail", "complete_fail", FakeCodexAppServerScript.Email, 0, 0, 0, 0, "fake-codex-complete_fail.ps1", 5922, "d0c3453acb97ba1a0c4b68909370e64d9cc5f6a601f5e46de6ccd7f8b61ef291" },
        { "init_fail", "init_fail", FakeCodexAppServerScript.Email, 0, 0, 0, 0, "fake-codex-init_fail.ps1", 5918, "1ce82eb7927b98d318e152ae8cc4eea40a5938fac6d11687d3c1318e9065727f" },
        { "ok+wrong", "ok", "wrong@example.test", 0, 0, 0, 0, "fake-codex-ok.ps1", 5910, "76ca6a37bb750c32bc5e4426baa5ad4029354c6e72c75ae2aba8b37683eba5d2" },
        { "ok+start5000", "ok", FakeCodexAppServerScript.Email, 5000, 0, 0, 0, "fake-codex-ok.ps1", 5914, "bde9e2b7405ca3d8ef44039ad0a5f5148e049843dfb26a1613fad904a5f19397" },
        { "ok+logout1+delay5000", "ok", FakeCodexAppServerScript.Email, 0, 1, 5000, 0, "fake-codex-ok.ps1", 5914, "bc7781c3b3a9d5b8170c440ca653d40df49127dbc02677b9ae10c166bf9c3f7b" },
        { "ok+read2000", "ok", FakeCodexAppServerScript.Email, 0, 0, 0, 2000, "fake-codex-ok.ps1", 5914, "640f461368934feee57cc12c4cf3c367248e11d28629f898436697ba46817d38" },
        { "ok+all", "ok", "x@example.test", 1, 2, 3, 4, "fake-codex-ok.ps1", 5906, "b1ef3812e6c2e1dbfdcce64966b1c9d6c8891ad575833ff3bdd3f45fc4b5b429" },
    };

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Theory]
    [MemberData(nameof(PreMove))]
    public void 每個情境的腳本雜湊等於搬移前(string name, string scenario, string email, int start, int logoutExit, int logoutDelay, int read, string fileName, int length,
        string sha256)
    {
        var bytes = new UTF8Encoding(false).GetBytes(FakeCodexAppServerScript.Build(scenario, email, start, logoutExit, logoutDelay, read));
        FakeCodexAppServerScript.FileName(scenario).Should().Be(fileName, name);
        bytes.Length.Should().Be(length, name);
        Sha256(bytes).Should().Be(sha256, $"{name} 的腳本要和搬移前逐位元組相同");
    }

    [Theory]
    [MemberData(nameof(PreMove))]
    public void 測試的FakeCodexAppServer寫出同一份檔_啟動器照舊(string name, string scenario, string email, int start, int logoutExit, int logoutDelay, int read,
        string fileName, int length, string sha256)
    {
        using var fake = new FakeCodexAppServer(scenario, email, start, logoutExit, logoutDelay, read);
        var script = Path.Combine(fake.Root, fileName);
        var bytes = File.ReadAllBytes(script);
        bytes.Length.Should().Be(length, name);
        Sha256(bytes).Should().Be(sha256, name);
        Directory.GetFiles(fake.Root).Should().Equal(new[] { script }, "只寫腳本，calls.txt 等第一次啟動才有");
        fake.Launcher.FileName.Should().Be(CodexAgentSessionTests.PowerShell);
        fake.Launcher.PrefixArguments.Should().Equal("-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script);
        fake.Started.Should().BeFalse();
        fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public void 情境清單_腳本只有ASCII_換行是LF()
    {
        FakeCodexAppServerScript.Scenarios.Should().Equal("ok", "evil_host", "http", "missing_fields", "unsupported", "complete_fail", "init_fail");
        foreach (var scenario in FakeCodexAppServerScript.Scenarios)
        {
            var text = FakeCodexAppServerScript.Build(scenario);
            text.Should().StartWith($"$scenario = '{scenario}'\n$userCode = 'QWER-TY12Z'\n");
            text.All(c => c is '\n' or (>= ' ' and <= '~')).Should().BeTrue($"{scenario}：Windows PowerShell 讀沒有 BOM 的腳本用系統碼頁，只能有 ASCII（沒有 CR、tab）");
            FakeCodexAppServerScript.CheckScenarioName(scenario).Should().BeNull();
        }
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("ok'; Remove-Item C:\\x; '")]
    [InlineData("")]
    [InlineData("bogus")]
    public void 不認得的情境被拒(string scenario)
    {
        FakeCodexAppServerScript.CheckScenarioName(scenario).Should().Contain("情境要是");
        FakeCodexAppServerScript.CheckScenarioName(null).Should().NotBeNull();
        var build = () => FakeCodexAppServerScript.Build(scenario);
        build.Should().Throw<ArgumentException>();
        var name = () => FakeCodexAppServerScript.FileName(scenario);
        name.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("a'b@example.test")]
    [InlineData("a b@example.test")]
    [InlineData("$(x)@example.test")]
    [InlineData("")]
    [InlineData("中文@example.test")]
    public void 不安全的email被拒(string email)
    {
        var build = () => FakeCodexAppServerScript.Build("ok", email);
        build.Should().Throw<ArgumentException>().WithMessage("*email*");
    }

    [Fact]
    public void 延遲不能是負數()
    {
        ((Action)(() => FakeCodexAppServerScript.Build("ok", startDelayMs: -1))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => FakeCodexAppServerScript.Build("ok", logoutDelayMs: -1))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => FakeCodexAppServerScript.Build("ok", readDelayMs: -1))).Should().Throw<ArgumentOutOfRangeException>();
        FakeCodexAppServerScript.Build("ok", logoutExitCode: -1).Should().Contain("$logoutExit = -1\n", "結束碼可以是負數（照不變文化格式化）");
    }

    [Fact]
    public void 共用的檔案輔助_呼叫紀錄與完成旗標()
    {
        using var temp = new TempDirectory("fake-codex-files");
        FakeCodexFiles.ReadCalls(temp.Root).Should().BeEmpty("還沒有 calls.txt");
        File.WriteAllText(Path.Combine(temp.Root, FakeCodexAppServerScript.CallsFile), "args=app-server\r\ncodexHome=C:\\x\ncodexHome=\n\nlogout\n");
        var calls = FakeCodexFiles.ReadCalls(temp.Root);
        calls.Should().Equal("args=app-server", "codexHome=C:\\x", "codexHome=", "logout");
        FakeCodexFiles.CodexHomes(calls).Should().Equal("C:\\x", "");
        FakeCodexFiles.WriteCompleteFlag(temp.Root);
        File.ReadAllText(Path.Combine(temp.Root, FakeCodexAppServerScript.CompleteFlagFile)).Should().BeEmpty();
    }
}
