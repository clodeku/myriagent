using Myriagent.Core;
using Myriagent.Launch;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Launch;

/// <summary>
/// 執行列記得的選擇（launch\prefs.json）：每個資料夾選的專案、每個專案的執行模式；最近用的在前、有上限、沒變不重寫；
/// 不認得的模式與不是目標鍵的選擇當成沒記；哪些階段的程式會跟著 app 結束（Debug 模式執行中、暫停中）
/// </summary>
public sealed class LaunchPrefsRulesTests
{
    private static string Id(string path) => LaunchRules.TargetId(path);

    [Fact]
    public void 沒記過_選擇是null_模式是Debug()
    {
        var prefs = new LaunchPrefsFile();
        LaunchRules.SelectedFromPrefs(prefs, @"C:\repo\a").Should().BeNull();
        LaunchRules.ModeFromPrefs(prefs, Id(@"C:\repo\a\A.csproj")).Should().Be(LaunchMode.Debug);
        LaunchRules.DefaultMode.Should().Be(LaunchMode.Debug);
    }

    [Fact]
    public void 記下選擇_資料夾不分大小寫_最近的在前_沒變回false()
    {
        var prefs = new LaunchPrefsFile();
        var a = Id(@"C:\repo\a\A.csproj");
        var b = Id(@"C:\repo\a\B.csproj");
        LaunchRules.RememberSelection(prefs, @"C:\repo\a", a).Should().BeTrue();
        LaunchRules.RememberSelection(prefs, @"C:\repo\a", a).Should().BeFalse("已經是最前面的同一筆：不必重寫");
        LaunchRules.RememberSelection(prefs, @"C:\repo\other", b).Should().BeTrue();
        LaunchRules.SelectedFromPrefs(prefs, @"c:\REPO\a").Should().Be(a, "資料夾不分大小寫");

        LaunchRules.RememberSelection(prefs, @"C:\REPO\A", b).Should().BeTrue();
        prefs.Folders.Should().HaveCount(2, "同一個資料夾只留一筆");
        prefs.Folders[0].Selected.Should().Be(b, "最近的在前");
        LaunchRules.SelectedFromPrefs(prefs, @"C:\repo\a").Should().Be(b);
    }

    [Fact]
    public void 記下模式_每個專案各自_沒變回false()
    {
        var prefs = new LaunchPrefsFile();
        var a = Id(@"C:\repo\a\A.csproj");
        var b = Id(@"C:\repo\b\B.csproj");
        LaunchRules.RememberMode(prefs, a, LaunchMode.Release).Should().BeTrue();
        LaunchRules.RememberMode(prefs, a, LaunchMode.Release).Should().BeFalse();
        LaunchRules.ModeFromPrefs(prefs, a).Should().Be(LaunchMode.Release);
        LaunchRules.ModeFromPrefs(prefs, b).Should().Be(LaunchMode.Debug, "別的專案不受影響");

        LaunchRules.RememberMode(prefs, b, LaunchMode.Release).Should().BeTrue();
        LaunchRules.RememberMode(prefs, a, LaunchMode.Debug).Should().BeTrue();
        prefs.Targets.Select(t => (t.TargetId, t.Mode)).Should().Equal((a, "debug"), (b, "release"));
    }

    [Fact]
    public void 有上限_舊的丟掉()
    {
        var prefs = new LaunchPrefsFile();
        for (var i = 0; i < LaunchRules.MaxPrefs + 5; i++)
        {
            var path = $@"C:\repo\p{i}\App.csproj";
            LaunchRules.RememberSelection(prefs, $@"C:\repo\p{i}", Id(path));
            LaunchRules.RememberMode(prefs, Id(path), LaunchMode.Release);
        }
        prefs.Folders.Should().HaveCount(LaunchRules.MaxPrefs);
        prefs.Targets.Should().HaveCount(LaunchRules.MaxPrefs);
        LaunchRules.SelectedFromPrefs(prefs, @"C:\repo\p0").Should().BeNull("最舊的丟掉");
        LaunchRules.ModeFromPrefs(prefs, Id(@"C:\repo\p0\App.csproj")).Should().Be(LaunchMode.Debug);
        LaunchRules.SelectedFromPrefs(prefs, $@"C:\repo\p{LaunchRules.MaxPrefs + 4}").Should().NotBeNull();
    }

    [Fact]
    public void 檔案裡壞的內容當成沒記()
    {
        var a = Id(@"C:\repo\a\A.csproj");
        var prefs = new LaunchPrefsFile
        {
            Folders = [null!, new LaunchFolderPref { Folder = @"C:\repo\a", Selected = @"..\..\evil" }, new LaunchFolderPref { Folder = null, Selected = a }],
            Targets = [null!, new LaunchTargetPref { TargetId = a, Mode = "profile" }],
        };
        LaunchRules.SelectedFromPrefs(prefs, @"C:\repo\a").Should().BeNull("不是目標的鍵");
        LaunchRules.ModeFromPrefs(prefs, a).Should().Be(LaunchMode.Debug, "之後的版本可能新增模式：不認得的當成沒記");
        LaunchRules.RememberSelection(prefs, @"C:\repo\a", a).Should().BeTrue();
        prefs.Folders.Should().NotContainNulls("記的時候順便清掉");
        LaunchRules.SelectedFromPrefs(prefs, @"C:\repo\a").Should().Be(a);
    }

    [Fact]
    public void 寫進檔案再讀回來一樣()
    {
        using var temp = new TempDirectory("launch-prefs");
        var a = Id(@"C:\repo\a\A.csproj");
        var prefs = new LaunchPrefsFile();
        LaunchRules.RememberSelection(prefs, @"C:\repo\a", a);
        LaunchRules.RememberMode(prefs, a, LaunchMode.Release);
        var path = temp.Combine(LaunchRules.PrefsFileName);
        JsonFile.Write(path, prefs);
        var back = JsonFile.Read<LaunchPrefsFile>(path)!;
        LaunchRules.SelectedFromPrefs(back, @"C:\repo\a").Should().Be(a);
        LaunchRules.ModeFromPrefs(back, a).Should().Be(LaunchMode.Release);
        File.ReadAllText(path).Should().Contain("\"release\"", "模式存名稱（前一版讀到不認得的列舉名稱會整份丟掉）");
    }

    [Fact]
    public void 跟著app結束的只有Debug模式執行中與暫停中()
    {
        LaunchRules.DiesWithApp(LaunchPhase.Running, LaunchMode.Debug).Should().BeTrue();
        LaunchRules.DiesWithApp(LaunchPhase.Paused, LaunchMode.Debug).Should().BeTrue();
        LaunchRules.DiesWithApp(LaunchPhase.Running, LaunchMode.Release).Should().BeFalse("Release 模式和 app 脫鉤");
        LaunchRules.DiesWithApp(LaunchPhase.Paused, LaunchMode.Release).Should().BeFalse();
        LaunchRules.DiesWithApp(LaunchPhase.Building, LaunchMode.Debug).Should().BeFalse("建置由 BlocksAppRestart 管");
        LaunchRules.DiesWithApp(LaunchPhase.Stopping, LaunchMode.Debug).Should().BeFalse();
        LaunchRules.DiesWithApp(LaunchPhase.Idle, LaunchMode.Debug).Should().BeFalse();
        foreach (var phase in Enum.GetValues<LaunchPhase>())
        {
            foreach (var mode in Enum.GetValues<LaunchMode>())
                _ = LaunchRules.DiesWithApp(phase, mode);
        }
    }
}
