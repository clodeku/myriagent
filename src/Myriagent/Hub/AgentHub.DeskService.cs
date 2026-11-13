using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.RemoteDesk;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 遠端桌面的鎖定畫面支援（docs/web-protocol.md 的 deskService）：
// 以 LocalSystem 執行的服務 MyriagentDesk 的安裝、更新、移除與狀態。設定頁打開同步區時送 deskService {action:"check"}，
// 安裝與移除由這裡以 UAC 啟動安裝器（要有人在這台按同意；app 自己一直是一般權限）。查詢與安裝都在背景執行緒，結果回 UI 執行緒才存與送。
// 只有這台自己的視窗能送（不在遠端指令的型錄裡：瀏覽器版與其他電腦不能在這台裝系統服務）。
// 更新不經 UAC（擁有者 2026-11-13 決定）：裝好的服務有 selfUpdate、app 帶的是簽章過而且比較新的那一版時，app 啟動一陣子後自動請服務自己換版
// （服務自己驗簽章與版本，DeskServiceRules.CheckSelfUpdate），按「更新」也先走這條路、不成才退回 UAC 的安裝器。
// 中樞預設沒接（不送 deskService、指令不做事），只有 AppController 以 AttachDeskService 接上真的；中樞測試用假的
internal sealed partial class AgentHub
{
    private IDeskServiceManager? deskService;

    /// <summary>設定頁要不要顯示那一區（app 一律給 true；false 時只有已經裝了服務的電腦看得到——才移除得掉）</summary>
    private bool deskServicePreview;

    private DeskServiceStatus? deskServiceStatus;

    /// <summary>進行中的動作（check、install、uninstall；沒有是 null）</summary>
    private string? deskServiceBusy;

    /// <summary>上一個動作的錯誤（給人看；下一個動作開始就清掉）</summary>
    private string? deskServiceError;

    /// <summary>接上鎖定畫面支援的管理（AppController；只接一次）</summary>
    /// <param name="autoUpdateAfter">多久之後自動請服務換成 app 帶的這一版（null＝不自動；測試不給）</param>
    internal void AttachDeskService(IDeskServiceManager manager, bool preview, TimeSpan? autoUpdateAfter = null)
    {
        deskService = manager ?? throw new ArgumentNullException(nameof(manager));
        deskServicePreview = preview;
        if (autoUpdateAfter is { } delay)
            _ = Task.Delay(delay).ContinueWith(_ => host.BeginInvoke(StartDeskServiceAutoUpdate), TaskScheduler.Default);
    }

    /// <summary>
    /// app 啟動一陣子後（UI 執行緒）：裝好的服務比 app 帶的舊、而且能自己更新，就請它換版。沒有人按、不跳 UAC；
    /// 不能自己更新的（舊的服務、開發版）不動，等使用者在設定頁按。有動作在進行、遠端桌面正在被看時不做（下次啟動再說）
    /// </summary>
    internal void StartDeskServiceAutoUpdate()
    {
        if (deskService is not { } manager || disposed || deskServiceBusy is not null || RemoteDeskActive)
            return;
        deskServiceBusy = DeskServiceHubRules.BusyUpdate;
        PostDeskService();
        _ = Task.Run(() =>
        {
            var status = manager.Query(probe: false);
            if (!DeskServiceHubRules.CanSelfUpdate(status))
                return (status, (string?)null);
            var error = manager.SelfUpdate();
            Log.Info("鎖定畫面支援自動更新：" + (error ?? "完成"));
            return (manager.Query(probe: false), error is null ? null : "自動更新沒有完成：" + error);
        }).ContinueWith(t => host.BeginInvoke(() => OnDeskServiceDone(DeskServiceHubRules.BusyUpdate, t)), TaskScheduler.Default);
    }

    /// <summary>網頁的 <c>deskService</c>（UI 執行緒）</summary>
    private void HandleDeskServiceCommand(string? action)
    {
        if (deskService is not { } manager || disposed)
            return;
        if (!DeskServiceHubRules.IsAction(action))
            return;
        if (deskServiceBusy is not null)
        {
            // 正在做：只重送目前的狀態
            PostDeskService();
            return;
        }

        // 更新先請服務自己換版（不必按 UAC）：照上一次查到的狀態決定要不要試（服務自己會再驗）
        var selfUpdate = action == DeskServiceHubRules.ActionInstall && DeskServiceHubRules.CanSelfUpdate(deskServiceStatus);
        deskServiceBusy = action;
        deskServiceError = null;
        PostDeskService();
        _ = Task.Run(() => RunDeskServiceAction(manager, action!, selfUpdate)).ContinueWith(t => host.BeginInvoke(() => OnDeskServiceDone(action!, t)), TaskScheduler.Default);
    }

    /// <summary>背景執行緒：做完動作再查一次狀態</summary>
    private static (DeskServiceStatus Status, string? Error) RunDeskServiceAction(IDeskServiceManager manager, string action, bool selfUpdate)
    {
        string? error = null;
        // 服務自己換版；它不收、沒換成才退回安裝器（使用者按的，人就在這裡）
        var updated = false;
        if (selfUpdate)
        {
            var problem = manager.SelfUpdate();
            updated = problem is null;
            Log.Info("鎖定畫面支援請服務自己更新：" + (problem ?? "完成"));
        }

        if (action != DeskServiceHubRules.ActionCheck && !updated)
        {
            var exit = manager.RunElevated(action);
            error = DeskServiceHubRules.ErrorText(action, exit);
            Log.Info($"鎖定畫面支援 {action}：結束碼 {exit}{(error is null ? string.Empty : "（" + error + "）")}");
        }
        // 剛裝好就順便問服務看到的輸入桌面（確認它在這個工作階段開得了幫手）
        return (manager.Query(probe: true), error);
    }

