using Myriagent.Core;

namespace Myriagent.Update;

/// <summary>updater-state.json（設計 8.1）：app、換版幫手、Program.Main 的閃退保護都讀寫它（經 UpdaterStateStore.Update 的 mutex）</summary>
internal sealed class UpdaterState
{
    public string? Current { get; set; }

    public string? Previous { get; set; }

    /// <summary>已下載驗證、放在 versions\ 等待套用的版本</summary>
    public string? Staged { get; set; }

    /// <summary>不再安裝的版本（健康閘門失敗兩次、或通過後連續閃退被切回）</summary>
    public List<string> Bad { get; set; } = new();

    /// <summary>release → 健康閘門失敗次數（第一次只回滾，第二次才列壞版）</summary>
    public Dictionary<string, int> GateFailures { get; set; } = new();

    /// <summary>release → 連續「啟動了但沒到健康就結束」的次數</summary>
    public Dictionary<string, int> LaunchFailures { get; set; } = new();

    /// <summary>撤銷的發佈金鑰 kid（U3 由信任資料寫入）</summary>
    public List<string> RevokedKids { get; set; } = new();

    /// <summary>最近一次更新或回滾的結果（設定頁顯示）</summary>
    public string? LastResult { get; set; }

    public DateTime? LastResultAt { get; set; }

    public DateTime? LastCheckAt { get; set; }
}

internal static class UpdaterStateStore
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 讀取（唯讀顯示用；讀不到或損毀時回預設值）。也拿 Update 的具名 mutex：寫入端以「取代」換檔，
    /// 別的程序這時開著狀態檔會讓換檔失敗。等不到鎖就不鎖直接讀（讀取不因此失敗）
    /// </summary>
    public static UpdaterState Load(InstallLayout layout)
    {
        using var mutex = new Mutex(false, layout.StateMutexName);
        bool owned = Acquire(mutex);
        try
        {
            if (!owned)
                Log.Warn($"等不到 {layout.StateFile} 的鎖，不鎖直接讀");
            var (status, value, _) = JsonFile.TryRead<UpdaterState>(layout.StateFile);
            return status == JsonReadStatus.Ok && value != null ? value : new UpdaterState();
        }
        finally
        {
            if (owned)
                mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// 讀－改－寫（具名 mutex 保護：app 與換版幫手可能同時寫）；回傳寫入後的狀態。
    /// 狀態檔讀不到時丟 IOException、不寫檔（呼叫端照現有的 IOException 處理）
    /// </summary>
    public static UpdaterState Update(InstallLayout layout, Func<UpdaterState, UpdaterState> mutate)
    {
        using var mutex = new Mutex(false, layout.StateMutexName);
        if (!Acquire(mutex))
            throw new TimeoutException("等不到 updater-state.json 的鎖");
        try
        {
            var (status, value, _) = JsonFile.TryRead<UpdaterState>(layout.StateFile);
            if (status == JsonReadStatus.Unreadable)
                throw new IOException($"讀不到 {layout.StateFile}（被占用或沒有權限），不覆寫它");
            var next = mutate(status == JsonReadStatus.Ok && value != null ? value : new UpdaterState());
            Directory.CreateDirectory(layout.Root);
            JsonFile.Write(layout.StateFile, next);
            return next;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>等鎖最多 LockTimeout；上一個持有者當掉（AbandonedMutexException）也算拿到：狀態檔是原子寫入，照常讀</summary>
    private static bool Acquire(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(LockTimeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }
}
