using Microsoft.Extensions.Hosting.WindowsServices;

namespace MyriagentHub.Hosting;

/// <summary>執行環境資訊（啟動後不會改變，算一次就好：判斷是否為服務要查父程序）</summary>
internal static class HostInfo
{
    private static readonly Lazy<bool> WindowsService = new(WindowsServiceHelpers.IsWindowsService);

    /// <summary>是否以 Windows 服務執行（SCM 啟動）</summary>
    public static bool IsWindowsService => WindowsService.Value;
}
