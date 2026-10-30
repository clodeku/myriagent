using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Remote;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 遠端帳戶與 CLI 操作的處理者（WP4.8；擁有者：WP4.8）：網頁對這台發起的 accountLogin、accountLoginDevice（步 7）、
// accountLogout、cliInstall、cliUpdate（步 8：回報真的結果，不再走退路的假 done，D1、D16、D17）。
// 經 WP4.4 的登記點 RegisterAccountRemoteHandlers 登記（建構式呼叫：那時帳戶清單還是空的，只訂閱事件）。
// 共同規則同對話的處理者：先看 context.Authorized；全在 UI 執行緒，每次 await 之後重新確認帳戶還在；
// rejected 一定什麼都沒改（這個指令新建的帳戶在拒絕之前移除，D10）；只輸出一則 accountLogin（D9）
internal sealed partial class AgentHub
{
    /// <summary>身分不符的系統匣通知標題（D15）</summary>
    internal const string RemoteIdentityMismatchTitle = "遠端登入的帳號不符";

    /// <summary>
    /// 驗證網址的最後一道（D19 的防禦：帳戶管理員已經以同一條規則擋過，這裡在輸出之前再查一次）。測試接縫：以它模擬「帳戶管理員放過、這裡擋下」，
    /// 驗證這條分支也取消登入、回滾新建的帳戶
    /// </summary>
    internal Func<string?, bool> RemoteVerificationUrlCheck { get; set; } = CodexDeviceCodeRules.IsAllowedVerificationUrl;

    /// <summary>WP4.1 的登記點：帳戶指令的處理者（建構式呼叫：accounts 已指定、帳戶清單還是空的）</summary>
    partial void RegisterAccountRemoteHandlers(List<IRemoteCommandHandler> into)
    {
        into.Add(new AccountRemoteHandler(this));
        accounts.RemoteIdentityMismatch += OnRemoteIdentityMismatch;
    }

    /// <summary>測試接縫：不經閘門直接呼叫處理者（驗「沒經過閘門的授權就拒絕」）</summary>
    internal IRemoteCommandHandler CreateAccountRemoteHandlerForTest() => new AccountRemoteHandler(this);

    /// <summary>
    /// 帳戶與 CLI 指令（D1）：accountLogin、accountLoginDevice、accountLogout、cliInstall、cliUpdate；不是這一包的回 false
    /// （交給下一個處理者或退路：accountLoginCancel、accountRemove、accountRefresh、cliCheck 留在退路，D18；accountAdd 維持 unsupported）。
    /// <paramref name="command"/> 已套過型錄的限制、通過閘門 5b 的形狀與 5c 的帳戶檢查
    /// </summary>
    private sealed class AccountRemoteHandler(AgentHub hub) : IRemoteCommandHandler
    {
        public bool TryHandle(RemoteViewContext context, IAgentView view, string cmd, JObject command)
        {
            switch (cmd)
            {
                case "accountLogin":
                case "accountLoginDevice":
                case RemoteCommandCatalog.AccountLoginCode:
                case "accountLogout":
                case "cliInstall":
                case "cliUpdate":
                    break;
                default:
                    return false;
            }

            if (!context.Authorized)
            {
                context.Reject(RemoteResultCodes.Invalid, "這個指令沒有經過這台電腦的遠端操作檢查", RemoteGateSteps.Handler);
                return true;
            }

            switch (cmd)
            {
                case "accountLogin":
                    _ = hub.RemoteLoginAsync(context, command.Value<string>("accountId"), null, command["method"]);
                    break;
                case "accountLoginDevice":
                    _ = hub.RemoteLoginAsync(context, null, command.Value<string>("logicalAccountId"), command["method"]);
                    break;
                case RemoteCommandCatalog.AccountLoginCode:
                    _ = hub.RemoteLoginCodeAsync(context, command.Value<string>("logicalAccountId"), command.Value<string>("code"));
                    break;
                case "accountLogout":
                    _ = hub.RemoteLogoutAsync(context, command.Value<string>("accountId"));
                    break;
                case "cliInstall":
                case "cliUpdate":
                    hub.RemoteCliAction(context, cmd, command.Value<string>("provider"));
                    break;
            }

            return true;
        }
    }

