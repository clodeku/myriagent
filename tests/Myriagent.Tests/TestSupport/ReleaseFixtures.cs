using System.Security.Cryptography;
using System.Text;
using Myriagent.Security;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.TestSupport;

/// <summary>發佈 manifest 與簽章的測試資料（格式同 tools/deploy/New-MyriagentHubRelease.ps1 與設計 6.2）</summary>
internal static class ReleaseFixtures
{
    public static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static ReleaseKey KeyOf(ECDsa key)
    {
        var spki = P256KeyRules.ExportSpki(key);
        return new ReleaseKey(ReleaseManifestRules.ComputeKid(spki), spki);
    }

    public static byte[] ManifestBytes(string release, IReadOnlyList<ReleaseFile> files, string? zipSha256 = null, string name = "Myriagent",
        string entry = "Myriagent.exe", int protocolVersion = 1, string minRuntime = "10.0.0")
    {
        ReleaseIdentity.TryParseRelease(release, out var id).Should().BeTrue();
        var json = new JObject
        {
            ["schema"] = 1,
            ["name"] = name,
            ["release"] = release,
            ["version"] = id!.Version.ToString(),
            ["build"] = id.Build,
            ["commit"] = id.Build + new string('0', 33),
            ["createdAt"] = "2026-10-20T03:12:00Z",
            ["entry"] = entry,
            ["minRuntime"] = new JObject { ["name"] = "Microsoft.WindowsDesktop.App", ["version"] = minRuntime },
            ["protocolVersion"] = protocolVersion,
            ["zip"] = $"myriagent-{release}.zip",
            ["zipSha256"] = zipSha256 ?? new string('a', 64),
            ["fileCount"] = files.Count,
            ["totalBytes"] = files.Sum(f => f.Size),
            ["files"] = new JArray(files.Select(f => new JObject { ["path"] = f.Path, ["size"] = f.Size, ["sha256"] = f.Sha256 })),
        };
        return new UTF8Encoding(false).GetBytes(json.ToString(Newtonsoft.Json.Formatting.Indented));
    }

    public static ReleaseFile File(string path, string content)
        => new(path, Encoding.UTF8.GetByteCount(content), ReleaseManifestRules.Sha256Hex(Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// 在 sourceDir 寫出一個發佈（同 FileReleaseSource 讀的格式）：zip、manifest、簽章、latest.json。
    /// zipContents 給了就照它放進 zip（模擬竄改、多檔、zip-slip），manifest 一律依 files 計算
    /// </summary>
    public static string WriteRelease(string sourceDir, string release, IReadOnlyDictionary<string, string> files, ECDsa key,
        IReadOnlyDictionary<string, string>? zipContents = null, bool writeLatest = true)
    {
        Directory.CreateDirectory(sourceDir);
        var zipPath = Path.Combine(sourceDir, $"myriagent-{release}.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var (path, content) in zipContents ?? files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        var zipSha = ReleaseManifestRules.Sha256Hex(System.IO.File.ReadAllBytes(zipPath));
        var manifest = ManifestBytes(release, files.Select(kv => File(kv.Key, kv.Value)).ToList(), zipSha);
        System.IO.File.WriteAllBytes(Path.Combine(sourceDir, $"myriagent-{release}.manifest.json"), manifest);
        System.IO.File.WriteAllText(Path.Combine(sourceDir, $"myriagent-{release}.manifest.sig"),
            ReleaseManifestRules.SerializeSignature(ReleaseManifestRules.Sign(manifest, key)));
        if (writeLatest)
            System.IO.File.WriteAllText(Path.Combine(sourceDir, "latest.json"), new JObject { ["release"] = release }.ToString());
        return zipPath;
    }
}
