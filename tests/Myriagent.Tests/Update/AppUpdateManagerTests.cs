using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Update;

public sealed class AppUpdateManagerTests : IDisposable
{
    private readonly TempDirectory temp = new("app-update");
    private readonly ECDsa key = ReleaseFixtures.NewKey();
    private readonly InstallLayout layout;
    private readonly string source;
    private static readonly Dictionary<string, string> Files = new()
    {
        ["Myriagent.exe"] = "exe v2",
        ["Myriagent.dll"] = "dll v2",
        ["Web/app.js"] = "console.log('v2');",
    };

    public AppUpdateManagerTests()
    {
        layout = new InstallLayout(temp.Combine("Programs", "Myriagent"));
        source = temp.CreateDirectory("source");
        Directory.CreateDirectory(layout.VersionsDirectory);
    }

    public void Dispose()
    {
        key.Dispose();
        temp.Dispose();
    }

    private static ReleaseIdentity Id(string release)
    {
        ReleaseIdentity.TryParseRelease(release, out var id).Should().BeTrue();
        return id!;
    }

    private sealed class FakeRuntime : IRuntimeProbe
    {
        public bool Has { get; set; } = true;

        public string? Removed { get; set; }

        public bool HasAtLeast(string name, ReleaseVersion version) => Has;

        public string? RemovedRuntime => Removed;
    }

    /// <summary>包住 FileReleaseSource，記下每次下載開始時目的檔已有的長度</summary>
    private sealed class RecordingSource : IAppReleaseSource
    {
        private readonly FileReleaseSource inner;

        public RecordingSource(string directory) => inner = new FileReleaseSource(directory);

        public List<long> ResumeOffsets { get; } = new();

        /// <summary>下一次下載只留下這麼多位元組就丟 IOException（模擬下載到一半斷線或睡眠）；用過一次就清掉</summary>
        public long? FailAfterBytes { get; set; }

        /// <summary>接下來幾次下載完成後改掉一個位元組（模擬傳輸中損壞）</summary>
        public int CorruptDownloads { get; set; }

        /// <summary>內層 GetLatestAsync 回來之後呼叫（模擬取得最新版之後、判斷壞版之前發生的事）</summary>
        public Action? AfterGetLatest { get; set; }

        /// <summary>DownloadZipAsync 一開頭、記錄位移之前呼叫（模擬下載期間發生的事）</summary>
        public Action? BeforeDownload { get; set; }

        public string Name => inner.Name;

        public async Task<ReleaseOffer?> GetLatestAsync(CancellationToken ct)
        {
            var offer = await inner.GetLatestAsync(ct);
            AfterGetLatest?.Invoke();
            return offer;
        }

        public async Task DownloadZipAsync(ReleaseOffer offer, string zipName, string destination, IProgress<long>? progress, CancellationToken ct)
        {
            BeforeDownload?.Invoke();
            ResumeOffsets.Add(File.Exists(destination) ? new FileInfo(destination).Length : 0);
            await inner.DownloadZipAsync(offer, zipName, destination, progress, ct);
            if (FailAfterBytes is { } kept)
            {
                FailAfterBytes = null;
                using (var partial = new FileStream(destination, FileMode.Open, FileAccess.Write))
                    partial.SetLength(kept);
                throw new IOException("連線中斷（測試）");
            }
            if (CorruptDownloads > 0)
            {
                CorruptDownloads--;
                var bytes = File.ReadAllBytes(destination);
                bytes[bytes.Length / 2] ^= 0xFF;
                File.WriteAllBytes(destination, bytes);
            }
        }
    }

    /// <summary>
    /// 高頻回報進度的來源（模擬 U3 的 HTTP 來源每讀一小塊就回報）：內層下載完之後，在同一個執行緒上同步回報 reports 次，
    /// 從 length/reports 遞增到 zip 的長度
    /// </summary>
    private sealed class ChattySource : IAppReleaseSource
    {
        private readonly FileReleaseSource inner;
        private readonly int reports;

        public ChattySource(string directory, int reports)
        {
            inner = new FileReleaseSource(directory);
            this.reports = reports;
        }

        public string Name => inner.Name;

        public Task<ReleaseOffer?> GetLatestAsync(CancellationToken ct) => inner.GetLatestAsync(ct);

