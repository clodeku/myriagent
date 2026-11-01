using System.Diagnostics;
using Myriagent.Preview;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Preview;

/// <summary>
/// 真的 Office 轉 PDF（文件預覽，2026-11-01）：先以 Office 自己在暫存資料夾建出 docx、xlsx、pptx，再經 OfficePdfConverter 轉成 PDF。
/// 要 <c>MYRIAGENT_OFFICE_IT=1</c> 才跑（需要這台裝 Microsoft Office；每個約 3～10 秒，會在背景開 Office、轉完關掉）
/// </summary>
public sealed class OfficePdfConverterIntegrationTests
{
    /// <summary>以 Office 的 COM 建一份文件（Windows PowerShell）；腳本只用 ASCII</summary>
    private static void Create(string kind, string path)
    {
        var script = kind switch
        {
            "word" => "$a = New-Object -ComObject Word.Application; $a.Visible = $false; $d = $a.Documents.Add(); $d.Content.Text = 'Preview test page'; $d.SaveAs2($args[0], 16); $d.Close(0); $a.Quit()",
            "excel" => "$a = New-Object -ComObject Excel.Application; $a.Visible = $false; $a.DisplayAlerts = $false; $w = $a.Workbooks.Add(); $w.Worksheets.Item(1).Cells.Item(1, 1).Value2 = 'Preview'; $w.Worksheets.Item(1).Cells.Item(2, 1).Value2 = 42; $w.SaveAs($args[0], 51); $w.Close($false); $a.Quit()",
            "powerpoint" => "$a = New-Object -ComObject PowerPoint.Application; $p = $a.Presentations.Add(0); $s = $p.Slides.Add(1, 1); $s.Shapes.Item(1).TextFrame.TextRange.Text = 'Preview slide'; $p.SaveAs($args[0], 24); $p.Close(); if ($a.Presentations.Count -eq 0 -and @(Get-Process POWERPNT -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }).Count -eq 0) { $a.Quit() }",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // -File＋參數（-Command 後面的引數不會變成 $args）；腳本只用 ASCII
        var file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "create-" + kind + ".ps1");
        File.WriteAllText(file, "param([string]$Path)" + Environment.NewLine + script.Replace("$args[0]", "$Path"), new System.Text.UTF8Encoding(false));
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file, "-Path", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(120_000).Should().BeTrue("建立測試文件");
        File.Exists(path).Should().BeTrue($"Office 建出了 {path}");
    }

    [EnvTheory("MYRIAGENT_OFFICE_IT", "本機的 Microsoft Office 轉 PDF")]
    [InlineData("word", "測試文件.docx", (int)DocPreviewKind.Word)]
    [InlineData("excel", "測試試算表.xlsx", (int)DocPreviewKind.Excel)]
    [InlineData("powerpoint", "測試簡報.pptx", (int)DocPreviewKind.PowerPoint)]
    public async Task Office文件轉成PDF(string kind, string name, int previewKind)
    {
        using var temp = new TempDirectory("office-pdf");
        var source = temp.Combine(name);
        Create(kind, source);
        var output = temp.Combine("out.pdf");
        var converter = new OfficePdfConverter(temp.Combine("work"));

        var error = await converter.ConvertAsync(source, (DocPreviewKind)previewKind, output, CancellationToken.None);

        error.Should().BeNull();
        var head = new byte[5];
        await using (var f = File.OpenRead(output))
            (await f.ReadAsync(head)).Should().Be(5);
        System.Text.Encoding.ASCII.GetString(head).Should().Be("%PDF-");
        Directory.GetFiles(temp.Root, "*.tmp.pdf").Should().BeEmpty("暫存檔改名或刪掉了");
    }

    [EnvFact("MYRIAGENT_OFFICE_IT", "本機的 Microsoft Office 轉 PDF")]
    public async Task 損毀的檔案_回無法轉換_不留半個檔()
    {
        using var temp = new TempDirectory("office-pdf");
        var source = temp.Combine("壞掉.docx");
        File.WriteAllText(source, "這不是 docx");
        var output = temp.Combine("out.pdf");

        var error = await new OfficePdfConverter(temp.Combine("work")).ConvertAsync(source, DocPreviewKind.Word, output, CancellationToken.None);

        error.Should().Be(DocPreviewRules.FailedText);
        File.Exists(output).Should().BeFalse();
    }
}
