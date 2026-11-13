using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Remote;
using Myriagent.RemoteDesk;
using Myriagent.Security;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;
using MyriagentDesk;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.RemoteDesk;

/// <summary>測試用的一份更新：假的服務檔＋以暫時的「發佈金鑰」簽的發佈清單（真的清單由發佈工具簽；這裡扮演發佈的人）</summary>
internal sealed class DeskReleaseSamples : IDisposable
{
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public IReadOnlyList<ReleaseKey> Keys
    {
        get
        {
            var spki = P256KeyRules.ExportSpki(key);
            return [new ReleaseKey(ReleaseManifestRules.ComputeKid(spki), spki)];
        }
    }

    public static ReleaseIdentity Identity(int revision, string build = "abc1234") => new(new ReleaseVersion(0, 2, revision), build);

    /// <summary>一份更新的每個檔（<see cref="DeskServiceRules.UpdateParts"/>）。服務檔的內容帶版本：不同版本的指紋不同</summary>
    public Dictionary<string, byte[]> Parts(ReleaseIdentity identity, ECDsa? signWith = null)
    {
        var parts = DeskServiceRules.PayloadFiles.ToDictionary(name => name, name => Encoding.UTF8.GetBytes($"payload of {name} {identity.Release}"), StringComparer.Ordinal);
        var files = parts.Select(pair => new ReleaseFile(pair.Key, pair.Value.Length, ReleaseManifestRules.Sha256Hex(pair.Value)))
            .Append(new ReleaseFile("Myriagent.exe", 3, ReleaseManifestRules.Sha256Hex("app"u8))).ToList();
        var manifest = ReleaseManifestRules.Build(identity, identity.Build + new string('0', 33),
            new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero), "Myriagent.exe",
            new ReleaseRuntime("Microsoft.WindowsDesktop.App", new ReleaseVersion(10, 0, 0)), 1, new string('d', 64), files);
        parts[DeskServiceRules.ManifestFile] = manifest;
        parts[DeskServiceRules.SignatureFile] = Encoding.UTF8.GetBytes(ReleaseManifestRules.SerializeSignature(ReleaseManifestRules.Sign(manifest, signWith ?? key)));
        return parts;
    }

    public static string PayloadHash(IReadOnlyDictionary<string, byte[]> parts)
        => DeskServiceRules.PayloadHash(DeskServiceRules.PayloadFiles.ToDictionary(name => name, name => ReleaseManifestRules.Sha256Hex(parts[name]), StringComparer.Ordinal));

    public static DeskServiceInstall Installed(ReleaseIdentity identity, IReadOnlyDictionary<string, byte[]> parts)
    {
        var hash = PayloadHash(parts);
        return new DeskServiceInstall(DeskServiceRules.ServiceName, identity.Version.ToString(), identity.Build, hash, DeskServiceRules.PayloadFolder(hash), "0123456789abcdef",
            "2026-11-13T03:00:00Z");
    }

    public void Dispose() => key.Dispose();
}

/// <summary>
/// 服務自己更新、不經 UAC（擁有者 2026-11-13 決定）：管道上的三個指令、不收的各種情況（<see cref="DeskServiceRules.CheckSelfUpdate"/>：
/// 沒有內建金鑰、簽章不對、服務檔被換過、不比現在的新）、服務這一端（<see cref="SelfUpdater"/>：暫存資料夾當安裝資料夾、假的啟動）、
/// 經真的管道從 app 的用戶端送一整份。真的換版（停下服務、啟動新的）是安裝器的事，在 <see cref="DeskServiceInstallerTests"/>
/// </summary>
public sealed class DeskSelfUpdateTests : IDisposable
{
    private const string Me = "S-1-5-21-1-2-3-1001";

    private readonly TempDirectory temp = new("desk-self-update");
    private readonly DeskReleaseSamples release = new();

    public void Dispose()
    {
        release.Dispose();
        temp.Dispose();
    }

    private static ReleaseIdentity V(int revision, string build = "abc1234") => DeskReleaseSamples.Identity(revision, build);

    // ---- 規則 ----

