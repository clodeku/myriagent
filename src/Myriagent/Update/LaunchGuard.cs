using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Update;

/// <summary>Program.Main 最前面的閃退保護（設計 8.6）與健康訊號（8.5 第 5 步）</summary>
internal static class LaunchGuard
{
    /// <summary>
    /// 這一版已連續 3 次沒到健康就結束、而且有前一版：current 切回前一版、啟動它，回 true（呼叫端結束自己）。
    /// 必須在拿到單一實例之後呼叫：只是叫醒既有實例就結束的啟動不能算失敗。
    /// 讀寫狀態或切回失敗都照常啟動（回 false、不丟例外：這時 Main 還沒裝例外處理）
    /// </summary>
    public static bool RollBackIfCrashing(InstallLayout layout, string release, Func<string, IReadOnlyList<string>, int?> launch)
    {
        string? rolledBackTo = null;
        try
        {
            UpdaterStateStore.Update(layout, s =>
            {
                var (next, action) = UpdaterStateRules.OnLaunch(s, release);
                if (action == LaunchAction.RollBack && next.Current is { } previous)
                {
                    // 先切回前一版才寫狀態：切不回去（前一版的程式不在、junction 被鎖）就丟例外、這次不寫，下次啟動再試。
                    // 先寫狀態的話，切換失敗之後狀態的目前版已經是前一版，閃退保護再也不會觸發
                    if (!File.Exists(layout.Executable(previous)))
                        throw new FileNotFoundException($"前一版 {previous} 的程式不存在", layout.Executable(previous));
                    UpdateApplier.RetargetWithRetry(layout, layout.VersionDirectory(previous), Thread.Sleep);
                    rolledBackTo = previous;
                }
                return next;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            if (rolledBackTo == null)
            {
                Log.Warn($"閃退保護沒有完成，照常啟動 {release}：{ex.Message}");
                return false;
            }
            Log.Warn($"已切回 {rolledBackTo}，但寫 updater-state.json 失敗：{ex.Message}");
        }
        if (rolledBackTo == null)
            return false;
        Log.Warn($"{release} 連續啟動失敗，切回 {rolledBackTo}");
        launch(layout.CurrentExecutable, Array.Empty<string>());
        return true;
    }

    /// <summary>
    /// 新版到了健康（主視窗網頁就緒、接續已開始）：連續失敗次數歸零；換版後的啟動另寫 update-health\&lt;token&gt;.json 給幫手。
    /// 兩件事各自進行：狀態檔暫時寫不進去，也照樣給幫手健康訊號（新版確實健康，不能因此被回滾）
    /// </summary>
    public static void MarkHealthy(InstallLayout layout, string release, string? token)
    {
        try
        {
            UpdaterStateStore.Update(layout, s => UpdaterStateRules.OnHealthy(s, release));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            Log.Warn($"連續啟動失敗次數歸零失敗：{ex.Message}");
        }
        if (token == null)
            return;
        try
        {
            Directory.CreateDirectory(layout.HealthDirectory);
            JsonFile.Write(layout.HealthFile(token), new JObject { ["release"] = release, ["pid"] = Environment.ProcessId, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"寫健康訊號失敗：{ex.Message}");
        }
    }
}
