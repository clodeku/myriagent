using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>以來源帳戶的短命 app-server 查歷史。只列資料、讀取回合與項目，不啟動或接續對話。</summary>
internal static class CodexThreadHistory
{
    private static Task<CodexAppServerClient> Connect(AgentLauncher launcher, string directory)
        => CodexAppServerClient.StartAsync(launcher, env =>
        {
            AccountRules.ApplyToolEnvironment(env);
            env["CODEX_HOME"] = directory;
        }, CancellationToken.None);

    private static async Task<JObject> Read(CodexAppServerClient client, string method, JObject parameters)
    {
        var (result, error) = await client.RequestAsync(method, parameters, TimeSpan.FromSeconds(20));
        return result ?? throw new InvalidOperationException(error ?? "Codex 沒有回傳歷史資料");
    }

    private static IEnumerable<JObject> Rows(JObject page)
        => (page["data"] as JArray ?? throw new InvalidDataException("Codex 歷史清單格式無法辨識")).OfType<JObject>();

    private static string Title(JObject thread) => ExternalConversationRules.MakeImportTitle(
        thread.Value<string>("name") ?? thread.Value<string>("agentNickname") ?? thread.Value<string>("preview"));

    private static DateTime Time(JObject row, string field) => DateTimeOffset.FromUnixTimeSeconds(row.Value<long>(field)).LocalDateTime;

