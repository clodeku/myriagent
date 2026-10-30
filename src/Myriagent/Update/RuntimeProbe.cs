using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Myriagent.Update;

internal interface IRuntimeProbe
{
    bool HasAtLeast(string name, ReleaseVersion version);

    /// <summary>執行中的程序載入的 .NET 已被移除（.NET 更新時刪掉了舊的修補版）時是哪一版；都還在是 null（RuntimeRules.RemovedRuntime）</summary>
    string? RemovedRuntime => null;
}

/// <summary>讀登錄判斷執行階段（設計 8.4 第 3 步；不啟動任何程序）</summary>
internal sealed class RegistryRuntimeProbe : IRuntimeProbe
{
    public bool HasAtLeast(string name, ReleaseVersion version)
    {
        try
        {
            // .NET 安裝程式只把 InstalledVersions 寫在 32 位元檢視（WOW6432Node）；這個 app 是 x64，預設會開 64 位元檢視而讀不到
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = hklm.OpenSubKey($@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\{name}");
            var installed = key?.GetValueNames() ?? Array.Empty<string>();
            // 目前這個程序跑的執行階段一定存在（自己就是 WindowsDesktop app）
            var running = $"{Environment.Version.Major}.{Environment.Version.Minor}.{Math.Max(0, Environment.Version.Build)}";
            return RuntimeRules.Satisfies(installed.Append(running), version);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>這個程序載入的兩個框架資料夾（核心與 WindowsDesktop）還在不在；只看資料夾，開銷小（協調器每 15 秒問一次）</summary>
    public string? RemovedRuntime
    {
        get
        {
            if (UpdateEnvironment.E2ERuntimeRemoved is { } simulated)
                return simulated;
            try
            {
                return RuntimeRules.RemovedRuntime(
                    [RuntimeEnvironment.GetRuntimeDirectory(), Path.GetDirectoryName(typeof(System.Windows.Forms.Control).Assembly.Location)], Directory.Exists);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
    }
}
