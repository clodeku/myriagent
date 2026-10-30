using Myriagent.Agents;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Myriagent.Core;

/// <summary>CLI 換版後，還在跑舊版程序的對話什麼時候改用新版（設定頁「CLI 更新後」；只能在這台電腦改）</summary>
[JsonConverter(typeof(StringEnumConverter))]
internal enum CliRestartPolicy
{
    /// <summary>在安全點立即切換並自動接續（預設）：閒置的立刻關程序；執行中的等到沒有工具在執行、沒有等你回應的時刻，以新版重開並接續</summary>
    SafePoint,

    /// <summary>等這一輪結束再切換：這一輪結束時關掉舊程序，下一則訊息自然用新版</summary>
    AfterTurn,

    /// <summary>下次送出時才用新版：不主動關任何程序</summary>
    NextSend,
}

/// <summary>
/// 讀到不認得的列舉值（手改 settings.json 打錯字、新版存的值給舊版讀、沒有定義的數字）就保留原本的值（屬性的預設值）：
/// 不能讓整份設定被當成損毀而重設
/// </summary>
internal sealed class TolerantStringEnumConverter : StringEnumConverter
{
    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var enumType = Nullable.GetUnderlyingType(objectType) ?? objectType;
        object? Fallback() => existingValue ?? (enumType == objectType ? Activator.CreateInstance(objectType) : null);
        try
        {
            var value = base.ReadJson(reader, objectType, existingValue, serializer);
            return value == null || Enum.IsDefined(enumType, value) ? value : Fallback();
        }
        catch (JsonSerializationException)
        {
            if (reader.TokenType is JsonToken.StartObject or JsonToken.StartArray)
                reader.Skip();
            return Fallback();
        }
    }
}

/// <summary>帳戶的對話預設值：這個帳戶上次在對話裡選的模型（設定頁的預設模型是「沿用上次選擇」時新對話用它；推理強度與權限模式改由 ProviderConversationDefaults 決定）</summary>
internal sealed class AccountDefaults
{
    public string? Model { get; set; }

    /// <summary>舊版記住的「上次選的權限模式」：只供升級遷移讀取（ConversationDefaultsRules.Migrate），遷移後清成 null、不再寫回</summary>
    [JsonConverter(typeof(TolerantStringEnumConverter))]
    public AgentPermissionMode? PermissionMode { get; set; }
}

/// <summary>一家代理的對話預設值（設定頁「模型設定」）：新對話一律套用，對話內修改只影響那個對話</summary>
internal sealed class ProviderConversationDefaults
{
    [JsonConverter(typeof(TolerantStringEnumConverter))]
    public AgentPermissionMode PermissionMode { get; set; } = AgentPermissionMode.Ask;

    /// <summary>新對話的模型（CLI 回報的 value；"default"＝帳戶預設模型）；null＝沿用上次選擇（每個帳戶上次選的，AccountDefaults.Model）</summary>
    public string? Model { get; set; }

    /// <summary>模型（CLI 回報的 value；"default"＝帳戶預設模型）→ 預設推理強度；沒有列的模型用 CLI 預設</summary>
    public Dictionary<string, string> Efforts { get; set; } = new();
}

/// <summary>視窗位置與大小（下次開啟還原）</summary>
internal sealed class WindowPlacementInfo
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Maximized { get; set; }
}

/// <summary>主視窗以外另外開的視窗（像 Chrome 的多個視窗）：位置、結束時是否最小化、各自的分頁版面</summary>
internal sealed class AppWindowState
{
    /// <summary>視窗的鍵（AgentHub 以 IWindowView.WindowKey 對應版面）</summary>
    public string Key { get; set; } = string.Empty;

    public WindowPlacementInfo? Placement { get; set; }

    /// <summary>app 結束時這個視窗是最小化的：下次啟動照樣最小化（使用者不在時換版重開不把它叫到面前）</summary>
    public bool Minimized { get; set; }

    /// <summary>網頁的分頁與窗格版面（同 AppSettings.Layout，網頁擁有、宿主只保存）</summary>
    public JObject? Layout { get; set; }
}

/// <summary>app 設定（settings.json）</summary>
/// <summary>
/// 多電腦同步的本機設定（settings.json 的 Sync 區；只用 BCL 型別：未配對時反序列化也不載入同步的組件）。
/// 遠端操作政策只能在這台改（web-protocol 的 syncSetPolicy）；第 3 階段只回報給伺服器顯示
/// </summary>
internal sealed class SyncSettings
{
    public const string LevelOff = "Off";
    public const string LevelView = "View";
    public const string LevelControl = "Control";

