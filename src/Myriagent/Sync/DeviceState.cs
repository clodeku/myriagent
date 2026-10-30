using Myriagent.Core;

namespace Myriagent.Sync;

/// <summary>
/// 引擎執行期間 device.json 的唯一寫入者（D9）：讀寫一律以記憶體裡這一份 <see cref="Current"/> 為準。
/// 改釘伺服器身分、換存輪換的權杖都經 <see cref="TryUpdate"/>：先存檔成功才換記憶體，存檔失敗時記憶體不變。
/// 只在引擎執行緒使用（不加鎖）。
/// </summary>
internal sealed class DeviceState
{
    private readonly SyncPaths paths;

    public DeviceState(SyncPaths paths, DeviceFileData initial)
    {
        this.paths = paths;
        Current = initial;
    }

    public DeviceFileData Current { get; private set; }

    /// <summary>存檔失敗（唯讀、被占用、磁碟滿）回 false，記憶體不變</summary>
    public bool TryUpdate(Func<DeviceFileData, DeviceFileData> change)
    {
        var next = change(Current);
        if (next == Current)
            return true;
        try
        {
            DeviceFile.Save(paths.DeviceFile, next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"device.json 存檔失敗（沿用記憶體裡的舊值）：{ex.Message}");
            return false;
        }

        Current = next;
        return true;
    }
}
