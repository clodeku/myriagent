using System.IO.Compression;
using System.Security.Cryptography;
using Myriagent.Core;

namespace Myriagent.Update;

internal enum AppUpdatePhase
{
    Idle,
    Checking,
    Downloading,
    Staged,
    Blocked,
    Failed,
    Disabled,
}

/// <param name="AvailableRelease">來源宣告、而且會安裝的新版（檢查通過後）</param>
/// <param name="Detail">Blocked、Failed、Disabled 的原因；Idle 時可能是「已是最新版」</param>
internal sealed record AppUpdateStatus(
    AppUpdatePhase Phase,
    string? AvailableRelease,
    string? StagedRelease,
    long DownloadedBytes,
    long TotalBytes,
    string? Detail,
    DateTime? LastCheckAt,
    string? LastResult,
    DateTime? LastResultAt);

/// <summary>
/// 檢查、下載、驗證、備妥（設計 8.4）。自己加鎖（同 CliManager），工作在背景執行緒；Changed 在背景執行緒觸發、不在鎖內。
/// 失敗的處理：下載中斷（網路、睡眠、取消）保留 .partial 下次續傳；內容不符（雜湊、zip 格式、檔案清單、路徑）刪掉 .partial 與解壓暫存
/// </summary>
internal sealed class AppUpdateManager : IDisposable
{
    public const string ManifestFileName = "release.manifest.json";
    public const string SignatureFileName = "release.manifest.sig";

    /// <summary>同一版內容驗證失敗時最多下載幾次（第一次失敗重新下載一次，第二次記成壞版）</summary>
    public const int ContentAttempts = 2;

    private readonly InstallLayout layout;
    private readonly ReleaseIdentity current;
    private readonly IAppReleaseSource? source;
    private readonly Func<UpdateBlockReason> enablement;
    private readonly IReadOnlyList<ReleaseKey> trustedKeys;
    private readonly IRuntimeProbe runtime;
    private readonly Func<string, long?> freeSpace;
    private readonly object gate = new();
    /// <summary>
    /// 這次執行中簽章不符的版本 → 第一次的原因：不再重試（設計第 12 節）；app 重新啟動才會再試（伺服器修正簽章後不必等新版號）。
    /// 只在檢查工作裡讀寫（同時只跑一個）
    /// </summary>
    private readonly Dictionary<string, string> signatureRejected = new(StringComparer.Ordinal);
    private AppUpdateStatus status;
    private Task? running;
    private System.Threading.Timer? timer;
    /// <summary>暫時失敗之後的重試（U3：照 Retry-After 或 1、2、4…60 分鐘退避，AppUpdateRetryRules）；成功或沒有新版時取消</summary>
    private System.Threading.Timer? retryTimer;
    /// <summary>連續暫時失敗的次數（只在檢查工作裡讀寫，同時只跑一個）</summary>
    private int consecutiveFailures;
    private bool disposed;

    public AppUpdateManager(InstallLayout layout, ReleaseIdentity current, IAppReleaseSource? source, Func<UpdateBlockReason> enablement,
        IReadOnlyList<ReleaseKey> trustedKeys, IRuntimeProbe runtime, Func<string, long?>? freeSpace = null)
    {
        this.layout = layout;
        this.current = current;
        this.source = source;
        this.enablement = enablement;
        this.trustedKeys = trustedKeys;
        this.runtime = runtime;
        this.freeSpace = freeSpace ?? DefaultFreeSpace;
        status = new AppUpdateStatus(AppUpdatePhase.Idle, null, null, 0, 0, null, null, null, null);
        RefreshFromState();
    }

    /// <summary>這個程序的 .NET 已被移除時是哪一版（IRuntimeProbe.RemovedRuntime）；協調器據此在閒置時重新啟動 app</summary>
    public string? RuntimeRemoved => runtime.RemovedRuntime;

    public AppUpdateStatus Status
    {
        get
        {
            lock (gate)
                return status;
        }
    }

    public event Action? Changed;

    /// <summary>啟用條件現在都成立（設計 8.2）：協調器開始排空或結束時換版之前再確認一次（Phase 也一律反映它，這是第二道）</summary>
    public bool IsEnabled => enablement() == UpdateBlockReason.None;