    [Fact]
    public void 開始的要求_每個檔各一次_大小合理_來回一致()
    {
        var sizes = DeskServiceRules.UpdateParts.ToDictionary(name => name, _ => 100, StringComparer.Ordinal);
        DeskServiceRules.TryParseRequest(DeskServiceRules.UpdateBeginRequest(sizes), out var op, out var request).Should().BeTrue();
        op.Should().Be(DeskServiceRules.OpUpdateBegin);
        DeskServiceRules.ParseUpdateBegin(request!).Should().BeEquivalentTo(sizes);

        JObject With(Action<JArray> change)
        {
            var copy = (JObject)request!.DeepClone();
            change((JArray)copy["parts"]!);
            return copy;
        }

        DeskServiceRules.ParseUpdateBegin(With(parts => parts.RemoveAt(0))).Should().BeNull("少一個檔");
        DeskServiceRules.ParseUpdateBegin(With(parts => parts[1]["name"] = parts[0]["name"])).Should().BeNull("同一個檔兩次");
        DeskServiceRules.ParseUpdateBegin(With(parts => parts[0]["name"] = "evil.dll")).Should().BeNull("清單以外的檔");
        DeskServiceRules.ParseUpdateBegin(With(parts => parts[0]["name"] = "..\\MyriagentDesk.exe")).Should().BeNull();
        DeskServiceRules.ParseUpdateBegin(With(parts => parts[0]["bytes"] = 0)).Should().BeNull();
        DeskServiceRules.ParseUpdateBegin(With(parts => parts[0]["bytes"] = DeskServiceRules.MaxUpdateBytes + 1)).Should().BeNull();
        DeskServiceRules.ParseUpdateBegin(With(parts => parts.Last!["bytes"] = ReleaseManifestRules.MaxSignatureBytes + 1)).Should().BeNull("簽章檔的上限很小");
        DeskServiceRules.ParseUpdateBegin(With(parts =>
        {
            foreach (var part in parts.Take(DeskServiceRules.PayloadFiles.Count))
                part["bytes"] = 8 * 1024 * 1024;
        })).Should().BeNull("合計超過上限");
        DeskServiceRules.ParseUpdateBegin(new JObject()).Should().BeNull();

        var id = new string('a', 32);
        DeskServiceRules.ParseUpdateBegin(DeskServiceRules.UpdateBeginReply(id)).Should().Be(id);
        DeskServiceRules.ParseUpdateBegin(DeskServiceRules.ErrorReply(DeskServiceRules.CodeBusy, "正在換版")).Should().BeNull();
        DeskServiceRules.IsUpdateId("AAAA").Should().BeFalse();
    }

    [Fact]
    public void 一段的要求_來回一致_不是base64或太大不收_一行不超過上限()
    {
        var id = new string('b', 32);
        var data = RandomNumberGenerator.GetBytes(DeskServiceRules.UpdateChunkBytes);
        var line = DeskServiceRules.UpdatePutRequest(id, "MyriagentDesk.dll", 65536, data);
        line.Length.Should().BeLessThan(DeskServiceRules.MaxLineChars);
        DeskServiceRules.TryParseRequest(line, out _, out var request).Should().BeTrue();
        var put = DeskServiceRules.ParseUpdatePut(request!)!.Value;
        (put.Id, put.Name, put.Offset).Should().Be((id, "MyriagentDesk.dll", 65536));
        put.Data.Should().Equal(data);

        JObject With(string key, JToken value)
        {
            var copy = (JObject)request!.DeepClone();
            copy[key] = value;
            return copy;
        }

        DeskServiceRules.ParseUpdatePut(With("id", "x")).Should().BeNull();
        DeskServiceRules.ParseUpdatePut(With("name", "evil.dll")).Should().BeNull();
        DeskServiceRules.ParseUpdatePut(With("offset", -1)).Should().BeNull();
        DeskServiceRules.ParseUpdatePut(With("data", "不是 base64")).Should().BeNull();
        DeskServiceRules.ParseUpdatePut(With("data", "")).Should().BeNull();
        DeskServiceRules.ParseUpdatePut(With("data", Convert.ToBase64String(new byte[DeskServiceRules.UpdateChunkBytes + 1]))).Should().BeNull("超過一段的上限");

        DeskServiceRules.ParseUpdatePut(DeskServiceRules.UpdatePutReply(4096)).Should().Be(4096);
        DeskServiceRules.ParseUpdateApply(DeskServiceRules.UpdateApplyReply("0.2.970-abc1234")).Should().Be("0.2.970-abc1234");
        DeskServiceRules.ParseUpdateApply(DeskServiceRules.ErrorReply(DeskServiceRules.CodeDenied, "不比現在的新")).Should().BeNull();
    }

