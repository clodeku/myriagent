using Myriagent.Background;

namespace Myriagent.Tests.Background;

/// <summary>
/// 對話開出的背景程式（BackgroundProgramRules）：帶標記、主控台程式、不在 CLI 程序底下的才算，每棵樹只算根；
/// 父程序以 PID 對上而且建立得比較早才算活著的父程序（PID 被重複使用時不誤接）
/// </summary>
public sealed class BackgroundProgramRulesTests
{
    private const string Conv = "0123456789abcdef0123456789abcdef";
    private const string Other = "fedcba9876543210fedcba9876543210";

    private static ProcessRow Row(int pid, int parent, long created, string name = "bash.exe", string? marker = Conv, bool? console = true, string? cmd = null)
        => new(pid, parent, created, name, marker, console, cmd);

    [Fact]
    public void 標記的格式與環境區塊()
    {
        BackgroundProgramRules.IsMarker(Conv).Should().BeTrue();
        BackgroundProgramRules.IsMarker(Conv.ToUpperInvariant()).Should().BeFalse("對話 id 一律小寫");
        BackgroundProgramRules.IsMarker(Conv[..31]).Should().BeFalse();
        BackgroundProgramRules.IsMarker(null).Should().BeFalse();

        BackgroundProgramRules.MarkerOf("PATH=C:\\x\0myriagent_agent_conversation=" + Conv + "\0OTHER=1\0\0").Should().Be(Conv, "名稱不分大小寫");
        BackgroundProgramRules.MarkerOf("A=1\0\0" + BackgroundProgramRules.MarkerVariable + "=" + Conv + "\0").Should().BeNull("空字串之後是區塊結尾");
        BackgroundProgramRules.MarkerOf(BackgroundProgramRules.MarkerVariable + "=not-an-id\0\0").Should().BeNull();
        BackgroundProgramRules.MarkerOf(string.Empty).Should().BeNull();
    }

    [Fact]
    public void 脫離CLI的程序樹算背景程式_只算根_CLI底下的不算()
    {
        var rows = new[]
        {
            Row(100, 1, 10, "claude.exe"),                                 // CLI（工作物件裡）
            Row(101, 100, 20, "node.exe"),                                 // MCP 伺服器：CLI 底下
            Row(102, 100, 30, "bash.exe"),                                 // 回合中的工具：CLI 底下
            Row(200, 999, 40, "bash.exe", cmd: "bash chain7.sh"),          // 父程序（工具的 bash）已經結束：脫離
            Row(201, 200, 50, "python.exe", cmd: "python worker6.py"),    // 同一棵樹：只算根
            Row(300, 777, 60, "python.exe", marker: Other),                // 另一個對話
            Row(400, 888, 70, "node.exe", marker: null),                   // 沒有標記
        };

        var result = BackgroundProgramRules.Classify(rows, new HashSet<int> { 100 });

        result.Keys.Should().BeEquivalentTo(new[] { Conv, Other });
        result[Conv].Should().ContainSingle().Which.Should().Be(new BackgroundProgram(200, "bash.exe", "bash chain7.sh", 40, 999));
        result[Conv][0].Id.Should().Be("200-40", "PID 會被重複使用：id 連建立時間");
        result[Other].Single().Pid.Should().Be(300);
    }

    [Fact]
    public void CLI結束之後_留下來的程式都算()
    {
        var rows = new[] { Row(101, 100, 20, "node.exe"), Row(102, 101, 30, "python.exe") };

        BackgroundProgramRules.Classify(rows, new HashSet<int>())[Conv].Select(p => p.Pid).Should().Equal(101);
    }

    [Fact]
    public void PID被重複使用的父程序不算活著()
    {
        // 300 的父程序 PID 是 100，但現在的 100 比 300 晚建立（原本的父程序已經結束、PID 給了別人）
        var rows = new[] { Row(100, 1, 500, "claude.exe"), Row(300, 100, 60, "python.exe") };

        BackgroundProgramRules.Classify(rows, new HashSet<int> { 100 })[Conv].Single().Pid.Should().Be(300);
    }

    [Fact]
    public void 視窗程式與主控台宿主不算_子系統不知道的照算()
    {
        var rows = new[]
        {
            Row(10, 999, 1, "chrome.exe", console: false),
            Row(11, 999, 2, "conhost.exe"),
            Row(12, 999, 3, "OpenConsole.exe"),
            Row(13, 999, 4, "python.exe", console: null),
        };

        BackgroundProgramRules.Classify(rows, new HashSet<int>())[Conv].Select(p => p.Pid).Should().Equal(13);
    }

