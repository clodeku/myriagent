using System.Runtime.CompilerServices;
using Myriagent.Core;

namespace Myriagent.Sync;

/// <summary>協調者建立引擎時給的參數（只用 BCL、Protocol 與桌面型別：未配對的路徑也看得到這個型別）</summary>
/// <param name="SignerFactory">null＝以 device.json 記的名稱與位置開 CNG 金鑰</param>
/// <param name="Options">null＝<see cref="SyncEngineOptions.Default"/></param>
internal sealed record SyncEngineCreateArgs(
    SyncPaths Paths,
    DeviceFileData Device,
    ISyncHostPort Host,
    ISyncUiApplier Applier,
    DevicePolicy Policy,
    bool DevTrustEnv,
    Uri? ServerOverride,
    string AppVersion,
    string? MachineName,
    string? WindowsUser,
    Func<DeviceFileData, IDeviceSigner?>? SignerFactory = null,
    SyncEngineOptions? Options = null);

/// <summary>
/// S6 的載入邊界（D7）：未配對的程式路徑碰 SignalR、SQLite 的唯一入口。兩個方法都標 <see cref="SyncLoadBoundaryAttribute"/>、
/// NoInlining、同步；這個型別不得有 lambda、區域函式或編譯器產生的巢狀型別（委派一律取自 <see cref="SyncEngineDefaults"/>：
/// 靜態方法群組轉成委派時編譯器會產生快取用的巢狀型別）——Sync.Isolation 的測試把關。
/// </summary>
internal static class SyncEngineLoader
{
    /// <summary>只建物件、不做 I/O：sync.db 在引擎執行緒上開（不在 UI 執行緒）</summary>
    [SyncLoadBoundary]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ISyncEngine Create(SyncEngineCreateArgs args) =>
        new SyncEngine(new SyncEngineArgs(
            args.Paths,
            args.Device,
            args.SignerFactory ?? SyncEngineDefaults.SignerFactory,
            SyncEngineDefaults.OpenStore,
            new SignalRSyncTransport(),
            args.Host,
            args.Applier,
            TimeProvider.System,
            args.Options ?? SyncEngineOptions.Default,
            args.Policy,
            args.ServerOverride,
            args.DevTrustEnv,
            args.AppVersion,
            SyncEngineDefaults.BootId,
            args.MachineName,
            args.WindowsUser,
            new ConversationChangeBox()));

    /// <summary>
    /// 配對提交（背景執行緒）：建 sync.db（結構、kv、名冊同一個交易）→ 最後寫 device.json（提交點：「已配對」只看它）。
    /// device.json 不存在或損毀時殘留的 sync.db 是沒有完成（或已經作廢）的配對留下的，先刪掉——協調者把損毀的 device.json
    /// 當成未配對、允許重新配對，不刪的話 SyncStore.Create 必定因為檔案已存在而失敗。損毀的 device.json 由這次的 Save 蓋掉
    /// （它記的舊金鑰名稱已經讀不出來，那把金鑰只能留著）。device.json 讀不到（Unreadable）時不刪：協調者在那種狀態不接受配對。
    /// 寫 device.json 失敗就刪掉剛建的 sync.db
    /// </summary>
    [SyncLoadBoundary]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static PairingCommitResult CompletePairing(SyncPaths paths, PairTokenResponse response, DeviceFileData device)
    {
        Directory.CreateDirectory(paths.Directory);
        if (File.Exists(paths.DatabaseFile) && DeviceFile.Load(paths.DeviceFile).Status is DeviceFileStatus.Missing or DeviceFileStatus.Corrupt)
        {
            Log.Warn("配對提交：刪掉沒有完成或已作廢的配對留下的 sync.db（device.json 不存在或損毀）");
            SyncEngineDefaults.DeleteDatabase(paths);
        }

        using (SyncStore.Create(paths.DatabaseFile, device.DeviceId, device.PairedAt, response.Server, response.Trust))
        {
        }

        try
        {
            DeviceFile.Save(paths.DeviceFile, device);
        }
        catch
        {
            SyncEngineDefaults.DeleteDatabase(paths);
            throw;
        }

        return new PairingCommitResult(device);
    }
}

/// <summary>引擎的預設元件（在載入邊界之內使用）</summary>
internal static class SyncEngineDefaults
{
    /// <summary>每個程序一個（同一程序重連時不變；伺服器以它判斷「重新啟動」）</summary>
    public static string BootId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>以 device.json 記的名稱與位置開 CNG 金鑰（找不到回 null；打不開丟 CryptographicException，由引擎分類）</summary>
    public static Func<DeviceFileData, IDeviceSigner?> SignerFactory { get; } = d => DeviceKey.Open(d.KeyName, d.KeyStorage);

    public static Func<string, string, SyncStore> OpenStore { get; } = SyncStore.Open;

    /// <summary>刪 sync.db 與它的 -wal／-shm（檔案不在就略過）</summary>
    public static void DeleteDatabase(SyncPaths paths)
    {
        foreach (var file in new[] { paths.DatabaseFile, paths.DatabaseFile + "-wal", paths.DatabaseFile + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"刪不掉 {Path.GetFileName(file)}：{ex.Message}");
            }
        }
    }
}
