using System.Runtime.InteropServices;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Isolation;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;
using R = Myriagent.Sync.RemoteFileGuardReasons;

namespace Myriagent.Tests.Sync;

/// <summary>要實際建立的檔案系統物件（建不起來時測試顯示「略過」並說明原因，不寫成沒執行也通過）</summary>
public enum FileSystemFeature
{
    Junction,
    SymbolicLink,
    HardLink,
    ShortName,

    /// <summary>暫存資料夾所在磁碟區的掛載點（FSCTL_SET_REPARSE_POINT 指到 <c>\??\Volume{GUID}\</c>；合併前審查 PROD-2）</summary>
    VolumeMountPoint,
}

/// <summary>需要在暫存資料夾建立 junction、符號連結、硬連結或 8.3 短名的測試：先在暫存資料夾試建一次，建不起來就略過並記原因</summary>
public sealed class FileSystemFactAttribute : FactAttribute
{
    public FileSystemFactAttribute(FileSystemFeature feature)
    {
        if (TestFileSystem.Unavailable(feature) is { } reason)
            Skip = reason;
    }
}

/// <summary>測試用的檔案系統操作：硬連結、8.3 短名，以及各種物件建不建得起來的探測（每種只試一次）</summary>
internal static class TestFileSystem
{
    private static readonly Dictionary<FileSystemFeature, Lazy<string?>> Probes = new()
    {
        [FileSystemFeature.Junction] = new(() => Probe(dir =>
        {
            var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
            Junction.Create(Path.Combine(dir, "link"), target);
            return Junction.IsJunction(Path.Combine(dir, "link")) ? null : "建了 junction 卻讀不回來";
        }, "建不起來 junction")),
        [FileSystemFeature.SymbolicLink] = new(() => Probe(dir =>
        {
            var target = Path.Combine(dir, "target.txt");
            File.WriteAllText(target, "x");
            File.CreateSymbolicLink(Path.Combine(dir, "link.txt"), target);
            return null;
        }, "沒有建立符號連結的權限（要管理員或開發人員模式）")),
        [FileSystemFeature.HardLink] = new(() => Probe(dir =>
        {
            var target = Path.Combine(dir, "target.txt");
            File.WriteAllText(target, "x");
            return TryCreateHardLink(Path.Combine(dir, "link.txt"), target) ? null : "暫存資料夾的磁碟區不支援硬連結";
        }, "建不起來硬連結")),
        [FileSystemFeature.ShortName] = new(() => Probe(dir =>
        {
            var file = Path.Combine(dir, "averylongprobefilename.png");
            File.WriteAllText(file, "x");
            return Path.GetFileName(ShortPath(file)) is { } name && !name.Equals("averylongprobefilename.png", StringComparison.OrdinalIgnoreCase)
                ? null
                : "暫存資料夾的磁碟區沒有產生 8.3 短名（fsutil 8dot3name）";
        }, "取不到 8.3 短名")),
        [FileSystemFeature.VolumeMountPoint] = new(() => Probe(dir =>
        {
            using var links = new TestJunctions();
            var volume = TestJunctions.VolumeNameOf(Path.GetPathRoot(Path.GetFullPath(dir))!);
            var link = links.CreateVolumeMountPoint(Path.Combine(dir, "mnt"), volume);
            var target = new DirectoryInfo(link).LinkTarget;
            return target is not null && target.Contains("Volume{", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(link, "Windows")) == Directory.Exists(Path.Combine(Path.GetPathRoot(dir)!, "Windows"))
                ? null
                : $"建了掛載點卻讀不回來（LinkTarget {target ?? "null"}）";
        }, "建不起來磁碟區掛載點")),
    };

    public static string? Unavailable(FileSystemFeature feature) => Probes[feature].Value;

    public static bool TryCreateHardLink(string link, string existing) => CreateHardLinkW(link, existing, IntPtr.Zero);

    /// <summary>GetShortPathName；失敗時回原路徑</summary>
    public static string ShortPath(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        return length == 0 || length >= buffer.Length ? path : new string(buffer, 0, (int)length);
    }

