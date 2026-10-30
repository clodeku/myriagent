using System.Text;

namespace MyriagentHub.Hosting;

/// <summary>
/// status\service.lock：伺服器執行期間以 FileShare.None 開著。同一個資料根只能有一個伺服器實例；
/// admin restore 拿得到這個鎖才代表服務已停止（還原絕不在服務執行中改資料庫檔）。程序結束（含被殺）時系統自動釋放。
/// </summary>
internal sealed class ServiceLock : IDisposable
{
    private readonly FileStream stream;

    private ServiceLock(FileStream stream)
    {
        this.stream = stream;
    }

    public static ServiceLock? TryAcquire(HubPaths paths, string holder)
    {
        Directory.CreateDirectory(paths.StatusDir);
        try
        {
            var stream = new FileStream(paths.ServiceLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            stream.SetLength(0);
            stream.Write(Encoding.UTF8.GetBytes($"{holder} pid={Environment.ProcessId} at={DateTimeOffset.UtcNow:O}\n"));
            stream.Flush(flushToDisk: true);
            return new ServiceLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ServiceLock Acquire(HubPaths paths, string holder) =>
        TryAcquire(paths, holder) ?? throw new InvalidOperationException($"資料根 {paths.Root} 已有另一個 MyriagentHub 在使用（{paths.ServiceLockFile} 被鎖住）");

    public void Dispose() => stream.Dispose();
}
