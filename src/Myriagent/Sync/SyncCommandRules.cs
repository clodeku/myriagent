using System.Security.Cryptography;
using System.Text;
using Myriagent.Remote;
using Myriagent.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary><see cref="SyncCommandRules.Decide"/> 的結果種類（計畫 D8）</summary>
internal enum CommandSeenKind
{
    /// <summary>沒見過：執行</summary>
    New,

    /// <summary>同一個內容已經有結果：回上一次的結果、不重做</summary>
    Duplicate,

    /// <summary>同一個指令 id 但內容不同：回 rejected／conflict、不執行</summary>
    Conflict,

    /// <summary>同一個內容正在執行（在引擎的進行中表）：等原本那次的結果</summary>
    InFlight,

    /// <summary>同一個內容還記著 executing、不在進行中也沒有快取的結果（當機或完成時寫入失敗留下的）：回 unknown、不重做</summary>
    Unknown,
}

/// <summary><c>commands_seen</c> 的一列（<see cref="SyncStore"/> 的 BeginCommand 讀出、交給 <see cref="SyncCommandRules.Decide"/>）</summary>
internal sealed record CommandSeenRow(string ContentSha256, string State, string? ResultJson, long At);

/// <summary>去重的判斷：<see cref="Previous"/> 只在 <see cref="CommandSeenKind.Duplicate"/> 時有值</summary>
internal sealed record CommandSeenDecision(CommandSeenKind Kind, CommandResult? Previous);

/// <summary>
/// 電腦端遠端指令（WP4.4）的純規則：配對網址 → aud、Hello 記下的伺服器時鐘的可用期限、<c>commands_seen</c> 的去重判斷、
/// 結果的序列化、驗信封失敗的結果、遠端操作紀錄的遮蔽與欄位、修剪的時機與匯出的位置。全部是純函式：時間與時鐘的讀數由呼叫端傳入，
/// 不碰 SQLite 與檔案系統（中樞與協調者看得到，未配對時不能載入 SQLite，S6）
/// </summary>
internal static class SyncCommandRules
{
    /// <summary>Hello 記下的時鐘最多用多久（毫秒）：單調時鐘在睡眠時可能停止，太舊的推算不可信（ENV-4，計畫 D4）</summary>
    public const long MaxClockAgeMs = 12L * 60 * 60 * 1000;

    /// <summary>協商出 cmd1 的連線，時鐘滿這麼久而且閒置就重新 Hello（毫秒）</summary>
    public const long ClockRefreshMs = 6L * 60 * 60 * 1000;

    /// <summary>時間判不過時安排重新 Hello 的最短間隔（毫秒）</summary>
    public const long ReHelloMinIntervalMs = 60_000;

    /// <summary><c>commands_seen</c> 保留多久（毫秒）：durable 的 7 天再加 1 天</summary>
    public const long SeenRetentionMs = CommandEnvelope.MaxDurableAgeMs + 24L * 60 * 60 * 1000;

    /// <summary>遠端操作紀錄保留多久（毫秒，180 天）</summary>
    public const long AuditRetentionMs = 180L * 24 * 60 * 60 * 1000;

    /// <summary>兩次修剪的最短間隔（毫秒）：Hello 時距上次修剪滿 24 小時才再做</summary>
    public const long PruneIntervalMs = 24L * 60 * 60 * 1000;

    /// <summary>斷線時保留、等下一條協商出 cmd1 的連線補送的結果最多幾則</summary>
    public const int MaxPendingResults = 64;

    /// <summary>已完成結果的快取（完成時寫入失敗也回得出上一次的結果）最多幾筆</summary>
    public const int MaxCompletedCache = 256;

    /// <summary>紀錄裡的簽章最多幾個字</summary>
    public const int MaxSigLength = 200;

    /// <summary>紀錄裡遮蔽後的 body 最多幾個字</summary>
    public const int MaxAuditBodyChars = 16_384;

    /// <summary>紀錄的 step：同一個指令 id 但內容不同（去重衝突）</summary>
    public const string SeenStep = "seen";

    /// <summary><c>commands_seen.state</c> 與紀錄的 <c>result</c>：已記下、正在執行（之後改成結果的 state）</summary>
    public const string ExecutingState = "executing";

    /// <summary>遮蔽後只留長度的指令欄位（代理輸入的文字、核准與計畫的附言、問卷的回答：設計 8.6「代理輸入、回答不記原文」）</summary>
    private static readonly HashSet<string> RedactedFields = new(StringComparer.Ordinal) { "text", "message", "answers" };