    /// <summary>重讀 updater-state.json：已備妥的版本（app 重開後仍有效）、最近一次的結果、上次檢查時間</summary>
    public void RefreshFromState()
    {
        var state = UpdaterStateStore.Load(layout);
        var staged = state.Staged is { } release && ReleaseIdentity.TryParseRelease(release, out var id)
                     && UpdaterStateRules.ShouldInstall(state, id!, current) && File.Exists(layout.Executable(release))
            ? release
            : null;
        Update(s => s with
        {
            Phase = staged != null ? AppUpdatePhase.Staged : s.Phase == AppUpdatePhase.Staged ? AppUpdatePhase.Idle : s.Phase,
            StagedRelease = staged,
            AvailableRelease = staged ?? s.AvailableRelease,
            LastResult = state.LastResult,
            LastResultAt = state.LastResultAt,
            LastCheckAt = state.LastCheckAt ?? s.LastCheckAt,
        });
    }

    /// <summary>定期檢查（第一次在 firstDelay 之後）</summary>
    public void Start(TimeSpan firstDelay, TimeSpan interval)
    {
        lock (gate)
        {
            if (disposed)
                return;
            timer?.Dispose();
            timer = new System.Threading.Timer(_ => _ = CheckAsync(), null, firstDelay, interval);
        }
    }

    /// <summary>檢查一次（同時只跑一次：正在跑就回同一個工作）</summary>
    public Task CheckAsync(CancellationToken ct = default)
    {
        lock (gate)
        {
            if (disposed)
                return Task.CompletedTask;
            if (running is { IsCompleted: false })
                return running;
            running = Task.Run(() => RunCheckAsync(ct), ct);
            return running;
        }
    }

    private async Task RunCheckAsync(CancellationToken ct)
    {
        var failed = false;
        TimeSpan? retryAfter = null;
        try
        {
            (failed, retryAfter) = await RunCheckCoreAsync(ct);
        }
        finally
        {
            ScheduleRetry(failed, retryAfter);
        }
    }

