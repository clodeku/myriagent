using System.Globalization;
using System.Text;
using Myriagent.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>transcript_full.jsonl 中單一工具呼叫的完整紀錄（含未截斷參數與預期的執行步驟編號）</summary>
internal sealed record AntigravityTranscriptToolCall(
    int PlannerStepIndex,
    int ExpectedStepIndex,
    string Name,
    JObject Args);

/// <summary>transcript_full.jsonl 中單一步驟的解析結果</summary>
internal sealed record AntigravityTranscriptStep(
    int StepIndex,
    string Type,
    string? Thinking,
    IReadOnlyList<AntigravityTranscriptToolCall> ToolCalls,
    IReadOnlyList<string> MediaPaths,
    string? Content = null);

/// <summary>
/// Antigravity 逐字稿（transcript_full.jsonl／transcript.jsonl）的純函式解析規則：
/// 解開雙重編碼參數、計算寫檔與改檔紅綠 Diff、過濾本機媒體 URI、偵測規劃與 RequestFeedback 確認需求、展開子代理完整逐字稿
/// </summary>
internal static class AntigravityTranscriptRules
{
    /// <summary>解析逐字稿的一行 JSON；格式不符或沒有 step_index 回 null</summary>
    public static AntigravityTranscriptStep? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        JObject obj;
        try
        {
            using var reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None };
            if (JToken.ReadFrom(reader) is not JObject parsed)
                return null;
            obj = parsed;
        }
        catch (JsonException)
        {
            return null;
        }

        if (!TryInt(obj["step_index"], out var stepIndex))
            return null;

        var type = Str(obj["type"]) ?? string.Empty;
        var thinking = Str(obj["thinking"]);
        if (string.IsNullOrWhiteSpace(thinking))
            thinking = null;
        var content = Str(obj["content"]);
        if (string.IsNullOrWhiteSpace(content))
            content = null;

        var calls = new List<AntigravityTranscriptToolCall>();
        if (obj["tool_calls"] is JArray rawCalls)
        {
            var offset = 0;
            foreach (var item in rawCalls.OfType<JObject>())
            {
                offset++;
                var name = Str(item["name"]) ?? Str(item["tool_name"]) ?? string.Empty;
                var args = UnwrapArgs(item["args"] as JObject ?? TryParseObject(Str(item["args"])));
                calls.Add(new AntigravityTranscriptToolCall(stepIndex, stepIndex + offset, name, args));
            }
        }

        var media = ExtractMediaPaths(obj["media"] as JArray);
        return new AntigravityTranscriptStep(stepIndex, type, thinking, calls, media, content);
    }

    /// <summary>
    /// transcript.jsonl（compact）的 args 屬性值常被再做一次 JSON 字串編碼（例如 "\"C:\\\\a.txt\""），
    /// transcript_full.jsonl 則是直接值：兩者都統一解成乾淨的 JObject
    /// </summary>
    public static JObject UnwrapArgs(JObject? raw)
    {
        var result = new JObject();
        if (raw == null)
            return result;
        foreach (var prop in raw.Properties())
        {
            result[prop.Name] = UnwrapToken(prop.Value);
        }
        return result;
    }

    private static JToken UnwrapToken(JToken? token)
    {
        if (token is not JValue { Type: JTokenType.String, Value: string s })
            return token?.DeepClone() ?? JValue.CreateNull();

        var trimmed = s.Trim();
        if (trimmed.Length >= 2 && (
                (trimmed[0] == '"' && trimmed[^1] == '"')
                || (trimmed[0] == '{' && trimmed[^1] == '}')
                || (trimmed[0] == '[' && trimmed[^1] == ']')))
        {
            try
            {
                using var reader = new JsonTextReader(new StringReader(trimmed)) { DateParseHandling = DateParseHandling.None };
                var inner = JToken.ReadFrom(reader);
                if (inner != null && !reader.Read())
                    return inner;
            }
            catch (JsonException)
            {
            }
        }
        if (trimmed is "true" or "false")
            return new JValue(trimmed == "true");
        return new JValue(s);
    }

    private static JObject? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{'))
            return null;
        try
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
            return JToken.ReadFrom(reader) as JObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 從逐字稿步驟的 media 陣列取出可顯示的本機圖片／影片路徑（file:/// URI 轉本機磁碟路徑，排除 UNC 與非媒體檔）
    /// </summary>
    public static IReadOnlyList<string> ExtractMediaPaths(JArray? media)
    {
        if (media == null || media.Count == 0)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in media.OfType<JObject>())
        {
            var rawUri = Str(item["uri"]) ?? Str(item["path"]);
            if (TryResolveLocalPath(rawUri) is { } local
                && AgentAttachmentRules.IsLocalDrivePath(local)
                && AgentAttachmentRules.IsDisplayableMedia(local)
                && !list.Contains(local, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(local);
            }
        }
        return list;
    }

    /// <summary>把 file:///C:/... URI 或本機路徑轉成乾淨的 Windows 本機路徑；UNC 或非檔案 URI 回 null</summary>
    public static string? TryResolveLocalPath(string? uriOrPath)
    {
        if (string.IsNullOrWhiteSpace(uriOrPath))
            return null;
        var text = uriOrPath.Trim();
        if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile && !uri.IsUnc)
                return uri.LocalPath;
            return null;
        }
        var normalized = text.Replace('/', '\\');
        return AgentAttachmentRules.IsLocalDrivePath(normalized) ? normalized : null;
    }

    /// <summary>
    /// 依工具名稱與完整參數計算紅綠 Diff（重用 DiffRules）：
    /// write_file／write_to_file → FromNewFile；edit_file／replace_file_content → FromEdit；multi_replace_file_content → FromEdits
    /// </summary>
    public static IReadOnlyList<AgentDiffLine>? ComputeToolDiff(string? toolName, JObject? args)
    {
        if (string.IsNullOrEmpty(toolName) || args == null || args.Count == 0)
            return null;
        if (toolName.Equals("write_file", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("write_to_file", StringComparison.OrdinalIgnoreCase))
        {
            var content = FindString(args, "Content", "CodeContent");
            return content != null ? DiffRules.FromNewFile(content) : null;
        }
        if (toolName.Equals("edit_file", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("replace_file_content", StringComparison.OrdinalIgnoreCase))
        {
            var target = FindString(args, "Target", "TargetContent", "OldString", "OldText");
            var replacement = FindString(args, "Replacement", "ReplacementContent", "NewString", "NewText");
            return target != null || replacement != null ? DiffRules.FromEdit(target, replacement) : null;
        }
        if (toolName.Equals("multi_replace_file_content", StringComparison.OrdinalIgnoreCase))
        {
            if (FindToken(args, "ReplacementChunks", "Edits") is JArray chunks && chunks.Count > 0)
            {
                var pairs = new List<(string? OldString, string? NewString)>();
                foreach (var chunk in chunks.OfType<JObject>())
                {
                    var oldStr = FindString(chunk, "TargetContent", "Target", "OldString");
                    var newStr = FindString(chunk, "ReplacementContent", "Replacement", "NewString");
                    if (oldStr != null || newStr != null)
                        pairs.Add((oldStr, newStr));
                }
                return pairs.Count > 0 ? DiffRules.FromEdits(pairs) : null;
            }
        }
        return null;
    }

    /// <summary>
    /// 檢查寫檔／改檔呼叫是否要求使用者確認計畫：
    /// 1) ArtifactMetadata.RequestFeedback == true，或
    /// 2) 規劃模式下寫入／更新 plan.md（或 brain\&lt;id&gt;\plan.md）
    /// </summary>
    public static bool TryExtractPlanReview(
        string? toolName,
        JObject? args,
        string? resolvedFilePath,
        bool isPlanMode,
        out string? planPath,
        out string? summary)
    {
        planPath = null;
        summary = null;
        if (string.IsNullOrEmpty(toolName))
            return false;
        bool isWriteOrEdit = toolName.Equals("write_file", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("write_to_file", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("edit_file", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("replace_file_content", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("multi_replace_file_content", StringComparison.OrdinalIgnoreCase);
        if (!isWriteOrEdit)
            return false;

        var path = resolvedFilePath ?? FindString(args, "Path", "TargetFile", "AbsolutePath", "FilePath");
        if (FindToken(args, "ArtifactMetadata") is JObject meta)
        {
            var req = FindToken(meta, "RequestFeedback");
            if (req is JValue { Type: JTokenType.Boolean, Value: true })
            {
                planPath = path;
                summary = FindString(meta, "Summary");
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            var fileName = Path.GetFileName(path);
            bool isPlanMd = string.Equals(fileName, "plan.md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "implementation_plan.md", StringComparison.OrdinalIgnoreCase);
            bool inBrainDir = path.Replace('/', '\\').Contains("\\brain\\", StringComparison.OrdinalIgnoreCase);
            if (isPlanMd && (isPlanMode || inBrainDir))
            {
                planPath = path;
                if (FindToken(args, "ArtifactMetadata") is JObject anyMeta)
                    summary = FindString(anyMeta, "Summary");
                return true;
            }
        }
        return false;
    }

    public const int MaxArtifactPreviewChars = 64_000;
    private const int MaxArtifactFileBytes = 256 * 1024;

    /// <summary>
    /// 檢查寫檔／改檔呼叫是否針對供使用者閱讀的 Markdown Artifact（帶 ArtifactMetadata 且 UserFacing != false，
    /// 或寫入 brain\&lt;id&gt;\*.md 但不在 .system_generated／scratch 內），並取出完整 Markdown 供對話內預覽卡渲染。
    /// 寫檔（非 Append）可直接由參數 Content 取得；改檔（或 Append）在工具完成時由 <paramref name="readFile"/> 讀取更新後的完整檔案
    /// </summary>
    public static bool TryExtractArtifactMarkdown(
        string? toolName,
        JObject? args,
        string? resolvedFilePath,
        Func<string, string?>? readFile,
        out string? markdown)
    {
        markdown = null;
        if (string.IsNullOrEmpty(toolName))
            return false;

        bool isWrite = toolName.Equals("write_file", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("write_to_file", StringComparison.OrdinalIgnoreCase);
        bool isEdit = toolName.Equals("edit_file", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("replace_file_content", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("multi_replace_file_content", StringComparison.OrdinalIgnoreCase);
        if (!isWrite && !isEdit)
            return false;

        var rawPath = resolvedFilePath ?? FindString(args, "Path", "TargetFile", "AbsolutePath", "FilePath");
        var localPath = TryResolveLocalPath(rawPath) ?? rawPath;
        if (!IsArtifactTarget(args, localPath))
            return false;

        bool isAppend = FindToken(args, "Append") is JValue { Type: JTokenType.Boolean, Value: true };
        if (isWrite && !isAppend)
        {
            if (readFile != null && !string.IsNullOrWhiteSpace(localPath) && readFile(localPath) is { Length: > 0 } diskText)
            {
                markdown = CapArtifactMarkdown(diskText);
                return true;
            }
            var inline = FindString(args, "Content", "CodeContent");
            if (!string.IsNullOrWhiteSpace(inline))
            {
                markdown = CapArtifactMarkdown(inline);
                return true;
            }
            return false;
        }

        if (readFile != null && !string.IsNullOrWhiteSpace(localPath) && readFile(localPath) is { Length: > 0 } updatedText)
        {
            markdown = CapArtifactMarkdown(updatedText);
            return true;
        }
        return false;
    }

    /// <summary>限制對話內預覽 Markdown 長度，避免超長報告在 UI 執行緒同步渲染卡頓，並自動閉合未關閉的程式碼圍欄</summary>
    public static string CapArtifactMarkdown(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (normalized.Length <= MaxArtifactPreviewChars)
            return normalized;

        int cut = normalized.LastIndexOf('\n', MaxArtifactPreviewChars - 1, MaxArtifactPreviewChars / 2);
        if (cut <= 0)
            cut = MaxArtifactPreviewChars;
        if (char.IsHighSurrogate(normalized[cut - 1]))
            cut--;

        var head = normalized[..cut];
        string? openFence = FindUnclosedFence(head);
        var suffix = openFence != null
            ? $"\n{openFence}\n\n> [!NOTE]\n> 預覽內容較長，已顯示前段；點擊上方檔名可開啟完整檔案。"
            : "\n\n> [!NOTE]\n> 預覽內容較長，已顯示前段；點擊上方檔名可開啟完整檔案。";
        return head + suffix;
    }

    private static string? FindUnclosedFence(string markdown)
    {
        char openChar = '\0';
        int openLen = 0;
        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            int indent = 0;
            while (indent < line.Length && indent < 4 && line[indent] == ' ')
                indent++;
            if (indent >= 4 || indent >= line.Length)
                continue;

            char c = line[indent];
            if (c != '`' && c != '~')
                continue;
            int run = 0;
            while (indent + run < line.Length && line[indent + run] == c)
                run++;
            if (run < 3)
                continue;

            if (openLen == 0)
            {
                if (c == '`' && line[(indent + run)..].Contains('`'))
                    continue;
                openChar = c;
                openLen = run;
            }
            else if (c == openChar && run >= openLen && string.IsNullOrWhiteSpace(line[(indent + run)..]))
            {
                openChar = '\0';
                openLen = 0;
            }
        }
        return openLen > 0 ? new string(openChar, openLen) : null;
    }

    public static bool IsArtifactTarget(JObject? args, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var ext = Path.GetExtension(path);
        if (!string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (FindToken(args, "ArtifactMetadata") is JObject meta)
        {
            if (FindToken(meta, "UserFacing") is JValue { Type: JTokenType.Boolean, Value: false })
                return false;
            return true;
        }

        var normalized = path.Replace('/', '\\');
        if (normalized.Contains("\\brain\\", StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains("\\.system_generated\\", StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains("\\scratch\\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    /// <summary>唯讀共享讀取本機 Markdown Artifact 檔案（上限 256 KB，UTF-8 邊界安全，絕不鎖檔；不存在或失敗回 null）</summary>
    public static string? ReadArtifactFileSafe(string? path)
    {
        var local = TryResolveLocalPath(path) ?? path;
        if (string.IsNullOrWhiteSpace(local) || !AgentAttachmentRules.IsLocalDrivePath(local) || !File.Exists(local))
            return null;
        try
        {
            using var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long fileLen = stream.Length;
            var toRead = (int)Math.Min(fileLen, MaxArtifactFileBytes);
            if (toRead <= 0)
                return null;
            var buffer = new byte[toRead];
            int total = 0;
            while (total < toRead)
            {
                int n = stream.Read(buffer, total, toRead - total);
                if (n <= 0)
                    break;
                total += n;
            }
            if (total <= 0)
                return null;
            int validLen = total < fileLen ? TrimIncompleteUtf8Tail(buffer, total) : total;
            if (validLen <= 0)
                return null;
            var text = Encoding.UTF8.GetString(buffer, 0, validLen).TrimStart('\uFEFF').Replace("\r\n", "\n");
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private const int MaxSubagentTranscriptBytes = 256 * 1024;
    private const int SubagentHeadBytes = 64 * 1024;
    private const int SubagentTailBytes = 192 * 1024;

    /// <summary>
    /// 解析子代理對應的本機逐字稿檔案路徑：優先從 <paramref name="subagentInfo"/> 的 <c>log_uri</c> 轉本機路徑，
    /// 沒有時以 <c>conversation_id</c> 透過 <paramref name="transcriptPathResolver"/> 尋找；
    /// 同目錄有精簡版 <c>transcript.jsonl</c> 時優先使用（避免巨型讀檔輸出佔滿配額），否則使用 <c>transcript_full.jsonl</c>
    /// </summary>
    public static string? ResolveSubagentTranscriptPath(JObject? subagentInfo, Func<string, string?>? transcriptPathResolver)
    {
        if (subagentInfo == null || subagentInfo.Count == 0)
            return null;

        string? candidate = null;
        var rawUri = FindString(subagentInfo, "log_uri", "logUri", "logAbsoluteUri");
        if (!string.IsNullOrWhiteSpace(rawUri))
            candidate = TryResolveLocalPath(rawUri);

        if (string.IsNullOrWhiteSpace(candidate) && transcriptPathResolver != null)
        {
            var convId = FindString(subagentInfo, "conversation_id", "conversationId");
            if (!string.IsNullOrWhiteSpace(convId))
                candidate = transcriptPathResolver(convId.Trim());
        }

        if (string.IsNullOrWhiteSpace(candidate) || !AgentAttachmentRules.IsLocalDrivePath(candidate))
            return null;

        var dir = Path.GetDirectoryName(candidate);
        if (!string.IsNullOrEmpty(dir))
        {
            var compact = Path.Combine(dir, "transcript.jsonl");
            if (File.Exists(compact))
                return compact;
            var full = Path.Combine(dir, "transcript_full.jsonl");
            if (File.Exists(full))
                return full;
        }
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// 唯讀共享讀取子代理逐字稿並整理為人類易讀的步驟摘要（思考過程、呼叫的工具與中間回覆），供點擊展開子代理工具卡時查看完整軌跡
    /// </summary>
    public static string? FormatSubagentTranscript(string? path)
    {
        var local = TryResolveLocalPath(path) ?? path;
        if (string.IsNullOrWhiteSpace(local) || !AgentAttachmentRules.IsLocalDrivePath(local) || !File.Exists(local))
            return null;

        string rawText;
        try
        {
            using var stream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = stream.Length;
            if (len <= 0)
                return null;
            if (len <= MaxSubagentTranscriptBytes)
            {
                int toRead = (int)len;
                var buffer = new byte[toRead];
                int total = ReadBuffer(stream, buffer, toRead);
                if (total <= 0)
                    return null;
                rawText = Encoding.UTF8.GetString(buffer, 0, total);
            }
            else
            {
                // 超大逐字稿保留開頭 64 KB 完整行＋結尾 192 KB 完整行，確保最終步驟與結論前夕不因中段大輸出被截掉
                var headBuf = new byte[SubagentHeadBytes];
                int headRead = ReadBuffer(stream, headBuf, SubagentHeadBytes);
                int headNewline = headRead > 0 ? Array.LastIndexOf(headBuf, (byte)'\n', headRead - 1, headRead) : -1;
                string headText = headNewline >= 0
                    ? Encoding.UTF8.GetString(headBuf, 0, headNewline + 1)
                    : string.Empty;

                long tailStart = Math.Max(headNewline >= 0 ? headNewline + 1 : 0, len - SubagentTailBytes);
                stream.Seek(tailStart, SeekOrigin.Begin);
                int tailToRead = (int)Math.Min(len - tailStart, SubagentTailBytes);
                var tailBuf = new byte[tailToRead];
                int tailRead = ReadBuffer(stream, tailBuf, tailToRead);
                string tailText = string.Empty;
                if (tailRead > 0)
                {
                    int firstNewline = tailStart > 0 ? Array.IndexOf(tailBuf, (byte)'\n', 0, tailRead) : -1;
                    int offset = firstNewline >= 0 ? firstNewline + 1 : SkipLeadingUtf8Continuation(tailBuf, tailRead);
                    if (offset < tailRead)
                        tailText = Encoding.UTF8.GetString(tailBuf, offset, tailRead - offset);
                }
                rawText = headText + tailText;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var entries = new List<string>();
        foreach (var rawLine in rawText.Split('\n'))
        {
            var trimmed = rawLine.TrimEnd('\r');
            if (trimmed.Length == 0 || ParseLine(trimmed) is not { } step)
                continue;

            if (!string.IsNullOrWhiteSpace(step.Thinking))
            {
                var brief = ClaudeStreamRules.Truncate(step.Thinking.Trim().Replace("\r\n", "\n"), 300);
                entries.Add($"💭 思考：{brief}");
            }
            foreach (var call in step.ToolCalls)
            {
                if (string.IsNullOrWhiteSpace(call.Name))
                    continue;
                var tool = AntigravityStepRules.DescribeTool(call.Name, new JObject { ["parameters"] = call.Args });
                entries.Add($"▸ {tool.Name}：{tool.Title}");
            }
            if (string.Equals(step.Type, "PLANNER_RESPONSE", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(step.Content)
                && step.ToolCalls.Count > 0)
            {
                var note = ClaudeStreamRules.Truncate(step.Content.Trim().Replace("\r\n", "\n"), 300);
                entries.Add($"💬 {note}");
            }
        }

        if (entries.Count == 0)
            return null;
        return $"── 子代理執行紀錄（共 {entries.Count} 項步驟）──\n{string.Join("\n", entries)}";
    }

    internal static string? FindString(JObject? obj, params string[] names)
    {
        if (FindToken(obj, names) is JValue { Value: not null } v)
            return Convert.ToString(v.Value, CultureInfo.InvariantCulture);
        return null;
    }

    internal static JToken? FindToken(JObject? obj, params string[] names)
    {
        if (obj == null)
            return null;
        foreach (var name in names)
        {
            foreach (var prop in obj.Properties())
            {
                if (string.Equals(prop.Name.Replace("_", string.Empty), name.Replace("_", string.Empty), StringComparison.OrdinalIgnoreCase))
                    return prop.Value;
            }
        }
        return null;
    }

    private static bool TryInt(JToken? token, out int value)
    {
        value = 0;
        return token is JValue { Value: not null } v
            && int.TryParse(Convert.ToString(v.Value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static int ReadBuffer(Stream stream, byte[] buffer, int toRead)
    {
        int total = 0;
        while (total < toRead)
        {
            int n = stream.Read(buffer, total, toRead - total);
            if (n <= 0)
                break;
            total += n;
        }
        return total;
    }

    private static int SkipLeadingUtf8Continuation(byte[] buffer, int length)
    {
        int offset = 0;
        while (offset < length && offset < 4 && (buffer[offset] & 0xC0) == 0x80)
            offset++;
        return offset;
    }

    private static int TrimIncompleteUtf8Tail(byte[] buffer, int length)
    {
        if (length <= 0)
            return 0;
        int cont = 0;
        while (cont < 3 && cont < length && (buffer[length - 1 - cont] & 0xC0) == 0x80)
            cont++;
        if (cont == length)
            return 0;
        byte lead = buffer[length - 1 - cont];
        if ((lead & 0x80) == 0)
            return cont == 0 ? length : length - cont;
        int expected = (lead & 0xE0) == 0xC0 ? 2
            : (lead & 0xF0) == 0xE0 ? 3
            : (lead & 0xF8) == 0xF0 ? 4
            : 1;
        return cont + 1 >= expected ? length : length - (cont + 1);
    }

    private static string? Str(JToken? token)
        => token is JValue { Value: not null } v ? Convert.ToString(v.Value, CultureInfo.InvariantCulture) : null;
}

/// <summary>
/// 增量伴讀 agy 寫在磁碟上的 transcript_full.jsonl（不存在時退回同目錄的 transcript.jsonl）：
/// 以 FileShare.ReadWrite | FileShare.Delete 開啟、記住最後一個完整換行位元組位移，每次只讀新增的完整行、絕不鎖檔
/// </summary>
internal sealed class AntigravityTranscriptReader
{
    public const int MaxSyncChunkBytes = 4 * 1024 * 1024;

    private readonly Func<string, string?>? pathResolver;
    private readonly Dictionary<int, AntigravityTranscriptStep> stepsByIndex = new();
    private readonly Dictionary<int, AntigravityTranscriptToolCall> callsByExpectedStep = new();
    private readonly List<AntigravityTranscriptToolCall> allCalls = new();
    private readonly HashSet<int> matchedCallExpectedSteps = new();

    private string? conversationId;
    private string? activeFilePath;
    private long byteOffset;
    private int maxSeenStepIndex = -1;

    public AntigravityTranscriptReader(Func<string, string?>? transcriptPathResolver = null)
    {
        pathResolver = transcriptPathResolver;
    }

    public bool HasResolver => pathResolver != null;

    public int MaxSeenStepIndex => maxSeenStepIndex;

    public static string? DefaultResolveTranscriptPath(string conversationId)
        => AntigravityStreamRules.ResolveTranscriptPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), conversationId);

    /// <summary>綁定對話 ID（變更時清空先前狀態）</summary>
    public void BindConversation(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || string.Equals(conversationId, id, StringComparison.Ordinal))
            return;
        conversationId = id;
        activeFilePath = null;
        byteOffset = 0;
        maxSeenStepIndex = -1;
        stepsByIndex.Clear();
        callsByExpectedStep.Clear();
        allCalls.Clear();
        matchedCallExpectedSteps.Clear();
    }

    /// <summary>讀取自上次以來新增的完整行，回傳這次新讀到的步驟（依 step_index 升冪）</summary>
    public IReadOnlyList<AntigravityTranscriptStep> Sync()
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return Array.Empty<AntigravityTranscriptStep>();

        var targetPath = ResolveAvailableFile(conversationId);
        if (targetPath == null)
            return Array.Empty<AntigravityTranscriptStep>();

        if (!string.Equals(activeFilePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            // 從 fallback 的 transcript.jsonl 切到剛建立的 transcript_full.jsonl（或第一次綁定）：從頭讀，但已收過的 step_index 只更新資料不當新步驟重發
            activeFilePath = targetPath;
            byteOffset = 0;
        }

        var newlyAdded = new List<AntigravityTranscriptStep>();
        try
        {
            using var stream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < byteOffset)
                byteOffset = 0;

            while (byteOffset < stream.Length)
            {
                stream.Seek(byteOffset, SeekOrigin.Begin);
                var toRead = (int)Math.Min(stream.Length - byteOffset, MaxSyncChunkBytes);
                var buffer = new byte[toRead];
                int totalRead = 0;
                while (totalRead < toRead)
                {
                    int read = stream.Read(buffer, totalRead, toRead - totalRead);
                    if (read <= 0)
                        break;
                    totalRead += read;
                }
                if (totalRead <= 0)
                    break;

                int lastNewline = Array.LastIndexOf(buffer, (byte)'\n', totalRead - 1, totalRead);
                if (lastNewline < 0)
                {
                    if (totalRead >= MaxSyncChunkBytes && TrySkipOversizedLine(stream, byteOffset + totalRead, out var nextLineOffset))
                    {
                        byteOffset = nextLineOffset;
                        continue;
                    }
                    break;
                }

                int consumedBytes = lastNewline + 1;
                byteOffset += consumedBytes;

                var text = Encoding.UTF8.GetString(buffer, 0, consumedBytes);
                foreach (var rawLine in text.Split('\n'))
                {
                    var trimmed = rawLine.TrimEnd('\r');
                    if (trimmed.Length == 0)
                        continue;
                    if (AntigravityTranscriptRules.ParseLine(trimmed) is not { } parsed)
                        continue;

                    bool isNewStep = !stepsByIndex.ContainsKey(parsed.StepIndex);
                    stepsByIndex[parsed.StepIndex] = parsed;
                    if (parsed.StepIndex > maxSeenStepIndex)
                        maxSeenStepIndex = parsed.StepIndex;
                    foreach (var call in parsed.ToolCalls)
                    {
                        callsByExpectedStep[call.ExpectedStepIndex] = call;
                        if (!allCalls.Any(c => c.PlannerStepIndex == call.PlannerStepIndex && c.ExpectedStepIndex == call.ExpectedStepIndex))
                            allCalls.Add(call);
                    }
                    if (isNewStep)
                        newlyAdded.Add(parsed);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"讀取 Antigravity 逐字稿失敗（{targetPath}）：{ex.Message}");
        }

        newlyAdded.Sort((a, b) => a.StepIndex.CompareTo(b.StepIndex));
        return newlyAdded;
    }

    private static bool TrySkipOversizedLine(Stream stream, long scanFrom, out long nextLineOffset)
    {
        nextLineOffset = 0;
        stream.Seek(scanFrom, SeekOrigin.Begin);
        var scratch = new byte[64 * 1024];
        long pos = scanFrom;
        while (pos < stream.Length)
        {
            int n = stream.Read(scratch, 0, scratch.Length);
            if (n <= 0)
                break;
            int idx = Array.IndexOf(scratch, (byte)'\n', 0, n);
            if (idx >= 0)
            {
                nextLineOffset = pos + idx + 1;
                return true;
            }
            pos += n;
        }
        return false;
    }

    public AntigravityTranscriptStep? GetStep(int stepIndex)
        => stepsByIndex.GetValueOrDefault(stepIndex);

    /// <summary>列出所有 step_index &gt;= minStepIndex 的步驟（依 step_index 升冪）</summary>
    public IEnumerable<AntigravityTranscriptStep> StepsFrom(int minStepIndex)
        => stepsByIndex.Values.Where(s => s.StepIndex >= minStepIndex).OrderBy(s => s.StepIndex);

    /// <summary>尋找工具步驟對應的呼叫紀錄（優先精確比對 ExpectedStepIndex，退回找最近同名的未配對呼叫）</summary>
    public AntigravityTranscriptToolCall? FindToolCall(int stepIndex, string? toolName, int minPlannerStepIndex)
    {
        if (callsByExpectedStep.TryGetValue(stepIndex, out var exact)
            && (string.IsNullOrEmpty(toolName) || string.Equals(exact.Name, toolName, StringComparison.OrdinalIgnoreCase)))
        {
            matchedCallExpectedSteps.Add(exact.ExpectedStepIndex);
            return exact;
        }

        if (string.IsNullOrEmpty(toolName))
            return null;

        for (int i = allCalls.Count - 1; i >= 0; i--)
        {
            var candidate = allCalls[i];
            if (candidate.PlannerStepIndex < minPlannerStepIndex || candidate.PlannerStepIndex >= stepIndex)
                continue;
            if (matchedCallExpectedSteps.Contains(candidate.ExpectedStepIndex))
                continue;
            if (string.Equals(candidate.Name, toolName, StringComparison.OrdinalIgnoreCase))
            {
                matchedCallExpectedSteps.Add(candidate.ExpectedStepIndex);
                return candidate;
            }
        }
        return null;
    }

    private string? ResolveAvailableFile(string id)
    {
        if (pathResolver == null)
            return null;
        var primary = pathResolver(id);
        if (string.IsNullOrWhiteSpace(primary))
            return null;
        if (File.Exists(primary))
            return primary;
        // transcript_full.jsonl 還沒產生時可先看同資料夾的 transcript.jsonl
        var dir = Path.GetDirectoryName(primary);
        if (!string.IsNullOrEmpty(dir))
        {
            var compact = Path.Combine(dir, "transcript.jsonl");
            if (File.Exists(compact))
                return compact;
        }
        return null;
    }
}
