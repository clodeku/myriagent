namespace MyriagentHub.Media;

/// <summary>媒體、blob 與上傳端點回給用戶端的錯誤代碼（JSON <c>{"error": 代碼}</c>）</summary>
internal static class MediaApiErrors
{
    /// <summary>400 上傳 id 不合格、422 項目 blob 不是嚴格 UTF-8、其他不合格的請求</summary>
    public const string Invalid = BlobInspectRules.Invalid;

    /// <summary>422：項目 blob 疑似含權杖（什麼都不留）</summary>
    public const string Dlp = BlobInspectRules.Dlp;

    /// <summary>400：內容的雜湊或長度和宣告的不符（什麼都不留）</summary>
    public const string Mismatch = "mismatch";

    /// <summary>413：超過端點的上限</summary>
    public const string TooLarge = "too_large";

    /// <summary>411：項目 blob 必須宣告 Content-Length</summary>
    public const string LengthRequired = "length_required";

    /// <summary>507：blob 配額到拒收門檻，或伺服器磁碟的可用空間低於下限</summary>
    public const string InsufficientStorage = "insufficient_storage";

    /// <summary>507：待處理（還沒成為附件）的上傳合計會超過上限</summary>
    public const string PendingFull = "pending_full";

    /// <summary>409：同一個上傳 id 正在上傳，或已上傳過不同的內容</summary>
    public const string Conflict = "conflict";

    /// <summary>404</summary>
    public const string NotFound = "not_found";

    /// <summary>503：伺服器暫時無法處理（寫入者停止等）</summary>
    public const string Unavailable = "unavailable";

    /// <summary>503：來源電腦離線或沒有宣告 media1（媒體轉送）</summary>
    public const string Offline = "offline";

    /// <summary>503：來源電腦同時的轉送已滿（伺服器或電腦自己的上限），帶 <c>Retry-After</c></summary>
    public const string Busy = "busy";

    /// <summary>504：來源電腦 15 秒內沒有回覆</summary>
    public const string Timeout = "timeout";

    /// <summary>502：來源電腦的回覆不合格</summary>
    public const string BadGateway = "bad_gateway";

    /// <summary>410（給裝置）：要這個轉送的瀏覽器已經離開或逾時</summary>
    public const string Gone = "gone";

    /// <summary>409（給裝置）：對話的政策不是 full，不收縮圖（E8）</summary>
    public const string Policy = Myriagent.Media.MediaErrorCodes.Policy;

    /// <summary>415（給裝置）：縮圖不是 PNG／JPEG（只看魔數，E8）</summary>
    public const string UnsupportedType = "unsupported_type";
}

/// <summary>媒體端點的狀態碼對應（純函式）</summary>
internal static class MediaApiRules
{
    /// <summary>
    /// <see cref="BlobStore.PutStreamingAsync"/> 的結果對應到失敗的回應；成功（Stored、Exists）回 null。
    /// 檢查拒絕（Rejected）一律 422，代碼是檢查給的（沒有就是 invalid）
    /// </summary>
    public static (int Status, string Code)? PutFailure(BlobPutOutcome outcome, string? rejection) => outcome switch
    {
        BlobPutOutcome.Stored or BlobPutOutcome.Exists => null,
        BlobPutOutcome.Mismatch => (StatusCodes.Status400BadRequest, MediaApiErrors.Mismatch),
        BlobPutOutcome.TooLarge => (StatusCodes.Status413PayloadTooLarge, MediaApiErrors.TooLarge),
        BlobPutOutcome.QuotaExceeded or BlobPutOutcome.DiskLow => (StatusCodes.Status507InsufficientStorage, MediaApiErrors.InsufficientStorage),
        BlobPutOutcome.Rejected => (StatusCodes.Status422UnprocessableEntity, string.IsNullOrEmpty(rejection) ? MediaApiErrors.Invalid : rejection),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "不認得的結果"),
    };

    /// <summary>上傳前的判斷對應到失敗的回應；New、Existing（要讀本文）回 null</summary>
    public static (int Status, string Code)? BeginFailure(UploadPutKind kind) => kind switch
    {
        UploadPutKind.New or UploadPutKind.Existing => null,
        UploadPutKind.Conflict => (StatusCodes.Status409Conflict, MediaApiErrors.Conflict),
        UploadPutKind.TooLarge => (StatusCodes.Status413PayloadTooLarge, MediaApiErrors.TooLarge),
        UploadPutKind.PendingFull => (StatusCodes.Status507InsufficientStorage, MediaApiErrors.PendingFull),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "不認得的判斷"),
    };

    /// <summary>讀本文時 Kestrel 丟的 <c>BadHttpRequestException</c>：413 是超過上限，其他（本文不完整、太慢）是不合格的請求</summary>
    public static string BadRequestCode(int status) => status == StatusCodes.Status413PayloadTooLarge ? MediaApiErrors.TooLarge : MediaApiErrors.Invalid;
}