        public async Task DownloadZipAsync(ReleaseOffer offer, string zipName, string destination, IProgress<long>? progress, CancellationToken ct)
        {
            await inner.DownloadZipAsync(offer, zipName, destination, null, ct);
            var length = new FileInfo(destination).Length;
            for (int i = 1; i <= reports; i++)
                progress?.Report(length * i / reports);
        }
    }

    /// <summary>取得最新版時丟指定例外的來源（U3 的 HubReleaseSource 連不上、伺服器暫停供應）；Throw 是 null 時照常</summary>
    private sealed class FlakySource : IAppReleaseSource
    {
        private readonly FileReleaseSource inner;

        public FlakySource(string directory) => inner = new FileReleaseSource(directory);

        public Exception? Throw { get; set; }

        public string Name => inner.Name;

        public Task<ReleaseOffer?> GetLatestAsync(CancellationToken ct) => Throw is { } ex ? Task.FromException<ReleaseOffer?>(ex) : inner.GetLatestAsync(ct);

        public Task DownloadZipAsync(ReleaseOffer offer, string zipName, string destination, IProgress<long>? progress, CancellationToken ct)
            => inner.DownloadZipAsync(offer, zipName, destination, progress, ct);
    }

    /// <param name="enablementNow">每次評估都重新讀的啟用條件（測試中途改變用）；給了就不看 enablement</param>
    private AppUpdateManager Manager(IAppReleaseSource? src = null, FakeRuntime? runtime = null, ReleaseKey? trusted = null,
        UpdateBlockReason enablement = UpdateBlockReason.None, Func<string, long?>? freeSpace = null, string current = "0.2.1-aaaaaa1",
        Func<UpdateBlockReason>? enablementNow = null)
        => new(layout, Id(current), src ?? new FileReleaseSource(source), enablementNow ?? (() => enablement),
            new[] { trusted ?? ReleaseFixtures.KeyOf(key) }, runtime ?? new FakeRuntime(), freeSpace ?? (_ => long.MaxValue));

    /// <summary>把來源的 zip 換成 zipBytes，manifest 的 zipSha256 跟著重算、重新簽章（zip 雜湊相符，但內容本身壞掉）</summary>
    private void ReplaceZip(string release, byte[] zipBytes)
    {
        File.WriteAllBytes(Path.Combine(source, $"myriagent-{release}.zip"), zipBytes);
        var manifest = ReleaseFixtures.ManifestBytes(release, Files.Select(kv => ReleaseFixtures.File(kv.Key, kv.Value)).ToList(),
            ReleaseManifestRules.Sha256Hex(zipBytes));
        File.WriteAllBytes(Path.Combine(source, $"myriagent-{release}.manifest.json"), manifest);
        File.WriteAllText(Path.Combine(source, $"myriagent-{release}.manifest.sig"),
            ReleaseManifestRules.SerializeSignature(ReleaseManifestRules.Sign(manifest, key)));
    }

