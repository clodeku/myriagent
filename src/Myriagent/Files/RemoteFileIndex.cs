using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Sync;

namespace Myriagent.Files;

/// <summary>
/// 遠端 @ 檔案提示（fileSearch）專用的檔案索引（WP4.7 計畫 D35）：和本機的 <see cref="FileBrowser"/> 一樣廣度優先、略過建置輸出與版控資料夾、
/// 最多 2 萬筆、12 層，但
/// <list type="bullet">
/// <item>帶重新解析屬性的項目只有雲端檔案的佔位檔（OneDrive）才收（<c>allowReparse</c>，正式是 <see cref="RemoteFileGuard.IsAllowedReparsePoint"/>）：
/// junction、符號連結背後的檔不會出現，也不會走進去。</item>
/// <item>禁止的資料夾（<c>excludeDirectory</c>，呼叫端以 RemoteFileGuardRules 判斷）不走進去，拒絕清單的檔名（憑證、金鑰、設定檔）不收。</item>
/// <item>快取是自己的（不和 <see cref="FileBrowser"/> 共用：本機的索引會跟進 junction），60 秒內重用、最多 <see cref="MaxCachedIndexes"/> 個根目錄。</item>
/// </list>
/// 遠端 listFiles 的列目錄（<see cref="ListDirectory"/>）也在這裡，略過重新解析點的規則相同。
/// 會碰檔案系統：<see cref="SearchAsync"/>、<see cref="ListDirectory"/> 要在背景執行緒呼叫
/// </summary>
internal sealed class RemoteFileIndex
{
    /// <summary>最多快取幾個根目錄的索引</summary>
    public const int MaxCachedIndexes = 8;

