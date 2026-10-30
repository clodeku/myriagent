namespace MyriagentHub.Ops;

/// <summary>
/// 備份連線的命令逾時（WP4.2 計畫 D41、答 7）：sync.db 會因回補長到幾 GB，固定 60 秒的 <c>VACUUM INTO</c>／<c>integrity_check</c> 可能不夠。
/// 60 秒＋每個完整的 GiB 30 秒，上限 1,800 秒。「每 GiB 30 秒」是估計，依步 15 的 Hub 主機實測（SqliteBench B6）調整，結果寫進 <c>docs/hub-setup.md</c>。
/// 步 15 的 B6（開發機）實測：Microsoft.Data.Sqlite 的命令逾時只限制遇到 busy／locked 時的重試，不會中斷執行中的單一敘述
/// （命令逾時 1 秒的 <c>VACUUM INTO</c> 跑了 2 秒照樣完成）。所以這個值是等鎖的上限，不會讓大的 sync.db 備份因為跑得久而失敗
/// （開發機每 GiB 的 <c>VACUUM INTO</c> 約 10 秒，見 <c>tools/hub-probe/README.md</c> 的 B6）
/// </summary>
internal static class BackupTimeoutRules
{
    public const int MinSeconds = 60;

    public const int SecondsPerGiB = 30;

    public const int MaxSeconds = 1800;

    private const long GiB = 1L << 30;

    /// <summary>以資料庫檔（含 -wal）的位元組數算命令逾時秒數；負數當 0</summary>
    public static int CommandTimeoutSeconds(long bytes)
    {
        var gib = Math.Max(0, bytes) / GiB;
        return (int)Math.Min(MaxSeconds, MinSeconds + SecondsPerGiB * gib);
    }
}
