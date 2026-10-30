using Myriagent.Media;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Media;

/// <summary>
/// 遠端指令的 <c>uploads</c>（<c>addUploaded</c>、<c>conversationCreate</c>）→ 上傳檢查的宣告（WP4.7 計畫 D21、6.3 的 WP4.5 契約點 3）：
/// 指令路由在簽章之前以 <see cref="IUploadRegistry.TryVerify"/> 確認每一筆都已上傳到這台伺服器。純函式
/// </summary>
internal static class CommandUploadRules
{
    public const string UploadsField = "uploads";

    /// <summary>
    /// 沒有 <c>uploads</c>（或是 JSON null）回空清單；有而任何一筆形狀不對（不是物件、<c>uploadId</c> 不合格、<c>size</c> 不是非負整數、
    /// <c>sha256</c> 不是 64 個小寫十六進位）或是空陣列回 null，呼叫端當成不通過（形狀檢查 <c>RemoteCommandShapeRules.CheckUploads</c> 之後不會發生）
    /// </summary>
    public static IReadOnlyList<UploadClaim>? Claims(JObject command)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command[UploadsField])
        {
            case null or { Type: JTokenType.Null }:
                return [];
            case JArray { Count: > 0 } array:
                var claims = new List<UploadClaim>(array.Count);
                foreach (var token in array)
                {
                    if (token is not JObject upload
                        || upload["uploadId"] is not JValue { Type: JTokenType.String, Value: string uploadId } || !UploadIdRules.IsValid(uploadId)
                        || upload["size"] is not JValue { Type: JTokenType.Integer, Value: long size } || size < 0
                        || upload["sha256"] is not JValue { Type: JTokenType.String, Value: string sha256 } || !BlobIdRules.IsBlobId(sha256))
                        return null;
                    claims.Add(new UploadClaim(uploadId, size, sha256));
                }

                return claims;
            default:
                return null;
        }
    }
}