    /// <summary>這次檢查有沒有暫時失敗（要不要退避重試）與伺服器給的 Retry-After</summary>
    private async Task<(bool Failed, TimeSpan? RetryAfter)> RunCheckCoreAsync(CancellationToken ct)
    {
        var block = enablement();
        if (block != UpdateBlockReason.None || source == null)
        {
            Update(s => s with { Phase = AppUpdatePhase.Disabled, Detail = InstallLayoutRules.Describe(block == UpdateBlockReason.None ? UpdateBlockReason.NotPaired : block) });
            return (false, null);
        }
        var now = DateTime.Now;
        var stagedBefore = Status.StagedRelease;
        Update(s => s with { Phase = AppUpdatePhase.Checking, Detail = null, LastCheckAt = now });
        try
        {
            // 壞版清單與撤銷的金鑰一律用這次嚴格讀取的狀態，讀不到就讓這次檢查失敗，絕不當成沒有壞版；Load 只給顯示用（RefreshFromState）
            var state = UpdaterStateStore.Update(layout, s =>
            {
                s.LastCheckAt = now;
                return s;
            });
            var offer = await source.GetLatestAsync(ct);
            if (offer == null)
            {
                SetIdle(stagedBefore, "沒有可用的發佈");
                return (false, null);
            }
            if (signatureRejected.TryGetValue(offer.Release, out var rejectedReason))
            {
                SetProblem(AppUpdatePhase.Failed, rejectedReason, offer.Release);   // 檢查開始時清掉了 Detail，補回第一次的原因
                return (false, null);
            }
            // 桌面的入口（PROTO-1）：簽章、格式、發佈政策，不看 protocolVersion（新版的協定比自己新是正常的；VerifyManifest 是伺服器收件用的）
            var verified = ReleasePackageRules.VerifyOffer(offer.ManifestBytes, offer.SignatureJson, trustedKeys, state.RevokedKids);
            if (verified.Step == ReleasePackageSteps.Signature)
            {
                var reason = $"簽章不符：{verified.Error}";
                signatureRejected[offer.Release] = reason;
                Fail(reason, offer.Release);
                return (false, null);
            }
            if (!verified.Ok || verified.Manifest!.Identity.Release != offer.Release)
            {
                Fail($"manifest 不合法：{verified.Error ?? "release 與宣告的不符"}", offer.Release);
                return (false, null);
            }
            var manifest = verified.Manifest;
            var release = manifest.Identity.Release;
            if (!UpdaterStateRules.ShouldInstall(state, manifest.Identity, current))
            {
                SetIdle(stagedBefore, "已是最新版");
                return (false, null);
            }
            if (IsCurrentTarget(release))
            {
                SetInstalled(release);
                return (false, null);
            }
            if (stagedBefore == release)
            {
                Update(s => s with { Phase = AppUpdatePhase.Staged, AvailableRelease = release });
                return (false, null);
            }
            if (!runtime.HasAtLeast(manifest.MinRuntime.Name, manifest.MinRuntime.Version))
            {
                SetProblem(AppUpdatePhase.Blocked,
                    $"新版需要 {manifest.MinRuntime.Name} {manifest.MinRuntime.Version} 以上，請先安裝（https://dotnet.microsoft.com/download/dotnet）", release);
                return (false, null);
            }
            // manifest 沒有 zip 的大小：以解壓後的總大小估（zip 不會比它大），zip＋解壓＋餘裕至少 3 倍
            if (freeSpace(layout.Root) is { } free && free < 3 * manifest.TotalBytes)
            {
                SetProblem(AppUpdatePhase.Blocked, $"磁碟空間不足：至少需要 {3 * manifest.TotalBytes / 1024 / 1024 + 1} MB", release);
                return (false, null);
            }
            // 這個程序的 .NET 已被移除（.NET 更新刪掉了舊的修補版）：解壓要載入的組件找不到，下載了也裝不起來（2026-10-27 一直重下載）。
            // 不下載、不退避重試，等協調器在閒置時重新啟動 app（RuntimeRemoved）
            if (runtime.RemovedRuntime is { } removed)
            {
                SetProblem(AppUpdatePhase.Blocked, RuntimeRules.RemovedDetail(removed), release);
                return (false, null);
            }
            await StageAsync(offer, manifest, ct);
            return (false, null);
        }
        catch (OperationCanceledException)
        {
            SetIdle(stagedBefore, "已取消");
            return (false, null);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or HttpRequestException or UnauthorizedAccessException or InvalidDataException
                                       or Newtonsoft.Json.JsonException)
        {
            // 連不上、讀不到（含 updater-state.json 被占用、等不到它的鎖）：保留部分下載，退避之後續傳（U3：照伺服器的 Retry-After 或 1、2、4…60 分鐘）
            if (ex is IRetryAfterHint { Offline: true })
            {
                // 沒有連上伺服器（同步還沒連上、離線）：不是失敗，等一下再試
                Log.Info($"檢查新版：{ex.Message}，稍後再試");
                SetIdle(stagedBefore, "等待連上伺服器");
                return (true, null);
            }
            // 訊息是空的也要看得出是什麼（2026-10-27：.NET 被移除後載入組件失敗，記錄只有「檢查或下載新版失敗：」）
            var reason = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            Log.Warn($"檢查或下載新版失敗（{ex.GetType().Name}）：{reason}");
            SetProblem(AppUpdatePhase.Failed, $"下載失敗：{reason}（稍後自動再試）");
            return (true, (ex as IRetryAfterHint)?.RetryAfter);
        }
    }

