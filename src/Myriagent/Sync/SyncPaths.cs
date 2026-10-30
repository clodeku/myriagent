using Myriagent.Core;

namespace Myriagent.Sync;

/// <summary>
/// 一台裝置的同步資料位置（設計 3.4、3.6）。同步引擎不讀 <see cref="AppPaths"/>，一律以這個注入：
/// 同一個程序可以同時跑好幾個引擎（S8 的多裝置情境、tools/fake-device）。
/// 「已配對」只看 <see cref="DeviceFile"/> 是否存在（<c>sync\partial</c> 是別的功能用的）。
/// </summary>
internal sealed record SyncPaths(string Directory)
{
    /// <summary>裝置身分、權杖（DPAPI）、伺服器身分釘選（只有一個寫入者，D9）</summary>
    public string DeviceFile => Path.Combine(Directory, "device.json");

    /// <summary>outbox、游標、目錄快取、名冊（SyncStore）</summary>
    public string DatabaseFile => Path.Combine(Directory, "sync.db");

    /// <summary>建立裝置金鑰之前寫、配對完成或放棄後刪：清理當機留下的孤兒 CNG 金鑰（PairingRecovery）</summary>
    public string PendingKeyFile => Path.Combine(Directory, "pending-key.json");

    public static SyncPaths FromAppPaths() => new(AppPaths.SyncDirectory);
}
