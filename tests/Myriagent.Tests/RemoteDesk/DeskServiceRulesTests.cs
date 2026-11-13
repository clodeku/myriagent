using Myriagent.Remote;
using Myriagent.Update;
using MyriagentDesk;

namespace Myriagent.Tests.RemoteDesk;

/// <summary>
/// 遠端桌面的鎖定畫面支援的純規則（<see cref="DeskServiceRules"/>、服務的命令列 <see cref="DeskServiceArgs"/>）：
/// 管道協定的一行要求與回覆、誰連得進來、安裝的位置與指紋、要不要更新、安裝器結束碼的說明
/// </summary>
public sealed class DeskServiceRulesTests
{
    private const string UserA = "S-1-5-21-1-2-3-1001";
    private const string UserB = "S-1-5-21-1-2-3-1002";

    private static Dictionary<string, string> Hashes(char fill = 'a')
        => DeskServiceRules.PayloadFiles.ToDictionary(n => n, _ => new string(fill, 64), StringComparer.Ordinal);

    [Fact]
    public void 要求_一行JSON_hello與probe讀得回來()
    {
        DeskServiceRules.TryParseRequest(DeskServiceRules.HelloRequest(), out var op, out var request).Should().BeTrue();
        op.Should().Be(DeskServiceRules.OpHello);
        request!.Value<int>("proto").Should().Be(DeskServiceRules.ProtocolVersion);
        DeskServiceRules.HelloRequest().Should().NotContain("\n");

        DeskServiceRules.TryParseRequest(DeskServiceRules.ProbeRequest(), out op, out _).Should().BeTrue();
        op.Should().Be(DeskServiceRules.OpProbe);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    [InlineData("{\"op\":1}")]
    [InlineData("{\"op\":\"\"}")]
    [InlineData("{\"op\":\"hello\"")]
    public void 要求_不是物件或沒有op_不收(string? line)
        => DeskServiceRules.TryParseRequest(line, out _, out _).Should().BeFalse();

    [Fact]
    public void 要求_超過一行的上限_不收()
        => DeskServiceRules.TryParseRequest("{\"op\":\"hello\",\"pad\":\"" + new string('x', DeskServiceRules.MaxLineChars) + "\"}", out _, out _).Should().BeFalse();

    [Fact]
    public void hello的回覆_來回一致()
    {
        var hello = new DeskServiceHello(1, "0.2.950", "abc1234", true, 2, ["probe"]);
        var parsed = DeskServiceRules.ParseHello(DeskServiceRules.HelloReply(hello));
        parsed.Should().BeEquivalentTo(hello);
        DeskServiceRules.ErrorOf(DeskServiceRules.HelloReply(hello)).Should().BeNull();
    }

    [Fact]
    public void probe的回覆_來回一致_開不了桌面時帶錯誤碼()
    {
        var secure = new DeskServiceProbe(1, "Winlogon", true, 0, ["Default", "Winlogon", "Disconnect"]);
        DeskServiceRules.ParseProbe(DeskServiceRules.ProbeReply(secure)).Should().BeEquivalentTo(secure);

        var denied = new DeskServiceProbe(3, null, false, 5, []);
        DeskServiceRules.ParseProbe(DeskServiceRules.ProbeReply(denied)).Should().BeEquivalentTo(denied);
    }

    [Fact]
    public void 錯誤的回覆_解析成錯誤_不當成hello或probe()
    {
        var reply = DeskServiceRules.ErrorReply(DeskServiceRules.CodeDenied, "不是那個工作階段登入的使用者");
        DeskServiceRules.ErrorOf(reply).Should().Be(new DeskServiceError("denied", "不是那個工作階段登入的使用者"));
        DeskServiceRules.ParseHello(reply).Should().BeNull();
        DeskServiceRules.ParseProbe(reply).Should().BeNull();
        DeskServiceRules.ErrorOf("不是 JSON")!.Code.Should().Be(DeskServiceRules.CodeInvalid);
        DeskServiceRules.ErrorOf("{\"ok\":\"true\"}").Should().NotBeNull("ok 要是布林的 true");
    }

    [Fact]
    public void 幫手的輸出_桌面名稱決定是不是安全桌面()
    {
        var normal = DeskServiceRules.ParseHelperProbe(DeskServiceRules.HelperProbeLine("Default", 0, ["Default"]), 1)!;
        normal.Secure.Should().BeFalse();
        normal.Desktops.Should().Equal("Default");

        var locked = DeskServiceRules.ParseHelperProbe(DeskServiceRules.HelperProbeLine("Winlogon", 0, ["Default", "Winlogon"]), 1)!;
        locked.Should().BeEquivalentTo(new DeskServiceProbe(1, "Winlogon", true, 0, ["Default", "Winlogon"]));

        var failed = DeskServiceRules.ParseHelperProbe(DeskServiceRules.HelperProbeLine(null, 5, []), 4)!;
        failed.Should().BeEquivalentTo(new DeskServiceProbe(4, null, false, 5, []));

        DeskServiceRules.ParseHelperProbe("Unhandled exception. System.Exception", 1).Should().BeNull();
        DeskServiceRules.ParseHelperProbe("{}", 1).Should().BeNull("沒有名稱也沒有錯誤碼");
        DeskServiceRules.ParseHelperProbe(null, 1).Should().BeNull();
    }

    [Theory]
    [InlineData("Default", false)]
    [InlineData("default", false)]
    [InlineData("Winlogon", true)]
    [InlineData("Screen-saver", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 安全桌面_只有Default不算(string? name, bool secure)
        => DeskServiceRules.IsSecureDesktop(name).Should().Be(secure);

    [Fact]
    public void 收不收用戶端_系統權限的服務只收那個工作階段登入的使用者()
    {
        DeskServiceRules.Admit(new DeskServiceClientInfo(1, UserA, UserA), serviceIsSystem: true).Should().BeNull();
        DeskServiceRules.Admit(new DeskServiceClientInfo(1, UserA.ToLowerInvariant(), UserA), true).Should().BeNull("SID 不分大小寫");
        DeskServiceRules.Admit(new DeskServiceClientInfo(1, UserB, UserA), true).Should().NotBeNull("同一個工作階段裡以別的使用者執行的程序（runas）");
        DeskServiceRules.Admit(new DeskServiceClientInfo(0, "S-1-5-18", "S-1-5-18"), true).Should().NotBeNull("工作階段 0 是服務的，沒有使用者的桌面");
        DeskServiceRules.Admit(new DeskServiceClientInfo(2, UserA, null), true).Should().NotBeNull("那個工作階段沒有人登入");
        DeskServiceRules.Admit(new DeskServiceClientInfo(1, null, UserA), true).Should().NotBeNull("查不到用戶端是誰");
        DeskServiceRules.Admit(new DeskServiceClientInfo(1, "", UserA), true).Should().NotBeNull();
    }

    [Fact]
    public void 收不收用戶端_前景模式只收執行它的使用者_不看工作階段()
    {
        DeskServiceRules.Admit(new DeskServiceClientInfo(0, UserA, UserA), serviceIsSystem: false).Should().BeNull();
        DeskServiceRules.Admit(new DeskServiceClientInfo(1, UserB, UserA), false).Should().NotBeNull();
    }

    [Theory]
    [InlineData("MyriagentDesk", true)]
    [InlineData("MyriagentDeskTest-0a1b", true)]
    [InlineData("MyriagentDeskTest-0123456789abcdef", true)]
    [InlineData("MyriagentDeskTest-abc", false)]
    [InlineData("MyriagentDeskTest-0123456789abcdef0", false)]
    [InlineData("MyriagentDeskTest-ABCD", false)]
    [InlineData("MyriagentDeskTest-..\\x", false)]
    [InlineData("Spooler", false)]
    [InlineData("myriagentdesk", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 服務名稱_只收真的與測試用的暫時名稱(string? name, bool ok)
        => DeskServiceRules.IsServiceName(name).Should().Be(ok);

    [Fact]
    public void 安裝位置_真的在DeskService_測試的在旁邊自己的資料夾()
    {
        DeskServiceRules.InstallRoot(@"C:\Program Files", "MyriagentDesk").Should().Be(@"C:\Program Files\Myriagent\DeskService");
        DeskServiceRules.InstallRoot(@"C:\Program Files", "MyriagentDeskTest-0a1b").Should().Be(@"C:\Program Files\Myriagent\MyriagentDeskTest-0a1b");
    }

    [Fact]
    public void 服務檔的指紋_固定_任何一個檔變了就變_少了檔丟例外()
    {
        var a = DeskServiceRules.PayloadHash(Hashes());
        a.Should().MatchRegex("^[0-9a-f]{64}$").And.Be(DeskServiceRules.PayloadHash(Hashes()));

        var changed = Hashes();
        changed["Newtonsoft.Json.dll"] = new string('b', 64);
        DeskServiceRules.PayloadHash(changed).Should().NotBe(a);

        var missing = Hashes();
        missing.Remove("MyriagentDesk.deps.json");
        FluentActions.Invoking(() => DeskServiceRules.PayloadHash(missing)).Should().Throw<ArgumentException>();

        var folder = DeskServiceRules.PayloadFolder(a);
        folder.Should().Be("p-" + a[..16]);
        DeskServiceRules.IsPayloadFolder(folder).Should().BeTrue();
        DeskServiceRules.IsPayloadFolder("logs").Should().BeFalse();
        DeskServiceRules.IsPayloadFolder("p-XYZ").Should().BeFalse();
    }

    [Fact]
    public void 啟動指令_路徑一律加引號()
        => DeskServiceRules.BinPath(@"C:\Program Files\Myriagent\DeskService\p-0123456789abcdef\MyriagentDesk.exe")
            .Should().Be("\"C:\\Program Files\\Myriagent\\DeskService\\p-0123456789abcdef\\MyriagentDesk.exe\" service");

    [Fact]
    public void 安裝紀錄_來回一致_格式不對讀成沒裝()
    {
        var payload = DeskServiceRules.PayloadHash(Hashes());
        var install = new DeskServiceInstall("MyriagentDesk", "0.2.950", "abc1234", payload, DeskServiceRules.PayloadFolder(payload), "0123456789abcdef", "2026-11-13T03:00:00Z");
        DeskServiceRules.ParseInstall(DeskServiceRules.SerializeInstall(install)).Should().Be(install);

        DeskServiceRules.ParseInstall(null).Should().BeNull();
        DeskServiceRules.ParseInstall("{}").Should().BeNull();
        DeskServiceRules.ParseInstall(DeskServiceRules.SerializeInstall(install with { Service = "Spooler" })).Should().BeNull();
        DeskServiceRules.ParseInstall(DeskServiceRules.SerializeInstall(install with { Folder = @"..\..\Windows" })).Should().BeNull();
        DeskServiceRules.ParseInstall(DeskServiceRules.SerializeInstall(install with { Payload = "zz" })).Should().BeNull();
    }

    [Fact]
    public void 要不要更新_指紋一樣不用_發佈版只往新的_開發版看指紋()
    {
        var installed = new DeskServiceInstall("MyriagentDesk", "0.2.950", "abc1234", new string('a', 64), "p-aaaaaaaaaaaaaaaa", null, "");
        DeskServiceRules.NeedsUpdate(installed, "0.2.960", "def5678", new string('a', 64)).Should().BeFalse("服務檔一模一樣（app 換版但服務沒改）");
        DeskServiceRules.NeedsUpdate(installed, "0.2.960", "def5678", new string('b', 64)).Should().BeTrue();
        DeskServiceRules.NeedsUpdate(installed, "0.2.940", "0123456", new string('b', 64)).Should().BeFalse("app 回滾到舊版時不把服務降回去");
        DeskServiceRules.NeedsUpdate(installed, "0.2.0", ReleaseIdentity.DevBuild, new string('b', 64)).Should().BeTrue("開發版只看指紋");
        DeskServiceRules.NeedsUpdate(installed with { Build = ReleaseIdentity.DevBuild }, "0.2.940", "0123456", new string('b', 64)).Should().BeTrue();
    }

    [Fact]
    public void 服務檔對發佈清單_每個檔都要在清單裡而且雜湊相同()
    {
        var hashes = Hashes();
        var files = DeskServiceRules.PayloadFiles.Select(n => new ReleaseFile(n, 10, hashes[n])).Append(new ReleaseFile("Myriagent.exe", 10, new string('c', 64))).ToList();
        var manifest = new ReleaseManifest(new ReleaseIdentity(new ReleaseVersion(0, 2, 950), "abc1234"), new string('a', 40), "Myriagent.exe",
            new ReleaseRuntime("Microsoft.WindowsDesktop.App", new ReleaseVersion(10, 0, 0)), 1, "x.zip", new string('d', 64), 80, files);
        DeskServiceRules.CheckPayload(manifest, hashes).Should().BeNull();

        var tampered = Hashes();
        tampered["MyriagentDesk.dll"] = new string('e', 64);
        DeskServiceRules.CheckPayload(manifest, tampered).Should().Contain("MyriagentDesk.dll");

        var without = manifest with { Files = files.Where(f => f.Path != "Markdig.dll").ToList() };
        DeskServiceRules.CheckPayload(without, hashes).Should().Contain("Markdig.dll");
    }

    [Fact]
    public void 結束碼_每一種都有說明_不認得的當成未預期()
    {
        DeskServiceRules.Describe(DeskServiceExit.Ok).Should().BeNull();
        foreach (var exit in Enum.GetValues<DeskServiceExit>().Where(e => e != DeskServiceExit.Ok))
            DeskServiceRules.Describe(exit).Should().NotBeNullOrWhiteSpace(exit.ToString());
        DeskServiceRules.ExitOf(13).Should().Be(DeskServiceExit.SignatureInvalid);
        DeskServiceRules.ExitOf(-1073741510).Should().Be(DeskServiceExit.Unexpected);
        DeskServiceRules.ExitOf(1).Should().Be(DeskServiceExit.Unexpected);
    }

    [Fact]
    public void 命令列_各種模式()
    {
        DeskServiceArgs.Parse(["service"]).Should().Be(new DeskServiceCommand(DeskServiceMode.Service, "MyriagentDesk", null, null));
        DeskServiceArgs.Parse(["install"]).Mode.Should().Be(DeskServiceMode.Install);
        DeskServiceArgs.Parse(["uninstall", "--name", "MyriagentDeskTest-0a1b"]).Should().Be(new DeskServiceCommand(DeskServiceMode.Uninstall, "MyriagentDeskTest-0a1b", null, null));
        DeskServiceArgs.Parse(["run", "--pipe", "desk-test_1.x"]).Should().Be(new DeskServiceCommand(DeskServiceMode.Run, "MyriagentDesk", "desk-test_1.x", null));
        DeskServiceArgs.Parse(["session-probe"]).Mode.Should().Be(DeskServiceMode.SessionProbe);
        DeskServiceArgs.Parse(["version"]).Mode.Should().Be(DeskServiceMode.Version);
    }

    [Theory]
    [InlineData]
    [InlineData("bogus")]
    [InlineData("install", "--name")]
    [InlineData("install", "--name", "Spooler")]
    [InlineData("install", "--from", @"C:\evil")]
    [InlineData("install", "--dir", @"C:\Users\me")]
    [InlineData("install", "--pipe", "x")]
    [InlineData("run")]
    [InlineData("run", "--pipe", @"a\b")]
    [InlineData("uninstall", "--name", "MyriagentDeskTest-../x")]
    public void 命令列_不合法的一律是用法錯誤_安裝器不收路徑與任意的服務名稱(params string[] args)
    {
        var command = DeskServiceArgs.Parse(args);
        command.Mode.Should().Be(DeskServiceMode.Usage);
        command.Error.Should().NotBeNullOrWhiteSpace();
    }
}
