using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>本機外部 CLI 歷史對話清單的一筆候選摘要（FilePath 只留在本機記憶體快取，不送給網頁）</summary>
internal sealed record ExternalConversationCandidate(
    string SourceKey,
    AgentProvider Provider,
    string AccountId,
    string AccountLabel,
    string SessionId,
    string Title,
    string? WorkingDirectory,
    DateTime UpdatedAt,
    int MessageCount,
    string FilePath);

/// <summary>本機外部 CLI 歷史對話完整解析結果（供建立 AgentConversation 與 items.json）</summary>
internal sealed record ExternalConversationImportData(
    string SessionId,
    string Title,
    string? WorkingDirectory,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<AgentChatItem> Items);

/// <summary>
/// 本機外部 CLI（Claude Code、Codex、Antigravity、Grok）歷史對話紀錄的純函式解析規則：
/// 不碰網路、不引用 SQLite，將各家 JSON／JSONL 逐字稿轉成預覽摘要與 <see cref="AgentChatItem"/> 清單
/// </summary>
internal static partial class ExternalConversationRules
{
    public const int MaxTitleLength = 60;

    /// <summary>計算來源識別鍵（由代理種類、帳戶 Id 與正規化路徑雜湊而成，避免網頁直接傳任意檔案路徑）</summary>
    public static string ComputeSourceKey(AgentProvider provider, string accountId, string filePath)
    {
        var raw = $"{provider}|{accountId}|{(filePath ?? string.Empty).Trim().Replace('/', '\\').ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash)[..32];
    }

    // ───────────────────────────── Claude Code（projects/*/*.jsonl） ─────────────────────────────

    /// <summary>解析 Claude Code 專案逐字稿（.jsonl）；子代理或無有效對話內容時回 null</summary>
    public static ExternalConversationImportData? ParseClaudeJsonl(string fallbackSessionId, IEnumerable<string> lines, DateTime fallbackTime)
    {
        string? sessionId = string.IsNullOrWhiteSpace(fallbackSessionId) ? null : fallbackSessionId.Trim();
        string? workingDir = null;
        string? explicitTitle = null;
        bool hasCustomTitle = false;
        DateTime? firstTime = null;
        DateTime? lastTime = null;
        var items = new List<AgentChatItem>();
        var toolsById = new Dictionary<string, AgentChatItem>(StringComparer.Ordinal);

        foreach (var rawLine in lines)
        {
            if (TryParseJsonObject(rawLine) is not { } obj)
                continue;

            if (obj.Value<bool?>("isSidechain") == true || obj.Value<bool?>("isMeta") == true)
                continue;

            if (TryParseTimestamp(obj["timestamp"], out var ts))
            {
                firstTime ??= ts;
                if (lastTime == null || ts > lastTime.Value)
                    lastTime = ts;
            }

            if (Str(obj["sessionId"]) is { Length: > 0 } sid)
                sessionId ??= sid;
            if (Str(obj["cwd"]) is { Length: > 0 } cwd && AgentAttachmentRules.IsLocalDrivePath(cwd))
                workingDir = cwd;

            var type = Str(obj["type"]);
            if (type == "custom-title" && Str(obj["customTitle"]) is { Length: > 0 } ct)
            {
                if (CleanTitle(ct) is { } cleanedCustom)
                {
                    explicitTitle = cleanedCustom;
                    hasCustomTitle = true;
                }
                continue;
            }
            if (type == "ai-title" && !hasCustomTitle && Str(obj["aiTitle"]) is { Length: > 0 } ait)
            {
                explicitTitle = CleanTitle(ait) ?? explicitTitle;
                continue;
            }
            if (type == "summary" && string.IsNullOrEmpty(explicitTitle) && Str(obj["summary"]) is { Length: > 0 } sum)
            {
                explicitTitle = CleanTitle(sum);
                continue;
            }

            if (obj["message"] is not JObject message)
                continue;

            var itemTime = ts != default ? ts : fallbackTime;
            if (type == "user")
            {
                var content = message["content"];
                if (content?.Type == JTokenType.String)
                {
                    var text = CleanClaudeUserText(content.Value<string>());
                    if (!string.IsNullOrWhiteSpace(text))
                        items.Add(NewItem(AgentItemKind.User, text, itemTime));
                }
                else if (content is JArray blocks)
                {
                    var textParts = new List<string>();
                    foreach (var block in blocks.OfType<JObject>())
                    {
                        var btype = Str(block["type"]);
                        if (btype == "tool_result")
                        {
                            var toolUseId = Str(block["tool_use_id"]);
                            if (!string.IsNullOrEmpty(toolUseId) && toolsById.TryGetValue(toolUseId, out var toolItem))
                            {
                                var outText = ExtractClaudeToolResultText(block["content"]);
                                if (!string.IsNullOrWhiteSpace(outText))
                                    toolItem.Output = AgentHubRules.TrimToolOutput(outText);
                                if (block.Value<bool?>("is_error") == true)
                                    toolItem.Status = AgentItemStatus.Failed;
                            }
                        }
                        else if (btype == "text")
                        {
                            var cleaned = CleanClaudeUserText(Str(block["text"]));
                            if (!string.IsNullOrWhiteSpace(cleaned))
                                textParts.Add(cleaned);
                        }
                    }
                    if (textParts.Count > 0)
                        items.Add(NewItem(AgentItemKind.User, string.Join("\n\n", textParts), itemTime));
                }
            }
            else if (type == "assistant" && message["content"] is JArray aBlocks)
            {
                foreach (var block in aBlocks.OfType<JObject>())
                {
                    var btype = Str(block["type"]);
                    if (btype == "thinking")
                    {
                        var thinking = Str(block["thinking"])?.Trim();
                        if (!string.IsNullOrEmpty(thinking))
                            items.Add(NewItem(AgentItemKind.Thinking, thinking, itemTime));
                    }
                    else if (btype == "text")
                    {
                        var text = Str(block["text"])?.Trim();
                        if (!string.IsNullOrEmpty(text))
                            items.Add(NewItem(AgentItemKind.Assistant, text, itemTime));
                    }
                    else if (btype == "tool_use")
                    {
                        var toolName = Str(block["name"]);
                        var input = block["input"] as JObject;
                        var desc = ClaudeStreamRules.DescribeTool(toolName, input);
                        var toolItem = new AgentChatItem
                        {
                            Kind = AgentItemKind.Tool,
                            Status = AgentItemStatus.Done,
                            ToolName = desc.DisplayName,
                            ToolKind = desc.Kind,
                            Title = desc.Title,
                            Detail = desc.Detail,
                            FilePath = desc.FilePath,
                            Diff = desc.Diff?.ToList(),
                            CreatedAt = itemTime,
                            UpdatedAt = itemTime,
                        };
                        items.Add(toolItem);
                        if (Str(block["id"]) is { Length: > 0 } id)
                            toolsById[id] = toolItem;
                    }
                }
            }
        }

        return FinalizeImportData(sessionId, explicitTitle, workingDir, firstTime, lastTime, fallbackTime, items);
    }

