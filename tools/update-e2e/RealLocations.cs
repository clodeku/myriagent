using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace MyriagentTools.UpdateE2e;

/// <summary>
/// 這台電腦真正的安裝與資料（端到端絕不能碰）：%LOCALAPPDATA%\Programs\Myriagent、%LOCALAPPDATA%\Myriagent、
/// 開始選單捷徑、HKCU 的移除項目與開機時自動啟動（Run、StartupApproved\Run 的值）。開始前記下現況，結束時比對；SelfInstaller.Uninstall 不分端到端（會刪真正的捷徑與移除項目），
/// 所以本工具絕不執行 --uninstall。
/// 安裝位置、捷徑與移除項目平常不會變，整份比對（大小、修改時間、junction 目標、登錄值）；app 資料夾可能正被使用者自己的 app 使用
/// （日誌、設定、對話一直在變），只比對端到端的 app 誤用它時一定會留下的痕跡：backups\、update\，以及測試版本的 build 在日誌裡出現的次數
/// </summary>
internal sealed class RealLocations
{
    /// <summary>同 SelfInstaller.UninstallKey</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Myriagent";

    /// <summary>同 AutoStartRegistration 的 Run 與 StartupApproved\Run 機碼、AutoStartRules.ValueName</summary>
    private static readonly string[] AutoStartKeys =
    {
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
    };

    private const string AutoStartValueName = "Myriagent";

    private const string Missing = "（不存在）";

    private RealLocations(IReadOnlyDictionary<string, IReadOnlyList<string>> parts)
    {
        Parts = parts;
    }

    public static string InstallRoot => Path.Combine(LocalAppData, "Programs", "Myriagent");

    public static string DataRoot => Path.Combine(LocalAppData, "Myriagent");

    /// <summary>同 SelfInstaller.StartMenuShortcutPath</summary>
    public static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Clode Myriagent.lnk");

    /// <summary>每一項的指紋（文字行，順序固定）</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Parts { get; }

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// 資料夾和真正的安裝位置或 app 資料夾重疊（同一個、在裡面、或包含它們）：工作資料夾不能是這種位置，
    /// 不然暫存的安裝會落在真正的位置上，通過後刪工作資料夾也會連它們一起刪
    /// </summary>
    public static bool Overlaps(string dir)
    {
        var full = WithSeparator(dir);
        return new[] { InstallRoot, DataRoot }
            .Select(WithSeparator)
            .Any(real => full.StartsWith(real, StringComparison.OrdinalIgnoreCase) || real.StartsWith(full, StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="testBuilds">測試版本的 build（e2e0001…）：在真正的日誌裡出現的次數不能變</param>
    public static RealLocations Capture(IReadOnlyList<string> testBuilds)
    {
        var logs = E2eInstall.ReadLogs(Path.Combine(DataRoot, "logs"));
        return new RealLocations(new Dictionary<string, IReadOnlyList<string>>
        {
            ["安裝位置"] = Tree(InstallRoot),
            ["開始選單捷徑"] = FileFingerprint(StartMenuShortcut),
            ["移除項目"] = UninstallEntry(),
            ["開機時自動啟動"] = AutoStartEntries(),
            [@"app 資料夾的 backups\"] = Tree(Path.Combine(DataRoot, "backups")),
            [@"app 資料夾的 update\"] = Tree(Path.Combine(DataRoot, "update")),
            ["app 日誌裡的測試版本"] = testBuilds.Select(build => $"{build}：{E2eInstall.CountOccurrences(logs, build)} 次").ToList(),
        });
    }

    /// <summary>和 before 不同的項目名稱（沒有變動為空）</summary>
    public IReadOnlyList<string> ChangedSince(RealLocations before)
        => Parts.Keys.Where(key => !before.Parts.TryGetValue(key, out var old) || !old.SequenceEqual(Parts[key], StringComparer.Ordinal)).ToList();

    private static string WithSeparator(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar;

    /// <summary>資料夾底下每個項目的相對路徑、大小、修改時間；junction（重解析點）只記目標，不走進去</summary>
    private static IReadOnlyList<string> Tree(string root)
    {
        if (!Directory.Exists(root))
            return new[] { Missing };
        var lines = new List<string>();
        void Walk(DirectoryInfo dir)
        {
            foreach (var entry in dir.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(root, entry.FullName);
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    lines.Add($"{relative} -> {entry.LinkTarget}");
                }
                else if (entry is DirectoryInfo sub)
                {
                    lines.Add(relative + Path.DirectorySeparatorChar);
                    Walk(sub);
                }
                else if (entry is FileInfo file)
                {
                    lines.Add($"{relative} {file.Length} {file.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)}");
                }
            }
        }
        Walk(new DirectoryInfo(root));
        return lines;
    }

    private static IReadOnlyList<string> FileFingerprint(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            return new[] { Missing };
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new[] { $"{file.Length} {file.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)} {sha}" };
    }

    /// <summary>HKCU 移除項目的每個值與子機碼（只讀）</summary>
    private static IReadOnlyList<string> UninstallEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: false);
        if (key == null)
            return new[] { Missing };
        return key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => $"{name}={Convert.ToString(key.GetValue(name), CultureInfo.InvariantCulture)}")
            .Concat(key.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase).Select(sub => sub + @"\"))
            .ToList();
    }

    /// <summary>HKCU 的 Run 與 StartupApproved\Run 底下 app 的值（只讀；二進位值以十六進位記）</summary>
    private static IReadOnlyList<string> AutoStartEntries()
        => AutoStartKeys.Select(path =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            var value = key?.GetValue(AutoStartValueName);
            var text = value switch
            {
                null => Missing,
                byte[] bytes => Convert.ToHexString(bytes),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            };
            return $"{path}={text}";
        }).ToList();
}