    [Fact]
    public void 能不能自己更新_簽章與每個檔都對_而且比執行中的與已安裝的新()
    {
        var parts = release.Parts(V(970));
        var installed = DeskReleaseSamples.Installed(V(960, "0123456"), release.Parts(V(960, "0123456")));
        DeskServiceRules.CheckSelfUpdate(parts, release.Keys, V(960, "0123456"), installed, out var identity, out var signedBy).Should().BeNull();
        identity.Should().Be(V(970));
        signedBy.Should().Be(release.Keys[0].Kid);

        // 換版的程式自己就是新版（不比執行中的）；原本沒有安裝紀錄也可以
        DeskServiceRules.CheckSelfUpdate(parts, release.Keys, null, null, out _, out _).Should().BeNull();
        // 裝著的是開發版：簽章過的發佈版可以蓋上去
        DeskServiceRules.CheckSelfUpdate(parts, release.Keys, new ReleaseIdentity(new ReleaseVersion(0, 2, 0), ReleaseIdentity.DevBuild),
            installed with { Build = ReleaseIdentity.DevBuild, Version = "0.2.0" }, out _, out _).Should().BeNull();
    }

    [Fact]
    public void 不能自己更新_沒有金鑰_簽章不對_服務檔被換過_不比現在的新()
    {
        var parts = release.Parts(V(970));
        var older = DeskReleaseSamples.Installed(V(960, "0123456"), release.Parts(V(960, "0123456")));
        string? Check(IReadOnlyDictionary<string, byte[]> p, IReadOnlyList<ReleaseKey>? keys = null, ReleaseIdentity? running = null, DeskServiceInstall? installed = null)
        {
            var reason = DeskServiceRules.CheckSelfUpdate(p, keys ?? release.Keys, running, installed, out var identity, out var signedBy);
            if (reason is not null)
                (identity, signedBy).Should().Be(((ReleaseIdentity?)null, (string?)null), "不通過就不給版本");
            return reason;
        }

        Check(parts, keys: []).Should().Contain("沒有內建的發佈簽章金鑰", "開發版與沒有金鑰的組建一律不自動更新");

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Check(release.Parts(V(970), signWith: other)).Should().NotBeNull("不是內建的金鑰簽的");

        Dictionary<string, byte[]> Without(string name) => parts.Where(pair => pair.Key != name).ToDictionary(pair => pair.Key, pair => pair.Value);
        Check(Without(DeskServiceRules.ManifestFile)).Should().Contain("沒有發佈清單");
        Check(Without(DeskServiceRules.SignatureFile)).Should().Contain("沒有發佈清單");
        Check(Without("Markdig.dll")).Should().Contain("Markdig.dll");

        var swapped = new Dictionary<string, byte[]>(parts) { ["MyriagentDesk.dll"] = "別人的程式"u8.ToArray() };
        Check(swapped).Should().Contain("MyriagentDesk.dll", "服務檔和簽章過的清單不符");
        var forged = new Dictionary<string, byte[]>(parts) { [DeskServiceRules.ManifestFile] = release.Parts(V(980))[DeskServiceRules.ManifestFile] };
        Check(forged).Should().NotBeNull("清單換成別的、簽章對不上");

        // 只往新的：同一個使用者的程式不能拿舊的、簽章照樣有效的版本把服務降回去
        Check(parts, running: V(970)).Should().Contain("不比執行中的服務");
        Check(parts, running: V(980, "fedcba9")).Should().Contain("不比執行中的服務");
        Check(parts, installed: DeskReleaseSamples.Installed(V(980, "fedcba9"), release.Parts(V(980, "fedcba9")))).Should().Contain("不比已經安裝的");
        Check(parts, installed: DeskReleaseSamples.Installed(V(960), parts)).Should().Contain("同一份");
        Check(parts, running: V(960, "0123456"), installed: older).Should().BeNull();
    }

