using Myriagent.Launch;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Launch;

/// <summary>執行列的專案偵測（真的檔案系統、暫存資料夾）：往下找到幾層、跳過哪些資料夾、不跟 junction、UNC 不讀</summary>
public sealed class LaunchScannerTests
{
    private const string WinForms = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
            <TargetFramework>net10.0-windows</TargetFramework>
            <UseWindowsForms>true</UseWindowsForms>
          </PropertyGroup>
        </Project>
        """;

    private const string Console = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>""";

    private const string Electron = """{ "name": "desk", "devDependencies": { "electron": "^30.0.0" }, "scripts": { "start": "electron ." } }""";

    [Fact]
    public void 找到工作資料夾與子資料夾裡的視窗程式_淺的在前_相對路徑()
    {
        using var temp = new TempDirectory("launch-scan");
        temp.WriteFile(@"src\App\App.csproj", WinForms);
        temp.WriteFile(@"src\Cli\Cli.csproj", Console);
        temp.WriteFile("Root.csproj", WinForms);
        temp.WriteFile(@"desk\package.json", Electron);
        temp.WriteFile(@"web\package.json", """{ "dependencies": { "react": "18" }, "scripts": { "start": "x" } }""");

        var found = LaunchScanner.Scan(temp.Root, CancellationToken.None);

        found.Select(t => t.RelativePath).Should().Equal("Root.csproj", @"desk\package.json", @"src\App\App.csproj");
        found[2].ProjectPath.Should().Be(temp.Combine("src", "App", "App.csproj"));
        found[1].Kind.Should().Be(LaunchKind.Electron);
    }

    [Fact]
    public void 跳過建置輸出_套件_點開頭的資料夾_超過三層不找()
    {
        using var temp = new TempDirectory("launch-scan");
        temp.WriteFile(@"bin\Debug\Copy.csproj", WinForms);
        temp.WriteFile(@"obj\X.csproj", WinForms);
        temp.WriteFile(@"node_modules\electron-app\package.json", Electron);
        temp.WriteFile(@".worktrees\other\App.csproj", WinForms);
        temp.WriteFile(@"a\b\c\Deep3.csproj", WinForms);
        temp.WriteFile(@"a\b\c\d\Deep4.csproj", WinForms);

        LaunchScanner.Scan(temp.Root, CancellationToken.None).Select(t => t.Name).Should().Equal("Deep3");
    }

    [Fact]
    public void 不跟junction()
    {
        using var temp = new TempDirectory("launch-scan");
        using var junctions = new TestJunctions();
        var outside = temp.CreateDirectory("outside");
        File.WriteAllText(Path.Combine(outside, "Linked.csproj"), WinForms);
        var work = temp.CreateDirectory("work");
        junctions.Create(Path.Combine(work, "linked"), outside);

        LaunchScanner.Scan(work, CancellationToken.None).Should().BeEmpty();
    }

    [Fact]
    public void 太大的專案檔不讀_資料夾不存在與UNC回空清單()
    {
        using var temp = new TempDirectory("launch-scan");
        temp.WriteFile("Big.csproj", WinForms + new string(' ', (int)LaunchRules.MaxProjectFileBytes));
        LaunchScanner.Scan(temp.Root, CancellationToken.None).Should().BeEmpty();
        LaunchScanner.Scan(temp.Combine("missing"), CancellationToken.None).Should().BeEmpty();
        LaunchScanner.Scan(@"\\server.invalid\share", CancellationToken.None).Should().BeEmpty();
        LaunchScanner.Scan("relative", CancellationToken.None).Should().BeEmpty();
    }
}
