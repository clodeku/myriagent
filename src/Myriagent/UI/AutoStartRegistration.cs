using System.Security;
using Myriagent.Core;
using Myriagent.Hub;
using Microsoft.Win32;

namespace Myriagent.UI;

/// <summary>
/// 開機時自動啟動的登錄（HKCU，不需要管理員）：Run 機碼的 Myriagent 值指向受管理安裝的 current\Myriagent.exe --autostart。
/// 工作管理員停用時寫的是 StartupApproved\Run 的同名值：從設定頁開啟時一併刪掉它才會真的啟動，關閉時也清掉
/// </summary>
internal sealed class AutoStartRegistration : IAutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private readonly string executable;

    /// <param name="executable">登錄指向的執行檔（InstallLayout.CurrentExecutable）</param>
    /// <param name="available">AutoStartRules.IsAvailable 的結果：false 時只讀不寫</param>
    public AutoStartRegistration(string executable, bool available)
    {
        this.executable = executable;
        Available = available;
    }

    public bool Available { get; }

    public bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return AutoStartRules.IsEnabled(run?.GetValue(AutoStartRules.ValueName) as string, approved?.GetValue(AutoStartRules.ValueName) as byte[]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Log.Warn($"讀開機時自動啟動的登錄失敗：{ex.Message}");
            return false;
        }
    }

    public bool? AutoLogon()
    {
        try
        {
            using var winlogon = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
            return AutoStartRules.AutoLogon(winlogon?.GetValue("AutoAdminLogon") as string ?? "0");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!Available)
            throw new InvalidOperationException("這次執行不能設定開機時自動啟動");
        if (enabled)
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            run.SetValue(AutoStartRules.ValueName, AutoStartRules.Command(executable), RegistryValueKind.String);
            DeleteValue(ApprovedKey);
            Log.Info($"已開啟開機時自動啟動：{AutoStartRules.Command(executable)}");
        }
        else
        {
            Remove();
            Log.Info("已關閉開機時自動啟動");
        }
    }

    /// <summary>刪掉 Run 與 StartupApproved\Run 的值（關閉、移除 app 時；沒有就什麼都不做）</summary>
    public static void Remove()
    {
        DeleteValue(RunKey);
        DeleteValue(ApprovedKey);
    }

    private static void DeleteValue(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(AutoStartRules.ValueName, throwOnMissingValue: false);
    }
}
