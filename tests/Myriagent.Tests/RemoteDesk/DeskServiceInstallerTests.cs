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

        public void Start(string name, TimeSpan timeout)
        {
            Calls.Add("start:" + name);
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
    public void 啟動不了_回StartFailed_安裝紀錄留著_舊的服務檔不清()
    {
        var installer = new DeskServiceInstaller(control);
        installer.Install(Options()).Should().Be(DeskServiceExit.Ok);
        var first = ReadInstall();

        control.FailStart = new InvalidOperationException("服務啟動後就停了");
        File.WriteAllText(Path.Combine(source, "MyriagentDesk.dll"), "a build that cannot start");
        installer.Install(Options(build: "def5678")).Should().Be(DeskServiceExit.StartFailed);

        ReadInstall().Build.Should().Be("def5678", "app 要看得到裝了但沒在執行");
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
