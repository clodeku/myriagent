using Myriagent.Cli;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>只接受 doctor 的遮蔽版 JSON；CLI 不支援或輸出格式不符時，不把原始 stdout／stderr 當成報告。</summary>
internal static class CodexDoctorRules
{
    public static string[] Arguments(bool networkOnly) => networkOnly ? ["doctor", "--json", "--network-only"] : ["doctor", "--json"];

    public static JObject Report(CliRunResult run)
    {
        if (run.TimedOut) throw new InvalidOperationException("Codex 診斷逾時，請稍後重試");
        if (run.Stdout.Length > 512_000) throw new InvalidOperationException("Codex 診斷報告過大");
        JObject report;
        try { report = JObject.Parse(run.Stdout); }
        catch (Newtonsoft.Json.JsonException) { throw new InvalidOperationException("Codex 沒有回傳可讀取的診斷報告，請確認 CLI 已更新並可正常啟動"); }
        if (report.Value<int?>("schemaVersion") != 1 || report["checks"] is not JObject checks)
            throw new InvalidOperationException("這個 Codex 診斷格式尚不支援，請更新 Myriagent 或 CLI");
        return new JObject
        {
            ["version"] = report["codexVersion"]?.DeepClone(),
            ["status"] = report["overallStatus"]?.DeepClone(),
            ["generatedAt"] = report["generatedAt"]?.DeepClone(),
            ["checks"] = new JArray(checks.Properties().Take(200).Where(p => p.Value is JObject).Select(p =>
            {
                var check = (JObject)p.Value;
                return new JObject
                {
                    ["id"] = p.Name, ["status"] = check["status"]?.DeepClone(), ["summary"] = check["summary"]?.DeepClone(),
                    ["details"] = check["details"]?.DeepClone(), ["remediation"] = check["remediation"]?.DeepClone(),
                };
            })),
        };
    }
}
