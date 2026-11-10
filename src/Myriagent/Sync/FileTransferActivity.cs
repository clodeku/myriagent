namespace Myriagent.Sync;

/// <summary>
/// 這台正在和其他電腦傳檔案（電腦狀態面板的檔案傳輸；任何執行緒）：替別台送檔案的分段轉送（<see cref="MediaServer"/>，purpose=file）、
/// 從別台分段下載（<see cref="ReplicaMediaSource.DownloadAsync"/>）、上傳給別台（<see cref="ReplicaMediaSource.UploadAsync(string, string, System.Action{long, long}?, System.Threading.CancellationToken)"/>）。
/// 分段下載是一段一段來要的，段與段之間沒有東西在跑，所以結束之後 <see cref="Linger"/> 內也算還在傳。
/// app 自我更新以它擋住自動重啟（AppRestartBlocker.FileTransfer）：2026-11-10 使用者下載另一台的 9.8 GB 資料夾，
/// 來源那台在途中自動換版重新啟動，正在轉送的那一段被切斷
/// </summary>
internal static class FileTransferActivity
{
    /// <summary>傳完一段之後多久內還算在傳（下一段的請求還在路上）</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(30);

    private static int running;
    private static long lastEnded = long.MinValue;

    /// <summary>開始一次傳輸；Dispose 時結束（記下結束的時間）</summary>
    public static IDisposable Begin()
    {
        Interlocked.Increment(ref running);
        return new Scope();
    }

    /// <summary>正在傳，或剛傳完不到 <see cref="Linger"/></summary>
    public static bool Active => Volatile.Read(ref running) > 0 || IsRecent(Interlocked.Read(ref lastEnded), Environment.TickCount64, Linger);

    /// <summary><paramref name="last"/>（毫秒的單調時鐘；long.MinValue＝從來沒有）到 <paramref name="now"/> 不到 <paramref name="window"/></summary>
    internal static bool IsRecent(long last, long now, TimeSpan window)
        => last != long.MinValue && now - last >= 0 && now - last < (long)window.TotalMilliseconds;

    private sealed class Scope : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            Interlocked.Exchange(ref lastEnded, Environment.TickCount64);
            Interlocked.Decrement(ref running);
        }
    }
}
