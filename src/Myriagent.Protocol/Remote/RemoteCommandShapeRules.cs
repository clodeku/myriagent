using System.Text;
using Myriagent.Agents;
using Myriagent.Media;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Remote;

/// <summary>型錄裡一個欄位的型別（<see cref="RemoteField"/>）</summary>
internal enum RemoteFieldKind
{
    /// <summary>線上的對話 id：32 個小寫十六進位（D38）</summary>
    ConversationId,

    /// <summary>項目、佇列訊息、附件的 id（<see cref="ConversationIds.IsItemId"/>）</summary>
    ItemId,

    /// <summary>字串（≤ <see cref="RemoteField.MaxLength"/> 個 UTF-16 字元）</summary>
    Text,

    /// <summary>字串，只收 <see cref="RemoteField.Values"/> 列的名稱（大小寫有別；數值一律拒絕，D6）</summary>
    Choice,

    Bool,

    /// <summary>卡片摘要 rd1：<c>rd1:</c>＋64 個小寫十六進位（RequestDigestRules）</summary>
    Digest,

    /// <summary>本機帳戶 id 或邏輯帳戶 id（<see cref="ConversationEventRules.IsLocalAccountId"/>）</summary>
    AccountId,

    /// <summary>邏輯帳戶 id（<c>la_</c>＋26 字）</summary>
    LogicalAccountId,

    /// <summary>裝置 id（<c>d_</c>＋26 字）</summary>
    DeviceId,

    /// <summary>網頁配的請求 id（1～64 個英數字與 <c>._:-</c>）：回應的 files／fileSearch／fileRef 帶回</summary>
    RequestId,

    /// <summary><c>answer</c> 的 <c>{題目 id: [字串…]}</c>：≤64 題、每題 ≤64 個值、鍵與值各 ≤4096 字</summary>
    Answers,

    /// <summary><c>addUploaded</c> 的 <c>[{uploadId, name, size, sha256}]</c>：1～20 個，物件裡不能有其他鍵</summary>
    Uploads,

    /// <summary><c>setOption</c> 的 value：key 是 permission 時只收 <c>default</c>、空字串與權限模式的名稱，其他 ≤128 字</summary>
    OptionValue,

    /// <summary>秒數：1～<see cref="RemoteExecRules.MaxTimeoutSeconds"/> 的 JSON 整數（<c>remoteExec</c> 的 <c>timeoutSec</c>）</summary>
    Seconds,
}

/// <param name="Name">鍵（大小寫有別）</param>
/// <param name="Required">一定要有（有帶 null 也算有，<paramref name="Nullable"/> 才收 null）</param>
/// <param name="Nullable">可以是 JSON null</param>
/// <param name="MaxLength">字串的長度上限（UTF-16 字元）</param>
/// <param name="Values">Choice 的名稱</param>
internal sealed record RemoteField(string Name, RemoteFieldKind Kind, bool Required = true, bool Nullable = false, int MaxLength = RemoteCommandShapeRules.MaxTextLength, IReadOnlyList<string>? Values = null);

/// <summary>
/// 遠端指令 <c>cmd</c> 物件的嚴格形狀（設計 14.11 D6；中樞閘門 5b，伺服器簽信封之前也查一次）：只收型錄列的鍵（多一個就拒絕，
/// 和網頁指令「不認得的欄位忽略」不同）、型別與長度照型錄、列舉只收名稱（<c>UnknownEnumNameAsNullConverter</c> 與
/// <c>Enum.TryParse</c> 都會讓數字通過，這裡一律拒絕）。純函式；不合格一律回 <see cref="RemoteResultCodes.Invalid"/>
/// </summary>
internal static class RemoteCommandShapeRules
{
    /// <summary>一般字串的上限（第 3 階段瀏覽器 MessageGuard 的單一字串上限）</summary>
    public const int MaxTextLength = 4096;

    /// <summary>送進代理的文字（send.text、附言、新對話的第一則）上限（web-protocol 第六節第 11 點）</summary>
    public const int MaxAgentTextLength = 16_000;