    [Fact]
    public void 視窗程式底下的主控台子程序_以它為根()
    {
        // 視窗程式不算，它開的主控台子程序就是自己那棵樹的根
        var rows = new[] { Row(10, 999, 1, "code.exe", console: false), Row(11, 10, 2, "node.exe") };

        BackgroundProgramRules.Classify(rows, new HashSet<int>())[Conv].Select(p => p.Pid).Should().Equal(11);
    }

    [Fact]
    public void 同一個對話的多個程式依建立時間排序_父子循環不會卡住()
    {
        var rows = new[]
        {
            Row(30, 999, 300),
            Row(20, 998, 200),
            Row(41, 42, 400),   // 41 與 42 互為父程序（不可能，但快照可能錯）：都是同一個時間
            Row(42, 41, 400),
        };

        var programs = BackgroundProgramRules.Classify(rows, new HashSet<int>())[Conv];

        programs.Select(p => p.Pid).Should().StartWith(new[] { 20, 30 });
    }

    [Theory]
    [InlineData("MSBuild.exe", "\"C:\\Program Files\\Microsoft Visual Studio\\MSBuild.exe\" /nologo /nodemode:1 /nodeReuse:true /low:false", true)]
    [InlineData("dotnet.exe", "\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\Program Files\\dotnet\\sdk\\10.0.100\\MSBuild.dll\" -nologo -nodemode:1 -nodeReuse:true", true)]
    [InlineData("dotnet.exe", "dotnet exec \"C:\\Program Files\\dotnet\\sdk\\10.0.100\\Roslyn\\bincore\\VBCSCompiler.dll\" \"-pipename:abc\"", true)]
    [InlineData("dotnet.exe", "dotnet \"C:\\sdk\\Sdks\\Microsoft.NET.Sdk.Razor\\tools\\rzc.dll\" server -p abc", true)]
    [InlineData("java.exe", "java -cp gradle.jar org.gradle.launcher.daemon.bootstrap.GradleDaemon 8.10", true)]
    [InlineData("git.exe", "git fsmonitor--daemon run", true)]
    [InlineData("adb.exe", "adb -L tcp:5037 fork-server server --reply-fd 584", true)]
    // LocalDB 的執行個體：第一次連線時自己啟動、所有程式共用（2026-10-24 核對時被算成連過它的對話的背景程式；命令列照實機）
    [InlineData("sqlservr.exe", "\"C:\\Program Files\\Microsoft SQL Server\\150\\LocalDB\\Binn\\\\sqlservr.exe\"   -c -SMSSQL15E.LOCALDB -sLOCALDB#E5C21CD6 -d\"C:\\Users\\U\\AppData\\Local\\Microsoft\\Microsoft SQL Server Local DB\\Instances\\MSSQLLocalDB\\master.mdf\"", true)]
    [InlineData("sqlservr.exe", "\"C:\\Program Files\\Microsoft SQL Server\\MSSQL16.SQLEXPRESS\\MSSQL\\Binn\\sqlservr.exe\" -sSQLEXPRESS", false)]
    [InlineData("dotnet.exe", "dotnet run --project src/App", false)]
    [InlineData("dotnet.exe", "dotnet \"C:\\sdk\\MSBuild.dll\" build", false)]
    [InlineData("java.exe", "java -jar server.jar", false)]
    [InlineData("node.exe", "node server.js", false)]
    [InlineData("dotnet.exe", null, false)]
    public void 建置工具留著的常駐程序不算(string name, string? commandLine, bool daemon)
        => BackgroundProgramRules.IsToolDaemon(name, commandLine).Should().Be(daemon);

    [Fact]
    public void 建置工具的常駐程序與編譯伺服器不列成背景程式()
    {
        var rows = new[]
        {
            Row(10, 999, 1, "dotnet.exe", cmd: "dotnet \"C:\\sdk\\MSBuild.dll\" /nodemode:1 /nodeReuse:true"),
            Row(11, 999, 2, "VBCSCompiler.exe"),
            Row(12, 999, 3, "mspdbsrv.exe"),
            Row(13, 999, 4, "node.exe", cmd: "node server.js"),
        };

        BackgroundProgramRules.Classify(rows, new HashSet<int>())[Conv].Select(p => p.Pid).Should().Equal(13);
    }

