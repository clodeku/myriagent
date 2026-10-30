using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 儀表板的 MCP 以 Claude／Codex 帳戶為單位，在中立的 cache\mcp-check 開檢查用的程序。
/// 只查狀態與授權，不送模型訊息；問完就關，OAuth 等待期間保留程序讓本機回呼完成。
/// 授權成功後對同帳戶存活對話重新連線（Codex 是重載整份 MCP 設定）；專案層以實際 cwd 為準。
/// 只有這台的主視窗（瀏覽器版與其他電腦不送）。
/// </summary>
internal sealed partial class AgentHub
{
    private readonly Dictionary<string, AccountMcpState> accountMcp = new(StringComparer.Ordinal);

    /// <summary>各帳戶檢查用的會話與它的 session id（問完或授權完就關）</summary>
    private readonly Dictionary<string, (IAgentSession Session, string SessionId)> mcpInspectors = new(StringComparer.Ordinal);

    /// <summary>授權時多久問一次狀態（測試改短）</summary>
    internal TimeSpan McpAuthPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>授權最多等多久（使用者在瀏覽器登入、同意）</summary>
    internal TimeSpan McpAuthLimit { get; set; } = TimeSpan.FromMinutes(5);

    private IEnumerable<AgentAccount> McpAccounts() => accounts.Accounts.Where(a => a.Provider is AgentProvider.Claude or AgentProvider.Codex);

    /// <summary>mcpCheck：切到儀表板時送（不帶 force：5 分鐘內查過就只重送），明細的「重新檢查」帶 force。精簡模式的帳戶不載入 MCP，不查</summary>
    private void RequestMcp(bool force)
    {
        if (disposed)
            return;
        var now = DateTime.Now;
        foreach (var account in McpAccounts().Where(a => !a.SafeMode).ToList())
        {
            accountMcp.TryGetValue(account.Id, out var state);
            if (!AccountMcpRules.ShouldCheck(state, now, force))
                continue;
            if (state == null)
                accountMcp[account.Id] = state = new AccountMcpState();
            state.Checking = true;
            _ = CheckAccountMcpAsync(account.Id);
        }
        PostMcp();
    }

    private async Task CheckAccountMcpAsync(string accountId)
    {
        AgentMcpStatusResult result;
        Health.ClaudeCliInventory? inventory = null;
        try
        {
            var (session, settings) = McpInspector(accountId);
            result = await session.GetMcpStatusAsync(settings);
            // 程序開著：順便問 CLI 自己的 skill、hooks、記憶清單（健康度用，各約 0.05 秒；Claude 才有）
            if (result.Servers != null)
                inventory = await session.GetInventoryAsync();
        }
        catch (Exception ex)
        {
            result = new AgentMcpStatusResult(null, ex.Message);
        }
        if (!disposed && inventory != null)
            OnCliInventory(accountId, inventory);
        if (disposed)
            return;
        if (accountMcp.TryGetValue(accountId, out var state))
        {
            state.Checking = false;
            ApplyMcpStatus(state, result);
            if (state.Authorizing == null)
                await CloseMcpInspectorAsync(accountId);
        }
        PostMcp();
    }

    private static void ApplyMcpStatus(AccountMcpState state, AgentMcpStatusResult result)
    {
        state.CheckedAt = DateTime.Now;
        if (result.Servers != null)
        {
            state.Servers = result.Servers.ToList();
            state.Error = null;
        }
        else
        {
            state.Error = result.Error ?? "查不到 MCP 狀態";
        }
    }

    /// <summary>
    /// mcpAction：authenticate（需要授權的伺服器）在瀏覽器開授權頁、等到授權完成；reconnect 在檢查用的程序重新連線並重新檢查。
    /// 都只認這個帳戶最近一次查到的伺服器名稱；成功後對這個帳戶開著的對話送重新連線。結果只對送指令的視窗提示
    /// </summary>
    private void McpAccountAction(IAgentView view, string? accountId, string? server, string? action)
    {
        var account = accounts.Find(accountId);
        if (account == null || account.Provider is not (AgentProvider.Claude or AgentProvider.Codex) || account.SafeMode || AccountMcpRules.ParseAction(action) is not { } act)
            return;
        if (string.IsNullOrWhiteSpace(server) || !accountMcp.TryGetValue(account.Id, out var state) || state.Servers?.Any(s => s.Name == server) != true)
        {
            Toast("找不到這個 MCP 伺服器，請先重新檢查", view: view);
            return;
        }
        if (state.Checking || state.Authorizing != null)
        {
            Toast("這個帳戶的 MCP 正在檢查或授權，請稍候", view: view);
            return;
        }
        state.Authorizing = act == AgentMcpAction.Authenticate ? server : null;
        state.Checking = act == AgentMcpAction.Reconnect;
        PostMcp();
        _ = act == AgentMcpAction.Authenticate ? AuthorizeMcpAsync(view, account.Id, server) : ReconnectMcpAsync(view, account.Id, server);
    }

