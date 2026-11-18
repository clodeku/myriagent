using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MyriagentSetup;

/// <summary>
/// 安裝程式：內含一份自帶 .NET 的發佈（嵌入資源 payload.zip）。解到暫存資料夾、執行裡面的 app 的 <c>--install-package</c>
/// （app 自己把整個資料夾安裝到使用者資料夾的受管理位置、建捷徑與移除項目，再啟動裝好的那一份），最後刪掉暫存資料夾。
/// 只安裝給目前的使用者，不需要系統管理員。參數：<c>/quiet</c> 不顯示視窗（結束碼 0 成功、1 失敗）；<c>/nolaunch</c> 裝好不啟動。
/// </summary>
internal static class Program
{
    internal const string AppName = "Clode Myriagent";

    internal const string AppExe = "Myriagent.exe";

    /// <summary>app 安裝失敗時寫在來源資料夾的原因（同 app 的 SelfInstaller.SetupErrorFileName）</summary>
    internal const string ErrorFile = "install-error.txt";

    [STAThread]
    private static int Main(string[] args)
    {
        bool quiet = Has(args, "/quiet");
        bool noLaunch = Has(args, "/nolaunch");
        if (quiet)
            return Installer.Run(noLaunch, null).Ok ? 0 : 1;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new SetupForm(noLaunch);
        Application.Run(form);
        return form.ExitCode;
    }

    private static bool Has(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase) || string.Equals(a, "-" + name.Substring(1), StringComparison.OrdinalIgnoreCase));

    /// <summary>給人看的版本（組件版本的前三段；建置時的 -p:Version）</summary>
    internal static string VersionText
    {
        get
        {
            var v = typeof(Program).Assembly.GetName().Version;
            return v == null ? string.Empty : v.Major + "." + v.Minor + "." + v.Build;
        }
    }
}

internal sealed class InstallResult
{
    public InstallResult(bool ok, string message)
    {
        Ok = ok;
        Message = message;
    }

    public bool Ok { get; }

    public string Message { get; }
}

internal static class Installer
{
    /// <summary>app 的安裝最多等這麼久（複製一百多 MB 的檔；防毒軟體逐檔掃描時會慢）</summary>
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>解壓縮、安裝、清理。progress（百分比 0～100、說明）在呼叫的執行緒上回呼；不丟例外</summary>
    public static InstallResult Run(bool noLaunch, Action<int, string>? progress)
    {
        string? temp = null;
        try
        {
            if (!Environment.Is64BitOperatingSystem || Environment.OSVersion.Version.Major < 10)
                return new InstallResult(false, "需要 64 位元的 Windows 10 或 Windows 11。");
            using var payload = typeof(Installer).Assembly.GetManifestResourceStream("payload.zip");
            if (payload == null)
                return new InstallResult(false, "這個安裝程式沒有內含程式檔案，請重新下載。");

            temp = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(Program.AppExe) + "-setup-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(temp);
            Extract(payload, temp, progress);

            var exe = Path.Combine(temp, Program.AppExe);
            if (!File.Exists(exe))
                return new InstallResult(false, "安裝程式內含的檔案不完整，請重新下載。");
            progress?.Invoke(92, "正在安裝…");
            var start = new ProcessStartInfo(exe, noLaunch ? "--install-package --no-relaunch" : "--install-package") { WorkingDirectory = temp, UseShellExecute = false };
            using (var process = Process.Start(start))
            {
                if (process == null)
                    return new InstallResult(false, "無法啟動安裝。");
                if (!process.WaitForExit((int)InstallTimeout.TotalMilliseconds))
                {
                    try { process.Kill(); } catch (Exception) { /* 已經結束 */ }
                    return new InstallResult(false, "安裝太久沒有完成，已經停止。請再試一次。");
                }

                if (process.ExitCode != 0)
                    return new InstallResult(false, ReadError(temp) ?? "安裝失敗（結束碼 " + process.ExitCode + "）。");
            }

            progress?.Invoke(100, "安裝完成");
            return new InstallResult(true, "安裝完成");
        }
        catch (Exception ex)
        {
            return new InstallResult(false, "安裝失敗：" + ex.Message);
        }
        finally
        {
            if (temp != null)
                TryDelete(temp);
        }
    }

