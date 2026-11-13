using System.Security.Cryptography;
using System.Text;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;
using MyriagentDesk;

namespace Myriagent.Tests.RemoteDesk;

/// <summary>
/// 鎖定畫面支援的安裝器（<see cref="DeskServiceInstaller"/>）：來源與「Program Files」都是暫存資料夾、服務控制是假的——
/// 不需要系統管理員，也絕不碰真的服務與真的 Program Files。驗：服務檔進自己的子資料夾、登記的啟動指令、安裝紀錄、更新時換資料夾並清掉舊的、
/// 有發佈清單時簽章與每個檔的雜湊都要對得上、不是一般的檔不裝、各種失敗的結束碼、移除
/// </summary>
public sealed class DeskServiceInstallerTests : IDisposable
{
    private readonly TempDirectory temp = new("desk-service-install");
    private readonly FakeServiceControl control = new();
    private readonly string source;
    private readonly string programFiles;

    public DeskServiceInstallerTests()
    {
        source = temp.Combine("app");
        programFiles = temp.Combine("Program Files");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(programFiles);
        foreach (var name in DeskServiceRules.PayloadFiles)
            File.WriteAllBytes(Path.Combine(source, name), Encoding.UTF8.GetBytes("payload of " + name));
    }

    public void Dispose() => temp.Dispose();

    private sealed class FakeServiceControl : IServiceControl
    {
        public ServiceRunState State { get; set; } = ServiceRunState.NotInstalled;

        public string? BinPath { get; private set; }

        public string? DisplayName { get; private set; }

        public List<string> Calls { get; } = new();

        public Exception? FailStart { get; set; }

        public Exception? FailStop { get; set; }

        public Exception? FailCreate { get; set; }

        public ServiceRunState Query(string name) => State;

        public void CreateOrUpdate(string name, string displayName, string description, string binPath)
        {
            Calls.Add("create:" + name);
            if (FailCreate is not null)
                throw FailCreate;
            BinPath = binPath;
            DisplayName = displayName;
            if (State == ServiceRunState.NotInstalled)
                State = ServiceRunState.Stopped;
        }

        /// <summary>每次啟動先叫（丟例外就是那一次啟動不了）</summary>
        public Action? StartHook { get; set; }

        public void Start(string name, TimeSpan timeout)
        {
            Calls.Add("start:" + name);
            StartHook?.Invoke();
            if (FailStart is not null)
                throw FailStart;
            State = ServiceRunState.Running;
        }

        public void Stop(string name, TimeSpan timeout)
        {
            Calls.Add("stop:" + name);
            if (FailStop is not null)
                throw FailStop;
            if (State == ServiceRunState.Running)
                State = ServiceRunState.Stopped;
        }

        public void Delete(string name)
        {
            Calls.Add("delete:" + name);
            State = ServiceRunState.NotInstalled;
        }
    }

    private DeskInstallOptions Options(IReadOnlyList<ReleaseKey>? keys = null, string name = DeskServiceRules.ServiceName, string build = "abc1234")
        => new(name, source, programFiles, new ReleaseIdentity(new ReleaseVersion(0, 2, 950), build), keys ?? [], new DateTimeOffset(2026, 11, 13, 3, 0, 0, TimeSpan.Zero));

    private string Root(string name = DeskServiceRules.ServiceName) => DeskServiceRules.InstallRoot(programFiles, name);

