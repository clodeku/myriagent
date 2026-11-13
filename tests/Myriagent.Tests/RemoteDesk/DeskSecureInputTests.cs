using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.RemoteDesk;
using Myriagent.Security;
using Myriagent.Update;
using MyriagentDesk;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.RemoteDesk;

/// <summary>測試用的 deskOpen 信封：以暫時的「伺服器身分金鑰」簽（真的信封由伺服器簽；這裡扮演伺服器）</summary>
internal sealed class DeskOpenSamples : IDisposable
{
    public const string Aud = "https://hub.test";
    public const string DeviceId = "d_0123456789ABCDEFGHJKMNPQRS";
    public const string Session = DeskSealRulesTests.Session;

    private readonly ECDsa hub = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string HubSpki => P256KeyRules.ExportSpki(hub);

    public DeskServiceTrust Trust => new(HubSpki, Aud, DeviceId);

    public CommandEnvelope Envelope(string? viewerKey = DeskSealRulesTests.ViewerPublic, string session = Session, string dev = DeviceId, string aud = Aud,
        string cmd = "deskOpen", ECDsa? signWith = null, string issuer = CommandIssuerKinds.Browser)
    {
        var command = new JObject { ["cmd"] = cmd, ["deviceId"] = dev, ["requestId"] = session };
        if (viewerKey is not null)
            command[DeskSealRules.ViewerKeyField] = viewerKey;
        var body = new CommandEnvelopeBody(aud, "01JDESKOPENCID000000000000", dev, "conn-nonce", null, null, command, 2, false, false, 1_000, 1_000, 61_000,
            new CommandIssuer(issuer, "擁有者的瀏覽器"), new CommandAuth(900, false));
        var json = CommandEnvelope.SerializeBody(body);
        return new CommandEnvelope(CommandEnvelope.Version, json, P256KeyRules.Sign(signWith ?? hub, CommandEnvelope.SignedData(json)));
    }

    public void Dispose() => hub.Dispose();
}

/// <summary>
/// 安全桌面的輸入：服務的幫手只收「伺服器簽的 deskOpen 帶來的那把公鑰」加密的輸入。
/// 信封的驗證（<see cref="CommandEnvelope.VerifyDeskOpen"/>）、信任的伺服器（安裝時記下）、幫手的 secureOpen／secureInput（真的管道、假的注入）、
/// 真的注入器在使用者的桌面一律不注入。固定的向量和網頁的 deskseal.test.mjs 是同一組
/// </summary>
public sealed class DeskSecureInputTests
{
    private const string Me = "S-1-5-21-1-2-3-1001";

    /// <summary>以服務的標籤、固定的兩把私鑰算出的第 1 包（網頁的測試用同一個值）</summary>
    internal const string SecureSealed1 = "cCCyRwyyq8nlYt1hm-bmm-OwXbU19vwZEzLsMnuHf7-RUkBmOY9zix6JYtG2tdrYabKzLRdMWv7K9BmrRGJrMySEP22ba9VmHMUO";

    private static readonly ReleaseIdentity Version = new(new ReleaseVersion(0, 2, 970), "abc1234");