    /// <summary>
    /// 檢查結束（同一個檢查工作裡）：暫時失敗就排一次退避後的重試（U3，AppUpdateRetryRules），成功或沒有新版時取消重試、歸零失敗次數。
    /// 每 6 小時的定期檢查照舊
    /// </summary>
    private void ScheduleRetry(bool failed, TimeSpan? retryAfter)
    {
        lock (gate)
        {
            retryTimer?.Dispose();
            retryTimer = null;
            if (disposed)
                return;
            if (!failed)
            {
                consecutiveFailures = 0;
                return;
            }
            consecutiveFailures++;
            var delay = AppUpdateRetryRules.NextDelay(consecutiveFailures, retryAfter);
            Log.Info($"新版的檢查或下載 {delay.TotalMinutes:0.#} 分鐘後再試（連續失敗 {consecutiveFailures} 次）");
            retryTimer = new System.Threading.Timer(_ => _ = CheckAsync(), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>連續暫時失敗的次數（測試）</summary>
    internal int ConsecutiveFailuresForTest
    {
        get
        {
            lock (gate)
                return consecutiveFailures;
        }
    }

    private async Task StageAsync(ReleaseOffer offer, ReleaseManifest manifest, CancellationToken ct)
    {
        var release = manifest.Identity.Release;
        Directory.CreateDirectory(layout.Staging);
        var zipPath = layout.StagingZip(release);
        foreach (var stale in Directory.EnumerateFiles(layout.Staging).Where(f => !string.Equals(f, zipPath, StringComparison.OrdinalIgnoreCase)))
            TryDeleteFile(stale);

        var partial = layout.PartialVersionDirectory(release);
        // 內容錯誤（zip 雜湊、格式、清單、路徑）刪掉重新下載一次；再錯就是發佈本身壞了，記成壞版（設計第 12 節）
        for (int attempt = 1; ; attempt++)
        {
            Update(s => s with { Phase = AppUpdatePhase.Downloading, AvailableRelease = release, DownloadedBytes = 0, TotalBytes = manifest.TotalBytes });
            var contentError = await DownloadAsync(offer, manifest.Zip, zipPath, ct) ?? await VerifyAndExtractAsync(zipPath, partial, manifest, ct);
            if (contentError == null)
                break;
            DeleteDirectory(partial);
            TryDeleteFile(zipPath);
            if (attempt >= ContentAttempts)
            {
                MarkBad(release, contentError);
                return;
            }
            Log.Warn($"新版 {release} 驗證失敗（{contentError}），重新下載一次");
        }
        await File.WriteAllBytesAsync(Path.Combine(partial, ManifestFileName), offer.ManifestBytes, ct);
        await File.WriteAllTextAsync(Path.Combine(partial, SignatureFileName), offer.SignatureJson, ct);

        var target = layout.VersionDirectory(release);
        if (IsCurrentTarget(release))
        {
            // 下載期間 current 被切到這一版（結束時換版的幫手）：它已經安裝好、可能正被使用，絕不刪掉重建
            DeleteDirectory(partial);
            TryDeleteFile(zipPath);
            SetInstalled(release);
            return;
        }
        // 上次失敗留下的同名資料夾（不會是目前版或前一版：ShouldInstall 已確認它比目前新，上面也確認了 current 沒有指向它）
        DeleteDirectory(target);
        Directory.Move(partial, target);
        TryDeleteFile(zipPath);
        // 寫 Staged 的同一次 mutex 內再檢查一次壞版：下載期間閃退保護或換版幫手把它列成壞版，就不備妥
        var becameBad = false;
        UpdaterStateStore.Update(layout, s =>
        {
            if (s.Bad.Contains(release, StringComparer.Ordinal))
            {
                becameBad = true;
                return s;
            }
            return UpdaterStateRules.OnStaged(s, release);
        });
        if (becameBad)
        {
            DeleteDirectory(target);
            Fail("下載期間這一版被列為壞版，不備妥", release);
            return;
        }
        Log.Info($"新版 {release} 已下載驗證，等待套用");
        Update(s => s with { Phase = AppUpdatePhase.Staged, StagedRelease = release, AvailableRelease = release, Detail = null });
    }

    /// <summary>
    /// 下載（續傳）zip 到 staging。來源說已下載的部分接不上（InvalidDataException，例如比來源還大）時回錯誤：當成內容錯誤，刪掉從頭下載。
    /// 連線中斷等其他例外往外丟，保留已下載的部分
    /// </summary>
    private async Task<string?> DownloadAsync(ReleaseOffer offer, string zipName, string zipPath, CancellationToken ct)
    {
        var progress = new ThrottledProgress(bytes => Update(s => s with { DownloadedBytes = bytes }));
        try
        {
            await source!.DownloadZipAsync(offer, zipName, zipPath, progress, ct);
            progress.Flush();
            return null;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 下載進度的節流（同 CliManager 下載 CLI 的 300 毫秒；給人看的即時更新可合併可丟）：來源每讀一小塊就回報，每一則都送到網頁會讓
    /// 橫幅與設定頁一直重畫。在回報的執行緒上同步處理（不像 Progress&lt;long&gt; 排到執行緒集區，順序不會亂、進度不會往回跳），
    /// 距上次送出不到 300 毫秒就只記下最新值，下載結束時 Flush 補送最後一次。來源在同一個下載迴圈裡依序回報，不需要加鎖
    /// </summary>
    private sealed class ThrottledProgress : IProgress<long>
    {
        private const long IntervalMs = 300;

        private readonly Action<long> publish;
        private long latest;
        private long published = -1;
        private long publishedAt;

        public ThrottledProgress(Action<long> publish) => this.publish = publish;

        public void Report(long value)
        {
            latest = value;
            var now = Environment.TickCount64;
            if (published >= 0 && now - publishedAt < IntervalMs)
                return;
            Publish(now);
        }

        /// <summary>補送節流期間沒送出的最後一次回報（沒有回報過就不送）</summary>
        public void Flush()
        {
            if (published >= 0 && latest != published)
                Publish(Environment.TickCount64);
        }

        private void Publish(long now)
        {
            published = latest;
            publishedAt = now;
            publish(latest);
        }
    }

    /// <summary>zip 的 SHA-256（設計 8.4 第 5 步），通過才解壓逐檔驗證。回傳錯誤或 null</summary>
    private static async Task<string?> VerifyAndExtractAsync(string zipPath, string partial, ReleaseManifest manifest, CancellationToken ct)
    {
        string zipSha;
        await using (var stream = File.OpenRead(zipPath))
            zipSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (zipSha != manifest.ZipSha256)
            return "下載的檔案雜湊不符";
        DeleteDirectory(partial);
        Directory.CreateDirectory(partial);
        try
        {
            return ExtractVerified(zipPath, partial, manifest);
        }
        catch (InvalidDataException ex)
        {
            // 雜湊與簽過的 manifest 相符、zip 本身卻壞掉：發佈就是壞的，重新下載也一樣，不能當成暫時的失敗一直重試
            return $"zip 格式不對：{ex.Message}";
        }
    }

    /// <summary>同一版的內容驗證失敗兩次：記成壞版（不再安裝），設定頁顯示原因</summary>
    private void MarkBad(string release, string reason)
    {
        var result = $"新版 {ResumeIntentRules.VersionOfRelease(release)} 下載的檔案驗證失敗兩次，不再安裝";
        var now = DateTime.Now;
        UpdaterStateStore.Update(layout, s =>
        {
            if (!s.Bad.Contains(release))
                s.Bad.Add(release);
            s.LastResult = result;
            s.LastResultAt = now;
            return s;
        });
        Log.Warn($"新版 {release} 沒有備妥：{reason}（第二次，記成壞版）");
        Update(s => s with
        {
            Phase = s.StagedRelease != null ? AppUpdatePhase.Staged : AppUpdatePhase.Failed,
            Detail = reason,
            LastResult = result,
            LastResultAt = now,
        });
    }

    /// <summary>
    /// 解壓並逐檔驗證（設計 8.4 第 6 步）：路徑必須正好在清單裡（擋 zip-slip）、同一個路徑只出現一次、宣告的長度不超過清單、大小與 SHA-256 相符、不多不少。
    /// 回傳錯誤或 null。zip 格式壞掉時丟 InvalidDataException。internal 是給 ReleasePackageRulesTests 比對它與伺服器的 ReleaseZipRules 判斷一致
    /// （兩份沒有合併，規格第 16 節第 58 項）
    /// </summary>
    internal static string? ExtractVerified(string zipPath, string destination, ReleaseManifest manifest)
    {
        var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var actual = new List<(string Path, long Size)>();
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (path.EndsWith('/'))
                continue;   // 資料夾項目
            if (!ReleaseManifestRules.IsValidPath(path) || !expected.TryGetValue(path, out var file))
                return $"zip 裡有清單以外的檔案：{path}";
            if (!seen.Add(path))
                return $"zip 裡的檔案重複：{path}";   // 不檢查的話第二個項目在 CreateNew 丟 IOException，會被當成暫時的失敗一直重試
            if (entry.Length > file.Size)
                return $"檔案比清單大：{path}";   // 宣告的長度就超過：不建檔、不解壓（同伺服器的 ReleaseZipRules）
            var full = Path.GetFullPath(Path.Combine(destination, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return $"zip 裡的路徑跳出安裝資料夾：{path}";
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            long written = 0;
            using (var input = entry.Open())
            using (var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    if (written > file.Size)
                        return $"檔案比清單大：{path}";
                    output.Write(buffer, 0, read);
                    hash.AppendData(buffer, 0, read);
                }
                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                    return $"檔案雜湊不符：{path}";
            }
            actual.Add((path, written));
        }
        return ReleaseManifestRules.CheckFileSet(manifest.Files, actual);
    }

    private void SetIdle(string? staged, string detail)
        => Update(s => s with { Phase = staged != null ? AppUpdatePhase.Staged : AppUpdatePhase.Idle, Detail = staged != null ? null : detail });

    /// <summary>
    /// current 已經指向這一版：執行中的這份比 current 舊（換版的空窗中使用者重開的舊版實例，換版幫手之後才把 current 切到新版）。
    /// 不下載、不備妥（重新備妥會刪掉 current 正指向的資料夾）；重新啟動 app 就會用它。讀不到 current 當成沒有指向
    /// </summary>
    private bool IsCurrentTarget(string release)
    {
        try
        {
            return Junction.GetTarget(layout.Current) is { } target
                   && string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(target)), release, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"讀不到 {layout.Current} 的目標：{ex.Message}");
            return false;
        }
    }

    private void SetInstalled(string release)
    {
        Log.Info($"新版 {release} 已經是 current 指向的版本，不再下載");
        Update(s => s with
        {
            Phase = AppUpdatePhase.Idle,
            StagedRelease = null,
            Detail = $"新版 {ResumeIntentRules.VersionOfRelease(release)} 已經安裝好，重新啟動 app 後就會使用",
        });
    }

    private void Fail(string reason, string release)
    {
        Log.Warn($"新版 {release} 沒有備妥：{reason}");
        SetProblem(AppUpdatePhase.Failed, reason);
    }

    /// <summary>較新版的檢查失敗或被擋時，已備妥的版本照樣有效：有 StagedRelease 就維持 Staged（原因留在 Detail 與日誌）</summary>
    private void SetProblem(AppUpdatePhase phase, string? detail, string? available = null)
        => Update(s => s with
        {
            Phase = s.StagedRelease != null ? AppUpdatePhase.Staged : phase,
            Detail = detail ?? s.Detail,
            AvailableRelease = available ?? s.AvailableRelease,
        });

    /// <summary>
    /// 改狀態並通知。Phase 一律反映啟用條件（設計 8.2「以下都成立才自動更新」）：不成立時不回報 Staged（協調器看到 Staged 就會排空換版），
    /// 改成 Disabled 並說明原因。涵蓋建構與重讀狀態檔、檢查途中關掉自動更新（併進正在跑的那次）之後的每個結尾；
    /// StagedRelease 照樣保留給設定頁顯示，重新打開自動更新後的檢查直接回到 Staged、不再下載
    /// </summary>
    private void Update(Func<AppUpdateStatus, AppUpdateStatus> change)
    {
        var block = enablement();
        lock (gate)
        {
            var next = change(status);
            status = next.Phase == AppUpdatePhase.Staged && block != UpdateBlockReason.None
                ? next with { Phase = AppUpdatePhase.Disabled, Detail = InstallLayoutRules.Describe(block) }
                : next;
        }
        Changed?.Invoke();
    }

    private static long? DefaultFreeSpace(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"刪不掉 {path}：{ex.Message}");
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"刪不掉 {path}：{ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer?.Dispose();
            timer = null;
            retryTimer?.Dispose();
            retryTimer = null;
        }
    }
}
