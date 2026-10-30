using Myriagent.Remote;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>
/// 瀏覽器模式下由伺服器的對話儲存處理的指令（WP4.2；設計 4.9.2、web-protocol 第六節第 9 點）：伺服器草稿的 <c>newConversation</c>、
/// 回 clipboard 的 <c>copyItem</c>、觀看與舊項目（<c>open</c>、<c>close</c>、<c>loadOlder</c>）等。
/// WP4.1 沒有實作：<see cref="BrowserCommandHandler"/> 的參數是 null，這些指令照舊回 <c>not_available</c> 或接受不處理。
/// 在 hub 的呼叫執行緒呼叫（每條連線同時一個）；<c>body</c> 只過了 <see cref="MessageGuard"/> 的形狀上限，欄位由處理者自己驗（不可信）
/// </summary>
internal interface IBrowserConversationCommands
{
    /// <summary>這個指令由對話儲存處理嗎；只會問 <see cref="MessageGuard"/> 判成 <c>not_available</c> 或接受不處理（<see cref="PassiveCommand"/>）的名稱</summary>
    bool Handles(string cmd);

    /// <summary>
    /// 處理 <see cref="Handles"/> 為 true 的指令；回 null＝接受，否則是錯誤碼（<see cref="BrowserErrorCodes"/>，被拒的 toast 照 <see cref="BrowserCommandRules.Toast"/> 送）。
    /// 丟出的例外記錄後回 <c>unavailable</c>
    /// </summary>
    Task<string?> HandleAsync(BrowserConnection connection, string cmd, JObject body, CancellationToken cancellationToken);
}

/// <summary>
/// 把型錄裡由電腦執行的指令（<see cref="RemoteCommandCatalog"/> 路由 Owner／Device）轉給目標電腦（WP4.5；設計 8.11、sync-protocol 14.4）：
/// 型錄、形狀、層級、驗證時間與速率檢查之後，以伺服器身分金鑰簽信封（<c>CommandEnvelope.TrySign</c>）、以 <c>CommandDelivery</c> 送出。
/// WP4.1 沒有實作：參數是 null，這些指令照舊回 <c>not_available</c>。只會收到 <see cref="MessageGuard"/> 判成 <c>not_available</c> 的指令；
/// 回傳的 <see cref="CmdResult"/> 原樣回給瀏覽器（<c>accepted</c> 只表示已送出，結果另以 <c>commandResult</c> 送，web-protocol）。丟出的例外記錄後回 <c>unavailable</c>
/// </summary>
internal interface IBrowserCommandRouter
{
    Task<CmdResult> RouteAsync(BrowserConnection connection, CmdParse parsed, RemoteCommandSpec spec, JObject body, CancellationToken cancellationToken);
}
