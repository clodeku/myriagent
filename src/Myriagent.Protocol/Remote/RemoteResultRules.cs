using Myriagent.Sync;

namespace Myriagent.Remote;

/// <summary>
/// 裝置回報的遠端指令結果與輸出（<see cref="CommandResult"/>、<see cref="CommandOutput"/>；docs/sync-protocol.md 14.6）。
/// 伺服器（WP4.5）收到時檢查、電腦（WP4.4）送出前也可以自檢：被入侵的裝置回報的內容一律當成不可信（D18），
/// 格式不合格的整則丟掉、不轉給瀏覽器。只收目標裝置已證明連線送來的、輸出只轉給發起的連線，由伺服器的路由另外保證
/// </summary>
internal static class RemoteResultRules
{
    /// <summary>給人看的說明的上限（字元）：瀏覽器以 textContent 顯示</summary>
    public const int MaxMessageLength = 1_000;

    /// <summary>
    /// 結果合不合格：合格回 null，否則是記錄用的原因。<c>commandId</c> 是 ULID（信封的 <c>cid</c>）；<c>state</c> 是
    /// <see cref="RemoteResultStates"/> 之一；<c>rejected</c> 一定帶 <see cref="RemoteResultCodes"/> 裡的 <c>code</c>，<c>done</c>／<c>unknown</c> 不帶；
    /// <c>message</c> ≤<see cref="MaxMessageLength"/> 字；<c>tier</c> 在 0～2
    /// </summary>
    public static string? Check(CommandResult? result)
    {
        if (result is null || !SyncIds.IsUlid(result.CommandId))
            return "commandId 不是 ULID";
        switch (result.State)
        {
            case RemoteResultStates.Rejected:
                if (result.Code is null || !RemoteResultCodes.All.Contains(result.Code, StringComparer.Ordinal))
                    return "rejected 要帶認得的 code";
                break;
            case RemoteResultStates.Done or RemoteResultStates.Unknown:
                if (result.Code != null)
                    return "done／unknown 不帶 code";
                break;
            default:
                return "state 不認得";
        }

        if (result.Message is { Length: > MaxMessageLength })
            return "message 太長";
        if (result.Tier is < 0 or > 2)
            return "tier 不在 0～2";
        return null;
    }

    /// <summary>
    /// 輸出合不合格：合格回 null。<c>commandId</c> 是 ULID；<c>message</c> 是有字串 <c>type</c> 的物件，而且型錄允許
    /// <paramref name="commandName"/>（伺服器記下的、這個 commandId 原本的指令名稱，不是裝置說的）回這種訊息（<see cref="RemoteCommandCatalog.AllowsOutput"/>）
    /// </summary>
    public static string? CheckOutput(CommandOutput? output, string? commandName)
    {
        if (output is null || !SyncIds.IsUlid(output.CommandId))
            return "commandId 不是 ULID";
        if (output.Message is null || !RemoteCommandShapeRules.IsString(output.Message["type"], out var type))
            return "message 不是有 type 的物件";
        return RemoteCommandCatalog.AllowsOutput(commandName, type) ? null : "這個指令不能回這種訊息";
    }
}
