namespace MyriagentHub.Hosting;

/// <summary>
/// 資料根底下的資料夾配置（9.1：C:\ProgramData\MyriagentHub\）。服務帳戶對整個資料根有修改權，
/// 其他帳戶沒有任何權限（由部署工具設定 ACL）；程式只建立自己需要的資料夾、不動任何 ACL 以外的東西。
/// </summary>
internal sealed class HubPaths
{
    public HubPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string ConfigDir => Path.Combine(Root, "config");

    public string DbDir => Path.Combine(Root, "db");

    public string HubDb => Path.Combine(DbDir, "hub.db");

    /// <summary>同步資料（3.2 sync.db）：持久事件、帳戶目錄、用量；只有 SyncWriter 一條寫入連線</summary>
    public string SyncDb => Path.Combine(DbDir, "sync.db");

    public string SecretsDir => Path.Combine(Root, "secrets");

    public string DataProtectionKeysDir => Path.Combine(SecretsDir, "dp-keys");

    public string AcmeAccountKeyFile => Path.Combine(SecretsDir, "acme-account.dpapi");

    /// <summary>伺服器身分金鑰（ECDSA P-256，DPAPI LocalMachine；裝置配對時釘選它的公鑰，設計 8.2.4）</summary>
    public string HubIdentityKeyFile => Path.Combine(SecretsDir, "hub-identity.dpapi");

    public string StatusDir => Path.Combine(Root, "status");

    public string RunningFile => Path.Combine(StatusDir, "running.json");

    public string SelfTestFile => Path.Combine(StatusDir, "selftest.json");

    /// <summary>信任資訊（伺服器身分指紋、名冊版本）：服務啟動與信任資料變更時寫，admin status 印出供本機比對</summary>
    public string TrustStatusFile => Path.Combine(StatusDir, "trust.json");

    /// <summary>服務執行期間以獨佔方式開著：同一個資料根只會有一個伺服器實例，還原指令也靠它判斷服務是否已停止</summary>
    public string ServiceLockFile => Path.Combine(StatusDir, "service.lock");

    /// <summary>第 2 階段 passkey 首次設定的一次性設定碼資料夾：帳號密碼登入後不用，但不刪（部署回滾時上一版還要用），下一版的遷移才刪</summary>
    public string LegacySetupDir => Path.Combine(Root, "setup");

    /// <summary>管理收件匣：只有本機管理員能寫入（資料根的 ACL），所有管理指令都認</summary>
    public string AdminInboxDir => Path.Combine(Root, "admin-inbox");

    public string AdminResultsDir => Path.Combine(AdminInboxDir, "results");

    /// <summary>部署用的收件匣：runner 帳戶在 admin-inbox\ 底下只能寫這個與 app-release\（U2，Setup-Runner 授權）兩個子資料夾，服務在這裡只接受 drain（docs/hub-setup.md 第 11 節）</summary>
    public string AdminInboxDeployDir => Path.Combine(AdminInboxDir, "deploy");

    /// <summary>部署 drain 的回覆：處理完 admin-inbox\deploy\ 的 drain 後寫入 {"id":…}，安裝腳本等這個 id（runner 對 status\ 只有讀取權）</summary>
    public string DrainAckFile => Path.Combine(StatusDir, "drain.json");

    /// <summary>桌面版的發佈收件匣（U2）：runner 送件寫三個檔，最後原子寫 request.json；服務在這裡只認發佈，其他的檔一律不看</summary>
    public string AdminInboxAppReleaseDir => Path.Combine(AdminInboxDir, "app-release");

    /// <summary>收進來的桌面版：每版一個資料夾 &lt;release&gt;\（zip、manifest 原文、簽章檔），收件途中的副本在 .partial-&lt;id&gt;\；每日備份不含</summary>
    public string AppReleasesDir => Path.Combine(Root, "app-releases");

    /// <summary>發佈收件的回執 {"id","ok","release","message","processedAt"}：送件腳本等這個 id（runner 對 status\ 只有讀取權）</summary>
    public string AppReleaseAckFile => Path.Combine(StatusDir, "app-release.json");

    public string BackupsDir => Path.Combine(Root, "backups");

    public string DailyBackupsDir => Path.Combine(BackupsDir, "daily");

    public string DiagDir => Path.Combine(Root, "diag");

    public string TmpDir => Path.Combine(Root, "tmp");

    /// <summary>內容定址的 blob（WP4.7 D16）：<c>blobs\{sha 前 2 碼}\{sha}</c>；和 <see cref="TmpDir"/> 同一個磁碟區（資料根，C:），才能原子改名</summary>
    public string BlobsDir => Path.Combine(Root, "blobs");

    /// <summary>媒體轉送得到的縮圖快取（WP4.7 D30，LRU，可以整個刪掉）</summary>
    public string ThumbCacheDir => Path.Combine(Root, "cache", "thumbs");

    /// <summary>
    /// 伺服器執行時需要的資料夾（不存在就建立；已存在不做任何事；只建資料夾，不碰 ACL）。blob 與上傳收到一半的暫存檔直接放 <see cref="TmpDir"/>（<c>blob-*</c>），
    /// 不另開子資料夾
    /// </summary>
    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, ConfigDir, DbDir, SecretsDir, DataProtectionKeysDir, StatusDir, AdminInboxDir, AdminResultsDir, AdminInboxDeployDir, AdminInboxAppReleaseDir, AppReleasesDir, BackupsDir, DailyBackupsDir, DiagDir, TmpDir, BlobsDir, ThumbCacheDir })
            Directory.CreateDirectory(dir);
    }
}