    /// <summary>超過上限時依序把每個字串截到這個長度再試</summary>
    private static readonly int[] TruncateSteps = [1024, 128, 16];

    /// <summary>
    /// 配對網址 → <c>aud</c>（計畫 D5）：規則與伺服器的 <c>HostingRules.NormalizeOrigin</c> 逐項相同——scheme 與 IdnHost 小寫、
    /// IPv6 加中括號、省略預設埠、只收 https 與 http、不收路徑、查詢、片段、帳密。不合格回 null（這時不宣告 cmd1）
    /// </summary>
    public static string? Audience(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || !Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath is not ("/" or "") || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (uri.HostNameType == UriHostNameType.IPv6)
            host = "[" + host.Trim('[', ']') + "]";
        return uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
    }

    /// <summary>
    /// 驗信封用的伺服器時間界線（計畫 D4）：時鐘是 null、年齡超過 <see cref="MaxClockAgeMs"/>（剛好等於算可用）、
    /// 或現在早於收到 Hello 回應的時戳（單調時鐘倒退）回 null；否則照 <see cref="ServerClockRules.Bounds"/>
    /// </summary>
    public static ServerTimeBounds? UsableBounds(ServerClock? clock, long nowTimestamp)
    {
        if (ClockAgeMs(clock, nowTimestamp) is not { } age || age > MaxClockAgeMs)
            return null;
        return ServerClockRules.Bounds(clock, nowTimestamp);
    }

    /// <summary>時鐘的年齡（收到 Hello 回應到現在，毫秒、無條件進位）；時鐘是 null、頻率不是正數、現在早於收到的時戳時回 null</summary>
    public static long? ClockAgeMs(ServerClock? clock, long nowTimestamp)
    {
        if (clock is not { Frequency: > 0 } c || nowTimestamp < c.ReceivedTimestamp)
            return null;
        var age = (((Int128)nowTimestamp - c.ReceivedTimestamp) * 1000 + c.Frequency - 1) / c.Frequency;
        return age <= long.MaxValue ? (long)age : long.MaxValue;
    }

    /// <summary>
    /// 要不要主動重新 Hello 換新的時鐘（計畫 D4）：有時鐘、年齡滿 <see cref="ClockRefreshMs"/>（或算不出年齡：單調時鐘倒退），
    /// 而且沒有執行中的指令（有就等它們做完，呼叫端每分鐘再看一次）。時鐘是 null 時回 false（沒有可換的；判不過的指令另外安排重新 Hello）
    /// </summary>
    public static bool ShouldRefreshClock(ServerClock? clock, long nowTimestamp, int inFlight)
    {
        if (clock is null || inFlight > 0)
            return false;
        return ClockAgeMs(clock, nowTimestamp) is not { } age || age >= ClockRefreshMs;
    }

    /// <summary>
    /// 時間判不過時可以再重新 Hello 嗎（計畫 D4：每分鐘最多一次）：以單調時戳判斷，距上一次滿 <see cref="ReHelloMinIntervalMs"/> 才放行。
    /// 還沒重新 Hello 過、頻率不是正數、現在早於上一次（單調時鐘倒退，不該發生）時一律放行——寧可多重連一次，不讓時鐘不可用的連線一直回 expired
    /// （合併前審查 T3：原本用牆上時鐘，往回調多久就卡多久）
    /// </summary>
    public static bool ReHelloAllowed(long? lastTimestamp, long nowTimestamp, long frequency)
        => lastTimestamp is not { } last || frequency <= 0 || nowTimestamp < last
           || ((Int128)nowTimestamp - last) * 1000 >= (Int128)ReHelloMinIntervalMs * frequency;

    /// <summary>
    /// 去重（計畫 D8；同步不變式「遠端指令至多執行一次」）：沒有列 → New；摘要不同 → Conflict；還是 executing 時，
    /// 在進行中表 → InFlight、有快取的結果 → Duplicate(快取)、都沒有 → Unknown；有 <c>result_json</c> → Duplicate(解析的結果)，解析不了 → Unknown
    /// </summary>
    public static CommandSeenDecision Decide(string contentSha256, CommandSeenRow? row, bool inFlight, CommandResult? cached)
    {
        if (row is null)
            return new CommandSeenDecision(CommandSeenKind.New, null);
        if (!string.Equals(row.ContentSha256, contentSha256, StringComparison.Ordinal))
            return new CommandSeenDecision(CommandSeenKind.Conflict, null);
        if (string.Equals(row.State, ExecutingState, StringComparison.Ordinal))
        {
            if (inFlight)
                return new CommandSeenDecision(CommandSeenKind.InFlight, null);
            return cached is not null
                ? new CommandSeenDecision(CommandSeenKind.Duplicate, cached)
                : new CommandSeenDecision(CommandSeenKind.Unknown, null);
        }

        return Parse(row.ResultJson, null) is { } previous
            ? new CommandSeenDecision(CommandSeenKind.Duplicate, previous)
            : new CommandSeenDecision(CommandSeenKind.Unknown, null);
    }

