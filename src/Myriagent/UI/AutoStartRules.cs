namespace Myriagent.UI;

/// <summary>
/// 開機時自動啟動（設定頁「一般 › 系統」）：登入 Windows 時由目前使用者的 Run 機碼執行。
/// 狀態以登錄為準、不存在 settings.json：工作管理員的「啟動應用程式」也能停用它（StartupApproved\Run），兩邊看到的要一致
/// </summary>
internal static class AutoStartRules
{
    /// <summary>Run 與 StartupApproved\Run 底下的值名稱</summary>
    public const string ValueName = "Myriagent";

    /// <summary>開機啟動時帶的參數（StartupOptions.AutoStart）：主視窗要不要藏著見 StartHidden</summary>
    public const string Argument = "--autostart";

    /// <summary>
    /// 這次執行能不能設定：只有從受管理的安裝位置、預設資料夾執行時。登錄指向 current\Myriagent.exe（換版只改 junction，登錄不用跟著改）；
    /// 開發版的 bin、--data 的探針、端到端測試都不寫使用者的登錄
    /// </summary>
    public static bool IsAvailable(bool managed, bool usesDefaultData, bool e2e) => managed && usesDefaultData && !e2e;

    /// <summary>Run 機碼的值：執行檔路徑加引號（路徑可能有空白）＋開機啟動的參數</summary>
    public static string Command(string executable) => $"\"{executable}\" {Argument}";

    /// <summary>
    /// 工作管理員「啟動應用程式」的開關（StartupApproved\Run 的二進位值）：第一個位元組是奇數（03）＝停用、偶數（02）＝啟用；
    /// 沒有值（從沒在工作管理員動過）或空值當成啟用
    /// </summary>
    public static bool IsApproved(byte[]? approved) => approved is not { Length: > 0 } || (approved[0] & 1) == 0;

    /// <summary>真的會在登入時啟動：Run 有值，而且沒有在工作管理員被停用</summary>
    public static bool IsEnabled(string? runCommand, byte[]? approved) => !string.IsNullOrWhiteSpace(runCommand) && IsApproved(approved);

    /// <summary>
    /// 開機啟動時主視窗藏著、只留系統匣圖示：只在「關閉視窗時縮到系統匣」開著時。關掉的人把視窗當成 app 本身，
    /// 藏起來會以為沒有啟動
    /// </summary>
    public static bool StartHidden(bool autoStart, bool closeToTray) => autoStart && closeToTray;

    /// <summary>開機自動啟動後鎖定電腦（設定 AutoStartLock）：只在這次是 --autostart 啟動時（換版重開、手動開都不鎖）</summary>
    public static bool LockAfterStart(bool autoStart, bool autoStartLock) => autoStart && autoStartLock;

    /// <summary>鎖定之前等多久：讓主視窗、系統匣圖示與同步先起來（鎖定之後 app 照常在背景執行）</summary>
    public static readonly TimeSpan LockDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Windows 自動登入（HKLM Winlogon 的 AutoAdminLogon 是 "1"）：沒開時重新開機（含 Windows 更新自動重開）之後停在登入畫面，
    /// 要有人登入 app 才會啟動——設定頁在說明裡提醒。讀不到是 null（不提醒）
    /// </summary>
    public static bool? AutoLogon(string? autoAdminLogon) => autoAdminLogon == null ? null : autoAdminLogon.Trim() == "1";
}
