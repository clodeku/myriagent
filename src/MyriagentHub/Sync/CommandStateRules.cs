using Myriagent.Remote;
using Myriagent.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>
/// sync.db <c>commands.state</c> 的六個值（WP4.5 計畫 D15、D18；只有伺服器寫）：<see cref="Pending"/>（durable、等電腦上線）、
/// <see cref="Delivered"/>（已送出、等結果），以及四個最終狀態
/// </summary>
internal static class CommandStates
{
    public const string Pending = "pending", Delivered = "delivered", Done = "done", Rejected = "rejected", Unknown = "unknown", Expired = "expired";

    public static IReadOnlyList<string> All { get; } = [Pending, Delivered, Done, Rejected, Unknown, Expired];

    /// <summary>最終狀態（done、rejected、unknown、expired）；不認得的值不算（呼叫端一律不動它，見 <see cref="CommandStateRules"/>）</summary>
    public static bool IsFinal(string state) => state is Done or Rejected or Unknown or Expired;

    /// <summary>還沒結束（pending、delivered）</summary>
    public static bool IsOpen(string state) => state is Pending or Delivered;
}

/// <summary>
/// 收到電腦的結果之後列的下一步（D18）：<paramref name="State"/> 是新的 <c>state</c>；<paramref name="Forward"/>＝轉給發起的連線；
/// <paramref name="Resend"/>＝durable 回到 pending、排重送（D27）；<paramref name="Late"/>＝列已經結束，結果只記進 <c>result_json.late</c>
/// </summary>
internal sealed record CommandResultStep(string State, bool Forward, bool Resend, bool Late);

/// <summary>
/// 遠端指令列的狀態轉移與期限（WP4.5 計畫 D13、D18、D19、D27、D28；durable 的流程在 <c>DurableCommands</c>）。純函式，時間一律是伺服器時間（Unix 毫秒，<c>TimeProvider</c>）。
/// 不認得的 <c>state</c>（不應出現：只有本程式寫這張表）一律當成已結束、不改它：不轉送、不重送、不清掉（至多執行一次優先）
/// </summary>
internal static class CommandStateRules
{
    /// <summary>非 durable 的結果期限：<c>iat</c>＋100 秒＝信封 60 秒＋電腦等完成信號 30 秒（契約 D21）＋傳輸 10 秒（D19）</summary>
    public const long ResultDeadlineMs = 100_000;

    /// <summary>列結束之後還收輸出多久（D20；同一條連線上輸出一定先於結果，這是重連與排程的餘裕）</summary>
    public const long OutputGraceMs = 30_000;

    /// <summary>結束多久之後刪掉（D28；與擁有者「被拒事件保留 30 天」一致）</summary>
    public const long RetentionMs = 30L * 24 * 60 * 60 * 1000;

    /// <summary>durable 收到 busy／expired 之後，同一條連線最多排幾次 5 秒後的重送（D27；之後等下一次 Prove，或每 60 秒的重掃再送一次）</summary>
    public const int MaxBusyRetries = 3;

    /// <summary>durable 收到 busy／expired 之後多久重送（D27）</summary>
    public const long BusyRetryDelayMs = 5_000;

    /// <summary>在線、有 cmd1 的電腦多久重掃一次 pending 的 durable 列（D27）</summary>
    public const long RescanIntervalMs = 60_000;

    /// <summary>清理時每個交易最多刪幾列（D28）</summary>
    public const int PruneBatch = 500;

    /// <summary><c>result_json.late</c> 最多記幾筆遲到的結果（D15；之後的不再記，保留先到的）</summary>
    public const int MaxLateResults = 4;

    /// <summary>列的 <c>expires_at</c>（D15）：非 durable 是結果期限 <c>created</c>＋100 秒，durable 是 <c>created</c>＋7 天</summary>
    public static long ExpiresAt(bool durable, long created)
        => created + (durable ? CommandEnvelope.MaxDurableAgeMs : ResultDeadlineMs);

    /// <summary>durable 重簽的 <c>exp</c>（D13）：min(<paramref name="now"/>＋60 秒, <paramref name="created"/>＋7 天)</summary>
    public static long ResignExp(long created, long now)
        => Math.Min(now + CommandEnvelope.MaxLifetimeMs, created + CommandEnvelope.MaxDurableAgeMs);

    /// <summary>
    /// 收到結果（已通過 <see cref="RemoteResultRules.Check"/>）時（D18）：
    /// <list type="bullet">
    /// <item>列還沒結束（pending、delivered）：durable 收到 <c>rejected busy／expired</c>（電腦什麼都沒執行）回到 pending、排重送、不轉送；
    /// 其他的 done、rejected、unknown 各自變成同名的最終狀態並轉送（非 durable 的 busy、expired 也是最終的 rejected）。</item>
    /// <item>列已結束（或不認得的狀態）：狀態不變、不轉送，記成遲到的結果（同一個 bcid 不會有兩個結果）。</item>
    /// </list>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="resultState"/> 不是 <see cref="RemoteResultStates"/> 之一</exception>
    public static CommandResultStep OnResult(string state, bool durable, string resultState, string? code)
    {
        var next = resultState switch
        {
            RemoteResultStates.Done => CommandStates.Done,
            RemoteResultStates.Rejected => CommandStates.Rejected,
            RemoteResultStates.Unknown => CommandStates.Unknown,
            _ => throw new ArgumentOutOfRangeException(nameof(resultState), resultState, "未知的結果狀態"),
        };
        if (!CommandStates.IsOpen(state))
            return new CommandResultStep(state, Forward: false, Resend: false, Late: true);
        if (durable && next == CommandStates.Rejected && code is RemoteResultCodes.Busy or RemoteResultCodes.Expired)
            return new CommandResultStep(CommandStates.Pending, Forward: false, Resend: true, Late: false);
        return new CommandResultStep(next, Forward: true, Resend: false, Late: false);
    }

