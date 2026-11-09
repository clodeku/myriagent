using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Media;

/// <summary>
/// 遠端檔案指令的輸出（<c>files</c>、<c>fileSearch</c>、<c>fileRef</c>，web-protocol 第一節）轉給瀏覽器之前的投影（WP4.7 計畫步 13；
/// 接在 WP4.5 的 <see cref="CommandOutputRules.Project"/> 裡）：只留已知的鍵、型別與長度要對，<c>requestId</c> 要等於指令的、
/// <c>conversationId</c> 要等於指令的線上 id（輸出換成瀏覽器看到的 id）。媒體參照（entry 的 <c>ref</c>、<c>ver</c>，<c>fileRef</c> 的 <c>ref</c>）
/// 原樣留著，交給之後的 <see cref="ICommandOutputRewriter"/> 換成本站網址並拿掉；電腦送來的 <c>url</c>、<c>thumbUrl</c> 在這裡就不留。
/// 任何一處不合格回 null（整則丟掉）。純函式，回傳新的物件
/// </summary>
internal static class CommandOutputFileRules
{
    public const string Files = "files", FileSearch = "fileSearch", FileRef = "fileRef";

    /// <summary>一份清單最多幾筆（web-protocol：最多 3000 筆，同桌面的 <c>FileBrowser.MaxEntries</c>）</summary>
    public const int MaxEntries = 3000;

    /// <summary>@ 提示最多幾筆（web-protocol：最多 30 筆）</summary>
    public const int MaxResults = 30;

    /// <summary>路徑與名稱的上限（Windows 長路徑；dirsOnly 的虛擬根以完整路徑當名稱）</summary>
    public const int MaxPathLength = RemoteFolderRules.MaxPathLength;

    /// <summary>錯誤訊息的上限（同電腦結果的 <c>message</c>）</summary>
    public const int MaxErrorLength = RemoteResultRules.MaxMessageLength;

    /// <summary>媒體參照與版本字串的上限（參照是 26 字、版本 ≤19 位數字；合格與否由改寫器判斷）</summary>
    public const int MaxRefLength = 64;