    /// <summary>
    /// 完成之後 <c>commands_seen</c> 那一列要不要留著（計畫 D9）：只有 rejected＋busy 不留——busy 表示什麼都沒執行，
    /// durable 的重簽要能再試；其他（含 expired 以外的所有拒絕、done、unknown）都回上一次的結果
    /// </summary>
    public static bool KeepsSeenRow(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return !(string.Equals(result.State, RemoteResultStates.Rejected, StringComparison.Ordinal)
                 && string.Equals(result.Code, RemoteResultCodes.Busy, StringComparison.Ordinal));
    }

    /// <summary>結果存進 <c>commands_seen.result_json</c> 的 JSON（駝峰式鍵、null 省略；含 commandId，一列自己就看得懂）</summary>
    public static string SerializeResult(CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return SyncJson.Serialize(result);
    }

    /// <summary>
    /// 讀回 <see cref="SerializeResult"/> 寫的結果：JSON 壞掉、不是合格的結果（<see cref="RemoteResultRules.Check"/>）、
    /// 或 commandId 與 <paramref name="commandId"/> 不同時回 null
    /// </summary>
    public static CommandResult? ParseResult(string commandId, string? json) => Parse(json, commandId);

    /// <summary>
    /// 驗信封失敗時回給伺服器的結果（計畫 D7）：外層 <c>commandId</c> 不是 ULID 回 null（伺服器的 <see cref="RemoteResultRules.Check"/> 也會丟掉，只寫紀錄）。
    /// 代碼照 verdict（第 8 步看伺服器時間界線的——過期、還沒生效、durable 超過 7 天、時鐘不可用——是 expired，其他 invalid）；
    /// <c>tier</c> 是解析過的信封宣告值（沒解析過、或不在 0～2 時是 null，否則整則結果不合格）；<c>message</c> 一律 null（步驟與細節只進紀錄）
    /// </summary>
    public static CommandResult? VerifyFailureResult(string? outerCommandId, CommandEnvelopeVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (verdict.Ok)
            throw new ArgumentException("verdict 是合格的，不是驗信封失敗", nameof(verdict));
        if (!SyncIds.IsUlid(outerCommandId))
            return null;
        var code = verdict.Code is { } c && RemoteResultCodes.All.Contains(c, StringComparer.Ordinal) ? c : RemoteResultCodes.Invalid;
        int? tier = verdict.Body is { Tier: >= 0 and <= 2 } body ? body.Tier : null;
        return new CommandResult(outerCommandId!, RemoteResultStates.Rejected, code, Message: null, Tier: tier);
    }

    /// <summary>不知道有沒有執行（執行中當機、完成時寫入失敗而快取也沒有、中樞 30 秒沒有完成信號）</summary>
    public static CommandResult UnknownResult(string commandId) => new(commandId, RemoteResultStates.Unknown);