    // ---- 服務這一端 ----

    private sealed class Launches
    {
        public List<(string Exe, string Arguments)> All { get; } = new();

        /// <summary>啟動的「換版的程式」：一個馬上結束的程序（結束碼 <see cref="ExitCode"/>）；<see cref="Fail"/> 時啟動不了</summary>
        public int ExitCode { get; set; } = (int)DeskServiceExit.StartFailed;

        public bool Fail { get; set; }

        public Process? Start(string exe, string arguments)
        {
            lock (All)
                All.Add((exe, arguments));
            return Fail ? null : Process.Start(new ProcessStartInfo("cmd.exe", "/c exit " + ExitCode) { UseShellExecute = false, CreateNoWindow = true });
        }
    }

    private SelfUpdater Updater(Launches launches, ReleaseIdentity? running = null, Func<bool>? inUse = null, IReadOnlyList<ReleaseKey>? keys = null,
        string serviceName = DeskServiceRules.ServiceName)
        => new(temp.Root, serviceName, keys ?? release.Keys, running ?? V(960, "0123456"), inUse ?? (() => false), launches.Start);

    private static string Send(ISelfUpdater updater, IReadOnlyDictionary<string, byte[]> parts, int chunk = 7)
    {
        var id = updater.Begin(parts.ToDictionary(pair => pair.Key, pair => pair.Value.Length, StringComparer.Ordinal));
        foreach (var (name, bytes) in parts)
        {
            for (var offset = 0; offset < bytes.Length; offset += chunk)
                updater.Put(id, name, offset, bytes[offset..Math.Min(bytes.Length, offset + chunk)]).Should().Be(Math.Min(bytes.Length, offset + chunk));
        }

        return id;
    }

    [Fact]
    public void 服務_收完驗過_寫進incoming_以那一份的執行檔啟動換版()
    {
        var launches = new Launches();
        var updater = Updater(launches);
        updater.Available.Should().BeTrue();
        var parts = release.Parts(V(970));

        updater.Apply(Send(updater, parts)).Should().Be(V(970));

        var incoming = Path.Combine(temp.Root, DeskServiceRules.IncomingFolder);
        Directory.GetFiles(incoming).Select(Path.GetFileName).Should().BeEquivalentTo(DeskServiceRules.UpdateParts);
        foreach (var (name, bytes) in parts)
            File.ReadAllBytes(Path.Combine(incoming, name)).Should().Equal(bytes);
        launches.All.Single().Should().Be((Path.Combine(incoming, DeskServiceRules.ExeName), "self-update"));
    }

    [Fact]
    public void 服務_測試用的暫時服務_換版的程式帶服務名稱()
    {
        var launches = new Launches();
        var updater = Updater(launches, serviceName: "MyriagentDeskTest-0a1b");
        updater.Apply(Send(updater, release.Parts(V(970))));
        launches.All.Single().Arguments.Should().Be("self-update --name MyriagentDeskTest-0a1b");
    }

