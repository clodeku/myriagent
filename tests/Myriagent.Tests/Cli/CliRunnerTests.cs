using Myriagent.Agents;
using Myriagent.Cli;

namespace Myriagent.Tests.Cli;

/// <summary>CliRunner（真的子程序 cmd.exe）：回傳 PID（判斷換版是不是 app 自己觸發的）、啟動當下就回報、起不來沒有 PID</summary>
public sealed class CliRunnerTests
{
    private static AgentLauncher Cmd => new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Array.Empty<string>());

    [Fact]
    public async Task RunAsync_回傳PID_啟動當下就回報_與結果的PID相同()
    {
        int? reported = null;
        bool reportedBeforeExit = false;

        var result = await CliRunner.RunAsync(Cmd, new[] { "/d", "/c", "echo hello & exit /b 3" }, null, TimeSpan.FromSeconds(30),
            killWithApp: false, started: pid =>
            {
                reported = pid;
                // 回報時程序還在（讀輸出之前）：更新指令執行期間就能把它的暫存檔名、版本鎖認成 app 自己的
                reportedBeforeExit = true;
            });

        result.ExitCode.Should().Be(3);
        result.Stdout.Trim().Should().Be("hello");
        result.ProcessId.Should().NotBeNull().And.Be(reported);
        reportedBeforeExit.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_起不來就沒有PID_也不回報()
    {
        bool reported = false;
        var result = await CliRunner.RunAsync(new AgentLauncher(@"C:\不存在\claude.exe", Array.Empty<string>()), new[] { "--version" }, null, TimeSpan.FromSeconds(5),
            started: _ => reported = true);

        result.ExitCode.Should().Be(-1);
        result.ProcessId.Should().BeNull();
        reported.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_input寫進stdin_UTF8不帶BOM_寫完就關()
    {
        // findstr "^" 原樣印出 stdin 的每一行，讀到結尾（stdin 關了）才結束
        var result = await CliRunner.RunAsync(new AgentLauncher(Path.Combine(Environment.SystemDirectory, "findstr.exe"), Array.Empty<string>()),
            new[] { "^" }, null, TimeSpan.FromSeconds(30), killWithApp: false, input: "abc\r\ndef\n");

        result.TimedOut.Should().BeFalse("stdin 寫完就關，程序讀到結尾");
        result.ExitCode.Should().Be(0);
        result.Stdout.Replace("\r", string.Empty).Should().Be("abc\ndef\n", "沒有 BOM");
    }

    [Fact]
    public async Task RunAsync_回報PID的處理常式丟例外不影響執行()
    {
        var result = await CliRunner.RunAsync(Cmd, new[] { "/d", "/c", "exit /b 0" }, null, TimeSpan.FromSeconds(30), started: _ => throw new InvalidOperationException("測試"));

        result.ExitCode.Should().Be(0);
        result.ProcessId.Should().NotBeNull();
    }
}
