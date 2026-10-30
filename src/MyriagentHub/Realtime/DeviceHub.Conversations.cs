using Myriagent.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 裝置 hub 的第 4 階段對話方法（WP4.2 計畫步 11；契約 D50：服務以方法參數注入，這個 partial 檔只用 <c>Context</c> 與注入的服務，不碰主檔的欄位）
/// </summary>
internal sealed partial class DeviceHub
{
    /// <summary>
    /// 裝置 → 伺服器（Send，<c>live1</c>）：串流中的項目（sync-protocol 14.3）。不等寫入者、不回錯誤：不合格、超速、不是擁有者的直接丟掉、只計數
    /// （<see cref="LiveRelayIntake"/>），通過的交給閘道的即時疊加層
    /// </summary>
    public void LiveRelay(LiveRelay? relay, ConnectionRegistry registry, LiveRelayIntake intake) =>
        intake.Receive(registry.Get(Context.ConnectionId), relay);

    /// <summary>
    /// 裝置 → 伺服器（Send，<c>live2</c>）：串流中的項目只送和上一則不同的部分（docs/plans/2026-10-23-live-delta-and-replica-live.md 3.2）。
    /// 收件以這條連線記著的基底重組成整則，之後同 <see cref="LiveRelay"/>；對不上時回 <c>LiveResync</c> 要整則
    /// </summary>
    public void LiveDelta(LiveDelta? delta, ConnectionRegistry registry, LiveRelayIntake intake) =>
        intake.ReceiveDelta(registry.Get(Context.ConnectionId), delta);
}