    /// <summary>
    /// 遠端登出（D16；UI 執行緒）：前置條件（RemoteAccountRules.CheckLogout：不進同步的種類 policy、CLI 正在更新或這個帳戶正在登入 busy、
    /// 沒安裝 conflict）不過就拒絕，什麼都沒執行（busy 讓「所有電腦登出」由伺服器重送）；本來就未登入直接 done、不跑 CLI；
    /// 其他在帳戶自己的設定資料夾跑 CLI 的登出，等它結束（最多 RemoteLogoutDeadline）：結束碼 0 是 done，失敗或逾時是 unknown（可能已經登出）。
    /// 有執行中的對話照樣登出（網頁的確認框寫明）
    /// </summary>
    private async Task RemoteLogoutAsync(RemoteViewContext context, string? accountId)
    {
        try
        {
            // 閘門 5c 已確認是這台的本機帳戶；這裡是防禦
            if (accounts.Find(accountId) is not { } account)
            {
                context.Reject(RemoteResultCodes.Invalid, "這台電腦沒有這個帳戶", RemoteGateSteps.Handler);
                return;
            }

            var facts = accounts.GetRemoteLogoutFacts(account);
            if (RemoteAccountRules.CheckLogout(facts) is { Allowed: false } refused)
            {
                context.Reject(refused.Code!, refused.Message!, RemoteGateSteps.Handler);
                return;
            }

            if (RemoteAccountRules.IsAlreadyLoggedOut(facts))
            {
                context.Complete();
                return;
            }

            Log.Info($"遠端指令 {context.CommandId}：登出帳戶「{account.Label}」");
            var result = await accounts.LogoutForRemoteAsync(account.Id);
            switch (result)
            {
                case RemoteLogoutResult.Done:
                    context.Complete();
                    break;
                case RemoteLogoutResult.Failed:
                case RemoteLogoutResult.TimedOut:
                    Log.Warn($"遠端指令 {context.CommandId}：帳戶「{account.Label}」的登出沒有成功（{result}）");
                    context.Fail(RemoteAccountRules.LogoutFailedMessage, RemoteGateSteps.Handler);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(result), result, "不認得的登出結果");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"遠端指令 {context.CommandId} 登出時出錯", ex);
            context.Fail("電腦處理這個指令時出錯，可能已經登出；請看那一格的狀態", RemoteGateSteps.Handler);
        }
    }

    /// <summary>
    /// 遠端安裝、更新 CLI（D17；UI 執行緒）：前置條件（RemoteAccountRules.CheckCliAction：四種都收，正在檢查安裝或更新 conflict，
    /// 更新但沒安裝 conflict〔免得落進安裝〕，安裝但已安裝而且沒損壞 conflict）由 CliManager 的狀態判斷；放行就在背景開始（同本機的按鈕，
    /// RunCliActionAsync），done＝已經開始，結果看 devicesCli。Claude 的安裝會在那台開安裝視窗（網頁的確認框寫明要有人在那台）
    /// </summary>
    private void RemoteCliAction(RemoteViewContext context, string cmd, string? providerName)
    {
        if (ParseProvider(providerName) is not { } provider)
        {
            context.Reject(RemoteResultCodes.Invalid, "不認得的代理種類", RemoteGateSteps.Handler);
            return;
        }

        var state = cli.Get(provider);
        if (RemoteAccountRules.CheckCliAction(cmd, provider, new RemoteCliFacts(state.Busy, state.Installed, state.InstallBroken)) is { Allowed: false } refused)
        {
            context.Reject(refused.Code!, refused.Message!, RemoteGateSteps.Handler);
            return;
        }

        Func<Task> action = cmd switch
        {
            "cliInstall" => () => Task.Run(() => cli.InstallAsync(provider)),
            "cliUpdate" => () => Task.Run(() => cli.UpdateAsync(provider)),
            _ => throw new ArgumentOutOfRangeException(nameof(cmd), cmd, "不是遠端 CLI 指令"),
        };
        Log.Info($"遠端指令 {context.CommandId}：{cmd} {provider}");
        _ = RunCliActionAsync(provider, action);
        context.Complete();
    }

    /// <summary>
    /// 遠端登入（D2～D10；UI 執行緒）。accountLogin 給本機帳戶（5c 已確認存在），預期身分經同步橋取（未配對是 None）；
    /// accountLoginDevice 給邏輯帳戶，要配對，經同步橋找這台的本機帳戶，沒有就以目錄的範本建立（不自動登入）。
    /// 前置條件（RemoteAccountRules.CheckLogin）都在起任何程序之前查；之後以 StartRemoteLoginAsync 等里程碑（最多 25 秒）：
    /// Codex 拿到驗證碼、Claude 主控台開啟就以唯一的一則 accountLogin（RemoteAccountRules.LoginOutput）回 done；Failed 回拒絕。
    /// 這個指令新建的帳戶在每一個拒絕之前移除（連資料夾），rejected＝什麼都沒改
    /// </summary>
    private async Task RemoteLoginAsync(RemoteViewContext context, string? localAccountId, string? logicalAccountId, JToken? methodToken)
    {
        string? createdId = null;
        try
        {
            if (CodexDeviceCodeRules.ParseLoginMethod(methodToken) is not { } method)
            {
                context.Reject(RemoteResultCodes.Invalid, "登入方式只能是裝置碼或不指定", RemoteGateSteps.Handler);
                return;
            }

            // 目標帳戶與目錄的預期身分
            AgentAccount? account;
            RemoteLoginExpectation expected;
            CatalogAccountTemplate? template = null;
            if (logicalAccountId is not null)
            {
                if (sync is not { } bridge)
                {
                    context.Reject(RemoteResultCodes.Invalid, AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.NotPaired), RemoteGateSteps.Handler);
                    return;
                }

                var lookup = bridge.FindRemoteLoginTarget(null, logicalAccountId);
                if (lookup.Target is not { } target)
                {
                    context.Reject(lookup.Code ?? RemoteResultCodes.Conflict, lookup.Message ?? AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.NotInCatalog),
                        RemoteGateSteps.Handler);
                    return;
                }

                expected = target.Expected;
                account = target.LocalAccountId is { } existing ? accounts.Find(existing) : null;
                template = target.Create;
                if (account is null && template is null)
                {
                    context.Reject(RemoteResultCodes.Conflict, "這台電腦沒有這個帳戶", RemoteGateSteps.Handler);
                    return;
                }
            }
            else
            {
                account = accounts.Find(localAccountId);
                if (account is null)
                {
                    context.Reject(RemoteResultCodes.Invalid, "這台電腦沒有這個帳戶", RemoteGateSteps.Handler);
                    return;
                }

                expected = sync?.FindRemoteLoginTarget(account.Id, null).Target?.Expected ?? RemoteLoginExpectation.None;
            }

            // 前置條件（D6）：什麼都還沒改、沒有起任何程序
            var facts = account is not null ? accounts.GetRemoteLoginFacts(account, DateTimeOffset.UtcNow) : accounts.GetNewAccountFacts(template!.Provider);
            var decision = RemoteAccountRules.CheckLogin(facts, method);
            if (!decision.Allowed)
            {
                context.Reject(decision.Code!, decision.Message!, RemoteGateSteps.Handler);
                return;
            }

            // 新建（D11：不自動登入，接著以 StartRemoteLoginAsync 開始）；名稱、顏色、預期信箱取自目錄
            if (account is null)
            {
                account = accounts.Add(template!.Provider, template.Label, template.Color, new AccountClaim(template.LogicalAccountId, template.ExpectedEmail),
                    startLogin: false);
                createdId = account.Id;
                Log.Info($"遠端指令 {context.CommandId}：在這台建立帳戶「{account.Label}」並開始遠端登入");
            }

            var email = account.Provider switch
            {
                AgentProvider.Claude => sync?.ExpectedEmail(account.Id),
                AgentProvider.Codex or AgentProvider.Antigravity or AgentProvider.Grok => null,
                _ => throw new ArgumentOutOfRangeException(nameof(localAccountId), account.Provider, "沒有登入方式的代理種類"),
            };
            var accountId = account.Id;
            var start = await accounts.StartRemoteLoginAsync(accountId, new LoginRequest(method, email, expected));

            if (disposed)
            {
                RollBackRemoteLogin(accountId, createdId, cancelLogin: true);
                context.Reject(RemoteResultCodes.Busy, "電腦的程式正在關閉", RemoteGateSteps.Handler);
                return;
            }

            if (start.Milestone == RemoteLoginMilestone.Failed)
            {
                // 登入已經結束（期限到時 StartRemoteLoginAsync 已取消它自己那一次）：已有的帳戶不動，只移除新建的
                RollBackRemoteLogin(accountId, createdId, cancelLogin: false);
                context.Reject(start.Code ?? RemoteResultCodes.Conflict, start.Message ?? RemoteAccountRules.LoginNotStartedMessage, RemoteGateSteps.Handler);
                return;
            }

            // 等里程碑的期間帳戶被移除（本機按了移除）：登入已隨之取消
            if (accounts.Find(accountId) is null)
            {
                RollBackRemoteLogin(accountId, createdId, cancelLogin: false);
                context.Reject(RemoteResultCodes.Conflict, RemoteAccountRules.LoginCanceledMessage, RemoteGateSteps.Handler);
                return;
            }

            // Claude 的登入網址（2026-10-26）：只給 claude.com／claude.ai 的 https 網址
            if (start.Milestone == RemoteLoginMilestone.UrlShown && !RemoteLoginUrlRules.IsAllowedClaudeLoginUrl(start.LoginUrl?.AbsoluteUri))
            {
                Log.Warn($"遠端指令 {context.CommandId}：Claude 的登入網址不是允許的主機，取消這次登入");
                RollBackRemoteLogin(accountId, createdId, cancelLogin: true);
                context.Reject(RemoteResultCodes.Policy, "那台的 CLI 給的登入網址不是允許的網址，已取消這次登入", RemoteGateSteps.Handler);
                return;
            }

            // 驗證網址的防禦（D19）：帳戶管理員已經擋過，這裡不過就取消登入、回滾
            if (start.Milestone == RemoteLoginMilestone.CodeShown && !RemoteVerificationUrlCheck(start.DeviceCode?.VerificationUrl.AbsoluteUri))
            {
                Log.Warn($"遠端指令 {context.CommandId}：驗證網址不是允許的主機，取消這次登入");
                RollBackRemoteLogin(accountId, createdId, cancelLogin: true);
                context.Reject(RemoteResultCodes.Policy, "那台的 CLI 給的驗證網址不是允許的網址，已取消這次登入", RemoteGateSteps.Handler);
                return;
            }

            createdId = null;
            context.Complete(RemoteAccountRules.LoginOutput(accountId, start, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            Log.Error($"遠端指令 {context.CommandId} 開始登入時出錯", ex);
            if (createdId is not null && !context.IsCompleted)
                RollBackRemoteLogin(createdId, createdId, cancelLogin: true);
            context.Fail("電腦處理這個指令時出錯，登入可能已經開始；請看那一格的狀態", RemoteGateSteps.Handler);
        }
    }

    /// <summary>
    /// 遠端登入貼回來的授權碼（accountLoginCode，2026-10-26；UI 執行緒）：以同步橋找這台對應的本機帳戶，交給它進行中的 Claude app 內登入。
    /// 交出去、CLI 接受了是 done（之後這台照常收尾、重新檢查）；沒有進行中的登入、格式不對、CLI 拒絕回 conflict 與原因
    /// </summary>
    private async Task RemoteLoginCodeAsync(RemoteViewContext context, string? logicalAccountId, string? code)
    {
        try
        {
            if (sync is not { } bridge || logicalAccountId is null || bridge.FindRemoteLoginTarget(null, logicalAccountId).Target?.LocalAccountId is not { } localId
                || accounts.Find(localId) is not { Provider: AgentProvider.Claude } account)
            {
                context.Reject(RemoteResultCodes.Conflict, "這台電腦沒有進行中的 Claude 登入", RemoteGateSteps.Handler);
                return;
            }
            Log.Info($"遠端指令 {context.CommandId}：收到「{account.Label}」的 Claude 授權碼");
            var error = await accounts.SubmitClaudeLoginCodeAsync(account.Id, code);
            if (disposed)
                return;
            if (error == null)
                context.Complete();
            else
                context.Reject(RemoteResultCodes.Conflict, error, RemoteGateSteps.Handler);
        }
        catch (Exception ex)
        {
            Log.Error($"遠端指令 {context.CommandId} 交出授權碼時出錯", ex);
            context.Fail("電腦處理授權碼時出錯，請重新登入", RemoteGateSteps.Handler);
        }
    }

    /// <summary>
    /// 遠端登入沒有開始（或開始了但要拒絕）：<paramref name="cancelLogin"/> 時取消這個帳戶進行中的登入（已開始、要拒絕的那一次）；
    /// 這個指令新建的帳戶連資料夾一起移除（D10：那時資料夾裡還沒有憑證；移除會取消它的登入，刪除等登入程序結束，照帳戶管理員既有的
    /// 待刪標記與重試）。已有的帳戶不移除。錯誤只記錄（結果照樣回拒絕）
    /// </summary>
    private void RollBackRemoteLogin(string accountId, string? createdId, bool cancelLogin)
    {
        try
        {
            if (cancelLogin)
                accounts.CancelLogin(accountId);
            if (createdId is not null && accounts.Find(createdId) is not null)
            {
                Log.Info($"遠端登入沒有開始：移除這個指令新建的帳戶 {createdId}");
                accounts.Remove(createdId, deleteData: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"遠端登入的回滾失敗（帳戶 {accountId}）", ex);
        }
    }

    /// <summary>
    /// 遠端登入的身分和目錄不同（D15；帳戶管理員在同一個資料夾自動登出之後，UI 執行緒）：系統匣通知一則，只帶帳戶名稱，
    /// 不含信箱與驗證碼。說明文字（RemoteLoginNote）由帳戶管理員經 link.upsert 發布
    /// </summary>
    private void OnRemoteIdentityMismatch(string accountId, bool loggedOut)
    {
        if (disposed)
            return;
        try
        {
            var label = accounts.Find(accountId)?.Label ?? "這個帳戶";
            host.Notify(RemoteIdentityMismatchTitle, RemoteAccountRules.MismatchNotifyText(label, loggedOut), null);
        }
        catch (Exception ex)
        {
            Log.Error("遠端登入身分不符的通知失敗", ex);
        }
    }
}
