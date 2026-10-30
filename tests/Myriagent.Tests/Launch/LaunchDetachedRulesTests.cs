using System.Runtime.InteropServices;
using Myriagent.Core;
using Myriagent.Launch;
using Newtonsoft.Json;

namespace Myriagent.Tests.Launch;

/// <summary>
/// 執行列和 app 脫鉤的執行的純規則：CreateProcess 的命令列（以 CommandLineToArgvW 拆回來要一模一樣）、在 PATH 上找執行檔、環境區塊、
/// 輸出檔名、runs.json 的紀錄來回、哪些階段擋 app 自我更新
/// </summary>
public sealed class LaunchDetachedRulesTests
{
    private const char Nul = (char)0;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static List<string> Split(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToList();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [Fact]
    public void 命令列_含空白的加引號_和平常的寫法一樣()
    {
        LaunchRules.CommandLine(@"C:\Program Files\dotnet\dotnet.exe", ["run", "--project", @"C:\my app\App.csproj", "-c", "Debug"])
            .Should().Be(@"""C:\Program Files\dotnet\dotnet.exe"" run --project ""C:\my app\App.csproj"" -c Debug");
        LaunchRules.CommandLine(@"C:\WINDOWS\system32\cmd.exe", ["/d", "/s", "/c", "npm run start"])
            .Should().Be(@"C:\WINDOWS\system32\cmd.exe /d /s /c ""npm run start""", "cmd 的 /s 去掉外層引號，執行 npm run start");
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("with space")]
    [InlineData("a\"b")]
    [InlineData(@"C:\dir\")]
    [InlineData(@"C:\my dir\")]
    [InlineData(@"a\""b")]
    [InlineData(@"a\\""b c")]
    [InlineData(@"\\server\share\x y\\")]
    [InlineData("tab\there")]
    [InlineData("中文 路徑")]
    public void 命令列_以CommandLineToArgvW拆回來一模一樣(string arg)
    {
        var exe = @"C:\Program Files\App\app.exe";
        Split(LaunchRules.CommandLine(exe, [arg, "next"])).Should().Equal(exe, arg, "next");
    }

    [Fact]
    public void 找執行檔_有路徑的照用_名稱在PATH上找_不找目前資料夾()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files\dotnet\dotnet.exe",
            @"C:\WINDOWS\system32\cmd.exe",
            @"C:\tools\node.cmd",
        };
        bool Exists(string p) => files.Contains(p);
        var path = @"relative;;C:\empty;""C:\Program Files\dotnet"";C:\WINDOWS\system32;C:\tools";

        LaunchRules.ResolveExecutable("dotnet", path, Exists).Should().Be(@"C:\Program Files\dotnet\dotnet.exe", "沒有副檔名的加 .exe；PATH 項目的引號去掉");
        LaunchRules.ResolveExecutable("cmd.exe", path, Exists).Should().Be(@"C:\WINDOWS\system32\cmd.exe");
        LaunchRules.ResolveExecutable("node.cmd", path, Exists).Should().Be(@"C:\tools\node.cmd");
        LaunchRules.ResolveExecutable(@"C:\WINDOWS\system32\cmd.exe", null, Exists).Should().Be(@"C:\WINDOWS\system32\cmd.exe");
        LaunchRules.ResolveExecutable(@"C:\nope\x.exe", path, Exists).Should().BeNull();
        LaunchRules.ResolveExecutable("npm", path, Exists).Should().BeNull("只補 .exe");
        LaunchRules.ResolveExecutable(@"bin\app.exe", path, Exists).Should().BeNull("相對路徑不猜");
        LaunchRules.ResolveExecutable("dotnet", null, Exists).Should().BeNull();
        LaunchRules.ResolveExecutable("", path, Exists).Should().BeNull();
    }

    [Fact]
    public void 環境區塊_依名稱排序_同名的後面蓋前面_空的也有結尾()
    {
        LaunchRules.EnvironmentBlock([new("b", "2"), new("A", "1"), new("a", "3"), new("DOTNET_NOLOGO", "1")])
            .Should().Be($"A=3{Nul}b=2{Nul}DOTNET_NOLOGO=1{Nul}");
        LaunchRules.EnvironmentBlock([new("bad" + Nul, "x"), new("", "y")]).Should().Be(Nul.ToString());
    }

    [Fact]
    public void 輸出檔名_目標的鍵加時間_清理只認這種()
    {
        var id = LaunchRules.TargetId(@"C:\repo\App\App.csproj");
        var name = LaunchRules.LogFileName(id, new DateTime(2026, 10, 23, 14, 5, 6, 789, DateTimeKind.Utc));
        name.Should().Be($"{id}-20261023140506789.log");
        LaunchRules.IsLogFileName(name).Should().BeTrue();
        LaunchRules.IsLogFileName(@"..\" + name).Should().BeFalse("不會指到狀態資料夾以外");
        LaunchRules.IsLogFileName("runs.json").Should().BeFalse();
        LaunchRules.IsLogFileName("notes.log").Should().BeFalse();
        LaunchRules.IsLogFileName(null).Should().BeFalse();
    }

    [Fact]
    public void 紀錄_存檔再讀回_目標與時間與程序都一樣()
    {
        var path = @"C:\repo\app\App.csproj";
        var target = new LaunchTarget(LaunchRules.TargetId(path), LaunchKind.DotNet, "App", path, @"app\App.csproj", "WinForms · net10.0-windows", Framework: "net10.0-windows");
        var since = new DateTime(2026, 10, 23, 6, 7, 8, 123, DateTimeKind.Utc);
        var record = LaunchRules.ToRecord(target, since, paused: true, "x.log", [new LaunchProcessRef(10, 1000), new LaunchProcessRef(11, 1001)]);
        var json = JsonConvert.SerializeObject(new LaunchRunsFile { Runs = [record] }, JsonFile.Settings);
        json.Should().Contain("\"Kind\": \"dotnet\"", "種類存名稱");
        var back = JsonConvert.DeserializeObject<LaunchRunsFile>(json, JsonFile.Settings)!.Runs.Single();

        LaunchRules.TargetFromRecord(back).Should().Be(target);
        LaunchRules.SinceFromRecord(back).Should().Be(since);
        back.Paused.Should().BeTrue();
        back.LogFile.Should().Be("x.log");
        back.Processes.Should().Equal(new LaunchProcessRef(10, 1000), new LaunchProcessRef(11, 1001));

        var electronPath = @"C:\repo\shell\package.json";
        var electron = new LaunchTarget(LaunchRules.TargetId(electronPath), LaunchKind.Electron, "shell", electronPath, @"shell\package.json", "Electron · npm run dev", Script: "dev");
        LaunchRules.TargetFromRecord(LaunchRules.ToRecord(electron, null, false, null, [])).Should().Be(electron);
        LaunchRules.SinceFromRecord(LaunchRules.ToRecord(electron, null, false, null, [])).Should().BeNull();
    }

    [Fact]
    public void 紀錄_不完整_種類不認得_鍵對不上_不是完整路徑_略過()
    {
        var path = @"C:\repo\app\App.csproj";
        var target = new LaunchTarget(LaunchRules.TargetId(path), LaunchKind.DotNet, "App", path, @"app\App.csproj", "WinExe");
        LaunchRunRecord Record() => LaunchRules.ToRecord(target, DateTime.UtcNow, false, null, []);

        var unknown = Record();
        unknown.Kind = "maui";
        LaunchRules.TargetFromRecord(unknown).Should().BeNull("之後的版本新增的種類：舊版略過那一筆");
        var wrongId = Record();
        wrongId.TargetId = "t000000000000";
        LaunchRules.TargetFromRecord(wrongId).Should().BeNull();
        var relative = Record();
        relative.ProjectPath = @"app\App.csproj";
        relative.TargetId = LaunchRules.TargetId(relative.ProjectPath);
        LaunchRules.TargetFromRecord(relative).Should().BeNull();
        LaunchRules.TargetFromRecord(new LaunchRunRecord()).Should().BeNull();
        var bare = Record();
        bare.Name = null;
        bare.RelativePath = null;
        bare.Detail = null;
        LaunchRules.TargetFromRecord(bare).Should().Be(target with { RelativePath = "App.csproj", Detail = string.Empty });
    }

    [Fact]
    public void 擋app自我更新的只有建置與停止中()
    {
        LaunchRules.BlocksAppRestart(LaunchPhase.Building).Should().BeTrue();
        LaunchRules.BlocksAppRestart(LaunchPhase.Stopping).Should().BeTrue("可能接著重新建置");
        LaunchRules.BlocksAppRestart(LaunchPhase.Running).Should().BeFalse("執行中的另由 DiesWithApp 判斷");
        LaunchRules.BlocksAppRestart(LaunchPhase.Paused).Should().BeFalse();
        LaunchRules.BlocksAppRestart(LaunchPhase.Idle).Should().BeFalse();
        foreach (var phase in Enum.GetValues<LaunchPhase>())
            _ = LaunchRules.BlocksAppRestart(phase);
    }
}
