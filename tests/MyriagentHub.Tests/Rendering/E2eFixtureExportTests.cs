using Myriagent.Media;
using Newtonsoft.Json.Linq;
using Xunit.Sdk;

namespace MyriagentHub.Tests.Rendering;

/// <summary>
/// 給 Node 端到端腳本的測試資料（WP4.9 計畫 D17、步 11）：XSS 原始語料（<see cref="ItemSanitizerXssCorpus.All"/> 淨化之前的 Html）與媒體回應的 CSP
/// （<see cref="MediaHeaderRules.MediaCsp"/>）寫成黃金檔 <c>Rendering/Golden/e2e-fixtures.json</c>，fake-device 的 xssFill 讀它，語料只有 C# 這一份。
/// 逐位元組比對；刻意改了語料或 CSP 時以 MYRIAGENT_GOLDEN_RECORD=1 重跑重錄
/// </summary>
public sealed class E2eFixtureExportTests
{
    private const string Name = "e2e-fixtures";

    internal static JObject Build() => new()
    {
        ["description"] = "端到端腳本的測試資料（WP4.9 步 11，計畫 D17）：xss 是 ItemSanitizerXssCorpus 每一筆淨化之前的原始 html（tools/fake-device 的 xssFill 把它放進標題、"
                          + "帳戶與裝置名稱、代理文字、工具、問卷、核准卡與檔名）；mediaCsp 是 /m/、/t/、/f/ 回應的 "
                          + "Content-Security-Policy（MediaHeaderRules.MediaCsp）",
        ["mediaCsp"] = MediaHeaderRules.MediaCsp,
        ["xss"] = new JArray(ItemSanitizerXssCorpus.All.Select(c => new JObject { ["name"] = c.Name, ["html"] = c.Html })),
    };

    [Fact]
    public void 匯出的語料與媒體CSP_黃金檔逐位元組相同()
    {
        var golden = Build();
        ((JArray)golden["xss"]!).Should().HaveCountGreaterThanOrEqualTo(200, "設計 S10：至少 200 筆");
        HubGoldenFile.Verify(Name, golden);
    }

    [Fact]
    public void 改一個字元_比對就失敗()
    {
        // 錄製模式會把輸出寫回 repo：只在比對模式跑
        if (HubGoldenFile.Recording)
            return;
        var changed = Build();
        var first = (JObject)((JArray)changed["xss"]!)[0];
        var html = first.Value<string>("html")!;
        first["html"] = (html[0] == 'x' ? 'y' : 'x') + html[1..];

        FluentActions.Invoking(() => HubGoldenFile.Verify(Name, changed)).Should().Throw<XunitException>().WithMessage("*e2e-fixtures.json*");

        changed["mediaCsp"] = MediaHeaderRules.MediaCsp + " ";
        first["html"] = html;
        FluentActions.Invoking(() => HubGoldenFile.Verify(Name, changed)).Should().Throw<XunitException>("CSP 多一個空白也要紅");
    }

    [Fact]
    public void 原始語料沒有經過淨化_名稱與內容都不重複()
    {
        var xss = (JArray)Build()["xss"]!;
        xss.Select(c => c.Value<string>("name")).Should().OnlyHaveUniqueItems();
        xss.Select(c => c.Value<string>("html")).Should().OnlyHaveUniqueItems();
        xss.Select(c => c.Value<string>("html")).Should().Contain(h => h!.Contains("onerror", StringComparison.OrdinalIgnoreCase), "是淨化之前的原始語料");
    }
}
