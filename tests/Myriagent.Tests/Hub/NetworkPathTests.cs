using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>網路芳鄰的路徑規則（NetworkPathRules）：UNC 的格式、上一層、只收區域網路、不收這台自己</summary>
public sealed class NetworkPathRulesTests
{
    [Fact]
    public void 拆UNC_格式不合格的不收()
    {
        NetworkPathRules.TryParse(@"\\nas\資料\2026\a.txt", out var host, out var share, out var rest).Should().BeTrue();
        host.Should().Be("nas");
        share.Should().Be("資料");
        rest.Should().Equal("2026", "a.txt");
        NetworkPathRules.TryParse(@"\\nas", out _, out var none, out _).Should().BeTrue();
        none.Should().BeNull();
        foreach (var bad in new[] { @"\\?\C:\x", @"\\.\pipe\x", @"\\nas\s\..\x", @"\\nas\s\a.txt:secret", @"\\nas\s\a.", @"\\na s\x", @"\\nas\\x", "C:\\x", @"\\", null })
            NetworkPathRules.TryParse(bad, out _, out _, out _).Should().BeFalse(bad ?? "null");
        NetworkPathRules.Normalize(@"\\nas\資料\").Should().Be(@"\\nas\資料");
        NetworkPathRules.Normalize(@"\\").Should().Be(@"\\");
    }

    [Fact]
    public void 上一層_分享到電腦到網路到磁碟與常用資料夾()
    {
        NetworkPathRules.Parent(@"\\nas\s\a\b").Should().Be(@"\\nas\s\a");
        NetworkPathRules.Parent(@"\\nas\s").Should().Be(@"\\nas");
        NetworkPathRules.Parent(@"\\nas").Should().Be(@"\\");
        NetworkPathRules.Parent(@"\\").Should().BeNull();
        NetworkPathRules.IsBlockedShare("ipc$").Should().BeTrue();
        NetworkPathRules.IsBlockedShare("資料").Should().BeFalse();
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.88.88", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("100.100.1.1", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12::1", true)]
    [InlineData("2001:4860::8888", false)]
    [InlineData("::ffff:192.168.1.1", true)]
    public void 區域網路的位址(string address, bool lan) => NetworkPathRules.IsLanAddress(IPAddress.Parse(address)).Should().Be(lan);

    [Fact]
    public void 主機判斷_這台自己_解析不出_有一個不在區網都不收()
    {
        IPAddress[] local = [IPAddress.Parse("192.168.1.5"), IPAddress.Loopback];
        NetworkPathRules.HostVerdict("nas", [IPAddress.Parse("192.168.1.20")], local, "MYPC").Should().BeNull();
        NetworkPathRules.HostVerdict("localhost", [IPAddress.Parse("192.168.1.20")], local, "MYPC").Should().Be(NetworkPathReasons.Self);
        NetworkPathRules.HostVerdict("mypc", [IPAddress.Parse("192.168.1.20")], local, "MYPC").Should().Be(NetworkPathReasons.Self);
        NetworkPathRules.HostVerdict("mypc.lan", [IPAddress.Parse("192.168.1.20")], local, "MYPC").Should().Be(NetworkPathReasons.Self);
        NetworkPathRules.HostVerdict("x", [IPAddress.Parse("192.168.1.5")], local, "MYPC").Should().Be(NetworkPathReasons.Self);
        NetworkPathRules.HostVerdict("x", [IPAddress.Parse("127.0.0.2")], local, "MYPC").Should().Be(NetworkPathReasons.Self);
        NetworkPathRules.HostVerdict("x", [], local, "MYPC").Should().Be(NetworkPathReasons.Unresolved);
        NetworkPathRules.HostVerdict("x", [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("8.8.8.8")], local, "MYPC").Should().Be(NetworkPathReasons.NotLan);
        NetworkPathRules.Explain(NetworkPathReasons.NotLan).Should().Contain("區域網路");
    }
}

