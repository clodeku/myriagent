using System.IO.Compression;
using System.Security.Cryptography;

namespace MyriagentTools.AcmeE2e;

/// <summary>固定版本的外部執行檔：GitHub release 資產＋大小＋SHA-256（GitHub 公布的 digest，研究時下載的副本也逐位元組相同）</summary>
internal sealed record PinnedAsset(string Name, Uri Url, long Size, string Sha256, string ExeEntry);

internal static class PinnedAssets
{
    public const string PebbleVersion = "v2.10.1";

    public static readonly PinnedAsset Pebble = new(
        "pebble-windows-amd64.zip",
        new Uri("https://github.com/letsencrypt/pebble/releases/download/v2.10.1/pebble-windows-amd64.zip"),
        3_331_375,
        "2539f3903302d75f897522b23b1b52b3300f5f992245f13d3bdf3967cf23af6d",
        "pebble-windows-amd64/windows/amd64/pebble.exe");

    public static readonly PinnedAsset ChallTestSrv = new(
        "pebble-challtestsrv-windows-amd64.zip",
        new Uri("https://github.com/letsencrypt/pebble/releases/download/v2.10.1/pebble-challtestsrv-windows-amd64.zip"),
        2_924_445,
        "f4621c6ce1ac025c4f49a7316ec3d16779df8c5b09276a2544f5a5198a95e32a",
        "pebble-challtestsrv-windows-amd64/windows/amd64/pebble-challtestsrv.exe");
}

/// <summary>驗證過的下載結果</summary>
internal sealed record CachedAsset(PinnedAsset Asset, string ZipPath, bool Downloaded);

/// <summary>解開後的執行檔</summary>
internal sealed record ExtractedExe(string Path, string Sha256, long Size);

/// <summary>
/// 下載與解壓：
/// <list type="bullet">
/// <item>快取在 repo 以外（預設 %LOCALAPPDATA%\myriagent-tools\downloads）；每次下載都放進自己新建的空資料夾，
/// 大小與 SHA-256 都對了才改名成 &lt;sha256&gt; 資料夾，不對就整個刪掉。</item>
/// <item>每次使用都重新驗快取裡壓縮檔的 SHA-256；被改過就刪掉重新下載。</item>
/// <item>每次執行解到工作資料夾底下新的空資料夾，不在快取裡就地執行。</item>
/// </list>
/// </summary>
internal static class Downloader
{
    public static async Task<CachedAsset> EnsureAsync(PinnedAsset asset, string cacheRoot, Action<string> log, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheRoot);
        var finalDir = Path.Combine(cacheRoot, asset.Sha256);
        var finalZip = Path.Combine(finalDir, asset.Name);
        if (File.Exists(finalZip))
        {
            var (size, sha) = await HashFileAsync(finalZip, cancellationToken);
            if (size == asset.Size && sha == asset.Sha256)
                return new CachedAsset(asset, finalZip, Downloaded: false);
            log($"快取的 {asset.Name} 雜湊不符（{sha}），刪掉重新下載");
            Directory.Delete(finalDir, recursive: true);
        }

        var tempDir = Path.Combine(cacheRoot, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var tempZip = Path.Combine(tempDir, asset.Name);
            log($"下載 {asset.Url}");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("myriagent-acme-e2e/1.0");
                using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = new FileStream(tempZip, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(target, cancellationToken);
            }
            var (size, sha) = await HashFileAsync(tempZip, cancellationToken);
            if (size != asset.Size || sha != asset.Sha256)
                throw new InvalidDataException($"{asset.Name} 驗證失敗：大小 {size}（預期 {asset.Size}）、SHA-256 {sha}（預期 {asset.Sha256}）");
            try
            {
                Directory.Move(tempDir, finalDir);
            }
            catch (IOException) when (File.Exists(finalZip))
            {
                // 另一個執行剛好也下載完：用它的（下次使用前一樣會重新驗雜湊）
            }
            return new CachedAsset(asset, finalZip, Downloaded: true);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>解到新的空資料夾（已存在就拒絕），回傳執行檔路徑與雜湊</summary>
    public static async Task<ExtractedExe> ExtractAsync(CachedAsset cached, string targetDir, CancellationToken cancellationToken)
    {
        if (Directory.Exists(targetDir) && Directory.EnumerateFileSystemEntries(targetDir).Any())
            throw new IOException("解壓目的地不是空資料夾：" + targetDir);
        Directory.CreateDirectory(targetDir);
        // 解壓前再驗一次（快取驗過之後到現在之間被換掉也擋得住）
        var (size, sha) = await HashFileAsync(cached.ZipPath, cancellationToken);
        if (size != cached.Asset.Size || sha != cached.Asset.Sha256)
            throw new InvalidDataException($"{cached.Asset.Name} 在解壓前驗證失敗：SHA-256 {sha}");
        // .NET 的 ExtractToDirectory 會拒絕跳出目的地的項目（../）
        ZipFile.ExtractToDirectory(cached.ZipPath, targetDir);
        var exe = Path.GetFullPath(Path.Combine(targetDir, cached.Asset.ExeEntry.Replace('/', Path.DirectorySeparatorChar)));
        if (!exe.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase) || !File.Exists(exe))
            throw new FileNotFoundException("壓縮檔裡找不到預期的執行檔：" + cached.Asset.ExeEntry);
        var (exeSize, exeSha) = await HashFileAsync(exe, cancellationToken);
        return new ExtractedExe(exe, exeSha, exeSize);
    }

    public static async Task<(long Size, string Sha256)> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return (stream.Length, Convert.ToHexStringLower(hash));
    }
}
