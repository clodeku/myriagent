using System.Text;
using Myriagent.Files;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Files;

/// <summary>
/// 遠端檔案指令輸出的規則（WP4.7 計畫步 11，D33～D36）：entries 量到 700 KB 就截斷、範圍的根沒有上一層、
/// 守門員拒絕的原因分成「路徑不能給遠端」（policy）與「這個檔本身的問題」（帶 error 的輸出）、縮圖網址的版本
/// </summary>
public sealed class RemoteListRulesTests
{
    private static JObject Entry(string name) => new() { ["name"] = name };

    [Fact]
    public void Fit_加總UTF8位元組含中括號與逗號_剛好放得下就不截斷()
    {
        var a = Entry("a");     // {"name":"a"} = 12 位元組
        var b = Entry("檔");    // {"name":"檔"} = 14 位元組（中文 3 位元組）
        var exact = 2 + 12 + 1 + 14;

        var (fit, truncated) = RemoteListRules.Fit([a, b], exact);

        truncated.Should().BeFalse();
        fit.Should().HaveCount(2);
        Encoding.UTF8.GetByteCount(fit.ToString(Formatting.None)).Should().Be(exact);
    }

    [Fact]
    public void Fit_放不下的那筆與之後的都不加_回截斷_順序不變()
    {
        var entries = new[] { Entry("a"), Entry("檔"), Entry("b") };

        var (fit, truncated) = RemoteListRules.Fit(entries, 2 + 12 + 1 + 14 - 1);

        truncated.Should().BeTrue();
        fit.Select(e => (string?)e["name"]).Should().Equal("a");
    }

    [Fact]
    public void Fit_空的不截斷_上限小於中括號時有東西就截斷()
    {
        var (none, noneTruncated) = RemoteListRules.Fit([], 0);
        none.Should().BeEmpty();
        noneTruncated.Should().BeFalse();

        var (fit, truncated) = RemoteListRules.Fit([Entry("a")], 2);
        fit.Should().BeEmpty();
        truncated.Should().BeTrue();
    }

    [Fact]
    public void Fit_三千筆長路徑的資料夾_序列化後不超過700KB()
    {
        var longName = new string('長', 200);
        var entries = Enumerable.Range(0, 3000).Select(i => new JObject
        {
            ["name"] = $"{longName}{i}.png",
            ["path"] = $@"C:\{longName}\{longName}{i}.png",
            ["kind"] = "image",
        });

        var (fit, truncated) = RemoteListRules.Fit(entries, RemoteListRules.MaxOutputBytes);

        truncated.Should().BeTrue();
        fit.Count.Should().BeGreaterThan(0).And.BeLessThan(3000);
        Encoding.UTF8.GetByteCount(fit.ToString(Formatting.None)).Should().BeLessThanOrEqualTo(RemoteListRules.MaxOutputBytes);
        RemoteListRules.MaxOutputBytes.Should().Be(700 * 1024);
    }

    [Theory]
    [InlineData(@"C:\proj\sub\deep", @"C:\proj\sub")]
    [InlineData(@"c:\proj\sub", @"C:\proj")]
    [InlineData(@"C:\proj", @"C:\")]
    [InlineData(@"C:/proj/sub/", @"C:\proj")]
    public void Parent_去掉最後一段(string path, string expected)
        => RemoteListRules.Parent(path, [@"D:\other"]).Should().Be(expected);

    [Theory]
    [InlineData(@"C:\proj")]
    [InlineData(@"c:\PROJ\")]
    [InlineData(@"C:\proj\sub\..")]
    public void Parent_在範圍的根是null(string path)
        => RemoteListRules.Parent(path, [null, @"D:\x", @"C:\Proj"]).Should().BeNull();

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"relative\path")]
    [InlineData(@"\\server\share\a")]
    [InlineData(@"C:\..\x")]
    public void Parent_磁碟根目錄與不合格的路徑是null(string path)
        => RemoteListRules.Parent(path, []).Should().BeNull();

    [Theory]
    [InlineData(RemoteFileGuardReasons.Scope)]
    [InlineData(RemoteFileGuardReasons.Forbidden)]
    [InlineData(RemoteFileGuardReasons.Unc)]
    [InlineData(RemoteFileGuardReasons.Device)]
    [InlineData(RemoteFileGuardReasons.Ads)]
    [InlineData(RemoteFileGuardReasons.Invalid)]
    [InlineData(RemoteFileGuardReasons.Drive)]
    [InlineData(RemoteFileGuardReasons.ShortName)]
    [InlineData(RemoteFileGuardReasons.FinalPath)]
    [InlineData(RemoteFileGuardReasons.Reparse)]
    public void RefusesPath_路徑不能給遠端的原因(string reason)
        => RemoteListRules.RefusesPath(reason).Should().BeTrue();

    [Theory]
    [InlineData(RemoteFileGuardReasons.NotFound)]
    [InlineData(RemoteFileGuardReasons.NotFile)]
    [InlineData(RemoteFileGuardReasons.NotDirectory)]
    [InlineData(RemoteFileGuardReasons.Unreadable)]
    [InlineData(RemoteFileGuardReasons.Timeout)]
    [InlineData(RemoteFileGuardReasons.Name)]
    [InlineData(RemoteFileGuardReasons.Type)]
    [InlineData(RemoteFileGuardReasons.HardLink)]
    [InlineData(RemoteFileGuardReasons.TooLarge)]
    [InlineData("something_new")]
    public void RefusesPath_檔案本身的問題不算(string reason)
        => RemoteListRules.RefusesPath(reason).Should().BeFalse();

    [Theory]
    [InlineData(RemoteFileGuardReasons.NotFound, "找不到資料夾")]
    [InlineData(RemoteFileGuardReasons.NotDirectory, "這不是資料夾")]
    [InlineData(RemoteFileGuardReasons.Timeout, "讀取資料夾逾時")]
    [InlineData(RemoteFileGuardReasons.Unreadable, "無法讀取這個資料夾")]
    [InlineData("whatever", "無法讀取這個資料夾")]
    public void ListError_文字(string reason, string expected)
        => RemoteListRules.ListError(reason).Should().Be(expected);

    [Fact]
    public void Version_UTC的ticks_本地時間先換成UTC_0回null()
    {
        var utc = new DateTime(2026, 10, 21, 1, 2, 3, DateTimeKind.Utc);

        RemoteListRules.Version(utc).Should().Be(utc.Ticks);
        RemoteListRules.Version(utc.ToLocalTime()).Should().Be(utc.Ticks);
        RemoteListRules.Version(DateTime.MinValue).Should().BeNull();
        RemoteListRules.FetchRefused.Should().Be("這個檔案不能從網頁讀取");
    }
}
