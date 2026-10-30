using Newtonsoft.Json.Linq;

namespace Myriagent.Health;

/// <summary>Claude CLI 列進 skill 清單的一個 skill（get_skills_dialog）</summary>
/// <param name="Name">名稱（外掛的帶命名空間）</param>
/// <param name="Tokens">CLI 算的清單 token 數</param>
/// <param name="Source">來源（user、plugin、claude.ai sync、project…）</param>
/// <param name="Advertised">有列給模型（false＝只有名稱、沒有描述或沒列）</param>
internal sealed record CliSkill(string Name, int Tokens, string Source, bool Advertised);

/// <summary>一個 hook（get_hooks_listing）</summary>
/// <param name="Event">事件（PreToolUse、SessionStart…）</param>
/// <param name="Source">來源（pluginHook、userSettings…）</param>
/// <param name="Plugin">外掛名稱（不是外掛帶的是 null）</param>
internal sealed record CliHook(string Event, string Source, string? Plugin);

/// <summary>
/// Claude CLI 自己回報的清單（儀表板的 MCP 檢查順便問的：get_skills_dialog、get_hooks_listing、get_memory_dialog，每個約 0.05 秒、不用額度）。
/// 健康度有它時 skill 清單用 CLI 實際的數字（含 claude.ai 同步過來的 skill），另外列出 hooks 與自動記憶的開關。
/// 實測見 docs/protocol-notes.md「Claude：CLI 代查用量、控制請求登入、標題產生、健康度清單與一輪中的事件」
/// </summary>
/// <param name="AutoMemory">自動記憶開著（null＝不知道）</param>
/// <param name="AutoDream">auto dream 開著（null＝不知道）</param>
internal sealed record ClaudeCliInventory(IReadOnlyList<CliSkill>? Skills, IReadOnlyList<CliHook>? Hooks, bool? AutoMemory, bool? AutoDream)
{
    /// <summary>三個回應都沒拿到（舊版 CLI、程序沒起來）時是 null</summary>
    public static ClaudeCliInventory? Parse(JObject? skillsDialog, JObject? hooksListing, JObject? memoryDialog)
    {
        var skills = skillsDialog?["skills"] is JArray s
            ? s.OfType<JObject>().Select(o => new CliSkill(
                Str(o, "name") ?? string.Empty,
                o["tokens"] is JValue { Type: JTokenType.Integer } t ? Math.Max(0, t.Value<int>()) : 0,
                Str(o, "source") ?? string.Empty,
                o["advertised"] is not JValue { Type: JTokenType.Boolean, Value: false }))
                .Where(k => k.Name.Length > 0).ToList()
            : null;
        var hooks = hooksListing?["hooks"] is JArray h
            ? h.OfType<JObject>().Select(o => new CliHook(Str(o, "event") ?? string.Empty, Str(o, "source") ?? string.Empty, Str(o, "pluginName")))
                .Where(k => k.Event.Length > 0).ToList()
            : null;
        bool? Flag(string key) => memoryDialog?[key] is JObject m && m["enabled"] is JValue { Type: JTokenType.Boolean } e ? e.Value<bool>() : null;
        var autoMemory = Flag("auto_memory");
        var autoDream = Flag("auto_dream");
        if (skills == null && hooks == null && autoMemory == null && autoDream == null)
            return null;
        return new ClaudeCliInventory(skills, hooks, autoMemory, autoDream);
    }

    private static string? Str(JObject o, string key) => o[key] is JValue { Type: JTokenType.String } v && v.ToString().Trim().Length > 0 ? v.ToString().Trim() : null;
}
