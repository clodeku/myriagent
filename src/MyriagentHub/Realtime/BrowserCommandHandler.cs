using Myriagent.Diagnostics;
using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>
/// 瀏覽器 Cmd 的處理（web-protocol 第六節第 9 點；設計 14.4 伺服器授權、14.6）：
/// <list type="bullet">
/// <item>MessageGuard 解析不過的照代碼回；被動通知接受、不處理；<c>ready</c> 交給閘道排 init 後接受（init 由閘道的迴圈送出）；
/// <c>monitor</c>（電腦狀態面板，stat1）只記在連線上、交給閘道送 <c>deviceStats</c>。</item>
/// <item>擁有者的修改（<c>setting</c>、<c>accountUpdate</c>、<c>accountReorder</c>、<c>deviceRename</c>）由伺服器直接寫進 sync.db（sync-protocol 6.2），
/// 本機提交之後才回 accepted；只等本機提交，不等任何裝置。成功時在同一個寫入工作裡排稽核（提交後才寫）。</item>
/// <item>被拒的代碼來自目錄方法的結果（<see cref="BrowserCommandRules.ErrorFor"/>）；寫入者停止、資料庫錯誤、取消等其他一切例外回
/// <c>unavailable</c>（不以例外型別猜原因）。</item>
/// <item>被拒時依 D17 送 toast：有對應文字、連線已送過 init、同一 cmd 10 秒內沒送過。</item>
/// <item>排空期間照收（D18）：這裡不看 DrainState。</item>
/// <item>第 4 階段的接縫（WP4.1 D50、6.2）：<paramref name="conversationCommands"/>（WP4.2）先接手它認得的 <c>not_available</c> 與接受不處理的指令，
/// <paramref name="router"/>（WP4.5）接手型錄裡由電腦執行的 <c>not_available</c> 指令；兩個都沒有登記（WP4.1，DI 給 null）時與第 3 階段完全相同。</item>
/// </list>
/// </summary>
internal sealed class BrowserCommandHandler(
    SyncWriter writer,
    Catalog catalog,
    AuditLog audit,
    BrowserGateway gateway,
    ILogger<BrowserCommandHandler> log,
    IBrowserConversationCommands? conversationCommands = null,
    IBrowserCommandRouter? router = null,
    CollaboratorDirectory? collaborators = null,
    CollaboratorCommandService? collaboratorCommands = null)
{
    private readonly LogThrottle clientErrorThrottle = new(TimeSpan.FromMinutes(10), TimeProvider.System);

    public async Task<CmdResult> HandleAsync(BrowserConnection connection, CmdParse parsed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(parsed);
        // 協作人（多人協作）：白名單以外一律 invalid、對話只認分享給他的、介面設定存成他自己的
        if (connection.IsCollaborator && await CollaboratorGateAsync(connection, parsed, cancellationToken) is { } gated)
            return gated;
        if (await TryPhase4Async(connection, parsed, cancellationToken) is { } handled)
            return handled;
        string? error;
        if (parsed.Error is { } parseError)
        {
            error = parseError;
            // 桌面專屬的設定鍵：每條連線每個鍵只稽核一次（設計 8.9 的「記入紀錄」；不讓惡意分頁灌稽核）
            if (parsed.DeniedSettingKey is { } key && connection.MarkDeniedSetting(key))
                await audit.WriteAsync(Entry(connection, AuditActions.PrefsSet, key, AuditOutcomes.Denied, new { key }), CancellationToken.None);
        }
        else
        {
            error = parsed.Command switch
            {
                ReadyCommand ready => Ready(connection, ready),
                MonitorCommand monitor => Monitor(connection, monitor),
                ClientErrorCommand clientError => ClientError(connection, clientError),
                PassiveCommand => null,
                SettingCommand setting => await ApplyAsync(parsed, context =>
                {
                    Require(catalog.SetPreference(context, AuditActors.Owner, setting.Key, setting.Value, null));
                    context.AuditAfterCommit(audit, Entry(connection, AuditActions.PrefsSet, setting.Key, AuditOutcomes.Ok,
                        new { key = setting.Key, value = (setting.Value as JValue)?.Value }));
                }, cancellationToken),
                AccountUpdateCommand update => await ApplyAsync(parsed, context =>
                {
                    Require(catalog.UpdateAccount(context, update.AccountId, null, update.Label, update.Color, null));
                    context.AuditAfterCommit(audit, Entry(connection, AuditActions.AccountUpdated, update.AccountId, AuditOutcomes.Ok,
                        new { fields = BrowserCommandRules.UpdatedFields(update.Label, update.Color) }));
                }, cancellationToken),
                AccountReorderCommand reorder => await ApplyAsync(parsed, context =>
                {
                    var count = AccountReorder.Apply(context, catalog, reorder.Ids);
                    if (count > 0)
                        context.AuditAfterCommit(audit, Entry(connection, AuditActions.AccountReordered, null, AuditOutcomes.Ok, new { count }));
                }, cancellationToken),
                DeviceRenameCommand rename => await ApplyAsync(parsed, context =>
                {
                    Require(catalog.RenameDevice(context, rename.DeviceId, rename.Name));
                    context.AuditAfterCommit(audit, Entry(connection, AuditActions.DeviceRenamed, rename.DeviceId, AuditOutcomes.Ok,
                        new { fields = new[] { "name" } }));
                }, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(parsed), parsed.CmdName, "伺服器處理的指令沒有對應的處理"),
            };
        }
        return Finish(connection, parsed, error);
    }

    private CmdResult Finish(BrowserConnection connection, CmdParse parsed, string? error)
    {
        if (error is null)
            return new CmdResult(true, parsed.CommandId);
        Toast(connection, parsed.CmdName, error);
        return new CmdResult(false, parsed.CommandId, error);
    }

    /// <summary>
    /// 第 4 階段的處理者接手的指令（只有認得的名稱、帶原始的 cmd 物件，而且是 <c>not_available</c> 或接受不處理的）：先問對話儲存，再看指令路由；
    /// 都不接手回 null（照第 3 階段處理）。處理者丟出的例外記錄後回 <c>unavailable</c>（同 <see cref="ApplyAsync"/>：不以例外型別猜原因）
    /// </summary>
    private async Task<CmdResult?> TryPhase4Async(BrowserConnection connection, CmdParse parsed, CancellationToken cancellationToken)
    {
        if (conversationCommands is null && router is null && collaboratorCommands is null)
            return null;
        if (parsed.CmdName is not { } name || parsed.Body is not { } body)
            return null;
        var notAvailable = parsed.Error == BrowserErrorCodes.NotAvailable;
        if (!notAvailable && !(parsed.Error is null && parsed.Command is PassiveCommand))
            return null;
        try
        {
            if (conversationCommands is not null && conversationCommands.Handles(name))
                return Finish(connection, parsed, await conversationCommands.HandleAsync(connection, name, body, cancellationToken));
            // 協作人的管理（多人協作）：擁有者才能（協作人在 CollaboratorGateAsync 就被擋）
            if (collaboratorCommands is not null && notAvailable && collaboratorCommands.Handles(name))
                return Finish(connection, parsed, await collaboratorCommands.HandleBrowserAsync(connection, name, body, cancellationToken));
            if (router is not null && notAvailable && RemoteCommandCatalog.Find(name) is { Route: RemoteRoute.Owner or RemoteRoute.Device } spec)
            {
                var result = await router.RouteAsync(connection, parsed, spec, body, cancellationToken);
                if (!result.Accepted && result.Error is { } routeError)
                    Toast(connection, name, routeError);
                return result;
            }
        }
        catch (OperationCanceledException)
        {
            // 連線斷了
            return Finish(connection, parsed, BrowserErrorCodes.Unavailable);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "瀏覽器的 {Cmd} 由第 4 階段的處理者處理時失敗", name);
            return Finish(connection, parsed, BrowserErrorCodes.Unavailable);
        }
        return null;
    }

    /// <summary>
    /// 協作人的指令（多人協作；docs/plans/2026-10-30-collaborators.md 2.3）：回 null＝照一般的流程繼續。
    /// 不在 <see cref="CollaboratorRules.BrowserCommands"/> 的、帶了伺服器草稿或不是分享給他的對話的，一律 invalid、不送 toast
    /// （網頁本來就不顯示這些按鈕；open 另送 conversationClosed，分頁會關掉）；<c>setting</c> 存成他自己的介面設定
    /// </summary>
    private async Task<CmdResult?> CollaboratorGateAsync(BrowserConnection connection, CmdParse parsed, CancellationToken cancellationToken)
    {
        if (parsed.CmdName is not { } name)
            return null;
        if (!CollaboratorRules.BrowserCommands.Contains(name) || collaborators is null)
            return new CmdResult(false, parsed.CommandId, BrowserErrorCodes.Invalid);
        if (parsed.Body?["conversationId"] is JValue { Type: JTokenType.String } conversationToken && (string?)conversationToken.Value is { } conversationId
            && !gateway.CanSeeConversation(connection, conversationId))
        {
            if (name is "open" && Myriagent.Sync.ConversationIds.IsWireId(conversationId))
                gateway.SendAfterInit(connection, ConversationGatewayRules.ClosedMessage(conversationId));
            return new CmdResult(false, parsed.CommandId, BrowserErrorCodes.Invalid);
        }
        if (parsed.Command is SettingCommand setting && parsed.Error is null)
        {
            try
            {
                if (setting.Value is null || !await collaborators.SetPrefAsync(connection.CollaboratorId!, setting.Key, setting.Value, cancellationToken))
                    return new CmdResult(false, parsed.CommandId, BrowserErrorCodes.Invalid);
            }
            catch (OperationCanceledException)
            {
                return new CmdResult(false, parsed.CommandId, BrowserErrorCodes.Unavailable);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "協作人的介面設定寫入失敗");
                return new CmdResult(false, parsed.CommandId, BrowserErrorCodes.Unavailable);
            }
            return new CmdResult(true, parsed.CommandId);
        }
        if (name is "setting")
            return new CmdResult(false, parsed.CommandId, parsed.Error ?? BrowserErrorCodes.Invalid);
        return null;
    }

    private string? Ready(BrowserConnection connection, ReadyCommand ready)
    {
        gateway.RequestInit(connection, ready.Mode);
        return null;
    }

    /// <summary>電腦狀態面板開關（stat1）：只記在這條連線、喚醒閘道，不寫資料庫、不稽核</summary>
    private string? Monitor(BrowserConnection connection, MonitorCommand monitor)
    {
        gateway.SetMonitoring(connection, monitor.On);
        return null;
    }

    /// <summary>
    /// 網頁的錯誤回報：寫進伺服器的記錄（Warning；同一件事 10 分鐘內只記一次，網頁每次重畫都可能再載入同一張破圖），不寫資料庫、不稽核
    /// </summary>
    private string? ClientError(BrowserConnection connection, ClientErrorCommand command)
    {
        if (clientErrorThrottle.ShouldLog(ClientErrorRules.ThrottleKey(command.Report), out var suppressed))
        {
            log.LogWarning("瀏覽器回報（連線 {Connection}）{Report}{Suppressed}", connection.ConnectionId, ClientErrorRules.Describe(command.Report),
                LogThrottle.SuppressedNote(suppressed));
        }

        return null;
    }

    /// <summary>在寫入者交易裡執行；回 null＝已提交，否則是錯誤碼</summary>
    private async Task<string?> ApplyAsync(CmdParse parsed, Action<SyncWriteContext> work, CancellationToken cancellationToken)
    {
        try
        {
            await writer.WriteAsync(work, cancellationToken);
            return null;
        }
        catch (CatalogRejectedException rejected)
        {
            return rejected.Code;
        }
        catch (OperationCanceledException)
        {
            // 連線斷了、工作還沒開始執行：什麼都沒寫
            return BrowserErrorCodes.Unavailable;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "瀏覽器的 {Cmd} 寫入失敗", parsed.CmdName);
            return BrowserErrorCodes.Unavailable;
        }
    }

    /// <summary>目錄方法被拒就丟 <see cref="CatalogRejectedException"/>：這個工作的 SAVEPOINT 回滾（什麼都不留）</summary>
    private static void Require(CatalogOutcome outcome)
    {
        if (BrowserCommandRules.ErrorFor(outcome.Rejected) is { } code)
            throw new CatalogRejectedException(code);
    }

    /// <summary>連線已送過 init、同一 cmd 10 秒內沒送過的檢查與入列由閘道在連線鎖裡一起做（BrowserGateway.SendToast）</summary>
    private void Toast(BrowserConnection connection, string? cmd, string error)
    {
        if (cmd is null || BrowserCommandRules.Toast(cmd, error) is not { } toast)
            return;
        gateway.SendToast(connection, cmd, new ClientOutboxMessage("toast", BrowserCommandRules.ToastJson(toast.Text, toast.Level)));
    }

    private static AuditEntry Entry(BrowserConnection connection, string action, string? target, string outcome, object detail) =>
        new(AuditActors.Owner, SessionRules.ShortId(connection.SessionId), connection.Ip, action, target, outcome, detail);
}