    [Fact]
    public void 建置工具常駐程序開出的程序也不算_MSBuild節點開的csc與vstest()
    {
        // 2026-10-24 核對：MSBuild 節點（被排除）開的 csc、vstest.console 成了根，結束時加了「已結束」提示
        var rows = new[]
        {
            Row(10, 999, 1, "dotnet.exe", cmd: "dotnet \"C:\\sdk\\MSBuild.dll\" /nologo /nodemode:1 /nodeReuse:false"),
            Row(20, 10, 2, "csc.exe", cmd: "csc /noconfig @\"C:\\Users\\U\\AppData\\Local\\Temp\\MSBuildTemp1\\tmp1.rsp\""),
            Row(21, 20, 3, "conhost.exe"),
            Row(30, 10, 4, "dotnet.exe", cmd: "dotnet exec \"C:\\Program Files\\dotnet\\sdk\\10.0.402\\vstest.console.dll\" --framework net10.0"),
            Row(31, 30, 5, "testhost.exe"),
            Row(40, 999, 6, "node.exe", cmd: "node server.js"),
        };

        BackgroundProgramRules.Classify(rows, new HashSet<int>())[Conv].Select(p => p.Pid).Should().Equal(40);
    }

    [Theory]
    [InlineData("pythonw.exe", "pythonw worker.py", true)]
    [InlineData("javaw.exe", null, true)]
    [InlineData("chrome.exe", "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --remote-debugging-port=9503 --user-data-dir=\"C:\\x\"", true)]
    [InlineData("msedge.exe", "msedge --headless=new --dump-dom https://example.test", true)]
    [InlineData("firefox.exe", "firefox --marionette -profile p", true)]
    [InlineData("chrome.exe", "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" https://example.test", false)]
    [InlineData("chrome.exe", null, false)]
    [InlineData("notepad.exe", "notepad README.md", false)]
    public void 沒有視窗的背景執行器與自動化用的瀏覽器也算(string name, string? commandLine, bool expected)
        => BackgroundProgramRules.IsBackgroundGuiProgram(name, commandLine).Should().Be(expected);

    [Fact]
    public void 視窗程式只算自動化用的瀏覽器_分頁等子程序跟著根()
    {
        var rows = new[]
        {
            Row(10, 999, 1, "chrome.exe", console: false, cmd: "chrome.exe --remote-debugging-port=9503 --user-data-dir=C:\\p"),
            Row(11, 10, 2, "chrome.exe", console: false, cmd: "chrome.exe --type=renderer --remote-debugging-port=9503"),
            Row(20, 999, 3, "chrome.exe", console: false, cmd: "chrome.exe https://example.test"),
            Row(30, 999, 4, "pythonw.exe", console: false, cmd: "pythonw worker.py"),
            Row(40, 999, 5, "notepad.exe", console: false, cmd: "notepad a.txt"),
        };

        BackgroundProgramRules.Classify(rows, new HashSet<int>())[Conv].Select(p => p.Pid).Should().Equal(10, 30);
    }

    [Theory]
    // Git Bash 經 timeout 開的前景指令（父子關係斷掉）：說明裡有同一個指令
    [InlineData("\"C:\\Program Files\\nodejs\\node.exe\" probe.mjs --account", "cd /c/repo/x/tools/ui-probe && timeout 400 node probe.mjs --account > /tmp/a.log 2>&1", true)]
    // MSYS 路徑（/c/…）與引號
    [InlineData("node C:/Users/U/AppData/Local/Temp/x/inspect2.mjs", "timeout 90 node \"/c/Users/U/AppData/Local/Temp/x/inspect2.mjs\"", true)]
    // 反斜線與大小寫
    [InlineData("\"C:\\Python\\python.exe\" -X utf8 D:\\Work\\Job.py", "python -x utf8 d:/work/job.py", true)]
    // 不同的程式
    [InlineData("python.exe worker6.py", "nohup python worker7.py > f.log &", false)]
    // 沒有參數的不比
    [InlineData("\"C:\\Program Files\\nodejs\\node.exe\"", "node", false)]
    public void CLI背景工作的說明裡有這個程式的命令列(string commandLine, string taskText, bool expected)
    {
        var program = new BackgroundProgram(1, "x.exe", commandLine, 1);
        BackgroundProgramRules.IsCliTask(program, [taskText]).Should().Be(expected);
    }

    [Fact]
    public void CLI背景工作_多行的完整指令也比_沒有說明的略過()
    {
        var program = new BackgroundProgram(1, "node.exe", "node server.js --port 5173", 1);
        BackgroundProgramRules.IsCliTask(program, [null, "", "cd web\nnode server.js --port 5173\n"]).Should().BeTrue();
        BackgroundProgramRules.IsCliTask(program, [null, "  "]).Should().BeFalse();
    }

    [Fact]
    public void 結束的程式_消失的才算_子程序接著當根的不算()
    {
        var a = new BackgroundProgram(10, "cmd.exe", "cmd /c npm run dev", 100, 1);
        var b = new BackgroundProgram(20, "python.exe", "python job.py", 200, 2);
        var c = new BackgroundProgram(30, "ping.exe", "ping -t x", 300, 3);
        var previous = new Dictionary<string, List<BackgroundProgram>> { [Conv] = [a, b], [Other] = [c] };
        // a 結束了、它開的 node 接著當根（父程序 PID 是 a）；b 結束了；另一個對話的 c 還在
        var node = new BackgroundProgram(11, "node.exe", "node dev.js", 150, 10);
        var current = new Dictionary<string, List<BackgroundProgram>> { [Conv] = [node], [Other] = [c] };

        BackgroundProgramRules.Exits(previous, current).Should().Equal((Conv, b));

        // PID 一樣、但建立時間不同（PID 被重複使用）：原本的已經結束
        var reused = new BackgroundProgram(30, "ping.exe", "ping -t x", 999, 3);
        BackgroundProgramRules.Exits(previous, new Dictionary<string, List<BackgroundProgram>> { [Conv] = [a, b], [Other] = [reused] })
            .Should().Equal((Other, c));
        // 建立得比它早的不是它的子程序（父程序 PID 是重複使用前的別人）
        var older = new BackgroundProgram(12, "node.exe", "node old.js", 50, 10);
        BackgroundProgramRules.Exits(previous, new Dictionary<string, List<BackgroundProgram>> { [Conv] = [b, older], [Other] = [c] })
            .Should().Equal((Conv, a));
    }

    [Fact]
    public void 結束的提示_結束碼與執行多久()
    {
        var start = new DateTime(2026, 10, 24, 1, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var program = new BackgroundProgram(10, "python.exe", "C:\\Python313\\python.exe worker6.py", start);

        BackgroundProgramRules.ExitNotice(new BackgroundProgramExit(Conv, program, 1, start + TimeSpan.FromMinutes(12.5).Ticks))
            .Should().Be("代理另開的程式「python worker6.py」已結束（結束碼 1，執行了 12 分鐘）");
        BackgroundProgramRules.ExitNotice(new BackgroundProgramExit(Conv, program, unchecked((int)0xC000013A), start + TimeSpan.FromSeconds(9).Ticks))
            .Should().Be("代理另開的程式「python worker6.py」已結束（被中斷（Ctrl+C），執行了 9 秒）");
        // 以前顯示成看不懂的「結束碼 0xFFFFFFFF」（TerminateProcess、taskkill /F、LocalDB 自己關掉）
        BackgroundProgramRules.ExitNotice(new BackgroundProgramExit(Conv, program, -1, start + TimeSpan.FromMinutes(5).Ticks))
            .Should().Be("代理另開的程式「python worker6.py」已結束（被強制結束，執行了 5 分鐘）");
        BackgroundProgramRules.ExitNotice(new BackgroundProgramExit(Conv, program, unchecked((int)0xC0000005), null))
            .Should().Be("代理另開的程式「python worker6.py」已結束（結束碼 0xC0000005）", "其他 NTSTATUS 照舊以十六進位顯示");
        BackgroundProgramRules.ExitNotice(new BackgroundProgramExit(Conv, program, null, null))
            .Should().Be("代理另開的程式「python worker6.py」已結束", "讀不到結束碼（沒有權限追蹤）");
        BackgroundProgramRules.DurationText(TimeSpan.FromMinutes(125)).Should().Be("2 小時 5 分");
        BackgroundProgramRules.DurationText(TimeSpan.FromHours(3)).Should().Be("3 小時");
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Git\\usr\\bin\\bash.exe\" chain7.sh", "bash.exe", "bash chain7.sh")]
    [InlineData("C:\\Python313\\python.exe  -u   worker6.py --batch", "python.exe", "python -u worker6.py --batch")]
    [InlineData("/usr/bin/sleep 600", "sleep.exe", "sleep 600")]
    [InlineData(null, "node.exe", "node")]
    [InlineData("", "dotnet.exe", "dotnet")]
    [InlineData("\"C:\\no closing quote\\x.exe", "x.exe", "x")]
    public void 命令列縮短成執行檔名加參數(string? commandLine, string name, string expected)
        => BackgroundProgramRules.ShortCommand(commandLine, name).Should().Be(expected);

    [Fact]
    public void 命令列太長截斷()
    {
        var shortened = BackgroundProgramRules.ShortCommand("python " + new string('a', 200), "python.exe");

        shortened.Should().HaveLength(BackgroundProgramRules.MaxCommandLength);
        shortened.Should().EndWith("…");
    }
}
