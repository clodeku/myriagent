using Myriagent.Cli;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Voice;

/// <summary>辨識程序回的一行（<see cref="VoiceToolRules.ParseWorkerLine"/>）：事件（ready、fatal）或某一個請求的結果</summary>
internal sealed record VoiceWorkerLine(string? Event, long? Id, bool Ok, string? Text, double? Duration, string? Error, string? Device, string? Model);

/// <summary>
/// 語音訊息的本機辨識（faster-whisper 的常駐程序，<see cref="WhisperVoiceTranscriber"/>）的純函式規則：找 Python、解析程序的輸出、
/// 給人看的錯誤說明
/// </summary>
internal static class VoiceToolRules
{
    /// <summary>GPU 上用的模型（貪婪解碼約 0.7 秒辨識 7 秒的話，2026-10-23 在 RTX 4070 實測）</summary>
    public const string GpuModel = "large-v3";

    /// <summary>沒有可用的 GPU 時改用的模型（CPU 跑 large-v3 太慢）</summary>
    public const string CpuModel = "small";

    /// <summary>辨識程序閒置多久就結束（釋放顯示卡記憶體；下一則語音再啟動，載入模型約 4 秒）</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    /// <summary>第一則（含啟動程序、載入模型；模型還沒下載時要先下載）最多等多久</summary>
    public static readonly TimeSpan FirstRequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>之後每一則最多等多久</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 依 PATH 的目錄順序找 python.exe；略過 <c>WindowsApps</c> 底下的（Microsoft Store 的捷徑：沒有安裝 Python 時執行它會開商店）。
    /// 目錄清單同 CLI 與 ffmpeg（<see cref="CliLocator.MergeSearchDirectories"/>：展開環境變數、去重、略過 UNC）
    /// </summary>
    public static string? FindPython(IEnumerable<string?> pathValues, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(pathValues);
        ArgumentNullException.ThrowIfNull(fileExists);
        foreach (var dir in CliLocator.MergeSearchDirectories(pathValues, Array.Empty<string>()))
        {
            if (dir.Contains(@"\Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase))
                continue;
            var candidate = Path.Join(dir, "python.exe");
            if (fileExists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>辨識程序的一行 JSON；不是 JSON 物件（Python 的警告印到 stdout 之類）回 null</summary>
    public static VoiceWorkerLine? ParseWorkerLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{')
            return null;
        JObject o;
        try
        {
            o = JObject.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
        return new VoiceWorkerLine(
            o["event"] is JValue { Type: JTokenType.String } e ? (string?)e : null,
            o["id"] is JValue { Type: JTokenType.Integer } id ? (long)id : null,
            o["ok"] is JValue { Type: JTokenType.Boolean } ok && (bool)ok,
            o["text"] is JValue { Type: JTokenType.String } t ? (string?)t : null,
            o["duration"] is JValue { Type: JTokenType.Float or JTokenType.Integer } d ? (double)d : null,
            o["error"] is JValue { Type: JTokenType.String } err ? (string?)err : null,
            o["device"] is JValue { Type: JTokenType.String } dev ? (string?)dev : null,
            o["model"] is JValue { Type: JTokenType.String } m ? (string?)m : null);
    }

    /// <summary>送給辨識程序的一行：<c>{"id":n,"path":"…"}</c></summary>
    public static string RequestLine(long id, string audioPath)
        => new JObject { ["id"] = id, ["path"] = audioPath }.ToString(Formatting.None);

    /// <summary>
    /// 程序在 ready 之前就結束（或回 fatal）時給人看的說明：找不到 faster-whisper 的提示怎麼裝，其他照原文（截短）
    /// </summary>
    public static string StartFailure(string? fatal, string? stderrTail)
    {
        var detail = !string.IsNullOrWhiteSpace(fatal) ? fatal! : (stderrTail ?? string.Empty);
        if (detail.Contains("No module named 'faster_whisper'", StringComparison.Ordinal) || detail.Contains("faster-whisper 沒有安裝", StringComparison.Ordinal))
            return "這台電腦的 Python 沒有安裝 faster-whisper（在命令列執行 pip install faster-whisper 後再試）";
        if (detail.Contains("No module named 'numpy'", StringComparison.Ordinal))
            return "這台電腦的 Python 沒有安裝 numpy（在命令列執行 pip install numpy 後再試）";
        detail = detail.Trim();
        if (detail.Length > 300)
            detail = "…" + detail[^300..];
        return detail.Length == 0 ? "語音辨識程序沒有啟動" : "語音辨識程序沒有啟動：" + detail;
    }
}
