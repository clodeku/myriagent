using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Myriagent.Update;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using MyriagentHub.Ops;
using MyriagentHub.Releases;
using MyriagentHub.Tests.Media;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Releases;

/// <summary>
/// app 發佈收件匣的腳本與服務介面（docs/hub-setup.md 第 11 節）：tools/release/Publish-DesktopRelease.ps1 把三個檔與
/// request.json 放進 admin-inbox\app-release\，等 status\app-release.json 的回執。腳本這一邊由 tools/hub/Test-Hub 主機Scripts.ps1
/// （契約字串、-WhatIf）與 tools/deploy/Test-MyriagentHubDeploy.ps1（S15～S21，假服務）守；這裡守服務這一邊，並以真的腳本對行程內主機送件。
/// </summary>
public sealed class AppReleaseInterfaceTests
{
    /// <summary>
    /// 測試主機的磁碟可用空間（500 GB）：不看跑測試那台的磁碟（同 AppReleaseInboxTests；Hub:Releases:MinFreeDiskMb 預設 2 GiB，
    /// 快滿的開發機或 GitHub 的 Windows runner 會讓收件回 disk_full）
    /// </summary>
    private const long PlentyOfDisk = 500L << 30;

    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string Script => RepoFiles.Combine("tools", "release", "Publish-DesktopRelease.ps1");

