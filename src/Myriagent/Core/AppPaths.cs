namespace Myriagent.Core;

/// <summary>
/// app 的本機資料位置（全部在 %LOCALAPPDATA%\Myriagent 底下）：
/// accounts.json、settings.json、accounts\{帳戶}\（隔離的 CLI 設定資料夾）、conversations\{對話}\、usage\{帳戶}.json、usage-tokens.json（側欄的 token 計數）、cache\、WebView2\、
/// sync\partial\（重開 CLI 前截掉的半行）、sync\device.json（配對的裝置身分、權杖與伺服器釘選）、sync\sync.db（同步的佇列、游標與快取）。
/// 測試與實機探針可用 MYRIAGENT_AGENT_DATA 環境變數或 Override 改到暫存資料夾。
/// </summary>
internal static class AppPaths
{
    /// <summary>任何執行緒都會讀（背景寫記錄檔）；測試的 HubHarness 建立與釋放時會改它</summary>
    private static volatile string? overrideRoot;

    /// <summary>探針與測試用：改用指定的根目錄</summary>
    public static void Override(string? root) => overrideRoot = root;

    public static string Root
    {
        get
        {
            // 只讀一次：檢查與回傳之間被改成 null（測試平行建立、釋放 HubHarness）時，回傳的會是 null，背景寫記錄的執行緒丟出未處理的例外、整個程序結束
            var root = overrideRoot;
            if (!string.IsNullOrWhiteSpace(root))
                return root;
            var env = Environment.GetEnvironmentVariable("MYRIAGENT_AGENT_DATA");
            return !string.IsNullOrWhiteSpace(env) ? env : DefaultRoot;
        }
    }

    /// <summary>沒有 --data、MYRIAGENT_AGENT_DATA 時的資料夾（單一實例的名稱以它為準：這個資料夾沒有後綴）</summary>
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Myriagent");

    public static string AccountsFile => Path.Combine(Root, "accounts.json");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string AccountsDirectory => Path.Combine(Root, "accounts");

    public static string ConversationsDirectory => Path.Combine(Root, "conversations");

    public static string UsageDirectory => Path.Combine(Root, "usage");

    /// <summary>各帳戶每天用掉的 token（TokenTallyStore）；不放在 usage\：那裡的 *.json 檔名就是帳戶 Id</summary>
    public static string TokenTallyFile => Path.Combine(Root, "usage-tokens.json");

    /// <summary>每週額度每 5% 的 token 分段紀錄（UsageSegmentStore：records.jsonl 一行一筆、open.json 進行中的各段）；同樣不放在 usage\</summary>
    public static string UsageSegmentsDirectory => Path.Combine(Root, "usage-segments");

    public static string CacheDirectory => Path.Combine(Root, "cache");

    public static string WebViewDataDirectory => Path.Combine(Root, "WebView2");

    /// <summary>app 自行下載安裝的 Codex（沒有 npm 時）</summary>
    public static string ManagedCodexDirectory => Path.Combine(Root, "codex");

    public static string LogDirectory => Path.Combine(Root, "logs");

    /// <summary>背景活動日誌（BackgroundJournalFile：yyyy-MM-dd.jsonl，保留 30 天；docs/background-journal.md）</summary>
    public static string BackgroundJournalDirectory => Path.Combine(LogDirectory, "background");

    /// <summary>重開 CLI 前從逐字稿／rollout 截掉的半行（TranscriptTailRules；保留 7 天）</summary>
    public static string PartialTailDirectory => Path.Combine(Root, "sync", "partial");

    /// <summary>多電腦同步的本機資料（device.json、sync.db、pending-key.json；同步引擎以 SyncPaths 注入，不直接讀這裡）</summary>
    public static string SyncDirectory => Path.Combine(Root, "sync");
}
