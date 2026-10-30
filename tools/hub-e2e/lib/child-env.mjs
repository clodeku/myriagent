// 子程序的環境變數（WP4.9 步 7；從 pairing.mjs 搬來，HER 的「剝掉的環境變數」一節）：不繼承本 session 的 Claude／agy 工作階段、
// 各代理的設定與金鑰、同步覆寫、ASP.NET Core 環境，與 GC 設定（DOTNET_GC* 與整個 COMPlus_ 前綴：.NET 10 照樣採用 COMPlus_gcServer 等，
// 會蓋掉 MyriagentHub 的 runtimeconfig；WP4.9 合併前審查 F3）。要給的 GC 設定由呼叫端以 extra 明確放回。
// 清單和 measure_sync.py 的 STRIP_PREFIXES、hub-probe 的 ChildEnvironment.cs 完全相同（test_measure_sync、FleetRulesTests 比對）。

/** 子程序不繼承、名稱以這些開頭的環境變數（不分大小寫） */
export const STRIP_PREFIXES = ['CLAUDE', 'ANTHROPIC_', 'OPENAI_', 'ANTIGRAVITY_', 'GROK_', 'XAI_', 'MYRIAGENT_', 'CODEX_', 'ASPNETCORE_', 'DOTNET_ENVIRONMENT', 'DOTNET_GC', 'COMPLUS_'];

/** 這個名稱會不會被剝掉 */
export function isStripped(name) {
  const upper = String(name).toUpperCase();
  return STRIP_PREFIXES.some((p) => upper.startsWith(p));
}

/** 剝掉之後的環境（<code>extra</code> 最後蓋上去，可以放回明確要給的值，例如 MYRIAGENT_AGENT_PROBE） */
export function childEnv(extra = {}, source = process.env) {
  const env = Object.fromEntries(Object.entries(source).filter(([k]) => !isStripped(k)));
  return { ...env, ...extra };
}