    private static readonly TimeSpan IndexLifetime = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> DotNetProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sln", ".slnx", ".csproj", ".fsproj", ".vbproj",
    };

    private readonly Func<string, bool> allowReparse;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<string, (long BuiltAt, IReadOnlyList<string> Files)> indexes = new(StringComparer.OrdinalIgnoreCase);
    private int builds;

    /// <param name="allowReparse">帶重新解析屬性的項目能不能收（參數是完整路徑；null＝<see cref="RemoteFileGuard.IsAllowedReparsePoint"/>）</param>
    /// <param name="time">快取的時鐘（null＝系統時鐘）</param>
    public RemoteFileIndex(Func<string, bool>? allowReparse = null, TimeProvider? time = null)
    {
        this.allowReparse = allowReparse ?? RemoteFileGuard.IsAllowedReparsePoint;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>快取著幾個根目錄（測試）</summary>
    internal int CachedIndexCount => indexes.Count;

    /// <summary>建過幾次索引（測試確認 60 秒內重用）</summary>
    internal int Builds => Volatile.Read(ref builds);

    /// <summary>
    /// 在 <paramref name="root"/>（守門員驗過的真實路徑）的索引裡模糊搜尋，最多 <see cref="FileBrowser.MaxResults"/> 筆（排序同本機的 @ 提示）。
    /// 索引超過 60 秒就同步重建（呼叫端本來就在背景）
    /// </summary>
    /// <param name="excludeDirectory">不走進去的資料夾（參數是完整路徑；禁止清單）</param>
    public Task<List<FileSearchResult>> SearchAsync(string root, string query, Func<string, bool> excludeDirectory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(excludeDirectory);
        var files = GetIndex(root, excludeDirectory, ct);
        return Task.FromResult(FileBrowser.Search(files, root, query ?? string.Empty, FileBrowser.MaxResults));
    }

    private IReadOnlyList<string> GetIndex(string root, Func<string, bool> excludeDirectory, CancellationToken ct)
    {
        var now = time.GetTimestamp();
        if (indexes.TryGetValue(root, out var cached) && time.GetElapsedTime(cached.BuiltAt, now) < IndexLifetime)
            return cached.Files;

        var files = BuildIndex(root, allowReparse, excludeDirectory, ct);
        Interlocked.Increment(ref builds);
        indexes[root] = (time.GetTimestamp(), files);
        while (indexes.Count > MaxCachedIndexes)
        {
            var oldest = indexes.Where(kv => !string.Equals(kv.Key, root, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Value.BuiltAt)
                .Select(kv => kv.Key)
                .FirstOrDefault();
            if (oldest is null || !indexes.TryRemove(oldest, out _))
                break;
        }

        return files;
    }

    /// <summary>
    /// 遠端 listFiles 列一個資料夾（WP4.7 計畫 D33；<paramref name="directory"/> 是守門員驗過的真實路徑）：同本機的 <see cref="FileBrowser.List"/>
    /// （資料夾在前、自然排序、最多 <see cref="FileBrowser.MaxEntries"/> 筆、略過 System 屬性），另外略過帶重新解析屬性、<paramref name="allowReparse"/> 不放行的項目
    /// （junction、符號連結；OneDrive 佔位檔放行）。<paramref name="dirsOnly"/> 時只列資料夾。讀不到丟 <see cref="IOException"/>／<see cref="UnauthorizedAccessException"/>；
    /// 會碰檔案系統，只能在背景執行緒呼叫
    /// </summary>
    internal static (List<FileEntry> Entries, bool Truncated) ListDirectory(string directory, Func<string, bool> allowReparse, bool dirsOnly)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(allowReparse);
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
            throw new DirectoryNotFoundException("找不到資料夾");

        // IgnoreInaccessible 要關掉：不遞迴時它只作用在要列的這個資料夾，沒有權限會變成「空資料夾」而不是錯誤
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.System, RecurseSubdirectories = false };
        IEnumerable<FileSystemInfo> items = info.EnumerateDirectories("*", options);
        if (!dirsOnly)
            items = items.Concat<FileSystemInfo>(info.EnumerateFiles("*", options));
        var entries = new List<FileEntry>();
        var truncated = false;
        foreach (var item in items)
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0 && !allowReparse(item.FullName))
                continue;
            if (entries.Count >= FileBrowser.MaxEntries)
            {
                truncated = true;
                break;
            }

            var isDirectory = (item.Attributes & FileAttributes.Directory) != 0;
            var hidden = (item.Attributes & FileAttributes.Hidden) != 0 || item.Name.StartsWith('.');
            entries.Add(new FileEntry(
                item.Name,
                item.FullName,
                isDirectory ? "dir" : AgentAttachmentRules.ClassifyForBrowser(item.Name),
                isDirectory ? null : (item as FileInfo)?.Length,
                item.LastWriteTime,
                hidden));
        }

        entries.Sort((a, b) =>
        {
            var order = (a.Kind == "dir" ? 0 : 1).CompareTo(b.Kind == "dir" ? 0 : 1);
            return order != 0 ? order : FileBrowser.NaturalCompare(a.Name, b.Name);
        });
        return (entries, truncated);
    }

    /// <summary>廣度優先列出檔案（相對 <paramref name="root"/> 的路徑）；規則見類別說明</summary>
    internal static List<string> BuildIndex(string root, Func<string, bool> allowReparse, Func<string, bool> excludeDirectory, CancellationToken ct)
    {
        var result = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System, RecurseSubdirectories = false };
        while (queue.Count > 0 && result.Count < FileBrowser.MaxIndexedFiles)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            try
            {
                var dotNetProject = false;
                var subdirectories = new List<string>();
                foreach (var item in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0 && !allowReparse(item.FullName))
                        continue;
                    if ((item.Attributes & FileAttributes.Directory) != 0)
                    {
                        subdirectories.Add(item.FullName);
                        continue;
                    }

                    dotNetProject |= DotNetProjectExtensions.Contains(Path.GetExtension(item.Name));
                    if (RemoteFileGuardRules.IsForbiddenFileName(item.FullName))
                        continue;
                    result.Add(Path.GetRelativePath(root, item.FullName));
                    if (result.Count >= FileBrowser.MaxIndexedFiles)
                        break;
                }

                if (depth >= FileBrowser.MaxDepth)
                    continue;
                foreach (var sub in subdirectories)
                {
                    if (!FileBrowser.IsIgnoredDirectory(Path.GetFileName(sub), dotNetProject) && !excludeDirectory(sub))
                        queue.Enqueue((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }
}
