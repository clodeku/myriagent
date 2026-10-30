using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Sync;
using Myriagent.Update;

namespace MyriagentTools.ReleaseTool;

/// <summary>打包的結果（輸出值交給 workflow 的下一個步驟）</summary>
internal sealed record PackageOutput(string ZipPath, string ManifestPath, string ZipSha256, string ManifestSha256, int FileCount, long TotalBytes);

/// <summary>
/// 打包與整包驗證（規格 6.2、6.3）。manifest 一律由 ReleaseManifestRules.Build 產生，之後只以原文位元組傳遞與驗證，任何一段都不重新序列化；
/// 發佈政策、zip 逐項比對與簽章驗證都用 ReleasePackageRules（伺服器收件與 U3 的桌面同一份）
/// </summary>
internal static class Packager
{
    public const string CreatedAtFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'";

    public static bool TryParseCreatedAt(string text, out DateTimeOffset createdAt)
        => DateTimeOffset.TryParseExact(text, CreatedAtFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out createdAt);

    /// <summary>
    /// 發佈資料夾 → &lt;out&gt;\myriagent-&lt;release&gt;.zip 與 .manifest.json。檔案依相對路徑的序數排序、斜線分隔，zip 項目同順序；
    /// 每個檔只讀一次，邊寫進 zip 邊算 SHA-256（清單與 zip 的內容一定一致）。最後以 VerifyZipAsync 自我檢查；失敗時不留下半成品。
    /// minRuntime 讀發佈資料夾的 Myriagent.runtimeconfig.json（ReleaseRuntimeRules，PIPE-1；原本寫死 10.0.0），讀不到或不是 framework-dependent 就拒絕。
    /// 同名的舊輸出（含舊的 .manifest.sig）一律先刪
    /// </summary>
    public static async Task<(PackageOutput? Output, string? Error)> PackAsync(string publishDir, string release, string commit, string outDir,
        DateTimeOffset createdAt, CancellationToken ct)
    {
        if (!ReleaseIdentity.TryParseRelease(release, out var identity))
            return (null, $"release 格式不對：{release}");
        if (!Versioning.IsCommit(commit) || !commit.StartsWith(identity!.Build, StringComparison.Ordinal))
            return (null, "commit 必須是 40 個小寫十六進位字，而且以 release 的 build 開頭");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(publishDir));
        if (!Directory.Exists(root))
            return (null, $"找不到發佈資料夾：{root}");
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir));
        if (string.Equals(output, root, StringComparison.OrdinalIgnoreCase)
            || output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return (null, "輸出資料夾不能是發佈資料夾，也不能在它裡面");

        // 相對路徑一律以 Path.GetRelativePath 對同一個 root 取，不靠截字串（GitHub 的 windows 機器 TEMP 是 8.3 短路徑）
        var sources = new List<(string Path, string Full)>();
        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (!ReleaseManifestRules.IsValidPath(path))
                return (null, $"檔名不符合套件的路徑規則：{path}");
            sources.Add((path, full));
        }
        if (!sources.Any(s => s.Path == ReleasePackageRules.EntryExecutable))
            return (null, $"發佈資料夾沒有 {ReleasePackageRules.EntryExecutable}：{root}");
        sources.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var duplicate = sources.GroupBy(s => s.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            return (null, $"不分大小寫重複的路徑：{duplicate.Key}");
        var runtimeConfig = new FileInfo(Path.Combine(root, ReleaseRuntimeRules.RuntimeConfigFileName));
        if (!runtimeConfig.Exists)
            return (null, $"發佈資料夾沒有 {ReleaseRuntimeRules.RuntimeConfigFileName}（minRuntime 由它決定）：{root}");
        if (runtimeConfig.Length > ReleaseRuntimeRules.MaxRuntimeConfigBytes)
            return (null, $"{ReleaseRuntimeRules.RuntimeConfigFileName} 超過 {ReleaseRuntimeRules.MaxRuntimeConfigBytes} 位元組");
        if (!ReleaseRuntimeRules.TryReadMinRuntime(await File.ReadAllTextAsync(runtimeConfig.FullName, ct), out var minRuntime, out var runtimeError))
            return (null, $"讀不出 minRuntime：{runtimeError}");

        Directory.CreateDirectory(output);
        var names = ReleasePackageRules.FileNames(release);
        var zipPath = Path.Combine(output, names.Zip);
        var manifestPath = Path.Combine(output, names.Manifest);
        File.Delete(zipPath);
        File.Delete(manifestPath);
        File.Delete(Path.Combine(output, names.Signature));
        var done = false;
        try
        {
            await using var zipStream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var files = new List<ReleaseFile>(sources.Count);
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, full) in sources)
                {
                    ct.ThrowIfCancellationRequested();
                    files.Add(AddEntry(archive, path, full, createdAt));
                }
            }
            zipStream.Position = 0;
            var zipSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(zipStream, ct));
            byte[] manifestBytes;
            try
            {
                manifestBytes = ReleaseManifestRules.Build(identity, commit, createdAt, ReleasePackageRules.EntryExecutable, minRuntime,
                    SyncProtocol.Version, zipSha256, files);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return (null, $"產生 manifest 失敗：{ex.Message}");
            }
            if (!ReleaseManifestRules.TryParse(manifestBytes, out var manifest, out var parseError))
                return (null, $"產生的 manifest 讀不回來：{parseError}");
            if (ReleasePackageRules.CheckPublishPolicy(manifest) is { } policyError)
                return (null, $"不符合發佈政策：{policyError}");
            zipStream.Position = 0;
            if (await ReleasePackageRules.VerifyZipAsync(manifest, zipStream, ct) is { } zipFailure)
                return (null, $"zip 自我檢查失敗（{zipFailure.Step}）：{zipFailure.Error}");
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, ct);
            done = true;
            return (new PackageOutput(zipPath, manifestPath, zipSha256, ReleaseManifestRules.Sha256Hex(manifestBytes), manifest.Files.Count, manifest.TotalBytes), null);
        }
        finally
        {
            if (!done)
            {
                File.Delete(zipPath);
                File.Delete(manifestPath);
            }
        }
    }

    /// <summary>
    /// 驗證一個資料夾裡的三個檔（verify 指令；selftest 以暫時金鑰呼叫同一個函式）：讀檔之前先比大小，
    /// 再交給 ReleasePackageRules.VerifyAsync，最後確認 manifest 的 release 就是檔名的 release。
    /// 只認傳入的清單（verify 傳 ReleaseKeys.Production），撤銷清單一律是空的（U2 設計 D1：工具與伺服器都沒有撤銷清單）。
    /// Error 是讀檔前就擋下的原因；Result 是 VerifyAsync 的結果（Step 是被哪一步擋下）
    /// </summary>
    public static async Task<(PackageVerification? Result, string? Error)> VerifyDirectoryAsync(string dir, string release,
        IEnumerable<ReleaseKey> trusted, CancellationToken ct)
    {
        if (!ReleaseIdentity.TryParseRelease(release, out _))
            return (null, $"release 格式不對：{release}");
        var names = ReleasePackageRules.FileNames(release);
        var root = Path.GetFullPath(dir);
        var zipPath = Path.Combine(root, names.Zip);
        var manifestPath = Path.Combine(root, names.Manifest);
        var sigPath = Path.Combine(root, names.Signature);
        foreach (var path in new[] { zipPath, manifestPath, sigPath })
        {
            if (!File.Exists(path))
                return (null, $"找不到 {Path.GetFileName(path)}");
        }
        if (new FileInfo(manifestPath).Length > ReleaseManifestRules.MaxManifestBytes)
            return (null, $"{names.Manifest} 超過 {ReleaseManifestRules.MaxManifestBytes} 位元組");
        if (new FileInfo(sigPath).Length > ReleaseManifestRules.MaxSignatureBytes)
            return (null, $"{names.Signature} 超過 {ReleaseManifestRules.MaxSignatureBytes} 位元組");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct);
        string signatureJson;
        try
        {
            signatureJson = new UTF8Encoding(false, true).GetString(await File.ReadAllBytesAsync(sigPath, ct));
        }
        catch (DecoderFallbackException)
        {
            return (null, $"{names.Signature} 不是 UTF-8");
        }
        await using var zip = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        var result = await ReleasePackageRules.VerifyAsync(manifestBytes, signatureJson, zip, trusted, Array.Empty<string>(), ct);
        if (result.Ok && result.Manifest!.Identity.Release != release)
            return (result, $"manifest 的 release 是 {result.Manifest.Identity.Release}，不是檔名的 {release}");
        return (result, null);
    }

    private static ReleaseFile AddEntry(ZipArchive archive, string path, string full, DateTimeOffset createdAt)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = createdAt;   // 項目時間一律是 createdAt：同樣的輸入與 createdAt 得到同一個 zip
        using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var entryStream = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        long size = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            entryStream.Write(buffer, 0, read);
            size += read;
        }
        return new ReleaseFile(path, size, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}