    private static string? CleanClaudeUserText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("<task-notification>", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<local-command-caveat>", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<local-command-stdout>", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<system-reminder>", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var cmdMatch = ClaudeCommandNameRegex().Match(trimmed);
        if (cmdMatch.Success)
        {
            var cmd = cmdMatch.Groups[1].Value.Trim();
            var argsMatch = ClaudeCommandArgsRegex().Match(trimmed);
            var args = argsMatch.Success ? argsMatch.Groups[1].Value.Trim() : string.Empty;
            if (cmd.Equals("/clear", StringComparison.OrdinalIgnoreCase))
                return null;
            return string.IsNullOrEmpty(args) ? cmd : $"{cmd} {args}";
        }
        return trimmed;
    }

    private static string? ExtractClaudeToolResultText(JToken? content)
    {
        if (content == null)
            return null;
        if (content.Type == JTokenType.String)
            return content.Value<string>();
        if (content is JArray arr)
        {
            var parts = arr.OfType<JObject>()
                .Where(o => Str(o["type"]) == "text")
                .Select(o => Str(o["text"]))
                .Where(s => !string.IsNullOrEmpty(s));
            return string.Join("\n", parts!);
        }
        return null;
    }

    // ───────────────────────────── Codex（sessions/**/rollout-*.jsonl） ─────────────────────────────

