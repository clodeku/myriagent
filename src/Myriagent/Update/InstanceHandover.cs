using System.Diagnostics;
using Myriagent.Core;
using Newtonsoft.Json;

namespace Myriagent.Update;

/// <summary>
/// 新版自動接手舊版的檔案（InstanceHandoverRules 的 I/O）：&lt;資料夾&gt;\update\instance.json（正在執行的實例，懂交棒）與
/// handover-request.json（較新的版本請它交棒）。兩個都是暫時的：讀不到、壞掉一律當成沒有，不備份、不提示
/// </summary>
internal static class InstanceHandover
{
    public static string RunningPath(string dataRoot) => Path.Combine(dataRoot, "update", "instance.json");

    public static string RequestPath(string dataRoot) => Path.Combine(dataRoot, "update", "handover-request.json");

    /// <summary>拿到單一實例之後：記下自己（失敗只記錄，交棒只是做不到，app 照常）</summary>
    public static void MarkRunning(string dataRoot, ReleaseIdentity self)
    {
        try
        {
            JsonFile.Write(RunningPath(dataRoot), new RunningInstanceFile
            {
                Pid = Environment.ProcessId,
                Release = self.Release,
                Executable = Environment.ProcessPath ?? string.Empty,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"寫 instance.json 失敗：{ex.Message}");
        }
    }

    /// <summary>結束時（開始結束就刪：結束中的實例不再接交棒）：只刪自己寫的</summary>
    public static void ClearRunning(string dataRoot)
    {
        var path = RunningPath(dataRoot);
        if (TryRead<RunningInstanceFile>(path) is { } file && file.Pid != Environment.ProcessId)
            return;
        TryDelete(path);
    }

    /// <summary>對方的 instance.json：程序還在、執行檔相同才算（當掉留下的舊檔不算）；沒有時等一下（對方可能剛拿到 mutex）</summary>
    public static RunningInstanceFile? ReadRunning(string dataRoot, TimeSpan grace)
    {
        var deadline = DateTime.UtcNow + grace;
        while (true)
        {
            if (TryRead<RunningInstanceFile>(RunningPath(dataRoot)) is { } file && file.Pid != Environment.ProcessId)
                return IsAlive(file) ? file : null;
            if (DateTime.UtcNow >= deadline)
                return null;
            Thread.Sleep(100);
        }
    }

    /// <summary>新版：寫交棒請求（之後由 SingleInstance 送訊號、等對方確認）</summary>
    public static bool WriteRequest(string dataRoot, ReleaseIdentity self)
    {
        if (Environment.ProcessPath is not { Length: > 0 } exe)
            return false;
        try
        {
            JsonFile.Write(RequestPath(dataRoot), new HandoverRequestFile
            {
                Executable = exe,
                Release = self.Release,
                Pid = Environment.ProcessId,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"寫交棒請求失敗：{ex.Message}");
            return false;
        }
    }

    /// <summary>舊版：讀出並刪掉交棒請求（每個請求只處理一次）</summary>
    public static HandoverRequestFile? TakeRequest(string dataRoot)
    {
        var path = RequestPath(dataRoot);
        var request = TryRead<HandoverRequestFile>(path);
        TryDelete(path);
        return request;
    }

    /// <summary>新版啟動時：這個資料夾已經有實例，要不要請它交棒（寫好請求回 true）；對方沒留 instance.json 時記下「要說明」</summary>
    public static bool Prepare(string dataRoot, ReleaseIdentity self, out LaunchDecision decision)
    {
        decision = InstanceHandoverRules.Decide(self.Version, ReadRunning(dataRoot, InstanceHandoverRules.RunningFileGrace));
        if (decision != LaunchDecision.RequestHandover)
            return false;
        if (WriteRequest(dataRoot, self))
            return true;
        decision = LaunchDecision.Activate;
        return false;
    }

    private static bool IsAlive(RunningInstanceFile file)
    {
        try
        {
            using var process = Process.GetProcessById(file.Pid);
            if (process.HasExited)
                return false;
            var path = process.MainModule?.FileName;
            // 同一個 pid 被別的程式重用（當掉留下的舊檔）：執行檔名稱不同就不算。只比名稱：經 current junction 啟動的路徑寫法可能不同
            return string.IsNullOrEmpty(path) || string.IsNullOrEmpty(file.Executable)
                   || string.Equals(Path.GetFileName(path), Path.GetFileName(file.Executable), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;   // 沒有這個程序
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or IOException)
        {
            return true;   // 看不到細節（權限）：程序在，就當成是它
        }
    }

    private static T? TryRead<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonConvert.DeserializeObject<T>(File.ReadAllText(path), JsonFile.Settings) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"刪 {Path.GetFileName(path)} 失敗：{ex.Message}");
        }
    }
}
