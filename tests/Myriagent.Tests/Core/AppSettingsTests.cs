using Myriagent.Core;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Core;

/// <summary>設定檔讀取：讀不到（被占用）時這次不存檔、原檔不動；損毀時備份原檔、改用預設值；兩種都提示使用者</summary>
[Collection(AppPathsCollection.Name)]
public sealed class AppSettingsTests : IDisposable
{
    private const string Saved = """{ "Theme": "light", "FontSize": 17, "Instructions": "我的自訂說明：回覆前先列計畫", "RecentFolders": [ "D:\\work\\proj" ] }""";

    private readonly TempDirectory temp = new("settings");
    private readonly AppPathsScope scope;

    public AppSettingsTests() => scope = new AppPathsScope(temp.Root);

    public void Dispose()
    {
        scope.Dispose();
        temp.Dispose();
    }

    [Fact]
    public void 正常讀回_不存在時用預設值_都沒有提示()
    {
        var fresh = AppSettings.Load();
        fresh.Theme.Should().Be("dark");
        fresh.StartupWarning.Should().BeNull();
        fresh.IsReadOnly.Should().BeFalse();

        File.WriteAllText(AppPaths.SettingsFile, Saved);
        var loaded = AppSettings.Load();
        loaded.Theme.Should().Be("light");
        loaded.Instructions.Should().Be("我的自訂說明：回覆前先列計畫");
        loaded.RecentFolders.Should().Equal(@"D:\work\proj");
        loaded.StartupWarning.Should().BeNull();
    }

    [Fact]
    public void 讀不到時用預設值執行_之後存檔不覆寫原檔_並提示()
    {
        File.WriteAllText(AppPaths.SettingsFile, Saved);
        var before = File.ReadAllBytes(AppPaths.SettingsFile);

        AppSettings settings;
        using (new FileStream(AppPaths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            settings = AppSettings.Load();

        settings.IsReadOnly.Should().BeTrue();
        settings.Theme.Should().Be("dark", "讀不到只能先用預設值");
        settings.StartupWarning.Should().Contain("暫時讀不到");
        settings.Layout = new Newtonsoft.Json.Linq.JObject { ["view"] = "chat" };
        settings.Save();
        File.ReadAllBytes(AppPaths.SettingsFile).Should().Equal(before, "讀不到時絕不能用預設值蓋掉使用者的設定");
        File.Exists(AppPaths.SettingsFile + ".tmp").Should().BeFalse();
        AppSettings.Load().Instructions.Should().Be("我的自訂說明：回覆前先列計畫", "下次啟動讀得到時設定都還在");
    }

    [Fact]
    public void CLI重開政策_預設安全點_存檔讀回_高風險清單可增減()
    {
        var fresh = AppSettings.Load();
        fresh.CliRestartPolicy.Should().Be(CliRestartPolicy.SafePoint, "預設在安全點立即切換並自動接續");
        fresh.CliRestartRiskExtra.Should().BeEmpty();
        fresh.CliRestartRiskDisabled.Should().BeEmpty();

        fresh.CliRestartPolicy = CliRestartPolicy.AfterTurn;
        fresh.CliRestartRiskExtra.Add(@"\bfly\s+deploy\b");
        fresh.CliRestartRiskDisabled.Add("kubectl");
        fresh.Save();

        File.ReadAllText(AppPaths.SettingsFile).Should().Contain("\"CliRestartPolicy\": \"AfterTurn\"", "存成名稱，手改設定檔看得懂");
        var loaded = AppSettings.Load();
        loaded.CliRestartPolicy.Should().Be(CliRestartPolicy.AfterTurn);
        loaded.CliRestartRiskExtra.Should().Equal(@"\bfly\s+deploy\b");
        loaded.CliRestartRiskDisabled.Should().Equal("kubectl");
    }

    [Theory]
    [InlineData("\"Bogus\"")]
    [InlineData("\"\"")]
    [InlineData("7")]
    [InlineData("null")]
    [InlineData("{ \"x\": 1 }")]
    [InlineData("[1, 2]")]
    public void CLI重開政策_不認得的值當成預設_不會讓整份設定被當成損毀(string value)
    {
        File.WriteAllText(AppPaths.SettingsFile, $$"""{ "Theme": "light", "CliRestartPolicy": {{value}}, "FontSize": 16 }""");

        var settings = AppSettings.Load();

        settings.StartupWarning.Should().BeNull("打錯一個值不能把整份設定重設");
        settings.CliRestartPolicy.Should().Be(CliRestartPolicy.SafePoint);
        settings.Theme.Should().Be("light", "其他設定照常讀回");
        settings.FontSize.Should().Be(16, "後面的欄位也照常讀回");
        Directory.GetFiles(temp.Root, "settings.json.corrupt-*").Should().BeEmpty();
    }

    [Fact]
    public void 損毀時備份原檔_改用預設值並提示_之後照常存檔()
    {
        File.WriteAllText(AppPaths.SettingsFile, "{ \"Theme\": \"light\", 寫到一半");

        var settings = AppSettings.Load();

        settings.IsReadOnly.Should().BeFalse();
        settings.StartupWarning.Should().Contain("已損毀").And.Contain(".corrupt-");
        var backup = Directory.GetFiles(temp.Root, "settings.json.corrupt-*").Should().ContainSingle().Subject;
        settings.StartupWarning.Should().Contain(backup);
        settings.Theme = "light";
        settings.Save();
        AppSettings.Load().Theme.Should().Be("light");
        File.ReadAllText(backup).Should().Contain("寫到一半", "備份保留原本的內容");
    }
}