    private static string? Probe(Func<string, string?> attempt, string failure)
    {
        using var temp = new TempDirectory("guard-probe");
        try
        {
            return attempt(temp.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return $"{failure}：{ex.Message}";
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint bufferLength);
}

/// <summary>
/// RemoteFileGuard（WP4.7 步 7；設計 8.7 的六步、SEC-22）：以真的檔案系統驗證。junction、符號連結、硬連結都建在同一個暫存根目錄裡
/// （遞迴刪除不會越界）；網路磁碟與卡住的 GetDriveType 以替身模擬，逾時用可以撥快的時鐘
/// </summary>
public sealed class RemoteFileGuardTests : IDisposable
{
    private static readonly RemoteFolderContext OtherDriveFolders = new(
        UserProfile: @"Q:\Users\Amy",
        LocalAppData: @"Q:\Users\Amy\AppData\Local",
        RoamingAppData: @"Q:\Users\Amy\AppData\Roaming",
        AppDataRoot: @"Q:\Users\Amy\AppData\Local\Myriagent",
        AccountConfigDirectories: []);

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly TempDirectory temp = new("remote-guard");
    private readonly string root;
    private readonly List<string> driveRoots = [];
    private int driveCalls;

    public RemoteFileGuardTests()
    {
        root = Long(temp.Root);
    }

    public void Dispose() => temp.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>暫存資料夾的長檔名、大寫磁碟代號的完整路徑（CI 的 %TEMP% 可能是 8.3 短名）</summary>
    private static string Long(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        var expanded = RemoteFileGuard.TryGetLongPath(full) ?? throw new InvalidOperationException($"展開不了：{full}");
        RemoteFileGuardRules.CheckSyntax(expanded, out var normalized).Should().BeNull(expanded);
        return normalized!;
    }

    private string Write(string relative, string content = "x")
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Dir(string relative) => Directory.CreateDirectory(Path.Combine(root, relative)).FullName;

    private RemoteFileGuard Guard(RemoteDriveKind kind = RemoteDriveKind.Fixed, TimeProvider? clock = null, TimeSpan? timeout = null)
        => new(r =>
        {
            Interlocked.Increment(ref driveCalls);
            lock (driveRoots)
                driveRoots.Add(r);
            return kind;
        }, timeout ?? Generous, clock ?? TimeProvider.System);

    private static RemoteFileScope Scope(string[] listRoots, string[]? exact = null, RemoteFolderContext? folders = null)
        => new("c1", listRoots, new HashSet<string>(exact ?? [], StringComparer.OrdinalIgnoreCase), folders ?? OtherDriveFolders);

    private static void ShouldBeClosed(string path)
    {
        // 守門員的串流還開著的話，獨占開檔會失敗（共用違規）
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        exclusive.CanRead.Should().BeTrue();
    }

    public static TheoryData<string, string> StringCorpus => new()
    {
        { @"\\server\share\a.png", R.Unc },
        { "//server/share/a.png", R.Unc },
        { @"\\?\UNC\server\share\a.png", R.Unc },
        { @"\\?\C:\a", R.Device },
        { @"\\.\C:\a", R.Device },
        { @"\??\C:\a", R.Device },
        { @"C:\a.png:ads", R.Ads },
        { @"C:\a.png::$DATA", R.Ads },
        { @"C:\CON", R.Device },
        { @"C:\a\NUL.txt", R.Device },
        { @"C:\a\..\..\x.png", R.Invalid },
        { "a.png", R.Invalid },
        { @"C:\a\b.png.", R.Invalid },
    };

    [Theory]
    [MemberData(nameof(StringCorpus))]
    public async Task 字串層拒絕且不碰檔案系統(string path, string reason)
    {
        var guard = Guard();
        var scope = Scope([@"C:\"], [path]);

        foreach (var purpose in Enum.GetValues<RemoteFilePurpose>())
        {
            var result = await guard.OpenAsync(path, purpose, scope, long.MaxValue, Ct);
            result.File.Should().BeNull();
            result.Reason.Should().Be(reason, $"{purpose}：{path}");
        }

        (await guard.CheckDirectoryAsync(path, scope, Ct)).Reason.Should().Be(reason);
        (await guard.NormalizeRootAsync(path, Ct)).Should().BeNull();
        driveCalls.Should().Be(0, "字串層失敗時連磁碟種類都不查");
        guard.FileSystemStages.Should().Be(0, "任何一步失敗都不碰檔案系統");
    }

    [Fact]
    public async Task 前綴陷阱與拒絕的檔名不查磁碟種類()
    {
        var repo = Dir("repo");
        var inside = Write(@"repo\a.png");
        var sibling = Write(@"repo2\a.png");
        var env = Write(@"repo\.env.local");
        var config = Write(@"repo\web.config");
        var guard = Guard();
        var scope = Scope([repo], [inside]);

        (await guard.OpenAsync(sibling, RemoteFilePurpose.List, scope, 1024, Ct)).Reason.Should().Be(R.Scope, "repo2 不在 repo 裡");
        (await guard.OpenAsync(sibling, RemoteFilePurpose.Media, scope, 1024, Ct)).Reason.Should().Be(R.Scope);
        (await guard.CheckDirectoryAsync(Path.Combine(root, "repo2"), scope, Ct)).Reason.Should().Be(R.Scope);
        (await guard.OpenAsync(env, RemoteFilePurpose.File, scope, 1024, Ct)).Reason.Should().Be(R.Name);
        (await guard.OpenAsync(config, RemoteFilePurpose.List, scope, 1024, Ct)).Reason.Should().Be(R.Name);
        driveCalls.Should().Be(0);
        guard.FileSystemStages.Should().Be(0);
    }

    [Fact]
    public async Task 範圍內的檔開得到_唯讀_真實路徑與長度正確()
    {
        var repo = Dir("repo");
        var file = Write(@"repo\sub\報告 a.png", "hello");
        var guard = Guard();

        var result = await guard.OpenAsync(file, RemoteFilePurpose.Media, Scope([repo], [file]), 1024, Ct);

        using var opened = result.File;
        opened.Should().NotBeNull(result.Reason);
        result.Reason.Should().BeNull();
        opened!.FinalPath.Should().BeEquivalentTo(file);
        opened.Length.Should().Be(5);
        opened.LastWriteTimeUtc.Should().Be(File.GetLastWriteTimeUtc(file));
        opened.Stream.CanWrite.Should().BeFalse();
        var buffer = new byte[16];
        var read = await opened.Stream.ReadAsync(buffer);
        System.Text.Encoding.UTF8.GetString(buffer, 0, read).Should().Be("hello");
        driveRoots.Should().Equal(root[..3]);
    }

    [FileSystemFact(FileSystemFeature.Junction)]
    public async Task junction被拒()
    {
        var scopeDir = Dir("scope");
        var target = Dir("target");
        Write(@"target\a.png");
        var link = Path.Combine(scopeDir, "link");
        Junction.Create(link, target);
        var viaLink = Path.Combine(link, "a.png");
        var guard = Guard();
        var scope = Scope([scopeDir], [viaLink]);

        (await guard.OpenAsync(viaLink, RemoteFilePurpose.List, scope, 1024, Ct)).Reason.Should().Be(R.Reparse);
        (await guard.OpenAsync(viaLink, RemoteFilePurpose.Media, scope, 1024, Ct)).Reason.Should().Be(R.Reparse);
        (await guard.CheckDirectoryAsync(link, scope, Ct)).Reason.Should().Be(R.Reparse);

        var rootLink = Path.Combine(root, "rootlink");
        Junction.Create(rootLink, target);
        var linkedScope = Scope([rootLink]);
        (await guard.OpenAsync(Path.Combine(rootLink, "a.png"), RemoteFilePurpose.File, linkedScope, 1024, Ct)).Reason
            .Should().Be(R.Reparse, "範圍的根本身是 junction 也拒絕");
        (await guard.CheckDirectoryAsync(rootLink, linkedScope, Ct)).Reason.Should().Be(R.Reparse);
    }

    [FileSystemFact(FileSystemFeature.SymbolicLink)]
    public async Task 符號連結被拒()
    {
        var scopeDir = Dir("scope");
        var target = Write(@"target\a.png");
        var fileLink = Path.Combine(scopeDir, "a.png");
        File.CreateSymbolicLink(fileLink, target);
        var dirLink = Path.Combine(scopeDir, "dirlink");
        Directory.CreateSymbolicLink(dirLink, Path.Combine(root, "target"));
        var guard = Guard();
        var scope = Scope([scopeDir], [fileLink]);

        (await guard.OpenAsync(fileLink, RemoteFilePurpose.Media, scope, 1024, Ct)).Reason.Should().Be(R.Reparse);
        (await guard.OpenAsync(Path.Combine(dirLink, "a.png"), RemoteFilePurpose.List, scope, 1024, Ct)).Reason.Should().Be(R.Reparse);
        (await guard.CheckDirectoryAsync(dirLink, scope, Ct)).Reason.Should().Be(R.Reparse);
    }

    [FileSystemFact(FileSystemFeature.Junction)]
    public async Task 開檔後真實路徑在範圍外被拒且串流已關()
    {
        var scopeDir = Dir("scope");
        var sub = Path.Combine(scopeDir, "sub");
        var file = Write(@"scope\sub\a.png", "inside");
        var outside = Dir("outside");
        var outsideFile = Write(@"outside\a.png", "outside");
        var guard = Guard();
        var swaps = 0;
        guard.BeforeOpenForTest = _ =>
        {
            // 重新解析點檢查之後、開檔之前把 sub 換成指到範圍外的 junction（TOCTOU）
            Directory.Move(sub, Path.Combine(root, $"moved{swaps++}"));
            Junction.Create(sub, outside);
        };

        var listed = await guard.OpenAsync(file, RemoteFilePurpose.List, Scope([scopeDir]), 1024, Ct);
        listed.File.Should().BeNull();
        listed.Reason.Should().Be(R.Scope, "真實路徑落在範圍外");
        ShouldBeClosed(outsideFile);

        Directory.Delete(sub);
        Directory.Move(Path.Combine(root, "moved0"), sub);
        var media = await guard.OpenAsync(file, RemoteFilePurpose.Media, Scope([scopeDir], [file]), 1024, Ct);
        media.File.Should().BeNull();
        media.Reason.Should().Be(R.Scope, "真實路徑不是登記過的那個檔");
        ShouldBeClosed(outsideFile);
    }

    [Fact]
    public void FromFinalPath以真的GetFinalPathNameByHandle驗()
    {
        var file = Write(@"a\資料\b.png");
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var final = RemoteFileGuard.GetFinalPath(stream.SafeFileHandle);

        final.Should().StartWith(@"\\?\");
        RemoteFileGuardRules.FromFinalPath(final).Should().BeEquivalentTo(file);
    }

    [Fact]
    public async Task 另一個handle以寫入開著照樣開得到()
    {
        var repo = Dir("repo");
        var file = Write(@"repo\log.txt", "abc");
        using var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write([1, 2, 3]);
        writer.Flush();

        var result = await Guard().OpenAsync(file, RemoteFilePurpose.File, Scope([repo]), 1024, Ct);

        using var opened = result.File;
        opened.Should().NotBeNull(result.Reason);
        opened!.Length.Should().Be(3);
    }

    [Theory]
    [InlineData(nameof(RemoteDriveKind.Network))]
    [InlineData(nameof(RemoteDriveKind.CdRom))]
    [InlineData(nameof(RemoteDriveKind.Ram))]
    [InlineData(nameof(RemoteDriveKind.Unknown))]
    [InlineData(nameof(RemoteDriveKind.NoRootDirectory))]
    public async Task 網路磁碟被拒(string kindName)
    {
        var kind = Enum.Parse<RemoteDriveKind>(kindName);
        var repo = Dir("repo");
        var file = Write(@"repo\a.png");
        var guard = Guard(kind);

        (await guard.OpenAsync(file, RemoteFilePurpose.Media, Scope([repo], [file]), 1024, Ct)).Reason.Should().Be(R.Drive);
        (await guard.CheckDirectoryAsync(repo, Scope([repo]), Ct)).Reason.Should().Be(R.Drive);
        (await guard.NormalizeRootAsync(repo, Ct)).Should().BeNull();
        guard.FileSystemStages.Should().Be(3, "只查了三次磁碟種類，沒有往下碰那個路徑");
    }

    [Fact]
    public async Task 卸除式磁碟照樣開得到()
    {
        var repo = Dir("repo");
        var file = Write(@"repo\a.png");
        using var opened = (await Guard(RemoteDriveKind.Removable).OpenAsync(file, RemoteFilePurpose.List, Scope([repo]), 1024, Ct)).File;
        opened.Should().NotBeNull();
    }

    [Fact]
    public async Task driveKind丟例外當成不明()
    {
        var repo = Dir("repo");
        var file = Write(@"repo\a.png");
        var guard = new RemoteFileGuard(_ => throw new IOException("網路磁碟機斷線"), Generous, TimeProvider.System);

        (await guard.OpenAsync(file, RemoteFilePurpose.List, Scope([repo]), 1024, Ct)).Reason.Should().Be(R.Drive);
    }

    [Fact]
    public async Task driveKind卡住時逾時()
    {
        var repo = Dir("repo");
        var file = Write(@"repo\a.png");
        var clock = new ManualTimeProvider();
        var release = new TaskCompletionSource();
        var calls = 0;
        var guard = new RemoteFileGuard(_ =>
        {
            Interlocked.Increment(ref calls);
            release.Task.Wait();
            return RemoteDriveKind.Fixed;
        }, RemoteFileGuard.DefaultTimeout, clock);
        try
        {
            var pending = guard.OpenAsync(file, RemoteFilePurpose.List, Scope([repo]), 1024, Ct);
            await clock.WaitForTimersAsync(1);

            clock.Advance(TimeSpan.FromMilliseconds(1999));
            clock.FiredCount.Should().Be(0);
            pending.IsCompleted.Should().BeFalse("還沒到 2 秒");

            clock.Advance(TimeSpan.FromMilliseconds(1));
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            result.File.Should().BeNull();
            result.Reason.Should().Be(R.Timeout);
            calls.Should().Be(1);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task 太大的檔被拒且串流已關()
    {
        var repo = Dir("repo");
        var file = Write(@"repo\a.png", "0123456789");
        var guard = Guard();

        (await guard.OpenAsync(file, RemoteFilePurpose.List, Scope([repo]), 9, Ct)).Reason.Should().Be(R.TooLarge);
        ShouldBeClosed(file);
        using var exact = (await guard.OpenAsync(file, RemoteFilePurpose.List, Scope([repo]), 10, Ct)).File;
        exact.Should().NotBeNull("剛好等於上限可以");
    }

    [Fact]
    public async Task 不存在與種類不符()
    {
        var repo = Dir("repo");
        var file = Write(@"repo\a.png");
        Dir(@"repo\folder.png");
        var guard = Guard();
        var scope = Scope([repo]);

        (await guard.OpenAsync(Path.Combine(repo, "missing.png"), RemoteFilePurpose.List, scope, 1024, Ct)).Reason.Should().Be(R.NotFound);
        (await guard.OpenAsync(Path.Combine(repo, @"nodir\a.png"), RemoteFilePurpose.List, scope, 1024, Ct)).Reason.Should().Be(R.NotFound);
        (await guard.OpenAsync(Path.Combine(file, "b.png"), RemoteFilePurpose.List, scope, 1024, Ct)).Reason.Should().Be(R.NotFound, "中間一層是檔案");
        (await guard.OpenAsync(Path.Combine(repo, "folder.png"), RemoteFilePurpose.List, scope, 1024, Ct)).Reason.Should().Be(R.NotFile);
        (await guard.CheckDirectoryAsync(file, scope, Ct)).Reason.Should().Be(R.NotDirectory);
        (await guard.CheckDirectoryAsync(Path.Combine(repo, "nodir"), scope, Ct)).Reason.Should().Be(R.NotFound);
    }

    [FileSystemFact(FileSystemFeature.HardLink)]
    public async Task 有多個硬連結的檔被拒()
    {
        var repo = Dir("repo");
        var secret = Write(@"outside\secret.txt", "token");
        var link = Path.Combine(repo, "a.png");
        TestFileSystem.TryCreateHardLink(link, secret).Should().BeTrue();
        var guard = Guard();

        (await guard.OpenAsync(link, RemoteFilePurpose.Media, Scope([repo], [link]), 1024, Ct)).Reason.Should().Be(R.HardLink);
        (await guard.OpenAsync(link, RemoteFilePurpose.File, Scope([repo]), 1024, Ct)).Reason.Should().Be(R.HardLink);
        ShouldBeClosed(link);

        File.Delete(secret);
        using var single = (await guard.OpenAsync(link, RemoteFilePurpose.File, Scope([repo]), 1024, Ct)).File;
        single.Should().NotBeNull("只剩一個名字就可以");
    }

    [FileSystemFact(FileSystemFeature.ShortName)]
    public async Task 短名展開後重跑()
    {
        var repo = Dir("repo");
        var file = Write(@"repo\averylongfilename.png", "img");
        var credentials = Write(@"repo\.credentials.json", "{}");
        var longDir = Dir(@"repo\alongdirectoryname");
        var shortFile = TestFileSystem.ShortPath(file);
        shortFile.Should().NotBeEquivalentTo(file);
        var guard = Guard();

        var result = await guard.OpenAsync(shortFile, RemoteFilePurpose.Media, Scope([repo], [file]), 1024, Ct);
        using (var opened = result.File)
        {
            opened.Should().NotBeNull(result.Reason);
            opened!.FinalPath.Should().BeEquivalentTo(file);
        }

        (await guard.OpenAsync(TestFileSystem.ShortPath(credentials), RemoteFilePurpose.File, Scope([repo]), 1024, Ct)).Reason
            .Should().Be(R.Name, "展開成長檔名之後才看得出是憑證檔");
        (await guard.NormalizeRootAsync(TestFileSystem.ShortPath(longDir), Ct)).Should().BeEquivalentTo(longDir);
        (await guard.CheckDirectoryAsync(TestFileSystem.ShortPath(longDir), Scope([repo]), Ct)).Directory.Should().BeEquivalentTo(longDir);
        (await guard.OpenAsync(Path.Combine(repo, "NOSUCH~1.PNG"), RemoteFilePurpose.List, Scope([repo]), 1024, Ct)).Reason
            .Should().Be(R.NotFound, "展開不了的短名");
    }

    [Fact]
    public async Task 禁止清單fail_closed()
    {
        var repo = Dir("repo");
        var file = Write(@"repo\a.png");
        var guard = Guard();
        var shortRoot = OtherDriveFolders with { AppDataRoot = @"Q:\Users\AMY~1\AppData\Local\Myriagent" };

        (await guard.OpenAsync(file, RemoteFilePurpose.Media, Scope([repo], [file], shortRoot), 1024, Ct)).Reason.Should().Be(R.Forbidden);
        (await guard.OpenAsync(file, RemoteFilePurpose.List, Scope([repo], folders: shortRoot), 1024, Ct)).Reason.Should().Be(R.Forbidden);
        (await guard.CheckDirectoryAsync(repo, Scope([repo], folders: shortRoot), Ct)).Reason.Should().Be(R.Forbidden);
        guard.FileSystemStages.Should().Be(0);
    }

    private (string Data, string Conversation, RemoteFolderContext Folders) DataRoot()
    {
        var data = Dir("data");
        var conversation = Dir(@"data\conversations\c1");
        Write(@"data\conversations\c1\items.json", "[]");
        Write(@"data\conversations\c1\queue.json", "{}");
        Write(@"data\conversations\c1\meta.json", "{}");
        Write(@"data\conversations\c1\attachments\photo.png", "png");
        Write(@"data\conversations\c1\workspace\out.txt", "txt");
        var folders = OtherDriveFolders with
        {
            AppDataRoot = data,
            AllowedDataSubtrees = [Path.Combine(conversation, "workspace"), Path.Combine(conversation, "attachments")],
            ConversationDirectory = conversation,
        };
        return (data, conversation, folders);
    }

    [Fact]
    public async Task items_json與queue_json被拒()
    {
        var (_, conversation, folders) = DataRoot();
        var guard = Guard();
        var normal = Scope([Path.Combine(conversation, "workspace"), Path.Combine(conversation, "attachments")], folders: folders);
        var wide = Scope([conversation], [Path.Combine(conversation, "items.json")], folders);

        foreach (var name in new[] { "items.json", "queue.json", "meta.json" })
        {
            var path = Path.Combine(conversation, name);
            (await guard.OpenAsync(path, RemoteFilePurpose.File, normal, 1024, Ct)).Reason.Should().Be(R.Scope, name);
            (await guard.OpenAsync(path, RemoteFilePurpose.File, wide, 1024, Ct)).Reason.Should().Be(R.Forbidden, name);
        }

        (await guard.CheckDirectoryAsync(conversation, wide, Ct)).Reason.Should().Be(R.Forbidden);
        guard.FileSystemStages.Should().Be(0);
    }

    [Fact]
    public async Task 附件資料夾在資料根目錄裡照樣通過()
    {
        var (_, conversation, folders) = DataRoot();
        var attachments = Path.Combine(conversation, "attachments");
        var photo = Path.Combine(attachments, "photo.png");
        var scope = Scope([Path.Combine(conversation, "workspace"), attachments], [photo], folders);
        var guard = Guard();

        using (var media = (await guard.OpenAsync(photo, RemoteFilePurpose.Media, scope, 1024, Ct)).File)
            media.Should().NotBeNull();
        using (var output = (await guard.OpenAsync(Path.Combine(conversation, @"workspace\out.txt"), RemoteFilePurpose.File, scope, 1024, Ct)).File)
            output.Should().NotBeNull();
        (await guard.CheckDirectoryAsync(attachments, scope, Ct)).Directory.Should().BeEquivalentTo(attachments);
        (await guard.OpenAsync(Path.Combine(root, @"data\conversations\c2\attachments\x.png"), RemoteFilePurpose.List,
            Scope([root], folders: folders), 1024, Ct)).Reason.Should().Be(R.Forbidden, "別的對話的附件不給");
    }

    [Fact]
    public async Task 資料夾檢查回真實路徑()
    {
        var repo = Dir("repo");
        var sub = Dir(@"repo\Sub Folder");
        var guard = Guard();
        var scope = Scope([repo]);

        var checkedRepo = await guard.CheckDirectoryAsync(repo, scope, Ct);
        checkedRepo.Reason.Should().BeNull();
        checkedRepo.Directory.Should().BeEquivalentTo(repo);
        (await guard.CheckDirectoryAsync(sub.ToUpperInvariant(), scope, Ct)).Directory.Should().BeEquivalentTo(sub)
            .And.EndWith(@"\repo\Sub Folder", "真實路徑照磁碟上的大小寫");
        (await guard.CheckDirectoryAsync(Dir("other"), scope, Ct)).Reason.Should().Be(R.Scope);
    }

    [Fact]
    public async Task NormalizeRootAsync()
    {
        var repo = Dir("repo");
        var guard = Guard();

        (await guard.NormalizeRootAsync(repo + @"\.\x\..", Ct)).Should().Be(repo);
        (await guard.NormalizeRootAsync(repo.ToLowerInvariant(), Ct)).Should().Be(char.ToUpperInvariant(repo[0]) + repo[1..].ToLowerInvariant(),
            "沒有短名時不碰檔案系統展開，只正規化");
        guard.FileSystemStages.Should().Be(2, "只查磁碟種類");
        (await guard.NormalizeRootAsync(null, Ct)).Should().BeNull();
        (await guard.NormalizeRootAsync(@"\\srv\share", Ct)).Should().BeNull();
        guard.FileSystemStages.Should().Be(2);
    }

    [Fact]
    public async Task 取消時丟例外()
    {
        var repo = Dir("repo");
        var file = Write(@"repo\a.png");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => Guard().OpenAsync(file, RemoteFilePurpose.List, Scope([repo]), 1024, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void 正式的磁碟種類查得到暫存資料夾的磁碟()
        => RemoteFileGuard.QueryDriveKind(root[..3]).Should().BeOneOf(RemoteDriveKind.Fixed, RemoteDriveKind.Removable);

    [Fact]
    public void 守門員的型別不碰SignalR與SQLite()
    {
        // 步 11 會把遠端檔案指令接到中樞（未配對的閉包）：守門員本身不能參考 SignalR、SQLite、ASP.NET Core（S6）
        var result = AssemblyIsolationScanner.Scan(typeof(AgentHub).Assembly.Location,
        [
            typeof(RemoteFileGuard).FullName!,
            typeof(RemoteFileGuardRules).FullName!,
            typeof(GuardedFile).FullName!,
            typeof(RemoteFileScope).FullName!,
            typeof(RemoteFileOpen).FullName!,
            typeof(RemoteDirectoryCheck).FullName!,
        ]);

        result.Findings.Should().BeEmpty();
        result.ScannedTypes.Should().Contain(typeof(RemoteFileGuard).FullName!, "掃描真的走過守門員");
        result.ScannedMethods.Should().BeGreaterThan(20);
    }
}
