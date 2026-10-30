using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;

namespace Myriagent.Tests.Protocol.Update;

/// <summary>
/// 共用的發佈規則（U2 設計 T2）：manifest 的產生（ReleaseManifestRules.Build）、zip 逐項驗證（ReleaseZipRules）、
/// 整包驗證與發佈政策（ReleasePackageRules）。夾具用暫時的 ECDsa 金鑰與暫存資料夾
/// </summary>
public sealed class ReleasePackageRulesTests : IDisposable
{
    private const string Release = "0.2.1523-ab12cd3";
    private const string Commit = "ab12cd34e5f60718293a4b5c6d7e8f9012345678";
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 20, 11, 12, 0, TimeSpan.FromHours(8));
    private static readonly ReleaseRuntime Runtime = new("Microsoft.WindowsDesktop.App", new ReleaseVersion(10, 0, 0));

    /// <summary>發佈資料夾的內容（路徑 → 內容）；Myriagent.exe 是 entry</summary>
    private static readonly IReadOnlyDictionary<string, string> Contents = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Myriagent.exe"] = "exe v2",
        ["Myriagent.dll"] = "dll v2",
        ["Web/app.js"] = "console.log('v2');",
    };

    private readonly TempDirectory temp = new("release-package");
    private readonly ECDsa key = ReleaseFixtures.NewKey();

    public void Dispose()
    {
        key.Dispose();
        temp.Dispose();
    }

    private ReleaseKey[] Trusted => [ReleaseFixtures.KeyOf(key)];

    private static ReleaseIdentity Identity(string release)
    {
        ReleaseIdentity.TryParseRelease(release, out var id).Should().BeTrue();
        return id!;
    }

    private static List<ReleaseFile> FilesOf(IReadOnlyDictionary<string, string> contents)
        => contents.Select(kv => ReleaseFixtures.File(kv.Key, kv.Value)).ToList();

    /// <summary>以 Build 產生 manifest：files 預設是 <see cref="Contents"/>，zipSha256 是 zip 的雜湊</summary>
    private static byte[] ManifestFor(byte[] zip, IReadOnlyDictionary<string, string>? contents = null, string entry = "Myriagent.exe",
        int protocolVersion = SyncProtocol.Version)
        => ReleaseManifestRules.Build(Identity(Release), Commit, CreatedAt, entry, Runtime, protocolVersion,
            ReleaseManifestRules.Sha256Hex(zip), FilesOf(contents ?? Contents));

    private static ReleaseManifest Parsed(byte[] manifestBytes)
    {
        ReleaseManifestRules.TryParse(manifestBytes, out var manifest, out var error).Should().BeTrue(error);
        return manifest!;
    }

    private string SignatureOf(byte[] manifestBytes) => ReleaseManifestRules.SerializeSignature(ReleaseManifestRules.Sign(manifestBytes, key));

    private static (string Name, string? Content)[] Good() => Contents.Select(kv => (kv.Key, (string?)kv.Value)).ToArray();

    /// <summary>照給的順序建 zip（同名的項目照樣放兩次）；Content 是 null 的是資料夾項目</summary>
    private static byte[] ZipOf(IEnumerable<(string Name, string? Content)> entries, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, level);
                if (content == null)
                    continue;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    [Fact]
    public void Build的輸出欄位順序照規格_TryParse讀得回()
    {
        var shaA = new string('a', 64);
        var shaB = new string('b', 64);
        var shaC = new string('c', 64);
        var zipSha = new string('e', 64);
        var files = new[]
        {
            new ReleaseFile("runtimes/win-x64/native/WebView2Loader.dll", 160, shaC),
            new ReleaseFile("Myriagent.exe", 151552, shaA),
            new ReleaseFile("Myriagent.dll", 2048, shaB),
        };

        var bytes = ReleaseManifestRules.Build(Identity(Release), Commit, CreatedAt, "Myriagent.exe", Runtime, 1, zipSha, files);

        var expected = $$"""
            {
              "schema": 1,
              "name": "Myriagent",
              "release": "0.2.1523-ab12cd3",
              "version": "0.2.1523",
              "build": "ab12cd3",
              "commit": "ab12cd34e5f60718293a4b5c6d7e8f9012345678",
              "createdAt": "2026-10-20T03:12:00Z",
              "entry": "Myriagent.exe",
              "minRuntime": {
                "name": "Microsoft.WindowsDesktop.App",
                "version": "10.0.0"
              },
              "protocolVersion": 1,
              "zip": "myriagent-0.2.1523-ab12cd3.zip",
              "zipSha256": "{{zipSha}}",
              "fileCount": 3,
              "totalBytes": 153760,
              "files": [
                {
                  "path": "Myriagent.dll",
                  "size": 2048,
                  "sha256": "{{shaB}}"
                },
                {
                  "path": "Myriagent.exe",
                  "size": 151552,
                  "sha256": "{{shaA}}"
                },
                {
                  "path": "runtimes/win-x64/native/WebView2Loader.dll",
                  "size": 160,
                  "sha256": "{{shaC}}"
                }
              ]
            }
            """.ReplaceLineEndings("\n");
        Encoding.UTF8.GetString(bytes).Should().Be(expected, "欄位順序、縮排、檔案依序數排序、createdAt 換成 UTC");
        bytes.Should().Equal(new UTF8Encoding(false).GetBytes(expected), "逐位元組：無 BOM、換行是 \\n、結尾沒有換行");

        var manifest = Parsed(bytes);
        manifest.Identity.Release.Should().Be(Release);
        manifest.Commit.Should().Be(Commit);
        manifest.Entry.Should().Be("Myriagent.exe");
        manifest.MinRuntime.Should().Be(Runtime);
        manifest.ProtocolVersion.Should().Be(1);
        manifest.Zip.Should().Be("myriagent-0.2.1523-ab12cd3.zip");
        manifest.ZipSha256.Should().Be(zipSha);
        manifest.TotalBytes.Should().Be(153760);
        manifest.Files.Should().Equal(files[2], files[1], files[0]);
    }

    [Fact]
    public void Build依序數排序檔案_拒絕不合法路徑與重複()
    {
        // 序數比較：大寫字母（0x41～0x5A）排在小寫（0x61～0x7A）前面；依文化排序的話會是 a、b、C
        var files = new[] { ReleaseFixtures.File("b.dll", "b"), ReleaseFixtures.File("C.dll", "c"), ReleaseFixtures.File("a.dll", "a"), ReleaseFixtures.File("Myriagent.exe", "exe") };
        var manifest = Parsed(ReleaseManifestRules.Build(Identity(Release), Commit, CreatedAt, "Myriagent.exe", Runtime, 1, new string('e', 64), files));
        manifest.Files.Select(f => f.Path).Should().Equal("C.dll", "Myriagent.exe", "a.dll", "b.dll");

        byte[] BuildWith(params ReleaseFile[] extra)
            => ReleaseManifestRules.Build(Identity(Release), Commit, CreatedAt, "Myriagent.exe", Runtime, 1, new string('e', 64), [.. files, .. extra]);

        FluentActions.Invoking(() => BuildWith(new ReleaseFile("../x.dll", 1, new string('d', 64)))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => BuildWith(new ReleaseFile("dir\\x.dll", 1, new string('d', 64)))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => BuildWith(ReleaseFixtures.File("b.dll", "b"))).Should().Throw<ArgumentException>("完全相同的路徑");
        FluentActions.Invoking(() => BuildWith(ReleaseFixtures.File("A.DLL", "a"))).Should().Throw<ArgumentException>("不分大小寫重複（TryParse 也不收）");
        FluentActions.Invoking(() => BuildWith(new ReleaseFile("big.dll", ReleaseManifestRules.MaxFileBytes + 1, new string('d', 64)))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => BuildWith(new ReleaseFile("bad" + (char)0xD800 + ".dll", 1, new string('d', 64)))).Should().Throw<ArgumentException>("不成對的代理字元不能默默變成 U+FFFD");
    }

    [Fact]
    public void Build的產出讀不回來就丟InvalidOperationException()
    {
        var files = FilesOf(Contents);
        var sha = new string('e', 64);
        FluentActions.Invoking(() => ReleaseManifestRules.Build(Identity(Release), new string('0', 40), CreatedAt, "Myriagent.exe", Runtime, 1, sha, files))
            .Should().Throw<InvalidOperationException>("commit 不以 build 開頭");
        FluentActions.Invoking(() => ReleaseManifestRules.Build(Identity(Release), Commit, CreatedAt, "missing.exe", Runtime, 1, sha, files))
            .Should().Throw<InvalidOperationException>("entry 不在清單裡");
        FluentActions.Invoking(() => ReleaseManifestRules.Build(new ReleaseIdentity(new ReleaseVersion(0, 2, 0), ReleaseIdentity.DevBuild), Commit, CreatedAt,
                "Myriagent.exe", Runtime, 1, sha, files))
            .Should().Throw<InvalidOperationException>("開發版沒有合法的 release 名稱");
        FluentActions.Invoking(() => ReleaseManifestRules.Build(Identity(Release), Commit, CreatedAt, "Myriagent.exe", Runtime, 1, "E" + sha[1..], files))
            .Should().Throw<InvalidOperationException>("zipSha256 必須是小寫");
        FluentActions.Invoking(() => ReleaseManifestRules.Build(Identity(Release), Commit, CreatedAt, "Myriagent.exe", Runtime, 1, sha, []))
            .Should().Throw<InvalidOperationException>("清單不能是空的");
    }

    [Fact]
    public void 檔名_與manifest的zip欄位一致_不合法的release丟例外()
    {
        ReleasePackageRules.FileNames(Release).Should().Be((
            "myriagent-0.2.1523-ab12cd3.zip", "myriagent-0.2.1523-ab12cd3.manifest.json", "myriagent-0.2.1523-ab12cd3.manifest.sig"));
        Parsed(ManifestFor(ZipOf(Good()))).Zip.Should().Be(ReleasePackageRules.FileNames(Release).Zip);
        FluentActions.Invoking(() => ReleasePackageRules.FileNames("../0.2.1-ab12cd3")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => ReleasePackageRules.FileNames("0.2.1-dev")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task 整包驗證通過_帶出manifest與簽章()
    {
        var zip = ZipOf(Good());
        var manifestBytes = ManifestFor(zip);
        using var stream = new MemoryStream(zip);

        var result = await ReleasePackageRules.VerifyAsync(manifestBytes, SignatureOf(manifestBytes), stream, Trusted, [], CancellationToken.None);

        result.Ok.Should().BeTrue(result.Error);
        result.Step.Should().BeNull();
        result.Error.Should().BeNull();
        result.Manifest!.Identity.Release.Should().Be(Release);
        result.Signature!.Kid.Should().Be(Trusted[0].Kid);
        stream.CanRead.Should().BeTrue("呼叫端的串流不被關閉");
    }

    [Fact]
    public async Task 簽章錯在解析前就拒絕()
    {
        var notJson = Encoding.UTF8.GetBytes("這不是 JSON");
        var goodManifest = ManifestFor(ZipOf(Good()));

        // manifest 不是 JSON、簽章是另一份 manifest 的：步驟是 signature，不是 manifest（沒有解析）
        var wrong = ReleasePackageRules.VerifyManifest(notJson, SignatureOf(goodManifest), Trusted, []);
        wrong.Step.Should().Be(ReleasePackageSteps.Signature);
        wrong.Manifest.Should().BeNull();

        // 簽章檔格式不對、沒有簽章檔、不認得的金鑰、信任清單是空的（O3 之前的正式清單）：都是 signature
        ReleasePackageRules.VerifyManifest(notJson, "nope", Trusted, []).Step.Should().Be(ReleasePackageSteps.Signature);
        ReleasePackageRules.VerifyManifest(goodManifest, null, Trusted, []).Step.Should().Be(ReleasePackageSteps.Signature);
        ReleasePackageRules.VerifyManifest(goodManifest, SignatureOf(goodManifest), [], []).Step.Should().Be(ReleasePackageSteps.Signature);
        using var other = ReleaseFixtures.NewKey();
        var unknown = ReleasePackageRules.VerifyManifest(goodManifest, SignatureOf(goodManifest), [ReleaseFixtures.KeyOf(other)], []);
        unknown.Step.Should().Be(ReleasePackageSteps.Signature);
        unknown.Error.Should().Contain("不認得");
        unknown.Signature!.Kid.Should().Be(Trusted[0].Kid, "簽章檔解析得出來時照樣帶出 kid（收件的服務記錄寫它）");

        // 對照組：同一份不是 JSON 的內容由信任的金鑰簽，才走到 manifest 這一步
        ReleasePackageRules.VerifyManifest(notJson, SignatureOf(notJson), Trusted, []).Step.Should().Be(ReleasePackageSteps.Manifest);

        // 整包驗證：簽章不對時連 zip 都不讀
        var package = await ReleasePackageRules.VerifyAsync(notJson, SignatureOf(goodManifest), new ProbeStream([], unreadable: true), Trusted, [], CancellationToken.None);
        package.Step.Should().Be(ReleasePackageSteps.Signature);
    }

    [Fact]
    public void 簽章對原文位元組_內容相同但多一個換行就不符()
    {
        var manifestBytes = ManifestFor(ZipOf(Good()));
        byte[] withNewline = [.. manifestBytes, (byte)'\n'];
        ReleaseManifestRules.TryParse(withNewline, out _, out _).Should().BeTrue("前提：結尾多一個換行 TryParse 照收");

        ReleasePackageRules.VerifyManifest(withNewline, SignatureOf(manifestBytes), Trusted, []).Step
            .Should().Be(ReleasePackageSteps.Signature, "簽章對原文位元組，不對解析後的內容：任何一段都不能重新序列化");
        ReleasePackageRules.VerifyManifest(withNewline, SignatureOf(withNewline), Trusted, []).Ok.Should().BeTrue();
    }

    [Fact]
    public void manifest格式不對_步驟是manifest()
    {
        // 簽章對、內容不合格（反斜線路徑）：TryParse 擋下。Build 產生不出這種 manifest，改用測試夾具直接寫
        var files = new List<ReleaseFile>(FilesOf(Contents)) { ReleaseFixtures.File("Web\\extra.js", "x") };
        var manifestBytes = ReleaseFixtures.ManifestBytes(Release, files);

        var result = ReleasePackageRules.VerifyManifest(manifestBytes, SignatureOf(manifestBytes), Trusted, []);

        result.Step.Should().Be(ReleasePackageSteps.Manifest);
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Signature.Should().NotBeNull("簽章已經驗過");
    }

    [Fact]
    public void 撤銷的kid不分大小寫()
    {
        // 伺服器（T4）與發佈工具（T10）一律傳空的撤銷清單（D1）。參數留著是因為 U1 的 VerifySignature 本來就收它
        // （桌面 updater-state.json 的 RevokedKids）：這裡確認 VerifyManifest 把它原樣交給 VerifySignature，比對不分大小寫
        var manifestBytes = ManifestFor(ZipOf(Good()));
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var candidate = ReleaseFixtures.NewKey();
            var signature = ReleaseManifestRules.Sign(manifestBytes, candidate);
            // kid 全是數字時大小寫沒有差別（約萬分之五），換一把再試
            if (!signature.Kid.Any(c => c is >= 'a' and <= 'f'))
                continue;
            var trusted = new[] { ReleaseFixtures.KeyOf(candidate) };
            var json = ReleaseManifestRules.SerializeSignature(signature);
            ReleasePackageRules.VerifyManifest(manifestBytes, json, trusted, []).Ok.Should().BeTrue("前提：沒撤銷時通過");

            var revoked = ReleasePackageRules.VerifyManifest(manifestBytes, json, trusted, [signature.Kid.ToUpperInvariant()]);

            revoked.Step.Should().Be(ReleasePackageSteps.Signature);
            revoked.Error.Should().Contain("撤銷");
            return;
        }
        throw new InvalidOperationException("100 把金鑰的 kid 都沒有字母，不可能發生");
    }

    [Fact]
    public void entry必須是Myriagent_exe_清單不得有保留檔名()
    {
        ReleasePackageRules.ReservedRootFiles.Should().Equal(AppUpdateManager.ManifestFileName, AppUpdateManager.SignatureFileName);
        var zip = ZipOf(Good());

        // entry 是清單裡的另一個檔：TryParse 收，政策不收
        var otherEntry = ManifestFor(zip, entry: "Myriagent.dll");
        var entryResult = ReleasePackageRules.VerifyManifest(otherEntry, SignatureOf(otherEntry), Trusted, []);
        entryResult.Step.Should().Be(ReleasePackageSteps.Policy);
        entryResult.Error.Should().Contain(ReleasePackageRules.EntryExecutable);
        entryResult.Manifest.Should().NotBeNull("政策不符時解析結果照樣帶出");

        foreach (var reserved in new[] { "release.manifest.json", "Release.Manifest.SIG" })
        {
            var withReserved = ManifestFor(zip, new Dictionary<string, string>(Contents) { [reserved] = "{}" });
            var result = ReleasePackageRules.VerifyManifest(withReserved, SignatureOf(withReserved), Trusted, []);
            result.Step.Should().Be(ReleasePackageSteps.Policy, reserved);
            result.Error.Should().Contain(reserved);
        }

        // 子資料夾裡同名的檔不會蓋到桌面寫的檔
        ReleasePackageRules.CheckPublishPolicy(Parsed(ManifestFor(zip, new Dictionary<string, string>(Contents) { ["docs/release.manifest.json"] = "{}" })))
            .Should().BeNull();
    }

    [Fact]
    public void 協定版本不在支援範圍()
    {
        // SyncProtocol.Version 是 1 時，protocolVersion 2 就是這種情況（伺服器回滾、或發佈的 app 比這版伺服器新）
        var zip = ZipOf(Good());
        var newer = ManifestFor(zip, protocolVersion: SyncProtocol.Version + 1);

        var result = ReleasePackageRules.VerifyManifest(newer, SignatureOf(newer), Trusted, []);

        result.Step.Should().Be(ReleasePackageSteps.Protocol);
        result.Error.Should().Contain($"{SyncProtocol.Version + 1}");
        var current = ManifestFor(zip);
        ReleasePackageRules.VerifyManifest(current, SignatureOf(current), Trusted, []).Ok.Should().BeTrue();
    }

    [Fact]
    public void 桌面的入口不看協定版本_其他步驟同VerifyManifest()
    {
        // PROTO-1：桌面驗伺服器給的新版不能用 VerifyManifest——它最後一步看的是「執行這段程式的這一版」支援的協定，新版的協定比自己新是正常的
        var zip = ZipOf(Good());
        var newer = ManifestFor(zip, protocolVersion: SyncProtocol.Version + 1);
        var offer = ReleasePackageRules.VerifyOffer(newer, SignatureOf(newer), Trusted, []);
        offer.Ok.Should().BeTrue(offer.Error);
        offer.Manifest!.ProtocolVersion.Should().Be(SyncProtocol.Version + 1);
        offer.Signature.Should().NotBeNull();

        // 簽章、格式、政策：兩個入口擋在同一步、同一個原因
        using var other = ReleaseFixtures.NewKey();
        var good = ManifestFor(zip);
        var otherEntry = ManifestFor(zip, entry: "Myriagent.dll");
        var notJson = Encoding.UTF8.GetBytes("{");
        foreach (var (bytes, signature, step) in new (byte[], string?, string)[]
                 {
                     (good, null, ReleasePackageSteps.Signature),
                     (good, ReleaseManifestRules.SerializeSignature(ReleaseManifestRules.Sign(good, other)), ReleasePackageSteps.Signature),
                     (notJson, SignatureOf(notJson), ReleasePackageSteps.Manifest),
                     (otherEntry, SignatureOf(otherEntry), ReleasePackageSteps.Policy),
                 })
        {
            var desktop = ReleasePackageRules.VerifyOffer(bytes, signature, Trusted, []);
            var server = ReleasePackageRules.VerifyManifest(bytes, signature, Trusted, []);
            desktop.Step.Should().Be(step);
            (desktop.Step, desktop.Error, desktop.Signature, desktop.Manifest is null).Should().Be((server.Step, server.Error, server.Signature, server.Manifest is null));
        }
    }

    [Fact]
    public void 檔案與資料夾同名_伺服器收件與桌面都在manifest擋下()
    {
        // PROTO-2：伺服器原本會收（簽過名、zip 也對得上），桌面解壓時才在 Directory.CreateDirectory 丟 IOException、當成暫時失敗一直重試
        var files = FilesOf(Contents).Append(ReleaseFixtures.File("Myriagent.dll/readme.txt", "x")).ToList();
        var manifestBytes = ReleaseFixtures.ManifestBytes(Release, files);

        var server = ReleasePackageRules.VerifyManifest(manifestBytes, SignatureOf(manifestBytes), Trusted, []);
        var desktop = ReleasePackageRules.VerifyOffer(manifestBytes, SignatureOf(manifestBytes), Trusted, []);

        server.Step.Should().Be(ReleasePackageSteps.Manifest);
        server.Error.Should().Contain("檔案與資料夾同名");
        desktop.Step.Should().Be(ReleasePackageSteps.Manifest);
        desktop.Error.Should().Be(server.Error);
    }

    [Fact]
    public void 桌面的原始碼不呼叫伺服器的整包驗證()
    {
        // PROTO-1 的守門：VerifyManifest、VerifyAsync 最後都看 SyncProtocol.IsSupported（伺服器收件與發佈工具的規則），桌面只用 VerifyOffer
        var root = Path.Combine(MyriagentHubExe.RepoRoot(), "src", "Myriagent");
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f) is var text
                        && (text.Contains("ReleasePackageRules.VerifyManifest(", StringComparison.Ordinal)
                            || text.Contains("ReleasePackageRules.VerifyAsync(", StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        offenders.Should().BeEmpty();
        File.ReadAllText(Path.Combine(root, "Update", "AppUpdateManager.cs")).Should().Contain("ReleasePackageRules.VerifyOffer(", "前提：桌面的檢查走這個入口");
    }

    [Fact]
    public async Task zip雜湊不符()
    {
        var zip = ZipOf(Good());
        var manifestBytes = ManifestFor(zip);
        var tampered = zip.ToArray();
        tampered[tampered.Length / 2] ^= 0xFF;
        var stream = new ProbeStream(tampered, singlePass: true);

        var result = await ReleasePackageRules.VerifyAsync(manifestBytes, SignatureOf(manifestBytes), stream, Trusted, [], CancellationToken.None);

        result.Step.Should().Be(ReleasePackageSteps.ZipHash);
        result.Manifest.Should().NotBeNull();
        stream.TotalRead.Should().Be(tampered.Length, "只讀了算雜湊的那一輪");
        stream.Invoking(s => s.ReadByte()).Should().Throw<InvalidOperationException>("前提：這個串流讀完一輪之後再讀就丟例外；上面沒丟，代表雜湊不符時沒有開 zip");
    }

    [Theory]
    [InlineData("多一項")]
    [InlineData("少一項")]
    [InlineData("大小不符")]
    [InlineData("內容不符")]
    [InlineData("重複項目")]
    public async Task zip多一項_少一項_大小不符_內容不符_重複項目(string kind)
    {
        var entries = kind switch
        {
            "多一項" => [.. Good(), ("extra.dll", "extra")],
            "少一項" => Good().Where(e => e.Name != "Web/app.js").ToArray(),
            "大小不符" => Good().Select(e => e.Name == "Myriagent.dll" ? (e.Name, "dll v2 longer") : e).ToArray(),
            "內容不符" => Good().Select(e => e.Name == "Myriagent.dll" ? (e.Name, "dll v3") : e).ToArray(),
            "重複項目" => [.. Good(), ("Myriagent.exe", Contents["Myriagent.exe"])],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        // manifest 照 Contents 寫、zipSha256 照竄改後的 zip 算（發佈本身就壞了）：只有逐項比對擋得下
        var zip = ZipOf(entries);
        var manifestBytes = ManifestFor(zip);
        using var stream = new MemoryStream(zip);

        var result = await ReleasePackageRules.VerifyAsync(manifestBytes, SignatureOf(manifestBytes), stream, Trusted, [], CancellationToken.None);

        result.Step.Should().Be(ReleasePackageSteps.ZipEntries, result.Error);
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task 格式壞掉的zip_算zip_entries()
    {
        var zip = Encoding.UTF8.GetBytes("這不是 zip 檔");
        var manifestBytes = ManifestFor(zip);
        using var stream = new MemoryStream(zip);

        var result = await ReleasePackageRules.VerifyAsync(manifestBytes, SignatureOf(manifestBytes), stream, Trusted, [], CancellationToken.None);

        result.Step.Should().Be(ReleasePackageSteps.ZipEntries);
        result.Error.Should().Contain("zip 格式不對");
    }

    [Fact]
    public void 反斜線與資料夾項目的處理同桌面()
    {
        var manifest = Parsed(ManifestFor(new byte[1]));
        var zip = ZipOf([("Web/", null), ("Myriagent.exe", Contents["Myriagent.exe"]), ("Myriagent.dll", Contents["Myriagent.dll"]),
            ("Web\\app.js", Contents["Web/app.js"])]);
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        archive.Entries.Select(e => e.FullName).Should().Contain(["Web/", "Web\\app.js"], "前提：zip 裡真的是資料夾項目與反斜線");

        ReleaseZipRules.VerifyEntries(archive, manifest, CancellationToken.None).Should().BeNull("dir\\a 視為 dir/a；dir/ 略過");
    }

    [Fact]
    public async Task 項目實際長度超過清單即中止()
    {
        // Myriagent.dll 實際 4 MiB，中央目錄與本機標頭卻宣告成清單的大小：先比宣告長度擋不下，只能靠讀取時計數。
        // 用不壓縮（Stored）的項目：Deflate 的項目 .NET 本身只解到宣告的長度，Stored 的項目會一直讀到實際的長度
        var big = new string('x', 4 * 1024 * 1024);
        var honest = ZipOf(Good().Select(e => e.Name == "Myriagent.dll" ? (e.Name, big) : e), CompressionLevel.NoCompression);
        var declared = (uint)Encoding.UTF8.GetByteCount(Contents["Myriagent.dll"]);
        var zip = WithDeclaredLength(honest, "Myriagent.dll", declared);
        using (var check = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            check.GetEntry("Myriagent.dll")!.Length.Should().Be(declared, "前提：宣告的長度改成清單的大小");
        var manifestBytes = ManifestFor(zip);
        var stream = new ProbeStream(zip);

        var result = await ReleasePackageRules.VerifyAsync(manifestBytes, SignatureOf(manifestBytes), stream, Trusted, [], CancellationToken.None);

        result.Step.Should().Be(ReleasePackageSteps.ZipEntries, result.Error);
        // 第一輪算整檔雜湊讀了 zip.Length；之後只讀中央目錄與標頭（實測約 4 KiB，留 64 KiB 餘裕），加上最多「清單大小＋一個緩衝區」的內容，遠小於 4 MiB
        (stream.TotalRead - zip.Length).Should().BeLessThan(declared + ReleaseZipRules.BufferBytes + 64 * 1024);
    }

    [Fact]
    public async Task 不可定位的串流丟ArgumentException()
    {
        var zip = ZipOf(Good());
        var manifestBytes = ManifestFor(zip);

        await FluentActions.Awaiting(() => ReleasePackageRules.VerifyZipAsync(Parsed(manifestBytes), new ProbeStream(zip, seekable: false), CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => ReleasePackageRules.VerifyAsync(manifestBytes, SignatureOf(manifestBytes), new ProbeStream(zip, seekable: false), Trusted, [],
                CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task 已取消就丟OperationCanceledException()
    {
        var zip = ZipOf(Good());
        var manifestBytes = ManifestFor(zip);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await FluentActions.Awaiting(() => ReleasePackageRules.VerifyAsync(manifestBytes, SignatureOf(manifestBytes), new MemoryStream(zip), Trusted, [], canceled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void 桌面也先比宣告的長度_比清單大就不解壓()
    {
        // 伺服器的 ReleaseZipRules 先比項目宣告的長度；桌面原本直接解壓、寫到超過清單大小才停，磁碟上已經多了一個半截的檔
        var big = new string('x', 256 * 1024);
        var zip = ZipOf(Good().Select(e => e.Name == "Myriagent.dll" ? (e.Name, big) : e));
        using (var check = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            check.GetEntry("Myriagent.dll")!.Length.Should().Be(big.Length, "前提：宣告的長度就是實際的長度，比清單大");
        var manifest = Parsed(ManifestFor(new byte[1]));
        var zipPath = temp.Combine("declared.zip");
        File.WriteAllBytes(zipPath, zip);
        var destination = temp.CreateDirectory("declared");

        AppUpdateManager.ExtractVerified(zipPath, destination, manifest).Should().Be("檔案比清單大：Myriagent.dll");

        File.Exists(Path.Combine(destination, "Myriagent.dll")).Should().BeFalse("宣告的長度就超過：不建檔、不解壓");
        ServerVerdict(zip, manifest).Should().Be("檔案比清單大：Myriagent.dll");
    }

    public static TheoryData<string> ParityCases =>
    [
        "正確", "反斜線路徑", "資料夾項目", "多一項", "少一項", "內容比清單長", "內容比清單短", "同長度不同內容", "重複項目", "反斜線與斜線重複",
        "大小寫不同", "zip-slip", "ADS", "格式壞掉",
    ];

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void 伺服器與桌面的zip規則判斷一致(string name)
    {
        var (zip, accept) = ParityCase(name);
        var manifest = Parsed(ManifestFor(new byte[1]));   // 逐項驗證不看 zipSha256

        var server = ServerVerdict(zip, manifest);
        var desktop = DesktopVerdict(zip, manifest);

        (server == null).Should().Be(accept, $"伺服器：{server}");
        (desktop == null).Should().Be(accept, $"桌面：{desktop}");
    }

    private static (byte[] Zip, bool Accept) ParityCase(string name)
    {
        (string Name, string? Content)[] Swap(string path, (string Name, string? Content) replacement)
            => Good().Select(e => e.Name == path ? replacement : e).ToArray();

        return name switch
        {
            "正確" => (ZipOf(Good()), true),
            "反斜線路徑" => (ZipOf(Swap("Web/app.js", ("Web\\app.js", Contents["Web/app.js"]))), true),
            "資料夾項目" => (ZipOf([("Web/", null), .. Good()]), true),
            "多一項" => (ZipOf([.. Good(), ("extra.dll", "extra")]), false),
            "少一項" => (ZipOf(Good().Where(e => e.Name != "Web/app.js")), false),
            "內容比清單長" => (ZipOf(Swap("Myriagent.dll", ("Myriagent.dll", "dll v2 longer"))), false),
            "內容比清單短" => (ZipOf(Swap("Myriagent.dll", ("Myriagent.dll", "dll"))), false),
            "同長度不同內容" => (ZipOf(Swap("Myriagent.dll", ("Myriagent.dll", "dll v3"))), false),
            "重複項目" => (ZipOf([.. Good(), ("Myriagent.exe", Contents["Myriagent.exe"])]), false),
            "反斜線與斜線重複" => (ZipOf([.. Good(), ("Web\\app.js", Contents["Web/app.js"])]), false),
            "大小寫不同" => (ZipOf(Swap("Myriagent.exe", ("myriagent.exe", Contents["Myriagent.exe"]))), false),
            "zip-slip" => (ZipOf([.. Good(), ("../evil.txt", "evil")]), false),
            "ADS" => (ZipOf([.. Good(), ("Myriagent.exe:evil", "evil")]), false),
            "格式壞掉" => (Encoding.UTF8.GetBytes("這不是 zip 檔"), false),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };
    }

    private static string? ServerVerdict(byte[] zip, ReleaseManifest manifest)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            return ReleaseZipRules.VerifyEntries(archive, manifest, CancellationToken.None);
        }
        catch (InvalidDataException ex)
        {
            return $"zip 格式不對：{ex.Message}";   // 同 ReleasePackageRules.VerifyZipAsync
        }
    }

    private string? DesktopVerdict(byte[] zip, ReleaseManifest manifest)
    {
        var id = Guid.NewGuid().ToString("N");
        var zipPath = temp.Combine($"{id}.zip");
        File.WriteAllBytes(zipPath, zip);
        var destination = temp.CreateDirectory(id);
        try
        {
            return AppUpdateManager.ExtractVerified(zipPath, destination, manifest);
        }
        catch (InvalidDataException ex)
        {
            return $"zip 格式不對：{ex.Message}";   // 同 AppUpdateManager.VerifyAndExtractAsync
        }
    }

    /// <summary>把 zip 裡某個項目宣告的解壓後長度（中央目錄與本機標頭）改成 declared，內容不動（模擬宣告長度造假的項目）</summary>
    private static byte[] WithDeclaredLength(byte[] zip, string entryName, uint declared)
    {
        var bytes = zip.ToArray();
        var end = bytes.Length - 22;   // 沒有註解的 zip：中央目錄結尾記錄正好是最後 22 位元組
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end)).Should().Be(0x06054b50u, "前提：zip 沒有註解");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end + 10));
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16));
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)).Should().Be(0x02014b50u, "中央目錄的項目標頭");
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 32));
            var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 42));
            if (Encoding.UTF8.GetString(bytes, offset + 46, nameLength) == entryName)
            {
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(local)).Should().Be(0x04034b50u, "本機標頭");
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24), declared);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(local + 22), declared);
                return bytes;
            }
            offset += 46 + nameLength + extraLength + commentLength;
        }
        throw new InvalidOperationException($"zip 裡沒有 {entryName}");
    }

    /// <summary>
    /// 測試用的唯讀串流（包住 MemoryStream），記下總共讀了多少位元組。seekable 是 false 時 CanSeek 回 false；
    /// singlePass 時讀到結尾之後再讀就丟例外（證明沒有讀第二輪）；unreadable 時任何讀取都丟例外（證明完全沒讀）
    /// </summary>
    private sealed class ProbeStream(byte[] data, bool seekable = true, bool singlePass = false, bool unreadable = false) : Stream
    {
        private readonly MemoryStream inner = new(data, writable: false);
        private bool finished;

        public long TotalRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => seekable;

        public override bool CanWrite => false;

        public override long Length => seekable ? inner.Length : throw new NotSupportedException();

        public override long Position
        {
            get => seekable ? inner.Position : throw new NotSupportedException();
            set
            {
                if (!seekable)
                    throw new NotSupportedException();
                inner.Position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (unreadable)
                throw new InvalidOperationException("這個串流不該被讀");
            if (singlePass && finished)
                throw new InvalidOperationException("讀了第二輪");
            var read = inner.Read(buffer);
            if (read == 0 && buffer.Length > 0)
                finished = true;
            TotalRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) => seekable ? inner.Seek(offset, origin) : throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
