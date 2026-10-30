using Myriagent.Update;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Ops;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Releases;

/// <summary>撤回的結果（版本頁與 admin app-release-withdraw 用）</summary>
internal enum AppReleaseWithdrawOutcome
{
    /// <summary>沒有這個版本（含 release 格式不對）</summary>
    NotFound,

    /// <summary>撤回了（原本是 available 或 pruned）</summary>
    Withdrawn,

    /// <summary>早就撤回了：不改任何東西</summary>
    AlreadyWithdrawn,
}

/// <summary>
/// 收進來的桌面版（U2；singleton）：app_releases 資料表、app-releases\ 的版本資料夾、最新版的記憶體快取、啟動對帳與保存（R29）。
/// 寫入（新增、撤回、改標 pruned）、資料夾的刪除與快取的重新載入一律在 <see cref="HubWriteLock"/> 裡做（同一個非同步流程可重入，
/// 不另設 store 自己的鎖）；快取整份換掉，讀取（<see cref="Latest"/>、<see cref="LatestPayload"/>）不用鎖。
/// 事件在鎖外觸發，處理器丟的例外只記錯誤。不要在持有 HubWriteLock 的流程裡呼叫寫入方法：那樣事件會在鎖裡觸發，
/// 處理器起的非同步工作會繼承「持有寫入鎖」的 AsyncLocal。這裡不寫稽核，由呼叫端寫。
/// 撤回與清除一律先改資料列、再刪資料夾：之後的 latest 與套件下載立刻不再提供它；下載中的 zip 以 FileShare.Read 開著，
/// 資料夾刪不掉就留到下次保存（進行中的下載照樣傳完）。
/// </summary>
internal sealed class AppReleaseStore(IServiceScopeFactory scopes, HubWriteLock writeLock, HubPaths paths,
    IOptions<HubOptions> options, TimeProvider time, ILogger<AppReleaseStore> log)
{
    /// <summary>收件途中的副本資料夾：app-releases\.partial-&lt;收件 id&gt;\（啟動對帳刪掉留下的）</summary>
    public const string PartialPrefix = ".partial-";

    private volatile ServableRelease? latest;

    /// <summary>新收的版本寫進資料表、快取更新之後觸發（<see cref="AddAsync"/>；冪等重送不呼叫 AddAsync，所以不觸發）。處理器不可阻塞</summary>
    public event Action<AppReleaseInfo>? Ingested;

    /// <summary>
    /// 新增、撤回、保存之後，<see cref="Latest"/> 跟之前不同時觸發（啟動對帳不觸發）。處理器不可阻塞。
    /// 目前沒有正式的訂閱者（只有測試）：已連線的裝置只在收件時收到 appRelease 通知（<see cref="Ingested"/>，規格 7.4），
    /// 撤回或清除之後不另外通知，由之後的 Hello（latestApp）與 latest 端點讀到新的快取。之後要在 latest 改變時通知裝置再訂閱它
    /// </summary>
    public event Action? LatestChanged;

    /// <summary>目前提供的最新版（Hello 的 latestApp）；沒有就是 null</summary>
    public AppReleaseInfo? Latest => latest?.Info;

    /// <summary>
    /// latest 端點的內容（manifest 原文的標準 base64 與簽章）；沒有就是 null。
    /// 載入時比對 manifest 的 SHA-256、簽章檔的 kid、zip 的大小，不符的版本記錯誤並跳過（不改資料表），改提供下一個可用版本
    /// </summary>
    public LatestAppRelease? LatestPayload => latest?.Payload;

    /// <summary>
    /// 啟動對帳（<see cref="StartupOrder.Releases"/>）：
    /// 1. 刪掉 .partial-&lt;id&gt;\（id 要通過 IsValidDeployId，其他東西一律不碰）；
    /// 2. 刪掉名稱是合法 release、但資料表沒有對應列的資料夾（收件在改名之後、寫資料表之前當掉留下的）；
    /// 3. available 的列缺任何一個檔就改標 pruned 並記警告；
    /// 4. 跑一次保存，再載入快取。不觸發事件。
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        writeLock.RunAsync(async () =>
        {
            DeletePartialFolders();
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
                var rows = await db.AppReleases.ToListAsync(cancellationToken);
                DeleteOrphanFolders(rows);
                var missing = false;
                foreach (var row in rows.Where(r => r.State == AppReleaseStates.Available && IsRelease(r.Release) && !HasAllFiles(r.Release)))
                {
                    row.State = AppReleaseStates.Pruned;
                    missing = true;
                    log.LogWarning("啟動對帳：版本 {Release} 缺檔，改標 pruned（不再提供）", row.Release);
                }
                if (missing)
                    await db.SaveChangesAsync(cancellationToken);
            }
            await PruneCoreAsync(cancellationToken);
            await ReloadAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>所有版本（含撤回與清除的），依收件時間由新到舊、同時間依 release</summary>
    public async Task<IReadOnlyList<AppReleaseRecord>> ListAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        return await db.AppReleases.AsNoTracking()
            .OrderByDescending(r => r.IngestedAt)
            .ThenBy(r => r.Release)
            .ToListAsync(cancellationToken);
    }

    /// <summary>找一個版本（不論狀態）；沒有或 release 格式不對回 null</summary>
    public async Task<AppReleaseRecord?> FindAsync(string release, CancellationToken cancellationToken)
    {
        if (!IsRelease(release))
            return null;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        return await db.AppReleases.AsNoTracking().FirstOrDefaultAsync(r => r.Release == release, cancellationToken);
    }

    /// <summary>
    /// 新增收進來的版本（只有收件會呼叫：檔案已經改名到 <see cref="FolderOf"/>）。state 必須是 available，release 是標準寫法、
    /// version 與 build 跟它一致，否則丟 <see cref="ArgumentException"/>；同名的列已存在時資料庫的主鍵擋下（DbUpdateException，U2 D14）。
    /// 不檢查版本順序、不搬檔、不寫稽核、不跑保存（都是收件流程的事）。
    /// 寫入、更新快取之後，先觸發 <see cref="LatestChanged"/>（最新版變了才觸發），再觸發 <see cref="Ingested"/>
    /// </summary>
    public async Task AddAsync(AppReleaseRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!ReleaseIdentity.TryParseRelease(record.Release, out var identity)
            || record.Version != identity!.Version.ToString()
            || record.Build != identity.Build)
            throw new ArgumentException($"release、version、build 不一致：{record.Release}／{record.Version}／{record.Build}", nameof(record));
        if (record.State != AppReleaseStates.Available)
            throw new ArgumentException($"新增的版本 state 必須是 {AppReleaseStates.Available}：{record.State}", nameof(record));

        await ChangeAsync(async () =>
        {
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
                db.AppReleases.Add(record);
                await db.SaveChangesAsync(cancellationToken);
            }
            await ReloadAsync(cancellationToken);
            return true;
        }, cancellationToken);
        Raise(Ingested, new AppReleaseInfo(record.Release, record.Version));
    }

    /// <summary>
    /// 撤回（擁有者）：available 或 pruned 改成 withdrawn 並記 withdrawn_at；已經撤回的不改。資料夾留給下一次保存刪
    /// （進行中的下載照樣傳完）。最新版變了就觸發 <see cref="LatestChanged"/>
    /// </summary>
    public Task<AppReleaseWithdrawOutcome> WithdrawAsync(string release, CancellationToken cancellationToken)
    {
        if (!IsRelease(release))
            return Task.FromResult(AppReleaseWithdrawOutcome.NotFound);
        return ChangeAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var row = await db.AppReleases.FirstOrDefaultAsync(r => r.Release == release, cancellationToken);
            if (row is null)
                return AppReleaseWithdrawOutcome.NotFound;
            if (row.State == AppReleaseStates.Withdrawn)
                return AppReleaseWithdrawOutcome.AlreadyWithdrawn;
            var wasAvailable = row.State == AppReleaseStates.Available;
            row.State = AppReleaseStates.Withdrawn;
            row.WithdrawnAt = time.GetUtcNow().ToUnixTimeMilliseconds();
            await db.SaveChangesAsync(cancellationToken);
            if (wasAvailable)
                await ReloadAsync(cancellationToken);
            return AppReleaseWithdrawOutcome.Withdrawn;
        }, cancellationToken);
    }

    /// <summary>
    /// 保存（R29）：<see cref="AppReleaseRules.FoldersToDelete"/> 列出的版本，還是 available 的先改標 pruned（並重新載入快取），
    /// 再逐一刪資料夾。刪不掉（下載中的 zip 開著，沒有 FileShare.Delete）就記資訊、留到下次：列已經是 pruned 或 withdrawn，
    /// 下一次的 FoldersToDelete 還會列出它。最新版變了就觸發 <see cref="LatestChanged"/>
    /// </summary>
    public Task PruneAsync(CancellationToken cancellationToken) =>
        ChangeAsync(async () =>
        {
            await PruneCoreAsync(cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>版本資料夾 app-releases\&lt;release&gt;\；release 不合法丟 <see cref="ArgumentException"/>（不拿沒驗證過的字串組路徑）</summary>
    public string FolderOf(string release) =>
        IsRelease(release)
            ? Path.Combine(paths.AppReleasesDir, release)
            : throw new ArgumentException($"不是合法的 release 名稱：{release}", nameof(release));

    public string ZipPathOf(string release) => Path.Combine(FolderOf(release), ReleasePackageRules.FileNames(release).Zip);

    public string ManifestPathOf(string release) => Path.Combine(FolderOf(release), ReleasePackageRules.FileNames(release).Manifest);

    public string SignaturePathOf(string release) => Path.Combine(FolderOf(release), ReleasePackageRules.FileNames(release).Signature);

    /// <summary>收件途中的副本資料夾 app-releases\.partial-&lt;id&gt;\；id 要通過 IsValidDeployId，否則丟 <see cref="ArgumentException"/></summary>
    public string PartialFolderOf(string id) =>
        AdminInboxRules.IsValidDeployId(id)
            ? Path.Combine(paths.AppReleasesDir, PartialPrefix + id)
            : throw new ArgumentException($"不是合法的收件 id：{id}", nameof(id));

    /// <summary>在寫入鎖裡做 <paramref name="work"/>，前後比較最新版；變了就在鎖外觸發 <see cref="LatestChanged"/></summary>
    private async Task<T> ChangeAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        AppReleaseInfo? before = null;
        AppReleaseInfo? after = null;
        var result = await writeLock.RunAsync(async () =>
        {
            before = Latest;
            var value = await work();
            after = Latest;
            return value;
        }, cancellationToken);
        if (before != after)
            Raise(LatestChanged);
        return result;
    }

    /// <summary>保存的本體（呼叫端持有寫入鎖）：先改資料列並重新載入快取，再刪資料夾</summary>
    private async Task PruneCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> targets;
        var marked = false;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var rows = await db.AppReleases.ToListAsync(cancellationToken);
            targets = AppReleaseRules.FoldersToDelete(rows, options.Value.Releases.KeepAvailable);
            var set = targets.ToHashSet(StringComparer.Ordinal);
            foreach (var row in rows.Where(r => r.State == AppReleaseStates.Available && set.Contains(r.Release)))
            {
                row.State = AppReleaseStates.Pruned;
                marked = true;
                log.LogInformation("版本 {Release} 超過保留數量，改標 pruned（不再提供）", row.Release);
            }
            if (marked)
                await db.SaveChangesAsync(cancellationToken);
        }
        if (marked)
            await ReloadAsync(cancellationToken);
        foreach (var release in targets)
        {
            var folder = FolderOf(release);
            if (Directory.Exists(folder))
                TryDeleteFolder(release, folder);
        }
    }

    /// <summary>
    /// 重新載入快取（呼叫端持有寫入鎖）：available 的版本由新到舊，提供第一個檔案齊全而且跟資料表相符的；
    /// 不符的記錯誤、跳過（不改資料表）
    /// </summary>
    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        List<AppReleaseRecord> candidates;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            candidates = await db.AppReleases.AsNoTracking().Where(r => r.State == AppReleaseStates.Available).ToListAsync(cancellationToken);
        }
        ServableRelease? next = null;
        while (next is null && AppReleaseRules.PickLatest(candidates) is { } row)
        {
            next = TryLoad(row);
            candidates.Remove(row);
        }
        latest = next;
    }

    /// <summary>讀 manifest 原文與簽章檔：manifest 的 SHA-256、簽章檔的 kid、zip 的大小都要跟資料表相符，否則記錯誤、回 null（不改資料表）</summary>
    private ServableRelease? TryLoad(AppReleaseRecord row)
    {
        try
        {
            var manifest = new FileInfo(ManifestPathOf(row.Release));
            var signature = new FileInfo(SignaturePathOf(row.Release));
            var zip = new FileInfo(ZipPathOf(row.Release));
            if (!manifest.Exists || !signature.Exists || !zip.Exists)
            {
                log.LogError("版本 {Release} 缺檔，不提供", row.Release);
                return null;
            }
            if (manifest.Length > ReleaseManifestRules.MaxManifestBytes || signature.Length > ReleaseManifestRules.MaxSignatureBytes)
            {
                log.LogError("版本 {Release} 的 manifest 或簽章檔超過上限，不提供", row.Release);
                return null;
            }
            var manifestBytes = File.ReadAllBytes(manifest.FullName);
            if (!string.Equals(ReleaseManifestRules.Sha256Hex(manifestBytes), row.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                log.LogError("版本 {Release} 的 manifest 跟資料表的 SHA-256 不符，不提供", row.Release);
                return null;
            }
            if (!ReleaseManifestRules.TryParseSignature(File.ReadAllText(signature.FullName), out var sig)
                || !string.Equals(sig.Kid, row.Kid, StringComparison.OrdinalIgnoreCase))
            {
                log.LogError("版本 {Release} 的簽章檔格式不對或 kid 跟資料表不符，不提供", row.Release);
                return null;
            }
            if (zip.Length != row.ZipBytes)
            {
                log.LogError("版本 {Release} 的 zip 大小 {Actual} 跟資料表的 {Expected} 不符，不提供", row.Release, zip.Length, row.ZipBytes);
                return null;
            }
            var payload = new LatestAppRelease(row.Release, row.Version, row.Build, Convert.ToBase64String(manifestBytes), sig, row.ZipBytes);
            return new ServableRelease(new AppReleaseInfo(row.Release, row.Version), payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "版本 {Release} 的檔案讀不到，不提供", row.Release);
            return null;
        }
    }

    /// <summary>先刪 zip：下載中開著它（沒有 FileShare.Delete）就在這裡失敗，manifest 與簽章檔留著、資料夾保持完整；之後刪整個資料夾</summary>
    private void TryDeleteFolder(string release, string folder)
    {
        try
        {
            File.Delete(ZipPathOf(release));
            Directory.Delete(folder, recursive: true);
            log.LogInformation("已刪除版本資料夾 {Release}", release);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogInformation("版本資料夾 {Release} 刪不掉（可能正在下載），下次再刪：{Error}", release, ex.Message);
        }
    }

    private void DeletePartialFolders()
    {
        if (!Directory.Exists(paths.AppReleasesDir))
            return;
        foreach (var dir in new DirectoryInfo(paths.AppReleasesDir).EnumerateDirectories(PartialPrefix + "*"))
        {
            if (dir.Name.StartsWith(PartialPrefix, StringComparison.Ordinal) && AdminInboxRules.IsValidDeployId(dir.Name[PartialPrefix.Length..]))
                TryDeleteDirectory(dir.FullName, "收件留下的副本");
        }
    }

    private void DeleteOrphanFolders(IReadOnlyCollection<AppReleaseRecord> rows)
    {
        if (!Directory.Exists(paths.AppReleasesDir))
            return;
        var known = rows.Select(r => r.Release).ToHashSet(StringComparer.Ordinal);
        foreach (var dir in new DirectoryInfo(paths.AppReleasesDir).EnumerateDirectories())
        {
            if (IsRelease(dir.Name) && !known.Contains(dir.Name))
                TryDeleteDirectory(dir.FullName, "沒有資料列的版本資料夾");
        }
    }

    private void TryDeleteDirectory(string path, string what)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            log.LogWarning("啟動對帳：已刪除{What} {Path}", what, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "啟動對帳：{What} {Path} 刪不掉，下次啟動再試", what, path);
        }
    }

    private bool HasAllFiles(string release) =>
        File.Exists(ZipPathOf(release)) && File.Exists(ManifestPathOf(release)) && File.Exists(SignaturePathOf(release));

    private static bool IsRelease(string? release) => ReleaseIdentity.TryParseRelease(release, out _);

    private void Raise(Action? handlers)
    {
        foreach (var handler in handlers?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "LatestChanged 的處理器丟出例外");
            }
        }
    }

    private void Raise(Action<AppReleaseInfo>? handlers, AppReleaseInfo info)
    {
        foreach (var handler in handlers?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<AppReleaseInfo>)handler)(info);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Ingested 的處理器丟出例外：{Release}", info.Release);
            }
        }
    }

    /// <summary>快取的可提供版本：Hello 用的摘要、latest 端點的內容</summary>
    private sealed record ServableRelease(AppReleaseInfo Info, LatestAppRelease Payload);
}
