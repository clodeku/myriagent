using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// conversationCreate 的內容（WP4.4 計畫 D26；閘門 5b 已照型錄查過形狀）。只有空白的 model、effort、title、text 與空字串的 folder 都當成沒給
/// </summary>
/// <param name="AccountId">這台的本機帳戶 id（5c 已確認存在）</param>
/// <param name="PermissionMode">指令給的權限模式（還沒照代理種類正規化）</param>
/// <param name="Folder">工作資料夾（原樣交給資料夾探針）；null＝對話自己的 workspace</param>
/// <param name="Uploads">要附上的上傳檔（WP4.7）；空陣列或沒給是 null</param>
internal sealed record RemoteCreateRequest(string AccountId, AgentPermissionMode PermissionMode, string? Model, string? Effort, string? Folder, string? Title,
    string? Text, JArray? Uploads);

/// <summary>遠端指令處理者的純規則（WP4.4 計畫步 6 起；D18、D19、D26）：不碰檔案系統、不讀中樞的狀態</summary>
internal static class RemoteHandlerRules
{
    /// <summary>遠端給的標題最多幾個字（同本機的改名）</summary>
    public const int MaxCreateTitleLength = 80;

    /// <summary>
    /// conversationCreate 的內容（D26）：帳戶 id 不是非空字串、權限模式不是型錄的名稱（大小寫有別、不收數字）回 null（形狀檢查之後不會發生，處理者回 invalid）。
    /// model、effort、title、text 只有空白算沒給（text 同 RemoteTierRules 的「帶文字」）；folder 只有 null 或空字串算沒給，其他原樣交給探針
    /// </summary>
    public static RemoteCreateRequest? ParseCreate(JObject command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Str(command, "accountId") is not { Length: > 0 } accountId
            || Str(command, "permissionMode") is not { } modeName || !Enum.GetNames<AgentPermissionMode>().Contains(modeName, StringComparer.Ordinal))
            return null;
        return new RemoteCreateRequest(
            accountId,
            Enum.Parse<AgentPermissionMode>(modeName),
            NotBlank(Str(command, "model")),
            NotBlank(Str(command, "effort")),
            Str(command, "folder") is { Length: > 0 } folder ? folder : null,
            NotBlank(Str(command, "title")),
            NotBlank(Str(command, "text")),
            command["uploads"] is JArray { Count: > 0 } uploads ? uploads : null);
    }

    /// <summary>新對話的標題（D26）：有給標題就用它（去掉前後空白、最多 80 字，同本機改名）；沒給取自文字的第一行（AgentHubRules.MakeTitle），再沒有就「新對話」</summary>
    public static string CreateTitle(string? title, string? text)
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length > 0)
            return trimmed.Length <= MaxCreateTitleLength ? trimmed : trimmed[..MaxCreateTitleLength];
        return AgentHubRules.MakeTitle(text);
    }

    private static string? Str(JObject command, string key) => command[key] is JValue { Type: JTokenType.String, Value: string text } ? text : null;

    private static string? NotBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// 純字串：是「磁碟代號:\」（或「磁碟代號:/」）開頭的完整路徑。UNC、<c>\\?\</c>、<c>\\.\</c>、相對路徑、<c>C:a</c> 這種磁碟相對路徑回 false，
    /// 呼叫端看到 false 就不碰檔案系統（光是檢查 UNC 存不存在就會對外送 SMB 雜湊，D18 第 1 步）
    /// </summary>
    public static bool LooksLikeLocalDrivePath(string? path) => AgentAttachmentRules.IsLocalDrivePath(path);

    /// <summary>遠端排進佇列的訊息的 Id（D19、D20）：<c>"r"＋cid</c>，同一個指令重試、被留下再放回都是同一個 Id（ULID 的 cid 時是 27 字）</summary>
    public static string QueueId(string commandId)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        return "r" + commandId;
    }

    /// <summary>
    /// 佇列訊息沒指定 Id 時的遠端 Id（D20）：有來源而且 <c>"r"＋cid</c> 是合格的項目 id 才回它，否則 null（呼叫端用新的 Guid）。
    /// queue.json 讀回的來源可能缺 cid 或是怪字串（手改、別的版本），不能讓中樞建構時丟例外
    /// </summary>
    public static string? QueueIdOf(RemoteOrigin? origin)
        => origin?.CommandId is { Length: > 0 } cid && ConversationIds.IsItemId("r" + cid) ? "r" + cid : null;

    /// <summary>
    /// 遠端回應卡片時這一次會設的 Decision（D21；照 ApproveAsync、ApprovePlanAsync、AnswerAsync 的對應）：處理者以它確認卡片是被這次呼叫處理的，
    /// 不是被作廢或在別處處理的。不是卡片指令回 null
    /// </summary>
    public static string? CardDecision(string cmd, string? choice) => cmd switch
    {
        "approve" => choice switch
        {
            "allowSession" => "allowSession",
            "deny" => "deny",
            _ => "allow",
        },
        "approvePlan" => choice switch
        {
            "acceptEdits" => "acceptEdits",
            "keepPlanning" => "keepPlanning",
            _ => "manual",
        },
        "answer" => "answered",
        _ => null,
    };

    /// <summary>對回原題的答案（<see cref="RemoteCardRules.TryMapAnswers"/> 的結果）→ AnswerAsync 收的 JSON（題目 id → 字串陣列，順序照原本的）</summary>
    public static JObject AnswersJson(IReadOnlyDictionary<string, IReadOnlyList<string>> mapped)
    {
        ArgumentNullException.ThrowIfNull(mapped);
        var json = new JObject();
        foreach (var (questionId, values) in mapped)
            json[questionId] = new JArray(values.Select(v => new JValue(v)));
        return json;
    }

    /// <summary>這個遠端指令送進代理的輸入的來源（D19）：層級與 FullAccess 是閘門 5d 的最終結果，模式是入列當下對話的模式</summary>
    public static RemoteOrigin Origin(RemoteViewContext context, AgentPermissionMode? mode)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new RemoteOrigin(context.CommandId, mode, context.Tier.Tier, context.Tier.FullAccess, context.Collaborator);
    }

    /// <summary>系統的磁碟種類 → 禁止清單規則用的種類（不認得的一律 Unknown：規則只收固定與卸除式，fail-closed）</summary>
    public static RemoteDriveKind DriveKindOf(DriveType type) => type switch
    {
        DriveType.Fixed => RemoteDriveKind.Fixed,
        DriveType.Removable => RemoteDriveKind.Removable,
        DriveType.Network => RemoteDriveKind.Network,
        DriveType.CDRom => RemoteDriveKind.CdRom,
        DriveType.Ram => RemoteDriveKind.Ram,
        DriveType.NoRootDirectory => RemoteDriveKind.NoRootDirectory,
        _ => RemoteDriveKind.Unknown,
    };
}
