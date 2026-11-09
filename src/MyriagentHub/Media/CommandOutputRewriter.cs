using MyriagentHub.Data;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Media;

/// <summary>
/// <see cref="ICommandOutputRewriter"/> 的實作（WP4.7 計畫 D37）：規則在 <see cref="CommandOutputRewriteRules"/>，這裡查對話目錄、
/// 把新的參照寫進 <c>media_refs</c>（先以唯讀連線查哪些已經在，只對新的經寫入者 <c>INSERT OR IGNORE</c>：抽屜的參照電腦會重用 29 天，
/// 同一份清單再列一次不寫資料庫），寫完才把改寫後的訊息交回（瀏覽器一拿到網址就查得到列）。寫入失敗時丟掉這則（回 null）並記錯誤。
/// WP4.5 的 <c>CommandResults.ReceiveOutputAsync</c> 在裝置的 <c>CommandOutput</c> 投影之後、進 <c>ClientOutbox</c> 之前呼叫（WP4.7 計畫步 13 接上）
/// </summary>
internal sealed class CommandOutputRewriter(SyncWriter writer, IConversationDirectory directory, TimeProvider time, ILogger<CommandOutputRewriter> log)
    : ICommandOutputRewriter
{
    /// <summary>查「已經在」時一次帶幾個參照</summary>
    private const int LookupChunk = 500;

    private long writes;
    private long inserted;

    /// <summary>呼叫寫入者的次數（測試與探針：同一個參照第二次不再寫）</summary>
    public long Writes => Interlocked.Read(ref writes);

    /// <summary>實際新增的列數</summary>
    public long Inserted => Interlocked.Read(ref inserted);

    public async Task<JObject?> RewriteAsync(string deviceId, string commandName, string? conversationId, JObject message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        // 只有三種檔案指令的輸出要看擁有者（toast、accountLogin 等原樣回同一個物件，不查對話目錄）
        var fileOutput = message["type"] is JValue { Type: JTokenType.String, Value: CommandOutputRewriteRules.FilesType or CommandOutputRewriteRules.FileRefType or CommandOutputRewriteRules.FileSearchType or CommandOutputRewriteRules.FileRefsType };
        var owner = conversationId is null || !fileOutput ? null : directory.Find(conversationId);
        var (rewritten, rows) = CommandOutputRewriteRules.Rewrite(deviceId, commandName, conversationId, owner, message, time.GetUtcNow().ToUnixTimeMilliseconds());
        if (rewritten is null)
        {
            log.LogWarning("裝置的指令輸出丟掉（{Command}，裝置 {Device}）：對話的擁有者不是這台，或改寫後超過 {Max} 位元組",
                commandName, deviceId, CommandOutputRewriteRules.MaxMessageBytes);
            return null;
        }

        if (rows.Count == 0)
            return rewritten;
        try
        {
            var fresh = WithoutExisting(rows);
            if (fresh.Count > 0)
            {
                Interlocked.Increment(ref writes);
                var added = await writer.WriteAsync(c =>
                {
                    var n = 0;
                    foreach (var row in fresh)
                    {
                        n += c.Execute(
                            "INSERT OR IGNORE INTO media_refs(device_id, ref, conversation_id, purpose, thumb_blob, created_at, last_used_at) VALUES($d, $r, $c, $p, NULL, $at, $at)",
                            ("$d", row.DeviceId), ("$r", row.Ref), ("$c", row.ConversationId), ("$p", row.Purpose), ("$at", row.CreatedAt));
                    }

                    return n;
                }, ct);
                Interlocked.Add(ref inserted, added);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "指令輸出的媒體參照寫不進資料庫，這則丟掉（{Command}，裝置 {Device}）", commandName, deviceId);
            return null;
        }

        return rewritten;
    }

    /// <summary>還不在 <c>media_refs</c> 的列（同一則輸出的列都是同一台裝置的）</summary>
    private List<MediaRefRow> WithoutExisting(IReadOnlyList<MediaRefRow> rows)
    {
        var existing = new HashSet<(string, string)>();
        using var connection = writer.OpenRead();
        foreach (var chunk in rows.Chunk(LookupChunk))
        {
            using var cmd = connection.CreateCommand();
            var names = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                var name = "$r" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                names.Add(name);
                cmd.Parameters.AddWithValue(name, chunk[i].Ref);
            }

            cmd.Parameters.AddWithValue("$d", chunk[0].DeviceId);
            cmd.CommandText = "SELECT device_id, ref FROM media_refs WHERE device_id = $d AND ref IN (" + string.Join(", ", names) + ")";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                existing.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows.Where(r => !existing.Contains((r.DeviceId, r.Ref))).ToList();
    }
}
