using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace MyriagentTools.UpdateE2e;

/// <summary>一個測試版本：版本＋40 碼 commit（前 7 碼＝build，全是十六進位）</summary>
internal sealed record TestRelease(string Version, string Commit)
{
    public string Build => Commit[..7];

    public string Release => $"{Version}-{Build}";

    /// <summary>app 日誌的啟動行（Program.cs：「啟動 Clode Myriagent {AppVersionInfo.Display}，…」）</summary>
    public string StartupLogLine => $"啟動 Clode Myriagent {Version}（{Build}）";

    /// <summary>0.2.9001-e2e0001、0.2.9002-e2e0002…（版本號遠大於正常開發版，build 一看就知道是測試版）</summary>
    public static TestRelease Of(int n) => new($"0.2.{9000 + n}", $"e2e{n:D4}" + new string('0', 33));
}

/// <summary>打包好的版本（還沒簽章：每個情境用不同金鑰簽）</summary>
internal sealed record ReleasePackage(TestRelease Release, string ZipPath, byte[] ManifestBytes);

internal static class ReleaseBuilder
{
    /// <summary>
    /// 以測試金鑰建出一個版本（framework-dependent，與正式發佈同樣的 publish）；回傳 publish 資料夾。
    /// 輸出（OutDir、-o）都在工作資料夾，不動 repo 的 bin；obj 與一般建置共用，下一次一般建置會重新產生組件資訊。
    /// 取消時結束整個 dotnet 程序樹（不讓它在背景繼續寫共用的 obj）
    /// </summary>
    public static async Task<string> PublishAsync(string repoRoot, TestRelease release, string testKeySpki, string workDir, CancellationToken ct)
    {
        var publish = Path.Combine(workDir, "publish", release.Release);
        var outDir = Path.Combine(workDir, "build", release.Release) + Path.DirectorySeparatorChar;
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repoRoot,
        };
        foreach (var arg in new[]
                 {
                     "publish", Path.Combine(repoRoot, "src", "Myriagent", "Myriagent.csproj"), "-c", "Release", "-o", publish,
                     $"-p:OutDir={outDir}", $"-p:Version={release.Version}", $"-p:SourceRevisionId={release.Commit}",
                     $"-p:MyriagentUpdateTestKeySpki={testKeySpki}", "-nodeReuse:false", "-p:UseSharedCompilation=false",
                 })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("無法啟動 dotnet");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception)
            {
                // 剛好自己結束了，或結束不了：照樣往外丟取消
            }
            throw;
        }
        var logName = $"publish-{release.Release}.log";
        await File.WriteAllTextAsync(Path.Combine(workDir, logName), await stdout + await stderr, new UTF8Encoding(false), ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet publish {release.Release} 失敗（結束碼 {process.ExitCode}），記錄在 {logName}");
        return publish;
    }

    /// <summary>
    /// 不會被安裝的假發佈（竄改、錯誤金鑰的情境）：幾個小檔就夠，因為在解壓前就被擋下。
    /// 這兩個情境會讓 app 把這一版記成壞版或這次執行不再重試，所以不能用之後要成功安裝的版本
    /// </summary>
    public static ReleasePackage FakePackage(TestRelease release, string workDir)
    {
        var dir = Path.Combine(workDir, "fake", release.Release);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Myriagent.exe"), "fake " + release.Release);
        File.WriteAllText(Path.Combine(dir, "Myriagent.dll"), "fake " + release.Release);
        return Package(dir, release, Path.Combine(workDir, "packages", release.Release));
    }

    /// <summary>
    /// publish 資料夾 → zip＋manifest（格式同設計 6.2、tests 的 ReleaseFixtures.ManifestBytes）：
    /// 檔案依路徑的序數排序、斜線分隔，zip 項目與 manifest 的清單同順序；createdAt 是 UTC 的 ISO 8601（不受地區設定影響）
    /// </summary>
    public static ReleasePackage Package(string publishDir, TestRelease release, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var zip = Path.Combine(outputDir, $"myriagent-{release.Release}.zip");
        File.Delete(zip);
        var files = Directory.EnumerateFiles(publishDir, "*", SearchOption.AllDirectories)
            .Select(full => (Path: Path.GetRelativePath(publishDir, full).Replace('\\', '/'), Full: full))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (path, full) in files)
                archive.CreateEntryFromFile(full, path, CompressionLevel.Optimal);
        }
        var entries = files
            .Select(f => new JObject
            {
                ["path"] = f.Path,
                ["size"] = new FileInfo(f.Full).Length,
                ["sha256"] = ReleaseManifestRules.Sha256Hex(File.ReadAllBytes(f.Full)),
            })
            .ToList();
        var manifest = new JObject
        {
            ["schema"] = 1,
            ["name"] = ReleaseManifestRules.AppName,
            ["release"] = release.Release,
            ["version"] = release.Version,
            ["build"] = release.Build,
            ["commit"] = release.Commit,
            ["createdAt"] = DateTime.UtcNow.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture),
            ["entry"] = "Myriagent.exe",
            ["minRuntime"] = new JObject { ["name"] = "Microsoft.WindowsDesktop.App", ["version"] = "10.0.0" },
            ["protocolVersion"] = 1,
            ["zip"] = Path.GetFileName(zip),
            ["zipSha256"] = ReleaseManifestRules.Sha256Hex(File.ReadAllBytes(zip)),
            ["fileCount"] = entries.Count,
            ["totalBytes"] = entries.Sum(f => (long)f["size"]!),
            ["files"] = new JArray(entries),
        };
        return new ReleasePackage(release, zip, new UTF8Encoding(false).GetBytes(manifest.ToString(Newtonsoft.Json.Formatting.Indented)));
    }

    /// <summary>
    /// 把版本放進本機來源（FileReleaseSource 讀的格式）：先拿掉 latest.json，放好 zip、manifest、簽章，最後才寫 latest.json，
    /// app 不會讀到一半的發佈。tamperZip＝簽章之後改掉 zip 中間的一個位元組（模擬鏡像站被竄改）
    /// </summary>
    public static void PublishToSource(string sourceDir, ReleasePackage package, ECDsa signer, bool tamperZip = false)
    {
        Directory.CreateDirectory(sourceDir);
        var latest = Path.Combine(sourceDir, "latest.json");
        RetryWhileOpen(() => File.Delete(latest));
        var name = $"myriagent-{package.Release.Release}";
        var zip = Path.Combine(sourceDir, name + ".zip");
        File.Copy(package.ZipPath, zip, overwrite: true);
        if (tamperZip)
        {
            using var stream = File.Open(zip, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.Position = stream.Length / 2;
            var b = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(b ^ 0xFF));
        }
        File.WriteAllBytes(Path.Combine(sourceDir, name + ".manifest.json"), package.ManifestBytes);
        File.WriteAllText(Path.Combine(sourceDir, name + ".manifest.sig"),
            ReleaseManifestRules.SerializeSignature(ReleaseManifestRules.Sign(package.ManifestBytes, signer)), new UTF8Encoding(false));
        var temp = latest + ".tmp";
        File.WriteAllText(temp, new JObject { ["release"] = package.Release.Release }.ToString(), new UTF8Encoding(false));
        RetryWhileOpen(() => File.Move(temp, latest, overwrite: true));
    }

    /// <summary>
    /// app 每 3 秒讀一次 latest.json（只共用讀取）：剛好開著時刪除丟 IOException、取代丟 UnauthorizedAccessException，
    /// 等一下再試（最多 10 次、每次 100 毫秒），不讓測試本身的時機問題變成失敗
    /// </summary>
    private static void RetryWhileOpen(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 10)
            {
                Thread.Sleep(100);
            }
        }
    }
}
