using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Myriagent.Update;

namespace MyriagentTools.UpdateE2e;

/// <summary>工作資料夾裡的受管理安裝、app 資料夾與本機來源：設定子程序環境、讀 updater-state.json、current 的指向、app 日誌、執行中的 app</summary>
internal sealed class E2eInstall
{
    public E2eInstall(string workDir)
    {
        // 與 app 的 InstallLayout.Root 同樣正規化（完整路徑、去掉結尾斜線）：StateMutexName 由它算出
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(workDir, "Programs", "Myriagent")));
        DataRoot = Path.Combine(workDir, "data");
        SourceDir = Path.Combine(workDir, "source");
    }

    public string Root { get; }

    public string DataRoot { get; }

    public string SourceDir { get; }

    public string Current => Path.Combine(Root, "current");

    public string CurrentExe => Path.Combine(Current, "Myriagent.exe");

    public string VersionDir(string release) => Path.Combine(Root, "versions", release);

    public string PartialVersionDir(string release) => Path.Combine(Root, "versions", ".partial-" + release);

    /// <summary>模擬這個 PID 的 app 的 .NET 已被移除（app 的 UpdateEnvironment.E2ERuntimeRemoved 讀這個檔；只對那個 PID 生效）</summary>
    public void MarkRuntimeRemoved(int pid, string version)
        => File.WriteAllText(Path.Combine(Root, "e2e-runtime-removed.txt"), $"{pid} {version}");

    public void ClearRuntimeRemoved() => File.Delete(Path.Combine(Root, "e2e-runtime-removed.txt"));

    /// <summary>
    /// app 與換版幫手讀改寫 updater-state.json 時拿的具名 mutex（同 InstallLayout.StateMutexName，兩邊要一起改）。
    /// 讀狀態也要拿：app 以「取代」換檔，這邊開著檔案時取代會失敗（拒絕存取），換版幫手記錄退回、換版的那一步就會失敗
    /// </summary>
    private string StateMutexName => @"Local\Myriagent.UpdaterState." + ReleaseManifestRules.Sha256Hex(Encoding.UTF8.GetBytes(Root.ToUpperInvariant()))[..12];

    /// <summary>所有 app 子程序（含換版幫手與它啟動的新版）都從這裡繼承環境</summary>
    public void ApplyEnvironment(ProcessStartInfo info, string crashRelease)
    {
        info.Environment["MYRIAGENT_UPDATE_E2E"] = "1";
        info.Environment["MYRIAGENT_UPDATE_E2E_ROOT"] = Root;
        info.Environment["MYRIAGENT_UPDATE_E2E_SOURCE"] = SourceDir;
        info.Environment["MYRIAGENT_UPDATE_E2E_FAST"] = "1";
        info.Environment["MYRIAGENT_UPDATE_E2E_CRASH_RELEASE"] = crashRelease;
        info.Environment["MYRIAGENT_AGENT_DATA"] = DataRoot;
    }

    /// <summary>app 資料夾先寫好設定：不自動更新 CLI（不動這台電腦的 CLI 安裝）、不跳系統通知、關視窗就結束</summary>
    public void SeedData()
    {
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(Path.Combine(DataRoot, "settings.json"), """{ "AutoUpdateCli": false, "Notifications": false, "CloseToTray": false }""", new UTF8Encoding(false));
    }

    /// <summary>current 指到哪個 release（不是 junction、或指到 versions 以外時回 null）</summary>
    public string? CurrentTarget()
    {
        if (!Directory.Exists(Current))
            return null;
        var target = new DirectoryInfo(Current).LinkTarget;
        if (target == null)
            return null;
        if (target.StartsWith(@"\??\", StringComparison.Ordinal))
            target = target[4..];
        var full = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(Path.GetDirectoryName(full), Path.Combine(Root, "versions"), StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(full) : null;
    }

    /// <summary>updater-state.json（在 app 的 mutex 裡讀；等不到鎖就不鎖直接讀，同 UpdaterStateStore.Load。讀到一半被換掉就重試）</summary>
    public JsonObject State()
    {
        var path = Path.Combine(Root, "updater-state.json");
        using var mutex = new Mutex(false, StateMutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            owned = true;   // 上一個持有者當掉：狀態檔是原子寫入，照常讀
        }
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                        return new JsonObject();
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    return JsonNode.Parse(stream)?.AsObject() ?? new JsonObject();
                }
                catch (Exception ex) when ((ex is IOException or JsonException) && attempt < 10)
                {
                    Thread.Sleep(100);
                }
            }
        }
        finally
        {
            if (owned)
                mutex.ReleaseMutex();
        }
    }

    public static string? Str(JsonObject state, string key)
        => state[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    /// <summary>任何型別的欄位轉成文字（LastCheckAt 這種只拿來比較有沒有變）</summary>
    public static string Raw(JsonObject state, string key) => state[key]?.ToJsonString() ?? "null";

    public static bool InList(JsonObject state, string key, string value)
        => state[key] is JsonArray list && list.Any(n => n is JsonValue v && v.TryGetValue<string>(out var t) && t == value);

    public static int CountOf(JsonObject state, string key, string release)
        => state[key] is JsonObject map && map[release] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    /// <summary>app 日誌全文（logs\*.log；app 正在寫，要共享讀取）</summary>
    public string Logs() => ReadLogs(Path.Combine(DataRoot, "logs"));

    public int CountInLogs(string needle) => CountOccurrences(Logs(), needle);

    /// <summary>資料夾裡所有 *.log 的全文（依檔名排序）；正在被寫或輪替中讀不到的檔這一輪略過</summary>
    public static string ReadLogs(string dir)
    {
        if (!Directory.Exists(dir))
            return string.Empty;
        var text = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(dir, "*.log").Order(StringComparer.Ordinal))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                text.Append(reader.ReadToEnd());
            }
            catch (IOException)
            {
                // 輪替中：下一輪再讀
            }
        }
        return text.ToString();
    }

    public static int CountOccurrences(string text, string needle)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    /// <summary>從這個暫存安裝執行中的 Myriagent.exe（主模組在 Root 底下；換版幫手也算）</summary>
    public IReadOnlyList<int> RunningApps()
    {
        var list = new List<int>();
        foreach (var process in Process.GetProcessesByName("Myriagent"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } file && file.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        list.Add(process.Id);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // 剛結束或拿不到模組：不算
                }
            }
        }
        return list;
    }
}
