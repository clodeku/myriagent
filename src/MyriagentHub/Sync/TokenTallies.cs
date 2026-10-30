using Myriagent.Sync;
using MyriagentHub.Data;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.Sync;

/// <summary>
/// 各帳戶的 token 計數（tok1；docs/sync-protocol.md 14.9）：裝置的 <c>TokensPublish</c> 寫進 sync.db 的 <c>token_tallies</c>（full 先刪掉這台的所有列，
/// 否則逐格取代），同一個交易刪掉所有裝置超過保留天數的列（以伺服器的本機日期算）。提交之後發 <see cref="Changed"/>（瀏覽器閘道只記髒）。
/// 讀的一方是瀏覽器快照（<see cref="AccountViewBuilder"/>，<see cref="ReadRows"/>）與給桌面的轉送迴圈（<see cref="TokenTallyFanout"/>，tok2）。
/// 只供顯示：不寫事件、不影響任何判斷
/// </summary>
internal sealed class TokenTallies(SyncWriter writer, TimeProvider time)
{
    /// <summary>提交之後（在寫入執行緒上：訂閱者只能記髒或喚醒，不可阻塞）</summary>
    public event Action? Changed;

    /// <summary>寫入一則已檢查過（<see cref="TokenTallySyncRules.Check"/>）的 TokensPublish</summary>
    public Task PublishAsync(string deviceId, TokensPublish publish, CancellationToken cancellationToken = default)
        => writer.WriteAsync(context =>
        {
            if (publish.Full)
                context.Execute("DELETE FROM token_tallies WHERE device_id = $d", ("$d", deviceId));
            foreach (var row in publish.Rows)
            {
                context.Execute(
                    "INSERT OR REPLACE INTO token_tallies(device_id, logical_account_id, day, input, cached, output, updated_at) "
                    + "VALUES($d, $la, $day, $in, $cached, $out, $now)",
                    ("$d", deviceId), ("$la", row.LogicalAccountId), ("$day", row.Day), ("$in", row.Input), ("$cached", row.Cached), ("$out", row.Output),
                    ("$now", context.Now));
            }
            // 保留天數以外的不會再顯示（View 只算 30 天內）：比最早的一天還早的列刪掉，表的大小有上限
            context.Execute("DELETE FROM token_tallies WHERE day < $from", ("$from", TokenTallySyncRules.DayKey(Today(context.Now).AddDays(-(TokenTallySyncRules.RetentionDays - 1)))));
            context.AfterCommit(() => Changed?.Invoke());
        }, cancellationToken);

    /// <summary>伺服器的本機日期（瀏覽器的「今日」以它為準）</summary>
    public DateOnly Today(long nowMs)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(nowMs), time.LocalTimeZone).DateTime);

    /// <summary>
    /// 瀏覽器快照讀的列（在呼叫者的唯讀交易裡）：<paramref name="today"/> 往回保留天數內、帳戶沒刪除、裝置沒撤銷（<paramref name="activeDevice"/>）的
    /// </summary>
    public static List<TokenTallyRow> ReadRows(SqliteConnection connection, SqliteTransaction transaction, DateOnly today, Func<string, bool> activeDevice)
        => ReadDeviceRows(connection, transaction, today, activeDevice).Select(r => r.Row).ToList();

    /// <summary>同 <see cref="ReadRows"/>，連同是哪一台的（tok2 的轉送迴圈：給每台的是它以外的合計，<see cref="TokenTallySyncRules.Others"/>）</summary>
    public static List<(string DeviceId, TokenTallyRow Row)> ReadDeviceRows(SqliteConnection connection, SqliteTransaction transaction, DateOnly today,
        Func<string, bool> activeDevice)
    {
        var rows = new List<(string DeviceId, TokenTallyRow Row)>();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT t.device_id, t.logical_account_id, t.day, t.input, t.cached, t.output FROM token_tallies t "
            + "JOIN logical_accounts a ON a.logical_account_id = t.logical_account_id AND a.deleted_at IS NULL WHERE t.day >= $from";
        cmd.Parameters.AddWithValue("$from", TokenTallySyncRules.DayKey(today.AddDays(-(TokenTallySyncRules.RetentionDays - 1))));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(0) is var deviceId && activeDevice(deviceId))
                rows.Add((deviceId, new TokenTallyRow(reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5))));
        }
        return rows;
    }
}
