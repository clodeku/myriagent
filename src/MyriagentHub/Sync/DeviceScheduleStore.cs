using Myriagent.Sync;
using MyriagentHub.Data;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>
/// 各電腦的排程（sched1；docs/sync-protocol.md 14.12）：裝置的 <c>SchedulesPublish</c> 寫進 sync.db 的 <c>device_schedules</c>（一台一列，整份取代）。
/// 提交之後發 <see cref="Changed"/>（瀏覽器閘道只記髒、轉送迴圈喚醒）。讀的一方是瀏覽器快照（<see cref="AccountViewBuilder"/>）與給桌面的轉送迴圈
/// （<see cref="ScheduleFanout"/>）。只供顯示：不寫事件、不解讀內容
/// </summary>
internal sealed class DeviceScheduleStore(SyncWriter writer)
{
    /// <summary>提交之後（在寫入執行緒上：訂閱者只能記髒或喚醒，不可阻塞）</summary>
    public event Action? Changed;

    /// <summary>寫入一則已檢查過（<see cref="ScheduleSyncRules.Check"/>）的 SchedulesPublish</summary>
    public Task PublishAsync(string deviceId, SchedulesPublish publish, CancellationToken cancellationToken = default)
        => writer.WriteAsync(context =>
        {
            context.Execute("INSERT OR REPLACE INTO device_schedules(device_id, snapshot, updated_at) VALUES($d, $s, $now)",
                ("$d", deviceId), ("$s", publish.Snapshot!.ToString(Formatting.None)), ("$now", context.Now));
            context.AfterCommit(() => Changed?.Invoke());
        }, cancellationToken);

    /// <summary>沒撤銷的電腦（<paramref name="activeDevice"/>）最後回報的摘要（在呼叫者的唯讀交易裡；解析失敗的列略過）</summary>
    public static List<DeviceSchedulesView> Read(SqliteConnection connection, SqliteTransaction transaction, Func<string, bool> activeDevice)
    {
        var list = new List<DeviceSchedulesView>();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT device_id, snapshot, updated_at FROM device_schedules ORDER BY device_id";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var deviceId = reader.GetString(0);
            if (!activeDevice(deviceId))
                continue;
            try
            {
                if (JToken.Parse(reader.GetString(1)) is JObject snapshot)
                    list.Add(new DeviceSchedulesView(deviceId, reader.GetInt64(2), snapshot));
            }
            catch (JsonException)
            {
                // 壞掉的列略過（下次那台上傳就取代）
            }
        }
        return list;
    }
}
