using System.Security.Cryptography;
using System.Text;

namespace MyriagentTools.ReleaseTool;

/// <summary>
/// 開發機的發佈私鑰檔（2026-10-25 起桌面發佈在開發機簽章、不經 GitHub Actions；手冊 docs/release.md 第 2、5 節）：
/// PKCS#8 DER 以 DPAPI（CurrentUser、固定的 entropy）加密後寫檔，只有產生它的 Windows 使用者在這台電腦解得開。
/// 檔案只由 keygen --dpapi-file 建立（已經存在就拒絕、不覆蓋），sign --key-file 只讀；解出的位元組用完就清掉，任何訊息都不含內容
/// </summary>
internal static class LocalKeyFile
{
    /// <summary>金鑰檔的大小上限（P-256 的 PKCS#8 加上 DPAPI 的外殼只有幾百位元組）：超過就不是 keygen 寫的，不讀</summary>
    public const int MaxFileBytes = 16 * 1024;

    /// <summary>DPAPI 的附加 entropy：同一個使用者別的 DPAPI 資料不能被當成這個檔解開</summary>
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("Myriagent desktop release key v1");

    /// <summary>預設位置 %LOCALAPPDATA%\MyriagentRelease\desktop-release-key.dpapi：不在 repo，也不在 app 的資料根目錄</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyriagentRelease", "desktop-release-key.dpapi");

    /// <summary>把私鑰加密寫進新檔；成功回 null。檔案已經存在一律拒絕（以 CreateNew 開檔，和別的程序同時寫也不會覆蓋）</summary>
    public static string? Write(string path, ECDsa key)
    {
        if (!OperatingSystem.IsWindows())
            return "DPAPI 只有 Windows 有：金鑰檔只能在 Windows 的開發機產生";
        var full = Path.GetFullPath(path);
        if (File.Exists(full))
            return $"金鑰檔已經存在，不覆蓋：{full}";
        var der = key.ExportPkcs8PrivateKey();
        byte[] blob;
        try
        {
            blob = ProtectedData.Protect(der, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        try
        {
            using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(blob);
        }
        catch (IOException) when (File.Exists(full))
        {
            return $"金鑰檔已經存在，不覆蓋：{full}";
        }
        return null;
    }

    /// <summary>解開金鑰檔，回 PKCS#8 DER（呼叫端用完要清掉）；失敗回固定的說明，不帶例外訊息也不帶檔案內容</summary>
    public static (byte[]? Der, string? Error) Read(string path)
    {
        if (!OperatingSystem.IsWindows())
            return (null, "DPAPI 只有 Windows 有：金鑰檔只能在產生它的 Windows 開發機使用");
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            return (null, $"找不到金鑰檔：{full}（這台電腦第一次發佈之前先 keygen --dpapi-file，手冊 docs/release.md 5.1）");
        if (new FileInfo(full).Length > MaxFileBytes)
            return (null, $"金鑰檔超過 {MaxFileBytes} 位元組，不是 keygen --dpapi-file 寫的");
        var blob = File.ReadAllBytes(full);
        try
        {
            return (ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser), null);
        }
        catch (CryptographicException)
        {
            return (null, "金鑰檔解不開：不是這台電腦的這個 Windows 使用者以 keygen --dpapi-file 產生的，或檔案損毀（內容不顯示）");
        }
    }
}