    /// <summary>FileEntry 與 @ 提示的 <c>kind</c>（web-protocol 的 FileEntry）</summary>
    public static IReadOnlySet<string> Kinds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "dir", "image", "video", "audio", "code", "doc", "archive", "file",
    };

    /// <summary><c>files</c>（回應 listFiles）</summary>
    public static JObject? ProjectFiles(JObject message, OutputContext context)
    {
        if (Header(message, Files, context) is not { } projected
            || !OptionalText(message["path"], MaxPathLength, out var path)
            || !OptionalText(message["parent"], MaxPathLength, out var parent)
            || !OptionalText(message["root"], MaxPathLength, out var root)
            || !OptionalBool(message["truncated"], out var truncated)
            || !OptionalText(message["error"], MaxErrorLength, out var error)
            || message["entries"] is not JArray { Count: <= MaxEntries } entries)
            return null;

        var projectedEntries = new JArray();
        foreach (var token in entries)
        {
            if (token is not JObject entry || ProjectEntry(entry) is not { } projectedEntry)
                return null;
            projectedEntries.Add(projectedEntry);
        }

        projected["path"] = Nullable(path);
        projected["parent"] = Nullable(parent);
        projected["root"] = Nullable(root);
        projected["entries"] = projectedEntries;
        projected["truncated"] = truncated;
        projected["error"] = Nullable(error);
        return projected;
    }

    /// <summary><c>fileSearch</c>（回應 fileSearch）</summary>
    public static JObject? ProjectFileSearch(JObject message, OutputContext context)
    {
        if (Header(message, FileSearch, context) is not { } projected
            || !RemoteCommandShapeRules.IsString(message["query"], out var query) || query.Length > RemoteCommandShapeRules.MaxTextLength
            || message["results"] is not JArray { Count: <= MaxResults } results)
            return null;

        var projectedResults = new JArray();
        foreach (var token in results)
        {
            if (token is not JObject result
                || !Text(result["relativePath"], MaxPathLength, out var relativePath)
                || !Text(result["fullPath"], MaxPathLength, out var fullPath) || fullPath.Length == 0
                || !Kind(result["kind"], out var kind))
                return null;
            projectedResults.Add(new JObject { ["relativePath"] = relativePath, ["fullPath"] = fullPath, ["kind"] = kind });
        }

        projected["query"] = query;
        projected["results"] = projectedResults;
        return projected;
    }

    /// <summary>
    /// <c>fileRef</c>（回應 fileFetch）：<c>error</c> 是字串時只留 type、requestId、conversationId、error（web-protocol：其他欄位省略）；
    /// 否則要有 <c>name</c>、<c>size</c>、<c>expiresAt</c>，<c>ref</c> 是字串才留
    /// </summary>
    public static JObject? ProjectFileRef(JObject message, OutputContext context)
    {
        if (Header(message, FileRef, context) is not { } projected || !OptionalText(message["error"], MaxErrorLength, out var error))
            return null;
        if (error is not null)
        {
            if (error.Length == 0)
                return null;
            projected["error"] = error;
            return projected;
        }

        if (!Text(message["name"], MaxPathLength, out var name) || name.Length == 0
            || message["size"] is not JValue { Type: JTokenType.Integer, Value: long size } || size < 0
            || message["expiresAt"] is not JValue { Type: JTokenType.Integer, Value: long expiresAt })
            return null;
        if (RefText(message["ref"]) is { } mediaRef)
            projected["ref"] = mediaRef;
        projected["name"] = name;
        projected["size"] = size;
        projected["expiresAt"] = expiresAt;
        projected["error"] = JValue.CreateNull();
        return projected;
    }

    /// <summary>
    /// 三種共同的開頭：<c>requestId</c> 等於指令的（指令一定帶，型錄必填）、<c>conversationId</c> 是線上 id 而且等於指令的；
    /// 輸出的 <c>conversationId</c> 是瀏覽器看到的 id。新對話的資料夾挑選（<c>listFolders</c>）那台還沒有對話：
    /// <c>conversationId</c> 要是 null（或不帶），輸出換成草稿 id
    /// </summary>
    private static JObject? Header(JObject message, string type, OutputContext context)
    {
        // 電腦狀態面板的檔案傳輸（deviceBrowse、deviceFetch；2026-11-09）：沒有對話，conversationId 要是 null（或不帶），網頁以 requestId 認領
        if (RemoteCommandCatalog.DeviceTransferCommands.Contains(context.CommandName))
        {
            if (context.RequestId is not { } transferRequest
                || !RemoteCommandShapeRules.IsString(message["requestId"], out var givenTransferRequest) || !string.Equals(givenTransferRequest, transferRequest, StringComparison.Ordinal)
                || message["conversationId"] is { Type: not JTokenType.Null })
                return null;
            return new JObject { ["type"] = type, ["requestId"] = transferRequest, ["conversationId"] = JValue.CreateNull() };
        }

        if (context.CommandName == RemoteCommandCatalog.ListFolders)
        {
            if (context.RequestId is not { } folderRequest
                || !RemoteCommandShapeRules.IsString(message["requestId"], out var givenFolderRequest) || !string.Equals(givenFolderRequest, folderRequest, StringComparison.Ordinal)
                || !BrowserDraftRules.IsDraftId(context.BrowserConversationId)
                || message["conversationId"] is { Type: not JTokenType.Null })
                return null;
            return new JObject { ["type"] = type, ["requestId"] = folderRequest, ["conversationId"] = context.BrowserConversationId };
        }

        var expected = context.BrowserConversationId ?? context.ExpectedConversationId;
        if (context.RequestId is not { } requestId
            || !RemoteCommandShapeRules.IsString(message["requestId"], out var givenRequest) || !string.Equals(givenRequest, requestId, StringComparison.Ordinal)
            || expected is null || !ConversationIds.IsWireId(expected)
            || !RemoteCommandShapeRules.IsString(message["conversationId"], out var conversationId) || !string.Equals(conversationId, expected, StringComparison.Ordinal))
            return null;
        return new JObject { ["type"] = type, ["requestId"] = requestId, ["conversationId"] = expected };
    }

    private static JObject? ProjectEntry(JObject entry)
    {
        if (!Text(entry["name"], MaxPathLength, out var name) || name.Length == 0
            || !Text(entry["path"], MaxPathLength, out var path) || path.Length == 0
            || !Kind(entry["kind"], out var kind)
            || !OptionalCount(entry["size"], out var size)
            || !OptionalTime(entry["modified"], out var modified)
            || !OptionalBool(entry["hidden"], out var hidden))
            return null;

        var projected = new JObject
        {
            ["name"] = name,
            ["path"] = path,
            ["kind"] = kind,
            ["size"] = size is { } n ? n : JValue.CreateNull(),
            ["modified"] = modified is { } m ? m : JValue.CreateNull(),
            ["hidden"] = hidden,
        };
        if (RefText(entry["ref"]) is { } mediaRef)
            projected["ref"] = mediaRef;
        switch (entry["ver"])
        {
            case JValue { Type: JTokenType.Integer, Value: long version }:
                projected["ver"] = version;
                break;
            case JValue { Type: JTokenType.String } when RefText(entry["ver"]) is { } version:
                projected["ver"] = version;
                break;
        }

        return projected;
    }

    /// <summary>媒體參照或版本：≤64 字的字串才留（格式由改寫器驗），其他略過</summary>
    private static string? RefText(JToken? token)
        => RemoteCommandShapeRules.IsString(token, out var text) && text.Length is > 0 and <= MaxRefLength ? text : null;

    private static bool Kind(JToken? token, out string kind)
        => RemoteCommandShapeRules.IsString(token, out kind) && Kinds.Contains(kind);

    private static bool Text(JToken? token, int max, out string text)
        => RemoteCommandShapeRules.IsString(token, out text) && text.Length <= max;

    /// <summary>沒給、JSON null 或 ≤ max 的字串</summary>
    private static bool OptionalText(JToken? token, int max, out string? text)
    {
        text = null;
        if (token is null || token.Type == JTokenType.Null)
            return true;
        if (!Text(token, max, out var value))
            return false;
        text = value;
        return true;
    }

    /// <summary>沒給（false）或布林</summary>
    private static bool OptionalBool(JToken? token, out bool value)
    {
        value = false;
        if (token is null)
            return true;
        if (token is not JValue { Type: JTokenType.Boolean, Value: bool flag })
            return false;
        value = flag;
        return true;
    }

    /// <summary>沒給、JSON null 或非負整數（大小；資料夾是 null）</summary>
    private static bool OptionalCount(JToken? token, out long? value)
    {
        value = null;
        if (token is null || token.Type == JTokenType.Null)
            return true;
        if (token is not JValue { Type: JTokenType.Integer, Value: long n } || n < 0)
            return false;
        value = n;
        return true;
    }

    /// <summary>沒給、JSON null 或整數（Unix 毫秒；讀不到修改時間時桌面給 null）</summary>
    private static bool OptionalTime(JToken? token, out long? value)
    {
        value = null;
        if (token is null || token.Type == JTokenType.Null)
            return true;
        if (token is not JValue { Type: JTokenType.Integer, Value: long n })
            return false;
        value = n;
        return true;
    }

    private static JToken Nullable(string? text) => text is null ? JValue.CreateNull() : new JValue(text);
}
