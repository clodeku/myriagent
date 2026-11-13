using Myriagent.Remote;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.RemoteDesk;

/// <summary>
/// 服務要帶走的檔（<see cref="DeskServiceRules.PayloadFiles"/>）和建置出來的服務對得上：服務以 LocalSystem 在 Program Files 裡執行，
/// 安裝器只複製清單上的檔——服務（或它參考的 Myriagent.Protocol）多了一個相依而清單沒跟著加，裝好的服務就少檔起不來，
/// 而且只有真的裝起來才會發現。這裡拿建置輸出的 <c>MyriagentDesk.deps.json</c> 比對
/// </summary>
public sealed class DeskServicePayloadTests
{
    [Fact]
    public void 服務的相依都在清單裡_清單上的檔都在輸出資料夾()
    {
        var deps = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MyriagentDesk.deps.json")));
        var runtime = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in ((JObject)deps["targets"]!).Properties())
        {
            foreach (var library in ((JObject)target.Value).Properties())
            {
                if (library.Value["runtime"] is JObject assets)
                    runtime.UnionWith(assets.Properties().Select(p => Path.GetFileName(p.Name)));
                if (library.Value["native"] is JObject native)
                    runtime.UnionWith(native.Properties().Select(p => Path.GetFileName(p.Name)));
            }
        }

        runtime.Should().Contain("MyriagentDesk.dll").And.Contain("Myriagent.Protocol.dll");
        runtime.Should().BeSubsetOf(DeskServiceRules.PayloadFiles, "服務多了相依：把檔名加進 DeskServiceRules.PayloadFiles（裝好的服務才找得到它）");
        foreach (var name in DeskServiceRules.PayloadFiles)
            File.Exists(Path.Combine(AppContext.BaseDirectory, name)).Should().BeTrue(name + " 要跟著桌面的輸出（安裝器從 app 的資料夾複製）");
    }

    [Fact]
    public void 服務只靠基本的執行階段_不需要桌面的執行階段()
    {
        var config = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MyriagentDesk.runtimeconfig.json")));
        var options = (JObject)config["runtimeOptions"]!;
        var frameworks = options["frameworks"] is JArray list ? list.Select(f => f.Value<string>("name")).ToList() : [options["framework"]!.Value<string>("name")];
        frameworks.Should().Equal("Microsoft.NETCore.App");
    }

    [Fact]
    public void 清單沒有重複_都是單純的檔名()
    {
        DeskServiceRules.PayloadFiles.Should().OnlyHaveUniqueItems();
        DeskServiceRules.PayloadFiles.Should().OnlyContain(n => n == Path.GetFileName(n) && !n.Contains(".."));
        DeskServiceRules.PayloadFiles.Should().Contain(DeskServiceRules.ExeName);
    }
}
