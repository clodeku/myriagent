using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// <c>sync\device.json</c>：裝置身分、裝置權杖（DPAPI）、伺服器身分釘選（設計 3.4、計畫 3.3）。存在＝已配對。
/// 寫入者只有一個（D9）：引擎執行期間是引擎執行緒（改釘、權杖輪換，經 <see cref="DeviceState"/>），配對提交與解除配對時是協調者的背景執行緒。
/// </summary>
/// <param name="ServerUrl">配對時的伺服器（只有 scheme、host、port）</param>
/// <param name="ProtectedDeviceToken">chd_ 權杖的 DPAPI 密文（<see cref="DeviceSecrets"/>）</param>
/// <param name="KeyName">CNG 金鑰名稱（<see cref="DeviceKey.NamePrefix"/>＋亂數）</param>
/// <param name="KeyStorage"><c>tpm</c> 或 <c>software</c></param>
/// <param name="DevicePubKey">裝置金鑰的 SPKI（配對時送出的同一串）</param>
/// <param name="HubIdentitySpki">釘選的伺服器身分公鑰（配對時首次信任，之後只經輪換聲明或本機確認改釘）</param>
internal sealed record DeviceFileData(
    int Version,
    string DeviceId,
    string DeviceName,
    string ServerUrl,
    long PairedAt,
    string ProtectedDeviceToken,
    string KeyName,
    string KeyStorage,
    string DevicePubKey,
    string HubIdentitySpki)
{
    public const int CurrentVersion = 1;

    /// <summary>伺服器身分指紋（16 字，與伺服器主控台 admin status 比對）</summary>
    public string HubFingerprint => P256KeyRules.AnchorFingerprint(HubIdentitySpki);

    /// <summary>裝置指紋（8 字）</summary>
    public string DeviceFingerprint => P256KeyRules.Fingerprint(DevicePubKey);

    /// <summary>ToString 不印權杖密文（D16：權杖不進記錄與例外訊息）</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Version = {Version}, DeviceId = {DeviceId}, DeviceName = {DeviceName}, ServerUrl = {ServerUrl}, ");
        builder.Append(CultureInfo.InvariantCulture, $"PairedAt = {PairedAt}, KeyName = {KeyName}, KeyStorage = {KeyStorage}, HubIdentitySpki = {HubIdentitySpki}");
        return true;
    }
}

internal enum DeviceFileStatus
{
    Ok,

    Missing,

    /// <summary>內容不是合格的 device.json（JSON 錯誤、缺欄位、格式不對）</summary>
    Corrupt,

    /// <summary>讀不到（被占用、沒有權限）：檔案還在，呼叫端不可以覆寫它</summary>
    Unreadable,
}

