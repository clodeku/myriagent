using System.Text;
using Myriagent.RemoteDesk;

namespace MyriagentTools.DeskProbe;

/// <summary>
/// 遠端桌面的擷取探針：把 <see cref="DeskDiagnostics"/> 的回報印到主控台（UTF-8）或寫進檔案。用法見 README.md
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("desk-probe [--wake] [--all] [--seconds N] [--out <檔案>]");
            Console.WriteLine("  --wake     最後再以會叫醒螢幕的方式擷取一次（會真的把這台的螢幕叫醒）");
            Console.WriteLine("  --all      每個螢幕都做（預設只做主螢幕）");
            Console.WriteLine("  --seconds  每種方式擷取多久（預設 3）");
            Console.WriteLine("  --out      另外寫進檔案（UTF-8）");
            return 0;
        }

        var seconds = 3;
        string? outFile = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--seconds" && int.TryParse(args[i + 1], out var n) && n is > 0 and <= 60)
                seconds = n;
            if (args[i] == "--out")
                outFile = args[i + 1];
        }

        // 和 app 一樣（PerMonitorV2）：螢幕的範圍要是實際像素，才對得上顯示卡輸出
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Console.OutputEncoding = new UTF8Encoding(false);
        var lines = new List<string>();
        void Write(string line)
        {
            lines.Add(line);
            Console.WriteLine(line);
        }

        Write($"{Environment.MachineName}　{DateTime.Now:yyyy-MM-dd HH:mm:ss}　{Environment.OSVersion.VersionString}");
        try
        {
            DeskDiagnostics.Run(Write, wake: args.Contains("--wake"), seconds, allScreens: args.Contains("--all"));
        }
        catch (Exception ex)
        {
            Write("失敗：" + ex);
            return 1;
        }
        finally
        {
            if (outFile is not null)
                File.WriteAllLines(outFile, lines, new UTF8Encoding(false));
        }

        return 0;
    }
}