    public static async Task<List<ExternalConversationCandidate>?> ListAsync(AgentLauncher launcher, ExternalAccountScanTarget account, int limit)
    {
        if (!Directory.Exists(account.ConfigDirectory)) return [];
        try
        {
            await using var client = await Connect(launcher, account.ConfigDirectory);
            var candidates = new List<ExternalConversationCandidate>();
            var query = new JObject { ["sortKey"] = "updated_at", ["sourceKinds"] = new JArray("cli", "vscode", "exec", "appServer", "unknown") };
            var cursors = new HashSet<string>();
            do
            {
                query["limit"] = Math.Min(50, limit - candidates.Count);
                var page = await Read(client, "thread/list", query);
                foreach (var thread in Rows(page))
                {
                    if (thread.Value<string>("historyMode") == null) return null;
                    if (!Guid.TryParseExact(thread.Value<string>("id"), "D", out var id) || thread.Value<string>("parentThreadId") != null) continue;
                    var key = ExternalConversationRules.ComputeSourceKey(AgentProvider.Codex, account.AccountId, id.ToString());
                    candidates.Add(new(key, AgentProvider.Codex, account.AccountId, account.AccountLabel, id.ToString(),
                        Title(thread), thread.Value<string>("cwd"), Time(thread, "updatedAt"), 0, "", account.ConfigDirectory));
                }
                if (page.Value<string>("nextCursor") is not { Length: > 0 } next || !cursors.Add(next)) break;
                query["cursor"] = next;
            } while (candidates.Count < limit);
            // 舊逐字稿可能尚未進入 CLI 的索引，保留原本的掃描入口。
            return candidates.Count == 0 && Directory.Exists(Path.Combine(account.ConfigDirectory, "sessions")) ? null : candidates;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"Codex 歷史清單改用本機逐字稿：{ex.Message}");
            return null;
        }
    }

    private static async Task<JObject> Metadata(CodexAppServerClient client, string threadId, bool turns = false)
    {
        var response = await Read(client, "thread/read", new JObject { ["threadId"] = threadId, ["includeTurns"] = turns });
        if (response["thread"] is JObject thread && string.Equals(thread.Value<string>("id"), threadId, StringComparison.OrdinalIgnoreCase)) return thread;
        throw new InvalidDataException("找不到這個帳戶的對話");
    }

    public static async Task<ExternalConversationImportData?> ImportAsync(AgentLauncher launcher, ExternalConversationCandidate candidate)
    {
        await using var client = await Connect(launcher, candidate.CodexHome!);
        var thread = await Metadata(client, candidate.SessionId);
        var created = Time(thread, "createdAt");
        var items = new List<AgentChatItem>();
        var query = new JObject { ["threadId"] = candidate.SessionId, ["limit"] = 50, ["sortDirection"] = "asc" };
        var cursors = new HashSet<string>();
        // 同逐字稿匯入保留頭尾。先讀到的提問留下，長對話把中段讓給最近的內容。
        var tail = new Queue<(AgentChatItem Item, int Size)>();
        long headSize = 0, tailSize = 0;
        int omitted = 0;
        do
        {
            var page = await HistoryPage(client, thread, query, turns: false);
            foreach (var entry in Rows(page))
                if (entry["item"] is JObject row && ToItem(row, created) is { } item)
                {
                    int size = item.Text.Length + (item.Detail?.Length ?? 0) + (item.Output?.Length ?? 0) + 128;
                    if (headSize < 512 * 1024)
                    {
                        headSize += size;
                        items.Add(item);
                    }
                    else
                    {
                        tail.Enqueue((item, size));
                        tailSize += size;
                        while (tailSize > 7_864_320 && tail.Count > 1)
                        {
                            tailSize -= tail.Dequeue().Size;
                            omitted++;
                        }
                    }
                }
            if (page.Value<string>("nextCursor") is not { Length: > 0 } cursor) break;
            if (!cursors.Add(cursor)) throw new InvalidDataException("Codex 傳回重複的分頁游標");
            query["cursor"] = cursor;
        } while (true);
        if (omitted > 0)
            items.Add(new AgentChatItem { Kind = AgentItemKind.Notice, Text = $"已略過中段 {omitted} 個歷史項目，完整紀錄可在 Codex 查看",
                CreatedAt = items[^1].CreatedAt, UpdatedAt = items[^1].UpdatedAt });
        items.AddRange(tail.Select(entry => entry.Item));
        return items.Count == 0 ? null : new(candidate.SessionId, Title(thread), thread.Value<string>("cwd"), created, Time(thread, "updatedAt"), items);
    }

    public static async Task<JObject> PreviewAsync(AgentLauncher launcher, string directory, string threadId, string? turnId, string? cursor)
    {
        await using var client = await Connect(launcher, directory);
        var thread = await Metadata(client, threadId);
        var turns = turnId == null;
        var query = new JObject { ["threadId"] = threadId, ["cursor"] = cursor, ["limit"] = 20, ["sortDirection"] = turns ? "desc" : "asc" };
        if (turns) query["itemsView"] = "summary";
        else query["turnId"] = turnId;
        var page = await HistoryPage(client, thread, query, turns);
        var rows = new JArray();
        foreach (var entry in Rows(page))
        {
            if (turns)
            {
                var first = (entry["items"] as JArray)?.OfType<JObject>().FirstOrDefault(x => x.Value<string>("type") == "userMessage");
                rows.Add(new JObject { ["id"] = entry["id"], ["time"] = entry["startedAt"], ["status"] = entry["status"],
                    ["title"] = first == null ? null : ExternalConversationRules.MakeImportTitle(UserText(first)) });
            }
            else if (entry["item"] is JObject row && ToItem(row, Time(thread, "createdAt")) is { } item)
                rows.Add(new JObject { ["kind"] = item.Kind.ToString(), ["title"] = item.Title, ["text"] = item.Text,
                    ["detail"] = item.Detail, ["output"] = item.Output, ["status"] = item.Status.ToString() });
        }
        return new JObject { ["title"] = Title(thread), ["rows"] = rows, ["nextCursor"] = page["nextCursor"] };
    }

    private static async Task<JObject> HistoryPage(CodexAppServerClient client, JObject thread, JObject query, bool turns)
    {
        // 舊格式由 CLI 還原其歷史，新格式只讀要求的那一頁。
        if (thread.Value<string>("historyMode") == "paginated")
            return await Read(client, turns ? "thread/turns/list" : "thread/items/list", query);
        if (thread["turns"] is not JArray { Count: > 0 })
            thread["turns"] = (await Metadata(client, query.Value<string>("threadId")!, turns: true))["turns"];
        var all = (thread["turns"] as JArray ?? []).OfType<JObject>();
        var turnId = query.Value<string>("turnId");
        IEnumerable<JObject> records = turns ? all.Reverse() : all.Where(t => turnId == null || t.Value<string>("id") == turnId)
            .SelectMany(t => (t["items"] as JArray ?? []).OfType<JObject>().Select(i => new JObject { ["item"] = i }));
        int offset = 0, limit = query.Value<int>("limit");
        if (query.Value<string>("cursor") is { } cursor && (!int.TryParse(cursor, out offset) || offset < 0))
            throw new InvalidDataException("歷史分頁位置無效");
        var slice = records.Skip(offset).Take(limit + 1).ToList();
        return new JObject { ["data"] = new JArray(slice.Take(limit)), ["nextCursor"] = slice.Count > limit ? (offset + limit).ToString() : null };
    }

    private static string UserText(JObject row)
        => string.Join("\n", (row["content"] as JArray ?? []).OfType<JObject>().Select(b => b.Value<string>("type") switch
        {
            "text" => ExternalConversationRules.CleanCodexUserBlock(b.Value<string>("text")),
            "skill" or "mention" => b.Value<string>("name"),
            "image" or "localImage" => "〔圖片〕",
            "audio" or "localAudio" => "〔音訊〕",
            _ => null,
        }).Where(s => !string.IsNullOrWhiteSpace(s)));

    internal static AgentChatItem? ToItem(JObject row, DateTime fallback)
    {
        var at = row.Value<long?>("startedAtMs") is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : fallback;
        var result = new AgentChatItem { CreatedAt = at, UpdatedAt = at };
        switch (row.Value<string>("type"))
        {
            case "userMessage": result.Kind = AgentItemKind.User; result.Text = UserText(row); break;
            case "agentMessage": case "plan": result.Kind = AgentItemKind.Assistant; result.Text = row.Value<string>("text") ?? ""; break;
            case "reasoning":
                result.Kind = AgentItemKind.Thinking;
                var reasoning = row["summary"] is JArray { Count: > 0 } summary ? summary : row["content"] as JArray;
                result.Text = string.Join("\n", (reasoning ?? []).Values<string>());
                break;
            default:
                result.Kind = AgentItemKind.Tool;
                result.ToolKind = "other";
                result.Title = row.Value<string>("tool") ?? row.Value<string>("name") ?? row.Value<string>("type");
                switch (row.Value<string>("type"))
                {
                    case "commandExecution":
                        result.ToolKind = "bash"; result.Title = CodexAppServerRules.DescribeCommand(row);
                        result.Detail = row.Value<string>("command"); result.Output = row.Value<string>("aggregatedOutput"); break;
                    case "fileChange":
                        result.ToolKind = "edit";
                        var change = CodexEventParser.DescribeFileChanges(row);
                        result.Title = change.Title; result.FilePath = change.Path; result.Diff = change.Diff?.ToList();
                        result.Detail = string.Join("\n", (row["changes"] as JArray ?? []).OfType<JObject>().Select(c => c.Value<string>("diff"))); break;
                    case "mcpToolCall": case "dynamicToolCall":
                        result.ToolKind = "mcp"; result.Detail = row["arguments"]?.ToString(Formatting.None);
                        result.Output = (row["error"] is JObject error ? error : row["result"] ?? row["contentItems"])?.ToString(Formatting.None); break;
                    case "subAgentActivity":
                        result.ToolKind = "agent"; result.Title = row.Value<string>("agentPath") ?? "子代理";
                        if (Guid.TryParseExact(row.Value<string>("agentThreadId"), "D", out var child)) result.Detail = $"codex://threads/{child}";
                        break;
                    case "collabAgentToolCall":
                        result.ToolKind = "agent"; result.Title = "子代理工作";
                        var links = (row["receiverThreadIds"] as JArray ?? []).Values<string>()
                            .Where(s => Guid.TryParseExact(s, "D", out _)).Select(s => $"codex://threads/{s}");
                        result.Detail = string.Join("\n", links.Prepend(row.Value<string>("prompt")).Where(s => !string.IsNullOrEmpty(s)));
                        break;
                    case "webSearch": result.ToolKind = "web"; result.Title = "網頁搜尋"; result.Detail = row.Value<string>("query"); break;
                    case "imageView": result.Title = "查看圖片"; result.Detail = row.Value<string>("path"); break;
                    case "imageGeneration": result.Title = "產生圖片"; break;
                    case "contextCompaction": result.Title = "壓縮對話"; break;
                    case "enteredReviewMode": case "exitedReviewMode": result.Title = "程式碼審查"; result.Detail = row.Value<string>("review"); break;
                    case "functionCallOutput":
                        result.Title = row.Value<string>("name") ?? "工具輸出";
                        result.Output = row["output"]?.Type == JTokenType.String ? row.Value<string>("output") : null;
                        break;
                    default: result.Title = $"歷史項目（{row.Value<string>("type")}）"; break;
                }
                result.ToolName = result.Title;
                result.Detail = AgentHubRules.TrimToolOutput(result.Detail);
                result.Output = AgentHubRules.TrimToolOutput(result.Output);
                result.Status = row.Value<string>("status") switch { "failed" => AgentItemStatus.Failed, "declined" => AgentItemStatus.Declined,
                    "inProgress" => AgentItemStatus.Running, "cancelled" => AgentItemStatus.Cancelled, _ => AgentItemStatus.Done };
                return result;
        }
        return string.IsNullOrWhiteSpace(result.Text) ? null : result;
    }
}
