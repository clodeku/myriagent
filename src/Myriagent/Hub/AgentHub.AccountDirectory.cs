using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 帳戶目錄（WP3.4）：「在這台登入」與登入的選項。中樞只碰帳戶的型別與同步橋的介面（S6：未配對時不碰同步的組件）
internal sealed partial class AgentHub
{
    /// <summary>
    /// accountAdd（web-protocol）：帶了 logicalAccountId（不是 JSON null）就是「在這台登入」帳戶目錄的邏輯帳戶——名稱、顏色、預期信箱一律取自目錄
    /// （網頁的 provider、label、color 不看），檢查不過只對送指令的視窗提示，絕不落回一般新增；其他情況照舊新增
    /// </summary>
    private void AccountAddCommand(IAgentView view, JObject msg)
    {
        var claimed = msg["logicalAccountId"];
        if (claimed is null || claimed.Type == JTokenType.Null)
        {
            AccountAdd(msg.Value<string>("provider"), msg.Value<string>("label"), msg.Value<string>("color"));
            return;
        }

        if (sync is not { } bridge)
        {
            SyncToast(AccountIdentityRules.DescribeClaimFailure(CatalogClaimCheck.NotPaired), view);
            return;
        }

        var logicalAccountId = claimed.Type == JTokenType.String ? (string?)claimed : null;
        if (bridge.ClaimCatalogAccount(logicalAccountId, out var failure) is not { } template)
        {
            SyncToast(failure, view);
            return;
        }

        accounts.Add(template.Provider, template.Label, template.Color, new AccountClaim(template.LogicalAccountId, template.ExpectedEmail));
    }

    /// <summary>
    /// accountLogin（web-protocol）：Claude 帳戶已對應或已宣告目錄的邏輯帳戶時，登入帶目錄的預期信箱（D12；不合格的信箱由帳戶管理員略過）。
    /// method＝"deviceCode" 是 Codex 的裝置碼登入（只有 Codex 接受，其他種類由帳戶管理員以 done:true 拒絕，D19）；看不懂的 method 記一筆、整個指令忽略
    /// （不落進別種登入）
    /// </summary>
    private void AccountLoginCommand(IAgentView view, JObject msg)
    {
        if (accounts.Find(msg.Value<string>("accountId")) is not { } account)
            return;
        if (CodexDeviceCodeRules.ParseLoginMethod(msg["method"]) is not { } method)
        {
            Log.Info("accountLogin 的 method 看不懂，忽略這個指令");
            return;
        }
        var email = account.Provider switch
        {
            AgentProvider.Claude => sync?.ExpectedEmail(account.Id),
            AgentProvider.Codex or AgentProvider.Antigravity or AgentProvider.Grok => null,
            _ => throw new ArgumentOutOfRangeException(nameof(msg), account.Provider, "沒有登入方式的代理種類"),
        };
        _ = accounts.LoginAsync(account.Id, new LoginRequest(method, email));
    }

    /// <summary>
    /// accountLogin 訊息（登入進度）：Codex 裝置碼登入進行中（還沒 done）時加上 deviceCode {userCode, verificationUrl, expiresAt}，網頁可以做成大字加複製鈕。
    /// 給這台的本機視窗（message 文字裡本來就有驗證碼；網頁發起的遠端登入也一樣）；給發起的瀏覽器連線的那一則由遠端處理者另外組、
    /// 經 CommandOutput 只給那條連線（WP4.8 D9），不經這裡。done 的訊息不帶
    /// </summary>
    private void AddDeviceCode(JObject message, string accountId, bool done)
    {
        if (done || accounts.Find(accountId) is null || accounts.GetState(accountId).DeviceCode is not { } code)
            return;
        message["deviceCode"] = new JObject
        {
            ["userCode"] = code.UserCode,
            ["verificationUrl"] = code.VerificationUrl.AbsoluteUri,
            ["expiresAt"] = code.ExpiresAt,
        };
    }

    /// <summary>
    /// 網頁（重新）載入（OnReady，init 之後）：進行中、還沒到期的 Codex 裝置碼登入對這個視窗重送一則進度（含 deviceCode）。
    /// 驗證碼只出現在登入開始時的那一則進度，渲染程序重啟或重新整理後畫面上就沒有了（合併前審查 F2）。只送這個視窗、彈出視窗不送
    /// </summary>
    private void ReplayDeviceCodes(IAgentView view)
    {
        if (view.IsPopout)
            return;
        var now = DateTimeOffset.UtcNow;
        foreach (var account in accounts.Accounts)
        {
            var state = accounts.GetState(account.Id);
            if (!state.LoginBusy || state.DeviceCode is not { } code || code.ExpiresAt <= now.ToUnixTimeMilliseconds())
                continue;
            var json = new JObject
            {
                ["type"] = "accountLogin",
                ["accountId"] = account.Id,
                ["message"] = CodexDeviceCodeRules.ProgressText(code, now),
                ["done"] = false,
                ["success"] = null,
            };
            AddDeviceCode(json, account.Id, false);
            Post(view, json);
        }
    }
}
