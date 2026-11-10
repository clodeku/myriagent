using Myriagent.Agents;
using Myriagent.Cli;
using System.Text;

namespace Myriagent.Git;

internal sealed record GitTurnFile(string? Text, bool Readable);
internal sealed record GitTurnSnapshot(string Root, string Head, Dictionary<string, GitTurnFile> DirtyFiles, bool Limited);
internal sealed record GitTurnChanges(IReadOnlyList<TurnFileDiff> Files, string Note, IReadOnlySet<string>? ComparedPaths = null);

internal interface IGitTurnReader
{
    Task<GitTurnSnapshot?> CaptureAsync(string cwd, CancellationToken ct);
    Task<GitTurnChanges> CompareAsync(GitTurnSnapshot before, CancellationToken ct);
}

/// <summary>只讀 Git 與工作檔；開始前只暫存已修改的文字，乾淨檔案以原 HEAD 為基準，包含這一輪已提交的變更。</summary>
internal sealed class GitTurnReader : IGitTurnReader
{
    private const int MaxText = 256_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static Task<CliRunResult> Git(string cwd, CancellationToken ct, params string[] args)
        => CliRunner.RunAsync(new AgentLauncher("git", ["-c", "core.fsmonitor=false", "-c", "core.quotepath=false"]), args, env =>
        {
            foreach (var key in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_COMMON_DIR", "GIT_NAMESPACE" }) env.Remove(key);
            env["GIT_OPTIONAL_LOCKS"] = "0";
            env["GIT_TERMINAL_PROMPT"] = "0";
        }, TimeSpan.FromSeconds(10), ct, cwd, maxOutputCharacters: 2_000_000);

    public async Task<GitTurnSnapshot?> CaptureAsync(string cwd, CancellationToken ct)
    {
        var root = await Git(cwd, ct, "rev-parse", "--show-toplevel", "HEAD");
        if (root.ExitCode != 0) return null;
        var parts = root.Stdout.Trim().Split('\n');
        if (parts.Length != 2) return null;
        var folder = parts[0].TrimEnd('\r');
        var head = parts[1].TrimEnd('\r');
        var tracked = await Git(folder, ct, "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", "HEAD", "--");
        var untracked = await Git(folder, ct, "ls-files", "--others", "--exclude-standard", "-z");
        if (tracked.ExitCode != 0 || untracked.ExitCode != 0 || tracked.Stdout.Length > 2_000_000 || untracked.Stdout.Length > 2_000_000) return null;
        var paths = Paths(tracked.Stdout).Concat(Paths(untracked.Stdout)).Distinct(StringComparer.Ordinal).ToList();
        var dirty = new Dictionary<string, GitTurnFile>(StringComparer.Ordinal);
        var total = 0;
        foreach (var path in paths.Take(TurnDiffRules.MaxFiles))
        {
            var file = await ReadFile(folder, path, ct);
            total += file.Text?.Length ?? 0;
            if (total > 2_000_000) return new(folder, head, dirty, true);
            dirty[path] = file;
        }
        return new(folder, head, dirty, paths.Count > TurnDiffRules.MaxFiles);
    }

    public async Task<GitTurnChanges> CompareAsync(GitTurnSnapshot before, CancellationToken ct)
    {
        if (before.Limited) return new([], "開始前的修改超過快照上限，未建立完整 Git 基準；保留 CLI 回報的差異。");
        var patch = await Git(before.Root, ct, "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", before.Head, "--");
        var untracked = await Git(before.Root, ct, "ls-files", "--others", "--exclude-standard", "-z");
        var tracked = await Git(before.Root, ct, "ls-files", "--cached", "-z");
        if (patch.ExitCode != 0 || untracked.ExitCode != 0 || tracked.ExitCode != 0) return new([], "Git 核對失敗，保留 CLI 回報的差異。");
        if (patch.Stdout.Length > 2_000_000 || untracked.Stdout.Length > 2_000_000 || tracked.Stdout.Length > 2_000_000) return new([], "Git 差異超過顯示上限，請在編輯器查看；保留 CLI 回報的差異。");
        var files = TurnDiffRules.Parse(patch.Stdout).Where(f => !before.DirtyFiles.ContainsKey(f.Path)).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var compared = Paths(tracked.Stdout).Concat(files.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        compared.ExceptWith(before.DirtyFiles.Keys);
        var dirtyPaths = before.DirtyFiles.Keys.Concat(Paths(untracked.Stdout)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var path in dirtyPaths.Take(TurnDiffRules.MaxFiles))
        {
            compared.Add(path);
            var old = before.DirtyFiles.GetValueOrDefault(path) ?? new GitTurnFile(null, true);
            var now = await ReadFile(before.Root, path, ct);
            if (old.Readable && now.Readable)
            {
                if (old.Text == now.Text) { files.Remove(path); continue; }
                files[path] = new(path, DiffRules.FromEdit(old.Text, now.Text));
            }
            else files[path] = new(path, [new("hunk", "二進位、大型檔案或無法讀取，未比較內容")]);
        }
        var note = "Git 比對這一輪開始與結束的檔案內容，包含已提交修改；同時由其他程序修改的內容也可能列入。";
        if (dirtyPaths.Count > TurnDiffRules.MaxFiles || files.Count > TurnDiffRules.MaxFiles) note += "僅核對／顯示前 100 個檔案。";
        return new(files.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(), note, compared);
    }

    private static IEnumerable<string> Paths(string text) => text.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static async Task<GitTurnFile> ReadFile(string root, string path, CancellationToken ct)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(root, path));
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return new(null, false);
            if (!File.Exists(full)) return new(null, true);
            var info = new FileInfo(full);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaxText) return new(null, false);
            var bytes = await File.ReadAllBytesAsync(full, ct);
            if (bytes.Length > MaxText || bytes.Contains((byte)0)) return new(null, false);
            return new(StrictUtf8.GetString(bytes).TrimStart('\uFEFF'), true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException) { return new(null, false); }
    }
}
