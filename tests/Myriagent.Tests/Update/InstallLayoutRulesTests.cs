using Myriagent.Update;

namespace Myriagent.Tests.Update;

public sealed class InstallLayoutRulesTests
{
    private static readonly InstallLayout Layout = new(@"C:\Users\u\AppData\Local\Programs\Myriagent");

    [Fact]
    public void 從current執行_以junction目標判斷版本()
        => InstallLayoutRules.ManagedRelease(Layout.Current + @"\", Layout, Layout.VersionDirectory("0.2.10-ab12cd3")).Should().Be("0.2.10-ab12cd3");

    [Fact]
    public void 直接從current指向的版本資料夾執行_也算受管理()
        => InstallLayoutRules.ManagedRelease(Layout.VersionDirectory("0.2.10-ab12cd3"), Layout, Layout.VersionDirectory("0.2.10-ab12cd3")).Should().Be("0.2.10-ab12cd3");

    [Fact]
    public void 從不是current指向的舊版資料夾執行_不算受管理()
        => InstallLayoutRules.ManagedRelease(Layout.VersionDirectory("0.2.9-0000000"), Layout, Layout.VersionDirectory("0.2.10-ab12cd3")).Should().BeNull();

    [Theory]
    [InlineData(@"C:\repo\myriagent\src\Myriagent\bin\Release\net10.0-windows\win-x64\")]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\Myriagent\versions\not-a-release\")]
    public void 開發版或資料夾名稱不是release_不算受管理(string dir)
        => InstallLayoutRules.ManagedRelease(dir, Layout, dir).Should().BeNull();

    [Fact]
    public void 沒有current_不算受管理()
        => InstallLayoutRules.ManagedRelease(Layout.VersionDirectory("0.2.10-ab12cd3"), Layout, null).Should().BeNull();

    [Theory]
    [InlineData(false, true, true, true, false, false, nameof(UpdateBlockReason.NotManaged))]
    [InlineData(true, false, true, true, false, false, nameof(UpdateBlockReason.CustomData))]
    [InlineData(true, true, false, true, false, false, nameof(UpdateBlockReason.Disabled))]
    [InlineData(true, true, true, false, false, false, nameof(UpdateBlockReason.NotPaired))]
    [InlineData(true, true, true, true, false, false, nameof(UpdateBlockReason.None))]
    [InlineData(true, false, true, false, true, true, nameof(UpdateBlockReason.None))]
    [InlineData(true, true, true, false, true, false, nameof(UpdateBlockReason.NotPaired))]
    public void 啟用條件依序判斷(bool managed, bool defaultData, bool autoUpdate, bool paired, bool testSource, bool e2e, string expected)
        => InstallLayoutRules.Evaluate(managed, defaultData, autoUpdate, paired, testSource, e2e).Should().Be(Enum.Parse<UpdateBlockReason>(expected));

    [Theory]
    [InlineData(null, "0.2.5-abcdef1", null, true)]                          // 還沒安裝
    [InlineData(null, "0.2.5-abcdef1", "not-a-release", true)]               // current 指向的資料夾不是 release：當成沒安裝
    [InlineData(null, "0.2.5-abcdef1", "0.2.4-1111111", true)]               // 安裝的比較舊
    [InlineData(null, "0.2.5-abcdef1", "0.2.5-1111111", false)]              // 同一版（build 不同也一樣）
    [InlineData(null, "0.2.5-abcdef1", "0.2.6-1111111", false)]              // 安裝的比較新
    [InlineData("0.2.5-abcdef1", "0.2.5-abcdef1", null, false)]              // 已經從安裝位置執行
    public void 何時提供安裝到這台電腦(string? managedRelease, string current, string? installedRelease, bool expected)
    {
        ReleaseIdentity.TryParseRelease(current, out var identity).Should().BeTrue();
        InstallLayoutRules.CanOfferInstall(managedRelease, identity!, installedRelease).Should().Be(expected);
    }

    [Fact]
    public void 開發版不提供安裝()
    {
        var dev = new ReleaseIdentity(new ReleaseVersion(0, 2, 5), ReleaseIdentity.DevBuild);
        InstallLayoutRules.CanOfferInstall(null, dev, null).Should().BeFalse();
    }

    [Fact]
    public void 每個原因都有說明()
    {
        foreach (var reason in Enum.GetValues<UpdateBlockReason>().Where(r => r != UpdateBlockReason.None))
            InstallLayoutRules.Describe(reason).Should().NotBeNullOrWhiteSpace();
    }
}