    /// <summary>暫停同步（不連線，outbox 照常累積）</summary>
    public bool Paused { get; set; }

    /// <summary>暫停遠端操作（系統匣）：Hello 回報的政策一律是 Off／false</summary>
    public bool RemotePaused { get; set; }

    /// <summary>Off／View／Control（不認得的值讀成 Off）</summary>
    public string RemoteControlLevel { get; set; } = LevelOff;

    public bool AllowRemoteFullAccess { get; set; }

    /// <summary>
    /// 允許其他電腦對話裡的代理在這台執行主控台指令（docs/sync-protocol.md 14.11；2026-10-25）：預設關、只能本機改；
    /// 另要遠端操作是 Control。暫停遠端操作時一律不收（Hello 回報的政策是 Off）
    /// </summary>
    public bool AllowRemoteExec { get; set; }

    /// <summary>
    /// 遠端執行的層級 1、2 動作在這台顯示通知（設計 8.6、14.11 D8；預設開、只能本機改）。舊設定檔沒有這個欄位時是 true。
    /// 第 4 階段的遠端執行（WP4.4）之前沒有東西會用到它
    /// </summary>
    public bool NotifyRemoteActions { get; set; } = true;

    public static bool IsLevel(string? level) => level is LevelOff or LevelView or LevelControl;

    /// <summary>手改或新版寫的不認得的層級改成 Off（最保守）</summary>
    public void Normalize()
    {
        if (!IsLevel(RemoteControlLevel))
            RemoteControlLevel = LevelOff;
    }
}

internal sealed class AppSettings
{
    public const int MinFontSize = 12;
    public const int MaxFontSize = 18;

    /// <summary>多電腦同步（暫停、遠端操作政策）</summary>
    public SyncSettings Sync { get; set; } = new();

    /// <summary>dark／light／system</summary>
    public string Theme { get; set; } = "dark";

    public int FontSize { get; set; } = 14;

    /// <summary>
    /// 介面的持續動態效果（執行中的掃描光、圓環轉動、電腦狀態面板的雷達等）。關掉時網頁照「減少動態」的規則停掉持續的動畫，
    /// 只留載入中的轉圈；系統本身要求減少動態時一律停掉。只能在本機修改，不同步；舊設定檔沒有這個欄位時是 true
    /// </summary>
    public bool VisualEffects { get; set; } = true;

    /// <summary>關閉視窗時縮到系統匣（背景繼續執行）</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>代理提問、核准、完成時系統通知</summary>
    public bool Notifications { get; set; } = true;

    /// <summary>
    /// 對話完成、失敗，或代理提問、要求核准時，用 Windows 內建語音念一句說明（VoiceNotifyRules；視窗在前景也念）。
    /// 只能在本機修改，不同步；舊設定檔沒有這個欄位時是 true
    /// </summary>
    public bool VoiceNotifications { get; set; } = true;

    /// <summary>
    /// 排程頁：列入管理的排程新的一串失敗開始時，自動在它的管理對話請代理診斷（只診斷不修改；ScheduleRunRules 的次數與額度上限）。
    /// 只能在本機修改，不同步；舊設定檔沒有這個欄位時是 true（擁有者 2026-10-26 決定要）
    /// </summary>
    public bool ScheduleAutoDiagnose { get; set; } = true;

    /// <summary>語音通知的語速（總倍速，0.5～2.5、間距 0.1；Speech/SpeechAudioRules）。只能在本機修改，不同步；舊設定檔沒有這個欄位時是 1.5</summary>
    public double VoiceSpeed { get; set; } = 1.5;

    /// <summary>語音通知的音調（升幾個半音，-6～6 的整數；Speech/SpeechAudioRules）。只能在本機修改，不同步；舊設定檔沒有這個欄位時是 2</summary>
    public double VoicePitch { get; set; } = 2;

    /// <summary>偵測到 CLI 新版且沒有執行中的對話時自動更新</summary>
    public bool AutoUpdateCli { get; set; } = true;

    /// <summary>自動下載並在閒置時套用 app 的新版（設計 8.2；只能在本機修改，不同步）</summary>
    public bool AutoUpdateApp { get; set; } = true;

    /// <summary>CLI 換版後，還在跑舊版程序的對話什麼時候改用新版（不認得的值當成預設）</summary>
    [JsonConverter(typeof(TolerantStringEnumConverter))]
    public CliRestartPolicy CliRestartPolicy { get; set; } = CliRestartPolicy.SafePoint;