    private async Task AuthorizeMcpAsync(IAgentView view, string accountId, string server)
    {
        var state = accountMcp[accountId];
        try
        {
            var (session, settings) = McpInspector(accountId);
            var result = await session.McpActionAsync(server, AgentMcpAction.Authenticate, settings);
            if (disposed)
                return;
            if (!result.Ok)
            {
                Toast($"MCP 伺服器「{server}」：{result.Error}", isError: true, view: view);
                return;
            }
            if (result.AuthUrl != null)
            {
                OpenLink(result.AuthUrl);
                Toast($"已在瀏覽器開啟 MCP 伺服器「{server}」的授權頁，完成後這裡會自動更新", view: view);
            }
            var deadline = DateTime.UtcNow + McpAuthLimit;
            while (true)
            {
                var status = await session.GetMcpStatusAsync(settings);
                if (disposed)
                    return;
                if (status.Servers != null)
                {
                    ApplyMcpStatus(state, status);
                    PostMcp();
                    var now = status.Servers.FirstOrDefault(s => s.Name == server)?.Status;
                    if (!AccountMcpRules.AwaitingAuth(now))
                    {
                        var reconnected = now == "connected" ? ReconnectLiveConversations(accountId, server) : 0;
                        Toast(now == "connected"
                            ? $"MCP 伺服器「{server}」已授權{(reconnected > 0 ? $"，開著的 {reconnected} 個對話已重新連線" : string.Empty)}"
                            : $"MCP 伺服器「{server}」授權後仍然連不上，請到明細看原因", isError: now != "connected", view: view);
                        return;
                    }
                }
                // 不用使用者做什麼（沒有授權網址）卻還是要授權：不再等
                if (result.AuthUrl == null || DateTime.UtcNow >= deadline)
                {
                    Toast($"MCP 伺服器「{server}」{(result.AuthUrl == null ? "沒有給授權網址" : "在 5 分鐘內沒有完成授權")}，可以再按一次授權", isError: true, view: view);
                    return;
                }
                await Task.Delay(McpAuthPollInterval);
            }
        }
        catch (Exception ex)
        {
            if (!disposed)
                Toast($"MCP 伺服器「{server}」授權失敗：{ex.Message}", isError: true, view: view);
        }
        finally
        {
            if (!disposed)
            {
                state.Authorizing = null;
                await CloseMcpInspectorAsync(accountId);
                PostMcp();
            }
        }
    }

    private async Task ReconnectMcpAsync(IAgentView view, string accountId, string server)
    {
        var state = accountMcp[accountId];
        try
        {
            var (session, settings) = McpInspector(accountId);
            var result = await session.McpActionAsync(server, AgentMcpAction.Reconnect, settings);
            var status = await session.GetMcpStatusAsync(settings);
            if (disposed)
                return;
            ApplyMcpStatus(state, status);
            var now = status.Servers?.FirstOrDefault(s => s.Name == server)?.Status;
            if (now == "connected")
            {
                var reconnected = ReconnectLiveConversations(accountId, server);
                Toast($"MCP 伺服器「{server}」連得上{(reconnected > 0 ? $"，開著的 {reconnected} 個對話已重新連線" : string.Empty)}", view: view);
            }
            else
            {
                Toast($"MCP 伺服器「{server}」還是連不上{(result.Error is { } error ? "：" + error : string.Empty)}", isError: true, view: view);
            }
        }
        catch (Exception ex)
        {
            if (!disposed)
                Toast($"MCP 伺服器「{server}」重新連線失敗：{ex.Message}", isError: true, view: view);
        }
        finally
        {
            if (!disposed)
            {
                state.Checking = false;
                await CloseMcpInspectorAsync(accountId);
                PostMcp();
            }
        }
    }

