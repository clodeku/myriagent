using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Myriagent.Core;
using Myriagent.UI;
using Microsoft.Win32;

namespace Myriagent.Update;

/// <summary>「安裝到這台電腦」與移除（設計 8.1、8.3）：全部在使用者資料夾，不需要管理員；移除不刪資料</summary>
internal static class SelfInstaller
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Myriagent";

    public static string StartMenuShortcutPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Clode Myriagent.lnk");

    /// <summary>
    /// 不是從安裝位置執行、不是開發版，而且還沒安裝或安裝的比自己舊（規則見 InstallLayoutRules.CanOfferInstall）。
    /// 讀不到 current 指向哪一版（開啟失敗）時不提供：不知道安裝的是哪一版，就不冒險蓋掉比較新的安裝
    /// </summary>
    public static bool CanOfferInstall(string? managedRelease, ReleaseIdentity current, InstallLayout layout)
    {
        string? installed;
        try
        {
            installed = CurrentTargetName(layout);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return InstallLayoutRules.CanOfferInstall(managedRelease, current, installed);
    }

    /// <summary>
    /// 把 sourceDirectory（這次執行的發佈資料夾）裡這個發佈自己的檔（ReleaseFilesIn）複製到 versions\&lt;release&gt;\（先複製到 .partial 再改名）、
    /// 移除每個檔的 Zone.Identifier、建立或改指向 current、記下目前版與前一版；registerShell 時建立開始選單捷徑與「應用程式」裡的移除項目。
    /// 回傳 current\Myriagent.exe。失敗（不是完整的發佈資料夾、和 manifest 不符時丟 InvalidDataException，含 updater-state.json 讀不到或等不到鎖）
    /// 時例外往上丟，由呼叫端記錄並顯示
    /// </summary>
    public static string Install(string sourceDirectory, InstallLayout layout, ReleaseIdentity identity, bool registerShell)
    {
        var release = identity.Release;
        var target = layout.VersionDirectory(release);
        var partial = layout.PartialVersionDirectory(release);
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        var copy = !string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        // 先決定要複製哪些檔：不是完整的發佈資料夾、或和 manifest 不符時在這裡就丟，安裝位置什麼都還沒動
        var files = copy ? ReleaseFilesIn(source, release) : Array.Empty<string>();
        Directory.CreateDirectory(layout.VersionsDirectory);
        if (copy)
        {
            if (Directory.Exists(partial))
                Directory.Delete(partial, recursive: true);
            CopyFiles(source, partial, files);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(partial, target);
        }

        var previous = CurrentTargetName(layout);
        if (Junction.IsJunction(layout.Current))
            Junction.Retarget(layout.Current, target);
        else
            Junction.Create(layout.Current, target);
        UpdaterStateStore.Update(layout, s =>
        {
            if (previous != null && previous != release && ReleaseIdentity.TryParseRelease(previous, out _))
                s.Previous = previous;
            s.Current = release;
            return s;
        });

        if (registerShell)
        {
            ShellLink.Create(StartMenuShortcutPath, layout.CurrentExecutable, layout.Current, ShellLink.AppUserModelId, ShellRules.AppTitle);
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", ShellRules.AppTitle);
            key.SetValue("DisplayVersion", identity.Version.ToString());
            key.SetValue("Publisher", "Myriagent");
            key.SetValue("InstallLocation", layout.Root);
            key.SetValue("DisplayIcon", layout.CurrentExecutable);
            key.SetValue("UninstallString", $"\"{layout.CurrentExecutable}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        Log.Info($"已安裝 {release} 到 {layout.Root}");
        return layout.CurrentExecutable;
    }

    /// <summary>
    /// 移除（--uninstall）：刪捷徑、移除項目、開機時自動啟動，程式資料夾在這個程序結束後由 cmd 刪除（自己正從裡面執行）；資料保留並告訴使用者位置。
    /// 回傳結束碼（0＝已移除、1＝使用者取消或還在執行）
    /// </summary>
    public static int Uninstall(InstallLayout layout, string dataRoot, bool interactive)
    {
        var processes = Process.GetProcessesByName("Myriagent");
        bool othersRunning = processes.Any(p => p.Id != Environment.ProcessId);
        foreach (var process in processes)
            process.Dispose();
        if (othersRunning)
        {
            if (interactive)
                MessageBox.Show("請先結束 Clode Myriagent（系統匣圖示的「結束」），再移除。", "移除 Clode Myriagent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        if (interactive && MessageBox.Show($"要移除 Clode Myriagent 嗎？\n\n帳戶與對話資料會保留在：\n{dataRoot}", "移除 Clode Myriagent",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return 1;
        try
        {
            File.Delete(StartMenuShortcutPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"刪捷徑失敗：{ex.Message}");
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        // 開機時自動啟動指向的 current 等一下就刪掉：不留下登入時找不到程式的項目
        try
        {
            AutoStartRegistration.Remove();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"刪開機時自動啟動的登錄失敗：{ex.Message}");
        }
        // 等這個程序結束後再刪整個程式資料夾（ping 當 3 秒的等待）。
        // cmd 不認 CommandLineToArgvW 的 \" 跳脫，不可用 ArgumentList：命令列原樣交給 cmd，/c 後面也不能以引號開頭（會觸發去掉頭尾引號的規則）
        var info = new ProcessStartInfo("cmd.exe", $"/d /c ping -n 4 127.0.0.1 > nul & rmdir /s /q \"{layout.Root}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        Process.Start(info)?.Dispose();
        Log.Info($"已移除捷徑與移除項目，程式資料夾 {layout.Root} 稍後刪除；資料保留在 {dataRoot}");
        return 0;
    }

    /// <summary>current 指向的資料夾名稱（沒有 current 或不是 junction 為 null）</summary>
    private static string? CurrentTargetName(InstallLayout layout)
        => Junction.GetTarget(layout.Current) is { } target ? Path.GetFileName(Path.TrimEndingDirectorySeparator(target)) : null;

    /// <summary>
    /// 這次要複製的檔（相對路徑、斜線分隔），只有這個發佈自己的：解壓到「下載」或桌面時 exe 旁邊是使用者自己的檔案，絕不整個資料夾複製。
    /// 資料夾裡有 release.manifest.json（從別台電腦拿來的版本資料夾）就照它的清單、逐檔驗大小與 SHA-256，連同 manifest 與簽章；
    /// 沒有就從 deps.json 推出（SelfInstallRules，只取實際存在的）。不是完整的發佈資料夾、或和 manifest 不符時丟 InvalidDataException（訊息給使用者看）
    /// </summary>
    private static IReadOnlyList<string> ReleaseFilesIn(string source, string release)
    {
        const string hint = "請解壓到一個空的資料夾，再從那裡執行";
        var manifestPath = Path.Combine(source, AppUpdateManager.ManifestFileName);
        if (File.Exists(manifestPath))
        {
            if (!ReleaseManifestRules.TryParse(File.ReadAllBytes(manifestPath), out var manifest, out var error))
                throw new InvalidDataException($"{AppUpdateManager.ManifestFileName} 讀不懂（{error}）；{hint}");
            if (manifest.Identity.Release != release)
                throw new InvalidDataException($"資料夾裡的 {AppUpdateManager.ManifestFileName} 是 {manifest.Identity.Release}，不是這一版 {release}；{hint}");
            foreach (var file in manifest.Files)
            {
                var full = Path.Combine(source, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full) || new FileInfo(full).Length != file.Size || Sha256Of(full) != file.Sha256)
                    throw new InvalidDataException($"檔案和 {AppUpdateManager.ManifestFileName} 不符：{file.Path}；{hint}");
            }
            var listed = manifest.Files.Select(f => f.Path).Append(AppUpdateManager.ManifestFileName).ToList();
            if (File.Exists(Path.Combine(source, AppUpdateManager.SignatureFileName)))
                listed.Add(AppUpdateManager.SignatureFileName);
            return listed;
        }

        var deps = Path.Combine(source, SelfInstallRules.DepsName);
        if (!File.Exists(deps))
            throw new InvalidDataException($"找不到 {SelfInstallRules.DepsName}，這不是完整的發佈資料夾；{hint}");
        var files = SelfInstallRules.CandidatesFromDeps(File.ReadAllText(deps))
            .Where(f => File.Exists(Path.Combine(source, f.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();
        if (SelfInstallRules.RequiredFiles.FirstOrDefault(f => !files.Contains(f, StringComparer.OrdinalIgnoreCase)) is { } missing)
            throw new InvalidDataException($"找不到 {missing}，這不是完整的發佈資料夾；{hint}");
        return files;
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void CopyFiles(string source, string destination, IReadOnlyList<string> files)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in files)
        {
            var relative = file.Replace('/', Path.DirectorySeparatorChar);
            var to = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(source, relative), to, overwrite: true);
            // CopyFile 連替代資料流一起複製：瀏覽器下載的 zip 解開後每個檔都有 Zone.Identifier
            if (!NativeMethods.DeleteAlternateStream(to, "Zone.Identifier"))
                Log.Warn($"移除網路來源標記失敗：{to}（錯誤 {Marshal.GetLastWin32Error()}）");
        }
    }
}
