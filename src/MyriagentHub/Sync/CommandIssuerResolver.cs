using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Auth;

namespace MyriagentHub.Sync;

/// <summary>遠端指令信封的發起者資訊：<c>issuer</c>（種類與標籤）與 <c>auth</c>（最後一次輸入密碼的時間、擁有者是否仍是預設密碼）</summary>
internal sealed record CommandIssuerFacts(CommandIssuer Issuer, CommandAuth Auth);

/// <summary>
/// 由發起者組出信封的 <c>issuer</c> 與 <c>auth</c>。裝置發起的（桌面遠端操作，sync-protocol 14.7）：<c>issuer</c>＝<c>{kind:"device", label: 電腦名稱}</c>
/// （<see cref="PeerRules.DeviceLabel"/>），<c>auth.uvAt</c> 是 null（裝置沒有輸入密碼這回事；兩個時間窗目前都是 null）。
/// 瀏覽器的：由瀏覽器連線的工作階段組出（WP4.5 計畫 D10、D11、D25；sync-protocol 14.4）：
/// <list type="bullet">
/// <item><c>issuer</c>＝<c>{kind:"browser", label}</c>，標籤由工作階段記下的 User-Agent 經 <see cref="IssuerLabelRules"/> 認出（只供顯示）。</item>
/// <item><c>auth.uvAt</c>＝工作階段最後一次輸入密碼（登入或再驗證）的伺服器時間；<c>auth.defaultPassword</c>＝<see cref="OwnerAccountService.IsDefaultPassword"/>。</item>
/// <item>找不到工作階段（或已撤銷、從沒輸入過密碼）時 <c>uvAt</c> 是 null、標籤是 <see cref="IssuerLabelRules.Fallback"/>。</item>
/// </list>
/// 每次指令都查一次 hub.db（<see cref="SessionService.FindAsync"/> 自己建 scope、不追蹤），不快取：再驗證之後的 <c>uvAt</c> 要立刻看得到。
/// 由指令路由（步 6 的 <c>AddHubCommands</c>）登記
/// </summary>
internal sealed class CommandIssuerResolver(SessionService sessions, OwnerAccountService owner, DeviceDirectory devices, CollaboratorDirectory? collaborators = null)
{
    public Task<CommandIssuerFacts> ResolveAsync(CommandOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return origin switch
        {
            BrowserCommandOrigin { Connection.CollaboratorId: { } collaboratorId } browser => ResolveCollaboratorAsync(browser.Connection.SessionId, collaboratorId, cancellationToken),
            BrowserCommandOrigin browser => ResolveAsync(browser.Connection.SessionId, cancellationToken),
            DeviceCommandOrigin device => Task.FromResult(new CommandIssuerFacts(
                new CommandIssuer(CommandIssuerKinds.Device, PeerRules.DeviceLabel(devices.Get(device.Device)?.Name)), new CommandAuth(null, owner.IsDefaultPassword))),
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin.GetType().Name, "未知的發起者"),
        };
    }

    /// <summary>
    /// 協作人的瀏覽器（多人協作，sync-protocol 14.14）：<c>issuer</c>＝<c>{kind:"collaborator", label: 顯示名稱}</c>（電腦把它記在使用者訊息與卡片上），
    /// <c>auth</c> 同擁有者的瀏覽器（工作階段最後一次輸入密碼）。不在名冊時標籤是空字串（閘道在這之前就擋掉了，這裡只是保險）
    /// </summary>
    private async Task<CommandIssuerFacts> ResolveCollaboratorAsync(string sessionId, string collaboratorId, CancellationToken cancellationToken)
    {
        var browser = await ResolveAsync(sessionId, cancellationToken);
        var label = CollaboratorRules.CleanAuthor(collaborators?.Find(collaboratorId)?.DisplayName) ?? string.Empty;
        return browser with { Issuer = new CommandIssuer(CommandIssuerKinds.Collaborator, label) };
    }

    public async Task<CommandIssuerFacts> ResolveAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await sessions.FindAsync(sessionId, cancellationToken);
        if (session is { RevokedAt: not null })
            session = null;
        var label = IssuerLabelRules.Label(session?.UserAgent);
        long? uvAt = session is { LastUvAt: > 0 } ? session.LastUvAt : null;
        return new CommandIssuerFacts(new CommandIssuer(CommandIssuerKinds.Browser, label), new CommandAuth(uvAt, owner.IsDefaultPassword));
    }
}
