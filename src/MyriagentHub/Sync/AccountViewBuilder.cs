using Myriagent.Agents;
using Myriagent.Sync;
using MyriagentHub.Data;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>瀏覽器快照的部分（各自整份取代、各自比對；設計 14.6）</summary>
[Flags]
internal enum BrowserParts
{
    None = 0,

    /// <summary><c>accounts</c>：AccountView 清單</summary>
    Accounts = 1,

    /// <summary><c>devices</c>（與 init 的 remote.devices）：DeviceView 清單</summary>
    Devices = 2,

    /// <summary><c>cli</c> 的 devicesCli（瀏覽器模式的 cli 一律是 {}）</summary>
    DevicesCli = 4,

    /// <summary><c>settings</c>：四個漫遊鍵</summary>
    Settings = 8,

    /// <summary><c>tokens</c>：各帳戶的 token 計數（tok1，各電腦的合計）</summary>
    Tokens = 16,

    /// <summary><c>schedules</c>：各電腦最後回報的排程（sched1）</summary>
    Schedules = 32,

    All = Accounts | Devices | DevicesCli | Settings | Tokens | Schedules,
}

/// <summary>一份快照：每個部分已序列化成 JSON（同一份字串給所有連線）；<c>TokensJson</c> 沒有計數來源時是 <c>null</c>（網頁不顯示 token 列）</summary>
internal sealed record BrowserSnapshotData(string AccountsJson, string DevicesJson, string DevicesCliJson, string SettingsJson, string TokensJson = "null",
    string SchedulesJson = "null");

/// <summary>瀏覽器快照的來源（測試以假的代替）</summary>
internal interface IBrowserSnapshotSource
{
    /// <summary>讀 <paramref name="parts"/> 指定的部分；沒要求的部分沿用 <paramref name="previous"/>（previous 是 null 時一律全讀）</summary>
    BrowserSnapshotData Read(BrowserParts parts, BrowserSnapshotData? previous, long nowMs);
}