    [Fact]
    public void 服務_不收的更新_什麼都不寫也不啟動_之後可以重新送()
    {
        var launches = new Launches();
        var updater = Updater(launches, running: V(970));
        var incoming = Path.Combine(temp.Root, DeskServiceRules.IncomingFolder);

        var refused = FluentActions.Invoking(() => updater.Apply(Send(updater, release.Parts(V(970))))).Should().Throw<SelfUpdateException>().Which;
        refused.Code.Should().Be(DeskServiceRules.CodeDenied);
        refused.Message.Should().Contain("不比執行中的服務");

        var swapped = release.Parts(V(980));
        swapped["MyriagentDesk.exe"] = "別人的程式"u8.ToArray();
        FluentActions.Invoking(() => updater.Apply(Send(updater, swapped))).Should().Throw<SelfUpdateException>().Which.Code.Should().Be(DeskServiceRules.CodeDenied);

        Directory.Exists(incoming).Should().BeFalse();
        launches.All.Should().BeEmpty();

        // 沒有內建金鑰的組建：這個功能不提供
        Updater(launches, keys: []).Available.Should().BeFalse();

        updater.Apply(Send(updater, release.Parts(V(980)))).Should().Be(V(980));
        launches.All.Should().HaveCount(1);
    }

    [Fact]
    public void 服務_還沒送完_不是這一次的id_位移不對_都不收()
    {
        var launches = new Launches();
        var updater = Updater(launches);
        var parts = release.Parts(V(970));
        var sizes = parts.ToDictionary(pair => pair.Key, pair => pair.Value.Length, StringComparer.Ordinal);

        var id = updater.Begin(sizes);
        updater.Put(id, "Markdig.dll", 0, parts["Markdig.dll"][..4]).Should().Be(4);
        FluentActions.Invoking(() => updater.Put(id, "Markdig.dll", 0, parts["Markdig.dll"][..4])).Should().Throw<SelfUpdateException>("位移要接著上一段")
            .Which.Code.Should().Be(DeskServiceRules.CodeInvalid);
        FluentActions.Invoking(() => updater.Put(id, "Markdig.dll", 4, new byte[parts["Markdig.dll"].Length])).Should().Throw<SelfUpdateException>("超過宣告的大小");
        FluentActions.Invoking(() => updater.Put(new string('0', 32), "Markdig.dll", 4, [1])).Should().Throw<SelfUpdateException>()
            .Which.Code.Should().Be(DeskServiceRules.CodeDenied);
        FluentActions.Invoking(() => updater.Apply(id)).Should().Throw<SelfUpdateException>().Which.Code.Should().Be(DeskServiceRules.CodeInvalid);

        // 重新開始：之前那一次作廢
        var second = Send(updater, parts);
        FluentActions.Invoking(() => updater.Apply(id)).Should().Throw<SelfUpdateException>().Which.Code.Should().Be(DeskServiceRules.CodeDenied);
        updater.Apply(second).Should().Be(V(970));
    }

    [Fact]
    public void 服務_遠端桌面正在用幫手_先不更新()
    {
        var launches = new Launches();
        var inUse = true;
        var updater = Updater(launches, inUse: () => inUse);
        var parts = release.Parts(V(970));
        FluentActions.Invoking(() => updater.Apply(Send(updater, parts))).Should().Throw<SelfUpdateException>().Which.Code.Should().Be(DeskServiceRules.CodeBusy);
        launches.All.Should().BeEmpty();
        Directory.Exists(Path.Combine(temp.Root, DeskServiceRules.IncomingFolder)).Should().BeFalse();

        inUse = false;
        updater.Apply(Send(updater, parts)).Should().Be(V(970));
    }

    [Fact]
    public void 服務_換版中不收新的_換版的程式沒換成結束了_可以再試()
    {
        var launches = new Launches();
        var updater = Updater(launches);
        var parts = release.Parts(V(970));
        updater.Apply(Send(updater, parts));

        // 換版的程式（這裡是馬上結束的 cmd）結束之後、服務還在：可以再送一次
        SpinWait.SpinUntil(() =>
        {
            try
            {
                updater.Begin(parts.ToDictionary(pair => pair.Key, pair => pair.Value.Length, StringComparer.Ordinal));
                return true;
            }
            catch (SelfUpdateException ex) when (ex.Code == DeskServiceRules.CodeBusy)
            {
                return false;
            }
        }, 10_000).Should().BeTrue();
        updater.Apply(Send(updater, parts)).Should().Be(V(970));
        launches.All.Should().HaveCount(2);
    }

