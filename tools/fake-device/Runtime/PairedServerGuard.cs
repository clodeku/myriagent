using Myriagent.Sync;

namespace FakeDevice;

/// <summary>
/// 已配對資料根的正式站守門（WP4.9 合併前審查 F4、計畫 D18）：<see cref="DeviceRuntime.StartAsync"/> 一啟動，協調者就照 <c>sync\device.json</c> 的
/// ServerUrl 建引擎、連線（run、status、backlog、unpair，以及 pair、session 自己的已配對檢查之前），完全不經 pair／session 的 <c>--server</c> 守門。
/// 拿真實資料根的複本執行時（同一個使用者的 CNG 金鑰與 DPAPI 權杖都打得開），就會以真實裝置的身分連到正式站。
/// 所以所有指令在啟動執行環境之前先看 device.json：讀得到（Ok）時網址先過 <see cref="LocalHubTargetRules.CheckUrl"/>（不連網）；
/// 會連線的指令再以 <see cref="LocalHubTarget.VerifyAsync"/> 讀一次 /readyz 與憑證。沒有、損毀或讀不到時協調者不建引擎，不必檢查。
/// 失敗丟 <see cref="DeviceRuntimeException"/>（Program 轉成結束碼 1）
/// </summary>
internal static class PairedServerGuard
{
    /// <summary>連線之後那一段的時限（同 pair／session 的守門）</summary>
    public static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 啟動之前檢查這個資料根配對的伺服器：沒有可用的 device.json 回 null；網址不合格丟例外；<paramref name="connect"/> 時再連線確認、回看到的事實。
    /// unpair 只刪本機檔，可以只做網址那一段（<paramref name="connect"/> false）：伺服器停了也解除得了
    /// </summary>
    public static async Task<LocalHubTargetFacts?> VerifyAsync(SyncPaths paths, bool connect, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var (status, device) = DeviceFile.Load(paths.DeviceFile);
        if (status != DeviceFileStatus.Ok || device is null)
            return null;
        if (LocalHubTargetRules.CheckUrl(device.ServerUrl) is { } problem)
        {
            throw new DeviceRuntimeException(
                $"這個資料根配對的伺服器不合格（sync\\device.json 的 {device.ServerUrl}）：{problem}。真實資料根的複本請整個刪掉，不要拿來執行");
        }

        return connect ? await LocalHubTarget.VerifyAsync(new Uri(device.ServerUrl), timeout, ct) : null;
    }
}
