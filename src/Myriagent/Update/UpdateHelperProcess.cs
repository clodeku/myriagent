using System.Diagnostics;
using Myriagent.Core;

namespace Myriagent.Update;

/// <summary>
/// app 這一側的換版幫手（AppController 交棒時啟動）：留著 Process 控制代碼不釋放，放棄交棒時以它結束幫手。
/// 持有控制代碼時 Windows 不會把這個 PID 給別的程序，所以 PID 相符就一定是自己啟動的那個幫手；
/// 只結束幫手本身（它在舊 app 結束前還沒有子程序），不用 WindowsApplierPlatform.Kill（以裸 PID 找程序、結束整棵樹）。
/// 只在 UI 執行緒使用
/// </summary>
internal sealed class UpdateHelperProcess : IDisposable
{
    private Process? helper;

    /// <summary>啟動程式（不經殼層、工作資料夾設在它的資料夾，同 WindowsApplierPlatform.Launch）；啟動不了記錄後回 null。呼叫端負責釋放回傳的 Process</summary>
    public static Process? StartProcess(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);
            return Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"啟動 {executable} 失敗：{ex.Message}");
            return null;
        }
    }

    /// <summary>啟動幫手並留著它的控制代碼；回傳 PID（啟動不了為 null）</summary>
    public int? Start(string executable, IReadOnlyList<string> arguments)
    {
        var process = StartProcess(executable, arguments);
        if (process == null)
            return null;
        helper?.Dispose();   // 照理沒有上一個（放棄時已結束、交棒後 app 就結束）；有的話只放開控制代碼
        helper = process;
        return process.Id;
    }

    /// <summary>放棄交棒：pid 是留著的那個幫手才結束它（最多等 5 秒）並放開控制代碼；不符就不動作，幫手已經結束也不丟例外</summary>
    public void Stop(int pid)
    {
        if (helper is not { } process || process.Id != pid)
            return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"結束換版幫手 {pid} 失敗：{ex.Message}");
        }
        process.Dispose();
        helper = null;
    }

    /// <summary>app 結束時：只放開控制代碼，不結束幫手（交棒之後換版要靠它）</summary>
    public void Dispose()
    {
        helper?.Dispose();
        helper = null;
    }
}