    [Fact]
    public async Task 收件匣路徑與回執格式是腳本認的那一份()
    {
        var script = await File.ReadAllTextAsync(Script);
        var paths = new HubPaths(@"C:\ProgramData\MyriagentHub");
        ScriptLiteral(script, "InboxRelative").Should().Be(Path.GetRelativePath(paths.Root, paths.AdminInboxAppReleaseDir));
        ScriptLiteral(script, "ReceiptRelative").Should().Be(Path.GetRelativePath(paths.Root, paths.AppReleaseAckFile));
        ScriptLiteral(script, "RequestName").Should().Be(AppReleaseIngestRules.RequestFileName);

        // 回執：服務以 StatusJson.Options 寫出的欄位名，就是腳本讀的那幾個（PowerShell 的屬性名稱不分大小寫，這裡照字面比）
        var receipt = JsonDocument.Parse(JsonSerializer.Serialize(
            new AppReleaseReceipt("pub-1", true, "0.2.300-ab12cd3", AppReleaseIngestCodes.Ingested, 1_760_000_000_000), StatusJson.Options)).RootElement;
        receipt.EnumerateObject().Select(p => p.Name).Should().Equal("id", "ok", "release", "message", "processedAt");
        foreach (var name in new[] { "id", "ok", "release", "message" })
            script.Should().Contain($"(Get-Prop $receipt '{name}')", $"腳本讀回執的 {name}");

        // request：腳本 -WhatIf 印出它會寫的 request.json，服務的解析器讀得懂
        var root = TempDir.Create("myriagent-publish-contract");
        try
        {
            var dataRoot = Path.Combine(root, "ProgramData", "MyriagentHub");
            Directory.CreateDirectory(new HubPaths(dataRoot).AdminInboxAppReleaseDir);
            var artifacts = Directory.CreateDirectory(Path.Combine(root, "artifact")).FullName;
            const string Release = "0.2.300-ab12cd3";
            var names = ReleasePackageRules.FileNames(Release);
            foreach (var name in new[] { names.Zip, names.Manifest, names.Signature })
                await File.WriteAllTextAsync(Path.Combine(artifacts, name), "fake " + name);

            var (code, output) = await RunPublishAsync(artifacts, Release, HashesOf(artifacts, names), dataRoot, "pub-contract", "-WhatIf");

            code.Should().Be(0, output);
            var line = output.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith("PLAN request ", StringComparison.Ordinal));
            var request = AppReleaseIngestRules.ParseRequest(line[line.IndexOf('{')..]);
            request.Should().NotBeNull(line);
            request!.Id.Should().Be("pub-contract");
            request.Release.Should().Be(Release);
            Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories).Should().BeEmpty("-WhatIf 不寫任何檔");
        }
        finally
        {
            TempDir.TryDelete(root);
        }
    }

    [Fact]
    public async Task 真的腳本對行程內主機送件成功()
    {
        using var fixture = new ReleasePackageFixture();
        // HubTestHost 預設 Hub:Releases:InboxPollSeconds=1：監看事件漏掉時一秒內也會處理；腳本的 -DataRoot 就是主機的暫存資料根。
        // 磁碟可用空間固定（FakeDiskSpace），不看跑測試那台的磁碟
        await using var host = await HubTestHost.StartAsync(services: s =>
        {
            s.AddSingleton<IBuiltInReleaseKeys>(new TestBuiltInReleaseKeys(fixture.Keys));
            s.AddSingleton<IDiskSpaceProbe>(new FakeDiskSpace(PlentyOfDisk));
        });
        var artifacts = TempDir.Create("myriagent-publish-artifact");
        try
        {
            // 夾具把三個檔與 request.json 寫進資料夾；artifact 只要三個檔（request 由送件腳本自己寫）
            fixture.Write(artifacts, "fixture-unused", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            File.Delete(Path.Combine(artifacts, AppReleaseIngestRules.RequestFileName));
            var names = ReleasePackageRules.FileNames(fixture.Release);
            var store = host.Services.GetRequiredService<AppReleaseStore>();

            var first = await RunPublishAsync(artifacts, fixture.Release, HashesOf(artifacts, names), host.DataDir, "it-first");
            first.ExitCode.Should().Be(0, first.Output);
            first.Output.Should().Contain("\"message\":\"" + AppReleaseIngestCodes.Ingested + "\"");
            var row = await store.FindAsync(fixture.Release, CancellationToken.None);
            row.Should().NotBeNull();
            row!.State.Should().Be(AppReleaseStates.Available);

            var again = await RunPublishAsync(artifacts, fixture.Release, HashesOf(artifacts, names), host.DataDir, "it-again");
            again.ExitCode.Should().Be(0, again.Output);
            again.Output.Should().Contain("\"message\":\"" + AppReleaseIngestCodes.AlreadyIngested + "\"", "同一份 manifest 再送一次是冪等的");

            // 竄改：manifest 改一個位元組，雜湊照改過的檔給（腳本這一關會過），由服務的驗簽擋下
            var manifestPath = Path.Combine(artifacts, names.Manifest);
            var manifest = await File.ReadAllBytesAsync(manifestPath);
            manifest[manifest.Length / 2] ^= 0x01;
            await File.WriteAllBytesAsync(manifestPath, manifest);
            var tampered = await RunPublishAsync(artifacts, fixture.Release, HashesOf(artifacts, names), host.DataDir, "it-tampered");
            tampered.ExitCode.Should().Be(1, tampered.Output);
            tampered.Output.Should().Contain("\"ok\":false").And.Contain("\"message\":\"" + ReleasePackageSteps.Signature + "\"");
            (await store.FindAsync(fixture.Release, CancellationToken.None))!.ManifestSha256.Should().Be(row.ManifestSha256, "竄改的送件沒有動到已收的那一版");
        }
        finally
        {
            TempDir.TryDelete(artifacts);
        }
    }

    /// <summary>腳本裡 <c>$名稱 = '值'</c> 那一行的值（介面常數只宣告一次）</summary>
    private static string ScriptLiteral(string script, string name)
    {
        var matches = Regex.Matches(script, @"^\$" + name + @" = '([^']*)'\r?$", RegexOptions.Multiline);
        matches.Should().HaveCount(1, $"腳本只宣告一次 ${name}");
        return matches[0].Groups[1].Value;
    }

    private static List<string> HashesOf(string dir, (string Zip, string Manifest, string Signature) names) =>
        new[] { names.Zip, names.Manifest, names.Signature }.Select(n => ReleaseManifestRules.Sha256Hex(File.ReadAllBytes(Path.Combine(dir, n)))).ToList();

    /// <summary>以 Windows PowerShell 5.1 執行送件腳本（同 publish job：-NoProfile -ExecutionPolicy Bypass -File）</summary>
    private static async Task<(int ExitCode, string Output)> RunPublishAsync(string artifactDir, string release, IReadOnlyList<string> hashes,
        string dataRoot, string requestId, params string[] extra)
    {
        var start = new ProcessStartInfo(PowerShell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script,
            "-ArtifactDir", artifactDir, "-Release", release, "-ZipSha256", hashes[0], "-ManifestSha256", hashes[1], "-SigSha256", hashes[2],
            "-DataRoot", dataRoot, "-RequestId", requestId,
        })
            start.ArgumentList.Add(arg);
        foreach (var arg in extra)
            start.ArgumentList.Add(arg);
        // 從 PowerShell 7 開的環境繼承的 PSModulePath 讓 5.1 載不到部分內建指令：拿掉，powershell.exe 用自己的預設值
        start.Environment.Remove("PSModulePath");
        // 在 GitHub Actions 上跑測試時不寫進 job 摘要
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Publish-DesktopRelease.ps1 180 秒沒有結束");
        }
        return (process.ExitCode, await stdout + await stderr);
    }
}