    /// <summary>
    /// 額外的「不可重複的高風險操作」樣式（不分大小寫的 .NET 正規式，比對工具名稱與輸入）：熱更新時被打斷的工具符合就不自動接續、
    /// 改成提示使用者確認。預設清單在 CliRestartRules（git push、npm publish、rm -rf…），這裡只放使用者加的
    /// </summary>
    public List<string> CliRestartRiskExtra { get; set; } = new();

    /// <summary>停用的預設高風險規則（CliRestartRules 預設清單的規則 id，例如 "git-push"）</summary>
    public List<string> CliRestartRiskDisabled { get; set; } = new();

    public bool EnterToSend { get; set; } = true;

    /// <summary>
    /// Claude 對話的程序啟動時開啟 Remote Control（可從 claude.ai/code 或 Claude App 接手）；開著的程序閒置 2 小時才回收（AgentHubRules.RemoteControlIdleTimeout）。
    /// 設定頁切換時立即套用到活著的程序
    /// </summary>
    public bool ClaudeRemoteControl { get; set; } = true;

    /// <summary>
    /// Claude 帳戶的登入權杖過期（約 8 小時沒用）、又借不到同身分的其他登入時，讓 Claude CLI 替它換發權杖（跑一次最小的 claude -p，
    /// 每次約 0.0001 美元等值額度，每個帳戶最多每小時一次），用量才不會停在過期前的數字（ClaudeTokenRecoveryRules）
    /// </summary>
    public bool ClaudeIdleTokenRefresh { get; set; } = true;

    /// <summary>Claude 主模型過載或不能用時自動改用的模型（--fallback-model；和對話的模型相同時不帶）；null＝不用</summary>
    public string? ClaudeFallbackModel { get; set; }

    /// <summary>
    /// 對話執行到一半碰到訂閱額度上限時，自動改用同一種代理、同一個模型、還有額度的其他帳戶繼續（挑下一次重置最早的；AccountSwitchRules）。
    /// 支援四種代理（Grok 沒有帶內額度錯誤，每一輪非中斷結束後查 _x.ai/billing，鎖定才發 AgentQuotaExhaustedEvent；Antigravity 等單一帳戶種類在開啟跨代理備援時可改用其他代理種類接手）
    /// </summary>
    public bool AutoSwitchAccountOnLimit { get; set; } = true;

    /// <summary>
    /// 額度用完自動換帳戶時，若同一種代理沒有其他合格可用的帳戶，允許跨代理種類改用其他已登入訂閱且還有額度的帳戶繼續（預設關，只能在本機修改）
    /// </summary>
    public bool AutoSwitchCrossProviderOnLimit { get; set; }

    /// <summary>額度用完、又沒有帳戶可以接手時，等重置後自動接著做（12 小時內重置的；Hub/QuotaWaitRules.cs）</summary>
    public bool AutoResumeAfterQuotaReset { get; set; } = true;

    /// <summary>Claude 對話閒置超過 1 小時（提示快取過期）、上下文很大時，送出前提醒會整份重算（網頁 claudeoptrules.cacheResumeWarning）</summary>
    public bool ClaudeIdleResumeWarning { get; set; } = true;

    /// <summary>
    /// 跨四家 CLI（Claude、Codex、Antigravity、Grok）共用全域技能（skills）與全域指示檔（SharedCustomizations；預設關，只能在本機修改）
    /// </summary>
    public bool ShareCrossCliCustomizations { get; set; }

    /// <summary>附加給代理的說明；null＝預設（AgentHubRules.DefaultInstructions）</summary>
    public string? Instructions { get; set; }

    /// <summary>輸入區「繼續」鈕送給代理的那一句；null＝預設（AgentHubRules.DefaultContinueText）</summary>
    public string? ContinueText { get; set; }

    /// <summary>上次使用的帳戶（新對話預設用它）</summary>
    public string? LastAccountId { get; set; }

    /// <summary>
    /// 已經替這台電腦補上過 Antigravity 的本機預設帳戶（升級到支援 agy 的版本後第一次啟動補一次；使用者之後移除不會再補，
    /// 要再加回請用「新增帳戶」）
    /// </summary>
    public bool AntigravityAccountAdded { get; set; }

    /// <summary>已經替這台電腦補上過 Grok 的本機預設帳戶（同 AntigravityAccountAdded：升級後第一次啟動補一次）</summary>
    public bool GrokAccountAdded { get; set; }

    public List<string> RecentFolders { get; set; } = new();