    /// <summary>單一指令的本文上限（UTF-8，不縮排的 JSON；web-protocol 第六節第 11 點的 MaxBodyBytes）</summary>
    public const int MaxBodyBytes = 96 * 1024;

    /// <summary>cmd 物件的巢狀深度上限（answers、uploads 是 3 層）</summary>
    public const int MaxDepth = 8;

    public const int MaxAnswerQuestions = 64;

    public const int MaxAnswerValues = 64;

    public const int MaxUploadNameLength = 260;

    private const string RequestIdChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._:-";

    private const string LowerHex = "0123456789abcdef";

    /// <summary>
    /// 檢查一個遠端指令；null＝合格，否則是給遠端操作紀錄看的原因（不含指令的內容）。
    /// 只收型錄裡由電腦執行的指令（路由 Owner／Device）：伺服器自己處理的、桌面專屬的、沒列名的一律不合格
    /// </summary>
    public static string? Check(JObject? command)
    {
        if (command == null)
            return "不是物件";
        if (command["cmd"] is not JValue { Type: JTokenType.String, Value: string name })
            return "cmd 不是字串";
        if (RemoteCommandCatalog.Find(name) is not { } spec)
            return "不是遠端指令";
        if (Encoding.UTF8.GetByteCount(command.ToString(Formatting.None)) > MaxBodyBytes)
            return "超過大小上限";
        if (Depth(command) > MaxDepth)
            return "巢狀太深";

        var fields = spec.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        foreach (var property in command.Properties())
        {
            if (property.Name == "cmd")
                continue;
            if (!fields.TryGetValue(property.Name, out var field))
                return $"不認得的鍵 {Short(property.Name)}";
            if (property.Value.Type == JTokenType.Null)
            {
                if (!field.Nullable)
                    return $"{field.Name} 不能是 null";
                continue;
            }

            if (CheckValue(field, property.Value, command) is { } error)
                return $"{field.Name}：{error}";
        }

        foreach (var field in spec.Fields)
        {
            if (field.Required && command.Property(field.Name, StringComparison.Ordinal) == null)
                return $"少了 {field.Name}";
        }

        return null;
    }

    /// <summary>JSON 字串（不是數字、布林或物件）</summary>
    public static bool IsString(JToken? token, out string value)
    {
        if (token is JValue { Type: JTokenType.String, Value: string text })
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>rd1 摘要的格式</summary>
    public static bool IsDigest(string? digest)
        => digest is { Length: 68 } && digest.StartsWith(RequestDigestRules.Prefix, StringComparison.Ordinal)
           && digest.AsSpan(4).IndexOfAnyExcept(LowerHex) < 0;

    private static string? CheckValue(RemoteField field, JToken value, JObject command)
    {
        switch (field.Kind)
        {
            case RemoteFieldKind.ConversationId:
                return IsString(value, out var conversationId) && ConversationIds.IsWireId(conversationId) ? null : "不是線上的對話 id";
            case RemoteFieldKind.ItemId:
                return IsString(value, out var itemId) && ConversationIds.IsItemId(itemId) ? null : "不是合格的 id";
            case RemoteFieldKind.Text:
                return IsString(value, out var text) && text.Length <= field.MaxLength ? null : "不是字串或太長";
            case RemoteFieldKind.Choice:
                return IsString(value, out var choice) && field.Values!.Contains(choice, StringComparer.Ordinal) ? null : "不是型錄列的名稱";
            case RemoteFieldKind.Bool:
                return value.Type == JTokenType.Boolean ? null : "不是布林";
            case RemoteFieldKind.Digest:
                return IsString(value, out var digest) && IsDigest(digest) ? null : "不是 rd1 摘要";
            case RemoteFieldKind.AccountId:
                return IsString(value, out var accountId) && ConversationEventRules.IsLocalAccountId(accountId) ? null : "不是帳戶 id";
            case RemoteFieldKind.LogicalAccountId:
                return IsString(value, out var logicalId) && SyncIds.IsLogicalAccountId(logicalId) ? null : "不是邏輯帳戶 id";
            case RemoteFieldKind.DeviceId:
                return IsString(value, out var deviceId) && SyncIds.IsDeviceId(deviceId) ? null : "不是裝置 id";
            case RemoteFieldKind.RequestId:
                return IsString(value, out var requestId) && requestId is { Length: > 0 and <= 64 } && requestId.AsSpan().IndexOfAnyExcept(RequestIdChars) < 0
                    ? null
                    : "不是請求 id";
            case RemoteFieldKind.Answers:
                return CheckAnswers(value);
            case RemoteFieldKind.Uploads:
                return CheckUploads(value);
            case RemoteFieldKind.Seconds:
                // 超出 long 的整數 Newtonsoft 存成 BigInteger，所以只認 long（同 uploads 的 size）
                return value is JValue { Type: JTokenType.Integer, Value: long seconds } && seconds is >= 1 and <= RemoteExecRules.MaxTimeoutSeconds
                    ? null
                    : "不是 1～86400 的整數";
            case RemoteFieldKind.OptionValue:
                if (!IsString(value, out var option) || option.Length > 128)
                    return "不是字串或太長";
                if (IsString(command["key"], out var key) && key == "permission"
                    && option is not ("" or "default") && !Enum.GetNames<AgentPermissionMode>().Contains(option, StringComparer.Ordinal))
                    return "不是權限模式的名稱";
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field.Kind, "未知的欄位型別");
        }
    }

