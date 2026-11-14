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

    [Fact]
    public void 對話之間互送訊息_conversations與send的參數()
    {
        CliArgs.Parse(["conversations"]).Should().Be(new CliArgs("conversations", null, null, null, null, null, false, false, false, null));
        CliArgs.Parse(["conversations", "PC-B", "--json"]).Should().Be(new CliArgs("conversations", "PC-B", null, null, null, null, false, true, false, null));
        var send = CliArgs.Parse(["send", "PC-B", "0123456789abcdef0123456789abcdef", "--", "報表", "好了"]);
        send.Error.Should().BeNull();
        send.Op.Should().Be("send");
        send.Device.Should().Be("PC-B");
        send.Target.Should().Be("0123456789abcdef0123456789abcdef");
        send.Command.Should().Be("報表 好了");
        var stdin = CliArgs.Parse(["send", "這台", "部署", "--stdin"]);
        stdin.ReadStdin.Should().BeTrue();
        stdin.Target.Should().Be("部署");

        var o = Newtonsoft.Json.Linq.JObject.Parse(CliArgs.Request(send, "conv", "tok"));
        o.Value<string>("op").Should().Be("send");
        o.Value<string>("target").Should().Be("0123456789abcdef0123456789abcdef");
        o.Value<string>("command").Should().Be("報表 好了");
        var parsed = Myriagent.RemoteExec.RemoteExecPipeRules.ParseRequest(o.ToString(Newtonsoft.Json.Formatting.None));
        parsed.Problem.Should().BeNull();
        parsed.Request!.Target.Should().Be("0123456789abcdef0123456789abcdef");
        Myriagent.RemoteExec.RemoteExecPipeRules.ParseRequest(CliArgs.Request(CliArgs.Parse(["conversations", "PC-B"]), "conv", "tok")).Request!.Device.Should().Be("PC-B");
    }

    [Theory]
    [InlineData(new[] { "send" }, "少了要送的電腦")]
    [InlineData(new[] { "send", "PC-B" }, "少了要送的對話")]
    [InlineData(new[] { "send", "PC-B", "部署" }, "少了要送的訊息")]
    [InlineData(new[] { "send", "PC-B", "部署", "--stdin", "--", "x" }, "擇一")]
    [InlineData(new[] { "send", "--bogus", "PC-B", "部署", "x" }, "不認得選項")]
    [InlineData(new[] { "conversations", "A", "B" }, "只收一台")]
    public void 對話之間互送訊息_參數不對(string[] args, string error) => CliArgs.Parse(args).Error.Should().Contain(error);

    [Fact]
    public void 對話之間互送訊息_回應的輸出()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var printer = new ReplyPrinter(stdout, stderr, json: false);
        printer.Handle("{\"t\":\"conversations\",\"paired\":true,\"items\":[{\"id\":\"abc\",\"title\":\"部署\",\"device\":\"PC-B\",\"self\":false,\"running\":true}]}").Should().Be(0);
        stdout.ToString().Should().Contain("PC-B\tabc\t執行中\t部署");
        printer.Handle("{\"t\":\"sent\",\"device\":\"PC-B\",\"conversation\":\"abc\",\"title\":\"部署\",\"queued\":true}").Should().Be(0);
        stdout.ToString().Should().Contain("已送到「PC-B」的對話「部署」（abc）").And.Contain("正在執行");
        printer.Handle("{\"t\":\"error\",\"code\":\"rejected\",\"message\":\"那台的設定不允許\"}").Should().Be(ExitCodes.NotSent);
        stderr.ToString().Should().Contain("那台的設定不允許");
    }

    [Fact]
    public void 傳檔案_push的路徑轉成完整路徑_pull沒給to是目前的資料夾()
    {
        var push = CliArgs.Parse(["push", "PC-B", "報表.txt", @"C:\data\相簿", "--to", @"D:\inbox"]);
        push.Error.Should().BeNull();
        push.Op.Should().Be("push");
        push.Device.Should().Be("PC-B");
        push.Paths.Should().Equal(Path.GetFullPath("報表.txt"), @"C:\data\相簿");
        push.To.Should().Be(@"D:\inbox");
        var pull = CliArgs.Parse(["pull", "PC-B", "--to", "out", @"D:\inbox\a.txt"]);
        pull.Paths.Should().Equal(@"D:\inbox\a.txt");
        pull.To.Should().Be(Path.GetFullPath("out"));
        CliArgs.Parse(["pull", "PC-B", @"D:\a.txt"]).To.Should().Be(Environment.CurrentDirectory);

        var o = Newtonsoft.Json.Linq.JObject.Parse(CliArgs.Request(push, "conv", "tok"));
        o["paths"]!.ToObject<string[]>().Should().Equal(push.Paths);
        var parsed = Myriagent.RemoteExec.RemoteExecPipeRules.ParseRequest(o.ToString(Newtonsoft.Json.Formatting.None));
        parsed.Problem.Should().BeNull();
        parsed.Request!.Paths.Should().Equal(push.Paths);
        parsed.Request.To.Should().Be(@"D:\inbox");
    }

    [Theory]
    [InlineData(new[] { "push" }, "少了另一台電腦")]
    [InlineData(new[] { "push", "PC-B", "a.txt" }, "--to")]
    [InlineData(new[] { "pull", "PC-B" }, "少了要傳回來")]
    [InlineData(new[] { "push", "PC-B", "--bogus" }, "不認得選項")]
    public void 傳檔案_參數不對(string[] args, string error) => CliArgs.Parse(args).Error.Should().Contain(error);

    [Fact]
    public void 傳檔案_結果的輸出與結束碼()
    {
        var stdout = new StringWriter();
        var printer = new ReplyPrinter(stdout, new StringWriter(), json: false);
        printer.Handle("{\"t\":\"files\",\"ok\":true,\"items\":[{\"source\":\"a.txt\",\"saved\":\"in/a.txt\",\"error\":null}]}").Should().Be(0);
        stdout.ToString().Should().Contain("完成：a.txt → in/a.txt");
        printer.Handle("{\"t\":\"files\",\"ok\":false,\"items\":[{\"source\":\"x\",\"saved\":null,\"error\":\"找不到\"}]}").Should().Be(1);
        stdout.ToString().Should().Contain("失敗：x：找不到");
    }
}
