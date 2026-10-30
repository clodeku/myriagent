using System.Globalization;
using Myriagent.Update;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Releases;

namespace MyriagentHub.Tests.Releases;

/// <summary>
/// 桌面版發佈在伺服器上的純函式規則（U2 T3）：最新版依版本號、收件只跟可用版本比、保存（撤回與清除的不佔名額）、
/// 設定驗證的邊界、min_runtime 的文字、套件端點開 zip 失敗的分類
/// </summary>
public sealed class AppReleaseRulesTests
{
    private static AppReleaseRecord Row(string release, string state = AppReleaseStates.Available)
    {
        ReleaseIdentity.TryParseRelease(release, out var parsed).Should().BeTrue(release);
        var identity = parsed!;
        return new AppReleaseRecord { Release = release, Version = identity.Version.ToString(), Build = identity.Build, State = state };
    }

    private static ReleaseVersion V(string text)
    {
        ReleaseVersion.TryParse(text, out var version).Should().BeTrue(text);
        return version;
    }

    [Fact]
    public void 最新版依版本號_0_2_999比0_2_1000舊()
    {
        AppReleaseRules.PickLatest([Row("0.2.999-aaaaaaa"), Row("0.2.1000-bbbbbbb")])!.Release.Should().Be("0.2.1000-bbbbbbb", "數字比較，不是字串比較");
        AppReleaseRules.PickLatest([Row("0.9.5-aaaaaaa"), Row("0.10.0-bbbbbbb"), Row("0.2.999-ccccccc")])!.Release.Should().Be("0.10.0-bbbbbbb");
        AppReleaseRules.PickLatest([Row("0.2.999-aaaaaaa"), Row("0.2.1000-bbbbbbb", AppReleaseStates.Withdrawn), Row("0.2.1001-ccccccc", AppReleaseStates.Pruned)])!
            .Release.Should().Be("0.2.999-aaaaaaa", "撤回與清除的版本不提供");
        AppReleaseRules.PickLatest([Row("0.2.5-aaaaaaa", AppReleaseStates.Withdrawn)]).Should().BeNull();
        AppReleaseRules.PickLatest([]).Should().BeNull();
    }

    [Fact]
    public void 撤回與清除的版本不佔保留名額()
    {
        var rows = new List<AppReleaseRecord>
        {
            Row("0.2.10-aaaaaa0"), Row("0.2.11-aaaaaa1"), Row("0.2.12-aaaaaa2"), Row("0.2.13-aaaaaa3"), Row("0.2.14-aaaaaa4"),
            Row("0.2.15-aaaaaa5", AppReleaseStates.Withdrawn), Row("0.2.9-aaaaaa9", AppReleaseStates.Withdrawn),
        };
        AppReleaseRules.FoldersToDelete(rows, keepAvailable: 3).Should().BeEquivalentTo(
            new[] { "0.2.10-aaaaaa0", "0.2.11-aaaaaa1", "0.2.15-aaaaaa5", "0.2.9-aaaaaa9" },
            "5 個可用留最新的 3 個（0.2.12～0.2.14）、刪 2 個最舊的；2 個撤回的不佔名額，資料夾都刪");

        rows.Add(Row("0.2.8-aaaaaa8", AppReleaseStates.Pruned));
        rows.Add(new AppReleaseRecord { Release = "0.2.7-aaaaaa7", State = "future-state" });
        var withPruned = AppReleaseRules.FoldersToDelete(rows, keepAvailable: 3);
        withPruned.Should().Contain("0.2.8-aaaaaa8", "已經 pruned 的版本，資料夾還在就刪（純函式看不到磁碟，呼叫端略過不存在的）");
        withPruned.Should().NotContain("0.2.7-aaaaaa7", "不認得的狀態（例如回滾前的新版寫的）不碰");
        withPruned.Should().HaveCount(5);
        AppReleaseRules.FoldersToDelete(rows, keepAvailable: 5).Should().BeEquivalentTo(new[] { "0.2.15-aaaaaa5", "0.2.9-aaaaaa9", "0.2.8-aaaaaa8" });
        AppReleaseRules.FoldersToDelete(rows, keepAvailable: 1).Should().NotContain("0.2.14-aaaaaa4", "最新的可用版本一定留著")
            .And.HaveCount(7);
    }

