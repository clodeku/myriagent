using System.Buffers.Text;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Security;
using Myriagent.Update;

namespace MyriagentTools.ReleaseTool;

/// <summary>要執行的 gh：正式是 PATH 上的 gh；selftest 換成把 stdin 與參數記下來的假 gh</summary>
internal sealed record GhCommand(string FileName, IReadOnlyList<string> PrefixArguments)
{
    public static GhCommand Default { get; } = new("gh", []);
}

/// <summary>
/// 產生發佈金鑰（規格 6.4）：ECDSA P-256。私鑰只去一個地方：「gh secret set &lt;名稱&gt; --repo &lt;repo&gt;」的 stdin（PKCS#8 的 base64，
/// GitHub Actions 簽章用），或 --dpapi-file 的金鑰檔（DPAPI 加密，開發機簽章用，2026-10-25 起；<see cref="LocalKeyFile"/>）；
/// 不印出，用過的位元組立即清掉。標準輸出只有公開的一行 new ReleaseKey("kid", "spki")：貼進 ReleaseKeys.Production（U2 設計 D1）。
/// gh 自己的訊息一律轉到標準錯誤
/// </summary>
internal static partial class KeyGen
{
    public static string PublicLine(string kid, string spki) => $"new ReleaseKey(\"{kid}\", \"{spki}\")";

    public static async Task<int> RunAsync(string? secretName, string? repo, string? dpapiFile, bool dryRun, GhCommand gh, TextWriter stdout, TextWriter stderr,
        CancellationToken ct)
    {
        if (dryRun)
        {
            if (secretName != null || repo != null || dpapiFile != null)
                throw new UsageException("--dry-run 不能和 --set-secret、--repo、--dpapi-file 一起用");
        }
        else if (dpapiFile != null)
        {
            if (secretName != null || repo != null)
                throw new UsageException("--dpapi-file 不能和 --set-secret、--repo 一起用（私鑰只去一個地方）");
        }
        else
        {
            if (secretName == null || repo == null)
                throw new UsageException("要給 --set-secret <名稱> 與 --repo <owner/repo>，或只給 --dry-run");
            if (!SecretName().IsMatch(secretName) || secretName.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase))
                throw new UsageException($"secret 名稱不合 GitHub 的規則：{secretName}");
            if (!RepoName().IsMatch(repo))
                throw new UsageException($"--repo 要寫成 owner/repo：{repo}");
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = P256KeyRules.ExportSpki(key);
        var kid = ReleaseManifestRules.ComputeKid(spki);
        if (dryRun)
        {
            stderr.WriteLine("--dry-run：沒有呼叫 gh，私鑰已丟棄（這一行只供檢查格式，不要提交）");
        }
        else if (dpapiFile != null)
        {
            if (LocalKeyFile.Write(dpapiFile, key) is { } error)
            {
                stderr.WriteLine($"失敗：{error}");
                return ToolExit.Failed;
            }
            stderr.WriteLine($"私鑰已以 DPAPI（這台電腦的這個 Windows 使用者）加密寫進 {Path.GetFullPath(dpapiFile)}（沒有顯示）");
        }
        else
        {
            if (await SetSecretAsync(gh, key, secretName!, repo!, stderr, ct) is { } error)
            {
                stderr.WriteLine($"失敗：{error}");
                return ToolExit.Failed;
            }
            stderr.WriteLine($"私鑰已經只經 stdin 交給 gh secret set {secretName} --repo {repo}（沒有寫檔、沒有顯示）");
        }
        stdout.WriteLine(PublicLine(kid, spki));
        return ToolExit.Ok;
    }

    /// <summary>以 stdin 把 PKCS#8 base64 交給 gh secret set；成功回 null。base64 直接編碼成位元組寫進管線，不經字串</summary>
    private static async Task<string?> SetSecretAsync(GhCommand gh, ECDsa key, string secretName, string repo, TextWriter stderr, CancellationToken ct)
    {
        var info = new ProcessStartInfo(gh.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in gh.PrefixArguments.Concat(["secret", "set", secretName, "--repo", repo]))
            info.ArgumentList.Add(arg);
        info.Environment["GH_PROMPT_DISABLED"] = "1";   // 沒登入時直接失敗，不等互動輸入
        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Win32Exception ex)
        {
            return $"無法啟動 gh（{ex.Message}）；請先安裝 GitHub CLI 並 gh auth login";
        }
        if (process == null)
            return "無法啟動 gh";
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            string? writeError = null;
            var der = key.ExportPkcs8PrivateKey();
            var base64 = new byte[(der.Length + 2) / 3 * 4];
            try
            {
                Base64.EncodeToUtf8(der, base64, out _, out var written);
                var stdin = process.StandardInput.BaseStream;
                await stdin.WriteAsync(base64.AsMemory(0, written), ct);
                await stdin.FlushAsync(ct);
            }
            catch (IOException)
            {
                writeError = "gh 在讀完私鑰之前就結束了；請重跑（會產生新的金鑰並覆蓋 secret）";
            }
            finally
            {
                CryptographicOperations.ZeroMemory(der);
                CryptographicOperations.ZeroMemory(base64);
                try
                {
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // gh 已經結束：管線關不關都一樣
                }
            }
            await process.WaitForExitAsync(ct);
            // gh 不會回顯 secret 的內容；它的訊息轉到標準錯誤，標準輸出只留給公開的那一行
            stderr.Write(await output);
            stderr.Write(await errors);
            if (process.ExitCode != 0)
                return $"gh secret set 失敗（結束碼 {process.ExitCode}）；這把私鑰已丟棄，secret 沒有改變，請重跑";
            return writeError;
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,99}$")]
    private static partial Regex SecretName();

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoName();
}
