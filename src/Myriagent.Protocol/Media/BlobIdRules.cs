using Myriagent.Sync;

namespace Myriagent.Media;

/// <summary>
/// blob 的 id（設計 3.5:668）：內容的 SHA-256（64 個小寫十六進位），同 <see cref="PayloadRules.IsSha256Hex"/>。
/// 伺服器存在 <c>blobs\{前 2 碼}\{sha}</c>，一個資料夾最多 256 個子資料夾
/// </summary>
internal static class BlobIdRules
{
    public static bool IsBlobId(string? s) => PayloadRules.IsSha256Hex(s);

    /// <summary>相對於 blob 根目錄的路徑 <c>{前 2 碼}\{sha}</c>；不是 blob id 丟 <see cref="ArgumentException"/>（不讓外部輸入組出路徑）</summary>
    public static string RelativePath(string sha256)
    {
        if (!IsBlobId(sha256))
            throw new ArgumentException("不是 blob id（64 個小寫十六進位）", nameof(sha256));
        return Path.Combine(sha256[..2], sha256);
    }
}
