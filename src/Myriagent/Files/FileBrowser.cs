using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Myriagent.Agents;

namespace Myriagent.Files;

/// <summary>檔案瀏覽器的一個項目</summary>
internal sealed record FileEntry(string Name, string Path, string Kind, long? Size, DateTime Modified, bool Hidden);

/// <summary>@ 檔案提示的一筆結果</summary>
internal sealed record FileSearchResult(string RelativePath, string FullPath, string Kind, int Score);

/// <summary>
/// 對話內的檔案瀏覽與 @ 檔案提示：列資料夾（資料夾在前、自然排序）、在工作資料夾建檔案索引（背景執行緒、快取 60 秒、略過 .git／node_modules／obj 等，
/// bin／packages 只在 .NET 專案旁略過），以子序列模糊比對排序。只認本機磁碟路徑（UNC 會對外發 SMB 連線，一律不列）。
/// </summary>
internal static class FileBrowser
{
    public const int MaxEntries = 3000;
    public const int MaxIndexedFiles = 20_000;
    public const int MaxDepth = 12;
    public const int MaxResults = 30;

    private static readonly TimeSpan IndexLifetime = TimeSpan.FromSeconds(60);

    /// <summary>建索引時一律略過的資料夾（建置輸出、套件、版控）</summary>
    public static readonly IReadOnlySet<string> IgnoredDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "obj", ".vs", ".idea", "__pycache__", ".venv", "venv",
        ".next", ".nuxt", ".cache", "TestResults", ".gradle", "target", ".pytest_cache", ".mypy_cache",
    };

    /// <summary>
    /// 只在 .NET 專案旁才略過的資料夾：bin 是 MSBuild 的建置輸出、packages 是舊版 NuGet 的套件資料夾（放在 .sln 旁）。
    /// 其他專案的同名資料夾是原始碼（JS／TS monorepo 的 packages/**、Node／Ruby 的 bin/ 腳本）
    /// </summary>
    public static readonly IReadOnlySet<string> DotNetOutputDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "packages",
    };

    private static readonly HashSet<string> DotNetProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sln", ".slnx", ".csproj", ".fsproj", ".vbproj",
    };

    private sealed record IndexEntry(DateTime BuiltAt, IReadOnlyList<string> Files, Task? Building);

    private static readonly ConcurrentDictionary<string, IndexEntry> Indexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最多快取幾個資料夾的索引（每個最多 2 萬筆路徑；常駐很久、用過很多工作資料夾時不會一直累積）</summary>
    public const int MaxCachedIndexes = 8;

    internal static int CachedIndexCount => Indexes.Count;

    /// <summary>超過上限時移除最久沒重建的索引（使用中的索引每分鐘會重建一次，最久沒重建的就是最久沒用的）</summary>
    private static void TrimIndexes(string keep)
    {
        while (Indexes.Count > MaxCachedIndexes)
        {
            var oldest = Indexes
                .Where(kv => kv.Value.Building == null && !string.Equals(kv.Key, keep, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Value.BuiltAt)
                .Select(kv => kv.Key)
                .FirstOrDefault();
            if (oldest == null || !Indexes.TryRemove(oldest, out _))
                return;
        }
    }

    /// <summary>列出資料夾內容</summary>
    public static (List<FileEntry> Entries, bool Truncated) List(string directory)
    {
        if (!AgentAttachmentRules.IsLocalDrivePath(directory))
            throw new UnauthorizedAccessException("只能瀏覽這台電腦磁碟上的資料夾");
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
            throw new DirectoryNotFoundException($"找不到資料夾：{directory}");

        var entries = new List<FileEntry>();
        bool truncated = false;
        // IgnoreInaccessible 要關掉：不遞迴時它只作用在要列的這個資料夾，沒有權限會變成「空資料夾」而不是錯誤
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.System, RecurseSubdirectories = false };
        try
        {
            // 先列資料夾再列檔案，滿了才截斷：檔案系統依名稱順序列舉、不分檔案與資料夾，混著截斷的話排在後面的子資料夾整個不見
            foreach (var item in info.EnumerateDirectories("*", options).Concat<FileSystemInfo>(info.EnumerateFiles("*", options)))
            {
                if (entries.Count >= MaxEntries)
                {
                    truncated = true;
                    break;
                }
                bool isDir = (item.Attributes & FileAttributes.Directory) != 0;
                bool hidden = (item.Attributes & FileAttributes.Hidden) != 0 || item.Name.StartsWith('.');
                entries.Add(new FileEntry(
                    item.Name,
                    item.FullName,
                    isDir ? "dir" : AgentAttachmentRules.ClassifyForBrowser(item.Name),
                    isDir ? null : (item as FileInfo)?.Length,
                    item.LastWriteTime,
                    hidden));
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"沒有權限讀取這個資料夾：{directory}", ex);
        }

        entries.Sort((a, b) =>
        {
            int dirOrder = (a.Kind == "dir" ? 0 : 1).CompareTo(b.Kind == "dir" ? 0 : 1);
            return dirOrder != 0 ? dirOrder : NaturalCompare(a.Name, b.Name);
        });
        return (entries, truncated);
    }

    /// <summary>上一層資料夾；磁碟根目錄回 null</summary>
    public static string? GetParent(string directory)
    {
        // 不能先 TrimEnd：「C:\」去掉反斜線會變成磁碟相對路徑「C:」（＝目前目錄），上一層就跑到別的地方
        try
        {
            return new DirectoryInfo(Path.GetFullPath(directory)).Parent?.FullName;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>自然排序（檔名裡的數字依數值比：img2 在 img10 前面）</summary>
    public static int NaturalCompare(string? a, string? b) => StrCmpLogicalW(a ?? string.Empty, b ?? string.Empty);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);

    /// <summary>@ 檔案提示：在根目錄的檔案索引中模糊搜尋（索引過期就在背景重建，這次先用舊的）</summary>
    public static async Task<List<FileSearchResult>> SearchAsync(string root, string query, CancellationToken ct)
    {
        if (!AgentAttachmentRules.IsLocalDrivePath(root) || !Directory.Exists(root))
            return new List<FileSearchResult>();
        var files = await GetIndexAsync(root, ct);
        return Search(files, root, query, MaxResults);
    }

    private static async Task<IReadOnlyList<string>> GetIndexAsync(string root, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (Indexes.TryGetValue(root, out var entry))
        {
            if (now - entry.BuiltAt > IndexLifetime && entry.Building == null)
            {
                var rebuild = Task.Run(() => BuildIndex(root, CancellationToken.None), CancellationToken.None);
                Indexes[root] = entry with { Building = rebuild };
                _ = rebuild.ContinueWith(t =>
                {
                    if (t.Status != TaskStatus.RanToCompletion)
                        return;
                    Indexes[root] = new IndexEntry(DateTime.UtcNow, t.Result, null);
                    TrimIndexes(root);
                }, TaskScheduler.Default);
            }
            if (entry.Files.Count > 0 || entry.Building == null)
                return entry.Files;
        }

        var files = await Task.Run(() => BuildIndex(root, ct), ct);
        Indexes[root] = new IndexEntry(DateTime.UtcNow, files, null);
        TrimIndexes(root);
        return files;
    }

    /// <summary>廣度優先列出檔案（相對路徑），略過忽略清單與隱藏的系統資料夾</summary>
    internal static List<string> BuildIndex(string root, CancellationToken ct)
    {
        var result = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System, RecurseSubdirectories = false };
        while (queue.Count > 0 && result.Count < MaxIndexedFiles)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            try
            {
                bool dotNetProject = false;
                foreach (var file in Directory.EnumerateFiles(dir, "*", options))
                {
                    dotNetProject |= DotNetProjectExtensions.Contains(Path.GetExtension(file));
                    result.Add(Path.GetRelativePath(root, file));
                    if (result.Count >= MaxIndexedFiles)
                        break;
                }
                if (depth >= MaxDepth)
                    continue;
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
                {
                    if (!IsIgnoredDirectory(Path.GetFileName(sub), dotNetProject))
                        queue.Enqueue((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return result;
    }

    /// <summary>建索引時要不要略過這個子資料夾；parentIsDotNetProject：上一層有 .sln、.csproj 這類 .NET 方案或專案檔</summary>
    internal static bool IsIgnoredDirectory(string name, bool parentIsDotNetProject)
        => IgnoredDirectories.Contains(name) || (parentIsDotNetProject && DotNetOutputDirectories.Contains(name));

    /// <summary>模糊搜尋排序（純函式）：檔名命中優先、連續命中與單字開頭加分、路徑短的優先</summary>
    internal static List<FileSearchResult> Search(IReadOnlyList<string> relativePaths, string root, string query, int max)
    {
        var q = (query ?? string.Empty).Trim().Replace('/', '\\');
        var scored = new List<FileSearchResult>();
        foreach (var rel in relativePaths)
        {
            int score;
            if (q.Length == 0)
            {
                score = 1000 - Math.Min(999, rel.Length);
            }
            else
            {
                var s = Score(q, rel);
                if (s <= 0)
                    continue;
                score = s;
            }
            scored.Add(new FileSearchResult(rel, Path.Combine(root, rel), AgentAttachmentRules.ClassifyForBrowser(rel), score));
        }
        return scored
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.RelativePath.Length)
            .ThenBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    /// <summary>子序列比對分數；不命中回 0</summary>
    internal static int Score(string query, string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
            return 10_000 - path.Length;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 8_000 - path.Length;
        int nameIndex = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (nameIndex >= 0)
            return 6_000 - nameIndex * 10 - path.Length;
        int pathIndex = path.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (pathIndex >= 0)
            return 4_000 - pathIndex - path.Length;

        // 子序列：每個字元依序出現；連續命中、緊接在分隔字元後面加分
        int score = 0, qi = 0, streak = 0;
        for (int i = 0; i < path.Length && qi < query.Length; i++)
        {
            if (char.ToLowerInvariant(path[i]) != char.ToLowerInvariant(query[qi]))
            {
                streak = 0;
                continue;
            }
            score += 10 + streak * 5;
            if (i == 0 || path[i - 1] is '\\' or '/' or '_' or '-' or '.' or ' ')
                score += 15;
            streak++;
            qi++;
        }
        return qi == query.Length ? Math.Max(1, score + 1_000 - path.Length) : 0;
    }
}
