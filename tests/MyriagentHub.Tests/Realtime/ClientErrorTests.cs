using MyriagentHub.Realtime;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 瀏覽器的錯誤回報 clientError（web-protocol 第二節，2026-10-23）：伺服器寫進自己的記錄（Warning）、同一件事 10 分鐘內只記一次、
/// 不合格回 invalid 但不送 toast（使用者沒做任何事）
/// </summary>
public sealed class ClientErrorTests
{
    [Fact]
    public async Task 瀏覽器回報寫進記錄_同一件事只記一次_不合格回invalid不送toast()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await HubTestHost.StartAsync(services: s => s.AddSingleton<ILoggerProvider>(logs));
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();

        var report = new { cmd = "clientError", kind = "media", message = "img 載入失敗", source = "/t/d_X-Y?s=320", detail = (string?)null };
        (await owner.Hub.CmdAsync(report))["accepted"]!.Value<bool>().Should().BeTrue();
        (await owner.Hub.CmdAsync(report))["accepted"]!.Value<bool>().Should().BeTrue("節流只影響記錄，照樣接受");
        (await owner.Hub.CmdAsync(new { cmd = "clientError", kind = "error", message = "TypeError: x\nis undefined", source = "app.js:1:2", detail = "at a" }))
            ["accepted"]!.Value<bool>().Should().BeTrue();

        var reported = logs.Entries.Where(e => e.Category == typeof(BrowserCommandHandler).FullName && e.Text.Contains("瀏覽器回報", StringComparison.Ordinal)).ToList();
        reported.Should().HaveCount(2, "同一則媒體失敗只記一次");
        reported.Should().OnlyContain(e => e.Level == LogLevel.Warning);
        reported[0].Text.Should().Contain("媒體載入失敗：img 載入失敗（/t/d_X-Y?s=320）");
        reported[1].Text.Should().Contain("例外：TypeError: x ⏎ is undefined（app.js:1:2）｜at a", "換行不讓記錄變成好幾行");

        var invalid = await owner.Hub.CmdAsync(new { cmd = "clientError", kind = "warn", message = "m" });
        invalid["accepted"]!.Value<bool>().Should().BeFalse();
        invalid["error"]!.Value<string>().Should().Be("invalid");
        await owner.Hub.AssertNoMessageAsync("toast", 300);
    }

    [Fact]
    public void clientError是伺服器處理的指令_被拒不送toast()
    {
        BrowserCommandRules.Classify("clientError").Should().Be(BrowserCommandClass.Server);
        BrowserCommandRules.Toast("clientError", "invalid").Should().BeNull();
    }
}