    /// <summary>網頁的分頁與窗格版面（網頁擁有、宿主只保存）</summary>
    public JObject? Layout { get; set; }

    /// <summary>
    /// 主視窗以外另外開的視窗（多視窗，2026-10-25）：結束 app 時留著、下次啟動照樣開回來（含 app 更新後），使用者自己關掉的視窗移除。
    /// 舊版不認得這個欄位（忽略），回滾後另外開的視窗就不見了，對話本身不受影響
    /// </summary>
    public List<AppWindowState> Windows { get; set; } = new();

    public Dictionary<string, AccountDefaults> AccountDefaults { get; set; } = new();

    /// <summary>設定頁「模型設定」，鍵是 "Claude"／"Codex"；null＝舊版設定檔還沒遷移（AgentHub 第一次取用時遷移，見 ProviderDefaults）</summary>
    public Dictionary<string, ProviderConversationDefaults>? ConversationDefaults { get; set; }

    /// <summary>每個帳戶上次 CLI 回報的模型清單（程序還沒啟動前也能選）</summary>
    public Dictionary<string, List<AgentModelOption>> Models { get; set; } = new();

    /// <summary>每個帳戶上次 CLI 回報的「/」指令</summary>
    public Dictionary<string, List<AgentSlashCommand>> Commands { get; set; } = new();

    /// <summary>每個 Claude 帳戶上次 CLI 回報的自訂代理（initialize 的 agents；對話開始前選主代理用）</summary>
    public Dictionary<string, List<AgentNamedOption>> ClaudeAgents { get; set; } = new();

    /// <summary>每個 Claude 帳戶上次 CLI 回報的輸出風格（initialize 的 available_output_styles）</summary>
    public Dictionary<string, List<string>> OutputStyles { get; set; } = new();

    public WindowPlacementInfo? Window { get; set; }

    public AccountDefaults DefaultsFor(string accountId)
    {
        if (!AccountDefaults.TryGetValue(accountId, out var defaults))
        {
            defaults = new AccountDefaults();
            AccountDefaults[accountId] = defaults;
        }
        return defaults;
    }

    /// <summary>這次執行不寫回 settings.json：啟動時讀不到原檔（被占用），不能用預設值把使用者的設定蓋掉</summary>
    [JsonIgnore]
    public bool IsReadOnly { get; private set; }

    /// <summary>啟動時要告訴使用者的事（設定檔讀不到或損毀）；中樞在介面就緒後顯示</summary>
    [JsonIgnore]
    public string? StartupWarning { get; private set; }

    /// <summary>
    /// 讀設定。讀不到（被其他程式占用）時用預設值執行但這次不存檔（之後的 Save 不寫檔，原檔不動）；
    /// 損毀時原檔已改名備份，改用預設值並提示使用者
    /// </summary>
    public static AppSettings Load()
    {
        var (status, saved, backup) = JsonFile.TryRead<AppSettings>(AppPaths.SettingsFile);
        return status switch
        {
            JsonReadStatus.Ok => Normalized(saved!),
            JsonReadStatus.Unreadable => new AppSettings
            {
                IsReadOnly = true,
                StartupWarning = "設定檔暫時讀不到（可能被其他程式占用），這次先用預設設定執行，不會改動原本的設定檔；請重新開啟 app。",
            },
            JsonReadStatus.Corrupt => new AppSettings { StartupWarning = $"設定檔已損毀（原檔備份為 {backup}），已改用預設設定。" },
            _ => new AppSettings(),
        };
    }

    /// <summary>讀回來之後補上缺的區段（舊版設定檔沒有 Sync、或寫成 null）、修正不認得的值</summary>
    private static AppSettings Normalized(AppSettings settings)
    {
        settings.Sync ??= new SyncSettings();
        settings.Sync.Normalize();
        // 另外開的視窗：手改壞的（沒有鍵、鍵重複、是主視窗的鍵 WindowRules.PrimaryKey）丟掉，免得兩個視窗共用一份版面
        settings.Windows = (settings.Windows ?? new())
            .Where(w => w != null && !string.IsNullOrWhiteSpace(w.Key) && w.Key != "main")
            .GroupBy(w => w.Key, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        return settings;
    }

    /// <summary>另外開的視窗的設定（找不到回 null）</summary>
    public AppWindowState? FindWindow(string key) => Windows.FirstOrDefault(w => w.Key == key);

    public void Save()
    {
        if (IsReadOnly)
            return;
        try
        {
            JsonFile.Write(AppPaths.SettingsFile, this);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"設定存檔失敗：{ex.Message}");
        }
    }
}