    private DeskServiceInstall ReadInstall(string name = DeskServiceRules.ServiceName)
        => DeskServiceRules.ParseInstall(File.ReadAllText(Path.Combine(Root(name), DeskServiceRules.InstallFile)))!;

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>在來源資料夾放簽章過的發佈清單（列出資料夾裡現有的每個檔）；回信任的金鑰</summary>
    private IReadOnlyList<ReleaseKey> SignRelease(ECDsa? signWith = null)
    {
        using var own = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = signWith ?? own;
        File.WriteAllText(Path.Combine(source, "Myriagent.exe"), "app");
        var files = Directory.GetFiles(source)
            .Where(f => Path.GetFileName(f) is not (DeskServiceInstaller.ManifestFile or DeskServiceInstaller.SignatureFile))
            .Select(f => new ReleaseFile(Path.GetFileName(f), new FileInfo(f).Length, Sha(f))).ToList();
        var manifest = ReleaseManifestRules.Build(new ReleaseIdentity(new ReleaseVersion(0, 2, 950), "abc1234"), "abc1234" + new string('0', 33),
            new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero), "Myriagent.exe",
            new ReleaseRuntime("Microsoft.WindowsDesktop.App", new ReleaseVersion(10, 0, 0)), 1, new string('d', 64), files);
        File.WriteAllBytes(Path.Combine(source, DeskServiceInstaller.ManifestFile), manifest);
        File.WriteAllText(Path.Combine(source, DeskServiceInstaller.SignatureFile), ReleaseManifestRules.SerializeSignature(ReleaseManifestRules.Sign(manifest, key)));
        var spki = P256KeyRules.ExportSpki(key);
        return [new ReleaseKey(ReleaseManifestRules.ComputeKid(spki), spki)];
    }

    [Fact]
    public void 信任的伺服器_安裝時給的記在安裝資料夾_重裝沒給就拿掉_不留上一次配對的()
    {
        using var hub = new DeskOpenSamples();
        var installer = new DeskServiceInstaller(control);
        var trustPath = Path.Combine(Root(), DeskServiceRules.TrustFile);

        installer.Install(Options() with { Trust = hub.Trust }).Should().Be(DeskServiceExit.Ok);
        DeskServiceRules.ParseTrust(File.ReadAllText(trustPath)).Should().Be(hub.Trust);

        // 換了伺服器再裝：以這一次的為準
        using var other = new DeskOpenSamples();
        installer.Install(Options() with { Trust = other.Trust }).Should().Be(DeskServiceExit.Ok);
        DeskServiceRules.ParseTrust(File.ReadAllText(trustPath)).Should().Be(other.Trust);

        // 沒配對時重裝：拿掉（裝了只能看）
        installer.Install(Options()).Should().Be(DeskServiceExit.Ok);
        File.Exists(trustPath).Should().BeFalse();
        File.Exists(Path.Combine(Root(), DeskServiceRules.InstallFile)).Should().BeTrue();
    }

    // ---- 服務自己更新（沒有人同意 UAC）：一定要簽章、只往新的、不動信任的伺服器 ----

    private static readonly ReleaseIdentity Older = new(new ReleaseVersion(0, 2, 940), "0123456");

    /// <summary>先裝一份舊的（940），再把來源換成簽章過的新版（950、內容不同）；回信任的金鑰</summary>
    private IReadOnlyList<ReleaseKey> InstallOlderThenSignNewer(DeskServiceTrust? trust = null)
    {
        new DeskServiceInstaller(control).Install(Options() with { Identity = Older, Trust = trust }).Should().Be(DeskServiceExit.Ok);
        foreach (var name in DeskServiceRules.PayloadFiles)
            File.WriteAllBytes(Path.Combine(source, name), Encoding.UTF8.GetBytes("newer payload of " + name));
        control.Calls.Clear();
        return SignRelease();
    }

    [Fact]
    public void 自己更新_簽章過而且比較新_換過去_信任的伺服器不動()
    {
        using var hub = new DeskOpenSamples();
        var keys = InstallOlderThenSignNewer(hub.Trust);
        var old = ReadInstall();

        new DeskServiceInstaller(control).Install(Options(keys) with { SelfUpdate = true }).Should().Be(DeskServiceExit.Ok);

        var now = ReadInstall();
        (now.Version, now.Build, now.SignedBy).Should().Be(("0.2.950", "abc1234", keys[0].Kid));
        now.Folder.Should().NotBe(old.Folder);
        control.BinPath.Should().Be("\"" + Path.Combine(Root(), now.Folder, "MyriagentDesk.exe") + "\" service");
        control.Calls.Should().Equal("stop:MyriagentDesk", "create:MyriagentDesk", "start:MyriagentDesk");
        Directory.Exists(Path.Combine(Root(), old.Folder)).Should().BeFalse("新的起來了才清舊的");
        DeskServiceRules.ParseTrust(File.ReadAllText(Path.Combine(Root(), DeskServiceRules.TrustFile))).Should().Be(hub.Trust, "同意 UAC 的人定下的，自己更新不動");
    }

    [Fact]
    public void 自己更新_沒有簽章_沒有金鑰_不比已安裝的新_執行檔和清單不是同一版_都不裝也不停服務()
    {
        var keys = InstallOlderThenSignNewer();
        var installed = ReadInstall();
        DeskServiceExit Try(DeskInstallOptions options)
        {
            control.Calls.Clear();
            var exit = new DeskServiceInstaller(control).Install(options with { SelfUpdate = true });
            control.Calls.Should().BeEmpty("不收的更新不能把執行中的服務停下");
            ReadInstall().Should().Be(installed);
            return exit;
        }

        Try(Options()).Should().Be(DeskServiceExit.SignatureInvalid, "沒有內建金鑰的組建不自動更新");
        Try(Options(keys) with { Identity = new ReleaseIdentity(new ReleaseVersion(0, 2, 951), "abc1234") }).Should().Be(DeskServiceExit.SignatureInvalid, "執行檔不是清單的那一版");

        // 已經裝著更新的：簽章有效的舊版不能把它降回去
        File.WriteAllText(Path.Combine(Root(), DeskServiceRules.InstallFile), DeskServiceRules.SerializeInstall(installed with { Version = "0.2.960", Build = "fedcba9" }));
        installed = ReadInstall();
        Try(Options(keys)).Should().Be(DeskServiceExit.SignatureInvalid);
        File.WriteAllText(Path.Combine(Root(), DeskServiceRules.InstallFile), DeskServiceRules.SerializeInstall(installed with { Version = "0.2.940", Build = "0123456" }));
        installed = ReadInstall();

        File.Delete(Path.Combine(source, DeskServiceInstaller.SignatureFile));
        Try(Options(keys)).Should().Be(DeskServiceExit.SignatureInvalid, "沒有簽章");
        File.Delete(Path.Combine(source, DeskServiceInstaller.ManifestFile));
        Try(Options(keys)).Should().Be(DeskServiceExit.SignatureInvalid, "沒有發佈清單（手動安裝的開發版可以，自己更新不行）");
    }

    [Fact]
    public void 新的啟動不了_退回原本那一份_服務照樣在執行()
    {
        var keys = InstallOlderThenSignNewer();
        var old = ReadInstall();
        var oldBinPath = "\"" + Path.Combine(Root(), old.Folder, "MyriagentDesk.exe") + "\" service";
        var starts = 0;
        control.StartHook = () =>
        {
            if (++starts == 1)
                throw new TimeoutException("服務沒有在時間內啟動");
        };

        new DeskServiceInstaller(control).Install(Options(keys) with { SelfUpdate = true }).Should().Be(DeskServiceExit.StartFailed);

        ReadInstall().Should().Be(old, "安裝紀錄改回原本的");
        control.BinPath.Should().Be(oldBinPath);
        control.State.Should().Be(ServiceRunState.Running);
        control.Calls.Should().Equal("stop:MyriagentDesk", "create:MyriagentDesk", "start:MyriagentDesk", "create:MyriagentDesk", "start:MyriagentDesk");
        Directory.GetDirectories(Root(), "p-*").Select(Path.GetFileName).Should().Equal([old.Folder], "啟動不了的那一份拿掉");

        // 手動安裝（按了 UAC）也一樣退回
        control.Calls.Clear();
        starts = 0;
        new DeskServiceInstaller(control).Install(Options(keys)).Should().Be(DeskServiceExit.StartFailed);
        ReadInstall().Should().Be(old);
        control.State.Should().Be(ServiceRunState.Running);
    }

    [Fact]
    public void 第一次安裝就啟動不了_沒得退_照舊回啟動失敗()
    {
        control.FailStart = new TimeoutException("服務沒有在時間內啟動");
        new DeskServiceInstaller(control).Install(Options()).Should().Be(DeskServiceExit.StartFailed);
        control.Calls.Should().Equal("stop:MyriagentDesk", "create:MyriagentDesk", "start:MyriagentDesk");
    }

    [Fact]
    public void 安裝_開發版沒有發佈清單_服務檔進自己的子資料夾_登記並啟動_寫安裝紀錄()
    {
        new DeskServiceInstaller(control).Install(Options(build: ReleaseIdentity.DevBuild)).Should().Be(DeskServiceExit.Ok);

        var install = ReadInstall();
        install.Should().BeEquivalentTo(new { Service = "MyriagentDesk", Version = "0.2.950", Build = "dev", SignedBy = (string?)null, InstalledAt = "2026-11-13T03:00:00Z" });
        var folder = Path.Combine(Root(), install.Folder);
        Directory.GetFiles(folder).Select(Path.GetFileName).Should().BeEquivalentTo(DeskServiceRules.PayloadFiles);
        foreach (var name in DeskServiceRules.PayloadFiles)
            File.ReadAllText(Path.Combine(folder, name)).Should().Be("payload of " + name);
        install.Folder.Should().Be(DeskServiceRules.PayloadFolder(install.Payload));

        control.BinPath.Should().Be("\"" + Path.Combine(folder, "MyriagentDesk.exe") + "\" service");
        control.DisplayName.Should().Be(DeskServiceRules.DisplayName);
        control.State.Should().Be(ServiceRunState.Running);
        control.Calls.Should().Equal("stop:MyriagentDesk", "create:MyriagentDesk", "start:MyriagentDesk");
    }

    [Fact]
    public void 安裝_測試的暫時服務_裝在自己的資料夾_顯示名稱標出是測試()
    {
        const string name = "MyriagentDeskTest-0a1b";
        new DeskServiceInstaller(control).Install(Options(name: name)).Should().Be(DeskServiceExit.Ok);
        Directory.Exists(Root()).Should().BeFalse("真的服務的資料夾不碰");
        ReadInstall(name).Service.Should().Be(name);
        control.DisplayName.Should().Contain("測試").And.Contain(name);
        control.BinPath.Should().StartWith("\"" + Root(name));
    }

    [Fact]
    public void 更新_先停下_換到新的子資料夾_新的啟動後清掉舊的()
    {
        var installer = new DeskServiceInstaller(control);
        installer.Install(Options()).Should().Be(DeskServiceExit.Ok);
        var first = ReadInstall();
        Directory.CreateDirectory(Path.Combine(Root(), "logs"));
        File.WriteAllText(Path.Combine(Root(), "logs", "service-2026-11-13.log"), "log");
        control.Calls.Clear();

        File.WriteAllText(Path.Combine(source, "MyriagentDesk.dll"), "a newer build");
        installer.Install(Options(build: "def5678")).Should().Be(DeskServiceExit.Ok);

        var second = ReadInstall();
        second.Folder.Should().NotBe(first.Folder);
        second.Build.Should().Be("def5678");
        Directory.Exists(Path.Combine(Root(), first.Folder)).Should().BeFalse("舊的服務檔在新的啟動之後清掉");
        File.ReadAllText(Path.Combine(Root(), second.Folder, "MyriagentDesk.dll")).Should().Be("a newer build");
        File.Exists(Path.Combine(Root(), "logs", "service-2026-11-13.log")).Should().BeTrue("記錄留著");
        control.BinPath.Should().Contain(second.Folder);
        control.Calls.Should().Equal("stop:MyriagentDesk", "create:MyriagentDesk", "start:MyriagentDesk");
    }

    [Fact]
    public void 重裝同一份_資料夾不變_照樣停下再啟動()
    {
        var installer = new DeskServiceInstaller(control);
        installer.Install(Options()).Should().Be(DeskServiceExit.Ok);
        var first = ReadInstall();
        installer.Install(Options()).Should().Be(DeskServiceExit.Ok);
        ReadInstall().Folder.Should().Be(first.Folder);
        Directory.GetDirectories(Root()).Select(Path.GetFileName).Should().Equal(first.Folder);
    }

    [Fact]
    public void 有發佈清單_簽章與雜湊都對得上才裝_安裝紀錄記下簽章金鑰()
    {
        var keys = SignRelease();
        new DeskServiceInstaller(control).Install(Options(keys)).Should().Be(DeskServiceExit.Ok);
        ReadInstall().SignedBy.Should().Be(keys[0].Kid);
    }

    [Fact]
    public void 有發佈清單_服務檔被換過_不裝()
    {
        var keys = SignRelease();
        File.WriteAllText(Path.Combine(source, "MyriagentDesk.exe"), "not the released exe");
        new DeskServiceInstaller(control).Install(Options(keys)).Should().Be(DeskServiceExit.SignatureInvalid);
        Directory.Exists(Root()).Should().BeFalse("什麼都沒有寫進 Program Files");
        control.Calls.Should().BeEmpty("沒有碰服務");
    }

    [Fact]
    public void 有發佈清單_不是信任的金鑰簽的_不裝()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trusted = SignRelease();
        // 清單改由另一把金鑰簽：kid 不在信任清單
        var manifest = File.ReadAllBytes(Path.Combine(source, DeskServiceInstaller.ManifestFile));
        File.WriteAllText(Path.Combine(source, DeskServiceInstaller.SignatureFile), ReleaseManifestRules.SerializeSignature(ReleaseManifestRules.Sign(manifest, other)));
        new DeskServiceInstaller(control).Install(Options(trusted)).Should().Be(DeskServiceExit.SignatureInvalid);
    }

    [Fact]
    public void 有發佈清單_清單被改過或沒有簽章檔_不裝()
    {
        var keys = SignRelease();
        var manifestPath = Path.Combine(source, DeskServiceInstaller.ManifestFile);
        var original = File.ReadAllBytes(manifestPath);
        File.WriteAllBytes(manifestPath, original.Append((byte)' ').ToArray());
        new DeskServiceInstaller(control).Install(Options(keys)).Should().Be(DeskServiceExit.SignatureInvalid);

        File.WriteAllBytes(manifestPath, original);
        File.Delete(Path.Combine(source, DeskServiceInstaller.SignatureFile));
        new DeskServiceInstaller(control).Install(Options(keys)).Should().Be(DeskServiceExit.SignatureInvalid);
    }

    [Fact]
    public void 這個組建沒有內建金鑰_有發佈清單也不驗()
    {
        SignRelease();
        File.WriteAllText(Path.Combine(source, "MyriagentDesk.exe"), "a self-built exe");
        new DeskServiceInstaller(control).Install(Options(keys: [])).Should().Be(DeskServiceExit.Ok);
        ReadInstall().SignedBy.Should().BeNull();
    }

    [Fact]
    public void 少了服務檔_不裝()
    {
        File.Delete(Path.Combine(source, "Newtonsoft.Json.dll"));
        new DeskServiceInstaller(control).Install(Options()).Should().Be(DeskServiceExit.PayloadMissing);
        Directory.Exists(Root()).Should().BeFalse();
    }

    [Fact]
    public void 服務檔的位置是資料夾_不裝()
    {
        File.Delete(Path.Combine(source, "Markdig.dll"));
        Directory.CreateDirectory(Path.Combine(source, "Markdig.dll"));
        new DeskServiceInstaller(control).Install(Options()).Should().Be(DeskServiceExit.PayloadInvalid);
    }

    [Fact]
    public void 服務檔是符號連結_不跟著走_不裝()
    {
        var secret = temp.Combine("secret.txt");
        File.WriteAllText(secret, "only administrators can read this");
        var link = Path.Combine(source, "Markdig.dll");
        File.Delete(link);
        try
        {
            File.CreateSymbolicLink(link, secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 這台不能建符號連結（沒有開發人員模式、不是系統管理員）：這個情況驗不了
            return;
        }

        new DeskServiceInstaller(control).Install(Options()).Should().Be(DeskServiceExit.PayloadInvalid);
        Directory.Exists(Root()).Should().BeFalse();
        PlainFile.TryRead(link, 1024, out var bytes).Should().Be(PlainFileStatus.Invalid);
        bytes.Should().BeEmpty();
    }

    [Fact]
    public void 讀檔_一般的檔讀得到_太大或不存在各有狀態()
    {
        var file = temp.Combine("plain.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        PlainFile.TryRead(file, 4, out var bytes).Should().Be(PlainFileStatus.Ok);
        bytes.Should().Equal(1, 2, 3, 4);
        PlainFile.TryRead(file, 3, out _).Should().Be(PlainFileStatus.Invalid);
        PlainFile.TryRead(temp.Combine("nope.bin"), 4, out _).Should().Be(PlainFileStatus.Missing);
        PlainFile.TryRead(temp.Combine("no-folder", "x.bin"), 4, out _).Should().Be(PlainFileStatus.Missing);
    }

    [Fact]
    public void 現有的服務停不下來_不動檔案()
    {
        new DeskServiceInstaller(control).Install(Options()).Should().Be(DeskServiceExit.Ok);
        var before = ReadInstall();
        control.FailStop = new TimeoutException("停止逾時");
        File.WriteAllText(Path.Combine(source, "MyriagentDesk.dll"), "newer");
        new DeskServiceInstaller(control).Install(Options()).Should().Be(DeskServiceExit.ServiceFailed);
        ReadInstall().Should().Be(before);
        Directory.GetDirectories(Root()).Select(Path.GetFileName).Should().Equal(before.Folder);
    }

    [Fact]
    public void 登記失敗_回ServiceFailed()
    {
        control.FailCreate = new System.ComponentModel.Win32Exception(5);
        new DeskServiceInstaller(control).Install(Options()).Should().Be(DeskServiceExit.ServiceFailed);
        File.Exists(Path.Combine(Root(), DeskServiceRules.InstallFile)).Should().BeFalse("沒有登記成功就沒有安裝紀錄");
    }

    [Fact]
    public void 新的啟動不了_原本那一份也啟動不了_回StartFailed_服務指回原本的_兩份都留著()
    {
        var installer = new DeskServiceInstaller(control);
        installer.Install(Options()).Should().Be(DeskServiceExit.Ok);
        var first = ReadInstall();

        control.FailStart = new InvalidOperationException("服務啟動後就停了");
        File.WriteAllText(Path.Combine(source, "MyriagentDesk.dll"), "a build that cannot start");
        installer.Install(Options(build: "def5678")).Should().Be(DeskServiceExit.StartFailed);

        // 退回去也啟動不了（服務本身有別的問題）：登記與安裝紀錄是原本那一份，app 看得到「裝了但沒在執行」
        ReadInstall().Should().Be(first);
        control.BinPath.Should().Be("\"" + Path.Combine(Root(), first.Folder, "MyriagentDesk.exe") + "\" service");
        Directory.Exists(Path.Combine(Root(), first.Folder)).Should().BeTrue("新的沒起來，舊的服務檔先留著");
    }

    [Fact]
    public void 移除_停下_刪登記_刪資料夾_沒裝過也算成功()
    {
        var installer = new DeskServiceInstaller(control);
        installer.Uninstall(DeskServiceRules.ServiceName, programFiles).Should().Be(DeskServiceExit.Ok);

        installer.Install(Options()).Should().Be(DeskServiceExit.Ok);
        control.Calls.Clear();
        installer.Uninstall(DeskServiceRules.ServiceName, programFiles).Should().Be(DeskServiceExit.Ok);
        control.Calls.Should().Equal("stop:MyriagentDesk", "delete:MyriagentDesk");
        control.State.Should().Be(ServiceRunState.NotInstalled);
        Directory.Exists(Root()).Should().BeFalse();
        Directory.Exists(programFiles).Should().BeTrue();
    }

    [Fact]
    public void 移除_停不下來也照樣刪登記()
    {
        var installer = new DeskServiceInstaller(control);
        installer.Install(Options()).Should().Be(DeskServiceExit.Ok);
        control.FailStop = new TimeoutException("停止逾時");
        installer.Uninstall(DeskServiceRules.ServiceName, programFiles).Should().Be(DeskServiceExit.Ok);
        control.State.Should().Be(ServiceRunState.NotInstalled);
    }
}
