using Microsoft.Data.Sqlite;

namespace Myriagent.Tests.TestSupport;

/// <summary>伺服器 sync.db 的一筆事件（作者、deviceSeq、種類、payload 的 SHA-256）</summary>
internal sealed record HubEvent(long Seq, string Author, long? DeviceSeq, string Kind, string Cls, byte[]? PayloadSha256);

/// <summary>hub.db 的一筆稽核</summary>
internal sealed record HubAudit(string ActorType, string? ActorId, string Action, string? Target, string Outcome, string? Detail);

/// <summary>
/// 直接讀寫 MyriagentHub 子程序的資料庫（<c>db\sync.db</c>、<c>db\hub.db</c>）。連線一律 <c>Pooling=False</c>、每次查詢用完就關；
/// 寫入只在伺服器停止時用。連線字串的產生點在測試組件（第 5 步的「單一產生點」只掃 Myriagent.dll）
/// </summary>
internal sealed class HubDb(string dataDir)
{
    public string SyncDb => Path.Combine(dataDir, "db", "sync.db");

    public string HubDbFile => Path.Combine(dataDir, "db", "hub.db");

    public static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public static SqliteConnection OpenWritable(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>某台裝置寫的事件（依伺服器 seq）</summary>
    public IReadOnlyList<HubEvent> DeviceEvents(string deviceId)
    {
        using var connection = OpenReadOnly(SyncDb);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT seq, author, device_seq, kind, cls, payload_sha256 FROM events WHERE author = $a ORDER BY seq";
        cmd.Parameters.AddWithValue("$a", deviceId);
        using var reader = cmd.ExecuteReader();
        var result = new List<HubEvent>();
        while (reader.Read())
        {
            result.Add(new HubEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5)));
        }

        return result;
    }

    /// <summary>某台裝置被擋下的上行事件數</summary>
    public long Rejected(string deviceId)
    {
        using var connection = OpenReadOnly(SyncDb);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rejected_events WHERE author = $a";
        cmd.Parameters.AddWithValue("$a", deviceId);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>某個動作的稽核（依稽核 seq）</summary>
    public IReadOnlyList<HubAudit> Audit(string action)
    {
        using var connection = OpenReadOnly(HubDbFile);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT actor_type, actor_id, action, target, outcome, detail FROM audit WHERE action = $a ORDER BY seq";
        cmd.Parameters.AddWithValue("$a", action);
        using var reader = cmd.ExecuteReader();
        var result = new List<HubAudit>();
        while (reader.Read())
        {
            result.Add(new HubAudit(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return result;
    }

    /// <summary>未刪除的邏輯帳戶 id（依 identity_key：<c>codex:{email}</c>、<c>claude:{orgId}:{email}</c>）；沒有就是 null</summary>
    public string? LogicalAccountId(string identityKey)
    {
        using var connection = OpenReadOnly(SyncDb);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT logical_account_id FROM logical_accounts WHERE identity_key = $k AND deleted_at IS NULL";
        cmd.Parameters.AddWithValue("$k", identityKey);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>連結到這個邏輯帳戶、狀態 ready 而且身分相符的裝置</summary>
    public IReadOnlyList<string> ReadyLinks(string logicalAccountId)
    {
        using var connection = OpenReadOnly(SyncDb);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT device_id FROM account_links WHERE logical_account_id = $id AND status = 'ready' AND identity_ok = 1 ORDER BY device_id";
        cmd.Parameters.AddWithValue("$id", logicalAccountId);
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>這台裝置連到這個邏輯帳戶的那一格的 identity_ok（沒有連結為 null）</summary>
    public bool? LinkIdentityOk(string logicalAccountId, string deviceId)
    {
        using var connection = OpenReadOnly(SyncDb);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT identity_ok FROM account_links WHERE logical_account_id = $id AND device_id = $d";
        cmd.Parameters.AddWithValue("$id", logicalAccountId);
        cmd.Parameters.AddWithValue("$d", deviceId);
        return cmd.ExecuteScalar() is { } value and not DBNull ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0 : null;
    }

    /// <summary>邏輯帳戶的列數（含已刪除的：「在這台登入」不該另建）</summary>
    public long LogicalAccountCount()
    {
        using var connection = OpenReadOnly(SyncDb);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM logical_accounts";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>伺服器採用的用量（usage.snapshot 事件，依 seq）：來源裝置、term（null＝對話中的 rate limit）、來源</summary>
    public IReadOnlyList<(long Seq, string SourceDeviceId, long? Term, string Source)> UsageSnapshots(string logicalAccountId)
    {
        using var connection = OpenReadOnly(SyncDb);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT seq, payload FROM events WHERE kind = 'usage.snapshot' ORDER BY seq";
        using var reader = cmd.ExecuteReader();
        var result = new List<(long, string, long?, string)>();
        while (reader.Read())
        {
            var payload = Newtonsoft.Json.Linq.JObject.Parse(reader.GetString(1));
            if (payload.Value<string>("logicalAccountId") != logicalAccountId)
                continue;
            result.Add((reader.GetInt64(0), payload.Value<string>("sourceDeviceId") ?? string.Empty, payload.Value<long?>("term"),
                (string?)payload.SelectToken("usage.source") ?? string.Empty));
        }

        return result;
    }

    /// <summary>server_meta 的值（restore_generation、instance_id）</summary>
    public string? ServerMeta(string key)
    {
        using var connection = OpenReadOnly(SyncDb);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM server_meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar()?.ToString();
    }

    /// <summary>某個類別（events.cls）目前最大的 seq（沒有事件為 0）</summary>
    public long MaxSeq(string cls)
    {
        using var connection = OpenReadOnly(SyncDb);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM events WHERE cls = $c";
        cmd.Parameters.AddWithValue("$c", cls);
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>改 devices.token_issued_at（伺服器停止時；下一次 Prove 依它判斷要不要輪換權杖）</summary>
    public void SetTokenIssuedAt(string deviceId, long unixMs)
    {
        using var connection = OpenWritable(HubDbFile);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE devices SET token_issued_at = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$t", unixMs);
        cmd.Parameters.AddWithValue("$id", deviceId);
        cmd.ExecuteNonQuery().Should().Be(1, "要改的裝置在 devices 裡");
    }

    /// <summary>直接把裝置標成撤銷（伺服器停止時；正式流程要經擁有者的撤銷）</summary>
    public void Revoke(string deviceId, long unixMs)
    {
        using var connection = OpenWritable(HubDbFile);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE devices SET revoked_at = $t, revoked_reason = 'test' WHERE id = $id";
        cmd.Parameters.AddWithValue("$t", unixMs);
        cmd.Parameters.AddWithValue("$id", deviceId);
        cmd.ExecuteNonQuery().Should().Be(1, "要撤銷的裝置在 devices 裡");
    }
}
