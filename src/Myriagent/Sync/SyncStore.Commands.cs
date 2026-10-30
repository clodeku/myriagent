using System.Globalization;
using System.Text;
using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Security;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 寫進遠端操作紀錄（<c>remote_audit</c>）的一列事實（WP4.4 計畫 D12），由引擎從信封與 verdict 組出：
/// <paramref name="BodyMasked"/> 是 <see cref="SyncCommandRules.MaskBodyForAudit"/> 的結果（代理輸入與回答不記原文），
/// <paramref name="SignedSha256"/>、<paramref name="Sig"/> 是 <see cref="SyncCommandRules.SignedSha256"/>、<see cref="SyncCommandRules.ClipSig"/> 的結果。
/// 沒解析過的信封只有外層的 <paramref name="CommandId"/>（可能不是 ULID）與簽章欄位
/// </summary>
/// <param name="DeclaredTier">信封宣告的層級（伺服器算的）</param>
/// <param name="Detail">只進紀錄的細節（驗信封失敗的原因、aud 不符時兩邊的值；不含指令的內容）</param>
internal sealed record CommandAuditFacts(
    string? CommandId,
    string? Cmd,
    string? ConversationId,
    int? DeclaredTier,
    bool? DefaultPassword,
    long? Iat,
    long? Created,
    string? Detail,
    string? BodyMasked,
    string? SignedSha256,
    string? Sig);

/// <summary><see cref="SyncStore.BeginCommand"/> 的結果種類</summary>
internal enum CommandBeginKind
{
    /// <summary>沒見過：已在同一個交易裡記下 executing 與一列紀錄（<see cref="CommandBegin.AuditSeq"/>），可以執行</summary>
    New,

    /// <summary>同一個內容見過：那個指令由 BeginCommand 寫的紀錄 <c>count</c> 加一，<see cref="CommandBegin.Row"/> 是現在的列（引擎照 SyncCommandRules.Decide 回結果）</summary>
    Duplicate,

    /// <summary>同一個指令 id 但內容不同：原本的列不變，多一列 <c>step=seen</c>、<c>code=conflict</c> 的紀錄</summary>
    Conflict,
}

/// <summary>
/// <see cref="SyncStore.BeginCommand"/> 的結果。<see cref="AuditSeq"/>：New 是新寫的紀錄（完成時以它更新）、Duplicate 是 BeginCommand 當初寫的那一列
/// （找不到時是 null）、Conflict 是新寫的衝突紀錄。<see cref="Row"/>：Duplicate、Conflict 時是 <c>commands_seen</c> 原本的列
/// </summary>
internal sealed record CommandBegin(CommandBeginKind Kind, long? AuditSeq, CommandSeenRow? Row);

/// <summary>
/// sync.db v2 的遠端指令去重（<c>commands_seen</c>）與遠端操作紀錄（<c>remote_audit</c>）（WP4.4 計畫 D8、D9、D11、D12、D33；結構凍結在
/// <see cref="SyncStoreSchema.CreateV2"/>，這裡不改結構）。同步不變式「遠端指令至多執行一次（先記 executing 再執行）」的裝置端：
/// 新指令的 executing 與紀錄在同一個交易寫，提交（fsync）之後引擎才交給中樞。
/// 只在引擎執行緒使用；失敗一律丟 <see cref="SyncStoreException"/>（損毀是 <see cref="SyncStoreFailure.Corrupt"/>，
/// 其他是 <see cref="SyncStoreFailure.Unavailable"/>），交易整個回滾。紀錄的 <c>result</c> 欄保留原值（<c>executing</c> 也照實回）
/// </summary>
internal sealed partial class SyncStore
{
    /// <summary>LoadRemoteAudit 一頁最多幾列</summary>
    public const int MaxRemoteAuditPage = 200;

