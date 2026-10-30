using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml;
using Myriagent.Core;

namespace Myriagent.Schedules;

/// <summary>工作排程器裡的一個工作（讀取器在背景執行緒讀；Definition 讀不到時 Error 寫原因）</summary>
internal sealed record ScheduledTaskInfo(
    string Path,
    string? Xml,
    ScheduleDefinition? Definition,
    string? Error,
    ScheduledTaskState State,
    bool Enabled,
    DateTime? LastRun,
    int LastResult,
    DateTime? NextRun,
    int MissedRuns,
    string? RepoRoot)
{
    public string Name => Path[(Path.LastIndexOf('\\') + 1)..];

    /// <summary>所在的資料夾（「\」或「\資料夾」）</summary>
    public string Folder => Path.LastIndexOf('\\') is var i and > 0 ? Path[..i] : "\\";
}

/// <summary>工作排程器的 TASK_STATE</summary>
internal enum ScheduledTaskState
{
    Unknown = 0,
    Disabled = 1,
    Queued = 2,
    Ready = 3,
    Running = 4,
}

/// <summary>一次掃描：\Microsoft\ 以外的所有工作</summary>
internal sealed record ScheduleScan(DateTime ScannedAt, IReadOnlyList<ScheduledTaskInfo> Tasks, string? Error, long ElapsedMs, ScheduleEnvironment Environment);

/// <summary>
/// 讀與操作 Windows 的工作排程器（中樞只經這個介面；測試用假的）。全部在背景執行緒呼叫，失敗丟例外（訊息給使用者看）
/// </summary>
internal interface IScheduleReader
{
    ScheduleScan Scan(CancellationToken ct);

    /// <summary>立即執行（不管觸發條件）</summary>
    void Run(string path);

    /// <summary>停止執行中的這個工作</summary>
    void Stop(string path);

    void SetEnabled(string path, bool enabled);
}

/// <summary>
/// 工作排程器的 COM（Schedule.Service）：只讀、一般權限。逐資料夾列出（含隱藏的工作），\Microsoft\ 不進去；
/// 實測（2026-10-26，這台 28 個工作）連線加列出約 110 ms，第二次以後約 6 ms。讀不到的工作（權限不足）記成 Error 照樣列出。
/// 判斷「在不在 git repo」也在這裡做（往上找 .git；UNC、網路磁碟不碰）
/// </summary>
internal sealed class TaskSchedulerReader(IReadOnlyList<string>? workRoots = null) : IScheduleReader
{
    private const int TaskEnumHidden = 1;

