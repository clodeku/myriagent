using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Update;

/// <summary>
/// 桌面版發佈的純函式規則（設計 6.2、6.3）：manifest 的產生、解析與驗證、套件內的路徑規則、檔案清單比對、簽章。
/// 打包工具、桌面更新器、伺服器收件（U2）共用同一份
/// </summary>
internal static class ReleaseManifestRules
{
    public const string AppName = "Myriagent";
    public const string SignaturePurpose = "myriagent-desktop-release-v1";
    public const int MaxManifestBytes = 4 * 1024 * 1024;
    public const int MaxFiles = 5000;
    public const int MaxPathChars = 240;

    /// <summary>單檔上限 4 GiB：MaxFiles 個檔加總遠小於 long 的上限，呼叫端拿 totalBytes 乘幾倍估磁碟空間也不會溢位</summary>
    public const long MaxFileBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// .manifest.sig 的上限：真正的檔約 120 字元（kid 16 字元、P1363 簽章的 base64url 86 字元，加上 JSON 外框）。
    /// 打包、伺服器收件與裝置的來源讀檔前先以位元組數比對它；<see cref="TryParseSignature"/> 以字元數比對
    /// （UTF-8 的位元組數不會少於字元數，讀檔前比過位元組的不會在這裡被多擋）
    /// </summary>
    public const int MaxSignatureBytes = 1024;