    /// <summary>
    /// 不是 BeginCommand 寫的紀錄的 step：驗信封失敗（<see cref="CommandEnvelopeSteps"/>）與去重衝突（<see cref="SyncCommandRules.SeenStep"/>）。
    /// 重送的 <c>count</c> 只加在 BeginCommand 寫的那一列，不加在這些列（計畫 D12）
    /// </summary>
    private static readonly string[] NotBeginSteps = [.. CommandEnvelopeSteps.Order, SyncCommandRules.SeenStep];

    /// <summary>找 BeginCommand 寫的紀錄的篩選（參數 $n0…）</summary>
    private static readonly string BeginRowFilter =
        " AND (step IS NULL OR step NOT IN (" + string.Join(", ", NotBeginSteps.Select((_, i) => "$n" + i.ToString(CultureInfo.InvariantCulture))) + "))";

    private bool commandTablesReady;

    /// <summary>
    /// <c>commands_seen</c> 與 <c>remote_audit</c> 都在（唯讀查詢；計畫 D6：宣告 cmd1 的條件之一）。是 true 才記下（之後不再查）；
    /// false 時每次都重查（開啟時升級失敗、之後補上的表也看得到）。查詢失敗記錄後回 false
    /// </summary>
    public bool CommandTablesReady
    {
        get
        {
            ThrowIfDisposed();
            if (commandTablesReady)
                return true;
            try
            {
                commandTablesReady = TableExists(connection, "commands_seen") && TableExists(connection, "remote_audit");
            }
            catch (SqliteException ex)
            {
                Log.Warn($"查不到遠端指令的表（SQLite {ex.SqliteErrorCode}），這次不宣告 cmd1：{ex.Message}");
                return false;
            }

            return commandTablesReady;
        }
    }

