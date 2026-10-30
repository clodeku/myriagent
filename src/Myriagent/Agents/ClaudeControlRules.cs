using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>背景工作目前的輸出（IAgentSession.GetTaskOutputAsync）；Error 不是 null＝取不到</summary>
/// <param name="Output">輸出的文字（超過上限只留尾端）</param>
/// <param name="TotalBytes">CLI 回報的總位元組數</param>
/// <param name="Truncated">CLI 或 app 截掉了前面</param>
internal sealed record AgentTaskOutput(string? Output, long TotalBytes, bool Truncated, string? Error);

/// <summary>回溯檔案的結果（IAgentSession.RewindFilesAsync）</summary>
/// <param name="CanRewind">CLI 說可以回溯（dry run）或已經回溯（實際做）</param>
/// <param name="Files">會變動或已變動的檔案</param>
/// <param name="Insertions">dry run 的增加行數</param>
/// <param name="Deletions">dry run 的刪除行數</param>
/// <param name="Error">做不到的原因（沒有程序、沒有這則訊息的還原點、CLI 的錯誤）</param>
internal sealed record AgentRewindResult(bool CanRewind, IReadOnlyList<string> Files, int Insertions, int Deletions, string? Error);

/// <summary>
/// Claude 的控制請求：背景工作的輸出（get_task_output）、取消插進這一輪還沒被讀走的訊息（cancel_async_message）、
/// 回溯檔案（rewind_files）。實測 2.1.310 見 docs/protocol-notes.md「Claude：回溯、插入的訊息取消、背景工作的輸出、主代理與輸出風格、備援模型」
/// （tools/probes/claude_cli6_probe.py）
/// </summary>
internal static class ClaudeControlRules
{
    /// <summary>給網頁的輸出最多這麼多字（只留尾端：背景指令的最新進度在最後）</summary>
    public const int MaxTaskOutputChars = 200_000;

    /// <summary>產生標題（generate_session_title；persist false：不改 CLI 這個工作階段的名稱，改名由中樞的 rename_session 做）</summary>
    public static string BuildGenerateTitleRequest(string requestId, string description)
        => ClaudeStreamRules.BuildControlRequest(requestId, new JObject
        {
            ["subtype"] = "generate_session_title",
            ["description"] = description,
            ["persist"] = false,
        });

    /// <summary>generate_session_title 的回應 {title}；沒有標題（說明是空的）回 null</summary>
    public static string? ParseGeneratedTitle(JObject? response)
        => response?["title"] is JValue { Type: JTokenType.String } t && t.ToString().Trim().Length > 0 ? t.ToString().Trim() : null;

    public static string BuildTaskOutputRequest(string requestId, string taskId)
        => ClaudeStreamRules.BuildControlRequest(requestId, new JObject { ["subtype"] = "get_task_output", ["task_id"] = taskId });

    /// <summary>get_task_output 的回應 {output, total_bytes, truncated}；超過上限只留尾端（算截掉）</summary>
    public static AgentTaskOutput ParseTaskOutput(JObject? response)
    {
        var output = response?["output"] is JValue { Type: JTokenType.String } o ? o.ToString() : string.Empty;
        long total = response?["total_bytes"] is JValue { Type: JTokenType.Integer } t ? t.Value<long>() : output.Length;
        bool truncated = response?["truncated"] is JValue { Type: JTokenType.Boolean } tr && tr.Value<bool>();
        if (output.Length > MaxTaskOutputChars)
        {
            output = output[^MaxTaskOutputChars..];
            truncated = true;
        }
        return new AgentTaskOutput(output, total, truncated, null);
    }

    /// <summary>
    /// 取消插進這一輪的訊息：還排著（CLI 還沒在工具結果之後讀走它）時回 {cancelled:true}，那則不會被代理看到；
    /// 已經讀走、或不認得的 uuid 回 {cancelled:false}
    /// </summary>
    public static string BuildCancelAsyncMessageRequest(string requestId, string messageUuid)
        => ClaudeStreamRules.BuildControlRequest(requestId, new JObject { ["subtype"] = "cancel_async_message", ["message_uuid"] = messageUuid });

    public static bool ParseCancelled(JObject? response)
        => response?["cancelled"] is JValue { Type: JTokenType.Boolean } c && c.Value<bool>();

    /// <summary>回溯檔案到這則使用者訊息之前（要開檔案檢查點，ClaudeStreamRules.FileCheckpointingVariable）；dryRun 只回會改哪些檔案</summary>
    public static string BuildRewindFilesRequest(string requestId, string userMessageUuid, bool dryRun)
        => ClaudeStreamRules.BuildControlRequest(requestId, new JObject
        {
            ["subtype"] = "rewind_files",
            ["user_message_id"] = userMessageUuid,
            ["dry_run"] = dryRun,
        });

    /// <summary>rewind_files 的回應 {canRewind, filesChanged, insertions, deletions, error}（實際做時沒有 filesChanged：沿用 dry run 的清單）</summary>
    public static AgentRewindResult ParseRewind(JObject? response)
    {
        bool can = response?["canRewind"] is JValue { Type: JTokenType.Boolean } c && c.Value<bool>();
        var files = (response?["filesChanged"] as JArray ?? new JArray()).OfType<JValue>()
            .Where(v => v.Type == JTokenType.String).Select(v => v.ToString()).Where(f => f.Length > 0).Take(500).ToList();
        int ins = response?["insertions"] is JValue { Type: JTokenType.Integer } i ? i.Value<int>() : 0;
        int del = response?["deletions"] is JValue { Type: JTokenType.Integer } d ? d.Value<int>() : 0;
        var error = response?["error"] is JValue { Type: JTokenType.String } e && e.ToString().Trim().Length > 0 ? e.ToString().Trim() : null;
        return new AgentRewindResult(can, files, ins, del, can ? null : error ?? "這則訊息沒有檔案還原點");
    }
}
