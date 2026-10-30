using System.Text.RegularExpressions;
using System.Xml.Linq;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;

namespace Myriagent.Tests.Protocol.Update;

/// <summary>manifest 的 minRuntime 由發佈資料夾的 runtimeconfig.json 決定（PIPE-1：發佈工具原本寫死 10.0.0，跟實際的目標框架沒有對照）</summary>
public sealed class ReleaseRuntimeRulesTests
{
    /// <summary>dotnet publish 寫的形狀：runtimeOptions 底下的 <paramref name="key"/>（frameworks、framework、includedFrameworks）是 <paramref name="value"/></summary>
    private static string Config(string key, string value) =>
        "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"" + key + "\":" + value + ",\"configProperties\":{\"System.GC.Concurrent\":true}}}";

    private static string Framework(string name, string version) => "{\"name\":\"" + name + "\",\"version\":\"" + version + "\"}";

    private const string Desktop = "Microsoft.WindowsDesktop.App";

    private static readonly string NetCore = Framework("Microsoft.NETCore.App", "10.0.0");

    [Theory]
    [InlineData("10.0.0")]
    [InlineData("10.0.4")]
    [InlineData("11.0.0")]
    public void dotnet_publish寫的形狀_讀出桌面框架的版本(string version)
    {
        var json = Config("frameworks", $"[{NetCore},{Framework(Desktop, version)}]");

        ReleaseRuntimeRules.TryReadMinRuntime(json, out var runtime, out var error).Should().BeTrue(error);

        ReleaseVersion.TryParse(version, out var expected).Should().BeTrue();
        runtime.Should().Be(new ReleaseRuntime(Desktop, expected));
    }

    [Fact]
    public void 只有一個框架時的單數寫法也收()
    {
        ReleaseRuntimeRules.TryReadMinRuntime(Config("framework", Framework(Desktop, "10.0.2")), out var runtime, out var error).Should().BeTrue(error);
        runtime!.Version.Should().Be(new ReleaseVersion(10, 0, 2));
    }

    [Fact]
    public void 不是framework_dependent的桌面發佈_拒絕()
    {
        var cases = new (string Name, string Json, string Reason)[]
        {
            ("沒有桌面框架", Config("frameworks", $"[{NetCore}]"), "沒有列"),
            ("self-contained", Config("includedFrameworks", $"[{NetCore},{Framework(Desktop, "10.0.0")}]"), "self-contained"),
            ("兩個桌面框架", Config("frameworks", $"[{Framework(Desktop, "10.0.0")},{Framework(Desktop, "10.0.1")}]"), "2 個"),
            ("預覽版", Config("frameworks", $"[{Framework(Desktop, "10.0.0-rc.2.25502.107")}]"), "三段"),
            ("兩段", Config("frameworks", $"[{Framework(Desktop, "10.0")}]"), "三段"),
            ("版本不是字串", Config("frameworks", "[{\"name\":\"" + Desktop + "\",\"version\":10}]"), "三段"),
        };
        foreach (var (name, json, reason) in cases)
        {
            ReleaseRuntimeRules.TryReadMinRuntime(json, out var runtime, out var error).Should().BeFalse(name);
            runtime.Should().BeNull(name);
            error.Should().Contain(reason, name);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"runtimeOptions\":1}")]
    [InlineData("{\"other\":{}}")]
    public void 不是runtimeconfig_拒絕不丟例外(string? json)
    {
        ReleaseRuntimeRules.TryReadMinRuntime(json, out var runtime, out var error).Should().BeFalse();
        runtime.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
        ReleaseRuntimeRules.TryReadMinRuntime(new string(' ', ReleaseRuntimeRules.MaxRuntimeConfigBytes + 1), out _, out _).Should().BeFalse("太大的不讀");
    }

    [Fact]
    public void 桌面的建置輸出讀得出來_主版本與目標框架一致()
    {
        // 發佈工具打包的就是這個檔（dotnet publish 寫的同一份）：目標框架改了，minRuntime 跟著變，不會再停在 10.0.0
        var root = MyriagentHubExe.RepoRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "Myriagent", "Myriagent.csproj"));
        string Property(string name) => project.Descendants(name).Single().Value.Trim();
        var tfm = Property("TargetFramework");
        var rid = Property("RuntimeIdentifier");
        var major = int.Parse(Regex.Match(tfm, @"^net(\d+)\.").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        // 測試專案參考桌面專案：同一個組態（Release／Debug）一定建置過它（測試的輸出是 bin\<組態>\<目標框架>\<RID>\，往上找到 bin 底下那一層）
        var dir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        while (dir.Parent is { } parent && !string.Equals(parent.Name, "bin", StringComparison.OrdinalIgnoreCase))
            dir = parent;
        dir.Parent.Should().NotBeNull($"{AppContext.BaseDirectory} 在某個 bin 資料夾底下");
        var configuration = dir.Name;
        var path = Path.Combine(root, "src", "Myriagent", "bin", configuration, tfm, rid, ReleaseRuntimeRules.RuntimeConfigFileName);
        File.Exists(path).Should().BeTrue($"桌面的建置輸出有 {path}");

        ReleaseRuntimeRules.TryReadMinRuntime(File.ReadAllText(path), out var runtime, out var error).Should().BeTrue(error);

        runtime!.Name.Should().Be(ReleaseRuntimeRules.DesktopFramework);
        runtime.Version.Major.Should().Be(major, $"目標框架是 {tfm}");
    }
}
