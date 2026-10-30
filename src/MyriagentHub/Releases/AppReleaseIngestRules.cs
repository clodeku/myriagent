using System.Text.Json;
using Myriagent.Update;
using MyriagentHub.Ops;

namespace MyriagentHub.Releases;

/// <summary>
/// 收件匣的一個要求（admin-inbox\app-release\request.json）：id 與 release 都驗證過。
/// 規格的 requestedAt 照收不看，也沒有過期；重送安全只靠 release 唯一與冪等（U2 設計 D14、D21）
/// </summary>
internal sealed record AppReleaseRequest(string Id, string Release);

/// <summary>
/// 收件的回執（status\app-release.json）：以 StatusJson.Options 寫成 {"id","ok","release","message","processedAt"}，送件腳本比對 id。
/// message 只放 ASCII 代碼（<see cref="AppReleaseIngestCodes"/> 或 ReleasePackageSteps，D19），中文原因只寫服務記錄；
/// id 解析不出來時是空字串，release 是 request 解析得出來時的 release（否則 null）：沒驗過的內容不寫進回執
/// </summary>
internal sealed record AppReleaseReceipt(string Id, bool Ok, string? Release, string Message, long ProcessedAt);

/// <summary>收件回執的代碼（D19：只有 ASCII）。驗證失敗沿用 ReleasePackageSteps：signature、manifest、policy、protocol、zip_hash、zip_entries</summary>
internal static class AppReleaseIngestCodes
{
    /// <summary>收進來了：寫進 app_releases、稽核 app.release.ingested、通知已連線的裝置</summary>
    public const string Ingested = "ingested";

    /// <summary>同一份 manifest 的可用版本已經收過（冪等的重送，D14）：ok，收件匣照樣清掉，不重複通知、不稽核</summary>
    public const string AlreadyIngested = "already_ingested";

    /// <summary>request.json 超過 64 KiB、不是 JSON 物件，或 id、release 不合法</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>收件匣裡沒有 request 寫的 release 的三個檔</summary>
    public const string MissingFile = "missing_file";

    /// <summary>超過大小上限（manifest 4 MiB、簽章檔 1 KiB、zip Hub:Releases:MaxZipMb）：不讀內容</summary>
    public const string TooLarge = "too_large";

    /// <summary>複製完剩下的磁碟空間會少於 Hub:Releases:MinFreeDiskMb</summary>
    public const string DiskFull = "disk_full";

    /// <summary>manifest 的 release 跟 request 的不同</summary>
    public const string ReleaseMismatch = "release_mismatch";

    /// <summary>同名的版本收過（撤回或清除過、或 manifest 不同）：同名的版本永遠不再收（D14）</summary>
    public const string ReleaseExists = "release_exists";

    /// <summary>版本不比所有可用版本新</summary>
    public const string NotNewer = "not_newer";

    /// <summary>複製、查磁碟空間或改名成版本資料夾時的 I/O 錯誤（檔案被鎖住不算：那是下次輪詢再試，不回執）</summary>
    public const string IoError = "io_error";

    /// <summary>未預期的例外（細節在服務記錄）</summary>
    public const string Internal = "internal_error";
}

/// <summary>收件匣的純函式規則（U2 設計 T4）：request.json 的解析、大小與磁碟空間的檢查、被鎖住的檔案</summary>
internal static class AppReleaseIngestRules
{
    /// <summary>送件腳本最後才原子寫入的要求檔：服務只認它，三個檔先到也不動</summary>
    public const string RequestFileName = "request.json";

    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>
    /// 解析 request.json 的 {"id","release","requestedAt"}：屬性名稱不分大小寫（同名的取第一個）；id 要通過
    /// <see cref="AdminInboxRules.IsValidDeployId"/>（它會成為 .partial-&lt;id&gt; 資料夾名稱的一部分，D12），release 要通過
    /// <see cref="ReleaseIdentity.TryParseRelease"/>（只收標準寫法）。其他屬性（含規格的 requestedAt）照收不看。不合格回 null
    /// </summary>
    public static AppReleaseRequest? ParseRequest(string json)
    {
        if (!TryReadStrings(json, out var id, out var release))
            return null;
        return AdminInboxRules.IsValidDeployId(id) && ReleaseIdentity.TryParseRelease(release, out _)
            ? new AppReleaseRequest(id!, release!)
            : null;
    }

    /// <summary>request 不合格時回執用的 id：解析得出合法的 id 就回它，否則 null（回執寫空字串）</summary>
    public static string? ReadId(string json) =>
        TryReadStrings(json, out var id, out _) && AdminInboxRules.IsValidDeployId(id) ? id : null;

    /// <summary>讀檔之前先比大小：超過 <paramref name="maxBytes"/> 回 <see cref="AppReleaseIngestCodes.TooLarge"/>，否則 null</summary>
    public static string? CheckSize(long length, long maxBytes) => length > maxBytes ? AppReleaseIngestCodes.TooLarge : null;

    /// <summary>複製 <paramref name="incomingBytes"/> 之後，磁碟至少還剩 <paramref name="minFreeBytes"/>（三個參數都不是負數）</summary>
    public static bool HasRoom(long availableBytes, long incomingBytes, long minFreeBytes) =>
        availableBytes >= incomingBytes && availableBytes - incomingBytes >= minFreeBytes;

    /// <summary>
    /// 這個 I/O 錯誤（<see cref="Exception.HResult"/>）是檔案被別人開著：共用違規 0x80070020、鎖定違規 0x80070021。
    /// 收件遇到它就下次輪詢再試（送件端還開著、防毒正在掃），不拒收；其他 I/O 錯誤（磁碟滿、目標已存在、路徑不對…）回 io_error
    /// </summary>
    public static bool IsLockedFile(int hresult) => hresult is SharingViolation or LockViolation;

    /// <summary>
    /// 第 10 步把 partial 改名成版本資料夾（或先刪掉留下的同名資料夾）時，這個 I/O 錯誤是「裡面有檔案被別人開著」（INGEST-2）：
    /// 刪除是共用違規（<see cref="IsLockedFile"/>）；改名的是存取被拒 0x80070005——資料夾裡任何一個檔有別的程序開著（防毒在掃剛複製的副本，
    /// 連開檔時允許刪除也一樣）時 Windows 一律回它，.NET 的 Directory.Move 把它包成 IOException（不是 UnauthorizedAccessException）。
    /// 收件遇到它跟第 5 步一樣下次輪詢再試；partial 是服務自己剛建立的，權限問題在第 5 步就會失敗，不會只在改名時才出現
    /// </summary>
    public static bool IsBusyFolder(int hresult) => IsLockedFile(hresult) || hresult == AccessDenied;

    /// <summary>JSON 物件的 id 與 release（字串才算）；不是 JSON 物件回 false</summary>
    private static bool TryReadStrings(string json, out string? id, out string? release)
    {
        id = null;
        release = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            id = StringProperty(doc.RootElement, "id");
            release = StringProperty(doc.RootElement, "release");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? StringProperty(JsonElement obj, string name)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        }
        return null;
    }
}