    /// <summary>解到 target（全新的資料夾）。每一筆的路徑都要落在 target 裡面；進度照解出的位元組算到 90%</summary>
    private static void Extract(Stream payload, string target, Action<int, string>? progress)
    {
        var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        long total = Math.Max(1, zip.Entries.Sum(e => e.Length));
        long done = 0;
        int shown = -1;
        var buffer = new byte[1 << 16];
        foreach (var entry in zip.Entries)
        {
            var full = Path.GetFullPath(Path.Combine(target, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安裝程式內含的檔案路徑不合法：" + entry.FullName);
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(full);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var from = entry.Open())
            using (var to = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
                {
                    to.Write(buffer, 0, read);
                    done += read;
                    int percent = (int)(done * 90 / total);
                    if (percent != shown)
                    {
                        shown = percent;
                        progress?.Invoke(percent, "正在解開檔案…");
                    }
                }
            }
        }
    }

    private static string? ReadError(string temp)
    {
        try
        {
            var path = Path.Combine(temp, Program.ErrorFile);
            var text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : string.Empty;
            return text.Length == 0 ? null : "安裝失敗：" + text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>刪暫存資料夾：防毒軟體可能還開著剛解出的檔，試幾次；刪不掉就留給系統清理</summary>
    private static void TryDelete(string directory)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(500);
            }
        }
    }
}

internal sealed class SetupForm : Form
{
    private readonly bool noLaunch;
    private readonly Label status;
    private readonly ProgressBar bar;
    private readonly Button install;
    private readonly Button cancel;
    private bool running;
    private bool finished;

    public SetupForm(bool noLaunch)
    {
        this.noLaunch = noLaunch;
        ExitCode = 1;
        Text = "安裝 " + Program.AppName;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 214);
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception)
        {
            // 沒有圖示照樣安裝
        }

        var title = new Label
        {
            Text = (Program.AppName + " " + Program.VersionText).Trim(),
            Font = new Font(Font.FontFamily, Font.Size + 4F, FontStyle.Bold),
            Location = new Point(24, 20),
            Size = new Size(432, 32),
        };
        var body = new Label
        {
            Text = "會安裝到你的使用者資料夾，不需要系統管理員權限。\r\n已經安裝過的話會換成這一版，帳戶與對話都會保留。",
            Location = new Point(24, 58),
            Size = new Size(432, 44),
        };
        bar = new ProgressBar { Location = new Point(24, 110), Size = new Size(432, 16), Minimum = 0, Maximum = 100, Visible = false };
        status = new Label { Location = new Point(24, 132), Size = new Size(432, 22), Text = string.Empty };
        install = new Button { Text = "安裝", Location = new Point(264, 168), Size = new Size(92, 30) };
        cancel = new Button { Text = "取消", Location = new Point(364, 168), Size = new Size(92, 30) };
        install.Click += (_, _) => Start();
        cancel.Click += (_, _) => Close();
        AcceptButton = install;
        CancelButton = cancel;
        Controls.AddRange(new Control[] { title, body, bar, status, install, cancel });
    }

    public int ExitCode { get; private set; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 安裝到一半不能關：app 正在複製檔案
        if (running)
            e.Cancel = true;
        base.OnFormClosing(e);
    }

    private void Start()
    {
        if (running)
            return;
        if (finished)
        {
            Close();
            return;
        }

        running = true;
        install.Enabled = false;
        cancel.Enabled = false;
        bar.Value = 0;
        bar.Visible = true;
        status.ForeColor = SystemColors.ControlText;
        status.Text = "正在解開檔案…";
        var worker = new Thread(() =>
        {
            var result = Installer.Run(noLaunch, (percent, text) => Post(() =>
            {
                bar.Value = Math.Max(0, Math.Min(100, percent));
                status.Text = text;
            }));
            Post(() => Done(result));
        }) { IsBackground = true, Name = "setup" };
        worker.Start();
    }

    private void Post(Action action)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // 視窗已經關了
        }
    }

    private void Done(InstallResult result)
    {
        running = false;
        if (result.Ok)
        {
            ExitCode = 0;
            if (!noLaunch)
            {
                // app 已經啟動：安裝程式不必留著
                Close();
                return;
            }

            finished = true;
            bar.Value = 100;
            status.Text = "安裝完成。可以從開始選單開啟 " + Program.AppName + "。";
            install.Text = "關閉";
            install.Enabled = true;
            cancel.Visible = false;
            return;
        }

        bar.Visible = false;
        status.ForeColor = Color.Firebrick;
        status.Text = "沒有安裝成功。";
        install.Text = "重試";
        install.Enabled = true;
        cancel.Enabled = true;
        MessageBox.Show(this, result.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