    /// <summary>
    /// 遠端操作紀錄的 <c>body_masked</c>（計畫 D12）：body 的 JSON（鍵順序同信封），<c>cmd</c> 裡的 <c>text</c>、<c>message</c>、<c>answers</c>
    /// 換成 <c>{"redacted": 字數}</c>（字串的長度；不是字串時是精簡 JSON 的長度），<c>uploads</c> 換成 <c>{"count": n}</c>，
    /// <c>cmd</c> 裡其他的字串與鍵經 <see cref="CredentialLeakRules.Mask"/>；<c>issuer</c> 原樣（WP4.6 從這裡讀發起者）。
    /// 整份超過 <see cref="MaxAuditBodyChars"/> 字時依序把每個字串截短、加上 <c>"truncated": true</c>，仍超過就只留 cid、指令名稱與發起者。
    /// body 是 null（沒解析過）回 null
    /// </summary>
    public static string? MaskBodyForAudit(CommandEnvelopeBody? body)
    {
        if (body is null)
            return null;
        var root = new JObject
        {
            ["aud"] = body.Aud,
            ["cid"] = body.Cid,
            ["dev"] = body.Dev,
            ["conn"] = body.Conn,
        };
        if (body.Conv is not null)
            root["conv"] = body.Conv;
        if (body.Epoch is { } epoch)
            root["epoch"] = epoch;
        root["cmd"] = MaskCommand(body.Cmd);
        root["tier"] = body.Tier;
        root["fullAccess"] = body.FullAccess;
        root["durable"] = body.Durable;
        root["created"] = body.Created;
        root["iat"] = body.Iat;
        root["exp"] = body.Exp;
        root["issuer"] = new JObject { ["kind"] = body.Issuer.Kind, ["label"] = body.Issuer.Label };
        var auth = new JObject();
        if (body.Auth.UvAt is { } uvAt)
            auth["uvAt"] = uvAt;
        auth["defaultPassword"] = body.Auth.DefaultPassword;
        root["auth"] = auth;
        var json = root.ToString(Formatting.None);
        if (json.Length <= MaxAuditBodyChars)
            return json;

        foreach (var limit in TruncateSteps)
        {
            var cut = (JObject)CutStrings(root, limit);
            cut["truncated"] = true;
            json = cut.ToString(Formatting.None);
            if (json.Length <= MaxAuditBodyChars)
                return json;
        }

        var name = RemoteCommandShapeRules.IsString(body.Cmd["cmd"], out var cmd) ? cmd : string.Empty;
        return new JObject
        {
            ["cid"] = Cut(body.Cid, 64),
            ["cmd"] = new JObject { ["cmd"] = Cut(CredentialLeakRules.Mask(name), 64) },
            ["issuer"] = new JObject { ["kind"] = Cut(body.Issuer.Kind, 64), ["label"] = Cut(body.Issuer.Label, 64) },
            ["truncated"] = true,
        }.ToString(Formatting.None);
    }

    /// <summary>
    /// 從 <c>body_masked</c> 取出發起者標籤（<c>issuer.label</c>，WP4.6 的紀錄畫面顯示它；另一台桌面發起的顯示成「電腦「X」」，
    /// <see cref="Hub.RemoteNotifyRules.IssuerDisplay"/>）；不是 JSON 物件、沒有這個字串、
    /// 或標籤不合格（<see cref="CommandEnvelope.IsValidIssuerLabel"/>：在第 9 步被拒的信封可能帶控制或格式字元）時回 null
    /// </summary>
    public static string? IssuerLabelFromMasked(string? bodyMasked)
    {
        if (string.IsNullOrEmpty(bodyMasked))
            return null;
        return ConversationEventRules.TryParseStrict(bodyMasked, out var token) && token is JObject root && root["issuer"] is JObject issuer
               && RemoteCommandShapeRules.IsString(issuer["label"], out var label) && CommandEnvelope.IsValidIssuerLabel(label)
            ? Hub.RemoteNotifyRules.IssuerDisplay(RemoteCommandShapeRules.IsString(issuer["kind"], out var kind) ? kind : null, label)
            : null;
    }