    /// <summary>
    /// 結果期限（D19）：非 durable 的列到 <paramref name="expiresAt"/>（含）還是 delivered 就變成 unknown；其他（durable、pending、已結束）回 null＝不變。
    /// durable 送達後沒回結果時保持 delivered，下次 Prove 重簽
    /// </summary>
    public static string? OnDeadline(string state, bool durable, long expiresAt, long now)
        => !durable && state == CommandStates.Delivered && now >= expiresAt ? CommandStates.Unknown : null;

    /// <summary>
    /// 伺服器啟動時（D28；至多執行一次）：非 durable、還沒結束的變成 unknown（絕不重送）；durable 到期（<c>created</c>＋7 天，含）的變成 expired；
    /// durable 的 delivered 改回 pending（下次 Prove 以新的連線重簽）；其他回 null＝不變
    /// </summary>
    public static string? OnStartup(string state, bool durable, long expiresAt, long now)
    {
        if (!CommandStates.IsOpen(state))
            return null;
        if (!durable)
            return CommandStates.Unknown;
        if (now >= expiresAt)
            return CommandStates.Expired;
        return state == CommandStates.Delivered ? CommandStates.Pending : null;
    }

    /// <summary>
    /// durable 的 7 天期限（D27；執行期間的維護與重簽前）：還沒結束（pending、delivered）的 durable 列到 <paramref name="expiresAt"/>
    /// （<c>created</c>＋7 天，含）變成 expired、不再送；其他（非 durable、已結束、還沒到期）回 null＝不變
    /// </summary>
    public static string? OnDurableExpiry(string state, bool durable, long expiresAt, long now)
        => durable && CommandStates.IsOpen(state) && now >= expiresAt ? CommandStates.Expired : null;

    /// <summary>清理（D28）：最終狀態、而且結束（<paramref name="doneAt"/>）滿 30 天（含）；還沒結束的永遠不清</summary>
    public static bool Prunable(string state, long? doneAt, long now)
        => CommandStates.IsFinal(state) && doneAt is { } at && now - at >= RetentionMs;

    /// <summary>
    /// 轉到這個狀態時要不要清掉 <c>unsigned_body</c>（D18、D41）：最終狀態（含 expired）要清；pending、delivered 還要靠它在重連時重簽
    /// （契約寫「送達或過期」，改成最終結果或過期時才清，設計 14.15）
    /// </summary>
    public static bool ClearsUnsignedBody(string state) => CommandStates.IsFinal(state);

    /// <summary>
    /// 列結束時的 <c>result_json</c>（D15）：<c>{state, code, message, tier, fullAccess, at}</c>（鍵的順序固定、不縮排；沒有的是 null）。
    /// <paramref name="state"/> 是列的最終狀態（伺服器自己判的 unknown／expired 沒有代碼與層級）；<paramref name="at"/> 是伺服器時間
    /// </summary>
    public static string ResultJson(string state, string? code, string? message, int? tier, bool? fullAccess, long at)
        => ResultEntry(state, code, message, tier, fullAccess, at).ToString(Formatting.None);

    /// <summary>
    /// 遲到的結果（列已結束，D18）記進 <c>result_json.late</c>（陣列、依到達順序），每筆的形狀同 <see cref="ResultJson"/>；已有
    /// <see cref="MaxLateResults"/> 筆時原樣回傳（不再記）。<paramref name="resultJson"/> 不是 JSON 物件（null、壞掉）時從空物件開始，其他鍵不動
    /// </summary>
    public static string WithLate(string? resultJson, string state, string? code, string? message, int? tier, bool? fullAccess, long at)
    {
        var root = Parse(resultJson) ?? [];
        if (root["late"] is not JArray late)
        {
            late = [];
            root["late"] = late;
        }
        if (late.Count >= MaxLateResults)
            return root.ToString(Formatting.None);
        late.Add(ResultEntry(state, code, message, tier, fullAccess, at));
        return root.ToString(Formatting.None);
    }

    private static JObject ResultEntry(string state, string? code, string? message, int? tier, bool? fullAccess, long at) => new()
    {
        ["state"] = state,
        ["code"] = code,
        ["message"] = message,
        ["tier"] = tier,
        ["fullAccess"] = fullAccess,
        ["at"] = at,
    };

    private static JObject? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            // 不把像日期的字串轉成 DateTime（電腦的說明原樣保留）
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            return JToken.ReadFrom(reader) as JObject;
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }
}
