using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Myriagent.Remote;

/// <summary>
/// 電腦狀態面板的電腦選單：調整那台的喇叭音量、靜音，立即關閉螢幕（2026-11-17 使用者要求；遠端指令 <c>deviceControl</c>，那台要「可以操作」，
/// 功能 <c>ctl1</c>，sync-protocol 14.21）。指令的欄位與結果的形狀（桌面與伺服器共用）。純函式
/// </summary>
internal static class DeviceControlRules
{
    /// <summary>查目前的音量與靜音</summary>
    public const string AudioGet = "audioGet";

    /// <summary>設定音量（<c>volume</c>，0～100 的字串）與／或靜音（<c>mute</c>）</summary>
    public const string AudioSet = "audioSet";

    /// <summary>立即關閉螢幕（同讓螢幕休眠；動滑鼠鍵盤就亮，不鎖定）</summary>
    public const string ScreenOff = "screenOff";

    public static IReadOnlyList<string> Actions { get; } = [AudioGet, AudioSet, ScreenOff];

    /// <summary>結果的種類（只回發起的視窗或連線）</summary>
    public const string StateType = "deviceControlState";

    /// <summary>錯誤說明的上限</summary>
    public const int MaxErrorLength = 300;

    /// <summary>音量的字串（0～100 的整數，前後不能有空白）；不合格回 null</summary>
    public static int? ParseVolume(string? text)
        => text is { Length: > 0 and <= 3 } && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 100 ? v : null;

    /// <summary>指令的參數不合格時回原因：audioSet 至少要有音量或靜音其中一個、音量要是 0～100</summary>
    public static string? Problem(string? action, string? volume, bool? mute)
    {
        if (action is null || !Actions.Contains(action))
            return "不認得的操作";
        if (action != AudioSet)
            return null;
        if (volume is null && mute is null)
            return "要設定音量或靜音";
        return volume is not null && ParseVolume(volume) is null ? "音量要是 0～100" : null;
    }

    /// <summary>結果：音量（0～100）與靜音（這台沒有播放裝置時都是 null）、錯誤說明（成功是 null）</summary>
    public static JObject State(string? requestId, string action, int? volume, bool? muted, string? error) => new()
    {
        ["type"] = StateType,
        ["requestId"] = requestId is null ? JValue.CreateNull() : new JValue(requestId),
        ["action"] = action,
        ["volume"] = volume is { } v ? new JValue(v) : JValue.CreateNull(),
        ["muted"] = muted is { } m ? new JValue(m) : JValue.CreateNull(),
        ["error"] = error is null ? JValue.CreateNull() : new JValue(error.Length > MaxErrorLength ? error[..MaxErrorLength] : error),
    };

    /// <summary>伺服器轉給瀏覽器之前的投影：只留已知的鍵、形狀不對整則丟掉（回 null）</summary>
    public static JObject? Project(JObject message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string? requestId = null;
        if (message["requestId"] is JValue { Type: JTokenType.String, Value: string rid })
        {
            if (rid.Length is 0 or > 64)
                return null;
            requestId = rid;
        }
        else if (message["requestId"] is not (null or { Type: JTokenType.Null }))
        {
            return null;
        }

        if (message["action"] is not JValue { Type: JTokenType.String, Value: string action } || !Actions.Contains(action))
            return null;
        int? volume = message["volume"] switch
        {
            null or { Type: JTokenType.Null } => null,
            JValue { Type: JTokenType.Integer } v when v.Value<long>() is >= 0 and <= 100 => (int)v.Value<long>(),
            _ => -1,
        };
        bool? muted = message["muted"] switch
        {
            null or { Type: JTokenType.Null } => null,
            JValue { Type: JTokenType.Boolean, Value: bool b } => b,
            _ => null,
        };
        if (volume == -1 || message["muted"] is { Type: not (JTokenType.Null or JTokenType.Boolean) })
            return null;
        string? error = message["error"] switch
        {
            null or { Type: JTokenType.Null } => null,
            JValue { Type: JTokenType.String, Value: string e } when e.Length <= MaxErrorLength => e,
            _ => "\0",
        };
        return error == "\0" ? null : State(requestId, action, volume, muted, error);
    }
}