/// <summary>device.json 的讀寫：保留不認得的欄位（新版 app 加的欄位，舊版重寫時不丟），先寫 .tmp 並落盤再改名取代</summary>
internal static class DeviceFile
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private static readonly JsonSerializerSettings ReadSettings = new()
    {
        DateParseHandling = DateParseHandling.None,
        MaxDepth = 32,
    };

    public static (DeviceFileStatus Status, DeviceFileData? Data) Load(string path)
    {
        if (!File.Exists(path))
            return (DeviceFileStatus.Missing, null);
        string text;
        try
        {
            text = File.ReadAllText(path, Utf8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (DeviceFileStatus.Unreadable, null);
        }

        JObject json;
        try
        {
            json = JsonConvert.DeserializeObject<JToken>(text, ReadSettings) as JObject ?? throw new JsonException("不是 JSON 物件");
        }
        catch (JsonException)
        {
            return (DeviceFileStatus.Corrupt, null);
        }

        var data = FromJson(json);
        return data is null ? (DeviceFileStatus.Corrupt, null) : (DeviceFileStatus.Ok, data);
    }

    /// <summary>讀既有的 JObject、覆寫已知欄位（保留不認得的）、先寫 .tmp 並 Flush(true) 再改名取代；失敗丟 IOException／UnauthorizedAccessException</summary>
    public static void Save(string path, DeviceFileData data)
    {
        var json = ReadExisting(path) ?? new JObject();
        json["version"] = data.Version;
        json["deviceId"] = data.DeviceId;
        json["deviceName"] = data.DeviceName;
        json["serverUrl"] = data.ServerUrl;
        json["pairedAt"] = data.PairedAt;
        json["protectedDeviceToken"] = data.ProtectedDeviceToken;
        json["keyName"] = data.KeyName;
        json["keyStorage"] = data.KeyStorage;
        json["devicePubKey"] = data.DevicePubKey;
        json["hubIdentitySpki"] = data.HubIdentitySpki;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        var bytes = Utf8.GetBytes(json.ToString(Formatting.Indented));
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static void Delete(string path)
    {
        File.Delete(path);
        TryDelete(path + ".tmp");
    }

    private static JObject? ReadExisting(string path)
    {
        try
        {
            return File.Exists(path) ? JsonConvert.DeserializeObject<JToken>(File.ReadAllText(path, Utf8), ReadSettings) as JObject : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DeviceFileData? FromJson(JObject json)
    {
        var version = json["version"] is JValue { Type: JTokenType.Integer, Value: long v } ? v : 0;
        var pairedAt = json["pairedAt"] is JValue { Type: JTokenType.Integer, Value: long p } ? p : -1;
        var data = new DeviceFileData(
            version is >= 1 and <= int.MaxValue ? (int)version : 0,
            Text(json, "deviceId"),
            Text(json, "deviceName"),
            Text(json, "serverUrl"),
            pairedAt,
            Text(json, "protectedDeviceToken"),
            Text(json, "keyName"),
            Text(json, "keyStorage"),
            Text(json, "devicePubKey"),
            Text(json, "hubIdentitySpki"));
        return IsValid(data) ? data : null;
    }

    /// <summary>
    /// 欄位格式：版本 ≥ 1（較新版寫的也讀：欄位只做加法）、deviceId、https 網址、DPAPI 密文、金鑰名稱與存放位置、兩把 P-256 公鑰
    /// </summary>
    internal static bool IsValid(DeviceFileData d) =>
        d.Version >= 1
        && SyncIds.IsDeviceId(d.DeviceId)
        && d.DeviceName.Length > 0
        && Uri.TryCreate(d.ServerUrl, UriKind.Absolute, out var server) && server.Scheme == Uri.UriSchemeHttps
        && d.PairedAt >= 0
        && d.ProtectedDeviceToken.Length > 0
        && d.KeyName.Length > 0
        && PairingRules.IsKeyStorage(d.KeyStorage)
        && P256KeyRules.IsValidSpki(d.DevicePubKey)
        && P256KeyRules.IsValidSpki(d.HubIdentitySpki);

    private static string Text(JObject json, string name) => json[name] is JValue { Type: JTokenType.String } value ? (string?)value ?? string.Empty : string.Empty;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 留著：下次存檔會覆寫
        }
    }
}

/// <summary>
/// 裝置權杖（chd_）的保存：DPAPI CurrentUser＋固定的 entropy（sync-protocol 2.3）。換了 Windows 使用者、或密文被竄改就解不開。
/// cha_（存取權杖）只在記憶體，不經這裡
/// </summary>
internal static class DeviceSecrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Myriagent.Sync.v1");

    public static string Protect(string token) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>解不開（換了 Windows 使用者、被竄改、不是 base64）回 null</summary>
    public static string? Unprotect(string? protectedToken)
    {
        if (string.IsNullOrEmpty(protectedToken))
            return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), Entropy, DataProtectionScope.CurrentUser);
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary><c>sync\pending-key.json</c>：建金鑰之前寫、配對完成或放棄之後刪（當機時留下的孤兒金鑰靠它找到）</summary>
internal sealed record PendingKeyRecord(string KeyName, string KeyStorage, long CreatedAt)
{
    public static void Write(string path, PendingKeyRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var json = new JObject
        {
            ["keyName"] = record.KeyName,
            ["keyStorage"] = record.KeyStorage,
            ["createdAt"] = record.CreatedAt,
        };
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(json.ToString(Formatting.None));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>不存在回 (false, null)；存在但讀不懂回 (true, null)</summary>
    public static (bool Exists, PendingKeyRecord? Record) Read(string path)
    {
        if (!File.Exists(path))
            return (false, null);
        try
        {
            if (JsonConvert.DeserializeObject<JToken>(File.ReadAllText(path, new UTF8Encoding(false))) is not JObject json)
                return (true, null);
            var name = json["keyName"] is JValue { Type: JTokenType.String } n ? (string?)n : null;
            var storage = json["keyStorage"] is JValue { Type: JTokenType.String } s ? (string?)s : null;
            var created = json["createdAt"] is JValue { Type: JTokenType.Integer, Value: long c } ? c : 0;
            return string.IsNullOrEmpty(name) || !PairingRules.IsKeyStorage(storage) ? (true, null) : (true, new PendingKeyRecord(name, storage!, created));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (true, null);
        }
    }
}

/// <summary>配對中途當掉（或失敗、取消）留下的東西：孤兒的 CNG 金鑰、pending-key.json、建到一半的 sync.db</summary>
internal static class PairingRecovery
{
    /// <summary>
    /// 有 pending-key.json 時：device.json 不存在→刪金鑰、pending 檔與 sync.db／-wal／-shm；device.json 存在而且是同一把→只刪 pending 檔；
    /// 不同把→刪 pending 那把與 pending 檔。device.json 讀不到或損毀時什麼都不刪（分不出 pending 那把是不是它的）。沒有 pending 檔就什麼都不做
    /// </summary>
    public static void Cleanup(SyncPaths paths, Action<string>? log)
    {
        var (exists, pending) = PendingKeyRecord.Read(paths.PendingKeyFile);
        if (!exists)
            return;
        var (status, device) = DeviceFile.Load(paths.DeviceFile);
        switch (status)
        {
            case DeviceFileStatus.Missing:
                if (pending is not null)
                    DeleteKey(pending, log);
                DeleteFile(paths.PendingKeyFile, log);
                DeleteFile(paths.DatabaseFile, log);
                DeleteFile(paths.DatabaseFile + "-wal", log);
                DeleteFile(paths.DatabaseFile + "-shm", log);
                break;
            case DeviceFileStatus.Ok:
                if (pending is not null && !string.Equals(pending.KeyName, device!.KeyName, StringComparison.Ordinal))
                    DeleteKey(pending, log);
                DeleteFile(paths.PendingKeyFile, log);
                break;
            case DeviceFileStatus.Corrupt or DeviceFileStatus.Unreadable:
                log?.Invoke($"device.json 讀不到或損毀（{status}），配對留下的金鑰先不清理");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }
    }

