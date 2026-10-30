using Myriagent.Media;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Media;

/// <summary>
/// 轉送得到的縮圖快取（WP4.7 計畫 D30；設計 4.10:1056）：<c>cache\thumbs\{key}</c>（抽屜的縮圖帶版本 <c>{key}@{v}</c>）。
/// 只寫完整、魔數正確（PNG／JPEG）、≤<see cref="MediaOptions.MaxThumbBytes"/> 的縮圖；先寫 <c>tmp\thumb-*</c> 再改名（同一個磁碟區）。
/// 合計超過 <see cref="MediaOptions.ThumbCacheBytes"/> 時刪最舊的（修改時間；讀取不寫，所以是依寫入的先後），磁碟可用空間低於下限時不寫。
/// 整個資料夾隨時可以刪掉（只是快取）
/// </summary>
internal sealed class ThumbCache(HubPaths paths, IOptions<HubOptions> options, IDiskSpaceProbe disk, ILogger<ThumbCache> log)
{
    /// <summary>暫存檔的前綴（在 <see cref="HubPaths.TmpDir"/>）</summary>
    public const string TmpPrefix = "thumb-";

    private const char VersionSeparator = '@';

    private readonly Lock gate = new();
    private long? bytes;

    /// <summary>快取的合計位元組（第一次用到時掃一次資料夾，之後隨寫入與刪除增減）</summary>
    public long Bytes
    {
        get
        {
            lock (gate)
                return bytes ??= Measure();
        }
    }

    /// <summary>快取檔的名稱：鍵要合格、版本照 <see cref="MediaServeRules.IsThumbVersion"/>；不合格回 null</summary>
    public static string? FileName(string key, string? version)
    {
        if (!MediaKeyRules.TryParse(key, out _, out _) || !MediaServeRules.IsThumbVersion(version))
            return null;
        return version is null ? key : key + VersionSeparator + version;
    }

    /// <summary>啟動時清掉上次留在 tmp 的 <c>thumb-*</c>（服務鎖保證沒有別的實例在寫）</summary>
    internal void Initialize()
    {
        foreach (var file in Directory.EnumerateFiles(paths.TmpDir, TmpPrefix + "*"))
            TryDelete(file);
    }

    /// <summary>快取裡的縮圖（不合格、太大、魔數不對都當成沒有）</summary>
    public byte[]? TryRead(string key, string? version)
    {
        if (FileName(key, version) is not { } name)
            return null;
        var path = Path.Combine(paths.ThumbCacheDir, name);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > options.Value.Media.MaxThumbBytes)
                return null;
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            return BlobInspectRules.ThumbnailType(data) is null ? null : data;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or EndOfStreamException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "縮圖快取讀不到：{Name}", name);
            return null;
        }
    }

    /// <summary>寫入（完整、魔數正確、≤上限才寫；磁碟可用空間低於下限時不寫）；回傳有沒有寫</summary>
    public async Task<bool> WriteAsync(string key, string? version, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var o = options.Value.Media;
        if (FileName(key, version) is not { } name || data.Length == 0 || data.Length > o.MaxThumbBytes || BlobInspectRules.ThumbnailType(data.Span) is null)
            return false;
        var free = disk.FreeBytes(paths.ThumbCacheDir);
        if (free < 0 || free - data.Length < o.MinFreeBytes)
        {
            log.LogWarning("磁碟可用空間低於下限，縮圖不寫進快取（可用 {Free}）", free);
            return false;
        }

        var target = Path.Combine(paths.ThumbCacheDir, name);
        var tmp = Path.Combine(paths.TmpDir, TmpPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                await file.WriteAsync(data, cancellationToken);
            lock (gate)
            {
                var current = bytes ?? Measure();
                var replaced = File.Exists(target) ? new FileInfo(target).Length : 0;
                File.Move(tmp, target, overwrite: true);
                bytes = current - replaced + data.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (ex is not OperationCanceledException)
                log.LogWarning(ex, "縮圖寫不進快取：{Name}", name);
            TryDelete(tmp);
            return false;
        }

        if (Bytes > o.ThumbCacheBytes)
            Trim(o.ThumbCacheBytes);
        return true;
    }

    /// <summary>刪掉這個鍵的所有快取（含各版本；對話刪除或降級時，步 5）；回傳刪了幾個</summary>
    public int Delete(string key)
    {
        if (!MediaKeyRules.TryParse(key, out _, out _))
            return 0;
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(paths.ThumbCacheDir, key + "*"))
        {
            // 檔名的萬用字元也比對 8.3 短名：再以完整名稱確認是這個鍵本身或它的版本
            var name = Path.GetFileName(file);
            if (!name.StartsWith(key, StringComparison.Ordinal) || (name.Length != key.Length && name[key.Length] != VersionSeparator))
                continue;
            if (DeleteCounted(file))
                deleted++;
        }

        return deleted;
    }

    /// <summary>合計超過 <paramref name="limitBytes"/> 時從最舊的（修改時間）開始刪，直到不超過；回傳刪了幾個</summary>
    public int Trim(long limitBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limitBytes);
        if (Bytes <= limitBytes)
            return 0;
        var deleted = 0;
        foreach (var file in new DirectoryInfo(paths.ThumbCacheDir).EnumerateFiles().OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            if (Bytes <= limitBytes)
                break;
            if (DeleteCounted(file.FullName))
                deleted++;
        }

        return deleted;
    }

    private bool DeleteCounted(string file)
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(file))
                    return false;
                var current = bytes ?? Measure();
                var length = new FileInfo(file).Length;
                File.Delete(file);
                bytes = current - length;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "縮圖快取刪不掉：{File}", file);
                return false;
            }
        }
    }

    private long Measure()
    {
        try
        {
            return new DirectoryInfo(paths.ThumbCacheDir).EnumerateFiles().Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "縮圖快取的大小量不到");
            return 0;
        }
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "縮圖暫存檔刪不掉：{File}", file);
        }
    }
}
