using Myriagent.Agents;
using Myriagent.Json;
using Newtonsoft.Json;

namespace Myriagent.Remote;

/// <summary>
/// 從遠端（瀏覽器經伺服器）送進代理的輸入的來源（設計 14.11 D13）：排進佇列的訊息與拒絕附言帶著它，
/// 從佇列取出時由 RemoteQueueRules.CanDequeue（WP4.1 步 6）判斷當時的模式與政策還允不允許（入列後模式被調高、政策關掉、遠端暫停時留在佇列）。
/// 存進 queue.json：本機的輸入沒有它（null，不寫進檔案，舊檔照讀）
/// </summary>
/// <param name="CommandId">信封的 cid（伺服器產生的 ULID）</param>
/// <param name="EffectiveMode">入列當下對話的權限模式；不認得的名稱讀成 null（取出時當成不允許，不讓整份 queue.json 被當成損毀）</param>
/// <param name="Tier">閘門算出的最終層級（0～2）</param>
/// <param name="FullAccess">需要「允許遠端完全自動」</param>
/// <param name="Author">協作人發起的（多人協作，2026-10-30）：協作人的顯示名稱，建立使用者訊息時記成 <see cref="AgentChatItem.Author"/>；擁有者的是 null（不寫進檔案）</param>
internal sealed record RemoteOrigin(
    string CommandId,
    [property: JsonConverter(typeof(UnknownEnumNameAsNullConverter))] AgentPermissionMode? EffectiveMode,
    int Tier,
    bool FullAccess,
    string? Author = null);
