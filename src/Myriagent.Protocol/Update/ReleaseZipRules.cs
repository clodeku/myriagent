using System.IO.Compression;
using System.Security.Cryptography;

namespace Myriagent.Update;

/// <summary>
/// 發佈 zip 的逐項驗證（設計 6.2；伺服器收件與發佈工具共用。桌面的 AppUpdateManager.ExtractVerified 是邊解壓邊驗的另一份實作，
/// 兩份沒有合併〔規格第 16 節第 58 項〕，判斷由 ReleasePackageRulesTests 比對一致）：
/// 反斜線換成斜線；以斜線結尾的資料夾項目略過；路徑要通過 IsValidPath 而且正好在清單裡（序數比較）；同一個路徑只能出現一次；
/// 先比項目宣告的長度，再串流算 SHA-256，讀到超過清單大小就中止（宣告長度造假也擋得住）；最後清單要不多不少（CheckFileSet）。
/// 只驗證，不寫磁碟
/// </summary>
internal static class ReleaseZipRules
{
    /// <summary>串流讀取的緩衝區（同桌面的 1 &lt;&lt; 16）：宣告長度造假的項目，最多比清單大小多讀這麼多就中止</summary>
    public const int BufferBytes = 64 * 1024;

    /// <summary>
    /// 通過回 null，否則回第一個問題（中文）。zip 格式壞掉時丟 <see cref="InvalidDataException"/>（同桌面；
    /// <see cref="ReleasePackageRules.VerifyZipAsync"/> 把它當成內容錯誤）
    /// </summary>
    public static string? VerifyEntries(ZipArchive archive, ReleaseManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(manifest);
        var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var actual = new List<(string Path, long Size)>(manifest.Files.Count);
        var buffer = new byte[BufferBytes];
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = entry.FullName.Replace('\\', '/');
            if (path.EndsWith('/'))
                continue;   // 資料夾項目
            if (!ReleaseManifestRules.IsValidPath(path) || !expected.TryGetValue(path, out var file))
                return $"zip 裡有清單以外的檔案：{path}";
            if (!seen.Add(path))
                return $"zip 裡的檔案重複：{path}";
            if (entry.Length > file.Size)
                return $"檔案比清單大：{path}";   // 宣告的長度就超過：不必解壓
            long read = 0;
            using (var input = entry.Open())
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    read += n;
                    if (read > file.Size)
                        return $"檔案比清單大：{path}";   // 宣告的長度是假的：實際內容讀到超過清單就停，不再往下解壓
                    hash.AppendData(buffer, 0, n);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                    return $"檔案雜湊不符：{path}";
            }
            actual.Add((path, read));
        }
        return ReleaseManifestRules.CheckFileSet(manifest.Files, actual);
    }
}
