using MyriagentHub.Auth;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;

namespace MyriagentHub.Sync;

/// <summary>
/// 遠端指令的啟動工作（WP4.5 計畫 D24、D28）：啟動工作在所有 <c>StartAsync</c> 之前執行（<see cref="StartupSequence"/>），比任何瀏覽器與裝置連線都早，
/// 所以第一則 init 之後就有 <c>control</c>、第一次改密碼就推得出去，第一條 Prove 的連線就排得到 durable 的重簽。
/// <list type="bullet">
/// <item>掛上 <c>control</c> 的來源（<see cref="BrowserGateway.AttachControl"/>）：擁有者是否仍是預設密碼（<see cref="OwnerAccountService.IsDefaultPassword"/>）。
/// 這裡排在擁有者帳號的啟動步驟之前也沒關係：來源是每次送出時才讀的委派。</item>
/// <item>訂閱 <see cref="OwnerAccountService.PasswordStateChanged"/> → <see cref="BrowserGateway.PushControl"/>（改密碼、重設擁有者時對每條已送過 init 的連線重送）。</item>
/// <item>啟動掃描（<see cref="CommandMaintenance.StartupScanAsync"/>）：非 durable、還沒結束的標成 unknown，durable 的 delivered 改回 pending、到期的標成 expired。
/// 失敗就讓啟動失敗（sync.db 寫不進去，伺服器本來就不能用）。</item>
/// <item>第一次清理（<see cref="CommandMaintenance.PruneAsync"/>；之後每 24 小時）：失敗只記警告，下一次再清。</item>
/// <item>訂閱 <see cref="ConnectionRegistry.ConnectionProven"/>／<see cref="ConnectionRegistry.ConnectionRemoved"/> → <see cref="DurableCommands"/>（處理者只排工作）。</item>
/// </list>
/// </summary>
internal sealed class CommandStartup(BrowserGateway gateway, OwnerAccountService owner, CommandMaintenance maintenance, DurableCommands durable,
    ConnectionRegistry registry, ILogger<CommandStartup> log) : IStartupTask
{
    /// <summary>
    /// 排在 sync.db（<see cref="StartupOrder.SyncDatabase"/>）、對話目錄（＋10）與對話未讀（＋20）之後：啟動掃描要讀寫 sync.db。
    /// 不在 <see cref="StartupOrder"/> 加常數（共用檔，WP4.5 計畫 D40）
    /// </summary>
    public const int StartupOrderValue = StartupOrder.SyncDatabase + 30;

    public int Order => StartupOrderValue;

    public string Name => "commands";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        gateway.AttachControl(() => owner.IsDefaultPassword);
        // 先拿掉再加：同一個主機的啟動工作只跑一次，這裡只是不讓重複執行訂閱兩次
        owner.PasswordStateChanged -= gateway.PushControl;
        owner.PasswordStateChanged += gateway.PushControl;

        await maintenance.StartupScanAsync(cancellationToken);
        try
        {
            await maintenance.PruneAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "遠端指令的第一次清理失敗（24 小時後再清）");
        }

        registry.ConnectionProven -= durable.OnProven;
        registry.ConnectionProven += durable.OnProven;
        registry.ConnectionRemoved -= durable.OnRemoved;
        registry.ConnectionRemoved += durable.OnRemoved;
    }
}
