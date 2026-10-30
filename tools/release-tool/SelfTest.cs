using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Update;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentTools.ReleaseTool;

/// <summary>
/// myriagent-release selftest（U2 設計 T10）：在暫存資料夾、以暫時金鑰把每個指令跑一遍（經 ToolCommands，與 workflow 同一份程式），逐項印出結果。
/// 任何一項失敗結束碼 1，暫存資料夾保留供檢查；全部通過就刪掉。不碰 repo、不讀 DESKTOP_RELEASE_KEY、不寫真正的 GITHUB_OUTPUT、不呼叫真的 gh，
/// 開發機的金鑰檔只寫在暫存資料夾（不碰 %LOCALAPPDATA%\MyriagentRelease 的正式金鑰檔）
/// </summary>
internal sealed partial class SelfTest
{
    private const string Release = "0.2.1523-ab12cd3";
    private const string CreatedAt = "2026-10-20T03:12:00Z";

    /// <summary>40 碼小寫、以 build（ab12cd3）開頭</summary>
    private static readonly string Commit = "ab12cd3" + new string('e', 33);

    /// <summary>build 相同、完整雜湊不同：check-assembly 必須比對完整的 40 碼</summary>
    private static readonly string OtherCommit = "ab12cd3" + new string('f', 33);

    private static readonly DateTimeOffset CreatedAtValue = new(2026, 10, 20, 3, 12, 0, TimeSpan.Zero);

    /// <summary>假的發佈資料夾的 runtimeconfig.json（同 dotnet publish 寫的形狀）：package 由它決定 minRuntime（PIPE-1）</summary>
    private static string RuntimeConfig(string desktopVersion, string frameworksKey = "frameworks") =>
        "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"" + frameworksKey + "\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"" + desktopVersion
        + "\"},{\"name\":\"Microsoft.WindowsDesktop.App\",\"version\":\"" + desktopVersion + "\"}]}}";

    /// <summary>假的發佈資料夾寫的執行階段（RuntimeConfig("10.0.0")）</summary>
    private static readonly ReleaseRuntime SelfTestRuntime = new(ReleaseRuntimeRules.DesktopFramework, new ReleaseVersion(10, 0, 0));

    private static readonly (string Path, string Content)[] PublishFiles =
    [
        ("Myriagent.exe", "exe"),
        ("Myriagent.dll", "dll"),
        (ReleaseRuntimeRules.RuntimeConfigFileName, RuntimeConfig("10.0.0")),
        ("a.txt", "lower"),
        ("B.txt", "upper"),
        ("runtimes/win-x64/native/WebView2Loader.dll", "loader"),
    ];

    /// <summary>序數排序：大寫字母在小寫之前（B.txt、Myriagent.* 在 a.txt 之前）</summary>
    private static readonly string[] ExpectedOrder =
        ["B.txt", "Myriagent.dll", "Myriagent.exe", "Myriagent.runtimeconfig.json", "a.txt", "runtimes/win-x64/native/WebView2Loader.dll"];

    private readonly TextWriter _out;
    private readonly string _work;
    private readonly List<string> _failures = [];
    private int _passed;

    private SelfTest(TextWriter output, string work)
    {
        _out = output;
        _work = work;
    }