    /// <summary>從檔名 rollout-yyyy-MM-ddTHH-mm-ss-&lt;threadId&gt;.jsonl 擷取結尾的 UUID threadId；不符回 null</summary>
    public static string? ExtractCodexThreadIdFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (!name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
            return null;
        var match = TrailingUuidRegex().Match(name);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>解析 Codex session_index.jsonl 中的自訂／自動標題對照表（threadId -> thread_name）</summary>
    public static Dictionary<string, string> ParseCodexSessionIndexTitles(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in lines)
        {
            if (TryParseJsonObject(rawLine) is not { } obj)
                continue;
            var id = Str(obj["id"]);
            var name = CleanTitle(Str(obj["thread_name"]) ?? Str(obj["title"]));
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                map[id.Trim()] = name;
        }
        return map;
    }

    /// <summary>解析 Codex rollout（.jsonl）；子代理（source.subagent 或帶 parent_thread_id）或無有效訊息時回 null</summary>
    public static ExternalConversationImportData? ParseCodexRolloutJsonl(
        string? fallbackThreadId,
        IEnumerable<string> lines,
        DateTime fallbackTime,
        string? fallbackTitle = null)
    {
        string? threadId = string.IsNullOrWhiteSpace(fallbackThreadId) ? null : fallbackThreadId.Trim();
        string? workingDir = null;
        string? explicitTitle = CleanTitle(fallbackTitle);
        DateTime? firstTime = null;
        DateTime? lastTime = null;

        var items = new List<AgentChatItem>();
        var eventFallbackItems = new List<AgentChatItem>();
        var toolsByCallId = new Dictionary<string, AgentChatItem>(StringComparer.Ordinal);
        bool hasResponseMessages = false;

        foreach (var rawLine in lines)
        {
            if (TryParseJsonObject(rawLine) is not { } obj)
                continue;

            if (TryParseTimestamp(obj["timestamp"], out var ts))
            {
                firstTime ??= ts;
                if (lastTime == null || ts > lastTime.Value)
                    lastTime = ts;
            }
            var itemTime = ts != default ? ts : fallbackTime;

            var lineType = Str(obj["type"]);
            var payload = obj["payload"] as JObject;
            if (lineType == "session_meta" && payload != null)
            {
                // 排除子代理的 rollout
                if ((payload["source"] is JObject src && src["subagent"] != null)
                    || !string.IsNullOrWhiteSpace(Str(payload["parent_thread_id"]) ?? Str(payload["parentThreadId"])))
                {
                    return null;
                }
                if (Str(payload["id"]) is { Length: > 0 } id)
                    threadId = id;
                if (Str(payload["cwd"]) is { Length: > 0 } cwd && AgentAttachmentRules.IsLocalDrivePath(cwd))
                    workingDir ??= cwd;
                continue;
            }

            if (lineType == "event_msg" && payload != null)
            {
                var evType = Str(payload["type"]);
                if (evType == "thread_rolled_back")
                    continue;
                if (evType == "user_message" && Str(payload["message"]) is { Length: > 0 } uMsg)
                {
                    var cleaned = uMsg.Trim();
                    if (cleaned.Length > 0)
                        eventFallbackItems.Add(NewItem(AgentItemKind.User, cleaned, itemTime));
                }
                else if (evType == "agent_message" && Str(payload["message"]) is { Length: > 0 } aMsg)
                {
                    var cleaned = aMsg.Trim();
                    if (cleaned.Length > 0)
                        eventFallbackItems.Add(NewItem(AgentItemKind.Assistant, cleaned, itemTime));
                }
                continue;
            }

            if (lineType == "response_item" && payload != null)
            {
                var pType = Str(payload["type"]);
                if (pType == "message")
                {
                    var role = Str(payload["role"]);
                    if (role == "user" && payload["content"] is JArray uContent)
                    {
                        var parts = new List<string>();
                        foreach (var block in uContent.OfType<JObject>())
                        {
                            var bType = Str(block["type"]);
                            if (bType is "input_text" or "text")
                            {
                                var text = CleanCodexUserBlock(Str(block["text"]));
                                if (!string.IsNullOrWhiteSpace(text))
                                    parts.Add(text);
                            }
                        }
                        if (parts.Count > 0)
                        {
                            hasResponseMessages = true;
                            items.Add(NewItem(AgentItemKind.User, string.Join("\n\n", parts), itemTime));
                        }
                    }
                    else if (role == "assistant" && payload["content"] is JArray aContent)
                    {
                        var parts = new List<string>();
                        foreach (var block in aContent.OfType<JObject>())
                        {
                            var bType = Str(block["type"]);
                            if (bType is "output_text" or "text" && Str(block["text"]) is { Length: > 0 } t)
                                parts.Add(t.Trim());
                        }
                        var joined = string.Join("\n\n", parts.Where(p => p.Length > 0));
                        if (joined.Length > 0)
                        {
                            hasResponseMessages = true;
                            items.Add(NewItem(AgentItemKind.Assistant, joined, itemTime));
                        }
                    }
                }
                else if (pType == "reasoning" && payload["summary"] is JArray summaries)
                {
                    var parts = summaries.OfType<JObject>()
                        .Select(s => Str(s["text"]))
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!.Trim())
                        .ToList();
                    if (parts.Count > 0)
                        items.Add(NewItem(AgentItemKind.Thinking, string.Join("\n\n", parts), itemTime));
                }
                else if (pType is "function_call" or "custom_tool_call")
                {
                    var name = Str(payload["name"]) ?? "工具";
                    var rawArgs = Str(payload["arguments"]) ?? Str(payload["input"]) ?? string.Empty;
                    var (toolLabel, toolKind, title, detail) = DescribeCodexTool(name, rawArgs);
                    var toolItem = new AgentChatItem
                    {
                        Kind = AgentItemKind.Tool,
                        Status = AgentItemStatus.Done,
                        ToolName = toolLabel,
                        ToolKind = toolKind,
                        Title = title,
                        Detail = detail,
                        CreatedAt = itemTime,
                        UpdatedAt = itemTime,
                    };
                    items.Add(toolItem);
                    if (Str(payload["call_id"]) is { Length: > 0 } callId)
                        toolsByCallId[callId] = toolItem;
                }
                else if (pType is "function_call_output" or "custom_tool_call_output")
                {
                    var callId = Str(payload["call_id"]);
                    var output = Str(payload["output"]);
                    if (!string.IsNullOrEmpty(callId) && !string.IsNullOrWhiteSpace(output) && toolsByCallId.TryGetValue(callId, out var toolItem))
                        toolItem.Output = AgentHubRules.TrimToolOutput(output);
                }
            }
        }