    /// <summary>
    /// 簽的位元組 <c>UTF-8("myriagent-hub-cmd-v1\n" + body)</c> 的 SHA-256（64 個小寫十六進位；紀錄核對用）。信封或 body 是 null 回 null；
    /// body 有不成對的代理字元（驗不過的信封）時以取代字元編碼，照樣有值
    /// </summary>
    public static string? SignedSha256(CommandEnvelope? envelope)
    {
        if (envelope?.Body is not { } body)
            return null;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CommandEnvelope.Version + "\n" + body)));
    }

    /// <summary>紀錄裡的簽章：截到 <see cref="MaxSigLength"/> 字</summary>
    public static string? ClipSig(string? sig) => sig is { Length: > MaxSigLength } ? sig[..MaxSigLength] : sig;

    /// <summary>
    /// 遠端操作紀錄一頁的篩選（<see cref="SyncStore"/> 的 LoadRemoteAudit；排序一律是 <c>ORDER BY at DESC, seq DESC</c>）：沒有 <paramref name="beforeAt"/> 是第一頁（空字串，
    /// <paramref name="beforeSeq"/> 不看）；只有 <paramref name="beforeAt"/> 取更早的 <c>at</c>（WPR 的 <c>before</c>）；兩個都有時以 (at, seq) 翻頁，
    /// 同一個 <c>at</c> 的列不會被跳過或重複（計畫 6.1；WP4.6 D44）。參數名是 <c>$before</c>、<c>$beforeSeq</c>
    /// </summary>
    public static string RemoteAuditPageWhere(long? beforeAt, long? beforeSeq) => (beforeAt, beforeSeq) switch
    {
        (null, _) => "",
        (_, null) => "WHERE at < $before",
        _ => "WHERE (at < $before OR (at = $before AND seq < $beforeSeq))",
    };

    /// <summary>
    /// 這次 Hello 要不要修剪（計畫 D11）：沒修剪過（null）、距上次滿 <see cref="PruneIntervalMs"/>、或時間倒退（上次比現在晚）時是 true
    /// </summary>
    public static bool PruneDue(long? lastPruneAtMs, long nowMs)
        => lastPruneAtMs is not { } last || nowMs < last || nowMs - last >= PruneIntervalMs;

    /// <summary>
    /// 解除配對前匯出遠端操作紀錄的位置（計畫 D33）：同步資料夾（<see cref="SyncPaths.Directory"/>）的上層加 <c>remote-audit\{deviceId}.jsonl</c>
    /// （sync.db 會被刪，紀錄留在資料根目錄）。deviceId 不合格時丟 <see cref="ArgumentException"/>（不讓它變成路徑的一部分）
    /// </summary>
    public static string RemoteAuditExportPath(SyncPaths paths, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!SyncIds.IsDeviceId(deviceId))
            throw new ArgumentException("deviceId 不合格", nameof(deviceId));
        var syncDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Directory));
        var root = Path.GetDirectoryName(syncDirectory) ?? throw new ArgumentException("同步資料夾沒有上層", nameof(paths));
        return Path.Combine(root, "remote-audit", deviceId + ".jsonl");
    }

    private static CommandResult? Parse(string? json, string? expectedCommandId)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        CommandResult? result;
        try
        {
            result = SyncJson.Deserialize<CommandResult>(json);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return null;
        }

        if (result is null || RemoteResultRules.Check(result) is not null)
            return null;
        return expectedCommandId is null || string.Equals(result.CommandId, expectedCommandId, StringComparison.Ordinal) ? result : null;
    }

    private static JObject MaskCommand(JObject cmd)
    {
        var masked = new JObject();
        foreach (var property in cmd.Properties())
        {
            var name = CredentialLeakRules.Mask(property.Name);
            if (RedactedFields.Contains(property.Name))
                masked[name] = new JObject { ["redacted"] = LengthOf(property.Value) };
            else if (property.Name == "uploads")
                masked[name] = property.Value is JArray uploads ? new JObject { ["count"] = uploads.Count } : new JObject { ["redacted"] = LengthOf(property.Value) };
            else
                masked[name] = MaskStrings(property.Value);
        }

        return masked;
    }

    private static int LengthOf(JToken value)
        => value is JValue { Type: JTokenType.String, Value: string text } ? text.Length : value.ToString(Formatting.None).Length;

    /// <summary>遞迴遮蔽每個字串值與屬性名稱（複本）</summary>
    private static JToken MaskStrings(JToken token)
    {
        switch (token)
        {
            case JValue { Type: JTokenType.String, Value: string text }:
                return new JValue(CredentialLeakRules.Mask(text));
            case JObject obj:
                var result = new JObject();
                foreach (var property in obj.Properties())
                    result[CredentialLeakRules.Mask(property.Name)] = MaskStrings(property.Value);
                return result;
            case JArray array:
                return new JArray(array.Select(MaskStrings));
            default:
                return token.DeepClone();
        }
    }

    /// <summary>每個字串值截到 <paramref name="limit"/> 字（複本；已經遮蔽過，截斷不會露出權杖）</summary>
    private static JToken CutStrings(JToken token, int limit)
    {
        switch (token)
        {
            case JValue { Type: JTokenType.String, Value: string text }:
                return new JValue(Cut(text, limit));
            case JObject obj:
                var result = new JObject();
                foreach (var property in obj.Properties())
                    result[property.Name] = CutStrings(property.Value, limit);
                return result;
            case JArray array:
                return new JArray(array.Select(t => CutStrings(t, limit)));
            default:
                return token.DeepClone();
        }
    }

    /// <summary>截到 <paramref name="limit"/> 字並加「…」；不切開代理字元對</summary>
    private static string Cut(string text, int limit)
    {
        if (text.Length <= limit)
            return text;
        var end = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return text[..end] + "…";
    }
}