    /// <summary>
    /// 一次配對失敗或取消（commit 沒完成）：先照 <see cref="Cleanup"/>，再直接刪這次建的那把與記著它的 pending 檔。
    /// commit 沒完成時 device.json 不可能引用這把，不能只靠 device.json 的狀態判斷——device.json 損毀或讀不到時 Cleanup 什麼都不刪，
    /// 每次失敗的配對都會留下一把孤兒金鑰。唯一的例外是 device.json 讀得到而且記的就是這把（提交其實已經完成），那就不刪
    /// </summary>
    /// <param name="keyStorage">建好的金鑰的存放位置；還沒建好（null）時兩個位置都試</param>
    public static void Abandon(SyncPaths paths, string keyName, string? keyStorage, Action<string>? log)
    {
        Cleanup(paths, log);
        var (status, device) = DeviceFile.Load(paths.DeviceFile);
        if (status == DeviceFileStatus.Ok && string.Equals(device!.KeyName, keyName, StringComparison.Ordinal))
            return;
        DeleteKey(new PendingKeyRecord(keyName, PairingRules.IsKeyStorage(keyStorage) ? keyStorage! : PairingRules.KeyStorageSoftware, 0), log);
        var (exists, pending) = PendingKeyRecord.Read(paths.PendingKeyFile);
        // 只刪記著這把的 pending 檔：記著別把的是之前當掉留下的（device.json 損毀時 Cleanup 留著它），要留給下次清理
        if (exists && pending is not null && string.Equals(pending.KeyName, keyName, StringComparison.Ordinal))
            DeleteFile(paths.PendingKeyFile, log);
    }

    /// <summary>刪掉 pending 記的那把：先試記下的存放位置，不在那裡就試另一個（TPM 失敗退回軟體 KSP 時，記錄可能還是建立前寫的）</summary>
    private static void DeleteKey(PendingKeyRecord pending, Action<string>? log)
    {
        var other = pending.KeyStorage == PairingRules.KeyStorageTpm ? PairingRules.KeyStorageSoftware : PairingRules.KeyStorageTpm;
        if (DeviceKey.Delete(pending.KeyName, pending.KeyStorage) || DeviceKey.Delete(pending.KeyName, other))
            log?.Invoke($"已刪除配對留下的裝置金鑰 {pending.KeyName}");
    }

    private static void DeleteFile(string path, Action<string>? log)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"刪不掉 {Path.GetFileName(path)}：{ex.Message}");
        }
    }
}
