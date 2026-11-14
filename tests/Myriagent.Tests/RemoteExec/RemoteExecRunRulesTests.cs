using System.Text;
using Myriagent.Remote;
using Myriagent.RemoteExec;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.RemoteExec;

/// <summary>被操作的電腦解析與組殼層、發起的電腦的管道協定（docs/sync-protocol.md 14.11）</summary>
public sealed class RemoteExecRunRulesTests
{
    private const string DeviceB = "d_01J9QZ3K8M5R7T2V4W6X8Y0A1B";

    [Fact]
    public void 解析閘門交來的指令()
    {
        var request = RemoteExecRunRules.Parse(new JObject
        {
            ["cmd"] = RemoteCommandCatalog.RemoteExec,
            ["deviceId"] = DeviceB,
            ["shell"] = "cmd",
            ["command"] = "echo hi",
            ["cwd"] = @" C:\work ",
            ["timeoutSec"] = 30,
            ["label"] = "整理",
        });
        request.Should().Be(new RemoteExecRequest("cmd", "echo hi", @"C:\work", 30, "整理"));

        RemoteExecRunRules.Parse(new JObject { ["shell"] = "powershell", ["command"] = "Get-Date" })
            .Should().Be(new RemoteExecRequest("powershell", "Get-Date", null, RemoteExecRules.DefaultTimeoutSeconds, null));
        RemoteExecRunRules.Parse(new JObject { ["shell"] = "bash", ["command"] = "ls" }).Should().BeNull();
        RemoteExecRunRules.Parse(new JObject { ["shell"] = "cmd", ["command"] = "  " }).Should().BeNull();
    }

    [Fact]
    public void 工作資料夾_網路路徑與相對路徑不收()
    {
        RemoteExecRunRules.CheckFolder(null, _ => false).Should().BeNull("沒給＝家目錄");
        RemoteExecRunRules.CheckFolder(@"C:\work", _ => true).Should().BeNull();
        RemoteExecRunRules.CheckFolder(@"\\server\share", _ => true).Should().Contain("網路路徑");
        RemoteExecRunRules.CheckFolder("//server/share", _ => true).Should().Contain("網路路徑");
        RemoteExecRunRules.CheckFolder("work", _ => true).Should().Contain("完整的路徑");
        RemoteExecRunRules.CheckFolder(@"C:\nope", _ => false).Should().Contain("沒有資料夾");
        var checkedUnc = false;
        RemoteExecRunRules.CheckFolder(@"\\server\share", _ => checkedUnc = true);
        checkedUnc.Should().BeFalse("UNC 不檢查存在（會對外送 SMB 雜湊）");
    }

    [Fact]
    public void PowerShell的腳本_UTF8輸出_結束碼對齊最後一個指令()
    {
        var script = RemoteExecRunRules.PowerShellScript("Get-Date\nexit 3");
        script.Should().StartWith("$ProgressPreference = 'SilentlyContinue'\r\n");
        script.Should().Contain("[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)");
        script.Should().Contain("Get-Date\r\nexit 3\r\n");
        script.Should().EndWith("exit 0\r\n");

        var launch = RemoteExecRunRules.Launch(new RemoteExecRequest("powershell", "Get-Date", null, 60, null), "CID", @"C:\ps.exe", null, @"C:\cmd.exe", @"C:\tmp")!;
        launch.FileName.Should().Be(@"C:\ps.exe");
        launch.ScriptPath.Should().Be(@"C:\tmp\CID.ps1");
        launch.Arguments.Should().Equal("-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", @"C:\tmp\CID.ps1");
        RemoteExecRunRules.Launch(new RemoteExecRequest("pwsh", "Get-Date", null, 60, null), "CID", @"C:\ps.exe", null, @"C:\cmd.exe", @"C:\tmp")
            .Should().BeNull("這台沒有 pwsh");
    }