    [Fact]
    public void 版本比較只看可用版本()
    {
        var rows = new[] { Row("0.2.298-aaaaaaa"), Row("0.2.300-bbbbbbb", AppReleaseStates.Withdrawn), Row("0.2.305-ccccccc", AppReleaseStates.Pruned) };
        AppReleaseRules.IsNewerThanAvailable(V("0.2.301"), rows).Should().BeTrue("撤回 0.2.300 之後，0.2.301 算比較新");
        AppReleaseRules.IsNewerThanAvailable(V("0.2.299"), rows).Should().BeTrue("撤回與清除的不算：只跟還可用的 0.2.298 比");
        AppReleaseRules.IsNewerThanAvailable(V("0.2.298"), rows).Should().BeFalse("跟可用版本同號不算比較新");
        AppReleaseRules.IsNewerThanAvailable(V("0.2.297"), rows).Should().BeFalse();
        AppReleaseRules.IsNewerThanAvailable(V("0.1.0"), []).Should().BeTrue("還沒有可用版本");
    }

    [Fact]
    public void 預設設定合格()
    {
        var settings = new ReleaseSettings();
        settings.InboxPollSeconds.Should().Be(30);
        settings.KeepAvailable.Should().Be(3);
        settings.MaxZipMb.Should().Be(256);
        settings.MinFreeDiskMb.Should().Be(2048);
        AppReleaseRules.ValidateSettings(settings).Should().BeNull();
        new HubOptions().Releases.Should().BeEquivalentTo(settings, "HubOptions.Releases 預設就是這組值");
    }

    [Theory]
    [InlineData("InboxPollSeconds", 1, true)]
    [InlineData("InboxPollSeconds", 3600, true)]
    [InlineData("InboxPollSeconds", 0, false)]
    [InlineData("InboxPollSeconds", 3601, false)]
    [InlineData("KeepAvailable", 1, true)]
    [InlineData("KeepAvailable", 20, true)]
    [InlineData("KeepAvailable", 0, false)]
    [InlineData("KeepAvailable", 21, false)]
    [InlineData("MaxZipMb", 1, true)]
    [InlineData("MaxZipMb", 4096, true)]
    [InlineData("MaxZipMb", 0, false)]
    [InlineData("MaxZipMb", 4097, false)]
    [InlineData("MinFreeDiskMb", 0, true)]
    [InlineData("MinFreeDiskMb", int.MaxValue, true)]
    [InlineData("MinFreeDiskMb", -1, false)]
    public void 設定驗證(string field, int value, bool ok)
    {
        var settings = new ReleaseSettings();
        switch (field)
        {
            case "InboxPollSeconds":
                settings.InboxPollSeconds = value;
                break;
            case "KeepAvailable":
                settings.KeepAvailable = value;
                break;
            case "MaxZipMb":
                settings.MaxZipMb = value;
                break;
            case "MinFreeDiskMb":
                settings.MinFreeDiskMb = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
        var error = AppReleaseRules.ValidateSettings(settings);
        if (ok)
        {
            error.Should().BeNull();
        }
        else
        {
            error.Should().NotBeNull().And.StartWith("Hub:Releases:" + field + " ").And.EndWith("：" + value.ToString(CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void 開zip失敗_再讀的資料列不是可用回410_檔案或資料夾不在回410_其他開檔失敗回503()
    {
        Exception[] missing = [new FileNotFoundException("zip 不在"), new DirectoryNotFoundException("版本資料夾不在")];
        Exception[] busy = [new IOException("共用違規", unchecked((int)0x80070020)), new UnauthorizedAccessException("刪除待決"), new PathTooLongException()];
        foreach (var state in new[] { AppReleaseStates.Withdrawn, AppReleaseStates.Pruned, null, "future-state" })
        {
            foreach (var error in missing.Concat(busy))
            {
                AppReleaseRules.ClassifyOpenFailure(state, error).Should().Be(AppReleaseOpenFailure.NoLongerAvailable,
                    $"讀資料列與開檔之間被撤回或清除（{state ?? "沒有這一列"}）：刪除中的檔案丟的不一定是 FileNotFoundException（{error.GetType().Name}）");
            }
        }
        foreach (var error in missing)
        {
            AppReleaseRules.ClassifyOpenFailure(AppReleaseStates.Available, error).Should().Be(AppReleaseOpenFailure.Missing,
                $"仍是可用版本、檔案或資料夾不在：{error.GetType().Name}（DirectoryNotFoundException 也是 IOException，不能落到暫時開不了）");
        }
        foreach (var error in busy)
        {
            AppReleaseRules.ClassifyOpenFailure(AppReleaseStates.Available, error).Should().Be(AppReleaseOpenFailure.Busy,
                $"仍是可用版本、檔案也在，只是暫時開不了：{error.GetType().Name}");
        }
    }

    [Fact]
    public void 最低執行階段的文字() =>
        AppReleaseRules.MinRuntimeText(new ReleaseRuntime("Microsoft.WindowsDesktop.App", new ReleaseVersion(10, 0, 0)))
            .Should().Be("Microsoft.WindowsDesktop.App 10.0.0");
}
