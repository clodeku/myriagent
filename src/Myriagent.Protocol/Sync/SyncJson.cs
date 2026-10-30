using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Myriagent.Sync;

/// <summary>
/// 裝置 hub 線上格式的 Newtonsoft 設定（伺服器的 NewtonsoftJsonHubProtocol 與桌面的 SignalR 用戶端用同一份）：
/// 屬性名 camelCase（字典的鍵照原樣，例如 <c>"Claude"</c>）、null 欄位省略、不把看起來像日期的字串轉成 DateTime（時間一律是 Unix 毫秒）、
/// 不認得的欄位忽略（新版多送的欄位，舊版照樣能讀）。這與桌面存檔用的 <c>ProtocolJson</c>（PascalCase、縮排）不同：
/// 事件 payload 裡的原始項目照存檔格式，外層的訊息照這份。
/// </summary>
internal static class SyncJson
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false, OverrideSpecifiedNames = false },
        },
        NullValueHandling = NullValueHandling.Ignore,
        DateParseHandling = DateParseHandling.None,
        MissingMemberHandling = MissingMemberHandling.Ignore,
        Formatting = Formatting.None,
        MaxDepth = 64,
    };

    public static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);

    public static T? Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);
}

/// <summary>
/// 事件 payload 的編碼（設計 4.3、4.6）：payload 在線上是字串——<c>enc=json</c> 時是 JSON 原文，<c>enc=gzip</c> 時是
/// gzip(UTF-8 原文) 的標準 base64（原文 1 KB 以上、而且壓完比原文短才壓；2026-10-23 以前是 8 KB 以上一律壓）。<c>payloadSha256</c> 一律是 UTF-8 原文的 SHA-256（小寫十六進位），
/// 由裝置寫進 outbox 時算好、重送時不變；伺服器解碼後重算比對，並以它判斷「同一個 deviceSeq 重送」是否冪等。
/// 原文以字串傳遞，伺服器存的、拉取時發出去的都是同一串位元組，不會因為重新序列化而改變雜湊。
/// </summary>
internal static class PayloadRules
{
    public const string EncJson = "json";

    public const string EncGzip = "gzip";

    /// <summary>
    /// 原文 UTF-8 至少這麼大才試著壓縮。對話項目大多是 1～8 KB，以前門檻 8 KB 時都原樣送，回填、積壓補送與其他電腦的拉取都多傳約一半。
    /// 解碼一律接受任何大小的 gzip（伺服器與舊版桌面也是），所以調低門檻不影響相容性
    /// </summary>
    public const int GzipThresholdBytes = 1024;

    public static string Sha256Hex(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    public static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>依大小決定編碼：原文 UTF-8 未滿 <see cref="GzipThresholdBytes"/> 照原樣；否則 gzip＋base64，但壓完沒有比原文短（短的中文字串、已經是亂數的內容）時照原樣</summary>
    public static (string Enc, string Payload) Encode(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length < GzipThresholdBytes)
            return (EncJson, json);
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(bytes);
        // base64 的長度（ASCII，每字元 1 位元組）對原文的 UTF-8 位元組數：沒有比較短就不值得讓讀的一方解壓
        if ((buffer.Length + 2) / 3 * 4 >= bytes.Length)
            return (EncJson, json);
        return (EncGzip, Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length));
    }

    /// <summary>還原原文；解壓後超過 <paramref name="maxJsonBytes"/>、格式錯誤一律失敗（防 gzip 炸彈）</summary>
    public static bool TryDecode(string? enc, string? payload, int maxJsonBytes, out string json)
    {
        json = string.Empty;
        if (payload is null)
            return false;
        if (enc == EncJson)
        {
            if (Encoding.UTF8.GetByteCount(payload) > maxJsonBytes)
                return false;
            json = payload;
            return true;
        }

        if (enc != EncGzip)
            return false;
        try
        {
            using var input = new MemoryStream(Convert.FromBase64String(payload));
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (output.Length + read > maxJsonBytes)
                    return false;
                output.Write(chunk, 0, read);
            }

            json = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(output.GetBuffer(), 0, (int)output.Length);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 估計一筆事件在批次裡佔的位元組（payload 字串＋欄位的固定開銷），切批次用。payload 以 JSON 字串送出，要算進跳脫的膨脹：
    /// Newtonsoft 預設只跳脫引號、反斜線（多 1 位元組）與控制字元、U+0085、U+2028、U+2029（最多 6 位元組，一律多算 5），所以這是上限
    /// </summary>
    public static int WireSize(string payload)
    {
        var bytes = Encoding.UTF8.GetByteCount(payload) + 256;
        foreach (var c in payload)
        {
            if (c == '"' || c == '\\')
                bytes += 1;
            else if (c < ' ' || c == (char)0x85 || c == (char)0x2028 || c == (char)0x2029)
                bytes += 5;
        }

        return bytes;
    }
}