        var chosen = hasResponseMessages ? items : eventFallbackItems;
        return FinalizeImportData(threadId, explicitTitle, workingDir, firstTime, lastTime, fallbackTime, chosen);
    }

    private static string? CleanCodexUserBlock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("<environment_context>", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<permissions instructions>", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<INSTRUCTIONS>", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<collaboration_mode>", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<app-context>", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<subagent_notification>", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("# AGENTS.md instructions", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return trimmed;
    }

    private static (string Name, string Kind, string Title, string? Detail) DescribeCodexTool(string name, string rawArgs)
    {
        if (name is "exec_command" or "shell_command" or "shell" or "local_shell")
        {
            var argsObj = TryParseJsonObject(rawArgs);
            var cmd = Str(argsObj?["cmd"]) ?? Str(argsObj?["command"]) ?? rawArgs;
            return ("執行指令", "bash", ClaudeStreamRules.FirstLine(cmd), string.IsNullOrWhiteSpace(cmd) ? null : cmd);
        }
        if (name == "apply_patch")
        {
            return ("套用修補", "edit", ClaudeStreamRules.FirstLine(rawArgs), string.IsNullOrWhiteSpace(rawArgs) ? null : rawArgs);
        }
        var summary = ClaudeStreamRules.FirstLine(string.IsNullOrWhiteSpace(rawArgs) ? name : rawArgs);
        return (name, "other", summary, string.IsNullOrWhiteSpace(rawArgs) ? null : rawArgs);
    }

    // ───────────────────────────── Antigravity（brain/*/.system_generated/logs/transcript*.jsonl） ─────────────────────────────

    /// <summary>收集 Antigravity 逐字稿中由 invoke_subagent 派生的子代理對話 ID，供主清單排除子代理</summary>
    public static IReadOnlySet<string> CollectAntigravitySubagentIds(IEnumerable<string> lines)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool expectSubagentResult = false;
        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;
            bool mentionsSubagent = rawLine.Contains("invoke_subagent", StringComparison.OrdinalIgnoreCase)
                || rawLine.Contains("spawnStepIndex", StringComparison.OrdinalIgnoreCase)
                || expectSubagentResult;
            if (!mentionsSubagent)
                continue;

            if (TryParseJsonObject(rawLine) is not { } obj)
                continue;

            bool currentHasInvoke = false;
            if (obj["tool_calls"] is JArray calls)
            {
                foreach (var call in calls.OfType<JObject>())
                {
                    var name = Str(call["name"]) ?? Str(call["tool_name"]);
                    if (string.Equals(name, "invoke_subagent", StringComparison.OrdinalIgnoreCase))
                        currentHasInvoke = true;
                }
            }

            if (expectSubagentResult || rawLine.Contains("spawnStepIndex", StringComparison.OrdinalIgnoreCase))
            {
                var content = Str(obj["content"]);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    foreach (Match m in SubagentIdJsonRegex().Matches(content))
                    {
                        var id = m.Groups[1].Value.Trim();
                        if (id.Length > 0)
                            ids.Add(id);
                    }
                }
            }
            expectSubagentResult = currentHasInvoke;
        }
        return ids;
    }

    /// <summary>解析 Antigravity 逐字稿（transcript_full.jsonl／transcript.jsonl）；無使用者訊息時回 null</summary>
    public static ExternalConversationImportData? ParseAntigravityTranscriptJsonl(string sessionId, IEnumerable<string> lines, DateTime fallbackTime)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        string? workingDir = null;
        DateTime? firstTime = null;
        DateTime? lastTime = null;
        var stepsByIndex = new SortedDictionary<int, (JObject Raw, AntigravityTranscriptStep Step, DateTime Time)>();

        foreach (var rawLine in lines)
        {
            if (TryParseJsonObject(rawLine) is not { } rawObj)
                continue;
            if (AntigravityTranscriptRules.ParseLine(rawLine) is not { } step)
                continue;

            DateTime stepTime = fallbackTime;
            if (TryParseTimestamp(rawObj["created_at"], out var ts))
            {
                firstTime ??= ts;
                if (lastTime == null || ts > lastTime.Value)
                    lastTime = ts;
                stepTime = ts;
            }

            if (workingDir == null && !string.IsNullOrEmpty(step.Content))
                workingDir = TryExtractAntigravityWorkingDir(step.Content);

            if ((string.Equals(step.Type, "SYSTEM_PROMPT", StringComparison.OrdinalIgnoreCase) || step.StepIndex <= 2)
                && !string.IsNullOrEmpty(step.Content)
                && step.Content.Contains("<subagent_reminder>", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            stepsByIndex[step.StepIndex] = (rawObj, step, stepTime);
        }

        var items = new List<AgentChatItem>();
        bool hasRealUserMessage = false;

        foreach (var (_, (rawObj, step, stepTime)) in stepsByIndex)
        {
            var source = Str(rawObj["source"]);
            if (string.Equals(source, "SYSTEM", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(step.Type, "USER_INPUT", StringComparison.OrdinalIgnoreCase))
            {
                var userText = CleanAntigravityUserText(step.Content);
                if (!string.IsNullOrWhiteSpace(userText))
                {
                    hasRealUserMessage = true;
                    items.Add(NewItem(AgentItemKind.User, userText, stepTime));
                }
            }
            else if (string.Equals(step.Type, "PLANNER_RESPONSE", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(step.Thinking))
                    items.Add(NewItem(AgentItemKind.Thinking, step.Thinking.Trim(), stepTime));

                foreach (var call in step.ToolCalls)
                {
                    if (string.IsNullOrWhiteSpace(call.Name))
                        continue;
                    var desc = AntigravityStepRules.DescribeTool(call.Name, new JObject { ["parameters"] = call.Args });
                    var diff = AntigravityTranscriptRules.ComputeToolDiff(call.Name, call.Args);
                    items.Add(new AgentChatItem
                    {
                        Kind = AgentItemKind.Tool,
                        Status = AgentItemStatus.Done,
                        ToolName = desc.Name,
                        ToolKind = desc.Kind,
                        Title = desc.Title,
                        Detail = desc.Detail,
                        FilePath = desc.FilePath,
                        Diff = diff?.ToList(),
                        CreatedAt = stepTime,
                        UpdatedAt = stepTime,
                    });
                }

                if (!string.IsNullOrWhiteSpace(step.Content))
                {
                    var reply = NewItem(AgentItemKind.Assistant, step.Content.Trim(), stepTime);
                    foreach (var media in step.MediaPaths)
                        reply.MediaPaths.Add(media);
                    items.Add(reply);
                }
            }
        }

        if (!hasRealUserMessage)
            return null;

        return FinalizeImportData(sessionId.Trim(), explicitTitle: null, workingDir, firstTime, lastTime, fallbackTime, items);
    }

    private static string? TryExtractAntigravityWorkingDir(string content)
    {
        var match = AntigravityCwdRegex().Match(content);
        if (!match.Success)
            return null;
        var candidate = match.Groups[1].Value.Trim();
        return AgentAttachmentRules.IsLocalDrivePath(candidate) ? candidate : null;
    }

    private static string? CleanAntigravityUserText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("<SYSTEM_MESSAGE>", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<CONTEXT_SUMMARY>", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var reqMatch = UserRequestTagRegex().Match(trimmed);
        if (reqMatch.Success)
        {
            var inner = reqMatch.Groups[1].Value.Trim();
            return inner.Length > 0 ? inner : null;
        }
        return trimmed;
    }

    // ───────────────────────────── Grok（sessions/<encoded-cwd>/<uuid>/chat_history.jsonl 與舊式單檔） ─────────────────────────────

    /// <summary>解析 Grok session 目錄旁的 prompt_context.json、summary.json 與 URL 編碼專案資料夾名稱</summary>
    public static (string? WorkingDirectory, string? Title, DateTime? UpdatedAt) ParseGrokSidecarMetadata(
        string? promptContextJson,
        string? summaryJson,
        string? encodedProjectFolderName)
    {
        string? cwd = null;
        if (TryParseJsonObject(promptContextJson) is { } ctxObj)
        {
            var rawCwd = Str(ctxObj["working_directory"]) ?? Str(ctxObj["workingDirectory"]) ?? Str(ctxObj["cwd"]);
            if (!string.IsNullOrWhiteSpace(rawCwd) && AgentAttachmentRules.IsLocalDrivePath(rawCwd))
                cwd = rawCwd.Trim();
        }
        if (cwd == null && !string.IsNullOrWhiteSpace(encodedProjectFolderName)
            && !string.Equals(encodedProjectFolderName, "sessions", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(encodedProjectFolderName, "conversations", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Uri.UnescapeDataString(encodedProjectFolderName);
                if (AgentAttachmentRules.IsLocalDrivePath(decoded))
                    cwd = decoded;
            }
            catch (UriFormatException)
            {
            }
        }

        string? title = null;
        DateTime? updatedAt = null;
        if (TryParseJsonObject(summaryJson) is { } sumObj)
        {
            title = CleanTitle(Str(sumObj["generated_title"]) ?? Str(sumObj["title"]));
            if (TryParseTimestamp(sumObj["updated_at"] ?? sumObj["updatedAt"], out var uTs))
                updatedAt = uTs;
        }
        return (cwd, title, updatedAt);
    }

    /// <summary>解析 Grok 本機明文 JSON／JSONL 對話檔（含 chat_history.jsonl 的思考摘要與工具呼叫）；無明文對話內容時回 null</summary>
    public static ExternalConversationImportData? ParseGrokFile(
        string fallbackSessionId,
        IReadOnlyList<string> lines,
        DateTime fallbackTime,
        string? fallbackWorkingDir = null,
        string? fallbackTitle = null,
        DateTime? fallbackUpdatedAt = null)
    {
        if (lines.Count == 0)
            return null;

        var validFallbackDir = !string.IsNullOrWhiteSpace(fallbackWorkingDir) && AgentAttachmentRules.IsLocalDrivePath(fallbackWorkingDir)
            ? fallbackWorkingDir.Trim()
            : null;
        var cleanedFallbackTitle = CleanTitle(fallbackTitle);

        var joined = string.Join("\n", lines).Trim();
        if (joined.StartsWith('{') && TryParseJsonObject(joined) is { } singleObj && singleObj["messages"] is JArray msgArr)
        {
            var sid = Str(singleObj["sessionId"]) ?? Str(singleObj["id"]) ?? fallbackSessionId;
            var cwd = Str(singleObj["cwd"]) ?? Str(singleObj["workingDirectory"]) ?? validFallbackDir;
            if (!string.IsNullOrEmpty(cwd) && !AgentAttachmentRules.IsLocalDrivePath(cwd))
                cwd = validFallbackDir;
            var title = CleanTitle(Str(singleObj["title"])) ?? cleanedFallbackTitle;
            TryParseTimestamp(singleObj["createdAt"], out var cTs);
            TryParseTimestamp(singleObj["updatedAt"], out var uTs);
            DateTime? finalUpdated = uTs != default ? uTs : fallbackUpdatedAt;
            var items = new List<AgentChatItem>();
            foreach (var m in msgArr.OfType<JObject>())
            {
                var role = Str(m["role"]);
                var text = Str(m["content"]) ?? Str(m["text"]);
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                if (role == "user")
                    items.Add(NewItem(AgentItemKind.User, text.Trim(), fallbackTime));
                else if (role == "assistant")
                    items.Add(NewItem(AgentItemKind.Assistant, text.Trim(), fallbackTime));
            }
            return FinalizeImportData(sid, title, cwd, cTs != default ? cTs : null, finalUpdated, fallbackTime, items);
        }

        string? sessionId = fallbackSessionId;
        string? workingDir = validFallbackDir;
        string? explicitTitle = cleanedFallbackTitle;
        DateTime? firstTime = null;
        DateTime? lastTime = fallbackUpdatedAt;
        var jsonlItems = new List<AgentChatItem>();
        var toolsById = new Dictionary<string, AgentChatItem>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (TryParseJsonObject(line) is not { } obj)
                continue;
            if (TryParseTimestamp(obj["timestamp"] ?? obj["createdAt"] ?? obj["created_at"], out var ts))
            {
                firstTime ??= ts;
                if (lastTime == null || ts > lastTime.Value)
                    lastTime = ts;
            }
            if ((Str(obj["sessionId"]) ?? Str(obj["session_id"])) is { Length: > 0 } sid)
                sessionId = sid;
            if ((Str(obj["cwd"]) ?? Str(obj["workingDirectory"]) ?? Str(obj["working_directory"])) is { Length: > 0 } cwd
                && AgentAttachmentRules.IsLocalDrivePath(cwd))
            {
                workingDir = cwd;
            }
            if (Str(obj["title"]) is { Length: > 0 } t)
                explicitTitle = CleanTitle(t) ?? explicitTitle;

            var role = Str(obj["role"]) ?? Str(obj["type"]);
            var text = ExtractGrokContentText(obj["content"]) ?? Str(obj["text"]);
            var itemTime = ts != default ? ts : fallbackTime;

            if (role == "user")
            {
                if (!string.IsNullOrWhiteSpace(text))
                    jsonlItems.Add(NewItem(AgentItemKind.User, text.Trim(), itemTime));
            }
            else if (role == "assistant")
            {
                var summary = Str(obj["summary"]) ?? Str(obj["reasoning_summary"]) ?? Str(obj["reasoning_content"]) ?? Str(obj["thinking"]);
                if (!string.IsNullOrWhiteSpace(summary))
                    jsonlItems.Add(NewItem(AgentItemKind.Thinking, summary.Trim(), itemTime));

                if (obj["tool_calls"] is JArray toolCalls)
                {
                    foreach (var tc in toolCalls.OfType<JObject>())
                    {
                        var fn = tc["function"] as JObject;
                        var name = Str(fn?["name"]) ?? Str(tc["name"]) ?? "工具";
                        var rawArgs = Str(fn?["arguments"]) ?? (tc["arguments"]?.ToString(Formatting.None) ?? string.Empty);
                        var (toolLabel, toolKind, toolTitle, toolDetail) = DescribeCodexTool(name, rawArgs);
                        var toolItem = new AgentChatItem
                        {
                            Kind = AgentItemKind.Tool,
                            Status = AgentItemStatus.Done,
                            ToolName = toolLabel,
                            ToolKind = toolKind,
                            Title = toolTitle,
                            Detail = toolDetail,
                            CreatedAt = itemTime,
                            UpdatedAt = itemTime,
                        };
                        jsonlItems.Add(toolItem);
                        if (Str(tc["id"]) is { Length: > 0 } tcId)
                            toolsById[tcId] = toolItem;
                    }
                }

                if (!string.IsNullOrWhiteSpace(text))
                    jsonlItems.Add(NewItem(AgentItemKind.Assistant, text.Trim(), itemTime));
            }
            else if (role == "tool")
            {
                var tcId = Str(obj["tool_call_id"]) ?? Str(obj["toolCallId"]);
                if (!string.IsNullOrEmpty(tcId) && !string.IsNullOrWhiteSpace(text) && toolsById.TryGetValue(tcId, out var toolItem))
                    toolItem.Output = AgentHubRules.TrimToolOutput(text);
            }
        }

        return FinalizeImportData(sessionId, explicitTitle, workingDir, firstTime, lastTime, fallbackTime, jsonlItems);
    }

    private static string? ExtractGrokContentText(JToken? token)
    {
        if (token == null)
            return null;
        if (token.Type == JTokenType.String)
            return token.Value<string>();
        if (token is JArray arr)
        {
            var parts = arr.OfType<JObject>()
                .Select(o => Str(o["text"]) ?? Str(o["content"]))
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var joined = string.Join("\n\n", parts!);
            return joined.Length > 0 ? joined : null;
        }
        return null;
    }

    // ───────────────────────────── 共用輔助 ─────────────────────────────

    private static ExternalConversationImportData? FinalizeImportData(
        string? sessionId,
        string? explicitTitle,
        string? workingDir,
        DateTime? firstTime,
        DateTime? lastTime,
        DateTime fallbackTime,
        List<AgentChatItem> items)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || items.Count == 0)
            return null;

        bool hasMessage = items.Any(i => i.Kind is AgentItemKind.User or AgentItemKind.Assistant);
        if (!hasMessage)
            return null;

        var firstUser = items.FirstOrDefault(i => i.Kind == AgentItemKind.User)?.Text;
        var firstAssistant = items.FirstOrDefault(i => i.Kind == AgentItemKind.Assistant)?.Text;
        var title = !string.IsNullOrWhiteSpace(explicitTitle)
            ? explicitTitle!
            : MakeImportTitle(firstUser ?? firstAssistant ?? "匯入的對話");

        var created = firstTime ?? lastTime ?? fallbackTime;
        var updated = lastTime ?? fallbackTime;
        if (updated < created)
            updated = created;

        return new ExternalConversationImportData(
            sessionId.Trim(),
            title,
            string.IsNullOrWhiteSpace(workingDir) ? null : workingDir.Trim(),
            created,
            updated,
            items);
    }

    public static string MakeImportTitle(string? raw)
    {
        var cleaned = CleanTitle(raw);
        return string.IsNullOrEmpty(cleaned) ? "匯入的對話" : cleaned;
    }

    private static string? CleanTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var firstLine = raw.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        if (string.IsNullOrEmpty(firstLine))
            return null;
        return firstLine.Length <= MaxTitleLength ? firstLine : firstLine[..MaxTitleLength] + "…";
    }

    private static AgentChatItem NewItem(AgentItemKind kind, string text, DateTime time)
        => new()
        {
            Kind = kind,
            Status = AgentItemStatus.Done,
            Text = text,
            CreatedAt = time,
            UpdatedAt = time,
        };

    private static JObject? TryParseJsonObject(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('{'))
            return null;
        try
        {
            using var reader = new JsonTextReader(new StringReader(trimmed)) { DateParseHandling = DateParseHandling.None };
            return JToken.ReadFrom(reader) as JObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseTimestamp(JToken? token, out DateTime localTime)
    {
        localTime = default;
        if (token == null)
            return false;
        if (token is JValue { Type: JTokenType.Integer or JTokenType.Float } num)
        {
            try
            {
                var val = (long)num.Value<double>();
                var dto = val > 10_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(val)
                    : DateTimeOffset.FromUnixTimeSeconds(val);
                localTime = dto.LocalDateTime;
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }
        var str = Str(token);
        if (!string.IsNullOrWhiteSpace(str)
            && DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            localTime = parsed.LocalDateTime;
            return true;
        }
        return false;
    }

    private static string? Str(JToken? token)
        => token is JValue { Value: not null } v ? Convert.ToString(v.Value, CultureInfo.InvariantCulture) : null;

    [GeneratedRegex(@"<command-name>\s*([^<]+?)\s*</command-name>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ClaudeCommandNameRegex();

    [GeneratedRegex(@"<command-args>\s*(.*?)\s*</command-args>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ClaudeCommandArgsRegex();

    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$")]
    private static partial Regex TrailingUuidRegex();

    [GeneratedRegex(@"""conversation_?[iI]d""\s*:\s*""([0-9a-fA-F\-]{8,64})""")]
    private static partial Regex SubagentIdJsonRegex();

    [GeneratedRegex(@"<USER_REQUEST>\s*(.*?)\s*</USER_REQUEST>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UserRequestTagRegex();

    [GeneratedRegex(@"Command Working Directory:\s*([^\r\n<]+)")]
    private static partial Regex AntigravityCwdRegex();
}
