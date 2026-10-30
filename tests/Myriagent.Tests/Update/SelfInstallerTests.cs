using Myriagent.Tests.TestSupport;
using Myriagent.UI;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Update;

public sealed class SelfInstallerTests
{
    /// <summary>WriteRelease 寫出的發佈檔（framework-dependent、RID 專屬的發佈佈局，同 src/Myriagent 的 publish）</summary>
    private static readonly string[] ReleaseFiles =
    {
        "Myriagent.exe", "Myriagent.dll", "Myriagent.pdb", "Myriagent.deps.json", "Myriagent.runtimeconfig.json",
        "Newtonsoft.Json.dll", "WebView2Loader.dll", "runtimes/win-x64/native/WebView2Loader.dll",
    };

    /// <summary>manifest 列出的檔（從別台電腦拿來的版本資料夾）</summary>
    private static readonly string[] ManifestListed = { "Myriagent.exe", "Myriagent.dll", "Myriagent.deps.json", "Myriagent.runtimeconfig.json" };

    private static ReleaseIdentity Id(string release)
    {
        ReleaseIdentity.TryParseRelease(release, out var id).Should().BeTrue();
        return id!;
    }

    /// <summary>在 temp 的 folder 底下寫出一份最小的發佈（deps.json 列出 Newtonsoft.Json 與 WebView2Loader）；exe 的內容是 marker</summary>
    private static string WriteRelease(TempDirectory temp, string folder, string marker)
    {
        var dir = temp.CreateDirectory(folder);
        var deps = new JObject
        {
            ["runtimeTarget"] = new JObject { ["name"] = ".NETCoreApp,Version=v10.0/win-x64" },
            ["targets"] = new JObject
            {
                [".NETCoreApp,Version=v10.0"] = new JObject(),
                [".NETCoreApp,Version=v10.0/win-x64"] = new JObject
                {
                    ["Myriagent/0.2.0"] = new JObject { ["runtime"] = new JObject { ["Myriagent.dll"] = new JObject() } },
                    ["Newtonsoft.Json/13.0.4"] = new JObject { ["runtime"] = new JObject { ["lib/net6.0/Newtonsoft.Json.dll"] = new JObject() } },
                    ["Microsoft.Web.WebView2/1.0.4191.47"] = new JObject
                    {
                        ["native"] = new JObject { ["runtimes/win-x64/native/WebView2Loader.dll"] = new JObject() },
                    },
                },
            },
        };
        foreach (var file in ReleaseFiles)
        {
            var content = file switch
            {
                "Myriagent.exe" => marker,
                "Myriagent.deps.json" => deps.ToString(),
                _ => file,
            };
            temp.WriteFile(Path.Combine(folder, file.Replace('/', Path.DirectorySeparatorChar)), content);
        }
        return dir;
    }

    /// <summary>在發佈資料夾放一份 manifest（列出 ManifestListed，大小與雜湊照目前的內容）</summary>
    private static void WriteManifest(string dir, string release)
    {
        var files = ManifestListed.Select(f => ReleaseFixtures.File(f, File.ReadAllText(Path.Combine(dir, f)))).ToList();
        File.WriteAllBytes(Path.Combine(dir, AppUpdateManager.ManifestFileName), ReleaseFixtures.ManifestBytes(release, files));
    }

