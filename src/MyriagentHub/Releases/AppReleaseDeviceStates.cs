using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace MyriagentHub.Releases;

/// <summary>
/// 版本頁的一台電腦（<c>GET /api/v1/app/releases</c> 的 <c>devices</c>）：<see cref="Name"/> 是顯示名稱（device_streams.name，沒有時用配對時的名稱）；
/// <see cref="AppVersion"/> 是 Hello 回報、寫進 device_streams 的版本（還沒連過的用配對時的）；<see cref="UpdateOnly"/> 是在線而且全部連線都是只更新（U2 D4）；
/// <see cref="App"/> 是 app.state 的最新一份（device_app，U3 的桌面才送；沒有、解不開或不合格是 null）
/// </summary>
internal sealed record AppDeviceState(string DeviceId, string Name, string? AppVersion, long? LastHelloAt,
    bool Online, bool UpdateOnly, AppStatePayload? App);

/// <summary>
/// 版本頁的各電腦狀態：裝置目錄（略過已撤銷的，依配對順序）＋sync.db 的 device_streams（顯示名稱、app 版本、最後一次 Hello）與 device_app，加上目前的連線。
/// 一次讀取是一個短的 deferred 唯讀交易（寫法同 <see cref="AccountViewBuilder"/>），不擋寫入者；這個類別沒有任何寫入。
/// app 版本以 device_streams.app_version 為準：只更新的電腦不能送批次，app.state 的 version 可能落後
/// </summary>
internal sealed class AppReleaseDeviceStates(SyncWriter writer, DeviceDirectory directory, ConnectionRegistry connections, ILogger<AppReleaseDeviceStates> log)
{
    public IReadOnlyList<AppDeviceState> Read()
    {
        var streams = new Dictionary<string, (string? Name, string? AppVersion, long? LastHelloAt)>(StringComparer.Ordinal);
        var apps = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var connection = writer.OpenRead())
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            using (var cmd = Command(connection, transaction, "SELECT device_id, name, app_version, last_hello_at FROM device_streams"))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                    streams[reader.GetString(0)] = (Text(reader, 1), Text(reader, 2), reader.IsDBNull(3) ? null : reader.GetInt64(3));
            }
            using (var cmd = Command(connection, transaction, "SELECT device_id, state_json FROM device_app"))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                    apps[reader.GetString(0)] = reader.GetString(1);
            }
        }
        return directory.Listed
            .Where(info => !info.Revoked)
            .Select(info =>
            {
                streams.TryGetValue(info.Id, out var stream);
                return new AppDeviceState(
                    info.Id,
                    string.IsNullOrWhiteSpace(stream.Name) ? info.Name : stream.Name,
                    stream.AppVersion ?? info.AppVersion,
                    stream.LastHelloAt,
                    connections.IsOnline(info.Id),
                    connections.IsUpdateOnly(info.Id),
                    apps.TryGetValue(info.Id, out var json) ? ParseApp(info.Id, json) : null);
            })
            .ToList();
    }

    /// <summary>device_app 的內容（伺服器驗過、重新序列化的）：解不開或不合格（<see cref="CatalogRules.ValidateApp"/>）時記警告，那一台的 app 給 null</summary>
    private AppStatePayload? ParseApp(string deviceId, string json)
    {
        try
        {
            var payload = SyncJson.Deserialize<AppStatePayload>(json);
            if (CatalogRules.ValidateApp(payload) is not { } error)
                return payload;
            log.LogWarning("device_app 的內容不合格，版本頁不顯示這台的更新狀態：{DeviceId}：{Error}", deviceId, error);
        }
        catch (JsonException ex)
        {
            log.LogWarning("device_app 的內容解析失敗，版本頁不顯示這台的更新狀態：{DeviceId}：{Message}", deviceId, ex.Message);
        }
        return null;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return cmd;
    }

    private static string? Text(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
}