/// <summary>
/// 從 sync.db 與裝置目錄組瀏覽器的 AccountView、DeviceView、devicesCli、Settings（逐欄白名單在 <see cref="AccountViewRules"/>）。
/// 一次讀取是一個短的 deferred 唯讀交易（<c>BeginTransaction()</c> 預設是 IMMEDIATE、會拿寫鎖；這裡不寫、不擋寫入者），
/// 所以每份快照都對應某個已提交的狀態。任何 JSON 欄位解析失敗時那一格給 null、整份照常產生；provider 不同步或不認得的列略過。
/// 只讀：這個類別沒有任何寫入。<paramref name="tokens"/> 是各帳戶的 token 計數（tok1）：null 時 <c>tokens</c> 一律是 <c>null</c>。
/// </summary>
internal sealed class AccountViewBuilder(SyncWriter writer, DeviceDirectory directory, IDevicePresence presence, UsageHistoryCache history,
    ILogger<AccountViewBuilder> log, TokenTallies? tokens = null, DeviceScheduleStore? schedules = null) : IBrowserSnapshotSource
{
    public BrowserSnapshotData Read(BrowserParts parts, BrowserSnapshotData? previous, long nowMs)
    {
        if (previous is null)
            parts = BrowserParts.All;
        using var connection = writer.OpenRead();
        using var transaction = connection.BeginTransaction(deferred: true);
        List<DeviceViewRow>? devices = null;
        List<DeviceViewRow> Devices() => devices ??= ReadDevices(connection, transaction);

        var accounts = parts.HasFlag(BrowserParts.Accounts) ? ReadAccounts(connection, transaction, Devices(), nowMs).ToString(Formatting.None) : previous!.AccountsJson;
        var deviceViews = parts.HasFlag(BrowserParts.Devices) ? AccountViewRules.Devices(Devices()).ToString(Formatting.None) : previous!.DevicesJson;
        var devicesCli = parts.HasFlag(BrowserParts.DevicesCli)
            ? AccountViewRules.DevicesCli(Devices(), ReadCli(connection, transaction)).ToString(Formatting.None)
            : previous!.DevicesCliJson;
        var settings = parts.HasFlag(BrowserParts.Settings) ? AccountViewRules.Settings(ReadPrefs(connection, transaction)).ToString(Formatting.None) : previous!.SettingsJson;
        var tokenView = parts.HasFlag(BrowserParts.Tokens) ? ReadTokens(connection, transaction, nowMs) : previous!.TokensJson;
        var scheduleView = parts.HasFlag(BrowserParts.Schedules) ? ReadSchedules(connection, transaction) : previous!.SchedulesJson;
        return new BrowserSnapshotData(accounts, deviceViews, devicesCli, settings, tokenView, scheduleView);
    }

    /// <summary>各電腦最後回報的排程（略過已撤銷的裝置）；沒有來源時是 null（網頁不顯示排程頁）</summary>
    private string ReadSchedules(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (schedules is null)
            return "null";
        var rows = DeviceScheduleStore.Read(connection, transaction, deviceId => directory.Get(deviceId) is { Revoked: false });
        return ScheduleSyncRules.BrowserView(rows).ToString(Formatting.None);
    }

    /// <summary>各帳戶的 token 計數：各台的格加總（今天以伺服器的本機日期算；略過已撤銷的裝置與已刪除的帳戶）</summary>
    private string ReadTokens(SqliteConnection connection, SqliteTransaction transaction, long nowMs)
    {
        if (tokens is null)
            return "null";
        var today = tokens.Today(nowMs);
        var rows = TokenTallies.ReadRows(connection, transaction, today, deviceId => directory.Get(deviceId) is { Revoked: false });
        return TokenTallySyncRules.View(rows, today).ToString(Formatting.None);
    }

    /// <summary>裝置目錄（hub.db 的不變資料）＋device_streams（顯示名稱、版本、上線時間、政策）＋目前在不在線</summary>
    private List<DeviceViewRow> ReadDevices(SqliteConnection connection, SqliteTransaction transaction)
    {
        var streams = new Dictionary<string, (string? Name, string? AppVersion, long? Hello, long? Heartbeat, DevicePolicy? Policy)>(StringComparer.Ordinal);
        using (var cmd = Command(connection, transaction, "SELECT device_id, name, app_version, last_hello_at, last_heartbeat_at, policy_json FROM device_streams"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                streams[reader.GetString(0)] = (Text(reader, 1), Text(reader, 2), Long(reader, 3), Long(reader, 4),
                    Text(reader, 5) is { } policy ? Parse<DevicePolicy>(policy, "device_streams.policy_json") : null);
            }
        }
        return directory.Listed
            .Select(info =>
            {
                streams.TryGetValue(info.Id, out var s);
                return new DeviceViewRow(info, s.Name, s.AppVersion, s.Hello, s.Heartbeat, s.Policy, presence.IsOnline(info.Id));
            })
            .ToList();
    }

    private JArray ReadAccounts(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<DeviceViewRow> devices, long nowMs)
    {
        var accounts = new Dictionary<string, AccountViewRow>(StringComparer.Ordinal);
        using (var cmd = Command(connection, transaction,
            "SELECT logical_account_id, provider, label, color, sort_order, expected_email, plan_hint, created_at FROM logical_accounts WHERE deleted_at IS NULL"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!IsSynced(reader.GetString(1)))
                    continue;
                var row = new AccountViewRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
                    Text(reader, 5), Text(reader, 6), reader.GetInt64(7));
                accounts[row.Id] = row;
            }
        }

        var links = new Dictionary<string, List<LinkViewRow>>(StringComparer.Ordinal);
        using (var cmd = Command(connection, transaction,
            "SELECT logical_account_id, device_id, status, status_text, email, plan, access_expires_at, login_expires_at, login_busy, identity_ok, sessions_json, checked_at "
            + "FROM account_links ORDER BY logical_account_id, device_id"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var link = new LinkViewRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), Text(reader, 3), Text(reader, 4), Text(reader, 5),
                    Long(reader, 6), Long(reader, 7), reader.GetInt64(8) != 0, reader.GetInt64(9) != 0,
                    Text(reader, 10) is { } sessions ? Parse<LinkSessions>(sessions, "account_links.sessions_json") : null, Long(reader, 11));
                if (!links.TryGetValue(link.LogicalAccountId, out var list))
                    links[link.LogicalAccountId] = list = [];
                list.Add(link);
            }
        }

        var usage = new Dictionary<string, SharedUsage>(StringComparer.Ordinal);
        using (var cmd = Command(connection, transaction, "SELECT logical_account_id, snapshot_json FROM usage_latest"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (Parse<SharedUsage>(reader.GetString(1), "usage_latest.snapshot_json") is { } snapshot)
                    usage[reader.GetString(0)] = snapshot;
            }
        }

        var pollers = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = Command(connection, transaction, "SELECT logical_account_id, device_id FROM usage_pollers"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                pollers[reader.GetString(0)] = reader.GetString(1);
        }

        // 走勢只讀目前顯示的帳戶與時間窗（usage_latest 的 windows）
        var windows = usage
            .Where(u => accounts.ContainsKey(u.Key))
            .ToDictionary(
                u => u.Key,
                u => (IReadOnlyList<string>)(u.Value.Windows ?? []).Where(w => w?.Key is not null).Select(w => w.Key).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        history.Refresh(connection, transaction, windows, nowMs);

        var ordered = AccountOrderRules.Sort(accounts.Values.Select(a => new AccountOrder(a.Id, a.SortOrder, a.CreatedAt)));
        return new JArray(ordered.Select(order =>
        {
            var account = accounts[order.LogicalAccountId];
            return AccountViewRules.Account(
                account,
                links.TryGetValue(account.Id, out var mine) ? mine : [],
                usage.GetValueOrDefault(account.Id),
                history.Get(account.Id),
                pollers.GetValueOrDefault(account.Id),
                devices);
        }));
    }

    private List<(string DeviceId, AgentProvider Provider, DeviceCliView View)> ReadCli(SqliteConnection connection, SqliteTransaction transaction)
    {
        var list = new List<(string, AgentProvider, DeviceCliView)>();
        using var cmd = Command(connection, transaction, "SELECT device_id, provider, state_json FROM device_cli ORDER BY device_id, provider");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!TryProvider(reader.GetString(1), out var provider) || !AccountViewRules.IsSyncedProvider(provider))
                continue;
            if (Parse<DeviceCliView>(reader.GetString(2), "device_cli.state_json") is { } view)
                list.Add((reader.GetString(0), provider, view));
        }
        return list;
    }

    private Dictionary<string, JToken> ReadPrefs(SqliteConnection connection, SqliteTransaction transaction)
    {
        var prefs = new Dictionary<string, JToken>(StringComparer.Ordinal);
        using var cmd = Command(connection, transaction, "SELECT key, value_json FROM prefs");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (ParseToken(reader.GetString(1), "prefs.value_json") is { } value)
                prefs[reader.GetString(0)] = value;
        }
        return prefs;
    }

    /// <summary>目錄的 provider 字串是同步的代理種類（Claude、Codex；名稱必須完全相同，數字字串不算）</summary>
    private static bool IsSynced(string provider) => TryProvider(provider, out var parsed) && AccountViewRules.IsSyncedProvider(parsed);

    private static bool TryProvider(string text, out AgentProvider provider) =>
        Enum.TryParse(text, ignoreCase: false, out provider) && Enum.IsDefined(provider) && provider.ToString() == text;

    private T? Parse<T>(string json, string column) where T : class
    {
        try
        {
            return SyncJson.Deserialize<T>(json);
        }
        catch (JsonException ex)
        {
            log.LogWarning("sync.db 的 {Column} 解析失敗，那一格給 null：{Message}", column, ex.Message);
            return null;
        }
    }

    private JToken? ParseToken(string json, string column)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = 16 };
            var token = JToken.ReadFrom(reader);
            return reader.Read() ? null : token;
        }
        catch (JsonException ex)
        {
            log.LogWarning("sync.db 的 {Column} 解析失敗，略過：{Message}", column, ex.Message);
            return null;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return cmd;
    }

    private static string? Text(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

    private static long? Long(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
}
