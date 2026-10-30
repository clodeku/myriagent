using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Launch;

/// <summary>在工作資料夾找可以執行的視窗程式專案（中樞在背景執行緒呼叫；測試換成假的）</summary>
internal interface ILaunchScanner
{
    /// <summary>依 <see cref="LaunchRules.Order"/> 排好；不是本機磁碟、資料夾不存在、沒有專案回空清單（不丟例外）</summary>
    Task<IReadOnlyList<LaunchTarget>> ScanAsync(string folder, CancellationToken ct);
}

/// <summary>
/// 只讀：從工作資料夾往下最多 <see cref="LaunchRules.MaxDepth"/> 層、最多 <see cref="LaunchRules.MaxDirectories"/> 個資料夾，
/// 讀 .csproj／.fsproj／.vbproj 與 package.json（各最多 1 MB）。不跟重新解析點、跳過建置輸出與套件資料夾（<see cref="LaunchRules.ShouldDescend"/>）；
/// UNC 與網路磁碟不讀（光是列目錄就會對外送 SMB 雜湊、也可能很慢）
/// </summary>
internal sealed class LaunchScanner : ILaunchScanner
{
    public Task<IReadOnlyList<LaunchTarget>> ScanAsync(string folder, CancellationToken ct) => Task.Run(() => Scan(folder, ct), ct);

    internal static IReadOnlyList<LaunchTarget> Scan(string folder, CancellationToken ct)
    {
        if (!AgentAttachmentRules.IsLocalDrivePath(folder) || IsNetworkDrive(folder) || !Directory.Exists(folder))
            return [];
        var found = new List<LaunchTarget>();
        var queue = new Queue<(DirectoryInfo Dir, int Depth)>();
        queue.Enqueue((new DirectoryInfo(folder), 0));
        var visited = 0;
        while (queue.Count > 0 && visited < LaunchRules.MaxDirectories)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            visited++;
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo sub)
                {
                    if (depth < LaunchRules.MaxDepth && LaunchRules.ShouldDescend(sub.Name, SafeAttributes(sub)))
                        queue.Enqueue((sub, depth + 1));
                }
                else if (entry is FileInfo file && LaunchRules.IsCandidateFile(file.Name) && TryRead(file) is { } text)
                {
                    var relative = Path.GetRelativePath(folder, file.FullName);
                    var target = LaunchRules.IsDotNetProjectFile(file.Name)
                        ? LaunchRules.FromDotNetProject(file.FullName, relative, text)
                        : LaunchRules.FromPackageJson(file.FullName, relative, text);
                    if (target != null)
                        found.Add(target);
                }
            }
        }
        return LaunchRules.Order(found);
    }

    private static FileAttributes SafeAttributes(FileSystemInfo info)
    {
        try
        {
            return info.Attributes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 讀不到屬性的當成重新解析點（不跟）
            return FileAttributes.ReparsePoint;
        }
    }

    private static string? TryRead(FileInfo file)
    {
        try
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > LaunchRules.MaxProjectFileBytes)
                return null;
            return File.ReadAllText(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool IsNetworkDrive(string folder)
    {
        try
        {
            return Path.GetPathRoot(folder) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            Log.Info($"判斷磁碟種類失敗（{folder}）：{ex.Message}");
            return true;
        }
    }
}
