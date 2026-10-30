using MyriagentRemote;

namespace Myriagent.Tests.RemoteExec;

/// <summary>myriagent-remote 的參數與回應的轉換（src/MyriagentRemote；docs/sync-protocol.md 14.11）</summary>
public sealed class MyriagentRemoteCliTests
{
    [Fact]
    public void 參數_run的選項在電腦前後都可以_之後的是指令()
    {
        CliArgs.Parse(["run", "laptop", "--shell", "cmd", "--cwd", @"C:\work", "--timeout", "30", "--", "dir", "/b"])
            .Should().Be(new CliArgs("run", "laptop", "cmd", @"C:\work", 30, "dir /b", false, false, false, null));
        CliArgs.Parse(["run", "--shell", "PWSH", "laptop", "Get-Process", "--help"])
            .Should().Be(new CliArgs("run", "laptop", "pwsh", null, null, "Get-Process --help", false, false, false, null), "電腦之後第一個不是選項的開始就是指令");
        CliArgs.Parse(["run", "laptop", "--stdin"]).Should().Be(new CliArgs("run", "laptop", null, null, null, null, true, false, false, null));
        CliArgs.Parse(["list", "--json"]).Should().Be(new CliArgs("list", null, null, null, null, null, false, true, false, null));
        CliArgs.Parse(["--help"]).Help.Should().BeTrue();
    }

    [Theory]
    [InlineData(new string[0], "少了")]
    [InlineData(new[] { "exec" }, "不認得的操作")]
    [InlineData(new[] { "run" }, "少了要執行的電腦")]
    [InlineData(new[] { "run", "laptop" }, "少了要執行的指令")]
    [InlineData(new[] { "run", "laptop", "--shell", "bash", "ls" }, "--shell")]
    [InlineData(new[] { "run", "laptop", "--timeout", "0", "dir" }, "--timeout")]
    [InlineData(new[] { "run", "laptop", "--stdin", "--", "dir" }, "擇一")]
    [InlineData(new[] { "run", "--bogus", "laptop", "dir" }, "不認得的選項")]
    [InlineData(new[] { "list", "extra" }, "不認得參數")]
    public void 參數不對(string[] args, string error) => CliArgs.Parse(args).Error.Should().Contain(error);

    [Fact]
    public void 第一行的要求_app照同一份規則解析()
    {
        var request = CliArgs.Request(new CliArgs("run", "laptop", null, null, 30, "Get-Date", false, false, false, null), "conv", "tok");
        var o = Newtonsoft.Json.Linq.JObject.Parse(request);
        o.Value<int>("v").Should().Be(1);
        o.Value<string>("op").Should().Be("run");
        o.Value<string>("device").Should().Be("laptop");
        o.Value<int>("timeoutSec").Should().Be(30);
        o.ContainsKey("shell").Should().BeFalse("沒給殼層由那台用預設");
        Myriagent.RemoteExec.RemoteExecPipeRules.ParseRequest(request).Problem.Should().BeNull();
    }

    [Fact]
    public void 回應轉成輸出與結束碼()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var printer = new ReplyPrinter(stdout, stderr, json: false);
        printer.Handle("{\"t\":\"accepted\",\"device\":\"LAPTOP\"}").Should().BeNull();
        printer.Handle("{\"t\":\"status\",\"code\":\"started\",\"text\":\"開始執行\"}").Should().BeNull();
        printer.Handle("{\"t\":\"out\",\"text\":\"中文\"}").Should().BeNull();
        printer.Handle("{\"t\":\"err\",\"text\":\"警告\"}").Should().BeNull();
        printer.Handle("{\"t\":\"gap\"}").Should().BeNull();
        printer.Handle("{\"t\":\"exit\",\"exitCode\":3,\"code\":null}").Should().Be(3);
        stdout.ToString().Should().Be("中文");
        stderr.ToString().Should().Contain("LAPTOP").And.Contain("開始執行").And.Contain("警告").And.Contain("沒收到");

        printer.Handle("{\"t\":\"exit\",\"exitCode\":1,\"code\":\"timeout\"}").Should().Be(ExitCodes.Timeout);
        printer.Handle("{\"t\":\"exit\",\"exitCode\":null,\"code\":\"denied\",\"text\":\"拒絕\"}").Should().Be(ExitCodes.Denied);
        printer.Handle("{\"t\":\"exit\",\"exitCode\":1,\"code\":\"cancelled\"}").Should().Be(ExitCodes.Cancelled);
        printer.Handle("{\"t\":\"exit\",\"exitCode\":null,\"code\":\"failed\"}").Should().Be(ExitCodes.NotSent);
        printer.Handle("{\"t\":\"exit\",\"exitCode\":1,\"code\":\"orphaned\"}").Should().Be(ExitCodes.Unknown);
        printer.Handle("{\"t\":\"error\",\"code\":\"offline\",\"message\":\"不在線上\"}").Should().Be(ExitCodes.NotSent);
        printer.Handle("{\"t\":\"error\",\"code\":\"unknown\",\"message\":\"不明\"}").Should().Be(ExitCodes.Unknown);
        printer.Handle("not json").Should().BeNull();
    }

    [Fact]
    public void 列出電腦()
    {
        var stdout = new StringWriter();
        var printer = new ReplyPrinter(stdout, new StringWriter(), json: false);
        printer.Handle("{\"t\":\"devices\",\"self\":\"d_a\",\"live\":true,\"devices\":[" +
                       "{\"id\":\"d_a\",\"name\":\"DESK\",\"self\":true,\"online\":true,\"exec\":true,\"allowExec\":false}," +
                       "{\"id\":\"d_b\",\"name\":\"LAPTOP\",\"machineName\":\"LT-01\",\"self\":false,\"online\":true,\"exec\":true,\"allowExec\":true}," +
                       "{\"id\":\"d_c\",\"name\":\"OLD\",\"self\":false,\"online\":true,\"exec\":false,\"allowExec\":false}," +
                       "{\"id\":\"d_d\",\"name\":\"OFF\",\"self\":false,\"online\":false,\"exec\":true,\"allowExec\":true}]}").Should().Be(0);
        printer.Listed.Should().BeTrue();
        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        lines.Should().Equal("DESK\t這台\td_a", "LAPTOP（LT-01）\t在線，可以執行\td_b", "OLD\t在線，版本太舊（要先更新那台）\td_c", "OFF\t離線\td_d");
    }
}