    /// <summary>
    /// 收到驗過信封的指令時（計畫 D8），在<b>單一交易</b>內查 <c>commands_seen</c>：
    /// <list type="bullet">
    /// <item>沒有：插入 <c>executing</c> 與一列紀錄（<c>result=executing</c>、<c>at</c> 相同），回 New。</item>
    /// <item>同摘要：BeginCommand 寫的那一列紀錄 <c>count+1</c>，回 Duplicate（引擎依 Row 與自己的進行中表、快取決定回什麼）。</item>
    /// <item>不同摘要：原本的列不變，插入一列 <c>result=rejected</c>、<c>step=seen</c>、<c>code=conflict</c> 的紀錄，回 Conflict。</item>
    /// </list>
    /// 紀錄的 <c>command_id</c> 一律是 <paramref name="commandId"/>（<paramref name="facts"/> 的 CommandId 不用）
    /// </summary>
    public CommandBegin BeginCommand(string commandId, string contentSha256, CommandAuditFacts facts, long now)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        ArgumentException.ThrowIfNullOrEmpty(contentSha256);
        ArgumentNullException.ThrowIfNull(facts);
        return Guard(nameof(BeginCommand), () =>
        {
            using var tx = connection.BeginTransaction();
            var row = ReadSeen(tx, commandId);
            CommandBegin begin;
            if (row is null)
            {
                using (var insert = Command(tx, """
                    INSERT INTO commands_seen (command_id, content_sha256, state, result_json, at) VALUES ($cid, $sha, $state, NULL, $at)
                    """))
                {
                    Bind(insert, ("$cid", commandId), ("$sha", contentSha256), ("$state", SyncCommandRules.ExecutingState), ("$at", now));
                    insert.ExecuteNonQuery();
                }

                Fault("BeginCommand:afterSeen");
                var seq = InsertAudit(tx, facts with { CommandId = commandId }, now, SyncCommandRules.ExecutingState, step: null, code: null);
                begin = new CommandBegin(CommandBeginKind.New, seq, null);
            }
            else if (string.Equals(row.ContentSha256, contentSha256, StringComparison.Ordinal))
            {
                var seq = FindBeginAudit(tx, commandId);
                if (seq is { } found)
                {
                    using var bump = Command(tx, "UPDATE remote_audit SET count = count + 1 WHERE seq = $seq");
                    Bind(bump, ("$seq", found));
                    bump.ExecuteNonQuery();
                }

                begin = new CommandBegin(CommandBeginKind.Duplicate, seq, row);
            }
            else
            {
                var seq = InsertAudit(tx, facts with { CommandId = commandId }, now, RemoteResultStates.Rejected, SyncCommandRules.SeenStep, RemoteResultCodes.Conflict);
                begin = new CommandBegin(CommandBeginKind.Conflict, seq, row);
            }

            Fault("BeginCommand:commit");
            tx.Commit();
            return begin;
        });
    }

    /// <summary>
    /// 指令有結果時（計畫 D9）：同一個交易裡更新 <c>commands_seen</c> 的 state 與 <c>result_json</c>（rejected＋busy 是刪掉那一列：
    /// 什麼都沒執行，durable 重簽要能再試），以及 <paramref name="auditSeq"/> 那一列紀錄的 result、code、step、tier、full_access、policy、paused
    /// （<paramref name="detail"/> 有值才寫）。<c>at</c> 不變：保留期從 BeginCommand 記下的時間算
    /// </summary>
    /// <param name="policy">當時這台的遠端控制政策（<c>RemoteControlLevel</c> 的名稱，例如 <c>Control</c>）</param>
    public void CompleteCommand(string commandId, long auditSeq, CommandResult result, string? step, string? policy, bool? paused, string? detail = null)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        ArgumentNullException.ThrowIfNull(result);
        Guard(nameof(CompleteCommand), () =>
        {
            using var tx = connection.BeginTransaction();
            if (SyncCommandRules.KeepsSeenRow(result))
            {
                using var update = Command(tx, "UPDATE commands_seen SET state = $state, result_json = $json WHERE command_id = $cid");
                Bind(update, ("$state", result.State), ("$json", SyncCommandRules.SerializeResult(result)), ("$cid", commandId));
                update.ExecuteNonQuery();
            }
            else
            {
                using var delete = Command(tx, "DELETE FROM commands_seen WHERE command_id = $cid");
                Bind(delete, ("$cid", commandId));
                delete.ExecuteNonQuery();
            }

            Fault("CompleteCommand:afterSeen");
            using (var audit = Command(tx, """
                UPDATE remote_audit SET result = $result, code = $code, step = $step, tier = $tier, full_access = $full, policy = $policy, paused = $paused,
                       detail = COALESCE($detail, detail)
                WHERE seq = $seq
                """))
            {
                Bind(audit, ("$result", result.State), ("$code", result.Code), ("$step", step), ("$tier", result.Tier), ("$full", Bit(result.FullAccess)),
                    ("$policy", policy), ("$paused", Bit(paused)), ("$detail", detail), ("$seq", auditSeq));
                audit.ExecuteNonQuery();
            }

            tx.Commit();
            return 0;
        });
    }

    /// <summary>
    /// 驗信封失敗（計畫 D7）：只寫一列紀錄（<c>result=rejected</c>，自己一個交易），<b>不寫</b> <c>commands_seen</c>——同一個 cid 之後合格的信封
    /// （例如 conn 不符之後重簽）照樣執行
    /// </summary>
    public void AuditRejected(CommandAuditFacts facts, string step, string code, long now)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrEmpty(step);
        ArgumentException.ThrowIfNullOrEmpty(code);
        Guard(nameof(AuditRejected), () => InsertAudit(null, facts, now, RemoteResultStates.Rejected, step, code));
    }

    /// <summary>
    /// 引擎實例第一次 Hello 之前（計畫 D11、D36）：上次執行中被中斷的指令（還是 <c>executing</c>）改成 <c>unknown</c>——<c>result_json</c> 寫成 unknown 的結果
    /// （重送回 unknown、不重做），紀錄的 <c>result=unknown</c>、<c>step=handler</c>。一個交易；回改了幾個指令
    /// </summary>
    public int MarkInterruptedUnknown()
    {
        ThrowIfDisposed();
        return Guard(nameof(MarkInterruptedUnknown), () =>
        {
            using var tx = connection.BeginTransaction();
            var executing = new List<string>();
            using (var select = Command(tx, "SELECT command_id FROM commands_seen WHERE state = $state ORDER BY command_id"))
            {
                Bind(select, ("$state", SyncCommandRules.ExecutingState));
                using var reader = select.ExecuteReader();
                while (reader.Read())
                    executing.Add(reader.GetString(0));
            }

            using (var update = Command(tx, "UPDATE commands_seen SET state = $state, result_json = $json WHERE command_id = $cid"))
            {
                foreach (var commandId in executing)
                {
                    update.Parameters.Clear();
                    Bind(update, ("$state", RemoteResultStates.Unknown), ("$json", SyncCommandRules.SerializeResult(SyncCommandRules.UnknownResult(commandId))),
                        ("$cid", commandId));
                    update.ExecuteNonQuery();
                }
            }

            // 紀錄裡還是 executing 的列一律改成 unknown（引擎還沒開始收指令，沒有真的在執行的）
            using (var audit = Command(tx, "UPDATE remote_audit SET result = $unknown, step = $step WHERE result = $executing"))
            {
                Bind(audit, ("$unknown", RemoteResultStates.Unknown), ("$step", RemoteGateSteps.Handler), ("$executing", SyncCommandRules.ExecutingState));
                audit.ExecuteNonQuery();
            }

            tx.Commit();
            return executing.Count;
        });
    }

    /// <summary>
    /// 修剪（計畫 D11；每次 Hello 時距上次 ≥ 24 小時才做，由引擎判斷）：<c>commands_seen</c> 保留 <see cref="SyncCommandRules.SeenRetentionMs"/>（8 天）、
    /// <c>remote_audit</c> 保留 <see cref="SyncCommandRules.AuditRetentionMs"/>（180 天），剛好到期的留著。一個交易；回各刪了幾列
    /// </summary>
    public (int Seen, int Audit) Prune(long now)
    {
        ThrowIfDisposed();
        return Guard(nameof(Prune), () =>
        {
            using var tx = connection.BeginTransaction();
            int seen;
            using (var delete = Command(tx, "DELETE FROM commands_seen WHERE at < $cut"))
            {
                Bind(delete, ("$cut", now - SyncCommandRules.SeenRetentionMs));
                seen = delete.ExecuteNonQuery();
            }

            int audit;
            using (var delete = Command(tx, "DELETE FROM remote_audit WHERE at < $cutAudit"))
            {
                Bind(delete, ("$cutAudit", now - SyncCommandRules.AuditRetentionMs));
                audit = delete.ExecuteNonQuery();
            }

            Fault("Prune:commit");
            tx.Commit();
            return (seen, audit);
        });
    }

    /// <summary>
    /// 遠端操作紀錄的一頁（WP4.6 的 <c>syncLoadRemoteAudit</c>；web-protocol <c>remoteAudit</c>）：<c>at</c> 由新到舊、同一個 <c>at</c> 依 seq 由大到小；
    /// <paramref name="beforeAt"/> 有值時只取更早的（WPR 的 <c>before</c> 是 <c>at</c>）；再帶 <paramref name="beforeSeq"/>（上一頁最後一列的 seq）時以 (at, seq) 翻頁，
    /// 同一個 <c>at</c> 的列不會被跳過（<see cref="SyncCommandRules.RemoteAuditPageWhere"/>）；<paramref name="limit"/> 夾到 1～<see cref="MaxRemoteAuditPage"/>
    /// </summary>
    public IReadOnlyList<RemoteAuditEntry> LoadRemoteAudit(long? beforeAt, int limit, long? beforeSeq = null)
    {
        ThrowIfDisposed();
        var take = Math.Clamp(limit, 1, MaxRemoteAuditPage);
        return Guard(nameof(LoadRemoteAudit), () =>
        {
            using var cmd = Command(null, $"""
                SELECT seq, at, command_id, cmd, conversation_id, body_masked, declared_tier, tier, full_access, policy, paused,
                       result, code, step, default_password, count
                FROM remote_audit
                {SyncCommandRules.RemoteAuditPageWhere(beforeAt, beforeSeq)}
                ORDER BY at DESC, seq DESC
                LIMIT $limit
                """);
            if (beforeAt is { } before)
            {
                Bind(cmd, ("$before", before));
                if (beforeSeq is { } seq)
                    Bind(cmd, ("$beforeSeq", seq));
            }
            Bind(cmd, ("$limit", take));
            using var reader = cmd.ExecuteReader();
            var entries = new List<RemoteAuditEntry>();
            while (reader.Read())
            {
                entries.Add(new RemoteAuditEntry(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    TextOrNull(reader, 2),
                    TextOrNull(reader, 3),
                    TextOrNull(reader, 4),
                    SyncCommandRules.IssuerLabelFromMasked(TextOrNull(reader, 5)),
                    IntOrNull(reader, 6),
                    IntOrNull(reader, 7),
                    BoolOrNull(reader, 8),
                    TextOrNull(reader, 9),
                    BoolOrNull(reader, 10),
                    reader.GetString(11),
                    TextOrNull(reader, 12),
                    TextOrNull(reader, 13),
                    BoolOrNull(reader, 14),
                    reader.GetInt32(15)));
            }

            return (IReadOnlyList<RemoteAuditEntry>)entries;
        });
    }

    /// <summary>
    /// 解除配對前匯出遠端操作紀錄（計畫 D33；sync.db 會被刪）：每列一個 JSON 物件（全部欄位、駝峰式鍵、依 seq 由小到大）、UTF-8 無 BOM、LF。
    /// 還是 <c>executing</c> 的列寫成 <c>unknown</c>、step <c>handler</c>（同 <see cref="MarkInterruptedUnknown"/>）：匯出就是最後的版本，之後完成的結果
    /// 只寫進隨後被刪掉的 sync.db（合併前審查 AMO-07）。先寫 <c>path + ".tmp"</c> 再改名（覆寫既有的檔）；失敗時刪掉暫存檔。回寫了幾列
    /// </summary>
    public int ExportRemoteAudit(string path)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(path);
        var temp = path + ".tmp";
        return Guard(nameof(ExportRemoteAudit), () =>
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            var count = 0;
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" })
                using (var cmd = Command(null, """
                    SELECT seq, at, command_id, cmd, conversation_id, declared_tier, tier, full_access, policy, paused, default_password,
                           iat, created, step, result, code, detail, count, body_masked, signed_sha256, sig
                    FROM remote_audit ORDER BY seq
                    """))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var interrupted = reader.GetString(14) == SyncCommandRules.ExecutingState;
                        var line = new JObject
                        {
                            ["seq"] = reader.GetInt64(0),
                            ["at"] = reader.GetInt64(1),
                            ["commandId"] = TextOrNull(reader, 2),
                            ["cmd"] = TextOrNull(reader, 3),
                            ["conversationId"] = TextOrNull(reader, 4),
                            ["declaredTier"] = IntOrNull(reader, 5),
                            ["tier"] = IntOrNull(reader, 6),
                            ["fullAccess"] = BoolOrNull(reader, 7),
                            ["policy"] = TextOrNull(reader, 8),
                            ["paused"] = BoolOrNull(reader, 9),
                            ["defaultPassword"] = BoolOrNull(reader, 10),
                            ["iat"] = LongOrNull(reader, 11),
                            ["created"] = LongOrNull(reader, 12),
                            ["step"] = interrupted ? RemoteGateSteps.Handler : TextOrNull(reader, 13),
                            ["result"] = interrupted ? RemoteResultStates.Unknown : reader.GetString(14),
                            ["code"] = TextOrNull(reader, 15),
                            ["detail"] = TextOrNull(reader, 16),
                            ["count"] = reader.GetInt32(17),
                            ["bodyMasked"] = TextOrNull(reader, 18),
                            ["signedSha256"] = TextOrNull(reader, 19),
                            ["sig"] = TextOrNull(reader, 20),
                        };
                        writer.WriteLine(line.ToString(Formatting.None));
                        count++;
                    }

                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            return count;
        });
    }

    // ── 內部 ──

    private CommandSeenRow? ReadSeen(SqliteTransaction tx, string commandId)
    {
        using var cmd = Command(tx, "SELECT content_sha256, state, result_json, at FROM commands_seen WHERE command_id = $cid");
        Bind(cmd, ("$cid", commandId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new CommandSeenRow(reader.GetString(0), reader.GetString(1), TextOrNull(reader, 2), reader.GetInt64(3)) : null;
    }

    /// <summary>BeginCommand 為這個指令寫的最後一列紀錄（不是驗信封失敗、不是去重衝突的列）；沒有回 null</summary>
    private long? FindBeginAudit(SqliteTransaction tx, string commandId)
    {
        using var cmd = Command(tx, "SELECT seq FROM remote_audit WHERE command_id = $cid" + BeginRowFilter + " ORDER BY seq DESC LIMIT 1");
        Bind(cmd, ("$cid", commandId));
        for (var i = 0; i < NotBeginSteps.Length; i++)
            Bind(cmd, ("$n" + i.ToString(CultureInfo.InvariantCulture), NotBeginSteps[i]));
        return cmd.ExecuteScalar() is long seq ? seq : null;
    }

    private long InsertAudit(SqliteTransaction? tx, CommandAuditFacts facts, long now, string result, string? step, string? code)
    {
        using var cmd = Command(tx, """
            INSERT INTO remote_audit (at, command_id, cmd, conversation_id, declared_tier, default_password, iat, created, step, result, code, detail,
                                      body_masked, signed_sha256, sig)
            VALUES ($at, $cid, $cmd, $conversation, $declared, $defaultPassword, $iat, $created, $step, $result, $code, $detail, $body, $signed, $sig)
            RETURNING seq
            """);
        Bind(cmd, ("$at", now), ("$cid", facts.CommandId), ("$cmd", facts.Cmd), ("$conversation", facts.ConversationId), ("$declared", facts.DeclaredTier),
            ("$defaultPassword", Bit(facts.DefaultPassword)), ("$iat", facts.Iat), ("$created", facts.Created), ("$step", step), ("$result", result),
            ("$code", code), ("$detail", facts.Detail), ("$body", facts.BodyMasked), ("$signed", facts.SignedSha256), ("$sig", SyncCommandRules.ClipSig(facts.Sig)));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>SQLite 與檔案的例外換成 <see cref="SyncStoreException"/>：損毀（11、26）是 Corrupt，其他是 Unavailable</summary>
    private static T Guard<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            throw new SyncStoreException(SyncStoreFailure.Corrupt, $"同步資料庫損毀（{operation}：SQLite {ex.SqliteErrorCode}）", ex);
        }
        catch (SqliteException ex)
        {
            throw new SyncStoreException(SyncStoreFailure.Unavailable, $"同步資料庫暫時無法寫入（{operation}：SQLite {ex.SqliteErrorCode}）：{ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncStoreException(SyncStoreFailure.Unavailable, $"同步資料庫的檔案無法寫入（{operation}）：{ex.Message}", ex);
        }
    }

    private static long? Bit(bool? value) => value is { } flag ? (flag ? 1 : 0) : null;

    private static string? TextOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? LongOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static int? IntOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static bool? BoolOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal) != 0;
}
