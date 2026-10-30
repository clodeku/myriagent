using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Isolation;

/// <summary>
/// R2（計畫 D4）：桌面測試以子程序執行 MyriagentHub.exe、不參考 MyriagentHub 專案。專案參考會把 MyriagentHub 的 NuGet 套件與
/// ASP.NET Core 共用架構傳遞進測試專案（測試主機就能載入伺服器的組件，S6 與 ProtocolAssemblyTests 的前提跟著失效），
/// 也會把 MyriagentHub 的 appsettings*.json、wwwroot 複製進測試輸出
/// </summary>
public sealed class MyriagentHubSeparationTests
{
    /// <summary>MyriagentHub.csproj 才有的套件（SignalR 用戶端、Microsoft.Extensions 的基本套件桌面也有，不算）</summary>
    private static readonly string[] HubOnlyPackages =
    [
        "Certes",
        "Microsoft.AspNetCore.Identity",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Extensions.Hosting",
        "Microsoft.AspNetCore.SignalR.Protocols.MessagePack",
        "MyriagentHub",
    ];

    [Fact]
    public void 測試專案的套件沒有MyriagentHub的()
    {
        var assets = Path.Combine(MyriagentHubExe.RepoRoot(), "tests", "Myriagent.Tests", "obj", "project.assets.json");
        var libraries = ((JObject)JObject.Parse(File.ReadAllText(assets))["libraries"]!).Properties()
            .Select(p => p.Name.Split('/')[0])
            .ToList();

        libraries.Should().Contain("Microsoft.AspNetCore.SignalR.Client", "讀的是這個專案的 assets（陽性對照）");
        libraries.Should().NotContain(name => HubOnlyPackages.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void 測試主機的執行階段沒有AspNetCore共用架構()
    {
        var runtimeConfig = Path.Combine(AppContext.BaseDirectory, "Myriagent.Tests.runtimeconfig.json");
        var text = File.ReadAllText(runtimeConfig);

        text.Should().Contain("Microsoft.NETCore.App", "讀的是測試主機的 runtimeconfig（陽性對照）");
        text.Should().NotContain("Microsoft.AspNetCore.App");
    }

    [Fact]
    public void 測試輸出沒有MyriagentHub的設定檔與網頁()
    {
        var output = AppContext.BaseDirectory;

        File.Exists(Path.Combine(output, "Myriagent.Tests.dll")).Should().BeTrue("看的是測試輸出資料夾（陽性對照）");
        Directory.GetFiles(output, "appsettings*.json").Should().BeEmpty();
        Directory.Exists(Path.Combine(output, "wwwroot")).Should().BeFalse();
        File.Exists(Path.Combine(output, "MyriagentHub.dll")).Should().BeFalse();
    }
}