    [Fact]
    public void 服務_換版的程式啟動不了_回失敗_之後可以再試()
    {
        var launches = new Launches { Fail = true };
        var updater = Updater(launches);
        var parts = release.Parts(V(970));
        FluentActions.Invoking(() => updater.Apply(Send(updater, parts))).Should().Throw<SelfUpdateException>().Which.Code.Should().Be(DeskServiceRules.CodeFailed);
        launches.Fail = false;
        updater.Apply(Send(updater, parts)).Should().Be(V(970));
    }

    [Fact]
    public void 清掉上一次換版留下的incoming()
    {
        var incoming = temp.Combine(DeskServiceRules.IncomingFolder);
        Directory.CreateDirectory(incoming);
        File.WriteAllText(Path.Combine(incoming, "MyriagentDesk.exe"), "x");
        SelfUpdater.CleanIncoming(temp.Root);
        Directory.Exists(incoming).Should().BeFalse();
        SelfUpdater.CleanIncoming(temp.Root);
    }

    // ---- 經真的管道 ----

    private sealed class FakeIdentifier(DeskServiceClientInfo info) : IDeskClientIdentifier
    {
        public DeskServiceClientInfo Identify(NamedPipeServerStream pipe) => info;
    }

    private sealed class NoAgents : ISessionAgents
    {
        public string Open(int sessionId) => throw new InvalidOperationException("這個測試不開幫手");
    }

    private sealed class NoProbe : IDeskSessionProbe
    {
        public DeskServiceProbe Probe(int sessionId) => new(sessionId, "Default", false, 0, []);
    }

    private static async Task<(DeskPipeServer Server, string Pipe)> StartServiceAsync(ISelfUpdater? updater)
    {
        var pipe = "desk-test-" + Guid.NewGuid().ToString("N")[..12];
        var server = new DeskPipeServer(pipe, system: false, new FakeIdentifier(new DeskServiceClientInfo(1, Me, Me)),
            new ServicePipeHandler(V(960, "0123456"), system: false, new NoProbe(), new NoAgents(), updater));
        server.Start();
        await server.Listening.WaitAsync(TimeSpan.FromSeconds(10));
        return (server, pipe);
    }

    [Fact]
    public async Task 管道_app的用戶端分段送一整份_服務驗過換版_不收的回原因()
    {
        var launches = new Launches();
        var (server, pipe) = await StartServiceAsync(Updater(launches));
        using var dispose = server;
        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        client.Hello().Features.Should().Contain(DeskServiceRules.FeatureSelfUpdate);

        // 一個檔比一段大：分好幾段
        var parts = release.Parts(V(970));
        parts["Myriagent.Protocol.dll"] = RandomNumberGenerator.GetBytes(DeskServiceRules.UpdateChunkBytes * 2 + 123);
        var files = parts.Where(pair => DeskServiceRules.PayloadFiles.Contains(pair.Key))
            .Select(pair => new ReleaseFile(pair.Key, pair.Value.Length, ReleaseManifestRules.Sha256Hex(pair.Value))).ToList();
        var manifest = ReleaseManifestRules.Build(V(970), "abc1234" + new string('0', 33), new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero), "MyriagentDesk.exe",
            new ReleaseRuntime("Microsoft.WindowsDesktop.App", new ReleaseVersion(10, 0, 0)), 1, new string('d', 64), files);
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        parts[DeskServiceRules.ManifestFile] = manifest;
        parts[DeskServiceRules.SignatureFile] = Encoding.UTF8.GetBytes(ReleaseManifestRules.SerializeSignature(ReleaseManifestRules.Sign(manifest, signer)));
        FluentActions.Invoking(() => client.SelfUpdate(parts)).Should().Throw<InvalidOperationException>().WithMessage("*denied*", "不是內建的金鑰簽的");
        launches.All.Should().BeEmpty();

