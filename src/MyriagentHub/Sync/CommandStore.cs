using MyriagentHub.Data;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.Sync;

/// <summary>
/// sync.db <c>commands</c> 的一列（WP4.5 計畫 D15；<c>SyncSchema</c> 的 v2 結構，不改結構）。<see cref="CmdJson"/> 是遮蔽後的指令（D29）；
/// <see cref="UnsignedBody"/> 只有 durable 的才存（步 8）
/// </summary>
internal sealed record CommandRow(
    string CommandId,
    string? BrowserCommandId,
    string Issuer,
    string? ConversationId,
    string TargetDeviceId,
    long? TargetEpoch,
    string Cmd,
    string CmdJson,
    int Tier,
    bool FullAccess,
    string ContentSha256,
    bool Durable,
    string? UnsignedBody,
    string State,
    long CreatedAt,
    long? DeliveredAt,
    long ExpiresAt);

/// <summary>收到電腦的結果時，列的處置（D17）</summary>
internal enum ResultDisposition
{
    /// <summary>沒有這個 cid（不讓別台試出 cid：呼叫端靜靜略過）</summary>
    NotFound,

    /// <summary>cid 存在但不是回報的這台的</summary>
    OtherDevice,

    /// <summary>照 <see cref="CommandStateRules.OnResult"/> 套用了（<see cref="ResultApplication.Step"/>）</summary>
    Applied,
}

/// <summary><see cref="CommandStore.ApplyResult"/> 的結果；<see cref="Step"/> 只在 <see cref="ResultDisposition.Applied"/> 時有值</summary>
internal sealed record ResultApplication(ResultDisposition Disposition, CommandResultStep? Step = null, bool Durable = false);

/// <summary>一列還沒結束的 durable 指令（重簽用；D27）</summary>
internal sealed record DurableRow(string CommandId, string State, string? UnsignedBody, string ContentSha256, long CreatedAt, long ExpiresAt);

/// <summary>啟動掃描（D28）一批的結果：標成 unknown、expired 的與改回 pending 的列數</summary>
internal sealed record StartupScanCounts(int Unknown, int Expired, int Pending)
{
    public static StartupScanCounts Zero { get; } = new(0, 0, 0);

    public StartupScanCounts Add(StartupScanCounts other) => new(Unknown + other.Unknown, Expired + other.Expired, Pending + other.Pending);
}

/// <summary>
/// 遠端指令的 sync.db 讀寫（WP4.5 計畫 D5、D15、D17～D19）：寫入一律在 <see cref="SyncWriter"/> 的交易裡（<see cref="SyncWriteContext"/>），
/// 讀取用 <see cref="SyncWriter.OpenRead"/> 的唯讀連線。列的狀態轉移一律照 <see cref="CommandStateRules"/>。
/// 會掃表的維護（期限、durable 到期、啟動掃描、清理）一律先以唯讀連線找候選、限制筆數，再在寫入交易裡逐列以規則判斷一次（候選讀出之後變了的不動）
/// </summary>
internal sealed class CommandStore
{
    /// <summary>每輪期限掃描最多處理幾列（之後的下一輪再做）</summary>
    public const int MaxDeadlinesPerSweep = 200;

