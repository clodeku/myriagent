namespace Myriagent.Sync;

/// <summary>
/// 載入邊界（D7）：未配對的程式路徑只能經標了這個屬性的方法碰到 SignalR、SQLite（S6：未配對不載入它們）。
/// 標示的方法必須同步、<c>[MethodImpl(MethodImplOptions.NoInlining)]</c>（TieredPGO 的 tier1 可能把它內聯進呼叫端，
/// 呼叫端一被 JIT 就載入邊界內的型別），所在的型別不得有 lambda、區域函式或編譯器產生的巢狀型別。
/// 靜態掃描（tests 的 AssemblyIsolationScanner）對它只看簽章、不看本體，並檢查上述條件。
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class SyncLoadBoundaryAttribute : Attribute;
