using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 對話事件的共用驗證與大小規則（設計 14.11 D33、D34；docs/sync-protocol.md 14.2）：裝置附加進 outbox 之前與伺服器收到時呼叫同一份。
/// 裝置寫的內容一律當成不可信：只收一種解讀（重複的鍵、註解、結尾多餘的內容、深度超過 <see cref="MaxDepth"/> 一律拒絕）、
/// 識別碼與列舉照格式、長度有上限；項目的 payload 另外不准出現本機路徑與只在記憶體的欄位（D32）。
/// 未知的欄位照舊忽略（新版多送的欄位，舊版照樣收）——只有項目裡那幾個會洩漏本機資料的欄位名稱例外。
/// </summary>
internal static class ConversationEventRules
{
    /// <summary>payload 解壓後的上限（同 <see cref="SyncLimits.MaxInlinePayload"/>，sync-protocol 5.4 的解碼上限）</summary>
    public const int MaxPayloadBytes = 128 * 1024;

    /// <summary>內嵌字串的上限（UTF-8）：超過的要換成 blob 參照（<see cref="TryFit"/>）</summary>
    public const int MaxInlineStringBytes = 32 * 1024;

    public const int MaxDepth = 32;

    public const int MaxTitleLength = 200;

    /// <summary>模型、強度、工具種類、決定、卡片摘要這類短欄位</summary>
    public const int MaxShortTextLength = 128;

    /// <summary>工作資料夾路徑、附件與媒體的檔名</summary>
    public const int MaxPathLength = 1024;

    public const int MaxQueueEntries = 50;

    public const int MaxQueueTextLength = 600;

    /// <summary>
    /// <c>conv.attention</c> 的 <c>speech</c> 最多幾個 UTF-16 字：語音通知的句子 2026-10-24 起沒有字數上限（以前 50 字、這裡 120），
    /// 這裡只是事件大小的防護（UTF-8 最多約 12 KB，在 <see cref="MaxInlineStringBytes"/> 之內），桌面超過時截短（VoiceNotifyRules.ForWire）
    /// </summary>
    public const int MaxSpeechLength = 4000;

    /// <summary>meta 的 <c>models</c> 最多幾項</summary>
    public const int MaxModelOptions = 32;

    /// <summary>meta 的 <c>models</c> 每項的說明上限（字）；<c>value</c>、<c>label</c> 是短欄位</summary>
    public const int MaxModelDescriptionLength = 256;

    /// <summary>meta 的 <c>models</c> 每項最多幾個推理強度（每個是短欄位）</summary>
    public const int MaxModelEfforts = 16;

    /// <summary>blob 參照的長度上限（blob 的實際上限在 WP4.7 的端點）</summary>
    public const long MaxBlobLength = 64L * 1024 * 1024;

    /// <summary><c>conv.lease</c> 的 <c>logs</c> 最多幾筆（計畫 5.2）</summary>
    public const int MaxLeaseLogs = 2000;

    /// <summary><c>conv.lease</c> 的 <c>skipped</c> 每個清單最多幾筆</summary>
    public const int MaxLeaseSkipped = 2000;

    /// <summary><c>workspace.dirtyFiles</c> 最多幾個檔名（A 只帶前 20 個，計畫 A4）</summary>
    public const int MaxDirtyFiles = 20;

    /// <summary><c>interruption.tools</c> 最多幾個</summary>
    public const int MaxInterruptedTools = 64;

    /// <summary>佇列一則的附件最多幾個</summary>
    public const int MaxQueueAttachments = 50;

    /// <summary><c>workingDirectoryRef.repoKey</c> 的長度上限</summary>
    public const int MaxRepoKeyLength = 512;

    /// <summary>
    /// <c>conv.lease</c> 裡可以整欄換成 blob 參照的欄位（同 <c>conv.item</c> 的 <see cref="BlobFields"/>，D34）：<c>logs</c> 是 2,000 筆時遠超過
    /// <see cref="MaxPayloadBytes"/>，佇列的原文也可能超過內嵌上限。內容是那個陣列的 JSON，讀的一方以 <see cref="ConversationWire.TryReadLease"/> 換回再驗
    /// </summary>
    public static IReadOnlyList<string> LeaseBlobFields { get; } = ["logs", "queue"];

    /// <summary>可以整欄換成 blob 參照的頂層欄位（字串或陣列；換成 <c>{blob, length}</c> 物件）。順序是大小相同時先換的順序</summary>
    public static IReadOnlyList<string> BlobFields { get; } =
        ["text", "output", "detail", "diff", "questions", "answers", "attachments", "media", "plan", "title", "toolName", "filePath", "allowSessionLabel"];

