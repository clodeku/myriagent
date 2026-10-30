using System.Text;
using System.Text.Json;
using Myriagent.Update;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using MyriagentHub.Ops;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Releases;

/// <summary>
/// 收件匣的一次處理（U2 設計 T4）：admin-inbox\app-release\request.json 指名一個 release，把它的三個檔複製到
/// app-releases\.partial-&lt;id&gt;\、以內建金鑰（<see cref="IBuiltInReleaseKeys"/>）驗證副本，提交成 app-releases\&lt;release&gt;\ 加一列資料表，
/// 寫回執 status\app-release.json。處理期間一直開著 request.json（FileShare.Read）：送件腳本刪不掉、也改不了名，就知道正在處理。
/// 拒收一律：回執 ok:false、以警告寫服務記錄（不寫稽核，D19）、刪 partial、刪 request.json、三個檔留著。
/// 檔案被鎖住（送件端或防毒還開著）則什麼都不刪、不回執，下次輪詢再試。
/// 磁碟可用空間經 <see cref="IDiskSpaceProbe"/> 查（同 blob 與縮圖快取的接縫；測試換成固定值）
/// </summary>
internal sealed class AppReleaseIngestor(HubPaths paths, AppReleaseStore store, IBuiltInReleaseKeys builtIn, IDiskSpaceProbe disk,
    IOptions<HubOptions> options, AuditLog audit, TimeProvider time, ILogger<AppReleaseIngestor> log)
{
    /// <summary>回執的目標檔被讀取者開著時，換檔最多試幾次</summary>
    public const int ReceiptWriteAttempts = 5;

    /// <summary>回執換檔重試的間隔（真實時間：等的是別的程序關檔）</summary>
    public static readonly TimeSpan ReceiptRetryDelay = TimeSpan.FromMilliseconds(200);

    private const int ZipBufferBytes = 81920;

    /// <summary>處理收件匣裡的要求（一次一個）。回 null：沒有要求，或檔案被鎖住、等下次輪詢再試；否則回寫出的回執</summary>
    public async Task<AppReleaseReceipt?> ProcessAsync(CancellationToken cancellationToken)
    {
        var requestPath = Path.Combine(paths.AdminInboxAppReleaseDir, AppReleaseIngestRules.RequestFileName);
        var requestFile = TryOpenRequest(requestPath);
        if (requestFile is null)
            return null;

        var work = new Work();
        Outcome outcome;
        AppReleaseReceipt receipt;
        using (requestFile)
        {
            try
            {
                outcome = await IngestAsync(requestFile, work, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 服務停止：request.json 留著，下次啟動再處理
                DeletePartial(work);
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "收件匣處理 {Id} 時發生未預期的錯誤", work.Id);
                outcome = Outcome.Reject(AppReleaseIngestCodes.Internal, ex.GetType().Name + ": " + ex.Message);
            }
            DeletePartial(work);
            if (outcome.RetryLater)
            {
                log.LogInformation("收件匣的 {Id} 下次再試：{Reason}", work.Id, outcome.Reason);
                return null;
            }
            if (!outcome.Ok)
                log.LogWarning("收件匣拒收 {Id}（{Release}）：{Code}，{Reason}", work.Id, work.Release, outcome.Code, outcome.Reason);
            receipt = new AppReleaseReceipt(work.Id ?? "", outcome.Ok, work.Release, outcome.Code, time.GetUtcNow().ToUnixTimeMilliseconds());
            // 回執在關閉 request.json 之前寫好：送件腳本看到 request.json 改得動時，回執一定已經在了
            await WriteReceiptAsync(receipt);
        }

        // 成功（含冪等的重送）才清掉三個檔；先刪三個檔再刪 request.json
        if (outcome.Ok && work.Release is { } release)
            DeleteInboxFiles(release);
        TryDeleteFile(requestPath);
        if (outcome.Code == AppReleaseIngestCodes.Ingested)
        {
            try
            {
                await store.PruneAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "收件後的保存失敗（下一輪再做）");
            }
        }
        return receipt;
    }

    private async Task<Outcome> IngestAsync(FileStream requestFile, Work work, CancellationToken cancellationToken)
    {
        var settings = options.Value.Releases;

        // 1. request.json 超過上限不讀；內容以 UTF-8 讀（有 BOM 也收：Windows PowerShell 5.1 的 Set-Content -Encoding UTF8 會加）
        if (requestFile.Length > AdminInboxRules.MaxFileBytes)
            return Outcome.Reject(AppReleaseIngestCodes.InvalidRequest, $"request.json 有 {requestFile.Length} 位元組，超過 {AdminInboxRules.MaxFileBytes}");
        string json;
        using (var reader = new StreamReader(requestFile, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
            json = await reader.ReadToEndAsync(cancellationToken);

        // 2. 解析；連 id 都解析不出來時回執的 id 是空字串
        work.Id = AppReleaseIngestRules.ReadId(json);
        if (AppReleaseIngestRules.ParseRequest(json) is not { } request)
            return Outcome.Reject(AppReleaseIngestCodes.InvalidRequest, "request.json 不是 JSON 物件，或 id、release 不合法");
        work.Release = request.Release;

        // 3. 三個檔在不在、大小（超過上限就不讀內容）
        var names = ReleasePackageRules.FileNames(request.Release);
        var manifestFile = new FileInfo(Path.Combine(paths.AdminInboxAppReleaseDir, names.Manifest));
        var signatureFile = new FileInfo(Path.Combine(paths.AdminInboxAppReleaseDir, names.Signature));
        var zipFile = new FileInfo(Path.Combine(paths.AdminInboxAppReleaseDir, names.Zip));
        FileInfo[] sources = [manifestFile, signatureFile, zipFile];
        if (sources.FirstOrDefault(f => !f.Exists) is { } missing)
            return Outcome.Reject(AppReleaseIngestCodes.MissingFile, $"收件匣沒有 {missing.Name}");
        (FileInfo File, long Max)[] limits =
        [
            (manifestFile, ReleaseManifestRules.MaxManifestBytes),
            (signatureFile, ReleaseManifestRules.MaxSignatureBytes),
            (zipFile, (long)settings.MaxZipMb * 1024 * 1024),
        ];
        foreach (var (file, max) in limits)
        {
            if (AppReleaseIngestRules.CheckSize(file.Length, max) is { } tooLarge)
                return Outcome.Reject(tooLarge, $"{file.Name} 有 {file.Length} 位元組，上限 {max}");
        }

        // 4. 複製完至少還要剩 MinFreeDiskMb；量不到可用空間（探針回負數）不猜，回 io_error
        var available = disk.FreeBytes(paths.AppReleasesDir);
        if (available < 0)
            return Outcome.Reject(AppReleaseIngestCodes.IoError, $"查不到 {paths.AppReleasesDir} 所在磁碟的可用空間");
        var incoming = sources.Sum(f => f.Length);
        if (!AppReleaseIngestRules.HasRoom(available, incoming, (long)settings.MinFreeDiskMb * 1024 * 1024))
            return Outcome.Reject(AppReleaseIngestCodes.DiskFull, $"可用 {available} 位元組，複製 {incoming} 位元組之後會少於 {settings.MinFreeDiskMb} MB");

        // 5. 複製到 partial（已存在先刪）；之後只讀副本：驗的跟提交的是同一份。每個副本先落盤再往下走（提交與回執之前）
        work.Partial = store.PartialFolderOf(request.Id);
        try
        {
            if (Directory.Exists(work.Partial))
                Directory.Delete(work.Partial, recursive: true);
            Directory.CreateDirectory(work.Partial);
            foreach (var file in sources)
            {
                var copy = Path.Combine(work.Partial, file.Name);
                File.Copy(file.FullName, copy);
                FlushToDisk(copy);
            }
        }
        catch (IOException ex) when (AppReleaseIngestRules.IsLockedFile(ex.HResult))
        {
            return Outcome.Retry($"檔案被別的程序開著：{ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Outcome.Reject(AppReleaseIngestCodes.IoError, $"複製到 {work.Partial} 失敗：{ex.Message}");
        }

        // 6. 簽章在解析 manifest 之前驗（5.1）；信任清單只有內建金鑰，撤銷清單一律是空的（D1）
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(work.Partial, names.Manifest), cancellationToken);
        var signatureJson = await File.ReadAllTextAsync(Path.Combine(work.Partial, names.Signature), cancellationToken);
        var verified = ReleasePackageRules.VerifyManifest(manifestBytes, signatureJson, builtIn.Keys, []);
        if (!verified.Ok)
            return Outcome.Reject(verified.Step!, verified.Error ?? verified.Step!);
        var manifest = verified.Manifest!;
        var kid = verified.Signature!.Kid;

        // 7. 檔名是照 request 的 release 找的：manifest 寫的必須是同一個
        if (!string.Equals(manifest.Identity.Release, request.Release, StringComparison.Ordinal))
            return Outcome.Reject(AppReleaseIngestCodes.ReleaseMismatch, $"manifest 的 release 是 {manifest.Identity.Release}，request 寫的是 {request.Release}");

        // 8. 同名的版本永遠不再收（D14）；只有同一份 manifest 的可用版本算冪等的重送
        var manifestSha256 = ReleaseManifestRules.Sha256Hex(manifestBytes);
        if (await store.FindAsync(request.Release, cancellationToken) is { } existing)
        {
            return existing.State == AppReleaseStates.Available && string.Equals(existing.ManifestSha256, manifestSha256, StringComparison.OrdinalIgnoreCase)
                ? Outcome.Accept(AppReleaseIngestCodes.AlreadyIngested)
                : Outcome.Reject(AppReleaseIngestCodes.ReleaseExists, $"{request.Release} 已經收過（{existing.State}），同名的版本不再收");
        }
        if (!AppReleaseRules.IsNewerThanAvailable(manifest.Identity.Version, await store.ListAsync(cancellationToken)))
            return Outcome.Reject(AppReleaseIngestCodes.NotNewer, $"{request.Release} 不比所有可用版本新");

        // 9. 副本 zip 的整檔 SHA-256 與逐項比對
        await using (var zip = new FileStream(Path.Combine(work.Partial, names.Zip), FileMode.Open, FileAccess.Read, FileShare.Read,
            ZipBufferBytes, FileOptions.SequentialScan))
        {
            if (await ReleasePackageRules.VerifyZipAsync(manifest, zip, cancellationToken) is { } zipFailure)
                return Outcome.Reject(zipFailure.Step, zipFailure.Error);
        }

        // 10. 提交。之後不再取消：改名之後要嘛寫進資料表，要嘛（寫不進去回 internal_error，或當機）留下沒有資料列的版本資料夾，
        // 由下一次同名的收件（這裡先刪掉）或下次啟動的對帳刪掉；裝置與版本頁只看資料表，看不到它
        var final = store.FolderOf(request.Release);
        try
        {
            if (Directory.Exists(final))
            {
                log.LogWarning("版本資料夾 {Folder} 已存在但資料表沒有這一版（收件中途失敗或當機留下的），先刪掉", final);
                Directory.Delete(final, recursive: true);
            }
            Directory.Move(work.Partial, final);
        }
        catch (IOException ex) when (AppReleaseIngestRules.IsBusyFolder(ex.HResult))
        {
            // 防毒還在掃剛複製的副本（或留下的資料夾）：同第 5 步，下次輪詢再試（INGEST-2；原本落到未預期的錯誤回 internal_error）
            return Outcome.Retry($"版本資料夾改名時裡面有檔案被別的程序開著：{ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Outcome.Reject(AppReleaseIngestCodes.IoError, $"把 {work.Partial} 改名成 {final} 失敗：{ex.Message}");
        }
        work.Partial = null;
        var zipBytes = new FileInfo(Path.Combine(final, names.Zip)).Length;
        await store.AddAsync(new AppReleaseRecord
        {
            Release = request.Release,
            Version = manifest.Identity.Version.ToString(),
            Build = manifest.Identity.Build,
            Commit = manifest.Commit,
            ManifestSha256 = manifestSha256,
            ZipSha256 = manifest.ZipSha256,
            ZipBytes = zipBytes,
            ProtocolVersion = manifest.ProtocolVersion,
            MinRuntime = AppReleaseRules.MinRuntimeText(manifest.MinRuntime),
            Kid = kid,
            State = AppReleaseStates.Available,
            IngestedAt = time.GetUtcNow().ToUnixTimeMilliseconds(),
        }, CancellationToken.None);
        await audit.WriteAsync(new AuditEntry(AuditActors.System, null, null, AuditActions.AppReleaseIngested, request.Release, AuditOutcomes.Ok,
            new { release = request.Release, commit = manifest.Commit, kid, zipBytes, manifestSha256 }), CancellationToken.None);
        log.LogInformation("收件匣收進 {Release}（{Id}，kid {Kid}，zip {ZipBytes} 位元組）", request.Release, request.Id, kid, zipBytes);
        return Outcome.Accept(AppReleaseIngestCodes.Ingested);
    }

    /// <summary>
    /// 副本寫回磁碟（FlushFileBuffers，要寫入權）：File.Copy 只寫進系統快取，改名與資料表都提交了、資料還沒寫回時停電，
    /// zip 沒寫回的部分會讀成零而長度照舊（重新載入只比大小，照樣當最新版提供；重送又會得到 already_ingested）。
    /// 副本被別的程序（防毒）開著時丟共用違規，跟複製一樣下次輪詢再試
    /// </summary>
    private static void FlushToDisk(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }

    private FileStream? TryOpenRequest(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogInformation("request.json 暫時開不了，下次再試：{Error}", ex.Message);
            return null;
        }
    }

    /// <summary>以 StatusJson 寫回執：先寫 .tmp 再覆寫改名；目標檔被讀取者開著時每 200 ms 重試，最多 5 次。寫不出去只記錯誤</summary>
    private async Task WriteReceiptAsync(AppReleaseReceipt receipt)
    {
        var target = paths.AppReleaseAckFile;
        var temp = target + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(receipt, StatusJson.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "收件回執寫不出去：{Id} {Message}", receipt.Id, receipt.Message);
            return;
        }
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReceiptWriteAttempts)
                {
                    log.LogError(ex, "收件回執換不上去（試了 {Attempts} 次）：{Id} {Message}", attempt, receipt.Id, receipt.Message);
                    return;
                }
            }
            await Task.Delay(ReceiptRetryDelay);
        }
    }

    private void DeleteInboxFiles(string release)
    {
        var names = ReleasePackageRules.FileNames(release);
        foreach (var name in new[] { names.Zip, names.Manifest, names.Signature })
            TryDeleteFile(Path.Combine(paths.AdminInboxAppReleaseDir, name));
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("收件匣的 {Path} 刪不掉：{Error}", path, ex.Message);
        }
    }

    private void DeletePartial(Work work)
    {
        if (work.Partial is not { } partial)
            return;
        work.Partial = null;
        try
        {
            if (Directory.Exists(partial))
                Directory.Delete(partial, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("{Folder} 刪不掉（下次收件同一個 id 或啟動對帳時再刪）：{Error}", partial, ex.Message);
        }
    }

    /// <summary>這一次處理到哪裡：回執的 id 與 release、要清掉的 partial</summary>
    private sealed class Work
    {
        public string? Id { get; set; }

        public string? Release { get; set; }

        public string? Partial { get; set; }
    }

    /// <summary>處理結果：Code 是回執的 message（ASCII 代碼），Reason 是寫進服務記錄的中文原因</summary>
    private sealed record Outcome(bool Ok, bool RetryLater, string Code, string Reason)
    {
        public static Outcome Accept(string code) => new(true, false, code, code);

        public static Outcome Reject(string code, string reason) => new(false, false, code, reason);

        public static Outcome Retry(string reason) => new(false, true, "", reason);
    }
}
