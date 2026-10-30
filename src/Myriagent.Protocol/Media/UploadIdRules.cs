namespace Myriagent.Media;

/// <summary>
/// 瀏覽器上傳的 id（WP4.7 計畫 D21）：英數字、<c>_</c>、<c>-</c>，1～64 字。網頁自己配（UUID 去掉大括號、ULID 都收），
/// 上傳端點 <c>PUT /api/v1/uploads/{uploadId}</c> 與遠端指令的 <c>uploads</c>（<c>RemoteCommandShapeRules</c>）用同一份，
/// 指令驗得過的 id 上傳一定也收
/// </summary>
internal static class UploadIdRules
{
    public const int MaxLength = 64;

    private const string Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";

    public static bool IsValid(string? id)
        => id is { Length: > 0 and <= MaxLength } && id.AsSpan().IndexOfAnyExcept(Chars) < 0;
}