    [Fact]
    public void cmd的命令列_多行接成一行_原樣傳()
    {
        RemoteExecRunRules.CmdArguments("echo a\r\n\r\n  echo \"b c\"  \n").Should().Be("/d /s /c \"chcp 65001>nul & echo a & echo \"b c\"\"");
        var launch = RemoteExecRunRules.Launch(new RemoteExecRequest("cmd", "dir", null, 60, null), "CID", @"C:\ps.exe", null, @"C:\cmd.exe", @"C:\tmp")!;
        launch.FileName.Should().Be(@"C:\cmd.exe");
        launch.RawArguments.Should().Be("/d /s /c \"chcp 65001>nul & dir\"");
        launch.Arguments.Should().BeEmpty();
        launch.Script.Should().BeNull();
    }

    [Fact]
    public void 不交給遠端指令的環境變數()
    {
        RemoteExecRunRules.IsStrippedVariable("MYRIAGENT_REMOTE_PIPE").Should().BeTrue();
        RemoteExecRunRules.IsStrippedVariable("myriagent_remote_token").Should().BeTrue();
        RemoteExecRunRules.IsStrippedVariable("MYRIAGENT_AGENT_CONVERSATION").Should().BeTrue();
        RemoteExecRunRules.IsStrippedVariable("PATH").Should().BeFalse();
    }

    [Fact]
    public void 輸出的解碼_切在字元中間的留到下一次_不合格就改用主控台字碼頁()
    {
        var decoder = new RemoteExecOutputDecoder(Encoding.Latin1);
        var bytes = Encoding.UTF8.GetBytes("中文");
        decoder.Decode(bytes.AsSpan(0, 2)).Should().BeEmpty();
        decoder.Decode(bytes.AsSpan(2)).Should().Be("中文");
        decoder.UsingFallback.Should().BeFalse();

        var legacy = new RemoteExecOutputDecoder(Encoding.Latin1);
        legacy.Decode([0x41, 0xE9, 0x42]).Should().Be("AéB", "0xE9 0x42 不是合格的 UTF-8");
        legacy.UsingFallback.Should().BeTrue();
        legacy.Decode([0xE9]).Should().Be("é");
    }

    // ───────────────────────────── 管道 ─────────────────────────────