    /// <summary>資料夾裡的檔（相對路徑、斜線分隔）</summary>
    private static IEnumerable<string> FilesIn(string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/'));

    [Fact]
    public void 安裝_複製到版本資料夾_移除網路來源標記_建立junction_記下目前版()
    {
        using var temp = new TempDirectory("install");
        var source = WriteRelease(temp, "download", "exe");
        var exe = Path.Combine(source, "Myriagent.exe");
        File.WriteAllText(exe + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        File.Exists(exe + ":Zone.Identifier").Should().BeTrue("測試前提：NTFS 替代資料流");
        var layout = new InstallLayout(temp.Combine("Programs", "Myriagent"));

        var installed = SelfInstaller.Install(source, layout, Id("0.2.5-abcdef1"), registerShell: false);

        installed.Should().Be(layout.CurrentExecutable);
        var dir = layout.VersionDirectory("0.2.5-abcdef1");
        File.ReadAllText(Path.Combine(dir, "Newtonsoft.Json.dll")).Should().Be("Newtonsoft.Json.dll");
        File.ReadAllText(Path.Combine(dir, "runtimes", "win-x64", "native", "WebView2Loader.dll")).Should().Be("runtimes/win-x64/native/WebView2Loader.dll");
        File.Exists(Path.Combine(dir, "Myriagent.exe") + ":Zone.Identifier").Should().BeFalse("SmartScreen 會擋有網路來源標記的檔");
        Junction.GetTarget(layout.Current).Should().BeEquivalentTo(dir);
        UpdaterStateStore.Load(layout).Current.Should().Be("0.2.5-abcdef1");
    }

    [Fact]
    public void 已有舊版安裝_改指向新版_舊版成為前一版()
    {
        using var temp = new TempDirectory("install");
        var layout = new InstallLayout(temp.Combine("Programs", "Myriagent"));
        var old = WriteRelease(temp, "old", "old");
        SelfInstaller.Install(old, layout, Id("0.2.4-1111111"), registerShell: false);
        var fresh = WriteRelease(temp, "new", "new");

        SelfInstaller.Install(fresh, layout, Id("0.2.5-2222222"), registerShell: false);

        Junction.GetTarget(layout.Current).Should().BeEquivalentTo(layout.VersionDirectory("0.2.5-2222222"));
        var state = UpdaterStateStore.Load(layout);
        state.Current.Should().Be("0.2.5-2222222");
        state.Previous.Should().Be("0.2.4-1111111");
    }

    [Fact]
    public void 解壓在下載或桌面_資料夾裡有不相干的檔案與子資料夾_只複製發佈自己的檔()
    {
        using var temp = new TempDirectory("install");
        // 用 7-Zip「解壓縮到這裡」解到「下載」：發佈的檔和使用者自己的檔、其他程式的資料夾放在一起
        var source = WriteRelease(temp, "Downloads", "exe");
        temp.WriteFile(Path.Combine("Downloads", "私人文件.docx"), "機密");
        temp.WriteFile(Path.Combine("Downloads", "照片", "假期.jpg"), "jpg");
        temp.WriteFile(Path.Combine("Downloads", "publish", "Myriagent.exe"), "另一份建置");
        temp.WriteFile(Path.Combine("Downloads", "其他程式.dll"), "dll");
        var layout = new InstallLayout(temp.Combine("Programs", "Myriagent"));

        SelfInstaller.Install(source, layout, Id("0.2.5-abcdef1"), registerShell: false);

        FilesIn(layout.VersionDirectory("0.2.5-abcdef1")).Should().BeEquivalentTo(ReleaseFiles, "只複製 deps.json 推出的發佈檔，資料夾裡其他東西一律不碰");
    }

    [Fact]
    public void 資料夾裡有manifest_照清單複製並驗大小與雜湊()
    {
        using var temp = new TempDirectory("install");
        // 從別台電腦拿來的版本資料夾（有 manifest 與簽章）
        var source = WriteRelease(temp, "copied", "exe");
        WriteManifest(source, "0.2.5-abcdef1");
        File.WriteAllText(Path.Combine(source, AppUpdateManager.SignatureFileName), "{}");
        temp.WriteFile(Path.Combine("copied", "不相干.txt"), "x");
        var layout = new InstallLayout(temp.Combine("Programs", "Myriagent"));

        SelfInstaller.Install(source, layout, Id("0.2.5-abcdef1"), registerShell: false);

        FilesIn(layout.VersionDirectory("0.2.5-abcdef1")).Should().BeEquivalentTo(
            ManifestListed.Append(AppUpdateManager.ManifestFileName).Append(AppUpdateManager.SignatureFileName), "有 manifest 就以它的清單為準");
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("otherRelease")]
    public void manifest與資料夾不符_拒絕而且什麼都沒裝(string kind)
    {
        using var temp = new TempDirectory("install");
        var source = WriteRelease(temp, "copied", "exe");
        WriteManifest(source, kind == "otherRelease" ? "0.2.6-1234567" : "0.2.5-abcdef1");
        if (kind == "changed")
            File.WriteAllText(Path.Combine(source, "Myriagent.dll"), "被改過");
        else if (kind == "missing")
            File.Delete(Path.Combine(source, "Myriagent.dll"));
        var layout = new InstallLayout(temp.Combine("Programs", "Myriagent"));

        var act = () => SelfInstaller.Install(source, layout, Id("0.2.5-abcdef1"), registerShell: false);

        act.Should().Throw<InvalidDataException>().WithMessage("*請解壓到一個空的資料夾*");
        Directory.Exists(layout.VersionDirectory("0.2.5-abcdef1")).Should().BeFalse();
        Directory.Exists(layout.PartialVersionDirectory("0.2.5-abcdef1")).Should().BeFalse();
        Junction.GetTarget(layout.Current).Should().BeNull();
    }

    [Fact]
    public void 不是完整的發佈資料夾_拒絕而且什麼都沒裝()
    {
        using var temp = new TempDirectory("install");
        var source = temp.CreateDirectory("partial");
        temp.WriteFile(Path.Combine("partial", "Myriagent.exe"), "只有 exe");
        var layout = new InstallLayout(temp.Combine("Programs", "Myriagent"));

        var act = () => SelfInstaller.Install(source, layout, Id("0.2.5-abcdef1"), registerShell: false);

        act.Should().Throw<InvalidDataException>().WithMessage("*Myriagent.deps.json*");
        Directory.Exists(layout.VersionDirectory("0.2.5-abcdef1")).Should().BeFalse();
        Junction.GetTarget(layout.Current).Should().BeNull();
    }

    [Fact]
    public void 何時提供安裝()
    {
        using var temp = new TempDirectory("install");
        var layout = new InstallLayout(temp.Combine("Programs", "Myriagent"));
        SelfInstaller.CanOfferInstall(null, Id("0.2.5-abcdef1"), layout).Should().BeTrue("還沒安裝");
        SelfInstaller.CanOfferInstall("0.2.5-abcdef1", Id("0.2.5-abcdef1"), layout).Should().BeFalse("已經從安裝位置執行");
        SelfInstaller.CanOfferInstall(null, new ReleaseIdentity(Id("0.2.5-abcdef1").Version, ReleaseIdentity.DevBuild), layout).Should().BeFalse("開發版不能安裝");

        var v = WriteRelease(temp, "v", "x");
        SelfInstaller.Install(v, layout, Id("0.2.6-1111111"), registerShell: false);
        SelfInstaller.CanOfferInstall(null, Id("0.2.5-abcdef1"), layout).Should().BeFalse("安裝的比較新");
        SelfInstaller.CanOfferInstall(null, Id("0.2.7-2222222"), layout).Should().BeTrue("安裝的比較舊");
    }

    [Fact]
    public void 捷徑帶AppUserModelID()
    {
        using var temp = new TempDirectory("shortcut");
        var target = temp.WriteFile("app.exe", "x");
        var link = temp.Combine("Clode Myriagent.lnk");

        ShellLink.Create(link, target, temp.Root, ShellLink.AppUserModelId, "Clode Myriagent");

        var (readTarget, appId) = ShellLink.Read(link);
        readTarget.Should().BeEquivalentTo(target);
        appId.Should().Be("Myriagent");
    }

    [Fact]
    public void 刪除替代資料流_有就刪掉_沒有也回true()
    {
        using var temp = new TempDirectory("install");
        var file = temp.WriteFile("a.exe", "x");
        File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");

        NativeMethods.DeleteAlternateStream(file, "Zone.Identifier").Should().BeTrue();
        File.Exists(file + ":Zone.Identifier").Should().BeFalse();
        File.ReadAllText(file).Should().Be("x", "只刪替代資料流，檔案本身不動");
        NativeMethods.DeleteAlternateStream(file, "Zone.Identifier").Should().BeTrue("沒有這個資料流也算成功");
    }
}
