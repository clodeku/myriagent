using System.Text;
using Myriagent.Launch;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Launch;

/// <summary>執行列：認得哪些專案、按鈕做什麼、建置與執行的指令、什麼時候重新偵測、輸出的整理、給網頁的 LaunchView、輸出緩衝</summary>
public sealed class LaunchRulesTests
{
    private const string Proj = @"C:\repo\app\src\App\App.csproj";
    private const string Rel = @"src\App\App.csproj";

    private static LaunchTarget? DotNet(string body, string sdk = "Microsoft.NET.Sdk")
        => LaunchRules.FromDotNetProject(Proj, Rel, $"<Project Sdk=\"{sdk}\">{body}</Project>");

    private static string Props(string inner) => $"<PropertyGroup>{inner}</PropertyGroup>";

    [Fact]
    public void WinForms的WinExe_認得_說明是介面與架構()
    {
        var t = DotNet(Props("<OutputType>WinExe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWindowsForms>true</UseWindowsForms>"));
        t.Should().NotBeNull();
        t!.Kind.Should().Be(LaunchKind.DotNet);
        t.Name.Should().Be("App");
        t.Detail.Should().Be("WinForms · net10.0-windows");
        t.Framework.Should().BeNull("只有一個目標架構時不必 -f");
        t.RelativePath.Should().Be(Rel);
        t.Id.Should().Be(LaunchRules.TargetId(Proj));
    }

    [Theory]
    [InlineData("<OutputType>Exe</OutputType><UseWPF>true</UseWPF>", "WPF")]
    [InlineData("<OutputType>exe</OutputType><UseWindowsForms>True</UseWindowsForms>", "WinForms")]
    [InlineData("<OutputType>WinExe</OutputType>", "WinExe")]
    public void Exe加上WinForms或WPF_也算視窗程式(string props, string ui)
        => DotNet(Props(props))!.Detail.Should().Be(ui);

    [Fact]
    public void Avalonia_看套件參照()
        => DotNet(Props("<OutputType>WinExe</OutputType>") + "<ItemGroup><PackageReference Include=\"Avalonia.Desktop\" Version=\"11.0.0\" /></ItemGroup>")!
            .Detail.Should().Be("Avalonia");

    [Theory]
    [InlineData("<OutputType>Exe</OutputType>")]                                     // 主控台
    [InlineData("<TargetFramework>net10.0</TargetFramework>")]                       // 程式庫
    [InlineData("<OutputType>WinExe</OutputType><IsTestProject>true</IsTestProject>")]
    [InlineData("<OutputType>Exe</OutputType><UseMaui>true</UseMaui><UseWindowsForms>true</UseWindowsForms>")]
    public void 主控台_程式庫_測試_MAUI_不列(string props) => DotNet(Props(props)).Should().BeNull();

    [Fact]
    public void 參照測試SDK的不列()
        => DotNet(Props("<OutputType>WinExe</OutputType>") + "<ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup>").Should().BeNull();

    [Fact]
    public void 有Condition的屬性不算()
    {
        DotNet("<PropertyGroup Condition=\"'$(Configuration)'=='Debug'\"><OutputType>WinExe</OutputType></PropertyGroup>").Should().BeNull();
        DotNet(Props("<OutputType Condition=\"'$(X)'=='1'\">WinExe</OutputType>")).Should().BeNull();
    }

    [Fact]
    public void 舊式非SDK專案與壞掉的XML_不列()
    {
        LaunchRules.FromDotNetProject(Proj, Rel,
            "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>")
            .Should().BeNull("不能 dotnet run");
        LaunchRules.FromDotNetProject(Proj, Rel, "<Project Sdk=\"x\"><PropertyGroup>").Should().BeNull();
        LaunchRules.FromDotNetProject(Proj, Rel, "<!DOCTYPE x [<!ENTITY a \"b\">]><Project Sdk=\"x\"/>").Should().BeNull("不處理 DTD");
    }

