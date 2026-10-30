using System.Diagnostics;
using System.Text;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Cli;

/// <summary>
/// 執行檔身分的讀取（FileId、大小、修改時間、版本資訊、npm 套件版本）：在暫存資料夾照 Claude 的換檔順序操作真的檔案，
/// 並驗證讀取時另一個執行緒改名不會失敗（Claude 換檔靠改名；只用 FileShare.Read 開著會讓它失敗）
/// </summary>
public sealed class CliBinaryIdentityRulesTests
{
    /// <summary>ERROR_SHARING_VIOLATION 的 HRESULT（0x80070020）</summary>
    private const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>有版本資源的真檔案（測試主機一定有的組件）：當成 claude.exe 的複本</summary>
    private static string VersionedFile => typeof(Newtonsoft.Json.JsonConvert).Assembly.Location;

    [Fact]
    public void ParseFileId_FileIdInfo的16個位元組依LittleEndian解讀_與fsutil印的十六進位相同()
    {
        // 研究實測（fsutil file queryfileid）：bin\claude.exe＝0x000000000000000000220000003bf109、versions\2.1.295＝0x000000000000000000080000003bf11c
        var bin = new byte[] { 0x09, 0xF1, 0x3B, 0x00, 0x00, 0x00, 0x22, 0x00, 0, 0, 0, 0, 0, 0, 0, 0 };
        var versions = new byte[] { 0x1C, 0xF1, 0x3B, 0x00, 0x00, 0x00, 0x08, 0x00, 0, 0, 0, 0, 0, 0, 0, 0 };

        CliBinaryIdentityRules.ParseFileId(bin).Should().Be((UInt128)0x220000003BF109UL);
        $"{CliBinaryIdentityRules.ParseFileId(bin):x32}".Should().Be("000000000000000000220000003bf109");
        $"{CliBinaryIdentityRules.ParseFileId(versions):x32}".Should().Be("000000000000000000080000003bf11c");

        var high = new byte[16];
        high[8] = 0x01;
        high[15] = 0xAB;
        CliBinaryIdentityRules.ParseFileId(high).Should().Be(new UInt128(0xAB00_0000_0000_0001UL, 0), "後 8 個位元組是高位");

        var act = () => CliBinaryIdentityRules.ParseFileId(new byte[8]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryReadStamp_照Claude的換檔順序_改名不換FileId_換上的新檔FileId不同但大小與修改時間相同()
    {
        using var temp = new TempDirectory("cli-identity");
        var exe = temp.WriteFile(@"bin\claude.exe", new string('a', 4096));
        var source = temp.WriteFile(@"versions\2.1.297", new string('a', 4096));
        File.SetLastWriteTimeUtc(source, new DateTime(2026, 10, 9, 14, 49, 39, 770, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(exe, new DateTime(2026, 10, 9, 14, 49, 39, 770, DateTimeKind.Utc));

        var before = CliBinaryIdentityRules.TryReadStamp(exe)!.Value;
        before.Size.Should().Be(4096);
        before.LastWriteUtc.Should().Be(File.GetLastWriteTimeUtc(exe));
        before.LastWriteUtc.Kind.Should().Be(DateTimeKind.Utc);
        before.FileId.Should().NotBe(UInt128.Zero);
        before.VolumeSerial.Should().NotBe(0UL);

        // 2.1.296 的換檔：複製成 .new（保留修改時間）→ 執行中的改名成 .old.<ms>.<pid> → .new 改名成 claude.exe
        var staged = exe + ".new.48780.1791575087343.abcd1234";
        var backup = exe + ".old.1791575087343.48780";
        File.Copy(source, staged);
        File.Move(exe, backup);
        File.Move(staged, exe);

        var after = CliBinaryIdentityRules.TryReadStamp(exe)!.Value;
        after.FileId.Should().NotBe(before.FileId, "換上的是另一個檔案：路徑不變、FileId 一定變");
        after.Size.Should().Be(before.Size);
        after.LastWriteUtc.Should().Be(before.LastWriteUtc, "複製保留修改時間（所以不能只看大小與時間）");
        CliBinaryIdentityRules.TryReadStamp(backup).Should().Be(before, "改名不換檔案：舊檔帶著原本的 FileId 變成 .old");

        var old = CliBinaryIdentityRules.Create(AgentProvider.Claude, CliInstallKind.Native, exe, "2.1.296", before)!;
        var fresh = CliBinaryIdentityRules.Create(AgentProvider.Claude, CliInstallKind.Native, exe, "2.1.297", after)!;
        CliBinaryIdentity.SameOrUnknown(old, fresh).Should().BeFalse("同一個路徑換了檔案＝換版");
    }

    [Fact]
    public void TryReadStamp_不存在_資料夾_空字串回Null()
    {
        using var temp = new TempDirectory("cli-identity");
        CliBinaryIdentityRules.TryReadStamp(temp.Combine("沒有.exe")).Should().BeNull();
        CliBinaryIdentityRules.TryReadStamp(temp.CreateDirectory("claude.exe")).Should().BeNull("資料夾不是執行檔");
        CliBinaryIdentityRules.TryReadStamp(string.Empty).Should().BeNull();
        CliBinaryIdentityRules.TryReadStamp(null).Should().BeNull();
    }

    [Fact]
    public void TryRead_版本一律轉成xyz_讀不到_版本不明_還沒解析原生檔都回Null()
    {
        using var temp = new TempDirectory("cli-identity");
        var exe = temp.WriteFile(@"bin\claude.exe", "x");
        var installation = new CliInstallation(AgentProvider.Claude, new AgentLauncher(exe, Array.Empty<string>()), CliInstallKind.Native, exe) { NativeExecutable = exe };

        var identity = CliBinaryIdentityRules.TryRead(installation, "2.1.296.0")!;
        identity.Version.Should().Be("2.1.296", "FileVersionInfo 的四段版本轉成 x.y.z");
        identity.NativeExecutable.Should().Be(exe);
        identity.Kind.Should().Be(CliInstallKind.Native);
        identity.Provider.Should().Be(AgentProvider.Claude);
        identity.Size.Should().Be(1);
        CliBinaryIdentityRules.TryRead(installation, "2.1.296 (Claude Code)")!.Should().Be(identity, "版本來源不同、同一份檔案就是同一個身分");

        CliBinaryIdentityRules.TryRead(installation, "看不懂").Should().BeNull("版本不明不構成身分");
        CliBinaryIdentityRules.TryRead(installation with { NativeExecutable = null }, "2.1.296").Should().BeNull("還沒解析出原生執行檔");
        CliBinaryIdentityRules.TryRead(installation with { NativeExecutable = temp.Combine("沒有.exe") }, "2.1.296").Should().BeNull();
        CliBinaryIdentityRules.Create(AgentProvider.Codex, CliInstallKind.Npm, exe, null, CliBinaryIdentityRules.TryReadStamp(exe)!.Value).Should().BeNull();
    }

    [Fact]
    public void ReadClaudeFileVersion_讀檔案版本資訊轉成xyz_沒有版本資源回Null()
    {
        using var temp = new TempDirectory("cli-identity");
        var exe = temp.Combine("claude.exe");
        File.Copy(VersionedFile, exe);
        var expected = CliVersionRules.ParseVersion(FileVersionInfo.GetVersionInfo(VersionedFile).FileVersion);
        expected.Should().NotBeNull();

        CliBinaryIdentityRules.ReadClaudeFileVersion(exe).Should().Be(expected);
        CliBinaryIdentityRules.ReadClaudeFileVersion(temp.WriteFile("codex.exe", "沒有版本資源（codex.exe 的每一份都是空的）")).Should().BeNull();
        CliBinaryIdentityRules.ReadClaudeFileVersion(temp.Combine("沒有.exe")).Should().BeNull();
        CliBinaryIdentityRules.ReadClaudeFileVersion(null).Should().BeNull();
    }

    [Theory]
    [InlineData("2.1.296.0", 2, 1, 296, "2.1.296")]
    [InlineData("2.1.295.0", 2, 1, 295, "2.1.295")]
    [InlineData("", 2, 1, 296, "2.1.296")]
    [InlineData(null, 2, 1, 296, "2.1.296")]
    [InlineData("", 0, 0, 0, null)]
    [InlineData(null, 0, 0, 0, null)]
    public void ParseFileVersion_先解析字串_空字串改用數字(string? fileVersion, int major, int minor, int build, string? expected)
        => CliBinaryIdentityRules.ParseFileVersion(fileVersion, major, minor, build).Should().Be(expected);

    [Fact]
    public void ReadCodexPackageVersions_巢狀平台套件_混版_缺CodexPackageJson改讀平台套件_提升到前綴()
    {
        using var temp = new TempDirectory("cli-identity");
        var root = temp.Combine("npm", "node_modules", "@openai", "codex");
        var vendor = Path.Combine(root, "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc");
        void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        Write(Path.Combine(root, "package.json"), """{"name": "@openai/codex", "version": "0.162.0"}""");
        Write(Path.Combine(vendor, "codex-package.json"), """{"layoutVersion": 1, "version": "0.162.0", "entrypoint": "bin/codex.exe"}""");
        Write(Path.Combine(vendor, "bin", "codex.exe"), "x");
        Write(Path.Combine(root, "node_modules", "@openai", "codex-win32-x64", "package.json"), """{"name": "@openai/codex", "version": "0.160.1-win32-x64"}""");
        CliBinaryIdentityRules.ReadCodexPackageVersions(root).Should().Be(("0.162.0", "0.162.0"));

        // npm rollback 把舊版搬回套件位置：兩個來源不一致
        Write(Path.Combine(vendor, "codex-package.json"), """{"version": "0.160.1"}""");
        CliBinaryIdentityRules.ReadCodexPackageVersions(root).Should().Be(("0.162.0", "0.160.1"));

        File.Delete(Path.Combine(vendor, "codex-package.json"));
        CliBinaryIdentityRules.ReadCodexPackageVersions(root).Should().Be(("0.162.0", "0.160.1"), "沒有 codex-package.json 的配置改讀平台套件的 package.json、去掉 -win32-x64");

        File.Delete(Path.Combine(vendor, "bin", "codex.exe"));
        CliBinaryIdentityRules.ReadCodexPackageVersions(root).Should().Be(("0.162.0", (string?)null), "找不到實際執行的 vendor codex.exe");

        // 平台套件被提升到前綴的 node_modules\@openai（codex.js 的 require.resolve 也找得到）
        var hoisted = temp.Combine("npm", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc");
        Write(Path.Combine(hoisted, "codex-package.json"), """{"version": "0.162.0"}""");
        Write(Path.Combine(hoisted, "bin", "codex.exe"), "x");
        CliBinaryIdentityRules.ReadCodexPackageVersions(root).Should().Be(("0.162.0", "0.162.0"));

        File.WriteAllText(Path.Combine(root, "package.json"), """{"version": "0.163.0"}""", new UTF8Encoding(true));
        CliBinaryIdentityRules.ReadCodexPackageVersions(root).Package.Should().Be("0.163.0", "有 BOM 的 package.json 也讀得到");
        CliBinaryIdentityRules.ReadCodexPackageVersions(temp.Combine("沒有")).Should().Be(((string?)null, (string?)null));
    }

    [Fact]
    public void 讀身分與版本資訊時另一個執行緒改名不會失敗_只用FileShareRead開檔的對照組會失敗()
    {
        using var temp = new TempDirectory("cli-identity");
        var exe = temp.Combine("claude.exe");
        File.Copy(VersionedFile, exe);
        var backup = exe + ".old.1791575087343.48780";

        // 對照組：.NET 預設的 FileShare.Read（PowerShell 的 Get-FileHash、Python 的 open 也是）開著時，換檔的改名會 ERROR_SHARING_VIOLATION
        var control = RaceRenames(exe, backup, () =>
        {
            using var stream = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            stream.ReadByte();
        });
        control.Reads.Should().BeGreaterThan(0);
        control.SharingViolations.Should().BeGreaterThan(0, "對照組要能重現改名失敗，這個測試才有意義");

        var ours = RaceRenames(exe, backup, () =>
        {
            CliBinaryIdentityRules.TryReadStamp(exe);
            CliBinaryIdentityRules.ReadClaudeFileVersion(exe);
            CliBinaryIdentityRules.ReadSharedText(exe, 4 << 20);
        });
        ours.Reads.Should().BeGreaterThan(0);
        ours.Renames.Should().BeGreaterThan(0);
        ours.SharingViolations.Should().Be(0, "取身分只要 FILE_READ_ATTRIBUTES、讀檔都帶 FileShare.Delete；FileVersionInfo 也不擋改名");
    }

    /// <summary>一個執行緒反覆讀 exe，同時在這裡把它改名來回（Claude 換檔的動作）約 1 秒；回傳讀到幾次、改名幾次、改名遇到共用違規幾次</summary>
    private static (int Reads, int Renames, int SharingViolations) RaceRenames(string exe, string backup, Action read)
    {
        int reads = 0;
        int renames = 0;
        int violations = 0;
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    read();
                    Interlocked.Increment(ref reads);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 改名的空檔找不到檔案：照常繼續
                }
            }
        });
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(1) || (reads == 0 && watch.Elapsed < TimeSpan.FromSeconds(10)))
        {
            foreach (var (from, to) in new[] { (exe, backup), (backup, exe) })
            {
                while (true)
                {
                    try
                    {
                        File.Move(from, to);
                        renames++;
                        break;
                    }
                    catch (IOException ex) when (ex.HResult == SharingViolation)
                    {
                        violations++;
                    }
                }
            }
        }
        stop.Cancel();
        reader.Wait();
        return (reads, renames, violations);
    }
}