    [Fact]
    public void 管道名稱依資料根_權杖依對話()
    {
        var a = RemoteExecPipeRules.PipeName(@"C:\Users\me\AppData\Local\Myriagent");
        a.Should().StartWith("myriagent-agent-remote-").And.HaveLength("myriagent-agent-remote-".Length + 16);
        RemoteExecPipeRules.PipeName(@"c:\users\ME\AppData\Local\Myriagent\").Should().Be(a, "不分大小寫、結尾的斜線不算");
        RemoteExecPipeRules.PipeName(@"C:\temp\other").Should().NotBe(a);

        var secret = new byte[32];
        var token = RemoteExecPipeRules.Token(secret, "conv-1");
        RemoteExecPipeRules.VerifyToken(secret, "conv-1", token).Should().BeTrue();
        RemoteExecPipeRules.VerifyToken(secret, "conv-2", token).Should().BeFalse();
        RemoteExecPipeRules.VerifyToken(new byte[] { 1 }, "conv-1", token).Should().BeFalse();
        RemoteExecPipeRules.VerifyToken(secret, "conv-1", "").Should().BeFalse();
    }

    [Fact]
    public void 解析myriagent_remote的要求()
    {
        var (run, problem) = RemoteExecPipeRules.ParseRequest(
            "{\"v\":1,\"op\":\"run\",\"conversation\":\"c\",\"token\":\"t\",\"device\":\"pc\",\"command\":\"dir\",\"shell\":\"cmd\",\"timeoutSec\":5}");
        problem.Should().BeNull();
        run.Should().Be(new RemoteExecPipeRequest("run", "c", "t", "pc", "cmd", null, 5, "dir"));

        RemoteExecPipeRules.ParseRequest("{\"v\":1,\"op\":\"list\",\"conversation\":\"c\",\"token\":\"t\"}").Request!.Op.Should().Be("list");
        RemoteExecPipeRules.ParseRequest(null).Problem.Should().NotBeNull();
        RemoteExecPipeRules.ParseRequest("nope").Problem.Should().NotBeNull();
        RemoteExecPipeRules.ParseRequest("{\"v\":2,\"op\":\"list\",\"conversation\":\"c\",\"token\":\"t\"}").Problem.Should().Contain("版本");
        RemoteExecPipeRules.ParseRequest("{\"v\":1,\"op\":\"list\",\"conversation\":\"c\"}").Problem.Should().Contain("權杖");
        RemoteExecPipeRules.ParseRequest("{\"v\":1,\"op\":\"run\",\"conversation\":\"c\",\"token\":\"t\",\"command\":\"dir\"}").Problem.Should().Contain("電腦");
        RemoteExecPipeRules.ParseRequest("{\"v\":1,\"op\":\"run\",\"conversation\":\"c\",\"token\":\"t\",\"device\":\"pc\"}").Problem.Should().Contain("指令");
        RemoteExecPipeRules.ParseRequest("{\"v\":1,\"op\":\"run\",\"conversation\":\"c\",\"token\":\"t\",\"device\":\"pc\",\"command\":\"x\",\"shell\":\"bash\"}")
            .Problem.Should().Contain("--shell");
        RemoteExecPipeRules.ParseRequest("{\"v\":1,\"op\":\"run\",\"conversation\":\"c\",\"token\":\"t\",\"device\":\"pc\",\"command\":\"x\",\"timeoutSec\":0}")
            .Problem.Should().Contain("--timeout");
    }

    [Fact]
    public void 找目標電腦_id_名稱_電腦名稱_唯一的開頭()
    {
        var devices = new[]
        {
            Device("d_01J9QZ3K8M5R7T2V4W6X8Y0A1A", "Office", machine: "OFFICE-PC"),
            Device("d_01J9QZ3K8M5R7T2V4W6X8Y0A1B", "Laptop"),
            Device("d_01J9QZ3K8M5R7T2V4W6X8Y0A1C", "Lab-1"),
            Device("d_01J9QZ3K8M5R7T2V4W6X8Y0A1D", "Lab-2"),
            Device("d_01J9QZ3K8M5R7T2V4W6X8Y0A1E", "Old", revoked: true),
        };
        RemoteExecPipeRules.Resolve(devices, "d_01J9QZ3K8M5R7T2V4W6X8Y0A1B").Device!.Name.Should().Be("Laptop");
        RemoteExecPipeRules.Resolve(devices, "laptop").Device!.Name.Should().Be("Laptop");
        RemoteExecPipeRules.Resolve(devices, "office-pc").Device!.Name.Should().Be("Office");
        RemoteExecPipeRules.Resolve(devices, "Off").Device!.Name.Should().Be("Office");
        RemoteExecPipeRules.Resolve(devices, "Lab").Problem.Should().Contain("Lab-1").And.Contain("Lab-2");
        RemoteExecPipeRules.Resolve(devices, "Old").Problem.Should().Contain("找不到", "撤銷的不算");
        RemoteExecPipeRules.Resolve(devices, "zzz").Problem.Should().Contain("找不到");
    }

    [Fact]
    public void 找目標電腦_同名的不只一台_只有一台在線上就是它()
    {
        // 同一台電腦重新配對之後，舊的那一筆還在名冊裡（離線、名稱一樣）
        var devices = new[]
        {
            Device("d_01J9QZ3K8M5R7T2V4W6X8Y0A2A", "Shop", machine: "SHOP-PC") with { Online = false },
            Device("d_01J9QZ3K8M5R7T2V4W6X8Y0A2B", "Shop", machine: "SHOP-PC"),
        };
        RemoteExecPipeRules.Resolve(devices, "shop").Device!.Id.Should().Be("d_01J9QZ3K8M5R7T2V4W6X8Y0A2B");
        RemoteExecPipeRules.Resolve(devices, "SHOP-PC").Device!.Id.Should().Be("d_01J9QZ3K8M5R7T2V4W6X8Y0A2B");
        RemoteExecPipeRules.Resolve(devices, "d_01J9QZ3K8M5R7T2V4W6X8Y0A2A").Device!.Online.Should().BeFalse("指定 id 照樣找得到舊的那一筆");

        // 兩台都在線上、或都不在：分不出來，請改用 id
        var bothOnline = devices.Select(d => d with { Online = true }).ToArray();
        RemoteExecPipeRules.Resolve(bothOnline, "shop").Problem.Should().Contain("2 台").And.Contain("id");
        var bothOffline = devices.Select(d => d with { Online = false }).ToArray();
        RemoteExecPipeRules.Resolve(bothOffline, "shop").Problem.Should().Contain("2 台");
    }

    [Fact]
    public void 送出之前的檢查()
    {
        RemoteExecPipeRules.Precheck(Device(DeviceB, "B")).Should().BeNull();
        RemoteExecPipeRules.Precheck(Device(DeviceB, "B") with { Self = true }).Should().Contain("這台電腦");
        RemoteExecPipeRules.Precheck(Device(DeviceB, "B") with { Online = false }).Should().Contain("不在線上");
        RemoteExecPipeRules.Precheck(Device(DeviceB, "B") with { SupportsExec = false }).Should().Contain("版本太舊");
        RemoteExecPipeRules.Precheck(Device(DeviceB, "B") with { AllowRemoteExec = false }).Should().Contain("允許其他電腦的代理在這台執行指令");
    }

    [Fact]
    public void 送給伺服器的指令通過型錄的形狀檢查()
    {
        var request = new RemoteExecPipeRequest("run", "c", "t", "B", null, @"C:\work", 30, "Get-Date");
        var cmd = RemoteExecPipeRules.Command(Device(DeviceB, "B"), request, "整理磁碟\u0007" + new string('長', 200));
        RemoteCommandShapeRules.Check(cmd).Should().BeNull();
        cmd.Value<string>("shell").Should().Be("powershell", "沒給殼層用 powershell");
        cmd.Value<string>("label")!.Length.Should().Be(RemoteExecRules.MaxLabelLength);
        cmd.Value<string>("label").Should().NotContain("\u0007");

        var bare = RemoteExecPipeRules.Command(Device(DeviceB, "B"), new RemoteExecPipeRequest("run", "c", "t", "B", "cmd", null, null, "dir"), null);
        bare.Properties().Select(p => p.Name).Should().Equal("cmd", "deviceId", "shell", "command");
        RemoteCommandShapeRules.Check(bare).Should().BeNull();
    }

    [Fact]
    public void 段落轉成一行()
    {
        JObject.Parse(RemoteExecPipeRules.Chunk(new ExecChunk("C", 0, RemoteExecRules.KindOut, "hi"))).Should()
            .BeEquivalentTo(new JObject { ["t"] = "out", ["text"] = "hi" });
        JObject.Parse(RemoteExecPipeRules.Chunk(new ExecChunk("C", 3, RemoteExecRules.KindExit, null, 2))).Should()
            .BeEquivalentTo(new JObject { ["t"] = "exit", ["exitCode"] = 2, ["code"] = null, ["text"] = null });
        JObject.Parse(RemoteExecPipeRules.Chunk(new ExecChunk("C", 1, RemoteExecRules.KindStatus, "等", null, RemoteExecRules.StatusConfirm))).Should()
            .BeEquivalentTo(new JObject { ["t"] = "status", ["code"] = "confirm", ["text"] = "等" });
    }

    private static RemoteExecDevice Device(string id, string name, string? machine = null, bool revoked = false) =>
        new(id, name, machine, Self: false, Online: true, revoked, SupportsExec: true, "Control", AllowRemoteExec: true);
}