    /// <summary><see cref="BlobFields"/> 裡值是陣列的（blob 的內容是那個陣列的 JSON）</summary>
    public static IReadOnlySet<string> ArrayFields { get; } = new HashSet<string>(StringComparer.Ordinal) { "diff", "questions", "answers", "attachments", "media", "plan" };

    /// <summary>項目 payload 裡任何一層都不准出現的欄位名稱（不分大小寫）：只在記憶體的欄位與本機路徑（D32）</summary>
    public static IReadOnlySet<string> ForbiddenItemKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "processInstance", "requestKey", "seq", "storedPath", "modelImagePaths", "mediaPaths", "mediaPath", "mediaRefs", "path",
    };

    private const string LocalAccountIdChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.";

    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private static readonly string[] PermissionModes = Enum.GetNames<AgentPermissionMode>();

    private static readonly string[] ItemKinds = Enum.GetNames<AgentItemKind>();

    private static readonly string[] ItemStatuses = Enum.GetNames<AgentItemStatus>();

    private static readonly string[] AttachmentKinds = Enum.GetNames<AgentAttachmentKind>();

    /// <summary>本機帳戶 id（同伺服器 CatalogRules.IsLocalAccountId）：1～64 個英數字、底線、連字號、句點</summary>
    public static bool IsLocalAccountId(string? id)
        => id is { Length: > 0 and <= 64 } && id.AsSpan().IndexOfAnyExcept(LocalAccountIdChars) < 0;

    /// <summary>裝置發的媒體參照（14.3）：26 個 Crockford Base32 字</summary>
    public static bool IsMediaRef(string? value) => value is { Length: 26 } && CrockfordBase32.IsValid(value);

    /// <summary>
    /// 事件外框：種類是對話種類、conversationId 是線上格式、epoch ≥ 1、串流是 <c>c:{conversationId}</c>。
    /// 合格回 null，否則回 <see cref="SyncErrorCodes.Invalid"/>
    /// </summary>
    public static string? CheckEvent(string? kind, string? stream, string? conversationId, long? epoch)
    {
        if (ConversationEventKinds.Placement(kind, conversationId) is not { } placement || !ConversationIds.IsEpoch(epoch))
            return SyncErrorCodes.Invalid;
        return stream == placement.Stream ? null : SyncErrorCodes.Invalid;
    }

    /// <summary>
    /// payload 的檢查：合格回 null；太大（整筆超過 <see cref="MaxPayloadBytes"/>、或有內嵌字串超過 <see cref="MaxInlineStringBytes"/>）回
    /// <see cref="SyncErrorCodes.TooLarge"/>；其他不合格回 <see cref="SyncErrorCodes.Invalid"/>
    /// </summary>
    public static string? Validate(string? kind, string? payloadJson) => Validate(kind, payloadJson, out _);

    /// <inheritdoc cref="Validate(string?, string?)"/>
    /// <param name="detail">不合格的原因（記錄用，不含 payload 的內容）</param>
    /// <param name="maxInlineStringBytes">
    /// 單一字串的上限（UTF-8）：持久事件是 <see cref="MaxInlineStringBytes"/>（超過的換成 blob）；即時更新沒有 blob 可用，
    /// <see cref="LiveRules.CheckRelay"/> 以整則的上限 <see cref="LiveRules.MaxLiveBytes"/> 呼叫
    /// </param>
    /// <param name="maxPayloadBytes">整筆的上限（UTF-8）：持久事件是 <see cref="MaxPayloadBytes"/>；協商出 <c>live2</c> 的即時更新是 <see cref="LiveRules.MaxDeltaItemBytes"/></param>
    public static string? Validate(string? kind, string? payloadJson, out string? detail, int maxInlineStringBytes = MaxInlineStringBytes, int maxPayloadBytes = MaxPayloadBytes)
    {
        detail = null;
        if (!ConversationEventKinds.IsConversationKind(kind))
            return Fail("不是對話事件的種類", out detail);
        if (payloadJson is null)
            return Fail("沒有 payload", out detail);
        if (Encoding.UTF8.GetByteCount(payloadJson) > maxPayloadBytes)
            return TooLarge("payload 超過上限", out detail);
        if (!TryParseStrict(payloadJson, out var token) || token is not JObject root)
            return Fail("payload 不是合法的 JSON 物件", out detail);
        if (LongestString(root) > maxInlineStringBytes)
            return TooLarge("有字串超過內嵌上限（要換成 blob 參照）", out detail);

        var problem = kind switch
        {
            ConversationEventKinds.Created or ConversationEventKinds.Meta => CheckMeta(root),
            ConversationEventKinds.Turn => CheckTurn(root),
            ConversationEventKinds.Attention => CheckAttention(root),
            ConversationEventKinds.Deleted => null,
            ConversationEventKinds.Item => CheckItem(root),
            ConversationEventKinds.ItemRemoved => ConversationIds.IsItemId(Str(root, "id")) ? null : "id 不合格",
            ConversationEventKinds.Queue => CheckQueue(root),
            ConversationEventKinds.Lease => CheckLease(root, allowBlobs: true),
            _ => "不是對話事件的種類",
        };
        return problem == null ? null : Fail(problem, out detail);
    }

    /// <summary>
    /// 把 <c>conv.item</c> 的 payload（<see cref="ConversationWire.TryItem"/> 的結果）放進上限（D34）：
    /// 先把含有超過 <see cref="MaxInlineStringBytes"/> 字串的頂層欄位（字串本身，或含它的陣列）整欄換成 blob 參照；
    /// 整筆仍超過 <paramref name="maxPayloadBytes"/> 時，由大到小把 <see cref="BlobFields"/> 一欄一欄換掉，直到放得下。
    /// 就地修改 <paramref name="payload"/>；換不下（只剩不能換的欄位還是太大）回 false
    /// </summary>
    /// <param name="blobs">要另外上傳的內容（同一個內容只列一次）</param>
    public static bool TryFit(JObject payload, out IReadOnlyList<ConversationBlob> blobs, int maxPayloadBytes = MaxPayloadBytes)
    {
        var list = new List<ConversationBlob>();
        blobs = list;
        foreach (var name in BlobFields)
        {
            if (payload[name] is { Type: not JTokenType.Null } value && !TryReadBlobRef(value, out _) && LongestString(value) > MaxInlineStringBytes)
                ToBlob(payload, name, list);
        }

        while (Encoding.UTF8.GetByteCount(payload.ToString(Formatting.None)) > maxPayloadBytes)
        {
            string? largest = null;
            var largestBytes = -1;
            foreach (var name in BlobFields)
            {
                if (payload[name] is not { Type: not JTokenType.Null } value || TryReadBlobRef(value, out _))
                    continue;
                var bytes = Content(value).Length;
                if (bytes > largestBytes)
                {
                    largest = name;
                    largestBytes = bytes;
                }
            }

            if (largest == null)
                return false;
            ToBlob(payload, largest, list);
        }

        return LongestString(payload) <= MaxInlineStringBytes;
    }

    /// <summary>blob 參照：恰好有 <c>blob</c>（64 個小寫十六進位）與 <c>length</c>（1～<see cref="MaxBlobLength"/>）兩個欄位的物件</summary>
    public static bool TryReadBlobRef(JToken? token, [NotNullWhen(true)] out ConversationBlobRef? blobRef)
    {
        blobRef = null;
        if (token is not JObject { Count: 2 } o || Str(o, "blob") is not { } sha || !PayloadRules.IsSha256Hex(sha)
            || Long(o, "length") is not { } length || length is < 1 or > MaxBlobLength)
            return false;
        blobRef = new ConversationBlobRef(sha, length);
        return true;
    }

    /// <summary>
    /// 只收一種解讀的 JSON 解析：重複的鍵、註解、結尾多餘的內容、深度超過 <see cref="MaxDepth"/> 一律失敗（不同的解析器對重複鍵取前或取後不一致）。
    /// Newtonsoft 仍接受單引號字串、不加引號的欄位名稱與結尾逗號，這些無法關掉；產生端（SyncJson）只輸出標準 JSON
    /// </summary>
    public static bool TryParseStrict(string json, [NotNullWhen(true)] out JToken? token)
    {
        token = null;
        try
        {
            // 先掃一遍找註解：JToken 載入時就算 CommentHandling.Load，屬性名稱與值之間的註解也會被默默丟掉
            using (var scan = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = MaxDepth })
            {
                while (scan.Read())
                {
                    if (scan.TokenType == JsonToken.Comment)
                        return false;
                }
            }

            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = MaxDepth };
            var settings = new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error };
            var value = JToken.ReadFrom(reader, settings);
            if (reader.Read())
                return false;
            token = value;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? CheckMeta(JObject o)
    {
        // 線上名稱（完全相同才算）。Grok、Antigravity 只在協商出 prov1 的連線上發布（DeviceFeatureRules.Prov1）：
        // 舊伺服器的這條規則只收 Claude、Codex，桌面的閘門讓它收不到這兩種
        if (Str(o, "provider") is not ("Claude" or "Codex" or "Grok" or "Antigravity"))
            return "provider 不合格";
        if (!Optional(o, "localAccountId", t => IsLocalAccountId(AsString(t))))
            return "localAccountId 不合格";
        if (Str(o, "title") is not { Length: <= MaxTitleLength })
            return "title 不合格";
        if (!Optional(o, "permissionMode", t => AsString(t) is { } mode && PermissionModes.Contains(mode, StringComparer.Ordinal)))
            return "permissionMode 不合格";
        if (!Optional(o, "model", IsShortText) || !Optional(o, "effort", IsShortText))
            return "model／effort 不合格";
        if (Bool(o, "customFolder") is not { } custom)
            return "customFolder 不合格";
        if (!Optional(o, "workingDirectory", t => custom && AsString(t) is { Length: > 0 and <= MaxPathLength }))
            return "workingDirectory 不合格";
        if (Str(o, "syncPolicy") is not (ConversationSyncPolicies.Full or ConversationSyncPolicies.Metadata or ConversationSyncPolicies.Paused))
            return "syncPolicy 不合格（localOnly 的對話不發布）";
        if (!Optional(o, "createdAt", IsTime) || !Optional(o, "updatedAt", IsTime))
            return "時間不合格";
        if (Bool(o, "running") is null || Bool(o, "waiting") is null)
            return "running／waiting 不合格";
        if (!Optional(o, "models", IsModelList))
            return "models 不合格";
        // 第 5 階段（選用）：逐字稿狀態是列舉名稱（不認得的照收：新版的值，讀的一方當成不能換電腦）；資料夾參照只在自訂資料夾時帶
        if (!Optional(o, "transcript", IsEnumName))
            return "transcript 不合格";
        if (!Optional(o, "workingDirectoryRef", t => custom && IsPathRef(t)))
            return "workingDirectoryRef 不合格";
        return null;
    }

    /// <summary>
    /// meta 的 <c>models</c>：最多 <see cref="MaxModelOptions"/> 項，每項是 <c>{value, label, description?, efforts?}</c>——value、label 是短欄位，
    /// description ≤ <see cref="MaxModelDescriptionLength"/> 字，efforts 最多 <see cref="MaxModelEfforts"/> 個短欄位。每項裡不認得的欄位照舊忽略
    /// </summary>
    private static bool IsModelList(JToken t)
    {
        if (t is not JArray list || list.Count > MaxModelOptions)
            return false;
        foreach (var entry in list)
        {
            if (entry is not JObject m || !IsShortText(m["value"]!) || !IsShortText(m["label"]!))
                return false;
            if (!Optional(m, "description", d => AsString(d) is { Length: <= MaxModelDescriptionLength }))
                return false;
            if (!Optional(m, "efforts", e => e is JArray efforts && efforts.Count <= MaxModelEfforts && efforts.All(IsShortText)))
                return false;
            // 不指定強度時這個模型用的強度（2026-11-12；選用）
            if (!Optional(m, "defaultEffort", IsShortText))
                return false;
        }

        return true;
    }

    private static bool IsEnumName(JToken t) => AsString(t) is { Length: > 0 and <= 32 } value && value.AsSpan().IndexOfAnyExcept(Letters) < 0;

    /// <summary><c>{kind, repoKey?, sub?, display?}</c>：kind 是列舉名稱（不認得的照收），git 一定要有 repoKey</summary>
    private static bool IsPathRef(JToken t)
    {
        if (t is not JObject r || r["kind"] is not { } kind || !IsEnumName(kind))
            return false;
        if (!Optional(r, "repoKey", v => AsString(v) is { Length: > 0 and <= MaxRepoKeyLength }))
            return false;
        if (Str(r, "kind") == PathRefKinds.Git && Str(r, "repoKey") is null)
            return false;
        return Optional(r, "sub", v => AsString(v) is { Length: > 0 and <= MaxPathLength })
               && Optional(r, "display", v => AsString(v) is { Length: > 0 and <= MaxTitleLength });
    }

    /// <summary>
    /// <c>conv.lease</c> payload 的形狀（不含大小上限）：合格回 null，否則回原因。<paramref name="allowBlobs"/>：<see cref="LeaseBlobFields"/> 可以是 blob 參照
    /// （讀的一方換回內容之後以 false 再驗一次，<see cref="ConversationWire.TryReadLease"/>）
    /// </summary>
    internal static string? CheckLeaseShape(JObject payload, bool allowBlobs) => CheckLease(payload, allowBlobs);

    private static string? CheckLease(JObject o, bool allowBlobs)
    {
        var reason = Str(o, "reason");
        if (!LeaseReasons.IsKnown(reason))
            return "reason 不合格";
        if (!SyncIds.IsDeviceId(Str(o, "owner")) || !SyncIds.IsDeviceId(Str(o, "prevOwner")) || Str(o, "owner") == Str(o, "prevOwner"))
            return "owner／prevOwner 不合格";
        var restore = reason == LeaseReasons.Restore;
        if (Long(o, "epoch") is not { } epoch || Long(o, "prevEpoch") is not { } prevEpoch || prevEpoch < 1
            || (restore ? epoch <= prevEpoch : epoch != prevEpoch + 1))
            return "epoch／prevEpoch 不合格（交接與強制接手一律＋1，還原後的認領只要變大）";
        if (restore ? !IsAbsent(o, "handoffId") : !SyncIds.IsUlid(Str(o, "handoffId")))
            return "handoffId 不合格（restore 沒有）";
        var mode = Str(o, "mode");
        if (reason == LeaseReasons.Transfer ? !HandoffModes.IsKnown(mode) : !IsAbsent(o, "mode"))
            return "mode 不合格（只有 transfer 有）";
        if (!Optional(o, "autoContinue", t => t is JValue { Type: JTokenType.Boolean, Value: bool on } && (!on || mode == HandoffModes.Now)))
            return "autoContinue 不合格（只有 now 可以）";
        if (!Optional(o, "initiator", t => !restore && IsInitiator(t)))
            return "initiator 不合格";
        if (!Optional(o, "fromName", IsDeviceName) || !Optional(o, "toName", IsDeviceName))
            return "fromName／toName 不合格";
        if (!Optional(o, "logicalAccountId", t => SyncIds.IsLogicalAccountId(AsString(t))))
            return "logicalAccountId 不合格";
        if (!Optional(o, "logs", t => (allowBlobs && TryReadBlobRef(t, out _)) || IsLogList(t)))
            return "logs 不合格";
        if (!Optional(o, "transcript", IsEnumName))
            return "transcript 不合格";
        if (!Optional(o, "workspace", IsWorkspace))
            return "workspace 不合格";
        if (!Optional(o, "interruption", IsInterruption))
            return "interruption 不合格";
        if (!Optional(o, "queue", t => (allowBlobs && TryReadBlobRef(t, out _)) || IsLeaseQueue(t)))
            return "queue 不合格";
        if (!Optional(o, "skipped", IsSkipped))
            return "skipped 不合格";
        if (reason == LeaseReasons.Takeover ? !IsFence(o["fence"]) : !IsAbsent(o, "fence"))
            return "fence 不合格（只有 takeover 有，而且一定要有）";
        return Optional(o, "cliVersion", IsShortText) ? null : "cliVersion 不合格";
    }

    private static bool IsInitiator(JToken t)
        => t is JObject i && HandoffInitiatorKinds.IsKnown(Str(i, "kind"))
           && Optional(i, "deviceId", v => SyncIds.IsDeviceId(AsString(v)))
           && Optional(i, "label", IsShortText);

    private static bool IsDeviceName(JToken t) => AsString(t) is { Length: > 0 and <= Security.DeviceRegistryRules.MaxNameLength };

    private static bool IsLogList(JToken t)
    {
        if (t is not JArray logs || logs.Count > MaxLeaseLogs)
            return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in logs)
        {
            if (!IsLogHead(entry, out var key) || !keys.Add(key))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 一個 <see cref="LogHead"/>：logId 合格、logKey 等於它的雜湊、kind 等於 logId 決定的種類、gen ≥1、length ≥0、chain 是 32 位元組的 base64、
    /// fullSha256 是 64 個小寫十六進位；relPath 有的話等於 logId 裡的相對路徑、refId 是項目 id；main 只有主檔與 rollout 可以
    /// </summary>
    private static bool IsLogHead(JToken t, out string key)
    {
        key = string.Empty;
        if (t is not JObject h || Str(h, "logId") is not { } logId || !LogIdRules.TryParse(logId, out var parts))
            return false;
        key = LogIdRules.LogKey(logId);
        if (Str(h, "logKey") != key || Str(h, "kind") != parts.Kind)
            return false;
        if (Bool(h, "main") is not { } main || (main && parts.Kind is not (LogKinds.ClaudeMain or LogKinds.CodexRollout)))
            return false;
        if (Long(h, "gen") is not >= 1 || Long(h, "length") is not >= 0)
            return false;
        if (!LogChainRules.TryFromWire(Str(h, "chain"), out _) || !PayloadRules.IsSha256Hex(Str(h, "fullSha256")))
            return false;
        return Optional(h, "relPath", v => AsString(v) is { } rel && rel == parts.RelativePath)
               && Optional(h, "refId", v => ConversationIds.IsItemId(AsString(v)));
    }

    private static bool IsWorkspace(JToken t)
        => t is JObject w
           && Optional(w, "repoKey", v => AsString(v) is { Length: > 0 and <= MaxRepoKeyLength })
           && Optional(w, "sub", v => AsString(v) is { Length: > 0 and <= MaxPathLength })
           && Optional(w, "branch", IsShortText) && Optional(w, "head", IsShortText) && Optional(w, "upstream", IsShortText)
           && Optional(w, "ahead", IsCount) && Optional(w, "behind", IsCount) && Optional(w, "dirty", IsCount) && Optional(w, "untracked", IsCount)
           && Optional(w, "dirtyFiles", v => v is JArray files && files.Count <= MaxDirtyFiles
                                             && files.All(f => AsString(f) is { Length: > 0 and <= MaxPathLength }));

    private static bool IsInterruption(JToken t)
        => t is JObject i && i["tools"] is JArray tools && tools.Count <= MaxInterruptedTools && tools.All(IsShortText)
           && IsCount(i["pendingRequests"]) && Bool(i, "backgroundStopped") is not null;

    private static bool IsLeaseQueue(JToken t)
        => t is JArray entries && entries.Count <= MaxQueueEntries
           && entries.All(e => e is JObject q && ConversationIds.IsItemId(Str(q, "id")) && Str(q, "text") is not null
                               && Optional(q, "attachments", v => v is JArray ids && ids.Count <= MaxQueueAttachments && ids.All(id => ConversationIds.IsItemId(AsString(id))))
                               && Optional(q, "secret", v => v.Type == JTokenType.Boolean));

    private static bool IsSkipped(JToken t)
        => t is JObject k
           && Optional(k, "workspaceFiles", v => v is JArray files && files.Count <= MaxLeaseSkipped && files.All(f => AsString(f) is { Length: > 0 and <= MaxPathLength }))
           && Optional(k, "attachments", v => v is JArray ids && ids.Count <= MaxLeaseSkipped && ids.All(id => ConversationIds.IsItemId(AsString(id))));

    private static bool IsFence(JToken? t) => t is JObject f && Long(f, "atSeq") is >= 1 && Long(f, "lastDeviceSeq") is >= 0;

    private static bool IsCount(JToken? t) => t is JValue { Type: JTokenType.Integer, Value: long n } && n >= 0 && n <= int.MaxValue;

    private static string? CheckTurn(JObject o)
    {
        switch (Str(o, "phase"))
        {
            case ConversationTurnPhases.Started:
                return IsAbsent(o, "outcome") && IsAbsent(o, "usage") ? null : "started 不帶 outcome、usage";
            case ConversationTurnPhases.Completed:
                if (!ConversationTurnOutcomes.IsKnown(Str(o, "outcome")))
                    return "outcome 不合格";
                return Optional(o, "usage", IsUsage) ? null : "usage 不合格";
            default:
                return "phase 不合格";
        }
    }

    private static bool IsUsage(JToken t)
        => t is JObject u && Long(u, "inputTokens") is >= 0 && Long(u, "outputTokens") is >= 0 && Long(u, "cachedInputTokens") is >= 0
           && Optional(u, "costUsd", IsNonNegativeNumber);

    private static string? CheckAttention(JObject o)
    {
        if (!ConversationAttentionKinds.IsKnown(Str(o, "kind")))
            return "kind 不合格";
        if (!Optional(o, "itemId", t => ConversationIds.IsItemId(AsString(t))))
            return "itemId 不合格";
        return Optional(o, "speech", t => t.Type == JTokenType.String && AsString(t) is { Length: <= MaxSpeechLength }) ? null : "speech 不合格";
    }

    /// <summary>
    /// 政策降級（改成 metadata／paused）時待送的 <c>conv.attention</c>：拿掉 <c>speech</c>（完整同步時組的句子可能帶對話內容的簡要說明；
    /// 伺服器改組只有標題的句子）。沒有 speech、或不是 JSON 物件回 null（不用改）
    /// </summary>
    public static string? AttentionWithoutSpeech(string json)
    {
        JObject o;
        try
        {
            o = JObject.Parse(json);
        }
        catch (JsonReaderException)
        {
            return null;
        }
        return o.Remove("speech") ? o.ToString(Formatting.None) : null;
    }

    private static string? CheckQueue(JObject o)
    {
        if (o["entries"] is not JArray entries || entries.Count > MaxQueueEntries)
            return "entries 不合格";
        foreach (var entry in entries)
        {
            if (entry is not JObject e || !ConversationIds.IsItemId(Str(e, "id")) || Str(e, "displayText") is not { Length: <= MaxQueueTextLength })
                return "entries 的一則不合格";
        }

        return Optional(o, "more", t => t is JValue { Type: JTokenType.Integer, Value: long more } && more >= 0) ? null : "more 不合格";
    }

    /// <summary>
    /// <c>conv.item</c> payload 的形狀（不准上線的欄位、識別碼、列舉、各欄位的型別；不含大小上限）：合格回 null，否則回原因。
    /// <see cref="Validate(string?, string?, out string?, int)"/> 在大小檢查之後用它；讀的一方把 blob 換回內容之後再用一次
    /// （<see cref="ConversationWire.TryReadItem"/>：blob 的內容不在 payload 的驗證範圍內）
    /// </summary>
    internal static string? CheckItemShape(JObject payload) => CheckItem(payload);

    private static string? CheckItem(JObject o)
    {
        if (FindForbiddenKey(o) is { } key)
            return $"不准上線的欄位 {key}";
        if (!ConversationIds.IsItemId(Str(o, "id")))
            return "id 不合格";
        if (Long(o, "ord") is not >= 1 || Long(o, "ver") is not >= 1)
            return "ord／ver 不合格";
        if (Str(o, "kind") is not { } kind || !ItemKinds.Contains(kind, StringComparer.Ordinal))
            return "kind 不合格";
        if (Str(o, "status") is not { } status || !ItemStatuses.Contains(status, StringComparer.Ordinal))
            return "status 不合格";
        if (!Optional(o, "createdAt", IsTime) || !Optional(o, "updatedAt", IsTime))
            return "時間不合格";
        foreach (var name in new[] { "toolKind", "decision", "requestDigest" })
        {
            if (!Optional(o, name, IsShortText))
                return $"{name} 不合格";
        }

        foreach (var name in new[] { "noticeKind", "risk" })
        {
            // 列舉名稱：不認得的照收（新版的值），讀的一方當成沒有值
            if (!Optional(o, name, t => AsString(t) is { Length: > 0 and <= 32 } value && value.AsSpan().IndexOfAnyExcept(Letters) < 0))
                return $"{name} 不合格";
        }

        if (!Optional(o, "canAllowForSession", t => t.Type == JTokenType.Boolean) || !Optional(o, "answerAsMessage", t => t.Type == JTokenType.Boolean))
            return "canAllowForSession／answerAsMessage 不合格";

        foreach (var name in BlobFields)
        {
            var value = o[name];
            if (value is null || value.Type == JTokenType.Null || TryReadBlobRef(value, out _))
                continue;
            var ok = name switch
            {
                "diff" => Every(value, IsDiffLine),
                "questions" => Every(value, IsQuestion),
                "answers" => Every(value, t => t is JObject a && Str(a, "id") != null && a["values"] is JArray values && values.All(v => v.Type == JTokenType.String)),
                "attachments" => Every(value, IsAttachment),
                "media" => Every(value, IsMedia),
                "plan" => Every(value, t => t is JObject s && Str(s, "text") != null && Str(s, "status") is { Length: > 0 and <= 32 }),
                "filePath" => AsString(value) is { Length: <= MaxPathLength },
                _ => value.Type == JTokenType.String,
            };
            if (!ok)
                return $"{name} 不合格";
        }

        return null;
    }

    private static bool IsDiffLine(JToken t) => t is JObject d && Str(d, "t") is "add" or "del" or "ctx" or "hunk" && Str(d, "s") != null;

    private static bool IsQuestion(JToken t)
        => t is JObject q && Str(q, "id") != null && Str(q, "text") != null && Optional(q, "header", v => v.Type == JTokenType.String)
           && Bool(q, "multiSelect") != null && Bool(q, "allowOther") != null && Bool(q, "isSecret") != null
           && q["options"] is JArray options
           && options.All(option => option is JObject p && Str(p, "label") != null
                                    && Optional(p, "description", v => v.Type == JTokenType.String) && Optional(p, "preview", v => v.Type == JTokenType.String));

    private static bool IsAttachment(JToken t)
        => t is JObject a && ConversationIds.IsItemId(Str(a, "id")) && Str(a, "name") is { Length: <= MaxPathLength }
           && Str(a, "kind") is { } kind && AttachmentKinds.Contains(kind, StringComparer.Ordinal)
           && Long(a, "size") is >= 0
           && Optional(a, "frames", v => v is JValue { Type: JTokenType.Integer, Value: long frames } && frames >= 0)
           && Optional(a, "duration", IsNonNegativeNumber)
           && Optional(a, "ref", v => IsMediaRef(AsString(v)));

    private static bool IsMedia(JToken t)
        => t is JObject m && Str(m, "kind") is "image" or "video" && Str(m, "name") is { Length: <= MaxPathLength }
           && Optional(m, "ref", v => IsMediaRef(AsString(v)));

    private static bool Every(JToken value, Func<JToken, bool> check) => value is JArray array && array.All(check);

    private static string? FindForbiddenKey(JToken token)
    {
        foreach (var property in token.DescendantsAndSelfTokens().OfType<JProperty>())
        {
            if (ForbiddenItemKeys.Contains(property.Name))
                return property.Name;
        }

        return null;
    }

    private static IEnumerable<JToken> DescendantsAndSelfTokens(this JToken token)
        => token is JContainer container ? container.DescendantsAndSelf() : [token];

    /// <summary>最長的字串（UTF-8 位元組；值與欄位名稱都算）</summary>
    private static int LongestString(JToken token)
    {
        var longest = 0;
        foreach (var t in token.DescendantsAndSelfTokens())
        {
            var bytes = t switch
            {
                JValue { Type: JTokenType.String, Value: string s } => Encoding.UTF8.GetByteCount(s),
                JProperty p => Encoding.UTF8.GetByteCount(p.Name),
                _ => 0,
            };
            longest = Math.Max(longest, bytes);
        }

        return longest;
    }

    /// <summary>一欄的 blob 內容：字串是本身的 UTF-8，陣列是它的 JSON</summary>
    private static byte[] Content(JToken value)
        => Encoding.UTF8.GetBytes(value is JValue { Type: JTokenType.String, Value: string s } ? s : value.ToString(Formatting.None));

    private static void ToBlob(JObject payload, string name, List<ConversationBlob> blobs)
    {
        var content = Content(payload[name]!);
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        payload[name] = new JObject { ["blob"] = sha, ["length"] = content.LongLength };
        if (!blobs.Any(b => b.Sha256 == sha))
            blobs.Add(new ConversationBlob(sha, content));
    }

    private static bool Optional(JObject o, string name, Func<JToken, bool> check)
        => o[name] is not { } value || value.Type == JTokenType.Null || check(value);

    private static bool IsAbsent(JObject o, string name) => o[name] is null || o[name]!.Type == JTokenType.Null;

    private static bool IsShortText(JToken t) => AsString(t) is { Length: > 0 and <= MaxShortTextLength };

    private static bool IsTime(JToken t) => t is JValue { Type: JTokenType.Integer, Value: long ms } && ms >= 0;

    private static bool IsNonNegativeNumber(JToken t) => t switch
    {
        JValue { Type: JTokenType.Integer, Value: long l } => l >= 0,
        JValue { Type: JTokenType.Float, Value: double d } => double.IsFinite(d) && d >= 0,
        _ => false,
    };

    private static string? AsString(JToken? t) => t is JValue { Type: JTokenType.String, Value: string s } ? s : null;

    private static string? Str(JObject o, string name) => AsString(o[name]);

    private static long? Long(JObject o, string name) => o[name] is JValue { Type: JTokenType.Integer, Value: long l } ? l : null;

    private static bool? Bool(JObject o, string name) => o[name] is JValue { Type: JTokenType.Boolean, Value: bool b } ? b : null;

    private static string Fail(string reason, out string? detail)
    {
        detail = reason;
        return SyncErrorCodes.Invalid;
    }

    private static string TooLarge(string reason, out string? detail)
    {
        detail = reason;
        return SyncErrorCodes.TooLarge;
    }
}