    [Fact]
    public void Sdk元素或Import帶Sdk_也是SDK式()
    {
        LaunchRules.FromDotNetProject(Proj, Rel, "<Project><Sdk Name=\"Microsoft.NET.Sdk\" />" + Props("<OutputType>WinExe</OutputType>") + "</Project>").Should().NotBeNull();
        LaunchRules.FromDotNetProject(Proj, Rel, "<Project><Import Project=\"Sdk.props\" Sdk=\"Microsoft.NET.Sdk\" />" + Props("<OutputType>WinExe</OutputType>") + "</Project>")
            .Should().NotBeNull();
    }

    [Fact]
    public void 多目標_選Windows的架構並以f指定()
    {
        var t = DotNet(Props("<OutputType>WinExe</OutputType><TargetFrameworks>net8.0;net10.0-windows;net48</TargetFrameworks>"))!;
        t.Framework.Should().Be("net10.0-windows");
        t.Detail.Should().Be("WinExe · net10.0-windows");
        LaunchRules.BuildCommand(t, LaunchMode.Debug)!.Arguments.Should().EndWith(["-f", "net10.0-windows"]);
        LaunchRules.RunCommand(t, null, LaunchMode.Debug).Arguments.Should().EndWith(["-f", "net10.0-windows"]);

        DotNet(Props("<OutputType>WinExe</OutputType><TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>"))!.Framework.Should().Be("net8.0");
        DotNet(Props("<OutputType>WinExe</OutputType><TargetFrameworks>$(MyTfms)</TargetFrameworks>"))!.Framework.Should().BeNull("屬性參照不猜");
    }

    [Fact]
    public void AssemblyName_當名稱_含屬性參照的不用()
    {
        DotNet(Props("<OutputType>WinExe</OutputType><AssemblyName>MyTool</AssemblyName>"))!.Name.Should().Be("MyTool");
        DotNet(Props("<OutputType>WinExe</OutputType><AssemblyName>$(Foo).App</AssemblyName>"))!.Name.Should().Be("App");
    }

    [Fact]
    public void Electron_有electron與start或dev指令碼才列()
    {
        const string path = @"C:\repo\desk\package.json";
        var t = LaunchRules.FromPackageJson(path, "package.json", """{ "name": "my-desk", "devDependencies": { "electron": "^30" }, "scripts": { "dev": "x", "start": "electron ." } }""")!;
        t.Kind.Should().Be(LaunchKind.Electron);
        t.Name.Should().Be("my-desk");
        t.Script.Should().Be("start", "start 優先");
        t.Detail.Should().Be("Electron · npm run start");
        LaunchRules.BuildCommand(t, LaunchMode.Debug).Should().BeNull("npm 指令碼自己處理");
        LaunchRules.BuildCommand(t, LaunchMode.Release).Should().BeNull();
        LaunchRules.RunCommand(t, null, LaunchMode.Release).Arguments.Should().Equal(LaunchRules.RunCommand(t, null, LaunchMode.Debug).Arguments,
            "兩種模式的指令相同，差別只在和 app 綁不綁在一起");
        var run = LaunchRules.RunCommand(t, @"C:\Windows\system32\cmd.exe", LaunchMode.Debug);
        run.FileName.Should().Be(@"C:\Windows\system32\cmd.exe");
        run.Arguments.Should().Equal("/d", "/s", "/c", "npm run start");
        run.WorkingDirectory.Should().Be(@"C:\repo\desk");

        LaunchRules.FromPackageJson(path, "package.json", """{ "dependencies": { "electron": "1" }, "scripts": { "dev": "vite" } }""")!.Script.Should().Be("dev");
        LaunchRules.FromPackageJson(path, "package.json", """{ "dependencies": { "electron": "1" }, "scripts": { "dev": "vite" } }""")!.Name.Should().Be("desk", "沒有 name 用資料夾名");
        LaunchRules.FromPackageJson(path, "package.json", """{ "dependencies": { "electron": "1" }, "scripts": { "build": "x" } }""").Should().BeNull();
        LaunchRules.FromPackageJson(path, "package.json", """{ "dependencies": { "react": "1" }, "scripts": { "start": "x" } }""").Should().BeNull();
        LaunchRules.FromPackageJson(path, "package.json", "{ not json").Should().BeNull();
    }

