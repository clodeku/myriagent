using System.Security;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>開機時自動啟動的登錄（AutoStartRegistration 實作；測試給假的，不碰真的登錄）</summary>
internal interface IAutoStart
{
    /// <summary>這次執行能不能設定（AutoStartRules.IsAvailable：受管理的安裝、預設資料夾）</summary>
    bool Available { get; }

    /// <summary>登入時真的會啟動（Run 有值、沒有在工作管理員被停用）；讀不到當成沒有</summary>
    bool IsEnabled();

    /// <summary>開啟或關閉；寫不進登錄時丟 IOException、UnauthorizedAccessException 或 SecurityException</summary>
    void SetEnabled(bool enabled);

    /// <summary>這台開了 Windows 自動登入（重新開機後不用有人登入 app 就會起來）；讀不到是 null</summary>
    bool? AutoLogon() => null;
}

/// <summary>設定頁「開機時自動啟動」（Settings.autoStart）：狀態在登錄、不存在 settings.json，每次渲染設定時重讀</summary>
internal sealed partial class AgentHub
{
    /// <summary>開機啟動的登錄交給誰（AppController 建立時設定；沒有時設定頁的開關停用）</summary>
    internal IAutoStart? AutoStart { get; set; }

    /// <summary>true／false；這次執行不能設定（開發版、--data 的探針、測試）是 null，網頁停用開關並說明原因</summary>
    private JToken RenderAutoStart()
        => AutoStart is { Available: true } autoStart ? new JValue(autoStart.IsEnabled()) : JValue.CreateNull();

    /// <summary>開機自動啟動後鎖定電腦（settings 的 AutoStartLock）：不能設定開機啟動時是 null（設定頁不顯示）</summary>
    private JToken RenderAutoStartLock()
        => AutoStart is { Available: true } ? new JValue(settings.AutoStartLock) : JValue.CreateNull();

    /// <summary>這台開了 Windows 自動登入：設定頁在沒開時提醒「重新開機後要有人登入才會啟動」；不能設定或讀不到是 null</summary>
    private JToken RenderAutoLogon()
        => AutoStart is { Available: true } autoStart && autoStart.AutoLogon() is { } on ? new JValue(on) : JValue.CreateNull();

    /// <summary>setting autoStart：不是布林、或這次執行不能設定就忽略。寫入失敗時提示原因，照樣重送設定讓開關回到登錄的實際狀態</summary>
    private void ChangeAutoStart(JToken value)
    {
        if (value.Type != JTokenType.Boolean || AutoStart is not { Available: true } autoStart)
            return;
        var enabled = value.Value<bool>();
        try
        {
            autoStart.SetEnabled(enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Log.Warn($"開機時自動啟動設定失敗：{ex.Message}");
            Toast($"{(enabled ? "開啟" : "關閉")}開機時自動啟動失敗：{ex.Message}", isError: true);
        }
    }
}
