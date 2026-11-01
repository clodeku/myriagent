using System.Diagnostics;
using System.Text;
using Myriagent.Core;
using Microsoft.Win32;

namespace Myriagent.Preview;

/// <summary>把 Office 文件轉成 PDF（文件預覽用；在背景執行緒呼叫）</summary>
internal interface IDocumentPdfConverter
{
    /// <summary>轉好寫到 <paramref name="outputPdf"/>（先寫暫存檔再改名，失敗不留半個檔）；成功回 null，失敗回給使用者看的說明</summary>
    Task<string?> ConvertAsync(string source, DocPreviewKind kind, string outputPdf, CancellationToken cancellationToken);
}

/// <summary>
/// 這台的 Microsoft Office（COM 自動化，經 Windows PowerShell 5.1 的腳本；一律唯讀開啟、關掉巨集、不顯示視窗與警告，
/// 有密碼的檔以假密碼開啟所以直接失敗、不跳出輸入框）；沒裝那個 Office 程式時改用 LibreOffice（headless）。
/// 逾時（<see cref="DocPreviewRules.ConvertTimeout"/>）就結束腳本與它以 COM 開出的 Office 程序（腳本把那個 PID 寫進檔案；
/// 使用者自己開的 Office 命令列沒有 -Embedding，不會被記下，也不會被關掉）。同一時間只轉一個由呼叫端負責
/// </summary>
internal sealed class OfficePdfConverter(string workDirectory) : IDocumentPdfConverter
{
    /// <summary>腳本只用 ASCII（Windows PowerShell 5.1 把沒有 BOM 的檔當 ANSI 讀）</summary>
    internal const string Script = """
        param([string]$Source, [string]$Out, [string]$Kind, [string]$PidFile)
        $ErrorActionPreference = 'Stop'
        function Save-OfficePid([string]$exe) {
          try {
            $p = Get-CimInstance Win32_Process -Filter ("Name='" + $exe + "'") |
              Where-Object { $_.CommandLine -match 'Embedding|automation' } |
              Sort-Object CreationDate -Descending | Select-Object -First 1
            if ($p) { [IO.File]::WriteAllText($PidFile, [string]$p.ProcessId) }
          } catch { }
        }
        $app = $null; $quit = $true; $code = 0
        try {
          if ($Kind -eq 'word') {
            try { $app = New-Object -ComObject Word.Application } catch { exit 3 }
            Save-OfficePid 'WINWORD.EXE'
            $app.Visible = $false; $app.DisplayAlerts = 0
            try { $app.AutomationSecurity = 3 } catch { }
            $doc = $app.Documents.Open($Source, $false, $true, $false, 'no-password-x')
            try { $doc.ExportAsFixedFormat($Out, 17) } finally { $doc.Close(0) }
          } elseif ($Kind -eq 'excel') {
            try { $app = New-Object -ComObject Excel.Application } catch { exit 3 }
            Save-OfficePid 'EXCEL.EXE'
            $app.Visible = $false; $app.DisplayAlerts = $false; $app.AskToUpdateLinks = $false
            try { $app.AutomationSecurity = 3 } catch { }
            $wb = $app.Workbooks.Open($Source, 0, $true, [Type]::Missing, 'no-password-x')
            try { $wb.ExportAsFixedFormat(0, $Out) } finally { $wb.Close($false) }
          } elseif ($Kind -eq 'powerpoint') {
            $quit = @(Get-Process POWERPNT -ErrorAction SilentlyContinue).Count -eq 0
            try { $app = New-Object -ComObject PowerPoint.Application } catch { exit 3 }
            if ($quit) { Save-OfficePid 'POWERPNT.EXE' }
            try { $app.AutomationSecurity = 3 } catch { }
            $pres = $app.Presentations.Open($Source, -1, 0, 0)
            try { $pres.SaveAs($Out, 32) } finally { $pres.Close() }
          } else { exit 4 }
        } catch {
          [Console]::Error.WriteLine($_.Exception.Message)
          $code = 2
        } finally {
          if ($app -ne $null) {
            if ($quit) { try { $app.Quit() } catch { } }
            try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($app) } catch { }
          }
        }
        exit $code
        """;

    private readonly string scriptPath = Path.Combine(workDirectory, "office-to-pdf.ps1");