/// <summary>
/// 電腦狀態面板的檔案傳輸裡的網路芳鄰（AgentHub.DeviceFiles.cs 的網路分支）：假的網路資訊；真的 SMB 讀寫只在這台的
/// 系統管理分享經區網位址讀得到時跑（把這台的區網位址假裝成另一台）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class DeviceNetworkFilesTests
{
    private sealed class FakeNetwork : INetworkShares
    {
        public Dictionary<string, IPAddress[]> Hosts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<IPAddress> Local { get; } = [IPAddress.Parse("192.168.1.5")];

        public string MachineName { get; set; } = "MYPC";

        public List<(string Root, string Target)> Mapped { get; } = [];

        public List<string> ComputerList { get; } = [];

        public Dictionary<string, List<string>> ShareList { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IPAddress>>(IPAddress.TryParse(host, out var ip) ? [ip] : Hosts.TryGetValue(host, out var list) ? list : []);

        public IReadOnlyCollection<IPAddress> LocalAddresses() => Local;

        public IReadOnlyList<(string Root, string Target)> MappedDrives() => Mapped;

        public IReadOnlyList<string> Computers() => ComputerList;

        public IReadOnlyList<string> Shares(string host) => ShareList.TryGetValue(host, out var list) ? list : [];

        public long? FreeSpace(string directory) => NetworkShares.Instance.FreeSpace(directory);
    }

    private sealed class FakeTransfer : IRemoteFileTransfer
    {
        public bool CanTransfer => true;

        public Recorder<(string ConversationId, string Kind, IReadOnlyList<string> Paths)> Registered { get; } = new();

        public Task<IReadOnlyList<string>> RegisterRefsAsync(string conversationId, string kind, IReadOnlyList<string> fullPaths, CancellationToken ct)
        {
            Registered.Add((conversationId, kind, fullPaths.ToList()));
            IReadOnlyList<string> refs = fullPaths.Select(_ => MediaRefIssueRules.NewRef(RandomNumberGenerator.GetBytes)).ToList();
            return Task.FromResult(refs);
        }

        public Task DownloadUploadAsync(RemoteUpload upload, string destinationFile, CancellationToken ct) => File.WriteAllBytesAsync(destinationFile, new byte[upload.Size], ct);
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            H = new HubHarness();
            H.Ui.Invoke(() =>
            {
                H.Settings.Sync.RemoteControlLevel = SyncSettings.LevelControl;
                H.Hub.RemoteFileTransferForTest = Transfer;
                H.Hub.DeviceFilesElevatedForTest = () => false;
                H.Hub.DeviceDriveRootsForTest = () => [];
                H.Hub.DevicePlacesForTest = () => [];
                H.Hub.DeviceNetworkForTest = Net;
            });
        }

        public HubHarness H { get; }

        public FakeNetwork Net { get; } = new();

        public FakeTransfer Transfer { get; } = new();

        public async Task<(RemoteOutcome Outcome, IReadOnlyList<JObject> Output)> RunAsync(string cmd, JObject args)
        {
            var command = new JObject { ["cmd"] = cmd, ["deviceId"] = RemoteSamples.DeviceId };
            foreach (var p in args.Properties())
                command[p.Name] = p.Value;
            var context = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, null, null, 2, false, 1_000, 2_000, false, "電腦「書房」");
            var output = new Recorder<JObject>();
            var outcome = await H.Ui.Invoke(() => H.Hub.ExecuteRemoteAsync(context, command, output.Add)).WaitAsync(TimeSpan.FromSeconds(40));
            return (outcome, output.All);
        }

        public async Task<JObject> BrowseAsync(string? path)
        {
            var (outcome, output) = await RunAsync(RemoteCommandCatalog.DeviceBrowse, new JObject { ["requestId"] = "b1", ["path"] = path is null ? JValue.CreateNull() : path });
            outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
            return output.Single();
        }

        public void Dispose() => H.Dispose();
    }

    private static List<string?> Names(JObject files) => files["entries"]!.Cast<JObject>().Select(e => e.Value<string>("name")).ToList();

    [Fact]
    public async Task 虛擬根多了網路與網路磁碟機_網路列出電腦_電腦列出分享()
    {
        using var rig = new Rig();
        rig.Net.Mapped.Add((@"Z:\", @"\\nas\資料"));
        rig.Net.ComputerList.AddRange(["NAS", "PRINTER-01", "bad name"]);
        rig.Net.Hosts["nas"] = [IPAddress.Parse("192.168.1.20")];
        rig.Net.ShareList["nas"] = ["資料", "IPC$", "影片"];

        var root = await rig.BrowseAsync(null);
        Names(root).Should().Contain([@"Z: (\\nas\資料)", "網路"]);
        root["entries"]!.Cast<JObject>().Single(e => e.Value<string>("name") == "網路").Value<string>("path").Should().Be(@"\\");

        var network = await rig.BrowseAsync(@"\\");
        Names(network).Should().Equal("NAS", "PRINTER-01");
        network["parent"]!.Type.Should().Be(JTokenType.Null);

        var nas = await rig.BrowseAsync(@"\\nas");
        Names(nas).Should().Equal("資料", "影片");
        nas.Value<string>("parent").Should().Be(@"\\");
        nas["entries"]!.Cast<JObject>().First().Value<string>("path").Should().Be(@"\\nas\資料");
    }

    [Fact]
    public async Task 網際網路的主機與這台自己一律不連_以error說明()
    {
        using var rig = new Rig();
        rig.Net.Hosts["evil.example.com"] = [IPAddress.Parse("203.0.113.5")];

        (await rig.BrowseAsync(@"\\evil.example.com\x")).Value<string>("error").Should().Contain("區域網路");
        (await rig.BrowseAsync(@"\\8.8.8.8")).Value<string>("error").Should().Contain("區域網路");
        (await rig.BrowseAsync(@"\\localhost\C$\Users")).Value<string>("error").Should().Contain("自己");
        (await rig.BrowseAsync(@"\\192.168.1.5\C$")).Value<string>("error").Should().Contain("自己");
        (await rig.BrowseAsync(@"\\mypc\C$")).Value<string>("error").Should().Contain("自己");
        (await rig.BrowseAsync(@"\\nobody\x")).Value<string>("error").Should().Contain("找不到");

        var (fetch, output) = await rig.RunAsync(RemoteCommandCatalog.DeviceFetch, new JObject { ["requestId"] = "f1", ["path"] = @"\\8.8.8.8\x\a.txt" });
        fetch.State.Should().Be(RemoteResultStates.Rejected);
        fetch.Code.Should().Be(RemoteResultCodes.Policy);
        output.Should().BeEmpty();
        rig.Transfer.Registered.Count.Should().Be(0);

        var (put, _) = await rig.RunAsync(RemoteCommandCatalog.DevicePut, new JObject
        {
            ["requestId"] = "p1", ["folder"] = @"\\localhost\C$\Temp",
            ["uploads"] = new JArray(new JObject { ["uploadId"] = "u1", ["name"] = "a.txt", ["size"] = 1, ["sha256"] = new string('a', 64) }),
        });
        put.State.Should().Be(RemoteResultStates.Rejected);
        put.Code.Should().Be(RemoteResultCodes.Policy);
    }

    /// <summary>這台的區網 IPv4（沒有就是 null）</summary>
    private static IPAddress? LanAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && NetworkPathRules.IsLanAddress(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));

    [Fact]
    public async Task 真的SMB_經區網位址讀這台的系統管理分享_列資料夾_下載參照_存檔改名()
    {
        var lan = LanAddress();
        using var temp = new TempDirectory("device-net");
        var unc = lan is null ? null : @"\\" + lan + @"\" + temp.Root[0] + "$" + temp.Root[2..];
        if (unc is null || !Directory.Exists(unc))
            return;   // 這台沒有區網位址或讀不到系統管理分享（不是系統管理員）：跳過
        temp.WriteFile("報告.txt", "hello");
        using var rig = new Rig();
        rig.Net.Local.Clear();   // 把這台的區網位址當成另一台
        rig.Net.MachineName = "SOMEONE-ELSE";

        var files = await rig.BrowseAsync(unc);
        files["error"]!.Type.Should().Be(JTokenType.Null, files.ToString());
        Names(files).Should().Contain("報告.txt");
        files.Value<string>("parent").Should().Be(NetworkPathRules.Parent(unc));

        var (fetch, output) = await rig.RunAsync(RemoteCommandCatalog.DeviceFetch, new JObject { ["requestId"] = "f1", ["path"] = unc + @"\報告.txt" });
        fetch.State.Should().Be(RemoteResultStates.Done, fetch.Message);
        output.Single().Value<long>("size").Should().Be(5);
        rig.Transfer.Registered.All.Single().Paths.Single().Should().Be(unc + @"\報告.txt");

        var (put, _) = await rig.RunAsync(RemoteCommandCatalog.DevicePut, new JObject
        {
            ["requestId"] = "p1", ["folder"] = unc,
            ["uploads"] = new JArray(new JObject { ["uploadId"] = "u1", ["name"] = "報告.txt", ["size"] = 3, ["sha256"] = new string('a', 64) }),
        });
        put.State.Should().Be(RemoteResultStates.Done, put.Message);
        await rig.H.WaitUntilAsync(() => rig.H.Hub.DeviceXferSnapshot()?.Any(x => x.State == "saved") == true, "存到網路資料夾", 15_000);
        File.Exists(temp.Combine("報告 (2).txt")).Should().BeTrue();
        File.ReadAllText(temp.Combine("報告.txt")).Should().Be("hello", "絕不覆寫");
    }
}