    /// <summary>
    /// 這個帳戶開著程序的對話都送重新連線（Claude 是 mcp_reconnect、Codex 是 config/mcpServer/reload）（授權好之後用上新的權杖、伺服器恢復之後接回來）；不開新的程序。
    /// 回傳送了幾個（結果不等：那個對話的資料夾停用了這個伺服器時 CLI 會回錯誤，不理會）
    /// </summary>
    private int ReconnectLiveConversations(string accountId, string server)
    {
        int count = 0;
        foreach (var c in conversations.Values.Where(c => !c.IsReplica && c.Meta.AccountId == accountId && c.Meta.Provider is AgentProvider.Claude or AgentProvider.Codex).ToList())
        {
            if (c.Session is not { HasLiveProcess: true } session)
                continue;
            try
            {
                _ = session.McpActionAsync(server, AgentMcpAction.Reconnect, BuildSessionSettings(c, historyRecap: null));
                count++;
            }
            catch (Exception ex)
            {
                Log.Warn($"對話 {c.Meta.Id} 重新連線 MCP 伺服器「{server}」失敗：{ex.Message}");
            }
        }
        return count;
    }

    /// <summary>這個帳戶檢查用的會話（沒有就建一個：新的 session id、中立的資料夾），與它的設定</summary>
    private (IAgentSession Session, AgentSessionSettings Settings) McpInspector(string accountId)
    {
        var account = accounts.Find(accountId) ?? throw new InvalidOperationException("這個帳戶已被移除");
        if (!mcpInspectors.TryGetValue(accountId, out var inspector))
            mcpInspectors[accountId] = inspector = (sessionFactory(account.Provider), Guid.NewGuid().ToString());
        return (inspector.Session, BuildMcpInspectorSettings(account, inspector.SessionId));
    }

    /// <summary>
    /// 檢查用的程序的設定：帳戶的環境與共用設定（隔離帳戶共用來源的 MCP 照樣以 --mcp-config 帶）同對話，工作資料夾是 cache\mcp-check
    /// （裡面沒有 .mcp.json，只看帳戶層的）；不帶模型、說明，權限是每步詢問（不會用到工具）
    /// </summary>
    private AgentSessionSettings BuildMcpInspectorSettings(AgentAccount account, string sessionId)
    {
        var dir = Path.Combine(AppPaths.CacheDirectory, "mcp-check");
        Directory.CreateDirectory(dir);
        var sharedCustomizations = accounts.Shared;
        var shared = sharedCustomizations != null ? SharedCustomizations.Snapshot(account) : null;
        bool crossCli = sharedCustomizations != null && settings.ShareCrossCliCustomizations && !account.SafeMode;
        return new AgentSessionSettings(
            sessionId,
            false,
            cli.GetLauncher(account.Provider),
            $"{account.Id}|{AccountRules.ResolveConfigDirectory(account)}|mcp-check",
            env => AccountRules.ApplyEnvironment(account, env),
            account.Provider == AgentProvider.Codex && !account.IsSystemDefault ? account.ConfigDirectory : null,
            null,
            null,
            AgentPermissionMode.Ask,
            dir,
            dir,
            string.Empty,
            UseLocalCustomizations: true,
            PrepareShared: shared == null && !crossCli ? null : cwd => sharedCustomizations!.Prepare(shared, cwd, crossCli));
    }

    private async Task CloseMcpInspectorAsync(string accountId)
    {
        if (!mcpInspectors.Remove(accountId, out var inspector))
            return;
        try
        {
            await inspector.Session.CloseAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"關閉帳戶 {accountId} 檢查 MCP 的程序失敗：{ex.Message}");
        }
        inspector.Session.Dispose();
    }

    /// <summary>中樞關閉：關掉檢查用的程序（Dispose 會結束程序）</summary>
    private void StopAccountMcp()
    {
        foreach (var inspector in mcpInspectors.Values)
            inspector.Session.Dispose();
        mcpInspectors.Clear();
    }

    private void PostMcp()
    {
        var targets = views.Values.Where(v => v.Id != SyncViewId && !v.IsPopout).ToList();
        if (targets.Count > 0)
            TryPost(targets, () => new JObject { ["type"] = "mcp", ["mcp"] = RenderAccountMcp() });
    }

    /// <summary>mcp 訊息的內容：{accounts: {帳戶 id: AccountMcpRules.RenderAccount}}，只有這台的 Claude 帳戶</summary>
    internal JObject RenderAccountMcp()
    {
        var view = new JObject();
        foreach (var account in McpAccounts())
        {
            accountMcp.TryGetValue(account.Id, out var state);
            view[account.Id] = AccountMcpRules.RenderAccount(state, account.SafeMode, at => ToUnixMs(at) ?? 0);
        }
        return new JObject { ["accounts"] = view };
    }
}
