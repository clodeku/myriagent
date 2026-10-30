using System.Text.Json;
using Myriagent.Update;
using MyriagentHub.Hosting;
using MyriagentHub.Ops;
using MyriagentHub.Trust;

namespace MyriagentHub.Admin;

/// <summary>admin 指令的結束碼（部署腳本依它判斷）</summary>
internal static class AdminExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    public const int ServiceRunning = 3;
    public const int Timeout = 4;
}

/// <summary>
/// <c>MyriagentHub.exe admin &lt;指令&gt; [--data &lt;資料根&gt;] [--wait &lt;秒&gt;] [--from &lt;備份資料夾&gt;]</c>：只能在 Hub 主機上以管理員執行
/// （8.3：沒有任何網路管理端點）。輸出一個 JSON 物件到標準輸出（非 ASCII 一律跳脫，經 ssh 的 cp950 主控台也不會亂碼）。
/// <list type="bullet">
/// <item>reset-owner（＝owner-reset）、drain、undrain、backup-now、selftest、cert-renew [--force]、verify-audit、stop：寫管理收件匣、等服務處理（預設 30 秒）。
/// cert-renew 一律重查 ARI，--force 不看續期時點直接續期。</item>
/// <item>app-releases：列出收進來的桌面版；app-release-withdraw &lt;release&gt;：撤回一個桌面版（release 是第二個參數，不合法時結束碼 2；
/// 不是暫時性指令，等不到服務處理時留在收件匣、服務啟動後處理）。同樣經管理收件匣。</item>
/// <item>app-devices：列出各電腦的 app 版本（版本頁的電腦表；開發機的桌面發佈腳本換簽章金鑰前以它確認）。同樣經管理收件匣。</item>
/// <item>restore --from &lt;資料夾&gt;：服務必須已停止，直接還原 hub.db。</item>
/// <item>preflight：檢查設定、對資料庫做唯讀快照並在副本上試跑遷移（不改線上檔案；服務執行中也可以跑；
/// 只需要 db、config 的讀取權與自己的 %TEMP%，部署時以 runner 帳戶執行）。</item>
/// <item>status：印出 status\running.json、selftest.json、trust.json，以及直接從金鑰檔算的伺服器身分指紋（與服務寫的不同就標出來）：
/// 裝置要本機確認伺服器身分時，以這裡印的為準（網頁不能當依據，sync-protocol 第 3 節）。</item>
/// </list>
/// 結束碼：0 成功、1 失敗、2 用法錯誤、3 服務仍在執行（restore）、4 等不到服務處理。
/// </summary>
internal static class AdminCommandLine
{
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?")
            return Print(output, AdminExitCodes.Usage, new { ok = false, usage = Usage() });

