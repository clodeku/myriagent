using MyriagentHub.Admin;

namespace MyriagentHub.Hosting;

internal static class Program
{
    /// <summary>
    /// 進入點。<c>MyriagentHub.exe --data &lt;資料根&gt;</c> 執行伺服器（以 Windows 服務或主控台）；
    /// <c>MyriagentHub.exe admin &lt;指令&gt;</c> 是只能在主機上以管理員執行的管理工具（見 Admin/AdminCommandLine.cs）。
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "admin", StringComparison.OrdinalIgnoreCase))
            return await AdminCommandLine.RunAsync(args[1..], Console.Out);

        await using var app = HubApplication.Build(args);
        await app.RunAsync();
        return Environment.ExitCode;
    }
}
