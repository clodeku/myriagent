using Myriagent.Files;

namespace Myriagent.Tests.Files;

/// <summary>
/// 瀏覽器上傳的檔下載到這台的規則（WP4.7 計畫步 11，D22）：暫存檔名只留最後一段、去掉 Windows 不收的字元與保留裝置名稱、重名加序號；
/// 暫存資料夾的界線只看正規化之後的字串（<c>..</c> 跳出去的不算）；磁碟空間要留 1 GB
/// </summary>
public sealed class UploadNameRulesTests
{
    private static string Safe(string? name, params string[] taken) => UploadNameRules.SafeName(name, new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase));

    [Theory]
    [InlineData(@"..\..\evil.exe", "evil.exe")]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", "hosts")]
    [InlineData("報告.txt", "報告.txt")]
    [InlineData("a:b.txt", "a_b.txt")]
    [InlineData("what?.png", "what_.png")]
    [InlineData("x.txt. ", "x.txt")]
    [InlineData("  前後空白.md  ", "前後空白.md")]
    public void SafeName_只留最後一段_換掉不收的字元(string name, string expected)
        => Safe(name).Should().Be(expected);

    [Theory]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("con", "_con")]
    [InlineData("NUL.tar.gz", "_NUL.tar.gz")]
    [InlineData("com1.log", "_com1.log")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("CONIN$", "_CONIN$")]
    [InlineData("console.txt", "console.txt")]
    [InlineData("com10.txt", "com10.txt")]
    public void SafeName_保留裝置名稱前面加底線(string name, string expected)
        => Safe(name).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("...")]
    [InlineData(@"..\")]
    [InlineData("\u0001\u0002")]
    public void SafeName_清不出字的用預設名稱(string? name)
        => Safe(name).Should().Be(UploadNameRules.FallbackName);

    [Fact]
    public void SafeName_去掉控制字元與RTL覆寫()
    {
        var rtl = "invoice" + (char)0x202E + "fdp.exe";
        Safe(rtl).Should().Be("invoicefdp.exe");
        Safe("a\tb\nc.txt").Should().Be("abc.txt");
    }

    [Fact]
    public void SafeName_重名照附件的規則加序號_不分大小寫_結果加進已用的()
    {
        var taken = new HashSet<string>(["A.PNG"], StringComparer.OrdinalIgnoreCase);

        UploadNameRules.SafeName("a.png", taken).Should().Be("a (2).png");
        UploadNameRules.SafeName("a.png", taken).Should().Be("a (3).png");
        taken.Should().Contain(["a (2).png", "a (3).png"]);
    }

    [Fact]
    public void SafeName_很長的檔名截到120字_保留副檔名()
    {
        var name = new string('長', 300) + ".pdf";

        var safe = Safe(name);

        safe.Length.Should().BeLessThanOrEqualTo(120);
        safe.Should().EndWith(".pdf");
    }

    [Theory]
    [InlineData(@"C:\data\cache\uploads\a\x.txt", @"C:\data\cache\uploads\a", true)]
    [InlineData(@"c:\DATA\cache\uploads\A\x.txt", @"C:\data\cache\uploads\a", true)]
    [InlineData(@"C:\data\cache\uploads\a\x.txt", @"C:\data\cache\uploads\a\", true)]
    [InlineData(@"C:\data\cache\uploads\a\sub\x.txt", @"C:\data\cache\uploads\a", true)]
    [InlineData(@"C:\data\cache\uploads\a\..\b\x", @"C:\data\cache\uploads\a", false)]
    [InlineData(@"C:\data\cache\uploads\a\..\..\x", @"C:\data\cache\uploads\a", false)]
    [InlineData(@"C:\data\cache\uploads\a", @"C:\data\cache\uploads\a", false)]
    [InlineData(@"C:\data\cache\uploads\a\", @"C:\data\cache\uploads\a", false)]
    [InlineData(@"C:\data\cache\uploads\ab\x", @"C:\data\cache\uploads\a", false)]
    [InlineData(@"D:\data\cache\uploads\a\x", @"C:\data\cache\uploads\a", false)]
    [InlineData(@"\\server\share\a\x", @"\\server\share\a", false)]
    [InlineData(@"\\?\C:\data\a\x", @"C:\data\a", false)]
    [InlineData(@"cache\uploads\a\x", @"cache\uploads\a", false)]
    public void IsUnder_只看正規化之後的字串(string path, string directory, bool expected)
        => UploadNameRules.IsUnder(path, directory).Should().Be(expected);

    [Fact]
    public void IsUnder_空的一律false()
    {
        UploadNameRules.IsUnder(null, @"C:\a").Should().BeFalse();
        UploadNameRules.IsUnder(@"C:\a\b", null).Should().BeFalse();
        UploadNameRules.IsUnder(" ", @"C:\a").Should().BeFalse();
    }

    [Fact]
    public void HasDiskRoom_合計之外至少留1GB_量不到當成不夠()
    {
        const long total = 512L * 1024 * 1024;

        UploadNameRules.HasDiskRoom(total + UploadNameRules.DiskMarginBytes, total).Should().BeTrue();
        UploadNameRules.HasDiskRoom(total + UploadNameRules.DiskMarginBytes - 1, total).Should().BeFalse();
        UploadNameRules.HasDiskRoom(null, 0).Should().BeFalse();
        UploadNameRules.HasDiskRoom(-1, 0).Should().BeFalse();
        UploadNameRules.DiskMarginBytes.Should().Be(1L << 30);
        FluentActions.Invoking(() => UploadNameRules.HasDiskRoom(long.MaxValue, -1)).Should().Throw<ArgumentOutOfRangeException>();
    }
}
