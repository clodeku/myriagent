using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Update;
using MyriagentHub.Releases;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>
/// 假的桌面版發佈（U2 T4）：在暫存資料夾產生 Myriagent.exe 加幾個檔，以 <see cref="ReleaseManifestRules.Build"/> 產生 manifest、
/// 建 zip、用暫時的 ECDsa P-256 金鑰簽。<see cref="Write(string, string, long)"/> 照送件腳本的順序放進收件匣：先三個檔，最後原子寫 request.json。
/// 竄改用的變體：<see cref="Build"/> 的參數（換版本號、zip 多一項、換 entry、換協定版本、清單加保留檔名、換金鑰、換 createdAt）、
/// <see cref="FlipByte"/>（改一個位元組）、<see cref="Sign"/>（替任意內容簽章）
/// </summary>
internal sealed class ReleasePackageFixture : IDisposable
{
    public const string DefaultRelease = "0.2.300-ab12cd3";

    /// <summary>manifest 的 createdAt 與 zip 項目的時間：固定，同一份內容產生的 zip 逐位元組相同</summary>
    public static readonly DateTimeOffset DefaultCreatedAt = new(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);

    public static readonly ReleaseRuntime MinRuntime = new("Microsoft.WindowsDesktop.App", new ReleaseVersion(10, 0, 0));

