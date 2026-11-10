using System.Globalization;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>
/// 清單與標頭的裝置欄位（WP4.2 計畫 P5）：擁有者裝置的 id、顯示名稱與在不在線。
/// 重新配對留下的舊身分（已撤銷或不再連線）<see cref="Online"/> 永遠是 false（答 8）
/// </summary>
internal sealed record ConversationDeviceInfo(string? DeviceId, string? DeviceName, bool Online)
{
    /// <summary>沒有擁有者裝置的對話（不會出現；防禦）</summary>
    public static ConversationDeviceInfo None { get; } = new(null, null, false);
}

/// <summary>一頁放幾筆（從最新的一筆往前數）、放不下的後面還有沒有</summary>
internal readonly record struct PageSelection(int Count, bool HasMore);

/// <summary>
/// 瀏覽器的對話清單、標頭與輸入區的組成（WP4.2 計畫步 8；D10、D11、D20、D21、D22；web-protocol 第三節 ConversationSummary、Header、Composer）。
/// 純函式：輸入是 <see cref="ConversationReader"/> 讀出的資料列與讀取當下的投影（擁有者在不在線、重建中、未讀），輸出逐欄組成（白名單），
/// 不把裝置送的 JSON 原樣轉出。擁有者離線、recovering、重建中都是讀取時的投影、不存。
/// <list type="bullet">
/// <item>時區照 JS 的 <c>getTimezoneOffset()</c>：<c>tzOffsetMinutes</c> 是「UTC 減本地」的分鐘數（臺灣是 -480），當地時間＝UTC 減它（D22）。</item>
/// <item>完整的模型清單只在桌面：<c>models</c> 只有「預設」與目前的模型，各自的 <c>efforts</c> 只有目前的強度（選單不會是空的，也不會把目前的值標成不支援；D21）。</item>
/// </list>
/// </summary>
internal static class ConversationViewRules
{
    /// <summary><c>conversation</c>／<c>olderItems</c> 一頁最多幾筆（D20）</summary>
    public const int PageItems = 80;

    /// <summary>清單最多幾筆（D20；web-protocol 的 conversations）</summary>
    public const int MaxListItems = 1000;

    /// <summary>擁有者不在線時標頭的狀態列（D21）</summary>
    public const string OfflineStatus = "電腦離線";

    /// <summary>伺服器還原之後、擁有者還沒重新上傳時的狀態列（D16、D21）</summary>
    public const string RecoveringStatus = "等待電腦重新上傳";

    /// <summary>物化表重建中的狀態列（D14、D21）</summary>
    public const string RebuildingStatus = "伺服器正在整理這個對話";

    /// <summary>模型與推理強度的「帳戶預設」</summary>
    public const string DefaultValue = "default";

    /// <summary>附件處理中而沒有說明時的文字（同桌面 AgentHub.RenderComposer）</summary>
    public const string DefaultBusyText = "正在處理附件…";

    private const long MinuteMs = 60_000;

    private const long DayMs = 86_400_000;

    /// <summary><see cref="DateTime"/> 表示得了的 Unix 毫秒範圍（超出的夾在邊界，不丟例外）</summary>
    private static readonly long MinUnixMs = (DateTime.MinValue.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;

    private static readonly long MaxUnixMs = (DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;

    // ───── 時區與日期分組（D22） ─────

    /// <summary>UTC 的 Unix 毫秒換成當地時間的「Unix 毫秒」：減掉 <paramref name="tzOffsetMinutes"/> 分鐘（臺灣 -480 → 加 8 小時）</summary>
    public static long ToLocalMs(long utcMs, int tzOffsetMinutes) => utcMs - tzOffsetMinutes * MinuteMs;

    /// <summary>
    /// 收到 ClientState 之前用的時區：伺服器的時區（<c>TimeProvider.LocalTimeZone</c>）在 <paramref name="utcMs"/> 那一刻的偏移，
    /// 換成 <c>getTimezoneOffset()</c> 的正負號（UTC＋8 → -480），夾在 ClientState 允許的 ±840 分鐘內
    /// </summary>
    public static int FallbackOffset(TimeZoneInfo zone, long utcMs)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var offset = zone.GetUtcOffset(DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(utcMs, DateTimeOffset.MinValue.ToUnixTimeMilliseconds(),
            DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())));
        return Math.Clamp(-(int)Math.Round(offset.TotalMinutes), -MessageGuard.MaxTzOffsetMinutes, MessageGuard.MaxTzOffsetMinutes);
    }

