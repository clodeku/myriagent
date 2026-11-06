using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Core;

/// <summary>其他電腦、瀏覽器版來的檔標成從網際網路下載（2026-11-07 資安檢查：Office 以保護的檢視開啟）</summary>
public sealed class MarkOfTheWebTests
{
    [Fact]
    public void 標記寫進Zone_Identifier_不存在的檔回false()
    {
        using var temp = new TempDirectory("motw");
        var path = temp.WriteFile("a.docx");
        MarkOfTheWeb.Mark(path).Should().BeTrue();
        File.ReadAllText(path + ":Zone.Identifier").Should().Be("[ZoneTransfer]\r\nZoneId=3\r\n");
        MarkOfTheWeb.Mark(temp.Combine("missing.docx")).Should().BeFalse();
        MarkOfTheWeb.Mark("").Should().BeFalse();
    }

    [Fact]
    public async Task 其他電腦下載到這台的檔_帶著標記()
    {
        using var temp = new TempDirectory("motw-peer");
        var saved = await PeerFileRules.SaveAsync(temp.Combine("downloads"), "報表.xlsx", [1, 2, 3]);
        File.ReadAllBytes(saved).Should().Equal(1, 2, 3);
        File.ReadAllText(saved + ":Zone.Identifier").Should().Contain("ZoneId=3");
    }
}