    private static string? CheckAnswers(JToken value)
    {
        if (value is not JObject answers || answers.Count > MaxAnswerQuestions)
            return "不是物件或題數太多";
        foreach (var property in answers.Properties())
        {
            if (property.Name.Length is 0 or > MaxTextLength)
                return "題目 id 不合格";
            if (property.Value is not JArray values || values.Count > MaxAnswerValues)
                return "答案不是字串陣列";
            if (values.Any(v => !IsString(v, out var answer) || answer.Length > MaxTextLength))
                return "答案不是字串或太長";
        }

        return null;
    }

    private static string? CheckUploads(JToken value)
    {
        if (value is not JArray uploads || uploads.Count is 0 or > AgentAttachmentRules.MaxAttachmentsPerMessage)
            return "不是 1～20 個的陣列";
        foreach (var upload in uploads)
        {
            if (upload is not JObject o || o.Properties().Any(p => p.Name is not ("uploadId" or "name" or "size" or "sha256")))
                return "不是 {uploadId, name, size, sha256}";
            // 和上傳端點同一份規則（WP4.7 計畫 D21）：指令驗得過的 id，上傳一定也收
            if (!IsString(o["uploadId"], out var id) || !UploadIdRules.IsValid(id))
                return "uploadId 不合格";
            if (!IsString(o["name"], out var name) || name.Length is 0 or > MaxUploadNameLength)
                return "name 不合格";
            // 超出 long 的整數 Newtonsoft 存成 BigInteger（Value<long>() 會丟例外），所以只認 long
            if (o["size"] is not JValue { Type: JTokenType.Integer, Value: long size } || size is < 0 or > AgentAttachmentRules.MaxAttachmentBytes)
                return "size 不合格";
            if (!IsString(o["sha256"], out var sha) || sha.Length != 64 || sha.AsSpan().IndexOfAnyExcept(LowerHex) >= 0)
                return "sha256 不合格";
        }

        return null;
    }

    /// <summary>物件與陣列的巢狀層數（純量是 0）</summary>
    private static int Depth(JToken token) => token switch
    {
        JObject o => 1 + (o.Count == 0 ? 0 : o.Properties().Max(p => Depth(p.Value))),
        JArray a => 1 + (a.Count == 0 ? 0 : a.Max(Depth)),
        _ => 0,
    };

    /// <summary>紀錄裡的鍵名截短（不讓任意長的鍵名撐大紀錄）</summary>
    private static string Short(string name) => name.Length <= 32 ? name : name[..32] + "…";
}