    [Fact]
    public void Electron的指令碼名稱不會帶進其他文字()
    {
        var t = new LaunchTarget("t1", LaunchKind.Electron, "x", @"C:\a\package.json", "package.json", "d", Script: "start & calc");
        LaunchRules.RunCommand(t, null, LaunchMode.Debug).Arguments.Should().Equal("/d", "/s", "/c", "npm run start");
        LaunchRules.RunCommand(t, null, LaunchMode.Debug).FileName.Should().Be("cmd.exe");
    }

    [Fact]
    public void DotNet的建置與執行指令()
    {
        var t = DotNet(Props("<OutputType>WinExe</OutputType>"))!;
        var build = LaunchRules.BuildCommand(t, LaunchMode.Debug)!;
        build.FileName.Should().Be("dotnet");
        build.Arguments.Should().Equal("build", Proj, "-c", "Debug", "-nologo", "-tl:off", "-nodeReuse:false");
        build.WorkingDirectory.Should().Be(@"C:\repo\app\src\App");
        var run = LaunchRules.RunCommand(t, null, LaunchMode.Debug);
        run.Arguments.Should().Equal("run", "--project", Proj, "-c", "Debug", "--no-build");
        run.WorkingDirectory.Should().Be(@"C:\repo\app\src\App");
        // Release 模式：建置與執行都用 Release 組態
        LaunchRules.BuildCommand(t, LaunchMode.Release)!.Arguments.Should().Equal("build", Proj, "-c", "Release", "-nologo", "-tl:off", "-nodeReuse:false");
        LaunchRules.RunCommand(t, null, LaunchMode.Release).Arguments.Should().Equal("run", "--project", Proj, "-c", "Release", "--no-build");
        LaunchRules.DisplayCommand(new LaunchCommand("dotnet", ["build", @"C:\my app\a.csproj", "-c", "Debug"], "."))
            .Should().Be("> dotnet build \"C:\\my app\\a.csproj\" -c Debug");
    }

    [Theory]
    [InlineData("Idle", "start", "Start")]
    [InlineData("Idle", "restart", "Start")]
    [InlineData("Idle", "stop", "None")]
    [InlineData("Idle", "pause", "None")]
    [InlineData("Building", "start", "None")]
    [InlineData("Building", "stop", "Stop")]
    [InlineData("Building", "restart", "Restart")]
    [InlineData("Building", "pause", "None")]
    [InlineData("Running", "start", "None")]
    [InlineData("Running", "pause", "Pause")]
    [InlineData("Running", "resume", "None")]
    [InlineData("Running", "stop", "Stop")]
    [InlineData("Running", "restart", "Restart")]
    [InlineData("Paused", "start", "Resume")]
    [InlineData("Paused", "resume", "Resume")]
    [InlineData("Paused", "stop", "Stop")]
    [InlineData("Paused", "restart", "Restart")]
    [InlineData("Stopping", "stop", "None")]
    [InlineData("Stopping", "start", "None")]
    [InlineData("Stopping", "restart", "Restart")]
    [InlineData("Running", "Start", "None")]
    [InlineData("Idle", null, "None")]
    public void 按鈕在各階段做的事(string phase, string? action, string expected)
        => LaunchRules.Plan(Enum.Parse<LaunchPhase>(phase), action).Should().Be(Enum.Parse<LaunchStep>(expected));