    private void OnDeskServiceDone(string action, Task<(DeskServiceStatus Status, string? Error)> task)
    {
        deskServiceBusy = null;
        if (disposed)
            return;
        if (task.IsCompletedSuccessfully)
        {
            deskServiceStatus = task.Result.Status;
            deskServiceError = task.Result.Error;
        }
        else
        {
            var ex = task.Exception?.GetBaseException();
            Log.Error($"鎖定畫面支援 {action} 失敗", ex ?? new InvalidOperationException(action));
            deskServiceError = "發生未預期的錯誤：" + (ex?.Message ?? "不明");
        }
        PostDeskService();
    }

    /// <summary>送給這台自己的視窗（同步鏡像不送）</summary>
    private void PostDeskService()
    {
        if (deskService is null)
            return;
        var targets = views.Values.Where(v => v.Id != SyncViewId).ToList();
        if (targets.Count > 0)
            TryPost(targets, () => DeskServiceHubRules.Message(deskServiceStatus, deskServicePreview, deskServiceBusy, deskServiceError));
    }
}

/// <summary>鎖定畫面支援在中樞的規則（純函式）</summary>
internal static class DeskServiceHubRules
{
    public const string ActionCheck = "check";

    public const string ActionInstall = "install";

    public const string ActionUninstall = "uninstall";

    /// <summary>自動更新進行中（<c>busy</c> 的值；不是網頁能送的動作）</summary>
    public const string BusyUpdate = "update";

    public static bool IsAction(string? action) => action is ActionCheck or ActionInstall or ActionUninstall;

    /// <summary>
    /// 能不能請服務自己換成 app 帶的這一版（不經 UAC）：服務在執行、是系統權限、認得 selfUpdate；app 帶的是簽章過的發佈版，
    /// 而且照 <see cref="DeskServiceRules.NeedsUpdate"/> 比裝好的新。服務自己會再驗一次（這裡只是不白送）
    /// </summary>
    public static bool CanSelfUpdate(DeskServiceStatus? status)
        => status is { Installed: { } installed, Bundled: { Signed: true } bundled, Hello: { System: true } hello }
           && hello.Features.Contains(DeskServiceRules.FeatureSelfUpdate)
           && bundled.Build != Update.ReleaseIdentity.DevBuild
           && DeskServiceRules.NeedsUpdate(installed, bundled.Version, bundled.Build, bundled.Payload);

    /// <summary>安裝器的結束碼 → 給人看的錯誤；成功回 null</summary>
    public static string? ErrorText(string action, int exit)
    {
        var what = action == ActionUninstall ? "移除" : "安裝";
        return exit switch
        {
            0 => null,
            DeskServiceManager.Declined => $"沒有{what}：Windows 的系統管理員確認（UAC）沒有按「是」",
            DeskServiceManager.TimedOut => $"{what}程式沒有在時間內結束",
            _ => $"{what}失敗：{DeskServiceRules.Describe(DeskServiceRules.ExitOf(exit))}（代碼 {exit}）",
        };
    }

    /// <summary>
    /// 裝好的服務能不能在安全桌面輸入：<c>ready</c>（安裝時記下的伺服器就是現在配對的這一台）、<c>reinstall</c>（沒記、或配對的伺服器換了：
    /// 要重新安裝才記得下來）、<c>unpaired</c>（這台沒有配對：遠端桌面本來就用不了）
    /// </summary>
    public static string SecureInputState(DeskServiceTrust? installed, DeskServiceTrust? current)
        => current is null ? "unpaired" : installed == current ? "ready" : "reinstall";

    /// <summary>
    /// 給網頁的 <c>deskService</c>（web-protocol）。<c>offered</c>＝設定頁要不要顯示這一區（<paramref name="preview"/>，或已經裝了服務）。
    /// 還沒查過（<paramref name="status"/> 是 null）時其他欄位都是還不知道的樣子
    /// </summary>
    public static JObject Message(DeskServiceStatus? status, bool preview, string? busy, string? error)
    {
        var installed = status?.Installed;
        var bundled = status?.Bundled;
        var message = new JObject
        {
            ["type"] = "deskService",
            ["offered"] = preview || installed is not null,
            ["known"] = status is not null,
            ["installed"] = installed is not null,
            ["running"] = status?.Hello is not null,
            ["version"] = installed?.Version,
            ["build"] = installed?.Build,
            ["bundled"] = bundled is null ? null : new JObject { ["version"] = bundled.Version, ["build"] = bundled.Build },
            ["canInstall"] = bundled is not null,
            ["update"] = installed is not null && bundled is not null && DeskServiceRules.NeedsUpdate(installed, bundled.Version, bundled.Build, bundled.Payload),
            ["system"] = status?.Hello?.System,
            ["selfUpdate"] = CanSelfUpdate(status),
            ["secureInput"] = installed is null ? null : SecureInputState(status!.InstalledTrust, status.CurrentTrust),
            ["busy"] = busy,
            ["error"] = error,
        };
        if (status?.Probe is { } probe)
        {
            message["probe"] = new JObject
            {
                ["desktop"] = probe.Desktop,
                ["secure"] = probe.Secure,
                ["error"] = probe.Error,
                ["desktops"] = new JArray(probe.Desktops),
            };
        }
        return message;
    }
}