    public ScheduleScan Scan(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var env = CurrentEnvironment(workRoots);
        var tasks = new List<ScheduledTaskInfo>();
        string? error = null;
        dynamic? service = null;
        try
        {
            service = Connect();
            var repoCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<dynamic>();
            stack.Push(service.GetFolder("\\"));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                dynamic folder = stack.Pop();
                string folderPath = folder.Path;
                if (ScheduleClassifyRules.IsSystemFolder(folderPath))
                    continue;
                try
                {
                    foreach (dynamic sub in folder.GetFolders(0))
                        stack.Push(sub);
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
                {
                    Log.Info($"排程：讀不到資料夾 {folderPath} 的子資料夾：{ex.Message}");
                }
                try
                {
                    foreach (dynamic task in folder.GetTasks(TaskEnumHidden))
                        tasks.Add(ReadTask(task, env, repoCache));
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
                {
                    Log.Info($"排程：讀不到資料夾 {folderPath} 的工作：{ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("排程：讀取工作排程器失敗", ex);
            error = ex.Message;
        }
        finally
        {
            Release(service);
        }
        return new ScheduleScan(DateTime.Now, tasks.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList(), error, sw.ElapsedMilliseconds, env);
    }

    private static ScheduledTaskInfo ReadTask(dynamic task, ScheduleEnvironment env, Dictionary<string, string?> repoCache)
    {
        string path = task.Path;
        try
        {
            string xml = task.Xml;
            ScheduleDefinition? definition = null;
            string? error = null;
            try
            {
                definition = ScheduleXmlRules.Parse(xml);
            }
            catch (XmlException ex)
            {
                error = "定義讀不懂：" + ex.Message;
            }
            int state = task.State;
            bool enabled = task.Enabled;
            DateTime last = task.LastRunTime;
            int result = task.LastTaskResult;
            DateTime next = task.NextRunTime;
            int missed = task.NumberOfMissedRuns;
            string? repo = null;
            if (definition != null)
            {
                foreach (var p in ScheduleClassifyRules.CandidatePaths(definition.Actions, env))
                {
                    repo = RepoRootOf(p, repoCache);
                    if (repo != null)
                        break;
                }
            }
            return new ScheduledTaskInfo(path, xml, definition, error, (ScheduledTaskState)state, enabled, ValidTime(last), result, ValidTime(next), missed, repo);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return new ScheduledTaskInfo(path, null, null, "讀不到這個工作：" + ex.Message, ScheduledTaskState.Unknown, false, null, 0, null, 0, null);
        }
    }

    /// <summary>COM 的「沒有時間」是 1899-12-30 或 0001-01-01</summary>
    private static DateTime? ValidTime(DateTime t) => t.Year < 2000 ? null : DateTime.SpecifyKind(t, DateTimeKind.Local);

    /// <summary>往上找 .git（資料夾或 worktree 的檔案）；UNC 與網路磁碟不碰（檢查存在就可能對外送 SMB 雜湊）</summary>
    internal static string? RepoRootOf(string path, Dictionary<string, string?> cache)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length < 3 || path[1] != ':')
            return null;
        try
        {
            var drive = new DriveInfo(path[..1]);
            if (drive.DriveType is DriveType.Network or DriveType.NoRootDirectory)
                return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        var dir = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path);
        var visited = new List<string>();
        while (!string.IsNullOrEmpty(dir))
        {
            if (cache.TryGetValue(dir, out var known))
            {
                foreach (var v in visited)
                    cache[v] = known;
                return known;
            }
            visited.Add(dir);
            var git = System.IO.Path.Combine(dir, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                foreach (var v in visited)
                    cache[v] = dir;
                return dir;
            }
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        foreach (var v in visited)
            cache[v] = null;
        return null;
    }

    public void Run(string path) => WithTask(path, task => task.Run(null));

    public void Stop(string path) => WithTask(path, task => task.Stop(0));

    public void SetEnabled(string path, bool enabled) => WithTask(path, task => task.Enabled = enabled);

    private static void WithTask(string path, Action<dynamic> action)
    {
        dynamic? service = null;
        try
        {
            service = Connect();
            int slash = path.LastIndexOf('\\');
            var folderPath = slash > 0 ? path[..slash] : "\\";
            dynamic folder = service.GetFolder(folderPath);
            dynamic task = folder.GetTask(path[(slash + 1)..]);
            action(task);
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005)
        {
            throw new InvalidOperationException("權限不足（這個排程要系統管理員才能改），請交給代理處理", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("權限不足（這個排程要系統管理員才能改），請交給代理處理", ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new InvalidOperationException("找不到這個排程（可能已被刪除）", ex);
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070002)
        {
            throw new InvalidOperationException("找不到這個排程（可能已被刪除）", ex);
        }
        finally
        {
            Release(service);
        }
    }

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com))
            Marshal.FinalReleaseComObject(com);
    }

    /// <summary>這台電腦的使用者與環境變數（判斷廠商位置、展開 %變數%）</summary>
    internal static ScheduleEnvironment CurrentEnvironment(IReadOnlyList<string>? workRoots = null)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k && e.Value is string v)
                vars[k] = v;
        }
        return new ScheduleEnvironment(Environment.UserName, Environment.MachineName, vars, workRoots);
    }
}