    /// <summary>
    /// Windows 的保留裝置名稱（不分大小寫；後面接副檔名也一樣是裝置）。上標 1、2、3（U+00B9、U+00B2、U+00B3）的 COM、LPT
    /// 以碼位組字，原始碼裡不放看起來像一般數字的字元
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM" + (char)0xB9, "COM" + (char)0xB2, "COM" + (char)0xB3,
        "LPT" + (char)0xB9, "LPT" + (char)0xB2, "LPT" + (char)0xB3,
    };

    /// <summary>反斜線、「:」（磁碟代號與 ADS）與 Windows 檔名不允許的字元</summary>
    private static readonly char[] InvalidPathChars = { '\\', ':', '*', '?', '"', '<', '>', '|' };

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>簽的內容：「myriagent-desktop-release-v1\n{manifest 原文的 SHA-256 小寫十六進位}」的 UTF-8</summary>
    public static byte[] SignedPayload(byte[] manifestBytes) => Encoding.UTF8.GetBytes($"{SignaturePurpose}\n{Sha256Hex(manifestBytes)}");

    /// <summary>公鑰 SPKI（base64url）的 SHA-256 前 16 個小寫十六進位字</summary>
    public static string ComputeKid(string spki)
    {
        if (!Base64UrlRules.TryDecode(spki, out var bytes, P256KeyRules.MaxSpkiBytes))
            throw new ArgumentException("不是合法的 base64url SPKI", nameof(spki));
        return Sha256Hex(bytes)[..16];
    }

    public static ReleaseSignature Sign(byte[] manifestBytes, ECDsa key)
        => new(ComputeKid(P256KeyRules.ExportSpki(key)), P256KeyRules.Sign(key, SignedPayload(manifestBytes)));

    public static string SerializeSignature(ReleaseSignature signature)
        => new JObject { ["kid"] = signature.Kid, ["sig"] = signature.Sig }.ToString(Formatting.None);

    /// <summary>解析 .manifest.sig：超過 <see cref="MaxSignatureBytes"/> 個字元不解析；kid 一律 16 個小寫十六進位字、sig 是標準的 base64url；多餘的欄位忽略</summary>
    public static bool TryParseSignature(string? json, [NotNullWhen(true)] out ReleaseSignature? signature)
    {
        signature = null;
        try
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxSignatureBytes || JToken.Parse(json) is not JObject o)
                return false;
            if (o["kid"] is not JValue { Type: JTokenType.String, Value: string kidText } || o["sig"] is not JValue { Type: JTokenType.String, Value: string sigText })
                return false;
            if (kidText.Length != 16 || !IsLowerHex(kidText) || !Base64UrlRules.IsCanonical(sigText, 128))
                return false;
            signature = new ReleaseSignature(kidText, sigText);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 驗章：kid 在信任清單、沒有被撤銷、以那把公鑰驗得過。通過回 null，否則回原因（給日誌與 app.state.lastError）。
    /// 撤銷清單（updater-state.json 的 RevokedKids）不分大小寫比對：寫入端存成大寫或混大小寫時，撤銷也不會無聲失效
    /// </summary>
    public static string? VerifySignature(byte[] manifestBytes, ReleaseSignature? signature, IEnumerable<ReleaseKey> trusted, IReadOnlyCollection<string> revokedKids)
    {
        if (signature == null)
            return "沒有簽章";
        if (revokedKids.Contains(signature.Kid, StringComparer.OrdinalIgnoreCase))
            return $"簽章金鑰 {signature.Kid} 已被撤銷";
        var keys = trusted.Where(k => k.Kid == signature.Kid).ToList();
        if (keys.Count == 0)
            return $"不認得簽章金鑰 {signature.Kid}";
        var payload = SignedPayload(manifestBytes);
        // kid 只是索引：一定要以清單裡那把公鑰真的驗過（冒牌項目可以宣稱同一個 kid）
        return keys.Any(k => P256KeyRules.Verify(k.Spki, payload, signature.Sig)) ? null : "簽章驗證失敗";
    }

    /// <summary>
    /// 套件內的相對路徑：斜線分隔、不得是絕對路徑或含磁碟代號、不得有「..」「.」與空段、不得有反斜線與「:」（擋 ADS）、
    /// 每段不得以空白或句點結尾（Windows 會默默去掉，兩個不同的路徑會落到同一個檔）、不得是保留裝置名稱
    /// （看第一個句點前的主檔名、去掉尾端空白：舊版 Windows 把「nul .txt」也當成 NUL）
    /// </summary>
    public static bool IsValidPath([NotNullWhen(true)] string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathChars || path[0] == '/')
            return false;
        if (path.IndexOfAny(InvalidPathChars) >= 0 || path.Any(char.IsControl))
            return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(' ') || segment.EndsWith('.'))
                return false;
            var stem = segment.Split('.')[0].TrimEnd(' ');
            if (ReservedNames.Contains(stem))
                return false;
        }
        return true;
    }

    /// <summary>manifest 的檔案清單與實際的（zip 項目或解壓後的檔案）完全相符：一樣多、路徑相同（序數比較）、大小相同。相符回 null，否則回第一個差異</summary>
    public static string? CheckFileSet(IReadOnlyList<ReleaseFile> expected, IReadOnlyList<(string Path, long Size)> actual)
    {
        var want = expected.ToDictionary(f => f.Path, f => f.Size, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, size) in actual)
        {
            if (!want.TryGetValue(path, out var expectedSize))
                return $"多了清單外的檔案：{path}";
            if (!seen.Add(path))
                return $"檔案重複：{path}";
            if (expectedSize != size)
                return $"大小不符：{path}（manifest {expectedSize}，實際 {size}）";
        }
        var missing = want.Keys.FirstOrDefault(p => !seen.Contains(p));
        return missing == null ? null : $"少了檔案：{missing}";
    }

    /// <summary>
    /// 清單裡有沒有「檔案與資料夾同名」（PROTO-2）：「x」是檔案、又有「x/y」（不分大小寫，「X」與「x/y」也算）。
    /// NTFS 上兩者不能並存，桌面解壓時建不了那個資料夾（或檔案），收進來的版本永遠裝不起來。有就回第一個衝突的路徑（照清單順序），否則 null
    /// </summary>
    public static string? FindFileFolderClash(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var files = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                if (files.Contains(path[..slash]))
                    return path;
            }
        }
        return null;
    }

    /// <summary>
    /// 產生 manifest 原文（設計 6.2；發佈工具 myriagent-release 與測試夾具共用）。欄位順序固定：schema、name、release、version、build、commit、
    /// createdAt（UTC，yyyy-MM-ddTHH:mm:ssZ）、entry、minRuntime、protocolVersion、zip、zipSha256、fileCount、totalBytes、files；
    /// files 依路徑的序數排序（zip 項目照同樣的順序寫入）。Newtonsoft 縮排兩格、換行一律 \n、無 BOM 的 UTF-8、結尾沒有換行。
    /// zip 檔名、fileCount、totalBytes 由這裡算。路徑不合法（含不成對的代理字元）、大小超出範圍、不分大小寫重複或檔案與資料夾同名丟 <see cref="ArgumentException"/>；
    /// 產出一定再經 <see cref="TryParse"/> 讀一次，讀不回來（例如 commit 不以 build 開頭、entry 不在清單、release 是開發版）丟 <see cref="InvalidOperationException"/>
    /// </summary>
    public static byte[] Build(ReleaseIdentity identity, string commit, DateTimeOffset createdAt, string entry,
        ReleaseRuntime minRuntime, int protocolVersion, string zipSha256, IReadOnlyList<ReleaseFile> files)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(minRuntime);
        ArgumentNullException.ThrowIfNull(zipSha256);
        ArgumentNullException.ThrowIfNull(files);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file == null || !IsValidPath(file.Path))
                throw new ArgumentException($"不合法的路徑：{file?.Path}", nameof(files));
            if (file.Size is < 0 or > MaxFileBytes)
                throw new ArgumentException($"檔案大小超出範圍：{file.Path}（{file.Size}）", nameof(files));
            if (!names.Add(file.Path))
                throw new ArgumentException($"不分大小寫重複的路徑：{file.Path}", nameof(files));
        }
        if (FindFileFolderClash(files.Select(f => f.Path).ToList()) is { } clash)
            throw new ArgumentException($"檔案與資料夾同名（不分大小寫）：{clash}", nameof(files));
        var sorted = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var root = new JObject
        {
            ["schema"] = 1,
            ["name"] = AppName,
            ["release"] = identity.Release,
            ["version"] = identity.Version.ToString(),
            ["build"] = identity.Build,
            ["commit"] = commit,
            ["createdAt"] = createdAt.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture),
            ["entry"] = entry,
            ["minRuntime"] = new JObject { ["name"] = minRuntime.Name, ["version"] = minRuntime.Version.ToString() },
            ["protocolVersion"] = protocolVersion,
            ["zip"] = $"myriagent-{identity.Release}.zip",
            ["zipSha256"] = zipSha256,
            ["fileCount"] = sorted.Count,
            ["totalBytes"] = sorted.Sum(f => f.Size),
            ["files"] = new JArray(sorted.Select(f => new JObject { ["path"] = f.Path, ["size"] = f.Size, ["sha256"] = f.Sha256 })),
        };
        // JsonTextWriter 縮排時的換行取自 TextWriter.NewLine（Windows 預設 \r\n）：明確設成 \n，產出跟作業系統無關
        var text = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        using (var writer = new JsonTextWriter(text) { Formatting = Formatting.Indented })
            root.WriteTo(writer);
        // 不成對的代理字元（NTFS 允許）預設會被默默換成 U+FFFD，manifest 寫的路徑就跟實際的檔不同：改成丟 EncoderFallbackException（是 ArgumentException）
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(text.ToString());
        if (!TryParse(bytes, out _, out var error))
            throw new InvalidOperationException($"產生的 manifest 讀不回來：{error}");
        return bytes;
    }

    /// <summary>
    /// 解析並驗證 manifest 原文。只收一種解讀：整份只能有一個 JSON 值（後面只能接空白與註解）、
    /// 同名欄位重複一律拒絕（不讓不同的解析器各取一個）、整數超出範圍或單檔超過 <see cref="MaxFileBytes"/> 一律回 false，不丟例外。
    /// Newtonsoft 仍接受註解、單引號字串、不加引號的欄位名稱與結尾逗號，也把結尾的 NUL 字元當成空白，這些無法關掉；打包工具只輸出標準 JSON
    /// </summary>
    public static bool TryParse(byte[] manifestBytes, [NotNullWhen(true)] out ReleaseManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        if (manifestBytes.Length == 0 || manifestBytes.Length > MaxManifestBytes)
            return Fail("manifest 大小不合理", out error);
        JObject root;
        try
        {
            using var reader = new JsonTextReader(new StringReader(new UTF8Encoding(false, true).GetString(manifestBytes))) { DateParseHandling = DateParseHandling.None, MaxDepth = 16 };
            var settings = new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error };
            if (JToken.ReadFrom(reader, settings) is not JObject o)
                return Fail("manifest 不是 JSON 物件", out error);
            // ReadFrom 讀完第一個值就停，不看後面還有沒有東西。SupportMultipleContent 是 false，
            // 後面若有註解以外的內容，Read 會丟 JsonReaderException（Additional text encountered…），由下面的 catch 接住
            while (reader.Read())
            {
                if (reader.TokenType != JsonToken.Comment)
                    return Fail("manifest 結尾有多餘的內容", out error);
            }
            root = o;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            return Fail($"manifest 不是合法的 JSON：{ex.Message}", out error);
        }

        if (Int(root, "schema") != 1)
            return Fail("不支援的 manifest schema", out error);
        if (Str(root, "name") != AppName)
            return Fail("manifest 的 name 不是 Myriagent", out error);
        if (!ReleaseIdentity.TryParseRelease(Str(root, "release"), out var identity))
            return Fail("release 格式不對", out error);
        if (Str(root, "version") != identity!.Version.ToString() || Str(root, "build") != identity.Build)
            return Fail("release 與 version、build 不一致", out error);
        var commit = Str(root, "commit");
        if (commit == null || commit.Length != 40 || !commit.StartsWith(identity.Build, StringComparison.Ordinal) || !IsLowerHex(commit))
            return Fail("commit 不是完整的小寫提交雜湊或與 build 不符", out error);
        var entry = Str(root, "entry");
        if (!IsValidPath(entry))
            return Fail("entry 不合法", out error);
        if (root["minRuntime"] is not JObject runtime || Str(runtime, "name") is not { Length: > 0 } runtimeName
            || !ReleaseVersion.TryParse(Str(runtime, "version"), out var runtimeVersion))
            return Fail("minRuntime 不合法", out error);
        if (Int(root, "protocolVersion") is not { } protocolVersion || protocolVersion < 1)
            return Fail("protocolVersion 不合法", out error);
        var zip = Str(root, "zip");
        if (zip != $"myriagent-{identity.Release}.zip")
            return Fail("zip 檔名與 release 不符", out error);
        var zipSha = Str(root, "zipSha256");
        if (!IsSha256(zipSha))
            return Fail("zipSha256 不合法", out error);
        if (root["files"] is not JArray array || array.Count == 0 || array.Count > MaxFiles)
            return Fail("files 清單不合法", out error);

        var files = new List<ReleaseFile>(array.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in array)
        {
            if (token is not JObject f || Str(f, "path") is not { } path || !IsValidPath(path) || Long(f, "size") is not { } size || size is < 0 or > MaxFileBytes
                || Str(f, "sha256") is not { } sha256 || !IsSha256(sha256))
                return Fail("files 裡有不合法的項目", out error);
            if (!names.Add(path))
                return Fail($"files 裡有不分大小寫重複的路徑：{path}", out error);
            files.Add(new ReleaseFile(path, size, sha256));
        }
        // 伺服器收件、發佈工具與桌面都經過這裡：同名的檔案與資料夾在 NTFS 上解壓不出來（PROTO-2）
        if (FindFileFolderClash(files.Select(f => f.Path).ToList()) is { } clash)
            return Fail($"files 裡的檔案與資料夾同名（不分大小寫）：{clash}", out error);
        if (!files.Any(f => f.Path == entry))
            return Fail("entry 不在 files 清單裡", out error);
        long total = files.Sum(f => f.Size);
        if (Long(root, "totalBytes") != total || Int(root, "fileCount") != files.Count)
            return Fail("totalBytes 或 fileCount 與清單不符", out error);

        manifest = new ReleaseManifest(identity, commit, entry, new ReleaseRuntime(runtimeName, runtimeVersion), protocolVersion, zip!, zipSha, total, files);
        return true;
    }

    private static bool IsSha256([NotNullWhen(true)] string? text) => text is { Length: 64 } && IsLowerHex(text);

    private static bool IsLowerHex(string text) => text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string? Str(JObject o, string name) => o[name] is JValue { Type: JTokenType.String } v ? (string?)v : null;

    // JsonTextReader 把 long 範圍內的整數讀成 long、超出的讀成 BigInteger（Type 一樣是 Integer）：只認 long，超出範圍就當不合法
    private static int? Int(JObject o, string name) => Long(o, name) is long n and >= int.MinValue and <= int.MaxValue ? (int)n : null;

    private static long? Long(JObject o, string name) => o[name] is JValue { Type: JTokenType.Integer, Value: long n } ? n : null;

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