    [Fact]
    public void 什麼時候重新偵測()
    {
        var now = new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);
        foreach (var trigger in Enum.GetValues<LaunchScanTrigger>())
            LaunchRules.ShouldScan(trigger, null, now).Should().BeTrue("沒偵測過一定偵測");
        LaunchRules.ShouldScan(LaunchScanTrigger.User, now, now).Should().BeTrue();
        LaunchRules.ShouldScan(LaunchScanTrigger.Open, now.AddSeconds(-29), now).Should().BeFalse();
        LaunchRules.ShouldScan(LaunchScanTrigger.Open, now.AddSeconds(-30), now).Should().BeTrue();
        LaunchRules.ShouldScan(LaunchScanTrigger.Changed, now.AddSeconds(-2), now).Should().BeFalse();
        LaunchRules.ShouldScan(LaunchScanTrigger.Changed, now.AddSeconds(-3), now).Should().BeTrue();
        LaunchRules.ShouldScan(LaunchScanTrigger.Periodic, now.AddSeconds(-59), now).Should().BeFalse();
        LaunchRules.ShouldScan(LaunchScanTrigger.Periodic, now.AddSeconds(-60), now).Should().BeTrue();
    }

    [Fact]
    public void 排序_淺的在前_去重複_最多20個_選擇保留()
    {
        LaunchTarget T(string rel) => new(LaunchRules.TargetId(@"C:\r\" + rel), LaunchKind.DotNet, rel, @"C:\r\" + rel, rel, "WinExe");
        var ordered = LaunchRules.Order([T(@"src\B\B.csproj"), T("Root.csproj"), T(@"src\A\A.csproj"), T("Root.csproj")]);
        ordered.Select(t => t.RelativePath).Should().Equal("Root.csproj", @"src\A\A.csproj", @"src\B\B.csproj");
        LaunchRules.Order(Enumerable.Range(0, 30).Select(i => T($"p{i:00}.csproj"))).Should().HaveCount(LaunchRules.MaxTargets);

        LaunchRules.Select(ordered, ordered[2].Id).Should().Be(ordered[2].Id);
        LaunchRules.Select(ordered, "gone").Should().Be(ordered[0].Id);
        LaunchRules.Select([], "gone").Should().BeNull();
    }

    [Theory]
    [InlineData("src", FileAttributes.Directory, true)]
    [InlineData("bin", FileAttributes.Directory, false)]
    [InlineData("OBJ", FileAttributes.Directory, false)]
    [InlineData("node_modules", FileAttributes.Directory, false)]
    [InlineData(".git", FileAttributes.Directory, false)]
    [InlineData(".vs", FileAttributes.Directory, false)]
    [InlineData("link", FileAttributes.Directory | FileAttributes.ReparsePoint, false)]
    [InlineData("hidden", FileAttributes.Directory | FileAttributes.Hidden, false)]
    public void 往下找的資料夾(string name, FileAttributes attributes, bool expected)
        => LaunchRules.ShouldDescend(name, attributes).Should().Be(expected);

    [Fact]
    public void 要讀的檔案()
    {
        LaunchRules.IsCandidateFile("App.csproj").Should().BeTrue();
        LaunchRules.IsCandidateFile("App.FSPROJ").Should().BeTrue();
        LaunchRules.IsCandidateFile("App.vbproj").Should().BeTrue();
        LaunchRules.IsCandidateFile("package.json").Should().BeTrue();
        LaunchRules.IsCandidateFile("App.csproj.user").Should().BeFalse();
        LaunchRules.IsCandidateFile("App.sln").Should().BeFalse();
    }

    [Fact]
    public void 輸出_先試UTF8_不是就用主控台字碼頁()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var big5 = Encoding.GetEncoding(950);
        LaunchRules.DecodeLine(Encoding.UTF8.GetBytes("建置成功。"), big5).Should().Be("建置成功。");
        LaunchRules.DecodeLine(big5.GetBytes("中文輸出"), big5).Should().Be("中文輸出");
        LaunchRules.DecodeLine([], big5).Should().BeEmpty();
    }

    [Fact]
    public void 輸出_去掉色碼與控制字元_tab換成空白_太長截掉()
    {
        LaunchRules.CleanLine("\x1B[32mok\x1B[0m done\r").Should().Be("ok done");
        LaunchRules.CleanLine("\x1B]0;title\x07text").Should().Be("text");
        LaunchRules.CleanLine("a\tb" + (char)1 + "c").Should().Be("a    bc");
        LaunchRules.CleanLine((char)0xFEFF + "bom").Should().Be("bom");
        var cut = LaunchRules.CleanLine(new string('x', 5000));
        cut.Should().HaveLength(LaunchRules.MaxLineLength + 1).And.EndWith("…");
    }

    [Fact]
    public void 建置錯誤的摘要_第一個錯誤_檔名只留名稱()
    {
        string[] lines =
        [
            "  正在判斷要還原的專案...",
            @"C:\repo\app\Form1.cs(12,5): warning CS0168: 已宣告變數 'e'，但從未使用過 [C:\repo\app\App.csproj]",
            @"C:\repo\app\Form1.cs(14,9): error CS1002: 必須是 ; [C:\repo\app\App.csproj]",
            @"C:\repo\app\Form2.cs(3,1): error CS0246: 找不到類型 'Foo' [C:\repo\app\App.csproj]",
            "建置失敗。",
            @"C:\repo\app\Form1.cs(14,9): error CS1002: 必須是 ; [C:\repo\app\App.csproj]",
        ];
        LaunchRules.BuildErrorSummary(lines).Should().Be("Form1.cs(14,9): CS1002 必須是 ;");
        LaunchRules.BuildErrorLines(lines).Should().HaveCount(2, "結尾摘要重複的那行不算");
        LaunchRules.BuildErrorSummary([@"C:\Program Files\dotnet\sdk\10.0.402\Sdks\Microsoft.NET.Sdk\targets\X.targets(1,1): error NETSDK1004: 找不到資產檔案 [C:\a.csproj]"])
            .Should().Be("X.targets(1,1): NETSDK1004 找不到資產檔案");
        LaunchRules.BuildErrorSummary(["MSBUILD : error MSB1009: 專案檔不存在。"]).Should().Be("MSB1009 專案檔不存在。");
        LaunchRules.BuildErrorSummary(["沒有錯誤"]).Should().BeNull();
        LaunchRules.BuildFailed(1, ["沒有錯誤"], DateTime.UtcNow).Text.Should().Be("建置失敗（結束碼 1）");
    }

    [Fact]
    public void 結束碼的寫法()
    {
        LaunchRules.FormatExitCode(0).Should().Be("0");
        LaunchRules.FormatExitCode(3).Should().Be("3");
        LaunchRules.FormatExitCode(unchecked((int)0xE0434352)).Should().Be("0xE0434352");
        LaunchRules.FormatExitCode(-1).Should().Be("0xFFFFFFFF");
        LaunchRules.Exited(0, DateTime.UtcNow).Text.Should().Be("已結束");
        LaunchRules.Exited(2, DateTime.UtcNow).Text.Should().Be("已結束（結束碼 2）");
    }

    [Fact]
    public void IsInside_資料夾本身與底下_不分大小寫_不被前綴騙()
    {
        LaunchRules.IsInside(@"C:\repo\app\a.csproj", @"C:\repo\app").Should().BeTrue();
        LaunchRules.IsInside(@"c:\REPO\APP", @"C:\repo\app").Should().BeTrue();
        LaunchRules.IsInside(@"C:\repo\app2\a.csproj", @"C:\repo\app").Should().BeFalse();
        LaunchRules.IsInside(@"D:\x.csproj", @"D:\").Should().BeTrue();
    }

    [Fact]
    public void Render_沒有目標是null_選的目標的狀態_各目標的階段()
    {
        LaunchRules.Render(@"C:\r", [], null, _ => null, _ => LaunchMode.Debug).Should().BeNull();

        var a = new LaunchTarget("ta", LaunchKind.DotNet, "A", @"C:\r\A.csproj", "A.csproj", "WinForms · net10.0-windows");
        var b = new LaunchTarget("tb", LaunchKind.Electron, "B", @"C:\r\b\package.json", @"b\package.json", "Electron · npm run start", Script: "start");
        var since = new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);
        var end = LaunchRules.BuildFailed(1, [@"x.cs(1,2): error CS1: boom [p]"], since.AddMinutes(1));
        var view = LaunchRules.Render(@"C:\r", [a, b], "tb", id => id switch
        {
            "ta" => new LaunchRunSnapshot(LaunchPhase.Running, since, null, 7, 2, LaunchMode.Debug),
            "tb" => new LaunchRunSnapshot(LaunchPhase.Idle, null, end, 3, 1),
            _ => null,
        }, id => id == "tb" ? LaunchMode.Release : LaunchMode.Debug)!;
        view.Value<string>("folder").Should().Be(@"C:\r");
        view.Value<string>("selected").Should().Be("tb");
        view.Value<string>("phase").Should().Be("idle");
        view["since"]!.Type.Should().Be(JTokenType.Null);
        view.Value<string>("mode").Should().Be("release", "選的目標選的模式");
        view["runMode"]!.Type.Should().Be(JTokenType.Null, "閒置沒有這一次的模式");
        view["last"]!.Value<string>("kind").Should().Be("buildFailed");
        view["last"]!.Value<int>("code").Should().Be(1);
        view["last"]!.Value<string>("text").Should().Be("x.cs(1,2): CS1 boom");
        view["last"]!.Value<long>("at").Should().Be(new DateTimeOffset(since.AddMinutes(1)).ToUnixTimeMilliseconds());
        view.Value<long>("logSeq").Should().Be(3);
        view.Value<long>("logEpoch").Should().Be(1);
        var targets = (JArray)view["targets"]!;
        targets.Select(t => t.Value<string>("phase")).Should().Equal("running", "idle");
        targets[1].Value<string>("kind").Should().Be("electron");
        targets[1].Value<string>("path").Should().Be(@"b\package.json");

        var first = LaunchRules.Render(@"C:\r", [a, b], "gone", id => id == "ta" ? new LaunchRunSnapshot(LaunchPhase.Running, since, null, 7, 2, LaunchMode.Debug) : null,
            _ => LaunchMode.Release)!;
        first.Value<string>("selected").Should().Be("ta", "選的不在了用第一個");
        first.Value<string>("mode").Should().Be("release", "執行中換了模式：下一次才套用");
        first.Value<string>("runMode").Should().Be("debug", "這一次照開始時的模式");
        first.Value<long>("since").Should().Be(new DateTimeOffset(since).ToUnixTimeMilliseconds());
        first["last"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void 列舉的名稱都有對應()
    {
        foreach (var k in Enum.GetValues<LaunchKind>())
            LaunchRules.KindName(k).Should().NotBeNullOrEmpty();
        foreach (var p in Enum.GetValues<LaunchPhase>())
            LaunchRules.PhaseName(p).Should().NotBeNullOrEmpty();
        foreach (var e in Enum.GetValues<LaunchEndKind>())
            LaunchRules.EndKindName(e).Should().NotBeNullOrEmpty();
        foreach (var m in Enum.GetValues<LaunchMode>())
        {
            LaunchRules.ParseMode(LaunchRules.ModeName(m)).Should().Be(m, "名稱來回");
            LaunchRules.Configuration(m).Should().NotBeNullOrEmpty();
        }
        LaunchRules.ParseMode("Debug").Should().BeNull("名稱分大小寫，不認得的當沒有");
        LaunchRules.ParseMode(null).Should().BeNull();
    }

    [Fact]
    public void 輸出緩衝_接著拿_丟掉的或換epoch的整份重拿_晚到的舊輸出不加()
    {
        var log = new LaunchLog(3);
        var none = log.Read(-1, 0);
        none.Reset.Should().BeTrue();
        none.Lines.Should().BeEmpty();
        none.Seq.Should().Be(0);
        log.Restart();
        var epoch = log.Epoch;
        epoch.Should().Be(1);
        log.Append("a");
        log.Append("b");
        var all = log.Read(-1, 0);
        all.Reset.Should().BeTrue();
        all.Lines.Should().Equal("a", "b");
        all.Seq.Should().Be(2);

        log.Append("c");
        var more = log.Read(epoch, 2);
        more.Reset.Should().BeFalse();
        more.Lines.Should().Equal("c");
        log.Read(epoch, 3).Lines.Should().BeEmpty();

        log.Append("d");
        log.Append("e");   // 容量 3：a、b 丟了
        var lost = log.Read(epoch, 1);
        lost.Reset.Should().BeTrue("要的第 2 行已經丟掉");
        lost.Lines.Should().Equal("c", "d", "e");
        log.Read(epoch, 2).Reset.Should().BeFalse("第 3 行還在");
        log.Read(epoch, 99).Reset.Should().BeTrue();

        log.Restart();
        log.AppendIf(epoch, "舊的").Should().BeFalse();
        log.AppendIf(log.Epoch, "新的").Should().BeTrue();
        var fresh = log.Read(epoch, 5);
        fresh.Reset.Should().BeTrue("epoch 換了");
        fresh.Lines.Should().Equal("新的");
        fresh.Epoch.Should().Be(2);
    }
}
