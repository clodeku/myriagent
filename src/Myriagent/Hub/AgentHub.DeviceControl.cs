using System.Runtime.InteropServices;
using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.SystemStatus;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 電腦狀態面板的電腦選單：喇叭音量、靜音、立即關閉螢幕（2026-11-17 使用者要求；web-protocol 的 deviceControl、sync-protocol 14.21）。
// 這台的卡片直接在這裡做；其他電腦的經型錄的 deviceControl（那台要「可以操作」）。調整在背景執行緒（Core Audio 的 COM 呼叫可能要等驅動程式），
// 結果以 deviceControlState 回發起的視窗或連線。中樞預設沒接（回 unsupported），只有 AppController 以 AttachDeviceControls 接上
internal sealed partial class AgentHub
{
    private IDeviceControls? deviceControls;

    /// <summary>接上這台的喇叭與螢幕（AppController；測試接假的）</summary>
    internal void AttachDeviceControls(IDeviceControls controls) => deviceControls = controls ?? throw new ArgumentNullException(nameof(controls));

    private void RegisterDeviceControlRemoteHandlers(List<IRemoteCommandHandler> into) => into.Add(new DeviceControlRemoteHandler(this));

    /// <summary>
    /// HandleCommand 開頭：這台的卡片送的 deviceControl（沒有 deviceId，或 deviceId 是這台）在這裡做、結果只回這個視窗，回 true；
    /// 其他電腦的由 TryForwardPeerCommand 交給同步
    /// </summary>
    private bool TryHandleLocalDeviceControl(IAgentView view, JObject command)
    {
        if (command.Value<string>("cmd") != RemoteCommandCatalog.DeviceControl)
            return false;
        var deviceId = command["deviceId"] is JValue { Type: JTokenType.String, Value: string d } ? d : null;
        if (!string.IsNullOrEmpty(deviceId) && deviceId != peerSink?.DeviceId)
            return false;
        var requestId = command["requestId"] is JValue { Type: JTokenType.String, Value: string r } ? r : null;
        var action = command.Value<string>("action") ?? string.Empty;
        var volume = command["volume"] is JValue { Type: JTokenType.String, Value: string v } ? v : null;
        bool? mute = command["mute"] is JValue { Type: JTokenType.Boolean, Value: bool m } ? m : null;
        var viewId = view.Id;
        _ = RunDeviceControlAsync(requestId, action, volume, mute).ContinueWith(t => host.BeginInvoke(() =>
        {
            if (!disposed && views.TryGetValue(viewId, out var v))
                Post(v, t.Result);
        }), TaskScheduler.Default);
        return true;
    }

    /// <summary>做一次調整（背景執行緒），回 deviceControlState；不合格、沒接上、失敗都以 error 說明</summary>
    private Task<JObject> RunDeviceControlAsync(string? requestId, string action, string? volumeText, bool? mute)
    {
        var shownAction = DeviceControlRules.Actions.Contains(action) ? action : DeviceControlRules.AudioGet;
        if (DeviceControlRules.Problem(action, volumeText, mute) is { } problem)
            return Task.FromResult(DeviceControlRules.State(requestId, shownAction, null, null, problem));
        if (deviceControls is not { } controls)
            return Task.FromResult(DeviceControlRules.State(requestId, action, null, null, "這台的 Clode Myriagent 不能調整喇叭與螢幕"));
        var volume = DeviceControlRules.ParseVolume(volumeText);
        return Task.Run(() =>
        {
            try
            {
                if (action == DeviceControlRules.ScreenOff)
                {
                    var ok = controls.ScreenOff();
                    Log.Info(ok ? "電腦選單：關閉了這台的螢幕" : "電腦選單：關閉螢幕失敗");
                    return DeviceControlRules.State(requestId, action, null, null, ok ? null : "關不了這台的螢幕");
                }

                var audio = action == DeviceControlRules.AudioSet ? controls.SetAudio(volume, mute) : controls.GetAudio();
                if (action == DeviceControlRules.AudioSet && audio is { } set)
                    Log.Info($"電腦選單：這台的音量 {set.Volume}%{(set.Muted ? "（靜音）" : string.Empty)}");
                return audio is { } a
                    ? DeviceControlRules.State(requestId, action, a.Volume, a.Muted, null)
                    : DeviceControlRules.State(requestId, action, null, null, "這台沒有可以調整的播放裝置（喇叭或耳機）");
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException or ExternalException)
            {
                Log.Warn($"電腦選單的 {action} 失敗：{ex.Message}");
                return DeviceControlRules.State(requestId, action, null, null, "調整失敗：" + ex.Message);
            }
        });
    }

    /// <summary>遠端的 deviceControl（其他電腦、瀏覽器版）：閘門已要「可以操作」，結果連同 deviceControlState 回去</summary>
    private sealed class DeviceControlRemoteHandler(AgentHub hub) : IRemoteCommandHandler
    {
        public bool TryHandle(RemoteViewContext context, IAgentView view, string cmd, JObject command)
        {
            if (cmd != RemoteCommandCatalog.DeviceControl)
                return false;
            if (!context.Authorized)
            {
                context.Reject(RemoteResultCodes.Invalid, "這個指令沒有經過這台電腦的遠端操作檢查", RemoteGateSteps.Handler);
                return true;
            }

            if (hub.deviceControls is null)
            {
                context.Reject(RemoteResultCodes.Unsupported, "這台的 Clode Myriagent 不能調整喇叭與螢幕", RemoteGateSteps.Handler);
                return true;
            }

            var action = command.Value<string>("action") ?? string.Empty;
            var volume = command["volume"] is JValue { Type: JTokenType.String, Value: string v } ? v : null;
            bool? mute = command["mute"] is JValue { Type: JTokenType.Boolean, Value: bool m } ? m : null;
            if (DeviceControlRules.Problem(action, volume, mute) is { } problem)
            {
                context.Reject(RemoteResultCodes.Invalid, problem, RemoteGateSteps.Handler);
                return true;
            }

            var requestId = command["requestId"] is JValue { Type: JTokenType.String, Value: string r } ? r : null;
            _ = hub.RunDeviceControlAsync(requestId, action, volume, mute).ContinueWith(t => hub.host.BeginInvoke(() => context.Complete(t.Result)), TaskScheduler.Default);
            return true;
        }
    }
}