        var command = args[0].Trim().ToLowerInvariant();
        var dataDir = Option(args, "--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyriagentHub");
        var paths = new HubPaths(dataDir);
        try
        {
            switch (command)
            {
                case "restore":
                    return Restore(output, paths, Option(args, "--from"));
                case "preflight":
                    var preflight = await Preflight.RunAsync(paths, Path.GetTempPath(), CancellationToken.None);
                    return Print(output, preflight.Ok ? AdminExitCodes.Ok : AdminExitCodes.Failed, preflight);
                case "status":
                    return Status(output, paths);
            }
            if (AdminInboxRules.Canonical(command) is not { } canonical)
                return Print(output, AdminExitCodes.Usage, new { ok = false, error = "不認得的指令：" + command, usage = Usage() });
            var force = args.Skip(1).Any(a => string.Equals(a, "--force", StringComparison.OrdinalIgnoreCase));
            if (force && canonical != "cert-renew")
                return Print(output, AdminExitCodes.Usage, new { ok = false, error = "--force 只用在 cert-renew", usage = Usage() });
            string? release = null;
            if (canonical == "app-release-withdraw")
            {
                // release 是第二個位置參數（admin app-release-withdraw <release> [--data …]）；不合法時什麼都不寫
                release = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
                if (!ReleaseIdentity.TryParseRelease(release, out _))
                    return Print(output, AdminExitCodes.Usage, new { ok = false, error = "app-release-withdraw 要在第二個參數指定版本（例如 0.2.1523-ab12cd3）", usage = Usage() });
            }
            var waitSeconds = int.TryParse(Option(args, "--wait"), out var w) ? Math.Clamp(w, 0, 3600) : 30;
            return await SendToInboxAsync(output, paths, canonical, TimeSpan.FromSeconds(waitSeconds), force, release);
        }
        catch (Exception ex)
        {
            return Print(output, AdminExitCodes.Failed, new { ok = false, error = ex.GetType().Name + ": " + ex.Message });
        }
    }

    /// <summary>寫進收件匣（暫存檔＋改名，服務不會讀到寫一半的檔）並等結果</summary>
    public static async Task<int> SendToInboxAsync(TextWriter output, HubPaths paths, string command, TimeSpan wait, bool force = false, string? release = null)
    {
        Directory.CreateDirectory(paths.AdminInboxDir);
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var file = Path.Combine(paths.AdminInboxDir, AdminInboxRules.FileName(now, id));
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new AdminCommand(id, command, now.ToUnixTimeMilliseconds(), Force: force, Release: release), StatusJson.Options));
        File.Move(temp, file);

        var resultFile = Path.Combine(paths.AdminResultsDir, id + ".json");
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(resultFile))
            {
                try
                {
                    var result = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(resultFile));
                    var ok = result.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
                    return Print(output, ok ? AdminExitCodes.Ok : AdminExitCodes.Failed, result);
                }
                catch (IOException)
                {
                    // 服務正在寫：再等一下
                }
            }
            await Task.Delay(250);
        }
        // 暫時性指令（drain 等）不留到下次啟動才生效：還沒被拿走就收回
        var withdrawn = false;
        if (AdminInboxRules.IsTransient(command))
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    withdrawn = true;
                }
            }
            catch (IOException)
            {
                // 服務剛好在讀：交給服務處理
            }
        }
        return Print(output, AdminExitCodes.Timeout, new
        {
            ok = false,
            error = withdrawn
                ? "等不到服務處理（服務沒有在執行？），指令已收回、沒有執行"
                : "等不到服務處理（服務沒有在執行？）：指令留在收件匣，服務啟動後處理",
            command,
            id,
            withdrawn,
            inboxFile = withdrawn ? null : file,
        });
    }

    private static int Restore(TextWriter output, HubPaths paths, string? from)
    {
        if (string.IsNullOrWhiteSpace(from))
            return Print(output, AdminExitCodes.Usage, new { ok = false, error = "restore 需要 --from <備份資料夾>" });
        try
        {
            var result = RestoreService.Restore(paths, from, TimeProvider.System);
            return Print(output, AdminExitCodes.Ok, new { ok = true, result });
        }
        catch (RestoreException ex)
        {
            return Print(output, ex.Code == "service_running" ? AdminExitCodes.ServiceRunning : AdminExitCodes.Failed, new { ok = false, code = ex.Code, error = ex.Message });
        }
    }

    private static int Status(TextWriter output, HubPaths paths)
    {
        static JsonElement? Read(string path) => File.Exists(path) ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path)) : null;
        var trust = Read(paths.TrustStatusFile);
        string? hubIdentity;
        string? hubIdentityError = null;
        try
        {
            hubIdentity = HubIdentity.ReadFingerprint(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            hubIdentity = null;
            hubIdentityError = ex.GetType().Name + ": " + ex.Message;
        }
        string? Field(string name) => trust is { } t && t.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var reported = Field("hubIdentityFingerprint");
        return Print(output, AdminExitCodes.Ok, new
        {
            ok = true,
            hubIdentityFingerprint = hubIdentity,
            hubIdentityError,
            hubIdentityMismatch = hubIdentity is not null && reported is not null && reported != hubIdentity ? reported : null,
            running = Read(paths.RunningFile),
            selftest = Read(paths.SelfTestFile),
            trust,
        });
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Print(TextWriter output, int exitCode, object value)
    {
        output.WriteLine(JsonSerializer.Serialize(value, Output));
        return exitCode;
    }

    private static string[] Usage() =>
    [
        "MyriagentHub.exe admin reset-owner [--data <資料根>] [--wait <秒>]（擁有者 admin 的密碼改回預設、撤銷所有瀏覽器工作階段）",
        "MyriagentHub.exe admin drain|undrain|backup-now|selftest|verify-audit|stop [--data <資料根>] [--wait <秒>]",
        "MyriagentHub.exe admin cert-renew [--force] [--data <資料根>] [--wait <秒>]（清掉失敗退避、重查 ARI；--force 不看續期時點直接續期）",
        "MyriagentHub.exe admin rotate-hub-identity [--data <資料根>] [--wait <秒>]（輪換伺服器身分金鑰；裝置下次連線自動改釘）",
        "MyriagentHub.exe admin app-releases [--data <資料根>] [--wait <秒>]（列出收進來的桌面版：版本、build、提交、大小、收件時間、狀態、簽章 kid）",
        "MyriagentHub.exe admin app-devices [--data <資料根>] [--wait <秒>]（列出各電腦：裝置 id、名稱、app 版本、最後一次 Hello、在線、只更新；已撤銷的不列）",
        "MyriagentHub.exe admin app-release-withdraw <release> [--data <資料根>] [--wait <秒>]（撤回一個桌面版，例如 0.2.1523-ab12cd3：撤回的是目前提供的版本時，裝置改下載前一個可用的版本；不能復原，同名的版本也永遠不能再收；服務沒在跑時留在收件匣，啟動後處理）",
        "MyriagentHub.exe admin restore --from <備份資料夾> [--data <資料根>]（服務必須已停止）",
        "MyriagentHub.exe admin preflight [--data <資料根>]（檢查設定、對資料庫的唯讀快照試跑遷移；只需要 db、config 的讀取權與 %TEMP%）",
        "MyriagentHub.exe admin status [--data <資料根>]（含伺服器身分指紋：裝置要本機確認時與它比對）",
    ];
}