    /// <summary>每個檔都在、但 Myriagent.exe 出現兩次的 zip</summary>
    private static byte[] ZipWithDuplicateEntry()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in Files.Append(Files.First(kv => kv.Key == "Myriagent.exe")))
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    [Fact]
    public async Task 新版_下載驗證解壓後備妥()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        using var m = Manager();

        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Staged, m.Status.Detail);
        m.Status.StagedRelease.Should().Be("0.2.2-bbbbbb2");
        var dir = layout.VersionDirectory("0.2.2-bbbbbb2");
        File.ReadAllText(Path.Combine(dir, "Web", "app.js")).Should().Be("console.log('v2');");
        File.Exists(Path.Combine(dir, "release.manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "release.manifest.sig")).Should().BeTrue();
        Directory.Exists(layout.PartialVersionDirectory("0.2.2-bbbbbb2")).Should().BeFalse();
        Directory.EnumerateFiles(layout.Staging).Should().BeEmpty("用完就清");
        UpdaterStateStore.Load(layout).Staged.Should().Be("0.2.2-bbbbbb2");
    }

    /// <summary>
    /// 2026-10-27：這個程序的 .NET 已被移除（.NET 更新刪掉了舊的修補版），解壓要載入的組件找不到，每小時重下載一次、每次都失敗。
    /// 有新版時不下載、不退避重試，標成無法安裝並說明要重新啟動（協調器在閒置時重新啟動）
    /// </summary>
    [Fact]
    public async Task 這個程序的NET已被移除_有新版也不下載_說明要重新啟動_不退避重試()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        var recording = new RecordingSource(source);
        using var m = Manager(recording, new FakeRuntime { Removed = "10.0.13" });

        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Blocked);
        m.Status.AvailableRelease.Should().Be("0.2.2-bbbbbb2");
        m.Status.Detail.Should().Be(RuntimeRules.RemovedDetail("10.0.13"));
        recording.ResumeOffsets.Should().BeEmpty("不下載");
        m.ConsecutiveFailuresForTest.Should().Be(0, "不是暫時的失敗，不退避重試");
        m.RuntimeRemoved.Should().Be("10.0.13");
    }

    [Fact]
    public async Task 已備妥的版本_重新建立manager時直接是備妥_不再下載()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        using (var first = Manager())
            await first.CheckAsync();
        var src = new RecordingSource(source);

        using var m = Manager(src);
        m.Status.Phase.Should().Be(AppUpdatePhase.Staged);
        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Staged);
        src.ResumeOffsets.Should().BeEmpty("同一版已備妥就跳過");
    }

    [Fact]
    public async Task 簽章不是信任的金鑰_拒絕_同一版這次執行不再重試_重新啟動後才再試()
    {
        using var other = ReleaseFixtures.NewKey();
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, other);
        var src = new RecordingSource(source);
        using (var m = Manager(src))
        {
            await m.CheckAsync();

            m.Status.Phase.Should().Be(AppUpdatePhase.Failed);
            m.Status.Detail.Should().Contain("簽章");
            src.ResumeOffsets.Should().BeEmpty("簽章不符不下載");
            Directory.Exists(layout.VersionDirectory("0.2.2-bbbbbb2")).Should().BeFalse();

            // 伺服器換成正確的簽章：這次執行仍不重試同一版（ZipFile 的 Create 模式不覆寫，先刪舊 zip）
            File.Delete(Path.Combine(source, "myriagent-0.2.2-bbbbbb2.zip"));
            ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
            await m.CheckAsync();
            m.Status.Phase.Should().Be(AppUpdatePhase.Failed);
            m.Status.Detail.Should().Contain("簽章");
            src.ResumeOffsets.Should().BeEmpty();
        }
        UpdaterStateStore.Load(layout).Bad.Should().BeEmpty("簽章不符不記成壞版");

        using var restarted = Manager();
        await restarted.CheckAsync();
        restarted.Status.Phase.Should().Be(AppUpdatePhase.Staged, restarted.Status.Detail);
    }

    [Fact]
    public async Task 簽章檔超過大小上限_讀檔前就擋下_不下載()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        var sigPath = Path.Combine(source, "myriagent-0.2.2-bbbbbb2.manifest.sig");
        var signature = JObject.Parse(File.ReadAllText(sigPath));
        signature["pad"] = string.Empty;
        var baseLength = signature.ToString(Newtonsoft.Json.Formatting.None).Length;
        signature["pad"] = new string('x', ReleaseManifestRules.MaxSignatureBytes + 1 - baseLength);
        File.WriteAllText(sigPath, signature.ToString(Newtonsoft.Json.Formatting.None));
        new FileInfo(sigPath).Length.Should().Be(ReleaseManifestRules.MaxSignatureBytes + 1);
        var src = new RecordingSource(source);
        using var m = Manager(src);

        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Failed);
        m.Status.Detail.Should().Contain(".manifest.sig").And.NotContain("簽章不符", "來源讀檔前就擋下，不是讀進來才驗章失敗");
        src.ResumeOffsets.Should().BeEmpty();
    }

    [Fact]
    public async Task zip雜湊不符一次_重新下載成功就備妥()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        var src = new RecordingSource(source) { CorruptDownloads = 1 };
        using var m = Manager(src);

        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Staged, m.Status.Detail);
        src.ResumeOffsets.Should().Equal(0, 0);
        UpdaterStateStore.Load(layout).Bad.Should().BeEmpty();
    }

    [Theory]
    [InlineData("竄改內容")]
    [InlineData("多一個檔")]
    [InlineData("少一個檔")]
    [InlineData("zip-slip")]
    [InlineData("重複的項目")]
    [InlineData("格式壞掉的zip")]
    public async Task zip內容與manifest不符_拒絕而且清掉暫存(string kind)
    {
        var zip = new Dictionary<string, string>(Files);
        switch (kind)
        {
            case "竄改內容":
                zip["Myriagent.dll"] = "dll evil";
                break;
            case "多一個檔":
                zip["extra.dll"] = "extra";
                break;
            case "少一個檔":
                zip.Remove("Web/app.js");
                break;
            case "zip-slip":
                zip["../evil.txt"] = "evil";
                break;
        }
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key, zipContents: zip);
        // 這兩種的 zip 雜湊與 manifest 相符（發佈本身就壞了）：重新下載也一樣錯，要記成壞版，不能當成暫時的失敗一直重試
        if (kind == "重複的項目")
            ReplaceZip("0.2.2-bbbbbb2", ZipWithDuplicateEntry());
        else if (kind == "格式壞掉的zip")
            ReplaceZip("0.2.2-bbbbbb2", Encoding.UTF8.GetBytes("這不是 zip 檔"));
        var src = new RecordingSource(source);
        using var m = Manager(src);

        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Failed);
        src.ResumeOffsets.Should().Equal(new long[] { 0, 0 }, "內容錯誤刪掉重新下載一次");
        Directory.Exists(layout.VersionDirectory("0.2.2-bbbbbb2")).Should().BeFalse();
        Directory.Exists(layout.PartialVersionDirectory("0.2.2-bbbbbb2")).Should().BeFalse();
        File.Exists(layout.StagingZip("0.2.2-bbbbbb2")).Should().BeFalse("內容錯的不留著續傳");
        File.Exists(Path.Combine(layout.VersionsDirectory, "evil.txt")).Should().BeFalse();
        var state = UpdaterStateStore.Load(layout);
        state.Staged.Should().BeNull();
        state.Bad.Should().Contain("0.2.2-bbbbbb2", "第二次還是錯：發佈本身壞了，不再安裝");
        m.Status.LastResult.Should().Be("新版 0.2.2 下載的檔案驗證失敗兩次，不再安裝");
    }

    [Fact]
    public async Task 部分下載的檔案從原本的長度續傳()
    {
        var zipPath = ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        var bytes = File.ReadAllBytes(zipPath);
        Directory.CreateDirectory(layout.Staging);
        File.WriteAllBytes(layout.StagingZip("0.2.2-bbbbbb2"), bytes[..(bytes.Length / 2)]);
        var src = new RecordingSource(source);
        using var m = Manager(src);

        await m.CheckAsync();

        src.ResumeOffsets.Should().Equal(bytes.Length / 2);
        m.Status.Phase.Should().Be(AppUpdatePhase.Staged, m.Status.Detail);
    }

    [Fact]
    public async Task 下載到一半中斷_保留已下載的部分_下次檢查從那裡續傳()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        var src = new RecordingSource(source) { FailAfterBytes = 100 };
        using var m = Manager(src);

        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Failed);
        m.Status.Detail.Should().Contain("連線中斷");
        new FileInfo(layout.StagingZip("0.2.2-bbbbbb2")).Length.Should().Be(100, "連線中斷不是內容錯誤，留著下次續傳");
        UpdaterStateStore.Load(layout).Bad.Should().BeEmpty();

        await m.CheckAsync();

        src.ResumeOffsets.Should().Equal(0, 100);
        m.Status.Phase.Should().Be(AppUpdatePhase.Staged, m.Status.Detail);
    }

    [Fact]
    public async Task 下載進度高頻回報_節流後Changed次數有上限_位元組只增不減()
    {
        var zipLength = new FileInfo(ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key)).Length;
        using var m = Manager(new ChattySource(source, 20_000));
        var observed = new List<long>();
        m.Changed += () =>
        {
            lock (observed)
                observed.Add(m.Status.DownloadedBytes);
        };

        await m.CheckAsync();
        await Task.Delay(300);   // 回呼若排到執行緒集區（Progress<long> 的寫法），等它們跑完再數

        m.Status.Phase.Should().Be(AppUpdatePhase.Staged, m.Status.Detail);
        long[] changes;
        lock (observed)
            changes = observed.ToArray();
        changes.Length.Should().BeLessThan(10, "下載進度最多每 300 毫秒更新一次（同 CliManager），不是每一塊都送給網頁");
        changes.Should().BeInAscendingOrder("回報在下載的執行緒上依序處理，進度不會往回跳");
        m.Status.DownloadedBytes.Should().Be(zipLength, "下載結束時送出最後一次回報的位元組數");
    }

    [Fact]
    public async Task 部分下載的檔案比來源還大_刪掉從頭下載()
    {
        var zipPath = ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        var bytes = File.ReadAllBytes(zipPath);
        Directory.CreateDirectory(layout.Staging);
        File.WriteAllBytes(layout.StagingZip("0.2.2-bbbbbb2"), bytes.Concat(new byte[100]).ToArray());
        var src = new RecordingSource(source);
        using var m = Manager(src);

        await m.CheckAsync();

        src.ResumeOffsets.Should().Equal(bytes.Length + 100, 0);
        m.Status.Phase.Should().Be(AppUpdatePhase.Staged, m.Status.Detail);
        UpdaterStateStore.Load(layout).Bad.Should().BeEmpty();
    }

    [Fact]
    public async Task 已備妥後_較新版檢查失敗或被擋_仍維持備妥()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        var runtime = new FakeRuntime();
        using var m = Manager(runtime: runtime);
        await m.CheckAsync();
        m.Status.Phase.Should().Be(AppUpdatePhase.Staged, m.Status.Detail);

        // 讀不到 manifest（IOException）
        File.WriteAllText(Path.Combine(source, "latest.json"), new JObject { ["release"] = "0.2.3-cccccc3" }.ToString());
        await m.CheckAsync();
        m.Status.Phase.Should().Be(AppUpdatePhase.Staged);
        m.Status.StagedRelease.Should().Be("0.2.2-bbbbbb2");

        // 較新版缺執行階段（Blocked）
        ReleaseFixtures.WriteRelease(source, "0.2.3-cccccc3", Files, key);
        runtime.Has = false;
        await m.CheckAsync();
        m.Status.Phase.Should().Be(AppUpdatePhase.Staged);
        m.Status.StagedRelease.Should().Be("0.2.2-bbbbbb2");
    }

    [Fact]
    public async Task 狀態檔被占用_檢查不丟例外_已備妥的版本照樣維持()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        using var m = Manager();
        await m.CheckAsync();
        m.Status.Phase.Should().Be(AppUpdatePhase.Staged, m.Status.Detail);

        using (new FileStream(layout.StateFile, FileMode.Open, FileAccess.Read, FileShare.None))
            await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Staged);
        m.Status.Detail.Should().Contain("updater-state.json");
        UpdaterStateStore.Load(layout).Staged.Should().Be("0.2.2-bbbbbb2");
    }

    [Fact]
    public async Task 取得最新版之後狀態檔被占用_仍照壞版清單不下載()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        UpdaterStateStore.Update(layout, s =>
        {
            s.Bad.Add("0.2.2-bbbbbb2");
            return s;
        });
        FileStream? held = null;
        var src = new RecordingSource(source)
        {
            // 寫完 LastCheckAt、判斷壞版之前：防毒、備份軟體或別的程序換檔時開著狀態檔（UpdaterStateStore.Load 這時會回預設值）
            AfterGetLatest = () => held = new FileStream(layout.StateFile, FileMode.Open, FileAccess.Read, FileShare.None),
            BeforeDownload = () => held?.Dispose(),
        };
        using var m = Manager(src);

        try
        {
            await m.CheckAsync();
        }
        finally
        {
            held?.Dispose();
        }

        src.ResumeOffsets.Should().BeEmpty("壞版清單一律用嚴格讀取的狀態，讀不到也不能當成沒有壞版");
        m.Status.Phase.Should().Be(AppUpdatePhase.Idle, m.Status.Detail);
        UpdaterStateStore.Load(layout).Staged.Should().BeNull();
        Directory.Exists(layout.VersionDirectory("0.2.2-bbbbbb2")).Should().BeFalse();
    }

    [Fact]
    public async Task 下載期間被列為壞版_不寫成備妥()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        var src = new RecordingSource(source)
        {
            // 下載期間閃退保護或換版幫手把這一版列成壞版
            BeforeDownload = () => UpdaterStateStore.Update(layout, s =>
            {
                s.Bad.Add("0.2.2-bbbbbb2");
                return s;
            }),
        };
        using var m = Manager(src);

        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Failed, m.Status.Detail);
        m.Status.Detail.Should().Contain("壞版");
        m.Status.StagedRelease.Should().BeNull();
        var state = UpdaterStateStore.Load(layout);
        state.Staged.Should().BeNull("寫 Staged 的同一次 mutex 內看到它已是壞版");
        state.Bad.Should().Contain("0.2.2-bbbbbb2");
        Directory.Exists(layout.VersionDirectory("0.2.2-bbbbbb2")).Should().BeFalse();
    }

    [Fact]
    public async Task 不是新版或是壞版_不下載()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        using (var same = Manager(current: "0.2.2-cccccc3"))
        {
            await same.CheckAsync();
            same.Status.Phase.Should().Be(AppUpdatePhase.Idle);
        }
        UpdaterStateStore.Update(layout, s =>
        {
            s.Bad.Add("0.2.2-bbbbbb2");
            return s;
        });
        using var bad = Manager();
        await bad.CheckAsync();
        bad.Status.Phase.Should().Be(AppUpdatePhase.Idle);
        Directory.Exists(layout.VersionDirectory("0.2.2-bbbbbb2")).Should().BeFalse();
    }

    [Fact]
    public async Task 缺少執行階段或空間不足_擋下()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        using (var noRuntime = Manager(runtime: new FakeRuntime { Has = false }))
        {
            await noRuntime.CheckAsync();
            noRuntime.Status.Phase.Should().Be(AppUpdatePhase.Blocked);
            noRuntime.Status.Detail.Should().Contain("Microsoft.WindowsDesktop.App").And.Contain("10.0.0");
        }
        using var noSpace = Manager(freeSpace: _ => 10);
        await noSpace.CheckAsync();
        noSpace.Status.Phase.Should().Be(AppUpdatePhase.Blocked);
        noSpace.Status.Detail.Should().Contain("空間");
    }

    [Fact]
    public async Task 沒有啟用_顯示原因不檢查()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        var src = new RecordingSource(source);
        using var m = Manager(src, enablement: UpdateBlockReason.NotPaired);

        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Disabled);
        m.Status.Detail.Should().Be(InstallLayoutRules.Describe(UpdateBlockReason.NotPaired));
        src.ResumeOffsets.Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(UpdateBlockReason.Disabled))]
    [InlineData(nameof(UpdateBlockReason.CustomData))]
    public async Task 已備妥的版本_啟用條件不成立時是停用不是備妥_備妥的版本照樣顯示(string reason)
    {
        var block = Enum.Parse<UpdateBlockReason>(reason);
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        using (var first = Manager())
            await first.CheckAsync();

        // 自動更新開著時備妥，之後關掉自動更新（或以 --data 執行）再啟動：建構時讀 updater-state 不能直接當成備妥
        using var m = Manager(enablement: block);

        m.Status.Phase.Should().Be(AppUpdatePhase.Disabled, "設計 8.2「以下都成立才自動更新」：Phase=Staged 會讓協調器排空換版、結束時換版");
        m.Status.Detail.Should().Be(InstallLayoutRules.Describe(block));
        m.Status.StagedRelease.Should().Be("0.2.2-bbbbbb2", "備妥的版本留著給設定頁顯示，重新打開自動更新時直接用");
        m.IsEnabled.Should().BeFalse();
        m.RefreshFromState();
        m.Status.Phase.Should().Be(AppUpdatePhase.Disabled, "主視窗就緒時的重讀也一樣");
    }

    [Fact]
    public async Task 下載途中關掉自動更新_下載完不是備妥_重新打開後不再下載就是備妥()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        var block = UpdateBlockReason.None;
        var src = new RecordingSource(source) { BeforeDownload = () => block = UpdateBlockReason.Disabled };
        using var m = Manager(src, enablementNow: () => block);

        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Disabled, "下載途中關掉自動更新：這次檢查結束時不能是備妥（6 小時後的下一次檢查之前協調器都會套用它）");
        m.Status.Detail.Should().Be(InstallLayoutRules.Describe(UpdateBlockReason.Disabled));
        m.Status.StagedRelease.Should().Be("0.2.2-bbbbbb2");
        UpdaterStateStore.Load(layout).Staged.Should().Be("0.2.2-bbbbbb2", "已下載驗證的版本照樣記著，重新打開時不必再下載");

        block = UpdateBlockReason.None;
        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Staged, m.Status.Detail);
        src.ResumeOffsets.Should().ContainSingle("同一版已備妥就跳過下載");
    }

    [Fact]
    public async Task current已經指向來源提供的版本_不重新下載也不刪它()
    {
        // 結束時換版的空窗中使用者重開的舊版實例：幫手之後才把 current 切到新版，這個實例仍以為自己是舊版
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        var installed = layout.VersionDirectory("0.2.2-bbbbbb2");
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(installed, "Myriagent.exe"), "已安裝、正在使用");
        Junction.Create(layout.Current, installed);
        var src = new RecordingSource(source);
        using var m = Manager(src);

        await m.CheckAsync();

        src.ResumeOffsets.Should().BeEmpty("current 指向的就是這一版：已經安裝好了");
        File.ReadAllText(Path.Combine(installed, "Myriagent.exe")).Should().Be("已安裝、正在使用", "絕不刪掉 current 正指向的資料夾");
        m.Status.Phase.Should().Be(AppUpdatePhase.Idle);
        m.Status.StagedRelease.Should().BeNull();
        m.Status.Detail.Should().Contain("重新啟動");
        UpdaterStateStore.Load(layout).Staged.Should().BeNull();
    }

    [Fact]
    public async Task 下載途中current被切到這一版_不刪它也不備妥()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        var installed = layout.VersionDirectory("0.2.2-bbbbbb2");
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(installed, "Myriagent.exe"), "已安裝、正在使用");
        var src = new RecordingSource(source) { BeforeDownload = () => Junction.Create(layout.Current, installed) };
        using var m = Manager(src);

        await m.CheckAsync();

        src.ResumeOffsets.Should().ContainSingle();
        File.ReadAllText(Path.Combine(installed, "Myriagent.exe")).Should().Be("已安裝、正在使用", "改名前看到 current 指向它：不刪掉重建");
        m.Status.Phase.Should().Be(AppUpdatePhase.Idle);
        UpdaterStateStore.Load(layout).Staged.Should().BeNull();
        Directory.Exists(layout.PartialVersionDirectory("0.2.2-bbbbbb2")).Should().BeFalse();
        Directory.EnumerateFiles(layout.Staging).Should().BeEmpty();
    }

    [Fact]
    public async Task 沒有可用的發佈_維持閒置()
    {
        using var m = Manager();
        await m.CheckAsync();
        m.Status.Phase.Should().Be(AppUpdatePhase.Idle);
        m.Status.LastCheckAt.Should().NotBeNull();
    }

    [Fact]
    public async Task latest_json的release不是字串_當成沒有可用的發佈()
    {
        File.WriteAllText(Path.Combine(source, "latest.json"), new JObject { ["release"] = new JObject { ["x"] = 1 } }.ToString());
        using var m = Manager();

        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Idle);
        m.Status.Detail.Should().Be("沒有可用的發佈");
    }

    [Fact]
    public async Task 沒有連上伺服器_不是失敗_顯示等待連上_退避重試_連上後備妥並歸零()
    {
        ReleaseFixtures.WriteRelease(source, "0.2.2-bbbbbb2", Files, key);
        var src = new FlakySource(source) { Throw = new Myriagent.Sync.ReleaseSourceUnavailableException("沒有連上伺服器", offline: true) };
        using var m = Manager(src);

        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Idle);
        m.Status.Detail.Should().Be("等待連上伺服器");
        m.ConsecutiveFailuresForTest.Should().Be(1);

        await m.CheckAsync();
        m.ConsecutiveFailuresForTest.Should().Be(2, "連續失敗的次數決定下一次的退避");

        src.Throw = null;
        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Staged, m.Status.Detail);
        m.ConsecutiveFailuresForTest.Should().Be(0);
    }

    [Fact]
    public async Task 伺服器暫停供應_顯示失敗與稍後再試_沒有新版時歸零()
    {
        var src = new FlakySource(source) { Throw = new Myriagent.Sync.ReleaseSourceUnavailableException("伺服器正在更新", TimeSpan.FromSeconds(30)) };
        using var m = Manager(src);

        await m.CheckAsync();

        m.Status.Phase.Should().Be(AppUpdatePhase.Failed);
        m.Status.Detail.Should().Contain("伺服器正在更新").And.Contain("稍後自動再試");
        m.ConsecutiveFailuresForTest.Should().Be(1);

        src.Throw = null;
        await m.CheckAsync();

        m.Status.Detail.Should().Be("沒有可用的發佈");
        m.ConsecutiveFailuresForTest.Should().Be(0);
    }
}
