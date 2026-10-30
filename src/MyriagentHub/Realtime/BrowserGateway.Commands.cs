namespace MyriagentHub.Realtime;

/// <summary>
/// 遠端指令給瀏覽器的 <c>control</c> 訊息（WP4.5 計畫 D24；web-protocol 第一節〔遠端〕、契約 D57）：
/// <list type="bullet">
/// <item>每則 init 入列之後，閘道在同一輪接著入列一則（<see cref="Deliver"/> 呼叫 <see cref="EnqueueControl"/>）；init 不帶這些欄位（契約 D57）。</item>
/// <item>擁有者密碼的狀態改變時（<see cref="MyriagentHub.Auth.OwnerAccountService.PasswordStateChanged"/>），對每條已送過 init 的連線各送一則（<see cref="PushControl"/>）。</item>
/// <item>合併鍵 <see cref="CommandMessageRules.ControlKey"/>：佇列裡只留最新的一則、位置不變；init 屏障清掉排著的（不是通知類），接著入列的是新的一則。</item>
/// <item><c>tier1Until</c>／<c>tier2Until</c> 一律 null：兩個時間窗是 null（<see cref="Myriagent.Remote.RemoteAuthRules"/>，擁有者 2026-10-18 的 Q2），
/// 有人改常數時 <c>CommandMessageRulesTests</c> 會提醒要改成每個工作階段的時間窗。</item>
/// <item>沒有掛上來源（<see cref="AttachControl"/>，由啟動工作 <c>CommandStartup</c> 掛上；只建閘道的單元測試 GatewayHarness 沒有）時一則都不送。</item>
/// </list>
/// 讀來源與入列在 <c>controlGate</c> 裡一起做：密碼狀態是先改值、再通知，閘道迴圈讀了舊值而還沒入列時通知插進來的話，通知要等迴圈入列完
/// 才讀新值入列、合併掉舊的，所以佇列裡最後一則一定是最新的值。鎖的順序固定是 <c>controlGate</c> → 連線的 gate → 送出佇列的鎖；
/// 溢出的處理（<see cref="HandleResult"/> 會取連線鎖、可能中斷連線）在 <c>controlGate</c> 之外
/// </summary>
internal sealed partial class BrowserGateway
{
    private readonly object controlGate = new();
    private Func<bool>? defaultPasswordSource;

    /// <summary>掛上 <c>control</c> 的來源（啟動工作，所有連線之前；D28）：擁有者是否仍是預設密碼。之後每則 init 都接著一則 control</summary>
    internal void AttachControl(Func<bool> defaultPassword)
    {
        ArgumentNullException.ThrowIfNull(defaultPassword);
        Volatile.Write(ref defaultPasswordSource, defaultPassword);
    }

    /// <summary>目前的 <c>control</c> 訊息（合併鍵 <see cref="CommandMessageRules.ControlKey"/>）；沒掛上來源回 null</summary>
    internal ClientOutboxMessage? ControlMessage()
    {
        if (Volatile.Read(ref defaultPasswordSource) is not { } source)
            return null;
        return new ClientOutboxMessage(CommandMessageRules.ControlType, CommandMessageRules.ControlJson(null, null, source()), CommandMessageRules.ControlKey);
    }

    /// <summary>
    /// 擁有者密碼的狀態改變了（<see cref="MyriagentHub.Auth.OwnerAccountService.PasswordStateChanged"/>，在改密碼的請求或管理收件匣的執行緒上）：
    /// 對每條已送過 init 的連線各送一則（在連線鎖裡看「已送過 init」再入列，同 <see cref="SendAfterInit"/>）；還沒送過 init 的連線等它的 init 帶出。
    /// 溢出照第 3 階段的規則重送 init（重送的 init 之後又是一則 control）
    /// </summary>
    public void PushControl()
    {
        var all = connections.Snapshot();
        if (all.Count == 0)
            return;
        var results = new List<(BrowserConnection Connection, ClientOutboxResult Result)>(all.Count);
        lock (controlGate)
        {
            if (ControlMessage() is not { } message)
                return;
            foreach (var connection in all)
            {
                // 擁有者的預設密碼警告不給協作人（多人協作）
                if (!connection.IsCollaborator && connection.TryEnqueueAfterInit(message) is { } result)
                    results.Add((connection, result));
            }
        }
        var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        var resync = false;
        foreach (var (connection, result) in results)
            resync |= HandleResult(connection, result, nowMs).Resync;
        if (resync)
            Wake();
    }

    /// <summary>
    /// <see cref="Deliver"/> 在 init 入列之後呼叫：同一輪接著入列一則 control（init 已在連線鎖裡標成送過，這裡照樣在連線鎖裡檢查再入列）。
    /// 溢出要重送 init 時回 true；沒掛上來源時什麼都不做
    /// </summary>
    private bool EnqueueControl(BrowserConnection connection, long nowMs)
    {
        ClientOutboxResult? result;
        lock (controlGate)
        {
            if (ControlMessage() is not { } message)
                return false;
            result = connection.TryEnqueueAfterInit(message);
        }
        return result is { } queued && HandleResult(connection, queued, nowMs).Resync;
    }
}
