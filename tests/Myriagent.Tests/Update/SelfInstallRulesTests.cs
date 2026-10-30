using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Update;

public sealed class SelfInstallRulesTests
{
    /// <summary>格式同 framework-dependent、RID 專屬的發佈（src/Myriagent 的 Myriagent.deps.json）</summary>
    private static string Deps(JObject libraries)
        => new JObject
        {
            ["runtimeTarget"] = new JObject { ["name"] = ".NETCoreApp,Version=v10.0/win-x64" },
            ["targets"] = new JObject
            {
                [".NETCoreApp,Version=v10.0"] = new JObject(),
                [".NETCoreApp,Version=v10.0/win-x64"] = libraries,
            },
        }.ToString();

    [Fact]
    public void 從deps推出發佈自己的檔_runtime在根目錄_native也保留相對路徑_語系資料夾_加上pdb與xml()
    {
        var deps = Deps(new JObject
        {
            ["Myriagent/0.2.0"] = new JObject { ["runtime"] = new JObject { ["Myriagent.dll"] = new JObject() } },
            ["Markdig/1.3.2"] = new JObject { ["runtime"] = new JObject { ["lib/net10.0/Markdig.dll"] = new JObject() } },
            ["Microsoft.Web.WebView2/1.0.4191.47"] = new JObject { ["native"] = new JObject { ["runtimes/win-x64/native/WebView2Loader.dll"] = new JObject() } },
            ["Some.Portable/1.0.0"] = new JObject
            {
                ["runtimeTargets"] = new JObject { ["runtimes/win/lib/net10.0/Some.Portable.dll"] = new JObject { ["rid"] = "win", ["assetType"] = "runtime" } },
                ["resources"] = new JObject { ["lib/net10.0/zh-Hant/Some.Portable.resources.dll"] = new JObject { ["locale"] = "zh-Hant" } },
            },
        });

        var files = SelfInstallRules.CandidatesFromDeps(deps);

        files.Should().Contain(new[]
        {
            "Myriagent.exe", "Myriagent.dll", "Myriagent.deps.json", "Myriagent.runtimeconfig.json", "Myriagent.pdb",
            "Markdig.dll", "Markdig.pdb", "Markdig.xml",
            "WebView2Loader.dll", "runtimes/win-x64/native/WebView2Loader.dll",
            "runtimes/win/lib/net10.0/Some.Portable.dll", "zh-Hant/Some.Portable.resources.dll",
        });
        files.Should().NotContain("lib/net10.0/Markdig.dll", "發佈時受管理的組件一律放在根目錄");
        files.Should().OnlyHaveUniqueItems();
        files.Should().OnlyContain(f => ReleaseManifestRules.IsValidPath(f));
    }

    [Fact]
    public void deps裡不合法的路徑整筆略過()
    {
        var deps = Deps(new JObject
        {
            ["Evil/1.0.0"] = new JObject
            {
                ["native"] = new JObject { ["../../evil.dll"] = new JObject(), ["C:/Windows/evil2.dll"] = new JObject() },
                ["resources"] = new JObject { ["lib/x.resources.dll"] = new JObject { ["locale"] = ".." } },
            },
        });

        var files = SelfInstallRules.CandidatesFromDeps(deps);

        files.Should().NotContain(f => f.Contains("evil", StringComparison.OrdinalIgnoreCase) || f.Contains("resources", StringComparison.OrdinalIgnoreCase));
        files.Should().Contain(SelfInstallRules.RequiredFiles);
    }

    [Theory]
    [InlineData("不是 JSON")]
    [InlineData("[1,2]")]
    public void deps讀不懂_丟InvalidDataException(string text)
    {
        var act = () => SelfInstallRules.CandidatesFromDeps(text);

        act.Should().Throw<InvalidDataException>().WithMessage("*Myriagent.deps.json*");
    }
}