    public void Insert(SyncWriteContext context, CommandRow row)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(row);
        context.Execute(
            """
            INSERT INTO commands(command_id, browser_command_id, issuer, conversation_id, target_device_id, target_epoch, cmd, cmd_json,
              tier, full_access, content_sha256, durable, unsigned_body, state, result_json, created_at, delivered_at, done_at, expires_at)
            VALUES($id, $bcid, $issuer, $conv, $dev, $epoch, $cmd, $json, $tier, $full, $sha, $durable, $body, $state, NULL, $created, $delivered, NULL, $expires)
            """,
            ("$id", row.CommandId), ("$bcid", row.BrowserCommandId), ("$issuer", row.Issuer), ("$conv", row.ConversationId),
            ("$dev", row.TargetDeviceId), ("$epoch", row.TargetEpoch), ("$cmd", row.Cmd), ("$json", row.CmdJson), ("$tier", row.Tier),
            ("$full", row.FullAccess ? 1 : 0), ("$sha", row.ContentSha256), ("$durable", row.Durable ? 1 : 0), ("$body", row.UnsignedBody),
            ("$state", row.State), ("$created", row.CreatedAt), ("$delivered", row.DeliveredAt), ("$expires", row.ExpiresAt));
    }

    /// <summary>
    /// 一個邏輯帳戶存不存在（有列而且沒刪除）與它的連結（每台一列，依 device_id 排序）。連結的狀態原樣帶出：路由不看它（電腦自己判斷帳戶的狀態，D5）
    /// </summary>
    public (bool Exists, IReadOnlyList<AccountLinkFact> Links) ReadAccount(SqliteConnection connection, string logicalAccountId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logicalAccountId);
        bool exists;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT deleted_at IS NULL FROM logical_accounts WHERE logical_account_id = $id";
            cmd.Parameters.AddWithValue("$id", logicalAccountId);
            exists = cmd.ExecuteScalar() is long alive && alive == 1;
        }
        var links = new List<AccountLinkFact>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT device_id, local_account_id, status FROM account_links WHERE logical_account_id = $id ORDER BY device_id";
            cmd.Parameters.AddWithValue("$id", logicalAccountId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                links.Add(new AccountLinkFact(logicalAccountId, reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return (exists, links);
    }

    /// <summary>
    /// 套用電腦回報的結果（D17、D18；在寫入交易裡，時間是交易的 <see cref="SyncWriteContext.Now"/>）：核對 <c>target_device_id</c> 是回報的
    /// <paramref name="deviceId"/>，再照 <see cref="CommandStateRules.OnResult"/>：
    /// <list type="bullet">
    /// <item>最終狀態：寫 <c>state</c>、<c>done_at</c>、<c>result_json</c>，清掉 <c>unsigned_body</c>（<see cref="CommandStateRules.ClearsUnsignedBody"/>）。</item>
    /// <item>durable 的 busy／expired：回到 pending（<c>unsigned_body</c> 留著重簽）。</item>
    /// <item>遲到的（列已結束）：狀態不變，結果記進 <c>result_json.late</c>。</item>
    /// </list>
    /// </summary>
    public ResultApplication ApplyResult(SyncWriteContext context, string deviceId, Myriagent.Sync.CommandResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(result);
        string target;
        string state;
        bool durable;
        string? resultJson;
        using (var cmd = context.Command("SELECT target_device_id, state, durable, result_json FROM commands WHERE command_id = $id", ("$id", result.CommandId)))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read())
                return new ResultApplication(ResultDisposition.NotFound);
            target = reader.GetString(0);
            state = reader.GetString(1);
            durable = reader.GetInt64(2) != 0;
            resultJson = reader.IsDBNull(3) ? null : reader.GetString(3);
        }
        if (!string.Equals(target, deviceId, StringComparison.Ordinal))
            return new ResultApplication(ResultDisposition.OtherDevice);

        var step = CommandStateRules.OnResult(state, durable, result.State, result.Code);
        var now = context.Now;
        if (step.Late)
        {
            context.Execute("UPDATE commands SET result_json = $json WHERE command_id = $id",
                ("$json", CommandStateRules.WithLate(resultJson, result.State, result.Code, result.Message, result.Tier, result.FullAccess, now)), ("$id", result.CommandId));
        }
        else if (step.Resend)
        {
            context.Execute("UPDATE commands SET state = $state WHERE command_id = $id", ("$state", step.State), ("$id", result.CommandId));
        }
        else
        {
            Finish(context, result.CommandId, step.State,
                CommandStateRules.ResultJson(step.State, result.Code, result.Message, result.Tier, result.FullAccess, now), now);
        }
        return new ResultApplication(ResultDisposition.Applied, step, durable);
    }

    /// <summary>
    /// 結果期限到了的候選（D19；唯讀連線）：非 durable、還是 delivered、<c>expires_at</c> ≤ <paramref name="now"/>，依期限先後最多
    /// <see cref="MaxDeadlinesPerSweep"/> 筆。真正的轉移在 <see cref="ExpireDeadlines"/> 的交易裡再判斷一次
    /// </summary>
    public IReadOnlyList<string> DueDeadlines(SqliteConnection connection, long now)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = DueDeadlinesCommand(connection, now);
        return ReadIds(cmd);
    }

    /// <summary><see cref="DueDeadlines"/> 的查詢（每秒一次；走 <c>ix_cmd_state_expires</c>，測試以 EXPLAIN QUERY PLAN 確認不整表掃描）</summary>
    internal static SqliteCommand DueDeadlinesCommand(SqliteConnection connection, long now)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT command_id FROM commands WHERE durable = 0 AND state = $delivered AND expires_at <= $now ORDER BY expires_at, command_id LIMIT $limit";
        cmd.Parameters.AddWithValue("$delivered", CommandStates.Delivered);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$limit", MaxDeadlinesPerSweep);
        return cmd;
    }

    /// <summary>
    /// 把期限到了的列標成 unknown（D19；在寫入交易裡，時間是交易的 <see cref="SyncWriteContext.Now"/>）：每列照
    /// <see cref="CommandStateRules.OnDeadline"/> 再判斷一次（候選讀出之後收到結果的就不動）；回傳真的變了的 cid
    /// </summary>
    public IReadOnlyList<string> ExpireDeadlines(SyncWriteContext context, IReadOnlyList<string> commandIds)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commandIds);
        var changed = new List<string>();
        foreach (var commandId in commandIds)
        {
            string state;
            bool durable;
            long expiresAt;
            using (var cmd = context.Command("SELECT state, durable, expires_at FROM commands WHERE command_id = $id", ("$id", commandId)))
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read())
                    continue;
                state = reader.GetString(0);
                durable = reader.GetInt64(1) != 0;
                expiresAt = reader.GetInt64(2);
            }
            if (CommandStateRules.OnDeadline(state, durable, expiresAt, context.Now) is not { } next)
                continue;
            Finish(context, commandId, next, CommandStateRules.ResultJson(next, null, null, null, null, context.Now), context.Now);
            changed.Add(commandId);
        }
        return changed;
    }

    /// <summary>這台還沒結束（pending、delivered）的 durable 列（D27；唯讀連線），依建立先後</summary>
    public IReadOnlyList<DurableRow> OpenDurable(SqliteConnection connection, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(deviceId);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT command_id, state, unsigned_body, content_sha256, created_at, expires_at FROM commands
            WHERE target_device_id = $dev AND durable = 1 AND state IN ($pending, $delivered) ORDER BY created_at, command_id
            """;
        cmd.Parameters.AddWithValue("$dev", deviceId);
        cmd.Parameters.AddWithValue("$pending", CommandStates.Pending);
        cmd.Parameters.AddWithValue("$delivered", CommandStates.Delivered);
        using var reader = cmd.ExecuteReader();
        var rows = new List<DurableRow>();
        while (reader.Read())
        {
            rows.Add(new DurableRow(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetInt64(5)));
        }
        return rows;
    }

    /// <summary>各台 pending 的 durable 列數（D27 的重掃、D30 的 <c>pendingDurable</c>；唯讀連線），依裝置 id</summary>
    public IReadOnlyList<(string DeviceId, long Count)> PendingDurable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT target_device_id, COUNT(*) FROM commands WHERE durable = 1 AND state = $pending GROUP BY target_device_id ORDER BY target_device_id";
        cmd.Parameters.AddWithValue("$pending", CommandStates.Pending);
        using var reader = cmd.ExecuteReader();
        var list = new List<(string, long)>();
        while (reader.Read())
            list.Add((reader.GetString(0), reader.GetInt64(1)));
        return list;
    }

    /// <summary>
    /// 重簽之後、送出之前把 durable 列標成 delivered（D27；寫入交易）：列還沒結束、而且還沒到 7 天期限才標（候選讀出之後收到結果、到期的不送）；
    /// 回傳標了沒有
    /// </summary>
    public bool MarkDelivered(SyncWriteContext context, string commandId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commandId);
        string state;
        bool durable;
        long expiresAt;
        using (var cmd = context.Command("SELECT state, durable, expires_at FROM commands WHERE command_id = $id", ("$id", commandId)))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read())
                return false;
            state = reader.GetString(0);
            durable = reader.GetInt64(1) != 0;
            expiresAt = reader.GetInt64(2);
        }
        if (!durable || !CommandStates.IsOpen(state) || CommandStateRules.OnDurableExpiry(state, durable, expiresAt, context.Now) is not null)
            return false;
        context.Execute("UPDATE commands SET state = $state, delivered_at = $now WHERE command_id = $id",
            ("$state", CommandStates.Delivered), ("$now", context.Now), ("$id", commandId));
        return true;
    }

    /// <summary>durable 的 7 天期限到了的候選（D27；唯讀連線）：還沒結束、<c>expires_at</c> ≤ <paramref name="now"/>，最多 <see cref="MaxDeadlinesPerSweep"/> 筆</summary>
    public IReadOnlyList<string> DueDurableExpiries(SqliteConnection connection, long now)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = DueDurableExpiriesCommand(connection, now);
        return ReadIds(cmd);
    }

    /// <summary><see cref="DueDurableExpiries"/> 的查詢（每 60 秒一次；走 <c>ix_cmd_state_expires</c>）</summary>
    internal static SqliteCommand DueDurableExpiriesCommand(SqliteConnection connection, long now)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT command_id FROM commands WHERE durable = 1 AND state IN ($pending, $delivered) AND expires_at <= $now ORDER BY expires_at, command_id LIMIT $limit";
        cmd.Parameters.AddWithValue("$pending", CommandStates.Pending);
        cmd.Parameters.AddWithValue("$delivered", CommandStates.Delivered);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$limit", MaxDeadlinesPerSweep);
        return cmd;
    }

    /// <summary>
    /// 把 7 天期限到了的 durable 列標成 expired（D27；寫入交易）：每列照 <see cref="CommandStateRules.OnDurableExpiry"/> 再判斷一次，
    /// 清掉 <c>unsigned_body</c>（<see cref="Finish"/>）；回傳真的變了的 cid
    /// </summary>
    public IReadOnlyList<string> ExpireDurable(SyncWriteContext context, IReadOnlyList<string> commandIds)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commandIds);
        var changed = new List<string>();
        foreach (var commandId in commandIds)
        {
            if (ReadState(context, commandId) is not { } row
                || CommandStateRules.OnDurableExpiry(row.State, row.Durable, row.ExpiresAt, context.Now) is not { } next)
                continue;
            Finish(context, commandId, next, CommandStateRules.ResultJson(next, null, null, null, null, context.Now), context.Now);
            changed.Add(commandId);
        }
        return changed;
    }

    /// <summary>啟動掃描的候選（D28；唯讀連線）：還沒結束的列，依 cid 排在 <paramref name="after"/> 之後的最多 <paramref name="limit"/> 筆（改回 pending 的列還是沒結束，所以以 cid 往後翻頁）</summary>
    public IReadOnlyList<string> OpenCommandIds(SqliteConnection connection, string? after, int limit)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT command_id FROM commands WHERE state IN ($pending, $delivered) AND command_id > $after ORDER BY command_id LIMIT $limit";
        cmd.Parameters.AddWithValue("$pending", CommandStates.Pending);
        cmd.Parameters.AddWithValue("$delivered", CommandStates.Delivered);
        cmd.Parameters.AddWithValue("$after", after ?? "");
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadIds(cmd);
    }

    /// <summary>
    /// 啟動掃描（D28；寫入交易）：每列照 <see cref="CommandStateRules.OnStartup"/>——非 durable、還沒結束的變成 unknown（絕不重送），
    /// durable 到期的變成 expired（兩者都經 <see cref="Finish"/>，清掉 <c>unsigned_body</c>），durable 的 delivered 改回 pending（下次 Prove 重簽）
    /// </summary>
    public StartupScanCounts StartupScan(SyncWriteContext context, IReadOnlyList<string> commandIds)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commandIds);
        int unknown = 0, expired = 0, pending = 0;
        foreach (var commandId in commandIds)
        {
            if (ReadState(context, commandId) is not { } row || CommandStateRules.OnStartup(row.State, row.Durable, row.ExpiresAt, context.Now) is not { } next)
                continue;
            switch (next)
            {
                case CommandStates.Unknown:
                case CommandStates.Expired:
                    Finish(context, commandId, next, CommandStateRules.ResultJson(next, null, null, null, null, context.Now), context.Now);
                    if (next == CommandStates.Unknown)
                        unknown++;
                    else
                        expired++;
                    break;
                case CommandStates.Pending:
                    context.Execute("UPDATE commands SET state = $state WHERE command_id = $id", ("$state", next), ("$id", commandId));
                    pending++;
                    break;
                default:
                    throw new InvalidOperationException("啟動掃描不會轉到這個狀態：" + next);
            }
        }
        return new StartupScanCounts(unknown, expired, pending);
    }

    /// <summary>清理的候選（D28；唯讀連線）：結束滿 30 天（<c>done_at</c> ≤ <paramref name="now"/>－30 天），依結束先後最多 <paramref name="limit"/> 筆</summary>
    public IReadOnlyList<string> PruneCandidates(SqliteConnection connection, long now, int limit)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = PruneCandidatesCommand(connection, now, limit);
        return ReadIds(cmd);
    }

    /// <summary><see cref="PruneCandidates"/> 的查詢（走只索引結束的列的 <c>ix_cmd_done</c>）</summary>
    internal static SqliteCommand PruneCandidatesCommand(SqliteConnection connection, long now, int limit)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT command_id FROM commands WHERE done_at IS NOT NULL AND done_at <= $cutoff ORDER BY done_at, command_id LIMIT $limit";
        cmd.Parameters.AddWithValue("$cutoff", now - CommandStateRules.RetentionMs);
        cmd.Parameters.AddWithValue("$limit", limit);
        return cmd;
    }

    /// <summary>刪掉可以清的列（D28；寫入交易）：每列照 <see cref="CommandStateRules.Prunable"/> 再判斷一次（還沒結束的永遠不刪）；回傳刪了幾列</summary>
    public int Prune(SyncWriteContext context, IReadOnlyList<string> commandIds)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commandIds);
        var deleted = 0;
        foreach (var commandId in commandIds)
        {
            string state;
            long? doneAt;
            using (var cmd = context.Command("SELECT state, done_at FROM commands WHERE command_id = $id", ("$id", commandId)))
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read())
                    continue;
                state = reader.GetString(0);
                doneAt = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
            if (!CommandStateRules.Prunable(state, doneAt, context.Now))
                continue;
            deleted += context.Execute("DELETE FROM commands WHERE command_id = $id", ("$id", commandId));
        }
        return deleted;
    }

    private static (string State, bool Durable, long ExpiresAt)? ReadState(SyncWriteContext context, string commandId)
    {
        using var cmd = context.Command("SELECT state, durable, expires_at FROM commands WHERE command_id = $id", ("$id", commandId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt64(1) != 0, reader.GetInt64(2)) : null;
    }

    private static List<string> ReadIds(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>轉到最終狀態：<c>state</c>、<c>done_at</c>、<c>result_json</c>，<see cref="CommandStateRules.ClearsUnsignedBody"/> 時清掉 <c>unsigned_body</c></summary>
    private static void Finish(SyncWriteContext context, string commandId, string state, string resultJson, long now) =>
        context.Execute(
            "UPDATE commands SET state = $state, done_at = $now, result_json = $json, unsigned_body = CASE WHEN $clear = 1 THEN NULL ELSE unsigned_body END WHERE command_id = $id",
            ("$state", state), ("$now", now), ("$json", resultJson), ("$clear", CommandStateRules.ClearsUnsignedBody(state) ? 1 : 0), ("$id", commandId));
}