    private static ECDiffieHellman Fixed(int first)
        => ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = Enumerable.Range(first, 32).Select(i => (byte)i).ToArray() });

    private sealed class FakeIdentifier(DeskServiceClientInfo info) : IDeskClientIdentifier
    {
        public DeskServiceClientInfo Identify(NamedPipeServerStream pipe) => info;
    }

    private sealed class FakeCapturer : IDesktopCapturer
    {
        public DeskServiceCaptureHeader Capture(DeskServiceCaptureRequest request, byte[] buffer)
        {
            var bytes = DeskServiceRules.CaptureBytes(request.OutW, request.OutH);
            buffer.AsSpan(0, bytes).Fill(1);
            return new DeskServiceCaptureHeader(DeskServiceRules.CaptureOk, bytes, "Winlogon", true, 0);
        }
    }

    /// <summary>假的注入：記下收到的事件；<see cref="Secure"/> 是 false 時當成現在不是安全桌面（不注入）</summary>
    internal sealed class FakeInjector : IDesktopInjector
    {
        public bool Secure { get; set; } = true;

        public List<(IReadOnlyList<DeskInputEvent> Events, DeskRect Screen)> Injected { get; } = new();

        public int Released;

        public bool Inject(IReadOnlyList<DeskInputEvent> events, DeskRect screen)
        {
            if (!Secure)
                return false;
            lock (Injected)
                Injected.Add((events, screen));
            return true;
        }

        public void ReleaseAll() => Interlocked.Increment(ref Released);
    }

    internal static async Task<(DeskPipeServer Server, string Pipe)> StartHelperAsync(FakeInjector? injector, Func<DeskServiceTrust?>? trust)
    {
        var pipe = DeskServiceRules.HelperPipeName(DeskServiceRules.TestServicePrefix + Guid.NewGuid().ToString("N")[..8], 1, Guid.NewGuid().ToString("N"));
        var server = new DeskPipeServer(pipe, system: false, new FakeIdentifier(new DeskServiceClientInfo(1, Me, Me)),
            new AgentPipeHandler(Version, system: false, 1, new FakeCapturer(), injector, trust));
        server.Start();
        await server.Listening.WaitAsync(TimeSpan.FromSeconds(10));
        return (server, pipe);
    }

    /// <summary>測試扮演看的一方：固定的私鑰（1～32）＋服務回的公鑰，以服務的標籤算金鑰</summary>
    private static byte[] ViewerSecureKey(string servicePublic, string session = DeskOpenSamples.Session)
    {
        using var viewer = Fixed(1);
        return DeskSealRules.DeriveKey(viewer, servicePublic, session, DeskSealRulesTests.ViewerPublic, servicePublic, DeskSealRules.SecureInfo)!;
    }

    private static string SealSecure(byte[] key, long n, string json = DeskSealRulesTests.Plain, string session = DeskOpenSamples.Session)
        => DeskSealRules.Seal(key, session, n, Encoding.UTF8.GetBytes(json), DeskSealRules.SecureInfo);

    // ---- 固定的向量：服務的標籤 ----

    [Fact]
    public void 固定的向量_服務的標籤算出的金鑰與密文和app的不同()
    {
        using var viewer = Fixed(1);
        using var target = Fixed(33);
        var secureKey = DeskSealRules.DeriveKey(target, DeskSealRulesTests.ViewerPublic, DeskOpenSamples.Session, DeskSealRulesTests.ViewerPublic, DeskSealRulesTests.TargetPublic,
            DeskSealRules.SecureInfo)!;
        Convert.ToHexString(secureKey).ToLowerInvariant().Should().NotBe(DeskSealRulesTests.KeyHex, "標籤不同，金鑰就不同");
        DeskSealRules.DeriveKey(viewer, DeskSealRulesTests.TargetPublic, DeskOpenSamples.Session, DeskSealRulesTests.ViewerPublic, DeskSealRulesTests.TargetPublic,
            DeskSealRules.SecureInfo).Should().Equal(secureKey);

        var ct = SealSecure(secureKey, 1);
        ct.Should().Be(SecureSealed1);
        DeskSealRules.Open(secureKey, DeskOpenSamples.Session, 1, ct, DeskSealRules.SecureInfo).Should().NotBeNull();
        DeskSealRules.Open(secureKey, DeskOpenSamples.Session, 1, ct).Should().BeNull("以 app 的標籤驗不過");
        DeskSealRules.Open(Convert.FromHexString(DeskSealRulesTests.KeyHex), DeskOpenSamples.Session, 1, ct, DeskSealRules.SecureInfo).Should().BeNull("app 的金鑰解不開給服務的");
    }

    [Fact]
    public void 線上的形狀_給服務的那一包帶k_status的svc與secure()
    {
        var forService = new JObject { ["e2e"] = new JObject { ["n"] = 2, ["ct"] = DeskSealRulesTests.Sealed1, ["k"] = "svc" } };
        DeskSealRules.SealedOf(forService, out var bad, out var service).Should().Be((2L, DeskSealRulesTests.Sealed1));
        (bad, service).Should().Be((false, true));
        DeskRules.ParseInput(forService).Should().BeEmpty("伺服器照樣轉送");

        DeskSealRules.SealedOf(new JObject { ["e2e"] = new JObject { ["n"] = 2, ["ct"] = DeskSealRulesTests.Sealed1 } }, out bad, out service).Should().NotBeNull();
        service.Should().BeFalse();
        DeskSealRules.SealedOf(new JObject { ["e2e"] = new JObject { ["n"] = 2, ["ct"] = DeskSealRulesTests.Sealed1, ["k"] = "app" } }, out bad, out _).Should().BeNull();
        bad.Should().BeTrue("不認得的 k");

        var status = DeskRules.StatusBody(null, null, secure: true, DeskSealRulesTests.TargetPublic);
        status.Value<bool>("secure").Should().BeTrue();
        DeskSealRules.ServicePublic(status).Should().Be(DeskSealRulesTests.TargetPublic);
        DeskSealRules.TargetPublic(status).Should().BeNull("svc 不是 kx");
        var plain = DeskRules.StatusBody(null, "只能看", secure: true, null);
        plain.ContainsKey("svc").Should().BeFalse();
        DeskRules.StatusBody(null, null).ContainsKey("secure").Should().BeFalse("不是安全桌面就不帶");
    }

    // ---- 信封的驗證 ----

    [Fact]
    public void 信封_伺服器簽的deskOpen_取出工作階段與看的一方的公鑰()
    {
        using var hub = new DeskOpenSamples();
        var proof = CommandEnvelope.VerifyDeskOpen(hub.Envelope(), hub.HubSpki, DeskOpenSamples.Aud, DeskOpenSamples.DeviceId, out var problem);
        problem.Should().BeEmpty();
        proof.Should().Be(new DeskOpenProof(DeskOpenSamples.Session, DeskSealRulesTests.ViewerPublic, CommandIssuerKinds.Browser, "擁有者的瀏覽器"));
    }

    [Fact]
    public void 信封_不是那台伺服器簽的_被改過_不是給這台的_不是deskOpen_沒有公鑰_都不收()
    {
        using var hub = new DeskOpenSamples();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DeskOpenProof? Verify(CommandEnvelope? envelope, string? spki = null)
            => CommandEnvelope.VerifyDeskOpen(envelope, spki ?? hub.HubSpki, DeskOpenSamples.Aud, DeskOpenSamples.DeviceId, out _);

        Verify(hub.Envelope(signWith: other)).Should().BeNull("別的金鑰簽的（這台自己造的）");
        Verify(hub.Envelope(), P256KeyRules.ExportSpki(other)).Should().BeNull("信任的不是這把");
        Verify(null).Should().BeNull();

        var good = hub.Envelope();
        Verify(good with { Body = good.Body.Replace(DeskSealRulesTests.ViewerPublic, DeskSealRulesTests.TargetPublic) }).Should().BeNull("換掉公鑰：簽章對不上");
        Verify(good with { V = "myriagent-hub-cmd-v2" }).Should().BeNull();

        Verify(hub.Envelope(dev: "d_1123456789ABCDEFGHJKMNPQRS")).Should().BeNull("給別台的");
        Verify(hub.Envelope(aud: "https://other.test")).Should().BeNull("別台伺服器的來源");
        Verify(hub.Envelope(cmd: "remoteExec")).Should().BeNull("不是遠端桌面的指令");
        Verify(hub.Envelope(viewerKey: null)).Should().BeNull("看的一方沒給公鑰（舊的網頁）");
        Verify(hub.Envelope(viewerKey: "garbage")).Should().BeNull();
        Verify(hub.Envelope(session: "x")).Should().BeNull();
        Verify(hub.Envelope(issuer: CommandIssuerKinds.Collaborator)).Should().BeNull("協作人不能用遠端桌面");
        Verify(hub.Envelope(issuer: CommandIssuerKinds.Device)).Should().NotBeNull("另一台已配對的電腦可以");
    }

    // ---- 信任的伺服器 ----

    [Fact]
    public void 信任的伺服器_來回一致_不合格的不收()
    {
        using var hub = new DeskOpenSamples();
        var trust = hub.Trust;
        DeskServiceRules.IsTrust(trust).Should().BeTrue();
        DeskServiceRules.ParseTrust(DeskServiceRules.SerializeTrust(trust)).Should().Be(trust);

        DeskServiceRules.IsTrust(trust with { HubSpki = "not-a-key" }).Should().BeFalse();
        DeskServiceRules.IsTrust(trust with { Aud = "hub.test" }).Should().BeFalse("要是來源（https://主機）");
        DeskServiceRules.IsTrust(trust with { Aud = "https://hub.test/path" }).Should().BeFalse();
        DeskServiceRules.IsTrust(trust with { DeviceId = "PC-1" }).Should().BeFalse();
        DeskServiceRules.IsTrust(null).Should().BeFalse();
        DeskServiceRules.ParseTrust("{}").Should().BeNull();
        DeskServiceRules.ParseTrust(null).Should().BeNull();
        DeskServiceRules.IsTrust(trust with { Aud = "http://127.0.0.1:5123" }).Should().BeTrue("本機測試的伺服器");
    }

    [Fact]
    public void 安裝器的參數_信任的伺服器三個一起給_只用在install()
    {
        using var hub = new DeskOpenSamples();
        var trust = hub.Trust;
        var parsed = DeskServiceArgs.Parse(["install", "--hub-key", trust.HubSpki, "--aud", trust.Aud, "--device", trust.DeviceId]);
        parsed.Mode.Should().Be(DeskServiceMode.Install);
        parsed.Trust.Should().Be(trust);
        DeskServiceArgs.Parse(["install"]).Trust.Should().BeNull("沒配對：裝了只能看");

        DeskServiceArgs.Parse(["install", "--hub-key", trust.HubSpki]).Mode.Should().Be(DeskServiceMode.Usage);
        DeskServiceArgs.Parse(["install", "--hub-key", "x", "--aud", trust.Aud, "--device", trust.DeviceId]).Mode.Should().Be(DeskServiceMode.Usage);
        DeskServiceArgs.Parse(["uninstall", "--hub-key", trust.HubSpki, "--aud", trust.Aud, "--device", trust.DeviceId]).Mode.Should().Be(DeskServiceMode.Usage);

        DeskServiceManager.InstallerArguments("install", trust).Should().Be($"install --hub-key {trust.HubSpki} --aud {trust.Aud} --device {trust.DeviceId}");
        DeskServiceManager.InstallerArguments("install", null).Should().Be("install");
        DeskServiceManager.InstallerArguments("uninstall", trust).Should().Be("uninstall");
        DeskServiceArgs.Parse(DeskServiceManager.InstallerArguments("install", trust).Split(' ')).Trust.Should().Be(trust, "app 組出來的參數安裝器讀得回來");
    }

    [Fact]
    public void 設定頁的狀態_安裝時記下的伺服器要是現在配對的這一台()
    {
        using var hub = new DeskOpenSamples();
        using var otherHub = new DeskOpenSamples();
        DeskServiceHubRules.SecureInputState(hub.Trust, hub.Trust).Should().Be("ready");
        DeskServiceHubRules.SecureInputState(null, hub.Trust).Should().Be("reinstall", "安裝時還沒配對");
        DeskServiceHubRules.SecureInputState(otherHub.Trust, hub.Trust).Should().Be("reinstall", "配對的伺服器換了");
        DeskServiceHubRules.SecureInputState(hub.Trust, null).Should().Be("unpaired");
    }

    // ---- 幫手（真的管道） ----

    [Fact]
    public async Task 幫手_驗過信封才給公鑰_以那把金鑰加密的輸入才注入_座標照最近擷取的那塊螢幕()
    {
        using var hub = new DeskOpenSamples();
        var injector = new FakeInjector();
        var (server, pipe) = await StartHelperAsync(injector, () => hub.Trust);
        using var dispose = server;
        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        client.Hello().Features.Should().Contain(DeskServiceRules.FeatureSecureInput);

        var servicePublic = client.SecureOpen(hub.Envelope(), DeskOpenSamples.Session);
        DeskSealRules.IsPublicKey(servicePublic).Should().BeTrue();
        var key = ViewerSecureKey(servicePublic);

        // 還沒擷取過：不知道看的一方看的是哪一塊螢幕，解開了也不注入
        client.SecureInput(DeskOpenSamples.Session, 1, SealSecure(key, 1)).Should().Be(0);
        injector.Injected.Should().BeEmpty();

        client.Capture(new DeskServiceCaptureRequest(1920, 0, 2560, 1440, 320, 180, true), new byte[320 * 180 * 4]);
        client.SecureInput(DeskOpenSamples.Session, 2, SealSecure(key, 2)).Should().Be(2);
        var (events, screen) = injector.Injected.Single();
        events[0].Should().Be(new DeskInputEvent("kd", Key: 65));
        events[1].Text.Should().Be("密碼 P@ss");
        screen.Should().Be(new DeskRect(1920, 0, 2560, 1440));

        // 現在不是安全桌面（解鎖了）：解開了也不注入——使用者的桌面不歸系統權限的幫手
        injector.Secure = false;
        client.SecureInput(DeskOpenSamples.Session, 3, SealSecure(key, 3)).Should().Be(0);
        injector.Injected.Should().HaveCount(1);
    }

    [Fact]
    public async Task 幫手_重送_別的金鑰_別的工作階段_app的標籤_都不注入()
    {
        using var hub = new DeskOpenSamples();
        var injector = new FakeInjector();
        var (server, pipe) = await StartHelperAsync(injector, () => hub.Trust);
        using var dispose = server;
        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        client.Capture(new DeskServiceCaptureRequest(0, 0, 640, 480, 64, 48, true), new byte[64 * 48 * 4]);
        var key = ViewerSecureKey(client.SecureOpen(hub.Envelope(), DeskOpenSamples.Session));

        client.SecureInput(DeskOpenSamples.Session, 1, SealSecure(key, 1)).Should().Be(2);
        void Denied(string session, long n, string ct)
            => FluentActions.Invoking(() => client.SecureInput(session, n, ct)).Should().Throw<InvalidOperationException>().WithMessage("*denied*");

        Denied(DeskOpenSamples.Session, 1, SealSecure(key, 1));
        Denied(DeskOpenSamples.Session, 2, SealSecure(Convert.FromHexString(DeskSealRulesTests.KeyHex), 2));
        Denied(DeskOpenSamples.Session, 2, DeskSealRules.Seal(key, DeskOpenSamples.Session, 2, Encoding.UTF8.GetBytes(DeskSealRulesTests.Plain)));
        Denied("01JDESKSEALVECTOR00000000B", 2, SealSecure(key, 2, session: "01JDESKSEALVECTOR00000000B"));
        injector.Injected.Should().HaveCount(1, "只有第一包注入了");
        // 解不開的不佔編號：正確的第 2 包照收
        client.SecureInput(DeskOpenSamples.Session, 2, SealSecure(key, 2)).Should().Be(2);
    }

    [Fact]
    public async Task 幫手_沒有信任的伺服器_信封驗不過_都不給公鑰()
    {
        using var hub = new DeskOpenSamples();
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DeskServiceTrust? trust = null;
        var injector = new FakeInjector();
        var (server, pipe) = await StartHelperAsync(injector, () => trust);
        using var dispose = server;
        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));

        FluentActions.Invoking(() => client.SecureOpen(hub.Envelope(), DeskOpenSamples.Session)).Should().Throw<InvalidOperationException>().WithMessage("*noTrust*");

        trust = hub.Trust;
        // 這台上的程式自己簽一個信封（它拿不到伺服器的私鑰）：不收
        FluentActions.Invoking(() => client.SecureOpen(hub.Envelope(signWith: attacker), DeskOpenSamples.Session)).Should().Throw<InvalidOperationException>().WithMessage("*denied*");
        FluentActions.Invoking(() => client.SecureOpen(hub.Envelope(viewerKey: null), DeskOpenSamples.Session)).Should().Throw<InvalidOperationException>().WithMessage("*denied*");
        // 沒有建立過工作階段：任何輸入都不收
        FluentActions.Invoking(() => client.SecureInput(DeskOpenSamples.Session, 1, DeskSealRulesTests.Sealed1)).Should().Throw<InvalidOperationException>().WithMessage("*denied*");
        injector.Injected.Should().BeEmpty();

        // 信封重放沒有用：拿到的只是服務新的公鑰，沒有看的一方的私鑰造不出解得開的輸入
        var replayed = client.SecureOpen(hub.Envelope(), DeskOpenSamples.Session);
        using var mine = DeskSealRules.CreateKey();
        var guess = DeskSealRules.DeriveKey(mine, replayed, DeskOpenSamples.Session, DeskSealRules.ExportPublicKey(mine), replayed, DeskSealRules.SecureInfo)!;
        client.Capture(new DeskServiceCaptureRequest(0, 0, 640, 480, 64, 48, true), new byte[64 * 48 * 4]);
        FluentActions.Invoking(() => client.SecureInput(DeskOpenSamples.Session, 1, SealSecure(guess, 1))).Should().Throw<InvalidOperationException>().WithMessage("*denied*");
        injector.Injected.Should().BeEmpty();
    }

    [Fact]
    public async Task 幫手_換一個工作階段_上一個的金鑰作廢_放開還按著的鍵()
    {
        using var hub = new DeskOpenSamples();
        var injector = new FakeInjector();
        var (server, pipe) = await StartHelperAsync(injector, () => hub.Trust);
        using var dispose = server;
        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        client.Capture(new DeskServiceCaptureRequest(0, 0, 640, 480, 64, 48, true), new byte[64 * 48 * 4]);
        var first = ViewerSecureKey(client.SecureOpen(hub.Envelope(), DeskOpenSamples.Session));
        client.SecureInput(DeskOpenSamples.Session, 1, SealSecure(first, 1)).Should().Be(2);

        const string next = "01JDESKSEALVECTOR00000000C";
        var second = ViewerSecureKey(client.SecureOpen(hub.Envelope(session: next), next), next);
        injector.Released.Should().Be(1);
        FluentActions.Invoking(() => client.SecureInput(DeskOpenSamples.Session, 2, SealSecure(first, 2))).Should().Throw<InvalidOperationException>();
        client.SecureInput(next, 1, SealSecure(second, 1, session: next)).Should().Be(2, "新的工作階段編號從 1 起算");
    }

    [Fact]
    public async Task 幫手_沒有注入器_不認得安全桌面輸入的指令()
    {
        using var hub = new DeskOpenSamples();
        var (server, pipe) = await StartHelperAsync(null, () => hub.Trust);
        using var dispose = server;
        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        client.Hello().Features.Should().NotContain(DeskServiceRules.FeatureSecureInput);
        FluentActions.Invoking(() => client.SecureOpen(hub.Envelope(), DeskOpenSamples.Session)).Should().Throw<InvalidOperationException>().WithMessage("*unsupported*");
    }

    // ---- app 這端整條路（連服務、開幫手、連幫手；兩個管道伺服器都在測試程序裡） ----

    private sealed class FakeAgents(string pipe) : ISessionAgents
    {
        public string Open(int sessionId) => pipe;
    }

    private sealed class NoProbe : IDeskSessionProbe
    {
        public DeskServiceProbe Probe(int sessionId) => new(sessionId, "Winlogon", true, 0, []);
    }

    internal static async Task<DeskPipeServer> StartServiceAsync(string servicePipe, string helperPipe)
    {
        var service = new DeskPipeServer(servicePipe, system: false, new FakeIdentifier(new DeskServiceClientInfo(1, Me, Me)),
            new ServicePipeHandler(Version, system: false, new NoProbe(), new FakeAgents(helperPipe)));
        service.Start();
        await service.Listening.WaitAsync(TimeSpan.FromSeconds(10));
        return service;
    }

    [Fact]
    public async Task app這端_擷取之後請幫手驗信封_拿到公鑰_加密的輸入轉過去注入()
    {
        using var hub = new DeskOpenSamples();
        var injector = new FakeInjector();
        var (helper, helperPipe) = await StartHelperAsync(injector, () => hub.Trust);
        using var a = helper;
        var servicePipe = "desk-test-" + Guid.NewGuid().ToString("N")[..12];
        using var b = await StartServiceAsync(servicePipe, helperPipe);
        var clock = 0L;
        using var capture = new SecureDeskCapture(() => true, servicePipe, () => clock += 1000);

        capture.OpenSecureInput(hub.Envelope(), DeskOpenSamples.Session).Should().BeNull("還沒連上幫手（還沒擷取過安全桌面）");
        capture.Capture(new DeskRect(0, 0, 640, 480), 64, 48, new byte[64 * 48 * 4], force: true).Should().Be(DeskCapture.Ok);

        var servicePublic = capture.OpenSecureInput(hub.Envelope(), DeskOpenSamples.Session);
        DeskSealRules.IsPublicKey(servicePublic).Should().BeTrue();
        var key = ViewerSecureKey(servicePublic!);
        capture.SendSecureInput(DeskOpenSamples.Session, 1, SealSecure(key, 1));
        injector.Injected.Single().Screen.Should().Be(new DeskRect(0, 0, 640, 480));

        // 幫手不收的（重送）：只記錄、不丟例外，連線留著
        capture.SendSecureInput(DeskOpenSamples.Session, 1, SealSecure(key, 1));
        capture.SendSecureInput(DeskOpenSamples.Session, 2, SealSecure(key, 2));
        injector.Injected.Should().HaveCount(2);
    }

    [Fact]
    public async Task app這端_幫手不收信封_沒有公鑰_舊的幫手連問都不問()
    {
        using var hub = new DeskOpenSamples();
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (helper, helperPipe) = await StartHelperAsync(new FakeInjector(), () => hub.Trust);
        using var a = helper;
        var servicePipe = "desk-test-" + Guid.NewGuid().ToString("N")[..12];
        using var b = await StartServiceAsync(servicePipe, helperPipe);
        var clock = 0L;
        using var capture = new SecureDeskCapture(() => true, servicePipe, () => clock += 1000);
        capture.Capture(new DeskRect(0, 0, 640, 480), 64, 48, new byte[64 * 48 * 4], force: true).Should().Be(DeskCapture.Ok);
        capture.OpenSecureInput(hub.Envelope(signWith: attacker), DeskOpenSamples.Session).Should().BeNull();
        capture.Capture(new DeskRect(0, 0, 640, 480), 64, 48, new byte[64 * 48 * 4], force: true).Should().Be(DeskCapture.Ok, "不收信封不影響擷取");

        // 舊的幫手（沒有注入器＝不宣告 secureInput）：不送它不認得的指令
        var (old, oldPipe) = await StartHelperAsync(null, () => hub.Trust);
        using var c = old;
        var oldServicePipe = "desk-test-" + Guid.NewGuid().ToString("N")[..12];
        using var d = await StartServiceAsync(oldServicePipe, oldPipe);
        using var oldCapture = new SecureDeskCapture(() => true, oldServicePipe, () => clock += 1000);
        oldCapture.Capture(new DeskRect(0, 0, 640, 480), 64, 48, new byte[64 * 48 * 4], force: true).Should().Be(DeskCapture.Ok);
        oldCapture.OpenSecureInput(hub.Envelope(), DeskOpenSamples.Session).Should().BeNull();
    }

    [Fact]
    public void 真的注入器_在使用者的桌面一律不注入()
    {
        // 一般權限的測試程序開不了安全桌面：真的注入器只在輸入桌面是安全桌面時才 SendInput，這裡一定回 false、什麼都不送
        using var injector = new DesktopInjector();
        injector.Inject([new DeskInputEvent("mm", 0.5, 0.5)], new DeskRect(0, 0, 640, 480)).Should().BeFalse();
        injector.ReleaseAll();
    }
}