    public static async Task<int> RunAsync(TextWriter output, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "myriagent-release-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var test = new SelfTest(output, work);
        await test.RunAllAsync(ct);
        if (test._failures.Count > 0)
        {
            output.WriteLine($"暫存資料夾保留在 {work}");
        }
        else
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.WriteLine($"（暫存資料夾刪不掉，留在 {work}：{ex.Message}）");
            }
        }
        // 最後一行一律是總結（workflow 的記錄最後看得到）
        output.WriteLine($"selftest：{test._passed} 項通過、{test._failures.Count} 項失敗");
        return test._failures.Count > 0 ? ToolExit.Failed : ToolExit.Ok;
    }

    private async Task RunAllAsync(CancellationToken ct)
    {
        using var keyA = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var keyB = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var keyP384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var a = KeyOf(keyA);
        var b = KeyOf(keyB);
        var pkcs8A = Convert.ToBase64String(keyA.ExportPkcs8PrivateKey());
        var pkcs8B = Convert.ToBase64String(keyB.ExportPkcs8PrivateKey());
        var pkcs8P384 = Convert.ToBase64String(keyP384.ExportPkcs8PrivateKey());

        await SectionAsync("命令列", () => CommandLineAsync(ct));
        await SectionAsync("版本計算", () => VersionAsync(ct));
        GoodPackage? good = null;
        await SectionAsync("打包、簽章、驗證", async () => good = await PackageSignVerifyAsync(a, pkcs8A, Secrets(keyA, pkcs8A), ct));
        if (good != null)
        {
            await SectionAsync("竄改", () => TamperAsync(good, a, b, pkcs8A, pkcs8B, pkcs8P384, ct));
            await SectionAsync("開發機的金鑰檔", () => LocalKeyFileAsync(good, a, keyA, ct));
        }
        else
        {
            Check(false, "竄改：前一節沒有產生合格的套件，無法測");
        }
        await SectionAsync("打包的守門", () => PackageGuardsAsync(ct));
        await SectionAsync("組件檢查", () => CheckAssemblyAsync(ct));
        await SectionAsync("產生金鑰", () => KeyGenAsync(ct));
        await SectionAsync("產生金鑰交給 gh", () => KeyGenSecretAsync(ct));
    }

    // ---------------------------------------------------------------- 命令列

    private async Task CommandLineAsync(CancellationToken ct)
    {
        var none = await RunToolAsync([], null, ct);
        Check(none.Exit == ToolExit.Usage, "命令列：沒有指令是用法錯（結束碼 2）", none.All);
        var unknown = await RunToolAsync([], null, ct, "publish");
        Check(unknown.Exit == ToolExit.Usage, "命令列：不認得的指令是用法錯", unknown.All);
        var extra = await RunToolAsync([], null, ct, "verify", "--dir", _work, "--release", Release, "--force");
        Check(extra.Exit == ToolExit.Usage, "命令列：不認得的參數是用法錯", extra.All);
        var help = await RunToolAsync([], null, ct, "--help");
        Check(help.Exit == ToolExit.Ok && help.Out.Contains("selftest", StringComparison.Ordinal), "命令列：--help 印出用法、結束碼 0", help.All);
        var output = new GitHubOutput(Path.Combine(_work, "github-output-newline.txt"));
        var rejected = false;
        try
        {
            output.Set("release", "0.2.1523-ab12cd3\nkid=forged");
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        Check(rejected && !File.Exists(output.FilePath), "GITHUB_OUTPUT：值含換行就丟例外、什麼都不寫（不讓值夾帶別的輸出）");
    }

    // ---------------------------------------------------------------- 版本

    private async Task VersionAsync(CancellationToken ct)
    {
        var csproj = WriteFile("version/Myriagent.csproj", Csproj("<Version>0.2.0</Version>"));
        var ok = await RunToolAsync([], null, ct, "version", "--csproj", csproj, "--count", "1523", "--commit", Commit);
        Check(ok.Exit == ToolExit.Ok && ok.Outputs == "version=0.2.1523\nbuild=ab12cd3\nrelease=0.2.1523-ab12cd3\n",
            "version：0.2.0 加 N=1523 得到 0.2.1523、ab12cd3、0.2.1523-ab12cd3，寫進 GITHUB_OUTPUT", ok.All);
        var max = await RunToolAsync([], null, ct, "version", "--csproj", csproj, "--count", "65534", "--commit", Commit);
        Check(max.Exit == ToolExit.Ok && max.Outputs.Contains("release=0.2.65534-ab12cd3\n", StringComparison.Ordinal),
            "version：N=65534 是組件版本每段的上限，照收", max.All);
        var over = await RunToolAsync([], null, ct, "version", "--csproj", csproj, "--count", "65535", "--commit", Commit);
        Check(over.Exit == ToolExit.Failed && over.Outputs.Length == 0, "version：N=65535 超過上限，拒絕、不寫輸出", over.All);
        var zero = await RunToolAsync([], null, ct, "version", "--csproj", csproj, "--count", "0", "--commit", Commit);
        Check(zero.Exit == ToolExit.Failed, "version：N=0 拒絕", zero.All);
        var upper = await RunToolAsync([], null, ct, "version", "--csproj", csproj, "--count", "1523", "--commit", Commit.ToUpperInvariant());
        Check(upper.Exit == ToolExit.Failed, "version：commit 有大寫字母拒絕", upper.All);
        var shortCommit = await RunToolAsync([], null, ct, "version", "--csproj", csproj, "--count", "1523", "--commit", Commit[..39]);
        Check(shortCommit.Exit == ToolExit.Failed, "version：commit 只有 39 碼拒絕", shortCommit.All);
        var notNumber = await RunToolAsync([], null, ct, "version", "--csproj", csproj, "--count", "15x", "--commit", Commit);
        Check(notNumber.Exit == ToolExit.Usage, "version：N 不是數字是用法錯（結束碼 2）", notNumber.All);
        var missing = await RunToolAsync([], null, ct, "version", "--csproj", csproj, "--count", "1523");
        Check(missing.Exit == ToolExit.Usage, "version：少了 --commit 是用法錯", missing.All);
        var patch = WriteFile("version-patch/Myriagent.csproj", Csproj("<Version>0.2.1</Version>"));
        var patchRun = await RunToolAsync([], null, ct, "version", "--csproj", patch, "--count", "1523", "--commit", Commit);
        Check(patchRun.Exit == ToolExit.Failed, "version：csproj 的 <Version> 不是「主.次.0」就拒絕", patchRun.All);
        var two = WriteFile("version-two/Myriagent.csproj", Csproj("<Version>0.2.0</Version>\n    <Version>0.3.0</Version>"));
        var twoRun = await RunToolAsync([], null, ct, "version", "--csproj", two, "--count", "1523", "--commit", Commit);
        Check(twoRun.Exit == ToolExit.Failed, "version：csproj 有兩個 <Version> 就拒絕", twoRun.All);
    }

    // ---------------------------------------------------------------- 打包、簽章、驗證

    private sealed record GoodPackage(string Dir, byte[] ManifestBytes, ReleaseManifest Manifest);

    private async Task<GoodPackage?> PackageSignVerifyAsync(ReleaseKey a, string pkcs8A, IReadOnlyList<string> secretsA, CancellationToken ct)
    {
        var names = ReleasePackageRules.FileNames(Release);
        var publish = WritePublishDir("publish", PublishFiles);
        var outDir = Path.Combine(_work, "out");
        var package = await RunToolAsync([a], null, ct, "package", "--publish-dir", publish, "--release", Release, "--commit", Commit,
            "--out", outDir, "--created-at", CreatedAt);
        Check(package.Exit == ToolExit.Ok, "package：打包成功", package.All);
        if (package.Exit != ToolExit.Ok)
            return null;
        var zipPath = Path.Combine(outDir, names.Zip);
        var manifestPath = Path.Combine(outDir, names.Manifest);
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct);
        var parsed = ReleaseManifestRules.TryParse(manifestBytes, out var manifest, out var parseError);
        Check(parsed, "package：manifest 通過 ReleaseManifestRules.TryParse", parseError);
        if (!parsed)
            return null;

        var totalBytes = PublishFiles.Sum(f => Encoding.UTF8.GetByteCount(f.Content));
        var expectedOutputs = $"zip_name={names.Zip}\nzip_sha256={Sha256File(zipPath)}\nmanifest_name={names.Manifest}\n"
            + $"manifest_sha256={ReleaseManifestRules.Sha256Hex(manifestBytes)}\nfile_count={PublishFiles.Length}\ntotal_bytes={totalBytes}\n";
        Check(package.Outputs == expectedOutputs, "package：輸出值（檔名、兩個 SHA-256、檔數、總大小）與實際的檔案相符", package.Outputs);
        Check(manifest!.Files.Select(f => f.Path).SequenceEqual(ExpectedOrder),
            "package：清單依路徑的序數排序、斜線分隔", string.Join(", ", manifest.Files.Select(f => f.Path)));
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entries = zip.Entries.Select(e => e.FullName).ToList();
            Check(entries.SequenceEqual(ExpectedOrder), "package：zip 項目與清單同順序", string.Join(", ", entries));
            Check(zip.Entries.All(e => e.LastWriteTime.DateTime == CreatedAtValue.UtcDateTime),
                "package：zip 項目的時間都是 createdAt", string.Join(", ", zip.Entries.Select(e => e.LastWriteTime.DateTime.ToString("s"))));
        }
        Check(manifest.Entry == ReleasePackageRules.EntryExecutable && manifest.ProtocolVersion == SyncProtocol.Version
            && manifest.MinRuntime == SelfTestRuntime && manifest.Commit == Commit,
            "package：entry 是 Myriagent.exe、protocolVersion 是 SyncProtocol.Version、minRuntime 照 runtimeconfig.json（10.0.0）、commit 是完整雜湊");
        Check(manifestBytes[0] == (byte)'{' && Array.IndexOf(manifestBytes, (byte)'\r') < 0 && ManifestField(manifestBytes, "createdAt") == CreatedAt,
            "package：manifest 沒有 BOM、換行是 \\n、createdAt 是指定的時間");

        var again = await RunToolAsync([a], null, ct, "package", "--publish-dir", publish, "--release", Release, "--commit", Commit,
            "--out", Path.Combine(_work, "out-again"), "--created-at", CreatedAt);
        var againPath = Path.Combine(_work, "out-again", names.Manifest);
        Check(again.Exit == ToolExit.Ok && File.Exists(againPath) && File.ReadAllBytes(againPath).AsSpan().SequenceEqual(manifestBytes),
            "package：同樣的輸入與 createdAt 打包兩次，manifest 逐位元組相同", again.All);

        var sigPath = Path.Combine(outDir, names.Signature);
        var sign = await RunToolAsync([a], pkcs8A, ct, "sign", "--manifest", manifestPath, "--out", sigPath);
        Check(sign.Exit == ToolExit.Ok && File.Exists(sigPath), "sign：以正式清單裡的金鑰簽章成功", sign.All);
        if (sign.Exit != ToolExit.Ok || !File.Exists(sigPath))
            return null;
        var sigBytes = await File.ReadAllBytesAsync(sigPath, ct);
        Check(sign.Outputs == $"sig_name={names.Signature}\nsig_sha256={ReleaseManifestRules.Sha256Hex(sigBytes)}\nkid={a.Kid}\n",
            "sign：輸出 sig_name、sig_sha256、kid", sign.Outputs);
        Check(sigBytes.Length <= ReleaseManifestRules.MaxSignatureBytes
            && ReleaseManifestRules.TryParseSignature(Encoding.UTF8.GetString(sigBytes), out var signature) && signature.Kid == a.Kid,
            "sign：簽章檔不超過 1024 位元組、格式合法、kid 是簽章金鑰的");
        Check(!secretsA.Any(s => (package.All + sign.All).Contains(s, StringComparison.Ordinal)),
            "sign：標準輸出、標準錯誤與 GITHUB_OUTPUT 都不含私鑰（PKCS#8 的 base64、私鑰純量的 base64、base64url、十六進位）");

        var verify = await RunToolAsync([a], null, ct, "verify", "--dir", outDir, "--release", Release);
        Check(verify.Exit == ToolExit.Ok, "verify：打包、簽章之後驗證通過", verify.All);
        Check(File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(manifestBytes), "sign、verify 都不改寫 manifest：原文位元組不變");
        return new GoodPackage(outDir, manifestBytes, manifest);
    }

    // ---------------------------------------------------------------- 竄改

    private async Task TamperAsync(GoodPackage good, ReleaseKey a, ReleaseKey b, string pkcs8A, string pkcs8B, string pkcs8P384, CancellationToken ct)
    {
        var names = ReleasePackageRules.FileNames(Release);

        // manifest 改一個位元組（createdAt 的秒數 00 → 01），簽章不變
        var dir = CopyPackage(good.Dir, "tamper-manifest");
        var bytes = good.ManifestBytes.ToArray();
        var at = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(CreatedAt)) + CreatedAt.Length - 2;
        bytes[at] = (byte)'1';
        await File.WriteAllBytesAsync(Path.Combine(dir, names.Manifest), bytes, ct);
        await ExpectStepAsync(dir, [a], ReleasePackageSteps.Signature, "竄改：manifest 改一個位元組 → 在 signature 擋下", ct);
        var verifyTampered = await RunToolAsync([a], null, ct, "verify", "--dir", dir, "--release", Release);
        Check(verifyTampered.Exit == ToolExit.Failed, "verify：竄改過的套件結束碼 1", verifyTampered.All);

        // zip 多一項、少一項，manifest 與簽章不變
        dir = CopyPackage(good.Dir, "tamper-zip-extra");
        using (var zip = ZipFile.Open(Path.Combine(dir, names.Zip), ZipArchiveMode.Update))
            WriteEntry(zip, "extra.txt", "extra");
        await ExpectStepAsync(dir, [a], ReleasePackageSteps.ZipHash, "竄改：zip 多一項 → 在 zip_hash 擋下", ct);
        dir = CopyPackage(good.Dir, "tamper-zip-missing");
        using (var zip = ZipFile.Open(Path.Combine(dir, names.Zip), ZipArchiveMode.Update))
            zip.GetEntry("a.txt")!.Delete();
        await ExpectStepAsync(dir, [a], ReleasePackageSteps.ZipHash, "竄改：zip 少一項 → 在 zip_hash 擋下", ct);

        // 簽過的 manifest 照實寫了竄改後 zip 的雜湊，清單卻是原本的：只剩逐項比對擋得住
        await ExpectSignedButWrongEntriesAsync(good, a, pkcs8A, "signed-extra", zip => WriteEntry(zip, "extra.txt", "extra"),
            "竄改：zip 多一項、manifest 卻照實簽了它的雜湊 → 在 zip_entries 擋下", ct);
        await ExpectSignedButWrongEntriesAsync(good, a, pkcs8A, "signed-missing", zip => zip.GetEntry("a.txt")!.Delete(),
            "竄改：zip 少一項、manifest 卻照實簽了它的雜湊 → 在 zip_entries 擋下", ct);

        // 錯的 kid：B 只在它自己的清單裡，所以 sign 照簽；驗證只認 A
        dir = CopyPackage(good.Dir, "tamper-wrong-kid", withSignature: false);
        var signB = await RunToolAsync([b], pkcs8B, ct, "sign", "--manifest", Path.Combine(dir, names.Manifest), "--out", Path.Combine(dir, names.Signature));
        Check(signB.Exit == ToolExit.Ok, "準備：以另一把金鑰簽同一份 manifest", signB.All);
        await ExpectStepAsync(dir, [a], ReleasePackageSteps.Signature, "竄改：不認得的 kid → 在 signature 擋下", ct);

        // sign 的守門：都不寫簽章檔
        dir = CopyPackage(good.Dir, "sign-guards", withSignature: false);
        var manifestPath = Path.Combine(dir, names.Manifest);
        var sigPath = Path.Combine(dir, names.Signature);
        var notInProduction = await RunToolAsync([a], pkcs8B, ct, "sign", "--manifest", manifestPath, "--out", sigPath);
        Check(notInProduction.Exit == ToolExit.Failed && !File.Exists(sigPath) && notInProduction.Error.Contains("ReleaseKeys.Production", StringComparison.Ordinal),
            "sign：金鑰不在正式清單（例如還沒提交進 Production 的新金鑰）→ 拒絕、不寫簽章檔", notInProduction.All);
        var emptyProduction = await RunToolAsync([], pkcs8A, ct, "sign", "--manifest", manifestPath, "--out", sigPath);
        Check(emptyProduction.Exit == ToolExit.Failed && !File.Exists(sigPath) && emptyProduction.Error.Contains("New-DesktopReleaseKey.ps1", StringComparison.Ordinal),
            "sign：正式清單是空的（產生正式金鑰之前）→ 拒絕，指向 New-DesktopReleaseKey.ps1", emptyProduction.All);
        var p384 = await RunToolAsync([a], pkcs8P384, ct, "sign", "--manifest", manifestPath, "--out", sigPath);
        Check(p384.Exit == ToolExit.Failed && !File.Exists(sigPath) && p384.Error.Contains("P-256", StringComparison.Ordinal),
            "sign：不是 P-256 的私鑰 → 拒絕", p384.All);
        var noKey = await RunToolAsync([a], null, ct, "sign", "--manifest", manifestPath, "--out", sigPath);
        Check(noKey.Exit == ToolExit.Failed && noKey.Error.Contains(Signer.KeyVariable, StringComparison.Ordinal),
            "sign：沒有設定 DESKTOP_RELEASE_KEY → 拒絕", noKey.All);
        const string garbage = "not-base64!";
        var garbageRun = await RunToolAsync([a], garbage, ct, "sign", "--manifest", manifestPath, "--out", sigPath);
        Check(garbageRun.Exit == ToolExit.Failed && !garbageRun.All.Contains(garbage, StringComparison.Ordinal),
            "sign：私鑰不是 base64 → 拒絕，訊息不回顯內容", garbageRun.All);
        var protocolDir = CopyPackage(good.Dir, "sign-protocol", withSignature: false);
        var protocolManifest = ReleaseManifestRules.Build(good.Manifest.Identity, Commit, CreatedAtValue, ReleasePackageRules.EntryExecutable,
            SelfTestRuntime, SyncProtocol.Version + 1, good.Manifest.ZipSha256, good.Manifest.Files);
        await File.WriteAllBytesAsync(Path.Combine(protocolDir, names.Manifest), protocolManifest, ct);
        var protocolRun = await RunToolAsync([a], pkcs8A, ct, "sign", "--manifest", Path.Combine(protocolDir, names.Manifest),
            "--out", Path.Combine(protocolDir, names.Signature));
        Check(protocolRun.Exit == ToolExit.Failed && !File.Exists(Path.Combine(protocolDir, names.Signature))
            && protocolRun.Error.Contains(ReleasePackageSteps.Protocol, StringComparison.Ordinal),
            "sign：protocolVersion 不在支援範圍的 manifest 不簽（自我驗證在 protocol 擋下）", protocolRun.All);
        var bigDir = Path.Combine(_work, "sign-too-big");
        Directory.CreateDirectory(bigDir);
        var bigManifest = Path.Combine(bigDir, names.Manifest);
        await File.WriteAllBytesAsync(bigManifest, new byte[ReleaseManifestRules.MaxManifestBytes + 1], ct);
        var bigRun = await RunToolAsync([a], pkcs8A, ct, "sign", "--manifest", bigManifest, "--out", Path.Combine(bigDir, names.Signature));
        Check(bigRun.Exit == ToolExit.Failed && bigRun.Error.Contains("超過", StringComparison.Ordinal),
            "sign：manifest 超過 4 MiB 時讀檔之前就拒絕", bigRun.All);
        var otherSig = Path.Combine(dir, "other.manifest.sig");
        var wrongName = await RunToolAsync([a], pkcs8A, ct, "sign", "--manifest", manifestPath, "--out", otherSig);
        Check(wrongName.Exit == ToolExit.Failed && !File.Exists(otherSig), "sign：簽章檔名不是 myriagent-<release>.manifest.sig → 拒絕", wrongName.All);

        // verify：簽章檔超過 1024 位元組時讀檔之前就拒絕
        dir = CopyPackage(good.Dir, "verify-big-sig", withSignature: false);
        await File.WriteAllTextAsync(Path.Combine(dir, names.Signature), new string(' ', ReleaseManifestRules.MaxSignatureBytes + 1), ct);
        var bigSig = await RunToolAsync([a], null, ct, "verify", "--dir", dir, "--release", Release);
        Check(bigSig.Exit == ToolExit.Failed && bigSig.Error.Contains("超過", StringComparison.Ordinal),
            "verify：簽章檔超過 1024 位元組時讀檔之前就拒絕", bigSig.All);

        // verify：三個檔改名成另一個 release，manifest 卻是這一版
        const string otherRelease = "0.2.1524-ab12cd3";
        var otherNames = ReleasePackageRules.FileNames(otherRelease);
        dir = Path.Combine(_work, "renamed");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(good.Dir, names.Zip), Path.Combine(dir, otherNames.Zip));
        File.Copy(Path.Combine(good.Dir, names.Manifest), Path.Combine(dir, otherNames.Manifest));
        File.Copy(Path.Combine(good.Dir, names.Signature), Path.Combine(dir, otherNames.Signature));
        var renamed = await RunToolAsync([a], null, ct, "verify", "--dir", dir, "--release", otherRelease);
        Check(renamed.Exit == ToolExit.Failed, "verify：檔名的 release 與 manifest 不符 → 結束碼 1", renamed.All);
    }

    /// <summary>竄改被哪一步擋下：直接看 verify 用的同一個 Packager.VerifyDirectoryAsync 回的 Step（D1：只認傳入的清單，沒有撤銷清單）</summary>
    private async Task ExpectStepAsync(string dir, IReadOnlyList<ReleaseKey> trusted, string step, string name, CancellationToken ct)
    {
        var (result, error) = await Packager.VerifyDirectoryAsync(dir, Release, trusted, ct);
        Check(error == null && result is { Ok: false } && result.Step == step, name, error ?? $"{result?.Step}：{result?.Error}");
    }

    private async Task ExpectSignedButWrongEntriesAsync(GoodPackage good, ReleaseKey a, string pkcs8A, string folder, Action<ZipArchive> tamper,
        string name, CancellationToken ct)
    {
        var names = ReleasePackageRules.FileNames(Release);
        var dir = CopyPackage(good.Dir, folder, withSignature: false);
        var zipPath = Path.Combine(dir, names.Zip);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
            tamper(zip);
        var manifestBytes = ReleaseManifestRules.Build(good.Manifest.Identity, Commit, CreatedAtValue, ReleasePackageRules.EntryExecutable,
            SelfTestRuntime, SyncProtocol.Version, Sha256File(zipPath), good.Manifest.Files);
        await File.WriteAllBytesAsync(Path.Combine(dir, names.Manifest), manifestBytes, ct);
        var sign = await RunToolAsync([a], pkcs8A, ct, "sign", "--manifest", Path.Combine(dir, names.Manifest), "--out", Path.Combine(dir, names.Signature));
        Check(sign.Exit == ToolExit.Ok, $"準備（{folder}）：簽一份寫著竄改後 zip 雜湊的 manifest", sign.All);
        await ExpectStepAsync(dir, [a], ReleasePackageSteps.ZipEntries, name, ct);
    }

    // ---------------------------------------------------------------- 打包的守門

    private async Task PackageGuardsAsync(CancellationToken ct)
    {
        var reserved = WritePublishDir("publish-reserved", [.. PublishFiles, ("release.manifest.json", "{}")]);
        var reservedOut = Path.Combine(_work, "out-reserved");
        var reservedRun = await RunToolAsync([], null, ct, "package", "--publish-dir", reserved, "--release", Release, "--commit", Commit,
            "--out", reservedOut, "--created-at", CreatedAt);
        Check(reservedRun.Exit == ToolExit.Failed && IsEmptyOrMissing(reservedOut),
            "package：發佈資料夾有保留檔名 release.manifest.json → 拒絕（發佈政策），不留下半成品", reservedRun.All);

        var noExe = WritePublishDir("publish-no-exe", PublishFiles.Where(f => f.Path != ReleasePackageRules.EntryExecutable));
        var noExeOut = Path.Combine(_work, "out-no-exe");
        var noExeRun = await RunToolAsync([], null, ct, "package", "--publish-dir", noExe, "--release", Release, "--commit", Commit,
            "--out", noExeOut, "--created-at", CreatedAt);
        Check(noExeRun.Exit == ToolExit.Failed && IsEmptyOrMissing(noExeOut) && noExeRun.Error.Contains(ReleasePackageRules.EntryExecutable, StringComparison.Ordinal),
            "package：沒有 Myriagent.exe → 寫 zip 之前就拒絕", noExeRun.All);

        // PIPE-1：minRuntime 照發佈資料夾的 runtimeconfig.json，不再寫死 10.0.0
        var noConfig = WritePublishDir("publish-no-runtimeconfig", PublishFiles.Where(f => f.Path != ReleaseRuntimeRules.RuntimeConfigFileName));
        var noConfigOut = Path.Combine(_work, "out-no-runtimeconfig");
        var noConfigRun = await RunToolAsync([], null, ct, "package", "--publish-dir", noConfig, "--release", Release, "--commit", Commit,
            "--out", noConfigOut, "--created-at", CreatedAt);
        Check(noConfigRun.Exit == ToolExit.Failed && IsEmptyOrMissing(noConfigOut)
            && noConfigRun.Error.Contains(ReleaseRuntimeRules.RuntimeConfigFileName, StringComparison.Ordinal),
            "package：沒有 Myriagent.runtimeconfig.json → 寫 zip 之前就拒絕（minRuntime 由它決定）", noConfigRun.All);
        var selfContained = WritePublishDir("publish-self-contained",
            PublishFiles.Select(f => f.Path == ReleaseRuntimeRules.RuntimeConfigFileName ? (f.Path, RuntimeConfig("10.0.0", "includedFrameworks")) : f));
        var selfContainedOut = Path.Combine(_work, "out-self-contained");
        var selfContainedRun = await RunToolAsync([], null, ct, "package", "--publish-dir", selfContained, "--release", Release, "--commit", Commit,
            "--out", selfContainedOut, "--created-at", CreatedAt);
        Check(selfContainedRun.Exit == ToolExit.Failed && IsEmptyOrMissing(selfContainedOut),
            "package：self-contained 的發佈（includedFrameworks）→ 拒絕", selfContainedRun.All);
        var newer = WritePublishDir("publish-runtime-10-0-4",
            PublishFiles.Select(f => f.Path == ReleaseRuntimeRules.RuntimeConfigFileName ? (f.Path, RuntimeConfig("10.0.4")) : f));
        var newerOut = Path.Combine(_work, "out-runtime-10-0-4");
        var newerRun = await RunToolAsync([], null, ct, "package", "--publish-dir", newer, "--release", Release, "--commit", Commit,
            "--out", newerOut, "--created-at", CreatedAt);
        var newerManifest = Path.Combine(newerOut, ReleasePackageRules.FileNames(Release).Manifest);
        Check(newerRun.Exit == ToolExit.Ok && File.Exists(newerManifest)
            && ReleaseManifestRules.TryParse(File.ReadAllBytes(newerManifest), out var newerParsed, out _)
            && newerParsed.MinRuntime == new ReleaseRuntime(ReleaseRuntimeRules.DesktopFramework, new ReleaseVersion(10, 0, 4)),
            "package：runtimeconfig.json 要 Microsoft.WindowsDesktop.App 10.0.4 → manifest 的 minRuntime 是 10.0.4", newerRun.All);

        var publish = WritePublishDir("publish-guards", PublishFiles);
        var inside = await RunToolAsync([], null, ct, "package", "--publish-dir", publish, "--release", Release, "--commit", Commit,
            "--out", Path.Combine(publish, "out"), "--created-at", CreatedAt);
        Check(inside.Exit == ToolExit.Failed && !Directory.Exists(Path.Combine(publish, "out")), "package：輸出資料夾在發佈資料夾裡面 → 拒絕", inside.All);
        var wrongBuild = await RunToolAsync([], null, ct, "package", "--publish-dir", publish, "--release", Release, "--commit", "ff12cd3" + new string('e', 33),
            "--out", Path.Combine(_work, "out-wrong-build"), "--created-at", CreatedAt);
        Check(wrongBuild.Exit == ToolExit.Failed, "package：commit 不以 release 的 build 開頭 → 拒絕", wrongBuild.All);
        var badTime = await RunToolAsync([], null, ct, "package", "--publish-dir", publish, "--release", Release, "--commit", Commit,
            "--out", Path.Combine(_work, "out-bad-time"), "--created-at", "2026-10-20 03:12:00");
        Check(badTime.Exit == ToolExit.Usage, "package：--created-at 不是 yyyy-MM-ddTHH:mm:ssZ 是用法錯", badTime.All);
    }

    // ---------------------------------------------------------------- 組件檢查

    private async Task CheckAssemblyAsync(CancellationToken ct)
    {
        var informational = $"0.2.1523+{Commit}";
        using var testKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var testSpki = P256KeyRules.ExportSpki(testKey);
        var clean = WriteDesktopPublish("asm-clean", informational, testKeySpki: null, withExe: true);
        var withTestKey = WriteDesktopPublish("asm-test-key", informational, testSpki, withExe: true);

        // 正向對照：讀得出 PersistedAssemblyBuilder 寫進去的屬性，「沒有測試金鑰」不是因為讀不到
        var facts = AssemblyChecks.Read(Path.Combine(withTestKey, AssemblyChecks.DesktopDll));
        Check(facts.Name == AssemblyChecks.DesktopAssemblyName && facts.InformationalVersion == informational
            && facts.Metadata.Any(m => m.Key == ReleaseKeys.TestKeyMetadata && m.Value == testSpki),
            "check-assembly：讀得出組件名稱、InformationalVersion 與測試金鑰的中繼資料（正向對照）",
            $"{facts.Name} {facts.InformationalVersion} {string.Join(", ", facts.Metadata.Select(m => m.Key))}");

        var ok = await RunToolAsync([], null, ct, "check-assembly", "--publish-dir", clean, "--release", Release, "--commit", Commit);
        Check(ok.Exit == ToolExit.Ok, "check-assembly：沒有測試金鑰、InformationalVersion 是 version+完整雜湊 → 通過", ok.All);
        var bad = await RunToolAsync([], null, ct, "check-assembly", "--publish-dir", withTestKey, "--release", Release, "--commit", Commit);
        Check(bad.Exit == ToolExit.Failed && bad.Error.Contains(ReleaseKeys.TestKeyMetadata, StringComparison.Ordinal),
            "check-assembly：帶著測試金鑰的中繼資料 → 拒絕", bad.All);
        var otherCommit = WriteDesktopPublish("asm-other-commit", $"0.2.1523+{OtherCommit}", testKeySpki: null, withExe: true);
        var otherCommitRun = await RunToolAsync([], null, ct, "check-assembly", "--publish-dir", otherCommit, "--release", Release, "--commit", Commit);
        Check(otherCommitRun.Exit == ToolExit.Failed, "check-assembly：InformationalVersion 是別的提交（build 相同也不行）→ 拒絕", otherCommitRun.All);
        var dev = WriteDesktopPublish("asm-dev", "0.2.1523", testKeySpki: null, withExe: true);
        var devRun = await RunToolAsync([], null, ct, "check-assembly", "--publish-dir", dev, "--release", Release, "--commit", Commit);
        Check(devRun.Exit == ToolExit.Failed, "check-assembly：InformationalVersion 沒有提交雜湊（開發版）→ 拒絕", devRun.All);
        var otherVersion = WriteDesktopPublish("asm-other-version", $"0.2.1522+{Commit}", testKeySpki: null, withExe: true);
        var otherVersionRun = await RunToolAsync([], null, ct, "check-assembly", "--publish-dir", otherVersion, "--release", Release, "--commit", Commit);
        Check(otherVersionRun.Exit == ToolExit.Failed, "check-assembly：版本不是這次的 → 拒絕", otherVersionRun.All);
        var noExe = WriteDesktopPublish("asm-no-exe", informational, testKeySpki: null, withExe: false);
        var noExeRun = await RunToolAsync([], null, ct, "check-assembly", "--publish-dir", noExe, "--release", Release, "--commit", Commit);
        Check(noExeRun.Exit == ToolExit.Failed, "check-assembly：沒有 Myriagent.exe → 拒絕", noExeRun.All);
    }

    // ---------------------------------------------------------------- 產生金鑰

    private async Task KeyGenAsync(CancellationToken ct)
    {
        var run = await RunToolAsync([], null, ct, "keygen", "--dry-run");
        var lines = run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        var match = lines.Count == 1 ? PublicLine().Match(lines[0]) : Match.Empty;
        Check(run.Exit == ToolExit.Ok && match.Success, "keygen --dry-run：標準輸出只有一行 new ReleaseKey(\"<kid>\", \"<spki>\")", run.Out);
        Check(match.Success && P256KeyRules.IsValidSpki(match.Groups[2].Value) && ReleaseManifestRules.ComputeKid(match.Groups[2].Value) == match.Groups[1].Value,
            "keygen --dry-run：spki 是 P-256 公鑰、kid 等於 ComputeKid(spki)", run.Out);
        var none = await RunToolAsync([], null, ct, "keygen");
        Check(none.Exit == ToolExit.Usage, "keygen：沒有 --set-secret／--repo，也沒有 --dry-run → 用法錯", none.All);
        var both = await RunToolAsync([], null, ct, "keygen", "--dry-run", "--set-secret", "DESKTOP_RELEASE_KEY", "--repo", "example-user/myriagent");
        Check(both.Exit == ToolExit.Usage, "keygen：--dry-run 不能和 --set-secret 一起用 → 用法錯", both.All);
    }

    // ---------------------------------------------------------------- 開發機的金鑰檔

    /// <summary>keygen --dpapi-file 與 sign --key-file（DPAPI，CurrentUser）：金鑰檔只寫在暫存資料夾</summary>
    private async Task LocalKeyFileAsync(GoodPackage good, ReleaseKey a, ECDsa keyA, CancellationToken ct)
    {
        var names = ReleasePackageRules.FileNames(Release);
        var keyPath = Path.Combine(_work, "local-key", "desktop-release-key.dpapi");
        var gen = await RunToolAsync([], null, ct, "keygen", "--dpapi-file", keyPath);
        var lines = gen.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        var match = lines.Count == 1 ? PublicLine().Match(lines[0]) : Match.Empty;
        Check(gen.Exit == ToolExit.Ok && File.Exists(keyPath) && match.Success,
            "keygen --dpapi-file：寫出金鑰檔，標準輸出只有公開的一行 new ReleaseKey(\"<kid>\", \"<spki>\")", gen.All);
        if (!match.Success || !File.Exists(keyPath))
            return;
        var local = new ReleaseKey(match.Groups[1].Value, match.Groups[2].Value);
        var blob = await File.ReadAllBytesAsync(keyPath, ct);
        Check(!IsPkcs8(blob), "keygen --dpapi-file：檔案是 DPAPI 加密過的，不是 PKCS#8 原文");
        var again = await RunToolAsync([], null, ct, "keygen", "--dpapi-file", keyPath);
        Check(again.Exit == ToolExit.Failed && File.ReadAllBytes(keyPath).AsSpan().SequenceEqual(blob),
            "keygen --dpapi-file：金鑰檔已經存在 → 拒絕、不覆蓋", again.All);
        var otherPath = Path.Combine(_work, "local-key", "other.dpapi");
        var mixed = await RunToolAsync([], null, ct, "keygen", "--dpapi-file", otherPath, "--set-secret", "DESKTOP_RELEASE_KEY", "--repo", "example-user/myriagent");
        Check(mixed.Exit == ToolExit.Usage && !File.Exists(otherPath), "keygen：--dpapi-file 不能和 --set-secret 一起用 → 用法錯、不寫檔", mixed.All);

        // 環境變數給的是另一把（a）：--key-file 時不讀它
        var dir = CopyPackage(good.Dir, "sign-key-file", withSignature: false);
        var sigPath = Path.Combine(dir, names.Signature);
        var sign = await RunToolAsync([a, local], Convert.ToBase64String(keyA.ExportPkcs8PrivateKey()), ct,
            "sign", "--manifest", Path.Combine(dir, names.Manifest), "--out", sigPath, "--key-file", keyPath);
        Check(sign.Exit == ToolExit.Ok && File.Exists(sigPath) && sign.Outputs.EndsWith($"kid={local.Kid}\n", StringComparison.Ordinal),
            "sign --key-file：以金鑰檔簽章成功，kid 是金鑰檔那一把（不讀環境變數）", sign.All);
        var verify = await RunToolAsync([local], null, ct, "verify", "--dir", dir, "--release", Release);
        Check(verify.Exit == ToolExit.Ok, "verify：金鑰檔簽的套件以只含新金鑰的清單驗證通過", verify.All);

        dir = CopyPackage(good.Dir, "sign-key-file-guards", withSignature: false);
        sigPath = Path.Combine(dir, names.Signature);
        var manifestPath = Path.Combine(dir, names.Manifest);
        var untrusted = await RunToolAsync([a], null, ct, "sign", "--manifest", manifestPath, "--out", sigPath, "--key-file", keyPath);
        Check(untrusted.Exit == ToolExit.Failed && !File.Exists(sigPath) && untrusted.Error.Contains("ReleaseKeys.Production", StringComparison.Ordinal),
            "sign --key-file：金鑰檔的金鑰不在正式清單 → 拒絕、不寫簽章檔", untrusted.All);
        var missing = await RunToolAsync([local], null, ct, "sign", "--manifest", manifestPath, "--out", sigPath, "--key-file", otherPath);
        Check(missing.Exit == ToolExit.Failed && !File.Exists(sigPath) && missing.Error.Contains("找不到金鑰檔", StringComparison.Ordinal),
            "sign --key-file：找不到金鑰檔 → 拒絕", missing.All);
        var broken = WriteFile("local-key/broken.dpapi", "not a dpapi blob");
        var brokenRun = await RunToolAsync([local], null, ct, "sign", "--manifest", manifestPath, "--out", sigPath, "--key-file", broken);
        Check(brokenRun.Exit == ToolExit.Failed && !File.Exists(sigPath) && brokenRun.Error.Contains("解不開", StringComparison.Ordinal),
            "sign --key-file：金鑰檔不是 DPAPI 的資料 → 拒絕", brokenRun.All);
        var raw = Path.Combine(_work, "local-key", "raw-pkcs8.dpapi");
        await File.WriteAllBytesAsync(raw, keyA.ExportPkcs8PrivateKey(), ct);
        var rawRun = await RunToolAsync([a], null, ct, "sign", "--manifest", manifestPath, "--out", sigPath, "--key-file", raw);
        Check(rawRun.Exit == ToolExit.Failed && !File.Exists(sigPath) && rawRun.Error.Contains("解不開", StringComparison.Ordinal),
            "sign --key-file：沒有加密的 PKCS#8 原文 → 拒絕（金鑰檔一定是 DPAPI 加密過的）", rawRun.All);
    }

    private static bool IsPkcs8(byte[] bytes)
    {
        using var probe = ECDsa.Create();
        try
        {
            probe.ImportPkcs8PrivateKey(bytes, out _);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// 假的 gh：把 stdin 與參數寫進檔案，再以指定的結束碼結束。只用 .NET 型別（不載入任何模組），
    /// 從 PowerShell 7 啟動、繼承了它的 PSModulePath 也能跑；只寫 ASCII（5.1 把無 BOM 的檔當 ANSI 讀）
    /// </summary>
    private const string FakeGhScript = """
        # Fake gh for myriagent-release selftest: records stdin and the arguments, then exits with the given code.
        $stdinFile = $args[0]
        $argsFile = $args[1]
        $exitCode = [int]$args[2]
        [IO.File]::WriteAllText($stdinFile, [Console]::In.ReadToEnd())
        [IO.File]::WriteAllText($argsFile, ($args[3..($args.Count - 1)] -join '|'))
        [Console]::Error.WriteLine('fake gh: secret received')
        exit $exitCode

        """;

    private async Task KeyGenSecretAsync(CancellationToken ct)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
        {
            Check(false, "keygen：找不到 Windows PowerShell，無法以假的 gh 測試", powershell);
            return;
        }
        var script = WriteFile("fake-gh/fake-gh.ps1", FakeGhScript);
        var stdinFile = Path.Combine(_work, "fake-gh", "stdin.txt");
        var argsFile = Path.Combine(_work, "fake-gh", "args.txt");
        GhCommand Fake(string exitCode) => new(powershell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, stdinFile, argsFile, exitCode]);

        // 名稱不合規則在產生金鑰之前就擋下：連假的 gh 都沒有被叫起來（用假的 gh，規則壞掉也不會碰到真的 gh）
        var badName = await RunToolWithGhAsync(Fake("0"), ct, "keygen", "--set-secret", "GITHUB_TOKEN", "--repo", "example-user/myriagent");
        Check(badName.Exit == ToolExit.Usage && !File.Exists(argsFile), "keygen：secret 名稱不合 GitHub 的規則 → 用法錯，沒有呼叫 gh", badName.All);

        var ok = await RunToolWithGhAsync(Fake("0"), ct, "keygen", "--set-secret", "DESKTOP_RELEASE_KEY", "--repo", "example-user/myriagent");
        var lines = ok.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        var match = lines.Count == 1 ? PublicLine().Match(lines[0]) : Match.Empty;
        Check(ok.Exit == ToolExit.Ok && match.Success, "keygen：gh 成功時標準輸出只有公開的那一行", ok.All);
        var arguments = File.Exists(argsFile) ? await File.ReadAllTextAsync(argsFile, ct) : string.Empty;
        Check(arguments == "secret|set|DESKTOP_RELEASE_KEY|--repo|example-user/myriagent",
            "keygen：執行的是 gh secret set DESKTOP_RELEASE_KEY --repo example-user/myriagent", arguments);
        var received = File.Exists(stdinFile) ? await File.ReadAllTextAsync(stdinFile, ct) : string.Empty;
        Check(match.Success && SpkiOfPkcs8(received) == match.Groups[2].Value,
            "keygen：gh 的 stdin 收到 PKCS#8 base64 的私鑰（沒有換行與 BOM），正好對應印出的公鑰");
        Check(received.Length > 0 && !ok.All.Contains(received, StringComparison.Ordinal),
            "keygen：私鑰只經 gh 的 stdin，標準輸出、標準錯誤都沒有它");

        var failed = await RunToolWithGhAsync(Fake("1"), ct, "keygen", "--set-secret", "DESKTOP_RELEASE_KEY", "--repo", "example-user/myriagent");
        Check(failed.Exit == ToolExit.Failed && failed.Out.Length == 0 && failed.Error.Contains("gh secret set", StringComparison.Ordinal),
            "keygen：gh 失敗時結束碼 1、不印公鑰那一行（不會提交一把私鑰沒進 secret 的公鑰）", failed.All);
    }

    /// <summary>PKCS#8 base64（不容許多餘的字元）→ 對應的 SPKI；不是合法的私鑰回 null</summary>
    private static string? SpkiOfPkcs8(string base64)
    {
        if (base64.Length == 0 || base64.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')))
            return null;
        try
        {
            using var key = ECDsa.Create();
            var der = Convert.FromBase64String(base64);
            key.ImportPkcs8PrivateKey(der, out var read);
            return read == der.Length ? P256KeyRules.ExportSpki(key) : null;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 工具

    private sealed record ToolRun(int Exit, string Out, string Error, string Outputs)
    {
        public string All => Out + Error + Outputs;
    }

    /// <summary>
    /// 以暫時的正式清單、私鑰與 GITHUB_OUTPUT 檔執行一個指令（同一份 ToolCommands）。gh 指向暫存資料夾裡不存在的檔：
    /// 這些指令都不該呼叫 gh，萬一呼叫了也只會啟動失敗，碰不到真的 gh
    /// </summary>
    private Task<ToolRun> RunToolAsync(IReadOnlyList<ReleaseKey> production, string? signingKey, CancellationToken ct, params string[] args)
        => RunCoreAsync(production, signingKey, new GhCommand(Path.Combine(_work, "no-gh", "gh.exe"), []), args, ct);

    /// <summary>同上，但 gh 換成假的</summary>
    private Task<ToolRun> RunToolWithGhAsync(GhCommand gh, CancellationToken ct, params string[] args)
        => RunCoreAsync([], null, gh, args, ct);

    private async Task<ToolRun> RunCoreAsync(IReadOnlyList<ReleaseKey> production, string? signingKey, GhCommand gh, string[] args, CancellationToken ct)
    {
        var outputs = Path.Combine(_work, "github-output", Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outputs)!);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = new ToolContext(production, () => signingKey, new GitHubOutput(outputs), stdout, stderr, gh);
        var exit = await ToolCommands.RunAsync(args, context, ct);
        var written = File.Exists(outputs) ? await File.ReadAllTextAsync(outputs, ct) : string.Empty;
        return new ToolRun(exit, stdout.ToString(), stderr.ToString(), written);
    }

    private async Task SectionAsync(string name, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Check(false, $"{name}：未預期的例外", ex.ToString());
        }
    }

    private void Check(bool condition, string name, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            _out.WriteLine($"[通過] {name}");
            return;
        }
        _failures.Add(name);
        _out.WriteLine($"[失敗] {name}");
        if (!string.IsNullOrEmpty(detail))
            _out.WriteLine("       " + detail.TrimEnd().Replace("\n", "\n       "));
    }

    private string WriteFile(string relative, string content)
    {
        var path = Path.Combine(_work, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private string WritePublishDir(string folder, IEnumerable<(string Path, string Content)> files)
    {
        var dir = Path.Combine(_work, folder);
        Directory.CreateDirectory(dir);
        foreach (var (path, content) in files)
            WriteFile($"{folder}/{path}", content);
        return dir;
    }

    private string CopyPackage(string from, string folder, bool withSignature = true)
    {
        var names = ReleasePackageRules.FileNames(Release);
        var dir = Path.Combine(_work, folder);
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(from, names.Zip), Path.Combine(dir, names.Zip));
        File.Copy(Path.Combine(from, names.Manifest), Path.Combine(dir, names.Manifest));
        if (withSignature)
            File.Copy(Path.Combine(from, names.Signature), Path.Combine(dir, names.Signature));
        return dir;
    }

    /// <summary>以 PersistedAssemblyBuilder 寫出一個名叫 Myriagent 的組件（只有組件層級的屬性），可選擇帶測試金鑰的中繼資料</summary>
    private string WriteDesktopPublish(string folder, string informational, string? testKeySpki, bool withExe)
    {
        var dir = Path.Combine(_work, folder);
        Directory.CreateDirectory(dir);
        var builder = new PersistedAssemblyBuilder(
            new AssemblyName(AssemblyChecks.DesktopAssemblyName) { Version = new Version(0, 2, 1523, 0) }, typeof(object).Assembly);
        builder.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor(new[] { typeof(string) })!, new object[] { informational }));
        if (testKeySpki != null)
        {
            builder.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(AssemblyMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!,
                new object[] { ReleaseKeys.TestKeyMetadata, testKeySpki }));
        }
        builder.DefineDynamicModule(AssemblyChecks.DesktopAssemblyName);
        builder.Save(Path.Combine(dir, AssemblyChecks.DesktopDll));
        if (withExe)
            File.WriteAllText(Path.Combine(dir, ReleasePackageRules.EntryExecutable), "exe");
        return dir;
    }

    private static string Csproj(string versionLines)
        => $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    {versionLines}\n  </PropertyGroup>\n</Project>\n";

    private static bool IsEmptyOrMissing(string dir) => !Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any();

    private static void WriteEntry(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string? ManifestField(byte[] manifestBytes, string name)
    {
        using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(manifestBytes))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader)[name]?.ToString();
    }

    private static ReleaseKey KeyOf(ECDsa key)
    {
        var spki = P256KeyRules.ExportSpki(key);
        return new ReleaseKey(ReleaseManifestRules.ComputeKid(spki), spki);
    }

    /// <summary>私鑰可能外洩的幾種寫法：PKCS#8 的 base64，以及私鑰純量 D 的 base64、base64url、十六進位（大小寫）</summary>
    private static IReadOnlyList<string> Secrets(ECDsa key, string pkcs8Base64)
    {
        var d = key.ExportParameters(includePrivateParameters: true).D!;
        return [pkcs8Base64, Convert.ToBase64String(d), Base64UrlRules.Encode(d), Convert.ToHexStringLower(d), Convert.ToHexString(d)];
    }

    [GeneratedRegex(@"^new ReleaseKey\(""([0-9a-f]{16})"", ""([A-Za-z0-9_-]+)""\)$")]
    private static partial Regex PublicLine();
}