    /// <summary>清單的日期分組（今天、昨天、過去 7 天、過去 30 天、月份）：兩個時間都換成連線的當地時間後呼叫 <see cref="AgentHubRules.GetGroupLabel"/></summary>
    public static string GroupLabel(long updatedMs, long nowMs, int tzOffsetMinutes) =>
        AgentHubRules.GetGroupLabel(LocalDateTime(ToLocalMs(Safe(updatedMs), tzOffsetMinutes)), LocalDateTime(ToLocalMs(Safe(nowMs), tzOffsetMinutes)));

    /// <summary>下一個當地午夜（UTC 的 Unix 毫秒）：到了就重送一次清單（分組跟著換，R21）</summary>
    public static long NextDayBoundaryMs(long nowMs, int tzOffsetMinutes)
    {
        var local = ToLocalMs(nowMs, tzOffsetMinutes);
        var nextMidnight = (FloorDiv(local, DayMs) + 1) * DayMs;
        return nextMidnight + tzOffsetMinutes * MinuteMs;
    }

    // ───── 清單（ConversationSummary） ─────

    /// <summary>
    /// 一列 ConversationSummary（web-protocol 第三節的欄位加 P5 的 <c>deviceId</c>、<c>deviceName</c>、<c>deviceOnline</c>）。
    /// <c>group</c> 依連線的時區；<c>unread</c> 由伺服器的未讀集合傳入（D45，步 12A）；擁有者不在線時 <c>running</c> 是 false（同標頭的離線投影）
    /// </summary>
    public static JObject Summary(ConversationListRow row, long nowMs, int tzOffsetMinutes, ConversationDeviceInfo device, bool unread)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(device);
        return new JObject
        {
            ["id"] = row.ConversationId,
            ["accountId"] = S(row.AccountId),
            ["provider"] = row.Provider.ToString(),
            ["title"] = row.Title,
            ["group"] = GroupLabel(row.UpdatedAt, nowMs, tzOffsetMinutes),
            ["updatedAt"] = row.UpdatedAt,
            ["workingDirectory"] = S(row.WorkingDirectory),
            ["customFolder"] = row.CustomFolder,
            ["running"] = row.Running && device.Online,
            ["waiting"] = row.Waiting,
            ["unread"] = unread,
            ["deviceId"] = S(device.DeviceId),
            ["deviceName"] = S(device.DeviceName),
            ["deviceOnline"] = device.Online,
        };
    }

    // ───── 標頭（Header） ─────

    /// <summary>
    /// 標頭（web-protocol 第三節的欄位加 P5 的裝置欄位；D21）。<c>commands</c>、<c>recentFolders</c> 是空的，<c>restart</c> 是 null、<c>isDraft</c> 是 false，
    /// <c>steer</c> 照代理種類（AgentHubRules.SupportsSteering）。
    /// 狀態列依序：重建中「伺服器正在整理這個對話」→ 擁有者不在線「電腦離線」（<c>running</c> 也是 false）→ recovering「等待電腦重新上傳」→ 存下的狀態文字
    /// </summary>
    public static JObject Header(ConversationHeaderRow row, ConversationDeviceInfo device, bool rebuilding)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(device);
        var running = row.Running && device.Online;
        var header = new JObject
        {
            ["id"] = row.ConversationId,
            ["accountId"] = S(row.AccountId),
            ["provider"] = row.Provider.ToString(),
            ["title"] = row.Title,
            ["model"] = OrDefault(row.Model),
            ["effort"] = OrDefault(row.Effort),
            ["permission"] = Permission(row.Provider, row.PermissionMode).ToString(),
            ["permissionModes"] = PermissionModes(row.Provider),
            ["models"] = Models(row.Model, row.Effort, row.Models),
            ["commands"] = new JArray(),
            ["workingDirectory"] = S(row.WorkingDirectory),
            ["customFolder"] = row.CustomFolder,
            ["recentFolders"] = new JArray(),
            ["isDraft"] = false,
            ["running"] = running,
            // 執行中送出直接插進這一輪（Claude、Codex）：電腦那邊插之前以當時的模式與政策重查，不允許才排隊（網頁據此寫送出鈕）
            ["steer"] = Myriagent.Hub.AgentHubRules.SupportsSteering(row.Provider),
            ["status"] = S(Status(row, device, rebuilding)),
            ["startedAt"] = running && row.TurnStartedAt is { } started ? new JValue(started) : JValue.CreateNull(),
            ["lastTurn"] = S(LastTurnText(row.LastTurn)),
            ["accountMissing"] = row.AccountId is null,
            ["restart"] = JValue.CreateNull(),
            ["deviceId"] = S(device.DeviceId),
            ["deviceName"] = S(device.DeviceName),
            ["deviceOnline"] = device.Online,
        };
        // 臨時對話（瀏覽器版開的，meta 的 ephemeral）：網頁在回覆上顯示「朗讀」、關掉面板就刪掉；一般對話不帶（標頭的形狀不變）
        if (row.Ephemeral)
            header["ephemeral"] = true;
        return header;
    }

    /// <summary>標頭的狀態列（見 <see cref="Header"/>）</summary>
    public static string? Status(ConversationHeaderRow row, ConversationDeviceInfo device, bool rebuilding)
    {
        if (rebuilding)
            return RebuildingStatus;
        if (!device.Online)
            return OfflineStatus;
        if (IsRecovering(row.LeaseState))
            return RecoveringStatus;
        return string.IsNullOrEmpty(row.StatusText) ? null : row.StatusText;
    }

    /// <summary><c>lease_state</c> 是 recovering；不認得的值（之後的版本）一律當成 owned（D16）</summary>
    public static bool IsRecovering(string? leaseState) => string.Equals(leaseState, ConversationLeaseStates.Recovering, StringComparison.Ordinal);

    /// <summary>meta 的權限模式（名稱；null 或不認得＝每步詢問，同桌面 ConversationMeta 的預設）經 <see cref="AgentPermissionModes.Normalize"/></summary>
    public static AgentPermissionMode Permission(AgentProvider provider, string? mode)
    {
        var parsed = mode is not null && Enum.TryParse<AgentPermissionMode>(mode, ignoreCase: false, out var value) && Enum.IsDefined(value)
            && string.Equals(value.ToString(), mode, StringComparison.Ordinal)
                ? value
                : AgentPermissionMode.Ask;
        return AgentPermissionModes.Normalize(provider, parsed);
    }

    /// <summary>這個代理可用的權限模式（同桌面 AgentHub.RenderPermissionModes 的形狀）</summary>
    public static JArray PermissionModes(AgentProvider provider) =>
        new(AgentPermissionModes.For(provider).Select(o => new JObject
        {
            ["value"] = o.Mode.ToString(),
            ["label"] = o.Label,
            ["description"] = o.Description,
        }));

    /// <summary>
    /// 模型選單。擁有者電腦發布了這個對話的帳戶可選的模型（meta 的 <c>models</c>，2026-10-23 起）時照它列，同桌面的標頭：最前面沒有「預設」就補一個
    /// （<c>efforts</c> 用第一項的，同桌面 AgentHub.EnsureDefaultModel），目前的模型不在清單裡時加在最後（「目前使用中」，<c>efforts</c> 只有目前的強度）；
    /// 目前的強度不在那個模型的 <c>efforts</c> 裡時網頁照實標成不支援（同桌面）。
    /// 沒發布時（舊版電腦）：「預設」加上目前的模型（不是預設時）；兩者的 <c>efforts</c> 都只有目前的強度（不是預設時），所以目前的值一定在清單裡（D21）
    /// </summary>
    public static JArray Models(string? model, string? effort, IReadOnlyList<ConversationModelOption>? published = null)
    {
        var efforts = IsSet(effort) ? new JArray(effort!) : new JArray();
        if (published is not { Count: > 0 })
        {
            var fallback = new JArray { ModelOption(DefaultValue, "預設", "帳戶預設模型", efforts) };
            if (IsSet(model))
                fallback.Add(ModelOption(model!, model!, "目前使用中", efforts));
            return fallback;
        }

        var models = new JArray();
        if (!published.Any(m => string.Equals(m.Value, DefaultValue, StringComparison.OrdinalIgnoreCase)))
            models.Add(ModelOption(DefaultValue, "預設", "帳戶預設模型", new JArray(published[0].Efforts ?? [])));
        foreach (var m in published)
            models.Add(ModelOption(m.Value, m.Label, m.Description, new JArray(m.Efforts ?? [])));
        if (IsSet(model) && !published.Any(m => string.Equals(m.Value, model, StringComparison.Ordinal)))
            models.Add(ModelOption(model!, model!, "目前使用中", efforts));
        return models;
    }

    /// <summary>
    /// meta_json 的 <c>models</c>（擁有者電腦整理過、伺服器收到時驗過的；這裡仍不信任存下的內容）：只收陣列裡 value 是 1～128 字字串的物件，
    /// label 不是非空字串時用 value、description 不是字串時 null、efforts 只收非空字串；同一個 value 只收第一個，最多
    /// <see cref="ConversationEventRules.MaxModelOptions"/> 項。不是陣列或一項都沒有回 null（照舊列「預設」與目前的模型）
    /// </summary>
    public static IReadOnlyList<ConversationModelOption>? PublishedModels(JToken? token)
    {
        if (token is not JArray list)
            return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ConversationModelOption>();
        foreach (var entry in list)
        {
            if (result.Count >= ConversationEventRules.MaxModelOptions)
                break;
            if (entry is not JObject m || Text(m["value"]) is not { Length: > 0 and <= ConversationEventRules.MaxShortTextLength } value || !seen.Add(value))
                continue;
            var efforts = m["efforts"] is JArray e ? e.Select(Text).OfType<string>().Where(x => x.Length > 0).ToList() : [];
            result.Add(new ConversationModelOption(value, Text(m["label"]) is { Length: > 0 } label ? label : value, Text(m["description"]),
                efforts.Count > 0 ? efforts : null));
        }

        return result.Count > 0 ? result : null;

        static string? Text(JToken? t) => t is JValue { Type: JTokenType.String, Value: string s } ? s : null;
    }

    /// <summary>上一輪的用量（同桌面標頭的格式；數字以不變文化特性加千分位）；沒有用量回 null</summary>
    public static string? LastTurnText(ConversationTurnPayload? turn) =>
        turn?.Usage is { } usage
            ? string.Create(CultureInfo.InvariantCulture, $"輸入 {usage.InputTokens:N0}・輸出 {usage.OutputTokens:N0} tokens")
            : null;

    // ───── 輸入區（Composer） ─────

    /// <summary>
    /// 輸入區：附件一律空的（伺服器沒有本機附件）、<c>queue</c> 照 <c>displayText</c>；佇列還有沒帶到的則數時另帶 <c>queueMore</c>（P11，省略＝0）。
    /// <c>busy</c>／<c>busyText</c> 讀 <c>conversation_runtime</c> 的欄位，但沒有任何事件寫入這兩欄（電腦不送附件處理的狀態，<c>conv.queue</c> 只有
    /// entries 與 more），所以實際上一律是 false／null（合併前審查 B；要顯示時第 5 階段另外擴充契約）。沒有這一列＝空的輸入區
    /// </summary>
    public static JObject Composer(ConversationRuntimeRow? runtime)
    {
        var busy = runtime?.Busy ?? false;
        var composer = new JObject
        {
            ["attachments"] = new JArray(),
            ["busy"] = busy,
            ["busyText"] = busy ? new JValue(runtime!.BusyText ?? DefaultBusyText) : JValue.CreateNull(),
            ["queue"] = new JArray((runtime?.Queue ?? Array.Empty<ConversationQueueEntry>()).Select(q => new JObject { ["id"] = q.Id, ["text"] = q.DisplayText })),
        };
        if (runtime is { More: > 0 } withMore)
            composer["queueMore"] = withMore.More;
        return composer;
    }

    // ───── 分頁（D20） ─────

    /// <summary>
    /// 一頁放幾筆：<paramref name="newestFirstBytes"/> 是候選項目渲染後的位元組數（最新的在前），最多 <paramref name="maxItems"/> 筆、
    /// 加起來不超過 <paramref name="maxBytes"/>；至少放一筆（單筆超過上限也放）。放不下的（含超過筆數的）由 <see cref="PageSelection.HasMore"/> 表示
    /// </summary>
    public static PageSelection SelectPage(IReadOnlyList<int> newestFirstBytes, int maxItems, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(newestFirstBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItems);
        var count = 0;
        long total = 0;
        foreach (var bytes in newestFirstBytes)
        {
            if (count >= maxItems || (count > 0 && total + bytes > maxBytes))
                break;
            total += bytes;
            count++;
        }
        return new PageSelection(count, count < newestFirstBytes.Count);
    }

    // ───── 代理種類與裝置 ─────

    /// <summary>
    /// <c>conversations.provider</c>（線上的名稱，完全相同才算）→ 代理種類；不認得回 null（讀取端跳過這一列並記錄）。
    /// 寫成 switch：新的代理種類要在這裡加（測試以 AgentProviders.All 檢查每一種都認得）
    /// </summary>
    public static AgentProvider? ProviderOf(string? wire) => wire switch
    {
        "Claude" => AgentProvider.Claude,
        "Codex" => AgentProvider.Codex,
        "Antigravity" => AgentProvider.Antigravity,
        "Grok" => AgentProvider.Grok,
        _ => null,
    };

    /// <summary>
    /// 擁有者裝置的欄位：名稱是 <c>device_streams.name</c>（沒有時用配對時的名稱，同 DeviceView）；在線＝有連線而且沒有撤銷、在裝置目錄裡
    /// （重新配對留下的舊身分：已撤銷或不再連線，永遠是 false，答 8）
    /// </summary>
    public static ConversationDeviceInfo Device(string? ownerDeviceId, DeviceInfo? info, string? streamName, bool online)
    {
        if (ownerDeviceId is null)
            return ConversationDeviceInfo.None;
        var name = !string.IsNullOrWhiteSpace(streamName) ? streamName : info?.Name;
        return new ConversationDeviceInfo(ownerDeviceId, name, online && info is { Revoked: false });
    }

    private static JObject ModelOption(string value, string label, string? description, JArray efforts) => new()
    {
        ["value"] = value,
        ["label"] = label,
        ["description"] = S(description),
        ["efforts"] = efforts.DeepClone(),
    };

    private static bool IsSet(string? value) => !string.IsNullOrEmpty(value) && !string.Equals(value, DefaultValue, StringComparison.Ordinal);

    private static string OrDefault(string? value) => string.IsNullOrEmpty(value) ? DefaultValue : value;

    /// <summary>夾在 <see cref="DateTime"/> 的範圍內、兩端各留一天（加減時區偏移不會溢位）</summary>
    private static long Safe(long utcMs) => Math.Clamp(utcMs, MinUnixMs + DayMs, MaxUnixMs - DayMs);

    private static DateTime LocalDateTime(long localMs) =>
        new(DateTime.UnixEpoch.Ticks + Math.Clamp(localMs, MinUnixMs, MaxUnixMs) * TimeSpan.TicksPerMillisecond, DateTimeKind.Unspecified);

    private static long FloorDiv(long value, long divisor)
    {
        var quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    /// <summary>可為 null 的字串：null 寫成 JSON null 的節點（同 AccountViewRules）</summary>
    private static JToken S(string? value) => value is null ? JValue.CreateNull() : new JValue(value);
}
