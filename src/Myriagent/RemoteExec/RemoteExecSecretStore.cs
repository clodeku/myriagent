using System.Security.Cryptography;
using System.Text;
using Myriagent.Core;

namespace Myriagent.RemoteExec;

/// <summary>
/// app 換版時把對話權杖的金鑰交給新版（代理主機 H1，設計 docs/plans/2026-11-26-agent-host.md）：對話 CLI 的環境變數
/// <c>MYRIAGENT_REMOTE_TOKEN</c> 是 HMAC(app 這次執行的亂數, 對話 id)，app 重開就換一把——排空時放開、由新版接回的 CLI 程序
/// 手上還是舊的權杖，myriagent-remote、myriagent-schedule 會被新版拒絕。所以只有「換版而且留著程序」時，結束前把這把（與它之前收下的）
/// 以 DPAPI（只有這個 Windows 使用者解得開）存到 <c>&lt;資料根目錄&gt;\host\remote-keys.bin</c>；新版啟動時讀走並刪掉，
/// 只拿來驗證（發新的權杖一律用新的亂數）。一般的結束不存：舊的權杖照舊跟著 app 結束失效
/// </summary>
internal static class RemoteExecSecretStore
{
    /// <summary>最多帶幾把（每次「換版而且留著程序」多一把；跨這麼多次換版還活著的程序實務上沒有）</summary>
    public const int MaxSecrets = 8;

    private const int SecretLength = 32;

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Myriagent.RemoteExec.Handoff.v1");

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, "host", "remote-keys.bin");

    /// <summary>存（最新的在前，超過 <see cref="MaxSecrets"/> 的丟掉）；寫不了只記警告（接回的程序用不了 myriagent-remote，其他照常）</summary>
    public static void Save(string dataRoot, IReadOnlyList<byte[]> secrets)
    {
        try
        {
            var kept = secrets.Where(s => s is { Length: SecretLength }).Take(MaxSecrets).ToList();
            if (kept.Count == 0)
                return;
            var plain = new byte[kept.Count * SecretLength];
            for (int i = 0; i < kept.Count; i++)
                kept[i].CopyTo(plain, i * SecretLength);
            var path = PathFor(dataRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
            CryptographicOperations.ZeroMemory(plain);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Warn($"交棒對話權杖的金鑰失敗：{ex.Message}");
        }
    }

    /// <summary>讀走上一版留下的（讀完就刪：只給緊接著的這一次啟動）；沒有、解不開、格式不對都回空的</summary>
    public static IReadOnlyList<byte[]> Take(string dataRoot)
    {
        var path = PathFor(dataRoot);
        try
        {
            if (!File.Exists(path))
                return Array.Empty<byte[]>();
            var stored = File.ReadAllBytes(path);
            File.Delete(path);
            var plain = ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser);
            if (plain.Length == 0 || plain.Length % SecretLength != 0)
                return Array.Empty<byte[]>();
            var secrets = new List<byte[]>();
            for (int i = 0; i < plain.Length && secrets.Count < MaxSecrets; i += SecretLength)
                secrets.Add(plain.AsSpan(i, SecretLength).ToArray());
            CryptographicOperations.ZeroMemory(plain);
            return secrets;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Warn($"讀不到上一版交棒的對話權杖金鑰：{ex.Message}");
            return Array.Empty<byte[]>();
        }
    }
}