    public async Task<string?> ConvertAsync(string source, DocPreviewKind kind, string outputPdf, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDirectory);
        var temp = Path.Combine(Path.GetDirectoryName(outputPdf)!, $".{Guid.NewGuid():N}.tmp.pdf");
        try
        {
            string? error;
            if (HasOffice(kind))
                error = await ConvertWithOfficeAsync(source, kind, temp, cancellationToken).ConfigureAwait(false);
            else if (FindLibreOffice() is { } soffice)
                error = await ConvertWithLibreOfficeAsync(soffice, source, temp, cancellationToken).ConfigureAwait(false);
            else
                return DocPreviewRules.NoConverterText;
            if (error != null)
                return error;
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                return DocPreviewRules.FailedText;
            File.Move(temp, outputPdf, overwrite: true);
            return null;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>這種文件的 Office 程式有沒有登記 COM（裝了 Office 才有）</summary>
    internal static bool HasOffice(DocPreviewKind kind)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(DocPreviewRules.ProgIdOf(kind) + @"\CLSID");
            return key?.GetValue(null) is string { Length: > 0 };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    internal static string? FindLibreOffice() => DocPreviewRules.LibreOfficeCandidates(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))
        .FirstOrDefault(File.Exists);

    private async Task<string?> ConvertWithOfficeAsync(string source, DocPreviewKind kind, string output, CancellationToken cancellationToken)
    {
        if (!File.Exists(scriptPath) || File.ReadAllText(scriptPath) != Script)
            File.WriteAllText(scriptPath, Script, new UTF8Encoding(false));
        var pidFile = Path.Combine(workDirectory, $"{Guid.NewGuid():N}.pid");
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                     "-Source", source, "-Out", output, "-Kind", DocPreviewRules.ScriptKindOf(kind), "-PidFile", pidFile })
            psi.ArgumentList.Add(arg);
        try
        {
            var code = await RunAsync(psi, cancellationToken, onTimeout: () => KillOfficeOf(pidFile)).ConfigureAwait(false);
            return code switch
            {
                null => DocPreviewRules.TimeoutText,
                _ => DocPreviewRules.ErrorOf(code.Value),
            };
        }
        finally
        {
            TryDelete(pidFile);
        }
    }

    private async Task<string?> ConvertWithLibreOfficeAsync(string soffice, string source, string output, CancellationToken cancellationToken)
    {
        // 獨立的設定檔資料夾：使用者自己開著 LibreOffice 時也不會把轉檔交給那個視窗
        var outDir = Path.Combine(workDirectory, "lo-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var profile = new Uri(Path.Combine(workDirectory, "lo-profile")).AbsoluteUri;
            var psi = new ProcessStartInfo(soffice) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-env:UserInstallation=" + profile, "--headless", "--norestore", "--nologo", "--convert-to", "pdf", "--outdir", outDir, source })
                psi.ArgumentList.Add(arg);
            var code = await RunAsync(psi, cancellationToken, onTimeout: null).ConfigureAwait(false);
            if (code == null)
                return DocPreviewRules.TimeoutText;
            var produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(source) + ".pdf");
            if (code != 0 || !File.Exists(produced))
                return DocPreviewRules.FailedText;
            File.Move(produced, output, overwrite: true);
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(outDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 留著，下次清快取時一起刪
            }
        }
    }

    /// <summary>執行並等結束；逾時回 null（先結束整棵程序樹，再呼叫 onTimeout）</summary>
    private static async Task<int?> RunAsync(ProcessStartInfo psi, CancellationToken cancellationToken, Action? onTimeout)
    {
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動轉檔程式");
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DocPreviewRules.ConvertTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            onTimeout?.Invoke();
            return null;
        }
        var err = await stderr.ConfigureAwait(false);
        await stdout.ConfigureAwait(false);
        if (process.ExitCode != 0 && err.Length > 0)
            Log.Warn($"文件預覽轉檔失敗（{process.ExitCode}）：{err.Trim()}");
        return process.ExitCode;
    }

    /// <summary>逾時：結束腳本以 COM 開出的 Office（只認 Office 的程式名稱，PID 已被別的程式重複使用時不動它）</summary>
    private static void KillOfficeOf(string pidFile)
    {
        try
        {
            if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
                return;
            using var p = Process.GetProcessById(pid);
            if (p.ProcessName.ToUpperInvariant() is "WINWORD" or "EXCEL" or "POWERPNT")
                p.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