    private static readonly IReadOnlyDictionary<string, string> DefaultContents = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ReleasePackageRules.EntryExecutable] = "fake exe",
        ["Myriagent.dll"] = "fake dll",
        ["Web/app.js"] = "console.log('fake');",
    };

    private readonly ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly List<ECDsa> otherKeys = [];
    private int builds;

    public ReleasePackageFixture()
    {
        WorkDir = TempDir.Create("myriagenthub-release");
        Keys = [KeyOf(signer)];
        Default = Build();
    }

    /// <summary>暫存資料夾：每次 <see cref="Build"/> 的發佈內容放在底下的 publish-&lt;n&gt;\</summary>
    public string WorkDir { get; }

    /// <summary>簽章金鑰的公鑰（給 <see cref="TestBuiltInReleaseKeys"/>）</summary>
    public ReleaseKey[] Keys { get; }

    /// <summary>預設的套件：<see cref="DefaultRelease"/>，以 <see cref="Keys"/> 那把金鑰簽</summary>
    public Package Default { get; }

    public string Release => Default.Release;

    public static ReleasePackageFixture Create() => new();

    /// <summary>
    /// 產生一個套件。<paramref name="extraFiles"/> 加進發佈內容（也進 manifest 與 zip）；<paramref name="zipOnlyFile"/> 只加進 zip
    /// （zip 的雜湊照實際內容算，項目比對才會不符）；<paramref name="key"/> 預設是 <see cref="Keys"/> 那把
    /// </summary>
    public Package Build(
        string release = DefaultRelease,
        IReadOnlyDictionary<string, string>? extraFiles = null,
        (string Path, string Content)? zipOnlyFile = null,
        string entry = ReleasePackageRules.EntryExecutable,
        int protocolVersion = SyncProtocol.Version,
        DateTimeOffset? createdAt = null,
        ECDsa? key = null)
    {
        if (!ReleaseIdentity.TryParseRelease(release, out var identity))
            throw new ArgumentException($"不是合法的 release：{release}", nameof(release));
        var publish = Path.Combine(WorkDir, "publish-" + Interlocked.Increment(ref builds).ToString(CultureInfo.InvariantCulture));
        var contents = new Dictionary<string, string>(DefaultContents, StringComparer.Ordinal);
        foreach (var (path, content) in extraFiles ?? new Dictionary<string, string>())
            contents[path] = content;
        var files = new List<ReleaseFile>();
        foreach (var (path, content) in contents.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var full = LocalPath(publish, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
            var bytes = File.ReadAllBytes(full);
            files.Add(new ReleaseFile(path, bytes.Length, ReleaseManifestRules.Sha256Hex(bytes)));
        }
        var zip = CreateZip(publish, files, zipOnlyFile);
        // 完整提交雜湊：build 加上由 release 算出的 33 個十六進位字（同一個 release 一定得到同一個 commit）
        var commit = identity!.Build + ReleaseManifestRules.Sha256Hex(Encoding.UTF8.GetBytes(release))[..33];
        var manifest = ReleaseManifestRules.Build(identity, commit, createdAt ?? DefaultCreatedAt, entry, MinRuntime, protocolVersion,
            ReleaseManifestRules.Sha256Hex(zip), files);
        return new Package(release, zip, manifest, Sign(manifest, key));
    }

    /// <summary>.manifest.sig 的內容（{"kid","sig"}）；預設以 <see cref="Keys"/> 那把金鑰簽</summary>
    public string Sign(byte[] manifest, ECDsa? key = null) =>
        ReleaseManifestRules.SerializeSignature(ReleaseManifestRules.Sign(manifest, key ?? signer));

    /// <summary>不在 <see cref="Keys"/> 裡的另一把金鑰（夾具釋放時一併釋放）</summary>
    public ECDsa CreateOtherKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        otherKeys.Add(key);
        return key;
    }

    /// <summary>把預設的套件送進收件匣：先三個檔，最後原子寫 request.json（requestedAt 照寫，服務不看）</summary>
    public void Write(string inboxDir, string id, long requestedAt) => Write(inboxDir, id, requestedAt, Default);

    public static void Write(string inboxDir, string id, long requestedAt, Package package)
    {
        WriteFiles(inboxDir, package);
        WriteRequest(inboxDir, id, package.Release, requestedAt);
    }

    /// <summary>以 <paramref name="asRelease"/>（預設是套件自己的 release）的檔名寫三個檔；資料夾不存在就建，同名的檔覆寫</summary>
    public static void WriteFiles(string dir, Package package, string? asRelease = null)
    {
        Directory.CreateDirectory(dir);
        var names = ReleasePackageRules.FileNames(asRelease ?? package.Release);
        File.WriteAllBytes(Path.Combine(dir, names.Zip), package.Zip);
        File.WriteAllBytes(Path.Combine(dir, names.Manifest), package.Manifest);
        File.WriteAllText(Path.Combine(dir, names.Signature), package.Signature, new UTF8Encoding(false));
    }

    /// <summary>送件腳本寫的 request.json：{"id","release","requestedAt"}</summary>
    public static void WriteRequest(string inboxDir, string id, string release, long requestedAt) =>
        WriteRawRequest(inboxDir, JsonSerializer.Serialize(new { id, release, requestedAt }));

    /// <summary>原樣寫 request.json：先寫 .tmp 再改名（request.json 已存在時丟 IOException：上一個要求還沒處理完）</summary>
    public static void WriteRawRequest(string inboxDir, string json)
    {
        Directory.CreateDirectory(inboxDir);
        var target = Path.Combine(inboxDir, AppReleaseIngestRules.RequestFileName);
        var temp = target + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, target);
    }

    /// <summary>複本中間的一個位元組取反</summary>
    public static byte[] FlipByte(byte[] data)
    {
        var copy = (byte[])data.Clone();
        copy[copy.Length / 2] ^= 0xFF;
        return copy;
    }

    public static ReleaseKey KeyOf(ECDsa key)
    {
        var spki = P256KeyRules.ExportSpki(key);
        return new ReleaseKey(ReleaseManifestRules.ComputeKid(spki), spki);
    }

    public void Dispose()
    {
        signer.Dispose();
        foreach (var key in otherKeys)
            key.Dispose();
        TempDir.TryDelete(WorkDir);
    }

    private static string LocalPath(string root, string releasePath) =>
        Path.Combine([root, .. releasePath.Split('/')]);

    private static byte[] CreateZip(string publish, IReadOnlyList<ReleaseFile> files, (string Path, string Content)? zipOnlyFile)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
                AddEntry(archive, file.Path, File.ReadAllBytes(LocalPath(publish, file.Path)));
            if (zipOnlyFile is { } extra)
                AddEntry(archive, extra.Path, Encoding.UTF8.GetBytes(extra.Content));
        }
        return buffer.ToArray();
    }

    private static void AddEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = DefaultCreatedAt;
        using var output = entry.Open();
        output.Write(content);
    }

    /// <summary>一個套件的三個檔的內容（Signature 是 .manifest.sig 的文字）</summary>
    public sealed record Package(string Release, byte[] Zip, byte[] Manifest, string Signature)
    {
        public string ManifestSha256 => ReleaseManifestRules.Sha256Hex(Manifest);

        /// <summary>簽章檔的 kid；簽章檔格式不對時是空字串</summary>
        public string Kid => ReleaseManifestRules.TryParseSignature(Signature, out var signature) ? signature.Kid : "";
    }
}
