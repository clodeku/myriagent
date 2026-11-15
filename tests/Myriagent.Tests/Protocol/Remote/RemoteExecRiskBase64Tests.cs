using Myriagent.Remote;

namespace Myriagent.Tests.Protocol.Remote;

/// <summary>
/// 高風險判斷不能被檔案內容的 Base64 字串誤判（2026-11-15 使用者回報：代理以 myriagent-remote run 傳圖片，編碼字串被判成「執行看不到內容的指令」）：
/// Base64 的 + 與 / 被當成字的邊界，字串裡剛好出現的 /iex+、+rm/ 之類的片段被當成指令。長的 Base64 字串（資料）先遮起來再判斷，
/// 真的 -EncodedCommand、iex＋解碼、刪系統資料夾照樣擋
/// </summary>
public sealed class RemoteExecRiskBase64Tests
{
    /// <summary>一段 Base64 圖片資料，中間剛好有 /Iex+ 與 +Shutdown/ 這種片段（真的圖片編出來就可能有）</summary>
    private static string Blob()
    {
        var random = new Random(7);
        var bytes = new byte[3000];
        random.NextBytes(bytes);
        var b64 = Convert.ToBase64String(bytes);
        return b64[..1000] + "/Iex+" + b64[1000..2000] + "+Shutdown/" + b64[2000..];
    }

    [Theory]
    [InlineData("[IO.File]::WriteAllBytes('C:\\temp\\a.png', [Convert]::FromBase64String('{0}'))")]
    [InlineData("$b = '{0}'; [IO.File]::WriteAllBytes(\"$env:TEMP\\a.png\", [Convert]::FromBase64String($b))")]
    [InlineData("Set-Content -Path C:\\temp\\a.b64 -Value '{0}'")]
    public void 檔案內容的Base64字串不算高風險(string template)
    {
        var command = template.Replace("{0}", Blob(), StringComparison.Ordinal);
        RemoteExecRiskRules.Classify(RemoteExecRules.PowerShell, command).Should().BeNull();
    }

    [Fact]
    public void 真的編碼指令與解碼後執行照樣擋()
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("Get-Date; Get-Process | Select-Object -First 3; Write-Output done-done-done"));
        RemoteExecRiskRules.Classify(RemoteExecRules.PowerShell, "powershell -EncodedCommand " + encoded)!.Category.Should().Be(RemoteExecRiskRules.Hidden);
        RemoteExecRiskRules.Classify(RemoteExecRules.PowerShell, "iex ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Blob() + "')))")!
            .Category.Should().Be(RemoteExecRiskRules.Hidden);
    }

    [Fact]
    public void 長的路徑不會被當成資料遮掉()
    {
        RemoteExecRiskRules.Classify(RemoteExecRules.PowerShell, "Remove-Item -Recurse C:\\Windows\\System32\\DriverStore\\FileRepository\\Abc123Def456Ghi789Jkl012Mno345")
            .Should().NotBeNull();
        RemoteExecRiskRules.Classify(RemoteExecRules.PowerShell, "rm -rf /c/windows")
            .Should().NotBeNull();
    }
}
