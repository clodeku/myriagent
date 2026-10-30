using System.Runtime.InteropServices;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Tests.Sync;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 遠端選資料夾的探針（WP4.4 計畫步 6、D18）：純字串先擋（UNC 不碰檔案系統）→ 磁碟種類 → 展開短檔名、不存在 → 正規化 → 禁止清單 →
/// 真實路徑（junction）再判一次；禁止清單的資料夾在同一個背景工作裡展開；整體逾時。禁止清單用暫存資料夾組（不看這台真的使用者資料夾）
/// </summary>
public sealed class RemoteFolderProbeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly TempDirectory temp = new("folder-probe");
    private readonly TestJunctions junctions = new();

    public void Dispose()
    {
        // junction 先拆（只刪連結本身），再刪暫存資料夾
        junctions.Dispose();
        temp.Dispose();
    }

    /// <summary>暫存資料夾的長檔名完整路徑（CI 的 TEMP 可能是 8.3 短路徑）</summary>
    private static string Long(string path) => WindowsRemoteFolderFileSystem.Instance.LongPath(path) ?? throw new InvalidOperationException("資料夾不存在：" + path);

    private static string Normalized(string path) => RemoteFolderRules.TryNormalize(Long(path), out var normalized) ? normalized : throw new InvalidOperationException(path);

    /// <summary>暫存的使用者資料夾、資料根目錄與一個對話（c1）的禁止清單</summary>
    private RemoteFolderContext Context(string? appDataRoot = null, IReadOnlyList<string>? configDirectories = null)
    {
        var data = appDataRoot ?? temp.Combine("data");
        var conversation = Path.Combine(temp.Combine("data"), "conversations", "c1");
        return new RemoteFolderContext(
            temp.Combine("home"), temp.Combine("local"), temp.Combine("roaming"), data, configDirectories ?? [temp.Combine("accounts", "acc-1")],
            [Path.Combine(conversation, "workspace"), Path.Combine(conversation, "attachments")], conversation);
    }

    private Task<RemoteFolderCheck> CheckAsync(string? path, RemoteFolderContext? context = null, IRemoteFolderFileSystem? fs = null, TimeSpan? timeout = null)
        => RemoteFolderProbe.CheckAsync(path, context ?? Context(), timeout ?? Timeout, fs ?? WindowsRemoteFolderFileSystem.Instance);

    /// <summary>記下每個動作被呼叫幾次（照樣交給真的檔案系統）</summary>
    private sealed class CountingFileSystem(IRemoteFolderFileSystem? inner = null, RemoteDriveKind? kind = null) : IRemoteFolderFileSystem
    {
        private readonly IRemoteFolderFileSystem inner = inner ?? WindowsRemoteFolderFileSystem.Instance;
        private int driveKinds;
        private int longPaths;
        private int exists;
        private int finalPaths;

        /// <summary>交給檔案系統的每個路徑</summary>
        public Recorder<string> Paths { get; } = new();

        public int DriveKinds => Volatile.Read(ref driveKinds);

        public int LongPaths => Volatile.Read(ref longPaths);

        public int FinalPaths => Volatile.Read(ref finalPaths);

        public int Calls => DriveKinds + LongPaths + Volatile.Read(ref exists) + FinalPaths;

        public RemoteDriveKind DriveKind(string fullPath)
        {
            Interlocked.Increment(ref driveKinds);
            Paths.Add(fullPath);
            return kind ?? inner.DriveKind(fullPath);
        }

        public string? LongPath(string fullPath)
        {
            Interlocked.Increment(ref longPaths);
            Paths.Add(fullPath);
            return inner.LongPath(fullPath);
        }

        public bool DirectoryExists(string path)
        {
            Interlocked.Increment(ref exists);
            Paths.Add(path);
            return inner.DirectoryExists(path);
        }

        public string? FinalPath(string normalizedPath)
        {
            Interlocked.Increment(ref finalPaths);
            Paths.Add(normalizedPath);
            return inner.FinalPath(normalizedPath);
        }
    }

    /// <summary>磁碟種類卡住，直到測試放開（模擬離線的磁碟）</summary>
    private sealed class HangingFileSystem : IRemoteFolderFileSystem
    {
        public ManualResetEventSlim Release { get; } = new();

        public RemoteDriveKind DriveKind(string fullPath)
        {
            Release.Wait(TimeSpan.FromSeconds(30));
            return RemoteDriveKind.Fixed;
        }

        public string? LongPath(string fullPath) => WindowsRemoteFolderFileSystem.Instance.LongPath(fullPath);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public string? FinalPath(string normalizedPath) => WindowsRemoteFolderFileSystem.Instance.FinalPath(normalizedPath);
    }

    /// <summary>沒有 8.3 短檔名的磁碟上模擬：把指定的短路徑對到長路徑，其他交給真的檔案系統</summary>
    private sealed class ShortNameFileSystem(IReadOnlyDictionary<string, string> shortToLong) : IRemoteFolderFileSystem
    {
        public RemoteDriveKind DriveKind(string fullPath) => WindowsRemoteFolderFileSystem.Instance.DriveKind(fullPath);

        public string? LongPath(string fullPath)
            => shortToLong.FirstOrDefault(p => string.Equals(p.Key, fullPath, StringComparison.OrdinalIgnoreCase)).Value
               ?? WindowsRemoteFolderFileSystem.Instance.LongPath(fullPath);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public string? FinalPath(string normalizedPath) => WindowsRemoteFolderFileSystem.Instance.FinalPath(normalizedPath);
    }

    /// <summary>真實路徑回指定的值（模擬連結指到網路、沒有代號的磁碟區、另一個磁碟，或打不開），其他交給真的檔案系統</summary>
    private sealed class FinalPathFileSystem(Func<string, string?> finalPath, RemoteDriveKind? otherDriveKind = null) : IRemoteFolderFileSystem
    {
        public RemoteDriveKind DriveKind(string fullPath)
            => otherDriveKind is { } other && !string.Equals(fullPath[..1], Path.GetTempPath()[..1], StringComparison.OrdinalIgnoreCase)
                ? other
                : WindowsRemoteFolderFileSystem.Instance.DriveKind(fullPath);

        public string? LongPath(string fullPath) => WindowsRemoteFolderFileSystem.Instance.LongPath(fullPath);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public string? FinalPath(string normalizedPath) => finalPath(normalizedPath);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, [Out] char[] lpszShortPath, uint cchBuffer);

    /// <summary>真的 8.3 短路徑；這個磁碟沒有產生短檔名（或取不到）時回 null</summary>
    private static string? RealShortPath(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        if (length == 0 || length >= buffer.Length)
            return null;
        var shortPath = new string(buffer, 0, (int)length);
        return string.Equals(shortPath, path, StringComparison.OrdinalIgnoreCase) ? null : shortPath;
    }

    [Theory]
    [InlineData(@"\\srv\share")]
    [InlineData(@"\\srv\share\proj")]
    [InlineData(@"//srv/share")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\COM1")]
    [InlineData(@"relative\path")]
    [InlineData(@"C:relative")]
    [InlineData("")]
    [InlineData(null)]
    public async Task UNC_invalid_不碰檔案系統(string? path)
    {
        var fs = new CountingFileSystem();

        var check = await CheckAsync(path, fs: fs);

        check.Verdict.Should().Be(RemoteFolderVerdict.Invalid);
        check.Path.Should().BeNull();
        check.Detail.Should().NotBeNullOrWhiteSpace();
        fs.Calls.Should().Be(0, "UNC 與不是本機磁碟的路徑一律不碰檔案系統（光是檢查存在就會對外送 SMB 雜湊）");
    }

    [Fact]
    public async Task 禁止清單裡的UNC_不碰檔案系統_一律policy()
    {
        var work = temp.CreateDirectory("work");
        var fs = new CountingFileSystem();

        var check = await CheckAsync(work, Context(configDirectories: [@"\\srv\share\.claude"]), fs);

        check.Verdict.Should().Be(RemoteFolderVerdict.Policy, "禁止清單有正規化不了的項目：fail-closed");
        fs.Paths.All.Should().NotBeEmpty().And.OnlyContain(p => RemoteHandlerRules.LooksLikeLocalDrivePath(p), "禁止清單裡的 UNC 不交給檔案系統展開");
        (await CheckAsync(work, fs: fs)).Verdict.Should().Be(RemoteFolderVerdict.Ok, "對照：清單完整時同一個資料夾可以");
    }

    [Fact]
    public async Task 不存在_Missing()
    {
        var check = await CheckAsync(temp.Combine("nope", "deeper"));

        check.Verdict.Should().Be(RemoteFolderVerdict.Missing);
        check.Detail.Should().Contain("找不到資料夾");
    }

    [Fact]
    public async Task 是檔案不是資料夾_Missing()
    {
        var file = temp.WriteFile("a.txt");

        (await CheckAsync(file)).Verdict.Should().Be(RemoteFolderVerdict.Missing);
    }

    [Fact]
    public async Task 存在而且不在禁止清單_Ok_路徑是正規化的長檔名()
    {
        var work = temp.CreateDirectory(Path.Combine("work", "proj"));

        var check = await CheckAsync(work + @"\.\..\proj\");

        check.Verdict.Should().Be(RemoteFolderVerdict.Ok, check.Detail);
        check.Path.Should().Be(Normalized(work));
        check.Detail.Should().BeNull();
    }

    [Fact]
    public async Task 資料根目錄裡_Policy()
    {
        var inside = temp.CreateDirectory(Path.Combine("data", "accounts", "x"));

        (await CheckAsync(inside)).Verdict.Should().Be(RemoteFolderVerdict.Policy);
        (await CheckAsync(temp.CreateDirectory("data"))).Verdict.Should().Be(RemoteFolderVerdict.Policy, "資料根目錄本身");
        (await CheckAsync(temp.CreateDirectory(Path.Combine("data", "conversations", "c1")))).Verdict.Should().Be(RemoteFolderVerdict.Policy,
            "對話資料夾本身不免除（queue.json、items.json 是 CLI 原文）");
    }

    [Fact]
    public async Task 對話自己的workspace_Ok()
    {
        var workspace = temp.CreateDirectory(Path.Combine("data", "conversations", "c1", "workspace", "sub"));

        var check = await CheckAsync(workspace);

        check.Verdict.Should().Be(RemoteFolderVerdict.Ok, check.Detail);
        check.Path.Should().Be(Normalized(workspace));
        (await CheckAsync(temp.CreateDirectory(Path.Combine("data", "conversations", "c2", "workspace")))).Verdict
            .Should().Be(RemoteFolderVerdict.Policy, "別的對話的 workspace 不免除");
    }

    [Fact]
    public async Task 使用者資料夾的ssh與帳戶設定_Policy()
    {
        (await CheckAsync(temp.CreateDirectory(Path.Combine("home", ".ssh")))).Verdict.Should().Be(RemoteFolderVerdict.Policy);
        (await CheckAsync(temp.CreateDirectory(Path.Combine("home", ".claude", "projects")))).Verdict.Should().Be(RemoteFolderVerdict.Policy);
        (await CheckAsync(temp.CreateDirectory(Path.Combine("accounts", "acc-1", "sub")))).Verdict.Should().Be(RemoteFolderVerdict.Policy);
        (await CheckAsync(temp.CreateDirectory(Path.Combine("home", "repo")))).Verdict.Should().Be(RemoteFolderVerdict.Ok, "使用者資料夾的其他地方可以");
    }

    [Fact]
    public async Task 網路磁碟或光碟_Policy_不再往下查()
    {
        var work = temp.CreateDirectory("work");

        foreach (var kind in new[] { RemoteDriveKind.Network, RemoteDriveKind.CdRom, RemoteDriveKind.Unknown, RemoteDriveKind.NoRootDirectory })
        {
            var fs = new CountingFileSystem(kind: kind);
            var check = await CheckAsync(work, fs: fs);
            check.Verdict.Should().Be(RemoteFolderVerdict.Policy, kind.ToString());
            fs.LongPaths.Should().Be(0, "種類不合格就不展開、不查存在");
        }

        (await CheckAsync(work, fs: new CountingFileSystem(kind: RemoteDriveKind.Removable))).Verdict.Should().Be(RemoteFolderVerdict.Ok, "卸除式磁碟可以");
    }

    [Fact]
    public async Task 裝置名稱或資料流_invalid()
    {
        var work = temp.CreateDirectory("work");

        (await CheckAsync(work + @"\CON")).Verdict.Should().NotBe(RemoteFolderVerdict.Ok);
        (await CheckAsync(work + @":stream")).Verdict.Should().NotBe(RemoteFolderVerdict.Ok);
    }

    [Fact]
    public async Task 名稱像短檔名但展開不了_invalid()
    {
        // 真的叫做「ABCDEF~1」的資料夾：GetLongPathName 原樣回來，規則不收（不知道它對到哪裡）
        var odd = temp.CreateDirectory("ABCDEF~1");

        (await CheckAsync(odd)).Verdict.Should().Be(RemoteFolderVerdict.Invalid);
    }

    [Fact]
    public async Task 根目錄_Ok()
    {
        var root = Path.GetPathRoot(temp.Root)!;

        var check = await CheckAsync(root);

        check.Verdict.Should().Be(RemoteFolderVerdict.Ok, check.Detail);
        check.Path.Should().Be(char.ToUpperInvariant(root[0]) + @":\");
    }

    [Fact]
    public async Task 逾時_Timeout()
    {
        var fs = new HangingFileSystem();
        try
        {
            var check = await CheckAsync(temp.CreateDirectory("work"), fs: fs, timeout: TimeSpan.FromMilliseconds(100));

            check.Verdict.Should().Be(RemoteFolderVerdict.Timeout);
            check.Detail.Should().Contain("逾時");
        }
        finally
        {
            fs.Release.Set();
        }
    }

    [Fact]
    public async Task 使用者資料夾不知道_禁止清單不完整_Policy()
    {
        var work = temp.CreateDirectory("work");
        var context = Context() with { UserProfile = "" };

        (await CheckAsync(work, context)).Verdict.Should().Be(RemoteFolderVerdict.Policy);
    }

    [Fact]
    public async Task 禁止清單的資料夾還不存在_從存在的上層展開()
    {
        // 帳戶的設定資料夾還沒建立：照樣是禁止清單的一員（建立之後裡面的資料夾仍不收）
        var work = temp.CreateDirectory("work");
        var context = Context(configDirectories: [temp.Combine("not-yet", "acc-2")]);

        (await CheckAsync(work, context)).Verdict.Should().Be(RemoteFolderVerdict.Ok);
        (await CheckAsync(temp.CreateDirectory(Path.Combine("not-yet", "acc-2", "x")), context)).Verdict.Should().Be(RemoteFolderVerdict.Policy);
    }

    [Fact]
    public async Task 八點三短檔名_展開後判斷()
    {
        var forbidden = temp.CreateDirectory(Path.Combine("data", "LongerFolderName"));
        var allowed = temp.CreateDirectory("AllowedLongFolderName");
        var dataRoot = temp.Combine("data");

        // 這台的磁碟有產生 8.3 短檔名就用真的（附錄 B 步 6 記下這台的情形）；沒有就以替身把短路徑對到長路徑
        var shortForbidden = RealShortPath(forbidden);
        var shortAllowed = RealShortPath(allowed);
        IRemoteFolderFileSystem fs = WindowsRemoteFolderFileSystem.Instance;
        if (shortForbidden == null || shortAllowed == null)
        {
            shortForbidden = Path.Combine(dataRoot, "LONGER~1");
            shortAllowed = temp.Combine("ALLOWE~1");
            fs = new ShortNameFileSystem(new Dictionary<string, string> { [shortForbidden] = Long(forbidden), [shortAllowed] = Long(allowed) });
        }

        RemoteFolderRules.TryNormalize(shortAllowed, out _).Should().BeFalse("前提：規則本身不收短檔名");

        var blocked = await CheckAsync(shortForbidden, fs: fs);
        var ok = await CheckAsync(shortAllowed, fs: fs);

        blocked.Verdict.Should().Be(RemoteFolderVerdict.Policy, "短路徑展開成資料根目錄裡的長路徑");
        ok.Verdict.Should().Be(RemoteFolderVerdict.Ok, ok.Detail);
        ok.Path.Should().Be(Normalized(allowed), "套用的是展開後的長檔名");
    }

    [Fact]
    public async Task 禁止清單本身是短檔名_展開後照樣判斷()
    {
        var dataRoot = temp.CreateDirectory("DataRootLongName");
        var inside = temp.CreateDirectory(Path.Combine("DataRootLongName", "x"));
        var work = temp.CreateDirectory("work");
        var shortRoot = RealShortPath(dataRoot);
        IRemoteFolderFileSystem fs = WindowsRemoteFolderFileSystem.Instance;
        if (shortRoot == null)
        {
            shortRoot = temp.Combine("DATARO~1");
            fs = new ShortNameFileSystem(new Dictionary<string, string> { [shortRoot] = Long(dataRoot) });
        }

        var context = Context(appDataRoot: shortRoot);

        (await CheckAsync(work, context, fs)).Verdict.Should().Be(RemoteFolderVerdict.Ok, "短檔名的資料根目錄先展開，清單才完整（否則 fail-closed 一律不收）");
        (await CheckAsync(inside, context, fs)).Verdict.Should().Be(RemoteFolderVerdict.Policy);
    }

    // ───────────────────────────── 路徑上的 junction：以真實路徑再判一次 ─────────────────────────────

    /// <summary>對話 c1 的預設工作資料夾（允許的子樹；不存在就建）</summary>
    private string Workspace => temp.CreateDirectory(Path.Combine("data", "conversations", "c1", "workspace"));

    [FileSystemFact(FileSystemFeature.Junction)]
    public async Task workspace裡指到資料根目錄的junction_Policy_任何一層都一樣()
    {
        // 代理在自己的 workspace 裡不用管理員權限就能建 junction：字串上在允許的子樹裡，實際是整個資料根目錄
        var data = temp.CreateDirectory("data");
        temp.CreateDirectory(Path.Combine("data", "accounts", "acc-9", "claude"));
        var link = junctions.Create(Path.Combine(Workspace, "link-data"), data);

        (await CheckAsync(link)).Verdict.Should().Be(RemoteFolderVerdict.Policy, "link 本身");
        (await CheckAsync(Path.Combine(link, "accounts"))).Verdict.Should().Be(RemoteFolderVerdict.Policy, @"link\sub");
        (await CheckAsync(Path.Combine(link, "accounts", "acc-9", "claude"))).Verdict.Should().Be(RemoteFolderVerdict.Policy, @"link\sub\sub");
        (await CheckAsync(Path.Combine(link, "conversations", "c1"))).Verdict.Should().Be(RemoteFolderVerdict.Policy, "對話資料夾本身（queue.json、items.json）");
    }

    [FileSystemFact(FileSystemFeature.Junction)]
    public async Task 指到ssh帳戶設定別的對話的junction_Policy_junction在中間一層也一樣()
    {
        var ssh = temp.CreateDirectory(Path.Combine("home", ".ssh"));
        var account = temp.CreateDirectory(Path.Combine("accounts", "acc-1"));
        var otherConversation = temp.CreateDirectory(Path.Combine("data", "conversations", "c2", "workspace"));
        var work = temp.CreateDirectory("work");

        (await CheckAsync(junctions.Create(Path.Combine(work, "to-ssh"), ssh))).Verdict.Should().Be(RemoteFolderVerdict.Policy, @"範圍外：~\.ssh");
        (await CheckAsync(junctions.Create(Path.Combine(work, "to-account"), account))).Verdict.Should().Be(RemoteFolderVerdict.Policy, "帳戶的設定資料夾");
        var toHome = junctions.Create(Path.Combine(work, "to-home"), temp.Combine("home"));
        (await CheckAsync(Path.Combine(toHome, ".ssh"))).Verdict.Should().Be(RemoteFolderVerdict.Policy, "junction 在中間一層");
        (await CheckAsync(junctions.Create(Path.Combine(Workspace, "to-ssh"), ssh))).Verdict.Should().Be(RemoteFolderVerdict.Policy, "從允許的 workspace 指出去");
        (await CheckAsync(junctions.Create(Path.Combine(Workspace, "to-c2"), otherConversation))).Verdict.Should().Be(RemoteFolderVerdict.Policy,
            "別的對話的 workspace 不免除");
    }

    [FileSystemFact(FileSystemFeature.Junction)]
    public async Task junction的目標裡又有junction_一路解析到底()
    {
        // work\a → mid；mid\b → 資料根目錄：要求 work\a\b\accounts，解析兩次才知道實際在資料根目錄
        temp.CreateDirectory(Path.Combine("data", "accounts"));
        var mid = temp.CreateDirectory("mid");
        junctions.Create(Path.Combine(mid, "b"), temp.Combine("data"));
        junctions.Create(Path.Combine(mid, "c"), temp.CreateDirectory(Path.Combine("projects", "ok")));
        var a = junctions.Create(temp.Combine("work", "a"), mid);

        (await CheckAsync(Path.Combine(a, "b", "accounts"))).Verdict.Should().Be(RemoteFolderVerdict.Policy);
        var ok = await CheckAsync(Path.Combine(a, "c"));
        ok.Verdict.Should().Be(RemoteFolderVerdict.Ok, ok.Detail);
        ok.Path.Should().Be(Normalized(temp.Combine("projects", "ok")));
    }

    [FileSystemFact(FileSystemFeature.Junction)]
    public async Task 指到可以選的地方的junction_Ok_套用真實路徑()
    {
        var real = temp.CreateDirectory(Path.Combine("data", "conversations", "c1", "workspace", "real", "sub"));
        var inWorkspace = junctions.Create(Path.Combine(Workspace, "link-in"), Path.GetDirectoryName(real)!);

        var inside = await CheckAsync(Path.Combine(inWorkspace, "sub"));

        inside.Verdict.Should().Be(RemoteFolderVerdict.Ok, inside.Detail);
        inside.Path.Should().Be(Normalized(real), "套用的是實際的資料夾：之後 junction 改指向也影響不到這個對話");

        var proj = temp.CreateDirectory(Path.Combine("projects", "proj"));
        var outside = await CheckAsync(junctions.Create(temp.Combine("work", "link-proj"), proj));
        outside.Verdict.Should().Be(RemoteFolderVerdict.Ok, outside.Detail);
        outside.Path.Should().Be(Normalized(proj));

        // 從範圍外經資料根目錄的 junction 繞回這個對話自己的 workspace：真實路徑在允許的子樹裡
        var toData = junctions.Create(temp.Combine("work", "link-data"), temp.CreateDirectory("data"));
        var back = await CheckAsync(Path.Combine(toData, "conversations", "c1", "workspace"));
        back.Verdict.Should().Be(RemoteFolderVerdict.Ok, back.Detail);
        back.Path.Should().Be(Normalized(Workspace));
    }

    [FileSystemFact(FileSystemFeature.Junction)]
    public async Task 資料根目錄本身是junction_直接選它的目標也是Policy()
    {
        // 資料根目錄（或任何禁止的資料夾）本身是 junction：禁止清單也要照真實路徑比對，否則直接選目標就繞過字串比對
        var realData = temp.CreateDirectory("real-data");
        temp.CreateDirectory(Path.Combine("real-data", "accounts", "x"));
        var realWorkspace = temp.CreateDirectory(Path.Combine("real-data", "conversations", "c1", "workspace"));
        var dataLink = junctions.Create(temp.Combine("data-link"), realData);
        var conversation = Path.Combine(dataLink, "conversations", "c1");
        var context = new RemoteFolderContext(temp.Combine("home"), temp.Combine("local"), temp.Combine("roaming"), dataLink, [],
            [Path.Combine(conversation, "workspace"), Path.Combine(conversation, "attachments")], conversation);

        (await CheckAsync(Path.Combine(realData, "accounts", "x"), context)).Verdict.Should().Be(RemoteFolderVerdict.Policy, "直接選資料根目錄的目標");
        (await CheckAsync(realData, context)).Verdict.Should().Be(RemoteFolderVerdict.Policy);
        (await CheckAsync(Path.Combine(dataLink, "accounts", "x"), context)).Verdict.Should().Be(RemoteFolderVerdict.Policy, "字串比對本來就擋");
        var ok = await CheckAsync(realWorkspace, context);
        ok.Verdict.Should().Be(RemoteFolderVerdict.Ok, "這個對話的 workspace 經過 junction 也是允許的子樹（" + ok.Detail + "）");
    }

    // ── 磁碟區掛載點（合併前審查 PROD-2）：.NET 的 LinkTarget 回 Volume{GUID}\（拿掉 \??\），不能當成相對路徑接在連結的資料夾後面 ──

    /// <summary>暫存資料夾所在的磁碟區掛載到 <c>&lt;temp&gt;\mnt</c>（不需要管理員）；回掛載點與磁碟區名稱</summary>
    private (string Mount, string Volume) MountTempVolume()
    {
        var volume = TestJunctions.VolumeNameOf(Path.GetPathRoot(Long(temp.Root))!);
        return (junctions.CreateVolumeMountPoint(temp.Combine("mnt"), volume), volume);
    }

    /// <summary>同一個資料夾經掛載點的寫法：<c>&lt;temp&gt;\mnt\</c>＋去掉磁碟根目錄的長檔名路徑</summary>
    private string ViaMount(string mount, string path) => Path.Combine(mount, Long(path)[3..]);

    [FileSystemFact(FileSystemFeature.VolumeMountPoint)]
    public async Task 經磁碟區掛載點的資料夾_換成那個磁碟區的磁碟代號_Ok_套用真實路徑()
    {
        var real = temp.CreateDirectory(Path.Combine("projects", "real"));
        var (mount, _) = MountTempVolume();
        var viaMount = ViaMount(mount, real);
        Directory.Exists(viaMount).Should().BeTrue("前提：經掛載點走得到同一個資料夾");

        var check = await CheckAsync(viaMount);

        check.Verdict.Should().Be(RemoteFolderVerdict.Ok, check.Detail + "（修正前接成 <temp>\\Volume{GUID}\\…、不存在而回 Missing）");
        check.Path.Should().Be(Normalized(real), "套用的是磁碟代號的真實路徑");
    }

    [FileSystemFact(FileSystemFeature.VolumeMountPoint)]
    public async Task 經磁碟區掛載點_旁邊剛好有叫Volume_GUID的資料夾_也不會走到它()
    {
        var real = temp.CreateDirectory(Path.Combine("projects", "real"));
        var (mount, volume) = MountTempVolume();
        // 修正前：目標被當成相對路徑接在 <temp> 後面，走到這個假的資料夾
        var fake = temp.CreateDirectory(Path.Combine(volume[4..^1], Long(real)[3..]));

        var check = await CheckAsync(ViaMount(mount, real));

        check.Verdict.Should().Be(RemoteFolderVerdict.Ok, check.Detail);
        check.Path.Should().Be(Normalized(real)).And.NotBe(Normalized(fake), "不是旁邊那個名叫 Volume{GUID} 的資料夾");
    }

    [FileSystemFact(FileSystemFeature.VolumeMountPoint)]
    public async Task 資料根目錄經磁碟區掛載點_指到它磁碟代號位置的junction_Policy()
    {
        // 資料根目錄的設定值經過掛載點（同一個磁碟區另有磁碟代號）：禁止清單要以真實路徑比對，否則指到它磁碟代號位置的 junction 繞得過去
        var realData = temp.CreateDirectory("real-data");
        temp.CreateDirectory(Path.Combine("real-data", "accounts", "x"));
        var (mount, _) = MountTempVolume();
        var dataViaMount = ViaMount(mount, realData);
        var conversation = Path.Combine(dataViaMount, "conversations", "c1");
        var context = new RemoteFolderContext(temp.Combine("home"), temp.Combine("local"), temp.Combine("roaming"), dataViaMount, [],
            [Path.Combine(conversation, "workspace"), Path.Combine(conversation, "attachments")], conversation);
        var link = junctions.Create(temp.Combine("work", "j"), realData);

        (await CheckAsync(link, context)).Verdict.Should().Be(RemoteFolderVerdict.Policy, "junction 指到資料根目錄的磁碟代號位置（修正前是 Ok）");
        (await CheckAsync(Path.Combine(link, "accounts", "x"), context)).Verdict.Should().Be(RemoteFolderVerdict.Policy);
        (await CheckAsync(realData, context)).Verdict.Should().Be(RemoteFolderVerdict.Policy, "直接選它的磁碟代號位置");
        (await CheckAsync(dataViaMount, context)).Verdict.Should().Be(RemoteFolderVerdict.Policy, "字串比對本來就擋");
    }

    [Fact]
    public async Task 真實路徑打不開_Missing_不能跟進或不在本機磁碟代號_Policy_名稱不合格_Invalid()
    {
        var work = temp.CreateDirectory("work");
        var requested = Normalized(work);
        IRemoteFolderFileSystem Returning(string? value)
            => new FinalPathFileSystem(p => string.Equals(p, requested, StringComparison.OrdinalIgnoreCase) ? value : WindowsRemoteFolderFileSystem.Instance.FinalPath(p));

        (await CheckAsync(work, fs: Returning(null))).Verdict.Should().Be(RemoteFolderVerdict.Missing, "打不開：同找不到（conflict）");
        foreach (var unfollowable in new[] { RemoteFolderFinalPathRules.NotFollowed, @"\\?\UNC\srv\share\proj", @"\\?\Volume{0b1f0c1e-0000-0000-0000-100000000000}\proj", requested })
        {
            var check = await CheckAsync(work, fs: Returning(unfollowable));
            check.Verdict.Should().Be(RemoteFolderVerdict.Policy, unfollowable);
            check.Path.Should().BeNull();
            check.Detail.Should().Contain("連結", "說明是經過連結");
        }

        (await CheckAsync(work, fs: Returning(@"\\?\" + requested + @"\CON"))).Verdict.Should().Be(RemoteFolderVerdict.Invalid, "同直接選保留名稱");
        var ok = await CheckAsync(work, fs: Returning(@"\\?\" + requested));
        ok.Verdict.Should().Be(RemoteFolderVerdict.Ok, ok.Detail);
        ok.Path.Should().Be(requested, "真實路徑相同：照舊套用要求的路徑");
    }

    [Fact]
    public async Task 真實路徑換到另一個磁碟_看那個磁碟的種類()
    {
        var work = temp.CreateDirectory("work");
        var requested = Normalized(work);
        var other = (char.ToUpperInvariant(requested[0]) == 'Q' ? "R" : "Q") + @":\proj";
        string? Final(string p) => string.Equals(p, requested, StringComparison.OrdinalIgnoreCase) ? @"\\?\" + other : WindowsRemoteFolderFileSystem.Instance.FinalPath(p);

        (await CheckAsync(work, fs: new FinalPathFileSystem(Final, RemoteDriveKind.Network))).Verdict.Should().Be(RemoteFolderVerdict.Policy, "實際在網路磁碟");
        (await CheckAsync(work, fs: new FinalPathFileSystem(Final, RemoteDriveKind.CdRom))).Verdict.Should().Be(RemoteFolderVerdict.Policy);
        var removable = await CheckAsync(work, fs: new FinalPathFileSystem(Final, RemoteDriveKind.Removable));
        removable.Verdict.Should().Be(RemoteFolderVerdict.Ok, removable.Detail);
        removable.Path.Should().Be(other, "subst 或指到另一個磁碟區的 junction：套用真實路徑");
    }

    [FileSystemFact(FileSystemFeature.SymbolicLink)]
    public async Task 指到網路的符號連結_Policy_不跟進()
    {
        // .invalid 網域不會解析（RFC 6761）。探針解析時讀連結的目標、不跟進：是 policy「經過連結」；
        // 要是跟進了，開檔會失敗而變成 Missing（而且會對外送 SMB 雜湊）
        var link = junctions.CreateSymbolicLink(Path.Combine(temp.CreateDirectory("work"), "to-share"), @"\\myriagent-test.invalid\share");
        var fs = new CountingFileSystem();

        var check = await CheckAsync(link, fs: fs);

        check.Verdict.Should().Be(RemoteFolderVerdict.Policy, check.Detail);
        check.Detail.Should().Contain("連結");
        fs.Paths.All.Should().OnlyContain(p => RemoteHandlerRules.LooksLikeLocalDrivePath(p), "UNC 不交給檔案系統");
    }

    [FileSystemFact(FileSystemFeature.SymbolicLink)]
    public async Task 相對的符號連結指到可以選的地方_Ok_套用真實路徑()
    {
        var proj = temp.CreateDirectory(Path.Combine("work", "real", "proj"));
        temp.CreateDirectory("data");
        var link = junctions.CreateSymbolicLink(temp.Combine("work", "rel"), @"real\proj");
        var toData = junctions.CreateSymbolicLink(temp.Combine("work", "rel-data"), @"..\data");

        var ok = await CheckAsync(link);
        ok.Verdict.Should().Be(RemoteFolderVerdict.Ok, ok.Detail);
        ok.Path.Should().Be(Normalized(proj));
        (await CheckAsync(toData)).Verdict.Should().Be(RemoteFolderVerdict.Policy, "相對的符號連結指到資料根目錄");
    }
}