        var good = release.Parts(V(970));
        client.SelfUpdate(good).Should().Be("0.2.970-abc1234");
        File.ReadAllBytes(Path.Combine(temp.Root, DeskServiceRules.IncomingFolder, "MyriagentDesk.dll")).Should().Equal(good["MyriagentDesk.dll"]);
        launches.All.Should().HaveCount(1);
    }

    [Fact]
    public async Task 管道_不自己更新的服務_不宣告也不認得那三個指令()
    {
        var (server, pipe) = await StartServiceAsync(null);
        using var dispose = server;
        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        client.Hello().Features.Should().NotContain(DeskServiceRules.FeatureSelfUpdate);
        FluentActions.Invoking(() => client.SelfUpdate(release.Parts(V(970)))).Should().Throw<InvalidOperationException>().WithMessage("*unsupported*");
    }

    /// <summary>
    /// 拿真的發佈版驗一次（只讀）：受管理安裝的某一版資料夾裡的服務檔、發佈清單與簽章，以**內建的正式發佈金鑰**通過自己更新的檢查，
    /// 而且服務執行檔自己報的版本就是清單的版本（換版的程式會比對）。環境變數指到那個資料夾的 release.manifest.json
    /// （例：%LOCALAPPDATA%\Programs\Myriagent\versions\0.2.970-abc1234\release.manifest.json，寫成 C:/…）
    /// </summary>
    [EnvFileFact("MYRIAGENT_DESKSVC_RELEASE_MANIFEST", "以真的發佈版驗服務自己更新的檢查（只讀那個資料夾）")]
    public void 真的發佈版_內建的正式金鑰驗得過_服務執行檔的版本就是清單的版本()
    {
        var folder = Path.GetDirectoryName(Environment.GetEnvironmentVariable("MYRIAGENT_DESKSVC_RELEASE_MANIFEST")!)!;
        var parts = DeskServiceRules.UpdateParts.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(folder, name)), StringComparer.Ordinal);
        ReleaseKeys.Production.Should().NotBeEmpty();

        DeskServiceRules.CheckSelfUpdate(parts, ReleaseKeys.Production, new ReleaseIdentity(new ReleaseVersion(0, 2, 1), "0000000"), null, out var identity, out var signedBy)
            .Should().BeNull();
        signedBy.Should().BeOneOf(ReleaseKeys.Production.Select(k => k.Kid));

        // 服務執行檔自己報的版本
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(folder, DeskServiceRules.ExeName), "version")
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
        })!;
        var printed = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(10_000).Should().BeTrue();
        printed.Should().Be(identity!.Release);

        // 同一版、較新的版本都不收；經真的 SelfUpdater 放進暫存的安裝資料夾
        DeskServiceRules.CheckSelfUpdate(parts, ReleaseKeys.Production, identity, null, out _, out _).Should().Contain("不比執行中的服務");
        var launches = new Launches();
        var updater = new SelfUpdater(temp.Root, DeskServiceRules.ServiceName, ReleaseKeys.Production, new ReleaseIdentity(new ReleaseVersion(0, 2, 1), "0000000"), () => false, launches.Start);
        updater.Apply(Send(updater, parts, chunk: DeskServiceRules.UpdateChunkBytes)).Should().Be(identity);
        File.ReadAllBytes(Path.Combine(temp.Root, DeskServiceRules.IncomingFolder, DeskServiceRules.ExeName)).Should().Equal(parts[DeskServiceRules.ExeName]);
    }

    [Fact]
    public void 命令列_selfUpdate_不收信任的伺服器與路徑()
    {
        DeskServiceArgs.Parse(["self-update"]).Should().Be(new DeskServiceCommand(DeskServiceMode.SelfUpdate, DeskServiceRules.ServiceName, null, null));
        DeskServiceArgs.Parse(["self-update", "--name", "MyriagentDeskTest-0a1b"]).ServiceName.Should().Be("MyriagentDeskTest-0a1b");
        using var hub = new DeskOpenSamples();
        DeskServiceArgs.Parse(["self-update", "--hub-key", hub.Trust.HubSpki, "--aud", hub.Trust.Aud, "--device", hub.Trust.DeviceId]).Mode.Should().Be(DeskServiceMode.Usage,
            "自己更新不能改信任的伺服器");
        DeskServiceArgs.Parse(["self-update", "--source", "C:\\x"]).Mode.Should().Be(DeskServiceMode.Usage);
    }
}
